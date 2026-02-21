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
    /// Adds context menu items to quantum chemistry files:
    ///   .log / .out  → "Save Summary"
    ///   .cube        → "Save Visualization"
    /// </summary>
    [ComVisible(true)]
    [Guid("9E6D4567-89AB-4DEF-B0F1-4D5E6F7A8B9C")]
    [DisplayName("QuantumAnalyzer Context Menu")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".log")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".out")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".cube")]
    public class QuantumContextMenuHandler : SharpContextMenu
    {
        protected override bool CanShowMenu()
        {
            try
            {
                string path = FirstSelectedPath();
                if (string.IsNullOrEmpty(path)) return false;
                string ext = Path.GetExtension(path)?.ToLowerInvariant();
                return ext == ".log" || ext == ".out" || ext == ".cube";
            }
            catch
            {
                return false;
            }
        }

        protected override ContextMenuStrip CreateMenu()
        {
            string ext = Path.GetExtension(FirstSelectedPath() ?? string.Empty)?.ToLowerInvariant();
            return ext == ".cube" ? CreateCubeMenu() : CreateSummaryMenu();
        }

        private ContextMenuStrip CreateSummaryMenu()
        {
            var menu = new ContextMenuStrip();
            var item = new ToolStripMenuItem("Save Summary");
            item.Click += OnSaveSummary;
            TrySetIcon(item);
            menu.Items.Add(item);
            return menu;
        }

        private ContextMenuStrip CreateCubeMenu()
        {
            var menu = new ContextMenuStrip();
            var item = new ToolStripMenuItem("Save Visualization");
            item.Click += OnSaveVisualization;
            TrySetIcon(item);
            menu.Items.Add(item);
            return menu;
        }

        private static void TrySetIcon(ToolStripMenuItem item)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuantumAnalyzer.ShellExtension.Resources.save-icon.png"))
                {
                    if (stream != null)
                        using (var fullImg = Image.FromStream(stream))
                            item.Image = ResizeImage(fullImg, 16, 16);
                }
            }
            catch { }
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
                string msg = $"Error saving summary:\n{ex.Message}\n\n{ex.StackTrace}";
                WriteDebugLog("OnSaveSummary ERROR: " + msg);
                MessageBox.Show($"Error saving summary:\n{ex.Message}",
                                "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSaveVisualization(object sender, EventArgs e)
        {
            string path = FirstSelectedPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var result = ParserFactory.TryParse(path);
                if (result?.VolumetricData == null)
                {
                    MessageBox.Show("Could not read volumetric data from the .cube file.",
                                    "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (var dlg = new SaveVisualizationDialog(result.Molecule, result.VolumetricData, path))
                    dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                WriteDebugLog("OnSaveVisualization ERROR: " + ex.ToString());
                MessageBox.Show($"Error opening visualization:\n{ex.Message}",
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
