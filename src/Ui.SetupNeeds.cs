namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Windows.Forms;

    // The "What you need" box at the top of a guide: a short checklist that the person ticks, kept only while the window is open.
    public sealed class NeedsBox : FlatControl, IMeasured
    {
        readonly List<NeedItem> items;
        readonly HashSet<string> ticked;
        readonly string title, intro, moreText;
        public event EventHandler MoreClicked;
        int focus = -1;
        Rectangle more;
        bool moreHot;

        public NeedsBox(string title, string intro, List<NeedItem> items, HashSet<string> ticked, string moreText)
        {
            this.title = title;
            this.intro = intro;
            this.items = items;
            this.ticked = ticked;
            this.moreText = moreText;
            TabStop = true;
            Cursor = Cursors.Default;
        }

        public int Ticked
        {
            get
            {
                int n = 0;
                foreach (NeedItem i in items) if (ticked.Contains(i.Id)) n++;
                return n;
            }
        }

        int Pad
        {
            get { return Dpi.S(20); }
        }

        int TextX
        {
            get { return Pad + Dpi.S(16) + Dpi.S(12); }
        }

        int ItemH(NeedItem i, int width)
        {
            return Math.Max(Dpi.S(26), Draw.WrapHeight(i.Text, Fonts.Small, Math.Max(Dpi.S(80), width - TextX - Pad)) + Dpi.S(10));
        }

        int HeadH(int width)
        {
            return Dpi.S(14) + Fonts.UiSemibold.Height + Dpi.S(4) + Draw.WrapHeight(intro, Fonts.Small, Math.Max(Dpi.S(80), width - Pad * 2)) + Dpi.S(8);
        }

        public int Measure(int width)
        {
            int h = HeadH(width);
            foreach (NeedItem i in items) h += ItemH(i, width);
            return h + Dpi.S(8) + Fonts.Small.Height + Dpi.S(14);
        }

        int ItemAt(Point p)
        {
            int y = HeadH(Width);
            for (int k = 0; k < items.Count; k++)
            {
                int h = ItemH(items[k], Width);
                if (p.Y >= y && p.Y < y + h) return k;
                y += h;
            }
            return -1;
        }

        void Toggle(int k)
        {
            if (k < 0 || k >= items.Count) return;
            string id = items[k].Id;
            if (!ticked.Remove(id)) ticked.Add(id);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = more.Contains(e.Location);
            int k = ItemAt(e.Location);
            Cursor = hot || k >= 0 ? Cursors.Hand : Cursors.Default;
            if (hot != moreHot) { moreHot = hot; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            moreHot = false;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (more.Contains(e.Location)) { if (MoreClicked != null) MoreClicked(this, EventArgs.Empty); }
            else
            {
                int k = ItemAt(e.Location);
                if (k >= 0) { focus = k; Toggle(k); }
            }
            base.OnMouseClick(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) { focus = Math.Min(items.Count - 1, focus + 1); e.Handled = true; Invalidate(); }
            else if (e.KeyCode == Keys.Up) { focus = Math.Max(0, focus - 1); e.Handled = true; Invalidate(); }
            else if (e.KeyCode == Keys.Space) { if (focus < 0) focus = 0; Toggle(focus); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { if (focus < 0) focus = 0; Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Draw.Card(g, ClientRectangle, T);
            int y = Dpi.S(14);
            Draw.Text(g, title, Fonts.UiSemibold, new Rectangle(Pad, y, Width - Pad * 2, Fonts.UiSemibold.Height), T.Text, Draw.Line);
            y += Fonts.UiSemibold.Height + Dpi.S(4);
            int ih = Draw.WrapHeight(intro, Fonts.Small, Width - Pad * 2);
            Draw.Text(g, intro, Fonts.Small, new Rectangle(Pad, y, Width - Pad * 2, ih + 2), T.Text2, Draw.Wrap);
            y = HeadH(Width);
            int box = Dpi.S(16);
            for (int k = 0; k < items.Count; k++)
            {
                NeedItem it = items[k];
                int h = ItemH(it, Width);
                bool on = ticked.Contains(it.Id);
                RectangleF r = new RectangleF(Pad, y + Dpi.S(4), box, box);
                if (on)
                {
                    Shapes.Fill(g, r, Dpi.S(3), T.Accent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Pen p = new Pen(T.OnAccent, Math.Max(1.5f, Dpi.S(2))))
                    {
                        p.StartCap = p.EndCap = LineCap.Round;
                        p.LineJoin = LineJoin.Round;
                        g.DrawLines(p, new PointF[] { new PointF(r.X + box * 0.24f, r.Y + box * 0.52f), new PointF(r.X + box * 0.44f, r.Y + box * 0.72f), new PointF(r.X + box * 0.78f, r.Y + box * 0.3f) });
                    }
                }
                else Shapes.Stroke(g, r, Dpi.S(3), T.Text2, 1.5f);
                if (Focused && ShowFocusCues && k == focus) Shapes.Stroke(g, RectangleF.Inflate(r, 3, 3), Dpi.S(5), T.Text, 2f);
                int tw = Math.Max(Dpi.S(80), Width - TextX - Pad);
                int th = Draw.WrapHeight(it.Text, Fonts.Small, tw);
                Draw.Text(g, it.Text, Fonts.Small, new Rectangle(TextX, y + Dpi.S(5), tw, th + 2), on ? T.Text2 : T.Text, Draw.Wrap);
                y += h;
            }
            y += Dpi.S(8);
            string count = Ticked.ToString(CultureInfo.InvariantCulture) + " of " + items.Count.ToString(CultureInfo.InvariantCulture) + " ticked";
            Draw.Text(g, count, Fonts.Small, new Rectangle(Pad, y, Width / 2, Fonts.Small.Height), T.Text2, Draw.Line);
            if (!string.IsNullOrEmpty(moreText))
            {
                Font f = moreHot ? new Font(Fonts.Small, FontStyle.Underline) : Fonts.Small;
                try
                {
                    int w = Draw.Measure(moreText, f).Width + Dpi.S(4);
                    more = new Rectangle(Width - Pad - w, y, w, Fonts.Small.Height);
                    Draw.Text(g, moreText, f, more, T.Accent, Draw.Line);
                }
                finally { if (moreHot) f.Dispose(); }
            }
        }
    }
}
