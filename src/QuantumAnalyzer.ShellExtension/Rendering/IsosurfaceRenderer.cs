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
    /// Renders a molecule + isosurface lobes into a Bitmap using the painter's algorithm.
    /// Positive lobe: semi-transparent green.  Negative lobe: semi-transparent red.
    /// Reuses MoleculeRenderer's internal atom/bond drawing helpers.
    /// </summary>
    public static class IsosurfaceRenderer
    {
        // Lobe colours (ARGB — 130 alpha ≈ 51% opacity)
        private static readonly Color PosLobeColor = Color.FromArgb(130,   0, 200,  80);
        private static readonly Color NegLobeColor = Color.FromArgb(130, 220,  40,  40);

        public static Bitmap Render(
            Molecule molecule,
            List<MarchingCubes.Triangle> posTriangles,
            List<MarchingCubes.Triangle> negTriangles,
            float[,] rotMatrix,
            int width, int height,
            Color background,
            bool lowQuality,
            float zoomFactor = 1.0f)
        {
            var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(background);

                bool hasMolecule  = molecule != null && molecule.Atoms != null && molecule.Atoms.Count > 0;
                bool hasPosTri    = posTriangles != null && posTriangles.Count > 0;
                bool hasNegTri    = negTriangles != null && negTriangles.Count > 0;
                bool hasTriangles = hasPosTri || hasNegTri;

                if (!hasMolecule && !hasTriangles) return bmp;

                // Project atoms via MoleculeProjector (PCA + user rotation)
                ProjectedAtom[] projected = hasMolecule
                    ? MoleculeProjector.ProjectWithMatrix(molecule, rotMatrix)
                    : new ProjectedAtom[0];

                // Compute molecule centroid for applying same transform to triangle vertices
                double cx = 0, cy = 0, cz = 0;
                if (hasMolecule)
                {
                    foreach (var a in molecule.Atoms) { cx += a.X; cy += a.Y; cz += a.Z; }
                    int n = molecule.Atoms.Count;
                    cx /= n; cy /= n; cz /= n;
                }

                float scale, offX, offY;
                if (hasMolecule)
                {
                    scale = MoleculeRenderer.ComputeFixedScale(molecule, width, height) * zoomFactor;
                    offX  = width  / 2f;
                    offY  = height / 2f;
                }
                else
                {
                    // No atoms: estimate scale from triangle extents
                    scale = ComputeScaleFromTriangles(posTriangles, negTriangles, rotMatrix, width, height) * zoomFactor;
                    offX  = width  / 2f;
                    offY  = height / 2f;
                }

                float baseRadius = scale * 0.35f;

                // Build depth range from atom projections
                float minDepth = float.MaxValue, maxDepth = float.MinValue;
                foreach (var pa in projected)
                {
                    if (pa.Depth < minDepth) minDepth = pa.Depth;
                    if (pa.Depth > maxDepth) maxDepth = pa.Depth;
                }
                float depthRange = (maxDepth - minDepth) < 0.01f ? 1f : (maxDepth - minDepth);

                // Build lookup for atoms
                ProjectedAtom[] lookup = hasMolecule ? new ProjectedAtom[molecule.Atoms.Count] : new ProjectedAtom[0];
                foreach (var pa in projected)
                    if (pa.Index < lookup.Length) lookup[pa.Index] = pa;

                // ── Draw list ─────────────────────────────────────────────────────────
                // Entry types:
                //   isAtom=true, isTri=false → atom at idxA
                //   isAtom=false, isTri=false → bond between idxA, idxB
                //   isTri=true → triangle index in combined list

                // Combine positive and negative triangles with a sign tag
                // Using a separate combined list: (depth, isAtom, isTri, idxA, idxB, isPosTri)
                var drawList = new List<DrawEntry>(
                    projected.Length + (molecule?.Bonds?.Count ?? 0) +
                    (posTriangles?.Count ?? 0) + (negTriangles?.Count ?? 0));

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

                // Triangles — skipped during interactive drag (lowQuality) for responsiveness.
                if (!lowQuality)
                {
                    float[,] combinedRot = hasMolecule
                        ? MoleculeProjector.GetCombinedRotation(molecule, rotMatrix)
                        : rotMatrix;

                    if (hasPosTri)
                        ProjectAndAddTriangles(drawList, posTriangles, cx, cy, cz, combinedRot, DrawKind.PosTri);
                    if (hasNegTri)
                        ProjectAndAddTriangles(drawList, negTriangles, cx, cy, cz, combinedRot, DrawKind.NegTri);
                }

                // Sort back-to-front
                drawList.Sort((a, b) => b.Depth.CompareTo(a.Depth));

                // ── Paint ─────────────────────────────────────────────────────────────
                float bondWidth = Math.Max(1f, baseRadius * 0.35f);
                using (var bondPen = new Pen(Color.FromArgb(140, 140, 140), bondWidth))
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
                                if (lowQuality)
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

                // Draw element legend on top
                if (hasMolecule)
                    MoleculeRenderer.DrawLegend(g, molecule, width, height, baseRadius, background);
            }

            return bmp;
        }

        // ──────────────────────────────────────────────────────────────────
        // Triangle projection helpers
        // ──────────────────────────────────────────────────────────────────

        private struct DrawEntry
        {
            public float    Depth;
            public DrawKind Kind;
            public int      IdxA;
            public int      IdxB;
            // Projected 2D vertices for triangles (packed: x0,y0, x1,y1, x2,y2)
            public float[] Screen;
        }

        private enum DrawKind { Atom, Bond, PosTri, NegTri }

        private static void ProjectAndAddTriangles(
            List<DrawEntry> drawList,
            List<MarchingCubes.Triangle> tris,
            double cx, double cy, double cz,
            float[,] combinedRot,
            DrawKind kind)
        {
            foreach (var tri in tris)
            {
                float[] sv0 = ProjectVert(tri.V0, cx, cy, cz, combinedRot);
                float[] sv1 = ProjectVert(tri.V1, cx, cy, cz, combinedRot);
                float[] sv2 = ProjectVert(tri.V2, cx, cy, cz, combinedRot);

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
        /// Applies centring + combined rotation (PCA × userRot) to a world-space vertex,
        /// returning (screenX, screenY, depth).  Matches the transform in
        /// MoleculeProjector.ProjectWithMatrix exactly when combinedRot = GetCombinedRotation().
        /// </summary>
        private static float[] ProjectVert(float[] v, double cx, double cy, double cz, float[,] rot)
        {
            double x = v[0] - cx;
            double y = v[1] - cy;
            double z = v[2] - cz;

            double xR = rot[0, 0]*x + rot[0, 1]*y + rot[0, 2]*z;
            double yR = rot[1, 0]*x + rot[1, 1]*y + rot[1, 2]*z;
            double zR = rot[2, 0]*x + rot[2, 1]*y + rot[2, 2]*z;

            return new float[] { (float)xR, (float)-yR, (float)zR };  // flip Y for screen
        }

        private static void DrawProjectedTriangle(Graphics g, DrawEntry entry, float scale, float offX, float offY, Color color)
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

        private static float ComputeScaleFromTriangles(
            List<MarchingCubes.Triangle> pos,
            List<MarchingCubes.Triangle> neg,
            float[,] rotMatrix,
            int width, int height)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            void CheckVert(float[] v)
            {
                float sx = rotMatrix[0, 0]*v[0] + rotMatrix[0, 1]*v[1] + rotMatrix[0, 2]*v[2];
                float sy = -(rotMatrix[1, 0]*v[0] + rotMatrix[1, 1]*v[1] + rotMatrix[1, 2]*v[2]);
                if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
                if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
            }

            if (pos != null) foreach (var t in pos) { CheckVert(t.V0); CheckVert(t.V1); CheckVert(t.V2); }
            if (neg != null) foreach (var t in neg) { CheckVert(t.V0); CheckVert(t.V1); CheckVert(t.V2); }

            float rangeX = maxX - minX; if (rangeX < 0.1f) rangeX = 1f;
            float rangeY = maxY - minY; if (rangeY < 0.1f) rangeY = 1f;
            float padding = 0.12f;
            float usableW = width  * (1f - 2f * padding);
            float usableH = height * (1f - 2f * padding);
            return Math.Min(usableW / rangeX, usableH / rangeY);
        }
    }
}
