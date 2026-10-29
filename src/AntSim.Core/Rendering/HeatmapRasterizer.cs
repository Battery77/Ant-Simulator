namespace AntSim.Core.Rendering;

public enum HeatmapScale : byte
{
    Linear,
    Log,
}

/// <summary>
/// 냄새 농도 필드 → RGBA8 히트맵. 기본은 선형 + 자동 최대값이며, 고정 최대값과 로그 스케일을 지원한다.
/// 농도 0 이하는 투명, 최대값 이상은 가장 밝은 색으로 고정된다.
/// </summary>
public static class HeatmapRasterizer
{
    // 어두운 보라 → 빨강 → 주황 → 노랑 (값이 클수록 밝고 불투명)
    private static readonly (float T, byte R, byte G, byte B)[] Stops =
    {
        (0.00f, 0x30, 0x0A, 0x60),
        (0.35f, 0xB0, 0x20, 0x60),
        (0.65f, 0xF0, 0x60, 0x20),
        (1.00f, 0xFF, 0xF0, 0x60),
    };

    public static float ComputeMax(ReadOnlySpan<float> field)
    {
        float max = 0;
        foreach (float v in field)
            if (v > max) max = v;
        return max;
    }

    /// <summary>농도를 0~1로 정규화한다. 최대값이 0 이하이면 0.</summary>
    public static float Normalize(float value, float max, HeatmapScale scale)
    {
        if (!(value > 0) || !(max > 0)) return 0;
        float t = scale == HeatmapScale.Log ? MathF.Log(1 + value) / MathF.Log(1 + max) : value / max;
        return Math.Clamp(t, 0f, 1f);
    }

    public static uint Color(float t)
    {
        if (t <= 0) return 0;
        int s = 1;
        while (s < Stops.Length - 1 && t > Stops[s].T) s++;
        var (t0, r0, g0, b0) = Stops[s - 1];
        var (t1, r1, g1, b1) = Stops[s];
        float f = Math.Clamp((t - t0) / (t1 - t0), 0f, 1f);
        byte r = (byte)(r0 + (r1 - r0) * f), g = (byte)(g0 + (g1 - g0) * f), b = (byte)(b0 + (b1 - b0) * f);
        byte a = (byte)(70 + 185 * t);
        return TilePalette.Rgba(r, g, b, a);
    }

    public static void Rasterize(ReadOnlySpan<float> field, float max, HeatmapScale scale, Span<byte> rgba)
    {
        if (rgba.Length < field.Length * 4) throw new ArgumentException("픽셀 버퍼가 너무 작습니다.", nameof(rgba));
        for (int i = 0; i < field.Length; i++)
        {
            uint c = Color(Normalize(field[i], max, scale));
            int o = i * 4;
            rgba[o] = (byte)c;
            rgba[o + 1] = (byte)(c >> 8);
            rgba[o + 2] = (byte)(c >> 16);
            rgba[o + 3] = (byte)(c >> 24);
        }
    }
}
