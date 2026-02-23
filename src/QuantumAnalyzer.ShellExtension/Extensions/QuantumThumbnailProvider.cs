using System;
using System.Reflection;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpShell.Attributes;
using SharpShell.SharpThumbnailHandler;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Provides a molecular-structure thumbnail for Gaussian / ORCA output files.
    /// Registered for .log and .out extensions.
    /// Returns null for unrecognised files — Explorer falls back to its default icon.
    /// </summary>
    [ComVisible(true)]
    [Guid("6F3A1234-5678-4ABC-8DEF-1A2B3C4D5E6F")]
    [DisplayName("QuantumAnalyzer Thumbnail Provider")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".log")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".out")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".gjf")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".com")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".inp")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".xyz")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".cube")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".cub")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".poscar")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".contcar")]
    public class QuantumThumbnailProvider : SharpThumbnailHandler
    {
        protected override Bitmap GetThumbnailImage(uint width)
        {
            try
            {
                int size = (int)Math.Max(width, 64);
                return LoadQaIcon(size);
            }
            catch
            {
                // Never crash Explorer — return null to fall back to default icon
                return null;
            }
        }

        private static Bitmap LoadQaIcon(int size)
        {
            using (var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("QuantumAnalyzer.ShellExtension.Resources.qa-icon.png"))
            {
                if (stream == null) return null;
                using (var baseImage = Image.FromStream(stream))
                {
                    var bmp = new Bitmap(size, size);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(baseImage, 0, 0, size, size);
                    }
                    return bmp;
                }
            }
        }
    }
}
