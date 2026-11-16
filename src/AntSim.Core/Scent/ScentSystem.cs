using System.Numerics;
using AntSim.Core.Worlds;

namespace AntSim.Core.Scent;

/// <summary>
/// 냄새 한 틱 계산. 종류마다 순서를 고정한다: 1) 4방향 확산 + 통로 교환 2) 지수 감쇠 3) 발생.
/// 저장 배치는 레이어마다 다르다(<see cref="ScentGrid"/>, docs/PERF_SCENT_DESIGN.md).
/// <list type="bullet">
/// <item>개미집(열린 칸 압축, 원래 해상도): C'ᵢ = Cᵢ + D·dt·Σ(Cⱼ − Cᵢ), j는 맵 안의 비고체 4이웃. 고체 칸·맵 밖과는 교환하지 않는다.
/// 열린 칸만 계산하므로 비용은 판 칸 수에 비례한다.</item>
/// <item>지상(F×F 묶음 격자): 같은 식을 묶음 칸 사이에 적용하되 교환율을 D·dt/F²로 보정한다(칸 간격이 F배). 묶음 칸 안에 비고체 칸이
/// 하나라도 있으면 열린 묶음 칸이다. 발생은 묶음 칸 안 수량 합 / F²(평균 농도 단위)를 쓴다.</item>
/// <item>통로: 중심 칸에서만, 두 레이어 모두 그 칸이 비고체일 때 Dp·dt·(C개미집 − C지상)을 교환한다. 지상 묶음 칸 쪽 변화는 1/F²로 나눠
/// 총량(칸 단위)을 보존한다.</item>
/// <item>감쇠: C ← C·exp(−k·dt). 발생: C ← C + e·dt·수량.</item>
/// <item>닫힌 칸(개미집 고체 칸은 저장 안 함, 닫힌 지상 묶음 칸)의 농도는 0이다. 반올림으로 0 미만이 된 값은 0으로,
/// 지원 상한(<see cref="ScentState.MaxConcentration"/>)을 반올림 여유(<see cref="RoundingSlackUlps"/>) 안에서 넘은 값은 상한으로 되돌린다.</item>
/// </list>
/// 모든 계산은 Current만 읽고 Next에 쓴 뒤 교환하므로 순회 순서와 무관하며, 단일 스레드에서 항상 같은 결과를 낸다.
/// 월드 타일 데이터는 청크 Version이 바뀐 청크만 원래 해상도 캐시로 다시 복사하고, 바뀐 레이어만 배치·발생원을 다시 만든다.
/// </summary>
public sealed class ScentSystem
{
    private readonly World _world;
    private readonly byte[][] _open;          // [layer] 원래 해상도, 1 = 비고체(감각의 벽 판정에도 쓴다)
    private readonly byte[][][] _quantities;  // [quantityKind][layer] 원래 해상도
    private readonly long[][] _chunkVersions; // [layer][chunk], -1 = 미캐시
    private readonly bool[] _openChanged = new bool[World.LayerCount];     // 고체 칸이 바뀜 → 배치 갱신
    private readonly bool[] _quantityChanged = new bool[World.LayerCount]; // 청크가 바뀜 → 발생원 갱신
    private readonly ScentGrid?[] _sourceGrid = new ScentGrid?[World.LayerCount]; // 발생원 배열을 만든 배치
    private readonly float[][][] _sources;    // [quantityKind][layer] 배치 순서, 칸당 수량(지상은 묶음 칸 합 / F²)
    private int[] _passages = Array.Empty<int>();
    private long _passageVersion = -1;
    private bool _cachesReady;

