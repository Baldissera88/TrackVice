namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Windows.Forms;

    // Colours and the round state icon used everywhere: a check when done, a cross when failed, a spinner while running.
    public static class StateLook
    {
        public static Color Color(State s, Theme t)
        {
            switch (s)
            {
                case State.Succeeded: return t.OkFg;
                case State.Failed: return t.ErrFg;
                case State.WaitingReboot: return t.WarnFg;
                case State.Downloading: case State.Installing: case State.Detecting: return t.Accent;
                default: return t.Text3;
            }
        }

        public static void Icon(Graphics g, State s, RectangleF r, Theme t, float spin)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = Color(s, t);
            float w = Math.Max(1.5f, r.Width / 9f);
            RectangleF inner = new RectangleF(r.X + w / 2, r.Y + w / 2, r.Width - w, r.Height - w);
            Color on = t.IsDark ? t.Bg : System.Drawing.Color.White;
            switch (s)
            {
                case State.Succeeded:
                    using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, r);
                    using (Pen p = new Pen(on, w * 1.15f))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        p.LineJoin = LineJoin.Round;
                        g.DrawLines(p, new PointF[] { P(r, 0.29f, 0.52f), P(r, 0.44f, 0.67f), P(r, 0.72f, 0.36f) });
                    }
                    break;
                case State.Failed:
                    using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, r);
                    using (Pen p = new Pen(on, w * 1.15f))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        g.DrawLine(p, P(r, 0.34f, 0.34f), P(r, 0.66f, 0.66f));
                        g.DrawLine(p, P(r, 0.66f, 0.34f), P(r, 0.34f, 0.66f));
                    }
                    break;
                case State.WaitingReboot:
                    using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, r);
                    using (Pen p = new Pen(on, w))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        RectangleF a = new RectangleF(r.X + r.Width * 0.28f, r.Y + r.Height * 0.28f, r.Width * 0.44f, r.Height * 0.44f);
                        g.DrawArc(p, a, -60, 290);
                        PointF tip = P(r, 0.6f, 0.3f);
                        g.DrawLine(p, tip, new PointF(tip.X + r.Width * 0.13f, tip.Y - r.Height * 0.02f));
                        g.DrawLine(p, tip, new PointF(tip.X + r.Width * 0.03f, tip.Y + r.Height * 0.13f));
                    }
                    break;
                case State.Downloading:
                case State.Installing:
                case State.Detecting:
                    using (Pen track = new Pen(Theme.Mix(t.Card, c, 0.22f), w))
                        g.DrawEllipse(track, inner);
                    using (Pen p = new Pen(c, w))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        g.DrawArc(p, inner, spin, 100);
                    }
                    if (s == State.Downloading)
                    {
                        using (Pen p = new Pen(c, w * 0.9f))
                        {
                            p.StartCap = p.EndCap = LineCap.Round;
                            p.LineJoin = LineJoin.Round;
                            g.DrawLine(p, P(r, 0.5f, 0.3f), P(r, 0.5f, 0.66f));
                            g.DrawLines(p, new PointF[] { P(r, 0.36f, 0.53f), P(r, 0.5f, 0.67f), P(r, 0.64f, 0.53f) });
                        }
                    }
                    else
                    {
                        using (SolidBrush b = new SolidBrush(c))
                        {
                            float d = r.Width * 0.22f;
                            g.FillEllipse(b, r.X + r.Width / 2 - d / 2, r.Y + r.Height / 2 - d / 2, d, d);
                        }
                    }
                    break;
                case State.Skipped:
                case State.NotApplicable:
                    using (Pen p = new Pen(c, w)) g.DrawEllipse(p, inner);
                    using (Pen p = new Pen(c, w))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        g.DrawLine(p, P(r, 0.34f, 0.5f), P(r, 0.66f, 0.5f));
                    }
                    break;
                default:
                    using (Pen p = new Pen(c, w))
                    {
                        if (s == State.Unknown) p.DashStyle = DashStyle.Dot;
                        g.DrawEllipse(p, inner);
                    }
                    break;
            }
            g.SmoothingMode = old;
        }

        static PointF P(RectangleF r, float x, float y)
        {
            return new PointF(r.X + r.Width * x, r.Y + r.Height * y);
        }

        public static void Bar(Graphics g, RectangleF r, double value, Color fill, Color track)
        {
            float rad = r.Height / 2;
            Shapes.Fill(g, r, rad, track);
            float w = (float)(r.Width * Math.Max(0, Math.Min(1, value)));
            if (w > 0.5f) Shapes.Fill(g, new RectangleF(r.X, r.Y, Math.Max(w, r.Height), r.Height), rad, fill);
        }

        public static string StateText(Item i)
        {
            string s = States.Label(i.State);
            if (States.IsActive(i.State) && i.Percent.HasValue && i.Percent.Value > 0 && i.Percent.Value < 100) s += "  " + i.Percent.Value.ToString(CultureInfo.InvariantCulture) + " %";
            else if (i.State == State.Failed && i.ExitCode.HasValue) s += " · " + Errors.Short(i.ExitCode);
            return s;
        }
    }

    // The phases side by side, each a card with its state, count, time and progress. Click one to jump to it in the list.
    public sealed class PhaseRail : FlatControl
    {
        List<Phase> phases = new List<Phase>();
        readonly List<RectangleF> hits = new List<RectangleF>();
        Func<DateTime> now = delegate { return DateTime.UtcNow; };
        public string Selected;
        int hot = -1, scroll;
        float spin;
        public event Action<Phase> PhaseClicked;
        public string EmptyText = "Phases show up here as soon as a deployment starts.";

        public PhaseRail()
        {
            Height = Dpi.S(84);
            Cursor = Cursors.Hand;
        }

        public void SetData(IEnumerable<Phase> list, Func<DateTime> clock)
        {
            phases = new List<Phase>(list);
            now = clock ?? now;
            Invalidate();
        }

        public void Tick(float angle)
        {
            spin = angle;
            foreach (Phase p in phases) if (States.IsActive(p.State)) { Invalidate(); return; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            hits.Clear();
            int pad = Dpi.S(18), gap = Dpi.S(26);
            if (phases.Count == 0)
            {
                Rectangle er = new Rectangle(pad, Dpi.S(8), Width - pad * 2, Height - Dpi.S(20));
                Shapes.Stroke(g, er, Dpi.S(8), T.Divider, 1f);
                TextRenderer.DrawText(g, EmptyText, Fonts.Ui, er, T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            int n = phases.Count;
            float avail = Width - pad * 2 - gap * (n - 1);
            float w = Math.Max(Dpi.S(170), Math.Min(Dpi.S(420), avail / n));
            float total = w * n + gap * (n - 1);
            int maxScroll = (int)Math.Max(0, total - (Width - pad * 2));
            scroll = Math.Max(0, Math.Min(scroll, maxScroll));
            float x = pad - scroll;
            float y = Dpi.S(6), h = Height - Dpi.S(18);
            for (int i = 0; i < n; i++)
            {
                Phase p = phases[i];
                RectangleF r = new RectangleF(x, y, w, h);
                hits.Add(r);
                State st = p.State;
                Color sc = StateLook.Color(st, T);
                bool sel = p.Key == Selected;
                Shapes.Fill(g, r, Dpi.S(8), sel ? T.AccentSoft : i == hot ? Theme.Mix(T.Card, T.Hover, 0.6f) : T.Card);
                Shapes.Stroke(g, r, Dpi.S(8), sel ? T.Accent : T.Divider, 1f);
                float ix = r.X + Dpi.S(12);
                StateLook.Icon(g, st, new RectangleF(ix, r.Y + Dpi.S(11), Dpi.S(16), Dpi.S(16)), T, spin);
                string dur = Txt.DurationShort(p.Duration(now()));
                int dw = string.IsNullOrEmpty(dur) ? 0 : TextRenderer.MeasureText(dur, Fonts.Small).Width;
                Rectangle nameR = new Rectangle((int)(ix + Dpi.S(24)), (int)r.Y + Dpi.S(8), (int)(r.Width - Dpi.S(48) - dw - Dpi.S(8)), Fonts.UiSemibold.Height + 2);
                TextRenderer.DrawText(g, p.Name, Fonts.UiSemibold, nameR, T.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                if (dw > 0) TextRenderer.DrawText(g, dur, Fonts.Small, new Rectangle((int)(r.Right - Dpi.S(12) - dw), (int)r.Y + Dpi.S(10), dw + 2, Fonts.Small.Height + 2), T.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                int done = p.Done, failed = p.FailedCount;
                string sub = done + " of " + p.Items.Count + " done";
                int sx = (int)(ix + Dpi.S(24)), sy = (int)r.Y + Dpi.S(30);
                TextRenderer.DrawText(g, sub, Fonts.Small, new Point(sx, sy), T.Text3, TextFormatFlags.NoPadding);
                if (failed > 0)
                {
                    int sw = TextRenderer.MeasureText(sub, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, " · " + failed + " failed", Fonts.SmallSemibold, new Point(sx + sw, sy), T.ErrFg, TextFormatFlags.NoPadding);
                }
                StateLook.Bar(g, new RectangleF(r.X + Dpi.S(12), r.Bottom - Dpi.S(14), r.Width - Dpi.S(24), Dpi.S(4)), p.Progress, sc, Theme.Mix(T.Card, T.Divider, 0.9f));
                if (i < n - 1)
                {
                    float cx = r.Right + gap / 2f, cy = r.Y + r.Height / 2f;
                    using (Pen pen = new Pen(T.Text3, Dpi.S(1.5f)))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        float a = Dpi.S(4);
                        g.DrawLines(pen, new PointF[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a) });
                    }
                }
                x += w + gap;
            }
            if (maxScroll > 0)
            {
                // Fade the edges so it is clear that more phases are off screen.
                if (scroll > 0) Fade(g, new Rectangle(0, 0, pad + Dpi.S(20), Height), true);
                if (scroll < maxScroll) Fade(g, new Rectangle(Width - pad - Dpi.S(20), 0, pad + Dpi.S(20), Height), false);
            }
        }

        void Fade(Graphics g, Rectangle r, bool left)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(r, left ? T.Bg : Color.FromArgb(0, T.Bg), left ? Color.FromArgb(0, T.Bg) : T.Bg, LinearGradientMode.Horizontal))
                g.FillRectangle(b, r);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < hits.Count; i++) if (hits[i].Contains(e.Location)) h = i;
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
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
    }

    // The overall progress chip: percent, a bar and the elapsed time.
    public sealed class OverallChip : FlatControl
    {
        public double Progress;
        public string Elapsed = "";
        public State State = State.Unknown;
        public string Label = "Overall";

        public OverallChip()
        {
            Size = new Size(Dpi.S(220), Dpi.S(58));
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(7), T.Card);
            Shapes.Stroke(g, r, Dpi.S(7), T.Divider, 1f);
            int pad = Dpi.S(12);
            string pct = ((int)Math.Round(Progress * 100)).ToString(CultureInfo.InvariantCulture) + " %";
            TextRenderer.DrawText(g, pct, Fonts.Title, new Rectangle(pad, Dpi.S(6), Width - pad, Dpi.S(28)), T.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int pw = TextRenderer.MeasureText(pct, Fonts.Title, Size.Empty, TextFormatFlags.NoPadding).Width;
            string st = State == State.Unknown ? "" : States.Label(State);
            if (st.Length > 0) TextRenderer.DrawText(g, st, Fonts.SmallSemibold, new Rectangle(pad + pw + Dpi.S(10), Dpi.S(12), Width - pw - pad * 2, Dpi.S(18)), StateLook.Color(State, T), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Label + (Elapsed.Length > 0 ? " · " + Elapsed : ""), Fonts.Small, new Rectangle(pad, Dpi.S(34), Width - pad * 2, Dpi.S(14)), T.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            StateLook.Bar(g, new RectangleF(pad, Height - Dpi.S(10), Width - pad * 2, Dpi.S(3)), Progress, State == State.Failed ? T.ErrFg : State == State.Succeeded ? T.OkFg : T.Accent, T.Divider);
        }
    }

    // The progress list: phases as group rows, items under them with their state, progress, time and last event.
    public sealed class ItemList : FlatControl
    {
        sealed class Row
        {
            public Phase Phase;
            public Item Item;
        }

        readonly List<Row> rows = new List<Row>();
        readonly HashSet<string> collapsed = new HashSet<string>();
        readonly Timer anim = new Timer();
        Func<DateTime> now = delegate { return DateTime.UtcNow; };
        IEnumerable<Phase> source = new Phase[0];
        int top, hot = -1, selected = -1;
        float spin;
        bool dragging;
        int dragOffset;
        public Func<Item, bool> Filter;
        public string EmptyText = "Nothing to show yet.";
        public event EventHandler SelectionChanged;
        public event Action<Phase> SpinTick;
        public Action<ContextMenuStrip, Item> MenuExtras;

        int ItemH { get { return Dpi.S(48); } }
        int PhaseH { get { return Dpi.S(36); } }
        int BarW { get { return Dpi.S(6); } }

        public ItemList()
        {
            TabStop = true;
            anim.Interval = 50;
            anim.Tick += delegate
            {
                spin = (spin + 14) % 360;
                if (SpinTick != null) SpinTick(null);
                if (HasActiveVisible()) Invalidate();
            };
            anim.Start();
            ContextMenuStrip m = new ContextMenuStrip();
            m.Opening += delegate (object s, System.ComponentModel.CancelEventArgs e)
            {
                Menus.Prepare(m, T);
                Item it = SelectedItem;
                if (it == null) { e.Cancel = true; return; }
                Menus.Add(m, "Copy name", delegate { Menus.Copy(it.DisplayName); });
                if (it.Get("ID") != null) Menus.Add(m, "Copy ID", delegate { Menus.Copy(it.Get("ID")); });
                if (it.Get("Command line") != null) Menus.Add(m, "Copy command line", delegate { Menus.Copy(it.Get("Command line")); });
                if (MenuExtras != null) { Menus.Separator(m); MenuExtras(m, it); }
            };
            ContextMenuStrip = m;
        }

        public float Spin
        {
            get { return spin; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }

        public void SetData(IEnumerable<Phase> phases, Func<DateTime> clock)
        {
            source = phases;
            if (clock != null) now = clock;
            Rebuild();
        }

        public void Rebuild()
        {
            string keepItem = SelectedItem != null ? SelectedItem.Key : null;
            string keepPhase = SelectedItem == null && SelectedPhase != null ? SelectedPhase.Key : null;
            rows.Clear();
            foreach (Phase p in source)
            {
                List<Item> items = new List<Item>();
                foreach (Item i in p.Items) if (Filter == null || Filter(i)) items.Add(i);
                if (items.Count == 0 && Filter != null) continue;
                Row h = new Row();
                h.Phase = p;
                rows.Add(h);
                if (collapsed.Contains(p.Key)) continue;
                foreach (Item i in items)
                {
                    Row r = new Row();
                    r.Phase = p;
                    r.Item = i;
                    rows.Add(r);
                }
            }
            selected = -1;
            for (int k = 0; k < rows.Count; k++)
            {
                if (keepItem != null && rows[k].Item != null && rows[k].Item.Key == keepItem) selected = k;
                if (keepPhase != null && rows[k].Item == null && rows[k].Phase.Key == keepPhase) selected = k;
            }
            ClampTop();
            Invalidate();
        }

        public Item SelectedItem
        {
            get { return selected >= 0 && selected < rows.Count ? rows[selected].Item : null; }
        }

        public Phase SelectedPhase
        {
            get { return selected >= 0 && selected < rows.Count ? rows[selected].Phase : null; }
        }

        public bool SelectKey(string key)
        {
            return SelectKey(key, false);
        }

        // center puts the row in the middle of the list, for a selection made by the app rather than by the user.
        public bool SelectKey(string key, bool center)
        {
            for (int k = 0; k < rows.Count; k++)
            {
                if (rows[k].Item != null && rows[k].Item.Key == key)
                {
                    if (center)
                    {
                        top = Y(k) - (Height - H(k)) / 2;
                        ClampTop();
                    }
                    Select(k);
                    return true;
                }
            }
            return false;
        }

        public void SelectPhase(string key)
        {
            collapsed.Remove(key);
            Rebuild();
            for (int k = 0; k < rows.Count; k++)
            {
                if (rows[k].Item == null && rows[k].Phase.Key == key)
                {
                    Select(k);
                    top = Math.Max(0, Y(k));
                    ClampTop();
                    Invalidate();
                    return;
                }
            }
        }

        // The first item that matches, for --select in screenshots.
        public bool SelectByName(string text)
        {
            for (int k = 0; k < rows.Count; k++)
                if (rows[k].Item != null && rows[k].Item.DisplayName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return SelectKey(rows[k].Item.Key, true);
            return false;
        }

        public void ClearHover()
        {
            hot = -1;
            Invalidate();
        }

        void Select(int k)
        {
            if (k < 0 || k >= rows.Count) return;
            selected = k;
            EnsureVisible(k);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        int H(int k)
        {
            return rows[k].Item == null ? PhaseH : ItemH;
        }

        int Y(int k)
        {
            int y = 0;
            for (int i = 0; i < k; i++) y += H(i);
            return y;
        }

        int ContentHeight
        {
            get
            {
                int y = 0;
                for (int i = 0; i < rows.Count; i++) y += H(i);
                return y + Dpi.S(8);
            }
        }

        void ClampTop()
        {
            top = Math.Max(0, Math.Min(top, ContentHeight - Height));
        }

        void EnsureVisible(int k)
        {
            int y = Y(k), h = H(k);
            if (y < top) top = y;
            else if (y + h > top + Height) top = y + h - Height;
            ClampTop();
        }

        bool HasActiveVisible()
        {
            int y = -top;
            for (int k = 0; k < rows.Count && y < Height; k++)
            {
                int h = H(k);
                if (y + h > 0)
                {
                    Row r = rows[k];
                    State s = r.Item != null ? r.Item.State : r.Phase.State;
                    if (States.IsActive(s)) return true;
                }
                y += h;
            }
            return false;
        }

        int RowAt(int py)
        {
            int y = -top;
            for (int k = 0; k < rows.Count; k++)
            {
                int h = H(k);
                if (py >= y && py < y + h) return k;
                y += h;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Card);
            if (rows.Count == 0)
            {
                Rectangle er = new Rectangle(Dpi.S(24), Dpi.S(40), Math.Max(0, Width - Dpi.S(48)), Dpi.S(120));
                TextRenderer.DrawText(g, EmptyText, Fonts.Ui, er, T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                return;
            }
            int y = -top;
            int w = Width - BarW - Dpi.S(4);
            DateTime nowUtc = now();
            for (int k = 0; k < rows.Count; k++)
            {
                int h = H(k);
                if (y + h > 0 && y < Height)
                {
                    Rectangle r = new Rectangle(0, y, w, h);
                    if (rows[k].Item == null) PaintPhase(g, rows[k].Phase, r, k == selected, k == hot, nowUtc);
                    else PaintItem(g, rows[k].Item, r, k == selected, k == hot, nowUtc);
                }
                y += h;
                if (y > Height) break;
            }
            PaintScroll(g);
        }

        void PaintPhase(Graphics g, Phase p, Rectangle r, bool sel, bool hover, DateTime nowUtc)
        {
            Rectangle band = new Rectangle(r.X, r.Y, r.Width + BarW + Dpi.S(4), r.Height);
            using (SolidBrush b = new SolidBrush(sel ? T.AccentSoft : hover ? Theme.Mix(T.CardAlt, T.Hover, 0.5f) : T.CardAlt)) g.FillRectangle(b, band);
            using (Pen pen = new Pen(T.Divider)) g.DrawLine(pen, 0, r.Bottom - 1, band.Right, r.Bottom - 1);
            bool open = !collapsed.Contains(p.Key);
            float cx = r.X + Dpi.S(18), cy = r.Y + r.Height / 2f, a = Dpi.S(3.5f);
            using (Pen pen = new Pen(T.Text3, Dpi.S(1.4f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (open) g.DrawLines(pen, new PointF[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
                else g.DrawLines(pen, new PointF[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a) });
            }
            int x = r.X + Dpi.S(34);
            string name = p.Name;
            int nw = TextRenderer.MeasureText(name, Fonts.UiSemibold, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, name, Fonts.UiSemibold, new Rectangle(x, r.Y, Math.Min(nw + 4, r.Width / 2), r.Height), T.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            State st = p.State;
            string pill = States.Label(st);
            if (st != State.Unknown)
            {
                Size ps = TextRenderer.MeasureText(pill, Fonts.SmallSemibold, Size.Empty, TextFormatFlags.NoPadding);
                Color c = StateLook.Color(st, T);
                RectangleF pr = new RectangleF(x + Math.Min(nw, r.Width / 2) + Dpi.S(10), cy - ps.Height / 2f - Dpi.S(2), ps.Width + Dpi.S(14), ps.Height + Dpi.S(4));
                Shapes.Fill(g, pr, pr.Height / 2, Theme.Mix(T.CardAlt, c, T.IsDark ? 0.22f : 0.13f));
                TextRenderer.DrawText(g, pill, Fonts.SmallSemibold, Rectangle.Round(pr), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            string right = p.Done + " of " + p.Items.Count + " done";
            if (p.FailedCount > 0) right += " · " + p.FailedCount + " failed";
            string dur = Txt.Duration(p.Duration(nowUtc));
            if (dur.Length > 0) right += " · " + dur;
            int bw = Dpi.S(90);
            int rw = TextRenderer.MeasureText(right, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, right, Fonts.Small, new Rectangle(r.Right - Dpi.S(16) - bw - Dpi.S(14) - rw, r.Y, rw + 2, r.Height), T.Text3, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            StateLook.Bar(g, new RectangleF(r.Right - Dpi.S(16) - bw, cy - Dpi.S(2), bw, Dpi.S(4)), p.Progress, StateLook.Color(st, T), T.Divider);
        }

        void PaintItem(Graphics g, Item i, Rectangle r, bool sel, bool hover, DateTime nowUtc)
        {
            if (sel || hover)
            {
                RectangleF hr = new RectangleF(r.X + Dpi.S(6), r.Y + Dpi.S(3), r.Width - Dpi.S(8), r.Height - Dpi.S(6));
                Shapes.Fill(g, hr, Dpi.S(6), sel ? T.AccentSoft : T.Hover);
                if (sel) Shapes.Fill(g, new RectangleF(hr.X, hr.Y + Dpi.S(9), Dpi.S(3), hr.Height - Dpi.S(18)), Dpi.S(1.5f), T.Accent);
            }
            float ic = Dpi.S(18);
            StateLook.Icon(g, i.State, new RectangleF(r.X + Dpi.S(26), r.Y + (r.Height - ic) / 2, ic, ic), T, spin);
            int rightW = Dpi.S(250);
            int x = r.X + Dpi.S(56);
            int tw = Math.Max(Dpi.S(80), r.Width - x - rightW - Dpi.S(12));
            Color nameC = i.State == State.Skipped || i.State == State.NotApplicable ? T.Text3 : T.Text;
            TextRenderer.DrawText(g, i.DisplayName, Fonts.Ui, new Rectangle(x, r.Y + Dpi.S(7), tw, Fonts.Ui.Height + 2), nameC, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            string sub = Sub(i);
            TextRenderer.DrawText(g, sub, Fonts.Small, new Rectangle(x, r.Y + Dpi.S(27), tw, Fonts.Small.Height + 2), T.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // Right side: state (with a bar while running), tries, duration.
            int durW = Dpi.S(74);
            int sx = r.Right - rightW;
            Color sc = StateLook.Color(i.State, T);
            string st = StateLook.StateText(i);
            bool active = States.IsActive(i.State);
            int stateW = rightW - durW - Dpi.S(16);
            TextRenderer.DrawText(g, st, i.State == State.Failed ? Fonts.SmallSemibold : Fonts.Small, new Rectangle(sx, r.Y + (active ? Dpi.S(9) : 0), stateW, active ? Fonts.Small.Height + 2 : r.Height), sc,
                (active ? TextFormatFlags.Top : TextFormatFlags.VerticalCenter) | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (active)
            {
                RectangleF br = new RectangleF(sx, r.Y + Dpi.S(29), stateW - Dpi.S(16), Dpi.S(4));
                if (i.Percent.HasValue) StateLook.Bar(g, br, i.Percent.Value / 100.0, sc, T.Divider);
                else Indeterminate(g, br, sc);
            }
            if (i.Attempts.Count > 1)
            {
                string tries = "×" + i.Attempts.Count;
                Size ts = TextRenderer.MeasureText(tries, Fonts.SmallSemibold, Size.Empty, TextFormatFlags.NoPadding);
                Color tc = i.FailedAttempts > 0 ? T.WarnFg : T.Text3;
                RectangleF tr = new RectangleF(r.Right - durW - Dpi.S(12) - ts.Width - Dpi.S(10), r.Y + (r.Height - ts.Height - Dpi.S(4)) / 2f, ts.Width + Dpi.S(10), ts.Height + Dpi.S(4));
                Shapes.Fill(g, tr, tr.Height / 2, Theme.Mix(T.Card, tc, T.IsDark ? 0.22f : 0.12f));
                TextRenderer.DrawText(g, tries, Fonts.SmallSemibold, Rectangle.Round(tr), tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            string dur = Txt.Duration(i.Duration(nowUtc));
            TextRenderer.DrawText(g, dur, Fonts.Small, new Rectangle(r.Right - durW - Dpi.S(8), r.Y, durW, r.Height), T.Text2, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            using (Pen pen = new Pen(Theme.Mix(T.Card, T.Divider, 0.6f))) g.DrawLine(pen, x, r.Bottom - 1, r.Right - Dpi.S(8), r.Bottom - 1);
        }

        void Indeterminate(Graphics g, RectangleF r, Color c)
        {
            Shapes.Fill(g, r, r.Height / 2, T.Divider);
            float seg = r.Width * 0.3f;
            float pos = (float)((spin / 360.0) * (r.Width + seg)) - seg;
            float x0 = Math.Max(r.X, r.X + pos), x1 = Math.Min(r.Right, r.X + pos + seg);
            if (x1 - x0 > 1) Shapes.Fill(g, new RectangleF(x0, r.Y, x1 - x0, r.Height), r.Height / 2, c);
        }

        static string Sub(Item i)
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(i.Kind)) parts.Add(i.Kind);
            // A log line says more than a registry or WMI state, so the newest log line wins.
            string last = null, any = null;
            for (int k = i.Events.Count - 1; k >= 0 && last == null; k--)
            {
                TEvent e = i.Events[k];
                if (string.IsNullOrEmpty(e.Text)) continue;
                if (any == null) any = e.Text;
                if (e.Evidence != null && e.Evidence.Kind == EvidenceKinds.LogLine) last = e.Text;
            }
            if (last == null) last = any;
            if (last != null) parts.Add(Clean(last));
            return string.Join("  ·  ", parts.ToArray());
        }

        // Log lines start with [Win32App][Handler] tags; the list shows the words.
        static string Clean(string t)
        {
            t = t.Trim();
            while (t.StartsWith("[") && t.IndexOf(']') > 0 && t.IndexOf(']') < 40) t = t.Substring(t.IndexOf(']') + 1).TrimStart();
            t = t.TrimStart('+', ' ');
            return Txt.Short(t, 220);
        }

        void PaintScroll(Graphics g)
        {
            int ch = ContentHeight;
            if (ch <= Height) return;
            float th = Math.Max(Dpi.S(30), Height * (float)Height / ch);
            float ty = (Height - th) * top / (float)(ch - Height);
            RectangleF r = new RectangleF(Width - BarW - Dpi.S(3), ty + Dpi.S(2), BarW, th - Dpi.S(4));
            Shapes.Fill(g, r, BarW / 2f, dragging || hot == -2 ? T.Text3 : Theme.Mix(T.Card, T.Text3, 0.5f));
        }

        RectangleF Thumb()
        {
            int ch = ContentHeight;
            if (ch <= Height) return RectangleF.Empty;
            float th = Math.Max(Dpi.S(30), Height * (float)Height / ch);
            float ty = (Height - th) * top / (float)(ch - Height);
            return new RectangleF(Width - BarW - Dpi.S(6), ty, BarW + Dpi.S(6), th);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            top -= e.Delta / 120 * Dpi.S(96);
            ClampTop();
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                int ch = ContentHeight;
                RectangleF t = Thumb();
                float track = Height - t.Height;
                if (track > 0) top = (int)((e.Y - dragOffset) / track * (ch - Height));
                ClampTop();
                Invalidate();
                return;
            }
            int h = Thumb().Contains(e.Location) ? -2 : RowAt(e.Y);
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
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
            int k = RowAt(e.Y);
            if (k >= 0)
            {
                if (e.Button == MouseButtons.Left && rows[k].Item == null && e.X < Dpi.S(34) + TextRenderer.MeasureText(rows[k].Phase.Name, Fonts.UiSemibold).Width) Toggle(k);
                else Select(k);
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

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int k = RowAt(e.Y);
            if (k >= 0 && rows[k].Item == null) Toggle(k);
            base.OnMouseDoubleClick(e);
        }

        void Toggle(int k)
        {
            Phase p = rows[k].Phase;
            if (!collapsed.Remove(p.Key)) collapsed.Add(p.Key);
            Rebuild();
            for (int i = 0; i < rows.Count; i++) if (rows[i].Item == null && rows[i].Phase == p) Select(i);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Left: case Keys.Right: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int page = Math.Max(1, Height / ItemH - 1);
            switch (e.KeyCode)
            {
                case Keys.Down: Select(Math.Min(rows.Count - 1, selected + 1)); break;
                case Keys.Up: Select(Math.Max(0, selected - 1)); break;
                case Keys.Home: Select(0); break;
                case Keys.End: Select(rows.Count - 1); break;
                case Keys.PageDown: Select(Math.Min(rows.Count - 1, selected + page)); break;
                case Keys.PageUp: Select(Math.Max(0, selected - page)); break;
                case Keys.Left:
                    if (selected >= 0 && rows[selected].Item == null && !collapsed.Contains(rows[selected].Phase.Key)) Toggle(selected);
                    else if (selected >= 0 && rows[selected].Item != null) { for (int k = selected; k >= 0; k--) if (rows[k].Item == null) { Select(k); break; } }
                    break;
                case Keys.Right:
                case Keys.Enter:
                    if (selected >= 0 && rows[selected].Item == null && collapsed.Contains(rows[selected].Phase.Key)) Toggle(selected);
                    break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            ClampTop();
            base.OnResize(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }
}
