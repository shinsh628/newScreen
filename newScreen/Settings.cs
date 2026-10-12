using System.Text.Json;

namespace NewScreen;

/// <summary>칼럼 위치 · 핫키 · 옵션을 JSON으로 영속화.</summary>
public class Settings
{
    public const int LaneCount = 4;

    /// <summary>캡처 대상 게임 창의 제목. 시작할 때 이 제목으로 창을 다시 찾는다.</summary>
    public string TargetWindowTitle { get; set; } = "";

    public bool TargetWindowSet => !string.IsNullOrWhiteSpace(TargetWindowTitle);

    /// <summary>
    /// 오버레이에 비출 osu! 창의 제목. 보통은 프로세스명으로 자동으로 찾으므로 비어 있고,
    /// 자동 탐색이 실패해서 사용자가 직접 고른 경우에만 채워진다.
    /// </summary>
    public string OsuWindowTitle { get; set; } = "";

    /// <summary>캡처 시작 시 osu! 화면을 대상 게임 위에 덮을지.</summary>
    public bool ShowOsuOverlay { get; set; } = true;

    /// <summary>
    /// osu! 오버레이의 불투명도(%). 100이면 완전히 덮고, 낮추면 아래의 대상 게임이 비쳐 보인다.
    /// <para>
    /// 디버깅용이다 — 두 화면의 노트가 같은 타이밍에 판정선에 닿는지 눈으로 겹쳐 볼 수 있다.
    /// </para>
    /// </summary>
    public int OsuOverlayOpacityPercent { get; set; } = 100;

    /// <summary>
    /// 4개 레인의 캡처 띠 중심 x좌표 — <b>대상 창의 클라이언트 좌표</b>. -1 = 미설정.
    /// <para>
    /// WGC 전환 전에는 화면 절대좌표(<c>ColumnXs</c>)였다. 창 기준으로 바뀌면서 값의 의미가 달라졌으므로
    /// 프로퍼티 이름도 같이 바꿨다 — 옛 설정 파일의 <c>ColumnXs</c>는 조용히 무시되고 재설정을 요구하게 된다
    /// (옛 값을 그대로 읽으면 엉뚱한 위치를 캡처하면서 원인을 알기 어렵다).
    /// </para>
    /// <para>
    /// 창 기준이라 대상 창이 움직이거나 화면 해상도가 바뀌어도 다시 찍을 필요가 없다.
    /// </para>
    /// </summary>
    public int[] ClientColumnXs { get; set; } = new int[LaneCount] { -1, -1, -1, -1 };

    public bool AllColumnsSet => ClientColumnXs.All(x => x >= 0);

    /// <summary>
    /// 판정선의 y좌표 — <b>대상 창의 클라이언트 좌표</b>. -1 = 미설정.
    /// <para>
    /// 노트가 "화면에 등장한 시각"이 아니라 <b>"판정선에 닿는 시각"</b>을 계산하는 데 쓴다.
    /// osu!의 <c>HitObject.StartTime</c>이 곧 판정선에서 쳐야 하는 시각이므로, 이 기준으로 맞춰야
    /// 두 게임의 타이밍이 일치한다. 캡처 띠의 y=0에서 이 값까지의 거리를 속도로 나눈 만큼이 그 차이다.
    /// </para>
    /// </summary>
    public int ClientJudgementLineY { get; set; } = -1;

    public bool JudgementLineSet => ClientJudgementLineY >= 0;

    /// <summary>
    /// 캡처 띠 위쪽 끝의 y좌표 — <b>대상 창의 클라이언트 좌표</b>. 칼럼 설정에서 판정선 다음에 찍는다.
    /// <para>
    /// 기본값 0(창 최상단) + <see cref="StripHeightPx"/> 400은 1.2.0까지의 고정 동작과 같다 — 띠를 따로
    /// 지정한 적 없는 기존 설정 파일은 예전과 똑같이 동작한다.
    /// </para>
    /// </summary>
    public int ClientStripTopY { get; set; } = 0;

    /// <summary>캡처 띠 길이(px). 칼럼 설정에서 띠 아래쪽 끝을 찍어 정한다.</summary>
    public int StripHeightPx { get; set; } = NoteDetector.DefaultStripHeight;

    /// <summary>캡처를 시작할 수 있는 상태인지 — 대상 창 + 레인 4개 + 판정선이 모두 있어야 한다.</summary>
    public bool CaptureReady => TargetWindowSet && AllColumnsSet && JudgementLineSet;

    /// <summary>
    /// 캘리브레이션으로 구한 스크롤 속도(px/ms). 0 = 미측정.
    /// <para>
    /// 노트별 실측 속도는 표본이 적어 가끔 크게 튀는데, 판정선 기준으로 시각을 옮기면 그 오차가
    /// 판정선까지의 거리만큼 증폭된다(실측에서 최악 2.5초). SV가 없다는 전제에서 속도는 상수이므로
    /// 표본을 많이 모은 전역값 하나가 원리적으로 더 정확하다.
    /// </para>
    /// </summary>
    public double CalibratedVelocityPxPerMs { get; set; }

    public bool VelocityCalibrated => CalibratedVelocityPxPerMs > 0;

    /// <summary>캡처한 노트를 osu!(패턴 복제 모드)로 실시간 전송할지. 전역 속도가 있어야 동작한다.</summary>
    public bool SendNotesToOsu { get; set; } = true;

    /// <summary>
    /// 캡처 종료 시 <c>events.log</c> + 대상 창 오디오로 osu! mania 6키 <c>.osz</c>를 자동 생성할지.
    /// 캘리브레이션 전용 캡처(events.log가 안 남는 모드)에서는 체크 여부와 무관하게 항상 건너뛴다.
    /// </summary>
    public bool GenerateOsz { get; set; } = false;

    /// <summary>
    /// true면 사이드노트 감지·전송은 평소대로 하되, osz를 만들 때만 SIDE 이벤트를 빼고 4키
    /// (CircleSize=4) 맵으로 만든다.
    /// </summary>
    public bool ExcludeSideNotesFromOsz { get; set; } = false;

    public bool AlwaysOnTop { get; set; } = true;

    // 버튼별 전역 단축키 (액션 id → 키). 기본값 없음 = 전부 빈칸.
    public Dictionary<string, HotkeyDef> Hotkeys { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore] static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewScreen");
    [System.Text.Json.Serialization.JsonIgnore] static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null && s.ClientColumnXs != null && s.ClientColumnXs.Length == LaneCount) return s;
            }
        }
        catch { /* 손상 시 기본값 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 저장 실패는 무시 */ }
    }
}

/// <summary>전역 단축키 한 개. Vk==0 이면 미설정(빈칸).</summary>
public class HotkeyDef
{
    public uint Mod { get; set; }
    public uint Vk { get; set; }
}
