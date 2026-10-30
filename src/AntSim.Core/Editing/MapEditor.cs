using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

/// <summary>
/// 맵 편집의 단일 진입점. 모든 편집은 연산 배치 단위로 원자적으로 검증·적용된다:
/// 배치의 연산을 임시 상태에 차례로 적용해 보고, 하나라도 규칙을 위반하면 월드를 전혀 바꾸지 않는다.
/// 성공한 배치는 Undo 기록이 된다. 스트로크(드래그) 동안의 배치들은 하나의 Undo 단계로 묶인다.
/// </summary>
/// <remarks>스레드 안전하지 않다. 월드를 소유한 스레드에서만 호출해야 한다.</remarks>
public sealed class MapEditor
{
    public const int DefaultHistoryCapacity = 256;

    /// <summary>통로 생성 시 개미집에서 굴착하는 영역의 반경(1 → 3×3).</summary>
    public const int PassageDigRadius = 1;

    private readonly EditHistory _history;
    private readonly List<StagedTile> _stagedTiles = new();
    private readonly Dictionary<long, int> _stagedTileIndex = new();
    private readonly List<StagedPassage> _stagedPassages = new();
    private readonly Dictionary<long, int> _stagedPassageIndex = new();
    private readonly EditOp[] _single = new EditOp[1];
    private EditRecord? _stroke;

    /// <summary>Undo 기록의 기본 메모리 상한(추정치). 타일 변경 1건 ≈ 24바이트 → 약 280만 건.</summary>
    public const long DefaultHistoryMemoryLimitBytes = 64L * 1024 * 1024;

