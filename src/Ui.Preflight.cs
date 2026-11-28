namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Threading;
    using System.Windows.Forms;

    // What the pre-flight check reads besides Autopilot and the Enrollment Status Pages, kept between starts.
    public static class PreflightPrefs
    {
        public static bool Apps
        {
            get { return Prefs.GetBool("Preflight.Apps", true); }
            set { Prefs.SetBool("Preflight.Apps", value); }
        }

        public static bool Groups
        {
            get { return Prefs.GetBool("Preflight.Groups", true); }
            set { Prefs.SetBool("Preflight.Groups", value); }
        }
    }

    // Shared drawing of the pre-flight page: colours of the three levels, the marks, and short eased motion.
    static class PfDraw
    {
        public const int Corner = 10;
        static Font big;

        // The large number of the summary, about 22 px.
        public static Font Big
        {
            get
            {
                if (big == null) big = new Font(Fonts.Title.FontFamily, 16.5f, Fonts.Title.Style);
                return big;
            }
        }

        // Motion is off when Windows is set to show no animations.
        public static bool Motion
        {
            get
            {
                try { return SystemInformation.UIEffectsEnabled; }
                catch (Exception) { return false; }
            }
        }

        public static float EaseOut(float t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            float u = 1 - t;
            return 1 - u * u * u;
        }

        public static Color Fg(Theme t, PfLevel l)
        {
            return l == PfLevel.Fail ? t.ErrFg : l == PfLevel.Warn ? t.WarnFg : t.OkFg;
        }

        public static Color Fg(Theme t, PfVerdict v)
        {
            return v == PfVerdict.WillFail ? t.ErrFg : v == PfVerdict.Check ? t.WarnFg : t.OkFg;
        }

        public static Color Soft(Theme t, PfVerdict v)
        {
            return v == PfVerdict.WillFail ? t.ErrBg : v == PfVerdict.Check ? t.WarnBg : Theme.Mix(t.Card, t.OkFg, t.IsDark ? 0.18f : 0.13f);
        }

        public static void Card(Graphics g, Control c, Theme t, Color fill)
        {
            g.Clear(t.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, c.Width - 1.5f, c.Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(Corner), fill);
            Shapes.Stroke(g, r, Dpi.S(Corner), t.Divider, 1f);
        }

        // A check, an exclamation mark or a cross, drawn so it does not depend on a font. A pass is a quiet check;
        // a warning and a failure sit on a soft disc of their colour.
        public static void Mark(Graphics g, Theme t, PfLevel l, RectangleF r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fg = Fg(t, l);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 16f;
            if (l != PfLevel.Ok)
                using (SolidBrush b = new SolidBrush(l == PfLevel.Fail ? t.ErrBg : t.WarnBg)) g.FillEllipse(b, r);
            using (Pen p = new Pen(fg, Math.Max(1.5f, 1.7f * u)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                if (l == PfLevel.Ok)
                    g.DrawLines(p, new PointF[] { new PointF(cx - 4.2f * u, cy + 0.2f * u), new PointF(cx - 1.2f * u, cy + 3.2f * u), new PointF(cx + 4.6f * u, cy - 3.4f * u) });
                else if (l == PfLevel.Warn)
                {
                    g.DrawLine(p, cx, cy - 4f * u, cx, cy + 0.8f * u);
                    g.DrawLine(p, cx, cy + 3.9f * u, cx, cy + 4f * u);
                }
                else
                {
                    g.DrawLine(p, cx - 3f * u, cy - 3f * u, cx + 3f * u, cy + 3f * u);
                    g.DrawLine(p, cx + 3f * u, cy - 3f * u, cx - 3f * u, cy + 3f * u);
                }
            }
        }
    }

    // The top of the page: what the check does, the customer it reads from, and the permissions it reads with.
    sealed class PreflightHeader : FlatControl, IMeasured
    {
        public readonly CustomerPicker Picker = new CustomerPicker();
        const string Title = "Pre-flight check";
        const string Lead = "Asks Intune, before a device is turned on, whether it will deploy: the Autopilot registration and profile, its groups, the Enrollment Status Page and the apps it waits for. Read-only.";
        const string PermLead = "Reads with these permissions. A check without its permission says so, and the others go on.";

        public PreflightHeader()
        {
            SetStyle(ControlStyles.Selectable, false);
            Controls.Add(Picker);
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            BackColor = t.Bg;
            Picker.ApplyTheme(t);
        }

        int Pad
        {
            get { return Dpi.S(2); }
        }

        // The permission and what it is for side by side, or under each other in a narrow window.
        bool Stacked(int width)
        {
            return width < Dpi.S(620);
        }

        int NameWidth
        {
            get
            {
                int w = 0;
                foreach (string[] p in Preflight.PermissionUse) w = Math.Max(w, Draw.Measure(p[0], Fonts.SmallSemibold).Width);
                return w + Dpi.S(16);
            }
        }

        public int Measure(int width)
        {
            int textW = Math.Max(Dpi.S(160), width - Pad * 2);
            bool pickerBeside = width >= Dpi.S(520);
            int y = Dpi.S(4);
            int titleH = Math.Max(Fonts.Title.Height + Dpi.S(6), Picker.Height);
            if (pickerBeside) Picker.Location = new Point(width - Picker.Width, y + (titleH - Picker.Height) / 2);
            y += titleH;
            if (!pickerBeside)
            {
                Picker.Location = new Point(Pad, y + Dpi.S(2));
                y += Picker.Height + Dpi.S(8);
            }
            y += Dpi.S(2) + Draw.WrapHeight(Lead, Fonts.Ui, textW) + Dpi.S(12);
            y += Draw.WrapHeight(PermLead, Fonts.Small, textW) + Dpi.S(6);
            int row = Fonts.Small.Height + Dpi.S(5);
            if (Stacked(width))
                foreach (string[] p in Preflight.PermissionUse) y += Fonts.Small.Height + Dpi.S(2) + Draw.WrapHeight(p[1], Fonts.Small, textW - Dpi.S(12)) + Dpi.S(5);
            else y += row * Preflight.PermissionUse.Length;
            return y + Dpi.S(6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            int width = Width;
            int textW = Math.Max(Dpi.S(160), width - Pad * 2);
            bool pickerBeside = width >= Dpi.S(520);
            int y = Dpi.S(4);
            int titleH = Math.Max(Fonts.Title.Height + Dpi.S(6), Picker.Height);
            Draw.Text(g, Title, Fonts.Title, new Rectangle(Pad, y, pickerBeside ? Math.Max(0, Picker.Left - Pad - Dpi.S(12)) : textW, titleH), T.Text, Draw.Line);
            y += titleH;
            if (!pickerBeside) y += Picker.Height + Dpi.S(8);
            y += Dpi.S(2);
            int h = Draw.WrapHeight(Lead, Fonts.Ui, textW);
            Draw.Text(g, Lead, Fonts.Ui, new Rectangle(Pad, y, textW, h + 2), T.Text2, Draw.Wrap);
            y += h + Dpi.S(12);
            h = Draw.WrapHeight(PermLead, Fonts.Small, textW);
            Draw.Text(g, PermLead, Fonts.Small, new Rectangle(Pad, y, textW, h + 2), T.Text3, Draw.Wrap);
            y += h + Dpi.S(6);
            int row = Fonts.Small.Height + Dpi.S(5);
            int nameW = NameWidth;
            foreach (string[] p in Preflight.PermissionUse)
            {
                float dot = Dpi.S(4);
                using (SolidBrush b = new SolidBrush(T.Accent))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(b, Pad + Dpi.S(1), y + (Fonts.Small.Height - dot) / 2f + 1, dot, dot);
                }
                int x = Pad + Dpi.S(12);
                if (Stacked(width))
                {
                    Draw.Text(g, p[0], Fonts.SmallSemibold, new Rectangle(x, y, textW - Dpi.S(12), Fonts.Small.Height + 2), T.Text, Draw.Line);
                    y += Fonts.Small.Height + Dpi.S(2);
                    int uh = Draw.WrapHeight(p[1], Fonts.Small, textW - Dpi.S(12));
                    Draw.Text(g, p[1], Fonts.Small, new Rectangle(x, y, textW - Dpi.S(12), uh + 2), T.Text3, Draw.Wrap);
                    y += uh + Dpi.S(5);
                }
                else
                {
                    Draw.Text(g, p[0], Fonts.SmallSemibold, new Rectangle(x, y, nameW, Fonts.Small.Height + 2), T.Text, Draw.Line);
                    Draw.Text(g, p[1], Fonts.Small, new Rectangle(x + nameW, y, Math.Max(0, width - x - nameW - Pad), Fonts.Small.Height + 2), T.Text3, Draw.Line);
                    y += row;
                }
            }
        }
    }

    // Where the serial numbers go, with what to read and the button that starts the check.
    sealed class PreflightInput : Panel, IThemed, IMeasured
    {
        public readonly TextBox Serials = new TextBox();
        public readonly Toggle Apps = new Toggle();
        public readonly Toggle Groups = new Toggle();
        public readonly FlatButton Check = new FlatButton("Check", Glyphs.Search);
        Theme theme = Theme.Light();
        const string Title = "Serial numbers";
        const string Hint = "One per line, or paste a list or a CSV such as a hardware hash export. Ctrl+Enter checks.";
        const string AppsText = "Apps", AppsHint = "Assignments and dependencies";
        const string GroupsText = "Groups", GroupsHint = "Of the device and its user";
        Rectangle titleAt, hintAt, appsAt, groupsAt;

        public PreflightInput()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Serials.Multiline = true;
            Serials.AcceptsReturn = true;
            Serials.ScrollBars = ScrollBars.Vertical;
            Serials.WordWrap = false;
            Serials.BorderStyle = BorderStyle.FixedSingle;
            Serials.Font = Fonts.Mono;
            Serials.AccessibleName = Title;
            Apps.AccessibleName = AppsText;
            Groups.AccessibleName = GroupsText;
            Check.Primary = true;
            Check.Width = Math.Max(Check.Width, Dpi.S(112));
            Controls.Add(Serials);
            Controls.Add(Apps);
            Controls.Add(Groups);
            Controls.Add(Check);
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            Theme.CornerScale = t.Corners;
            BackColor = t.Card;
            Serials.BackColor = t.Field;
            Serials.ForeColor = t.Text;
            Apps.ApplyTheme(t);
            Groups.ApplyTheme(t);
            Check.ApplyTheme(t);
            Invalidate();
        }

        // The options sit beside the serial numbers, or under them in a narrow window.
        public int Measure(int width)
        {
            int pad = Dpi.S(16);
            bool beside = width >= Dpi.S(640);
            int side = Dpi.S(300);
            int boxW = beside ? width - pad * 3 - side : width - pad * 2;
            int y = pad - Dpi.S(2);
            titleAt = new Rectangle(pad, y, boxW, Fonts.UiSemibold.Height + 2);
            y += Fonts.UiSemibold.Height + Dpi.S(4);
            int hh = Draw.WrapHeight(Hint, Fonts.Small, boxW);
            hintAt = new Rectangle(pad, y, boxW, hh + 2);
            y += hh + Dpi.S(8);
            int boxH = Dpi.S(beside ? 104 : 88);
            Serials.SetBounds(pad, y, boxW, boxH);
            int bottom = y + boxH;

            int ox = beside ? pad * 2 + boxW : pad;
            int ow = beside ? side : boxW;
            int oy = beside ? pad - Dpi.S(2) : bottom + Dpi.S(12);
            int rowH = Dpi.S(40);
            appsAt = new Rectangle(ox, oy, ow - Apps.Width - Dpi.S(4), rowH);
            Apps.Location = new Point(ox + ow - Apps.Width, oy + (rowH - Apps.Height) / 2);
            oy += rowH + Dpi.S(4);
            groupsAt = new Rectangle(ox, oy, ow - Groups.Width - Dpi.S(4), rowH);
            Groups.Location = new Point(ox + ow - Groups.Width, oy + (rowH - Groups.Height) / 2);
            oy += rowH + Dpi.S(12);
            if (beside) Check.Location = new Point(ox + ow - Check.Width, Math.Max(oy, bottom - Check.Height));
            else Check.Location = new Point(ox, oy);
            bottom = Math.Max(bottom, Check.Bottom);
            Invalidate();
            return bottom + pad;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PfDraw.Card(g, this, theme, theme.Card);
            Draw.Text(g, Title, Fonts.UiSemibold, titleAt, theme.Text, Draw.Line);
            Draw.Text(g, Hint, Fonts.Small, hintAt, theme.Text3, Draw.Wrap);
            Option(g, appsAt, AppsText, AppsHint);
            Option(g, groupsAt, GroupsText, GroupsHint);
        }

        void Option(Graphics g, Rectangle r, string title, string hint)
        {
            int th = Fonts.Ui.Height, hh = Fonts.Small.Height;
            int y = r.Y + (r.Height - th - hh) / 2;
            Draw.Text(g, title, Fonts.Ui, new Rectangle(r.X, y, r.Width, th + 2), theme.Text, Draw.Line);
            Draw.Text(g, hint, Fonts.Small, new Rectangle(r.X, y + th, r.Width, hh + 2), theme.Text3, Draw.Line);
        }
    }

    // How the checked devices came out: a ring for the share that is ready, and the three counts. Before the first
    // check it says what to do, and during a check it shows how far it is.
    sealed class PreflightSummary : FlatControl, IMeasured
    {
        int ready, check, fail, total = -1;
        int doing = -1, of;
        string doingSerial, note, emptyText = "";
        float shown = 1f;
        DateTime animStart;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public PreflightSummary()
        {
            SetStyle(ControlStyles.Selectable, false);
            timer.Interval = 15;
            timer.Tick += delegate
            {
                float t = (float)((DateTime.UtcNow - animStart).TotalMilliseconds / 200.0);
                shown = PfDraw.EaseOut(t);
                if (t >= 1) { shown = 1f; timer.Stop(); }
                Invalidate();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        public void Empty(string text)
        {
            total = -1;
            doing = -1;
            emptyText = text ?? "";
            Invalidate();
        }

        public void Busy(int index, int count, string serial)
        {
            doing = index;
            of = count;
            doingSerial = serial;
            Invalidate();
        }

        public void Set(List<PfResult> results, string source)
        {
            Preflight.Count(results, out ready, out check, out fail);
            total = results.Count;
            doing = -1;
            note = source;
            shown = 1f;
            if (PfDraw.Motion && total > 0)
            {
                shown = 0f;
                animStart = DateTime.UtcNow;
                timer.Start();
            }
            Invalidate();
        }

        public int Measure(int width)
        {
            return Dpi.S(width < Dpi.S(560) && total >= 0 && doing < 0 ? 148 : 96);
        }

        void Ring(Graphics g, RectangleF r, float share, Color arc)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Dpi.S(6);
            using (Pen track = new Pen(Theme.Mix(T.Card, T.Text3, 0.28f), w)) g.DrawEllipse(track, r);
            if (share <= 0) return;
            using (Pen p = new Pen(arc, w))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                if (share >= 0.999f) g.DrawEllipse(p, r);
                else g.DrawArc(p, r, -90, 360f * share);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PfDraw.Card(g, this, T, T.Card);
            int pad = Dpi.S(20);
            int ring = Dpi.S(56);
            int top = Dpi.S(20);
            RectangleF rr = new RectangleF(pad + Dpi.S(3), top, ring, ring);
            int x = pad + ring + Dpi.S(22);
            int w = Math.Max(0, Width - x - pad);
            if (doing >= 0)
            {
                Ring(g, rr, of > 0 ? (float)doing / of : 0, T.Accent);
                Draw.Text(g, "Checking " + (Math.Min(doing + 1, of)).ToString(CultureInfo.InvariantCulture) + " of " + of.ToString(CultureInfo.InvariantCulture) + "…", Fonts.UiSemibold, new Rectangle(x, top + Dpi.S(8), w, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
                Draw.Text(g, doingSerial ?? "", T.Num(Fonts.Ui), new Rectangle(x, top + Dpi.S(10) + Fonts.UiSemibold.Height, w, Fonts.Ui.Height + 2), T.Text3, Draw.Line);
                return;
            }
            if (total < 0)
            {
                Ring(g, rr, 0, T.Accent);
                Draw.Text(g, "No devices checked yet", Fonts.UiSemibold, new Rectangle(x, top + Dpi.S(8), w, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
                Draw.Text(g, emptyText, Fonts.Ui, new Rectangle(x, top + Dpi.S(10) + Fonts.UiSemibold.Height, w, Fonts.Ui.Height + 2), T.Text3, Draw.Line);
                return;
            }
            float share = total > 0 ? (float)ready / total : 0;
            Ring(g, rr, share * shown, T.OkFg);
            string pct = ((int)Math.Round(share * 100)).ToString(CultureInfo.InvariantCulture) + "%";
            TextRenderer.DrawText(g, pct, T.Num(Fonts.UiSemibold), Rectangle.Round(rr), T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            bool narrow = Width < Dpi.S(560);
            int colW = narrow ? Math.Max(Dpi.S(64), w / 3) : Dpi.S(112);
            Count(g, x, top, colW, ready, "Ready", T.OkFg);
            Count(g, x + colW, top, colW, check, "Check", T.WarnFg);
            Count(g, x + colW * 2, top, colW, fail, "Will fail", T.ErrFg);
            string head = total.ToString(CultureInfo.InvariantCulture) + (total == 1 ? " device checked" : " devices checked");
            if (narrow)
            {
                int ny = top + ring + Dpi.S(12);
                Draw.Text(g, head, Fonts.UiSemibold, new Rectangle(pad, ny, Width - pad * 2, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
                Draw.Text(g, note ?? "", Fonts.Small, new Rectangle(pad, ny + Fonts.UiSemibold.Height + Dpi.S(2), Width - pad * 2, Fonts.Small.Height + 2), T.Text3, Draw.Line);
            }
            else
            {
                int nx = x + colW * 3 + Dpi.S(8);
                int nw = Math.Max(0, Width - nx - pad);
                Draw.Text(g, head, Fonts.UiSemibold, new Rectangle(nx, top + Dpi.S(8), nw, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line | TextFormatFlags.Right);
                Draw.Text(g, note ?? "", Fonts.Small, new Rectangle(nx, top + Dpi.S(11) + Fonts.UiSemibold.Height, nw, Fonts.Small.Height + 2), T.Text3, Draw.Line | TextFormatFlags.Right);
            }
        }

        // A count with its label under it. A count of zero is grey: nothing to look at there.
        void Count(Graphics g, int x, int top, int w, int n, string label, Color c)
        {
            Font f = T.MonoNumbers ? Fonts.MonoTitle : PfDraw.Big;
            Draw.Text(g, n.ToString(CultureInfo.InvariantCulture), f, new Rectangle(x, top + Dpi.S(2), w, f.Height + 2), n == 0 ? T.Text3 : c, Draw.Line);
            Draw.Text(g, label, Fonts.Small, new Rectangle(x + 1, top + Dpi.S(4) + f.Height, w, Fonts.Small.Height + 2), T.Text2, Draw.Line);
        }
    }

    // One device: its verdict, what Intune knows about it, and the checklist. The top row opens and closes the list.
    sealed class PreflightCard : FlatControl, IMeasured
    {
        public readonly PfResult Result;
        public event EventHandler Resized;
        bool open;
        float shown;
        DateTime animStart;
        float animFrom;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool headHover;

        sealed class Row
        {
            public string Group;
            public PfFinding Line;
            public int Y, TextH, FixH;
        }

        readonly List<Row> rows = new List<Row>();
        int laidFor = -1, bodyH, factsH;
        string facts;

        public PreflightCard(PfResult r)
        {
            Result = r;
            TabStop = true;
            open = r.Verdict != PfVerdict.Ready;
            shown = open ? 1f : 0f;
            AccessibleName = r.Serial + ": " + r.VerdictText;
            List<string> parts = new List<string>();
            foreach (string[] f in r.Facts) parts.Add(f[0] + " " + f[1]);
            facts = string.Join("   ·   ", parts.ToArray());
            timer.Interval = 15;
            timer.Tick += delegate
            {
                float t = (float)((DateTime.UtcNow - animStart).TotalMilliseconds / 160.0);
                float to = open ? 1f : 0f;
                shown = animFrom + (to - animFrom) * PfDraw.EaseOut(t);
                if (t >= 1) { shown = to; timer.Stop(); }
                if (Resized != null) Resized(this, EventArgs.Empty);
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        public bool Open
        {
            get { return open; }
            set
            {
                if (open == value) return;
                open = value;
                if (PfDraw.Motion && IsHandleCreated && Visible)
                {
                    animFrom = shown;
                    animStart = DateTime.UtcNow;
                    timer.Start();
                }
                else
                {
                    shown = open ? 1f : 0f;
                    if (Resized != null) Resized(this, EventArgs.Empty);
                }
                Invalidate();
            }
        }

        int Pad { get { return Dpi.S(16); } }
        int HeadH { get { return Dpi.S(60); } }
        int MarkW { get { return Dpi.S(16); } }
        int TextX { get { return Pad + MarkW + Dpi.S(12); } }

        void LayOut(int width)
        {
            if (laidFor == width) return;
            laidFor = width;
            rows.Clear();
            int textW = Math.Max(Dpi.S(120), width - TextX - Pad);
            int y = Dpi.S(4);
            factsH = facts.Length > 0 ? Draw.WrapHeight(facts, Fonts.Small, width - Pad * 2) : 0;
            if (factsH > 0) y += factsH + Dpi.S(10);
            string group = null;
            foreach (PfFinding f in Result.Findings)
            {
                if (f.Group != group)
                {
                    group = f.Group;
                    Row gr = new Row();
                    gr.Group = group;
                    y += rows.Count > 0 ? Dpi.S(8) : 0;
                    gr.Y = y;
                    rows.Add(gr);
                    y += Fonts.SmallSemibold.Height + Dpi.S(8);
                }
                Row r = new Row();
                r.Line = f;
                r.Y = y;
                r.TextH = Draw.WrapHeight(f.Text, Fonts.Ui, textW);
                y += r.TextH;
                if (f.Fix != null && f.Level != PfLevel.Ok)
                {
                    r.FixH = Draw.WrapHeight(f.Fix, Fonts.Ui, textW - Dpi.S(12));
                    y += Dpi.S(4) + r.FixH;
                }
                y += Dpi.S(8);
                rows.Add(r);
            }
            bodyH = y + Dpi.S(8);
        }

        public int Measure(int width)
        {
            LayOut(width);
            return HeadH + (int)Math.Round(bodyH * shown);
        }

        string Counts
        {
            get
            {
                int ok = Result.Count(PfLevel.Ok), warn = Result.Count(PfLevel.Warn), bad = Result.Count(PfLevel.Fail);
                List<string> parts = new List<string>();
                if (bad > 0) parts.Add(bad.ToString(CultureInfo.InvariantCulture) + (bad == 1 ? " failure" : " failures"));
                if (warn > 0) parts.Add(warn.ToString(CultureInfo.InvariantCulture) + " to check");
                if (ok > 0 || parts.Count == 0) parts.Add(ok.ToString(CultureInfo.InvariantCulture) + " passed");
                return string.Join("  ·  ", parts.ToArray());
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            LayOut(Width);
            PfDraw.Card(g, this, T, T.Card);
            int head = HeadH;
            // A pointer that left without a message (a window closed over it) must not leave the row lit.
            if (headHover && !down)
            {
                Point at = PointToClient(Cursor.Position);
                if (!ClientRectangle.Contains(at) || at.Y >= head) headHover = false;
            }
            if (headHover || down)
            {
                // The hover tint keeps the rounded top of the card and is square where the list follows.
                GraphicsState st = g.Save();
                g.SetClip(new Rectangle(1, 1, Width - 3, Math.Min(head, Height - 2)));
                Shapes.Fill(g, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height + Dpi.S(20)), Dpi.S(PfDraw.Corner) - 1, down ? T.Pressed : T.Hover);
                g.Restore(st);
            }
            PfVerdict v = Result.Verdict;
            string vt = Result.VerdictText;
            int pillW = Math.Max(Dpi.S(74), Draw.Measure(vt, Fonts.SmallSemibold).Width + Dpi.S(22));
            int pillH = Dpi.S(24);
            RectangleF pill = new RectangleF(Pad, (head - pillH) / 2f, pillW, pillH);
            Shapes.Fill(g, pill, pillH / 2f, PfDraw.Soft(T, v));
            TextRenderer.DrawText(g, vt, Fonts.SmallSemibold, Rectangle.Round(pill), PfDraw.Fg(T, v), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            int chev = Dpi.S(28);
            int x = Pad + pillW + Dpi.S(14);
            int w = Math.Max(0, Width - x - Pad - chev);
            int lineH = Fonts.UiSemibold.Height;
            int ty = (head - lineH - Fonts.Small.Height - Dpi.S(2)) / 2;
            Font sf = T.Num(Fonts.UiSemibold);
            int sw = Math.Min(w, Draw.Measure(Result.Serial, sf).Width + Dpi.S(4));
            Draw.Text(g, Result.Serial, sf, new Rectangle(x, ty, sw, lineH + 2), T.Text, Draw.Line);
            if (Result.Title != Result.Serial)
                Draw.Text(g, Result.Title, Fonts.Ui, new Rectangle(x + sw + Dpi.S(8), ty, Math.Max(0, w - sw - Dpi.S(8)), lineH + 2), T.Text2, Draw.Line);
            Draw.Text(g, Counts, Fonts.Small, new Rectangle(x, ty + lineH + Dpi.S(2), w, Fonts.Small.Height + 2), T.Text3, Draw.Line);

            // The chevron points down when the list is closed and up when it is open.
            float cx = Width - Pad - chev / 2f + Dpi.S(4), cy = head / 2f, s = Dpi.S(4);
            float dir = 1 - 2 * shown;
            using (Pen p = new Pen(T.Text2, Math.Max(1f, Dpi.S(1.5f))))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLines(p, new PointF[] { new PointF(cx - s, cy - s / 2 * dir), new PointF(cx, cy + s / 2 * dir), new PointF(cx + s, cy - s / 2 * dir) });
            }
            if (Focused && ShowFocusCues) Shapes.Stroke(g, new RectangleF(2.5f, 2.5f, Width - 5.5f, Math.Min(head, Height) - 5.5f), Dpi.S(PfDraw.Corner) - 2, T.Accent, 1.5f);
            if (shown <= 0.01f || Height <= head + 2) return;

            // The list. While it opens, what does not fit yet is cut off inside the card.
            g.SetClip(new Rectangle(1, head, Width - 3, Height - head - 2));
            using (Pen line = new Pen(T.Divider)) g.DrawLine(line, Pad, head, Width - Pad, head);
            int textW = Math.Max(Dpi.S(120), Width - TextX - Pad);
            int top = head + Dpi.S(8);
            if (factsH > 0) Draw.Text(g, facts, Fonts.Small, new Rectangle(Pad, top + Dpi.S(4), Width - Pad * 2, factsH + 2), T.Text3, Draw.Wrap);
            foreach (Row r in rows)
            {
                int y = top + r.Y;
                if (r.Line == null)
                {
                    Draw.Text(g, r.Group, Fonts.SmallSemibold, new Rectangle(Pad, y, Width - Pad * 2, Fonts.SmallSemibold.Height + 2), T.Text3, Draw.Line);
                    continue;
                }
                PfLevel l = r.Line.Level;
                PfDraw.Mark(g, T, l, new RectangleF(Pad, y + (Fonts.Ui.Height - MarkW) / 2f, MarkW, MarkW));
                Draw.Text(g, r.Line.Text, Fonts.Ui, new Rectangle(TextX, y, textW, r.TextH + 2), l == PfLevel.Ok ? T.Text2 : T.Text, Draw.Wrap);
                if (r.FixH > 0)
                {
                    int fy = y + r.TextH + Dpi.S(4);
                    Shapes.Fill(g, new RectangleF(TextX, fy + 1, Dpi.S(3), r.FixH - 2), Dpi.S(1.5f), PfDraw.Fg(T, l));
                    Draw.Text(g, r.Line.Fix, Fonts.Ui, new Rectangle(TextX + Dpi.S(12), fy, textW - Dpi.S(12), r.FixH + 2), T.Text2, Draw.Wrap);
                }
            }
            g.ResetClip();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = e.Y < HeadH;
            if (h != headHover) { headHover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            headHover = false;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Y >= HeadH) { Focus(); return; }
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool was = down;
            base.OnMouseUp(e);
            if (was && e.Button == MouseButtons.Left && e.Y < HeadH && ClientRectangle.Contains(e.Location)) Open = !Open;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Open = !Open; e.Handled = true; }
            else if (e.KeyCode == Keys.Right && !Open) { Open = true; e.Handled = true; }
            else if (e.KeyCode == Keys.Left && Open) { Open = false; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // Devices > Intune / cloud > Pre-flight check: asks Intune, per serial number, whether the device will deploy, and
    // shows a checklist with a fix for what will not. Read-only.
    public sealed class PreflightDialog : PageForm
    {
        IGraph graph;
        readonly bool demo;
        readonly CustomerBook book;
        // Signs in with the permissions given, and returns the sign-in, or null when it was cancelled.
        public Func<List<string>, GraphClient> SignIn;
        // The sign-in the window holds for the customer in use, asked again after the customer was switched.
        public Func<GraphClient> CurrentGraph;
        public bool RunAtStart;
        // A serial number to scroll to once the check is done, for screenshots.
        public string ShowSerial;

        readonly PreflightHeader header = new PreflightHeader();
        readonly PreflightInput input = new PreflightInput();
        readonly PreflightSummary summary = new PreflightSummary();
        readonly List<PreflightCard> cards = new List<PreflightCard>();
        readonly FooterNote note = new FooterNote();
        readonly FlatButton bCopy = new FlatButton("Copy as text", Glyphs.Copy);
        readonly FlatButton bAll = new FlatButton("Open all", null);
        List<PfResult> results = new List<PfResult>();
        ManualResetEvent cancel;
        bool relayoutQueued;

        public PreflightDialog(Theme t, IGraph g, bool isDemo) : base("Pre-flight check", t)
        {
            graph = g;
            demo = isDemo;
            book = isDemo ? CustomerBook.Demo() : CustomerUi.Book;
            Size = new Size(Dpi.S(980), Dpi.S(820));
            MinimumSize = new Size(Dpi.S(620), Dpi.S(480));
            MaximizeBox = true;

            header.Picker.Value = book.Current == null ? "" : book.Current.Name;
            header.Picker.Enabled = !demo;
            header.Picker.Click += Safe.Wrap(delegate { CustomerMenu(); });
            header.Picker.Resize += delegate { Relayout(); };

            input.Apps.On = demo || PreflightPrefs.Apps;
            input.Groups.On = demo || PreflightPrefs.Groups;
            input.Apps.Enabled = input.Groups.Enabled = !demo;
            input.Apps.Changed += delegate { if (!demo) PreflightPrefs.Apps = input.Apps.On; };
            input.Groups.Changed += delegate { if (!demo) PreflightPrefs.Groups = input.Groups.On; };
            input.Serials.TextChanged += delegate { UpdateButtons(); };
            input.Check.Click += Safe.Wrap(delegate { Check(); });
            if (demo) input.Serials.Text = DemoPreflightGraph.Serials;

            summary.Empty(demo ? "Demo data: a made-up Contoso tenant. Press Check." : "Enter serial numbers and press Check.");
            Add(header);
            Add(input);
            Add(summary);

            bClose.Primary = false;
            bClose.ApplyTheme(t);
            note.Dock = DockStyle.Fill;
            note.ApplyTheme(t);
            bCopy.Dock = DockStyle.Right;
            bCopy.Outline = true;
            bCopy.ApplyTheme(t);
            bCopy.Click += Safe.Wrap(delegate { Copy(); });
            bAll.Dock = DockStyle.Right;
            bAll.Width = Math.Max(bAll.Width, Dpi.S(96));
            bAll.ApplyTheme(t);
            bAll.Click += Safe.Wrap(delegate { ToggleAll(); });
            footer.Controls.Add(Gap());
            footer.Controls.Add(bCopy);
            footer.Controls.Add(Gap());
            footer.Controls.Add(bAll);
            footer.Controls.Add(note);
            note.BringToFront();
            // Close stays the button furthest to the right.
            bClose.SendToBack();
            UpdateButtons();
            Say(demo ? "Demo data: nothing is read from Intune." : "", false);
            Shown += Safe.Wrap(delegate
            {
                input.Serials.Focus();
                if (RunAtStart) Check();
            });
            FormClosed += delegate { if (cancel != null) cancel.Set(); };
        }

        // The demo, as --page preflight shows it. select is a serial number to scroll to, or "input" for the page before a check.
        public static PreflightDialog ForShot(Theme t, string select)
        {
            PreflightDialog d = new PreflightDialog(t, new DemoPreflightGraph(DateTime.UtcNow), true);
            if (select == "input") return d;
            d.ShowSerial = select;
            // Checked before the page shows, so the picture does not depend on how fast the check is.
            PreflightRun run = new PreflightRun(d.graph);
            run.Times = DemoPreflightGraph.Times();
            List<PfResult> done = run.CheckAll(ReadyLines.ParseSerials(DemoPreflightGraph.Serials), null);
            d.Shown += Safe.Wrap(delegate
            {
                d.ShowResults(done, "Demo data: Contoso");
                d.Say("Times come from 12 earlier deployments TrackVice has seen for this customer.", false);
            });
            return d;
        }

        static Panel Gap()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Right;
            p.Width = Dpi.S(8);
            return p;
        }

        void Say(string text, bool error)
        {
            note.Say(text, error);
        }

        bool Busy
        {
            get { return cancel != null && !cancel.WaitOne(0); }
        }

        string Customer
        {
            get { return book.Current == null ? "" : book.Current.Name; }
        }

        void UpdateButtons()
        {
            bool busy = Busy;
            input.Check.Enabled = !busy && ReadyLines.ParseSerials(input.Serials.Text).Count > 0;
            header.Picker.Enabled = !demo && !busy;
            bCopy.Enabled = !busy && results.Count > 0;
            bAll.Enabled = !busy && cards.Count > 0;
            bAll.Text = AllOpen ? "Close all" : "Open all";
            bAll.Width = Math.Max(bAll.Width, Dpi.S(96));
        }

        bool AllOpen
        {
            get
            {
                if (cards.Count == 0) return false;
                foreach (PreflightCard c in cards) if (!c.Open) return false;
                return true;
            }
        }

        void ToggleAll()
        {
            bool to = !AllOpen;
            foreach (PreflightCard c in cards) c.Open = to;
            UpdateButtons();
        }

        // ---------- the customer ----------

        void CustomerMenu()
        {
            if (demo || Busy) return;
            ContextMenuStrip m = Menus.New(theme);
            m.ShowCheckMargin = true;
            Customer cur = book.Current;
            foreach (Customer c in book.All)
            {
                Customer one = c;
                ToolStripMenuItem i = Menus.Add(m, c.Name, delegate { SwitchCustomer(one); });
                i.Checked = c == cur;
            }
            if (book.Count == 0)
            {
                ToolStripMenuItem none = Menus.Add(m, "No customers yet", delegate { });
                none.Enabled = false;
            }
            Menus.Separator(m);
            Menus.Add(m, "Manage customers…", delegate { ManageCustomers(); });
            m.Show(header.Picker, new Point(0, header.Picker.Height));
        }

        void ManageCustomers()
        {
            Customer before = book.Current == null ? null : book.Current.Copy();
            using (CustomersPage p = new CustomersPage(theme, book)) p.ShowDialog(this);
            Customer now = book.Current;
            header.Picker.Value = now == null ? "" : now.Name;
            if (now != null && (before == null || !CustomerUi.Same(before, now))) SwitchCustomer(now);
        }

        // Another customer is another tenant: what was checked goes, and so does the sign-in unless it is the same one.
        void SwitchCustomer(Customer c)
        {
            if (demo || Busy) return;
            bool keep = CustomerUi.Switch(c);
            header.Picker.Value = c.Name;
            if (!keep) graph = CurrentGraph != null ? CurrentGraph() : null;
            ShowResults(new List<PfResult>(), null);
            summary.Empty("Enter serial numbers and press Check.");
            Say("Customer " + c.Name + ". Devices are read from its tenant.", false);
        }

        // ---------- the check ----------

        // A real sign-in asks again when it lacks a permission that is needed or turned on.
        bool EnsureGraph(List<string> scopes)
        {
            if (demo) return true;
            GraphClient g = graph as GraphClient;
            bool missing = g == null || !g.SignedIn;
            if (!missing) foreach (string s in scopes) if (!NamesDialog.Has(g, s)) missing = true;
            if (!missing) return true;
            if (SignIn == null) return false;
            g = SignIn(scopes);
            if (g == null || !g.SignedIn)
            {
                Say("Not signed in, so nothing was read from Intune.", true);
                return false;
            }
            graph = g;
            return true;
        }

        void Check()
        {
            List<string> list = ReadyLines.ParseSerials(input.Serials.Text);
            if (list.Count == 0 || Busy) return;
            bool apps = input.Apps.On, groups = input.Groups.On;
            if (!EnsureGraph(PreflightRun.Scopes(apps, groups))) return;
            ManualResetEvent stop = new ManualResetEvent(false);
            cancel = stop;
            UpdateButtons();
            IGraph g = graph;
            GraphClient gc = g as GraphClient;
            string customer = Customer;
            string from = demo ? "Demo data: Contoso" : (customer.Length > 0 ? customer + ", " : "") + (gc != null && !string.IsNullOrEmpty(gc.Account) ? "signed in as " + gc.Account : "Intune");
            Say("", false);
            summary.Busy(0, list.Count, list[0]);
            QueueRelayout();
            Thread th = new Thread(delegate ()
            {
                List<PfResult> done = new List<PfResult>();
                string problem = null;
                int runs = 0;
                try
                {
                    PreflightRun run = new PreflightRun(g);
                    run.ReadAppList = apps;
                    run.ReadGroups = groups;
                    run.Times = demo ? DemoPreflightGraph.Times() : PreflightRun.TimesFrom(DeploymentHistory.Shared ?? DeploymentHistory.Load(DeploymentHistory.DefaultPath), customer);
                    if (run.Times != null) runs = run.Times.Runs;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (stop.WaitOne(0)) return;
                        int at = i;
                        Ui(delegate { if (cancel == stop) summary.Busy(at, list.Count, list[at]); });
                        done.Add(run.Check(list[i]));
                    }
                }
                catch (Exception ex) { problem = "The check stopped: " + GraphClient.Clean(ex.Message); }
                Ui(delegate
                {
                    if (cancel != stop) return;
                    stop.Set();
                    ShowResults(done, from);
                    if (problem != null) Say(problem, true);
                    else Say(runs > 0 ? "Times come from " + runs.ToString(CultureInfo.InvariantCulture) + (runs == 1 ? " earlier deployment" : " earlier deployments") + " TrackVice has seen for this customer." : "TrackVice has seen no earlier Autopilot deployments of this customer, so the time the ESP needs is not estimated.", false);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        void Ui(MethodInvoker m)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { Safe.Run(delegate { m(); }); }); }
            catch (Exception) { }
        }

        void ShowResults(List<PfResult> list, string source)
        {
            results = list;
            List<PreflightCard> old = new List<PreflightCard>(cards);
            cards.Clear();
            ClearItems();
            Add(header);
            Add(input);
            Add(summary);
            PreflightCard first = null, wanted = null;
            foreach (PfResult r in list)
            {
                PreflightCard c = new PreflightCard(r);
                c.Resized += delegate { QueueRelayout(); UpdateButtons(); };
                cards.Add(c);
                Add(c);
                if (first == null && r.Verdict != PfVerdict.Ready) first = c;
                if (ShowSerial != null && string.Equals(r.Serial, ShowSerial, StringComparison.OrdinalIgnoreCase)) wanted = c;
            }
            foreach (PreflightCard c in old) c.Dispose();
            if (source != null) summary.Set(list, source);
            Relayout();
            UpdateButtons();
            if (wanted != null) { wanted.Open = true; Relayout(); ScrollTo(wanted); }
            else if (list.Count > 0) ScrollTo(summary);
        }

        // A card that opens or closes changes its height on every step: the column is laid out once per message round.
        void QueueRelayout()
        {
            if (relayoutQueued || !IsHandleCreated) return;
            relayoutQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    relayoutQueued = false;
                    if (!IsDisposed) Safe.Run(delegate { Relayout(); });
                });
            }
            catch (Exception) { relayoutQueued = false; }
        }

        void Copy()
        {
            if (results.Count == 0) return;
            try
            {
                Clipboard.SetText(Preflight.ToText(results, demo ? "Contoso (demo)" : Customer, DateTime.UtcNow));
                Say("Copied the checklist of " + results.Count.ToString(CultureInfo.InvariantCulture) + (results.Count == 1 ? " device" : " devices") + " as text.", false);
            }
            catch (Exception) { Say("The clipboard is busy. Try again.", true); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter) || keyData == Keys.F5) { Safe.Run(Check); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.C)) { Safe.Run(Copy); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    public sealed partial class MainForm
    {
        // Devices > Intune / cloud > Pre-flight check. Uses the Intune sign-in of the customer in use, and asks for the
        // permissions the check needs when the sign-in does not have them yet.
        void OpenPreflight(bool demo)
        {
            IGraph g = demo ? (IGraph)new DemoPreflightGraph(DateTime.UtcNow) : graph;
            using (PreflightDialog d = new PreflightDialog(theme, g, demo))
            {
                d.CurrentGraph = delegate { return graph; };
                d.SignIn = delegate (List<string> scopes)
                {
                    using (RemoteDialog r = new RemoteDialog(RemoteDialog.Intune, theme, null, null, false))
                    {
                        r.Text = "Sign in for the pre-flight check";
                        r.NamesOnly = true;
                        r.MoreScopes = scopes;
                        r.ShowDialog(d);
                        if (r.Graph == null || !r.Graph.SignedIn) return null;
                        intuneCatalog = r.Catalog;
                        graph = r.Graph;
                        KeepSignIn(graph);
                        return graph;
                    }
                };
                d.ShowDialog(this);
            }
        }
    }
}
