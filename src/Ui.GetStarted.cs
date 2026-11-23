namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // The top of the Get started page: what it is and how far along the setup is.
    public sealed class GetStartedHead : FlatControl, IMeasured
    {
        public int Done, Total;
        public string Progress = "";

        public GetStartedHead()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        const string Intro = "The steps to follow devices from this PC, in order. Each button opens the window for that step. A step shows Done when TrackVice finds its result on this PC.";

        public int Measure(int width)
        {
            return Fonts.Title.Height + Dpi.S(6) + Draw.WrapHeight(Intro, Fonts.Ui, width) + Dpi.S(14) + Fonts.Ui.Height + Dpi.S(6) + Dpi.S(8) + Dpi.S(6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Draw.Text(g, "Get started", Fonts.Title, new Rectangle(0, 0, Width, Fonts.Title.Height + 2), T.Text, Draw.Line);
            int y = Fonts.Title.Height + Dpi.S(6);
            int ih = Draw.WrapHeight(Intro, Fonts.Ui, Width);
            Draw.Text(g, Intro, Fonts.Ui, new Rectangle(0, y, Width, ih + 2), T.Text2, Draw.Wrap);
            y += ih + Dpi.S(14);
            Draw.Text(g, Progress, Fonts.UiSemibold, new Rectangle(0, y, Width, Fonts.Ui.Height + 2), T.Text, Draw.Line);
            y += Fonts.Ui.Height + Dpi.S(6);
            RectangleF track = new RectangleF(0, y, Width - 1, Dpi.S(8));
            Shapes.Fill(g, track, track.Height / 2f, T.Divider);
            if (Total > 0 && Done > 0)
            {
                RectangleF fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * Done / Total), track.Height);
                Shapes.Fill(g, fill, track.Height / 2f, Done == Total ? T.OkFg : T.Accent);
            }
        }
    }

    // One step: a number or tick, the title with its status, what it makes and why, and one button.
    public sealed class GetStartedCard : FlatControl, IMeasured
    {
        public readonly GetStartedStep Step;
        public readonly int Number;
        public readonly FlatButton Button;
        public event EventHandler NeedsToggled;
        bool next, open, linkHot;
        Rectangle link;

        public GetStartedCard(int number, GetStartedStep step, bool isNext)
        {
            Number = number;
            Step = step;
            next = isNext;
            Button = new FlatButton(step.Button, null);
            Button.Primary = isNext;
            Button.Outline = !isNext;
            Button.AutoWidth();
            Button.Width = Math.Max(Button.Width, Dpi.S(150));
            Controls.Add(Button);
        }

        const int TextX = 72;

        bool Under(int width)
        {
            return width - Dpi.S(TextX) - Button.Width - Dpi.S(40) < Dpi.S(300);
        }

        int TextW(int width)
        {
            return Math.Max(Dpi.S(120), Under(width) ? width - Dpi.S(TextX) - Dpi.S(20) : width - Dpi.S(TextX) - Button.Width - Dpi.S(40));
        }

        string NeedsText
        {
            get
            {
                string s = "";
                foreach (string n in Step.Needs) s += (s.Length > 0 ? "\n" : "") + "– " + n;
                return s;
            }
        }

        int BodyHeight(int width)
        {
            int tw = TextW(width);
            int h = Fonts.UiSemibold.Height + Dpi.S(4) + Draw.WrapHeight(Step.Text, Fonts.Ui, tw);
            if (!string.IsNullOrEmpty(Step.Reason)) h += Dpi.S(4) + Draw.WrapHeight(Step.Reason, Fonts.Ui, tw);
            if (Step.Needs.Count > 0)
            {
                h += Dpi.S(6) + Fonts.Small.Height;
                if (open) h += Dpi.S(4) + Draw.WrapHeight(NeedsText, Fonts.Small, tw);
            }
            return h;
        }

        public int Measure(int width)
        {
            int h = Dpi.S(16) + BodyHeight(width) + Dpi.S(16);
            if (Under(width)) h += Dpi.S(10) + Button.Height;
            return Math.Max(Dpi.S(76), h);
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            BackColor = t.Card;
            Button.ApplyTheme(t);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            // Called once from the base constructor, before the button is made.
            if (Button == null) return;
            if (Under(Width)) Button.Location = new Point(Dpi.S(TextX), Height - Dpi.S(16) - Button.Height);
            else Button.Location = new Point(Width - Dpi.S(20) - Button.Width, (Height - Button.Height) / 2);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hot = Step.Needs.Count > 0 && link.Contains(e.Location);
            if (hot != linkHot) { linkHot = hot; Cursor = hot ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (linkHot) { linkHot = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (Step.Needs.Count > 0 && link.Contains(e.Location))
            {
                open = !open;
                if (NeedsToggled != null) NeedsToggled(this, EventArgs.Empty);
            }
            base.OnMouseClick(e);
        }

        Color StatusColor
        {
            get
            {
                switch (Step.Status)
                {
                    case StepStatus.Done: return T.OkFg;
                    case StepStatus.Attention: return T.ErrFg;
                    case StepStatus.Check: return T.WarnFg;
                    default: return T.Text3;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Draw.Card(g, ClientRectangle, T);
            if (next) Shapes.Stroke(g, new RectangleF(1f, 1f, Width - 2.5f, Height - 2.5f), Dpi.S(7), T.Accent, 1.5f);
            bool done = Step.IsDone;
            int d = Dpi.S(30);
            RectangleF c = new RectangleF(Dpi.S(22), Dpi.S(14), d, d);
            Color tone = done ? T.OkFg : Step.Status == StepStatus.ToDo ? T.Accent : StatusColor;
            if (done)
            {
                Shapes.Fill(g, c, d / 2f, T.OkFg);
                using (Pen p = new Pen(T.Card, Dpi.S(2.5f)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawLines(p, new PointF[] { new PointF(c.X + d * 0.28f, c.Y + d * 0.52f), new PointF(c.X + d * 0.44f, c.Y + d * 0.68f), new PointF(c.X + d * 0.73f, c.Y + d * 0.36f) });
                }
            }
            else
            {
                Shapes.Stroke(g, c, d / 2f, tone, 1.5f);
                Draw.Text(g, Number.ToString(), Fonts.UiSemibold, Rectangle.Round(c), tone, Draw.Line | TextFormatFlags.HorizontalCenter);
            }
            int x = Dpi.S(TextX), tw = TextW(Width);
            int y = Dpi.S(16);
            int th = Fonts.UiSemibold.Height;
            Draw.Text(g, Step.Title, Fonts.UiSemibold, new Rectangle(x, y, tw, th + 2), done ? T.Text2 : T.Text, Draw.Line);
            string status = GetStarted.StatusText(Step.Status);
            int w = Math.Min(tw - Dpi.S(60), Draw.Measure(Step.Title, Fonts.UiSemibold).Width) + Dpi.S(10);
            if (w + Dpi.S(60) <= tw) Draw.Text(g, status, Fonts.Small, new Rectangle(x + w, y, tw - w, th + 2), StatusColor, Draw.Line);
            else Draw.Text(g, status, Fonts.Small, new Rectangle(x, y + th + 2, tw, Fonts.Small.Height + 2), StatusColor, Draw.Line);
            y += th + Dpi.S(4);
            int dh = Draw.WrapHeight(Step.Text, Fonts.Ui, tw);
            Draw.Text(g, Step.Text, Fonts.Ui, new Rectangle(x, y, tw, dh + 2), done ? T.Text3 : T.Text2, Draw.Wrap);
            y += dh;
            if (!string.IsNullOrEmpty(Step.Reason))
            {
                y += Dpi.S(4);
                int rh = Draw.WrapHeight(Step.Reason, Fonts.Ui, tw);
                Draw.Text(g, Step.Reason, Fonts.Ui, new Rectangle(x, y, tw, rh + 2), StatusColor, Draw.Wrap);
                y += rh;
            }
            if (Step.Needs.Count > 0)
            {
                y += Dpi.S(6);
                if (open)
                {
                    int nh = Draw.WrapHeight(NeedsText, Fonts.Small, tw);
                    Draw.Text(g, NeedsText, Fonts.Small, new Rectangle(x, y + Fonts.Small.Height + Dpi.S(4), tw, nh + 2), T.Text2, Draw.Wrap);
                }
                string label = open ? "Hide" : "What do I need?";
                Font f = linkHot ? new Font(Fonts.Small, FontStyle.Underline) : Fonts.Small;
                try
                {
                    link = new Rectangle(x, y, Draw.Measure(label, f).Width + Dpi.S(4), Fonts.Small.Height);
                    Draw.Text(g, label, f, link, T.Accent, Draw.Line);
                }
                finally { if (linkHot) f.Dispose(); }
            }
        }
    }

    // A square with a label that is ticked or not.
    public sealed class TickBox : FlatControl
    {
        public bool On;
        public event EventHandler Changed;

        public TickBox(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            TabStop = true;
            Width = Dpi.S(26) + Draw.Measure(text, Fonts.Ui).Width + Dpi.S(8);
        }

        protected override void OnClick(EventArgs e)
        {
            On = !On;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            int d = Dpi.S(16);
            RectangleF b = new RectangleF(0.5f, (Height - d) / 2f, d, d);
            if (On) Shapes.Fill(g, b, Dpi.S(3), T.Accent);
            Shapes.Stroke(g, b, Dpi.S(3), On || (Focused && ShowFocusCues) ? T.Accent : T.FieldStroke, 1.2f);
            if (On)
            {
                using (Pen p = new Pen(T.OnAccent, Dpi.S(1.8f)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawLines(p, new PointF[] { new PointF(b.X + d * 0.25f, b.Y + d * 0.52f), new PointF(b.X + d * 0.43f, b.Y + d * 0.70f), new PointF(b.X + d * 0.76f, b.Y + d * 0.30f) });
                }
            }
            Draw.Text(g, Text, Fonts.Ui, new Rectangle(Dpi.S(26), 0, Width - Dpi.S(26), Height), T.Text2, Draw.Line);
        }
    }

    // The Get started page. A button sets Chosen and closes it, so the window for that step opens on its own.
    // The guide step does not close it: it asks Link to open the guide.
    public sealed partial class GetStartedDialog : PageForm
    {
        public string Chosen;
        public readonly TickBox NotAgain;
        public event Action<string> Guide;
        readonly List<GetStartedCard> cards = new List<GetStartedCard>();

        public GetStartedDialog(Theme t, List<GetStartedStep> steps, bool hidden) : base("Get started", t)
        {
            Size = new Size(Dpi.S(860), Dpi.S(760));
            GetStartedHead head = new GetStartedHead();
            head.Done = GetStarted.DoneCount(steps);
            head.Total = steps.Count;
            head.Progress = GetStarted.Progress(steps);
            Add(head);
            GetStartedStep next = GetStarted.Next(steps);
            for (int i = 0; i < steps.Count; i++)
            {
                GetStartedStep s = steps[i];
                GetStartedCard c = new GetStartedCard(i + 1, s, next != null && next.Id == s.Id);
                string id = s.Id;
                c.Button.Click += Safe.Wrap(delegate { Pick(id); });
                c.NeedsToggled += delegate { Relayout(); };
                cards.Add(c);
                Add(c);
                if (i == 0) AddVaultLink(s);
            }
            NotAgain = new TickBox("Do not show again");
            NotAgain.On = hidden;
            NotAgain.Dock = DockStyle.Left;
            NotAgain.ApplyTheme(t);
            footer.Controls.Add(NotAgain);
            // The one button is the way out when every step is done.
            if (next == null) bClose.Primary = true;
            else bClose.Primary = false;
            bClose.ApplyTheme(t);
        }

        void Pick(string id)
        {
            if (id == GetStarted.Test)
            {
                if (Guide != null) Guide(id);
                return;
            }
            Chosen = id;
            Close();
        }
    }

    public sealed partial class MainForm
    {
        // Screenshots show made-up progress instead of what this PC has.
        GetStartedState shotGetStarted;

        GetStartedState ReadGetStarted()
        {
            if (shotGetStarted != null) return shotGetStarted;
            GetStartedState s = new GetStartedState();
            try
            {
                OwnStorage o = YourStorageUi.Load();
                if (o != null)
                {
                    s.StorageSet = true;
                    s.StorageExpired = o.ExpiresUtc != default(DateTime) && o.ExpiresUtc < DateTime.UtcNow;
                    s.PublishedKnown = !string.IsNullOrEmpty(o.PublishedVersion);
                    s.PublishedOlder = s.PublishedKnown && OwnPublish.Needed(o, o.PublishedVersion, YourStorageUi.RunningVersion);
                }
            }
            catch (Exception) { }
            try
            {
                foreach (Customer c in CustomerUi.Book.All)
                {
                    s.Customers++;
                    if (!string.IsNullOrEmpty(c.AgentLocation)) s.CustomersWithLocation++;
                    if (CustomerCodes.Load(YourStorageUi.Secrets, c.Name) != null) s.ScriptMade = true;
                }
            }
            catch (Exception) { }
            s.TaskSequenceAdded = !string.IsNullOrEmpty(Prefs.Get("TsAgentSource", ""));
            s.UploadSeen = Prefs.GetBool(GettingStarted.UploadPref, false);
            s.SccmSide = DevicesSide == DeviceSides.Sccm;
            s.Hidden = Prefs.GetBool(GetStarted.HiddenPref, false);
            s.AutoShown = Prefs.GetBool(GetStarted.ShownPref, false);
            return s;
        }

        // Devices > Get started and Help > Get started. It opens again after each step, so the status is read anew.
        void OpenGetStarted()
        {
            while (true)
            {
                GetStartedState state = ReadGetStarted();
                string id;
                using (GetStartedDialog d = new GetStartedDialog(theme, GetStarted.Steps(state), state.Hidden))
                {
                    d.Guide += delegate { OpenLink(AgentGuide + "#test-on-one-device-first"); };
                    d.ShowDialog(this);
                    if (d.NotAgain.On != state.Hidden) Remember(GetStarted.HiddenPref, d.NotAgain.On ? "1" : null);
                    id = d.Chosen;
                }
                if (id == null || shotGetStarted != null) return;
                if (!RunGetStartedStep(id)) return;
            }
        }

        // Opens the window of a step. False when the page should not open again, as when a publish runs.
        bool RunGetStartedStep(string id)
        {
            switch (id)
            {
                case GetStarted.Storage:
                    if (YourStorageUi.Load() != null) OpenSettings();
                    else using (YourStorageDialog d = YourStorageDialog.ForLive(theme)) d.ShowDialog(this);
                    break;
                case GetStarted.Publish:
                    if (YourStorageUi.Load() == null)
                    {
                        using (YourStorageDialog d = YourStorageDialog.ForLive(theme)) d.ShowDialog(this);
                        if (YourStorageUi.Load() == null) break;
                    }
                    Status("Publishing TrackVice " + YourStorageUi.RunningVersion + " to your storage...");
                    YourStorageUi.PublishRunning(this, delegate (string msg, bool ok)
                    {
                        Status(msg);
                        OpenGetStarted();
                    });
                    return false;
                case VaultSetupUi.GetStartedId:
                    Status(VaultSetupUi.RunGet(this, YourStorageUi.Keys, YourStorageUi.Secrets));
                    break;
                case GetStarted.Customer:
                    OpenAddCustomer(false);
                    break;
                case GetStarted.Script:
                    if (DevicesSide == DeviceSides.Sccm) { OpenTsAgent(); break; }
                    Customer withCode = null;
                    Customer cur = CustomerUi.Book.Current;
                    List<Customer> all = CustomerUi.Book.All;
                    if (cur != null) all.Insert(0, cur);
                    foreach (Customer c in all) if (CustomerCodeDialog.Has(c.Name)) { withCode = c; break; }
                    if (withCode != null) CustomerCodeDialog.Open(this, theme, withCode);
                    else Status("No customer code is kept on this PC. Add a customer first: step 3 makes the code and the script.");
                    break;
            }
            UpdateSetupBar();
            return true;
        }

        // At start, when nothing is set up: opens the page once. Not when the welcome page shows, which has its own list.
        void MaybeGetStarted(bool forSomething, string welcomeReason)
        {
            if (start.Shot != null || forSomething || welcomeReason != "Seen before.") return;
            if (!GetStarted.AutoShow(ReadGetStarted())) return;
            Remember(GetStarted.ShownPref, "1");
            BeginInvoke((MethodInvoker)delegate { Safe.Run(OpenGetStarted); });
        }

        // --page getstarted and getstarted-done, with made-up progress. --select attention shows a step that needs attention.
        public static readonly string[] GetStartedPages = { "getstarted", "getstarted-done" };

        Form ShotGetStarted()
        {
            GetStartedState s = new GetStartedState();
            s.StorageSet = s.PublishedKnown = start.Select != "vault";
            if (start.Select == "vault") VaultSetupUi.DemoAddress = true;
            if (start.Page == "getstarted-done")
            {
                s.Customers = s.CustomersWithLocation = 1;
                s.ScriptMade = s.UploadSeen = true;
            }
            else if (start.Select == "attention") s.Customers = 1;
            shotGetStarted = s;
            return new GetStartedDialog(theme, GetStarted.Steps(s), false);
        }
    }
}
