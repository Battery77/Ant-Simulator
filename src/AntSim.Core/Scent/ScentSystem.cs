using System.Numerics;
using AntSim.Core.Worlds;

namespace AntSim.Core.Scent;

/// <summary>
/// 냄새 한 틱 계산. 종류마다 순서를 고정한다: 1) 4방향 확산 + 통로 교환 2) 지수 감쇠 3) 발생.
/// <list type="bullet">
/// <item>확산: C'ᵢ = Cᵢ + D·dt·Σ(Cⱼ − Cᵢ), j는 맵 안의 비고체 4이웃. 고체 칸·맵 밖과는 교환하지 않는다(밀폐 경계).</item>
/// <item>통로: 중심 좌표에서만, 두 레이어의 중심이 모두 비고체일 때 Dp·dt·(C반대 − C)를 교환한다.</item>
/// <item>감쇠: C ← C·exp(−k·dt). 발생: C ← C + e·dt·수량.</item>
/// <item>고체 칸의 농도는 항상 0이다. 반올림으로 0 미만이 된 값은 0으로, 지원 상한(<see cref="ScentState.MaxConcentration"/>)을
/// 반올림 여유(<see cref="RoundingSlackUlps"/>) 안에서 넘은 값은 상한으로 되돌린다.</item>
/// </list>
/// 모든 계산은 Current만 읽고 Next에 쓴 뒤 교환하므로 순회 순서와 무관하며, 단일 스레드에서 항상 같은 결과를 낸다.
/// 월드 타일 데이터는 청크 Version이 바뀐 청크만 레이어 전체 배열 캐시로 다시 복사한다.
/// </summary>
public sealed class ScentSystem
{
    private readonly World _world;
    private readonly byte[][] _open;          // [layer] 1 = 비고체
    private readonly byte[][][] _quantities;  // [quantityKind][layer]
    private readonly long[][] _chunkVersions; // [layer][chunk], -1 = 미캐시
    private int[] _passages = Array.Empty<int>();
    private long _passageVersion = -1;

    public ScentSystem(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        int tiles = world.Config.TileCount;
        _open = new byte[World.LayerCount][];
        _quantities = new byte[TileEnums.QuantityKindCount][][];
        _chunkVersions = new long[World.LayerCount][];
        for (int l = 0; l < World.LayerCount; l++)
        {
            _open[l] = new byte[tiles];
            _chunkVersions[l] = new long[world.Config.ChunkCount];
            Array.Fill(_chunkVersions[l], -1L);
        }
        for (int q = 0; q < _quantities.Length; q++)
        {
            _quantities[q] = new byte[World.LayerCount][];
            for (int l = 0; l < World.LayerCount; l++) _quantities[q][l] = new byte[tiles];
        }
    }

    public World World => _world;

    public void Tick(ScentState state, ScentSettings settings)
    {
        if (state.TypeCount != settings.Types.Count)
            throw new ArgumentException("냄새 상태와 설정의 종류 수가 다릅니다.", nameof(settings));
        RefreshCaches();

        double dt = settings.TickSeconds;
        for (int t = 0; t < settings.Types.Count; t++)
        {
            var type = settings.Types[t];
            float a = (float)(type.Diffusion * dt);
            float ap = (float)(type.PassageDiffusion * dt);
            float decay = (float)Math.Exp(-type.Decay * dt);
            float emit = (float)(type.Emission * dt);

            for (int l = 0; l < World.LayerCount; l++)
                Diffuse(state.CurrentArray(t, (LayerId)l), state.NextArray(t, (LayerId)l), _open[l], a);

            ExchangeThroughPassages(state, t, ap);

            for (int l = 0; l < World.LayerCount; l++)
            {
                byte[]? source = type.Source is { } kind ? _quantities[(int)kind][l] : null;
                DecayAndEmit(state.NextArray(t, (LayerId)l), decay, source, emit);
            }
        }
        state.Swap();
    }

