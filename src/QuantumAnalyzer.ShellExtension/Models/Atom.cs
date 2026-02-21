namespace QuantumAnalyzer.ShellExtension.Models
{
    public class Atom
    {
        public string Element { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public Atom(string element, double x, double y, double z)
        {
            Element = element;
            X = x;
            Y = y;
            Z = z;
        }
    }
}
