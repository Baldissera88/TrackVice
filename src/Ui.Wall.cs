namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Threading;
    using System.Windows.Forms;

    // Every device with the agent on one screen, for a TV in a staging room. Reads only the list, never a whole session.
    public sealed class WallView : Form
    {
        public RemoteDeployment Picked;
        public readonly AgentCatalog Catalog;
        // Shows the filters when the window opens. Off for screenshots without them.
        public bool FiltersAtStart = true;

        static readonly int[] KeepHours = { 1, 2, 4, 8 };
        Theme theme;
        readonly bool demo;
        readonly WallBoard board = new WallBoard();
        readonly WallHeader header = new WallHeader();
        readonly WallTiles tiles = new WallTiles();
        readonly Panel filterRow = new Panel();
        readonly Panel filterBox = new Panel();
        readonly FlowLayoutPanel filters = new FlowLayoutPanel();
        readonly Segmented show = new Segmented("All", "Running", "Failed", "Finished today");
        readonly Segmented keep = new Segmented("1 h", "2 h", "4 h", "8 h");
        readonly TextBox search = new TextBox();
        readonly List<Label> labels = new List<Label>();
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();
        bool loading, full;
        Rectangle normal;
        Point lastMouse = new Point(-1, -1);
        string error;
        DateTime readAt;

        public WallView(Theme t, AgentCatalog c, bool isDemo)
        {
            theme = t;
            Catalog = c;
            demo = isDemo;
            Text = "TrackVice wall view";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            KeyPreview = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(Dpi.S(640), Dpi.S(420));
            DialogFit.Keep(this);

            header.Dock = DockStyle.Top;
            header.Height = Dpi.S(96);
            header.Source = demo ? "Demo data: made-up devices." : c.Source;
            header.Close.Click += delegate { Close(); };
            tiles.Dock = DockStyle.Fill;
            tiles.Picked += delegate (object s, EventArgs e) { Pick(tiles.Clicked); };
            tiles.EmptyText = "Reading the uploads…";

            filters.AutoSize = true;
            filters.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            filters.WrapContents = false;
            filters.Padding = new Padding(Dpi.S(14), Dpi.S(10), Dpi.S(14), Dpi.S(10));
            filters.Location = new Point(1, 1);
            show.Margin = new Padding(0, 0, Dpi.S(14), 0);
            show.Changed += Safe.Wrap(delegate { Bind(); });
            search.Width = Dpi.S(240);
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Margin = new Padding(0, Dpi.S(5), Dpi.S(20), 0);
            search.TextChanged += delegate { Safe.Run(Bind); };
            keep.Selected = Math.Max(0, Array.IndexOf(KeepHours, Prefs.GetInt("WallKeepHours", 2)));
            keep.Margin = new Padding(0, 0, Dpi.S(20), 0);
            keep.Changed += Safe.Wrap(delegate
            {
                Prefs.Set("WallKeepHours", KeepHours[keep.Selected].ToString(CultureInfo.InvariantCulture));
                Bind();
            });
            filters.Controls.Add(show);
            filters.Controls.Add(search);
            filters.Controls.Add(Note("Finished devices stay", false));
            filters.Controls.Add(keep);
            filters.Controls.Add(Note("F11 full screen · Esc leaves", true));
            filterBox.Padding = new Padding(1);
            filterBox.Controls.Add(filters);
            filterRow.Dock = DockStyle.Top;
            filterRow.Visible = false;
            filterRow.Controls.Add(filterBox);
            filters.SizeChanged += delegate { PlaceFilters(); };

            Controls.Add(tiles);
            Controls.Add(filterRow);
            Controls.Add(header);
            Watch(this);
            ApplyTheme(t);

            refresh.Interval = Math.Max(15, c.RefreshSeconds) * 1000;
            refresh.Tick += Safe.Wrap(delegate { LoadList(); });
            clock.Interval = 1000;
            clock.Tick += Safe.Wrap(delegate { header.Invalidate(); tiles.Tick(); });
            hide.Interval = 4000;
            hide.Tick += Safe.Wrap(delegate
            {
                if (search.Focused || filterRow.ClientRectangle.Contains(filterRow.PointToClient(Cursor.Position))) return;
                hide.Stop();
                filterRow.Visible = false;
                tiles.ClearHover();
            });
            Load += delegate { SetFull(true); };
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            HandleCreated += delegate
            {
                try { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Search devices and profiles"); } catch (Exception) { }
            };
            Shown += Safe.Wrap(delegate
            {
                if (FiltersAtStart) ShowFilters();
                LoadList();
                refresh.Start();
                clock.Start();
            });
            FormClosed += delegate { refresh.Stop(); clock.Stop(); hide.Stop(); };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        Label Note(string text, bool quiet)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, Dpi.S(8), Dpi.S(8), 0);
            l.Tag = quiet ? "quiet" : null;
            labels.Add(l);
            return l;
        }

        // Every control reports mouse moves, so a move anywhere brings the filters back.
        void Watch(Control c)
        {
            c.MouseMove += delegate { Moved(); };
            foreach (Control k in c.Controls) Watch(k);
        }

        void Moved()
        {
            Point p = Cursor.Position;
            if (p == lastMouse) return;
            bool first = lastMouse.X < 0;
            lastMouse = p;
            if (!first) ShowFilters();
        }

        public void ShowFilters()
        {
            PlaceFilters();
            filterRow.Visible = true;
            hide.Stop();
            hide.Start();
        }

        void PlaceFilters()
        {
            filterBox.Size = new Size(filters.Width + 2, filters.Height + 2);
            filterBox.Location = new Point(Dpi.S(24), Dpi.S(12));
            filterRow.Height = filterBox.Height + Dpi.S(12);
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            header.ApplyTheme(t);
            tiles.ApplyTheme(t);
            show.ApplyTheme(t);
            keep.ApplyTheme(t);
            filterRow.BackColor = t.Bg;
            filterBox.BackColor = t.Divider;
            filters.BackColor = t.Card;
            search.BackColor = t.Field;
            search.ForeColor = t.Text;
            foreach (Label l in labels) { l.BackColor = t.Card; l.ForeColor = "quiet".Equals(l.Tag) ? t.Text3 : t.Text2; }
            if (IsHandleCreated) Draw.TitleBar(Handle, t);
            Invalidate(true);
        }

        // Full screen covers the whole screen without a border. Otherwise it is a normal window.
        void SetFull(bool on)
        {
            Screen sc = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2));
            if (on)
            {
                if (!full && FormBorderStyle != FormBorderStyle.None) normal = Bounds;
                full = true;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = sc.Bounds;
            }
            else
            {
                full = false;
                FormBorderStyle = FormBorderStyle.Sizable;
                Rectangle wa = sc.WorkingArea;
                if (normal.Width <= 0 || !wa.IntersectsWith(normal))
                {
                    Size s = new Size(Math.Min(wa.Width, Dpi.S(1280)), Math.Min(wa.Height, Dpi.S(800)));
                    normal = new Rectangle(wa.X + (wa.Width - s.Width) / 2, wa.Y + (wa.Height - s.Height) / 2, s.Width, s.Height);
                }
                Bounds = normal;
            }
            header.Full = full;
            PlaceFilters();
        }

        void LoadList()
        {
            if (loading) return;
            loading = true;
            ISessionStore st = Catalog.Store;
            Thread th = new Thread(delegate ()
            {
                List<StoreEntry> l = null;
                Exception err = null;
                try { l = st.List(AgentData.Prefix); }
                catch (Exception ex) { err = ex; }
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Safe.Run(delegate
                        {
                            loading = false;
                            if (err != null) error = "Reading failed: " + err.Message + " Trying again in " + (refresh.Interval / 1000) + " s.";
                            else
                            {
                                error = null;
                                readAt = DateTime.UtcNow;
                                board.Update(l, readAt);
                            }
                            Bind();
                        });
                    });
                }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        WallFilter Filter
        {
            get { return show.Selected == 1 ? WallFilter.Running : show.Selected == 2 ? WallFilter.Failed : show.Selected == 3 ? WallFilter.FinishedToday : WallFilter.All; }
        }

        void Bind()
        {
            DateTime now = DateTime.UtcNow;
            DateTime today = WallModel.TodayStartUtc(now);
            List<WallTile> l = board.Shown(Filter, search.Text, now, today, TimeSpan.FromHours(KeepHours[Math.Max(0, keep.Selected)]));
            header.Summary = board.Summary(now, today).Text();
            string q = search.Text.Trim();
            header.Filter = Filter == WallFilter.All && q.Length == 0 ? "" : "Showing " + (Filter == WallFilter.All ? "all" : Filter == WallFilter.Running ? "running" : Filter == WallFilter.Failed ? "failed" : "finished today") + (q.Length > 0 ? " matching “" + q + "”" : "");
            header.Status = error ?? (readAt.Year > 2000 ? "Updated " + readAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ", every " + (refresh.Interval / 1000) + " s" : "");
            if (board.Tiles.Count == 0) tiles.EmptyText = error ?? (readAt.Year > 2000 ? "No device has uploaded yet.\n\nDevices with the TrackVice agent show here as they upload." : "Reading the uploads…");
            else tiles.EmptyText = "No devices match. Move the mouse to change the filters.";
            tiles.SetTiles(l);
            header.Invalidate();
        }

        // The filter shown: 0 all, 1 running, 2 failed, 3 finished today.
        public int FilterIndex
        {
            get { return show.Selected; }
            set { show.Selected = value; }
        }

        void Pick(WallTile t)
        {
            if (t == null || t.Entry == null) return;
            Picked = AgentData.ToDeployment(t.Entry, DateTime.UtcNow);
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (search.Focused && search.Text.Length > 0) { search.Text = ""; return true; }
                Close();
                return true;
            }
            if (keyData == Keys.F11) { Safe.Run(delegate { SetFull(!full); }); return true; }
            if (keyData == Keys.F5) { Safe.Run(LoadList); return true; }
            if (keyData == (Keys.Control | Keys.F)) { ShowFilters(); search.Focus(); search.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // The top of the wall: what it is, the counts of today and the time.
    sealed class WallHeader : FlatControl
    {
        public readonly FlatButton Close = new FlatButton("", "");
        public string Summary = "", Source = "", Status = "", Filter = "";
        public bool Full;
        Font big, mid;

        public WallHeader()
        {
            Close.Compact = true;
            if (Fonts.Icons == null) Close.Text = "Close";
            Close.AutoWidth();
            Controls.Add(Close);
            big = new Font(Fonts.Title.FontFamily, Dpi.S(30), FontStyle.Bold, GraphicsUnit.Pixel);
            mid = new Font(Fonts.Ui.FontFamily, Dpi.S(19), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { big.Dispose(); mid.Dispose(); }
            base.Dispose(disposing);
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            BackColor = t.Bg;
            Close.ApplyTheme(t);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Close.Location = new Point(Width - Close.Width - Dpi.S(14), Dpi.S(12));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            int pad = Dpi.S(24), m = Dpi.S(48);
            Draw.Mark(g, new RectangleF(pad, Dpi.S(20), m, m));
            int x = pad + m + Dpi.S(16);
            string time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            int timeW = Draw.Measure("00:00", big).Width + Dpi.S(8);
            int right = Close.Left - Dpi.S(18);
            Draw.Text(g, time, big, new Rectangle(right - timeW, Dpi.S(14), timeW, big.Height + 2), T.Text, Draw.Line | TextFormatFlags.Right);
            string under = Status.Length > 0 ? Status : Source;
            int underW = Math.Min(Dpi.S(520), Draw.Measure(under, Fonts.Ui).Width + Dpi.S(8));
            Draw.Text(g, under, Fonts.Ui, new Rectangle(right - underW, Dpi.S(16) + big.Height, underW, Fonts.Ui.Height + 4), Status.StartsWith("Reading failed") ? T.ErrFg : T.Text3, Draw.Line | TextFormatFlags.Right);
            int textW = right - timeW - Dpi.S(24) - x;
            string title = "Enrollments" + (Filter.Length > 0 ? "  ·  " + Filter : "");
            Draw.Text(g, title, big, new Rectangle(x, Dpi.S(14), textW, big.Height + 2), T.Text, Draw.Line);
            Draw.Text(g, Summary, mid, new Rectangle(x, Dpi.S(18) + big.Height, Math.Max(0, right - Math.Max(underW, timeW) - Dpi.S(16) - x), mid.Height + 4), T.Text2, Draw.Line);
            using (Pen p = new Pen(T.Divider)) g.DrawLine(p, pad, Height - 1, Width - pad, Height - 1);
        }
    }

    // The tiles, one per device, in a grid that fits the window.
    sealed class WallTiles : FlatControl
    {
        public string EmptyText = "";
        public WallTile Clicked;
        public event EventHandler Picked;
        List<WallTile> shown = new List<WallTile>();
        readonly List<Rectangle> rects = new List<Rectangle>();
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        WallGrid grid;
        int hot = -1, fontH = -1;
        Point lastMove;
        float spin;
        Font name, mid, small, pct, empty;

        public WallTiles()
        {
            anim.Interval = 80;
            anim.Tick += delegate
            {
                spin = (spin + 18) % 360;
                for (int i = 0; i < rects.Count && i < shown.Count; i++)
                    if (shown[i].State == WallState.Running) Invalidate(IconRect(rects[i]));
            };
            anim.Start();
            empty = new Font(Fonts.Ui.FontFamily, Dpi.S(22), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Dispose(); DropFonts(); empty.Dispose(); }
            base.Dispose(disposing);
        }

        void DropFonts()
        {
            foreach (Font f in new Font[] { name, mid, small, pct }) if (f != null) f.Dispose();
            name = mid = small = pct = null;
        }

        public void SetTiles(List<WallTile> l)
        {
            WallTile keep = hot >= 0 && hot < shown.Count ? shown[hot] : null;
            shown = l;
            hot = keep == null ? -1 : l.IndexOf(keep);
            Lay();
            Invalidate();
        }

        // The pointer has rested for a while: no tile stays lit on the screen in the room.
        public void ClearHover()
        {
            if (hot < 0) return;
            hot = -1;
            Invalidate();
        }

        // Once a second: the times in every tile move on.
        public void Tick()
        {
            Invalidate();
        }

        const int BaseH = 214, MinH = 150, MinW = 390;

        int Pad
        {
            get { return Dpi.S(24); }
        }

        void Lay()
        {
            rects.Clear();
            int w = Width - Pad * 2, h = Height - Pad - Dpi.S(16);
            grid = WallGrid.Fit(shown.Count, w, h, Dpi.S(MinW), Dpi.S(BaseH), Dpi.S(MinH), Dpi.S(16));
            for (int i = 0; i < grid.Shown; i++)
            {
                int x, y;
                grid.Cell(i, out x, out y);
                rects.Add(new Rectangle(Pad + x, Dpi.S(16) + y, grid.TileW, grid.TileH));
            }
            if (fontH != grid.TileH) MakeFonts(grid.TileH);
        }

        // Text grows with the tiles, so it stays readable from a distance.
        void MakeFonts(int tileH)
        {
            DropFonts();
            fontH = tileH;
            float k = Math.Max(0.72f, Math.Min(1.25f, tileH / (float)Dpi.S(BaseH)));
            FontFamily semi = Fonts.UiSemibold.FontFamily;
            FontFamily ui = Fonts.Ui.FontFamily;
            FontStyle semiStyle = Fonts.UiSemibold.Style;
            name = new Font(Fonts.Title.FontFamily, Dpi.S(30) * k, Fonts.Title.Style | FontStyle.Bold, GraphicsUnit.Pixel);
            mid = new Font(ui, Dpi.S(19) * k, FontStyle.Regular, GraphicsUnit.Pixel);
            pct = new Font(semi, Dpi.S(19) * k, semiStyle | FontStyle.Bold, GraphicsUnit.Pixel);
            small = new Font(ui, Dpi.S(16) * k, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Lay();
            Invalidate();
        }

        Rectangle IconRect(Rectangle r)
        {
            int s = name == null ? Dpi.S(30) : name.Height;
            int p = Inner(r);
            return new Rectangle(r.Right - p - s, r.Y + p, s, s);
        }

        int Inner(Rectangle r)
        {
            return Math.Max(Dpi.S(12), r.Height / 11);
        }

        static State IconState(WallState s)
        {
            switch (s)
            {
                case WallState.Running: return State.Installing;
                case WallState.Finished: return State.Succeeded;
                case WallState.Failed: return State.Failed;
                default: return State.Unknown;
            }
        }

        Color Tone(WallState s)
        {
            switch (s)
            {
                case WallState.Running: return T.Accent;
                case WallState.Finished: return T.OkFg;
                case WallState.Failed: return T.ErrFg;
                default: return T.Text3;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            if (shown.Count == 0)
            {
                Rectangle er = new Rectangle(Pad, Height / 3, Width - Pad * 2, Height / 2);
                Draw.Text(g, EmptyText, empty, er, T.Text3, Draw.Wrap | TextFormatFlags.HorizontalCenter);
                return;
            }
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < rects.Count && i < shown.Count; i++)
                if (e.ClipRectangle.IntersectsWith(rects[i])) PaintTile(g, shown[i], rects[i], i == hot, now);
            if (grid != null && grid.Hidden > 0)
            {
                string more = grid.Hidden + " more device" + (grid.Hidden == 1 ? " does" : "s do") + " not fit. Use the filters or the search to see them.";
                Draw.Text(g, more, Fonts.Ui, new Rectangle(Pad, Height - Dpi.S(26), Width - Pad * 2, Dpi.S(20)), T.Text3, Draw.Line | TextFormatFlags.Right);
            }
        }

        void PaintTile(Graphics g, WallTile t, Rectangle r, bool isHot, DateTime now)
        {
            Color c = Tone(t.State);
            RectangleF box = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f);
            Color bg = t.State == WallState.Failed ? Theme.Mix(T.Card, T.ErrFg, T.IsDark ? 0.12f : 0.07f) : T.Card;
            if (isHot) bg = Theme.Mix(bg, T.Accent, 0.08f);
            float rad = Dpi.S(10);
            Shapes.Fill(g, box, rad, bg);
            Shapes.Stroke(g, box, rad, isHot ? T.Accent : t.State == WallState.Failed ? Theme.Mix(T.Divider, T.ErrFg, 0.5f) : T.Divider, isHot ? 2f : 1f);
            // The coloured edge says the state at a glance from across the room.
            GraphicsState st = g.Save();
            using (GraphicsPath clip = Shapes.Round(box, rad)) g.SetClip(clip);
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, r.X, r.Y, Dpi.S(7), r.Height);
            g.Restore(st);

            int p = Inner(r);
            int x = r.X + p + Dpi.S(8), w = r.Right - p - x;
            int y = r.Y + p;
            Rectangle ic = IconRect(r);
            StateLook.Icon(g, IconState(t.State), new RectangleF(ic.X + 2, ic.Y + 2, ic.Width - 4, ic.Height - 4), T, spin);
            Draw.Text(g, t.Device, name, new Rectangle(x, y, ic.X - Dpi.S(10) - x, name.Height + 2), T.Text, Draw.Line);
            y += name.Height + Dpi.S(2);
            Draw.Text(g, (t.User != null ? t.User + " · " : "") + t.Deployment, mid, new Rectangle(x, y, w, mid.Height + 2), T.Text2, Draw.Line);
            y += mid.Height;

            // The bar, its percent and how many of the items are done, spread over what is left above the bottom line.
            int bottomY = r.Bottom - p - small.Height;
            int free = bottomY - y;
            int barH = Math.Max(Dpi.S(6), r.Height / 22);
            int lineY = y + Math.Max(Dpi.S(4), (free - barH - mid.Height - Dpi.S(8)) / 3);
            string percent = t.Percent.ToString(CultureInfo.InvariantCulture) + " %";
            int pw = Draw.Measure("100 %", pct).Width + Dpi.S(6);
            int barY = lineY + (pct.Height - barH) / 2;
            StateLook.Bar(g, new RectangleF(x, barY, w - pw - Dpi.S(10), barH), t.Progress, t.State == WallState.Silent ? T.Text3 : c, Theme.Mix(bg, T.Text3, 0.25f));
            Draw.Text(g, percent, pct, new Rectangle(r.Right - p - pw, lineY, pw, pct.Height + 2), T.Text, Draw.Line | TextFormatFlags.Right);
            int curY = lineY + pct.Height + Math.Max(Dpi.S(4), (free - barH - mid.Height - Dpi.S(8)) / 3);
            string line = t.Line(now);
            string count = t.Items > 0 && t.State != WallState.Finished ? t.Done + " of " + t.Items : "";
            int cw = count.Length > 0 ? Draw.Measure(count, mid).Width + Dpi.S(4) : 0;
            Color lc = t.State == WallState.Failed ? T.ErrFg : t.State == WallState.Silent ? T.WarnFg : t.State == WallState.Finished ? T.OkFg : T.Text;
            Draw.Text(g, line, mid, new Rectangle(x, curY, w - cw - Dpi.S(10), mid.Height + 2), lc, Draw.Line);
            if (cw > 0) Draw.Text(g, count, mid, new Rectangle(r.Right - p - cw, curY, cw, mid.Height + 2), T.Text3, Draw.Line | TextFormatFlags.Right);

            TimeSpan? el = t.Elapsed(now);
            string left = el.HasValue ? (t.Final ? "Took " : "Running for ") + Txt.DurationShort(el) : "";
            if (!string.IsNullOrEmpty(t.Version)) left += (left.Length > 0 ? " · " : "") + AgentVersions.Header(t.Version);
            string right = "Last upload " + Txt.Ago(t.LastUtc, now);
            int rw = Draw.Measure(right, small).Width + Dpi.S(4);
            Draw.Text(g, left, small, new Rectangle(x, bottomY, w - rw - Dpi.S(10), small.Height + 2), T.Text3, Draw.Line);
            Draw.Text(g, right, small, new Rectangle(r.Right - p - rw, bottomY, rw, small.Height + 2), t.State == WallState.Silent ? T.WarnFg : T.Text3, Draw.Line | TextFormatFlags.Right);
        }

        int HitTest(Point pt)
        {
            for (int i = 0; i < rects.Count && i < shown.Count; i++) if (rects[i].Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = PointToScreen(e.Location);
            if (p == lastMove) { base.OnMouseMove(e); return; }
            lastMove = p;
            int i = HitTest(e.Location);
            if (i != hot)
            {
                if (hot >= 0 && hot < rects.Count) Invalidate(Grow(rects[hot]));
                hot = i;
                if (hot >= 0) Invalidate(Grow(rects[hot]));
                Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
            }
            base.OnMouseMove(e);
        }

        static Rectangle Grow(Rectangle r)
        {
            r.Inflate(2, 2);
            return r;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hot >= 0) { hot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = HitTest(e.Location);
            if (i < 0) return;
            Clicked = shown[i];
            if (Picked != null) Picked(this, EventArgs.Empty);
        }
    }

    public sealed partial class MainForm
    {
        // The wall view of the devices with the agent. Without a location it uses the one of this window or the current customer.
        void OpenWall(string upload, bool demo)
        {
            AgentCatalog c = null;
            if (demo) c = new AgentCatalog(new WallDemoStore());
            else if (!string.IsNullOrWhiteSpace(upload))
            {
                try { c = new AgentCatalog(Stores.Open(upload)); }
                catch (Exception ex) { Status("The wall view could not open the upload location: " + ex.Message); return; }
            }
            else
            {
                c = agentCatalog as AgentCatalog;
                if (c == null)
                {
                    try
                    {
                        Customer cu = CustomerUi.Book.Current;
                        if (cu != null && !string.IsNullOrWhiteSpace(cu.AgentLocation)) { c = new AgentCatalog(Stores.Open(cu.AgentLocation)); agentCatalog = c; }
                    }
                    catch (Exception) { c = null; }
                }
                if (c == null)
                {
                    using (RemoteDialog r = new RemoteDialog(RemoteDialog.Agents, theme, null, null, false))
                    {
                        r.NamesOnly = true;
                        r.ShowDialog(this);
                        if (r.Catalog != null) agentCatalog = r.Catalog;
                    }
                    c = agentCatalog as AgentCatalog;
                    if (c == null) return;
                }
            }
            using (WallView w = new WallView(theme, c, demo))
            {
                w.ShowDialog(this);
                if (w.Picked == null) return;
                watchedCustomer = demo ? null : CustomerUi.CurrentName;
                Begin(Engine.ForRemote(c.Open(w.Picked), w.Picked), true);
            }
        }
    }
}
