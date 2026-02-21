using System;
using System.Collections.Generic;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Chemistry
{
    public static class BondDetector
    {
        // Additive tolerance (Angstroms) applied to the sum of covalent radii.
        // Matches Avogadro / OpenBabel standard: more accurate across all element combinations.
        private const double Tolerance = 0.45;

        public static List<(int A, int B)> Detect(List<Atom> atoms)
        {
            var bonds = new List<(int, int)>();
            if (atoms == null || atoms.Count < 2) return bonds;

            for (int i = 0; i < atoms.Count - 1; i++)
            {
                for (int j = i + 1; j < atoms.Count; j++)
                {
                    double ri = ElementData.GetCovalentRadius(atoms[i].Element);
                    double rj = ElementData.GetCovalentRadius(atoms[j].Element);
                    double threshold = ri + rj + Tolerance;

                    double dx = atoms[i].X - atoms[j].X;
                    double dy = atoms[i].Y - atoms[j].Y;
                    double dz = atoms[i].Z - atoms[j].Z;
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                    if (dist > 0.4 && dist <= threshold)
                        bonds.Add((i, j));
                }
            }

            return bonds;
        }
    }
}
