using System.Runtime.InteropServices;

namespace AntSim.Core.Rendering;

/// <summary>복합 히트맵의 색 합성 방식. 렌더링 단계에서만 쓰이며 농도 데이터는 바꾸지 않는다.</summary>
public enum CompositeBlendMode : byte
{
    /// <summary>
    /// 기본값. 색상 = 각 채널 색의 가중 평균(가중치 = 정규화 강도^지수), 불투명도 = 1 − Π(1 − αᵢ).
    /// 평균이므로 RGB가 넘치지 않고, 지수 &lt; 1이면 약한 채널도 비율보다 크게 드러난다.
    /// </summary>
    WeightedAverage,
    /// <summary>비교용. 각 채널 색을 불투명도만큼 더한 뒤 255로 자른다(겹치면 흰색 쪽으로 포화될 수 있음).</summary>
    AdditiveClamped,
}

/// <summary>합성 방식 설정.</summary>
/// <param name="WeightExponent">
/// 가중 평균의 강도 지수 γ (0 &lt; γ ≤ 1). 0.5면 강도 1과 0.1인 두 채널의 색 비중이 1 : 0.32가 되어
/// 약한 채널이 완전히 묻히지 않는다. 1이면 강도 비율 그대로.
/// </param>
public sealed record CompositeBlend(CompositeBlendMode Mode = CompositeBlendMode.WeightedAverage, float WeightExponent = 0.5f)
{
    public static CompositeBlend Default { get; } = new();
}

/// <summary>
/// 복합 히트맵의 한 채널 색 램프. 기본 색 하나에서 256단계 조회표(색·불투명도·가중치)를 미리 만든다.
/// 인덱스는 <see cref="HeatmapRasterizer.Quantize"/>와 같은 0~255 양자화를 쓴다.
/// </summary>
public sealed class HeatmapRamp
{
    private readonly byte[] _r = new byte[256], _g = new byte[256], _b = new byte[256];
    private readonly float[] _alpha = new float[256];

    /// <param name="color">가장 진할 때의 색(RGBA8, 알파 무시).</param>
    /// <param name="darkFactor">가장 옅을 때 기본 색에 곱하는 밝기(0~1).</param>
    public const byte DefaultMinAlpha = 20;
    public const byte DefaultMaxAlpha = 235;

    /// <param name="minAlpha">가장 옅은 단계의 불투명도. 낮을수록 옅은 냄새가 배경을 덜 가린다.</param>
    public HeatmapRamp(uint color, float darkFactor = 0.55f, byte minAlpha = DefaultMinAlpha, byte maxAlpha = DefaultMaxAlpha)
    {
        Color = color;
        byte r = (byte)color, g = (byte)(color >> 8), b = (byte)(color >> 16);
        for (int i = 1; i < 256; i++)
        {
            float t = i / 255f;
            float k = darkFactor + (1 - darkFactor) * t;
            _r[i] = (byte)(r * k + 0.5f);
            _g[i] = (byte)(g * k + 0.5f);
            _b[i] = (byte)(b * k + 0.5f);
            _alpha[i] = (minAlpha + (maxAlpha - minAlpha) * t) / 255f;
        }
    }

    public uint Color { get; }

    /// <summary>인덱스 i(1~255)의 RGBA8 색. 0은 투명.</summary>
    public uint ColorAt(int index) => index <= 0 ? 0 : TilePalette.Rgba(_r[index], _g[index], _b[index], (byte)(_alpha[index] * 255 + 0.5f));

    internal byte R(int i) => _r[i];
    internal byte G(int i) => _g[i];
    internal byte B(int i) => _b[i];
    internal float Alpha(int i) => _alpha[i];
}

/// <summary>복합 히트맵 채널 하나의 입력. 농도 배열은 읽기만 한다.</summary>
/// <param name="Max">이 채널의 표시 최대값(종류별 독립 정규화: 자동이면 그 종류의 현재 최대값, 아니면 고정값).</param>
public readonly record struct CompositeChannel(float[] Field, float Max, HeatmapScale Scale, HeatmapRamp Ramp);

/// <summary>
/// 여러 냄새 농도 필드를 한 장의 RGBA8 히트맵으로 합성한다. 농도는 종류별로 따로 정규화·양자화(단일 히트맵과 같은
/// <see cref="HeatmapQuantizer"/>)한 뒤 색 단계에서만 섞는다. 농도가 0인 채널은 기여하지 않고, 모든 채널이 0이면 투명이다.
/// 램프마다 256단계 조회표(가중 RGB, 1 − α, 단일 채널 색)를 한 번 만들어 재사용하며, 타일당 객체를 만들지 않는다.
/// 채널 수에 제한이 없어 페로몬 종류가 늘어도 그대로 쓸 수 있다(2채널은 전용 경로).
/// 내부 버퍼를 재사용하므로 인스턴스 하나를 여러 스레드에서 동시에 쓰면 안 된다.
/// </summary>
public sealed class CompositeHeatmapRasterizer
{
    private const int BlockSize = 4096;

