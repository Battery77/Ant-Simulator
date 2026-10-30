using AntSim.Core.Editing;
using AntSim.Core.Rendering;
using AntSim.Core.Serialization;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

/// <summary>
/// 헤드리스 통합 점검: <c>godot --headless --path godot -- --smoke-test</c>
/// 실제 노드 구성으로 편집(복합 규칙 포함) → Undo/Redo → 맵 저장/불러오기 → 시뮬레이션 모드(단일 틱, 재생/일시정지,
/// 히트맵, 실험 저장/초기화/불러오기) → 편집 모드 복귀를 수행하고 결과를 종료 코드로 알린다(0 = 성공).
/// <c>--screenshot=경로</c>를 주면 편집 화면과 시뮬레이션 화면(경로-sim.png)을 저장한다(디스플레이 필요).
/// </summary>
public partial class Main
{
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(20);

    private static bool IsSmokeTest => OS.GetCmdlineUserArgs().Contains("--smoke-test");

    private static string? ScreenshotPath =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot="))?["--screenshot=".Length..];

    private bool HasDisplay => DisplayServer.GetName() != "headless";

    private async void RunSmokeTest()
    {
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        try
        {
            await SmokeEditMode(Check);
            await SmokeSimulationMode(Check);
        }
        catch (Exception e)
        {
            failures.Add($"예외: {e}");
        }

        await NextFrame();
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

    private async Task SmokeEditMode(Action<bool, string> check)
    {
        await NextFrame();
        check(_world.Config.Width == 1024 && _world.Config.Height == 1024, "기본 맵 크기");
        check(_world.Nest.GetSolid(0, 0) == SolidBlock.Soil && _world.Surface.GetSolid(0, 0) == SolidBlock.None, "개미집 프리셋");
        ulong initial = _world.ComputeContentHash();

        var water = new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Water);
        var stroke = EditorStroke.Begin(_editor, water, LayerId.Surface, 10, 10);
        stroke.MoveTo(200, 10);
        stroke.End();
        check(stroke.AppliedCount == 191, $"드래그 칸 수 {stroke.AppliedCount}");

        SetLayer(LayerId.Nest);
        check(_editor.Apply(EditOp.AddPassage(7, 7)).Changed, "통로 생성");
        check(_world.Nest.GetSolid(6, 6) == SolidBlock.None && _world.Nest.GetSolid(5, 5) == SolidBlock.Soil, "개미집 3×3 굴착");
        check(_editor.Apply(EditOp.AddPassage(0, 7)).Error == WorldError.PassageAreaOutOfBounds, "가장자리 통로 거부");
        check(_editor.Apply(EditOp.PlaceSolid(LayerId.Nest, 5, 5, SolidBlock.Rock)).Changed, "토양 → 바위 교체");
        check(_editor.Apply(EditOp.AdjustQuantity(LayerId.Nest, 5, 5, QuantityKind.Food, 1)).Error == WorldError.SolidOccupied, "고체 위 먹이 거부");
        check(_editor.Apply(EditOp.SetQuantity(LayerId.Nest, 6, 6, QuantityKind.Larva, 10)).Changed, "굴착 공간에 유충 10");
        check(_editor.Apply(EditOp.SetQuantity(LayerId.Surface, 40, 40, QuantityKind.Food, 10)).Changed, "지상 먹이 10");
        check(_editor.Apply(EditOp.PlaceSolid(LayerId.Surface, 41, 41, SolidBlock.Soil)).Changed, "지상 토양");
        check(_editor.Apply(EditOp.AddPassage(41, 41)).Error == WorldError.PassageBlockedBySolid, "고체 위 통로 거부");

        await NextFrame();
        await NextFrame();
        check(_world.Nest.CollectDirtyChunks(new List<Chunk>()) == 0, "MapView가 더티 청크를 소비함");

        if (ScreenshotPath is { } screenshot && HasDisplay) await CaptureEditScreenshot(screenshot);
        if (HasDisplay) await CheckMouseInput(check);
        ulong edited = _world.ComputeContentHash();

        while (_editor.Undo()) { }
        check(_world.ComputeContentHash() == initial, $"전체 Undo 후 초기 맵 (남은 Undo {_editor.UndoCount})");
        while (_editor.Redo()) { }
        check(_world.ComputeContentHash() == edited, "전체 Redo 후 편집 상태");

        string path = ProjectSettings.GlobalizePath("user://smoke-test.antmap");
        SaveTo(path);
        NewMap(64, 64);
        check(_world.Config.Width == 64, "새 맵");
        LoadFrom(path);
        check(_world.ComputeContentHash() == edited, "맵 저장 → 불러오기 해시 일치");
        check(!_editor.CanUndo, "불러온 맵은 편집 기록 없음");
        System.IO.File.Delete(path);
    }

