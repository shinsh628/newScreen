using System.Diagnostics;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;

namespace NewScreen;

/// <summary>
/// 대상 게임 창의 얇은 세로 띠 4개(레인마다 하나)를 고정 주기로 캡처해서 레인별 노트 유무·색상을 판정하는 감지기.
/// <para>
/// 감지한 노트는 <see cref="NoteSender"/>를 거쳐 osu!로 실시간 전송된다. 캘리브레이션이 아닌 일반 모드에서는
/// 동시에 로그도 남긴다(검증·디버깅용, 실시간 주입 자체는 로그 없이도 동작한다).
/// - 원본(raw) 로그: 매 틱, 레인 4개의 행별 상태(0=배경,1=흰/파랑,2=빨강)를 탭으로 구분해서 그대로.
/// - 가공(events) 로그: 레인마다 별도의 NoteTracker가 마디선을 걸러내고 계산한 속도/길이/TAP·HOLD·SIDE 분류, 시계열.
/// </para>
/// </summary>
public sealed class NoteDetector : IDisposable
{
    public const int LaneCount = Settings.LaneCount;

    // 캡처 띠 폭 (고정값). 띠의 시작 y와 길이는 칼럼 설정에서 정한다(Settings).
    public const int StripWidth = 6;

    /// <summary>띠 길이 기본값 — 1.2.0까지의 고정값. 띠를 지정한 적 없는 설정 파일은 이 값으로 동작한다.</summary>
    public const int DefaultStripHeight = 400;

    /// <summary>
    /// 띠 길이 하한. 100ms 틱이면 노트가 한 틱에 ~100px 움직이므로, 띠가 짧으면 노트가 두 번 찍히기도 전에
    /// 지나가 버려 속도 측정·추적이 깨진다. 롱노트 기준(<see cref="NoteTracker.LongNoteMinHeightPx"/>)보다도
    /// 충분히 길어야 끝 미정 롱노트를 일찍 내보낼 수 있다.
    /// </summary>
    public const int MinStripHeight = 250;

    // "검정이 아니다" 판정 기준 밝기 (0~255). 배경(검정)보다 확실히 밝은 값이면 통과.
    // 배경이 거의 순수 검정이라 여유를 낮게 잡아도 안전하고, 문턱값을 낮출수록 흐릿한/반투명한
    // 노트 가장자리까지 잡아낼 수 있다. 얇은 run(마디선 등)은 매칭 전에 걸러지므로(FindRuns 참고),
    // 문턱값을 낮춰서 생기는 자잘한 노이즈 blob이 늘어나도 매칭을 방해하지 않는다.
    const int BrightnessThreshold = 15;

    // R이 G/B보다 이 값(0~255) 이상 튀면 "빨간색 계열"(사이드노트)로 분류. 흰색은 R≈G≈B, 파란색은 B가 더 큼.
    const int RedDominanceMargin = 25;

    // 목표 캡처 주기 — 띠를 넓히는 대신 주기를 늦춰서 렉으로 인한 프레임 중복/스킵 영향을 줄인다.
    // (위치+타임스탬프 기반 속도 외삽이라 주기 자체는 타이밍 정밀도의 하한이 아님. 대신 노트당 샘플 수가
    //  줄어드는 만큼 노이즈에 조금 더 민감해지므로, 그 트레이드오프는 실기 로그로 검증한다.)
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewScreen", "screenLog");

    readonly int[] _columnXs;
    readonly int _judgementLineY;
    readonly int _stripTopY;
    readonly int _stripHeight;
    readonly IntPtr _targetHwnd;
    readonly bool _calibrationOnly;
    readonly double _calibratedVelocity;
    readonly NoteSender? _sender;
    readonly bool _generateOsz;
    readonly bool _excludeSideNotes;
    Thread? _thread;
    volatile bool _running;

    /// <summary>레인별 노트 감지 상태(마디선 필터 반영됨)가 바뀔 때마다 발생 (백그라운드 스레드 호출 — UI는 BeginInvoke 필요).</summary>
    public event Action<int, bool>? LaneStateChanged;

    /// <summary>캡처를 시작조차 못했을 때 발생 (백그라운드 스레드 호출 — UI는 BeginInvoke 필요).</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// osz 자동 생성이 실패했을 때 발생 (백그라운드 스레드 호출 — UI는 BeginInvoke 필요).
    /// 노트 감지/전송 자체는 이 실패와 무관하게 계속 정상 동작한다.
    /// </summary>
    public event Action<string>? OszFailed;

