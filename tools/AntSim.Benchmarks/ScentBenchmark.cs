using System.Diagnostics;
using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

/// <summary>
/// 냄새 성능 기반 측정(docs/PERF_SCENT_DESIGN.md 7절). 변경 전(main)과 변경 후를 같은 코드로 재도록 공개 API만 쓴다.
/// <list type="bullet">
/// <item>지도: 개미집 프리셋(개미집은 흙) + 통로 굴착 2,000회(1024² 기준, 면적 비례) + 먹이 5,000칸(면적 비례).</item>
/// <item>개미집 판 칸: 통로 굴착 외에 방을 더 파서 열린 칸 수를 1천 / 1만 / 10만으로 맞춘 경우.</item>
/// <item>냄새 1틱: <see cref="ScentSystem.Tick"/> 한 번. 세션 1틱: 냄새 + 감각 + 이동.</item>
/// <item>교차 실행: 모든 조건을 한 바퀴씩 번갈아 rounds번. 한 바퀴에서 조건마다 예열 3회 뒤 5회 중앙값, 바퀴별 중앙값의 중앙값과 범위.</item>
/// </list>
/// </summary>
internal static class ScentBenchmark
{
    private sealed record Case(string Name, Func<double> Run);

    public static void Run(int rounds)
    {
        Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical cores, {Environment.OSVersion}");
        Console.WriteLine($"냄새 측정: 교차 {rounds}바퀴 × (예열 3 + 측정 5)");
        var cases = new List<Case>();

        foreach (int size in new[] { 1024, 2048 })
        foreach (int types in new[] { 2, 4 })
        {
            var world = BuildWorld(size, extraNestTiles: 0);
            var settings = Settings(types);
            var state = new ScentState(world.Config, types);
            var system = new ScentSystem(world);
            for (int t = 0; t < 3; t++) system.Tick(state, settings);
            cases.Add(new Case($"냄새 1틱 | {size}² | {types}종 | 판 칸 통로만", () => Time(() => system.Tick(state, settings))));
        }

        foreach (int dug in new[] { 1_000, 10_000, 100_000 })
        {
            var world = BuildWorld(1024, dug);
            var settings = Settings(2);
            var state = new ScentState(world.Config, 2);
            var system = new ScentSystem(world);
            for (int t = 0; t < 3; t++) system.Tick(state, settings);
            cases.Add(new Case($"냄새 1틱 | 1024² | 2종 | 판 칸 통로 + {dug:N0}", () => Time(() => system.Tick(state, settings))));
        }

        foreach (int ants in new[] { 10_000, 100_000 })
        {
            var session = new SimulationSession(BuildWorld(1024, 0), ScentSettings.Default, AntSettings.Default with { InitialCount = ants, Seed = 7 });
            session.Step(30);
            cases.Add(new Case($"세션 1틱 | 1024² | 2종 | 개미 {ants:N0}", () => Time(() => session.Step())));
        }

        var samples = cases.ToDictionary(c => c.Name, _ => new List<double>());
        for (int round = 0; round < rounds; round++)
            foreach (var c in cases)
            {
                for (int w = 0; w < 3; w++) c.Run();
                var inner = Enumerable.Range(0, 5).Select(_ => c.Run()).OrderBy(v => v).ToArray();
                samples[c.Name].Add(inner[2]);
            }

        Console.WriteLine();
        Console.WriteLine("| 측정 | 지도 | 냄새 종류 | 조건 | 중앙값 ms | 범위 ms |");
        Console.WriteLine("|---|---|---|---|---:|---|");
        foreach (var c in cases)
        {
            var s = samples[c.Name].OrderBy(v => v).ToArray();
            var parts = c.Name.Split('|').Select(p => p.Trim()).ToArray();
            Console.WriteLine($"| {parts[0]} | {parts[1]} | {parts[2]} | {parts[3]} | {s[s.Length / 2]:F3} | {s[0]:F3}~{s[^1]:F3} |");
        }

        // 메모리(관리 힙 할당): 냄새 상태 + 계산 시스템 생성, 첫 틱 포함.
        foreach (int size in new[] { 1024, 2048 })
        {
            var world = BuildWorld(size, 0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var state = new ScentState(world.Config, 2);
            var system = new ScentSystem(world);
            system.Tick(state, Settings(2));
            Console.WriteLine($"냄새 상태 + 시스템 + 첫 틱 할당 {size}², 2종: {(GC.GetAllocatedBytesForCurrentThread() - before) / 1e6:F1} MB");
        }
    }

    private static ScentSettings Settings(int types) => new()
    {
        Types = Enumerable.Range(0, types).Select(i => new ScentTypeSettings(i switch { 0 => "food", 1 => "larva", _ => $"p{i}" }, $"t{i}",
            i switch { 0 => QuantityKind.Food, 1 => QuantityKind.Larva, _ => null })).ToArray(),
    };

    /// <summary>개미집 프리셋 + 통로·먹이(1024² 기준 개수를 면적에 비례) + 개미집 방(판 칸 추가).</summary>
    private static World BuildWorld(int size, int extraNestTiles)
    {
        var world = WorldPresets.StandardNest(new WorldConfig(size, size));
        var editor = new MapEditor(world, historyCapacity: 1);
        int scale = size / 1024 * (size / 1024);
        for (int i = 0; i < 2000 * scale; i++) editor.Apply(EditOp.AddPassage(2 + (i * 37) % (size - 4), 2 + (i * 91) % (size - 4)));
        for (int i = 0; i < 5000 * scale; i++) editor.Apply(EditOp.SetQuantity(LayerId.Surface, (i * 13) % size, (i * 29) % size, QuantityKind.Food, 5));
        // 방: 가운데부터 64×64 방을 줄지어 판다(벽 2칸 간격). 마지막 방은 필요한 칸 수만큼만.
        int remaining = extraNestTiles, room = 0;
        while (remaining > 0)
        {
            int ox = 8 + (room % 14) * 66, oy = 8 + (room / 14) * 66;
            for (int y = oy; y < oy + 64 && remaining > 0; y++)
            for (int x = ox; x < ox + 64 && remaining > 0; x++)
                if (world.Nest.GetState(x, y).HasSolid && editor.Apply(EditOp.RemoveSolid(LayerId.Nest, x, y)).Changed) remaining--;
            room++;
        }
        return world;
    }

    private static double Time(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
