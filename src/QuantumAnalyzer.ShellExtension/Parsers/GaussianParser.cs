using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    public class GaussianParser : IQuantumParser
    {
        private const double HartreeToEv = 27.211385;

        // ── Large-file optimisation ───────────────────────────────────────────────
        // Gaussian OPT/FREQ jobs repeat the full orientation block and eigenvalues
        // for every step, growing to 100+ MB.  All we need is:
        //   HEAD  — route section, charge/mult, gen basis (always in first ~50–200 lines)
        //   BACKWARD SCAN — last "Standard orientation:" / "Input orientation:" for geometry
        //   TAIL  — last SCF Done, eigenvalues, frequencies, thermochemistry, termination flag
        private const long LargeFileThreshold = 5L * 1024 * 1024;  // 5 MB
        private const int  HeadLineCount      = 300;                 // lines from start
        private const long TailBytes          = 4L * 1024 * 1024;   // 4 MB from end

        // ──────────────────────────────────────────────────────────────────────────

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
            // Route large seekable files through the head+tail optimisation.
            if (reader is StreamReader sr &&
                sr.BaseStream.CanSeek &&
                sr.BaseStream.Length > LargeFileThreshold)
            {
                return ParseLargeFile(sr);
            }
            return ParseFull(reader);
        }

        // ── Head + tail optimisation ──────────────────────────────────────────────
        // Reads the first HeadLineCount lines (route/basis/charge), does a fast
        // backward chunk scan to locate the last orientation block (final geometry),
        // then reads the last TailBytes (energy/eigenvalues/thermo/termination).
        // CalcType is determined from the route section in the HEAD, so no
        // post-hoc correction is needed (unlike the ORCA parser).
        private ParseResult ParseLargeFile(StreamReader sr)
        {
            var stream = sr.BaseStream;
            var sb = new StringBuilder();

            // Head ────────────────────────────────────────────────────────────────
            stream.Seek(0, SeekOrigin.Begin);
            sr.DiscardBufferedData();
            for (int i = 0; i < HeadLineCount; i++)
            {
                string ln = sr.ReadLine();
                if (ln == null) break;
                sb.AppendLine(ln);
            }

            // Find last orientation block (backward chunk scan) ───────────────────
            // Prefer "Standard orientation:" (default); fall back to "Input orientation:"
            // (printed when nosymm is used).
            long orientPos = FindLastByteOffset(stream, "Standard orientation:");
            if (orientPos < 0)
                orientPos = FindLastByteOffset(stream, "Input orientation:");

            List<string> orientLines = null;
            if (orientPos >= 0)
                orientLines = ReadGaussianOrientationBlock(stream, orientPos);

            // Tail ────────────────────────────────────────────────────────────────
            long tailStart = stream.Length - TailBytes;
            stream.Seek(tailStart, SeekOrigin.Begin);
            sr.DiscardBufferedData();
            sr.ReadLine(); // discard the partial line at the seek boundary

            string tl;
            while ((tl = sr.ReadLine()) != null)
                sb.AppendLine(tl);

            // Parse the combined head+tail slice with the normal parser
            ParseResult result;
            using (var combined = new StringReader(sb.ToString()))
                result = ParseFull(combined);

            if (result == null) return null;

            // Override molecule with coordinates from the last orientation block.
            // The tail window may not contain an orientation block at all for very
            // large OPT+FREQ jobs where thermochemistry dominates the last 4 MB.
            if (orientLines != null && orientLines.Count > 0)
            {
                var molecule = new Molecule();
                foreach (string ol in orientLines)
                {
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
                if (molecule.Atoms.Count > 0)
                {
                    molecule.Bonds = BondDetector.Detect(molecule.Atoms);
                    result.Molecule = molecule;
                    result.Summary.AtomCounts = BuildAtomCounts(molecule.Atoms);
                }
            }

            // Large-file mode parses head+tail, which can miss many intermediate SCF blocks.
            // Recover full optimization profile via a lightweight full-stream energy scan.
            var fullScfEnergiesEh = ExtractAllScfEnergiesEh(stream);
            if (fullScfEnergiesEh.Count > 1)
            {
                var fullScfEnergiesEv = new List<double>(fullScfEnergiesEh.Count);
                foreach (double e in fullScfEnergiesEh)
                    fullScfEnergiesEv.Add(e * HartreeToEv);
                result.OptimizationStepEnergiesEV = fullScfEnergiesEv;
            }

            var fullFrames = ExtractAllGaussianOrientationFrames(stream);
            if (fullFrames.Count > 1)
            {
                result.MoleculeFrames = fullFrames;
                result.MoleculeFrameNames = new List<string>(fullFrames.Count);
                for (int i = 0; i < fullFrames.Count; i++)
                    result.MoleculeFrameNames.Add("Step " + (i + 1));
                result.Molecule = fullFrames[fullFrames.Count - 1];
            }

            return result;
        }

        // ── Full line-by-line parse ───────────────────────────────────────────────
        // Used directly for small files; called with the head+tail slice for large ones.
        private ParseResult ParseFull(TextReader reader)
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
            var standardOrientationBlocks = new List<List<string>>();
            var inputOrientationBlocks = new List<List<string>>();
            bool inOrientation = false;
            bool currentBlockIsStandard = false;
            int orientationHeaderCount = 0;

            bool normalTermination = false;
            int imagFreqCount = 0;
            var optimizationStepEnergiesEh = new List<double>();

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

            // Buffer all lines upfront so a second pass can handle input files (.gjf/.com)
            var allLines = new List<string>();
            { string _t; while ((_t = reader.ReadLine()) != null) allLines.Add(_t); }

            string line;
            foreach (string _lineItem in allLines)
            {
                line = _lineItem;
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
                            {
                                lastOrientationBlock = new List<string>(currentOrientationBlock);
                                standardOrientationBlocks.Add(new List<string>(currentOrientationBlock));
                            }
                            else
                            {
                                lastInputOrientationBlock = new List<string>(currentOrientationBlock);
                                inputOrientationBlocks.Add(new List<string>(currentOrientationBlock));
                            }
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
                    {
                        summary.ElectronicEnergy = scfEnergy;
                        optimizationStepEnergiesEh.Add(scfEnergy);
                    }
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
            else if (molecule.Atoms.Count == 0 && summary.CalcType != null)
            {
                // No orientation block found — likely a Gaussian input file (.gjf / .com).
                // Extract geometry from the "charge mult" + "Element X Y Z" input section.
                TryExtractInputGeometry(allLines, molecule, summary);
            }

            if (!summary.IsValid()) return null;

            // Build per-step geometry frames from all orientation blocks.
            var frameBlocks = standardOrientationBlocks.Count > 0 ? standardOrientationBlocks : inputOrientationBlocks;
            List<Molecule> moleculeFrames = null;
            List<string> moleculeFrameNames = null;
            if (frameBlocks.Count > 1)
            {
                moleculeFrames = new List<Molecule>();
                moleculeFrameNames = new List<string>();
                int frameNo = 1;
                foreach (var block in frameBlocks)
                {
                    var frameMol = BuildMoleculeFromGaussianOrientationBlock(block);
                    if (frameMol == null || !frameMol.HasGeometry) continue;
                    moleculeFrames.Add(frameMol);
                    moleculeFrameNames.Add("Step " + frameNo);
                    frameNo++;
                }
                if (moleculeFrames.Count <= 1)
                {
                    moleculeFrames = null;
                    moleculeFrameNames = null;
                }
            }

            List<double> optimizationStepEnergiesEv = null;
            if (optimizationStepEnergiesEh.Count > 1)
            {
                optimizationStepEnergiesEv = new List<double>(optimizationStepEnergiesEh.Count);
                foreach (double e in optimizationStepEnergiesEh)
                    optimizationStepEnergiesEv.Add(e * HartreeToEv);
            }

            return new ParseResult
            {
                Summary = summary,
                Molecule = molecule,
                MoleculeFrames = moleculeFrames,
                MoleculeFrameNames = moleculeFrameNames,
                OptimizationStepEnergiesEV = optimizationStepEnergiesEv,
            };
        }

        // ──────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────

        // ── Read orientation block from stream ────────────────────────────────────
        // Seeks to fromOffset (the start of "Standard/Input orientation:" line),
        // then collects the atom rows that sit between the 2nd and 3rd dashes blocks.
        // Gaussian orientation format:
        //   "                         Standard orientation:"
        //   " ------------..."   (1st dashes — column headers follow)
        //   " Center  Atomic  Atomic     Coordinates..."
        //   " ------------..."   (2nd dashes — atom data follows)
        //   "  1  6  0  0.000  0.000  0.000"
        //   " ------------..."   (3rd dashes — end of block)
        private static List<string> ReadGaussianOrientationBlock(Stream stream, long fromOffset)
        {
            const int MaxScanLines = 200;
            stream.Seek(fromOffset, SeekOrigin.Begin);

            var coords = new List<string>();
            int dashCount = 0;
            bool pastHeader = false; // skip the "Standard/Input orientation:" line itself

            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                for (int i = 0; i < MaxScanLines; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;

                    if (!pastHeader) { pastHeader = true; continue; }

                    if (line.Contains("-----"))
                    {
                        dashCount++;
                        if (dashCount == 3) break; // end of atom data
                        continue;
                    }

                    if (dashCount == 2) // between 2nd and 3rd dashes = atom rows
                        coords.Add(line);
                }
            }
            return coords;
        }

        private static List<double> ExtractAllScfEnergiesEh(Stream stream)
        {
            var energies = new List<double>();
            stream.Seek(0, SeekOrigin.Begin);

            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var scf = Regex.Match(line, @"SCF Done:\s+E\(\S+\)\s*=\s*([-\d.]+)");
                    if (!scf.Success) continue;
                    if (double.TryParse(
                        scf.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double e))
                    {
                        energies.Add(e);
                    }
                }
            }

            return energies;
        }

        private static Molecule BuildMoleculeFromGaussianOrientationBlock(List<string> orientationBlock)
        {
            if (orientationBlock == null || orientationBlock.Count == 0) return null;
            var molecule = new Molecule();
            foreach (string ol in orientationBlock)
            {
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

            if (molecule.Atoms.Count == 0) return null;
            molecule.Bonds = BondDetector.Detect(molecule.Atoms);
            return molecule;
        }

        private static List<Molecule> ExtractAllGaussianOrientationFrames(Stream stream)
        {
            var frames = new List<Molecule>();
            stream.Seek(0, SeekOrigin.Begin);

            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                bool inOrientation = false;
                int dashCount = 0;
                var block = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!inOrientation)
                    {
                        if (line.Contains("Standard orientation:") || line.Contains("Input orientation:"))
                        {
                            inOrientation = true;
                            dashCount = 0;
                            block = new List<string>();
                        }
                        continue;
                    }

                    if (line.Contains("-----"))
                    {
                        dashCount++;
                        if (dashCount == 3)
                        {
                            inOrientation = false;
                            var mol = BuildMoleculeFromGaussianOrientationBlock(block);
                            if (mol != null && mol.HasGeometry)
                                frames.Add(mol);
                        }
                        continue;
                    }

                    if (dashCount >= 2)
                        block.Add(line);
                }
            }

            return frames;
        }

        // ── Backward chunk scan ───────────────────────────────────────────────────
        // Identical algorithm to OrcaParser.FindLastByteOffset — scans right-to-left
        // in 64 KB chunks with (markerLen-1) byte overlap at chunk boundaries.
        // Returns the byte offset of the last occurrence, or -1 if not found.
        private static long FindLastByteOffset(Stream stream, string marker)
        {
            byte[] pat = Encoding.UTF8.GetBytes(marker);
            if (pat.Length == 0) return -1;

            int overlap = pat.Length - 1;
            const int chunkSize = 65536;
            byte[] buffer = new byte[chunkSize + overlap];
            byte[] rightOverlapBuf = overlap > 0 ? new byte[overlap] : null;
            int rightOverlapLen = 0;

            long chunkRight = stream.Length;
            while (chunkRight > 0)
            {
                long chunkLeft = Math.Max(0L, chunkRight - chunkSize);
                int mainLen = (int)(chunkRight - chunkLeft);

                stream.Seek(chunkLeft, SeekOrigin.Begin);
                int bytesRead = 0;
                while (bytesRead < mainLen)
                {
                    int n = stream.Read(buffer, bytesRead, mainLen - bytesRead);
                    if (n == 0) break;
                    bytesRead += n;
                }

                if (rightOverlapLen > 0)
                    Buffer.BlockCopy(rightOverlapBuf, 0, buffer, bytesRead, rightOverlapLen);

                int bufLen = bytesRead + rightOverlapLen;

                for (int i = bufLen - pat.Length; i >= 0; i--)
                {
                    if (i >= mainLen) continue;
                    bool found = true;
                    for (int j = 0; j < pat.Length; j++)
                    {
                        if (buffer[i + j] != pat[j]) { found = false; break; }
                    }
                    if (found) return chunkLeft + i;
                }

                rightOverlapLen = Math.Min(overlap, bytesRead);
                if (rightOverlapLen > 0)
                    Buffer.BlockCopy(buffer, 0, rightOverlapBuf, 0, rightOverlapLen);

                chunkRight = chunkLeft;
            }
            return -1;
        }

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

        // ── Input-file geometry extraction (.gjf / .com) ──────────────────────────
        // Scans the buffered line list for the charge/mult + atom-coordinate section
        // that Gaussian input files use.  Also reads gen basis names when present.
        // Called only when no orientation block was found in the file.
        private static void TryExtractInputGeometry(List<string> allLines, Molecule molecule, QuantumSummary summary)
        {
            bool genNeeded = summary.BasisSet != null &&
                (summary.BasisSet.Equals("gen",    StringComparison.OrdinalIgnoreCase) ||
                 summary.BasisSet.Equals("genecp", StringComparison.OrdinalIgnoreCase));

            // State machine:
            //  0 = scanning for end of route section
            //  1 = skip the title card (first non-blank line after route)
            //  2 = wait for blank line after title
            //  3 = look for charge/mult line ("int int")
            //  4 = read atom lines (Element X Y Z) until blank
            //  5 = read gen basis block
            int state = 0;
            bool inRoute = false;
            var genBasisNames = new List<string>();
            int genState = 0; // 0=element line, 1=basis name, 2=skip to ****

            foreach (string rawLine in allLines)
            {
                string t = rawLine.Trim();

                if (state == 0) // find end of route
                {
                    if (!inRoute && t.Length > 0 && t[0] == '#')
                        inRoute = true;
                    else if (inRoute && (t.Length == 0 || t.StartsWith("---")))
                        state = 1;
                }
                else if (state == 1) // skip title card
                {
                    if (t.Length > 0) state = 2;
                }
                else if (state == 2) // wait for blank after title
                {
                    if (t.Length == 0) state = 3;
                }
                else if (state == 3) // look for "charge mult" (two integers)
                {
                    if (t.Length == 0) continue;
                    var tks = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tks.Length == 2 &&
                        int.TryParse(tks[0], out int charge) &&
                        int.TryParse(tks[1], out int mult) &&
                        mult >= 1 && mult <= 10)
                    {
                        summary.Charge = charge;
                        summary.Spin   = MultiplicityToSpinName(mult);
                        state = 4;
                    }
                }
                else if (state == 4) // read atom coordinates
                {
                    if (t.Length == 0) { state = 5; continue; }
                    var tks = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tks.Length >= 4 && tks[0].Length >= 1 && char.IsLetter(tks[0][0]))
                    {
                        // Strip any fragment/ECP annotations: "C(Fragment=1)" → "C"
                        string elemRaw = tks[0];
                        int pi = elemRaw.IndexOf('(');
                        if (pi >= 0) elemRaw = elemRaw.Substring(0, pi);
                        string elem = char.ToUpperInvariant(elemRaw[0]) +
                            (elemRaw.Length > 1 ? elemRaw.Substring(1).ToLowerInvariant() : "");
                        if (double.TryParse(tks[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                            double.TryParse(tks[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double y) &&
                            double.TryParse(tks[3], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double z))
                        {
                            molecule.Atoms.Add(new Atom(elem, x, y, z));
                        }
                    }
                }
                else if (state == 5 && genNeeded) // read gen basis section
                {
                    if (genState == 0)
                    {
                        if (t.Length == 0) continue;
                        var tks = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tks.Length >= 2 && tks[tks.Length - 1] == "0")
                        {
                            bool allElems = true;
                            for (int i = 0; i < tks.Length - 1; i++)
                                if (!Regex.IsMatch(tks[i], @"^[A-Z][a-z]?$")) { allElems = false; break; }
                            if (allElems) genState = 1;
                        }
                    }
                    else if (genState == 1)
                    {
                        if (t.Length > 0)
                        {
                            string bn = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                            if (!genBasisNames.Contains(bn)) genBasisNames.Add(bn);
                            genState = 2;
                        }
                    }
                    else if (genState == 2)
                    {
                        if (t == "****") genState = 0;
                    }
                }
            }

            if (molecule.Atoms.Count > 0)
            {
                molecule.Bonds = BondDetector.Detect(molecule.Atoms);
                summary.AtomCounts = BuildAtomCounts(molecule.Atoms);
                if (genBasisNames.Count > 0)
                    summary.BasisSet = genBasisNames.Count == 1
                        ? genBasisNames[0]
                        : "(" + string.Join("+", genBasisNames) + ")";
            }
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
