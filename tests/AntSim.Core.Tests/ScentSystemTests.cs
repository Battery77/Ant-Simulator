using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class ScentSystemTests
{
    private const LayerId S = LayerId.Surface;
    private const LayerId N = LayerId.Nest;

    /// <summary>테스트용 단일 냄새 종류 설정(발생원 없음).</summary>
    private static ScentSettings Single(double d = 0.6, double dp = 0.6, double k = 0, double e = 0, double dt = 0.1, QuantityKind? source = null) =>
        new() { TickSeconds = dt, Types = new[] { new ScentTypeSettings("test", "테스트", source, d, dp, k, e) } };

    private static (World world, ScentSystem system, ScentState state) Setup(int w, int h, ScentSettings settings, int chunk = 64)
    {
        var world = TestWorlds.Create(w, h, chunk);
        return (world, new ScentSystem(world), new ScentState(world.Config, settings.Types.Count));
    }

    private static void Put(ScentState state, LayerId layer, int x, int y, float value) =>
        state.MutableField(0, layer)[state.Config.ToLinearIndex(x, y)] = value;

    // ---- 설정 검증 ----

    [Fact]
    public void DefaultSettings_MatchSpecAndAreValid()
    {
        var settings = ScentSettings.Default;
        Assert.Empty(settings.Validate());
        Assert.Equal(0.1, settings.TickSeconds);
        Assert.Equal(new[] { "food", "larva" }, settings.Types.Select(t => t.Key));
        foreach (var type in settings.Types)
        {
            Assert.Equal(0.6, type.Diffusion);
            Assert.Equal(0.6, type.PassageDiffusion);
            Assert.Equal(0.025, type.Decay);
            Assert.Equal(1.0, type.Emission);
        }
        Assert.Equal(QuantityKind.Food, settings.Types[0].Source);
        Assert.Equal(QuantityKind.Larva, settings.Types[1].Source);
        Assert.Equal(0.30, settings.Types[0].MaxOutflowFraction(0.1), 12);
    }

    [Theory]
    [InlineData(0.1, true)]
    [InlineData(1.0 / 3.0, true)]   // (4·0.6 + 0.6)·dt = 3·dt = 1 → 경계값 허용
    [InlineData(0.34, false)]
    [InlineData(1.0, false)]
    [InlineData(0, false)]
    [InlineData(-0.1, false)]
    [InlineData(double.NaN, false)]
    public void TickChange_IsValidatedAgainstStability(double dt, bool valid)
    {
        var errors = ScentSettings.Default.WithTick(dt).Validate();
        Assert.Equal(valid, errors.Count == 0);
        if (!valid && dt > 0.34 - 1e-9 && double.IsFinite(dt)) Assert.Contains(errors, e => e.Contains("안정성"));
    }

    [Fact]
    public void InvalidCoefficientsAndKeys_AreRejected()
    {
        var bad = new ScentSettings
        {
            Types = new[]
            {
                new ScentTypeSettings("a", "A", null, Diffusion: -1),
                new ScentTypeSettings("a", "A2", null, Decay: double.PositiveInfinity),
                new ScentTypeSettings("", "빈 키", null, Emission: -0.5),
            },
        };
        var errors = bad.Validate();
        Assert.Contains(errors, e => e.Contains("확산 계수"));
        Assert.Contains(errors, e => e.Contains("중복"));
        Assert.Contains(errors, e => e.Contains("감쇠 계수"));
        Assert.Contains(errors, e => e.Contains("발생 계수"));
        Assert.Contains(errors, e => e.Contains("키가 비어"));
        Assert.NotEmpty(new ScentSettings().Validate()); // 종류 0개
    }

    // ---- 확산 ----

    [Fact]
    public void Diffusion_FromPoint_IsSymmetric_AndMatchesFormula()
    {
        var settings = Single();
        var (_, system, state) = Setup(9, 9, settings);
        Put(state, S, 4, 4, 100);
        system.Tick(state, settings);

        Near.Equal(100f - 4 * 0.06f * 100f, state.Get(0, S, 4, 4), 4);
        foreach (var (x, y) in new[] { (3, 4), (5, 4), (4, 3), (4, 5) })
            Near.Equal(6f, state.Get(0, S, x, y), 4);
        Assert.Equal(0f, state.Get(0, S, 3, 3));

        for (int i = 0; i < 20; i++) system.Tick(state, settings);
        for (int y = 0; y < 9; y++)
        for (int x = 0; x < 9; x++)
        {
            // 물리적으로 대칭이지만 이웃 합산 순서가 달라 float 마지막 자리는 다를 수 있다.
            float v = state.Get(0, S, x, y);
            Assert.True(Math.Abs(v - state.Get(0, S, 8 - x, y)) <= 1e-6f * Math.Max(1f, v));
            Assert.True(Math.Abs(v - state.Get(0, S, y, x)) <= 1e-6f * Math.Max(1f, v));
        }
    }

    [Fact]
    public void ClosedMap_WithoutDecayOrEmission_ConservesTotal()
    {
        var settings = Single();
        var (world, system, state) = Setup(40, 30, settings);
        world.Surface.TrySetState(10, 10, new TileState(Terrain.Dirt, SolidBlock.Rock, 0, 0));
        Put(state, S, 0, 0, 500);   // 모서리: 경계 밖으로 새지 않아야 함
        Put(state, S, 39, 15, 250);
        Put(state, N, 20, 29, 125);
        double before = state.Total(0);
        for (int i = 0; i < 200; i++) system.Tick(state, settings);
        Assert.Equal(before, state.Total(0), 2);
    }

    [Fact]
    public void Solids_BlockScentCompletely()
    {
        // 바위 벽(x = 5)으로 나뉜 맵: 오른쪽으로는 냄새가 넘어가지 않는다.
        var settings = Single();
        var (world, system, state) = Setup(11, 5, settings);
        for (int y = 0; y < 5; y++) world.Surface.TrySetState(5, y, new TileState(Terrain.Dirt, SolidBlock.Rock, 0, 0));
        Put(state, S, 2, 2, 100);
        for (int i = 0; i < 500; i++) system.Tick(state, settings);

        for (int y = 0; y < 5; y++)
        {
            Assert.Equal(0f, state.Get(0, S, 5, y));
            for (int x = 6; x < 11; x++) Assert.Equal(0f, state.Get(0, S, x, y));
        }
        Assert.Equal(100, state.Total(0), 2);
    }

    [Fact]
    public void ScentOnTileThatBecomesSolid_IsRemoved()
    {
        var settings = Single();
        var (world, system, state) = Setup(5, 5, settings);
        Put(state, S, 2, 2, 50);
        world.Surface.TrySetState(2, 2, new TileState(Terrain.Dirt, SolidBlock.Soil, 0, 0));
        system.Tick(state, settings);
        Assert.Equal(0f, state.Get(0, S, 2, 2));
        Assert.Equal(0f, state.Get(0, S, 1, 2));
    }

    [Fact]
    public void DirtAndWater_DiffuseIdentically()
    {
        var settings = Single();
        var (worldA, systemA, a) = Setup(15, 15, settings);
        var (worldB, systemB, b) = Setup(15, 15, settings);
        for (int y = 0; y < 15; y++)
        for (int x = 0; x < 15; x += 2)
            worldB.Surface.TrySetState(x, y, new TileState(Terrain.Water, SolidBlock.None, 0, 0));
        Put(a, S, 7, 7, 10);
        Put(b, S, 7, 7, 10);
        for (int i = 0; i < 30; i++)
        {
            systemA.Tick(a, settings);
            systemB.Tick(b, settings);
        }
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Results_AreBitIdentical_AcrossChunkSizes()
    {
        var settings = ScentSettings.Default;
        ScentState Run(int chunk)
        {
            var world = TestWorlds.Create(150, 90, chunk);
            TestWorlds.Paint(world);
            var system = new ScentSystem(world);
            var state = new ScentState(world.Config, settings.Types.Count);
            for (int i = 0; i < 25; i++) system.Tick(state, settings);
            return state;
        }
        Assert.Equal(Run(16).ComputeHash(), Run(128).ComputeHash());
    }

    [Fact]
    public void Concentrations_StayNonNegative_AtStabilityLimit()
    {
        // (4·D + Dp)·dt = 1 정확히 경계값, 체커보드 초기 조건(가장 진동하기 쉬운 경우).
        var settings = Single(d: 0.6, dp: 0.6, dt: 1.0 / 3.0);
        Assert.Empty(settings.Validate());
        var (world, system, state) = Setup(16, 16, settings);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            Put(state, (x + y) % 2 == 0 ? S : N, x, y, (x + y) % 2 == 0 ? 1000f : 0f);
            if ((x * 3 + y) % 5 == 0) world.TrySetPassage(x, y, true);
        }
        for (int i = 0; i < 100; i++)
        {
            system.Tick(state, settings);
            foreach (var layer in new[] { S, N })
                foreach (float v in state.Field(0, layer)) Assert.True(v >= 0, $"음수 농도 {v}");
        }
    }

    // ---- 감쇠·발생·순서 ----

    [Fact]
    public void Decay_IsExponential()
    {
        var settings = Single(d: 0, dp: 0, k: 0.025);
        var (_, system, state) = Setup(3, 3, settings);
        Put(state, S, 1, 1, 100);
        for (int i = 0; i < 10; i++) system.Tick(state, settings);
        float factor = (float)Math.Exp(-0.025 * 0.1);
        float expected = 100;
        for (int i = 0; i < 10; i++) expected *= factor;
        Assert.Equal(expected, state.Get(0, S, 1, 1));
        Assert.Equal(100 * Math.Exp(-0.025), state.Get(0, S, 1, 1), 3); // 1초 후
    }

    [Fact]
    public void Emission_IsProportionalToQuantity_PerSourceKind()
    {
        var settings = ScentSettings.Default;
        var world = TestWorlds.Create(20, 20);
        world.Surface.TrySetState(3, 3, new TileState(Terrain.Dirt, SolidBlock.None, 2, 0));
        world.Surface.TrySetState(10, 10, new TileState(Terrain.Dirt, SolidBlock.None, 7, 3));
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 2);
        system.Tick(state, settings); // 0에서 시작: 확산·감쇠 후 0, 발생만 남는다

        Near.Equal(1.0f * 0.1f * 2, state.Get(0, S, 3, 3), 6);
        Near.Equal(1.0f * 0.1f * 7, state.Get(0, S, 10, 10), 6);
        Near.Equal(1.0f * 0.1f * 3, state.Get(1, S, 10, 10), 6);
        Assert.Equal(0f, state.Get(1, S, 3, 3));
        Assert.Equal(0f, state.Get(0, N, 10, 10));
    }

    [Fact]
    public void TickOrder_IsDiffusionThenDecayThenEmission()
    {
        // 발생원 칸(수량 1)에 초기 농도 10. 기대값 = (10·(1 − 4a))·exp(−k·dt) + e·dt·1
        var settings = Single(d: 0.6, dp: 0, k: 0.5, e: 2.0, source: QuantityKind.Food);
        var (world, system, state) = Setup(5, 5, settings);
        world.Surface.TrySetState(2, 2, new TileState(Terrain.Dirt, SolidBlock.None, 1, 0));
        Put(state, S, 2, 2, 10);
        system.Tick(state, settings);

        float a = (float)(0.6 * 0.1), decay = (float)Math.Exp(-0.5 * 0.1), emit = (float)(2.0 * 0.1);
        float expectedCenter = (10 + a * (-4 * 10f)) * decay + emit * 1;
        float expectedNeighbor = (0 + a * 10f) * decay;
        Assert.Equal(expectedCenter, state.Get(0, S, 2, 2));
        Assert.Equal(expectedNeighbor, state.Get(0, S, 1, 2));
    }

    [Fact]
    public void SteadyState_IsReachedWithContinuousEmission()
    {
        var settings = ScentSettings.Default;
        var world = TestWorlds.Create(30, 30);
        world.Surface.TrySetState(15, 15, new TileState(Terrain.Dirt, SolidBlock.None, 10, 0));
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 2);
        for (int i = 0; i < 3000; i++) system.Tick(state, settings);
        double total = state.Total(0);
        system.Tick(state, settings);
        // 밀폐 맵 정상 상태: 발생량 = 감쇠량 → 총량 ≈ e·q / k = 10 / 0.025 = 400
        Assert.Equal(400, total, 0);
        Assert.Equal(total, state.Total(0), 1);
    }

    // ---- 레이어 간 이동 ----

    [Fact]
    public void WithoutPassage_LayersNeverExchange()
    {
        var settings = Single();
        var (_, system, state) = Setup(10, 10, settings);
        Put(state, S, 5, 5, 100);
        for (int i = 0; i < 100; i++) system.Tick(state, settings);
        Assert.All(state.Field(0, N).ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Passage_ExchangesOnlyAtCenter()
    {
        var settings = Single(d: 0, dp: 0.6); // 레이어 내 확산을 꺼서 통로 교환만 관찰
        var (world, system, state) = Setup(10, 10, settings);
        new MapEditor(world).Apply(EditOp.AddPassage(5, 5));
        Put(state, S, 5, 5, 100);
        Put(state, S, 4, 5, 100); // 굴착된 3×3 안이지만 중심이 아님
        system.Tick(state, settings);

        Near.Equal(100 - 6f, state.Get(0, S, 5, 5), 4);
        Near.Equal(6f, state.Get(0, N, 5, 5), 4);
        Assert.Equal(0f, state.Get(0, N, 4, 5));
        Assert.Equal(100f, state.Get(0, S, 4, 5));
    }

    [Fact]
    public void Passage_ConservesTotalAndEqualizes()
    {
        var settings = Single();
        var world = WorldPresets.StandardNest(new WorldConfig(12, 12));
        new MapEditor(world).Apply(EditOp.AddPassage(6, 6));
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 1);
        Put(state, S, 6, 6, 90);
        for (int i = 0; i < 2000; i++) system.Tick(state, settings);
        Assert.Equal(90, state.Total(0), 2);
        // 개미집은 3×3(9칸)만 열려 있으므로 평형에서 지상 144칸 + 개미집 9칸에 고르게 퍼진다.
        Assert.Equal(90.0 / 153, state.Get(0, N, 5, 5), 2);
        Assert.Equal(90.0 / 153, state.Get(0, S, 0, 0), 2);
        Assert.Equal(0f, state.Get(0, N, 4, 4)); // 토양
    }

    [Fact]
    public void Passage_WithSolidOnEitherSide_IsBlocked()
    {
        var settings = Single(d: 0);
        var (world, system, state) = Setup(10, 10, settings);
        var editor = new MapEditor(world);
        editor.Apply(EditOp.AddPassage(5, 5));
        editor.Apply(EditOp.PlaceSolid(N, 5, 5, SolidBlock.Rock));
        Put(state, S, 5, 5, 100);
        system.Tick(state, settings);
        Assert.Equal(100f, state.Get(0, S, 5, 5));
        Assert.Equal(0f, state.Get(0, N, 5, 5));
    }

    [Fact]
    public void WorldChangesBetweenTicks_AreSeenByCache()
    {
        var settings = ScentSettings.Default;
        var world = TestWorlds.Create(70, 70); // 청크 2×2
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 2);
        system.Tick(state, settings);
        world.Nest.TrySetState(66, 66, new TileState(Terrain.Dirt, SolidBlock.None, 0, 4));
        system.Tick(state, settings);
        Near.Equal(0.4f, state.Get(1, N, 66, 66), 5);
    }

    [Fact]
    public void NewScentType_CanBeAddedWithoutCodeChanges()
    {
        var settings = ScentSettings.Default with
        {
            Types = ScentSettings.Default.Types.Append(new ScentTypeSettings("alarm", "경보", null, Decay: 1.0)).ToArray(),
        };
        Assert.Empty(settings.Validate());
        var world = TestWorlds.Create(10, 10);
        world.Surface.TrySetState(1, 1, new TileState(Terrain.Dirt, SolidBlock.None, 5, 0));
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 3);
        state.MutableField(2, S)[world.Config.ToLinearIndex(5, 5)] = 10;
        system.Tick(state, settings);
        Assert.True(state.Get(0, S, 1, 1) > 0);
        Assert.True(state.Get(2, S, 5, 5) > 0 && state.Get(2, S, 5, 5) < 10);
        Assert.Equal(0f, state.Get(2, S, 1, 1));
    }
}
