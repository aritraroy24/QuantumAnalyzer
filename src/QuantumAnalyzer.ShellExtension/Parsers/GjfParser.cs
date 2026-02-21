using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for Gaussian input files (.gjf, .com).
    /// Detects via %link0 directives or # route card.
    /// </summary>
    public class GjfParser : IQuantumParser
    {
        public bool CanParse(string[] firstLines)
        {
            foreach (string line in firstLines)
            {
                if (line == null) continue;
                string trimmed = line.TrimStart();
                // Link0 directives
                if (trimmed.StartsWith("%chk=",  StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("%mem=",  StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("%nproc", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("%rwf=",  StringComparison.OrdinalIgnoreCase)) return true;
                // Route card
                if (trimmed.StartsWith("#P ", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("#N ", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("#T ", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("#p ", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("#n ", StringComparison.OrdinalIgnoreCase)) return true;
                if (trimmed.StartsWith("#t ", StringComparison.OrdinalIgnoreCase)) return true;
                // Route card with no level keyword (just #)
                if (trimmed == "#" || (trimmed.StartsWith("#") && trimmed.Length > 1 &&
                    (trimmed[1] == ' ' || char.IsLetter(trimmed[1])))) return true;
            }
            return false;
        }

        public ParseResult Parse(TextReader reader)
        {
            var summary = new QuantumSummary { Software = SoftwareType.Gaussian };
            var molecule = new Molecule();

            // Section states
            bool inLink0 = true;
            bool inRoute = false;
            bool routeDone = false;
            bool titleDone = false;
            bool chargeDone = false;
            bool inAtoms = false;

            var routeLines = new List<string>();
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();

                // ── Link0 section: %xxx= lines ─────────────────────────
                if (inLink0)
                {
                    if (trimmed.StartsWith("%"))
                        continue;
                    inLink0 = false;
                }

                // ── Route section: # lines ────────────────────────────
                if (!routeDone)
                {
                    if (!inRoute && trimmed.StartsWith("#"))
                        inRoute = true;

                    if (inRoute)
                    {
                        if (trimmed.Length == 0)
                        {
                            routeDone = true;
                            inRoute = false;
                            ParseRoute(string.Join(" ", routeLines), summary);
                        }
                        else
                        {
                            routeLines.Add(trimmed.TrimStart('#').Trim());
                        }
                        continue;
                    }
                    continue;
                }

                // ── Title line: skip first non-blank line after route ──
                if (!titleDone)
                {
                    if (trimmed.Length == 0 && routeLines.Count > 0)
                        continue; // blank separator between route and title
                    if (trimmed.Length > 0)
                    {
                        titleDone = true;
                        continue; // skip title
                    }
                    continue;
                }

                // ── Blank line after title signals charge/mult ──────────
                if (titleDone && !chargeDone)
                {
                    if (trimmed.Length == 0) continue;
                    // "charge mult" line
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 &&
                        int.TryParse(parts[0], out int charge) &&
                        int.TryParse(parts[1], out int mult))
                    {
                        summary.Charge = charge;
                        summary.Spin = MultiplicityToSpinName(mult);
                        chargeDone = true;
                        inAtoms = true;
                    }
                    continue;
                }

                // ── Atom coordinate lines ────────────────────────────
                if (inAtoms)
                {
                    if (trimmed.Length == 0) break; // blank line ends atom block

                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;

                    // Symbol may be "C", "6", or "C(Fragment=1)" — extract element
                    string symRaw = parts[0];
                    // Strip any parenthetical modifier
                    int parenIdx = symRaw.IndexOf('(');
                    if (parenIdx > 0) symRaw = symRaw.Substring(0, parenIdx);

                    // If it's an atomic number, convert
                    string sym;
                    if (int.TryParse(symRaw, out int atomicNum))
                        sym = ElementData.SymbolFromAtomicNumber(atomicNum);
                    else
                        sym = char.ToUpperInvariant(symRaw[0]) +
                              (symRaw.Length > 1 ? symRaw.Substring(1).ToLowerInvariant() : "");

                    if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;
                    if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;
                    if (!double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double z)) continue;

                    molecule.Atoms.Add(new Atom(sym, x, y, z));
                }
            }

            if (molecule.Atoms.Count == 0 && summary.Method == null) return null;

            if (molecule.Atoms.Count > 0)
            {
                molecule.Bonds = BondDetector.Detect(molecule.Atoms);
                summary.AtomCounts = BuildAtomCounts(molecule.Atoms);
            }

            // Must pass IsValid: needs CalcType or energy
            if (summary.CalcType == null) summary.CalcType = "SP";

            return new ParseResult { Summary = summary, Molecule = molecule };
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers (shared logic from GaussianParser)
        // ──────────────────────────────────────────────────────────────────

        private static void ParseRoute(string route, QuantumSummary s)
        {
            string upper = route.ToUpperInvariant();
            bool hasFreq = upper.Contains("FREQ");
            bool hasOpt  = upper.Contains("OPT");
            if (hasFreq && hasOpt) s.CalcType = "OPT+FREQ";
            else if (hasFreq)     s.CalcType = "FREQ";
            else if (hasOpt)      s.CalcType = "OPT";
            else                  s.CalcType = "SP";

            var mb = Regex.Match(route, @"([A-Za-z0-9\(\)\-]+)/(\S+)");
            if (mb.Success)
            {
                s.Method   = mb.Groups[1].Value.ToUpperInvariant();
                s.BasisSet = mb.Groups[2].Value;
            }

            var solv = Regex.Match(route, @"(?:SCRF|PCM|SMD)\s*=?\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
            s.Solvation = solv.Success ? solv.Groups[1].Value : "None";
        }

        private static Dictionary<string, int> BuildAtomCounts(List<Atom> atoms)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var atom in atoms)
            {
                if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                else counts[atom.Element] = 1;
            }
            return counts;
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