    private void Diffuse(float[] cur, float[] next, byte[] open, float a)
    {
        if (a == 0)
        {
            // 확산 계수 0: 이웃 차이를 계산하지 않는다(c + 0·Σ와 같은 값이며, 큰 농도에서 Σ가 넘쳐 0·∞ = NaN이 되는 연산을 피한다).
            for (int i = 0; i < cur.Length; i++) next[i] = open[i] != 0 ? cur[i] : 0;
            return;
        }
        int w = _world.Config.Width, h = _world.Config.Height;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            bool up = y > 0, down = y < h - 1;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                if (open[i] == 0)
                {
                    next[i] = 0;
                    continue;
                }
                float c = cur[i];
                float flux = 0;
                if (x > 0 && open[i - 1] != 0) flux += cur[i - 1] - c;
                if (x < w - 1 && open[i + 1] != 0) flux += cur[i + 1] - c;
                if (up && open[i - w] != 0) flux += cur[i - w] - c;
                if (down && open[i + w] != 0) flux += cur[i + w] - c;
                next[i] = c + a * flux;
            }
        }
    }

    private void ExchangeThroughPassages(ScentState state, int type, float ap)
    {
        if (_passages.Length == 0 || ap == 0) return;
        var curS = state.CurrentArray(type, LayerId.Surface);
        var curN = state.CurrentArray(type, LayerId.Nest);
        var nextS = state.NextArray(type, LayerId.Surface);
        var nextN = state.NextArray(type, LayerId.Nest);
        var openS = _open[(int)LayerId.Surface];
        var openN = _open[(int)LayerId.Nest];
        foreach (int i in _passages)
        {
            if (openS[i] == 0 || openN[i] == 0) continue;
            float flow = ap * (curN[i] - curS[i]);
            nextS[i] += flow;
            nextN[i] -= flow;
        }
    }

    /// <summary>
    /// 상한 보정 여유(ULP). 안정 조건 (4D+Dp)·dt ≤ 1을 만족하면 실수 연산에서는 확산·통로 교환 결과가 입력 최대값을 넘지 않지만,
    /// float 계수 변환(5개 계수의 반올림, 각 0.5 ULP 이하)과 이웃 차이·곱·합의 반올림(칸당 10회 이하, 각 0.5 ULP 이하) 때문에
    /// 상한 근처 값이 몇 ULP 넘을 수 있다(재검증 반례 +1 ULP, 보정 없이 CFL 경계 무작위 조합 약 19만 건 실측 최대 +1 ULP).
    /// 이 반올림 오차보다 넉넉한 64 ULP(상대 약 7.6e-6)까지만 보정한다.
    /// 감쇠 계수는 ≤ 1이고, 발생 증분(≤ 1e6 × 10초 × 255)은 상한 근처 float 간격(약 7.5e22)보다 훨씬 작아 상한을 넘기지 못한다.
    /// </summary>
    internal const int RoundingSlackUlps = 64;

    private static readonly float RoundingCeiling =
        BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(ScentState.MaxConcentration) + RoundingSlackUlps);

    /// <summary>
    /// 지원 상한을 반올림 여유 안에서 넘은 값만 상한으로 되돌린다. 그보다 큰 값(지원 범위 밖 입력)은 그대로 두어
    /// 저장 검사에서 거부되게 한다. 상한 이하 값은 바꾸지 않으므로 정상 농도의 계산 결과·상태 해시는 그대로다.
    /// </summary>
    internal static float CorrectRoundingOvershoot(float value) =>
        value > ScentState.MaxConcentration && value <= RoundingCeiling ? ScentState.MaxConcentration : value;

    /// <summary>0 미만(−0 포함) → +0, 상한을 반올림 여유 안에서 넘은 값 → 상한, 그 외(NaN 포함)는 그대로.</summary>
    private static float ClampToSupported(float value) => CorrectRoundingOvershoot(MathF.Max(0f, value));

    /// <summary><see cref="ClampToSupported(float)"/>의 벡터판. 원소마다 같은 비교·선택이라 결과 비트가 같다.</summary>
    private static Vector<float> ClampToSupported(Vector<float> v)
    {
        v = Vector.ConditionalSelect(Vector.LessThanOrEqual(v, Vector<float>.Zero), Vector<float>.Zero, v);
        var max = new Vector<float>(ScentState.MaxConcentration);
        var overshoot = Vector.GreaterThan(v, max) & Vector.LessThanOrEqual(v, new Vector<float>(RoundingCeiling));
        return Vector.ConditionalSelect(overshoot, max, v);
    }

    /// <summary>
    /// 감쇠·발생과 범위 정리: next ← clamp(next·decay [+ emit·수량]). 매 틱 모든 칸을 지나는 경로라 벡터로 계산하고 나머지는 스칼라로 처리한다.
    /// 곱·합은 원소마다 같은 IEEE 단정도 연산(융합 없음)이므로 스칼라 계산과 비트 단위로 같다.
    /// </summary>
    internal static void DecayAndEmit(float[] next, float decay, byte[]? source, float emit)
    {
        int i = 0;
        var vDecay = new Vector<float>(decay);
        if (source is null || emit == 0)
        {
            if (Vector.IsHardwareAccelerated)
                for (; i <= next.Length - Vector<float>.Count; i += Vector<float>.Count)
                    ClampToSupported(new Vector<float>(next, i) * vDecay).CopyTo(next, i);
            for (; i < next.Length; i++) next[i] = ClampToSupported(next[i] * decay);
            return;
        }

        if (Vector.IsHardwareAccelerated)
        {
            var vEmit = new Vector<float>(emit);
            int floats = Vector<float>.Count;
            for (; i <= next.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(source, i), out var low, out var high);
                Vector.Widen(low, out var q0, out var q1);
                Vector.Widen(high, out var q2, out var q3);
                Apply(i, q0);
                Apply(i + floats, q1);
                Apply(i + 2 * floats, q2);
                Apply(i + 3 * floats, q3);
            }

            void Apply(int at, Vector<uint> quantity)
            {
                var q = Vector.ConvertToSingle(Vector.AsVectorInt32(quantity)); // 0~255라 정확히 변환
                ClampToSupported(new Vector<float>(next, at) * vDecay + vEmit * q).CopyTo(next, at);
            }
        }
        for (; i < next.Length; i++) next[i] = ClampToSupported(next[i] * decay + emit * source[i]);
    }

    /// <summary>바뀐 청크의 고체/수량 정보와 통로 목록을 레이어 전체 배열 캐시에 반영한다.</summary>
    private void RefreshCaches()
    {
        var config = _world.Config;
        foreach (var layer in _world.Layers)
        {
            int l = (int)layer.Id;
            var versions = _chunkVersions[l];
            for (int c = 0; c < layer.Chunks.Count; c++)
            {
                var chunk = layer.Chunks[c];
                if (versions[c] == chunk.Version) continue;
                versions[c] = chunk.Version;
                for (int ly = 0; ly < chunk.ValidHeight; ly++)
                {
                    int dst = (chunk.OriginY + ly) * config.Width + chunk.OriginX;
                    var solid = chunk.Row(TilePlane.Solid, ly);
                    var open = _open[l].AsSpan(dst, chunk.ValidWidth);
                    for (int x = 0; x < solid.Length; x++) open[x] = solid[x] == 0 ? (byte)1 : (byte)0;
                    chunk.Row(TilePlane.Food, ly).CopyTo(_quantities[(int)QuantityKind.Food][l].AsSpan(dst));
                    chunk.Row(TilePlane.Larva, ly).CopyTo(_quantities[(int)QuantityKind.Larva][l].AsSpan(dst));
                }
            }
        }

        if (_passageVersion != _world.Passages.Version)
        {
            var list = new List<int>(_world.Passages.Count);
            _world.Passages.CopyIndicesTo(list);
            _passages = list.ToArray();
            _passageVersion = _world.Passages.Version;
        }
    }
}
