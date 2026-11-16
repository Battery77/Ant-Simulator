using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

internal static class TestWorlds
{
    public static World Create(int width, int height, int chunkSize = WorldConfig.DefaultChunkSize, WorldRules? rules = null) =>
        new(new WorldConfig(width, height, chunkSize), rules);

    /// <summary>좌표에서 결정적으로 계산한 다양한 타일·통로 패턴(난수 사용 안 함). 모두 규칙을 만족한다.</summary>
    public static void Paint(World world)
    {
        for (int y = 0; y < world.Config.Height; y += 3)
        for (int x = 0; x < world.Config.Width; x += 5)
        {
            int k = (x * 7 + y * 13) % 6;
            var state = k switch
            {
                0 => new TileState(Terrain.Water, SolidBlock.None, 0, 0),
                1 => new TileState(Terrain.Dirt, SolidBlock.Soil, 0, 0),
                2 => new TileState(Terrain.Dirt, SolidBlock.Rock, 0, 0),
                3 => new TileState(Terrain.Dirt, SolidBlock.None, (byte)(x % 11), 0),
                4 => new TileState(Terrain.Water, SolidBlock.None, 3, 7),
                _ => new TileState(Terrain.Dirt, SolidBlock.None, 0, (byte)(y % 11)),
            };
            Assert.Equal(WorldError.None, world.GetLayer((LayerId)((x + y) & 1)).TrySetState(x, y, state));
            if ((x + y) % 17 == 0) world.TrySetPassage(x, y, true);
        }
    }
}

internal static class ScentDisplay
{
    /// <summary>스냅샷과 같은 표시 격자로 펼친 냄새 필드(지상 묶음 격자 그대로, 개미집은 W·H에 열린 칸 값, 나머지 0).</summary>
    public static float[] Of(Scent.ScentState state, int type, LayerId layer)
    {
        var display = new float[state.Grid(layer).DisplayLength];
        state.ExpandTo(type, layer, display);
        return display;
    }
}