    private async Task SmokeSimulationMode(Action<bool, string> check)
    {
        ulong editorWorld = _world.ComputeContentHash();
        SetLayer(LayerId.Surface);
        SetMode(AppMode.Simulation);
        check(_mode == AppMode.Simulation && _host is not null && _simPanel.Visible && !_panel.Visible, "시뮬레이션 모드 진입");
        var host = _host!;

        // 시뮬레이션 모드에서는 클릭해도 편집되지 않는다.
        await ClickTile(30, 30, MouseButton.Left);
        check(_world.ComputeContentHash() == editorWorld && _simWorld!.ComputeContentHash() == editorWorld, "시뮬레이션 모드에서 편집 불가");

        await StepOnce();
        await StepOnce();
        await StepOnce();
        check(host.Tick == 3, $"단일 틱 3회 → 틱 {host.Tick}");

        host.Speed = 50;
        host.Play();
        await WaitUntil(() => host.Tick >= 40, "재생으로 틱 진행");
        host.Pause();
        long paused = await host.RunExclusiveAsync(s => s.Tick);
        await Wait(0.2);
        check(await host.RunExclusiveAsync(s => s.Tick) == paused, "일시정지 후 틱 멈춤");

        await WaitUntil(() => _snapshot is { } s && s.Tick == paused, "히트맵 스냅샷 수신");
        await NextFrame();
        check(_heatmap.Visible, "히트맵 표시(음식, 지상)");
        check(_snapshot!.Data[_simWorld!.Config.ToLinearIndex(40, 40)] > 0, "먹이 칸에 음식 냄새");
        check(_world.ComputeContentHash() == editorWorld, "시뮬레이션이 원본 맵을 바꾸지 않음");

        if (ScreenshotPath is { } screenshot && HasDisplay) await CaptureSimulationScreenshot(screenshot);
        await SmokeCompositeHeatmap(check);

        string path = ProjectSettings.GlobalizePath("user://smoke-test.antexp");
        await SaveExperiment(path);
        var (savedTick, savedHash) = await host.RunExclusiveAsync(s => (s.Tick, s.ComputeStateHash()));

        await ResetSimulation();
        check(host.Tick == 0, "초기화 → 틱 0");
        check(await host.RunExclusiveAsync(s => s.Scent.Total(0)) == 0, "초기화 → 냄새 0");

        LoadExperiment(path);
        check(_host is not null && _host != host, "실험 불러오기로 새 세션");
        check(_host!.Tick == savedTick, $"불러온 틱 {_host.Tick} = {savedTick}");
        check(await _host.RunExclusiveAsync(s => s.ComputeStateHash()) == savedHash, "실험 저장 → 불러오기 상태 해시 일치");
        System.IO.File.Delete(path);

        var badSettings = _scentSettings.WithTick(1.0);
        await ApplySettings(badSettings);
        check(_scentSettings.TickSeconds == 0.1, "불안정 틱 설정 거부");
        await ApplySettings(_scentSettings.WithTick(0.05));
        check(_scentSettings.TickSeconds == 0.05, "안정 틱 설정 적용");

        await SmokeAuditFixes(check);

        // F02: 저장을 요청한 직후 편집 모드로 나가도(호스트 종료) 저장 요청은 제한 시간 안에 끝나야 한다.
        string exitSavePath = ProjectSettings.GlobalizePath("user://smoke-exit.antexp");
        var pendingSave = SaveExperiment(exitSavePath);
        SetMode(AppMode.Edit);
        var finished = await Task.WhenAny(pendingSave, Task.Delay(TimeSpan.FromSeconds(10)));
        check(finished == pendingSave, "저장 직후 모드 전환: 저장 요청이 끝남(무기한 대기 없음)");
        System.IO.File.Delete(exitSavePath);
        check(_mode == AppMode.Edit && _host is null && _panel.Visible && !_heatmap.Visible, "편집 모드 복귀");
        check(_world.ComputeContentHash() == editorWorld, "편집 모드 복귀 후 원본 그대로");
    }