    private readonly float[] _weight = new float[256];
    private byte[] _quantized = Array.Empty<byte>();
    private readonly Dictionary<HeatmapRamp, RampTables> _tables = new(ReferenceEqualityComparer.Instance);

    public CompositeHeatmapRasterizer(CompositeBlend? blend = null)
    {
        Blend = blend ?? CompositeBlend.Default;
        if (!(Blend.WeightExponent > 0) || Blend.WeightExponent > 1)
            throw new ArgumentOutOfRangeException(nameof(blend), "가중치 지수는 0 초과 1 이하여야 합니다.");
        for (int i = 1; i < 256; i++) _weight[i] = MathF.Pow(i / 255f, Blend.WeightExponent);
    }

    public CompositeBlend Blend { get; }

    public void Rasterize(ReadOnlySpan<CompositeChannel> channels, Span<byte> rgba)
    {
        if (channels.Length == 0) throw new ArgumentException("채널이 하나 이상 필요합니다.", nameof(channels));
        int length = channels[0].Field.Length;
        foreach (var channel in channels)
            if (channel.Field.Length != length) throw new ArgumentException("채널 필드 크기가 서로 다릅니다.", nameof(channels));
        if (rgba.Length < length * 4) throw new ArgumentException("픽셀 버퍼가 너무 작습니다.", nameof(rgba));

        // 채널별 준비(호출당 1회, 채널 수만큼의 작은 배열): 기여할 수 없는 채널(최대값 0)은 미리 뺀다.
        var fields = new float[channels.Length][];
        var quantizers = new HeatmapQuantizer[channels.Length];
        var tables = new RampTables[channels.Length];
        int active = 0;
        foreach (var channel in channels)
        {
            var quantizer = new HeatmapQuantizer(channel.Max, channel.Scale);
            if (quantizer.IsEmpty) continue;
            fields[active] = channel.Field;
            quantizers[active] = quantizer;
            tables[active] = TablesFor(channel.Ramp);
            active++;
        }

        var pixels = MemoryMarshal.Cast<byte, uint>(rgba)[..length];
        if (active == 0)
        {
            pixels.Clear();
            return;
        }

        // 블록 단위: 채널마다 농도를 0~255 인덱스로 먼저 일괄 변환(단순 반복문)한 뒤 인덱스로만 합성한다.
        if (_quantized.Length < active * BlockSize) _quantized = new byte[active * BlockSize];
        for (int start = 0; start < length; start += BlockSize)
        {
            int count = Math.Min(BlockSize, length - start);
            for (int c = 0; c < active; c++)
            {
                quantizers[c].QuantizeTo(fields[c].AsSpan(start, count), _quantized.AsSpan(c * BlockSize, count));
            }

            var block = pixels.Slice(start, count);
            if (active == 2) ComposeTwo(block, tables[0], tables[1]);
            else ComposeMany(block, tables, active);
        }
    }

    /// <summary>가장 흔한 2채널(음식 + 유충) 전용 합성.</summary>
    private void ComposeTwo(Span<uint> pixels, RampTables t0, RampTables t1)
    {
        var q0s = _quantized.AsSpan(0, pixels.Length);
        var q1s = _quantized.AsSpan(BlockSize, pixels.Length);
        bool average = Blend.Mode == CompositeBlendMode.WeightedAverage;
        for (int i = 0; i < pixels.Length; i++)
        {
            int q0 = q0s[i], q1 = q1s[i];
            if (q1 == 0)
            {
                pixels[i] = t0.Single[q0]; // q0도 0이면 Single[0] = 0(투명)
            }
            else if (q0 == 0)
            {
                pixels[i] = t1.Single[q1];
            }
            else
            {
                float inv = average ? 1 / (t0.Weight[q0] + t1.Weight[q1]) : 1;
                pixels[i] = TilePalette.Rgba(
                    ToByte((t0.WeightedR[q0] + t1.WeightedR[q1]) * inv),
                    ToByte((t0.WeightedG[q0] + t1.WeightedG[q1]) * inv),
                    ToByte((t0.WeightedB[q0] + t1.WeightedB[q1]) * inv),
                    ToByte((1 - t0.OneMinusAlpha[q0] * t1.OneMinusAlpha[q1]) * 255));
            }
        }
    }

