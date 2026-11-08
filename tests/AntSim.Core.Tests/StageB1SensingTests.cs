using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;
using Xunit.Abstractions;

namespace AntSim.Core.Tests;

/// <summary>
/// 단계 B1 페로몬 감각(docs/STAGE_B_DESIGN.md C.1~D.3, H). 기대값은 손으로 계산한 값이며 구현 식을 다시 쓰지 않는다
/// (센서 좌표는 회전 행렬 대신 cos/sin(h ∓ θ)를 double로 직접 계산해 비교한다).
/// </summary>
public class StageB1SensingTests(ITestOutputHelper output)
{
    private const float Tolerance = 1e-5f;
    private static readonly TileState Rock = new(Terrain.Dirt, SolidBlock.Rock, 0, 0);
    private static readonly TileState Water = new(Terrain.Water, SolidBlock.None, 0, 0);

    // ---------- 도구 ----------

    private sealed class Rig
    {
        public World World = null!;
        public ScentSettings Scent = ScentSettings.Default;
        public ScentState State = null!;
        public AntSettings Settings = AntSettings.Default;
        public AntSensingConfig Config = null!;
        public AntPopulation Ants = new();

        public static Rig Create(int width = 16, int height = 16, AntSettings? settings = null, ScentSettings? scent = null)
        {
            var rig = new Rig { World = WorldPresets.Empty(new WorldConfig(width, height)) };
            if (settings is not null) rig.Settings = settings;
            if (scent is not null) rig.Scent = scent;
            rig.State = new ScentState(rig.World.Config, rig.Scent.Types.Count);
            return rig;
        }

        public void Solid(int x, int y, LayerId layer = LayerId.Surface) =>
            Assert.Equal(WorldError.None, World.GetLayer(layer).TrySetState(x, y, Rock));

        public void Wet(int x, int y) => Assert.Equal(WorldError.None, World.Surface.TrySetState(x, y, Water));

        public void Set(int type, int x, int y, float value, LayerId layer = LayerId.Surface) =>
            State.MutableField(type, layer)[y * World.Config.Width + x] = value;

        public void Fill(int type, Func<int, int, float> value, LayerId layer = LayerId.Surface)
        {
            var field = State.MutableField(type, layer);
            for (int y = 0; y < World.Config.Height; y++)
            for (int x = 0; x < World.Config.Width; x++)
                field[y * World.Config.Width + x] = value(x, y);
        }

        /// <summary>개미 한 마리를 놓고(방향 지정) 바로 다음 감각 패스에서 감지하게 한 뒤 한 번 감지한다. 개미 인덱스를 돌려준다.</summary>
        public int SenseOne(float x, float y, float heading, LayerId layer = LayerId.Surface)
        {
            Config = AntSensingConfig.Create(Settings, Scent);
            Ants = new AntPopulation();
            Ants.ConfigureSensing(Config.TypeCount);
            Assert.True(AntSpawner.TrySpawn(Ants, World, Settings, layer, x, y, out int index, heading: heading));
            Ants.SenseTimerArray[index] = 0.01f;
            AntSensing.Sense(Ants, State, new ScentSystem(World).OpenMasks(), Config, 0.1f, 1);
            Assert.Equal(1, Ants.GetSense(index, new float[Config.TypeCount * 3]).SenseTick);
            return index;
        }

        public float Value(int index, int type, int sensor)
        {
            var values = new float[Config.TypeCount * 3];
            Ants.GetSense(index, values);
            return values[type * 3 + sensor];
        }

        public AntSenseInfo Info(int index) => Ants.GetSense(index, new float[Config.TypeCount * 3]);
    }

    private const int Left = 0, Front = 1, Right = 2;

    // ---------- 1·2. Stage A 이동·이동 난수 불변 ----------

