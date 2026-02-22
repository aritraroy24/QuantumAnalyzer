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
    [COMServerAssociation(AssociationType.ClassOfExtension, ".poscar")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".contcar")]
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

                    // Route CHGCAR (volumetric + crystal data) to ChgcarPreviewControl
                    if (result?.VolumetricData != null && result?.CrystalData != null)
                    {
                        var chgcarCtrl = new ChgcarPreviewControl();
                        chgcarCtrl.SetData(result.Molecule, result.VolumetricData, result.CrystalData, label);
                        return chgcarCtrl;
                    }

                    // Route .cube files (volumetric data only) to CubePreviewControl
                    if (result?.VolumetricData != null)
                    {
                        var cubeCtrl = new CubePreviewControl();
                        cubeCtrl.SetData(result.Molecule, result.VolumetricData, label);
                        return cubeCtrl;
                    }

                    // Route POSCAR/CONTCAR (crystal data only) to CrystalPreviewControl
                    if (result?.CrystalData != null)
                    {
                        var crystalCtrl = new CrystalPreviewControl();
                        crystalCtrl.SetCrystal(result.Molecule, result.CrystalData, label);
                        return crystalCtrl;
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

        private static bool IsVaspStructureFilename(string name)
        {
            string upper = name.ToUpperInvariant();
            return upper == "POSCAR" || upper == "CONTCAR";
        }

        private static bool IsChgcarFilename(string name)
        {
            return name.ToUpperInvariant() == "CHGCAR";
        }

        private static string BuildDisplayLabel(string fileName, string ext, QuantumSummary summary)
        {
            if (ext == ".xyz")
                return $"[{fileName}] - XYZ Structure";

            if (ext == ".cube")
                return $"[{fileName}] - Cube File";

            if (ext == ".poscar" || ext == ".contcar"
                || (string.IsNullOrEmpty(ext) && IsVaspStructureFilename(fileName)))
                return $"[{fileName}] - VASP Structure";

            if (string.IsNullOrEmpty(ext) && IsChgcarFilename(fileName))
                return $"[{fileName}] - Charge Density";

            string s = summary == null ? string.Empty
                : $"{summary.Software}  {summary.Method ?? "?"}/{summary.BasisSet ?? "?"}  {summary.CalcType ?? ""}  {summary.Spin ?? ""}".Trim();
            return string.IsNullOrEmpty(s) ? fileName : $"{fileName}  —  {s}";
        }
    }
}
