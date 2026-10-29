using AntSim.Core.Content;

namespace AntSim.Core.Worlds;

/// <summary>
/// 시뮬레이션 월드 데이터의 루트. 지상/개미집 두 레이어와 타일·블록 타입 레지스트리를 소유한다.
/// Godot 타입에 의존하지 않으며, 렌더링은 이 데이터를 읽기만 한다.
/// </summary>
public sealed class World
{
    public const int LayerCount = 2;

    private readonly WorldLayer[] _layers;

    /// <param name="defaultTile">모든 레이어를 처음 채울 타일 타입.</param>
    public World(WorldConfig config, TypeRegistry tileTypes, TypeRegistry blockTypes, ushort defaultTile)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tileTypes);
        ArgumentNullException.ThrowIfNull(blockTypes);
        if (!tileTypes.Contains(defaultTile))
            throw new ArgumentOutOfRangeException(nameof(defaultTile), defaultTile, "등록되지 않은 기본 타일 타입입니다.");
        if (!blockTypes.HasEmpty)
            throw new ArgumentException("블록 레지스트리는 ID 0을 '블록 없음'으로 예약해야 합니다. TypeRegistry.CreateWithEmpty를 사용하세요.", nameof(blockTypes));

        Config = config;
        TileTypes = tileTypes;
        BlockTypes = blockTypes;
        _layers = new WorldLayer[LayerCount];
        for (int i = 0; i < LayerCount; i++)
            _layers[i] = new WorldLayer((LayerId)i, config, tileTypes, blockTypes, defaultTile);
    }

    public WorldConfig Config { get; }
    public TypeRegistry TileTypes { get; }
    public TypeRegistry BlockTypes { get; }

    public WorldLayer Surface => _layers[(int)LayerId.Surface];
    public WorldLayer Nest => _layers[(int)LayerId.Nest];

    public IReadOnlyList<WorldLayer> Layers => _layers;

    public WorldLayer GetLayer(LayerId id)
    {
        if ((uint)id >= LayerCount) throw new ArgumentOutOfRangeException(nameof(id), id, "알 수 없는 레이어입니다.");
        return _layers[(int)id];
    }

    /// <summary>모든 레이어 내용을 결합한 해시. 동일 조건 재현 검증용.</summary>
    public ulong ComputeContentHash()
    {
        var hash = Util.Fnv1a64.Create();
        foreach (var layer in _layers) hash.Add(layer.ComputeContentHash());
        return hash.Value;
    }
}
