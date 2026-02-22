using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using SharpShell.SharpPreviewHandler;
using QuantumAnalyzer.ShellExtension.Chemistry;
using QuantumAnalyzer.ShellExtension.Models;
using QuantumAnalyzer.ShellExtension.Rendering;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    /// <summary>
    /// Preview pane control for VASP OUTCAR files.
    ///
    /// Layout (multi-step OPT/MD/FREQ):
    ///   Top label       (24px)   — filename + calc type + energy
    ///   Structure view  (~62%)   — rotating 3D crystal (CrystalRenderer)
    ///   Energy graph    (~38%)   — energy(sigma->0) convergence chart
    ///   Bottom panel    (80px)   — Row1: [◄][Step N/M][►][TrackBar — full width]
    ///                             Row2: [☑Unit Cell] x:[-][1][+] y:[-][1][+] z:[-][1][+] [☑Vectors] [BG]
    ///
    /// Layout (single-step SP):
    ///   Top label       (24px)
    ///   Structure view  (fills)
    ///   Bottom panel    (80px)   — [BG] + crystal controls
    /// </summary>
    public class OutcarPreviewControl : PreviewHandlerControl
    {
        // ── Widgets ──────────────────────────────────────────────────────────
        private PictureBox _structurePicture;
        private PictureBox _graphPicture;
        private Label      _label;
        private Panel      _bottomPanel;

        // Row 1 — step navigation
        private Button   _bgButton;
        private Button   _prevBtn;
        private Button   _nextBtn;
        private Label    _stepLabel;
        private TrackBar _stepSlider;

        // Row 2 — crystal controls
        private CheckBox   _chkUnitCell;
        private CheckBox   _chkVectors;
        private Label[]    _supercellCountLabels = new Label[3];
        private Button[]   _btnMinus             = new Button[3];
        private Button[]   _btnPlus              = new Button[3];

        private Timer _timer;

        // ── State ─────────────────────────────────────────────────────────────
        private OutcarData  _outcarData;
        private LatticeCell _crystal;           // unit cell for current step (null if no lattice)
        private Molecule    _expandedMolecule;  // supercell-expanded molecule
        private float[,]    _rotMatrix;
        private float       _zoomFactor   = 1.0f;
        private Color       _background   = Color.FromArgb(18, 18, 30);
        private int         _selectedStep = 0;
        private int[]       _supercell    = { 1, 1, 1 };
        private bool        _showUnitCell = true;
        private bool        _showVectors  = true;
        private bool        _hasCrystal   = false;

        // Drag state
        private bool     _isDragging;
        private float[,] _dragBaseMatrix;
        private float[]  _dragStartVec;

        private const float StepDeg = 0.5f;
        private const int   TickMs  = 33;
        private const int   PanelH  = 80;

        // ── Constructor ───────────────────────────────────────────────────────

        public OutcarPreviewControl()
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

            _structurePicture = new PictureBox
            {
                SizeMode  = PictureBoxSizeMode.CenterImage,
                BackColor = _background,
            };

            _graphPicture = new PictureBox
            {
                SizeMode  = PictureBoxSizeMode.CenterImage,
                BackColor = _background,
                Cursor    = Cursors.Hand,
            };

            _bottomPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = PanelH,
                BackColor = Color.FromArgb(10, 10, 20),
            };

            BuildBottomPanel();

            Controls.Add(_structurePicture);
            Controls.Add(_graphPicture);
            Controls.Add(_bottomPanel);
            Controls.Add(_label);

            _structurePicture.MouseDown  += OnStructureMouseDown;
            _structurePicture.MouseMove  += OnStructureMouseMove;
            _structurePicture.MouseUp    += OnStructureMouseUp;
            _structurePicture.MouseEnter += (s, e) => this.Focus();

            _graphPicture.MouseDown += OnGraphClick;

            _timer = new Timer { Interval = TickMs };
            _timer.Tick += OnTick;

            _rotMatrix = InitialRotation();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void SetData(Molecule molecule, OutcarData outcarData, string displayLabel)
        {
            _outcarData = outcarData;
            _label.Text = displayLabel ?? "VASP OUTCAR";

            _hasCrystal = outcarData?.LatticeA != null &&
                          outcarData?.LatticeB != null &&
                          outcarData?.LatticeC != null;

            bool multiStep = outcarData != null && outcarData.StepPositions.Count > 1;

            // Default to final step
            _selectedStep = (outcarData != null && outcarData.StepPositions.Count > 0)
                                ? outcarData.StepPositions.Count - 1
                                : 0;

            _rotMatrix = InitialRotation();

            // Build crystal + expanded molecule for the selected (final) step
            _crystal = _hasCrystal ? BuildCrystal(_selectedStep) : null;
            if (_crystal != null)
                ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            else
                _expandedMolecule = molecule;

            // Step controls visibility
            _graphPicture.Visible = multiStep;
            _stepSlider.Visible   = multiStep;
            _prevBtn.Visible      = multiStep;
            _nextBtn.Visible      = multiStep;
            _stepLabel.Visible    = multiStep;

            if (multiStep)
            {
                _stepSlider.Minimum = 1;
                _stepSlider.Maximum = outcarData.StepPositions.Count;
                _stepSlider.Value   = outcarData.StepPositions.Count;
                _stepLabel.Text     = $"Step {_selectedStep + 1} / {outcarData.StepPositions.Count}";
            }

            // Crystal controls enabled only if we have lattice data
            _chkUnitCell.Enabled = _hasCrystal;
            _chkVectors.Enabled  = _hasCrystal;
            foreach (var b in _btnMinus) b.Enabled = _hasCrystal;
            foreach (var b in _btnPlus)  b.Enabled = _hasCrystal;

            LayoutControls();
            AdjustSliderWidth();

            if (_expandedMolecule != null && _expandedMolecule.HasGeometry)
            {
                _timer.Start();
                RenderStructure();
                if (multiStep) DrawGraph();
            }
            else
            {
                _label.Text += "  (no geometry)";
            }
        }

        // ── Crystal helpers ───────────────────────────────────────────────────

        private LatticeCell BuildCrystal(int stepIndex)
        {
            if (!_hasCrystal || _outcarData == null) return null;
            if (stepIndex >= _outcarData.StepPositions.Count) return null;

            var pos = _outcarData.StepPositions[stepIndex];
            var unitCellAtoms = new List<(string Element, double X, double Y, double Z)>();
            for (int i = 0; i < pos.Length; i++)
            {
                if (pos[i] == null) continue;
                string elem = (_outcarData.AtomElements != null && i < _outcarData.AtomElements.Length)
                    ? _outcarData.AtomElements[i] : "X";
                unitCellAtoms.Add((elem, pos[i][0], pos[i][1], pos[i][2]));
            }

            return new LatticeCell
            {
                SystemName    = "OUTCAR",
                VectorA       = _outcarData.LatticeA,
                VectorB       = _outcarData.LatticeB,
                VectorC       = _outcarData.LatticeC,
                UnitCellAtoms = unitCellAtoms,
            };
        }

        private void ExpandSupercell(int nx, int ny, int nz)
        {
            if (_crystal == null)
            {
                // No lattice — build molecule directly from step positions
                if (_outcarData == null || _outcarData.StepPositions.Count == 0) return;
                int si = Math.Min(_selectedStep, _outcarData.StepPositions.Count - 1);
                var pos = _outcarData.StepPositions[si];
                var atoms = new List<Atom>();
                for (int i = 0; i < pos.Length; i++)
                {
                    if (pos[i] == null) continue;
                    string elem = (_outcarData.AtomElements != null && i < _outcarData.AtomElements.Length)
                        ? _outcarData.AtomElements[i] : "X";
                    atoms.Add(new Atom(elem, pos[i][0], pos[i][1], pos[i][2]));
                }
                _expandedMolecule = new Molecule { Atoms = atoms, Bonds = BondDetector.Detect(atoms) };
                return;
            }

            var atomList = new List<Atom>();
            foreach (var uc in _crystal.UnitCellAtoms)
            {
                for (int ix = 0; ix < nx; ix++)
                for (int iy = 0; iy < ny; iy++)
                for (int iz = 0; iz < nz; iz++)
                {
                    double x = uc.X + ix * _crystal.VectorA[0] + iy * _crystal.VectorB[0] + iz * _crystal.VectorC[0];
                    double y = uc.Y + ix * _crystal.VectorA[1] + iy * _crystal.VectorB[1] + iz * _crystal.VectorC[1];
                    double z = uc.Z + ix * _crystal.VectorA[2] + iy * _crystal.VectorB[2] + iz * _crystal.VectorC[2];
                    atomList.Add(new Atom(uc.Element, x, y, z));
                }
            }
            _expandedMolecule = new Molecule { Atoms = atomList, Bonds = BondDetector.Detect(atomList) };
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void LayoutControls()
        {
            int w      = Math.Max(ClientSize.Width, 64);
            int h      = Math.Max(ClientSize.Height, 64);
            int labelH = _label.Height;
            int panelH = _bottomPanel.Height;
            int remain = h - labelH - panelH;
            if (remain < 4) remain = 4;

            bool multiStep = _outcarData != null && _outcarData.StepPositions.Count > 1;

            if (multiStep && _graphPicture.Visible)
            {
                int graphH  = Math.Max((int)(remain * 0.38), 40);
                int structH = remain - graphH;
                _structurePicture.SetBounds(0, labelH, w, Math.Max(structH, 4));
                _graphPicture.SetBounds(0, labelH + structH, w, Math.Max(graphH, 4));
            }
            else
            {
                _structurePicture.SetBounds(0, labelH, w, remain);
                _graphPicture.SetBounds(0, 0, 0, 0);
            }
        }

        private void AdjustSliderWidth()
        {
            if (_stepSlider == null || _bgButton == null) return;
            int panelW = Math.Max(_bottomPanel.Width, 200);
            // Slider fills all of row 1 to the right edge
            _stepSlider.Width = Math.Max(20, panelW - _stepSlider.Left - 4);
            // BG button right-aligned on row 2
            _bgButton.Left = panelW - _bgButton.Width - 4;
        }

        // ── Step selection ────────────────────────────────────────────────────

        private void SetSelectedStep(int stepIndex)
        {
            if (_outcarData == null) return;
            int n = _outcarData.StepPositions.Count;
            if (n == 0) return;

            _selectedStep = Math.Max(0, Math.Min(stepIndex, n - 1));

            // Rebuild crystal + expanded molecule for new step
            _crystal = _hasCrystal ? BuildCrystal(_selectedStep) : null;
            if (_crystal != null)
                ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            else
                ExpandSupercell(1, 1, 1);

            _stepLabel.Text = $"Step {_selectedStep + 1} / {n}";

            // Sync slider without re-firing
            if (_stepSlider.Value != _selectedStep + 1)
            {
                _stepSlider.ValueChanged -= OnSliderChanged;
                _stepSlider.Value = _selectedStep + 1;
                _stepSlider.ValueChanged += OnSliderChanged;
            }

            RenderStructure();
            DrawGraph();
        }

        // ── Bottom panel ──────────────────────────────────────────────────────

        private void BuildBottomPanel()
        {
            const int row1Y = 5;
            const int row2Y = 48;
            const int btnH  = 22;

            // ── Row 1: Step navigation + slider + BG ──────────────────────────

            _prevBtn = new Button
            {
                Text      = "◄", Width = 22, Height = btnH,
                Location  = new Point(4, row1Y),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _prevBtn.FlatAppearance.BorderColor = Color.Gray;
            _prevBtn.Click += (s, e) => SetSelectedStep(_selectedStep - 1);
            _bottomPanel.Controls.Add(_prevBtn);

            _stepLabel = new Label
            {
                Text      = "Step 1/1", Width = 80, Height = 18,
                Location  = new Point(30, row1Y + 2),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 8f),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            };
            _bottomPanel.Controls.Add(_stepLabel);

            _nextBtn = new Button
            {
                Text      = "►", Width = 22, Height = btnH,
                Location  = new Point(114, row1Y),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _nextBtn.FlatAppearance.BorderColor = Color.Gray;
            _nextBtn.Click += (s, e) => SetSelectedStep(_selectedStep + 1);
            _bottomPanel.Controls.Add(_nextBtn);

            // Slider — width set dynamically by AdjustSliderWidth()
            _stepSlider = new TrackBar
            {
                Location  = new Point(140, 2),
                Size      = new Size(100, 28),
                Minimum   = 1,
                Maximum   = 1,
                Value     = 1,
                TickStyle = TickStyle.None,
                AutoSize  = false,
            };
            _stepSlider.ValueChanged += OnSliderChanged;
            _bottomPanel.Controls.Add(_stepSlider);

            // ── Row 2: Crystal controls ────────────────────────────────────────

            _chkUnitCell = new CheckBox
            {
                Text      = "Unit Cell",
                AutoSize  = true,
                Checked   = true,
                Location  = new Point(6, row2Y),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 8f),
            };
            _chkUnitCell.CheckedChanged += (s, e) => { _showUnitCell = _chkUnitCell.Checked; RenderStructure(); };
            _bottomPanel.Controls.Add(_chkUnitCell);

            // x / y / z supercell controls
            string[] axisNames = { "x:", "y:", "z:" };
            int[]    axisX     = { 90, 172, 254 };

            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                int ax   = axisX[i];

                var lbl = new Label
                {
                    Text     = axisNames[i],
                    AutoSize = true,
                    Location = new Point(ax, row2Y + 3),
                    ForeColor = Color.FromArgb(140, 140, 160),
                    Font     = new Font("Segoe UI", 8f),
                };
                _bottomPanel.Controls.Add(lbl);

                _btnMinus[i] = new Button
                {
                    Text      = "−", Width = 18, Height = btnH,
                    Location  = new Point(ax + 16, row2Y),
                    FlatStyle = FlatStyle.Flat,
                    Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(180, 180, 200),
                };
                _btnMinus[i].FlatAppearance.BorderColor = Color.Gray;
                _btnMinus[i].Click += (s, e) => OnSupercellMinus(axis);
                _bottomPanel.Controls.Add(_btnMinus[i]);

                _supercellCountLabels[i] = new Label
                {
                    Text      = "1", Width = 20, Height = 18,
                    Location  = new Point(ax + 36, row2Y + 2),
                    ForeColor = Color.FromArgb(200, 200, 220),
                    Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                };
                _bottomPanel.Controls.Add(_supercellCountLabels[i]);

                _btnPlus[i] = new Button
                {
                    Text      = "+", Width = 18, Height = btnH,
                    Location  = new Point(ax + 57, row2Y),
                    FlatStyle = FlatStyle.Flat,
                    Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(180, 180, 200),
                };
                _btnPlus[i].FlatAppearance.BorderColor = Color.Gray;
                _btnPlus[i].Click += (s, e) => OnSupercellPlus(axis);
                _bottomPanel.Controls.Add(_btnPlus[i]);
            }

            _chkVectors = new CheckBox
            {
                Text      = "Vectors",
                AutoSize  = true,
                Checked   = true,
                Location  = new Point(340, row2Y),
                ForeColor = Color.FromArgb(180, 180, 200),
                Font      = new Font("Segoe UI", 8f),
            };
            _chkVectors.CheckedChanged += (s, e) => { _showVectors = _chkVectors.Checked; RenderStructure(); };
            _bottomPanel.Controls.Add(_chkVectors);

            // BG button — right-aligned on row 2, position set dynamically by AdjustSliderWidth()
            _bgButton = new Button
            {
                Text      = "BG", Width = 36, Height = btnH,
                Location  = new Point(400, row2Y),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(180, 180, 200),
            };
            _bgButton.FlatAppearance.BorderColor = Color.Silver;
            _bgButton.Click += OnBgButtonClick;
            _bottomPanel.Controls.Add(_bgButton);

            _bottomPanel.Resize += (s, e) => AdjustSliderWidth();
        }

        private void OnSliderChanged(object sender, EventArgs e)
        {
            if (_outcarData == null) return;
            SetSelectedStep(_stepSlider.Value - 1);
        }

        private void OnSupercellPlus(int axis)
        {
            _supercell[axis]++;
            _supercellCountLabels[axis].Text = _supercell[axis].ToString();
            _crystal = BuildCrystal(_selectedStep);
            ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            RenderStructure();
        }

        private void OnSupercellMinus(int axis)
        {
            if (_supercell[axis] <= 1) return;
            _supercell[axis]--;
            _supercellCountLabels[axis].Text = _supercell[axis].ToString();
            _crystal = BuildCrystal(_selectedStep);
            ExpandSupercell(_supercell[0], _supercell[1], _supercell[2]);
            RenderStructure();
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        private void RenderStructure(bool lowQuality = false)
        {
            if (_expandedMolecule == null || !_expandedMolecule.HasGeometry) return;
            int w = Math.Max(_structurePicture.Width,  64);
            int h = Math.Max(_structurePicture.Height, 64);
            try
            {
                Bitmap bmp;
                if (_crystal != null)
                {
                    bmp = CrystalRenderer.Render(
                        _expandedMolecule, _crystal, _supercell,
                        _rotMatrix, w, h, _background, lowQuality, _zoomFactor,
                        _showUnitCell, _showVectors);
                }
                else
                {
                    bmp = MoleculeRenderer.RenderWithMatrix(
                        _expandedMolecule, _rotMatrix, w, h, null, lowQuality, _background);
                }
                var old = _structurePicture.Image;
                _structurePicture.Image = bmp;
                old?.Dispose();
            }
            catch { }
        }

        private void DrawGraph()
        {
            if (_outcarData == null || !_graphPicture.Visible) return;
            int gw = Math.Max(_graphPicture.Width,  64);
            int gh = Math.Max(_graphPicture.Height, 64);

            var bmp = new Bitmap(gw, gh);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(_background);

                    if (_outcarData.StepEnergies.Count < 1)
                    {
                        using (var f  = new Font("Segoe UI", 8f))
                        using (var br = new SolidBrush(Color.FromArgb(120, 180, 180, 200)))
                            g.DrawString("No energy data", f, br, new PointF(10, gh / 2f - 8));
                    }
                    else
                    {
                        DrawEnergyGraph(g, gw, gh, _outcarData, _selectedStep, _background);
                    }
                }
            }
            catch { }

            var old = _graphPicture.Image;
            _graphPicture.Image = bmp;
            old?.Dispose();
        }

        /// <summary>
        /// Static helper — renders the energy convergence graph into an existing Graphics context.
        /// Called both by the preview control and by SaveEnergyProfileDialog.
        /// </summary>
        internal static void DrawEnergyGraph(
            Graphics g, int gw, int gh,
            OutcarData outcarData, int selectedStep, Color background, string energyUnit = "eV")
        {
            int n = outcarData.StepEnergies.Count;
            if (n < 1) return;
            bool isAu = string.Equals(energyUnit, "au", StringComparison.OrdinalIgnoreCase);

            float lum = 0.299f * background.R + 0.587f * background.G + 0.114f * background.B;
            Color textColor = lum > 128f ? Color.Black : Color.White;

            const int ml = 62, mr = 24, mt = 10, mb = 28;
            var chartRect = new Rectangle(ml, mt, gw - ml - mr, gh - mt - mb);
            if (chartRect.Width < 2 || chartRect.Height < 2) return;

            double minE   = outcarData.StepEnergies.Min();
            double maxE   = outcarData.StepEnergies.Max();
            double rangeE = maxE - minE;
            if (rangeE < 1e-10) { minE -= 0.001; maxE += 0.001; rangeE = 0.002; }

            Func<int, float> stepToX = step =>
                n > 1
                    ? chartRect.Left + (step - 1f) / (n - 1f) * chartRect.Width
                    : chartRect.Left + chartRect.Width / 2f;

            Func<double, float> energyToY = e =>
                chartRect.Bottom - (float)((e - minE) / rangeE * chartRect.Height);

            // Grid lines
            using (var gridPen = new Pen(Color.FromArgb(40, textColor.R, textColor.G, textColor.B), 0.5f))
            {
                gridPen.DashStyle = DashStyle.Dot;
                for (int yi = 0; yi <= 4; yi++)
                {
                    float y = chartRect.Bottom - yi * chartRect.Height / 4f;
                    g.DrawLine(gridPen, chartRect.Left, y, chartRect.Right, y);
                }
            }

            // Data points array
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
                pts[i] = new PointF(stepToX(i + 1), energyToY(outcarData.StepEnergies[i]));

            // Connecting line
            if (n > 1)
                using (var linePen = new Pen(Color.CornflowerBlue, 1.5f))
                    g.DrawLines(linePen, pts);

            // All data points (small white circles)
            foreach (var pt in pts)
                FillCircle(g, Brushes.White, pt, 3f);

            // Vertical dashed indicator for selected step
            int sel = Math.Max(0, Math.Min(selectedStep, n - 1));
            using (var selPen = new Pen(Color.FromArgb(100, 0, 255, 255), 1f))
            {
                selPen.DashStyle = DashStyle.Dash;
                g.DrawLine(selPen, pts[sel].X, chartRect.Top, pts[sel].X, chartRect.Bottom);
            }

            // Energy annotation next to the indicator line
            if (sel < outcarData.StepEnergies.Count)
            {
                string eLabel = "E = " + outcarData.StepEnergies[sel].ToString(isAu ? "F6" : "F4") + " " + energyUnit;
                using (var ef = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                using (var eb = new SolidBrush(Color.Cyan))
                {
                    SizeF ts = g.MeasureString(eLabel, ef);
                    float tx = pts[sel].X + 5f;
                    float ty = chartRect.Top + 3f;
                    if (tx + ts.Width > chartRect.Right)
                        tx = pts[sel].X - 5f - ts.Width;
                    if (tx < chartRect.Left + 6f)
                        tx = chartRect.Left + 6f;
                    g.DrawString(eLabel, ef, eb, tx, ty);
                }
            }

            // Final step point (gold)
            FillCircle(g, Brushes.Gold, pts[n - 1], 5f);

            // Selected step point (cyan) — drawn on top
            FillCircle(g, Brushes.Cyan, pts[sel], 5f);
            // If selected == final: add a cyan ring so both markers are visible
            if (sel == n - 1)
            {
                using (var cyanPen = new Pen(Color.Cyan, 1.5f))
                    g.DrawEllipse(cyanPen, pts[n - 1].X - 7, pts[n - 1].Y - 7, 14, 14);
            }

            // Y-axis labels
            using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            using (var labelFont  = new Font("Segoe UI", 7f))
            using (var labelBrush = new SolidBrush(textColor))
            {
                for (int yi = 0; yi <= 4; yi++)
                {
                    double eVal = minE + rangeE * yi / 4.0;
                    float  y    = energyToY(eVal);
                    g.DrawString(eVal.ToString(isAu ? "F4" : "F2"), labelFont, labelBrush,
                        new RectangleF(0, y - 8, ml - 3, 16), sf);
                }
            }

            // X-axis labels (step numbers, smart decimation)
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
            using (var labelFont  = new Font("Segoe UI", 7f))
            using (var labelBrush = new SolidBrush(textColor))
            {
                int maxLabels = Math.Max(2, chartRect.Width / 30);
                int step      = Math.Max(1, (int)Math.Ceiling((double)n / maxLabels));
                for (int i = 0; i < n; i += step)
                    g.DrawString((i + 1).ToString(), labelFont, labelBrush,
                        new RectangleF(pts[i].X - 15, chartRect.Bottom + 3, 30, 16), sf);
            }

            // Axis title labels
            using (var labelFont  = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (var labelBrush = new SolidBrush(textColor))
            {
                // X-axis title
                g.DrawString("Ionic Step", labelFont, labelBrush,
                    new RectangleF(chartRect.Left, gh - 14, chartRect.Width, 14),
                    new StringFormat { Alignment = StringAlignment.Center });
                // Y-axis title (rotated)
                var state = g.Save();
                g.TranslateTransform(14, chartRect.Top + chartRect.Height / 2f);
                g.RotateTransform(-90);
                g.DrawString("Energy (" + energyUnit + ")", labelFont, labelBrush,
                    new RectangleF(-46, -8, 92, 16),
                    new StringFormat { Alignment = StringAlignment.Center });
                g.Restore(state);
            }

            // Chart border
            using (var borderPen = new Pen(Color.FromArgb(80, textColor.R, textColor.G, textColor.B), 1f))
                g.DrawRectangle(borderPen, chartRect);
        }

        private static void FillCircle(Graphics g, Brush b, PointF center, float r)
            => g.FillEllipse(b, center.X - r, center.Y - r, 2 * r, 2 * r);

        // ── Graph click interaction ────────────────────────────────────────────

        private void OnGraphClick(object sender, MouseEventArgs e)
        {
            if (_outcarData == null) return;
            int n = _outcarData.StepEnergies.Count;
            if (n < 2) return;

            const int ml = 62, mr = 24;
            int chartW = _graphPicture.Width - ml - mr;
            if (chartW <= 0) return;

            float relX = e.X - ml;
            int   step = (int)Math.Round(relX / chartW * (n - 1));
            step = Math.Max(0, Math.Min(n - 1, step));
            SetSelectedStep(step);
        }

        // ── Animation & drag ──────────────────────────────────────────────────

        private void OnTick(object sender, EventArgs e)
        {
            float step = StepDeg * (float)(Math.PI / 180.0);
            float c    = (float)Math.Cos(step);
            float s    = (float)Math.Sin(step);
            var   ry   = new float[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
            _rotMatrix = MatMul3(ry, _rotMatrix);
            RenderStructure();
        }

        private void OnStructureMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            _isDragging     = true;
            _dragBaseMatrix = (float[,])_rotMatrix.Clone();
            _dragStartVec   = ArcballVec(e.X, e.Y);
            _timer.Stop();
            _structurePicture.Cursor = Cursors.SizeAll;
        }

        private void OnStructureMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            float[]  cur   = ArcballVec(e.X, e.Y);
            float[,] delta = ArcballRotation(_dragStartVec, cur);
            _rotMatrix = MatMul3(delta, _dragBaseMatrix);
            RenderStructure(lowQuality: true);
        }

        private void OnStructureMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !_isDragging) return;
            _isDragging = false;
            _structurePicture.Cursor = Cursors.Default;
            RenderStructure();
            _timer.Start();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _zoomFactor *= e.Delta > 0 ? 1.1f : (1f / 1.1f);
            if (_zoomFactor < 0.2f) _zoomFactor = 0.2f;
            if (_zoomFactor > 5.0f) _zoomFactor = 5.0f;
            RenderStructure();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutControls();
            AdjustSliderWidth();
            RenderStructure();
            if (_outcarData?.StepPositions.Count > 1) DrawGraph();
        }

        // ── Background colour ──────────────────────────────────────────────────

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
            _structurePicture.BackColor = bg;
            _graphPicture.BackColor     = bg;
            BackColor = bg;

            float lum       = 0.299f * bg.R + 0.587f * bg.G + 0.114f * bg.B;
            Color textColor = lum > 128f ? Color.Black : Color.White;
            Color panelBg   = lum > 128f ? Color.FromArgb(220, 220, 225) : Color.FromArgb(10, 10, 20);

            _label.ForeColor       = textColor;
            _label.BackColor       = panelBg;
            _bottomPanel.BackColor = panelBg;

            foreach (Control c in _bottomPanel.Controls)
            {
                if (c is Label    lbl) lbl.ForeColor = textColor;
                if (c is Button   btn) btn.ForeColor = textColor;
                if (c is CheckBox chk) chk.ForeColor = textColor;
            }

            RenderStructure();
            DrawGraph();
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _structurePicture?.Image?.Dispose();
                _structurePicture?.Dispose();
                _graphPicture?.Image?.Dispose();
                _graphPicture?.Dispose();
                _label?.Dispose();
                _bottomPanel?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ── Arcball helpers ────────────────────────────────────────────────────

        private float[] ArcballVec(int px, int py)
        {
            float cx = _structurePicture.Width  / 2f;
            float cy = _structurePicture.Height / 2f;
            float r  = Math.Min(_structurePicture.Width, _structurePicture.Height) / 2f * 0.85f;
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
