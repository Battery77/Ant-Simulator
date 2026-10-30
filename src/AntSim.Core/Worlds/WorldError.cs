namespace AntSim.Core.Worlds;

/// <summary>월드 규칙 위반 또는 편집 실패 사유.</summary>
public enum WorldError : byte
{
    None = 0,
    OutOfBounds,
    InvalidLayer,
    InvalidOperation,
    InvalidTerrain,
    InvalidSolid,
    InvalidQuantityKind,
    /// <summary>고체 블록이 있는 타일에 수량 블록을 두려 함(또는 그 반대).</summary>
    SolidOccupied,
    /// <summary>한 타일의 수량 합계가 상한을 넘음.</summary>
    QuantityLimitExceeded,
    /// <summary>수량이 0 미만이 됨.</summary>
    NegativeQuantity,
    /// <summary>통로를 놓을 지상 중심 타일에 고체 블록이 있음.</summary>
    PassageBlockedBySolid,
    /// <summary>통로의 개미집 굴착 영역(3×3)이 맵 안에 완전히 들어가지 않음.</summary>
    PassageAreaOutOfBounds,
}
