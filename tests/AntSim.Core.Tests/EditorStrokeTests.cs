using AntSim.Core.Editing;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class EditorStrokeTests
{
    [Fact]
    public void Drag_FillsGapsBetweenPointerSamples()
    {
        var editor = new MapEditor(TestWorlds.Create(64, 64));
        var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Water), LayerId.Surface, 0, 0);
        stroke.MoveTo(20, 0);
        stroke.End();
        for (int x = 0; x <= 20; x++) Assert.Equal(Terrain.Water, editor.World.Surface.GetTerrain(x, 0));
        Assert.Equal(Terrain.Dirt, editor.World.Surface.GetTerrain(21, 0));
        Assert.Equal(1, editor.UndoCount);
    }

    [Fact]
    public void QuantityTool_AppliesOncePerTilePerStroke()
    {
        var editor = new MapEditor(TestWorlds.Create(64, 64));
        var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.AddQuantity, Quantity: QuantityKind.Food), LayerId.Nest, 5, 5);
        stroke.MoveTo(6, 5);
        stroke.MoveTo(5, 5); // 되돌아와도 다시 증가하지 않는다
        stroke.End();
        Assert.Equal(1, editor.World.Nest.GetQuantity(QuantityKind.Food, 5, 5));
        Assert.Equal(1, editor.World.Nest.GetQuantity(QuantityKind.Food, 6, 5));
    }

    [Fact]
    public void FailingTiles_AreSkippedAndReported()
    {
        var editor = new MapEditor(TestWorlds.Create(64, 64));
        editor.Apply(EditOp.PlaceSolid(LayerId.Surface, 2, 0, SolidBlock.Rock));
        var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.AddQuantity, Quantity: QuantityKind.Larva), LayerId.Surface, 0, 0);
        stroke.MoveTo(4, 0);
        stroke.End();

        Assert.Equal(4, stroke.AppliedCount);
        Assert.Equal(1, stroke.FailedCount);
        Assert.Equal(WorldError.SolidOccupied, stroke.LastError);
        Assert.Equal(SolidBlock.Rock, editor.World.Surface.GetSolid(2, 0));
    }

    [Fact]
    public void Stroke_LeavingMap_IgnoresOutOfBoundsTiles()
    {
        var editor = new MapEditor(TestWorlds.Create(10, 10));
        var stroke = EditorStroke.Begin(editor, new EditorTool(EditorToolKind.AddPassage), LayerId.Surface, 8, 5);
        stroke.MoveTo(15, 5);
        stroke.End();
        Assert.Equal(2, editor.World.Passages.Count);
        Assert.Equal(0, stroke.FailedCount);
    }

    [Fact]
    public void InspectTool_DoesNotEdit()
    {
        var editor = new MapEditor(TestWorlds.Create(10, 10));
        EditorStroke.Begin(editor, new EditorTool(EditorToolKind.Inspect), LayerId.Surface, 1, 1).End();
        Assert.Equal(0, editor.UndoCount);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 1)]
    [InlineData(0, 0, 5, 0, 6)]
    [InlineData(0, 0, 5, 5, 6)]
    [InlineData(5, 2, -3, 0, 9)]
    [InlineData(0, 0, 2, 7, 8)]
    public void GridLine_IsContinuousAndIncludesEndpoints(int x0, int y0, int x1, int y1, int expectedCount)
    {
        var points = new List<TileCoord>();
        GridLine.Rasterize(x0, y0, x1, y1, points);
        Assert.Equal(expectedCount, points.Count);
        Assert.Equal(new TileCoord(x0, y0), points[0]);
        Assert.Equal(new TileCoord(x1, y1), points[^1]);
        for (int i = 1; i < points.Count; i++)
        {
            Assert.True(Math.Abs(points[i].X - points[i - 1].X) <= 1);
            Assert.True(Math.Abs(points[i].Y - points[i - 1].Y) <= 1);
        }
    }
}
