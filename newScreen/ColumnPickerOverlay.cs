namespace NewScreen;

/// <summary>
/// 화면 전체를 덮는 반투명 오버레이. 레인 1→4를 차례로 클릭해 각 레인의 x좌표를 잡고,
/// 마지막으로 <b>판정선</b>을 클릭해 y좌표를 잡는다. 5번째 클릭 후 자동으로 닫힌다.
/// <para>
/// 판정선은 y좌표만 필요하므로 그 단계에서는 미리보기 선이 <b>가로</b>로 바뀐다.
/// </para>
/// <para>
/// 뒤의 실제 게임 화면이 비쳐 보이도록 옅게만 칠한다(클릭 정확도를 위해). 그만큼 그려지는 선도
/// 흐려지므로 <b>현재 좌표를 숫자로도 같이 표시</b>한다 — 흐릿해도 값은 확실히 읽을 수 있어야 한다.
/// </para>
/// </summary>
public class ColumnPickerOverlay : Form
{
    readonly int[] _xs = new int[Settings.LaneCount];
    int _judgementY = -1;

    /// <summary>0..LaneCount-1 = 레인 선택 중, LaneCount = 판정선 선택 중.</summary>
    int _step;

    Point _cursor;

    readonly Label _hint;

    public bool Completed { get; private set; }

    /// <summary>레인 4개의 x좌표(화면 절대좌표).</summary>
    public int[] SelectedXs => _xs;

    /// <summary>판정선 y좌표(화면 절대좌표).</summary>
    public int SelectedJudgementY => _judgementY;

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

    bool PickingJudgementLine => _step >= Settings.LaneCount;

    void UpdateHint()
    {
        _hint.Text = PickingJudgementLine
            ? $"판정선 높이를 클릭하세요  (y = {_cursor.Y})   (Esc = 취소)"
            : $"레인 {_step + 1}/{Settings.LaneCount} 안쪽을 클릭하세요  (x = {_cursor.X})   (Esc = 취소)";
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
        using var preview = new Pen(Color.Yellow, 3);

        // 이미 확정한 레인들
        for (int i = 0; i < _step && i < Settings.LaneCount; i++)
        {
            int x = PointToClient(new Point(_xs[i], 0)).X;
            g.DrawLine(marker, x, 0, x, Height);
        }

        if (PickingJudgementLine)
            g.DrawLine(preview, 0, _cursor.Y, Width, _cursor.Y);   // 판정선은 가로
        else
            g.DrawLine(preview, _cursor.X, 0, _cursor.X, Height);  // 레인은 세로
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var screen = PointToScreen(e.Location);

        if (!PickingJudgementLine)
        {
            _xs[_step] = screen.X;
            _step++;
            UpdateHint();
            Invalidate();
            return;
        }

        _judgementY = screen.Y;
        Completed = true;
        DialogResult = DialogResult.OK;
        Close();
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
