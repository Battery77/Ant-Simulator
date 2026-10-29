using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class WorldLayerTests
{
    private static readonly TileState Empty = new(Terrain.Dirt, SolidBlock.None, 0, 0);

    [Fact]
    public void NewWorld_IsDirtWithNoBlocksOrPassages()
    {
        var world = TestWorlds.Create(100, 70, chunkSize: 32);
        foreach (var layer in world.Layers)
        for (int y = 0; y < 70; y++)
        for (int x = 0; x < 100; x++)
            Assert.Equal(Empty, layer.GetState(x, y));
        Assert.Equal(0, world.Passages.Count);
    }

    [Fact]
    public void SetGet_RoundTripsAcrossChunkBoundaries()
    {
        var layer = TestWorlds.Create(100, 100, chunkSize: 16).Surface;
        var points = new[] { (0, 0), (15, 15), (16, 15), (15, 16), (16, 16), (99, 99), (31, 64) };
        for (int i = 0; i < points.Length; i++)
            Assert.Equal(WorldError.None, layer.TrySetState(points[i].Item1, points[i].Item2, new TileState(Terrain.Water, SolidBlock.None, (byte)i, 1)));

        for (int i = 0; i < points.Length; i++)
        {
            var (x, y) = points[i];
            Assert.Equal(new TileState(Terrain.Water, SolidBlock.None, (byte)i, 1), layer.GetState(x, y));
            Assert.Equal(Terrain.Water, layer.GetTerrain(x, y));
            Assert.Equal((byte)i, layer.GetQuantity(QuantityKind.Food, x, y));
            Assert.Equal(1, layer.GetQuantity(QuantityKind.Larva, x, y));
        }
    }

    [Fact]
    public void Layers_AreIndependent()
    {
        var world = TestWorlds.Create(64, 64);
        world.Surface.TrySetState(5, 5, Empty with { Terrain = Terrain.Water });
        world.Nest.TrySetState(5, 5, Empty with { Solid = SolidBlock.Rock });

        Assert.Equal(Terrain.Water, world.Surface.GetTerrain(5, 5));
        Assert.Equal(SolidBlock.None, world.Surface.GetSolid(5, 5));
        Assert.Equal(Terrain.Dirt, world.Nest.GetTerrain(5, 5));
        Assert.Equal(SolidBlock.Rock, world.Nest.GetSolid(5, 5));
        Assert.Same(world.Nest, world.GetLayer(LayerId.Nest));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    [InlineData(127, 127)] // 가장자리 청크의 패딩 영역도 범위 밖이다
    public void OutOfBounds_IsRejected(int x, int y)
    {
        var layer = TestWorlds.Create(100, 100, chunkSize: 64).Surface;
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.GetState(x, y));
        Assert.Equal(WorldError.OutOfBounds, layer.TrySetState(x, y, Empty));
    }

    [Fact]
    public void TrySetState_RejectsRuleViolationsWithoutChanging()
    {
        var layer = TestWorlds.Create(10, 10).Surface;
        Assert.Equal(WorldError.SolidOccupied, layer.TrySetState(1, 1, new TileState(Terrain.Dirt, SolidBlock.Soil, 1, 0)));
        Assert.Equal(WorldError.QuantityLimitExceeded, layer.TrySetState(1, 1, new TileState(Terrain.Dirt, SolidBlock.None, 6, 5)));
        Assert.Equal(WorldError.InvalidTerrain, layer.TrySetState(1, 1, Empty with { Terrain = (Terrain)9 }));
        Assert.Equal(WorldError.InvalidSolid, layer.TrySetState(1, 1, Empty with { Solid = (SolidBlock)9 }));
        Assert.Equal(Empty, layer.GetState(1, 1));
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
        Assert.Equal(36, corner.Row(TilePlane.Terrain, 0).Length);
        Assert.Equal(64 * 64, corner.Plane(TilePlane.Food).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.GetChunk(new ChunkCoord(2, 0)));
    }

    [Fact]
    public void CopyRow_ReturnsWorldOrderedRow()
    {
        var layer = TestWorlds.Create(100, 10, chunkSize: 16).Surface;
        layer.TrySetState(0, 3, Empty with { Food = 1 });
        layer.TrySetState(50, 3, Empty with { Food = 2 });
        layer.TrySetState(99, 3, Empty with { Food = 3 });
        var row = new byte[100];
        layer.CopyRow(TilePlane.Food, 3, row);
        Assert.Equal(1, row[0]);
        Assert.Equal(2, row[50]);
        Assert.Equal(3, row[99]);
        Assert.Equal(6, row.Sum(b => b));
    }
}
