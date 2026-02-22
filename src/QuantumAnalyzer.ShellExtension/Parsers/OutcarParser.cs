using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for VASP OUTCAR files (always extensionless, filename = "OUTCAR").
    /// Detected by filename in ParserFactory.TryParse(string) — CanParse() returns false.
    ///
    /// Extracts per-ionic-step geometry and energy(sigma->0), plus calculation settings:
    ///   • Element types    — TITEL lines (POTCAR order, strips _pv/_sv suffixes)
    ///   • Ion counts       — "ions per type =" line
    ///   • All POSITION blocks  — one per ionic step
    ///   • energy(sigma->0) — last value per ionic step (paired with position block)
    ///   • Fermi energy     — E-fermi
    ///   • Calc settings    — ENCUT, K-point mesh, ISMEAR, SIGMA, EDIFF, EDIFFG, ISIF, IBRION
    /// </summary>
    public class OutcarParser : IQuantumParser
    {
        // OUTCAR is always routed by filename; content-based detection not used
        public bool CanParse(string[] firstLines) => false;

        public ParseResult Parse(TextReader reader)
        {
            try   { return DoParse(reader); }
            catch { return null; }
        }

        private static ParseResult DoParse(TextReader reader)
        {
            // ── Element / ion tracking ───────────────────────────────────────
            var elementSymbols = new List<string>();
            int[] ionCounts    = null;
            int   nions        = 0;
            int   ibrionRaw    = int.MinValue;   // raw IBRION integer

            // ── Lattice (keep latest) ────────────────────────────────────────
            double[] latticeA = null, latticeB = null, latticeC = null;
            var      tempLattice = new double[3][];
            int      latticeLeft = 0;

            // ── Per-step position tracking ───────────────────────────────────
            var allStepPositions = new List<double[][]>();
            var allStepEnergies  = new List<double>();
            double? latestSigmaEnergy      = null;  // last sigma->0 seen (fallback)
            bool    waitingForPostBlockEnergy = false; // true right after a POSITION block commits

            double[][] positions   = null;   // pointer to last committed position block
            double[][] pendingPos  = null;
            int        posCount    = 0;
            int        posExpected = 0;
            // posPhase: 0=idle, 1=skip opening separator, 2=reading atom lines
            int posPhase = 0;

            // ── New VASP fields ──────────────────────────────────────────────
            double? fermiEnergy   = null;
            double? encut         = null;
            string  kpointMesh    = null;
            int?    ismear        = null;
            double? sigma         = null;
            double? ediff         = null;
            double? ediffg        = null;
            int?    isif          = null;
            bool    kpointsFound  = false;
            int     kpointsSearchLines = 0;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                // ── Element types ────────────────────────────────────────────
                // "   TITEL  = PAW_PBE Fe 06Sep2000"
                if (line.Contains("TITEL") && line.Contains("="))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1));
                        if (parts.Length >= 2)
                            elementSymbols.Add(NormalizeElement(parts[1]));
                    }
                    continue;
                }

                // ── Ion counts ───────────────────────────────────────────────
                // "   ions per type =               4   4"
                if (line.Contains("ions per type ="))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1));
                        var list = new List<int>();
                        foreach (string p in parts)
                        {
                            if (int.TryParse(p, out int n)) list.Add(n);
                            else break;
                        }
                        if (list.Count > 0) ionCounts = list.ToArray();
                    }
                    continue;
                }

                // ── Total ion count ──────────────────────────────────────────
                // "   NIONS =        8"
                if (line.Contains("NIONS ="))
                {
                    // "number of dos  NEDOS =  301  number of ions  NIONS =  32"
                    // LastIndexOf avoids picking up NEDOS= that appears earlier on the same line
                    int eq = line.LastIndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1));
                        if (parts.Length > 0) int.TryParse(parts[0], out nions);
                    }
                    continue;
                }

                // ── IBRION ───────────────────────────────────────────────────
                // "   IBRION =  2"  or  "   IBRION =  -1;"
                if (ibrionRaw == int.MinValue && line.Contains("IBRION") && line.Contains("="))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1).Replace(";", " "));
                        if (parts.Length > 0) int.TryParse(parts[0], out ibrionRaw);
                    }
                    continue;
                }

                // ── Fermi energy ─────────────────────────────────────────────
                // "   E-fermi :     -3.4567"
                if (line.Contains("E-fermi") && line.Contains(":"))
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0)
                    {
                        string[] parts = Split(line.Substring(colon + 1));
                        if (parts.Length > 0 && TryFloat(parts[0], out double ef))
                            fermiEnergy = ef;   // keep latest (final value for relaxation)
                    }
                    continue;
                }

                // ── ENCUT ────────────────────────────────────────────────────
                // "   ENCUT  =  400.0000 eV"
                if (!encut.HasValue && line.Contains("ENCUT") && line.Contains("="))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1).Replace("eV", "").Replace(";", " "));
                        if (parts.Length > 0 && TryFloat(parts[0], out double ec))
                            encut = ec;
                    }
                    continue;
                }

                // ── ISMEAR and SIGMA (often on same line) ────────────────────
                // "   ISMEAR =     0;  SIGMA  = 0.0500 eV"
                if (!ismear.HasValue && line.Contains("ISMEAR") && line.Contains("="))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string part = line.Substring(eq + 1).Replace(";", " ");
                        string[] parts = Split(part);
                        if (parts.Length > 0 && int.TryParse(parts[0], out int im))
                            ismear = im;
                    }
                    // Fall through: also check SIGMA on same line below
                }
                if (!sigma.HasValue && line.Contains("SIGMA") && line.Contains("="))
                {
                    int eq = line.LastIndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1).Replace("eV", "").Replace(";", " "));
                        if (parts.Length > 0 && TryFloat(parts[0], out double sg))
                            sigma = sg;
                    }
                    continue;
                }

                // ── EDIFF ────────────────────────────────────────────────────
                // "   EDIFF  =   0.1E-05"
                if (!ediff.HasValue && line.TrimStart().StartsWith("EDIFF") &&
                    line.Contains("=") && !line.Contains("EDIFFG"))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1).Replace(";", " "));
                        if (parts.Length > 0 && TryFloat(parts[0], out double ed))
                            ediff = ed;
                    }
                    continue;
                }

                // ── EDIFFG ───────────────────────────────────────────────────
                // "   EDIFFG =  -0.0100"
                if (!ediffg.HasValue && line.Contains("EDIFFG") && line.Contains("="))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1).Replace(";", " "));
                        if (parts.Length > 0 && TryFloat(parts[0], out double edg))
                            ediffg = edg;
                    }
                    continue;
                }

                // ── ISIF ─────────────────────────────────────────────────────
                // "   ISIF   =    3"  (guard: not on same line as EDIFFG or ENCUT)
                if (!isif.HasValue && line.TrimStart().StartsWith("ISIF") &&
                    line.Contains("=") && !line.Contains("EDIFFG") && !line.Contains("ENCUT"))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1).Replace(";", " "));
                        if (parts.Length > 0 && int.TryParse(parts[0], out int isifVal))
                            isif = isifVal;
                    }
                    continue;
                }

                // ── K-point mesh ─────────────────────────────────────────────
                // "KPOINTS: automatic mesh"  → then within next few lines: "  4  4  4"
                if (!kpointsFound && line.TrimStart().StartsWith("KPOINTS"))
                    kpointsSearchLines = 6;
                if (kpointsSearchLines > 0 && !kpointsFound)
                {
                    kpointsSearchLines--;
                    string[] p = Split(line);
                    if (p.Length == 3 &&
                        int.TryParse(p[0], out int k1) && k1 > 0 &&
                        int.TryParse(p[1], out int k2) && k2 > 0 &&
                        int.TryParse(p[2], out int k3) && k3 > 0)
                    {
                        kpointMesh   = $"{k1}x{k2}x{k3}";
                        kpointsFound = true;
                    }
                }

                // ── Lattice vectors ──────────────────────────────────────────
                if (latticeLeft == 0 && line.Contains("direct lattice vectors"))
                {
                    latticeLeft  = 3;
                    tempLattice  = new double[3][];
                    continue;
                }
                if (latticeLeft > 0)
                {
                    string[] p = Split(line);
                    if (p.Length >= 3 &&
                        TryFloat(p[0], out double ax) &&
                        TryFloat(p[1], out double ay) &&
                        TryFloat(p[2], out double az))
                    {
                        tempLattice[3 - latticeLeft] = new[] { ax, ay, az };
                    }
                    latticeLeft--;
                    if (latticeLeft == 0 &&
                        tempLattice[0] != null && tempLattice[1] != null && tempLattice[2] != null)
                    {
                        latticeA = tempLattice[0];
                        latticeB = tempLattice[1];
                        latticeC = tempLattice[2];
                    }
                    continue;
                }

                // ── energy(sigma->0) ─────────────────────────────────────────
                // "  energy  without entropy=  -125.123456  energy(sigma->0) =  -125.234567"
                if (line.Contains("energy(sigma->0)") && line.Contains("="))
                {
                    int eq = line.LastIndexOf('=');
                    if (eq >= 0)
                    {
                        string[] parts = Split(line.Substring(eq + 1));
                        if (parts.Length > 0 && TryFloat(parts[0], out double e))
                        {
                            latestSigmaEnergy = e;
                            // The FIRST sigma->0 after a POSITION block closes is that
                            // step's converged energy (VASP always prints it right after).
                            if (waitingForPostBlockEnergy)
                            {
                                allStepEnergies.Add(e);
                                waitingForPostBlockEnergy = false;
                            }
                        }
                    }
                    continue;
                }

                // ── Position block header ─────────────────────────────────────
                // "  POSITION                                  TOTAL-FORCE (eV/Angst)"
                if (posPhase == 0 &&
                    line.Contains("POSITION") && line.Contains("TOTAL-FORCE"))
                {
                    int expected = (ionCounts != null) ? ArraySum(ionCounts) : nions;
                    if (expected > 0)
                    {
                        posExpected = expected;
                        pendingPos  = new double[expected][];
                        posCount    = 0;
                        posPhase    = 1;
                    }
                    continue;
                }

                if (posPhase == 1)           // skip opening "---" separator
                {
                    posPhase = 2;
                    continue;
                }

                if (posPhase == 2)
                {
                    if (line.TrimStart().StartsWith("---"))
                    {
                        // Closing separator — commit block only when all atoms read
                        if (posCount == posExpected)
                        {
                            var committed = (double[][])pendingPos.Clone();
                            allStepPositions.Add(committed);
                            positions = committed;
                            waitingForPostBlockEnergy = true; // expect energy summary next
                        }
                        posPhase = 0;
                    }
                    else if (posCount < posExpected)
                    {
                        string[] p = Split(line);
                        if (p.Length >= 3 &&
                            TryFloat(p[0], out double px) &&
                            TryFloat(p[1], out double py) &&
                            TryFloat(p[2], out double pz))
                        {
                            pendingPos[posCount++] = new[] { px, py, pz };
                        }
                    }
                    continue;
                }
            }

            // ── After loop: fallback for truncated file ──────────────────────
            // If the last POSITION block was committed but no post-block energy
            // was found (e.g. calculation terminated mid-output), use the last
            // known sigma->0 energy.
            if (waitingForPostBlockEnergy && latestSigmaEnergy.HasValue)
                allStepEnergies.Add(latestSigmaEnergy.Value);

            // ── Require at least one position block ──────────────────────────
            if (positions == null || positions.Length == 0) return null;

            // ── Build atom element array ─────────────────────────────────────
            string[] atomElements = MapElements(elementSymbols, ionCounts, positions.Length);

            // ── Build final-step molecule ────────────────────────────────────
            var atoms = new List<Atom>();
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i] == null) continue;
                atoms.Add(new Atom(atomElements[i], positions[i][0], positions[i][1], positions[i][2]));
            }
            if (atoms.Count == 0) return null;

            var mol    = new Molecule { Atoms = atoms, Bonds = BondDetector.Detect(atoms) };
            var counts = BuildCounts(atoms);

            // ── Calc type from IBRION ────────────────────────────────────────
            string calcType = ibrionRaw == int.MinValue || ibrionRaw <= -1 ? "SP"
                            : ibrionRaw == 0                               ? "MD"
                            : ibrionRaw >= 1 && ibrionRaw <= 3             ? "OPT"
                            : ibrionRaw >= 5 && ibrionRaw <= 8             ? "FREQ"
                            : "SP";

            // ── Align step lists (truncate to paired data) ───────────────────
            int nSteps = Math.Min(allStepPositions.Count, allStepEnergies.Count);

            var outcarData = new OutcarData
            {
                StepPositions = allStepPositions.GetRange(0, allStepPositions.Count),
                StepEnergies  = allStepEnergies.GetRange(0, nSteps),
                AtomElements  = atomElements,
                LatticeA      = latticeA,
                LatticeB      = latticeB,
                LatticeC      = latticeC,
            };

            // ── Build summary ────────────────────────────────────────────────
            double? finalEnergy = allStepEnergies.Count > 0
                                    ? allStepEnergies[allStepEnergies.Count - 1]
                                    : (double?)null;

            var summary = new QuantumSummary
            {
                Software          = SoftwareType.VASP,
                CalcType          = calcType,
                NormalTermination = finalEnergy.HasValue,
                AtomCounts        = counts,
                TotalEnergyEV     = finalEnergy,
                FermiEnergyEV     = fermiEnergy,
                Encut             = encut,
                KpointMesh        = kpointMesh,
                Ismear            = ismear,
                Sigma             = sigma,
                Ediff             = ediff,
                Ediffg            = ediffg,
                Isif              = isif,
                Ibrion            = ibrionRaw == int.MinValue ? (int?)null : ibrionRaw,
            };

            return new ParseResult { Summary = summary, Molecule = mol, OutcarStepData = outcarData };
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string[] MapElements(List<string> symbols, int[] counts, int total)
        {
            if (symbols.Count > 0 && counts != null && counts.Length == symbols.Count)
            {
                var list = new List<string>();
                for (int i = 0; i < symbols.Count; i++)
                    for (int j = 0; j < counts[i]; j++)
                        list.Add(symbols[i]);
                if (list.Count == total) return list.ToArray();
            }
            var fallback = new string[total];
            for (int i = 0; i < total; i++) fallback[i] = "X";
            return fallback;
        }

        private static Dictionary<string, int> BuildCounts(List<Atom> atoms)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in atoms)
            {
                if (d.ContainsKey(a.Element)) d[a.Element]++;
                else d[a.Element] = 1;
            }
            return d;
        }

        private static int ArraySum(int[] arr)
        {
            int s = 0;
            foreach (int x in arr) s += x;
            return s;
        }

        private static string[] Split(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
            return s.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool TryFloat(string s, out double val)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);

        private static string NormalizeElement(string sym)
        {
            if (string.IsNullOrEmpty(sym)) return "X";
            int under = sym.IndexOf('_');
            if (under > 0) sym = sym.Substring(0, under);
            if (sym.Length == 0) return "X";
            return char.ToUpperInvariant(sym[0]) +
                   (sym.Length > 1 ? sym.Substring(1).ToLowerInvariant() : "");
        }
    }
}
