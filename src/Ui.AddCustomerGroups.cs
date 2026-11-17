namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // Step 3 of Add customer: where the platform script is assigned.
    sealed partial class AddCustomerDialog
    {
        public const string AssignHint = "In a production tenant, start with a small test group, then change the assignment to all Autopilot devices.";

        SettingRow searchRow;

        string PlanText()
        {
            return string.Join("\n", run.Plan(own, IntuneReady).ToArray());
        }

        void BuildAssign(List<Control> l)
        {
            List<ChoiceCard> cards = new List<ChoiceCard>();
            l.Add(Card(cards, ScriptTarget.NewGroup, "Create the group " + AddCustomerRun.GroupName + " (default)",
                "Every Autopilot device; only new enrollments install the agent. A dynamic device group with the rule " + AddCustomerRun.GroupRule + ". The group with that name is used when it is there."));
            l.Add(Card(cards, ScriptTarget.ChosenGroup, "Assign to a group I choose", "A security group in the tenant, such as a small group of test devices. Search it by a word of its name."));
            if (run.Target == ScriptTarget.ChosenGroup)
            {
                ButtonStrip s = new ButtonStrip(tSearch, bSearch);
                s.Fit();
                searchRow = Row(Glyphs.Search, "Find the group", SearchDesc(), s);
                l.Add(searchRow);
                if (found != null) l.Add(new GroupTable(found, run.Chosen, Picked));
            }
            l.Add(Card(cards, ScriptTarget.None, "Do not assign: I assign it in Intune myself", "The platform script is added without an assignment."));
            l.Add(new HintLine(AssignHint));
        }

        ChoiceCard Card(List<ChoiceCard> cards, ScriptTarget target, string title, string desc)
        {
            ChoiceCard c = new ChoiceCard(title, desc);
            c.Group = cards;
            cards.Add(c);
            c.IsChosen = run.Target == target;
            // The page is built again after the click, which throws this card away.
            c.Chosen += delegate { if (run.Target != target) BeginInvoke((MethodInvoker)delegate { Choose(target); }); };
            return c;
        }

        string SearchDesc()
        {
            string chosen = run.Chosen != null ? "Picked: " + run.Chosen.Name + " (" + run.Chosen.Members.ToLowerInvariant() + ")." : "Pick a group in the list.";
            return (searchText.Length > 0 ? searchText + " " : "") + chosen;
        }

        void Choose(ScriptTarget t)
        {
            if (busy) return;
            run.Target = t;
            note.Say("", false);
            if (t == ScriptTarget.ChosenGroup && found == null && IntuneReady) { SearchGroups(); return; }
            Rebuild();
            Nav();
        }

        void SearchGroups()
        {
            if (busy || !IntuneReady) return;
            string q = tSearch.Text.Trim();
            List<TenantGroup> got = null;
            Work("Searching the groups...", delegate { got = GroupSearch.Find(web, graphIn.Token, q); }, delegate
            {
                found = got;
                if (got.Count == 0) searchText = q.Length > 0 ? "No security group has the word " + q + " in its name." : "The tenant has no security groups.";
                else if (got.Count >= GroupSearch.Max) searchText = "The first " + GroupSearch.Max + " groups" + (q.Length > 0 ? " with " + q : "") + ". Enter a word of the name to find others.";
                else searchText = got.Count + (got.Count == 1 ? " group" : " groups") + (q.Length > 0 ? " with " + q + "." : ".");
            }, null);
        }

        void Picked(TenantGroup g)
        {
            if (g == null) return;
            run.Chosen = g;
            note.Say("", false);
            if (planRow != null) { planRow.Desc = PlanText(); planRow.Invalidate(); }
            if (searchRow != null) { searchRow.Desc = SearchDesc(); searchRow.Invalidate(); }
            Relayout();
        }

        // The demo picks a group by its name.
        void Pick(string name)
        {
            if (found != null) foreach (TenantGroup g in found) if (g.Name == name) run.Chosen = g;
            Rebuild();
        }
    }

    // The groups found, in a table with a fixed height. A row is picked by a click or the arrow keys.
    sealed class GroupTable : Panel, IThemed, IMeasured
    {
        const int Rows = 6;
        readonly Table<TenantGroup> table = new Table<TenantGroup>();
        readonly TenantGroup chosen;

        public GroupTable(List<TenantGroup> groups, TenantGroup chosen, Action<TenantGroup> picked)
        {
            this.chosen = chosen;
            Padding = new Padding(1);
            table.Col("Group", 400, delegate (TenantGroup g) { return g.Name; }).Fill = true;
            table.Col("Members", 120, delegate (TenantGroup g) { return g.Members; });
            foreach (DataGridViewColumn c in table.Grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            table.EmptyText = "No groups found.";
            table.Grid.Dock = DockStyle.Fill;
            Controls.Add(table.Grid);
            table.SetRows(groups);
            table.Grid.CellClick += delegate (object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) picked(table.Current); };
            table.Grid.KeyUp += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown) picked(table.Current);
            };
            // The grid makes the first row current on its own; only the picked group is shown as picked.
            table.Grid.HandleCreated += delegate { BeginInvoke((MethodInvoker)delegate { ShowChosen(); }); };
        }

        void ShowChosen()
        {
            if (IsDisposed) return;
            TenantGroup have = null;
            if (chosen != null) foreach (TenantGroup g in table.Rows) if (g.Id == chosen.Id) have = g;
            if (have != null) { table.Select(have); return; }
            try
            {
                table.Grid.CurrentCell = null;
                table.Grid.ClearSelection();
            }
            catch (Exception) { }
        }

        public int Measure(int width)
        {
            return table.Grid.ColumnHeadersHeight + Rows * table.Grid.RowTemplate.Height + 2;
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.Divider;
            table.ApplyTheme(t);
        }
    }

    // One line of small text between the rows of a guide.
    sealed class HintLine : FlatControl, IMeasured
    {
        public HintLine(string text)
        {
            Text = text;
        }

        int TextW(int width)
        {
            return Math.Max(Dpi.S(80), width - Dpi.S(40));
        }

        public int Measure(int width)
        {
            return Draw.WrapHeight(Text, Fonts.Small, TextW(width)) + Dpi.S(8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Draw.Text(e.Graphics, Text, Fonts.Small, new Rectangle(Dpi.S(20), Dpi.S(4), TextW(Width), Height - Dpi.S(4)), T.Text2, Draw.Wrap);
        }
    }
}
