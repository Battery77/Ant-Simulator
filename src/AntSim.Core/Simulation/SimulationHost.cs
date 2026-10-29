using System.Diagnostics;
using AntSim.Core.Rendering;
using AntSim.Core.Worlds;

namespace AntSim.Core.Simulation;

/// <summary>
/// 계산 스레드가 게시한 냄새 필드 복사본(한 레이어, 선택한 냄새 종류들). 사용 후 <see cref="SimulationHost.Release"/>로 반납한다.
/// 시뮬레이션 데이터와 메모리를 공유하지 않으므로 렌더링이 실제 농도를 바꿀 수 없다.
/// 배열은 풀에서 재사용되며 종류별로 처음 필요할 때 한 번만 만든다.
/// </summary>
public sealed class ScentSnapshot
{
    private readonly float[]?[] _fields;
    private readonly float[] _maxes;
    private readonly int _tileCount;
    private int[] _types = Array.Empty<int>();

    internal ScentSnapshot(int typeCount, int tileCount)
    {
        _fields = new float[typeCount][];
        _maxes = new float[typeCount];
        _tileCount = tileCount;
    }

    /// <summary>포함된 냄새 종류 인덱스(선택 순서).</summary>
    public IReadOnlyList<int> Types => _types;

    public LayerId Layer { get; internal set; }
    public long Tick { get; internal set; }
    public double ElapsedSeconds { get; internal set; }

    public bool Contains(int type) => Array.IndexOf(_types, type) >= 0;

    /// <summary>종류 <paramref name="type"/>의 농도 배열(W×H). 포함되지 않았으면 예외.</summary>
    public float[] Field(int type) =>
        Contains(type) ? _fields[type]! : throw new ArgumentOutOfRangeException(nameof(type), type, "스냅샷에 없는 냄새 종류입니다.");

    /// <summary>종류 <paramref name="type"/>의 현재 최대 농도(자동 최대값).</summary>
    public float MaxOf(int type) =>
        Contains(type) ? _maxes[type] : throw new ArgumentOutOfRangeException(nameof(type), type, "스냅샷에 없는 냄새 종류입니다.");

    /// <summary>첫 번째 포함 종류(없으면 -1). 단일 히트맵용 편의 속성.</summary>
    public int TypeIndex => _types.Length > 0 ? _types[0] : -1;

    /// <summary>첫 번째 포함 종류의 농도 배열. 단일 히트맵용 편의 속성.</summary>
    public float[] Data => Field(TypeIndex);

    /// <summary>첫 번째 포함 종류의 최대 농도. 단일 히트맵용 편의 속성.</summary>
    public float Max => MaxOf(TypeIndex);

    internal void Fill(ReadOnlySpan<int> types, Scent.ScentState scent, LayerId layer)
    {
        if (_types.Length != types.Length) _types = new int[types.Length];
        types.CopyTo(_types);
        foreach (int type in types)
        {
            var field = scent.Field(type, layer);
            var buffer = _fields[type] ??= new float[_tileCount];
            field.CopyTo(buffer);
            _maxes[type] = HeatmapRasterizer.ComputeMax(field);
        }
        Layer = layer;
    }
}

/// <summary>
/// 시뮬레이션 계산 스레드. Godot 메인 스레드와 세션을 분리한다.
/// 메인 스레드는 명령(재생/일시정지/단일 틱/초기화/설정/임의 작업)만 보내고, 결과는 게시된 스냅샷으로만 읽는다.
/// 세션은 이 스레드에서만 접근한다. 틱 결과는 실행 시간과 무관하며, 재생 속도는 "몇 틱을 돌릴지"에만 영향을 준다.
/// </summary>
public sealed class SimulationHost : IDisposable
{
    /// <summary>재생 중 한 루프에서 따라잡을 최대 틱 수. 넘으면 밀린 시간을 버린다(계산이 실시간을 못 따라갈 때).</summary>
    public const int MaxCatchUpTicks = 64;

