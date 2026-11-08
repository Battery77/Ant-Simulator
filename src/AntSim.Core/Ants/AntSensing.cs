using AntSim.Core.Scent;
using AntSim.Core.Worlds;

namespace AntSim.Core.Ants;

/// <summary>샘플링 방식. 게임은 항상 <see cref="Bilinear"/>이며, <see cref="Nearest"/>는 벤치마크·테스트 비교용 내부 경로다(설정·저장 없음).</summary>
internal enum SenseSampling
{
    Bilinear,
    Nearest,
}

/// <summary>
/// 세션이 감지 설정을 해석한 결과(생성·초기화·불러오기 때 한 번 만든다). 실행 중에는 바뀌지 않는다.
/// </summary>
public sealed class AntSensingConfig
{
    private AntSensingConfig(float period, float distance, float angleDegrees, string[] keys, int[] typeIndices)
    {
        Period = period;
        Distance = distance;
        AngleDegrees = angleDegrees;
        double radians = angleDegrees * Math.PI / 180;
        CosAngle = (float)Math.Cos(radians);
        SinAngle = (float)Math.Sin(radians);
        Keys = keys;
        TypeIndices = typeIndices;
    }

    public float Period { get; }
    public float Distance { get; }
    public float AngleDegrees { get; }
    internal float CosAngle { get; }
    internal float SinAngle { get; }

    /// <summary>해석된 감지 키(감지 값 배열 순서).</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>키마다 냄새 종류 인덱스.</summary>
    public IReadOnlyList<int> TypeIndices { get; }

    public int TypeCount => TypeIndices.Count;

    /// <summary>개미 설정과 냄새 설정으로 만든다. 감지 키를 해석할 수 없으면 <see cref="ArgumentException"/>.</summary>
    public static AntSensingConfig Create(AntSettings ants, ScentSettings scent)
    {
        ArgumentNullException.ThrowIfNull(ants);
        if (!ants.TryResolveSensedScentKeys(scent, out var keys, out var indices, out string? error))
            throw new ArgumentException(error, nameof(ants));
        return new AntSensingConfig(ants.SensePeriodSeconds, ants.SensorDistance, ants.SensorAngleDegrees, keys, indices);
    }
}

/// <summary>
/// 단계 B1 페로몬 감각(docs/STAGE_B_DESIGN.md D.1~D.3). 냄새 틱 뒤, 개미 이동 전에 별도 패스로 실행한다.
/// <list type="bullet">
/// <item>스케줄: 개미별 카운트다운. 감지한 틱 뒤 타이머는 항상 (0, P]이며 한 틱에 한 번만 감지한다. 난수를 쓰지 않는다.</item>
/// <item>센서: 왼쪽(Heading − θ) / 정면 / 오른쪽(Heading + θ), 개미 위치에서 거리 d.</item>
/// <item>차폐: 개미 칸 → 센서 칸 격자 DDA가 고체 칸·맵 밖을 지나면 그 센서는 값 0, 차폐 비트 1. 물은 가리지 않는다(냄새 확산과 같은 "고체 = 막힘").</item>
/// <item>보간: 칸 중심 기준 이중선형. 2×2 샘플 중 맵 밖·고체 칸과 센서 칸에서 보이지 않는 대각 칸(사이의 두 칸이 모두 고체)은 빼고
/// 남은 가중치로 다시 정규화한다. 차폐·가중치는 센서마다 한 번 계산해 K개 종류가 공유한다.</item>
/// <item>개미가 있는 레이어의 농도만 읽는다. 이동 상태(위치·방향·이동 난수)는 읽기만 한다.</item>
/// </list>
/// 틱당 할당 없음. 쓰기 위치가 개미별로 분리되어 있다.
/// </summary>
public static class AntSensing
{
    /// <summary>차폐 DDA 반복 상한(거리 ≤ 2이면 칸 이동은 최대 5번). 수치 예외에 대비한 방어선.</summary>
    private const int MaxDdaSteps = 8;

    /// <summary>생성 시 초기 위상: P · (h(id) + 1) / 2^32 ∈ (0, P]. h(id) = (uint)(id · 2654435761).</summary>
    public static float InitialPhase(int id, float period)
    {
        uint h = unchecked((uint)id * 2654435761u);
        return Normalize((float)(period * ((h + 1.0) / 4294967296.0)), period);
    }

    /// <summary>타이머를 (0, P]로 맞춘다(float 반올림·NaN 방어). 범위 안이면 그대로.</summary>
    internal static float Normalize(float timer, float period) => timer > 0 && timer <= period ? timer : period;

    /// <summary>
    /// 타이머 한 틱 진행. 감지할 차례면 true이고 <paramref name="timer"/>는 다음 감지까지 남은 시간((0, P])이 된다.
    /// 감지하지 않으면 timer − dt(> 0). dt ≥ P여도 한 틱에 한 번만 감지한다.
    /// </summary>
    internal static bool AdvanceTimer(ref float timer, float dt, float period)
    {
        float t = timer - dt;
        if (t <= 0)
        {
            double k = Math.Floor(-(double)t / period) + 1; // t = 0이면 k = 1
            timer = Normalize((float)(t + k * period), period);
            return true;
        }
        timer = Normalize(t, period);
        return false;
    }

