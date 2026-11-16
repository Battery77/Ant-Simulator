using System.Runtime.InteropServices;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Scent;

/// <summary>
/// 모든 냄새 종류 × 레이어의 농도. 레이어마다 저장 배치(<see cref="ScentGrid"/>)가 다르다:
/// 지상은 <see cref="ScentGrid.SurfaceCellSize"/>² 칸 묶음 격자, 개미집은 열린 칸만 압축한 원래 해상도(docs/PERF_SCENT_DESIGN.md).
/// Current/Next 이중 버퍼를 가진다. 배치는 월드의 고체 칸이 바뀔 때 <see cref="ScentSystem"/>이 갱신하며 값을 옮긴다.
/// </summary>
public sealed class ScentState
{
    /// <summary>
    /// 지원하는 농도 상한. 이 범위(0 ~ 1e30)에서는 네 이웃 차이의 합(최대 4e30)과 감쇠·발생이 float에서 넘치지 않는다.
    /// 실험 파일 저장·불러오기는 이 범위 밖(음수, NaN, ∞, 1e30 초과)의 농도를 거부한다.
    /// <see cref="MutableField"/>로 범위 밖 값을 직접 넣는 것은 지원하지 않는다.
    /// </summary>
    public const float MaxConcentration = 1e30f;

    private readonly ScentGrid[] _grids = new ScentGrid[World.LayerCount];
    private float[][][] _current; // [type][layer][index]
    private float[][][] _next;

    /// <summary>월드의 현재 고체 칸으로 배치를 만든다(값 0).</summary>
    public ScentState(World world, int typeCount)
        : this(world?.Config ?? throw new ArgumentNullException(nameof(world)), typeCount,
            ScentGrid.OpenMask(world.Surface), ScentGrid.OpenMask(world.Nest))
    {
    }

    /// <summary>
    /// 모든 칸이 열린 배치로 만든다(빈 월드와 같음). 고체가 있는 월드에 쓰면 첫 냄새 틱에서 <see cref="ScentSystem"/>이 배치를 월드에 맞춘다.
    /// </summary>
    public ScentState(WorldConfig config, int typeCount)
        : this(config ?? throw new ArgumentNullException(nameof(config)), typeCount, ScentGrid.AllOpen(config), ScentGrid.AllOpen(config))
    {
    }

    private ScentState(WorldConfig config, int typeCount, byte[] surfaceOpen, byte[] nestOpen)
    {
        if (typeCount < 1) throw new ArgumentOutOfRangeException(nameof(typeCount));
        Config = config;
        TypeCount = typeCount;
        _grids[(int)LayerId.Surface] = ScentGrid.Coarse(config, surfaceOpen);
        _grids[(int)LayerId.Nest] = ScentGrid.Sparse(config, nestOpen);
        _current = Allocate();
        _next = Allocate();
    }

    public WorldConfig Config { get; }
    public int TypeCount { get; }

    /// <summary>레이어의 저장 배치.</summary>
    public ScentGrid Grid(LayerId layer) => _grids[(int)layer];

    /// <summary>모든 값이 지원 범위(0 ~ <see cref="MaxConcentration"/>, 유한)인지. 저장 전 검사용.</summary>
    public bool AllValuesInSupportedRange()
    {
        foreach (var type in _current)
        foreach (var field in type)
            foreach (float v in field)
                if (!(v >= 0 && v <= MaxConcentration)) return false; // NaN도 여기서 걸린다
        return true;
    }

    /// <summary>저장 값(배치 순서, 길이 = <see cref="ScentGrid.Length"/>).</summary>
    public ReadOnlySpan<float> Field(int type, LayerId layer) => _current[type][(int)layer];

    /// <summary>저장 값에 직접 쓴다(초기 조건 설정, 불러오기, 테스트용). 인덱스는 <see cref="IndexOf"/>.</summary>
    public Span<float> MutableField(int type, LayerId layer) => _current[type][(int)layer];

    /// <summary>칸 (x, y)의 저장 인덱스(<see cref="ScentGrid.IndexOf"/>). 맵 밖·개미집 고체 칸이면 −1.</summary>
    public int IndexOf(LayerId layer, int x, int y) => _grids[(int)layer].IndexOf(x, y);

    /// <summary>칸 (x, y)의 농도(지상은 그 칸이 속한 묶음 칸 값). 저장되지 않는 칸(개미집 고체)은 0.</summary>
    public float Get(int type, LayerId layer, int x, int y)
    {
        int i = IndexOf(layer, x, y);
        return i < 0 ? 0 : _current[type][(int)layer][i];
    }

