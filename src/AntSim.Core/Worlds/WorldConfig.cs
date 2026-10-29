using System.Numerics;

namespace AntSim.Core.Worlds;

/// <summary>
/// 월드 크기와 청크 분할 설정. 생성 후 변경할 수 없다.
/// 월드 크기는 청크 크기의 배수일 필요가 없으며, 가장자리 청크는 일부만 유효하다.
/// </summary>
public sealed class WorldConfig
{
    public const int DefaultChunkSize = 64;
    public const int MinChunkSize = 8;
    public const int MaxChunkSize = 256;

    /// <summary>한 레이어의 최대 타일 수. 레이어 전체 연속 배열(냄새 등)의 인덱스가 int 범위를 넘지 않도록 제한한다.</summary>
    public const long MaxTilesPerLayer = 1L << 28;

    public WorldConfig(int width, int height, int chunkSize = DefaultChunkSize)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "월드 너비는 1 이상이어야 합니다.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "월드 높이는 1 이상이어야 합니다.");
        if ((long)width * height > MaxTilesPerLayer)
            throw new ArgumentOutOfRangeException(nameof(width), $"레이어당 타일 수는 {MaxTilesPerLayer} 이하여야 합니다.");
        if (chunkSize < MinChunkSize || chunkSize > MaxChunkSize || !BitOperations.IsPow2(chunkSize))
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize,
                $"청크 크기는 {MinChunkSize}~{MaxChunkSize} 범위의 2의 거듭제곱이어야 합니다.");

        Width = width;
        Height = height;
        ChunkSize = chunkSize;
        ChunkShift = BitOperations.Log2((uint)chunkSize);
        ChunkMask = chunkSize - 1;
        ChunksX = (width + chunkSize - 1) >> ChunkShift;
        ChunksY = (height + chunkSize - 1) >> ChunkShift;
    }

    public int Width { get; }
    public int Height { get; }
    public int ChunkSize { get; }
    public int ChunkShift { get; }
    public int ChunkMask { get; }
    public int ChunksX { get; }
    public int ChunksY { get; }

    public int TileCount => Width * Height;
    public int ChunkCount => ChunksX * ChunksY;

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public ChunkCoord ToChunk(int x, int y) => new(x >> ChunkShift, y >> ChunkShift);

    /// <summary>레이어 전체 연속 배열에서의 행 우선 인덱스.</summary>
    public int ToLinearIndex(int x, int y) => y * Width + x;
}
