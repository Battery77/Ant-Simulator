using System.Globalization;
using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Rendering;
using AntSim.Core.Scent;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

public enum HeatmapMode
{
    Hidden,
    /// <summary>냄새 한 종류(기존 단일 히트맵).</summary>
    Single,
    /// <summary>모든 냄새 종류를 색으로 합성(종류별 독립 정규화).</summary>
    Composite,
}

/// <summary>히트맵 표시 선택. 고정 최대값은 냄새 종류별로 따로 둔다.</summary>
public sealed record HeatmapOptions(HeatmapMode Mode, int TypeIndex, HeatmapScale Scale, bool AutoMax, IReadOnlyList<float> FixedMax)
{
    public float FixedMaxOf(int type) => type < FixedMax.Count ? FixedMax[type] : 10f;
}

/// <summary>범례 한 줄에 표시할 값: 종류, 현재 최대 농도, 실제 표시 기준 최대값.</summary>
public readonly record struct LegendEntry(int TypeIndex, float CurrentMax, float ShownMax);

/// <summary>시뮬레이션 모드 왼쪽 패널: 실행 제어, 히트맵, 냄새 설정, 실험 저장/불러오기, 타일 정보.</summary>
public partial class SimulationPanel : PanelContainer
{
    private static readonly double[] Speeds = { 1, 2, 5, 10, 50 };

    private Label _clock = null!;
    private OptionButton _speed = null!;
    private OptionButton _heatType = null!;
    private OptionButton _heatScale = null!;
    private CheckBox _autoMax = null!;
    private VBoxContainer _legend = null!;
    private readonly List<(int Type, Label Text, SpinBox Fixed)> _legendRows = new();
    private float[] _fixedMax = Array.Empty<float>();
    private Label _maxLabel = null!;
    private Func<int, Gradient>? _legendGradient;
    private SpinBox _tick = null!;
    private OptionButton _settingsType = null!;
    private SpinBox _diffusion = null!;
    private SpinBox _passageDiffusion = null!;
    private SpinBox _decay = null!;
    private SpinBox _emission = null!;
    private Label _settingsMessage = null!;
    private Label _info = null!;
    private Label _antCount = null!;
    private SpinBox _antInitialCount = null!;
    private SpinBox _antSeed = null!;
    private AntSettings _antSettings = AntSettings.Default;
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
    public event Action<AntSettings>? AntSettingsApplyRequested;
    public event Action? LoadExperimentRequested;

    /// <summary>보는 레이어 변경 요청(표시만 바뀐다. 시뮬레이션 상태와 무관).</summary>
    public event Action<LayerId>? LayerChanged;

    private readonly ButtonGroup _layerGroup = new();
    private readonly Dictionary<LayerId, Button> _layerButtons = new();

    public HeatmapOptions Heatmap
    {
        get
        {
            int selected = _heatType.Selected, types = _pending.Types.Count;
            var mode = selected <= 0 ? HeatmapMode.Hidden : selected <= types ? HeatmapMode.Single : HeatmapMode.Composite;
            return new HeatmapOptions(mode, mode == HeatmapMode.Single ? selected - 1 : -1,
                _heatScale.Selected == 1 ? HeatmapScale.Log : HeatmapScale.Linear,
                _autoMax.ButtonPressed, (float[])_fixedMax.Clone());
        }
    }

    /// <summary>보는 레이어 버튼 상태를 맞춘다. ButtonPressed 설정은 toggled만 보내고 Pressed(→ LayerChanged)는 보내지 않는다.</summary>
    public void SetLayer(LayerId layer)
    {
        if (_layerButtons.TryGetValue(layer, out var button)) button.ButtonPressed = true;
    }

    /// <summary>보는 레이어 버튼(스모크 테스트에서 실제 클릭·표시 확인용).</summary>
    internal Button LayerButton(LayerId layer) => _layerButtons[layer];

    /// <summary>타일·개미 정보 칸의 현재 문구(스모크 테스트용).</summary>
    internal string InfoText => _info.Text;

    /// <summary>현재 눌린 보는 레이어 버튼.</summary>
    public LayerId ShownLayer => _layerButtons.FirstOrDefault(p => p.Value.ButtonPressed).Key;

