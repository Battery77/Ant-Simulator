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

    public override void _Ready()
    {
        _view = new MapView { Name = "MapView" };
        AddChild(_view);
        _heatmap = new HeatmapView { Name = "Heatmap" };
        AddChild(_heatmap);
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
                // 시뮬레이션 모드에서는 편집하지 않고 타일 정보만 고정한다.
                if (button.Pressed)
                {
                    var target = _view.ScreenToTile(button.Position);
                    _pinnedTile = CurrentWorld.Config.InBounds(target.X, target.Y) ? target : null;
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

    public override void _ShortcutInput(InputEvent @event)
    {
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
        if (key.Keycode == Key.Tab)
        {
            var next = _view.Layer == LayerId.Surface ? LayerId.Nest : LayerId.Surface;
            SetLayer(next);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.Keycode == Key.Escape)
        {
            _pinnedTile = null;
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

    private void SetLayer(LayerId layer)
    {
        EndStroke();
        _view.Layer = layer;
        _panel.SetLayer(layer);
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

    private void ShowFileDialog(FileDialog.FileModeEnum mode, string filter, Action<string> onSelected)
    {
        EndStroke();
        var dialog = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { filter },
            UseNativeDialog = true,
        };
        dialog.FileSelected += path =>
        {
            onSelected(path);
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void SaveTo(string path)
    {
        try
        {
            MapSerializer.Save(_world, path);
            _currentPath = path;
            _panel.SetStatus($"저장됨: {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _panel.SetStatus($"저장 실패: {e.Message}");
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
