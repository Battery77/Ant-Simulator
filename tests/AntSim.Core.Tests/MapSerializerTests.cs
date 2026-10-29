using System.Buffers.Binary;
using AntSim.Core.Serialization;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class MapSerializerTests
{
    private static byte[] SaveToBytes(World world)
    {
        using var stream = new MemoryStream();
        MapSerializer.Save(world, stream);
        return stream.ToArray();
    }

    private static World LoadFromBytes(byte[] bytes, int chunkSize = WorldConfig.DefaultChunkSize) =>
        MapSerializer.Load(new MemoryStream(bytes), chunkSize);

    [Fact]
    public void RoundTrip_PreservesHash()
    {
        var world = TestWorlds.Create(130, 90, rules: new WorldRules { MaxQuantityPerTile = 12 });
        TestWorlds.Paint(world);
        var loaded = LoadFromBytes(SaveToBytes(world));
        Assert.Equal(world.ComputeContentHash(), loaded.ComputeContentHash());
        Assert.Equal(12, loaded.Rules.MaxQuantityPerTile);
        Assert.Equal(world.Passages.Count, loaded.Passages.Count);
    }

    [Fact]
    public void RoundTrip_WorksWithDifferentChunkSize()
    {
        var world = TestWorlds.Create(130, 90, chunkSize: 16);
        TestWorlds.Paint(world);
        var loaded = LoadFromBytes(SaveToBytes(world), chunkSize: 64);
        Assert.Equal(world.ComputeContentHash(), loaded.ComputeContentHash());
    }

    [Fact]
    public void SavedOutput_IsDeterministic()
    {
        var a = TestWorlds.Create(50, 40);
        var b = TestWorlds.Create(50, 40);
        TestWorlds.Paint(a);
        TestWorlds.Paint(b);
        Assert.Equal(SaveToBytes(a), SaveToBytes(b));
    }

    [Fact]
    public void LoadedWorld_HasNoDirtyChunks()
    {
        var world = TestWorlds.Create(64, 64);
        TestWorlds.Paint(world);
        var loaded = LoadFromBytes(SaveToBytes(world));
        Assert.Equal(0, loaded.Surface.CollectDirtyChunks(new List<Chunk>()));
    }

    [Fact]
    public void FileRoundTrip_UsesAtomicReplace()
    {
        string path = Path.Combine(Path.GetTempPath(), $"antmap-{Guid.NewGuid():N}.antmap");
        try
        {
            var world = TestWorlds.Create(40, 40);
            TestWorlds.Paint(world);
            MapSerializer.Save(world, path);
            MapSerializer.Save(world, path); // 덮어쓰기
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(world.ComputeContentHash(), MapSerializer.Load(path).ComputeContentHash());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BadMagic_IsRejected()
    {
        var bytes = SaveToBytes(TestWorlds.Create(8, 8));
        bytes[0] = (byte)'X';
        Assert.Throws<MapFormatException>(() => LoadFromBytes(bytes));
    }

    [Fact]
    public void NewerVersion_IsRejected()
    {
        var bytes = SaveToBytes(TestWorlds.Create(8, 8));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), MapSerializer.CurrentVersion + 1);
        var e = Assert.Throws<MapFormatException>(() => LoadFromBytes(bytes));
        Assert.Contains("버전", e.Message);
    }

    [Fact]
    public void Truncated_IsRejected()
    {
        var bytes = SaveToBytes(TestWorlds.Create(8, 8));
        Assert.Throws<MapFormatException>(() => LoadFromBytes(bytes[..^3]));
        Assert.Throws<MapFormatException>(() => LoadFromBytes(bytes[..20]));
    }

    [Fact]
    public void CorruptedPayload_FailsChecksum()
    {
        var world = TestWorlds.Create(8, 8);
        var bytes = SaveToBytes(world);
        int terrainStart = 8 + 2 + 2 + 4 + 4 + 4 + 1;
        bytes[terrainStart] = 1; // 유효한 값(물)이지만 체크섬과 맞지 않음
        var e = Assert.Throws<MapFormatException>(() => LoadFromBytes(bytes));
        Assert.Contains("체크섬", e.Message);
    }

    [Fact]
    public void RuleViolatingTile_IsRejected()
    {
        // 직렬화 형식을 직접 만들어 고체 + 먹이 타일을 넣는다(체크섬은 올바르게 계산).
        var world = TestWorlds.Create(8, 8);
        var bytes = SaveToBytes(world);
        int tiles = 64, header = 8 + 2 + 2 + 4 + 4 + 4 + 1;
        bytes[header + tiles * (int)TilePlane.Solid] = (byte)SolidBlock.Rock;
        bytes[header + tiles * (int)TilePlane.Food] = 1;
        var fnv = Util.Fnv1a64.Create();
        fnv.Add(bytes.AsSpan(0, bytes.Length - 8));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), fnv.Value);

        var e = Assert.Throws<MapFormatException>(() => LoadFromBytes(bytes));
        Assert.Contains("SolidOccupied", e.Message);
    }
}
