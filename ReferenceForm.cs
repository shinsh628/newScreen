namespace ScreenEditor;

/// <summary>
/// 편집 모드에서만 보이는 풀스크린 반투명 참조 이미지 창.
/// 썸네일 변형 파이프라인 밖의 독립 최상위 창 → 화면을 이동/확대해도 항상 고정.
/// 클릭 통과(WS_EX_TRANSPARENT)라 편집 조작을 방해하지 않음.
/// </summary>
public class ReferenceForm : Form
{
    readonly PictureBox _pic = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.StretchImage,   // 풀스크린 꽉 채우기
        BackColor = Color.Transparent
    };

    public ReferenceForm(double opacity)
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        BackColor = Color.Black;
        Opacity = Math.Clamp(opacity, 0.05, 1.0);
        Controls.Add(_pic);
        Visible = false;
    }

    // 레이어드 + 클릭통과 + 작업표시줄/Alt-Tab 미노출
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;  // 포커스 가로채지 않음

    public bool HasImage => _pic.Image != null;

    /// <summary>참조 이미지 숨김 (이미지는 유지). 일반 모드 진입 시 호출.</summary>
    public void HideReference() => Visible = false;

    /// <summary>참조 이미지 제거 — 숨기고 이미지도 비운다.</summary>
    public void ClearImage()
    {
        Visible = false;
        var old = _pic.Image;
        _pic.Image = null;
        old?.Dispose();
    }

    /// <summary>파일 탐색기로 이미지 선택 → 반투명 표시.</summary>
    public void LoadImageDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "참조 이미지 선택",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif|모든 파일|*.*"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var old = _pic.Image;
            // 파일 잠금 방지: 스트림에서 복사 로드
            using var fs = File.OpenRead(dlg.FileName);
            _pic.Image = Image.FromStream(fs);
            old?.Dispose();
            Visible = true;
            BringToFront();
        }
        catch (Exception ex)
        {
            MessageBox.Show("이미지를 불러올 수 없습니다: " + ex.Message);
        }
    }

    public void AdjustOpacity(double delta) => Opacity = Math.Clamp(Opacity + delta, 0.05, 1.0);
}
