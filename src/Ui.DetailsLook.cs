namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // What the details card shows. MainForm fills it from an item or a phase; DetailView draws it.
    public sealed class DetailKv
    {
        public string Label, Value;
        public bool Mono, Copy;
        public double? Bar;
        public Color Tone;
    }

    public sealed class DetailAttempt
    {
        public DateTime Start, End;
        public bool Open, RestartBefore;
        public Color Tone;
        public string Label, Times;
    }

    public sealed class DetailEvent
    {
        public string Time, Source, Text;
        public int Severity;
    }

    public sealed class DetailCallout
    {
        public const int Tinted = 0, Plain = 1, Note = 2;
        // Tinted: the block with the error. Plain: a labelled block beside a thin bar. Note: a small box.
        public int Kind;
        public string Label, Title, Text;
        public Color Tone;
    }

    // A line that leads to an item: a finding on the empty card, an item on a phase card.
    public sealed class DetailRow
    {
        public string Key, Tag, Text, Right;
        public Color Tone;
    }

    public sealed class DetailModel
    {
        public string Key, Title, Tile, Pill, Meta;
        public Color PillTone;
        public readonly List<DetailCallout> Callouts = new List<DetailCallout>();
        public string ResultTitle, DetailsTitle;
        public readonly List<DetailKv> Result = new List<DetailKv>();
        public readonly List<DetailKv> Details = new List<DetailKv>();
        public string AttemptsTitle;
        public readonly List<DetailAttempt> Attempts = new List<DetailAttempt>();
        public string RowsTitle;
        public readonly List<DetailRow> Rows = new List<DetailRow>();
        public string EventsTitle;
        public readonly List<DetailEvent> Events = new List<DetailEvent>();
        public string EvWhere, EvPath, EvRaw;
        public bool EvCanOpen;
        public bool Empty;
        public string EmptyTitle, EmptyText;
    }

    // The details card: a header, the finding in three blocks, the result as a grid, the attempts on a time axis,
    // the events as a log and the evidence in a code box. Drawn by hand so it scrolls smoothly.
    public sealed class DetailView : FlatControl
    {
        sealed class Hit
        {
            public Rectangle R;
            public int Kind;
            public string Text;

            public bool Same(Hit o)
            {
                return o != null && o.Kind == Kind && o.R == R && o.Text == Text;
            }
        }

        const int HitCopy = 0, HitOpen = 1, HitCopyRaw = 2, HitSelect = 3;
        const string RawKey = "\u0001raw";

        public event Action<string> CopyRequested;
        public event Action<string> SelectRequested;
        public event Action OpenLogRequested;

        DetailModel model;
        readonly List<Hit> hits = new List<Hit>();
        Hit hot, press;
        double cur, target;
        int contentH;
        bool dragging, overBar;
        int dragOffset;
        string copiedKey;
        DateTime copiedAt = DateTime.MinValue;
        readonly Timer anim = new Timer();
        readonly Timer copiedTimer = new Timer();
        readonly Font fTitle, fBig, fBody, fSemi, fHead, fLabel, fSmallSemi, fChip, fMono, fMonoSmall;

        // Drawing state of the current pass.
        Graphics G;
        int W, dy;
        bool paint;

        public DetailView()
        {
            Cursor = Cursors.Default;
            TabStop = false;
            fTitle = Px(Fonts.UiSemibold, 22);
            fBig = Px(Fonts.UiSemibold, 15);
            fBody = Px(Fonts.Ui, 13);
            fSemi = Px(Fonts.UiSemibold, 13);
            fHead = Px(Fonts.UiSemibold, 13);
            fLabel = Px(Fonts.Ui, 12);
            fSmallSemi = Px(Fonts.UiSemibold, 11);
            fChip = Px(Fonts.Ui, 11);
            fMono = Px(Fonts.Mono, 12);
            fMonoSmall = Px(Fonts.Mono, 11);
            anim.Interval = 15;
            anim.Tick += delegate { Step(); };
            copiedTimer.Interval = 1700;
            copiedTimer.Tick += delegate { copiedTimer.Stop(); copiedKey = null; Invalidate(); };
        }

        static Font Px(Font basis, float px)
        {
            return new Font(basis.FontFamily, Dpi.S(px), basis.Style, GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                anim.Dispose();
                copiedTimer.Dispose();
                foreach (Font f in new Font[] { fTitle, fBig, fBody, fSemi, fHead, fLabel, fSmallSemi, fChip, fMono, fMonoSmall }) f.Dispose();
            }
            base.Dispose(disposing);
        }

        // Off when Windows is set not to show animations.
        static bool Animated
        {
            get
            {
                try { return SystemInformation.UIEffectsEnabled && !SystemInformation.TerminalServerSession; }
                catch (Exception) { return true; }
            }
        }

        // The same item keeps its scroll position while it is refreshed; another one starts at the top.
        public void SetModel(DetailModel m)
        {
            bool same = model != null && m != null && model.Key != null && model.Key == m.Key && !model.Empty && !m.Empty;
            model = m;
            if (!same) { cur = target = 0; anim.Stop(); }
            hot = press = null;
            MeasureNow();
            Invalidate();
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            MeasureNow();
        }

        void MeasureNow()
        {
            if (Width <= 0 || Height <= 0) return;
            contentH = Flow(null, Width, false);
            Clamp();
        }

        int MaxScroll
        {
            get { return Math.Max(0, contentH - Height); }
        }

        void Clamp()
        {
            double max = MaxScroll;
            target = Math.Max(0, Math.Min(max, target));
            cur = Math.Max(0, Math.Min(max, cur));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            MeasureNow();
        }

        void ScrollBy(double pixels)
        {
            if (MaxScroll == 0) return;
            target += pixels;
            Clamp();
            if (Animated) anim.Start();
            else { cur = target; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-e.Delta / 120.0 * Dpi.S(72));
        }

        // Ease-out: each tick covers a part of what is left, so a scroll settles in about 150 ms.
        void Step()
        {
            double d = target - cur;
            if (Math.Abs(d) < 0.6) { cur = target; anim.Stop(); }
            else cur += d * 0.3;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int page = Math.Max(Dpi.S(72), Height - Dpi.S(48));
            if (e.KeyCode == Keys.Down) ScrollBy(Dpi.S(48));
            else if (e.KeyCode == Keys.Up) ScrollBy(-Dpi.S(48));
            else if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.Space) ScrollBy(page);
            else if (e.KeyCode == Keys.PageUp) ScrollBy(-page);
            else if (e.KeyCode == Keys.Home) ScrollBy(-contentH);
            else if (e.KeyCode == Keys.End) ScrollBy(contentH);
            else return;
            e.Handled = true;
        }

        // ---------- the scroll bar ----------

        Rectangle Thumb()
        {
            if (MaxScroll == 0) return Rectangle.Empty;
            int th = Math.Max(Dpi.S(28), (int)((long)Height * Height / contentH));
            int ty = (int)Math.Round((Height - th - Dpi.S(8)) * (cur / MaxScroll)) + Dpi.S(4);
            int w = overBar || dragging ? Dpi.S(6) : Dpi.S(3);
            return new Rectangle(Width - Dpi.S(4) - w, ty, w, th);
        }

        bool OnBarStrip(Point p)
        {
            return MaxScroll > 0 && p.X >= Width - Dpi.S(12);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (CanFocus) Focus();
            if (OnBarStrip(e.Location))
            {
                Rectangle t = Thumb();
                if (e.Y >= t.Top && e.Y <= t.Bottom) dragOffset = e.Y - t.Top;
                else
                {
                    dragOffset = t.Height / 2;
                    ScrollToThumb(e.Y);
                }
                dragging = true;
                Capture = true;
                Invalidate();
                return;
            }
            press = HitAt(e.Location);
            Invalidate();
        }

        void ScrollToThumb(int mouseY)
        {
            int th = Thumb().Height;
            double span = Math.Max(1, Height - th - Dpi.S(8));
            double f = Math.Max(0, Math.Min(1, (mouseY - dragOffset - Dpi.S(4)) / span));
            anim.Stop();
            cur = target = f * MaxScroll;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { ScrollToThumb(e.Y); return; }
            bool bar = OnBarStrip(e.Location);
            Hit h = bar ? null : HitAt(e.Location);
            bool sameHot = h == null ? hot == null : h.Same(hot);
            if (bar != overBar || !sameHot)
            {
                overBar = bar;
                hot = h;
                Cursor = h != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Capture = false; Invalidate(); return; }
            Hit h = HitAt(e.Location);
            Hit was = press;
            press = null;
            Invalidate();
            if (h != null && h.Same(was)) Activate(h);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = null;
            overBar = false;
            Cursor = Cursors.Default;
        }

        Hit HitAt(Point p)
        {
            int y = p.Y + (int)Math.Round(cur);
            for (int k = hits.Count - 1; k >= 0; k--) if (hits[k].R.Contains(p.X, y)) return hits[k];
            return null;
        }

        void Activate(Hit h)
        {
            if (h.Kind == HitOpen)
            {
                if (OpenLogRequested != null) OpenLogRequested();
                return;
            }
            if (h.Kind == HitSelect)
            {
                if (SelectRequested != null) SelectRequested(h.Text);
                return;
            }
            string text = h.Kind == HitCopyRaw ? (model != null ? model.EvRaw : null) : h.Text;
            if (string.IsNullOrEmpty(text)) return;
            if (CopyRequested != null) CopyRequested(text);
            copiedKey = h.Kind == HitCopyRaw ? RawKey : text;
            copiedAt = DateTime.Now;
            copiedTimer.Stop();
            copiedTimer.Start();
        }

        bool Copied(string key)
        {
            return copiedKey != null && copiedKey == key && (DateTime.Now - copiedAt).TotalMilliseconds < 1650;
        }

        // Adds what can be clicked at r (in content coordinates) and tells whether the pointer is on it.
        bool Touch(Rectangle r, int kind, string text, out bool isDown)
        {
            isDown = false;
            if (!paint) return false;
            Hit h = new Hit();
            h.R = r;
            h.Kind = kind;
            h.Text = text;
            hits.Add(h);
            bool isHot = h.Same(hot);
            isDown = isHot && h.Same(press);
            return isHot;
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Card);
            if (Width <= 0 || Height <= 0) return;
            int h = Flow(g, Width, true);
            if (h != contentH)
            {
                contentH = h;
                Clamp();
            }
            if (MaxScroll == 0) return;
            // A hairline where the text goes on above or below.
            using (Pen p = new Pen(T.Divider))
            {
                if (cur > 0.5) g.DrawLine(p, 0, 0, Width, 0);
                if (cur < MaxScroll - 0.5) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            }
            Rectangle t = Thumb();
            Shapes.Fill(g, new RectangleF(t.X, t.Y, t.Width, t.Height), t.Width / 2f, Color.FromArgb(dragging || overBar ? 190 : 110, T.Text3));
        }

        Rectangle O(int x, int y, int w, int h)
        {
            return new Rectangle(x, y + dy, w, h);
        }

        bool Shown(int y, int h)
        {
            return paint && y + dy + h >= 0 && y + dy <= Height;
        }

        const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left;
        const TextFormatFlags OneLineRight = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;
        const TextFormatFlags Centered = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis;

        void Str(string s, Font f, int x, int y, int w, int h, Color c, TextFormatFlags fl)
        {
            if (string.IsNullOrEmpty(s) || w <= 0 || !Shown(y, h)) return;
            TextRenderer.DrawText(G, s, f, O(x, y, w, h), c, fl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        int Wrap(string s, Font f, int x, int y, int w, Color c)
        {
            if (string.IsNullOrEmpty(s) || w <= 0) return 0;
            int h = Draw.WrapHeight(s, f, w);
            Str(s, f, x, y, w, h + Dpi.S(2), c, Draw.Wrap);
            return h;
        }

        static int TextW(string s, Font f)
        {
            return string.IsNullOrEmpty(s) ? 0 : TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        }

        // A rounded box; the corners follow the theme and never grow past a half circle.
        void Box(int x, int y, int w, int h, float rad, Color fill, Color stroke)
        {
            if (w <= 0 || h <= 0 || !Shown(y, h)) return;
            RectangleF r = new RectangleF(x, y + dy, w, h);
            if (!fill.IsEmpty)
                using (GraphicsPath p = Shapes.Fixed(r, Corner(rad, w, h)))
                using (SolidBrush b = new SolidBrush(fill)) G.FillPath(b, p);
            if (!stroke.IsEmpty)
                using (GraphicsPath p = Shapes.Fixed(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), Corner(rad, w - 1, h - 1)))
                using (Pen pen = new Pen(stroke, 1f)) G.DrawPath(pen, p);
        }

        float Corner(float rad, float w, float h)
        {
            return Math.Max(0f, Math.Min(rad * T.Corners, Math.Min(w, h) / 2f - 0.01f));
        }

        void Dot(int x, int y, int d, Color c)
        {
            if (!Shown(y, d)) return;
            using (SolidBrush b = new SolidBrush(c)) G.FillEllipse(b, x, y + dy, d, d);
        }

        void Rule(int x, int y, int w)
        {
            if (!Shown(y, 1)) return;
            using (Pen p = new Pen(T.Divider)) G.DrawLine(p, x, y + dy, x + w, y + dy);
        }

        // The card's colour with a little of the tone in it: more on dark themes, where a tint is harder to see.
        Color Tint(Color tone, float amount)
        {
            return Theme.Mix(T.Card, tone, T.IsDark ? amount * 1.7f : amount);
        }

        Color Edge(Color tone)
        {
            return Theme.Mix(T.Card, tone, T.IsDark ? 0.42f : 0.34f);
        }

        int Flow(Graphics g, int width, bool doPaint)
        {
            G = g;
            W = width;
            paint = doPaint && g != null;
            dy = paint ? -(int)Math.Round(cur) : 0;
            if (paint)
            {
                hits.Clear();
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
            int pad = W < Dpi.S(360) ? Dpi.S(14) : Dpi.S(20);
            int y = Dpi.S(20);
            if (model == null) return y;
            if (model.Empty) y = Empty(y, pad);
            else
            {
                y = Header(y, pad);
                foreach (DetailCallout c in model.Callouts) y = Callout(y, pad, c);
                if (model.Callouts.Count > 0) y += Dpi.S(4);
                if (model.Result.Count > 0) { y = Head(y, pad, model.ResultTitle); y = Grid(y, pad, model.Result); }
                if (model.Attempts.Count > 0) { y = Head(y, pad, model.AttemptsTitle); y = Attempts(y, pad); }
                if (model.Rows.Count > 0) { y = Head(y, pad, model.RowsTitle); y = Rows(y, pad); }
                if (model.Details.Count > 0) { y = Head(y, pad, model.DetailsTitle ?? "Details"); y = Grid(y, pad, model.Details); }
                if (model.Events.Count > 0) { y = Head(y, pad, model.EventsTitle); y = EventList(y, pad); }
                if (!string.IsNullOrEmpty(model.EvRaw) || !string.IsNullOrEmpty(model.EvWhere)) { y = Head(y, pad, "Evidence"); y = Evidence(y, pad); }
            }
            return y + Dpi.S(20);
        }

        // ---------- the pieces ----------

        int Empty(int y, int pad)
        {
            int cw = W - 2 * pad;
            bool rows = model.Rows.Count > 0;
            // Alone, the message sits a third down the card; with findings under it, it stays at the top.
            if (!rows) y = Math.Max(y + Dpi.S(28), Height / 3 - Dpi.S(70));
            else y += Dpi.S(20);
            int d = Dpi.S(56);
            int cx = (W - d) / 2;
            if (Shown(y, d))
            {
                Shapes.Fill(G, new RectangleF(cx, y + dy, d, d), d / 2f, Tint(T.Text3, 0.10f));
                // A small list with one line picked.
                float lx = cx + d * 0.30f, lw = d * 0.40f;
                for (int k = 0; k < 3; k++)
                {
                    float ly = y + dy + d * (0.35f + k * 0.15f);
                    using (Pen p = new Pen(k == 1 ? T.Accent : Color.FromArgb(150, T.Text3), Dpi.S(2f)))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        G.DrawLine(p, lx, ly, lx + (k == 2 ? lw * 0.6f : lw), ly);
                    }
                }
            }
            y += d + Dpi.S(16);
            int th = Draw.WrapHeight(model.EmptyTitle, fBig, cw);
            Str(model.EmptyTitle, fBig, pad, y, cw, th + Dpi.S(2), T.Text, Draw.Wrap | TextFormatFlags.HorizontalCenter);
            y += th + Dpi.S(6);
            if (!string.IsNullOrEmpty(model.EmptyText))
            {
                int tw = Math.Min(cw, Dpi.S(340));
                int h = Draw.WrapHeight(model.EmptyText, fBody, tw);
                Str(model.EmptyText, fBody, (W - tw) / 2, y, tw, h + Dpi.S(2), T.Text3, Draw.Wrap | TextFormatFlags.HorizontalCenter);
                y += h;
            }
            if (rows)
            {
                y += Dpi.S(22);
                y = Head(y, pad, model.RowsTitle);
                y = Rows(y, pad);
            }
            return y;
        }

        int Header(int y, int pad)
        {
            int tile = Dpi.S(44);
            Box(pad, y, tile, tile, Dpi.S(12), Tint(T.Text3, 0.10f), T.Divider);
            Str(model.Tile, fSmallSemi, pad, y, tile, tile, T.Text2, Centered);
            int tx = pad + tile + Dpi.S(14), tw = W - pad - tx;
            int ty = y - Dpi.S(3);
            int th = Wrap(model.Title, fTitle, tx, ty, tw, T.Text);
            ty += Math.Max(th, fTitle.Height) + Dpi.S(8);
            int ph = Dpi.S(24);
            int pw = Math.Min(tw, TextW(model.Pill, fSemi) + Dpi.S(34));
            Box(tx, ty, pw, ph, ph / 2f, Tint(model.PillTone, 0.13f), Edge(model.PillTone));
            Dot(tx + Dpi.S(11), ty + (ph - Dpi.S(7)) / 2, Dpi.S(7), model.PillTone);
            Str(model.Pill, fSemi, tx + Dpi.S(24), ty, pw - Dpi.S(34), ph, model.PillTone, OneLine);
            if (!string.IsNullOrEmpty(model.Meta))
            {
                int mx = tx + pw + Dpi.S(10);
                if (mx + TextW(model.Meta, fLabel) <= W - pad) Str(model.Meta, fLabel, mx, ty, W - pad - mx, ph, T.Text3, OneLine);
                else
                {
                    ty += ph + Dpi.S(8);
                    ty += Wrap(model.Meta, fLabel, tx, ty, tw, T.Text3) - ph;
                }
            }
            return Math.Max(y + tile, ty + ph) + Dpi.S(20);
        }

        int Callout(int y, int pad, DetailCallout c)
        {
            int cw = W - 2 * pad;
            int bar = Dpi.S(3);
            int ix = pad + Dpi.S(16), iw = cw - Dpi.S(30);
            int lh = fSmallSemi.Height;
            if (c.Kind == DetailCallout.Tinted)
            {
                int ip = Dpi.S(14);
                int h = ip + lh + Dpi.S(6);
                if (!string.IsNullOrEmpty(c.Title)) h += Draw.WrapHeight(c.Title, fSemi, iw) + Dpi.S(4);
                h += Draw.WrapHeight(c.Text, fBig, iw) + ip;
                Box(pad, y, cw, h, Dpi.S(12), Tint(c.Tone, 0.08f), Edge(c.Tone));
                Box(pad, y + Dpi.S(12), bar, h - Dpi.S(24), bar / 2f, c.Tone, Color.Empty);
                int iy = y + ip;
                Str(c.Label, fSmallSemi, ix, iy, iw, lh, c.Tone, OneLine);
                iy += lh + Dpi.S(6);
                if (!string.IsNullOrEmpty(c.Title)) iy += Wrap(c.Title, fSemi, ix, iy, iw, T.Text2) + Dpi.S(4);
                Wrap(c.Text, fBig, ix, iy, iw, T.Text);
                return y + h + Dpi.S(14);
            }
            if (c.Kind == DetailCallout.Note)
            {
                int ip = Dpi.S(12);
                int h = ip * 2 + lh + Dpi.S(4) + Draw.WrapHeight(c.Text, fLabel, iw);
                Box(pad, y, cw, h, Dpi.S(10), Tint(c.Tone, 0.07f), Edge(c.Tone));
                Str(c.Label, fSmallSemi, ix, y + ip, iw, lh, c.Tone, OneLine);
                Wrap(c.Text, fLabel, ix, y + ip + lh + Dpi.S(4), iw, T.Text);
                return y + h + Dpi.S(14);
            }
            // A plain block: the same thin bar and the same text edge as the tinted one, without the box.
            int top = y;
            Str(c.Label, fSmallSemi, ix, y, iw, lh, c.Tone, OneLine);
            y += lh + Dpi.S(4);
            if (!string.IsNullOrEmpty(c.Title)) y += Wrap(c.Title, fSemi, ix, y, iw, T.Text) + Dpi.S(2);
            y += Wrap(c.Text, fBody, ix, y, iw, T.Text);
            Box(pad, top + Dpi.S(1), bar, y - top, bar / 2f, Theme.Mix(T.Card, c.Tone, 0.55f), Color.Empty);
            return y + Dpi.S(14);
        }

        int Head(int y, int pad, string title)
        {
            y += Dpi.S(6);
            Rule(pad, y, W - 2 * pad);
            y += Dpi.S(14);
            Str(title, fHead, pad, y, W - 2 * pad, Dpi.S(20), T.Text, OneLine);
            return y + Dpi.S(28);
        }

        // Two columns, labels left and values right; one under the other when the card is narrow.
        int Grid(int y, int pad, List<DetailKv> rows)
        {
            int cw = W - 2 * pad;
            bool stacked = cw < Dpi.S(300);
            int lw = stacked ? cw : Math.Min(Dpi.S(132), cw * 2 / 5);
            int gap = Dpi.S(12);
            int line = Math.Max(fBody.Height, fMono.Height);
            foreach (DetailKv r in rows)
            {
                Font vf = r.Mono ? fMono : fBody;
                int cpw = r.Copy ? Dpi.S(28) : 0;
                int vx = stacked ? pad : pad + lw + gap;
                int vw = cw - (stacked ? 0 : lw + gap) - cpw;
                int lh = Draw.WrapHeight(r.Label, fLabel, lw);
                int vh = Draw.WrapHeight(r.Value, vf, vw);
                Color vc = r.Tone.IsEmpty ? T.Text : r.Tone;
                int vy = y, h;
                if (stacked)
                {
                    Wrap(r.Label, fLabel, pad, y, lw, T.Text3);
                    vy = y + lh + Dpi.S(2);
                    Wrap(r.Value, vf, vx, vy, vw, vc);
                    h = lh + Dpi.S(2) + vh;
                }
                else
                {
                    // Label and value share the first line's middle, whatever their fonts.
                    Wrap(r.Label, fLabel, pad, y + (line - fLabel.Height) / 2, lw, T.Text3);
                    Wrap(r.Value, vf, vx, y + (line - vf.Height) / 2, vw, vc);
                    h = Math.Max(Math.Max(vh, lh), line);
                }
                if (r.Copy) CopyMark(pad + cw - Dpi.S(24), vy + (line - Dpi.S(24)) / 2, r.Value);
                if (r.Bar.HasValue)
                {
                    int bw = Math.Min(vw, Dpi.S(200));
                    if (Shown(y + h, Dpi.S(12))) StateLook.Bar(G, new RectangleF(vx, y + h + Dpi.S(6) + dy, bw, Dpi.S(4)), r.Bar.Value, r.Tone.IsEmpty ? T.Accent : r.Tone, Tint(T.Text3, 0.14f));
                    h += Dpi.S(12);
                }
                y += h + Dpi.S(stacked ? 12 : 10);
            }
            return y - Dpi.S(4);
        }

        // Two sheets to copy a value; a check for a moment after it was copied.
        void CopyMark(int x, int y, string value)
        {
            int s = Dpi.S(24);
            bool isDown;
            bool isHot = Touch(new Rectangle(x, y, s, s), HitCopy, value, out isDown);
            if (!Shown(y, s)) return;
            bool done = Copied(value);
            if (isHot) Shapes.Fill(G, new RectangleF(x, y + dy, s, s), Dpi.S(6), isDown ? T.Pressed : T.Hover);
            using (Pen p = new Pen(done ? T.OkFg : isHot ? T.Text : T.Text3, Dpi.S(1.3f)))
            {
                p.LineJoin = LineJoin.Round;
                p.StartCap = p.EndCap = LineCap.Round;
                float cx = x + Dpi.S(6), cy = y + dy + Dpi.S(6);
                float u = Dpi.S(1f);
                if (done) G.DrawLines(p, new PointF[] { new PointF(cx + u, cy + 6 * u), new PointF(cx + 4.5f * u, cy + 9.5f * u), new PointF(cx + 11 * u, cy + 2.5f * u) });
                else
                {
                    using (GraphicsPath front = Shapes.Fixed(new RectangleF(cx + 3.5f * u, cy + 3.5f * u, 8 * u, 8.5f * u), 1.5f * u)) G.DrawPath(p, front);
                    G.DrawLines(p, new PointF[] { new PointF(cx + 0.5f * u, cy + 8 * u), new PointF(cx + 0.5f * u, cy + 0.5f * u), new PointF(cx + 8 * u, cy + 0.5f * u) });
                }
            }
        }

        // One bar per attempt on a shared time axis. A gap in which the device restarted is hatched.
        int Attempts(int y, int pad)
        {
            int cw = W - 2 * pad;
            List<DetailAttempt> list = model.Attempts;
            DateTime t0 = list[0].Start, t1 = list[0].End;
            foreach (DetailAttempt a in list)
            {
                if (a.Start < t0) t0 = a.Start;
                if (a.End > t1) t1 = a.End;
            }
            double span = Math.Max(1.0, (t1 - t0).TotalSeconds);
            int bh = Dpi.S(10), minw = Dpi.S(6), gapPx = Dpi.S(2);
            Box(pad, y, cw, bh, bh / 2f, Tint(T.Text3, 0.12f), Color.Empty);
            int prevEnd = pad;
            bool hatched = false;
            int[] xs = new int[list.Count], xe = new int[list.Count];
            for (int k = 0; k < list.Count; k++)
            {
                DetailAttempt a = list[k];
                int s = pad + (int)Math.Round((a.Start - t0).TotalSeconds / span * cw);
                int e = pad + (int)Math.Round((a.End - t0).TotalSeconds / span * cw);
                // Every attempt can be seen and none runs into the one before.
                if (k > 0 && s < prevEnd + gapPx) s = prevEnd + gapPx;
                if (e - s < minw) e = s + minw;
                if (e > pad + cw) { e = pad + cw; s = Math.Min(s, e - minw); }
                xs[k] = s;
                xe[k] = e;
                prevEnd = e;
            }
            for (int k = 0; k < list.Count; k++)
            {
                DetailAttempt a = list[k];
                if (k > 0 && a.RestartBefore && xs[k] - xe[k - 1] >= Dpi.S(6))
                {
                    Hatch(xe[k - 1] + gapPx, y, xs[k] - xe[k - 1] - 2 * gapPx, bh);
                    hatched = true;
                }
                Box(xs[k], y, xe[k] - xs[k], bh, bh / 2f, a.Open ? Theme.Mix(T.Card, a.Tone, 0.75f) : a.Tone, Color.Empty);
            }
            y += bh + Dpi.S(6);
            int ah = fMonoSmall.Height + Dpi.S(2);
            Str(Txt.LocalTime(t0), fMonoSmall, pad, y, cw / 2, ah, T.Text3, OneLine);
            if ((t1 - t0).TotalSeconds >= 1) Str(list[list.Count - 1].Open ? "now" : Txt.LocalTime(t1), list[list.Count - 1].Open ? fChip : fMonoSmall, pad + cw / 2, y, cw - cw / 2, ah, T.Text3, OneLineRight);
            y += ah + Dpi.S(10);
            int dot = Dpi.S(8), rowH = Dpi.S(20);
            int lx = pad + dot + Dpi.S(10), room = W - pad - lx;
            foreach (DetailAttempt a in list)
            {
                int tw = TextW(a.Times, fMonoSmall);
                if (TextW(a.Label, fSemi) + tw + Dpi.S(16) <= room)
                {
                    Dot(pad, y + (rowH - dot) / 2, dot, a.Tone);
                    Str(a.Label, fSemi, lx, y, room - tw - Dpi.S(8), rowH, T.Text, OneLine);
                    Str(a.Times, fMonoSmall, lx, y, room, rowH, T.Text3, OneLineRight);
                    y += rowH + Dpi.S(6);
                }
                else
                {
                    Dot(pad, y + (fSemi.Height - dot) / 2 + Dpi.S(1), dot, a.Tone);
                    y += Wrap(a.Label, fSemi, lx, y, room, T.Text) + Dpi.S(2);
                    Str(a.Times, fMonoSmall, lx, y, room, ah, T.Text3, OneLine);
                    y += ah + Dpi.S(8);
                }
            }
            if (hatched)
            {
                int sw = Dpi.S(18);
                Hatch(pad, y + Dpi.S(5), sw, bh);
                Str("The device restarted", fChip, pad + sw + Dpi.S(8), y, cw - sw - Dpi.S(8), rowH, T.Text3, OneLine);
                y += rowH + Dpi.S(4);
            }
            return y - Dpi.S(2);
        }

        void Hatch(int x, int y, int w, int h)
        {
            if (w <= 0 || !Shown(y, h)) return;
            RectangleF r = new RectangleF(x, y + dy, w, h);
            GraphicsState st = G.Save();
            using (GraphicsPath clip = Shapes.Fixed(r, Dpi.S(2)))
            using (Pen p = new Pen(Color.FromArgb(T.IsDark ? 200 : 170, T.Text3), Dpi.S(1.2f)))
            {
                G.SetClip(clip, CombineMode.Intersect);
                float step = Dpi.S(5f);
                for (float lx = r.X - r.Height; lx < r.Right; lx += step) G.DrawLine(p, lx, r.Bottom, lx + r.Height, r.Y);
            }
            G.Restore(st);
        }

        // Lines that lead to an item: a state or severity, the name and, on the right, how long it took.
        int Rows(int y, int pad)
        {
            int cw = W - 2 * pad;
            int inset = Dpi.S(8);
            int tagW = 0, rightW = 0;
            foreach (DetailRow r in model.Rows)
            {
                tagW = Math.Max(tagW, TextW(r.Tag, fLabel));
                rightW = Math.Max(rightW, TextW(r.Right, fLabel));
            }
            int dot = Dpi.S(8);
            int tx = pad + dot + Dpi.S(10);
            bool wide = cw >= Dpi.S(330);
            y -= Dpi.S(4);
            foreach (DetailRow r in model.Rows)
            {
                int nameW = W - pad - tx - (rightW > 0 ? rightW + Dpi.S(12) : 0) - (wide && tagW > 0 ? tagW + Dpi.S(12) : 0);
                int nh = Math.Max(fBody.Height, Draw.WrapHeight(r.Text, fBody, nameW));
                int subH = wide || string.IsNullOrEmpty(r.Tag) ? 0 : fLabel.Height + Dpi.S(1);
                int h = nh + subH + Dpi.S(12);
                bool isDown = false;
                bool isHot = r.Key != null && Touch(new Rectangle(pad - inset, y, cw + 2 * inset, h), HitSelect, r.Key, out isDown);
                if (isHot) Box(pad - inset, y, cw + 2 * inset, h, Dpi.S(8), isDown ? T.Pressed : T.Hover, Color.Empty);
                int ry = y + Dpi.S(6);
                Dot(pad, ry + (fBody.Height - dot) / 2 + Dpi.S(1), dot, r.Tone);
                Wrap(r.Text, fBody, tx, ry, nameW, T.Text);
                int line = fBody.Height + Dpi.S(2);
                if (wide) Str(r.Tag, fLabel, tx + nameW + Dpi.S(12), ry, tagW, line, r.Tone, OneLine);
                else Str(r.Tag, fLabel, tx, ry + nh + Dpi.S(1), nameW, fLabel.Height + Dpi.S(2), r.Tone, OneLine);
                Str(r.Right, fLabel, W - pad - rightW, ry, rightW, line, T.Text3, OneLineRight);
                y += h;
            }
            return y + Dpi.S(2);
        }

        // The log: time, where it came from, what it said. Warnings and errors carry their colour.
        int EventList(int y, int pad)
        {
            int cw = W - 2 * pad;
            bool stacked = cw < Dpi.S(380);
            int inset = Dpi.S(8);
            int tcol = TextW("00:00:00", fMonoSmall) + Dpi.S(10);
            int chipH = Dpi.S(18);
            int ccol = 0;
            foreach (DetailEvent e in model.Events) ccol = Math.Max(ccol, TextW(e.Source, fChip) + Dpi.S(14));
            ccol = Math.Min(ccol, Dpi.S(stacked ? 200 : 116));
            y -= Dpi.S(4);
            foreach (DetailEvent e in model.Events)
            {
                Color tone = e.Severity >= 2 ? T.ErrFg : e.Severity == 1 ? T.WarnFg : T.Text;
                int mx = stacked ? pad : pad + tcol + (ccol > 0 ? ccol + Dpi.S(10) : 0);
                int mw = W - pad - mx;
                int mh = Math.Max(fBody.Height, Draw.WrapHeight(e.Text, fBody, mw));
                int h = (stacked ? chipH + Dpi.S(4) : 0) + mh + Dpi.S(10);
                if (e.Severity >= 1)
                {
                    Box(pad - inset, y + Dpi.S(1), cw + 2 * inset, h - Dpi.S(2), Dpi.S(6), Tint(tone, 0.06f), Color.Empty);
                }
                int ry = y + Dpi.S(5);
                int line = Math.Max(chipH, fBody.Height);
                Str(e.Time, fMonoSmall, pad, ry, tcol, line, T.Text3, OneLine);
                if (!string.IsNullOrEmpty(e.Source))
                {
                    int sw = Math.Min(ccol, TextW(e.Source, fChip) + Dpi.S(14));
                    int sy = ry + (line - chipH) / 2;
                    Box(pad + tcol, sy, sw, chipH, chipH / 2f, Tint(T.Text3, 0.13f), Color.Empty);
                    Str(e.Source, fChip, pad + tcol + Dpi.S(7), sy, sw - Dpi.S(14), chipH, T.Text2, OneLine);
                }
                Wrap(e.Text, fBody, mx, stacked ? ry + chipH + Dpi.S(4) : ry + (line - fBody.Height) / 2, mw, tone);
                y += h;
            }
            return y + Dpi.S(2);
        }

        // Where the last line about the item is, and the line itself as it stands in the log.
        int Evidence(int y, int pad)
        {
            int cw = W - 2 * pad;
            if (!string.IsNullOrEmpty(model.EvWhere)) y += Wrap(model.EvWhere, fLabel, pad, y, cw, T.Text3) + Dpi.S(10);
            bool hasPath = !string.IsNullOrEmpty(model.EvPath), hasRaw = !string.IsNullOrEmpty(model.EvRaw);
            if (hasPath || hasRaw)
            {
                int ip = Dpi.S(12), iw = cw - 2 * ip;
                int ph = hasPath ? Draw.WrapHeight(model.EvPath, fMonoSmall, iw) : 0;
                int rh = hasRaw ? Draw.WrapHeight(model.EvRaw, fMono, iw) : 0;
                int between = hasPath && hasRaw ? Dpi.S(17) : 0;
                int h = ip * 2 + ph + rh + between;
                Box(pad, y, cw, h, Dpi.S(10), T.IsDark ? Theme.Mix(T.Card, Color.Black, 0.30f) : Theme.Mix(T.Card, T.Text, 0.04f), T.Divider);
                int iy = y + ip;
                if (hasPath)
                {
                    Wrap(model.EvPath, fMonoSmall, pad + ip, iy, iw, T.Text3);
                    iy += ph;
                    if (hasRaw) { Rule(pad + 1, iy + Dpi.S(8), cw - 2); iy += between; }
                }
                if (hasRaw) Wrap(model.EvRaw, fMono, pad + ip, iy, iw, T.Text);
                y += h + Dpi.S(12);
            }
            int bx = pad, rowH = Dpi.S(32);
            bool any = false;
            if (model.EvCanOpen) { bx += Button(bx, y, "Open in LogVice at this line", null, true, HitOpen, cw) + Dpi.S(8); any = true; }
            if (hasRaw)
            {
                int bw = ButtonWidth("Copy line", cw);
                if (bx > pad && bx + bw > pad + cw) { bx = pad; y += rowH + Dpi.S(8); }
                Button(bx, y, "Copy line", Copied(RawKey) ? "Copied" : null, false, HitCopyRaw, cw);
                any = true;
            }
            return any ? y + rowH : y - Dpi.S(12);
        }

        int ButtonWidth(string text, int maxW)
        {
            return Math.Min(maxW, TextW(text, fSemi) + Dpi.S(28));
        }

        // A button the size of its usual text; shown may be another word for a moment, such as Copied.
        int Button(int x, int y, string text, string shown, bool primary, int kind, int maxW)
        {
            int w = ButtonWidth(text, maxW), h = Dpi.S(32);
            bool isDown;
            bool isHot = Touch(new Rectangle(x, y, w, h), kind, null, out isDown);
            if (!Shown(y, h)) return w;
            RectangleF r = new RectangleF(x + 0.5f, y + dy + 0.5f, w - 1f, h - 1f);
            Color fg = T.Text;
            if (primary)
            {
                Shapes.Fill(G, r, Dpi.S(5), isDown ? Theme.Mix(T.AccentHover, Color.Black, 0.12f) : isHot ? T.AccentHover : T.Accent);
                fg = T.OnAccent;
            }
            else
            {
                Color bg = isDown ? T.Pressed : isHot ? T.Hover : Color.Empty;
                if (!bg.IsEmpty) Shapes.Fill(G, r, Dpi.S(5), bg);
                Shapes.Stroke(G, r, Dpi.S(5), isHot ? T.Text3 : T.Divider, 1f);
                if (shown != null) fg = T.OkFg;
            }
            TextRenderer.DrawText(G, shown ?? text, fSemi, O(x, y, w, h), fg, Centered | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return w;
        }
    }

    public sealed partial class MainForm
    {
        DetailView detailView;

        // From BuildViews: the card is what shows; the text box behind it keeps the same facts as plain text for Copy details.
        void AttachItemView(Panel detailHost)
        {
            detailView = new DetailView();
            detailView.Dock = DockStyle.Fill;
            detailView.CopyRequested += delegate (string s) { Safe.Run(delegate { Menus.Copy(LaunchGuard.Hide(s)); Status("Copied to the clipboard."); }); };
            detailView.OpenLogRequested += delegate { Safe.Run(delegate { OpenItemLog(list.SelectedItem); }); };
            detailView.SelectRequested += delegate (string key) { Safe.Run(delegate { list.SelectKey(key, true); }); };
            themed.Add(detailView);
            Control hidden = Padded(itemDetail);
            hidden.Visible = false;
            detailHost.Controls.Add(hidden);
            detailHost.Controls.Add(detailView);
            bItemLog.Primary = true;
            bItemCopy.Outline = true;
            if (bReport != null) bReport.Outline = true;
        }

        // Nothing selected: what to do, and what needs attention first.
        void ShowEmptyCard()
        {
            if (detailView == null) return;
            DetailModel m = new DetailModel();
            m.Empty = true;
            Session s = engine.S;
            if (s.Items.Count == 0)
            {
                m.EmptyTitle = "Waiting for a deployment";
                m.EmptyText = engine.Mode == Modes.Live ? "TrackVice is watching this device. Items show up here as soon as a task sequence, an SCCM install, Autopilot or an Intune app starts." : "Nothing was found in what was read.";
            }
            else
            {
                m.EmptyTitle = "Select an item to see what happened";
                m.EmptyText = "Select an item or a phase to see what happened, with the log line behind each step.";
                m.RowsTitle = "Needs attention";
                foreach (Finding f in s.Findings)
                {
                    DetailRow r = new DetailRow();
                    r.Key = f.ItemKey != null && s.Find(f.ItemKey) != null ? f.ItemKey : null;
                    r.Tag = f.SeverityName;
                    r.Tone = Sev(f.Severity);
                    r.Text = f.Title;
                    m.Rows.Add(r);
                }
            }
            detailView.SetModel(m);
        }

        void ShowPhaseCard(Phase p)
        {
            if (detailView == null) return;
            DateTime now = engine.Now;
            DetailModel m = new DetailModel();
            m.Key = "phase|" + p.Key;
            m.Title = p.Name;
            m.Tile = p.Done + "/" + p.Items.Count;
            State st = p.State;
            m.Pill = States.Label(st);
            m.PillTone = StateLook.Color(st, theme);
            m.Meta = p.Done + " of " + p.Items.Count + " done" + (p.FailedCount > 0 ? ", " + p.FailedCount + " failed" : "");
            m.ResultTitle = "Time";
            if (p.StartUtc.HasValue) m.Result.Add(Kv("Started", Txt.Local(p.StartUtc.Value), false));
            if (p.EndUtc.HasValue) m.Result.Add(Kv("Ended", Txt.Local(p.EndUtc.Value), false));
            string took = Txt.Duration(p.Duration(now));
            if (!string.IsNullOrEmpty(took)) m.Result.Add(Kv(p.EndUtc.HasValue ? "Took" : "Running for", took, false));
            if ((p.Key == Phases.DeviceSetup || p.Key == Phases.AccountSetup) && engine.S.GetInfo("ESP time limit (min)") != null)
                m.Result.Add(Kv("ESP time limit", engine.S.GetInfo("ESP time limit (min)") + " min", false));
            m.RowsTitle = "Items";
            foreach (Item i in p.Items)
            {
                DetailRow r = new DetailRow();
                r.Key = i.Key;
                r.Tag = States.Label(i.State);
                r.Tone = StateLook.Color(i.State, theme);
                r.Text = i.DisplayName;
                r.Right = Txt.Duration(i.Duration(now));
                m.Rows.Add(r);
            }
            detailView.SetModel(m);
        }

        void ShowItemCard(Item i, Phase ph)
        {
            if (detailView == null) return;
            DateTime now = engine.Now;
            DetailModel m = new DetailModel();
            m.Key = i.Key;
            m.Title = i.DisplayName;
            m.Tile = DetailText.KindCode(i.Kind);
            m.PillTone = i.Expected ? theme.Text3 : StateLook.Color(i.State, theme);
            m.Pill = StateLook.StateText(i);
            List<string> where = new List<string>();
            if (!string.IsNullOrEmpty(i.Kind)) where.Add(i.Kind);
            if (!string.IsNullOrEmpty(i.Origin)) where.Add(i.Origin);
            if (ph != null) where.Add(ph.Name);
            m.Meta = string.Join(" · ", where.ToArray());

            bool explained = false;
            foreach (Finding f in engine.S.Findings)
            {
                if (f.ItemKey != i.Key) continue;
                if (f.Severity >= 2) explained = true;
                AddFinding(m, f, i);
            }
            bool unexplained = !explained && i.ExitCode.HasValue && i.ExitCode.Value != 0;
            // A failure no finding explains still says what happened: the decoded exit code.
            if (unexplained && i.State == State.Failed) m.Callouts.Insert(0, Callout(DetailCallout.Tinted, "What happened", null, Errors.Describe(i.ExitCode.Value), theme.ErrFg));
            string noNet = NetworkOutages.NearItem(engine.S, i);
            if (noNet != null) m.Callouts.Add(Callout(DetailCallout.Note, "Network", null, noNet, theme.WarnFg));

            m.ResultTitle = States.IsActive(i.State) ? "Now" : i.Expected ? "Not on the device yet" : "Result";
            if (unexplained && i.State != State.Failed) m.Result.Add(Kv("Exit code", Errors.Describe(i.ExitCode.Value), false));
            string inst = i.Get("Installer exit code");
            if (!string.IsNullOrEmpty(inst)) m.Result.Add(Kv("Installer exit code", inst, true));
            if (States.IsActive(i.State) && i.Percent.HasValue)
            {
                DetailKv k = Kv("Progress", i.Percent.Value + " %", false);
                k.Bar = i.Percent.Value / 100.0;
                m.Result.Add(k);
            }
            if (i.StartUtc.HasValue) m.Result.Add(Kv("Started", Txt.Local(i.StartUtc.Value), false));
            if (i.EndUtc.HasValue) m.Result.Add(Kv("Ended", Txt.Local(i.EndUtc.Value), false));
            string took = Txt.Duration(i.Duration(now));
            if (!string.IsNullOrEmpty(took)) m.Result.Add(Kv(States.IsActive(i.State) ? "Running for" : "Took", took, false));
            if (!i.Expected && i.LastUtc > DateTime.MinValue) m.Result.Add(Kv("Last heard of", Txt.Local(i.LastUtc) + " (" + Txt.Ago(i.LastUtc, now) + ")", false));

            if (i.Attempts.Count > 0)
            {
                m.AttemptsTitle = i.Attempts.Count == 1 ? "Attempt" : i.Attempts.Count + " attempts";
                List<DateTime> restarts = new List<DateTime>();
                foreach (Item x in engine.S.Items) if (x.Kind == Kinds.Restart && x.StartUtc.HasValue) restarts.Add(x.StartUtc.Value);
                Attempt before = null;
                foreach (Attempt a in i.Attempts)
                {
                    DetailAttempt d = new DetailAttempt();
                    d.Start = a.StartUtc;
                    d.Open = !a.EndUtc.HasValue;
                    d.End = a.EndUtc.HasValue ? a.EndUtc.Value : (now > a.StartUtc ? now : a.StartUtc);
                    d.Tone = StateLook.Color(a.State, theme);
                    d.Label = DetailText.AttemptLabel(a.Index, d.End - d.Start, d.Open, States.Label(a.State), a.ExitCode, a.Reboot);
                    d.Times = Txt.LocalTime(a.StartUtc) + (a.EndUtc.HasValue ? " → " + Txt.LocalTime(a.EndUtc.Value) : " →");
                    d.RestartBefore = before != null && DetailText.RestartBetween(before.EndUtc ?? before.StartUtc, a.StartUtc, before.Reboot, restarts);
                    m.Attempts.Add(d);
                    before = a;
                }
            }

            foreach (Pair p in i.Info)
            {
                if (p.Key == "Installer exit code" || string.IsNullOrEmpty(p.Value)) continue;
                m.Details.Add(Kv(p.Key, p.Value, p.Key == "Command line" || p.Key.EndsWith("ID") || p.Key == "Content path" || p.Key == "Cache folder"));
            }

            // An expected item has no result yet: its details say why it is expected.
            if (i.Expected && m.Result.Count == 0) m.DetailsTitle = m.ResultTitle;

            m.EventsTitle = "Events" + (i.Events.Count > 40 ? " (newest 40 of " + i.Events.Count + ")" : "");
            int from = Math.Max(0, i.Events.Count - 40);
            for (int k = i.Events.Count - 1; k >= from; k--)
            {
                TEvent e = i.Events[k];
                DetailEvent d = new DetailEvent();
                d.Time = Txt.LocalTime(e.TimeUtc);
                d.Source = e.Source ?? "";
                d.Severity = e.Severity;
                d.Text = Txt.Short(e.Text, 400);
                m.Events.Add(d);
            }

            Evidence ev = i.LastEvidence;
            if (ev != null)
            {
                m.EvWhere = ev.Where;
                if (ev.Kind == EvidenceKinds.LogLine) m.EvPath = ev.Path;
                m.EvRaw = Txt.Short(ev.Text, 2000);
                m.EvCanOpen = i.LogFile != null;
            }
            detailView.SetModel(m);
        }

        // Values in the code font are IDs, paths and command lines: they can be copied on their own.
        static DetailKv Kv(string label, string value, bool mono)
        {
            DetailKv k = new DetailKv();
            k.Label = label;
            k.Value = value;
            k.Mono = mono;
            k.Copy = mono;
            return k;
        }

        static DetailCallout Callout(int kind, string label, string title, string text, Color tone)
        {
            DetailCallout c = new DetailCallout();
            c.Kind = kind;
            c.Label = label;
            c.Title = title;
            c.Text = text;
            c.Tone = tone;
            return c;
        }

        // What happened is the message, tinted by how bad it is; the likely reason and what to do come out of the same words.
        void AddFinding(DetailModel m, Finding f, Item i)
        {
            Color tone = f.Severity >= 1 ? Sev(f.Severity) : theme.Accent;
            string what, whyMsg, whyHint, todo;
            DetailText.SplitMessage(f.Message, out what, out whyMsg);
            DetailText.SplitHint(f.Hint, out whyHint, out todo);
            string title = f.Title == i.DisplayName + " failed" ? null : f.Title;
            if (string.IsNullOrEmpty(what)) { what = title; title = null; }
            if (!string.IsNullOrEmpty(what)) m.Callouts.Add(Callout(DetailCallout.Tinted, "What happened", title, what, tone));
            string why = whyMsg;
            if (!string.IsNullOrEmpty(whyHint)) why = string.IsNullOrEmpty(why) ? whyHint : why + " " + whyHint;
            if (!string.IsNullOrEmpty(why)) m.Callouts.Add(Callout(DetailCallout.Plain, "Likely why", null, why, theme.Text3));
            if (!string.IsNullOrEmpty(todo)) m.Callouts.Add(Callout(DetailCallout.Plain, "What to do", null, todo, theme.Accent));
        }
    }
}
