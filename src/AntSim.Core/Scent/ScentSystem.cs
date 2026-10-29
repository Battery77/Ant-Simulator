using AntSim.Core.Worlds;

namespace AntSim.Core.Scent;

/// <summary>
/// 냄새 한 틱 계산. 종류마다 순서를 고정한다: 1) 4방향 확산 + 통로 교환 2) 지수 감쇠 3) 발생.
/// <list type="bullet">
/// <item>확산: C'ᵢ = Cᵢ + D·dt·Σ(Cⱼ − Cᵢ), j는 맵 안의 비고체 4이웃. 고체 칸·맵 밖과는 교환하지 않는다(밀폐 경계).</item>
/// <item>통로: 중심 좌표에서만, 두 레이어의 중심이 모두 비고체일 때 Dp·dt·(C반대 − C)를 교환한다.</item>
/// <item>감쇠: C ← C·exp(−k·dt). 발생: C ← C + e·dt·수량.</item>
/// <item>고체 칸의 농도는 항상 0이다.</item>
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

    private static void DecayAndEmit(float[] next, float decay, byte[]? source, float emit)
    {
        if (source is null || emit == 0)
        {
            for (int i = 0; i < next.Length; i++) next[i] = MathF.Max(0f, next[i] * decay);
            return;
        }
        for (int i = 0; i < next.Length; i++)
            next[i] = MathF.Max(0f, next[i] * decay + emit * source[i]);
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
