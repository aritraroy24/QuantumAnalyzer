using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;
using static QuantumAnalyzer.ShellExtension.Rendering.MoleculeProjector;

namespace QuantumAnalyzer.ShellExtension.Rendering
{
    public static class MoleculeRenderer
    {
        // Background colour for all renders
        private static readonly Color Background = Color.FromArgb(18, 18, 30);

        // ──────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────

        /// <summary>Render at best PCA angle — used for the static thumbnail.</summary>
        public static Bitmap RenderBestAngle(Molecule molecule, int width, int height)
        {
            var projected = MoleculeProjector.ProjectBestAngle(molecule);
            return Render(molecule, projected, width, height);
        }

        /// <summary>Render with a given Y-rotation — used by the animated preview.</summary>
        /// <param name="fixedScale">When provided, bypasses per-frame layout and uses this constant scale.</param>
        public static Bitmap RenderRotated(Molecule molecule, float angleYDeg, int width, int height, float? fixedScale = null)
        {
            var projected = MoleculeProjector.ProjectWithRotation(molecule, angleYDeg);
            return Render(molecule, projected, width, height, fixedScale);
        }

        /// <summary>
        /// Render with an explicit 3×3 rotation matrix — used by the arcball interactive preview.
        /// </summary>
        public static Bitmap RenderWithMatrix(Molecule molecule, float[,] rotMatrix, int width, int height, float? fixedScale = null)
        {
            var projected = MoleculeProjector.ProjectWithMatrix(molecule, rotMatrix);
            return Render(molecule, projected, width, height, fixedScale);
        }

        /// <summary>
        /// Compute a rotation-invariant scale from the 3D bounding sphere radius.
        /// Call once per molecule (or on resize) and pass to every RenderRotated() call
        /// to prevent zoom oscillation during rotation.
        /// </summary>
        public static float ComputeFixedScale(Molecule molecule, int width, int height)
        {
            if (molecule == null || molecule.Atoms.Count == 0) return 1f;

            double cx = 0, cy = 0, cz = 0;
            foreach (var a in molecule.Atoms) { cx += a.X; cy += a.Y; cz += a.Z; }
            int n = molecule.Atoms.Count;
            cx /= n; cy /= n; cz /= n;

            double maxDist = 0;
            foreach (var a in molecule.Atoms)
            {
                double d = Math.Sqrt((a.X - cx) * (a.X - cx)
                                   + (a.Y - cy) * (a.Y - cy)
                                   + (a.Z - cz) * (a.Z - cz));
                if (d > maxDist) maxDist = d;
            }

            float radius  = (float)(maxDist < 0.01 ? 1.0 : maxDist);
            float padding = 0.12f;
            float usable  = Math.Min(width, height) * (1f - 2f * padding);
            return usable / (2f * radius);
        }

        // ──────────────────────────────────────────────────────────────────
        // Core render
        // ──────────────────────────────────────────────────────────────────

