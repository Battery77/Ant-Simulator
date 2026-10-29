using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class DirtyTrackingTests
{
    private static readonly TileState Water = new(Terrain.Water, SolidBlock.None, 0, 0);

    [Fact]
    public void NewWorld_HasNoDirtyChunks()
    {
        var world = TestWorlds.Create(128, 128, chunkSize: 32);
        Assert.Equal(0, world.Surface.CollectDirtyChunks(new List<Chunk>()));
    }

    [Fact]
    public void Change_MarksOnlyContainingChunk_AndCollectClearsIt()
    {
        var layer = TestWorlds.Create(128, 128, chunkSize: 32).Surface;
        layer.TrySetState(40, 70, Water);

        var dirty = new List<Chunk>();
        Assert.Equal(1, layer.CollectDirtyChunks(dirty));
        Assert.Equal(new ChunkCoord(1, 2), dirty[0].Coord);

        dirty.Clear();
        Assert.Equal(0, layer.CollectDirtyChunks(dirty));
    }

    [Fact]
    public void SettingSameValue_IsNoOp()
    {
        var layer = TestWorlds.Create(64, 64).Surface;
        var chunk = layer.GetChunk(new ChunkCoord(0, 0));
        uint before = chunk.Version;
        layer.TrySetState(1, 1, new TileState(Terrain.Dirt, SolidBlock.None, 0, 0));
        Assert.Equal(before, chunk.Version);
        Assert.False(chunk.IsDirty);
    }

    [Fact]
    public void Version_IncrementsPerChange()
    {
        var layer = TestWorlds.Create(64, 64).Surface;
        var chunk = layer.GetChunk(new ChunkCoord(0, 0));
        layer.TrySetState(0, 0, Water);
        layer.TrySetState(0, 0, Water with { Food = 1 });
        layer.TrySetState(0, 0, Water with { Food = 1 });
        Assert.Equal(2u, chunk.Version);
    }

    [Fact]
    public void DirtyChunks_AreCollectedInRowMajorOrder()
    {
        var layer = TestWorlds.Create(128, 128, chunkSize: 32).Surface;
        layer.TrySetState(100, 100, Water); // (3,3)
        layer.TrySetState(0, 100, Water);   // (0,3)
        layer.TrySetState(100, 0, Water);   // (3,0)

        var dirty = new List<Chunk>();
        layer.CollectDirtyChunks(dirty);
        Assert.Equal(
            new[] { new ChunkCoord(3, 0), new ChunkCoord(0, 3), new ChunkCoord(3, 3) },
            dirty.Select(c => c.Coord));
    }

    [Fact]
    public void Layers_TrackDirtinessSeparately()
    {
        var world = TestWorlds.Create(64, 64);
        world.Nest.TrySetState(0, 0, Water);
        Assert.Equal(0, world.Surface.CollectDirtyChunks(new List<Chunk>()));
        Assert.Equal(1, world.Nest.CollectDirtyChunks(new List<Chunk>()));
    }
}