    /// <summary>
    /// 감각 패스. <paramref name="tick"/>은 이 틱이 끝난 뒤의 틱 번호(감지 틱으로 기록). <paramref name="open"/>은 레이어별 냄새 열린 칸(1 = 비고체).
    /// </summary>
    internal static void Sense(AntPopulation ants, ScentState scent, byte[][] open, AntSensingConfig config, float dt, long tick,
        SenseSampling sampling = SenseSampling.Bilinear)
    {
        if (ants.SensedTypeCount != config.TypeCount) throw new InvalidOperationException("개미 감지 종류 수가 감지 설정과 다릅니다.");
        int width = scent.Config.Width, height = scent.Config.Height;
        int k = config.TypeCount, stride = k * AntPopulation.SensorCount;
        float period = config.Period;

        var x = ants.XArray; var y = ants.YArray; var heading = ants.HeadingArray; var layers = ants.LayerArray;
        var timer = ants.SenseTimerArray; var senseTick = ants.SenseTickArray; var senseHeading = ants.SenseHeadingArray;
        var flags = ants.SenseFlagsArray; var values = ants.SenseValuesArray;

        // 종류별 농도 배열(레이어 2개). 스택 버퍼라 할당이 없다.
        Span<int> types = stackalloc int[AntSettings.MaxSensedTypes];
        for (int t = 0; t < k; t++) types[t] = config.TypeIndices[t];

        var geometry = new SensorGeometry(config.Distance, config.CosAngle, config.SinAngle);
        for (int i = 0; i < ants.Count; i++)
        {
            if (!AdvanceTimer(ref timer[i], dt, period)) continue;
            int layer = layers[i];
            float h = heading[i];
            senseTick[i] = tick;
            senseHeading[i] = h;
            byte blocked = 0;
            var output = values.AsSpan(i * stride, stride);
            geometry.Compute(x[i], y[i], h, out var sensors);
            for (int d = 0; d < AntPopulation.SensorCount; d++)
            {
                var (sx, sy) = sensors[d];
                if (!SampleWeights(open[layer], width, height, x[i], y[i], sx, sy, sampling, out var weights))
                {
                    blocked |= (byte)(1 << d);
                    for (int t = 0; t < k; t++) output[t * AntPopulation.SensorCount + d] = 0;
                    continue;
                }
                for (int t = 0; t < k; t++)
                    output[t * AntPopulation.SensorCount + d] = weights.Apply(scent.CurrentArray(types[t], (LayerId)layer));
            }
            flags[i] = blocked;
        }
    }

    /// <summary>센서 세 점(왼쪽·정면·오른쪽)의 좌표 계산. sin/cos은 개미당 한 번, 센서 각도는 설정에서 미리 계산한 값으로 회전한다.</summary>
    internal readonly struct SensorGeometry(float distance, float cosAngle, float sinAngle)
    {
        public void Compute(float x, float y, float heading, out Sensors sensors)
        {
            var (s, c) = MathF.SinCos(heading);
            float d = distance;
            sensors = new Sensors(
                (x + d * (c * cosAngle + s * sinAngle), y + d * (s * cosAngle - c * sinAngle)),
                (x + d * c, y + d * s),
                (x + d * (c * cosAngle - s * sinAngle), y + d * (s * cosAngle + c * sinAngle)));
        }
    }

    internal readonly struct Sensors((float X, float Y) left, (float X, float Y) front, (float X, float Y) right)
    {
        public (float X, float Y) this[int index] => index switch { 0 => left, 1 => front, _ => right };
    }

    /// <summary>센서 하나의 샘플 칸과 정규화된 가중치(최대 4칸, 쓰지 않는 칸은 가중치 0·인덱스 0).</summary>
    internal struct Weights
    {
        public int I0, I1, I2, I3;
        public float W0, W1, W2, W3;

        public readonly float Apply(float[] field)
        {
            float v = W0 * field[I0] + W1 * field[I1] + W2 * field[I2] + W3 * field[I3];
            // 볼록 결합이라 이론상 샘플 범위 안이지만, 반올림으로 지원 상한을 넘지 않도록 맞춘다.
            return MathF.Min(v, ScentState.MaxConcentration);
        }
    }

