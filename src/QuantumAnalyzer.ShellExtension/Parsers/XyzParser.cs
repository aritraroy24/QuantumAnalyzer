using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for XYZ coordinate files (.xyz).
    /// Format: first line is atom count, second line is comment, then "symbol x y z" rows.
    /// </summary>
    public class XyzParser : IQuantumParser
    {
        public bool CanParse(string[] firstLines)
        {
            if (firstLines.Length < 3) return false;
            // First line must be a positive integer (atom count)
            string first = firstLines[0]?.Trim();
            if (!int.TryParse(first, out int atomCount) || atomCount <= 0) return false;
            // Third line should look like an atom coordinate row: symbol x y z
            string third = firstLines[2]?.Trim();
            if (string.IsNullOrEmpty(third)) return false;
            var parts = third.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;
            // First token should be an element symbol (letters only, 1-2 chars)
            if (!Regex.IsMatch(parts[0], @"^[A-Za-z]{1,2}$")) return false;
            // Remaining three should be numbers
            return double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out _) &&
                   double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out _) &&
                   double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        public ParseResult Parse(TextReader reader)
        {
            var molecule = new Molecule();
            var summary = new QuantumSummary
            {
                Software = SoftwareType.Structure,
                CalcType = "Structure",
            };

            string line = reader.ReadLine(); // atom count line — skip
            reader.ReadLine();               // comment line — skip

            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (!Regex.IsMatch(parts[0], @"^[A-Za-z]{1,2}$")) continue;
                if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;
                if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;
                if (!double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double z)) continue;

                // Normalise symbol to Title case
                string sym = char.ToUpperInvariant(parts[0][0]) +
                             (parts[0].Length > 1 ? parts[0].Substring(1).ToLowerInvariant() : "");
                molecule.Atoms.Add(new Atom(sym, x, y, z));
            }

            if (molecule.Atoms.Count == 0) return null;

            molecule.Bonds = BondDetector.Detect(molecule.Atoms);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var atom in molecule.Atoms)
            {
                if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                else counts[atom.Element] = 1;
            }
            summary.AtomCounts = counts;

            return new ParseResult { Summary = summary, Molecule = molecule };
        }
    }
}
