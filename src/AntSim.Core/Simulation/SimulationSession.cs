using AntSim.Core.Ants;
using AntSim.Core.Scent;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Simulation;

/// <summary>
/// 시뮬레이션 모드의 독립 실험 상태. 시작할 때 편집 월드를 복제하므로, 시뮬레이션은 원본 맵을 바꾸지 않고
/// 원본 편집도 시뮬레이션에 영향을 주지 않는다. 같은 시작 월드·설정·틱 수면 항상 같은 상태가 된다.
/// 한 틱의 순서는 냄새(확산 → 감쇠 → 발생) → 개미다.
/// </summary>
/// <remarks>스레드 안전하지 않다. <see cref="SimulationHost"/>가 계산 스레드에서만 다룬다.</remarks>
public sealed class SimulationSession
{
    private readonly World _initialWorld;
    private World _world;
    private ScentSystem _scentSystem;
    private readonly AntSystem _antSystem = new();

    /// <summary>
    /// 편집 월드 <paramref name="source"/>를 복제해 새 실험을 시작한다.
    /// <paramref name="ants"/>가 없으면 개미 없이 시작한다(<see cref="AntSettings.None"/>).
    /// </summary>
    public SimulationSession(World source, ScentSettings settings, AntSettings? ants = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfInvalid(settings);
        AntSettings = ants ?? AntSettings.None;
        ThrowIfInvalid(AntSettings);
        _initialWorld = source.Clone();
        _world = _initialWorld.Clone();
        _scentSystem = new ScentSystem(_world);
        Settings = settings;
        Scent = new ScentState(_world.Config, settings.Types.Count);
        LastSpawn = AntSpawner.SpawnInitial(Ants, _world, AntSettings);
    }

    /// <summary>저장된 실험 상태에서 복원한다(<see cref="Serialization.ExperimentSerializer"/> 전용).</summary>
    internal SimulationSession(World initialWorld, World currentWorld, ScentSettings settings, ScentState scent, long tick, double elapsedSeconds,
        AntSettings antSettings, AntPopulation ants)
    {
        ThrowIfInvalid(settings);
        ThrowIfInvalid(antSettings);
        AntSettings = antSettings;
        Ants = ants;
        LastSpawn = new AntSpawnReport(antSettings.InitialCount, ants.Count);
        _initialWorld = initialWorld;
        _world = currentWorld;
        _scentSystem = new ScentSystem(_world);
        Settings = settings;
        Scent = scent;
        Tick = tick;
        ElapsedSeconds = elapsedSeconds;
    }

    /// <summary>시뮬레이션 중인 월드(복제본).</summary>
    public World World => _world;

    /// <summary>초기화 시 돌아갈 시작 월드.</summary>
    public World InitialWorld => _initialWorld;

    public ScentSettings Settings { get; private set; }
    public ScentState Scent { get; }

    /// <summary>개미 생성·이동 설정. 바꾸려면 <see cref="Reset(AntSettings?)"/>에 넘긴다.</summary>
    public AntSettings AntSettings { get; private set; }

    /// <summary>시뮬레이션 중인 개미들(SoA).</summary>
    public AntPopulation Ants { get; } = new();

    /// <summary>마지막 생성(시작·초기화) 결과.</summary>
    public AntSpawnReport LastSpawn { get; private set; }
    public long Tick { get; private set; }

    /// <summary>시뮬레이션 시간(초). 틱 간격이 실행 중 바뀔 수 있으므로 누적한다.</summary>
    public double ElapsedSeconds { get; private set; }

    public void Step(int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            _scentSystem.Tick(Scent, Settings);
            if (Ants.Count > 0) _antSystem.Tick(Ants, _world, AntSettings, (float)Settings.TickSeconds);
            Tick++;
            ElapsedSeconds += Settings.TickSeconds;
        }
    }

    /// <summary>
    /// 시작 상태(시작 월드, 냄새 0, 틱 0, 개미 재생성)로 되돌린다. 냄새 설정은 유지한다.
    /// <paramref name="ants"/>를 주면 그 개미 설정으로 다시 생성한다.
    /// </summary>
    public void Reset(AntSettings? ants = null)
    {
        if (ants is not null)
        {
            ThrowIfInvalid(ants);
            AntSettings = ants;
        }
        _world = _initialWorld.Clone();
        _scentSystem = new ScentSystem(_world);
        Ants.Clear();
        LastSpawn = AntSpawner.SpawnInitial(Ants, _world, AntSettings);
        Scent.Clear();
        Tick = 0;
        ElapsedSeconds = 0;
    }

    /// <summary>
    /// 계수·틱 간격을 바꾼다. 다음 틱부터 적용된다. 실패하면 오류 목록을 반환하고 아무것도 바꾸지 않는다.
    /// 냄새 종류 구성은 실행 중에 바꿀 수 없다.
    /// </summary>
    public IReadOnlyList<string> TrySetSettings(ScentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0) return errors;
        if (!settings.HasSameTypesAs(Settings))
            return new[] { "냄새 종류 구성은 실행 중에 바꿀 수 없습니다. 새 시뮬레이션을 시작하세요." };
        Settings = settings;
        return Array.Empty<string>();
    }

    /// <summary>월드·냄새·틱을 결합한 상태 해시(재현성 검증용).</summary>
    public ulong ComputeStateHash()
    {
        var hash = Fnv1a64.Create();
        hash.Add((ulong)Tick);
        hash.Add(_world.ComputeContentHash());
        hash.Add(Scent.ComputeHash());
        hash.Add(Ants.ComputeHash());
        return hash.Value;
    }

    private static void ThrowIfInvalid(AntSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
    }

    private static void ThrowIfInvalid(ScentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
    }
}
