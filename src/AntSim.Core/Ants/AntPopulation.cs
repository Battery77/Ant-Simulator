using System.Runtime.InteropServices;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Ants;

/// <summary>개미 계급. 단계 A~D는 일개미만 사용한다.</summary>
public enum AntCaste : byte
{
    Worker = 0,
}

/// <summary>
/// 이동 능력. 현재는 걷기만 있다. 이후 수영 등 능력을 플래그로 추가하면
/// <see cref="AntMovement.IsPassable"/>의 지형 판정만 확장하면 된다.
/// </summary>
[Flags]
public enum AntMobility : byte
{
    Walk = 1,
}

/// <summary>개미 한 마리의 읽기 전용 정보(UI·테스트용 값 복사본).</summary>
public readonly record struct AntInfo(
    int Id, AntCaste Caste, LayerId Layer, float X, float Y, float Heading, float TargetHeading,
    float BaseSpeed, float SpeedFactor, float Size, AntMobility Mobility)
{
    public int TileX => (int)MathF.Floor(X);
    public int TileY => (int)MathF.Floor(Y);
    public float CurrentSpeed => BaseSpeed * SpeedFactor;
}

/// <summary>
/// 개미 한 마리의 감각 상태(값 복사). <see cref="SenseTick"/> = <see cref="AntPopulation.NotSensed"/>이면 아직 감지 전이다.
/// <see cref="Flags"/>의 bit d(0 왼쪽 / 1 정면 / 2 오른쪽)가 1이면 그 센서는 지형·맵 경계로 차폐되었다(값 0).
/// </summary>
public readonly record struct AntSenseInfo(float SenseTimer, long SenseTick, float SenseHeading, byte Flags, int SensedTypeCount)
{
    public bool HasSensed => SenseTick != AntPopulation.NotSensed;
    public bool IsBlocked(int sensor) => (Flags & (1 << sensor)) != 0;
}

/// <summary>
/// 개미 개체 데이터(SoA: 속성마다 연속 배열). 개미를 Godot 노드나 개별 객체로 만들지 않는다.
/// 인덱스 0..Count-1이 개미이며, 순회 순서는 항상 인덱스 순서(결정적)다.
/// 단계 B~D의 감각·기억·목표 데이터는 같은 방식으로 배열을 추가한다.
/// </summary>
public sealed class AntPopulation
{
    private int[] _id = Array.Empty<int>();
    private byte[] _caste = Array.Empty<byte>();
    private byte[] _layer = Array.Empty<byte>();
    private byte[] _mobility = Array.Empty<byte>();
    private float[] _x = Array.Empty<float>(), _y = Array.Empty<float>();
    private float[] _prevX = Array.Empty<float>(), _prevY = Array.Empty<float>();
    private float[] _heading = Array.Empty<float>(), _targetHeading = Array.Empty<float>();
    private float[] _baseSpeed = Array.Empty<float>(), _speedFactor = Array.Empty<float>(), _size = Array.Empty<float>();
    private float[] _turnTimer = Array.Empty<float>(), _turnIntervalScale = Array.Empty<float>();
    private int[] _lastTile = Array.Empty<int>();
    private ulong[] _rng = Array.Empty<ulong>();

    // 단계 B1 감각(docs/STAGE_B_DESIGN.md C.2). 이동 계산은 읽지도 쓰지도 않는다.
    private float[] _senseTimer = Array.Empty<float>();
    private long[] _senseTick = Array.Empty<long>();
    private float[] _senseHeading = Array.Empty<float>();
    private byte[] _senseFlags = Array.Empty<byte>();
    private float[] _senseValues = Array.Empty<float>(); // [i·K·3 + k·3 + d], d = 0 왼쪽 / 1 정면 / 2 오른쪽

    // 단계 B2 경로 적분 필드. B1에서는 초기값(무효, 0, 시드 상태)만 유지하고 저장한다.
    private float[] _homeX = Array.Empty<float>(), _homeY = Array.Empty<float>();
    private byte[] _homeFlags = Array.Empty<byte>();
    private ulong[] _navRng = Array.Empty<ulong>();
    private float[] _debugExitX = Array.Empty<float>(), _debugExitY = Array.Empty<float>();

