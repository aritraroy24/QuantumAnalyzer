using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SharpShell.SharpPreviewHandler;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Rendering;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Preview pane control for VASP CHGCAR files.
    /// Combines crystal structure controls (unit cell wireframe, supercell, lattice vectors)
    /// with isosurface control (show/hide toggle, isovalue slider).
    ///
    /// Layout:
    ///   Top label   (24px)  — filename
    ///   PictureBox  (Fill)  — rotating crystal + charge-density isosurface
    ///   Bottom panel (68px) —
    ///     Row 1 (y=4):  [☑ Isosurface]  Isovalue: [slider 1-2000, default 1000] [1.000]  [BG]
    ///     Row 2 (y=38): [☑ Unit Cell]  x:[−][1][+]  y:[−][1][+]  z:[−][1][+]  [☑ Vectors]
    /// </summary>
    public class ChgcarPreviewControl : PreviewHandlerControl
    {
        // ── Widgets ──────────────────────────────────────────────────────────
        private PictureBox _picture;
        private Label      _label;
        private Panel      _bottomPanel;
        private Button     _bgButton;
        private Timer      _timer;

        // Row 1 — isosurface controls
        private CheckBox _chkIso;
        private TrackBar _isoSlider;
        private Label    _isoLabel;

        // Row 2 — crystal controls
        private CheckBox   _chkUnitCell;
        private CheckBox   _chkVectors;
        private readonly Label[]  _supercellCountLabels = new Label[3];
        private readonly Button[] _btnMinus             = new Button[3];
        private readonly Button[] _btnPlus              = new Button[3];

        // ── State ─────────────────────────────────────────────────────────────
        private LatticeCell    _crystal;
        private VolumetricGrid _grid;
        private Molecule       _expandedMolecule;
        private float[,]       _rotMatrix;
        private float          _zoomFactor   = 1.0f;
        private Color          _background   = Color.FromArgb(18, 18, 30);
        private int[]          _supercell    = new[] { 1, 1, 1 };
        private bool           _showUnitCell = true;
        private bool           _showVectors  = true;
        private bool           _showIso      = true;
        private float          _isovalue     = 1.0f;

        private List<MarchingCubes.Triangle> _isoTriangles;

        // Drag state
        private bool     _isDragging;
        private float[,] _dragBaseMatrix;
        private float[]  _dragStartVec;

        private const float StepDeg = 0.3f;
        private const int   TickMs  = 100;  // 10 fps — keeps isosurface visible during rotation

        // ── Constructor ───────────────────────────────────────────────────────

        public ChgcarPreviewControl()
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

            _picture = new PictureBox
            {
                Dock      = DockStyle.Fill,
                SizeMode  = PictureBoxSizeMode.CenterImage,
                BackColor = _background,
            };

            _bottomPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 68,
                BackColor = Color.FromArgb(10, 10, 20),
            };

            BuildBottomPanel();

            Controls.Add(_picture);
            Controls.Add(_bottomPanel);
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

        public void SetData(Molecule mol, VolumetricGrid grid, LatticeCell crystal, string displayLabel)
        {
            _grid             = grid;
            _crystal          = crystal;
            _expandedMolecule = mol;
            _label.Text       = displayLabel ?? "CHGCAR";
            _rotMatrix        = InitialRotation();

            if (grid != null)
            {
                ExtractTriangles();
                _timer.Start();
                RenderFrame(lowQuality: false);
            }
            else
            {
                _label.Text += "  (no volumetric data)";
            }
        }

        // ── Bottom panel ──────────────────────────────────────────────────────

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

            _bgButton = new Button
            {
                Text      = "BG",
                Width     = 36,
                Height    = 22,
                Location  = new Point(x, row1Y),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgButton.FlatAppearance.BorderColor = Color.Silver;
            _bgButton.Click += OnBgButtonClick;
            _bottomPanel.Controls.Add(_bgButton);

            // ── Row 2 (y=38): crystal controls ────────────────────────────────
            const int row2Y = 38;
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
            _chkUnitCell.CheckedChanged += (s, e) => { _showUnitCell = _chkUnitCell.Checked; RenderFrame(false); };
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
            _chkVectors.CheckedChanged += (s, e) => { _showVectors = _chkVectors.Checked; RenderFrame(false); };
            _bottomPanel.Controls.Add(_chkVectors);
        }

        // ── Isovalue controls ─────────────────────────────────────────────────

        private void OnIsoCheckChanged(object sender, EventArgs e)
        {
            _showIso = _chkIso.Checked;
            if (_showIso && _isoTriangles == null) ExtractTriangles();
            RenderFrame(lowQuality: false);
        }

        private void OnIsoSliderScroll(object sender, EventArgs e)
        {
            _isovalue = _isoSlider.Value / 1000f;
            _isoLabel.Text = _isovalue.ToString("0.000");
            ExtractTriangles();
            RenderFrame(lowQuality: false);
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
            ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            RenderFrame(lowQuality: false);
        }

        private void OnMinus(int axis)
        {
            if (_supercell[axis] <= 1) return;
            _supercell[axis]--;
            _supercellCountLabels[axis].Text = _supercell[axis].ToString();
            ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            RenderFrame(lowQuality: false);
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

        // lowQuality=true  → skip triangles (drag)
        // flatAtoms=true   → flat circle atoms instead of gradient spheres (auto-rotation)
        private void RenderFrame(bool lowQuality, bool flatAtoms = false)
        {
            if (_crystal == null && (_expandedMolecule == null || !_expandedMolecule.HasGeometry)) return;
            int w = Math.Max(_picture.Width,  64);
            int h = Math.Max(_picture.Height, 64);
            try
            {
                var iso = _showIso ? _isoTriangles : null;
                var bmp = ChgcarRenderer.Render(
                    _expandedMolecule, _crystal, _supercell,
                    iso, null,
                    _rotMatrix, w, h, _background, lowQuality, flatAtoms,
                    _zoomFactor, _showUnitCell, _showVectors, _showIso, false);
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
            // Show isosurface during auto-rotation; use flat atoms to keep 10 fps smooth.
            RenderFrame(lowQuality: false, flatAtoms: true);
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
            RenderFrame(lowQuality: true, flatAtoms: true);
        }

        private void OnPictureMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !_isDragging) return;
            _isDragging     = false;
            _picture.Cursor = Cursors.Default;
            RenderFrame(lowQuality: false, flatAtoms: false);
            _timer.Start();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _zoomFactor *= e.Delta > 0 ? 1.1f : (1f / 1.1f);
            if (_zoomFactor < 0.2f) _zoomFactor = 0.2f;
            if (_zoomFactor > 5.0f) _zoomFactor = 5.0f;
            RenderFrame(lowQuality: false);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RenderFrame(lowQuality: false);
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
            _bottomPanel.BackColor = panelBg;

            foreach (Control c in _bottomPanel.Controls)
            {
                if (c is Label lbl) lbl.ForeColor = textColor;
                if (c is Button btn) btn.ForeColor = textColor;
                if (c is CheckBox chk && chk != _chkIso) chk.ForeColor = textColor;
            }

            RenderFrame(lowQuality: false);
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
                _label?.Dispose();
                _bottomPanel?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ── Arcball helpers ───────────────────────────────────────────────────

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