    /// <summary>
    /// 캘리브레이션 모드에서 노트 하나의 속도가 측정될 때마다 발생 (백그라운드 스레드 호출).
    /// </summary>
    public event Action<double>? VelocityMeasured;

    public string? RawLogFilePath { get; private set; }
    public string? EventsLogFilePath { get; private set; }

    /// <param name="targetHwnd">캡처할 게임 창.</param>
    /// <param name="columnXs">레인 4개의 캡처 띠 중심 x좌표 — <b>대상 창의 클라이언트 좌표</b>.</param>
    /// <param name="judgementLineY">판정선의 y좌표 — <b>대상 창의 클라이언트 좌표</b>.</param>
    /// <param name="stripTopY">캡처 띠 위쪽 끝의 y좌표 — <b>대상 창의 클라이언트 좌표</b>.</param>
    /// <param name="stripHeight">캡처 띠 길이(px).</param>
    /// <param name="calibratedVelocity">
    /// 캘리브레이션으로 구한 스크롤 속도(px/ms). 0보다 크면 노트별 실측 속도 대신 이 값을 쓴다.
    /// 판정선까지의 거리가 멀어 <b>속도 오차가 그 거리만큼 증폭되므로</b>, 표본이 많은 전역값이 훨씬 정확하다.
    /// </param>
    /// <param name="calibrationOnly">
    /// true면 로그를 남기지 않고 <see cref="VelocityMeasured"/>만 발생시킨다(속도 측정 전용 모드).
    /// </param>
    /// <param name="sender">
    /// 주면 감지한 노트를 실시간으로 osu!에 보낸다. null이면 로그만 남긴다.
    /// <b>전역 속도(캘리브레이션)가 있어야 동작한다</b> — 처음 감지한 순간에는 그 노트의 속도를 잴
    /// 표본이 아직 없어서 판정선 도달 시각을 계산할 수 없기 때문이다.
    /// </param>
    /// <param name="generateOsz">
    /// true면 캡처 시작~종료 구간의 대상 창 오디오를 녹음해 두었다가, 종료 시 events.log와 함께
    /// osu! mania 6키 <c>.osz</c>를 만든다. 캘리브레이션 모드에서는 무시된다.
    /// </param>
    /// <param name="excludeSideNotes">
    /// true면 감지 자체는 그대로 하되(사이드도 평소처럼 인식), osz를 만들 때만 SIDE 이벤트를 빼고
    /// 4키(CircleSize=4) 맵으로 만든다.
    /// </param>
    public NoteDetector(IntPtr targetHwnd, int[] columnXs, int judgementLineY, int stripTopY, int stripHeight,
                        double calibratedVelocity = 0, bool calibrationOnly = false, NoteSender? sender = null,
                        bool generateOsz = false, bool excludeSideNotes = false)
    {
        if (columnXs.Length != LaneCount) throw new ArgumentException($"columnXs must have {LaneCount} entries");
        _targetHwnd = targetHwnd;
        _columnXs = columnXs;
        _judgementLineY = judgementLineY;
        _stripTopY = stripTopY;
        _stripHeight = stripHeight;
        _calibratedVelocity = calibratedVelocity;
        _calibrationOnly = calibrationOnly;
        _sender = calibrationOnly ? null : sender;
        _generateOsz = generateOsz;
        _excludeSideNotes = excludeSideNotes;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "NoteDetector" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }

    void CaptureLoop()
    {
        try
        {
            CaptureLoopCore();
        }
        catch (Exception ex)
        {
            _running = false;
            Failed?.Invoke(ex.Message);
        }
    }

    // 최근 (raw,events) 로그 한 쌍만 남기고 지운다 — 매 캡처마다 쌓이면 %appdata%가 무한정 커지므로.
    static void DeleteOldLogs()
    {
        foreach (var path in Directory.EnumerateFiles(LogDir, "capture_*_*.log"))
        {
            try { File.Delete(path); } catch { /* 다른 프로세스가 잡고 있어도 다음 캡처 때 다시 시도하면 되므로 무시 */ }
        }
    }