    /// <summary>
    /// 재생 중 스냅샷 게시 최소 간격(초). 화면 갱신에 필요한 정도(약 30Hz)로만 복사해 냄새 계산 시간을 빼앗지 않는다.
    /// 명령·선택 변경·일시정지 직후에는 간격과 무관하게 즉시 게시한다.
    /// </summary>
    public const double MinPublishIntervalSeconds = 1.0 / 30;

    private readonly SimulationSession _session;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly Queue<Action> _commands = new();
    private readonly Stack<ScentSnapshot> _freeSnapshots = new();
    private readonly AutoResetEvent _wake = new(false);
    private ScentSnapshot? _latest;
    private volatile bool _disposed;
    private volatile bool _playing;
    private double _speed = 1;
    private int[] _heatmapTypes = { 0 };
    private LayerId _heatmapLayer;
    private volatile bool _publishRequested = true;
    private bool _ticksSincePublish;
    private long _tick;
    private double _lastTickMilliseconds;
    private Exception? _failure;

    public SimulationHost(SimulationSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tick = session.Tick;
        _thread = new Thread(Run) { IsBackground = true, Name = "AntSim.Simulation" };
        _thread.Start();
    }

    public bool IsPlaying => _playing;

    /// <summary>마지막으로 완료된 틱 번호.</summary>
    public long Tick => Interlocked.Read(ref _tick);

    /// <summary>마지막 틱 1회 계산 시간(ms). 성능 표시용.</summary>
    public double LastTickMilliseconds => Volatile.Read(ref _lastTickMilliseconds);

    /// <summary>계산 스레드에서 발생한 예외(있으면 스레드는 멈춘다).</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>재생 속도 배율(시뮬레이션 시간 / 실제 시간).</summary>
    public double Speed
    {
        get => Volatile.Read(ref _speed);
        set
        {
            if (!(value > 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _speed, value);
        }
    }

    public void Play()
    {
        _playing = true;
        _wake.Set();
    }

    public void Pause()
    {
        _playing = false;
        _publishRequested = true; // 멈춘 시점의 상태를 바로 보여 준다
        _wake.Set();
    }

    /// <summary>정확히 한 틱을 실행한다(재생 중이면 먼저 일시정지한다).</summary>
    public Task StepAsync()
    {
        _playing = false;
        return RunExclusiveAsync(session =>
        {
            RunTick(session);
            return true;
        });
    }

    /// <summary>시작 상태로 초기화하고 일시정지한다.</summary>
    public Task ResetAsync()
    {
        _playing = false;
        return RunExclusiveAsync(session =>
        {
            session.Reset();
            return true;
        });
    }

    /// <summary>설정 변경을 요청한다. 다음 틱부터 적용되며, 오류가 있으면 오류 목록을 돌려준다.</summary>
    public Task<IReadOnlyList<string>> SetSettingsAsync(Scent.ScentSettings settings) =>
        RunExclusiveAsync(session => session.TrySetSettings(settings));

    /// <summary>틱 사이에 계산 스레드에서 세션에 대한 작업을 실행한다(저장 등).</summary>
    public Task<T> RunExclusiveAsync<T>(Func<SimulationSession, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _commands.Enqueue(() =>
            {
                T result;
                try
                {
                    result = work(_session);
                }
                catch (Exception e)
                {
                    completion.SetException(e);
                    _publishRequested = true;
                    return;
                }
                // 작업이 세션을 진행시켰을 수 있으므로 공개 틱을 먼저 갱신한 뒤 완료를 알린다.
                Interlocked.Exchange(ref _tick, _session.Tick);
                _publishRequested = true;
                completion.SetResult(result);
            });
        }
        _wake.Set();
        return completion.Task;
    }

    /// <summary>히트맵으로 게시할 냄새 종류 하나와 레이어를 고른다(단일 히트맵).</summary>
    public void SelectHeatmap(int typeIndex, LayerId layer) => SelectHeatmap(new[] { typeIndex }, layer);

