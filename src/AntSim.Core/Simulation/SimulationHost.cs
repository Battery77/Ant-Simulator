using System.Diagnostics;
using AntSim.Core.Rendering;
using AntSim.Core.Worlds;

namespace AntSim.Core.Simulation;

/// <summary>계산 스레드가 게시한 히트맵용 냄새 필드 복사본. 사용 후 <see cref="SimulationHost.Release"/>로 반납한다.</summary>
public sealed class ScentSnapshot
{
    internal ScentSnapshot(int tileCount) => Data = new float[tileCount];

    public float[] Data { get; }
    public int TypeIndex { get; internal set; }
    public LayerId Layer { get; internal set; }
    public long Tick { get; internal set; }
    public double ElapsedSeconds { get; internal set; }
    public float Max { get; internal set; }
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
    private int _heatmapType;
    private LayerId _heatmapLayer;
    private volatile bool _publishRequested = true;
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
            Interlocked.Exchange(ref _tick, session.Tick);
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
                try
                {
                    completion.SetResult(work(_session));
                }
                catch (Exception e)
                {
                    completion.SetException(e);
                }
                _publishRequested = true;
            });
        }
        _wake.Set();
        return completion.Task;
    }

    /// <summary>히트맵으로 게시할 냄새 종류와 레이어를 고른다.</summary>
    public void SelectHeatmap(int typeIndex, LayerId layer)
    {
        lock (_gate)
        {
            _heatmapType = typeIndex;
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
        double lastSeconds = 0, owedSimulationSeconds = 0;
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
                    if (ticks > 0) _publishRequested = true;
                    waitMs = (int)Math.Clamp((dt - owedSimulationSeconds) / Speed * 1000, 0, 50);
                }
                else
                {
                    owedSimulationSeconds = 0;
                }

                if (_publishRequested) Publish();
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
        int type;
        LayerId layer;
        ScentSnapshot? snapshot;
        lock (_gate)
        {
            _publishRequested = false;
            type = Math.Clamp(_heatmapType, 0, _session.Scent.TypeCount - 1);
            layer = _heatmapLayer;
            _freeSnapshots.TryPop(out snapshot);
        }
        snapshot ??= new ScentSnapshot(_session.World.Config.TileCount);

        var field = _session.Scent.Field(type, layer);
        field.CopyTo(snapshot.Data);
        snapshot.TypeIndex = type;
        snapshot.Layer = layer;
        snapshot.Tick = _session.Tick;
        snapshot.ElapsedSeconds = _session.ElapsedSeconds;
        snapshot.Max = HeatmapRasterizer.ComputeMax(field);

        lock (_gate)
        {
            if (_latest is not null) _freeSnapshots.Push(_latest);
            _latest = snapshot;
        }
    }
}
