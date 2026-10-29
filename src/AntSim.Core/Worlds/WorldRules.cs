namespace AntSim.Core.Worlds;

/// <summary>
/// 타일 상태 불변 조건. 편집기, 맵 불러오기, (이후) 시뮬레이션이 모두 이 검사를 공유한다.
/// 값은 맵 파일에 함께 저장된다.
/// </summary>
public sealed record WorldRules
{
    public const int DefaultMaxQuantityPerTile = 10;

    private readonly int _maxQuantityPerTile = DefaultMaxQuantityPerTile;

    public static WorldRules Default { get; } = new();

    /// <summary>한 타일의 먹이 + 유충 수량 합계 상한.</summary>
    public int MaxQuantityPerTile
    {
        get => _maxQuantityPerTile;
        init
        {
            if (value < 0 || value > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(MaxQuantityPerTile), value, "타일당 수량 상한은 0~255 범위여야 합니다.");
            _maxQuantityPerTile = value;
        }
    }

    public WorldError Validate(in TileState state)
    {
        if (!TileEnums.IsDefined(state.Terrain)) return WorldError.InvalidTerrain;
        if (!TileEnums.IsDefined(state.Solid)) return WorldError.InvalidSolid;
        if (state.HasSolid && state.TotalQuantity > 0) return WorldError.SolidOccupied;
        if (state.TotalQuantity > MaxQuantityPerTile) return WorldError.QuantityLimitExceeded;
        return WorldError.None;
    }
}
