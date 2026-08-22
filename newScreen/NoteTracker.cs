using System.Threading;

namespace NewScreen;

/// <summary>스트립의 한 행(row)이 이번 틱에 어떤 상태인지.</summary>
public enum RowState : byte
{
    Background = 0,
    Normal = 1,   // 흰색/파란색 계열 — 기존 단노트/롱노트
    Side = 2,     // 빨간색 계열 — 사이드노트(12칼럼 또는 34칼럼을 통째로 덮는 직사각형)
}

/// <summary>
/// 확정된(더 이상 안 보이는) 노트 하나의 요약 정보.
/// <para>
/// <see cref="HitMs"/>는 <b>노트가 판정선에 닿는 시각</b>이다 — 화면에 등장한 시각이 아니다.
/// osu!의 <c>HitObject.StartTime</c>이 곧 "판정선에서 쳐야 하는 시각"이라, 이 기준으로 맞춰야
/// 두 게임의 타이밍이 일치한다. 그 결과 노트가 보이기 시작하는 시점이나 스크롤 속도는 양쪽이
/// 달라 보이는 것이 정상이다 — 일치해야 하는 것은 <b>판정 시각</b>뿐이다.
/// </para>
/// <para>
/// 홀드 종료 시각은 <c>HitMs + LengthPx/VelocityPxPerMs</c>로 구한다(지속시간은 스크롤 속도와 무관).
/// </para>
/// </summary>
public sealed record NoteEvent(
    long HitMs, double VelocityPxPerMs, double LengthPx, string Kind);

/// <summary>
/// osu!로 <b>즉시 쏘기 위한</b> 노트. 노트가 사라질 때까지 기다리는 <see cref="NoteEvent"/>와 달리
/// <b>처음 감지된 순간</b> 만들어진다 — 그러지 않으면 리드타임을 스트립 통과시간만큼 통째로 잃는다.
/// </summary>
/// <param name="Open">
/// true면 아직 길이를 모르는 롱노트다. 일단 아주 길게 깔아두고, 나중에 <see cref="NoteLength"/>로
/// 실제 길이가 확정되면 잘라낸다. 대상 게임의 롱노트는 언제 끝날지 미리 알 수 없기 때문이다.
/// </param>
public sealed record LiveNote(int Id, int Lane, long HitMs, double DurationMs, bool IsSide, bool Open);

/// <summary>앞서 <see cref="LiveNote.Open"/>으로 내보낸 롱노트의 실제 길이가 확정됐다.</summary>
public sealed record NoteLength(int Id, double DurationMs);

/// <summary>
/// 매 틱의 행(row) 상태 배열을 받아, 여러 틱에 걸쳐 같은 런(run)을 추적한다.
/// - 마디선 필터: 런이 살아있는 동안 한 번도 MeasureLineMaxWidthPx를 넘지 못하면 노이즈로 버림.
/// - 매칭은 "직전 틱과 겹치는가"가 아니라 "그동안 추정한 속도로 예측한 위치와 가까운가"로 판단한다.
///   원본 게임이 가끔 프레임을 중복/스킵해서 노트가 한 틱 사이에 예상보다 훨씬 멀리 이동한 것처럼 보여도
///   예측 기반 매칭이면 여전히 같은 노트로 이어붙일 수 있다.
/// - 색상(Normal/Side)은 런이 살아있는 동안 각 상태였던 행 수를 누적해서, 더 많이 나온 쪽으로 다수결 판정한다.
///   Side로 판정되면 길이와 상관없이 무조건 SIDE로 분류하고(사이드노트는 항상 길게 등장하니 어차피 길이도 큼),
///   Normal이면 기존처럼 길이로 TAP/HOLD를 가른다.
/// </summary>
public sealed class NoteTracker
{
    /// <summary>런의 최대 폭이 이 값(px) 이하로만 머물다 사라지면 마디선으로 간주하고 버린다.</summary>
    public const int MeasureLineMaxWidthPx = 4;

