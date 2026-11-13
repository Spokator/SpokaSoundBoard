# Builds rnnoise.dll (Xiph RNNoise 0.2, BSD-3-Clause) for x64 with MSVC, from the official release tarball.
# The DLL is committed in src/SpokaSoundBoard.Core/native/win-x64: run this only to rebuild it.
#
# Needs Visual Studio 2022 with the C++ tools (cl.exe). The Windows SDK does not have to be installed: its headers
# and libraries are taken from Microsoft's NuGet packages (Microsoft.Windows.SDK.CPP[.x64]), downloaded once into
# packaging/deps and checked against Microsoft's signature.
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\..\src\SpokaSoundBoard.Core\native\win-x64")
)
$ErrorActionPreference = "Stop"

$Version = "0.2"
$Url = "https://github.com/xiph/rnnoise/releases/download/v$Version/rnnoise-$Version.tar.gz"
$Sha256 = "90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37"
$SdkVersion = "10.0.28000.2957"

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$deps = Join-Path $repo "packaging\deps"
$work = Join-Path ([IO.Path]::GetTempPath()) "spoka-rnnoise-build"
New-Item -ItemType Directory $work, $deps -Force | Out-Null

# --- RNNoise source ---------------------------------------------------------------------------------------
$tarball = Join-Path $work "rnnoise-$Version.tar.gz"
if (-not (Test-Path $tarball)) { Invoke-WebRequest $Url -OutFile $tarball -UseBasicParsing }
$hash = (Get-FileHash $tarball -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $Sha256) { Remove-Item $tarball; throw "SHA-256 inattendu pour $Url : $hash" }
$src = Join-Path $work "rnnoise-$Version"
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
tar -xzf $tarball -C $work

# --- Windows SDK headers and libraries (NuGet) -------------------------------------------------------------
$sdk = Join-Path $deps "sdk-cpp"
foreach ($id in "microsoft.windows.sdk.cpp", "microsoft.windows.sdk.cpp.x64") {
    $pkg = Join-Path $deps "$id.$SdkVersion.nupkg"
    if (-not (Test-Path $pkg)) { Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$SdkVersion/$id.$SdkVersion.nupkg" -OutFile $pkg -UseBasicParsing }
    $verify = dotnet nuget verify --all $pkg 2>&1 | Out-String
    if ($verify -notmatch "CN=Microsoft Corporation") { throw "Signature Microsoft introuvable sur $pkg" }
    $dest = Join-Path $sdk $id
    if (-not (Test-Path $dest)) { Expand-Archive $pkg $dest }
}
$inc = Get-ChildItem (Join-Path $sdk "microsoft.windows.sdk.cpp\c\Include") -Directory | Select-Object -First 1
$sdkLib = Join-Path $sdk "microsoft.windows.sdk.cpp.x64\c"

# --- MSVC ---------------------------------------------------------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio avec les outils C++ est introuvable." }
$msvc = Get-ChildItem (Join-Path $vs "VC\Tools\MSVC") -Directory | Sort-Object Name | Select-Object -Last 1
$bin = Join-Path $msvc.FullName "bin\Hostx64\x64"
$env:INCLUDE = @("$($msvc.FullName)\include", "$($inc.FullName)\ucrt", "$($inc.FullName)\um", "$($inc.FullName)\shared") -join ';'
$env:LIB = @("$($msvc.FullName)\lib\x64", "$sdkLib\ucrt\x64", "$sdkLib\um\x64") -join ';'

# SSE2 is part of every x64 CPU: the vectorized path of RNNoise runs on every 64-bit PC (no AVX required).
$flags = @("/nologo", "/O2", "/fp:fast", "/MT", "/DNDEBUG", "/DWIN32", "/DRNNOISE_BUILD", "/DDLL_EXPORT", "/DDISABLE_DEBUG_FLOAT",
           "/DOPUS_X86_MAY_HAVE_SSE", "/DOPUS_X86_MAY_HAVE_SSE2",
           "/I$src\include", "/I$src\src")
$files = "denoise.c", "rnn.c", "rnnoise_data.c", "rnnoise_tables.c", "nnet.c", "nnet_default.c", "pitch.c", "celt_lpc.c",
         "kiss_fft.c", "parse_lpcnet_weights.c" | ForEach-Object { Join-Path $src "src\$_" }

$obj = Join-Path $work "obj"
if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }
New-Item -ItemType Directory $obj | Out-Null
Push-Location $obj
try {
    $ErrorActionPreference = "Continue"
    & "$bin\cl.exe" @flags /c @files 2>&1 | Where-Object { "$_" -match "error|warning C4(013|047|133)" } | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "La compilation de RNNoise a échoué." }
    & "$bin\link.exe" /nologo /DLL /OUT:rnnoise.dll (Get-ChildItem *.obj).FullName 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "L'édition de liens de RNNoise a échoué." }
}
finally {
    Pop-Location
    $ErrorActionPreference = "Stop"
}

New-Item -ItemType Directory $OutDir -Force | Out-Null
Copy-Item (Join-Path $obj "rnnoise.dll") $OutDir -Force
Copy-Item (Join-Path $src "COPYING") (Join-Path $OutDir "RNNOISE-LICENSE.txt") -Force
$dll = Get-Item (Join-Path $OutDir "rnnoise.dll")
Write-Host "OK -> $($dll.FullName) ($([math]::Round($dll.Length / 1MB, 1)) Mo, SHA-256 $((Get-FileHash $dll.FullName).Hash.ToLowerInvariant()))"
