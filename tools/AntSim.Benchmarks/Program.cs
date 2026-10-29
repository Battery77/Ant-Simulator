using System.Diagnostics;
using AntSim.Core.Content;
using AntSim.Core.Worlds;

// 기준 성능 측정 도구. 최적화 전후 비교용이며 결과는 docs/BENCHMARKS.md에 기록한다.
// 실행: dotnet run -c Release --project tools/AntSim.Benchmarks [-- <size> <iterations>]

int size = args.Length > 0 ? int.Parse(args[0]) : 1024;
int iterations = args.Length > 1 ? int.Parse(args[1]) : 7;

var tiles = new TypeRegistry("tile");
tiles.Register("bench_a", "A");
tiles.Register("bench_b", "B");
var blocks = TypeRegistry.CreateWithEmpty("block");
blocks.Register("bench_block", "Block");
var config = new WorldConfig(size, size);

Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical cores, {size}x{size} tiles, chunk {config.ChunkSize}, {iterations} runs (median)");
Console.WriteLine();

World world = null!;
Measure("World 생성 (2 레이어)", () => world = new World(config, tiles, blocks, 0), config.TileCount * 2);

Measure("SetTile 전체 순회 (값 변경)", () =>
{
    var layer = world.Surface;
    ushort v = (ushort)(layer.GetTile(0, 0) ^ 1);
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
        layer.SetTile(x, y, v);
}, config.TileCount);

long sink = 0;
Measure("GetTile 전체 순회 (행 우선)", () =>
{
    var layer = world.Surface;
    long sum = 0;
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
        sum += layer.GetTile(x, y);
    sink += sum;
}, config.TileCount);

var rng = new Random(12345);
var xs = new int[1 << 20];
var ys = new int[1 << 20];
for (int i = 0; i < xs.Length; i++) { xs[i] = rng.Next(size); ys[i] = rng.Next(size); }
Measure("GetTile 무작위 접근", () =>
{
    var layer = world.Surface;
    long sum = 0;
    for (int i = 0; i < xs.Length; i++) sum += layer.GetTile(xs[i], ys[i]);
    sink += sum;
}, xs.Length);

Measure("Chunk.TileRow 스캔 (렌더러 업로드 경로)", () =>
{
    long sum = 0;
    foreach (var chunk in world.Surface.Chunks)
    for (int ly = 0; ly < chunk.ValidHeight; ly++)
        foreach (var t in chunk.TileRow(ly)) sum += t;
    sink += sum;
}, config.TileCount);

var dirty = new List<Chunk>();
Measure("CollectDirtyChunks", () =>
{
    world.Surface.SetTile(size / 2, size / 2, (ushort)(world.Surface.GetTile(size / 2, size / 2) ^ 1));
    dirty.Clear();
    world.Surface.CollectDirtyChunks(dirty);
}, config.ChunkCount);

Measure("ComputeContentHash (2 레이어)", () => sink += (long)world.ComputeContentHash(), config.TileCount * 2);

Console.WriteLine();
Console.WriteLine($"(sink {sink & 1})");

void Measure(string name, Action action, long elements)
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
    Console.WriteLine($"| {name,-38} | {median,10:F3} ms | {nsPerElement,8:F2} ns/요소 |");
}
