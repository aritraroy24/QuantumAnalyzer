namespace QuantumAnalyzer.ShellExtension.Models
{
    public class ParseResult
    {
        public QuantumSummary Summary { get; set; }
        public Molecule Molecule { get; set; }
        public VolumetricGrid VolumetricData { get; set; }
        public LatticeCell    CrystalData   { get; set; }

        public bool IsValid => Summary != null;
    }
}
