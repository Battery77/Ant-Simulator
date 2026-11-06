using Godot;

namespace AntSim.App;

/// <summary>
/// 화면 위쪽 가운데(모드 막대 아래)의 비차단 알림. 저장 성공·실패처럼 놓치면 안 되는 결과를 잠깐 보여 주고 사라진다.
/// 확인 버튼이 없고 마우스 입력을 막지 않는다. 새 알림은 이전 알림을 바로 대체한다(겹치거나 쌓이지 않음).
/// </summary>
public partial class Toast : PanelContainer
{
    public const double SuccessSeconds = 2.5;
    public const double ErrorSeconds = 5;

    private static readonly Color SuccessColor = new(0.16f, 0.42f, 0.22f, 0.94f);
    private static readonly Color ErrorColor = new(0.55f, 0.13f, 0.13f, 0.96f);

    private Label _label = null!;
    private StyleBoxFlat _style = null!;
    private Tween? _tween;

    /// <summary>지금 보이는 알림 문구(스모크 테스트용). 숨겨져 있으면 null.</summary>
    public string? CurrentText => Visible ? _label.Text : null;

    /// <summary>지금 보이는 알림이 오류인가(스모크 테스트용).</summary>
    public bool IsError { get; private set; }

    /// <summary>알림을 띄운 횟수(스모크 테스트용).</summary>
    public int ShownCount { get; private set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _style = new StyleBoxFlat { ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 10, ContentMarginBottom = 10 };
        _style.SetCornerRadiusAll(6);
        AddThemeStyleboxOverride("panel", _style);
        _label = new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        _label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(_label);
    }

    public void ShowSuccess(string text) => Show(text, error: false, SuccessSeconds);

    public void ShowError(string text) => Show(text, error: true, ErrorSeconds);

    private void Show(string text, bool error, double seconds)
    {
        _tween?.Kill();
        IsError = error;
        ShownCount++;
        _style.BgColor = error ? ErrorColor : SuccessColor;
        _label.Text = text;
        Modulate = Colors.White;
        Visible = true;
        // 가운데 정렬은 내용 크기가 정해진 뒤 맞춘다.
        ResetSize();
        CallDeferred(MethodName.Center);
        _tween = CreateTween();
        _tween.TweenInterval(seconds);
        _tween.TweenProperty(this, "modulate:a", 0f, 0.4);
        _tween.TweenCallback(Callable.From(() => Visible = false));
    }

    private void Center()
    {
        var viewport = GetViewportRect().Size;
        Position = new Vector2((viewport.X - Size.X) / 2, 52);
    }
}
