using System.IO;
using System.Text;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Writes a plain-text summary file ([filename]_summary.txt) next to the source file.
    /// Uses the same key-value format as the tooltip, plus an OPTIMIZED COORDINATES section.
    /// </summary>
    public static class SummaryWriter
    {
        /// <summary>
        /// Writes the summary file and returns the output path.
        /// </summary>
        public static string Write(string filePath, ParseResult result)
        {
            string outputPath = Path.Combine(
                Path.GetDirectoryName(filePath),
                Path.GetFileNameWithoutExtension(filePath) + "_summary.txt");

            var sb = new StringBuilder();

            // Main summary block (same as tooltip)
            string mainContent = QuantumInfoTipHandler.FormatMultiLine(result.Summary, filePath, forSummary: true);
            sb.Append(mainContent);

            // Coordinates section (not shown in tooltip)
            Molecule mol = result.Molecule;
            if (mol != null && mol.HasGeometry)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("OPTIMIZED COORDINATES");
                sb.AppendLine(new string('─', 42));
                foreach (var atom in mol.Atoms)
                    sb.AppendLine($"{atom.Element,-9}{atom.X,14:F9}  {atom.Y,14:F9}  {atom.Z,14:F9}");
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            return outputPath;
        }
    }
}
