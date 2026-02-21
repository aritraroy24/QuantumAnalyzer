using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for Gaussian .cube files containing 3D volumetric data
    /// (electron density, molecular orbital coefficients, etc.).
    ///
    /// Format:
    ///   Line 1-2: Comment / title lines
    ///   Line 3:   NAtoms OX OY OZ  (atom count, negative = MO cube; origin in Bohr)
    ///   Line 4-6: N dX dY dZ       (grid dimension + axis step vector in Bohr)
    ///   Lines 7..(7+|NAtoms|-1): Z charge X Y Z  (atomic number, Bohr coords)
    ///   Remaining: NX*NY*NZ floats, up to 6 per line
    ///
    /// All Bohr coordinates are converted to Angstroms (× 0.529177).
    /// </summary>
    public class CubeParser : IQuantumParser
    {
        private const double BohrToAngstrom = 0.529177;

        public bool CanParse(string[] firstLines)
        {
            // Need at least 4 lines (2 comments + atom count line + one axis line)
            if (firstLines.Length < 4) return false;

            // Line 3 (index 2): integer  float float float
            if (!MatchesAtomCountLine(firstLines[2])) return false;

            // Line 4 (index 3): integer  float float float  (grid axis)
            if (!MatchesAxisLine(firstLines[3])) return false;

            return true;
        }

        public ParseResult Parse(TextReader reader)
        {
            // Line 1 & 2: comments
            string comment1 = reader.ReadLine() ?? "";
            string comment2 = reader.ReadLine() ?? "";
            string dataLabel = (comment1.Trim() + " " + comment2.Trim()).Trim();

            // Line 3: NAtoms OX OY OZ
            string line3 = reader.ReadLine();
            if (line3 == null) return null;
            var p3 = SplitLine(line3);
            if (p3.Length < 4) return null;
            if (!int.TryParse(p3[0], out int nAtomsRaw)) return null;
            int nAtoms = Math.Abs(nAtomsRaw);

            double ox = ParseDouble(p3[1]);
            double oy = ParseDouble(p3[2]);
            double oz = ParseDouble(p3[3]);

            // Lines 4-6: NX dX0 dX1 dX2 / NY ... / NZ ...
            int[] dims = new int[3];
            double[,] axes = new double[3, 3];
            for (int axis = 0; axis < 3; axis++)
            {
                string axLine = reader.ReadLine();
                if (axLine == null) return null;
                var pa = SplitLine(axLine);
                if (pa.Length < 4) return null;
                if (!int.TryParse(pa[0], out dims[axis])) return null;
                axes[axis, 0] = ParseDouble(pa[1]);
                axes[axis, 1] = ParseDouble(pa[2]);
                axes[axis, 2] = ParseDouble(pa[3]);
            }

            int nx = dims[0], ny = dims[1], nz = dims[2];
            if (nx <= 0 || ny <= 0 || nz <= 0) return null;

            // Convert axes from Bohr to Angstrom
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    axes[i, j] *= BohrToAngstrom;

            // Origin in Angstrom
            double[] origin = {
                ox * BohrToAngstrom,
                oy * BohrToAngstrom,
                oz * BohrToAngstrom
            };

            // Read atom lines
            var molecule = new Molecule();
            for (int i = 0; i < nAtoms; i++)
            {
                string atomLine = reader.ReadLine();
                if (atomLine == null) break;
                var pa = SplitLine(atomLine);
                if (pa.Length < 5) continue;
                if (!int.TryParse(pa[0], out int atomicNum)) continue;

                string sym = ElementData.SymbolFromAtomicNumber(atomicNum);
                double ax = ParseDouble(pa[2]) * BohrToAngstrom;
                double ay = ParseDouble(pa[3]) * BohrToAngstrom;
                double az = ParseDouble(pa[4]) * BohrToAngstrom;
                molecule.Atoms.Add(new Atom(sym, ax, ay, az));
            }

            if (molecule.Atoms.Count > 0)
                molecule.Bonds = BondDetector.Detect(molecule.Atoms);

            // Read volumetric data: NX*NY*NZ floats (row-major: ix fastest? No — Gaussian cube is ix outer, iy middle, iz inner)
            var data = new float[nx, ny, nz];
            long total = (long)nx * ny * nz;
            long read = 0;
            int ix = 0, iy = 0, iz = 0;

            string dataLine;
            while (read < total && (dataLine = reader.ReadLine()) != null)
            {
                var tokens = SplitLine(dataLine);
                foreach (var tok in tokens)
                {
                    if (read >= total) break;
                    if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        data[ix, iy, iz] = val;
                        read++;
                        iz++;
                        if (iz >= nz) { iz = 0; iy++; }
                        if (iy >= ny) { iy = 0; ix++; }
                    }
                }
            }

            var grid = new VolumetricGrid
            {
                NX = nx, NY = ny, NZ = nz,
                Origin = origin,
                Axes = axes,
                Data = data,
                DataLabel = dataLabel,
            };

            var summary = new QuantumSummary
            {
                Software = SoftwareType.CubeFile,
                CalcType = "Volumetric",
            };

            // Atom composition
            if (molecule.Atoms.Count > 0)
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var atom in molecule.Atoms)
                {
                    if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                    else counts[atom.Element] = 1;
                }
                summary.AtomCounts = counts;
            }

            return new ParseResult
            {
                Summary = summary,
                Molecule = molecule.Atoms.Count > 0 ? molecule : null,
                VolumetricData = grid,
            };
        }

        // ──────────────────────────────────────────────────────────────────

        private static bool MatchesAtomCountLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var p = SplitLine(line);
            if (p.Length < 4) return false;
            if (!int.TryParse(p[0], out _)) return false;
            return TryParseDouble(p[1]) && TryParseDouble(p[2]) && TryParseDouble(p[3]);
        }

        private static bool MatchesAxisLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var p = SplitLine(line);
            if (p.Length < 4) return false;
            // First token must be a positive integer (grid dimension)
            if (!int.TryParse(p[0], out int n) || n <= 0) return false;
            return TryParseDouble(p[1]) && TryParseDouble(p[2]) && TryParseDouble(p[3]);
        }

        private static string[] SplitLine(string line)
        {
            return line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static double ParseDouble(string s)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
        }

        private static bool TryParseDouble(string s)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }
    }
}
