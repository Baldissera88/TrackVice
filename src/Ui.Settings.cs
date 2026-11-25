namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    public sealed class PrefsSettingStore : ISettingStore
    {
        public string Get(string name) { return Prefs.Get(name, null); }
        public void Set(string name, string value) { Prefs.Set(name, value); }
    }

    // The subjects on the left of Settings. When the window is narrow, only their icons or first letters show.
    public sealed class SubjectList : FlatControl
    {
        readonly SettingPage[] pages;
        readonly string[] glyphs;
        readonly HashSet<string> changed = new HashSet<string>();
        readonly ToolTip tip = new ToolTip();
        string selected;
        int hot = -1;
        bool compact;
        public event EventHandler Picked;

        public SubjectList(SettingPage[] pages, string[] glyphs)
        {
            this.pages = pages;
            this.glyphs = glyphs;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public string Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public bool Compact
        {
            get { return compact; }
            set { if (compact == value) return; compact = value; Invalidate(); }
        }

        public void SetChanged(ICollection<string> ids)
        {
            changed.Clear();
            foreach (string id in ids) changed.Add(id);
            Invalidate();
        }

        int RowH
        {
            get { return Dpi.S(38); }
        }

        Rectangle RowRect(int i)
        {
            return new Rectangle(Dpi.S(8), Dpi.S(6) + i * RowH, Math.Max(0, Width - Dpi.S(17)), RowH - Dpi.S(4));
        }

        int IndexAt(Point p)
        {
            for (int i = 0; i < pages.Length; i++) if (RowRect(i).Contains(p)) return i;
            return -1;
        }

        int SelectedIndex
        {
            get
            {
                for (int i = 0; i < pages.Length; i++) if (pages[i].Id == selected) return i;
                return -1;
            }
        }

        void Pick(int i)
        {
            if (i < 0 || i >= pages.Length) return;
            selected = pages[i].Id;
            Invalidate();
            if (Picked != null) Picked(this, EventArgs.Empty);
        }

        static string Initials(string title)
        {
            string t = title.Replace("/", " ").Trim();
            string[] w = t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length >= 2 && char.IsUpper(w[1][0])) return w[0].Substring(0, 1) + w[1].Substring(0, 1);
            return t.Length >= 2 ? t.Substring(0, 2) : t;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            using (Pen p = new Pen(T.Divider)) g.DrawLine(p, Width - 1, 0, Width - 1, Height);
            bool icons = Fonts.Icons != null;
            for (int i = 0; i < pages.Length; i++)
            {
                Rectangle r = RowRect(i);
                bool sel = pages[i].Id == selected;
                if (sel)
                {
                    Shapes.Fill(g, r, Dpi.S(5), Theme.Mix(T.Bg, T.Accent, T.IsDark ? 0.22f : 0.13f));
                    Shapes.Fill(g, new RectangleF(r.X, r.Y + Dpi.S(9), Dpi.S(3), r.Height - Dpi.S(18)), 1.5f, T.Accent);
                }
                else if (i == hot) Shapes.Fill(g, r, Dpi.S(5), T.Hover);
                Color fg = sel ? T.Text : T.Text2;
                Font f = sel ? Fonts.UiSemibold : Fonts.Ui;
                if (compact)
                {
                    if (icons) Draw.Glyph(g, glyphs[i], r, sel ? T.Accent : fg);
                    else Draw.Text(g, Initials(pages[i].Title), f, r, sel ? T.Accent : fg, Draw.Line | TextFormatFlags.HorizontalCenter);
                }
                else
                {
                    int x = r.X + Dpi.S(14);
                    if (icons)
                    {
                        Draw.Glyph(g, glyphs[i], new Rectangle(x - Dpi.S(2), r.Y, Dpi.S(20), r.Height), sel ? T.Accent : fg);
                        x += Dpi.S(28);
                    }
                    Draw.Text(g, pages[i].Title, f, new Rectangle(x, r.Y, r.Right - x - Dpi.S(20), r.Height), fg, Draw.Line);
                }
                if (changed.Contains(pages[i].Id))
                {
                    int d = Dpi.S(6);
                    float cx = compact ? r.Right - Dpi.S(8) - d : r.Right - Dpi.S(10) - d;
                    float cy = compact ? r.Y + Dpi.S(6) : r.Y + (r.Height - d) / 2f;
                    Shapes.Fill(g, new RectangleF(cx, cy, d, d), d / 2f, T.Accent);
                }
            }
            int si = SelectedIndex;
            if (Focused && ShowFocusCues && si >= 0) Shapes.Stroke(g, RowRect(si), Dpi.S(5), T.Text, 2f);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hot)
            {
                hot = i;
                Invalidate();
                tip.SetToolTip(this, compact && i >= 0 ? pages[i].Title : null);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            Pick(IndexAt(e.Location));
            base.OnMouseClick(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int si = SelectedIndex;
            if (e.KeyCode == Keys.Up) { Pick(si <= 0 ? 0 : si - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Pick(Math.Min(pages.Length - 1, si + 1)); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }

    // The top of a Settings page: its subject, what it holds, and Reset.
    sealed class SettingsHeader : Panel, IThemed, IMeasured
    {
        Theme theme = Theme.Light();
        public string Title, Summary, Note;
        // Draws the changed mark before the note, so the note explains it.
        public bool NoteMark;
        public readonly FlatButton Reset = new FlatButton("Reset", null);

        public SettingsHeader(string title, string summary)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Title = title;
            Summary = summary;
            Reset.Compact = true;
            Reset.AutoWidth();
            Reset.Width += Dpi.S(8);
            Controls.Add(Reset);
        }

        int TextW(int width)
        {
            return Math.Max(Dpi.S(80), width - (Reset.Visible ? Reset.Width + Dpi.S(16) : 0));
        }

        int MarkW
        {
            get { return NoteMark ? Dpi.S(14) : 0; }
        }

        public int Measure(int width)
        {
            int tw = TextW(width);
            int h = Dpi.S(8) + Fonts.Title.Height + Dpi.S(4) + Draw.WrapHeight(Summary, Fonts.Ui, tw);
            if (!string.IsNullOrEmpty(Note)) h += Dpi.S(6) + Draw.WrapHeight(Note, Fonts.Small, tw - MarkW);
            return h + Dpi.S(8);
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            Reset.ApplyTheme(t);
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            Reset.Location = new Point(Width - Reset.Width, Dpi.S(8));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.Bg);
            int tw = TextW(Width);
            int y = Dpi.S(8);
            Draw.Text(g, Title, Fonts.Title, new Rectangle(Dpi.S(2), y, tw, Fonts.Title.Height + 2), theme.Text, Draw.Line);
            y += Fonts.Title.Height + Dpi.S(4);
            int sh = Draw.WrapHeight(Summary, Fonts.Ui, tw);
            Draw.Text(g, Summary, Fonts.Ui, new Rectangle(Dpi.S(2), y, tw, sh + 2), theme.Text2, Draw.Wrap);
            y += sh;
            if (string.IsNullOrEmpty(Note)) return;
            y += Dpi.S(6);
            if (NoteMark)
            {
                int d = Dpi.S(7);
                Shapes.Fill(g, new RectangleF(Dpi.S(3), y + (Fonts.Small.Height - d) / 2f, d, d), d / 2f, theme.Accent);
            }
            int nh = Draw.WrapHeight(Note, Fonts.Small, tw - MarkW);
            Draw.Text(g, Note, Fonts.Small, new Rectangle(Dpi.S(2) + MarkW, y, tw - MarkW, nh + 2), theme.Text3, Draw.Wrap);
        }
    }

    // Settings: a list of subjects on the left, one page on the right, and a search box over all of them.
    public sealed partial class SettingsDialog : PageForm
    {
        public event EventHandler ThemeChanged, IntervalChanged, StuckChanged;
        // A setting that changes what is read was changed, so a live session should read the device again.
        public bool ReadAgain, RestartRequested;
        // Off for screenshots, so the subject shown is not saved.
        public static bool RememberPage = true;

        static readonly string[] PageGlyphs = { Glyphs2.Settings, Glyphs2.Device, Glyphs2.Apps, Glyphs2.Sync, Glyphs2.Code, Glyphs2.Folder, Glyphs.Log, Glyphs.Shield };

        readonly ISettingStore store = new PrefsSettingStore();
        readonly SubjectList subjects;
        readonly Panel head = new Panel();
        readonly TextBox search = new TextBox();
        readonly Dictionary<string, List<Control>> built = new Dictionary<string, List<Control>>();
        readonly Dictionary<string, SettingsHeader> headers = new Dictionary<string, SettingsHeader>();
        // Each setting's row, and the card in the column that holds it.
        readonly Dictionary<string, SettingRow> rows = new Dictionary<string, SettingRow>();
        readonly Dictionary<string, Control> cards = new Dictionary<string, Control>();
        readonly List<Control> results = new List<Control>();
        string current;
        SettingRow lit;
        Control pendingScroll;
        bool quiet;
        SettingRow logViceRow, customersRow, codeRow, signRow;
        TextBox client, tenant, server, site;
        readonly DownloadSettingsUi downloadUi = new DownloadSettingsUi();

        public SettingsDialog(Theme t) : this(t, null)
        {
        }

        public SettingsDialog(Theme t, string page) : base("TrackVice settings", t)
        {
            subjects = new SubjectList(SettingsCatalog.Pages, PageGlyphs);
            subjects.Dock = DockStyle.Left;
            subjects.Width = Dpi.S(208);
            subjects.ApplyTheme(t);
            subjects.Picked += Safe.Wrap(delegate
            {
                quiet = true;
                search.Text = "";
                quiet = false;
                ShowPage(subjects.Selected, null);
            });
            head.Dock = DockStyle.Top;
            head.Height = Dpi.S(52);
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Font = Fonts.Ui;
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                Draw.Text(e.Graphics, SearchLabel, Fonts.Ui, new Rectangle(Dpi.S(16), 0, search.Left - Dpi.S(16), head.Height), theme.Text2, Draw.Line);
            };
            search.Location = new Point(Dpi.S(16) + Draw.Measure(SearchLabel, Fonts.Ui).Width + Dpi.S(12), Dpi.S(14));
            search.TextChanged += Safe.Wrap(delegate { if (!quiet) Searched(); });
            search.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && search.Text.Trim().Length > 0)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    List<SettingDef> hits = SettingsCatalog.Search(search.Text);
                    if (hits.Count > 0) Open(hits[0]);
                }
                else if (e.KeyCode == Keys.Down) { e.Handled = true; subjects.Focus(); }
            };
            head.Controls.Add(search);
            HandleCreated += delegate
            {
                try { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Name or description (Ctrl+F)"); } catch (Exception) { }
            };
            Controls.Add(subjects);
            Controls.Add(head);
            Resize += delegate { Fit(); };
            FormClosing += delegate { SaveSccm(); };
            PaintChrome(t);
            Fit();
            ActiveControl = search;
            string first = SettingsCatalog.Page(page) != null ? SettingsCatalog.Page(page).Id : SettingsCatalog.Remembered(Prefs.Get(SettingsCatalog.LastPagePref, null));
            ShowPage(first, null);
        }

        const string SearchLabel = "Search";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public string CurrentPage
        {
            get { return current; }
        }

        void PaintChrome(Theme t)
        {
            head.BackColor = t.Bg;
            head.Invalidate();
            search.BackColor = t.Field;
            search.ForeColor = t.Text;
            subjects.ApplyTheme(t);
        }

        void ApplyNewTheme(Theme t)
        {
            Retheme(t);
            PaintChrome(t);
            foreach (KeyValuePair<string, List<Control>> p in built)
                foreach (Control c in p.Value) { IThemed th = c as IThemed; if (th != null) th.ApplyTheme(t); }
            if (ThemeChanged != null) ThemeChanged(this, EventArgs.Empty);
        }

        // Narrow windows keep only the icons or first letters of the subjects.
        void Fit()
        {
            bool narrow = ClientSize.Width < Dpi.S(700);
            subjects.Compact = narrow;
            subjects.Width = narrow ? Dpi.S(60) : Dpi.S(208);
            search.Width = Math.Max(Dpi.S(120), Math.Min(Dpi.S(380), ClientSize.Width - search.Left - Dpi.S(16)));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (pendingScroll != null) { ScrollTo(pendingScroll); pendingScroll = null; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && search.Text.Length > 0)
            {
                search.Text = "";
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                search.Focus();
                search.SelectAll();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- pages ----------

        public void ShowPage(string id, string highlight)
        {
            if (SettingsCatalog.Page(id) == null) id = SettingsCatalog.General;
            if (lit != null) { lit.Highlight = false; lit.Invalidate(); lit = null; }
            current = id;
            subjects.Selected = id;
            ClearResults();
            ClearItems();
            foreach (Control c in Built(id)) Add(c);
            RefreshMarks();
            Relayout();
            if (RememberPage) Prefs.Set(SettingsCatalog.LastPagePref, id);
            SettingRow row;
            Control card;
            if (highlight != null && rows.TryGetValue(highlight, out row) && cards.TryGetValue(highlight, out card))
            {
                row.Highlight = true;
                row.Invalidate();
                lit = row;
                if (Visible) ScrollTo(card);
                else pendingScroll = card;
            }
        }

        List<Control> Built(string id)
        {
            List<Control> l;
            if (built.TryGetValue(id, out l)) return l;
            SettingPage p = SettingsCatalog.Page(id);
            SettingsHeader h = new SettingsHeader(p.Title, p.Summary);
            string page = id;
            h.Reset.Click += Safe.Wrap(delegate { ResetPage(page); });
            headers[id] = h;
            l = new List<Control>();
            l.Add(h);
            switch (id)
            {
                case SettingsCatalog.General: BuildGeneral(l); break;
                case SettingsCatalog.ThisDevice: BuildDevice(l); break;
                case SettingsCatalog.Customers: BuildCustomers(l); break;
                case SettingsCatalog.Intune: BuildIntune(l); break;
                case SettingsCatalog.Sccm: BuildSccm(l); break;
                case SettingsCatalog.Storage: BuildStorage(l); break;
                case SettingsCatalog.LogVice: BuildLogVice(l); break;
                case SettingsCatalog.Data: BuildData(l); break;
            }
            // Rows that other parts of TrackVice make are matched by their title.
            foreach (Control c in l)
            {
                SettingRow r = RowOf(c);
                if (r == null || rows.ContainsValue(r)) continue;
                foreach (SettingDef d in SettingsCatalog.On(id))
                {
                    if ((d.Row ?? d.Title) != r.Title || rows.ContainsKey(d.Id)) continue;
                    rows[d.Id] = r;
                    cards[d.Id] = c;
                    r.WrapAccessory = true;
                    r.MoreToggled += delegate { Relayout(); };
                    break;
                }
            }
            built[id] = l;
            return l;
        }

        static SettingRow RowOf(Control c)
        {
            SettingRow r = c as SettingRow;
            if (r != null) return r;
            foreach (Control k in c.Controls) { r = k as SettingRow; if (r != null) return r; }
            return null;
        }

        // Registers the row of a setting, with its description from the catalog when the row has none.
        SettingRow Bind(string id, SettingRow row)
        {
            SettingDef d = SettingsCatalog.Find(id);
            if (row.Desc == null) row.Desc = d.Summary;
            if (row.More == null) row.More = d.More;
            row.WrapAccessory = true;
            rows[id] = row;
            cards[id] = row;
            row.MoreToggled += delegate { Relayout(); };
            Watch(row.Accessory);
            return row;
        }

        // The changed marks follow every change of a toggle, a choice or a field.
        void Watch(Control c)
        {
            Toggle tg = c as Toggle;
            if (tg != null) { tg.Changed += delegate { RefreshMarks(); }; return; }
            Segmented sg = c as Segmented;
            if (sg != null) { sg.Changed += delegate { RefreshMarks(); }; return; }
            ThemePicker tp = c as ThemePicker;
            if (tp != null) { tp.Changed += delegate { RefreshMarks(); }; return; }
            TextBox tb = c as TextBox;
            if (tb != null) tb.TextChanged += delegate { RefreshMarks(); };
        }

        void RefreshMarks()
        {
            foreach (SettingDef d in SettingsCatalog.All)
            {
                SettingRow r;
                if (!rows.TryGetValue(d.Id, out r)) continue;
                bool m = d.Changed(store);
                if (m != r.Marked) { r.Marked = m; r.Invalidate(); }
            }
            List<string> pages = new List<string>();
            foreach (SettingPage p in SettingsCatalog.Pages) if (SettingsCatalog.ChangedOn(p.Id, store).Count > 0) pages.Add(p.Id);
            subjects.SetChanged(pages);
            foreach (KeyValuePair<string, SettingsHeader> kv in headers) UpdateHeader(kv.Key, kv.Value);
        }

        void UpdateHeader(string page, SettingsHeader h)
        {
            bool can = SettingsCatalog.Resettable(page);
            int n = SettingsCatalog.ChangedOn(page, store).Count;
            h.Reset.Visible = can;
            h.Reset.Enabled = n > 0;
            h.NoteMark = n > 0;
            if (!can) h.Note = page == SettingsCatalog.Data ? null : "Nothing here is reset. Customers, sign-ins and Your storage have their own Remove buttons.";
            else if (n > 0) h.Note = "Changed from the default. Reset puts back the defaults of this page.";
            else h.Note = "Every setting here has its default.";
            h.Invalidate();
        }

        void ResetPage(string page)
        {
            List<SettingDef> changed = SettingsCatalog.ChangedOn(page, store);
            if (changed.Count == 0) return;
            List<string> names = new List<string>();
            foreach (SettingDef d in changed) names.Add(d.Title);
            string what = string.Join(", ", names.ToArray());
            if (MessageBox.Show(this, "Put back the default of " + what + "? Customers, sign-ins and Your storage are not changed.", "Reset " + SettingsCatalog.Page(page).Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool themeChanged = changed.Contains(SettingsCatalog.Find("theme"));
            SettingsCatalog.Reset(page, store);
            // The page is made again, so its controls show the defaults.
            List<Control> old;
            if (built.TryGetValue(page, out old))
            {
                built.Remove(page);
                headers.Remove(page);
                foreach (SettingDef d in SettingsCatalog.On(page)) { rows.Remove(d.Id); cards.Remove(d.Id); }
            }
            if (page == SettingsCatalog.ThisDevice)
            {
                ReadAgain = true;
                if (IntervalChanged != null) IntervalChanged(this, EventArgs.Empty);
                if (StuckChanged != null) StuckChanged(this, EventArgs.Empty);
            }
            ShowPage(page, null);
            if (themeChanged) ApplyNewTheme(Theme.Named(Theme.SavedName()));
            if (old != null) foreach (Control c in old) c.Dispose();
        }

        // ---------- search ----------

        void Searched()
        {
            string text = search.Text.Trim();
            if (text.Length == 0) { ShowPage(current, null); return; }
            if (lit != null) { lit.Highlight = false; lit.Invalidate(); lit = null; }
            subjects.Selected = null;
            ClearResults();
            ClearItems();
            List<SettingDef> hits = SettingsCatalog.Search(text);
            SettingsHeader h = new SettingsHeader("Search", hits.Count == 0
                ? "No setting matches “" + text + "”."
                : hits.Count + (hits.Count == 1 ? " setting matches “" : " settings match “") + text + "”. Enter opens the first.");
            h.Reset.Visible = false;
            results.Add(h);
            foreach (SettingDef d in hits)
            {
                SettingDef one = d;
                SettingPage p = SettingsCatalog.Page(d.Page);
                FlatButton show = SmallButton("Show", delegate { Open(one); });
                SettingRow r = new SettingRow(PageGlyphs[Array.IndexOf(SettingsCatalog.Pages, p)], d.Title, p.Title + ": " + d.Summary, show);
                r.Marked = d.Changed(store);
                results.Add(r);
            }
            foreach (Control c in results) Add(c);
            Relayout();
        }

        void ClearResults()
        {
            if (results.Count == 0) return;
            ClearItems();
            foreach (Control c in results) c.Dispose();
            results.Clear();
        }

        void Open(SettingDef d)
        {
            quiet = true;
            search.Text = "";
            quiet = false;
            ShowPage(d.Page, d.Id);
        }

        // ---------- the pages ----------

        void BuildGeneral(List<Control> l)
        {
            ThemePicker segTheme = new ThemePicker(Palettes.Choices);
            segTheme.SelectedName = Theme.SavedName();
            segTheme.Changed += Safe.Wrap(delegate
            {
                Prefs.Set("Theme", segTheme.SelectedName);
                ApplyNewTheme(Theme.Named(segTheme.SelectedName));
            });
            Toggle watch = new Toggle();
            watch.On = Settings.WatchOnStart;
            watch.Changed += Safe.Wrap(delegate { Prefs.SetBool("WatchOnStart", watch.On); });
            l.Add(Bind("theme", new SettingRow(Glyphs2.Palette, "Theme", null, segTheme)));
            l.Add(Bind("watchonstart", new SettingRow(Glyphs.Watch, "Watch when TrackVice opens", null, watch)));
            SettingDef check = SettingsCatalog.Find("updatecheck");
            foreach (Control c in UpdatesUi.SettingRows(theme, delegate { Relayout(); }))
            {
                SettingRow r = c as SettingRow;
                // The row of the policy says why it is off; the toggle gets the short text and More.
                if (r != null && r.Title == check.Title && r.Accessory is Toggle)
                {
                    r.Desc = check.Summary;
                    r.More = check.More;
                    Watch(r.Accessory);
                }
                l.Add(c);
            }
        }

        void BuildDevice(List<Control> l)
        {
            Segmented segHistory = new Segmented(Settings.HistoryNames);
            segHistory.Selected = Settings.HistoryIndex;
            segHistory.Changed += Safe.Wrap(delegate
            {
                Settings.Hours = Settings.Histories[segHistory.Selected];
                ReadAgain = true;
            });
            Segmented segInterval = new Segmented(Settings.IntervalNames);
            segInterval.Selected = Settings.IntervalIndex;
            segInterval.Changed += Safe.Wrap(delegate
            {
                Prefs.Set("PollMs", Settings.Intervals[segInterval.Selected].ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (IntervalChanged != null) IntervalChanged(this, EventArgs.Empty);
            });
            Segmented segStuck = new Segmented(Settings.StuckNames);
            segStuck.Selected = Settings.StuckIndex;
            segStuck.Changed += Safe.Wrap(delegate
            {
                Prefs.Set("StuckMinutes", Settings.StuckChoices[segStuck.Selected].ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (StuckChanged != null) StuckChanged(this, EventArgs.Empty);
            });
            Toggle wmi = new Toggle();
            wmi.On = Settings.UseWmi;
            wmi.Changed += Safe.Wrap(delegate { Prefs.SetBool("UseWmi", wmi.On); ReadAgain = true; });
            Toggle events = new Toggle();
            events.On = Settings.UseEvents;
            events.Changed += Safe.Wrap(delegate { Prefs.SetBool("UseEvents", events.On); ReadAgain = true; });

            l.Add(new SectionTitle("Reading"));
            l.Add(Bind("history", new SettingRow(Glyphs2.Calendar, "History to read", null, segHistory)));
            l.Add(Bind("wmi", new SettingRow(Glyphs2.Apps, "ConfigMgr client WMI", null, wmi)));
            l.Add(Bind("events", new SettingRow(Glyphs.Log, "Event logs", null, events)));
            if (Program.IsAdmin)
            {
                SettingRow admin = new SettingRow(Glyphs.Shield, "Administrator rights", "TrackVice is running as administrator, so it can read every log, ESP key and WMI class.", null);
                admin.Value = "On";
                l.Add(Bind("admin", admin));
            }
            else
            {
                FlatButton bAdmin = SmallButton("Restart as administrator", delegate
                {
                    RestartRequested = true;
                    Close();
                });
                l.Add(Bind("admin", new SettingRow(Glyphs.Shield, "Administrator rights", null, bAdmin)));
            }
            l.Add(new SectionTitle("Watching"));
            l.Add(Bind("interval", new SettingRow(Glyphs2.Sync, "Read every", null, segInterval)));
            l.Add(Bind("stuck", new SettingRow(Glyphs2.Clock, "Warn when nothing changes for", null, segStuck)));
        }

        void BuildCustomers(List<Control> l)
        {
            FlatButton bOpen = SmallButton("Open", delegate
            {
                using (CustomersPage p = new CustomersPage(theme, CustomerUi.Book)) p.ShowDialog(this);
                // The customer in use may have changed, and with it the tenant, client ID and SMS Provider.
                if (client != null) client.Text = Prefs.Get("GraphClientId", "");
                if (tenant != null) tenant.Text = Prefs.Get("GraphTenant", "");
                if (server != null) server.Text = Prefs.Get("SccmServer", "");
                if (site != null) site.Text = Prefs.Get("SccmSite", "");
                UpdateCustomers();
            });
            FlatButton bCode = SmallButton("Customer code…", delegate
            {
                Customer c = CustomerUi.Book.Current;
                if (c != null) CustomerCodeDialog.Open(this, theme, c);
            });
            customersRow = Bind("customers", new SettingRow(Glyphs2.Device, "Customers", null, bOpen));
            codeRow = Bind("customercode", new SettingRow(Glyphs.Shield, "Customer code", null, bCode));
            l.Add(customersRow);
            l.Add(codeRow);
            l.AddRange(downloadUi.Build(Bind, delegate (string text, EventHandler click) { return SmallButton(text, click); }));
            UpdateCustomers();
        }

        void UpdateCustomers()
        {
            if (customersRow == null) return;
            CustomerBook book = CustomerUi.Book;
            string cur = CustomerUi.CurrentName;
            customersRow.Desc = SettingsCatalog.Find("customers").Summary + " " + (book.Count == 0 ? "None saved yet." : book.Count + " saved. In use: " + (cur ?? "none") + ".");
            bool has = false;
            try { has = cur != null && CustomerCodeDialog.Has(cur); } catch (Exception) { }
            codeRow.Desc = SettingsCatalog.Find("customercode").Summary + " " + (has ? "" : cur == null ? "No customer is in use." : cur + " has no customer code. Devices > Add customer makes one.");
            codeRow.Accessory.Enabled = has;
            customersRow.Invalidate();
            codeRow.Invalidate();
            downloadUi.Refresh();
            Relayout();
        }

        void BuildIntune(List<Control> l)
        {
            client = Field(Prefs.Get("GraphClientId", ""), Dpi.S(300), true);
            client.TextChanged += Safe.Wrap(delegate
            {
                string v = client.Text.Trim();
                if (v.Length == 0) Prefs.Set("GraphClientId", null);
                else if (GraphClient.IsGuid(v)) Prefs.Set("GraphClientId", v);
            });
            tenant = Field(Prefs.Get("GraphTenant", ""), Dpi.S(300), false);
            tenant.TextChanged += Safe.Wrap(delegate
            {
                string v = tenant.Text.Trim();
                Prefs.Set("GraphTenant", v.Length == 0 ? null : v);
            });
            string kept = MainForm.KeptAccount;
            string who = CustomerUi.CurrentName ?? "Default";
            FlatButton bSignOut = SmallButton("Sign out", delegate { });
            signRow = Bind("signin", new SettingRow(Glyphs.Shield, "Intune sign-in", kept != null
                ? "Signed in as " + kept + " for " + who + ". The sign-in is kept encrypted for your Windows account. Sign out deletes it and the kept names."
                : "Not signed in for " + who + ". Sign in from Devices > Autopilot in Intune or Names from Intune. The sign-in is then kept until you sign out here.", bSignOut));
            bSignOut.Enabled = kept != null;
            bSignOut.Click += Safe.Wrap(delegate
            {
                MainForm.SignOutIntune();
                bSignOut.Enabled = false;
                signRow.Desc = "Signed out. The kept sign-in and names are deleted.";
                signRow.Invalidate();
                Relayout();
            });
            l.Add(new SectionTitle("Sign-in"));
            l.Add(signRow);
            SettingRow clientRow = Bind("clientid", new SettingRow(Glyphs2.Code, "Client ID", null, client));
            clientRow.Desc = clientRow.Desc.TrimEnd('.') + ", " + GraphClient.DefaultClientId + ".";
            l.Add(clientRow);
            l.Add(Bind("tenant", new SettingRow(Glyphs.Shield, "Tenant", null, tenant)));
            AddAppRegistration(l);
            l.Add(new SectionTitle("Names from Intune"));
            l.Add(NameRow("names.scripts", Glyphs2.Code));
            l.Add(NameRow("names.apps", Glyphs2.Apps));
            l.Add(NameRow("names.remediations", Glyphs.Shield));
            l.Add(NameRow("names.profiles", Glyphs2.Device));
            l.Add(new SectionTitle("Expected apps"));
            l.Add(ExpectedRow("expected.intune", Glyphs2.Apps, kept != null
                ? "Reads the apps assigned to the device from Intune with the sign-in of " + kept + "."
                : "Not signed in for " + who + ", so this source is not used until you sign in."));
            l.Add(ExpectedRow("expected.history", Glyphs2.Calendar, null));
        }

        // The apps a deployment is expected to bring, listed as waiting from the start. Each source has its own switch.
        SettingRow ExpectedRow(string id, string glyph, string desc)
        {
            SettingDef d = SettingsCatalog.Find(id);
            string key = d.Keys[0];
            Toggle tg = new Toggle();
            tg.On = Prefs.GetBool(key, d.Defaults[0] == "1");
            tg.Changed += Safe.Wrap(delegate { Prefs.SetBool(key, tg.On); });
            SettingRow row = Bind(id, new SettingRow(glyph, d.Title, null, tg));
            if (desc != null) row.Desc = desc + " " + row.Desc;
            return row;
        }

        // The lookups Devices > Names from Intune starts with ticked. Each needs a permission of its own.
        SettingRow NameRow(string id, string glyph)
        {
            SettingDef d = SettingsCatalog.Find(id);
            string key = d.Keys[0];
            Toggle tg = new Toggle();
            tg.On = Prefs.GetBool(key, d.Defaults[0] == "1");
            tg.Changed += Safe.Wrap(delegate { Prefs.SetBool(key, tg.On); });
            return Bind(id, new SettingRow(glyph, d.Title, null, tg));
        }

        void BuildSccm(List<Control> l)
        {
            server = Field(Prefs.Get("SccmServer", ""), Dpi.S(260), false);
            site = Field(Prefs.Get("SccmSite", ""), Dpi.S(80), false);
            site.MaxLength = 3;
            server.Leave += delegate { SaveSccm(); };
            site.Leave += delegate { SaveSccm(); };
            Toggle earlier = new Toggle();
            earlier.On = Prefs.GetBool("SccmEarlierRuns", false);
            earlier.Changed += Safe.Wrap(delegate { Prefs.SetBool("SccmEarlierRuns", earlier.On); });
            l.Add(new SectionTitle("SMS Provider"));
            l.Add(Bind("smsprovider", new SettingRow(Glyphs2.Device, "SMS Provider", null, server)));
            l.Add(Bind("sitecode", new SettingRow(Glyphs2.Code, "Site code", null, site)));
            l.Add(new SectionTitle("Task sequences"));
            l.Add(Bind("earlierruns", new SettingRow(Glyphs2.Calendar, "Show earlier runs", null, earlier)));
        }

        // Saved like SMS Provider connection does: on this PC and with the customer in use.
        void SaveSccm()
        {
            if (server == null || site == null) return;
            string srv = server.Text.Trim(), sc = site.Text.Trim();
            if (srv == Prefs.Get("SccmServer", "") && sc == Prefs.Get("SccmSite", "")) return;
            Prefs.Set("SccmServer", srv.Length == 0 ? null : srv);
            Prefs.Set("SccmSite", sc.Length == 0 ? null : sc);
            CustomerUi.Remember(delegate (Customer cu) { cu.SccmServer = srv; cu.SccmSite = sc; });
        }

        void BuildStorage(List<Control> l)
        {
            foreach (Control c in YourStorageUi.SettingRows(theme, delegate { Relayout(); })) l.Add(c);
            FlatButton bPublish = SmallButton("Publish", delegate
            {
                bool setUp;
                using (PublishDialog p = PublishDialog.ForCustomer(theme)) { p.ShowDialog(this); setUp = p.WantsSetUp; }
                if (setUp)
                {
                    using (YourStorageDialog d = YourStorageDialog.ForLive(theme)) d.ShowDialog(this);
                    Relayout();
                }
            });
            l.Add(new SectionTitle("Agent updates"));
            l.Add(Bind("agentupdate", new SettingRow(Glyphs.Shield, "Publish agent update", null, bPublish)));
        }

        void BuildLogVice(List<Control> l)
        {
            FlatButton bDownload = SmallButton("Download", delegate { DownloadLogVice(); });
            FlatButton bBrowse = SmallButton("Browse", delegate { BrowseLogVice(); });
            FlatButton bClear = SmallButton("Clear", delegate
            {
                Prefs.Set("LogVicePath", null);
                UpdateLogVice();
            });
            logViceRow = Bind("logvice", new SettingRow(Glyphs2.Folder, "LogVice", "", new ButtonStrip(bDownload, bBrowse, bClear)));
            l.Add(logViceRow);
            UpdateLogVice();
        }

        void UpdateLogVice()
        {
            string custom = Prefs.Get("LogVicePath", null);
            string found = LogViceLink.Find();
            string where;
            string version = LogViceGet.VersionOf(found);
            string has = version != null ? " Version " + version + "." : "";
            if (!string.IsNullOrEmpty(custom)) where = "Chosen: " + custom + (found == custom ? "." + has : " (not found)");
            else if (found != null) where = "Found: " + found + "." + has;
            else where = "Not found. Download gets it from GitHub, Browse chooses it.";
            logViceRow.Desc = SettingsCatalog.Find("logvice").Summary + " " + where + " LogVice 2.0 or later opens at the line behind a step and in TrackVice's theme.";
            logViceRow.Invalidate();
            RefreshMarks();
            Relayout();
        }

        // Opens the same question as a LogVice button, without opening logs afterwards.
        void DownloadLogVice()
        {
            LogViceContext c = null;
            foreach (Form f in Application.OpenForms)
            {
                MainForm m = f as MainForm;
                if (m != null) { c = m.LogViceContextNow(); break; }
            }
            if (c == null) { c = new LogViceContext(); c.Window = true; c.Admin = Program.IsAdmin; }
            bool find;
            using (LogViceDialog d = new LogViceDialog(theme, c, false))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                find = d.FindChosen;
            }
            if (find) BrowseLogVice();
            else UpdateLogVice();
        }

        void BrowseLogVice()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "LogVice|LogVice.exe|Programs|*.exe";
                d.Title = "Locate LogVice";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Prefs.Set("LogVicePath", d.FileName);
            }
            UpdateLogVice();
        }

        static string LocalFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"); }
        }

        void BuildData(List<Control> l)
        {
            SettingRow store = Bind("settingsstore", new SettingRow(Glyphs2.Settings, "Where settings are stored", null, null));
            store.Desc += " " + Prefs.Location;
            FlatButton bFolder = SmallButton("Open folder", delegate
            {
                if (!Directory.Exists(LocalFolder)) { Safe.Say("Nothing is kept in " + LocalFolder + " yet."); return; }
                try { Process.Start("explorer.exe", "\"" + LocalFolder + "\""); } catch (Exception) { }
            });
            FlatButton bIntune = SmallButton("Intune / cloud", delegate { ShowPage(SettingsCatalog.Intune, "signin"); });
            SettingRow kept = null;
            FlatButton bDelete = SmallButton("Delete", delegate
            {
                if (MessageBox.Show(this, "Delete the names read from Intune and the logs downloaded from devices on this PC? They are read or downloaded again when needed. Sign-ins and customers are kept.", "Kept names and logs", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                bool all = true;
                foreach (string f in new string[] { NameCache.DefaultFolder, DeviceLogs.Root })
                {
                    try { if (Directory.Exists(f)) Directory.Delete(f, true); }
                    catch (Exception) { all = false; }
                }
                kept.Desc = all ? "Deleted. Names are read again from Intune the next time they are needed." : "Some files could not be deleted. A log may be open in another program.";
                kept.Invalidate();
                Relayout();
            });
            l.Add(new SectionTitle("On this PC"));
            l.Add(store);
            l.Add(Bind("files", new SettingRow(Glyphs2.Folder, "Files on this PC", null, bFolder)));
            l.Add(new SectionTitle("What leaves this PC"));
            l.Add(Bind("sends", new SettingRow(Glyphs.Info, "What TrackVice sends", null, null)));
            l.Add(new SectionTitle("Forget"));
            l.Add(Bind("signins", new SettingRow(Glyphs.Shield, "Sign-ins", null, bIntune)));
            kept = Bind("keptdata", new SettingRow(Glyphs2.Reset, "Kept names and logs", null, bDelete));
            l.Add(kept);
        }

        TextBox Field(string value, int width, bool mono)
        {
            TextBox b = new TextBox();
            b.Width = width;
            b.Font = mono ? Fonts.Mono : Fonts.Ui;
            b.BorderStyle = BorderStyle.FixedSingle;
            b.BackColor = theme.Field;
            b.ForeColor = theme.Text;
            b.Text = value ?? "";
            return b;
        }

        // ---------- screenshots ----------

        // --page settings, settings-<subject>, commandline and shortcuts. With settings, --select is a search, the
        // ID of a setting to open highlighted, or narrow for the smallest window.
        public static bool IsShotPage(string page)
        {
            if (page == null) return false;
            return page == "settings" || page.StartsWith("settings-", StringComparison.Ordinal) || page == AboutDialog.CommandLinePart || page == AboutDialog.ShortcutsPart;
        }

        public static Form ForShot(Theme t, string page, string select)
        {
            if (page == AboutDialog.CommandLinePart || page == AboutDialog.ShortcutsPart) return new AboutDialog(t, page);
            RememberPage = false;
            string sub = page.StartsWith("settings-", StringComparison.Ordinal) ? page.Substring(9) : null;
            if (sub == SettingsCatalog.Storage) YourStorageUi.UseDemo();
            SettingsDialog d = new SettingsDialog(t, sub ?? SettingsCatalog.General);
            SettingDef def = select != null ? SettingsCatalog.Find(select) : null;
            if (select == "narrow") d.Size = d.MinimumSize;
            else if (def != null) d.ShowPage(def.Page, def.Id);
            else if (!string.IsNullOrEmpty(select)) d.Shown += delegate { d.search.Text = select; };
            return d;
        }
    }

    public sealed partial class MainForm
    {
        // Help > Command line and Help > Keyboard shortcuts.
        void HelpPage(string part)
        {
            using (AboutDialog d = new AboutDialog(theme, part)) d.ShowDialog(this);
        }
    }
}
