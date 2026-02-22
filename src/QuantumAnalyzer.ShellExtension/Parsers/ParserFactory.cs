using System;
using System.Collections.Generic;
using System.IO;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Parsers
{
    public static class ParserFactory
    {
        private static readonly IQuantumParser[] Parsers = new IQuantumParser[]
        {
            new GaussianParser(),
            new OrcaParser(),
            new GjfParser(),
            new OrcaInputParser(),
            new PoscarParser(),
            new XyzParser(),
            new CubeParser(),
            new ChgcarParser(),
            new OutcarParser(),
        };

        /// <summary>
        /// Reads the first N lines from the stream, detects the software type,
        /// then parses the full stream. Returns null if not a recognised format.
        /// The stream is rewound to position 0 before full parsing.
        /// </summary>
        public static ParseResult TryParse(Stream stream)
        {
            try
            {
                if (stream == null || !stream.CanRead) return null;

                // Read first 30 lines for detection
                string[] firstLines = ReadFirstLines(stream, 30);

                IQuantumParser parser = FindParser(firstLines);
                if (parser == null) return null;

                // Rewind for full parse
                if (stream.CanSeek)
                    stream.Seek(0, SeekOrigin.Begin);
                else
                    return null; // can't rewind — can't parse

                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, true))
                    return parser.Parse(reader);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Parse from a file path (preferred when file path is available).</summary>
        public static ParseResult TryParse(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

                // CHGCAR and OUTCAR have no extension; detect by filename before content
                // scanning. CHGCAR: volumetric section falls outside the 30-line window.
                // OUTCAR: arbitrary length with final geometry at the end.
                string fname = Path.GetFileName(filePath).ToUpperInvariant();
                if (fname == "CHGCAR")
                {
                    try
                    {
                        using (var reader = new StreamReader(filePath))
                        {
                            var r = new ChgcarParser().Parse(reader);
                            if (r != null) return r;
                        }
                    }
                    catch { }
                }
                if (fname == "OUTCAR")
                {
                    try
                    {
                        using (var reader = new StreamReader(filePath))
                        {
                            var r = new OutcarParser().Parse(reader);
                            if (r != null) return r;
                        }
                    }
                    catch { }
                }

                string[] firstLines = ReadFirstLines(filePath, 30);
                IQuantumParser parser = FindParser(firstLines);
                if (parser == null) return null;

                using (var reader = new StreamReader(filePath))
                    return parser.Parse(reader);
            }
            catch
            {
                return null;
            }
        }

        // ──────────────────────────────────────────────────────────────

        private static IQuantumParser FindParser(string[] firstLines)
        {
            foreach (var p in Parsers)
            {
                if (p.CanParse(firstLines)) return p;
            }
            return null;
        }

        private static string[] ReadFirstLines(Stream stream, int count)
        {
            var lines = new List<string>();
            // Temporary reader — do NOT dispose the stream
            var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, true);
            for (int i = 0; i < count; i++)
            {
                string line = reader.ReadLine();
                if (line == null) break;
                lines.Add(line);
            }
            return lines.ToArray();
        }

        private static string[] ReadFirstLines(string filePath, int count)
        {
            var lines = new List<string>();
            using (var reader = new StreamReader(filePath))
            {
                for (int i = 0; i < count; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }
    }
}
