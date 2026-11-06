using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

/// <summary>단계 A: 개미 이동·장애물·감속·통로.</summary>
public class AntMovementTests
{
    private const float Dt = 0.1f;
    private static readonly AntSettings Base = AntSettings.Default with { InitialCount = 0 };
    private static readonly TileState Empty = new(Terrain.Dirt, SolidBlock.None, 0, 0);

    private static World Open(int w = 60, int h = 60) => TestWorlds.Create(w, h);

    private static int Spawn(AntPopulation ants, World world, float x, float y, AntSettings? s = null,
        LayerId layer = LayerId.Surface, float? speed = null, float? size = null, float? heading = null)
    {
        Assert.True(AntSpawner.TrySpawn(ants, world, s ?? Base, layer, x, y, out int i, speed, size, heading));
        return i;
    }

    private static void AssertAllOnPassableTiles(AntPopulation ants, World world)
    {
        for (int i = 0; i < ants.Count; i++)
        {
            var ant = ants.Get(i);
            Assert.True(AntMovement.IsPassable(world.GetLayer(ant.Layer), ant.X, ant.Y),
                $"개미 {ant.Id}가 이동 불가 타일 ({ant.TileX},{ant.TileY}) {world.GetLayer(ant.Layer).GetState(ant.TileX, ant.TileY)}에 있음");
        }
    }

    // ---- 이동 ----

    [Fact]
    public void MovesContinuouslyOnDirt()
    {
        var world = Open();
        var ants = new AntPopulation();
        int i = Spawn(ants, world, 30.5f, 30.5f, speed: 3f, heading: 0);
        var system = new AntSystem();
        system.Tick(ants, world, Base with { WanderAngleDegrees = 0 }, Dt);
        var ant = ants.Get(i);
        Near.Equal(30.8f, ant.X, 4); // 3 타일/초 × 0.1초 = 0.3 타일(타일 단위로 뛰지 않음)
        Near.Equal(30.5f, ant.Y, 4);
        Near.Equal(30.5f, ants.PrevX[i], 4);
    }

    [Theory]
    [InlineData("water")]
    [InlineData("soil")]
    [InlineData("rock")]
    public void NeverEntersWaterSoilOrRock(string obstacle)
    {
        var world = Open(40, 40);
        var blocked = obstacle switch
        {
            "water" => Empty with { Terrain = Terrain.Water },
            "soil" => Empty with { Solid = SolidBlock.Soil },
            _ => Empty with { Solid = SolidBlock.Rock },
        };
        // 테두리 벽 + 안쪽 장애물(체크무늬 덩어리)
        for (int y = 0; y < 40; y++)
        for (int x = 0; x < 40; x++)
            if (x < 2 || y < 2 || x > 37 || y > 37 || ((x / 4 + y / 4) % 3 == 0 && x % 4 != 0))
                world.Surface.TrySetState(x, y, blocked);

        var ants = new AntPopulation();
        var settings = Base with { BaseSpeed = 8f, Seed = 7 };
        var rng = AntRandom.CreateState(3, 3);
        while (ants.Count < 150)
            AntSpawner.TrySpawn(ants, world, settings, LayerId.Surface, AntRandom.Range(ref rng, 2, 38), AntRandom.Range(ref rng, 2, 38), out _);

        var system = new AntSystem();
        for (int t = 0; t < 600; t++)
        {
            system.Tick(ants, world, settings, Dt);
            AssertAllOnPassableTiles(ants, world);
        }
    }

