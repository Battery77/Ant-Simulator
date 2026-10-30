namespace AntSim.Core.Worlds;

/// <summary>새 월드 생성 프리셋.</summary>
public static class WorldPresets
{
    /// <summary>두 레이어 모두 흙 지형, 블록 없음. 테스트와 다른 프리셋의 바탕으로 쓴다.</summary>
    public static World Empty(WorldConfig config, WorldRules? rules = null) => new(config, rules);

    /// <summary>
    /// 기본 프리셋. 지상: 흙, 블록 없음 / 개미집: 흙 지형, 모든 타일에 토양 블록.
    /// 개미집 공간은 통로 생성(굴착)으로 연다.
    /// </summary>
    public static World StandardNest(WorldConfig config, WorldRules? rules = null)
    {
        var world = new World(config, rules);
        world.Nest.FillUnchecked(TilePlane.Solid, (byte)SolidBlock.Soil);
        foreach (var layer in world.Layers) layer.ClearAllDirty();
        return world;
    }
}
