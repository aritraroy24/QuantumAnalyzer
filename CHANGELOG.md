# Changelog

All notable changes to this project are documented in this file.

## [0.0.1] - 23-02-2026

### Added

- Windows Explorer shell extension for quantum chemistry and VASP workflows on `.NET Framework 4.8` (`x64`).
- Explorer thumbnail provider for molecular files: `.log`, `.out`, `.gjf`, `.com`, `.inp`, `.xyz`.
- Hover info tip handler with parsed metadata for Gaussian, ORCA, and VASP `OUTCAR` outputs.
- Preview pane handler (Alt+P) with format-aware routing:
  - Molecule preview for Gaussian/ORCA input-output and `.xyz`
  - Isosurface + molecule preview for `.cube`/`.cub`
  - Crystal structure preview for `POSCAR` and `CONTCAR` (with and without extensions)
  - Charge-density crystal + isosurface preview for `CHGCAR`
  - Ionic-step crystal + energy convergence preview for `OUTCAR`
- Context menu actions for supported formats:
  - Save Summary
  - Save Image (format-specific visualization dialogs)
  - Save Structure (OUTCAR final geometry)
  - Save Energy Profile (OUTCAR convergence graph)
- Parser pipeline and data model support for:
  - Gaussian outputs and inputs
  - ORCA outputs and inputs
  - XYZ structures (for both single and multiple structures)
  - Gaussian cube volumetric files
  - VASP POSCAR/CONTCAR, CHGCAR, and OUTCAR
- Custom rendering stack:
  - PCA best-angle projection
  - Covalent-radius bond detection
  - Arcball interactive rotation
  - Marching cubes isosurface extraction
  - Crystal lattice/vector overlays
  - Atom sprite cache for interactive performance

### Changed

- Preview-pane metadata presentation was redesigned into consistent in-panel column layouts for supported file types, replacing tooltip-first behavior.
- Multi-structure `.xyz` previews now default to the first frame and use slider-based frame navigation in preview.
- Gaussian/ORCA optimization previews were aligned with OUTCAR-style step navigation, including step indexing and interactive energy-profile selection.
- Bottom control layout in molecule/output preview was adjusted so navigation controls and BG control do not overlap.
- Crystal preview details were streamlined to avoid duplicate lattice-parameter rendering blocks.
- Energy-profile plotting alignment was refined for better axis/label readability.
- Gaussian/ORCA output previews now default to the final optimization step/frame (last step) in both structure and energy-profile navigation.
- `.cube` preview info panel was simplified by removing the extra `Data` field.
- Thumbnail generation was switched to a consistent QuantumAnalyzer icon-based thumbnail for supported extension-based file types.

### Fixed

- Restored and aligned OUTCAR image export availability with other output-preview workflows.
- Improved optimization-step handling in Gaussian/ORCA previews so step selection and profile interaction stay synchronized.
- Added `.cub` handling alongside `.cube` in preview, context-menu, and installer registration paths.
- Removed malformed/over-broad installer registry mappings (`.\shellex...` preview key and wildcard `*\shellex\ContextMenuHandlers`) in favor of scoped associations.

### Documentation

- Added root project documentation: `README.md` with feature overview, branding/demo placeholders, install notes, and author/contact details.
- Added contributor guide: `CONTRIBUTING.md`.
- Added GitHub issue templates for bug reports and feature requests in `.github/ISSUE_TEMPLATE/`.
- Added docs asset placeholders in `docs/assets/README.md` for logo and demo GIF.
