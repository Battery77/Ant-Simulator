using AntSim.Core.Editing;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

/// <summary>3차 명세의 복합 편집 규칙: 고체 덮어쓰기, 물 자동 변환, 통로 굴착.</summary>
public class EditRulesTests
{
    private const LayerId S = LayerId.Surface;
    private const LayerId N = LayerId.Nest;

    private static MapEditor Empty(int size = 32) => new(WorldPresets.Empty(new WorldConfig(size, size)));
    private static MapEditor Standard(int size = 32) => new(WorldPresets.StandardNest(new WorldConfig(size, size)));

    // ---- 고체 블록 덮어쓰기 ----

    [Theory]
    [InlineData(SolidBlock.Soil)]
    [InlineData(SolidBlock.Rock)]
    public void PlaceSolid_ClearsFoodAndLarva_AndUndoRestoresThem(SolidBlock solid)
    {
        var editor = Empty();
        editor.Apply(EditOp.SetQuantity(S, 3, 3, QuantityKind.Food, 4));
        editor.Apply(EditOp.SetQuantity(S, 3, 3, QuantityKind.Larva, 6));
        int undoBefore = editor.UndoCount;

        Assert.Equal(EditResult.Applied, editor.Apply(EditOp.PlaceSolid(S, 3, 3, solid)));
        Assert.Equal(new TileState(Terrain.Dirt, solid, 0, 0), editor.World.Surface.GetState(3, 3));
        Assert.Equal(undoBefore + 1, editor.UndoCount); // 하나의 원자적 명령

        editor.Undo();
        Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.None, 4, 6), editor.World.Surface.GetState(3, 3));
    }

    [Fact]
    public void PlaceSolid_ReplacesExistingSolid_AndUndoRestoresIt()
    {
        var editor = Empty();
        editor.Apply(EditOp.PlaceSolid(N, 1, 1, SolidBlock.Soil));
        editor.Apply(EditOp.PlaceSolid(N, 1, 1, SolidBlock.Rock));
        Assert.Equal(SolidBlock.Rock, editor.World.Nest.GetSolid(1, 1));
        editor.Undo();
        Assert.Equal(SolidBlock.Soil, editor.World.Nest.GetSolid(1, 1));
    }

    [Fact]
    public void PlaceSolid_OnWater_ConvertsToDirt_AndUndoRestoresWater()
    {
        var editor = Empty();
        editor.Apply(EditOp.SetTerrain(S, 2, 2, Terrain.Water));
        editor.Apply(EditOp.SetQuantity(S, 2, 2, QuantityKind.Food, 2));

        editor.Apply(EditOp.PlaceSolid(S, 2, 2, SolidBlock.Rock));
        Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.Rock, 0, 0), editor.World.Surface.GetState(2, 2));

        editor.Undo();
        Assert.Equal(new TileState(Terrain.Water, SolidBlock.None, 2, 0), editor.World.Surface.GetState(2, 2));
    }

    [Fact]
    public void RemovingSolid_DoesNotRestoreWater()
    {
        var editor = Empty();
        editor.Apply(EditOp.SetTerrain(S, 2, 2, Terrain.Water));
        editor.Apply(EditOp.PlaceSolid(S, 2, 2, SolidBlock.Soil));
        editor.Apply(EditOp.RemoveSolid(S, 2, 2));
        Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.None, 0, 0), editor.World.Surface.GetState(2, 2));
    }

    [Fact]
    public void PlaceSolid_InStroke_UndoesAsOneStep()
    {
        var editor = Empty();
        for (int x = 0; x < 5; x++) editor.Apply(EditOp.SetQuantity(S, x, 0, QuantityKind.Food, (byte)(x + 1)));
        ulong before = editor.World.ComputeContentHash();
        var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.PlaceSolid, Solid: SolidBlock.Rock), S, 0, 0);
        stroke.MoveTo(4, 0);
        stroke.End();
        Assert.Equal(0, stroke.FailedCount);
        editor.Undo();
        Assert.Equal(before, editor.World.ComputeContentHash());
    }

    // ---- 통로 생성(굴착) ----

    [Fact]
    public void AddPassage_DigsNest3x3_AndConnectsCenter()
    {
        var editor = Standard();
        Assert.Equal(EditResult.Applied, editor.Apply(EditOp.AddPassage(10, 10)));

        Assert.True(editor.World.Passages.Contains(10, 10));
        Assert.Equal(1, editor.World.Passages.Count);
        for (int y = 8; y <= 12; y++)
        for (int x = 8; x <= 12; x++)
        {
            bool inside = Math.Abs(x - 10) <= 1 && Math.Abs(y - 10) <= 1;
            var expected = inside ? SolidBlock.None : SolidBlock.Soil;
            Assert.Equal(expected, editor.World.Nest.GetSolid(x, y));
        }
        Assert.Equal(SolidBlock.None, editor.World.Surface.GetSolid(10, 10));
    }

    [Fact]
    public void AddPassage_ConvertsNestAreaToDirt_AndRemovesRock()
    {
        var editor = Empty();
        editor.Apply(EditOp.SetTerrain(N, 9, 9, Terrain.Water));
        editor.Apply(EditOp.PlaceSolid(N, 11, 11, SolidBlock.Rock));
        editor.Apply(EditOp.SetTerrain(N, 11, 10, Terrain.Water));

        editor.Apply(EditOp.AddPassage(10, 10));
        for (int y = 9; y <= 11; y++)
        for (int x = 9; x <= 11; x++)
        {
            var state = editor.World.Nest.GetState(x, y);
            Assert.Equal(Terrain.Dirt, state.Terrain);
            Assert.Equal(SolidBlock.None, state.Solid);
        }
    }

    [Fact]
    public void AddPassage_KeepsQuantityBlocksInNestArea()
    {
        var editor = Empty();
        editor.Apply(EditOp.SetQuantity(N, 9, 10, QuantityKind.Larva, 5));
        editor.Apply(EditOp.AddPassage(10, 10));
        Assert.Equal(5, editor.World.Nest.GetQuantity(QuantityKind.Larva, 9, 10));
    }

    [Fact]
    public void AddPassage_OnSurfaceWater_ConvertsCenterToDirt_Only()
    {
        var editor = Standard();
        editor.Apply(EditOp.SetTerrain(S, 10, 10, Terrain.Water));
        editor.Apply(EditOp.SetTerrain(S, 11, 10, Terrain.Water));
        editor.Apply(EditOp.AddPassage(10, 10));
        Assert.Equal(Terrain.Dirt, editor.World.Surface.GetTerrain(10, 10));
        Assert.Equal(Terrain.Water, editor.World.Surface.GetTerrain(11, 10)); // 지상은 중심만
    }

    [Fact]
    public void AddPassage_KeepsSurfaceQuantities()
    {
        var editor = Standard();
        editor.Apply(EditOp.SetQuantity(S, 10, 10, QuantityKind.Food, 3));
        editor.Apply(EditOp.AddPassage(10, 10));
        Assert.Equal(3, editor.World.Surface.GetQuantity(QuantityKind.Food, 10, 10));
    }

    [Theory]
    [InlineData(SolidBlock.Soil)]
    [InlineData(SolidBlock.Rock)]
    public void AddPassage_OnSurfaceSolid_IsRejectedAtomically(SolidBlock solid)
    {
        var editor = Standard();
        editor.Apply(EditOp.PlaceSolid(S, 10, 10, solid));
        ulong before = editor.World.ComputeContentHash();
        int undo = editor.UndoCount;

        var result = editor.Apply(EditOp.AddPassage(10, 10));
        Assert.Equal(WorldError.PassageBlockedBySolid, result.Error);
        Assert.Equal(before, editor.World.ComputeContentHash()); // 개미집도 굴착되지 않음
        Assert.Equal(undo, editor.UndoCount);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(31, 10)]
    [InlineData(10, 0)]
    [InlineData(10, 31)]
    [InlineData(0, 0)]
    public void AddPassage_AtMapEdge_IsRejectedAtomically(int x, int y)
    {
        var editor = Standard();
        ulong before = editor.World.ComputeContentHash();
        Assert.Equal(WorldError.PassageAreaOutOfBounds, editor.Apply(EditOp.AddPassage(x, y)).Error);
        Assert.Equal(before, editor.World.ComputeContentHash());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    public void AddPassage_NextToEdge_FitsAndSucceeds(int x, int y)
    {
        Assert.True(Standard().Apply(EditOp.AddPassage(x, y)).Changed);
    }

    [Fact]
    public void AddPassage_Undo_RestoresAllTilesAndConnection()
    {
        var editor = Standard();
        editor.Apply(EditOp.SetTerrain(S, 10, 10, Terrain.Water));
        editor.Apply(EditOp.PlaceSolid(N, 10, 10, SolidBlock.Rock));
        ulong before = editor.World.ComputeContentHash();
        int undo = editor.UndoCount;

        editor.Apply(EditOp.AddPassage(10, 10));
        Assert.Equal(undo + 1, editor.UndoCount);
        ulong after = editor.World.ComputeContentHash();

        editor.Undo();
        Assert.Equal(before, editor.World.ComputeContentHash());
        Assert.False(editor.World.Passages.Contains(10, 10));
        Assert.Equal(SolidBlock.Rock, editor.World.Nest.GetSolid(10, 10));
        Assert.Equal(Terrain.Water, editor.World.Surface.GetTerrain(10, 10));

        editor.Redo();
        Assert.Equal(after, editor.World.ComputeContentHash());
    }

    [Fact]
    public void RemovePassage_RemovesConnectionOnly_AndUndoRestoresIt()
    {
        var editor = Standard();
        editor.Apply(EditOp.AddPassage(10, 10));
        editor.Apply(EditOp.RemovePassage(10, 10));

        Assert.False(editor.World.Passages.Contains(10, 10));
        Assert.Equal(SolidBlock.None, editor.World.Nest.GetSolid(10, 10)); // 굴착 공간은 남음
        Assert.Equal(SolidBlock.None, editor.World.Nest.GetSolid(9, 9));

        editor.Undo();
        Assert.True(editor.World.Passages.Contains(10, 10));
    }

    [Fact]
    public void AddPassage_OnExistingDugPassage_IsNoChange()
    {
        var editor = Standard();
        editor.Apply(EditOp.AddPassage(10, 10));
        Assert.Equal(EditResult.NoChange, editor.Apply(EditOp.AddPassage(10, 10)));
    }

    [Fact]
    public void AddPassage_ReDigsAreaRefilledAfterwards()
    {
        var editor = Standard();
        editor.Apply(EditOp.AddPassage(10, 10));
        editor.Apply(EditOp.PlaceSolid(N, 9, 9, SolidBlock.Rock));
        Assert.True(editor.Apply(EditOp.AddPassage(10, 10)).Changed);
        Assert.Equal(SolidBlock.None, editor.World.Nest.GetSolid(9, 9));
    }

    [Fact]
    public void OverlappingPassages_InOneStroke_UndoTogether()
    {
        var editor = Standard();
        ulong before = editor.World.ComputeContentHash();
        var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.AddPassage), S, 5, 5);
        stroke.MoveTo(9, 5);
        stroke.End();
        Assert.Equal(5, editor.World.Passages.Count);
        Assert.Equal(1, editor.UndoCount);
        editor.Undo();
        Assert.Equal(before, editor.World.ComputeContentHash());
    }

    [Fact]
    public void AllEditResults_SatisfyWorldRules()
    {
        // 다양한 복합 편집 후 모든 타일이 최종 불변 조건을 만족하는지 확인한다.
        var editor = Standard(24);
        var ops = new List<EditOp>();
        for (int i = 0; i < 400; i++)
        {
            int x = (i * 7) % 24, y = (i * 11) % 24;
            var layer = (LayerId)(i % 2);
            ops.Add((i % 6) switch
            {
                0 => EditOp.PlaceSolid(layer, x, y, (SolidBlock)(1 + i % 2)),
                1 => EditOp.AdjustQuantity(layer, x, y, QuantityKind.Food, 3),
                2 => EditOp.AdjustQuantity(layer, x, y, QuantityKind.Larva, 4),
                3 => EditOp.AddPassage(x, y),
                4 => EditOp.SetTerrain(layer, x, y, Terrain.Water),
                _ => EditOp.RemovePassage(x, y),
            });
        }
        foreach (var op in ops) editor.Apply(op);

        foreach (var layer in editor.World.Layers)
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++)
            Assert.Equal(WorldError.None, editor.World.Rules.Validate(layer.GetState(x, y)));
    }
}
