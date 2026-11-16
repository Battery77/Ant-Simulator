using System.Runtime.InteropServices;
using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Util;
using AntSim.Core.Worlds;
using Xunit.Abstractions;

namespace AntSim.Core.Tests;

/// <summary>
/// 단계 B0: Stage A(개미 이동·충돌·통로·생성·저장) 회귀 기준. 기대값은 Stage B 코드가 들어가기 전의 main(80f28aa)에서
/// 이 파일의 시나리오를 실행해 얻은 값이며(생성 조건은 <see cref="GoldenPlatform"/>), 이후 단계에서 다시 만들지 않는다.
/// <para>
/// 해시는 <see cref="AntPopulation.ComputeHash"/>를 쓰지 않고 이 파일에서 Stage A 필드만 직접 읽어 계산한다(이후 단계가 개체 해시에
/// 감각 필드를 넣어도 기준이 흔들리지 않도록). 대상: Count, NextId, id, caste, layer, mobility, x, y, prevX, prevY, heading,
/// targetHeading, baseSpeed, speedFactor, size, turnTimer, turnIntervalScale, lastTile, 이동 난수 상태.
/// </para>
/// <para>
/// 플랫폼 정책: 이동 계산은 MathF.SinCos·MathF.Exp를 쓰며, 이 함수들은 .NET 8에서 운영체제 C 런타임 구현을 따른다(마지막 비트가
/// 플랫폼마다 다를 수 있다). 그래서 비트 단위 기대값은 기록한 플랫폼(<see cref="GoldenPlatform"/>)에서만 비교한다. 다른 플랫폼에서는
/// 같은 시나리오의 플랫폼 독립 계약(같은 입력 두 번 실행 = 같은 결과, 초기화 재현, 저장·불러오기 이어 실행 = 끊김 없는 실행)을
/// 검사하고, 계산한 해시를 테스트 출력에 남긴다(그 플랫폼의 기준을 기록할 때 쓴다). 기대값을 느슨하게 만들지 않는다.
/// </para>
/// </summary>
public class StageAGoldenTests
{
    /// <summary>기대값을 기록한 플랫폼. 생성: main 80f28aa, .NET SDK 8.0.132 / 런타임 8.0, linux-x64, Release·Debug 동일.</summary>
    public static bool GoldenPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private const float Dt = 0.1f;
    private readonly ITestOutputHelper _output;

    public StageAGoldenTests(ITestOutputHelper output) => _output = output;

    // ---------- 해시 ----------

    /// <summary>Stage A 개체 필드 해시(이 파일 전용 정의).</summary>
    internal static ulong StageAHash(AntPopulation ants)
    {
        int n = ants.Count;
        var h = Fnv1a64.Create();
        h.Add(n);
        h.Add(ants.NextId);
        Add(ref h, ants.IdArray, n); Add(ref h, ants.CasteArray, n); Add(ref h, ants.LayerArray, n); Add(ref h, ants.MobilityArray, n);
        Add(ref h, ants.XArray, n); Add(ref h, ants.YArray, n); Add(ref h, ants.PrevXArray, n); Add(ref h, ants.PrevYArray, n);
        Add(ref h, ants.HeadingArray, n); Add(ref h, ants.TargetHeadingArray, n);
        Add(ref h, ants.BaseSpeedArray, n); Add(ref h, ants.SpeedFactorArray, n); Add(ref h, ants.SizeArray, n);
        Add(ref h, ants.TurnTimerArray, n); Add(ref h, ants.TurnIntervalScaleArray, n);
        Add(ref h, ants.LastTileArray, n); Add(ref h, ants.RngArray, n);
        return h.Value;

        static void Add<T>(ref Fnv1a64 hash, T[] array, int count) where T : unmanaged =>
            hash.Add(MemoryMarshal.AsBytes(array.AsSpan(0, count)));
    }

    /// <summary>이동 난수 상태만의 해시(난수 소비 순서·횟수 검사).</summary>
    internal static ulong RngHash(AntPopulation ants)
    {
        var h = Fnv1a64.Create();
        h.Add(MemoryMarshal.AsBytes(ants.RngArray.AsSpan(0, ants.Count)));
        return h.Value;
    }

    private static ulong Combine(params ulong[] values)
    {
        var h = Fnv1a64.Create();
        foreach (ulong v in values) h.Add(v);
        return h.Value;
    }

    // ---------- 지도 ----------

