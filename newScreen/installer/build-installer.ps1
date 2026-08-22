# NewScreen installer build script
# Usage: .\installer\build-installer.ps1
# 1) self-contained publish (.NET runtime included)  2) Inno Setup -> Setup.exe

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot          # C:\dev\ScreenEditor\newScreen
$proj = Join-Path $root "NewScreen.csproj"
$pub  = Join-Path $root "publish"
$iss  = Join-Path $PSScriptRoot "NewScreen.iss"
$iscc = "C:\Users\shins\AppData\Local\Programs\Inno Setup 6\ISCC.exe"

if (-not (Test-Path $iscc)) { throw "Inno Setup (ISCC.exe) not found: $iscc" }

Write-Host "[1/2] Publishing self-contained build..." -ForegroundColor Cyan
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $pub -nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "[2/2] Compiling Inno Setup..." -ForegroundColor Cyan
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed" }

Write-Host "Done: Setup.exe created in $PSScriptRoot\Output\" -ForegroundColor Green