    /// <summary>
    /// 같은 틱, 같은 레인 안에서 두 run 사이의 배경(Background) 간격이 이 값(px) 이하면 하나의 run으로 합친다.
    /// 홀드노트 몸통 안의 얇은 장식선/구간표시나 머리·꼬리 캡 경계가 밝기 문턱값 때문에 배경으로 살짝
    /// 끊기면, 하나의 물리적 노트가 여러 개의 run으로 쪼개져서 서로 다른 Track으로(=여러 겹치는
    /// 이벤트로) 추적되는 문제가 있었다. 400px처럼 스트립을 넓히면서 이런 내부 구조가 잡히기 시작해
    /// 표면화됨 — 진짜로 떨어져 있는 별개의 노트라면 이 정도 간격보다 훨씬 크게 떨어져 있을 것이므로
    /// 작은 간격만 안전하게 합친다.
    /// </summary>
    public const int MaxRunMergeGapPx = 5;

    /// <summary>이 값(px) 이상이면 롱노트. 단노트 크기가 환경마다 들쭉날쭉할 수 있으니 여유롭게 잡음.</summary>
    public const int LongNoteMinHeightPx = 90;

    /// <summary>
    /// 아직 속도를 못 잰(샘플 1개짜리) 트랙의 다음 틱 예측에 쓰는 가정 속도(px/ms).
    /// 실기 로그에서 관측된 스크롤 속도가 최대 ~0.7px/ms였던 것에 여유를 두고 잡았다.
    /// 이 값이 없으면(=0으로 가정하면) 캡처 주기가 느릴 때(예: 100ms) 한 틱 사이 실제 이동량이
    /// 고정 허용오차(15px)를 훌쩍 넘어서, 매 틱 새 트랙으로 오판되어 노트 하나가 계속 1틱짜리로 쪼개진다.
    /// </summary>
    const double AssumedBootstrapVelocityPxPerMs = 1.2;

    sealed class Track
    {
        public readonly List<(long Ms, int Start, int End)> History = new();
        public int MaxWidth;
        public bool MatchedThisTick;
        public int NormalVotes, SideVotes;

        /// <summary>실시간 발신용 식별자. 롱노트를 나중에 잘라낼 때 이 값으로 찾는다.</summary>
        public int Id;

        /// <summary>leading edge가 캡처 띠 최상단(y=0)을 지난 시각. 모든 시각 계산의 기준점.</summary>
        public double T0;

        /// <summary>이미 osu!로 내보냈는지.</summary>
        public bool Emitted;

        /// <summary>길이 미정 롱노트로 내보냈는지 — 나중에 잘라줘야 한다.</summary>
        public bool Open;
        double _velocitySum;
        int _velocityCount;

        // 각 경계(위/아래)별로 "마지막으로 값이 실제 바뀌었을 때"의 기준점.
        // 원본 프레임이 중복(값 그대로)되는 틱에서는 이 기준점을 갱신하지 않고 그대로 둔다 —
        // 그래야 다음 실제 변화가 왔을 때, 중복된 틱들을 건너뛴 "진짜 경과시간"으로 델타를 계산하게 된다.
        long? _startRefMs; int _startRefVal;
        long? _endRefMs; int _endRefVal;

        public double VelocityEstimate => _velocityCount > 0 ? _velocitySum / _velocityCount : 0;
        public bool HasVelocityEstimate => _velocityCount > 0;

        public void AppendSample(long ms, int start, int end, int stripHeight, int normalCount, int sideCount)
        {
            NormalVotes += normalCount;
            SideVotes += sideCount;

            if (start > 0)   // 위쪽 경계가 안 잘린 구간(이탈 단계)
            {
                if (_startRefMs.HasValue)
                {
                    if (start != _startRefVal)
                    {
                        double dt = ms - _startRefMs.Value;
                        if (dt > 0) { _velocitySum += (start - _startRefVal) / dt; _velocityCount++; }
                        _startRefMs = ms; _startRefVal = start;
                    }
                }
                else { _startRefMs = ms; _startRefVal = start; }
            }

            if (end < stripHeight - 1)   // 아래쪽 경계가 안 잘린 구간(진입 단계)
            {
                if (_endRefMs.HasValue)
                {
                    if (end != _endRefVal)
                    {
                        double dt = ms - _endRefMs.Value;
                        if (dt > 0) { _velocitySum += (end - _endRefVal) / dt; _velocityCount++; }
                        _endRefMs = ms; _endRefVal = end;
                    }
                }
                else { _endRefMs = ms; _endRefVal = end; }
            }

            History.Add((ms, start, end));
            MaxWidth = Math.Max(MaxWidth, end - start + 1);
        }
    }

