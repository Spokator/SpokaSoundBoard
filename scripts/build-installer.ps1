# Builds the complete Windows installer: dist\SpokaSoundBoard-Setup-<version>.exe
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$csproj = Get-Content "$root\src\SpokaSoundBoard\SpokaSoundBoard.csproj"
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Version $version" -ForegroundColor Cyan

# 1. Self-contained single-file application.
& "$PSScriptRoot\publish.ps1" -SkipTests:$SkipTests
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "La publication a échoué." }

# 2. yt-dlp (Unlicense) bundled so that link import works right after installation.
$deps = Join-Path $root "installer\deps"
New-Item -ItemType Directory $deps -Force | Out-Null
$ytdlp = Join-Path $deps "yt-dlp.exe"
if (-not (Test-Path $ytdlp) -or ((Get-Date) - (Get-Item $ytdlp).LastWriteTime).TotalDays -gt 7) {
    Write-Host "Téléchargement de yt-dlp…"
    Invoke-WebRequest "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile $ytdlp -UseBasicParsing
}

# 3. Inno Setup.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Installation d'Inno Setup…"
    winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
    $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
}

& $iscc "/DAppVersion=$version" /Qp "$root\installer\SpokaSoundBoard.iss"
if ($LASTEXITCODE -ne 0) { throw "La compilation de l'installeur a échoué." }

Write-Host "OK -> $root\dist\SpokaSoundBoard-Setup-$version.exe" -ForegroundColor Green