    /// <summary>센서 수(왼쪽·정면·오른쪽).</summary>
    public const int SensorCount = 3;

    /// <summary>감지 틱 값: 아직 한 번도 감지하지 않음.</summary>
    public const long NotSensed = -1;

    /// <summary>감지하는 냄새 종류 수 K(0~8). 세션 생성·초기화·불러오기 때만 <see cref="ConfigureSensing"/>로 바뀐다.</summary>
    public int SensedTypeCount { get; private set; }

    public int Count { get; private set; }

    /// <summary>다음에 생성될 개미의 ID(생성 순서대로 증가, 재사용하지 않음).</summary>
    public int NextId { get; private set; }

    public ReadOnlySpan<int> Ids => _id.AsSpan(0, Count);
    public ReadOnlySpan<float> X => _x.AsSpan(0, Count);
    public ReadOnlySpan<float> Y => _y.AsSpan(0, Count);
    public ReadOnlySpan<float> PrevX => _prevX.AsSpan(0, Count);
    public ReadOnlySpan<float> PrevY => _prevY.AsSpan(0, Count);
    public ReadOnlySpan<float> Heading => _heading.AsSpan(0, Count);
    public ReadOnlySpan<float> BaseSpeed => _baseSpeed.AsSpan(0, Count);
    public ReadOnlySpan<float> SpeedFactor => _speedFactor.AsSpan(0, Count);
    public ReadOnlySpan<float> Size => _size.AsSpan(0, Count);
    public ReadOnlySpan<byte> Layer => _layer.AsSpan(0, Count);

    public AntInfo Get(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new AntInfo(_id[index], (AntCaste)_caste[index], (LayerId)_layer[index], _x[index], _y[index],
            _heading[index], _targetHeading[index], _baseSpeed[index], _speedFactor[index], _size[index], (AntMobility)_mobility[index]);
    }

    public int IndexOfId(int id) => Array.IndexOf(_id, id, 0, Count);

