using System.Collections.Generic;

namespace QuantumAnalyzer.ShellExtension.Models
{
    public class ParseResult
    {
        public QuantumSummary Summary { get; set; }
        public Molecule Molecule { get; set; }
        public List<Molecule> MoleculeFrames { get; set; }
        public List<string> MoleculeFrameNames { get; set; }
        public List<double> OptimizationStepEnergiesEV { get; set; }
        public VolumetricGrid VolumetricData { get; set; }
        public LatticeCell    CrystalData    { get; set; }
        public OutcarData     OutcarStepData { get; set; }

        public bool IsValid => Summary != null;
    }
}

