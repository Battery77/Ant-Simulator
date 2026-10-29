using AntSim.Core.Util;

namespace AntSim.Core.Worlds;

/// <summary>
/// 시뮬레이션 월드 데이터의 루트. 지상/개미집 두 레이어, 통로 집합, 규칙을 소유한다.
/// Godot 타입에 의존하지 않으며, 렌더링은 이 데이터를 읽기만 한다.
/// 생성자는 빈 월드(모든 타일 흙, 블록 없음)를 만든다. 기본 개미집 프리셋은 <see cref="WorldPresets"/>.
/// </summary>
public sealed class World
{
    public const int LayerCount = 2;

    private readonly WorldLayer[] _layers;

    public World(WorldConfig config, WorldRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        Rules = rules ?? WorldRules.Default;
        _layers = new WorldLayer[LayerCount];
        for (int i = 0; i < LayerCount; i++)
            _layers[i] = new WorldLayer((LayerId)i, config, Rules);
        Passages = new PassageMap(config);
    }

    public WorldConfig Config { get; }
    public WorldRules Rules { get; }
    public PassageMap Passages { get; }

    public WorldLayer Surface => _layers[(int)LayerId.Surface];
    public WorldLayer Nest => _layers[(int)LayerId.Nest];

    public IReadOnlyList<WorldLayer> Layers => _layers;

    public WorldLayer GetLayer(LayerId id)
    {
        if ((uint)id >= LayerCount) throw new ArgumentOutOfRangeException(nameof(id), id, "알 수 없는 레이어입니다.");
        return _layers[(int)id];
    }

    /// <summary>
    /// 타일·통로·규칙이 같은 독립 복제본을 만든다(시뮬레이션 모드용). 이후 두 월드는 서로 영향을 주지 않는다.
    /// 복제본의 청크는 더티가 아닌 상태로 시작한다.
    /// </summary>
    public World Clone()
    {
        var copy = new World(Config, Rules);
        for (int i = 0; i < LayerCount; i++) copy._layers[i].CopyFrom(_layers[i]);
        copy.Passages.CopyFrom(Passages);
        return copy;
    }

    /// <summary>통로 유무를 설정한다. 바뀌면 두 레이어의 해당 청크를 더티로 표시한다.</summary>
    public WorldError TrySetPassage(int x, int y, bool present)
    {
        if (!Config.InBounds(x, y)) return WorldError.OutOfBounds;
        SetPassageUnchecked(x, y, present);
        return WorldError.None;
    }

    internal bool SetPassageUnchecked(int x, int y, bool present)
    {
        if (!Passages.Set(x, y, present)) return false;
        foreach (var layer in _layers) layer.MarkDirty(x, y);
        return true;
    }

    /// <summary>모든 레이어 내용과 통로, 규칙을 결합한 해시. 동일 조건 재현 검증용.</summary>
    public ulong ComputeContentHash()
    {
        var hash = Fnv1a64.Create();
        hash.Add(Rules.MaxQuantityPerTile);
        foreach (var layer in _layers) hash.Add(layer.ComputeContentHash());
        Passages.AddToHash(ref hash);
        return hash.Value;
    }
}