    /// <summary>
    /// 센서점 (sx, sy)를 읽을 수 있으면 true와 샘플 가중치, 차폐(맵 밖·고체 칸 통과)면 false.
    /// </summary>
    internal static bool SampleWeights(byte[] open, int width, int height, float ax, float ay, float sx, float sy, SenseSampling sampling,
        out Weights weights)
    {
        weights = default;
        if (!(sx >= 0 && sy >= 0 && sx < width && sy < height)) return false;
        int tx = (int)MathF.Floor(sx), ty = (int)MathF.Floor(sy);
        if (tx >= width || ty >= height) return false; // float 경계(sx가 width 바로 아래에서 반올림)
        if (!PathOpen(open, width, height, ax, ay, sx, sy, tx, ty)) return false;

        int center = ty * width + tx;
        if (sampling == SenseSampling.Nearest)
        {
            weights.I0 = weights.I1 = weights.I2 = weights.I3 = center;
            weights.W0 = 1;
            return true;
        }

        float u = sx - 0.5f, v = sy - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        float w0 = (1 - fx) * (1 - fy), w1 = fx * (1 - fy), w2 = (1 - fx) * fy, w3 = fx * fy;
        if (!Visible(open, width, height, tx, ty, x0, y0, out int i0)) w0 = 0;
        if (!Visible(open, width, height, tx, ty, x0 + 1, y0, out int i1)) w1 = 0;
        if (!Visible(open, width, height, tx, ty, x0, y0 + 1, out int i2)) w2 = 0;
        if (!Visible(open, width, height, tx, ty, x0 + 1, y0 + 1, out int i3)) w3 = 0;
        float sum = w0 + w1 + w2 + w3;
        if (!(sum > 0))
        {
            // 센서 칸의 가중치는 항상 0.25 이상이라 오지 않는 경로(방어용): 센서 칸 값을 그대로 쓴다.
            weights.I0 = weights.I1 = weights.I2 = weights.I3 = center;
            weights.W0 = 1;
            return true;
        }
        float inv = 1 / sum;
        weights.I0 = i0; weights.I1 = i1; weights.I2 = i2; weights.I3 = i3;
        weights.W0 = w0 * inv; weights.W1 = w1 * inv; weights.W2 = w2 * inv; weights.W3 = w3 * inv;
        return true;
    }

    private static bool IsOpen(byte[] open, int width, int height, int x, int y) =>
        (uint)x < (uint)width && (uint)y < (uint)height && open[y * width + x] != 0;

    /// <summary>
    /// 샘플 칸 (x, y)가 센서 칸 (tx, ty)에서 보이는가: 맵 안의 열린 칸이고, 센서 칸 자신·상하좌우 이웃이거나,
    /// 대각 이웃이면 사이의 두 칸 중 하나 이상이 열려 있어야 한다. 보이면 선형 인덱스를 준다(아니면 0).
    /// </summary>
    private static bool Visible(byte[] open, int width, int height, int tx, int ty, int x, int y, out int index)
    {
        index = 0;
        if (!IsOpen(open, width, height, x, y)) return false;
        if (x != tx && y != ty && !IsOpen(open, width, height, x, ty) && !IsOpen(open, width, height, tx, y)) return false;
        index = y * width + x;
        return true;
    }

    /// <summary>
    /// 개미 위치에서 센서점까지 선분이 지나는 칸(격자 DDA, 개미 이동 <see cref="AntMovement.Sweep"/>과 같은 순회: 모서리를 정확히 지나면
    /// x축 칸을 먼저 거친다)이 모두 맵 안의 열린 칸인가. 개미 칸과 센서 칸도 확인한다.
    /// </summary>
    private static bool PathOpen(byte[] open, int width, int height, float x, float y, float sx, float sy, int targetX, int targetY)
    {
        int tx = (int)MathF.Floor(x), ty = (int)MathF.Floor(y);
        if (!IsOpen(open, width, height, tx, ty) || !IsOpen(open, width, height, targetX, targetY)) return false;
        float dx = sx - x, dy = sy - y;
        int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
        float tDeltaX = dx != 0 ? 1 / MathF.Abs(dx) : float.PositiveInfinity;
        float tDeltaY = dy != 0 ? 1 / MathF.Abs(dy) : float.PositiveInfinity;
        float tMaxX = dx > 0 ? (tx + 1 - x) / dx : dx < 0 ? (x - tx) / -dx : float.PositiveInfinity;
        float tMaxY = dy > 0 ? (ty + 1 - y) / dy : dy < 0 ? (y - ty) / -dy : float.PositiveInfinity;
        for (int step = 0; step < MaxDdaSteps && (tx != targetX || ty != targetY); step++)
        {
            if (tMaxX <= tMaxY)
            {
                if (tMaxX > 1) break;
                tx += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                if (tMaxY > 1) break;
                ty += stepY;
                tMaxY += tDeltaY;
            }
            if (!IsOpen(open, width, height, tx, ty)) return false;
        }
        return true;
    }

    /// <summary>
    /// 선택한 개미 표시용: 현재 위치·방향 기준 센서 세 점과 각 센서의 현재 차폐 여부(상태를 바꾸지 않는다).
    /// </summary>
    internal static void Preview(float x, float y, float heading, int layer, byte[][] open, int width, int height, AntSensingConfig config,
        Span<float> points, out byte blocked)
    {
        var geometry = new SensorGeometry(config.Distance, config.CosAngle, config.SinAngle);
        geometry.Compute(x, y, heading, out var sensors);
        blocked = 0;
        for (int d = 0; d < AntPopulation.SensorCount; d++)
        {
            var (sx, sy) = sensors[d];
            points[2 * d] = sx;
            points[2 * d + 1] = sy;
            if (!SampleWeights(open[layer], width, height, x, y, sx, sy, SenseSampling.Bilinear, out _)) blocked |= (byte)(1 << d);
        }
    }
}