        private static Bitmap Render(Molecule molecule, ProjectedAtom[] projected, int width, int height,
                                     float? fixedScale = null)
        {
            var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Background
                g.Clear(Background);

                if (projected == null || projected.Length == 0)
                    return bmp;

                // Compute scale and offset.
                // When fixedScale is provided (rotation mode), use the constant 3D-sphere scale
                // and centre the molecule — prevents zoom oscillation as rotation changes the
                // 2D bounding box.  For the static thumbnail use the normal per-frame layout.
                float scale, offX, offY;
                if (fixedScale.HasValue)
                {
                    scale = fixedScale.Value;
                    offX  = width  / 2f;
                    offY  = height / 2f;
                }
                else
                {
                    ComputeLayout(projected, width, height, out scale, out offX, out offY);
                }

                // Compute depth range for fog effect
                float minDepth = float.MaxValue, maxDepth = float.MinValue;
                foreach (var pa in projected)
                {
                    if (pa.Depth < minDepth) minDepth = pa.Depth;
                    if (pa.Depth > maxDepth) maxDepth = pa.Depth;
                }
                float depthRange = (maxDepth - minDepth) < 0.01f ? 1f : (maxDepth - minDepth);

                // Atom radius base: make heavy atoms visible but not overwhelming
                float baseRadius = scale * 0.35f;   // Angstrom → pixels, fraction of bond length

                // Build lookup: atom index → projected atom
                var lookup = new ProjectedAtom[molecule.Atoms.Count];
                foreach (var pa in projected) lookup[pa.Index] = pa;

                // Merge atoms and bonds into one depth-sorted draw list (true painter's algorithm).
                // Bonds drawn as a batch before atoms caused front-facing bonds to be buried under
                // back atoms.  Sorting everything together fixes the per-layer ordering.
                // Entry: (Depth, IsAtom, IdxA, IdxB)  — bonds use avg depth of their endpoints.
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

                // Sort back-to-front (largest depth = furthest from viewer → drawn first)
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
                            DrawAtomSphere(g, sx, sy, r, ElementData.GetCpkColor(atom.Element), fogFactor);
                        }
                        else
                        {
                            var pa = lookup[idxA];
                            var pb = lookup[idxB];

                            float ax = pa.X * scale + offX;
                            float ay = pa.Y * scale + offY;
                            float bx = pb.X * scale + offX;
                            float by = pb.Y * scale + offY;

                            float dx  = bx - ax;
                            float dy  = by - ay;
                            float len = (float)Math.Sqrt(dx * dx + dy * dy);
                            if (len > 0.001f)
                            {
                                float nx = dx / len;
                                float ny = dy / len;

                                // Screen radius for each atom (same formula as DrawAtomSphere)
                                var atomA = molecule.Atoms[idxA];
                                var atomB = molecule.Atoms[idxB];
                                float rA = Math.Max(baseRadius * 0.4f, Math.Min(baseRadius * (float)ElementData.GetCovalentRadius(atomA.Element) / 0.77f, baseRadius * 2.5f));
                                float rB = Math.Max(baseRadius * 0.4f, Math.Min(baseRadius * (float)ElementData.GetCovalentRadius(atomB.Element) / 0.77f, baseRadius * 2.5f));

                                bondPen.Color = ApplyFog(Color.FromArgb(180, 180, 180), fogFactor);
                                g.DrawLine(bondPen,
                                    ax + nx * rA, ay + ny * rA,
                                    bx - nx * rB, by - ny * rB);
                            }
                        }
                    }
                }
            }

            return bmp;
        }

        // ──────────────────────────────────────────────────────────────────
        // Atom sphere (PathGradientBrush for glossy 3D appearance)
        // ──────────────────────────────────────────────────────────────────

        private static void DrawAtomSphere(Graphics g, float cx, float cy, float r, Color cpk, float fogFactor)
        {
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);

            using (var path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (var brush = new PathGradientBrush(path))
                {
                    // Highlight offset: upper-left of centre
                    brush.CenterPoint  = new PointF(cx - r * 0.28f, cy - r * 0.28f);
                    brush.CenterColor  = ApplyFog(Lighten(cpk, 0.72f), fogFactor);
                    brush.SurroundColors = new Color[] { ApplyFog(Darken(cpk, 0.60f), fogFactor) };
                    g.FillPath(brush, path);
                }
            }

            // Subtle dark outline
            using (var pen = new Pen(Color.FromArgb(80, 0, 0, 0), Math.Max(0.5f, r * 0.06f)))
                g.DrawEllipse(pen, rect);

            // Specular highlight — polished-sphere dot in upper-left quadrant
            float specR = r * 0.35f;
            float specX = cx - r * 0.32f - specR;
            float specY = cy - r * 0.32f - specR;
            int specAlpha = Math.Max(0, (int)(160 * (1f - fogFactor * 0.5f)));
            if (specAlpha > 4 && specR > 0.5f)
            {
                var specRect = new RectangleF(specX, specY, specR * 2, specR * 2);
                using (var specPath = new GraphicsPath())
                {
                    specPath.AddEllipse(specRect);
                    using (var specBrush = new PathGradientBrush(specPath))
                    {
                        specBrush.CenterPoint  = new PointF(specX + specR * 0.4f, specY + specR * 0.4f);
                        specBrush.CenterColor  = Color.FromArgb(specAlpha, 255, 255, 255);
                        specBrush.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillPath(specBrush, specPath);
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Layout helpers
        // ──────────────────────────────────────────────────────────────────

        private static void ComputeLayout(ProjectedAtom[] projected,
                                          int width, int height,
                                          out float scale, out float offX, out float offY)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var pa in projected)
            {
                if (pa.X < minX) minX = pa.X;
                if (pa.X > maxX) maxX = pa.X;
                if (pa.Y < minY) minY = pa.Y;
                if (pa.Y > maxY) maxY = pa.Y;
            }

            float rangeX = maxX - minX;
            float rangeY = maxY - minY;

            // Avoid division by zero for single-atom or linear molecules
            if (rangeX < 0.1f) rangeX = 1f;
            if (rangeY < 0.1f) rangeY = 1f;

            float padding = 0.12f; // 12% padding on each side
            float usableW = width  * (1f - 2f * padding);
            float usableH = height * (1f - 2f * padding);

            scale = Math.Min(usableW / rangeX, usableH / rangeY);

            // Centre the molecule
            float centreX = (minX + maxX) / 2f;
            float centreY = (minY + maxY) / 2f;
            offX = width  / 2f - centreX * scale;
            offY = height / 2f - centreY * scale;
        }

        // ──────────────────────────────────────────────────────────────────
        // Colour utilities
        // ──────────────────────────────────────────────────────────────────

        private static Color ApplyFog(Color c, float fogFactor)
        {
            const float FogStrength = 0.60f;
            float t = Math.Min(1f, fogFactor * FogStrength);
            return Color.FromArgb(c.A,
                (int)(c.R * (1 - t) + Background.R * t),
                (int)(c.G * (1 - t) + Background.G * t),
                (int)(c.B * (1 - t) + Background.B * t));
        }

        private static Color Lighten(Color c, float amount)
        {
            return Color.FromArgb(
                c.A,
                Math.Min(255, (int)(c.R + (255 - c.R) * amount)),
                Math.Min(255, (int)(c.G + (255 - c.G) * amount)),
                Math.Min(255, (int)(c.B + (255 - c.B) * amount)));
        }

        private static Color Darken(Color c, float amount)
        {
            return Color.FromArgb(
                c.A,
                Math.Max(0, (int)(c.R * (1f - amount))),
                Math.Max(0, (int)(c.G * (1f - amount))),
                Math.Max(0, (int)(c.B * (1f - amount))));
        }
    }
}
