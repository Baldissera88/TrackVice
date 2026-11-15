namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // Statistics over every session the agents have uploaded in a period: how long enrollments take, and which apps,
    // scripts and steps are slow or fail.
    public sealed class StatsDialog : Form
    {
        public const int MaxSessions = 300;
        public RemoteDeployment Picked;
        public readonly IRemoteCatalog Catalog;

        Theme theme;
        readonly AgentCatalog catalog;
        readonly List<IThemed> themed = new List<IThemed>();
        readonly Panel head = new Panel();
        readonly Label title = new Label();
        readonly Label desc = new Label();
        readonly FlowLayoutPanel bar = new FlowLayoutPanel();
        readonly FlowLayoutPanel chips = new FlowLayoutPanel();
        readonly TabStrip tabs = new TabStrip();
        readonly Panel card = new Panel();
        readonly Panel border = new Panel();
        readonly Table<FleetItem> items = new Table<FleetItem>();
        readonly Table<FleetRun> runs = new Table<FleetRun>();
        readonly Panel footer = new Panel();
        readonly Label status = new Label();
        readonly FlatButton bWatch = new FlatButton("Watch this device", Glyphs.Watch);
        readonly Panel watchGap = new Panel();
        readonly FlatButton bClose = new FlatButton("Close", null);
        readonly FlatButton bRefresh = new FlatButton("Refresh", Glyphs2.Sync);
        readonly FlatButton bExport = new FlatButton("Export CSV", Glyphs.Export);
        readonly FlatButton bPowerBi = new FlatButton("Export for Power BI\u2026", Glyphs.Export);
        List<Session> loaded = new List<Session>();
        List<string> loadedKeys = new List<string>();
        readonly TextBox search = new TextBox();
        readonly Segmented since = new Segmented("24 h", "7 d", "30 d", "90 d");
        static readonly int[] SinceHours = { 24, 168, 720, 2160 };
        readonly ValueChip cRuns = new ValueChip("Enrollments", 0);
        readonly ValueChip cFinished = new ValueChip("Finished", 0);
        readonly ValueChip cFailed = new ValueChip("With failures", 2);
        readonly ValueChip cAvg = new ValueChip("Average", 0);
        readonly ValueChip cMedian = new ValueChip("Median", 0);
        readonly ValueChip cLongest = new ValueChip("Longest", 0);
        FleetStats stats;
        // The reference of the customer, and what each enrollment shows against it, by upload.
        readonly Reference reference;
        Dictionary<string, string> refBadges = new Dictionary<string, string>();
        Dictionary<string, RemoteDeployment> byKey = new Dictionary<string, RemoteDeployment>();
        // Set to stop the load that runs now. A new load or closing the window sets it.
        ManualResetEvent cancel;

        public StatsDialog(Theme t, AgentCatalog c, bool demo)
        {
            theme = t;
            catalog = c;
            Catalog = c;
            reference = ReferenceUi.ForStats(demo);
            Text = "Statistics";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(Dpi.S(1180), Dpi.S(760));
            MinimumSize = new Size(Dpi.S(820), Dpi.S(520));

            head.Dock = DockStyle.Top;
            head.Height = Dpi.S(70);
            title.Text = "Statistics for devices with the agent";
            title.Font = Fonts.Title;
            title.AutoSize = true;
            title.Location = new Point(Dpi.S(22), Dpi.S(12));
            desc.AutoSize = false;
            desc.Location = new Point(Dpi.S(24), Dpi.S(44));
            desc.Size = new Size(Dpi.S(1000), Dpi.S(22));
            desc.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            desc.Font = Fonts.Small;
            desc.Text = "How long enrollments take, and which apps, scripts and steps are slow or fail, over every session the agents uploaded in the period.";
            head.Controls.Add(title);
            head.Controls.Add(desc);

            bar.Dock = DockStyle.Top;
            bar.Height = Dpi.S(46);
            bar.Padding = new Padding(Dpi.S(22), Dpi.S(6), Dpi.S(22), 0);
            bar.WrapContents = false;
            since.Selected = 1;
            since.Margin = new Padding(0, 0, Dpi.S(12), 0);
            since.Changed += Safe.Wrap(delegate { LoadStats(); });
            themed.Add(since);
            bRefresh.Compact = true;
            bRefresh.AutoWidth();
            bRefresh.Margin = new Padding(0, Dpi.S(2), Dpi.S(8), 0);
            bRefresh.Click += Safe.Wrap(delegate { LoadStats(); });
            themed.Add(bRefresh);
            bExport.Compact = true;
            bExport.AutoWidth();
            bExport.Margin = new Padding(0, Dpi.S(2), Dpi.S(16), 0);
            bExport.Enabled = false;
            bExport.Click += Safe.Wrap(delegate { Export(); });
            themed.Add(bExport);
            bPowerBi.Compact = true;
            bPowerBi.AutoWidth();
            bPowerBi.Margin = new Padding(0, Dpi.S(2), Dpi.S(16), 0);
            bPowerBi.Enabled = false;
            bPowerBi.Click += Safe.Wrap(delegate { ExportPowerBi(); });
            themed.Add(bPowerBi);
            search.Width = Dpi.S(260);
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Margin = new Padding(0, Dpi.S(5), Dpi.S(12), 0);
            search.TextChanged += delegate { Bind(); };
            bar.Controls.Add(since);
            bar.Controls.Add(bRefresh);
            bar.Controls.Add(bExport);
            bar.Controls.Add(bPowerBi);
            bar.Controls.Add(search);
            if (demo)
            {
                Label note = new Label();
                note.AutoSize = true;
                note.Margin = new Padding(0, Dpi.S(9), 0, 0);
                note.Text = "Demo data: made-up devices.";
                note.Tag = "quiet";
                bar.Controls.Add(note);
            }

            chips.Dock = DockStyle.Top;
            chips.Height = Dpi.S(74);
            chips.Padding = new Padding(Dpi.S(22), Dpi.S(6), Dpi.S(22), 0);
            chips.WrapContents = false;
            foreach (ValueChip v in new ValueChip[] { cRuns, cFinished, cFailed, cAvg, cMedian, cLongest })
            {
                v.Margin = new Padding(0, 0, Dpi.S(10), 0);
                chips.Controls.Add(v);
                themed.Add(v);
            }

            tabs.Dock = DockStyle.Top;
            tabs.SetTabs("Apps, scripts and steps", "Enrollments");
            tabs.SelectedChanged += delegate { Safe.Run(ShowTab); };
            themed.Add(tabs);

            Column<FleetItem> ic;
            items.Col("Name", 300, delegate (FleetItem f) { return f.Name; }).Fill = true;
            items.Col("Kind", 110, delegate (FleetItem f) { return f.Kind; });
            items.Col("Devices", 76, delegate (FleetItem f) { return f.Devices.ToString(CultureInfo.InvariantCulture); });
            items.Col("Failed", 70, delegate (FleetItem f) { return f.Failed.ToString(CultureInfo.InvariantCulture); });
            ic = items.Col("Fail rate", 80, delegate (FleetItem f) { return f.Done + f.Failed == 0 ? "" : f.FailRate.ToString("0", CultureInfo.InvariantCulture) + " %"; });
            ic.SortKey = delegate (FleetItem f) { return Num(f.Done + f.Failed == 0 ? -1 : f.FailRate); };
            ic = items.Col("Average", 100, delegate (FleetItem f) { return f.Timed == 0 ? "" : Txt.Duration(TimeSpan.FromSeconds(f.AvgSeconds)); });
            ic.SortKey = delegate (FleetItem f) { return Num(f.Timed == 0 ? -1 : f.AvgSeconds); };
            ic = items.Col("Longest", 100, delegate (FleetItem f) { return f.Timed == 0 ? "" : Txt.Duration(TimeSpan.FromSeconds(f.MaxSeconds)); });
            ic.SortKey = delegate (FleetItem f) { return Num(f.Timed == 0 ? -1 : f.MaxSeconds); };
            ic = items.Col("Download", 150, delegate (FleetItem f) { return f.Downloads == 0 ? "" : Downloads.Time(f.AvgDownloadSeconds) + (f.Speeds == 0 ? "" : ", " + Downloads.Speed(f.AvgDownloadMbit)); });
            ic.SortKey = delegate (FleetItem f) { return Num(f.Downloads == 0 ? -1 : f.AvgDownloadSeconds); };
            items.Col("Slowest on", 150, delegate (FleetItem f) { return f.SlowestOn; });
            items.ForeColor = delegate (FleetItem f) { return f.Failed > 0 ? theme.ErrFg : f.Timed == 0 ? theme.Text3 : Color.Empty; };
            items.EmptyText = "Reading the uploads…";
            themed.Add(items);

            Column<FleetRun> rc;
            runs.Col("Device", 140, delegate (FleetRun r) { return r.Computer; });
            runs.Col("Deployment", 170, delegate (FleetRun r) { return r.Kind; });
            runs.Col("Profile", 200, delegate (FleetRun r) { return r.Profile; }).Fill = true;
            rc = runs.Col("Started", 128, delegate (FleetRun r) { return r.StartUtc.HasValue ? Txt.Local(r.StartUtc.Value).Substring(0, 16) : ""; });
            rc.SortKey = delegate (FleetRun r) { return r.StartUtc.HasValue ? r.StartUtc.Value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : "0"; };
            rc = runs.Col("Duration", 150, delegate (FleetRun r) { return r.Seconds <= 0 ? "" : Txt.Duration(TimeSpan.FromSeconds(r.Seconds)) + (r.Finished ? "" : ", so far"); });
            rc.SortKey = delegate (FleetRun r) { return Num(r.Seconds); };
            runs.Col("Items", 62, delegate (FleetRun r) { return r.Items.ToString(CultureInfo.InvariantCulture); });
            runs.Col("Failed", 62, delegate (FleetRun r) { return r.Failed.ToString(CultureInfo.InvariantCulture); });
            if (reference != null) runs.Col("Reference", 150, delegate (FleetRun r) { string b; return r.Key != null && refBadges.TryGetValue(r.Key, out b) ? b : ""; });
            runs.Col("Slowest part", 240, delegate (FleetRun r) { return r.Slowest == null ? "" : r.Slowest + " (" + Txt.DurationShort(TimeSpan.FromSeconds(r.SlowestSeconds)) + ")"; }).Fill = true;
            runs.ForeColor = delegate (FleetRun r) { return r.Failed > 0 ? theme.ErrFg : !r.Finished ? theme.Accent : Color.Empty; };
            runs.EmptyText = "Reading the uploads…";
            runs.SelectionChanged += delegate { UpdateWatch(); };
            runs.Grid.CellDoubleClick += delegate (object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) Safe.Run(Pick); };
            themed.Add(runs);

            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Dpi.S(24), Dpi.S(4), Dpi.S(24), Dpi.S(4));
            border.Dock = DockStyle.Fill;
            border.Padding = new Padding(1);
            items.Grid.Dock = DockStyle.Fill;
            runs.Grid.Dock = DockStyle.Fill;
            runs.Grid.Visible = false;
            border.Controls.Add(items.Grid);
            border.Controls.Add(runs.Grid);
            card.Controls.Add(border);
            Panel tabWrap = new Panel();
            tabWrap.Dock = DockStyle.Top;
            tabWrap.Height = tabs.Height;
            tabWrap.Padding = new Padding(Dpi.S(14), 0, Dpi.S(24), 0);
            tabWrap.Tag = "bg";
            tabWrap.Controls.Add(tabs);

            footer.Dock = DockStyle.Bottom;
            footer.Height = Dpi.S(60);
            footer.Padding = new Padding(Dpi.S(24), Dpi.S(14), Dpi.S(24), Dpi.S(14));
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            bWatch.Primary = true;
            bWatch.Dock = DockStyle.Right;
            bWatch.Visible = false;
            bWatch.Enabled = false;
            bWatch.Click += Safe.Wrap(delegate { Pick(); });
            bClose.Primary = true;
            bClose.Width = Math.Max(bClose.Width, Dpi.S(96));
            bClose.Dock = DockStyle.Right;
            bClose.Click += delegate { Close(); };
            watchGap.Dock = DockStyle.Right;
            watchGap.Width = Dpi.S(8);
            watchGap.Visible = false;
            themed.Add(bWatch);
            themed.Add(bClose);
            footer.Controls.Add(status);
            footer.Controls.Add(bClose);
            footer.Controls.Add(watchGap);
            footer.Controls.Add(bWatch);

            Controls.Add(card);
            Controls.Add(tabWrap);
            Controls.Add(chips);
            Controls.Add(bar);
            Controls.Add(head);
            Controls.Add(footer);
            ApplyTheme(t);
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            HandleCreated += delegate
            {
                try { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Filter by name, kind or device"); } catch (Exception) { }
            };
            Shown += Safe.Wrap(delegate { LoadStats(); });
            FormClosed += delegate { if (cancel != null) cancel.Set(); };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // The tab shown: 0 for apps, scripts and steps, 1 for enrollments.
        public int Tab
        {
            get { return tabs.Selected; }
            set { tabs.Selected = value; ShowTab(); }
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            foreach (Control c in Controls) { c.BackColor = t.Bg; c.ForeColor = t.Text; }
            border.BackColor = t.Divider;
            title.ForeColor = t.Text;
            desc.ForeColor = t.Text3;
            status.ForeColor = t.Text3;
            foreach (Control c in bar.Controls)
            {
                Label l = c as Label;
                if (l != null) l.ForeColor = t.Text3;
            }
            search.BackColor = t.Field;
            search.ForeColor = t.Text;
            foreach (IThemed x in themed) x.ApplyTheme(t);
            if (IsHandleCreated) Draw.TitleBar(Handle, t);
        }

        void Status(string s)
        {
            status.Text = s;
        }

        void Ui(MethodInvoker m)
        {
            try { if (!IsDisposed) BeginInvoke((MethodInvoker)delegate { Safe.Run(delegate { m(); }); }); }
            catch (Exception) { }
        }

        void ShowTab()
        {
            bool enr = tabs.Selected == 1;
            runs.Grid.Visible = enr;
            items.Grid.Visible = !enr;
            bWatch.Visible = enr;
            watchGap.Visible = enr;
            // Close is the main button when there is no device to watch.
            bClose.Primary = !enr;
            bClose.Invalidate();
            UpdateWatch();
        }

        void UpdateWatch()
        {
            FleetRun r = runs.Current;
            bWatch.Enabled = r != null && r.Key != null && byKey.ContainsKey(r.Key);
        }

        // Reads the sessions on a thread. A load that is still running is stopped first.
        void LoadStats()
        {
            if (cancel != null) cancel.Set();
            ManualResetEvent stop = new ManualResetEvent(false);
            cancel = stop;
            AgentCatalog c = catalog;
            DateTime now = DateTime.UtcNow;
            DateTime from = now.AddHours(-SinceHours[Math.Max(0, Math.Min(SinceHours.Length - 1, since.Selected))]);
            Status("Listing the uploads in " + c.Source + "…");
            Thread th = new Thread(delegate ()
            {
                try
                {
                    List<RemoteDeployment> list = c.List(from);
                    int total = list.Count;
                    if (list.Count > MaxSessions) list.RemoveRange(MaxSessions, list.Count - MaxSessions);
                    List<Session> sessions = new List<Session>();
                    List<string> keys = new List<string>();
                    Dictionary<string, RemoteDeployment> map = new Dictionary<string, RemoteDeployment>();
                    int unreadable = 0;
                    DateTime said = DateTime.MinValue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (stop.WaitOne(0)) return;
                        if ((DateTime.UtcNow - said).TotalMilliseconds > 200)
                        {
                            said = DateTime.UtcNow;
                            int at = i, of = list.Count;
                            Ui(delegate { if (cancel == stop) Status("Reading upload " + (at + 1) + " of " + of + "…"); });
                        }
                        Session s = null;
                        try { s = c.Load(list[i].Key); }
                        catch (Exception) { }
                        if (s == null) { unreadable++; continue; }
                        sessions.Add(s);
                        keys.Add(list[i].Key);
                        map[list[i].Key] = list[i];
                    }
                    if (stop.WaitOne(0)) return;
                    FleetStats st = FleetStats.Build(sessions, keys, DateTime.UtcNow);
                    Dictionary<string, string> rb = ReferenceCheck.Badges(reference, sessions, keys);
                    Ui(delegate
                    {
                        if (cancel != stop) return;
                        refBadges = rb;
                        loaded = sessions;
                        loadedKeys = keys;
                        ShowStats(st, map, total, unreadable);
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate { if (cancel == stop) Status("Reading failed: " + ex.Message); });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        void ShowStats(FleetStats st, Dictionary<string, RemoteDeployment> map, int total, int unreadable)
        {
            stats = st;
            byKey = map;
            cRuns.Value = st.Runs.Count.ToString(CultureInfo.InvariantCulture);
            cFinished.Value = st.Finished.ToString(CultureInfo.InvariantCulture);
            cFailed.Value = st.WithFailures.ToString(CultureInfo.InvariantCulture);
            cFailed.Tone = st.WithFailures > 0 ? 2 : 0;
            cAvg.Value = Time(st.Finished, st.AvgSeconds);
            cMedian.Value = Time(st.Finished, st.MedianSeconds);
            cLongest.Value = Time(st.Finished, st.LongestSeconds);
            foreach (Control v in chips.Controls) v.Invalidate();
            items.EmptyText = "No apps, scripts or steps in this period.";
            runs.EmptyText = "No uploads in this period.";
            bExport.Enabled = st.Runs.Count > 0;
            bPowerBi.Enabled = st.Runs.Count > 0;
            Bind();
            string s = catalog.Source + " · " + st.Runs.Count + " enrollment" + (st.Runs.Count == 1 ? "" : "s");
            if (total > MaxSessions) s += ", the newest " + MaxSessions + " of " + total + " uploads";
            if (unreadable > 0) s += ", " + unreadable + " could not be read";
            s += ". Times count finished enrollments only · updated " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Status(s);
        }

        static string Time(int n, double seconds)
        {
            return n == 0 ? "–" : Txt.DurationShort(TimeSpan.FromSeconds(seconds));
        }

        void Bind()
        {
            if (stats == null) return;
            string q = search.Text.Trim();
            List<FleetItem> il = new List<FleetItem>();
            foreach (FleetItem f in stats.Items)
                if (q.Length == 0 || Has(f.Name, q) || Has(f.Kind, q) || Has(f.SlowestOn, q)) il.Add(f);
            List<FleetRun> rl = new List<FleetRun>();
            foreach (FleetRun r in stats.Runs)
                if (q.Length == 0 || Has(r.Computer, q) || Has(r.Kind, q) || Has(r.Profile, q) || Has(r.Slowest, q)) rl.Add(r);
            items.SetRows(il);
            runs.SetRows(rl);
            tabs.SetText(0, "Apps, scripts and steps (" + il.Count + ")");
            tabs.SetText(1, "Enrollments (" + rl.Count + ")");
            UpdateWatch();
        }

        static bool Has(string s, string q)
        {
            return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Export()
        {
            if (stats == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV (*.csv)|*.csv";
                d.FileName = "TrackVice statistics " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, stats.ToCsv() + "\r\n" + stats.RunsToCsv(), new UTF8Encoding(true));
                Status("Saved " + d.FileName);
            }
        }

        // Every enrollment of the period as flat tables for Power BI, in a folder.
        void ExportPowerBi()
        {
            if (loaded.Count == 0) return;
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Choose the folder for Enrollments.csv, Items.csv, Findings.csv and README.txt. Files from an earlier export there are replaced.";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                DataExport.Result r = DataExport.Write(d.SelectedPath, loaded, loadedKeys, DateTime.UtcNow, catalog.Source + ", the uploads of the last " + SinceHours[Math.Max(0, Math.Min(SinceHours.Length - 1, since.Selected))] + " hours");
                Status("Saved " + r.Enrollments + " enrollments, " + r.Items + " items and " + r.Findings + " findings in " + d.SelectedPath + ". README.txt says how to load them in Power BI.");
            }
        }

        void Pick()
        {
            FleetRun r = tabs.Selected == 1 ? runs.Current : null;
            RemoteDeployment d;
            if (r == null || r.Key == null || !byKey.TryGetValue(r.Key, out d)) return;
            Picked = d;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.F5) { Safe.Run(LoadStats); return true; }
            if (keyData == Keys.Enter && runs.Grid.Focused) { Safe.Run(Pick); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // A value with a label under it, in the style of the count chips, for numbers that are not counts.
    public sealed class ValueChip : FlatControl
    {
        public string Value = "–";
        public string Label;
        public int Tone;

        public ValueChip(string label, int tone)
        {
            Label = label;
            Tone = tone;
            Size = new Size(Math.Max(Dpi.S(124), TextRenderer.MeasureText(label, Fonts.Small).Width + Dpi.S(30)), Dpi.S(58));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(7), T.Card);
            Shapes.Stroke(g, r, Dpi.S(7), T.Divider, 1f);
            Color num = Tone == 2 ? T.ErrFg : Tone == 1 ? T.WarnFg : T.Text;
            int pad = Dpi.S(12);
            TextRenderer.DrawText(g, Value, Fonts.Title, new Rectangle(pad, Dpi.S(6), Width - pad, Dpi.S(28)), num, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Label, Fonts.Small, new Rectangle(pad, Dpi.S(34), Width - pad, Dpi.S(18)), T.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}
