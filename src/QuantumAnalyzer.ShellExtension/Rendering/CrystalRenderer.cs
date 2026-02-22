using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;
using static QuantumAnalyzer.ShellExtension.Rendering.MoleculeProjector;

namespace QuantumAnalyzer.ShellExtension.Rendering
{
    /// <summary>
    /// Renders a crystal structure: atoms, bonds, unit cell wireframe, and lattice vector arrows.
    /// Uses the same atom/bond drawing helpers as MoleculeRenderer.
    /// </summary>
    public static class CrystalRenderer
    {
        // Arrow colours: A = red, B = green, C = blue
        private static readonly Color ArrowColorA = Color.FromArgb(220, 50,  50);
        private static readonly Color ArrowColorB = Color.FromArgb(50,  180, 50);
        private static readonly Color ArrowColorC = Color.FromArgb(50,  80,  220);

        /// <summary>
        /// Renders a crystal structure into a bitmap.
        /// </summary>
        /// <param name="molecule">Supercell-expanded atoms + bonds.</param>
        /// <param name="crystal">Original unit cell data (for wireframe + vector arrows).</param>
        /// <param name="supercell">Supercell repetitions {nx, ny, nz}.</param>
        /// <param name="rotMatrix">Arcball rotation matrix (user rotation).</param>
        /// <param name="width">Bitmap width in pixels.</param>
        /// <param name="height">Bitmap height in pixels.</param>
        /// <param name="background">Background colour.</param>
        /// <param name="lowQuality">Use flat atoms during drag (fast).</param>
        /// <param name="zoomFactor">Zoom multiplier.</param>
        /// <param name="showUnitCell">Draw supercell wireframe.</param>
        /// <param name="showVectors">Draw lattice vector arrows.</param>
        public static Bitmap Render(
            Molecule   molecule,
            LatticeCell crystal,
            int[]      supercell,
            float[,]   rotMatrix,
            int        width, int height,
            Color      background,
            bool       lowQuality,
            float      zoomFactor,
            bool       showUnitCell,
            bool       showVectors)
        {
            var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(background);

                bool hasMolecule = molecule != null && molecule.Atoms != null && molecule.Atoms.Count > 0;
                if (!hasMolecule && crystal == null) return bmp;

                // Project atoms using PCA + user rotation
                ProjectedAtom[] projected = hasMolecule
                    ? MoleculeProjector.ProjectWithMatrix(molecule, rotMatrix)
                    : new ProjectedAtom[0];

                // Scale and layout (centre at bitmap centre, same as IsosurfaceRenderer)
                float scale = hasMolecule
                    ? MoleculeRenderer.ComputeFixedScale(molecule, width, height) * zoomFactor
                    : zoomFactor;
                float offX = width  / 2f;
                float offY = height / 2f;

                float baseRadius = scale * 0.35f;

                // Compute centroid of expanded molecule (world-space)
                double cx = 0, cy = 0, cz = 0;
                if (hasMolecule)
                {
                    foreach (var a in molecule.Atoms) { cx += a.X; cy += a.Y; cz += a.Z; }
                    int n = molecule.Atoms.Count;
                    cx /= n; cy /= n; cz /= n;
                }

                // Combined rotation: PCA(molecule) × userRot — for wireframe/arrow projection
                float[,] combinedRot = hasMolecule
                    ? MoleculeProjector.GetCombinedRotation(molecule, rotMatrix)
                    : rotMatrix;

                // Adaptive text/wireframe colour based on background luminance
                float bgLum = 0.299f * background.R + 0.587f * background.G + 0.114f * background.B;
                Color wireColor = bgLum > 128f ? Color.FromArgb(80, 80, 100) : Color.FromArgb(180, 180, 200);

                // ── 1. Draw unit cell wireframe ────────────────────────────────────
                if (showUnitCell && crystal != null)
                {
                    int nx = supercell[0], ny = supercell[1], nz = supercell[2];
                    double[] A = Scale(crystal.VectorA, nx);
                    double[] B = Scale(crystal.VectorB, ny);
                    double[] C = Scale(crystal.VectorC, nz);

                    // 8 corners of the supercell parallelepiped
                    double[][] corners = new double[8][];
                    corners[0] = new double[] { 0,              0,              0              };
                    corners[1] = new double[] { A[0],           A[1],           A[2]           };
                    corners[2] = new double[] { B[0],           B[1],           B[2]           };
                    corners[3] = new double[] { C[0],           C[1],           C[2]           };
                    corners[4] = new double[] { A[0]+B[0],      A[1]+B[1],      A[2]+B[2]      };
                    corners[5] = new double[] { A[0]+C[0],      A[1]+C[1],      A[2]+C[2]      };
                    corners[6] = new double[] { B[0]+C[0],      B[1]+C[1],      B[2]+C[2]      };
                    corners[7] = new double[] { A[0]+B[0]+C[0], A[1]+B[1]+C[1], A[2]+B[2]+C[2] };

                    PointF[] screenCorners = new PointF[8];
                    for (int i = 0; i < 8; i++)
                    {
                        var (sx, sy, _) = ProjectPoint(
                            corners[i][0], corners[i][1], corners[i][2],
                            cx, cy, cz, combinedRot, scale, width, height);
                        screenCorners[i] = new PointF(sx, sy);
                    }

                    // 12 edges of the parallelepiped
                    int[][] edges = new int[][]
                    {
                        new[]{0,1}, new[]{0,2}, new[]{0,3},
                        new[]{1,4}, new[]{1,5},
                        new[]{2,4}, new[]{2,6},
                        new[]{3,5}, new[]{3,6},
                        new[]{4,7}, new[]{5,7}, new[]{6,7},
                    };

                    using (var wirePen = new Pen(wireColor, 1.2f))
                    {
                        wirePen.DashStyle = DashStyle.Dot;
                        foreach (var edge in edges)
                        {
                            g.DrawLine(wirePen, screenCorners[edge[0]], screenCorners[edge[1]]);
                        }
                    }
                }

                // ── 2. Draw lattice vector arrows (corner indicator, bottom-left) ────
                if (showVectors && crystal != null)
                    DrawCornerVectorIndicator(g, crystal, combinedRot, background,
                        molecule, width, height, baseRadius);

                // ── 3. Draw atoms + bonds (depth-sorted) ──────────────────────────
                if (hasMolecule)
                {
                    // Build depth range for fog
                    float minDepth = float.MaxValue, maxDepth = float.MinValue;
                    foreach (var pa in projected)
                    {
                        if (pa.Depth < minDepth) minDepth = pa.Depth;
                        if (pa.Depth > maxDepth) maxDepth = pa.Depth;
                    }
                    float depthRange = (maxDepth - minDepth) < 0.01f ? 1f : (maxDepth - minDepth);

                    // Build atom lookup
                    var lookup = new ProjectedAtom[molecule.Atoms.Count];
                    foreach (var pa in projected) lookup[pa.Index] = pa;

                    // Depth-sorted draw list
                    var drawList = new List<(float Depth, bool IsAtom, int IdxA, int IdxB)>(
                        projected.Length + (molecule.Bonds?.Count ?? 0));

                    foreach (var pa in projected)
                        drawList.Add((pa.Depth, true, pa.Index, -1));

                    if (molecule.Bonds != null)
                    {
                        foreach (var (a, b) in molecule.Bonds)
                        {
                            var pa = lookup[a];
                            var pb = lookup[b];
                            if (pa == null || pb == null) continue;
                            drawList.Add(((pa.Depth + pb.Depth) / 2f, false, a, b));
                        }
                    }

                    drawList.Sort((x, y) => y.Depth.CompareTo(x.Depth));

                    float bondWidth = Math.Max(1f, baseRadius * 0.35f);
                    using (var bondPen = new Pen(Color.FromArgb(180, 180, 180), bondWidth))
                    {
                        bondPen.StartCap = LineCap.Round;
                        bondPen.EndCap   = LineCap.Round;

                        foreach (var (depth, isAtom, idxA, idxB) in drawList)
                        {
                            float fogFactor = (depth - minDepth) / depthRange;
                            if (isAtom)
                            {
                                var pa   = lookup[idxA];
                                var atom = molecule.Atoms[idxA];
                                float sx = pa.X * scale + offX;
                                float sy = pa.Y * scale + offY;
                                float r  = baseRadius * (float)ElementData.GetCovalentRadius(atom.Element) / 0.77f;
                                r = Math.Max(r, baseRadius * 0.4f);
                                r = Math.Min(r, baseRadius * 2.5f);
                                Color cpk = ElementData.GetCpkColor(atom.Element);
                                if (lowQuality)
                                    MoleculeRenderer.DrawAtomFlat(g, sx, sy, r, cpk, fogFactor, background);
                                else
                                    MoleculeRenderer.DrawAtomSphere(g, sx, sy, r, cpk, fogFactor, background);
                            }
                            else
                            {
                                var pa = lookup[idxA];
                                var pb = lookup[idxB];
                                float ax = pa.X * scale + offX;
                                float ay = pa.Y * scale + offY;
                                float bx = pb.X * scale + offX;
                                float by = pb.Y * scale + offY;
                                float dx  = bx - ax, dy2 = by - ay;
                                float len = (float)Math.Sqrt(dx*dx + dy2*dy2);
                                if (len > 0.001f)
                                {
                                    float nx2 = dx / len, ny2 = dy2 / len;
                                    var atomA = molecule.Atoms[idxA];
                                    var atomB = molecule.Atoms[idxB];
                                    float rA = Math.Max(baseRadius * 0.4f, Math.Min(baseRadius * (float)ElementData.GetCovalentRadius(atomA.Element) / 0.77f, baseRadius * 2.5f));
                                    float rB = Math.Max(baseRadius * 0.4f, Math.Min(baseRadius * (float)ElementData.GetCovalentRadius(atomB.Element) / 0.77f, baseRadius * 2.5f));
                                    if (len > rA + rB)
                                    {
                                        bondPen.Color = MoleculeRenderer.ApplyFog(Color.FromArgb(180, 180, 180), fogFactor, background);
                                        g.DrawLine(bondPen,
                                            ax + nx2 * rA, ay + ny2 * rA,
                                            bx - nx2 * rB, by - ny2 * rB);
                                    }
                                }
                            }
                        }
                    }
                }

                // ── 4. Draw a/b/c text overlay (top-right) ────────────────────────
                // DrawLatticeLengths(g, crystal, width, wireColor); // moved to top info panel

                // ── 5. Draw element legend ─────────────────────────────────────────
                if (hasMolecule)
                    MoleculeRenderer.DrawLegend(g, molecule, width, height, baseRadius, background);
            }