    private static readonly TileState Rock = new(Terrain.Dirt, SolidBlock.Rock, 0, 0);
    private static readonly TileState Water = new(Terrain.Water, SolidBlock.None, 0, 0);

    private static void Set(World world, LayerId layer, int x, int y, TileState state) =>
        Assert.Equal(WorldError.None, world.GetLayer(layer).TrySetState(x, y, state));

    /// <summary>시나리오 2: 32×32 지상, 바위 벽 두 개. 개미는 중앙 반경 12에 생성되어 벽과 맵 경계에 부딪힌다.</summary>
    internal static World WallsWorld()
    {
        var world = WorldPresets.Empty(new WorldConfig(32, 32));
        for (int y = 4; y < 26; y++) Set(world, LayerId.Surface, 10, y, Rock);
        for (int x = 12; x < 30; x++) Set(world, LayerId.Surface, x, 21, Rock);
        Set(world, LayerId.Surface, 20, 10, Rock);
        Set(world, LayerId.Surface, 21, 11, Rock); // 대각 모서리
        return world;
    }

    /// <summary>시나리오 3: 32×32 지상, 물 웅덩이와 물 기둥(개미는 물을 지나지 못한다).</summary>
    internal static World WaterWorld()
    {
        var world = WorldPresets.Empty(new WorldConfig(32, 32));
        for (int y = 8; y < 24; y++)
        for (int x = 18; x < 25; x++)
            Set(world, LayerId.Surface, x, y, Water);
        for (int y = 0; y < 14; y++) Set(world, LayerId.Surface, 12, y, Water);
        return world;
    }

