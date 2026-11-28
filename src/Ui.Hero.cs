namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    // Shared bits of the top of a device: the card size, the larger fonts, the card look, the ring and the short motion.
    public static class Hero
    {
        // The overall card, the stat chips and the support buttons beside them are this high.
        public static int CardHeight { get { return Dpi.S(88); } }
        // The room between the cards, across and down.
        public static int Gap { get { return Dpi.S(12); } }
        public static int Radius { get { return Dpi.S(12); } }

        static readonly Dictionary<string, Font> scaled = new Dictionary<string, Font>();

        // The same family and style as basis, k times as large; made once.
        public static Font Scaled(Font basis, float k)
        {
            string key = basis.Name + "|" + basis.Size.ToString("R", CultureInfo.InvariantCulture) + "|" + (int)basis.Style + "|" + (int)basis.Unit + "|" + k.ToString("R", CultureInfo.InvariantCulture);
            Font f;
            if (!scaled.TryGetValue(key, out f))
            {
                f = new Font(basis.FontFamily, basis.Size * k, basis.Style, basis.Unit);
                scaled[key] = f;
            }
            return f;
        }

        // The large numbers, about 22 px, in the theme's font for numbers.
        public static Font Number(Theme t)
        {
            return Scaled(t.Num(Fonts.Title), 1.18f);
        }

        // The time left on the overall card, about 15 px.
        public static Font Lead
        {
            get { return Scaled(Fonts.UiSemibold, 1.15f); }
        }

        // The size of the font in pixels, and from the top of what TextRenderer draws to the baseline.
        public static float Em(Graphics g, Font f)
        {
            return f.SizeInPoints * g.DpiY / 72f;
        }

        public static float Ascent(Graphics g, Font f)
        {
            try
            {
                FontFamily ff = f.FontFamily;
                return Em(g, f) * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
            }
            catch (Exception) { return Em(g, f) * 0.92f; }
        }

        // A card: one step lighter than the window with a hairline; lit is the hover, from 0 to 1. Selected has a soft
        // accent and an accent border.
        public static void Card(Graphics g, RectangleF r, Theme t, float lit, bool down, bool selected)
        {
            Color fill = selected ? Theme.Mix(t.Card, t.Accent, t.IsDark ? 0.12f : 0.06f) : t.Card;
            if (down) fill = selected ? Theme.Mix(fill, t.Accent, 0.06f) : t.Pressed;
            else if (lit > 0) fill = Theme.Mix(fill, selected ? Theme.Mix(fill, t.Accent, 0.06f) : t.Hover, lit);
            Shapes.Fill(g, r, Radius, fill);
            if (selected) Shapes.Stroke(g, Inset(r, 0.25f), Radius, t.Accent, 1.5f);
            else Shapes.Stroke(g, r, Radius, Theme.Mix(t.Divider, t.Text3, 0.5f * lit), 1f);
        }

        public static RectangleF Inset(RectangleF r, float d)
        {
            return new RectangleF(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d);
        }

        // A ring from the top, clockwise, with round caps, over a full ring as the track.
        public static void Ring(Graphics g, RectangleF r, double value, float width, Color fill, Color track)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF a = new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width);
            using (Pen p = new Pen(track, width)) g.DrawEllipse(p, a);
            value = Math.Max(0, Math.Min(1, value));
            if (value > 0.0005)
            {
                using (Pen p = new Pen(fill, width))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    if (value >= 0.9995) g.DrawEllipse(p, a);
                    else g.DrawArc(p, a, -90f, (float)Math.Max(1.0, value * 360.0));
                }
            }
            g.SmoothingMode = old;
        }

        [DllImport("user32.dll")]
        static extern bool SystemParametersInfo(int action, int param, ref int value, int winIni);
        const int GetClientAreaAnimation = 0x1042;
        static bool animate = true, known;
        static int checkedAt;

        // Motion is short and off when Windows shows no animations. Asked again every few seconds.
        public static bool Animate
        {
            get
            {
                int now = Environment.TickCount;
                if (known && unchecked(now - checkedAt) < 5000) return animate;
                known = true;
                checkedAt = now;
                bool on = true;
                try { on = SystemInformation.UIEffectsEnabled; }
                catch (Exception) { }
                if (on && Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    try
                    {
                        int v = 1;
                        if (SystemParametersInfo(GetClientAreaAnimation, 0, ref v, 0)) on = v != 0;
                    }
                    catch (Exception) { }
                }
                animate = on;
                return on;
            }
        }

        // A value that moves to a new one in a short time, easing out, and repaints its control on the way. The first
        // value, a tiny step, and every value when Windows shows no animations, is taken at once.
        public sealed class Glide : IDisposable
        {
            readonly Control owner;
            readonly int ms;
            readonly Timer timer = new Timer();
            double from, to, now;
            int t0;
            bool set;

            public Glide(Control owner, int ms)
            {
                this.owner = owner;
                this.ms = ms;
                timer.Interval = 15;
                timer.Tick += delegate { Step(); };
            }

            public double Value { get { return now; } }
            public double Target { get { return to; } }

            public void To(double v)
            {
                if (set && v == to) return;
                bool first = !set;
                set = true;
                to = v;
                if (first || Math.Abs(v - now) < 0.002 || !Animate || !owner.IsHandleCreated || !owner.Visible)
                {
                    timer.Stop();
                    now = v;
                    owner.Invalidate();
                    return;
                }
                from = now;
                t0 = Environment.TickCount;
                timer.Start();
            }

            void Step()
            {
                double k = Math.Min(1.0, unchecked(Environment.TickCount - t0) / (double)ms);
                double u = 1 - k;
                now = from + (to - from) * (1 - u * u * u);
                if (k >= 1.0)
                {
                    now = to;
                    timer.Stop();
                }
                owner.Invalidate();
            }

            public void Dispose()
            {
                timer.Dispose();
            }
        }
    }

    // The overall progress as the hero card: a ring with the percent, and beside it the state, the time left, the finish
    // and the time so far.
    public sealed class OverallChip : FlatControl
    {
        readonly Hero.Glide ring, lit;
        public string Elapsed = "";
        public State State = State.Unknown;
        public string Label = "Overall";
        HeroText words = new HeroText();
        const TextFormatFlags One = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        public OverallChip()
        {
            Size = new Size(Dpi.S(300), Hero.CardHeight);
            TabStop = false;
            ring = new Hero.Glide(this, 180);
            lit = new Hero.Glide(this, 120);
            lit.To(0);
        }

        // The share done, 0 to 1. The ring moves to a new value in a moment; the first is drawn at once.
        public double Progress
        {
            get { return ring.Target; }
            set { ring.To(Math.Max(0, Math.Min(1, value))); }
        }

        // The time left in words. True when it changed, so the chips are laid out again.
        public bool SetWords(HeroText h)
        {
            if (h == null) h = new HeroText();
            if (words.Same(h)) return false;
            words = h;
            Invalidate();
            return true;
        }

        static int RingSize { get { return Dpi.S(64); } }
        static int Pad { get { return Dpi.S(16); } }
        static int TextX { get { return Pad + RingSize + Dpi.S(14); } }

        string StateText
        {
            get { return State == State.Unknown ? "" : States.Label(State); }
        }

        // Without an estimate the time so far is the large line.
        string[] Leads
        {
            get { return words.Leads.Length > 0 ? words.Leads : Elapsed.Length > 0 ? new string[] { Elapsed } : new string[0]; }
        }

        // The label and the time so far, then the time alone.
        string[] Foots
        {
            get
            {
                if (words.Leads.Length == 0) return new string[] { Elapsed.Length > 0 ? Label + " time" : Label };
                if (Elapsed.Length == 0) return new string[] { Label };
                return new string[] { Label + " · " + Elapsed, Elapsed };
            }
        }

        static int W(string s, Font f)
        {
            return string.IsNullOrEmpty(s) ? 0 : TextRenderer.MeasureText(s, f, Size.Empty, One).Width;
        }

        // The width for all the words beside the ring.
        public int NaturalWidth
        {
            get
            {
                int w = Dpi.S(120);
                w = Math.Max(w, W(StateText, Fonts.SmallSemibold));
                string[] leads = Leads;
                if (leads.Length > 0) w = Math.Max(w, W(leads[0], Hero.Lead));
                if (words.Details.Length > 0) w = Math.Max(w, W(words.Details[0], Fonts.Small));
                // Room for the time so far to grow by a digit before the next layout.
                w = Math.Max(w, W(Foots[0], Fonts.Small) + Dpi.S(8));
                return TextX + w + Pad;
            }
        }

        // The shorter texts fit in this, the lead in a smaller font at the least.
        public int MinWidth
        {
            get { return TextX + Dpi.S(96) + Pad; }
        }

        Color RingColor()
        {
            if (State == State.Failed) return T.ErrFg;
            if (State == State.Succeeded || ring.Target >= 0.9995) return T.OkFg;
            if (words.Over) return T.WarnFg;
            return T.Accent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Hero.Card(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), T, (float)lit.Value, down, false);
            Color rc = RingColor();
            int rs = RingSize;
            RectangleF rr = new RectangleF(Pad, (Height - rs) / 2f, rs, rs);
            Hero.Ring(g, rr, ring.Value, Dpi.S(6), rc, Theme.Mix(T.Card, rc, T.IsDark ? 0.2f : 0.15f));
            Percent(g, rr);
            Words(g);
        }

        // The percent in the middle of the ring, the digits large and the sign small on the same baseline.
        void Percent(Graphics g, RectangleF rr)
        {
            string num = ((int)Math.Round(ring.Value * 100)).ToString(CultureInfo.InvariantCulture);
            Font nf = Hero.Number(T), pf = Fonts.SmallSemibold;
            int nw = TextRenderer.MeasureText(g, num, nf, Size.Empty, One).Width;
            int pw = TextRenderer.MeasureText(g, "%", pf, Size.Empty, One).Width;
            int gap = Dpi.S(1);
            float x = rr.X + (rr.Width - nw - gap - pw) / 2f;
            float baseline = rr.Y + rr.Height / 2f + Hero.Em(g, nf) * 0.35f;
            TextRenderer.DrawText(g, num, nf, new Point((int)Math.Round(x), (int)Math.Round(baseline - Hero.Ascent(g, nf))), T.Text, One);
            TextRenderer.DrawText(g, "%", pf, new Point((int)Math.Round(x + nw + gap), (int)Math.Round(baseline - Hero.Ascent(g, pf))), T.Text3, One);
        }

        // The first text of each line that fits; the lead in a smaller font before it is cut. A card with room to spare
        // has the time so far on the right, on the lines of the state and the time left.
        void Words(Graphics g)
        {
            int tx = TextX, room = Math.Max(0, Width - tx - Pad + Dpi.S(4));
            Font lf = Hero.Lead, sf = Fonts.Small;
            string[] leads = Leads;
            string sideLabel = Label + " time";
            int sideW = Math.Max(W(sideLabel, sf), W(Elapsed, lf));
            bool side = words.Leads.Length > 0 && Elapsed.Length > 0
                && Width >= tx + Math.Max(W(StateText, Fonts.SmallSemibold), Math.Max(W(leads[0], lf), W(words.Details.Length > 0 ? words.Details[0] : "", sf))) + Dpi.S(40) + sideW + Pad + Dpi.S(4);
            if (side) room -= sideW + Dpi.S(40);
            string st = Fit.Text(StateText, Fonts.SmallSemibold, room);
            string lead = "";
            foreach (string l in leads)
                if (W(l, lf) <= room) { lead = l; break; }
            if (lead.Length == 0 && leads.Length > 0)
            {
                lf = Fonts.UiSemibold;
                lead = Fit.Text(leads[leads.Length - 1], lf, room);
            }
            string detail = "";
            if (words.Leads.Length > 0)
                foreach (string d in words.Details)
                    if (W(d, sf) <= room) { detail = d; break; }
            string foot = "";
            if (!side)
            {
                foreach (string f in Foots)
                {
                    foot = f;
                    if (W(f, sf) <= room) break;
                }
                foot = Fit.Text(foot, sf, room);
            }
            int h1 = st.Length > 0 ? Fonts.SmallSemibold.Height + Dpi.S(1) : 0;
            int h2 = lead.Length > 0 ? lf.Height + Dpi.S(2) : 0;
            int h3 = detail.Length > 0 ? sf.Height : 0;
            int h4 = foot.Length > 0 ? sf.Height : 0;
            int y = (Height - h1 - h2 - h3 - h4) / 2;
            if (side)
            {
                int right = Width - Pad - Dpi.S(4);
                TextRenderer.DrawText(g, sideLabel, sf, new Point(right - W(sideLabel, sf), y + (h1 > 0 ? (Fonts.SmallSemibold.Height - sf.Height) / 2 : 0)), T.Text3, One);
                TextRenderer.DrawText(g, Elapsed, lf, new Point(right - W(Elapsed, lf), y + h1), T.Text2, One);
            }
            if (h1 > 0) { TextRenderer.DrawText(g, st, Fonts.SmallSemibold, new Point(tx, y), StateLook.Color(State, T), One); y += h1; }
            if (h2 > 0) { TextRenderer.DrawText(g, lead, lf, new Point(tx, y), words.Over ? T.WarnFg : T.Text, One); y += h2; }
            if (h3 > 0) { TextRenderer.DrawText(g, detail, sf, new Point(tx, y), T.Text2, One); y += h3; }
            if (h4 > 0) TextRenderer.DrawText(g, foot, sf, new Point(tx, y), T.Text3, One);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            lit.To(1);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            lit.To(0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ring.Dispose();
                lit.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // A count that also works as a filter: a small dot in the colour of what it counts and the label, the number large
    // under them. The number has a colour only when it matters: failed red and findings amber when there are any.
    public sealed class StatChip : FlatControl
    {
        public int Count;
        public string Label;
        // 0 a plain count, 1 amber when there are any (findings), 2 red when there are any (failed).
        public int Tone;
        public bool Selected;
        public int NaturalWidth;
        readonly Hero.Glide lit;

        public StatChip(string label, int tone)
        {
            Label = label;
            Tone = tone;
            Cursor = Cursors.Hand;
            lit = new Hero.Glide(this, 120);
            lit.To(0);
            NaturalWidth = Math.Max(Dpi.S(120), LabelWidth + Dpi.S(16) * 2 + Dpi.S(8));
            Size = new Size(NaturalWidth, Hero.CardHeight);
        }

        static int DotSize { get { return Dpi.S(7); } }

        int LabelWidth
        {
            get { return DotSize + Dpi.S(7) + TextRenderer.MeasureText(Label, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width; }
        }

        // The smallest width: the number or the label with its dot, whichever is wider, and the room around it.
        public int MinWidth
        {
            get
            {
                int num = TextRenderer.MeasureText(Count.ToString(CultureInfo.InvariantCulture), Hero.Number(T), Size.Empty, TextFormatFlags.NoPadding).Width;
                return Math.Min(NaturalWidth, Math.Max(Dpi.S(76), Math.Max(num, LabelWidth) + Dpi.S(24)));
            }
        }

        // In progress is the accent, waiting a grey ring, failed red, findings amber, the rest grey; dim at none.
        void Dot(out Color c, out bool ring)
        {
            ring = false;
            if (Tone == 2) c = T.ErrFg;
            else if (Tone == 1) c = T.WarnFg;
            else if (Label == "In progress") c = T.Accent;
            else if (Label == "Waiting") { c = T.Text3; ring = true; }
            else c = T.Text2;
            if (Count == 0 && !ring) c = Theme.Mix(T.Card, c, 0.45f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Hero.Card(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), T, (float)lit.Value, down, Selected);
            Color dot;
            bool ring;
            Dot(out dot, out ring);
            Color num = Count == 0 ? T.Text3 : Tone == 2 ? T.ErrFg : Tone == 1 ? T.WarnFg : T.Text;
            int pad = Width < Dpi.S(104) ? Dpi.S(12) : Dpi.S(16);
            Font nf = Hero.Number(T);
            const TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int labH = Fonts.Small.Height, numH = nf.Height, gap = Dpi.S(3);
            int y = (Height - labH - gap - numH) / 2;
            float d = DotSize;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF dr = new RectangleF(pad, y + (labH - d) / 2f, d, d);
            if (ring) using (Pen p = new Pen(dot, Dpi.S(1.5f))) g.DrawEllipse(p, Hero.Inset(dr, Dpi.S(0.75f)));
            else using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, dr);
            g.SmoothingMode = old;
            int lx = pad + (int)d + Dpi.S(7);
            TextRenderer.DrawText(g, Label, Fonts.Small, new Rectangle(lx, y, Math.Max(0, Width - lx - pad + Dpi.S(6)), labH), Selected ? T.Text : T.Text2, f);
            TextRenderer.DrawText(g, Count.ToString(CultureInfo.InvariantCulture), nf, new Rectangle(pad - Dpi.S(1), y + labH + gap, Math.Max(0, Width - pad * 2 + Dpi.S(2)), numH), num, f);
            if (Focused && ShowFocusCues) Shapes.Stroke(g, new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), Hero.Radius - Dpi.S(2), T.Accent, 1.5f);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            lit.To(1);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            lit.To(0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) lit.Dispose();
            base.Dispose(disposing);
        }
    }

    // The phases as connected steps: a thin line from tile to tile, each tile with its glyph, name, count and time and a
    // thin bar. The phase that runs now has an accent border; a failure is red on the glyph and the bar only. Click a
    // tile to jump to the phase in the list.
    public sealed class PhaseRail : FlatControl
    {
        List<Phase> phases = new List<Phase>();
        readonly List<RectangleF> hits = new List<RectangleF>();
        readonly List<Hero.Glide> lit = new List<Hero.Glide>();
        readonly ToolTip tip = new ToolTip();
        Func<DateTime> now = delegate { return DateTime.UtcNow; };
        public string Selected;
        int hot = -1, scroll;
        float spin;
        public event Action<Phase> PhaseClicked;
        public string EmptyText = "Phases show up here as soon as a deployment starts.";
        const TextFormatFlags One = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        public PhaseRail()
        {
            Height = Dpi.S(84);
            Cursor = Cursors.Hand;
        }

        public void SetData(IEnumerable<Phase> list, Func<DateTime> clock)
        {
            phases = new List<Phase>(list);
            now = clock ?? now;
            if (hot >= phases.Count) Hot(-1);
            Invalidate();
        }

        public void Tick(float angle)
        {
            spin = angle;
            foreach (Phase p in phases) if (Running(p)) { Invalidate(); return; }
        }

        static bool Running(Phase p)
        {
            foreach (Item i in p.Items) if (States.IsActive(i.State)) return true;
            return false;
        }

        Hero.Glide Lit(int i)
        {
            while (lit.Count <= i)
            {
                Hero.Glide gl = new Hero.Glide(this, 120);
                gl.To(0);
                lit.Add(gl);
            }
            return lit[i];
        }

        // The glyph, the name and the first line of every tile sit on this line; the connectors too.
        static float GlyphMid { get { return Dpi.S(6) + Dpi.S(13) + Dpi.S(9); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            hits.Clear();
            int pad = Dpi.S(18), gap = Dpi.S(24);
            float y = Dpi.S(6), h = Height - Dpi.S(14);
            if (phases.Count == 0)
            {
                RectangleF er = new RectangleF(pad + 0.5f, y + 0.5f, Width - pad * 2 - 1, h - 1);
                using (GraphicsPath path = Shapes.Round(er, Hero.Radius))
                using (Pen pen = new Pen(T.Divider, 1f))
                {
                    pen.DashPattern = new float[] { 4f, 3f };
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, EmptyText, Fonts.Ui, Rectangle.Round(er), T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            int n = phases.Count;
            float avail = Width - pad * 2 - gap * (n - 1);
            if (avail / n < Dpi.S(170))
            {
                gap = Dpi.S(16);
                avail = Width - pad * 2 - gap * (n - 1);
            }
            float w = Math.Max(Dpi.S(112), Math.Min(Dpi.S(420), avail / n));
            float total = w * n + gap * (n - 1);
            int maxScroll = (int)Math.Max(0, total - (Width - pad * 2));
            scroll = Math.Max(0, Math.Min(scroll, maxScroll));
            float x0 = pad - scroll;
            float cy = GlyphMid;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // The connectors first: green-tinted after a phase that is done.
            for (int i = 0; i < n - 1; i++)
            {
                float a = x0 + (w + gap) * i + w, b = a + gap;
                Color c = States.IsComplete(phases[i].State) ? Theme.Mix(T.Divider, T.OkFg, 0.55f) : T.Divider;
                using (Pen pen = new Pen(c, Dpi.S(1.5f))) g.DrawLine(pen, a - 1, cy, b + 1, cy);
            }
            for (int i = 0; i < n; i++)
            {
                RectangleF r = new RectangleF(x0 + (w + gap) * i, y, w, h);
                hits.Add(r);
                if (r.Right < 0 || r.X > Width) continue;
                Tile(g, phases[i], r, i);
            }
            if (maxScroll > 0)
            {
                // Fade the edges so it is clear that more phases are off screen.
                if (scroll > 0) Fade(g, new Rectangle(0, 0, pad + Dpi.S(20), Height), true);
                if (scroll < maxScroll) Fade(g, new Rectangle(Width - pad - Dpi.S(20), 0, pad + Dpi.S(20), Height), false);
            }
        }

        void Tile(Graphics g, Phase p, RectangleF r, int i)
        {
            State st = p.State;
            bool running = Running(p), sel = p.Key == Selected;
            float hv = i < lit.Count ? (float)lit[i].Value : 0f;
            Color fill = sel ? Theme.Mix(T.Card, T.Accent, T.IsDark ? 0.09f : 0.045f) : T.Card;
            if (down && i == hot) fill = T.Pressed;
            else if (hv > 0) fill = Theme.Mix(fill, sel ? Theme.Mix(fill, T.Accent, 0.05f) : T.Hover, hv);
            Shapes.Fill(g, r, Hero.Radius, fill);
            if (running) Shapes.Stroke(g, Hero.Inset(r, 0.25f), Hero.Radius, T.Accent, 1.5f);
            else Shapes.Stroke(g, r, Hero.Radius, sel ? Theme.Mix(T.Divider, T.Accent, 0.5f) : Theme.Mix(T.Divider, T.Text3, 0.5f * hv), 1f);

            int ix = (int)(r.X + Dpi.S(14));
            float isz = Dpi.S(18);
            StateLook.Icon(g, st, new RectangleF(ix, GlyphMid - isz / 2, isz, isz), T, spin);
            int tx = (int)(ix + isz + Dpi.S(8));
            int right = (int)(r.Right - Dpi.S(14));
            Font nf = Fonts.UiSemibold;
            int nh = nf.Height;
            TextRenderer.DrawText(g, Fit.Text(p.Name, nf, Math.Max(0, right - tx)), nf, new Point(tx, (int)Math.Round(GlyphMid - nh / 2f)), T.Text, One);

            // "8 of 10 · 1 failed · 5 min": the time goes first when it does not fit, then the failures, then the count is cut.
            int sx = r.Width >= Dpi.S(150) ? tx : ix;
            int sy = (int)(r.Y + Dpi.S(36));
            Font sf = T.Num(Fonts.Small), bf = Fonts.SmallSemibold;
            string count = p.Done + " of " + p.Items.Count;
            string bad = p.FailedCount > 0 ? p.FailedCount + " failed" : "";
            string dur = Txt.DurationShort(p.Duration(now()));
            int room = Math.Max(0, right - sx);
            int cw = Mw(count, sf), bw = Mw(bad, bf), tw = Mw(dur, sf);
            int dw = Mw("\u00b7", sf) + Dpi.S(4) * 2;
            bool showBad = bad.Length > 0 && cw + dw + bw <= room;
            bool showDur = !string.IsNullOrEmpty(dur) && (bad.Length == 0 || showBad) && cw + (showBad ? dw + bw : 0) + dw + tw <= room;
            int x = sx;
            TextRenderer.DrawText(g, Fit.Text(count, sf, room), sf, new Point(x, sy), T.Text3, One);
            x += cw;
            if (showBad)
            {
                TextRenderer.DrawText(g, "\u00b7", sf, new Point(x + Dpi.S(4), sy), T.Text3, One);
                x += dw;
                TextRenderer.DrawText(g, bad, bf, new Point(x, sy + (sf.Height - bf.Height) / 2), T.ErrFg, One);
                x += bw;
            }
            if (showDur)
            {
                TextRenderer.DrawText(g, "\u00b7", sf, new Point(x + Dpi.S(4), sy), T.Text3, One);
                TextRenderer.DrawText(g, dur, sf, new Point(x + dw, sy), T.Text3, One);
            }

            Color bar = st == State.Failed ? T.ErrFg : running ? T.Accent : st == State.WaitingReboot ? T.WarnFg
                : States.IsComplete(st) ? T.OkFg : Theme.Mix(T.Card, T.Text3, 0.7f);
            StateLook.Bar(g, new RectangleF(r.X + Dpi.S(14), r.Bottom - Dpi.S(14), r.Width - Dpi.S(28), Dpi.S(3)), p.Progress, bar, Theme.Mix(T.Card, T.Divider, T.IsDark ? 0.9f : 0.8f));
        }

        static int Mw(string s, Font f)
        {
            return string.IsNullOrEmpty(s) ? 0 : TextRenderer.MeasureText(s, f, Size.Empty, One).Width;
        }

        string TipText(Phase p)
        {
            string s = p.Name + "\n" + p.Done + " of " + p.Items.Count + " done";
            if (p.FailedCount > 0) s += " · " + p.FailedCount + " failed";
            string dur = Txt.DurationShort(p.Duration(now()));
            if (!string.IsNullOrEmpty(dur)) s += " · " + dur;
            if (Running(p)) s += "\nRunning now";
            return s;
        }

        void Fade(Graphics g, Rectangle r, bool left)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(r, left ? T.Bg : Color.FromArgb(0, T.Bg), left ? Color.FromArgb(0, T.Bg) : T.Bg, LinearGradientMode.Horizontal))
                g.FillRectangle(b, r);
        }

        void Hot(int h)
        {
            if (h == hot) return;
            if (hot >= 0) Lit(hot).To(0);
            hot = h;
            if (hot >= 0) Lit(hot).To(1);
            tip.SetToolTip(this, hot >= 0 && hot < phases.Count ? TipText(phases[hot]) : null);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < hits.Count; i++) if (hits[i].Contains(e.Location)) h = i;
            Hot(h);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            Hot(-1);
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta / 120 * Dpi.S(120);
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            for (int i = 0; i < hits.Count && i < phases.Count; i++)
            {
                if (!hits[i].Contains(e.Location)) continue;
                if (PhaseClicked != null) PhaseClicked(phases[i]);
            }
            base.OnMouseClick(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tip.Dispose();
                foreach (Hero.Glide gl in lit) gl.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
