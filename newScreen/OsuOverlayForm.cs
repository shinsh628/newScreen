using System.Diagnostics;

namespace NewScreen;

/// <summary>
/// 화면 전체를 덮고 그 위에 <b>osu! 화면을 DWM 썸네일로 비추는</b> 오버레이.
/// <para>
/// osu! 원본 창은 전혀 건드리지 않는다 — 남의 프로세스 창에 확장 스타일을 거는 방식은
/// UIPI 제약이나 SDL의 스타일 리셋에 걸릴 위험이 있어서, 대신 <b>우리 창</b>에 osu!를 비추고
/// 그 창을 우리가 완전히 통제한다 (조상 프로그램 <c>ScreenEditor/OverlayForm.cs</c>에서 검증된 방식).
/// </para>
/// <para>
/// 입력은 통과시킨다 — 마우스는 <c>WM_NCHITTEST</c>에서 아래(대상 게임)로 흘리고,
/// <c>WS_EX_NOACTIVATE</c>로 포커스도 뺏지 않으므로 대상 게임이 계속 키보드를 받는다.
/// </para>
/// <para>
/// 대상 게임을 덮어도 캡처는 멀쩡하다 — <see cref="WindowCapture"/>가 데스크톱 합성 결과가 아니라
/// 대상 창 자체를 WGC로 읽기 때문이다.
/// </para>
/// </summary>
public class OsuOverlayForm : Form
{
    IntPtr _thumb = IntPtr.Zero;
    readonly IntPtr _osuHwnd;
    Native.SIZE _srcSize;

    /// <summary>
    /// 오버레이 전체의 불투명도(%). 낮추면 아래의 대상 게임이 비쳐 보인다 — 두 화면의 노트가
    /// 같은 타이밍에 판정선에 닿는지 겹쳐서 확인할 수 있다.
    /// <para>
    /// 썸네일 자체의 opacity가 아니라 <b>창의 불투명도</b>를 쓴다. 썸네일만 반투명하게 하면
    /// 검은 배경과 섞여 어두워질 뿐 아래 화면이 보이지 않는다.
    /// </para>
    /// </summary>
    public void SetOpacityPercent(int percent)
    {
        Opacity = Math.Clamp(percent, 10, 100) / 100.0;
    }

    public OsuOverlayForm(IntPtr osuHwnd)
    {
        _osuHwnd = osuHwnd;

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        BackColor = Color.Black;
        Text = "NewScreen osu! Overlay";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>표시해도 포커스를 가로채지 않는다 — 대상 게임이 계속 입력을 받아야 한다.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterThumbnail();
    }

    protected override void WndProc(ref Message m)
    {
        // 마우스를 아래(대상 게임)로 통과시킨다. 이 창은 보기만 하는 창이다.
        if (m.Msg == Native.WM_NCHITTEST)
        {
            m.Result = (IntPtr)Native.HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    bool RegisterThumbnail()
    {
        if (_thumb != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }

        // 최소화된 창은 DWM이 갱신하지 않아 썸네일이 멈춘다.
        if (Native.IsIconic(_osuHwnd))
            Native.ShowWindow(_osuHwnd, Native.SW_RESTORE);

        if (Native.DwmRegisterThumbnail(Handle, _osuHwnd, out _thumb) != 0 || _thumb == IntPtr.Zero)
            return false;

        Native.DwmQueryThumbnailSourceSize(_thumb, out _srcSize);
        if (_srcSize.cx <= 0 || _srcSize.cy <= 0)
            _srcSize = new Native.SIZE { cx = Bounds.Width, cy = Bounds.Height };

        ApplyLayout();
        return true;
    }

    /// <summary>화면에 비율을 유지한 채 최대 크기로 맞춘다(레터박스).</summary>
    void ApplyLayout()
    {
        if (_thumb == IntPtr.Zero) return;

        float scale = Math.Min((float)Bounds.Width / _srcSize.cx, (float)Bounds.Height / _srcSize.cy);
        int w = (int)Math.Round(_srcSize.cx * scale);
        int h = (int)Math.Round(_srcSize.cy * scale);
        int left = (Bounds.Width - w) / 2;
        int top = (Bounds.Height - h) / 2;

        var p = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.TNP_RECTDESTINATION | Native.TNP_VISIBLE | Native.TNP_OPACITY | Native.TNP_SOURCECLIENTAREAONLY,
            fVisible = true,
            opacity = 255,
            fSourceClientAreaOnly = false,
            rcDestination = new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h },
        };

        Native.DwmUpdateThumbnailProperties(_thumb, ref p);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_thumb != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
        base.OnFormClosing(e);
    }

    // ---- osu! 창 찾기 ----

    /// <summary>
    /// 실행 중인 osu!의 메인 창을 찾는다. lazer 실행 파일은 <c>osu!.exe</c> 또는 <c>osu.exe</c>이므로
    /// 프로세스명으로 먼저 시도한다. 못 찾으면 <see cref="IntPtr.Zero"/>.
    /// </summary>
    public static IntPtr FindOsuWindow()
    {
        foreach (string name in new[] { "osu!", "osu" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    IntPtr h = proc.MainWindowHandle;
                    if (h != IntPtr.Zero && Native.IsWindow(h) && Native.IsWindowVisible(h))
                        return h;
                }
                catch { /* 접근 불가 프로세스는 무시 */ }
                finally { proc.Dispose(); }
            }
        }

        return IntPtr.Zero;
    }
}
