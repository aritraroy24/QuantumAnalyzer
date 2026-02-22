using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    /// <summary>
    /// Parser for VASP CHGCAR files (charge density).
    ///
    /// CHGCAR format:
    ///   Lines 1-N: POSCAR-identical header (system name, scale, vectors, elements, counts, coords)
    ///   Blank line separator
    ///   Line: NGX NGY NGZ
    ///   Lines: NGX × NGY × NGZ charge-density values (ρ × Vcell), ix varies fastest
    ///
    /// Detection is filename-based only (CanParse always returns false).
    /// ParserFactory routes "CHGCAR" filenames to this parser before normal detection.
    /// </summary>
    public class ChgcarParser : IQuantumParser
    {
        public bool CanParse(string[] firstLines) => false;

        public ParseResult Parse(TextReader reader)
        {
            try
            {
                return DoParse(reader);
            }
            catch
            {
                return null;
            }
        }

        private ParseResult DoParse(TextReader reader)
        {
            // ── Parse POSCAR header (identical to PoscarParser) ───────────────────

            string systemName = reader.ReadLine()?.Trim() ?? "VASP Charge Density";

            string scaleLine = reader.ReadLine();
            string[] scaleTokens = Split(scaleLine);
            if (scaleTokens.Length == 0) return null;
            if (!TryFloat(scaleTokens[0], out double scale) || scale <= 0) return null;

            double[] va = ReadVector(reader, scale);
            double[] vb = ReadVector(reader, scale);
            double[] vc = ReadVector(reader, scale);
            if (va == null || vb == null || vc == null) return null;

            string line6 = reader.ReadLine()?.Trim() ?? "";
            string[] tokens6 = Split(line6);
            if (tokens6.Length == 0) return null;

            string[] elementSymbols;
            int[]    atomCounts;
            string   line7;

            bool isVasp5 = !int.TryParse(tokens6[0], out _);
            if (isVasp5)
            {
                elementSymbols = tokens6;
                line7 = reader.ReadLine()?.Trim() ?? "";
                string[] tokens7 = Split(line7);
                atomCounts = new int[tokens7.Length];
                for (int i = 0; i < tokens7.Length; i++)
                    if (!int.TryParse(tokens7[i], out atomCounts[i]) || atomCounts[i] < 0) return null;
                if (atomCounts.Length != elementSymbols.Length) return null;
                // Advance to next line (Selective dynamics / Direct / Cartesian)
                line7 = reader.ReadLine()?.Trim() ?? "";
            }
            else
            {
                atomCounts     = new int[tokens6.Length];
                elementSymbols = new string[tokens6.Length];
                for (int i = 0; i < tokens6.Length; i++)
                {
                    if (!int.TryParse(tokens6[i], out atomCounts[i]) || atomCounts[i] < 0) return null;
                    elementSymbols[i] = "X";
                }
                line7 = reader.ReadLine()?.Trim() ?? "";
            }

            // Skip optional "Selective dynamics" line
            string coordModeLine = line7;
            if (coordModeLine.Length > 0 && (coordModeLine[0] == 'S' || coordModeLine[0] == 's'))
                coordModeLine = reader.ReadLine()?.Trim() ?? "";

            bool direct = coordModeLine.Length == 0 || coordModeLine[0] == 'D' || coordModeLine[0] == 'd';

            var crystal = new LatticeCell
            {
                SystemName = systemName,
                VectorA    = va,
                VectorB    = vb,
                VectorC    = vc,
            };

            // Read atom coordinates
            for (int elemIdx = 0; elemIdx < elementSymbols.Length; elemIdx++)
            {
                string sym = NormalizeElement(elementSymbols[elemIdx]);
                for (int k = 0; k < atomCounts[elemIdx]; k++)
                {
                    string coordLine = reader.ReadLine();
                    if (coordLine == null) return null;
                    string[] parts = Split(coordLine);
                    if (parts.Length < 3) return null;

                    if (!TryFloat(parts[0], out double p0) ||
                        !TryFloat(parts[1], out double p1) ||
                        !TryFloat(parts[2], out double p2)) return null;

                    double x, y, z;
                    if (direct)
                    {
                        x = p0*va[0] + p1*vb[0] + p2*vc[0];
                        y = p0*va[1] + p1*vb[1] + p2*vc[1];
                        z = p0*va[2] + p1*vb[2] + p2*vc[2];
                    }
                    else
                    {
                        x = p0; y = p1; z = p2;
                    }
                    crystal.UnitCellAtoms.Add((sym, x, y, z));
                }
            }

            if (crystal.UnitCellAtoms.Count == 0) return null;

            // ── Skip lines until blank separator before volumetric section ────────
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) break;
            }

            // Find the NGX NGY NGZ line (first non-blank line after separator)
            string gridLine = null;
            while ((gridLine = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(gridLine)) break;
            }
            if (gridLine == null) return null;

            string[] gridToks = Split(gridLine);
            if (gridToks.Length < 3) return null;
            if (!int.TryParse(gridToks[0], out int ngx) ||
                !int.TryParse(gridToks[1], out int ngy) ||
                !int.TryParse(gridToks[2], out int ngz)) return null;
            if (ngx <= 0 || ngy <= 0 || ngz <= 0) return null;

            // ── Read volumetric data ───────────────────────────────────────────────
            // CHGCAR stores data with ix varying fastest (column-major / Fortran order):
            //   data[ix, iy, iz] at linear index = ix + NGX*(iy + NGY*iz)
            long totalPoints = (long)ngx * ngy * ngz;
            var data = new float[ngx, ngy, ngz];

            long count = 0;
            string dataLine;
            while (count < totalPoints && (dataLine = reader.ReadLine()) != null)
            {
                string[] vals = Split(dataLine);
                foreach (string val in vals)
                {
                    if (count >= totalPoints) break;
                    if (!TryFloat(val, out double d)) continue;

                    long idx = count;
                    int ix = (int)(idx % ngx);
                    int iy = (int)((idx / ngx) % ngy);
                    int iz = (int)(idx / ((long)ngx * ngy));
                    data[ix, iy, iz] = (float)d;
                    count++;
                }
            }

            // ── Normalise by cell volume (CHGCAR stores ρ × Vcell) ───────────────
            double vcell = CellVolume(va, vb, vc);
            if (vcell < 1e-10) return null;
            float invVcell = (float)(1.0 / vcell);
            for (int ix = 0; ix < ngx; ix++)
            for (int iy = 0; iy < ngy; iy++)
            for (int iz = 0; iz < ngz; iz++)
                data[ix, iy, iz] *= invVcell;

            // ── Build VolumetricGrid ──────────────────────────────────────────────
            // Voxel step = lattice vector / grid dimension
            var grid = new VolumetricGrid
            {
                NX     = ngx,
                NY     = ngy,
                NZ     = ngz,
                Origin = new double[] { 0, 0, 0 },
                Axes   = new double[,]
                {
                    { va[0]/ngx, va[1]/ngx, va[2]/ngx },
                    { vb[0]/ngy, vb[1]/ngy, vb[2]/ngy },
                    { vc[0]/ngz, vc[1]/ngz, vc[2]/ngz },
                },
                Data      = data,
                DataLabel = "Charge Density",
            };

            // ── Build Molecule (1×1×1 unit cell) ─────────────────────────────────
            var mol = new Molecule();
            mol.Atoms = crystal.UnitCellAtoms
                .Select(a => new Atom(a.Element, a.X, a.Y, a.Z))
                .ToList();
            mol.Bonds = BondDetector.Detect(mol.Atoms);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var atom in mol.Atoms)
            {
                if (counts.ContainsKey(atom.Element)) counts[atom.Element]++;
                else counts[atom.Element] = 1;
            }

            var summary = new QuantumSummary
            {
                Software          = SoftwareType.VASP,
                CalcType          = "Charge Density",
                NormalTermination = true,
                AtomCounts        = counts,
            };

            return new ParseResult
            {
                Summary        = summary,
                Molecule       = mol,
                CrystalData    = crystal,
                VolumetricData = grid,
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static double CellVolume(double[] a, double[] b, double[] c)
        {
            double bx = b[1]*c[2] - b[2]*c[1];
            double by = b[2]*c[0] - b[0]*c[2];
            double bz = b[0]*c[1] - b[1]*c[0];
            return Math.Abs(a[0]*bx + a[1]*by + a[2]*bz);
        }

        private static double[] ReadVector(TextReader reader, double scale)
        {
            string line = reader.ReadLine();
            if (line == null) return null;
            string[] parts = Split(line);
            if (parts.Length < 3) return null;
            if (!TryFloat(parts[0], out double x) ||
                !TryFloat(parts[1], out double y) ||
                !TryFloat(parts[2], out double z)) return null;
            return new double[] { x * scale, y * scale, z * scale };
        }

        private static string[] Split(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return Array.Empty<string>();
            return line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool TryFloat(string s, out double val)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        private static string NormalizeElement(string sym)
        {
            if (string.IsNullOrEmpty(sym)) return "X";
            return char.ToUpperInvariant(sym[0]) +
                   (sym.Length > 1 ? sym.Substring(1).ToLowerInvariant() : "");
        }
    }
}
