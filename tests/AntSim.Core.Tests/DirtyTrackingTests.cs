using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class DirtyTrackingTests
{
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
        Assert.True(layer.SetTile(40, 70, 1));

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

        Assert.False(layer.SetTile(1, 1, 0));
        Assert.False(layer.SetBlock(1, 1, 0));
        Assert.Equal(before, chunk.Version);
        Assert.False(chunk.IsDirty);
    }

    [Fact]
    public void Version_IncrementsPerChange()
    {
        var layer = TestWorlds.Create(64, 64).Surface;
        var chunk = layer.GetChunk(new ChunkCoord(0, 0));
        layer.SetTile(0, 0, 1);
        layer.SetTile(0, 0, 2);
        layer.SetBlock(0, 0, 1);
        Assert.Equal(3u, chunk.Version);
    }

    [Fact]
    public void DirtyChunks_AreCollectedInRowMajorOrder()
    {
        var layer = TestWorlds.Create(128, 128, chunkSize: 32).Surface;
        layer.SetTile(100, 100, 1); // (3,3)
        layer.SetTile(0, 100, 1);   // (0,3)
        layer.SetTile(100, 0, 1);   // (3,0)

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
        world.Nest.SetTile(0, 0, 1);
        Assert.Equal(0, world.Surface.CollectDirtyChunks(new List<Chunk>()));
        Assert.Equal(1, world.Nest.CollectDirtyChunks(new List<Chunk>()));
    }
}
