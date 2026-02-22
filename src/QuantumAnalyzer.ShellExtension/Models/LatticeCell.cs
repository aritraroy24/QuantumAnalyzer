using System;
using System.Collections.Generic;

namespace QuantumAnalyzer.ShellExtension.Models
{
    public class LatticeCell
    {
        public string SystemName { get; set; }

        // Lattice vectors in Angstroms (scaling factor already applied)
        public double[] VectorA { get; set; }  // [3]
        public double[] VectorB { get; set; }  // [3]
        public double[] VectorC { get; set; }  // [3]

        // Computed lattice parameters
        public double LengthA => Math.Sqrt(VectorA[0]*VectorA[0] + VectorA[1]*VectorA[1] + VectorA[2]*VectorA[2]);
        public double LengthB => Math.Sqrt(VectorB[0]*VectorB[0] + VectorB[1]*VectorB[1] + VectorB[2]*VectorB[2]);
        public double LengthC => Math.Sqrt(VectorC[0]*VectorC[0] + VectorC[1]*VectorC[1] + VectorC[2]*VectorC[2]);

        // Atom positions in Cartesian Angstroms for the 1×1×1 unit cell
        // (fractional coords are converted to Cartesian during parsing)
        public List<(string Element, double X, double Y, double Z)> UnitCellAtoms { get; set; }
            = new List<(string, double, double, double)>();
    }
}
