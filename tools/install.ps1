#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the QuantumAnalyzer Shell Extension.

.DESCRIPTION
    Copies the compiled DLL to Program Files, registers it via regasm,
    approves it in the shell extension registry key, and restarts Explorer.

    Shell-handler associations (.log / .out) are written directly under each
    extension's shellex key rather than through a ProgID.  This bypasses the
    InvalidOperationException that SharpShell throws when it tries to create
    the ProgID 'log.1' for '.log' - a name already owned by "log Application".

.NOTES
    Must be run as Administrator.
    Build the project in Release|x64 before running this script.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------- Paths ------------------------------------------------------------

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$DllSource   = Join-Path $ProjectRoot "src\QuantumAnalyzer.ShellExtension\bin\x64\Release\net48\QuantumAnalyzer.ShellExtension.dll"
$InstallDir  = Join-Path $env:ProgramFiles "QuantumAnalyzer"
$DllDest     = Join-Path $InstallDir "QuantumAnalyzer.ShellExtension.dll"

# regasm from .NET Framework 4.8
$RegAsm = Join-Path $env:SystemRoot "Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

# Shell Extensions Approved registry key (required for non-admin shell extensions)
$ApprovedKey   = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"
$ExtGuidThumb    = "{6F3A1234-5678-4ABC-8DEF-1A2B3C4D5E6F}"
$ExtGuidTip      = "{7E4B2345-6789-4BCD-9EF0-2B3C4D5E6F7A}"
$ExtGuidPrev     = "{8D5C3456-789A-4CDE-AEF1-3C4D5E6F7A8B}"
$ExtGuidMenu     = "{9E6D4567-89AB-4DEF-B0F1-4D5E6F7A8B9C}"

# Shell-interface GUIDs (Windows-defined, do not change)
$IID_Thumbnail = "{E357FCCD-A995-4576-B01F-234630154E96}"
$IID_InfoTip   = "{00021500-0000-0000-C000-000000000046}"
$IID_Preview   = "{8895b1c6-b41f-4c1c-a562-0d564250836f}"

$Extensions = @('.log', '.out', '.gjf', '.com', '.inp', '.xyz', '.cube')
if (-not (Test-Path 'HKCR:')) {
    $null = New-PSDrive -Name HKCR -PSProvider Registry -Root HKEY_CLASSES_ROOT
}

# ---------- Helpers: write / remove shellex entries under the extension -------
#
# Windows Explorer checks HKCR\.<ext>\shellex\<IID> in addition to the ProgID
# path.  Registering here avoids creating or modifying any ProgID and therefore
# sidesteps the 'log.1' conflict entirely.

function Register-ShellexEntries {
    param([string[]]$Exts)

    foreach ($ext in $Exts) {
        $base = "HKCR:\$ext\shellex"

        # Thumbnail Provider
        $key = New-Item -Path "$base\$IID_Thumbnail" -Force
        $key.SetValue('', $ExtGuidThumb)

        # Info Tip Handler
        $key = New-Item -Path "$base\$IID_InfoTip" -Force
        $key.SetValue('', $ExtGuidTip)

        # Preview Handler
        $key = New-Item -Path "$base\$IID_Preview" -Force
        $key.SetValue('', $ExtGuidPrev)
    }

    # Context Menu Handler — output files (.log / .out) and cube files (.cube)
    # Registered separately so it does not appear on input / structure files.
    #
    # Windows Explorer resolves ContextMenuHandlers through the ProgID chain.
    # For extensions with no default ProgID (e.g. .log on systems where VSCode
    # registers it only in OpenWithProgids), Explorer falls back to
    # SystemFileAssociations\.<ext>.  We register in all four places to be safe:
    #   1. Extension shellex key (belt-and-suspenders)
    #   2. SystemFileAssociations\.<ext>  (reliable fallback — always checked)
    #   3. Default ProgID  (e.g. txtfile if .log has one)
    #   4. Every ProgID in OpenWithProgids  (e.g. VSCode.log)
    foreach ($path in @('.log', '.out')) {
        # 1. Extension shellex key
        $key = New-Item -Path "HKCR:\$path\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
        $key.SetValue('', $ExtGuidMenu)

        # 2. SystemFileAssociations — checked by Explorer when there is no default ProgID
        $key = New-Item -Path "HKCR:\SystemFileAssociations\$path\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
        $key.SetValue('', $ExtGuidMenu)

        # 3. Default ProgID (may be empty on many machines)
        $progId = try { (Get-Item "HKCR:\$path").GetValue('') } catch { $null }
        if ($progId -and (Test-Path "HKCR:\$progId")) {
            $key = New-Item -Path "HKCR:\$progId\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
            $key.SetValue('', $ExtGuidMenu)
        }

        # 4. OpenWithProgids (e.g. VSCode.log, Applications\code.exe)
        $owpPath = "HKCR:\$path\OpenWithProgids"
        if (Test-Path $owpPath) {
            foreach ($progIdName in (Get-Item -Path $owpPath).GetValueNames()) {
                if ($progIdName -and (Test-Path "HKCR:\$progIdName")) {
                    $key = New-Item -Path "HKCR:\$progIdName\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
                    $key.SetValue('', $ExtGuidMenu)
                }
            }
        }
    }

    # Cube + XYZ context menus — same 4-path strategy, same GUID as .log/.out handler
    foreach ($path in @('.cube', '.xyz')) {
        # 1. Extension shellex key
        $key = New-Item -Path "HKCR:\$path\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
        $key.SetValue('', $ExtGuidMenu)

        # 2. SystemFileAssociations
        $key = New-Item -Path "HKCR:\SystemFileAssociations\$path\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
        $key.SetValue('', $ExtGuidMenu)

        # 3. Default ProgID
        $progId = try { (Get-Item "HKCR:\$path").GetValue('') } catch { $null }
        if ($progId -and (Test-Path "HKCR:\$progId")) {
            $key = New-Item -Path "HKCR:\$progId\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
            $key.SetValue('', $ExtGuidMenu)
        }

        # 4. OpenWithProgids
        $owpPath = "HKCR:\$path\OpenWithProgids"
        if (Test-Path $owpPath) {
            foreach ($progIdName in (Get-Item -Path $owpPath).GetValueNames()) {
                if ($progIdName -and (Test-Path "HKCR:\$progIdName")) {
                    $key = New-Item -Path "HKCR:\$progIdName\shellex\ContextMenuHandlers\QuantumAnalyzer" -Force
                    $key.SetValue('', $ExtGuidMenu)
                }
            }
        }
    }

    # Preview handlers must also appear in this global list
    $phKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers"
    if (-not (Test-Path $phKey)) { $null = New-Item -Path $phKey -Force }
    Set-ItemProperty -Path $phKey -Name $ExtGuidPrev -Value "QuantumAnalyzer Preview Handler" -ErrorAction SilentlyContinue
}

