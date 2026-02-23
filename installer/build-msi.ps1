[CmdletBinding()]
param(
    [string]$Version = "0.0.1",
    [string]$Configuration = "Release",
    [string]$BinDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if (-not $BinDir) {
    $BinDir = Join-Path $repoRoot "src\QuantumAnalyzer.ShellExtension\bin\x64\$Configuration\net48"
}

$mainDll = Join-Path $BinDir "QuantumAnalyzer.ShellExtension.dll"
$sharpShellDll = Join-Path $BinDir "SharpShell.dll"

if (-not (Test-Path $mainDll)) {
    throw "Missing build output: $mainDll. Build Release|x64 first."
}
if (-not (Test-Path $sharpShellDll)) {
    throw "Missing dependency: $sharpShellDll. Restore/build to generate it."
}

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX CLI not found. Install with: dotnet tool install --global wix"
}

$outDir = Join-Path $repoRoot "dist"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$wxsPath = Join-Path $scriptDir "QuantumAnalyzer.wxs"
$outMsi = Join-Path $outDir "QuantumAnalyzer-$Version-x64.msi"

$cmd = @(
    "build",
    "`"$wxsPath`"",
    "-arch", "x64",
    "-ext", "WixToolset.Util.wixext",
    "-d", "BinDir=$BinDir",
    "-d", "ProductVersion=$Version",
    "-o", "`"$outMsi`""
)

Write-Host "Building MSI..." -ForegroundColor Cyan
& wix @cmd
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE"
}

Write-Host "MSI created: $outMsi" -ForegroundColor Green
