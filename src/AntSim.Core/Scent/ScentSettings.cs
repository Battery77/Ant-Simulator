using AntSim.Core.Worlds;

namespace AntSim.Core.Scent;

/// <summary>
/// 냄새 종류 하나의 정의와 계수. 새 냄새 종류는 이 항목을 <see cref="ScentSettings.Types"/>에 추가하면 된다.
/// </summary>
/// <param name="Key">저장·식별용 고유 키.</param>
/// <param name="Source">이 냄새를 내는 수량 블록. null이면 타일에서 발생하지 않는다(예: 이후 개미가 남기는 페로몬).</param>
/// <param name="Diffusion">이웃 하나당 교환율 D (1/초).</param>
/// <param name="PassageDiffusion">통로 중심 좌표의 레이어 간 교환율 Dp (1/초).</param>
/// <param name="Decay">지수 감쇠 계수 k (1/초). 한 틱에 C ← C·exp(−k·dt).</param>
/// <param name="Emission">발생 계수 e (농도/초/개). 한 틱에 C ← C + e·dt·수량.</param>
public sealed record ScentTypeSettings(
    string Key,
    string DisplayName,
    QuantityKind? Source,
    double Diffusion = ScentTypeSettings.DefaultDiffusion,
    double PassageDiffusion = ScentTypeSettings.DefaultPassageDiffusion,
    double Decay = ScentTypeSettings.DefaultDecay,
    double Emission = ScentTypeSettings.DefaultEmission)
{
    public const double DefaultDiffusion = 0.6;
    public const double DefaultPassageDiffusion = 0.6;
    public const double DefaultDecay = 0.025;
    public const double DefaultEmission = 1.0;

    /// <summary>한 틱 동안 한 칸에서 빠져나갈 수 있는 최대 비율 (4·D + Dp)·dt. 1 이하여야 농도가 음수가 되지 않는다.</summary>
    public double MaxOutflowFraction(double tickSeconds) => (4 * Diffusion + PassageDiffusion) * tickSeconds;
}

/// <summary>냄새 시뮬레이션 설정. 불변이며, 바꿀 때는 새 인스턴스를 만들어 <see cref="Validate"/>를 통과해야 적용된다.</summary>
public sealed record ScentSettings
{
    public const double DefaultTickSeconds = 0.1;
    public const double MaxTickSeconds = 10;

    public static ScentSettings Default { get; } = new()
    {
        Types = new[]
        {
            new ScentTypeSettings("food", "음식", QuantityKind.Food),
            new ScentTypeSettings("larva", "유충", QuantityKind.Larva),
        },
    };

    /// <summary>냄새 계산 틱 간격(초).</summary>
    public double TickSeconds { get; init; } = DefaultTickSeconds;

    public IReadOnlyList<ScentTypeSettings> Types { get; init; } = Array.Empty<ScentTypeSettings>();

    public ScentSettings WithTick(double tickSeconds) => this with { TickSeconds = tickSeconds };

    public ScentSettings WithType(int index, ScentTypeSettings type)
    {
        var types = Types.ToArray();
        types[index] = type;
        return this with { Types = types };
    }

    public int IndexOf(string key)
    {
        for (int i = 0; i < Types.Count; i++)
            if (Types[i].Key == key) return i;
        return -1;
    }

    /// <summary>설정 오류 목록(비어 있으면 유효). 틱 변경 시 확산 안정성 조건도 여기서 검사한다.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!double.IsFinite(TickSeconds) || TickSeconds <= 0 || TickSeconds > MaxTickSeconds)
            errors.Add($"틱 간격은 0초 초과 {MaxTickSeconds}초 이하여야 합니다 (현재 {TickSeconds}).");
        if (Types.Count == 0) errors.Add("냄새 종류가 하나 이상 있어야 합니다.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Types)
        {
            string name = string.IsNullOrWhiteSpace(type.DisplayName) ? type.Key : type.DisplayName;
            if (string.IsNullOrWhiteSpace(type.Key)) errors.Add("냄새 키가 비어 있습니다.");
            else if (!keys.Add(type.Key)) errors.Add($"냄새 키 '{type.Key}'가 중복됩니다.");
            if (type.Source is { } source && !TileEnums.IsDefined(source)) errors.Add($"{name}: 발생원 종류가 잘못되었습니다.");

            CheckCoefficient(errors, name, "확산 계수", type.Diffusion);
            CheckCoefficient(errors, name, "통로 확산 계수", type.PassageDiffusion);
            CheckCoefficient(errors, name, "감쇠 계수", type.Decay);
            CheckCoefficient(errors, name, "발생 계수", type.Emission);

            if (double.IsFinite(TickSeconds) && TickSeconds > 0)
            {
                double outflow = type.MaxOutflowFraction(TickSeconds);
                if (!(outflow <= 1))
                    errors.Add($"{name}: 확산 안정성 조건 위반 — (4·D + Dp)·dt = {outflow:0.###} > 1. 틱 간격이나 확산 계수를 줄이세요.");
            }
        }
        return errors;
    }

    public bool IsValid => Validate().Count == 0;

    /// <summary>냄새 종류 구성(키 순서)이 같은지. 실행 중에는 계수만 바꿀 수 있다.</summary>
    public bool HasSameTypesAs(ScentSettings other) =>
        Types.Count == other.Types.Count && Types.Select(t => t.Key).SequenceEqual(other.Types.Select(t => t.Key));

    private static void CheckCoefficient(List<string> errors, string name, string label, double value)
    {
        if (!double.IsFinite(value) || value < 0) errors.Add($"{name}: {label}는 0 이상의 유한한 값이어야 합니다 (현재 {value}).");
    }
}
