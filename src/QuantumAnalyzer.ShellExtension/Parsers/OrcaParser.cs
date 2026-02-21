using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    public class OrcaParser : IQuantumParser
    {
        private const double HartreeToEv = 27.211385;

        public bool CanParse(string[] firstLines)
        {
            foreach (string line in firstLines)
            {
                if (line == null) continue;
                if (line.IndexOf("O   R   C   A", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (line.IndexOf("ORCA", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (line.IndexOf("This ORCA binary", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public ParseResult Parse(TextReader reader)
        {
            var summary = new QuantumSummary { Software = SoftwareType.Orca };
            var molecule = new Molecule();

            // Coordinate block tracking (keep last occurrence)
            var currentCoordBlock = new List<string>();
            var lastCoordBlock    = new List<string>();
            bool inCoordBlock = false;
            int coordSkipLines = 0;

            // Orbital energies: track last ORBITAL ENERGIES section
            var orbitalLines = new List<string>();
            bool inOrbitalEnergies = false;
            bool orbitalHeaderPassed = false;

            // Frequency counting
            int imagFreqCount = 0;

            // Input block (first lines) for method/basis parsing
            bool inInputBlock = false;
            bool inputBlockSawBorder = false;
            var inputLines = new List<string>();

            bool hasFreq = false;
            bool hasOpt  = false;
            bool normalTermination = false;
            double? kBT = null;  // "Thermal Enthalpy correction" (kB*T), used to compute ThermalEnthalpy

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();

                // ── Detect calc type from section headers ──────────────
                if (line.IndexOf("ORCA FREQUENCY CALCULATION", StringComparison.OrdinalIgnoreCase) >= 0)    hasFreq = true;
                if (line.IndexOf("GEOMETRY OPTIMIZATION CYCLE", StringComparison.OrdinalIgnoreCase) >= 0)   hasOpt  = true;
                if (line.IndexOf("ORCA TERMINATED NORMALLY", StringComparison.OrdinalIgnoreCase) >= 0)      normalTermination = true;

                // ── Input block (contains method/basis) ────────────────
                if (trimmed.StartsWith("INPUT FILE", StringComparison.OrdinalIgnoreCase) || 
                    line.IndexOf("Your calculation input:", StringComparison.OrdinalIgnoreCase) >= 0)
                    inInputBlock = true;
                if (inInputBlock)
                {
                    // Handle both ORCA 5.x (---- borders) and 6.x (==== borders with | N> prefixes)
                    if (trimmed.StartsWith("====") || (trimmed.StartsWith("----") && inputLines.Count > 0))
                    {
                        if (inputBlockSawBorder)
                            inInputBlock = false;
                        else
                            inputBlockSawBorder = true;
                        continue;
                    }

                    // Strip ORCA 6.x "|  N> " line number prefix
                    string content = trimmed;
                    var prefixMatch = Regex.Match(content, @"^\|\s*\d+>\s*(.*)$");
                    if (prefixMatch.Success)
                        content = prefixMatch.Groups[1].Value.TrimStart();

                    if (content.StartsWith("!") || content.StartsWith("%"))
                        inputLines.Add(content);
                    continue;
                }

                // ── Charge / Multiplicity ──────────────────────────────
                if (summary.Spin == null)
                {
                    var cm = Regex.Match(line, @"Total\s+Charge\s+Charge\s*\.\.\.\s*(-?\d+)", RegexOptions.IgnoreCase);
                    if (!cm.Success) cm = Regex.Match(line, @"Charge\s*=\s*(-?\d+)\s+Multiplicity\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                    if (cm.Success && cm.Groups.Count >= 3)
                    {
                        summary.Charge = int.Parse(cm.Groups[1].Value);
                        int mult = int.Parse(cm.Groups[2].Value);
                        summary.Spin = MultiplicityToSpinName(mult);
                    }
                    // Alternative ORCA format
                    var mult2 = Regex.Match(line, @"Multiplicity\s+Mult\s*\.\.\.\s*(\d+)", RegexOptions.IgnoreCase);
                    if (mult2.Success)
                        summary.Spin = MultiplicityToSpinName(int.Parse(mult2.Groups[1].Value));
                    var chg2 = Regex.Match(line, @"Total\s+Charge\s*\.\.\.\s*(-?\d+)", RegexOptions.IgnoreCase);
                    if (chg2.Success)
                        summary.Charge = int.Parse(chg2.Groups[1].Value);
                }

                // ── Cartesian coordinates block ────────────────────────
                if (line.IndexOf("CARTESIAN COORDINATES (ANGSTROEM)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inCoordBlock = true;
                    coordSkipLines = 1; // skip the dashes line
                    currentCoordBlock = new List<string>();
                    continue;
                }
                if (inCoordBlock)
                {
                    if (coordSkipLines > 0) { coordSkipLines--; continue; }
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        inCoordBlock = false;
                        lastCoordBlock = new List<string>(currentCoordBlock);
                        continue;
                    }
                    currentCoordBlock.Add(line);
                    continue;
                }

                // ── Final single-point energy ──────────────────────────
                var energy = Regex.Match(line, @"FINAL SINGLE POINT ENERGY\s+([-\d.]+)", RegexOptions.IgnoreCase);
                if (energy.Success)
                {
                    summary.ElectronicEnergy = double.Parse(energy.Groups[1].Value,
                                                System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (summary.ElectronicEnergy == null)
                {
                    // Fallback for cases where FINAL SINGLE POINT ENERGY is not yet printed or formatted differently
                    var totalEnergy = Regex.Match(line, @"Total Energy\s+:\s+([-\d.]+)", RegexOptions.IgnoreCase);
                    if (totalEnergy.Success)
                    {
                        summary.ElectronicEnergy = double.Parse(totalEnergy.Groups[1].Value,
                                                    System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                // ── Orbital energies ───────────────────────────────────
                if (trimmed.Equals("ORBITAL ENERGIES", StringComparison.OrdinalIgnoreCase))
                {
                    inOrbitalEnergies  = true;
                    orbitalHeaderPassed = false;
                    orbitalLines = new List<string>();
                    continue;
                }
                if (inOrbitalEnergies)
                {
                    if (trimmed.StartsWith("---") && !orbitalHeaderPassed) { orbitalHeaderPassed = true; continue; }

                    // Exit only on the second dashes block or if we already have data and hit an empty line
                    if (trimmed.StartsWith("---") || (orbitalLines.Count > 0 && string.IsNullOrWhiteSpace(trimmed)))
                    {
                        inOrbitalEnergies = false;
                        continue;
                    }

                    if (orbitalHeaderPassed && !string.IsNullOrWhiteSpace(trimmed))
                        orbitalLines.Add(line);

                    continue;
                }

                // ── Imaginary frequencies ──────────────────────────────
                var imgFreq = Regex.Match(line, @"^\s*\d+:\s+([-\d.]+)\s+cm\*\*-1.*\(imaginary mode\)");
                if (imgFreq.Success) imagFreqCount++;
                // Also catch negative frequency values in the plain list
                var freqLine = Regex.Match(line, @"^\s*\d+:\s+([-\d.]+)\s+cm");
                if (freqLine.Success && double.TryParse(freqLine.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double fval) && fval < 0)
                    imagFreqCount++;

                // ── Thermochemistry ────────────────────────────────────
                ParseThermoLine(line, summary);

                // kBT: "Thermal Enthalpy correction       ...      0.00094421 Eh"
                if (kBT == null &&
                    line.IndexOf("Thermal Enthalpy correction", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var m = Regex.Match(line, @"-?\d+\.\d+");
                    if (m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double kbt))
                        kBT = kbt;
                }
            }

            // Post-pass finalization
            summary.NormalTermination = normalTermination;
            summary.ImaginaryFreq = imagFreqCount;

            // Derived thermo values
            if (summary.ElectronicEnergy.HasValue && summary.ZPE.HasValue)
                summary.EE_ZPE = summary.ElectronicEnergy.Value + summary.ZPE.Value;

            if (summary.ThermalEnergy.HasValue)
                summary.ThermalEnthalpy = summary.ThermalEnergy.Value + (kBT ?? 0.0);

            if (hasFreq && hasOpt) summary.CalcType = "OPT+FREQ";
            else if (hasFreq)     summary.CalcType = "FREQ";
            else if (hasOpt)      summary.CalcType = "OPT";
            else                  summary.CalcType = "SP";

            ParseInputBlock(inputLines, summary);

            // HOMO / LUMO from orbital energies
            ParseOrbitalEnergies(orbitalLines, summary);

            // Build molecule from last coordinate block
            if (lastCoordBlock.Count > 0)
            {
                foreach (string ol in lastCoordBlock)
                {
                    string[] parts = ol.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // Format: Element X Y Z
                    if (parts.Length >= 4 &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                        double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out double y) &&
                        double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out double z))
                    {
                        molecule.Atoms.Add(new Atom(parts[0], x, y, z));
                    }
                }
                molecule.Bonds = BondDetector.Detect(molecule.Atoms);

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var atom in molecule.Atoms)
                {
                    if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                    else counts[atom.Element] = 1;
                }
                summary.AtomCounts = counts;
            }

            if (summary.ElectronicEnergy == null && molecule.Atoms.Count == 0) return null;

            return new ParseResult { Summary = summary, Molecule = molecule };
        }

        // ──────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────

        private static void ParseInputBlock(List<string> lines, QuantumSummary s)
        {
            foreach (string line in lines)
            {
                if (!line.StartsWith("!")) continue;
                string tokens = line.TrimStart('!').Trim();

                string[] parts = tokens.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                // Skip warning/error/notification lines (e.g. "! WARNING: SERIOUS PROBLEM")
                string firstTok = parts[0].ToUpperInvariant();
                if (firstTok == "WARNING" || firstTok == "ERROR" || firstTok == "NOTE"
                    || firstTok == "CAUTION" || firstTok == "***") continue;

                if (s.Method == null)
                {
                    var basisSets = new List<string>();

                    // Handle "B3LYP/def2-TZVP" combined token
                    if (parts[0].Contains("/"))
                    {
                        var split = parts[0].Split('/');
                        s.Method = split[0].ToUpperInvariant();
                        basisSets.Add(split[1]);
                        // Collect additional basis set tokens from remaining parts
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (!IsKeyword(parts[i]))
                                basisSets.Add(parts[i]);
                        }
                    }
                    else
                    {
                        s.Method = parts[0].ToUpperInvariant();
                        // Collect ALL non-keyword tokens after method as basis sets
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (!IsKeyword(parts[i]))
                                basisSets.Add(parts[i]);
                        }
                    }

                    if (basisSets.Count == 1)
                        s.BasisSet = basisSets[0];
                    else if (basisSets.Count > 1)
                        s.BasisSet = "(" + string.Join("+", basisSets) + ")";
                }
            }

            if (s.Solvation == null) s.Solvation = "None";
        }

        private static bool IsKeyword(string tok)
        {
            string upper = tok.ToUpperInvariant();
            switch (upper)
            {
                case "FREQ":
                case "OPT":
                case "SP":
                case "TIGHTSCF":
                case "VERYTIGHTSCF":
                case "NORMALSCF":
                case "LOOSESCF":
                case "DEFGRID1":
                case "DEFGRID2":
                case "DEFGRID3":
                case "RIJCOSX":
                case "NORI":
                case "RI":
                case "LARGEPRINT":
                case "MINIPRINT":
                case "NOPRINT":
                case "ENGRAD":
                case "NUMGRAD":
                case "NUMFREQ":
                case "ANFREQ":
                case "TIGHTOPT":
                case "VERYTIGHTOPT":
                case "SLOWCONV":
                case "NBO":
                case "NPA":
                case "CONV":
                case "NOCONV":
                case "LEANSCF":
                case "AODIIS":
                case "SOSCF":
                case "NOSOSCF":
                case "KDIIS":
                case "DIIS":
                case "NRSCF":
                case "D3":
                case "D3BJ":
                case "D4":
                case "D3ZERO":
                case "BOHRS":
                case "UHF":
                case "RHF":
                case "ROHF":
                case "UKS":
                case "RKS":
                case "ROKS":
                case "MOREAD":
                case "AUTOSTART":
                case "NOAUTOSTART":
                case "KEEPDENS":
                case "NOFROZENCORE":
                case "FROZENCORE":
                case "CPCM":
                case "SMD":
                    return true;
                default:
                    if (upper.StartsWith("GRID") || upper.StartsWith("AUX"))
                        return true;
                    // Tokens starting with "NO" followed by uppercase (e.g. NoMulliken, NoLoewdin, NoPrintMOs)
                    if (upper.Length > 2 && upper.StartsWith("NO") && tok.Length > 2 && char.IsUpper(tok[2]))
                        return true;
                    return false;
            }
        }

        private static void ParseOrbitalEnergies(List<string> lines, QuantumSummary s)
        {
            // Each line format: NO  OCC  E(Eh)  E(eV)
            double lastOccEh    = double.NaN;
            double firstVirtEh  = double.NaN;
            int    lastOccNo    = 0;
            bool   virtFound    = false;

            foreach (string line in lines)
            {
                string[] parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[0], out int no)) continue;
                if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out double occ)) continue;
                if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out double eHartree)) continue;

                if (occ > 0.01)
                {
                    lastOccEh = eHartree;
                    lastOccNo = no;
                    virtFound = false;
                }
                else if (!virtFound)
                {
                    firstVirtEh = eHartree;
                    virtFound   = true;
                }
            }

            if (!double.IsNaN(lastOccEh) && !double.IsNaN(firstVirtEh))
            {
                s.HomoIndex    = lastOccNo + 1;   // 1-based
                s.LumoIndex    = lastOccNo + 2;
                s.HomoSpin     = "Alpha";
                s.HomoLumoGap  = (firstVirtEh - lastOccEh) * HartreeToEv;
            }
        }

        private static void ParseThermoLine(string line, QuantumSummary s)
        {
            // "Zero point energy                ...      0.21886874 Eh     137.34 kcal/mol"
            TryParseEh(line, "Zero point energy",        v => s.ZPE           = v);

            // "Total thermal correction                  0.02730438 Eh      17.13 kcal/mol"
            // = thermal correction to energy (no ZPE), maps to Gaussian's ThermalEnergy
            TryParseEh(line, "Total thermal correction", v => s.ThermalEnergy  = v);

            // "G-E(el)                           ...      0.16520351 Eh    103.67 kcal/mol"
            // = full Gibbs correction relative to E(el), maps to ThermalFreeEnergy
            TryParseEh(line, "G-E(el)",                  v => s.ThermalFreeEnergy = v);

            // "Total thermal energy                  -2801.18355477 Eh"
            TryParseEh(line, "Total thermal energy",     v => s.EE_Thermal    = v);

            // "Total Enthalpy                    ...  -2801.18261056 Eh"
            TryParseEh(line, "Total Enthalpy",           v => s.EE_Enthalpy   = v);

            // "Final Gibbs free energy         ...  -2801.26452439 Eh"
            TryParseEh(line, "Final Gibbs free energy",  v => s.EE_FreeEnergy = v);

            // EThermal_kcal: second number on the "Total thermal correction" line (kcal/mol column)
            if (s.EThermal_kcal == null &&
                line.IndexOf("Total thermal correction", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var matches = Regex.Matches(line, @"-?\d+\.\d+");
                if (matches.Count >= 2 && double.TryParse(matches[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double kcal))
                    s.EThermal_kcal = kcal;
            }

            // Entropy: "Final entropy term                ...      0.08191383 Eh     51.40 kcal/mol"
            // T*S in Eh → S in cal/mol·K (T = 298.15 K assumed)
            if (s.Entropy == null &&
                line.IndexOf("Final entropy term", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var m = Regex.Match(line, @"-?\d+\.\d+");
                if (m.Success && double.TryParse(m.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double tSEh))
                    s.Entropy = tSEh * 627509.47 / 298.15;   // Eh → cal/mol, divide by T(K)
            }
        }

        // Uses (-?\d+\.\d+) so the ORCA "..." separator is never mistaken for a number
        private static void TryParseEh(string line, string key, Action<double> setter)
        {
            int idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return;
            var m = Regex.Match(line.Substring(idx + key.Length), @"(-?\d+\.\d+)");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
                setter(v);
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
