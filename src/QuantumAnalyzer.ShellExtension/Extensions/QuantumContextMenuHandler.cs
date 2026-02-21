using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SharpShell.Attributes;
using SharpShell.SharpContextMenu;
using QuantumAnalyzer.ShellExtension.Parsers;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Adds a right-click "Save Summary" menu item to Gaussian/ORCA output files (.log, .out).
    /// The menu only appears when the file is recognised as a quantum chemistry output.
    /// </summary>
    [ComVisible(true)]
    [Guid("9E6D4567-89AB-4DEF-B0F1-4D5E6F7A8B9C")]
    [DisplayName("QuantumAnalyzer Context Menu")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".log")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".out")]
    public class QuantumContextMenuHandler : SharpContextMenu
    {
        protected override bool CanShowMenu()
        {
            // Keep this as lightweight as possible — avoid any file I/O here.
            // SharpShell may call CanShowMenu() before Initialize() has populated
            // SelectedItemPaths, so guard against null/empty paths gracefully.
            // The actual quantum-file check happens inside OnSaveSummary, which
            // already shows a friendly error if the file isn't recognised.
            try
            {
                string path = FirstSelectedPath();
                if (string.IsNullOrEmpty(path)) return false;
                string ext = Path.GetExtension(path)?.ToLowerInvariant();
                return ext == ".log" || ext == ".out";
            }
            catch
            {
                return false;
            }
        }

        protected override ContextMenuStrip CreateMenu()
        {
            var menu = new ContextMenuStrip();
            var item = new ToolStripMenuItem("Save Summary");
            item.Click += OnSaveSummary;

            // Set icon from embedded resource (resized to 16x16)
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuantumAnalyzer.ShellExtension.Resources.save-icon.png"))
                {
                    if (stream != null)
                    {
                        using (var fullImg = Image.FromStream(stream))
                        {
                            item.Image = ResizeImage(fullImg, 16, 16);
                        }
                    }
                }
            }
            catch { }

            menu.Items.Add(item);
            return menu;
        }

        // ──────────────────────────────────────────────────────────────────

        private static Image ResizeImage(Image img, int width, int height)
        {
            var res = new Bitmap(width, height);
            using (var g = Graphics.FromImage(res))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, width, height);
            }
            return res;
        }

        private void OnSaveSummary(object sender, EventArgs e)
        {
            string path = FirstSelectedPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var result = ParserFactory.TryParse(path);
                if (result == null)
                {
                    MessageBox.Show("Could not parse the file as a Gaussian or ORCA output.",
                                    "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string outPath = SummaryWriter.Write(path, result);

                MessageBox.Show($"Summary saved to:\n{outPath}",
                                "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                // Show the full error (including stack trace) and write to log file for diagnosis
                string msg = $"Error saving summary:\n{ex.Message}\n\n{ex.StackTrace}";
                WriteDebugLog("OnSaveSummary ERROR: " + msg);
                MessageBox.Show($"Error saving summary:\n{ex.Message}",
                                "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Appends a timestamped line to %TEMP%\QuantumAnalyzer_debug.log.
        /// Safe to call from any thread; ignores I/O errors.
        /// </summary>
        internal static void WriteDebugLog(string message)
        {
            try
            {
                string logPath = Path.Combine(Path.GetTempPath(), "QuantumAnalyzer_debug.log");
                string entry   = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, entry, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        private string FirstSelectedPath()
        {
            foreach (string p in SelectedItemPaths)
                return p;
            return null;
        }
    }
}
