using AntSim.Core.Ants;
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
        await SmokeAnts(check);

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
        await SmokeSimulationLayerSwitch(check);
        await SmokeStageAResetIntegration(check);
        if (HasDisplay) await SmokeNumberFieldFocus(check);
        await SmokeExperimentSaveUx(check);

        // F02: 저장을 요청한 직후 편집 모드로 나가도(호스트 종료) 저장 요청은 제한 시간 안에 끝나야 한다.
        string exitSavePath = ProjectSettings.GlobalizePath("user://smoke-exit.antexp");
        var pendingSave = SaveExperiment(exitSavePath);
        SetMode(AppMode.Edit);
        var finished = await Task.WhenAny(pendingSave, Task.Delay(TimeSpan.FromSeconds(10)));
        check(finished == pendingSave, "저장 직후 모드 전환: 저장 요청이 끝남(무기한 대기 없음)");
        System.IO.File.Delete(exitSavePath);
        check(_mode == AppMode.Edit && _host is null && _panel.Visible && !_heatmap.Visible, "편집 모드 복귀");
        check(_world.ComputeContentHash() == editorWorld, "편집 모드 복귀 후 원본 그대로");
        await SmokeMapSaveUx(check);
    }

    /// <summary>개미(단계 A): 생성·표시, 설정 적용(초기화), 단일 틱, 재생/일시정지, 통로로 지상 이동, 선택.</summary>
    private async Task SmokeAnts(Action<bool, string> check)
    {
        var host = _host!;
        await WaitUntil(() => _snapshot is not null, "개미 프레임");
        check(_snapshot!.Ants.Count == AntSettings.Default.InitialCount, $"시작 시 개미 {AntSettings.Default.InitialCount}마리 (실제 {_snapshot.Ants.Count})");

        _simPanel.SetPendingAntCount(150);
        await ApplyAntSettings(_simPanel.PendingAntSettings with { Seed = 3 });
        await WaitUntil(() => _snapshot is { Tick: 0 } s && s.Ants.Count == 150, "개미 150마리로 초기화");
        check(host.Tick == 0, "개미 설정 적용 → 틱 0");
        check(_antSettings.InitialCount == 150, "개미 설정 저장");
        check(_simPanel.PendingAntSettings.Seed == 3, "패널에 적용된 시드 표시");

        SetLayer(LayerId.Nest);
        await NextFrame();
        await NextFrame();
        check(_antView.VisibleCount == 150, $"틱 0: 개미는 모두 개미집 굴착 공간에 생성 (표시 {_antView.VisibleCount})");
        if (HasDisplay)
        {
            // 데이터만이 아니라 실제 화면에 그려졌는지 픽셀로 확인한다(개미 중심 = 어두운 몸통, 굴착 공간 = 밝은 흙).
            // 컬링 결함은 카메라 구도에 따라 나타나므로 여러 구도에서 본다.
            // 통로가 여러 곳이면 무리 사이가 비므로 첫 개미를 중심으로 잡는다.
            var f = _snapshot!.Ants;
            float cx = f.X[0], cy = f.Y[0];
            foreach (var (w, h) in new[] { (24f, 16f), (28f, 16f), (56f, 32f) })
                await CheckAntsDrawn(check, new Rect2(cx - w / 2, cy - h / 2, w, h), $"{w}×{h} 구도");
        }
        var start = _snapshot!.Ants.X.AsSpan(0, 150).ToArray();

        for (int i = 0; i < 5; i++) await StepOnce();
        await WaitUntil(() => _snapshot is { Tick: 5 }, "단일 틱 5회");
        check(!_snapshot!.Ants.X.AsSpan(0, 150).ToArray().SequenceEqual(start), "단일 틱으로 개미가 움직임");

        host.Speed = 50;
        host.Play();
        await WaitUntil(() => host.Tick >= 30, "재생으로 30틱");
        host.Pause();
        long playedTo = await host.RunExclusiveAsync(s => s.Tick);
        await Wait(0.2);
        check(await host.RunExclusiveAsync(s => s.Tick) == playedTo, "일시정지 후 개미·틱 멈춤");
        // Debug 빌드에서 재생으로 수백 틱을 기다리면 오래 걸리므로 나머지는 직접 진행한다.
        long paused = await host.RunExclusiveAsync(s =>
        {
            s.Step(300 - (int)s.Tick);
            return s.Tick;
        });
        await WaitUntil(() => _snapshot is { } s && s.Tick == paused, "일시정지 스냅샷");
        var frame = _snapshot!.Ants;
        int onSurface = frame.CountOn(LayerId.Surface);
        check(onSurface > 0, $"통로로 지상에 올라온 개미 {onSurface}마리");
        bool allPassable = true;
        for (int i = 0; i < frame.Count; i++)
            allPassable &= AntMovement.IsPassable(_simWorld!.GetLayer((LayerId)frame.Layer[i]), frame.X[i], frame.Y[i]);
        check(allPassable, "모든 개미가 이동 가능한 타일에 있음");

        // 실제 클릭으로 지상의 개미 선택
        SetLayer(LayerId.Surface);
        int target = -1;
        for (int i = 0; i < frame.Count && target < 0; i++)
            if (frame.Layer[i] == (byte)LayerId.Surface) target = i;
        if (target >= 0)
        {
            _camera.FitTo(new Rect2(frame.X[target] - 15, frame.Y[target] - 10, 30, 20));
            await NextFrame();
            int id = frame.Id[target];
            if (HasDisplay)
            {
                // 실제 마우스 이벤트로 클릭(헤드리스 창은 입력 이벤트를 받지 못해 디스플레이 모드에서만)
                var at = _view.GetGlobalTransformWithCanvas() * new Vector2(frame.X[target], frame.Y[target]);
                await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, at);
                await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }, at);
                check(_selectedAntId == id, $"클릭으로 개미 #{id} 선택 (선택됨: {_selectedAntId})");
            }
            else
            {
                check(TrySelectAnt(new Vector2(frame.X[target] + 0.2f, frame.Y[target])) && _selectedAntId == id, $"선택 로직으로 개미 #{id} 선택");
            }
            check(DescribeSelectedAnt()?.Contains($"개미 #{id}") == true, "선택한 개미 정보 표시");
        }
        else
        {
            check(false, "선택 시험용 지상 개미를 찾지 못함");
        }

        if (ScreenshotPath is { } screenshot && HasDisplay) await CaptureAntScreenshots(screenshot);
        ClearAntSelection();
        _simPanel.SelectHeatmap(HeatmapMode.Single, 0);
    }

    /// <summary>
    /// 시뮬레이션 모드의 보는 레이어 전환(사용자 보고: 시뮬레이션 모드에서 레이어 전환 UI가 보이지 않음).
    /// 버튼이 보이고 클릭되며, 전환은 표시(지형·개미·히트맵·타일 정보)만 바꾸고 틱·개미·냄새·재생 상태는 바꾸지 않는다.
    /// 일시정지·재생·단일 틱 모두에서 확인한다.
    /// </summary>
    private async Task SmokeSimulationLayerSwitch(Action<bool, string> check)
    {
        var host = _host!;
        foreach (var layer in new[] { LayerId.Surface, LayerId.Nest })
        {
            var button = _simPanel.LayerButton(layer);
            check(button.IsVisibleInTree(), $"시뮬레이션 모드: '{EditorText.Name(layer)}' 레이어 버튼이 보임");
            if (HasDisplay)
            {
                var rect = button.GetGlobalRect();
                check(GetViewport().GetVisibleRect().Encloses(rect) && rect.Size.X > 20 && rect.Size.Y > 10,
                    $"'{EditorText.Name(layer)}' 버튼이 화면 안에 있음 ({rect})");
            }
        }

        // 두 레이어 모두에 개미가 있는 상태를 만든다.
        await ApplyAntSettings(_antSettings with { InitialCount = 200, Seed = 5 });
        await host.RunExclusiveAsync(s => { s.Step(300); return true; });
        _simPanel.SelectHeatmap(HeatmapMode.Single, 0);
        await WaitUntil(() => _snapshot is { Tick: 300 }, "레이어 전환 시험용 스냅샷");
        _pinnedTile = new Vector2I(7, 7); // 통로 중심(두 레이어 모두 열림)

        async Task Press(LayerId layer)
        {
            var button = _simPanel.LayerButton(layer);
            if (HasDisplay)
            {
                var at = button.GetGlobalRect().GetCenter();
                await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, at);
                await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }, at);
            }
            else
            {
                button.EmitSignal(BaseButton.SignalName.Pressed); // 헤드리스는 입력 이벤트가 전달되지 않으므로 버튼 신호로 같은 경로를 탄다
            }
            await NextFrame();
            await NextFrame();
        }

        async Task CheckShown(LayerId layer, string when)
        {
            string name = EditorText.Name(layer);
            check(_view.Layer == layer, $"[{when}] 보는 레이어 = {name}");
            check(_simPanel.LayerButton(layer).ButtonPressed && !_simPanel.LayerButton(Other(layer)).ButtonPressed, $"[{when}] {name} 버튼만 눌림");
            await WaitUntil(() => _snapshot is { } s && s.Layer == layer, $"[{when}] {name} 히트맵 스냅샷");
            await NextFrame();
            var frame = _snapshot!.Ants;
            check(_antView.VisibleCount == frame.CountOn(layer), $"[{when}] {name} 개미만 표시 ({_antView.VisibleCount} = {frame.CountOn(layer)})");
            check(_simPanel.InfoText.Contains($") {name}"), $"[{when}] 타일 정보가 {name} 기준");
        }

        static LayerId Other(LayerId layer) => layer == LayerId.Surface ? LayerId.Nest : LayerId.Surface;

        // 일시정지
        var before = await host.RunExclusiveAsync(s => (s.Tick, Hash: s.ComputeStateHash(), Layers: s.Ants.Layer.ToArray()));
        await Press(LayerId.Nest);
        await CheckShown(LayerId.Nest, "일시정지");
        await Press(LayerId.Surface);
        await CheckShown(LayerId.Surface, "일시정지");
        await Press(LayerId.Nest);
        var after = await host.RunExclusiveAsync(s => (s.Tick, Hash: s.ComputeStateHash(), Layers: s.Ants.Layer.ToArray()));
        check(after.Tick == before.Tick && after.Hash == before.Hash && after.Layers.SequenceEqual(before.Layers) && !host.IsPlaying,
            "레이어 전환은 틱·개미 레이어·냄새·재생 상태를 바꾸지 않음");
        check(_host == host, "레이어 전환이 호스트를 다시 만들지 않음");

        // 보는 레이어에 없는 개미는 선택되지 않는다(개미집을 보면서 지상 개미 위치 클릭).
        var f = _snapshot!.Ants;
        for (int i = 0; i < f.Count; i++)
        {
            if (f.Layer[i] != (byte)LayerId.Surface) continue;
            bool nestNearby = false;
            for (int j = 0; j < f.Count && !nestNearby; j++)
                nestNearby = f.Layer[j] == (byte)LayerId.Nest && MathF.Abs(f.X[j] - f.X[i]) < 1.5f && MathF.Abs(f.Y[j] - f.Y[i]) < 1.5f;
            if (nestNearby) continue;
            check(!TrySelectAnt(new Vector2(f.X[i], f.Y[i])), "개미집을 보는 중에는 지상 개미를 선택하지 않음");
            break;
        }

        // 재생 중
        host.Speed = 10;
        host.Play();
        await WaitUntil(() => host.Tick >= after.Tick + 5, "레이어 전환 시험: 재생");
        await Press(LayerId.Surface);
        check(host.IsPlaying, "재생 중 레이어 전환 후에도 재생 유지");
        await CheckShown(LayerId.Surface, "재생 중");
        long t0 = host.Tick;
        await WaitUntil(() => host.Tick >= t0 + 3, "전환 후 계속 진행");
        if (ScreenshotPath is { } shot && HasDisplay)
        {
            await SaveScreenshot(shot.Replace(".png", $"-layer-surface-playing.png"));
            await Press(LayerId.Nest);
            await SaveScreenshot(shot.Replace(".png", $"-layer-nest-playing-a.png"));
            await Wait(0.5);
            await SaveScreenshot(shot.Replace(".png", $"-layer-nest-playing-b.png"));
        }
        await Press(LayerId.Nest);
        await CheckShown(LayerId.Nest, "재생 중");
        host.Pause();

        // 단일 틱 전후
        long beforeStep = await host.RunExclusiveAsync(s => s.Tick);
        await StepOnce();
        await Press(LayerId.Surface);
        await CheckShown(LayerId.Surface, "단일 틱 후");
        check(await host.RunExclusiveAsync(s => s.Tick) == beforeStep + 1, "단일 틱은 정확히 1틱(레이어 전환은 틱을 진행하지 않음)");

        // 초기화·모드 전환 후에도 버튼 = 보는 레이어
        await Press(LayerId.Nest);
        await ResetSimulation();
        check(_view.Layer == LayerId.Nest && _simPanel.LayerButton(LayerId.Nest).ButtonPressed, "초기화 후에도 보는 레이어·버튼 유지");
        _pinnedTile = null;
        await Press(LayerId.Surface);
    }

    /// <summary>
    /// 단계 A 통합: 초기화·개미 설정 적용이 화면 월드·세대·선택·보간을 새 세대로 맞추고, 이전 호스트의 늦은 결과가 새 호스트 화면을 덮어쓰지 않는다.
    /// </summary>
    private async Task SmokeStageAResetIntegration(Action<bool, string> check)
    {
        var host = _host!;
        await WaitUntil(() => _snapshot is not null, "통합 시험 시작 스냅샷");

        // 개미를 선택한 뒤 초기화하면 선택이 풀리고(같은 ID의 다른 개미를 가리키지 않음), 화면 월드·세대가 새 결과로 바뀐다.
        var f = _snapshot!.Ants;
        int pick = f.Count > 0 ? 0 : -1;
        if (pick >= 0) SetLayer((LayerId)f.Layer[pick]);
        check(pick >= 0 && TrySelectAnt(new Vector2(f.X[pick], f.Y[pick])) && DescribeSelectedAnt() is not null, "초기화 전 개미 선택");
        long generation = _minResetCount;
        await host.RunExclusiveAsync(s => { new MapEditor(s.World).Apply(EditOp.SetTerrain(LayerId.Surface, 9, 9, Terrain.Water)); s.Step(20); return true; });
        await ResetSimulation();
        check(_selectedAntId == -1 && DescribeSelectedAnt() is null, "초기화 후 개미 선택 해제");
        check(_minResetCount == generation + 1, $"초기화 세대 증가 ({_minResetCount})");
        ulong computed = await host.RunExclusiveAsync(s => s.World.ComputeContentHash());
        check(_simWorld!.ComputeContentHash() == computed && ReferenceEquals(_view.DisplayedWorld, _simWorld), "초기화 후 화면 월드 = 계산 월드");
        await WaitUntil(() => _snapshot is { } s && s.ResetCount == _minResetCount, "새 세대 스냅샷");
        check(_snapshot!.Tick == 0 && _frameResetCount == _minResetCount, "보간 기준이 새 세대의 틱 0");

        // 개미 설정 적용(초기화 경로)도 같은 계약
        await host.RunExclusiveAsync(s => { new MapEditor(s.World).Apply(EditOp.SetTerrain(LayerId.Surface, 9, 9, Terrain.Water)); return true; });
        await ApplyAntSettings(_antSettings with { InitialCount = 77, Seed = 11 });
        computed = await host.RunExclusiveAsync(s => s.World.ComputeContentHash());
        check(_simWorld!.ComputeContentHash() == computed && _simWorld.Surface.GetState(9, 9).Terrain == Terrain.Dirt,
            "개미 설정 적용 후 화면 월드 = 계산 월드(시작 월드)");
        check(_minResetCount == generation + 2, "개미 설정 적용도 초기화 세대를 올림");
        await WaitUntil(() => _snapshot is { } s && s.ResetCount == _minResetCount && s.Ants.Count == 77, "개미 77마리 새 세대 프레임");

        // 이전 호스트의 늦은 초기화 결과가 새 호스트(불러온 실험)의 화면을 덮어쓰지 않는다.
        string path = ProjectSettings.GlobalizePath("user://smoke-stage-a.antexp");
        await host.RunExclusiveAsync(s =>
        {
            new MapEditor(s.World).Apply(EditOp.SetTerrain(LayerId.Surface, 9, 9, Terrain.Water));
            ExperimentSerializer.Save(s, path);
            return true;
        });
        var lateReset = ResetSimulation(); // 이전 호스트에 초기화 요청
        LoadExperiment(path);              // 곧바로 새 호스트로 교체
        var finished = await Task.WhenAny(lateReset, Task.Delay(TimeSpan.FromSeconds(10)));
        check(finished == lateReset, "이전 호스트의 초기화 요청이 끝남");
        await NextFrame();
        check(_host != host, "불러오기로 새 호스트");
        check(_simWorld!.Surface.GetState(9, 9).Terrain == Terrain.Water, "이전 호스트의 초기화 결과가 새 화면 월드를 덮어쓰지 않음");
        check(_minResetCount == 0, $"새 호스트의 세대 기준 유지 ({_minResetCount})");
        await WaitUntil(() => _snapshot is { } s && s.Ants.Count == 77, "불러온 실험의 개미 프레임");
        System.IO.File.Delete(path);
    }

    // ---------------- 입력 포커스·단축키·저장 UX ----------------

    /// <summary>반사로 패널의 숫자 입력란을 가져온다(이전 코드에서도 같은 시험을 돌릴 수 있도록).</summary>
    private static SpinBox PanelSpinBox(Control panel, string field) =>
        (SpinBox)panel.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;

    private async Task SendKey(Key key, bool ctrl = false, bool shift = false, char text = '\0')
    {
        var down = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift, Unicode = text };
        Input.ParseInputEvent(down);
        Input.FlushBufferedEvents();
        await NextFrame();
        var up = new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false, CtrlPressed = ctrl, ShiftPressed = shift };
        Input.ParseInputEvent(up);
        Input.FlushBufferedEvents();
        await NextFrame();
    }

    private async Task Type(string text)
    {
        foreach (char c in text)
        {
            var key = c switch
            {
                >= '0' and <= '9' => Key.Key0 + (c - '0'),
                >= 'a' and <= 'z' => Key.A + (c - 'a'),
                '.' => Key.Period,
                _ => Key.Space,
            };
            await SendKey(key, text: c);
        }
    }

    private async Task ClickAt(Vector2 at)
    {
        await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, at);
        await SendMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false }, at);
    }

    /// <summary>입력란을 실제로 클릭하고 글자를 모두 선택한다(이후 입력이 기존 값을 대체).</summary>
    private async Task FocusField(SpinBox box)
    {
        await ClickAt(box.GetLineEdit().GetGlobalRect().GetCenter());
        box.GetLineEdit().SelectAll();
        await NextFrame();
    }

    private Vector2 MapPoint => new(GetViewport().GetVisibleRect().Size.X * 0.75f, GetViewport().GetVisibleRect().Size.Y * 0.6f);

    /// <summary>
    /// 숫자 입력 확정·포커스 해제와 Tab / Ctrl+Tab(실제 키·마우스 이벤트, 디스플레이 모드).
    /// Enter·바깥 클릭·다른 입력란 클릭으로 확정, 바깥 클릭 후 키 입력이 이전 칸에 들어가지 않음, Escape 취소, 잘못된 값·범위 밖 값,
    /// Tab = 포커스 이동(레이어 그대로), 입력란에 포커스가 있어도 Ctrl+Tab = 레이어 전환, 적용 버튼이 마지막 입력값을 씀.
    /// </summary>
    private async Task SmokeNumberFieldFocus(Action<bool, string> check)
    {
        var count = PanelSpinBox(_simPanel, "_antInitialCount");
        var seed = PanelSpinBox(_simPanel, "_antSeed");
        var focusOwner = () => GetViewport().GuiGetFocusOwner();

        // 바깥(맵) 클릭으로 확정 + 포커스 해제, 이후 숫자 키가 이전 칸에 들어가지 않음
        await FocusField(count);
        check(focusOwner() == count.GetLineEdit(), "입력란 클릭 → 포커스");
        await Type("345");
        await ClickAt(MapPoint);
        check(count.Value == 345, $"맵 클릭으로 입력 확정 (값 {count.Value})");
        check(focusOwner() != count.GetLineEdit(), "맵 클릭 후 입력란 포커스 해제");
        await Type("9");
        check(count.Value == 345 && count.GetLineEdit().Text == "345", $"바깥 클릭 후 숫자 키가 이전 칸에 들어가지 않음 ('{count.GetLineEdit().Text}')");

        // Enter로 확정
        await FocusField(count);
        await Type("77");
        await SendKey(Key.Enter);
        check(count.Value == 77, $"Enter로 입력 확정 (값 {count.Value})");
        check(focusOwner() != count.GetLineEdit(), "Enter 후 포커스 해제");

        // Escape로 취소
        await FocusField(count);
        await Type("999");
        await SendKey(Key.Escape);
        check(count.Value == 77 && count.GetLineEdit().Text == "77", $"Escape로 입력 취소, 기존 값 복원 (값 {count.Value}, 글자 '{count.GetLineEdit().Text}')");
        check(focusOwner() != count.GetLineEdit(), "Escape 후 포커스 해제");

        // 잘못된 값·빈 문자열은 기존 값 유지, 범위 밖은 최대값으로
        await FocusField(count);
        await Type("abc");
        await ClickAt(MapPoint);
        check(count.Value == 77, $"잘못된 글자는 기존 값 유지 (값 {count.Value})");
        await FocusField(count);
        await SendKey(Key.Backspace);
        await ClickAt(MapPoint);
        check(count.Value == 77, $"빈 입력은 기존 값 유지 (값 {count.Value})");
        await FocusField(count);
        await Type("999999");
        await ClickAt(MapPoint);
        check(count.Value == count.MaxValue, $"범위 밖 값은 최대값으로 (값 {count.Value})");

        // 다른 입력란 클릭: 이전 칸 확정 + 새 칸으로 포커스 이동
        await FocusField(count);
        await Type("120");
        await ClickAt(seed.GetLineEdit().GetGlobalRect().GetCenter());
        check(count.Value == 120, $"다른 입력란 클릭으로 이전 입력 확정 (값 {count.Value})");
        check(focusOwner() == seed.GetLineEdit(), "다른 입력란으로 포커스 이동");

        // Tab = 포커스 이동(레이어 그대로), Ctrl+Tab = 레이어 전환(입력란 포커스 중에도)
        var layer = _view.Layer;
        await FocusField(count);
        await SendKey(Key.Tab);
        check(_view.Layer == layer, "Tab은 레이어를 바꾸지 않음");
        check(focusOwner() != count.GetLineEdit() && focusOwner() is LineEdit, $"Tab은 다음 입력란으로 포커스 이동 ({focusOwner()?.GetParent()?.Name})");
        await FocusField(count);
        await SendKey(Key.Tab, ctrl: true);
        check(_view.Layer != layer, "입력란 포커스 중 Ctrl+Tab으로 레이어 전환");
        check(_simPanel.LayerButton(_view.Layer).ButtonPressed, "Ctrl+Tab 후 레이어 버튼 상태 일치");
        await SendKey(Key.Tab, ctrl: true);
        check(_view.Layer == layer, "Ctrl+Tab 다시 → 원래 레이어");
        await ClickAt(MapPoint);

        // 적용 버튼: 입력란에서 바로 버튼을 눌러도 마지막 입력값으로 적용
        await FocusField(count);
        await Type("60");
        var apply = _simPanel.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text.StartsWith("개미 설정 적용"));
        await ClickAt(apply.GetGlobalRect().GetCenter());
        await WaitUntil(() => _snapshot is { } s && s.ResetCount == _minResetCount && s.Ants.Count == 60, "적용 버튼: 마지막 입력값 60마리");
        check(_antSettings.InitialCount == 60, $"적용 버튼이 마지막 입력값을 씀 ({_antSettings.InitialCount})");
        check(focusOwner() != count.GetLineEdit(), "적용 버튼 클릭 후 입력란 포커스 해제");

        // 입력란 확정만으로는 초기화하지 않는다.
        long resets = _minResetCount;
        await FocusField(count);
        await Type("61");
        await ClickAt(MapPoint);
        await Wait(0.2);
        check(_minResetCount == resets && _antSettings.InitialCount == 60, "입력 확정만으로는 설정 적용·초기화하지 않음");
    }

    /// <summary>
    /// 실험 저장 UX(시뮬레이션 모드): Ctrl+S(경로 없음 → 저장 대화상자, 취소 → 저장·성공 알림 없음), 대화상자로 새 파일 저장 → 성공 알림·빠른 저장 경로,
    /// Ctrl+S 빠른 저장, 기존 파일 덮어쓰기 확인(취소 → 파일 그대로, 확인 → 덮어씀), 대화상자가 열린 동안 단축키 무시, 저장 실패 알림,
    /// v1 파일을 불러온 뒤 Ctrl+S는 원본을 덮어쓰지 않음, 연속 저장.
    /// </summary>
    private async Task SmokeExperimentSaveUx(Action<bool, string> check)
    {
        string dir = ProjectSettings.GlobalizePath("user://save-ux");
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        string first = System.IO.Path.Combine(dir, "experiment.antexp");

        // 새 실험: 빠른 저장 경로 없음 → Ctrl+S가 저장 대화상자를 연다. 취소하면 아무것도 저장하지 않는다.
        SetMode(AppMode.Edit);
        SetMode(AppMode.Simulation);
        check(_experimentPath is null, "새 실험은 빠른 저장 경로 없음");
        int toasts = _toast.ShownCount;
        await PressCtrlS();
        var dialog = _modal as FileDialog;
        check(dialog is { FileMode: FileDialog.FileModeEnum.SaveFile, UseNativeDialog: false }, "Ctrl+S(경로 없음) → 실험 저장 대화상자");
        await PressCtrlS();
        check(_modal == dialog && GetChildren().OfType<FileDialog>().Count() == 1, "대화상자가 열린 동안 Ctrl+S는 대화상자를 더 열지 않음");
        var layer = _view.Layer;
        await PressCtrlTab();
        check(_view.Layer == layer, "대화상자가 열린 동안 Ctrl+Tab은 레이어를 바꾸지 않음");
        dialog!.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
        await NextFrame();
        await Wait(0.3);
        check(_modal is null && !System.IO.File.Exists(first) && _toast.ShownCount == toasts, "저장 취소: 파일·성공 알림 없음");

        // 대화상자로 새 파일 저장(확장자 없이 입력 → .antexp)
        await ChooseInSaveDialog(ShowExperimentSaveDialog, System.IO.Path.Combine(dir, "experiment"));
        await WaitUntil(() => _toast.ShownCount > toasts, "실험 저장 알림");
        check(System.IO.File.Exists(first), "대화상자로 새 파일 저장(확장자 자동)");
        check(_toast.CurrentText == "실험을 저장했습니다. — experiment.antexp" && !_toast.IsError, $"실험 저장 성공 알림 ('{_toast.CurrentText}')");
        check(_experimentPath == first, "저장한 파일이 빠른 저장 대상");
        if (ScreenshotPath is { } shot && HasDisplay) await SaveScreenshot(shot.Replace(".png", "-toast-experiment.png"));
        var loadedBack = ExperimentSerializer.Load(first);
        check(loadedBack.Tick == _host!.Tick, "저장한 실험 파일을 다시 불러올 수 있음");

        // Ctrl+S 빠른 저장(대화상자 없이 같은 파일)
        await StepOnce();
        toasts = _toast.ShownCount;
        await PressCtrlS();
        check(_modal is null, "Ctrl+S(경로 있음)는 대화상자를 열지 않음");
        await WaitUntil(() => _toast.ShownCount > toasts, "빠른 저장 알림");
        check(ExperimentSerializer.Load(first).Tick == _host!.Tick, "Ctrl+S 빠른 저장이 같은 파일에 최신 상태를 씀");

        // 연속 저장: 알림이 쌓이지 않고 마지막 결과를 보여 준다
        toasts = _toast.ShownCount;
        var a = SaveExperiment(first);
        var b = SaveExperiment(first);
        await Task.WhenAll(a, b);
        check(_toast.ShownCount == toasts + 2 && !_toast.IsError && _toast.CurrentText!.Contains("experiment.antexp"), "연속 저장: 각 결과 한 번씩, 마지막 알림은 성공");
        check(GetTree().Root.FindChildren("Toast", "", true, false).Count == 1, "알림 노드는 하나(겹쳐 쌓이지 않음)");

        // 기존 파일 덮어쓰기: 저장 대화상자에서 기존 파일 선택 → 확인 창. 취소 → 파일 그대로, 확인 → 덮어씀
        await StepOnce();
        byte[] before = System.IO.File.ReadAllBytes(first);
        toasts = _toast.ShownCount;
        var confirm = await ChooseInSaveDialog(ShowExperimentSaveDialog, first, expectConfirm: true);
        check(confirm is not null, "기존 파일 선택 → 덮어쓰기 확인 창");
        if (confirm is not null)
        {
            if (ScreenshotPath is { } confirmShot && HasDisplay) await SaveScreenshot(confirmShot.Replace(".png", "-overwrite-confirm.png"));
            confirm.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(0.3);
            check(System.IO.File.ReadAllBytes(first).SequenceEqual(before) && _toast.ShownCount == toasts, "덮어쓰기 취소: 기존 파일 그대로, 성공 알림 없음");
            (_modal as FileDialog)?.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
            await NextFrame();
            confirm = await ChooseInSaveDialog(ShowExperimentSaveDialog, first, expectConfirm: true);
            confirm?.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await WaitUntil(() => _toast.ShownCount > toasts, "덮어쓰기 확인 후 저장 알림");
            check(!System.IO.File.ReadAllBytes(first).SequenceEqual(before) && ExperimentSerializer.Load(first).Tick == _host!.Tick,
                "덮어쓰기 확인: 최신 상태로 덮어씀");
        }
        check(_modal is null, "대화상자 정리");

        // 저장 실패 알림
        toasts = _toast.ShownCount;
        await SaveExperiment(System.IO.Path.Combine(dir, "no-such-dir", "x.antexp"));
        check(_toast.ShownCount == toasts + 1 && _toast.IsError && _toast.CurrentText!.StartsWith("실험 저장 실패"), $"저장 실패 알림 ('{_toast.CurrentText}')");
        if (ScreenshotPath is { } failShot && HasDisplay) await SaveScreenshot(failShot.Replace(".png", "-toast-error.png"));

        // v1 실험 파일을 불러오면 Ctrl+S가 원본을 덮어쓰지 않는다(저장 대화상자).
        string fixture = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "tests", "AntSim.Core.Tests", "Fixtures", "experiment-v1.antexp"));
        string v1 = System.IO.Path.Combine(dir, "old-v1.antexp");
        System.IO.File.Copy(fixture, v1);
        byte[] v1Bytes = System.IO.File.ReadAllBytes(v1);
        LoadExperiment(v1);
        check(_experimentPath is null, "v1 파일은 빠른 저장 대상이 아님");
        await PressCtrlS();
        check(_modal is FileDialog, "v1 불러온 뒤 Ctrl+S → 저장 대화상자(원본 덮어쓰기 안 함)");
        (_modal as FileDialog)?.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
        await NextFrame();
        check(System.IO.File.ReadAllBytes(v1).SequenceEqual(v1Bytes), "v1 원본 파일 그대로");

        // v2 파일을 불러오면 그 파일이 빠른 저장 대상
        LoadExperiment(first);
        check(_experimentPath == first, "v2 파일은 불러온 뒤 빠른 저장 대상");
        System.IO.Directory.Delete(dir, true);
    }

    /// <summary>맵 저장 UX(편집 모드): Ctrl+S(경로 있음) 빠른 저장 + 성공 알림, 저장 실패 알림, 대화상자 덮어쓰기 확인.</summary>
    private async Task SmokeMapSaveUx(Action<bool, string> check)
    {
        string dir = ProjectSettings.GlobalizePath("user://map-save-ux");
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "colony.antmap");

        int toasts = _toast.ShownCount;
        await ChooseInSaveDialog(ShowSaveDialog, System.IO.Path.Combine(dir, "colony"));
        check(System.IO.File.Exists(path) && _currentPath == path, "맵: 대화상자로 새 파일 저장(확장자 자동)");
        check(_toast.ShownCount == toasts + 1 && _toast.CurrentText == "맵을 저장했습니다. — colony.antmap" && !_toast.IsError,
            $"맵 저장 성공 알림 ('{_toast.CurrentText}')");
        if (ScreenshotPath is { } shot && HasDisplay) await SaveScreenshot(shot.Replace(".png", "-toast-map.png"));
        await Wait(Toast.SuccessSeconds + 0.6);
        check(_toast.CurrentText is null, "성공 알림은 잠시 뒤 자동으로 사라짐");

        _editor.Apply(EditOp.SetTerrain(LayerId.Surface, 3, 3, Terrain.Water));
        toasts = _toast.ShownCount;
        await PressCtrlS();
        check(_modal is null && _toast.ShownCount == toasts + 1, "맵: Ctrl+S(경로 있음) 빠른 저장, 대화상자 없음");
        check(MapSerializer.Load(path).ComputeContentHash() == _world.ComputeContentHash(), "맵: 빠른 저장이 최신 편집을 씀");

        byte[] before = System.IO.File.ReadAllBytes(path);
        _editor.Apply(EditOp.SetTerrain(LayerId.Surface, 4, 4, Terrain.Water));
        var confirm = await ChooseInSaveDialog(ShowSaveDialog, path, expectConfirm: true);
        check(confirm is not null, "맵: 기존 파일 선택 → 덮어쓰기 확인 창");
        confirm?.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
        await NextFrame();
        check(System.IO.File.ReadAllBytes(path).SequenceEqual(before), "맵: 덮어쓰기 취소 → 기존 파일 그대로");
        (_modal as FileDialog)?.GetCancelButton().EmitSignal(BaseButton.SignalName.Pressed);
        await NextFrame();

        SaveTo(System.IO.Path.Combine(dir, "no-such-dir", "x.antmap"));
        check(_toast.IsError && _toast.CurrentText!.StartsWith("맵 저장 실패"), $"맵 저장 실패 알림 ('{_toast.CurrentText}')");
        check(_currentPath == path, "실패한 저장은 빠른 저장 경로를 바꾸지 않음");
        System.IO.Directory.Delete(dir, true);
    }

    private async Task PressCtrlS()
    {
        if (HasDisplay) await SendKey(Key.S, ctrl: true, text: 's');
        else
        {
            // 헤드리스는 입력 이벤트가 노드에 전달되지 않으므로 같은 처리 함수를 직접 부른다(대화상자 가드 포함).
            if (!IsModalOpen) HandleShortcut(new InputEventKey { Keycode = Key.S, CtrlPressed = true, Pressed = true });
            await NextFrame();
        }
    }

    private async Task PressCtrlTab()
    {
        if (HasDisplay) await SendKey(Key.Tab, ctrl: true);
        else
        {
            _Input(new InputEventKey { Keycode = Key.Tab, CtrlPressed = true, Pressed = true });
            await NextFrame();
        }
    }

    /// <summary>
    /// 저장 대화상자를 열고 경로를 입력한 뒤 저장 버튼을 누른다. 기존 파일이면 대화상자가 띄운 덮어쓰기 확인 창을 돌려준다.
    /// </summary>
    private async Task<ConfirmationDialog?> ChooseInSaveDialog(Action open, string path, bool expectConfirm = false)
    {
        open();
        await NextFrame();
        if (_modal is not FileDialog dialog) return null;
        dialog.CurrentPath = path;
        await NextFrame();
        dialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
        await NextFrame();
        await NextFrame();
        if (!expectConfirm) return null;
        return dialog.FindChildren("*", "ConfirmationDialog", true, false).OfType<ConfirmationDialog>()
            .Concat(dialog.GetChildren(true).OfType<ConfirmationDialog>())
            .FirstOrDefault(c => c != dialog && c.Visible);
    }

    private async Task CheckAntsDrawn(Action<bool, string> check, Rect2 view, string label)
    {
        _camera.FitTo(view);
        _camera.Position -= new Vector2(view.Size.X * 0.15f, 0); // 왼쪽 패널만큼
        for (int i = 0; i < 3; i++) await NextFrame();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        var toScreen = _view.GetGlobalTransformWithCanvas();
        var frame = _snapshot!.Ants;
        int inView = 0, drawn = 0;
        for (int i = 0; i < frame.Count; i++)
        {
            if (frame.Layer[i] != (byte)_view.Layer) continue;
            var p = toScreen * new Vector2(frame.X[i], frame.Y[i]);
            if (p.X < 450 || p.Y < 40 || p.X >= image.GetWidth() || p.Y >= image.GetHeight()) continue;
            inView++;
            if (image.GetPixel((int)p.X, (int)p.Y).Luminance < 0.2f) drawn++;
        }
        check(inView > 0 && drawn >= inView * 0.8, $"화면에 개미가 실제로 그려짐 [{label}] (개미 중심 픽셀이 몸통 색: {drawn}/{inView})");
    }

    /// <summary>같은 장면을 서로 다른 틱에서 캡처한다(히트맵 끔, 통로 5개 주변 확대).</summary>
    private async Task CaptureAntScreenshots(string path)
    {
        var host = _host!;
        _simPanel.SelectHeatmap(HeatmapMode.Hidden);
        await ApplyAntSettings(_antSettings with { InitialCount = 150, Seed = 3 });
        await WaitUntil(() => _snapshot is { Tick: 0 } s && s.Ants.Count == 150, "캡처용 초기화");
        _camera.FitTo(new Rect2(84, 52, 28, 16));
        _camera.Position -= new Vector2(4, 0);

        SetLayer(LayerId.Nest);
        await SaveScreenshot(path.Replace(".png", "-ants-t0-nest.png"));
        SetLayer(LayerId.Surface);
        foreach (int tick in new[] { 30, 150, 600 })
        {
            await host.RunExclusiveAsync(s =>
            {
                s.Step(tick - (int)s.Tick);
                return true;
            });
            await WaitUntil(() => _snapshot is { } s && s.Tick == tick, $"틱 {tick} 스냅샷");
            if (tick == 600)
            {
                // 통로 주변에서 가장 가까운 지상 개미를 선택하고 그 개미를 중심으로 찍는다(선택 정보 표시 확인).
                var f = _snapshot!.Ants;
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < f.Count; i++)
                {
                    if (f.Layer[i] != (byte)LayerId.Surface) continue;
                    float d = new Vector2(f.X[i] - 98, f.Y[i] - 60).Length();
                    if (d < bestDistance) { bestDistance = d; best = i; }
                }
                if (best >= 0)
                {
                    SelectAnt(f.Id[best], _snapshot.ResetCount);
                    _camera.FitTo(new Rect2(f.X[best] - 14, f.Y[best] - 8, 28, 16));
                    _camera.Position -= new Vector2(4, 0);
                }
            }
            await SaveScreenshot(path.Replace(".png", $"-ants-t{tick}.png"));
        }
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
