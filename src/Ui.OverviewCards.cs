namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    // The type scale of Overview: about 30, 22, 15, 13 and 11 px. The 13 and 11 px sizes are the fonts of the app.
    public static class OvFonts
    {
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        static Font Get(FontFamily f, float pts, FontStyle st)
        {
            string key = f.Name + "|" + pts.ToString(CultureInfo.InvariantCulture) + "|" + (int)st;
            Font r;
            if (!cache.TryGetValue(key, out r))
            {
                r = new Font(f, pts, st);
                cache[key] = r;
            }
            return r;
        }

        static Font Display(float pts)
        {
            return Get(Fonts.Title.FontFamily, pts, Fonts.Title.Style);
        }

        // 30 px: the headline.
        public static Font Headline
        {
            get { return Display(22.5f); }
        }

        // 22 px: the numbers of the key figures.
        public static Font Kpi(Theme t)
        {
            return t != null && t.MonoNumbers ? Get(Fonts.Mono.FontFamily, 16.5f, FontStyle.Bold) : Display(16.5f);
        }

        // 15 px: the title of a section and the name of a device.
        public static Font Section
        {
            get { return Get(Fonts.UiSemibold.FontFamily, 11.25f, Fonts.UiSemibold.Style); }
        }
    }

    // How the rows of a tile are drawn: as a list in a card, as device cards in a grid, or as one card per item.
    public enum TileMode { List, Grid, Cards }

    // Fades the hover look of one item in, short and ease-out, so the page feels calm. Off when Windows shows no
    // animations: the hover look is then there at once.
    public sealed class HoverFade
    {
        const double Ms = 160;
        readonly Timer timer = new Timer();
        object key;
        DateTime start;

        public HoverFade(Control owner)
        {
            Control c = owner;
            timer.Interval = 15;
            timer.Tick += delegate
            {
                if (Progress >= 1) timer.Stop();
                c.Invalidate();
            };
            owner.Disposed += delegate { timer.Dispose(); };
        }

        public object Key
        {
            get { return key; }
        }

        public void Set(object k)
        {
            if (Equals(k, key)) return;
            key = k;
            start = DateTime.UtcNow;
            if (k != null && Fx.On) timer.Start();
            else timer.Stop();
        }

        // 0 when k is not under the pointer, rising to 1.
        public float Of(object k)
        {
            if (k == null || key == null || !Equals(k, key)) return 0f;
            double p = 1 - Progress;
            return (float)(1 - p * p * p);
        }

        double Progress
        {
            get
            {
                if (!Fx.On) return 1;
                return Math.Max(0, Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / Ms));
            }
        }
    }

    public sealed partial class OverviewTile
    {
        // ---------- the grid of device cards ----------

        static int CardGap
        {
            get { return Dpi.S(16); }
        }

        static int CardH
        {
            get { return Dpi.S(104); }
        }

        // Three cards side by side on a wide window, two on a medium one and one when narrow: each gets about 360 px.
        static int GridCols(int width)
        {
            int min = Dpi.S(360);
            if (width >= min * 3 + CardGap * 2) return 3;
            if (width >= min * 2 + CardGap) return 2;
            return 1;
        }

        int GridHeight(int width)
        {
            int n = Rows.Count;
            if (n == 0) return 0;
            int cols = GridCols(width);
            int rows = (n + cols - 1) / cols;
            return rows * CardH + (rows - 1) * CardGap;
        }

        int PaintGrid(Graphics g, int y)
        {
            int cols = GridCols(Width);
            int cw = (Width - CardGap * (cols - 1)) / cols;
            for (int k = 0; k < Rows.Count; k++)
            {
                int col = k % cols;
                // The last column takes what the division leaves, so the grid ends where the page does.
                int x = col * (cw + CardGap), w = col == cols - 1 ? Width - x : cw;
                Rectangle rect = new Rectangle(x, y + (k / cols) * (CardH + CardGap), w, CardH);
                PaintDevice(g, rect, Rows[k]);
                hits.Add(new KeyValuePair<Rectangle, TileRow>(rect, Rows[k]));
            }
            return y + GridHeight(Width);
        }

        // A device deploying now: a ring with how far it is, the name, the customer and user, what it does now and when it
        // will be done. Amber when it takes longer than usual, red when it failed.
        void PaintDevice(Graphics g, Rectangle rect, TileRow r)
        {
            string key = KeyOf(r);
            float a = fade.Of(key);
            Color edge = r.Level >= 2 ? T.ErrFg : r.Level == 1 ? T.WarnFg : T.Divider;
            Color fill0 = r.Level >= 1 ? Theme.Mix(T.Card, edge, T.IsDark ? 0.10f : 0.06f) : T.Card;
            Color fill = Equals(pressed, key) ? T.Pressed : Theme.Mix(fill0, T.Hover, a);
            float sw = r.Level >= 1 ? 1.5f : 1f;
            RectangleF rf = new RectangleF(rect.X + sw / 2, rect.Y + sw / 2, rect.Width - sw - 0.5f, rect.Height - sw - 0.5f);
            Shapes.Fill(g, rf, Dpi.S(12), fill);
            Shapes.Stroke(g, rf, Dpi.S(12), r.Level >= 1 ? edge : Theme.Mix(T.Divider, Theme.Mix(T.Divider, T.Accent, 0.6f), a), sw);

            int d = Dpi.S(56);
            Color ring = r.Level >= 2 ? T.ErrFg : r.Level == 1 ? T.WarnFg : T.Accent;
            RectangleF box = new RectangleF(rect.X + Dpi.S(18), rect.Y + (rect.Height - d) / 2f, d, d);
            Ring(g, box, r.Percent, ring, Theme.Mix(fill, ring, T.IsDark ? 0.22f : 0.16f), Dpi.S(5));
            string pct = r.Percent.HasValue ? Math.Round(r.Percent.Value).ToString("0", CultureInfo.InvariantCulture) + "%" : "…";
            Draw.Text(g, pct, T.Num(Fonts.UiSemibold), Rectangle.Round(box), r.Percent.HasValue ? (r.Level >= 2 ? T.ErrFg : T.Text) : T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            // The lines beside the ring, centred as a block.
            List<string> text = new List<string>();
            List<Font> fonts = new List<Font>();
            List<Color> colors = new List<Color>();
            if (!string.IsNullOrEmpty(r.Sub)) { text.Add(r.Sub); fonts.Add(Fonts.Small); colors.Add(T.Text3); }
            if (!string.IsNullOrEmpty(r.Sub2)) { text.Add(r.Sub2); fonts.Add(Fonts.Small); colors.Add(T.Text2); }
            if (!string.IsNullOrEmpty(r.Right)) { text.Add(r.Right); fonts.Add(T.Num(Fonts.SmallSemibold)); colors.Add(r.Level >= 2 ? T.ErrFg : r.Level == 1 ? T.WarnFg : T.Text); }
            int lh = Fonts.Small.Height + Dpi.S(2);
            int block = OvFonts.Section.Height + Dpi.S(4) + text.Count * lh;
            int tx = (int)box.Right + Dpi.S(16);
            int tw = rect.Right - Dpi.S(16) - tx;
            int y = rect.Y + (rect.Height - block) / 2;
            Draw.Text(g, r.Text, OvFonts.Section, new Rectangle(tx, y, tw, OvFonts.Section.Height + 2), T.Text, Draw.Line);
            y += OvFonts.Section.Height + Dpi.S(4);
            for (int i = 0; i < text.Count; i++)
            {
                Draw.Text(g, text[i], fonts[i], new Rectangle(tx, y, tw, lh), colors[i], Draw.Line);
                y += lh;
            }
        }

        // A ring that shows how far something is. Without a number, a short arc.
        public static void Ring(Graphics g, RectangleF box, double? percent, Color c, Color track, float w)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(box.X + w / 2, box.Y + w / 2, box.Width - w, box.Height - w);
            using (Pen t = new Pen(track, w)) g.DrawEllipse(t, r);
            float sweep = percent.HasValue ? (float)(Math.Max(0, Math.Min(100, percent.Value)) * 3.6) : 70f;
            if (sweep >= 359.5f) sweep = 359.5f;
            if (sweep > 0)
                using (Pen p = new Pen(c, w))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawArc(p, r, -90, Math.Max(1f, sweep));
                }
            g.SmoothingMode = old;
        }

        // ---------- one card per item ----------

        int ItemTextWidth(int width)
        {
            return Math.Max(Dpi.S(60), width - Dpi.S(16) - Dpi.S(38));
        }

        int ItemHeight(TileRow r, int width)
        {
            int h = Dpi.S(14) + Fonts.UiSemibold.Height + Dpi.S(3) + Draw.WrapHeight(r.Sub, Fonts.Small, ItemTextWidth(width));
            if (!string.IsNullOrEmpty(r.Link)) h += Dpi.S(8) + Fonts.Ui.Height;
            return h + Dpi.S(14);
        }

        static int ItemGap
        {
            get { return Dpi.S(8); }
        }

        int CardsHeight(int width)
        {
            int h = 0;
            for (int k = 0; k < Rows.Count; k++) h += ItemHeight(Rows[k], width) + (k < Rows.Count - 1 ? ItemGap : 0);
            return h;
        }

        // Something that needs attention: a dot in the colour of how urgent it is, the title, the text, the count and
        // what to do about it.
        int PaintCards(Graphics g, int y)
        {
            for (int k = 0; k < Rows.Count; k++)
            {
                TileRow r = Rows[k];
                string key = KeyOf(r);
                float a = fade.Of(key);
                int h = ItemHeight(r, Width);
                Rectangle rect = new Rectangle(0, y, Width, h);
                Color lv = r.Level >= 2 ? T.ErrFg : r.Level == 1 ? T.WarnFg : T.Text3;
                Color fill = Equals(pressed, key) ? T.Pressed : Theme.Mix(T.Card, T.Hover, a);
                RectangleF rf = new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1.5f, rect.Height - 1.5f);
                Shapes.Fill(g, rf, Dpi.S(12), fill);
                Shapes.Stroke(g, rf, Dpi.S(12), Theme.Mix(T.Divider, Theme.Mix(T.Divider, T.Accent, 0.6f), a), 1f);
                int x = Dpi.S(18), tx = Dpi.S(38), tw = Width - Dpi.S(16) - tx;
                int ty = y + Dpi.S(14);
                float dot = Dpi.S(10);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Theme.Mix(fill, lv, T.IsDark ? 0.25f : 0.18f))) g.FillEllipse(b, x - Dpi.S(3), ty + (Fonts.UiSemibold.Height - dot) / 2f - Dpi.S(3), dot + Dpi.S(6), dot + Dpi.S(6));
                using (SolidBrush b = new SolidBrush(lv)) g.FillEllipse(b, x, ty + (Fonts.UiSemibold.Height - dot) / 2f, dot, dot);
                int pw = 0;
                if (!string.IsNullOrEmpty(r.Right))
                {
                    Font pf = T.Num(Fonts.SmallSemibold);
                    pw = Math.Max(Dpi.S(26), Draw.Measure(r.Right, pf).Width + Dpi.S(16));
                    Rectangle pill = new Rectangle(Width - Dpi.S(16) - pw, ty + (Fonts.UiSemibold.Height - Dpi.S(20)) / 2, pw, Dpi.S(20));
                    Shapes.Fill(g, pill, Dpi.S(10), Theme.Mix(fill, lv, T.IsDark ? 0.22f : 0.13f));
                    Draw.Text(g, r.Right, pf, pill, r.Level == 0 ? T.Text2 : lv, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    pw += Dpi.S(10);
                }
                Draw.Text(g, r.Text, Fonts.UiSemibold, new Rectangle(tx, ty, tw - pw, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
                int sy = ty + Fonts.UiSemibold.Height + Dpi.S(3);
                int sh = Draw.WrapHeight(r.Sub, Fonts.Small, ItemTextWidth(Width));
                Draw.Text(g, r.Sub, Fonts.Small, new Rectangle(tx, sy, ItemTextWidth(Width), sh + 2), T.Text2, Draw.Wrap);
                if (!string.IsNullOrEmpty(r.Link))
                {
                    string link = r.Link + "  ›";
                    Rectangle lr = new Rectangle(tx, sy + sh + Dpi.S(8), Math.Min(tw, Draw.Measure(link, Fonts.Ui).Width + 2), Fonts.Ui.Height + 2);
                    LinkText(g, link, lr, a > 0);
                }
                hits.Add(new KeyValuePair<Rectangle, TileRow>(rect, r));
                y += h + ItemGap;
            }
            return Rows.Count > 0 ? y - ItemGap : y;
        }

        // ---------- the chart of the last days ----------

        static int ChartBars
        {
            get { return Dpi.S(64); }
        }

        static int CaptionH
        {
            get { return Dpi.S(18); }
        }

        int DaysHeight
        {
            get { return Days.Count == 0 ? 0 : CaptionH + Dpi.S(12) + Fonts.Small.Height + Dpi.S(2) + ChartBars + Dpi.S(6) + Fonts.Small.Height + Dpi.S(2); }
        }

        // Done and failed deployments per day, as stacked bars with the total above and the day under each bar. No axis:
        // the numbers say it. Today is drawn full and the days before a little softer.
        void PaintDays(Graphics g, int x, int y, int w)
        {
            if (Days.Count == 0) return;
            int fw = Draw.Measure("Failed", Fonts.Small).Width + Dpi.S(16);
            int dw = Draw.Measure("Done", Fonts.Small).Width + Dpi.S(16);
            Draw.Text(g, DaysTitle, Fonts.SmallSemibold, new Rectangle(x, y, w - fw - dw - Dpi.S(24), CaptionH), T.Text3, Draw.Line);
            Key(g, x + w - fw, y, "Failed", T.ErrFg);
            Key(g, x + w - fw - dw - Dpi.S(14), y, "Done", T.OkFg);
            int max = 1;
            foreach (DayBar d in Days) max = Math.Max(max, d.Total);
            int slot = w / Days.Count;
            int barW = Math.Max(Dpi.S(8), Math.Min(Dpi.S(30), slot * 56 / 100));
            int baseY = y + CaptionH + Dpi.S(12) + Fonts.Small.Height + Dpi.S(2) + ChartBars;
            TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            for (int i = 0; i < Days.Count; i++)
            {
                DayBar d = Days[i];
                int cx = x + i * slot + slot / 2;
                if (d.Total == 0)
                    Shapes.Fill(g, new RectangleF(cx - barW / 2f, baseY - Dpi.S(3), barW, Dpi.S(3)), Dpi.S(1.5f), T.Divider);
                else
                {
                    int h = Math.Max(Dpi.S(6), (int)Math.Round(ChartBars * (double)d.Total / max));
                    float soft = d.Today ? 0f : T.IsDark ? 0.28f : 0.22f;
                    Color ok = Theme.Mix(T.OkFg, T.Card, soft), err = Theme.Mix(T.ErrFg, T.Card, soft);
                    RectangleF bar = new RectangleF(cx - barW / 2f, baseY - h, barW, h);
                    using (GraphicsPath p = TopRounded(bar, Dpi.S(4)))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        int doneH = d.Failed == 0 ? h : d.Done == 0 ? 0 : Math.Max(Dpi.S(2), Math.Min(h - Dpi.S(3), (int)Math.Round(h * (double)d.Done / d.Total)));
                        Region old = g.Clip;
                        try
                        {
                            if (doneH > 0)
                            {
                                g.SetClip(new RectangleF(bar.X - 1, baseY - doneH, bar.Width + 2, doneH + 1));
                                using (SolidBrush b = new SolidBrush(ok)) g.FillPath(b, p);
                                g.Clip = old;
                            }
                            if (doneH < h)
                            {
                                // A hairline of the card between the two parts.
                                int gap = doneH > 0 ? Math.Max(1, Dpi.S(1.5f)) : 0;
                                g.SetClip(new RectangleF(bar.X - 1, bar.Y - 1, bar.Width + 2, h - doneH - gap + 1));
                                using (SolidBrush b = new SolidBrush(err)) g.FillPath(b, p);
                                g.Clip = old;
                            }
                        }
                        finally { g.Clip = old; }
                    }
                    Draw.Text(g, d.Total.ToString(CultureInfo.InvariantCulture), T.Num(d.Today ? Fonts.SmallSemibold : Fonts.Small), new Rectangle(cx - slot / 2, baseY - h - Fonts.Small.Height - Dpi.S(3), slot, Fonts.Small.Height + 1), d.Today ? T.Text : T.Text2, centre);
                }
                Draw.Text(g, d.Label, d.Today ? Fonts.SmallSemibold : Fonts.Small, new Rectangle(cx - slot / 2, baseY + Dpi.S(6), slot, Fonts.Small.Height + 2), d.Today ? T.Text : T.Text3, centre);
            }
        }

        // A bar with round corners on top and a flat foot.
        static GraphicsPath TopRounded(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2 * Theme.CornerScale, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + d / 2, r.Right, r.Bottom);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }

        void Key(Graphics g, int x, int y, string text, Color c)
        {
            Shapes.Fill(g, new RectangleF(x, y + (CaptionH - Dpi.S(8)) / 2f, Dpi.S(8), Dpi.S(8)), Dpi.S(2), c);
            Draw.Text(g, text, Fonts.Small, new Rectangle(x + Dpi.S(14), y, Dpi.S(60), CaptionH), T.Text3, Draw.Line);
        }

        // ---------- the check of a calm empty state ----------

        void PaintCheck(Graphics g, int x, int y, int d)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Theme.Mix(T.Card, T.OkFg, T.IsDark ? 0.20f : 0.13f))) g.FillEllipse(b, x, y, d, d);
            using (Pen p = new Pen(T.OkFg, Math.Max(1.5f, d / 12f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[] { new PointF(x + d * 0.30f, y + d * 0.52f), new PointF(x + d * 0.44f, y + d * 0.66f), new PointF(x + d * 0.71f, y + d * 0.37f) });
            }
        }
    }

    // The headline of Overview: the answer first, such as "6 deploying - 2 need attention", with the date and the
    // filters beside it, or under it when the window is narrow.
    public sealed class OverviewHeadline : FlatControl
    {
        public bool HasSources;
        public int Deploying, Attention, AttentionLevel;
        public string Sub = "";

        List<KeyValuePair<string, Color>> Parts()
        {
            List<KeyValuePair<string, Color>> l = new List<KeyValuePair<string, Color>>();
            if (!HasSources)
            {
                l.Add(new KeyValuePair<string, Color>("Overview", T.Text));
                return l;
            }
            l.Add(new KeyValuePair<string, Color>(Deploying == 0 ? "Nothing deploying" : Deploying.ToString(CultureInfo.InvariantCulture) + " deploying", T.Text));
            l.Add(new KeyValuePair<string, Color>("·", T.Text3));
            if (Attention == 0) l.Add(new KeyValuePair<string, Color>("nothing needs you", T.Text3));
            else l.Add(new KeyValuePair<string, Color>(Attention.ToString(CultureInfo.InvariantCulture) + (Attention == 1 ? " needs attention" : " need attention"), AttentionLevel >= 2 ? T.ErrFg : T.WarnFg));
            return l;
        }

        static int DotPad
        {
            get { return Dpi.S(12); }
        }

        int TextWidth(Font f)
        {
            int w = 0;
            foreach (KeyValuePair<string, Color> p in Parts()) w += Draw.Measure(p.Key, f).Width + (p.Key == "·" ? DotPad * 2 : 0);
            return w;
        }

        bool Beside(int width)
        {
            if (string.IsNullOrEmpty(Sub)) return true;
            return TextWidth(OvFonts.Headline) + Dpi.S(32) + Draw.Measure(Sub, Fonts.Ui).Width <= width;
        }

        public int Measure(int width)
        {
            int h = OvFonts.Headline.Height + Dpi.S(2);
            if (!Beside(width)) h += Fonts.Ui.Height + Dpi.S(4);
            return h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Font f = OvFonts.Headline;
            int x = 0;
            foreach (KeyValuePair<string, Color> p in Parts())
            {
                int w = Draw.Measure(p.Key, f).Width;
                bool dot = p.Key == "·";
                if (dot) x += DotPad;
                Draw.Text(g, p.Key, f, new Rectangle(x, 0, w + Dpi.S(8), f.Height + 2), p.Value, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                x += w;
                if (dot) x += DotPad;
            }
            if (string.IsNullOrEmpty(Sub)) return;
            if (Beside(Width))
            {
                // On the baseline of the headline.
                int sw = Draw.Measure(Sub, Fonts.Ui).Width + 2;
                Draw.Text(g, Sub, Fonts.Ui, new Rectangle(Width - sw, f.Height - Fonts.Ui.Height - Dpi.S(4), sw, Fonts.Ui.Height + 2), T.Text3, Draw.Line | TextFormatFlags.Right);
            }
            else Draw.Text(g, Sub, Fonts.Ui, new Rectangle(0, f.Height + Dpi.S(4), Width, Fonts.Ui.Height + 2), T.Text3, Draw.Line);
        }
    }

    // One key figure: a label, a big number and a small line that compares it.
    public sealed class KpiCell
    {
        public string Label, Value, Delta;
        // The colour of the small line: 0 quiet, 1 good, 2 warning, 3 bad.
        public int Level;
        public bool Link;
        // A share from 0 to 100 drawn as a small ring beside the number, such as the success rate: done in the colour of
        // done, the rest in a soft red. Null for none.
        public double? Ring;
    }

    // The row of key figures under the toolbar: four cards, two by two when the window is narrow.
    public sealed class KpiStrip : FlatControl
    {
        public readonly List<KpiCell> Cells = new List<KpiCell>();
        public event Action<int> Clicked;
        readonly HoverFade fade;
        int pressedCell = -1;

        public KpiStrip()
        {
            fade = new HoverFade(this);
        }

        static int Gap
        {
            get { return Dpi.S(16); }
        }

        int CellH
        {
            get { return Dpi.S(16) + Fonts.Small.Height + Dpi.S(6) + OvFonts.Kpi(T).Height + Dpi.S(4) + Fonts.Small.Height + Dpi.S(16); }
        }

        int Cols(int width)
        {
            int n = Math.Max(1, Cells.Count);
            if ((width - Gap * (n - 1)) / n >= Dpi.S(200)) return n;
            return Math.Min(n, 2);
        }

        public int Measure(int width)
        {
            if (Cells.Count == 0) return 0;
            int cols = Cols(width);
            int rows = (Cells.Count + cols - 1) / cols;
            return rows * CellH + (rows - 1) * Gap;
        }

        Rectangle CellRect(int k)
        {
            int cols = Cols(Width);
            int cw = (Width - Gap * (cols - 1)) / cols;
            int col = k % cols, x = col * (cw + Gap);
            return new Rectangle(x, (k / cols) * (CellH + Gap), col == cols - 1 ? Width - x : cw, CellH);
        }

        Color DeltaColor(int level)
        {
            return level == 1 ? T.OkFg : level == 2 ? T.WarnFg : level >= 3 ? T.ErrFg : T.Text3;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            for (int k = 0; k < Cells.Count; k++)
            {
                KpiCell c = Cells[k];
                Rectangle r = CellRect(k);
                float a = c.Link ? fade.Of(k) : 0f;
                Color fill = c.Link && pressedCell == k ? T.Pressed : Theme.Mix(T.Card, T.Hover, a);
                RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f);
                Shapes.Fill(g, rf, Dpi.S(12), fill);
                Shapes.Stroke(g, rf, Dpi.S(12), Theme.Mix(T.Divider, Theme.Mix(T.Divider, T.Accent, 0.6f), a), 1f);
                int x = r.X + Dpi.S(18), w = r.Width - Dpi.S(36);
                if (c.Ring.HasValue)
                {
                    int d = Dpi.S(44);
                    RectangleF box = new RectangleF(r.Right - Dpi.S(18) - d, r.Y + (r.Height - d) / 2f, d, d);
                    OverviewTile.Ring(g, box, c.Ring.Value, T.OkFg, Theme.Mix(fill, T.ErrFg, T.IsDark ? 0.45f : 0.35f), Dpi.S(5));
                    w -= d + Dpi.S(12);
                }
                int y = r.Y + Dpi.S(16);
                Draw.Text(g, c.Label, Fonts.Small, new Rectangle(x, y, w, Fonts.Small.Height + 2), T.Text3, Draw.Line);
                y += Fonts.Small.Height + Dpi.S(6);
                Font vf = OvFonts.Kpi(T);
                Draw.Text(g, c.Value, vf, new Rectangle(x, y, w, vf.Height + 2), T.Text, Draw.Line);
                y += vf.Height + Dpi.S(4);
                Draw.Text(g, c.Delta, Fonts.Small, new Rectangle(x, y, w, Fonts.Small.Height + 2), DeltaColor(c.Level), Draw.Line);
            }
        }

        int At(Point p)
        {
            for (int k = 0; k < Cells.Count; k++) if (Cells[k].Link && CellRect(k).Contains(p)) return k;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int k = At(e.Location);
            Cursor = k >= 0 ? Cursors.Hand : Cursors.Default;
            fade.Set(k >= 0 ? (object)k : null);
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            fade.Set(null);
            pressedCell = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressedCell = At(e.Location); Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressedCell = -1;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int k = e.Button == MouseButtons.Left ? At(e.Location) : -1;
            if (k >= 0 && Clicked != null) Clicked(k);
        }

        public void ClearHover()
        {
            fade.Set(null);
            pressedCell = -1;
        }
    }

    // The toolbar of Overview: one rounded strip that holds the filters and the buttons, and wraps when narrow.
    public sealed class ToolbarFlow : FlowLayoutPanel
    {
        public Theme Th = Theme.Light();

        public ToolbarFlow()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Th.Bg);
            RectangleF rf = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, rf, Dpi.S(12), Th.Card);
            Shapes.Stroke(g, rf, Dpi.S(12), Th.Divider, 1f);
        }
    }

    // A rounded search field around a text box without its own border, with a focus ring in the accent colour.
    public sealed class OverviewSearch : Panel, IThemed
    {
        public readonly TextBox Box;
        Theme th = Theme.Light();
        bool hot;

        public OverviewSearch(TextBox box)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Box = box;
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Fonts.Ui;
            Height = Dpi.S(32);
            Cursor = Cursors.IBeam;
            Controls.Add(Box);
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.MouseEnter += delegate { hot = true; Invalidate(); };
            Box.MouseLeave += delegate { hot = ClientRectangle.Contains(PointToClient(MousePosition)); Invalidate(); };
        }

        int Lead
        {
            get { return Fonts.Icons != null ? Dpi.S(34) : Dpi.S(12); }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int h = Box.PreferredHeight;
            Box.SetBounds(Lead, Math.Max(0, (Height - h) / 2), Math.Max(Dpi.S(40), Width - Lead - Dpi.S(12)), h);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
        }

        public void ApplyTheme(Theme t)
        {
            th = t;
            Box.BackColor = t.Field;
            Box.ForeColor = t.Text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : th.Card);
            bool focus = Box.Focused;
            float w = focus ? 1.5f : 1f;
            RectangleF r = new RectangleF(w / 2, w / 2, Width - w - 0.5f, Height - w - 0.5f);
            Shapes.Fill(g, r, Dpi.S(8), th.Field);
            Shapes.Stroke(g, r, Dpi.S(8), focus ? th.Accent : hot ? th.Text3 : th.FieldStroke, w);
            if (Fonts.Icons != null)
                TextRenderer.DrawText(g, Glyphs.Search, Fonts.IconsSmall ?? Fonts.Icons, new Rectangle(Dpi.S(10), 0, Dpi.S(18), Height), focus ? th.Accent : th.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // A button that opens a menu, drawn as a field with a chevron, such as the customer filter.
    public sealed class DropButton : FlatControl
    {
        public int MinWidth;

        public DropButton(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            Height = Dpi.S(32);
            TabStop = true;
            Fit();
        }

        public void Fit()
        {
            Width = Math.Max(MinWidth, Dpi.S(12) + Draw.Measure(Text, Fonts.Ui).Width + Dpi.S(14) + Dpi.S(10) + Dpi.S(10));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Fit();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Card);
            bool focus = Focused && ShowFocusCues;
            float w = focus ? 1.5f : 1f;
            RectangleF r = new RectangleF(w / 2, w / 2, Width - w - 0.5f, Height - w - 0.5f);
            Shapes.Fill(g, r, Dpi.S(8), down ? T.Pressed : hover ? Theme.Mix(T.Field, T.Hover, 0.8f) : T.Field);
            Shapes.Stroke(g, r, Dpi.S(8), focus ? T.Accent : hover ? T.Text3 : T.FieldStroke, w);
            int cw = Dpi.S(10);
            Draw.Text(g, Text, Fonts.Ui, new Rectangle(Dpi.S(12), 0, Width - Dpi.S(12) - cw - Dpi.S(20), Height), T.Text, Draw.Line);
            float cx = Width - Dpi.S(12) - cw / 2f, cy = Height / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(T.Text2, Math.Max(1.25f, Dpi.S(1.5f))))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[] { new PointF(cx - cw / 2f, cy - cw / 4f), new PointF(cx, cy + cw / 4f), new PointF(cx + cw / 2f, cy - cw / 4f) });
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }
}
