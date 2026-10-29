using System.Diagnostics;
using AntSim.Core.Editing;
using AntSim.Core.Rendering;
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
    Console.WriteLine($"| {name,-42} | {median,10:F3} ms | {nsPerElement,8:F2} ns/요소 |");
}
