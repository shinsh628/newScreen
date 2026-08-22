namespace ScreenEditor;

/// <summary>
/// 항상 보이는 작은 제어판. 모든 기능을 버튼으로 제공한다.
/// 이 창이 어떤 방식으로 닫히든(X 버튼 · Alt+F4 · 종료 버튼) 오버레이 · 전역 핫키 · 썸네일 · 참조/가림막 창까지
/// 전부 정리하고 프로그램이 완전히 종료된다.
///
/// 단축키 편집: "단축키 수정"을 누르면 편집 모드가 되어, 각 버튼을 누른 뒤 키를 입력하면 그 버튼의 전역 단축키로 저장된다.
/// "단축키 저장"을 누르면 버튼들이 본래 기능을 회복하고 새 단축키로 동작한다.
/// </summary>
public class ControlPanelForm : Form
{
    readonly OverlayForm _overlay;
    readonly Settings _settings;

    // 단축키를 가지는 기능 버튼들
    readonly Button _btnShow = new();
    readonly Button _btnEdit = new();
    readonly Button _btnAddMask = new();
    readonly Button _btnAddRef = new();
    readonly Button _btnRemoveRef = new();
    readonly Button _btnReset = new();
    readonly Button _btnChangeTarget = new();
    readonly Button _btnQuit = new();
    // 단축키를 가지지 않는 메타 버튼
    readonly Button _btnEditHk = new();

    /// <summary>단축키를 가지는 버튼 1개의 정보.</summary>
    sealed class Act
    {
        public required string Id;           // 설정 저장 키 + 식별자
        public required int HkId;            // RegisterHotKey id
        public required Button Btn;
        public required Action Run;          // 본래 기능
        public required Func<string> Name;   // 현재 표시 기본 이름(상태에 따라 동적)
    }

    readonly List<Act> _acts = new();

    bool _editingHotkeys;       // 단축키 편집 모드
    string? _capturingId;       // 지금 키 입력을 기다리는 액션 id

    public ControlPanelForm(OverlayForm overlay, Settings settings)
    {
        _overlay = overlay;
        _settings = settings;

        Text = "ScreenEditor 제어판";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = settings.AlwaysOnTop;
        MaximizeBox = false;
        ShowInTaskbar = true;
        KeyPreview = true;
        ClientSize = new Size(248, 540);

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - ClientSize.Width - 16, wa.Top + 16);

        int y = 12;
        Place(_btnShow, ref y);
        Place(_btnEdit, ref y);
        Place(_btnAddMask, ref y);
        Place(_btnAddRef, ref y);
        Place(_btnRemoveRef, ref y);
        Place(_btnReset, ref y);
        Place(_btnEditHk, ref y);
        Place(_btnChangeTarget, ref y);
        Place(_btnQuit, ref y);

        _btnQuit.BackColor = Color.FromArgb(180, 40, 40);
        _btnQuit.ForeColor = Color.White;
        _btnQuit.FlatStyle = FlatStyle.Flat;

        // 단축키를 가지는 액션 정의 (id, 핫키 id, 버튼, 본래기능, 기본 이름)
        Add("overlay",   1, _btnShow,      () => _overlay.ToggleOverlay(),  () => _overlay.OverlayVisible ? "오버레이 숨김" : "오버레이 표시");
        Add("edit",      2, _btnEdit,      () => _overlay.ToggleEdit(),     () => _overlay.Editing ? "편집 종료" : "편집 시작");
        Add("addmask",   3, _btnAddMask,   () => _overlay.AddMask(),        () => "가림막 추가");
        Add("addref",    4, _btnAddRef,    () => _overlay.AddReference(),   () => "참조 이미지 추가");
        Add("removeref", 5, _btnRemoveRef, () => _overlay.RemoveReference(),() => "참조 이미지 제거");
        Add("reset",     6, _btnReset,     () => _overlay.ResetTransform(), () => "변형 초기화");
        Add("changetgt", 8, _btnChangeTarget, ShowPickerAndChange,         () => "대상 프로그램 변경");
        Add("quit",      7, _btnQuit,      Close,                           () => "종료");

