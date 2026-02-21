using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    public class GaussianParser : IQuantumParser
    {
        private const double HartreeToEv = 27.211385;

        public bool CanParse(string[] firstLines)
        {
            foreach (string line in firstLines)
            {
                if (line == null) continue;
                string trimmed = line.Trim();
                if (line.IndexOf("Entering Gaussian System", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (line.IndexOf("Gaussian(R)", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (line.IndexOf("Gaussian, Inc.", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                
                // Specific revision line: "Gaussian 16:  ES64L-G16RevA.03  25-Dec-2016"
                if (line.IndexOf("Gaussian", StringComparison.OrdinalIgnoreCase) >= 0 && 
                    (line.IndexOf("Revision", StringComparison.OrdinalIgnoreCase) >= 0 || 
                     line.IndexOf("Rev", StringComparison.OrdinalIgnoreCase) >= 0)) return true;

                // Route card start
                if (trimmed.StartsWith("#P", StringComparison.OrdinalIgnoreCase) || 
                    trimmed.StartsWith("#N", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("#T", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public ParseResult Parse(TextReader reader)
        {
            var summary = new QuantumSummary { Software = SoftwareType.Gaussian };
            var molecule = new Molecule();

            // Accumulate state while scanning the file line by line
            var routeLines = new List<string>();
            bool inRoute = false;
            bool routeDone = false;

            // Orientation block tracking: keep the last standard and last input separately.
            // Standard orientation is preferred; input orientation used as fallback.
            var currentOrientationBlock = new List<string>();
            var lastOrientationBlock = new List<string>();      // Standard orientation
            var lastInputOrientationBlock = new List<string>(); // Input orientation (nosymm fallback)
            bool inOrientation = false;
            bool currentBlockIsStandard = false;
            int orientationHeaderCount = 0;

            bool normalTermination = false;
            int imagFreqCount = 0;

            // Gen basis set parsing state
            bool lookingForGenBasis = false;
            int genState = 0; // 0=looking for element line, 1=expecting basis name, 2=skip to ****
            var genBasisNames = new List<string>();

            // Alpha occupied eigenvalue lines (for HOMO/LUMO)
            double lastAlphaOccEnergy = double.NaN;
            double firstAlphaVirtEnergy = double.NaN;
            int alphaOccCount = 0;
            bool foundFirstAlphaVirt = false;
            // Reset per SCF Done (we want the last set of eigenvalues)
            bool eigenvaluesSectionSeen = false;
            double tempLastOcc = double.NaN;
            double tempFirstVirt = double.NaN;
            int tempOccCount = 0;
            bool tempFoundVirt = false;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                // ── Route section ──────────────────────────────────────
                if (!routeDone)
                {
                    if (!inRoute && (line.TrimStart().StartsWith("#") || line.TrimStart().StartsWith("# ")))
                    {
                        inRoute = true;
                    }
                    if (inRoute)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("---") || (trimmed.Length == 0 && routeLines.Count > 0))
                        {
                            routeDone = true;
                            inRoute = false;
                            ParseRoute(string.Join(" ", routeLines), summary);
                        }
                        else if (trimmed.Length > 0)
                        {
                            routeLines.Add(trimmed.TrimStart('#').Trim());
                        }
                        continue;
                    }
                }

                // ── Charge / Multiplicity ──────────────────────────────
                if (summary.Spin == null)
                {
                    var cm = Regex.Match(line, @"Charge\s*=\s*(-?\d+)\s+Multiplicity\s*=\s*(\d+)");
                    if (!cm.Success)
                        cm = Regex.Match(line, @"Charge\s+and\s+Multiplicity\s+(-?\d+)\s+(\d+)", RegexOptions.IgnoreCase);

                    if (cm.Success)
                    {
                        summary.Charge = int.Parse(cm.Groups[1].Value);
                        int mult = int.Parse(cm.Groups[2].Value);
                        summary.Spin = MultiplicityToSpinName(mult);
                        // Start looking for gen basis if applicable
                        if (summary.BasisSet != null &&
                            (summary.BasisSet.Equals("gen", StringComparison.OrdinalIgnoreCase) ||
                             summary.BasisSet.Equals("genecp", StringComparison.OrdinalIgnoreCase)))
                        {
                            lookingForGenBasis = true;
                            genState = 0;
                        }
                    }
                }

                // ── Gen basis set extraction ─────────────────────────
                // The gen block sits between charge/mult and the first orientation block.
                // Format: element_list 0 / basis_name / **** repeated per fragment.
                if (lookingForGenBasis)
                {
                    string trimmedGen = line.Trim();
                    // Stop at orientation blocks
                    if (trimmedGen.StartsWith("Standard orientation", StringComparison.OrdinalIgnoreCase) ||
                        trimmedGen.StartsWith("Input orientation", StringComparison.OrdinalIgnoreCase))
                    {
                        lookingForGenBasis = false;
                    }
                    else if (genState == 0)
                    {
                        // Looking for element line: all tokens except last are element symbols, last is "0"
                        if (!string.IsNullOrWhiteSpace(trimmedGen))
                        {
                            string[] toks = trimmedGen.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (toks.Length >= 2 && toks[toks.Length - 1] == "0")
                            {
                                bool allElements = true;
                                for (int i = 0; i < toks.Length - 1; i++)
                                {
                                    if (!Regex.IsMatch(toks[i], @"^[A-Z][a-z]?$"))
                                    { allElements = false; break; }
                                }
                                if (allElements)
                                    genState = 1; // next non-blank line is basis name
                            }
                        }
                    }
                    else if (genState == 1)
                    {
                        if (!string.IsNullOrWhiteSpace(trimmedGen))
                        {
                            // If it looks like a coefficient line (e.g. "SP   3   1.00"), skip - shouldn't happen right after element line
                            if (!Regex.IsMatch(trimmedGen, @"^[A-Z]{1,2}\s+\d+"))
                            {
                                string basisName = trimmedGen.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                                if (!genBasisNames.Contains(basisName))
                                    genBasisNames.Add(basisName);
                            }
                            genState = 2; // skip to ****
                        }
                    }
                    else if (genState == 2)
                    {
                        if (trimmedGen == "****")
                            genState = 0; // back to looking for next element line
                    }

                    // Apply collected names at the end of gen block scanning
                    if (!lookingForGenBasis && genBasisNames.Count > 0)
                    {
                        summary.BasisSet = genBasisNames.Count == 1
                            ? genBasisNames[0]
                            : "(" + string.Join("+", genBasisNames) + ")";
                    }
                }

                // ── Geometry orientation blocks ────────────────────────
                // "Standard orientation:" is printed by default; when nosymm is used
                // only "Input orientation:" appears — keep both and prefer standard.
                if (line.Contains("Standard orientation:"))
                {
                    inOrientation = true;
                    currentBlockIsStandard = true;
                    orientationHeaderCount = 0;
                    currentOrientationBlock = new List<string>();
                    continue;
                }
                if (line.Contains("Input orientation:"))
                {
                    inOrientation = true;
                    currentBlockIsStandard = false;
                    orientationHeaderCount = 0;
                    currentOrientationBlock = new List<string>();
                    continue;
                }
                if (inOrientation)
                {
                    if (line.Contains("-----"))
                    {
                        orientationHeaderCount++;
                        // Atom data sits between the 2nd and 3rd dashes block
                        if (orientationHeaderCount == 3)
                        {
                            inOrientation = false;
                            if (currentBlockIsStandard)
                                lastOrientationBlock = new List<string>(currentOrientationBlock);
                            else
                                lastInputOrientationBlock = new List<string>(currentOrientationBlock);
                        }
                        continue;
                    }
                    if (orientationHeaderCount >= 2)
                        currentOrientationBlock.Add(line);
                    continue;
                }

                // ── Normal termination ─────────────────────────────────
                if (line.Contains("Normal termination of Gaussian"))
                    normalTermination = true;

                // ── SCF Done ───────────────────────────────────────────
                var scf = Regex.Match(line, @"SCF Done:\s+E\(\S+\)\s*=\s*([-\d.]+)");
                if (scf.Success)
                {
                    if (double.TryParse(scf.Groups[1].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double scfEnergy))
                        summary.ElectronicEnergy = scfEnergy;
                    // Reset eigenvalue accumulators for new SCF
                    tempLastOcc = double.NaN;
                    tempFirstVirt = double.NaN;
                    tempOccCount = 0;
                    tempFoundVirt = false;
                    eigenvaluesSectionSeen = false;
                }
                else if (summary.ElectronicEnergy == null)
                {
                    // Fallback for some older or different Gaussian formats
                    var scf2 = Regex.Match(line, @"E\([RU]HF\)\s*=\s*([-\d.]+)", RegexOptions.IgnoreCase);
                    if (scf2.Success && double.TryParse(scf2.Groups[1].Value, 
                        System.Globalization.NumberStyles.Float, 
                        System.Globalization.CultureInfo.InvariantCulture, out double e2))
                        summary.ElectronicEnergy = e2;
                }

                // ── Alpha orbital eigenvalues ──────────────────────────
                if (line.Contains("Alpha  occ. eigenvalues --"))
                {
                    eigenvaluesSectionSeen = true;
                    string vals = line.Substring(line.IndexOf("--") + 2);
                    double[] energies = ParseDoubles(vals);
                    tempOccCount += energies.Length;
                    if (energies.Length > 0)
                        tempLastOcc = energies[energies.Length - 1];
                }
                else if (line.Contains("Alpha virt. eigenvalues --") && !tempFoundVirt && eigenvaluesSectionSeen)
                {
                    string vals = line.Substring(line.IndexOf("--") + 2);
                    double[] energies = ParseDoubles(vals);
                    if (energies.Length > 0)
                    {
                        tempFirstVirt = energies[0];
                        tempFoundVirt = true;
                        // Commit this set as the latest
                        lastAlphaOccEnergy = tempLastOcc;
                        firstAlphaVirtEnergy = tempFirstVirt;
                        alphaOccCount = tempOccCount;
                        foundFirstAlphaVirt = true;
                    }
                }

                // ── Imaginary frequencies ──────────────────────────────
                if (line.Contains("Frequencies --"))
                {
                    string vals = line.Substring(line.IndexOf("--") + 2);
                    foreach (string tok in vals.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (double.TryParse(tok, out double freq) && freq < 0)
                            imagFreqCount++;
                    }
                }

                // ── Thermochemistry ────────────────────────────────────
                ParseThermoLine(line, summary);
            }

            // Post-pass: finalise
            // Apply gen basis names if not already applied (e.g. file ended without orientation block)
            if (lookingForGenBasis && genBasisNames.Count > 0)
            {
                summary.BasisSet = genBasisNames.Count == 1
                    ? genBasisNames[0]
                    : "(" + string.Join("+", genBasisNames) + ")";
            }

            summary.NormalTermination = normalTermination;
            summary.ImaginaryFreq = imagFreqCount;

            if (foundFirstAlphaVirt && !double.IsNaN(lastAlphaOccEnergy) && !double.IsNaN(firstAlphaVirtEnergy))
            {
                summary.HomoIndex = alphaOccCount;
                summary.LumoIndex = alphaOccCount + 1;
                summary.HomoSpin = "Alpha";
                summary.HomoLumoGap = (firstAlphaVirtEnergy - lastAlphaOccEnergy) * HartreeToEv;
            }

            // Build molecule: prefer Standard orientation; fall back to Input orientation
            // (Input orientation is the only one printed when nosymm is used)
            var orientationBlock = lastOrientationBlock.Count > 0
                ? lastOrientationBlock
                : lastInputOrientationBlock;

            if (orientationBlock.Count > 0)
            {
                foreach (string ol in orientationBlock)
                {
                    // Format: center# atomicZ type X Y Z
                    string[] parts = ol.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 6 &&
                        int.TryParse(parts[0], out _) &&
                        int.TryParse(parts[1], out int atomicZ) &&
                        double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                        double.TryParse(parts[4], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double y) &&
                        double.TryParse(parts[5], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double z))
                    {
                        string element = ElementData.SymbolFromAtomicNumber(atomicZ);
                        molecule.Atoms.Add(new Atom(element, x, y, z));
                    }
                }
                molecule.Bonds = BondDetector.Detect(molecule.Atoms);
                summary.AtomCounts = BuildAtomCounts(molecule.Atoms);
            }

            if (!summary.IsValid()) return null;

            return new ParseResult { Summary = summary, Molecule = molecule };
        }

        // ──────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────

        private static void ParseRoute(string route, QuantumSummary s)
        {
            // CalcType
            string upper = route.ToUpperInvariant();
            bool hasFreq = upper.Contains("FREQ");
            bool hasOpt  = upper.Contains("OPT");
            if (hasFreq && hasOpt) s.CalcType = "OPT+FREQ";
            else if (hasFreq)     s.CalcType = "FREQ";
            else if (hasOpt)      s.CalcType = "OPT";
            else                  s.CalcType = "SP";

            // Method and basis: look for token like METHOD/BASIS.
            // Include '-' so hyphenated functionals (M06-2X, CAM-B3LYP, ωB97X-D …) are captured whole.
            var mb = Regex.Match(route, @"([A-Za-z0-9\(\)\-]+)/(\S+)");
            if (mb.Success)
            {
                s.Method   = mb.Groups[1].Value.ToUpperInvariant();
                s.BasisSet = mb.Groups[2].Value;
            }

            // Solvation
            var solv = Regex.Match(route, @"(?:SCRF|PCM|SMD)\s*=?\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
            s.Solvation = solv.Success ? solv.Groups[1].Value : "None";
        }

        private static void ParseThermoLine(string line, QuantumSummary s)
        {
            TryParseValue(line, "Zero-point correction=",                      v => s.ZPE             = v);
            TryParseValue(line, "Thermal correction to Energy=",               v => s.ThermalEnergy   = v);
            TryParseValue(line, "Thermal correction to Enthalpy=",             v => s.ThermalEnthalpy = v);
            TryParseValue(line, "Thermal correction to Gibbs Free Energy=",    v => s.ThermalFreeEnergy = v);
            TryParseValue(line, "Sum of electronic and zero-point Energies=",  v => s.EE_ZPE         = v);
            TryParseValue(line, "Sum of electronic and thermal Energies=",     v => s.EE_Thermal     = v);
            TryParseValue(line, "Sum of electronic and thermal Enthalpies=",   v => s.EE_Enthalpy    = v);
            TryParseValue(line, "Sum of electronic and thermal Free Energies=",v => s.EE_FreeEnergy  = v);

            // E (Thermal) / Cv / S from the Total row in thermochemistry table
            // Format: " Total               245.969           101.005           190.381"
            var totalRow = Regex.Match(line, @"\bTotal\b\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)");
            if (totalRow.Success)
            {
                if (double.TryParse(totalRow.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double eth))
                    s.EThermal_kcal = eth;
                if (double.TryParse(totalRow.Groups[2].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double cv))
                    s.Cv = cv;
                if (double.TryParse(totalRow.Groups[3].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double entr))
                    s.Entropy = entr;
            }
        }

        private static void TryParseValue(string line, string key, Action<double> setter)
        {
            int idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return;
            string rest = line.Substring(idx + key.Length).Trim();
            // Take the first token (may have trailing units like "(Hartree/Particle)")
            string tok = rest.Split(new[] { ' ', '\t', '(' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (double.TryParse(tok, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double v))
                setter(v);
        }

        private static double[] ParseDoubles(string text)
        {
            var result = new List<double>();
            foreach (string tok in text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(tok, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                    result.Add(v);
            }
            return result.ToArray();
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

    internal static class QuantumSummaryExtensions
    {
        public static bool IsValid(this QuantumSummary s)
        {
            return s != null && (s.ElectronicEnergy.HasValue || s.CalcType != null);
        }
    }
}
