using System;
using System.Collections.Generic;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Rendering
{
    /// <summary>
    /// Classic Marching Cubes isosurface extraction.
    /// Returns a triangle list in world Angstrom coordinates.
    /// </summary>
    public static class MarchingCubes
    {
        public struct Triangle
        {
            public float[] V0;   // float[3] — world Angstrom position
            public float[] V1;
            public float[] V2;
            public float[] Normal; // surface normal (unit vector, pointing outward)
        }

        /// <summary>
        /// Extract isosurface triangles from <paramref name="grid"/>.
        /// When <paramref name="positivePolarity"/> is true: extracts where value >= isovalue.
        /// When false: extracts where value &lt;= -isovalue.
        /// Caller should cache the result and only re-extract when isovalue changes.
        /// </summary>
        public static List<Triangle> Extract(VolumetricGrid grid, float isovalue, bool positivePolarity)
        {
            var triangles = new List<Triangle>(2048);

            int nx = grid.NX, ny = grid.NY, nz = grid.NZ;
            float[,,] data = grid.Data;

            // Always use isovalue as threshold — GetVal() negates the data for negative
            // polarity, so the >= check becomes: -v >= isovalue  →  v <= -isovalue.
            float threshold = isovalue;

            for (int ix = 0; ix < nx - 1; ix++)
            for (int iy = 0; iy < ny - 1; iy++)
            for (int iz = 0; iz < nz - 1; iz++)
            {
                // 8 corners of this voxel cube, ordered by the MC convention:
                //  0=(ix,iy,iz)   1=(ix+1,iy,iz)   2=(ix+1,iy+1,iz)   3=(ix,iy+1,iz)
                //  4=(ix,iy,iz+1) 5=(ix+1,iy,iz+1) 6=(ix+1,iy+1,iz+1) 7=(ix,iy+1,iz+1)
                float v0 = GetVal(data, ix,   iy,   iz,   positivePolarity);
                float v1 = GetVal(data, ix+1, iy,   iz,   positivePolarity);
                float v2 = GetVal(data, ix+1, iy+1, iz,   positivePolarity);
                float v3 = GetVal(data, ix,   iy+1, iz,   positivePolarity);
                float v4 = GetVal(data, ix,   iy,   iz+1, positivePolarity);
                float v5 = GetVal(data, ix+1, iy,   iz+1, positivePolarity);
                float v6 = GetVal(data, ix+1, iy+1, iz+1, positivePolarity);
                float v7 = GetVal(data, ix,   iy+1, iz+1, positivePolarity);

                // Build 8-bit case index
                int cubeIndex = 0;
                if (v0 >= threshold) cubeIndex |= 1;
                if (v1 >= threshold) cubeIndex |= 2;
                if (v2 >= threshold) cubeIndex |= 4;
                if (v3 >= threshold) cubeIndex |= 8;
                if (v4 >= threshold) cubeIndex |= 16;
                if (v5 >= threshold) cubeIndex |= 32;
                if (v6 >= threshold) cubeIndex |= 64;
                if (v7 >= threshold) cubeIndex |= 128;

                if (cubeIndex == 0 || cubeIndex == 255) continue;

                // World positions of the 8 corners
                float[] p0 = GridToWorld(grid, ix,   iy,   iz  );
                float[] p1 = GridToWorld(grid, ix+1, iy,   iz  );
                float[] p2 = GridToWorld(grid, ix+1, iy+1, iz  );
                float[] p3 = GridToWorld(grid, ix,   iy+1, iz  );
                float[] p4 = GridToWorld(grid, ix,   iy,   iz+1);
                float[] p5 = GridToWorld(grid, ix+1, iy,   iz+1);
                float[] p6 = GridToWorld(grid, ix+1, iy+1, iz+1);
                float[] p7 = GridToWorld(grid, ix,   iy+1, iz+1);

                // Interpolate edge vertices
                float[] verts = new float[12 * 3]; // 12 edges, 3 floats each — interleaved for cache

                int edgeMask = EdgeTable[cubeIndex];
                if ((edgeMask &    1) != 0) Interp(verts,  0, p0, p1, v0, v1, threshold);
                if ((edgeMask &    2) != 0) Interp(verts,  1, p1, p2, v1, v2, threshold);
                if ((edgeMask &    4) != 0) Interp(verts,  2, p2, p3, v2, v3, threshold);
                if ((edgeMask &    8) != 0) Interp(verts,  3, p3, p0, v3, v0, threshold);
                if ((edgeMask &   16) != 0) Interp(verts,  4, p4, p5, v4, v5, threshold);
                if ((edgeMask &   32) != 0) Interp(verts,  5, p5, p6, v5, v6, threshold);
                if ((edgeMask &   64) != 0) Interp(verts,  6, p6, p7, v6, v7, threshold);
                if ((edgeMask &  128) != 0) Interp(verts,  7, p7, p4, v7, v4, threshold);
                if ((edgeMask &  256) != 0) Interp(verts,  8, p0, p4, v0, v4, threshold);
                if ((edgeMask &  512) != 0) Interp(verts,  9, p1, p5, v1, v5, threshold);
                if ((edgeMask & 1024) != 0) Interp(verts, 10, p2, p6, v2, v6, threshold);
                if ((edgeMask & 2048) != 0) Interp(verts, 11, p3, p7, v3, v7, threshold);

                // Emit triangles from triangle table
                int[] triRow = TriTable[cubeIndex];
                for (int t = 0; t < triRow.Length; t += 3)
                {
                    int e0 = triRow[t], e1 = triRow[t+1], e2 = triRow[t+2];
                    var tv0 = ExtractVert(verts, e0);
                    var tv1 = ExtractVert(verts, e1);
                    var tv2 = ExtractVert(verts, e2);
                    triangles.Add(new Triangle
                    {
                        V0 = tv0, V1 = tv1, V2 = tv2,
                        Normal = ComputeNormal(tv0, tv1, tv2),
                    });
                }
            }

            return triangles;
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the scalar value at the given grid index.
        /// For negative polarity, returns the negated value so threshold comparisons stay >= iso.
        /// </summary>
        private static float GetVal(float[,,] data, int ix, int iy, int iz, bool positivePolarity)
        {
            float v = data[ix, iy, iz];
            return positivePolarity ? v : -v;
        }

        private static float[] GridToWorld(VolumetricGrid g, int ix, int iy, int iz)
        {
            double x = g.Origin[0] + ix * g.Axes[0, 0] + iy * g.Axes[1, 0] + iz * g.Axes[2, 0];
            double y = g.Origin[1] + ix * g.Axes[0, 1] + iy * g.Axes[1, 1] + iz * g.Axes[2, 1];
            double z = g.Origin[2] + ix * g.Axes[0, 2] + iy * g.Axes[1, 2] + iz * g.Axes[2, 2];
            return new float[] { (float)x, (float)y, (float)z };
        }

        private static void Interp(float[] buf, int edgeIdx, float[] p0, float[] p1, float v0, float v1, float threshold)
        {
            float t = (Math.Abs(v1 - v0) < 1e-9f) ? 0.5f : (threshold - v0) / (v1 - v0);
            t = Math.Max(0f, Math.Min(1f, t));
            int off = edgeIdx * 3;
            buf[off]     = p0[0] + t * (p1[0] - p0[0]);
            buf[off + 1] = p0[1] + t * (p1[1] - p0[1]);
            buf[off + 2] = p0[2] + t * (p1[2] - p0[2]);
        }

        private static float[] ExtractVert(float[] buf, int edgeIdx)
        {
            int off = edgeIdx * 3;
            return new float[] { buf[off], buf[off + 1], buf[off + 2] };
        }

        private static float[] ComputeNormal(float[] a, float[] b, float[] c)
        {
            float ux = b[0]-a[0], uy = b[1]-a[1], uz = b[2]-a[2];
            float vx = c[0]-a[0], vy = c[1]-a[1], vz = c[2]-a[2];
            float nx = uy*vz - uz*vy;
            float ny = uz*vx - ux*vz;
            float nz = ux*vy - uy*vx;
            float len = (float)Math.Sqrt(nx*nx + ny*ny + nz*nz);
            if (len < 1e-9f) return new float[] { 0, 0, 1 };
            return new float[] { nx/len, ny/len, nz/len };
        }

        // ──────────────────────────────────────────────────────────────────
        // Standard Marching Cubes lookup tables (Paul Bourke / Lorensen & Cline)
        // EdgeTable: bitmask of which of the 12 edges are intersected
        // TriTable: triangle indices into those 12 edges
        // ──────────────────────────────────────────────────────────────────

        private static readonly int[] EdgeTable = {
            0x000,0x109,0x203,0x30a,0x406,0x50f,0x605,0x70c,
            0x80c,0x905,0xa0f,0xb06,0xc0a,0xd03,0xe09,0xf00,
            0x190,0x099,0x393,0x29a,0x596,0x49f,0x795,0x69c,
            0x99c,0x895,0xb9f,0xa96,0xd9a,0xc93,0xf99,0xe90,
            0x230,0x339,0x033,0x13a,0x636,0x73f,0x435,0x53c,
            0xa3c,0xb35,0x83f,0x936,0xe3a,0xf33,0xc39,0xd30,
            0x3a0,0x2a9,0x1a3,0x0aa,0x7a6,0x6af,0x5a5,0x4ac,
            0xbac,0xaa5,0x9af,0x8a6,0xfaa,0xea3,0xda9,0xca0,
            0x460,0x569,0x663,0x76a,0x066,0x16f,0x265,0x36c,
            0xc6c,0xd65,0xe6f,0xf66,0x86a,0x963,0xa69,0xb60,
            0x5f0,0x4f9,0x7f3,0x6fa,0x1f6,0x0ff,0x3f5,0x2fc,
            0xdfc,0xcf5,0xfff,0xef6,0x9fa,0x8f3,0xbf9,0xaf0,
            0x650,0x759,0x453,0x55a,0x256,0x35f,0x055,0x15c,
            0xe5c,0xf55,0xc5f,0xd56,0xa5a,0xb53,0x859,0x950,
            0x7c0,0x6c9,0x5c3,0x4ca,0x3c6,0x2cf,0x1c5,0x0cc,
            0xfcc,0xec5,0xdcf,0xcc6,0xbca,0xac3,0x9c9,0x8c0,
            0x8c0,0x9c9,0xac3,0xbca,0xcc6,0xdcf,0xec5,0xfcc,
            0x0cc,0x1c5,0x2cf,0x3c6,0x4ca,0x5c3,0x6c9,0x7c0,
            0x950,0x859,0xb53,0xa5a,0xd56,0xc5f,0xf55,0xe5c,
            0x15c,0x055,0x35f,0x256,0x55a,0x453,0x759,0x650,
            0xaf0,0xbf9,0x8f3,0x9fa,0xef6,0xfff,0xcf5,0xdfc,
            0x2fc,0x3f5,0x0ff,0x1f6,0x6fa,0x7f3,0x4f9,0x5f0,
            0xb60,0xa69,0x963,0x86a,0xf66,0xe6f,0xd65,0xc6c,
            0x36c,0x265,0x16f,0x066,0x76a,0x663,0x569,0x460,
            0xca0,0xda9,0xea3,0xfaa,0x8a6,0x9af,0xaa5,0xbac,
            0x4ac,0x5a5,0x6af,0x7a6,0x0aa,0x1a3,0x2a9,0x3a0,
            0xd30,0xc39,0xf33,0xe3a,0x936,0x83f,0xb35,0xa3c,
            0x53c,0x435,0x73f,0x636,0x13a,0x033,0x339,0x230,
            0xe90,0xf99,0xc93,0xd9a,0xa96,0xb9f,0x895,0x99c,
            0x69c,0x795,0x49f,0x596,0x29a,0x393,0x099,0x190,
            0xf00,0xe09,0xd03,0xc0a,0xb06,0xa0f,0x905,0x80c,
            0x70c,0x605,0x50f,0x406,0x30a,0x203,0x109,0x000
        };

        private static readonly int[][] TriTable = {
            new int[]{},
            new int[]{0,8,3},
            new int[]{0,1,9},
            new int[]{1,8,3,9,8,1},
            new int[]{1,2,10},
            new int[]{0,8,3,1,2,10},
            new int[]{9,2,10,0,2,9},
            new int[]{2,8,3,2,10,8,10,9,8},
            new int[]{3,11,2},
            new int[]{0,11,2,8,11,0},
            new int[]{1,9,0,2,3,11},
            new int[]{1,11,2,1,9,11,9,8,11},
            new int[]{3,10,1,11,10,3},
            new int[]{0,10,1,0,8,10,8,11,10},
            new int[]{3,9,0,3,11,9,11,10,9},
            new int[]{9,8,10,10,8,11},
            new int[]{4,7,8},
            new int[]{4,3,0,7,3,4},
            new int[]{0,1,9,8,4,7},
            new int[]{4,1,9,4,7,1,7,3,1},
            new int[]{1,2,10,8,4,7},
            new int[]{3,4,7,3,0,4,1,2,10},
            new int[]{9,2,10,9,0,2,8,4,7},
            new int[]{2,10,9,2,9,7,2,7,3,7,9,4},
            new int[]{8,4,7,3,11,2},
            new int[]{11,4,7,11,2,4,2,0,4},
            new int[]{9,0,1,8,4,7,2,3,11},
            new int[]{4,7,11,9,4,11,9,11,2,9,2,1},
            new int[]{3,10,1,3,11,10,7,8,4},
            new int[]{1,11,10,1,4,11,1,0,4,7,11,4},
            new int[]{4,7,8,9,0,11,9,11,10,11,0,3},
            new int[]{4,7,11,4,11,9,9,11,10},
            new int[]{9,5,4},
            new int[]{9,5,4,0,8,3},
            new int[]{0,5,4,1,5,0},
            new int[]{8,5,4,8,3,5,3,1,5},
            new int[]{1,2,10,9,5,4},
            new int[]{3,0,8,1,2,10,4,9,5},
            new int[]{5,2,10,5,4,2,4,0,2},
            new int[]{2,10,5,3,2,5,3,5,4,3,4,8},
            new int[]{9,5,4,2,3,11},
            new int[]{0,11,2,0,8,11,4,9,5},
            new int[]{0,5,4,0,1,5,2,3,11},
            new int[]{2,1,5,2,5,8,2,8,11,4,8,5},
            new int[]{10,3,11,10,1,3,9,5,4},
            new int[]{4,9,5,0,8,1,8,10,1,8,11,10},
            new int[]{5,4,0,5,0,11,5,11,10,11,0,3},
            new int[]{5,4,8,5,8,10,10,8,11},
            new int[]{9,7,8,5,7,9},
            new int[]{9,3,0,9,5,3,5,7,3},
            new int[]{0,7,8,0,1,7,1,5,7},
            new int[]{1,5,3,3,5,7},
            new int[]{9,7,8,9,5,7,10,1,2},
            new int[]{10,1,2,9,5,0,5,3,0,5,7,3},
            new int[]{8,0,2,8,2,5,8,5,7,10,5,2},
            new int[]{2,10,5,2,5,3,3,5,7},
            new int[]{7,9,5,7,8,9,3,11,2},
            new int[]{9,5,7,9,7,2,9,2,0,2,7,11},
            new int[]{2,3,11,0,1,8,1,7,8,1,5,7},
            new int[]{11,2,1,11,1,7,7,1,5},
            new int[]{9,5,8,8,5,7,10,1,3,10,3,11},
            new int[]{5,7,0,5,0,9,7,11,0,1,0,10,11,10,0},
            new int[]{11,10,0,11,0,3,10,5,0,8,0,7,5,7,0},
            new int[]{11,10,5,7,11,5},
            new int[]{10,6,5},
            new int[]{0,8,3,5,10,6},
            new int[]{9,0,1,5,10,6},
            new int[]{1,8,3,1,9,8,5,10,6},
            new int[]{1,6,5,2,6,1},
            new int[]{1,6,5,1,2,6,3,0,8},
            new int[]{9,6,5,9,0,6,0,2,6},
            new int[]{5,9,8,5,8,2,5,2,6,3,2,8},
            new int[]{2,3,11,10,6,5},
            new int[]{11,0,8,11,2,0,10,6,5},
            new int[]{0,1,9,2,3,11,5,10,6},
            new int[]{5,10,6,1,9,2,9,11,2,9,8,11},
            new int[]{6,3,11,6,5,3,5,1,3},
            new int[]{0,8,11,0,11,5,0,5,1,5,11,6},
            new int[]{3,11,6,0,3,6,0,6,5,0,5,9},
            new int[]{6,5,9,6,9,11,11,9,8},
            new int[]{5,10,6,4,7,8},
            new int[]{4,3,0,4,7,3,6,5,10},
            new int[]{1,9,0,5,10,6,8,4,7},
            new int[]{10,6,5,1,9,7,1,7,3,7,9,4},
            new int[]{6,1,2,6,5,1,4,7,8},
            new int[]{1,2,5,5,2,6,3,0,4,3,4,7},
            new int[]{8,4,7,9,0,5,0,6,5,0,2,6},
            new int[]{7,3,9,7,9,4,3,2,9,5,9,6,2,6,9},
            new int[]{3,11,2,7,8,4,10,6,5},
            new int[]{5,10,6,4,7,2,4,2,0,2,7,11},
            new int[]{0,1,9,4,7,8,2,3,11,5,10,6},
            new int[]{9,2,1,9,11,2,9,4,11,7,11,4,5,10,6},
            new int[]{8,4,7,3,11,5,3,5,1,5,11,6},
            new int[]{5,1,11,5,11,6,1,0,11,7,11,4,0,4,11},
            new int[]{0,5,9,0,6,5,0,3,6,11,6,3,8,4,7},
            new int[]{6,5,9,6,9,11,4,7,9,7,11,9},
            new int[]{10,4,9,6,4,10},
            new int[]{4,10,6,4,9,10,0,8,3},
            new int[]{10,0,1,10,6,0,6,4,0},
            new int[]{8,3,1,8,1,6,8,6,4,6,1,10},
            new int[]{1,4,9,1,2,4,2,6,4},
            new int[]{3,0,8,1,2,9,2,4,9,2,6,4},
            new int[]{0,2,4,4,2,6},
            new int[]{8,3,2,8,2,4,4,2,6},
            new int[]{10,4,9,10,6,4,11,2,3},
            new int[]{0,8,2,2,8,11,4,9,10,4,10,6},
            new int[]{3,11,2,0,1,6,0,6,4,6,1,10},
            new int[]{6,4,1,6,1,10,4,8,1,2,1,11,8,11,1},
            new int[]{9,6,4,9,3,6,9,1,3,11,6,3},
            new int[]{8,11,1,8,1,0,11,6,1,9,1,4,6,4,1},
            new int[]{3,11,6,3,6,0,0,6,4},
            new int[]{6,4,8,11,6,8},
            new int[]{7,10,6,7,8,10,8,9,10},
            new int[]{0,7,3,0,10,7,0,9,10,6,7,10},
            new int[]{10,6,7,1,10,7,1,7,8,1,8,0},
            new int[]{10,6,7,10,7,1,1,7,3},
            new int[]{1,2,6,1,6,8,1,8,9,8,6,7},
            new int[]{2,6,9,2,9,1,6,7,9,0,9,3,7,3,9},
            new int[]{7,8,0,7,0,6,6,0,2},
            new int[]{7,3,2,6,7,2},
            new int[]{2,3,11,10,6,8,10,8,9,8,6,7},
            new int[]{2,0,7,2,7,11,0,9,7,6,7,10,9,10,7},
            new int[]{1,8,0,1,7,8,1,10,7,6,7,10,2,3,11},
            new int[]{11,2,1,11,1,7,10,6,1,6,7,1},
            new int[]{8,9,6,8,6,7,9,1,6,11,6,3,1,3,6},
            new int[]{0,9,1,11,6,7},
            new int[]{7,8,0,7,0,6,3,11,0,11,6,0},
            new int[]{7,11,6},
            new int[]{7,6,11},
            new int[]{3,0,8,11,7,6},
            new int[]{0,1,9,11,7,6},
            new int[]{8,1,9,8,3,1,11,7,6},
            new int[]{10,1,2,6,11,7},
            new int[]{1,2,10,3,0,8,6,11,7},
            new int[]{2,9,0,2,10,9,6,11,7},
            new int[]{6,11,7,2,10,3,10,8,3,10,9,8},
            new int[]{7,2,3,6,2,7},
            new int[]{7,0,8,7,6,0,6,2,0},
            new int[]{2,7,6,2,3,7,0,1,9},
            new int[]{1,6,2,1,8,6,1,9,8,8,7,6},
            new int[]{10,7,6,10,1,7,1,3,7},
            new int[]{10,7,6,1,7,10,1,8,7,1,0,8},
            new int[]{0,3,7,0,7,10,0,10,9,6,10,7},
            new int[]{7,6,10,7,10,8,8,10,9},
            new int[]{6,8,4,11,8,6},
            new int[]{3,6,11,3,0,6,0,4,6},
            new int[]{8,6,11,8,4,6,9,0,1},
            new int[]{9,4,6,9,6,3,9,3,1,11,3,6},
            new int[]{6,8,4,6,11,8,2,10,1},
            new int[]{1,2,10,3,0,11,0,6,11,0,4,6},
            new int[]{4,11,8,4,6,11,0,2,9,2,10,9},
            new int[]{10,9,3,10,3,2,9,4,3,11,3,6,4,6,3},
            new int[]{8,2,3,8,4,2,4,6,2},
            new int[]{0,4,2,4,6,2},
            new int[]{1,9,0,2,3,4,2,4,6,4,3,8},
            new int[]{1,9,4,1,4,2,2,4,6},
            new int[]{8,1,3,8,6,1,8,4,6,6,10,1},
            new int[]{10,1,0,10,0,6,6,0,4},
            new int[]{4,6,3,4,3,8,6,10,3,0,3,9,10,9,3},
            new int[]{10,9,4,6,10,4},
            new int[]{4,9,5,7,6,11},
            new int[]{0,8,3,4,9,5,11,7,6},
            new int[]{5,0,1,5,4,0,7,6,11},
            new int[]{11,7,6,8,3,4,3,5,4,3,1,5},
            new int[]{9,5,4,10,1,2,7,6,11},
            new int[]{6,11,7,1,2,10,0,8,3,4,9,5},
            new int[]{7,6,11,5,4,10,4,2,10,4,0,2},
            new int[]{3,4,8,3,5,4,3,2,5,10,5,2,11,7,6},
            new int[]{7,2,3,7,6,2,5,4,9},
            new int[]{9,5,4,0,8,6,0,6,2,6,8,7},
            new int[]{3,6,2,3,7,6,1,5,0,5,4,0},
            new int[]{6,2,8,6,8,7,2,1,8,4,8,5,1,5,8},
            new int[]{9,5,4,10,1,6,1,7,6,1,3,7},
            new int[]{1,6,10,1,7,6,1,0,7,8,7,0,9,5,4},
            new int[]{4,0,10,4,10,5,0,3,10,6,10,7,3,7,10},
            new int[]{7,6,10,7,10,8,5,4,10,4,8,10},
            new int[]{6,9,5,6,11,9,11,8,9},
            new int[]{3,6,11,0,6,3,0,5,6,0,9,5},
            new int[]{0,11,8,0,5,11,0,1,5,5,6,11},
            new int[]{6,11,3,6,3,5,5,3,1},
            new int[]{1,2,10,9,5,11,9,11,8,11,5,6},
            new int[]{0,11,3,0,6,11,0,9,6,5,6,9,1,2,10},
            new int[]{11,8,5,11,5,6,8,0,5,10,5,2,0,2,5},
            new int[]{6,11,3,6,3,5,2,10,3,10,5,3},
            new int[]{5,8,9,5,2,8,5,6,2,3,8,2},
            new int[]{9,5,6,9,6,0,0,6,2},
            new int[]{1,5,8,1,8,0,5,6,8,3,8,2,6,2,8},
            new int[]{1,5,6,2,1,6},
            new int[]{1,3,6,1,6,10,3,8,6,5,6,9,8,9,6},
            new int[]{10,1,0,10,0,6,9,5,0,5,6,0},
            new int[]{0,3,8,5,6,10},
            new int[]{10,5,6},
            new int[]{11,5,10,7,5,11},
            new int[]{11,5,10,11,7,5,8,3,0},
            new int[]{5,11,7,5,10,11,1,9,0},
            new int[]{10,7,5,10,11,7,9,8,1,8,3,1},
            new int[]{11,1,2,11,7,1,7,5,1},
            new int[]{0,8,3,1,2,7,1,7,5,7,2,11},
            new int[]{9,7,5,9,2,7,9,0,2,2,11,7},
            new int[]{7,5,2,7,2,11,5,9,2,3,2,8,9,8,2},
            new int[]{2,5,10,2,3,5,3,7,5},
            new int[]{8,2,0,8,5,2,8,7,5,10,2,5},
            new int[]{9,0,1,5,10,3,5,3,7,3,10,2},
            new int[]{9,8,2,9,2,1,8,7,2,10,2,5,7,5,2},
            new int[]{1,3,5,3,7,5},
            new int[]{0,8,7,0,7,1,1,7,5},
            new int[]{9,0,3,9,3,5,5,3,7},
            new int[]{9,8,7,5,9,7},
            new int[]{5,8,4,5,10,8,10,11,8},
            new int[]{5,0,4,5,11,0,5,10,11,11,3,0},
            new int[]{0,1,9,8,4,10,8,10,11,10,4,5},
            new int[]{10,11,4,10,4,5,11,3,4,9,4,1,3,1,4},
            new int[]{2,5,1,2,8,5,2,11,8,4,5,8},
            new int[]{0,4,11,0,11,3,4,5,11,2,11,1,5,1,11},
            new int[]{0,2,5,0,5,9,2,11,5,4,5,8,11,8,5},
            new int[]{9,4,5,2,11,3},
            new int[]{2,5,10,3,5,2,3,4,5,3,8,4},
            new int[]{5,10,2,5,2,4,4,2,0},
            new int[]{3,10,2,3,5,10,3,8,5,4,5,8,0,1,9},
            new int[]{5,10,2,5,2,4,1,9,2,9,4,2},
            new int[]{8,4,5,8,5,3,3,5,1},
            new int[]{0,4,5,1,0,5},
            new int[]{8,4,5,8,5,3,9,0,5,0,3,5},
            new int[]{9,4,5},
            new int[]{4,11,7,4,9,11,9,10,11},
            new int[]{0,8,3,4,9,7,9,11,7,9,10,11},
            new int[]{1,10,11,1,11,4,1,4,0,7,4,11},
            new int[]{3,1,4,3,4,8,1,10,4,7,4,11,10,11,4},
            new int[]{4,11,7,9,11,4,9,2,11,9,1,2},
            new int[]{9,7,4,9,11,7,9,1,11,2,11,1,0,8,3},
            new int[]{11,7,4,11,4,2,2,4,0},
            new int[]{11,7,4,11,4,2,8,3,4,3,2,4},
            new int[]{2,9,10,2,7,9,2,3,7,7,4,9},
            new int[]{9,10,7,9,7,4,10,2,7,8,7,0,2,0,7},
            new int[]{3,7,10,3,10,2,7,4,10,1,10,0,4,0,10},
            new int[]{1,10,2,8,7,4},
            new int[]{4,9,1,4,1,7,7,1,3},
            new int[]{4,9,1,4,1,7,0,8,1,8,7,1},
            new int[]{4,0,3,7,4,3},
            new int[]{4,8,7},
            new int[]{9,10,8,10,11,8},
            new int[]{3,0,9,3,9,11,11,9,10},
            new int[]{0,1,10,0,10,8,8,10,11},
            new int[]{3,1,10,11,3,10},
            new int[]{1,2,11,1,11,9,9,11,8},
            new int[]{3,0,9,3,9,11,1,2,9,2,11,9},
            new int[]{0,2,11,8,0,11},
            new int[]{3,2,11},
            new int[]{2,3,8,2,8,10,10,8,9},
            new int[]{9,10,2,0,9,2},
            new int[]{2,3,8,2,8,10,0,1,8,1,10,8},
            new int[]{1,10,2},
            new int[]{1,3,8,9,1,8},
            new int[]{0,9,1},
            new int[]{0,3,8},
            new int[]{}
        };
    }
}
