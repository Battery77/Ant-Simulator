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

    private const int BlockSize = 2048;

    // 정규화 값 t를 256단계로 양자화한 색 조회표. 0번은 투명(농도 0), t > 0은 1~255.
    private static readonly uint[] Lut = BuildLut();

    public static float ComputeMax(ReadOnlySpan<float> field)
    {
        float max = 0;
        foreach (float v in field)
            if (v > max) max = v;
        return max;
    }

    /// <summary>농도를 0~1로 정규화한다. 최대값이 0 이하이면 0. 로그는 log(1+x)를 아주 작은 x에서도 정확하게 계산한다.</summary>
    public static float Normalize(float value, float max, HeatmapScale scale)
    {
        if (!(value > 0) || !(max > 0)) return 0;
        double t = scale == HeatmapScale.Log ? Log1P(value) / Log1P(max) : (double)value / max;
        return double.IsNaN(t) ? 1f : (float)Math.Clamp(t, 0.0, 1.0); // NaN은 value, max가 모두 +∞일 때만 생긴다
    }

    /// <summary>
    /// log(1 + x) (x ≥ 0). float로 1 + x를 계산하면 x가 작을 때 1로 반올림되어 0이 되므로(0/0 = NaN의 원인),
    /// double에서 보정식 log(u)·x/(u−1) (u = 1 + x)을 쓴다. u가 1로 반올림될 만큼 작으면 log(1+x) ≈ x.
    /// </summary>
    internal static double Log1P(double x)
    {
        if (double.IsPositiveInfinity(x)) return x;
        double u = 1.0 + x;
        return u == 1.0 ? x : Math.Log(u) * x / (u - 1.0);
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

    /// <summary>정규화 값을 조회표 색으로 바꾼다(0은 투명, 0보다 크면 최소 1단계).</summary>
    public static uint LutColor(float t) => t > 0 ? Lut[QuantizeNormalized(t)] : 0;

    /// <summary>정규화 값(0 &lt; t ≤ 1)을 조회표 인덱스 1~255로 바꾼다. 아주 작은 양수도 1단계로 보이게 한다.</summary>
    public static int QuantizeNormalized(float t) => Math.Clamp((int)(t * 255 + 0.5f), 1, 255);

    /// <summary>농도 → 조회표 인덱스(0 = 표시 안 함, 1~255). <see cref="HeatmapQuantizer"/>와 같은 계산.</summary>
    public static int Quantize(float value, float max, HeatmapScale scale) => new HeatmapQuantizer(max, scale).Quantize(value);

    public static void Rasterize(ReadOnlySpan<float> field, float max, HeatmapScale scale, Span<byte> rgba)
    {
        if (rgba.Length < field.Length * 4) throw new ArgumentException("픽셀 버퍼가 너무 작습니다.", nameof(rgba));
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba);
        if (!(max > 0))
        {
            pixels[..field.Length].Clear();
            return;
        }
        var quantizer = new HeatmapQuantizer(max, scale);
        Span<byte> indices = stackalloc byte[BlockSize];
        for (int start = 0; start < field.Length; start += BlockSize)
        {
            int count = Math.Min(BlockSize, field.Length - start);
            var block = indices[..count];
            quantizer.QuantizeTo(field.Slice(start, count), block);
            var target = pixels.Slice(start, count);
            for (int i = 0; i < block.Length; i++) target[i] = Lut[block[i]];
        }
    }

    private static uint[] BuildLut()
    {
        var lut = new uint[256]; // lut[0] = 0 (투명)
        for (int i = 1; i < 256; i++) lut[i] = Color(i / 255f);
        return lut;
    }
}

/// <summary>
/// 농도 → 0~255 조회표 인덱스 변환기. 최대값·스케일에 따른 상수를 한 번만 계산한다(로그의 log(1+max) 포함).
/// 단일·복합 히트맵이 같은 변환을 쓰므로 같은 농도는 항상 같은 단계로 표시된다.
/// 0 이하 또는 최대값이 0 이하면 0(표시 안 함), 양수는 최소 1, 최대값 이상은 255.
/// </summary>
public readonly struct HeatmapQuantizer
{
    private readonly float _factor;     // 선형: 255 / max (float, 기존 계산 그대로)
    private readonly double _logFactor; // 로그: 255 / log1p(max) (double: 작은 max에서도 유한)
    private readonly bool _log;

    public HeatmapQuantizer(float max, HeatmapScale scale)
    {
        _log = scale == HeatmapScale.Log;
        bool usable = max > 0 && !float.IsInfinity(max);
        _factor = usable && !_log ? 255f / max : 0;
        _logFactor = usable && _log ? 255.0 / HeatmapRasterizer.Log1P(max) : 0;
        if (_log && usable) _factor = 1; // IsEmpty 판정용 표식(로그 계산에는 쓰지 않음)
    }

    /// <summary>표시할 수 없는 최대값(0 이하)이면 true. 이 채널은 아무 칸에도 기여하지 않는다.</summary>
    public bool IsEmpty => _factor == 0;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int Quantize(float value)
    {
        if (!(value > 0) || _factor == 0) return 0;
        if (_log)
        {
            // value = max → 정확히 255.5 근처 → 255. 0 < value < max → 1~255. 비유한 값은 정수 변환에 넘기지 않는다.
            double y = HeatmapRasterizer.Log1P(value) * _logFactor + 0.5;
            return !(y < 255) ? 255 : y < 1 ? 1 : (int)y;
        }
        float x = value * _factor + 0.5f;
        return x >= 255 ? 255 : x < 1 ? 1 : (int)x;
    }

    /// <summary>
    /// 여러 값을 한 번에 변환한다. 선형 스케일은 SIMD로 처리하며 결과는 <see cref="Quantize"/>와 비트 단위로 같다.
    /// </summary>
    public void QuantizeTo(ReadOnlySpan<float> values, Span<byte> indices)
    {
        if (indices.Length < values.Length) throw new ArgumentException("대상 버퍼가 너무 작습니다.", nameof(indices));
        if (_factor == 0)
        {
            indices[..values.Length].Clear();
            return;
        }

        int i = 0;
        if (!_log && System.Numerics.Vector.IsHardwareAccelerated)
        {
            int width = System.Numerics.Vector<float>.Count;
            var factor = new System.Numerics.Vector<float>(_factor);
            var half = new System.Numerics.Vector<float>(0.5f);
            var one = System.Numerics.Vector<float>.One;
            var top = new System.Numerics.Vector<float>(255f);
            for (; i + 4 * width <= values.Length; i += 4 * width)
            {
                var a = System.Numerics.Vector.Narrow(Lanes(values.Slice(i)), Lanes(values.Slice(i + width)));
                var b = System.Numerics.Vector.Narrow(Lanes(values.Slice(i + 2 * width)), Lanes(values.Slice(i + 3 * width)));
                System.Numerics.Vector.AsVectorByte(System.Numerics.Vector.Narrow(a, b)).CopyTo(indices.Slice(i));
            }

            // v > 0 인 칸만: clamp(v·f + 0.5, 1, 255)를 버림 변환. NaN·0 이하는 0.
            System.Numerics.Vector<int> Lanes(ReadOnlySpan<float> source)
            {
                var v = new System.Numerics.Vector<float>(source);
                var x = System.Numerics.Vector.Min(System.Numerics.Vector.Max(v * factor + half, one), top);
                return System.Numerics.Vector.ConvertToInt32(x) & System.Numerics.Vector.GreaterThan(v, System.Numerics.Vector<float>.Zero);
            }
        }
        for (; i < values.Length; i++) indices[i] = (byte)Quantize(values[i]);
    }
}
