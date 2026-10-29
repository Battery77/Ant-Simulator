namespace AntSim.Core.Content;

/// <summary>타일 또는 블록 타입 정의. 게임 규칙(통행 가능 여부 등)은 아직 정하지 않았으므로 식별 정보만 가진다.</summary>
public sealed record TypeDefinition(ushort Id, string Key, string DisplayName);

/// <summary>
/// 문자열 키 ↔ 숫자 ID 매핑. ID는 등록 순서대로 0부터 부여되므로 같은 순서로 등록하면 항상 같은 ID가 나온다.
/// 월드 데이터에는 ushort ID만 저장한다.
/// </summary>
public sealed class TypeRegistry
{
    /// <summary>"없음"을 뜻하는 예약 ID (블록 레지스트리에서 사용).</summary>
    public const ushort EmptyId = 0;
    public const string EmptyKey = "none";

    private readonly List<TypeDefinition> _definitions = new();
    private readonly Dictionary<string, ushort> _byKey = new(StringComparer.Ordinal);

    public TypeRegistry(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
    }

    /// <summary>ID 0을 "none"으로 예약한 레지스트리를 만든다.</summary>
    public static TypeRegistry CreateWithEmpty(string kind)
    {
        var registry = new TypeRegistry(kind);
        registry.Register(EmptyKey, "없음");
        return registry;
    }

    /// <summary>레지스트리 종류 이름(예: "tile", "block"). 오류 메시지와 직렬화에 사용.</summary>
    public string Kind { get; }

    public int Count => _definitions.Count;

    public IReadOnlyList<TypeDefinition> All => _definitions;

    public bool HasEmpty => _definitions.Count > 0 && _definitions[0].Key == EmptyKey;

    public TypeDefinition Register(string key, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_byKey.ContainsKey(key))
            throw new ArgumentException($"{Kind} 타입 키 '{key}'가 이미 등록되어 있습니다.", nameof(key));
        if (_definitions.Count > ushort.MaxValue)
            throw new InvalidOperationException($"{Kind} 타입은 최대 {ushort.MaxValue + 1}개까지 등록할 수 있습니다.");

        var definition = new TypeDefinition((ushort)_definitions.Count, key, displayName);
        _definitions.Add(definition);
        _byKey.Add(key, definition.Id);
        return definition;
    }

    public bool Contains(ushort id) => id < _definitions.Count;

    public TypeDefinition Get(ushort id)
    {
        if (!Contains(id)) throw new ArgumentOutOfRangeException(nameof(id), id, $"등록되지 않은 {Kind} 타입 ID입니다.");
        return _definitions[id];
    }

    public bool TryGetId(string key, out ushort id) => _byKey.TryGetValue(key, out id);

    public ushort GetId(string key) =>
        _byKey.TryGetValue(key, out var id) ? id : throw new KeyNotFoundException($"{Kind} 타입 키 '{key}'를 찾을 수 없습니다.");
}
