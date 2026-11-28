namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // The TrackVice mark at the left of the header.
    public sealed class BrandMark : FlatControl
    {
        public BrandMark()
        {
            Size = new Size(Dpi.S(28), Dpi.S(28));
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Draw.Mark(e.Graphics, new RectangleF(0, 0, Width, Height));
        }
    }

    // Where you are: "Contoso / CONTOSO-LT0417", or the title of the page that is open.
    public sealed class Crumb : FlatControl
    {
        string root = "", device = "", page;

        public Crumb()
        {
            Height = Dpi.S(26);
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public void ShowDevice(string r, string d)
        {
            root = r ?? "";
            device = d ?? "";
            page = null;
            Invalidate();
        }

        public void ShowPage(string title)
        {
            page = title ?? "";
            Invalidate();
        }

        public string Full
        {
            get { return page != null ? page : root.Length > 0 && device.Length > 0 ? root + " / " + device : root + device; }
        }

        public int PreferredWidth
        {
            get
            {
                if (page != null) return Measure(page, Fonts.UiSemibold) + Dpi.S(4);
                int w = Measure(device, Fonts.UiSemibold);
                if (root.Length > 0) w += Measure(root, Fonts.Ui) + Dpi.S(26);
                return w + Dpi.S(4);
            }
        }

        static int Measure(string s, Font f)
        {
            return string.IsNullOrEmpty(s) ? 0 : TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            TextFormatFlags fl = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            if (page != null)
            {
                TextRenderer.DrawText(g, page, Fonts.UiSemibold, new Rectangle(0, 0, Width, Height), T.Text, fl);
                return;
            }
            int x = 0;
            if (root.Length > 0)
            {
                int dw = Measure(device, Fonts.UiSemibold);
                int rw = Math.Min(Measure(root, Fonts.Ui), Math.Max(Dpi.S(40), (Width - Dpi.S(26)) / 2));
                if (device.Length > 0) rw = Math.Min(rw, Math.Max(Dpi.S(40), Width - dw - Dpi.S(26)));
                TextRenderer.DrawText(g, root, Fonts.Ui, new Rectangle(0, 0, rw, Height), T.Text3, fl);
                x = rw;
                if (device.Length > 0)
                {
                    using (Pen p = new Pen(T.Text3, 1.2f))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        int cx = x + Dpi.S(12), cy = Height / 2;
                        g.DrawLine(p, cx - Dpi.S(2), cy + Dpi.S(5), cx + Dpi.S(2), cy - Dpi.S(5));
                    }
                    x += Dpi.S(24);
                }
            }
            if (device.Length > 0) TextRenderer.DrawText(g, device, Fonts.UiSemibold, new Rectangle(x, 0, Math.Max(0, Width - x), Height), T.Text, fl);
        }
    }

    // The facts of the device as small chips: what runs, the profile, the user, Windows and when it started.
    // What does not fit is left out, and the last chip says how many.
    public sealed class FactChips : FlatControl
    {
        string[] items = new string[0];

        public FactChips()
        {
            Height = Dpi.S(24);
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public string[] Items
        {
            get { return items; }
            set { items = value ?? new string[0]; Invalidate(); }
        }

        int ChipWidth(string s)
        {
            return TextRenderer.MeasureText(s, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Dpi.S(16);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            int x = 0, gap = Dpi.S(6), more = Dpi.S(34);
            for (int i = 0; i < items.Length; i++)
            {
                int w = ChipWidth(items[i]);
                bool last = i == items.Length - 1;
                int need = w + (last ? 0 : gap + more);
                if (x + need > Width)
                {
                    int avail = Width - x;
                    int hidden = items.Length - i;
                    if (avail >= Dpi.S(90) && !last)
                    {
                        Chip(g, items[i], x, avail - gap - more);
                        hidden--;
                        x += avail - more;
                    }
                    else if (last && avail >= Dpi.S(60) && avail >= w * 3 / 4) { Chip(g, items[i], x, avail); hidden = 0; }
                    if (hidden > 0 && Width - x >= more - Dpi.S(4)) Chip(g, "+" + hidden, x, Width - x < more ? Width - x : more - gap);
                    return;
                }
                Chip(g, items[i], x, w);
                x += w + gap;
            }
        }

        void Chip(Graphics g, string text, int x, int w)
        {
            RectangleF r = new RectangleF(x + 0.5f, 1.5f, w - 1.5f, Height - 3.5f);
            Shapes.Fill(g, r, Dpi.S(8), T.Card);
            Shapes.Stroke(g, r, Dpi.S(8), T.Divider, 1f);
            TextRenderer.DrawText(g, text, Fonts.Small, new Rectangle(x + Dpi.S(8), 0, Math.Max(0, w - Dpi.S(16)), Height), T.Text2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // The field in the header that opens the command palette.
    public sealed class HeaderSearch : FlatControl
    {
        public HeaderSearch()
        {
            Height = Dpi.S(28);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public const string Label = "Search, jump or act…";
        public const string KeyHint = "Ctrl K";

        public static int NaturalWidth
        {
            get { return Dpi.S(34) + TextRenderer.MeasureText(Label, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(20) + KeyWidth; }
        }

        static int KeyWidth
        {
            get { return TextRenderer.MeasureText(KeyHint, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(12); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(8), down ? T.Pressed : hover ? T.Hover : T.Field);
            Shapes.Stroke(g, r, Dpi.S(8), Focused && ShowFocusCues ? T.Accent : hover ? T.Text3 : T.FieldStroke, Focused && ShowFocusCues ? 1.5f : 1f);
            Magnifier(g, new PointF(Dpi.S(11), Height / 2f - Dpi.S(1)), Dpi.S(5), hover ? T.Text2 : T.Text3);
            bool keys = Width >= Dpi.S(150);
            int kw = keys ? KeyWidth : 0;
            if (Width >= Dpi.S(96))
            {
                string label = Width >= Dpi.S(190) ? Label : "Search";
                TextRenderer.DrawText(g, label, Fonts.Small, new Rectangle(Dpi.S(26), 0, Math.Max(0, Width - Dpi.S(26) - kw - Dpi.S(14)), Height), T.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            if (keys)
            {
                RectangleF k = new RectangleF(Width - kw - Dpi.S(7) + 0.5f, Height / 2f - Dpi.S(9) + 0.5f, kw - 1f, Dpi.S(18) - 1f);
                Shapes.Stroke(g, k, Dpi.S(5), T.Divider, 1f);
                TextRenderer.DrawText(g, KeyHint, Fonts.Small, Rectangle.Round(k), T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        public static void Magnifier(Graphics g, PointF c, float rad, Color color)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(color, 1.5f))
            {
                p.EndCap = LineCap.Round;
                g.DrawEllipse(p, c.X - rad, c.Y - rad, rad * 2, rad * 2);
                float d = rad * 0.72f;
                g.DrawLine(p, c.X + d, c.Y + d, c.X + rad * 1.55f, c.Y + rad * 1.55f);
            }
            g.Restore(st);
        }
    }

    // The right side of the status bar: segments with a hairline between them, and a small live dot while watching.
    public sealed class StatusSegments : FlatControl
    {
        sealed class Seg
        {
            public string Text;
            public int Priority;
            public bool Live;
            public bool Shown = true;
        }

        readonly List<Seg> segs = new List<Seg>();

        public StatusSegments()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            Font = Fonts.Small;
        }

        public void Clear()
        {
            segs.Clear();
        }

        // Low priority goes first when the bar is narrow. Live gives the segment the dot.
        public void Add(string text, int priority, bool live)
        {
            Seg s = new Seg();
            s.Text = text;
            s.Priority = priority;
            s.Live = live;
            segs.Add(s);
        }

        public string AllText
        {
            get
            {
                List<string> l = new List<string>();
                foreach (Seg s in segs) l.Add(s.Text);
                return string.Join("  ·  ", l.ToArray());
            }
        }

        int SegWidth(Seg s)
        {
            return TextRenderer.MeasureText(s.Text, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (s.Live ? Dpi.S(14) : 0);
        }

        int Gap
        {
            get { return Dpi.S(22); }
        }

        // The width the visible segments need, dropping the lowest priority first until they fit in max.
        public int Fit(int max)
        {
            foreach (Seg s in segs) s.Shown = true;
            while (true)
            {
                int w = Total();
                if (w <= max) return w;
                Seg drop = null;
                foreach (Seg s in segs) if (s.Shown && s.Priority < 90 && (drop == null || s.Priority < drop.Priority)) drop = s;
                if (drop == null) return w;
                drop.Shown = false;
            }
        }

        int Total()
        {
            int w = 0, n = 0;
            foreach (Seg s in segs) if (s.Shown) { w += SegWidth(s); n++; }
            return w + Math.Max(0, n - 1) * Gap;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            int x = Width;
            for (int i = segs.Count - 1; i >= 0; i--)
            {
                Seg s = segs[i];
                if (!s.Shown) continue;
                int w = SegWidth(s);
                x -= w;
                int tx = x;
                if (s.Live)
                {
                    int d = Dpi.S(6), cy = Height / 2;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush halo = new SolidBrush(Color.FromArgb(55, T.OkFg))) g.FillEllipse(halo, x - Dpi.S(2), cy - d / 2 - Dpi.S(2), d + Dpi.S(4), d + Dpi.S(4));
                    using (SolidBrush dot = new SolidBrush(T.OkFg)) g.FillEllipse(dot, x, cy - d / 2, d, d);
                    tx = x + Dpi.S(14);
                }
                TextRenderer.DrawText(g, s.Text, Fonts.Small, new Rectangle(tx, 0, w, Height), s.Live ? T.Text2 : T.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                x -= Gap;
                bool more = false;
                for (int k = i - 1; k >= 0; k--) if (segs[k].Shown) more = true;
                if (more)
                {
                    using (Pen p = new Pen(T.Divider, 1f)) g.DrawLine(p, x + Gap / 2, Height / 2 - Dpi.S(6), x + Gap / 2, Height / 2 + Dpi.S(6));
                }
            }
        }
    }
}
