using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class ContentHashTests
{
    [Fact]
    public void SameEdits_ProduceSameHash()
    {
        var a = TestWorlds.Create(200, 150);
        var b = TestWorlds.Create(200, 150);
        TestWorlds.Paint(a);
        TestWorlds.Paint(b);
        Assert.Equal(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void Hash_IsIndependentOfChunkSize()
    {
        var a = TestWorlds.Create(200, 150, chunkSize: 16);
        var b = TestWorlds.Create(200, 150, chunkSize: 128);
        TestWorlds.Paint(a);
        TestWorlds.Paint(b);
        Assert.Equal(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void AnySingleChange_ChangesHash()
    {
        var world = TestWorlds.Create(100, 100);
        ulong original = world.ComputeContentHash();

        world.Nest.TrySetState(99, 99, new TileState(Terrain.Dirt, SolidBlock.None, 0, 1));
        Assert.NotEqual(original, world.ComputeContentHash());
        world.Nest.TrySetState(99, 99, new TileState(Terrain.Dirt, SolidBlock.None, 0, 0));
        Assert.Equal(original, world.ComputeContentHash());

        world.TrySetPassage(3, 3, true);
        Assert.NotEqual(original, world.ComputeContentHash());
    }

    [Fact]
    public void SameEditOnDifferentLayers_ProducesDifferentHash()
    {
        var a = TestWorlds.Create(50, 50);
        var b = TestWorlds.Create(50, 50);
        var water = new TileState(Terrain.Water, SolidBlock.None, 0, 0);
        a.Surface.TrySetState(1, 1, water);
        b.Nest.TrySetState(1, 1, water);
        Assert.NotEqual(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void Hash_IsStableAcrossRuns()
    {
        // 해시 알고리즘/평면 순서가 바뀌면 이 값이 바뀐다. 의도된 변경일 때만 갱신한다.
        var world = TestWorlds.Create(64, 32, chunkSize: 16);
        world.Surface.TrySetState(10, 20, new TileState(Terrain.Water, SolidBlock.None, 2, 3));
        world.Nest.TrySetState(63, 31, new TileState(Terrain.Dirt, SolidBlock.Rock, 0, 0));
        world.TrySetPassage(5, 6, true);
        Assert.Equal(10783977250890355522UL, world.ComputeContentHash());
    }
}
