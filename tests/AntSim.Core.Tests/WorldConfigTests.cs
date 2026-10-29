using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class WorldConfigTests
{
    [Theory]
    [InlineData(0, 10, 64)]
    [InlineData(10, 0, 64)]
    [InlineData(-1, 10, 64)]
    [InlineData(10, 10, 48)]   // 2의 거듭제곱 아님
    [InlineData(10, 10, 4)]    // 너무 작음
    [InlineData(10, 10, 512)]  // 너무 큼
    [InlineData(20000, 20000, 64)] // 레이어당 타일 수 초과
    public void InvalidConfig_Throws(int w, int h, int chunk)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldConfig(w, h, chunk));
    }

    [Fact]
    public void ChunkCounts_RoundUpForPartialEdgeChunks()
    {
        var config = new WorldConfig(1000, 1000, 64);
        Assert.Equal(16, config.ChunksX);
        Assert.Equal(16, config.ChunksY);
        Assert.Equal(6, config.ChunkShift);
        Assert.Equal(63, config.ChunkMask);
    }

    [Fact]
    public void ExactMultiple_HasNoExtraChunk()
    {
        var config = new WorldConfig(1024, 512, 64);
        Assert.Equal(16, config.ChunksX);
        Assert.Equal(8, config.ChunksY);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(63, 63, 0, 0)]
    [InlineData(64, 0, 1, 0)]
    [InlineData(999, 999, 15, 15)]
    public void ToChunk_MapsTileToContainingChunk(int x, int y, int cx, int cy)
    {
        var config = new WorldConfig(1000, 1000, 64);
        Assert.Equal(new ChunkCoord(cx, cy), config.ToChunk(x, y));
    }

    [Theory]
    [InlineData(-1, 0, false)]
    [InlineData(0, -1, false)]
    [InlineData(1000, 0, false)]
    [InlineData(0, 1000, false)]
    [InlineData(999, 999, true)]
    public void InBounds(int x, int y, bool expected)
    {
        Assert.Equal(expected, new WorldConfig(1000, 1000).InBounds(x, y));
    }
}
