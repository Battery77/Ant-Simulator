using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

public enum EditorToolKind : byte
{
    /// <summary>편집하지 않고 타일 정보만 조회.</summary>
    Inspect,
    PaintTerrain,
    PlaceSolid,
    ClearBlocks,
    AddQuantity,
    RemoveQuantity,
    AddPassage,
    RemovePassage,
}

/// <summary>편집기 UI에서 선택한 도구. 한 타일에 적용할 <see cref="EditOp"/>로 변환된다.</summary>
public readonly record struct EditorTool(
    EditorToolKind Kind,
    Terrain Terrain = Terrain.Dirt,
    SolidBlock Solid = SolidBlock.Soil,
    QuantityKind Quantity = QuantityKind.Food,
    int Amount = 1)
{
    public bool Edits => Kind != EditorToolKind.Inspect;

    /// <summary>도구를 (layer, x, y)에 적용하는 연산. 조회 도구면 false.</summary>
    public bool TryCreateOp(LayerId layer, int x, int y, out EditOp op)
    {
        op = Kind switch
        {
            EditorToolKind.PaintTerrain => EditOp.SetTerrain(layer, x, y, Terrain),
            EditorToolKind.PlaceSolid => EditOp.PlaceSolid(layer, x, y, Solid),
            EditorToolKind.ClearBlocks => EditOp.ClearBlocks(layer, x, y),
            EditorToolKind.AddQuantity => EditOp.AdjustQuantity(layer, x, y, Quantity, Amount),
            EditorToolKind.RemoveQuantity => EditOp.AdjustQuantity(layer, x, y, Quantity, -Amount),
            EditorToolKind.AddPassage => EditOp.AddPassage(x, y),
            EditorToolKind.RemovePassage => EditOp.RemovePassage(x, y),
            _ => default,
        };
        return Edits;
    }
}
