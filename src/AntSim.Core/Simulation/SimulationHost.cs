using System.Diagnostics;
using AntSim.Core.Ants;
using AntSim.Core.Rendering;
using AntSim.Core.Worlds;

namespace AntSim.Core.Simulation;

/// <summary>
/// 게시된 개미 표시 데이터(전 레이어, 복사본). 화면은 PrevX/PrevY → X/Y 사이를 보간해 틱 사이에도 부드럽게 그린다.
/// 배열은 스냅샷 풀과 함께 재사용된다.
/// </summary>
public sealed class AntFrame
{
    public int Count { get; private set; }
    public int[] Id { get; private set; } = Array.Empty<int>();
    public byte[] Layer { get; private set; } = Array.Empty<byte>();
    public float[] X { get; private set; } = Array.Empty<float>();
    public float[] Y { get; private set; } = Array.Empty<float>();
    public float[] PrevX { get; private set; } = Array.Empty<float>();
    public float[] PrevY { get; private set; } = Array.Empty<float>();
    public float[] Heading { get; private set; } = Array.Empty<float>();
    public float[] Size { get; private set; } = Array.Empty<float>();
    public float[] BaseSpeed { get; private set; } = Array.Empty<float>();
    public float[] SpeedFactor { get; private set; } = Array.Empty<float>();

    /// <summary>
    /// 화면에 그리는 위치: 이전 틱 → 현재 틱을 <paramref name="alpha"/>(0~1)로 보간한다. 화면 표시(AntView)와 클릭 선택이
    /// 같은 식을 써서, 보이는 위치와 선택 기준이 비트 단위로 같다(감사 SA05).
    /// </summary>
    public (float X, float Y) DisplayPosition(int index, float alpha) =>
        (PrevX[index] + (X[index] - PrevX[index]) * alpha, PrevY[index] + (Y[index] - PrevY[index]) * alpha);

    /// <summary>
    /// <paramref name="layer"/>의 개미 중 표시 위치(<see cref="DisplayPosition"/>)가 (x, y)에서 <paramref name="maxDistance"/> 미만으로 가장 가까운
    /// 개미의 인덱스. 없으면 -1. 거리가 같으면 앞 인덱스.
    /// </summary>
    public int FindNearestShown(LayerId layer, float alpha, float x, float y, float maxDistance)
    {
        int best = -1;
        float bestSquared = maxDistance * maxDistance;
        for (int i = 0; i < Count; i++)
        {
            if (Layer[i] != (byte)layer) continue;
            var (sx, sy) = DisplayPosition(i, alpha);
            float dx = sx - x, dy = sy - y, squared = dx * dx + dy * dy;
            if (squared < bestSquared)
            {
                bestSquared = squared;
                best = i;
            }
        }
        return best;
    }

    /// <summary>레이어별 개미 수.</summary>
    public int CountOn(LayerId layer)
    {
        int n = 0;
        for (int i = 0; i < Count; i++)
            if (Layer[i] == (byte)layer) n++;
        return n;
    }

    /// <summary>테스트용: 지정한 값으로 채운다.</summary>
    internal void SetForTest(int[] id, byte[] layer, float[] prevX, float[] prevY, float[] x, float[] y)
    {
        int n = id.Length;
        Id = id; Layer = layer; PrevX = prevX; PrevY = prevY; X = x; Y = y;
        Heading = new float[n]; Size = new float[n]; BaseSpeed = new float[n]; SpeedFactor = new float[n];
        Count = n;
    }

