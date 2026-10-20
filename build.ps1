# Builds bin\PixView.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$icon = Join-Path $root 'src\icon.ico'
if (-not (Test-Path $icon)) { & (Join-Path $root 'tools\make-icon.ps1') -Out $icon }

$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    "/out:$bin\PixView.exe" "/win32icon:$icon" "/win32manifest:$root\src\app.manifest" `
    "/lib:$fw\WPF" /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "OK -> $bin\PixView.exe"
