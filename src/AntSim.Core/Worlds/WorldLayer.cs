using System.Runtime.InteropServices;
using AntSim.Core.Content;
using AntSim.Core.Util;

namespace AntSim.Core.Worlds;

/// <summary>
/// 하나의 월드 레이어(지상 또는 개미집). 청크 격자로 타일과 블록을 보관한다.
/// 모든 쓰기 연산은 타입 ID의 유효성을 검사하고, 값이 실제로 바뀐 청크만 더티로 표시한다.
/// </summary>
public sealed class WorldLayer
{
    private readonly Chunk[] _chunks;
    private readonly TypeRegistry _tileTypes;
    private readonly TypeRegistry _blockTypes;

    internal WorldLayer(LayerId id, WorldConfig config, TypeRegistry tileTypes, TypeRegistry blockTypes, ushort fillTile)
    {
        Id = id;
        Config = config;
        _tileTypes = tileTypes;
        _blockTypes = blockTypes;
        _chunks = new Chunk[config.ChunkCount];
        for (int cy = 0; cy < config.ChunksY; cy++)
        for (int cx = 0; cx < config.ChunksX; cx++)
            _chunks[cy * config.ChunksX + cx] = new Chunk(new ChunkCoord(cx, cy), config, fillTile, TypeRegistry.EmptyId);
    }

    public LayerId Id { get; }
    public WorldConfig Config { get; }

    /// <summary>청크 목록(행 우선 순서). 순회 순서는 항상 결정적이다.</summary>
    public IReadOnlyList<Chunk> Chunks => _chunks;

    public Chunk GetChunk(ChunkCoord coord)
    {
        if ((uint)coord.X >= (uint)Config.ChunksX || (uint)coord.Y >= (uint)Config.ChunksY)
            throw new ArgumentOutOfRangeException(nameof(coord), coord, "청크 좌표가 월드 범위를 벗어났습니다.");
        return _chunks[coord.Y * Config.ChunksX + coord.X];
    }

    public ushort GetTile(int x, int y) => ChunkAt(x, y).GetTile(x & Config.ChunkMask, y & Config.ChunkMask);

    public ushort GetBlock(int x, int y) => ChunkAt(x, y).GetBlock(x & Config.ChunkMask, y & Config.ChunkMask);

    /// <summary>타일을 설정한다. 값이 바뀌었으면 true.</summary>
    public bool SetTile(int x, int y, ushort tileId)
    {
        if (!_tileTypes.Contains(tileId))
            throw new ArgumentOutOfRangeException(nameof(tileId), tileId, "등록되지 않은 타일 타입입니다.");
        return ChunkAt(x, y).SetTile(x & Config.ChunkMask, y & Config.ChunkMask, tileId);
    }

    /// <summary>블록을 설정한다(<see cref="TypeRegistry.EmptyId"/>는 블록 없음). 값이 바뀌었으면 true.</summary>
    public bool SetBlock(int x, int y, ushort blockId)
    {
        if (!_blockTypes.Contains(blockId))
            throw new ArgumentOutOfRangeException(nameof(blockId), blockId, "등록되지 않은 블록 타입입니다.");
        return ChunkAt(x, y).SetBlock(x & Config.ChunkMask, y & Config.ChunkMask, blockId);
    }

    /// <summary>
    /// 마지막 수집 이후 변경된 청크를 행 우선 순서로 <paramref name="output"/>에 추가하고 더티 표시를 지운다.
    /// </summary>
    public int CollectDirtyChunks(List<Chunk> output)
    {
        int count = 0;
        foreach (var chunk in _chunks)
        {
            if (!chunk.IsDirty) continue;
            chunk.ClearDirty();
            output.Add(chunk);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 레이어 내용의 64비트 해시. 월드 좌표 행 우선 순서로 계산하므로 청크 크기와 무관하다.
    /// 재현성 검증(같은 입력 → 같은 해시)에 사용한다.
    /// </summary>
    public ulong ComputeContentHash()
    {
        var hash = Fnv1a64.Create();
        hash.Add((byte)Id);
        hash.Add(Config.Width);
        hash.Add(Config.Height);
        HashPlane(ref hash, tiles: true);
        HashPlane(ref hash, tiles: false);
        return hash.Value;
    }

    private void HashPlane(ref Fnv1a64 hash, bool tiles)
    {
        for (int cy = 0; cy < Config.ChunksY; cy++)
        {
            int rowBase = cy * Config.ChunksX;
            int rows = _chunks[rowBase].ValidHeight;
            for (int ly = 0; ly < rows; ly++)
            for (int cx = 0; cx < Config.ChunksX; cx++)
            {
                var chunk = _chunks[rowBase + cx];
                var row = tiles ? chunk.TileRow(ly) : chunk.BlockRow(ly);
                hash.Add(MemoryMarshal.AsBytes(row));
            }
        }
    }

    private Chunk ChunkAt(int x, int y)
    {
        if (!Config.InBounds(x, y))
            throw new ArgumentOutOfRangeException(nameof(x), $"타일 좌표 ({x}, {y})가 월드 범위를 벗어났습니다.");
        return _chunks[(y >> Config.ChunkShift) * Config.ChunksX + (x >> Config.ChunkShift)];
    }
}
