using AntSim.Core.Editing;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

/// <summary>
/// 헤드리스 통합 점검: <c>godot --headless --path godot -- --smoke-test</c>
/// 실제 노드 구성(MapView 텍스처 갱신, 패널, 저장/불러오기 경로)을 거쳐 편집 → Undo/Redo → 저장 → 불러오기를 수행하고
/// 결과를 종료 코드로 알린다(0 = 성공).
/// </summary>
public partial class Main
{
    private static bool IsSmokeTest => OS.GetCmdlineUserArgs().Contains("--smoke-test");

    /// <summary>표시 확인용 예시 지형을 칠하고(하나의 Undo 단계로 남긴다) 화면을 PNG로 저장한다.</summary>
    private async Task CaptureScreenshot(string path)
    {
        SetLayer(LayerId.Surface);
        _editor.BeginStroke();
        for (int y = 20; y < 60; y++)
        for (int x = 20; x < 70; x++)
        {
            int dx = x - 45, dy = y - 40;
            if (dx * dx / 4 + dy * dy < 220) _editor.Apply(EditOp.SetTerrain(LayerId.Surface, x, y, Terrain.Water));
        }
        for (int i = 0; i < 30; i++) _editor.Apply(EditOp.PlaceSolid(LayerId.Surface, 75 + i % 6, 25 + i / 6 * 2, i % 3 == 0 ? SolidBlock.Rock : SolidBlock.Soil));
        for (int i = 0; i <= 10; i++) _editor.Apply(EditOp.SetQuantity(LayerId.Surface, 20 + i * 2, 70, QuantityKind.Food, i));
        for (int i = 0; i <= 10; i++) _editor.Apply(EditOp.SetQuantity(LayerId.Surface, 20 + i * 2, 74, QuantityKind.Larva, i));
        for (int i = 0; i < 5; i++) _editor.Apply(EditOp.AddPassage(90 + i * 3, 60));
        _editor.EndStroke();

        _camera.FitTo(new Rect2(0, 0, 110, 85));
        _camera.Position -= new Vector2(10, 0); // 왼쪽 패널만큼 이동
        _pinnedTile = new Vector2I(30, 70);
        for (int i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(error == Error.Ok ? $"screenshot: {path}" : $"screenshot failed: {error}");
    }

    /// <summary>실제 입력 이벤트(왼쪽 버튼 누름 → 이동 → 해제)로 드래그 편집 경로를 점검한다. 디스플레이가 있을 때만.</summary>
    private async Task CheckMouseInput(Action<bool, string> check)
    {
        SetLayer(LayerId.Nest);
        _panel.EmulateToolSelection(new EditorTool(EditorToolKind.AddQuantity, Quantity: QuantityKind.Food));
        int undoBefore = _editor.UndoCount;
        Vector2 Screen(int x, int y) => _view.GetGlobalTransformWithCanvas() * new Vector2(x + 0.5f, y + 0.5f);

        async Task Send(InputEvent e, Vector2 at)
        {
            if (e is InputEventMouse mouse) mouse.Position = mouse.GlobalPosition = at;
            Input.ParseInputEvent(e);
            Input.FlushBufferedEvents();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        await Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, Screen(40, 80));
        await Send(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left }, Screen(50, 80));
        await Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }, Screen(50, 80));

        int filled = 0;
        for (int x = 40; x <= 50; x++) filled += _world.Nest.GetQuantity(QuantityKind.Food, x, 80);
        check(filled == 11, $"마우스 드래그로 먹이 11칸 (실제 {filled})");
        check(_editor.UndoCount == undoBefore + 1, "마우스 스트로크는 Undo 1단계");
        _panel.EmulateToolSelection(new EditorTool(EditorToolKind.Inspect));
    }

    private async void RunSmokeTest()
    {
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(_world.Config.Width == 1024 && _world.Config.Height == 1024, "기본 맵 크기");
        ulong empty = _world.ComputeContentHash();

        var water = new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Water);
        var stroke = EditorStroke.Begin(_editor, water, LayerId.Surface, 10, 10);
        stroke.MoveTo(200, 10);
        stroke.End();
        Check(stroke.AppliedCount == 191, $"드래그 칸 수 {stroke.AppliedCount}");

        SetLayer(LayerId.Nest);
        Check(_editor.Apply(EditOp.PlaceSolid(LayerId.Nest, 5, 5, SolidBlock.Rock)).Changed, "바위 배치");
        Check(_editor.Apply(EditOp.AdjustQuantity(LayerId.Nest, 5, 5, QuantityKind.Food, 1)).Error == WorldError.SolidOccupied, "고체 위 먹이 거부");
        Check(_editor.Apply(EditOp.SetQuantity(LayerId.Nest, 6, 5, QuantityKind.Larva, 10)).Changed, "유충 10");
        Check(_editor.Apply(EditOp.AddPassage(7, 7)).Changed, "통로 생성");

        // 렌더 갱신 경로(더티 청크 → 텍스처 업데이트)를 실제로 돌린다.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(_world.Nest.CollectDirtyChunks(new List<Chunk>()) == 0, "MapView가 더티 청크를 소비함");

        string? screenshot = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot="))?["--screenshot=".Length..];
        if (screenshot is not null) await CaptureScreenshot(screenshot);

        if (DisplayServer.GetName() != "headless") await CheckMouseInput(Check);
        ulong edited = _world.ComputeContentHash();

        while (_editor.Undo()) { }
        Check(_world.ComputeContentHash() == empty, $"전체 Undo 후 빈 맵 (남은 Undo {_editor.UndoCount})");
        while (_editor.Redo()) { }
        Check(_world.ComputeContentHash() == edited, "전체 Redo 후 편집 상태");

        string path = ProjectSettings.GlobalizePath("user://smoke-test.antmap");
        SaveTo(path);
        NewMap(64, 64);
        Check(_world.Config.Width == 64, "새 맵");
        LoadFrom(path);
        Check(_world.ComputeContentHash() == edited, "저장 → 불러오기 해시 일치");
        Check(!_editor.CanUndo, "불러온 맵은 편집 기록 없음");
        System.IO.File.Delete(path);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (failures.Count == 0)
        {
            GD.Print("SMOKE TEST PASSED");
            GetTree().Quit(0);
        }
        else
        {
            foreach (var failure in failures) GD.PrintErr($"SMOKE TEST FAILED: {failure}");
            GetTree().Quit(1);
        }
    }
}
