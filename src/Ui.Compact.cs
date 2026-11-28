namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Windows.Forms;

    // A small window that stays on top: the phase, what is installing now, overall progress and counts.
    public sealed class CompactView : Form
    {
        readonly CompactPanel panel = new CompactPanel();
        Theme theme;
        public event EventHandler FullView;

        public CompactView(Theme t)
        {
            theme = t;
            Text = "TrackVice";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(Dpi.S(460), Dpi.S(212));
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Dpi.S(24), wa.Bottom - Height - Dpi.S(24));
            panel.Dock = DockStyle.Fill;
            panel.DoubleClick += delegate { if (FullView != null) FullView(this, EventArgs.Empty); };
            Controls.Add(panel);
            Retheme(t);
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            KeyPreview = true;
        }

        public void Retheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            panel.ApplyTheme(t);
            if (IsHandleCreated) Draw.TitleBar(Handle, t);
        }

        // What items took on other devices, for the overall percentage. Null: the defaults by kind.
        public ExpectedTimes Expected;

        public void UpdateFrom(Engine e, bool watching)
        {
            panel.Expected = Expected;
            panel.Set(e, watching);
            Session s = e.S;
            Text = ((int)Math.Round(panel.Overall * 100)).ToString(CultureInfo.InvariantCulture) + " % · " + s.Computer;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape || keyData == (Keys.Control | Keys.M)) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    sealed class CompactPanel : FlatControl
    {
        public double Overall;
        public ExpectedTimes Expected;
        string[] left = new string[0];
        string device = "", kind = "", phase = "", itemName = "", stateText = "", counts = "", last = "", elapsed = "";
        State itemState = State.Unknown, overallState = State.Unknown;
        int? percent;
        float spin, ring;
        bool ringSet;
        readonly List<StripSeg> strip = new List<StripSeg>();
        readonly Timer anim = new Timer();

        public CompactPanel()
        {
            anim.Interval = 50;
            anim.Tick += delegate
            {
                spin = (spin + 14) % 360;
                if (States.IsActive(itemState)) Invalidate();
            };
            anim.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Dispose(); Motion.Cancel(this); }
            base.Dispose(disposing);
        }

        public void Set(Engine e, bool watching)
        {
            Session s = e.S;
            DateTime now = e.Now;
            device = s.Computer;
            kind = s.Items.Count > 0 ? s.Kind : (watching ? "Watching" : "Paused");
            int total = 0;
            foreach (Phase p in s.OrderedPhases) total += p.Items.Count;
            Overall = OverallProgress.Fraction(s, now, Expected);
            RingTo(Overall);
            strip.Clear();
            foreach (Phase sp in s.OrderedPhases)
            {
                StripSeg sg = new StripSeg();
                sg.Fraction = sp.Items.Count == 0 ? 0f : (float)sp.Done / sp.Items.Count;
                sg.State = sp.State;
                strip.Add(sg);
            }
            Item pick = null;
            foreach (Item i in s.Items) if (States.IsActive(i.State) && (pick == null || i.StateUtc > pick.StateUtc)) pick = i;
            if (pick == null) foreach (Item i in s.Items) if (pick == null || i.LastUtc > pick.LastUtc) pick = i;
            if (pick != null)
            {
                Phase ph = s.FindPhase(pick.PhaseKey);
                phase = ph != null ? ph.Name.ToUpperInvariant() + "  ·  " + ph.Done + " of " + ph.Items.Count : "";
                itemName = pick.DisplayName;
                itemState = pick.State;
                percent = pick.Percent;
                stateText = StateLook.StateText(pick);
                TimeSpan? d = pick.Duration(now);
                if (d.HasValue) stateText += "  ·  " + Txt.Duration(d);
                last = "";
                for (int k = pick.Events.Count - 1; k >= 0 && last.Length == 0; k--) last = Txt.Short(Clean(pick.Events[k].Text), 120);
            }
            else
            {
                phase = "";
                itemName = s.Items.Count == 0 ? "Waiting for a deployment" : "";
                itemState = State.Unknown;
                stateText = s.Items.Count == 0 ? "Nothing has started yet." : "";
                percent = null;
                last = "";
            }
            int failed = s.Count(State.Failed);
            overallState = failed > 0 ? State.Failed : s.CountActive > 0 ? State.Installing : total > 0 && s.CountComplete == total ? State.Succeeded : State.Pending;
            counts = s.CountComplete + " done  ·  " + s.CountActive + " running  ·  " + failed + " failed  ·  " + (s.Count(State.Pending) + s.Count(State.Unknown)) + " waiting";
            DeploymentEstimate est = null;
            try { est = DeploymentEstimate.Make(s, now, AutopilotFlow.BoxedTime(s, now), Expected); }
            catch (Exception) { est = null; }
            left = est != null ? est.Variants() : new string[0];
            DateTime? f = s.FirstUtc;
            elapsed = f.HasValue ? OffTime.Elapsed(s, now) : "";
            Invalidate();
        }

        static string Clean(string t)
        {
            t = (t ?? "").Trim();
            while (t.StartsWith("[") && t.IndexOf(']') > 0 && t.IndexOf(']') < 40) t = t.Substring(t.IndexOf(']') + 1).TrimStart();
            return t.TrimStart('+', ' ');
        }

        // The ring eases to the new value when the numbers change.
        void RingTo(double target)
        {
            if (!ringSet) { ring = (float)target; ringSet = true; return; }
            Motion.Animate(ring, (float)target, 180, Motion.EaseOut, delegate (float v) { ring = v; Invalidate(); }, this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Dpi.S(14), w = Width - pad * 2;
            TextFormatFlags ell = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            // Header: mark, device, deployment kind, time since start.
            Draw.Mark(g, new RectangleF(pad, Dpi.S(10), Dpi.S(18), Dpi.S(18)));
            int ew = elapsed.Length > 0 ? Dpi.S(80) : 0;
            TextRenderer.DrawText(g, device + "  ·  " + kind, Fonts.SmallSemibold, new Rectangle(pad + Dpi.S(26), Dpi.S(8), w - Dpi.S(26) - ew, Dpi.S(22)), T.Text2, ell);
            if (ew > 0) TextRenderer.DrawText(g, elapsed, Fonts.Small, new Rectangle(Width - pad - ew, Dpi.S(8), ew, Dpi.S(22)), T.Text3, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // The card with the ring and what runs now.
            Rectangle card = new Rectangle(pad, Dpi.S(36), w, Dpi.S(108));
            RectangleF cf = new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1f, card.Height - 1f);
            Shapes.Fill(g, cf, Dpi.S(12), T.Card);
            Shapes.Stroke(g, cf, Dpi.S(12), T.Divider, 1f);
            int rs = Dpi.S(76);
            RectangleF rr = new RectangleF(card.X + Dpi.S(14), card.Y + (card.Height - rs) / 2f, rs, rs);
            Color rc = overallState == State.Failed ? T.ErrFg : overallState == State.Succeeded ? T.OkFg : T.Accent;
            float rw = Dpi.S(7);
            RectangleF ri = new RectangleF(rr.X + rw / 2, rr.Y + rw / 2, rr.Width - rw, rr.Height - rw);
            using (Pen p = new Pen(Theme.Mix(T.Card, T.Text3, 0.28f), rw)) g.DrawEllipse(p, ri);
            float sweep = 360f * Math.Max(0f, Math.Min(1f, ring));
            if (sweep > 0.5f)
                using (Pen p = new Pen(rc, rw))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawArc(p, ri, -90f, Math.Min(sweep, 359.5f));
                }
            string pct = ((int)Math.Round(Overall * 100)).ToString(CultureInfo.InvariantCulture);
            TextRenderer.DrawText(g, pct, Fonts.Title, Rectangle.Round(new RectangleF(rr.X, rr.Y, rr.Width, rr.Height - Dpi.S(6))), T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "%", Fonts.Small, Rectangle.Round(new RectangleF(rr.X, rr.Y + rr.Height * 0.62f, rr.Width, Dpi.S(14))), T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            int tx = card.X + Dpi.S(14) + rs + Dpi.S(16), tw = card.Right - Dpi.S(14) - tx;
            bool bar = States.IsActive(itemState) && percent.HasValue && percent.Value > 0 && percent.Value < 100;
            int y = card.Y + (card.Height - Dpi.S(bar ? 90 : 80)) / 2;
            TextRenderer.DrawText(g, phase, Fonts.SmallSemibold, new Rectangle(tx, y, tw, Dpi.S(16)), T.Text3, ell);
            y += Dpi.S(18);
            StateLook.Icon(g, itemState, new RectangleF(tx, y + Dpi.S(3), Dpi.S(18), Dpi.S(18)), T, spin);
            TextRenderer.DrawText(g, itemName, Fonts.UiSemibold, new Rectangle(tx + Dpi.S(26), y, tw - Dpi.S(26), Dpi.S(24)), T.Text, ell);
            y += Dpi.S(26);
            TextRenderer.DrawText(g, stateText, Fonts.SmallSemibold, new Rectangle(tx, y, tw, Dpi.S(18)), StateLook.Color(itemState, T), ell);
            y += Dpi.S(20);
            if (bar)
            {
                StateLook.Bar(g, new RectangleF(tx, y + Dpi.S(2), Math.Min(Dpi.S(200), tw), Dpi.S(4)), percent.Value / 100.0, StateLook.Color(itemState, T), T.Divider);
                y += Dpi.S(10);
            }
            TextRenderer.DrawText(g, last, Fonts.Small, new Rectangle(tx, y, tw, Dpi.S(16)), T.Text3, ell);

            // Time left and the finish clock.
            int ly = card.Bottom + Dpi.S(10);
            string est = null;
            foreach (string v in left)
            {
                est = v;
                if (TextRenderer.MeasureText(v, Fonts.SmallSemibold, Size.Empty, TextFormatFlags.NoPadding).Width <= w) break;
            }
            if (est != null) TextRenderer.DrawText(g, est, Fonts.SmallSemibold, new Rectangle(pad, ly, w, Dpi.S(18)), T.Text, ell);

            // The thin phase strip: one rounded segment per phase.
            int sy = ly + Dpi.S(26), n = strip.Count;
            if (n > 0)
            {
                int gap = Dpi.S(3);
                float sw = (w - gap * (n - 1)) / (float)n;
                for (int k = 0; k < n; k++)
                {
                    RectangleF seg = new RectangleF(pad + k * (sw + gap), sy, sw, Dpi.S(5));
                    Shapes.Fill(g, seg, Dpi.S(2.5f), T.Divider);
                    float f = strip[k].Fraction;
                    Color c = strip[k].State == State.Failed ? T.ErrFg : strip[k].State == State.Succeeded ? T.OkFg : T.Accent;
                    if (f > 0.01f) Shapes.Fill(g, new RectangleF(seg.X, seg.Y, Math.Max(seg.Height, seg.Width * f), seg.Height), Dpi.S(2.5f), c);
                }
            }
            TextRenderer.DrawText(g, counts, Fonts.Small, new Rectangle(pad, sy + Dpi.S(9), w, Dpi.S(16)), T.Text3, ell);
        }
    }

    struct StripSeg
    {
        public float Fraction;
        public State State;
    }
}
