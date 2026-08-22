namespace ScreenEditor;

/// <summary>
/// 게임 화면을 가리는 검정(#000000) 직사각형. 오버레이/DWM 썸네일 위에 뜨는 독립 최상위 창
/// (참조 이미지와 같은 패턴 — 썸네일은 항상 자기 창 위에 합성되므로 가림막은 별도 창이어야 한다).
/// 편집 모드에서만 드래그=이동 · 가장자리 드래그=크기변경. 일반 모드에선 완전 클릭 통과(조작 불가)하며 그대로 표시.
/// </summary>
public class MaskForm : Form
{
    const int EdgeGrab = 12;   // 가장자리 리사이즈 감지 폭(px)
    const int MinSize = 24;

    bool _editable;
    bool _topBorder;
    int _borderThickness = 3;
    HitZone _zone = HitZone.None;
    bool _dragging;
    Point _grabScreen;
    Rectangle _startBounds;

    enum HitZone { None, Move, L, R, T, B, TL, TR, BL, BR }

    /// <summary>편집 모드에서 우클릭으로 이 가림막을 삭제 요청.</summary>
    public event Action<MaskForm>? DeleteRequested;

    public MaskForm(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = Color.Black;     // #000000
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Text = "Mask";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TOOLWINDOW: Alt-Tab/작업표시줄 미노출. NOACTIVATE: 클릭해도 포커스를 뺏지 않음(게임 키 입력 유지).
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public bool Editable
    {
        get => _editable;
        set
        {
            if (_editable == value) return;
            _editable = value;
            _dragging = false;
            Cursor = Cursors.Default;
            if (IsHandleCreated) Invalidate();
        }
    }

    public Rectangle MaskBounds => Bounds;

    /// <summary>상단에만 흰색(#ffffff) 테두리 표시 여부 · 두께(px).</summary>
    public void SetTopBorder(bool on, int thickness)
    {
        _topBorder = on;
        _borderThickness = Math.Max(1, thickness);
        if (IsHandleCreated) Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        // 일반 모드: 완전 클릭 통과 → 마우스/입력에 절대 안 걸림(게임으로 통과).
        if (m.Msg == Native.WM_NCHITTEST && !_editable)
        {
            m.Result = (IntPtr)Native.HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    HitZone ZoneAt(Point p)
    {
        bool l = p.X <= EdgeGrab, r = p.X >= Width - EdgeGrab;
        bool t = p.Y <= EdgeGrab, b = p.Y >= Height - EdgeGrab;
        if (t && l) return HitZone.TL;
        if (t && r) return HitZone.TR;
        if (b && l) return HitZone.BL;
        if (b && r) return HitZone.BR;
        if (l) return HitZone.L;
        if (r) return HitZone.R;
        if (t) return HitZone.T;
        if (b) return HitZone.B;
        return HitZone.Move;
    }

    static Cursor CursorFor(HitZone z) => z switch
    {
        HitZone.L or HitZone.R => Cursors.SizeWE,
        HitZone.T or HitZone.B => Cursors.SizeNS,
        HitZone.TL or HitZone.BR => Cursors.SizeNWSE,
        HitZone.TR or HitZone.BL => Cursors.SizeNESW,
        _ => Cursors.SizeAll
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!_editable) return;
        if (e.Button == MouseButtons.Right) { DeleteRequested?.Invoke(this); return; }
        if (e.Button != MouseButtons.Left) return;

        _zone = ZoneAt(e.Location);
        _dragging = true;
        _grabScreen = Cursor.Position;
        _startBounds = Bounds;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_editable) return;
        if (!_dragging) { Cursor = CursorFor(ZoneAt(e.Location)); return; }

        Point now = Cursor.Position;
        int dx = now.X - _grabScreen.X, dy = now.Y - _grabScreen.Y;
        var b = _startBounds;
        int L = b.Left, T = b.Top, R = b.Right, B = b.Bottom;

        switch (_zone)
        {
            case HitZone.Move: L += dx; T += dy; R += dx; B += dy; break;
            case HitZone.L: L += dx; break;
            case HitZone.R: R += dx; break;
            case HitZone.T: T += dy; break;
            case HitZone.B: B += dy; break;
            case HitZone.TL: L += dx; T += dy; break;
            case HitZone.TR: R += dx; T += dy; break;
            case HitZone.BL: L += dx; B += dy; break;
            case HitZone.BR: R += dx; B += dy; break;
        }

        if (_zone != HitZone.Move)   // 최소 크기 보장
        {
            if (R - L < MinSize) { if (L != b.Left) L = R - MinSize; else R = L + MinSize; }
            if (B - T < MinSize) { if (T != b.Top) T = B - MinSize; else B = T + MinSize; }
        }
        Bounds = new Rectangle(L, T, R - L, B - T);
    }

    protected override void OnMouseUp(MouseEventArgs e) => _dragging = false;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);   // 순수 #000000

        if (_topBorder)   // 상단에만 흰색 테두리
            e.Graphics.FillRectangle(Brushes.White, 0, 0, Width, Math.Min(_borderThickness, Height));

        if (!_editable) return;

        // 편집 모드 표시(가림막이 어디 있는지/잡을 수 있게): 점선 테두리 + 모서리/변 핸들
        using var pen = new Pen(Color.White) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using var br = new SolidBrush(Color.White);
        foreach (var p in Handles())
            e.Graphics.FillRectangle(br, p.X - 3, p.Y - 3, 6, 6);
    }

    IEnumerable<Point> Handles()
    {
        int mx = Width / 2, my = Height / 2, r = Width - 1, b = Height - 1;
        yield return new Point(0, 0); yield return new Point(mx, 0); yield return new Point(r, 0);
        yield return new Point(0, my); yield return new Point(r, my);
        yield return new Point(0, b); yield return new Point(mx, b); yield return new Point(r, b);
    }
}
