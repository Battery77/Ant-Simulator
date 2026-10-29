namespace AntSim.Core.Worlds;

/// <summary>타일의 지형. 저장 형식에 그대로 기록되므로 값을 바꾸지 말 것(추가만 가능).</summary>
public enum Terrain : byte
{
    Dirt = 0,
    Water = 1,
}

/// <summary>타일을 독점하는 고체 블록. 고체 블록이 있으면 수량 블록과 공존할 수 없다.</summary>
public enum SolidBlock : byte
{
    None = 0,
    Soil = 1,
    Rock = 2,
}

/// <summary>수량을 갖는 블록 종류. 한 타일에 여러 종류가 함께 있을 수 있다.</summary>
public enum QuantityKind : byte
{
    Food = 0,
    Larva = 1,
}

/// <summary>
/// 타일 속성 평면. 청크는 평면마다 별도의 연속 byte 배열을 가진다(SoA).
/// 저장 형식과 해시가 이 순서를 사용한다.
/// </summary>
public enum TilePlane : byte
{
    Terrain = 0,
    Solid = 1,
    Food = 2,
    Larva = 3,
}

public static class TileEnums
{
    public const int PlaneCount = 4;
    public const int QuantityKindCount = 2;

    public static bool IsDefined(Terrain terrain) => terrain <= Terrain.Water;
    public static bool IsDefined(SolidBlock solid) => solid <= SolidBlock.Rock;
    public static bool IsDefined(QuantityKind kind) => kind <= QuantityKind.Larva;

    public static TilePlane ToPlane(this QuantityKind kind) => kind switch
    {
        QuantityKind.Food => TilePlane.Food,
        QuantityKind.Larva => TilePlane.Larva,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>한 타일의 전체 상태. 편집 검증, Undo 기록, 타일 정보 조회에 쓰는 값 타입.</summary>
public readonly record struct TileState(Terrain Terrain, SolidBlock Solid, byte Food, byte Larva)
{
    public bool HasSolid => Solid != SolidBlock.None;

    public int TotalQuantity => Food + Larva;

    public byte GetQuantity(QuantityKind kind) => kind switch
    {
        QuantityKind.Food => Food,
        QuantityKind.Larva => Larva,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public TileState WithQuantity(QuantityKind kind, byte value) => kind switch
    {
        QuantityKind.Food => this with { Food = value },
        QuantityKind.Larva => this with { Larva = value },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
