using System.Globalization;
using AntSim.Core.Rendering;
using AntSim.Core.Scent;
using Godot;

namespace AntSim.App;

/// <summary>히트맵 표시 선택. TypeIndex가 -1이면 히트맵을 끈다.</summary>
public readonly record struct HeatmapOptions(int TypeIndex, HeatmapScale Scale, bool AutoMax, float FixedMax);

/// <summary>시뮬레이션 모드 왼쪽 패널: 실행 제어, 히트맵, 냄새 설정, 실험 저장/불러오기, 타일 정보.</summary>
public partial class SimulationPanel : PanelContainer
{
    private static readonly double[] Speeds = { 1, 2, 5, 10, 50 };

    private Label _clock = null!;
    private OptionButton _speed = null!;
    private OptionButton _heatType = null!;
    private OptionButton _heatScale = null!;
    private CheckBox _autoMax = null!;
    private SpinBox _fixedMax = null!;
    private Label _maxLabel = null!;
    private SpinBox _tick = null!;
    private OptionButton _settingsType = null!;
    private SpinBox _diffusion = null!;
    private SpinBox _passageDiffusion = null!;
    private SpinBox _decay = null!;
    private SpinBox _emission = null!;
    private Label _settingsMessage = null!;
    private Label _info = null!;
    private Label _status = null!;
    private ScentSettings _pending = ScentSettings.Default;
    private bool _populating;

    public event Action? PlayRequested;
    public event Action? PauseRequested;
    public event Action? StepRequested;
    public event Action? ResetRequested;
    public event Action<double>? SpeedChanged;
    public event Action<HeatmapOptions>? HeatmapChanged;
    public event Action<ScentSettings>? SettingsApplyRequested;
    public event Action? SaveExperimentRequested;
    public event Action? LoadExperimentRequested;

