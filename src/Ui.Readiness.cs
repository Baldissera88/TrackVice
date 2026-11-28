namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // One row of the results: a device, a part of the checks, or one check.
    public sealed class ReadyRow
    {
        public ReadyDevice Device;
        public string Group;
        public ReadyLine Line;
        public int Order;

        public bool IsDevice
        {
            get { return Group == null && Line == null; }
        }
    }

    // The optional permissions of the check before enrollment, kept between starts.
    public static class ReadinessScopes
    {
        public const string GroupScope = "https://graph.microsoft.com/Device.Read.All";

        public static bool Apps
        {
            get { return Prefs.GetBool("Readiness.Apps", true); }
            set { Prefs.SetBool("Readiness.Apps", value); }
        }

        public static bool Groups
        {
            get { return Prefs.GetBool("Readiness.Groups", false); }
            set { Prefs.SetBool("Readiness.Groups", value); }
        }

        // DeviceManagementServiceConfig.Read.All is needed; app names and groups are read only when picked.
        public static List<string> Wanted()
        {
            List<string> l = new List<string>();
            l.Add(ReadinessCheck.Scope);
            if (Apps) l.Add(LookupChoice.AppScope);
            if (Groups) l.Add(GroupScope);
            return l;
        }

        public static bool Missing(GraphClient g)
        {
            if (g == null || !g.SignedIn) return true;
            foreach (string s in Wanted()) if (!NamesDialog.Has(g, s)) return true;
            return false;
        }
    }

    // Checks devices before they are enrolled: registered in Autopilot, the profile and the Enrollment Status Page
    // they get, and what earlier enrollments say about the time the ESP needs.
    public sealed class ReadinessDialog : Form
    {
        public const int MaxHistory = 300;

        Theme theme;
        IGraph graph;
        readonly bool demo;
        readonly AgentCatalog catalog;
        List<Session> history;
        // Signs in with the permissions given, and returns the sign-in, or null when it was cancelled.
        public Func<List<string>, GraphClient> SignIn;
        public bool RunAtStart;

        readonly List<IThemed> themed = new List<IThemed>();
        readonly Panel head = new Panel();
        readonly Label title = new Label();
        readonly Label desc = new Label();
        readonly Panel left = new Panel();
        readonly Panel right = new Panel();
        readonly Label inTitle = new Label();
        readonly Label inHint = new Label();
        readonly TextBox serials = new TextBox();
        readonly FlowLayoutPanel buttons = new FlowLayoutPanel();
        readonly FlatButton bCheck = new FlatButton("Check", Glyphs.Search);
        readonly FlatButton bPick = new FlatButton("From Autopilot…", Glyphs2.Device);
        readonly TableLayoutPanel options = new TableLayoutPanel();
        readonly Toggle tApps = new Toggle();
        readonly Toggle tGroups = new Toggle();
        readonly Label lApps = new Label();
        readonly Label lGroups = new Label();
        readonly FlowLayoutPanel chips = new FlowLayoutPanel();
        readonly ValueChip cDevices = new ValueChip("Devices", 0);
        readonly ValueChip cReady = new ValueChip("Ready", 0);
        readonly ValueChip cWarn = new ValueChip("With warnings", 1);
        readonly ValueChip cBad = new ValueChip("With problems", 2);
        readonly Panel border = new Panel();
        readonly Table<ReadyRow> table = new Table<ReadyRow>();
        readonly Label detail = new Label();
        readonly Panel footer = new Panel();
        readonly Label status = new Label();
        readonly FlatButton bCsv = new FlatButton("Export CSV", Glyphs.Export);
        readonly FlatButton bHtml = new FlatButton("Export HTML", Glyphs.Export);
        readonly FlatButton bClose = new FlatButton("Close", null);
        List<ReadyDevice> results = new List<ReadyDevice>();
        string source;
        ManualResetEvent cancel;

        public ReadinessDialog(Theme t, IGraph g, AgentCatalog agents, bool isDemo)
        {
            theme = t;
            graph = g;
            catalog = agents;
            demo = isDemo;
            Text = "Check before enrollment";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(Dpi.S(1180), Dpi.S(760));
            MinimumSize = new Size(Dpi.S(940), Dpi.S(520));
            DialogFit.Keep(this);

            head.Dock = DockStyle.Top;
            head.Height = Dpi.S(78);
            title.Text = "Check devices before enrollment";
            title.Font = Fonts.Title;
            title.AutoSize = true;
            title.Location = new Point(Dpi.S(22), Dpi.S(12));
            desc.AutoSize = false;
            desc.Location = new Point(Dpi.S(24), Dpi.S(44));
            desc.Size = new Size(Dpi.S(1100), Dpi.S(34));
            desc.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            desc.Font = Fonts.Small;
            desc.Text = "Finds problems before a device is turned on: the Autopilot registration, the deployment profile, the Enrollment Status Page, and whether the ESP is likely to run out of time. Read-only.";
            head.Controls.Add(title);
            head.Controls.Add(desc);

            // The serial numbers and the buttons, on the left.
            left.Dock = DockStyle.Left;
            left.Width = Dpi.S(320);
            left.Padding = new Padding(Dpi.S(24), Dpi.S(6), Dpi.S(8), Dpi.S(4));
            inTitle.Text = "Serial numbers";
            inTitle.Font = Fonts.UiSemibold;
            inTitle.Dock = DockStyle.Top;
            inTitle.Height = Dpi.S(24);
            inHint.Text = "One per line, or paste a CSV such as a hardware hash export.";
            inHint.Font = Fonts.Small;
            inHint.Dock = DockStyle.Top;
            inHint.Height = Dpi.S(36);
            serials.Multiline = true;
            serials.AcceptsReturn = true;
            serials.ScrollBars = ScrollBars.Vertical;
            serials.WordWrap = false;
            serials.BorderStyle = BorderStyle.FixedSingle;
            serials.Font = Fonts.Mono;
            serials.Dock = DockStyle.Fill;
            serials.TextChanged += delegate { UpdateButtons(); };
            if (demo) serials.Text = DemoReadinessGraph.Serials;

            buttons.Dock = DockStyle.Bottom;
            buttons.Height = Dpi.S(48);
            buttons.Padding = new Padding(0, Dpi.S(10), 0, 0);
            buttons.WrapContents = false;
            bCheck.Primary = true;
            bCheck.Width = Math.Max(bCheck.Width, Dpi.S(104));
            bCheck.Margin = new Padding(0, 0, Dpi.S(8), 0);
            bCheck.Click += Safe.Wrap(delegate { Check(); });
            bPick.Compact = false;
            bPick.AutoWidth();
            bPick.Margin = new Padding(0);
            bPick.Click += Safe.Wrap(delegate { PickFromAutopilot(); });
            themed.Add(bCheck);
            themed.Add(bPick);
            buttons.Controls.Add(bCheck);
            buttons.Controls.Add(bPick);

            options.Dock = DockStyle.Bottom;
            options.Height = Dpi.S(124);
            options.Padding = new Padding(0, Dpi.S(12), 0, 0);
            options.ColumnCount = 2;
            options.RowCount = 2;
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(96)));
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(56)));
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(56)));
            Option(lApps, tApps, "App names of the blocking apps. Needs DeviceManagementApps.Read.All.", ReadinessScopes.Apps, 0);
            Option(lGroups, tGroups, "The device's groups, to tell which targeted ESP applies. Needs Device.Read.All.", ReadinessScopes.Groups, 1);
            tApps.Changed += delegate { ReadinessScopes.Apps = tApps.On; };
            tGroups.Changed += delegate { ReadinessScopes.Groups = tGroups.On; };

            left.Controls.Add(serials);
            left.Controls.Add(inHint);
            left.Controls.Add(inTitle);
            left.Controls.Add(buttons);
            left.Controls.Add(options);

            // The results, on the right.
            right.Dock = DockStyle.Fill;
            right.Padding = new Padding(Dpi.S(16), Dpi.S(6), Dpi.S(24), Dpi.S(4));
            chips.Dock = DockStyle.Top;
            chips.Height = Dpi.S(70);
            chips.WrapContents = false;
            foreach (ValueChip v in new ValueChip[] { cDevices, cReady, cWarn, cBad })
            {
                v.Margin = new Padding(0, 0, Dpi.S(10), 0);
                chips.Controls.Add(v);
                themed.Add(v);
            }

            table.Col("Device", 150, delegate (ReadyRow r) { return r.IsDevice ? r.Device.Serial : ""; });
            table.Col("Check", 230, delegate (ReadyRow r) { return r.IsDevice ? (r.Device.Title != r.Device.Serial ? r.Device.Title : "") : r.Line == null ? r.Group : "    " + r.Line.Check; });
            table.Col("Result", 120, delegate (ReadyRow r) { return r.IsDevice ? r.Device.Summary : r.Line == null ? "" : r.Line.LevelText; });
            table.Col("Details", 400, delegate (ReadyRow r) { return r.Line == null ? "" : r.Line.Text; }).Fill = true;
            // The rows keep their order: devices, then the checks of each in their parts.
            foreach (DataGridViewColumn c in table.Grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            table.ForeColor = delegate (ReadyRow r)
            {
                ReadyLevel l = r.IsDevice ? r.Device.Worst : r.Line != null ? r.Line.Level : ReadyLevel.Ok;
                if (r.IsDevice && l == ReadyLevel.Ok) return theme.OkFg;
                if (!r.IsDevice && r.Line == null) return theme.Text2;
                return l == ReadyLevel.Problem ? theme.ErrFg : l == ReadyLevel.Warning ? theme.WarnFg : Color.Empty;
            };
            table.Grid.CellFormatting += delegate (object s, DataGridViewCellFormattingEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= table.Rows.Count) return;
                ReadyRow r = table.Rows[e.RowIndex];
                if (r.IsDevice)
                {
                    e.CellStyle.Font = Fonts.UiSemibold;
                    e.CellStyle.BackColor = theme.Header;
                }
                else if (r.Line == null) e.CellStyle.Font = Fonts.SmallSemibold;
            };
            table.EmptyText = demo ? "Demo data: made-up Contoso devices.\n\nPress Check to check them." : "Enter serial numbers, or pick devices from Autopilot, and press Check.";
            table.SelectionChanged += delegate { ShowDetail(); };
            themed.Add(table);

            border.Dock = DockStyle.Fill;
            border.Padding = new Padding(1);
            table.Grid.Dock = DockStyle.Fill;
            border.Controls.Add(table.Grid);
            detail.Dock = DockStyle.Bottom;
            detail.Height = Dpi.S(58);
            detail.Padding = new Padding(Dpi.S(2), Dpi.S(8), 0, 0);
            detail.AutoEllipsis = true;
            right.Controls.Add(border);
            right.Controls.Add(detail);
            right.Controls.Add(chips);

            footer.Dock = DockStyle.Bottom;
            footer.Height = Dpi.S(60);
            footer.Padding = new Padding(Dpi.S(24), Dpi.S(14), Dpi.S(24), Dpi.S(14));
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            bCsv.Dock = DockStyle.Right;
            bCsv.Click += Safe.Wrap(delegate { Export(false); });
            bHtml.Dock = DockStyle.Right;
            bHtml.Click += Safe.Wrap(delegate { Export(true); });
            bClose.Width = Math.Max(bClose.Width, Dpi.S(96));
            bClose.Dock = DockStyle.Right;
            bClose.Click += delegate { Close(); };
            themed.Add(bCsv);
            themed.Add(bHtml);
            themed.Add(bClose);
            footer.Controls.Add(status);
            footer.Controls.Add(bCsv);
            footer.Controls.Add(Gap());
            footer.Controls.Add(bHtml);
            footer.Controls.Add(Gap());
            footer.Controls.Add(bClose);

            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(head);
            Controls.Add(footer);
            UpdateButtons();
            Status(demo ? "Demo data: a made-up Contoso tenant and earlier enrollments." : catalog != null ? "Predictions use the uploads in " + catalog.Source + "." : "No upload location of the agents is connected, so there are no predictions from earlier enrollments.");
            ApplyTheme(t);
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            Shown += Safe.Wrap(delegate { if (RunAtStart) Check(); });
            FormClosed += delegate { if (cancel != null) cancel.Set(); };
        }

        static Panel Gap()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Right;
            p.Width = Dpi.S(8);
            return p;
        }

        void Option(Label l, Toggle tg, string text, bool on, int row)
        {
            l.Text = text;
            l.Font = Fonts.Small;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(0, 0, Dpi.S(6), 0);
            tg.On = on;
            tg.Anchor = AnchorStyles.Right;
            tg.Margin = new Padding(0);
            themed.Add(tg);
            options.Controls.Add(l, 0, row);
            options.Controls.Add(tg, 1, row);
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            foreach (Control c in new Control[] { head, left, right, chips, buttons, options, footer, detail }) { c.BackColor = t.Bg; c.ForeColor = t.Text; }
            border.BackColor = t.Divider;
            title.ForeColor = t.Text;
            desc.ForeColor = t.Text3;
            inTitle.ForeColor = t.Text;
            inHint.ForeColor = t.Text3;
            lApps.ForeColor = t.Text2;
            lGroups.ForeColor = t.Text2;
            detail.ForeColor = t.Text2;
            status.ForeColor = t.Text3;
            serials.BackColor = t.Field;
            serials.ForeColor = t.Text;
            foreach (IThemed x in themed) x.ApplyTheme(t);
            table.Grid.Invalidate();
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

        bool Busy
        {
            get { return cancel != null && !cancel.WaitOne(0); }
        }

        void UpdateButtons()
        {
            bool busy = Busy;
            bCheck.Enabled = !busy && ReadyLines.ParseSerials(serials.Text).Count > 0;
            bPick.Enabled = !busy;
            bCsv.Enabled = !busy && results.Count > 0;
            bHtml.Enabled = !busy && results.Count > 0;
            tApps.Enabled = !demo;
            tGroups.Enabled = !demo;
        }

        void ShowDetail()
        {
            ReadyRow r = table.Current;
            if (r == null) { detail.Text = ""; return; }
            if (r.IsDevice) detail.Text = r.Device.Serial + (r.Device.Title != r.Device.Serial ? ", " + r.Device.Title : "") + ": " + r.Device.Summary + ".";
            else if (r.Line == null) detail.Text = r.Group;
            else detail.Text = r.Line.Check + ": " + r.Line.Text;
        }

        // A real sign-in asks again when it lacks a permission that is needed or picked.
        bool EnsureGraph()
        {
            if (demo) return true;
            GraphClient g = graph as GraphClient;
            if (!ReadinessScopes.Missing(g)) return true;
            if (SignIn == null) return false;
            g = SignIn(ReadinessScopes.Wanted());
            if (g == null || !g.SignedIn)
            {
                Status("Not signed in, so nothing was read from Intune.");
                return false;
            }
            graph = g;
            return true;
        }

        void Check()
        {
            List<string> list = ReadyLines.ParseSerials(serials.Text);
            if (list.Count == 0 || Busy) return;
            if (!EnsureGraph()) return;
            ManualResetEvent stop = new ManualResetEvent(false);
            cancel = stop;
            UpdateButtons();
            IGraph g = graph;
            AgentCatalog c = catalog;
            List<Session> known = history;
            DateTime now = DateTime.UtcNow;
            GraphClient gc = g as GraphClient;
            string from = demo ? "Demo data: Contoso" : gc != null && !string.IsNullOrEmpty(gc.Account) ? "Intune, signed in as " + gc.Account : "Intune";
            Status(known == null && c != null ? "Reading the earlier enrollments in " + c.Source + "…" : "Checking…");
            Thread th = new Thread(delegate ()
            {
                List<ReadyDevice> done = new List<ReadyDevice>();
                string note = null;
                try
                {
                    if (known == null && demo) known = DemoReadinessGraph.History(now);
                    else if (known == null && c != null)
                    {
                        try
                        {
                            known = c.LoadAll(now.AddDays(-30), MaxHistory, delegate (int i, int n)
                            {
                                if (i % 10 == 0 && i < n) Ui(delegate { if (cancel == stop) Status("Reading earlier enrollment " + (i + 1) + " of " + n + "…"); });
                            });
                        }
                        catch (Exception ex) { note = "The earlier enrollments were not read: " + ex.Message; }
                    }
                    ReadinessCheck rc = new ReadinessCheck(g);
                    rc.History = known;
                    rc.NowUtc = now;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (stop.WaitOne(0)) return;
                        int at = i;
                        Ui(delegate { if (cancel == stop) Status("Checking " + list[at] + ", " + (at + 1) + " of " + list.Count + "…"); });
                        done.Add(rc.Check(list[i]));
                    }
                }
                catch (Exception ex) { note = "The check stopped: " + ex.Message; }
                List<Session> keep = known;
                Ui(delegate
                {
                    if (cancel != stop) return;
                    history = keep;
                    stop.Set();
                    source = from;
                    Show(done, note);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        void Show(List<ReadyDevice> list, string note)
        {
            results = list;
            int ready = 0, bad = 0;
            foreach (ReadyDevice d in list)
            {
                if (d.Worst == ReadyLevel.Ok) ready++;
                else if (d.Worst == ReadyLevel.Problem) bad++;
            }
            cDevices.Value = list.Count.ToString(CultureInfo.InvariantCulture);
            cReady.Value = ready.ToString(CultureInfo.InvariantCulture);
            cWarn.Value = (list.Count - ready - bad).ToString(CultureInfo.InvariantCulture);
            cBad.Value = bad.ToString(CultureInfo.InvariantCulture);
            foreach (Control v in chips.Controls) v.Invalidate();
            List<ReadyRow> rows = new List<ReadyRow>();
            foreach (ReadyDevice d in list)
            {
                ReadyRow dr = new ReadyRow();
                dr.Device = d;
                dr.Order = rows.Count;
                rows.Add(dr);
                string group = null;
                foreach (ReadyLine l in d.Lines)
                {
                    if (l.Group != group)
                    {
                        group = l.Group;
                        ReadyRow gr = new ReadyRow();
                        gr.Device = d;
                        gr.Group = group;
                        gr.Order = rows.Count;
                        rows.Add(gr);
                    }
                    ReadyRow lr = new ReadyRow();
                    lr.Device = d;
                    lr.Group = group;
                    lr.Line = l;
                    lr.Order = rows.Count;
                    rows.Add(lr);
                }
            }
            table.EmptyText = "No devices were checked.";
            table.SetRows(rows);
            UpdateButtons();
            string s = list.Count + (list.Count == 1 ? " device" : " devices") + " checked: " + ready + " ready, " + (list.Count - ready - bad) + " with warnings, " + bad + " with problems.";
            if (history != null) s += " Predictions from " + history.Count + (history.Count == 1 ? " earlier enrollment." : " earlier enrollments.");
            if (note != null) s += " " + note;
            Status(s);
        }

        void Export(bool html)
        {
            if (results.Count == 0) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = html ? "HTML report (*.html)|*.html" : "CSV (*.csv)|*.csv";
                d.FileName = "TrackVice check before enrollment " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (html ? ".html" : ".csv");
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (html) File.WriteAllText(d.FileName, Report.ReadinessHtml(results, source ?? "Intune", DateTime.UtcNow), Encoding.UTF8);
                else File.WriteAllText(d.FileName, ReadyLines.ToCsv(results), new UTF8Encoding(true));
                Status("Saved " + d.FileName);
                if (html)
                {
                    // Only the web page that was just saved is opened, never a file that Windows would run.
                    try { if (LaunchGuard.IsReportFile(d.FileName)) Process.Start(d.FileName); }
                    catch (Exception) { }
                }
            }
        }

        void PickFromAutopilot()
        {
            if (Busy || !EnsureGraph()) return;
            using (AutopilotPicker p = new AutopilotPicker(theme, graph))
            {
                p.ShowDialog(this);
                if (p.Picked.Count == 0) return;
                List<string> have = ReadyLines.ParseSerials(serials.Text);
                foreach (string s in p.Picked) if (!have.Contains(s)) have.Add(s);
                serials.Text = string.Join("\r\n", have.ToArray());
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Enter) || keyData == Keys.F5) { Safe.Run(Check); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // The devices registered in Windows Autopilot, to pick the ones to check.
    public sealed class AutopilotPicker : Form
    {
        public const int MaxDevices = 5000;
        public readonly List<string> Picked = new List<string>();

        Theme theme;
        readonly IGraph graph;
        readonly List<IThemed> themed = new List<IThemed>();
        readonly Panel head = new Panel();
        readonly Label title = new Label();
        readonly FlowLayoutPanel bar = new FlowLayoutPanel();
        readonly TextBox search = new TextBox();
        readonly Panel card = new Panel();
        readonly Panel border = new Panel();
        readonly Table<AutopilotEntry> table = new Table<AutopilotEntry>();
        readonly Panel footer = new Panel();
        readonly Label status = new Label();
        readonly FlatButton bAdd = new FlatButton("Add", null);
        readonly FlatButton bClose = new FlatButton("Cancel", null);
        List<AutopilotEntry> all = new List<AutopilotEntry>();

        public AutopilotPicker(Theme t, IGraph g)
        {
            theme = t;
            graph = g;
            Text = "Devices in Windows Autopilot";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(Dpi.S(980), Dpi.S(620));
            MinimumSize = new Size(Dpi.S(700), Dpi.S(420));
            DialogFit.Keep(this);

            head.Dock = DockStyle.Top;
            head.Height = Dpi.S(52);
            title.Text = "Devices in Windows Autopilot";
            title.Font = Fonts.Title;
            title.AutoSize = true;
            title.Location = new Point(Dpi.S(22), Dpi.S(12));
            head.Controls.Add(title);

            bar.Dock = DockStyle.Top;
            bar.Height = Dpi.S(42);
            bar.Padding = new Padding(Dpi.S(22), Dpi.S(4), Dpi.S(22), 0);
            bar.WrapContents = false;
            search.Width = Dpi.S(300);
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Margin = new Padding(0, Dpi.S(5), Dpi.S(12), 0);
            search.TextChanged += delegate { Bind(); };
            Label hint = new Label();
            hint.AutoSize = true;
            hint.Margin = new Padding(0, Dpi.S(8), 0, 0);
            hint.Text = "Select one or more with Ctrl or Shift.";
            hint.Tag = "quiet";
            bar.Controls.Add(search);
            bar.Controls.Add(hint);

            table.Col("Serial number", 150, delegate (AutopilotEntry e) { return e.Serial; });
            table.Col("Manufacturer", 110, delegate (AutopilotEntry e) { return e.Manufacturer; });
            table.Col("Model", 180, delegate (AutopilotEntry e) { return e.Model; }).Fill = true;
            table.Col("Group tag", 110, delegate (AutopilotEntry e) { return e.GroupTag; });
            table.Col("Profile", 150, delegate (AutopilotEntry e) { return e.Profile; });
            table.Col("Enrollment", 110, delegate (AutopilotEntry e) { return e.State; });
            Column<AutopilotEntry> lc = table.Col("Last contact", 110, delegate (AutopilotEntry e) { return e.LastContactUtc.HasValue ? Txt.Ago(e.LastContactUtc.Value, DateTime.UtcNow) : ""; });
            lc.SortKey = delegate (AutopilotEntry e) { return e.LastContactUtc.HasValue ? e.LastContactUtc.Value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : "0"; };
            table.Grid.MultiSelect = true;
            table.EmptyText = "Reading the Autopilot devices…";
            table.SelectionChanged += delegate { bAdd.Enabled = table.Grid.SelectedRows.Count > 0; };
            table.Grid.CellDoubleClick += delegate (object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) Safe.Run(Add); };
            themed.Add(table);

            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Dpi.S(24), Dpi.S(4), Dpi.S(24), Dpi.S(4));
            border.Dock = DockStyle.Fill;
            border.Padding = new Padding(1);
            table.Grid.Dock = DockStyle.Fill;
            border.Controls.Add(table.Grid);
            card.Controls.Add(border);

            footer.Dock = DockStyle.Bottom;
            footer.Height = Dpi.S(60);
            footer.Padding = new Padding(Dpi.S(24), Dpi.S(14), Dpi.S(24), Dpi.S(14));
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            bAdd.Primary = true;
            bAdd.Width = Math.Max(bAdd.Width, Dpi.S(96));
            bAdd.Dock = DockStyle.Right;
            bAdd.Enabled = false;
            bAdd.Click += Safe.Wrap(delegate { Add(); });
            bClose.Width = Math.Max(bClose.Width, Dpi.S(96));
            bClose.Dock = DockStyle.Right;
            bClose.Click += delegate { Close(); };
            themed.Add(bAdd);
            themed.Add(bClose);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            footer.Controls.Add(status);
            footer.Controls.Add(bAdd);
            footer.Controls.Add(gap);
            footer.Controls.Add(bClose);

            Controls.Add(card);
            Controls.Add(bar);
            Controls.Add(head);
            Controls.Add(footer);
            ApplyTheme(t);
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            HandleCreated += delegate
            {
                try { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Filter by serial number, model or group tag"); } catch (Exception) { }
            };
            Shown += Safe.Wrap(delegate { ReadList(); });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            foreach (Control c in Controls) { c.BackColor = t.Bg; c.ForeColor = t.Text; }
            border.BackColor = t.Divider;
            title.ForeColor = t.Text;
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

        void ReadList()
        {
            status.Text = "Reading the Autopilot devices…";
            IGraph g = graph;
            Thread th = new Thread(delegate ()
            {
                List<AutopilotEntry> l = null;
                string err = null;
                try { l = ReadinessCheck.ListIdentities(g, MaxDevices); }
                catch (Exception ex) { err = IntuneLookup.Problem("The Autopilot devices", ReadinessCheck.Scope, ex); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Safe.Run(delegate
                        {
                            if (err != null) { table.EmptyText = err; table.Grid.Invalidate(); status.Text = ""; return; }
                            all = l;
                            table.EmptyText = "No devices are registered in Windows Autopilot.";
                            Bind();
                            status.Text = l.Count + (l.Count == 1 ? " device" : " devices") + (l.Count >= MaxDevices ? ", the first " + MaxDevices : "") + ".";
                        });
                    });
                }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void Bind()
        {
            string q = search.Text.Trim();
            List<AutopilotEntry> l = new List<AutopilotEntry>();
            foreach (AutopilotEntry e in all)
                if (q.Length == 0 || Has(e.Serial, q) || Has(e.Model, q) || Has(e.Manufacturer, q) || Has(e.GroupTag, q) || Has(e.User, q)) l.Add(e);
            table.SetRows(l);
        }

        static bool Has(string s, string q)
        {
            return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Add()
        {
            List<int> rows = new List<int>();
            foreach (DataGridViewRow r in table.Grid.SelectedRows) rows.Add(r.Index);
            rows.Sort();
            foreach (int i in rows) if (i >= 0 && i < table.Rows.Count) Picked.Add(table.Rows[i].Serial);
            if (Picked.Count == 0) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Enter && table.Grid.Focused) { Safe.Run(Add); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    public sealed partial class MainForm
    {
        // Checks devices before they are enrolled. Signs in to Intune with the permissions it needs, and uses the
        // uploads of the agents for the predictions when an upload location is connected.
        void OpenReadiness(bool demo)
        {
            IGraph g = demo ? (IGraph)new DemoReadinessGraph(DateTime.UtcNow) : graph;
            AgentCatalog c = demo ? null : agentCatalog as AgentCatalog;
            using (ReadinessDialog d = new ReadinessDialog(theme, g, c, demo))
            {
                d.SignIn = delegate (List<string> scopes)
                {
                    using (RemoteDialog r = new RemoteDialog(RemoteDialog.Intune, theme, null, null, false))
                    {
                        r.Text = "Sign in to check devices before enrollment";
                        r.NamesOnly = true;
                        r.MoreScopes = scopes;
                        r.ShowDialog(d);
                        if (r.Graph == null || !r.Graph.SignedIn) return null;
                        intuneCatalog = r.Catalog;
                        graph = r.Graph;
                        KeepSignIn(graph);
                        return graph;
                    }
                };
                d.ShowDialog(this);
            }
        }
    }
}
