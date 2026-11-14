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
Copy-Item "$root\engine\LICENSE" (Join-Path $out "LICENSE.txt")
Copy-Item "$root\engine\README.md" (Join-Path $out "LISEZ-MOI.md")
@"
SpokaVoiceEngine est distribué sous licence GPL-3.0-or-later (voir LICENSE.txt).
Son code source complet, et les scripts qui produisent ses modèles, sont publiés ici :
https://github.com/Spokator/SpokaSoundBoard/tree/main/engine
"@ | Set-Content (Join-Path $out "SOURCE.txt") -Encoding utf8

$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("OK -> $out ({0:N0} Mo)" -f $size) -ForegroundColor Green