    /// <summary>
    /// 감각을 켠 세션(K = 2 기본, K = 8·거리 2·각도 90)과, 같은 월드·설정에서 감각 없이 이동만 돌린 결과가 Stage A 필드·이동 난수까지 같다.
    /// (플랫폼 독립 계약. 비트 기준은 StageAGoldenTests.)
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sensing_DoesNotChangeStageAMovement_OrMovementRng(bool eightTypes)
    {
        var scent = eightTypes ? EightTypes() : ScentSettings.Default;
        var ants = StageAGoldenTests_NestAnts() with
        {
            SensedScentKeys = eightTypes ? scent.Types.Select(t => t.Key).ToArray() : null,
            SensorDistance = eightTypes ? 2f : 1f,
            SensorAngleDegrees = eightTypes ? 90f : 45f,
            SensePeriodSeconds = eightTypes ? 0.1f : 0.3f,
        };
        var session = new SimulationSession(NestWorld(), scent, ants);
        Assert.Equal(eightTypes ? 8 : 2, session.Ants.SensedTypeCount);

        var world = NestWorld();
        var plain = new AntPopulation();
        AntSpawner.SpawnInitial(plain, world, ants);
        var system = new AntSystem();
        for (int t = 0; t < 400; t++)
        {
            session.Step();
            system.Tick(plain, world, ants, 0.1f);
            if (t % 40 == 39)
            {
                Assert.Equal(StageAGoldenTests.StageAHash(plain), StageAGoldenTests.StageAHash(session.Ants));
                Assert.Equal(StageAGoldenTests.RngHash(plain), StageAGoldenTests.RngHash(session.Ants));
            }
        }
        // 감각이 실제로 일어났다(빈 시험이 아님).
        Assert.True(Enumerable.Range(0, session.Ants.Count).Count(i => session.Ants.GetSense(i, new float[24]).HasSensed) == session.Ants.Count);
    }

    private static AntSettings StageAGoldenTests_NestAnts() => AntSettings.Default with { InitialCount = 300, Seed = 7 };

    private static World NestWorld() => StageAGoldenTests.NestWorld();

    private static ScentSettings EightTypes() => new()
    {
        Types = Enumerable.Range(0, 8).Select(i => new ScentTypeSettings(i switch { 0 => "food", 1 => "larva", _ => $"p{i}" },
            $"냄새{i}", i switch { 0 => QuantityKind.Food, 1 => QuantityKind.Larva, _ => null })).ToArray(),
    };

    // ---------- 3. 종류 분리 / 레이어 분리 ----------

    [Fact]
    public void Types_AreSeparated_AndKeyOrderFollowsResolvedList()
    {
        var rig = Rig.Create();
        rig.Fill(0, (_, _) => 2f); // food
        rig.Fill(1, (_, _) => 0f); // larva
        int i = rig.SenseOne(8.5f, 8.5f, 0);
        for (int d = 0; d < 3; d++)
        {
            Assert.Equal(2f, rig.Value(i, 0, d), Tolerance);
            Assert.Equal(0f, rig.Value(i, 1, d));
        }
        // 명시 순서 [larva, food]: 값 배열의 0번이 larva가 된다.
        rig.Settings = AntSettings.Default with { SensedScentKeys = new[] { "larva", "food" } };
        i = rig.SenseOne(8.5f, 8.5f, 0);
        Assert.Equal(new[] { "larva", "food" }, rig.Config.Keys);
        Assert.Equal(new[] { 1, 0 }, rig.Config.TypeIndices);
        Assert.Equal(0f, rig.Value(i, 0, Front));
        Assert.Equal(2f, rig.Value(i, 1, Front), Tolerance);
    }

    [Fact]
    public void OnlyTheAntsLayerIsRead()
    {
        var rig = Rig.Create();
        rig.Fill(0, (_, _) => 5f, LayerId.Nest); // 개미집 레이어에만 냄새
        rig.World.Nest.FillUnchecked(TilePlane.Solid, 0);
        int i = rig.SenseOne(8.5f, 8.5f, 0);
        for (int d = 0; d < 3; d++) Assert.Equal(0f, rig.Value(i, 0, d));
        i = rig.SenseOne(8.5f, 8.5f, 0, LayerId.Nest);
        for (int d = 0; d < 3; d++) Assert.Equal(5f, rig.Value(i, 0, d), Tolerance);
    }

    // ---------- 4·5·6. 키 해석, K = 0/1/2/8, 잘못된 키 ----------

    private static ScentSettings Types(params string[] keys) =>
        new() { Types = keys.Select(k => new ScentTypeSettings(k, k, null)).ToArray() };