    internal void Fill(AntPopulation ants)
    {
        int n = ants.Count;
        if (Id.Length < n)
        {
            int capacity = Math.Max(n, Id.Length * 2);
            Id = new int[capacity]; Layer = new byte[capacity];
            X = new float[capacity]; Y = new float[capacity]; PrevX = new float[capacity]; PrevY = new float[capacity];
            Heading = new float[capacity]; Size = new float[capacity]; BaseSpeed = new float[capacity]; SpeedFactor = new float[capacity];
        }
        ants.Ids.CopyTo(Id); ants.Layer.CopyTo(Layer);
        ants.X.CopyTo(X); ants.Y.CopyTo(Y); ants.PrevX.CopyTo(PrevX); ants.PrevY.CopyTo(PrevY);
        ants.Heading.CopyTo(Heading); ants.Size.CopyTo(Size); ants.BaseSpeed.CopyTo(BaseSpeed); ants.SpeedFactor.CopyTo(SpeedFactor);
        Count = n;
    }
}

/// <summary>
/// 선택한 개미 한 마리의 단계 B1 감각 상세(계산 스레드가 게시, 스냅샷과 함께 재사용). 10만 마리 감각 배열 전체를 복사하지 않고
/// 이 개미 하나만 복사한다. <see cref="ResetCount"/>·<see cref="Id"/>는 요청한 선택이며, 화면은 자기 선택과 같을 때만 표시한다.
/// </summary>
public sealed class AntSenseDetail
{
    /// <summary>값이 들어 있는가(선택이 있고, 요청 세대가 게시 세대와 같고, 그 개미가 있다).</summary>
    public bool HasValue { get; private set; }

    /// <summary>선택 요청이 있었는가(값이 없어도 "개미 없음" 표시에 쓴다).</summary>
    public bool Requested { get; private set; }

    public long ResetCount { get; private set; } = -1;
    public int Id { get; private set; } = -1;
    public LayerId Layer { get; private set; }
    public float X { get; private set; }
    public float Y { get; private set; }
    public float Heading { get; private set; }

    /// <summary>다음 감지까지 남은 시간(초, (0, P]).</summary>
    public float SenseTimer { get; private set; }

    /// <summary>마지막 감지 틱(<see cref="AntPopulation.NotSensed"/>면 감지 전).</summary>
    public long SenseTick { get; private set; }
    public float SenseHeading { get; private set; }

    /// <summary>마지막 감지의 센서별 차폐 비트(bit0 왼쪽 / 1 정면 / 2 오른쪽).</summary>
    public byte SenseFlags { get; private set; }

    public bool HasSensed => HasValue && SenseTick != AntPopulation.NotSensed;

    /// <summary>감지 키(감지 값 배열 순서).</summary>
    public IReadOnlyList<string> Keys { get; private set; } = Array.Empty<string>();

    /// <summary>[k·3 + d] 농도(d = 0 왼쪽 / 1 정면 / 2 오른쪽). 길이는 최대 K·3.</summary>
    public float[] Values { get; } = new float[AntSettings.MaxSensedTypes * AntPopulation.SensorCount];

    public float Period { get; private set; }
    public float Distance { get; private set; }
    public float AngleDegrees { get; private set; }

    /// <summary>현재 위치·방향 기준 센서 세 점(x0, y0, x1, y1, x2, y2)과 각 센서의 현재 차폐 비트(표시용, 상태와 무관하게 다시 계산).</summary>
    public float[] SensorPoints { get; } = new float[2 * AntPopulation.SensorCount];
    public byte SensorPointsBlocked { get; private set; }

    public float Value(int type, int sensor) => Values[type * AntPopulation.SensorCount + sensor];

    internal void Clear()
    {
        HasValue = Requested = false;
        ResetCount = -1;
        Id = -1;
    }