    readonly int _stripHeight;
    readonly int _judgementLineY;
    readonly double _overrideVelocity;
    readonly List<Track> _active = new();

    /// <param name="judgementLineY">
    /// 캡처 띠 최상단(y=0)에서 판정선까지의 거리(px). 클라이언트 좌표계에서 띠가 y=0부터 시작하므로
    /// 판정선의 y좌표가 곧 이 거리다.
    /// </param>
    /// <param name="overrideVelocity">
    /// 0보다 크면 노트별 실측 속도 대신 이 값을 쓴다(캘리브레이션으로 구한 전역 속도).
    /// <para>
    /// 판정선 기준으로 시각을 옮기면 <b>속도 오차가 판정선까지의 거리만큼 증폭된다</b>.
    /// 실측 로그에서 노트별 속도를 쓴 결과 12%가 50ms 넘게, 최악은 2.5초까지 어긋났다 —
    /// SV가 없다는 전제에서는 속도가 상수이므로 표본이 많은 전역값이 원리적으로 더 정확하다.
    /// </para>
    /// </param>
    readonly int _lane;
    static int _nextId;

    /// <summary>노트가 <b>처음 확정된 순간</b> 발생 — 즉시 osu!로 쏘기 위한 것.</summary>
    public event Action<LiveNote>? NoteReady;

    /// <summary>길이 미정으로 내보냈던 롱노트의 실제 길이가 확정됐을 때 발생.</summary>
    public event Action<NoteLength>? NoteLengthKnown;

    public NoteTracker(int stripHeight, int judgementLineY, double overrideVelocity = 0, int lane = 0)
    {
        _stripHeight = stripHeight;
        _judgementLineY = judgementLineY;
        _overrideVelocity = overrideVelocity;
        _lane = lane;
    }

    /// <summary>
    /// 실시간 발신은 <b>전역 속도가 있어야만</b> 가능하다. 처음 감지한 순간에는 그 노트의 속도를
    /// 잴 표본이 아직 없어서, 캘리브레이션 값 없이는 판정선 도달 시각을 계산할 수 없다.
    /// </summary>
    bool CanStream => _overrideVelocity > 0;

    /// <summary>
    /// 이번 틱에 새로 알게 된 것을 즉시 내보낸다.
    /// <list type="bullet">
    /// <item>위 경계가 들어왔으면(start &gt; 0) 전체 길이가 확정된 것 — 최종 형태로 내보낸다.</item>
    /// <item>아직 안 들어왔는데 이미 롱노트 길이면 — 끝을 모르는 롱노트로 먼저 내보낸다.</item>
    /// <item>그 외(짧고 아직 진입 중)는 단노트일 수 있으니 판단을 미룬다.</item>
    /// </list>
    /// </summary>
    void Stream(Track t)
    {
        if (!CanStream) return;

        double v = _overrideVelocity;
        var (ms, start, end) = t.History[^1];

        bool topEntered = start > 0;

        // 위 경계가 y=0을 지난 시각 → 그 시점까지가 곧 노트의 지속시간이다.
        // (길이 = v × (topCross - T0). 스트립보다 긴 노트에서도 성립한다.)
        double durationMs = topEntered ? (ms - start / v) - t.T0 : 0;

        if (!t.Emitted)
        {
            bool isSide = t.SideVotes > t.NormalVotes;
            long hitMs = (long)Math.Round(t.T0 + _judgementLineY / v);

            if (topEntered)
            {
                bool isLong = durationMs * v >= LongNoteMinHeightPx;
                t.Emitted = true;
                NoteReady?.Invoke(new LiveNote(t.Id, _lane, hitMs, isLong ? durationMs : 0, isSide, Open: false));
                return;
            }

            // 아직 다 안 들어왔는데 이미 롱노트 길이 → 끝을 모르는 롱노트로 먼저 쏜다.
            if (end + 1 >= LongNoteMinHeightPx)
            {
                t.Emitted = true;
                t.Open = true;
                NoteReady?.Invoke(new LiveNote(t.Id, _lane, hitMs, 0, isSide, Open: true));
            }

            return;
        }

        if (t.Open && topEntered)
        {
            t.Open = false;
            NoteLengthKnown?.Invoke(new NoteLength(t.Id, durationMs));
        }
    }