    [Fact]
    public void PassesThroughFoodAndLarvaTiles()
    {
        // 바위로 둘러싼 폭 1 복도를 먹이·유충으로 채운다. 직진하는 개미가 끝까지 지나간다.
        var world = Open(40, 5);
        for (int x = 0; x < 40; x++)
        {
            world.Surface.TrySetState(x, 1, Empty with { Solid = SolidBlock.Rock });
            world.Surface.TrySetState(x, 3, Empty with { Solid = SolidBlock.Rock });
            if (x > 2) world.Surface.TrySetState(x, 2, Empty with { Food = (byte)(x % 6), Larva = (byte)(x % 5) });
        }
        var ants = new AntPopulation();
        int i = Spawn(ants, world, 1.5f, 2.5f, heading: 0, speed: 3f);
        var settings = Base with { WanderAngleDegrees = 0 };
        var system = new AntSystem();
        for (int t = 0; t < 200 && ants.Get(i).X < 38; t++) system.Tick(ants, world, settings, Dt);
        Assert.True(ants.Get(i).X >= 38, $"복도 끝에 도달해야 함 (x = {ants.Get(i).X})");
    }

    [Theory]
    [InlineData(0, 1.00f)]
    [InlineData(1, 0.95f)]
    [InlineData(3, 0.95f)]
    [InlineData(4, 0.90f)]
    [InlineData(7, 0.90f)]
    [InlineData(8, 0.85f)]
    [InlineData(10, 0.85f)]
    public void SpeedDependsOnFoodPlusLarva(int quantity, float expected)
    {
        var world = Open(30, 30);
        for (int y = 0; y < 30; y++)
        for (int x = 0; x < 30; x++)
            world.Surface.TrySetState(x, y, Empty with { Food = (byte)(quantity / 2), Larva = (byte)(quantity - quantity / 2) });
        var ants = new AntPopulation();
        int i = Spawn(ants, world, 15.5f, 15.5f, speed: 2f);
        var system = new AntSystem();
        for (int t = 0; t < 30; t++) system.Tick(ants, world, Base, Dt);

        var ant = ants.Get(i);
        Near.Equal(expected, ant.SpeedFactor, 3);
        Assert.Equal(2f, ant.BaseSpeed);                 // 기본 속도는 바뀌지 않는다
        float moved = MathF.Sqrt(MathF.Pow(ant.X - ants.PrevX[i], 2) + MathF.Pow(ant.Y - ants.PrevY[i], 2));
        Near.Equal(2f * ant.SpeedFactor * Dt, moved, 3);
    }

    [Fact]
    public void Slowdown_ChangesSmoothlyAtTileBoundary_AndRecovers()
    {
        var world = Open(60, 5);
        for (int x = 20; x < 30; x++) world.Surface.TrySetState(x, 2, Empty with { Food = 10 });
        var ants = new AntPopulation();
        int i = Spawn(ants, world, 15.5f, 2.5f, heading: 0, speed: 3f);
        var settings = Base with { WanderAngleDegrees = 0 };
        var system = new AntSystem();
        var factors = new List<float>();
        for (int t = 0; t < 120 && ants.Get(i).X < 45; t++)
        {
            system.Tick(ants, world, settings, Dt);
            factors.Add(ants.Get(i).SpeedFactor);
        }
        Assert.Contains(factors, f => f > 0.86f && f < 0.99f);                  // 중간값을 거친다(계단식 아님)
        Assert.True(factors.Min() >= 0.85f - 1e-4f);
        Assert.True(factors.Min() < 0.87f, "먹이 구간 안에서 거의 0.85까지 감속");
        Assert.True(factors[^1] > 0.99f, "먹이 구간을 지나면 원래 속도로 복귀");
        for (int k = 1; k < factors.Count; k++) Assert.True(MathF.Abs(factors[k] - factors[k - 1]) < 0.1f);

        // 완화 시간 0이면 즉시 바뀐다.
        var instant = new AntPopulation();
        int j = Spawn(instant, world, 19.9f, 2.5f, heading: 0, speed: 3f);
        system.Tick(instant, world, settings with { SpeedFactorSmoothingSeconds = 0 }, Dt);
        system.Tick(instant, world, settings with { SpeedFactorSmoothingSeconds = 0 }, Dt);
        Near.Equal(0.85f, instant.Get(j).SpeedFactor, 5);
    }