function Unregister-ShellexEntries {
    param([string[]]$Exts)

    foreach ($ext in $Exts) {
        $base = "HKCR:\$ext\shellex"
        Remove-Item -Path "$base\$IID_Thumbnail"                      -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path "$base\$IID_InfoTip"                        -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path "$base\$IID_Preview"                        -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path "$base\ContextMenuHandlers\QuantumAnalyzer" -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Remove SystemFileAssociations context menu entries
    foreach ($path in @('.log', '.out', '.cube', '.xyz')) {
        Remove-Item -Path "HKCR:\SystemFileAssociations\$path\shellex\ContextMenuHandlers\QuantumAnalyzer" -Recurse -Force -ErrorAction SilentlyContinue
    }

    $phKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers"
    Remove-ItemProperty -Path $phKey -Name $ExtGuidPrev -Force -ErrorAction SilentlyContinue
}

# ---------- Pre-flight checks ------------------------------------------------

if (-not (Test-Path $DllSource)) {
    Write-Error "DLL not found at: $DllSource`nPlease build the project in Release|x64 first."
    exit 1
}

if (-not (Test-Path $RegAsm)) {
    Write-Error "regasm.exe not found at: $RegAsm`nPlease install .NET Framework 4.8 Developer Pack."
    exit 1
}

# ---------- Unregister previous version --------------------------------------
#
# Unregistration is best-effort: SharpShell's UnregisterFunction throws the same
# ProgID conflict as RegisterFunction.  Suppress ALL output (stdout + stderr) so
# the error does not surface and stop the script.

if (Test-Path $DllDest) {
    Write-Host "Unregistering previous version to release COM lock..." -ForegroundColor Cyan
    # try/catch is required: with $ErrorActionPreference='Stop', PowerShell 5
    # throws on NativeCommandError (stderr) before 2>&1 | Out-Null can absorb it.
    try { & $RegAsm /unregister "$DllDest" 2>&1 | Out-Null } catch { }
    # Also remove any manually-written shellex entries from a prior install
    Unregister-ShellexEntries -Exts $Extensions
}

# ---------- Kill Explorer / COM Surrogate ------------------------------------

Write-Host "Stopping Explorer and COM Surrogate..." -ForegroundColor Cyan
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Stop-Process -Name dllhost  -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 3000

# ---------- Copy DLL ---------------------------------------------------------

Write-Host "Creating install directory: $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Write-Host "Copying DLL..." -ForegroundColor Cyan
$copied = $false
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Copy-Item -Path $DllSource -Destination $DllDest -Force
        $copied = $true
        break
    } catch [System.IO.IOException] {
        if ($attempt -eq 5) { throw }
        Write-Warning "DLL still locked (attempt $attempt/5) - retrying in 2 s..."
        Start-Sleep -Seconds 2
    }
}
if (-not $copied) {
    Write-Error "Could not copy DLL after 5 attempts. Close any app holding the file and retry."
    exit 1
}

# Copy SharpShell.dll if it lives in the same output folder
$SharpShellSource = Join-Path (Split-Path -Parent $DllSource) "SharpShell.dll"
if (Test-Path $SharpShellSource) {
    Copy-Item -Path $SharpShellSource -Destination $InstallDir -Force
    Write-Host "Copied SharpShell.dll" -ForegroundColor Cyan
}

