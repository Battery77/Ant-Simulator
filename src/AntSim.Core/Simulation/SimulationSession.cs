using AntSim.Core.Scent;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Simulation;

/// <summary>
/// 시뮬레이션 모드의 독립 실험 상태. 시작할 때 편집 월드를 복제하므로, 시뮬레이션은 원본 맵을 바꾸지 않고
/// 원본 편집도 시뮬레이션에 영향을 주지 않는다. 같은 시작 월드·설정·틱 수면 항상 같은 상태가 된다.
/// </summary>
/// <remarks>스레드 안전하지 않다. <see cref="SimulationHost"/>가 계산 스레드에서만 다룬다.</remarks>
public sealed class SimulationSession
{
    private readonly World _initialWorld;
    private World _world;
    private ScentSystem _scentSystem;

    /// <summary>편집 월드 <paramref name="source"/>를 복제해 새 실험을 시작한다.</summary>
    public SimulationSession(World source, ScentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfInvalid(settings);
        _initialWorld = source.Clone();
        _world = _initialWorld.Clone();
        _scentSystem = new ScentSystem(_world);
        Settings = settings;
        Scent = new ScentState(_world.Config, settings.Types.Count);
    }

    /// <summary>저장된 실험 상태에서 복원한다(<see cref="Serialization.ExperimentSerializer"/> 전용).</summary>
    internal SimulationSession(World initialWorld, World currentWorld, ScentSettings settings, ScentState scent, long tick, double elapsedSeconds)
    {
        ThrowIfInvalid(settings);
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
    public long Tick { get; private set; }

    /// <summary>초기화 횟수(초기화 세대). 스냅샷에 함께 실려, 초기화 이전에 만든 화면 데이터를 구분하는 데 쓴다.</summary>
    public long ResetCount { get; private set; }

    /// <summary>시뮬레이션 시간(초). 틱 간격이 실행 중 바뀔 수 있으므로 누적한다.</summary>
    public double ElapsedSeconds { get; private set; }

    public void Step(int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            _scentSystem.Tick(Scent, Settings);
            Tick++;
            ElapsedSeconds += Settings.TickSeconds;
        }
    }

    /// <summary>시작 상태(시작 월드, 냄새 0, 틱 0)로 되돌린다. 설정은 유지한다.</summary>
    public void Reset()
    {
        _world = _initialWorld.Clone();
        _scentSystem = new ScentSystem(_world);
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
        return hash.Value;
    }

    private static void ThrowIfInvalid(ScentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
    }
}
