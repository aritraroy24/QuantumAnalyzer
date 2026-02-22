using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SharpShell.Attributes;
using SharpShell.SharpInfoTipHandler;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Parsers;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Provides a detailed key-value text tooltip on hover for Gaussian / ORCA output files
    /// and input files (.gjf, .inp). Returns empty string for .xyz and unrecognised files.
    /// </summary>
    [ComVisible(true)]
    [Guid("7E4B2345-6789-4BCD-9EF0-2B3C4D5E6F7A")]
    [DisplayName("QuantumAnalyzer Info Tip Handler")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".log")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".out")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".gjf")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".com")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".inp")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".xyz")]
    public class QuantumInfoTipHandler : SharpInfoTipHandler
    {
        protected override string GetInfo(RequestedInfoType requestedInfoType, bool singleLine)
        {
            try
            {
                string ext = Path.GetExtension(SelectedItemPath ?? "")?.ToLowerInvariant() ?? "";
                if (ext == ".xyz") return string.Empty;

                var result = ParserFactory.TryParse(SelectedItemPath);
                if (result == null || result.Summary == null) return string.Empty;

                return singleLine
                    ? FormatSingleLine(result.Summary)
                    : FormatMultiLine(result.Summary, SelectedItemPath);
            }
            catch
            {
                return string.Empty;
            }
        }

        // ──────────────────────────────────────────────────────────────────

        private static string FormatSingleLine(QuantumSummary s)
        {
            if (s.Software == SoftwareType.VASP && s.TotalEnergyEV.HasValue)
            {
                int natoms = 0;
                if (s.AtomCounts != null)
                    foreach (int v in s.AtomCounts.Values) natoms += v;
                string perAtom = natoms > 0 ? $"  ({s.TotalEnergyEV.Value / natoms:F3}/atom)" : "";
                return $"[VASP] {s.CalcType ?? "OUTCAR"}  E={s.TotalEnergyEV.Value:F3} eV{perAtom}";
            }
            string energy = s.ElectronicEnergy.HasValue ? $"  E={s.ElectronicEnergy.Value:F4} Eh" : "";
            return $"[{s.Software}] {s.Method ?? "?"}/{s.BasisSet ?? "?"}  {s.CalcType ?? ""}  {s.Spin ?? ""}{energy}";
        }

        internal static string FormatMultiLine(QuantumSummary s, string filePath, bool forSummary = false)
        {
            var sb = new StringBuilder();
            string ext = Path.GetExtension(filePath ?? "")?.ToLowerInvariant() ?? "";
            bool isInputFile = ext == ".gjf" || ext == ".com" || ext == ".inp";

            string sw = s.Software == SoftwareType.Gaussian ? "GAUSSIAN"
                      : s.Software == SoftwareType.Orca     ? "ORCA"
                      : s.Software == SoftwareType.VASP     ? "VASP"
                      : "STRUCTURE";

            string methodBasis;
            if (s.Software == SoftwareType.VASP)
                methodBasis = s.CalcType ?? "OUTCAR";
            else
                methodBasis = (!string.IsNullOrEmpty(s.Method) && !string.IsNullOrEmpty(s.BasisSet))
                    ? $"{s.Method}/{s.BasisSet}"
                    : s.Method ?? s.BasisSet ?? "?";
            sb.AppendLine($"[{sw}]  {methodBasis}");
            sb.AppendLine(new string('─', 42));

            bool hasThermo = s.ZPE.HasValue || s.EE_ZPE.HasValue;

            if (s.ElectronicEnergy.HasValue || s.NormalTermination)
                AppendField(sb, "Status", s.NormalTermination ? "Normal termination" : "Abnormal / incomplete", forSummary);

            AppendField(sb, "Type",      s.CalcType, forSummary);
            AppendField(sb, "Charge",    s.Charge.ToString(), forSummary);
            AppendField(sb, "Spin",      s.Spin, forSummary);
            AppendField(sb, "Solvation", s.Solvation ?? "None", forSummary);

            // Show energy — eV for VASP, Hartree for Gaussian/ORCA (only when no thermo section)
            if (s.Software == SoftwareType.VASP && s.TotalEnergyEV.HasValue)
            {
                int natoms = 0;
                if (s.AtomCounts != null)
                    foreach (int v in s.AtomCounts.Values) natoms += v;
                AppendField(sb, "Total Energy", $"{s.TotalEnergyEV.Value:F6} eV", forSummary);
                if (natoms > 0)
                    AppendField(sb, "Energy/Atom",
                        $"{s.TotalEnergyEV.Value / natoms:F6} eV", forSummary);
                if (s.FermiEnergyEV.HasValue)
                    AppendField(sb, "Fermi Energy", $"{s.FermiEnergyEV.Value:F4} eV", forSummary);
            }
            else if (s.ElectronicEnergy.HasValue && !hasThermo)
                AppendField(sb, "Total Energy", $"{s.ElectronicEnergy.Value:F6} Eh", forSummary);

            if (!isInputFile && s.CalcType != null && s.CalcType.Contains("FREQ"))
                AppendField(sb, "Imaginary Freq.", s.ImaginaryFreq.ToString(), forSummary);

            if (s.HomoIndex.HasValue)
            {
                AppendField(sb, "HOMO", $"{s.HomoIndex} ({s.HomoSpin})", forSummary);
                AppendField(sb, "LUMO", $"{s.LumoIndex} ({s.HomoSpin})", forSummary);
            }
            if (s.HomoLumoGap.HasValue)
                AppendField(sb, "HOMO→LUMO Gap", $"{s.HomoLumoGap.Value:F3} eV", forSummary);

            // VASP calculation settings section
            bool hasVaspSettings = s.Software == SoftwareType.VASP &&
                (s.Encut.HasValue || s.KpointMesh != null || s.Ismear.HasValue ||
                 s.Sigma.HasValue || s.Ediff.HasValue || s.Ediffg.HasValue ||
                 s.Isif.HasValue  || s.Ibrion.HasValue);
            if (hasVaspSettings)
            {
                sb.AppendLine();
                sb.AppendLine("CALCULATION SETTINGS");
                sb.AppendLine(new string('\u2500', 42));
                if (s.Encut.HasValue)
                    AppendField(sb, "Energy Cutoff", $"{s.Encut.Value:F1} eV", forSummary);
                if (s.KpointMesh != null)
                    AppendField(sb, "K-point Mesh", s.KpointMesh, forSummary);
                if (s.Ismear.HasValue)
                {
                    string smearLabel;
                    if      (s.Ismear.Value == -5) smearLabel = "Tetrahedron";
                    else if (s.Ismear.Value == -1) smearLabel = "Fermi";
                    else if (s.Ismear.Value ==  0) smearLabel = "Gaussian";
                    else if (s.Ismear.Value >=  1) smearLabel = $"Methfessel-Paxton (N={s.Ismear.Value})";
                    else                           smearLabel = s.Ismear.Value.ToString();
                    AppendField(sb, "Smearing Type", $"{s.Ismear.Value} ({smearLabel})", forSummary);
                }
                if (s.Sigma.HasValue)
                    AppendField(sb, "Smearing Width", $"{s.Sigma.Value:F4} eV", forSummary);
                if (s.Ediff.HasValue)
                    AppendField(sb, "EDIFF", $"{s.Ediff.Value:G4} eV", forSummary);
                if (s.Ediffg.HasValue)
                    AppendField(sb, "EDIFFG", $"{s.Ediffg.Value:G4} eV/\u212B", forSummary);
                if (s.Isif.HasValue)
                    AppendField(sb, "ISIF", s.Isif.Value.ToString(), forSummary);
                if (s.Ibrion.HasValue)
                    AppendField(sb, "IBRION", s.Ibrion.Value.ToString(), forSummary);
            }

            // Energy details section
            if (hasThermo)
            {
                sb.AppendLine();
                sb.AppendLine("ENERGY DETAILS");
                sb.AppendLine(new string('─', 42));
                AppendFieldOpt(sb, "Total Energy",              s.ElectronicEnergy,  "F6", " Eh", forSummary);
                AppendFieldOpt(sb, "ZPE Corr.",                 s.ZPE,               "F5", " Eh", forSummary);
                AppendFieldOpt(sb, "Enthalpy Corr.",            s.ThermalEnthalpy,   "F5", " Eh", forSummary);
                AppendFieldOpt(sb, "Free E Corr.",              s.ThermalFreeEnergy, "F5", " Eh", forSummary);
                AppendFieldOpt(sb, "Tot. E + ZPE Corr.",        s.EE_ZPE,            "F6", " Eh", forSummary);
                AppendFieldOpt(sb, "Tot. E + Enthalpy Corr.",   s.EE_Enthalpy,       "F6", " Eh", forSummary);
                AppendFieldOpt(sb, "Tot. E + Free E Corr.",     s.EE_FreeEnergy,     "F6", " Eh", forSummary);
            }

            return sb.ToString().TrimEnd();
        }

        // Returns enough tabs so the colon aligns reasonably for labels of varying lengths.
        // Tab stops are every 8 characters; targets column 24 for short/medium labels.
        // forSummary adjusts specific labels that render differently in monospaced summary files.
        private static string GetTabs(string label, bool forSummary = false)
        {
            int len = label.Length;
            // Special cases requested by user
            if (label == "HOMO→LUMO Gap")       return forSummary ? "\t\t" : "\t";
            if (label == "Tot. E + ZPE Corr.")  return forSummary ? "\t"   : "\t\t";
            // VASP-specific overrides for labels that cross tab boundaries awkwardly
            if (label == "Smearing Width")       return "\t";    // 14 chars → 1 tab to col 16
            if (label == "EDIFF")                return "\t\t\t";
            if (label == "EDIFFG")               return "\t\t\t";
            if (label == "ISIF")                 return "\t\t\t";
            if (label == "IBRION")               return "\t\t\t";

            if (len < 8)  return "\t\t\t";
            if (len < 16) return "\t\t";
            return "\t";
        }

        private static void AppendField(StringBuilder sb, string label, string value, bool forSummary = false)
        {
            if (string.IsNullOrEmpty(value)) return;
            sb.AppendLine($"{label}{GetTabs(label, forSummary)}: {value}");
        }

        private static void AppendFieldOpt(StringBuilder sb, string label, double? value,
                                           string fmt, string unit, bool forSummary = false)
        {
            if (!value.HasValue) return;
            sb.AppendLine($"{label}{GetTabs(label, forSummary)}: {value.Value.ToString(fmt)}{unit}");
        }
    }
}
