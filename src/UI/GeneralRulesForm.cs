using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Control = System.Windows.Forms.Control;
using SleevesOpenings.Rules;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Manual p.2 gate: each general rule is an "I confirm" checkbox. Continue is enabled only when all are ticked.
    /// </summary>
    public class GeneralRulesForm : System.Windows.Forms.Form
    {
        private readonly List<CheckBox> _confirms = new List<CheckBox>();
        private readonly Button _continue;

        public GeneralRulesForm(GeneralRules rules, string fileName, string user)
        {
            Text = "Sleeves & Openings — General Rules";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 100);
            const int pad = 16;
            int y = pad;

            var head = new Label
            {
                Text = "Before starting any work (Standards Manual, General Rules)", Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                AutoSize = true, Location = new Point(pad, y)
            };
            Controls.Add(head); y += head.PreferredHeight + 4;
            var file = new Label { Text = $"File: {fileName}    User: {user}", ForeColor = Color.DimGray, AutoSize = true, Location = new Point(pad, y) };
            Controls.Add(file); y += file.PreferredHeight + 14;

            foreach (var rule in rules.MustConfirm)
            {
                var cb = new CheckBox
                {
                    Text = rule, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 2 * pad, 0),
                    Location = new Point(pad, y), Padding = new Padding(0, 0, 0, 4)
                };
                cb.CheckedChanged += (o, e) => _continue.Enabled = _confirms.All(c => c.Checked);
                Controls.Add(cb);
                _confirms.Add(cb);
                y += Math.Max(cb.PreferredSize.Height, 28) + 8;
            }

            y += 4;
            var note = new Label
            {
                Text = "If you cannot confirm both, get the Owner's plan / the latest file first — the add-in will not place anything until then." +
                       Environment.NewLine + Environment.NewLine +
                       "Shower drains, wall-hung toilets, medicine cabinets and niches are checked automatically against the model in Final Check.",
                ForeColor = Color.DimGray, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 2 * pad, 0), Location = new Point(pad, y)
            };
            Controls.Add(note); y += note.PreferredHeight + 20;

            _continue = new Button { Text = "I confirm — Continue", DialogResult = DialogResult.OK, Size = new Size(180, 34), Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };
            cancel.Location = new Point(ClientSize.Width - pad - cancel.Width, y);
            _continue.Location = new Point(cancel.Left - 10 - _continue.Width, y);
            Controls.Add(_continue); Controls.Add(cancel);
            y += _continue.Height + pad;

            ClientSize = new Size(ClientSize.Width, y);
            CancelButton = cancel;
        }
    }
}
