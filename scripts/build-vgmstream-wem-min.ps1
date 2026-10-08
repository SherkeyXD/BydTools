# Build libvgmstream.dll and copy the x64 Vorbis/FFmpeg runtimes into BydTools.Audio/3rdParty.
# Clones the commit in scripts/vgmstream.rev when -VgmstreamRoot is omitted or not a git checkout.
# Visual Studio is discovered with vswhere when -VsDevCmdPath or -Generator is omitted.
param(
    [string]$VsDevCmdPath = "",
    [string]$VgmstreamRoot = "",
    [string]$Project3rdPartyDir = "$PSScriptRoot\..\BydTools.Audio\3rdParty",
    [string]$BuildDir = "",
    [string]$Generator = "",
    [string]$Configuration = "Release",
    [string]$Arch = "x64",
    [string]$HostArch = "x64",
    [switch]$PruneUnusedDlls = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Path {
    param(
        [string]$Path,
        [string]$Description
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    & git @GitArgs
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArgs -join ' ') failed."
    }
}

function Get-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "vswhere.exe not found. Pass -VsDevCmdPath and -Generator."
    }
    $require = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
    $install = (& $vswhere -latest -products * -requires $require -property installationPath | Select-Object -First 1)
    $version = (& $vswhere -latest -products * -requires $require -property installationVersion | Select-Object -First 1)
    if (-not $install -or -not $version) {
        throw "Visual Studio with the C++ toolset was not found."
    }
    $major = [int]($version.Trim().Split('.')[0])
    $generatorName = switch ($major) {
        17 { "Visual Studio 17 2022" }
        18 { "Visual Studio 18 2026" }
        default { throw "Visual Studio $version is not supported. Pass -Generator." }
    }
    [pscustomobject]@{
        DevCmd = Join-Path $install.Trim() "Common7\Tools\VsDevCmd.bat"
        Generator = $generatorName
    }
}

function Ensure-VgmstreamCheckout {
    param(
        [string]$Root,
        [string]$Revision
    )
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $Root ".git"))) {
        Invoke-Git -C $Root init
        Invoke-Git -C $Root remote add origin "https://github.com/vgmstream/vgmstream.git"
    }
    Write-Host "== Fetch vgmstream $Revision =="
    Invoke-Git -C $Root fetch --depth 1 origin $Revision
    Invoke-Git -C $Root checkout --force --detach FETCH_HEAD
}

function Invoke-InVsDevCmd {
    param([string]$Command)

    $cmdLine = "call `"$VsDevCmdPath`" -arch=$Arch -host_arch=$HostArch && $Command"
    & cmd.exe /c $cmdLine
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed in VS Dev environment: $Command"
    }
}

$revisionPath = Join-Path $PSScriptRoot "vgmstream.rev"
$revision = (Get-Content -LiteralPath $revisionPath -Raw).Trim()
if ($revision -notmatch '^[0-9a-f]{40}$') {
    throw "scripts/vgmstream.rev must be a 40-character commit SHA."
}

if ([string]::IsNullOrWhiteSpace($VsDevCmdPath) -or [string]::IsNullOrWhiteSpace($Generator)) {
    $visualStudio = Get-VisualStudio
    if ([string]::IsNullOrWhiteSpace($VsDevCmdPath)) {
        $VsDevCmdPath = $visualStudio.DevCmd
    }
    if ([string]::IsNullOrWhiteSpace($Generator)) {
        $Generator = $visualStudio.Generator
    }
}

if ([string]::IsNullOrWhiteSpace($VgmstreamRoot)) {
    $base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
    $VgmstreamRoot = Join-Path $base "vgmstream\src"
    Ensure-VgmstreamCheckout -Root $VgmstreamRoot -Revision $revision
}
elseif (-not (Test-Path -LiteralPath (Join-Path $VgmstreamRoot ".git"))) {
    Ensure-VgmstreamCheckout -Root $VgmstreamRoot -Revision $revision
}

if ([string]::IsNullOrWhiteSpace($BuildDir)) {
    $BuildDir = Join-Path $VgmstreamRoot "build-msvc-mindeps"
}

$Project3rdPartyDir = [System.IO.Path]::GetFullPath($Project3rdPartyDir)
$BuildDir = [System.IO.Path]::GetFullPath($BuildDir)
New-Item -ItemType Directory -Force -Path $Project3rdPartyDir | Out-Null

Assert-Path -Path $VsDevCmdPath -Description "VsDevCmd"
Assert-Path -Path $VgmstreamRoot -Description "vgmstream root"

$ffmpegPath = Join-Path $VgmstreamRoot "ext_libs"
Assert-Path -Path $ffmpegPath -Description "vgmstream ext_libs (FFmpeg path)"

Write-Host "== Configure vgmstream (WEM minimal profile) =="
Write-Host "Generator: $Generator"
Invoke-InVsDevCmd -Command @"
cmake -S "$VgmstreamRoot" -B "$BuildDir" -G "$Generator" -A $Arch -DBUILD_SHARED_LIBS=ON -DBUILD_CLI=OFF -DBUILD_FB2K=OFF -DBUILD_WINAMP=OFF -DBUILD_XMPLAY=OFF -DUSE_FFMPEG=ON -DFFMPEG_PATH="$ffmpegPath" -DUSE_MPEG=OFF -DUSE_G719=OFF -DUSE_ATRAC9=OFF -DUSE_CELT=OFF -DUSE_SPEEX=OFF -DCMAKE_C_FLAGS="/utf-8"
"@

Write-Host "== Build libvgmstream_shared ($Configuration) =="
Invoke-InVsDevCmd -Command "cmake --build `"$BuildDir`" --config $Configuration --target libvgmstream_shared"

$builtDll = Join-Path $BuildDir "src\$Configuration\libvgmstream.dll"
Assert-Path -Path $builtDll -Description "Built libvgmstream.dll"

$runtimeDir = $ffmpegPath
if ($Arch -eq "x64") {
    $x64Dir = Join-Path $ffmpegPath "dll-x64"
    if (Test-Path -LiteralPath $x64Dir) {
        $runtimeDir = $x64Dir
    }
}

Write-Host "== Deploy required DLLs to 3rdParty =="
Copy-Item -LiteralPath $builtDll -Destination (Join-Path $Project3rdPartyDir "libvgmstream.dll") -Force
foreach ($dll in @(
        "libvorbis.dll",
        "avcodec-vgmstream-59.dll",
        "avformat-vgmstream-59.dll",
        "avutil-vgmstream-57.dll"
    )) {
    $source = Join-Path $runtimeDir $dll
    Assert-Path -Path $source -Description "vgmstream runtime DLL"
    Copy-Item -LiteralPath $source -Destination (Join-Path $Project3rdPartyDir $dll) -Force
}

if ($PruneUnusedDlls) {
    foreach ($dll in @(
            "libmpg123-0.dll",
            "libg719_decode.dll",
            "libatrac9.dll",
            "libcelt-0061.dll",
            "libcelt-0110.dll",
            "libspeex-1.dll",
            "swresample-vgmstream-4.dll"
        )) {
        $path = Join-Path $Project3rdPartyDir $dll
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

Write-Host "== Final 3rdParty DLL set =="
Get-ChildItem -LiteralPath $Project3rdPartyDir -Filter "*.dll" | Select-Object -ExpandProperty Name | Sort-Object

Write-Host "`nDone."
