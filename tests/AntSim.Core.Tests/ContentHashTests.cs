namespace AntSim.Core.Tests;

public class ContentHashTests
{
    private static void Paint(Worlds.World world)
    {
        // 결정적 패턴: 난수 없이 좌표에서 값을 계산한다.
        for (int y = 0; y < world.Config.Height; y += 3)
        for (int x = 0; x < world.Config.Width; x += 7)
        {
            world.Surface.SetTile(x, y, (ushort)((x + y) % 3));
            world.Nest.SetBlock(x, y, (ushort)((x * y) % 2));
        }
    }

    [Fact]
    public void SameEdits_ProduceSameHash()
    {
        var a = TestWorlds.Create(200, 150);
        var b = TestWorlds.Create(200, 150);
        Paint(a);
        Paint(b);
        Assert.Equal(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void Hash_IsIndependentOfChunkSize()
    {
        var a = TestWorlds.Create(200, 150, chunkSize: 16);
        var b = TestWorlds.Create(200, 150, chunkSize: 128);
        Paint(a);
        Paint(b);
        Assert.Equal(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void AnySingleChange_ChangesHash()
    {
        var world = TestWorlds.Create(100, 100);
        ulong original = world.ComputeContentHash();

        world.Nest.SetBlock(99, 99, 1);
        ulong changed = world.ComputeContentHash();
        Assert.NotEqual(original, changed);

        world.Nest.SetBlock(99, 99, 0);
        Assert.Equal(original, world.ComputeContentHash());
    }

    [Fact]
    public void SameEditOnDifferentLayers_ProducesDifferentHash()
    {
        var a = TestWorlds.Create(50, 50);
        var b = TestWorlds.Create(50, 50);
        a.Surface.SetTile(1, 1, 1);
        b.Nest.SetTile(1, 1, 1);
        Assert.NotEqual(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void Hash_IsStableAcrossRuns()
    {
        // 해시 알고리즘/직렬화 순서가 바뀌면 이 값이 바뀐다. 의도된 변경일 때만 갱신한다.
        var world = TestWorlds.Create(64, 32, chunkSize: 16);
        world.Surface.SetTile(10, 20, 2);
        world.Nest.SetBlock(63, 31, 1);
        Assert.Equal(891372233350739547UL, world.ComputeContentHash());
    }
}
