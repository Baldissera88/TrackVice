namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // Two enrollments side by side: both as a whole at the top, then every item with what differs.
    public sealed class CompareDialog : Form
    {
        Theme theme;
        readonly Session a;
        readonly DateTime nowA;
        Session b;
        DateTime nowB;
        List<DiffRow> rows = new List<DiffRow>();
        DiffSummary sum;
        readonly List<IThemed> themed = new List<IThemed>();
        readonly Panel head = new Panel();
        readonly Label title = new Label();
        readonly Label desc = new Label();
        readonly SidesView sides = new SidesView();
        readonly FlowLayoutPanel bar = new FlowLayoutPanel();
        readonly TextBox search = new TextBox();
        readonly FlatButton bOnly = new FlatButton("Only differences", null);
        readonly SplitContainer split = new SplitContainer();
        readonly Table<DiffRow> table = new Table<DiffRow>();
        readonly Table<TEvent> evA = new Table<TEvent>();
        readonly Table<TEvent> evB = new Table<TEvent>();
        readonly Label capA = new Label();
        readonly Label capB = new Label();
        readonly Panel footer = new Panel();
        readonly Label status = new Label();
        readonly FlatButton bExport = new FlatButton("Export", Glyphs.Export);
        readonly FlatButton bClose = new FlatButton("Close", null);
        readonly List<Panel> borders = new List<Panel>();
        readonly List<Panel> plain = new List<Panel>();
        const int LastEvents = 40;

        // B is read by loadB on a background thread, since it can come from a storage container.
        public CompareDialog(Theme t, Session a, DateTime nowA, string whatB, Func<Session> loadB)
        {
            theme = t;
            this.a = a;
            this.nowA = nowA;
            Text = "Compare two enrollments";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(Dpi.S(1180), Dpi.S(780));
            MinimumSize = new Size(Dpi.S(760), Dpi.S(540));

            head.Dock = DockStyle.Top;
            head.Height = Dpi.S(72);
            title.Text = Text;
            title.Font = Fonts.Title;
            title.AutoSize = true;
            title.Location = new Point(Dpi.S(22), Dpi.S(12));
            desc.AutoSize = false;
            desc.AutoEllipsis = true;
            desc.Location = new Point(Dpi.S(24), Dpi.S(44));
            desc.Size = new Size(Dpi.S(1100), Dpi.S(22));
            desc.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            desc.Text = "Reading " + whatB + "…";
            head.Controls.Add(title);
            head.Controls.Add(desc);

            Panel sidesPad = Pad(DockStyle.Top, Dpi.S(112), new Padding(Dpi.S(24), Dpi.S(4), Dpi.S(24), Dpi.S(4)));
            sides.Dock = DockStyle.Fill;
            sidesPad.Controls.Add(sides);
            themed.Add(sides);

            bar.Dock = DockStyle.Top;
            bar.Height = Dpi.S(46);
            bar.Padding = new Padding(Dpi.S(22), Dpi.S(6), Dpi.S(22), 0);
            bar.WrapContents = false;
            search.Width = Dpi.S(260);
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Margin = new Padding(0, Dpi.S(5), Dpi.S(12), 0);
            search.TextChanged += delegate { Bind(); };
            bOnly.Checked = true;
            bOnly.Compact = true;
            bOnly.AutoWidth();
            bOnly.Margin = new Padding(0, Dpi.S(2), 0, 0);
            bOnly.Click += Safe.Wrap(delegate { bOnly.Checked = !bOnly.Checked; bOnly.Invalidate(); Bind(); });
            themed.Add(bOnly);
            bar.Controls.Add(search);
            bar.Controls.Add(bOnly);

            table.Col("Item", 260, delegate (DiffRow r) { return r.Name; }).Fill = true;
            table.Col("Kind", 110, delegate (DiffRow r) { return r.Kind; });
            table.Col("State A", 100, delegate (DiffRow r) { return r.StateA; });
            table.Col("Time A", 100, delegate (DiffRow r) { return Time(r.SecondsA); });
            table.Col("State B", 100, delegate (DiffRow r) { return r.StateB; });
            table.Col("Time B", 100, delegate (DiffRow r) { return Time(r.SecondsB); });
            table.Col("Difference", 300, delegate (DiffRow r) { return r.Note; }).Fill = true;
            table.ForeColor = delegate (DiffRow r) { return r.Weight >= 2 ? theme.ErrFg : r.Weight == 0 ? theme.Text3 : Color.Empty; };
            table.EmptyText = "Reading the other session…";
            table.SelectionChanged += delegate { ShowEvents(table.Current); };
            themed.Add(table);
            foreach (Table<TEvent> e in new Table<TEvent>[] { evA, evB })
            {
                e.Col("Time", 76, delegate (TEvent x) { return Txt.LocalTime(x.TimeUtc); }).Mono = true;
                e.Col("Event", 300, delegate (TEvent x) { return x.Text; }).Fill = true;
                e.ForeColor = delegate (TEvent x) { return x.Severity >= 2 ? theme.ErrFg : x.Severity == 1 ? theme.WarnFg : Color.Empty; };
                e.EmptyText = "Select an item to see its last events.";
                themed.Add(e);
            }

            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = Dpi.S(8);
            split.Panel1.Padding = new Padding(Dpi.S(24), Dpi.S(4), Dpi.S(24), 0);
            split.Panel2.Padding = new Padding(Dpi.S(24), 0, Dpi.S(24), Dpi.S(4));
            split.Panel1.Controls.Add(Bordered(table.Grid));
            TableLayoutPanel two = new TableLayoutPanel();
            two.Dock = DockStyle.Fill;
            two.ColumnCount = 2;
            two.RowCount = 1;
            two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            two.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            two.Padding = Padding.Empty;
            two.Controls.Add(EventColumn(capA, evA, new Padding(0, 0, Dpi.S(6), 0)), 0, 0);
            two.Controls.Add(EventColumn(capB, evB, new Padding(Dpi.S(6), 0, 0, 0)), 1, 0);
            split.Panel2.Controls.Add(two);
            plain.Add(split.Panel1);
            plain.Add(split.Panel2);

            footer.Dock = DockStyle.Bottom;
            footer.Height = Dpi.S(60);
            footer.Padding = new Padding(Dpi.S(24), Dpi.S(14), Dpi.S(24), Dpi.S(14));
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            bExport.Dock = DockStyle.Right;
            bExport.Enabled = false;
            bExport.Click += Safe.Wrap(delegate { Export(); });
            bClose.Dock = DockStyle.Right;
            bClose.Click += delegate { Close(); };
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            plain.Add(gap);
            themed.Add(bExport);
            themed.Add(bClose);
            footer.Controls.Add(status);
            footer.Controls.Add(bExport);
            footer.Controls.Add(gap);
            footer.Controls.Add(bClose);

            Controls.Add(split);
            Controls.Add(bar);
            Controls.Add(sidesPad);
            Controls.Add(head);
            Controls.Add(footer);
            sides.Set(SessionDiff.NameA(a, a), a.Kind, a.FirstUtc, SessionDiff.Total(a, nowA), a.Count(State.Succeeded), a.Count(State.Failed), null, null, null, null, 0, 0);
            capA.Text = "A";
            capB.Text = "B";
            ApplyTheme(t);
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            HandleCreated += delegate
            {
                try { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Search items and differences"); } catch (Exception) { }
            };
            Shown += delegate
            {
                try { split.SplitterDistance = Math.Max(Dpi.S(160), (int)(split.Height * 0.62)); } catch (Exception) { }
                ReadB(whatB, loadB);
            };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        Panel Pad(DockStyle dock, int height, Padding p)
        {
            Panel x = new Panel();
            x.Dock = dock;
            x.Height = height;
            x.Padding = p;
            plain.Add(x);
            return x;
        }

        Panel Bordered(Control c)
        {
            Panel border = new Panel();
            border.Dock = DockStyle.Fill;
            border.Padding = new Padding(1);
            c.Dock = DockStyle.Fill;
            border.Controls.Add(c);
            borders.Add(border);
            return border;
        }

        Panel EventColumn(Label cap, Table<TEvent> t, Padding margin)
        {
            Panel col = new Panel();
            col.Dock = DockStyle.Fill;
            col.Margin = margin;
            cap.Dock = DockStyle.Top;
            cap.Height = Dpi.S(28);
            cap.TextAlign = ContentAlignment.MiddleLeft;
            cap.Font = Fonts.SmallSemibold;
            cap.AutoEllipsis = true;
            col.Controls.Add(Bordered(t.Grid));
            col.Controls.Add(cap);
            plain.Add(col);
            return col;
        }

        static string Time(double? s)
        {
            return s.HasValue ? Txt.Duration(TimeSpan.FromSeconds(s.Value)) : "";
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            foreach (Control c in new Control[] { head, bar, split, footer }) { c.BackColor = t.Bg; c.ForeColor = t.Text; }
            foreach (Panel p in plain) { p.BackColor = t.Bg; p.ForeColor = t.Text; }
            foreach (Panel p in borders) p.BackColor = t.Divider;
            title.ForeColor = t.Text;
            desc.ForeColor = t.Text2;
            status.ForeColor = t.Text3;
            capA.ForeColor = t.Text2;
            capB.ForeColor = t.Text2;
            search.BackColor = t.Field;
            search.ForeColor = t.Text;
            foreach (IThemed x in themed) x.ApplyTheme(t);
            if (IsHandleCreated) Draw.TitleBar(Handle, t);
        }

        void Ui(MethodInvoker m)
        {
            try { if (!IsDisposed) BeginInvoke((MethodInvoker)delegate { Safe.Run(delegate { m(); }); }); }
            catch (Exception) { }
        }

        void ReadB(string whatB, Func<Session> loadB)
        {
            status.Text = "Reading " + whatB + "…";
            Thread th = new Thread(delegate ()
            {
                Session s = null;
                Exception err = null;
                try { s = loadB(); if (s != null) s.Rebuild(); }
                catch (Exception ex) { err = ex; }
                Ui(delegate
                {
                    if (err != null || s == null)
                    {
                        string why = err != null ? err.Message : "it was not found";
                        desc.Text = "The other session could not be read: " + why;
                        status.Text = "";
                        table.EmptyText = "Nothing to compare.";
                        table.SetRows(new List<DiffRow>());
                        return;
                    }
                    Set(s);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        // Shows the comparison with B, which is already read.
        public void Set(Session other)
        {
            b = other;
            nowB = b.ClockUtc.Year > 2000 ? b.ClockUtc : DateTime.UtcNow;
            rows = SessionDiff.Compare(a, nowA, b, nowB);
            sum = SessionDiff.Summarize(a, nowA, b, nowB, rows);
            sides.Set(sum.NameA, sum.KindA, sum.StartA, sum.TotalA, sum.DoneA, sum.FailedA, sum.NameB, sum.KindB, sum.StartB, sum.TotalB, sum.DoneB, sum.FailedB);
            desc.Text = sum.Sentence;
            capA.Text = "Last events on " + sum.NameA;
            capB.Text = "Last events on " + sum.NameB;
            table.EmptyText = "No item differs. Turn off Only differences to see every item.";
            bExport.Enabled = true;
            Bind();
            if (table.Rows.Count > 0) table.Select(table.Rows[0]);
        }

        void Bind()
        {
            if (b == null) return;
            string q = search.Text.Trim();
            List<DiffRow> l = new List<DiffRow>();
            foreach (DiffRow r in rows)
            {
                if (bOnly.Checked && r.Weight == 0) continue;
                if (q.Length > 0 && !Has(r.Name, q) && !Has(r.Note, q) && !Has(r.Kind, q) && !Has(r.StateA, q) && !Has(r.StateB, q)) continue;
                l.Add(r);
            }
            table.SetRows(l);
            int differ = 0;
            foreach (DiffRow r in rows) if (r.Weight > 0) differ++;
            status.Text = rows.Count + " rows, " + differ + " with a difference" + (l.Count != rows.Count ? ", " + l.Count + " shown" : "");
        }

        static bool Has(string s, string q)
        {
            return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void ShowEvents(DiffRow r)
        {
            Fill(evA, r == null ? null : r.ItemA, r != null && r.IsFinding);
            Fill(evB, r == null ? null : r.ItemB, r != null && r.IsFinding);
        }

        static void Fill(Table<TEvent> t, Item i, bool finding)
        {
            List<TEvent> l = new List<TEvent>();
            if (i != null)
            {
                int from = Math.Max(0, i.Events.Count - LastEvents);
                for (int k = i.Events.Count - 1; k >= from; k--) l.Add(i.Events[k]);
            }
            t.EmptyText = finding ? "A finding has no events of its own." : i == null ? "This item is not in this session." : "No events for this item.";
            t.SetRows(l);
            t.Grid.Invalidate();
        }

        void Export()
        {
            if (sum == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV (*.csv)|*.csv";
                d.FileName = "TrackVice compare " + Stem(sum.NameA) + " " + Stem(sum.NameB) + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, SessionDiff.ToCsv(rows, sum), new UTF8Encoding(true));
                status.Text = "Saved " + d.FileName;
            }
        }

        static string Stem(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- opening it ----------

        // The submenu for the Devices menus: where the other enrollment comes from.
        public static ToolStripMenuItem AddMenu(ContextMenuStrip m, Theme t, bool enabled, EventHandler fromAgent, EventHandler fromFile)
        {
            ToolStripMenuItem top = new ToolStripMenuItem("Compare with another enrollment…");
            top.ForeColor = t.Text;
            top.Padding = new Padding(Dpi.S(4), Dpi.S(3), Dpi.S(4), Dpi.S(3));
            top.Enabled = enabled;
            foreach (KeyValuePair<string, EventHandler> kv in new KeyValuePair<string, EventHandler>[] {
                new KeyValuePair<string, EventHandler>("A device with the agent…", fromAgent),
                new KeyValuePair<string, EventHandler>("A saved session file…", fromFile) })
            {
                ToolStripMenuItem sub = new ToolStripMenuItem(kv.Key, null, Safe.Wrap(kv.Value));
                sub.ForeColor = t.Text;
                sub.BackColor = t.Card;
                sub.Padding = top.Padding;
                top.DropDownItems.Add(sub);
            }
            top.DropDown.BackColor = t.Card;
            top.DropDown.Renderer = m.Renderer;
            m.Items.Add(top);
            return top;
        }

        // Compares a with a session the user picks from the devices with the agent. Returns the catalog to keep.
        public static IRemoteCatalog FromAgent(IWin32Window owner, Theme t, Session a, DateTime nowA, IRemoteCatalog agents, GraphClient graph)
        {
            RemoteDeployment picked;
            using (RemoteDialog d = new RemoteDialog(RemoteDialog.Agents, t, agents, graph, false))
            {
                d.Text = "Pick the enrollment to compare with";
                d.PickText("Compare with this device");
                d.ShowDialog(owner);
                agents = d.Catalog;
                picked = d.Picked;
            }
            AgentCatalog c = agents as AgentCatalog;
            if (picked == null || c == null) return agents;
            string key = picked.Key;
            using (CompareDialog cd = new CompareDialog(t, a, nowA, picked.Device, delegate { return c.Load(key); }))
                cd.ShowDialog(owner);
            return agents;
        }

        public static void FromFile(IWin32Window owner, Theme t, Session a, DateTime nowA)
        {
            string path;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "TrackVice sessions (*.json)|*.json|All files (*.*)|*.*";
                d.Title = "Open the session to compare with";
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                path = d.FileName;
            }
            using (CompareDialog cd = new CompareDialog(t, a, nowA, Path.GetFileName(path), delegate { return Session.Load(path); }))
                cd.ShowDialog(owner);
        }

        // Two plays of the Autopilot demo, the second on a slower device where one app failed.
        public static CompareDialog ForDemo(Theme t)
        {
            Session a = DemoCompare.A();
            return new CompareDialog(t, a, a.ClockUtc, "the demo", delegate { return DemoCompare.B(); });
        }
    }

    // Both sessions as a whole, side by side.
    public sealed class SidesView : FlatControl
    {
        readonly string[] name = new string[2], kind = new string[2];
        readonly DateTime?[] start = new DateTime?[2];
        readonly TimeSpan?[] total = new TimeSpan?[2];
        readonly int[] done = new int[2], failed = new int[2];

        public void Set(string nameA, string kindA, DateTime? startA, TimeSpan? totalA, int doneA, int failedA,
                        string nameB, string kindB, DateTime? startB, TimeSpan? totalB, int doneB, int failedB)
        {
            name[0] = nameA; kind[0] = kindA; start[0] = startA; total[0] = totalA; done[0] = doneA; failed[0] = failedA;
            name[1] = nameB; kind[1] = kindB; start[1] = startB; total[1] = totalB; done[1] = doneB; failed[1] = failedB;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            int gap = Dpi.S(12), w = (Width - gap) / 2;
            for (int k = 0; k < 2; k++)
            {
                Rectangle r = new Rectangle(k * (w + gap), 0, w, Height);
                g.TranslateTransform(r.X, 0);
                Draw.Card(g, new Rectangle(0, 0, w, Height), T);
                g.ResetTransform();
                Side(g, r, k);
            }
        }

        void Side(Graphics g, Rectangle r, int k)
        {
            int x = r.X + Dpi.S(16), y = Dpi.S(12), lw = Dpi.S(96);
            int badge = Dpi.S(22);
            RectangleF br = new RectangleF(x, y + Dpi.S(1), badge, badge);
            Shapes.Fill(g, br, Dpi.S(4), T.AccentSoft);
            Draw.Text(g, k == 0 ? "A" : "B", Fonts.SmallSemibold, Rectangle.Round(br), T.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Draw.Text(g, name[k] ?? "Reading…", Fonts.UiSemibold, new Rectangle(x + badge + Dpi.S(10), y, r.Width - badge - Dpi.S(42), Dpi.S(24)), name[k] == null ? T.Text3 : T.Text, Draw.Line);
            if (name[k] == null) return;
            y += Dpi.S(32);
            int col2 = r.Width / 2;
            Pair(g, x, y, lw, col2 - Dpi.S(24), "Deployment", kind[k]);
            Pair(g, r.X + col2, y, Dpi.S(70), col2 - Dpi.S(16), "Took", Txt.Duration(total[k]));
            y += Dpi.S(24);
            Pair(g, x, y, lw, col2 - Dpi.S(24), "Started", start[k].HasValue ? Txt.Local(start[k].Value).Substring(0, 16) : "");
            Pair(g, r.X + col2, y, Dpi.S(70), col2 - Dpi.S(16), "Items", done[k] + " done, ");
            int off = Draw.Measure(done[k] + " done,", Fonts.Ui).Width + Draw.Measure("a a", Fonts.Ui).Width - Draw.Measure("aa", Fonts.Ui).Width, fx = r.X + col2 + Dpi.S(70) + off;
            Draw.Text(g, failed[k] + " failed", Fonts.Ui, new Rectangle(fx, y, Math.Max(0, r.Right - fx - Dpi.S(12)), Dpi.S(22)), failed[k] > 0 ? T.ErrFg : T.Text, Draw.Line);
        }

        void Pair(Graphics g, int x, int y, int lw, int w, string label, string value)
        {
            Draw.Text(g, label, Fonts.Small, new Rectangle(x, y, lw, Dpi.S(22)), T.Text3, Draw.Line);
            Draw.Text(g, value, Fonts.Ui, new Rectangle(x + lw, y, w - lw, Dpi.S(22)), T.Text, Draw.Line);
        }
    }
}