    [Fact]
    public void BodySize_DoesNotAffectMovement()
    {
        World MakeWorld()
        {
            var w = Open(30, 30);
            for (int k = 0; k < 30; k++) w.Surface.TrySetState(k, (k * 7) % 30, Empty with { Solid = SolidBlock.Rock });
            return w;
        }
        var worldA = MakeWorld();
        var worldB = MakeWorld();
        var a = new AntPopulation();
        var b = new AntPopulation();
        Spawn(a, worldA, 15.5f, 16.5f, size: 0.3f, speed: 4f, heading: 1f);
        Spawn(b, worldB, 15.5f, 16.5f, size: 1.5f, speed: 4f, heading: 1f);
        var system = new AntSystem();
        for (int t = 0; t < 500; t++)
        {
            system.Tick(a, worldA, Base, Dt);
            system.Tick(b, worldB, Base, Dt);
            Assert.Equal(a.Get(0) with { Size = 0 }, b.Get(0) with { Size = 0 });
        }
    }

    [Fact]
    public void FastAnt_DoesNotTunnelThroughThinWall()
    {
        var world = Open(60, 20);
        for (int y = 0; y < 20; y++) world.Surface.TrySetState(30, y, Empty with { Solid = SolidBlock.Rock });
        var ants = new AntPopulation();
        int i = Spawn(ants, world, 20.5f, 10.5f, heading: 0, speed: 75f); // 한 틱에 7.5 타일
        var system = new AntSystem();
        var settings = Base with { WanderAngleDegrees = 0 };
        for (int t = 0; t < 200; t++)
        {
            system.Tick(ants, world, settings, Dt);
            Assert.True(ants.Get(i).X < 30, $"벽(x=30)을 넘으면 안 됨: x = {ants.Get(i).X}");
        }
    }

    [Fact]
    public void DiagonalMove_CannotSlipThroughCornerGap()
    {
        // (21,20)과 (20,21)이 바위: (20,20)에서 (21,21)로 모서리만 맞닿은 틈은 지나갈 수 없다.
        var world = Open(40, 40);
        world.Surface.TrySetState(21, 20, Empty with { Solid = SolidBlock.Rock });
        world.Surface.TrySetState(20, 21, Empty with { Solid = SolidBlock.Rock });
        var layer = world.Surface;
        float fraction = AntMovement.Sweep(layer, 20.5f, 20.5f, 2f, 2f, AntMobility.Walk, out bool blocked);
        Assert.True(blocked);
        Assert.True(20.5f + 2f * fraction < 21f);
    }

    [Fact]
    public void BlockedAnt_TurnsAwayInsteadOfPushingForever()
    {
        // 폭 1 막다른 복도 끝에서 출발해도 되돌아 나온다.
        var world = Open(40, 7);
        for (int x = 10; x < 40; x++)
        {
            world.Surface.TrySetState(x, 2, Empty with { Solid = SolidBlock.Rock });
            world.Surface.TrySetState(x, 4, Empty with { Solid = SolidBlock.Rock });
        }
        for (int y = 0; y < 7; y++) world.Surface.TrySetState(30, y, Empty with { Solid = SolidBlock.Rock });
        var ants = new AntPopulation();
        int i = Spawn(ants, world, 29.5f, 3.5f, heading: 0, speed: 3f);
        var system = new AntSystem();
        float minX = float.MaxValue;
        int stillTicks = 0;
        for (int t = 0; t < 400; t++)
        {
            system.Tick(ants, world, Base, Dt);
            var ant = ants.Get(i);
            minX = MathF.Min(minX, ant.X);
            if (ant.X == ants.PrevX[i] && ant.Y == ants.PrevY[i]) stillTicks++;
        }
        Assert.True(minX < 15, $"막다른 곳에서 빠져나와야 함 (최소 x = {minX})");
        Assert.True(stillTicks < 40, $"제자리 정지 틱이 너무 많음: {stillTicks}");
    }