    void CaptureLoopCore()
    {
        var startTime = DateTime.Now;
        string tag = startTime.ToString("yyyy-MM-dd_HH-mm-ss");
        string? oszOutputPath = null;

        // 캘리브레이션은 속도만 재는 모드라 로그 파일을 아예 만들지 않는다 — 패턴을 기록하지 않는다.
        if (!_calibrationOnly)
        {
            Directory.CreateDirectory(LogDir);
            DeleteOldLogs();
            RawLogFilePath = Path.Combine(LogDir, $"capture_{tag}_raw.log");
            EventsLogFilePath = Path.Combine(LogDir, $"capture_{tag}_events.log");
            oszOutputPath = Path.Combine(LogDir, $"capture_{tag}.osz");
        }

        // 4레인을 각각 따로 캡처하면 순서대로(0→1→2→3) 캡처하는 동안 실제 시각이 벌어지는데도
        // 전부 같은 틱 타임스탬프를 붙이게 돼서, 동시에 등장한 노트인데 레인마다 다른 시각으로
        // 역산되는 문제가 있었다. 4레인 x범위를 통째로 덮는 넓은 영역 하나를 한 번에 캡처하고,
        // 레인별 스캔은 그 안에서 잘라서 하면 애초에 "동시에 찍힌 한 장"이 되어 이 스큐 자체가 생기지 않는다.
        int captureLeft = _columnXs.Min() - StripWidth / 2;
        int captureRight = _columnXs.Max() - StripWidth / 2 + StripWidth;
        int captureWidth = captureRight - captureLeft;
        var laneXOffsets = new int[LaneCount];
        for (int i = 0; i < LaneCount; i++) laneXOffsets[i] = (_columnXs[i] - StripWidth / 2) - captureLeft;

        // 띠가 창 클라이언트 영역을 벗어나면 캡처 프레임 밖을 복사하게 되는데, 이때 D3D11은 에러 없이
        // 복사를 건너뛸 수 있어 "노트가 하나도 안 잡힌다"로만 나타난다 — 시작 전에 명시적으로 막는다.
        // (칼럼을 찍은 뒤 창 크기가 바뀐 경우가 여기 걸린다.)
        if (Native.GetClientRect(_targetHwnd, out var client) &&
            (_stripTopY < 0 || _stripTopY + _stripHeight > client.Bottom - client.Top))
            throw new InvalidOperationException(
                $"캡처 띠(y={_stripTopY}~{_stripTopY + _stripHeight})가 대상 창 영역(높이 {client.Bottom - client.Top})을 벗어납니다. 칼럼 위치를 다시 설정하세요.");

        // 좌표는 대상 창의 클라이언트 기준이다. y=0은 화면 최상단이 아니라 창 클라이언트 영역의 최상단.
        using var capture = new WindowCapture(_targetHwnd, captureLeft, _stripTopY, captureWidth, _stripHeight);

        using var rawLog = _calibrationOnly ? null : new StreamWriter(RawLogFilePath!, append: false) { AutoFlush = false };
        using var evLog = _calibrationOnly ? null : new StreamWriter(EventsLogFilePath!, append: false) { AutoFlush = false };

        string meta = $"clientColumns={string.Join(",", _columnXs)} judgementLineY={_judgementLineY} " +
                      $"stripWidth={StripWidth} stripTopY={_stripTopY} stripHeight={_stripHeight} " +
                      $"capture=WGC clientOffset={capture.ClientOffsetX},{capture.ClientOffsetY} borderHidden={capture.BorderHidden} " +
                      $"tickIntervalMs={TickInterval.TotalMilliseconds:F0} " +
                      $"threshold={BrightnessThreshold} redDominanceMargin={RedDominanceMargin} " +
                      $"measureLineMaxWidthPx={NoteTracker.MeasureLineMaxWidthPx} " +
                      $"longNoteMinHeightPx={NoteTracker.LongNoteMinHeightPx} start={startTime:O}";
        rawLog?.WriteLine($"# {meta}");
        rawLog?.WriteLine("# elapsedMs\tlane0\tlane1\tlane2\tlane3 (0=배경,1=흰/파랑,2=빨강, index0=띠 맨위=클라이언트 y=stripTopY, 4레인 한 번에 동시 캡처됨)");
        evLog?.WriteLine($"# {meta}");
        evLog?.WriteLine("# lane\thitMs\tvelocity_px_per_ms\tlengthPx\tkind(TAP/HOLD/SIDE)");
        evLog?.WriteLine("# hitMs = 노트가 판정선에 닿는 시각(= osu! HitObject.StartTime과 같은 기준). 등장 시각이 아님.");
        evLog?.WriteLine("# 홀드 종료 시각 = hitMs + lengthPx/velocity");

        // 캘리브레이션 중에는 속도를 재는 것이 목적이므로 보정 속도를 쓰지 않는다(자기 자신을 기준 삼게 된다).
        double overrideVelocity = _calibrationOnly ? 0 : _calibratedVelocity;

        var trackers = new NoteTracker[LaneCount];
        for (int i = 0; i < LaneCount; i++)
        {
            // 트래커는 띠 안의 좌표(띠 맨위 = 0)로 계산하므로, 판정선도 띠 맨위로부터의 거리로 넘긴다.
            trackers[i] = new NoteTracker(_stripHeight, _judgementLineY - _stripTopY, overrideVelocity, lane: i);

            if (_sender == null) continue;

            trackers[i].NoteReady += _sender.Queue;
            trackers[i].NoteLengthKnown += _sender.SendLength;
        }

        // 오디오 녹음은 events.log의 hitMs와 동일한 t=0(스톱워치 시작 직전)에 맞춰 시작한다.
        // 그래야 나중에 osz를 만들 때 hitMs를 그대로 오디오 타임라인의 시각으로 써도 어긋나지 않는다.
        ProcessAudioRecorder? audioRecorder = null;
        if (_generateOsz && !_calibrationOnly)
        {
            try
            {
                Native.GetWindowThreadProcessId(_targetHwnd, out uint pid);
                audioRecorder = new ProcessAudioRecorder(pid);
                audioRecorder.Start();
            }
            catch (Exception ex)
            {
                audioRecorder = null;
                OszFailed?.Invoke($"오디오 녹음을 시작하지 못해 osz를 만들 수 없습니다: {ex.Message}");
            }
        }

        var sw = Stopwatch.StartNew();
        long nextTickTicks = 0;
        var lastState = new bool[LaneCount];
        var rowStates = new RowState[LaneCount][];
        for (int i = 0; i < LaneCount; i++) rowStates[i] = new RowState[_stripHeight];
        var lineBuf = new StringBuilder();
        int linesSinceFlush = 0;

        while (_running)
        {
            // 타임스탬프는 우리가 요청한 시각이 아니라 프레임이 실제로 합성된 시각을 쓴다.
            // WGC 프레임은 최대 한 화면주사만큼 묵은 것일 수 있어서, 요청 시각을 쓰면 그만큼 흔들린다.
            long elapsedMs = 0;

            bool got = capture.TryProcessLatestFrame((scan0, stride, frameTimeMs) =>
            {
                elapsedMs = (long)Math.Round(frameTimeMs);

                for (int lane = 0; lane < LaneCount; lane++)
                    ScanRows(scan0, stride, _stripHeight, laneXOffsets[lane], StripWidth, rowStates[lane]);
            });

            // 아직 프레임이 없으면(캡처 시작 직후 등) 이번 틱은 건너뛴다.
            if (!got)
            {
                WaitForNextTick(sw, ref nextTickTicks);
                continue;
            }

            if (rawLog != null)
            {
                lineBuf.Clear();
                lineBuf.Append(elapsedMs);
                for (int lane = 0; lane < LaneCount; lane++)
                {
                    lineBuf.Append('\t');
                    foreach (var s in rowStates[lane]) lineBuf.Append((char)('0' + (int)s));
                }
                rawLog.WriteLine(lineBuf.ToString());
            }

            for (int lane = 0; lane < LaneCount; lane++)
            {
                var finished = trackers[lane].Update(elapsedMs, rowStates[lane]);
                foreach (var ev in finished) Emit(evLog, lane, ev);

                bool detected = trackers[lane].AnyRealNoteActive();
                if (detected != lastState[lane])
                {
                    lastState[lane] = detected;
                    LaneStateChanged?.Invoke(lane, detected);
                }
            }

            // 원점 동기화는 매 틱 보낸다 — osu!가 나중에 모드에 들어와도 그 시점에 원점을 잡을 수 있어야 한다.
            _sender?.Sync(elapsedMs);

            // 4레인이 모두 처리된 뒤에 보낸다 — 사이드노트 병합은 같은 틱의 두 레인을 함께 봐야 한다.
            _sender?.Flush();

            if (++linesSinceFlush >= 60)   // 대략 1초마다 flush
            {
                rawLog?.Flush();
                evLog?.Flush();
                linesSinceFlush = 0;
            }

            WaitForNextTick(sw, ref nextTickTicks);
        }

        // 화면에 남아 있던 노트도 마저 확정한다 — 캘리브레이션은 이 표본까지 세야 한다.
        for (int lane = 0; lane < LaneCount; lane++)
            foreach (var ev in trackers[lane].FlushAll()) Emit(evLog, lane, ev);

        _sender?.End();
        rawLog?.Flush();
        evLog?.Flush();

        // osz 빌더가 이 파일을 다시 열어 읽어야 하므로, 여기서 명시적으로 닫는다 — 메서드 끝의
        // `using`이 닫아줄 때까지 기다리면 그 사이에 OszBuilder가 "다른 프로세스가 쓰고 있다"며 실패한다
        // (같은 프로세스 안에서도 아직 열려 있는 핸들과는 공유 모드가 안 맞을 수 있음, 실기에서 확인됨).
        rawLog?.Dispose();
        evLog?.Dispose();

        if (audioRecorder != null)
        {
            try
            {
                byte[]? wav = audioRecorder.Stop();
                if (wav != null && wav.Length > 0 && EventsLogFilePath != null && oszOutputPath != null)
                    OszBuilder.Build(EventsLogFilePath, wav, startTime, oszOutputPath, _excludeSideNotes);
            }
            catch (Exception ex)
            {
                OszFailed?.Invoke($"osz 생성에 실패했습니다: {ex.Message}");
            }
            finally
            {
                audioRecorder.Dispose();
            }
        }
    }

