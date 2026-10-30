using System.Diagnostics;
using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Rendering;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Serialization;
using AntSim.Core.Worlds;

// 기준 성능 측정 도구. 최적화 전후 비교용이며 결과는 docs/BENCHMARKS.md에 기록한다.
// 실행: dotnet run -c Release --project tools/AntSim.Benchmarks [-- <size> <iterations>]

int size = args.Length > 0 ? int.Parse(args[0]) : 1024;
int iterations = args.Length > 1 ? int.Parse(args[1]) : 7;
var config = new WorldConfig(size, size);

Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical cores, {size}x{size} tiles, chunk {config.ChunkSize}, {iterations} runs (median)");
Console.WriteLine();

World world = null!;
Measure("World 생성 (2 레이어)", () => world = new World(config), config.TileCount * 2L);

var water = new TileState(Terrain.Water, SolidBlock.None, 0, 0);
var dirt = new TileState(Terrain.Dirt, SolidBlock.None, 0, 0);
bool flip = false;
Measure("TrySetState 전체 순회 (값 변경)", () =>
{
    var layer = world.Surface;
    var state = (flip = !flip) ? water : dirt;
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
        layer.TrySetState(x, y, state);
}, config.TileCount);

long sink = 0;
Measure("GetState 전체 순회", () =>
{
    var layer = world.Surface;
    long sum = 0;
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
        sum += layer.GetState(x, y).Food;
    sink += sum;
}, config.TileCount);

var row = new byte[size];
Measure("CopyRow 전체 (1 평면)", () =>
{
    for (int y = 0; y < size; y++) world.Surface.CopyRow(TilePlane.Terrain, y, row);
    sink += row[0];
}, config.TileCount);

const int editCount = 100_000;
Measure("MapEditor.Apply 단일 연산 ×100k (+Undo 기록)", () =>
{
    var editor = new MapEditor(world, historyCapacity: editCount);
    for (int i = 0; i < editCount; i++)
        editor.Apply(EditOp.AdjustQuantity(LayerId.Nest, i % size, i / size, QuantityKind.Food, 1));
    while (editor.Undo()) { }
}, editCount * 2L);

Measure("드래그 스트로크 (대각선 왕복 ×50, 1 Undo)", () =>
{
    var editor = new MapEditor(world);
    var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Water), LayerId.Nest, 0, 0);
    for (int k = 0; k < 50; k++) stroke.MoveTo(k % 2 == 0 ? size - 1 : 0, (k * 17) % size);
    stroke.End();
    editor.Undo();
}, 50L * size);

var dirty = new List<Chunk>();
Measure("CollectDirtyChunks", () =>
{
    world.Surface.TrySetState(size / 2, size / 2, (flip = !flip) ? water : dirt);
    dirty.Clear();
    world.Surface.CollectDirtyChunks(dirty);
}, config.ChunkCount);

var rgba = new byte[ChunkRasterizer.BufferSize(config.ChunkSize)];
for (int i = 0; i < 2000; i++) world.TrySetPassage((i * 37) % size, (i * 91) % size, true);
Measure("ChunkRasterizer 전체 레이어", () =>
{
    foreach (var chunk in world.Surface.Chunks)
        ChunkRasterizer.Rasterize(chunk, world.Passages, world.Rules.MaxQuantityPerTile, TilePalette.Default, rgba);
    sink += rgba[0];
}, config.TileCount);

var stream = new MemoryStream();
Measure("MapSerializer.Save", () =>
{
    stream.SetLength(0);
    MapSerializer.Save(world, stream);
}, config.TileCount * 2L);
Console.WriteLine($"  저장 크기: {stream.Length / 1024.0 / 1024.0:F2} MiB");

Measure("MapSerializer.Load", () =>
{
    stream.Position = 0;
    sink += MapSerializer.Load(stream).Passages.Count;
}, config.TileCount * 2L);

Measure("ComputeContentHash", () => sink += (long)world.ComputeContentHash(), config.TileCount * 2L);

Measure("PassageDig ×1000 (원자적 굴착 + Undo 기록)", () =>
{
    var nestWorld = WorldPresets.StandardNest(config);
    var editor = new MapEditor(nestWorld);
    for (int i = 0; i < 1000; i++) editor.Apply(EditOp.AddPassage(2 + (i * 37) % (size - 4), 2 + (i * 91) % (size - 4)));
}, 1000);

// 냄새: 기본 설정(2종) × 2레이어, 개미집 프리셋 + 통로 2000개 + 먹이/유충 발생원.
var scentWorld = WorldPresets.StandardNest(config);
{
    var editor = new MapEditor(scentWorld);
    for (int i = 0; i < 2000; i++) editor.Apply(EditOp.AddPassage(2 + (i * 37) % (size - 4), 2 + (i * 91) % (size - 4)));
    for (int i = 0; i < 5000; i++) editor.Apply(EditOp.SetQuantity(LayerId.Surface, (i * 13) % size, (i * 29) % size, QuantityKind.Food, 5));
}
PrintWorkload(scentWorld);
var scentSession = new SimulationSession(scentWorld, ScentSettings.Default);
scentSession.Step(5);
double tickMs = Measure("냄새 1틱 (2종 × 2레이어)", () => scentSession.Step(), config.TileCount * 4L);
Console.WriteLine($"  0.1초 틱 실시간 재생 시 계산 부하: {tickMs / 100:P0}");

