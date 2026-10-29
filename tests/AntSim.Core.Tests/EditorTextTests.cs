using AntSim.Core.Editing;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class EditorTextTests
{
    [Fact]
    public void DescribeTile_ShowsAllTileProperties()
    {
        var world = TestWorlds.Create(20, 20);
        world.Nest.TrySetState(3, 4, new TileState(Terrain.Water, SolidBlock.None, 2, 5));
        world.TrySetPassage(3, 4, true);

        string text = EditorText.DescribeTile(world, LayerId.Nest, 3, 4);
        Assert.Contains("(3, 4) 개미집", text);
        Assert.Contains("지형: 물", text);
        Assert.Contains("고체 블록: 없음", text);
        Assert.Contains("먹이: 2, 유충: 5 (합계 7/10)", text);
        Assert.Contains("통로: 있음", text);
    }

    [Fact]
    public void DescribeTile_OutOfBounds()
    {
        Assert.Contains("맵 밖", EditorText.DescribeTile(TestWorlds.Create(5, 5), LayerId.Surface, 5, 0));
    }

    [Fact]
    public void EveryErrorHasKoreanDescription()
    {
        foreach (var error in Enum.GetValues<WorldError>())
            Assert.NotEqual(error.ToString(), EditorText.Describe(error));
    }
}
