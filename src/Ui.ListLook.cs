namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    // The small pieces of the Progress list: the kind tile in front of a row, the state pill and whether to animate.
    public static class ListLook
    {
        // ---------- motion ----------

        [DllImport("user32.dll")]
        static extern bool SystemParametersInfo(int action, int param, ref bool value, int ini);

        const int SpiGetClientAreaAnimation = 0x1042;
        static bool noSpi;
        static bool lastAnimations = true;
        static int lastAsked;
        static bool asked;

        // Off when Windows "Animation effects" is off. Asked at most once a second.
        public static bool Animations
        {
            get
            {
                int now = Environment.TickCount;
                if (asked && unchecked(now - lastAsked) < 1000) return lastAnimations;
                asked = true;
                lastAsked = now;
                bool on = true;
                if (!noSpi)
                {
                    try
                    {
                        bool v = true;
                        if (SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref v, 0)) on = v;
                    }
                    catch (Exception) { noSpi = true; }
                }
                try { if (!SystemInformation.UIEffectsEnabled) on = false; }
                catch (Exception) { }
                lastAnimations = on;
                return on;
            }
        }

        // 0 to 1 over ms milliseconds from start, eased out.
        public static float EaseOut(int start, int ms)
        {
            float t = Math.Max(0f, Math.Min(1f, unchecked(Environment.TickCount - start) / (float)ms));
            return 1f - (1f - t) * (1f - t) * (1f - t);
        }

        // ---------- kind tile ----------

        // Kinds fall into four families, each with one muted colour: what installs, scripts, configuration and the deployment's own steps.
        const int FamInstall = 0, FamScript = 1, FamConfig = 2, FamFlow = 3;

        static int Family(string kind)
        {
            switch (kind)
            {
                case Kinds.Win32App: case Kinds.MsiApp: case Kinds.StoreApp: case Kinds.Application: case Kinds.Office:
                case Kinds.Package: case Kinds.Update: case Kinds.Content:
                    return FamInstall;
                case Kinds.Script: return FamScript;
                case Kinds.Policy: case Kinds.Profile: case Kinds.Certificate: return FamConfig;
                default: return FamFlow;
            }
        }

        static readonly int[] Hues = { 0x8C96F2, 0xB48CEB, 0x62AED6, 0x8F9AAE };

        // Terminal and Amber draw everything in their one ink, so there the tile is the ink at low strength.
        static bool OneInk(Theme t)
        {
            return t.MonoNumbers;
        }

        public static Color KindInk(string kind, Theme t)
        {
            if (OneInk(t)) return t.Text2;
            Color hue = Theme.Hex(Hues[Family(kind)]);
            return t.IsDark ? Theme.Mix(hue, t.Text, 0.18f) : Theme.Mix(hue, t.Text, 0.42f);
        }

        public static Color KindFill(string kind, Theme t, Color under)
        {
            if (OneInk(t)) return Theme.Mix(under, t.Text, 0.09f);
            Color hue = Theme.Hex(Hues[Family(kind)]);
            return Theme.Mix(under, hue, t.IsDark ? 0.17f : 0.13f);
        }

        // Apps show their initials; everything else a small drawn glyph for its kind.
        static bool HasGlyph(string kind)
        {
            switch (kind)
            {
                case Kinds.Script: case Kinds.Policy: case Kinds.Profile: case Kinds.Certificate: case Kinds.Restart:
                case Kinds.Esp: case Kinds.Agent: case Kinds.Enrollment: case Kinds.Step: case Kinds.Package:
                case Kinds.Update: case Kinds.Content:
                    return true;
            }
            return false;
        }

        static readonly string[] MinorWords = { "a", "an", "and", "the", "for", "of", "to", "in", "on", "by", "with", "x64", "x86" };

        // "Contoso VPN Client 5.2" gives CV, "Microsoft 365 Apps for enterprise" MA, "7-Zip 24.08" 7Z.
        public static string Initials(string name)
        {
            string[] w = (name ?? "").Split(new char[] { ' ', '-', '_', '.', '(', ')', '/', '+', ',' }, StringSplitOptions.RemoveEmptyEntries);
            string s = "";
            for (int k = 0; k < w.Length && s.Length < 2; k++)
            {
                string x = w[k];
                if (!char.IsLetterOrDigit(x[0])) continue;
                if (s.Length > 0)
                {
                    if (!char.IsLetter(x[0])) continue;
                    if (Array.IndexOf(MinorWords, x.ToLowerInvariant()) >= 0) continue;
                }
                s += char.ToUpperInvariant(x[0]);
            }
            return s.Length > 0 ? s : "?";
        }

        // under: the surface the tile sits on. An expected item gets a dashed outline and no fill.
        public static void Tile(Graphics g, Item i, RectangleF r, Theme t, Color under)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float rad = Dpi.S(8);
            Color ink = KindInk(i.Kind, t);
            if (i.Expected)
            {
                ink = Theme.Mix(under, t.Text3, 0.85f);
                using (GraphicsPath p = Shapes.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), rad))
                using (Pen pen = new Pen(Theme.Mix(under, t.Text3, 0.75f), 1f))
                {
                    pen.DashPattern = new float[] { 3f, 2.5f };
                    g.DrawPath(pen, p);
                }
            }
            else Shapes.Fill(g, r, rad, KindFill(i.Kind, t, under));
            if (HasGlyph(i.Kind))
            {
                float s = (float)Math.Round(r.Width * 0.44f);
                RectangleF b = new RectangleF((float)Math.Round(r.X + (r.Width - s) / 2), (float)Math.Round(r.Y + (r.Height - s) / 2), s, s);
                Glyph(g, i.Kind, b, ink);
                return;
            }
            string tx = Initials(i.DisplayName);
            TextRenderer.DrawText(g, tx, Fonts.SmallSemibold, Rectangle.Round(r), ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        static PointF P(RectangleF b, float x, float y)
        {
            return new PointF(b.X + b.Width * x, b.Y + b.Height * y);
        }

        // A line glyph inside the box b: a 14 px square at 100 %.
        static void Glyph(Graphics g, string kind, RectangleF b, Color c)
        {
            using (Pen pen = new Pen(c, Math.Max(1.25f, Dpi.S(1.5f))))
            using (SolidBrush br = new SolidBrush(c))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case Kinds.Script:
                        g.DrawLines(pen, new PointF[] { P(b, 0.08f, 0.2f), P(b, 0.42f, 0.5f), P(b, 0.08f, 0.8f) });
                        g.DrawLine(pen, P(b, 0.54f, 0.84f), P(b, 0.94f, 0.84f));
                        break;
                    case Kinds.Policy:
                        using (GraphicsPath p = new GraphicsPath())
                        {
                            p.AddLine(P(b, 0.5f, 0.02f), P(b, 0.92f, 0.17f));
                            p.AddLine(P(b, 0.92f, 0.17f), P(b, 0.92f, 0.48f));
                            p.AddBezier(P(b, 0.92f, 0.48f), P(b, 0.92f, 0.74f), P(b, 0.72f, 0.9f), P(b, 0.5f, 0.99f));
                            p.AddBezier(P(b, 0.5f, 0.99f), P(b, 0.28f, 0.9f), P(b, 0.08f, 0.74f), P(b, 0.08f, 0.48f));
                            p.AddLine(P(b, 0.08f, 0.48f), P(b, 0.08f, 0.17f));
                            p.CloseFigure();
                            g.DrawPath(pen, p);
                        }
                        break;
                    case Kinds.Certificate:
                        g.DrawEllipse(pen, b.X + b.Width * 0.2f, b.Y, b.Width * 0.6f, b.Height * 0.6f);
                        g.DrawLines(pen, new PointF[] { P(b, 0.33f, 0.56f), P(b, 0.26f, 0.98f), P(b, 0.5f, 0.85f), P(b, 0.74f, 0.98f), P(b, 0.67f, 0.56f) });
                        break;
                    case Kinds.Profile:
                        {
                            float[] ys = { 0.16f, 0.5f, 0.84f };
                            float[] ks = { 0.68f, 0.3f, 0.58f };
                            float d = b.Width * 0.26f;
                            for (int k = 0; k < 3; k++)
                            {
                                g.DrawLine(pen, P(b, 0.02f, ys[k]), P(b, 0.98f, ys[k]));
                                PointF m = P(b, ks[k], ys[k]);
                                g.FillEllipse(br, m.X - d / 2, m.Y - d / 2, d, d);
                            }
                        }
                        break;
                    case Kinds.Esp:
                        {
                            float d = b.Width * 0.2f;
                            foreach (float y in new float[] { 0.14f, 0.5f, 0.86f })
                            {
                                PointF m = P(b, 0.1f, y);
                                g.FillEllipse(br, m.X - d / 2, m.Y - d / 2, d, d);
                                g.DrawLine(pen, P(b, 0.38f, y), P(b, 0.96f, y));
                            }
                        }
                        break;
                    case Kinds.Restart:
                        g.DrawArc(pen, b.X + b.Width * 0.06f, b.Y + b.Height * 0.08f, b.Width * 0.88f, b.Height * 0.88f, 20, 290);
                        Head(g, br, b, 0.5f + 0.44f * Cos(310), 0.52f + 0.44f * Sin(310), 310 + 90);
                        break;
                    case Kinds.Update:
                        g.DrawArc(pen, b.X + b.Width * 0.06f, b.Y + b.Height * 0.06f, b.Width * 0.88f, b.Height * 0.88f, 200, 130);
                        g.DrawArc(pen, b.X + b.Width * 0.06f, b.Y + b.Height * 0.06f, b.Width * 0.88f, b.Height * 0.88f, 20, 130);
                        Head(g, br, b, 0.5f + 0.44f * Cos(330), 0.5f + 0.44f * Sin(330), 330 + 90);
                        Head(g, br, b, 0.5f + 0.44f * Cos(150), 0.5f + 0.44f * Sin(150), 150 + 90);
                        break;
                    case Kinds.Enrollment:
                        g.DrawLines(pen, new PointF[] { P(b, 0.56f, 0.04f), P(b, 0.94f, 0.04f), P(b, 0.94f, 0.96f), P(b, 0.56f, 0.96f) });
                        g.DrawLine(pen, P(b, 0.04f, 0.5f), P(b, 0.68f, 0.5f));
                        g.DrawLines(pen, new PointF[] { P(b, 0.46f, 0.28f), P(b, 0.68f, 0.5f), P(b, 0.46f, 0.72f) });
                        break;
                    case Kinds.Agent:
                        g.DrawLines(pen, new PointF[] { P(b, 0.0f, 0.56f), P(b, 0.24f, 0.56f), P(b, 0.38f, 0.16f), P(b, 0.6f, 0.86f), P(b, 0.74f, 0.44f), P(b, 1f, 0.44f) });
                        break;
                    case Kinds.Step:
                        g.DrawLines(pen, new PointF[] { P(b, 0.04f, 0.92f), P(b, 0.36f, 0.92f), P(b, 0.36f, 0.6f), P(b, 0.66f, 0.6f), P(b, 0.66f, 0.28f), P(b, 0.96f, 0.28f) });
                        g.DrawLine(pen, P(b, 0.96f, 0.28f), P(b, 0.96f, 0.06f));
                        break;
                    case Kinds.Package:
                        g.DrawPolygon(pen, new PointF[] { P(b, 0.5f, 0.02f), P(b, 0.94f, 0.25f), P(b, 0.94f, 0.75f), P(b, 0.5f, 0.98f), P(b, 0.06f, 0.75f), P(b, 0.06f, 0.25f) });
                        g.DrawLines(pen, new PointF[] { P(b, 0.06f, 0.25f), P(b, 0.5f, 0.48f), P(b, 0.94f, 0.25f) });
                        g.DrawLine(pen, P(b, 0.5f, 0.48f), P(b, 0.5f, 0.98f));
                        break;
                    case Kinds.Content:
                        g.DrawLine(pen, P(b, 0.5f, 0.02f), P(b, 0.5f, 0.62f));
                        g.DrawLines(pen, new PointF[] { P(b, 0.26f, 0.4f), P(b, 0.5f, 0.64f), P(b, 0.74f, 0.4f) });
                        g.DrawLines(pen, new PointF[] { P(b, 0.04f, 0.62f), P(b, 0.04f, 0.96f), P(b, 0.96f, 0.96f), P(b, 0.96f, 0.62f) });
                        break;
                }
            }
        }

        static float Cos(float deg) { return (float)Math.Cos(deg * Math.PI / 180); }
        static float Sin(float deg) { return (float)Math.Sin(deg * Math.PI / 180); }

        // A small filled arrow head at (x, y) in the box, pointing along the angle in degrees.
        static void Head(Graphics g, Brush br, RectangleF b, float x, float y, float angle)
        {
            float dx = Cos(angle), dy = Sin(angle);
            float nx = -dy, ny = dx;
            float s = b.Width;
            PointF c = new PointF(b.X + s * x, b.Y + s * y);
            PointF tip = new PointF(c.X + dx * s * 0.2f, c.Y + dy * s * 0.2f);
            PointF l = new PointF(c.X - dx * s * 0.06f + nx * s * 0.2f, c.Y - dy * s * 0.06f + ny * s * 0.2f);
            PointF rr = new PointF(c.X - dx * s * 0.06f - nx * s * 0.2f, c.Y - dy * s * 0.06f - ny * s * 0.2f);
            g.FillPolygon(br, new PointF[] { tip, l, rr });
        }

        // ---------- state pill ----------

        public static int PillH { get { return Dpi.S(22); } }

        public static int PillWidth(string text)
        {
            return TextRenderer.MeasureText(text, Fonts.SmallSemibold, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Dpi.S(20);
        }

        // A rounded state pill: a soft tint of the colour with the label in it, or a dashed outline for something only expected.
        public static void Pill(Graphics g, string text, RectangleF r, Color c, Color under, Theme t, bool dashed)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float rad = r.Height / 2;
            if (dashed)
            {
                // The outline is one pixel smaller than the pill, and so is its radius.
                using (GraphicsPath p = Shapes.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), (r.Height - 1) / 2))
                using (Pen pen = new Pen(Theme.Mix(under, c, 0.7f), 1f))
                {
                    pen.DashPattern = new float[] { 3f, 2.5f };
                    g.DrawPath(pen, p);
                }
            }
            else Shapes.Fill(g, r, rad, Tint(c, under, t));
            Rectangle tr = Rectangle.Round(new RectangleF(r.X + Dpi.S(8), r.Y, r.Width - Dpi.S(16), r.Height));
            TextRenderer.DrawText(g, text, Fonts.SmallSemibold, tr, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        public static Color Tint(Color c, Color under, Theme t)
        {
            return Theme.Mix(under, c, t.IsDark ? 0.17f : 0.11f);
        }

        // A thin rounded bar from 0 to 1.
        public static void Bar(Graphics g, RectangleF r, double value, Color fill, Color track)
        {
            float rad = r.Height / 2;
            Shapes.Fill(g, r, rad, track);
            float w = (float)(r.Width * Math.Max(0, Math.Min(1, value)));
            if (w > 0.5f) Shapes.Fill(g, new RectangleF(r.X, r.Y, Math.Max(w, r.Height), r.Height), rad, fill);
        }

        // The same bar with a segment sliding through it when the progress is not known; pos runs from 0 to 1.
        public static void Sliding(Graphics g, RectangleF r, float pos, Color fill, Color track)
        {
            float rad = r.Height / 2;
            Shapes.Fill(g, r, rad, track);
            float seg = r.Width * 0.32f;
            float x = r.X + pos * (r.Width + seg) - seg;
            float x0 = Math.Max(r.X, x), x1 = Math.Min(r.Right, x + seg);
            if (x1 - x0 > 1) Shapes.Fill(g, new RectangleF(x0, r.Y, x1 - x0, r.Height), rad, fill);
        }
    }

    // The search box of the Progress tab: a rounded field with a magnifier, a placeholder and a clear button around a plain TextBox.
    public sealed class SearchField : FlatControl
    {
        readonly TextBox box;
        readonly Hint hint = new Hint();
        bool overClear, downClear;
        string placeholder = "";
        string shortcut = "Ctrl+F";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public SearchField(TextBox b)
        {
            box = b;
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Height = Dpi.S(32);
            Width = Dpi.S(320);
            box.BorderStyle = BorderStyle.None;
            box.Font = Fonts.Ui;
            box.TabStop = true;
            Controls.Add(box);
            hint.Font = Fonts.Ui;
            hint.Cursor = Cursors.IBeam;
            hint.MouseDown += delegate { box.Focus(); };
            Controls.Add(hint);
            hint.BringToFront();
            box.TextChanged += delegate { Sync(); };
            box.GotFocus += delegate { Sync(); };
            box.LostFocus += delegate { Sync(); };
            box.MouseEnter += delegate { Hover(); };
            box.MouseLeave += delegate { Hover(); };
            hint.MouseEnter += delegate { Hover(); };
            hint.MouseLeave += delegate { Hover(); };
            box.HandleCreated += delegate { NativeLook(); };
            if (box.IsHandleCreated) NativeLook();
            Cursor = Cursors.IBeam;
            Place();
            Sync();
        }

        // Shown in the empty field. Windows shows the same words while the field has the focus and is still empty.
        public string Placeholder
        {
            get { return placeholder; }
            set
            {
                placeholder = value ?? "";
                hint.Text = placeholder;
                NativeLook();
                Invalidate();
            }
        }

        // The shortcut shown at the right end of the empty field, or null for none.
        public string Shortcut
        {
            get { return shortcut; }
            set { shortcut = value; Place(); Invalidate(); }
        }

        void NativeLook()
        {
            if (!box.IsHandleCreated) return;
            try
            {
                // No inner margins, so typed text starts exactly where the placeholder does.
                SendMessage(box.Handle, 0xD3, (IntPtr)3, IntPtr.Zero);
                SendMessage(box.Handle, 0x1501, (IntPtr)1, placeholder);
            }
            catch (Exception) { }
        }

        int TextX { get { return Dpi.S(36); } }

        int ShortcutWidth
        {
            get { return string.IsNullOrEmpty(shortcut) ? 0 : TextRenderer.MeasureText(shortcut, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(12); }
        }

        // The key cap shows while the field is empty and has no focus; the text box then stops short of it.
        bool ShowsShortcut
        {
            get { return box.Text.Length == 0 && !box.Focused && !string.IsNullOrEmpty(shortcut); }
        }

        void Place()
        {
            if (box == null) return;
            int h = box.Font.Height;
            int y = Math.Max(0, (Height - h) / 2);
            int kw = ShortcutWidth;
            int end = ShowsShortcut ? Width - Dpi.S(18) - kw : Width - Dpi.S(36);
            box.SetBounds(TextX, y, Math.Max(Dpi.S(10), end - TextX), h);
            hint.SetBounds(TextX, y, Math.Max(Dpi.S(10), end - TextX), h);
        }

        void Sync()
        {
            bool show = box.Text.Length == 0 && !box.Focused;
            if (hint.Visible != show) hint.Visible = show;
            if (!HasText) { overClear = false; downClear = false; }
            Place();
            Invalidate();
        }

        void Hover()
        {
            bool h = ClientRectangle.Contains(PointToClient(Cursor.Position));
            if (h != hover) { hover = h; Invalidate(); }
        }

        Rectangle ClearRect
        {
            get { return new Rectangle(Width - Dpi.S(30), (Height - Dpi.S(22)) / 2, Dpi.S(22), Dpi.S(22)); }
        }

        bool HasText { get { return box.Text.Length > 0; } }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            box.BackColor = t.Field;
            box.ForeColor = t.Text;
            hint.BackColor = t.Field;
            hint.ForeColor = Theme.Mix(t.Field, t.Text3, 0.9f);
            hint.Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Place();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool o = HasText && ClearRect.Contains(e.Location);
            if (o != overClear) { overClear = o; Cursor = o ? Cursors.Hand : Cursors.IBeam; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            overClear = false;
            downClear = false;
            base.OnMouseLeave(e);
            Hover();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            downClear = e.Button == MouseButtons.Left && HasText && ClearRect.Contains(e.Location);
            box.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (downClear && HasText && ClearRect.Contains(e.Location)) box.Text = "";
            downClear = false;
            base.OnMouseUp(e);
            box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            bool focus = box.Focused;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Dpi.S(10);
            Shapes.Fill(g, r, rad, T.Field);
            Color edge = focus ? T.Accent : hover ? Theme.Mix(T.FieldStroke, T.Text3, 0.55f) : T.FieldStroke;
            Shapes.Stroke(g, r, rad, edge, focus ? 1.5f : 1f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Dpi.S(17), cy = Height / 2f - Dpi.S(1), rr = Dpi.S(5);
            using (Pen pen = new Pen(focus || HasText ? T.Text2 : T.Text3, Math.Max(1.25f, Dpi.S(1.5f))))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
                g.DrawLine(pen, cx + rr * 0.72f, cy + rr * 0.72f, cx + rr * 1.55f, cy + rr * 1.55f);
            }
            if (HasText)
            {
                Rectangle cr = ClearRect;
                if (overClear) Shapes.Fill(g, cr, cr.Width / 2f, downClear ? T.Pressed : T.Hover);
                float a = Dpi.S(3.5f), mx = cr.X + cr.Width / 2f, my = cr.Y + cr.Height / 2f;
                using (Pen pen = new Pen(overClear ? T.Text : T.Text3, Math.Max(1.25f, Dpi.S(1.5f))))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, mx - a, my - a, mx + a, my + a);
                    g.DrawLine(pen, mx + a, my - a, mx - a, my + a);
                }
            }
            else if (ShowsShortcut)
            {
                // The shortcut as a small key cap at the right end.
                int kw = ShortcutWidth;
                RectangleF kr = new RectangleF(Width - Dpi.S(8) - kw, (Height - Dpi.S(20)) / 2f, kw, Dpi.S(20));
                Shapes.Stroke(g, kr, Dpi.S(5), Theme.Mix(T.Field, T.FieldStroke, 0.9f), 1f);
                TextRenderer.DrawText(g, shortcut, Fonts.Small, Rectangle.Round(kr), T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        // The placeholder while the field is empty and has no focus, drawn where the text would start.
        sealed class Hint : Control
        {
            public Hint()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }

            protected override void OnTextChanged(EventArgs e)
            {
                Invalidate();
                base.OnTextChanged(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