    internal void Fill(SimulationSession session, int id, long resetCount)
    {
        Requested = id >= 0;
        Id = id;
        ResetCount = resetCount;
        HasValue = false;
        if (id < 0 || resetCount != session.ResetCount) return; // 다른 세대의 선택 요청: 재사용된 같은 id를 보여 주지 않는다
        var ants = session.Ants;
        int i = ants.IndexOfId(id);
        if (i < 0) return;
        var info = ants.Get(i);
        var sense = ants.GetSense(i, Values);
        var config = session.Sensing;
        Layer = info.Layer;
        X = info.X;
        Y = info.Y;
        Heading = info.Heading;
        SenseTimer = sense.SenseTimer;
        SenseTick = sense.SenseTick;
        SenseHeading = sense.SenseHeading;
        SenseFlags = sense.Flags;
        Keys = config.Keys;
        Period = config.Period;
        Distance = config.Distance;
        AngleDegrees = config.AngleDegrees;
        var cfg = session.World.Config;
        AntSensing.Preview(info.X, info.Y, info.Heading, (int)info.Layer, session.SenseOpenMasks(), cfg.Width, cfg.Height, config,
            SensorPoints, out byte blocked);
        SensorPointsBlocked = blocked;
        HasValue = true;
    }
}

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

    /// <summary>게시 시점의 초기화 세대(<see cref="SimulationSession.ResetCount"/>). 냄새 필드와 <see cref="Ants"/>에 함께 적용된다.</summary>
    public long ResetCount { get; internal set; }

    /// <summary>같은 틱·같은 초기화 세대의 개미 표시 데이터(히트맵 선택과 무관하게 항상 포함).</summary>
    public AntFrame Ants { get; } = new();

    /// <summary>선택한 개미의 감각 상세(같은 틱). 선택 요청이 없거나 세대가 다르면 <see cref="AntSenseDetail.HasValue"/> = false.</summary>
    public AntSenseDetail SelectedAnt { get; } = new();

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
    private readonly Queue<PendingCommand> _commands = new();
    private readonly Stack<ScentSnapshot> _freeSnapshots = new();
    private readonly AutoResetEvent _wake = new(false);
    private ScentSnapshot? _latest;
    private volatile bool _disposed;
    private HostState _state = HostState.Running; // _gate로 보호
    private volatile bool _playing;
    private double _speed = 1;
    private int[] _heatmapTypes = { 0 };
    private LayerId _heatmapLayer;
    private volatile bool _publishRequested = true;
    private int _selectedAntId = -1;          // _gate로 보호
    private long _selectedAntResetCount = -1; // _gate로 보호
    private bool _ticksSincePublish;
    private long _tick;
    private double _lastTickMilliseconds;
    private Exception? _failure;

    /// <summary>
    /// 테스트 전용 동기화 지점. 계산 스레드가 "tick"(재생 틱 직후), "publish"(스냅샷 게시 시작),
    /// "before-dequeue"(다음 명령을 꺼내려고 lock을 잡기 직전)에서 호출한다.
    /// 경쟁 조건을 시간 지연 없이 재현하기 위해 쓴다. 운영 코드에서는 설정하지 않는다.
    /// </summary>
    internal volatile Action<string>? TestHook;

    /// <summary>테스트 전용: Dispose가 요청되었는지.</summary>
    internal bool DisposeRequested => _disposed;

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

    /// <summary>
    /// 시작 상태(시작 월드, 냄새 0, 틱 0, 개미 재생성)로 초기화하고 일시정지한다. <paramref name="ants"/>를 주면 그 설정으로 개미를 다시 만든다.
    /// 한 명령 안에서 월드·냄새·개미·초기화 세대를 함께 바꾸며, 결과에는 같은 틱 경계에서 복제한 표시용 월드,
    /// 초기화 세대, 개미 생성 결과가 들어 있다(화면이 계산 상태와 같은 상태를 그리도록).
    /// 개미 설정이 잘못되었으면 아무것도 바꾸지 않고 Task가 <see cref="ArgumentException"/>으로 실패한다.
    /// </summary>
    public Task<SimulationResetResult> ResetAsync(AntSettings? ants = null)
    {
        _playing = false;
        return RunExclusiveAsync(session =>
        {
            session.Reset(ants);
            lock (_gate)
            {
                // 이전 세대의 선택은 초기화 뒤 의미가 없다(같은 id가 다른 개미에 재사용된다).
                if (_selectedAntResetCount != session.ResetCount) _selectedAntId = -1;
            }
            return new SimulationResetResult(session.World.Clone(), session.ResetCount, session.Tick, session.LastSpawn);
        });
    }

    /// <summary>설정 변경을 요청한다. 다음 틱부터 적용되며, 오류가 있으면 오류 목록을 돌려준다.</summary>
    public Task<IReadOnlyList<string>> SetSettingsAsync(Scent.ScentSettings settings) =>
        RunExclusiveAsync(session => session.TrySetSettings(settings));

    /// <summary>
    /// 틱 사이에 계산 스레드에서 세션에 대한 작업을 실행한다(저장 등).
    /// <para>명령 계약: 접수된 명령의 Task는 반드시 성공·실패·취소 중 하나로 끝난다.</para>
    /// <list type="bullet">
    /// <item>종료(Dispose)되었거나 계산 스레드가 실패한 호스트는 새 명령을 즉시 거부한다
    /// (<see cref="ObjectDisposedException"/> / <see cref="InvalidOperationException"/>).</item>
    /// <item>Dispose 시 실행 중인 명령은 끝까지 실행하고, 아직 시작하지 않은 명령은 취소된다.</item>
    /// <item>계산 스레드가 실패하면 대기 중인 명령은 그 예외로 실패한다.</item>
    /// <item><paramref name="work"/>가 예외를 던져도 이미 바뀐 세션 상태는 되돌리지 않는다(롤백 없음).
    /// 공개 <see cref="Tick"/>은 성공·실패와 무관하게 Task 완료 전에 세션과 맞춘다.</item>
    /// </list>
    /// </summary>
    public Task<T> RunExclusiveAsync<T>(Func<SimulationSession, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var command = new PendingCommand<T>(work);
        lock (_gate)
        {
            if (_state == HostState.Faulted)
                throw new InvalidOperationException("계산 스레드가 실패해 명령을 받을 수 없습니다.", _failure);
            ObjectDisposedException.ThrowIf(_state != HostState.Running, this);
            _commands.Enqueue(command);
        }
        _wake.Set();
        return command.Task;
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

    /// <summary>
    /// 감각 상세를 게시할 개미를 고른다(표시 선택, 시뮬레이션 상태에는 영향 없음). <paramref name="resetCount"/>는 화면이 그 개미를 고른
    /// 스냅샷의 초기화 세대이며, 계산 스레드의 세대와 다르면(초기화 뒤 늦게 도착한 요청) 상세를 채우지 않는다.
    /// </summary>
    public void SelectAnt(int id, long resetCount)
    {
        lock (_gate)
        {
            _selectedAntId = id;
            _selectedAntResetCount = resetCount;
            _publishRequested = true;
        }
        _wake.Set();
    }

    /// <summary>감각 상세 선택을 지운다.</summary>
    public void ClearAntSelection() => SelectAnt(-1, -1);

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

    /// <summary>
    /// 계산 스레드를 멈춘다. 실행 중인 명령·틱은 끝까지 마치고, 아직 시작하지 않은 명령은 취소한다.
    /// 여러 번 호출해도 안전하다. (대기 이벤트는 다른 스레드의 Play/Pause 호출과 경합하지 않도록 해제하지 않는다.)
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_state == HostState.Running) _state = HostState.Stopping;
        }
        _wake.Set();
        if (Thread.CurrentThread != _thread) _thread.Join();
        // 계산 스레드가 시작 전에 끝났거나 이미 실패한 경우에도 남은 명령이 없도록 한 번 더 정리한다.
        TerminatePending(HostState.Stopped, failure: null);
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
                        TestHook?.Invoke("tick");
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
            TerminatePending(HostState.Faulted, e);
            return;
        }
        TerminatePending(HostState.Stopped, failure: null);
    }

    /// <summary>
    /// 상태를 바꾸고(같은 lock 안에서 새 명령 접수를 막는다) 아직 시작하지 않은 명령을 모두 종결한다.
    /// 종료면 취소, 실패면 그 예외로 실패. 완료 처리는 lock 밖에서 하며 TrySet 계열이라 중복 완료가 없다.
    /// </summary>
    private void TerminatePending(HostState state, Exception? failure)
    {
        PendingCommand[] pending;
        lock (_gate)
        {
            if (_state != HostState.Faulted) _state = state;
            pending = _commands.ToArray();
            _commands.Clear();
        }
        var reason = failure is null ? null : new InvalidOperationException("계산 스레드가 실패해 명령이 실행되지 않았습니다.", failure);
        foreach (var command in pending) command.Abandon(reason);
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

    /// <summary>
    /// 대기 명령을 차례로 실행한다. 상태 확인과 큐에서 꺼내기를 같은 lock 안에서 한 번에 결정한다:
    /// 꺼낸 명령은 실행권을 얻은 것이고(Dispose와 무관하게 끝까지 실행), Dispose가 먼저 Stopping으로 바꾸면 더 꺼내지 않는다
    /// (남은 명령은 <see cref="TerminatePending"/>이 취소). 콜백 실행과 Task 완료는 lock 밖에서 한다.
    /// </summary>
    private void DrainCommands()
    {
        while (true)
        {
            TestHook?.Invoke("before-dequeue");
            PendingCommand command;
            lock (_gate)
            {
                if (_state != HostState.Running || !_commands.TryDequeue(out command!)) return;
            }
            command.Execute(this);
        }
    }

    /// <summary>명령 실행 후(성공·실패 무관) 공개 상태를 세션과 맞춘다. Task 완료 전에 호출된다.</summary>
    private void AfterCommand()
    {
        Interlocked.Exchange(ref _tick, _session.Tick);
        _publishRequested = true;
    }

    private enum HostState
    {
        Running,
        Stopping,
        Stopped,
        Faulted,
    }

    private abstract class PendingCommand
    {
        public abstract void Execute(SimulationHost host);

        /// <summary>실행하지 않고 종결한다. reason이 없으면 취소, 있으면 실패.</summary>
        public abstract void Abandon(Exception? reason);
    }

    private sealed class PendingCommand<T>(Func<SimulationSession, T> work) : PendingCommand
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public override void Execute(SimulationHost host)
        {
            T result = default!;
            Exception? error = null;
            try
            {
                result = work(host._session);
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                host.AfterCommand(); // 실패해도 이미 진행된 세션 상태를 공개 Tick에 반영(F07)
            }
            if (error is null) _completion.TrySetResult(result);
            else _completion.TrySetException(error);
        }

        public override void Abandon(Exception? reason)
        {
            if (reason is null) _completion.TrySetCanceled();
            else _completion.TrySetException(reason);
        }
    }

    private void Publish()
    {
        TestHook?.Invoke("publish");
        int[] requested;
        LayerId layer;
        ScentSnapshot? snapshot;
        int selectedId;
        long selectedResetCount;
        lock (_gate)
        {
            selectedId = _selectedAntId;
            selectedResetCount = _selectedAntResetCount;
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
        snapshot.Ants.Fill(_session.Ants);
        if (selectedId >= 0) snapshot.SelectedAnt.Fill(_session, selectedId, selectedResetCount);
        else snapshot.SelectedAnt.Clear();
        snapshot.Tick = _session.Tick;
        snapshot.ElapsedSeconds = _session.ElapsedSeconds;
        snapshot.ResetCount = _session.ResetCount;

        lock (_gate)
        {
            if (_latest is not null) _freeSnapshots.Push(_latest);
            _latest = snapshot;
        }
    }
}

/// <summary>
/// 초기화 결과. <see cref="World"/>는 초기화 직후 계산 월드를 같은 틱 경계에서 복제한 것으로, 호출자(화면)가 소유한다.
/// <see cref="ResetCount"/>보다 작은 세대의 스냅샷(냄새·<see cref="AntFrame"/>)은 초기화 이전 상태이므로 화면에서 버려야 한다.
/// <see cref="Ants"/>는 이 초기화에서 개미를 생성한 결과다(요청 수, 실제 생성 수).
/// </summary>
public sealed record SimulationResetResult(Worlds.World World, long ResetCount, long Tick, AntSpawnReport Ants);
