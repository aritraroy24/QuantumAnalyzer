namespace QuantumAnalyzer.ShellExtension.Models
{
    /// <summary>
    /// Holds the 3D scalar field from a Gaussian .cube file.
    /// All coordinates are in Angstroms (converted from Bohr during parsing).
    /// </summary>
    public class VolumetricGrid
    {
        public int NX { get; set; }
        public int NY { get; set; }
        public int NZ { get; set; }

        /// <summary>World-space origin of the grid (Angstroms). Length 3.</summary>
        public double[] Origin { get; set; }

        /// <summary>
        /// Step vectors along each axis (Angstroms per voxel).
        /// Axes[0,*] = X step, Axes[1,*] = Y step, Axes[2,*] = Z step.
        /// </summary>
        public double[,] Axes { get; set; }

        /// <summary>Scalar values indexed [ix, iy, iz].</summary>
        public float[,,] Data { get; set; }

        /// <summary>Description from the cube file comment lines.</summary>
        public string DataLabel { get; set; }
    }
}