    /// <summary>다음 틱까지 대기 (Stopwatch 기준으로 드리프트 누적 방지).</summary>
    static void WaitForNextTick(Stopwatch sw, ref long nextTickTicks)
    {
        nextTickTicks += TickInterval.Ticks;
        long waitTicks = nextTickTicks - sw.Elapsed.Ticks;

        if (waitTicks <= 0) return;

        int waitMs = (int)TimeSpan.FromTicks(waitTicks).TotalMilliseconds;
        if (waitMs > 1) Thread.Sleep(waitMs - 1);
        while (sw.Elapsed.Ticks < nextTickTicks) Thread.SpinWait(50);
    }

    /// <summary>
    /// 스트립을 완전히 빠져나가 확정된 노트. 일반 모드에서는 이미 실시간으로 보낸 뒤라 로그만 남기고,
    /// 캘리브레이션 모드에서는 로그 없이 속도 표본으로만 쓴다.
    /// </summary>
    void Emit(StreamWriter? evLog, int lane, NoteEvent ev)
    {
        if (_calibrationOnly)
        {
            if (ev.VelocityPxPerMs > 0)
                VelocityMeasured?.Invoke(ev.VelocityPxPerMs);
            return;
        }

        evLog?.WriteLine($"{lane}\t{ev.HitMs}\t{ev.VelocityPxPerMs:F4}\t{ev.LengthPx:F1}\t{ev.Kind}");
    }