    public ScentSystem(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        int tiles = world.Config.TileCount;
        _open = new byte[World.LayerCount][];
        _quantities = new byte[TileEnums.QuantityKindCount][][];
        _sources = new float[TileEnums.QuantityKindCount][][];
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
            _sources[q] = new float[World.LayerCount][];
            for (int l = 0; l < World.LayerCount; l++)
            {
                _quantities[q][l] = new byte[tiles];
                _sources[q][l] = Array.Empty<float>();
            }
        }
    }

    public World World => _world;

    /// <summary>
    /// 레이어별 원래 해상도 열린 칸(1 = 비고체) 캐시. 감각 패스(<see cref="Ants.AntSensing"/>)가 벽 판정에 읽는다. 반환 배열은 읽기 전용으로 쓴다.
    /// 캐시는 <see cref="Tick"/>이 매번 월드와 맞추며, 감각 패스는 같은 틱의 냄새 계산 직후에 실행되므로 다시 맞추지 않는다(추가 할당·순회 없음).
    /// 아직 한 번도 맞추지 않았거나 <paramref name="refresh"/>가 true면 지금 맞춘다(첫 틱 전 표시용 등).
    /// </summary>
    internal byte[][] OpenMasks(bool refresh = false)
    {
        if (refresh || !_cachesReady) RefreshCaches();
        return _open;
    }

    public void Tick(ScentState state, ScentSettings settings)
    {
        if (state.TypeCount != settings.Types.Count)
            throw new ArgumentException("냄새 상태와 설정의 종류 수가 다릅니다.", nameof(settings));
        if (state.Config.Width != _world.Config.Width || state.Config.Height != _world.Config.Height)
            throw new ArgumentException("냄새 상태와 월드의 크기가 다릅니다.", nameof(state));
        RefreshCaches();
        Sync(state);

        double dt = settings.TickSeconds;
        var surfaceGrid = state.Grid(LayerId.Surface);
        var nestGrid = state.Grid(LayerId.Nest);
        int f2 = surfaceGrid.CellSize * surfaceGrid.CellSize;
        for (int t = 0; t < settings.Types.Count; t++)
        {
            var type = settings.Types[t];
            float a = (float)(type.Diffusion * dt);
            float aSurface = (float)(type.Diffusion * dt / f2);
            float ap = (float)(type.PassageDiffusion * dt);
            float decay = (float)Math.Exp(-type.Decay * dt);
            float emit = (float)(type.Emission * dt);

            DiffuseCoarse(state.CurrentArray(t, LayerId.Surface), state.NextArray(t, LayerId.Surface), surfaceGrid, aSurface);
            DiffuseSparse(state.CurrentArray(t, LayerId.Nest), state.NextArray(t, LayerId.Nest), nestGrid, a);
            ExchangeThroughPassages(state, t, ap, f2);

            for (int l = 0; l < World.LayerCount; l++)
            {
                float[]? source = type.Source is { } kind ? _sources[(int)kind][l] : null;
                DecayAndEmit(state.NextArray(t, (LayerId)l), decay, source, emit);
            }
        }
        state.Swap();
    }

    /// <summary>바뀐 레이어(또는 아직 맞추지 않은 냄새 상태)의 배치와 발생원 배열을 월드에 맞춘다.</summary>
    private void Sync(ScentState state)
    {
        for (int l = 0; l < World.LayerCount; l++)
        {
            var layer = (LayerId)l;
            bool foreign = _sourceGrid[l] is null || !ReferenceEquals(_sourceGrid[l], state.Grid(layer));
            if (_openChanged[l] || foreign)
            {
                var built = layer == LayerId.Surface ? ScentGrid.Coarse(_world.Config, _open[l]) : ScentGrid.Sparse(_world.Config, _open[l]);
                state.UpdateLayout(layer, built); // 같은 배치면 기존 배치·값을 그대로 둔다
            }
            if (_openChanged[l] || foreign || _quantityChanged[l])
            {
                var grid = state.Grid(layer);
                for (int q = 0; q < _quantities.Length; q++) _sources[q][l] = BuildSources(grid, _quantities[q][l]);
                _sourceGrid[l] = grid;
            }
            _openChanged[l] = _quantityChanged[l] = false;
        }
    }

    /// <summary>배치 순서의 발생원(칸당 수량). 묶음 격자는 열린 묶음 칸 안 수량 합 / F², 압축은 그 칸의 수량.</summary>
    private float[] BuildSources(ScentGrid grid, byte[] quantity)
    {
        var config = _world.Config;
        var sources = new float[grid.Length];
        if (grid.IsSparse)
        {
            var tiles = grid.Tiles!;
            for (int e = 0; e < tiles.Length; e++) sources[e] = quantity[tiles[e]];
            return sources;
        }
        int f = grid.CellSize;
        var open = grid.CellOpen!;
        for (int y = 0; y < config.Height; y++)
        {
            int row = y * config.Width, cellRow = (y / f) * grid.Width;
            for (int x = 0; x < config.Width; x++) sources[cellRow + x / f] += quantity[row + x];
        }
        float inv = 1f / (f * f); // f = 4: 1/16은 정확한 float
        for (int c = 0; c < sources.Length; c++) sources[c] = open[c] != 0 ? sources[c] * inv : 0;
        return sources;
    }

    /// <summary>묶음 격자 확산(교환율 a = D·dt/F²). 닫힌 묶음 칸은 0, 열린 이웃끼리만 교환한다.</summary>
    private static void DiffuseCoarse(float[] cur, float[] next, ScentGrid grid, float a)
    {
        var open = grid.CellOpen!;
        int w = grid.Width, h = grid.Height;
        if (a == 0)
        {
            // 확산 계수 0: 이웃 차이를 계산하지 않는다(c + 0·Σ와 같은 값이며, 큰 농도에서 Σ가 넘쳐 0·∞ = NaN이 되는 연산을 피한다).
            for (int i = 0; i < cur.Length; i++) next[i] = open[i] != 0 ? cur[i] : 0;
            return;
        }
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

    /// <summary>열린 칸 압축 확산. 이웃 순서(왼쪽, 오른쪽, 위, 아래)와 식은 원래 해상도 전체 순회와 같아 같은 값을 낸다.</summary>
    private static void DiffuseSparse(float[] cur, float[] next, ScentGrid grid, float a)
    {
        if (a == 0)
        {
            cur.AsSpan().CopyTo(next);
            return;
        }
        var neighbors = grid.Neighbors!;
        for (int i = 0; i < cur.Length; i++)
        {
            float c = cur[i];
            float flux = 0;
            int o = 4 * i;
            int n0 = neighbors[o], n1 = neighbors[o + 1], n2 = neighbors[o + 2], n3 = neighbors[o + 3];
            if (n0 >= 0) flux += cur[n0] - c;
            if (n1 >= 0) flux += cur[n1] - c;
            if (n2 >= 0) flux += cur[n2] - c;
            if (n3 >= 0) flux += cur[n3] - c;
            next[i] = c + a * flux;
        }
    }

    private void ExchangeThroughPassages(ScentState state, int type, float ap, int f2)
    {
        if (_passages.Length == 0 || ap == 0) return;
        var curS = state.CurrentArray(type, LayerId.Surface);
        var curN = state.CurrentArray(type, LayerId.Nest);
        var nextS = state.NextArray(type, LayerId.Surface);
        var nextN = state.NextArray(type, LayerId.Nest);
        var openS = _open[(int)LayerId.Surface];
        var openN = _open[(int)LayerId.Nest];
        var surface = state.Grid(LayerId.Surface);
        var nest = state.Grid(LayerId.Nest);
        int width = _world.Config.Width;
        float inv = 1f / f2;
        foreach (int i in _passages)
        {
            if (openS[i] == 0 || openN[i] == 0) continue;
            int x = i % width, y = i / width;
            int s = surface.IndexOf(x, y), n = nest.IndexOf(x, y);
            if (s < 0 || n < 0) continue;
            float flow = ap * (curN[n] - curS[s]);
            nextS[s] += flow * inv;
            nextN[n] -= flow;
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
    /// 감쇠·발생과 범위 정리: next ← clamp(next·decay [+ emit·수량]). 매 틱 모든 저장 칸을 지나는 경로라 벡터로 계산하고 나머지는 스칼라로 처리한다.
    /// 곱·합은 원소마다 같은 IEEE 단정도 연산(융합 없음)이므로 스칼라 계산과 비트 단위로 같다. 수량은 배치 순서의 칸당 수량(float)이다.
    /// </summary>
    internal static void DecayAndEmit(float[] next, float decay, float[]? source, float emit)
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
            for (; i <= next.Length - Vector<float>.Count; i += Vector<float>.Count)
                ClampToSupported(new Vector<float>(next, i) * vDecay + vEmit * new Vector<float>(source, i)).CopyTo(next, i);
        }
        for (; i < next.Length; i++) next[i] = ClampToSupported(next[i] * decay + emit * source[i]);
    }

    /// <summary>바뀐 청크의 고체/수량 정보와 통로 목록을 레이어 전체 배열 캐시에 반영한다.</summary>
    private void RefreshCaches()
    {
        _cachesReady = true;
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
                _quantityChanged[l] = true;
                for (int ly = 0; ly < chunk.ValidHeight; ly++)
                {
                    int dst = (chunk.OriginY + ly) * config.Width + chunk.OriginX;
                    var solid = chunk.Row(TilePlane.Solid, ly);
                    var open = _open[l].AsSpan(dst, chunk.ValidWidth);
                    for (int x = 0; x < solid.Length; x++)
                    {
                        byte value = solid[x] == 0 ? (byte)1 : (byte)0;
                        if (open[x] != value)
                        {
                            open[x] = value;
                            _openChanged[l] = true;
                        }
                    }
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
