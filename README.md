<p align="center">
  <img src="src/assets/qa-logo.png" alt="QuantumAnalyzer Logo" />
</p>

<h1 align="center">QuantumAnalyzer</h1>

<p align="center">
  <a href="https://github.com/aritraroy24/QuantumAnalyzer/releases"><img src="https://img.shields.io/github/v/release/aritraroy24/QuantumAnalyzer?display_name=tag" alt="GitHub Release"></a>
  <a href="https://github.com/aritraroy24/QuantumAnalyzer/issues"><img src="https://img.shields.io/github/issues/aritraroy24/QuantumAnalyzer?color=2ea44f" alt="GitHub Issues"></a>
  <img src="https://img.shields.io/badge/platform-Windows-0078D6" alt="Platform Windows">
  <img src="https://img.shields.io/badge/.NET-Framework%204.8-512BD4" alt=".NET Framework 4.8">
</p>

<p align="center" style="font-size:1.2em; font-weight:700;">
  Preview Gaussian, ORCA, and VASP results directly in Windows Explorer for a fast first look and quick presentation-ready images, without opening any software.
</p>

## Key Features

- Native Windows Shell Extension on `.NET Framework 4.8 (x64)`.
- Explorer thumbnails with the QuantumAnalyzer icon for supported extension-based files (`.log`, `.out`, `.gjf`, `.com`, `.inp`, `.xyz`, `.cube`, `.cub`, `.poscar`, `.contcar`).
- Preview Pane (`Alt+P`) support with format-aware visualization:
  - Molecule preview for Gaussian/ORCA input-output and XYZ (including multi-structure XYZ with frame navigation).
  - Crystal preview for `POSCAR`/`CONTCAR`.
  - Volumetric + isosurface preview for `.cube`, `.cub`, and `CHGCAR`.
  - OUTCAR ionic-step crystal and energy profile preview.
- Structured metadata panels for output files (general/model/energy style data blocks).
- Context-menu exports for supported formats:
  - Save Summary
  - Save Image
  - Save Structure (where supported)
  - Save Energy Profile
- Energy profile diagrams with interactive step selection for optimization workflows.

## Supported File Types

- Quantum chemistry: `.log`, `.out`, `.gjf`, `.com`, `.inp`, `.xyz`, `.cube`, `.cub`
- VASP: `POSCAR`, `CONTCAR`, `CHGCAR`, `OUTCAR`

## Demo

<p align="center">
  <video src="src/assets/demo.mp4" alt="QuantumAnalyzer Demo" controls width="100%"></video>
</p>

## Installation

### Option 1: MSI

Use the generated installer from:

- [dist/QuantumAnalyzer-0.0.1-x64.msi](dist/QuantumAnalyzer-0.0.1-x64.msi)

### Option 2: Build from Source

1. Open `QuantumAnalyzer.sln` in Visual Studio (x64, Release recommended).
2. Build solution.
3. Register/deploy shell extension as per project setup scripts/workflow.

## Development

- Installer files are in `installer/`.
- MSI build helper: `installer/build-msi.ps1`
- Release notes: `CHANGELOG.md`

## Contributing

Please read `CONTRIBUTING.md` before opening pull requests.

## Author

- Name: Aritra Roy
- GitHub: [@aritraroy24](https://github.com/aritraroy24)
- Email: [contact@aritraroy.live](mailto:contact@aritraroy.live)
- Website: [www.aritraroy.live](https://www.aritraroy.live)

---

Vibe-coded with ❤️ for the scientific community.
