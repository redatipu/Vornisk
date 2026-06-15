#requires -Version 5.1
<#
.SYNOPSIS
    Publish the Vornisk CLI as a self-contained single-file Linux binary.
.DESCRIPTION
    Cross-compiles from any OS (the .NET SDK emits the target-RID binary). The output needs no
    .NET install on the Linux host. Default RID linux-x64; pass -Runtime linux-arm64 for ARM.
.EXAMPLE
    pwsh scripts/build-linux.ps1
    pwsh scripts/build-linux.ps1 -Runtime linux-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64')]
    [string]$Runtime = 'linux-x64',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$proj    = Join-Path $root 'src/VorniskCli/VorniskCli.csproj'
$outDir  = Join-Path $root "dist/$Runtime"

Write-Host "=== Vornisk CLI build ($Runtime) ===" -ForegroundColor Cyan
Write-Host "Project : $proj"
Write-Host "Output  : $outDir"
Write-Host ""

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

$bin = Join-Path $outDir 'vornisk'
if (Test-Path $bin) {
    $mb = [math]::Round((Get-Item $bin).Length / 1MB, 1)
    Write-Host ""
    Write-Host "=== Done: $bin  ($mb MB) ===" -ForegroundColor Green
    Write-Host "On the Linux host: chmod +x vornisk && ./vornisk --help"
} else {
    throw "Expected binary not found: $bin"
}