var field = scentSession.Scent.Field(0, LayerId.Surface).ToArray();
var heat = new byte[config.TileCount * 4];
Measure("Heatmap 래스터 (레이어 1장, 선형)", () => HeatmapRasterizer.Rasterize(field, HeatmapRasterizer.ComputeMax(field), HeatmapScale.Linear, heat), config.TileCount);
var larvaField = scentSession.Scent.Field(1, LayerId.Surface).ToArray();
for (int i = 0; i < larvaField.Length; i += 7) larvaField[i] += 0.5f; // 겹침이 많은 경우
var compositePalette = CompositeHeatmapPalette.CreateDefault();
var composite = new CompositeHeatmapRasterizer();
var channels = new[]
{
    new CompositeChannel(field, HeatmapRasterizer.ComputeMax(field), HeatmapScale.Linear, compositePalette.Get("food")),
    new CompositeChannel(larvaField, HeatmapRasterizer.ComputeMax(larvaField), HeatmapScale.Linear, compositePalette.Get("larva")),
};
Measure("Composite 래스터 (2종, 선형)", () => composite.Rasterize(channels, heat), config.TileCount);
var logChannels = channels.Select(c => c with { Scale = HeatmapScale.Log }).ToArray();
Measure("Composite 래스터 (2종, 로그)", () => composite.Rasterize(logChannels, heat), config.TileCount);
Measure("Heatmap 래스터 (레이어 1장, 로그)", () => HeatmapRasterizer.Rasterize(field, HeatmapRasterizer.ComputeMax(field), HeatmapScale.Log, heat), config.TileCount);

Measure("World.Clone (시뮬레이션 시작)", () => sink += scentWorld.Clone().Passages.Count, config.TileCount * 2L);
var expStream = new MemoryStream();
Measure("ExperimentSerializer.Save", () =>
{
    expStream.SetLength(0);
    AntSim.Core.Serialization.ExperimentSerializer.Save(scentSession, expStream);
}, config.TileCount * 2L);
Console.WriteLine($"  실험 파일 크기: {expStream.Length / 1024.0 / 1024.0:F2} MiB");

// 개미(단계 A 이동): 개미집 프리셋 + 통로 2000개, 개미 수를 늘려 가며 1틱 비용을 잰다.
// JIT 단계별 최적화가 끝나도록 1초 동안 먼저 돌린다(그렇지 않으면 앞쪽 측정이 과대평가된다).
{
    var warmWorld = scentWorld.Clone();
    var warmAnts = new AntPopulation();
    var warmSettings = AntSettings.Default with { InitialCount = 2000 };
    AntSpawner.SpawnInitial(warmAnts, warmWorld, warmSettings);
    var warmSystem = new AntSystem();
    var warmClock = Stopwatch.StartNew();
    while (warmClock.Elapsed.TotalSeconds < 1) warmSystem.Tick(warmAnts, warmWorld, warmSettings, 0.1f);
}
foreach (int antCount in new[] { 100, 1000, 5000, 10000, 20000 })
{
    var antWorld = scentWorld.Clone();
    var population = new AntPopulation();
    var antSettings = AntSettings.Default with { InitialCount = antCount, Seed = 7 };
    var report = AntSpawner.SpawnInitial(population, antWorld, antSettings);
    var antSystem = new AntSystem();
    for (int t = 0; t < 50; t++) antSystem.Tick(population, antWorld, antSettings, 0.1f); // 지상으로 퍼지게
    Measure($"개미 1틱 ×{report.Spawned}", () => antSystem.Tick(population, antWorld, antSettings, 0.1f), report.Spawned);
}

Console.WriteLine();
Console.WriteLine($"(sink {sink & 1})");

// 측정 입력의 실제 구성(반복 횟수가 아니라 고유 좌표 기준)을 출력한다. 이 수치는 이 입력의 조건일 뿐,
// 모든 월드 구성을 대표하지 않는다(대부분 고체인 개미집, 희소한 발생원, 유충 발생원 없음).
static void PrintWorkload(World world)
{
    int tiles = world.Config.TileCount;
    Console.WriteLine($"  [냄새 측정 입력] 고유 통로 {world.Passages.Count}개");
    foreach (var layer in world.Layers)
    {
        int open = 0, food = 0, larva = 0;
        for (int y = 0; y < world.Config.Height; y++)
        for (int x = 0; x < world.Config.Width; x++)
        {
            var state = layer.GetState(x, y);
            if (!state.HasSolid) open++;
            if (state.Food > 0) food++;
            if (state.Larva > 0) larva++;
        }
        Console.WriteLine($"  [냄새 측정 입력] {layer.Id}: 열린 칸 {open}/{tiles} ({100.0 * open / tiles:0.####}%), 먹이 발생원 {food}칸, 유충 발생원 {larva}칸");
    }
}

double Measure(string name, Action action, long elements)
{
    action(); // 워밍업(JIT)
    var samples = new double[iterations];
    for (int i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        samples[i] = sw.Elapsed.TotalMilliseconds;
    }
    Array.Sort(samples);
    double median = samples[iterations / 2];
    double nsPerElement = median * 1_000_000 / elements;
    Console.WriteLine($"| {name,-42} | {median,10:F3} ms | {nsPerElement,8:F2} ns/요소 |");
    return median;
}
