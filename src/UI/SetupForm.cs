using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Project Setup dialog: confirm level roles, tick the "always verify" checklist,
    /// pick bathtub/condensate answers, choose which setup actions to run.
    /// </summary>
    public class SetupForm : Form
    {
        private readonly RuleSet _rules;
        private readonly LevelMap _levels;
        private readonly ProjectState _state;

        private DataGridView _levelGrid;
        private ComboBox _bathtub;
        private ComboBox _condensate;
        private CheckBox _doFamilies;
        private CheckBox _doViewRange;

        public bool RunLoadFamilies => _doFamilies.Checked;
        public bool RunViewRange => _doViewRange.Checked;

        public SetupForm(RuleSet rules, LevelMap levels, ProjectState state)
        {
            _rules = rules;
            _levels = levels;
            _state = state;
            Build();
        }

        private void Build()
        {
            Text = "Sleeves & Openings — Project Setup";
            Width = 720;
            Height = 640;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // --- Levels (left) ---
            var levelBox = new GroupBox { Text = "Level roles (highest Apartment level = where placement starts)", Dock = DockStyle.Fill };
            _levelGrid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _levelGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Level", ReadOnly = true, Name = "Level" });
            _levelGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Elev (ft)", ReadOnly = true, Name = "Elev", FillWeight = 40 });
            var roleCol = new DataGridViewComboBoxColumn { HeaderText = "Role", Name = "Role", FillWeight = 60 };
            roleCol.Items.AddRange(Enum.GetNames(typeof(LevelRole)));
            _levelGrid.Columns.Add(roleCol);
            foreach (var lv in _levels.Everything.AsEnumerable().Reverse())   // top of building first, reference levels included so they can be un-ignored
                _levelGrid.Rows.Add(lv.Name, lv.Level.Elevation.ToString("0.##"), lv.Role.ToString());
            levelBox.Controls.Add(_levelGrid);
            root.Controls.Add(levelBox, 0, 0);

            // --- General rules (right): enforced by the gate, shown here for information only ---
            var preBox = new GroupBox { Text = "General rules (enforced)", Dock = DockStyle.Fill };
            var c = _state.Confirmation;
            string status = c == null ? "Not confirmed."
                : $"Confirmed by {c.User} on {c.Date:yyyy-MM-dd HH:mm} for {c.FileName}." + (_rules.GeneralRules.ReconfirmEveryDay ? " Re-asked each working day." : "");
            var f = _rules.Fixtures;
            var rulesText = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(8),
                Text = "Confirmed before setup and before any work:" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", _rules.GeneralRules.MustConfirm) +
                       Environment.NewLine + "  " + status + Environment.NewLine + Environment.NewLine +
                       "Checked automatically against the model (Final Check):" + Environment.NewLine +
                       "  - Shower drains: a sleeve within " + Units.FormatInches(f.ShowerDrain.SleeveRadius) + " of every drain" + Environment.NewLine +
                       "  - Toilets: floor-mounted needs a sleeve nearby; wall-hung must NOT have a slab sleeve outside the wall" + Environment.NewLine +
                       "  - Medicine cabinets and niches: no sleeve behind them (" + Units.FormatInches(f.MedicineCabinet.Clearance) + " clearance)" + Environment.NewLine +
                       "  - Never at the edge of a wall; not in the middle of a room" + Environment.NewLine + Environment.NewLine +
                       "Fixture recognition patterns are in rules.json → fixtures."
            };
            preBox.Controls.Add(rulesText);
            root.Controls.Add(preBox, 1, 0);

            // --- Project answers ---
            var answers = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
            answers.Controls.Add(new Label { Text = "Bathtub sleeves:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _bathtub = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            _bathtub.Items.Add("(not decided)");
            foreach (var kv in _rules.Systems.Bathtub.Options)
                _bathtub.Items.Add($"{kv.Key}: {kv.Value.Count} x {Units.FormatInches(kv.Value.Diameter)}");
            _bathtub.SelectedIndex = 0;
            var keys = _rules.Systems.Bathtub.Options.Keys.ToList();
            int bi = keys.IndexOf(_state.BathtubOption ?? _rules.Systems.Bathtub.Default ?? "");
            if (bi >= 0) _bathtub.SelectedIndex = bi + 1;
            answers.Controls.Add(_bathtub, 1, 0);

            answers.Controls.Add(new Label { Text = "Condensate risers required:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
            _condensate = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _condensate.Items.AddRange(new object[] { "(ask)", "Yes", "No" });
            _condensate.SelectedIndex = _state.CondensateRequired == null ? 0 : (_state.CondensateRequired.Value ? 1 : 2);
            answers.Controls.Add(_condensate, 3, 0);
            root.Controls.Add(answers, 0, 1);
            root.SetColumnSpan(answers, 2);

            // --- Actions + buttons ---
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            _doFamilies = new CheckBox { Text = "Load missing families", Checked = true, AutoSize = true };
            _doViewRange = new CheckBox { Text = "Set view range on floor plans (Top = Level Above, Bottom = Level)", Checked = true, AutoSize = true };
            var ok = new Button { Text = "Run Setup", DialogResult = DialogResult.OK, Width = 110 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            var rulesLbl = new Label { Text = "Rules: " + _rules.SourcePath, AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(3, 8, 3, 3) };
            bottom.Controls.AddRange(new Control[] { _doFamilies, _doViewRange, ok, cancel, rulesLbl });
            root.Controls.Add(bottom, 0, 2);
            root.SetColumnSpan(bottom, 2);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>Copies dialog values back into the project state and level map.</summary>
        public void Apply()
        {
            _levelGrid.EndEdit();
            foreach (DataGridViewRow row in _levelGrid.Rows)
            {
                var name = (string)row.Cells["Level"].Value;
                var role = (LevelRole)Enum.Parse(typeof(LevelRole), (string)row.Cells["Role"].Value);
                var lv = _levels.Everything.First(l => l.Name == name);
                lv.Role = role;
            }
            _state.LevelRoles = _levels.ToRoles();

            var keys = _rules.Systems.Bathtub.Options.Keys.ToList();
            _state.BathtubOption = _bathtub.SelectedIndex > 0 ? keys[_bathtub.SelectedIndex - 1] : null;
            _state.CondensateRequired = _condensate.SelectedIndex == 0 ? (bool?)null : _condensate.SelectedIndex == 1;
            _state.LastSetup = DateTime.Now;
        }
    }
}
