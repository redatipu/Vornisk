#requires -Version 5.1
<#
.SYNOPSIS
    Publish the Vornisk Avalonia GUI as a self-contained single-file Linux binary.
.DESCRIPTION
    Cross-compiles the desktop GUI (`vornisk-gui`) from any OS. The Linux host needs a desktop
    environment with X11 or Wayland + the usual GUI native libs (libX11/libICE/libSM, fontconfig,
    libGL). Default RID linux-x64; pass -Runtime linux-arm64 for ARM.
.EXAMPLE
    pwsh scripts/build-linux-gui.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64')]
    [string]$Runtime = 'linux-x64',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$proj   = Join-Path $root 'src/VorniskGui/VorniskGui.csproj'
$outDir = Join-Path $root "dist/$Runtime-gui"

Write-Host "=== Vornisk GUI build ($Runtime) ===" -ForegroundColor Cyan
dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:PublishTrimmed=false `
    --output $outDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$bin = Join-Path $outDir 'vornisk-gui'
if (Test-Path $bin) {
    $mb = [math]::Round((Get-Item $bin).Length / 1MB, 1)
    Write-Host "=== Done: $bin  ($mb MB) ===" -ForegroundColor Green
    Write-Host "On the Linux host: chmod +x vornisk-gui && ./vornisk-gui   (needs X11/Wayland desktop)"
} else {
    throw "Expected binary not found: $bin"
}