    public HeatmapOptions Heatmap => new(
        _heatType.Selected - 1,
        _heatScale.Selected == 1 ? HeatmapScale.Log : HeatmapScale.Linear,
        _autoMax.ButtonPressed,
        (float)_fixedMax.Value);

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(280, 0);
        MouseFilter = MouseFilterEnum.Stop;
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 8);
        AddChild(margin);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        margin.AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(root);

        root.AddChild(Header("실행 (Space: 재생/일시정지, .: 단일 틱)"));
        var controls = new GridContainer { Columns = 2 };
        root.AddChild(controls);
        controls.AddChild(ActionButton("재생", () => PlayRequested?.Invoke()));
        controls.AddChild(ActionButton("일시정지", () => PauseRequested?.Invoke()));
        controls.AddChild(ActionButton("단일 틱", () => StepRequested?.Invoke()));
        controls.AddChild(ActionButton("초기화", () => ResetRequested?.Invoke()));
        _speed = Options(Speeds.Select(s => $"속도 ×{s}").ToArray());
        _speed.ItemSelected += i => SpeedChanged?.Invoke(Speeds[i]);
        root.AddChild(_speed);
        _clock = new Label();
        root.AddChild(_clock);

        root.AddChild(Header("히트맵"));
        _heatType = Options("끄기");
        _heatType.ItemSelected += _ => HeatmapChanged?.Invoke(Heatmap);
        root.AddChild(_heatType);
        _heatScale = Options("선형", "로그");
        _heatScale.ItemSelected += _ => HeatmapChanged?.Invoke(Heatmap);
        root.AddChild(_heatScale);
        var maxRow = new HBoxContainer();
        _autoMax = new CheckBox { Text = "자동 최대값", ButtonPressed = true, FocusMode = FocusModeEnum.None };
        _autoMax.Toggled += _ => HeatmapChanged?.Invoke(Heatmap);
        _fixedMax = Number(0.001, 1_000_000, 0.001, 10);
        _fixedMax.ValueChanged += _ => HeatmapChanged?.Invoke(Heatmap);
        maxRow.AddChild(_autoMax);
        maxRow.AddChild(_fixedMax);
        root.AddChild(maxRow);
        _maxLabel = new Label();
        root.AddChild(_maxLabel);

        root.AddChild(Header("냄새 설정"));
        _tick = AddNumberRow(root, "틱 간격(초)", 0.001, ScentSettings.MaxTickSeconds, 0.001);
        _settingsType = Options();
        _settingsType.ItemSelected += _ => ShowPendingType();
        root.AddChild(_settingsType);
        _diffusion = AddNumberRow(root, "확산 D (/초)", 0, 100, 0.001);
        _passageDiffusion = AddNumberRow(root, "통로 확산 Dp (/초)", 0, 100, 0.001);
        _decay = AddNumberRow(root, "감쇠 k (/초)", 0, 100, 0.001);
        _emission = AddNumberRow(root, "발생 e (/초/개)", 0, 1000, 0.001);
        foreach (var box in new[] { _diffusion, _passageDiffusion, _decay, _emission }) box.ValueChanged += _ => StorePendingType();
        _tick.ValueChanged += v =>
        {
            if (!_populating) _pending = _pending.WithTick(v);
        };
        root.AddChild(ActionButton("설정 적용", () => SettingsApplyRequested?.Invoke(_pending)));
        _settingsMessage = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_settingsMessage);

        root.AddChild(Header("실험 상태"));
        var file = new HBoxContainer();
        root.AddChild(file);
        file.AddChild(ActionButton("실험 저장", () => SaveExperimentRequested?.Invoke()));
        file.AddChild(ActionButton("실험 불러오기", () => LoadExperimentRequested?.Invoke()));

        root.AddChild(Header("타일 정보"));
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_info);
        root.AddChild(Header("상태"));
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_status);
        root.AddChild(new Label
        {
            Text = "시뮬레이션은 편집 월드의 복제본에서 실행되며\n원본 맵에는 반영되지 않습니다.",
            Modulate = new Color(1, 1, 1, 0.6f),
        });

        SetSettings(ScentSettings.Default);
    }

    /// <summary>현재 적용된 설정을 표시한다(냄새 종류 목록도 갱신).</summary>
    public void SetSettings(ScentSettings settings)
    {
        _populating = true;
        _pending = settings;
        int heatSelected = Math.Max(0, _heatType.Selected);
        _heatType.Clear();
        _heatType.AddItem("끄기");
        foreach (var type in settings.Types) _heatType.AddItem($"냄새: {type.DisplayName}");
        _heatType.Selected = Math.Min(heatSelected == 0 && settings.Types.Count > 0 ? 1 : heatSelected, settings.Types.Count);

        int typeSelected = Math.Clamp(_settingsType.Selected, 0, settings.Types.Count - 1);
        _settingsType.Clear();
        foreach (var type in settings.Types) _settingsType.AddItem(type.DisplayName);
        _settingsType.Selected = typeSelected;
        _tick.Value = settings.TickSeconds;
        _populating = false;
        ShowPendingType();
    }

    public void SetClock(string text) => _clock.Text = text;
    public void SetMaxText(string text) => _maxLabel.Text = text;
    public void SetSettingsMessage(string text) => _settingsMessage.Text = text;
    public void SetInfo(string text) => _info.Text = text;
    public void SetStatus(string text) => _status.Text = text;

    private void ShowPendingType()
    {
        if (_settingsType.Selected < 0) return;
        var type = _pending.Types[_settingsType.Selected];
        _populating = true;
        _diffusion.Value = type.Diffusion;
        _passageDiffusion.Value = type.PassageDiffusion;
        _decay.Value = type.Decay;
        _emission.Value = type.Emission;
        _populating = false;
    }

    private void StorePendingType()
    {
        if (_populating || _settingsType.Selected < 0) return;
        int i = _settingsType.Selected;
        _pending = _pending.WithType(i, _pending.Types[i] with
        {
            Diffusion = _diffusion.Value,
            PassageDiffusion = _passageDiffusion.Value,
            Decay = _decay.Value,
            Emission = _emission.Value,
        });
    }

    private static SpinBox AddNumberRow(VBoxContainer root, string label, double min, double max, double step)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var box = Number(min, max, step, min);
        row.AddChild(box);
        root.AddChild(row);
        return box;
    }

    private static SpinBox Number(double min, double max, double step, double value) => new()
    {
        MinValue = min,
        MaxValue = max,
        Step = step,
        Value = value,
        AllowGreater = false,
        CustomMinimumSize = new Vector2(110, 0),
    };

    private static OptionButton Options(params string[] items)
    {
        var options = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var item in items) options.AddItem(item);
        return options;
    }

    private static Label Header(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", new Color(0.5f, 0.85f, 1f));
        return label;
    }

    private static Button ActionButton(string text, Action onPressed)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        button.Pressed += onPressed;
        return button;
    }

    public static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
