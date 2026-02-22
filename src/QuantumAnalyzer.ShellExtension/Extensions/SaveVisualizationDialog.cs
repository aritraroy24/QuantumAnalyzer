using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Rendering;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    public class SaveVisualizationDialog : Form
    {
        private Molecule _molecule;
        private readonly VolumetricGrid _grid;
        private readonly string _sourcePath;
        private readonly List<Molecule> _moleculeFrames;
        private readonly List<string> _moleculeFrameNames;
        private int _currentFrameIndex;

        private PictureBox _picture;
        private Label _hintLabel;
        private Panel _bottomPanel;
        private CheckBox _chkPos;
        private CheckBox _chkNeg;
        private TrackBar _isoSlider;
        private Label _isoLabel;
        private Button _bgColorBtn;
        private ComboBox _formatCombo;
        private Button _saveBtn;
        private Button _saveAllBtn;
        private Button _cancelBtn;
        private TrackBar _frameSlider;
        private Label _frameLabel;
        private Label _frameNameLabel;

        private List<MarchingCubes.Triangle> _posTriangles;
        private List<MarchingCubes.Triangle> _negTriangles;
        private float _isovalue = 0.020f;
        private bool _showPos = true;
        private bool _showNeg = true;
        private Color _background = Color.FromArgb(18, 18, 30);
        private float _zoomFactor = 1.0f;
        private float[,] _rotMatrix;

        private bool _isDragging;
        private float[,] _dragBaseMatrix;
        private float[] _dragStartVec;

        private bool HasMultipleFrames => _grid == null && _moleculeFrames != null && _moleculeFrames.Count > 1;

        public SaveVisualizationDialog(
            Molecule molecule,
            VolumetricGrid grid,
            string sourcePath = null,
            List<Molecule> moleculeFrames = null,
            List<string> moleculeFrameNames = null)
        {
            _molecule = molecule;
            _grid = grid;
            _sourcePath = sourcePath;
            _moleculeFrames = moleculeFrames;
            _moleculeFrameNames = moleculeFrameNames;
            _rotMatrix = InitialRotation();

            if ((_molecule == null || !_molecule.HasGeometry) && _moleculeFrames != null && _moleculeFrames.Count > 0)
                _molecule = _moleculeFrames[0];

            Text = "Save Image";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MinimumSize = new Size(400, 380);
            ClientSize = new Size(540, 520);
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(18, 18, 30);

            BuildUI();
            ExtractTriangles();
        }

        private void BuildUI()
        {
            _hintLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "Right-click drag to rotate  |  Scroll to zoom",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(120, 120, 140),
                BackColor = Color.FromArgb(10, 10, 20),
                Font = new Font("Segoe UI", 8f),
            };

            int moleculePanelHeight = HasMultipleFrames ? 72 : 38;
            _bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = _grid != null ? 72 : moleculePanelHeight,
                BackColor = Color.FromArgb(10, 10, 20),
            };
            BuildBottomPanel();

            _picture = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.CenterImage,
                BackColor = _background,
            };
            _picture.MouseDown += OnPictureMouseDown;
            _picture.MouseMove += OnPictureMouseMove;
            _picture.MouseUp += OnPictureMouseUp;
            _picture.MouseEnter += (s, e) => Focus();

            Controls.Add(_picture);
            Controls.Add(_bottomPanel);
            Controls.Add(_hintLabel);
        }

        private void BuildBottomPanel()
        {
            if (_grid != null)
                BuildBottomPanelWithIso();
            else
                BuildBottomPanelMoleculeOnly();
        }

        private void BuildBottomPanelWithIso()
        {
            int x = 6;
            const int row1Y = 5;

            _chkPos = new CheckBox
            {
                Text = "+",
                Checked = true,
                ForeColor = Color.DarkGreen,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(x, row1Y),
            };
            _chkPos.CheckedChanged += OnLobeCheckChanged;
            _bottomPanel.Controls.Add(_chkPos);
            x += _chkPos.PreferredSize.Width + 8;

            _chkNeg = new CheckBox
            {
                Text = "-",
                Checked = true,
                ForeColor = Color.DarkRed,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(x, row1Y),
            };
            _chkNeg.CheckedChanged += OnLobeCheckChanged;
            _bottomPanel.Controls.Add(_chkNeg);
            x += _chkNeg.PreferredSize.Width + 10;

            var isoLbl = new Label
            {
                Text = "Isovalue:",
                AutoSize = true,
                Location = new Point(x, row1Y + 2),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(isoLbl);
            x += isoLbl.PreferredSize.Width + 4;

            _isoSlider = new TrackBar
            {
                Minimum = 1,
                Maximum = 200,
                Value = 20,
                Width = 100,
                Height = 22,
                Location = new Point(x, row1Y - 2),
                TickFrequency = 20,
                LargeChange = 10,
            };
            _isoSlider.Scroll += OnIsoSliderScroll;
            _bottomPanel.Controls.Add(_isoSlider);
            x += _isoSlider.Width + 4;

            _isoLabel = new Label
            {
                Text = "0.020",
                AutoSize = true,
                Location = new Point(x, row1Y + 2),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(_isoLabel);
            x += 52;

            _bgColorBtn = new Button
            {
                Text = "BG",
                Width = 36,
                Height = 22,
                Location = new Point(x, row1Y),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgColorBtn.FlatAppearance.BorderColor = Color.Silver;
            _bgColorBtn.Click += OnBgColorClick;
            _bottomPanel.Controls.Add(_bgColorBtn);

            BuildFormatSaveRow(40);
        }

        private void BuildBottomPanelMoleculeOnly()
        {
            const int rowY = 6;

            _bgColorBtn = new Button
            {
                Text = "BG",
                Width = 36,
                Height = 22,
                Location = new Point(6, rowY),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgColorBtn.FlatAppearance.BorderColor = Color.Silver;
            _bgColorBtn.Click += OnBgColorClick;
            _bottomPanel.Controls.Add(_bgColorBtn);

            if (HasMultipleFrames)
            {
                _frameNameLabel = new Label
                {
                    AutoSize = false,
                    Width = 80,
                    Height = 16,
                    Location = new Point(6, 36),
                    ForeColor = Color.FromArgb(180, 180, 200),
                    Font = new Font("Segoe UI", 8f),
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                _bottomPanel.Controls.Add(_frameNameLabel);

                _frameSlider = new TrackBar
                {
                    Minimum = 0,
                    Maximum = _moleculeFrames.Count - 1,
                    Value = 0,
                    TickStyle = TickStyle.None,
                    Height = 24,
                    Location = new Point(90, 30),
                };
                _frameSlider.Scroll += OnFrameSliderScroll;
                _bottomPanel.Controls.Add(_frameSlider);

                _frameLabel = new Label
                {
                    AutoSize = false,
                    Width = 56,
                    Height = 16,
                    ForeColor = Color.FromArgb(180, 180, 200),
                    Font = new Font("Segoe UI", 8f),
                    TextAlign = ContentAlignment.MiddleRight,
                };
                _bottomPanel.Controls.Add(_frameLabel);

                _bottomPanel.Resize += (s, e) => LayoutFrameControls();
                SetCurrentFrame(0, resetRotation: false);
            }

            BuildFormatSaveRow(rowY);
        }

        private void LayoutFrameControls()
        {
            if (!HasMultipleFrames || _frameSlider == null || _frameLabel == null) return;
            int rightPad = 6;
            int labelWidth = 56;
            int nameWidth = _frameNameLabel != null ? 80 : 0;
            int sliderX = 6 + nameWidth + (nameWidth > 0 ? 4 : 0);
            int sliderWidth = Math.Max(60, _bottomPanel.ClientSize.Width - sliderX - rightPad - labelWidth);
            if (_frameNameLabel != null)
                _frameNameLabel.Location = new Point(6, 36);
            _frameSlider.Location = new Point(sliderX, 30);
            _frameSlider.Width = sliderWidth;
            _frameLabel.Location = new Point(sliderX + sliderWidth, 36);
        }

        private void BuildFormatSaveRow(int rowY)
        {
            int fmtX = _grid == null ? 50 : 6;

            var fmtLbl = new Label
            {
                Text = "Format:",
                AutoSize = true,
                Location = new Point(fmtX, rowY + 4),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(fmtLbl);

            _formatCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(fmtX + 56, rowY + 2),
                Width = 72,
            };
            _formatCombo.Items.AddRange(new object[] { "PNG", "TIFF", "JPEG" });
            _formatCombo.SelectedIndex = 0;
            _bottomPanel.Controls.Add(_formatCombo);

            _saveBtn = new Button
            {
                Text = "Save Image",
                Width = 90,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
            };
            _saveBtn.FlatAppearance.BorderColor = Color.SteelBlue;
            _saveBtn.ForeColor = Color.SteelBlue;
            _saveBtn.Click += OnSaveClick;
            AcceptButton = _saveBtn;
            _bottomPanel.Controls.Add(_saveBtn);

            if (HasMultipleFrames)
            {
                _saveAllBtn = new Button
                {
                    Text = "Save All Images",
                    Width = 110,
                    Height = 26,
                    FlatStyle = FlatStyle.Flat,
                    Anchor = AnchorStyles.Right | AnchorStyles.Top,
                };
                _saveAllBtn.FlatAppearance.BorderColor = Color.SteelBlue;
                _saveAllBtn.ForeColor = Color.SteelBlue;
                _saveAllBtn.Click += OnSaveAllClick;
                _bottomPanel.Controls.Add(_saveAllBtn);
            }

            _cancelBtn = new Button
            {
                Text = "Cancel",
                Width = 76,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
            };
            _cancelBtn.FlatAppearance.BorderColor = Color.Silver;
            _cancelBtn.ForeColor = Color.FromArgb(180, 180, 200);
            _cancelBtn.Click += (s, e) => Close();
            CancelButton = _cancelBtn;
            _bottomPanel.Controls.Add(_cancelBtn);

            Action layoutButtons = () =>
            {
                int w = _bottomPanel.ClientSize.Width;
                _cancelBtn.Location = new Point(w - _cancelBtn.Width - 6, rowY);

                if (_saveAllBtn != null)
                {
                    _saveAllBtn.Location = new Point(_cancelBtn.Left - _saveAllBtn.Width - 6, rowY);
                    _saveBtn.Location = new Point(_saveAllBtn.Left - _saveBtn.Width - 6, rowY);
                }
                else
                {
                    _saveBtn.Location = new Point(_cancelBtn.Left - _saveBtn.Width - 6, rowY);
                }

                LayoutFrameControls();
            };

            layoutButtons();
            _bottomPanel.Resize += (s, e) => layoutButtons();
        }

        private void SetCurrentFrame(int index, bool resetRotation)
        {
            if (!HasMultipleFrames) return;
            if (index < 0) index = 0;
            if (index >= _moleculeFrames.Count) index = _moleculeFrames.Count - 1;

            _currentFrameIndex = index;
            _molecule = _moleculeFrames[index];

            if (_frameSlider != null && _frameSlider.Value != index)
                _frameSlider.Value = index;

            if (_frameLabel != null)
                _frameLabel.Text = (index + 1).ToString() + "/" + _moleculeFrames.Count;

            if (_frameNameLabel != null)
            {
                string name = GetFrameName(index);
                _frameNameLabel.Text = string.IsNullOrEmpty(name) ? "Frame" : name;
            }

            if (resetRotation)
                _rotMatrix = InitialRotation();

            RenderPreview();
        }

        private string GetFrameName(int index)
        {
            if (_moleculeFrameNames == null || index < 0 || index >= _moleculeFrameNames.Count)
                return null;
            string n = _moleculeFrameNames[index];
            return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
        }

        private void LayoutPicture()
        {
            int top = _hintLabel.Height;
            int h = Math.Max(ClientSize.Height - _hintLabel.Height - _bottomPanel.Height, 1);
            _picture.SetBounds(0, top, ClientSize.Width, h);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutPicture();
            LayoutFrameControls();
            RenderPreview();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_picture != null)
            {
                LayoutPicture();
                RenderPreview();
            }
        }

        private void RenderPreview()
        {
            if (_picture == null) return;
            if (_molecule == null || !_molecule.HasGeometry) return;
            if (_picture.Width < 10 || _picture.Height < 10) return;

            int w = _picture.Width;
            int h = _picture.Height;

            try
            {
                var pos = _showPos ? _posTriangles : null;
                var neg = _showNeg ? _negTriangles : null;
                var bmp = IsosurfaceRenderer.Render(_molecule, pos, neg, _rotMatrix, w, h, _background, false, _zoomFactor);
                var old = _picture.Image;
                _picture.Image = bmp;
                old?.Dispose();
            }
            catch
            {
            }
        }

        private void ExtractTriangles()
        {
            if (_grid == null)
            {
                _posTriangles = null;
                _negTriangles = null;
                return;
            }

            try
            {
                _posTriangles = _showPos ? MarchingCubes.Extract(_grid, _isovalue, true) : null;
                _negTriangles = _showNeg ? MarchingCubes.Extract(_grid, _isovalue, false) : null;
            }
            catch
            {
                _posTriangles = null;
                _negTriangles = null;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _zoomFactor *= e.Delta > 0 ? 1.1f : (1f / 1.1f);
            if (_zoomFactor < 0.2f) _zoomFactor = 0.2f;
            if (_zoomFactor > 5.0f) _zoomFactor = 5.0f;
            RenderPreview();
        }

        private void OnPictureMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            _isDragging = true;
            _dragBaseMatrix = (float[,])_rotMatrix.Clone();
            _dragStartVec = ArcballVec(e.X, e.Y);
            _picture.Cursor = Cursors.SizeAll;
        }

        private void OnPictureMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            float[] cur = ArcballVec(e.X, e.Y);
            float[,] delta = ArcballRotation(_dragStartVec, cur);
            _rotMatrix = MatMul3(delta, _dragBaseMatrix);
            RenderPreview();
        }

        private void OnPictureMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !_isDragging) return;
            _isDragging = false;
            _picture.Cursor = Cursors.Default;
            RenderPreview();
        }

        private void OnFrameSliderScroll(object sender, EventArgs e)
        {
            if (_frameSlider == null) return;
            SetCurrentFrame(_frameSlider.Value, resetRotation: false);
        }

        private void OnLobeCheckChanged(object sender, EventArgs e)
        {
            bool newPos = _chkPos.Checked;
            bool newNeg = _chkNeg.Checked;
            bool needExtract = (newPos != _showPos && newPos && _posTriangles == null)
                            || (newNeg != _showNeg && newNeg && _negTriangles == null);

            _showPos = newPos;
            _showNeg = newNeg;
            if (needExtract) ExtractTriangles();
            RenderPreview();
        }

        private void OnIsoSliderScroll(object sender, EventArgs e)
        {
            _isovalue = _isoSlider.Value / 1000f;
            _isoLabel.Text = _isovalue.ToString("0.000");
            ExtractTriangles();
            RenderPreview();
        }

        private void OnBgColorClick(object sender, EventArgs e)
        {
            using (var dlg = new ColorDialog { Color = _background, FullOpen = true })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    ApplyBackground(dlg.Color);
            }
        }

        private void ApplyBackground(Color bg)
        {
            _background = bg;
            _picture.BackColor = bg;

            float lum = 0.299f * bg.R + 0.587f * bg.G + 0.114f * bg.B;
            Color textColor = lum > 128f ? Color.Black : Color.White;
            Color panelBg = lum > 128f ? Color.FromArgb(220, 220, 225) : Color.FromArgb(10, 10, 20);

            _hintLabel.ForeColor = lum > 128f ? Color.FromArgb(80, 80, 100) : Color.FromArgb(120, 120, 140);
            _hintLabel.BackColor = panelBg;
            _bottomPanel.BackColor = panelBg;

            foreach (Control c in _bottomPanel.Controls)
            {
                if (c is Label lbl) lbl.ForeColor = textColor;
                if (c is Button btn && btn != _saveBtn && btn != _saveAllBtn) btn.ForeColor = textColor;
            }

            RenderPreview();
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            string fmt = _formatCombo.SelectedItem?.ToString() ?? "PNG";
            ImageFormat imgFmt = fmt == "TIFF" ? ImageFormat.Tiff : fmt == "JPEG" ? ImageFormat.Jpeg : ImageFormat.Png;
            string ext = fmt == "TIFF" ? ".tiff" : fmt == "JPEG" ? ".jpg" : ".png";

            string defaultBaseName = string.IsNullOrEmpty(_sourcePath)
                ? "visualization"
                : Path.GetFileNameWithoutExtension(_sourcePath);
            string defaultDir = string.IsNullOrEmpty(_sourcePath)
                ? ""
                : Path.GetDirectoryName(_sourcePath);
            string filter = fmt == "TIFF" ? "TIFF Image (*.tiff)|*.tiff|All Files (*.*)|*.*"
                          : fmt == "JPEG" ? "JPEG Image (*.jpg)|*.jpg|All Files (*.*)|*.*"
                          : "PNG Image (*.png)|*.png|All Files (*.*)|*.*";

            using (var dlg = new SaveFileDialog
            {
                Title = "Save Image",
                Filter = filter,
                DefaultExt = ext.TrimStart('.'),
                FileName = defaultBaseName,
                InitialDirectory = defaultDir,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    var pos = _showPos ? _posTriangles : null;
                    var neg = _showNeg ? _negTriangles : null;
                    using (var bmp = IsosurfaceRenderer.Render(_molecule, pos, neg, _rotMatrix, 1200, 900, _background, false, _zoomFactor))
                    {
                        bmp.Save(dlg.FileName, imgFmt);
                    }

                    MessageBox.Show("Image saved to:\n" + dlg.FileName,
                        "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error saving visualization:\n" + ex.Message,
                        "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnSaveAllClick(object sender, EventArgs e)
        {
            string fmt = _formatCombo.SelectedItem?.ToString() ?? "PNG";
            ImageFormat imgFmt = fmt == "TIFF" ? ImageFormat.Tiff : fmt == "JPEG" ? ImageFormat.Jpeg : ImageFormat.Png;
            string ext = fmt == "TIFF" ? ".tiff" : fmt == "JPEG" ? ".jpg" : ".png";

            string defaultBaseName = string.IsNullOrEmpty(_sourcePath)
                ? "visualization"
                : Path.GetFileNameWithoutExtension(_sourcePath);
            string defaultDir = string.IsNullOrEmpty(_sourcePath)
                ? ""
                : Path.GetDirectoryName(_sourcePath);

            SaveAllFrames(imgFmt, ext, defaultBaseName, defaultDir);
        }
        private void SaveAllFrames(ImageFormat imgFmt, string ext, string defaultBaseName, string defaultDir)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select folder for frame exports";
                if (!string.IsNullOrEmpty(defaultDir) && Directory.Exists(defaultDir))
                    dlg.SelectedPath = defaultDir;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int saved = 0;

                    for (int i = 0; i < _moleculeFrames.Count; i++)
                    {
                        Molecule mol = _moleculeFrames[i];
                        if (mol == null || !mol.HasGeometry) continue;

                        string baseName = GetFrameOutputBaseName(i, defaultBaseName);
                        string fileName = EnsureUniqueFileName(baseName, i, used) + ext;
                        string fullPath = Path.Combine(dlg.SelectedPath, fileName);

                        using (var bmp = IsosurfaceRenderer.Render(mol, null, null, _rotMatrix, 1200, 900, _background, false, _zoomFactor))
                        {
                            bmp.Save(fullPath, imgFmt);
                        }
                        saved++;
                    }

                    MessageBox.Show(saved + " images saved to:\n" + dlg.SelectedPath,
                        "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error saving frame images:\n" + ex.Message,
                        "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string GetFrameOutputBaseName(int index, string defaultBaseName)
        {
            string frameName = GetFrameName(index);
            if (!string.IsNullOrWhiteSpace(frameName))
            {
                string safe = SanitizeFileName(frameName.Trim());
                if (!string.IsNullOrWhiteSpace(safe)) return safe;
            }

            return defaultBaseName + "_" + index;
        }

        private static string EnsureUniqueFileName(string baseName, int index, HashSet<string> used)
        {
            string candidate = baseName;
            if (used.Add(candidate)) return candidate;

            candidate = baseName + "_" + index;
            if (used.Add(candidate)) return candidate;

            int n = 1;
            while (!used.Add(candidate + "_" + n)) n++;
            return candidate + "_" + n;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }
            return new string(chars).Trim();
        }

        private float[] ArcballVec(int px, int py)
        {
            float cx = _picture.Width / 2f;
            float cy = _picture.Height / 2f;
            float r = Math.Min(_picture.Width, _picture.Height) / 2f * 0.85f;
            if (r < 1f) r = 1f;

            float x = (px - cx) / r;
            float y = (cy - py) / r;
            float d2 = x * x + y * y;
            if (d2 <= 1f)
                return new[] { x, y, (float)Math.Sqrt(1f - d2) };

            float len = (float)Math.Sqrt(d2);
            return new[] { x / len, y / len, 0f };
        }

        private static float[,] ArcballRotation(float[] from, float[] to)
        {
            float dot = from[0] * to[0] + from[1] * to[1] + from[2] * to[2];
            dot = Math.Max(-1f, Math.Min(1f, dot));

            float kx = from[1] * to[2] - from[2] * to[1];
            float ky = from[2] * to[0] - from[0] * to[2];
            float kz = from[0] * to[1] - from[1] * to[0];
            float kLen = (float)Math.Sqrt(kx * kx + ky * ky + kz * kz);
            if (kLen < 1e-6f) return Identity3();

            kx /= kLen;
            ky /= kLen;
            kz /= kLen;

            float angle = (float)Math.Acos(dot);
            float c = (float)Math.Cos(angle);
            float s = (float)Math.Sin(angle);
            float t = 1f - c;

            return new float[,]
            {
                { t*kx*kx + c,    t*kx*ky - s*kz, t*kx*kz + s*ky },
                { t*kx*ky + s*kz, t*ky*ky + c,    t*ky*kz - s*kx },
                { t*kx*kz - s*ky, t*ky*kz + s*kx, t*kz*kz + c    },
            };
        }

        private static float[,] MatMul3(float[,] A, float[,] B)
        {
            var C = new float[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        C[i, j] += A[i, k] * B[k, j];
            return C;
        }

        private static float[,] Identity3()
        {
            var I = new float[3, 3];
            I[0, 0] = I[1, 1] = I[2, 2] = 1f;
            return I;
        }

        private static float[,] InitialRotation()
        {
            float rx = 8f * (float)(Math.PI / 180.0);
            float c = (float)Math.Cos(rx);
            float s = (float)Math.Sin(rx);
            return new float[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _picture?.Image?.Dispose();
                _picture?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}





