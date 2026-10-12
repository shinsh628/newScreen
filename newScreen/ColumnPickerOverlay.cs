namespace NewScreen;

/// <summary>
/// 화면 전체를 덮는 반투명 오버레이. 레인 1→4를 차례로 클릭해 각 레인의 x좌표를 잡고,
/// <b>판정선</b>을 클릭해 y좌표를 잡은 뒤, 마지막으로 <b>캡처 띠의 위쪽 끝 → 아래쪽 끝</b>을 클릭한다.
/// 7번째 클릭 후 자동으로 닫힌다.
/// <para>
/// 판정선과 띠는 y좌표만 필요하므로 그 단계에서는 미리보기 선이 <b>가로</b>로 바뀐다.
/// </para>
/// <para>
/// 띠는 판정선보다 위에 있어야 하고(판정선을 넘으면 판정 이펙트·판정선 자체를 노트로 읽는다) 길이가
/// <see cref="NoteDetector.MinStripHeight"/> 이상이어야 한다. 조건에 안 맞는 위치는 미리보기를 빨갛게
/// 그리고 클릭을 무시한다.
/// </para>
/// <para>
/// 뒤의 실제 게임 화면이 비쳐 보이도록 옅게만 칠한다(클릭 정확도를 위해). 그만큼 그려지는 선도
/// 흐려지므로 <b>현재 좌표를 숫자로도 같이 표시</b>한다 — 흐릿해도 값은 확실히 읽을 수 있어야 한다.
/// </para>
/// </summary>
public class ColumnPickerOverlay : Form
{
    const int JudgementStep = Settings.LaneCount;
    const int StripTopStep = Settings.LaneCount + 1;
    const int StripBottomStep = Settings.LaneCount + 2;

    readonly int[] _xs = new int[Settings.LaneCount];
    int _judgementY = -1;
    int _stripTopY = -1;
    int _stripBottomY = -1;

    /// <summary>0..LaneCount-1 = 레인 선택 중, 이후 판정선 → 띠 위쪽 끝 → 띠 아래쪽 끝.</summary>
    int _step;

    Point _cursor;

    readonly Label _hint;

    public bool Completed { get; private set; }

    /// <summary>레인 4개의 x좌표(화면 절대좌표).</summary>
    public int[] SelectedXs => _xs;

    /// <summary>판정선 y좌표(화면 절대좌표).</summary>
    public int SelectedJudgementY => _judgementY;

    /// <summary>캡처 띠 위쪽 끝 y좌표(화면 절대좌표).</summary>
    public int SelectedStripTopY => _stripTopY;

    /// <summary>캡처 띠 아래쪽 끝 y좌표(화면 절대좌표). 띠 길이 = 이 값 − <see cref="SelectedStripTopY"/>.</summary>
    public int SelectedStripBottomY => _stripBottomY;

    public ColumnPickerOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Lime;
        Opacity = 0.12;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;

        _hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.Black,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Location = new Point(40, 40)
        };
        Controls.Add(_hint);
        UpdateHint();
    }

    bool PickingLanes => _step < JudgementStep;

    /// <summary>
    /// 지금 커서 위치(화면 절대좌표 y)를 이번 단계 값으로 받아도 되는지. 안 되면 이유를 돌려준다.
    /// 띠 위쪽 끝은 아래쪽 끝이 들어갈 자리(판정선 위로 최소 길이)가 남아 있어야 한다.
    /// </summary>
    string? Reject(int screenY)
    {
        if (_step == StripTopStep)
        {
            if (screenY + NoteDetector.MinStripHeight > _judgementY)
                return $"판정선보다 {NoteDetector.MinStripHeight}px 이상 위여야 합니다";
            return null;
        }

        if (_step == StripBottomStep)
        {
            if (screenY > _judgementY) return "판정선보다 위여야 합니다";
            if (screenY - _stripTopY < NoteDetector.MinStripHeight) return $"띠 길이는 {NoteDetector.MinStripHeight}px 이상이어야 합니다";
            return null;
        }

        return null;
    }

    Point CursorScreen => PointToScreen(_cursor);

    void UpdateHint()
    {
        string? error = Reject(CursorScreen.Y);
        string suffix = error != null ? $"   ✕ {error}" : "";

        _hint.Text = _step switch
        {
            < JudgementStep => $"레인 {_step + 1}/{Settings.LaneCount} 안쪽을 클릭하세요  (x = {_cursor.X})   (Esc = 취소)",
            JudgementStep => $"판정선 높이를 클릭하세요  (y = {_cursor.Y})   (Esc = 취소)",
            StripTopStep => $"캡처 띠 위쪽 끝을 클릭하세요  (y = {_cursor.Y})   (Esc = 취소){suffix}",
            _ => $"캡처 띠 아래쪽 끝을 클릭하세요  (y = {_cursor.Y}, 길이 = {CursorScreen.Y - _stripTopY})   (Esc = 취소){suffix}",
        };
        _hint.ForeColor = error != null ? Color.OrangeRed : Color.White;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _cursor = e.Location;
        UpdateHint();
        Invalidate();   // 미리보기 선을 커서를 따라 다시 그린다
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;

        using var marker = new Pen(Color.Red, 2);
        using var preview = new Pen(Reject(CursorScreen.Y) != null ? Color.Red : Color.Yellow, 3);

        // 이미 확정한 레인들
        for (int i = 0; i < _step && i < Settings.LaneCount; i++)
        {
            int x = PointToClient(new Point(_xs[i], 0)).X;
            g.DrawLine(marker, x, 0, x, Height);
        }

        // 이미 확정한 판정선 — 띠를 고를 때 "이보다 위"라는 기준선으로 보인다.
        if (_step > JudgementStep)
        {
            int y = PointToClient(new Point(0, _judgementY)).Y;
            g.DrawLine(marker, 0, y, Width, y);
        }

        if (_step > StripTopStep)
        {
            int top = PointToClient(new Point(0, _stripTopY)).Y;
            g.DrawLine(marker, 0, top, Width, top);
        }

        if (PickingLanes)
            g.DrawLine(preview, _cursor.X, 0, _cursor.X, Height);  // 레인은 세로
        else
            g.DrawLine(preview, 0, _cursor.Y, Width, _cursor.Y);   // 판정선·띠는 가로
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var screen = PointToScreen(e.Location);

        if (PickingLanes)
        {
            _xs[_step] = screen.X;
            Advance();
            return;
        }

        if (Reject(screen.Y) != null) return;   // 조건에 안 맞는 위치는 무시 (힌트에 이유가 떠 있다)

        switch (_step)
        {
            case JudgementStep:
                _judgementY = screen.Y;
                Advance();
                return;

            case StripTopStep:
                _stripTopY = screen.Y;
                Advance();
                return;
        }

        _stripBottomY = screen.Y;
        Completed = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    void Advance()
    {
        _step++;
        UpdateHint();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Completed = false;
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    protected override bool ShowWithoutActivation => false;   // 클릭/키를 직접 받아야 하므로 활성화 필요
}
