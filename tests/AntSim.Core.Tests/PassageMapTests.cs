using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class PassageMapTests
{
    [Fact]
    public void AddRemove_TracksCountAndContains()
    {
        var world = TestWorlds.Create(100, 100);
        Assert.Equal(WorldError.None, world.TrySetPassage(10, 20, true));
        Assert.Equal(WorldError.None, world.TrySetPassage(10, 20, true)); // 중복 추가는 변경 없음
        Assert.True(world.Passages.Contains(10, 20));
        Assert.False(world.Passages.Contains(20, 10));
        Assert.Equal(1, world.Passages.Count);

        world.TrySetPassage(10, 20, false);
        Assert.False(world.Passages.Contains(10, 20));
        Assert.Equal(0, world.Passages.Count);
    }

    [Fact]
    public void OutOfBounds_IsRejected()
    {
        var world = TestWorlds.Create(10, 10);
        Assert.Equal(WorldError.OutOfBounds, world.TrySetPassage(10, 0, true));
        Assert.False(world.Passages.Contains(-1, 0));
    }

    [Fact]
    public void Enumeration_IsInAscendingLinearOrder()
    {
        var world = TestWorlds.Create(100, 100);
        world.TrySetPassage(99, 99, true);
        world.TrySetPassage(0, 0, true);
        world.TrySetPassage(70, 1, true);
        world.TrySetPassage(63, 0, true);
        world.TrySetPassage(64, 0, true);
        Assert.Equal(
            new[] { new TileCoord(0, 0), new TileCoord(63, 0), new TileCoord(64, 0), new TileCoord(70, 1), new TileCoord(99, 99) },
            world.Passages.Enumerate());
    }

    [Fact]
    public void Passage_IsSeparateFromTileData_AndMarksBothLayersDirty()
    {
        var world = TestWorlds.Create(128, 128, chunkSize: 32);
        world.TrySetPassage(40, 40, true);

        Assert.Equal(new TileState(Terrain.Dirt, SolidBlock.None, 0, 0), world.Surface.GetState(40, 40));
        foreach (var layer in world.Layers)
        {
            var dirty = new List<Chunk>();
            Assert.Equal(1, layer.CollectDirtyChunks(dirty));
            Assert.Equal(new ChunkCoord(1, 1), dirty[0].Coord);
        }
    }
}
