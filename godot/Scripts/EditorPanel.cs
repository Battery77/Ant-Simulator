using AntSim.Core.Editing;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

/// <summary>편집기 왼쪽 패널. 코드로 구성하며 동작은 이벤트로 <see cref="Main"/>에 전달한다.</summary>
public partial class EditorPanel : PanelContainer
{
    private readonly ButtonGroup _toolGroup = new();
    private readonly ButtonGroup _layerGroup = new();
    private readonly Dictionary<LayerId, Button> _layerButtons = new();
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private SpinBox _amount = null!;
    private SpinBox _newWidth = null!;
    private SpinBox _newHeight = null!;
    private Label _info = null!;
    private Label _status = null!;
    private EditorTool _baseTool = new(EditorToolKind.Inspect);

    public event Action<EditorTool>? ToolChanged;
    public event Action<LayerId>? LayerChanged;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? SaveRequested;
    public event Action? LoadRequested;
    public event Action<int, int>? NewMapRequested;

    public EditorTool CurrentTool => _baseTool with { Amount = (int)_amount.Value };

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(260, 0);
        MouseFilter = MouseFilterEnum.Stop;

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 8);
        AddChild(margin);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        margin.AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(root);

        root.AddChild(Header("레이어 (Ctrl+Tab)"));
        var layers = new HBoxContainer();
        root.AddChild(layers);
        foreach (var layer in new[] { LayerId.Surface, LayerId.Nest })
        {
            var button = ToggleButton(EditorText.Name(layer), _layerGroup);
            button.Pressed += () => LayerChanged?.Invoke(layer);
            layers.AddChild(button);
            _layerButtons[layer] = button;
        }
        _layerButtons[LayerId.Surface].ButtonPressed = true;

        root.AddChild(Header("도구"));
        var tools = new GridContainer { Columns = 2 };
        root.AddChild(tools);
        AddTool(tools, "조회", new EditorTool(EditorToolKind.Inspect), selected: true);
        AddTool(tools, "지형: 흙", new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Dirt));
        AddTool(tools, "지형: 물", new EditorTool(EditorToolKind.PaintTerrain, Terrain: Terrain.Water));
        AddTool(tools, "토양 배치", new EditorTool(EditorToolKind.PlaceSolid, Solid: SolidBlock.Soil));
        AddTool(tools, "바위 배치", new EditorTool(EditorToolKind.PlaceSolid, Solid: SolidBlock.Rock));
        AddTool(tools, "블록 제거", new EditorTool(EditorToolKind.ClearBlocks));
        AddTool(tools, "먹이 +", new EditorTool(EditorToolKind.AddQuantity, Quantity: QuantityKind.Food));
        AddTool(tools, "먹이 −", new EditorTool(EditorToolKind.RemoveQuantity, Quantity: QuantityKind.Food));
        AddTool(tools, "유충 +", new EditorTool(EditorToolKind.AddQuantity, Quantity: QuantityKind.Larva));
        AddTool(tools, "유충 −", new EditorTool(EditorToolKind.RemoveQuantity, Quantity: QuantityKind.Larva));
        AddTool(tools, "통로 생성", new EditorTool(EditorToolKind.AddPassage));
        AddTool(tools, "통로 제거", new EditorTool(EditorToolKind.RemovePassage));

        var amountRow = new HBoxContainer();
        amountRow.AddChild(new Label { Text = "수량 증감 단위" });
        _amount = new SpinBox { MinValue = 1, MaxValue = WorldRules.DefaultMaxQuantityPerTile, Value = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _amount.ValueChanged += _ => ToolChanged?.Invoke(CurrentTool);
        amountRow.AddChild(_amount);
        root.AddChild(amountRow);

        root.AddChild(Header("편집 기록"));
        var history = new HBoxContainer();
        root.AddChild(history);
        _undoButton = ActionButton("실행 취소 (Ctrl+Z)", () => UndoRequested?.Invoke());
        _redoButton = ActionButton("다시 실행 (Ctrl+Y)", () => RedoRequested?.Invoke());
        history.AddChild(_undoButton);
        history.AddChild(_redoButton);

        root.AddChild(Header("맵"));
        var file = new HBoxContainer();
        root.AddChild(file);
        file.AddChild(ActionButton("저장 (Ctrl+S)", () => SaveRequested?.Invoke()));
        file.AddChild(ActionButton("불러오기", () => LoadRequested?.Invoke()));
        var size = new HBoxContainer();
        root.AddChild(size);
        _newWidth = new SpinBox { MinValue = 16, MaxValue = 4096, Value = 1024, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _newHeight = new SpinBox { MinValue = 16, MaxValue = 4096, Value = 1024, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        size.AddChild(_newWidth);
        size.AddChild(new Label { Text = "×" });
        size.AddChild(_newHeight);
        root.AddChild(ActionButton("새 맵", () => NewMapRequested?.Invoke((int)_newWidth.Value, (int)_newHeight.Value)));

        root.AddChild(Header("타일 정보"));
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_info);

        root.AddChild(Header("상태"));
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_status);

        root.AddChild(new Label
        {
            Text = "왼쪽 클릭·드래그: 편집\n가운데/오른쪽 드래그: 이동\n휠: 확대/축소",
            Modulate = new Color(1, 1, 1, 0.6f),
        });
    }

    /// <summary>테스트용: 버튼을 누른 것과 같은 효과로 도구를 선택한다.</summary>
    public void EmulateToolSelection(EditorTool tool)
    {
        _baseTool = tool;
        ToolChanged?.Invoke(CurrentTool);
    }

    public void SetLayer(LayerId layer) => _layerButtons[layer].ButtonPressed = true;

    public void SetInfo(string text) => _info.Text = text;

    public void SetStatus(string text) => _status.Text = text;

    public void SetHistoryState(bool canUndo, bool canRedo)
    {
        _undoButton.Disabled = !canUndo;
        _redoButton.Disabled = !canRedo;
    }

    private void AddTool(GridContainer container, string label, EditorTool tool, bool selected = false)
    {
        var button = ToggleButton(label, _toolGroup);
        button.ButtonPressed = selected;
        button.Pressed += () =>
        {
            _baseTool = tool;
            ToolChanged?.Invoke(CurrentTool);
        };
        container.AddChild(button);
    }

    private static Label Header(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        return label;
    }

    private static Button ToggleButton(string text, ButtonGroup group) => new()
    {
        Text = text,
        ToggleMode = true,
        FocusMode = FocusModeEnum.None,
        ButtonGroup = group,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };

    private static Button ActionButton(string text, Action onPressed)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        button.Pressed += onPressed;
        return button;
    }
}
