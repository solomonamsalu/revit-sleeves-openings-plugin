using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Report;
using SleevesOpenings.Setup;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using TextBox = System.Windows.Forms.TextBox;
using View = Autodesk.Revit.DB.View;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;

namespace SleevesOpenings.Automation.UI
{
    /// <summary>
    /// Plan section 9: the review list after an Auto Run. One row per opening / reported item / Final Check finding /
    /// S&amp;O difference; double-click zooms to it in the floor's Sleeves view (placed openings are selected, the rest are
    /// shown at their drawing position). Review spots can be marked in the model (one undo removes them). Opens the
    /// HTML report and the run folder.
    /// </summary>
    public class ReviewForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly RunReport _report;
        private readonly LevelMap _levels;
        private readonly string _viewSuffix, _html, _folder;
        private readonly DataGridView _grid;
        private readonly ComboBox _filter;
        private readonly Label _count;
        private List<ReportRow> _shown = new List<ReportRow>();

        private static readonly string[] Filters =
        {
            "Needs attention", "Openings from the drawings", "Reported (not placed)", "Final Check", "S&O set differences", "Everything"
        };

        public ReviewForm(UIDocument uidoc, RunReport report, LevelMap levels, string summary, string viewSuffix, string html, string folder)
        {
            _uidoc = uidoc; _report = report; _levels = levels; _viewSuffix = viewSuffix ?? " Sleeves"; _html = html; _folder = folder;
            Text = "Sleeves & Openings — Auto Run result";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            var screen = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1250, screen.Width - 40), Math.Min(820, screen.Height - 40));
            MinimumSize = new Size(800, 450);

            var text = new TextBox
            {
                Dock = DockStyle.Top, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 170,
                BackColor = SystemColors.Window, Text = (summary ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 6, 6, 0) };
            bar.Controls.Add(new Label { Text = "Show:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
            _filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            _filter.Items.AddRange(Filters);
            _count = new Label { AutoSize = true, Padding = new Padding(8, 4, 0, 0), ForeColor = Color.DimGray };
            bar.Controls.Add(_filter);
            bar.Controls.Add(_count);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true
            };
            foreach (var (h, w) in new[] { ("Floor", 55), ("Tag", 40), ("System", 50), ("Duct", 40), ("Opening", 55), ("Result", 70), ("Notes", 220), ("S&O set", 110) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Zoom(_shown[e.RowIndex]); };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6, 8, 6, 0) };
            var zoom = new Button { Text = "Zoom to selected", AutoSize = true };
            var mark = new Button { Text = "Mark spots in the model", AutoSize = true };
            var open = new Button { Text = "Open report", AutoSize = true, Enabled = html != null && File.Exists(html) };
            var folderBtn = new Button { Text = "Open run folder", AutoSize = true, Enabled = folder != null && Directory.Exists(folder) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            var hint = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 6, 0, 0),
                Text = "Double-click a row to zoom to it. Marks are model lines (circle + cross) on the rows' levels; one undo removes them."
            };
            zoom.Click += (s, e) => { var r = Current(); if (r != null) Zoom(r); };
            mark.Click += (s, e) => Mark();
            open.Click += (s, e) => Start(_html);
            folderBtn.Click += (s, e) => Start(_folder);
            bottom.Controls.AddRange(new Control[] { zoom, mark, open, folderBtn, close, hint });

            Controls.Add(_grid);
            Controls.Add(bar);
            Controls.Add(text);
            Controls.Add(bottom);
            CancelButton = close;

            _filter.SelectedIndexChanged += (s, e) => Fill();
            _filter.SelectedIndex = _report.Attention.Any() ? 0 : 1;
        }

        private IEnumerable<ReportRow> Rows(int filter)
        {
            switch (filter)
            {
                case 0: return _report.Attention;
                case 1: return _report.Rows.Where(r => r.Section == ReportRow.Opening);
                case 2: return _report.Rows.Where(r => r.Section == ReportRow.Reported);
                case 3: return _report.Rows.Where(r => r.Section == ReportRow.FinalCheck);
                case 4: return _report.Rows.Where(r => r.Section == ReportRow.SoSetDiff || (r.SoSet != null && !r.SoSet.StartsWith("same") && !r.SoSet.StartsWith("inside")));
                default: return _report.Rows;
            }
        }

        private void Fill()
        {
            _shown = Rows(_filter.SelectedIndex).OrderByDescending(r => FloorKey.Order(r.Floor)).ThenBy(r => r.Tag).ToList();
            _grid.Rows.Clear();
            foreach (var r in _shown)
            {
                int i = _grid.Rows.Add(r.Where, r.Tag ?? "-", r.System, r.DuctSize, r.OpeningSize, r.Result, r.Notes, r.SoSet);
                string x = (r.Result ?? "").ToLowerInvariant();
                _grid.Rows[i].DefaultCellStyle.BackColor =
                    x == "placed" || x == "already in the model" || x == "resized" || x == "fixed" ? (r.Attention ? Color.LightYellow : Color.Honeydew)
                    : r.Attention ? (x == "review" || x.StartsWith("warning") ? Color.LightYellow : Color.MistyRose)
                    : SystemColors.Window;
            }
            _count.Text = $"{_shown.Count} row(s); {_shown.Count(r => r.Ids.Count > 0)} with elements in the model, {_shown.Count(r => r.Ids.Count == 0 && r.X.HasValue)} shown at their drawing position";
        }

        private ReportRow Current() =>
            _grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _shown.Count ? _shown[_grid.CurrentRow.Index] : null;

        /// <summary>Opens the floor's Sleeves view (else its plan) and zooms: to the elements when the row has any, else to its drawing position.</summary>
        private void Zoom(ReportRow r)
        {
            var doc = _uidoc.Document;
            try
            {
                var ids = r.Ids.Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();
                var level = _levels.All.FirstOrDefault(l => l.Name == r.Level)?.Level;
                var view = level == null ? null : ViewFor(doc, level);
                if (view != null && _uidoc.ActiveView?.Id != view.Id)
                {
                    try { _uidoc.ActiveView = view; }
                    catch (Exception) { _uidoc.RequestViewChange(view); }
                }
                if (ids.Count > 0)
                {
                    _uidoc.Selection.SetElementIds(ids);
                    _uidoc.ShowElements(ids);
                    return;
                }
                if (!r.X.HasValue || level == null)
                {
                    MessageBox.Show(this, r.X.HasValue ? $"'{r.Level ?? "?"}' is not a level of this model." : "This row has no position (the floor is not lined up with Revit).",
                                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var ui = _uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == (view?.Id ?? _uidoc.ActiveView.Id));
                var c = new XYZ(r.X.Value, r.Y.Value, level.ProjectElevation);
                var d = new XYZ(6, 6, 0);
                ui?.ZoomAndCenterRectangle(c - d, c + d);
            }
            catch (Exception ex)
            {
                App.Log("Auto Run review zoom: " + ex);
                MessageBox.Show(this, "Could not zoom: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private View ViewFor(Document doc, Level level)
        {
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.GenLevel?.Id == level.Id).ToList();
            return plans.FirstOrDefault(v => v.Name == level.Name + _viewSuffix)
                   ?? plans.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan && v.Name.EndsWith(_viewSuffix.Trim()))
                   ?? plans.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan);
        }

        /// <summary>Circle + cross at every selected row (all shown rows when one or none is selected) that has a position and no element.</summary>
        private void Mark()
        {
            var picked = _grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Index).Where(i => i >= 0 && i < _shown.Count).Select(i => _shown[i]).ToList();
            var rows = (picked.Count > 1 ? picked : _shown).Where(r => r.Ids.Count == 0 && r.X.HasValue && r.Level != null).ToList();
            if (rows.Count == 0) { MessageBox.Show(this, "No row here has a drawing position without an element in the model.", Text); return; }
            var doc = _uidoc.Document;
            try
            {
                List<ElementId> ids;
                using (var t = new Transaction(doc, $"Sleeves & Openings: mark {rows.Count} review spot(s)"))
                {
                    t.Start();
                    ids = AnchorMarks.DrawAt(doc, rows.Select(r => (r.Level, r.X.Value, r.Y.Value)), _levels);
                    t.Commit();
                }
                if (ids.Count > 0) _uidoc.Selection.SetElementIds(ids);
                MessageBox.Show(this, $"{rows.Count} spot(s) marked ({ids.Count} model lines, selected). Undo removes them.", Text);
            }
            catch (Exception ex)
            {
                App.Log("Auto Run review marks: " + ex);
                MessageBox.Show(this, "Could not mark: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Start(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        }
    }
}
