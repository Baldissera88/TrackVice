namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Windows.Forms;

    // A card with a title, a short description and a text box to type in.
    public sealed class FieldBox : Panel, IThemed, IMeasured
    {
        Theme theme = Theme.Light();
        readonly string title, desc;
        readonly int lines;
        public readonly TextBox Box = new TextBox();

        public FieldBox(string title, string desc, int lines)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            this.title = title;
            this.desc = desc;
            this.lines = Math.Max(1, lines);
            Box.Multiline = this.lines > 1;
            Box.AcceptsReturn = this.lines > 1;
            Box.ScrollBars = this.lines > 1 ? ScrollBars.Vertical : ScrollBars.None;
            Box.BorderStyle = BorderStyle.FixedSingle;
            Box.Font = Fonts.Ui;
            Controls.Add(Box);
        }

        public string Value
        {
            get { return Box.Text.Trim(); }
        }

        int TextW(int width)
        {
            return Math.Max(Dpi.S(80), width - Dpi.S(40));
        }

        int BoxTop(int width)
        {
            int y = Dpi.S(12) + Fonts.Ui.Height;
            if (!string.IsNullOrEmpty(desc)) y += Dpi.S(2) + Draw.WrapHeight(desc, Fonts.Small, TextW(width));
            return y + Dpi.S(8);
        }

        int BoxH
        {
            get { return lines > 1 ? Fonts.Ui.Height * lines + Dpi.S(8) : Fonts.Ui.Height + Dpi.S(8); }
        }

        public int Measure(int width)
        {
            return BoxTop(width) + BoxH + Dpi.S(14);
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Card;
            Box.BackColor = t.Field;
            Box.ForeColor = t.Text;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            Box.SetBounds(Dpi.S(20), BoxTop(Width), TextW(Width), BoxH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : theme.Bg);
            Draw.Card(g, ClientRectangle, theme);
            int y = Dpi.S(12);
            Draw.Text(g, title, Fonts.Ui, new Rectangle(Dpi.S(20), y, TextW(Width), Fonts.Ui.Height), theme.Text, Draw.Line);
            y += Fonts.Ui.Height + Dpi.S(2);
            if (!string.IsNullOrEmpty(desc))
            {
                int dh = Draw.WrapHeight(desc, Fonts.Small, TextW(Width));
                Draw.Text(g, desc, Fonts.Small, new Rectangle(Dpi.S(20), y, TextW(Width), dh + 2), theme.Text2, Draw.Wrap);
            }
        }
    }

    // A card that lists fields and their values: what goes along with a report.
    public sealed class FieldList : FlatControl, IMeasured
    {
        List<FeedbackField> rows = new List<FeedbackField>();

        public FieldList()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public void SetRows(List<FeedbackField> list)
        {
            rows = list ?? new List<FeedbackField>();
            Invalidate();
        }

        int LabelW(int width)
        {
            int w = 0;
            foreach (FeedbackField f in rows) w = Math.Max(w, Draw.Measure(f.Label, Fonts.SmallSemibold).Width + Dpi.S(20));
            return Math.Min(w, width / 3);
        }

        static Font ValueFont(FeedbackField f)
        {
            return f.Id == "errors" ? Fonts.MonoSmall : Fonts.Ui;
        }

        int RowH(FeedbackField f, int width)
        {
            int vw = Math.Max(Dpi.S(60), width - Dpi.S(40) - LabelW(width));
            return Math.Max(Fonts.Ui.Height, Draw.WrapHeight(f.Value.Length > 0 ? f.Value : "-", ValueFont(f), vw)) + Dpi.S(10);
        }

        public int Measure(int width)
        {
            int h = Dpi.S(16);
            foreach (FeedbackField f in rows) h += RowH(f, width);
            return h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            Draw.Card(g, ClientRectangle, T);
            int lw = LabelW(Width);
            int vx = Dpi.S(20) + lw;
            int vw = Math.Max(Dpi.S(60), Width - vx - Dpi.S(20));
            int y = Dpi.S(8);
            foreach (FeedbackField f in rows)
            {
                int h = RowH(f, Width);
                Draw.Text(g, f.Label, Fonts.SmallSemibold, new Rectangle(Dpi.S(20), y + Dpi.S(5), lw - Dpi.S(8), Fonts.Ui.Height), T.Text2, Draw.Line);
                string v = f.Value.Length > 0 ? f.Value : "-";
                Draw.Text(g, v, ValueFont(f), new Rectangle(vx, y + Dpi.S(5), vw, h), T.Text, Draw.Wrap);
                y += h;
            }
        }
    }

    // The way a published version goes: this PC, your storage's trackvice-agent, the devices. Drawn in the theme's colours.
    public sealed class PublishDrawing : FlatControl, IMeasured
    {
        public string Running = "", Published = "";

        public PublishDrawing()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public int Measure(int width)
        {
            return Dpi.S(186);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            Draw.Card(g, ClientRectangle, T);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Dpi.S(20), gap = Dpi.S(84);
            int bw = Math.Max(Dpi.S(120), (Width - pad * 2 - gap * 2) / 3);
            int top = Dpi.S(40), bh = Height - top - Dpi.S(20);
            Rectangle a = new Rectangle(pad, top, bw, bh);
            Rectangle b = new Rectangle(a.Right + gap, top, bw, bh);
            Rectangle c = new Rectangle(b.Right + gap, top, bw, bh);
            Box(g, a, Glyphs2.Device, "This PC", new string[] { "TrackVice " + Running, "the update key" }, false);
            Box(g, b, Glyphs2.Folder, "Your storage", new string[] { Arm.AgentFiles, "agent/latest.json, signed", "agent/TrackVice-" + Running + ".exe" }, true);
            Box(g, c, Glyphs2.Apps, "Devices", new string[] { "at your customers", "New: at enrollment", "Installed agents: within " + AgentUpdate.Every.TotalHours.ToString(CultureInfo.InvariantCulture) + " hours" }, false);
            Arrow(g, a, b, "Publish");
            Arrow(g, b, c, "Read");
            string head = Published.Length > 0 ? "Published now: " + Published + ". Publish sends " + Running + "." : "Nothing is published yet. Publish sends " + Running + ".";
            Draw.Text(g, head, Fonts.Small, new Rectangle(pad, Dpi.S(10), Width - pad * 2, Fonts.Small.Height + 4), T.Text2, Draw.Line);
        }

        void Box(Graphics g, Rectangle r, string glyph, string title, string[] lines, bool accent)
        {
            RectangleF f = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            Shapes.Fill(g, f, Dpi.S(6), accent ? T.AccentSoft : T.CardAlt);
            Shapes.Stroke(g, f, Dpi.S(6), accent ? T.Accent : T.Divider, accent ? 1.5f : 1f);
            int x = r.X + Dpi.S(12), y = r.Y + Dpi.S(10);
            int tx = x;
            if (Fonts.Icons != null)
            {
                Draw.Glyph(g, glyph, new Rectangle(x, y, Dpi.S(20), Fonts.UiSemibold.Height), accent ? T.Accent : T.Text2);
                tx += Dpi.S(26);
            }
            Draw.Text(g, title, Fonts.UiSemibold, new Rectangle(tx, y, r.Right - tx - Dpi.S(8), Fonts.UiSemibold.Height), T.Text, Draw.Line);
            y += Fonts.UiSemibold.Height + Dpi.S(8);
            for (int i = 0; i < lines.Length; i++)
            {
                Font font = Fonts.Small;
                Draw.Text(g, lines[i], font, new Rectangle(x, y, r.Width - Dpi.S(20), font.Height + 2), i == 0 ? T.Text : T.Text2, Draw.Line);
                y += font.Height + Dpi.S(4);
            }
        }

        void Arrow(Graphics g, Rectangle from, Rectangle to, string label)
        {
            int y = from.Y + from.Height / 2 + Dpi.S(6);
            int x1 = from.Right + Dpi.S(6), x2 = to.Left - Dpi.S(6);
            using (Pen p = new Pen(T.Accent, Dpi.S(2)))
            {
                p.CustomEndCap = new AdjustableArrowCap(4, 4);
                g.DrawLine(p, x1, y, x2, y);
            }
            Draw.Text(g, label, Fonts.SmallSemibold, new Rectangle(from.Right, y - Fonts.SmallSemibold.Height - Dpi.S(8), to.Left - from.Right, Fonts.SmallSemibold.Height + 2), T.Accent, Draw.Line | TextFormatFlags.HorizontalCenter);
        }
    }

    // What does Publish do? The drawing and the same text as docs/AGENT.md.
    public sealed class PublishExplainDialog : PageForm
    {
        public PublishExplainDialog(Theme t, string running, string published) : base(PublishBanner.ExplainTitle, t)
        {
            Size = new Size(Dpi.S(820), Dpi.S(760));
            PublishDrawing d = new PublishDrawing();
            d.Running = running ?? "";
            d.Published = published ?? "";
            Add(d);
            string[] glyphs = { Glyphs2.Sync, Glyphs2.Apps, Glyphs.Shield, Glyphs2.Back };
            string[][] s = PublishExplain.Sections();
            for (int i = 0; i < s.Length; i++) Add(new SettingRow(glyphs[i % glyphs.Length], s[i][0], s[i][1], null));
            Add(new SettingRow(Glyphs.Info, "It never publishes by itself", "Only Publish in the banner, the status bar or Settings > Your storage publishes, and only when you click it.", null));
        }

        // A small button for next to a Publish button.
        public static FlatButton Button(Theme theme)
        {
            FlatButton b = new FlatButton(PublishBanner.ExplainTitle, Glyphs2.Help);
            b.Compact = true;
            b.AutoWidth();
            b.ApplyTheme(theme);
            b.Click += Safe.Wrap(delegate { Open(b); });
            return b;
        }

        public static void Open(Control from)
        {
            Form f = from != null ? from.FindForm() : null;
            PageForm page = f as PageForm;
            MainForm main = f as MainForm;
            Theme t = page != null ? page.CurrentTheme : main != null ? main.CurrentTheme : Theme.Named(Theme.SavedName());
            OwnStorage o = YourStorageUi.Load();
            using (PublishExplainDialog d = new PublishExplainDialog(t, YourStorageUi.RunningVersion, o != null ? o.PublishedVersion : "")) d.ShowDialog(f);
        }
    }

    // At the top of the main window after a newer TrackVice starts than the one in your storage. Publishes only when clicked.
    public sealed class PublishBannerBar : Panel, IThemed
    {
        public readonly Label Text2 = new Label();
        public readonly FlatButton PublishButton = new FlatButton("Publish", null);
        public readonly FlatButton ExplainButton = new FlatButton(PublishBanner.ExplainTitle, null);
        public readonly FlatButton LaterButton = new FlatButton("Later", null);

        public PublishBannerBar()
        {
            Dock = DockStyle.Top;
            Height = Dpi.S(44);
            Padding = new Padding(Dpi.S(20), Dpi.S(6), Dpi.S(20), Dpi.S(6));
            Text2.AutoSize = false;
            Text2.TextAlign = ContentAlignment.MiddleLeft;
            Text2.UseMnemonic = false;
            PublishButton.Primary = true;
            Controls.Add(Text2);
            foreach (FlatButton b in Buttons())
            {
                b.Compact = true;
                b.AutoWidth();
                Controls.Add(b);
            }
        }

        // Left to right, as they show.
        FlatButton[] Buttons()
        {
            return new FlatButton[] { PublishButton, ExplainButton, LaterButton };
        }

        bool arranging, publishShown = true;

        // The text wraps, and the buttons go below it when the window is too narrow for both on one line.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (arranging) return;
            arranging = true;
            try
            {
                List<FlatButton> shown = new List<FlatButton>();
                foreach (FlatButton b in Buttons()) if (b != PublishButton || publishShown) shown.Add(b);
                int[] widths = new int[shown.Count];
                for (int i = 0; i < widths.Length; i++) widths[i] = shown[i].Width;
                int avail = Math.Max(Dpi.S(40), ClientSize.Width - Padding.Horizontal);
                int gap = Dpi.S(6);
                BannerFlow f = BannerFlow.Arrange(avail, widths, gap, Dpi.S(280));
                Size need = TextRenderer.MeasureText(Text2.Text ?? "", Text2.Font, new Size(f.TextWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                int bh = Dpi.S(32);
                foreach (FlatButton b in shown) bh = Math.Max(bh, b.Height);
                int th = need.Height;
                int top = Padding.Top;
                int h;
                if (f.OneRow)
                {
                    int line = Math.Max(th, bh);
                    Text2.Bounds = new Rectangle(Padding.Left, top, f.TextWidth, line);
                    for (int i = 0; i < shown.Count; i++) shown[i].Bounds = new Rectangle(Padding.Left + f.X[i], top + (line - bh) / 2, widths[i], bh);
                    h = top + line + Padding.Bottom;
                }
                else
                {
                    Text2.Bounds = new Rectangle(Padding.Left, top, f.TextWidth, th + Dpi.S(4));
                    int y = top + th + Dpi.S(8);
                    for (int i = 0; i < shown.Count; i++) shown[i].Bounds = new Rectangle(Padding.Left + f.X[i], y + f.Row[i] * (bh + gap), Math.Min(widths[i], avail), bh);
                    h = y + f.Rows * (bh + gap) - gap + Padding.Bottom;
                }
                if (Height != h) Height = h;
            }
            finally { arranging = false; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PerformLayout();
        }

        public void Say(string text, bool canPublish, string closeText)
        {
            Text2.Text = text;
            PublishButton.Visible = canPublish;
            publishShown = canPublish;
            PublishButton.Enabled = true;
            ExplainButton.Visible = true;
            LaterButton.Text = closeText;
            LaterButton.AutoWidth();
            PerformLayout();
        }

        public void Working(string text)
        {
            Text2.Text = text;
            PublishButton.Enabled = false;
            PerformLayout();
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.AccentSoft;
            foreach (Control c in Controls) if (!(c is FlatButton)) c.BackColor = t.AccentSoft;
            Text2.ForeColor = t.Text;
            PublishButton.ApplyTheme(t);
            ExplainButton.ApplyTheme(t);
            LaterButton.ApplyTheme(t);
        }
    }

    // A short line with a link at the right of the status bar, such as "Report a problem" after an error.
    public sealed class LinkNote : FlatControl
    {
        readonly string action;
        public event EventHandler ActionClicked;

        public LinkNote(string action)
        {
            this.action = action;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        public void Say(string text)
        {
            Text = text;
            Width = TextRenderer.MeasureText(Text, Fonts.Ui).Width + TextRenderer.MeasureText(action, Fonts.UiSemibold).Width + Dpi.S(32);
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (ActionClicked != null) ActionClicked(this, e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            int tw = TextRenderer.MeasureText(Text, Fonts.Ui).Width;
            int x = Dpi.S(16);
            Draw.Text(g, Text, Fonts.Ui, new Rectangle(x, 0, tw + 2, Height), T.Text2, Draw.Line);
            Font f = hover ? new Font(Fonts.UiSemibold, FontStyle.Underline) : Fonts.UiSemibold;
            try { Draw.Text(g, action, f, new Rectangle(x + tw + Dpi.S(8), 0, Width - x - tw - Dpi.S(8), Height), T.Accent, Draw.Line); }
            finally { if (hover) f.Dispose(); }
        }
    }

    // The pages under Help that open a GitHub issue, a discussion or an e-mail, filled in with what is on the page.
    public abstract class FeedbackPage : PageForm
    {
        readonly SettingRow result;

        protected FeedbackPage(string title, Theme t) : base(title, t)
        {
            Size = new Size(Dpi.S(800), Dpi.S(720));
            result = new SettingRow(Glyphs.Info, "Nothing has left this PC", "", null);
        }

        protected void AddResultRow()
        {
            Add(result);
        }

        protected void Said(string title, string text)
        {
            result.Title = title;
            result.Desc = text;
            result.Invalidate();
            Relayout();
        }

        // Docked from the right, after Close: the first one given ends up next to Close.
        protected FlatButton Footer(string text, bool primary, EventHandler click)
        {
            FlatButton b = new FlatButton(text, null);
            b.Primary = primary;
            b.AutoWidth();
            b.Width += Dpi.S(8);
            b.Dock = DockStyle.Right;
            b.Click += Safe.Wrap(click);
            b.ApplyTheme(theme);
            Panel gap = new Panel();
            gap.Width = Dpi.S(8);
            gap.Dock = DockStyle.Right;
            footer.Controls.Add(gap);
            footer.Controls.Add(b);
            gap.BringToFront();
            b.BringToFront();
            return b;
        }

        protected void Secondary()
        {
            bClose.Primary = false;
            bClose.ApplyTheme(theme);
        }

        // Opens the link in the browser or mail program. When the text was cut to fit, the whole text goes on the clipboard.
        protected void OpenLink(string url, string where, string fullText)
        {
            bool cut = url.IndexOf(Feedback.Escape(Feedback.CutNote), StringComparison.Ordinal) >= 0;
            try { Process.Start(url); }
            catch (Exception)
            {
                bool copied = Menus.Copy(fullText);
                Said("Could not open " + where, copied ? "The text is on the clipboard. Paste it into an e-mail to " + Feedback.Email + " or a new issue on " + Feedback.RepoUrl + "." : "Write to " + Feedback.Email + ".");
                return;
            }
            if (cut && Menus.Copy(fullText)) Said("Opened " + where, "Some text was too long for the link and was cut. The whole text is on the clipboard: paste the rest in yourself. You send it; TrackVice sent nothing.");
            else Said("Opened " + where, "Look it through and send it there. TrackVice sent nothing.");
        }

        protected void OpenDiscussion(string text)
        {
            bool copied = Menus.Copy(text);
            try { Process.Start(Feedback.Discussions + "/new/choose"); }
            catch (Exception) { Said("Could not open GitHub Discussions", Feedback.Discussions); return; }
            Said("Opened GitHub Discussions", copied ? "The text is on the clipboard. Pick a category and paste it." : "Pick a category and write there.");
        }
    }

    // Help > Report a problem.
    public sealed class ProblemDialog : FeedbackPage
    {
        readonly FieldBox fWhat, fExpected, fSteps;
        readonly ProblemReport filled;
        readonly Func<Session> session;
        readonly SettingRow bundleRow;

        // session gives the session for the log bundle, or null when there is none to save.
        public ProblemDialog(Theme t, ProblemReport report, Func<Session> session) : base("Report a problem", t)
        {
            filled = report;
            this.session = session;
            Add(new SettingRow(Glyphs.Shield, "TrackVice sends nothing by itself", "The buttons open a GitHub issue in your browser, or an e-mail in your mail program, filled in with what is on this page. You look it through and send it. TrackVice adds no customer names, tenants, device names or links: in its errors they are replaced with <name>, <id> and <link>. They only go along if you type them in.", null));
            Add(new SectionTitle("In your words"));
            fWhat = new FieldBox("What happened", "What TrackVice did, or the message it showed.", 4);
            fExpected = new FieldBox("What you expected", null, 3);
            fSteps = new FieldBox("Steps to repeat it", "For example: Devices > Intune / cloud, sign in, open a device.", 4);
            Add(fWhat);
            Add(fExpected);
            Add(fSteps);
            Add(new SectionTitle("Filled in by TrackVice, and sent along"));
            FieldList list = new FieldList();
            list.SetRows(report.Filled());
            Add(list);
            Add(new SectionTitle("Log bundle, optional"));
            FlatButton bSave = SmallButton("Save a log bundle", delegate { SaveBundle(); });
            bSave.Enabled = session != null;
            bundleRow = new SettingRow(Glyphs2.Folder, "A zip of the session and the logs it read", session != null
                ? "Passwords, keys and tokens in the session are masked as the agent masks them; the log files are as they are on the device. Look it through, and attach it to the issue or e-mail yourself. It is never attached for you."
                : "There is no session to save for what is open now.", bSave);
            Add(bundleRow);
            AddResultRow();
            Secondary();
            Footer("Open an e-mail", false, delegate { ProblemReport r = Report(); OpenLink(r.MailUrl(), "an e-mail to " + Feedback.Email, Feedback.Body(r.Fields())); });
            Footer("Open a GitHub issue", true, delegate { ProblemReport r = Report(); OpenLink(r.IssueUrl(), "a new issue on GitHub", Feedback.Body(r.Fields())); });
        }

        public void Fill(string what, string expected, string steps)
        {
            fWhat.Box.Text = what ?? "";
            fExpected.Box.Text = expected ?? "";
            fSteps.Box.Text = steps ?? "";
        }

        ProblemReport Report()
        {
            ProblemReport r = ProblemReport.Make(filled.Version, filled.Windows, filled.Mode, null, null);
            r.Errors.AddRange(filled.Errors);
            r.What = fWhat.Value;
            r.Expected = fExpected.Value;
            r.Steps = fSteps.Value;
            return r;
        }

        void SaveBundle()
        {
            Session s = session != null ? session() : null;
            if (s == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Zip files (*.zip)|*.zip";
                d.FileName = "TrackVice-problem-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string path = d.FileName;
                bundleRow.Desc = "Saving...";
                bundleRow.Invalidate();
                Thread th = new Thread(delegate ()
                {
                    string msg;
                    try
                    {
                        byte[] zip = LogBundle.Make(Secrets.MaskedCopy(s));
                        if (zip == null) msg = "The bundle would be larger than " + (LogBundle.MaxBytes / 1024 / 1024) + " MB, so it was not saved. Save the session with Save > Save the session instead.";
                        else
                        {
                            File.WriteAllBytes(path, zip);
                            msg = "Saved " + path + ". Look it through before you attach it: the log files are as they are on the device.";
                            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch (Exception) { }
                        }
                    }
                    catch (Exception ex) { msg = "Not saved: " + ex.Message; }
                    try { BeginInvoke((MethodInvoker)delegate { bundleRow.Desc = msg; bundleRow.Invalidate(); Relayout(); }); }
                    catch (Exception) { }
                });
                th.IsBackground = true;
                th.Start();
            }
        }
    }

    // Help > Suggest a feature.
    public sealed class FeatureDialog : FeedbackPage
    {
        readonly FieldBox fWhat, fWhy, fHow;

        public FeatureDialog(Theme t) : base("Suggest a feature", t)
        {
            Add(new SettingRow(Glyphs.Info, "A wish for TrackVice", "Say what you would like, why, and how you would use it. The buttons open a GitHub issue, a GitHub discussion or an e-mail with this text. You send it; TrackVice sends nothing by itself and adds nothing.", null));
            fWhat = new FieldBox("What you would like", null, 4);
            fWhy = new FieldBox("Why", "What it would solve, or what takes too long today.", 3);
            fHow = new FieldBox("How you would use it", null, 3);
            Add(fWhat);
            Add(fWhy);
            Add(fHow);
            AddResultRow();
            Secondary();
            Footer("Open an e-mail", false, delegate { FeatureWish w = Wish(); OpenLink(w.MailUrl(), "an e-mail to " + Feedback.Email, Feedback.Body(w.Fields())); });
            Footer("Open a discussion", false, delegate { OpenDiscussion(Feedback.Body(Wish().Fields())); });
            Footer("Open a GitHub issue", true, delegate { FeatureWish w = Wish(); OpenLink(w.IssueUrl(), "a new issue on GitHub", Feedback.Body(w.Fields())); });
        }

        public void Fill(string what, string why, string how)
        {
            fWhat.Box.Text = what ?? "";
            fWhy.Box.Text = why ?? "";
            fHow.Box.Text = how ?? "";
        }

        FeatureWish Wish()
        {
            FeatureWish w = new FeatureWish();
            w.What = fWhat.Value;
            w.Why = fWhy.Value;
            w.How = fHow.Value;
            return w;
        }
    }

    // Help > Tell the author you use TrackVice.
    public sealed class TellAuthorDialog : FeedbackPage
    {
        readonly FieldBox fName, fCompany, fEmail, fUse;

        public TellAuthorDialog(Theme t) : base("Tell the author you use TrackVice", t)
        {
            Add(new SettingRow(Glyphs.Info, "Using TrackVice? Tell me", "TrackVice counts nothing and sends nothing by itself, so this is the only way the author knows. Every field is optional. The buttons open an e-mail to " + Feedback.Email + " or a GitHub discussion with this text, and you send it.", null));
            fName = new FieldBox("Name", null, 1);
            fCompany = new FieldBox("Company", null, 1);
            fEmail = new FieldBox("E-mail", "Only if you would like an answer.", 1);
            fUse = new FieldBox("What you use it for", "For example: Autopilot at customers, task sequences in SCCM, the agent.", 3);
            Add(fName);
            Add(fCompany);
            Add(fEmail);
            Add(fUse);
            AddResultRow();
            Secondary();
            Footer("Open a discussion", false, delegate { OpenDiscussion(Note().Text()); });
            Footer("Open an e-mail", true, delegate { UsageNote n = Note(); OpenLink(n.MailUrl(), "an e-mail to " + Feedback.Email, n.Text()); });
        }

        UsageNote Note()
        {
            UsageNote n = new UsageNote();
            n.Name = fName.Value;
            n.Company = fCompany.Value;
            n.Email = fEmail.Value;
            n.Use = fUse.Value;
            n.Version = Program.Version;
            return n;
        }
    }

    // The downloads of each release, as bars.
    public sealed class DownloadTable : FlatControl, IMeasured
    {
        List<ReleaseCount> rows = new List<ReleaseCount>();

        public DownloadTable()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public void SetRows(List<ReleaseCount> list)
        {
            rows = list ?? new List<ReleaseCount>();
            Invalidate();
        }

        int RowH
        {
            get { return Fonts.Ui.Height + Fonts.Small.Height + Dpi.S(16); }
        }

        public int Measure(int width)
        {
            return Dpi.S(12) + Math.Max(1, rows.Count) * RowH;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            Draw.Card(g, ClientRectangle, T);
            long max = 1;
            foreach (ReleaseCount r in rows) max = Math.Max(max, r.Total);
            int x = Dpi.S(20), vw = Dpi.S(110), nw = Dpi.S(90);
            int barX = x + vw, barW = Math.Max(Dpi.S(40), Width - barX - nw - Dpi.S(28));
            int y = Dpi.S(6);
            foreach (ReleaseCount r in rows)
            {
                int top = y + Dpi.S(8);
                Draw.Text(g, r.Version, T.Num(Fonts.UiSemibold), new Rectangle(x, top, vw, Fonts.Ui.Height), T.Text, Draw.Line);
                string when = r.PublishedUtc.HasValue ? r.PublishedUtc.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "";
                if (r.Prerelease) when += (when.Length > 0 ? " · " : "") + "pre-release";
                Draw.Text(g, when, Fonts.Small, new Rectangle(x, top + Fonts.Ui.Height + Dpi.S(2), vw, Fonts.Small.Height + 2), T.Text3, Draw.Line);
                int bw = (int)Math.Round((double)barW * r.Total / max);
                Rectangle bar = new Rectangle(barX, top + Dpi.S(2), Math.Max(Dpi.S(2), bw), Fonts.Ui.Height - Dpi.S(4));
                Shapes.Fill(g, new RectangleF(barX, bar.Y, barW, bar.Height), Dpi.S(3), T.CardAlt);
                Shapes.Fill(g, bar, Dpi.S(3), T.Accent);
                List<string> parts = new List<string>();
                foreach (Pair p in r.Files) parts.Add(p.Key + " " + p.Value);
                Draw.Text(g, string.Join("   ", parts.ToArray()), Fonts.Small, new Rectangle(barX, top + Fonts.Ui.Height + Dpi.S(2), barW, Fonts.Small.Height + 2), T.Text2, Draw.Line);
                Draw.Text(g, r.Total.ToString("N0", CultureInfo.CurrentCulture), T.Num(Fonts.UiSemibold), new Rectangle(Width - nw - Dpi.S(20), top, nw, Fonts.Ui.Height), T.Text, Draw.Line | TextFormatFlags.Right);
                y += RowH;
                if (y < Height - Dpi.S(8))
                    using (Pen p = new Pen(T.Divider)) g.DrawLine(p, x, y + Dpi.S(4), Width - Dpi.S(20), y + Dpi.S(4));
            }
        }
    }

    // Help > Downloads: how often each release was downloaded from GitHub.
    public sealed class DownloadsDialog : PageForm
    {
        readonly SettingRow summary;
        readonly DownloadTable table = new DownloadTable();

        public DownloadsDialog(Theme t, DownloadCounts demo) : base("Downloads", t)
        {
            Size = new Size(Dpi.S(780), Dpi.S(640));
            summary = new SettingRow(Glyphs2.Sync, "Reading GitHub...", "", null);
            Add(summary);
            Add(new SectionTitle("Per version"));
            Add(table);
            Add(new SettingRow(Glyphs.Info, "What the numbers are", "GitHub counts how often each file of a release was downloaded: by people, by winget and by scripts alike, and again for each update. So they are not the number of users. TrackVice collects nothing; Help > Tell the author you use TrackVice is the other way to be counted.", null));
            FlatButton bPage = new FlatButton("Releases on GitHub", null);
            bPage.Dock = DockStyle.Left;
            bPage.ApplyTheme(t);
            bPage.Click += Safe.Wrap(delegate { try { Process.Start(AppUpdate.ReleasesUrl); } catch (Exception) { } });
            footer.Controls.Add(bPage);
            if (demo != null) Shown += delegate { Show(demo); };
            else Shown += delegate { Read(); };
        }

        void Read()
        {
            Thread th = new Thread(delegate ()
            {
                DownloadCounts c = DownloadCounts.Read();
                try { BeginInvoke((MethodInvoker)delegate { Show(c); }); }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void Show(DownloadCounts c)
        {
            if (c.Error != null)
            {
                summary.Title = "No counts";
                summary.Desc = c.Error;
            }
            else
            {
                summary.Title = c.Total.ToString("N0", CultureInfo.CurrentCulture) + " downloads of " + c.Releases.Count + " version" + (c.Releases.Count == 1 ? "" : "s");
                summary.Desc = "From the releases of " + AppUpdate.Repo + " on GitHub, read " + c.ReadUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) + ". This is TrackVice " + Program.Version + ".";
            }
            table.SetRows(c.Releases);
            summary.Invalidate();
            Relayout();
        }
    }

    public sealed partial class MainForm
    {
        PublishBannerBar publishBanner;
        LinkNote reportNote;
        FlatButton bReport;
        string bannerRunning;
        // Later hides the banner until TrackVice starts again.
        static bool publishLater;

        public Theme CurrentTheme
        {
            get { return theme; }
        }

        // Help > Report a problem, Suggest a feature, Tell the author you use TrackVice and Downloads.
        void AddFeedbackItems(ContextMenuStrip m)
        {
            string why = FeedbackBlocked();
            Menus.Separator(m);
            ToolStripMenuItem[] items =
            {
                Menus.Add(m, "Report a problem…", delegate { OpenProblem(); }),
                Menus.Add(m, "Suggest a feature…", delegate { using (FeatureDialog d = new FeatureDialog(theme)) d.ShowDialog(this); }),
                Menus.Add(m, "Tell the author you use TrackVice…", delegate { using (TellAuthorDialog d = new TellAuthorDialog(theme)) d.ShowDialog(this); }),
                Menus.Add(m, "Downloads…", delegate { using (DownloadsDialog d = new DownloadsDialog(theme, null)) d.ShowDialog(this); }),
            };
            if (why == null) return;
            foreach (ToolStripMenuItem i in items)
            {
                i.Enabled = false;
                i.ToolTipText = why;
            }
        }

        // Why the Help pages that open links or ask GitHub are not offered on this device now, or null.
        string FeedbackBlocked()
        {
            if (start.Shot != null) return null;
            UpdateContext c = new UpdateContext();
            c.Window = true;
            c.Deploying = engine != null && engine.Mode == Modes.Live && UpdateGate.Deploying(engine.S, DateTime.UtcNow);
            bool system = false, ts = false;
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent()) system = id.IsSystem;
            }
            catch (Exception) { }
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
            return FeedbackGate.Reason(c);
        }

        ProblemReport MakeProblem()
        {
            List<string> names = new List<string>();
            names.Add(Environment.MachineName);
            names.Add(Environment.UserName);
            names.Add(Environment.UserDomainName);
            if (engine != null)
            {
                names.Add(engine.S.Computer);
                names.Add(engine.S.User);
            }
            names.Add(watchedCustomer);
            try { foreach (Customer c in CustomerUi.Book.All) names.Add(c.Name); }
            catch (Exception) { }
            string mode = engine != null ? Feedback.ModeName(engine.Mode, engine.IsRemote) : Feedback.ModeName(null, false);
            return ProblemReport.Make(Program.Version, Program.OsVersion(), mode, ProblemLog.Recent(), names);
        }

        void OpenProblem()
        {
            if (FeedbackBlocked() != null) return;
            Engine e = engine;
            Func<Session> s = e != null && e.Mode != Modes.Demo ? (Func<Session>)delegate { e.S.ClockUtc = e.Now; return e.S; } : null;
            using (ProblemDialog d = new ProblemDialog(theme, MakeProblem(), s)) d.ShowDialog(this);
        }

        // From BuildViews: Report a problem below a failed item, and a link in the status bar after an error.
        void AddReportLinks(Panel foot)
        {
            bReport = new FlatButton("Report a problem", null);
            bReport.Dock = DockStyle.Right;
            bReport.Visible = false;
            bReport.Click += Safe.Wrap(delegate { OpenProblem(); });
            Tip(bReport, "When TrackVice shows something wrong here: tell the author, with a GitHub issue or an e-mail you send yourself");
            themed.Add(bReport);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            gap.Tag = "card-foot";
            foot.Controls.Add(gap);
            foot.Controls.Add(bReport);
            gap.BringToFront();
            bReport.BringToFront();
            clockTimer.Tick += delegate { Safe.Run(delegate { bReport.Visible = HasFailed(); }); };

            Action<Exception> shown = Safe.Reporter;
            Safe.Reporter = delegate (Exception ex)
            {
                ProblemLog.Add(DateTime.Now, ex.Message);
                if (shown != null) shown(ex);
                ShowReportNote();
            };
        }

        bool HasFailed()
        {
            if (engine == null) return false;
            foreach (Item i in engine.S.Items) if (i.State == State.Failed) return true;
            return false;
        }

        void ShowReportNote()
        {
            if (FeedbackBlocked() != null) return;
            if (reportNote == null)
            {
                reportNote = new LinkNote("Report a problem");
                reportNote.Dock = DockStyle.Right;
                reportNote.ActionClicked += Safe.Wrap(delegate { reportNote.Visible = false; OpenProblem(); });
                themed.Add(reportNote);
                statusBar.Controls.Add(reportNote);
                statusLeft.BringToFront();
                reportNote.ApplyTheme(theme);
            }
            reportNote.Say("Something went wrong in TrackVice.");
            reportNote.Visible = true;
        }

        // From ShowPublishNote: the banner, when a version is published and this one is newer.
        void ShowPublishBanner(string running)
        {
            OwnStorage own = YourStorageUi.Load();
            string published = own != null ? own.PublishedVersion : "";
            if (!PublishBanner.Shown(own != null, published, running, publishLater)) return;
            if (FeedbackBlocked() != null) return;
            bannerRunning = running;
            if (publishBanner == null)
            {
                publishBanner = new PublishBannerBar();
                publishBanner.PublishButton.Click += Safe.Wrap(delegate { PublishNow(); });
                publishBanner.ExplainButton.Click += Safe.Wrap(delegate { PublishExplainDialog.Open(publishBanner); });
                publishBanner.LaterButton.Click += Safe.Wrap(delegate
                {
                    if (publishBanner.PublishButton.Visible) publishLater = true;
                    publishBanner.Visible = false;
                });
                themed.Add(publishBanner);
                Controls.Add(publishBanner);
                Controls.SetChildIndex(publishBanner, Controls.GetChildIndex(busyBar));
                publishBanner.ApplyTheme(theme);
                // Published from the status bar: the banner has nothing more to say.
                if (publishNote != null) publishNote.VisibleChanged += delegate { if (!publishNote.Visible && publishBanner.PublishButton.Visible) publishBanner.Visible = false; };
            }
            publishBanner.Say(PublishBanner.Text(running, published), true, "Later");
            publishBanner.Visible = true;
        }

        IAttentionProvider publishProvider;

        // Puts "Publish <version>" under Needs attention on Overview while the banner's rule holds.
        void RegisterPublishTile()
        {
            if (publishProvider != null) return;
            publishProvider = OverviewAttention.Register(delegate
            {
                List<AttentionTile> tiles = new List<AttentionTile>();
                PublishTile t;
                try { t = PublishAttention(); }
                catch (Exception) { return tiles; }
                if (t == null || !t.Show) return tiles;
                AttentionTile a = new AttentionTile();
                a.Key = "publish";
                a.Level = 1;
                a.Title = "Publish TrackVice " + YourStorageUi.RunningVersion;
                a.Text = t.Text;
                a.ActionText = "Publish";
                Action publish = t.Publish;
                a.Open = delegate { BeginInvoke((MethodInvoker)delegate { publish(); }); };
                a.Order = 15;
                tiles.Add(a);
                return tiles;
            });
            FormClosed += delegate { OverviewAttention.Unregister(publishProvider); };
        }

        // What the Needs attention tile on Overview needs: whether to show it, its text, and Publish.
        public PublishTile PublishAttention()
        {
            OwnStorage o = YourStorageUi.Load();
            return PublishBanner.Tile(o != null, o != null ? o.PublishedVersion : null, YourStorageUi.RunningVersion, delegate { PublishNow(); });
        }

        void PublishNow()
        {
            string running = bannerRunning ?? YourStorageUi.RunningVersion;
            if (publishBanner != null) publishBanner.Working("Publishing TrackVice " + running + " to your storage...");
            YourStorageUi.PublishRunning(this, delegate (string msg, bool ok)
            {
                if (ok && publishNote != null) publishNote.Visible = false;
                if (publishBanner != null)
                {
                    if (ok) publishBanner.Say(PublishBanner.Done(running), false, "Close");
                    else publishBanner.Say(msg, true, "Later");
                    publishBanner.Visible = true;
                }
                Status(ok ? PublishBanner.Done(running) : msg);
            });
        }

        public static readonly string[] FeedbackPages = { "publish-banner", "publish-done", "publish-explain", "report", "feature", "tellauthor", "downloads", "report-note" };

        // Screenshots of the banner, the explanation and the Help pages, with made-up data. --tab scrolls a page down.
        Form ShotFeedback()
        {
            Form f = ShotFeedbackPage();
            if (f != null && start.Tab >= 0)
                f.Shown += delegate { f.BeginInvoke((MethodInvoker)delegate { foreach (Control c in f.Controls) { Panel p = c as Panel; if (p != null && p.AutoScroll) p.AutoScrollPosition = new Point(0, Dpi.S(start.Tab)); } }); };
            return f;
        }

        Form ShotFeedbackPage()
        {
            switch (start.Page)
            {
                case "publish-banner":
                case "publish-done":
                    YourStorageUi.UseDemo();
                    ShowPublishBanner(YourStorageUi.RunningVersion);
                    if (start.Page == "publish-done" && publishBanner != null) publishBanner.Say(PublishBanner.Done(YourStorageUi.RunningVersion), false, "Close");
                    return null;
                case "publish-explain":
                    YourStorageUi.UseDemo();
                    return new PublishExplainDialog(theme, YourStorageUi.RunningVersion, YourStorageUi.Load().PublishedVersion);
                case "report":
                    ProblemLog.Add(DateTime.Now.AddMinutes(-3), "The list of devices could not be read from https://contosotrackvice.blob.core.windows.net/tv-contoso?sv=2024 (403).");
                    ProblemLog.Add(DateTime.Now.AddMinutes(-1), "That did not work: CONTOSO-PC01 has no session for 6d2f1c7e-0b5a-4f1e-9a43-3c2b8e7d9f10.");
                    ProblemDialog pd = new ProblemDialog(theme, ProblemReport.Make(Program.Version, "Windows 11 Enterprise 24H2 (26100.2605)", Feedback.ModeName(Modes.Agent, true), ProblemLog.Recent(), new string[] { "CONTOSO-PC01", "Contoso" }), delegate { return engine != null ? engine.S : null; });
                    pd.Fill("The list of devices with the agent stays empty after I switch customer.", "The devices of the customer I switched to.", "1. Devices > Intune / cloud > Devices with the TrackVice agent\r\n2. Switch to another customer\r\n3. The list is empty until TrackVice is started again");
                    return pd;
                case "feature":
                    FeatureDialog fd = new FeatureDialog(theme);
                    fd.Fill("Filter the wall view by Autopilot profile.", "We run three profiles at one customer, and the wall gets full.", "Pick the profile at the top of the wall view during a roll-out day.");
                    return fd;
                case "tellauthor": return new TellAuthorDialog(theme);
                case "downloads": return new DownloadsDialog(theme, DownloadCounts.Demo());
                default:
                    ShowReportNote();
                    return null;
            }
        }
    }
}
