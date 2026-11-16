using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

/// <summary>
/// 냄새 저장 배치(docs/PERF_SCENT_DESIGN.md): 지상 F×F 묶음 격자, 개미집 열린 칸 압축.
/// </summary>
public class ScentGridTests
{
    private const int F = ScentGrid.SurfaceCellSize;
    private static readonly TileState Soil = new(Terrain.Dirt, SolidBlock.Soil, 0, 0);
    private static readonly TileState Open = new(Terrain.Dirt, SolidBlock.None, 0, 0);

    private static ScentSettings Single(double d = 0.6, double dp = 0.6, double k = 0, double e = 0, QuantityKind? source = null) =>
        new() { Types = new[] { new ScentTypeSettings("t", "t", source, d, dp, k, e) } };

    /// <summary>
    /// 원래 해상도 기준식(변경 전 ScentSystem의 전체 순회와 같은 순서: 확산 → 감쇠 → 발생, 이웃 왼쪽·오른쪽·위·아래). 통로 없음.
    /// </summary>
    private static void ReferenceTick(float[] cur, float[] next, byte[] open, byte[] quantity, int w, int h, float a, float decay, float emit)
    {
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (open[i] == 0)
            {
                next[i] = 0;
                continue;
            }
            float c = cur[i], flux = 0;
            if (x > 0 && open[i - 1] != 0) flux += cur[i - 1] - c;
            if (x < w - 1 && open[i + 1] != 0) flux += cur[i + 1] - c;
            if (y > 0 && open[i - w] != 0) flux += cur[i - w] - c;
            if (y < h - 1 && open[i + w] != 0) flux += cur[i + w] - c;
            next[i] = c + a * flux;
        }
        for (int i = 0; i < next.Length; i++) next[i] = MathF.Max(0f, next[i] * decay + emit * quantity[i]);
    }

    /// <summary>개미집 열린 칸 압축 계산은 원래 해상도 전체 순회와 비트 단위로 같다(판 칸만 계산할 뿐 식·순서가 같다).</summary>
    [Fact]
    public void NestSparse_IsBitIdenticalToFullResolutionReference()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(48, 40, 16));
        var editor = new MapEditor(world);
        // 불규칙한 굴: 방 두 개 + 구불구불한 복도 + 막다른 길
        for (int y = 5; y < 12; y++) for (int x = 4; x < 14; x++) editor.Apply(EditOp.RemoveSolid(LayerId.Nest, x, y));
        for (int y = 20; y < 31; y++) for (int x = 25; x < 40; x++) if ((x + y) % 7 != 0) editor.Apply(EditOp.RemoveSolid(LayerId.Nest, x, y));
        for (int t = 0; t < 30; t++) editor.Apply(EditOp.RemoveSolid(LayerId.Nest, 13 + t / 2, 11 + t / 3));
        editor.Apply(EditOp.SetQuantity(LayerId.Nest, 6, 7, QuantityKind.Food, 9));
        editor.Apply(EditOp.SetQuantity(LayerId.Nest, 30, 25, QuantityKind.Food, 4));

        var settings = Single(d: 0.55, dp: 0, k: 0.3, e: 1.7, source: QuantityKind.Food);
        var state = new ScentState(world, 1);
        var system = new ScentSystem(world);

        int w = world.Config.Width, h = world.Config.Height;
        var open = new byte[w * h];
        var quantity = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var s = world.Nest.GetState(x, y);
            open[y * w + x] = s.HasSolid ? (byte)0 : (byte)1;
            quantity[y * w + x] = s.Food;
        }
        var cur = new float[w * h];
        var next = new float[w * h];
        float a = (float)(0.55 * 0.1), decay = (float)Math.Exp(-0.3 * 0.1), emit = (float)(1.7 * 0.1);
        for (int tick = 0; tick < 200; tick++)
        {
            system.Tick(state, settings);
            ReferenceTick(cur, next, open, quantity, w, h, a, decay, emit);
            (cur, next) = (next, cur);
        }
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            Assert.True(BitConverter.SingleToInt32Bits(cur[y * w + x]) == BitConverter.SingleToInt32Bits(state.Get(0, LayerId.Nest, x, y)),
                $"({x},{y}) 기준 {cur[y * w + x]:R}, 압축 {state.Get(0, LayerId.Nest, x, y):R}");
        Assert.True(state.Get(0, LayerId.Nest, 6, 7) > 0);
        Assert.Equal(open.Count(o => o != 0), state.Grid(LayerId.Nest).Length);
    }

    /// <summary>방이 흙 1칸으로만 나뉘어 있어도 냄새가 섞이지 않는다(방·복도 구분 없이 칸 단위).</summary>
    [Fact]
    public void Nest_RoomsSeparatedByOneTileWall_DoNotMix()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(32, 16));
        var editor = new MapEditor(world);
        for (int y = 4; y < 10; y++)
        {
            for (int x = 4; x < 12; x++) editor.Apply(EditOp.RemoveSolid(LayerId.Nest, x, y)); // 방 A
            for (int x = 13; x < 21; x++) editor.Apply(EditOp.RemoveSolid(LayerId.Nest, x, y)); // 방 B (x = 12는 흙 벽)
        }
        editor.Apply(EditOp.SetQuantity(LayerId.Nest, 6, 6, QuantityKind.Larva, 8));
        var settings = ScentSettings.Default;
        var state = new ScentState(world, 2);
        var system = new ScentSystem(world);
        for (int t = 0; t < 1000; t++) system.Tick(state, settings);
        Assert.True(state.Get(1, LayerId.Nest, 11, 6) > 0);
        for (int y = 4; y < 10; y++)
        {
            Assert.Equal(-1, state.IndexOf(LayerId.Nest, 12, y));
            for (int x = 13; x < 21; x++) Assert.Equal(0f, state.Get(1, LayerId.Nest, x, y));
        }
    }

    /// <summary>땅을 파면 다음 틱에 배치가 바뀌고 기존 값은 자기 칸에 그대로 남으며, 새 칸은 0에서 확산을 받는다. 막은 칸의 값은 버린다.</summary>
    [Fact]
    public void Digging_UpdatesLayout_KeepsExistingValues()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(24, 24));
        var editor = new MapEditor(world);
        for (int x = 5; x < 10; x++) editor.Apply(EditOp.RemoveSolid(LayerId.Nest, x, 8));
        var settings = Single(d: 0); // 확산 없이 값 이동만 본다
        var state = new ScentState(world, 1);
        var system = new ScentSystem(world);
        for (int x = 5; x < 10; x++) Assert.True(state.Set(0, LayerId.Nest, x, 8, 10 + x));
        system.Tick(state, settings);
        Assert.Equal(5, state.Grid(LayerId.Nest).Length);

        editor.Apply(EditOp.RemoveSolid(LayerId.Nest, 3, 8)); // 앞쪽(행 우선 순서상 앞)에 새 칸 → 인덱스가 밀린다
        editor.Apply(EditOp.RemoveSolid(LayerId.Nest, 7, 9));
        editor.Apply(EditOp.PlaceSolid(LayerId.Nest, 9, 8, SolidBlock.Soil));
        system.Tick(state, settings);
        Assert.Equal(6, state.Grid(LayerId.Nest).Length);
        for (int x = 5; x < 9; x++) Assert.Equal(10f + x, state.Get(0, LayerId.Nest, x, 8));
        Assert.Equal(0f, state.Get(0, LayerId.Nest, 3, 8));
        Assert.Equal(0f, state.Get(0, LayerId.Nest, 7, 9));
        Assert.Equal(-1, state.IndexOf(LayerId.Nest, 9, 8));

        var diffusing = Single();
        system.Tick(state, diffusing);
        Assert.True(state.Get(0, LayerId.Nest, 7, 9) > 0); // 새 칸이 이웃에게서 받는다
    }

    /// <summary>지상 묶음 칸 사이 교환율은 D·dt/F²다.</summary>
    [Fact]
    public void Surface_CoarseDiffusion_UsesScaledCoefficient()
    {
        var world = TestWorlds.Create(5 * F, 5 * F);
        var state = new ScentState(world, 1);
        var system = new ScentSystem(world);
        Assert.True(state.Set(0, LayerId.Surface, 2 * F, 2 * F, 100)); // 묶음 칸 (2,2)
        system.Tick(state, Single(d: 0.6, dp: 0));
        float a = (float)(0.6 * 0.1 / (F * F));
        Assert.Equal(100 + a * (-4 * 100f), state.Get(0, LayerId.Surface, 2 * F, 2 * F));
        Assert.Equal(a * 100f, state.Get(0, LayerId.Surface, 3 * F, 2 * F));
        Assert.Equal(a * 100f, state.Get(0, LayerId.Surface, 2 * F + F - 1, 1 * F)); // 묶음 칸 (2,1)
        Assert.Equal(0f, state.Get(0, LayerId.Surface, 3 * F, 3 * F));       // 대각
        Assert.Equal(5, state.Grid(LayerId.Surface).Width);
        Assert.Equal(25, state.Grid(LayerId.Surface).Length);
    }

    /// <summary>F×F칸이 모두 고체인 묶음 칸은 닫혀 있다(0 유지, 교환 없음). 하나라도 열려 있으면 열린 묶음 칸이다.</summary>
    [Fact]
    public void Surface_FullySolidCellIsClosed_PartiallySolidCellIsOpen()
    {
        var world = TestWorlds.Create(3 * F, F);
        for (int y = 0; y < F; y++)
        for (int x = F; x < 2 * F; x++)
            Assert.Equal(WorldError.None, world.Surface.TrySetState(x, y, new TileState(Terrain.Dirt, SolidBlock.Rock, 0, 0)));
        var state = new ScentState(world, 1);
        var system = new ScentSystem(world);
        Assert.True(state.Set(0, LayerId.Surface, 0, 0, 50));
        for (int t = 0; t < 500; t++) system.Tick(state, Single());
        Assert.Equal(0f, state.Get(0, LayerId.Surface, F, 0));
        Assert.Equal(0f, state.Get(0, LayerId.Surface, 2 * F, 0)); // 닫힌 묶음 칸 너머로 넘어가지 않는다
        Assert.Equal(50f, state.Get(0, LayerId.Surface, 0, 0), 4);

        // 한 칸만 열면 그 묶음 칸이 열리고 냄새가 지나간다(지상의 얇은 벽은 냄새를 막지 못할 수 있다: 설계상 받아들인 대가).
        Assert.Equal(WorldError.None, world.Surface.TrySetState(F + 1, 1, new TileState(Terrain.Dirt, SolidBlock.None, 0, 0)));
        for (int t = 0; t < 2000; t++) system.Tick(state, Single());
        Assert.True(state.Get(0, LayerId.Surface, 2 * F, 0) > 0);
    }

    /// <summary>표시용 펼침: 지상은 묶음 격자 그대로, 개미집은 W·H(고체 칸 0).</summary>
    [Fact]
    public void ExpandTo_ProducesDisplayGrids()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(4 * F, 2 * F));
        new MapEditor(world).Apply(EditOp.RemoveSolid(LayerId.Nest, 5, 3));
        var state = new ScentState(world, 1);
        Assert.True(state.Set(0, LayerId.Nest, 5, 3, 7));
        Assert.True(state.Set(0, LayerId.Surface, 9, 1, 2));
        var nest = ScentDisplay.Of(state, 0, LayerId.Nest);
        Assert.Equal(world.Config.TileCount, nest.Length);
        Assert.Equal(7f, nest[3 * world.Config.Width + 5]);
        Assert.Equal(7f, nest.Sum());
        var surface = ScentDisplay.Of(state, 0, LayerId.Surface);
        Assert.Equal(4 * 2, surface.Length);
        Assert.Equal(2f, surface[0 * 4 + 2]);
    }

    /// <summary>지도 크기가 F의 배수가 아니어도 가장자리 묶음 칸이 생기고 범위 밖 접근이 없다.</summary>
    [Fact]
    public void Surface_NonMultipleSize_HasEdgeCells()
    {
        var world = TestWorlds.Create(4 * F + 3, 2 * F + 1);
        var state = new ScentState(world, 1);
        Assert.Equal(5, state.Grid(LayerId.Surface).Width);
        Assert.Equal(3, state.Grid(LayerId.Surface).Height);
        Assert.True(state.Set(0, LayerId.Surface, world.Config.Width - 1, world.Config.Height - 1, 3));
        var system = new ScentSystem(world);
        for (int t = 0; t < 50; t++) system.Tick(state, Single());
        Assert.True(state.AllValuesInSupportedRange());
        Assert.Equal(-1, state.IndexOf(LayerId.Surface, world.Config.Width, 0));
    }

    /// <summary>세션: 개미집 계산량이 판 칸 수에 비례한다(지도 크기와 무관한 저장 크기).</summary>
    [Fact]
    public void Session_NestStorage_IsProportionalToDugArea()
    {
        var small = new SimulationSession(WorldPresets.StandardNest(new WorldConfig(64, 64)), ScentSettings.Default);
        var big = new SimulationSession(WorldPresets.StandardNest(new WorldConfig(512, 512)), ScentSettings.Default);
        Assert.Equal(0, small.Scent.Grid(LayerId.Nest).Length);
        Assert.Equal(0, big.Scent.Grid(LayerId.Nest).Length);
        var world = WorldPresets.StandardNest(new WorldConfig(512, 512));
        new MapEditor(world).Apply(EditOp.AddPassage(100, 100));
        var dug = new SimulationSession(world, ScentSettings.Default);
        Assert.Equal(9, dug.Scent.Grid(LayerId.Nest).Length); // 통로 굴착 3×3
        Assert.Equal(128 * 128, dug.Scent.Grid(LayerId.Surface).Length);
    }
}
