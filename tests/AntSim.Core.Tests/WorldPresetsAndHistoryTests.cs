using AntSim.Core.Editing;
using AntSim.Core.Serialization;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class WorldPresetsTests
{
    [Fact]
    public void StandardNest_FillsNestWithSoil_SurfaceEmpty()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(100, 70, 32));
        for (int y = 0; y < 70; y++)
        for (int x = 0; x < 100; x++)
        {
            Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.None, 0, 0), world.Surface.GetState(x, y));
            Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.Soil, 0, 0), world.Nest.GetState(x, y));
        }
        Assert.Equal(0, world.Passages.Count);
        Assert.Equal(0, world.Nest.CollectDirtyChunks(new List<Chunk>()));
    }

    [Fact]
    public void Empty_KeepsBothLayersEmpty()
    {
        var world = WorldPresets.Empty(new WorldConfig(20, 20));
        Assert.Equal(SolidBlock.None, world.Nest.GetSolid(5, 5));
        Assert.Equal(new World(new WorldConfig(20, 20)).ComputeContentHash(), world.ComputeContentHash());
    }

    [Fact]
    public void StandardNest_RoundTripsThroughMapFile()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(40, 30));
        new MapEditor(world).Apply(EditOp.AddPassage(5, 5));
        using var stream = new MemoryStream();
        MapSerializer.Save(world, stream);
        stream.Position = 0;
        Assert.Equal(world.ComputeContentHash(), MapSerializer.Load(stream).ComputeContentHash());
    }

    [Fact]
    public void Clone_IsIndependentInBothDirections()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(40, 30, 16));
        var editor = new MapEditor(world);
        editor.Apply(EditOp.AddPassage(5, 5));
        var clone = world.Clone();
        Assert.Equal(world.ComputeContentHash(), clone.ComputeContentHash());
        Assert.Equal(0, clone.Nest.CollectDirtyChunks(new List<Chunk>()));

        ulong cloneHash = clone.ComputeContentHash();
        editor.Apply(EditOp.AddPassage(20, 20));
        Assert.Equal(cloneHash, clone.ComputeContentHash());

        ulong worldHash = world.ComputeContentHash();
        new MapEditor(clone).Apply(EditOp.PlaceSolid(LayerId.Surface, 1, 1, SolidBlock.Rock));
        clone.TrySetPassage(5, 5, false);
        Assert.Equal(worldHash, world.ComputeContentHash());
    }
}

public class EditHistoryLimitTests
{
    [Fact]
    public void DefaultCapacityIs256()
    {
        var editor = new MapEditor(TestWorlds.Create(300, 2));
        for (int x = 0; x < 300; x++) editor.Apply(EditOp.SetTerrain(LayerId.Surface, x, 0, Terrain.Water));
        Assert.Equal(256, editor.UndoCount);
    }

    [Fact]
    public void MemoryLimit_EvictsOldestRecords()
    {
        // 기록 하나 ≈ 128 + 24·칸 수. 100칸 스트로크 ≈ 2,528바이트 → 상한 6,000이면 2개만 남는다.
        var editor = new MapEditor(TestWorlds.Create(200, 10), historyCapacity: 256, historyMemoryLimitBytes: 6_000);
        for (int row = 0; row < 5; row++)
        {
            var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Water), LayerId.Surface, 0, row);
            stroke.MoveTo(99, row);
            stroke.End();
        }
        Assert.Equal(2, editor.UndoCount);
        Assert.True(editor.HistoryEstimatedBytes <= 6_000);

        while (editor.Undo()) { }
        Assert.Equal(Terrain.Water, editor.World.Surface.GetTerrain(0, 2)); // 버려진 기록은 되돌릴 수 없음
        Assert.Equal(Terrain.Dirt, editor.World.Surface.GetTerrain(0, 3));
    }

    [Fact]
    public void MostRecentRecord_IsKeptEvenIfOverLimit()
    {
        var editor = new MapEditor(TestWorlds.Create(200, 10), historyMemoryLimitBytes: 100);
        var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Water), LayerId.Surface, 0, 0);
        stroke.MoveTo(199, 0);
        stroke.End();
        Assert.Equal(1, editor.UndoCount);
        Assert.True(editor.Undo());
    }

    [Fact]
    public void EstimatedBytes_IsConstantAcrossUndoRedo_AndDropsWithRedoClear()
    {
        var editor = new MapEditor(TestWorlds.Create(20, 20));
        editor.Apply(EditOp.SetTerrain(LayerId.Surface, 0, 0, Terrain.Water));
        editor.Apply(EditOp.SetTerrain(LayerId.Surface, 1, 0, Terrain.Water));
        long bytes = editor.HistoryEstimatedBytes;
        editor.Undo();
        Assert.Equal(bytes, editor.HistoryEstimatedBytes);
        editor.Apply(EditOp.SetTerrain(LayerId.Surface, 2, 0, Terrain.Water)); // Redo 삭제
        Assert.Equal(bytes, editor.HistoryEstimatedBytes);
        Assert.False(editor.CanRedo);
    }
}
