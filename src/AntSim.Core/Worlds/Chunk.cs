namespace AntSim.Core.Worlds;

/// <summary>
/// 한 레이어의 정사각형 영역(ChunkSize x ChunkSize)에 대한 타일/블록 데이터.
/// 데이터는 종류별 연속 배열(SoA)로 저장되며 행 간격(stride)은 항상 ChunkSize이다.
/// 가장자리 청크는 <see cref="ValidWidth"/> x <see cref="ValidHeight"/> 영역만 의미가 있다.
/// </summary>
public sealed class Chunk
{
    private readonly ushort[] _tiles;
    private readonly ushort[] _blocks;
    private readonly int _shift;

    internal Chunk(ChunkCoord coord, WorldConfig config, ushort fillTile, ushort fillBlock)
    {
        Coord = coord;
        Size = config.ChunkSize;
        _shift = config.ChunkShift;
        OriginX = coord.X << _shift;
        OriginY = coord.Y << _shift;
        ValidWidth = Math.Min(Size, config.Width - OriginX);
        ValidHeight = Math.Min(Size, config.Height - OriginY);

        _tiles = new ushort[Size * Size];
        _blocks = new ushort[Size * Size];
        if (fillTile != 0) Array.Fill(_tiles, fillTile);
        if (fillBlock != 0) Array.Fill(_blocks, fillBlock);
    }

    public ChunkCoord Coord { get; }
    public int Size { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public int ValidWidth { get; }
    public int ValidHeight { get; }

    /// <summary>내용이 바뀔 때마다 증가한다. 렌더러/캐시가 변경 여부를 판단하는 데 쓴다.</summary>
    public uint Version { get; private set; }

    /// <summary>마지막으로 더티 목록을 수집한 이후 변경되었는지 여부.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>청크 로컬 타일 배열(행 우선, stride = Size). 읽기 전용 뷰.</summary>
    public ReadOnlySpan<ushort> Tiles => _tiles;

    /// <summary>청크 로컬 블록 배열(행 우선, stride = Size). 읽기 전용 뷰.</summary>
    public ReadOnlySpan<ushort> Blocks => _blocks;

    /// <summary>청크 로컬 y행의 유효 타일 구간.</summary>
    public ReadOnlySpan<ushort> TileRow(int localY) => new(_tiles, localY << _shift, ValidWidth);

    /// <summary>청크 로컬 y행의 유효 블록 구간.</summary>
    public ReadOnlySpan<ushort> BlockRow(int localY) => new(_blocks, localY << _shift, ValidWidth);

    internal ushort GetTile(int localX, int localY) => _tiles[(localY << _shift) | localX];

    internal ushort GetBlock(int localX, int localY) => _blocks[(localY << _shift) | localX];

    internal bool SetTile(int localX, int localY, ushort value) => Set(_tiles, (localY << _shift) | localX, value);

    internal bool SetBlock(int localX, int localY, ushort value) => Set(_blocks, (localY << _shift) | localX, value);

    internal void ClearDirty() => IsDirty = false;

    private bool Set(ushort[] array, int index, ushort value)
    {
        if (array[index] == value) return false;
        array[index] = value;
        Version++;
        IsDirty = true;
        return true;
    }
}
