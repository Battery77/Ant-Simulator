using AntSim.Core.Worlds;

namespace AntSim.Core.Serialization;

/// <summary>
/// 버전이 있는 바이너리 맵 형식. 모든 정수는 리틀 엔디언.
/// <code>
/// [v1]
/// magic        8 bytes  "ANTMAP\0\0"
/// version      u16      = 1
/// flags        u16      = 0 (예약)
/// width        i32
/// height       i32
/// maxQuantity  i32      타일당 수량 상한 (WorldRules)
/// layerCount   u8       = 2
/// layers       layerCount × planeCount(4) × (width*height) bytes
///              레이어 순서 Surface, Nest / 평면 순서 Terrain, Solid, Food, Larva / 각 평면은 행 우선
/// passages     i32 count, 이후 count × i32 선형 인덱스(y*width+x), 엄격한 오름차순
/// checksum     u64      위 모든 바이트의 FNV-1a 64
/// </code>
/// 청크 크기는 실행 시 설정이므로 저장하지 않는다.
/// </summary>
public static class MapSerializer
{
    public const ushort CurrentVersion = 1;
    private static ReadOnlySpan<byte> Magic => "ANTMAP\0\0"u8;

    public static void Save(World world, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(stream);
        var writer = new ChecksumWriter(stream);
        var config = world.Config;

        writer.Write(Magic);
        writer.WriteUInt16(CurrentVersion);
        writer.WriteUInt16(0);
        writer.WriteInt32(config.Width);
        writer.WriteInt32(config.Height);
        writer.WriteInt32(world.Rules.MaxQuantityPerTile);
        writer.WriteByte((byte)World.LayerCount);

        var row = new byte[config.Width];
        foreach (var layer in world.Layers)
        for (int p = 0; p < TileEnums.PlaneCount; p++)
        for (int y = 0; y < config.Height; y++)
        {
            layer.CopyRow((TilePlane)p, y, row);
            writer.Write(row);
        }

        var indices = new List<int>(world.Passages.Count);
        world.Passages.CopyIndicesTo(indices);
        writer.WriteInt32(indices.Count);
        foreach (int i in indices) writer.WriteInt32(i);

        writer.WriteChecksum();
    }

    public static void Save(World world, string path)
    {
        // 저장 도중 실패해도 기존 파일이 깨지지 않도록 임시 파일에 쓴 뒤 교체한다.
        string temp = path + ".tmp";
        using (var file = File.Create(temp)) Save(world, file);
        File.Move(temp, path, overwrite: true);
    }

    public static World Load(string path, int chunkSize = WorldConfig.DefaultChunkSize)
    {
        using var file = File.OpenRead(path);
        return Load(file, chunkSize);
    }

    public static World Load(Stream stream, int chunkSize = WorldConfig.DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new ChecksumReader(stream);

        Span<byte> magic = stackalloc byte[8];
        reader.Read(magic);
        if (!magic.SequenceEqual(Magic)) throw new MapFormatException("맵 파일이 아닙니다(매직 값 불일치).");
        ushort version = reader.ReadUInt16();
        if (version == 0 || version > CurrentVersion)
            throw new MapFormatException($"지원하지 않는 맵 형식 버전입니다: {version} (지원: 1~{CurrentVersion}).");
        reader.ReadUInt16(); // flags

        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        int maxQuantity = reader.ReadInt32();
        WorldConfig config;
        WorldRules rules;
        try
        {
            config = new WorldConfig(width, height, chunkSize);
            rules = new WorldRules { MaxQuantityPerTile = maxQuantity };
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new MapFormatException($"맵 헤더 값이 잘못되었습니다: {e.Message}", e);
        }

        byte layerCount = reader.ReadByte();
        if (layerCount != World.LayerCount)
            throw new MapFormatException($"레이어 수가 잘못되었습니다: {layerCount}.");

        var world = new World(config, rules);
        int tiles = config.TileCount;
        var planes = new byte[TileEnums.PlaneCount][];
        for (int p = 0; p < planes.Length; p++) planes[p] = new byte[tiles];

        foreach (var layer in world.Layers)
        {
            foreach (var plane in planes) reader.Read(plane);
            for (int i = 0; i < tiles; i++)
            {
                var state = new TileState(
                    (Terrain)planes[(int)TilePlane.Terrain][i],
                    (SolidBlock)planes[(int)TilePlane.Solid][i],
                    planes[(int)TilePlane.Food][i],
                    planes[(int)TilePlane.Larva][i]);
                var error = rules.Validate(state);
                if (error != WorldError.None)
                    throw new MapFormatException($"{layer.Id} 레이어 ({i % width}, {i / width}) 타일이 규칙을 위반합니다: {error}.");
            }
            for (int p = 0; p < planes.Length; p++)
            for (int y = 0; y < height; y++)
                layer.WriteRowUnchecked((TilePlane)p, y, planes[p].AsSpan(y * width, width));
        }

        int passageCount = reader.ReadInt32();
        if (passageCount < 0 || passageCount > tiles) throw new MapFormatException($"통로 수가 잘못되었습니다: {passageCount}.");
        int previous = -1;
        for (int n = 0; n < passageCount; n++)
        {
            int index = reader.ReadInt32();
            if (index <= previous || index >= tiles)
                throw new MapFormatException($"통로 인덱스가 잘못되었습니다: {index}.");
            world.SetPassageUnchecked(index % width, index / width, true);
            previous = index;
        }

        reader.VerifyChecksum();

        foreach (var layer in world.Layers) layer.ClearAllDirty();
        return world;
    }
}
