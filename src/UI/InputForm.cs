using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace SleevesOpenings.UI
{
    /// <summary>Small generic dialog: a few labeled fields (numbers in inches or text) and an info line.</summary>
    public class InputForm : Form
    {
        public class Field
        {
            public string Key, Label, Default;
            public bool Numeric = true;
            public string[] Choices;      // when set, a dropdown instead of a textbox
        }

        private readonly Dictionary<string, Control> _controls = new Dictionary<string, Control>();
        private readonly List<Field> _fields;

        public InputForm(string title, string info, IEnumerable<Field> fields)
        {
            _fields = fields.ToList();
            Text = title;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 100);   // fixed width; height is set once the rows are laid out

            const int pad = 16, labelW = 200, inputW = 280, rowH = 36;
            int y = pad;

            if (!string.IsNullOrEmpty(info))
            {
                var lbl = new Label
                {
                    Text = info, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 2 * pad, 0),
                    ForeColor = Color.DimGray, Location = new Point(pad, y)
                };
                Controls.Add(lbl);
                y += lbl.PreferredHeight + 14;
            }

            foreach (var f in _fields)
            {
                Controls.Add(new Label
                {
                    Text = f.Label, Width = labelW, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(pad, y)
                });
                Control c;
                if (f.Choices != null)
                {
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = inputW };
                    cb.Items.AddRange(f.Choices);
                    cb.SelectedIndex = Math.Max(0, Array.IndexOf(f.Choices, f.Default));
                    c = cb;
                }
                else c = new TextBox { Text = f.Default ?? "", Width = inputW };
                c.Location = new Point(pad + labelW, y);
                _controls[f.Key] = c;
                Controls.Add(c);
                y += rowH;
            }

            y += 8;
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(100, 32) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            cancel.Location = new Point(ClientSize.Width - pad - cancel.Width, y);
            ok.Location = new Point(cancel.Left - 10 - ok.Width, y);
            Controls.Add(ok); Controls.Add(cancel);
            y += ok.Height + pad;

            ClientSize = new Size(ClientSize.Width, y);
            AcceptButton = ok; CancelButton = cancel;

            Shown += (s, e) =>
            {
                var first = _controls.Values.FirstOrDefault();
                first?.Focus();
                if (first is TextBox tb) tb.SelectAll();
            };

            ok.Click += (s, e) =>
            {
                foreach (var f in _fields.Where(f => f.Numeric && f.Choices == null))
                    if (!TryParseInches(Value(f.Key), out _))
                    {
                        MessageBox.Show(this, $"'{f.Label}' must be a number in inches (e.g. 6, 10.5, 2'-4\").", "Invalid input");
                        DialogResult = DialogResult.None; return;
                    }
            };
        }

        public string Value(string key) => _controls[key] is ComboBox cb ? (string)cb.SelectedItem : ((TextBox)_controls[key]).Text.Trim();
        public double Inches(string key) { TryParseInches(Value(key), out var v); return v; }
        public int Int(string key) => (int)Math.Round(Inches(key));

        /// <summary>Accepts 6, 6.5, 6", 2', 2'-4", 2' 4 1/2".</summary>
        public static bool TryParseInches(string s, out double inches)
        {
            inches = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Replace("”", "\"").Replace("’", "'").Trim();
            double feet = 0; string rest = s;
            int ft = s.IndexOf('\'');
            if (ft >= 0)
            {
                if (!double.TryParse(s.Substring(0, ft).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out feet)) return false;
                rest = s.Substring(ft + 1).TrimStart('-', ' ');
            }
            rest = rest.TrimEnd('"').Trim();
            double inch = 0;
            if (rest.Length > 0)
            {
                var parts = rest.Split(' ');
                foreach (var p in parts)
                {
                    if (p.Contains("/"))
                    {
                        var fr = p.Split('/');
                        if (fr.Length != 2 || !double.TryParse(fr[0], out var n) || !double.TryParse(fr[1], out var d) || d == 0) return false;
                        inch += n / d;
                    }
                    else if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) inch += v;
                    else return false;
                }
            }
            inches = feet * 12 + inch;
            return inches > 0;
        }
    }
}