    /// <summary>채널 수가 1 또는 3 이상일 때의 일반 합성.</summary>
    private void ComposeMany(Span<uint> pixels, RampTables[] tables, int active)
    {
        bool average = Blend.Mode == CompositeBlendMode.WeightedAverage;
        for (int i = 0; i < pixels.Length; i++)
        {
            float r = 0, g = 0, b = 0, weightSum = 0, transparency = 1;
            int contributing = 0, lastQ = 0, lastC = 0;
            for (int c = 0; c < active; c++)
            {
                int q = _quantized[c * BlockSize + i];
                if (q == 0) continue;
                var t = tables[c];
                r += t.WeightedR[q];
                g += t.WeightedG[q];
                b += t.WeightedB[q];
                weightSum += t.Weight[q];
                transparency *= t.OneMinusAlpha[q];
                contributing++;
                lastC = c;
                lastQ = q;
            }

            if (contributing <= 1)
            {
                pixels[i] = tables[lastC].Single[lastQ]; // 0개면 Single[0] = 0, 1개면 그 채널 램프 색
                continue;
            }
            float inv = average ? 1 / weightSum : 1;
            pixels[i] = TilePalette.Rgba(ToByte(r * inv), ToByte(g * inv), ToByte(b * inv), ToByte((1 - transparency) * 255));
        }
    }

    /// <summary>한 타일의 합성 색(테스트·범례용). <see cref="Rasterize"/>와 같은 계산.</summary>
    public uint ComposePixel(ReadOnlySpan<float> values, ReadOnlySpan<CompositeChannel> channels)
    {
        var fields = new CompositeChannel[channels.Length];
        for (int c = 0; c < channels.Length; c++) fields[c] = channels[c] with { Field = new[] { values[c] } };
        var rgba = new byte[4];
        Rasterize(fields, rgba);
        return BitConverter.ToUInt32(rgba);
    }

    private RampTables TablesFor(HeatmapRamp ramp)
    {
        if (!_tables.TryGetValue(ramp, out var tables))
        {
            tables = new RampTables(ramp, _weight, Blend.Mode);
            _tables.Add(ramp, tables);
        }
        return tables;
    }

    private static byte ToByte(float v) => v >= 255 ? (byte)255 : v <= 0 ? (byte)0 : (byte)(v + 0.5f);

    /// <summary>램프 하나의 합성용 조회표. 가중 평균이면 가중치 = 강도^γ, 가산이면 가중치 = α.</summary>
    private sealed class RampTables
    {
        public readonly float[] WeightedR = new float[256], WeightedG = new float[256], WeightedB = new float[256];
        public readonly float[] Weight = new float[256], OneMinusAlpha = new float[256];
        public readonly uint[] Single = new uint[256];

        public RampTables(HeatmapRamp ramp, float[] weights, CompositeBlendMode mode)
        {
            for (int q = 1; q < 256; q++)
            {
                float a = ramp.Alpha(q);
                float w = mode == CompositeBlendMode.WeightedAverage ? weights[q] : a;
                Weight[q] = w;
                WeightedR[q] = w * ramp.R(q);
                WeightedG[q] = w * ramp.G(q);
                WeightedB[q] = w * ramp.B(q);
                OneMinusAlpha[q] = 1 - a;
                Single[q] = ramp.ColorAt(q);
            }
        }
    }
}

/// <summary>
/// 냄새 키 → 복합 히트맵 색. 기본은 음식 = 주황, 유충 = 보라. 등록되지 않은 키(이후 추가될 페로몬)는
/// 예비 색 목록에서 순서대로 받는다. 팔레트는 교체 가능하다.
/// </summary>
public sealed class CompositeHeatmapPalette
{
    public static readonly uint FoodOrange = TilePalette.Rgba(0xFF, 0x8C, 0x1A);
    public static readonly uint LarvaPurple = TilePalette.Rgba(0xA8, 0x4D, 0xFF);

    private static readonly uint[] Fallback =
    {
        TilePalette.Rgba(0x2E, 0xC4, 0xB6), // 청록
        TilePalette.Rgba(0x4C, 0xC9, 0x3E), // 초록
        TilePalette.Rgba(0xF2, 0xD3, 0x2E), // 노랑
        TilePalette.Rgba(0x3E, 0x8E, 0xF7), // 파랑
    };

    private readonly Dictionary<string, HeatmapRamp> _ramps = new(StringComparer.Ordinal);
    private int _nextFallback;

    public static CompositeHeatmapPalette CreateDefault()
    {
        var palette = new CompositeHeatmapPalette();
        palette.Set("food", FoodOrange);
        palette.Set("larva", LarvaPurple);
        return palette;
    }

    public void Set(string scentKey, uint color) => _ramps[scentKey] = new HeatmapRamp(color);

    public HeatmapRamp Get(string scentKey)
    {
        if (!_ramps.TryGetValue(scentKey, out var ramp))
        {
            ramp = new HeatmapRamp(Fallback[_nextFallback++ % Fallback.Length]);
            _ramps[scentKey] = ramp;
        }
        return ramp;
    }
}