        _btnEditHk.Click += (_, _) => ToggleHotkeyEdit();

        // ---- 옵션: 항상 위에 표시 ----
        var chkTop = new CheckBox { Text = "항상 위에 표시", Checked = _settings.AlwaysOnTop };
        chkTop.SetBounds(12, y, 224, 24); y += 28;
        Controls.Add(chkTop);
        chkTop.CheckedChanged += (_, _) =>
        {
            _settings.AlwaysOnTop = chkTop.Checked;
            TopMost = chkTop.Checked;
            if (chkTop.Checked) Front();
        };

        // ---- 옵션: 가림막 상단 흰 테두리 + 두께 슬라이더 ----
        var chkBorder = new CheckBox { Text = "가림막 테두리", Checked = _settings.MaskBorder };
        chkBorder.SetBounds(12, y, 224, 24); y += 26;
        Controls.Add(chkBorder);

        var lblThick = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        lblThick.SetBounds(12, y, 224, 18); y += 20;
        Controls.Add(lblThick);

        var trk = new TrackBar
        {
            Minimum = 1,
            Maximum = 30,
            TickFrequency = 5,
            Value = Math.Clamp(_settings.MaskBorderThickness, 1, 30)
        };
        trk.SetBounds(8, y, 232, 45); y += 48;
        Controls.Add(trk);

        void ApplyBorder()
        {
            _settings.MaskBorder = chkBorder.Checked;
            _settings.MaskBorderThickness = trk.Value;
            trk.Enabled = chkBorder.Checked;
            lblThick.Text = $"테두리 두께: {trk.Value} px";
            _overlay.SetMaskBorder(chkBorder.Checked, trk.Value);
        }
        chkBorder.CheckedChanged += (_, _) => ApplyBorder();
        trk.ValueChanged += (_, _) => ApplyBorder();
        trk.Enabled = chkBorder.Checked;
        lblThick.Text = $"테두리 두께: {trk.Value} px";

        _overlay.StateChanged += UpdateLabels;
        _overlay.HotkeyPressed += OnHotkeyPressed;

