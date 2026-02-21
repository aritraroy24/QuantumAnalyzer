using System.Collections.Generic;

namespace QuantumAnalyzer.ShellExtension.Models
{
    public class Molecule
    {
        public List<Atom> Atoms { get; set; } = new List<Atom>();

        // Each bond is a pair of zero-based atom indices
        public List<(int A, int B)> Bonds { get; set; } = new List<(int, int)>();

        public bool HasGeometry => Atoms != null && Atoms.Count > 0;
    }
}
