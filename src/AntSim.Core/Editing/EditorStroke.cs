using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

/// <summary>
/// 클릭·드래그 편집 세션. 포인터가 이동한 경로를 격자 선으로 보간해 빈틈 없이 칠하고,
/// 한 스트로크 안에서 같은 타일에는 한 번만 적용한다(수량 도구가 제자리에서 반복 증가하지 않도록).
/// 각 타일 편집은 독립적으로 원자 검증되며, 실패한 타일은 건너뛰고 마지막 오류를 보고한다.
/// 스트로크 전체가 하나의 Undo 단계가 된다.
/// </summary>
public sealed class EditorStroke
{
    private readonly MapEditor _editor;
    private readonly HashSet<long> _visited = new();
    private readonly List<TileCoord> _line = new();
    private int _lastX, _lastY;

    private EditorStroke(MapEditor editor, EditorTool tool, LayerId layer)
    {
        _editor = editor;
        Tool = tool;
        Layer = layer;
    }

    public EditorTool Tool { get; }
    public LayerId Layer { get; }
    public int AppliedCount { get; private set; }
    public int FailedCount { get; private set; }
    public WorldError LastError { get; private set; }
    public bool IsOpen { get; private set; }

    public static EditorStroke Begin(MapEditor editor, EditorTool tool, LayerId layer, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var stroke = new EditorStroke(editor, tool, layer) { IsOpen = true };
        editor.BeginStroke();
        stroke._lastX = x;
        stroke._lastY = y;
        stroke.ApplyAt(x, y);
        return stroke;
    }

    public void MoveTo(int x, int y)
    {
        if (!IsOpen) throw new InvalidOperationException("이미 종료된 스트로크입니다.");
        if (x == _lastX && y == _lastY) return;
        _line.Clear();
        GridLine.Rasterize(_lastX, _lastY, x, y, _line);
        for (int i = 1; i < _line.Count; i++) ApplyAt(_line[i].X, _line[i].Y);
        _lastX = x;
        _lastY = y;
    }

    /// <summary>스트로크를 끝내고 Undo 단계로 확정한다. 변경이 있었으면 true.</summary>
    public bool End()
    {
        if (!IsOpen) return false;
        IsOpen = false;
        return _editor.EndStroke();
    }

    private void ApplyAt(int x, int y)
    {
        if (!_editor.World.Config.InBounds(x, y)) return;
        if (!_visited.Add(((long)y << 32) | (uint)x)) return;
        if (!Tool.TryCreateOp(Layer, x, y, out var op)) return;

        var result = _editor.Apply(op);
        if (result.Succeeded)
        {
            if (result.Changed) AppliedCount++;
        }
        else
        {
            FailedCount++;
            LastError = result.Error;
        }
    }
}
