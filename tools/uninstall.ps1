#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the QuantumAnalyzer Shell Extension.

.DESCRIPTION
    Unregisters the COM server via regasm, removes approval registry entries,
    deletes the install directory, and restarts Explorer.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'   # Continue so all steps run even on error

$InstallDir  = Join-Path $env:ProgramFiles "QuantumAnalyzer"
$DllDest     = Join-Path $InstallDir "QuantumAnalyzer.ShellExtension.dll"
$RegAsm      = Join-Path $env:SystemRoot "Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
$ApprovedKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"
$ExtGuidThumb    = "{6F3A1234-5678-4ABC-8DEF-1A2B3C4D5E6F}"
$ExtGuidTip      = "{7E4B2345-6789-4BCD-9EF0-2B3C4D5E6F7A}"
$ExtGuidPrev     = "{8D5C3456-789A-4CDE-AEF1-3C4D5E6F7A8B}"
$ExtGuidMenu     = "{9E6D4567-89AB-4DEF-B0F1-4D5E6F7A8B9C}"

$Extensions = @('.log', '.out', '.gjf', '.com', '.inp', '.xyz', '.cube')

$IID_Thumbnail = "{E357FCCD-A995-4576-B01F-234630154E96}"
$IID_InfoTip   = "{00021500-0000-0000-C000-000000000046}"
$IID_Preview   = "{8895b1c6-b41f-4c1c-a562-0d564250836f}"

if (-not (Test-Path 'HKCR:')) {
    $null = New-PSDrive -Name HKCR -PSProvider Registry -Root HKEY_CLASSES_ROOT
}

# ── Helpers ──────────────────────────────────────────────────────────────────

function Unregister-ShellexEntries {
    param([string[]]$Exts)

    foreach ($ext in $Exts) {
        $base = "HKCR:\$ext\shellex"
        Remove-Item -Path "$base\$IID_Thumbnail"                      -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path "$base\$IID_InfoTip"                        -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path "$base\$IID_Preview"                        -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path "$base\ContextMenuHandlers\QuantumAnalyzer" -Recurse -Force -ErrorAction SilentlyContinue

        # Also clean up ProgID-based context menu entry (installed for .log/.out)
        $progId = try { (Get-Item "HKCR:\$ext").GetValue('') } catch { $null }
        if ($progId -and (Test-Path "HKCR:\$progId")) {
            Remove-Item -Path "HKCR:\$progId\shellex\ContextMenuHandlers\QuantumAnalyzer" -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Remove SystemFileAssociations context menu entries
    foreach ($path in @('.log', '.out', '.cube', '.xyz')) {
        Remove-Item -Path "HKCR:\SystemFileAssociations\$path\shellex\ContextMenuHandlers\QuantumAnalyzer" -Recurse -Force -ErrorAction SilentlyContinue
    }

    $phKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers"
    Remove-ItemProperty -Path $phKey -Name $ExtGuidPrev -Force -ErrorAction SilentlyContinue
}

# ── Unregister ───────────────────────────────────────────────────────────────

if (Test-Path $DllDest) {
    Write-Host "Unregistering COM server..." -ForegroundColor Cyan
    & $RegAsm /unregister "$DllDest"
} else {
    Write-Warning "DLL not found at $DllDest — skipping regasm unregister."
}

Write-Host "Removing shellex registry entries..." -ForegroundColor Cyan
Unregister-ShellexEntries -Exts $Extensions

Write-Host "Removing shell extension approvals from registry..." -ForegroundColor Cyan
foreach ($guid in @($ExtGuidThumb, $ExtGuidTip, $ExtGuidPrev, $ExtGuidMenu)) {
    try { Remove-ItemProperty -Path $ApprovedKey -Name $guid -ErrorAction SilentlyContinue }
    catch { }
}

Write-Host "Deleting install directory..." -ForegroundColor Cyan
if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

Write-Host "Restarting Windows Explorer..." -ForegroundColor Cyan
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Start-Process explorer

Write-Host ""
Write-Host "Uninstallation complete." -ForegroundColor Green