            return bmp;
        }

        // ──────────────────────────────────────────────────────────────────
        // Projection helper
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Projects a world-space point through the combined rotation matrix,
        /// returning its screen position (sx, sy) and depth.
        /// </summary>
        private static (float sx, float sy, float depth) ProjectPoint(
            double wx, double wy, double wz,
            double centX, double centY, double centZ,
            float[,] rot, float scale, int w, int h)
        {
            float px = (float)(wx - centX);
            float py = (float)(wy - centY);
            float pz = (float)(wz - centZ);
            float rx = rot[0,0]*px + rot[0,1]*py + rot[0,2]*pz;
            float ry = rot[1,0]*px + rot[1,1]*py + rot[1,2]*pz;
            float rz = rot[2,0]*px + rot[2,1]*py + rot[2,2]*pz;
            return (rx * scale + w / 2f, -ry * scale + h / 2f, rz);
        }

        // ──────────────────────────────────────────────────────────────────
        // Corner vector indicator (top-left, fixed screen-space size)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draws a compact orientation indicator in the bottom-left corner showing
        /// the a, b, c lattice vector directions as they rotate with the structure.
        /// Positioned just above the element legend so they never overlap.
        /// Uses fixed screen-space length — independent of molecule scale or zoom.
        /// </summary>
        private static void DrawCornerVectorIndicator(
            Graphics g, LatticeCell crystal, float[,] combinedRot, Color background,
            Molecule molecule, int width, int height, float baseRadius)
        {
            const float OriginX  = 55f;
            const float ArrowLen = 40f;
            const float LabelPad = 12f;   // extra space for labels beyond arrow tips
            const float Gap      = 10f;   // gap between indicator bottom and legend top

            // Compute how many legend rows will be drawn so we can sit just above them
            int legendRows = ComputeLegendRows(molecule, g, width, baseRadius);
            float legendTopY = height - legendRows * 25f;

            // Origin Y: ensure the lowest point of the indicator (origin + ArrowLen + LabelPad)
            // stays above the legend top by Gap pixels.
            float originY = legendTopY - Gap - ArrowLen - LabelPad;
            // Clamp so we never go above the top of the bitmap
            originY = Math.Max(originY, ArrowLen + LabelPad + 5f);

            Color[] colors  = { ArrowColorA, ArrowColorB, ArrowColorC };
            string[] labels = { "a", "b", "c" };
            double[][] vecs = { crystal.VectorA, crystal.VectorB, crystal.VectorC };

            // Small dot at the common origin
            float bgLum = 0.299f * background.R + 0.587f * background.G + 0.114f * background.B;
            Color dotColor = bgLum > 128f ? Color.FromArgb(80, 80, 100) : Color.FromArgb(200, 200, 220);
            using (var dotBrush = new SolidBrush(dotColor))
                g.FillEllipse(dotBrush, OriginX - 3f, originY - 3f, 6f, 6f);

            for (int i = 0; i < 3; i++)
            {
                double[] v = vecs[i];
                double vLen = Math.Sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2]);
                if (vLen < 1e-10) continue;

