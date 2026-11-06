using AntSim.Core.Worlds;

namespace AntSim.Core.Ants;

/// <summary>
/// 개미 이동 판정. 개미는 점으로 취급하며, 외형 크기는 판정에 쓰지 않는다.
/// 타일 조회는 청크 기반 <see cref="WorldLayer.GetState"/>로 O(1)이며 맵 전체를 훑지 않는다.
/// </summary>
public static class AntMovement
{
    /// <summary>
    /// 이동 능력 <paramref name="mobility"/>로 타일에 들어갈 수 있는가.
    /// 걷기: 맵 안, 고체 블록(토양·바위) 없음, 물이 아님. 먹이·유충 타일은 통과 가능.
    /// </summary>
    public static bool IsPassable(WorldLayer layer, int tileX, int tileY, AntMobility mobility = AntMobility.Walk)
    {
        if (!layer.Config.InBounds(tileX, tileY)) return false;
        var state = layer.GetState(tileX, tileY);
        if (state.HasSolid) return false;
        // 이후 수영 능력이 생기면 여기서 물 통과 여부를 능력 플래그로 판단한다.
        return state.Terrain != Terrain.Water;
    }

    public static bool IsPassable(WorldLayer layer, float x, float y, AntMobility mobility = AntMobility.Walk) =>
        IsPassable(layer, (int)MathF.Floor(x), (int)MathF.Floor(y), mobility);

    /// <summary>
    /// (x, y)에서 (x+dx, y+dy)로 직선 이동할 때 지나는 모든 타일을 차례로 검사한다(격자 DDA).
    /// 빠른 개미가 한 번의 갱신에서 얇은 벽을 건너뛰지 않도록, 끝점만이 아니라 경로 전체를 본다.
    /// 반환값은 막히기 전까지 이동할 수 있는 비율(0~1)이며 막힌 타일에 들어가기 직전에서 멈춘다.
    /// 대각선이 타일 모서리를 정확히 지나면 두 축 중 한 칸을 먼저 거치므로 모서리 틈으로 빠져나가지 못한다.
    /// </summary>
    public static float Sweep(WorldLayer layer, float x, float y, float dx, float dy, AntMobility mobility, out bool blocked)
    {
        blocked = false;
        int tx = (int)MathF.Floor(x), ty = (int)MathF.Floor(y);
        if (dx == 0 && dy == 0) return 1;

        int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
        float tDeltaX = dx != 0 ? 1 / MathF.Abs(dx) : float.PositiveInfinity;
        float tDeltaY = dy != 0 ? 1 / MathF.Abs(dy) : float.PositiveInfinity;
        float tMaxX = dx > 0 ? (tx + 1 - x) / dx : dx < 0 ? (x - tx) / -dx : float.PositiveInfinity;
        float tMaxY = dy > 0 ? (ty + 1 - y) / dy : dy < 0 ? (y - ty) / -dy : float.PositiveInfinity;

        while (true)
        {
            float tEnter;
            if (tMaxX <= tMaxY)
            {
                tEnter = tMaxX;
                if (tEnter > 1) return 1;
                tx += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                tEnter = tMaxY;
                if (tEnter > 1) return 1;
                ty += stepY;
                tMaxY += tDeltaY;
            }

            if (!IsPassable(layer, tx, ty, mobility))
            {
                blocked = true;
                // 경계 바로 앞(현재 타일 안쪽)에서 멈춘다.
                float length = MathF.Sqrt(dx * dx + dy * dy);
                return MathF.Max(0, tEnter - BoundaryMargin / length);
            }
        }
    }

    /// <summary>막힌 타일 경계에서 떨어져 멈추는 거리(타일).</summary>
    public const float BoundaryMargin = 1e-3f;
}
