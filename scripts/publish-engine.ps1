# Publishes SpokaVoiceEngine (the AI voice changer, a separate GPL-3.0 program) into <Destination>\voice-engine,
# with its licence and source pointer. Used by publish.ps1 (installer) and build-store.ps1 (Microsoft Store).
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $Destination "voice-engine"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish "$root\engine\SpokaVoiceEngine\SpokaVoiceEngine.csproj" `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishTrimmed=true -p:TrimMode=partial `
    -p:DebugType=none -p:GenerateDocumentationFile=false `
    -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "La publication du moteur du changeur de voix a échoué." }

# Debug layers and import libraries are not needed at run time.
Get-ChildItem $out -Include "*.pdb", "*.lib", "DirectML.Debug.dll", "createdump.exe" -Recurse | Remove-Item -Force

# onnxruntime.dll needs the Visual C++ runtime (MSVCP140, MSVCP140_1, VCRUNTIME140, VCRUNTIME140_1), which a fresh
# Windows does not have: without it the engine cannot start (TypeInitializationException in NativeMethods). These
# files are redistributable app-locally (Visual Studio « REDIST » list). Taken from the newest installed toolset, never
# older than 14.40 (ONNX Runtime 1.20+ needs it).
$crtRoots = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\*\VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT",
    "$root\packaging\deps\vc-crt"
)
$crt = $crtRoots | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
    Where-Object { Test-Path (Join-Path $_.FullName "msvcp140_1.dll") } |
    Sort-Object { (Get-Item (Join-Path $_.FullName "msvcp140.dll")).VersionInfo.FileVersionRaw } -Descending |
    Select-Object -First 1
if (-not $crt) { throw "Runtime Visual C++ (Microsoft.VC143.CRT) introuvable : installez les outils C++ de Visual Studio 2022 ou copiez les DLL dans packaging\deps\vc-crt." }
$crtVersion = (Get-Item (Join-Path $crt.FullName "msvcp140.dll")).VersionInfo.FileVersionRaw
if ($crtVersion -lt [version]"14.40") { throw "Runtime Visual C++ trop ancien ($crtVersion) : ONNX Runtime demande 14.40 ou plus." }
foreach ($dll in "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll") {
    Copy-Item (Join-Path $crt.FullName $dll) $out -Force
}
Write-Host "Runtime Visual C++ $crtVersion copié ($($crt.FullName))"
Copy-Item "$root\engine\LICENSE" (Join-Path $out "LICENSE.txt")
Copy-Item "$root\engine\README.md" (Join-Path $out "LISEZ-MOI.md")
@"
SpokaVoiceEngine est distribué sous licence GPL-3.0-or-later (voir LICENSE.txt).
Son code source complet, et les scripts qui produisent ses modèles, sont publiés ici :
https://github.com/Spokator/SpokaSoundBoard/tree/main/engine
"@ | Set-Content (Join-Path $out "SOURCE.txt") -Encoding utf8

$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("OK -> $out ({0:N0} Mo)" -f $size) -ForegroundColor Green
