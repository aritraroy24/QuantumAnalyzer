using System.Collections.Generic;

namespace QuantumAnalyzer.ShellExtension.Models
{
    /// <summary>
    /// Per-ionic-step data from a VASP OUTCAR file.
    /// Used by OutcarPreviewControl to build the energy convergence graph
    /// and to reconstruct the molecular geometry at each ionic step.
    /// </summary>
    public class OutcarData
    {
        /// <summary>
        /// Cartesian positions [Å] for each ionic step.
        /// StepPositions[i][j] = { x, y, z } for atom j at step i (0-based).
        /// </summary>
        public List<double[][]> StepPositions { get; set; } = new List<double[][]>();

        /// <summary>
        /// energy(sigma->0) value [eV] for each ionic step (same indexing as StepPositions).
        /// May be shorter than StepPositions for incomplete runs.
        /// </summary>
        public List<double> StepEnergies { get; set; } = new List<double>();

        /// <summary>
        /// Element symbol for each atom (same ordering across all steps).
        /// </summary>
        public string[] AtomElements { get; set; }

        /// <summary>
        /// Lattice vectors in Angstroms from the last parsed "direct lattice vectors" block.
        /// Null if not found (unusual for VASP OUTCAR).
        /// </summary>
        public double[] LatticeA { get; set; }
        public double[] LatticeB { get; set; }
        public double[] LatticeC { get; set; }
    }
}
