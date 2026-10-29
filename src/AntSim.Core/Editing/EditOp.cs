using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

public enum EditOpKind : byte
{
    SetTerrain,
    PlaceSolid,
    /// <summary>고체 블록만 제거. 없으면 변경 없음.</summary>
    RemoveSolid,
    /// <summary>고체·수량 블록을 모두 제거.</summary>
    ClearBlocks,
    AdjustQuantity,
    SetQuantity,
    AddPassage,
    RemovePassage,
}

/// <summary>
/// 단일 편집 연산. 데이터로만 이루어져 있어 큐에 담아 다른 스레드(시뮬레이션 틱 경계)에서 적용하거나 기록할 수 있다.
/// 통로 연산은 <see cref="Layer"/>를 무시한다(두 레이어를 함께 연결).
/// </summary>
public readonly record struct EditOp(
    EditOpKind Kind,
    LayerId Layer,
    int X,
    int Y,
    Terrain Terrain = Terrain.Dirt,
    SolidBlock Solid = SolidBlock.None,
    QuantityKind Quantity = QuantityKind.Food,
    int Amount = 0)
{
    public static EditOp SetTerrain(LayerId layer, int x, int y, Terrain terrain) =>
        new(EditOpKind.SetTerrain, layer, x, y, Terrain: terrain);

    public static EditOp PlaceSolid(LayerId layer, int x, int y, SolidBlock solid) =>
        new(EditOpKind.PlaceSolid, layer, x, y, Solid: solid);

    public static EditOp RemoveSolid(LayerId layer, int x, int y) => new(EditOpKind.RemoveSolid, layer, x, y);

    public static EditOp ClearBlocks(LayerId layer, int x, int y) => new(EditOpKind.ClearBlocks, layer, x, y);

    public static EditOp AdjustQuantity(LayerId layer, int x, int y, QuantityKind kind, int delta) =>
        new(EditOpKind.AdjustQuantity, layer, x, y, Quantity: kind, Amount: delta);

    public static EditOp SetQuantity(LayerId layer, int x, int y, QuantityKind kind, int value) =>
        new(EditOpKind.SetQuantity, layer, x, y, Quantity: kind, Amount: value);

    public static EditOp AddPassage(int x, int y) => new(EditOpKind.AddPassage, LayerId.Surface, x, y);

    public static EditOp RemovePassage(int x, int y) => new(EditOpKind.RemovePassage, LayerId.Surface, x, y);

    public bool IsPassageOp => Kind is EditOpKind.AddPassage or EditOpKind.RemovePassage;
}
