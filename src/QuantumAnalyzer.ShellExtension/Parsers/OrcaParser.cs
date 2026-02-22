using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    public class OrcaParser : IQuantumParser
    {
        private const double HartreeToEv = 27.211385;

        // ── Large-file optimisation ───────────────────────────────────────────────
        // ORCA optimization/frequency jobs grow to 100+ MB because every SCF cycle
        // and every gradient step is printed in full.  Everything we actually need
        // for the preview / thumbnail is either at the very start of the file
        // (input block → method/basis/charge/mult) or at the very end
        // (final geometry, final energy, thermochemistry, orbital energies,
        // termination flag, imaginary frequencies).
        //
        // Strategy: for files larger than LargeFileThreshold, read only the first
        // HeadLineCount lines and the last TailBytes, concatenate them, and run the
        // normal ParseFull() parser on the result.  I/O drops from ~100 MB to ~4 MB.
        private const long LargeFileThreshold = 5L * 1024 * 1024;  // 5 MB
        private const int  HeadLineCount      = 400;                // lines from start (ORCA 6.x has ~220 lines of preamble before INPUT FILE)
        private const long TailBytes          = 4L * 1024 * 1024;  // 4 MB from end

        // ──────────────────────────────────────────────────────────────────────────

        public bool CanParse(string[] firstLines)
        {
            bool hasExclaim = false, hasStarXyz = false;
            foreach (string line in firstLines)
            {
                if (line == null) continue;
                if (line.IndexOf("O   R   C   A", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (line.IndexOf("ORCA", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (line.IndexOf("This ORCA binary", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                // Detect ORCA input files (! keyword line + * xyz coordinate block)
                string t = line.TrimStart();
                if (t.StartsWith("!")) hasExclaim = true;
                if (t.StartsWith("*") && line.IndexOf("xyz", StringComparison.OrdinalIgnoreCase) >= 0) hasStarXyz = true;
            }
            return hasExclaim && hasStarXyz;
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
        // Reads the first HeadLineCount lines, then does a fast backward chunk scan
        // to locate the last GEOMETRY OPTIMIZATION CYCLE (gives hasOpt + final coords),
        // then reads the last TailBytes for energy/thermo/orbitals/freq/termination.
        // I/O drops from ~100 MB to ~4 MB even for large OPT+FREQ jobs.
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

            // Find last GEOMETRY OPTIMIZATION CYCLE (backward chunk scan) ────────
            // GEOMETRY OPTIMIZATION CYCLE lives in the middle of the file and is
            // missed by the tail window.  Backward scan stops as soon as found
            // (typically 1-2 chunks from the end for OPT+FREQ jobs).
            long optCyclePos = FindLastByteOffset(stream, "GEOMETRY OPTIMIZATION CYCLE");
            List<string> optCoordLines = null;
            if (optCyclePos >= 0)
                optCoordLines = ReadOptCoordBlock(stream, optCyclePos);

            // Find last ORBITAL ENERGIES section (backward chunk scan) ────────────
            // For large SP jobs the orbital table can sit far from the end of the
            // file (e.g. line 737k in a 1.35M-line file) and is never reached by
            // the 4 MB tail window.
            long orbEnergiesPos = FindLastByteOffset(stream, "ORBITAL ENERGIES");
            List<string> midOrbitalLines = null;
            if (orbEnergiesPos >= 0)
                midOrbitalLines = ReadOrbitalEnergiesBlock(stream, orbEnergiesPos);

            // Tail ────────────────────────────────────────────────────────────────
            long tailStart = stream.Length - TailBytes; // > 0: guaranteed by threshold check
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

            // Apply OPT overrides ─────────────────────────────────────────────────
            if (optCyclePos >= 0)
            {
                // GEOMETRY OPTIMIZATION CYCLE is not in the tail, so ParseFull
                // will have set CalcType to SP or FREQ.  Correct it here.
                if (result.Summary.CalcType == "SP")
                    result.Summary.CalcType = "OPT";
                else if (result.Summary.CalcType == "FREQ")
                    result.Summary.CalcType = "OPT+FREQ";

                // Override the molecule with coordinates from the last opt cycle.
                // The tail window may not contain CARTESIAN COORDINATES (ANGSTROEM)
                // at all (e.g. when the freq section dominates the last 4 MB).
                if (optCoordLines != null && optCoordLines.Count > 0)
                {
                    var molecule = new Molecule();
                    foreach (string ol in optCoordLines)
                    {
                        string[] parts = ol.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
                    if (molecule.Atoms.Count > 0)
                    {
                        molecule.Bonds = BondDetector.Detect(molecule.Atoms);
                        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var atom in molecule.Atoms)
                        {
                            if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                            else counts[atom.Element] = 1;
                        }
                        result.Molecule = molecule;
                        result.Summary.AtomCounts = counts;
                    }
                }
            }

            // Override orbital energies from direct mid-file scan ─────────────────
            // The backward scan guarantees we use the LAST ORBITAL ENERGIES section,
            // even when it sits outside the 4 MB tail window.
            if (midOrbitalLines != null && midOrbitalLines.Count > 0)
                ParseOrbitalEnergies(midOrbitalLines, result.Summary);

            // Large-file mode parses head+tail, which can miss early optimization steps.
            // Recover the full optimization profile via a lightweight full-stream scan.
            var fullOptEnergiesEh = ExtractAllFinalSinglePointEnergiesEh(stream);
            if (fullOptEnergiesEh.Count > 1)
            {
                var fullOptEnergiesEv = new List<double>(fullOptEnergiesEh.Count);
                foreach (double e in fullOptEnergiesEh)
                    fullOptEnergiesEv.Add(e * HartreeToEv);
                result.OptimizationStepEnergiesEV = fullOptEnergiesEv;
            }

            var fullFrames = ExtractAllOrcaCoordinateFrames(stream);
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

        // ── Backward chunk scan ───────────────────────────────────────────────────
        // Scans the stream right-to-left in 64 KB chunks to find the byte offset of
        // the last occurrence of marker (UTF-8, exact case).
        // An overlap of (markerLen - 1) bytes is carried between adjacent chunks so
        // that a match is never missed at a chunk boundary.
        // Returns -1 if not found.  No external dependencies required.
        private static long FindLastByteOffset(Stream stream, string marker)
        {
            byte[] pat = Encoding.UTF8.GetBytes(marker);
            if (pat.Length == 0) return -1;

            int overlap = pat.Length - 1;
            const int chunkSize = 65536; // 64 KB
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

                // Append bytes saved from the chunk to our right so cross-boundary matches are caught
                if (rightOverlapLen > 0)
                    Buffer.BlockCopy(rightOverlapBuf, 0, buffer, bytesRead, rightOverlapLen);

                int bufLen = bytesRead + rightOverlapLen;

                // Walk right-to-left; only count a match whose START is within [0, mainLen)
                // so we never double-report a match already found from the right chunk.
                for (int i = bufLen - pat.Length; i >= 0; i--)
                {
                    if (i >= mainLen) continue; // in right-overlap territory → skip

                    bool found = true;
                    for (int j = 0; j < pat.Length; j++)
                    {
                        if (buffer[i + j] != pat[j]) { found = false; break; }
                    }
                    if (found) return chunkLeft + i;
                }

                // Save the leftmost bytes of this chunk as right-overlap for the next (leftward) chunk
                rightOverlapLen = Math.Min(overlap, bytesRead);
                if (rightOverlapLen > 0)
                    Buffer.BlockCopy(buffer, 0, rightOverlapBuf, 0, rightOverlapLen);

                chunkRight = chunkLeft;
            }

            return -1;
        }

        // ── Read coordinate block following a GEOMETRY OPTIMIZATION CYCLE ─────────
        // Seeks to fromOffset, then scans forward for "CARTESIAN COORDINATES (ANGSTROEM)"
        // and collects atom lines until "CARTESIAN COORDINATES (A.U.)" or a blank line.
        private static List<string> ReadOptCoordBlock(Stream stream, long fromOffset)
        {
            const int MaxScanLines = 500; // guard against runaway reads
            stream.Seek(fromOffset, SeekOrigin.Begin);

            var coords = new List<string>();
            bool inCoord = false;
            int skipLines = 0;

            // leaveOpen: true so we don't close the underlying FileStream
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                for (int i = 0; i < MaxScanLines; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;

                    if (!inCoord)
                    {
                        if (line.IndexOf("CARTESIAN COORDINATES (ANGSTROEM)", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            inCoord = true;
                            skipLines = 1; // skip the dashes line
                        }
                        continue;
                    }

                    if (skipLines > 0) { skipLines--; continue; }

                    string trimmed = line.Trim();
                    if (trimmed.IndexOf("CARTESIAN COORDINATES (A.U.)", StringComparison.OrdinalIgnoreCase) >= 0)
                        break;
                    if (string.IsNullOrWhiteSpace(trimmed))
                        break;

                    coords.Add(line);
                }
            }

            return coords;
        }

        // ── Read orbital energies block ───────────────────────────────────────────
        // Seeks to fromOffset (byte position of "ORBITAL ENERGIES" string), then reads
        // the orbital data rows that follow the column header.  Stops at the first
        // blank line after data has started.  Returns only the raw data lines (no
        // header), which ParseOrbitalEnergies() can consume directly.
        private static List<string> ReadOrbitalEnergiesBlock(Stream stream, long fromOffset)
        {
            const int MaxScanLines = 5000; // enough for any realistic orbital count
            stream.Seek(fromOffset, SeekOrigin.Begin);
            var lines = new List<string>();
            bool dataStarted = false;

            // leaveOpen: true so we don't close the underlying FileStream
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                reader.ReadLine(); // consume the partial/full "ORBITAL ENERGIES" line

                for (int i = 0; i < MaxScanLines; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    string trimmed = line.Trim();

                    if (!dataStarted)
                    {
                        // Skip dashes, blank lines, and the "NO  OCC  E(Eh)  E(eV)" header
                        if (trimmed.StartsWith("---") ||
                            string.IsNullOrWhiteSpace(trimmed) ||
                            trimmed.StartsWith("NO", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // First actual data row: must start with an integer orbital index
                        string[] p = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3 && int.TryParse(p[0], out _))
                        {
                            dataStarted = true;
                            lines.Add(line);
                        }
                        continue;
                    }

                    // In data: blank line signals end of block
                    if (string.IsNullOrWhiteSpace(trimmed)) break;

                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || !int.TryParse(parts[0], out _)) break;

                    lines.Add(line);
                }
            }

            return lines;
        }

        private static List<double> ExtractAllFinalSinglePointEnergiesEh(Stream stream)
        {
            var energies = new List<double>();
            stream.Seek(0, SeekOrigin.Begin);

            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var m = Regex.Match(line, @"FINAL SINGLE POINT ENERGY\s+([-\d.]+)", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    if (double.TryParse(
                        m.Groups[1].Value,
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

        private static Molecule BuildMoleculeFromOrcaCoordBlock(List<string> coordBlock)
        {
            if (coordBlock == null || coordBlock.Count == 0) return null;
            var molecule = new Molecule();
            foreach (string ol in coordBlock)
            {
                string[] parts = ol.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
            if (molecule.Atoms.Count == 0) return null;
            molecule.Bonds = BondDetector.Detect(molecule.Atoms);
            return molecule;
        }

        private static List<Molecule> ExtractAllOrcaCoordinateFrames(Stream stream)
        {
            var frames = new List<Molecule>();
            stream.Seek(0, SeekOrigin.Begin);

            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                bool inCoord = false;
                int skipLines = 0;
                var block = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (!inCoord)
                    {
                        if (line.IndexOf("CARTESIAN COORDINATES (ANGSTROEM)", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            inCoord = true;
                            skipLines = 1;
                            block = new List<string>();
                        }
                        continue;
                    }

                    if (skipLines > 0) { skipLines--; continue; }
                    if (string.IsNullOrWhiteSpace(trimmed) ||
                        trimmed.IndexOf("CARTESIAN COORDINATES (A.U.)", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        inCoord = false;
                        var mol = BuildMoleculeFromOrcaCoordBlock(block);
                        if (mol != null && mol.HasGeometry)
                            frames.Add(mol);
                        continue;
                    }

                    block.Add(line);
                }
            }

            return frames;
        }

        // ── Full line-by-line parse ───────────────────────────────────────────────
        // Used directly for small files; called with the head+tail slice for large ones.
        private ParseResult ParseFull(TextReader reader)
        {
            var summary = new QuantumSummary { Software = SoftwareType.Orca };
            var molecule = new Molecule();

            // Coordinate block tracking (keep last occurrence)
            var currentCoordBlock = new List<string>();
            var lastCoordBlock    = new List<string>();
            var allCoordBlocks    = new List<List<string>>();
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
            double? kBT = null;
            var optimizationStepEnergiesEh = new List<double>();

            // Buffer all lines so a second pass can handle input files (.inp)
            var allLines = new List<string>();
            { string _t; while ((_t = reader.ReadLine()) != null) allLines.Add(_t); }

            string line;
            foreach (string _lineItem in allLines)
            {
                line = _lineItem;
                string trimmed = line.Trim();

                // ── Detect calc type from section headers ──────────────
                if (line.IndexOf("ORCA FREQUENCY CALCULATION", StringComparison.OrdinalIgnoreCase) >= 0)    hasFreq = true;
                if (line.IndexOf("VIBRATIONAL FREQUENCIES",    StringComparison.OrdinalIgnoreCase) >= 0)    hasFreq = true;  // in tail when ORCA FREQUENCY CALCULATION header is not
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
                        if (currentCoordBlock.Count > 0)
                            allCoordBlocks.Add(new List<string>(currentCoordBlock));
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
                    optimizationStepEnergiesEh.Add(summary.ElectronicEnergy.Value);
                }
                else if (summary.ElectronicEnergy == null)
                {
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
                    inOrbitalEnergies   = true;
                    orbitalHeaderPassed = false;
                    orbitalLines = new List<string>();
                    continue;
                }
                if (inOrbitalEnergies)
                {
                    if (trimmed.StartsWith("---") && !orbitalHeaderPassed) { orbitalHeaderPassed = true; continue; }

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

            if (summary.ElectronicEnergy.HasValue && summary.ZPE.HasValue)
                summary.EE_ZPE = summary.ElectronicEnergy.Value + summary.ZPE.Value;

            if (summary.ThermalEnergy.HasValue)
                summary.ThermalEnthalpy = summary.ThermalEnergy.Value + (kBT ?? 0.0);

            if (hasFreq && hasOpt) summary.CalcType = "OPT+FREQ";
            else if (hasFreq)     summary.CalcType = "FREQ";
            else if (hasOpt)      summary.CalcType = "OPT";
            else                  summary.CalcType = "SP";

            ParseInputBlock(inputLines, summary);
            ParseOrbitalEnergies(orbitalLines, summary);

            // Build molecule from last coordinate block
            if (lastCoordBlock.Count > 0)
            {
                foreach (string ol in lastCoordBlock)
                {
                    string[] parts = ol.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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

            // Build per-step geometry frames from all coordinate blocks.
            List<Molecule> moleculeFrames = null;
            List<string> moleculeFrameNames = null;
            if (allCoordBlocks.Count > 1)
            {
                moleculeFrames = new List<Molecule>();
                moleculeFrameNames = new List<string>();
                int frameNo = 1;
                foreach (var block in allCoordBlocks)
                {
                    var frameMol = BuildMoleculeFromOrcaCoordBlock(block);
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

            // Input-file fallback: if nothing was parsed (ORCA .inp input file)
            if (summary.ElectronicEnergy == null && molecule.Atoms.Count == 0)
                TryExtractOrcaInputGeometry(allLines, molecule, summary);

            if (summary.ElectronicEnergy == null && molecule.Atoms.Count == 0) return null;

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

        // ── ORCA input-file geometry extraction (.inp) ────────────────────────────
        // Parses a plain ORCA input file: "! keywords" + "* xyz charge mult … *" block.
        // Called only when no ORCA output markers were found in the file.
        private static void TryExtractOrcaInputGeometry(List<string> allLines, Molecule molecule, QuantumSummary summary)
        {
            bool inXyzBlock = false;
            var inputKeyLines = new List<string>();

            foreach (string rawLine in allLines)
            {
                string t = rawLine.Trim();
                if (t.Length == 0) continue;

                // Collect keyword lines for method/basis/calcType
                if (t.StartsWith("!"))
                {
                    inputKeyLines.Add(t);
                    continue;
                }

                // Skip %block settings
                if (t.StartsWith("%")) continue;

                // "* xyz charge mult" or "* int charge mult" — start coordinate block
                if (!inXyzBlock && t.StartsWith("*") &&
                    t.IndexOf("xyz", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var tks = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // * xyz <charge> <mult>
                    if (tks.Length >= 4 &&
                        int.TryParse(tks[2], out int charge) &&
                        int.TryParse(tks[3], out int mult))
                    {
                        summary.Charge = charge;
                        summary.Spin   = MultiplicityToSpinName(mult);
                    }
                    inXyzBlock = true;
                    continue;
                }

                // "*" alone closes the coordinate block
                if (inXyzBlock && t == "*")
                {
                    inXyzBlock = false;
                    continue;
                }

                // Atom lines inside the block: "Element X Y Z"
                if (inXyzBlock)
                {
                    var tks = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tks.Length >= 4 && tks[0].Length >= 1 && char.IsLetter(tks[0][0]))
                    {
                        string elem = char.ToUpperInvariant(tks[0][0]) +
                            (tks[0].Length > 1 ? tks[0].Substring(1).ToLowerInvariant() : "");
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
            }

            if (molecule.Atoms.Count > 0)
            {
                molecule.Bonds = BondDetector.Detect(molecule.Atoms);
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var atom in molecule.Atoms)
                {
                    if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                    else counts[atom.Element] = 1;
                }
                summary.AtomCounts = counts;
                // Parse method/basis/calcType from the collected keyword lines
                if (inputKeyLines.Count > 0)
                    ParseInputBlock(inputKeyLines, summary);
                if (summary.CalcType == null) summary.CalcType = "SP";
                summary.Software = SoftwareType.Orca;
            }
        }

        private static void ParseInputBlock(List<string> lines, QuantumSummary s)
        {
            foreach (string line in lines)
            {
                if (!line.StartsWith("!")) continue;
                string tokens = line.TrimStart('!').Trim();

                string[] parts = tokens.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                string firstTok = parts[0].ToUpperInvariant();
                if (firstTok == "WARNING" || firstTok == "ERROR" || firstTok == "NOTE"
                    || firstTok == "CAUTION" || firstTok == "***") continue;

                if (s.Method == null)
                {
                    var basisSets = new List<string>();

                    if (parts[0].Contains("/"))
                    {
                        var split = parts[0].Split('/');
                        s.Method = split[0].ToUpperInvariant();
                        basisSets.Add(split[1]);
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (!IsKeyword(parts[i]))
                                basisSets.Add(parts[i]);
                        }
                    }
                    else
                    {
                        s.Method = parts[0].ToUpperInvariant();
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
                case "FREQ": case "OPT": case "SP":
                case "TIGHTSCF": case "VERYTIGHTSCF": case "NORMALSCF": case "LOOSESCF":
                case "DEFGRID1": case "DEFGRID2": case "DEFGRID3":
                case "RIJCOSX": case "NORI": case "RI":
                case "LARGEPRINT": case "MINIPRINT": case "NOPRINT":
                case "ENGRAD": case "NUMGRAD": case "NUMFREQ": case "ANFREQ":
                case "TIGHTOPT": case "VERYTIGHTOPT":
                case "SLOWCONV": case "NBO": case "NPA": case "CONV": case "NOCONV":
                case "LEANSCF": case "AODIIS": case "SOSCF": case "NOSOSCF":
                case "KDIIS": case "DIIS": case "NRSCF":
                case "D3": case "D3BJ": case "D4": case "D3ZERO":
                case "BOHRS": case "UHF": case "RHF": case "ROHF":
                case "UKS": case "RKS": case "ROKS":
                case "MOREAD": case "AUTOSTART": case "NOAUTOSTART":
                case "KEEPDENS": case "NOFROZENCORE": case "FROZENCORE":
                case "CPCM": case "SMD":
                    return true;
                default:
                    if (upper.StartsWith("GRID") || upper.StartsWith("AUX"))
                        return true;
                    if (upper.Length > 2 && upper.StartsWith("NO") && tok.Length > 2 && char.IsUpper(tok[2]))
                        return true;
                    return false;
            }
        }

        private static void ParseOrbitalEnergies(List<string> lines, QuantumSummary s)
        {
            double lastOccEh   = double.NaN;
            double firstVirtEh = double.NaN;
            int    lastOccNo   = 0;
            bool   virtFound   = false;

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
                s.HomoIndex   = lastOccNo + 1;
                s.LumoIndex   = lastOccNo + 2;
                s.HomoSpin    = "Alpha";
                s.HomoLumoGap = (firstVirtEh - lastOccEh) * HartreeToEv;
            }
        }

        private static void ParseThermoLine(string line, QuantumSummary s)
        {
            TryParseEh(line, "Zero point energy",        v => s.ZPE              = v);
            TryParseEh(line, "Total thermal correction", v => s.ThermalEnergy    = v);
            TryParseEh(line, "G-E(el)",                  v => s.ThermalFreeEnergy = v);
            TryParseEh(line, "Total thermal energy",     v => s.EE_Thermal       = v);
            TryParseEh(line, "Total Enthalpy",           v => s.EE_Enthalpy      = v);
            TryParseEh(line, "Final Gibbs free energy",  v => s.EE_FreeEnergy    = v);

            if (s.EThermal_kcal == null &&
                line.IndexOf("Total thermal correction", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var matches = Regex.Matches(line, @"-?\d+\.\d+");
                if (matches.Count >= 2 && double.TryParse(matches[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double kcal))
                    s.EThermal_kcal = kcal;
            }

            if (s.Entropy == null &&
                line.IndexOf("Final entropy term", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var m = Regex.Match(line, @"-?\d+\.\d+");
                if (m.Success && double.TryParse(m.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double tSEh))
                    s.Entropy = tSEh * 627509.47 / 298.15;
            }
        }

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