    /// <summary>개미 한 마리의 감각 상태(UI·테스트용 복사). 값은 [k·3 + d] 순서로 <paramref name="values"/>에 쓴다(길이 ≥ K·3).</summary>
    public AntSenseInfo GetSense(int index, Span<float> values)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        int stride = SensedTypeCount * SensorCount;
        _senseValues.AsSpan(index * stride, stride).CopyTo(values);
        return new AntSenseInfo(_senseTimer[index], _senseTick[index], _senseHeading[index], _senseFlags[index], SensedTypeCount);
    }

    /// <summary>
    /// 감지 종류 수를 정한다. 개미가 없을 때만 바꿀 수 있다(초기화·불러오기 직전). 감지 값 배열을 새 크기로 다시 만든다.
    /// </summary>
    internal void ConfigureSensing(int sensedTypeCount)
    {
        if ((uint)sensedTypeCount > AntSettings.MaxSensedTypes) throw new ArgumentOutOfRangeException(nameof(sensedTypeCount));
        if (Count != 0) throw new InvalidOperationException("개미가 있는 동안에는 감지 종류 수를 바꿀 수 없습니다.");
        if (sensedTypeCount == SensedTypeCount) return;
        SensedTypeCount = sensedTypeCount;
        _senseValues = new float[_id.Length * sensedTypeCount * SensorCount];
    }

    /// <summary>
    /// 불러오기 전용: 개체 수를 정한 뒤(<see cref="SetCount"/>) 감지 종류 수를 정하고 감지 값 배열을 0으로 다시 만든다.
    /// 단계 B 배열은 호출자가 모두 채워야 한다.
    /// </summary>
    internal void ConfigureSensingForLoad(int sensedTypeCount)
    {
        if ((uint)sensedTypeCount > AntSettings.MaxSensedTypes) throw new ArgumentOutOfRangeException(nameof(sensedTypeCount));
        SensedTypeCount = sensedTypeCount;
        _senseValues = new float[_id.Length * sensedTypeCount * SensorCount];
    }

    /// <summary>
    /// 단계 B 필드를 생성 때와 같은 결정적 초기값으로 채운다(v1/v2 불러오기): 타이머 = id 위상, 감지 전, 값 0, 귀환 기억 무효, Nav 난수 = 시드 상태.
    /// </summary>
    internal void InitializeStageB(AntSettings settings)
    {
        int stride = SensedTypeCount * SensorCount;
        for (int i = 0; i < Count; i++)
        {
            _senseTimer[i] = AntSensing.InitialPhase(_id[i], settings.SensePeriodSeconds);
            _senseTick[i] = NotSensed;
            _senseHeading[i] = 0;
            _senseFlags[i] = 0;
            _homeX[i] = _homeY[i] = 0;
            _homeFlags[i] = 0;
            _navRng[i] = AntRandom.CreateState(settings.Seed, AntSpawner.NavStream(_id[i]));
            _debugExitX[i] = _debugExitY[i] = 0;
        }
        Array.Clear(_senseValues, 0, Count * stride);
    }

    public void Clear()
    {
        Count = 0;
        NextId = 0;
    }

    /// <summary>모든 개체 상태의 비트 단위 해시(재현성 검증용).</summary>
    public ulong ComputeHash()
    {
        var hash = Fnv1a64.Create();
        hash.Add(Count);
        hash.Add(NextId);
        Add(ref hash, _id); Add(ref hash, _caste); Add(ref hash, _layer); Add(ref hash, _mobility);
        Add(ref hash, _x); Add(ref hash, _y); Add(ref hash, _prevX); Add(ref hash, _prevY);
        Add(ref hash, _heading); Add(ref hash, _targetHeading); Add(ref hash, _baseSpeed); Add(ref hash, _speedFactor);
        Add(ref hash, _size); Add(ref hash, _turnTimer); Add(ref hash, _turnIntervalScale); Add(ref hash, _lastTile);
        Add(ref hash, _rng);
        return hash.Value;

        void Add<T>(ref Fnv1a64 h, T[] array) where T : unmanaged => h.Add(MemoryMarshal.AsBytes(array.AsSpan(0, Count)));
    }

    /// <summary>단계 B 필드(감각·경로 적분)의 비트 단위 해시(재현성 검증용). Stage A 필드는 <see cref="ComputeHash"/>.</summary>
    public ulong ComputeStageBHash()
    {
        var hash = Fnv1a64.Create();
        hash.Add(Count);
        hash.Add(SensedTypeCount);
        AddN(ref hash, _senseTimer, Count); AddN(ref hash, _senseTick, Count); AddN(ref hash, _senseHeading, Count); AddN(ref hash, _senseFlags, Count);
        AddN(ref hash, _senseValues, Count * SensedTypeCount * SensorCount);
        AddN(ref hash, _homeX, Count); AddN(ref hash, _homeY, Count); AddN(ref hash, _homeFlags, Count); AddN(ref hash, _navRng, Count);
        AddN(ref hash, _debugExitX, Count); AddN(ref hash, _debugExitY, Count);
        return hash.Value;

        static void AddN<T>(ref Fnv1a64 h, T[] array, int n) where T : unmanaged => h.Add(MemoryMarshal.AsBytes(array.AsSpan(0, n)));
    }

    internal int Add(int id, AntCaste caste, AntMobility mobility, LayerId layer, float x, float y, float heading,
        float baseSpeed, float speedFactor, float size, float turnTimer, float turnIntervalScale, int tileIndex, ulong rng,
        float senseTimer, ulong navRng)
    {
        if (Count == _id.Length) Grow(Math.Max(16, _id.Length * 2));
        int i = Count++;
        _id[i] = id;
        _caste[i] = (byte)caste;
        _mobility[i] = (byte)mobility;
        _layer[i] = (byte)layer;
        _x[i] = _prevX[i] = x;
        _y[i] = _prevY[i] = y;
        _heading[i] = _targetHeading[i] = heading;
        _baseSpeed[i] = baseSpeed;
        _speedFactor[i] = speedFactor;
        _size[i] = size;
        _turnTimer[i] = turnTimer;
        _turnIntervalScale[i] = turnIntervalScale;
        _lastTile[i] = tileIndex;
        _rng[i] = rng;
        _senseTimer[i] = senseTimer;
        _senseTick[i] = NotSensed;
        _senseHeading[i] = 0;
        _senseFlags[i] = 0;
        int stride = SensedTypeCount * SensorCount;
        Array.Clear(_senseValues, i * stride, stride);
        _homeX[i] = _homeY[i] = 0;
        _homeFlags[i] = 0;
        _navRng[i] = navRng;
        _debugExitX[i] = _debugExitY[i] = 0;
        NextId = Math.Max(NextId, id + 1);
        return i;
    }

    internal void Grow(int capacity)
    {
        if (capacity <= _id.Length) return;
        Array.Resize(ref _id, capacity);
        Array.Resize(ref _caste, capacity);
        Array.Resize(ref _layer, capacity);
        Array.Resize(ref _mobility, capacity);
        Array.Resize(ref _x, capacity);
        Array.Resize(ref _y, capacity);
        Array.Resize(ref _prevX, capacity);
        Array.Resize(ref _prevY, capacity);
        Array.Resize(ref _heading, capacity);
        Array.Resize(ref _targetHeading, capacity);
        Array.Resize(ref _baseSpeed, capacity);
        Array.Resize(ref _speedFactor, capacity);
        Array.Resize(ref _size, capacity);
        Array.Resize(ref _turnTimer, capacity);
        Array.Resize(ref _turnIntervalScale, capacity);
        Array.Resize(ref _lastTile, capacity);
        Array.Resize(ref _rng, capacity);
        Array.Resize(ref _senseTimer, capacity);
        Array.Resize(ref _senseTick, capacity);
        Array.Resize(ref _senseHeading, capacity);
        Array.Resize(ref _senseFlags, capacity);
        Array.Resize(ref _senseValues, capacity * SensedTypeCount * SensorCount);
        Array.Resize(ref _homeX, capacity);
        Array.Resize(ref _homeY, capacity);
        Array.Resize(ref _homeFlags, capacity);
        Array.Resize(ref _navRng, capacity);
        Array.Resize(ref _debugExitX, capacity);
        Array.Resize(ref _debugExitY, capacity);
    }

    // 시스템(이동·직렬화)용 내부 배열 접근. 길이는 Count보다 클 수 있다.
    internal int[] IdArray => _id;
    internal byte[] CasteArray => _caste;
    internal byte[] LayerArray => _layer;
    internal byte[] MobilityArray => _mobility;
    internal float[] XArray => _x;
    internal float[] YArray => _y;
    internal float[] PrevXArray => _prevX;
    internal float[] PrevYArray => _prevY;
    internal float[] HeadingArray => _heading;
    internal float[] TargetHeadingArray => _targetHeading;
    internal float[] BaseSpeedArray => _baseSpeed;
    internal float[] SpeedFactorArray => _speedFactor;
    internal float[] SizeArray => _size;
    internal float[] TurnTimerArray => _turnTimer;
    internal float[] TurnIntervalScaleArray => _turnIntervalScale;
    internal int[] LastTileArray => _lastTile;
    internal ulong[] RngArray => _rng;
    internal float[] SenseTimerArray => _senseTimer;
    internal long[] SenseTickArray => _senseTick;
    internal float[] SenseHeadingArray => _senseHeading;
    internal byte[] SenseFlagsArray => _senseFlags;
    internal float[] SenseValuesArray => _senseValues;
    internal float[] HomeXArray => _homeX;
    internal float[] HomeYArray => _homeY;
    internal byte[] HomeFlagsArray => _homeFlags;
    internal ulong[] NavRngArray => _navRng;
    internal float[] DebugExitXArray => _debugExitX;
    internal float[] DebugExitYArray => _debugExitY;

    /// <summary>직렬화에서 불러온 개체 수와 다음 ID를 설정한다(배열은 미리 채워져 있어야 함).</summary>
    internal void SetCount(int count, int nextId)
    {
        Grow(count);
        Count = count;
        NextId = nextId;
    }
}