    [Fact]
    public void Wander_PicksNewTargetEveryHalfToOneSecond_WithinThirtyDegrees()
    {
        var world = Open(400, 400);
        var ants = new AntPopulation();
        var settings = Base with { TurnIntervalVariation = 0 };
        int i = Spawn(ants, world, 200.5f, 200.5f, settings);
        var system = new AntSystem();
        int changes = 0;
        for (int t = 0; t < 100; t++) // 10초
        {
            float headingBefore = ants.Get(i).Heading, targetBefore = ants.Get(i).TargetHeading;
            system.Tick(ants, world, settings, Dt);
            float target = ants.Get(i).TargetHeading;
            if (target != targetBefore)
            {
                changes++;
                float offset = MathF.Abs(AntSystem.WrapAngle(target - headingBefore)) * 180 / MathF.PI;
                Assert.True(offset <= 30.001f, $"새 목표 방향은 현재 방향 ±30° 안: {offset}°");
            }
        }
        Assert.InRange(changes, 10, 21); // 0.5~1초 간격 → 10초에 10~20회(첫 결정 시점 차이 +1)
    }

    [Fact]
    public void Heading_TurnsSmoothlyTowardTarget()
    {
        var world = Open(400, 400);
        var ants = new AntPopulation();
        var settings = Base with { TurnRateDegreesPerSecond = 90 };
        int i = Spawn(ants, world, 200.5f, 200.5f, settings);
        var system = new AntSystem();
        for (int t = 0; t < 100; t++)
        {
            float before = ants.Get(i).Heading;
            system.Tick(ants, world, settings, Dt);
            float turned = MathF.Abs(AntSystem.WrapAngle(ants.Get(i).Heading - before)) * 180 / MathF.PI;
            Assert.True(turned <= 9.001f, $"한 틱 회전은 90°/초 × 0.1초 이하: {turned}");
        }
    }

    // ---- 통로 ----

