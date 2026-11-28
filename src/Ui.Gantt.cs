namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // The Gantt tab: a bar with the two filters above the chart.
    public sealed class GanttPage : Panel, IThemed
    {
        public readonly GanttChart Chart = new GanttChart();
        readonly Panel bar = new Panel();
        readonly FlowLayoutPanel flow = new FlowLayoutPanel();
        readonly Panel card = new Panel();
        readonly FlatButton bPolicies = new FlatButton("Policies", null);
        readonly FlatButton bTimed = new FlatButton("Only what took time", null);
        readonly FlatButton bOff = new FlatButton("Collapse time off", null);

        public event Action<Item> ItemClicked;

        public GanttPage()
        {
            Dock = DockStyle.Fill;
            bar.Dock = DockStyle.Top;
            bar.Height = Dpi.S(42);
            bar.Padding = new Padding(0, 0, 0, Dpi.S(8));
            flow.Dock = DockStyle.Fill;
            flow.WrapContents = false;
            bar.Controls.Add(flow);
            foreach (FlatButton b in new FlatButton[] { bPolicies, bTimed, bOff })
            {
                b.Compact = true;
                b.AutoWidth();
                b.Margin = new Padding(0, Dpi.S(2), Dpi.S(4), 0);
                flow.Controls.Add(b);
            }
            bPolicies.Click += Safe.Wrap(delegate { Chart.ShowPolicies = !Chart.ShowPolicies; bPolicies.Checked = Chart.ShowPolicies; bPolicies.Invalidate(); });
            bTimed.Click += Safe.Wrap(delegate { Chart.OnlyTimed = !Chart.OnlyTimed; bTimed.Checked = Chart.OnlyTimed; bTimed.Invalidate(); });
            bOff.Checked = Chart.CollapseOff;
            bOff.Visible = false;
            bOff.Click += Safe.Wrap(delegate { Chart.CollapseOff = !Chart.CollapseOff; bOff.Checked = Chart.CollapseOff; bOff.Invalidate(); });
            Chart.Rebuilt += delegate { bOff.Visible = Chart.HasOff; };
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(1);
            card.Tag = "card";
            Chart.Dock = DockStyle.Fill;
            card.Controls.Add(Chart);
            Controls.Add(card);
            Controls.Add(bar);
            Chart.ItemClicked += delegate (Item i) { if (ItemClicked != null) ItemClicked(i); };
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = bar.BackColor = flow.BackColor = t.Bg;
            card.BackColor = t.Divider;
            bPolicies.ApplyTheme(t);
            bTimed.ApplyTheme(t);
            bOff.ApplyTheme(t);
            Chart.ApplyTheme(t);
        }
    }

    // One bar per item across time, grouped by phase, with the time axis kept on top.
    public sealed class GanttChart : FlatControl
    {
        readonly ToolTip tip = new ToolTip();
        List<GanttRow> rows = new List<GanttRow>();
        Session session;
        DateTime nowUtc = DateTime.UtcNow, fromUtc, toUtc;
        bool live, showPolicies, onlyTimed, dragging, collapseOff = true;
        GanttAxis axis = new GanttAxis();
        int top, hot = -1, dragOffset;
        string tipText;

        public string EmptyText = "Nothing to show yet.";
        public event Action<Item> ItemClicked;
        public event Action Rebuilt;

        int RowH { get { return Dpi.S(26); } }
        int AxisH { get { return Dpi.S(30); } }
        int BarW { get { return Dpi.S(6); } }
        int LabelW { get { return Math.Max(Dpi.S(140), Math.Min(Dpi.S(320), Width * 28 / 100)); } }
        int ChartL { get { return LabelW + Dpi.S(12); } }
        int ChartR { get { return Width - BarW - Dpi.S(20); } }
        int ContentH { get { return rows.Count * RowH; } }

        public GanttChart()
        {
            TabStop = true;
            tip.ShowAlways = true;
            tip.OwnerDraw = true;
            tip.Popup += delegate (object s, PopupEventArgs e)
            {
                Size ts = TextRenderer.MeasureText(tipText ?? "", Fonts.SmallSemibold);
                e.ToolTipSize = new Size(ts.Width + Dpi.S(16), ts.Height + Dpi.S(12));
            };
            tip.Draw += delegate (object s, DrawToolTipEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(T.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                using (Pen p = new Pen(T.Divider)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                // The name on the first line stands out; the times under it are quieter.
                string all = e.ToolTipText ?? "";
                int nl = all.IndexOf('\n');
                string head = nl < 0 ? all : all.Substring(0, nl);
                TextRenderer.DrawText(e.Graphics, head, Fonts.SmallSemibold, new Point(Dpi.S(8), Dpi.S(6)), T.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (nl >= 0)
                {
                    int lh = TextRenderer.MeasureText("Ag", Fonts.Small).Height;
                    TextRenderer.DrawText(e.Graphics, all.Substring(nl + 1), Fonts.Small, new Point(Dpi.S(8), Dpi.S(6) + lh), T.Text2, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }

        public bool ShowPolicies
        {
            get { return showPolicies; }
            set { showPolicies = value; Rebuild(); }
        }

        public bool OnlyTimed
        {
            get { return onlyTimed; }
            set { onlyTimed = value; Rebuild(); }
        }

        // Times the device was off take a narrow hatched band instead of their length.
        public bool CollapseOff
        {
            get { return collapseOff; }
            set { collapseOff = value; Rebuild(); }
        }

        public bool HasOff
        {
            get { return axis.Bands.Count > 0; }
        }

        public void SetData(Session s, DateTime now, bool isLive)
        {
            session = s;
            nowUtc = now;
            live = isLive;
            Rebuild();
        }

        void Rebuild()
        {
            rows = session == null ? new List<GanttRow>() : GanttLayout.Rows(session, nowUtc, showPolicies, onlyTimed);
            GanttLayout.Range(rows, nowUtc, live, out fromUtc, out toUtc);
            axis = session == null ? new GanttAxis() : GanttAxis.For(session, fromUtc, toUtc, nowUtc, collapseOff);
            top = GanttLayout.ClampTop(top, rows.Count, RowH, Height - AxisH);
            if (hot >= rows.Count) hot = -1;
            Invalidate();
            if (Rebuilt != null) Rebuilt();
        }

        float X(DateTime utc)
        {
            if (axis.Bands.Count > 0) return axis.X(utc, ChartL, Math.Max(1, ChartR - ChartL));
            return GanttLayout.X(utc, fromUtc, toUtc, ChartL, Math.Max(1, ChartR - ChartL));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Card);
            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Fonts.Ui, ClientRectangle, T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int cl = ChartL, cr = ChartR;
            List<GanttTick> ticks = axis.Bands.Count > 0 ? axis.Ticks(cr - cl, Dpi.S(84), TimeZoneInfo.Local) : GanttLayout.Ticks(fromUtc, toUtc, GanttLayout.Step(toUtc - fromUtc, cr - cl, Dpi.S(84)), TimeZoneInfo.Local);
            Color grid = Theme.Mix(T.Card, T.Divider, 0.35f);
            using (Pen pen = new Pen(grid)) foreach (GanttTick k in ticks) { float x = X(k.Utc); g.DrawLine(pen, x, AxisH, x, Height); }

            g.SetClip(new Rectangle(0, AxisH, Width, Math.Max(0, Height - AxisH)));
            int first = Math.Max(0, top / RowH);
            for (int k = first; k < rows.Count; k++)
            {
                int y = AxisH + k * RowH - top;
                if (y > Height) break;
                Rectangle r = new Rectangle(0, y, Width - BarW - Dpi.S(4), RowH);
                if (rows[k].IsHeader) PaintHeader(g, rows[k], r);
                else PaintItem(g, rows[k], r, k == hot);
            }
            PaintOff(g);
            PaintRestarts(g);
            if (live)
            {
                float nx = X(nowUtc);
                using (Pen pen = new Pen(T.Accent, Dpi.S(1.5f))) g.DrawLine(pen, nx, AxisH, nx, Height);
            }
            g.ResetClip();
            PaintAxis(g, ticks);
            PaintScroll(g);
        }

        // The times the device was off, hatched, with how long at the top of the chart.
        void PaintOff(Graphics g)
        {
            foreach (OffSpan b in axis.Bands)
            {
                float x0 = X(b.FromUtc), x1 = Math.Max(X(b.ToUtc.Value), x0 + Dpi.S(4));
                RectangleF r = new RectangleF(x0, AxisH, x1 - x0, Height - AxisH);
                using (SolidBrush fill = new SolidBrush(T.Card)) g.FillRectangle(fill, r);
                using (HatchBrush h = new HatchBrush(HatchStyle.WideUpwardDiagonal, Theme.Mix(T.Card, T.Text3, 0.35f), Theme.Mix(T.Card, T.Text3, 0.08f))) g.FillRectangle(h, r);
                using (Pen pen = new Pen(Theme.Mix(T.Card, T.Text3, 0.5f))) { g.DrawLine(pen, x0, AxisH, x0, Height); g.DrawLine(pen, x1, AxisH, x1, Height); }
                string label = GanttAxis.Label(b);
                Size ts = TextRenderer.MeasureText(label, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding);
                int pad = Dpi.S(5), w = ts.Width + pad * 2, h2 = ts.Height + Dpi.S(4);
                int lx = (int)((x0 + x1) / 2 - w / 2f);
                lx = Math.Max(ChartL, Math.Min(ChartR + Dpi.S(14) - w, lx));
                int ly = AxisH + Dpi.S(4);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Shapes.Fill(g, new RectangleF(lx, ly, w, h2), Dpi.S(3), T.CardAlt);
                Shapes.Stroke(g, new RectangleF(lx, ly, w, h2), Dpi.S(3), T.Divider, 1f);
                TextRenderer.DrawText(g, label, Fonts.Small, new Point(lx + pad, ly + Dpi.S(2)), T.Text2, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        OffSpan BandAt(float x)
        {
            foreach (OffSpan b in axis.Bands)
            {
                float x0 = X(b.FromUtc), x1 = Math.Max(X(b.ToUtc.Value), x0 + Dpi.S(4));
                if (x >= x0 && x <= x1) return b;
            }
            return null;
        }

        // A thin line where the device restarted, with a small label at the top of the chart.
        void PaintRestarts(Graphics g)
        {
            if (session == null) return;
            foreach (GanttTick k in GanttLayout.RestartMarks(session, TimeZoneInfo.Local))
            {
                if (k.Utc < fromUtc || k.Utc > toUtc) continue;
                // A time off has its band and label.
                if (axis.Bands.Exists(delegate (OffSpan b) { return b.FromUtc == k.Utc; })) continue;
                float x = X(k.Utc);
                using (Pen pen = new Pen(T.WarnFg, Math.Max(1f, Dpi.S(1f))))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawLine(pen, x, AxisH, x, Height);
                }
                Size ts = TextRenderer.MeasureText(k.Label, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding);
                int pad = Dpi.S(5), w = ts.Width + pad * 2, h = ts.Height + Dpi.S(4);
                int lx = (int)x + Dpi.S(4);
                if (lx + w > ChartR + Dpi.S(14)) lx = (int)x - Dpi.S(4) - w;
                int ly = AxisH + Dpi.S(4);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Shapes.Fill(g, new RectangleF(lx, ly, w, h), Dpi.S(3), T.WarnBg);
                TextRenderer.DrawText(g, k.Label, Fonts.Small, new Point(lx + pad, ly + Dpi.S(2)), T.WarnFg, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        void PaintAxis(Graphics g, List<GanttTick> ticks)
        {
            using (SolidBrush b = new SolidBrush(T.Card)) g.FillRectangle(b, 0, 0, Width, AxisH);
            using (Pen pen = new Pen(T.Divider))
            {
                g.DrawLine(pen, 0, AxisH - 1, Width, AxisH - 1);
                foreach (GanttTick k in ticks)
                {
                    float x = X(k.Utc);
                    g.DrawLine(pen, x, AxisH - Dpi.S(5), x, AxisH - 1);
                    Size ts = TextRenderer.MeasureText(k.Label, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding);
                    int lx = (int)Math.Max(ChartL - Dpi.S(4), Math.Min(Width - BarW - ts.Width - Dpi.S(4), x - ts.Width / 2f));
                    TextRenderer.DrawText(g, k.Label, Fonts.Small, new Point(lx, (AxisH - ts.Height) / 2 - Dpi.S(2)), T.Text3, TextFormatFlags.NoPadding);
                }
            }
            if (live)
            {
                float nx = X(nowUtc);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float a = Dpi.S(4);
                using (SolidBrush b = new SolidBrush(T.Accent))
                    g.FillPolygon(b, new PointF[] { new PointF(nx - a, AxisH - a - 1), new PointF(nx + a, AxisH - a - 1), new PointF(nx, AxisH - 1) });
            }
        }

        void PaintHeader(Graphics g, GanttRow row, Rectangle r)
        {
            using (SolidBrush b = new SolidBrush(T.CardAlt)) g.FillRectangle(b, r);
            using (Pen pen = new Pen(T.Divider)) g.DrawLine(pen, 0, r.Bottom - 1, r.Right, r.Bottom - 1);
            Rectangle tr = new Rectangle(Dpi.S(12), r.Y, LabelW, r.Height);
            TextRenderer.DrawText(g, row.Phase.Name, Fonts.SmallSemibold, tr, T.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            float x0 = X(row.StartUtc), x1 = Math.Max(X(row.EndUtc), x0 + Dpi.S(2));
            float cy = r.Y + r.Height / 2f;
            Shapes.Fill(g, new RectangleF(x0, cy - Dpi.S(2), x1 - x0, Dpi.S(4)), Dpi.S(2), Theme.Mix(T.CardAlt, T.Text3, 0.45f));
        }

        void PaintItem(Graphics g, GanttRow row, Rectangle r, bool hover)
        {
            Item i = row.Item;
            if (hover) using (SolidBrush b = new SolidBrush(Theme.Mix(T.Card, T.Hover, 0.7f))) g.FillRectangle(b, r);
            Color nameC = i.State == State.Skipped || i.State == State.NotApplicable ? T.Text3 : T.Text;
            Rectangle tr = new Rectangle(Dpi.S(24), r.Y, LabelW - Dpi.S(12), r.Height);
            TextRenderer.DrawText(g, i.DisplayName, Fonts.Small, tr, nameC, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            Color c = StateLook.Color(i.State, T);
            float cy = r.Y + r.Height / 2f;
            float x0 = X(row.StartUtc);
            float end;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (row.Instant)
            {
                float w = Dpi.S(3), h = Dpi.S(14);
                Shapes.Fill(g, new RectangleF(x0 - w / 2, cy - h / 2, w, h), w / 2, c);
                end = x0 + w;
            }
            else
            {
                float x1 = Math.Max(X(row.EndUtc), x0 + Dpi.S(6));
                float h = Dpi.S(14);
                RectangleF bar = new RectangleF(x0, cy - h / 2, x1 - x0, h);
                bool running = States.IsActive(i.State) && i.EndUtc == null;
                if (running) Shapes.Fill(g, new RectangleF(bar.X - 2, bar.Y - 2, bar.Width + 4, bar.Height + 4), Dpi.S(7), Color.FromArgb(T.IsDark ? 40 : 30, c));
                Shapes.Fill(g, bar, Math.Min(Dpi.S(5), bar.Width / 2), c);
                end = x1;
                string dur = Txt.DurationShort(row.Duration);
                int dw = TextRenderer.MeasureText(dur, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
                float lx = GanttLayout.LabelX(end, dw, ChartR + Dpi.S(14), Dpi.S(6));
                if (lx >= 0) TextRenderer.DrawText(g, dur, Fonts.Small, new Rectangle((int)lx, r.Y, dw + 2, r.Height), T.Text3, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        void PaintScroll(Graphics g)
        {
            RectangleF t = Thumb();
            if (t.IsEmpty) return;
            RectangleF r = new RectangleF(Width - BarW - Dpi.S(3), t.Y + Dpi.S(2), BarW, t.Height - Dpi.S(4));
            Shapes.Fill(g, r, BarW / 2f, dragging ? T.Text3 : Theme.Mix(T.Card, T.Text3, 0.5f));
        }

        RectangleF Thumb()
        {
            int view = Height - AxisH, ch = ContentH;
            if (ch <= view || view <= 0) return RectangleF.Empty;
            float th = Math.Max(Dpi.S(30), view * (float)view / ch);
            float ty = AxisH + (view - th) * top / (float)(ch - view);
            return new RectangleF(Width - BarW - Dpi.S(6), ty, BarW + Dpi.S(6), th);
        }

        string TipFor(GanttRow r)
        {
            if (r.IsHeader) return r.Phase.Name + "\nStart: " + Txt.Local(r.StartUtc) + "\nEnd: " + Txt.Local(r.EndUtc) + "\nDuration: " + Txt.Duration(r.Duration);
            Item i = r.Item;
            bool running = States.IsActive(i.State) && i.EndUtc == null;
            return i.DisplayName
                + "\nState: " + States.Label(i.State)
                + "\nStart: " + Txt.Local(r.StartUtc)
                + "\nEnd: " + (running ? "still running" : Txt.Local(r.EndUtc))
                + "\nDuration: " + (r.Instant ? "under a second" : Txt.Duration(r.Duration));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            top = GanttLayout.ClampTop(top - e.Delta / 120 * RowH * 3, rows.Count, RowH, Height - AxisH);
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                RectangleF t = Thumb();
                float track = Height - AxisH - t.Height;
                if (track > 0) top = GanttLayout.ClampTop((int)((e.Y - AxisH - dragOffset) / track * (ContentH - (Height - AxisH))), rows.Count, RowH, Height - AxisH);
                Invalidate();
                return;
            }
            int h = Thumb().Contains(e.Location) ? -1 : GanttLayout.RowAt(e.Y, AxisH, top, RowH, rows.Count);
            OffSpan band = h >= 0 && (rows[h].IsHeader || e.X < X(rows[h].StartUtc) || e.X > X(rows[h].EndUtc)) ? BandAt(e.X) : null;
            string want = band != null ? GanttAxis.Tip(band) : h >= 0 ? TipFor(rows[h]) : null;
            if (h != hot || want != tipText)
            {
                hot = h;
                Invalidate();
                string text = want;
                if (text != tipText)
                {
                    tipText = text;
                    if (text == null) tip.Hide(this);
                    else tip.Show(text, this, e.X + Dpi.S(14), e.Y + Dpi.S(18));
                }
            }
            Cursor = h >= 0 && !rows[h].IsHeader ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        // Drops the hovered row and its tip, for screenshots.
        public void ClearHover()
        {
            hot = -1;
            tipText = null;
            tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
            tipText = null;
            tip.Hide(this);
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            RectangleF t = Thumb();
            if (e.Button == MouseButtons.Left && t.Contains(e.Location))
            {
                dragging = true;
                dragOffset = (int)(e.Y - t.Y);
                Capture = true;
                return;
            }
            int k = GanttLayout.RowAt(e.Y, AxisH, top, RowH, rows.Count);
            if (e.Button == MouseButtons.Left && k >= 0 && !rows[k].IsHeader && ItemClicked != null)
            {
                tip.Hide(this);
                tipText = null;
                ItemClicked(rows[k].Item);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                Capture = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnResize(EventArgs e)
        {
            top = GanttLayout.ClampTop(top, rows.Count, RowH, Height - AxisH);
            base.OnResize(e);
        }
    }
}
