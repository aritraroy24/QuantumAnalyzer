using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SharpShell.SharpPreviewHandler;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Rendering;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Preview pane control for Gaussian .cube files.
    /// Shows a rotating 3D molecule + isosurface lobes with interactive controls.
    ///
    /// Layout:
    ///   Top label  (24px)   — filename + data description
    ///   PictureBox (Fill)   — rotating isosurface + molecule
    ///   Bottom panel (32px) — ☑+ ☑−  Isovalue: [slider] 0.020  [BG]
    /// </summary>
    public class CubePreviewControl : PreviewHandlerControl
    {
        // ── Widgets ──────────────────────────────────────────────────────
        private PictureBox _picture;
        private Label      _label;
        private Panel      _bottomPanel;
        private CheckBox   _chkPos;
        private CheckBox   _chkNeg;
        private TrackBar   _isoSlider;
        private Label      _isoLabel;
        private Button     _bgButton;
        private Timer      _timer;

        // ── State ────────────────────────────────────────────────────────
        private Molecule       _molecule;
        private VolumetricGrid _grid;
        private float[,]       _rotMatrix;
        private Color          _background = Color.FromArgb(18, 18, 30);   // same dark as MoleculePreviewControl
        private float          _isovalue   = 0.020f;
        private float          _zoomFactor = 1.0f;
        private bool           _showPos    = true;
        private bool           _showNeg    = true;

        private List<MarchingCubes.Triangle> _posTriangles;
        private List<MarchingCubes.Triangle> _negTriangles;

        // Drag state
        private bool     _isDragging;
        private float[,] _dragBaseMatrix;
        private float[]  _dragStartVec;

        private const float StepDeg = 0.5f;
        private const int   TickMs  = 33;

        public CubePreviewControl()
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
                Height    = 32,
                BackColor = Color.FromArgb(10, 10, 20),
            };

            BuildBottomPanel();

            Controls.Add(_picture);
            Controls.Add(_bottomPanel);
            Controls.Add(_label);

            _picture.MouseDown  += OnPictureMouseDown;
            _picture.MouseMove  += OnPictureMouseMove;
            _picture.MouseUp    += OnPictureMouseUp;
            _picture.MouseEnter += (s, e) => this.Focus();  // allow parent to receive scroll

            _timer = new Timer { Interval = TickMs };
            _timer.Tick += OnTick;

            _rotMatrix = InitialRotation();
        }

        /// <summary>Called by QuantumPreviewHandler after parsing a .cube file.</summary>
        public void SetData(Molecule molecule, VolumetricGrid grid, string displayLabel)
        {
            _molecule = molecule;
            _grid     = grid;
            _label.Text = displayLabel ?? "Cube file";

            if (grid != null)
            {
                _rotMatrix = InitialRotation();
                ExtractTriangles();
                _timer.Start();
                RenderFrame();
            }
            else
            {
                _label.Text += "  (no volumetric data)";
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Rendering
        // ──────────────────────────────────────────────────────────────────

        private void RenderFrame(bool lowQuality = false)
        {
            int w = Math.Max(_picture.Width,  64);
            int h = Math.Max(_picture.Height, 64);
            try
            {
                var pos = _showPos ? _posTriangles : null;
                var neg = _showNeg ? _negTriangles : null;
                var bmp = IsosurfaceRenderer.Render(
                    _molecule, pos, neg, _rotMatrix, w, h, _background, lowQuality, _zoomFactor);
                var old = _picture.Image;
                _picture.Image = bmp;
                old?.Dispose();
            }
            catch
            {
                // Keep last frame on render error
            }
        }

        private void ExtractTriangles()
        {
            if (_grid == null) { _posTriangles = null; _negTriangles = null; return; }
            try
            {
                _posTriangles = _showPos ? MarchingCubes.Extract(_grid, _isovalue, true)  : null;
                _negTriangles = _showNeg ? MarchingCubes.Extract(_grid, _isovalue, false) : null;
            }
            catch
            {
                _posTriangles = null;
                _negTriangles = null;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Animation & drag
        // ──────────────────────────────────────────────────────────────────

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

        // ──────────────────────────────────────────────────────────────────
        // Bottom panel controls
        // ──────────────────────────────────────────────────────────────────

        private void BuildBottomPanel()
        {
            int x = 6;
            const int cy = 6;

            // ☑ + (green)
            _chkPos = new CheckBox
            {
                Text      = "+",
                Checked   = true,
                ForeColor = Color.DarkGreen,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize  = true,
                Location  = new Point(x, cy),
            };
            _chkPos.CheckedChanged += OnLobeCheckChanged;
            _bottomPanel.Controls.Add(_chkPos);
            x += _chkPos.PreferredSize.Width + 8;

            // ☑ − (red)
            _chkNeg = new CheckBox
            {
                Text      = "−",
                Checked   = true,
                ForeColor = Color.DarkRed,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize  = true,
                Location  = new Point(x, cy),
            };
            _chkNeg.CheckedChanged += OnLobeCheckChanged;
            _bottomPanel.Controls.Add(_chkNeg);
            x += _chkNeg.PreferredSize.Width + 10;

            // "Isovalue:" label
            var isoLbl = new Label
            {
                Text      = "Isovalue:",
                AutoSize  = true,
                Location  = new Point(x, cy + 2),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(isoLbl);
            x += isoLbl.PreferredSize.Width + 4;

            // Slider: 1–200, default 20  (÷1000 → 0.001–0.200)
            _isoSlider = new TrackBar
            {
                Minimum  = 1,
                Maximum  = 200,
                Value    = 20,
                Width    = 100,
                Height   = 22,
                Location = new Point(x, cy - 2),
                TickFrequency = 20,
                LargeChange   = 10,
            };
            _isoSlider.Scroll += OnIsoSliderScroll;
            _bottomPanel.Controls.Add(_isoSlider);
            x += _isoSlider.Width + 4;

            // Numeric label
            _isoLabel = new Label
            {
                Text      = "0.020",
                AutoSize  = true,
                Location  = new Point(x, cy + 2),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 9f),
            };
            _bottomPanel.Controls.Add(_isoLabel);
            x += 52;

            // BG button
            _bgButton = new Button
            {
                Text      = "BG",
                Width     = 36,
                Height    = 22,
                Location  = new Point(x, cy),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
            };
            _bgButton.FlatAppearance.BorderColor = Color.Silver;
            _bgButton.Click += OnBgButtonClick;
            _bottomPanel.Controls.Add(_bgButton);
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
            RenderFrame();
        }

        private void OnIsoSliderScroll(object sender, EventArgs e)
        {
            _isovalue = _isoSlider.Value / 1000f;
            _isoLabel.Text = _isovalue.ToString("0.000");
            ExtractTriangles();
            RenderFrame();
        }

        private void OnBgButtonClick(object sender, EventArgs e)
        {
            using (var dlg = new ColorDialog { Color = _background, FullOpen = true })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    ApplyBackground(dlg.Color);
            }
        }

        /// <summary>
        /// Applies a new background colour and updates text colours throughout
        /// so that the label and controls remain readable on any background.
        /// Light background → dark text.  Dark background → light text.
        /// </summary>
        private void ApplyBackground(Color bg)
        {
            _background = bg;
            _picture.BackColor = bg;
            BackColor = bg;

            // Binary black/white text based on perceived luminance (WCAG formula)
            float lum     = 0.299f * bg.R + 0.587f * bg.G + 0.114f * bg.B;
            Color textColor = lum > 128f ? Color.Black : Color.White;
            Color panelBg   = lum > 128f ? Color.FromArgb(220, 220, 225) : Color.FromArgb(10, 10, 20);

            _label.ForeColor       = textColor;
            _label.BackColor       = panelBg;
            _bottomPanel.BackColor = panelBg;

            // Update labels and buttons in the bottom panel (CheckBoxes keep their semantic green/red)
            foreach (Control c in _bottomPanel.Controls)
            {
                if (c is Label lbl) lbl.ForeColor = textColor;
                if (c is Button btn) btn.ForeColor = textColor;
            }

            RenderFrame();
        }

        // ──────────────────────────────────────────────────────────────────
        // Dispose
        // ──────────────────────────────────────────────────────────────────

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

        // ──────────────────────────────────────────────────────────────────
        // Arcball helpers (shared with MoleculePreviewControl)
        // ──────────────────────────────────────────────────────────────────

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
