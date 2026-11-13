namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    public sealed class MainForm : Form
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int TabProgress = 0, TabTimeline = 1, TabFindings = 2, TabSession = 3;

        Theme theme;
        Engine engine;
        bool busy, watching, loadedOnce;
        readonly StartOptions start;
        readonly BusyBar busyBar = new BusyBar();
        readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer clockTimer = new System.Windows.Forms.Timer();
        DateTime lastUpdate = DateTime.MinValue;
        CompactView compact;
        IRemoteCatalog intuneCatalog, sccmCatalog, agentCatalog;
        GraphClient graph;

        readonly List<IThemed> themed = new List<IThemed>();
        readonly Panel header = new Panel();
        readonly Label title = new Label();
        readonly Label subtitle = new Label();
        readonly FlowLayoutPanel toolbar = new FlowLayoutPanel();
        readonly Panel banner = new Panel();
        readonly Label bannerText = new Label();
        readonly FlowLayoutPanel chips = new FlowLayoutPanel();
        readonly PhaseRail rail = new PhaseRail();
        readonly TabStrip tabs = new TabStrip();
        readonly Panel content = new Panel();
        readonly Panel statusBar = new Panel();
        readonly Label statusLeft = new Label();
        readonly Label statusRight = new Label();
        readonly Control[] views = new Control[4];
        readonly List<Control> plain = new List<Control>();

        FlatButton bWatch, bOpen, bDevices, bSave, bExport, bLogVice, bCompact, bTheme, bSettings, bAbout, bAdmin;
        OverallChip overall;
        StatChip cItems, cActive, cWaiting, cFailed, cFindings;
        string chipFilter;

        readonly ItemList list = new ItemList();
        readonly RichTextBox itemDetail = Detail();
        readonly FlatButton bItemLog = new FlatButton("Open in LogVice", Glyphs.Log);
        readonly FlatButton bItemCopy = new FlatButton("Copy details", Glyphs.Copy);
        readonly TextBox search = new TextBox();
        readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer();
        string shownSig;

        readonly Table<TEvent> timeline = new Table<TEvent>();
        readonly RichTextBox eventDetail = Detail();
        readonly TextBox tlSearch = new TextBox();
        readonly FlatButton bProblems = new FlatButton("Errors and warnings", null);
        readonly FlatButton bThisItem = new FlatButton("Selected item only", null);
        bool problemsOnly, thisItemOnly;
        int timelineCount = -1;

        readonly Table<Finding> findings = new Table<Finding>();
        readonly RichTextBox findingDetail = Detail();
        readonly RichTextBox sessionView = Detail();

        public MainForm(StartOptions o)
        {
            start = o ?? new StartOptions();
            Safe.Reporter = delegate (Exception ex) { Status("That did not work: " + ex.Message); };
            Safe.Status = delegate (string s) { Status(s); };
            Text = "TrackVice";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Dpi.S(1320), Dpi.S(860));
            MinimumSize = new Size(Dpi.S(940), Dpi.S(600));
            if (start.Shot == null) LoadBounds();
            if (!start.Size.IsEmpty)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(0, 0);
                Size = start.Size;
            }
            BuildHeader();
            BuildViews();
            Controls.Add(content);
            Controls.Add(tabs);
            Controls.Add(rail);
            Controls.Add(chips);
            Controls.Add(banner);
            busyBar.Dock = DockStyle.Top;
            Controls.Add(busyBar);
            themed.Add(busyBar);
            Controls.Add(header);
            Controls.Add(statusBar);
            theme = Theme.Named(start.Theme ?? Prefs.Get("Theme", "System"));
            ApplyTheme();
            tabs.SelectedChanged += Safe.Wrap(delegate { ShowView(); });
            ShowView();
            pollTimer.Tick += Safe.Wrap(delegate { Poll(); });
            Finder.StuckAfter = TimeSpan.FromMinutes(Settings.StuckMinutes);
            clockTimer.Interval = 1000;
            clockTimer.Tick += Safe.Wrap(delegate { Tick(); });
            clockTimer.Start();
            Shown += Safe.Wrap(delegate
            {
                if (start.Remote != null && start.Pick != null)
                {
                    IRemoteCatalog c = RemoteDialog.DemoCatalog(start.Remote);
                    foreach (RemoteDeployment d in c.List(DateTime.UtcNow.AddDays(-30)))
                        if (d.Device.IndexOf(start.Pick, StringComparison.OrdinalIgnoreCase) >= 0) { Begin(Engine.ForRemote(c.Open(d), d), true); return; }
                }
                if (start.Remote != null && start.Shot == null) { Begin(Engine.ForDemo("autopilot"), true); OpenRemote(start.Remote, true); return; }
                if (start.Demo != null) Begin(Engine.ForDemo(start.Demo), true);
                else if (start.File != null) Begin(Engine.ForFile(start.File), false);
                else if (start.Folder != null) Begin(Engine.ForFolder(start.Folder), false);
                else Begin(Settings.NewLive(), true);
            });
        }

        static int Hours
        {
            get { return Settings.Hours; }
        }

        // ---------- building ----------

        void BuildHeader()
        {
            header.Dock = DockStyle.Top;
            header.Height = Dpi.S(64);
            title.Text = "TrackVice";
            title.Font = Fonts.Title;
            title.AutoSize = true;
            title.Location = new Point(Dpi.S(20), Dpi.S(8));
            subtitle.AutoSize = true;
            subtitle.Font = Fonts.Small;
            subtitle.Location = new Point(Dpi.S(22), Dpi.S(38));
            toolbar.Dock = DockStyle.Right;
            toolbar.AutoSize = true;
            toolbar.WrapContents = false;
            toolbar.FlowDirection = FlowDirection.LeftToRight;
            toolbar.Padding = new Padding(0, Dpi.S(6), Dpi.S(12), 0);

            bWatch = Button("Watch", Glyphs.Watch, delegate { ToggleWatch(); });
            bOpen = Button("Open", Glyphs2.Folder, delegate { OpenMenu().Show(bOpen, new Point(0, bOpen.Height)); });
            bDevices = Button("Devices", Glyphs2.Apps, delegate { DevicesMenu().Show(bDevices, new Point(0, bDevices.Height)); });
            bSave = Button("Save", Glyphs.Save, delegate { SaveMenu().Show(bSave, new Point(0, bSave.Height)); });
            bExport = Button("Export", Glyphs.Export, delegate { ExportMenu().Show(bExport, new Point(0, bExport.Height)); });
            bLogVice = Button("LogVice", Glyphs.Log, delegate { LogViceMenu().Show(bLogVice, new Point(0, bLogVice.Height)); });
            bCompact = Button("Compact", Glyphs2.Device, delegate { ToggleCompact(); });
            bTheme = Button("", Glyphs.Theme, delegate { CycleTheme(); });
            bSettings = Button("", Glyphs2.Settings, delegate { OpenSettings(); });
            bAbout = Button("", Glyphs.Info, delegate { About(); });
            if (Fonts.Icons == null)
            {
                bTheme.Text = "Theme";
                bSettings.Text = "Settings";
                bAbout.Text = "About";
            }
            Tip(bWatch, "Follow the device live, or pause (Ctrl+W)");
            Tip(bOpen, "Watch this device, open a saved session or a folder of logs, or play a demo (Ctrl+O)");
            Tip(bDevices, "Follow other devices: Autopilot deployments from Intune, or task sequences from your SCCM site (Ctrl+D)");
            Tip(bSave, "Save the session, or collect it with its logs in a zip (Ctrl+S)");
            Tip(bExport, "Export an HTML report or every event as CSV (Ctrl+E)");
            Tip(bLogVice, "Open the deployment logs in LogVice");
            Tip(bCompact, "A small window that stays on top, for OOBE and for watching over a shoulder (Ctrl+M)");
            Tip(bTheme, "Change the theme");
            Tip(bSettings, "Settings: history to read, how often to read, sources, LogVice (Ctrl+,)");
            Tip(bAbout, "About TrackVice and the command line (F1)");
            foreach (FlatButton b in new FlatButton[] { bWatch, bOpen, bDevices, bSave, bExport, bLogVice, bCompact, bTheme, bSettings, bAbout })
            {
                b.Margin = new Padding(Dpi.S(2), 0, 0, 0);
                toolbar.Controls.Add(b);
            }
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(toolbar);

            banner.Dock = DockStyle.Top;
            banner.Height = Dpi.S(44);
            banner.Padding = new Padding(Dpi.S(20), Dpi.S(6), Dpi.S(20), Dpi.S(6));
            banner.Visible = false;
            bannerText.Dock = DockStyle.Fill;
            bannerText.TextAlign = ContentAlignment.MiddleLeft;
            bannerText.Text = "TrackVice is not running as administrator. The ConfigMgr and Intune logs, the ESP keys and some event logs need administrator rights.";
            bAdmin = new FlatButton("Restart as administrator", Glyphs.Shield);
            bAdmin.Primary = true;
            bAdmin.Dock = DockStyle.Right;
            bAdmin.Click += Safe.Wrap(delegate { if (Program.RestartElevated()) Close(); else Status("TrackVice was not restarted as administrator."); });
            themed.Add(bAdmin);
            banner.Controls.Add(bannerText);
            banner.Controls.Add(bAdmin);

            chips.Dock = DockStyle.Top;
            chips.Height = Dpi.S(74);
            chips.Padding = new Padding(Dpi.S(18), Dpi.S(8), Dpi.S(18), Dpi.S(4));
            chips.WrapContents = false;
            overall = new OverallChip();
            overall.Margin = new Padding(0, 0, Dpi.S(10), 0);
            overall.Cursor = Cursors.Hand;
            overall.Click += Safe.Wrap(delegate { SetChip(null); });
            themed.Add(overall);
            chips.Controls.Add(overall);
            Tip(overall, "How far the deployment has come, counting the items TrackVice has seen. Click to show everything.");
            cItems = Chip("Items", 0, delegate { SetChip(null); });
            cActive = Chip("In progress", 0, delegate { SetChip("active"); });
            cWaiting = Chip("Waiting", 0, delegate { SetChip("waiting"); });
            cFailed = Chip("Failed", 2, delegate { SetChip("failed"); });
            cFindings = Chip("Findings", 1, delegate { tabs.Selected = TabFindings; });
            Tip(cActive, "Downloading, installing or detecting now");
            Tip(cWaiting, "Known, but not started yet");
            Tip(cFailed, "Failed items");
            Tip(cFindings, "What needs attention: failures, retries, restarts, time limits");

            rail.Dock = DockStyle.Top;
            rail.PhaseClicked += delegate (Phase p) { Safe.Run(delegate { tabs.Selected = TabProgress; rail.Selected = p.Key; list.SelectPhase(p.Key); list.Focus(); }); };
            themed.Add(rail);

            tabs.Dock = DockStyle.Top;
            tabs.SetTabs("Progress", "Timeline", "Findings", "Session");
            themed.Add(tabs);

            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(Dpi.S(16), Dpi.S(10), Dpi.S(16), Dpi.S(8));

            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = Dpi.S(28);
            statusBar.Padding = new Padding(Dpi.S(18), 0, Dpi.S(18), 0);
            statusLeft.Dock = DockStyle.Fill;
            statusLeft.TextAlign = ContentAlignment.MiddleLeft;
            statusLeft.AutoEllipsis = true;
            statusRight.Dock = DockStyle.Right;
            statusRight.AutoSize = true;
            statusRight.TextAlign = ContentAlignment.MiddleRight;
            statusRight.Padding = new Padding(0, Dpi.S(6), 0, 0);
            statusBar.Controls.Add(statusLeft);
            statusBar.Controls.Add(statusRight);
        }

        void BuildViews()
        {
            // Progress
            Panel bar = FilterBar();
            FlowLayoutPanel fl = (FlowLayoutPanel)bar.Controls[0];
            search.Width = Dpi.S(320);
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Font = Fonts.Ui;
            search.Margin = new Padding(0, Dpi.S(6), Dpi.S(10), 0);
            Cue(search, "Search items, IDs and command lines (Ctrl+F)");
            searchTimer.Interval = 200;
            searchTimer.Tick += Safe.Wrap(delegate { searchTimer.Stop(); ApplyFilter(); });
            search.TextChanged += delegate { searchTimer.Stop(); searchTimer.Start(); };
            fl.Controls.Add(search);
            FlatButton clear = Small("Clear filters", delegate { search.Text = ""; SetChip(null); });
            fl.Controls.Add(clear);
            list.SelectionChanged += Safe.Wrap(delegate { ShowSelection(true); });
            list.SpinTick += delegate { rail.Tick(list.Spin); };
            list.MenuExtras = delegate (ContextMenuStrip m, Item it)
            {
                Menus.Add(m, "Open in LogVice", delegate { OpenItemLog(it); }).Enabled = it.LogFile != null;
                Menus.Add(m, "Show on the timeline", delegate { thisItemOnly = true; bThisItem.Checked = true; tabs.Selected = TabTimeline; BindTimeline(true); });
            };
            themed.Add(list);
            Panel detailHost = new Panel();
            detailHost.Dock = DockStyle.Fill;
            Panel foot = new Panel();
            foot.Dock = DockStyle.Bottom;
            foot.Height = Dpi.S(52);
            foot.Padding = new Padding(Dpi.S(12), Dpi.S(10), Dpi.S(12), Dpi.S(10));
            foot.Tag = "card-foot";
            bItemLog.Dock = DockStyle.Right;
            bItemLog.Click += Safe.Wrap(delegate { OpenItemLog(list.SelectedItem); });
            bItemCopy.Dock = DockStyle.Left;
            bItemCopy.Click += Safe.Wrap(delegate { Menus.Copy(itemDetail.Text.Trim()); });
            Tip(bItemLog, "Open the newest log this item has a line in (Ctrl+L)");
            themed.Add(bItemLog);
            themed.Add(bItemCopy);
            foot.Controls.Add(bItemLog);
            foot.Controls.Add(bItemCopy);
            itemDetail.Dock = DockStyle.Fill;
            detailHost.Controls.Add(itemDetail);
            detailHost.Controls.Add(foot);
            plain.Add(foot);
            views[TabProgress] = Split(list, detailHost, bar);

            // Timeline
            Panel tbar = FilterBar();
            FlowLayoutPanel tfl = (FlowLayoutPanel)tbar.Controls[0];
            tlSearch.Width = Dpi.S(320);
            tlSearch.BorderStyle = BorderStyle.FixedSingle;
            tlSearch.Font = Fonts.Ui;
            tlSearch.Margin = new Padding(0, Dpi.S(6), Dpi.S(10), 0);
            Cue(tlSearch, "Search events");
            tlSearch.TextChanged += delegate { searchTimer.Stop(); searchTimer.Start(); };
            tfl.Controls.Add(tlSearch);
            bProblems.Compact = true;
            bProblems.AutoWidth();
            bProblems.Margin = new Padding(0, Dpi.S(2), Dpi.S(4), 0);
            bProblems.Click += Safe.Wrap(delegate { problemsOnly = !problemsOnly; bProblems.Checked = problemsOnly; BindTimeline(true); });
            bThisItem.Compact = true;
            bThisItem.AutoWidth();
            bThisItem.Margin = new Padding(0, Dpi.S(2), 0, 0);
            bThisItem.Click += Safe.Wrap(delegate { thisItemOnly = !thisItemOnly; bThisItem.Checked = thisItemOnly; BindTimeline(true); });
            themed.Add(bProblems);
            themed.Add(bThisItem);
            tfl.Controls.Add(bProblems);
            tfl.Controls.Add(bThisItem);
            timeline.Col("Time", 92, delegate (TEvent e) { return Txt.LocalTime(e.TimeUtc); }).Mono = true;
            timeline.Grid.Columns[0].DefaultCellStyle.Font = Fonts.Mono;
            timeline.Col("Source", 120, delegate (TEvent e) { return e.Source; });
            timeline.Col("Item", 220, delegate (TEvent e) { Item i = engine == null ? null : engine.S.Find(e.ItemKey); return i != null ? i.DisplayName : ""; }).Fill = true;
            timeline.Col("State", 104, delegate (TEvent e) { return e.StateName != null ? States.Label(e.State) : ""; });
            timeline.Col("Event", 420, delegate (TEvent e) { return e.Text; }).Fill = true;
            timeline.ForeColor = delegate (TEvent e) { return e.Severity >= 2 ? theme.ErrFg : e.Severity == 1 ? theme.WarnFg : Color.Empty; };
            timeline.SelectionChanged += delegate { ShowEvent(timeline.Current); };
            timeline.EmptyText = "No events match.";
            timeline.MenuExtras = delegate (ContextMenuStrip m, TEvent e)
            {
                if (e.Evidence != null && e.Evidence.Text != null) Menus.Add(m, "Copy the raw line", delegate { Menus.Copy(e.Evidence.Text); });
                if (e.Evidence != null && e.Evidence.IsLogFile) Menus.Add(m, "Open " + Txt.FileName(e.Evidence.Path) + " in LogVice", delegate { OpenInLogVice(e.Evidence.Path); });
                Item it = engine.S.Find(e.ItemKey);
                if (it != null) Menus.Add(m, "Go to " + Txt.Short(it.DisplayName, 40), delegate { GoTo(it.Key); });
            };
            themed.Add(timeline);
            views[TabTimeline] = Split(timeline.Grid, eventDetail, tbar);

            // Findings
            findings.Col("Severity", 80, delegate (Finding f) { return f.SeverityName; });
            findings.Col("Finding", 380, delegate (Finding f) { return f.Title; }).Fill = true;
            findings.Col("Kind", 150, delegate (Finding f) { return f.Kind; });
            findings.Col("Time", 92, delegate (Finding f) { return Txt.LocalTime(f.TimeUtc); });
            findings.ForeColor = delegate (Finding f) { return f.Severity >= 2 ? theme.ErrFg : f.Severity == 1 ? theme.WarnFg : Color.Empty; };
            findings.SelectionChanged += delegate { ShowFinding(findings.Current); };
            findings.EmptyText = "Nothing needs attention right now.";
            findings.MenuExtras = delegate (ContextMenuStrip m, Finding f)
            {
                if (f.ItemKey != null) Menus.Add(m, "Go to the item", delegate { GoTo(f.ItemKey); });
                if (f.Evidence != null && f.Evidence.IsLogFile) Menus.Add(m, "Open " + Txt.FileName(f.Evidence.Path) + " in LogVice", delegate { OpenInLogVice(f.Evidence.Path); });
            };
            findings.Grid.CellDoubleClick += delegate { Safe.Run(delegate { Finding f = findings.Current; if (f != null && f.ItemKey != null) GoTo(f.ItemKey); }); };
            themed.Add(findings);
            views[TabFindings] = Split(findings.Grid, findingDetail, null);

            // Session
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Controls.Add(Card(sessionView));
            views[TabSession] = host;

            foreach (Control v in views)
            {
                v.Visible = false;
                content.Controls.Add(v);
            }
        }

        Panel FilterBar()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Top;
            p.Height = Dpi.S(42);
            p.Padding = new Padding(0, 0, 0, Dpi.S(8));
            FlowLayoutPanel fl = new FlowLayoutPanel();
            fl.Dock = DockStyle.Fill;
            fl.WrapContents = false;
            p.Controls.Add(fl);
            plain.Add(p);
            plain.Add(fl);
            return p;
        }

        FlatButton Small(string text, EventHandler click)
        {
            FlatButton b = new FlatButton(text, null);
            b.Compact = true;
            b.AutoWidth();
            b.Margin = new Padding(0, Dpi.S(2), 0, 0);
            b.Click += Safe.Wrap(click);
            themed.Add(b);
            return b;
        }

        Control Split(Control left, Control right, Control top)
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            SplitContainer sp = new SplitContainer();
            sp.Dock = DockStyle.Fill;
            sp.Orientation = Orientation.Vertical;
            sp.SplitterWidth = Dpi.S(10);
            sp.FixedPanel = FixedPanel.Panel2;
            sp.Panel1.Controls.Add(Card(left));
            sp.Panel2.Controls.Add(Card(right));
            host.Controls.Add(sp);
            if (top != null) host.Controls.Add(top);
            sp.Tag = false;
            host.Resize += delegate
            {
                try
                {
                    int want = Math.Min(Dpi.S(470), Math.Max(Dpi.S(300), host.Width * 36 / 100));
                    if (sp.Width > want + Dpi.S(240) && Math.Abs(sp.Width - sp.SplitterDistance - sp.SplitterWidth - want) > Dpi.S(4) && !sp.Tag.Equals(true))
                        sp.SplitterDistance = sp.Width - want - sp.SplitterWidth;
                }
                catch (Exception) { }
            };
            sp.SplitterMoved += delegate { if (sp.Focused) sp.Tag = true; };
            return host;
        }

        Panel Card(Control inner)
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Padding = new Padding(1);
            p.Tag = "card";
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);
            return p;
        }

        static RichTextBox Detail()
        {
            RichTextBox r = new RichTextBox();
            r.ReadOnly = true;
            r.BorderStyle = BorderStyle.None;
            r.DetectUrls = false;
            r.WordWrap = true;
            r.ScrollBars = RichTextBoxScrollBars.Vertical;
            return r;
        }

        FlatButton Button(string text, string glyph, EventHandler click)
        {
            FlatButton b = new FlatButton(text, glyph);
            b.Click += Safe.Wrap(click);
            themed.Add(b);
            return b;
        }

        StatChip Chip(string label, int tone, EventHandler click)
        {
            StatChip c = new StatChip(label, tone);
            c.Margin = new Padding(0, 0, Dpi.S(10), 0);
            c.Click += Safe.Wrap(click);
            themed.Add(c);
            chips.Controls.Add(c);
            return c;
        }

        readonly ToolTip tips = new ToolTip();

        void Tip(Control c, string text)
        {
            tips.SetToolTip(c, text);
        }

        static void Cue(TextBox box, string text)
        {
            try { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); } catch (Exception) { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // ---------- theme ----------

        void CycleTheme()
        {
            string[] order = { "System", "Light", "Dark", "Vice" };
            string cur = Prefs.Get("Theme", "System");
            int i = Array.IndexOf(order, cur);
            string next = order[(i + 1) % order.Length];
            Prefs.Set("Theme", next);
            theme = Theme.Named(next);
            ApplyTheme();
            Status("Theme: " + (next == "System" ? "follow Windows (" + theme.Name + ")" : next));
        }

        void ApplyTheme()
        {
            Theme t = theme;
            BackColor = t.Bg;
            ForeColor = t.Text;
            foreach (Control c in new Control[] { header, toolbar, chips, content, statusBar })
            {
                c.BackColor = t.Bg;
                c.ForeColor = t.Text;
            }
            title.ForeColor = t.Text;
            subtitle.ForeColor = t.Text3;
            banner.BackColor = t.WarnBg;
            bannerText.ForeColor = t.Text;
            statusLeft.ForeColor = t.Text3;
            statusRight.ForeColor = t.Text3;
            foreach (Control v in views) Recolor(v, t);
            foreach (Control c in plain) c.BackColor = "card-foot".Equals(c.Tag) ? t.Card : t.Bg;
            foreach (TextBox b in new TextBox[] { search, tlSearch })
            {
                b.BackColor = t.Field;
                b.ForeColor = t.Text;
            }
            foreach (RichTextBox r in new RichTextBox[] { itemDetail, eventDetail, findingDetail, sessionView })
            {
                r.BackColor = t.Card;
                r.ForeColor = t.Text;
            }
            foreach (IThemed x in themed) x.ApplyTheme(t);
            Draw.TitleBar(Handle, t);
            if (compact != null) compact.Retheme(t);
            shownSig = null;
            ShowSelection(false);
            ShowEvent(timeline.Current);
            ShowFinding(findings.Current);
            ShowSession();
            Invalidate(true);
        }

        void Recolor(Control c, Theme t)
        {
            if (c is SplitContainer)
            {
                c.BackColor = t.Bg;
                ((SplitContainer)c).Panel1.BackColor = t.Bg;
                ((SplitContainer)c).Panel2.BackColor = t.Bg;
            }
            else if (c is Panel && "card".Equals(c.Tag)) c.BackColor = t.Divider;
            else if (c is Panel && "card-foot".Equals(c.Tag)) c.BackColor = t.Card;
            else if (c is Panel || c is FlowLayoutPanel) c.BackColor = t.Bg;
            foreach (Control k in c.Controls) Recolor(k, t);
        }

        // ---------- engine ----------

        void Begin(Engine e, bool watch)
        {
            pollTimer.Stop();
            if (engine != null) engine.Dispose();
            engine = e;
            loadedOnce = false;
            watching = false;
            timelineCount = -1;
            shownSig = null;
            chipFilter = null;
            rail.Selected = null;
            banner.Visible = e.Mode == Modes.Live && !Program.IsAdmin;
            list.EmptyText = e.Mode == Modes.Live
                ? "TrackVice found no deployment on this device in " + Settings.HoursText(Hours) + ".\n\nIt reads smsts.log, the ConfigMgr client logs, the Intune Management Extension logs, the ESP tracking keys and ConfigMgr client WMI. Start a deployment and it shows up here, or change History to read in Settings to look further back."
                : e.IsRemote ? "Reading this deployment\u2026" : "Nothing found.";
            rail.EmptyText = e.Mode == Modes.Live ? "Phases show up here as soon as a deployment starts." : "No phases.";
            Bind();
            busyBar.Running = true;
            Status(e.Mode == Modes.Live ? "Reading this device…" : e.Mode == Modes.Demo ? "Starting the demo…" : "Reading…");
            if (start.At >= 0 && e.Mode == Modes.Demo)
            {
                e.Apply(e.DemoTo(start.At * 60));
                Loaded(e, null, true);
                return;
            }
            RunPoll(e, watch);
        }

        void RunPoll(Engine e, bool thenWatch)
        {
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Batch b = null;
                Exception err = null;
                try { b = e.Poll(); }
                catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Safe.Run(delegate
                        {
                            busy = false;
                            if (e != engine) return;
                            if (err == null) e.Apply(b);
                            Loaded(e, err, thenWatch);
                        });
                    });
                }
                catch (Exception) { }
            });
        }

        void Loaded(Engine e, Exception err, bool thenWatch)
        {
            busyBar.Running = false;
            if (err != null) Status("Reading failed: " + err.Message);
            lastUpdate = DateTime.UtcNow;
            bool first = !loadedOnce;
            loadedOnce = true;
            Bind();
            if (first)
            {
                bool auto = e.Mode != Modes.Live || Settings.WatchOnStart;
                if (thenWatch && auto && e.CanWatch && start.Shot == null && start.At < 0) SetWatching(true);
                else SetWatching(false);
                Status(err != null ? "Reading failed: " + err.Message : Summary());
                if (err != null && e.IsRemote) list.EmptyText = "This deployment could not be read.\n\n" + err.Message;
                if (err != null) list.Rebuild();
                if (e.StartedBeforeHistory.HasValue)
                    Status("This deployment started " + Txt.Local(e.StartedBeforeHistory.Value) + ", before the history TrackVice reads, so older items show only their saved state. Settings > History to read > Since install reads everything.");
                SelectInteresting();
                if (start.Shot != null) Shot();
                else if (start.Compact) ToggleCompact();
            }
        }

        string Summary()
        {
            Session s = engine.S;
            if (s.Items.Count == 0) return engine.Mode == Modes.Live ? "No deployment activity found yet." : "Nothing found.";
            return s.Kind + ": " + s.Items.Count + " items, " + s.CountComplete + " done, " + s.CountActive + " in progress, " + s.Count(State.Failed) + " failed.";
        }

        // On first load, select what most needs a look: a failure, else what is running.
        void SelectInteresting()
        {
            Session s = engine.S;
            if (start.Select != null && list.SelectByName(start.Select)) return;
            Item pick = null;
            foreach (Item i in s.Items) if (i.State == State.Failed) { pick = i; break; }
            if (pick == null) foreach (Item i in s.Items) if (States.IsActive(i.State)) { pick = i; break; }
            if (pick != null) list.SelectKey(pick.Key, true);
        }

        void Poll()
        {
            if (busy || !watching || engine == null) return;
            if (!engine.CanWatch)
            {
                SetWatching(false);
                if (engine.Mode == Modes.Demo) Status("The demo has finished. Open > Play a demo to start it again.");
                return;
            }
            RunPoll(engine, false);
        }

        void SetWatching(bool on)
        {
            watching = on && engine != null && engine.CanWatch;
            bWatch.Checked = watching;
            bWatch.Text = watching ? "Watching" : "Watch";
            bWatch.Enabled = engine != null && (engine.CanWatch || watching);
            pollTimer.Interval = engine != null ? engine.PollIntervalMs : 2000;
            if (watching) pollTimer.Start(); else pollTimer.Stop();
            UpdateStatusRight();
        }

        void ToggleWatch()
        {
            if (engine == null) return;
            if (!engine.CanWatch)
            {
                Status(engine.Mode == Modes.File ? "A saved session does not change. Open > Watch this device to follow it live." : engine.Mode == Modes.Folder ? "A folder of logs is read once. Open > Watch this device to follow it live." : "The demo has finished.");
                return;
            }
            SetWatching(!watching);
            Status(watching ? "Watching: TrackVice reads the device every " + (engine.PollIntervalMs / 1000.0).ToString("0.#", Inv) + " second" + (engine.PollIntervalMs == 1000 ? "" : "s") + "." : "Paused.");
        }

        void Tick()
        {
            if (engine == null) return;
            UpdateStatusRight();
            // Running items get their durations moved on even when nothing new arrived.
            if (watching && engine.S.CountActive > 0)
            {
                overall.Elapsed = Elapsed();
                overall.Invalidate();
                rail.Invalidate();
                list.Invalidate();
            }
            if (compact != null && !compact.IsDisposed) compact.UpdateFrom(engine, watching);
        }

        void UpdateStatusRight()
        {
            if (engine == null) { statusRight.Text = "TrackVice " + Program.Version; return; }
            List<string> parts = new List<string>();
            if (engine.Mode == Modes.Demo) parts.Add("Demo data, " + engine.DemoSpeed.ToString("0", Inv) + "× speed");
            else if (engine.Mode == Modes.Intune) parts.Add("Intune, read every " + (engine.PollIntervalMs / 1000) + " s");
            else if (engine.Mode == Modes.Sccm) parts.Add("SCCM status messages, read every " + (engine.PollIntervalMs / 1000) + " s");
            else if (engine.Mode == Modes.Agent) parts.Add(engine.LastUploadUtc.Year > 2000 ? "Agent, uploaded " + Txt.LocalTime(engine.LastUploadUtc) : "Agent");
            else if (engine.Mode == Modes.File) parts.Add("Saved session");
            else if (engine.Mode == Modes.Folder) parts.Add("Log folder");
            if (watching) parts.Add("Watching · updated " + Txt.Ago(lastUpdate, DateTime.UtcNow));
            else if (engine.Mode == Modes.Live || engine.IsRemote) parts.Add("Paused");
            if (engine.SourceCount > 0) parts.Add(engine.SourceCount + " source" + (engine.SourceCount == 1 ? "" : "s"));
            parts.Add("TrackVice " + Program.Version);
            statusRight.Text = string.Join("  ·  ", parts.ToArray());
        }

        string Elapsed()
        {
            DateTime? f = engine.S.FirstUtc;
            return f.HasValue ? Txt.DurationShort(engine.Now - f.Value) : "";
        }

        // ---------- binding ----------

        void Bind()
        {
            Session s = engine.S;
            List<string> sub = new List<string>();
            if (engine.Mode == Modes.Demo) sub.Add("Demo data");
            else if (engine.Mode == Modes.Intune) sub.Add("Intune");
            else if (engine.Mode == Modes.Sccm) sub.Add("SCCM");
            else if (engine.Mode == Modes.Agent) sub.Add("Agent");
            sub.Add(s.Computer);
            if (s.Items.Count > 0) sub.Add(s.Kind);
            string profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            if (profile != null) sub.Add(profile);
            if (!string.IsNullOrEmpty(s.User) && engine.Mode != Modes.Folder) sub.Add(s.User);
            if (!string.IsNullOrEmpty(s.OsVersion)) sub.Add(s.OsVersion);
            DateTime? f = s.FirstUtc;
            if (f.HasValue) sub.Add("started " + Txt.Local(f.Value));
            subtitle.Text = string.Join(" · ", sub.ToArray());

            List<Phase> phases = new List<Phase>(s.OrderedPhases);
            double prog = 0;
            int total = 0;
            foreach (Phase p in phases) { prog += p.Progress * p.Items.Count; total += p.Items.Count; }
            overall.Progress = total == 0 ? 0 : prog / total;
            State os = State.Unknown;
            if (s.CountActive > 0) os = State.Installing;
            else if (s.Count(State.Failed) > 0 || (s.GetInfo("Task sequence result") ?? "").StartsWith("Failed")) os = State.Failed;
            else if (total > 0 && s.CountComplete == total) os = State.Succeeded;
            else if (total > 0) os = State.Pending;
            overall.State = os;
            overall.Elapsed = Elapsed();
            overall.Invalidate();
            cItems.Count = s.Items.Count;
            cActive.Count = s.CountActive;
            cWaiting.Count = s.Count(State.Pending) + s.Count(State.Unknown);
            cFailed.Count = s.Count(State.Failed);
            int warn = 0;
            foreach (Finding x in s.Findings) if (x.Severity >= 1) warn++;
            cFindings.Count = warn;
            cItems.Selected = chipFilter == null && search.Text.Length == 0 && s.Items.Count > 0;
            cActive.Selected = chipFilter == "active";
            cWaiting.Selected = chipFilter == "waiting";
            cFailed.Selected = chipFilter == "failed";
            foreach (StatChip c in new StatChip[] { cItems, cActive, cWaiting, cFailed, cFindings }) c.Invalidate();

            rail.SetData(phases, delegate { return engine.Now; });
            list.SetData(phases, delegate { return engine.Now; });
            tabs.SetText(TabProgress, "Progress (" + s.Items.Count + ")");
            tabs.SetText(TabTimeline, "Timeline (" + s.Events.Count + ")");
            tabs.SetText(TabFindings, "Findings (" + s.Findings.Count + ")");
            findings.SetRows(new List<Finding>(s.Findings));
            if (tabs.Selected == TabTimeline || timelineCount < 0) BindTimeline(false);
            if (tabs.Selected == TabSession) ShowSession();
            ShowSelection(false);
            if (compact != null && !compact.IsDisposed) compact.UpdateFrom(engine, watching);
            UpdateStatusRight();
            Text = (s.Items.Count > 0 ? ((int)Math.Round(overall.Progress * 100)).ToString(Inv) + " % · " : "") + s.Computer + " - TrackVice";
        }

        void SetChip(string f)
        {
            chipFilter = f;
            tabs.Selected = TabProgress;
            ApplyFilter();
        }

        void ApplyFilter()
        {
            string q = search.Text.Trim();
            string chip = chipFilter;
            if (q.Length == 0 && chip == null) list.Filter = null;
            else list.Filter = delegate (Item i)
            {
                if (chip == "active" && !States.IsActive(i.State)) return false;
                if (chip == "failed" && i.State != State.Failed) return false;
                if (chip == "waiting" && i.State != State.Pending && i.State != State.Unknown) return false;
                if (q.Length == 0) return true;
                if (Has(i.DisplayName, q) || Has(i.Key, q) || Has(i.Kind, q)) return true;
                foreach (Pair p in i.Info) if (Has(p.Value, q)) return true;
                return false;
            };
            list.Rebuild();
            BindTimeline(true);
            if (engine != null) Bind();
        }

        static bool Has(string s, string q)
        {
            return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void BindTimeline(bool force)
        {
            if (engine == null) return;
            Session s = engine.S;
            if (!force && s.Events.Count == timelineCount) return;
            timelineCount = s.Events.Count;
            string q = tlSearch.Text.Trim();
            Item only = thisItemOnly ? list.SelectedItem : null;
            List<TEvent> rows = new List<TEvent>();
            for (int k = s.Events.Count - 1; k >= 0; k--)
            {
                TEvent e = s.Events[k];
                if (problemsOnly && e.Severity < 1) continue;
                if (thisItemOnly && (only == null || e.ItemKey != only.Key)) continue;
                if (q.Length > 0 && !Has(e.Text, q) && !Has(e.Source, q))
                {
                    Item i = s.Find(e.ItemKey);
                    if (i == null || !Has(i.DisplayName, q)) continue;
                }
                rows.Add(e);
            }
            timeline.SetRows(rows);
            timeline.EmptyText = thisItemOnly && only == null ? "Select an item on the Progress tab first." : "No events match.";
        }

        void ShowView()
        {
            for (int i = 0; i < views.Length; i++) views[i].Visible = i == tabs.Selected;
            if (tabs.Selected == TabTimeline) BindTimeline(false);
            if (tabs.Selected == TabSession) ShowSession();
        }

        void GoTo(string key)
        {
            tabs.Selected = TabProgress;
            if (!list.SelectKey(key))
            {
                search.Text = "";
                chipFilter = null;
                ApplyFilter();
                list.SelectKey(key);
            }
            list.Focus();
        }

        void Status(string text)
        {
            statusLeft.Text = text;
        }

        // ---------- detail panes ----------

        void Heading(RichTextBox r, string text)
        {
            r.SelectionFont = Fonts.Title;
            r.SelectionColor = theme.Text;
            r.AppendText(text + "\n");
        }

        void Section(RichTextBox r, string text)
        {
            r.SelectionFont = Fonts.UiSemibold;
            r.SelectionColor = theme.Text;
            r.AppendText("\n" + text + "\n");
        }

        void Field(RichTextBox r, string label, string value, bool mono)
        {
            if (string.IsNullOrEmpty(value)) return;
            r.SelectionFont = Fonts.Small;
            r.SelectionColor = theme.Text3;
            r.AppendText(label + "\n");
            r.SelectionFont = mono ? Fonts.Mono : Fonts.Ui;
            r.SelectionColor = theme.Text;
            r.AppendText(value + "\n");
        }

        void Line(RichTextBox r, string tag, Color tagColor, string text, Color textColor)
        {
            r.SelectionFont = Fonts.UiSemibold;
            r.SelectionColor = tagColor;
            r.AppendText(tag + "  ");
            r.SelectionFont = Fonts.Ui;
            r.SelectionColor = textColor.IsEmpty ? theme.Text : textColor;
            r.AppendText(text + "\n");
        }

        void Plain(RichTextBox r, string text, Color c, Font f)
        {
            r.SelectionFont = f ?? Fonts.Ui;
            r.SelectionColor = c.IsEmpty ? theme.Text : c;
            r.AppendText(text + "\n");
        }

        void Begin(RichTextBox r)
        {
            r.SuspendLayout();
            r.Clear();
            r.SelectionIndent = Dpi.S(12);
            r.SelectionRightIndent = Dpi.S(8);
            r.AppendText("\n");
        }

        void End(RichTextBox r)
        {
            r.SelectionStart = 0;
            r.ScrollToCaret();
            r.ResumeLayout();
        }

        Color Sev(int s)
        {
            return s >= 2 ? theme.ErrFg : s == 1 ? theme.WarnFg : theme.Text3;
        }

        // Re-renders the detail only when the selection or what it shows changed, so reading is not disturbed while watching.
        void ShowSelection(bool force)
        {
            if (engine == null) return;
            Item i = list.SelectedItem;
            Phase p = i == null ? list.SelectedPhase : null;
            if (p != null) rail.Selected = p.Key;
            else if (i != null) rail.Selected = i.PhaseKey;
            rail.Invalidate();
            string sig = i != null ? "i|" + i.Key + "|" + i.StateName + "|" + i.Events.Count + "|" + i.Percent + "|" + i.Name + "|" + engine.S.Findings.Count
                : p != null ? "p|" + p.Key + "|" + p.Items.Count + "|" + p.Done + "|" + p.State : "none|" + engine.S.Items.Count;
            if (!force && sig == shownSig) return;
            shownSig = sig;
            bItemLog.Enabled = i != null && i.LogFile != null;
            bItemCopy.Enabled = i != null || p != null;
            if (thisItemOnly) BindTimeline(true);
            if (i != null) ShowItem(i);
            else if (p != null) ShowPhase(p);
            else ShowOverview();
        }

        void ShowItem(Item i)
        {
            RichTextBox r = itemDetail;
            DateTime now = engine.Now;
            Begin(r);
            Heading(r, i.DisplayName);
            Phase ph = engine.S.FindPhase(i.PhaseKey);
            List<string> where = new List<string>();
            if (!string.IsNullOrEmpty(i.Kind)) where.Add(i.Kind);
            if (!string.IsNullOrEmpty(i.Origin)) where.Add(i.Origin);
            if (ph != null) where.Add(ph.Name);
            Line(r, StateLook.StateText(i), StateLook.Color(i.State, theme), string.Join(" · ", where.ToArray()), theme.Text3);

            foreach (Finding f in engine.S.Findings)
            {
                if (f.ItemKey != i.Key) continue;
                Section(r, f.Title);
                Plain(r, f.Message, Color.Empty, null);
                if (!string.IsNullOrEmpty(f.Hint)) Plain(r, f.Hint, theme.Text3, null);
            }

            bool explained = false;
            foreach (Finding f in engine.S.Findings) if (f.ItemKey == i.Key && f.Severity >= 2) explained = true;
            Section(r, States.IsActive(i.State) ? "Now" : "Result");
            if (!explained && i.ExitCode.HasValue && i.ExitCode.Value != 0) Field(r, i.State == State.Failed ? "Error" : "Exit code", Errors.Describe(i.ExitCode.Value), false);
            Field(r, "Installer exit code", i.Get("Installer exit code"), true);
            if (States.IsActive(i.State) && i.Percent.HasValue) Field(r, "Progress", i.Percent.Value + " %", false);
            if (i.StartUtc.HasValue) Field(r, "Started", Txt.Local(i.StartUtc.Value), false);
            if (i.EndUtc.HasValue) Field(r, "Ended", Txt.Local(i.EndUtc.Value), false);
            Field(r, States.IsActive(i.State) ? "Running for" : "Took", Txt.Duration(i.Duration(now)), false);
            Field(r, "Last heard of", i.LastUtc > DateTime.MinValue ? Txt.Local(i.LastUtc) + " (" + Txt.Ago(i.LastUtc, now) + ")" : null, false);

            if (i.Attempts.Count > 0)
            {
                Section(r, i.Attempts.Count == 1 ? "Attempt" : i.Attempts.Count + " attempts");
                foreach (Attempt a in i.Attempts)
                {
                    StringBuilder b = new StringBuilder();
                    b.Append(Txt.LocalTime(a.StartUtc));
                    if (a.EndUtc.HasValue) b.Append(" → ").Append(Txt.LocalTime(a.EndUtc.Value)).Append("  (").Append(Txt.Duration(a.EndUtc.Value - a.StartUtc)).Append(")");
                    b.Append("  ").Append(States.Label(a.State));
                    if (a.ExitCode.HasValue) b.Append(", ").Append(Errors.Short(a.ExitCode));
                    if (a.Reboot) b.Append(", restart needed");
                    Line(r, "#" + a.Index, StateLook.Color(a.State, theme), b.ToString(), Color.Empty);
                }
            }

            if (i.Info.Count > 0)
            {
                Section(r, "Details");
                foreach (Pair p in i.Info)
                {
                    if (p.Key == "Installer exit code") continue;
                    bool mono = p.Key == "Command line" || p.Key.EndsWith("ID") || p.Key == "Content path" || p.Key == "Cache folder";
                    Field(r, p.Key, p.Value, mono);
                }
            }

            Section(r, "Events" + (i.Events.Count > 40 ? " (newest 40 of " + i.Events.Count + ")" : ""));
            int from = Math.Max(0, i.Events.Count - 40);
            for (int k = i.Events.Count - 1; k >= from; k--)
            {
                TEvent e = i.Events[k];
                r.SelectionFont = Fonts.MonoSmall;
                r.SelectionColor = theme.Text3;
                r.AppendText(Txt.LocalTime(e.TimeUtc) + "  ");
                r.SelectionFont = Fonts.Small;
                r.SelectionColor = theme.Text3;
                r.AppendText((e.Source ?? "") + "  ");
                r.SelectionFont = Fonts.Ui;
                r.SelectionColor = e.Severity >= 2 ? theme.ErrFg : e.Severity == 1 ? theme.WarnFg : theme.Text;
                r.AppendText(Txt.Short(e.Text, 400) + "\n");
            }
            Evidence ev = i.LastEvidence;
            if (ev != null)
            {
                Section(r, "Evidence");
                Field(r, "Where", ev.Where, false);
                if (ev.Kind == EvidenceKinds.LogLine) Field(r, "Path", ev.Path, true);
                Field(r, "Raw", Txt.Short(ev.Text, 2000), true);
            }
            End(r);
        }

        void ShowPhase(Phase p)
        {
            RichTextBox r = itemDetail;
            DateTime now = engine.Now;
            Begin(r);
            Heading(r, p.Name);
            State st = p.State;
            Line(r, States.Label(st), StateLook.Color(st, theme), p.Done + " of " + p.Items.Count + " done" + (p.FailedCount > 0 ? ", " + p.FailedCount + " failed" : ""), theme.Text3);
            Section(r, "Time");
            if (p.StartUtc.HasValue) Field(r, "Started", Txt.Local(p.StartUtc.Value), false);
            if (p.EndUtc.HasValue) Field(r, "Ended", Txt.Local(p.EndUtc.Value), false);
            Field(r, p.EndUtc.HasValue ? "Took" : "Running for", Txt.Duration(p.Duration(now)), false);
            if (p.Key == Phases.DeviceSetup || p.Key == Phases.AccountSetup) Field(r, "ESP time limit", engine.S.GetInfo("ESP time limit (min)") != null ? engine.S.GetInfo("ESP time limit (min)") + " min" : null, false);
            Section(r, "Items");
            foreach (Item i in p.Items)
                Line(r, States.Label(i.State), StateLook.Color(i.State, theme), i.DisplayName + (i.Duration(now).HasValue ? "  ·  " + Txt.Duration(i.Duration(now)) : ""), Color.Empty);
            End(r);
        }

        void ShowOverview()
        {
            RichTextBox r = itemDetail;
            Begin(r);
            Session s = engine.S;
            Heading(r, s.Items.Count == 0 ? "Waiting for a deployment" : s.Kind);
            if (s.Items.Count == 0)
            {
                Plain(r, engine.Mode == Modes.Live ? "TrackVice is watching this device. Items show up here as soon as a task sequence, an SCCM install, Autopilot or an Intune app starts." : "Nothing was found in what was read.", theme.Text2, null);
            }
            else
            {
                Plain(r, "Select an item or a phase to see what happened, with the log line behind each step.", theme.Text3, null);
                if (s.Findings.Count > 0)
                {
                    Section(r, "Needs attention");
                    foreach (Finding f in s.Findings) Line(r, f.SeverityName, Sev(f.Severity), f.Title, Color.Empty);
                }
            }
            End(r);
        }

        void ShowEvent(TEvent e)
        {
            RichTextBox r = eventDetail;
            Begin(r);
            if (e == null || engine == null)
            {
                Plain(r, "Select an event to see the line behind it.", theme.Text3, null);
                End(r);
                return;
            }
            Item it = engine.S.Find(e.ItemKey);
            Heading(r, it != null ? it.DisplayName : e.Source);
            Line(r, e.Severity >= 2 ? "Error" : e.Severity == 1 ? "Warning" : "Info", Sev(e.Severity), Txt.Local(e.TimeUtc) + " · " + e.Source, theme.Text3);
            Section(r, "Event");
            Plain(r, e.Text, Color.Empty, null);
            if (e.StateName != null) Field(r, "State after this", States.Label(e.State), false);
            if (e.Evidence != null)
            {
                Section(r, "Evidence");
                Field(r, "Where", e.Evidence.Where, false);
                if (e.Evidence.Kind != EvidenceKinds.EventRecord) Field(r, "Path", e.Evidence.Path, true);
                Field(r, "Raw", Txt.Short(e.Evidence.Text, 4000), true);
            }
            End(r);
        }

        void ShowFinding(Finding f)
        {
            RichTextBox r = findingDetail;
            Begin(r);
            if (f == null || engine == null)
            {
                Plain(r, engine != null && engine.S.Findings.Count == 0 ? "Nothing needs attention right now." : "Select a finding.", theme.Text3, null);
                End(r);
                return;
            }
            Heading(r, f.Title);
            Item it = engine.S.Find(f.ItemKey);
            Line(r, f.SeverityName, Sev(f.Severity), f.Kind + (it != null ? " · " + it.DisplayName : ""), theme.Text3);
            Section(r, "What happened");
            Plain(r, f.Message, Color.Empty, null);
            if (!string.IsNullOrEmpty(f.Hint))
            {
                Section(r, "What to check");
                Plain(r, f.Hint, Color.Empty, null);
            }
            if (f.Evidence != null)
            {
                Section(r, "Evidence");
                Field(r, "Where", f.Evidence.Where, false);
                Field(r, "Raw", Txt.Short(f.Evidence.Text, 2000), true);
            }
            if (it != null) Plain(r, "\nDouble-click the finding to go to the item.", theme.Text3, Fonts.Small);
            End(r);
        }

        void ShowSession()
        {
            if (engine == null) return;
            RichTextBox r = sessionView;
            Session s = engine.S;
            Begin(r);
            Heading(r, s.Computer);
            Line(r, engine.Mode == Modes.Live ? "This device" : engine.Mode == Modes.Demo ? "Demo" : engine.Mode == Modes.File ? "Saved session" : engine.Mode == Modes.Intune ? "Intune" : engine.Mode == Modes.Sccm ? "SCCM" : engine.Mode == Modes.Agent ? "Uploaded by the TrackVice agent" : "Log folder", theme.Accent, s.Kind, theme.Text3);
            Section(r, "Device");
            Field(r, "Windows", s.OsVersion, false);
            Field(r, "User", s.User, false);
            Field(r, "Administrator", engine.Mode == Modes.Live ? (s.Elevated ? "Yes" : "No: some logs and keys may be missing") : null, false);
            if (engine.Mode == Modes.Live) Field(r, "History read", "Since " + Txt.Local(s.SinceUtc) + " (" + Settings.HoursText(Hours) + ")", false);
            if (engine.Mode == Modes.File) Field(r, "File", s.SourcePath, true);
            if (engine.Mode == Modes.Folder) Field(r, "Folder", s.SourcePath, true);
            if (s.Info.Count > 0)
            {
                Section(r, "Deployment");
                foreach (Pair p in s.Info) if (p.Key != "Task sequence ended") Field(r, p.Key, p.Value, p.Key.EndsWith("ID"));
            }
            List<string> src = engine.Sources;
            if (src.Count > 0)
            {
                Section(r, "Logs read (" + src.Count + ")");
                src.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string p in src) Plain(r, p, theme.Text2, Fonts.MonoSmall);
            }
            if (engine.Mode == Modes.Live)
            {
                Section(r, "Also read");
                Plain(r, "ESP tracking: " + EspWatch.Root, theme.Text2, Fonts.MonoSmall);
                Plain(r, "Win32 apps: " + ImeCollector.Win32AppsKey, theme.Text2, Fonts.MonoSmall);
                Plain(r, "Autopilot profile: " + AutopilotInfo.ProfileKey, theme.Text2, Fonts.MonoSmall);
                Plain(r, "ConfigMgr client WMI: root\\ccm\\ClientSDK (CCM_Application, CCM_Program, CCM_SoftwareUpdate)", theme.Text2, Fonts.MonoSmall);
                foreach (string[] ch in EventWatch.Channels) Plain(r, "Event log: " + ch[0] + (ch[2] == "errors" ? " (errors and warnings)" : ""), theme.Text2, Fonts.MonoSmall);
            }
            if (s.Notes.Count > 0)
            {
                Section(r, "Notes");
                foreach (string n in s.Notes) Plain(r, n, theme.WarnFg, null);
            }
            End(r);
        }

        // ---------- menus and actions ----------

        ContextMenuStrip OpenMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            Menus.Add(m, "Watch this device", delegate { Begin(Settings.NewLive(), true); });
            ToolStripMenuItem hist = new ToolStripMenuItem("History to read: " + Settings.HistoryLong[Settings.HistoryIndex].ToLowerInvariant());
            hist.ForeColor = theme.Text;
            hist.Padding = new Padding(Dpi.S(4), Dpi.S(3), Dpi.S(4), Dpi.S(3));
            m.Items.Add(hist);
            for (int k = 0; k < Settings.Histories.Length; k++)
            {
                int hh = Settings.Histories[k];
                ToolStripMenuItem sub = new ToolStripMenuItem(Settings.HistoryLong[k], null, Safe.Wrap(delegate
                {
                    Settings.Hours = hh;
                    if (engine != null && engine.Mode == Modes.Live) Begin(Settings.NewLive(), true);
                    else Status("TrackVice now reads " + Settings.HoursText(hh) + " when it watches this device.");
                }));
                sub.Checked = hh == Hours;
                sub.ForeColor = theme.Text;
                sub.BackColor = theme.Card;
                hist.DropDownItems.Add(sub);
            }
            hist.DropDown.BackColor = theme.Card;
            hist.DropDown.Renderer = m.Renderer;
            Menus.Separator(m);
            Menus.Add(m, "Open a saved session…  (Ctrl+O)", delegate { OpenSession(); });
            Menus.Add(m, "Open a folder of logs…", delegate { OpenFolder(); });
            Menus.Separator(m);
            Menus.Add(m, "Autopilot in Intune\u2026", delegate { OpenRemote(RemoteDialog.Intune); });
            Menus.Add(m, "Devices with the TrackVice agent\u2026", delegate { OpenRemote(RemoteDialog.Agents); });
            Menus.Add(m, "Task sequences in SCCM\u2026", delegate { OpenRemote(RemoteDialog.Sccm); });
            Menus.Separator(m);
            Menus.Add(m, "Play the Autopilot demo", delegate { Begin(Engine.ForDemo("autopilot"), true); });
            Menus.Add(m, "Play the task sequence demo", delegate { Begin(Engine.ForDemo("osd"), true); });
            Menus.Add(m, "Demo: Autopilot in Intune", delegate { OpenRemote(RemoteDialog.Intune, true); });
            Menus.Add(m, "Demo: devices with the agent", delegate { OpenRemote(RemoteDialog.Agents, true); });
            Menus.Add(m, "Demo: task sequences in SCCM", delegate { OpenRemote(RemoteDialog.Sccm, true); });
            return m;
        }

        ContextMenuStrip DevicesMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            Menus.Add(m, "Autopilot in Intune\u2026", delegate { OpenRemote(RemoteDialog.Intune); });
            Menus.Add(m, "Devices with the TrackVice agent\u2026", delegate { OpenRemote(RemoteDialog.Agents); });
            Menus.Add(m, "Task sequences in SCCM\u2026", delegate { OpenRemote(RemoteDialog.Sccm); });
            Menus.Separator(m);
            Menus.Add(m, "This device", delegate { Begin(Settings.NewLive(), true); });
            Menus.Separator(m);
            Menus.Add(m, "Demo: Autopilot in Intune", delegate { OpenRemote(RemoteDialog.Intune, true); });
            Menus.Add(m, "Demo: devices with the agent", delegate { OpenRemote(RemoteDialog.Agents, true); });
            Menus.Add(m, "Demo: task sequences in SCCM", delegate { OpenRemote(RemoteDialog.Sccm, true); });
            return m;
        }

        ContextMenuStrip SaveMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            Menus.Add(m, "Save the session…  (Ctrl+S)", delegate { SaveSession(); });
            Menus.Add(m, "Collect the session and its logs in a zip…", delegate { CollectBundle(); }).Enabled = engine != null && engine.Mode != Modes.Demo;
            return m;
        }

        ContextMenuStrip ExportMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            Menus.Add(m, "HTML report…  (Ctrl+E)", delegate { ExportHtml(); });
            Menus.Add(m, "Every event as CSV…", delegate { ExportCsv(); });
            return m;
        }

        ContextMenuStrip LogViceMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            Item sel = list.SelectedItem;
            if (sel != null && sel.LogFile != null) Menus.Add(m, "Log of " + Txt.Short(sel.DisplayName, 40) + "  (Ctrl+L)", delegate { OpenItemLog(sel); });
            List<string> smsts = new List<string>();
            foreach (string p in LiveCollector.SmstsPaths()) if (Exists(p)) smsts.Add(p);
            if (smsts.Count > 0) Menus.Add(m, "smsts.log", delegate { OpenInLogVice(smsts[smsts.Count - 1]); });
            string ccm = LiveCollector.CcmLogFolder;
            List<string> apps = new List<string>();
            foreach (string n in new string[] { "AppEnforce", "AppDiscovery", "AppIntentEval" }) { string p = Path.Combine(ccm, n + ".log"); if (Exists(p)) apps.Add(p); }
            if (apps.Count > 0) Menus.Add(m, "SCCM application logs", delegate { OpenInLogVice(apps.ToArray()); });
            List<string> sw = new List<string>();
            foreach (string n in new string[] { "execmgr", "CAS", "UpdatesDeployment", "UpdatesHandler" }) { string p = Path.Combine(ccm, n + ".log"); if (Exists(p)) sw.Add(p); }
            if (sw.Count > 0) Menus.Add(m, "SCCM package, content and update logs", delegate { OpenInLogVice(sw.ToArray()); });
            List<string> ime = new List<string>();
            foreach (string n in LogParser.ImeLogs) { string p = Path.Combine(LiveCollector.ImeLogFolder, n + ".log"); if (Exists(p)) ime.Add(p); }
            if (ime.Count > 0) Menus.Add(m, "Intune Management Extension logs", delegate { OpenInLogVice(ime.ToArray()); });
            Menus.Separator(m);
            Menus.Add(m, "Autopilot event log", delegate { OpenInLogVice(LogViceLink.Autopilot); });
            Menus.Add(m, "MDM event log", delegate { OpenInLogVice(LogViceLink.MdmAdmin); });
            return m;
        }

        static bool Exists(string p)
        {
            try { return File.Exists(p); } catch (Exception) { return false; }
        }

        void OpenItemLog(Item i)
        {
            if (i == null || i.LogFile == null) { Status("This item has no log line behind it."); return; }
            Evidence ev = null;
            for (int k = i.Events.Count - 1; k >= 0 && ev == null; k--) if (i.Events[k].Evidence != null && i.Events[k].Evidence.IsLogFile) ev = i.Events[k].Evidence;
            OpenInLogVice(i.LogFile);
            if (ev != null && ev.Line > 0) Status("Opened " + Txt.FileName(ev.Path) + " in LogVice. The newest line about " + i.DisplayName + " is line " + ev.Line + ".");
        }

        void OpenInLogVice(params string[] targets)
        {
            if (targets == null || targets.Length == 0) return;
            if (engine != null && engine.Mode == Modes.Demo && !targets[0].StartsWith("evt:"))
            {
                Status("The demo has no real log files. Open a log on a real device, or a saved session from one.");
                return;
            }
            if (engine != null && engine.Mode == Modes.Agent)
            {
                Status("That log is on " + engine.S.Computer + ", not on this computer. The lines TrackVice read from it are in the evidence of each event.");
                return;
            }
            string exe = LogViceLink.Find();
            if (exe == null)
            {
                DialogResult r = MessageBox.Show(this, "LogVice was not found on this computer. Do you want to locate LogVice.exe?", "TrackVice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "LogVice|LogVice.exe|Programs (*.exe)|*.exe";
                    d.Title = "Locate LogVice.exe";
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    exe = d.FileName;
                    Prefs.Set("LogVicePath", exe);
                }
            }
            try
            {
                LogViceLink.Open(exe, targets);
                List<string> names = new List<string>();
                foreach (string t in targets) names.Add(t.StartsWith("evt:") ? t.Substring(4) : Txt.FileName(t));
                Status("Opened in LogVice: " + string.Join(", ", names.ToArray()));
            }
            catch (Exception ex) { Fail(ex); }
        }

        void OpenSession()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "TrackVice sessions (*.json)|*.json|All files (*.*)|*.*";
                d.Title = "Open a saved session";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { Begin(Engine.ForFile(d.FileName), false); }
                catch (Exception ex) { Fail(new Exception("This is not a TrackVice session: " + ex.Message)); }
            }
        }

        void OpenFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Choose a folder of logs: a CCM Logs folder, the IME Logs folder, or a collected bundle. TrackVice reads every log it knows in it and in its subfolders.";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Begin(Engine.ForFolder(d.SelectedPath), false);
            }
        }

        string FileStem
        {
            get { return "TrackVice-" + engine.S.Computer + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm", Inv); }
        }

        void SaveSession()
        {
            if (engine == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "TrackVice session (*.json)|*.json";
                d.FileName = FileStem + ".json";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                engine.S.ClockUtc = engine.Now;
                engine.S.Save(d.FileName);
                Status("Saved " + d.FileName);
            }
        }

        void CollectBundle()
        {
            if (engine == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Zip files (*.zip)|*.zip";
                d.FileName = FileStem + ".zip";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                engine.S.ClockUtc = engine.Now;
                int n = Report.Bundle(engine.S, d.FileName);
                Status("Saved " + d.FileName + " with the session and " + n + " log file" + (n == 1 ? "" : "s") + ".");
            }
        }

        void ExportHtml()
        {
            if (engine == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "HTML report (*.html)|*.html";
                d.FileName = FileStem + ".html";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, Report.Html(engine.S, engine.Now), Encoding.UTF8);
                Status("Saved " + d.FileName);
                OpenFile(d.FileName);
            }
        }

        void ExportCsv()
        {
            if (engine == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV (*.csv)|*.csv";
                d.FileName = FileStem + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Report.Csv(engine.S, d.FileName);
                Status("Saved " + d.FileName);
            }
        }

        static void OpenFile(string path)
        {
            try
            {
                if (Program.IsAdmin) System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + path + "\"");
                else System.Diagnostics.Process.Start(path);
            }
            catch (Exception) { }
        }

        void Fail(Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TrackVice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void OpenRemote(string kind)
        {
            OpenRemote(kind, false);
        }

        // Lists deployments on other devices and watches the one picked. A demo list is never kept.
        void OpenRemote(string kind, bool demo)
        {
            IRemoteCatalog existing = demo ? null : kind == RemoteDialog.Intune ? intuneCatalog : kind == RemoteDialog.Agents ? agentCatalog : sccmCatalog;
            using (RemoteDialog d = new RemoteDialog(kind, theme, existing, graph, demo))
            {
                d.ShowDialog(this);
                if (!demo)
                {
                    if (kind == RemoteDialog.Intune) { intuneCatalog = d.Catalog; graph = d.Graph; }
                    else if (kind == RemoteDialog.Agents) agentCatalog = d.Catalog;
                    else sccmCatalog = d.Catalog;
                }
                if (d.Picked == null || d.Catalog == null) return;
                Begin(Engine.ForRemote(d.Catalog.Open(d.Picked), d.Picked), true);
            }
        }

        void OpenSettings()
        {
            using (SettingsDialog d = new SettingsDialog(theme))
            {
                d.ThemeChanged += delegate
                {
                    theme = Theme.Named(Prefs.Get("Theme", "System"));
                    ApplyTheme();
                };
                d.IntervalChanged += delegate
                {
                    if (engine != null && engine.Mode == Modes.Live)
                    {
                        engine.PollIntervalMs = Settings.PollMs;
                        pollTimer.Interval = engine.PollIntervalMs;
                    }
                };
                d.StuckChanged += delegate
                {
                    Finder.StuckAfter = TimeSpan.FromMinutes(Settings.StuckMinutes);
                    if (engine != null) { Finder.Run(engine.S, engine.Now, engine.Mode == Modes.Live || engine.Mode == Modes.Demo); Bind(); }
                };
                d.ShowDialog(this);
                if (d.RestartRequested)
                {
                    if (Program.RestartElevated()) { Close(); return; }
                    Status("TrackVice was not restarted as administrator.");
                }
                if (d.ReadAgain && engine != null && engine.Mode == Modes.Live)
                {
                    Begin(Settings.NewLive(), watching || Settings.WatchOnStart);
                    Status("Reading " + Settings.HoursText(Hours) + "\u2026");
                }
            }
        }

        void About()
        {
            using (AboutDialog d = new AboutDialog(theme)) d.ShowDialog(this);
        }

        void ToggleCompact()
        {
            if (compact != null && !compact.IsDisposed)
            {
                compact.Close();
                compact = null;
                bCompact.Checked = false;
                return;
            }
            compact = new CompactView(theme);
            compact.FormClosed += delegate { bCompact.Checked = false; compact = null; };
            compact.FullView += delegate { Activate(); };
            bCompact.Checked = true;
            if (engine != null) compact.UpdateFrom(engine, watching);
            compact.Show();
        }

        // ---------- screenshots for the README (--shot) ----------

        void Shot()
        {
            if (start.Tab >= 0) tabs.Selected = start.Tab;
            if (start.Select != null)
            {
                if (start.Tab == TabFindings) { foreach (Finding f in findings.Rows) if (f.Title.IndexOf(start.Select, StringComparison.OrdinalIgnoreCase) >= 0) { findings.Select(f); break; } }
                else if (start.Tab == TabTimeline) { foreach (TEvent e in timeline.Rows) if (e.Text != null && e.Text.IndexOf(start.Select, StringComparison.OrdinalIgnoreCase) >= 0) { timeline.Select(e); break; } }
                else list.SelectByName(start.Select);
            }
            if (start.Compact) ToggleCompact();
            Form page = null;
            if (start.Page == "settings") page = new SettingsDialog(theme);
            else if (start.Page == "about") page = new AboutDialog(theme);
            else if (start.Remote != null && start.Pick == null)
            {
                bool sign = start.Select != null && start.Select.StartsWith("signin");
                RemoteDialog rd = new RemoteDialog(start.Remote, theme, null, null, !sign);
                if (sign) { string st = start.Select.Substring(6).TrimStart('-'); start.Select = null; rd.Shown += delegate { rd.PreviewSignIn(st); }; }
                rd.Shown += delegate { System.Windows.Forms.Timer st = new System.Windows.Forms.Timer(); st.Interval = 900; st.Tick += delegate { st.Stop(); if (start.Select != null) rd.SelectDevice(start.Select); }; st.Start(); };
                page = rd;
            }
            if (page != null)
            {
                page.StartPosition = FormStartPosition.Manual;
                page.Location = new Point(Left + 40, Top + 40);
                page.Show(this);
            }
            // Keep the pointer out of the picture, so no row shows as hovered.
            try { Cursor.Position = new Point(Screen.PrimaryScreen.Bounds.Right - 2, Screen.PrimaryScreen.Bounds.Bottom - 2); } catch (Exception) { }
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1500;
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    list.ClearHover();
                    list.Refresh();
                    Form target = page != null ? page : start.Compact && compact != null ? (Form)compact : this;
                    Rectangle b = target.Bounds;
                    using (Bitmap bmp = new Bitmap(b.Width, b.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                        bmp.Save(start.Shot, ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("Screenshot failed: " + ex.Message); }
                Close();
            };
            t.Start();
        }

        // ---------- window ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            try
            {
                switch (keyData)
                {
                    case Keys.F5: if (engine != null) { if (engine.Mode == Modes.Live) Begin(Settings.NewLive(), watching); else if (engine.Mode == Modes.Folder) Begin(Engine.ForFolder(engine.S.SourcePath), false); } return true;
                    case Keys.F1: About(); return true;
                    case Keys.Control | Keys.Oemcomma: OpenSettings(); return true;
                    case Keys.Control | Keys.W: ToggleWatch(); return true;
                    case Keys.Control | Keys.F: tabs.Selected = TabProgress; search.Focus(); search.SelectAll(); return true;
                    case Keys.Control | Keys.S: SaveSession(); return true;
                    case Keys.Control | Keys.O: OpenSession(); return true;
                    case Keys.Control | Keys.E: ExportHtml(); return true;
                    case Keys.Control | Keys.L: OpenItemLog(list.SelectedItem); return true;
                    case Keys.Control | Keys.M: ToggleCompact(); return true;
                    case Keys.Control | Keys.D: DevicesMenu().Show(bDevices, new Point(0, bDevices.Height)); return true;
                    case Keys.Escape:
                        if (search.Focused && search.Text.Length > 0) { search.Text = ""; return true; }
                        if (tlSearch.Focused && tlSearch.Text.Length > 0) { tlSearch.Text = ""; return true; }
                        break;
                }
            }
            catch (Exception ex)
            {
                Safe.Report(ex);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void LoadBounds()
        {
            string b = Prefs.Get("Bounds", null);
            if (b == null) return;
            string[] p = b.Split(',');
            int x, y, w, h;
            if (p.Length != 4 || !int.TryParse(p[0], out x) || !int.TryParse(p[1], out y) || !int.TryParse(p[2], out w) || !int.TryParse(p[3], out h)) return;
            Rectangle r = new Rectangle(x, y, w, h);
            foreach (Screen s in Screen.AllScreens)
            {
                if (!s.WorkingArea.IntersectsWith(r)) continue;
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
                if (Prefs.Get("Maximized", "0") == "1") WindowState = FormWindowState.Maximized;
                return;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (start.Shot == null)
            {
                Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Prefs.Set("Bounds", r.X + "," + r.Y + "," + r.Width + "," + r.Height);
                Prefs.Set("Maximized", WindowState == FormWindowState.Maximized ? "1" : "0");
            }
            pollTimer.Stop();
            clockTimer.Stop();
            try { if (engine != null) engine.Dispose(); } catch (Exception) { }
            try { if (compact != null && !compact.IsDisposed) compact.Close(); } catch (Exception) { }
            base.OnFormClosing(e);
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        readonly Theme t;

        public MenuColors(Theme theme)
        {
            t = theme;
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground { get { return t.Card; } }
        public override Color MenuBorder { get { return t.Divider; } }
        public override Color MenuItemBorder { get { return t.Hover; } }
        public override Color MenuItemSelected { get { return t.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return t.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return t.Hover; } }
        public override Color ImageMarginGradientBegin { get { return t.Card; } }
        public override Color ImageMarginGradientMiddle { get { return t.Card; } }
        public override Color ImageMarginGradientEnd { get { return t.Card; } }
        public override Color SeparatorDark { get { return t.Divider; } }
        public override Color SeparatorLight { get { return t.Divider; } }
        public override Color CheckBackground { get { return t.AccentSoft; } }
        public override Color CheckSelectedBackground { get { return t.AccentSoft; } }
    }
}