    /// <summary>범례 색 막대(종류 → 그라디언트). 단일 모드는 type = -1로 호출된다.</summary>
    public void SetLegendGradientProvider(Func<int, Gradient> provider) => _legendGradient = provider;

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

        // 편집 패널과 같은 위치·방식의 레이어 전환(편집 패널은 시뮬레이션 모드에서 숨겨지므로 여기에도 둔다).
        root.AddChild(Header("보는 레이어 (Ctrl+Tab)"));
        var layers = new HBoxContainer();
        root.AddChild(layers);
        foreach (var layer in new[] { LayerId.Surface, LayerId.Nest })
        {
            var button = new Button
            {
                Text = EditorText.Name(layer), ToggleMode = true, ButtonGroup = _layerGroup,
                FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            button.Pressed += () => LayerChanged?.Invoke(layer);
            layers.AddChild(button);
            _layerButtons[layer] = button;
        }
        _layerButtons[LayerId.Surface].ButtonPressed = true;

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

        root.AddChild(Header("개미 (클릭: 선택)"));
        _antCount = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; // 숫자·레이어가 바뀌어도 패널 폭이 흔들리지 않게
        root.AddChild(_antCount);
        _antInitialCount = AddNumberRow(root, "개체 수", 0, AntSettings.MaxCount, 1);
        _antSeed = AddNumberRow(root, "시드", 0, int.MaxValue, 1);
        root.AddChild(ActionButton("개미 설정 적용 (초기화)", () => AntSettingsApplyRequested?.Invoke(PendingAntSettings)));

        root.AddChild(Header("히트맵"));
        _heatType = Options("끄기");
        _heatType.ItemSelected += _ => OnHeatmapOptionsChanged();
        root.AddChild(_heatType);
        _heatScale = Options("선형", "로그");
        _heatScale.ItemSelected += _ => OnHeatmapOptionsChanged();
        root.AddChild(_heatScale);
        _autoMax = new CheckBox { Text = "자동 최대값 (냄새 종류별)", ButtonPressed = true, FocusMode = FocusModeEnum.None };
        _autoMax.Toggled += _ => OnHeatmapOptionsChanged();
        root.AddChild(_autoMax);
        _legend = new VBoxContainer();
        root.AddChild(_legend);
        _maxLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.7f) };
        root.AddChild(_maxLabel);

        root.AddChild(Header("타일 정보 (클릭: 고정, Esc: 해제)"));
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_info);

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
        file.AddChild(ActionButton("실험 저장 (Ctrl+S)", () => SaveExperimentRequested?.Invoke()));
        file.AddChild(ActionButton("실험 불러오기", () => LoadExperimentRequested?.Invoke()));

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
        if (settings.Types.Count > 1) _heatType.AddItem($"복합: {string.Join(" + ", settings.Types.Select(t => t.DisplayName))}");
        _heatType.Selected = Math.Min(heatSelected == 0 && settings.Types.Count > 0 ? 1 : heatSelected, _heatType.ItemCount - 1);
        if (_fixedMax.Length != settings.Types.Count)
        {
            var resized = new float[settings.Types.Count];
            for (int i = 0; i < resized.Length; i++) resized[i] = i < _fixedMax.Length ? _fixedMax[i] : 10f;
            _fixedMax = resized;
        }
        RebuildLegend();

        int typeSelected = Math.Clamp(_settingsType.Selected, 0, settings.Types.Count - 1);
        _settingsType.Clear();
        foreach (var type in settings.Types) _settingsType.AddItem(type.DisplayName);
        _settingsType.Selected = typeSelected;
        _tick.Value = settings.TickSeconds;
        _populating = false;
        ShowPendingType();
    }

    /// <summary>히트맵 선택(끄기/단일/복합)을 고른다. 테스트·단축 조작용.</summary>
    public void SelectHeatmap(HeatmapMode mode, int typeIndex = 0)
    {
        _heatType.Selected = mode switch
        {
            HeatmapMode.Hidden => 0,
            HeatmapMode.Single => typeIndex + 1,
            _ => Math.Min(_pending.Types.Count + 1, _heatType.ItemCount - 1),
        };
        OnHeatmapOptionsChanged();
    }

    /// <summary>범례의 냄새별 현재 최대 농도와 표시 기준값을 갱신한다.</summary>
    public void SetLegendValues(ReadOnlySpan<LegendEntry> entries)
    {
        foreach (var entry in entries)
        foreach (var row in _legendRows)
        {
            if (row.Type != entry.TypeIndex) continue;
            string name = _pending.Types[entry.TypeIndex].DisplayName;
            row.Text.Text = $"{name} · 표시 최대 {entry.ShownMax:0.####} {(_autoMax.ButtonPressed ? "(자동)" : "(고정)")}\n현재 최대 {entry.CurrentMax:0.####}";
        }
    }

    /// <summary>입력란 값으로 만든 개미 설정(나머지 값은 현재 설정 유지).</summary>
    public AntSettings PendingAntSettings =>
        _antSettings with { InitialCount = (int)_antInitialCount.Value, Seed = (ulong)_antSeed.Value };

    public void SetAntSettings(AntSettings settings)
    {
        _antSettings = settings;
        _antInitialCount.Value = settings.InitialCount;
        _antSeed.Value = Math.Min(settings.Seed, int.MaxValue);
    }

    /// <summary>입력란만 바꾼다(테스트·단축 조작용). 적용은 <see cref="AntSettingsApplyRequested"/>.</summary>
    public void SetPendingAntCount(int count) => _antInitialCount.Value = count;

    public void SetAntCount(string text) => _antCount.Text = text;

    public void SetClock(string text) => _clock.Text = text;
    public int LegendRowCount => _legendRows.Count;

    public void SetSettingsMessage(string text) => _settingsMessage.Text = text;
    public void SetInfo(string text) => _info.Text = text;
    public void SetStatus(string text) => _status.Text = text;

    private void OnHeatmapOptionsChanged()
    {
        RebuildLegend();
        HeatmapChanged?.Invoke(Heatmap);
    }

    /// <summary>표시 중인 냄새 종류마다 색 막대, 표시 최대값, 고정 최대값 입력을 한 줄씩 만든다.</summary>
    private void RebuildLegend()
    {
        if (_legend is null) return;
        foreach (var child in _legend.GetChildren()) child.QueueFree();
        _legendRows.Clear();

        var options = Heatmap;
        IEnumerable<int> types = options.Mode switch
        {
            HeatmapMode.Single => new[] { options.TypeIndex },
            HeatmapMode.Composite => Enumerable.Range(0, _pending.Types.Count),
            _ => Array.Empty<int>(),
        };
        foreach (int type in types)
        {
            var row = new HBoxContainer();
            var gradient = _legendGradient?.Invoke(options.Mode == HeatmapMode.Composite ? type : -1);
            if (gradient is not null)
            {
                row.AddChild(new TextureRect
                {
                    Texture = new GradientTexture1D { Gradient = gradient, Width = 64 },
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                    CustomMinimumSize = new Vector2(36, 28),
                });
            }
            var text = new Label { Text = _pending.Types[type].DisplayName, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddChild(text);
            var fixedMax = Number(0.0001, 1_000_000, 0.0001, _fixedMax[type]);
            fixedMax.CustomMinimumSize = new Vector2(90, 0);
            fixedMax.Editable = !_autoMax.ButtonPressed;
            fixedMax.TooltipText = "고정 최대값 (자동 최대값을 끄면 사용)";
            int captured = type;
            fixedMax.ValueChanged += v =>
            {
                _fixedMax[captured] = (float)v;
                HeatmapChanged?.Invoke(Heatmap);
            };
            row.AddChild(fixedMax);
            _legend.AddChild(row);
            _legendRows.Add((type, text, fixedMax));
        }

        _maxLabel.Text = options.Mode == HeatmapMode.Composite
            ? "복합 모드: 냄새마다 따로 정규화합니다. 색의 진하기는 같은 냄새 안에서만 비교하고, 냄새 간 실제 농도는 위의 표시 최대값으로 비교하세요."
            : options.Mode == HeatmapMode.Hidden ? "히트맵 꺼짐" : "";
    }

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
