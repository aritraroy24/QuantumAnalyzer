using System;
using System.Drawing;
using System.Windows.Forms;
using SharpShell.SharpPreviewHandler;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Rendering;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// WinForms control that displays a live rotating 3D molecule.
    /// Inherits PreviewHandlerControl so SharpShell can host it directly
    /// inside the Explorer Preview Pane (Alt+P).
    ///
    /// Auto-rotation: spins around Y.
    /// Right-click drag: full arcball rotation (X, Y and Z axes).
    ///   - Drag inside the virtual sphere → X/Y rotation.
    ///   - Drag near/outside the sphere edge → Z roll.
    /// </summary>
    public class MoleculePreviewControl : PreviewHandlerControl
    {
        private PictureBox _picture;
        private Label      _label;
        private Panel      _bottomPanel;
        private Button     _bgButton;
        private Timer      _timer;
        private Molecule   _molecule;
        private float      _fixedScale = 0f;
        private float      _zoomFactor = 1.0f;
        private Color      _background = Color.FromArgb(18, 18, 30);
        private const float StepDeg = 0.5f;   // degrees per tick for auto-rotation
        private const int   TickMs  = 33;      // ~30 fps

        // Accumulated rotation expressed as a 3×3 matrix in PCA-aligned space.
        // Initialized with a slight X tilt so the molecule has visible depth.
        private float[,] _rotMatrix;

        // Right-click drag state
        private bool     _isDragging;
        private float[,] _dragBaseMatrix;   // _rotMatrix snapshot at drag start
        private float[]  _dragStartVec;     // arcball sphere vector at drag start

        public MoleculePreviewControl()
        {
            BackColor     = Color.FromArgb(18, 18, 30);
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
                BackColor = Color.FromArgb(18, 18, 30),
            };

            _bottomPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 32,
                BackColor = Color.FromArgb(10, 10, 20),
            };

            _bgButton = new Button
            {
                Text      = "BG",
                Width     = 36,
                Height    = 22,
                Location  = new System.Drawing.Point(6, 5),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgButton.FlatAppearance.BorderColor = Color.Silver;
            _bgButton.Click += OnBgButtonClick;
            _bottomPanel.Controls.Add(_bgButton);

            Controls.Add(_picture);
            Controls.Add(_bottomPanel);
            Controls.Add(_label);   // added last so it docks on top

            _picture.MouseDown  += OnPictureMouseDown;
            _picture.MouseMove  += OnPictureMouseMove;
            _picture.MouseUp    += OnPictureMouseUp;
            _picture.MouseEnter += (s, e) => this.Focus();  // allow parent to receive scroll

            _timer = new Timer { Interval = TickMs };
            _timer.Tick += OnTick;

            _rotMatrix = InitialRotation();
        }

        /// <summary>Called by QuantumPreviewHandler after it parses the file.</summary>
        public void SetMolecule(Molecule molecule, string displayLabel)
        {
            _molecule = molecule;
            _label.Text = displayLabel;

            if (molecule != null && molecule.HasGeometry)
            {
                _rotMatrix = InitialRotation();
                int w = Math.Max(_picture.Width,  64);
                int h = Math.Max(_picture.Height, 64);
                _fixedScale = MoleculeRenderer.ComputeFixedScale(molecule, w, h);
                _timer.Start();
                RenderFrame();
            }
            else
            {
                _label.Text += "  (no geometry)";
            }
        }

        // ──────────────────────────────────────────────────────────────────

        private void OnTick(object sender, EventArgs e)
        {
            // Spin around Y in PCA space (left-multiply keeps it a consistent world-Y spin)
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
            // Left-multiply: apply delta in current view space, on top of the base rotation
            _rotMatrix = MatMul3(delta, _dragBaseMatrix);
            RenderFrame(lowQuality: true);  // flat atoms during drag — much faster for large molecules
        }

        private void OnPictureMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !_isDragging) return;
            _isDragging     = false;
            _picture.Cursor = Cursors.Default;
            if (_molecule != null && _molecule.HasGeometry)
            {
                RenderFrame();      // restore full glossy quality on release
                _timer.Start();
            }
        }

        private void RenderFrame(bool lowQuality = false)
        {
            if (_molecule == null || !_molecule.HasGeometry) return;
            int w = Math.Max(_picture.Width,  64);
            int h = Math.Max(_picture.Height, 64);
            try
            {
                float? scale = _fixedScale > 0f ? _fixedScale * _zoomFactor : (float?)null;
                var bmp = MoleculeRenderer.RenderWithMatrix(_molecule, _rotMatrix, w, h, scale, lowQuality, _background);
                var old = _picture.Image;
                _picture.Image = bmp;
                old?.Dispose();
            }
            catch
            {
                // Ignore render errors — keep the last frame
            }
        }

        // ──────────────────────────────────────────────────────────────────

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_molecule != null && _molecule.HasGeometry)
            {
                int w = Math.Max(_picture.Width,  64);
                int h = Math.Max(_picture.Height, 64);
                _fixedScale = MoleculeRenderer.ComputeFixedScale(_molecule, w, h);
            }
            RenderFrame();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _zoomFactor *= e.Delta > 0 ? 1.1f : (1f / 1.1f);
            if (_zoomFactor < 0.2f) _zoomFactor = 0.2f;
            if (_zoomFactor > 5.0f) _zoomFactor = 5.0f;
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
            _bgButton.ForeColor    = textColor;

            RenderFrame();
        }

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
        // Arcball helpers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps a screen point to a point on a unit sphere.
        /// Points inside the sphere project onto the upper hemisphere (X/Y rotation).
        /// Points outside the sphere project onto the equatorial ring (Z roll).
        /// </summary>
        private float[] ArcballVec(int px, int py)
        {
            float cx = _picture.Width  / 2f;
            float cy = _picture.Height / 2f;
            float r  = Math.Min(_picture.Width, _picture.Height) / 2f * 0.85f;
            if (r < 1f) r = 1f;

            float x  = (px - cx) / r;
            float y  = (cy - py) / r;   // flip Y: up is positive on the sphere
            float d2 = x * x + y * y;

            if (d2 <= 1f)
                return new[] { x, y, (float)Math.Sqrt(1f - d2) };

            // Outside sphere — project to equator for roll rotation
            float len = (float)Math.Sqrt(d2);
            return new[] { x / len, y / len, 0f };
        }

        /// <summary>
        /// Returns the rotation matrix that maps unit vector <paramref name="from"/> to
        /// unit vector <paramref name="to"/> using Rodrigues' formula.
        /// </summary>
        private static float[,] ArcballRotation(float[] from, float[] to)
        {
            float dot = from[0]*to[0] + from[1]*to[1] + from[2]*to[2];
            dot = Math.Max(-1f, Math.Min(1f, dot));   // guard against float rounding

            // Rotation axis = cross product
            float kx = from[1]*to[2] - from[2]*to[1];
            float ky = from[2]*to[0] - from[0]*to[2];
            float kz = from[0]*to[1] - from[1]*to[0];
            float kLen = (float)Math.Sqrt(kx*kx + ky*ky + kz*kz);
            if (kLen < 1e-6f) return Identity3();   // vectors are parallel — no rotation

            kx /= kLen; ky /= kLen; kz /= kLen;

            float angle = (float)Math.Acos(dot);
            float c = (float)Math.Cos(angle);
            float s = (float)Math.Sin(angle);
            float t = 1f - c;

            // Rodrigues' rotation matrix
            return new float[,]
            {
                { t*kx*kx + c,      t*kx*ky - s*kz,  t*kx*kz + s*ky },
                { t*kx*ky + s*kz,   t*ky*ky + c,     t*ky*kz - s*kx },
                { t*kx*kz - s*ky,   t*ky*kz + s*kx,  t*kz*kz + c    },
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

        /// <summary>
        /// 8° tilt around X axis — matches the previous default angleXDeg = 8f so the
        /// initial view shows the same slightly-tilted perspective.
        /// </summary>
        private static float[,] InitialRotation()
        {
            float rx = 8f * (float)(Math.PI / 180.0);
            float c  = (float)Math.Cos(rx);
            float s  = (float)Math.Sin(rx);
            return new float[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
        }
    }
}
