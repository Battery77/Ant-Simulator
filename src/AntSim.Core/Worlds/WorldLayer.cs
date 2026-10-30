using AntSim.Core.Util;

namespace AntSim.Core.Worlds;

/// <summary>
/// 하나의 월드 레이어(지상 또는 개미집). 청크 격자로 타일 속성을 보관한다.
/// 공개 쓰기 연산은 <see cref="WorldRules"/>로 검증하며, 값이 실제로 바뀐 청크만 더티로 표시한다.
/// </summary>
public sealed class WorldLayer
{
    private readonly Chunk[] _chunks;

    internal WorldLayer(LayerId id, WorldConfig config, WorldRules rules)
    {
        Id = id;
        Config = config;
        Rules = rules;
        _chunks = new Chunk[config.ChunkCount];
        for (int cy = 0; cy < config.ChunksY; cy++)
        for (int cx = 0; cx < config.ChunksX; cx++)
            _chunks[cy * config.ChunksX + cx] = new Chunk(new ChunkCoord(cx, cy), config);
    }

    public LayerId Id { get; }
    public WorldConfig Config { get; }
    public WorldRules Rules { get; }

    /// <summary>청크 목록(행 우선 순서). 순회 순서는 항상 결정적이다.</summary>
    public IReadOnlyList<Chunk> Chunks => _chunks;

    public Chunk GetChunk(ChunkCoord coord)
    {
        if ((uint)coord.X >= (uint)Config.ChunksX || (uint)coord.Y >= (uint)Config.ChunksY)
            throw new ArgumentOutOfRangeException(nameof(coord), coord, "청크 좌표가 월드 범위를 벗어났습니다.");
        return _chunks[coord.Y * Config.ChunksX + coord.X];
    }

    public Chunk GetChunkAt(int x, int y) => ChunkAt(x, y);

    public TileState GetState(int x, int y) => ChunkAt(x, y).GetState(x & Config.ChunkMask, y & Config.ChunkMask);

    public Terrain GetTerrain(int x, int y) => (Terrain)Read(TilePlane.Terrain, x, y);
    public SolidBlock GetSolid(int x, int y) => (SolidBlock)Read(TilePlane.Solid, x, y);
    public byte GetQuantity(QuantityKind kind, int x, int y) => Read(kind.ToPlane(), x, y);

    /// <summary>규칙을 검증한 뒤 타일 상태를 쓴다. 위반 시 아무것도 바꾸지 않고 사유를 반환한다.</summary>
    public WorldError TrySetState(int x, int y, in TileState state)
    {
        if (!Config.InBounds(x, y)) return WorldError.OutOfBounds;
        var error = Rules.Validate(state);
        if (error != WorldError.None) return error;
        SetStateUnchecked(x, y, state);
        return WorldError.None;
    }

    /// <summary>검증된 상태를 쓴다(편집기 커밋, Undo, 불러오기 전용). 값이 바뀌었으면 true.</summary>
    internal bool SetStateUnchecked(int x, int y, in TileState state) =>
        ChunkAt(x, y).SetState(x & Config.ChunkMask, y & Config.ChunkMask, state);

    internal void MarkDirty(int x, int y) => ChunkAt(x, y).MarkDirty();

    internal void FillUnchecked(TilePlane plane, byte value)
    {
        foreach (var chunk in _chunks) chunk.Fill(plane, value);
    }

    internal void CopyFrom(WorldLayer source)
    {
        for (int i = 0; i < _chunks.Length; i++) _chunks[i].CopyFrom(source._chunks[i]);
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

    /// <summary>모든 청크의 더티 표시를 지운다(새 월드를 통째로 다시 그린 직후 등).</summary>
    public void ClearAllDirty()
    {
        foreach (var chunk in _chunks) chunk.ClearDirty();
    }

    /// <summary>월드 좌표 y행의 한 속성을 <paramref name="destination"/>(길이 Width)에 복사한다.</summary>
    public void CopyRow(TilePlane plane, int y, Span<byte> destination)
    {
        if ((uint)y >= (uint)Config.Height) throw new ArgumentOutOfRangeException(nameof(y));
        if (destination.Length < Config.Width) throw new ArgumentException("대상 버퍼가 월드 너비보다 짧습니다.", nameof(destination));
        int cy = y >> Config.ChunkShift, ly = y & Config.ChunkMask;
        for (int cx = 0; cx < Config.ChunksX; cx++)
        {
            var chunk = _chunks[cy * Config.ChunksX + cx];
            chunk.Row(plane, ly).CopyTo(destination[chunk.OriginX..]);
        }
    }

    /// <summary>월드 좌표 y행의 한 속성을 검증 없이 덮어쓴다(불러오기 전용).</summary>
    internal void WriteRowUnchecked(TilePlane plane, int y, ReadOnlySpan<byte> source)
    {
        int cy = y >> Config.ChunkShift, ly = y & Config.ChunkMask;
        for (int cx = 0; cx < Config.ChunksX; cx++)
        {
            var chunk = _chunks[cy * Config.ChunksX + cx];
            source.Slice(chunk.OriginX, chunk.ValidWidth).CopyTo(chunk.MutableRow(plane, ly));
        }
    }

    /// <summary>
    /// 레이어 내용의 64비트 해시. 월드 좌표 행 우선 순서로 계산하므로 청크 크기와 무관하다.
    /// </summary>
    public ulong ComputeContentHash()
    {
        var hash = Fnv1a64.Create();
        hash.Add((byte)Id);
        hash.Add(Config.Width);
        hash.Add(Config.Height);
        Span<byte> row = Config.Width <= 4096 ? stackalloc byte[Config.Width] : new byte[Config.Width];
        for (int p = 0; p < TileEnums.PlaneCount; p++)
        for (int y = 0; y < Config.Height; y++)
        {
            CopyRow((TilePlane)p, y, row);
            hash.Add(row);
        }
        return hash.Value;
    }

    private byte Read(TilePlane plane, int x, int y) =>
        ChunkAt(x, y).Plane(plane)[((y & Config.ChunkMask) << Config.ChunkShift) | (x & Config.ChunkMask)];

    private Chunk ChunkAt(int x, int y)
    {
        if (!Config.InBounds(x, y))
            throw new ArgumentOutOfRangeException(nameof(x), $"타일 좌표 ({x}, {y})가 월드 범위를 벗어났습니다.");
        return _chunks[(y >> Config.ChunkShift) * Config.ChunksX + (x >> Config.ChunkShift)];
    }
}
