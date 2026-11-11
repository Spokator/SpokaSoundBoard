# Builds the Microsoft Store package: dist-store\SpokaSoundBoard-<version>.msix (unsigned: the Store signs it).
#
#   .\scripts\build-store.ps1              -> package to upload in Partner Center
#   .\scripts\build-store.ps1 -TestSign    -> package signed with a local self-signed certificate (sideloading test)
#
# No Windows SDK needed: makeappx / makepri / signtool come from the NuGet package Microsoft.Windows.SDK.BuildTools,
# yt-dlp and ffmpeg are downloaded once. Everything is cached in packaging\deps (gitignored). See docs\STORE.md.
param(
    [switch]$TestSign,
    [switch]$SkipTests,
    [string]$Configuration = "Release",
    # Pinned for reproducible builds; any recent version works.
    [string]$BuildToolsVersion = "10.0.28000.2957"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is very slow with the progress bar.
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$deps = Join-Path $root "packaging\deps"
$out = Join-Path $root "dist-store"
$staging = Join-Path $out "staging"
$priWork = Join-Path $out "pri"
New-Item -ItemType Directory $deps, $out -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Invoke-Tool([string]$exe, [string[]]$arguments, [string]$what, [switch]$Quiet) {
    if ($Quiet) {
        # makeappx / makepri list every file: only show their output when they fail.
        $ErrorActionPreference = "Continue"   # Windows PowerShell turns stderr lines into errors.
        $log = & $exe @arguments 2>&1
        if ($LASTEXITCODE -ne 0) { $log | Write-Host }
    }
    else { & $exe @arguments }
    if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)." }
}

function Get-File([string]$url, [string]$target, [string]$sumsUrl) {
    Write-Host "Téléchargement de $url"
    $tmp = "$target.part"
    Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing
    if ($sumsUrl) {
        # The tools are shipped to every user: refuse a file that differs from what its publisher lists.
        $name = [IO.Path]::GetFileName(([Uri]$url).AbsolutePath)
        $sums = (Invoke-WebRequest $sumsUrl -UseBasicParsing).Content
        if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
        $line = ($sums -split "`n") | Where-Object { $_ -match "^[0-9a-fA-F]{64}\s+\*?$([regex]::Escape($name))\s*$" } | Select-Object -First 1
        if (-not $line) { Remove-Item $tmp -Force; throw "Empreinte de $name introuvable dans $sumsUrl." }
        $expected = $line.Substring(0, 64).ToLowerInvariant()
        $actual = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { Remove-Item $tmp -Force; throw "$name ne correspond pas à l'empreinte publiée ($actual au lieu de $expected)." }
        Write-Host "  empreinte vérifiée ($name)"
    }
    Move-Item $tmp $target -Force
}

# ---------------------------------------------------------------- 1. Version (csproj 2.0.0 -> 2.0.0.0)
[xml]$csproj = Get-Content "$root\src\SpokaSoundBoard\SpokaSoundBoard.csproj"
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version du csproj inattendue : '$version' (attendu x.y.z)." }
$packageVersion = "$version.0"   # The Store requires the 4th number to be 0.
Write-Host "Version $packageVersion" -ForegroundColor Cyan

# ---------------------------------------------------------------- 2. makeappx / makepri / signtool
$sdkDir = Join-Path $deps "sdk-buildtools\$BuildToolsVersion"
if (-not (Test-Path $sdkDir)) {
    $nupkg = Join-Path $deps "microsoft.windows.sdk.buildtools.$BuildToolsVersion.nupkg"
    if (-not (Test-Path $nupkg)) {
        Get-File "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$BuildToolsVersion/microsoft.windows.sdk.buildtools.$BuildToolsVersion.nupkg" $nupkg
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $sdkDir)
}
$sdkBin = Get-ChildItem (Join-Path $sdkDir "bin") -Directory | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName "x64" } | Where-Object { Test-Path (Join-Path $_ "makeappx.exe") } | Select-Object -First 1
if (-not $sdkBin) { throw "makeappx.exe introuvable dans $sdkDir." }
$makeappx = Join-Path $sdkBin "makeappx.exe"
$makepri = Join-Path $sdkBin "makepri.exe"
$signtool = Join-Path $sdkBin "signtool.exe"

