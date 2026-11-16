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

    /// <summary>속도 구간 수 상한. 검증·저장·불러오기가 같은 값을 쓴다(저장할 수 있으면 불러올 수 있다).</summary>
    public const int MaxSpeedTiers = 256;

    // 지원 범위(감사 SA03). 개체 편차(±1 미만)를 곱해도 2배 미만이고, 최대 틱 간격(10초)을 곱해도 float가 넘치지 않는다
    // (이동 거리 < 2,000 × 10 = 20,000타일, 방향 변경 간격 < 2e6초). 기본값(3타일/초, 0.8타일, 0.5~1초)보다 수백 배 이상 크다.

    /// <summary>기본 속도 상한(타일/초).</summary>
    public const float MaxBaseSpeed = 1000f;

    /// <summary>표시 크기 상한(타일).</summary>
    public const float MaxBaseSize = 1000f;

    /// <summary>방향 변경 간격 상한(초).</summary>
    public const float MaxTurnIntervalSeconds = 1e6f;

    /// <summary>개체별 값의 상한: 설정 상한 × (1 + 편차 &lt; 1).</summary>
    public const float MaxIndividualSpeed = 2 * MaxBaseSpeed;
    public const float MaxIndividualSize = 2 * MaxBaseSize;
    public const float MaxIndividualTurnTimer = 2 * MaxTurnIntervalSeconds;

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
    /// 틱이 끝난 위치가 새 타일이고 그 타일이 통로 중심일 때 반대 레이어로 넘어갈 확률(단계 A의 임시 규칙, 이동 경로 중간의 통로는 보지 않음).
    /// 단계 C에서 목표·행동이 통로 이용을 결정하게 되면 대체된다.
    /// </summary>
    public float PassageUseProbability { get; init; } = 0.5f;

    // ---------- 단계 B: 감각·경로 적분 설정 (docs/STAGE_B_DESIGN.md C.1) ----------

    /// <summary>감지 주기 상한(초).</summary>
    public const float MaxSensePeriodSeconds = 60f;

    /// <summary>센서 거리 상한(타일). 차폐 DDA의 칸 조회 수를 2⌈d⌉ + 1(= 5) 이하로 묶는다.</summary>
    public const float MaxSensorDistance = 2f;

    /// <summary>좌우 센서 각도 상한(도). 좌우 센서가 개미 뒤쪽으로 가지 않는다.</summary>
    public const float MaxSensorAngleDegrees = 90f;

    /// <summary>경로 적분 오차 σ 상한(타일당 축별 표준편차 계수).</summary>
    public const float MaxPathErrorSigma = 10f;

    /// <summary>동시에 감지할 수 있는 냄새 종류 수 상한(K = 0~8).</summary>
    public const int MaxSensedTypes = 8;

    /// <summary><see cref="SensedScentKeys"/>가 null(기본 규칙)일 때 감지 후보. 현재 냄새 설정에 있는 키만, 이 순서로 쓴다.</summary>
    public static IReadOnlyList<string> DefaultSensedScentKeys { get; } = new[] { "food", "larva" };

    /// <summary>감지 주기(시뮬레이션 시간, 초). 개미마다 id 기반 위상으로 분산된다.</summary>
    public float SensePeriodSeconds { get; init; } = 0.3f;

    /// <summary>개미 위치에서 센서까지의 거리(타일).</summary>
    public float SensorDistance { get; init; } = 1f;

    /// <summary>좌우 센서 각도(도). 왼쪽 = Heading − 각도, 오른쪽 = Heading + 각도.</summary>
    public float SensorAngleDegrees { get; init; } = 45f;

    /// <summary>
    /// 감지할 냄새 키 목록. null이면 기본 규칙(<see cref="DefaultSensedScentKeys"/> 중 냄새 설정에 있는 키만, K = 0~2).
    /// 명시 목록은 모든 키가 냄새 설정에 있어야 하며(없으면 세션 생성·초기화가 거부된다), 감지 값 배열 순서가 된다.
    /// </summary>
    public IReadOnlyList<string>? SensedScentKeys { get; init; }

    /// <summary>경로 적분 오차 σ(단계 B2에서 사용, B1에서는 저장만 한다).</summary>
    public float PathErrorSigma { get; init; } = 0.14f;

    /// <summary>
    /// 감지 키를 냄새 설정에 맞춰 해석한다(세션 생성·초기화·불러오기 때 한 번). 성공하면 키와 냄새 종류 인덱스(감지 값 배열 순서),
    /// 실패하면 오류 문구. 키 목록 자체의 규칙(개수·빈 키·중복)은 <see cref="Validate"/>가 먼저 확인한다.
    /// </summary>
    public bool TryResolveSensedScentKeys(Scent.ScentSettings scent, out string[] keys, out int[] typeIndices, out string? error)
    {
        ArgumentNullException.ThrowIfNull(scent);
        var resolvedKeys = new List<string>();
        var resolvedIndices = new List<int>();
        error = null;
        if (SensedScentKeys is null)
        {
            foreach (string key in DefaultSensedScentKeys)
            {
                int index = scent.IndexOf(key);
                if (index < 0) continue; // 기본 규칙: 없는 기본 키는 쓰지 않는다
                resolvedKeys.Add(key);
                resolvedIndices.Add(index);
            }
        }
        else
        {
            foreach (string key in SensedScentKeys)
            {
                int index = key is null ? -1 : scent.IndexOf(key);
                if (index < 0)
                {
                    error = $"감지할 냄새 키 '{key}'가 냄새 설정에 없습니다.";
                    break;
                }
                resolvedKeys.Add(key!);
                resolvedIndices.Add(index);
            }
        }
        keys = error is null ? resolvedKeys.ToArray() : Array.Empty<string>();
        typeIndices = error is null ? resolvedIndices.ToArray() : Array.Empty<int>();
        return error is null;
    }

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

    /// <summary>
    /// 개미 계산은 틱 간격을 float로 쓴다. double 틱 간격이 float로 0이 되면(약 1.4e-45초 미만) 개미가 있는 실험을 진행할 수 없다.
    /// 냄새만 있는 실험(개미 0마리)에는 적용하지 않는다(PR #2의 냄새 틱 범위는 그대로).
    /// </summary>
    public static bool SupportsTick(double tickSeconds) => (float)tickSeconds > 0 && float.IsFinite((float)tickSeconds);

    public static string TickNotSupportedMessage(double tickSeconds) =>
        $"틱 간격 {tickSeconds}초는 개미 계산(float)에서 0이 되어 개미가 있는 실험에 쓸 수 없습니다.";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (InitialCount < 0 || InitialCount > MaxCount) errors.Add($"개미 수는 0~{MaxCount}이어야 합니다 (현재 {InitialCount}).");
        Positive(errors, "기본 속도", BaseSpeed);
        Positive(errors, "기본 크기", BaseSize);
        if (BaseSpeed > MaxBaseSpeed) errors.Add($"기본 속도는 {MaxBaseSpeed}타일/초 이하여야 합니다 (현재 {BaseSpeed}).");
        if (BaseSize > MaxBaseSize) errors.Add($"기본 크기는 {MaxBaseSize}타일 이하여야 합니다 (현재 {BaseSize}).");
        Fraction(errors, "속도 편차", SpeedVariation);
        Fraction(errors, "크기 편차", SizeVariation);
        Fraction(errors, "방향 변경 간격 편차", TurnIntervalVariation);
        Positive(errors, "방향 변경 최소 간격", TurnIntervalMin);
        if (!(TurnIntervalMax >= TurnIntervalMin)) errors.Add("방향 변경 최대 간격은 최소 간격 이상이어야 합니다.");
        if (!(TurnIntervalMax <= MaxTurnIntervalSeconds))
            errors.Add($"방향 변경 최대 간격은 {MaxTurnIntervalSeconds}초 이하의 유한한 값이어야 합니다 (현재 {TurnIntervalMax}).");
        if (!(WanderAngleDegrees >= 0 && WanderAngleDegrees <= 180)) errors.Add("탐색 회전 각도는 0~180도여야 합니다.");
        Positive(errors, "회전 속도", TurnRateDegreesPerSecond);
        if (!(SpeedFactorSmoothingSeconds >= 0) || float.IsInfinity(SpeedFactorSmoothingSeconds)) errors.Add("감속 완화 시간은 0 이상이어야 합니다.");
        if (!(PassageUseProbability >= 0 && PassageUseProbability <= 1)) errors.Add("통로 이용 확률은 0~1이어야 합니다.");

        if (!(SensePeriodSeconds > 0 && SensePeriodSeconds <= MaxSensePeriodSeconds))
            errors.Add($"감지 주기는 0초 초과 {MaxSensePeriodSeconds}초 이하여야 합니다 (현재 {SensePeriodSeconds}).");
        if (!(SensorDistance > 0 && SensorDistance <= MaxSensorDistance))
            errors.Add($"센서 거리는 0타일 초과 {MaxSensorDistance}타일 이하여야 합니다 (현재 {SensorDistance}).");
        if (!(SensorAngleDegrees > 0 && SensorAngleDegrees <= MaxSensorAngleDegrees))
            errors.Add($"센서 각도는 0도 초과 {MaxSensorAngleDegrees}도 이하여야 합니다 (현재 {SensorAngleDegrees}).");
        if (!(PathErrorSigma >= 0 && PathErrorSigma <= MaxPathErrorSigma))
            errors.Add($"경로 적분 오차 σ는 0 이상 {MaxPathErrorSigma} 이하여야 합니다 (현재 {PathErrorSigma}).");
        if (SensedScentKeys is { } sensed)
        {
            if (sensed.Count > MaxSensedTypes) errors.Add($"감지할 냄새 종류는 {MaxSensedTypes}개 이하여야 합니다 (현재 {sensed.Count}).");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in sensed)
            {
                if (string.IsNullOrEmpty(key)) errors.Add("감지할 냄새 키가 비어 있습니다.");
                else if (!seen.Add(key)) errors.Add($"감지할 냄새 키 '{key}'가 중복됩니다.");
            }
        }

        if (QuantitySpeedTiers.Count > MaxSpeedTiers)
            errors.Add($"속도 구간은 {MaxSpeedTiers}개 이하여야 합니다 (현재 {QuantitySpeedTiers.Count}).");
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
