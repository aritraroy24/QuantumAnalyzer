using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for VASP POSCAR and CONTCAR files.
    ///
    /// POSCAR/CONTCAR format:
    ///   Line 1: System name (comment)
    ///   Line 2: Universal scaling factor (single float)
    ///   Lines 3–5: Lattice vectors a, b, c (3 floats each)
    ///   Line 6: Element symbols (VASP5) OR atom counts (VASP4)
    ///   Line 7: Atom counts (VASP5) OR coordinate mode line (VASP4 with Selective dynamics)
    ///   Optional: "Selective dynamics" line
    ///   Coordinate mode: "Direct" (fractional) or "Cartesian"
    ///   Then: atom coordinates
    /// </summary>
    public class PoscarParser : IQuantumParser
    {
        public bool CanParse(string[] firstLines)
        {
            // Require at least 7 lines
            if (firstLines == null || firstLines.Length < 7) return false;

            // Line 2 (index 1): single positive float (scaling factor)
            string[] l1 = Split(firstLines[1]);
            if (l1.Length != 1) return false;
            if (!TryFloat(l1[0], out double scale) || scale <= 0) return false;

            // Lines 3–5 (indices 2–4): exactly 3 floats each (lattice vectors)
            for (int i = 2; i <= 4; i++)
            {
                string[] parts = Split(firstLines[i]);
                if (parts.Length != 3) return false;
                if (!TryFloat(parts[0], out _) || !TryFloat(parts[1], out _) || !TryFloat(parts[2], out _))
                    return false;
            }

            // Line 6 (index 5): at least 1 token
            string[] l5 = Split(firstLines[5]);
            if (l5.Length < 1) return false;

            return true;
        }

        public ParseResult Parse(TextReader reader)
        {
            try
            {
                return DoParse(reader);
            }
            catch
            {
                return null;
            }
        }

        private ParseResult DoParse(TextReader reader)
        {
            // Line 1: system name
            string systemName = reader.ReadLine()?.Trim() ?? "VASP Structure";

            // Line 2: scaling factor
            string scaleLine = reader.ReadLine();
            if (!TryFloat(Split(scaleLine)[0], out double scale) || scale <= 0) return null;

            // Lines 3–5: lattice vectors
            double[] va = ReadVector(reader, scale);
            double[] vb = ReadVector(reader, scale);
            double[] vc = ReadVector(reader, scale);
            if (va == null || vb == null || vc == null) return null;

            // Line 6: element symbols (VASP5) or counts (VASP4)
            string line6 = reader.ReadLine()?.Trim() ?? "";
            string[] tokens6 = Split(line6);
            if (tokens6.Length == 0) return null;

            string[] elementSymbols;
            int[]    atomCounts;
            string   line7;

            bool isVasp5 = !int.TryParse(tokens6[0], out _);
            if (isVasp5)
            {
                // VASP5: line 6 = element symbols, line 7 = counts
                elementSymbols = tokens6;
                line7 = reader.ReadLine()?.Trim() ?? "";
                string[] tokens7 = Split(line7);
                atomCounts = new int[tokens7.Length];
                for (int i = 0; i < tokens7.Length; i++)
                    if (!int.TryParse(tokens7[i], out atomCounts[i]) || atomCounts[i] < 0) return null;
                if (atomCounts.Length != elementSymbols.Length) return null;
                // Advance to the next line (Selective dynamics / Direct / Cartesian).
                // For VASP4 this is done inside the else branch; for VASP5 we need
                // one more ReadLine() because line7 held the atom counts, not the mode.
                line7 = reader.ReadLine()?.Trim() ?? "";
            }
            else
            {
                // VASP4: line 6 = counts, elements unknown
                atomCounts     = new int[tokens6.Length];
                elementSymbols = new string[tokens6.Length];
                for (int i = 0; i < tokens6.Length; i++)
                {
                    if (!int.TryParse(tokens6[i], out atomCounts[i]) || atomCounts[i] < 0) return null;
                    elementSymbols[i] = "X";
                }
                line7 = reader.ReadLine()?.Trim() ?? "";
            }

            // Skip optional "Selective dynamics" line
            string coordModeLine = line7;
            if (coordModeLine.Length > 0 && (coordModeLine[0] == 'S' || coordModeLine[0] == 's'))
                coordModeLine = reader.ReadLine()?.Trim() ?? "";

            // Coordinate mode: Direct (fractional) or Cartesian
            bool direct = coordModeLine.Length == 0 || coordModeLine[0] == 'D' || coordModeLine[0] == 'd';

            // Build lattice cell
            var crystal = new LatticeCell
            {
                SystemName = systemName,
                VectorA    = va,
                VectorB    = vb,
                VectorC    = vc,
            };

            // Read atom coordinates
            int totalAtoms = 0;
            foreach (int c in atomCounts) totalAtoms += c;

            int atomIndex = 0;
            for (int elemIdx = 0; elemIdx < elementSymbols.Length; elemIdx++)
            {
                string sym = NormalizeElement(elementSymbols[elemIdx]);
                for (int k = 0; k < atomCounts[elemIdx]; k++)
                {
                    string coordLine = reader.ReadLine();
                    if (coordLine == null) return null;
                    string[] parts = Split(coordLine);
                    if (parts.Length < 3) return null;

                    if (!TryFloat(parts[0], out double p0) ||
                        !TryFloat(parts[1], out double p1) ||
                        !TryFloat(parts[2], out double p2)) return null;

                    double x, y, z;
                    if (direct)
                    {
                        // Fractional → Cartesian
                        x = p0*va[0] + p1*vb[0] + p2*vc[0];
                        y = p0*va[1] + p1*vb[1] + p2*vc[1];
                        z = p0*va[2] + p1*vb[2] + p2*vc[2];
                    }
                    else
                    {
                        // Already Cartesian (scale factor was applied to vectors only)
                        x = p0;
                        y = p1;
                        z = p2;
                    }

                    crystal.UnitCellAtoms.Add((sym, x, y, z));
                    atomIndex++;
                }
            }

            if (crystal.UnitCellAtoms.Count == 0) return null;

            // Build Molecule for 1×1×1 unit cell
            var mol = new Molecule();
            mol.Atoms = crystal.UnitCellAtoms
                .Select(a => new Atom(a.Element, a.X, a.Y, a.Z))
                .ToList();
            mol.Bonds = BondDetector.Detect(mol.Atoms);

            // Atom counts for summary
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var atom in mol.Atoms)
            {
                if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                else counts[atom.Element] = 1;
            }

            var summary = new QuantumSummary
            {
                Software          = SoftwareType.VASP,
                CalcType          = "VASP Structure",
                NormalTermination = true,
                AtomCounts        = counts,
            };

            return new ParseResult { Summary = summary, Molecule = mol, CrystalData = crystal };
        }

        // ──────────────────────────────────────────────────────────────────

        private static double[] ReadVector(TextReader reader, double scale)
        {
            string line = reader.ReadLine();
            if (line == null) return null;
            string[] parts = Split(line);
            if (parts.Length < 3) return null;
            if (!TryFloat(parts[0], out double x) ||
                !TryFloat(parts[1], out double y) ||
                !TryFloat(parts[2], out double z)) return null;
            return new double[] { x * scale, y * scale, z * scale };
        }

        private static string[] Split(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return Array.Empty<string>();
            return line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool TryFloat(string s, out double val)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        private static string NormalizeElement(string sym)
        {
            if (string.IsNullOrEmpty(sym)) return "X";
            return char.ToUpperInvariant(sym[0]) +
                   (sym.Length > 1 ? sym.Substring(1).ToLowerInvariant() : "");
        }
    }
}
