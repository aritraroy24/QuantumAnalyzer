using System;
using System.Collections.Generic;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Rendering
{
    /// <summary>
    /// Projects 3D atom coordinates onto a 2D canvas.
    /// Best-angle view is computed via PCA (Jacobi eigendecomposition of the
    /// 3×3 covariance matrix), choosing the plane of maximum structural spread.
    /// </summary>
    public static class MoleculeProjector
    {
        public class ProjectedAtom
        {
            public float X     { get; set; }   // screen X (canvas units, not yet scaled)
            public float Y     { get; set; }   // screen Y
            public float Depth { get; set; }   // depth (more positive = further from viewer)
            public int   Index { get; set; }   // index into Molecule.Atoms
        }

        // ──────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────

        /// <summary>Returns the best-angle projection (PCA), sorted back-to-front.</summary>
        public static ProjectedAtom[] ProjectBestAngle(Molecule molecule)
        {
            return ProjectWithRotation(molecule, 0f, 0f);
        }

        /// <summary>
        /// Returns the effective 3×3 rotation matrix that maps centred world-space coordinates
        /// to screen-space (X right, Y up-positive, Z toward viewer), combining the PCA step
        /// with the user rotation.  Triangle vertices can be projected by multiplying this
        /// matrix onto <c>v - centroid</c> (flip Y for screen).
        /// </summary>
        internal static float[,] GetCombinedRotation(Molecule molecule, float[,] userRot)
        {
            var atoms = molecule.Atoms;
            int n = atoms.Count;
            if (n == 0) return userRot;

            double cx = 0, cy = 0, cz = 0;
            foreach (var a in atoms) { cx += a.X; cy += a.Y; cz += a.Z; }
            cx /= n; cy /= n; cz /= n;

            double[][] pts = new double[n][];
            for (int i = 0; i < n; i++)
                pts[i] = new double[] { atoms[i].X - cx, atoms[i].Y - cy, atoms[i].Z - cz };

            double[,] cov = Covariance(pts);
            Jacobi3x3(cov, out double[] eigenvalues, out double[,] eigenvectors);
            int[] order = SortDescending(eigenvalues);

            // PCA basis as 3×3 float matrix: rows are screen-X, screen-Y, depth axes
            var pca = new float[3, 3];
            for (int j = 0; j < 3; j++)
            {
                double[] col = GetColumn(eigenvectors, order[j]);
                pca[j, 0] = (float)col[0];
                pca[j, 1] = (float)col[1];
                pca[j, 2] = (float)col[2];
            }

            // Combined = userRot @ PCA  (3×3 matrix multiply)
            var combined = new float[3, 3];
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                    for (int j = 0; j < 3; j++)
                        combined[i, j] += userRot[i, k] * pca[k, j];
            return combined;
        }

        /// <summary>
        /// Returns a projection with an explicit 3×3 rotation matrix applied after PCA.
        /// The matrix operates in PCA-aligned coordinates (X = max variance, Y = 2nd, Z = depth).
        /// Used by the arcball-based interactive preview.
        /// </summary>
        public static ProjectedAtom[] ProjectWithMatrix(Molecule molecule, float[,] userRot)
        {
            var atoms = molecule.Atoms;
            int n = atoms.Count;
            if (n == 0) return Array.Empty<ProjectedAtom>();

            // 1. Centroid
            double cx = 0, cy = 0, cz = 0;
            foreach (var a in atoms) { cx += a.X; cy += a.Y; cz += a.Z; }
            cx /= n; cy /= n; cz /= n;

            // 2. Centered coordinates
            double[][] pts = new double[n][];
            for (int i = 0; i < n; i++)
                pts[i] = new double[] { atoms[i].X - cx, atoms[i].Y - cy, atoms[i].Z - cz };

            // 3. PCA
            double[,] cov = Covariance(pts);
            Jacobi3x3(cov, out double[] eigenvalues, out double[,] eigenvectors);
            int[] order = SortDescending(eigenvalues);
            double[] ax0 = GetColumn(eigenvectors, order[0]);
            double[] ax1 = GetColumn(eigenvectors, order[1]);
            double[] ax2 = GetColumn(eigenvectors, order[2]);

            // 4. PCA rotation
            double[][] pca = new double[n][];
            for (int i = 0; i < n; i++)
                pca[i] = new double[] { Dot(pts[i], ax0), Dot(pts[i], ax1), Dot(pts[i], ax2) };

            // 5. Apply user rotation matrix
            var result = new ProjectedAtom[n];
            for (int i = 0; i < n; i++)
            {
                double x = pca[i][0], y = pca[i][1], z = pca[i][2];
                double xR = userRot[0, 0]*x + userRot[0, 1]*y + userRot[0, 2]*z;
                double yR = userRot[1, 0]*x + userRot[1, 1]*y + userRot[1, 2]*z;
                double zR = userRot[2, 0]*x + userRot[2, 1]*y + userRot[2, 2]*z;
                result[i] = new ProjectedAtom
                {
                    X     = (float)xR,
                    Y     = (float)(-yR),  // flip Y (screen Y increases downward)
                    Depth = (float)zR,
                    Index = i,
                };
            }

            Array.Sort(result, (a, b) => b.Depth.CompareTo(a.Depth));
            return result;
        }

        /// <summary>
        /// Returns a projection starting from the PCA orientation then applying
        /// additional rotations around Y (horizontal spin) and X (tilt).
        /// </summary>
        public static ProjectedAtom[] ProjectWithRotation(Molecule molecule, float angleYDeg, float angleXDeg = 8f)
        {
            var atoms = molecule.Atoms;
            int n = atoms.Count;
            if (n == 0) return Array.Empty<ProjectedAtom>();

            // 1. Centroid
            double cx = 0, cy = 0, cz = 0;
            foreach (var a in atoms) { cx += a.X; cy += a.Y; cz += a.Z; }
            cx /= n; cy /= n; cz /= n;

            // 2. Centered coordinates
            double[][] pts = new double[n][];
            for (int i = 0; i < n; i++)
                pts[i] = new double[] { atoms[i].X - cx, atoms[i].Y - cy, atoms[i].Z - cz };

            // 3. PCA — rotate to principal axes
            double[,] cov = Covariance(pts);
            Jacobi3x3(cov, out double[] eigenvalues, out double[,] eigenvectors);

            // Sort axes: largest variance → screen X, second → screen Y, smallest → depth
            int[] order = SortDescending(eigenvalues);
            double[] ax0 = GetColumn(eigenvectors, order[0]);  // primary   (screen X)
            double[] ax1 = GetColumn(eigenvectors, order[1]);  // secondary (screen Y)
            double[] ax2 = GetColumn(eigenvectors, order[2]);  // depth axis

            // 4. Apply PCA rotation to get coordinates in principal-axis frame
            double[][] pca = new double[n][];
            for (int i = 0; i < n; i++)
            {
                pca[i] = new double[]
                {
                    Dot(pts[i], ax0),
                    Dot(pts[i], ax1),
                    Dot(pts[i], ax2),
                };
            }

            // 5. Apply Y-axis rotation (spin around vertical axis)
            float ry = angleYDeg * (float)(Math.PI / 180.0);
            float rx = angleXDeg * (float)(Math.PI / 180.0);
            double cosY = Math.Cos(ry), sinY = Math.Sin(ry);
            double cosX = Math.Cos(rx), sinX = Math.Sin(rx);

            var result = new ProjectedAtom[n];
            for (int i = 0; i < n; i++)
            {
                double x = pca[i][0];
                double y = pca[i][1];
                double z = pca[i][2];

                // Rotate around Y axis
                double xY =  x * cosY + z * sinY;
                double zY = -x * sinY + z * cosY;
                double yY =  y;

                // Rotate around X axis (slight tilt for depth perception)
                double xF = xY;
                double yF =  yY * cosX - zY * sinX;
                double zF =  yY * sinX + zY * cosX;

                result[i] = new ProjectedAtom
                {
                    X     = (float)xF,
                    Y     = (float)(-yF),  // flip Y (screen Y increases downward)
                    Depth = (float)zF,
                    Index = i,
                };
            }

            // 6. Sort back-to-front (painter's algorithm)
            Array.Sort(result, (a, b) => b.Depth.CompareTo(a.Depth));
            return result;
        }

        // ──────────────────────────────────────────────────────────────────
        // Jacobi eigendecomposition (3×3 symmetric matrix)
        // ──────────────────────────────────────────────────────────────────

        private static void Jacobi3x3(double[,] A, out double[] eigenvalues, out double[,] V)
        {
            // Work on a copy
            double[,] S = (double[,])A.Clone();
            V = Identity3();

            const int maxIter = 200;
            const double eps  = 1e-12;

            for (int iter = 0; iter < maxIter; iter++)
            {
                // Find largest off-diagonal element
                double maxVal = 0;
                int p = 0, q = 1;
                for (int i = 0; i < 3; i++)
                    for (int j = i + 1; j < 3; j++)
                        if (Math.Abs(S[i, j]) > maxVal) { maxVal = Math.Abs(S[i, j]); p = i; q = j; }

                if (maxVal < eps) break;

                // Givens rotation angle
                double theta = 0.5 * Math.Atan2(2.0 * S[p, q], S[q, q] - S[p, p]);
                double c = Math.Cos(theta);
                double s = Math.Sin(theta);

                // Build rotation matrix G (identity with 2×2 block at p,q)
                double[,] G = Identity3();
                G[p, p] =  c; G[q, q] = c;
                G[p, q] =  s; G[q, p] = -s;

                S = Mat3Mul(Mat3Mul(Transpose3(G), S), G);
                V = Mat3Mul(V, G);
            }

            eigenvalues = new double[] { S[0, 0], S[1, 1], S[2, 2] };
        }

        // ──────────────────────────────────────────────────────────────────
        // Matrix / vector helpers (3×3)
        // ──────────────────────────────────────────────────────────────────

        private static double[,] Covariance(double[][] pts)
        {
            int n = pts.Length;
            double[,] cov = new double[3, 3];
            foreach (double[] p in pts)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        cov[i, j] += p[i] * p[j];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    cov[i, j] /= n;
            return cov;
        }

        private static double[,] Identity3()
        {
            double[,] I = new double[3, 3];
            I[0, 0] = I[1, 1] = I[2, 2] = 1.0;
            return I;
        }

        private static double[,] Transpose3(double[,] M)
        {
            double[,] T = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    T[i, j] = M[j, i];
            return T;
        }

        private static double[,] Mat3Mul(double[,] A, double[,] B)
        {
            double[,] C = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        C[i, j] += A[i, k] * B[k, j];
            return C;
        }

        private static double[] GetColumn(double[,] M, int col)
        {
            return new double[] { M[0, col], M[1, col], M[2, col] };
        }

        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        private static int[] SortDescending(double[] vals)
        {
            int[] idx = { 0, 1, 2 };
            Array.Sort(idx, (a, b) => vals[b].CompareTo(vals[a]));
            return idx;
        }
    }
}
