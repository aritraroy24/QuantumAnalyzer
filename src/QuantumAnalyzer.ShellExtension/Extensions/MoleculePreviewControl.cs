using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
    /// </summary>
    public class MoleculePreviewControl : PreviewHandlerControl
    {
        private PictureBox _picture;
        private Label _label;
        private Panel _titleSeparator;
        private Panel _infoPanel;
        private Label _infoLeftHeader;
        private Label _infoMidHeader;
        private Label _infoRightHeader;
        private Panel _infoLeftDivider;
        private Panel _infoMidDivider;
        private Panel _infoRightDivider;
        private Label _infoLeft;
        private Label _infoMid;
        private Label _infoRight;
        private PictureBox _energyGraphPicture;
        private Panel _bottomPanel;
        private Button _bgButton;
        private Button _prevBtn;
        private Button _nextBtn;
        private TrackBar _frameSlider;
        private Label _frameNameLabel;
        private Label _frameValueLabel;
        private Timer _timer;
        private Molecule _molecule;
        private List<Molecule> _moleculeFrames;
        private List<string> _moleculeFrameNames;
        private int _currentFrameIndex;
        private string _baseLabel;
        private QuantumSummary _summary;
        private string _sourcePath;
        private List<double> _optimizationStepEnergiesEv;
        private int _selectedOptimizationStep;
        private bool _usingOptimizationStepNavigation;
        private float _fixedScale = 0f;
        private float _zoomFactor = 1.0f;
        private Color _background = Color.FromArgb(18, 18, 30);
        private const float StepDeg = 0.5f;
        private const int TickMs = 33;
        private const double HartreeToEv = 27.211385;

        private float[,] _rotMatrix;

        private bool _isDragging;
        private float[,] _dragBaseMatrix;
        private float[] _dragStartVec;

        public MoleculePreviewControl()
        {
            BackColor = Color.FromArgb(18, 18, 30);
            DoubleBuffered = true;

            _label = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = Color.FromArgb(180, 180, 200),
                BackColor = Color.FromArgb(10, 10, 20),
                Font = new Font("Segoe UI", 9f),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Text = "Loading...",
                Padding = new Padding(4, 0, 4, 0),
            };

            _titleSeparator = new Panel
            {
                Dock = DockStyle.Top,
                Height = 10,
                BackColor = Color.FromArgb(10, 10, 20),
            };
            _titleSeparator.Paint += (s, e) =>
            {
                int y1 = 2;
                int y2 = 4;
                using (var p1 = new Pen(Color.FromArgb(95, 95, 120), 1f))
                using (var p2 = new Pen(Color.FromArgb(55, 55, 75), 1f))
                {
                    e.Graphics.DrawLine(p1, 0, y1, _titleSeparator.Width, y1);
                    e.Graphics.DrawLine(p2, 0, y2, _titleSeparator.Width, y2);
                }
            };

            _picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.CenterImage,
                BackColor = Color.FromArgb(18, 18, 30),
            };

            _infoPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 0,
                BackColor = Color.FromArgb(8, 8, 16),
                Visible = false,
            };

            _infoLeftHeader = CreateInfoHeader("GENERAL");
            _infoMidHeader = CreateInfoHeader("MODEL");
            _infoRightHeader = CreateInfoHeader("ENERGY");
            _infoLeftDivider = CreateInfoDivider();
            _infoMidDivider = CreateInfoDivider();
            _infoRightDivider = CreateInfoDivider();

            _infoLeft = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Consolas", 8f),
                TextAlign = ContentAlignment.TopLeft,
            };
            _infoMid = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Consolas", 8f),
                TextAlign = ContentAlignment.TopLeft,
            };
            _infoRight = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Consolas", 8f),
                TextAlign = ContentAlignment.TopLeft,
            };
            _infoPanel.Controls.Add(_infoLeftHeader);
            _infoPanel.Controls.Add(_infoMidHeader);
            _infoPanel.Controls.Add(_infoRightHeader);
            _infoPanel.Controls.Add(_infoLeftDivider);
            _infoPanel.Controls.Add(_infoMidDivider);
            _infoPanel.Controls.Add(_infoRightDivider);
            _infoPanel.Controls.Add(_infoLeft);
            _infoPanel.Controls.Add(_infoMid);
            _infoPanel.Controls.Add(_infoRight);
            _infoPanel.Resize += (s, e) =>
            {
                int pad = 8;
                int gap = 12;
                int third = Math.Max(40, (_infoPanel.ClientSize.Width - (pad * 2) - (gap * 2)) / 3);
                int headerH = 16;
                int dividerH = 1;
                int bodyY = 6 + headerH + 4 + dividerH + 5;
                int colH = Math.Max(10, _infoPanel.ClientSize.Height - bodyY - 4);

                int x1 = pad;
                int x2 = pad + third + gap;
                int x3 = pad + (third * 2) + (gap * 2);

                _infoLeftHeader.SetBounds(x1, 6, third, headerH);
                _infoMidHeader.SetBounds(x2, 6, third, headerH);
                _infoRightHeader.SetBounds(x3, 6, third, headerH);

                _infoLeftDivider.SetBounds(x1, 6 + headerH + 4, third, dividerH);
                _infoMidDivider.SetBounds(x2, 6 + headerH + 4, third, dividerH);
                _infoRightDivider.SetBounds(x3, 6 + headerH + 4, third, dividerH);

                _infoLeft.SetBounds(x1, bodyY, third, colH);
                _infoMid.SetBounds(x2, bodyY, third, colH);
                _infoRight.SetBounds(x3, bodyY, third, colH);
            };

            _energyGraphPicture = new PictureBox
            {
                Dock = DockStyle.Bottom,
                Height = 0,
                BackColor = Color.FromArgb(12, 12, 24),
                Visible = false,
            };
            _energyGraphPicture.MouseDown += OnEnergyGraphClick;

            _bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                BackColor = Color.FromArgb(10, 10, 20),
            };

            _prevBtn = new Button
            {
                Text = "<",
                Width = 24,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
                Visible = false,
            };
            _prevBtn.FlatAppearance.BorderColor = Color.Gray;
            _prevBtn.Click += (s, e) => ChangeFrameBy(-1);
            _bottomPanel.Controls.Add(_prevBtn);

            _nextBtn = new Button
            {
                Text = ">",
                Width = 24,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
                Visible = false,
            };
            _nextBtn.FlatAppearance.BorderColor = Color.Gray;
            _nextBtn.Click += (s, e) => ChangeFrameBy(+1);
            _bottomPanel.Controls.Add(_nextBtn);

            _bgButton = new Button
            {
                Text = "BG",
                Width = 36,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgButton.FlatAppearance.BorderColor = Color.Silver;
            _bgButton.Click += OnBgButtonClick;
            _bottomPanel.Controls.Add(_bgButton);

            _frameNameLabel = new Label
            {
                AutoSize = false,
                Width = 108,
                Height = 18,
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Segoe UI", 8f),
                TextAlign = ContentAlignment.MiddleLeft,
                Visible = false,
            };
            _bottomPanel.Controls.Add(_frameNameLabel);

            _frameSlider = new TrackBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                TickStyle = TickStyle.None,
                Height = 26,
                Visible = false,
            };
            _frameSlider.Scroll += OnFrameSliderScroll;
            _bottomPanel.Controls.Add(_frameSlider);

            _frameValueLabel = new Label
            {
                AutoSize = false,
                Width = 72,
                Height = 18,
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Segoe UI", 8f),
                TextAlign = ContentAlignment.MiddleRight,
                Visible = false,
            };
            _bottomPanel.Controls.Add(_frameValueLabel);

            _bottomPanel.Resize += (s, e) => LayoutBottomPanel();

            Controls.Add(_picture);
            Controls.Add(_energyGraphPicture);
            Controls.Add(_bottomPanel);
            Controls.Add(_infoPanel);
            Controls.Add(_titleSeparator);
            Controls.Add(_label);

            _picture.MouseDown += OnPictureMouseDown;
            _picture.MouseMove += OnPictureMouseMove;
            _picture.MouseUp += OnPictureMouseUp;
            _picture.MouseEnter += (s, e) => this.Focus();

            _timer = new Timer { Interval = TickMs };
            _timer.Tick += OnTick;

            _rotMatrix = InitialRotation();
        }

        /// <summary>Called by QuantumPreviewHandler after it parses the file.</summary>
        public void SetMolecule(
            Molecule molecule,
            string displayLabel,
            QuantumSummary summary = null,
            string sourcePath = null,
            List<double> optimizationStepEnergiesEv = null)
        {
            _moleculeFrames = null;
            _moleculeFrameNames = null;
            _baseLabel = displayLabel;
            _currentFrameIndex = 0;
            _summary = summary;
            _sourcePath = sourcePath;
            _optimizationStepEnergiesEv = optimizationStepEnergiesEv;
            _selectedOptimizationStep = (_optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 0)
                ? _optimizationStepEnergiesEv.Count - 1
                : 0;

            ConfigureFrameUi(false, _optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 1);
            _molecule = molecule;
            ApplyInfoSummary();
            ConfigureEnergyGraphUi();
            UpdateTitle();
            StartOrStopRendering(resetRotation: true);
        }

        /// <summary>Sets trajectory-like multiple molecules (used for multi-structure XYZ).</summary>
        public void SetMolecules(
            List<Molecule> molecules,
            List<string> frameNames,
            string displayLabel,
            QuantumSummary summary = null,
            string sourcePath = null,
            List<double> optimizationStepEnergiesEv = null)
        {
            _moleculeFrames = molecules;
            _moleculeFrameNames = frameNames;
            _baseLabel = displayLabel;
            _currentFrameIndex = 0;
            _summary = summary;
            _sourcePath = sourcePath;
            _optimizationStepEnergiesEv = optimizationStepEnergiesEv;
            _selectedOptimizationStep = (_optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 0)
                ? _optimizationStepEnergiesEv.Count - 1
                : 0;

            bool hasMultiple = _moleculeFrames != null && _moleculeFrames.Count > 1;
            ConfigureFrameUi(hasMultiple, !hasMultiple && _optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 1);
            ApplyInfoSummary();
            ConfigureEnergyGraphUi();

            if (_moleculeFrames == null || _moleculeFrames.Count == 0)
            {
                _molecule = null;
                UpdateTitle();
                StartOrStopRendering(resetRotation: true);
                return;
            }

            SetCurrentFrame(0, resetRotation: true);
        }

        private void ConfigureFrameUi(bool showFrames, bool showOptimizationSteps)
        {
            _usingOptimizationStepNavigation = !showFrames && showOptimizationSteps;
            bool show = showFrames || _usingOptimizationStepNavigation;
            _frameSlider.Visible = show;
            _prevBtn.Visible = show;
            _nextBtn.Visible = show;
            _frameNameLabel.Visible = show;
            _frameValueLabel.Visible = show;
            _bottomPanel.Height = show ? 90 : 38;
            LayoutBottomPanel();

            if (showFrames && _moleculeFrames != null && _moleculeFrames.Count > 1)
            {
                _frameSlider.Minimum = 0;
                _frameSlider.Maximum = _moleculeFrames.Count - 1;
                _frameSlider.Value = Math.Min(_frameSlider.Maximum, _currentFrameIndex);
                _frameNameLabel.Text = "Frame";
                _frameValueLabel.Text = (_currentFrameIndex + 1) + "/" + _moleculeFrames.Count;
            }
            else if (_usingOptimizationStepNavigation && _optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 1)
            {
                _frameSlider.Minimum = 0;
                _frameSlider.Maximum = _optimizationStepEnergiesEv.Count - 1;
                _frameSlider.Value = Math.Max(0, Math.Min(_frameSlider.Maximum, _selectedOptimizationStep));
                _frameNameLabel.Text = "Step";
                _frameValueLabel.Text = (_frameSlider.Value + 1) + "/" + _optimizationStepEnergiesEv.Count;
            }
        }

        private void LayoutBottomPanel()
        {
            int panelW = Math.Max(_bottomPanel.ClientSize.Width, 120);
            if (!_frameSlider.Visible)
            {
                _bgButton.Location = new Point(6, 8);
                return;
            }

            const int navY = 6;
            const int sliderY = 30;
            const int bgY = 62;
            const int left = 6;
            const int gap = 4;
            const int right = 6;

            _prevBtn.Location = new Point(left, navY);
            _nextBtn.Location = new Point(_prevBtn.Right + gap, navY);
            _frameNameLabel.Location = new Point(_nextBtn.Right + 6, navY + 2);
            _frameNameLabel.Width = Math.Max(80, Math.Min(180, panelW / 3));

            _frameValueLabel.Location = new Point(panelW - right - _frameValueLabel.Width, navY + 2);
            _frameSlider.Location = new Point(left, sliderY);
            _frameSlider.Width = Math.Max(90, panelW - left - right);

            _bgButton.Location = new Point(left, bgY);
        }

        private void SetCurrentFrame(int index, bool resetRotation)
        {
            if (_moleculeFrames == null || _moleculeFrames.Count == 0) return;
            if (index < 0) index = 0;
            if (index >= _moleculeFrames.Count) index = _moleculeFrames.Count - 1;

            _currentFrameIndex = index;
            _molecule = _moleculeFrames[index];

            if (_frameSlider.Visible && _frameSlider.Value != index)
                _frameSlider.Value = index;

            if (_frameSlider.Visible)
            {
                _frameValueLabel.Text = (index + 1).ToString() + "/" + _moleculeFrames.Count;
                _frameNameLabel.Text = "Frame";
                string frameName = GetFrameName(index);
                if (!string.IsNullOrEmpty(frameName))
                    _frameNameLabel.Text = frameName;
            }

            if (_optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 1)
                _selectedOptimizationStep = System.Math.Max(0, System.Math.Min(_optimizationStepEnergiesEv.Count - 1, index));

            UpdateTitle();
            StartOrStopRendering(resetRotation);
        }

        private string GetFrameName(int index)
        {
            if (_moleculeFrameNames == null || index < 0 || index >= _moleculeFrameNames.Count)
                return null;
            string name = _moleculeFrameNames[index];
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        private void UpdateTitle()
        {
            string fileName = !string.IsNullOrEmpty(_sourcePath) ? Path.GetFileName(_sourcePath) : null;
            if (!string.IsNullOrEmpty(fileName))
                _label.Text = fileName;
            else if (!string.IsNullOrEmpty(_baseLabel))
                _label.Text = _baseLabel;
            else
                _label.Text = "Molecule";
        }

        private void StartOrStopRendering(bool resetRotation)
        {
            if (resetRotation)
                _rotMatrix = InitialRotation();

            if (_molecule != null && _molecule.HasGeometry)
            {
                int w = Math.Max(_picture.Width, 64);
                int h = Math.Max(_picture.Height, 64);
                _fixedScale = MoleculeRenderer.ComputeFixedScale(_molecule, w, h);
                _timer.Start();
                RenderFrame();
            }
            else
            {
                _timer.Stop();
                RenderFrame();
            }
        }

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
            _isDragging = true;
            _dragBaseMatrix = (float[,])_rotMatrix.Clone();
            _dragStartVec = ArcballVec(e.X, e.Y);
            _timer.Stop();
            _picture.Cursor = Cursors.SizeAll;
        }

        private void OnPictureMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            float[] cur = ArcballVec(e.X, e.Y);
            float[,] delta = ArcballRotation(_dragStartVec, cur);
            _rotMatrix = MatMul3(delta, _dragBaseMatrix);
            RenderFrame(lowQuality: true);
        }

        private void OnPictureMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !_isDragging) return;
            _isDragging = false;
            _picture.Cursor = Cursors.Default;
            if (_molecule != null && _molecule.HasGeometry)
            {
                RenderFrame();
                _timer.Start();
            }
        }

        private void OnFrameSliderScroll(object sender, EventArgs e)
        {
            if (!_frameSlider.Visible) return;
            if (_usingOptimizationStepNavigation)
            {
                SetSelectedOptimizationStep(_frameSlider.Value, updateSlider: false);
            }
            else
            {
                SetCurrentFrame(_frameSlider.Value, resetRotation: false);
            }
        }

        private void ChangeFrameBy(int delta)
        {
            if (!_frameSlider.Visible) return;
            int next = _frameSlider.Value + delta;
            next = Math.Max(_frameSlider.Minimum, Math.Min(_frameSlider.Maximum, next));
            if (next == _frameSlider.Value) return;

            if (_usingOptimizationStepNavigation)
            {
                SetSelectedOptimizationStep(next, updateSlider: true);
            }
            else
            {
                SetCurrentFrame(next, resetRotation: false);
            }
        }

        private void SetSelectedOptimizationStep(int step, bool updateSlider)
        {
            if (_optimizationStepEnergiesEv == null || _optimizationStepEnergiesEv.Count < 2) return;
            if (step < 0) step = 0;
            if (step >= _optimizationStepEnergiesEv.Count) step = _optimizationStepEnergiesEv.Count - 1;
            _selectedOptimizationStep = step;

            if (_usingOptimizationStepNavigation)
            {
                if (updateSlider && _frameSlider.Value != step)
                    _frameSlider.Value = step;
                _frameNameLabel.Text = "Step";
                _frameValueLabel.Text = (step + 1) + "/" + _optimizationStepEnergiesEv.Count;
            }

            RenderEnergyGraph();
        }

        private void OnEnergyGraphClick(object sender, MouseEventArgs e)
        {
            if (_optimizationStepEnergiesEv == null || _optimizationStepEnergiesEv.Count < 2)
                return;

            int n = _optimizationStepEnergiesEv.Count;
            const int ml = 62;
            const int mr = 24;
            int chartW = _energyGraphPicture.Width - ml - mr;
            if (chartW <= 0) return;

            float relX = e.X - ml;
            int step = (int)System.Math.Round(relX / chartW * (n - 1));
            step = System.Math.Max(0, System.Math.Min(n - 1, step));
            if (_usingOptimizationStepNavigation)
            {
                SetSelectedOptimizationStep(step, updateSlider: true);
            }
            else if (_moleculeFrames != null && _moleculeFrames.Count > 1)
            {
                SetCurrentFrame(System.Math.Min(step, _moleculeFrames.Count - 1), resetRotation: false);
            }
            else
            {
                SetSelectedOptimizationStep(step, updateSlider: false);
            }
        }

        private void RenderFrame(bool lowQuality = false)
        {
            if (_molecule == null || !_molecule.HasGeometry) return;
            int w = Math.Max(_picture.Width, 64);
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
            }

            RenderEnergyGraph();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_molecule != null && _molecule.HasGeometry)
            {
                int w = Math.Max(_picture.Width, 64);
                int h = Math.Max(_picture.Height, 64);
                _fixedScale = MoleculeRenderer.ComputeFixedScale(_molecule, w, h);
            }
            LayoutBottomPanel();
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
            Color panelBg = lum > 128f ? Color.FromArgb(220, 220, 225) : Color.FromArgb(10, 10, 20);

            _label.ForeColor = textColor;
            _label.BackColor = panelBg;
            _titleSeparator.BackColor = panelBg;
            _infoPanel.BackColor = panelBg;
            _bottomPanel.BackColor = panelBg;
            _bgButton.ForeColor = textColor;
            _prevBtn.ForeColor = textColor;
            _nextBtn.ForeColor = textColor;
            _frameNameLabel.ForeColor = textColor;
            _frameValueLabel.ForeColor = textColor;
            _infoLeftHeader.ForeColor = textColor;
            _infoMidHeader.ForeColor = textColor;
            _infoRightHeader.ForeColor = textColor;
            Color dividerColor = Color.FromArgb(
                Math.Max(0, textColor.R - 35),
                Math.Max(0, textColor.G - 35),
                Math.Max(0, textColor.B - 35));
            _infoLeftDivider.BackColor = dividerColor;
            _infoMidDivider.BackColor = dividerColor;
            _infoRightDivider.BackColor = dividerColor;
            _infoLeft.ForeColor = textColor;
            _infoMid.ForeColor = textColor;
            _infoRight.ForeColor = textColor;
            _energyGraphPicture.BackColor = bg;
            _titleSeparator.Invalidate();

            RenderFrame();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _picture?.Image?.Dispose();
                _energyGraphPicture?.Image?.Dispose();
                _energyGraphPicture?.Dispose();
                _picture?.Dispose();
                _titleSeparator?.Dispose();
                _infoPanel?.Dispose();
                _label?.Dispose();
                _bottomPanel?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ApplyInfoSummary()
        {
            if (_summary == null)
            {
                _infoPanel.Visible = false;
                _infoPanel.Height = 0;
                return;
            }

            bool isOutput = !string.IsNullOrEmpty(_sourcePath)
                && (string.Equals(System.IO.Path.GetExtension(_sourcePath), ".log", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(System.IO.Path.GetExtension(_sourcePath), ".out", StringComparison.OrdinalIgnoreCase));

            var general = new List<string>();
            var model = new List<string>();
            var energy = new List<string>();

            AddField(general, "Software", _summary.Software.ToString());
            AddField(general, "Type", _summary.CalcType);
            AddField(general, "Charge", _summary.Charge.ToString());
            AddField(general, "Spin", _summary.Spin);
            AddField(general, "Status", _summary.NormalTermination ? "Normal" : "Incomplete");

            AddField(model, "Method", _summary.Method);
            AddField(model, "Basis", _summary.BasisSet);
            AddField(model, "Solvation", _summary.Solvation);
            if (_summary.ImaginaryFreq > 0)
                AddField(model, "ImagFreq", _summary.ImaginaryFreq.ToString());

            if (_summary.ElectronicEnergy.HasValue)
                AddField(energy, "E(total)", _summary.ElectronicEnergy.Value.ToString("F6") + " Eh");
            if (_summary.HomoIndex.HasValue) AddField(energy, "HOMO", _summary.HomoIndex.Value.ToString());
            if (_summary.LumoIndex.HasValue) AddField(energy, "LUMO", _summary.LumoIndex.Value.ToString());
            if (_summary.HomoLumoGap.HasValue) AddField(energy, "Gap", _summary.HomoLumoGap.Value.ToString("F3") + " eV");
            if (_optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 1)
            {
                string unit = GetProfileEnergyUnit();
                var profileEnergies = GetProfileEnergiesForDisplay();
                AddField(energy, "OptSteps", _optimizationStepEnergiesEv.Count.ToString());
                AddField(energy, "Final(" + unit + ")", profileEnergies[profileEnergies.Count - 1].ToString("F6"));
            }

            _infoLeft.Text = string.Join(Environment.NewLine, general);
            _infoMid.Text = string.Join(Environment.NewLine, model);
            _infoRight.Text = string.Join(Environment.NewLine, energy);

            int lineCount = Math.Max(general.Count, Math.Max(model.Count, energy.Count));
            _infoPanel.Visible = lineCount > 0;
            _infoPanel.Height = lineCount > 0 ? Math.Max(isOutput ? 94 : 82, 36 + (lineCount * 14)) : 0;
            _infoPanel.PerformLayout();
        }

        private static Label CreateInfoHeader(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(210, 210, 220),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            };
        }

        private static Panel CreateInfoDivider()
        {
            return new Panel
            {
                BackColor = Color.FromArgb(80, 80, 105),
                Height = 1,
            };
        }

        private static void AddField(List<string> list, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            const int keyWidth = 9;
            string clippedKey = key ?? string.Empty;
            if (clippedKey.Length > keyWidth) clippedKey = clippedKey.Substring(0, keyWidth);
            list.Add(clippedKey.PadRight(keyWidth) + " : " + value.Trim());
        }

        private void ConfigureEnergyGraphUi()
        {
            bool show = _optimizationStepEnergiesEv != null && _optimizationStepEnergiesEv.Count > 1;
            _energyGraphPicture.Visible = show;
            _energyGraphPicture.Height = show ? 130 : 0;
            bool canNavigate = _usingOptimizationStepNavigation || (_moleculeFrames != null && _moleculeFrames.Count > 1);
            _energyGraphPicture.Cursor = show && canNavigate ? Cursors.Hand : Cursors.Default;
        }

        private void RenderEnergyGraph()
        {
            if (!_energyGraphPicture.Visible || _optimizationStepEnergiesEv == null || _optimizationStepEnergiesEv.Count < 2)
                return;

            int w = Math.Max(_energyGraphPicture.Width, 64);
            int h = Math.Max(_energyGraphPicture.Height, 64);
            if (w < 10 || h < 10) return;

            var profileEnergies = GetProfileEnergiesForDisplay();
            var data = new OutcarData { StepEnergies = profileEnergies };
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(_background);
                int selectedStep = _selectedOptimizationStep;
                if (_moleculeFrames != null && _moleculeFrames.Count > 1)
                    selectedStep = System.Math.Max(0, System.Math.Min(data.StepEnergies.Count - 1, _currentFrameIndex));
                else if (!_usingOptimizationStepNavigation)
                    selectedStep = data.StepEnergies.Count - 1;
                OutcarPreviewControl.DrawEnergyGraph(
                    g, w, h, data, selectedStep, _background, GetProfileEnergyUnit());
            }
            var old = _energyGraphPicture.Image;
            _energyGraphPicture.Image = bmp;
            old?.Dispose();
        }

        private List<double> GetProfileEnergiesForDisplay()
        {
            var values = new List<double>();
            if (_optimizationStepEnergiesEv == null) return values;

            bool useAu = _summary != null &&
                (_summary.Software == SoftwareType.Gaussian || _summary.Software == SoftwareType.Orca);
            if (!useAu)
            {
                values.AddRange(_optimizationStepEnergiesEv);
                return values;
            }

            foreach (double eEv in _optimizationStepEnergiesEv)
                values.Add(eEv / HartreeToEv);
            return values;
        }

        private string GetProfileEnergyUnit()
        {
            if (_summary != null &&
                (_summary.Software == SoftwareType.Gaussian || _summary.Software == SoftwareType.Orca))
                return "au";
            return "eV";
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

        private static float[,] InitialRotation()
        {
            float rx = 8f * (float)(Math.PI / 180.0);
            float c = (float)Math.Cos(rx);
            float s = (float)Math.Sin(rx);
            return new float[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
        }
    }
}
