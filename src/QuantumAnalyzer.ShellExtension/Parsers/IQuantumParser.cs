using System.IO;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    public interface IQuantumParser
    {
        /// <summary>Returns true if the first lines indicate this parser can handle the file.</summary>
        bool CanParse(string[] firstLines);

        /// <summary>Full parse from a TextReader (supports both streams and file paths).</summary>
        ParseResult Parse(TextReader reader);
    }
}
