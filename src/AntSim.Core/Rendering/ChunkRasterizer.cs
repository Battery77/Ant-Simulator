using AntSim.Core.Worlds;

namespace AntSim.Core.Rendering;

/// <summary>표시 색상(RGBA8). 렌더링 전용이며 시뮬레이션 규칙과 무관하다.</summary>
public sealed record TilePalette
{
    public static TilePalette Default { get; } = new();

    public uint Dirt { get; init; } = Rgba(0x8B, 0x6B, 0x4A);
    public uint Water { get; init; } = Rgba(0x3A, 0x7B, 0xC8);
    public uint Soil { get; init; } = Rgba(0x4A, 0x33, 0x22);
    public uint Rock { get; init; } = Rgba(0x80, 0x80, 0x88);
    public uint Food { get; init; } = Rgba(0x5C, 0xD6, 0x3A);
    public uint Larva { get; init; } = Rgba(0xF4, 0xEE, 0xD8);
    public uint Passage { get; init; } = Rgba(0xE0, 0x3C, 0xC8);

    public static uint Rgba(byte r, byte g, byte b, byte a = 0xFF) => (uint)(r | g << 8 | b << 16 | a << 24);
}

/// <summary>
/// 청크 하나를 RGBA8 픽셀(청크 크기 × 청크 크기, 행 우선)로 변환한다. Godot의 Image.FORMAT_RGBA8과 같은 배치.
/// 유효 영역 밖(가장자리 청크의 패딩)은 투명으로 채운다.
/// </summary>
public static class ChunkRasterizer
{
    public static int BufferSize(int chunkSize) => chunkSize * chunkSize * 4;

    public static void Rasterize(Chunk chunk, PassageMap passages, int maxQuantity, TilePalette palette, Span<byte> rgba)
    {
        int size = chunk.Size;
        if (rgba.Length < BufferSize(size)) throw new ArgumentException("픽셀 버퍼가 너무 작습니다.", nameof(rgba));
        rgba[..BufferSize(size)].Clear();

        var terrain = chunk.Plane(TilePlane.Terrain);
        var solid = chunk.Plane(TilePlane.Solid);
        var food = chunk.Plane(TilePlane.Food);
        var larva = chunk.Plane(TilePlane.Larva);
        bool anyPassages = passages.Count > 0;

        for (int ly = 0; ly < chunk.ValidHeight; ly++)
        {
            int row = ly * size;
            for (int lx = 0; lx < chunk.ValidWidth; lx++)
            {
                int i = row + lx;
                uint color = (SolidBlock)solid[i] switch
                {
                    SolidBlock.Soil => palette.Soil,
                    SolidBlock.Rock => palette.Rock,
                    _ => (Terrain)terrain[i] == Terrain.Water ? palette.Water : palette.Dirt,
                };

                int total = food[i] + larva[i];
                if (total > 0)
                {
                    uint block = food[i] >= larva[i] ? palette.Food : palette.Larva;
                    int weight = maxQuantity > 0 ? 96 + 159 * Math.Min(total, maxQuantity) / maxQuantity : 255;
                    color = Lerp(color, block, weight);
                }

                if (anyPassages && passages.Contains(chunk.OriginX + lx, chunk.OriginY + ly))
                    color = Lerp(color, palette.Passage, 170);

                int o = i * 4;
                rgba[o] = (byte)color;
                rgba[o + 1] = (byte)(color >> 8);
                rgba[o + 2] = (byte)(color >> 16);
                rgba[o + 3] = (byte)(color >> 24);
            }
        }
    }

    /// <summary>두 색을 weight/255 비율로 섞는다.</summary>
    private static uint Lerp(uint a, uint b, int weight)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int ca = (int)(a >> shift) & 0xFF, cb = (int)(b >> shift) & 0xFF;
            result |= (uint)(ca + (cb - ca) * weight / 255) << shift;
        }
        return result;
    }
}
