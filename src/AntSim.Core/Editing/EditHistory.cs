using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

internal readonly record struct TileChange(LayerId Layer, int X, int Y, TileState Before, TileState After);

internal readonly record struct PassageChange(int X, int Y, bool Before, bool After);

/// <summary>
/// Undo 한 단계. 셀마다 최초 Before와 최종 After만 보관하므로 적용 순서와 무관하게 되돌릴 수 있다.
/// </summary>
internal sealed class EditRecord
{
    private readonly Dictionary<long, int> _tileIndex = new();
    private readonly Dictionary<long, int> _passageIndex = new();

    public List<TileChange> Tiles { get; } = new();
    public List<PassageChange> Passages { get; } = new();

    public bool IsEmpty => Tiles.Count == 0 && Passages.Count == 0;

    public void Merge(in TileChange change)
    {
        long key = ((long)change.Layer << 48) | ((long)(uint)change.Y << 24) | (uint)change.X;
        if (_tileIndex.TryGetValue(key, out int i))
            Tiles[i] = Tiles[i] with { After = change.After };
        else
        {
            _tileIndex.Add(key, Tiles.Count);
            Tiles.Add(change);
        }
    }

    public void Merge(in PassageChange change)
    {
        long key = ((long)change.Y << 32) | (uint)change.X;
        if (_passageIndex.TryGetValue(key, out int i))
            Passages[i] = Passages[i] with { After = change.After };
        else
        {
            _passageIndex.Add(key, Passages.Count);
            Passages.Add(change);
        }
    }

    /// <summary>Before == After가 된 항목을 제거한다(스트로크 안에서 되돌아온 셀).</summary>
    public void Compact()
    {
        Tiles.RemoveAll(c => c.Before == c.After);
        Passages.RemoveAll(c => c.Before == c.After);
    }
}

/// <summary>Undo/Redo 스택. 상한을 넘으면 가장 오래된 기록부터 버린다.</summary>
internal sealed class EditHistory
{
    private readonly LinkedList<EditRecord> _undo = new();
    private readonly Stack<EditRecord> _redo = new();

    public EditHistory(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public void Push(EditRecord record)
    {
        _redo.Clear();
        _undo.AddLast(record);
        while (_undo.Count > Capacity) _undo.RemoveFirst();
    }

    public void ClearRedo() => _redo.Clear();

    public EditRecord? PopUndo()
    {
        if (_undo.Last is null) return null;
        var record = _undo.Last.Value;
        _undo.RemoveLast();
        _redo.Push(record);
        return record;
    }

    public EditRecord? PopRedo()
    {
        if (!_redo.TryPop(out var record)) return null;
        _undo.AddLast(record);
        return record;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
