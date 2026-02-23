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
    /// DoPreview() returns the control=> SharpShell handles window hosting.
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
    [COMServerAssociation(AssociationType.ClassOfExtension, ".cub")]
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
                    string title    = fileName;

                    // Route CHGCAR (volumetric + crystal data) to ChgcarPreviewControl
                    if (result?.VolumetricData != null && result?.CrystalData != null)
                    {
                        var chgcarCtrl = new ChgcarPreviewControl();
                        chgcarCtrl.SetData(result.Molecule, result.VolumetricData, result.CrystalData, title);
                        return chgcarCtrl;
                    }

                    // Route .cube/.cub files (volumetric data only) to CubePreviewControl
                    if (result?.VolumetricData != null)
                    {
                        var cubeCtrl = new CubePreviewControl();
                        cubeCtrl.SetData(result.Molecule, result.VolumetricData, title);
                        return cubeCtrl;
                    }

                    // Route POSCAR/CONTCAR (crystal data only) to CrystalPreviewControl
                    if (result?.CrystalData != null)
                    {
                        var crystalCtrl = new CrystalPreviewControl();
                        crystalCtrl.SetCrystal(result.Molecule, result.CrystalData, title);
                        return crystalCtrl;
                    }

                    // Route OUTCAR (with step data) to OutcarPreviewControl
                    if (string.IsNullOrEmpty(ext) && IsOutcarFilename(fileName) &&
                        result?.OutcarStepData != null)
                    {
                        var outcarCtrl = new OutcarPreviewControl();
                        outcarCtrl.SetData(result.Molecule, result.OutcarStepData, title);
                        return outcarCtrl;
                    }

                    var control = new MoleculePreviewControl();
                    if (result?.MoleculeFrames != null && result.MoleculeFrames.Count > 1)
                        control.SetMolecules(
                            result.MoleculeFrames,
                            result.MoleculeFrameNames,
                            label,
                            result?.Summary,
                            filePath,
                            result?.OptimizationStepEnergiesEV);
                    else
                        control.SetMolecule(
                            result?.Molecule,
                            label,
                            result?.Summary,
                            filePath,
                            result?.OptimizationStepEnergiesEV);
                    return control;
                }
            }
            catch
            {
                // Fail silently=> return blank control
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

        private static bool IsOutcarFilename(string name)
        {
            return name.ToUpperInvariant() == "OUTCAR";
        }

        private static string BuildDisplayLabel(string fileName, string ext, QuantumSummary summary)
        {
            if (ext == ".xyz")
                return $"[{fileName}] - XYZ Structure";

            if (ext == ".cube" || ext == ".cub")
                return $"[{fileName}] - Cube File";

            if (ext == ".poscar" || ext == ".contcar"
                || (string.IsNullOrEmpty(ext) && IsVaspStructureFilename(fileName)))
                return $"[{fileName}] - VASP Structure";

            if (string.IsNullOrEmpty(ext) && IsChgcarFilename(fileName))
                return $"[{fileName}] - Charge Density";

            if (string.IsNullOrEmpty(ext) && IsOutcarFilename(fileName))
            {
                string calcType = summary?.CalcType ?? "OUTCAR";
                if (summary?.TotalEnergyEV.HasValue == true)
                {
                    double e    = summary.TotalEnergyEV.Value;
                    int natoms  = 0;
                    if (summary.AtomCounts != null)
                        foreach (int v in summary.AtomCounts.Values) natoms += v;
                    string perAtom = natoms > 0 ? $"  ({e / natoms:F3} eV/atom)" : "";
                    return $"[{fileName}]=> VASP {calcType}  E = {e:F3} eV{perAtom}";
                }
                return $"[{fileName}]=> VASP {calcType}";
            }

            string s = summary == null ? string.Empty
                : $"{summary.Software}  {summary.Method ?? "?"}/{summary.BasisSet ?? "?"}  {summary.CalcType ?? ""}  {summary.Spin ?? ""}".Trim();
            return string.IsNullOrEmpty(s) ? fileName : $"{fileName}=>  {s}";
        }
    }
}
