using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Panel = System.Windows.Forms.Panel;
using Control = System.Windows.Forms.Control;

namespace SleevesOpenings.UI
{
    public enum StepState { Todo, Partial, Done, NotNeeded }

    /// <summary>One step of the manual's sequence, with its computed status and the ribbon button that does it.</summary>
    public class WorkflowStep
    {
        public string Title;
        public string Detail;        // what "done" means / what to do
        public StepState State;
        public string ButtonId;      // ribbon button name (null = no launch)
        public string ButtonText;
    }

    /// <summary>The manual as a checklist: status per step, a Go button per step that launches the ribbon command.</summary>
    public class WorkflowForm : System.Windows.Forms.Form
    {
        public string LaunchButtonId { get; private set; }

        public WorkflowForm(IList<WorkflowStep> steps, string project)
        {
            Text = "Sleeves & Openings — Workflow";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 100);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;

            const int pad = 16;
            int y = pad;
            var head = new Label { Text = "The manual's order, for " + project, Font = new Font("Segoe UI", 12f, FontStyle.Bold), AutoSize = true, Location = new Point(pad, y) };
            Controls.Add(head); y += head.PreferredHeight + 4;
            int done = steps.Count(s => s.State == StepState.Done || s.State == StepState.NotNeeded);
            var sub = new Label { Text = $"{done} of {steps.Count} steps complete. Work top to bottom; each step assumes the ones above it.", ForeColor = Color.DimGray, AutoSize = true, Location = new Point(pad, y) };
            Controls.Add(sub); y += sub.PreferredHeight + 12;

            int n = 1;
            foreach (var s in steps)
            {
                var row = new Panel { Location = new Point(pad, y), Size = new Size(ClientSize.Width - 2 * pad, 52), BorderStyle = BorderStyle.FixedSingle };
                var mark = new Label
                {
                    Text = s.State == StepState.Done ? "✓" : s.State == StepState.NotNeeded ? "–" : s.State == StepState.Partial ? "…" : (n).ToString(),
                    Font = new Font("Segoe UI", 14f, FontStyle.Bold), AutoSize = false, Size = new Size(36, 50), TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = s.State == StepState.Done ? Color.SeaGreen : s.State == StepState.Partial ? Color.DarkGoldenrod : s.State == StepState.NotNeeded ? Color.Gray : Color.Firebrick
                };
                var title = new Label { Text = s.Title, Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoSize = true, Location = new Point(44, 6) };
                var detail = new Label { Text = s.Detail, ForeColor = Color.DimGray, AutoSize = true, MaximumSize = new Size(row.Width - 190, 0), Location = new Point(44, 26) };
                row.Controls.AddRange(new Control[] { mark, title, detail });
                if (s.ButtonId != null)
                {
                    var go = new Button { Text = s.ButtonText ?? "Open", Size = new Size(120, 30), Location = new Point(row.Width - 132, 10) };
                    var id = s.ButtonId;
                    go.Click += (o, e) => { LaunchButtonId = id; DialogResult = DialogResult.OK; Close(); };
                    row.Controls.Add(go);
                }
                Controls.Add(row);
                y += row.Height + 6;
                n++;
            }

            y += 6;
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 32), Location = new Point(ClientSize.Width - pad - 100, y) };
            Controls.Add(close);
            CancelButton = close;
            ClientSize = new Size(ClientSize.Width, y + 32 + pad);
        }
    }
}
