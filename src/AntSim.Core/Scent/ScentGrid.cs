using AntSim.Core.Worlds;

namespace AntSim.Core.Scent;

/// <summary>
/// 한 레이어의 냄새 저장 배치(docs/PERF_SCENT_DESIGN.md).
/// <list type="bullet">
/// <item><b>묶음 격자</b>(지상): <see cref="CellSize"/>×<see cref="CellSize"/>칸이 냄새 값 하나다. 저장 크기 ⌈W/F⌉ × ⌈H/F⌉.
/// 묶음 칸 안에 고체가 아닌 칸이 하나라도 있으면 열린 묶음 칸이다.</item>
/// <item><b>열린 칸 압축</b>(개미집): 원래 해상도. 고체가 아닌 칸만 행 우선 순서로 저장하고, 이웃 4개의 저장 인덱스를 미리 만든다.
/// 칸 → 인덱스 조회는 열린 칸이 있는 청크에만 만든 청크별 표를 쓴다.</item>
/// </list>
/// 배치는 불변이다. 땅이 바뀌면 <see cref="ScentState"/>가 새 배치를 만들어 값을 옮긴다.
/// </summary>
public sealed class ScentGrid
{
    /// <summary>지상 묶음 칸 크기(타일). 사용자 결정 P-1.</summary>
    public const int SurfaceCellSize = 4;

    private readonly WorldConfig _config;
    private readonly byte[]? _cellOpen;     // 묶음 격자: [cell] 1 = 열림
    private readonly int[]? _tiles;         // 압축: [entry] 원래 해상도 선형 인덱스
    private readonly int[]? _neighbors;     // 압축: [entry·4 + 0 왼쪽 / 1 오른쪽 / 2 위 / 3 아래], 없으면 −1
    private readonly int[]?[]? _chunkTables; // 압축: [chunk][local] 저장 인덱스, 열린 칸 없는 청크는 null

    private ScentGrid(WorldConfig config, int cellSize, byte[] cellOpen)
    {
        _config = config;
        IsSparse = false;
        CellSize = cellSize;
        Width = (config.Width + cellSize - 1) / cellSize;
        Height = (config.Height + cellSize - 1) / cellSize;
        _cellOpen = cellOpen;
        Length = Width * Height;
    }

    private ScentGrid(WorldConfig config, int[] tiles, int[] neighbors, int[]?[] chunkTables)
    {
        _config = config;
        IsSparse = true;
        CellSize = 1;
        Width = config.Width;
        Height = config.Height;
        _tiles = tiles;
        _neighbors = neighbors;
        _chunkTables = chunkTables;
        Length = tiles.Length;
    }

    /// <summary>열린 칸 압축 배치인가(아니면 묶음 격자).</summary>
    public bool IsSparse { get; }

    /// <summary>값 하나가 덮는 칸의 한 변(묶음 격자 F, 압축 1).</summary>
    public int CellSize { get; }

    /// <summary>표시 격자 크기. 묶음 격자는 저장 격자(⌈W/F⌉ × ⌈H/F⌉), 압축은 원래 지도(W × H).</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>냄새 종류 하나의 저장 값 수.</summary>
    public int Length { get; }

    internal byte[]? CellOpen => _cellOpen;
    internal int[]? Tiles => _tiles;
    internal int[]? Neighbors => _neighbors;

    /// <summary>
    /// 칸 (x, y)의 저장 인덱스. 맵 밖이면 −1. 압축 배치에서 고체 칸이면 −1. 묶음 격자에서는 맵 안이면 항상 그 묶음 칸
    /// (닫힌 묶음 칸의 값은 0).
    /// </summary>
    public int IndexOf(int x, int y)
    {
        if (!_config.InBounds(x, y)) return -1;
        if (!IsSparse) return (y / CellSize) * Width + x / CellSize;
        var table = _chunkTables![(y >> _config.ChunkShift) * _config.ChunksX + (x >> _config.ChunkShift)];
        return table is null ? -1 : table[((y & _config.ChunkMask) << _config.ChunkShift) | (x & _config.ChunkMask)];
    }

