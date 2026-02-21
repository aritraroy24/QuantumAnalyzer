using System;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpShell.Attributes;
using SharpShell.SharpThumbnailHandler;
using QuantumAnalyzer.ShellExtension.Parsers;
using QuantumAnalyzer.ShellExtension.Rendering;

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
    public class QuantumThumbnailProvider : SharpThumbnailHandler
    {
        protected override Bitmap GetThumbnailImage(uint width)
        {
            try
            {
                // SelectedItemStream is provided by SharpShell from the shell item
                if (SelectedItemStream == null) return null;

                var result = ParserFactory.TryParse(SelectedItemStream);
                if (result == null || result.Molecule == null || !result.Molecule.HasGeometry)
                    return null;

                int size = (int)Math.Max(width, 64);
                return MoleculeRenderer.RenderBestAngle(result.Molecule, size, size);
            }
            catch
            {
                // Never crash Explorer — return null to fall back to default icon
                return null;
            }
        }
    }
}
