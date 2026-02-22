# Changelog

All notable changes to this project are documented in this file.

## [0.0.1] - 2026-02-22

### Added

- Windows Explorer shell extension for quantum chemistry and VASP workflows on `.NET Framework 4.8` (`x64`).
- Explorer thumbnail provider for molecular files: `.log`, `.out`, `.gjf`, `.com`, `.inp`, `.xyz`.
- Hover info tip handler with parsed metadata for Gaussian, ORCA, and VASP `OUTCAR` outputs.
- Preview pane handler (Alt+P) with format-aware routing:
  - Molecule preview for Gaussian/ORCA input-output and `.xyz`
  - Isosurface + molecule preview for `.cube`
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
