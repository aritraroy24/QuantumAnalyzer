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
    /// Adds a "QuantumAnalyzer ▶" submenu to quantum chemistry files:
    ///   .log / .out  → "Save Summary" + "Save Image"
    ///   .cube        → "Save Image"  (with isosurface)
    ///   .xyz         → "Save Image"  (molecule-only)
    /// </summary>
    [ComVisible(true)]
    [Guid("9E6D4567-89AB-4DEF-B0F1-4D5E6F7A8B9C")]
    [DisplayName("QuantumAnalyzer Context Menu")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".log")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".out")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".cube")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".xyz")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".poscar")]
    [COMServerAssociation(AssociationType.ClassOfExtension, ".contcar")]
    public class QuantumContextMenuHandler : SharpContextMenu
    {
        protected override bool CanShowMenu()
        {
            try
            {
                string path = FirstSelectedPath();
                if (string.IsNullOrEmpty(path)) return false;
                string ext = Path.GetExtension(path)?.ToLowerInvariant();
                return ext == ".log" || ext == ".out" || ext == ".cube" || ext == ".xyz"
                    || ext == ".poscar" || ext == ".contcar"
                    || (string.IsNullOrEmpty(ext) && IsPoscarFilename(path))
                    || (string.IsNullOrEmpty(ext) && IsChgcarFilename(path));
            }
            catch
            {
                return false;
            }
        }

        protected override ContextMenuStrip CreateMenu()
        {
            string selectedPath = FirstSelectedPath() ?? string.Empty;
            string ext = Path.GetExtension(selectedPath)?.ToLowerInvariant();
            // Treat extensionless POSCAR/CONTCAR filenames as crystal files
            bool isCrystalFile = ext == ".poscar" || ext == ".contcar"
                || (string.IsNullOrEmpty(ext) && IsPoscarFilename(selectedPath));
            bool isChgcarFile = string.IsNullOrEmpty(ext) && IsChgcarFilename(selectedPath);

            var menu = new ContextMenuStrip();
            var qa   = new ToolStripMenuItem("QuantumAnalyzer");
            TrySetIcon(qa);

            if (ext == ".cube")
            {
                // Cube: Save Image with isosurface controls
                var saveImg = new ToolStripMenuItem("Save Image");
                saveImg.Click += OnSaveVisualization;
                TrySetIcon(saveImg);
                qa.DropDownItems.Add(saveImg);
            }
            else if (isChgcarFile)
            {
                // CHGCAR: Save Image (crystal + isosurface)
                var saveImg = new ToolStripMenuItem("Save Image");
                saveImg.Click += OnSaveChgcarImage;
                TrySetIcon(saveImg);
                qa.DropDownItems.Add(saveImg);
            }
            else if (isCrystalFile)
            {
                // POSCAR/CONTCAR: Save Image (crystal structure)
                var saveImg = new ToolStripMenuItem("Save Image");
                saveImg.Click += OnSaveCrystalImage;
                TrySetIcon(saveImg);
                qa.DropDownItems.Add(saveImg);
            }
            else if (ext == ".xyz")
            {
                // XYZ: Save Image only (molecule-only, no summary)
                var saveImg = new ToolStripMenuItem("Save Image");
                saveImg.Click += OnSaveImage;
                TrySetIcon(saveImg);
                qa.DropDownItems.Add(saveImg);
            }
            else
            {
                // .log / .out: Save Summary + Save Image
                var saveSummary = new ToolStripMenuItem("Save Summary");
                saveSummary.Click += OnSaveSummary;
                TrySetIcon(saveSummary);
                qa.DropDownItems.Add(saveSummary);

                var saveImage = new ToolStripMenuItem("Save Image");
                saveImage.Click += OnSaveImage;
                TrySetIcon(saveImage);
                qa.DropDownItems.Add(saveImage);
            }

            menu.Items.Add(qa);
            return menu;
        }

        private static void TrySetIcon(ToolStripMenuItem item)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuantumAnalyzer.ShellExtension.Resources.qa-icon.png"))
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

        private void OnSaveImage(object sender, EventArgs e)
        {
            string path = FirstSelectedPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var result = ParserFactory.TryParse(path);
                if (result?.Molecule == null || !result.Molecule.HasGeometry)
                {
                    MessageBox.Show("Could not read molecule geometry from the file.",
                                    "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Pass null grid → molecule-only mode (no isovalue row in dialog)
                using (var dlg = new SaveVisualizationDialog(result.Molecule, null, path))
                    dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                WriteDebugLog("OnSaveImage ERROR: " + ex.ToString());
                MessageBox.Show($"Error opening save dialog:\n{ex.Message}",
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

        private void OnSaveChgcarImage(object sender, EventArgs e)
        {
            string path = FirstSelectedPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var result = ParserFactory.TryParse(path);
                if (result?.VolumetricData == null || result?.CrystalData == null)
                {
                    MessageBox.Show("Could not read CHGCAR data from the file.",
                                    "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (var dlg = new SaveChgcarDialog(result.Molecule, result.VolumetricData, result.CrystalData))
                    dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                WriteDebugLog("OnSaveChgcarImage ERROR: " + ex.ToString());
                MessageBox.Show($"Error opening save dialog:\n{ex.Message}",
                                "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSaveCrystalImage(object sender, EventArgs e)
        {
            string path = FirstSelectedPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var result = ParserFactory.TryParse(path);
                if (result?.CrystalData == null)
                {
                    MessageBox.Show("Could not read crystal structure from the file.",
                                    "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (var dlg = new SaveCrystalDialog(result.Molecule, result.CrystalData))
                    dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                WriteDebugLog("OnSaveCrystalImage ERROR: " + ex.ToString());
                MessageBox.Show($"Error opening save dialog:\n{ex.Message}",
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

        /// <summary>
        /// Returns true when the filename (without path) is "POSCAR" or "CONTCAR",
        /// case-insensitive. Used to handle VASP files that have no file extension.
        /// </summary>
        private static bool IsPoscarFilename(string path)
        {
            string name = Path.GetFileName(path).ToUpperInvariant();
            return name == "POSCAR" || name == "CONTCAR";
        }

        /// <summary>
        /// Returns true when the filename (without path) is "CHGCAR", case-insensitive.
        /// </summary>
        private static bool IsChgcarFilename(string path)
        {
            return Path.GetFileName(path).ToUpperInvariant() == "CHGCAR";
        }
    }
}
