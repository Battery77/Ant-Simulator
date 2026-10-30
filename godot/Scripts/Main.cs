using AntSim.Core.Editing;
using AntSim.Core.Serialization;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

/// <summary>
/// 조립 루트: 코어 월드·편집기와 Godot 표현 노드(MapView, 카메라, 패널)를 연결하고 입력을 편집 명령으로 바꾼다.
/// 편집 로직(검증, Undo, 스트로크)은 모두 AntSim.Core에 있다.
/// 편집 모드에서는 원본 월드(<see cref="_world"/>)를 고치고, 시뮬레이션 모드(Main.Simulation.cs)에서는
/// 복제된 월드로 계산 스레드에서 실험을 실행한다.
/// </summary>
public partial class Main : Node
{
    private const string MapFileFilter = "*.antmap ; 개미 시뮬레이터 맵";

    private MapView _view = null!;
    private EditorCamera _camera = null!;
    private EditorPanel _panel = null!;
    private World _world = null!;
    private MapEditor _editor = null!;
    private EditorTool _tool = new(EditorToolKind.Inspect);
    private EditorStroke? _stroke;
    private Vector2I? _pinnedTile;
    private string? _currentPath;
    private AppMode _mode = AppMode.Edit;
    private Toast _toast = null!;

    /// <summary>열려 있는 파일 대화상자·확인 창. 열려 있는 동안 게임 단축키와 입력란 처리를 하지 않는다(대화상자 입력을 방해하지 않음).</summary>
    private Window? _modal;

    private bool IsModalOpen => _modal is not null && IsInstanceValid(_modal);

    public override void _Ready()
    {
        _view = new MapView { Name = "MapView" };
        AddChild(_view);
        _heatmap = new HeatmapView { Name = "Heatmap" };
        AddChild(_heatmap);
        _antView = new AntView { Name = "AntView" };
        AddChild(_antView);
        _camera = new EditorCamera { Name = "Camera" };
        AddChild(_camera);
        _camera.MakeCurrent();

        var ui = new CanvasLayer { Name = "UI" };
        AddChild(ui);
        _panel = new EditorPanel { Name = "EditorPanel" };
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.LeftWide);
        ui.AddChild(_panel);

        _panel.ToolChanged += tool => _tool = tool;
        _panel.LayerChanged += SetLayer;
        _panel.UndoRequested += Undo;
        _panel.RedoRequested += Redo;
        _panel.SaveRequested += ShowSaveDialog;
        _panel.LoadRequested += ShowLoadDialog;
        _panel.NewMapRequested += (w, h) => NewMap(w, h);

