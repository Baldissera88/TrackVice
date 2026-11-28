namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // One line in a tile of Overview: a device, an app, a step or a heading.
    public sealed class TileRow
    {
        public string Text, Sub, Sub2, Right, Link;
        public bool HasState;
        public State State;
        public double? Percent;
        // The colour of the right text: 0 plain, 1 warning, 2 error.
        public int Level;
        public bool Header;
        // The second line wraps instead of being cut off.
        public bool Wrap;
        // A number in front of the row, for a ranked list.
        public int Rank;
        // A row that is history, such as a deployment replaced by a newer one: drawn quieter.
        public bool Quiet;
        public object Tag;
    }

    // One number in the strip at the top of a tile, such as the deployments today. A Link opens the list behind it.
    public sealed class CountCell
    {
        public string Label, Value, Sub;
        public bool Link;
    }

    // A section of Overview: a title with a note beside it, and under it a card with numbers, a chart and rows that can be
    // clicked, device cards in a grid, one card per item, or a plain empty state with the button that sets the source up.
    public sealed partial class OverviewTile : FlatControl
    {
        public string Title, Note;
        public TileMode Mode;
        // The empty state is good news, such as nothing that needs attention.
        public bool Calm;
        // Rows under two headings go side by side when the card is wide, such as the slowest steps and the ones that fail most.
        public bool SplitGroups;
        public readonly List<TileRow> Rows = new List<TileRow>();
        // Label, today, last 7 days. The first entry is the heading.
        public readonly List<string[]> Stats = new List<string[]>();
        // Numbers side by side above the rows. They go two by two when the tile is narrow.
        public readonly List<CountCell> Counts = new List<CountCell>();
        // The chart of the last days, oldest first.
        public readonly List<DayBar> Days = new List<DayBar>();
        public string DaysTitle;
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
        readonly HoverFade fade;
        object pressed;

        static int Pad { get { return Dpi.S(16); } }

        public OverviewTile(string title)
        {
            Title = title;
            TabStop = false;
            EmptyButton.Visible = false;
            EmptyButton.Primary = true;
            Controls.Add(EmptyButton);
            fade = new HoverFade(this);
        }

        public bool Empty
        {
            get { return Rows.Count == 0 && Stats.Count == 0 && Counts.Count == 0 && Days.Count == 0; }
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

        // The title and the note, above the card.
        static int HeadHeight
        {
            get { return OvFonts.Section.Height + Dpi.S(12); }
        }

        // Where the content of the card starts.
        static int BodyTop
        {
            get { return HeadHeight + Dpi.S(12); }
        }

        static int EmptyY
        {
            get { return HeadHeight + Pad + Dpi.S(2); }
        }

        int EmptyOffset
        {
            get { return Calm ? Dpi.S(46) : 0; }
        }

        int FootHeight
        {
            get { return string.IsNullOrEmpty(Footer) ? 0 : Dpi.S(36); }
        }

        // A key for the fade and the pressed look that stays the same when the rows are filled again.
        string KeyOf(TileRow r)
        {
            return "r" + Rows.IndexOf(r).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        int RowHeight(TileRow r, int w)
        {
            if (r.Header) return Dpi.S(32);
            int h = string.IsNullOrEmpty(r.Sub) ? Dpi.S(34) : Dpi.S(46);
            if (r.Wrap && !string.IsNullOrEmpty(r.Sub)) h = Dpi.S(5) + Fonts.Ui.Height + Dpi.S(2) + Draw.WrapHeight(r.Sub, Fonts.Small, Math.Max(Dpi.S(60), w - Lead(r))) + Dpi.S(9);
            if (r.Percent.HasValue) h += Dpi.S(8);
            return h;
        }

        // The room of the state glyph or the rank in front of the text.
        static int Lead(TileRow r)
        {
            return r.HasState || r.Rank > 0 ? Dpi.S(24) : 0;
        }

        // The columns of the count strip: all in one row when each gets room for "Last 30 days" and a number, else two.
        int CountColumns(int inner)
        {
            int n = Counts.Count;
            if (n == 0) return 1;
            return inner / n >= Dpi.S(104) ? n : Math.Min(2, n);
        }

        int CountsHeight(int inner)
        {
            int cols = CountColumns(inner);
            int rows = (Counts.Count + cols - 1) / cols;
            return rows * CountCellHeight + Dpi.S(8);
        }

        static int CountCellHeight
        {
            get { return Fonts.Small.Height + Fonts.Title.Height + Fonts.Small.Height + Dpi.S(18); }
        }

        int StatsHeight
        {
            get { return Stats.Count * Dpi.S(26); }
        }

        public int Measure(int width)
        {
            if (Empty)
            {
                int tw = Math.Max(Dpi.S(80), width - Pad * 2 - EmptyOffset);
                int h = EmptyY;
                if (!string.IsNullOrEmpty(EmptyTitle)) h += Fonts.UiSemibold.Height + Dpi.S(4);
                h += Draw.WrapHeight(EmptyText, Fonts.Ui, tw);
                if (EmptyButton.Visible) h += Dpi.S(14) + EmptyButton.Height;
                if (Calm) h = Math.Max(h, EmptyY + Dpi.S(30));
                return h + Pad + Dpi.S(2);
            }
            if (Mode == TileMode.Grid) return HeadHeight + GridHeight(width) + FootHeight;
            if (Mode == TileMode.Cards) return HeadHeight + CardsHeight(width) + FootHeight;
            return Body(null, width);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (EmptyButton == null || !EmptyButton.Visible) return;
            int tw = Math.Max(Dpi.S(80), Width - Pad * 2 - EmptyOffset);
            int y = EmptyY;
            if (!string.IsNullOrEmpty(EmptyTitle)) y += Fonts.UiSemibold.Height + Dpi.S(4);
            y += Draw.WrapHeight(EmptyText, Fonts.Ui, tw) + Dpi.S(14);
            EmptyButton.Location = new Point(Pad + EmptyOffset, y);
        }

        Color LevelColor(int level)
        {
            return level >= 2 ? T.ErrFg : level == 1 ? T.WarnFg : T.Text2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            hits.Clear();
            statHits.Clear();
            countHits.Clear();
            footerHit = Rectangle.Empty;
            int noteW = string.IsNullOrEmpty(Note) ? 0 : Draw.Measure(Note, Fonts.Small).Width + 2;
            Draw.Text(g, Title, OvFonts.Section, new Rectangle(0, 0, Width - noteW - Dpi.S(12), OvFonts.Section.Height + 2), T.Text, Draw.Line);
            if (noteW > 0) Draw.Text(g, Note, Fonts.Small, new Rectangle(Width - noteW - Dpi.S(2), 0, noteW, OvFonts.Section.Height + 2), T.Text3, Draw.Line);
            if (Empty)
            {
                Frame(g, HeadHeight, Height);
                PaintEmpty(g);
                return;
            }
            if (Mode == TileMode.List)
            {
                Frame(g, HeadHeight, Height);
                Body(g, Width);
                return;
            }
            int y = Mode == TileMode.Grid ? PaintGrid(g, HeadHeight) : PaintCards(g, HeadHeight);
            if (!string.IsNullOrEmpty(Footer))
            {
                footerHit = new Rectangle(Dpi.S(2), y + Dpi.S(10), Draw.Measure(Footer, Fonts.Ui).Width + 2, Dpi.S(24));
                LinkText(g, Footer, footerHit, Equals(fade.Key, "footer"));
            }
        }

        // The card under the title.
        void Frame(Graphics g, int top, int bottom)
        {
            RectangleF cr = new RectangleF(0.5f, top + 0.5f, Width - 1.5f, bottom - top - 1.5f);
            Shapes.Fill(g, cr, Dpi.S(12), T.Card);
            Shapes.Stroke(g, cr, Dpi.S(12), T.Divider, 1f);
        }

        void LinkText(Graphics g, string text, Rectangle r, bool hot)
        {
            if (!hot)
            {
                Draw.Text(g, text, Fonts.Ui, r, T.Accent, Draw.Line);
                return;
            }
            using (Font u = new Font(Fonts.Ui, FontStyle.Underline)) Draw.Text(g, text, u, r, T.AccentHover, Draw.Line);
        }

        void PaintEmpty(Graphics g)
        {
            int y = EmptyY;
            if (Calm) PaintCheck(g, Pad, y + Dpi.S(1), Dpi.S(32));
            int ex = Pad + EmptyOffset, ew = Width - Pad - ex;
            if (!string.IsNullOrEmpty(EmptyTitle))
            {
                Draw.Text(g, EmptyTitle, Fonts.UiSemibold, new Rectangle(ex, y, ew, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
                y += Fonts.UiSemibold.Height + Dpi.S(4);
            }
            Draw.Text(g, EmptyText, Fonts.Ui, new Rectangle(ex, y, ew, Math.Max(Fonts.Ui.Height + 2, Height - y)), T.Text3, Draw.Wrap);
        }

        // The content of the card: the counts, the chart and the table, then the rows. Measures when g is null. Returns
        // the bottom of the card.
        int Body(Graphics g, int width)
        {
            int inner = width - Pad * 2;
            int y = BodyTop;
            if (Counts.Count > 0)
            {
                if (g != null) PaintCounts(g, Pad, y, inner);
                y += CountsHeight(inner);
            }
            bool stats = Stats.Count > 0, days = Days.Count > 0;
            if (stats && days && inner >= Dpi.S(600))
            {
                // The chart and the table side by side.
                int gap = Dpi.S(40), cw = (inner - gap) * 55 / 100, sw = inner - gap - cw;
                if (g != null)
                {
                    PaintDays(g, Pad, y, cw);
                    PaintStats(g, Pad + cw + gap, y + Dpi.S(2), sw);
                }
                y += Math.Max(DaysHeight, StatsHeight) + Dpi.S(10);
            }
            else
            {
                if (days)
                {
                    if (g != null) PaintDays(g, Pad, y, inner);
                    y += DaysHeight + Dpi.S(14);
                }
                if (stats)
                {
                    if (g != null) PaintStats(g, Pad, y, inner);
                    y += StatsHeight + Dpi.S(8);
                }
            }
            List<List<TileRow>> groups = SplitGroups ? Groups() : null;
            if (groups != null && groups.Count == 2 && inner >= Dpi.S(620))
            {
                int gap = Dpi.S(32), cw = (inner - gap) / 2;
                int a = RowsBlock(g, groups[0], Pad, y, cw), b = RowsBlock(g, groups[1], Pad + cw + gap, y, cw);
                y += Math.Max(a, b);
            }
            else y += RowsBlock(g, Rows, Pad, y, inner);
            if (!string.IsNullOrEmpty(Footer))
            {
                if (g != null)
                {
                    footerHit = new Rectangle(Pad, y + Dpi.S(6), Draw.Measure(Footer, Fonts.Ui).Width + 2, Dpi.S(24));
                    LinkText(g, Footer, footerHit, Equals(fade.Key, "footer"));
                }
                y += FootHeight;
            }
            return y + Dpi.S(8);
        }

        // The rows split at each heading.
        List<List<TileRow>> Groups()
        {
            List<List<TileRow>> l = new List<List<TileRow>>();
            foreach (TileRow r in Rows)
            {
                if (r.Header || l.Count == 0) l.Add(new List<TileRow>());
                l[l.Count - 1].Add(r);
            }
            return l;
        }

        int RowsBlock(Graphics g, List<TileRow> rows, int x, int y, int w)
        {
            int top = y;
            for (int i = 0; i < rows.Count; i++)
            {
                int h = RowHeight(rows[i], w);
                if (g != null) PaintRow(g, rows[i], x, y, w, h, i + 1 < rows.Count ? rows[i + 1] : null);
                y += h;
            }
            return y - top;
        }

        void PaintRow(Graphics g, TileRow r, int x, int y, int w, int h, TileRow next)
        {
            if (r.Header)
            {
                Draw.Text(g, r.Text, Fonts.SmallSemibold, new Rectangle(x, y + Dpi.S(10), w, h - Dpi.S(10)), T.Text3, Draw.Line);
                return;
            }
            string key = KeyOf(r);
            Rectangle rr = new Rectangle(x - Dpi.S(10), y + Dpi.S(1), w + Dpi.S(20), h - Dpi.S(2));
            hits.Add(new KeyValuePair<Rectangle, TileRow>(rr, r));
            float a = fade.Of(key);
            if (Equals(pressed, key)) Shapes.Fill(g, rr, Dpi.S(6), T.Pressed);
            else if (a > 0) Shapes.Fill(g, rr, Dpi.S(6), Theme.Mix(T.Card, T.Hover, a));
            bool sub = !string.IsNullOrEmpty(r.Sub);
            int ty = sub ? y + Dpi.S(5) : y;
            int th = sub ? Fonts.Ui.Height + 2 : Dpi.S(34);
            float mid = ty + th / 2f;
            int gx = x, d = Dpi.S(14);
            if (r.HasState && r.Quiet)
            {
                // History: a small hollow ring instead of the state.
                float q = Dpi.S(8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen qp = new Pen(T.Text3, Math.Max(1f, Dpi.S(1.25f)))) g.DrawEllipse(qp, gx + (d - q) / 2f, mid - q / 2f, q, q);
                gx += d + Dpi.S(10);
            }
            else if (r.HasState)
            {
                StateLook.Icon(g, r.State, new RectangleF(gx, mid - d / 2f, d, d), T, 0f);
                gx += d + Dpi.S(10);
            }
            else if (r.Rank > 0)
            {
                Draw.Text(g, r.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture), T.Num(Fonts.SmallSemibold), new Rectangle(gx, ty, d, th), r.Rank == 1 ? T.Text2 : T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                gx += d + Dpi.S(10);
            }
            string right = r.Link ?? r.Right;
            Font rf = r.Link != null ? Fonts.Ui : T.Num(Fonts.Ui);
            int rw = string.IsNullOrEmpty(right) ? 0 : Math.Min(w / 2, Draw.Measure(right, rf).Width + 2);
            int textW = x + w - gx - rw - (rw > 0 ? Dpi.S(12) : 0);
            Draw.Text(g, r.Text, Fonts.Ui, new Rectangle(gx, ty, textW, th), r.Quiet ? T.Text2 : T.Text, Draw.Line);
            if (sub)
            {
                int sy = ty + Fonts.Ui.Height + Dpi.S(2);
                if (r.Wrap) Draw.Text(g, r.Sub, Fonts.Small, new Rectangle(gx, sy, x + w - gx, y + h - sy), T.Text3, Draw.Wrap);
                else Draw.Text(g, r.Sub, Fonts.Small, new Rectangle(gx, sy, x + w - gx, Fonts.Small.Height + 2), T.Text3, Draw.Line);
            }
            if (rw > 0)
                Draw.Text(g, right, rf, new Rectangle(x + w - rw, ty, rw, th), r.Link != null ? T.Accent : r.Quiet ? T.Text3 : LevelColor(r.Level), Draw.Line | TextFormatFlags.Right);
            if (r.Percent.HasValue)
            {
                RectangleF track = new RectangleF(gx, y + h - Dpi.S(12), x + w - gx, Dpi.S(4));
                Shapes.Fill(g, track, Dpi.S(2), Theme.Mix(T.Card, T.Divider, 0.8f));
                float p = (float)Math.Max(0, Math.Min(1, r.Percent.Value / 100));
                if (p > 0) Shapes.Fill(g, new RectangleF(track.X, track.Y, Math.Max(Dpi.S(4), track.Width * p), track.Height), Dpi.S(2), r.Level >= 2 ? T.ErrFg : T.Accent);
            }
            else if (next != null && !next.Header && !next.Percent.HasValue)
                using (Pen sep = new Pen(Theme.Mix(T.Card, T.Divider, 0.75f), 1f)) g.DrawLine(sep, gx, y + h - 1, x + w, y + h - 1);
        }

        void PaintCounts(Graphics g, int x, int y, int inner)
        {
            int cols = CountColumns(inner);
            int cw = inner / cols, ch = CountCellHeight;
            for (int k = 0; k < Counts.Count; k++)
            {
                CountCell c = Counts[k];
                Rectangle cell = new Rectangle(x + (k % cols) * cw, y + (k / cols) * ch, cw, ch);
                bool hot = c.Link && cell.Contains(mouse);
                int ty = cell.Y + Dpi.S(2);
                Draw.Text(g, c.Label, Fonts.Small, new Rectangle(cell.X, ty, cw - Dpi.S(8), Fonts.Small.Height + 2), T.Text3, Draw.Line);
                ty += Fonts.Small.Height + Dpi.S(4);
                Font vf = hot ? new Font(Fonts.Title, FontStyle.Underline) : Fonts.Title;
                try { Draw.Text(g, c.Value, T.Num(vf), new Rectangle(cell.X, ty, cw - Dpi.S(8), Fonts.Title.Height + 2), c.Link ? (hot ? T.AccentHover : T.Accent) : T.Text, Draw.Line); }
                finally { if (hot) vf.Dispose(); }
                ty += Fonts.Title.Height + Dpi.S(2);
                Draw.Text(g, c.Sub, Fonts.Small, new Rectangle(cell.X, ty, cw - Dpi.S(8), Fonts.Small.Height + 2), T.Text2, Draw.Line);
                if (c.Link) countHits.Add(new KeyValuePair<Rectangle, int>(cell, k));
            }
        }

        void PaintStats(Graphics g, int x, int y, int w)
        {
            int colW = Math.Min(Dpi.S(110), w / 3);
            for (int k = 0; k < Stats.Count; k++)
            {
                string[] s = Stats[k];
                bool head = k == 0;
                Rectangle row = new Rectangle(x, y, w, Dpi.S(26));
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
                if (k < Stats.Count - 1)
                    using (Pen sep = new Pen(Theme.Mix(T.Card, T.Divider, head ? 1f : 0.6f), 1f)) g.DrawLine(sep, x, row.Bottom - 1, x + w, row.Bottom - 1);
                y += row.Height;
            }
        }

        // What is under the pointer: a key for the fade and the pressed look.
        object HotAt(Point p)
        {
            foreach (KeyValuePair<Rectangle, TileRow> h in hits) if (h.Key.Contains(p)) return KeyOf(h.Value);
            foreach (KeyValuePair<Rectangle, int[]> h in statHits) if (h.Key.Contains(p)) return "s" + h.Value[0] + ":" + h.Value[1];
            foreach (KeyValuePair<Rectangle, int> h in countHits) if (h.Key.Contains(p)) return "c" + h.Value;
            return footerHit.Contains(p) ? "footer" : null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            object k = HotAt(e.Location);
            Cursor = k != null ? Cursors.Hand : Cursors.Default;
            fade.Set(k);
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouse = new Point(-1, -1);
            fade.Set(null);
            pressed = null;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = HotAt(e.Location); Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pressed != null) { pressed = null; Invalidate(); }
            base.OnMouseUp(e);
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
            fade.Set(null);
            pressed = null;
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
        public event EventHandler RefreshClicked, FilterChanged, ThisPc, AllRecent, ReportClicked;

        Theme theme = Theme.Light();
        public readonly OverviewFilter Filter = new OverviewFilter();
        readonly Panel col = new Panel();
        readonly OverviewHeadline headline = new OverviewHeadline();
        readonly KpiStrip kpi = new KpiStrip();
        readonly ToolbarFlow bar = new ToolbarFlow();
        readonly DropButton bCustomer = new DropButton("All customers");
        readonly Segmented side = SideFilterUi.Make(DeviceSides.Both);
        public readonly TextBox Search = new TextBox();
        readonly OverviewSearch searchField;
        readonly FlatButton bSearch = new FlatButton("Search", null);
        readonly FlatButton bRefresh = new FlatButton("Refresh", Glyphs2.Sync);
        readonly FlatButton bThisPc = new FlatButton("Follow this PC", Glyphs2.Device);
        string subText = "", statusText = "";
        readonly FlatButton bReport = new FlatButton("Report\u2026", Glyphs.Export);
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
            bar.WrapContents = true;
            bar.Padding = new Padding(Dpi.S(8), Dpi.S(8), Dpi.S(8), 0);
            bar.Height = Dpi.S(48);
            now.Mode = TileMode.Grid;
            attention.Mode = TileMode.Cards;
            period.SplitGroups = true;
            kpi.Visible = false;
            foreach (FlatButton b in new FlatButton[] { bSearch, bRefresh, bThisPc, bReport }) { b.Compact = true; b.AutoWidth(); }
            bCustomer.MinWidth = Dpi.S(160);
            bCustomer.Fit();
            searchField = new OverviewSearch(Search);
            searchField.Width = Dpi.S(300);
            Control[] items = { bCustomer, side, searchField, bSearch, bRefresh, bThisPc, bReport };
            foreach (Control c in items)
            {
                c.Margin = new Padding(0, 0, Dpi.S(8), Dpi.S(8));
                bar.Controls.Add(c);
            }
            // A little more room between the filters and the buttons that act.
            bSearch.Margin = new Padding(0, 0, Dpi.S(16), Dpi.S(8));
            bRefresh.Margin = new Padding(0, 0, Dpi.S(4), Dpi.S(8));
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
            bReport.Click += Safe.Wrap(delegate { if (ReportClicked != null) ReportClicked(this, EventArgs.Empty); });
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
            kpi.Clicked += delegate (int k) { Safe.Run(delegate { KpiClicked(k); }); };
            col.Controls.Add(headline);
            col.Controls.Add(bar);
            col.Controls.Add(kpi);
            foreach (OverviewTile t in Tiles) col.Controls.Add(t);
            Controls.Add(col);
            Resize += delegate { Relayout(); };
            Search.HandleCreated += delegate { try { Cue(Search, "Search devices: name, serial number, user, profile or task sequence"); } catch (Exception) { } };
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
            set
            {
                string v = (value ?? "").Trim();
                if (statusText == v) return;
                statusText = v;
                SetSub();
                Relayout();
            }
        }

        // The line beside the headline: the date, the filters and when the data was read.
        void SetSub()
        {
            headline.Sub = statusText.Length == 0 ? subText : subText + " \u00b7 " + statusText.TrimEnd('.');
            headline.Invalidate();
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
            Relayout();
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

        void KpiClicked(int k)
        {
            if (data == null) return;
            if (k == 0) List("now", "Deploying now", Devices(NowDevices()));
            else if (k == 1) Stat(1, false);
            else if (k == 2) Stat(2, true);
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
            now.Note = d.HasSources && d.Now.Count > 0 ? d.Now.Count + " deploying" : null;
            for (int k = 0; k < d.Now.Count && k < NowRows; k++)
            {
                NowRow n = d.Now[k];
                TileRow r = new TileRow();
                r.Text = n.Dev.D.Device;
                List<string> sub = new List<string>();
                if (!string.IsNullOrEmpty(n.Dev.Customer)) sub.Add(n.Dev.Customer);
                string who = Who.Short(n.Dev.S) ?? n.Dev.D.EnrollUser;
                if (!string.IsNullOrEmpty(who)) sub.Add(who);
                r.Sub = string.Join(" \u00b7 ", sub.ToArray());
                List<string> doing = new List<string>();
                if (!string.IsNullOrEmpty(n.Phase)) doing.Add(n.Phase);
                if (!string.IsNullOrEmpty(n.WaitsFor)) doing.Add(n.WaitsFor);
                r.Sub2 = string.Join(" \u00b7 ", doing.ToArray());
                r.HasState = true;
                r.State = n.Failed ? State.Failed : State.Installing;
                r.Percent = n.Percent;
                List<string> when = new List<string>();
                if (n.Failed) when.Add(n.Percent.HasValue ? "Failed at " + n.Percent.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %" : "Failed");
                else
                {
                    if (!string.IsNullOrEmpty(n.Left)) when.Add(n.Left);
                    if (n.FinishUtc.HasValue) when.Add("done " + n.FinishUtc.Value.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
                    if (when.Count == 0) when.Add("Running");
                }
                r.Right = string.Join(" \u00b7 ", when.ToArray());
                r.Level = n.Failed ? 2 : n.Slow ? 1 : 0;
                r.Tag = n.Dev;
                now.Rows.Add(r);
            }
            now.Footer = d.Now.Count > NowRows ? "Show all " + d.Now.Count : null;
            Empty(now, d.HasSources, nothing, "Now shows devices with the TrackVice agent and task sequences from SCCM while they deploy, with how far each is and what it waits for.",
                "Devices show here while they deploy, with how far each is and what it waits for.", emptyAction, emptyId);
            if (d.HasSources) now.EmptyTitle = "Nothing deploys right now";

            // The headline and the key figures
            int attn = 0, attnLevel = 0, failedNow = 0, slowNow = 0;
            foreach (AttentionTile t in d.Attention) if (t.Level >= 1) { attn++; attnLevel = Math.Max(attnLevel, t.Level); }
            foreach (NowRow n in d.Now) { if (n.Failed) failedNow++; else if (n.Slow) slowNow++; }
            headline.HasSources = d.HasSources;
            headline.Deploying = d.Now.Count;
            headline.Attention = attn;
            headline.AttentionLevel = attnLevel;
            subText = DateTime.Now.ToString("dddd d MMMM", System.Globalization.CultureInfo.CurrentCulture) + " \u00b7 " + (Filter.Customer ?? "All customers") + " \u00b7 " + (Filter.Side == DeviceSides.Intune ? "Intune" : Filter.Side == DeviceSides.Sccm ? "SCCM" : "Intune and SCCM");
            SetSub();
            kpi.Visible = d.HasSources;
            kpi.Cells.Clear();
            if (d.HasSources)
            {
                KpiCell c0 = new KpiCell();
                c0.Label = "Deploying now";
                c0.Value = N(d.Now.Count);
                c0.Link = d.Now.Count > 0;
                if (d.Now.Count == 0) c0.Delta = "Nothing running";
                else if (failedNow > 0) { c0.Delta = failedNow + " failed"; c0.Level = 3; }
                else if (slowNow > 0) { c0.Delta = slowNow + " slower than usual"; c0.Level = 2; }
                else { c0.Delta = "All on track"; c0.Level = 1; }
                kpi.Cells.Add(c0);
                KpiCell c1 = new KpiCell();
                c1.Label = "Finished today";
                c1.Value = N(d.Today.Finished);
                c1.Link = d.Today.Finished > 0;
                double perDay = d.Week.Finished / 7.0;
                string avg = d.Week.Finished == 0 ? "none in 7 days" : "7-day average " + perDay.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                c1.Delta = d.Today.Failed > 0 ? d.Today.Failed + " failed \u00b7 " + avg : avg;
                c1.Level = d.Today.Failed > 0 ? 3 : 0;
                kpi.Cells.Add(c1);
                KpiCell c2 = new KpiCell();
                c2.Label = "Success rate, 7 days";
                c2.Value = OverviewModel.Rate(d.Week.SuccessRate);
                c2.Ring = d.Week.SuccessRate;
                c2.Link = d.Week.Failed > 0;
                if (d.Today.SuccessRate.HasValue && d.Week.SuccessRate.HasValue)
                {
                    int diff = (int)Math.Round(d.Today.SuccessRate.Value - d.Week.SuccessRate.Value);
                    c2.Delta = "Today " + OverviewModel.Rate(d.Today.SuccessRate) + " (" + (diff >= 0 ? "+" : "\u2212") + Math.Abs(diff) + " pts)";
                    c2.Level = diff >= 3 ? 1 : diff <= -10 ? 2 : 0;
                }
                else c2.Delta = d.Week.Finished == 0 ? "Nothing finished yet" : "None finished today";
                kpi.Cells.Add(c2);
                KpiCell c3 = new KpiCell();
                c3.Label = "Median time, 7 days";
                c3.Value = OverviewModel.Time(d.Week.MedianSeconds);
                if (d.Today.MedianSeconds > 0 && d.Week.MedianSeconds > 0)
                {
                    c3.Delta = "Today " + OverviewModel.Time(d.Today.MedianSeconds);
                    double ratio = d.Today.MedianSeconds / d.Week.MedianSeconds;
                    c3.Level = ratio >= 1.2 ? 2 : ratio <= 0.85 ? 1 : 0;
                }
                else c3.Delta = d.Week.MedianSeconds > 0 ? "None finished today" : "Nothing finished yet";
                kpi.Cells.Add(c3);
            }
            kpi.Invalidate();

            // Needs attention
            attention.Rows.Clear();
            foreach (AttentionTile t in d.Attention)
            {
                TileRow r = new TileRow();
                r.Text = t.Title;
                r.Sub = t.Text;
                r.Right = t.Count > 0 ? t.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                r.Link = !string.IsNullOrEmpty(t.ActionText) ? t.ActionText : t.Open != null ? "Open" : "Show devices";
                r.Level = t.Level;
                r.Tag = t;
                attention.Rows.Add(r);
            }
            attention.Calm = false;
            Empty(attention, d.HasSources, "Nothing to check yet", "Failed devices, devices with no progress, agents that stopped sending and findings worth acting on show here.",
                "Failed devices, stalled agents and findings show up here when they need you.", emptyAction, emptyId);
            if (d.HasSources)
            {
                attention.EmptyTitle = "Nothing needs you";
                attention.Calm = true;
            }
            attention.Note = d.HasSources && d.Attention.Count > 0 ? d.Attention.Count + (d.Attention.Count == 1 ? " item" : " items") : null;

            // Today and the last 7 days
            period.Rows.Clear();
            period.Stats.Clear();
            period.Days.Clear();
            if (d.HasSources)
            {
                period.Stats.Add(new string[] { "", "Today", "Last 7 days" });
                period.Stats.Add(new string[] { "Finished", N(d.Today.Finished), N(d.Week.Finished), "link" });
                period.Stats.Add(new string[] { "Failed", N(d.Today.Failed), N(d.Week.Failed), "link", "err" });
                period.Stats.Add(new string[] { "Success rate", OverviewModel.Rate(d.Today.SuccessRate), OverviewModel.Rate(d.Week.SuccessRate) });
                period.Stats.Add(new string[] { "Average time", OverviewModel.Time(d.Today.AvgSeconds), OverviewModel.Time(d.Week.AvgSeconds) });
                period.DaysTitle = "Finished per day";
                period.Days.AddRange(OverviewDays.Build(d.Week.FinishedDevices, nowUtc.ToLocalTime().Date, System.Globalization.CultureInfo.CurrentCulture));
                double maxAvg = 1, maxFail = 1;
                foreach (StepStat s in d.Slowest) maxAvg = Math.Max(maxAvg, s.AvgSeconds);
                foreach (StepStat s in d.FailingMost) maxFail = Math.Max(maxFail, s.Failed);
                if (d.Slowest.Count > 0) period.Rows.Add(Head("Slowest steps"));
                int rank = 0;
                foreach (StepStat s in d.Slowest)
                {
                    TileRow r = new TileRow();
                    r.Rank = ++rank;
                    r.Percent = s.AvgSeconds / maxAvg * 100;
                    r.Text = s.Name;
                    r.Sub = s.Kind + " · on " + s.Devices + " device" + (s.Devices == 1 ? "" : "s");
                    r.Right = "avg " + OverviewModel.Time(s.AvgSeconds);
                    r.Tag = s;
                    period.Rows.Add(r);
                }
                if (d.FailingMost.Count > 0) period.Rows.Add(Head("Fail most"));
                rank = 0;
                foreach (StepStat s in d.FailingMost)
                {
                    TileRow r = new TileRow();
                    r.Rank = ++rank;
                    r.Percent = s.Failed / maxFail * 100;
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
                    r.Quiet = last.Replaced || (last.D.StateText ?? "").StartsWith(OverviewModel.ReplacedText, StringComparison.Ordinal);
                    sub.Add(r.Quiet ? "replaced by a newer deployment" : "last result: " + States.Label(last.D.State).ToLowerInvariant());
                    sub.Add(Txt.Ago(OverviewCounts.StartOf(last), nowUtc));
                    r.Sub = string.Join(" \u00b7 ", sub.ToArray());
                    r.HasState = true;
                    r.State = last.D.State;
                    r.Right = OverviewCounts.Times(rd.Times);
                    r.Level = last.D.State == State.Failed && !r.Quiet ? 2 : 0;
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
                r.Quiet = (f.StateText ?? "").StartsWith(OverviewModel.ReplacedText, StringComparison.Ordinal);
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
            kpi.ClearHover();
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

        // The headline, the toolbar and the key figures on top, Now across the page, then two columns when there is
        // room: what needs attention and the last days on the left, the deployments and the recent devices on the right.
        // Each column is as long as its sections: no card is stretched to fill.
        void Lay()
        {
            int pad = Dpi.S(24);
            int avail = Width - SystemInformation.VerticalScrollBarWidth;
            int w = Math.Min(Dpi.S(1280), Math.Max(Dpi.S(320), avail - pad * 2));
            int gap = Dpi.S(24), section = Dpi.S(32);
            bool two = NarrowFit.TwoColumns(w, Dpi.S(400), gap, 56);
            int left = two ? (w - gap) * 56 / 100 : w;
            int right = two ? w - left - gap : w;
            int y = 0;
            col.SuspendLayout();
            int hh = headline.Measure(w);
            headline.SetBounds(0, y, w, hh);
            y += hh + Dpi.S(16);
            FitSearch(w);
            int bh = Math.Max(Dpi.S(48), bar.GetPreferredSize(new Size(w, 0)).Height);
            bar.SetBounds(0, y, w, bh);
            y += bh + Dpi.S(24);
            if (kpi.Visible)
            {
                int kh = kpi.Measure(w);
                kpi.SetBounds(0, y, w, kh);
                y += kh + section;
            }
            if (setup.Visible)
            {
                int sh = setup.Measure(w);
                setup.SetBounds(0, y, w, sh);
                y += sh + section;
            }
            int nh = now.Measure(w);
            now.SetBounds(0, y, w, nh);
            y += nh + section;
            if (two)
            {
                int ly = y, ry = y;
                foreach (OverviewTile t in new OverviewTile[] { attention, period })
                {
                    int th = t.Measure(left);
                    t.SetBounds(0, ly, left, th);
                    ly += th + section;
                }
                foreach (OverviewTile t in new OverviewTile[] { deploy, recent })
                {
                    if (!t.Visible) continue;
                    int th = t.Measure(right);
                    t.SetBounds(left + gap, ry, right, th);
                    ry += th + section;
                }
                y = Math.Max(ly, ry) - section;
            }
            else
            {
                foreach (OverviewTile t in new OverviewTile[] { attention, deploy, period, recent })
                {
                    if (!t.Visible) continue;
                    int th = t.Measure(w);
                    t.SetBounds(0, y, w, th);
                    y += th + section;
                }
                y -= section;
            }
            col.ResumeLayout(true);
            col.SetBounds(Math.Max(pad, (avail - w) / 2), Dpi.S(24) + AutoScrollPosition.Y, w, y + Dpi.S(32));
            foreach (OverviewTile t in Tiles) t.PerformLayout();
        }

        // The search field takes the room the other controls of the toolbar leave, so the toolbar stays one row for as
        // long as it can. On a narrow window the field shares a row with the filters or gets a row of its own.
        void FitSearch(int w)
        {
            int inner = w - bar.Padding.Horizontal - Dpi.S(2);
            int others = 0;
            foreach (Control c in bar.Controls)
                if (c != searchField && c.Visible) others += c.Width + c.Margin.Horizontal;
            int min = Dpi.S(180), max = Dpi.S(360), m = searchField.Margin.Horizontal;
            int room = inner - others - m;
            if (room < min) room = inner - bCustomer.Width - bCustomer.Margin.Horizontal - side.Width - side.Margin.Horizontal - m;
            if (room < min) room = inner - m;
            searchField.Width = Math.Max(Dpi.S(120), Math.Min(max, room));
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            col.BackColor = t.Bg;
            bar.BackColor = t.Card;
            headline.ApplyTheme(t);
            kpi.ApplyTheme(t);
            bar.Th = t;
            bCustomer.ApplyTheme(t);
            foreach (FlatButton b in new FlatButton[] { bSearch, bRefresh, bThisPc, bReport }) b.ApplyTheme(t);
            side.ApplyTheme(t);
            searchField.ApplyTheme(t);
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
