using System.Diagnostics;
using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

/// <summary>
/// 단계 B1 감각 패스 측정(docs/STAGE_B_DESIGN.md F.3). 결과는 docs/BENCHMARKS.md에 기록한다.
/// <list type="bullet">
/// <item>지도: 1024² 개미집 프리셋 + 통로 굴착 2,000회 + 먹이 5,000칸(단계 A 측정과 같음). 개미는 지상에 놓는다.</item>
/// <item>분포: 밀집 = 지상 중앙 64×64 안 균등, 흩어짐 = 지상 1024² 전체 균등(고정 시드).</item>
/// <item>냄새: 종류마다 두 레이어 전체를 0이 아닌 값으로 채운다(0 페이지 공유로 메모리 접근이 싸게 측정되지 않도록).</item>
/// <item>감각: 기본 주기 0.3초, dt 0.1초(틱마다 약 1/3 감지). "감각 패스"는 개미 상태를 고정한 채 감각 패스만 반복한다.</item>
/// <item>교차 실행: 모든 조건을 한 바퀴씩 번갈아 rounds번 돈다. 한 바퀴에서 조건마다 예열 3회 뒤 5회를 재 중앙값을 내고,
/// 바퀴별 중앙값들의 중앙값과 범위(최소~최대)를 보고한다.</item>
/// </list>
/// </summary>
internal static class SensingBenchmark
{
    private const float Dt = 0.1f;
    private static readonly int[] Counts = { 100, 1_000, 10_000, 100_000 };

    private sealed record Case(string Name, Func<double> Run);

