namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // The theme choice in Settings: one tile per theme, each with a small sample of its colours, four to a row.
    public sealed class ThemePicker : FlatControl
    {
        const int PerRow = 4;
        readonly string[] names;
        readonly Theme[] samples;
        int sel, hot = -1;
        public event EventHandler Changed;

        public ThemePicker(string[] names)
        {
            this.names = names;
            samples = new Theme[names.Length];
            for (int i = 0; i < names.Length; i++) samples[i] = Palettes.Find(names[i]) != null ? Theme.Named(names[i]) : null;
            TabStop = true;
            Cursor = Cursors.Hand;
            int rows = (names.Length + PerRow - 1) / PerRow;
            Width = Dpi.S(6) + PerRow * TileW();
            Height = Dpi.S(6) + rows * TileH();
        }

        static int TileW() { return Dpi.S(112); }
        static int TileH() { return Dpi.S(36); }

        public string SelectedName
        {
            get { return names[sel]; }
            set
            {
                int i = Array.FindIndex(names, delegate(string n) { return string.Equals(n, value, StringComparison.OrdinalIgnoreCase); });
                Selected = i < 0 ? 0 : i;
            }
        }

        public int Selected
        {
            get { return sel; }
            set
            {
                if (value < 0 || value >= names.Length || value == sel) return;
                sel = value;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
        }

        Rectangle TileRect(int i)
        {
            return new Rectangle(Dpi.S(3) + (i % PerRow) * TileW(), Dpi.S(3) + (i / PerRow) * TileH(), TileW(), TileH());
        }

        int IndexAt(Point p)
        {
            for (int i = 0; i < names.Length; i++) if (TileRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hot) { hot = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = IndexAt(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left) Selected = i;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int to = sel;
            if (e.KeyCode == Keys.Left) to = sel - 1;
            else if (e.KeyCode == Keys.Right) to = sel + 1;
            else if (e.KeyCode == Keys.Up) to = sel - PerRow;
            else if (e.KeyCode == Keys.Down) to = sel + PerRow;
            if (to != sel)
            {
                Selected = Math.Max(0, Math.Min(names.Length - 1, to));
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Card);
            RectangleF outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, outer, Dpi.S(10), T.Field);
            Shapes.Stroke(g, outer, Dpi.S(10), T.FieldStroke, 1f);
            for (int i = 0; i < names.Length; i++)
            {
                Rectangle r = TileRect(i);
                RectangleF rf = new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                if (i == sel)
                {
                    Shapes.Fill(g, rf, Dpi.S(7), Theme.Mix(T.Field, T.Accent, T.IsDark ? 0.24f : 0.14f));
                    Shapes.Stroke(g, rf, Dpi.S(7), Theme.Mix(T.Field, T.Accent, 0.55f), 1f);
                }
                else if (i == hot) Shapes.Fill(g, rf, Dpi.S(7), T.Hover);
                int s = Dpi.S(20);
                Rectangle sw = new Rectangle(r.X + Dpi.S(8), r.Y + (r.Height - s) / 2, s, s);
                Sample(g, sw, samples[i]);
                Rectangle tr = new Rectangle(sw.Right + Dpi.S(8), r.Y, r.Right - sw.Right - Dpi.S(12), r.Height);
                Draw.Text(g, names[i], i == sel ? Fonts.UiSemibold : Fonts.Ui, tr, i == sel ? T.Accent : T.Text, Draw.Line);
            }
            if (Focused && ShowFocusCues) Shapes.Stroke(g, outer, Dpi.S(10), T.Text, 2f);
        }

        // A small window of the theme: its background with a card on it, a line in its accent, a line of its text and
        // its done and failed colours. System shows Light and Dark side by side.
        void Sample(Graphics g, Rectangle r, Theme t)
        {
            RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            float rad = Dpi.S(5);
            Color edge = Theme.Mix(T.Field, T.Text3, 0.55f);
            if (t == null)
            {
                Theme l = Theme.Light(), d = Theme.Dark();
                GraphicsState st = g.Save();
                using (GraphicsPath clip = Shapes.Round(rf, rad))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.SetClip(clip, CombineMode.Intersect);
                    using (SolidBrush b = new SolidBrush(l.Card)) g.FillRectangle(b, rf);
                    using (SolidBrush b = new SolidBrush(d.Card)) g.FillPolygon(b, new PointF[] { new PointF(rf.Right, rf.Y), new PointF(rf.Right, rf.Bottom), new PointF(rf.X, rf.Bottom) });
                }
                g.Restore(st);
                Shapes.Stroke(g, rf, rad, edge, 1f);
                return;
            }
            Shapes.Fill(g, rf, rad, t.Bg);
            int u = Dpi.S(2), x = r.X + Dpi.S(4), w = r.Width - Dpi.S(8);
            Shapes.Fill(g, new RectangleF(x, r.Y + Dpi.S(4), w * 0.7f, u + 1), u, t.Accent);
            Shapes.Fill(g, new RectangleF(x, r.Y + Dpi.S(9), w, u), u / 2f, t.Text2);
            float dot = Dpi.S(4);
            Shapes.Fill(g, new RectangleF(x, r.Bottom - Dpi.S(4) - dot, dot, dot), dot / 2f, t.OkFg);
            Shapes.Fill(g, new RectangleF(x + dot + Dpi.S(2), r.Bottom - Dpi.S(4) - dot, dot, dot), dot / 2f, t.WarnFg);
            Shapes.Fill(g, new RectangleF(x + (dot + Dpi.S(2)) * 2, r.Bottom - Dpi.S(4) - dot, dot, dot), dot / 2f, t.ErrFg);
            Shapes.Stroke(g, rf, rad, edge, 1f);
        }
    }
}
