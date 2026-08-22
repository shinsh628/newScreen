using System.Globalization;

namespace NewScreen;

/// <summary>
/// 레인 단위로 감지된 노트를 <b>osu! 6키 컬럼</b>으로 옮겨 파이프로 보낸다.
/// <para>
/// 매핑: 레인0~3 → 컬럼 1~4, 왼쪽 사이드(레인0·1 동시) → 컬럼 0, 오른쪽 사이드(레인2·3 동시) → 컬럼 5.
/// </para>
/// <para>
/// 사이드노트는 두 레인에 걸친 하나의 직사각형이라 <b>두 레인이 각각 감지한다</b>. 그대로 보내면
/// 노트가 둘로 늘어나므로 여기서 합친다. 둘은 같은 순간에 등장하므로 <b>같은 틱에 나온 것끼리</b>
/// 짝지으면 되고, 그래서 짝을 기다리는 지연이 붙지 않는다.
/// </para>
/// </summary>
public sealed class NoteSender
{
    /// <summary>레인 → osu! 컬럼(사이드가 아닐 때).</summary>
    static readonly int[] LaneToColumn = { 1, 2, 3, 4 };

    const int LeftSideColumn = 0;
    const int RightSideColumn = 5;

    readonly PatternPipeClient _pipe;

    /// <summary>합쳐진 사이드노트: 흡수된 쪽 id → 대표 id. 나중에 오는 길이 확정을 대표에게 돌린다.</summary>
    readonly Dictionary<int, int> _mergedInto = new();

    /// <summary>이번 틱에 나온 노트들. 틱이 끝날 때 사이드 병합을 하고 한꺼번에 보낸다.</summary>
    readonly List<LiveNote> _pending = new();

    public NoteSender(PatternPipeClient pipe) => _pipe = pipe;

    /// <summary>보낸 노트 수(진단용).</summary>
    public int SentNotes { get; private set; }

    /// <summary>
    /// 시간 원점 동기화. <b>매 틱 보낸다</b> — 캡처 시작과 osu! 모드 진입의 순서가 어느 쪽이든
    /// 상관없게 하려는 것이다. osu!는 원점이 아직 없을 때만 이 값을 쓴다.
    /// </summary>
    public void Sync(long elapsedMs) =>
        _pipe.Send($"pc:sync:{elapsedMs.ToString(CultureInfo.InvariantCulture)}");

    public void End() => _pipe.Send("pc:stop");

    /// <summary>틱 중에 감지된 노트를 모아둔다(아직 보내지 않는다).</summary>
    public void Queue(LiveNote note) => _pending.Add(note);

    /// <summary>
    /// 길이가 확정된 롱노트. 사이드로 합쳐진 노트라면 대표 id로 바꿔 보낸다.
    /// </summary>
    public void SendLength(NoteLength length)
    {
        int id = _mergedInto.TryGetValue(length.Id, out int primary) ? primary : length.Id;
        _pipe.Send($"pc:cut:{id}:{F(length.DurationMs)}");
    }

    /// <summary>틱이 끝날 때 호출 — 사이드 병합 후 전송한다.</summary>
    public void Flush()
    {
        if (_pending.Count == 0) return;

        MergeSide(0, 1, LeftSideColumn);
        MergeSide(2, 3, RightSideColumn);

        foreach (var n in _pending)
        {
            int column = n.IsSide
                ? (n.Lane <= 1 ? LeftSideColumn : RightSideColumn)
                : LaneToColumn[n.Lane];

            _pipe.Send(n.Open
                ? $"pc:hold:{n.Id}:{column}:{n.HitMs}"
                : $"pc:note:{n.Id}:{column}:{n.HitMs}:{F(n.DurationMs)}");

            SentNotes++;
        }

        _pending.Clear();
    }

    /// <summary>
    /// 두 레인이 같은 틱에 동시에 SIDE를 냈으면 하나로 합친다. 흡수된 쪽은 보내지 않고,
    /// 그 id가 나중에 길이 확정을 보내오면 대표 id로 돌려준다.
    /// </summary>
    void MergeSide(int laneA, int laneB, int column)
    {
        var a = _pending.FindIndex(n => n.Lane == laneA && n.IsSide);
        var b = _pending.FindIndex(n => n.Lane == laneB && n.IsSide);

        if (a < 0 || b < 0) return;

        // 길이를 아직 모르는 쪽(Open)이 하나라도 있으면 대표도 Open이어야 자를 기회가 남는다.
        var primary = _pending[a];
        var absorbed = _pending[b];

        if (absorbed.Open && !primary.Open)
            (primary, absorbed) = (absorbed, primary);

        _mergedInto[absorbed.Id] = primary.Id;

        _pending.Remove(absorbed);
    }

    static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