    [Fact]
    public void DefaultKeys_AreTheExistingOnes_InFoodLarvaOrder()
    {
        Assert.Equal(new[] { "food", "larva" }, AntSensingConfig.Create(AntSettings.Default, Types("larva", "x", "food")).Keys);
        Assert.Equal(new[] { 2, 0 }, AntSensingConfig.Create(AntSettings.Default, Types("larva", "x", "food")).TypeIndices);
        Assert.Equal(new[] { "larva" }, AntSensingConfig.Create(AntSettings.Default, Types("x", "larva")).Keys);
        Assert.Empty(AntSensingConfig.Create(AntSettings.Default, Types("x", "y")).Keys); // 사용자 정의 냄새만: K = 0, 오류 아님
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void SessionsWork_ForKZeroOneTwoEight(int k)
    {
        var scent = EightTypes();
        var ants = AntSettings.Default with { InitialCount = 60, Seed = 2, SensedScentKeys = scent.Types.Take(k).Select(t => t.Key).ToArray() };
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), scent, ants);
        Assert.Equal(k, session.Ants.SensedTypeCount);
        Assert.Equal(k, session.Sensing.TypeCount);
        session.Step(10);
        var values = new float[k * 3];
        var info = session.Ants.GetSense(0, values);
        Assert.True(info.HasSensed);
        Assert.Equal(k, info.SensedTypeCount);
        Assert.Null(Serialization.ExperimentSerializer.ValidateStageB(session.Ants, session.Tick, 0.3f, session.World));
    }

    [Fact]
    public void InvalidKeyLists_AreRejected_AndResetKeepsTheSession()
    {
        var nine = Enumerable.Range(0, 9).Select(i => $"k{i}").ToArray();
        Assert.Contains((AntSettings.Default with { SensedScentKeys = nine }).Validate(), e => e.Contains("8개 이하"));
        Assert.Contains((AntSettings.Default with { SensedScentKeys = new[] { "food", "food" } }).Validate(), e => e.Contains("중복"));
        Assert.Contains((AntSettings.Default with { SensedScentKeys = new[] { "" } }).Validate(), e => e.Contains("비어"));
        Assert.Contains((AntSettings.Default with { SensedScentKeys = new string[] { null! } }).Validate(), e => e.Contains("비어"));
        Assert.Empty((AntSettings.Default with { SensedScentKeys = Array.Empty<string>() }).Validate()); // 명시적 빈 목록 = K 0

        // 냄새 설정에 없는 키: 조용히 빼지 않고 거부한다.
        var missing = AntSettings.Default with { SensedScentKeys = new[] { "food", "nope" } };
        Assert.Empty(missing.Validate());
        var e1 = Assert.Throws<ArgumentException>(() => new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default, missing));
        Assert.Contains("nope", e1.Message);

        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default, AntSettings.Default with { InitialCount = 20 });
        session.Step(7);
        ulong before = session.ComputeStateHash();
        Assert.Throws<ArgumentException>(() => session.Reset(missing));
        Assert.Throws<ArgumentException>(() => session.Reset(AntSettings.Default with { SensedScentKeys = nine }));
        Assert.Equal(before, session.ComputeStateHash()); // 거부되면 아무것도 바뀌지 않는다
        Assert.Equal(2, session.Ants.SensedTypeCount);

        session.Reset(AntSettings.Default with { InitialCount = 20, SensedScentKeys = new[] { "larva" } });
        Assert.Equal(1, session.Ants.SensedTypeCount);
        Assert.Equal(new[] { "larva" }, session.Sensing.Keys);
    }

    // ---------- 7·8·9. 센서 좌표, 거리·각도, 범위 ----------

    [Theory]
    [InlineData(0.0, 45f, 1f)]
    [InlineData(Math.PI / 2, 45f, 1f)]
    [InlineData(-3 * Math.PI / 4, 45f, 1f)]
    [InlineData(Math.PI, 45f, 1f)]
    [InlineData(0.3, 30f, 1.5f)]
    [InlineData(-1.2, 90f, 2f)]
    [InlineData(2.0, 10f, 0.5f)]
    public void SensorPositions_MatchDirectTrigonometry(double heading, float angle, float distance)
    {
        var settings = AntSettings.Default with { SensorAngleDegrees = angle, SensorDistance = distance };
        var config = AntSensingConfig.Create(settings, ScentSettings.Default);
        var points = new float[6];
        var world = WorldPresets.Empty(new WorldConfig(32, 32));
        AntSensing.Preview(16f, 16f, (float)heading, 0, new ScentSystem(world).OpenMasks(), 32, 32, config, points, out byte blocked);
        Assert.Equal(0, blocked);
        double theta = angle * Math.PI / 180;
        double[] directions = { heading - theta, heading, heading + theta }; // 왼쪽 = Heading − θ
        for (int d = 0; d < 3; d++)
        {
            Assert.Equal(16 + distance * Math.Cos(directions[d]), points[2 * d], 4);
            Assert.Equal(16 + distance * Math.Sin(directions[d]), points[2 * d + 1], 4);
        }
    }

    [Fact]
    public void DefaultSensors_AreFrontAndPlusMinus45_AtOneTile()
    {
        var points = new float[6];
        var world = WorldPresets.Empty(new WorldConfig(32, 32));
        AntSensing.Preview(10f, 10f, 0f, 0, new ScentSystem(world).OpenMasks(), 32, 32, AntSensingConfig.Create(AntSettings.Default, ScentSettings.Default), points, out _);
        float r = MathF.Sqrt(2) / 2;
        Assert.Equal(new[] { 10 + r, 10 - r, 11f, 10f, 10 + r, 10 + r }, points, new FloatComparer(1e-5f)); // y 아래로 증가: 왼쪽 = 화면 위
    }

    private sealed class FloatComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => MathF.Abs(a - b) <= tolerance;
        public int GetHashCode(float v) => 0;
    }

    [Theory]
    [InlineData(nameof(AntSettings.SensorDistance), 2.0001f, "센서 거리")]
    [InlineData(nameof(AntSettings.SensorDistance), 3f, "센서 거리")]
    [InlineData(nameof(AntSettings.SensorDistance), 0f, "센서 거리")]
    [InlineData(nameof(AntSettings.SensorDistance), -1f, "센서 거리")]
    [InlineData(nameof(AntSettings.SensorDistance), float.NaN, "센서 거리")]
    [InlineData(nameof(AntSettings.SensorDistance), float.PositiveInfinity, "센서 거리")]
    [InlineData(nameof(AntSettings.SensorAngleDegrees), 90.01f, "센서 각도")]
    [InlineData(nameof(AntSettings.SensorAngleDegrees), 0f, "센서 각도")]
    [InlineData(nameof(AntSettings.SensorAngleDegrees), float.NaN, "센서 각도")]
    [InlineData(nameof(AntSettings.SensePeriodSeconds), 0f, "감지 주기")]
    [InlineData(nameof(AntSettings.SensePeriodSeconds), 60.01f, "감지 주기")]
    [InlineData(nameof(AntSettings.SensePeriodSeconds), float.NaN, "감지 주기")]
    [InlineData(nameof(AntSettings.SensePeriodSeconds), float.NegativeInfinity, "감지 주기")]
    [InlineData(nameof(AntSettings.PathErrorSigma), -0.01f, "σ")]
    [InlineData(nameof(AntSettings.PathErrorSigma), 10.01f, "σ")]
    [InlineData(nameof(AntSettings.PathErrorSigma), float.NaN, "σ")]
    public void OutOfRangeSensingSettings_AreRejected(string field, float value, string message)
    {
        var settings = field switch
        {
            nameof(AntSettings.SensorDistance) => AntSettings.Default with { SensorDistance = value },
            nameof(AntSettings.SensorAngleDegrees) => AntSettings.Default with { SensorAngleDegrees = value },
            nameof(AntSettings.SensePeriodSeconds) => AntSettings.Default with { SensePeriodSeconds = value },
            _ => AntSettings.Default with { PathErrorSigma = value },
        };
        Assert.Contains(settings.Validate(), e => e.Contains(message));
        Assert.Throws<ArgumentException>(() => new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default, settings));
    }

    [Fact]
    public void Defaults_AndUpperBounds_AreAccepted()
    {
        Assert.Equal(0.3f, AntSettings.Default.SensePeriodSeconds);
        Assert.Equal(1f, AntSettings.Default.SensorDistance);
        Assert.Equal(45f, AntSettings.Default.SensorAngleDegrees);
        Assert.Equal(0.14f, AntSettings.Default.PathErrorSigma);
        Assert.Null(AntSettings.Default.SensedScentKeys);
        Assert.Equal(2f, AntSettings.MaxSensorDistance);
        Assert.Equal(8, AntSettings.MaxSensedTypes);
        Assert.Empty((AntSettings.Default with { SensorDistance = 2f, SensorAngleDegrees = 90f, SensePeriodSeconds = 60f, PathErrorSigma = 10f }).Validate());
        Assert.Empty((AntSettings.Default with { PathErrorSigma = 0f }).Validate());
    }

    // ---------- 10. 보간 ----------

    [Fact]
    public void UniformField_GivesTheSameValue_EvenNextToWalls()
    {
        var rig = Rig.Create();
        for (int y = 0; y < 16; y++) rig.Solid(10, y);
        rig.Fill(0, (x, _) => x == 10 ? 0f : 0.7f); // 고체 칸의 농도는 항상 0
        foreach (var (x, y, h) in new[] { (5.5f, 5.5f, 0f), (9.1f, 4.2f, 0.4f), (8.8f, 12.6f, -0.3f), (2.2f, 2.9f, 2.5f) })
        {
            int i = rig.SenseOne(x, y, h);
            for (int d = 0; d < 3; d++)
                if (!rig.Info(i).IsBlocked(d)) Assert.Equal(0.7f, rig.Value(i, 0, d), 1e-6f);
        }
    }

    [Fact]
    public void LinearField_IsInterpolatedExactly()
    {
        // c(칸 x) = x + 0.5 = 칸 중심의 x좌표. 칸 중심 기준 이중선형이면 센서점의 값은 센서 x좌표와 같다.
        var rig = Rig.Create(32, 32, AntSettings.Default with { SensorDistance = 1.5f, SensorAngleDegrees = 30f });
        rig.Fill(0, (x, _) => x + 0.5f);
        int i = rig.SenseOne(10.3f, 10.6f, 0.3f);
        double[] directions = { 0.3 - Math.PI / 6, 0.3, 0.3 + Math.PI / 6 };
        for (int d = 0; d < 3; d++) Assert.Equal(10.3 + 1.5 * Math.Cos(directions[d]), rig.Value(i, 0, d), 4);
    }

    // ---------- 11·12·13. 벽 차폐, 벽 너머 샘플, 대각 모서리 (설계 D.2 예 1~3) ----------

    [Fact]
    public void Example1_WallBetweenAntAndSensor_Blocks_AtDistanceOneAndTwo()
    {
        var rig = Rig.Create();
        for (int y = 0; y < 16; y++) rig.Solid(6, y);
        rig.Set(0, 5, 5, 0.4f);
        rig.Set(0, 7, 5, 0.9f);
        rig.Fill(1, (x, _) => x == 6 ? 0 : 3f);
        int i = rig.SenseOne(5.5f, 5.5f, 0);
        Assert.True(rig.Info(i).IsBlocked(Front));
        Assert.Equal(0f, rig.Value(i, 0, Front));
        Assert.Equal(0f, rig.Value(i, 1, Front)); // 차폐는 모든 종류에 공통

        rig.Settings = AntSettings.Default with { SensorDistance = 2f }; // 센서 (7.5, 5.5)는 열린 칸이지만 경로가 벽을 지난다
        i = rig.SenseOne(5.5f, 5.5f, 0);
        Assert.True(rig.Info(i).IsBlocked(Front));
        Assert.Equal(0f, rig.Value(i, 0, Front));
    }

    [Fact]
    public void Example2_SamplesBehindTheWall_AreNotMixed()
    {
        var rig = Rig.Create();
        for (int y = 0; y < 16; y++) rig.Solid(6, y);
        rig.Set(0, 5, 5, 0.4f);
        rig.Set(0, 5, 6, 0.2f);
        rig.Set(0, 7, 5, 0.9f);
        int i = rig.SenseOne(4.9f, 5.5f, 0); // 센서 (5.9, 5.5): u = 5.4, v = 5.0
        Assert.False(rig.Info(i).IsBlocked(Front));
        Assert.Equal(0.4f, rig.Value(i, 0, Front), 1e-6f); // 막힌 칸을 0으로 넣었다면 0.24
    }

    [Fact]
    public void Example3_DiagonalBehindABlockedCorner_IsExcluded()
    {
        var rig = Rig.Create();
        rig.Solid(6, 5);
        rig.Solid(5, 6);
        rig.Set(0, 5, 5, 0.4f);
        rig.Set(0, 6, 6, 0.9f);
        int i = rig.SenseOne(5.2f, 5.2f, MathF.PI / 4); // 정면 센서 ≈ (5.907, 5.907), 칸 (5,5)
        Assert.False(rig.Info(i).IsBlocked(Front));
        Assert.Equal(0.4f, rig.Value(i, 0, Front), 1e-6f); // 열린 칸만 재정규화했다면 ≈ 0.56
        Assert.True(rig.Info(i).IsBlocked(Left));  // h − 45° = 0 → (6.2, 5.2) 고체
        Assert.True(rig.Info(i).IsBlocked(Right)); // h + 45° = 90° → (5.2, 6.2) 고체
        Assert.Equal(0b101, rig.Info(i).Flags);
    }

    [Fact]
    public void ScentThatDiffusedAroundAWall_IsSensed_ButNotThroughTheWall()
    {
        // 벽 x = 8 (y 0..11), 아래쪽 y ≥ 12는 열림. 먹이는 벽 오른쪽 (12, 4). 냄새는 틈을 돌아 왼쪽에 도달한다.
        var rig = Rig.Create();
        for (int y = 0; y < 12; y++) rig.Solid(8, y);
        Assert.Equal(WorldError.None, rig.World.Surface.TrySetState(12, 4, new TileState(Terrain.Dirt, SolidBlock.None, 9, 0)));
        var system = new ScentSystem(rig.World);
        for (int t = 0; t < 600; t++) system.Tick(rig.State, rig.Scent);
        float around = rig.State.Get(0, LayerId.Surface, 7, 4);
        Assert.True(around > 0, "냄새가 벽을 돌아 왼쪽에 도달해야 한다");

        int i = rig.SenseOne(6.5f, 4.5f, 0); // 센서 (7.5, 4.5): 벽 앞 열린 칸
        Assert.False(rig.Info(i).IsBlocked(Front));
        Assert.True(rig.Value(i, 0, Front) > 0);
        i = rig.SenseOne(7.5f, 4.5f, 0); // 센서 (8.5, 4.5): 벽
        Assert.True(rig.Info(i).IsBlocked(Front));
        Assert.True(rig.State.Get(0, LayerId.Surface, 9, 4) > around); // 벽 너머가 더 진하지만 읽지 않는다
    }

    // ---------- 14. 물, 맵 경계 ----------

    [Fact]
    public void Water_DoesNotBlockSensing()
    {
        var rig = Rig.Create(settings: AntSettings.Default with { SensorDistance = 2f });
        rig.Wet(12, 10);
        rig.Wet(11, 10);
        rig.Fill(0, (_, _) => 0.3f);
        int i = rig.SenseOne(10.5f, 10.5f, 0); // 경로 (11,10) 물, 센서 (12.5, 10.5) 물
        Assert.False(rig.Info(i).IsBlocked(Front));
        Assert.Equal(0.3f, rig.Value(i, 0, Front), 1e-6f);
    }

    [Fact]
    public void MapBoundary_BlocksOutside_AndPartialSamplesStayInBounds()
    {
        var rig = Rig.Create();
        rig.Fill(0, (_, _) => 0.6f);
        int i = rig.SenseOne(0.5f, 5.5f, MathF.PI); // 센서 (−0.5, 5.5): 맵 밖
        Assert.True(rig.Info(i).IsBlocked(Front));
        Assert.Equal(0f, rig.Value(i, 0, Front));

        i = rig.SenseOne(1.2f, 0.4f, MathF.PI); // 센서 (0.2, 0.4): 2×2 중 3칸이 맵 밖
        Assert.False(rig.Info(i).IsBlocked(Front));
        Assert.Equal(0.6f, rig.Value(i, 0, Front), 1e-6f);

        i = rig.SenseOne(15.5f, 15.5f, MathF.PI / 4); // 오른쪽 아래 모서리 밖
        Assert.True(rig.Info(i).IsBlocked(Front));
    }

    // ---------- 15. 감지 타이머 경계 ----------

    private static List<int> SenseTicks(float start, float dt, float period, int ticks)
    {
        var result = new List<int>();
        float timer = start;
        for (int t = 1; t <= ticks; t++)
        {
            if (AntSensing.AdvanceTimer(ref timer, dt, period)) result.Add(t);
            Assert.True(timer > 0 && timer <= period, $"틱 {t}: 타이머 {timer} ∉ (0, {period}]");
        }
        return result;
    }

    private static int[] Intervals(List<int> ticks) => ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToArray();

    [Fact]
    public void Timer_P03_Dt01_SensesEveryThreeTicks()
    {
        // float: 0.3 − 3·0.1 = 1.49e-8 > 0 이라 타이머 P에서 출발하면 4번째 틱에 첫 감지, 이후 3틱 간격.
        var ticks = SenseTicks(0.3f, 0.1f, 0.3f, 300);
        Assert.Equal(4, ticks[0]);
        Assert.All(Intervals(ticks), i => Assert.Equal(3, i));
    }

    [Fact]
    public void Timer_P025_Dt01_AlternatesTwoAndThree_NeverOne()
    {
        var intervals = Intervals(SenseTicks(0.25f, 0.1f, 0.25f, 1000));
        Assert.All(intervals, i => Assert.InRange(i, 2, 3));
        Assert.Equal(2.5, intervals.Average(), 2);
    }

    [Fact]
    public void Timer_Dt007_MixesFourAndFive()
    {
        var intervals = Intervals(SenseTicks(0.3f, 0.07f, 0.3f, 2000));
        Assert.All(intervals, i => Assert.InRange(i, 4, 5));
        Assert.Equal(0.3 / 0.07, intervals.Average(), 1);
    }

    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    [InlineData(10f)]
    public void Timer_DtAtLeastP_SensesOncePerTick(float dt)
    {
        var ticks = SenseTicks(0.3f, dt, 0.3f, 50);
        Assert.Equal(Enumerable.Range(1, 50), ticks);
    }

    [Fact]
    public void Timer_ExactlyZero_SchedulesTheNextSenseOnePeriodLater()
    {
        float timer = 0.1f;
        Assert.True(AntSensing.AdvanceTimer(ref timer, 0.1f, 0.3f)); // 0.1 − 0.1 = 0 → 감지
        Assert.Equal(0.3f, timer); // 다음 감지는 P 뒤(이전 식은 0에 머물러 다음 틱에 다시 감지했다)
        Assert.False(AntSensing.AdvanceTimer(ref timer, 0.1f, 0.3f));
    }

    [Theory]
    [InlineData(1e-9f, 0.1f, 0.3f)]
    [InlineData(0.3f, 10f, 1e-3f)]   // k ≈ 10,000
    [InlineData(0.3f, 10f, 1e-7f)]   // k ≈ 1e8 (double 계산 후 범위 방어)
    [InlineData(5e-8f, 1e-7f, 60f)]
    public void Timer_FloatBoundaries_StayInRange(float start, float dt, float period)
    {
        float timer = Math.Min(start, period);
        for (int t = 0; t < 100; t++)
        {
            AntSensing.AdvanceTimer(ref timer, dt, period);
            Assert.True(timer > 0 && timer <= period, $"{timer}");
        }
    }

    [Fact]
    public void Timer_TickIntervalChangesMidRun_KeepInvariant_AndNoDoubleSense()
    {
        float timer = AntSensing.InitialPhase(17, 0.3f);
        var dts = new[] { 0.1f, 0.05f, 0.5f, 0.07f, 10f, 0.013f };
        int lastSense = -10;
        for (int t = 0; t < 600; t++)
        {
            float dt = dts[(t / 40) % dts.Length];
            if (AntSensing.AdvanceTimer(ref timer, dt, 0.3f))
            {
                if (dt < 0.3f) Assert.True(t - lastSense >= 1);
                lastSense = t;
            }
            Assert.True(timer > 0 && timer <= 0.3f);
        }
    }

    [Fact]
    public void Session_TimersAlwaysInRange_AfterEveryTick_IncludingTickChange()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default, AntSettings.Default with { InitialCount = 200, Seed = 4 });
        for (int t = 0; t < 120; t++)
        {
            if (t == 40) Assert.Empty(session.TrySetSettings(session.Settings.WithTick(0.05)));
            if (t == 80) Assert.Empty(session.TrySetSettings(session.Settings.WithTick(0.15)));
            session.Step();
            Assert.Null(Serialization.ExperimentSerializer.ValidateStageB(session.Ants, session.Tick, 0.3f, session.World));
        }
    }

    // ---------- 16·17. 위상 분산, 감지 전 무효 ----------

    [Fact]
    public void InitialPhases_AreInRange_Deterministic_AndSpread()
    {
        var buckets = new int[10];
        for (int id = 0; id < 10_000; id++)
        {
            float phase = AntSensing.InitialPhase(id, 0.3f);
            Assert.True(phase > 0 && phase <= 0.3f);
            Assert.Equal(phase, AntSensing.InitialPhase(id, 0.3f));
            buckets[Math.Min(9, (int)(phase / 0.03f))]++;
        }
        output.WriteLine(string.Join(", ", buckets));
        Assert.All(buckets, b => Assert.InRange(b, 850, 1150));
        Assert.True(AntSensing.InitialPhase(0, 0.3f) > 0); // id 0도 0이 아니다(이전 식은 0)

        // 세션: 첫 틱에 감지하는 개미는 전체의 일부(약 1/3)이고, 4틱 안에 모두 감지한다.
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default, AntSettings.Default with { InitialCount = 600, Seed = 8 });
        session.Step();
        int first = Enumerable.Range(0, 600).Count(i => session.Ants.GetSense(i, new float[6]).HasSensed);
        Assert.InRange(first, 120, 280);
        session.Step(3);
        Assert.Equal(600, Enumerable.Range(0, 600).Count(i => session.Ants.GetSense(i, new float[6]).HasSensed));
    }

    [Fact]
    public void BeforeFirstSense_StateIsMarkedInvalid_AndHoldsNoValues()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default, AntSettings.Default with { InitialCount = 30 });
        var values = new float[6];
        for (int i = 0; i < 30; i++)
        {
            var info = session.Ants.GetSense(i, values);
            Assert.False(info.HasSensed);
            Assert.Equal(AntPopulation.NotSensed, info.SenseTick);
            Assert.Equal(0, info.Flags);
            Assert.All(values, v => Assert.Equal(0f, v));
            Assert.Equal(AntSensing.InitialPhase(session.Ants.Ids[i], 0.3f), info.SenseTimer);
        }
    }

    [Fact]
    public void BetweenSenses_LastValuesAreKept()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default, AntSettings.Default with { InitialCount = 40, Seed = 6 });
        session.Step(30);
        var before = new float[6];
        var after = new float[6];
        for (int t = 0; t < 30; t++)
        {
            var snapshot = Enumerable.Range(0, 40).Select(i => (session.Ants.GetSense(i, before), before.ToArray())).ToArray();
            session.Step();
            for (int i = 0; i < 40; i++)
            {
                var info = session.Ants.GetSense(i, after);
                if (info.SenseTick != session.Tick) // 이번 틱에 감지하지 않았다
                {
                    Assert.Equal(snapshot[i].Item1.SenseTick, info.SenseTick);
                    Assert.Equal(snapshot[i].Item2, after);
                }
            }
        }
    }

    // ---------- 18. 초기화 결정성 ----------

    [Fact]
    public void Reset_ReproducesTheFullState_IncludingSensing()
    {
        var session = new SimulationSession(StageAGoldenTests.NestWorld(), ScentSettings.Default, AntSettings.Default with { InitialCount = 300, Seed = 7 });
        session.Step(120);
        ulong full = session.ComputeStateHash(), stageB = session.Ants.ComputeStageBHash();
        session.Reset();
        session.Step(120);
        Assert.Equal(full, session.ComputeStateHash());
        Assert.Equal(stageB, session.Ants.ComputeStageBHash());
        var fresh = new SimulationSession(StageAGoldenTests.NestWorld(), ScentSettings.Default, AntSettings.Default with { InitialCount = 300, Seed = 7 });
        fresh.Step(120);
        Assert.Equal(stageB, fresh.Ants.ComputeStageBHash());
    }

    // ---------- 25. 할당 ----------

    [Fact]
    public void RepeatedTicks_DoNotAllocate()
    {
        var session = new SimulationSession(StageAGoldenTests.NestWorld(), EightTypes(),
            AntSettings.Default with { InitialCount = 2000, Seed = 3, SensedScentKeys = EightTypes().Types.Select(t => t.Key).ToArray(), SensorDistance = 2f });
        session.Step(100); // 예열(JIT 계층 컴파일·캐시)
        // 정상 상태 측정: 200틱 측정을 3번 해서 가장 작은 값을 쓴다(전체 테스트 병렬 실행 중 한 번 생기는 JIT 전환 등 일시 할당 제외).
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            session.Step(200);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        output.WriteLine($"200틱 할당 {allocated}바이트");
        Assert.True(allocated < 200 * 1024, $"틱당 1KB 이상 할당: {allocated}바이트/200틱");

        // 감각 패스 자체는 할당하지 않는다. (세션 틱의 나머지 할당은 B1 이전부터 있던 냄새 캐시 갱신의 열거자, 틱당 32바이트 —
        // main에서 같은 측정으로 확인. 범위 밖이라 그대로 둔다.)
        var masks = session.SenseOpenMasks();
        long senseOnly = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 200; t++) AntSensing.Sense(session.Ants, session.Scent, masks, session.Sensing, 0.1f, session.Tick + 1 + t);
            senseOnly = Math.Min(senseOnly, GC.GetAllocatedBytesForCurrentThread() - start);
        }
        Assert.Equal(0, senseOnly);
        Assert.True(allocated <= 200 * 32, $"감각 추가 후 틱당 할당이 기존(32바이트)보다 많음: {allocated}바이트/200틱");
    }

    // ---------- 최근접(내부 비교 경로) ----------

    [Fact]
    public void NearestSampling_ReadsTheSensorTile_WithTheSameOcclusion()
    {
        var rig = Rig.Create();
        rig.Fill(0, (x, _) => x);
        rig.Solid(12, 8);
        var config = AntSensingConfig.Create(rig.Settings, rig.Scent);
        var ants = new AntPopulation();
        ants.ConfigureSensing(2);
        Assert.True(AntSpawner.TrySpawn(ants, rig.World, rig.Settings, LayerId.Surface, 9.9f, 8.5f, out int i, heading: 0));
        ants.SenseTimerArray[i] = 0.01f;
        AntSensing.Sense(ants, rig.State, new ScentSystem(rig.World).OpenMasks(), config, 0.1f, 1, SenseSampling.Nearest);
        var values = new float[6];
        var info = ants.GetSense(i, values);
        Assert.Equal(10f, values[Front]); // 센서 (10.9, 8.5) → 칸 10
        Assert.False(info.IsBlocked(Front));
    }
}