# ---------------------------------------------------------------- 3. yt-dlp and ffmpeg (bundled: the Store edition never downloads executables)
$ytdlp = Join-Path $deps "yt-dlp.exe"
if (-not (Test-Path $ytdlp) -or ((Get-Date) - (Get-Item $ytdlp).LastWriteTime).TotalDays -gt 7) {
    Get-File "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" $ytdlp "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS"
}
$ffmpeg = Join-Path $deps "ffmpeg.exe"
$ffmpegLicense = Join-Path $deps "ffmpeg-LICENSE.txt"
if (-not (Test-Path $ffmpeg) -or ((Get-Date) - (Get-Item $ffmpeg).LastWriteTime).TotalDays -gt 30) {
    $zip = Join-Path $deps "ffmpeg-win64-gpl.zip"
    if (-not (Test-Path $zip) -or ((Get-Date) - (Get-Item $zip).LastWriteTime).TotalDays -gt 30) {
        Get-File "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip" $zip "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256"
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $exe = $archive.Entries | Where-Object { $_.Name -eq "ffmpeg.exe" } | Select-Object -First 1
        if (-not $exe) { throw "ffmpeg.exe introuvable dans $zip." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($exe, $ffmpeg, $true)
        $license = $archive.Entries | Where-Object { $_.Name -eq "LICENSE.txt" } | Select-Object -First 1
        if ($license) { [IO.Compression.ZipFileExtensions]::ExtractToFile($license, $ffmpegLicense, $true) }
    }
    finally { $archive.Dispose() }
    (Get-Item $ffmpeg).LastWriteTime = Get-Date
    Remove-Item $zip -Force
}

# ---------------------------------------------------------------- 4. Tests and publish (self-contained, NOT single-file, no trimming)
if (-not $SkipTests) {
    Invoke-Tool dotnet @("test", "$root\tests\SpokaSoundBoard.Tests", "-c", $Configuration, "--nologo") "Les tests"
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Invoke-Tool dotnet @(
    "publish", "$root\src\SpokaSoundBoard\SpokaSoundBoard.csproj",
    "-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-p:PublishReadyToRun=false",
    "-p:DebugType=none", "-p:GenerateDocumentationFile=false",
    "-o", $staging, "--nologo") "La publication"

$tools = Join-Path $staging "tools"
New-Item -ItemType Directory $tools -Force | Out-Null
Copy-Item $ytdlp, $ffmpeg $tools
if (Test-Path $ffmpegLicense) { Copy-Item $ffmpegLicense $tools }

# ---------------------------------------------------------------- 5. Manifest, assets, resources.pri
$publisher = "CN=1FBF4454-E4DA-41D2-BBA3-8C9433B84CE4"
$manifest = (Get-Content "$root\packaging\AppxManifest.xml" -Raw -Encoding UTF8).Replace("__VERSION__", $packageVersion)
if ($TestSign) {
    # Throwaway publisher: a Store identity can only be signed by the Store.
    $publisher = "CN=SpokaSoundBoard-Dev"
    $manifest = $manifest.Replace('Publisher="CN=1FBF4454-E4DA-41D2-BBA3-8C9433B84CE4"', "Publisher=`"$publisher`"")
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $staging "AppxManifest.xml"), $manifest, $utf8)
Copy-Item "$root\packaging\Assets" (Join-Path $staging "Assets") -Recurse

# resources.pri maps "Assets\X.png" to the scale-/targetsize- variants. Indexed from a folder that only holds the
# manifest and the assets, so the hundreds of runtime files are not listed in it.
if (Test-Path $priWork) { Remove-Item $priWork -Recurse -Force }
New-Item -ItemType Directory $priWork | Out-Null
Copy-Item (Join-Path $staging "AppxManifest.xml") $priWork
Copy-Item "$root\packaging\Assets" (Join-Path $priWork "Assets") -Recurse
$priConfig = Join-Path $out "priconfig.xml"
Invoke-Tool $makepri @("createconfig", "/cf", $priConfig, "/dq", "fr-FR", "/pv", "10.0.0", "/o") "makepri createconfig" -Quiet
# One single resources.pri (no split per scale / language: those only make sense in bundles).
[xml]$cfg = Get-Content $priConfig
$cfg.resources.SelectNodes("packaging") | ForEach-Object { [void]$cfg.resources.RemoveChild($_) }
$cfg.Save($priConfig)
Invoke-Tool $makepri @("new", "/pr", $priWork, "/cf", $priConfig, "/mn", (Join-Path $priWork "AppxManifest.xml"),
    "/of", (Join-Path $staging "resources.pri"), "/o") "makepri new" -Quiet
Remove-Item $priWork -Recurse -Force
Remove-Item $priConfig -Force

# ---------------------------------------------------------------- 6. Pack (and optionally sign)
$suffix = if ($TestSign) { "-test" } else { "" }
$msix = Join-Path $out "SpokaSoundBoard-$version$suffix.msix"
Invoke-Tool $makeappx @("pack", "/d", $staging, "/p", $msix, "/o") "makeappx pack" -Quiet

if ($TestSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date).AddDays(1) -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        Write-Host "Création du certificat de test $publisher (magasin de l'utilisateur, sans droits administrateur)"
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName "SpokaSoundBoard test MSIX" -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(2)
    }
    $cer = Join-Path $out "SpokaSoundBoard-Dev.cer"
    Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
    Invoke-Tool $signtool @("sign", "/fd", "SHA256", "/sha1", $cert.Thumbprint, "/s", "My", $msix) "signtool sign"
    Write-Host ""
    Write-Host "Paquet de test signé. Pour l'installer (voir docs\STORE.md, « Essai local ») :" -ForegroundColor Yellow
    Write-Host "  1. Une seule fois, dans un PowerShell ADMINISTRATEUR :"
    Write-Host "     Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "  2. Add-AppxPackage `"$msix`""
}

$size = [math]::Round((Get-Item $msix).Length / 1MB, 1)
Write-Host "OK -> $msix ($size Mo)" -ForegroundColor Green