    /// <summary>
    /// 감사 수정 점검. F05: 시작·현재 월드가 다른 실험을 불러와 초기화하면 화면 월드가 계산 월드와 같아야 한다.
    /// F01: 손상 파일을 불러오면 오류만 표시하고 기존 세션은 그대로 동작해야 한다.
    /// </summary>
    private async Task SmokeAuditFixes(Action<bool, string> check)
    {
        // 현재 월드만 바꾼 실험 파일을 정상 저장 경로로 만든다.
        string path = ProjectSettings.GlobalizePath("user://smoke-diverged.antexp");
        await _host!.RunExclusiveAsync(s =>
        {
            var editor = new MapEditor(s.World);
            editor.Apply(EditOp.SetTerrain(LayerId.Surface, 3, 3, Terrain.Water));
            editor.Apply(EditOp.PlaceSolid(LayerId.Surface, 4, 3, SolidBlock.Rock));
            editor.Apply(EditOp.SetQuantity(LayerId.Surface, 12, 12, QuantityKind.Food, 9));
            ExperimentSerializer.Save(s, path);
            return true;
        });
        LoadExperiment(path);
        var host = _host!;
        var (initial, current) = await host.RunExclusiveAsync(s => (s.InitialWorld.ComputeContentHash(), s.World.ComputeContentHash()));
        check(initial != current, "시험 파일: 시작 월드 ≠ 현재 월드");
        check(_simWorld!.ComputeContentHash() == current, "불러온 직후 화면 월드 = 현재 월드");

        await ResetSimulation();
        ulong computed = await host.RunExclusiveAsync(s => s.World.ComputeContentHash());
        check(computed == initial, "초기화 후 계산 월드 = 시작 월드");
        check(_simWorld!.ComputeContentHash() == computed, "초기화 후 화면 월드(_simWorld) = 계산 월드");
        check(ReferenceEquals(_view.DisplayedWorld, _simWorld), "초기화 후 MapView가 새 화면 월드를 그림");
        check(EditorText.DescribeTile(_simWorld!, LayerId.Surface, 3, 3).Contains("지형: 흙"), "초기화 후 타일 정보가 시작 월드 기준");
        await StepOnce();
        float food = await host.RunExclusiveAsync(s => s.Scent.Get(0, LayerId.Surface, 12, 12));
        check(food == 0f, "초기화 후 다음 틱 발생원이 시작 월드 기준(현재 월드에만 있던 먹이 없음)");
        await WaitUntil(() => _snapshot is { } s && s.Tick == 1, "초기화 후 새 스냅샷");
        System.IO.File.Delete(path);

        // 손상 파일 불러오기: 오류 표시, 기존 세션 유지
        string broken = ProjectSettings.GlobalizePath("user://smoke-broken.antexp");
        System.IO.File.WriteAllBytes(broken, new byte[] { (byte)'A', (byte)'N', (byte)'T', (byte)'E', (byte)'X', (byte)'P', 0, 0, 1, 0, 0, 0 });
        LoadExperiment(broken);
        check(_host == host, "손상 파일 불러오기 실패 후 기존 세션 유지");
        await StepOnce();
        check(host.Tick == 2, "기존 세션이 계속 동작");
        System.IO.File.Delete(broken);
    }

    /// <summary>복합 히트맵: 단일 ↔ 복합 ↔ 숨김 전환, 레이어 전환, 전환해도 시뮬레이션 상태 불변.</summary>
    private async Task SmokeCompositeHeatmap(Action<bool, string> check)
    {
        var host = _host!;
        var (tickBefore, hashBefore) = await host.RunExclusiveAsync(s => (s.Tick, s.ComputeStateHash()));

        _simPanel.SelectHeatmap(HeatmapMode.Composite);
        check(_heatmapOptions.Mode == HeatmapMode.Composite, "복합 모드 선택");
        await WaitUntil(() => _snapshot is { } s && s.Types.Count == 2 && s.Layer == LayerId.Surface, "복합 스냅샷(음식+유충)");
        await NextFrame();
        check(_heatmap.Visible, "복합 히트맵 표시");
        check(_simPanel.LegendRowCount == 2, $"범례 2줄 (실제 {_simPanel.LegendRowCount})");

        SetLayer(LayerId.Nest);
        await WaitUntil(() => _snapshot is { } s && s.Types.Count == 2 && s.Layer == LayerId.Nest, "개미집 복합 스냅샷");
        await NextFrame();
        check(_heatmap.Visible, "개미집 레이어 복합 히트맵");
        check(_snapshot!.MaxOf(1) > 0, "개미집 유충 냄새 존재");
        SetLayer(LayerId.Surface);

        _simPanel.SelectHeatmap(HeatmapMode.Hidden);
        await WaitUntil(() => _snapshot is { } s && s.Types.Count == 0, "숨김 스냅샷(필드 없음)");
        await NextFrame();
        check(!_heatmap.Visible, "히트맵 숨김");

        _simPanel.SelectHeatmap(HeatmapMode.Single, 1);
        await WaitUntil(() => _snapshot is { } s && s.Types.Count == 1 && s.TypeIndex == 1, "유충 단일 스냅샷");
        await NextFrame();
        check(_heatmap.Visible && _simPanel.LegendRowCount == 1, "단일(유충) 히트맵 복귀");

        var (tickAfter, hashAfter) = await host.RunExclusiveAsync(s => (s.Tick, s.ComputeStateHash()));
        check(tickAfter == tickBefore && hashAfter == hashBefore, "히트맵 모드 전환 후 시뮬레이션 상태 불변");

        if (ScreenshotPath is { } screenshot && HasDisplay) await CaptureCompositeScreenshot(screenshot);
        _simPanel.SelectHeatmap(HeatmapMode.Single, 0);
    }

