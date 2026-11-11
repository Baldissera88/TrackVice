namespace TrackVice
{
    using System;
    using System.Drawing;
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
            ClientSize = new Size(Dpi.S(480), Dpi.S(236));
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

        public void UpdateFrom(Engine e, bool watching)
        {
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
        string device = "", kind = "", phase = "", itemName = "", stateText = "", counts = "", last = "", elapsed = "";
        State itemState = State.Unknown, overallState = State.Unknown;
        int? percent;
        float spin;
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
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }

        public void Set(Engine e, bool watching)
        {
            Session s = e.S;
            DateTime now = e.Now;
            device = s.Computer;
            kind = s.Items.Count > 0 ? s.Kind : (watching ? "Watching" : "Paused");
            double prog = 0;
            int total = 0;
            foreach (Phase p in s.OrderedPhases) { prog += p.Progress * p.Items.Count; total += p.Items.Count; }
            Overall = total == 0 ? 0 : prog / total;
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
            DateTime? f = s.FirstUtc;
            elapsed = f.HasValue ? Txt.DurationShort(now - f.Value) : "";
            Invalidate();
        }

        static string Clean(string t)
        {
            t = (t ?? "").Trim();
            while (t.StartsWith("[") && t.IndexOf(']') > 0 && t.IndexOf(']') < 40) t = t.Substring(t.IndexOf(']') + 1).TrimStart();
            return t.TrimStart('+', ' ');
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            int pad = Dpi.S(18), w = Width - pad * 2;
            Draw.Mark(g, new RectangleF(pad, Dpi.S(14), Dpi.S(22), Dpi.S(22)));
            TextRenderer.DrawText(g, device + "  ·  " + kind, Fonts.SmallSemibold, new Rectangle(pad + Dpi.S(30), Dpi.S(14), w - Dpi.S(110), Dpi.S(22)), T.Text2, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (elapsed.Length > 0) TextRenderer.DrawText(g, elapsed, Fonts.Small, new Rectangle(Width - pad - Dpi.S(90), Dpi.S(14), Dpi.S(90), Dpi.S(22)), T.Text3, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int y = Dpi.S(50);
            TextRenderer.DrawText(g, phase, Fonts.SmallSemibold, new Rectangle(pad, y, w, Dpi.S(16)), T.Text3, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += Dpi.S(20);
            StateLook.Icon(g, itemState, new RectangleF(pad, y + Dpi.S(4), Dpi.S(22), Dpi.S(22)), T, spin);
            TextRenderer.DrawText(g, itemName, Fonts.Title, new Rectangle(pad + Dpi.S(32), y, w - Dpi.S(32), Fonts.Title.Height + 2), T.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            y += Fonts.Title.Height + Dpi.S(4);
            TextRenderer.DrawText(g, stateText, Fonts.UiSemibold, new Rectangle(pad + Dpi.S(32), y, w - Dpi.S(32), Fonts.Ui.Height + 2), StateLook.Color(itemState, T), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += Fonts.Ui.Height + Dpi.S(2);
            if (States.IsActive(itemState) && percent.HasValue && percent.Value > 0 && percent.Value < 100)
            {
                StateLook.Bar(g, new RectangleF(pad + Dpi.S(32), y + Dpi.S(2), Math.Min(Dpi.S(220), w - Dpi.S(32)), Dpi.S(4)), percent.Value / 100.0, StateLook.Color(itemState, T), T.Divider);
                y += Dpi.S(10);
            }
            TextRenderer.DrawText(g, last, Fonts.Small, new Rectangle(pad + Dpi.S(32), y, w - Dpi.S(32), Fonts.Small.Height + 2), T.Text3, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            int by = Height - Dpi.S(58);
            string pct = ((int)Math.Round(Overall * 100)).ToString(CultureInfo.InvariantCulture) + " %";
            TextRenderer.DrawText(g, pct, Fonts.UiSemibold, new Rectangle(Width - pad - Dpi.S(60), by - Dpi.S(4), Dpi.S(60), Dpi.S(20)), T.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            StateLook.Bar(g, new RectangleF(pad, by + Dpi.S(3), w - Dpi.S(70), Dpi.S(6)), Overall, overallState == State.Failed ? T.ErrFg : overallState == State.Succeeded ? T.OkFg : T.Accent, T.Divider);
            TextRenderer.DrawText(g, counts, Fonts.Small, new Rectangle(pad, by + Dpi.S(20), w, Dpi.S(18)), T.Text3, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}
