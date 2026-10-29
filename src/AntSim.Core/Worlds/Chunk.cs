namespace AntSim.Core.Worlds;

/// <summary>
/// 한 레이어의 정사각형 영역(ChunkSize x ChunkSize)에 대한 타일 데이터.
/// 속성(<see cref="TilePlane"/>)마다 별도의 연속 byte 배열을 두며 행 간격(stride)은 항상 ChunkSize이다.
/// 가장자리 청크는 <see cref="ValidWidth"/> x <see cref="ValidHeight"/> 영역만 의미가 있다.
/// </summary>
public sealed class Chunk
{
    private readonly byte[][] _planes;
    private readonly int _shift;

    internal Chunk(ChunkCoord coord, WorldConfig config)
    {
        Coord = coord;
        Size = config.ChunkSize;
        _shift = config.ChunkShift;
        OriginX = coord.X << _shift;
        OriginY = coord.Y << _shift;
        ValidWidth = Math.Min(Size, config.Width - OriginX);
        ValidHeight = Math.Min(Size, config.Height - OriginY);

        _planes = new byte[TileEnums.PlaneCount][];
        for (int i = 0; i < _planes.Length; i++) _planes[i] = new byte[Size * Size];
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

    /// <summary>청크 로컬 속성 배열(행 우선, stride = Size). 읽기 전용 뷰.</summary>
    public ReadOnlySpan<byte> Plane(TilePlane plane) => _planes[(int)plane];

    /// <summary>청크 로컬 y행의 유효 구간.</summary>
    public ReadOnlySpan<byte> Row(TilePlane plane, int localY) => new(_planes[(int)plane], localY << _shift, ValidWidth);

    internal Span<byte> MutableRow(TilePlane plane, int localY) => new(_planes[(int)plane], localY << _shift, ValidWidth);

    internal TileState GetState(int localX, int localY)
    {
        int i = (localY << _shift) | localX;
        return new TileState(
            (Terrain)_planes[(int)TilePlane.Terrain][i],
            (SolidBlock)_planes[(int)TilePlane.Solid][i],
            _planes[(int)TilePlane.Food][i],
            _planes[(int)TilePlane.Larva][i]);
    }

    /// <summary>검증 없이 상태를 쓴다. 값이 바뀌었으면 true.</summary>
    internal bool SetState(int localX, int localY, in TileState state)
    {
        int i = (localY << _shift) | localX;
        bool changed = false;
        changed |= Write(TilePlane.Terrain, i, (byte)state.Terrain);
        changed |= Write(TilePlane.Solid, i, (byte)state.Solid);
        changed |= Write(TilePlane.Food, i, state.Food);
        changed |= Write(TilePlane.Larva, i, state.Larva);
        if (changed) MarkDirty();
        return changed;
    }

    /// <summary>타일 데이터 외의 변경(통로 등)으로 다시 그려야 할 때 호출한다.</summary>
    internal void MarkDirty()
    {
        Version++;
        IsDirty = true;
    }

    internal void ClearDirty() => IsDirty = false;

    private bool Write(TilePlane plane, int index, byte value)
    {
        ref byte slot = ref _planes[(int)plane][index];
        if (slot == value) return false;
        slot = value;
        return true;
    }
}
