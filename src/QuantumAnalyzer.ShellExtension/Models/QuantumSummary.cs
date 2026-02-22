using System.Collections.Generic;

namespace QuantumAnalyzer.ShellExtension.Models
{
    public enum SoftwareType { Gaussian, Orca, Structure, CubeFile, VASP }

    public class QuantumSummary
    {
        // ── Identity ──────────────────────────────────────────────
        public SoftwareType Software { get; set; }

        // ── Overall ───────────────────────────────────────────────
        public string CalcType { get; set; }          // SP / OPT / FREQ / OPT+FREQ
        public string Method { get; set; }            // e.g. UPBE1PBE
        public string BasisSet { get; set; }          // e.g. Gen / 6-31G*
        public int Charge { get; set; }
        public string Spin { get; set; }              // Singlet / Doublet / Triplet …
        public string Solvation { get; set; }         // None / SMD(water) …
        public double? ElectronicEnergy { get; set; } // Hartree
        public int ImaginaryFreq { get; set; }        // count of negative frequencies
        public int? HomoIndex { get; set; }           // 1-based MO number
        public int? LumoIndex { get; set; }
        public string HomoSpin { get; set; }          // Alpha / Beta
        public double? HomoLumoGap { get; set; }      // eV

        // ── Thermochemistry (null when no FREQ job) ───────────────
        public double? ZPE { get; set; }
        public double? ThermalEnergy { get; set; }
        public double? ThermalEnthalpy { get; set; }
        public double? ThermalFreeEnergy { get; set; }
        public double? EE_ZPE { get; set; }
        public double? EE_Thermal { get; set; }
        public double? EE_Enthalpy { get; set; }
        public double? EE_FreeEnergy { get; set; }
        public double? EThermal_kcal { get; set; }   // kcal/mol
        public double? Cv { get; set; }               // cal/mol·K
        public double? Entropy { get; set; }          // cal/mol·K

        // ── Job status ────────────────────────────────────────────────
        public bool NormalTermination { get; set; }   // true when "Normal termination" found

        // ── Atom composition (input files and xyz) ────────────────────
        public Dictionary<string, int> AtomCounts { get; set; }
    }
}