    /// <summary>이번 틱 결과를 반영하고, 이번 틱에 확정(종료)된 노트 이벤트들을 돌려준다(마디선으로 버려진 건 제외).</summary>
    public List<NoteEvent> Update(long elapsedMs, RowState[] rowStates)
    {
        var unmatchedRuns = FindRuns(rowStates);
        foreach (var t in _active) t.MatchedThisTick = false;

        // 트랙별로 예측 위치를 계산해서 (트랙, run) 후보 쌍을 전부 모은 다음, 오차가 가장 작은 쌍부터
        // 순서대로 확정한다(그리디 최소오차 우선, 전역적으로).
        // 예전에는 _active 리스트 순서대로(=트랙이 생긴 순서대로) 트랙마다 그리디하게 "일단 가까우면
        // 잡고 보는" 방식이었는데, 넓은(폭이 큰) run은 허용오차 판정이 관대해서 이미 화면 밖으로
        // 거의 빠져나간 낡은 트랙이 예측 위치가 한참 벗어났는데도 "근처"로 잘못 판정돼 다른(진짜 그
        // run의 주인인) 새 트랙의 몫을 먼저 가로채는 문제가 있었다. 예: 홀드노트 하나가 거의 다
        // 빠져나갔는데 마침 뒤이어 등장한 다른 홀드노트를 가로채서 늙은 트랙이 계속 이어지고, 정작
        // 새 노트의 진짜 트랙은 시작을 도둑맞아 이벤트가 겹치거나 시각이 틀어졌다.
        // 트랙 등록 순서와 무관하게 "가장 잘 맞는 쌍부터" 배정하면 이 문제가 사라진다.
        var candidates = new List<(int TrackIdx, int RunIdx, double Err)>();
        var predicted = new (double Start, double End)[_active.Count];
        for (int ti = 0; ti < _active.Count; ti++)
        {
            var t = _active[ti];
            var (lastMs, lastStart, lastEnd) = t.History[^1];
            double dt = elapsedMs - lastMs;
            // 속도를 아직 못 잰 트랙(샘플 1개)은 v=0으로 예측하면 안 된다 — 실제로는 이미 화면을 가로질러
            // 이동 중인데 "제자리에 머문다"고 가정하는 셈이라, 느린 캡처 주기에서 다음 틱에 완전히 놓친다.
            bool hasVelocity = t.HasVelocityEstimate;
            double v = hasVelocity ? t.VelocityEstimate : AssumedBootstrapVelocityPxPerMs;
            double predStart = lastStart + v * dt;
            double predEnd = lastEnd + v * dt;
            double tolerance = Math.Max(15.0, Math.Abs(v) * dt);
            predicted[ti] = (predStart, predEnd);

            for (int ri = 0; ri < unmatchedRuns.Count; ri++)
            {
                var (rs, re, _, _) = unmatchedRuns[ri];
                bool near = rs <= predEnd + tolerance && re >= predStart - tolerance;
                if (!near) continue;
                double err = Math.Abs(rs - predStart) + Math.Abs(re - predEnd);
                candidates.Add((ti, ri, err));
            }
        }
        candidates.Sort((a, b) => a.Err.CompareTo(b.Err));

        var trackDone = new bool[_active.Count];
        var runTaken = new bool[unmatchedRuns.Count];
        var matchedRunIdx = new int[_active.Count];
        for (int i = 0; i < matchedRunIdx.Length; i++) matchedRunIdx[i] = -1;
        foreach (var (ti, ri, _) in candidates)
        {
            if (trackDone[ti] || runTaken[ri]) continue;
            trackDone[ti] = true;
            runTaken[ri] = true;
            matchedRunIdx[ti] = ri;
        }

        for (int ti = 0; ti < _active.Count; ti++)
        {
            if (matchedRunIdx[ti] < 0) continue;
            var (rs, re, nc, sc) = unmatchedRuns[matchedRunIdx[ti]];
            _active[ti].AppendSample(elapsedMs, rs, re, _stripHeight, nc, sc);
            _active[ti].MatchedThisTick = true;
            Stream(_active[ti]);
        }

        var leftoverRuns = new List<(int Start, int End, int NormalCount, int SideCount)>();
        for (int ri = 0; ri < unmatchedRuns.Count; ri++)
            if (!runTaken[ri]) leftoverRuns.Add(unmatchedRuns[ri]);
        unmatchedRuns = leftoverRuns;

        // 어느 트랙에도 안 붙은 run은 새 노트의 시작.
        foreach (var (rs, re, nc, sc) in unmatchedRuns)
        {
            var nt = new Track { Id = Interlocked.Increment(ref _nextId) };
            nt.AppendSample(elapsedMs, rs, re, _stripHeight, nc, sc);
            nt.MatchedThisTick = true;

            // 모든 시각 계산의 기준점 — leading edge가 y=0을 지난 순간을 역산해 둔다.
            if (CanStream) nt.T0 = elapsedMs - re / _overrideVelocity;

            _active.Add(nt);
            Stream(nt);
        }

        var finished = new List<NoteEvent>();
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var t = _active[i];
            if (t.MatchedThisTick) continue;   // 이번 틱에도 살아있음 → 계속 추적

            _active.RemoveAt(i);
            var ev = Finalize(t);
            if (ev != null) finished.Add(ev);
        }
        return finished;
    }

    /// <summary>세션 종료 시, 아직 안 끝난(화면에 남아있는) 추적 중인 노트도 강제로 확정한다.</summary>
    public List<NoteEvent> FlushAll()
    {
        var finished = new List<NoteEvent>();
        foreach (var t in _active)
        {
            var ev = Finalize(t);
            if (ev != null) finished.Add(ev);
        }
        _active.Clear();
        return finished;
    }

    /// <summary>지금 이 순간, 마디선 기준을 넘어선(=진짜 노트로 보이는) 런이 하나라도 진행 중인지.</summary>
    public bool AnyRealNoteActive()
    {
        foreach (var t in _active)
            if (t.MaxWidth >= MeasureLineMaxWidthPx) return true;
        return false;
    }

    NoteEvent? Finalize(Track t)
    {
        if (t.MaxWidth < MeasureLineMaxWidthPx) return null;   // 마디선 노이즈로 버림

        var (firstMs, _, firstEnd) = t.History[0];
        var (lastMs, lastStart, _) = t.History[^1];

        // 전역 속도가 있으면 그것을 쓴다 — 노트별 실측치는 표본이 적어 가끔 크게 튄다.
        double v = _overrideVelocity > 0 ? _overrideVelocity : t.VelocityEstimate;

        long t0, tN;
        if (v > 0)
        {
            // 틱 타임스탬프는 캡처 주기에 맞춰 양자화돼 있어 최대 한 틱만큼 오차가 있다.
            // 속도 + 그 시점의 실측 leading-edge px좌표로 "y=0(등장)/y=stripHeight(퇴장)를 지난
            // 정확한 순간"을 역산하면 캡처 주기와 무관하게 정밀한 타임스탬프를 얻는다.
            // 기준점(0 또는 stripHeight)에 가장 가까운 샘플(첫/마지막)을 써야 외삽 거리가 짧아져서
            // 속도 오차가 시간 오차로 증폭되는 정도가 작다.
            t0 = (long)Math.Round(firstMs - firstEnd / v);
            tN = (long)Math.Round(lastMs + (_stripHeight - lastStart) / v);
        }
        else
        {
            t0 = firstMs;
            tN = lastMs;
        }

        double totalDurationMs = tN - t0;
        double lengthPx = v > 0 ? v * totalDurationMs - _stripHeight : t.MaxWidth;
        if (lengthPx < t.MaxWidth) lengthPx = t.MaxWidth;   // 실측 최대폭보다 작게 나올 순 없음(물리적 하한)

        // t0는 leading edge가 캡처 띠 최상단(y=0)을 지난 시각이다. 거기서 판정선까지 더 내려가는
        // 시간을 더해 "판정선에 닿는 시각"으로 옮긴다 — 이것이 osu! HitObject.StartTime과 같은 기준이다.
        // 속도를 못 잰 트랙(v<=0)은 역산이 불가능하므로 t0를 그대로 둔다.
        long hitMs = v > 0 ? (long)Math.Round(t0 + _judgementLineY / v) : t0;

        bool isSide = t.SideVotes > t.NormalVotes;
        string kind = isSide ? "SIDE" : (lengthPx >= LongNoteMinHeightPx ? "HOLD" : "TAP");
        return new NoteEvent(hitMs, v, lengthPx, kind);
    }

    static List<(int Start, int End, int NormalCount, int SideCount)> FindRuns(RowState[] rowStates)
    {
        var raw = new List<(int Start, int End, int NormalCount, int SideCount)>();
        int i = 0;
        while (i < rowStates.Length)
        {
            if (rowStates[i] == RowState.Background) { i++; continue; }
            int start = i;
            int normal = 0, side = 0;
            while (i < rowStates.Length && rowStates[i] != RowState.Background)
            {
                if (rowStates[i] == RowState.Side) side++; else normal++;
                i++;
            }
            raw.Add((start, i - 1, normal, side));
        }

        // 마디선처럼 얇은(<=MeasureLineMaxWidthPx) run은 매칭 후보에서 아예 제외한다.
        // 예전에는 이런 얇은 run도 일단 트랙으로 만들었다가 Finalize()에서 폭 기준으로 버렸는데,
        // 그 사이에 매칭 알고리즘이 얇은 run을 진짜 노트로 착각해서 실수로 붙잡거나, 반대로 진짜
        // 노트가 붙어야 할 run을 마디선 트랙에게 뺏기는 일이 있었다(홀드노트 도중에 마디선이 스트립을
        // 지나가면서 홀드 하나가 여러 개의 겹치는 이벤트로 쪼개지는 원인이었음). 매칭 이전에 걸러내면
        // 이 간섭 자체가 사라진다.
        // 남은(=얇지 않은) run들 사이의 작은 배경 간격은 하나로 합친다(안티에일리어싱 등으로 생기는 미세한 끊김 방어).
        // 단, 다수결 색상이 다른 run끼리는 절대 합치지 않는다 — 사이드노트와 일반노트는 실제로 배경 간격
        // 없이(또는 몇 px만 두고) 붙어 나올 수 있는데, 색상 구분 없이 간격만 보고 합치면 좁은 쪽 노트가
        // 통째로 다수결에 묻혀 사라지고 남은 쪽 길이도 둘을 합친 값으로 잘못 계산된다(실기 로그로 확인:
        // 같은 색끼리 이 임계값 이내로 붙는 경우는 없었고, 예외는 항상 SIDE-NORMAL 경계였음).
        var runs = new List<(int, int, int, int)>();
        foreach (var r in raw)
        {
            if (r.End - r.Start + 1 <= MeasureLineMaxWidthPx) continue;   // 마디선 후보는 매칭에서 아예 제외

            if (runs.Count > 0)
            {
                var last = runs[^1];
                bool sameColor = (last.Item4 > last.Item3) == (r.SideCount > r.NormalCount);
                if (sameColor && r.Start - last.Item2 - 1 <= MaxRunMergeGapPx)
                {
                    runs[^1] = (last.Item1, r.End, last.Item3 + r.NormalCount, last.Item4 + r.SideCount);
                    continue;
                }
            }
            runs.Add((r.Start, r.End, r.NormalCount, r.SideCount));
        }
        return runs;
    }
}