    public static void Run(int rounds)
    {
        Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical cores, {Environment.OSVersion}, Release={!Debugger.IsAttached}");
        Console.WriteLine($"단계 B1 감각 측정: 1024², 교차 {rounds}바퀴 × (예열 3 + 측정 5)");
        var config = new WorldConfig(1024, 1024);
        var world = WorldPresets.StandardNest(config);
        var editor = new MapEditor(world);
        for (int i = 0; i < 2000; i++) editor.Apply(EditOp.AddPassage(2 + (i * 37) % 1020, 2 + (i * 91) % 1020));
        for (int i = 0; i < 5000; i++) editor.Apply(EditOp.SetQuantity(LayerId.Surface, (i * 13) % 1024, (i * 29) % 1024, QuantityKind.Food, 5));

        var scent8 = new ScentSettings
        {
            Types = Enumerable.Range(0, 8).Select(i => new ScentTypeSettings(i switch { 0 => "food", 1 => "larva", _ => $"p{i}" }, $"t{i}",
                i switch { 0 => QuantityKind.Food, 1 => QuantityKind.Larva, _ => null })).ToArray(),
        };
        var state = new ScentState(config, 8);
        var fill = new Random(1);
        for (int t = 0; t < 8; t++)
        for (int l = 0; l < World.LayerCount; l++)
        {
            var field = state.MutableField(t, (LayerId)l);
            for (int i = 0; i < field.Length; i++) field[i] = (float)fill.NextDouble();
        }
        var masks = new ScentSystem(world).OpenMasks();

        // 개미 배치(분포·수·K별). 같은 (분포, 수)는 K와 무관하게 같은 위치·방향이다.
        var populations = new Dictionary<(string Dist, int N, int K), AntPopulation>();
        foreach (string dist in new[] { "밀집", "흩어짐" })
        foreach (int n in Counts)
        foreach (int k in new[] { 2, 8 })
            populations[(dist, n, k)] = Place(world, n, k, dist == "밀집" ? 64 : 1024);

        var cases = new List<Case>();
        var allocations = new List<string>();
        foreach (string dist in new[] { "밀집", "흩어짐" })
        foreach (int n in Counts)
        {
            // Stage A만 / Stage A + 감각(기본 K = 2, 거리 1, 이중선형). 이동은 상태를 바꾸므로 각자 복사본을 쓴다.
            var moveOnly = Place(world, n, 2, dist == "밀집" ? 64 : 1024);
            var moveAndSense = Place(world, n, 2, dist == "밀집" ? 64 : 1024);
            var moveWorld = world.Clone();
            var system = new AntSystem();
            var settings = AntSettings.Default;
            var sense2 = AntSensingConfig.Create(settings, scent8);
            long tick = 0;
            cases.Add(new Case($"{dist,-3} {n,7} | Stage A만 1틱", () => Time(() => system.Tick(moveOnly, moveWorld, settings, Dt))));
            cases.Add(new Case($"{dist,-3} {n,7} | Stage A + 감각 1틱 (K2 d1 이중선형)", () => Time(() =>
            {
                AntSensing.Sense(moveAndSense, state, masks, sense2, Dt, ++tick);
                system.Tick(moveAndSense, moveWorld, settings, Dt);
            })));
            foreach (int k in new[] { 2, 8 })
            foreach (float d in new[] { 1f, 2f })
            foreach (var sampling in new[] { SenseSampling.Bilinear, SenseSampling.Nearest })
            {
                var ants = populations[(dist, n, k)];
                var sensing = AntSensingConfig.Create(AntSettings.Default with
                {
                    SensorDistance = d, SensedScentKeys = scent8.Types.Take(k).Select(t => t.Key).ToArray(),
                }, scent8);
                long senseTick = 0;
                string name = $"{dist,-3} {n,7} | 감각 패스 K{k} d{d:0} {(sampling == SenseSampling.Bilinear ? "이중선형" : "최근접")}";
                cases.Add(new Case(name, () => Time(() => AntSensing.Sense(ants, state, masks, sensing, Dt, ++senseTick, sampling))));
                if (n == 100_000 && d == 1 && sampling == SenseSampling.Bilinear)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int t = 0; t < 100; t++) AntSensing.Sense(ants, state, masks, sensing, Dt, ++senseTick, sampling);
                    allocations.Add($"{dist} 100,000 K{k}: 감각 패스 100회 할당 {GC.GetAllocatedBytesForCurrentThread() - before}바이트");
                }
            }
        }

        // 교차 실행
        var samples = cases.ToDictionary(c => c.Name, _ => new List<double>());
        for (int round = 0; round < rounds; round++)
            foreach (var c in cases)
            {
                for (int w = 0; w < 3; w++) c.Run();
                var inner = Enumerable.Range(0, 5).Select(_ => c.Run()).OrderBy(v => v).ToArray();
                samples[c.Name].Add(inner[2]);
            }

        Console.WriteLine();
        Console.WriteLine("| 분포 | 개미 수 | 측정 | 중앙값 ms | 범위 ms |");
        Console.WriteLine("|---|---:|---|---:|---|");
        foreach (var c in cases)
        {
            var s = samples[c.Name].OrderBy(v => v).ToArray();
            var parts = c.Name.Split('|');
            var head = parts[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Console.WriteLine($"| {head[0]} | {int.Parse(head[1]):N0} | {parts[1].Trim()} | {s[s.Length / 2]:F3} | {s[0]:F3}~{s[^1]:F3} |");
        }

        Console.WriteLine();
        foreach (string line in allocations) Console.WriteLine(line);

        // 세션 전체(냄새 2종 + 감각 + 이동): 10,000마리 2,000틱 누적 할당, 100,000마리 1틱 시간.
        foreach (int n in new[] { 10_000, 100_000 })
        {
            var session = new SimulationSession(world, ScentSettings.Default, AntSettings.Default with { InitialCount = n, Seed = 7 });
            session.Step(30);
            if (n == 10_000)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                session.Step(2000);
                Console.WriteLine($"세션 10,000마리 2,000틱 누적 할당: {GC.GetAllocatedBytesForCurrentThread() - before}바이트 (냄새 캐시 갱신 열거자 32바이트/틱은 B1 이전부터 있음)");
            }
            else
            {
                var ticks = Enumerable.Range(0, 15).Select(_ => Time(() => session.Step())).OrderBy(v => v).ToArray();
                Console.WriteLine($"세션 100,000마리 1틱(냄새 2종 + 감각 + 이동, 통로 생성 후 30틱): 중앙값 {ticks[7]:F2} ms (범위 {ticks[0]:F2}~{ticks[^1]:F2})");
            }
        }

        // 메모리: K별 개체 배열 크기(구조 계산)와 실제 할당.
        foreach (int k in new[] { 0, 2, 8 })
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var p = new AntPopulation();
            p.ConfigureSensing(k);
            p.Grow(100_000);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"개체 배열 100,000마리 K={k}: 할당 {allocated / 1e6:F2} MB (구조 계산 {(63 + 42 + 12 * k) * 100_000 / 1e6:F2} MB)");
        }
    }

    private static AntPopulation Place(World world, int n, int k, int span)
    {
        var ants = new AntPopulation();
        ants.ConfigureSensing(k);
        var settings = AntSettings.Default;
        var random = new Random(12345 + n + span);
        int offset = (1024 - span) / 2;
        while (ants.Count < n)
        {
            float x = offset + (float)random.NextDouble() * span, y = offset + (float)random.NextDouble() * span;
            float h = (float)(random.NextDouble() * 2 * Math.PI - Math.PI);
            AntSpawner.TrySpawn(ants, world, settings, LayerId.Surface, x, y, out _, heading: h);
        }
        return ants;
    }

    private static double Time(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