    /// <summary>
    /// 게시할 냄새 종류들과 레이어를 고른다(복합 히트맵은 여러 종류, 히트맵을 끄면 빈 목록 → 필드 복사 없음).
    /// 표시 선택만 바꾸며 시뮬레이션 상태에는 영향을 주지 않는다. 범위를 벗어난 종류는 무시한다.
    /// </summary>
    public void SelectHeatmap(IReadOnlyList<int> typeIndices, LayerId layer)
    {
        ArgumentNullException.ThrowIfNull(typeIndices);
        var types = typeIndices.Distinct().ToArray();
        lock (_gate)
        {
            _heatmapTypes = types;
            _heatmapLayer = layer;
            _publishRequested = true;
        }
        _wake.Set();
    }

    /// <summary>새로 게시된 스냅샷이 있으면 가져온다. 다 쓴 뒤 <see cref="Release"/>로 반납해야 한다.</summary>
    public bool TryTakeSnapshot(out ScentSnapshot snapshot)
    {
        lock (_gate)
        {
            snapshot = _latest!;
            _latest = null;
            return snapshot is not null;
        }
    }

    public void Release(ScentSnapshot snapshot)
    {
        lock (_gate) _freeSnapshots.Push(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double lastSeconds = 0, owedSimulationSeconds = 0, lastPublish = double.NegativeInfinity;
        try
        {
            while (!_disposed)
            {
                DrainCommands();

                double now = clock.Elapsed.TotalSeconds;
                double realDelta = now - lastSeconds;
                lastSeconds = now;
                int waitMs = 50;

                if (_playing)
                {
                    double dt = _session.Settings.TickSeconds;
                    owedSimulationSeconds += realDelta * Speed;
                    int ticks = 0;
                    while (owedSimulationSeconds >= dt && ticks < MaxCatchUpTicks && _playing && !_disposed)
                    {
                        RunTick(_session);
                        owedSimulationSeconds -= dt;
                        ticks++;
                        if (HasPendingCommands()) break;
                    }
                    if (ticks == MaxCatchUpTicks) owedSimulationSeconds = 0;
                    if (ticks > 0) _ticksSincePublish = true;
                    waitMs = (int)Math.Clamp((dt - owedSimulationSeconds) / Speed * 1000, 0, 50);
                }
                else
                {
                    owedSimulationSeconds = 0;
                }

                bool due = _ticksSincePublish && (!_playing || now - lastPublish >= MinPublishIntervalSeconds);
                if (_publishRequested || due)
                {
                    Publish();
                    lastPublish = now;
                }
                if (waitMs > 0 && !HasPendingCommands()) _wake.WaitOne(waitMs);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _failure, e);
            _playing = false;
        }
    }

    private void RunTick(SimulationSession session)
    {
        long start = Stopwatch.GetTimestamp();
        session.Step();
        Volatile.Write(ref _lastTickMilliseconds, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        Interlocked.Exchange(ref _tick, session.Tick);
    }

    private bool HasPendingCommands()
    {
        lock (_gate) return _commands.Count > 0;
    }

    private void DrainCommands()
    {
        while (true)
        {
            Action command;
            lock (_gate)
            {
                if (!_commands.TryDequeue(out command!)) return;
            }
            command();
        }
    }

    private void Publish()
    {
        int[] requested;
        LayerId layer;
        ScentSnapshot? snapshot;
        lock (_gate)
        {
            _publishRequested = false;
            _ticksSincePublish = false;
            requested = _heatmapTypes;
            layer = _heatmapLayer;
            _freeSnapshots.TryPop(out snapshot);
        }
        int typeCount = _session.Scent.TypeCount;
        snapshot ??= new ScentSnapshot(typeCount, _session.World.Config.TileCount);

        Span<int> types = stackalloc int[requested.Length];
        int count = 0;
        foreach (int type in requested)
            if ((uint)type < (uint)typeCount) types[count++] = type;
        snapshot.Fill(types[..count], _session.Scent, layer);
        snapshot.Tick = _session.Tick;
        snapshot.ElapsedSeconds = _session.ElapsedSeconds;

        lock (_gate)
        {
            if (_latest is not null) _freeSnapshots.Push(_latest);
            _latest = snapshot;
        }
    }
}