# ---------- Register COM server ----------------------------------------------
#
# Exit code semantics:
#   0   - success
#   100 - success with warnings (e.g. no strong name) - treat as OK
#   1   - failure
#
# RegAsm error RA0000 means SharpShell's RegisterFunction threw an exception
# (the .log ProgID conflict).  The COM server classes in HKCR\CLSID\ ARE
# registered correctly before RegisterFunction is called, so RA0000 is safe
# to treat as a non-fatal warning - we register the file associations manually.

Write-Host "Registering COM server via regasm..." -ForegroundColor Cyan
$regasmOut = @()
try {
    $regasmOut = & $RegAsm /codebase "$DllDest" 2>&1
} catch {
    # NativeCommandError thrown by $ErrorActionPreference='Stop' on stderr output.
    # Capture the message so we can still distinguish RA0000 from real failures.
    $regasmOut = @($_.ToString())
}

$ra0000Only = @($regasmOut | Where-Object { $_ -match '\berror\b' -and $_ -notmatch 'RA0000' }).Count -eq 0

if ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq 100) {
    if ($LASTEXITCODE -eq 100) {
        Write-Warning "regasm: exit 100 (no strong name) - normal for dev builds."
    }
} elseif ($ra0000Only) {
    Write-Warning "regasm: RA0000 (ProgID conflict on .log/.out) - registering shell handlers manually instead."
} else {
    # A genuine regasm failure unrelated to the ProgID conflict
    $regasmOut | ForEach-Object { Write-Host $_ }
    Write-Error "regasm failed with exit code $LASTEXITCODE"
    exit 1
}

# ---------- Register shell handlers ------------------------------------------
#
# Written directly under the extension paths (HKCR\.<ext>\shellex\<IID>).
# Windows Explorer checks this location directly, so no ProgID is needed.

Write-Host "Registering shell extension handlers in registry..." -ForegroundColor Cyan
Register-ShellexEntries -Exts $Extensions

# ---------- Approve shell extensions -----------------------------------------

Write-Host "Approving shell extensions in registry..." -ForegroundColor Cyan
Set-ItemProperty -Path $ApprovedKey -Name $ExtGuidThumb    -Value "QuantumAnalyzer Thumbnail Provider"
Set-ItemProperty -Path $ApprovedKey -Name $ExtGuidTip      -Value "QuantumAnalyzer Info Tip Handler"
Set-ItemProperty -Path $ApprovedKey -Name $ExtGuidPrev     -Value "QuantumAnalyzer Preview Handler"
Set-ItemProperty -Path $ApprovedKey -Name $ExtGuidMenu     -Value "QuantumAnalyzer Context Menu"

# ---------- Restart Explorer -------------------------------------------------

Write-Host "Restarting Windows Explorer..." -ForegroundColor Cyan
Start-Process explorer

Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green

# ---------- Verify key entries -----------------------------------------------

Write-Host ""
Write-Host "Verification:" -ForegroundColor Yellow
$checks = @(
    @{ Label = "COM CLSID (context menu)";         Path = "HKCR:\CLSID\$ExtGuidMenu" },
    @{ Label = ".log shellex context menu";         Path = "HKCR:\.log\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = ".out shellex context menu";         Path = "HKCR:\.out\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = ".cube shellex context menu";        Path = "HKCR:\.cube\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = ".xyz shellex context menu";         Path = "HKCR:\.xyz\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = "SFA .log context menu";             Path = "HKCR:\SystemFileAssociations\.log\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = "SFA .out context menu";             Path = "HKCR:\SystemFileAssociations\.out\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = "SFA .cube context menu";            Path = "HKCR:\SystemFileAssociations\.cube\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = "SFA .xyz context menu";             Path = "HKCR:\SystemFileAssociations\.xyz\shellex\ContextMenuHandlers\QuantumAnalyzer" },
    @{ Label = "Approved (context menu)";           Path = $ApprovedKey; ValueName = $ExtGuidMenu },
    @{ Label = ".log shellex preview";              Path = "HKCR:\.log\shellex\$IID_Preview" },
    @{ Label = ".out shellex preview";              Path = "HKCR:\.out\shellex\$IID_Preview" },
    @{ Label = ".cube shellex preview";             Path = "HKCR:\.cube\shellex\$IID_Preview" }
)
foreach ($c in $checks) {
    $ok = if ($c.ContainsKey('ValueName')) {
        try { $null -ne (Get-ItemProperty -Path $c.Path -Name $c.ValueName -ErrorAction Stop) } catch { $false }
    } else {
        Test-Path $c.Path
    }
    $status = if ($ok) { "[OK]  " } else { "[MISS]" }
    $color  = if ($ok) { 'Green'  } else { 'Red'   }
    Write-Host "  $status $($c.Label)" -ForegroundColor $color
}

Write-Host ""
Write-Host "On Windows 11: right-click then choose 'Show more options' → 'QuantumAnalyzer' to see the submenu options." -ForegroundColor DarkCyan
