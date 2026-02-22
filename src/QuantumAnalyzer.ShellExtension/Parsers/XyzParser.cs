using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for XYZ coordinate files (.xyz).
    /// Supports both single-structure and multi-structure XYZ files.
    /// </summary>
    public class XyzParser : IQuantumParser
    {
        public bool CanParse(string[] firstLines)
        {
            if (firstLines.Length < 3) return false;

            string first = firstLines[0]?.Trim();
            if (!int.TryParse(first, out int atomCount) || atomCount <= 0) return false;

            string third = firstLines[2]?.Trim();
            if (string.IsNullOrEmpty(third)) return false;

            var parts = third.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;
            if (!Regex.IsMatch(parts[0], @"^[A-Za-z]{1,2}$")) return false;

            return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        public ParseResult Parse(TextReader reader)
        {
            var frames = new List<Molecule>();
            var frameNames = new List<string>();

            while (TryReadNextFrame(reader, out Molecule frame, out string frameName))
            {
                if (frame == null || !frame.HasGeometry) continue;
                frames.Add(frame);
                frameNames.Add(frameName);
            }

            if (frames.Count == 0) return null;

            var firstFrame = frames[0];
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var atom in firstFrame.Atoms)
            {
                if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                else counts[atom.Element] = 1;
            }

            var summary = new QuantumSummary
            {
                Software = SoftwareType.Structure,
                CalcType = "Structure",
                AtomCounts = counts,
            };

            return new ParseResult
            {
                Summary = summary,
                Molecule = firstFrame,
                MoleculeFrames = frames,
                MoleculeFrameNames = frameNames,
            };
        }

        private static bool TryReadNextFrame(TextReader reader, out Molecule frame, out string frameName)
        {
            frame = null;
            frameName = null;

            string atomCountLine = ReadNextNonEmptyLine(reader);
            if (atomCountLine == null) return false;

            if (!int.TryParse(atomCountLine.Trim(), out int atomCount) || atomCount <= 0)
            {
                // Keep scanning: malformed chunks should not abort parsing of later frames.
                return TryReadNextFrame(reader, out frame, out frameName);
            }

            string commentLine = reader.ReadLine();
            frameName = string.IsNullOrWhiteSpace(commentLine) ? null : commentLine.Trim();

            var atoms = new List<Atom>(atomCount);
            for (int i = 0; i < atomCount; i++)
            {
                string atomLine = reader.ReadLine();
                if (atomLine == null)
                {
                    // Incomplete frame at EOF.
                    return false;
                }

                if (!TryParseAtomLine(atomLine, out Atom atom))
                {
                    // Malformed frame: skip this frame but continue scanning next ones.
                    return TryReadNextFrame(reader, out frame, out frameName);
                }

                atoms.Add(atom);
            }

            frame = new Molecule { Atoms = atoms, Bonds = BondDetector.Detect(atoms) };
            return true;
        }

        private static string ReadNextNonEmptyLine(TextReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line)) return line;
            }
            return null;
        }

        private static bool TryParseAtomLine(string line, out Atom atom)
        {
            atom = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;
            if (!Regex.IsMatch(parts[0], @"^[A-Za-z]{1,2}$")) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return false;
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) return false;

            string symbol = char.ToUpperInvariant(parts[0][0])
                + (parts[0].Length > 1 ? parts[0].Substring(1).ToLowerInvariant() : "");

            atom = new Atom(symbol, x, y, z);
            return true;
        }
    }
}
