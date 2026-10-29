using AntSim.Core.Content;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

/// <summary>테스트용 월드 생성 도우미. 타입 키는 테스트 전용이며 게임 규칙이 아니다.</summary>
internal static class TestWorlds
{
    public static TypeRegistry Tiles()
    {
        var tiles = new TypeRegistry("tile");
        tiles.Register("test_a", "A");
        tiles.Register("test_b", "B");
        tiles.Register("test_c", "C");
        return tiles;
    }

    public static TypeRegistry Blocks()
    {
        var blocks = TypeRegistry.CreateWithEmpty("block");
        blocks.Register("test_block", "Block");
        return blocks;
    }

    public static World Create(int width, int height, int chunkSize = WorldConfig.DefaultChunkSize, ushort defaultTile = 0) =>
        new(new WorldConfig(width, height, chunkSize), Tiles(), Blocks(), defaultTile);
}