    /// <summary>시나리오 4~7: 64×64 표준 개미집, 통로 3개, 지상 먹이·유충(속도 구간과 냄새가 실제로 작동).</summary>
    internal static World NestWorld()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(64, 64));
        var editor = new MapEditor(world);
        editor.Apply(EditOp.AddPassage(20, 20));
        editor.Apply(EditOp.AddPassage(40, 40));
        editor.Apply(EditOp.AddPassage(20, 44));
        for (int y = 26; y < 34; y++)
        for (int x = 26; x < 34; x++)
            Set(world, LayerId.Surface, x, y, new TileState(Terrain.Dirt, SolidBlock.None, (byte)((x + y) % 9), (byte)(x % 3)));
        Set(world, LayerId.Surface, 30, 20, Rock);
        Set(world, LayerId.Surface, 31, 20, Water);
        return world;
    }

    private static readonly AntSettings NestAnts = AntSettings.Default with { InitialCount = 400, Seed = 7 };

    // ---------- 기대값 (main 80f28aa, linux-x64) ----------

    private static readonly Dictionary<string, ulong> Expected = new()
    {
        // GOLDEN-BEGIN
        ["count1.final"] = 0xFA866C2CB9045A2EUL,
        ["count1.rng"] = 0x34A7A81B48B77DF4UL,
        ["count1.trace"] = 0xF9B296B79A7D25C0UL,
        ["count1000.final"] = 0xC4F1B1798876D429UL,
        ["count1000.rng"] = 0xF765341D1AFE0641UL,
        ["count1000.trace"] = 0x0423A329DE11E66DUL,
        ["count37.final"] = 0xFD4356915A288092UL,
        ["count37.rng"] = 0x0293568D323C5333UL,
        ["count37.trace"] = 0xC8A70214DEAF49EAUL,
        ["fixture.continued"] = 0xA58F190D4AAF1C4DUL,
        ["fixture.file"] = 0xB8ABDDAA2137D12EUL,
        ["fixture.loaded"] = 0xBA459D2E41A20748UL,
        ["open.final"] = 0x131EE81B201F5468UL,
        ["open.rng"] = 0x7F746E0DA40247F8UL,
        ["open.trace"] = 0x8168847B111A29AAUL,
        ["passages.final"] = 0x8AA22EA2F538CBF6UL,
        ["passages.rng"] = 0xABB09F2CDDD12D72UL,
        ["passages.surface"] = 0x0000000000000169UL,
        ["passages.trace"] = 0x35E5C339E697F9F9UL,
        ["passages.transitions"] = 0x000000000000019BUL,
        ["save.150.state"] = 0xBA459D2E41A20748UL,
        ["session.300"] = 0xA58F190D4AAF1C4DUL,
        ["session.300.scent"] = 0x0081BBDB9C6C5B72UL,
        ["session.reset50.200"] = 0x37B494859AD11872UL,
        ["session.spawn"] = 0x8A9C1B2DAD5EFD82UL,
        ["walls.final"] = 0xBE2AA3011277DCBEUL,
        ["walls.rng"] = 0x5FC97C6ADD8B32D3UL,
        ["walls.trace"] = 0x2EF7FE3EE3BEE8E9UL,
        ["water.final"] = 0x0A804AB65FDBB895UL,
        ["water.rng"] = 0xE58856F865162E80UL,
        ["water.trace"] = 0x0240E38652FEF54DUL,
        // GOLDEN-END
    };

    private void Check(string name, ulong actual)
    {
        _output.WriteLine($"[\"{name}\"] = 0x{actual:X16}UL,");
        if (!GoldenPlatform) return;
        Assert.True(Expected.TryGetValue(name, out ulong expected), $"기대값 없음: {name} = 0x{actual:X16}");
        Assert.Equal(expected, actual);
    }

    // ---------- 시나리오 ----------

    private static (ulong Final, ulong Rng, ulong Trace) RunSystem(World world, AntSettings settings, int ticks)
    {
        var ants = new AntPopulation();
        var report = AntSpawner.SpawnInitial(ants, world, settings);
        Assert.Equal(settings.InitialCount, report.Spawned);
        var system = new AntSystem();
        var trace = Fnv1a64.Create();
        for (int t = 0; t < ticks; t++)
        {
            system.Tick(ants, world, settings, Dt);
            if (t % 50 == 49) trace.Add(StageAHash(ants));
        }
        return (StageAHash(ants), RngHash(ants), trace.Value);
    }

    private void CheckSystem(string name, Func<World> world, AntSettings settings, int ticks)
    {
        var a = RunSystem(world(), settings, ticks);
        var b = RunSystem(world(), settings, ticks);
        Assert.Equal(a, b); // 플랫폼 독립: 같은 입력 = 같은 결과
        Check(name + ".final", a.Final);
        Check(name + ".rng", a.Rng);
        Check(name + ".trace", a.Trace);
    }

    [Fact]
    public void Scenario1_OpenSurface() =>
        CheckSystem("open", () => WorldPresets.Empty(new WorldConfig(64, 64)), AntSettings.Default with { InitialCount = 100, Seed = 11 }, 400);

    [Fact]
    public void Scenario2_WallsAndMapBoundary()
    {
        CheckSystem("walls", WallsWorld, AntSettings.Default with { InitialCount = 300, Seed = 3 }, 600);
        // 시나리오가 실제로 벽·경계에 닿는지(골든이 빈 시나리오가 아닌지) 확인: 경계 1칸 안이나 벽 옆에 간 개미가 있다.
        var world = WallsWorld();
        var ants = new AntPopulation();
        AntSpawner.SpawnInitial(ants, world, AntSettings.Default with { InitialCount = 300, Seed = 3 });
        var system = new AntSystem();
        bool edge = false;
        for (int t = 0; t < 600; t++)
        {
            system.Tick(ants, world, AntSettings.Default with { InitialCount = 300, Seed = 3 }, Dt);
            for (int i = 0; i < ants.Count; i++)
                if (ants.X[i] < 1 || ants.Y[i] < 1 || ants.X[i] > 31 || ants.Y[i] > 31) edge = true;
        }
        Assert.True(edge);
    }

    [Fact]
    public void Scenario3_Water()
    {
        CheckSystem("water", WaterWorld, AntSettings.Default with { InitialCount = 200, Seed = 5 }, 600);
        var world = WaterWorld();
        var ants = new AntPopulation();
        var settings = AntSettings.Default with { InitialCount = 200, Seed = 5 };
        AntSpawner.SpawnInitial(ants, world, settings);
        var system = new AntSystem();
        for (int t = 0; t < 600; t++)
        {
            system.Tick(ants, world, settings, Dt);
            for (int i = 0; i < ants.Count; i++)
                Assert.NotEqual(Terrain.Water, world.Surface.GetTerrain(ants.Get(i).TileX, ants.Get(i).TileY));
        }
    }

    [Fact]
    public void Scenario4_PassageLayerTransitions()
    {
        CheckSystem("passages", NestWorld, NestAnts, 800);
        // 전환 횟수(틱마다 레이어가 바뀐 개미 수의 합)도 기준으로 고정한다.
        var world = NestWorld();
        var ants = new AntPopulation();
        AntSpawner.SpawnInitial(ants, world, NestAnts);
        var system = new AntSystem();
        var before = new byte[ants.Count];
        long transitions = 0;
        for (int t = 0; t < 800; t++)
        {
            ants.Layer.CopyTo(before);
            system.Tick(ants, world, NestAnts, Dt);
            for (int i = 0; i < ants.Count; i++)
                if (ants.Layer[i] != before[i]) transitions++;
        }
        Assert.True(transitions > 0);
        Check("passages.transitions", (ulong)transitions);
        Check("passages.surface", (ulong)ants.Layer.ToArray().Count(l => l == (byte)LayerId.Surface));
    }

    private static SimulationSession NewSession(AntSettings? ants = null) =>
        new(NestWorld(), ScentSettings.Default, ants ?? NestAnts);

    private static ulong SessionHash(SimulationSession s) =>
        Combine(StageAHash(s.Ants), RngHash(s.Ants), s.Scent.ComputeHash(), s.World.ComputeContentHash(), (ulong)s.Tick);

    [Fact]
    public void Scenario5_SpawnAndReset()
    {
        var session = NewSession();
        Check("session.spawn", StageAHash(session.Ants));
        session.Step(300);
        ulong first = SessionHash(session);
        Check("session.300", first);
        Check("session.300.scent", session.Scent.ComputeHash());

        session.Reset();
        Assert.Equal(StageAHash(NewSession().Ants), StageAHash(session.Ants)); // 초기화 = 새 실험
        session.Step(300);
        Assert.Equal(first, SessionHash(session)); // 플랫폼 독립: 초기화 재현

        session.Reset(NestAnts with { InitialCount = 50, Seed = 9 });
        session.Step(200);
        Check("session.reset50.200", SessionHash(session));
    }

    [Fact]
    public void Scenario6_SaveLoadContinue()
    {
        var straight = NewSession();
        straight.Step(300);

        var session = NewSession();
        session.Step(150);
        using var buffer = new MemoryStream();
        ExperimentSerializer.Save(session, buffer);
        buffer.Position = 0;
        var loaded = ExperimentSerializer.Load(buffer);
        Assert.Equal(StageAHash(session.Ants), StageAHash(loaded.Ants));
        loaded.Step(150);
        Assert.Equal(SessionHash(straight), SessionHash(loaded)); // 플랫폼 독립: 저장·불러오기 후 이어 실행 = 끊김 없는 실행
        Check("save.150.state", StageAHash(session.Ants));
    }

    /// <summary>v2 고정 파일(main에서 저장)을 불러와 이어 실행한다. 파일은 바꾸지 않으며, 불러오기와 이어 실행 결과를 고정한다.</summary>
    [Fact]
    public void Scenario6_V2FixtureLoadAndContinue()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "experiment-v2-stagea.antexp");
        var bytes = File.ReadAllBytes(path);
        var fileHash = Fnv1a64.Create();
        fileHash.Add(bytes);
        Assert.Equal(2, BitConverter.ToUInt16(bytes, 8));
        var loaded = ExperimentSerializer.Load(new MemoryStream(bytes));
        Assert.Equal(150, loaded.Tick);
        Check("fixture.file", fileHash.Value);
        Check("fixture.loaded", StageAHash(loaded.Ants));
        loaded.Step(150);
        Check("fixture.continued", SessionHash(loaded));
        if (GoldenPlatform)
        {
            var straight = NewSession();
            straight.Step(300);
            Assert.Equal(SessionHash(straight), SessionHash(loaded)); // 고정 파일 = 이 시나리오의 150틱 상태
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(1000)]
    public void Scenario7_DifferentCounts(int count)
    {
        var settings = NestAnts with { InitialCount = count };
        CheckSystem($"count{count}", NestWorld, settings, 200);
    }

    /// <summary>B0 생성 도구: 고정 v2 파일을 만든다(main에서 한 번 실행). 환경 변수가 없으면 아무것도 하지 않는다.</summary>
    [Fact]
    public void Tool_WriteV2Fixture()
    {
        string? target = Environment.GetEnvironmentVariable("ANTSIM_WRITE_V2_FIXTURE");
        if (string.IsNullOrEmpty(target)) return;
        var session = NewSession();
        session.Step(150);
        using var file = File.Create(target);
        ExperimentSerializer.Save(session, file);
    }
}
