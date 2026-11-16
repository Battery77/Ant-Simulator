using AntSim.Core.Ants;
using AntSim.Core.Scent;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Simulation;

/// <summary>
/// 시뮬레이션 모드의 독립 실험 상태. 시작할 때 편집 월드를 복제하므로, 시뮬레이션은 원본 맵을 바꾸지 않고
/// 원본 편집도 시뮬레이션에 영향을 주지 않는다. 같은 시작 월드·설정·틱 수면 항상 같은 상태가 된다.
/// 한 틱의 순서는 냄새(확산 → 감쇠 → 발생) → 개미 감각(단계 B1, 이동 전) → 개미 이동이다.
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
        ThrowIfTickUnsupported(settings.TickSeconds, AntSettings.InitialCount > 0);
        Sensing = AntSensingConfig.Create(AntSettings, settings);
        Ants.ConfigureSensing(Sensing.TypeCount);
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
        ThrowIfTickUnsupported(settings.TickSeconds, ants.Count > 0 || antSettings.InitialCount > 0);
        Sensing = AntSensingConfig.Create(antSettings, settings);
        if (ants.SensedTypeCount != Sensing.TypeCount)
            throw new ArgumentException("개미 감지 종류 수가 감지 설정과 다릅니다.", nameof(ants));
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

    /// <summary>해석된 감지 설정(키·종류 인덱스·주기·거리·각도). 생성·초기화 때만 바뀐다.</summary>
    public AntSensingConfig Sensing { get; private set; }

    /// <summary>시뮬레이션 중인 개미들(SoA).</summary>
    public AntPopulation Ants { get; } = new();

    /// <summary>마지막 생성(시작·초기화) 결과.</summary>
    public AntSpawnReport LastSpawn { get; private set; }
    public long Tick { get; private set; }

    /// <summary>초기화 횟수(초기화 세대). 스냅샷에 함께 실려, 초기화 이전에 만든 화면 데이터를 구분하는 데 쓴다.</summary>
    public long ResetCount { get; private set; }

    /// <summary>시뮬레이션 시간(초). 틱 간격이 실행 중 바뀔 수 있으므로 누적한다.</summary>
    public double ElapsedSeconds { get; private set; }

    public void Step(int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            // 개미 계산이 거부할 틱 간격이면 냄새를 바꾸기 전에 멈춘다(냄새만 갱신된 부분 틱 방지). 설정·생성·불러오기 검증으로
            // 이 상태에 오지 않아야 하며, 이는 마지막 방어선이다.
            float antDt = (float)Settings.TickSeconds;
            if (Ants.Count > 0 && !AntSettings.SupportsTick(Settings.TickSeconds))
                throw new InvalidOperationException(AntSettings.TickNotSupportedMessage(Settings.TickSeconds));
            _scentSystem.Tick(Scent, Settings);
            if (Ants.Count > 0)
            {
                // 단계 B1 감각: 이번 틱 농도(Current)를 이동 전 위치·방향에서 읽는다. 이동 상태와 이동 난수는 바꾸지 않는다.
                AntSensing.Sense(Ants, Scent, _scentSystem.OpenMasks(), Sensing, antDt, Tick + 1);
                _antSystem.Tick(Ants, _world, AntSettings, antDt);
            }
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
        if (ants is not null) ThrowIfInvalid(ants);
        ThrowIfTickUnsupported(Settings.TickSeconds, (ants ?? AntSettings).InitialCount > 0);
        var sensing = AntSensingConfig.Create(ants ?? AntSettings, Settings); // 감지 키를 해석할 수 없으면 아무것도 바꾸지 않는다
        if (ants is not null) AntSettings = ants;
        Sensing = sensing;
        _world = _initialWorld.Clone();
        _scentSystem = new ScentSystem(_world);
        Ants.Clear();
        Ants.ConfigureSensing(Sensing.TypeCount);
        LastSpawn = AntSpawner.SpawnInitial(Ants, _world, AntSettings);
        Scent.Clear();
        Tick = 0;
        ElapsedSeconds = 0;
        ResetCount++;
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
        if ((Ants.Count > 0 || AntSettings.InitialCount > 0) && !AntSettings.SupportsTick(settings.TickSeconds))
            return new[] { AntSettings.TickNotSupportedMessage(settings.TickSeconds) };
        Settings = settings;
        return Array.Empty<string>();
    }

    /// <summary>감각 패스가 쓰는 레이어별 냄새 열린 칸(선택한 개미 표시용, 계산 스레드 전용).</summary>
    internal byte[][] SenseOpenMasks() => _scentSystem.OpenMasks(refresh: true);

    /// <summary>월드·냄새·틱을 결합한 상태 해시(재현성 검증용).</summary>
    public ulong ComputeStateHash()
    {
        var hash = Fnv1a64.Create();
        hash.Add((ulong)Tick);
        hash.Add(_world.ComputeContentHash());
        hash.Add(Scent.ComputeHash());
        hash.Add(Ants.ComputeHash());
        hash.Add(Ants.ComputeStageBHash());
        return hash.Value;
    }

    /// <summary>개미가 있거나 생길 실험이면 틱 간격이 개미 계산 범위 안이어야 한다(감사 SA03). 개미 없는 냄새 실험은 제한하지 않는다.</summary>
    private static void ThrowIfTickUnsupported(double tickSeconds, bool hasAnts)
    {
        if (hasAnts && !AntSettings.SupportsTick(tickSeconds))
            throw new ArgumentException(AntSettings.TickNotSupportedMessage(tickSeconds), nameof(tickSeconds));
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
