using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenEditor;

/// <summary>핫키 · 변형 상태 · 참조이미지 설정을 JSON으로 영속화.</summary>
public class Settings
{
    public string GameTitle { get; set; } = "";
    public float Zoom { get; set; } = 1f;
    public float PanX { get; set; } = 0f;
    public float PanY { get; set; } = 0f;
    public double RefOpacity { get; set; } = 0.5;

    // 가림막(검정 사각형) 목록 — 편집 종료/재시작 후에도 유지
    public List<MaskRect> Masks { get; set; } = new();

    // 버튼별 전역 단축키 (액션 id → 키). 기본값 없음 = 전부 빈칸.
    public Dictionary<string, HotkeyDef> Hotkeys { get; set; } = new();

    public bool AlwaysOnTop { get; set; } = true;       // 제어판 항상 위에 표시
    public bool MaskBorder { get; set; } = false;       // 가림막 상단 흰 테두리
    public int MaskBorderThickness { get; set; } = 3;   // 테두리 두께(px)

    [JsonIgnore] static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenEditor");
    [JsonIgnore] static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
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
    public uint Mod { get; set; }   // MOD_CONTROL | MOD_ALT | MOD_SHIFT 조합
    public uint Vk { get; set; }    // 가상 키 코드 (0 = 미설정)
}

/// <summary>가림막 한 개의 위치·크기(화면 좌표, px).</summary>
public class MaskRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    public MaskRect() { }
    public MaskRect(Rectangle r) { X = r.X; Y = r.Y; W = r.Width; H = r.Height; }
    public Rectangle ToRectangle() => new(X, Y, W, H);
}
