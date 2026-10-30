using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

internal readonly record struct TileChange(LayerId Layer, int X, int Y, TileState Before, TileState After);

internal readonly record struct PassageChange(int X, int Y, bool Before, bool After);

/// <summary>
/// Undo 한 단계. 셀마다 최초 Before와 최종 After만 보관하므로 적용 순서와 무관하게 되돌릴 수 있다.
/// </summary>
internal sealed class EditRecord
{
    // 메모리 상한 계산용 추정치(바이트). 정확한 값이 아니라 상대적인 크기를 제한하기 위한 값이다.
    internal const int TileChangeBytes = 24;
    internal const int PassageChangeBytes = 16;
    internal const int RecordOverheadBytes = 128;

    private Dictionary<long, int>? _tileIndex = new();
    private Dictionary<long, int>? _passageIndex = new();

    public List<TileChange> Tiles { get; } = new();
    public List<PassageChange> Passages { get; } = new();

    public bool IsEmpty => Tiles.Count == 0 && Passages.Count == 0;

    public long EstimatedBytes =>
        RecordOverheadBytes + (long)Tiles.Count * TileChangeBytes + (long)Passages.Count * PassageChangeBytes;

    public void Merge(in TileChange change)
    {
        var index = _tileIndex ?? throw new InvalidOperationException("확정된 기록은 수정할 수 없습니다.");
        long key = ((long)change.Layer << 48) | ((long)(uint)change.Y << 24) | (uint)change.X;
        if (index.TryGetValue(key, out int i))
            Tiles[i] = Tiles[i] with { After = change.After };
        else
        {
            index.Add(key, Tiles.Count);
            Tiles.Add(change);
        }
    }

    public void Merge(in PassageChange change)
    {
        var index = _passageIndex ?? throw new InvalidOperationException("확정된 기록은 수정할 수 없습니다.");
        long key = ((long)change.Y << 32) | (uint)change.X;
        if (index.TryGetValue(key, out int i))
            Passages[i] = Passages[i] with { After = change.After };
        else
        {
            index.Add(key, Passages.Count);
            Passages.Add(change);
        }
    }

    /// <summary>
    /// 기록을 확정한다: Before == After가 된 항목(스트로크 안에서 되돌아온 셀)을 지우고 병합용 색인을 해제한다.
    /// </summary>
    public void Seal()
    {
        Tiles.RemoveAll(c => c.Before == c.After);
        Passages.RemoveAll(c => c.Before == c.After);
        Tiles.TrimExcess();
        Passages.TrimExcess();
        _tileIndex = null;
        _passageIndex = null;
    }
}

/// <summary>
/// Undo/Redo 스택. 단계 수 상한과 추정 메모리 상한을 모두 지키며, 넘으면 가장 오래된 기록부터 버린다.
/// 단, 가장 최근 기록 하나는 메모리 상한을 넘더라도 유지한다(방금 한 편집은 항상 되돌릴 수 있도록).
/// </summary>
internal sealed class EditHistory
{
    private readonly LinkedList<EditRecord> _undo = new();
    private readonly Stack<EditRecord> _redo = new();

    public EditHistory(int capacity, long memoryLimitBytes)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (memoryLimitBytes < 1) throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes));
        Capacity = capacity;
        MemoryLimitBytes = memoryLimitBytes;
    }

    public int Capacity { get; }
    public long MemoryLimitBytes { get; }
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>Undo + Redo 기록의 추정 메모리 합계.</summary>
    public long EstimatedBytes { get; private set; }

    public void Push(EditRecord record)
    {
        record.Seal();
        ClearRedo();
        _undo.AddLast(record);
        EstimatedBytes += record.EstimatedBytes;
        while (_undo.Count > Capacity || (EstimatedBytes > MemoryLimitBytes && _undo.Count > 1))
        {
            EstimatedBytes -= _undo.First!.Value.EstimatedBytes;
            _undo.RemoveFirst();
        }
    }

    public void ClearRedo()
    {
        foreach (var record in _redo) EstimatedBytes -= record.EstimatedBytes;
        _redo.Clear();
    }

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
        EstimatedBytes = 0;
    }
}