    /// <summary>칸 (x, y)의 저장 값에 쓴다(지상은 묶음 칸 전체). 저장되지 않는 칸이면 false.</summary>
    public bool Set(int type, LayerId layer, int x, int y, float value)
    {
        int i = IndexOf(layer, x, y);
        if (i < 0) return false;
        _current[type][(int)layer][i] = value;
        return true;
    }

    /// <summary>표시용 격자로 펼친다(<see cref="ScentGrid.ExpandTo"/>).</summary>
    public void ExpandTo(int type, LayerId layer, Span<float> display) => _grids[(int)layer].ExpandTo(_current[type][(int)layer], display);

    internal float[] CurrentArray(int type, LayerId layer) => _current[type][(int)layer];
    internal float[] NextArray(int type, LayerId layer) => _next[type][(int)layer];

    internal void Swap() => (_current, _next) = (_next, _current);

    /// <summary>
    /// 레이어 배치를 바꾼다. 새 배치의 각 저장 칸 값은 그 자리의 이전 값(같은 칸이 이전에도 저장되어 있었으면)이고, 새로 열린 칸은 0이다.
    /// 막힌 칸의 값은 버린다(고체 칸 농도 = 0 규칙). 배치가 같으면 아무것도 하지 않는다.
    /// </summary>
    internal void UpdateLayout(LayerId layer, ScentGrid grid)
    {
        int l = (int)layer;
        var old = _grids[l];
        if (old.SameAs(grid)) return;
        for (int t = 0; t < TypeCount; t++)
        {
            var before = _current[t][l];
            var after = new float[grid.Length];
            if (grid.IsSparse)
            {
                var tiles = grid.Tiles!;
                for (int e = 0; e < tiles.Length; e++)
                {
                    int previous = old.IndexOf(tiles[e] % Config.Width, tiles[e] / Config.Width);
                    if (previous >= 0) after[e] = before[previous];
                }
            }
            else
            {
                var open = grid.CellOpen!;
                for (int c = 0; c < after.Length; c++) after[c] = open[c] != 0 ? before[c] : 0;
            }
            _current[t][l] = after;
            _next[t][l] = new float[grid.Length];
        }
        _grids[l] = grid;
    }

    /// <summary>월드의 현재 고체 칸으로 두 레이어 배치를 다시 만들고 모든 값을 0으로 한다(초기화용).</summary>
    internal void Rebuild(World world)
    {
        if (world.Config.Width != Config.Width || world.Config.Height != Config.Height)
            throw new ArgumentException("월드 크기가 다릅니다.", nameof(world));
        _grids[(int)LayerId.Surface] = ScentGrid.Coarse(Config, ScentGrid.OpenMask(world.Surface));
        _grids[(int)LayerId.Nest] = ScentGrid.Sparse(Config, ScentGrid.OpenMask(world.Nest));
        _current = Allocate();
        _next = Allocate();
    }

    public void Clear()
    {
        foreach (var type in _current)
        foreach (var field in type)
            Array.Clear(field);
    }

    /// <summary>
    /// 한 종류의 전체 총량(칸 단위, double로 누적). 지상 묶음 칸 값은 F² 칸의 평균 농도이므로 F²를 곱한다.
    /// </summary>
    public double Total(int type)
    {
        double surface = 0, nest = 0;
        foreach (float v in _current[type][(int)LayerId.Surface]) surface += v;
        foreach (float v in _current[type][(int)LayerId.Nest]) nest += v;
        int f = _grids[(int)LayerId.Surface].CellSize;
        return surface * f * f + nest;
    }

    public void CopyFrom(ScentState other)
    {
        if (other.TypeCount != TypeCount || other.Config.TileCount != Config.TileCount)
            throw new ArgumentException("냄새 상태 구성이 다릅니다.", nameof(other));
        for (int l = 0; l < World.LayerCount; l++)
            if (!_grids[l].SameAs(other._grids[l])) throw new ArgumentException("냄새 배치가 다릅니다.", nameof(other));
        for (int t = 0; t < TypeCount; t++)
        for (int l = 0; l < World.LayerCount; l++)
            other._current[t][l].CopyTo(_current[t][l], 0);
    }

    /// <summary>모든 값의 비트 단위 해시(재현성 검증용).</summary>
    public ulong ComputeHash()
    {
        var hash = Fnv1a64.Create();
        foreach (var type in _current)
        foreach (var field in type)
            hash.Add(MemoryMarshal.AsBytes(field.AsSpan()));
        return hash.Value;
    }

    private float[][][] Allocate()
    {
        var result = new float[TypeCount][][];
        for (int t = 0; t < TypeCount; t++)
        {
            result[t] = new float[World.LayerCount][];
            for (int l = 0; l < World.LayerCount; l++) result[t][l] = new float[_grids[l].Length];
        }
        return result;
    }
}
