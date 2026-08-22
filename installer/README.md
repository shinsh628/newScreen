# ScreenEditor 설치 프로그램

## 빌드 방법
PowerShell에서:
```powershell
.\installer\build-installer.ps1
```
→ `installer\Output\ScreenEditor-Setup-<버전>.exe` 생성.

## 수동 빌드
```powershell
# 1) 자체 포함 배포 (반드시 프로젝트 폴더에서 — global.json 의 .NET 8 고정 적용)
cd C:\dev\ScreenEditor
dotnet publish ScreenEditor.csproj -c Release -r win-x64 --self-contained true -o publish

# 2) Inno Setup 컴파일
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\ScreenEditor.iss
```

## 특징
- **자체 포함(self-contained)**: .NET 8 런타임이 포함되어 있어 대상 PC에 .NET 설치가 필요 없음.
- 설치 경로: `Program Files\ScreenEditor` (관리자 권한 필요).
- 시작 메뉴 바로가기 자동 생성, 바탕화면 바로가기는 선택.
- 한국어 설치 마법사.
- 버전 변경: `ScreenEditor.csproj` 의 `<Version>` 과 `ScreenEditor.iss` 의 `MyAppVersion` 을 함께 수정.
