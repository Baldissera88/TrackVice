namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // One line in a tile of Overview: a device, an app, a step or a heading.
    public sealed class TileRow
    {
        public string Text, Sub, Right, Link;
        public bool HasState;
        public State State;
        public double? Percent;
        // The colour of the right text: 0 plain, 1 warning, 2 error.
        public int Level;
        public bool Header;
        // The second line wraps instead of being cut off.
        public bool Wrap;
        public object Tag;
    }

    // One number in the strip at the top of a tile, such as the deployments today. A Link opens the list behind it.
    public sealed class CountCell
    {
        public string Label, Value, Sub;
        public bool Link;
    }

    // A card on Overview: a title, a small table of numbers, rows that can be clicked, or a plain empty state with the
    // button that sets the source up.
    public sealed class OverviewTile : FlatControl
    {
        public string Title, Note;
        public readonly List<TileRow> Rows = new List<TileRow>();
        // Label, today, last 7 days. The first entry is the heading.
        public readonly List<string[]> Stats = new List<string[]>();
        // Numbers side by side above the rows. They go two by two when the tile is narrow.
        public readonly List<CountCell> Counts = new List<CountCell>();
        public string EmptyTitle, EmptyText, Footer;
        public readonly FlatButton EmptyButton = new FlatButton("", null);
        public event Action<TileRow> RowClicked;
        public event Action<int, bool> StatClicked;
        public event Action<int> CountClicked;
        public event EventHandler FooterClicked;

        readonly List<KeyValuePair<Rectangle, TileRow>> hits = new List<KeyValuePair<Rectangle, TileRow>>();
        readonly List<KeyValuePair<Rectangle, int[]>> statHits = new List<KeyValuePair<Rectangle, int[]>>();
        readonly List<KeyValuePair<Rectangle, int>> countHits = new List<KeyValuePair<Rectangle, int>>();
        Rectangle footerHit;
        Point mouse = new Point(-1, -1);

        static int Pad { get { return Dpi.S(16); } }

        public OverviewTile(string title)
        {
            Title = title;
            TabStop = false;
            EmptyButton.Visible = false;
            EmptyButton.Primary = true;
            Controls.Add(EmptyButton);
        }

        public bool Empty
        {
            get { return Rows.Count == 0 && Stats.Count == 0 && Counts.Count == 0; }
        }

        public void SetEmptyAction(string text)
        {
            EmptyButton.Text = text ?? "";
            EmptyButton.Visible = !string.IsNullOrEmpty(text);
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            EmptyButton.ApplyTheme(t);
        }

        int RowHeight(TileRow r)
        {
            if (r.Header) return Dpi.S(30);
            int h = string.IsNullOrEmpty(r.Sub) ? Dpi.S(30) : Dpi.S(44);
            if (r.Wrap && !string.IsNullOrEmpty(r.Sub)) h = Dpi.S(5) + Fonts.Ui.Height + Dpi.S(2) + Draw.WrapHeight(r.Sub, Fonts.Small, SubWidth(r)) + Dpi.S(9);
            if (r.Percent.HasValue) h += Dpi.S(6);
            return h;
        }

        int SubWidth(TileRow r)
        {
            return Math.Max(Dpi.S(60), measureWidth - Pad * 2 - (r.HasState ? Dpi.S(24) : 0));
        }

        int measureWidth = 400;

        // The columns of the count strip: all in one row when each gets room for "Last 30 days" and a number, else two.
        int CountColumns(int width)
        {
            int n = Counts.Count;
            if (n == 0) return 1;
            return (width - Pad * 2) / n >= Dpi.S(104) ? n : Math.Min(2, n);
        }

        int CountsHeight(int width)
        {
            int cols = CountColumns(width);
            int rows = (Counts.Count + cols - 1) / cols;
            return rows * CountCellHeight + Dpi.S(8);
        }

        static int CountCellHeight
        {
            get { return Fonts.Small.Height + Fonts.Title.Height + Fonts.Small.Height + Dpi.S(18); }
        }

        int HeadHeight
        {
            get { return Pad + Fonts.UiSemibold.Height + Dpi.S(10); }
        }

        public int Measure(int width)
        {
            measureWidth = width;
            int h = HeadHeight;
            if (Empty)
            {
                int tw = Math.Max(Dpi.S(80), width - Pad * 2);
                if (!string.IsNullOrEmpty(EmptyTitle)) h += Fonts.UiSemibold.Height + Dpi.S(4);
                h += Draw.WrapHeight(EmptyText, Fonts.Ui, tw) + Dpi.S(8);
                if (EmptyButton.Visible) h += EmptyButton.Height + Dpi.S(8);
                return h + Pad;
            }
            if (Counts.Count > 0) h += CountsHeight(width);
            if (Stats.Count > 0) h += Stats.Count * Dpi.S(26) + Dpi.S(8);
            foreach (TileRow r in Rows) h += RowHeight(r);
            if (!string.IsNullOrEmpty(Footer)) h += Dpi.S(34);
            return h + Dpi.S(10);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (EmptyButton == null || !EmptyButton.Visible) return;
            int tw = Math.Max(Dpi.S(80), Width - Pad * 2);
            int y = HeadHeight;
            if (!string.IsNullOrEmpty(EmptyTitle)) y += Fonts.UiSemibold.Height + Dpi.S(4);
            y += Draw.WrapHeight(EmptyText, Fonts.Ui, tw) + Dpi.S(12);
            EmptyButton.Location = new Point(Pad, y);
        }

        Color LevelColor(int level)
        {
            return level >= 2 ? T.ErrFg : level == 1 ? T.WarnFg : T.Text2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Draw.Card(g, ClientRectangle, T);
            hits.Clear();
            statHits.Clear();
            countHits.Clear();
            measureWidth = Width;
            footerHit = Rectangle.Empty;
            int w = Width - Pad * 2;
            int noteW = string.IsNullOrEmpty(Note) ? 0 : Draw.Measure(Note, Fonts.Small).Width + 2;
            Draw.Text(g, Title, Fonts.UiSemibold, new Rectangle(Pad, Pad, w - noteW - Dpi.S(8), Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
            if (noteW > 0) Draw.Text(g, Note, Fonts.Small, new Rectangle(Width - Pad - noteW, Pad, noteW, Fonts.UiSemibold.Height + 2), T.Text3, Draw.Line);
            int y = HeadHeight;
            if (Empty)
            {
                if (!string.IsNullOrEmpty(EmptyTitle))
                {
                    Draw.Text(g, EmptyTitle, Fonts.Ui, new Rectangle(Pad, y, w, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
                    y += Fonts.UiSemibold.Height + Dpi.S(4);
                }
                Draw.Text(g, EmptyText, Fonts.Ui, new Rectangle(Pad, y, w, Height - y), T.Text3, Draw.Wrap);
                return;
            }
            if (Counts.Count > 0)
            {
                int cols = CountColumns(Width);
                int cw = w / cols, ch = CountCellHeight;
                for (int k = 0; k < Counts.Count; k++)
                {
                    CountCell c = Counts[k];
                    Rectangle cell = new Rectangle(Pad + (k % cols) * cw, y + (k / cols) * ch, cw, ch);
                    bool hot = c.Link && cell.Contains(mouse);
                    int ty = cell.Y + Dpi.S(2);
                    Draw.Text(g, c.Label, Fonts.Small, new Rectangle(cell.X, ty, cw - Dpi.S(8), Fonts.Small.Height + 2), T.Text3, Draw.Line);
                    ty += Fonts.Small.Height + Dpi.S(4);
                    Font vf = hot ? new Font(Fonts.Title, FontStyle.Underline) : Fonts.Title;
                    try { Draw.Text(g, c.Value, T.Num(vf), new Rectangle(cell.X, ty, cw - Dpi.S(8), Fonts.Title.Height + 2), c.Link ? T.Accent : T.Text, Draw.Line); }
                    finally { if (hot) vf.Dispose(); }
                    ty += Fonts.Title.Height + Dpi.S(2);
                    Draw.Text(g, c.Sub, Fonts.Small, new Rectangle(cell.X, ty, cw - Dpi.S(8), Fonts.Small.Height + 2), T.Text2, Draw.Line);
                    if (c.Link) countHits.Add(new KeyValuePair<Rectangle, int>(cell, k));
                }
                y += ((Counts.Count + cols - 1) / cols) * ch + Dpi.S(8);
            }
            if (Stats.Count > 0)
            {
                int colW = Math.Min(Dpi.S(130), w / 3);
                for (int k = 0; k < Stats.Count; k++)
                {
                    string[] s = Stats[k];
                    bool head = k == 0;
                    Rectangle row = new Rectangle(Pad, y, w, Dpi.S(26));
                    Font f = head ? Fonts.Small : Fonts.Ui;
                    Draw.Text(g, s[0], f, new Rectangle(row.X, row.Y, w - colW * 2, row.Height), head ? T.Text3 : T.Text2, Draw.Line);
                    for (int c = 1; c <= 2; c++)
                    {
                        Rectangle cell = new Rectangle(row.Right - colW * (3 - c), row.Y, colW, row.Height);
                        bool link = !head && s.Length > 3 && s[3] == "link" && s[c] != "0" && s[c] != "-";
                        bool hot = link && cell.Contains(mouse);
                        Color col = head ? T.Text3 : link ? (s.Length > 4 && s[4] == "err" ? T.ErrFg : T.Accent) : T.Text;
                        Font cf = head ? Fonts.Small : T.Num(hot ? Fonts.UiSemibold : Fonts.Ui);
                        Draw.Text(g, s[c], cf, cell, col, Draw.Line | TextFormatFlags.Right);
                        if (link) statHits.Add(new KeyValuePair<Rectangle, int[]>(cell, new int[] { k, c }));
                    }
                    y += row.Height;
                }
                y += Dpi.S(8);
            }
            foreach (TileRow r in Rows)
            {
                int h = RowHeight(r);
                Rectangle rr = new Rectangle(Dpi.S(6), y, Width - Dpi.S(12), h);
                if (r.Header)
                {
                    Draw.Text(g, r.Text, Fonts.SmallSemibold, new Rectangle(Pad, y + Dpi.S(8), w, h - Dpi.S(8)), T.Text3, Draw.Line);
                    y += h;
                    continue;
                }
                bool hot = rr.Contains(mouse);
                if (hot) Shapes.Fill(g, rr, Dpi.S(5), T.Hover);
                hits.Add(new KeyValuePair<Rectangle, TileRow>(rr, r));
                int x = Pad;
                if (r.HasState)
                {
                    int d = Dpi.S(14);
                    StateLook.Icon(g, r.State, new RectangleF(x, y + (string.IsNullOrEmpty(r.Sub) ? (h - d) / 2f : Dpi.S(8)), d, d), T, 0f);
                    x += d + Dpi.S(10);
                }
                string right = r.Link ?? r.Right;
                Font rf = r.Link != null ? Fonts.Ui : T.Num(Fonts.Ui);
                int rw = string.IsNullOrEmpty(right) ? 0 : Math.Min(w / 2, Draw.Measure(right, rf).Width + 2);
                int textW = Width - Pad - x - rw - Dpi.S(10);
                int ty = string.IsNullOrEmpty(r.Sub) ? y : y + Dpi.S(5);
                int th = string.IsNullOrEmpty(r.Sub) ? Dpi.S(30) : Fonts.Ui.Height + 2;
                Draw.Text(g, r.Text, Fonts.Ui, new Rectangle(x, ty, textW, th), T.Text, Draw.Line);
                if (!string.IsNullOrEmpty(r.Sub))
                {
                    int sy = ty + Fonts.Ui.Height + Dpi.S(2);
                    if (r.Wrap) Draw.Text(g, r.Sub, Fonts.Small, new Rectangle(x, sy, Width - Pad - x, y + h - sy), T.Text3, Draw.Wrap);
                    else Draw.Text(g, r.Sub, Fonts.Small, new Rectangle(x, sy, Width - Pad - x, Fonts.Small.Height + 2), T.Text3, Draw.Line);
                }
                if (rw > 0)
                    Draw.Text(g, right, rf, new Rectangle(Width - Pad - rw, ty, rw, th), r.Link != null ? T.Accent : LevelColor(r.Level), Draw.Line | TextFormatFlags.Right);
                if (r.Percent.HasValue)
                {
                    int by = y + h - Dpi.S(8);
                    RectangleF track = new RectangleF(x, by, Width - Pad - x, Dpi.S(3));
                    Shapes.Fill(g, track, Dpi.S(1.5f), T.Divider);
                    float p = (float)Math.Max(0, Math.Min(1, r.Percent.Value / 100));
                    if (p > 0) Shapes.Fill(g, new RectangleF(track.X, track.Y, Math.Max(Dpi.S(3), track.Width * p), track.Height), Dpi.S(1.5f), r.Level >= 2 ? T.ErrFg : T.Accent);
                }
                y += h;
            }
            if (!string.IsNullOrEmpty(Footer))
            {
                int fw = Draw.Measure(Footer, Fonts.Ui).Width + 2;
                footerHit = new Rectangle(Pad, y + Dpi.S(6), fw, Dpi.S(24));
                Draw.Text(g, Footer, footerHit.Contains(mouse) ? Fonts.UiSemibold : Fonts.Ui, footerHit, T.Accent, Draw.Line);
            }
        }

        bool Over(Point p)
        {
            foreach (KeyValuePair<Rectangle, TileRow> h in hits) if (h.Key.Contains(p)) return true;
            foreach (KeyValuePair<Rectangle, int[]> h in statHits) if (h.Key.Contains(p)) return true;
            foreach (KeyValuePair<Rectangle, int> h in countHits) if (h.Key.Contains(p)) return true;
            return footerHit.Contains(p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            Cursor = Over(e.Location) ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouse = new Point(-1, -1);
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (KeyValuePair<Rectangle, TileRow> h in hits)
                if (h.Key.Contains(e.Location)) { if (RowClicked != null) RowClicked(h.Value); return; }
            foreach (KeyValuePair<Rectangle, int[]> h in statHits)
                if (h.Key.Contains(e.Location)) { if (StatClicked != null) StatClicked(h.Value[0], h.Value[1] == 2); return; }
            foreach (KeyValuePair<Rectangle, int> h in countHits)
                if (h.Key.Contains(e.Location)) { if (CountClicked != null) CountClicked(h.Value); return; }
            if (footerHit.Contains(e.Location) && FooterClicked != null) FooterClicked(this, EventArgs.Empty);
        }

        // Keeps the pointer out of the screenshots.
        public void ClearHover()
        {
            mouse = new Point(-1, -1);
        }
    }

    // Overview: the home page in the main window. Devices deploying now, today and the last 7 days, what needs
    // attention, recent and pinned devices, and what is not set up yet.
    public sealed class OverviewPage : Panel, IThemed
    {
        public event Action<OverviewDevice> OpenDevice;
        public event Action<FoundDevice> OpenFound;
        public event Action<string, string, List<FoundDevice>> OpenList;
        public event Action<AttentionTile> AttentionClicked;
        public event Action<string> SearchFor;
        public event Action<string> SetupClicked;
        public event EventHandler RefreshClicked, FilterChanged, ThisPc, AllRecent;

        Theme theme = Theme.Light();
        public readonly OverviewFilter Filter = new OverviewFilter();
        readonly Panel col = new Panel();
        readonly WelcomeHead head = new WelcomeHead("Overview", "Devices deploying now, the last 7 days and what needs attention, for every customer. It refreshes by itself every minute.");
        readonly FlowLayoutPanel bar = new FlowLayoutPanel();
        readonly FlatButton bCustomer = new FlatButton("All customers", null);
        readonly Segmented side = SideFilterUi.Make(DeviceSides.Both);
        public readonly TextBox Search = new TextBox();
        readonly FlatButton bSearch = new FlatButton("Search", null);
        readonly FlatButton bRefresh = new FlatButton("Refresh", Glyphs2.Sync);
        readonly FlatButton bThisPc = new FlatButton("Follow this PC", Glyphs2.Device);
        readonly Label status = new Label();
        readonly OverviewTile now = new OverviewTile("Now");
        readonly OverviewTile attention = new OverviewTile("Needs attention");
        readonly OverviewTile deploy = new OverviewTile("Deployments");
        readonly OverviewTile period = new OverviewTile("Today and the last 7 days");
        readonly OverviewTile recent = new OverviewTile("Recent and pinned devices");
        readonly OverviewTile setup = new OverviewTile("Setup");
        List<string> customers = new List<string>();
        OverviewData data;
        public const int NowRows = 6, RecentRows = 6;

        public OverviewPage()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            DoubleBuffered = true;
            head.Mark = false;
            bar.WrapContents = true;
            bar.Height = Dpi.S(40);
            foreach (FlatButton b in new FlatButton[] { bCustomer, bSearch, bRefresh, bThisPc }) { b.Compact = true; b.AutoWidth(); }
            bCustomer.Width = Math.Max(bCustomer.Width, Dpi.S(150));
            bCustomer.Margin = new Padding(0, 0, Dpi.S(8), 0);
            side.Margin = new Padding(0, 0, Dpi.S(12), 0);
            Search.Width = Dpi.S(300);
            Search.BorderStyle = BorderStyle.FixedSingle;
            Search.Font = Fonts.Ui;
            Search.Margin = new Padding(0, Dpi.S(4), Dpi.S(4), 0);
            bSearch.Margin = new Padding(0, 0, Dpi.S(12), 0);
            bRefresh.Margin = new Padding(0, 0, Dpi.S(4), 0);
            bThisPc.Margin = new Padding(0, 0, Dpi.S(12), 0);
            status.AutoSize = true;
            status.Font = Fonts.Small;
            status.Margin = new Padding(0, Dpi.S(9), 0, 0);
            bar.Controls.Add(bCustomer);
            bar.Controls.Add(side);
            bar.Controls.Add(Search);
            bar.Controls.Add(bSearch);
            bar.Controls.Add(bRefresh);
            bar.Controls.Add(bThisPc);
            bar.Controls.Add(status);
            bCustomer.Click += Safe.Wrap(delegate { CustomerMenu().Show(bCustomer, new Point(0, bCustomer.Height)); });
            side.Changed += Safe.Wrap(delegate { Filter.Side = SideFilterUi.Of(side); Changed(); });
            Search.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                Safe.Run(delegate { if (SearchFor != null) SearchFor(Search.Text.Trim()); });
            };
            bSearch.Click += Safe.Wrap(delegate { if (SearchFor != null) SearchFor(Search.Text.Trim()); });
            bRefresh.Click += Safe.Wrap(delegate { if (RefreshClicked != null) RefreshClicked(this, EventArgs.Empty); });
            bThisPc.Click += Safe.Wrap(delegate { if (ThisPc != null) ThisPc(this, EventArgs.Empty); });
            now.RowClicked += delegate (TileRow r) { Safe.Run(delegate { Device(r.Tag as OverviewDevice); }); };
            now.FooterClicked += Safe.Wrap(delegate { if (data != null) List("now", "Deploying now", Devices(NowDevices())); });
            attention.RowClicked += delegate (TileRow r) { Safe.Run(delegate { if (AttentionClicked != null && r.Tag is AttentionTile) AttentionClicked((AttentionTile)r.Tag); }); };
            period.RowClicked += delegate (TileRow r) { Safe.Run(delegate { StepStat s = r.Tag as StepStat; if (s != null) List("step:" + s.Name, s.Name, Devices(s.On)); }); };
            deploy.CountClicked += delegate (int k) { Safe.Run(delegate { CountList(k); }); };
            deploy.RowClicked += delegate (TileRow r) { Safe.Run(delegate { RepeatList(r.Tag as RepeatDevice); }); };
            deploy.FooterClicked += Safe.Wrap(delegate { AllRepeats(); });
            period.StatClicked += delegate (int row, bool week) { Safe.Run(delegate { Stat(row, week); }); };
            recent.RowClicked += delegate (TileRow r) { Safe.Run(delegate { if (OpenFound != null && r.Tag is FoundDevice) OpenFound((FoundDevice)r.Tag); }); };
            recent.FooterClicked += Safe.Wrap(delegate { if (AllRecent != null) AllRecent(this, EventArgs.Empty); });
            setup.RowClicked += delegate (TileRow r) { Safe.Run(delegate { if (SetupClicked != null && r.Tag is SetupNeed) SetupClicked(((SetupNeed)r.Tag).Id); }); };
            foreach (OverviewTile t in new OverviewTile[] { now, attention, period })
            {
                OverviewTile tile = t;
                tile.EmptyButton.Click += Safe.Wrap(delegate { if (SetupClicked != null && tile.EmptyButton.Tag is string) SetupClicked((string)tile.EmptyButton.Tag); });
            }
            col.Controls.Add(head);
            col.Controls.Add(bar);
            foreach (OverviewTile t in Tiles) col.Controls.Add(t);
            Controls.Add(col);
            Resize += delegate { Relayout(); };
            HandleCreated += delegate { try { Cue(Search, "Search devices: name, serial number, user, profile or task sequence"); } catch (Exception) { } };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void Cue(TextBox box, string text)
        {
            SendMessage(box.Handle, 0x1501, (IntPtr)1, text);
        }

        OverviewTile[] Tiles
        {
            get { return new OverviewTile[] { now, attention, deploy, period, recent, setup }; }
        }

        public string StatusText
        {
            set { status.Text = value ?? ""; }
        }

        ContextMenuStrip CustomerMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            Menus.Add(m, "All customers", delegate { SetCustomer(null); }).Checked = Filter.Customer == null;
            if (customers.Count > 0) Menus.Separator(m);
            foreach (string c in customers)
            {
                string name = c;
                Menus.Add(m, name, delegate { SetCustomer(name); }).Checked = string.Equals(Filter.Customer, name, StringComparison.OrdinalIgnoreCase);
            }
            return m;
        }

        void SetCustomer(string name)
        {
            Filter.Customer = name;
            bCustomer.Text = name ?? "All customers";
            bCustomer.Width = Math.Max(bCustomer.Width, Dpi.S(150));
            Changed();
        }

        void Changed()
        {
            if (FilterChanged != null) FilterChanged(this, EventArgs.Empty);
        }

        void Device(OverviewDevice o)
        {
            if (o != null && OpenDevice != null) OpenDevice(o);
        }

        void List(string id, string title, List<FoundDevice> rows)
        {
            if (OpenList != null) OpenList(id, title, rows);
        }

        public static List<FoundDevice> Devices(IEnumerable<OverviewDevice> l)
        {
            List<FoundDevice> r = new List<FoundDevice>();
            foreach (OverviewDevice o in l) r.Add(FoundDevice.Of(o));
            return r;
        }

        List<OverviewDevice> NowDevices()
        {
            List<OverviewDevice> l = new List<OverviewDevice>();
            foreach (NowRow r in data.Now) l.Add(r.Dev);
            return l;
        }

        void Stat(int row, bool week)
        {
            if (data == null) return;
            PeriodStats p = week ? data.Week : data.Today;
            string when = week ? "in the last 7 days" : "today";
            if (row == 1) List("finished:" + week, "Finished " + when, Devices(p.FinishedDevices));
            else if (row == 2) List("failedp:" + week, "Failed " + when, Devices(p.FailedDevices));
        }

        // Customer and side of the filter, for the title of a list: " · Contoso · SCCM".
        string FilterText()
        {
            string t = "";
            if (!string.IsNullOrEmpty(Filter.Customer)) t += " \u00b7 " + Filter.Customer;
            if (Filter.Side == DeviceSides.Intune) t += " \u00b7 Intune";
            else if (Filter.Side == DeviceSides.Sccm) t += " \u00b7 SCCM";
            return t;
        }

        string FilterId()
        {
            return (Filter.Customer ?? "").ToLowerInvariant() + ":" + Filter.Side;
        }

        void CountList(int k)
        {
            if (data == null || k < 0 || k >= data.Counts.Windows.Count) return;
            DeployWindow w = data.Counts.Windows[k];
            string title = w.Id == DeployCounts.Today ? "Deployments today" : w.Id == DeployCounts.Week ? "Deployments in the last 7 days" : w.Id == DeployCounts.Month ? "Deployments in the last 30 days" : "All deployments";
            List("runs:" + w.Id + ":" + FilterId(), title + FilterText(), Devices(w.Rows));
        }

        void RepeatList(RepeatDevice r)
        {
            if (r == null) return;
            List("runs-of:" + (r.Customer ?? "").ToLowerInvariant() + ":" + r.Device.ToLowerInvariant(), "Deployments of " + r.Device + (string.IsNullOrEmpty(r.Customer) ? "" : " \u00b7 " + r.Customer), Devices(r.Runs));
        }

        void AllRepeats()
        {
            if (data == null) return;
            List<OverviewDevice> all = new List<OverviewDevice>();
            foreach (RepeatDevice r in data.Counts.Repeats) all.AddRange(r.Runs);
            List("repeats:" + FilterId(), "Deployed more than once" + FilterText(), Devices(all));
        }

        public void SetCustomers(List<string> names)
        {
            customers = names ?? new List<string>();
        }

        // Shows what was read. setupFor names the action of the empty tiles, such as Add customer.
        public void SetData(OverviewData d, List<RecentDevice> recentList, List<SetupNeed> needs, DateTime nowUtc)
        {
            data = d;
            string emptyAction = null, emptyId = null;
            if (needs.Count > 0)
            {
                SetupNeed first = needs[0];
                foreach (SetupNeed n in needs) if (n.Id == OverviewModel.Customer || (Filter.Side == DeviceSides.Sccm && n.Id == OverviewModel.Sccm)) { first = n; break; }
                emptyAction = first.ActionText;
                emptyId = first.Id;
            }
            string nothing = Filter.Side == DeviceSides.Sccm ? "No SCCM site yet" : Filter.Side == DeviceSides.Intune ? "No devices with the agent yet" : "Nothing set up yet";

            // Now
            now.Rows.Clear();
            now.Note = d.HasSources ? d.Now.Count + " deploying" : null;
            for (int k = 0; k < d.Now.Count && k < NowRows; k++)
            {
                NowRow n = d.Now[k];
                TileRow r = new TileRow();
                r.Text = n.Dev.D.Device;
                List<string> sub = new List<string>();
                if (!string.IsNullOrEmpty(n.Dev.Customer)) sub.Add(n.Dev.Customer);
                string who = Who.Short(n.Dev.S) ?? n.Dev.D.EnrollUser;
                if (!string.IsNullOrEmpty(who)) sub.Add(who);
                if (!string.IsNullOrEmpty(n.Phase)) sub.Add(n.Phase);
                if (!string.IsNullOrEmpty(n.WaitsFor)) sub.Add(n.WaitsFor);
                r.Sub = string.Join(" · ", sub.ToArray());
                r.HasState = true;
                r.State = n.Failed ? State.Failed : State.Installing;
                r.Percent = n.Percent;
                r.Right = n.Percent.HasValue ? n.Percent.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %" : "Running";
                if (n.Failed) { r.Right = (n.Percent.HasValue ? r.Right + " · " : "") + "failed"; r.Level = 2; }
                r.Tag = n.Dev;
                now.Rows.Add(r);
            }
            now.Footer = d.Now.Count > NowRows ? "Show all " + d.Now.Count : null;
            Empty(now, d.HasSources, nothing, "Now shows devices with the TrackVice agent and task sequences from SCCM while they deploy, with how far each is and what it waits for.",
                "Nothing deploys right now.", emptyAction, emptyId);

            // Needs attention
            attention.Rows.Clear();
            foreach (AttentionTile t in d.Attention)
            {
                TileRow r = new TileRow();
                r.Text = t.Title;
                r.Sub = t.Text;
                r.Right = t.Count > 0 ? t.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                r.Link = t.Count > 0 ? null : t.ActionText;
                r.Level = t.Level;
                r.Wrap = true;
                r.HasState = true;
                r.State = t.Level >= 2 ? State.Failed : t.Level == 1 ? State.WaitingReboot : State.Pending;
                r.Tag = t;
                attention.Rows.Add(r);
            }
            Empty(attention, d.HasSources, "Nothing to check yet", "Failed devices, devices with no progress, agents that stopped sending and findings worth acting on show here.",
                "Nothing needs attention right now.", emptyAction, emptyId);

            // Today and the last 7 days
            period.Rows.Clear();
            period.Stats.Clear();
            if (d.HasSources)
            {
                period.Stats.Add(new string[] { "", "Today", "Last 7 days" });
                period.Stats.Add(new string[] { "Finished", N(d.Today.Finished), N(d.Week.Finished), "link" });
                period.Stats.Add(new string[] { "Failed", N(d.Today.Failed), N(d.Week.Failed), "link", "err" });
                period.Stats.Add(new string[] { "Success rate", OverviewModel.Rate(d.Today.SuccessRate), OverviewModel.Rate(d.Week.SuccessRate) });
                period.Stats.Add(new string[] { "Average time", OverviewModel.Time(d.Today.AvgSeconds), OverviewModel.Time(d.Week.AvgSeconds) });
                if (d.Slowest.Count > 0) period.Rows.Add(Head("Slowest steps"));
                foreach (StepStat s in d.Slowest)
                {
                    TileRow r = new TileRow();
                    r.Text = s.Name;
                    r.Sub = s.Kind + " · on " + s.Devices + " device" + (s.Devices == 1 ? "" : "s");
                    r.Right = "avg " + OverviewModel.Time(s.AvgSeconds);
                    r.Tag = s;
                    period.Rows.Add(r);
                }
                if (d.FailingMost.Count > 0) period.Rows.Add(Head("Fail most"));
                foreach (StepStat s in d.FailingMost)
                {
                    TileRow r = new TileRow();
                    r.Text = s.Name;
                    r.Sub = s.Kind + " · failed on " + s.Failed + " of " + s.Devices + " device" + (s.Devices == 1 ? "" : "s");
                    r.Right = s.Failed + " failed";
                    r.Level = 2;
                    r.Tag = s;
                    period.Rows.Add(r);
                }
            }
            Empty(period, d.HasSources, nothing, "Finished and failed devices, the success rate, the average time, and the apps and steps that take longest or fail most.", null, emptyAction, emptyId);

            // Deployments: every run, also repeats on one device
            deploy.Rows.Clear();
            deploy.Counts.Clear();
            deploy.Footer = null;
            deploy.Visible = d.HasSources;
            deploy.Note = d.HasSccm && d.HasSources ? "SCCM: the last 12 months" : null;
            if (d.HasSources)
            {
                foreach (DeployWindow w in d.Counts.Windows)
                {
                    CountCell c = new CountCell();
                    c.Label = w.Label;
                    c.Value = N(w.Runs);
                    c.Sub = w.Runs == 0 ? "none" : "on " + w.Devices + (w.Devices == 1 ? " device" : " devices");
                    c.Link = w.Runs > 0;
                    deploy.Counts.Add(c);
                }
                deploy.Rows.Add(Head(d.Counts.Repeats.Count == 0 ? "No device was deployed more than once" : "Deployed more than once"));
                for (int k = 0; k < d.Counts.Repeats.Count && k < OverviewCounts.TopRepeats; k++)
                {
                    RepeatDevice rd = d.Counts.Repeats[k];
                    OverviewDevice last = rd.Last;
                    TileRow r = new TileRow();
                    r.Text = rd.Device;
                    List<string> sub = new List<string>();
                    if (!string.IsNullOrEmpty(rd.Customer)) sub.Add(rd.Customer);
                    sub.Add("last result: " + States.Label(last.D.State).ToLowerInvariant());
                    sub.Add(Txt.Ago(OverviewCounts.StartOf(last), nowUtc));
                    r.Sub = string.Join(" \u00b7 ", sub.ToArray());
                    r.HasState = true;
                    r.State = last.D.State;
                    r.Right = OverviewCounts.Times(rd.Times);
                    r.Level = last.D.State == State.Failed ? 2 : 0;
                    r.Tag = rd;
                    deploy.Rows.Add(r);
                }
                if (d.Counts.Repeats.Count > OverviewCounts.TopRepeats) deploy.Footer = "Show all " + d.Counts.Repeats.Count + " devices";
            }

            // Recent and pinned devices
            recent.Rows.Clear();
            List<FoundDevice> found = DeviceSearch.Merge(recentList, d.Devices);
            int shown = 0;
            foreach (FoundDevice f in found)
            {
                if (!f.Recent) continue;
                if (shown++ >= RecentRows) break;
                TileRow r = new TileRow();
                r.Text = f.Device;
                List<string> sub = new List<string>();
                if (f.Pinned) sub.Add("Pinned");
                sub.Add(RecentSources.Name(f.Source));
                if (!string.IsNullOrEmpty(f.Customer)) sub.Add(f.Customer);
                if (!string.IsNullOrEmpty(f.User)) sub.Add(f.User);
                if (!string.IsNullOrEmpty(f.StateText)) sub.Add(f.StateText);
                if (!string.IsNullOrEmpty(f.Note)) sub.Add("Note: " + f.Note);
                r.Sub = string.Join(" · ", sub.ToArray());
                r.HasState = true;
                r.State = f.State;
                r.Right = f.LastUtc.Year > 2000 ? Txt.Ago(f.LastUtc, nowUtc) : null;
                r.Tag = f;
                recent.Rows.Add(r);
            }
            recent.Note = recentList.Count > 0 ? recentList.Count + " kept on this PC" : null;
            recent.Footer = recentList.Count > 0 ? "All recent devices and search" : null;
            recent.EmptyTitle = "No devices yet";
            recent.EmptyText = "Every device you open or follow shows up here, newest first: devices with the agent, SCCM runs, Autopilot in Intune, this PC and opened files. Pin one to keep it at the top.";
            recent.SetEmptyAction(null);

            // Setup
            setup.Rows.Clear();
            foreach (SetupNeed n in needs)
            {
                TileRow r = new TileRow();
                r.Text = n.Title;
                r.Sub = n.Text;
                r.Link = n.ActionText;
                r.Wrap = true;
                r.Tag = n;
                setup.Rows.Add(r);
            }
            setup.Note = needs.Count > 0 ? needs.Count + " missing" : null;
            setup.Visible = needs.Count > 0;
            Relayout();
            foreach (OverviewTile t in Tiles) t.Invalidate();
        }

        static string N(int n)
        {
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static TileRow Head(string text)
        {
            TileRow r = new TileRow();
            r.Text = text;
            r.Header = true;
            return r;
        }

        static void Empty(OverviewTile t, bool hasSources, string title, string text, string none, string action, string actionId)
        {
            if (!hasSources)
            {
                t.EmptyTitle = title;
                t.EmptyText = text;
                t.SetEmptyAction(action);
                t.EmptyButton.Tag = actionId;
            }
            else
            {
                t.EmptyTitle = null;
                t.EmptyText = none;
                t.SetEmptyAction(null);
            }
        }

        public void ClearHover()
        {
            foreach (OverviewTile t in Tiles) { t.ClearHover(); t.Invalidate(); }
        }

        bool laying;

        void Relayout()
        {
            if (laying) return;
            laying = true;
            try { Lay(); }
            finally { laying = false; }
        }

        // Two columns when there is room: Now and Needs attention, then the numbers and the recent devices.
        void Lay()
        {
            int pad = Dpi.S(20);
            int avail = Width - SystemInformation.VerticalScrollBarWidth;
            int w = Math.Min(Dpi.S(1280), Math.Max(Dpi.S(320), avail - pad * 2));
            int gap = Dpi.S(14);
            bool two = NarrowFit.TwoColumns(w, Dpi.S(400), gap, 56);
            int left = two ? (w - gap) * 56 / 100 : w;
            int right = two ? w - left - gap : w;
            int y = 0;
            col.SuspendLayout();
            int hh = head.Measure(w);
            head.SetBounds(0, y, w, hh);
            y += hh + Dpi.S(10);
            int bh = Math.Max(Dpi.S(40), bar.GetPreferredSize(new Size(w, 0)).Height);
            bar.SetBounds(0, y, w, bh);
            y += bh + Dpi.S(10);
            if (setup.Visible)
            {
                int sh = setup.Measure(w);
                setup.SetBounds(0, y, w, sh);
                y += sh + gap;
            }
            if (two)
            {
                y = Pair(now, attention, y, left, right, gap);
                if (deploy.Visible)
                {
                    // The long tile on the left, the deployment counts and the recent devices stacked beside it.
                    int dh = deploy.Measure(right), rh = recent.Measure(right), lh = period.Measure(left);
                    int h = Math.Max(lh, dh + gap + rh);
                    period.SetBounds(0, y, left, h);
                    deploy.SetBounds(left + gap, y, right, dh);
                    recent.SetBounds(left + gap, y + dh + gap, right, h - dh - gap);
                    y += h + gap;
                }
                else y = Pair(period, recent, y, left, right, gap);
            }
            else
            {
                foreach (OverviewTile t in new OverviewTile[] { now, attention, deploy, period, recent })
                {
                    if (!t.Visible) continue;
                    int th = t.Measure(w);
                    t.SetBounds(0, y, w, th);
                    y += th + gap;
                }
            }
            col.ResumeLayout(true);
            col.SetBounds(Math.Max(pad, (avail - w) / 2), Dpi.S(20) + AutoScrollPosition.Y, w, y + Dpi.S(20));
            foreach (OverviewTile t in Tiles) t.PerformLayout();
        }

        static int Pair(OverviewTile a, OverviewTile b, int y, int left, int right, int gap)
        {
            int h = Math.Max(a.Measure(left), b.Measure(right));
            a.SetBounds(0, y, left, h);
            b.SetBounds(left + gap, y, right, h);
            return y + h + gap;
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            col.BackColor = t.Bg;
            bar.BackColor = t.Bg;
            head.ApplyTheme(t);
            foreach (FlatButton b in new FlatButton[] { bCustomer, bSearch, bRefresh, bThisPc }) b.ApplyTheme(t);
            side.ApplyTheme(t);
            Search.BackColor = t.Field;
            Search.ForeColor = t.Text;
            status.ForeColor = t.Text3;
            status.BackColor = t.Bg;
            foreach (OverviewTile x in Tiles) x.ApplyTheme(t);
            Invalidate(true);
        }
    }

    // A list of devices in the main window: the recent devices with the search over everything loaded, or the devices
    // behind a tile of Overview.
    public sealed class DeviceListPage : Panel, IThemed
    {
        public event Action<FoundDevice> Open;
        public event EventHandler Changed, Home;

        Theme theme = Theme.Light();
        readonly WelcomeHead head = new WelcomeHead("", "");
        readonly FlowLayoutPanel bar = new FlowLayoutPanel();
        public readonly TextBox Search = new TextBox();
        readonly FlatButton bOpen = new FlatButton("Open", null);
        readonly FlatButton bPin = new FlatButton("Pin", null);
        readonly FlatButton bRemove = new FlatButton("Remove from the list", null);
        readonly FlatButton bHome = new FlatButton("Overview", null);
        readonly Label count = new Label();
        readonly Panel card = new Panel();
        readonly Table<FoundDevice> table = new Table<FoundDevice>();
        readonly System.Windows.Forms.Timer typing = new System.Windows.Forms.Timer();
        Func<string, List<FoundDevice>> rows;
        RecentStore store;
        public string Id;

        public DeviceListPage()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(12));
            head.Mark = false;
            head.Dock = DockStyle.Top;
            bar.Dock = DockStyle.Top;
            bar.Height = Dpi.S(46);
            bar.WrapContents = false;
            bar.Padding = new Padding(0, Dpi.S(6), 0, 0);
            Search.Width = Dpi.S(320);
            Search.BorderStyle = BorderStyle.FixedSingle;
            Search.Font = Fonts.Ui;
            Search.Margin = new Padding(0, Dpi.S(4), Dpi.S(12), 0);
            bar.Controls.Add(Search);
            foreach (FlatButton b in new FlatButton[] { bOpen, bPin, bRemove, bHome })
            {
                b.Compact = true;
                b.AutoWidth();
                b.Margin = new Padding(0, 0, Dpi.S(4), 0);
                bar.Controls.Add(b);
            }
            bOpen.Primary = true;
            count.AutoSize = true;
            count.Font = Fonts.Small;
            count.Margin = new Padding(Dpi.S(8), Dpi.S(9), 0, 0);
            bar.Controls.Add(count);
            table.Col("Device", 170, delegate (FoundDevice f) { return f.Device; });
            table.Col("State", 220, delegate (FoundDevice f) { return f.StateText; }).Fill = true;
            table.Col("Customer", 130, delegate (FoundDevice f) { return f.Customer; });
            table.Col("Source", 130, delegate (FoundDevice f) { return RecentSources.Name(f.Source); });
            table.Col("Profile or task sequence", 200, delegate (FoundDevice f) { return f.Profile; }).Fill = true;
            table.Col("User", 170, delegate (FoundDevice f) { return f.User; });
            table.Col("Serial number", 120, delegate (FoundDevice f) { return f.Serial; });
            table.Col("Note", 160, delegate (FoundDevice f) { return f.Note; });
            Column<FoundDevice> seen = table.Col("Last seen", 110, delegate (FoundDevice f) { return f.LastUtc.Year > 2000 ? Txt.Ago(f.LastUtc, DateTime.UtcNow) : ""; });
            seen.SortKey = delegate (FoundDevice f) { return (DateTime.UtcNow - f.LastUtc).TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture); };
            table.Col("Pinned", 70, delegate (FoundDevice f) { return f.Pinned ? "Pinned" : ""; });
            table.ForeColor = delegate (FoundDevice f) { return f.State == State.Failed ? theme.ErrFg : States.IsActive(f.State) ? theme.Accent : Color.Empty; };
            table.SelectionChanged += delegate { UpdateButtons(); };
            table.Grid.CellDoubleClick += delegate (object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) Safe.Run(OpenCurrent); };
            table.Grid.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.Handled = true; Safe.Run(OpenCurrent); } };
            table.MenuExtras = delegate (ContextMenuStrip m, FoundDevice f)
            {
                Menus.Add(m, "Open", delegate { OpenCurrent(); });
                if (store != null && f.Recent)
                {
                    Menus.Add(m, f.Pinned ? "Unpin" : "Pin to the top", delegate { TogglePin(); });
                    Menus.Add(m, "Remove from the list", delegate { RemoveCurrent(); });
                }
            };
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(1);
            card.Controls.Add(table.Grid);
            Controls.Add(card);
            Controls.Add(bar);
            Controls.Add(head);
            typing.Interval = 250;
            typing.Tick += Safe.Wrap(delegate { typing.Stop(); Bind(); });
            Search.TextChanged += delegate { typing.Stop(); typing.Start(); };
            Search.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Down) { e.Handled = true; table.Grid.Focus(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; typing.Stop(); Bind(); if (table.Rows.Count > 0) { table.Select(table.Rows[0]); OpenCurrent(); } }
            };
            bOpen.Click += Safe.Wrap(delegate { OpenCurrent(); });
            bPin.Click += Safe.Wrap(delegate { TogglePin(); });
            bRemove.Click += Safe.Wrap(delegate { RemoveCurrent(); });
            bHome.Click += Safe.Wrap(delegate { if (Home != null) Home(this, EventArgs.Empty); });
            Resize += delegate { head.Height = head.Measure(Math.Max(Dpi.S(200), ClientSize.Width - Padding.Horizontal)); };
            HandleCreated += delegate { try { SendMessage(Search.Handle, 0x1501, (IntPtr)1, "Search devices: name, serial number, user, profile or task sequence"); } catch (Exception) { } };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // Shows a list. rowsFor gives the rows for what is typed in the search box. With a store, Pin and Remove work on
        // the recent devices.
        public void Show(string id, string title, string desc, Func<string, List<FoundDevice>> rowsFor, RecentStore recent, string query)
        {
            Id = id;
            head.Title = title;
            head.Desc = desc;
            head.Height = head.Measure(Math.Max(Dpi.S(200), ClientSize.Width - Padding.Horizontal));
            head.Invalidate();
            rows = rowsFor;
            store = recent;
            bPin.Visible = recent != null;
            bRemove.Visible = recent != null;
            typing.Stop();
            Search.Text = query ?? "";
            table.EmptyText = recent != null ? "No device matches. The search looks at the recent devices and the devices Overview has loaded: device name, serial number, user, profile and task sequence." : "No device matches.";
            Bind();
        }

        public void Bind()
        {
            List<FoundDevice> l = rows == null ? new List<FoundDevice>() : rows(Search.Text.Trim());
            table.SetRows(l);
            count.Text = l.Count + " device" + (l.Count == 1 ? "" : "s");
            if (l.Count > 0 && table.Current == null) table.Select(l[0]);
            UpdateButtons();
        }

        public string Query
        {
            get { return Search.Text.Trim(); }
        }

        void UpdateButtons()
        {
            FoundDevice f = table.Current;
            bOpen.Enabled = f != null;
            bPin.Enabled = f != null && f.Recent;
            bRemove.Enabled = f != null && f.Recent;
            bPin.Text = f != null && f.Pinned ? "Unpin" : "Pin";
            bPin.Invalidate();
            bOpen.Invalidate();
            bRemove.Invalidate();
        }

        void OpenCurrent()
        {
            FoundDevice f = table.Current;
            if (f != null && Open != null) Open(f);
        }

        void TogglePin()
        {
            FoundDevice f = table.Current;
            if (f == null || store == null || !f.Recent) return;
            store.Pin(f.Key, !f.Pinned);
            Safe.Say(f.Pinned ? f.Device + " is not pinned any more." : f.Device + " is pinned to the top of the recent devices.");
            Bind();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        void RemoveCurrent()
        {
            FoundDevice f = table.Current;
            if (f == null || store == null || !f.Recent) return;
            store.Remove(f.Key);
            Safe.Say(f.Device + " is removed from the recent devices. Nothing on the device or in the uploads is changed.");
            Bind();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            bar.BackColor = t.Bg;
            card.BackColor = t.Divider;
            head.ApplyTheme(t);
            foreach (FlatButton b in new FlatButton[] { bOpen, bPin, bRemove, bHome }) b.ApplyTheme(t);
            Search.BackColor = t.Field;
            Search.ForeColor = t.Text;
            count.ForeColor = t.Text3;
            count.BackColor = t.Bg;
            table.ApplyTheme(t);
            Invalidate(true);
        }
    }
}
