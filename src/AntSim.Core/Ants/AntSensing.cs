using AntSim.Core.Scent;
using AntSim.Core.Worlds;

namespace AntSim.Core.Ants;

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
/// 단계 B1 페로몬 감각(docs/STAGE_B_DESIGN.md D.1~D.3, 게임형 단순화). 냄새 틱 뒤, 개미 이동 전에 별도 패스로 실행한다.
/// <list type="bullet">
/// <item>스케줄: 개미별 카운트다운. 감지한 틱 뒤 타이머는 항상 (0, P]이며 한 틱에 한 번만 감지한다. 난수를 쓰지 않는다.</item>
/// <item>센서: 왼쪽(Heading − θ) / 정면 / 오른쪽(Heading + θ), 개미 위치에서 거리 d.</item>
/// <item>샘플링: 센서점이 있는 칸의 농도를 그대로 읽는다(보간 없음). 센서 칸이 맵 밖이거나 고체면 그 센서는 값 0, 차폐 비트 1.
/// 물은 가리지 않는다(냄새 확산과 같은 "고체 = 막힘"). 개미와 센서 칸 사이의 칸은 보지 않는다(거리 2에서는 얇은 벽 너머 칸을 읽을 수 있다).
/// 센서 칸 판정은 센서마다 한 번 하고 K개 종류가 공유한다.</item>
/// <item>개미가 있는 레이어의 농도만 읽는다. 이동 상태(위치·방향·이동 난수)는 읽기만 한다.</item>
/// </list>
/// 틱당 할당 없음. 쓰기 위치가 개미별로 분리되어 있다.
/// </summary>
public static class AntSensing
{
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
    internal static void Sense(AntPopulation ants, ScentState scent, byte[][] open, AntSensingConfig config, float dt, long tick)
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
                if (!SensorTile(open[layer], width, height, sx, sy, out int tile))
                {
                    blocked |= (byte)(1 << d);
                    for (int t = 0; t < k; t++) output[t * AntPopulation.SensorCount + d] = 0;
                    continue;
                }
                for (int t = 0; t < k; t++)
                    output[t * AntPopulation.SensorCount + d] = scent.CurrentArray(types[t], (LayerId)layer)[tile];
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

    /// <summary>
    /// 센서점 (sx, sy)가 있는 칸의 선형 인덱스. 맵 밖이거나 고체 칸이면 false(차폐).
    /// </summary>
    internal static bool SensorTile(byte[] open, int width, int height, float sx, float sy, out int index)
    {
        index = 0;
        if (!(sx >= 0 && sy >= 0 && sx < width && sy < height)) return false;
        int tx = (int)MathF.Floor(sx), ty = (int)MathF.Floor(sy);
        if ((uint)tx >= (uint)width || (uint)ty >= (uint)height) return false; // float 경계(sx가 width 바로 아래에서 반올림)
        int i = ty * width + tx;
        if (open[i] == 0) return false;
        index = i;
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
            if (!SensorTile(open[layer], width, height, sx, sy, out _)) blocked |= (byte)(1 << d);
        }
    }
}