    public MapEditor(World world, int historyCapacity = DefaultHistoryCapacity, long historyMemoryLimitBytes = DefaultHistoryMemoryLimitBytes)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        _history = new EditHistory(historyCapacity, historyMemoryLimitBytes);
    }

    public World World { get; }

    public bool InStroke => _stroke is not null;
    public bool CanUndo => !InStroke && _history.UndoCount > 0;
    public bool CanRedo => !InStroke && _history.RedoCount > 0;
    public int UndoCount => _history.UndoCount;
    public int RedoCount => _history.RedoCount;

    /// <summary>Undo/Redo 기록의 추정 메모리 사용량(바이트).</summary>
    public long HistoryEstimatedBytes => _history.EstimatedBytes;

    /// <summary>단일 연산을 적용한다.</summary>
    public EditResult Apply(in EditOp op)
    {
        _single[0] = op;
        return Apply(_single);
    }

    /// <summary>연산 배치를 원자적으로 적용한다. 전부 성공하거나, 아무것도 바뀌지 않는다.</summary>
    public EditResult Apply(ReadOnlySpan<EditOp> ops)
    {
        ClearStaging();
        for (int i = 0; i < ops.Length; i++)
        {
            var error = Stage(ops[i], i);
            if (error != WorldError.None)
            {
                ClearStaging();
                return EditResult.Fail(error, i);
            }
        }

        // 연산별 검사를 통과했더라도 최종 상태 전체를 규칙으로 한 번 더 검증한다.
        for (int i = 0; i < _stagedTiles.Count; i++)
        {
            var error = World.Rules.Validate(_stagedTiles[i].After);
            if (error != WorldError.None)
            {
                int opIndex = _stagedTiles[i].OpIndex;
                ClearStaging();
                return EditResult.Fail(error, opIndex);
            }
        }

        var record = _stroke ?? new EditRecord();
        bool changed = false;
        foreach (var staged in _stagedTiles)
        {
            if (staged.Before == staged.After) continue;
            World.GetLayer(staged.Layer).SetStateUnchecked(staged.X, staged.Y, staged.After);
            record.Merge(new TileChange(staged.Layer, staged.X, staged.Y, staged.Before, staged.After));
            changed = true;
        }
        foreach (var staged in _stagedPassages)
        {
            if (staged.Before == staged.After) continue;
            World.SetPassageUnchecked(staged.X, staged.Y, staged.After);
            record.Merge(new PassageChange(staged.X, staged.Y, staged.Before, staged.After));
            changed = true;
        }
        ClearStaging();

        if (!changed) return EditResult.NoChange;
        if (_stroke is null) _history.Push(record);
        else _history.ClearRedo();
        return EditResult.Applied;
    }

    /// <summary>드래그 등 연속 편집을 시작한다. <see cref="EndStroke"/>까지의 변경이 하나의 Undo 단계가 된다.</summary>
    public void BeginStroke()
    {
        if (_stroke is not null) throw new InvalidOperationException("이미 스트로크가 진행 중입니다.");
        _stroke = new EditRecord();
    }

    /// <summary>스트로크를 끝낸다. 실제 변경이 있었으면 true.</summary>
    public bool EndStroke()
    {
        var stroke = _stroke ?? throw new InvalidOperationException("진행 중인 스트로크가 없습니다.");
        _stroke = null;
        stroke.Seal();
        if (stroke.IsEmpty) return false;
        _history.Push(stroke);
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var record = _history.PopUndo()!;
        for (int i = record.Passages.Count - 1; i >= 0; i--)
            World.SetPassageUnchecked(record.Passages[i].X, record.Passages[i].Y, record.Passages[i].Before);
        for (int i = record.Tiles.Count - 1; i >= 0; i--)
        {
            var c = record.Tiles[i];
            World.GetLayer(c.Layer).SetStateUnchecked(c.X, c.Y, c.Before);
        }
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        var record = _history.PopRedo()!;
        foreach (var c in record.Tiles) World.GetLayer(c.Layer).SetStateUnchecked(c.X, c.Y, c.After);
        foreach (var c in record.Passages) World.SetPassageUnchecked(c.X, c.Y, c.After);
        return true;
    }

    public void ClearHistory()
    {
        if (InStroke) throw new InvalidOperationException("스트로크 중에는 기록을 지울 수 없습니다.");
        _history.Clear();
    }

    private WorldError Stage(in EditOp op, int opIndex)
    {
        if (!World.Config.InBounds(op.X, op.Y)) return WorldError.OutOfBounds;

        switch (op.Kind)
        {
            case EditOpKind.AddPassage:
                return StageAddPassage(op.X, op.Y, opIndex);
            case EditOpKind.RemovePassage:
                // 연결만 끊는다. 굴착된 공간은 그대로 남는다.
                StagePassage(op.X, op.Y, present: false);
                return WorldError.None;
        }

        if ((uint)op.Layer >= World.LayerCount) return WorldError.InvalidLayer;
        int index = StagedTileIndex(op.Layer, op.X, op.Y);
        var error = Transform(op, _stagedTiles[index].After, out var next);
        if (error != WorldError.None) return error;
        _stagedTiles[index] = _stagedTiles[index] with { After = next, OpIndex = opIndex };
        return WorldError.None;
    }

    /// <summary>
    /// 통로 생성(굴착 포함)을 스테이징한다.
    /// 1) 개미집 3×3 영역이 맵 안에 완전히 들어가야 함 2) 지상 중심에 고체가 없어야 함
    /// 3) 지상 중심이 물이면 흙으로 4) 개미집 3×3의 고체 제거 + 흙 지형(수량 블록은 유지) 5) 통로 연결.
    /// </summary>
    private WorldError StageAddPassage(int x, int y, int opIndex)
    {
        var config = World.Config;
        if (x < PassageDigRadius || y < PassageDigRadius ||
            x + PassageDigRadius >= config.Width || y + PassageDigRadius >= config.Height)
            return WorldError.PassageAreaOutOfBounds;

        int surface = StagedTileIndex(LayerId.Surface, x, y);
        var center = _stagedTiles[surface].After;
        if (center.HasSolid) return WorldError.PassageBlockedBySolid;
        _stagedTiles[surface] = _stagedTiles[surface] with { After = center with { Terrain = Terrain.Dirt }, OpIndex = opIndex };

        for (int dy = -PassageDigRadius; dy <= PassageDigRadius; dy++)
        for (int dx = -PassageDigRadius; dx <= PassageDigRadius; dx++)
        {
            int nest = StagedTileIndex(LayerId.Nest, x + dx, y + dy);
            var state = _stagedTiles[nest].After;
            _stagedTiles[nest] = _stagedTiles[nest] with
            {
                After = state with { Terrain = Terrain.Dirt, Solid = SolidBlock.None },
                OpIndex = opIndex,
            };
        }

        StagePassage(x, y, present: true);
        return WorldError.None;
    }

    private void StagePassage(int x, int y, bool present)
    {
        long key = ((long)y << 32) | (uint)x;
        if (!_stagedPassageIndex.TryGetValue(key, out int index))
        {
            bool before = World.Passages.Contains(x, y);
            index = _stagedPassages.Count;
            _stagedPassageIndex.Add(key, index);
            _stagedPassages.Add(new StagedPassage(x, y, before, before));
        }
        _stagedPassages[index] = _stagedPassages[index] with { After = present };
    }

    private int StagedTileIndex(LayerId layer, int x, int y)
    {
        long key = ((long)layer << 48) | ((long)(uint)y << 24) | (uint)x;
        if (!_stagedTileIndex.TryGetValue(key, out int index))
        {
            var before = World.GetLayer(layer).GetState(x, y);
            index = _stagedTiles.Count;
            _stagedTileIndex.Add(key, index);
            _stagedTiles.Add(new StagedTile(layer, x, y, before, before, 0));
        }
        return index;
    }

    private WorldError Transform(in EditOp op, in TileState current, out TileState next)
    {
        next = current;
        int max = World.Rules.MaxQuantityPerTile;
        switch (op.Kind)
        {
            case EditOpKind.SetTerrain:
                if (!TileEnums.IsDefined(op.Terrain)) return WorldError.InvalidTerrain;
                next = current with { Terrain = op.Terrain };
                return WorldError.None;

            case EditOpKind.PlaceSolid:
                // 고체 블록은 타일을 독점하므로 기존 수량 블록을 제거하고, 물 지형은 흙으로 바꾼다.
                // 기존 고체 블록은 새 블록으로 교체된다.
                if (op.Solid == SolidBlock.None || !TileEnums.IsDefined(op.Solid)) return WorldError.InvalidSolid;
                next = current with { Terrain = Terrain.Dirt, Solid = op.Solid, Food = 0, Larva = 0 };
                return WorldError.None;

            case EditOpKind.RemoveSolid:
                next = current with { Solid = SolidBlock.None };
                return WorldError.None;

            case EditOpKind.ClearBlocks:
                next = current with { Solid = SolidBlock.None, Food = 0, Larva = 0 };
                return WorldError.None;

            case EditOpKind.AdjustQuantity:
            case EditOpKind.SetQuantity:
            {
                if (!TileEnums.IsDefined(op.Quantity)) return WorldError.InvalidQuantityKind;
                long value = op.Kind == EditOpKind.SetQuantity
                    ? op.Amount
                    : (long)current.GetQuantity(op.Quantity) + op.Amount;
                if (value == current.GetQuantity(op.Quantity)) return WorldError.None;
                if (current.HasSolid) return WorldError.SolidOccupied;
                if (value < 0) return WorldError.NegativeQuantity;
                if (current.TotalQuantity - current.GetQuantity(op.Quantity) + value > max) return WorldError.QuantityLimitExceeded;
                next = current.WithQuantity(op.Quantity, (byte)value);
                return WorldError.None;
            }

            default:
                return WorldError.InvalidOperation;
        }
    }

    private void ClearStaging()
    {
        _stagedTiles.Clear();
        _stagedTileIndex.Clear();
        _stagedPassages.Clear();
        _stagedPassageIndex.Clear();
    }

    private readonly record struct StagedTile(LayerId Layer, int X, int Y, TileState Before, TileState After, int OpIndex);

    private readonly record struct StagedPassage(int X, int Y, bool Before, bool After);
}