        ReRegister();     // 저장된 단축키 등록(기본값 없음)
        UpdateLabels();
    }

    void Place(Button b, ref int y)
    {
        b.SetBounds(12, y, 224, 38);
        b.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(b);
        y += 44;
    }

    void Add(string id, int hkId, Button btn, Action run, Func<string> name)
    {
        var a = new Act { Id = id, HkId = hkId, Btn = btn, Run = run, Name = name };
        _acts.Add(a);
        btn.Click += (_, _) => OnActionClick(a);
    }

    // ---- 버튼 클릭 ----

    void OnActionClick(Act a)
    {
        if (_editingHotkeys)
        {
            _capturingId = a.Id;     // 이 버튼의 단축키 입력 대기
            UpdateLabels();
            return;
        }
        a.Run();
        Front();
    }

    /// <summary>대상 프로그램 선택창을 띄워, 고른 창으로 편집 대상을 교체한다.</summary>
    void ShowPickerAndChange()
    {
        using var picker = new WindowPicker();
        if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedHandle != IntPtr.Zero)
            _overlay.ChangeTarget(picker.SelectedHandle, picker.SelectedTitle);
    }

    void ToggleHotkeyEdit()
    {
        if (!_editingHotkeys)
        {
            _editingHotkeys = true;
            _capturingId = null;
            _overlay.ApplyHotkeys(Array.Empty<(int, uint, uint)>());   // 입력 키가 기존 핫키에 안 먹히게 임시 해제
        }
        else
        {
            _editingHotkeys = false;
            _capturingId = null;
            _settings.Save();
            ReRegister();            // 새 단축키로 등록
        }
        UpdateLabels();
    }

    // ---- 키 캡처 ----

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturingId != null)
        {
            Keys key = keyData & Keys.KeyCode;
            // 조합키만 눌린 상태는 무시하고 계속 대기
            if (key is Keys.Menu or Keys.ControlKey or Keys.ShiftKey or Keys.LWin or Keys.RWin)
                return true;
            if (key == Keys.Escape) { _capturingId = null; UpdateLabels(); return true; }  // 취소
            CaptureKey(keyData);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void CaptureKey(Keys keyData)
    {
        uint mod = 0;
        if ((keyData & Keys.Control) != 0) mod |= Native.MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) mod |= Native.MOD_ALT;
        if ((keyData & Keys.Shift) != 0) mod |= Native.MOD_SHIFT;
        uint vk = (uint)(keyData & Keys.KeyCode);

        _settings.Hotkeys[_capturingId!] = new HotkeyDef { Mod = mod, Vk = vk };
        _capturingId = null;
        UpdateLabels();
    }

    static string KeyText(HotkeyDef? hk)
    {
        if (hk == null || hk.Vk == 0) return "";
        var parts = new List<string>();
        if ((hk.Mod & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((hk.Mod & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((hk.Mod & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(((Keys)hk.Vk).ToString());
        return string.Join("+", parts);
    }

    HotkeyDef? Hk(string id) => _settings.Hotkeys.TryGetValue(id, out var hk) ? hk : null;

    // ---- 핫키 등록 / 발동 ----

    void ReRegister()
    {
        var list = new List<(int, uint, uint)>();
        foreach (var a in _acts)
        {
            var hk = Hk(a.Id);
            if (hk != null && hk.Vk != 0) list.Add((a.HkId, hk.Mod, hk.Vk));
        }
        _overlay.ApplyHotkeys(list);
    }

    void OnHotkeyPressed(int hkId)
    {
        if (IsDisposed || _editingHotkeys) return;
        var a = _acts.Find(x => x.HkId == hkId);
        if (a == null) return;
        a.Run();
        Front();
    }

    // ---- 표시 ----

    void Front()
    {
        // "항상 위에 표시"가 꺼져 있으면 강제로 끌어올리지 않는다(일반 창처럼 동작).
        if (IsDisposed || !_settings.AlwaysOnTop) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void UpdateLabels()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(UpdateLabels)); return; }

        foreach (var a in _acts)
        {
            if (_editingHotkeys && _capturingId == a.Id)
                a.Btn.Text = "단축키를 입력해 주세요";
            else
                a.Btn.Text = $"{a.Name()} ({KeyText(Hk(a.Id))})";
        }

        _btnEdit.BackColor = _overlay.Editing ? Color.FromArgb(70, 130, 70) : SystemColors.Control;
        _btnEdit.ForeColor = _overlay.Editing ? Color.White : SystemColors.ControlText;

        // 편집 모드 한정 버튼: 단축키 편집 중에는 클릭(=키 지정) 가능하도록 항상 활성화
        bool any = _editingHotkeys;
        _btnAddMask.Enabled = any || _overlay.Editing;
        _btnAddRef.Enabled = any || _overlay.Editing;
        _btnRemoveRef.Enabled = any || _overlay.Editing;

        _btnEditHk.Text = _editingHotkeys ? "단축키 저장" : "단축키 수정";
        _btnEditHk.BackColor = _editingHotkeys ? Color.FromArgb(70, 110, 160) : SystemColors.Control;
        _btnEditHk.ForeColor = _editingHotkeys ? Color.White : SystemColors.ControlText;

        Front();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 제어판이 닫히면 = 프로그램 전체 종료. 모든 기능을 확실히 정리한다.
        _overlay.StateChanged -= UpdateLabels;
        _overlay.HotkeyPressed -= OnHotkeyPressed;
        _overlay.Shutdown();
        base.OnFormClosing(e);
    }
}
