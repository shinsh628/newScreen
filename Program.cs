namespace ScreenEditor;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();   // ApplicationHighDpiMode(PerMonitorV2) 자동 적용

        var settings = Settings.Load();

        // 1) 설정에 저장된 게임 창 제목으로 먼저 시도
        IntPtr game = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(settings.GameTitle))
        {
            var h = Native.FindWindow(null, settings.GameTitle);
            if (h != IntPtr.Zero && Native.IsWindow(h)) game = h;
        }

        // 2) 없으면 창 목록에서 선택
        if (game == IntPtr.Zero)
        {
            using var picker = new WindowPicker();
            if (picker.ShowDialog() != DialogResult.OK || picker.SelectedHandle == IntPtr.Zero)
                return;   // 취소
            game = picker.SelectedHandle;
            settings.GameTitle = picker.SelectedTitle;
            settings.Save();
        }

        // 오버레이는 백그라운드 엔진(시작 시 숨김). 제어판이 메인 창이 된다.
        var overlay = new OverlayForm(game, settings);
        _ = overlay.Handle;   // 핸들 강제 생성 → OnHandleCreated에서 전역 핫키·DWM 썸네일 등록

        // 제어판이 닫히면 Application.Run이 끝나고, OnFormClosing이 오버레이까지 모두 정리한다.
        Application.Run(new ControlPanelForm(overlay, settings));
    }
}
