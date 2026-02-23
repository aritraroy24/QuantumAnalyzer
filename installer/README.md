# QuantumAnalyzer MSI Installer

This folder contains a WiX v4 setup for building an x64 MSI for `QuantumAnalyzer`.

## Prerequisites

- Windows with Administrator rights for install testing
- .NET Framework 4.8 build output from this project (`Release|x64`)
- WiX Toolset v4 CLI

Install WiX CLI and util extension:

```powershell
dotnet tool install --global wix
wix extension add WixToolset.Util.wixext
```

## Build

From repo root:

```powershell
.\installer\build-msi.ps1 -Version 0.0.1
```

Output MSI:

- `dist\QuantumAnalyzer-<version>-x64.msi`

## What this installer does

- Installs `QuantumAnalyzer.ShellExtension.dll` and `SharpShell.dll` to `Program Files\QuantumAnalyzer`
- Adds Shell Extensions Approved entries
- Adds file association shell keys used by your current scripts
- Calls `regasm.exe` during install and uninstall (deferred elevated custom actions)

## Notes

- This is a starter MSI scaffold. Validate on a clean VM before release.
- If WiX reports custom-action symbol errors (for `Wix4UtilCA_X64` / `WixQuietExec`), ensure the Util extension is installed and up to date.
- Consider code-signing both DLL and MSI before distribution.