    private async Task CaptureCompositeScreenshot(string path)
    {
        _simPanel.SelectHeatmap(HeatmapMode.Composite);
        _camera.FitTo(new Rect2(10, 55, 48, 30));
        _camera.Position -= new Vector2(6, 0);
        await WaitUntil(() => _snapshot is { } s && s.Types.Count == 2 && s.Layer == LayerId.Surface, "스크린샷용 복합 스냅샷");
        _pinnedTile = new Vector2I(30, 72); // 먹이 줄(y=70)과 유충 줄(y=74) 사이: 두 냄새가 겹침
        await SaveScreenshot(path.Replace(".png", "-composite.png"));
        _pinnedTile = null;
    }

    /// <summary>실제 입력 이벤트(왼쪽 버튼 누름 → 이동 → 해제)로 드래그 편집 경로를 점검한다. 디스플레이가 있을 때만.</summary>
    private async Task CheckMouseInput(Action<bool, string> check)
    {
        SetLayer(LayerId.Surface);
        _panel.EmulateToolSelection(new EditorTool(EditorToolKind.AddQuantity, Quantity: QuantityKind.Food));
        int undoBefore = _editor.UndoCount;

        await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, TileToScreen(40, 80));
        await SendMouse(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left }, TileToScreen(50, 80));
        await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }, TileToScreen(50, 80));

        int filled = 0;
        for (int x = 40; x <= 50; x++) filled += _world.Surface.GetQuantity(QuantityKind.Food, x, 80);
        check(filled == 11, $"마우스 드래그로 먹이 11칸 (실제 {filled})");
        check(_editor.UndoCount == undoBefore + 1, "마우스 스트로크는 Undo 1단계");
        _panel.EmulateToolSelection(new EditorTool(EditorToolKind.Inspect));
    }

    private Vector2 TileToScreen(int x, int y) => _view.GetGlobalTransformWithCanvas() * new Vector2(x + 0.5f, y + 0.5f);

    private async Task ClickTile(int x, int y, MouseButton button)
    {
        var at = TileToScreen(x, y);
        await SendMouse(new InputEventMouseButton { ButtonIndex = button, Pressed = true }, at);
        await SendMouse(new InputEventMouseButton { ButtonIndex = button, Pressed = false }, at);
    }

    private async Task SendMouse(InputEventMouse e, Vector2 at)
    {
        e.Position = e.GlobalPosition = at;
        Input.ParseInputEvent(e);
        Input.FlushBufferedEvents();
        await NextFrame();
    }

    /// <summary>표시 확인용 예시 지형을 칠하고(하나의 Undo 단계로 남긴다) 화면을 PNG로 저장한다.</summary>
    private async Task CaptureEditScreenshot(string path)
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
        for (int i = 0; i < 5; i++) _editor.Apply(EditOp.AddPassage(90 + i * 4, 60));
        _editor.Apply(EditOp.AddPassage(45, 40)); // 물 위 통로 → 중심만 흙
        _editor.EndStroke();

        _camera.FitTo(new Rect2(0, 0, 110, 85));
        _camera.Position -= new Vector2(10, 0);
        _pinnedTile = new Vector2I(30, 70);
        await SaveScreenshot(path);
        SetLayer(LayerId.Nest);
        await SaveScreenshot(path.Replace(".png", "-nest.png"));
        SetLayer(LayerId.Surface);
        _pinnedTile = null;
    }

    private async Task CaptureSimulationScreenshot(string path)
    {
        _host!.Speed = 50;
        await _host.RunExclusiveAsync(s =>
        {
            s.Step(600);
            return true;
        });
        _camera.FitTo(new Rect2(0, 0, 110, 85));
        _camera.Position -= new Vector2(10, 0);
        _heatmapOptions = _heatmapOptions with { Scale = HeatmapScale.Log };
        SelectHeatmapOnHost();
        await WaitUntil(() => _snapshot is { Tick: > 600 }, "스크린샷용 스냅샷");
        _pinnedTile = new Vector2I(40, 40);
        await SaveScreenshot(path.Replace(".png", "-sim.png"));
        _pinnedTile = null;
        _heatmapOptions = _heatmapOptions with { Scale = HeatmapScale.Linear };
        SelectHeatmapOnHost();
    }

    private async Task SaveScreenshot(string path)
    {
        for (int i = 0; i < 4; i++) await NextFrame();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print(error == Error.Ok ? $"screenshot: {path}" : $"screenshot failed: {error}");
    }

    private async Task NextFrame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task WaitUntil(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + SmokeTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"시간 초과: {what}");
            await NextFrame();
        }
    }
}
