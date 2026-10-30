using System.Text;
using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

/// <summary>편집기 UI 표시 문자열. Godot 없이 테스트할 수 있도록 코어에 둔다.</summary>
public static class EditorText
{
    public static string Name(LayerId layer) => layer switch
    {
        LayerId.Surface => "지상",
        LayerId.Nest => "개미집",
        _ => layer.ToString(),
    };

    public static string Name(Terrain terrain) => terrain switch
    {
        Terrain.Dirt => "흙",
        Terrain.Water => "물",
        _ => terrain.ToString(),
    };

    public static string Name(SolidBlock solid) => solid switch
    {
        SolidBlock.None => "없음",
        SolidBlock.Soil => "토양",
        SolidBlock.Rock => "바위",
        _ => solid.ToString(),
    };

    public static string Name(QuantityKind kind) => kind switch
    {
        QuantityKind.Food => "먹이",
        QuantityKind.Larva => "유충",
        _ => kind.ToString(),
    };

    public static string Describe(WorldError error) => error switch
    {
        WorldError.None => "성공",
        WorldError.OutOfBounds => "맵 범위를 벗어났습니다",
        WorldError.InvalidLayer => "잘못된 레이어입니다",
        WorldError.InvalidOperation => "알 수 없는 편집 연산입니다",
        WorldError.InvalidTerrain => "잘못된 지형입니다",
        WorldError.InvalidSolid => "잘못된 고체 블록입니다",
        WorldError.InvalidQuantityKind => "잘못된 수량 블록 종류입니다",
        WorldError.SolidOccupied => "고체 블록은 다른 블록과 공존할 수 없습니다",
        WorldError.QuantityLimitExceeded => "타일의 먹이·유충 합계가 상한을 넘습니다",
        WorldError.NegativeQuantity => "수량은 0보다 작아질 수 없습니다",
        WorldError.PassageBlockedBySolid => "지상 타일에 고체 블록이 있어 통로를 만들 수 없습니다",
        WorldError.PassageAreaOutOfBounds => "맵 가장자리라 개미집 3×3 굴착 공간이 들어가지 않습니다",
        _ => error.ToString(),
    };

    /// <summary>타일 정보 조회 패널 내용.</summary>
    public static string DescribeTile(World world, LayerId layer, int x, int y)
    {
        if (!world.Config.InBounds(x, y)) return $"({x}, {y}) 맵 밖";
        var state = world.GetLayer(layer).GetState(x, y);
        var sb = new StringBuilder();
        sb.Append('(').Append(x).Append(", ").Append(y).Append(") ").Append(Name(layer)).Append('\n');
        sb.Append("지형: ").Append(Name(state.Terrain)).Append('\n');
        sb.Append("고체 블록: ").Append(Name(state.Solid)).Append('\n');
        sb.Append("먹이: ").Append(state.Food).Append(", 유충: ").Append(state.Larva)
          .Append(" (합계 ").Append(state.TotalQuantity).Append('/').Append(world.Rules.MaxQuantityPerTile).Append(")\n");
        sb.Append("통로: ").Append(world.Passages.Contains(x, y) ? "있음" : "없음");
        return sb.ToString();
    }
}