                // Normalised direction in world space
                double vx = v[0] / vLen, vy = v[1] / vLen, vz = v[2] / vLen;

                // Project direction through combined rotation into screen space.
                // Row 0 → screen X, row 1 → screen Y (negated because Y increases downward).
                float sx = (float)(combinedRot[0,0]*vx + combinedRot[0,1]*vy + combinedRot[0,2]*vz);
                float sy = -(float)(combinedRot[1,0]*vx + combinedRot[1,1]*vy + combinedRot[1,2]*vz);

                float sLen = (float)Math.Sqrt(sx*sx + sy*sy);
                if (sLen < 1e-6f) continue;
                sx /= sLen; sy /= sLen;

                float tx = OriginX + sx * ArrowLen;
                float ty = originY + sy * ArrowLen;

                // Arrow shaft
                using (var pen = new Pen(colors[i], 2.0f))
                    g.DrawLine(pen, OriginX, originY, tx, ty);

                // Arrowhead
                const float HeadLen  = 8f;
                const float HeadHalf = 3.5f;
                float px = -sy, py = sx;   // perpendicular in screen space
                var tip = new PointF(tx, ty);
                var b1  = new PointF(tx - sx*HeadLen + px*HeadHalf, ty - sy*HeadLen + py*HeadHalf);
                var b2  = new PointF(tx - sx*HeadLen - px*HeadHalf, ty - sy*HeadLen - py*HeadHalf);
                using (var brush = new SolidBrush(colors[i]))
                    g.FillPolygon(brush, new[] { tip, b1, b2 });

