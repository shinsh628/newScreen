namespace ScreenEditor;

/// <summary>
/// 화면 전체를 덮는 최상위 오버레이. DWM 썸네일로 게임 창을 live 합성해
/// zoom/pan 변형을 적용해 보여준다. 평소엔 클릭 통과(입력 씹힘 없음),
/// 편집 모드에서만 마우스/키보드를 직접 받아 화면을 조정한다.
/// </summary>
public class OverlayForm : Form
{
    readonly Settings _settings;
    readonly ReferenceForm _reference;
    readonly List<MaskForm> _masks = new();   // 가림막(검정 사각형) 창들
    bool _maskBorder;
    int _maskBorderThickness;

    IntPtr _thumb = IntPtr.Zero;     // DWM 썸네일 핸들
    IntPtr _gameHwnd;                // 편집 대상 게임 창
    Native.SIZE _srcSize;            // 게임 창 원본 픽셀 크기

    float _zoom;
    float _panX, _panY;              // 화면상 좌상단 오프셋(px)
    bool _editing;
    Point _dragStart;
    float _panStartX, _panStartY;

    readonly List<int> _registeredHk = new();   // 현재 등록된 전역 핫키 id들

    /// <summary>상태(표시 여부/편집 여부)가 바뀔 때마다 발생 — 제어판 버튼 라벨 갱신용.</summary>
    public event Action? StateChanged;
    /// <summary>전역 핫키가 눌렸을 때 발생(인자=핫키 id) — 제어판이 받아 해당 동작 실행.</summary>
    public event Action<int>? HotkeyPressed;

    public bool OverlayVisible => Visible;
    public bool Editing => _editing;

    public OverlayForm(IntPtr gameHwnd, Settings settings)
    {
        _gameHwnd = gameHwnd;
        _settings = settings;
        _zoom = settings.Zoom > 0 ? settings.Zoom : 1f;
        _panX = settings.PanX;
        _panY = settings.PanY;
        _maskBorder = settings.MaskBorder;
        _maskBorderThickness = settings.MaskBorderThickness;

        _reference = new ReferenceForm(settings.RefOpacity);

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;   // 단일 모니터 기준
        BackColor = Color.Black;
        KeyPreview = true;
        Text = "ScreenEditor";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TOOLWINDOW: Alt-Tab/작업표시줄 미노출, NOACTIVATE: 표시돼도 게임의 키보드 포커스를 뺏지 않음
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    // Show() 시 포커스를 가로채지 않음(일반 모드에서 게임 입력 유지). 편집 진입의 명시적 Activate()는 별개.
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _reference.Show();
        _reference.Visible = false;

        // 저장된 가림막 복원 (오버레이가 표시될 때 함께 보이도록, 지금은 숨김으로 생성)
        foreach (var r in _settings.Masks)
            CreateMask(r.ToRectangle(), editable: false, show: false);

        SetClickThrough(true);   // 평소: 입력 통과

        // 전역 핫키는 제어판이 설정값을 보고 ApplyHotkeys로 등록한다(기본값 없음).

        RegisterThumbnail();
    }

    /// <summary>현재 _gameHwnd 로 DWM 썸네일을 (재)등록하고 변형을 반영한다.</summary>
    bool RegisterThumbnail()
    {
        if (_thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(_thumb); _thumb = IntPtr.Zero; }

        int hr = Native.DwmRegisterThumbnail(Handle, _gameHwnd, out _thumb);
        if (hr != 0 || _thumb == IntPtr.Zero)
        {
            MessageBox.Show($"DWM 썸네일 등록 실패 (hr=0x{hr:X}). 대상이 Borderless Windowed인지 확인하세요.");
            return false;
        }
        Native.DwmQueryThumbnailSourceSize(_thumb, out _srcSize);
        if (_srcSize.cx == 0 || _srcSize.cy == 0)
            _srcSize = new Native.SIZE { cx = Bounds.Width, cy = Bounds.Height };

        ApplyTransform();
        return true;
    }

