using AntSim.Core.Rendering;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class ChunkRasterizerTests
{
    private static uint Pixel(byte[] rgba, int size, int x, int y)
    {
        int o = (y * size + x) * 4;
        return (uint)(rgba[o] | rgba[o + 1] << 8 | rgba[o + 2] << 16 | rgba[o + 3] << 24);
    }

    [Fact]
    public void ProducesPaletteColorsAndTransparentPadding()
    {
        var world = TestWorlds.Create(40, 40, chunkSize: 32);
        world.Surface.TrySetState(33, 1, new TileState(Terrain.Water, SolidBlock.None, 0, 0));
        world.Surface.TrySetState(34, 1, new TileState(Terrain.Dirt, SolidBlock.Rock, 0, 0));
        var chunk = world.Surface.GetChunk(new ChunkCoord(1, 0));
        var rgba = new byte[ChunkRasterizer.BufferSize(32)];
        var palette = TilePalette.Default;

        ChunkRasterizer.Rasterize(chunk, world.Passages, 10, palette, rgba);

        Assert.Equal(palette.Dirt, Pixel(rgba, 32, 0, 0));
        Assert.Equal(palette.Water, Pixel(rgba, 32, 1, 1));
        Assert.Equal(palette.Rock, Pixel(rgba, 32, 2, 1));
        Assert.Equal(0u, Pixel(rgba, 32, 8, 0));   // x=40: 유효 영역 밖
        Assert.Equal(palette.Dirt, Pixel(rgba, 32, 0, 8)); // y=8은 유효 영역

        ChunkRasterizer.Rasterize(world.Surface.GetChunk(new ChunkCoord(1, 1)), world.Passages, 10, palette, rgba);
        Assert.Equal(palette.Dirt, Pixel(rgba, 32, 7, 7));
        Assert.Equal(0u, Pixel(rgba, 32, 0, 8));   // y=40: 유효 영역 밖
    }

    [Fact]
    public void QuantityAndPassage_ChangeColor()
    {
        var world = TestWorlds.Create(32, 32, chunkSize: 32);
        world.Surface.TrySetState(1, 0, new TileState(Terrain.Dirt, SolidBlock.None, 10, 0));
        world.TrySetPassage(2, 0, true);
        var rgba = new byte[ChunkRasterizer.BufferSize(32)];
        ChunkRasterizer.Rasterize(world.Surface.GetChunk(new ChunkCoord(0, 0)), world.Passages, 10, TilePalette.Default, rgba);

        Assert.Equal(TilePalette.Default.Food, Pixel(rgba, 32, 1, 0));
        Assert.NotEqual(TilePalette.Default.Dirt, Pixel(rgba, 32, 2, 0));
    }
}
