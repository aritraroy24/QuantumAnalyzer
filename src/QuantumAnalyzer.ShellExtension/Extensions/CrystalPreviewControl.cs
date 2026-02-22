using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SharpShell.SharpPreviewHandler;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Rendering;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Preview pane control for VASP POSCAR / CONTCAR crystal structure files.
    ///
    /// Layout:
    ///   Top label   (24px)  — filename
    ///   PictureBox  (Fill)  — rotating crystal with wireframe + vector arrows
    ///   Bottom panel (36px) — [☑ Unit Cell]  x:[−][1][+]  y:[−][1][+]  z:[−][1][+]  [☑ Vectors]  [BG]
    /// </summary>
    public class CrystalPreviewControl : PreviewHandlerControl
    {
        // ── Widgets ──────────────────────────────────────────────────────────
        private PictureBox _picture;
        private Label      _label;
        private Panel      _titleSeparator;
        private PreviewInfoPanel _infoPanel;
        private Panel      _bottomPanel;
        private Button     _bgButton;
        private Timer      _timer;

        private CheckBox   _chkUnitCell;
        private CheckBox   _chkVectors;
        private readonly Label[]  _supercellCountLabels = new Label[3];
        private readonly Button[] _btnMinus             = new Button[3];
        private readonly Button[] _btnPlus              = new Button[3];

        // ── State ─────────────────────────────────────────────────────────────
        private LatticeCell _crystal;
        private Molecule    _expandedMolecule;
        private float[,]    _rotMatrix;
        private float       _zoomFactor   = 1.0f;
        private Color       _background   = Color.FromArgb(18, 18, 30);
        private int[]       _supercell    = new[] { 1, 1, 1 };
        private bool        _showUnitCell = true;
        private bool        _showVectors  = true;

        // Drag state
        private bool     _isDragging;
        private float[,] _dragBaseMatrix;
        private float[]  _dragStartVec;

        private const float StepDeg = 0.3f;
        private const int   TickMs  = 33;

        // ── Constructor ───────────────────────────────────────────────────────

        public CrystalPreviewControl()
        {
            BackColor      = _background;
            DoubleBuffered = true;

            _label = new Label
            {
                Dock      = DockStyle.Top,
                Height    = 24,
                ForeColor = Color.FromArgb(180, 180, 200),
                BackColor = Color.FromArgb(10, 10, 20),
                Font      = new Font("Segoe UI", 9f),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Text      = "Loading…",
                Padding   = new Padding(4, 0, 4, 0),
            };

            _titleSeparator = new Panel
            {
                Dock = DockStyle.Top,
                Height = 10,
                BackColor = Color.FromArgb(10, 10, 20),
            };
            _titleSeparator.Paint += (s, e) =>
            {
                using (var p1 = new Pen(Color.FromArgb(95, 95, 120), 1f))
                using (var p2 = new Pen(Color.FromArgb(55, 55, 75), 1f))
                {
                    e.Graphics.DrawLine(p1, 0, 2, _titleSeparator.Width, 2);
                    e.Graphics.DrawLine(p2, 0, 4, _titleSeparator.Width, 4);
                }
            };

            _infoPanel = new PreviewInfoPanel();
            _infoPanel.SetHeaders("GENERAL", "MODEL", "LATTICE");

            _picture = new PictureBox
            {
                Dock      = DockStyle.Fill,
                SizeMode  = PictureBoxSizeMode.CenterImage,
                BackColor = _background,
            };

            _bottomPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 36,
                BackColor = Color.FromArgb(10, 10, 20),
            };

            BuildBottomPanel();

            Controls.Add(_picture);
            Controls.Add(_bottomPanel);
            Controls.Add(_infoPanel);
            Controls.Add(_titleSeparator);
            Controls.Add(_label);

            _picture.MouseDown  += OnPictureMouseDown;
            _picture.MouseMove  += OnPictureMouseMove;
            _picture.MouseUp    += OnPictureMouseUp;
            _picture.MouseEnter += (s, e) => this.Focus();

            _timer = new Timer { Interval = TickMs };
            _timer.Tick += OnTick;

            _rotMatrix = InitialRotation();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Called by QuantumPreviewHandler after parsing a POSCAR/CONTCAR file.</summary>
        public void SetCrystal(Molecule unitCellMolecule, LatticeCell crystal, string displayLabel)
        {
            _crystal          = crystal;
            _expandedMolecule = unitCellMolecule;
            _label.Text       = string.IsNullOrWhiteSpace(displayLabel) ? "Crystal Structure" : Path.GetFileName(displayLabel);
            _rotMatrix        = InitialRotation();
            ApplyInfoSummary(unitCellMolecule, crystal);

            if (unitCellMolecule != null && unitCellMolecule.HasGeometry)
            {
                _timer.Start();
                RenderFrame();
            }
            else
            {
                _label.Text += "  (no geometry)";
            }
        }

        // ── Bottom panel ──────────────────────────────────────────────────────

        private void BuildBottomPanel()
        {
            int x = 6;
            const int cy = 7;

            // [☑ Unit Cell]
            _chkUnitCell = new CheckBox
            {
                Text      = "Unit Cell",
                Checked   = true,
                AutoSize  = true,
                Location  = new Point(x, cy),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 8.5f),
            };
            _chkUnitCell.CheckedChanged += (s, e) =>
            {
                _showUnitCell = _chkUnitCell.Checked;
                ApplyInfoSummary(_expandedMolecule, _crystal);
                RenderFrame();
            };
            _bottomPanel.Controls.Add(_chkUnitCell);
            x += _chkUnitCell.PreferredSize.Width + 8;

            // x / y / z +/- controls
            string[] axisLabels = { "x:", "y:", "z:" };
            for (int axis = 0; axis < 3; axis++)
            {
                int capturedAxis = axis;

                var axisLabel = new Label
                {
                    Text     = axisLabels[axis],
                    AutoSize = true,
                    Location = new Point(x, cy + 2),
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
                    Location  = new Point(x, cy),
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
                    Location  = new Point(x, cy + 2),
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
                    Location  = new Point(x, cy),
                    FlatStyle = FlatStyle.Flat,
                    Font      = new Font("Segoe UI", 8f),
                    ForeColor = Color.FromArgb(180, 180, 200),
                };
                _btnPlus[axis].FlatAppearance.BorderColor = Color.Gray;
                _btnPlus[axis].Click += (s, e) => OnPlus(capturedAxis);
                _bottomPanel.Controls.Add(_btnPlus[axis]);
                x += 24;
            }

            // [☑ Vectors]
            _chkVectors = new CheckBox
            {
                Text      = "Vectors",
                Checked   = true,
                AutoSize  = true,
                Location  = new Point(x, cy),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 8.5f),
            };
            _chkVectors.CheckedChanged += (s, e) =>
            {
                _showVectors = _chkVectors.Checked;
                ApplyInfoSummary(_expandedMolecule, _crystal);
                RenderFrame();
            };
            _bottomPanel.Controls.Add(_chkVectors);
            x += _chkVectors.PreferredSize.Width + 8;

            // [BG]
            _bgButton = new Button
            {
                Text      = "BG",
                Width     = 36,
                Height    = 22,
                Location  = new Point(x, cy),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgButton.FlatAppearance.BorderColor = Color.Silver;
            _bgButton.Click += OnBgButtonClick;
            _bottomPanel.Controls.Add(_bgButton);
        }

        // ── Supercell controls ────────────────────────────────────────────────

        private void OnPlus(int axis)
        {
            _supercell[axis]++;
            _supercellCountLabels[axis].Text = _supercell[axis].ToString();
            ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            ApplyInfoSummary(_expandedMolecule, _crystal);
            RenderFrame();
        }

        private void OnMinus(int axis)
        {
            if (_supercell[axis] <= 1) return;
            _supercell[axis]--;
            _supercellCountLabels[axis].Text = _supercell[axis].ToString();
            ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            ApplyInfoSummary(_expandedMolecule, _crystal);
            RenderFrame();
        }

        private void ExpandSupercell(int nx, int ny, int nz)
        {
            if (_crystal == null) return;

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

        // ── Rendering ─────────────────────────────────────────────────────────

        private void RenderFrame(bool lowQuality = false)
        {
            if (_crystal == null && (_expandedMolecule == null || !_expandedMolecule.HasGeometry)) return;
            int w = Math.Max(_picture.Width,  64);
            int h = Math.Max(_picture.Height, 64);
            try
            {
                var bmp = CrystalRenderer.Render(
                    _expandedMolecule, _crystal, _supercell,
                    _rotMatrix, w, h, _background, lowQuality,
                    _zoomFactor, _showUnitCell, _showVectors);
                var old = _picture.Image;
                _picture.Image = bmp;
                old?.Dispose();
            }
            catch
            {
                // Keep last frame on render error
            }
        }

        // ── Animation & drag ──────────────────────────────────────────────────

        private void OnTick(object sender, EventArgs e)
        {
            float step = StepDeg * (float)(Math.PI / 180.0);
            float c = (float)Math.Cos(step);
            float s = (float)Math.Sin(step);
            var ry = new float[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
            _rotMatrix = MatMul3(ry, _rotMatrix);
            RenderFrame();
        }

        private void OnPictureMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            _isDragging     = true;
            _dragBaseMatrix = (float[,])_rotMatrix.Clone();
            _dragStartVec   = ArcballVec(e.X, e.Y);
            _timer.Stop();
            _picture.Cursor = Cursors.SizeAll;
        }

        private void OnPictureMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            float[]  cur   = ArcballVec(e.X, e.Y);
            float[,] delta = ArcballRotation(_dragStartVec, cur);
            _rotMatrix = MatMul3(delta, _dragBaseMatrix);
            RenderFrame(lowQuality: true);
        }

        private void OnPictureMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !_isDragging) return;
            _isDragging     = false;
            _picture.Cursor = Cursors.Default;
            RenderFrame();
            _timer.Start();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _zoomFactor *= e.Delta > 0 ? 1.1f : (1f / 1.1f);
            if (_zoomFactor < 0.2f) _zoomFactor = 0.2f;
            if (_zoomFactor > 5.0f) _zoomFactor = 5.0f;
            RenderFrame();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RenderFrame();
        }

        // ── Background colour ─────────────────────────────────────────────────

        private void OnBgButtonClick(object sender, EventArgs e)
        {
            using (var dlg = new ColorDialog { Color = _background, FullOpen = true })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    ApplyBackground(dlg.Color);
            }
        }

        private void ApplyBackground(Color bg)
        {
            _background = bg;
            _picture.BackColor = bg;
            BackColor = bg;

            float lum = 0.299f * bg.R + 0.587f * bg.G + 0.114f * bg.B;
            Color textColor = lum > 128f ? Color.Black : Color.White;
            Color panelBg   = lum > 128f ? Color.FromArgb(220, 220, 225) : Color.FromArgb(10, 10, 20);

            _label.ForeColor       = textColor;
            _label.BackColor       = panelBg;
            _titleSeparator.BackColor = panelBg;
            _bottomPanel.BackColor = panelBg;
            _infoPanel.ApplyTheme(textColor, panelBg);
            _titleSeparator.Invalidate();

            foreach (Control c in _bottomPanel.Controls)
            {
                if (c is Label lbl) lbl.ForeColor = textColor;
                if (c is Button btn) btn.ForeColor = textColor;
                if (c is CheckBox chk) chk.ForeColor = textColor;
            }

            RenderFrame();
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _picture?.Image?.Dispose();
                _picture?.Dispose();
                _titleSeparator?.Dispose();
                _infoPanel?.Dispose();
                _label?.Dispose();
                _bottomPanel?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ApplyInfoSummary(Molecule unitCellMolecule, LatticeCell crystal)
        {
            if (crystal == null)
            {
                _infoPanel.SetSections(new List<string>(), new List<string>(), new List<string>());
                return;
            }

            var general = new List<string>();
            var model = new List<string>();
            var lattice = new List<string>();

            int atomCount = unitCellMolecule?.Atoms?.Count ?? crystal.UnitCellAtoms?.Count ?? 0;
            int elemCount = unitCellMolecule?.Atoms?.Select(a => a.Element).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;

            PreviewInfoPanel.AddField(general, "Type", "Crystal");
            PreviewInfoPanel.AddField(general, "Atoms", atomCount > 0 ? atomCount.ToString() : null);
            PreviewInfoPanel.AddField(general, "Elements", elemCount > 0 ? elemCount.ToString() : null);
            PreviewInfoPanel.AddField(general, "CellAtoms", crystal.UnitCellAtoms?.Count.ToString());

            PreviewInfoPanel.AddField(model, "UnitCell", _showUnitCell ? "Shown" : "Hidden");
            PreviewInfoPanel.AddField(model, "Vectors", _showVectors ? "Shown" : "Hidden");
            PreviewInfoPanel.AddField(model, "Supercell", $"{_supercell[0]}x{_supercell[1]}x{_supercell[2]}");

            PreviewInfoPanel.AddField(lattice, "a(A)", crystal.LengthA.ToString("F3"));
            PreviewInfoPanel.AddField(lattice, "b(A)", crystal.LengthB.ToString("F3"));
            PreviewInfoPanel.AddField(lattice, "c(A)", crystal.LengthC.ToString("F3"));
            PreviewInfoPanel.AddField(lattice, "A", $"{crystal.VectorA[0]:F2},{crystal.VectorA[1]:F2},{crystal.VectorA[2]:F2}");
            PreviewInfoPanel.AddField(lattice, "B", $"{crystal.VectorB[0]:F2},{crystal.VectorB[1]:F2},{crystal.VectorB[2]:F2}");
            PreviewInfoPanel.AddField(lattice, "C", $"{crystal.VectorC[0]:F2},{crystal.VectorC[1]:F2},{crystal.VectorC[2]:F2}");

            _infoPanel.SetSections(general, model, lattice);
        }

        // ── Arcball helpers (identical to MoleculePreviewControl) ─────────────

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
    }
}
