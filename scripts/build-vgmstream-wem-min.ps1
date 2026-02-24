param(
    [string]$VsDevCmdPath = "D:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\Tools\VsDevCmd.bat",
    [string]$VgmstreamRoot = "F:\Workplace\Endfield\vgmstream",
    [string]$Project3rdPartyDir = "$PSScriptRoot\..\BydTools.Wwise\3rdParty",
    [string]$BuildDir = "",
    [string]$Generator = "Visual Studio 18 2026",
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

function Invoke-InVsDevCmd {
    param([string]$Command)

    $cmdLine = "call `"$VsDevCmdPath`" -arch=$Arch -host_arch=$HostArch && $Command"
    & cmd.exe /c $cmdLine
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed in VS Dev environment: $Command"
    }
}

if ([string]::IsNullOrWhiteSpace($BuildDir)) {
    $BuildDir = Join-Path $VgmstreamRoot "build-msvc-mindeps"
}

$Project3rdPartyDir = [System.IO.Path]::GetFullPath($Project3rdPartyDir)
$BuildDir = [System.IO.Path]::GetFullPath($BuildDir)

Assert-Path -Path $VsDevCmdPath -Description "VsDevCmd"
Assert-Path -Path $VgmstreamRoot -Description "vgmstream root"
Assert-Path -Path $Project3rdPartyDir -Description "BydTools 3rdParty directory"

$ffmpegPath = Join-Path $VgmstreamRoot "ext_libs"
Assert-Path -Path $ffmpegPath -Description "vgmstream ext_libs (FFmpeg path)"

Write-Host "== Configure vgmstream (WEM minimal profile) =="
Invoke-InVsDevCmd -Command @"
cmake -S "$VgmstreamRoot" -B "$BuildDir" -G "$Generator" -A $Arch -DBUILD_SHARED_LIBS=ON -DBUILD_CLI=OFF -DBUILD_FB2K=OFF -DBUILD_WINAMP=OFF -DBUILD_XMPLAY=OFF -DUSE_FFMPEG=ON -DFFMPEG_PATH="$ffmpegPath" -DUSE_MPEG=OFF -DUSE_G719=OFF -DUSE_ATRAC9=OFF -DUSE_CELT=OFF -DUSE_SPEEX=OFF -DCMAKE_C_FLAGS="/utf-8"
"@

Write-Host "== Build libvgmstream_shared ($Configuration) =="
Invoke-InVsDevCmd -Command "cmake --build `"$BuildDir`" --config $Configuration --target libvgmstream_shared"

$builtDll = Join-Path $BuildDir "src\$Configuration\libvgmstream.dll"
Assert-Path -Path $builtDll -Description "Built libvgmstream.dll"

Write-Host "== Deploy required DLLs to 3rdParty =="
Copy-Item -LiteralPath $builtDll -Destination (Join-Path $Project3rdPartyDir "libvgmstream.dll") -Force

# Keep these runtime dependencies for WEM minimal profile.
$requiredDlls = @(
    "libvgmstream.dll",
    "libvorbis.dll",
    "avcodec-vgmstream-59.dll",
    "avformat-vgmstream-59.dll",
    "avutil-vgmstream-57.dll"
)

if ($PruneUnusedDlls) {
    $pruneDlls = @(
        "libmpg123-0.dll",
        "libg719_decode.dll",
        "libatrac9.dll",
        "libcelt-0061.dll",
        "libcelt-0110.dll",
        "libspeex-1.dll",
        "swresample-vgmstream-4.dll"
    )

    foreach ($dll in $pruneDlls) {
        $path = Join-Path $Project3rdPartyDir $dll
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

Write-Host "== Final 3rdParty DLL set =="
Get-ChildItem -LiteralPath $Project3rdPartyDir -Filter "*.dll" | Select-Object -ExpandProperty Name | Sort-Object

Write-Host "`nDone."
