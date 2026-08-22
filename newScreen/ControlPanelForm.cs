namespace NewScreen;

/// <summary>
/// 노트 캡처 감지기 최소 테스트용 제어판.
/// 칼럼 위치 설정(레인 4개) → 캡처 시작 → 레인별 "노트 감지"/"노트 없음" 만 실시간으로 보여준다.
/// 단축키 편집/항상 위에 표시는 기존 ScreenEditor/ControlPanelForm.cs 패턴을 그대로 가져옴.
/// </summary>
public class ControlPanelForm : Form
{
    readonly Settings _settings;
    NoteDetector? _detector;
    OsuOverlayForm? _overlay;
    PatternPipeClient? _pipe;

    readonly Button _btnPickWindow = new();
    readonly Button _btnSetColumn = new();
    readonly Button _btnCalibrate = new();
    readonly Button _btnCapture = new();
    readonly Button _btnQuit = new();
    readonly Button _btnEditHk = new();

    readonly Label _lblWindow = new();
    readonly Label _lblColumn = new();
    readonly Label _lblVelocity = new();
    readonly Label _lblPipe = new();

    /// <summary>파이프 상태를 주기적으로 갱신 — 노트가 안 넘어갈 때 어디서 막혔는지 보이게 한다.</summary>
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 500 };

    NoteSender? _sender;

    /// <summary>캘리브레이션 중 모은 속도 표본. 중앙값을 최종값으로 쓴다(이상치에 강함).</summary>
    readonly List<double> _velocitySamples = new();
    bool _calibrating;
    readonly Label[] _laneStatus = new Label[Settings.LaneCount];

    sealed class Act
    {
        public required string Id;
        public required int HkId;
        public required Button Btn;
        public required Action Run;
        public required Func<string> Name;
    }

    readonly List<Act> _acts = new();
    bool _editingHotkeys;
    string? _capturingId;

    public ControlPanelForm()
    {
        _settings = Settings.Load();

        Text = "NewScreen — 노트 캡처 테스트";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = _settings.AlwaysOnTop;
        MaximizeBox = false;
        ShowInTaskbar = true;
        KeyPreview = true;
        ClientSize = new Size(260, 677);

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - ClientSize.Width - 16, wa.Top + 16);

        int y = 12;
        Place(_lblWindow, ref y, 22);
        _lblWindow.TextAlign = ContentAlignment.MiddleLeft;
        _lblWindow.AutoEllipsis = true;

        Place(_lblColumn, ref y, 22);
        _lblColumn.TextAlign = ContentAlignment.MiddleLeft;

        Place(_lblVelocity, ref y, 22);
        _lblVelocity.TextAlign = ContentAlignment.MiddleLeft;

        Place(_lblPipe, ref y, 22);
        _lblPipe.TextAlign = ContentAlignment.MiddleLeft;
        _lblPipe.AutoEllipsis = true;

        _statusTimer.Tick += (_, _) => UpdatePipeLabel();
        _statusTimer.Start();

        Place(_btnPickWindow, ref y, 38);
        Place(_btnSetColumn, ref y, 38);
        Place(_btnCalibrate, ref y, 38);
        Place(_btnCapture, ref y, 38);
        Place(_btnEditHk, ref y, 38);
        Place(_btnQuit, ref y, 38);

        _btnQuit.BackColor = Color.FromArgb(180, 40, 40);
        _btnQuit.ForeColor = Color.White;
        _btnQuit.FlatStyle = FlatStyle.Flat;

        var chkTop = new CheckBox { Text = "항상 위에 표시", Checked = _settings.AlwaysOnTop };
        chkTop.SetBounds(12, y, 236, 24); y += 26;
        Controls.Add(chkTop);
        chkTop.CheckedChanged += (_, _) =>
        {
            _settings.AlwaysOnTop = chkTop.Checked;
            TopMost = chkTop.Checked;
            if (chkTop.Checked) Front();
        };

        var chkSend = new CheckBox { Text = "osu!로 노트 실시간 전송", Checked = _settings.SendNotesToOsu };
        chkSend.SetBounds(12, y, 236, 24); y += 26;
        Controls.Add(chkSend);
        chkSend.CheckedChanged += (_, _) =>
        {
            _settings.SendNotesToOsu = chkSend.Checked;
            UpdateLabels();
        };

        var chkOverlay = new CheckBox { Text = "캡처 중 osu! 화면 덮기", Checked = _settings.ShowOsuOverlay };
        chkOverlay.SetBounds(12, y, 236, 24); y += 26;
        Controls.Add(chkOverlay);
        chkOverlay.CheckedChanged += (_, _) =>
        {
            _settings.ShowOsuOverlay = chkOverlay.Checked;

            // 캡처 중이면 즉시 반영한다 — 끄고 켜보며 확인할 수 있어야 한다.
            if (_detector == null) return;
            if (chkOverlay.Checked) StartOverlay();
            else StopOverlay();
        };

        var chkOsz = new CheckBox { Text = "캡처 종료 시 osz 자동 생성", Checked = _settings.GenerateOsz };
        chkOsz.SetBounds(12, y, 236, 24); y += 26;
        Controls.Add(chkOsz);
        chkOsz.CheckedChanged += (_, _) => _settings.GenerateOsz = chkOsz.Checked;

        var chkExcludeSide = new CheckBox { Text = "osz에서 사이드 배제(4K)", Checked = _settings.ExcludeSideNotesFromOsz };
        chkExcludeSide.SetBounds(12, y, 236, 24); y += 26;
        Controls.Add(chkExcludeSide);
        chkExcludeSide.CheckedChanged += (_, _) => _settings.ExcludeSideNotesFromOsz = chkExcludeSide.Checked;

        // 오버레이 불투명도 — 낮추면 아래의 대상 게임이 비쳐 보인다(두 화면 겹쳐 보기용).
        var lblOpacity = new Label { TextAlign = ContentAlignment.MiddleLeft };
        lblOpacity.SetBounds(12, y, 236, 18); y += 18;
        Controls.Add(lblOpacity);

        var barOpacity = new TrackBar
        {
            Minimum = 10,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 5,
            LargeChange = 10,
            Value = Math.Clamp(_settings.OsuOverlayOpacityPercent, 10, 100),
        };
        barOpacity.SetBounds(8, y, 244, 40); y += 44;
        Controls.Add(barOpacity);

        void applyOpacity()
        {
            lblOpacity.Text = $"osu! 불투명도: {barOpacity.Value}%";
            _settings.OsuOverlayOpacityPercent = barOpacity.Value;
            _overlay?.SetOpacityPercent(barOpacity.Value);
        }

        barOpacity.ValueChanged += (_, _) => applyOpacity();
        applyOpacity();

        // 레인 4개 상태 박스를 가로로 나란히
        int laneW = (236 - 3 * 6) / Settings.LaneCount;
        for (int lane = 0; lane < Settings.LaneCount; lane++)
        {
            var lbl = new Label
            {
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle,
                Text = (lane + 1).ToString()
            };
            lbl.SetBounds(12 + lane * (laneW + 6), y, laneW, 60);
            Controls.Add(lbl);
            _laneStatus[lane] = lbl;
        }
        y += 68;

        Add("pickwindow", 4, _btnPickWindow, ShowWindowPicker, () => "대상 창 선택");
        Add("setcolumn", 1, _btnSetColumn, ShowColumnPicker, () => "칼럼 위치 설정");
        Add("calibrate", 5, _btnCalibrate, ToggleCalibration, () => _calibrating ? "캘리브레이션 종료" : "속도 캘리브레이션");
        Add("capture", 2, _btnCapture, ToggleCapture, () => _detector != null ? "캡처 중지" : "캡처 시작");
        Add("quit", 3, _btnQuit, Close, () => "종료");

        _btnEditHk.Click += (_, _) => ToggleHotkeyEdit();

        UpdateWindowLabel();
        UpdateColumnLabel();
        UpdateVelocityLabel();
        for (int lane = 0; lane < Settings.LaneCount; lane++) SetStatus(lane, null);
        ReRegister();
        UpdateLabels();
    }

    void Place(Control c, ref int y, int height)
    {
        c.SetBounds(12, y, 236, height);
        Controls.Add(c);
        y += height + 8;
    }

    void Add(string id, int hkId, Button btn, Action run, Func<string> name)
    {
        var a = new Act { Id = id, HkId = hkId, Btn = btn, Run = run, Name = name };
        _acts.Add(a);
        btn.Click += (_, _) => OnActionClick(a);
    }

    void OnActionClick(Act a)
    {
        if (_editingHotkeys)
        {
            _capturingId = a.Id;
            UpdateLabels();
            return;
        }
        a.Run();
        Front();
    }

    // ---- 대상 창 선택 ----

    /// <summary>
    /// WGC는 화면 영역이 아니라 특정 창을 캡처하므로 대상 창 지정이 필수다.
    /// 창이 바뀌면 클라이언트 좌표 기준도 바뀌므로 칼럼 설정을 무효화한다.
    /// </summary>
    void ShowWindowPicker()
    {
        StopCapture();

        using var picker = new WindowPicker();
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        bool changed = _settings.TargetWindowTitle != picker.SelectedTitle;
        _settings.TargetWindowTitle = picker.SelectedTitle;

        if (changed)
        {
            _settings.ClientColumnXs = new int[Settings.LaneCount] { -1, -1, -1, -1 };
            _settings.ClientJudgementLineY = -1;
        }

        _settings.Save();
        UpdateWindowLabel();
        UpdateColumnLabel();
        UpdateLabels();
    }

    /// <summary>저장된 제목으로 대상 창을 다시 찾는다. 못 찾으면 IntPtr.Zero.</summary>
    IntPtr FindTargetWindow()
    {
        if (!_settings.TargetWindowSet) return IntPtr.Zero;
        var h = Native.FindWindow(null, _settings.TargetWindowTitle);
        return h != IntPtr.Zero && Native.IsWindow(h) ? h : IntPtr.Zero;
    }

    void UpdateWindowLabel()
    {
        _lblWindow.Text = _settings.TargetWindowSet
            ? $"대상: {_settings.TargetWindowTitle}"
            : "대상 창 미설정";
    }

    // ---- 칼럼 위치 설정 (레인 4개, 연속 클릭) ----

    void ShowColumnPicker()
    {
        StopCapture();   // 좌표가 바뀔 수 있으므로 캡처 중이면 먼저 멈춤

        IntPtr target = FindTargetWindow();
        if (target == IntPtr.Zero)
        {
            MessageBox.Show(this, "먼저 대상 창을 선택하세요. (찾을 수 없으면 게임을 실행한 뒤 다시 선택)", "NewScreen");
            return;
        }

        Hide();   // 제어판이 클릭을 가리지 않도록 잠깐 숨김
        using var picker = new ColumnPickerOverlay();
        var result = picker.ShowDialog();
        Show();

        if (result != DialogResult.OK || !picker.Completed) return;

        // 픽커는 화면 절대좌표를 준다. 저장은 대상 창의 클라이언트 좌표로 한다 —
        // 그래야 나중에 창이 움직여도 다시 찍지 않아도 된다. 변환은 반드시 이 시점에 해야 한다
        // (캡처 시작 시점에 변환하면 그 사이 창이 움직였을 때 어긋난다).
        var xs = new int[Settings.LaneCount];
        for (int i = 0; i < Settings.LaneCount; i++)
        {
            var p = new Native.POINT { X = picker.SelectedXs[i], Y = 0 };
            Native.ScreenToClient(target, ref p);
            xs[i] = p.X;
        }

        var j = new Native.POINT { X = 0, Y = picker.SelectedJudgementY };
        Native.ScreenToClient(target, ref j);

        _settings.ClientColumnXs = xs;
        _settings.ClientJudgementLineY = j.Y;
        _settings.Save();
        UpdateColumnLabel();
        UpdateLabels();
    }

    void UpdateColumnLabel()
    {
        if (!_settings.AllColumnsSet)
        {
            _lblColumn.Text = "칼럼 미설정";
            return;
        }

        string line = _settings.JudgementLineSet ? $" / 판정선 y={_settings.ClientJudgementLineY}" : " / 판정선 미설정";
        _lblColumn.Text = $"칼럼: {string.Join(", ", _settings.ClientColumnXs)}{line}";
    }

    // ---- 속도 캘리브레이션 ----

    /// <summary>
    /// 노트의 스크롤 속도만 재는 모드. osu! 오버레이를 띄우지 않고 로그도 남기지 않는다.
    /// <para>
    /// 판정선까지의 거리(수백 px)를 속도로 나눠 시각을 옮기기 때문에 속도 오차가 그만큼 증폭된다.
    /// 노트별 실측치는 표본이 적어 가끔 크게 튀므로, 표본을 많이 모아 <b>중앙값</b>을 쓴다.
    /// </para>
    /// </summary>
    void ToggleCalibration()
    {
        if (_calibrating) { StopCalibration(save: true); return; }

        if (_detector != null)
        {
            MessageBox.Show(this, "캡처 중에는 캘리브레이션을 할 수 없습니다. 먼저 캡처를 중지하세요.", "NewScreen");
            return;
        }

        if (!_settings.AllColumnsSet)
        {
            MessageBox.Show(this, "먼저 칼럼 위치를 설정하세요.", "NewScreen");
            return;
        }

        IntPtr target = FindTargetWindow();
        if (target == IntPtr.Zero)
        {
            MessageBox.Show(this, "대상 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.", "NewScreen");
            return;
        }

        _velocitySamples.Clear();
        _calibrating = true;

        // 판정선은 이 모드에서 쓰이지 않고(속도만 잰다), 보정 속도도 넘기지 않는다 —
        // 넘기면 측정 대상이 자기 자신이 되어 버린다.
        _detector = new NoteDetector(target, _settings.ClientColumnXs, 0, calibratedVelocity: 0, calibrationOnly: true);
        _detector.LaneStateChanged += OnLaneStateChanged;
        _detector.Failed += OnCaptureFailed;
        _detector.VelocityMeasured += OnVelocityMeasured;
        _detector.Start();

        for (int lane = 0; lane < Settings.LaneCount; lane++) SetStatus(lane, false);
        UpdateVelocityLabel();
        UpdateLabels();
    }

    void StopCalibration(bool save)
    {
        if (!_calibrating) return;

        _calibrating = false;
        StopCapture();

        if (save && _velocitySamples.Count > 0)
        {
            _settings.CalibratedVelocityPxPerMs = Median(_velocitySamples);
            _settings.Save();

            MessageBox.Show(this,
                $"속도 = {_settings.CalibratedVelocityPxPerMs:F4} px/ms\n" +
                $"표본 {_velocitySamples.Count}개 (중앙값)\n" +
                $"±5% 안에 든 표본: {WithinTolerance(_velocitySamples, _settings.CalibratedVelocityPxPerMs, 0.05)}개",
                "캘리브레이션 완료");
        }
        else if (save)
        {
            MessageBox.Show(this, "측정된 노트가 없습니다. 노트가 흐르는 동안 다시 시도하세요.", "NewScreen");
        }

        UpdateVelocityLabel();
        UpdateLabels();
    }

    void OnVelocityMeasured(double v)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<double>(OnVelocityMeasured), v); return; }

        _velocitySamples.Add(v);
        UpdateVelocityLabel();
    }

    static double Median(List<double> values)
    {
        var s = new List<double>(values);
        s.Sort();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }

    static int WithinTolerance(List<double> values, double center, double ratio) =>
        values.Count(v => Math.Abs(v - center) <= center * ratio);

    /// <summary>
    /// 노트가 osu!로 안 넘어갈 때, 어느 단계에서 막혔는지 한 줄로 보여준다.
    /// 미연결이면 파이프 문제, 연결됐는데 보낸 수가 0이면 감지/발신 문제,
    /// 보낸 수가 느는데 osu!에 안 뜨면 Hook 쪽 문제로 좁혀진다.
    /// </summary>
    void UpdatePipeLabel()
    {
        if (IsDisposed) return;

        if (_pipe == null)
        {
            _lblPipe.Text = _settings.SendNotesToOsu
                ? (_settings.VelocityCalibrated ? "osu! 전송: 대기" : "osu! 전송: 캘리브레이션 필요")
                : "osu! 전송: 꺼짐";
            _lblPipe.ForeColor = SystemColors.ControlText;
            return;
        }

        if (_pipe.Connected)
        {
            _lblPipe.Text = $"osu! 연결됨 — 보낸 노트 {_sender?.SentNotes ?? 0}";
            _lblPipe.ForeColor = Color.FromArgb(0, 110, 0);
            return;
        }

        _lblPipe.Text = $"osu! 미연결: {_pipe.LastError ?? "연결 중"}";
        _lblPipe.ForeColor = Color.FromArgb(160, 0, 0);
    }

    void UpdateVelocityLabel()
    {
        if (_calibrating)
        {
            _lblVelocity.Text = _velocitySamples.Count > 0
                ? $"측정 중… 표본 {_velocitySamples.Count}개, 중앙값 {Median(_velocitySamples):F4}"
                : "측정 중… (노트를 기다리는 중)";
            return;
        }

        _lblVelocity.Text = _settings.VelocityCalibrated
            ? $"속도: {_settings.CalibratedVelocityPxPerMs:F4} px/ms"
            : "속도 미측정 (노트별 실측치 사용)";
    }

    // ---- 캡처 시작/중지 ----

    void ToggleCapture()
    {
        if (_detector != null) { StopCapture(); UpdateLabels(); return; }

        if (!_settings.AllColumnsSet || !_settings.JudgementLineSet)
        {
            MessageBox.Show(this, "먼저 칼럼 위치 설정에서 레인 4개와 판정선을 모두 지정하세요.", "NewScreen");
            return;
        }

        IntPtr target = FindTargetWindow();
        if (target == IntPtr.Zero)
        {
            MessageBox.Show(this, "대상 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하고 다시 선택하세요.", "NewScreen");
            return;
        }

        StartCapture(target);
        UpdateLabels();
    }

    void StartCapture(IntPtr target)
    {
        // 오버레이가 대상 게임을 덮어도 캡처는 멀쩡하다 — WGC가 데스크톱 합성 결과가 아니라
        // 대상 창 자체를 읽기 때문이다. 오버레이 실패는 캡처를 막지 않는다.
        if (_settings.ShowOsuOverlay)
            StartOverlay();

        // 실시간 주입은 전역 속도가 있어야 성립한다 — 없으면 로그만 남기고 조용히 넘어간다.
        _sender = null;

        if (_settings.SendNotesToOsu && _settings.VelocityCalibrated)
        {
            _pipe = new PatternPipeClient();
            _sender = new NoteSender(_pipe);
        }

        _detector = new NoteDetector(target, _settings.ClientColumnXs, _settings.ClientJudgementLineY,
                                     _settings.CalibratedVelocityPxPerMs, sender: _sender,
                                     generateOsz: _settings.GenerateOsz,
                                     excludeSideNotes: _settings.ExcludeSideNotesFromOsz);
        _detector.LaneStateChanged += OnLaneStateChanged;
        _detector.Failed += OnCaptureFailed;
        _detector.OszFailed += OnOszFailed;
        _detector.Start();
        for (int lane = 0; lane < Settings.LaneCount; lane++) SetStatus(lane, false);

        Text = "NewScreen — 캡처 중";
    }

    /// <summary>
    /// osu! 화면을 대상 게임 위에 덮는다. 자동 탐색이 실패하면 사용자가 직접 고르게 하고 제목을 기억한다.
    /// </summary>
    void StartOverlay()
    {
        StopOverlay();

        IntPtr osu = OsuOverlayForm.FindOsuWindow();

        // 자동 탐색 실패 시, 전에 사용자가 직접 골라둔 창이 있으면 그것으로.
        if (osu == IntPtr.Zero && !string.IsNullOrWhiteSpace(_settings.OsuWindowTitle))
        {
            var h = Native.FindWindow(null, _settings.OsuWindowTitle);
            if (h != IntPtr.Zero && Native.IsWindow(h)) osu = h;
        }

        if (osu == IntPtr.Zero)
        {
            using var picker = new WindowPicker { Text = "osu! 창 선택" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;   // 취소해도 캡처는 계속한다
            osu = picker.SelectedHandle;
            _settings.OsuWindowTitle = picker.SelectedTitle;
            _settings.Save();
        }

        _overlay = new OsuOverlayForm(osu);
        _overlay.SetOpacityPercent(_settings.OsuOverlayOpacityPercent);
        _overlay.Show();
        Front();   // 제어판은 오버레이 위로 다시 올린다
    }

    void StopOverlay()
    {
        if (_overlay == null) return;
        if (!_overlay.IsDisposed) _overlay.Close();
        _overlay = null;
    }

    void StopCapture()
    {
        StopOverlay();

        if (_detector == null) return;
        _detector.LaneStateChanged -= OnLaneStateChanged;
        _detector.Failed -= OnCaptureFailed;
        _detector.OszFailed -= OnOszFailed;
        _detector.VelocityMeasured -= OnVelocityMeasured;
        _detector.Dispose();
        _detector = null;

        // 감지기가 완전히 멈춘 뒤에 끊는다 — 종료 시 보내는 pc:stop이 나갈 기회를 줘야 한다.
        _pipe?.Dispose();
        _pipe = null;
        _sender = null;
        UpdatePipeLabel();
        for (int lane = 0; lane < Settings.LaneCount; lane++) SetStatus(lane, null);
        Text = "NewScreen — 노트 캡처 테스트";
    }

    void OnCaptureFailed(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<string>(OnCaptureFailed), message); return; }

        StopCapture();
        UpdateLabels();
        MessageBox.Show(this, $"캡처를 시작하지 못했습니다.\n\n{message}", "NewScreen");
    }

    void OnOszFailed(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<string>(OnOszFailed), message); return; }

        MessageBox.Show(this, message, "NewScreen — osz 생성 실패");
    }

    void OnLaneStateChanged(int lane, bool detected)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<int, bool>(OnLaneStateChanged), lane, detected); return; }
        SetStatus(lane, detected);
    }

    void SetStatus(int lane, bool? detected)
    {
        var lbl = _laneStatus[lane];
        string laneName = (lane + 1).ToString();
        if (detected == true)
        {
            lbl.Text = $"{laneName}\n감지";
            lbl.BackColor = Color.FromArgb(60, 160, 60);
            lbl.ForeColor = Color.White;
        }
        else if (detected == false)
        {
            lbl.Text = $"{laneName}\n없음";
            lbl.BackColor = Color.FromArgb(60, 60, 60);
            lbl.ForeColor = Color.White;
        }
        else
        {
            lbl.Text = laneName;
            lbl.BackColor = SystemColors.Control;
            lbl.ForeColor = SystemColors.ControlText;
        }
    }

    // ---- 단축키 편집 (ScreenEditor/ControlPanelForm.cs 패턴 그대로) ----

    void ToggleHotkeyEdit()
    {
        if (!_editingHotkeys)
        {
            _editingHotkeys = true;
            _capturingId = null;
            UnregisterAll();
        }
        else
        {
            _editingHotkeys = false;
            _capturingId = null;
            _settings.Save();
            ReRegister();
        }
        UpdateLabels();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturingId != null)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key is Keys.Menu or Keys.ControlKey or Keys.ShiftKey or Keys.LWin or Keys.RWin)
                return true;
            if (key == Keys.Escape) { _capturingId = null; UpdateLabels(); return true; }
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

    readonly List<int> _registeredHk = new();

    void UnregisterAll()
    {
        if (!IsHandleCreated) return;
        foreach (var id in _registeredHk) Native.UnregisterHotKey(Handle, id);
        _registeredHk.Clear();
    }

    void ReRegister()
    {
        if (!IsHandleCreated) return;
        UnregisterAll();
        foreach (var a in _acts)
        {
            var hk = Hk(a.Id);
            if (hk != null && hk.Vk != 0 && Native.RegisterHotKey(Handle, a.HkId, hk.Mod | Native.MOD_NOREPEAT, hk.Vk))
                _registeredHk.Add(a.HkId);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ReRegister();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            if (!_editingHotkeys)
            {
                int hkId = (int)m.WParam;
                var a = _acts.Find(x => x.HkId == hkId);
                if (a != null) { a.Run(); Front(); }
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ---- 표시 ----

    void Front()
    {
        if (IsDisposed || !_settings.AlwaysOnTop) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void UpdateLabels()
    {
        if (IsDisposed) return;

        foreach (var a in _acts)
        {
            if (_editingHotkeys && _capturingId == a.Id)
                a.Btn.Text = "단축키를 입력해 주세요";
            else
                a.Btn.Text = $"{a.Name()} ({KeyText(Hk(a.Id))})";
        }

        _btnCapture.Enabled = _editingHotkeys || (_settings.CaptureReady && !_calibrating);
        _btnSetColumn.Enabled = _editingHotkeys || (_settings.TargetWindowSet && !_calibrating);
        _btnPickWindow.Enabled = _editingHotkeys || !_calibrating;
        _btnCalibrate.Enabled = _editingHotkeys || (_settings.TargetWindowSet && _settings.AllColumnsSet && _detector == null) || _calibrating;
        _btnCalibrate.BackColor = _calibrating ? Color.FromArgb(160, 110, 60) : SystemColors.Control;
        _btnCalibrate.ForeColor = _calibrating ? Color.White : SystemColors.ControlText;
        _btnCapture.BackColor = _detector != null ? Color.FromArgb(70, 130, 70) : SystemColors.Control;
        _btnCapture.ForeColor = _detector != null ? Color.White : SystemColors.ControlText;

        _btnEditHk.Text = _editingHotkeys ? "단축키 저장" : "단축키 수정";
        _btnEditHk.BackColor = _editingHotkeys ? Color.FromArgb(70, 110, 160) : SystemColors.Control;
        _btnEditHk.ForeColor = _editingHotkeys ? Color.White : SystemColors.ControlText;

        Front();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopCalibration(save: false);
        StopCapture();
        UnregisterAll();
        _settings.Save();
        base.OnFormClosing(e);
    }
}
