namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // The command palette: a text box and grouped results in a card over the dimmed window. Up and Down move, Enter does
    // it, Esc closes, and the mouse hovers and clicks. A click beside the card closes it.
    public sealed class CommandPaletteView : FlatControl
    {
        sealed class Line
        {
            public bool Header;
            public string Text;
            public CommandEntry E;
            // In the card, from its top left corner.
            public Rectangle R;
        }

        // The entries for what is typed, ranked, at most as many as the number says.
        public Func<string, int, List<CommandEntry>> Source;
        public event Action<CommandEntry> Chosen;
        public event EventHandler Dismissed;
        public bool Animate = true;

        readonly TextBox box = new TextBox();
        readonly List<Line> lines = new List<Line>();
        readonly System.Windows.Forms.Timer slide = new System.Windows.Forms.Timer();
        Bitmap backdrop;
        Rectangle card;
        string query = "";
        int selected = -1, step, restTop, maxRows = CommandSearch.MaxResults;
        Point lastMouse = new Point(-1, -1);

        const int SlideSteps = 8;

        int InputH { get { return Dpi.S(56); } }
        int RowH { get { return Dpi.S(36); } }
        int HeadH { get { return Dpi.S(30); } }
        int FootH { get { return Dpi.S(34); } }
        int Pad { get { return Dpi.S(8); } }
        int BoxLeft { get { return Dpi.S(48); } }

        public CommandPaletteView()
        {
            Visible = false;
            TabStop = false;
            // Clicking a result must not take the focus from the text box, because losing it closes the palette.
            SetStyle(ControlStyles.Selectable, false);
            box.BorderStyle = BorderStyle.None;
            box.Font = new Font(Fonts.Ui.FontFamily, 12f);
            box.KeyDown += OnBoxKeyDown;
            box.TextChanged += delegate { query = box.Text; Rebuild(); };
            box.LostFocus += delegate { if (Visible) Dismiss(); };
            Controls.Add(box);
            slide.Interval = 15;
            slide.Tick += delegate
            {
                step++;
                // Ease out: fast at first, then it settles.
                double left = Math.Pow(1 - Math.Min(1.0, step / (double)SlideSteps), 3);
                PlaceCard(restTop - (int)Math.Round(Dpi.S(10) * left));
                if (step >= SlideSteps) slide.Stop();
            };
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            box.BackColor = t.Card;
            box.ForeColor = t.Text;
        }

        public bool IsOpen
        {
            get { return Visible; }
        }

        public string Query
        {
            get { return query; }
        }

        public CommandEntry Current
        {
            get { return selected >= 0 && selected < lines.Count ? lines[selected].E : null; }
        }

        // Shows the palette over the host: the window as it is now, dimmed, and the card in the upper middle.
        public void Open(Control host, string text)
        {
            Size client = host.ClientSize;
            int w = Math.Min(Dpi.S(640), Math.Max(Dpi.S(280), client.Width - Dpi.S(32)));
            int top = Math.Max(Dpi.S(48), Math.Min(Dpi.S(120), client.Height / 8));
            // As many lines as the window has room for: three headings at the most, and some air below.
            int room = client.Height - top - Dpi.S(16) - InputH - FootH - HeadH * 3 - Dpi.S(10);
            maxRows = Math.Max(3, Math.Min(CommandSearch.MaxResults, room / RowH));
            Snapshot(host);
            if (backdrop != null) Bounds = new Rectangle(Point.Empty, client);
            else Bounds = new Rectangle((client.Width - w) / 2, top, w, Dpi.S(100));
            card = new Rectangle(backdrop != null ? (client.Width - w) / 2 : 0, backdrop != null ? top : 0, w, Dpi.S(100));
            restTop = card.Y;
            lastMouse = new Point(-1, -1);
            box.Text = text ?? "";
            query = box.Text;
            Rebuild();
            Visible = true;
            BringToFront();
            box.Focus();
            box.SelectionStart = box.Text.Length;
            bool motion = Animate && backdrop != null;
            try { motion = motion && SystemInformation.UIEffectsEnabled; } catch (Exception) { }
            if (motion) { step = 0; PlaceCard(restTop - Dpi.S(10)); slide.Start(); }
        }

        // The window as it looks now, dimmed. Without it (no screen to copy from) the card shows alone.
        void Snapshot(Control host)
        {
            DropBackdrop();
            try
            {
                Size s = host.ClientSize;
                if (s.Width <= 0 || s.Height <= 0) return;
                host.Update();
                Bitmap b = new Bitmap(s.Width, s.Height);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.CopyFromScreen(host.PointToScreen(Point.Empty), Point.Empty, s);
                    bool dark = T.Bg.GetBrightness() < 0.5f;
                    using (SolidBrush dim = new SolidBrush(dark ? Color.FromArgb(130, 0, 0, 0) : Color.FromArgb(72, 16, 18, 28))) g.FillRectangle(dim, 0, 0, s.Width, s.Height);
                }
                backdrop = b;
            }
            catch (Exception) { DropBackdrop(); }
        }

        void DropBackdrop()
        {
            if (backdrop != null) backdrop.Dispose();
            backdrop = null;
        }

        public void Dismiss()
        {
            if (!Visible) return;
            slide.Stop();
            Visible = false;
            DropBackdrop();
            if (Dismissed != null) Dismissed(this, EventArgs.Empty);
        }

        void PlaceCard(int top)
        {
            card.Y = top;
            PlaceBox();
            Invalidate();
        }

        // The text box is only as wide as the caret while it is empty, so the hint can be drawn beside it.
        void PlaceBox()
        {
            int full = card.Width - BoxLeft - Dpi.S(18);
            box.SetBounds(card.X + BoxLeft, card.Y + (InputH - box.Height) / 2 + Dpi.S(1), box.Text.Length == 0 ? Dpi.S(4) : full, box.Height);
        }

        void Rebuild()
        {
            List<CommandEntry> rows = Source != null ? Source(query, maxRows) : new List<CommandEntry>();
            CommandEntry keep = Current;
            lines.Clear();
            string group = null;
            int y = InputH + Dpi.S(2);
            foreach (CommandEntry e in rows)
            {
                if (e.Group != group)
                {
                    group = e.Group;
                    Line h = new Line();
                    h.Header = true;
                    h.Text = (e.GroupLabel ?? group).ToUpperInvariant();
                    h.R = new Rectangle(0, y, card.Width, HeadH);
                    lines.Add(h);
                    y += HeadH;
                }
                Line l = new Line();
                l.E = e;
                l.R = new Rectangle(Pad, y, card.Width - Pad * 2, RowH);
                lines.Add(l);
                y += RowH;
            }
            if (rows.Count == 0) y += Dpi.S(72);
            selected = -1;
            // What is typed changed: the best match is the one to do, so the first line is taken again.
            if (query.Trim().Length == 0)
                for (int i = 0; i < lines.Count; i++) if (lines[i].E != null && lines[i].E == keep) selected = i;
            if (selected < 0) Step(1, -1);
            card.Height = y + Dpi.S(8) + FootH;
            if (backdrop == null) Height = card.Height;
            PlaceBox();
            Invalidate();
        }

        void Step(int dir, int from)
        {
            int n = lines.Count;
            if (n == 0) { selected = -1; return; }
            int i = from;
            for (int k = 0; k < n; k++)
            {
                i = ((i + dir) % n + n) % n;
                if (lines[i].E != null) { selected = i; Invalidate(); return; }
            }
            selected = -1;
        }

        public void MoveBy(int dir)
        {
            Step(dir, selected < 0 && dir < 0 ? 0 : selected);
        }

        void Edge(bool last)
        {
            selected = -1;
            if (last) Step(-1, 0);
            else Step(1, -1);
            Invalidate();
        }

        void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Down: Step(1, selected); break;
                case Keys.Up: MoveBy(-1); break;
                case Keys.PageDown: Edge(true); break;
                case Keys.PageUp: Edge(false); break;
                case Keys.Enter: Choose(Current); break;
                case Keys.Escape: Dismiss(); break;
                default: return;
            }
            e.Handled = e.SuppressKeyPress = true;
        }

        void Choose(CommandEntry e)
        {
            if (e == null) return;
            Dismiss();
            if (Chosen != null) Chosen(e);
        }

        Rectangle InCard(Rectangle r)
        {
            return new Rectangle(card.X + r.X, card.Y + r.Y, r.Width, r.Height);
        }

        int LineAt(Point p)
        {
            for (int i = 0; i < lines.Count; i++) if (lines[i].E != null && InCard(lines[i].R).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // Only a pointer that moves chooses: one that just happens to be over the card when it opens does not.
            bool first = lastMouse.X < 0;
            bool moved = !first && e.Location != lastMouse;
            lastMouse = e.Location;
            int i = LineAt(e.Location);
            if (moved && i >= 0 && i != selected) { selected = i; Invalidate(); }
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!card.Contains(e.Location)) { Dismiss(); return; }
            if (e.Button != MouseButtons.Left) return;
            int i = LineAt(e.Location);
            if (i >= 0 && i != selected) { selected = i; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !Visible) return;
            int i = LineAt(e.Location);
            if (i >= 0 && i == selected) Choose(lines[i].E);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (backdrop != null) g.DrawImageUnscaled(backdrop, 0, 0);
            else g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF c = new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1f, card.Height - 1f);
            if (backdrop != null)
            {
                // A soft edge of shade around the card, so it lifts from the window without a heavy shadow.
                for (int i = 4; i >= 1; i--)
                    Shapes.Stroke(g, new RectangleF(c.X - i, c.Y - i + Dpi.S(2), c.Width + i * 2, c.Height + i * 2), Dpi.S(12) + i, Color.FromArgb(10 + (4 - i) * 6, 0, 0, 0), 1.5f);
            }
            Shapes.Fill(g, c, Dpi.S(12), T.Card);
            Shapes.Stroke(g, c, Dpi.S(12), T.Stroke, 1f);
            SearchField.Magnifier(g, new PointF(card.X + Dpi.S(27), card.Y + InputH / 2f - Dpi.S(1)), Dpi.S(6.5f), box.Text.Length > 0 ? T.Accent : T.Text3);
            using (Pen p = new Pen(T.Divider, 1f)) g.DrawLine(p, card.X + 1, card.Y + InputH, card.Right - 2, card.Y + InputH);
            TextFormatFlags one = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            if (box.Text.Length == 0)
                TextRenderer.DrawText(g, SearchField.Label, box.Font, new Rectangle(box.Right + Dpi.S(1), box.Top, card.Right - box.Right - Dpi.S(18), box.Height), T.Text3, TextFormatFlags.Left | one);
            for (int i = 0; i < lines.Count; i++)
            {
                Line l = lines[i];
                Rectangle r = InCard(l.R);
                if (l.Header)
                {
                    TextRenderer.DrawText(g, l.Text, Fonts.SmallSemibold, new Rectangle(r.X + Pad + Dpi.S(14), r.Y + Dpi.S(8), r.Width - Pad * 2 - Dpi.S(28), r.Height - Dpi.S(8)), T.Text3, TextFormatFlags.Left | one);
                    continue;
                }
                PaintEntry(g, l.E, r, i == selected, one);
            }
            if (lines.Count == 0)
            {
                Rectangle none = new Rectangle(card.X + Dpi.S(24), card.Y + InputH + Dpi.S(14), card.Width - Dpi.S(48), Dpi.S(22));
                TextRenderer.DrawText(g, "Nothing matches “" + query.Trim() + "”", Fonts.UiSemibold, none, T.Text2, TextFormatFlags.HorizontalCenter | one);
                none.Offset(0, Dpi.S(22));
                TextRenderer.DrawText(g, "Try a device name, a user, an action or the name of a setting.", Fonts.Small, none, T.Text3, TextFormatFlags.HorizontalCenter | one);
            }
            int fy = card.Bottom - FootH;
            using (Pen p = new Pen(T.Divider, 1f)) g.DrawLine(p, card.X + 1, fy, card.Right - 2, fy);
            Rectangle foot = new Rectangle(card.X + Dpi.S(16), fy, card.Width - Dpi.S(32), FootH);
            int fx = foot.X;
            fx = FootKey(g, "↑↓", "move", fx, foot, one);
            fx = FootKey(g, "Enter", Current != null ? Verb(Current) : "open", fx, foot, one);
            if (card.Width >= Dpi.S(420)) FootKey(g, "Esc", "close", fx, foot, one);
            if (card.Width >= Dpi.S(520))
            {
                int n = 0;
                foreach (Line l in lines) if (l.E != null) n++;
                string count = n == 0 ? "" : query.Trim().Length == 0 ? "Type to search everything" : n == 1 ? "1 result" : n + " results";
                TextRenderer.DrawText(g, count, Fonts.Small, foot, T.Text3, TextFormatFlags.Right | one);
            }
        }

        static string Verb(CommandEntry e)
        {
            return e.Group == CommandGroups.Actions ? "run" : "open";
        }

        int FootKey(Graphics g, string key, string what, int x, Rectangle foot, TextFormatFlags one)
        {
            x += KeyCap(g, key, x, foot.Y + foot.Height / 2, false) + Dpi.S(6);
            int w = TextRenderer.MeasureText(what, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, what, Fonts.Small, new Rectangle(x, foot.Y, w + Dpi.S(2), foot.Height), T.Text3, TextFormatFlags.Left | one);
            return x + w + Dpi.S(16);
        }

        // A key in a small outlined box, from x to the right, or ending at x. Gives its width.
        int KeyCap(Graphics g, string key, int x, int cy, bool rightAligned)
        {
            int w = TextRenderer.MeasureText(key, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Dpi.S(12);
            int h = Dpi.S(18);
            if (rightAligned) x -= w;
            RectangleF k = new RectangleF(x + 0.5f, cy - h / 2 + 0.5f, w - 1f, h - 1f);
            Shapes.Fill(g, k, Dpi.S(5), T.Bg);
            Shapes.Stroke(g, k, Dpi.S(5), T.Divider, 1f);
            TextRenderer.DrawText(g, key, Fonts.Small, new Rectangle(x, cy - h / 2, w, h), T.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return w;
        }

        void PaintEntry(Graphics g, CommandEntry e, Rectangle r, bool on, TextFormatFlags one)
        {
            if (on)
            {
                Shapes.Fill(g, r, Dpi.S(8), T.AccentSoft);
                Shapes.Fill(g, new RectangleF(r.X, r.Y + Dpi.S(9), Dpi.S(3), r.Height - Dpi.S(18)), Dpi.S(1.5f), T.Accent);
            }
            int cy = r.Y + r.Height / 2;
            // The mark of the line, centred in a slot: a ring with how far a device is, its state, an icon, or a dot.
            int slot = Dpi.S(18), sx = r.X + Dpi.S(14);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.HasState && e.Percent >= 0)
            {
                Color sc = StateLook.Color(e.State, T);
                float d = Dpi.S(14), pw = Dpi.S(2.25f);
                RectangleF ring = new RectangleF(sx + (slot - d) / 2f, cy - d / 2f, d, d);
                using (Pen track = new Pen(on ? Color.FromArgb(70, T.Text3) : T.Divider, pw)) g.DrawEllipse(track, ring);
                using (Pen arc = new Pen(sc, pw))
                {
                    arc.StartCap = arc.EndCap = LineCap.Round;
                    if (e.Percent >= 100) g.DrawEllipse(arc, ring);
                    else if (e.Percent > 0) g.DrawArc(arc, ring, -90, Math.Max(8f, 360f * e.Percent / 100f));
                }
            }
            else if (e.HasState)
            {
                int d = Dpi.S(8);
                using (SolidBrush b = new SolidBrush(StateLook.Color(e.State, T))) g.FillEllipse(b, sx + (slot - d) / 2, cy - d / 2, d, d);
            }
            else if (Fonts.Icons != null && !string.IsNullOrEmpty(e.Glyph))
                TextRenderer.DrawText(g, e.Glyph, Fonts.Icons, new Rectangle(sx - Dpi.S(2), r.Y, slot + Dpi.S(4), r.Height), on ? T.Accent : T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            else
            {
                int d = Dpi.S(4);
                using (SolidBrush b = new SolidBrush(on ? T.Accent : T.Text3)) g.FillEllipse(b, sx + (slot - d) / 2, cy - d / 2, d, d);
            }
            int x = sx + slot + Dpi.S(10);
            int right = r.Right - Dpi.S(12);
            if (!string.IsNullOrEmpty(e.Hint))
            {
                if (e.HasState)
                {
                    int hw = TextRenderer.MeasureText(e.Hint, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                    // Red is for failures only, the accent for what runs now; the rest stays quiet.
                    Color hc = e.State == State.Failed || e.State == State.Installing || e.State == State.Downloading || e.State == State.Detecting || e.State == State.WaitingReboot ? StateLook.Color(e.State, T) : T.Text2;
                    TextRenderer.DrawText(g, e.Hint, Fonts.Small, new Rectangle(right - hw, r.Y, hw, r.Height), hc, TextFormatFlags.Right | one);
                    right -= hw + Dpi.S(14);
                }
                else right -= KeyCap(g, e.Hint, right, cy, true) + Dpi.S(14);
            }
            Font tf = e.HasState ? Fonts.UiSemibold : Fonts.Ui;
            int room = Math.Max(0, right - x);
            int tw = Math.Min(TextRenderer.MeasureText(e.Title, tf, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width, room);
            TextRenderer.DrawText(g, e.Title, tf, new Rectangle(x, r.Y, room, r.Height), T.Text, TextFormatFlags.Left | one);
            if (!string.IsNullOrEmpty(e.Detail) && room - tw > Dpi.S(70))
                TextRenderer.DrawText(g, e.Detail, Fonts.Small, new Rectangle(x + tw + Dpi.S(10), r.Y + Dpi.S(1), room - tw - Dpi.S(10), r.Height), on ? T.Text2 : T.Text3, TextFormatFlags.Left | one);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { slide.Dispose(); DropBackdrop(); }
            base.Dispose(disposing);
        }
    }
}

namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Forms;

    // The command palette of the main window: Ctrl+K, Ctrl+P or the search field in the header. It only starts what
    // the window already does: it opens a device, a page or a setting, or runs an action of the device that is open.
    public sealed partial class MainForm
    {
        CommandPaletteView palette;
        List<CommandEntry> paletteEntries = new List<CommandEntry>();
        Control paletteFocus;

        void BuildPalette()
        {
            palette = new CommandPaletteView();
            palette.Source = PaletteFor;
            palette.Chosen += delegate (CommandEntry e)
            {
                Action run = e.Tag as Action;
                if (run == null) return;
                // After the palette is gone, so a window it opens does not open over it.
                BeginInvoke((MethodInvoker)delegate { Safe.Run(run); });
            };
            palette.Dismissed += delegate
            {
                try { if (paletteFocus != null && !paletteFocus.IsDisposed && paletteFocus.CanFocus) paletteFocus.Focus(); } catch (Exception) { }
                paletteFocus = null;
            };
            themed.Add(palette);
            Controls.Add(palette);
            // The picture of the window behind it no longer fits when the window changes.
            Resize += delegate { if (palette.IsOpen) palette.Dismiss(); };
        }

        void TogglePalette()
        {
            if (palette == null) return;
            if (palette.IsOpen) palette.Dismiss();
            else OpenPalette("");
        }

        // The keys of the open palette that never reach its text box. Tab moves like the arrows, and does not leave it.
        bool PaletteKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.K: case Keys.Control | Keys.P: case Keys.Escape: palette.Dismiss(); return true;
                case Keys.Tab: palette.MoveBy(1); return true;
                case Keys.Shift | Keys.Tab: palette.MoveBy(-1); return true;
                case Keys.F1: case Keys.F5: return true;
            }
            return false;
        }

        void OpenPalette(string text)
        {
            if (palette == null) return;
            paletteEntries = PaletteEntries();
            paletteFocus = ActiveControl;
            palette.Animate = start.Shot == null;
            try { tips.Hide(searchField); } catch (Exception) { }
            palette.Open(this, text);
        }

        List<CommandEntry> PaletteFor(string query, int max)
        {
            List<CommandEntry> all = new List<CommandEntry>(paletteEntries);
            if (CommandSearch.Words(query).Length > 0)
            {
                int n = 0;
                foreach (SettingDef d in SettingsCatalog.Search(query))
                {
                    if (++n > 8) break;
                    SettingDef def = d;
                    SettingPage sp = SettingsCatalog.Page(d.Page);
                    CommandEntry e = Entry(CommandGroups.Settings, d.Title, sp != null ? sp.Title : "", "", delegate { OpenSettings(def.Page, def.Id); });
                    e.Found = n;
                    e.Glyph = Glyphs2.Settings;
                    all.Add(e);
                }
            }
            return CommandSearch.Search(query, all, Math.Min(max, CommandSearch.MaxResults));
        }

        static CommandEntry Entry(string group, string title, string detail, string keywords, Action run)
        {
            CommandEntry e = new CommandEntry();
            e.Group = group;
            e.Title = title;
            e.Detail = detail;
            e.Keywords = keywords;
            e.Tag = run;
            return e;
        }

        List<CommandEntry> PaletteEntries()
        {
            List<CommandEntry> l = new List<CommandEntry>();
            string current = engine != null ? engine.S.Computer : null;
            int shown = 0;
            foreach (FoundDevice f in DeviceSearch.Merge(Recent.All, LoadedDevices))
            {
                if (string.IsNullOrEmpty(f.Device) || (current != null && string.Equals(f.Device, current, StringComparison.OrdinalIgnoreCase) && !PageVisible)) continue;
                if (++shown > 80) break;
                FoundDevice found = f;
                List<string> det = new List<string>();
                if (f.Customer.Length > 0) det.Add(f.Customer);
                if (f.Profile.Length > 0) det.Add(f.Profile);
                else if (f.User.Length > 0) det.Add(f.User);
                CommandEntry e = Entry(CommandGroups.Devices, f.Device, string.Join(" · ", det.ToArray()),
                    f.Serial + " " + f.User + " " + f.Profile + " " + f.Customer + " " + f.Note + " " + RecentSources.Name(f.Source), delegate { OpenFound(found); });
                e.Percent = CommandSearch.PercentOf(f.StateText);
                if (e.Percent < 0 && f.State == State.Succeeded) e.Percent = 100;
                e.HasState = f.State != State.Unknown || !string.IsNullOrEmpty(f.StateText);
                e.State = f.State;
                string label = f.State == State.Unknown ? f.StateText : States.Label(f.State);
                e.Hint = e.Percent >= 0 ? (label.Length > 0 ? label + " · " : "") + e.Percent + " %" : label;
                l.Add(e);
            }
            if (engine != null && !PageVisible)
            {
                string who = string.IsNullOrEmpty(current) ? "this device" : current;
                Item sel = list.SelectedItem;
                CommandEntry a;
                a = Entry(CommandGroups.Actions, "Copy for ticket", "A short summary for a support ticket", "copy clipboard summary support", delegate { CopyTicket(); });
                a.Glyph = Glyphs.Copy;
                l.Add(a);
                a = Entry(CommandGroups.Actions, "Open in LogVice", "The logs of this deployment", "log logs open", delegate
                {
                    Item it = list.SelectedItem;
                    if (it != null && it.LogFile != null) OpenItemLog(it);
                    else LogViceMenu().Show(bLogVice, new System.Drawing.Point(0, bLogVice.Height));
                });
                a.Glyph = Glyphs.Log;
                if (sel != null && sel.LogFile != null) a.Hint = "Ctrl+L";
                l.Add(a);
                a = Entry(CommandGroups.Actions, "Open in Intune", "The device in the Intune admin center", "portal admin center browser", delegate { OpenInIntune(); });
                a.Glyph = Glyphs.Export;
                a.Enabled = PortalLinks.Intune(engine.S.GetInfo(DeviceFacts.IntuneId)) != null;
                l.Add(a);
                a = Entry(CommandGroups.Actions, "Save", "The session to a file", "session file", delegate { SaveSession(); });
                a.Glyph = Glyphs.Save;
                a.Hint = "Ctrl+S";
                l.Add(a);
                a = Entry(CommandGroups.Actions, "Export", "An HTML report", "html report csv ship", delegate { ExportHtml(); });
                a.Glyph = Glyphs.Export;
                a.Hint = "Ctrl+E";
                l.Add(a);
                a = Entry(CommandGroups.Actions, "Compact", "A small window that stays on top", "small window overlay oobe", delegate { ToggleCompact(); });
                a.Glyph = Glyphs2.Device;
                a.Hint = "Ctrl+M";
                l.Add(a);
                foreach (CommandEntry x in l) if (x.Group == CommandGroups.Actions) x.GroupLabel = "Actions on " + who;
            }
            CommandEntry p;
            p = Entry(CommandGroups.Pages, "Overview", "Devices deploying now and what needs attention", "home dashboard", delegate { GoHome(true); });
            p.Hint = "Alt+Home";
            p.Glyph = bHome.Glyph;
            l.Add(p);
            p = Entry(CommandGroups.Pages, "Devices", "Recent devices, and a search over them", "recent find search", delegate { OpenRecentList(""); });
            p.Glyph = Glyphs2.Apps;
            l.Add(p);
            foreach (SettingPage sp in SettingsCatalog.Pages)
            {
                string id = sp.Id;
                p = Entry(CommandGroups.Pages, sp.Title, "Settings", "settings " + sp.Summary + (id == SettingsCatalog.Storage ? " your storage" : ""), delegate { OpenSettings(id, null); });
                p.Glyph = Glyphs2.Settings;
                if (id == SettingsCatalog.General) p.Hint = "Ctrl+,";
                l.Add(p);
            }
            return l;
        }

        // --page palette: the palette open over the demo, with made-up recent devices. --select is what is typed.
        void ShotPalette()
        {
            DateTime now = DateTime.UtcNow;
            overviewDemo = true;
            recentStore = OverviewDemo.Recent(now);
            overviewSnap = new OverviewLoader(OverviewDemo.Sources(now)).Read(now, true);
            // Once the window is drawn, because the palette lies over a picture of it.
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 500;
            t.Tick += delegate { t.Stop(); t.Dispose(); DrawNow(this); OpenPalette(start.Select ?? ""); DrawNow(palette); };
            t.Start();
        }

        static void DrawNow(Control c)
        {
            if (!c.Visible) return;
            c.Refresh();
            foreach (Control k in c.Controls) DrawNow(k);
        }
    }
}
