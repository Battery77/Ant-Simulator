using AntSim.Core.Content;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class WorldLayerTests
{
    [Fact]
    public void NewWorld_IsFilledWithDefaultTileAndNoBlocks()
    {
        var world = TestWorlds.Create(100, 70, chunkSize: 32, defaultTile: 2);
        foreach (var layer in world.Layers)
        for (int y = 0; y < 70; y++)
        for (int x = 0; x < 100; x++)
        {
            Assert.Equal(2, layer.GetTile(x, y));
            Assert.Equal(TypeRegistry.EmptyId, layer.GetBlock(x, y));
        }
    }

    [Fact]
    public void SetGet_RoundTripsAcrossChunkBoundaries()
    {
        var world = TestWorlds.Create(100, 100, chunkSize: 16);
        var layer = world.Surface;
        var points = new[] { (0, 0), (15, 15), (16, 15), (15, 16), (16, 16), (99, 99), (31, 64) };
        for (int i = 0; i < points.Length; i++)
            layer.SetTile(points[i].Item1, points[i].Item2, (ushort)(i % 3));

        for (int i = 0; i < points.Length; i++)
            Assert.Equal((ushort)(i % 3), layer.GetTile(points[i].Item1, points[i].Item2));
    }

    [Fact]
    public void Layers_AreIndependent()
    {
        var world = TestWorlds.Create(64, 64);
        world.Surface.SetTile(5, 5, 1);
        world.Nest.SetBlock(5, 5, 1);

        Assert.Equal(1, world.Surface.GetTile(5, 5));
        Assert.Equal(0, world.Nest.GetTile(5, 5));
        Assert.Equal(0, world.Surface.GetBlock(5, 5));
        Assert.Equal(1, world.Nest.GetBlock(5, 5));
        Assert.Same(world.Nest, world.GetLayer(LayerId.Nest));
    }

    [Fact]
    public void TilesAndBlocks_AreIndependent()
    {
        var world = TestWorlds.Create(64, 64);
        world.Surface.SetBlock(3, 4, 1);
        Assert.Equal(0, world.Surface.GetTile(3, 4));
        world.Surface.SetTile(3, 4, 2);
        Assert.Equal(1, world.Surface.GetBlock(3, 4));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    [InlineData(127, 127)] // 가장자리 청크의 패딩 영역도 범위 밖이다
    public void OutOfBounds_Throws(int x, int y)
    {
        var layer = TestWorlds.Create(100, 100, chunkSize: 64).Surface;
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.GetTile(x, y));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetTile(x, y, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetBlock(x, y, 1));
    }

    [Fact]
    public void UnregisteredIds_Throw()
    {
        var layer = TestWorlds.Create(10, 10).Surface;
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetTile(0, 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetBlock(0, 0, 2));
    }

    [Fact]
    public void World_RejectsInvalidRegistries()
    {
        var config = new WorldConfig(10, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => new World(config, TestWorlds.Tiles(), TestWorlds.Blocks(), 99));
        Assert.Throws<ArgumentException>(() => new World(config, TestWorlds.Tiles(), new TypeRegistry("block"), 0));
    }

    [Fact]
    public void EdgeChunks_ReportValidArea()
    {
        var layer = TestWorlds.Create(100, 70, chunkSize: 64).Surface;
        var corner = layer.GetChunk(new ChunkCoord(1, 1));
        Assert.Equal(64, corner.OriginX);
        Assert.Equal(64, corner.OriginY);
        Assert.Equal(36, corner.ValidWidth);
        Assert.Equal(6, corner.ValidHeight);
        Assert.Equal(36, corner.TileRow(0).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.GetChunk(new ChunkCoord(2, 0)));
    }
}
