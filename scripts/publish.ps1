# Builds a self-contained, single-file SpokaSoundBoard.exe into .\dist (no .NET install needed).
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipTests) {
    dotnet test "$root\tests\SpokaSoundBoard.Tests" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Les tests ont échoué." }
}

$dist = Join-Path $root "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish "$root\src\SpokaSoundBoard\SpokaSoundBoard.csproj" `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }

Get-ChildItem $dist | Where-Object { $_.Extension -ne ".exe" } | Remove-Item -Recurse -Force
Write-Host "OK -> $dist\SpokaSoundBoard.exe" -ForegroundColor Green
