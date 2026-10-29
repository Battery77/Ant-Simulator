using Godot;

namespace AntSim.App;

/// <summary>휠 확대/축소(커서 기준), 가운데·오른쪽 버튼 드래그 이동.</summary>
public partial class EditorCamera : Camera2D
{
    private const float MinZoom = 0.1f;
    private const float MaxZoom = 64f;
    private const float ZoomStep = 1.15f;

    /// <summary>맵 전체가 화면에 들어오도록 위치와 배율을 맞춘다.</summary>
    public void FitTo(Rect2 area)
    {
        var viewport = GetViewportRect().Size;
        float zoom = Mathf.Clamp(Mathf.Min(viewport.X / area.Size.X, viewport.Y / area.Size.Y) * 0.9f, MinZoom, MaxZoom);
        Zoom = new Vector2(zoom, zoom);
        Position = area.GetCenter();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wheelUp:
                ZoomAt(wheelUp.Position, ZoomStep);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wheelDown:
                ZoomAt(wheelDown.Position, 1f / ZoomStep);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion motion
                when (motion.ButtonMask & (MouseButtonMask.Middle | MouseButtonMask.Right)) != 0:
                Position -= motion.Relative / Zoom;
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void ZoomAt(Vector2 screenPoint, float factor)
    {
        // 커서 아래 월드 좌표가 확대 전후에 같은 화면 위치에 남도록 카메라를 옮긴다.
        var offset = screenPoint - GetViewportRect().Size / 2f;
        var worldPoint = Position + offset / Zoom;
        float zoom = Mathf.Clamp(Zoom.X * factor, MinZoom, MaxZoom);
        Zoom = new Vector2(zoom, zoom);
        Position = worldPoint - offset / Zoom;
    }
}
