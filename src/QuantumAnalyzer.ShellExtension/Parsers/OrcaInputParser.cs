using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for ORCA input files (.inp).
    /// Detects via ! keyword lines or *xyz / *int coordinate block markers.
    /// </summary>
    public class OrcaInputParser : IQuantumParser
    {
        public bool CanParse(string[] firstLines)
        {
            foreach (string line in firstLines)
            {
                if (line == null) continue;
                string trimmed = line.Trim();
                // ! keyword line (must have content after the !)
                if (trimmed.StartsWith("!") && trimmed.Length > 1) return true;
                // Coordinate block start
                if (trimmed.StartsWith("*xyz",  StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("*int",  StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("*xyzf", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public ParseResult Parse(TextReader reader)
        {
            var summary = new QuantumSummary { Software = SoftwareType.Orca };
            var molecule = new Molecule();

            var keywordLines = new List<string>();
            bool inAtoms = false;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();

                // ── Keyword lines ──────────────────────────────────────
                if (trimmed.StartsWith("!"))
                {
                    keywordLines.Add(trimmed);
                    continue;
                }

                // ── Coordinate block: *xyz charge mult ─────────────────
                if (!inAtoms && (trimmed.StartsWith("*xyz",  StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("*int",  StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("*xyzf", StringComparison.OrdinalIgnoreCase)))
                {
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[1], out int charge) &&
                        int.TryParse(parts[2], out int mult))
                    {
                        summary.Charge = charge;
                        summary.Spin = MultiplicityToSpinName(mult);
                    }
                    inAtoms = true;
                    continue;
                }

                // ── Atom lines inside the coordinate block ─────────────
                if (inAtoms)
                {
                    if (trimmed == "*") { inAtoms = false; continue; } // end of block

                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;

                    string sym = char.ToUpperInvariant(parts[0][0]) +
                                 (parts[0].Length > 1 ? parts[0].Substring(1).ToLowerInvariant() : "");

                    if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;
                    if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;
                    if (!double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double z)) continue;

                    molecule.Atoms.Add(new Atom(sym, x, y, z));
                }
            }

            if (molecule.Atoms.Count == 0 && keywordLines.Count == 0) return null;

            // Parse method/basis/calctype from ! keyword lines
            ParseKeywordLines(keywordLines, summary);

            if (molecule.Atoms.Count > 0)
                molecule.Bonds = BondDetector.Detect(molecule.Atoms);

            if (summary.CalcType == null) summary.CalcType = "SP";

            return new ParseResult { Summary = summary, Molecule = molecule };
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        private static void ParseKeywordLines(List<string> lines, QuantumSummary s)
        {
            bool hasFreq = false, hasOpt = false;

            foreach (string line in lines)
            {
                string tokens = line.TrimStart('!').Trim();
                string upper = tokens.ToUpperInvariant();

                if (upper.Contains("FREQ") || upper.Contains("NUMFREQ") || upper.Contains("ANFREQ"))
                    hasFreq = true;
                if (upper.Contains("OPT") || upper.Contains("TIGHTOPT") || upper.Contains("VERYTIGHTOPT"))
                    hasOpt = true;

                if (s.Method != null) continue;

                var parts = tokens.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                // Skip warning/error/notification lines
                string firstTok = parts[0].ToUpperInvariant();
                if (firstTok == "WARNING" || firstTok == "ERROR" || firstTok == "NOTE"
                    || firstTok == "CAUTION") continue;

                // Handle "B3LYP/def2-TZVP" combined token
                if (parts[0].Contains("/"))
                {
                    var split = parts[0].Split(new[] { '/' }, 2);
                    s.Method   = split[0].ToUpperInvariant();
                    s.BasisSet = split[1];
                }
                else
                {
                    s.Method = parts[0].ToUpperInvariant();
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (IsKeyword(parts[i])) continue;
                        s.BasisSet = parts[i];
                        break;
                    }
                }
            }

            if (hasFreq && hasOpt) s.CalcType = "OPT+FREQ";
            else if (hasFreq)     s.CalcType = "FREQ";
            else if (hasOpt)      s.CalcType = "OPT";

            if (s.Solvation == null) s.Solvation = "None";
        }

        private static bool IsKeyword(string tok)
        {
            string upper = tok.ToUpperInvariant();
            switch (upper)
            {
                case "FREQ": case "OPT": case "SP":
                case "TIGHTSCF": case "VERYTIGHTSCF": case "NORMALSCF": case "LOOSESCF":
                case "DEFGRID1": case "DEFGRID2": case "DEFGRID3":
                case "RIJCOSX": case "NORI": case "RI":
                case "LARGEPRINT": case "MINIPRINT": case "NOPRINT":
                case "ENGRAD": case "NUMGRAD": case "NUMFREQ": case "ANFREQ":
                case "TIGHTOPT": case "VERYTIGHTOPT": case "SLOWCONV":
                case "NBO": case "NPA":
                    return true;
                default:
                    return upper.StartsWith("GRID") || upper.StartsWith("AUX");
            }
        }

        private static string MultiplicityToSpinName(int mult)
        {
            switch (mult)
            {
                case 1: return "Singlet";
                case 2: return "Doublet";
                case 3: return "Triplet";
                case 4: return "Quartet";
                case 5: return "Quintet";
                case 6: return "Sextet";
                case 7: return "Septet";
                default: return $"Mult={mult}";
            }
        }
    }
}