    /// <summary>편집 대상(게임) 창을 다른 창으로 교체 — 썸네일 재등록.</summary>
    public void ChangeTarget(IntPtr hwnd, string title)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
        _gameHwnd = hwnd;
        _settings.GameTitle = title;
        if (RegisterThumbnail())
            _settings.Save();
    }

    /// <summary>전역 핫키를 통째로 다시 등록한다(빈칸=vk 0 은 건너뜀). 제어판이 호출.</summary>
    public void ApplyHotkeys(IEnumerable<(int id, uint mod, uint vk)> keys)
    {
        if (!IsHandleCreated) return;
        foreach (var id in _registeredHk) Native.UnregisterHotKey(Handle, id);
        _registeredHk.Clear();
        foreach (var (id, mod, vk) in keys)
        {
            if (vk == 0) continue;
            if (Native.RegisterHotKey(Handle, id, mod | Native.MOD_NOREPEAT, vk))
                _registeredHk.Add(id);
        }
    }

    /// <summary>
    /// 일반 모드(on)=클릭 통과: 마우스는 WM_NCHITTEST에서 게임으로 흘리고, 키보드 포커스도 안 뺏음(NOACTIVATE).
    /// 편집 모드(off)=오버레이가 직접 입력을 받도록 NOACTIVATE 해제.
    /// </summary>
    void SetClickThrough(bool on)
    {
        var ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
        if (on) ex |= Native.WS_EX_NOACTIVATE;
        else ex &= ~(long)Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
    }

    /// <summary>
    /// 게임 창을 강제로 포그라운드로 만들어 키보드 입력이 게임에 들어가게 한다.
    /// SetForegroundWindow 단독은 OS 포커스 제약으로 자주 무시되므로,
    /// 현재 포그라운드 스레드에 입력 큐를 붙여(AttachThreadInput) 포커스를 확실히 이전한다.
    /// (오버레이는 TopMost라 게임을 BringWindowToTop 해도 시각적으론 여전히 위에 있음.)
    /// </summary>
    void RestoreGameFocus()
    {
        if (_gameHwnd == IntPtr.Zero || !Native.IsWindow(_gameHwnd)) return;

        if (Native.IsIconic(_gameHwnd)) Native.ShowWindow(_gameHwnd, Native.SW_RESTORE);

        IntPtr fg = Native.GetForegroundWindow();
        uint cur = Native.GetCurrentThreadId();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint gameThread = Native.GetWindowThreadProcessId(_gameHwnd, out _);

        if (fgThread != cur) Native.AttachThreadInput(cur, fgThread, true);
        if (gameThread != cur && gameThread != fgThread) Native.AttachThreadInput(cur, gameThread, true);

        Native.BringWindowToTop(_gameHwnd);
        Native.SetForegroundWindow(_gameHwnd);
        Native.SetFocus(_gameHwnd);

        if (gameThread != cur && gameThread != fgThread) Native.AttachThreadInput(cur, gameThread, false);
        if (fgThread != cur) Native.AttachThreadInput(cur, fgThread, false);
    }

    /// <summary>zoom/pan → rcDestination 계산 후 DWM에 반영. 이것이 "화면 변형"의 전부.</summary>
    void ApplyTransform()
    {
        if (_thumb == IntPtr.Zero) return;

        int w = (int)Math.Round(_srcSize.cx * _zoom);
        int h = (int)Math.Round(_srcSize.cy * _zoom);
        var dest = new Native.RECT
        {
            Left = (int)Math.Round(_panX),
            Top = (int)Math.Round(_panY),
            Right = (int)Math.Round(_panX) + w,
            Bottom = (int)Math.Round(_panY) + h
        };

        var p = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.TNP_RECTDESTINATION | Native.TNP_VISIBLE | Native.TNP_OPACITY | Native.TNP_SOURCECLIENTAREAONLY,
            fVisible = true,
            opacity = 255,
            fSourceClientAreaOnly = false,
            rcDestination = dest
        };
        Native.DwmUpdateThumbnailProperties(_thumb, ref p);
    }

    protected override void WndProc(ref Message m)
    {
        // 일반 모드에서는 마우스를 아래(게임)로 통과시킨다 → 휠/드래그/클릭이 오버레이에 안 잡힘.
        if (m.Msg == Native.WM_NCHITTEST && !_editing)
        {
            m.Result = (IntPtr)Native.HTTRANSPARENT;
            return;
        }
        if (m.Msg == Native.WM_HOTKEY)
        {
            HotkeyPressed?.Invoke((int)m.WParam);
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>오버레이 표시/숨김. 숨기면 즉시 바탕화면이 돌아온다.</summary>
    public void ToggleOverlay()
    {
        if (Visible)
        {
            ExitEdit();
            ShowMasks(false);
            Hide();
            _reference.HideReference();
        }
        else
        {
            Show();
            SetClickThrough(true);
            ApplyTransform();
            ShowMasks(true);      // 가림막도 함께 표시(유지된 상태)
            RestoreGameFocus();   // 표시만 하고 게임에 입력 포커스는 유지
        }
        StateChanged?.Invoke();
    }

    void ExitEdit()
    {
        if (!_editing) return;
        _editing = false;
        SetClickThrough(true);
        _reference.HideReference();   // 일반 모드 진입 → 참조 이미지 제거
        SetMasksEditable(false);
        RestoreGameFocus();
    }

    public void ToggleEdit()
    {
        if (!Visible) Show();   // 숨김 상태에서 편집 들어가면 먼저 표시

        _editing = !_editing;
        SetClickThrough(!_editing);
        ShowMasks(true);        // 편집 진입 시 숨김상태였어도 가림막 표시

        if (_editing)
        {
            // 편집 모드: 입력을 직접 받도록 활성화
            TopMost = true;
            BringToFront();
            Activate();
            Focus();
        }
        else
        {
            _reference.HideReference();   // 일반 모드 진입 → 참조 이미지 제거
            RestoreGameFocus();           // 게임으로 키보드 포커스 복귀
        }

        SetMasksEditable(_editing);   // 편집 시 가림막을 오버레이 위로 올리고 조작 가능하게
        StateChanged?.Invoke();
    }

    // ---- 가림막(검정 사각형) 관리 ----

    /// <summary>가림막 추가 — 편집 모드에서만. 화면 중앙에 기본 크기로 생성.</summary>
    public void AddMask()
    {
        if (!_editing) return;
        var s = Bounds;
        var b = new Rectangle(s.Left + s.Width / 2 - 160, s.Top + s.Height / 2 - 90, 320, 180);
        var m = CreateMask(b, editable: true, show: true);
        RaiseMask(m);
    }

    MaskForm CreateMask(Rectangle b, bool editable, bool show)
    {
        var m = new MaskForm(b);
        m.DeleteRequested += RemoveMask;
        m.Editable = editable;
        m.SetTopBorder(_maskBorder, _maskBorderThickness);
        _masks.Add(m);
        if (show) { m.Show(); RaiseMask(m); }
        return m;
    }

    /// <summary>모든 가림막의 상단 흰 테두리 표시/두께 갱신.</summary>
    public void SetMaskBorder(bool on, int thickness)
    {
        _maskBorder = on;
        _maskBorderThickness = thickness;
        foreach (var m in _masks) m.SetTopBorder(on, thickness);
    }

    void RemoveMask(MaskForm m)
    {
        _masks.Remove(m);
        m.DeleteRequested -= RemoveMask;
        m.Dispose();
    }

    void ShowMasks(bool on)
    {
        foreach (var m in _masks)
        {
            m.Visible = on;
            if (on) RaiseMask(m);
        }
    }

    void SetMasksEditable(bool editing)
    {
        foreach (var m in _masks)
        {
            m.Editable = editing;
            if (editing) RaiseMask(m);   // 오버레이 위로 올려 클릭이 가림막에 잡히게
        }
    }

    static void RaiseMask(MaskForm m)
    {
        if (m.IsHandleCreated)
            Native.SetWindowPos(m.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>참조 이미지 추가 (편집 모드 전용) — 유일한 "등장" 트리거.</summary>
    public void AddReference()
    {
        if (!_editing) return;
        _reference.LoadImageDialog();
        if (!_reference.HasImage) return;

        // 순서 중요: 먼저 오버레이로 키보드 포커스를 가져온 뒤(Activate는 오버레이를 z-최상단으로 올림),
        // 마지막에 참조를 그 위로 raise 해야 한다. 반대로 하면 Activate가 참조를 도로 덮어 사라진다.
        Activate(); Focus();
        RaiseReference();
    }

    /// <summary>참조 이미지 제거 — "제거" 트리거(버튼).</summary>
    public void RemoveReference() => _reference.ClearImage();

    void RaiseReference()
    {
        if (_reference.IsHandleCreated)
            Native.SetWindowPos(_reference.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>변형 초기화 (제어판 버튼 / R 키).</summary>
    public void ResetTransform()
    {
        _zoom = 1f; _panX = 0; _panY = 0;
        ApplyTransform();
    }

    /// <summary>제어판이 닫힐 때 호출 — 모든 기능을 정리하고 자신을 파기한다.</summary>
    public void Shutdown()
    {
        Cleanup();
        if (!IsDisposed) Dispose();
    }

    // ---- 편집 모드 입력 (클릭통과 해제 상태에서만 들어옴) ----
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!_editing) return;
        _dragStart = e.Location;
        _panStartX = _panX;
        _panStartY = _panY;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_editing) return;
        if (e.Button == MouseButtons.Left)
        {
            _panX = _panStartX + (e.X - _dragStart.X);
            _panY = _panStartY + (e.Y - _dragStart.Y);
            ApplyTransform();
        }
    }

    // 휠 한 칸당 확대 비율. 기존 1.1(10%)에서 1/4로 완화 → 2.5%.
    const float ZoomStep = 1.025f;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!_editing) return;
        float old = _zoom;
        _zoom *= e.Delta > 0 ? ZoomStep : 1f / ZoomStep;
        _zoom = Math.Clamp(_zoom, 0.1f, 10f);

        // 커서 지점을 고정점으로 (사진 편집기식 확대/축소)
        _panX = e.X - (e.X - _panX) * (_zoom / old);
        _panY = e.Y - (e.Y - _panY) * (_zoom / old);
        ApplyTransform();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_editing) return;
        switch (e.KeyCode)
        {
            case Keys.O:                         // 참조 이미지 추가
                AddReference();
                break;
            case Keys.Oemplus or Keys.Add:       // 참조 이미지 더 진하게
                _reference.AdjustOpacity(+0.05);
                break;
            case Keys.OemMinus or Keys.Subtract: // 참조 이미지 더 투명하게
                _reference.AdjustOpacity(-0.05);
                break;
            case Keys.R:                         // 변형 초기화
                ResetTransform();
                break;
            case Keys.Escape:                    // 편집 모드 종료
                ToggleEdit();
                break;
        }
        e.Handled = true;
    }

    bool _cleaned;

    /// <summary>핫키 해제 · 썸네일 해제 · 설정 저장 · 참조창 정리. 두 번 호출돼도 안전.</summary>
    void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;

        if (IsHandleCreated)
            foreach (var id in _registeredHk) Native.UnregisterHotKey(Handle, id);
        _registeredHk.Clear();
        if (_thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(_thumb); _thumb = IntPtr.Zero; }

        _settings.Zoom = _zoom;
        _settings.PanX = _panX;
        _settings.PanY = _panY;
        _settings.RefOpacity = _reference.Opacity;
        _settings.Masks = _masks.Select(m => new MaskRect(m.MaskBounds)).ToList();
        _settings.Save();

        foreach (var m in _masks)
            if (!m.IsDisposed) m.Dispose();
        _masks.Clear();

        if (!_reference.IsDisposed) _reference.Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Cleanup();
        base.OnFormClosing(e);
    }
}
