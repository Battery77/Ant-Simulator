using AntSim.Core.Editing;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class MapEditorTests
{
    private const LayerId S = LayerId.Surface;
    private const LayerId N = LayerId.Nest;

    private static MapEditor NewEditor(int size = 64) => new(TestWorlds.Create(size, size));

    [Fact]
    public void SetTerrain_ChangesOnlyTargetLayer()
    {
        var editor = NewEditor();
        Assert.Equal(EditResult.Applied, editor.Apply(EditOp.SetTerrain(N, 3, 4, Terrain.Water)));
        Assert.Equal(Terrain.Water, editor.World.Nest.GetTerrain(3, 4));
        Assert.Equal(Terrain.Dirt, editor.World.Surface.GetTerrain(3, 4));
    }

    [Fact]
    public void PlaceSolid_OnEmptyTile_Succeeds_AndCanReplaceOtherSolid()
    {
        var editor = NewEditor();
        Assert.True(editor.Apply(EditOp.PlaceSolid(S, 1, 1, SolidBlock.Soil)).Changed);
        Assert.True(editor.Apply(EditOp.PlaceSolid(S, 1, 1, SolidBlock.Rock)).Changed);
        Assert.Equal(SolidBlock.Rock, editor.World.Surface.GetSolid(1, 1));
    }

    [Fact]
    public void PlaceSolid_OnTileWithQuantity_RemovesQuantities()
    {
        var editor = NewEditor();
        editor.Apply(EditOp.AdjustQuantity(S, 1, 1, QuantityKind.Larva, 1));
        Assert.True(editor.Apply(EditOp.PlaceSolid(S, 1, 1, SolidBlock.Soil)).Changed);
        Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.Soil, 0, 0), editor.World.Surface.GetState(1, 1));
    }

    [Fact]
    public void PlaceSolid_None_IsInvalid()
    {
        Assert.Equal(WorldError.InvalidSolid, NewEditor().Apply(EditOp.PlaceSolid(S, 1, 1, SolidBlock.None)).Error);
    }

    [Fact]
    public void Quantity_OnSolidTile_IsRejected()
    {
        var editor = NewEditor();
        editor.Apply(EditOp.PlaceSolid(S, 1, 1, SolidBlock.Rock));
        Assert.Equal(WorldError.SolidOccupied, editor.Apply(EditOp.AdjustQuantity(S, 1, 1, QuantityKind.Food, 1)).Error);
    }

    [Fact]
    public void Quantity_IncrementDecrement_RespectsLimits()
    {
        var editor = NewEditor();
        var layer = editor.World.Surface;
        Assert.True(editor.Apply(EditOp.AdjustQuantity(S, 2, 2, QuantityKind.Food, 6)).Succeeded);
        Assert.True(editor.Apply(EditOp.AdjustQuantity(S, 2, 2, QuantityKind.Larva, 4)).Succeeded);
        Assert.Equal(10, layer.GetState(2, 2).TotalQuantity);

        Assert.Equal(WorldError.QuantityLimitExceeded, editor.Apply(EditOp.AdjustQuantity(S, 2, 2, QuantityKind.Food, 1)).Error);
        Assert.Equal(WorldError.NegativeQuantity, editor.Apply(EditOp.AdjustQuantity(S, 2, 2, QuantityKind.Larva, -5)).Error);
        Assert.Equal(WorldError.QuantityLimitExceeded, editor.Apply(EditOp.SetQuantity(S, 2, 2, QuantityKind.Larva, 5)).Error);

        Assert.True(editor.Apply(EditOp.AdjustQuantity(S, 2, 2, QuantityKind.Food, -6)).Succeeded);
        Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.None, 0, 4), layer.GetState(2, 2));
    }

    [Fact]
    public void RemoveSolidAndClearBlocks()
    {
        var editor = NewEditor();
        editor.Apply(EditOp.PlaceSolid(S, 1, 1, SolidBlock.Soil));
        Assert.True(editor.Apply(EditOp.RemoveSolid(S, 1, 1)).Changed);
        Assert.Equal(EditResult.NoChange, editor.Apply(EditOp.RemoveSolid(S, 1, 1)));

        editor.Apply(EditOp.SetQuantity(S, 1, 1, QuantityKind.Food, 3));
        editor.Apply(EditOp.SetQuantity(S, 1, 1, QuantityKind.Larva, 2));
        Assert.True(editor.Apply(EditOp.ClearBlocks(S, 1, 1)).Changed);
        Assert.Equal(0, editor.World.Surface.GetState(1, 1).TotalQuantity);
    }

    [Fact]
    public void Passages_AddRemove_AreIdempotent()
    {
        var editor = NewEditor();
        Assert.Equal(EditResult.Applied, editor.Apply(EditOp.AddPassage(5, 5)));
        Assert.Equal(EditResult.NoChange, editor.Apply(EditOp.AddPassage(5, 5)));
        Assert.True(editor.World.Passages.Contains(5, 5));
        Assert.Equal(EditResult.Applied, editor.Apply(EditOp.RemovePassage(5, 5)));
        Assert.Equal(EditResult.NoChange, editor.Apply(EditOp.RemovePassage(5, 5)));
        Assert.Equal(2, editor.UndoCount);
    }

    [Fact]
    public void OutOfBounds_IsRejected()
    {
        var editor = NewEditor(10);
        Assert.Equal(WorldError.OutOfBounds, editor.Apply(EditOp.SetTerrain(S, 10, 0, Terrain.Water)).Error);
        Assert.Equal(WorldError.OutOfBounds, editor.Apply(EditOp.AddPassage(-1, 0)).Error);
        Assert.Equal(WorldError.InvalidLayer, editor.Apply(EditOp.SetTerrain((LayerId)5, 0, 0, Terrain.Water)).Error);
    }

    [Fact]
    public void Batch_IsAtomic_NothingChangesWhenAnyOpFails()
    {
        var editor = NewEditor();
        ulong before = editor.World.ComputeContentHash();
        var ops = new[]
        {
            EditOp.SetTerrain(S, 0, 0, Terrain.Water),
            EditOp.AddPassage(1, 1),
            EditOp.AdjustQuantity(N, 2, 2, QuantityKind.Food, 8),
            EditOp.AdjustQuantity(N, 2, 2, QuantityKind.Larva, 3), // 합계 11 → 실패
        };
        var result = editor.Apply(ops);
        Assert.Equal(WorldError.QuantityLimitExceeded, result.Error);
        Assert.Equal(3, result.FailedOpIndex);
        Assert.Equal(before, editor.World.ComputeContentHash());
        Assert.Equal(0, editor.UndoCount);
    }

    [Fact]
    public void Batch_OpsComposeSequentiallyOnSameTile()
    {
        var editor = NewEditor();
        editor.Apply(EditOp.PlaceSolid(S, 4, 4, SolidBlock.Rock));
        // 같은 배치 안에서 바위를 제거한 뒤 먹이를 놓는 것은 유효하다.
        var result = editor.Apply(new[]
        {
            EditOp.RemoveSolid(S, 4, 4),
            EditOp.SetQuantity(S, 4, 4, QuantityKind.Food, 10),
        });
        Assert.True(result.Succeeded);
        Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.None, 10, 0), editor.World.Surface.GetState(4, 4));
        Assert.True(editor.Undo());
        Assert.Equal(SolidBlock.Rock, editor.World.Surface.GetSolid(4, 4));
    }

    [Fact]
    public void Batch_ThatEndsUnchanged_RecordsNothing()
    {
        var editor = NewEditor();
        var result = editor.Apply(new[] { EditOp.AddPassage(1, 1), EditOp.RemovePassage(1, 1) });
        Assert.Equal(EditResult.NoChange, result);
        Assert.Equal(0, editor.UndoCount);
    }

    [Fact]
    public void UndoRedo_RestoresExactHashes()
    {
        var editor = NewEditor();
        var hashes = new List<ulong> { editor.World.ComputeContentHash() };
        editor.Apply(EditOp.SetTerrain(S, 1, 1, Terrain.Water)); hashes.Add(editor.World.ComputeContentHash());
        editor.Apply(EditOp.PlaceSolid(N, 1, 1, SolidBlock.Soil)); hashes.Add(editor.World.ComputeContentHash());
        editor.Apply(EditOp.AddPassage(2, 2)); hashes.Add(editor.World.ComputeContentHash());
        editor.Apply(EditOp.SetQuantity(S, 2, 2, QuantityKind.Food, 7)); hashes.Add(editor.World.ComputeContentHash());

        for (int i = hashes.Count - 2; i >= 0; i--)
        {
            Assert.True(editor.Undo());
            Assert.Equal(hashes[i], editor.World.ComputeContentHash());
        }
        Assert.False(editor.Undo());

        for (int i = 1; i < hashes.Count; i++)
        {
            Assert.True(editor.Redo());
            Assert.Equal(hashes[i], editor.World.ComputeContentHash());
        }
        Assert.False(editor.Redo());
    }

    [Fact]
    public void NewEdit_ClearsRedo()
    {
        var editor = NewEditor();
        editor.Apply(EditOp.SetTerrain(S, 1, 1, Terrain.Water));
        editor.Undo();
        Assert.True(editor.CanRedo);
        editor.Apply(EditOp.SetTerrain(S, 2, 2, Terrain.Water));
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void FailedOrNoOpEdit_DoesNotClearRedo()
    {
        var editor = NewEditor();
        editor.Apply(EditOp.SetTerrain(S, 1, 1, Terrain.Water));
        editor.Undo();
        editor.Apply(EditOp.AdjustQuantity(S, 1, 1, QuantityKind.Food, -1));
        editor.Apply(EditOp.SetTerrain(S, 1, 1, Terrain.Dirt));
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void History_DropsOldestBeyondCapacity()
    {
        var editor = new MapEditor(TestWorlds.Create(16, 16), historyCapacity: 3);
        for (int i = 0; i < 5; i++) editor.Apply(EditOp.SetTerrain(S, i, 0, Terrain.Water));
        Assert.Equal(3, editor.UndoCount);
        while (editor.Undo()) { }
        Assert.Equal(Terrain.Water, editor.World.Surface.GetTerrain(0, 0));
        Assert.Equal(Terrain.Water, editor.World.Surface.GetTerrain(1, 0));
        Assert.Equal(Terrain.Dirt, editor.World.Surface.GetTerrain(2, 0));
    }

    [Fact]
    public void Stroke_GroupsEditsIntoSingleUndoStep()
    {
        var editor = NewEditor();
        ulong before = editor.World.ComputeContentHash();
        editor.BeginStroke();
        for (int x = 0; x < 10; x++) editor.Apply(EditOp.SetTerrain(S, x, 0, Terrain.Water));
        editor.Apply(EditOp.AddPassage(3, 3));
        Assert.False(editor.CanUndo); // 스트로크 중에는 Undo 불가
        Assert.True(editor.EndStroke());
        ulong after = editor.World.ComputeContentHash();

        Assert.Equal(1, editor.UndoCount);
        editor.Undo();
        Assert.Equal(before, editor.World.ComputeContentHash());
        editor.Redo();
        Assert.Equal(after, editor.World.ComputeContentHash());
    }

    [Fact]
    public void Stroke_ThatReturnsToOriginal_RecordsNothing()
    {
        var editor = NewEditor();
        editor.BeginStroke();
        editor.Apply(EditOp.SetTerrain(S, 0, 0, Terrain.Water));
        editor.Apply(EditOp.SetTerrain(S, 0, 0, Terrain.Dirt));
        Assert.False(editor.EndStroke());
        Assert.Equal(0, editor.UndoCount);
    }

    [Fact]
    public void Stroke_MisuseThrows()
    {
        var editor = NewEditor();
        Assert.Throws<InvalidOperationException>(() => editor.EndStroke());
        editor.BeginStroke();
        Assert.Throws<InvalidOperationException>(() => editor.BeginStroke());
    }

    [Fact]
    public void UndoRedo_MarksAffectedChunksDirty()
    {
        var editor = new MapEditor(TestWorlds.Create(128, 128, chunkSize: 32));
        editor.Apply(EditOp.SetTerrain(N, 100, 5, Terrain.Water));
        editor.World.Nest.CollectDirtyChunks(new List<Chunk>());
        editor.Undo();
        var dirty = new List<Chunk>();
        editor.World.Nest.CollectDirtyChunks(dirty);
        Assert.Equal(new ChunkCoord(3, 0), Assert.Single(dirty).Coord);
    }
}
