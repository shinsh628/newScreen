namespace NewScreen;

/// <summary>
/// 실행 중인 보이는 최상위 창 목록에서 <b>캡처 대상 게임 창</b>을 고르는 간단한 다이얼로그.
/// (ScreenEditor/WindowPicker.cs를 그대로 가져옴.)
/// <para>
/// WGC는 화면 영역이 아니라 특정 창을 캡처하므로 대상 창 지정이 필수다.
/// </para>
/// </summary>
public class WindowPicker : Form
{
    record WinItem(IntPtr Handle, string Title) { public override string ToString() => Title; }

    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public IntPtr SelectedHandle { get; private set; } = IntPtr.Zero;
    public string SelectedTitle { get; private set; } = "";

    public WindowPicker()
    {
        Text = "캡처 대상 창 선택";
        Width = 480; Height = 400;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        var ok = new Button { Text = "선택", Dock = DockStyle.Bottom, Height = 36 };
        ok.Click += (_, _) => Choose();
        _list.DoubleClick += (_, _) => Choose();

        Controls.Add(_list);
        Controls.Add(ok);
        AcceptButton = ok;

        LoadWindows();
    }

    void LoadWindows()
    {
        var items = new List<WinItem>();
        uint self = (uint)Environment.ProcessId;

        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            string t = Native.GetTitle(h);
            if (string.IsNullOrWhiteSpace(t)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;   // 우리 프로그램(제어판/오버레이) 제외
            items.Add(new WinItem(h, t));
            return true;
        }, IntPtr.Zero);

        foreach (var i in items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(i);
    }

    void Choose()
    {
        if (_list.SelectedItem is WinItem w)
        {
            SelectedHandle = w.Handle;
            SelectedTitle = w.Title;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
