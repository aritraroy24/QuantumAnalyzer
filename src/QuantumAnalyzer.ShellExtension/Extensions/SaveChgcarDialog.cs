using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Rendering;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Modal dialog for exporting a CHGCAR visualization (crystal structure + charge-density isosurface).
    /// Contains an interactive preview (right-click drag = rotate, scroll = zoom).
    /// </summary>
    public class SaveChgcarDialog : Form
    {
        // ── Data ──────────────────────────────────────────────────────────────
        private readonly Molecule       _unitCellMolecule;
        private readonly VolumetricGrid _grid;
        private readonly LatticeCell    _crystal;

        // ── Widgets ───────────────────────────────────────────────────────────
        private PictureBox _picture;
        private Label      _hintLabel;
        private Panel      _bottomPanel;

        // Row 1 — isovalue
        private CheckBox _chkIso;
        private TrackBar _isoSlider;
        private Label    _isoLabel;
        private Button   _bgColorBtn;

        // Row 2 — crystal
        private CheckBox   _chkUnitCell;
        private CheckBox   _chkVectors;
        private readonly Label[]  _supercellCountLabels = new Label[3];
        private readonly Button[] _btnMinus             = new Button[3];
        private readonly Button[] _btnPlus              = new Button[3];

        // Row 3 — save
        private ComboBox _formatCombo;
        private Button   _saveBtn;
        private Button   _cancelBtn;

        // ── State ─────────────────────────────────────────────────────────────
        private Molecule    _expandedMolecule;
        private int[]       _supercell    = new[] { 1, 1, 1 };
        private bool        _showUnitCell = true;
        private bool        _showVectors  = true;
        private bool        _showIso      = true;
        private float       _isovalue     = 1.0f;
        private Color       _background   = Color.FromArgb(18, 18, 30);
        private float       _zoomFactor   = 1.0f;
        private float[,]    _rotMatrix;

        private List<MarchingCubes.Triangle> _isoTriangles;

        // ── Arcball drag ──────────────────────────────────────────────────────
        private bool     _isDragging;
        private float[,] _dragBaseMatrix;
        private float[]  _dragStartVec;

        // ── Constructor ───────────────────────────────────────────────────────

        public SaveChgcarDialog(Molecule mol, VolumetricGrid grid, LatticeCell crystal)
        {
            _unitCellMolecule = mol;
            _grid             = grid;
            _crystal          = crystal;
            _expandedMolecule = mol;
            _rotMatrix        = InitialRotation();

            Text            = "Save Image — CHGCAR";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterScreen;
            MinimizeBox     = false;
            MinimumSize     = new Size(480, 450);
            ClientSize      = new Size(620, 590);
            ShowInTaskbar   = false;
            Font            = new Font("Segoe UI", 9f);
            BackColor       = Color.FromArgb(18, 18, 30);

            BuildUI();
            ExtractTriangles();
        }

        // ── UI layout ─────────────────────────────────────────────────────────

        private void BuildUI()
        {
            _hintLabel = new Label
            {
                Dock      = DockStyle.Top,
                Height    = 22,
                Text      = "Right-click drag to rotate  ·  Scroll to zoom",
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(120, 120, 140),
                BackColor = Color.FromArgb(10, 10, 20),
                Font      = new Font("Segoe UI", 8f),
            };

            _bottomPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 112,
                BackColor = Color.FromArgb(10, 10, 20),
            };
            BuildBottomPanel();

            _picture = new PictureBox
            {
                SizeMode  = PictureBoxSizeMode.CenterImage,
                BackColor = _background,
            };
            _picture.MouseDown  += OnPictureMouseDown;
            _picture.MouseMove  += OnPictureMouseMove;
            _picture.MouseUp    += OnPictureMouseUp;
            _picture.MouseEnter += (s, e) => this.Focus();

            Controls.Add(_picture);
            Controls.Add(_bottomPanel);
            Controls.Add(_hintLabel);
        }

        private void BuildBottomPanel()
        {
            // ── Row 1 (y=4): isovalue controls ────────────────────────────────
            const int row1Y = 4;
            int x = 6;

            _chkIso = new CheckBox
            {
                Text      = "Isosurface",
                Checked   = true,
                ForeColor = Color.FromArgb(0, 180, 80),
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                AutoSize  = true,
                Location  = new Point(x, row1Y),
            };
            _chkIso.CheckedChanged += OnIsoCheckChanged;
            _bottomPanel.Controls.Add(_chkIso);
            x += _chkIso.PreferredSize.Width + 10;

            var isoLbl = new Label
            {
                Text      = "Isovalue:",
                AutoSize  = true,
                Location  = new Point(x, row1Y + 2),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(isoLbl);
            x += isoLbl.PreferredSize.Width + 4;

            // Slider: 1–2000, default 1000 (÷1000 → 0.001–2.000, default 1.000)
            _isoSlider = new TrackBar
            {
                Minimum       = 1,
                Maximum       = 2000,
                Value         = 1000,
                Width         = 100,
                Height        = 22,
                Location      = new Point(x, row1Y - 2),
                TickFrequency = 200,
                LargeChange   = 100,
            };
            _isoSlider.Scroll += OnIsoSliderScroll;
            _bottomPanel.Controls.Add(_isoSlider);
            x += _isoSlider.Width + 4;

            _isoLabel = new Label
            {
                Text      = "1.000",
                AutoSize  = true,
                Location  = new Point(x, row1Y + 2),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(_isoLabel);
            x += 52;

            _bgColorBtn = new Button
            {
                Text      = "BG",
                Width     = 36,
                Height    = 22,
                Location  = new Point(x, row1Y),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgColorBtn.FlatAppearance.BorderColor = Color.Silver;
            _bgColorBtn.Click += OnBgColorClick;
            _bottomPanel.Controls.Add(_bgColorBtn);

            // ── Row 2 (y=50): crystal controls ────────────────────────────────
            const int row2Y = 50;
            x = 6;

            _chkUnitCell = new CheckBox
            {
                Text      = "Unit Cell",
                Checked   = true,
                AutoSize  = true,
                Location  = new Point(x, row2Y),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 8.5f),
            };
            _chkUnitCell.CheckedChanged += (s, e) => { _showUnitCell = _chkUnitCell.Checked; RenderPreview(); };
            _bottomPanel.Controls.Add(_chkUnitCell);
            x += _chkUnitCell.PreferredSize.Width + 8;

            string[] axisLabels = { "x:", "y:", "z:" };
            for (int axis = 0; axis < 3; axis++)
            {
                int capturedAxis = axis;

                var axisLabel = new Label
                {
                    Text      = axisLabels[axis],
                    AutoSize  = true,
                    Location  = new Point(x, row2Y + 2),
                    ForeColor = Color.FromArgb(180, 180, 200),
                    Font      = new Font("Segoe UI", 8.5f),
                };
                _bottomPanel.Controls.Add(axisLabel);
                x += axisLabel.PreferredSize.Width + 2;

                _btnMinus[axis] = new Button
                {
                    Text      = "−",
                    Width     = 20,
                    Height    = 22,
                    Location  = new Point(x, row2Y),
                    FlatStyle = FlatStyle.Flat,
                    Font      = new Font("Segoe UI", 8f),
                    ForeColor = Color.FromArgb(180, 180, 200),
                };
                _btnMinus[axis].FlatAppearance.BorderColor = Color.Gray;
                _btnMinus[axis].Click += (s, e) => OnMinus(capturedAxis);
                _bottomPanel.Controls.Add(_btnMinus[axis]);
                x += 22;

                _supercellCountLabels[axis] = new Label
                {
                    Text      = "1",
                    Width     = 18,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    Location  = new Point(x, row2Y + 2),
                    ForeColor = Color.FromArgb(180, 180, 200),
                    Font      = new Font("Segoe UI", 8.5f),
                };
                _bottomPanel.Controls.Add(_supercellCountLabels[axis]);
                x += 20;

                _btnPlus[axis] = new Button
                {
                    Text      = "+",
                    Width     = 20,
                    Height    = 22,
                    Location  = new Point(x, row2Y),
                    FlatStyle = FlatStyle.Flat,
                    Font      = new Font("Segoe UI", 8f),
                    ForeColor = Color.FromArgb(180, 180, 200),
                };
                _btnPlus[axis].FlatAppearance.BorderColor = Color.Gray;
                _btnPlus[axis].Click += (s, e) => OnPlus(capturedAxis);
                _bottomPanel.Controls.Add(_btnPlus[axis]);
                x += 24;
            }

            _chkVectors = new CheckBox
            {
                Text      = "Vectors",
                Checked   = true,
                AutoSize  = true,
                Location  = new Point(x, row2Y),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 8.5f),
            };
            _chkVectors.CheckedChanged += (s, e) => { _showVectors = _chkVectors.Checked; RenderPreview(); };
            _bottomPanel.Controls.Add(_chkVectors);

            // ── Row 3 (y=84): format · save · cancel ──────────────────────────
            const int row3Y = 84;

            var fmtLbl = new Label
            {
                Text      = "Format:",
                AutoSize  = true,
                Location  = new Point(6, row3Y + 4),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(fmtLbl);

            _formatCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location      = new Point(62, row3Y + 2),
                Width         = 72,
            };
            _formatCombo.Items.AddRange(new object[] { "PNG", "TIFF", "JPEG" });
            _formatCombo.SelectedIndex = 0;
            _bottomPanel.Controls.Add(_formatCombo);

            _saveBtn = new Button
            {
                Text      = "Save Image",
                Width     = 90,
                Height    = 26,
                FlatStyle = FlatStyle.Flat,
                Anchor    = AnchorStyles.Right | AnchorStyles.Top,
                Location  = new Point(_bottomPanel.Width - 174, row3Y),
            };
            _saveBtn.FlatAppearance.BorderColor = Color.SteelBlue;
            _saveBtn.ForeColor = Color.SteelBlue;
            _saveBtn.Click += OnSaveClick;
            AcceptButton = _saveBtn;
            _bottomPanel.Controls.Add(_saveBtn);

            _cancelBtn = new Button
            {
                Text      = "Cancel",
                Width     = 76,
                Height    = 26,
                FlatStyle = FlatStyle.Flat,
                Anchor    = AnchorStyles.Right | AnchorStyles.Top,
                Location  = new Point(_bottomPanel.Width - 82, row3Y),
            };
            _cancelBtn.FlatAppearance.BorderColor = Color.Silver;
            _cancelBtn.ForeColor = Color.FromArgb(180, 180, 200);
            _cancelBtn.Click += (s, e) => Close();
            CancelButton = _cancelBtn;
            _bottomPanel.Controls.Add(_cancelBtn);

            _bottomPanel.Resize += (s, e) =>
            {
                int w = _bottomPanel.ClientSize.Width;
                _cancelBtn.Location = new Point(w - _cancelBtn.Width - 6, row3Y);
                _saveBtn.Location   = new Point(w - _cancelBtn.Width - _saveBtn.Width - 12, row3Y);
            };
        }

        // ── Preview rendering ─────────────────────────────────────────────────

        private void LayoutPicture()
        {
            int top = _hintLabel.Height;
            int h   = Math.Max(ClientSize.Height - _hintLabel.Height - _bottomPanel.Height, 1);
            _picture.SetBounds(0, top, ClientSize.Width, h);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutPicture();
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

        private void RenderPreview(bool lowQuality = false)
        {
            if (_picture == null || _picture.Width < 10 || _picture.Height < 10) return;
            int w = _picture.Width;
            int h = _picture.Height;
            try
            {
                var iso = _showIso ? _isoTriangles : null;
                var bmp = ChgcarRenderer.Render(
                    _expandedMolecule, _crystal, _supercell,
                    iso, null,
                    _rotMatrix, w, h, _background, lowQuality, lowQuality,
                    _zoomFactor, _showUnitCell, _showVectors, _showIso, false);
                var old = _picture.Image;
                _picture.Image = bmp;
                old?.Dispose();
            }
            catch { }
        }

        // ── Isovalue controls ─────────────────────────────────────────────────

        private void OnIsoCheckChanged(object sender, EventArgs e)
        {
            _showIso = _chkIso.Checked;
            if (_showIso && _isoTriangles == null) ExtractTriangles();
            RenderPreview();
        }

        private void OnIsoSliderScroll(object sender, EventArgs e)
        {
            _isovalue = _isoSlider.Value / 1000f;
            _isoLabel.Text = _isovalue.ToString("0.000");
            ExtractTriangles();
            RenderPreview();
        }

        private void ExtractTriangles()
        {
            if (_grid == null) { _isoTriangles = null; return; }
            try
            {
                _isoTriangles = _showIso ? MarchingCubes.Extract(_grid, _isovalue, true) : null;
            }
            catch
            {
                _isoTriangles = null;
            }
        }

        // ── Supercell controls ────────────────────────────────────────────────

        private void OnPlus(int axis)
        {
            _supercell[axis]++;
            _supercellCountLabels[axis].Text = _supercell[axis].ToString();
            ExpandSupercell();
            RenderPreview();
        }

        private void OnMinus(int axis)
        {
            if (_supercell[axis] <= 1) return;
            _supercell[axis]--;
            _supercellCountLabels[axis].Text = _supercell[axis].ToString();
            ExpandSupercell();
            RenderPreview();
        }

        private void ExpandSupercell()
        {
            if (_crystal == null) return;
            int nx = _supercell[0], ny = _supercell[1], nz = _supercell[2];
            var atoms = new List<Atom>();
            foreach (var uc in _crystal.UnitCellAtoms)
            {
                for (int ix = 0; ix < nx; ix++)
                for (int iy = 0; iy < ny; iy++)
                for (int iz = 0; iz < nz; iz++)
                {
                    double x = uc.X + ix*_crystal.VectorA[0] + iy*_crystal.VectorB[0] + iz*_crystal.VectorC[0];
                    double y = uc.Y + ix*_crystal.VectorA[1] + iy*_crystal.VectorB[1] + iz*_crystal.VectorC[1];
                    double z = uc.Z + ix*_crystal.VectorA[2] + iy*_crystal.VectorB[2] + iz*_crystal.VectorC[2];
                    atoms.Add(new Atom(uc.Element, x, y, z));
                }
            }
            _expandedMolecule = new Molecule { Atoms = atoms, Bonds = BondDetector.Detect(atoms) };
        }

        // ── Background colour ─────────────────────────────────────────────────

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

            float lum     = 0.299f * bg.R + 0.587f * bg.G + 0.114f * bg.B;
            Color textColor = lum > 128f ? Color.Black : Color.White;
            Color panelBg   = lum > 128f ? Color.FromArgb(220, 220, 225) : Color.FromArgb(10, 10, 20);

            _hintLabel.ForeColor   = lum > 128f ? Color.FromArgb(80, 80, 100) : Color.FromArgb(120, 120, 140);
            _hintLabel.BackColor   = panelBg;
            _bottomPanel.BackColor = panelBg;

            foreach (Control c in _bottomPanel.Controls)
            {
                if (c is Label lbl) lbl.ForeColor = textColor;
                if (c is Button btn && btn != _saveBtn) btn.ForeColor = textColor;
                if (c is CheckBox chk && chk != _chkIso) chk.ForeColor = textColor;
            }

            RenderPreview();
        }

        // ── Zoom ──────────────────────────────────────────────────────────────

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _zoomFactor *= e.Delta > 0 ? 1.1f : (1f / 1.1f);
            if (_zoomFactor < 0.2f) _zoomFactor = 0.2f;
            if (_zoomFactor > 5.0f) _zoomFactor = 5.0f;
            RenderPreview();
        }

        // ── Arcball drag ──────────────────────────────────────────────────────

        private void OnPictureMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            _isDragging     = true;
            _dragBaseMatrix = (float[,])_rotMatrix.Clone();
            _dragStartVec   = ArcballVec(e.X, e.Y);
            _picture.Cursor = Cursors.SizeAll;
        }

        private void OnPictureMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            float[]  cur   = ArcballVec(e.X, e.Y);
            float[,] delta = ArcballRotation(_dragStartVec, cur);
            _rotMatrix = MatMul3(delta, _dragBaseMatrix);
            RenderPreview(true);
        }

        private void OnPictureMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !_isDragging) return;
            _isDragging     = false;
            _picture.Cursor = Cursors.Default;
            RenderPreview();
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private void OnSaveClick(object sender, EventArgs e)
        {
            string fmt    = _formatCombo.SelectedItem?.ToString() ?? "PNG";
            string filter = fmt == "TIFF" ? "TIFF Image (*.tiff)|*.tiff|All Files (*.*)|*.*"
                          : fmt == "JPEG" ? "JPEG Image (*.jpg)|*.jpg|All Files (*.*)|*.*"
                          :                 "PNG Image (*.png)|*.png|All Files (*.*)|*.*";

            string defaultName = _crystal?.SystemName ?? "chgcar";

            using (var dlg = new SaveFileDialog
            {
                Title      = "Save Image",
                Filter     = filter,
                DefaultExt = fmt.ToLowerInvariant(),
                FileName   = defaultName,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    var iso = _showIso ? _isoTriangles : null;
                    using (var bmp = ChgcarRenderer.Render(
                        _expandedMolecule, _crystal, _supercell,
                        iso, null,
                        _rotMatrix, 1200, 900, _background, false, false,
                        _zoomFactor, _showUnitCell, _showVectors, _showIso, false))
                    {
                        ImageFormat imgFmt = fmt == "TIFF" ? ImageFormat.Tiff
                                          : fmt == "JPEG" ? ImageFormat.Jpeg
                                          : ImageFormat.Png;
                        bmp.Save(dlg.FileName, imgFmt);
                    }

                    MessageBox.Show($"Image saved to:\n{dlg.FileName}",
                        "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving image:\n{ex.Message}",
                        "QuantumAnalyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ── Arcball math ──────────────────────────────────────────────────────

        private float[] ArcballVec(int px, int py)
        {
            float cx = _picture.Width  / 2f;
            float cy = _picture.Height / 2f;
            float r  = Math.Min(_picture.Width, _picture.Height) / 2f * 0.85f;
            if (r < 1f) r = 1f;
            float x  = (px - cx) / r;
            float y  = (cy - py) / r;
            float d2 = x * x + y * y;
            if (d2 <= 1f)
                return new[] { x, y, (float)Math.Sqrt(1f - d2) };
            float len = (float)Math.Sqrt(d2);
            return new[] { x / len, y / len, 0f };
        }

        private static float[,] ArcballRotation(float[] from, float[] to)
        {
            float dot = from[0]*to[0] + from[1]*to[1] + from[2]*to[2];
            dot = Math.Max(-1f, Math.Min(1f, dot));
            float kx = from[1]*to[2] - from[2]*to[1];
            float ky = from[2]*to[0] - from[0]*to[2];
            float kz = from[0]*to[1] - from[1]*to[0];
            float kLen = (float)Math.Sqrt(kx*kx + ky*ky + kz*kz);
            if (kLen < 1e-6f) return Identity3();
            kx /= kLen; ky /= kLen; kz /= kLen;
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
            float c  = (float)Math.Cos(rx);
            float s  = (float)Math.Sin(rx);
            return new float[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
        }

        // ── Dispose ───────────────────────────────────────────────────────────

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
