using System.Globalization;
using Godot;

namespace AntSim.App;

/// <summary>
/// 숫자 입력란(SpinBox) 입력 확정·취소 규칙. 패널의 버튼·선택 상자는 포커스를 받지 않고(FocusMode.None) 맵은 Control이 아니어서,
/// 그대로 두면 다른 곳을 눌러도 입력란에 포커스가 남아 값이 확정되지 않고 이후 키 입력이 계속 그 칸으로 들어간다.
/// <list type="bullet">
/// <item>Enter(편집 중) 또는 입력란 밖 클릭: 입력을 확정(<see cref="SpinBox.Apply"/>)하고 포커스를 놓는다. 다른 입력란을 누르면 그 칸이 포커스를 받는다.</item>
/// <item>Escape: 입력 중인 글자를 버리고 기존 값으로 되돌린 뒤 포커스를 놓는다.</item>
/// <item>잘못된 값·빈 문자열은 SpinBox가 기존 값을 유지하고, 범위 밖 값은 최소·최대로 맞춘다(<see cref="Godot.Range"/> 규칙).</item>
/// </list>
/// 확정은 값만 바꾼다. 설정 적용·초기화는 각 버튼을 눌렀을 때만 일어난다(버튼을 누르면 먼저 확정되므로 마지막 입력이 빠지지 않는다).
/// </summary>
internal static class NumberFieldFocus
{
    /// <summary>키보드 포커스를 가진 숫자 입력란(SpinBox 내부 LineEdit의 부모). 없으면 null.</summary>
    public static SpinBox? Focused(Viewport viewport) =>
        viewport.GuiGetFocusOwner() is LineEdit edit && edit.GetParent() is SpinBox box ? box : null;

    /// <summary>입력을 확정하고 포커스를 놓는다. 같은 값이면 ValueChanged가 다시 나가지 않는다.</summary>
    public static void Commit(SpinBox box)
    {
        box.Apply();
        box.GetLineEdit().ReleaseFocus();
    }

    /// <summary>입력 중인 글자를 버리고 현재 값으로 되돌린 뒤 포커스를 놓는다.</summary>
    public static void Cancel(SpinBox box)
    {
        var edit = box.GetLineEdit();
        // 포커스를 놓을 때 SpinBox가 글자를 다시 해석하므로, 먼저 현재 값의 글자로 되돌려 둔다(값 변화 없음).
        edit.Text = box.Value.ToString("R", CultureInfo.InvariantCulture);
        edit.ReleaseFocus();
        box.Apply();
    }
}