                // Label just beyond the tip
                using (var font  = new Font("Segoe UI", 8f, FontStyle.Bold))
                using (var brush = new SolidBrush(colors[i]))
                {
                    SizeF sz = g.MeasureString(labels[i], font);
                    g.DrawString(labels[i], font, brush,
                        tx + sx * 5f - sz.Width  / 2f,
                        ty + sy * 5f - sz.Height / 2f);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Lattice lengths overlay
        // ──────────────────────────────────────────────────────────────────

        private static void DrawLatticeLengths(Graphics g, LatticeCell crystal, int width, Color textColor)
        {
            using (var font = new Font("Segoe UI", 8f, FontStyle.Regular))
            using (var brush = new SolidBrush(textColor))
            {
                string[] lines =
                {
                    $"a = {crystal.LengthA:0.000} \u212B",
                    $"b = {crystal.LengthB:0.000} \u212B",
                    $"c = {crystal.LengthC:0.000} \u212B",
                };

                float lineH = font.GetHeight(g) + 1f;
                float y = 6f;
                foreach (string txt in lines)
                {
                    SizeF sz = g.MeasureString(txt, font);
                    g.DrawString(txt, font, brush, width - sz.Width - 6f, y);
                    y += lineH;
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors the row-wrapping logic of MoleculeRenderer.DrawLegend so we can
        /// compute the legend height before drawing and position the vector indicator
        /// just above it.
        /// </summary>
        private static int ComputeLegendRows(Molecule molecule, Graphics g, int width, float baseRadius)
        {
            if (molecule?.Atoms == null || molecule.Atoms.Count == 0) return 0;

            var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var elements = new List<string>();
            foreach (var atom in molecule.Atoms)
                if (seen.Add(atom.Element)) elements.Add(atom.Element);

            float legendRadius = Math.Max(7f, Math.Min(11f, baseRadius * 0.6f));
            float itemSpacing  = 22f;
            float maxWidth     = width * 0.9f;

            int   rows     = 1;
            float rowWidth = 0f;
            bool  first    = true;

            using (var font = new Font("Segoe UI", 9f))
            {
                foreach (var sym in elements)
                {
                    string name      = Chemistry.ElementData.GetElementName(sym);
                    float  itemWidth = legendRadius * 2 + 6f + g.MeasureString(name, font).Width;

                    if (!first && rowWidth + itemSpacing + itemWidth > maxWidth)
                    {
                        rows++;
                        rowWidth = 0f;
                        first    = true;
                    }
                    rowWidth += (first ? 0f : itemSpacing) + itemWidth;
                    first = false;
                }
            }
            return rows;
        }

        private static double[] Scale(double[] v, int factor)
        {
            return new double[] { v[0] * factor, v[1] * factor, v[2] * factor };
        }
    }
}
