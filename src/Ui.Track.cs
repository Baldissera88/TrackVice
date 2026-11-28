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
            string s = AppGroups.StateLabel(i);
            if (States.IsActive(i.State) && i.Percent.HasValue && i.Percent.Value > 0 && i.Percent.Value < 100) s += "  " + i.Percent.Value.ToString(CultureInfo.InvariantCulture) + " %";
            else if (i.State == State.Failed && i.ExitCode.HasValue) s += " · " + Errors.Short(i.ExitCode);
            return s;
        }
    }

    // The progress list: phases as group rows, items under them with their state, progress, time and last event.
    public sealed class ItemList : FlatControl
    {
        sealed class Row
        {
            public Phase Phase;
            public Item Item;
            // A sub-header inside a phase, such as the groups of the Apps phase. Item is null.
            public string Group;
            public int GroupCount;
            // After the count of a group: where an expected app comes from.
            public string GroupNote;
        }

        // A pill that changes state fades from the old colour to the new one.
        sealed class Fade
        {
            public Color From, To;
            public int Start;
        }

        const int FadeMs = 150;
        const int AppearMs = 180;

        readonly List<Row> rows = new List<Row>();
        readonly HashSet<string> collapsed = new HashSet<string>();
        readonly Timer anim = new Timer();
        readonly Timer fadeTimer = new Timer();
        readonly Dictionary<string, State> lastState = new Dictionary<string, State>();
        readonly Dictionary<string, Fade> fades = new Dictionary<string, Fade>();
        // Items seen for the first time after the list had items: they fade in.
        readonly Dictionary<string, int> born = new Dictionary<string, int>();
        Func<DateTime> now = delegate { return DateTime.UtcNow; };
        IEnumerable<Phase> source = new Phase[0];
        int top, hot = -1, selected = -1;
        float spin;
        bool dragging;
        int dragOffset;
        Font nameFont, nameBold;
        string nameFamily;
        public Func<Item, bool> Filter;
        public string EmptyText = "Nothing to show yet.";
        public event EventHandler SelectionChanged;
        public event Action<Phase> SpinTick;
        public Action<ContextMenuStrip, Item> MenuExtras;

        int ItemH { get { return Dpi.S(56); } }
        int PhaseH { get { return Dpi.S(48); } }
        int GroupH { get { return Dpi.S(36); } }
        int BarW { get { return Dpi.S(6); } }
        // Names and sub-headers start here, in line with the item names; the chevron sits over the tiles.
        int TextX { get { return Dpi.S(64); } }
        // From this width the state, tries and time sit in columns on one line; below it they stack at the right.
        int WideAt { get { return Dpi.S(600); } }

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
            fadeTimer.Interval = 15;
            fadeTimer.Tick += delegate
            {
                if (!PruneFades()) fadeTimer.Stop();
                Invalidate();
            };
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
            if (disposing)
            {
                anim.Dispose();
                fadeTimer.Dispose();
                if (nameFont != null) nameFont.Dispose();
                if (nameBold != null) nameBold.Dispose();
            }
            base.Dispose(disposing);
        }

        // 15 px names, made again only when the UI font changes.
        void EnsureFonts()
        {
            if (nameFont != null && nameFamily == Fonts.Ui.Name) return;
            if (nameFont != null) nameFont.Dispose();
            if (nameBold != null) nameBold.Dispose();
            nameFamily = Fonts.Ui.Name;
            nameFont = new Font(Fonts.Ui.FontFamily, 11.25f, Fonts.Ui.Style);
            nameBold = new Font(Fonts.UiSemibold.FontFamily, 11.25f, Fonts.UiSemibold.Style);
        }

        public void SetData(IEnumerable<Phase> phases, Func<DateTime> clock)
        {
            source = phases;
            if (clock != null) now = clock;
            Rebuild();
        }

        public void Rebuild()
        {
            NoteStates();
            // The first row in view stays where it is, whatever comes or goes above it.
            string anchor = null;
            int anchorOff = 0;
            if (top > 0)
            {
                int y = -top;
                for (int k = 0; k < rows.Count; k++)
                {
                    int h = H(k);
                    if (y + h > 0) { anchor = RowId(rows[k]); anchorOff = y; break; }
                    y += h;
                }
            }
            string keepItem = SelectedItem != null ? SelectedItem.Key : null;
            string keepPhase = SelectedItem == null && SelectedPhase != null ? SelectedPhase.Key : null;
            rows.Clear();
            // Every app in one Apps section: the apps the ESP waits for are listed there, not under their ESP phase.
            List<Item> espApps = new List<Item>();
            Phase appsPhase = null;
            int lastEsp = -1, at = 0;
            foreach (Phase p in source)
            {
                if (p.Key == Phases.IntuneApps) appsPhase = p;
                if (p.Key == Phases.DeviceSetup || p.Key == Phases.AccountSetup)
                {
                    lastEsp = at;
                    foreach (Item i in p.Items) if (AppGroups.IsApp(i)) espApps.Add(i);
                }
                at++;
            }
            List<Phase> shown = new List<Phase>();
            Phase apps = null;
            if (espApps.Count > 0)
            {
                apps = new Phase();
                apps.Key = Phases.IntuneApps;
                apps.Name = appsPhase != null ? appsPhase.Name : Phases.Name(Phases.IntuneApps);
                apps.Order = appsPhase != null ? appsPhase.Order : 0;
                apps.Items.AddRange(espApps);
                if (appsPhase != null) apps.Items.AddRange(appsPhase.Items);
            }
            at = 0;
            foreach (Phase p in source)
            {
                if (apps != null && p == appsPhase) shown.Add(apps);
                else shown.Add(p);
                if (apps != null && appsPhase == null && at == lastEsp) shown.Add(apps);
                at++;
            }
            foreach (Phase p in shown)
            {
                bool esp = apps != null && (p.Key == Phases.DeviceSetup || p.Key == Phases.AccountSetup);
                List<Item> items = new List<Item>();
                foreach (Item i in p.Items) if ((Filter == null || Filter(i)) && !(esp && AppGroups.IsApp(i))) items.Add(i);
                if (items.Count == 0 && Filter != null) continue;
                Row h = new Row();
                h.Phase = p;
                rows.Add(h);
                if (collapsed.Contains(p.Key)) continue;
                if (p.Key == Phases.IntuneApps) items = Grouped(p, items);
                foreach (Item i in items)
                {
                    if (i == null) continue;
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
                if (keepPhase != null && IsPhaseRow(k) && rows[k].Phase.Key == keepPhase) selected = k;
            }
            if (anchor != null)
            {
                for (int k = 0; k < rows.Count; k++)
                    if (RowId(rows[k]) == anchor) { top = Y(k) - anchorOff; break; }
            }
            ClampTop();
            Invalidate();
        }

        static string RowId(Row r)
        {
            if (r.Item != null) return "i:" + r.Item.Key;
            if (r.Group != null) return "g:" + r.Phase.Key + ":" + r.Group;
            return "p:" + r.Phase.Key;
        }

        // Starts the fades: a pill whose state changed, an item that was not there before.
        void NoteStates()
        {
            bool motion = ListLook.Animations;
            // Keys of deployments looked at long ago are let go.
            if (lastState.Count > 4000) lastState.Clear();
            bool known = lastState.Count > 0;
            int tick = Environment.TickCount;
            foreach (Phase p in source)
            {
                foreach (Item i in p.Items)
                {
                    if (i.Key == null) continue;
                    State old;
                    if (!lastState.TryGetValue(i.Key, out old))
                    {
                        if (known && motion) born[i.Key] = tick;
                    }
                    else if (old != i.State && motion)
                    {
                        Fade f = new Fade();
                        Fade cur;
                        f.From = fades.TryGetValue(i.Key, out cur) ? FadeColor(cur) : StateLook.Color(old, T);
                        f.To = StateLook.Color(i.State, T);
                        f.Start = tick;
                        fades[i.Key] = f;
                    }
                    lastState[i.Key] = i.State;
                }
            }
            if ((fades.Count > 0 || born.Count > 0) && !fadeTimer.Enabled) fadeTimer.Start();
        }

        static Color FadeColor(Fade f)
        {
            return Theme.Mix(f.From, f.To, ListLook.EaseOut(f.Start, FadeMs));
        }

        // Drops the fades that are over. True while some still run.
        bool PruneFades()
        {
            int tick = Environment.TickCount;
            List<string> over = new List<string>();
            foreach (KeyValuePair<string, Fade> kv in fades) if (unchecked(tick - kv.Value.Start) >= FadeMs) over.Add(kv.Key);
            foreach (string k in over) fades.Remove(k);
            over.Clear();
            foreach (KeyValuePair<string, int> kv in born) if (unchecked(tick - kv.Value) >= AppearMs) over.Add(kv.Key);
            foreach (string k in over) born.Remove(k);
            return fades.Count > 0 || born.Count > 0;
        }

        Color PillColor(Item i)
        {
            Fade f;
            if (i.Key != null && fades.TryGetValue(i.Key, out f)) return FadeColor(f);
            return StateLook.Color(i.State, T);
        }

        // The apps of the Apps phase in their groups, each after a sub-header row. Null entries in the list stand for the headers.
        List<Item> Grouped(Phase p, List<Item> items)
        {
            Dictionary<string, List<Item>> by = new Dictionary<string, List<Item>>();
            List<Item> rest = new List<Item>();
            foreach (Item i in items)
            {
                string g = AppGroups.Group(i);
                if (g == null) { rest.Add(i); continue; }
                List<Item> l;
                if (!by.TryGetValue(g, out l)) { l = new List<Item>(); by[g] = l; }
                l.Add(i);
            }
            if (by.Count == 0) return items;
            foreach (Item i in rest)
            {
                Row r = new Row();
                r.Phase = p;
                r.Item = i;
                rows.Add(r);
            }
            foreach (string g in AppGroups.Order)
            {
                List<Item> l;
                if (!by.TryGetValue(g, out l)) continue;
                Row h = new Row();
                h.Phase = p;
                h.Group = g;
                h.GroupCount = l.Count;
                if (g == AppGroups.ExpectedApps) h.GroupNote = l[0].ExpectedSource == ExpectedSources.Intune ? "assigned in Intune" : "usual in earlier deployments";
                rows.Add(h);
                foreach (Item i in l)
                {
                    Row r = new Row();
                    r.Phase = p;
                    r.Item = i;
                    rows.Add(r);
                }
            }
            return new List<Item>();
        }

        bool IsPhaseRow(int k)
        {
            return k >= 0 && k < rows.Count && rows[k].Item == null && rows[k].Group == null;
        }

        bool IsGroupRow(int k)
        {
            return k >= 0 && k < rows.Count && rows[k].Group != null;
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
                if (IsPhaseRow(k) && rows[k].Phase.Key == key)
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
            return rows[k].Group != null ? GroupH : rows[k].Item == null ? PhaseH : ItemH;
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

        // Rows under a phase are scrolled clear of the phase header that stays pinned at the top.
        void EnsureVisible(int k)
        {
            int y = Y(k), h = H(k);
            bool under = !IsPhaseRow(k);
            if (y < top + (under ? PhaseH : 0)) top = Math.Max(0, under ? y - PhaseH : y);
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

        // The phase header that stays at the top while its rows scroll under it, and where it sits: the next header pushes it up.
        int Pinned(out int pinY)
        {
            pinY = 0;
            if (top <= 0 || rows.Count == 0) return -1;
            int y = -top, k0 = -1;
            for (int k = 0; k < rows.Count; k++)
            {
                int h = H(k);
                if (y + h > 0) { k0 = k; break; }
                y += h;
            }
            if (k0 < 0) return -1;
            if (IsPhaseRow(k0) && y >= 0) return -1;
            int ph = -1;
            for (int k = k0; k >= 0; k--) if (IsPhaseRow(k)) { ph = k; break; }
            if (ph < 0) return -1;
            int ny = y;
            for (int k = k0; k < rows.Count; k++)
            {
                if (k > k0 && IsPhaseRow(k)) { pinY = Math.Min(0, ny - PhaseH); break; }
                ny += H(k);
                if (ny > PhaseH * 2) break;
            }
            return ph;
        }

        int RowAt(int py)
        {
            int pinY;
            int pin = Pinned(out pinY);
            if (pin >= 0 && py >= pinY && py < pinY + PhaseH) return pin;
            int y = -top;
            for (int k = 0; k < rows.Count; k++)
            {
                int h = H(k);
                if (py >= y && py < y + h) return k;
                y += h;
            }
            return -1;
        }

        // ---------- painting ----------

        // The surface of the selected row: one step up from the card, with a trace of the accent.
        Color Raised
        {
            get { return T.IsDark ? Theme.Mix(Theme.Mix(T.Card, T.Text, 0.05f), T.Accent, 0.05f) : Theme.Mix(T.Card, T.Accent, 0.05f); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Card);
            EnsureFonts();
            if (rows.Count == 0)
            {
                PaintEmpty(g);
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
                    if (rows[k].Group != null) PaintGroup(g, rows[k], r);
                    else if (rows[k].Item == null) PaintPhase(g, k, r, nowUtc);
                    else PaintItem(g, k, r, nowUtc);
                }
                y += h;
                if (y > Height) break;
            }
            int pinY;
            int pin = Pinned(out pinY);
            if (pin >= 0)
            {
                PaintPhase(g, pin, new Rectangle(0, pinY, w, PhaseH), nowUtc);
                // A soft edge under the pinned header, so the rows read as passing beneath it.
                Rectangle sh = new Rectangle(0, pinY + PhaseH, Width, Dpi.S(6));
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(sh.X, sh.Y - 1, sh.Width, sh.Height + 2), Color.FromArgb(T.IsDark ? 70 : 22, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(b, sh);
            }
            PaintScroll(g);
        }

        void PaintEmpty(Graphics g)
        {
            if (Filter == null)
            {
                Rectangle er = new Rectangle(Dpi.S(24), Dpi.S(40), Math.Max(0, Width - Dpi.S(48)), Dpi.S(120));
                TextRenderer.DrawText(g, EmptyText, Fonts.Ui, er, T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                return;
            }
            int w = Math.Max(0, Width - Dpi.S(48));
            TextRenderer.DrawText(g, "Nothing matches", nameBold, new Rectangle(Dpi.S(24), Dpi.S(48), w, nameBold.Height + 4), T.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, "Try other words, or clear the filters.", Fonts.Ui, new Rectangle(Dpi.S(24), Dpi.S(48) + nameBold.Height + Dpi.S(6), w, Dpi.S(60)), T.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        }

        void PaintPhase(Graphics g, int k, Rectangle r, DateTime nowUtc)
        {
            Phase p = rows[k].Phase;
            bool sel = k == selected, hover = k == hot;
            Color bg = sel ? Theme.Mix(T.CardAlt, T.Accent, Focused ? 0.1f : 0.06f) : hover ? Theme.Mix(T.CardAlt, T.Hover, 0.7f) : T.CardAlt;
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, 0, r.Y, Width, r.Height);
            using (Pen pen = new Pen(Theme.Mix(bg, T.Divider, 0.85f)))
            {
                if (k > 0 || r.Y > 0) g.DrawLine(pen, 0, r.Y, Width, r.Y);
                g.DrawLine(pen, 0, r.Bottom - 1, Width, r.Bottom - 1);
            }
            if (sel) Shapes.Fill(g, new RectangleF(r.X, r.Y + Dpi.S(12), Dpi.S(3), r.Height - Dpi.S(24)), Dpi.S(1.5f), T.Accent);

            float cx = r.X + Dpi.S(36), cy = r.Y + r.Height / 2f, a = Dpi.S(3.5f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(hover || sel ? T.Text2 : T.Text3, Math.Max(1.25f, Dpi.S(1.5f))))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (!collapsed.Contains(p.Key)) g.DrawLines(pen, new PointF[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
                else g.DrawLines(pen, new PointF[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a) });
            }

            int x = r.X + TextX;
            int right = r.Right - Dpi.S(16);
            int nw = TextRenderer.MeasureText(p.Name, nameBold, Size.Empty, TextFormatFlags.NoPadding).Width;
            int nameW = Math.Max(Dpi.S(60), Math.Min(nw + 2, (r.Width - x) / 2));
            TextRenderer.DrawText(g, p.Name, nameBold, new Rectangle(x, r.Y, nameW, r.Height), T.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int px = x + Math.Min(nw, nameW) + Dpi.S(12);
            State st = p.State;
            if (st != State.Unknown)
            {
                string pill = States.Label(st);
                int pw = ListLook.PillWidth(pill), ph = ListLook.PillH;
                if (px + pw <= right)
                {
                    ListLook.Pill(g, pill, new RectangleF(px, (float)Math.Round(cy - ph / 2f), pw, ph), StateLook.Color(st, T), bg, T, false);
                    px += pw + Dpi.S(16);
                }
            }

            // Right: "9 of 10 done · 1 failed · 11 min 33 s" and a slim bar. Narrow: the bar goes first, then the time.
            Font f = T.Num(Fonts.Ui);
            string done = p.Done + " of " + p.Items.Count + " done";
            string failed = p.FailedCount > 0 ? p.FailedCount + " failed" : null;
            string dur = Txt.Duration(p.Duration(nowUtc));
            int bw = Dpi.S(96);
            for (int fit = 0; fit < 3; fit++)
            {
                bool bar = fit == 0;
                List<string> parts = new List<string>();
                List<Color> colors = new List<Color>();
                parts.Add(done); colors.Add(T.Text2);
                if (failed != null) { parts.Add(failed); colors.Add(T.ErrFg); }
                if (dur.Length > 0 && fit < 2) { parts.Add(dur); colors.Add(T.Text3); }
                int tw = RunsWidth(parts, f);
                int end = bar ? right - bw - Dpi.S(14) : right;
                if (end - tw < px && fit < 2) continue;
                if (bar) ListLook.Bar(g, new RectangleF(right - bw, cy - Dpi.S(2), bw, Dpi.S(4)), p.Progress, StateLook.Color(st, T), Theme.Mix(bg, T.Text3, T.IsDark ? 0.24f : 0.2f));
                DrawRuns(g, parts, colors, f, Math.Max(px, end - tw), end, r.Y, r.Height);
                break;
            }
        }

        static string Dot { get { return "  ·  "; } }

        // A middle dot between runs, with even room on both sides whatever the font does with spaces.
        static int SepW(Font f)
        {
            return TextRenderer.MeasureText("·", f, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(14);
        }

        static int RunsWidth(List<string> parts, Font f)
        {
            int w = 0;
            for (int k = 0; k < parts.Count; k++)
            {
                if (k > 0) w += SepW(f);
                w += TextRenderer.MeasureText(parts[k], f, Size.Empty, TextFormatFlags.NoPadding).Width;
            }
            return w;
        }

        void DrawRuns(Graphics g, List<string> parts, List<Color> colors, Font f, int x, int end, int y, int h)
        {
            TextFormatFlags fl = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            for (int k = 0; k < parts.Count && x < end; k++)
            {
                if (k > 0)
                {
                    int sw = SepW(f);
                    TextRenderer.DrawText(g, "·", f, new Rectangle(x, y, Math.Max(0, Math.Min(sw, end - x)), h), T.Text3, fl | TextFormatFlags.HorizontalCenter);
                    x += sw;
                }
                int w = TextRenderer.MeasureText(parts[k], f, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, parts[k], f, new Rectangle(x, y, Math.Max(0, Math.Min(w + 2, end - x)), h), colors[k], fl);
                x += w;
            }
        }

        void PaintGroup(Graphics g, Row row, Rectangle r)
        {
            int x = r.X + TextX;
            Rectangle tr = new Rectangle(x, r.Y + Dpi.S(14), 0, r.Height - Dpi.S(18));
            int nw = TextRenderer.MeasureText(row.Group, Fonts.SmallSemibold, Size.Empty, TextFormatFlags.NoPadding).Width;
            tr.Width = Math.Max(0, Math.Min(nw + 2, r.Right - x - Dpi.S(60)));
            TextRenderer.DrawText(g, row.Group, Fonts.SmallSemibold, tr, T.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            string count = row.GroupCount.ToString(CultureInfo.InvariantCulture);
            string tail = row.GroupNote != null ? count + Dot + row.GroupNote : count;
            TextRenderer.DrawText(g, tail, T.Num(Fonts.Small), new Rectangle(tr.Right + Dpi.S(8), tr.Y, Math.Max(Dpi.S(50), r.Right - tr.Right - Dpi.S(24)), tr.Height), Theme.Mix(T.Card, T.Text3, 0.7f), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        void PaintItem(Graphics g, int k, Rectangle r, DateTime nowUtc)
        {
            Item i = rows[k].Item;
            bool sel = k == selected, hover = k == hot;
            Color under = T.Card;
            if (sel || hover)
            {
                RectangleF hr = new RectangleF(r.X + Dpi.S(8), r.Y + Dpi.S(2), r.Width - Dpi.S(10), r.Height - Dpi.S(4));
                float rad = Dpi.S(10);
                under = sel ? Raised : T.Hover;
                Shapes.Fill(g, hr, rad, under);
                if (sel)
                {
                    Color edge = Focused ? Theme.Mix(under, T.Accent, 0.7f) : Theme.Mix(under, T.Text3, T.IsDark ? 0.3f : 0.35f);
                    Shapes.Stroke(g, new RectangleF(hr.X + 0.5f, hr.Y + 0.5f, hr.Width - 1, hr.Height - 1), rad, edge, 1f);
                    Shapes.Fill(g, new RectangleF(hr.X + Dpi.S(4), hr.Y + Dpi.S(14), Dpi.S(3), hr.Height - Dpi.S(28)), Dpi.S(1.5f), T.Accent);
                }
            }
            bool dim = i.Expected || i.State == State.Skipped || i.State == State.NotApplicable;
            int tile = Dpi.S(32);
            ListLook.Tile(g, i, new RectangleF(r.X + Dpi.S(20), r.Y + (r.Height - tile) / 2, tile, tile), T, under);

            int x = r.X + TextX;
            int right = r.Right - Dpi.S(16);
            bool active = States.IsActive(i.State) && !i.Expected;
            Color sc = i.Expected ? T.Text3 : PillColor(i);
            string dur = Txt.Duration(i.Duration(nowUtc));
            string tries = i.Attempts.Count > 1 ? "×" + i.Attempts.Count : null;
            Font num = T.Num(Fonts.Ui);
            Color durC = dim ? T.Text3 : T.Text2;
            int ph = ListLook.PillH;
            int metaY = r.Y + Dpi.S(30);
            int nameRight, metaRight;
            if (r.Width >= WideAt)
            {
                // One line: pill column (with its bar while running), tries, time.
                string st = StateLook.StateText(i);
                int durW = Dpi.S(84), col = Dpi.S(136);
                int pillX = right - durW - Dpi.S(48) - col;
                int pw = Math.Min(col, ListLook.PillWidth(st));
                float py = active ? r.Y + Dpi.S(12) : r.Y + (r.Height - ph) / 2;
                ListLook.Pill(g, st, new RectangleF(pillX, py, pw, ph), sc, under, T, i.Expected);
                if (active) Progress(g, i, new RectangleF(pillX + Dpi.S(1), r.Y + Dpi.S(40), col - Dpi.S(24), Dpi.S(4)), sc, under);
                if (tries != null) Tries(g, i, tries, right - durW - Dpi.S(10), r.Y + r.Height / 2f, under);
                TextRenderer.DrawText(g, dur, num, new Rectangle(right - durW, r.Y, durW, r.Height), durC, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                nameRight = metaRight = pillX - Dpi.S(16);
            }
            else
            {
                // Stacked at the right: the pill beside the name, the time (and the bar while running) beside the meta line.
                string st = AppGroups.StateLabel(i) + (i.State == State.Failed && i.ExitCode.HasValue ? " · " + Errors.Short(i.ExitCode) : "");
                int pw = Math.Min(Dpi.S(150), ListLook.PillWidth(st));
                RectangleF pr = new RectangleF(right - pw, r.Y + Dpi.S(7), pw, ph);
                ListLook.Pill(g, st, pr, sc, under, T, i.Expected);
                nameRight = (int)pr.X;
                if (tries != null) nameRight = Tries(g, i, tries, (int)pr.X - Dpi.S(6), pr.Y + ph / 2f, under);
                int dw = dur.Length > 0 ? TextRenderer.MeasureText(dur, num, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
                Rectangle dr = new Rectangle(right - dw - Dpi.S(2), metaY, dw + Dpi.S(2), Fonts.Ui.Height + 2);
                TextRenderer.DrawText(g, dur, num, dr, durC, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                metaRight = right - dw;
                if (active)
                {
                    int bw = Dpi.S(48);
                    RectangleF br = new RectangleF(metaRight - (dw > 0 ? Dpi.S(10) : 0) - bw, metaY + Fonts.Ui.Height / 2f - Dpi.S(2), bw, Dpi.S(4));
                    Progress(g, i, br, sc, under);
                    metaRight = (int)br.X;
                }
                nameRight -= Dpi.S(14);
                metaRight -= Dpi.S(14);
            }

            TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, i.DisplayName, nameFont, new Rectangle(x, r.Y + Dpi.S(8), Math.Max(Dpi.S(40), nameRight - x), nameFont.Height + 2), dim ? T.Text3 : T.Text, tf);
            Color metaC = i.Expected ? Theme.Mix(under, T.Text3, 0.85f) : T.Text3;
            TextRenderer.DrawText(g, Sub(i, rows[k].Phase), Fonts.Ui, new Rectangle(x, metaY, Math.Max(Dpi.S(40), metaRight - x), Fonts.Ui.Height + 2), metaC, tf);

            // A hairline under the row, left out next to a raised or hovered row and above a phase header.
            bool nextLifted = k + 1 < rows.Count && (k + 1 == selected || k + 1 == hot);
            if (!sel && !hover && !nextLifted && !IsPhaseRow(k + 1) && k + 1 < rows.Count)
                using (Pen pen = new Pen(Theme.Mix(T.Card, T.Divider, 0.55f))) g.DrawLine(pen, x, r.Bottom - 1, right, r.Bottom - 1);

            int b0;
            if (i.Key != null && born.TryGetValue(i.Key, out b0))
            {
                // A new row comes in from the card colour.
                int alpha = (int)Math.Round(255 * (1f - ListLook.EaseOut(b0, AppearMs)));
                if (alpha > 0) using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, T.Card))) g.FillRectangle(b, r.X, r.Y, r.Width, r.Height);
            }
        }

        void Progress(Graphics g, Item i, RectangleF br, Color c, Color under)
        {
            Color track = Theme.Mix(under, T.Text3, T.IsDark ? 0.24f : 0.2f);
            if (i.Percent.HasValue) ListLook.Bar(g, br, i.Percent.Value / 100.0, c, track);
            else ListLook.Sliding(g, br, spin / 360f, c, track);
        }

        // The ×3 badge for an item that ran more than once, right-aligned at right. Returns its left edge.
        int Tries(Graphics g, Item i, string tries, int right, float cy, Color under)
        {
            Size ts = TextRenderer.MeasureText(tries, Fonts.SmallSemibold, Size.Empty, TextFormatFlags.NoPadding);
            Color tc = i.FailedAttempts > 0 ? T.WarnFg : T.Text3;
            RectangleF tr = new RectangleF(right - ts.Width - Dpi.S(12), (float)Math.Round(cy - Dpi.S(10)), ts.Width + Dpi.S(12), Dpi.S(20));
            Shapes.Fill(g, tr, tr.Height / 2, ListLook.Tint(tc, under, T));
            TextRenderer.DrawText(g, tries, Fonts.SmallSemibold, Rectangle.Round(tr), tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return (int)tr.X;
        }

        static string Sub(Item i, Phase p)
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(i.Kind)) parts.Add(i.Kind);
            parts.AddRange(AppGroups.Tags(i));
            if (i.Expected)
            {
                parts.Add(i.Get(ExpectedApps.WhyKey));
                return string.Join(Dot, parts.ToArray());
            }
            // The ESP step Apps says which apps it waits for.
            string apps = AppGroups.IsAppsStep(i) ? AppGroups.StepText(p, i) : null;
            if (apps != null)
            {
                parts.Add(apps);
                return string.Join(Dot, parts.ToArray());
            }
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
            return string.Join(Dot, parts.ToArray());
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

        // ---------- input ----------

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
            if (IsGroupRow(h)) h = -1;
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
                EnsureFonts();
                if (e.Button == MouseButtons.Left && IsPhaseRow(k) && e.X < TextX + TextRenderer.MeasureText(rows[k].Phase.Name, nameBold, Size.Empty, TextFormatFlags.NoPadding).Width + Dpi.S(8)) Toggle(k);
                else if (rows[k].Group == null) Select(k);
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
            if (IsPhaseRow(k)) Toggle(k);
            base.OnMouseDoubleClick(e);
        }

        void Toggle(int k)
        {
            Phase p = rows[k].Phase;
            if (!collapsed.Remove(p.Key)) collapsed.Add(p.Key);
            Rebuild();
            for (int i = 0; i < rows.Count; i++) if (IsPhaseRow(i) && rows[i].Phase == p) Select(i);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Left: case Keys.Right: return true;
            }
            return base.IsInputKey(keyData);
        }

        // The keyboard moves over phases and items; the sub-headers between them are skipped.
        int Step(int from, int dir)
        {
            int k = from + dir;
            while (IsGroupRow(k)) k += dir;
            return k >= 0 && k < rows.Count ? k : from;
        }

        int Land(int k, int dir)
        {
            k = Math.Max(0, Math.Min(rows.Count - 1, k));
            if (!IsGroupRow(k)) return k;
            int s = Step(k, dir);
            return s != k ? s : Step(k, -dir);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int page = Math.Max(1, Height / ItemH - 1);
            if (rows.Count > 0)
            {
                switch (e.KeyCode)
                {
                    case Keys.Down: Select(selected < 0 ? 0 : Step(selected, 1)); break;
                    case Keys.Up: Select(selected < 0 ? 0 : Step(selected, -1)); break;
                    case Keys.Home: Select(Land(0, 1)); break;
                    case Keys.End: Select(Land(rows.Count - 1, -1)); break;
                    case Keys.PageDown: Select(Land(selected + page, 1)); break;
                    case Keys.PageUp: Select(Land(selected - page, -1)); break;
                    case Keys.Left:
                        if (IsPhaseRow(selected) && !collapsed.Contains(rows[selected].Phase.Key)) Toggle(selected);
                        else if (selected >= 0 && !IsPhaseRow(selected)) { for (int k = selected; k >= 0; k--) if (IsPhaseRow(k)) { Select(k); break; } }
                        break;
                    case Keys.Right:
                    case Keys.Enter:
                        if (IsPhaseRow(selected) && collapsed.Contains(rows[selected].Phase.Key)) Toggle(selected);
                        break;
                }
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
