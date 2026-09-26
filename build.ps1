# Compila EtlStudio y crea el instalador en dist\EtlStudio-Setup.exe
#   .\build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $msbuild) { throw "No encontre MSBuild. Instala Visual Studio (o las Build Tools) con la carga de trabajo de escritorio .NET." }

if (-not (Test-Path "$root\assets\EtlStudio.ico")) { & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\tools\make-icon.ps1" }
Get-Process EtlStudio -ErrorAction SilentlyContinue | Stop-Process -Force

& $msbuild.FullName "$root\EtlStudio.csproj" /nologo /v:m /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de EtlStudio" }
& $msbuild.FullName "$root\installer\Setup.csproj" /nologo /v:m /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion del instalador" }

$setup = "$root\dist\EtlStudio-Setup.exe"
"Instalador listo: $setup ({0:N1} MB)" -f ((Get-Item $setup).Length / 1MB)
