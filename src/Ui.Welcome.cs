namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Windows.Forms;

    // A large choice on the welcome page: a glyph, a title and one or two plain sentences.
    public sealed class WelcomeCard : FlatControl
    {
        public string Glyph, Title, Desc;

        public WelcomeCard(string glyph, string title, string desc)
        {
            Glyph = glyph;
            Title = title;
            Desc = desc;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        int TextX
        {
            get { return Fonts.Icons != null && !string.IsNullOrEmpty(Glyph) ? Dpi.S(64) : Dpi.S(22); }
        }

        public int Measure(int width)
        {
            int tw = Math.Max(Dpi.S(80), width - TextX - Dpi.S(20));
            return Math.Max(Dpi.S(96), Dpi.S(20) + Fonts.UiSemibold.Height + Dpi.S(6) + Draw.WrapHeight(Desc, Fonts.Ui, tw) + Dpi.S(20));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(8), down ? T.Pressed : hover ? Theme.Mix(T.Card, T.Accent, T.IsDark ? 0.08f : 0.05f) : T.Card);
            Shapes.Stroke(g, r, Dpi.S(8), hover || (Focused && ShowFocusCues) ? T.Accent : T.Divider, 1f);
            Draw.Glyph(g, Glyph, new Rectangle(Dpi.S(20), Dpi.S(20), Dpi.S(28), Dpi.S(28)), T.Accent);
            int x = TextX, tw = Math.Max(0, Width - x - Dpi.S(20));
            int y = Dpi.S(20);
            Draw.Text(g, Title, Fonts.UiSemibold, new Rectangle(x, y, tw, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
            y += Fonts.UiSemibold.Height + Dpi.S(6);
            Draw.Text(g, Desc, Fonts.Ui, new Rectangle(x, y, tw, Height - y), T.Text2, Draw.Wrap);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // One step of Getting started: its number, or a tick when done, what it is, and the button that opens its guide.
    public sealed class StepRow : FlatControl
    {
        public readonly SetupStep Step;
        public readonly int Number;
        public readonly FlatButton Button;

        public StepRow(int number, SetupStep step)
        {
            Number = number;
            Step = step;
            Button = new FlatButton(step.Action, null);
            Button.Primary = !step.Done;
            Button.AutoWidth();
            Button.Width = Math.Max(Button.Width, Dpi.S(120));
            Controls.Add(Button);
        }

        int TextW(int width)
        {
            return Math.Max(Dpi.S(80), width - Dpi.S(72) - Button.Width - Dpi.S(40));
        }

        public int Measure(int width)
        {
            return Math.Max(Dpi.S(76), Dpi.S(16) + Fonts.UiSemibold.Height + Dpi.S(4) + Draw.WrapHeight(Step.Text, Fonts.Ui, TextW(width)) + Dpi.S(16));
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            BackColor = t.Card;
            if (Button != null) Button.ApplyTheme(t);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            // Called once from the base constructor, before the button is made.
            if (Button == null) return;
            Button.Location = new Point(Width - Dpi.S(20) - Button.Width, (Height - Button.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Draw.Card(g, ClientRectangle, T);
            int d = Dpi.S(30);
            RectangleF c = new RectangleF(Dpi.S(22), (Height - d) / 2f, d, d);
            if (Step.Done)
            {
                Shapes.Fill(g, c, d / 2f, T.OkFg);
                using (Pen p = new Pen(T.Card, Dpi.S(2.5f)))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLines(p, new PointF[] { new PointF(c.X + d * 0.28f, c.Y + d * 0.52f), new PointF(c.X + d * 0.44f, c.Y + d * 0.68f), new PointF(c.X + d * 0.73f, c.Y + d * 0.36f) });
                }
            }
            else
            {
                Shapes.Stroke(g, c, d / 2f, T.Accent, 1.5f);
                Draw.Text(g, Number.ToString(), Fonts.UiSemibold, Rectangle.Round(c), T.Accent, Draw.Line | TextFormatFlags.HorizontalCenter);
            }
            int x = Dpi.S(72), tw = TextW(Width);
            int th = Fonts.UiSemibold.Height, dh = Draw.WrapHeight(Step.Text, Fonts.Ui, tw);
            int y = (Height - th - Dpi.S(4) - dh) / 2;
            string title = Step.Title;
            Draw.Text(g, title, Fonts.UiSemibold, new Rectangle(x, y, tw, th + 2), Step.Done ? T.Text2 : T.Text, Draw.Line);
            if (Step.Done)
            {
                int w = Draw.Measure(title, Fonts.UiSemibold).Width + Dpi.S(10);
                Draw.Text(g, "Done", Fonts.Small, new Rectangle(x + w, y, Dpi.S(80), th + 2), T.OkFg, Draw.Line);
            }
            Draw.Text(g, Step.Text, Fonts.Ui, new Rectangle(x, y + th + Dpi.S(4), tw, dh + 2), Step.Done ? T.Text3 : T.Text2, Draw.Wrap);
        }
    }

    // The heading of a welcome page: the TrackVice mark, a title and a line under it.
    public sealed class WelcomeHead : FlatControl
    {
        public string Title, Desc;
        public bool Mark = true;

        public WelcomeHead(string title, string desc)
        {
            Title = title;
            Desc = desc;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        int TextX
        {
            get { return Mark ? Dpi.S(76) : 0; }
        }

        public int Measure(int width)
        {
            return Math.Max(Mark ? Dpi.S(64) : 0, Fonts.Title.Height + Dpi.S(6) + Draw.WrapHeight(Desc, Fonts.Ui, Math.Max(Dpi.S(80), width - TextX)) + Dpi.S(4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            if (Mark) Draw.Mark(g, new RectangleF(0, 0, Dpi.S(60), Dpi.S(60)));
            int x = TextX, tw = Math.Max(0, Width - x);
            Draw.Text(g, Title, Fonts.Title, new Rectangle(x, 0, tw, Fonts.Title.Height + 2), T.Text, Draw.Line);
            int y = Fonts.Title.Height + Dpi.S(6);
            Draw.Text(g, Desc, Fonts.Ui, new Rectangle(x, y, tw, Height - y), T.Text2, Draw.Wrap);
        }
    }

    // The welcome page in the main window: four ways to start, the question who the setup is for, and Getting started.
    public sealed class WelcomePage : Panel, IThemed
    {
        public const string Choices = "choices", Audience = "audience", Checklist = "checklist";
        public event Action<string> Chosen;
        public event Action<string> StepClicked;
        public event Action<string> AudienceChosen;
        public event EventHandler NotAgain, Closed, Back;
        public event EventHandler RecentClicked;

        Theme theme = Theme.Light();
        readonly Panel col = new Panel();
        readonly List<Control> items = new List<Control>();
        string mode = Choices;
        string audience;

        public WelcomePage()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            DoubleBuffered = true;
            Controls.Add(col);
            Resize += delegate { Relayout(); };
        }

        public string Mode
        {
            get { return mode; }
        }

        public void ShowChoices()
        {
            mode = Choices;
            Clear();
            Add(new WelcomeHead("Welcome to TrackVice", "TrackVice follows deployments while they happen: SCCM task sequences, Autopilot with the Enrollment Status Page, and Intune apps, scripts and policies. It only reads. How do you want to start?"));
            Add(Card(Glyphs2.Device, "Follow this device", "Watch what deploys on this PC right now, with the log line behind every step.", "device"));
            Add(Card(Glyphs2.Apps, "Follow devices from my PC", "Follow Autopilot and task sequences on other devices. Set up once for your own organization or for customers.", "setup"));
            Add(Card(Glyphs2.Folder, "Open logs or a session", "Read a folder of logs from a device, such as a collected zip, or a session someone saved.", "open"));
            Add(Card(Glyphs.Watch, "Look around", "Play a made-up Autopilot deployment. Nothing on this PC or in Intune is used.", "demo"));
            FlowLayoutPanel foot = Foot();
            FlatButton not = Small("Don't show again", delegate { if (NotAgain != null) NotAgain(this, EventArgs.Empty); });
            foot.Controls.Add(not);
            foot.Controls.Add(Small("Recent devices", delegate { if (RecentClicked != null) RecentClicked(this, EventArgs.Empty); }));
            foot.Controls.Add(Quiet("Help > Welcome shows this page again."));
            Add(foot);
            Done();
        }

        public void ShowAudience()
        {
            mode = Audience;
            Clear();
            WelcomeHead h = new WelcomeHead("Follow devices from your PC", "For your own organization or for customers? The steps are the same; only the words differ.");
            Add(h);
            Add(Card(Glyphs2.Device, "My own organization", "One Intune tenant: the devices of the organization you work for.", GettingStarted.Own));
            Add(Card(Glyphs2.Apps, "Customers", "You follow devices in the tenants of several customers, as a consultant or partner.", GettingStarted.Customers));
            FlowLayoutPanel foot = Foot();
            foot.Controls.Add(Small("Back", delegate { if (Back != null) Back(this, EventArgs.Empty); }));
            Add(foot);
            Done();
        }

        public void ShowChecklist(string who, List<SetupStep> list)
        {
            mode = Checklist;
            audience = who;
            Clear();
            int done = GettingStarted.DoneCount(list);
            string desc = (who == GettingStarted.Own ? "To follow the devices of your organization from this PC." : "To follow the devices of your customers from this PC.")
                + " " + done + " of " + list.Count + " done. Each step ticks itself off when TrackVice sees it is done.";
            Add(new WelcomeHead("Getting started", desc));
            for (int i = 0; i < list.Count; i++)
            {
                StepRow r = new StepRow(i + 1, list[i]);
                string id = list[i].Id;
                r.Button.Click += Safe.Wrap(delegate { if (StepClicked != null) StepClicked(id); });
                Add(r);
            }
            FlowLayoutPanel foot = Foot();
            FlatButton close = Small("Close", delegate { if (Closed != null) Closed(this, EventArgs.Empty); });
            foot.Controls.Add(close);
            string other = who == GettingStarted.Own ? GettingStarted.Customers : GettingStarted.Own;
            foot.Controls.Add(Small(other == GettingStarted.Own ? "For my own organization instead" : "For customers instead", delegate { if (AudienceChosen != null) AudienceChosen(other); }));
            foot.Controls.Add(Quiet("Help > Getting started opens this list again."));
            Add(foot);
            Done();
        }

        public string ChecklistAudience
        {
            get { return audience; }
        }

        WelcomeCard Card(string glyph, string title, string desc, string id)
        {
            WelcomeCard c = new WelcomeCard(glyph, title, desc);
            c.Tag = "choice";
            c.Click += Safe.Wrap(delegate
            {
                if (mode == Audience) { if (AudienceChosen != null) AudienceChosen(id); }
                else if (Chosen != null) Chosen(id);
            });
            return c;
        }

        FlatButton Small(string text, EventHandler click)
        {
            FlatButton b = new FlatButton(text, null);
            b.Compact = true;
            b.AutoWidth();
            b.Margin = new Padding(0, 0, Dpi.S(8), 0);
            b.Click += Safe.Wrap(click);
            return b;
        }

        static Label Quiet(string text)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = text;
            l.Font = Fonts.Small;
            l.Tag = "quiet";
            l.Margin = new Padding(Dpi.S(8), Dpi.S(9), 0, 0);
            return l;
        }

        static FlowLayoutPanel Foot()
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.WrapContents = false;
            f.Height = Dpi.S(36);
            return f;
        }

        void Clear()
        {
            foreach (Control c in items) c.Dispose();
            items.Clear();
            col.Controls.Clear();
        }

        void Add(Control c)
        {
            items.Add(c);
            col.Controls.Add(c);
        }

        void Done()
        {
            ApplyTheme(theme);
            AutoScrollPosition = new Point(0, 0);
            Relayout();
        }

        static int Measure(Control c, int w)
        {
            if (c is WelcomeCard) return ((WelcomeCard)c).Measure(w);
            if (c is StepRow) return ((StepRow)c).Measure(w);
            if (c is WelcomeHead) return ((WelcomeHead)c).Measure(w);
            return c.Height;
        }

        // A column at most 860 wide in the middle. The four choices go in two columns when there is room.
        bool laying;

        void Relayout()
        {
            if (laying) return;
            laying = true;
            try { Lay(); }
            finally { laying = false; }
        }

        // The width leaves room for the scroll bar either way, so the bar coming or going does not move the column.
        void Lay()
        {
            int pad = Dpi.S(24);
            int avail = Width - SystemInformation.VerticalScrollBarWidth;
            int w = Math.Min(Dpi.S(860), Math.Max(Dpi.S(300), avail - pad * 2));
            int gap = Dpi.S(12);
            bool two = w >= Dpi.S(620);
            int cw = two ? (w - gap) / 2 : w;
            int y = 0;
            col.SuspendLayout();
            for (int i = 0; i < items.Count; i++)
            {
                Control c = items[i];
                if ("choice".Equals(c.Tag) && two && i + 1 < items.Count && "choice".Equals(items[i + 1].Tag))
                {
                    Control n = items[i + 1];
                    int h = Math.Max(Measure(c, cw), Measure(n, cw));
                    c.SetBounds(0, y, cw, h);
                    n.SetBounds(cw + gap, y, w - cw - gap, h);
                    y += h + gap;
                    i++;
                    continue;
                }
                int ch = Measure(c, w);
                if (c is WelcomeHead) { c.SetBounds(0, y, w, ch); y += ch + Dpi.S(24); continue; }
                if (c is FlowLayoutPanel) y += Dpi.S(8);
                c.SetBounds(0, y, w, ch);
                y += ch + gap;
            }
            col.ResumeLayout(true);
            int top = Math.Max(Dpi.S(28), (ClientSize.Height - y) / 3);
            col.SetBounds(Math.Max(pad, (avail - w) / 2), top + AutoScrollPosition.Y, w, y + Dpi.S(24));
            foreach (Control c in items) c.Invalidate();
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            col.BackColor = t.Bg;
            foreach (Control c in items) Recolor(c, t);
            Invalidate(true);
        }

        static void Recolor(Control c, Theme t)
        {
            IThemed th = c as IThemed;
            if (th != null) { th.ApplyTheme(t); return; }
            Label l = c as Label;
            if (l != null) { l.ForeColor = "quiet".Equals(l.Tag) ? t.Text3 : t.Text2; l.BackColor = t.Bg; return; }
            c.BackColor = t.Bg;
            foreach (Control k in c.Controls) Recolor(k, t);
        }
    }

    // A slim line under the toolbar while Getting started is not done: what is next, and buttons to go on or hide it.
    public sealed class SetupBar : Panel, IThemed
    {
        public readonly Label Text2 = new Label();
        public readonly FlatButton Next = new FlatButton("Next step", null);
        public readonly FlatButton All = new FlatButton("Getting started", null);
        public readonly FlatButton HideButton = new FlatButton("Hide", null);

        public SetupBar()
        {
            Dock = DockStyle.Top;
            Height = Dpi.S(44);
            Padding = new Padding(Dpi.S(20), Dpi.S(6), Dpi.S(20), Dpi.S(6));
            Text2.Dock = DockStyle.Fill;
            Text2.TextAlign = ContentAlignment.MiddleLeft;
            Text2.AutoEllipsis = true;
            Next.Primary = true;
            foreach (FlatButton b in new FlatButton[] { HideButton, All, Next })
            {
                b.Compact = true;
                b.AutoWidth();
                b.Dock = DockStyle.Right;
                Controls.Add(b);
                Panel gap = new Panel();
                gap.Dock = DockStyle.Right;
                gap.Width = Dpi.S(6);
                Controls.Add(gap);
            }
            Controls.Add(Text2);
            Text2.BringToFront();
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.AccentSoft;
            foreach (Control c in Controls) if (!(c is FlatButton)) c.BackColor = t.AccentSoft;
            Text2.ForeColor = t.Text;
            Next.ApplyTheme(t);
            All.ApplyTheme(t);
            HideButton.ApplyTheme(t);
        }
    }

    // What Getting started looks at on this PC.
    public sealed class LiveSetupFacts : ISetupFacts
    {
        public bool StorageReady
        {
            get
            {
                try { return YourStorageUi.Load() != null; }
                catch (Exception) { return false; }
            }
        }

        // A customer made with Add customer has a customer code kept on this PC.
        public bool CustomerMade
        {
            get
            {
                try
                {
                    foreach (Customer c in CustomerUi.Book.All)
                        if (CustomerCodes.Load(YourStorageUi.Secrets, c.Name) != null) return true;
                }
                catch (Exception) { }
                return false;
            }
        }

        public bool UploadSeen
        {
            get { return Prefs.GetBool(GettingStarted.UploadPref, false); }
        }

        public bool ListOpened
        {
            get { return Prefs.GetBool(GettingStarted.ListPref, false); }
        }
    }

    public sealed partial class MainForm
    {
        WelcomePage welcome;
        SetupBar setupBar;
        // Screenshots show made-up progress instead of what this PC has.
        ISetupFacts setupFacts;
        string shotAudience;

        ISetupFacts SetupFacts
        {
            get { return setupFacts ?? (setupFacts = new LiveSetupFacts()); }
        }

        string SetupAudience
        {
            get
            {
                string a = shotAudience ?? Prefs.Get(GettingStarted.AudiencePref, null);
                return a == GettingStarted.Own || a == GettingStarted.Customers ? a : null;
            }
        }

        void Remember(string name, string value)
        {
            if (start.Shot == null) Prefs.Set(name, value);
        }

        // At start, after this device is read: the welcome, unless the device deploys or TrackVice was started for something.
        void MaybeWelcome()
        {
            bool forSomething = start.Demo != null || start.File != null || start.Folder != null || start.Remote != null || start.Wall || start.Compact || start.Page != null || start.Shot != null;
            UpdateContext c = StartContext();
            string why = WelcomeGate.Reason(c, forSomething, Prefs.GetBool(WelcomeGate.SeenPref, false));
            if (why == null) ShowWelcome(WelcomePage.Choices);
            MaybeGetStarted(forSomething, why);
            UpdateSetupBar();
        }

        // The context the update check uses, here for the welcome.
        UpdateContext StartContext()
        {
            UpdateContext c = new UpdateContext();
            c.Window = true;
            c.Screenshot = start.Shot != null;
            bool system = false;
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent()) system = id.IsSystem;
            }
            catch (Exception) { }
            bool ts = false;
            try
            {
                foreach (string n in new string[] { "TSManager", "TsProgressUI" })
                {
                    Process[] ps = Process.GetProcessesByName(n);
                    if (ps.Length > 0) ts = true;
                    foreach (Process p in ps) p.Dispose();
                }
            }
            catch (Exception) { }
            try
            {
                using (LiveRegistry reg = new LiveRegistry()) UpdateGate.ReadDevice(c, reg, Environment.UserName, system, ts);
            }
            catch (Exception) { }
            return c;
        }

        void BuildWelcome()
        {
            if (welcome != null) return;
            welcome = new WelcomePage();
            welcome.Visible = false;
            welcome.Chosen += delegate (string id) { Safe.Run(delegate { WelcomeChoice(id); }); };
            welcome.AudienceChosen += delegate (string a) { Safe.Run(delegate { Remember(GettingStarted.AudiencePref, a); Remember(GettingStarted.HiddenPref, null); ShowChecklist(a); }); };
            welcome.StepClicked += delegate (string id) { Safe.Run(delegate { OpenStep(id); }); };
            welcome.Back += delegate { Safe.Run(delegate { welcome.ShowChoices(); }); };
            welcome.Closed += delegate { Safe.Run(delegate { HideWelcome(); AfterWelcome(); }); };
            welcome.RecentClicked += delegate { Safe.Run(delegate { OpenRecentList(""); }); };
            welcome.NotAgain += delegate
            {
                Safe.Run(delegate
                {
                    Remember(WelcomeGate.SeenPref, "1");
                    HideWelcome();
                    AfterWelcome();
                    Status("The welcome page will not show at start. Help > Welcome shows it again.");
                });
            };
            themed.Add(welcome);
            Controls.Add(welcome);
            Controls.SetChildIndex(welcome, 0);
            welcome.ApplyTheme(theme);
        }

        void ShowWelcome(string mode)
        {
            BuildWelcome();
            HidePages();
            if (mode == WelcomePage.Checklist)
            {
                string a = SetupAudience;
                if (a == null) welcome.ShowAudience();
                else ShowChecklist(a);
            }
            else if (mode == WelcomePage.Audience) welcome.ShowAudience();
            else welcome.ShowChoices();
            foreach (Control c in new Control[] { chips, rail, tabs, content, banner }) c.Visible = false;
            if (setupBar != null) setupBar.Visible = false;
            welcome.Visible = true;
            welcome.BringToFront();
            welcome.Focus();
        }

        void ShowChecklist(string audience)
        {
            BuildWelcome();
            welcome.ShowChecklist(audience, GettingStarted.Steps(audience, SetupFacts));
        }

        bool WelcomeVisible
        {
            get { return welcome != null && welcome.Visible; }
        }

        void HideWelcome()
        {
            if (!WelcomeVisible) return;
            welcome.Visible = false;
            foreach (Control c in new Control[] { chips, rail, tabs, content }) c.Visible = true;
            banner.Visible = engine != null && engine.Mode == Modes.Live && !Program.IsAdmin;
            UpdateSetupBar();
        }

        // After the first read of this device: a deployment that runs here goes first, as it did before the welcome.
        void WelcomeAfterLoad(Engine e)
        {
            if (!WelcomeVisible || welcome.Mode != WelcomePage.Choices || e.Mode != Modes.Live) return;
            if (!UpdateGate.Deploying(e.S, DateTime.UtcNow)) return;
            HideWelcome();
            Status("A deployment runs on this device, so TrackVice follows it.");
        }

        void WelcomeChoice(string id)
        {
            if (id != "setup") Remember(WelcomeGate.SeenPref, "1");
            switch (id)
            {
                case "device":
                    if (engine == null || engine.Mode != Modes.Live) Begin(Settings.NewLive(), true);
                    else HideWelcome();
                    break;
                case "setup":
                    Remember(WelcomeGate.SeenPref, "1");
                    ShowWelcome(WelcomePage.Checklist);
                    break;
                case "open":
                    ContextMenuStrip m = Menus.New(theme);
                    Menus.Add(m, "Open a saved session…", delegate { OpenSession(); });
                    Menus.Add(m, "Open a folder of logs…", delegate { OpenFolder(); });
                    m.Show(Cursor.Position);
                    break;
                case "demo":
                    Begin(Engine.ForDemo("autopilot"), true);
                    Status("The Autopilot demo plays made-up data. The Open menu also has a task sequence demo, and Devices has demos of the device lists.");
                    break;
            }
        }

        // Opens the guide of a step. The checklist is read again when it closes, so a step that was done ticks itself off.
        void OpenStep(string id)
        {
            string a = SetupAudience ?? GettingStarted.Customers;
            switch (id)
            {
                case GettingStarted.Storage:
                    if (SetupFacts.StorageReady) { OpenSettings(); Status("Your storage is in Settings > Your storage."); }
                    else using (YourStorageDialog d = YourStorageDialog.ForLive(theme)) d.ShowDialog(this);
                    break;
                case GettingStarted.Customer:
                    OpenAddCustomer(false);
                    break;
                case GettingStarted.Test:
                    using (TestVmDialog d = new TestVmDialog(theme, a == GettingStarted.Own, TestCode()))
                    {
                        d.ShowDialog(this);
                        if (d.OpenList) OpenRemote(RemoteDialog.Agents);
                    }
                    break;
                case GettingStarted.Follow:
                    OpenRemote(RemoteDialog.Agents);
                    break;
            }
            if (WelcomeVisible && welcome.Mode == WelcomePage.Checklist) ShowChecklist(welcome.ChecklistAudience ?? a);
            UpdateSetupBar();
        }

        // The customer code for the test: the customer in use if it has one, else the first that has.
        string[] TestCode()
        {
            try
            {
                List<Customer> all = CustomerUi.Book.All;
                Customer cur = CustomerUi.Book.Current;
                if (cur != null) all.Insert(0, cur);
                foreach (Customer c in all)
                {
                    string code = CustomerCodes.Load(YourStorageUi.Secrets, c.Name);
                    if (code != null) return new string[] { c.Name, code };
                }
            }
            catch (Exception) { }
            return null;
        }

        // Called when a list of devices with the agent closes: it ticks off the last two steps.
        void SetupSawList(int rows)
        {
            Remember(GettingStarted.ListPref, "1");
            if (rows > 0) Remember(GettingStarted.UploadPref, "1");
            UpdateSetupBar();
        }

        void UpdateSetupBar()
        {
            string a = SetupAudience;
            List<SetupStep> steps = GettingStarted.Steps(a ?? GettingStarted.Customers, SetupFacts);
            bool show = !WelcomeVisible && GettingStarted.ShowBar(a, shotAudience == null && Prefs.GetBool(GettingStarted.HiddenPref, false), steps);
            if (!show) { if (setupBar != null) setupBar.Visible = false; return; }
            if (setupBar == null)
            {
                setupBar = new SetupBar();
                setupBar.Next.Click += Safe.Wrap(delegate
                {
                    SetupStep n = GettingStarted.Next(GettingStarted.Steps(SetupAudience ?? GettingStarted.Customers, SetupFacts));
                    if (n != null) OpenStep(n.Id);
                });
                setupBar.All.Click += Safe.Wrap(delegate { ShowWelcome(WelcomePage.Checklist); });
                setupBar.HideButton.Click += Safe.Wrap(delegate
                {
                    Remember(GettingStarted.HiddenPref, "1");
                    setupBar.Visible = false;
                    Status("Getting started is hidden. Help > Getting started shows it again.");
                });
                themed.Add(setupBar);
                Controls.Add(setupBar);
                Controls.SetChildIndex(setupBar, Controls.GetChildIndex(busyBar));
                setupBar.ApplyTheme(theme);
            }
            SetupStep next = GettingStarted.Next(steps);
            setupBar.Text2.Text = GettingStarted.BarText(steps);
            setupBar.Next.Text = next != null ? next.Action : "Next step";
            setupBar.Visible = true;
        }

        ContextMenuStrip HelpMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            Menus.Add(m, "Getting started", delegate { Remember(GettingStarted.HiddenPref, null); ShowWelcome(WelcomePage.Checklist); });
            Menus.Add(m, "Welcome", delegate { ShowWelcome(WelcomePage.Choices); });
            Menus.Add(m, "Get started page…", delegate { OpenGetStarted(); });
            Menus.Separator(m);
            Menus.Add(m, "The guide to the agent, on GitHub", delegate { OpenLink(AgentGuide); });
            Menus.Add(m, "TrackVice on GitHub", delegate { OpenLink(AppUpdate.ReleasesUrl.Replace("/releases", "")); });
            AddFeedbackItems(m);
            Menus.Separator(m);
            Menus.Add(m, "Command line", delegate { HelpPage(AboutDialog.CommandLinePart); });
            Menus.Add(m, "Keyboard shortcuts", delegate { HelpPage(AboutDialog.ShortcutsPart); });
            Menus.Add(m, "About TrackVice  (F1)", delegate { About(); });
            return m;
        }

        const string AgentGuide = "https://github.com/Baldissera88/TrackVice/blob/main/docs/AGENT.md";

        void OpenLink(string url)
        {
            // Only TrackVice's own https links are handed to Windows; anything else would be started as a program or a file.
            if (!LaunchGuard.IsWebLink(url)) { Status("Not opened: it is not a link TrackVice knows."); return; }
            try { Process.Start(url); }
            catch (Exception) { Status("Could not open " + url); }
        }

        // --page welcome, welcome-setup, gettingstarted (--select done for every step done), testvm.
        public static readonly string[] WelcomePages = { "welcome", "welcome-setup", "gettingstarted", "gettingstarted-own", "setupbar", "testvm" };

        Form ShotWelcome()
        {
            switch (start.Page)
            {
                case "welcome": ShowWelcome(WelcomePage.Choices); return null;
                case "welcome-setup": ShowWelcome(WelcomePage.Audience); return null;
                case "gettingstarted":
                case "gettingstarted-own":
                    setupFacts = start.Select == "done" ? new FixedSetupFacts(true, true, true, true) : new FixedSetupFacts(true, true, false, false);
                    ShowWelcome(WelcomePage.Choices);
                    ShowChecklist(start.Page == "gettingstarted-own" ? GettingStarted.Own : GettingStarted.Customers);
                    return null;
                case "setupbar":
                    setupFacts = new FixedSetupFacts(true, false, false, false);
                    shotAudience = GettingStarted.Customers;
                    UpdateSetupBar();
                    return null;
                default:
                    return new TestVmDialog(theme, false, new string[] { "Contoso", "tv1:made-up-code-for-the-demo" });
            }
        }
    }
}
