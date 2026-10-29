using Godot;

namespace AntSim.App;

public enum AppMode
{
    /// <summary>월드 원본을 만들고 고치는 모드(Undo/Redo 제공).</summary>
    Edit,
    /// <summary>편집 월드의 복제본으로 실험을 실행하는 모드(원본은 바뀌지 않음).</summary>
    Simulation,
}

/// <summary>화면 위쪽 가운데의 모드 전환 막대.</summary>
public partial class ModeBar : PanelContainer
{
    private readonly ButtonGroup _group = new();
    private Button _edit = null!;
    private Button _simulation = null!;

    public event Action<AppMode>? ModeRequested;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var row = new HBoxContainer();
        AddChild(row);
        _edit = AddModeButton(row, "편집 모드", AppMode.Edit);
        _simulation = AddModeButton(row, "시뮬레이션 모드", AppMode.Simulation);
        _edit.ButtonPressed = true;
    }

    public void SetMode(AppMode mode) => (mode == AppMode.Edit ? _edit : _simulation).SetPressedNoSignal(true);

    private Button AddModeButton(HBoxContainer row, string text, AppMode mode)
    {
        var button = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonGroup = _group,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(150, 0),
        };
        button.Pressed += () => ModeRequested?.Invoke(mode);
        row.AddChild(button);
        return button;
    }
}
