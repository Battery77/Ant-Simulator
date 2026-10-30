namespace AntSim.Core.Ants;

/// <summary>먹이 + 유충 수량이 <see cref="MinQuantity"/> 이상인 타일에서의 이동 속도 배율.</summary>
public readonly record struct SpeedTier(int MinQuantity, float Factor);

/// <summary>
/// 개미 생성·이동 설정. 실험마다 바꿀 수 있도록 값을 코드에서 분리했다(단위: 타일, 초, 도).
/// 변경은 시뮬레이션 초기화 시점에 적용된다.
/// </summary>
public sealed record AntSettings
{
    public const int MaxCount = 100_000;

    // 아래 None/Default보다 먼저 초기화되어야 한다(정적 초기화는 선언 순서).
    public static IReadOnlyList<SpeedTier> DefaultQuantitySpeedTiers { get; } = new[]
    {
        new SpeedTier(0, 1.00f),
        new SpeedTier(1, 0.95f),
        new SpeedTier(4, 0.90f),
        new SpeedTier(8, 0.85f),
    };

    /// <summary>개미 없음(기존 동작). 코어 기본값.</summary>
    public static AntSettings None { get; } = new() { InitialCount = 0 };

    public static AntSettings Default { get; } = new();

    /// <summary>시뮬레이션 시작·초기화 시 생성할 일개미 수.</summary>
    public int InitialCount { get; init; } = 100;

    /// <summary>생성 위치와 개체별 난수의 시드. 같은 시드·월드·설정이면 같은 결과.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>기본 이동 속도(타일/초)와 개체별 편차(±비율, 균등 분포).</summary>
    public float BaseSpeed { get; init; } = 3f;
    public float SpeedVariation { get; init; } = 0.20f;

    /// <summary>표시 크기(몸 길이, 타일)와 개체별 편차. 이동 판정에는 쓰이지 않는다.</summary>
    public float BaseSize { get; init; } = 0.8f;
    public float SizeVariation { get; init; } = 0.15f;

    /// <summary>무작위 탐색: 목표 방향을 다시 고르는 간격(초)과 개체별 간격 편차(±비율).</summary>
    public float TurnIntervalMin { get; init; } = 0.5f;
    public float TurnIntervalMax { get; init; } = 1.0f;
    public float TurnIntervalVariation { get; init; } = 0.15f;

    /// <summary>새 목표 방향은 현재 방향 ± 이 각도 안에서 고른다.</summary>
    public float WanderAngleDegrees { get; init; } = 30f;

    /// <summary>목표 방향으로 회전하는 최대 속도(도/초).</summary>
    public float TurnRateDegreesPerSecond { get; init; } = 240f;

    /// <summary>먹이 + 유충 수량에 따른 속도 배율 구간(MinQuantity 오름차순, 첫 구간은 0).</summary>
    public IReadOnlyList<SpeedTier> QuantitySpeedTiers { get; init; } = DefaultQuantitySpeedTiers;

    /// <summary>타일이 바뀔 때 속도 배율이 새 값에 다가가는 시간 상수(초). 0이면 즉시.</summary>
    public float SpeedFactorSmoothingSeconds { get; init; } = 0.15f;

    /// <summary>
    /// 통로 중심 타일에 새로 들어섰을 때 반대 레이어로 넘어갈 확률(단계 A의 임시 규칙).
    /// 단계 C에서 목표·행동이 통로 이용을 결정하게 되면 대체된다.
    /// </summary>
    public float PassageUseProbability { get; init; } = 0.5f;

    public float SpeedFactorFor(int quantity)
    {
        // 개미마다 매 틱 호출되므로 인터페이스 foreach(열거자 할당)를 쓰지 않는다.
        var tiers = QuantitySpeedTiers;
        float factor = 1f;
        for (int i = 0; i < tiers.Count; i++)
        {
            if (quantity < tiers[i].MinQuantity) break;
            factor = tiers[i].Factor;
        }
        return factor;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (InitialCount < 0 || InitialCount > MaxCount) errors.Add($"개미 수는 0~{MaxCount}이어야 합니다 (현재 {InitialCount}).");
        Positive(errors, "기본 속도", BaseSpeed);
        Positive(errors, "기본 크기", BaseSize);
        Fraction(errors, "속도 편차", SpeedVariation);
        Fraction(errors, "크기 편차", SizeVariation);
        Fraction(errors, "방향 변경 간격 편차", TurnIntervalVariation);
        Positive(errors, "방향 변경 최소 간격", TurnIntervalMin);
        if (!(TurnIntervalMax >= TurnIntervalMin)) errors.Add("방향 변경 최대 간격은 최소 간격 이상이어야 합니다.");
        if (!(WanderAngleDegrees >= 0 && WanderAngleDegrees <= 180)) errors.Add("탐색 회전 각도는 0~180도여야 합니다.");
        Positive(errors, "회전 속도", TurnRateDegreesPerSecond);
        if (!(SpeedFactorSmoothingSeconds >= 0) || float.IsInfinity(SpeedFactorSmoothingSeconds)) errors.Add("감속 완화 시간은 0 이상이어야 합니다.");
        if (!(PassageUseProbability >= 0 && PassageUseProbability <= 1)) errors.Add("통로 이용 확률은 0~1이어야 합니다.");

        if (QuantitySpeedTiers.Count == 0 || QuantitySpeedTiers[0].MinQuantity != 0)
            errors.Add("속도 구간은 수량 0부터 시작해야 합니다.");
        for (int i = 0; i < QuantitySpeedTiers.Count; i++)
        {
            var tier = QuantitySpeedTiers[i];
            if (!(tier.Factor > 0 && tier.Factor <= 1)) errors.Add($"속도 배율은 0 초과 1 이하여야 합니다 (구간 {tier.MinQuantity}).");
            if (i > 0 && tier.MinQuantity <= QuantitySpeedTiers[i - 1].MinQuantity) errors.Add("속도 구간은 수량 오름차순이어야 합니다.");
        }
        return errors;
    }

    private static void Positive(List<string> errors, string name, float value)
    {
        if (!(value > 0) || float.IsInfinity(value)) errors.Add($"{name}는 0보다 큰 유한한 값이어야 합니다 (현재 {value}).");
    }

    private static void Fraction(List<string> errors, string name, float value)
    {
        if (!(value >= 0 && value < 1)) errors.Add($"{name}는 0 이상 1 미만이어야 합니다 (현재 {value}).");
    }
}