    private static (World World, AntPopulation Ants, AntSystem System) PassageWorld()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(30, 30));
        new MapEditor(world).Apply(EditOp.AddPassage(10, 10));
        return (world, new AntPopulation(), new AntSystem());
    }

    [Fact]
    public void LayerChangesOnlyAtPassageCenter()
    {
        var (world, ants, system) = PassageWorld();
        var settings = Base with { WanderAngleDegrees = 0, PassageUseProbability = 1 };
        int center = Spawn(ants, world, 5.5f, 10.5f, settings, heading: 0, speed: 3f);  // 통로 중심 행
        int offRow = Spawn(ants, world, 5.5f, 11.5f, settings, heading: 0, speed: 3f);   // 바로 아래 행(굴착 영역이지만 통로 아님)
        int? switchedAtTileX = null;
        for (int t = 0; t < 40; t++)
        {
            system.Tick(ants, world, settings, Dt);
            var a = ants.Get(center);
            if (switchedAtTileX is null && a.Layer == LayerId.Nest) switchedAtTileX = a.TileX;
            Assert.Equal(LayerId.Surface, ants.Get(offRow).Layer);
        }
        Assert.Equal(10, switchedAtTileX);
        // 개미집 3×3 안에서는 고체가 없는 곳만 이동한다.
        for (int t = 0; t < 300; t++)
        {
            system.Tick(ants, world, settings with { WanderAngleDegrees = 30 }, Dt);
            AssertAllOnPassableTiles(ants, world);
        }
    }

    [Fact]
    public void NoLayerChange_WithoutPassage_OrWhenOtherSideIsSolid()
    {
        var (world, ants, system) = PassageWorld();
        new MapEditor(world).Apply(EditOp.PlaceSolid(LayerId.Nest, 10, 10, SolidBlock.Rock)); // 개미집 쪽 중심을 막음
        var settings = Base with { WanderAngleDegrees = 0, PassageUseProbability = 1 };
        int i = Spawn(ants, world, 5.5f, 10.5f, settings, heading: 0, speed: 3f);
        for (int t = 0; t < 60; t++) system.Tick(ants, world, settings, Dt);
        Assert.Equal(LayerId.Surface, ants.Get(i).Layer);
        Assert.True(ants.Get(i).X > 12, "지상에서는 계속 지나간다");

        var plain = TestWorlds.Create(30, 30);
        var other = new AntPopulation();
        int j = Spawn(other, plain, 5.5f, 10.5f, settings, heading: 0);
        for (int t = 0; t < 60; t++) system.Tick(other, plain, settings, Dt);
        Assert.Equal(LayerId.Surface, other.Get(j).Layer);
    }

    [Fact]
    public void PassageProbability_IsRespected()
    {
        var (world, ants, system) = PassageWorld();
        var settings = Base with { WanderAngleDegrees = 0, PassageUseProbability = 0 };
        int i = Spawn(ants, world, 5.5f, 10.5f, settings, heading: 0);
        for (int t = 0; t < 60; t++) system.Tick(ants, world, settings, Dt);
        Assert.Equal(LayerId.Surface, ants.Get(i).Layer);
    }

    // ---- 생성 ----

    [Theory]
    [InlineData(-1f, 5f)]
    [InlineData(5f, 40f)]
    [InlineData(float.NaN, 5f)]
    [InlineData(3.5f, 3.5f)] // 물
    [InlineData(4.5f, 3.5f)] // 바위
    [InlineData(5.5f, 3.5f)] // 토양
    public void Spawn_RejectsImpassablePositions(float x, float y)
    {
        var world = Open(30, 30);
        world.Surface.TrySetState(3, 3, Empty with { Terrain = Terrain.Water });
        world.Surface.TrySetState(4, 3, Empty with { Solid = SolidBlock.Rock });
        world.Surface.TrySetState(5, 3, Empty with { Solid = SolidBlock.Soil });
        var ants = new AntPopulation();
        Assert.False(AntSpawner.TrySpawn(ants, world, Base, LayerId.Surface, x, y, out _));
        Assert.Equal(0, ants.Count);
        Assert.True(AntSpawner.TrySpawn(ants, world, Base, LayerId.Surface, 6.5f, 3.5f, out _)); // 먹이 없는 흙: 가능
    }

    [Fact]
    public void InitialSpawn_UsesNestDigAreas_AndNeverInvalidTiles()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(64, 64));
        var editor = new MapEditor(world);
        editor.Apply(EditOp.AddPassage(10, 10));
        editor.Apply(EditOp.AddPassage(40, 30));
        var ants = new AntPopulation();
        var report = AntSpawner.SpawnInitial(ants, world, Base with { InitialCount = 300 });
        Assert.Equal(new AntSpawnReport(300, 300), report);
        for (int i = 0; i < ants.Count; i++)
        {
            var a = ants.Get(i);
            Assert.Equal(LayerId.Nest, a.Layer);
            Assert.True(AntMovement.IsPassable(world.Nest, a.X, a.Y));
            bool nearA = Math.Abs(a.TileX - 10) <= 1 && Math.Abs(a.TileY - 10) <= 1;
            bool nearB = Math.Abs(a.TileX - 40) <= 1 && Math.Abs(a.TileY - 30) <= 1;
            Assert.True(nearA || nearB);
        }
    }

    [Fact]
    public void InitialSpawn_WithNoOpenSpace_SpawnsNothing()
    {
        var world = TestWorlds.Create(40, 40);
        for (int y = 0; y < 40; y++)
        for (int x = 0; x < 40; x++)
            world.Surface.TrySetState(x, y, Empty with { Terrain = Terrain.Water });
        var ants = new AntPopulation();
        var report = AntSpawner.SpawnInitial(ants, world, Base with { InitialCount = 50 });
        Assert.Equal(0, report.Spawned);
        Assert.Equal(50, report.Failed);
        Assert.Equal(0, ants.Count);
    }

    // ---- 개체 차이·결정성 ----

    [Fact]
    public void IndividualSpeedAndSize_VaryWithinRange_Independently()
    {
        var world = TestWorlds.Create(200, 200);
        var ants = new AntPopulation();
        AntSpawner.SpawnInitial(ants, world, Base with { InitialCount = 2000, Seed = 99 });
        Assert.Equal(2000, ants.Count);
        var speeds = ants.BaseSpeed.ToArray();
        var sizes = ants.Size.ToArray();
        Assert.All(speeds, s => Assert.InRange(s, 3f * 0.8f, 3f * 1.2f));
        Assert.All(sizes, s => Assert.InRange(s, 0.8f * 0.85f, 0.8f * 1.15f));
        Assert.True(speeds.Max() - speeds.Min() > 1.0f, "속도 편차가 실제로 있어야 함");
        Assert.True(sizes.Max() - sizes.Min() > 0.2f, "크기 편차가 실제로 있어야 함");
        Assert.InRange(speeds.Average(), 2.9, 3.1);

        double ms = speeds.Average(), mz = sizes.Average();
        double cov = speeds.Zip(sizes, (s, z) => (s - ms) * (z - mz)).Sum();
        double r = cov / Math.Sqrt(speeds.Sum(s => (s - ms) * (s - ms)) * sizes.Sum(z => (z - mz) * (z - mz)));
        Assert.InRange(r, -0.1, 0.1); // 속도와 크기는 서로 독립
    }

    [Fact]
    public void SameSeed_SameResult_DifferentSeed_DifferentResult()
    {
        ulong Run(ulong seed)
        {
            var world = WorldPresets.StandardNest(new WorldConfig(64, 64));
            new MapEditor(world).Apply(EditOp.AddPassage(20, 20));
            var ants = new AntPopulation();
            var settings = Base with { InitialCount = 50, Seed = seed };
            AntSpawner.SpawnInitial(ants, world, settings);
            var system = new AntSystem();
            for (int t = 0; t < 300; t++) system.Tick(ants, world, settings, Dt);
            return ants.ComputeHash();
        }
        Assert.Equal(Run(5), Run(5));
        Assert.NotEqual(Run(5), Run(6));
    }

    [Fact]
    public void Settings_Validation()
    {
        Assert.Empty(AntSettings.Default.Validate());
        Assert.NotEmpty((AntSettings.Default with { BaseSpeed = 0 }).Validate());
        Assert.NotEmpty((AntSettings.Default with { SpeedVariation = 1.5f }).Validate());
        Assert.NotEmpty((AntSettings.Default with { InitialCount = -1 }).Validate());
        Assert.NotEmpty((AntSettings.Default with { TurnIntervalMax = 0.1f }).Validate());
        Assert.NotEmpty((AntSettings.Default with { PassageUseProbability = 2 }).Validate());
        Assert.NotEmpty((AntSettings.Default with { QuantitySpeedTiers = new[] { new SpeedTier(1, 0.9f) } }).Validate());
        Assert.NotEmpty((AntSettings.Default with { QuantitySpeedTiers = new[] { new SpeedTier(0, 1f), new SpeedTier(5, 1.5f) } }).Validate());
        Assert.Equal(1.00f, AntSettings.Default.SpeedFactorFor(0));
        Assert.Equal(0.85f, AntSettings.Default.SpeedFactorFor(10));
    }

    [Fact]
    public void Random_IsDeterministicAndInRange()
    {
        ulong a = AntRandom.CreateState(1, 2), b = AntRandom.CreateState(1, 2), c = AntRandom.CreateState(1, 3);
        Assert.Equal(AntRandom.NextULong(ref a), AntRandom.NextULong(ref b));
        Assert.NotEqual(a, c);
        for (int k = 0; k < 10000; k++)
        {
            float f = AntRandom.NextFloat(ref a);
            Assert.InRange(f, 0f, 0.99999994f);
            Assert.InRange(AntRandom.NextInt(ref a, 7), 0, 6);
        }
    }
}
