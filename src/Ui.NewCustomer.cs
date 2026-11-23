namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // The numbered steps at the top of the guided setup.
    public sealed class StepRail : FlatControl, IMeasured
    {
        readonly string[] names;
        int current;

        public StepRail(params string[] names)
        {
            this.names = names;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            Height = Dpi.S(58);
        }

        public int Current
        {
            get { return current; }
            set { current = value; Invalidate(); }
        }

        public int Measure(int width)
        {
            return Height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int n = names.Length;
            float slot = Width / (float)n;
            int d = Dpi.S(26);
            int cy = Dpi.S(4) + d / 2;
            using (Pen line = new Pen(T.Divider, Math.Max(1f, Dpi.S(1.5f))))
                for (int i = 0; i + 1 < n; i++)
                    g.DrawLine(line, slot * i + slot / 2 + d / 2 + Dpi.S(4), cy, slot * (i + 1) + slot / 2 - d / 2 - Dpi.S(4), cy);
            for (int i = 0; i < n; i++)
            {
                float cx = slot * i + slot / 2;
                RectangleF c = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
                Color fill = i == current ? T.Accent : i < current ? T.AccentSoft : T.Field;
                Color fg = i == current ? T.OnAccent : i < current ? T.Accent : T.Text3;
                using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, c);
                if (i > current) using (Pen p = new Pen(T.FieldStroke, 1f)) g.DrawEllipse(p, c);
                string mark = (i + 1).ToString(CultureInfo.InvariantCulture);
                Draw.Text(g, mark, Fonts.SmallSemibold, Rectangle.Round(c), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Rectangle lr = new Rectangle((int)(slot * i), cy + d / 2 + Dpi.S(4), (int)slot, Fonts.Small.Height + 2);
                Draw.Text(g, names[i], i == current ? Fonts.SmallSemibold : Fonts.Small, lr, i == current ? T.Text : T.Text2, Draw.Line | TextFormatFlags.HorizontalCenter);
            }
        }
    }

    // The page of the guided setup: its rows change from step to step, inside the one scrolling page.
    sealed class WizardBody : Panel, IThemed, IMeasured
    {
        Theme theme = Theme.Light();
        List<Control> items = new List<Control>();
        // Inputs that live across steps. They are taken out of a row before the row is thrown away.
        public readonly HashSet<Control> Keep = new HashSet<Control>();
        readonly HashSet<Control> hooked = new HashSet<Control>();

        public WizardBody()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetItems(List<Control> list)
        {
            SuspendLayout();
            List<Control> old = items;
            items = list;
            foreach (Control c in list)
            {
                Controls.Add(c);
                IThemed th = c as IThemed;
                if (th != null) th.ApplyTheme(theme);
                Wheel(c);
            }
            foreach (Control c in old)
            {
                if (list.Contains(c)) continue;
                Controls.Remove(c);
                Detach(c);
                c.Dispose();
            }
            ResumeLayout(false);
        }

        void Detach(Control c)
        {
            Control[] kids = new Control[c.Controls.Count];
            c.Controls.CopyTo(kids, 0);
            foreach (Control k in kids)
            {
                if (Keep.Contains(k)) c.Controls.Remove(k);
                else Detach(k);
            }
        }

        // The page scrolls with the wheel over any row.
        void Wheel(Control c)
        {
            if (c is NumericUpDown || c is ComboBox || c is DataGridView) return;
            if (hooked.Contains(c)) { foreach (Control k in c.Controls) Wheel(k); return; }
            hooked.Add(c);
            c.Disposed += delegate { hooked.Remove(c); };
            c.MouseWheel += delegate (object s, MouseEventArgs e)
            {
                ScrollableControl sc = null;
                for (Control p = Parent; p != null; p = p.Parent) { ScrollableControl x = p as ScrollableControl; if (x != null && x.AutoScroll) { sc = x; break; } }
                if (sc == null) return;
                HandledMouseEventArgs h = e as HandledMouseEventArgs;
                if (h != null) h.Handled = true;
                int y = -sc.AutoScrollPosition.Y - e.Delta / 120 * Dpi.S(80);
                sc.AutoScrollPosition = new Point(0, Math.Max(0, y));
            };
            foreach (Control k in c.Controls) Wheel(k);
        }

        public int Measure(int width)
        {
            int y = 0;
            foreach (Control c in items)
            {
                IMeasured m = c as IMeasured;
                int h = m != null ? m.Measure(width) : c.Height;
                if (c is SectionTitle && y > 0) y += Dpi.S(10);
                c.SetBounds(0, y, width, h);
                y += h + (c is SectionTitle ? Dpi.S(2) : Dpi.S(6));
            }
            return Math.Max(0, y - Dpi.S(6));
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            foreach (Control c in items) { IThemed th = c as IThemed; if (th != null) th.ApplyTheme(t); }
            foreach (Control c in Keep) { if (c.Parent == null) { c.BackColor = t.Field; c.ForeColor = t.Text; } }
        }
    }

    // New customer: sets up the storage, the links, the exe and the platform script for the TrackVice agent, one
    // confirmed step at a time. Signing in and listing change nothing; every button that creates something says what.
    public sealed class NewCustomerDialog : PageForm
    {
        public const string GuideUrl = "https://github.com/Baldissera88/TrackVice/blob/main/docs/AGENT.md#alerts-in-teams";
        static readonly string[] StepNames = { "Customer", "Storage", "Links", "Upload", "Alerts", "Script", "Done" };

        readonly IWeb web;
        readonly bool demo;
        readonly bool live;
        readonly CustomerBook book;
        readonly NewCustomerRun run = new NewCustomerRun();
        readonly StepRail rail = new StepRail(StepNames);
        readonly WizardBody body = new WizardBody();
        readonly FlatButton bBack = new FlatButton("Back", null);
        readonly FlatButton bNext = new FlatButton("Next", null);
        readonly FooterNote note = new FooterNote();
        readonly ManualResetEvent cancel = new ManualResetEvent(false);
        int page;
        bool busy;
        Customer before;

        // Inputs kept across steps.
        readonly TextBox tName, tTenant, tAzTenant, tClient, tAccount, tAlert, tSearch;
        readonly ComboBox cSub, cGroup, cRegion, cAccount, cFound;
        readonly Segmented sKind = new Segmented("New account", "Existing account");
        readonly Segmented sValid = new Segmented("6 months", "1 year", "2 years");
        readonly NumericUpDown nDays, nMaxAge;
        readonly FlatButton bArm, bCreate, bLinks, bExe, bUpload, bGuide, bTest, bSave, bGraph, bSearch, bIntune;

        WebSignIn armIn, graphIn;
        DeviceCode armCode, graphCode;
        string armText = "", storageText = "", nameText = "", linksText = "", uploadText = "", alertText = "", scriptText = "", graphText = "", intuneText = "";
        List<AzureSubscription> subs = new List<AzureSubscription>();
        List<AzureAccount> accounts = new List<AzureAccount>();
        List<DirectoryGroup> found = new List<DirectoryGroup>();
        bool filling;

        // The public update key of this PC, or empty. Filled in as $UpdateKey.
        public Func<string> UpdateKey;
        // Saves the link TrackVice.exe was uploaded with as the customer's publish location for agent updates.
        public Action<string, string> SavePublishLink;

        // What is ticked in the What you need box of step 1.
        readonly HashSet<string> ticks = new HashSet<string>();

        public NewCustomerDialog(Theme t, CustomerBook book, IWeb web, bool isDemo) : base("New customer", t)
        {
            this.book = book;
            this.web = web;
            demo = isDemo;
            live = book == CustomerUi.Book;
            Size = new Size(Dpi.S(880), Dpi.S(780));

            tName = Field(300, false);
            tTenant = Field(300, false);
            tAzTenant = Field(300, false);
            tClient = Field(300, true);
            tClient.Text = Arm.ClientId;
            tAccount = Field(220, true);
            tAccount.MaxLength = 24;
            tAccount.CharacterCasing = CharacterCasing.Lower;
            tAccount.Leave += delegate { CheckName(); };
            tAlert = Field(360, true);
            tSearch = Field(200, false);
            cSub = Combo(360, true);
            cGroup = Combo(260, false);
            cRegion = Combo(200, false);
            foreach (string r in Arm.Regions) cRegion.Items.Add(r);
            cRegion.Text = Arm.DefaultRegion;
            cAccount = Combo(360, true);
            cFound = Combo(300, true);
            nDays = Number(1, 3650, 30);
            nMaxAge = Number(0, 720, 24);
            sValid.Selected = 1;
            body.Keep.Add(sKind);
            body.Keep.Add(sValid);

            bArm = Button("Sign in", delegate { SignIn(true); });
            bCreate = Button("Create", delegate { CreateStorage(); });
            bCreate.Primary = true;
            bLinks = Button("Make the links", delegate { MakeLinks(); });
            bLinks.Primary = true;
            bExe = Button("Browse", delegate { BrowseExe(); });
            bUpload = Button("Upload", delegate { Upload(); });
            bUpload.Primary = true;
            bGuide = Button("Open the guide", delegate { Open(GuideUrl); });
            bTest = Button("Send a test", delegate { TestAlert(); });
            bSave = Button("Save as", delegate { SaveScript(); });
            bSave.Primary = true;
            bGraph = Button("Sign in", delegate { SignIn(false); });
            bSearch = Button("Search", delegate { SearchGroups(); });
            bIntune = Button("Add to Intune", delegate { AddToIntune(); });

            cSub.SelectedIndexChanged += delegate { if (!filling) LoadSubscription(); };
            sKind.Changed += delegate { run.NewAccount = sKind.Selected == 0; Rebuild(); };
            cGroup.TextChanged += delegate { if (!filling) Plan(); };
            cRegion.TextChanged += delegate { if (!filling) Plan(); };
            tAccount.TextChanged += delegate { if (!filling) { nameText = ""; Plan(); } };
            cAccount.SelectedIndexChanged += delegate { if (!filling) Plan(); };
            nDays.ValueChanged += delegate { if (!filling) Plan(); };
            tSearch.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SearchGroups(); } };

            run.ExePath = Program.Location;

            Add(rail);
            Add(body);

            bClose.Text = "Cancel";
            bClose.Primary = false;
            bClose.ApplyTheme(t);
            bNext.Primary = true;
            bNext.Width = Math.Max(bNext.Width, Dpi.S(96));
            bNext.Dock = DockStyle.Right;
            bNext.Click += Safe.Wrap(delegate { Next(); });
            bBack.Width = Math.Max(bBack.Width, Dpi.S(96));
            bBack.Dock = DockStyle.Right;
            bBack.Click += Safe.Wrap(delegate { if (!busy && page > 0) Go(page - 1); });
            Panel gap1 = new Panel(), gap2 = new Panel();
            gap1.Dock = gap2.Dock = DockStyle.Right;
            gap1.Width = gap2.Width = Dpi.S(8);
            note.Dock = DockStyle.Fill;
            // Docked right in this order: Cancel, Back, Next from left to right.
            footer.Controls.Add(gap1);
            footer.Controls.Add(bBack);
            footer.Controls.Add(gap2);
            footer.Controls.Add(bNext);
            footer.Controls.Add(note);
            note.BringToFront();
            foreach (Control c in footer.Controls) { IThemed th = c as IThemed; if (th != null) th.ApplyTheme(t); }
            AcceptButton = null;
            FormClosed += delegate { cancel.Set(); };
            Shown += delegate { if (page == 0) { tName.Focus(); } };
            Go(0);
        }

        // The demo, with made-up answers. tab picks the step to show, with the steps before it already done.
        public static NewCustomerDialog ForDemo(Theme t, int tab)
        {
            NewCustomerDialog d = new NewCustomerDialog(t, CustomerBook.Demo(), new DemoSetupWeb(), true);
            d.DemoTo(Math.Max(0, Math.Min(StepNames.Length - 1, tab)));
            return d;
        }

        public static NewCustomerDialog ForLive(Theme t)
        {
            NewCustomerDialog d = new NewCustomerDialog(t, CustomerUi.Book, YourStorageUi.Web, false);
            d.UpdateKey = delegate
            {
                try
                {
                    using (RSACryptoServiceProvider k = UpdateKeys.Load(new DpapiKeyStore())) return k == null ? "" : UpdateKeys.PublicKey(k);
                }
                catch (Exception) { return ""; }
            };
            d.SavePublishLink = delegate (string customer, string link) { Secret.Save("AgentUpdateStore." + customer, link); };
            return d;
        }

        void DemoTo(int target)
        {
            tName.Text = "Northwind Traders";
            tTenant.Text = "northwind.onmicrosoft.com";
            if (target >= 1) Next();
            if (target >= 1) SignIn(true);
            if (target >= 2) { CreateStorage(); Next(); MakeLinks(); }
            if (target >= 3) { Next(); Upload(); }
            if (target >= 4) Next();
            if (target >= 5) { tAlert.Text = "https://northwind.example/workflows/trackvice-alerts"; Next(); SignIn(false); tSearch.Text = "Autopilot"; SearchGroups(); }
            if (target >= 6) { AddToIntune(); Next(); }
            note.Say("", false);
        }

        // ---------- controls ----------

        TextBox Field(int width, bool mono)
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

        ComboBox Combo(int width, bool list)
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

        NumericUpDown Number(int min, int max, int value)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = value;
            n.Width = Dpi.S(90);
            n.BorderStyle = BorderStyle.FixedSingle;
            n.Font = Fonts.Ui;
            body.Keep.Add(n);
            return n;
        }

        FlatButton Button(string text, EventHandler click)
        {
            FlatButton b = SmallButton(text, click);
            body.Keep.Add(b);
            return b;
        }

        static void Open(string url)
        {
            try { Process.Start(url); }
            catch (Exception) { Menus.Copy(url); }
        }

        SettingRow Row(string glyph, string title, string desc, Control right)
        {
            return new SettingRow(glyph, title, desc, right);
        }

        ButtonStrip Strip(params Control[] items)
        {
            ButtonStrip s = new ButtonStrip(items);
            s.Fit();
            return s;
        }

        // ---------- navigation ----------

        void Go(int p)
        {
            page = p;
            rail.Current = p;
            Rebuild();
            Nav();
        }

        void Rebuild()
        {
            List<Control> l = Build(page);
            body.SetItems(l);
            body.ApplyTheme(theme);
            Relayout();
        }

        void Nav()
        {
            bool last = page == StepNames.Length - 1;
            bBack.Visible = page > 0;
            bBack.Enabled = !busy;
            bClose.Visible = !last;
            bNext.Text = last ? "Finish" : page == 5 ? "Save and finish" : page == 4 && tAlert.Text.Trim().Length == 0 ? "Skip" : "Next";
            bNext.Width = Math.Max(bNext.Width, Dpi.S(96));
            bNext.Enabled = !busy && CanLeave();
            bNext.Invalidate();
            bBack.Invalidate();
        }

        bool CanLeave()
        {
            switch (page)
            {
                case 1: return run.StorageReady;
                case 2: return run.Links != null;
                case 3: return run.ExeSha256.Length > 0;
                default: return true;
            }
        }

        void Next()
        {
            if (busy) return;
            if (page == 0) { if (!TakeCustomer()) return; }
            else if (page == 4)
            {
                string a = tAlert.Text.Trim();
                if (a.Length > 0 && AlertSender.Problem(a) != null) { note.Say(AlertSender.Problem(a), true); return; }
                run.Alert = a;
            }
            else if (page == 5) { if (!SaveCustomer()) return; }
            else if (page == StepNames.Length - 1) { DialogResult = DialogResult.OK; Close(); return; }
            if (!CanLeave()) return;
            note.Say("", false);
            Go(page + 1);
        }

        // Runs a step away from the window. In the demo it runs at once, so a screenshot shows the result.
        void Work(string doing, Action job, Action done)
        {
            Work(doing, job, done, null);
        }

        void Work(string doing, Action job, Action done, Action failed)
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
            catch (SetupException ex) { return ex.Message; }
            catch (GraphException ex) { return ex.Message; }
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
        }

        // Posts to the window from a step that is still running.
        void Ui(Action a)
        {
            if (demo || !InvokeRequired) { a(); return; }
            try { BeginInvoke((MethodInvoker)delegate { a(); }); }
            catch (Exception) { }
        }

        // ---------- the steps ----------

        List<Control> Build(int p)
        {
            List<Control> l = new List<Control>();
            switch (p)
            {
                case 0: BuildCustomer(l); break;
                case 1: BuildStorage(l); break;
                case 2: BuildLinks(l); break;
                case 3: BuildUpload(l); break;
                case 4: BuildAlerts(l); break;
                case 5: BuildScript(l); break;
                default: BuildDone(l); break;
            }
            return l;
        }

        void BuildCustomer(List<Control> l)
        {
            l.Add(new SectionTitle("1. Customer and tenant"));
            l.Add(Row(Glyphs.Info, "What this does", "Sets up what the TrackVice agent needs for a new customer: a storage account, the links, TrackVice.exe and the platform script. Each step says what it does, and nothing is created or changed until you press its button. Signing in and listing change nothing.", null));
            NeedsBox needs = new NeedsBox("What you need", SetupNeeds.BoxIntro, SetupNeeds.LinksChecklist(), ticks, "Roles and consent as a table");
            needs.MoreClicked += delegate { Open(AddCustomerUi.AgentDoc + "#what-you-need"); };
            l.Add(needs);
            l.Add(Row(Glyphs2.Device, "Customer name", "Shown at the top of the device lists. When a customer with this name exists, it is used and its details are kept until the last step.", tName));
            l.Add(Row(Glyphs.Shield, "Tenant", "The tenant ID or domain of the customer's Intune, such as contoso.onmicrosoft.com. The sign-ins in the next steps go to it.", tTenant));
            l.Add(Row(Glyphs2.Help, "Next", "Adds the customer to your list on this PC, or picks the one with this name.", null));
        }

        bool TakeCustomer()
        {
            string name = tName.Text.Trim();
            Customer have = book.Find(name);
            string problem = have != null ? null : book.NameProblem(name, null);
            if (problem != null) { note.Say(problem, true); tName.Focus(); return false; }
            string tenant = tTenant.Text.Trim();
            if (tenant.Length == 0) { note.Say("Enter the tenant of the customer, such as contoso.onmicrosoft.com.", true); tTenant.Focus(); return false; }
            if (before == null) before = book.Current == null ? null : book.Current.Copy();
            if (have == null)
            {
                Customer c = new Customer();
                c.Name = name;
                c.Tenant = tenant;
                have = book.Add(c);
                if (live) book.Save();
            }
            run.Customer = have.Name;
            run.Tenant = tenant;
            if (tAzTenant.Text.Trim().Length == 0) tAzTenant.Text = tenant;
            if (tAccount.Text.Length == 0) { filling = true; tAccount.Text = Arm.Suggest(have.Name, 0); filling = false; }
            if (cGroup.Text.Length == 0) { filling = true; cGroup.Text = "rg-trackvice"; filling = false; }
            return true;
        }

        void BuildStorage(List<Control> l)
        {
            l.Add(new SectionTitle("2. Azure storage"));
            l.Add(Row(Glyphs.Info, "What this does", "Signs in to Azure and makes a storage account with two private containers: " + Arm.Sessions + " for the sessions and logs the agents upload, and " + Arm.AgentFiles + " for TrackVice.exe. The account does not have to be in the customer's tenant.", null));
            l.Add(new SectionTitle("Sign in"));
            l.Add(Row(Glyphs.Shield, "Azure tenant", "Where the subscription is. Empty signs in to the tenant of your account.", tAzTenant));
            l.Add(Row(Glyphs2.Code, "Client ID", "The app to sign in with. The default is Azure PowerShell, which may use Azure Resource Manager. Enter your own app registration if your organization blocks it.", tClient));
            bArm.Text = armCode != null && armIn != null && !armIn.SignedIn ? "Copy code and open" : armIn != null && armIn.SignedIn ? "Sign in again" : "Sign in";
            l.Add(Row(Glyphs2.Sync, "Sign in to Azure", armText.Length > 0 ? armText : "With a code in the browser. Reads your subscriptions; nothing is changed.", bArm));
            if (armIn == null || !armIn.SignedIn || subs.Count == 0) return;
            l.Add(new SectionTitle("Storage account"));
            AzureSubscription sub = cSub.SelectedItem as AzureSubscription;
            l.Add(Row(Glyphs2.Settings, "Subscription", sub != null ? "ID " + sub.Id : "", cSub));
            l.Add(Row(Glyphs2.Folder, "Account", "A new account just for TrackVice, or an existing one that gets the two containers.", sKind));
            if (run.NewAccount)
            {
                l.Add(Row(Glyphs2.Folder, "Resource group", "Pick one, or type a name to make a new group.", cGroup));
                l.Add(Row(Glyphs2.Apps, "Name", nameText.Length > 0 ? nameText : "3 to 24 lowercase letters and digits, unique in all of Azure. Checked with Azure before it is made.", tAccount));
                l.Add(Row(Glyphs.Info, "Region", "Where the data is kept.", cRegion));
            }
            else l.Add(Row(Glyphs2.Folder, "Existing account", accounts.Count == 0 ? "This subscription has no storage accounts." : "Its settings are kept. Only the cleanup rule is added.", cAccount));
            l.Add(Row(Glyphs2.Calendar, "Delete sessions and logs after", "Days. A lifecycle rule deletes the blobs under " + Arm.Sessions + "/sessions/ and " + Arm.Sessions + "/logs/ when they are older.", nDays));
            bCreate.Text = run.NewAccount ? "Create" : "Set up";
            bCreate.Enabled = !run.StorageReady;
            l.Add(Row(Glyphs.Save, run.StorageReady ? "Done" : run.NewAccount ? "Create" : "Set up", run.StorageReady ? storageText : PlanText(), bCreate));
        }

        string RegionName
        {
            get { return (cRegion.Text ?? "").Trim().ToLowerInvariant().Replace(" ", ""); }
        }

        string GroupName
        {
            get { return (cGroup.Text ?? "").Trim(); }
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
                b.Append("the storage account ").Append(tAccount.Text.Trim()).Append(" in ").Append(GroupName.Length > 0 ? GroupName : "a resource group").Append(" (StorageV2, Standard_LRS, ").Append(RegionName)
                    .Append(", HTTPS only, TLS 1.2 or later, no anonymous access, account key access on), ");
            }
            else
            {
                AzureAccount a = cAccount.SelectedItem as AzureAccount;
                b.Append("in ").Append(a != null ? a.Name : "the account").Append(" ");
            }
            b.Append("the private containers ").Append(Arm.Sessions).Append(" and ").Append(Arm.AgentFiles)
                .Append(", and a lifecycle rule that deletes sessions and logs after ").Append((int)nDays.Value).Append(" days. Other lifecycle rules are kept.");
            return b.ToString();
        }

        void Plan()
        {
            if (page != 1) return;
            foreach (Control c in body.Controls)
            {
                SettingRow r = c as SettingRow;
                if (r != null && r.Accessory == bCreate && !run.StorageReady) { r.Desc = PlanText(); r.Invalidate(); }
                if (r != null && r.Accessory == tAccount) { r.Desc = nameText.Length > 0 ? nameText : "3 to 24 lowercase letters and digits, unique in all of Azure. Checked with Azure before it is made."; r.Invalidate(); }
            }
            Relayout();
        }

        Arm ArmApi()
        {
            Arm a = new Arm(web, armIn.Token);
            if (demo) a.Pause = delegate { };
            return a;
        }

        void SignIn(bool azure)
        {
            WebSignIn current = azure ? armIn : graphIn;
            DeviceCode code = azure ? armCode : graphCode;
            if (busy && current != null && !current.SignedIn && code != null)
            {
                bool copied = Menus.Copy(code.UserCode);
                Open(code.VerificationUri);
                note.Say(copied ? "The code is copied. Paste it on the page that opened, and sign in." : "Enter the code on the page that opened, and sign in.", false);
                return;
            }
            if (busy) return;
            WebSignIn s;
            if (azure)
            {
                string id = tClient.Text.Trim();
                if (id.Length > 0 && !GraphClient.IsGuid(id)) { note.Say("The client ID is a GUID, such as 1950a258-227b-4e31-a9cf-717495945fc2.", true); return; }
                s = SignIns.Get(web, id.Length > 0 ? id : Arm.ClientId, tAzTenant.Text, Arm.Scope);
                armIn = s;
                armCode = null;
                subs = new List<AzureSubscription>();
                armText = s.SignedIn ? "Signed in" + (string.IsNullOrEmpty(s.Account) ? "." : " as " + s.Account + ".") : "Asking for a sign-in code...";
            }
            else
            {
                Customer c = book.Find(run.Customer);
                s = SignIns.Get(web, c != null ? c.GraphClientId : null, run.Tenant, IntuneScripts.ScriptScope + " " + IntuneScripts.GroupScope);
                graphIn = s;
                graphCode = null;
                found = new List<DirectoryGroup>();
                graphText = s.SignedIn ? "Signed in" + (string.IsNullOrEmpty(s.Account) ? "." : " as " + s.Account + ".") : "Asking for a sign-in code...";
            }
            Rebuild();
            List<AzureSubscription> got = null;
            Work("Signing in...", delegate
            {
                if (!s.SignedIn && !s.Quiet())
                {
                    DeviceCode d = s.Start();
                    Ui(delegate
                    {
                        string text = "Open " + d.VerificationUri + " and enter the code " + d.UserCode + ". TrackVice waits for the sign-in.";
                        if (azure) { armCode = d; armText = text; } else { graphCode = d; graphText = text; }
                        Rebuild();
                        if (!demo) SignIn(azure);
                    });
                    s.Wait(d, cancel);
                }
                if (azure) got = new Arm(web, s.Token).Subscriptions();
            }, delegate
            {
                string who = string.IsNullOrEmpty(s.Account) ? "Signed in" : "Signed in as " + s.Account;
                if (azure)
                {
                    subs = got ?? new List<AzureSubscription>();
                    armText = subs.Count == 0 ? who + ". The account has no enabled subscriptions in this tenant. Check the Azure tenant." : who + ". Nothing was changed.";
                    filling = true;
                    cSub.Items.Clear();
                    foreach (AzureSubscription x in subs) cSub.Items.Add(x);
                    filling = false;
                    if (subs.Count > 0) cSub.SelectedIndex = 0;
                }
                else graphText = who + ". It can add platform scripts and read group names.";
            }, delegate
            {
                if (azure) { armText = ""; armCode = null; armIn = null; }
                else { graphText = ""; graphCode = null; graphIn = null; }
            });
        }

        // Reads the resource groups and accounts of the subscription picked.
        void LoadSubscription()
        {
            AzureSubscription s = cSub.SelectedItem as AzureSubscription;
            if (s == null || armIn == null || !armIn.SignedIn) return;
            run.Subscription = s;
            List<KeyValuePair<string, string>> groups = null;
            List<AzureAccount> accts = null;
            string name = tAccount.Text.Trim();
            string problem = null;
            Work("Reading " + s.Name + "...", delegate
            {
                Arm a = ArmApi();
                groups = a.ResourceGroups(s.Id);
                accts = a.StorageAccounts(s.Id);
                if (Arm.NameRule(name) == null) problem = a.NameProblem(s.Id, name);
            }, delegate
            {
                if (Arm.NameRule(name) == null) nameText = problem ?? "The name " + name + " is free. Checked again before it is made.";
                filling = true;
                string keep = cGroup.Text;
                cGroup.Items.Clear();
                foreach (KeyValuePair<string, string> g in groups) cGroup.Items.Add(g.Key);
                cGroup.Text = keep;
                accounts = accts;
                cAccount.Items.Clear();
                foreach (AzureAccount x in accts) cAccount.Items.Add(x);
                if (accts.Count > 0) cAccount.SelectedIndex = 0;
                filling = false;
            });
        }

        // Only the rule here: a check with Azure on leaving the box would hold up a click on Create.
        void CheckName()
        {
            if (run.StorageReady) return;
            string rule = Arm.NameRule(tAccount.Text.Trim());
            if (rule != null) { nameText = rule; Plan(); }
        }

        void CreateStorage()
        {
            if (busy) return;
            run.NewAccount = sKind.Selected == 0;
            run.RetentionDays = (int)nDays.Value;
            if (run.NewAccount)
            {
                string rule = Arm.NameRule(tAccount.Text.Trim());
                if (rule != null) { note.Say(rule, true); return; }
                if (GroupName.Length == 0) { note.Say("Pick or enter a resource group.", true); return; }
                run.AccountName = tAccount.Text.Trim();
                run.ResourceGroup = GroupName;
                run.NewGroup = !GroupExists(GroupName);
                run.Region = RegionName.Length > 0 ? RegionName : Arm.DefaultRegion;
            }
            else if (!(cAccount.SelectedItem is AzureAccount)) { note.Say("Pick a storage account.", true); return; }
            if (!demo && MessageBox.Show(this, PlanText() + "\n\nCreate this in " + (run.Subscription != null ? run.Subscription.Name : "the subscription") + "?", "Azure storage", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (!run.NewAccount) run.Account = (AzureAccount)cAccount.SelectedItem;
            int from = run.Done.Count;
            Arm a = ArmApi();
            Work("Creating...", delegate
            {
                run.SetUpStorage(a, delegate (string p) { Ui(delegate { note.Say(p, false); }); });
            }, delegate
            {
                storageText = string.Join("\n", run.Done.GetRange(from, run.Done.Count - from).ToArray());
            });
        }

        void BuildLinks(List<Control> l)
        {
            l.Add(new SectionTitle("3. Links"));
            l.Add(Row(Glyphs.Info, "What this does", "Reads the account key of " + (run.Account != null ? run.Account.Name : "the account") + " and signs the links on this PC. The key is not shown and not saved. The links are shared access signatures for HTTPS only, each for one container or file.", null));
            l.Add(Row(Glyphs2.Calendar, "Valid for", "Make new links before they expire, see Maintenance in docs/AGENT.md.", sValid));
            bLinks.Enabled = run.Links == null;
            l.Add(Row(Glyphs.Shield, run.Links == null ? "Make the links" : "Done", linksText.Length > 0 ? linksText : "Reads the account key once. Nothing in Azure is changed.", bLinks));
            if (run.Links == null) return;
            l.Add(new SectionTitle("The links"));
            l.Add(LinkRow("Upload", "Create and Write on " + Arm.Sessions + ". Goes into the script as $Upload.", run.Links.Upload));
            l.Add(LinkRow("Viewer", "Read and List on " + Arm.Sessions + ". Saved encrypted in the customer in the last step. Treat it like a password.", run.Links.Viewer));
            l.Add(LinkRow("Download", "Read on " + Arm.AgentFiles + "/TrackVice.exe. Goes into the script as $Download.", run.Links.Download));
            l.Add(LinkRow("Agent updates", "Read on " + Arm.AgentFiles + ". Goes into the script as $UpdateLink when this PC has an update key.", run.Links.UpdateRead));
            l.Add(LinkRow("Publish", "Create and Write on " + Arm.AgentFiles + ". Uploads TrackVice.exe in the next step, and is saved for Publish agent update.", run.Links.Publish));
        }

        SettingRow LinkRow(string title, string what, string link)
        {
            FlatButton copy = SmallButton("Copy", delegate
            {
                note.Say(Menus.Copy(link) ? "Copied the " + title.ToLowerInvariant() + " link. Keep it like a password." : "The link could not be copied.", false);
            });
            copy.Glyph = Glyphs.Copy;
            copy.AutoWidth();
            copy.Width += Dpi.S(8);
            SettingRow r = Row(Glyphs.Shield, title, what + "\n" + SasLinks.Masked(link), copy);
            return r;
        }

        int LinkDays
        {
            get { return sValid.Selected == 0 ? 182 : sValid.Selected == 2 ? 730 : 365; }
        }

        void MakeLinks()
        {
            if (busy || run.Account == null) return;
            run.LinkDays = LinkDays;
            Arm a = ArmApi();
            Work("Reading the account key and signing the links...", delegate { run.MakeLinks(a, DateTime.UtcNow); }, delegate
            {
                linksText = "Made five links, valid until " + run.Links.ExpiresUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) + ". The key is not kept.";
            });
        }

        void BuildUpload(List<Control> l)
        {
            l.Add(new SectionTitle("4. Upload TrackVice.exe"));
            l.Add(Row(Glyphs.Info, "What this does", "Uploads TrackVice.exe to " + Arm.AgentFiles + ", where devices download it with the download link. The script checks its SHA-256 before it runs it.", null));
            string v = File.Exists(run.ExePath) ? SemVer.OfFile(run.ExePath) : null;
            l.Add(Row(Glyphs2.Apps, "TrackVice.exe", run.ExePath + (v != null ? " · version " + v : ""), bExe));
            bUpload.Text = run.ExeSha256.Length > 0 ? "Upload again" : "Upload";
            l.Add(Row(Glyphs2.Sync, run.ExeSha256.Length > 0 ? "Uploaded" : "Upload", uploadText.Length > 0 ? uploadText : "Puts the file in " + Arm.AgentFiles + " as TrackVice.exe, over one that is there.", bUpload));
        }

        void BrowseExe()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "TrackVice|TrackVice*.exe|Programs|*.exe";
                d.Title = "Choose the TrackVice.exe for the devices";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                run.ExePath = d.FileName;
                run.ExeSha256 = "";
                uploadText = "";
            }
            Rebuild();
            Nav();
        }

        void Upload()
        {
            if (busy || run.Links == null) return;
            byte[] data;
            try { data = demo ? Encoding.ASCII.GetBytes("MZ TrackVice demo") : File.ReadAllBytes(run.ExePath); }
            catch (Exception ex) { note.Say("TrackVice.exe could not be read: " + ex.Message, true); return; }
            if (!demo && MessageBox.Show(this, "Upload " + run.ExePath + " to " + Arm.AgentFiles + " in " + run.Account.Name + " as TrackVice.exe?\n\nIt replaces a TrackVice.exe that is there. Devices whose script has another $Sha256 then stop installing the agent until that script is updated.", "Upload TrackVice.exe", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Work("Uploading TrackVice.exe...", delegate { run.Upload(web, data); }, delegate
            {
                uploadText = "SHA-256 " + run.ExeSha256 + ". " + (demo ? "" : (run.ExeSize / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB. ")
                    + (run.DownloadChecked ? "The download link reads it, as a device will." : "The download link could not read it yet. Check the storage account's network settings.");
            });
        }

        void BuildAlerts(List<Control> l)
        {
            l.Add(new SectionTitle("5. Alerts in Teams (optional)"));
            l.Add(Row(Glyphs.Info, "What this does", "With an alert link the agent posts to a Teams channel when something fails and when a deployment is finished. Leave the link empty to skip this.", null));
            l.Add(Row(Glyphs2.Help, "Make the link", "In Teams, open the channel > ... > Workflows > Post to a channel when a webhook request is received. Copy the link it shows.", bGuide));
            l.Add(Row(Glyphs.Shield, "Alert link", "Anyone with it can post to the channel. It goes into the script as $Alert.", tAlert));
            l.Add(Row(Glyphs2.Sync, "Test", alertText.Length > 0 ? alertText : "Posts one test card to the channel, as /alert-test does.", bTest));
        }

        void TestAlert()
        {
            string url = tAlert.Text.Trim();
            string problem = AlertSender.Problem(url);
            if (problem != null) { note.Say(problem, true); return; }
            if (demo) { alertText = "The demo sends nothing."; Rebuild(); return; }
            Alert a = new Alert();
            a.Key = "test";
            a.Level = "good";
            a.Title = run.Customer + ": test alert";
            a.Text = "The TrackVice agent can send alerts to this address. It sends one when something fails and one when a deployment is finished.";
            a.Facts.Add(new Pair("Customer", run.Customer));
            a.Facts.Add(new Pair("Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            Work("Sending a test alert...", delegate { AlertSender.Send(url, a); }, delegate
            {
                run.AlertTested = true;
                alertText = "The test alert was sent. Check that it arrived in the channel.";
            });
        }

        bool HasUpdateKey
        {
            get { return run.UpdateKey.Length > 0; }
        }

        void BuildScript(List<Control> l)
        {
            if (UpdateKey != null && run.UpdateKey.Length == 0) run.UpdateKey = UpdateKey() ?? "";
            run.MaxAgeHours = (int)nMaxAge.Value;
            l.Add(new SectionTitle("6. The platform script"));
            l.Add(Row(Glyphs.Info, "What this does", "Fills in Install-TrackViceAgent-Links.ps1: the download link, the SHA-256, the upload link, the alert link and $MaxAgeHours. The script installs the agent on a device during enrollment.", null));
            l.Add(Row(Glyphs2.Clock, "$MaxAgeHours", "Only devices enrolled within this many hours install the agent, so assigning the script to older devices does nothing. 0 installs on every device.", nMaxAge));
            l.Add(Row(Glyphs.Shield, "Agent updates", HasUpdateKey
                ? "The update key of this PC goes in as $UpdateKey and the read link of " + Arm.AgentFiles + " as $UpdateLink. The agents install versions you publish."
                : "This PC has no update key, so $UpdateKey stays empty. Settings > Publish agent update makes one.", null));
            l.Add(Row(Glyphs.Save, "Save the script", scriptText.Length > 0 ? scriptText : "Saves the filled script where you choose. It holds the upload and download links: keep it like a password.", bSave));
            l.Add(new SectionTitle("Intune (optional)"));
            l.Add(Row(Glyphs.Info, "This changes Intune", "It is the only step that does, and it is optional. It adds the platform script TrackVice agent (system context, no signature check, 64-bit PowerShell) and assigns it to the device group you pick. Or skip it and add the saved script by hand, as step 6 in docs/AGENT.md says.", null));
            bGraph.Text = graphCode != null && graphIn != null && !graphIn.SignedIn ? "Copy code and open" : graphIn != null && graphIn.SignedIn ? "Sign in again" : "Sign in";
            l.Add(Row(Glyphs2.Sync, "Sign in to Intune", graphText.Length > 0 ? graphText : "To " + run.Tenant + " with DeviceManagementScripts.ReadWrite.All and GroupMember.Read.All. Signing in changes nothing.", bGraph));
            if (graphIn == null || !graphIn.SignedIn) return;
            l.Add(Row(Glyphs.Search, "Device group", "Search by name, then pick the group. Start with a group that has one test device.", Strip(tSearch, bSearch)));
            if (found.Count > 0) l.Add(Row(Glyphs2.Device, "Group", "", cFound));
            bIntune.Primary = run.IntuneScriptId.Length == 0;
            bIntune.Enabled = found.Count > 0;
            l.Add(Row(Glyphs2.Apps, "Add to Intune", intuneText.Length > 0 ? intuneText : "Adds the platform script and assigns it to the group.", bIntune));
        }

        void SaveScript()
        {
            run.MaxAgeHours = (int)nMaxAge.Value;
            string text;
            try { text = run.Script(); }
            catch (SetupException ex) { note.Say(ex.Message, true); return; }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "PowerShell script (*.ps1)|*.ps1";
                d.FileName = AgentScript.FileName(run.Customer);
                d.Title = "Save the platform script";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, text, new UTF8Encoding(false));
                run.ScriptPath = d.FileName;
            }
            scriptText = "Saved to " + run.ScriptPath + ". It holds the upload and download links: keep it like a password.";
            Rebuild();
        }

        void SearchGroups()
        {
            if (busy || graphIn == null || !graphIn.SignedIn) return;
            string q = tSearch.Text.Trim();
            List<DirectoryGroup> got = null;
            Work("Searching the groups...", delegate { got = new IntuneScripts(web, graphIn.Token).FindGroups(q); }, delegate
            {
                found = got;
                cFound.Items.Clear();
                foreach (DirectoryGroup g in got) cFound.Items.Add(g);
                if (got.Count > 0) cFound.SelectedIndex = 0;
                else note.Say("No group has a name with " + q + ".", true);
            });
        }

        void AddToIntune()
        {
            if (busy || graphIn == null || !graphIn.SignedIn) return;
            DirectoryGroup g = cFound.SelectedItem as DirectoryGroup;
            if (g == null) { note.Say("Search for the device group and pick it.", true); return; }
            run.MaxAgeHours = (int)nMaxAge.Value;
            if (!demo && MessageBox.Show(this, "Add the platform script TrackVice agent to Intune in " + run.Tenant + " and assign it to " + g.Name + "?\n\nDevices in the group run it at their next check-in, also devices enrolled earlier when $MaxAgeHours is 0.", "Intune", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            run.Group = g;
            IntuneScripts api = new IntuneScripts(web, graphIn.Token);
            Work("Adding the platform script...", delegate { run.AddToIntune(api); }, delegate
            {
                intuneText = "Added the platform script TrackVice agent and assigned it to " + g.Name + ".";
            });
        }

        // Saves the viewer link, encrypted, and the tenant in the customer.
        bool SaveCustomer()
        {
            Customer c = book.Find(run.Customer);
            if (c == null) { note.Say("The customer " + run.Customer + " is gone. Go back to the first step.", true); return false; }
            if (run.Links == null) return false;
            Customer d = c.Copy();
            d.Tenant = run.Tenant;
            d.AgentLocation = run.Links.Viewer;
            book.Update(c.Name, d);
            if (live)
            {
                book.Save();
                CustomerUi.Edited(before);
            }
            if (SavePublishLink != null) SavePublishLink(c.Name, run.Links.Publish);
            return true;
        }

        void BuildDone(List<Control> l)
        {
            l.Add(new SectionTitle("7. Done"));
            l.Add(Row(Glyphs2.Device, run.Customer, "The tenant " + run.Tenant + " and the viewer link are saved in the customer, the link encrypted for your Windows account. The publish link is saved for Publish agent update.", null));
            l.Add(Row(Glyphs.Save, "Made", string.Join("\n", run.Done.ToArray()), null));
            List<string> open = new List<string>();
            if (run.ScriptPath.Length == 0) open.Add("The script was not saved. Go back to step 6 to save it.");
            else open.Add("The script is in " + run.ScriptPath + ".");
            if (run.IntuneScriptId.Length == 0) open.Add("The script is not in Intune. Add it as a platform script, as step 6 in docs/AGENT.md says.");
            if (run.Alert.Length == 0) open.Add("No alert link. The agent sends no alerts.");
            l.Add(Row(Glyphs.Info, "Still to do", string.Join("\n", open.ToArray()), null));
            l.Add(new SectionTitle("Next step"));
            l.Add(Row(Glyphs2.Sync, "Enroll a test device", "Assign the script to a group with one test device, then enroll or reset that device. Open Devices > Devices with the TrackVice agent with " + run.Customer + " chosen: the device appears during device setup.", null));
            l.Add(Row(Glyphs2.Calendar, "Renew the links", "The links expire on " + (run.Links != null ? run.Links.ExpiresUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : "") + ". Run New customer again with the same name and the existing account to make new ones.", null));
        }
    }

    public sealed partial class MainForm
    {
        void OpenNewCustomer(bool demo)
        {
            using (NewCustomerDialog d = demo ? NewCustomerDialog.ForDemo(theme, 0) : NewCustomerDialog.ForLive(theme)) d.ShowDialog(this);
        }
    }
}