        _simPanel = new SimulationPanel { Name = "SimulationPanel", Visible = false };
        _simPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.LeftWide);
        ui.AddChild(_simPanel);
        ConnectSimulationPanel();

        _modeBar = new ModeBar { Name = "ModeBar" };
        _modeBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _modeBar.GrowHorizontal = Control.GrowDirection.Both;
        ui.AddChild(_modeBar);
        _modeBar.ModeRequested += SetMode;

        _toast = new Toast { Name = "Toast" };
        ui.AddChild(_toast);

        NewMap(1024, 1024);
        if (IsSmokeTest) RunSmokeTest();
    }

    public override void _Process(double delta)
    {
        // 버튼 해제가 UI 위에서 일어나 이 노드에 전달되지 않은 경우에도 스트로크를 닫는다.
        if (_stroke is not null && !Input.IsMouseButtonPressed(MouseButton.Left)) EndStroke();

        bool inside = _view.TryGetTileUnderMouse(out var tile);
        _view.SetHover(inside ? tile : null);
        var shown = _pinnedTile ?? (inside ? tile : null);

        if (_mode == AppMode.Simulation)
        {
            ProcessSimulation(shown);
            return;
        }
        _panel.SetInfo(shown is { } t ? EditorText.DescribeTile(_world, _view.Layer, t.X, t.Y) : "");
        _panel.SetHistoryState(_editor.CanUndo, _editor.CanRedo);
    }

    public override void _ExitTree() => StopSimulation();

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button when _mode == AppMode.Simulation:
                // 시뮬레이션 모드에서는 편집하지 않는다. 개미 근처를 누르면 개미 선택, 아니면 타일 정보 고정.
                if (button.Pressed)
                {
                    var target = _view.ScreenToTile(button.Position);
                    var point = _view.GetGlobalTransformWithCanvas().AffineInverse() * button.Position;
                    bool antSelected = TrySelectAnt(point);
                    _pinnedTile = CurrentWorld.Config.InBounds(target.X, target.Y) ? target : null;
                    if (antSelected) _pinnedTile = null;
                }
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                if (button.Pressed) BeginStroke(_view.ScreenToTile(button.Position));
                else EndStroke();
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseMotion motion when _stroke is not null && (motion.ButtonMask & MouseButtonMask.Left) != 0:
                var tile = _view.ScreenToTile(motion.Position);
                _stroke.MoveTo(tile.X, tile.Y);
                GetViewport().SetInputAsHandled();
                break;

        }
    }

    /// <summary>
    /// GUI보다 먼저 받는 입력. Tab은 Godot 기본 포커스 이동(입력란 사이 이동)에 맡기고, 레이어 전환은 Ctrl+Tab으로 여기서 처리한다
    /// (입력란에 포커스가 있어도 동작). 숫자 입력란의 Enter·Escape·바깥 클릭 확정도 여기서 처리한다(<see cref="NumberFieldFocus"/>).
    /// 파일 대화상자·확인 창이 열려 있으면 아무것도 하지 않는다.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (IsModalOpen) return;
        switch (@event)
        {
            case InputEventKey { Pressed: true, Keycode: Key.Tab } key when key.CtrlPressed:
                if (!key.Echo) ToggleLayer();
                GetViewport().SetInputAsHandled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } when NumberFieldFocus.Focused(GetViewport()) is { } box:
                NumberFieldFocus.Cancel(box);
                GetViewport().SetInputAsHandled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter }
                when NumberFieldFocus.Focused(GetViewport()) is { } box && box.GetLineEdit().IsEditing():
                NumberFieldFocus.Commit(box);
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } mouse
                when NumberFieldFocus.Focused(GetViewport()) is { } box && !box.GetGlobalRect().HasPoint(mouse.Position):
                // 입력란 밖(맵, 버튼, 다른 입력란)을 누르면 먼저 확정한다. 클릭 자체는 그대로 전달되어 버튼은 확정된 값으로 동작하고,
                // 다른 입력란은 포커스를 받는다.
                NumberFieldFocus.Commit(box);
                break;
        }
    }

    public override void _ShortcutInput(InputEvent @event)
    {
        if (IsModalOpen) return;
        if (@event is InputEventKey { Pressed: true, Echo: false } key) HandleShortcut(key);
    }

    public override void _Notification(int what)
    {
        // 드래그 중 창이 포커스를 잃으면 버튼 해제 이벤트를 못 받을 수 있으므로 스트로크를 닫는다.
        if (what == NotificationWMWindowFocusOut) EndStroke();
    }

    private void HandleShortcut(InputEventKey key)
    {
        bool ctrl = key.CtrlPressed || key.MetaPressed;
        if (key.Keycode == Key.Escape)
        {
            _pinnedTile = null;
            ClearAntSelection();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_mode == AppMode.Simulation)
        {
            if (HandleSimulationShortcut(key)) GetViewport().SetInputAsHandled();
            return;
        }

        switch (key.Keycode)
        {
            case Key.Z when ctrl && key.ShiftPressed:
            case Key.Y when ctrl:
                Redo();
                break;
            case Key.Z when ctrl:
                Undo();
                break;
            case Key.S when ctrl:
                if (_currentPath is null) ShowSaveDialog();
                else SaveTo(_currentPath);
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    private void BeginStroke(Vector2I start)
    {
        EndStroke();
        if (!_tool.Edits)
        {
            _pinnedTile = _world.Config.InBounds(start.X, start.Y) ? start : null;
            return;
        }
        _stroke = EditorStroke.Begin(_editor, _tool, _view.Layer, start.X, start.Y);
    }

    private void EndStroke()
    {
        if (_stroke is null) return;
        var stroke = _stroke;
        _stroke = null;
        stroke.End();
        _panel.SetStatus(stroke.FailedCount == 0
            ? $"{stroke.AppliedCount}칸 변경"
            : $"{stroke.AppliedCount}칸 변경, {stroke.FailedCount}칸 실패: {EditorText.Describe(stroke.LastError)}");
    }

    /// <summary>보는 레이어를 지상 ↔ 개미집으로 바꾼다(Ctrl+Tab).</summary>
    private void ToggleLayer() => SetLayer(_view.Layer == LayerId.Surface ? LayerId.Nest : LayerId.Surface);

    private void SetLayer(LayerId layer)
    {
        EndStroke();
        _view.Layer = layer;
        // 보는 레이어만 바꾼다(표시 상태). 시뮬레이션·개미의 실제 레이어·냄새·호스트는 건드리지 않는다.
        _panel.SetLayer(layer);
        _simPanel.SetLayer(layer);
        OnSimulationLayerChanged();
    }

    private void Undo()
    {
        EndStroke();
        _panel.SetStatus(_editor.Undo() ? "실행 취소" : "취소할 편집이 없습니다");
    }

    private void Redo()
    {
        EndStroke();
        _panel.SetStatus(_editor.Redo() ? "다시 실행" : "다시 실행할 편집이 없습니다");
    }

    private void NewMap(int width, int height)
    {
        try
        {
            SetWorld(WorldPresets.StandardNest(new WorldConfig(width, height)), null);
            _panel.SetStatus($"새 맵 {width}×{height}");
        }
        catch (ArgumentException e)
        {
            _panel.SetStatus($"새 맵을 만들 수 없습니다: {e.Message}");
        }
    }

    private void SetWorld(World world, string? path)
    {
        EndStroke();
        _world = world;
        _editor = new MapEditor(world);
        _currentPath = path;
        _pinnedTile = null;
        _view.SetWorld(world);
        _camera.FitTo(new Rect2(0, 0, world.Config.Width, world.Config.Height));
    }

    private void ShowSaveDialog() => ShowFileDialog(FileDialog.FileModeEnum.SaveFile, MapFileFilter, SaveTo);

    private void ShowLoadDialog() => ShowFileDialog(FileDialog.FileModeEnum.OpenFile, MapFileFilter, LoadFrom);

    /// <summary>
    /// 파일 대화상자. 열기는 운영체제 대화상자를 쓴다. 저장은 Godot 대화상자를 쓴다: 확장자를 붙인 최종 경로로 "이미 있는 파일입니다.
    /// 덮어쓸까요?"를 직접 묻기 때문에(운영체제 대화상자는 확장자 없이 입력한 이름으로 존재 여부를 판단해 확인 없이 덮어쓸 수 있음)
    /// 모든 플랫폼에서 덮어쓰기 확인이 한 번만 나온다. 취소하면 아무것도 저장하지 않는다.
    /// </summary>
    private void ShowFileDialog(FileDialog.FileModeEnum mode, string filter, Action<string> onSelected)
    {
        EndStroke();
        if (IsModalOpen) return;
        bool save = mode == FileDialog.FileModeEnum.SaveFile;
        var dialog = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { filter },
            UseNativeDialog = !save,
        };
        _modal = dialog;
        dialog.FileSelected += path =>
        {
            ClearModal(dialog);
            if (!save)
            {
                onSelected(path);
                return;
            }
            // 대화상자가 확장자를 붙이지 못한 경우(예외적)에만 붙이고, 그 경로가 이미 있으면 여기서 한 번 묻는다.
            string extension = filter.Split(';')[0].Trim().TrimStart('*');
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                onSelected(path);
                return;
            }
            string withExtension = path + extension;
            if (File.Exists(withExtension)) ConfirmOverwrite(withExtension, () => onSelected(withExtension));
            else onSelected(withExtension);
        };
        dialog.Canceled += () => ClearModal(dialog);
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void ConfirmOverwrite(string path, Action onConfirmed)
    {
        var confirm = new ConfirmationDialog
        {
            Title = "덮어쓰기 확인",
            DialogText = $"이미 있는 파일입니다. 덮어쓸까요?\n{path}",
            OkButtonText = "덮어쓰기",
            CancelButtonText = "취소",
        };
        _modal = confirm;
        confirm.Confirmed += () =>
        {
            ClearModal(confirm);
            onConfirmed();
        };
        confirm.Canceled += () => ClearModal(confirm);
        AddChild(confirm);
        confirm.PopupCentered();
    }

    private void ClearModal(Window window)
    {
        if (_modal == window) _modal = null;
        window.QueueFree();
    }

    /// <summary>저장 결과를 상태 줄과 화면 위 알림으로 함께 알린다(실제 저장이 끝난 뒤에만 호출).</summary>
    private void NotifySaved(string what, string path, bool simulationPanel)
    {
        string message = $"{what}을 저장했습니다. — {Path.GetFileName(path)}";
        if (simulationPanel) _simPanel.SetStatus($"{message}\n{path}");
        else _panel.SetStatus($"{message}\n{path}");
        _toast.ShowSuccess(message);
    }

    /// <summary>저장 실패를 알린다. 화면 위 알림은 파일 이름과 짧은 이유, 상태 줄에는 원래 오류 문구까지 남긴다.</summary>
    private void NotifySaveFailed(string what, string path, Exception error, bool simulationPanel)
    {
        string reason = error switch
        {
            DirectoryNotFoundException => "폴더를 찾을 수 없습니다.",
            UnauthorizedAccessException => "쓰기 권한이 없습니다.",
            PathTooLongException => "경로가 너무 깁니다.",
            OperationCanceledException or ObjectDisposedException => "시뮬레이션이 종료되어 저장하지 못했습니다.",
            _ => error.Message,
        };
        string message = $"{what} 저장 실패 — {Path.GetFileName(path)}: {reason}";
        if (simulationPanel) _simPanel.SetStatus($"{message}\n{error.Message}");
        else _panel.SetStatus($"{message}\n{error.Message}");
        _toast.ShowError(message);
    }

    private void SaveTo(string path)
    {
        try
        {
            MapSerializer.Save(_world, path);
            _currentPath = path;
            NotifySaved("맵", path, simulationPanel: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            NotifySaveFailed("맵", path, e, simulationPanel: false);
        }
    }

    private void LoadFrom(string path)
    {
        try
        {
            SetWorld(MapSerializer.Load(path), path);
            _panel.SetStatus($"불러옴: {path} ({_world.Config.Width}×{_world.Config.Height})");
        }
        catch (Exception e) when (e is MapFormatException or IOException or UnauthorizedAccessException)
        {
            _panel.SetStatus($"불러오기 실패: {e.Message}");
        }
    }
}
