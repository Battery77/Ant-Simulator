using System.Runtime.InteropServices;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Scent;

/// <summary>
/// 모든 냄새 종류 × 레이어의 농도 필드. 각 필드는 레이어 전체(W×H, 행 우선) 연속 float 배열이며
/// Current/Next 이중 버퍼를 가진다. 청크와 무관한 하나의 배열이므로 청크 경계에서도 계산이 연속적이다.
/// </summary>
public sealed class ScentState
{
    /// <summary>
    /// 지원하는 농도 상한. 이 범위(0 ~ 1e30)에서는 네 이웃 차이의 합(최대 4e30)과 감쇠·발생이 float에서 넘치지 않는다.
    /// 실험 파일 저장·불러오기는 이 범위 밖(음수, NaN, ∞, 1e30 초과)의 농도를 거부한다.
    /// <see cref="MutableField"/>로 범위 밖 값을 직접 넣는 것은 지원하지 않는다.
    /// </summary>
    public const float MaxConcentration = 1e30f;

    /// <summary>모든 필드의 값이 지원 범위(0 ~ <see cref="MaxConcentration"/>, 유한)인지. 저장 전 검사용.</summary>
    public bool AllValuesInSupportedRange()
    {
        foreach (var type in _current)
        foreach (var field in type)
            foreach (float v in field)
                if (!(v >= 0 && v <= MaxConcentration)) return false; // NaN도 여기서 걸린다
        return true;
    }

    private float[][][] _current;
    private float[][][] _next;

    public ScentState(WorldConfig config, int typeCount)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (typeCount < 1) throw new ArgumentOutOfRangeException(nameof(typeCount));
        Config = config;
        TypeCount = typeCount;
        _current = Allocate(config, typeCount);
        _next = Allocate(config, typeCount);
    }

    public WorldConfig Config { get; }
    public int TypeCount { get; }

    public ReadOnlySpan<float> Field(int type, LayerId layer) => _current[type][(int)layer];

    /// <summary>현재 필드에 직접 쓴다(초기 조건 설정, 불러오기, 테스트용).</summary>
    public Span<float> MutableField(int type, LayerId layer) => _current[type][(int)layer];

    public float Get(int type, LayerId layer, int x, int y) => _current[type][(int)layer][Config.ToLinearIndex(x, y)];

    internal float[] CurrentArray(int type, LayerId layer) => _current[type][(int)layer];
    internal float[] NextArray(int type, LayerId layer) => _next[type][(int)layer];

    internal void Swap() => (_current, _next) = (_next, _current);

    public void Clear()
    {
        foreach (var type in _current)
        foreach (var field in type)
            Array.Clear(field);
    }

    /// <summary>한 종류의 전체 총량(두 레이어 합, double로 누적).</summary>
    public double Total(int type)
    {
        double sum = 0;
        foreach (var field in _current[type])
            foreach (float v in field) sum += v;
        return sum;
    }

    public void CopyFrom(ScentState other)
    {
        if (other.TypeCount != TypeCount || other.Config.TileCount != Config.TileCount)
            throw new ArgumentException("냄새 상태 구성이 다릅니다.", nameof(other));
        for (int t = 0; t < TypeCount; t++)
        for (int l = 0; l < World.LayerCount; l++)
            other._current[t][l].CopyTo(_current[t][l], 0);
    }

    /// <summary>모든 필드의 비트 단위 해시(재현성 검증용).</summary>
    public ulong ComputeHash()
    {
        var hash = Fnv1a64.Create();
        foreach (var type in _current)
        foreach (var field in type)
            hash.Add(MemoryMarshal.AsBytes(field.AsSpan()));
        return hash.Value;
    }

    private static float[][][] Allocate(WorldConfig config, int typeCount)
    {
        var result = new float[typeCount][][];
        for (int t = 0; t < typeCount; t++)
        {
            result[t] = new float[World.LayerCount][];
            for (int l = 0; l < World.LayerCount; l++) result[t][l] = new float[config.TileCount];
        }
        return result;
    }
}
