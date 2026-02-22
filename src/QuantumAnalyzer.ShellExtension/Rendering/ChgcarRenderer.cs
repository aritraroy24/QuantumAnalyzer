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
    /// Renders a CHGCAR file: crystal structure (wireframe, atoms, bonds, lattice vectors)
    /// combined with isosurface lobes for the charge density, all depth-sorted.
    ///
    /// Merges the draw pipelines of CrystalRenderer and IsosurfaceRenderer.
    /// </summary>
    public static class ChgcarRenderer
    {
        // Arrow colours: A = red, B = green, C = blue
        private static readonly Color ArrowColorA = Color.FromArgb(220, 50,  50);
        private static readonly Color ArrowColorB = Color.FromArgb(50,  180, 50);
        private static readonly Color ArrowColorC = Color.FromArgb(50,  80,  220);

        // Lobe colours
        private static readonly Color PosLobeColor = Color.FromArgb(130,   0, 200,  80);
        private static readonly Color NegLobeColor = Color.FromArgb(130, 220,  40,  40);

        public static Bitmap Render(
            Molecule                      molecule,
            LatticeCell                   crystal,
            int[]                         supercell,
            List<MarchingCubes.Triangle>  posTriangles,
            List<MarchingCubes.Triangle>  negTriangles,
            float[,]                      rotMatrix,
            int width, int height,
            Color background,
            bool lowQuality,
            bool flatAtoms,
            float zoomFactor,
            bool showUnitCell,
            bool showVectors,
            bool showPos,
            bool showNeg)
        {
            var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(background);

                bool hasMolecule  = molecule != null && molecule.Atoms != null && molecule.Atoms.Count > 0;
                bool hasPosTri    = showPos && posTriangles != null && posTriangles.Count > 0;
                bool hasNegTri    = showNeg && negTriangles != null && negTriangles.Count > 0;
                bool hasTriangles = hasPosTri || hasNegTri;

                if (!hasMolecule && crystal == null && !hasTriangles) return bmp;

                // Project atoms via PCA + user rotation
                ProjectedAtom[] projected = hasMolecule
                    ? MoleculeProjector.ProjectWithMatrix(molecule, rotMatrix)
                    : new ProjectedAtom[0];

                // Geometric centroid of all expanded atoms (world-space)
                double cx = 0, cy = 0, cz = 0;
                if (hasMolecule)
                {
                    foreach (var a in molecule.Atoms) { cx += a.X; cy += a.Y; cz += a.Z; }
                    int n = molecule.Atoms.Count;
                    cx /= n; cy /= n; cz /= n;
                }

                // Combined rotation (PCA × userRot) for wireframe, arrows, triangles
                float[,] combinedRot = hasMolecule
                    ? MoleculeProjector.GetCombinedRotation(molecule, rotMatrix)
                    : rotMatrix;

                // Atom-based scale
                float atomScale = hasMolecule
                    ? MoleculeRenderer.ComputeFixedScale(molecule, width, height)
                    : 20f;

                // Cell-aware scale: use 3D distances from centroid to each wireframe corner
                // (rotation-independent — avoids zooming artefact on every rotation frame).
                float scale;
                if (crystal != null)
                {
                    double[] cA = ScaleVec(crystal.VectorA, supercell[0]);
                    double[] cB = ScaleVec(crystal.VectorB, supercell[1]);
                    double[] cC = ScaleVec(crystal.VectorC, supercell[2]);
                    double[][] cellCorners =
                    {
                        new double[]{ 0,              0,              0              },
                        new double[]{ cA[0],          cA[1],          cA[2]          },
                        new double[]{ cB[0],          cB[1],          cB[2]          },
                        new double[]{ cC[0],          cC[1],          cC[2]          },
                        new double[]{ cA[0]+cB[0],    cA[1]+cB[1],    cA[2]+cB[2]   },
                        new double[]{ cA[0]+cC[0],    cA[1]+cC[1],    cA[2]+cC[2]   },
                        new double[]{ cB[0]+cC[0],    cB[1]+cC[1],    cB[2]+cC[2]   },
                        new double[]{ cA[0]+cB[0]+cC[0], cA[1]+cB[1]+cC[1], cA[2]+cB[2]+cC[2] },
                    };
                    float maxDist = 0f;
                    foreach (var corner in cellCorners)
                    {
                        double dx = corner[0] - cx;
                        double dy = corner[1] - cy;
                        double dz = corner[2] - cz;
                        float dist = (float)Math.Sqrt(dx*dx + dy*dy + dz*dz);
                        if (dist > maxDist) maxDist = dist;
                    }
                    const float Margin = 0.88f;
                    float cellScale = maxDist > 0.001f
                        ? Math.Min(width, height) / 2f * Margin / maxDist
                        : atomScale;
                    scale = Math.Min(atomScale, cellScale) * zoomFactor;
                }
                else
                {
                    scale = atomScale * zoomFactor;
                }

                float offX = width  / 2f;
                float offY = height / 2f;

                float baseRadius = scale * 0.35f;

                // Adaptive text/wire colour
                float bgLum = 0.299f * background.R + 0.587f * background.G + 0.114f * background.B;
                Color wireColor = bgLum > 128f ? Color.FromArgb(80, 80, 100) : Color.FromArgb(180, 180, 200);

                // ── 1. Draw unit cell wireframe (back layer — dotted, behind atoms/isosurface) ──
                if (showUnitCell && crystal != null)
                {
                    int nx = supercell[0], ny = supercell[1], nz = supercell[2];
                    double[] A = ScaleVec(crystal.VectorA, nx);
                    double[] B = ScaleVec(crystal.VectorB, ny);
                    double[] C = ScaleVec(crystal.VectorC, nz);

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

                    int[][] edges =
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
                            g.DrawLine(wirePen, screenCorners[edge[0]], screenCorners[edge[1]]);
                    }
                }

                // ── 2. Build merged depth-sorted draw list ─────────────────────────
                // Build depth range from atoms for fog
                float minDepth = float.MaxValue, maxDepth = float.MinValue;
                foreach (var pa in projected)
                {
                    if (pa.Depth < minDepth) minDepth = pa.Depth;
                    if (pa.Depth > maxDepth) maxDepth = pa.Depth;
                }
                float depthRange = (maxDepth - minDepth) < 0.01f ? 1f : (maxDepth - minDepth);

                // Build atom lookup
                ProjectedAtom[] lookup = hasMolecule
                    ? new ProjectedAtom[molecule.Atoms.Count]
                    : new ProjectedAtom[0];
                foreach (var pa in projected)
                    if (pa.Index < lookup.Length) lookup[pa.Index] = pa;

                // Draw list entries
                var drawList = new List<DrawEntry>(
                    projected.Length +
                    (molecule?.Bonds?.Count ?? 0) +
                    (posTriangles?.Count ?? 0) +
                    (negTriangles?.Count ?? 0));

                // Atoms
                foreach (var pa in projected)
                    drawList.Add(new DrawEntry { Depth = pa.Depth, Kind = DrawKind.Atom, IdxA = pa.Index });

                // Bonds
                if (hasMolecule && molecule.Bonds != null)
                {
                    foreach (var bond in molecule.Bonds)
                    {
                        var pa = lookup[bond.A];
                        var pb = lookup[bond.B];
                        if (pa == null || pb == null) continue;
                        drawList.Add(new DrawEntry
                        {
                            Depth = (pa.Depth + pb.Depth) / 2f,
                            Kind  = DrawKind.Bond,
                            IdxA  = bond.A,
                            IdxB  = bond.B,
                        });
                    }
                }

                // Isosurface triangles — skipped during drag for responsiveness
                if (!lowQuality)
                {
                    if (hasPosTri)
                        ProjectAndAddTriangles(drawList, posTriangles, cx, cy, cz, combinedRot, DrawKind.PosTri);
                    if (hasNegTri)
                        ProjectAndAddTriangles(drawList, negTriangles, cx, cy, cz, combinedRot, DrawKind.NegTri);
                }

                // Sort back-to-front
                drawList.Sort((a, b) => b.Depth.CompareTo(a.Depth));

                // ── 3. Paint depth-sorted list ────────────────────────────────────
                float bondWidth = Math.Max(1f, baseRadius * 0.35f);
                using (var bondPen = new Pen(Color.FromArgb(180, 180, 180), bondWidth))
                {
                    bondPen.StartCap = LineCap.Round;
                    bondPen.EndCap   = LineCap.Round;

                    foreach (var entry in drawList)
                    {
                        switch (entry.Kind)
                        {
                            case DrawKind.Atom:
                            {
                                var pa   = lookup[entry.IdxA];
                                var atom = molecule.Atoms[entry.IdxA];
                                float fogF = (entry.Depth - minDepth) / depthRange;
                                float sx = pa.X * scale + offX;
                                float sy = pa.Y * scale + offY;
                                float r  = baseRadius * (float)ElementData.GetCovalentRadius(atom.Element) / 0.77f;
                                r = Math.Max(r, baseRadius * 0.4f);
                                r = Math.Min(r, baseRadius * 2.5f);
                                Color cpk = ElementData.GetCpkColor(atom.Element);
                                if (lowQuality || flatAtoms)
                                    MoleculeRenderer.DrawAtomFlat(g, sx, sy, r, cpk, fogF, background);
                                else
                                    MoleculeRenderer.DrawAtomSphere(g, sx, sy, r, cpk, fogF, background);
                                break;
                            }
                            case DrawKind.Bond:
                            {
                                var pa = lookup[entry.IdxA];
                                var pb = lookup[entry.IdxB];
                                float ax = pa.X * scale + offX;
                                float ay = pa.Y * scale + offY;
                                float bx = pb.X * scale + offX;
                                float by = pb.Y * scale + offY;
                                float dx  = bx - ax, dy = by - ay;
                                float len = (float)Math.Sqrt(dx*dx + dy*dy);
                                if (len > 0.001f)
                                {
                                    float nx2 = dx / len, ny2 = dy / len;
                                    var atomA = molecule.Atoms[entry.IdxA];
                                    var atomB = molecule.Atoms[entry.IdxB];
                                    float rA = Math.Max(baseRadius * 0.4f, Math.Min(baseRadius * (float)ElementData.GetCovalentRadius(atomA.Element) / 0.77f, baseRadius * 2.5f));
                                    float rB = Math.Max(baseRadius * 0.4f, Math.Min(baseRadius * (float)ElementData.GetCovalentRadius(atomB.Element) / 0.77f, baseRadius * 2.5f));
                                    if (len > rA + rB)
                                    {
                                        float fogF = (entry.Depth - minDepth) / depthRange;
                                        bondPen.Color = MoleculeRenderer.ApplyFog(Color.FromArgb(180, 180, 180), fogF, background);
                                        g.DrawLine(bondPen,
                                            ax + nx2 * rA, ay + ny2 * rA,
                                            bx - nx2 * rB, by - ny2 * rB);
                                    }
                                }
                                break;
                            }
                            case DrawKind.PosTri:
                            case DrawKind.NegTri:
                            {
                                Color lobeColor = entry.Kind == DrawKind.PosTri ? PosLobeColor : NegLobeColor;
                                DrawProjectedTriangle(g, entry, scale, offX, offY, lobeColor);
                                break;
                            }
                        }
                    }
                }

                // ── 4. Overlays ────────────────────────────────────────────────────
                if (showVectors && crystal != null)
                    DrawCornerVectorIndicator(g, crystal, combinedRot, background, molecule, width, height, baseRadius);

                // DrawLatticeLengths(g, crystal, width, wireColor); // moved to top info panel

                if (hasMolecule)
                    MoleculeRenderer.DrawLegend(g, molecule, width, height, baseRadius, background);
            }

            return bmp;
        }

        // ── Triangle helpers ───────────────────────────────────────────────────────

        private struct DrawEntry
        {
            public float    Depth;
            public DrawKind Kind;
            public int      IdxA;
            public int      IdxB;
            // Projected 2D vertices for triangles (pre-scale; packed x0,y0, x1,y1, x2,y2)
            public float[] Screen;
        }

        private enum DrawKind { Atom, Bond, PosTri, NegTri }

        private static void ProjectAndAddTriangles(
            List<DrawEntry>              drawList,
            List<MarchingCubes.Triangle> tris,
            double cx, double cy, double cz,
            float[,] combinedRot,
            DrawKind kind)
        {
            foreach (var tri in tris)
            {
                float[] sv0 = ProjectTriVert(tri.V0, cx, cy, cz, combinedRot);
                float[] sv1 = ProjectTriVert(tri.V1, cx, cy, cz, combinedRot);
                float[] sv2 = ProjectTriVert(tri.V2, cx, cy, cz, combinedRot);

                float depth = (sv0[2] + sv1[2] + sv2[2]) / 3f;
                drawList.Add(new DrawEntry
                {
                    Depth  = depth,
                    Kind   = kind,
                    Screen = new float[] { sv0[0], sv0[1], sv1[0], sv1[1], sv2[0], sv2[1] },
                });
            }
        }

        /// <summary>
        /// Centres on molecule centroid, applies combined rotation, returns
        /// (normalisedScreenX, normalisedScreenY, depth).  Multiply X/Y by scale and
        /// add offX/offY to obtain pixel positions.
        /// </summary>
        private static float[] ProjectTriVert(float[] v, double cx, double cy, double cz, float[,] rot)
        {
            double x = v[0] - cx;
            double y = v[1] - cy;
            double z = v[2] - cz;

            double xR = rot[0,0]*x + rot[0,1]*y + rot[0,2]*z;
            double yR = rot[1,0]*x + rot[1,1]*y + rot[1,2]*z;
            double zR = rot[2,0]*x + rot[2,1]*y + rot[2,2]*z;

            return new float[] { (float)xR, (float)-yR, (float)zR };  // flip Y for screen
        }

        private static void DrawProjectedTriangle(
            Graphics g, DrawEntry entry, float scale, float offX, float offY, Color color)
        {
            float[] s = entry.Screen;
            var pts = new PointF[]
            {
                new PointF(s[0] * scale + offX, s[1] * scale + offY),
                new PointF(s[2] * scale + offX, s[3] * scale + offY),
                new PointF(s[4] * scale + offX, s[5] * scale + offY),
            };
            using (var brush = new SolidBrush(color))
                g.FillPolygon(brush, pts);
        }

        // ── Projection helpers ─────────────────────────────────────────────────────

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

        private static double[] ScaleVec(double[] v, int factor)
            => new double[] { v[0] * factor, v[1] * factor, v[2] * factor };

        // ── Lattice vector corner indicator ───────────────────────────────────────

        private static void DrawCornerVectorIndicator(
            Graphics g, LatticeCell crystal, float[,] combinedRot, Color background,
            Molecule molecule, int width, int height, float baseRadius)
        {
            const float OriginX  = 55f;
            const float ArrowLen = 40f;
            const float LabelPad = 12f;
            const float Gap      = 10f;

            int legendRows = ComputeLegendRows(molecule, g, width, baseRadius);
            float legendTopY = height - legendRows * 25f;
            float originY = legendTopY - Gap - ArrowLen - LabelPad;
            originY = Math.Max(originY, ArrowLen + LabelPad + 5f);

            Color[] colors  = { ArrowColorA, ArrowColorB, ArrowColorC };
            string[] labels = { "a", "b", "c" };
            double[][] vecs = { crystal.VectorA, crystal.VectorB, crystal.VectorC };

            float bgLum = 0.299f * background.R + 0.587f * background.G + 0.114f * background.B;
            Color dotColor = bgLum > 128f ? Color.FromArgb(80, 80, 100) : Color.FromArgb(200, 200, 220);
            using (var dotBrush = new SolidBrush(dotColor))
                g.FillEllipse(dotBrush, OriginX - 3f, originY - 3f, 6f, 6f);

            for (int i = 0; i < 3; i++)
            {
                double[] v = vecs[i];
                double vLen = Math.Sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2]);
                if (vLen < 1e-10) continue;

                double vx = v[0] / vLen, vy = v[1] / vLen, vz = v[2] / vLen;

                float sx = (float)(combinedRot[0,0]*vx + combinedRot[0,1]*vy + combinedRot[0,2]*vz);
                float sy = -(float)(combinedRot[1,0]*vx + combinedRot[1,1]*vy + combinedRot[1,2]*vz);

                float sLen = (float)Math.Sqrt(sx*sx + sy*sy);
                if (sLen < 1e-6f) continue;
                sx /= sLen; sy /= sLen;

                float tx = OriginX + sx * ArrowLen;
                float ty = originY + sy * ArrowLen;

                using (var pen = new Pen(colors[i], 2.0f))
                    g.DrawLine(pen, OriginX, originY, tx, ty);

                const float HeadLen  = 8f;
                const float HeadHalf = 3.5f;
                float px = -sy, py = sx;
                var tip = new PointF(tx, ty);
                var b1  = new PointF(tx - sx*HeadLen + px*HeadHalf, ty - sy*HeadLen + py*HeadHalf);
                var b2  = new PointF(tx - sx*HeadLen - px*HeadHalf, ty - sy*HeadLen - py*HeadHalf);
                using (var brush = new SolidBrush(colors[i]))
                    g.FillPolygon(brush, new[] { tip, b1, b2 });

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

        private static void DrawLatticeLengths(Graphics g, LatticeCell crystal, int width, Color textColor)
        {
            using (var font  = new Font("Segoe UI", 8f, FontStyle.Regular))
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
                    string name      = ElementData.GetElementName(sym);
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
    }
}
