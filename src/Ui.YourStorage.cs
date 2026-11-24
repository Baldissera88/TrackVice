namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // Secrets in the TrackVice settings of this Windows user, encrypted with DPAPI.
    public sealed class PrefsSecrets : ISecretStore
    {
        public string Load(string name) { return Secret.Load(name); }

        public void Save(string name, string value)
        {
            if (value == null) Prefs.Set(name, null);
            else Secret.Save(name, value);
        }
    }

    // A page of a short guide: rows that change as it goes, a line for messages in the footer, and work away from the window.
    abstract partial class GuideForm : PageForm
    {
        protected readonly WizardBody body = new WizardBody();
        protected readonly FooterNote note = new FooterNote();
        protected readonly ManualResetEvent cancel = new ManualResetEvent(false);
        protected readonly IWeb web;
        protected readonly bool demo;
        protected bool busy;
        // The code of a sign-in that is waiting.
        protected DeviceCode pending;

        protected GuideForm(string title, Theme t, IWeb web, bool isDemo) : base(title, t)
        {
            this.web = web;
            demo = isDemo;
            note.Dock = DockStyle.Fill;
            footer.Controls.Add(note);
            note.BringToFront();
            note.ApplyTheme(t);
            AcceptButton = null;
            FormClosed += delegate { cancel.Set(); };
        }

        protected abstract void Build(List<Control> l);

        protected virtual void Nav() { }

        protected void Rebuild()
        {
            List<Control> l = new List<Control>();
            Build(l);
            body.SetItems(l);
            body.ApplyTheme(theme);
            Relayout();
        }

        protected TextBox Field(int width, bool mono)
        {
            TextBox b = new TextBox();
            b.Width = Dpi.S(width);
            b.Font = mono ? Fonts.Mono : Fonts.Ui;
            b.BorderStyle = BorderStyle.FixedSingle;
            b.BackColor = theme.Field;
            b.ForeColor = theme.Text;
            body.Keep.Add(b);
            return b;
        }

        protected ComboBox Combo(int width, bool list)
        {
            ComboBox c = new ComboBox();
            c.Width = Dpi.S(width);
            c.DropDownStyle = list ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
            c.FlatStyle = FlatStyle.Flat;
            c.Font = Fonts.Ui;
            c.BackColor = theme.Field;
            c.ForeColor = theme.Text;
            c.MaxDropDownItems = 12;
            body.Keep.Add(c);
            return c;
        }

        protected FlatButton Button(string text, EventHandler click)
        {
            FlatButton b = SmallButton(text, click);
            body.Keep.Add(b);
            return b;
        }

        protected T Kept<T>(T c) where T : Control
        {
            body.Keep.Add(c);
            return c;
        }

        protected static SettingRow Row(string glyph, string title, string desc, Control right)
        {
            return new SettingRow(glyph, title, desc, right);
        }

        protected static void Open(string url)
        {
            try { Process.Start(url); }
            catch (Exception) { Menus.Copy(url); }
        }

        protected FlatButton CopyButton(string what, Func<string> text)
        {
            FlatButton b = SmallButton("Copy", delegate
            {
                note.Say(Menus.Copy(text()) ? "Copied the " + what + "." : "The " + what + " could not be copied.", false);
            });
            b.Glyph = Glyphs.Copy;
            b.AutoWidth();
            b.Width += Dpi.S(8);
            return b;
        }

        // Runs a step away from the window. In the demo it runs at once, so a screenshot shows the result.
        protected void Work(string doing, Action job, Action done, Action failed)
        {
            if (busy) return;
            busy = true;
            note.Say(doing, false);
            Nav();
            if (demo)
            {
                Finish(Try(job), done, failed);
                return;
            }
            Thread th = new Thread(delegate ()
            {
                string err = Try(job);
                try { BeginInvoke((MethodInvoker)delegate { Finish(err, done, failed); }); }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        static string Try(Action job)
        {
            try { job(); return null; }
            catch (OperationCanceledException) { return ""; }
            catch (Exception ex) { return ex.Message; }
        }

        void Finish(string err, Action done, Action failed)
        {
            busy = false;
            if (IsDisposed) return;
            if (err == null)
            {
                note.Say("", false);
                if (done != null) done();
            }
            else
            {
                if (failed != null) failed();
                if (err.Length > 0) note.Say(err, true);
            }
            Rebuild();
            Nav();
            AfterFinish();
        }

        // Called when a step has ended and the page is rebuilt. A choice made while it ran can be acted on here.
        protected virtual void AfterFinish() { }

        // Posts to the window from a step that is still running.
        protected void Ui(Action a)
        {
            if (demo || !InvokeRequired) { a(); return; }
            try { BeginInvoke((MethodInvoker)delegate { a(); }); }
            catch (Exception) { }
        }

        // A device code sign-in. The code is copied and the page opened; pressing the button again does it again.
        protected void SignIn(WebSignIn s, Action<string> say, Action extra, Action done, Action failed)
        {
            say(s.SignedIn ? "Signed in" + (string.IsNullOrEmpty(s.Account) ? "." : " as " + s.Account + ".") : "Asking for a sign-in code...");
            Rebuild();
            Work("Signing in...", delegate
            {
                // A sign-in made earlier in this run is used again, with a token for more scopes when they are needed.
                if (!s.SignedIn && !s.Quiet())
                {
                    DeviceCode d = s.Start();
                    Ui(delegate
                    {
                        pending = d;
                        say("Open " + d.VerificationUri + " and enter the code " + d.UserCode + ". TrackVice waits for the sign-in.");
                        Rebuild();
                        if (!demo) ShowCode(d);
                    });
                    s.Wait(d, cancel);
                }
                if (extra != null) extra();
            }, delegate
            {
                pending = null;
                say(string.IsNullOrEmpty(s.Account) ? "Signed in." : "Signed in as " + s.Account + ".");
                if (done != null) done();
            }, delegate { pending = null; if (failed != null) failed(); });
        }

        protected void ShowCode(DeviceCode d)
        {
            bool copied = Menus.Copy(d.UserCode);
            Open(d.VerificationUri);
            note.Say(copied ? "The code is copied. Paste it on the page that opened, and sign in." : "Enter the code on the page that opened, and sign in.", false);
        }
    }

    // Your storage: kept on this PC, shown in Settings, and offered for publishing when TrackVice is newer.
    public static class YourStorageUi
    {
        static ISecretStore secrets;
        static IWeb web;

        // The demo and screenshots put made-up ones here.
        public static ISecretStore Secrets
        {
            get { return secrets ?? (secrets = new PrefsSecrets()); }
            set { secrets = value; }
        }

        public static IWeb Web
        {
            get { return web ?? (web = new HttpWeb()); }
            set { web = value; }
        }

        public static IUpdateKeyStore Keys = new DpapiKeyStore();
        public static bool Demo;

        public static OwnStorage Load()
        {
            return OwnStorage.Load(Secrets);
        }

        // Made-up storage for screenshots, with an older version published.
        public static void UseDemo()
        {
            Secrets = DemoSecrets();
            Web = new DemoStorageWeb(true);
            Keys = DemoKeys();
            Demo = true;
        }

        public static MemorySecrets DemoSecrets()
        {
            MemorySecrets s = new MemorySecrets();
            AzureAccount a = new AzureAccount();
            a.Name = "contosotrackvice";
            a.BlobEndpoint = "https://contosotrackvice.blob.core.windows.net/";
            a.Location = "westeurope";
            OwnStorage o = OwnStorage.Make(a, Convert.ToBase64String(Encoding.UTF8.GetBytes("made-up key for the TrackVice demo, not a real one")), "Contoso Production", DateTime.UtcNow);
            o.PublishedVersion = "1.1.2";
            o.Save(s);
            return s;
        }

        public static MemoryKeyStore DemoKeys()
        {
            MemoryKeyStore k = new MemoryKeyStore();
            bool created;
            using (UpdateKeys.LoadOrCreate(k, out created)) { }
            return k;
        }

        public static string RunningVersion
        {
            get { return SemVer.OfFile(Program.Location) ?? Program.Version; }
        }

        public static string Expires(OwnStorage o)
        {
            return o.ExpiresUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        // Publishes the running TrackVice.exe away from the window. done gets the message and whether it worked.
        public static void PublishRunning(Control owner, Action<string, bool> done)
        {
            OwnStorage own = Load();
            if (own == null) { done("Your storage is not set up.", false); return; }
            if (!own.KeyKept)
            {
                // The publish link is not kept with the key, so a sign-in reads the key for this publish.
                Form f = owner.FindForm();
                PageForm pf = f as PageForm;
                string msg = "Not published.";
                bool ok = false;
                using (OwnPublishDialog d = new OwnPublishDialog(pf != null ? pf.CurrentTheme : Theme.Light(), Web, Secrets, Keys, Demo))
                {
                    d.ShowDialog(f);
                    if (d.Message.Length > 0) { msg = d.Message; ok = d.Ok; }
                }
                done(msg, ok);
                return;
            }
            IWeb w = Web;
            ISecretStore s = Secrets;
            IUpdateKeyStore keys = Keys;
            bool demo = Demo;
            Thread th = new Thread(delegate ()
            {
                string msg;
                bool ok = PublishWith(w, own, own, s, keys, demo, out msg);
                try { owner.BeginInvoke((MethodInvoker)delegate { done(msg, ok); }); }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        // Publishes with own, which has a publish link, and saves the version in kept, which may have no key. Runs away from the window.
        public static bool PublishWith(IWeb w, OwnStorage own, OwnStorage kept, ISecretStore s, IUpdateKeyStore keys, bool demo, out string msg)
        {
            try
            {
                byte[] exe = demo ? Encoding.ASCII.GetBytes("MZ TrackVice demo") : File.ReadAllBytes(Program.Location);
                string v = RunningVersion;
                bool created;
                using (RSACryptoServiceProvider key = UpdateKeys.LoadOrCreate(keys, out created))
                {
                    string problem = OwnPublish.KeyProblem(w, own, UpdateKeys.PublicKey(key));
                    if (problem != null) throw new SetupException(problem);
                    OwnPublish.Publish(w, own, exe, v, key, DateTime.UtcNow);
                }
                kept.PublishedVersion = own.PublishedVersion;
                kept.Save(s);
                msg = "Published TrackVice " + v + " to your storage." + (created ? " A new update key was made: customer codes made before have the old key, so make them again." : " Agents install it within 6 hours.");
                return true;
            }
            catch (Exception ex)
            {
                msg = "Not published: " + ex.Message;
                return false;
            }
        }

        static FlatButton Small(Theme theme, string text, EventHandler click)
        {
            FlatButton b = new FlatButton(text, null);
            b.Compact = true;
            b.AutoWidth();
            b.Width += Dpi.S(8);
            b.Click += Safe.Wrap(click);
            b.ApplyTheme(theme);
            return b;
        }

        // The Your storage section of Settings.
        public static List<Control> SettingRows(Theme theme, MethodInvoker relayout)
        {
            List<Control> l = new List<Control>();
            SettingRow main = null, version = null, remove = null, security = null, days = null, keyRow = null;
            Collapsible versionBox = null, removeBox = null, securityBox = null, daysBox = null, keyBox = null;
            Segmented sDays = null;
            Toggle gAsk = null;
            bool filling = false;
            FlatButton bSetUp = null, bPublish = null, bRemove = null;
            ButtonStrip strip = null;
            MethodInvoker show = null;
            show = delegate
            {
                OwnStorage o = Load();
                string running = RunningVersion;
                bSetUp.Text = o == null ? "Set up" : "Set up again";
                bSetUp.AutoWidth();
                bSetUp.Width += Dpi.S(8);
                strip.Fit();
                main.Desc = o == null
                    ? "Not set up. One storage account in your Azure for all customers: TrackVice versions for the devices in " + Arm.AgentFiles + ", and a private container for each customer's uploads. Devices > Add customer uses it."
                    : o.Account + (o.Subscription.Length > 0 ? " in " + o.Subscription : "") + ". The links to " + Arm.AgentFiles + " are valid until " + Expires(o) + ". " + (o.KeyKept ? "The account key and the links are kept on this PC, encrypted for your Windows account." : "The read link is kept on this PC, encrypted for your Windows account; the account key is not.");
                versionBox.Shown = removeBox.Shown = securityBox.Shown = daysBox.Shown = keyBox.Shown = o != null;
                if (o != null)
                {
                    filling = true;
                    int di = Array.IndexOf(StorageSecurity.DayChoices, o.RetentionDays);
                    sDays.Selected = di >= 0 ? di : Array.IndexOf(StorageSecurity.DayChoices, StorageSecurity.DefaultDays);
                    gAsk.On = o.AskKey;
                    filling = false;
                    security.Desc = "Reads " + o.Account + " through Azure: anonymous blob access, HTTPS only, minimum TLS 1.2, soft delete of blobs and containers, and the rule that deletes old uploads. A setting that is not in order can be changed, one at a time, after you confirm. The account may also be used for other things, so TrackVice never changes network rules or keys.";
                    days.Desc = "The lifecycle rule " + StorageSecurity.RuleName + " deletes blobs in the containers tv-... this many days after they last changed. "
                        + (o.RuleDays == o.RetentionDays ? "The rule in Azure does this." : o.RuleDays > 0 ? "The rule in Azure deletes after " + o.RuleDays + " days; Check security updates it." : "TrackVice has not seen the rule in Azure; Check security adds it.");
                    keyRow.Desc = o.KeyKept
                        ? "Off: the account key is kept on this PC, encrypted for your Windows account, so Add customer needs no Azure sign-in. On: the key and the publish link are deleted from this PC, and Add customer, Publish, New links and Remove customer sign in to Azure to read the key, and forget it after."
                        : "On: the account key is not kept on this PC. Add customer, Publish, New links and Remove customer sign in to Azure to read it, and forget it after." + (o.AskKey ? "" : " It is kept again the next time it is read.");
                    bool need = OwnPublish.Needed(o, o.PublishedVersion, running);
                    version.Desc = (o.PublishedVersion.Length > 0 ? "Published: " + o.PublishedVersion + "." : "Nothing is published yet.") + " This is TrackVice " + running + "."
                        + (need ? " Publish uploads it with a signed agent/latest.json, and the agents install it within 6 hours." : "");
                    bPublish.Enabled = need;
                }
                main.Invalidate();
                version.Invalidate();
                security.Invalidate();
                days.Invalidate();
                keyRow.Invalidate();
                relayout();
            };
            bSetUp = Small(theme, "Set up", delegate
            {
                Form f = bSetUp.FindForm();
                // The theme may have been changed in Settings since these rows were made.
                PageForm page = f as PageForm;
                using (YourStorageDialog d = YourStorageDialog.ForLive(page != null ? page.CurrentTheme : theme)) d.ShowDialog(f);
                show();
            });
            bPublish = Small(theme, "Publish", delegate
            {
                bPublish.Enabled = false;
                version.Desc = "Publishing...";
                version.Invalidate();
                PublishRunning(bPublish, delegate (string msg, bool ok) { show(); version.Desc = msg; version.Invalidate(); relayout(); });
            });
            bPublish.Primary = true;
            bRemove = Small(theme, "Remove", delegate
            {
                if (MessageBox.Show(bRemove.FindForm(), "Forget your storage on this PC? The account key and the links are deleted here. Nothing in Azure is deleted, and devices keep working.", "Your storage", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                OwnStorage.Forget(Secrets);
                show();
            });
            FlatButton bCheck = Small(theme, "Check security", delegate
            {
                Form f = bSetUp.FindForm();
                PageForm page = f as PageForm;
                using (StorageSecurityDialog d = StorageSecurityDialog.ForLive(page != null ? page.CurrentTheme : theme)) d.ShowDialog(f);
                show();
            });
            string[] dayNames = new string[StorageSecurity.DayChoices.Length];
            for (int i = 0; i < dayNames.Length; i++) dayNames[i] = StorageSecurity.DayChoices[i].ToString(CultureInfo.InvariantCulture) + " days";
            sDays = new Segmented(dayNames);
            sDays.Changed += Safe.Wrap(delegate
            {
                if (filling) return;
                OwnStorage o = Load();
                if (o == null) return;
                o.RetentionDays = StorageSecurity.DayChoices[sDays.Selected];
                o.Save(Secrets);
                show();
            });
            gAsk = new Toggle();
            gAsk.Changed += Safe.Wrap(delegate
            {
                if (filling) return;
                OwnStorage o = Load();
                if (o == null) return;
                if (gAsk.On && o.KeyKept && !Demo && MessageBox.Show(gAsk.FindForm(), "Delete the account key of " + o.Account + " and the publish link from this PC? Add customer, Publish, New links and Remove customer then sign in to Azure to read the key. Nothing in Azure is changed.", "Your storage", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    filling = true;
                    gAsk.On = false;
                    filling = false;
                    return;
                }
                o.AskKey = gAsk.On;
                if (o.AskKey) o.DropKey();
                o.Save(Secrets);
                show();
            });
            strip = new ButtonStrip(bSetUp);
            main = new SettingRow(Glyphs2.Folder, "Your storage", "", strip);
            version = new SettingRow(Glyphs2.Sync, "TrackVice for the devices", "", new ButtonStrip(PublishExplainDialog.Button(theme), bPublish));
            remove = new SettingRow(Glyphs.Shield, "Remove", "Forgets the account key and the links on this PC. Nothing in Azure is deleted.", bRemove);
            security = new SettingRow(Glyphs.Shield, "Security", "", bCheck);
            days = new SettingRow(Glyphs2.Calendar, "Delete uploads after", "", sDays);
            keyRow = new SettingRow(Glyphs.Shield, "Ask for Azure sign-in when adding a customer instead of keeping the account key on this PC", "", gAsk);
            securityBox = new Collapsible(security);
            daysBox = new Collapsible(days);
            keyBox = new Collapsible(keyRow);
            l.Add(new SectionTitle("Your storage"));
            versionBox = new Collapsible(version);
            removeBox = new Collapsible(remove);
            l.Add(main);
            l.Add(versionBox);
            l.Add(securityBox);
            l.Add(daysBox);
            l.Add(keyBox);
            l.Add(removeBox);
            show();
            return l;
        }
    }

    // A setting row that takes no room in a page while it is not shown. The page lays out every item, hidden or not.
    sealed class Collapsible : Panel, IThemed, IMeasured
    {
        readonly SettingRow row;
        bool shown = true;

        public Collapsible(SettingRow row)
        {
            this.row = row;
            row.Dock = DockStyle.Fill;
            Controls.Add(row);
        }

        public bool Shown
        {
            get { return shown; }
            set { shown = value; row.Visible = value; }
        }

        public int Measure(int width)
        {
            return shown ? row.Measure(width) : 0;
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.Bg;
            row.ApplyTheme(t);
        }
    }

    // In the status bar: the running version is newer than the one in your storage. Publishes only when clicked.
    public sealed class PublishNote : FlatControl
    {
        const string Action = "Publish";
        public event EventHandler PublishClicked;
        bool working;

        public PublishNote()
        {
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        public void Say(string text, bool action)
        {
            Text = text;
            working = !action;
            Cursor = action ? Cursors.Hand : Cursors.Default;
            Fit();
            Invalidate();
        }

        void Fit()
        {
            int w = TextRenderer.MeasureText(Text, Fonts.Ui).Width + Dpi.S(8);
            if (!working) w += TextRenderer.MeasureText(Action, Fonts.UiSemibold).Width + Dpi.S(4);
            Width = w + Dpi.S(20);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!working && PublishClicked != null) PublishClicked(this, e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            int tw = TextRenderer.MeasureText(Text, Fonts.Ui).Width;
            int x = Dpi.S(16);
            Draw.Text(g, Text, Fonts.Ui, new Rectangle(x, 0, tw + 2, Height), T.Text2, Draw.Line);
            if (working) return;
            Font f = hover ? new Font(Fonts.UiSemibold, FontStyle.Underline) : Fonts.UiSemibold;
            try { Draw.Text(g, Action, f, new Rectangle(x + tw + Dpi.S(8), 0, Width - x - tw - Dpi.S(8), Height), T.Accent, Draw.Line); }
            finally { if (hover) f.Dispose(); }
        }
    }

    // Sets up your storage once: sign in to Azure, pick or make the account, and Set up.
    sealed class YourStorageDialog : GuideForm
    {
        readonly ISecretStore secrets;
        readonly IUpdateKeyStore keys;
        readonly Func<byte[]> exe;
        readonly string version;
        readonly OwnStorageRun run = new OwnStorageRun();
        readonly TextBox tTenant, tAccount;
        readonly ComboBox cSub, cGroup, cRegion, cAccount;
        readonly Segmented sKind;
        readonly FlatButton bArm, bSetUp;
        WebSignIn armIn;
        string armText = "";
        List<AzureSubscription> subs = new List<AzureSubscription>();
        List<AzureAccount> accounts = new List<AzureAccount>();
        bool filling;
        readonly List<UsedAccount> used;
        readonly Toggle gAsk;
        readonly Segmented sDays;
        // The account the customers' links point at, to select once the subscription is read.
        string preselect;
        // What is ticked in the What you need box.
        readonly HashSet<string> ticks = new HashSet<string>();

        public YourStorageDialog(Theme t, IWeb web, ISecretStore secrets, IUpdateKeyStore keys, Func<byte[]> exe, string version, bool isDemo, IEnumerable<Customer> customers) : base("Your storage", t, web, isDemo)
        {
            used = StorageFinder.Used(customers ?? new List<Customer>());
            OwnStorage before = OwnStorage.Load(secrets);
            gAsk = Kept(new Toggle());
            gAsk.On = before != null && before.AskKey;
            string[] dayNames = new string[StorageSecurity.DayChoices.Length];
            for (int i = 0; i < dayNames.Length; i++) dayNames[i] = StorageSecurity.DayChoices[i].ToString(CultureInfo.InvariantCulture) + " days";
            sDays = Kept(new Segmented(dayNames));
            int di = Array.IndexOf(StorageSecurity.DayChoices, before != null ? before.RetentionDays : StorageSecurity.DefaultDays);
            sDays.Selected = di >= 0 ? di : Array.IndexOf(StorageSecurity.DayChoices, StorageSecurity.DefaultDays);
            this.secrets = secrets;
            this.keys = keys;
            this.exe = exe;
            this.version = version;
            Size = new Size(Dpi.S(860), Dpi.S(760));
            tTenant = Field(300, false);
            tAccount = Field(220, true);
            tAccount.MaxLength = 24;
            tAccount.CharacterCasing = CharacterCasing.Lower;
            if (demo) tAccount.Text = "contosotrackvice";
            cSub = Combo(360, true);
            cGroup = Combo(260, false);
            cGroup.Text = "rg-trackvice";
            cRegion = Combo(200, false);
            foreach (string r in Arm.Regions) cRegion.Items.Add(r);
            cRegion.Text = Arm.DefaultRegion;
            cAccount = Combo(360, true);
            sKind = Kept(new Segmented("New account", "Existing account"));
            bArm = Button("Sign in", delegate { SignInAzure(); });
            bSetUp = Button("Set up", delegate { SetUp(); });
            bSetUp.Primary = true;
            cSub.SelectedIndexChanged += delegate { if (!filling) { chosenSub = cSub.SelectedItem as AzureSubscription; LoadSubscription(); } };
            watch.Interval = 400;
            watch.Tick += delegate { WatchSubscription(); };
            watch.Start();
            FormClosed += delegate { watch.Stop(); watch.Dispose(); };
            cSub.SelectionChangeCommitted += delegate { chosenSub = cSub.SelectedItem as AzureSubscription; LoadSubscription(); };
            sKind.Changed += delegate { run.NewAccount = sKind.Selected == 0; Rebuild(); };
            foreach (Control c in new Control[] { cGroup, cRegion, tAccount }) c.TextChanged += delegate { if (!filling) Plan(); };
            cAccount.SelectedIndexChanged += delegate { if (!filling) { Plan(); Rebuild(); } };
            sDays.Changed += delegate { Plan(); };
            gAsk.Changed += delegate { Plan(); };
            Add(body);
            Rebuild();
        }

        public static YourStorageDialog ForLive(Theme t)
        {
            YourStorageDialog d = new YourStorageDialog(t, YourStorageUi.Web, YourStorageUi.Secrets, YourStorageUi.Keys, delegate { return File.ReadAllBytes(Program.Location); }, YourStorageUi.RunningVersion, false, CustomerUi.Book.All);
            d.Shown += delegate { if (SignIns.Has(d.web, Arm.ClientId, d.tTenant.Text, Arm.Scope)) d.SignInAzure(); };
            return d;
        }

        // tab 0: before the sign-in, 1: signed in, 2: set up.
        public static YourStorageDialog ForDemo(Theme t, int tab)
        {
            MemoryKeyStore k = new MemoryKeyStore();
            YourStorageDialog d = new YourStorageDialog(t, new DemoStorageWeb(true), new MemorySecrets(), k, delegate { return Encoding.ASCII.GetBytes("MZ TrackVice demo"); }, YourStorageUi.RunningVersion, true, DemoStorageWeb.Customers());
            if (tab >= 1) d.SignInAzure();
            if (tab >= 2 && tab < 4) d.SetUp();
            d.note.Say("", false);
            // tab 4: a long message in the footer, as when a role is missing.
            if (tab == 4) d.note.Say("Your account may not create the resource group rg-contoso. It needs the Azure role Contributor (or Owner) on the subscription. An Owner of the subscription can give it: in the Azure portal, open the subscription, then Access control (IAM) and Add role assignment. A role given a minute ago can take a few minutes to work.", true);
            return d;
        }

        public bool IsSetUp
        {
            get { return run.Result != null; }
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle("Your storage"));
            l.Add(Row(Glyphs.Info, "What this does", "Sets up one storage account in your Azure for all customers. TrackVice versions go in the container " + Arm.AgentFiles + ", where devices download them, and Add customer makes a private container for each customer's uploads. The account key is kept on this PC, encrypted for your Windows account, to make those containers and their links, unless you choose below to sign in each time. Signing in and listing change nothing.", null));
            if (run.Result == null)
            {
                NeedsBox needs = new NeedsBox("What you need", SetupNeeds.BoxIntro, SetupNeeds.StorageChecklist(), ticks, "Roles and consent as a table");
                needs.MoreClicked += delegate { Open(AddCustomerUi.AgentDoc + "#what-you-need"); };
                l.Add(needs);
            }
            if (run.Result != null)
            {
                l.Add(new SectionTitle("Done"));
                l.Add(Row(Glyphs.Save, "Made", string.Join("\n", run.Done.ToArray()), null));
                l.Add(Row(Glyphs2.Help, "Next", "Add your customers with Devices > Add customer. When you update TrackVice, it offers to publish the new version to your storage.", null));
                AddSecurityRows(l, run.Result, ArmApi, secrets);
                return;
            }
            l.Add(new SectionTitle("Sign in"));
            l.Add(Row(Glyphs.Shield, "Azure tenant", "Where your subscription is. Empty signs in to the tenant of your account.", tTenant));
            bArm.Text = armIn != null && armIn.SignedIn ? "Sign in again" : pending != null ? "Copy code and open" : "Sign in";
            l.Add(Row(Glyphs2.Sync, "Sign in to Azure", armText.Length > 0 ? armText : "With a code in the browser. Reads your subscriptions; nothing is changed.", bArm));
            if (armIn == null || !armIn.SignedIn || subs.Count == 0) return;
            l.Add(new SectionTitle("Storage account"));
            AzureSubscription sub = ShownSubscription();
            l.Add(Row(Glyphs2.Settings, "Subscription", sub != null ? "ID " + sub.Id : "", cSub));
            l.Add(Row(Glyphs2.Folder, "Account", "A new account just for TrackVice, or an existing one that gets the container " + Arm.AgentFiles + ".", sKind));
            if (run.NewAccount)
            {
                l.Add(Row(Glyphs2.Folder, "Resource group", "Pick one, or type a name to make a new group.", cGroup));
                l.Add(Row(Glyphs2.Apps, "Name", "3 to 24 lowercase letters and digits, unique in all of Azure. Checked with Azure before it is made.", tAccount));
                l.Add(Row(Glyphs.Info, "Region", "Where the data of your customers is kept.", cRegion));
            }
            else l.Add(Row(Glyphs2.Folder, "Existing account", accounts.Count == 0 ? "This subscription has no storage accounts." + (diag.Length > 0 ? " " + diag : "") : (UsedText() + " Its settings are kept. Account key access must be on.").Trim(), cAccount));
            l.Add(Row(Glyphs2.Calendar, "Delete uploads after", "The lifecycle rule " + StorageSecurity.RuleName + " deletes blobs in the containers tv-... this many days after they last changed. Other rules on the account are kept.", sDays));
            l.Add(Row(Glyphs.Shield, "Ask for Azure sign-in when adding a customer instead of keeping the account key on this PC", "Off: the account key is kept on this PC, encrypted for your Windows account. On: it is not kept, and Add customer and Publish sign in to Azure to read it.", gAsk));
            l.Add(Row(Glyphs.Save, "Set up", PlanText(), bSetUp));
        }

        // "Used by your customers ..." for the account picked, or empty.
        string UsedText()
        {
            AzureAccount a = cAccount.SelectedItem as AzureAccount;
            if (a == null) return "";
            foreach (UsedAccount u in used) if (string.Equals(u.Name, a.Name, StringComparison.OrdinalIgnoreCase)) return StorageFinder.UsedLine(u.Customers);
            return "";
        }

        string GroupName
        {
            get { return (cGroup.Text ?? "").Trim(); }
        }

        string RegionName
        {
            get { return (cRegion.Text ?? "").Trim().ToLowerInvariant().Replace(" ", ""); }
        }

        bool GroupExists(string name)
        {
            foreach (object o in cGroup.Items) if (string.Equals(o as string, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        string PlanText()
        {
            StringBuilder b = new StringBuilder("Makes ");
            if (run.NewAccount)
            {
                if (GroupName.Length > 0 && !GroupExists(GroupName)) b.Append("the resource group ").Append(GroupName).Append(" in ").Append(RegionName).Append(", ");
                b.Append("the storage account ").Append(tAccount.Text.Trim()).Append(" (StorageV2, Standard_LRS, ").Append(RegionName).Append(", HTTPS only, TLS 1.2 or later, no anonymous access), and in it ");
            }
            else
            {
                AzureAccount a = cAccount.SelectedItem as AzureAccount;
                b.Append("in ").Append(a != null ? a.Name : "the account").Append(" ");
            }
            b.Append("the private container ").Append(Arm.AgentFiles).Append(", and the lifecycle rule ").Append(StorageSecurity.RuleName).Append(" that deletes uploads after ").Append(StorageSecurity.DayChoices[sDays.Selected]).Append(" days. ");
            if (gAsk.On) b.Append("Reads the account key once and keeps only a read link to ").Append(Arm.AgentFiles).Append(" on this PC");
            else b.Append("Reads the account key once and keeps it on this PC with a read link and a publish link to ").Append(Arm.AgentFiles);
            b.Append(", valid for 5 years. Makes the update key if this PC has none, publishes this TrackVice ").Append(version).Append(" if nothing is published yet, and checks the security of the account.");
            return b.ToString();
        }

        void Plan()
        {
            foreach (Control c in body.Controls)
            {
                SettingRow r = c as SettingRow;
                if (r != null && r.Accessory == bSetUp) { r.Desc = PlanText(); r.Invalidate(); }
            }
            Relayout();
        }

        Arm ArmApi()
        {
            Arm a = new Arm(web, armIn.Token);
            if (demo) a.Pause = delegate { };
            return a;
        }

        void SignInAzure()
        {
            if (busy && pending != null) { ShowCode(pending); return; }
            if (busy) return;
            WebSignIn s = SignIns.Get(web, Arm.ClientId, tTenant.Text, Arm.Scope);
            armIn = s;
            subs = new List<AzureSubscription>();
            List<AzureSubscription> got = null;
            List<FoundAccount> found = null;
            SignIn(s, delegate (string text) { armText = text; }, delegate
            {
                Arm a = new Arm(web, s.Token);
                got = a.Subscriptions();
                Ui(delegate { note.Say("Looking for the storage account your customers use...", false); });
                found = StorageFinder.Find(a, got, used);
            }, delegate
            {
                subs = got ?? new List<AzureSubscription>();
                if (subs.Count == 0) armText += " The account has no enabled subscriptions in this tenant. Check the Azure tenant.";
                int pick = 0;
                preselect = null;
                if (found != null && found.Count > 0)
                {
                    FoundAccount f = found[0];
                    for (int i = 0; i < subs.Count; i++) if (subs[i].Id == f.Subscription.Id) pick = i;
                    preselect = f.Account.Name;
                    armText += " Found " + f.Account.Name + ", which your customers use.";
                }
                filling = true;
                cSub.Items.Clear();
                foreach (AzureSubscription x in subs) cSub.Items.Add(x);
                filling = false;
                chosenSub = subs.Count > 0 ? subs[pick] : null;
                if (subs.Count > 0) cSub.SelectedIndex = pick;
            }, delegate { armText = ""; armIn = null; });
        }

        AzureSubscription chosenSub;
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
        string loadedSub = "";

        // Does not depend on the box raising its events: what the box shows is read when the window is free.
        void WatchSubscription()
        {
            if (busy || armIn == null || !armIn.SignedIn || subs.Count == 0) return;
            AzureSubscription s = cSub.SelectedItem as AzureSubscription;
            if (s == null || s.Id == loadedSub) return;
            chosenSub = s;
            LoadSubscription();
        }
        string diag = "";
        bool reloadSub;

        protected override void AfterFinish()
        {
            SyncSubscriptionBox();
            if (!reloadSub) return;
            reloadSub = false;
            LoadSubscription();
        }

        // The subscription the box shows: its text is what the person sees, so that decides.
        AzureSubscription ShownSubscription()
        {
            if (chosenSub != null) return chosenSub;
            return cSub.SelectedItem as AzureSubscription;
        }

        // The box must show the subscription that is read.
        void SyncSubscriptionBox()
        {
            if (chosenSub == null || cSub.SelectedItem == chosenSub) return;
            bool was = filling;
            filling = true;
            cSub.SelectedItem = chosenSub;
            filling = was;
        }

        void LoadSubscription()
        {
            AzureSubscription s = ShownSubscription();
            if (s == null || armIn == null || !armIn.SignedIn) return;
            // Another step is running: the subscription is read when it has ended, so the choice is not lost.
            if (busy) { reloadSub = true; return; }
            run.Subscription = s;
            loadedSub = s.Id;
            // An error about the subscription chosen before no longer applies.
            note.Say("", false);
            List<KeyValuePair<string, string>> groups = null;
            List<AzureAccount> accts = null;
            Work("Reading " + s.Name + "...", delegate
            {
                Arm a = ArmApi();
                groups = a.ResourceGroups(s.Id);
                accts = a.StorageAccounts(s.Id);
            }, delegate
            {
                diag = "Azure answered for " + s.Name + " (" + s.Id + "): " + groups.Count + " resource groups and " + accts.Count + " storage accounts, signed in as " + (string.IsNullOrEmpty(armIn.Account) ? "an unknown account" : armIn.Account) + ".";
                filling = true;
                string keep = cGroup.Text;
                cGroup.Items.Clear();
                foreach (KeyValuePair<string, string> g in groups) cGroup.Items.Add(g.Key);
                cGroup.Text = keep;
                accounts = accts;
                cAccount.Items.Clear();
                foreach (AzureAccount x in accts) cAccount.Items.Add(x);
                if (accts.Count > 0) cAccount.SelectedIndex = 0;
                bool pre = false;
                if (preselect != null)
                    for (int i = 0; i < accts.Count; i++)
                        if (string.Equals(accts[i].Name, preselect, StringComparison.OrdinalIgnoreCase)) { cAccount.SelectedIndex = i; pre = true; }
                preselect = null;
                filling = false;
                if (pre) sKind.Selected = 1;
            }, null);
        }

        void SetUp()
        {
            if (busy || run.Subscription == null) return;
            run.NewAccount = sKind.Selected == 0;
            if (run.NewAccount)
            {
                string rule = Arm.NameRule(tAccount.Text.Trim());
                if (rule != null) { note.Say(rule, true); return; }
                if (GroupName.Length == 0) { note.Say("Pick or enter a resource group.", true); return; }
                if (run.Account == null)
                {
                    run.AccountName = tAccount.Text.Trim();
                    run.ResourceGroup = GroupName;
                    run.NewGroup = !GroupExists(GroupName);
                    run.Region = RegionName.Length > 0 ? RegionName : Arm.DefaultRegion;
                }
            }
            else
            {
                if (!(cAccount.SelectedItem is AzureAccount)) { note.Say("Pick a storage account.", true); return; }
                run.Account = (AzureAccount)cAccount.SelectedItem;
            }
            if (!demo && MessageBox.Show(this, PlanText() + "\n\nSet this up in " + run.Subscription.Name + "?", "Your storage", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            run.AskKey = gAsk.On;
            run.RetentionDays = StorageSecurity.DayChoices[sDays.Selected];
            byte[] file;
            try { file = exe(); }
            catch (Exception ex) { note.Say("TrackVice.exe could not be read: " + ex.Message, true); return; }
            Arm arm = ArmApi();
            Work("Setting up...", delegate
            {
                run.Run(arm, web, keys, secrets, file, version, DateTime.UtcNow, delegate (string p) { Ui(delegate { note.Say(p, false); }); });
                Ui(delegate { note.Say("Checking the security of " + run.Account.Name + "...", false); });
                try
                {
                    ReadSecurity(arm, run.Result, run.Account);
                    run.Result.Save(secrets);
                }
                catch (SetupException ex) { run.Done.Add("The security check could not read the account: " + ex.Message); }
            }, delegate
            {
                bClose.Text = "Close";
                bClose.Primary = true;
                bClose.Invalidate();
            }, null);
        }
    }
}
