using AntSim.Core.Worlds;

namespace AntSim.Core.Util;

public static class GridLine
{
    /// <summary>
    /// 두 타일 사이의 격자 선(브레젠험)을 시작점과 끝점을 포함해 <paramref name="output"/>에 추가한다.
    /// 연속한 두 점은 8방향으로 인접한다.
    /// </summary>
    public static void Rasterize(int x0, int y0, int x1, int y1, List<TileCoord> output)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            output.Add(new TileCoord(x0, y0));
            if (x0 == x1 && y0 == y1) return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
