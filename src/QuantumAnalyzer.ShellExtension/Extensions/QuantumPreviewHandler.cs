using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpShell.Attributes;
using SharpShell.SharpPreviewHandler;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Parsers;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Preview Pane handler (Alt+P in Explorer) that shows a live rotating
    /// 3D molecule for Gaussian / ORCA output files.
    /// DoPreview() returns the control — SharpShell handles window hosting.
    /// </summary>
    [ComVisible(true)]
    [Guid("8D5C3456-789A-4CDE-AEF1-3C4D5E6F7A8B")]
    [DisplayName("QuantumAnalyzer Preview Handler")]
    [PreviewHandler]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".log")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".out")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".gjf")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".com")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".inp")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".xyz")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".cube")]
    public class QuantumPreviewHandler : SharpPreviewHandler
    {
        protected override PreviewHandlerControl DoPreview()
        {
            try
            {
                string filePath = SelectedFilePath;
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    var result      = ParserFactory.TryParse(filePath);
                    string ext      = Path.GetExtension(filePath).ToLowerInvariant();
                    string fileName = Path.GetFileName(filePath);
                    string label    = BuildDisplayLabel(fileName, ext, result?.Summary);

                    // Route .cube files (or any result with volumetric data) to CubePreviewControl
                    if (result?.VolumetricData != null)
                    {
                        var cubeCtrl = new CubePreviewControl();
                        cubeCtrl.SetData(result.Molecule, result.VolumetricData, label);
                        return cubeCtrl;
                    }

                    var control = new MoleculePreviewControl();
                    control.SetMolecule(result?.Molecule, label);
                    return control;
                }
            }
            catch
            {
                // Fail silently — return blank control
            }
            return new MoleculePreviewControl();
        }

        private static string BuildDisplayLabel(string fileName, string ext, QuantumSummary summary)
        {
            if (ext == ".xyz")
                return $"[{fileName}] - XYZ Structure";

            if (ext == ".cube")
                return $"[{fileName}] - Cube File";

            string s = summary == null ? string.Empty
                : $"{summary.Software}  {summary.Method ?? "?"}/{summary.BasisSet ?? "?"}  {summary.CalcType ?? ""}  {summary.Spin ?? ""}".Trim();
            return string.IsNullOrEmpty(s) ? fileName : $"{fileName}  —  {s}";
        }
    }
}