    /// <summary>
    /// 4레인을 한 번에 캡처한 프레임에서, 레인 하나에 해당하는 xOffset~xOffset+width 구간만
    /// 행(y)별로 훑어서 각 행이 배경/흰·파랑/빨강 중 무엇인지 rows에 채운다.
    /// <para>
    /// 픽셀은 BGRA 32bpp다. WGC가 매핑해준 텍스처를 <b>복사 없이 그 자리에서</b> 읽는다
    /// (예전에는 <c>Bitmap.LockBits</c>였는데, 형식이 같아서 스캔 로직은 그대로다).
    /// </para>
    /// </summary>
    static unsafe void ScanRows(IntPtr scan0, int stride, int height, int xOffset, int width, RowState[] rows)
    {
        byte* p = (byte*)scan0;

        for (int y = 0; y < height; y++)
        {
            byte* row = p + y * stride;
            long sumR = 0, sumG = 0, sumB = 0;

            for (int x = xOffset; x < xOffset + width; x++)
            {
                sumB += row[x * 4 + 0];
                sumG += row[x * 4 + 1];
                sumR += row[x * 4 + 2];
            }

            int avgR = (int)(sumR / width);
            int avgG = (int)(sumG / width);
            int avgB = (int)(sumB / width);
            int brightness = (avgR + avgG + avgB) / 3;

            if (brightness < BrightnessThreshold)
                rows[y] = RowState.Background;
            else if (avgR - Math.Max(avgG, avgB) >= RedDominanceMargin)
                rows[y] = RowState.Side;
            else
                rows[y] = RowState.Normal;
        }
    }

    public void Dispose() => Stop();
}
