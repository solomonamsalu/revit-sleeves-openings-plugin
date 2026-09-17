using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Panel = System.Windows.Forms.Panel;
using Control = System.Windows.Forms.Control;
using SleevesOpenings.Rules;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Manual p.2 gate. The general rules need an explicit Yes (No blocks the add-in); the
    /// "always verify" items must each be confirmed. Continue is disabled until everything is answered.
    /// </summary>
    public class GeneralRulesForm : System.Windows.Forms.Form
    {
        private readonly List<(RadioButton yes, RadioButton no)> _answers = new List<(RadioButton, RadioButton)>();
        private readonly Button _continue;
        private readonly Label _blocked;

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
                var lbl = new Label { Text = rule, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 2 * pad - 170, 0), Location = new Point(pad, y + 3) };
                var yes = new RadioButton { Text = "Yes", AutoSize = true, Location = new Point(ClientSize.Width - pad - 150, y) };
                var no = new RadioButton { Text = "No", AutoSize = true, Location = new Point(ClientSize.Width - pad - 80, y) };
                yes.CheckedChanged += (o, e) => Update(); no.CheckedChanged += (o, e) => Update();
                Controls.AddRange(new Control[] { lbl, yes, no });
                _answers.Add((yes, no));
                y += Math.Max(lbl.PreferredHeight, 26) + 10;
            }

            var note = new Label
            {
                Text = "Shower drains, wall-hung toilets, medicine cabinets and niches are checked automatically against the model in Final Check.",
                ForeColor = Color.DimGray, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 2 * pad, 0), Location = new Point(pad, y)
            };
            Controls.Add(note); y += note.PreferredHeight + 10;
            _blocked = new Label
            {
                Text = "Answered No: get the Owner's plan / the latest file first, then start again. The add-in will not place anything.",
                ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 2 * pad, 0), Location = new Point(pad, y), Visible = false
            };
            Controls.Add(_blocked); y += 44;

            _continue = new Button { Text = "Continue", DialogResult = DialogResult.OK, Size = new Size(120, 34), Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };
            cancel.Location = new Point(ClientSize.Width - pad - cancel.Width, y);
            _continue.Location = new Point(cancel.Left - 10 - _continue.Width, y);
            Controls.Add(_continue); Controls.Add(cancel);
            y += _continue.Height + pad;

            ClientSize = new Size(ClientSize.Width, y);
            CancelButton = cancel;
        }

        private void Update()
        {
            bool anyNo = _answers.Any(a => a.no.Checked);
            bool allYes = _answers.All(a => a.yes.Checked);
            _blocked.Visible = anyNo;
            _continue.Enabled = allYes && !anyNo;
        }
    }
}