    /// <summary>
    /// 표시용 격자(<see cref="Width"/> × <see cref="Height"/>)로 값을 옮긴다. 묶음 격자는 그대로 복사, 압축은 0으로 채운 뒤 열린 칸에 흩뿌린다.
    /// </summary>
    public void ExpandTo(ReadOnlySpan<float> values, Span<float> display)
    {
        if (values.Length != Length) throw new ArgumentException("값 수가 배치와 다릅니다.", nameof(values));
        if (display.Length < Width * Height) throw new ArgumentException("표시 배열이 작습니다.", nameof(display));
        if (!IsSparse)
        {
            values.CopyTo(display);
            return;
        }
        display[..(Width * Height)].Clear();
        var tiles = _tiles!;
        for (int i = 0; i < tiles.Length; i++) display[tiles[i]] = values[i];
    }

    /// <summary>표시 격자 크기의 배열 길이.</summary>
    public int DisplayLength => Width * Height;

    /// <summary>원래 해상도 열린 칸 표시(1 = 비고체, 길이 W·H)로 묶음 격자 배치를 만든다.</summary>
    public static ScentGrid Coarse(WorldConfig config, byte[] fineOpen, int cellSize = SurfaceCellSize)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (cellSize < 1) throw new ArgumentOutOfRangeException(nameof(cellSize));
        int w = (config.Width + cellSize - 1) / cellSize, h = (config.Height + cellSize - 1) / cellSize;
        var open = new byte[w * h];
        for (int y = 0; y < config.Height; y++)
        {
            int row = y * config.Width, cellRow = (y / cellSize) * w;
            for (int x = 0; x < config.Width; x++)
                if (fineOpen[row + x] != 0) open[cellRow + x / cellSize] = 1;
        }
        return new ScentGrid(config, cellSize, open);
    }

    /// <summary>원래 해상도 열린 칸 표시로 열린 칸 압축 배치를 만든다(행 우선 순서, 결정적).</summary>
    public static ScentGrid Sparse(WorldConfig config, byte[] fineOpen)
    {
        ArgumentNullException.ThrowIfNull(config);
        int count = 0;
        for (int i = 0; i < config.TileCount; i++) if (fineOpen[i] != 0) count++;
        var tiles = new int[count];
        var tables = new int[]?[config.ChunkCount];
        int chunkArea = config.ChunkSize * config.ChunkSize, n = 0;
        for (int y = 0; y < config.Height; y++)
        for (int x = 0; x < config.Width; x++)
        {
            int i = y * config.Width + x;
            if (fineOpen[i] == 0) continue;
            int chunk = (y >> config.ChunkShift) * config.ChunksX + (x >> config.ChunkShift);
            var table = tables[chunk];
            if (table is null)
            {
                table = tables[chunk] = new int[chunkArea];
                Array.Fill(table, -1);
            }
            table[((y & config.ChunkMask) << config.ChunkShift) | (x & config.ChunkMask)] = n;
            tiles[n++] = i;
        }
        var grid = new ScentGrid(config, tiles, new int[count * 4], tables);
        var neighbors = grid._neighbors!;
        for (int e = 0; e < count; e++)
        {
            int x = tiles[e] % config.Width, y = tiles[e] / config.Width;
            neighbors[4 * e] = grid.IndexOf(x - 1, y);
            neighbors[4 * e + 1] = grid.IndexOf(x + 1, y);
            neighbors[4 * e + 2] = grid.IndexOf(x, y - 1);
            neighbors[4 * e + 3] = grid.IndexOf(x, y + 1);
        }
        return grid;
    }

    /// <summary>모든 칸이 열린 원래 해상도 표시(빈 월드와 같음).</summary>
    internal static byte[] AllOpen(WorldConfig config)
    {
        var open = new byte[config.TileCount];
        Array.Fill(open, (byte)1);
        return open;
    }

    /// <summary>월드 레이어의 원래 해상도 열린 칸 표시(1 = 고체 아님).</summary>
    internal static byte[] OpenMask(WorldLayer layer)
    {
        var config = layer.Config;
        var open = new byte[config.TileCount];
        var row = new byte[config.Width];
        for (int y = 0; y < config.Height; y++)
        {
            layer.CopyRow(TilePlane.Solid, y, row);
            int dst = y * config.Width;
            for (int x = 0; x < row.Length; x++) open[dst + x] = row[x] == 0 ? (byte)1 : (byte)0;
        }
        return open;
    }

    /// <summary>같은 열린 칸 표시로 만든 배치인가(값을 옮길 필요가 없는가).</summary>
    internal bool SameAs(ScentGrid other)
    {
        if (IsSparse != other.IsSparse || CellSize != other.CellSize || Length != other.Length) return false;
        return IsSparse ? _tiles.AsSpan().SequenceEqual(other._tiles) : _cellOpen.AsSpan().SequenceEqual(other._cellOpen);
    }
}
