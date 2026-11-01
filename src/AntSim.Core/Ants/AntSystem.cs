using AntSim.Core.Worlds;

namespace AntSim.Core.Ants;

/// <summary>
/// 개미 한 틱 갱신(단계 A: 이동). 개미마다 고정 순서로 처리한다:
/// 1) 방향 결정(무작위 탐색: 개체별 간격으로 ±각도 안에서 새 목표 방향, 목표 방향으로 부드럽게 회전)
/// 2) 속도 결정(기본 속도 × 현재 타일 먹이·유충 수량에 따른 배율, 타일이 바뀌면 부드럽게 변함)
/// 3) 경로 검사 이동(막히면 경계 앞에서 멈추고 열린 방향을 새로 고름)
/// 4) 틱이 끝난 위치의 타일이 직전 틱과 다르고 그 타일이 통로 중심이면 확률적으로 레이어 전환(단계 A 임시 규칙: 한 틱 이동 중
///    지나치기만 한 통로 중심은 보지 않는다. 목표 기반 층간 이동은 이후 단계에서 다시 설계한다)
/// 단계 B~D는 1)의 방향 결정에 감각·기억·목표 가중치를 더하는 식으로 확장한다.
/// 모든 난수는 개체별 상태에서 뽑으므로 같은 시드·입력이면 항상 같은 결과다. 틱당 할당 없음.
/// </summary>
public sealed class AntSystem
{
    private static readonly float[] EscapeOffsets = { MathF.PI / 4, MathF.PI / 2, 3 * MathF.PI / 4 };
    private const float ProbeDistance = 0.6f;

    public void Tick(AntPopulation ants, World world, AntSettings settings, float dt)
    {
        if (!(dt > 0)) throw new ArgumentOutOfRangeException(nameof(dt));
        float wander = settings.WanderAngleDegrees * MathF.PI / 180;
        float maxTurn = settings.TurnRateDegreesPerSecond * MathF.PI / 180 * dt;
        float smoothing = settings.SpeedFactorSmoothingSeconds > 0 ? 1 - MathF.Exp(-dt / settings.SpeedFactorSmoothingSeconds) : 1;
        int width = world.Config.Width;

        var x = ants.XArray; var y = ants.YArray;
        var prevX = ants.PrevXArray; var prevY = ants.PrevYArray;
        var heading = ants.HeadingArray; var target = ants.TargetHeadingArray;
        var turnTimer = ants.TurnTimerArray; var turnScale = ants.TurnIntervalScaleArray;
        var baseSpeed = ants.BaseSpeedArray; var speedFactor = ants.SpeedFactorArray;
        var layerIds = ants.LayerArray; var mobility = ants.MobilityArray;
        var lastTile = ants.LastTileArray; var rng = ants.RngArray;

        for (int i = 0; i < ants.Count; i++)
        {
            prevX[i] = x[i];
            prevY[i] = y[i];
            var layer = world.GetLayer((LayerId)layerIds[i]);
            var ability = (AntMobility)mobility[i];

            // 1) 무작위 탐색 방향: 매 틱이 아니라 개체별 간격마다 새 목표 방향을 고른다.
            turnTimer[i] -= dt;
            if (turnTimer[i] <= 0)
            {
                target[i] = WrapAngle(heading[i] + AntRandom.Range(ref rng[i], -wander, wander));
                turnTimer[i] += NextTurnInterval(settings, turnScale[i], ref rng[i]);
            }
            float diff = WrapAngle(target[i] - heading[i]);
            heading[i] = WrapAngle(heading[i] + Math.Clamp(diff, -maxTurn, maxTurn));

            // 2) 속도: 현재 타일의 먹이 + 유충 수량에 따른 배율로 부드럽게 수렴(기본 속도는 그대로).
            int tileX = (int)MathF.Floor(x[i]), tileY = (int)MathF.Floor(y[i]);
            var state = layer.GetState(tileX, tileY);
            float desired = settings.SpeedFactorFor(state.TotalQuantity);
            speedFactor[i] = SmoothSpeedFactor(speedFactor[i], desired, smoothing);

            // 3) 경로 검사 이동.
            float distance = baseSpeed[i] * speedFactor[i] * dt;
            var (sin, cos) = MathF.SinCos(heading[i]);
            float dx = cos * distance, dy = sin * distance;
            float fraction = AntMovement.Sweep(layer, x[i], y[i], dx, dy, ability, out bool blocked);
            float nx = x[i] + dx * fraction, ny = y[i] + dy * fraction;
            if (AntMovement.IsPassable(layer, nx, ny, ability))
            {
                x[i] = nx;
                y[i] = ny;
            }
            if (blocked)
            {
                // 막힌 방향으로 계속 밀지 않고, 열린 방향을 찾아 즉시 돌아선다.
                float escape = ChooseEscapeHeading(layer, x[i], y[i], heading[i], ability, ref rng[i]);
                heading[i] = target[i] = escape;
                turnTimer[i] = NextTurnInterval(settings, turnScale[i], ref rng[i]);
            }

            // 4) 통로: 틱이 끝난 위치가 새 타일이고 그 타일이 통로 중심일 때만 레이어 전환을 시도한다(이동 경로 중간의 통로는 보지 않음).
            tileX = (int)MathF.Floor(x[i]);
            tileY = (int)MathF.Floor(y[i]);
            int tile = tileY * width + tileX;
            if (tile != lastTile[i])
            {
                lastTile[i] = tile;
                if (world.Passages.Contains(tileX, tileY) &&
                    AntRandom.NextFloat(ref rng[i]) < settings.PassageUseProbability)
                {
                    var other = layerIds[i] == (byte)LayerId.Surface ? LayerId.Nest : LayerId.Surface;
                    if (AntMovement.IsPassable(world.GetLayer(other), tileX, tileY, ability))
                    {
                        layerIds[i] = (byte)other;
                        prevX[i] = x[i]; // 레이어가 바뀐 틱은 화면 보간을 하지 않는다
                        prevY[i] = y[i];
                    }
                }
            }
        }
    }

