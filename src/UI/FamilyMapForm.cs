using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// One row per role: pick the loaded family/type and which parameters drive width/length/diameter/label.
    /// Parameters are auto-guessed when a symbol is chosen.
    /// </summary>
    public class FamilyMapForm : System.Windows.Forms.Form
    {
        private class Row
        {
            public string Role;
            public ComboBox Symbol, Width, Length, Diameter, Name, DownHeight;
        }

        private readonly Document _doc;
        private readonly List<FamilySymbol> _symbols;
        private readonly List<Row> _rows = new List<Row>();
        private readonly ProjectState _state;
        private readonly RuleSet _rules;

        public FamilyMapForm(Document doc, RuleSet rules, ProjectState state)
        {
            _doc = doc; _rules = rules; _state = state;
            _symbols = FamilyMapping.AllSymbols(doc).ToList();
            Build();
        }

        private static string Key(FamilySymbol s) => $"{s.Family.Name} : {s.Name}";

        private void Build()
        {
            Text = "Sleeves & Openings — Map Families";
            Width = 1100; Height = 420;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, AutoScroll = true, Padding = new Padding(10) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

            string[] headers = { "Role", "Family : Type (loaded in project)", "Width", "Length", "Diameter", "Label", "Down Height" };
            for (int i = 0; i < headers.Length; i++)
                table.Controls.Add(new Label { Text = headers[i], Font = new Font(Font, FontStyle.Bold), AutoSize = true }, i, 0);

            int r = 1;
            foreach (var role in FamilyRole.All)
            {
                var row = new Row { Role = role };
                table.Controls.Add(new Label { Text = FamilyRole.Describe(role), AutoSize = true, MaximumSize = new Size(220, 0), Margin = new Padding(3, 8, 3, 3) }, 0, r);

                row.Symbol = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
                row.Symbol.Items.Add("(none)");
                foreach (var s in _symbols) row.Symbol.Items.Add(Key(s));
                table.Controls.Add(row.Symbol, 1, r);

                row.Width = ParamCombo(); row.Length = ParamCombo(); row.Diameter = ParamCombo(); row.Name = ParamCombo(); row.DownHeight = ParamCombo();
                table.Controls.Add(row.Width, 2, r); table.Controls.Add(row.Length, 3, r);
                table.Controls.Add(row.Diameter, 4, r); table.Controls.Add(row.Name, 5, r); table.Controls.Add(row.DownHeight, 6, r);

                row.Symbol.SelectedIndexChanged += (s, e) => OnSymbolChanged(row, guess: true);

                // Initial value: project mapping > rules.json > keyword suggestion
                var current = FamilyMapping.Get(_rules, _state, role);
                var sym = FamilyMapping.FindSymbol(_doc, current) ?? FamilyMapping.Suggest(_doc, role);
                row.Symbol.SelectedIndex = sym == null ? 0 : row.Symbol.Items.IndexOf(Key(sym));
                OnSymbolChanged(row, guess: false);
                if (sym != null && current != null)
                {
                    Select(row.Width, current.WidthParam); Select(row.Length, current.LengthParam);
                    Select(row.Diameter, current.DiameterParam); Select(row.Name, current.NameParam); Select(row.DownHeight, current.DownHeightParam);
                }
                if (sym != null && (row.Width.SelectedIndex <= 0 && row.Diameter.SelectedIndex <= 0)) OnSymbolChanged(row, guess: true);

                _rows.Add(row);
                r++;
            }

            var hint = new Label
            {
                Dock = DockStyle.Bottom, AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Padding = new Padding(10),
                Text = "Rectangular roles need Width + Length; round roles need Diameter (a 'Radius' parameter is handled). " +
                       "Type-driven parameters get a new type per size automatically. Saved in this project."
            };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 100 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);

            Controls.Add(table);
            Controls.Add(hint);
            Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
        }

        private static ComboBox ParamCombo() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };

        private static void Select(ComboBox cb, string value)
        {
            int i = string.IsNullOrEmpty(value) ? -1 : cb.Items.IndexOf(value);
            cb.SelectedIndex = i >= 0 ? i : 0;
        }

        private FamilySymbol SymbolOf(Row row) =>
            row.Symbol.SelectedIndex <= 0 ? null : _symbols[row.Symbol.SelectedIndex - 1];

        private void OnSymbolChanged(Row row, bool guess)
        {
            var sym = SymbolOf(row);
            var lengths = new List<string>(); var texts = new List<string>();
            if (sym != null) FamilyMapping.ParamNames(_doc, sym, out lengths, out texts);

            foreach (var cb in new[] { row.Width, row.Length, row.Diameter, row.DownHeight })
            { cb.Items.Clear(); cb.Items.Add("(none)"); foreach (var n in lengths) cb.Items.Add(n); cb.SelectedIndex = 0; }
            row.Name.Items.Clear(); row.Name.Items.Add("(none)"); foreach (var n in texts) row.Name.Items.Add(n); row.Name.SelectedIndex = 0;

            if (sym != null && guess)
            {
                var g = new FamilyMapEntry();
                FamilyMapping.GuessParams(_doc, sym, g);
                Select(row.Width, g.WidthParam); Select(row.Length, g.LengthParam);
                Select(row.Diameter, g.DiameterParam); Select(row.Name, g.NameParam); Select(row.DownHeight, g.DownHeightParam);
            }
        }

        /// <summary>Writes the chosen mapping into the project state.</summary>
        public void Apply()
        {
            _state.FamilyMap = new Dictionary<string, FamilyMapEntry>();
            foreach (var row in _rows)
            {
                var sym = SymbolOf(row);
                if (sym == null) continue;
                string V(ComboBox cb) => cb.SelectedIndex <= 0 ? null : (string)cb.SelectedItem;
                _state.FamilyMap[row.Role] = new FamilyMapEntry
                {
                    FamilyName = sym.Family.Name, TypeName = sym.Name,
                    WidthParam = V(row.Width), LengthParam = V(row.Length), DiameterParam = V(row.Diameter),
                    NameParam = V(row.Name), DownHeightParam = V(row.DownHeight)
                };
            }
        }
    }
}
