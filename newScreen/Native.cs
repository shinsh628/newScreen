using System.Runtime.InteropServices;
using System.Text;

namespace NewScreen;

/// <summary>Win32 P/Invoke 선언, 구조체, 상수 모음 (ScreenEditor/Native.cs에서 필요한 것만 축소).</summary>
internal static class Native
{
    // ---- 전역 핫키 ----
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ---- 창 열거 (캡처 대상 창 선택용) ----
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // ---- 좌표 변환 (화면 절대좌표 ↔ 대상 창 클라이언트 좌표) ----
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);

    /// <summary>
    /// WGC(<c>CreateForWindow</c>)가 캡처하는 표면의 원점은 <c>GetWindowRect</c>가 아니라
    /// DWM이 아는 <b>확장 프레임 경계</b>다. 클라이언트 좌표를 캡처 프레임 좌표로 옮기려면 이 값이 필요하다.
    /// (테두리 없는 전체화면 게임이면 둘이 같아서 오프셋이 0이 된다.)
    /// </summary>
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // ---- DWM 썸네일 (osu! 화면을 우리 오버레이에 live 합성) ----
    // 표시 전용이다 — 픽셀을 읽을 수는 없다(그건 WGC 담당). osu! 원본 창은 전혀 건드리지 않는다.
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public const int SW_RESTORE = 9;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80;          // Alt-Tab/작업표시줄 미노출
    public const int WS_EX_NOACTIVATE = 0x08000000;    // 표시돼도 포커스를 가져가지 않음
    public const int WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1;               // 마우스를 아래 창으로 통과

    public const int TNP_RECTDESTINATION = 0x1, TNP_RECTSOURCE = 0x2, TNP_OPACITY = 0x4,
                     TNP_VISIBLE = 0x8, TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public static string GetTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 클라이언트 좌표 원점이 캡처 프레임 안에서 어디인지. 실패하면 (0,0) — 테두리 없는 창에서는 그것이 정답이다.
    /// </summary>
    public static POINT GetClientOffsetInCaptureFrame(IntPtr hwnd)
    {
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin))
            return new POINT { X = 0, Y = 0 };

        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
            return new POINT { X = 0, Y = 0 };

        return new POINT { X = origin.X - frame.Left, Y = origin.Y - frame.Top };
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