    /// <summary>
    /// 속도 배율을 목표로 다가가게 한다. 실수 계산이면 결과는 항상 이전 값과 목표 사이(둘 다 (0, 1])지만, float에서는
    /// 1 + (1e-8 − 1) = 0처럼 반올림으로 구간을 벗어날 수 있다(감사 SA02). smoothing ≥ 1(평활화 0초, 또는 1 − exp(−dt/T)가 1로 반올림)이면
    /// 목표를 그대로 쓰고, 그 밖에는 결과를 두 값 사이로 맞춘다. 정상 값에서는 이미 구간 안이라 결과가 바뀌지 않는다.
    /// </summary>
    internal static float SmoothSpeedFactor(float current, float desired, float smoothing)
    {
        if (smoothing >= 1) return desired;
        float next = current + (desired - current) * smoothing;
        return Math.Clamp(next, MathF.Min(current, desired), MathF.Max(current, desired));
    }

    internal static float NextTurnInterval(AntSettings settings, float scale, ref ulong rng) =>
        AntRandom.Range(ref rng, settings.TurnIntervalMin, settings.TurnIntervalMax) * scale;

    /// <summary>현재 방향에서 ±45°, ±90°, ±135°(좌우 순서는 무작위), 180° 순으로 짧게 살펴 처음 열린 방향을 고른다.</summary>
    private static float ChooseEscapeHeading(WorldLayer layer, float x, float y, float heading, AntMobility ability, ref ulong rng)
    {
        foreach (float offset in EscapeOffsets)
        {
            float sign = AntRandom.NextFloat(ref rng) < 0.5f ? 1 : -1;
            for (int k = 0; k < 2; k++, sign = -sign)
            {
                float candidate = WrapAngle(heading + sign * offset);
                if (IsOpen(layer, x, y, candidate, ability)) return candidate;
            }
        }
        float back = WrapAngle(heading + MathF.PI);
        if (IsOpen(layer, x, y, back, ability)) return back;
        // 사방이 막힌 경우(1칸 공간): 무작위 방향으로 바꿔 두고 다음 틱에 다시 시도한다.
        return AntRandom.Range(ref rng, -MathF.PI, MathF.PI);
    }

    private static bool IsOpen(WorldLayer layer, float x, float y, float heading, AntMobility ability)
    {
        var (sin, cos) = MathF.SinCos(heading);
        AntMovement.Sweep(layer, x, y, cos * ProbeDistance, sin * ProbeDistance, ability, out bool blocked);
        return !blocked;
    }

    /// <summary>각도를 (−π, π]로 정규화한다.</summary>
    public static float WrapAngle(float angle)
    {
        if (angle > MathF.PI || angle <= -MathF.PI)
        {
            angle %= 2 * MathF.PI;
            if (angle > MathF.PI) angle -= 2 * MathF.PI;
            else if (angle <= -MathF.PI) angle += 2 * MathF.PI;
        }
        return angle;
    }
}
