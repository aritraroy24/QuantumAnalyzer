using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using QuantumAnalyzer.ShellExtension.Models;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Modal dialog for saving the OUTCAR energy convergence profile as an image.
    /// The graph is interactive: click any point to select it and show the energy annotation.
    /// Uses Paint-event rendering so the graph always fills the panel at the correct size.
    /// </summary>
    public class SaveEnergyProfileDialog : Form
    {
        private readonly PictureBox _graphPicture;
        private readonly Button     _bgButton;
        private readonly ComboBox   _formatCombo;
        private readonly Button     _saveButton;
        private readonly Button     _cancelButton;
        private readonly Label      _stepInfoLabel;

        private readonly OutcarData _outcarData;
        private readonly string _energyUnit;
        private int   _selectedStep;
        private Color _background = Color.FromArgb(18, 18, 30);

        public SaveEnergyProfileDialog(
            OutcarData outcarData,
            int initialSelectedStep,
            string energyUnit = "eV",
            string title = "Save Energy Profile")
        {
            _outcarData   = outcarData;
            _energyUnit   = string.IsNullOrWhiteSpace(energyUnit) ? "eV" : energyUnit;
            _selectedStep = Math.Max(0, Math.Min(initialSelectedStep,
                (outcarData?.StepEnergies?.Count ?? 1) - 1));

            Text            = title;
            Size            = new Size(920, 560);
            MinimumSize     = new Size(600, 420);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            BackColor       = Color.FromArgb(18, 18, 30);

            // ── Graph panel — Paint event draws at current size, always correct ─
            _graphPicture = new PictureBox
            {
                Dock      = DockStyle.Fill,
                BackColor = _background,
                Cursor    = Cursors.Hand,
            };
            // Paint renders directly into the control at whatever size it currently is.
            _graphPicture.Paint     += OnGraphPaint;
            _graphPicture.MouseDown += OnGraphClick;
            Controls.Add(_graphPicture);

            // ── Bottom panel ──────────────────────────────────────────────────
            var bottomPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 44,
                BackColor = Color.FromArgb(10, 10, 20),
            };

            _bgButton = new Button
            {
                Text      = "BG Color",
                Width     = 72,
                Height    = 26,
                Location  = new Point(6, 9),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgButton.FlatAppearance.BorderColor = Color.Silver;
            _bgButton.Click += OnBgClick;
            bottomPanel.Controls.Add(_bgButton);

            var formatLabel = new Label
            {
                Text      = "Format:",
                AutoSize  = true,
                Location  = new Point(88, 14),
                ForeColor = Color.FromArgb(150, 150, 170),
                Font      = new Font("Segoe UI", 8f),
            };
            bottomPanel.Controls.Add(formatLabel);

            _formatCombo = new ComboBox
            {
                Width         = 80,
                Location      = new Point(138, 10),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font          = new Font("Segoe UI", 8f),
                FlatStyle     = FlatStyle.Flat,
            };
            _formatCombo.Items.AddRange(new object[] { "PNG", "TIFF", "JPEG" });
            _formatCombo.SelectedIndex = 0;
            bottomPanel.Controls.Add(_formatCombo);

            // Step info label (shows selected step + energy after a click)
            _stepInfoLabel = new Label
            {
                Text      = "",
                AutoSize  = false,
                Width     = 300,
                Height    = 26,
                Location  = new Point(240, 9),
                ForeColor = Color.FromArgb(160, 200, 200),
                Font      = new Font("Segoe UI", 8f),
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            };
            bottomPanel.Controls.Add(_stepInfoLabel);

            // Close / Save buttons (right-anchored via Resize handler)
            _cancelButton = new Button
            {
                Text      = "Close",
                Width     = 70,
                Height    = 26,
                Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                Location  = new Point(840, 9),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _cancelButton.FlatAppearance.BorderColor = Color.Gray;
            _cancelButton.Click += (s, e) => Close();
            bottomPanel.Controls.Add(_cancelButton);

            _saveButton = new Button
            {
                Text      = "Save Image",
                Width     = 88,
                Height    = 26,
                Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                Location  = new Point(744, 9),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(160, 210, 160),
            };
            _saveButton.FlatAppearance.BorderColor = Color.FromArgb(80, 150, 80);
            _saveButton.Click += OnSaveClick;
            bottomPanel.Controls.Add(_saveButton);

            Controls.Add(bottomPanel);

            bottomPanel.Resize += (s, e) =>
            {
                int w = bottomPanel.Width;
                _cancelButton.Left = w - _cancelButton.Width - 6;
                _saveButton.Left   = _cancelButton.Left - _saveButton.Width - 6;
            };

            UpdateStepInfo();
        }

        // ── Graph rendering via Paint event ───────────────────────────────────
        // Paint is called by WinForms at the correct control size every time
        // the control is shown, resized, or invalidated.

        private void OnGraphPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_background);
            if (_outcarData?.StepEnergies?.Count > 0)
                OutcarPreviewControl.DrawEnergyGraph(
                    g, _graphPicture.Width, _graphPicture.Height,
                    _outcarData, _selectedStep, _background, _energyUnit);
        }

        private void RefreshGraph() => _graphPicture.Invalidate();

        private void UpdateStepInfo()
        {
            if (_outcarData?.StepEnergies == null || _outcarData.StepEnergies.Count == 0)
            {
                _stepInfoLabel.Text = "";
                return;
            }
            int n   = _outcarData.StepEnergies.Count;
            int sel = Math.Max(0, Math.Min(_selectedStep, n - 1));
            _stepInfoLabel.Text = $"Step {sel + 1} / {n}   E = {_outcarData.StepEnergies[sel]:F6} {_energyUnit}";
        }

        // ── Graph click ───────────────────────────────────────────────────────

        private void OnGraphClick(object sender, MouseEventArgs e)
        {
            if (_outcarData?.StepEnergies == null) return;
            int n = _outcarData.StepEnergies.Count;
            if (n < 2) return;

            const int ml = 62, mr = 24;
            int chartW = _graphPicture.Width - ml - mr;
            if (chartW <= 0) return;

            float relX = e.X - ml;
            int   step = (int)Math.Round(relX / chartW * (n - 1));
            _selectedStep = Math.Max(0, Math.Min(n - 1, step));
            UpdateStepInfo();
            RefreshGraph();
        }

        // ── Background ────────────────────────────────────────────────────────

        private void OnBgClick(object sender, EventArgs e)
        {
            using (var dlg = new ColorDialog { Color = _background, FullOpen = true })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                _background             = dlg.Color;
                _graphPicture.BackColor = _background;
                BackColor               = _background;
                RefreshGraph();
            }
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private void OnSaveClick(object sender, EventArgs e)
        {
            string fmt    = _formatCombo.SelectedItem?.ToString() ?? "PNG";
            string filter = fmt == "TIFF" ? "TIFF Image|*.tiff;*.tif"
                          : fmt == "JPEG" ? "JPEG Image|*.jpg;*.jpeg"
                          :                 "PNG Image|*.png";

            using (var dlg = new SaveFileDialog
            {
                Title    = "Save Energy Profile",
                Filter   = filter,
                FileName = "energy_profile",
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                const int saveW = 1400, saveH = 600;
                using (var bmp = new Bitmap(saveW, saveH))
                using (var g   = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(_background);
                    if (_outcarData?.StepEnergies?.Count > 0)
                        OutcarPreviewControl.DrawEnergyGraph(
                            g, saveW, saveH, _outcarData, _selectedStep, _background, _energyUnit);

                    ImageFormat imgFmt = fmt == "TIFF" ? ImageFormat.Tiff
                                       : fmt == "JPEG" ? ImageFormat.Jpeg
                                       :                 ImageFormat.Png;
                    bmp.Save(dlg.FileName, imgFmt);
                }

                MessageBox.Show($"Energy profile saved to:\n{dlg.FileName}",
                    "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
