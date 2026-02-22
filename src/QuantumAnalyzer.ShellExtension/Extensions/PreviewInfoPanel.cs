using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace QuantumAnalyzer.ShellExtension.Extensions
{
    internal class PreviewInfoPanel : Panel
    {
        private readonly Label _leftHeader;
        private readonly Label _midHeader;
        private readonly Label _rightHeader;
        private readonly Panel _leftDivider;
        private readonly Panel _midDivider;
        private readonly Panel _rightDivider;
        private readonly Label _leftBody;
        private readonly Label _midBody;
        private readonly Label _rightBody;

        public PreviewInfoPanel()
        {
            Dock = DockStyle.Top;
            Height = 0;
            Visible = false;
            BackColor = Color.FromArgb(8, 8, 16);

            _leftHeader = CreateHeader();
            _midHeader = CreateHeader();
            _rightHeader = CreateHeader();
            _leftDivider = CreateDivider();
            _midDivider = CreateDivider();
            _rightDivider = CreateDivider();
            _leftBody = CreateBody();
            _midBody = CreateBody();
            _rightBody = CreateBody();

            Controls.Add(_leftHeader);
            Controls.Add(_midHeader);
            Controls.Add(_rightHeader);
            Controls.Add(_leftDivider);
            Controls.Add(_midDivider);
            Controls.Add(_rightDivider);
            Controls.Add(_leftBody);
            Controls.Add(_midBody);
            Controls.Add(_rightBody);

            Resize += (s, e) => LayoutChildren();
        }

        public void SetHeaders(string left, string mid, string right)
        {
            _leftHeader.Text = left ?? "GENERAL";
            _midHeader.Text = mid ?? "MODEL";
            _rightHeader.Text = right ?? "ENERGY";
        }

        public void SetSections(List<string> left, List<string> mid, List<string> right)
        {
            _leftBody.Text = string.Join(Environment.NewLine, left ?? new List<string>());
            _midBody.Text = string.Join(Environment.NewLine, mid ?? new List<string>());
            _rightBody.Text = string.Join(Environment.NewLine, right ?? new List<string>());

            int lc = Math.Max((left ?? new List<string>()).Count, Math.Max((mid ?? new List<string>()).Count, (right ?? new List<string>()).Count));
            Visible = lc > 0;
            Height = lc > 0 ? Math.Max(88, 36 + (lc * 14)) : 0;
            PerformLayout();
        }

        public void ApplyTheme(Color textColor, Color panelBg)
        {
            BackColor = panelBg;
            _leftHeader.ForeColor = textColor;
            _midHeader.ForeColor = textColor;
            _rightHeader.ForeColor = textColor;
            _leftBody.ForeColor = textColor;
            _midBody.ForeColor = textColor;
            _rightBody.ForeColor = textColor;
            Color divider = Color.FromArgb(
                Math.Max(0, textColor.R - 35),
                Math.Max(0, textColor.G - 35),
                Math.Max(0, textColor.B - 35));
            _leftDivider.BackColor = divider;
            _midDivider.BackColor = divider;
            _rightDivider.BackColor = divider;
        }

        public static void AddField(List<string> list, string key, string value)
        {
            if (list == null || string.IsNullOrWhiteSpace(value)) return;
            const int keyWidth = 9;
            string clippedKey = key ?? string.Empty;
            if (clippedKey.Length > keyWidth) clippedKey = clippedKey.Substring(0, keyWidth);
            list.Add(clippedKey.PadRight(keyWidth) + " : " + value.Trim());
        }

        private static Label CreateHeader()
        {
            return new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(210, 210, 220),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            };
        }

        private static Panel CreateDivider()
        {
            return new Panel { Height = 1, BackColor = Color.FromArgb(80, 80, 105) };
        }

        private static Label CreateBody()
        {
            return new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(180, 180, 200),
                Font = new Font("Consolas", 8f),
                TextAlign = ContentAlignment.TopLeft,
            };
        }

        private void LayoutChildren()
        {
            int pad = 8;
            int gap = 12;
            int third = Math.Max(40, (ClientSize.Width - (pad * 2) - (gap * 2)) / 3);
            int headerH = 16;
            int dividerH = 1;
            int bodyY = 6 + headerH + 4 + dividerH + 5;
            int bodyH = Math.Max(10, ClientSize.Height - bodyY - 4);

            int x1 = pad;
            int x2 = pad + third + gap;
            int x3 = pad + (third * 2) + (gap * 2);

            _leftHeader.SetBounds(x1, 6, third, headerH);
            _midHeader.SetBounds(x2, 6, third, headerH);
            _rightHeader.SetBounds(x3, 6, third, headerH);

            _leftDivider.SetBounds(x1, 6 + headerH + 4, third, dividerH);
            _midDivider.SetBounds(x2, 6 + headerH + 4, third, dividerH);
            _rightDivider.SetBounds(x3, 6 + headerH + 4, third, dividerH);

            _leftBody.SetBounds(x1, bodyY, third, bodyH);
            _midBody.SetBounds(x2, bodyY, third, bodyH);
            _rightBody.SetBounds(x3, bodyY, third, bodyH);
        }
    }
}
