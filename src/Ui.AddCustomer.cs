namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    public static class AddCustomerUi
    {
        // Where Add customer saves the platform scripts.
        public static string ScriptFolder
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrackVice"), "Scripts"); }
        }

        public static readonly string[] Pages = { "addcustomer", "yourstorage", "yourstorage-settings", "customercode", "publishnote", "storagesecurity", "removecustomer", "appregistration" };
        public const string AgentDoc = "https://github.com/Baldissera88/TrackVice/blob/main/docs/AGENT.md";

        public static string PublicKey(IUpdateKeyStore keys)
        {
            try
            {
                using (RSACryptoServiceProvider k = UpdateKeys.Load(keys)) return k == null ? "" : UpdateKeys.PublicKey(k);
            }
            catch (Exception) { return ""; }
        }

        public static void OpenScript(string path)
        {
            // Notepad, so the script is shown and never run.
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception) { }
        }
    }

    // Add customer: name, sign in, Create. Your storage holds the uploads unless the customer's own Azure is chosen.
    sealed partial class AddCustomerDialog : GuideForm
    {
        static readonly string[] StepNames = { "Customer", "Sign in", "Create" };
        const int ResultPage = 3;

        readonly CustomerBook book;
        readonly bool live;
        readonly ISecretStore secrets;
        readonly IUpdateKeyStore keys;
        readonly string folder;
        readonly AddCustomerRun run = new AddCustomerRun();
        readonly StepRail rail = new StepRail(StepNames);
        readonly FlatButton bBack = new FlatButton("Back", null);
        readonly FlatButton bNext = new FlatButton("Next", null);
        readonly TextBox tName, tAlert, tTenant, tAccount, tSearch;
        readonly ComboBox cSub, cGroup, cRegion, cAccount;
        readonly Segmented sKind;
        readonly Toggle gScript, gZone;
        readonly ChoiceCard cYours, cTheirs;
        readonly FlatButton bMore;
        readonly FlatButton bGraph, bSkip, bArm, bSearch;
        // Step 1: the name of a customer that exists waits for Update or Choose another name.
        bool asking;
        string confirmed = "";
        // Step 3: the groups found for Assign to a group I choose, null before the first search.
        List<TenantGroup> found;
        string searchText = "";
        SettingRow planRow;
        OwnStorage own;
        WebSignIn graphIn, armIn;
        string graphText = "", armText = "";
        bool skipped;
        int page;
        Customer before;
        List<AzureSubscription> subs = new List<AzureSubscription>();
        List<AzureAccount> accounts = new List<AzureAccount>();
        bool filling;
        // What is ticked in the What you need box of step 1.
        readonly HashSet<string> ticks = new HashSet<string>();

        public AddCustomerDialog(Theme t, CustomerBook book, IWeb web, ISecretStore secrets, IUpdateKeyStore keys, string folder, bool isDemo) : base("Add customer", t, web, isDemo)
        {
            this.book = book;
            this.secrets = secrets;
            this.keys = keys;
            this.folder = folder;
            live = book == CustomerUi.Book;
            own = OwnStorage.Load(secrets);
            Size = new Size(Dpi.S(860), Dpi.S(720));

            tName = Field(300, false);
            tAlert = Field(360, true);
            tTenant = Field(300, false);
            tAccount = Field(220, true);
            tAccount.MaxLength = 24;
            tAccount.CharacterCasing = CharacterCasing.Lower;
            cSub = Combo(360, true);
            cGroup = Combo(260, false);
            cGroup.Text = "rg-trackvice";
            cRegion = Combo(200, false);
            foreach (string r in Arm.Regions) cRegion.Items.Add(r);
            cRegion.Text = Arm.DefaultRegion;
            cAccount = Combo(360, true);
            sKind = Kept(new Segmented("New account", "Existing account"));
            cYours = Kept(new ChoiceCard("Your storage (default)", ""));
            cTheirs = Kept(new ChoiceCard("The customer's own Azure", "A storage account in the customer's subscription. They own and control the data, its region and how long it is kept, and pay for it (a few kroner a month). Needs an account in their tenant with Contributor on a subscription or resource group. Devices still download TrackVice from your storage."));
            List<ChoiceCard> group = new List<ChoiceCard>(new ChoiceCard[] { cYours, cTheirs });
            cYours.Group = cTheirs.Group = group;
            cYours.IsChosen = true;
            cYours.Chosen += delegate { Rebuild(); };
            cTheirs.Chosen += delegate { Rebuild(); };
            bMore = Button("More about this", delegate { Open(AddCustomerUi.AgentDoc); });
            gScript = Kept(new Toggle());
            gScript.On = true;
            gZone = Kept(new Toggle());
            bGraph = Button("Sign in", delegate { SignInIntune(); });
            bSkip = Button("Skip", delegate { if (busy) return; skipped = true; Next(); });
            bArm = Button("Sign in", delegate { SignInAzure(); });
            tSearch = Field(240, false);
            bSearch = Button("Search", delegate { SearchGroups(); });
            tSearch.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = e.SuppressKeyPress = true;
                SearchGroups();
            };
            tName.TextChanged += delegate { if (asking) { asking = false; Rebuild(); Nav(); } };
            gScript.Changed += delegate { run.AddScript = gScript.On; Rebuild(); };
            sKind.Changed += delegate { Rebuild(); Nav(); };
            cSub.SelectedIndexChanged += delegate { if (!filling) LoadSubscription(); };
            watch.Interval = 400;
            watch.Tick += delegate { WatchSubscription(); };
            watch.Start();
            FormClosed += delegate { watch.Stop(); watch.Dispose(); };
            cAccount.SelectedIndexChanged += delegate { if (!filling) Nav(); };

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
            bBack.Click += Safe.Wrap(delegate { if (!busy && page > 0 && page < ResultPage) Go(page - 1); });
            Panel gap1 = new Panel(), gap2 = new Panel();
            gap1.Dock = gap2.Dock = DockStyle.Right;
            gap1.Width = gap2.Width = Dpi.S(8);
            // Docked right in this order: Cancel, Back, Next from left to right.
            footer.Controls.Add(gap1);
            footer.Controls.Add(bBack);
            footer.Controls.Add(gap2);
            footer.Controls.Add(bNext);
            note.BringToFront();
            foreach (Control c in footer.Controls) { IThemed th = c as IThemed; if (th != null) th.ApplyTheme(t); }
            Shown += delegate { if (page == 0) tName.Focus(); };
            Go(0);
        }

        public static AddCustomerDialog ForLive(Theme t)
        {
            return new AddCustomerDialog(t, CustomerUi.Book, YourStorageUi.Web, YourStorageUi.Secrets, YourStorageUi.Keys, AddCustomerUi.ScriptFolder, false);
        }

        // The demo, with made-up answers. tab picks the step; "own" picks the customer's own Azure.
        public static AddCustomerDialog ForDemo(Theme t, int tab, string select)
        {
            AddCustomerDialog d = new AddCustomerDialog(t, CustomerBook.Demo(), new DemoAddCustomerWeb(), YourStorageUi.DemoSecrets(), YourStorageUi.DemoKeys(), AddCustomerUi.ScriptFolder, true);
            d.tName.Text = "Northwind Traders";
            d.tAlert.Text = "https://northwind.example/workflows/trackvice-alerts";
            if (select == "own") { d.cYours.IsChosen = false; d.cTheirs.IsChosen = true; }
            // "exists": a customer that is there, with the question of step 1.
            if (select == "exists") { d.tName.Text = "Contoso"; d.Next(); }
            if (tab >= 1) d.Next();
            if (tab >= 1 && d.run.OwnAzure) { d.tAccount.Text = "northwindtrackvice"; d.SignInAzure(); }
            if (tab >= 2) { d.SignInIntune(); d.Next(); }
            // "group" and "none" pick the assignment of step 3.
            if (tab >= 2 && select == "group") { d.Choose(ScriptTarget.ChosenGroup); d.Pick("Autopilot test devices"); }
            if (tab >= 2 && select == "none") d.Choose(ScriptTarget.None);
            if (tab >= 3) d.Next();
            d.note.Say("", false);
            return d;
        }

        // ---------- navigation ----------

        void Go(int p)
        {
            page = p;
            rail.Current = p;
            Rebuild();
            Nav();
        }

        protected override void Nav()
        {
            bool result = page == ResultPage;
            bBack.Visible = page > 0 && !result;
            bBack.Enabled = !busy;
            bClose.Visible = !result;
            bNext.Text = result ? "Close" : page == 2 ? "Create" : "Next";
            bNext.AutoWidth();
            bNext.Width = Math.Max(bNext.Width, Dpi.S(96));
            bNext.Enabled = !busy && CanLeave();
            bNext.Invalidate();
            bBack.Invalidate();
        }

        bool IntuneReady
        {
            get { return graphIn != null && graphIn.SignedIn; }
        }

        bool AzureReady
        {
            get { return armIn != null && armIn.SignedIn && cSub.SelectedItem is AzureSubscription && (sKind.Selected == 0 || cAccount.SelectedItem is AzureAccount); }
        }

        // Your storage keeps no account key, so Create reads it through an Azure sign-in.
        bool NeedOwnKey
        {
            get { return own != null && !own.KeyKept; }
        }

        bool CanLeave()
        {
            if (page == 0) return !asking;
            if (page == 1) return (IntuneReady || skipped) && (!run.OwnAzure || AzureReady) && (run.OwnAzure || !NeedOwnKey || OwnSignedIn);
            return true;
        }

        void Next()
        {
            if (busy) return;
            if (page == 0) { if (!TakeCustomer()) return; }
            else if (page == 1) { if (!TakeSignIn()) return; }
            else if (page == 2) { Create(); return; }
            else if (page == ResultPage) { DialogResult = DialogResult.OK; Close(); return; }
            note.Say("", false);
            Go(page + 1);
        }

        protected override void Build(List<Control> l)
        {
            switch (page)
            {
                case 0: BuildCustomer(l); break;
                case 1: BuildSignIn(l); break;
                case 2: BuildCreate(l); break;
                default: BuildResult(l); break;
            }
        }

        // ---------- 1. customer ----------

        void BuildCustomer(List<Control> l)
        {
            l.Add(new SectionTitle("1. Customer"));
            l.Add(Row(Glyphs.Info, "What this does", "Adds a customer in three steps. Devices of the customer upload to a private container in your storage " + (own != null ? own.Account : "") + " and download TrackVice from it. Nothing is created until you press Create in step 3.", null));
            NeedsBox needs = new NeedsBox("What you need", SetupNeeds.BoxIntro, SetupNeeds.Checklist(cTheirs.IsChosen, NeedOwnKey), ticks, "Roles and consent as a table");
            needs.MoreClicked += delegate { Open(AddCustomerUi.AgentDoc + "#what-you-need"); };
            l.Add(needs);
            l.Add(Row(Glyphs2.Device, "Customer name", "Shown at the top of the device lists. For a customer that exists, TrackVice asks first: updating it makes new links, the old ones stop, and the script in Intune must be updated too.", tName));
            if (asking) l.Add(ExistsRow());
            l.Add(new SectionTitle("Where the device data goes"));
            cYours.Desc = YourChoiceText(own);
            l.Add(cYours);
            l.Add(cTheirs);
            l.Add(Row(Glyphs.Info, "What is uploaded", WhatIsUploaded, bMore));
            l.Add(Row(Glyphs.Shield, "Alert link (optional)", "A Teams workflow link. The agent posts to the channel when something fails and when a deployment is finished. Anyone with the link can post to the channel.", tAlert));
            l.Add(Row(Glyphs.Info, AutoZoneUi.Title, AutoZoneUi.Text, gZone));
        }

        // What the agent really uploads: the session (src/Model.cs Session, masked by Secrets.MaskedCopy) and the log zip.
        public const string WhatIsUploaded = "The device name and Windows version, the Autopilot profile and tenant, the apps, scripts, policies and task sequence steps with their states, error codes, command lines and script output, and the log lines and events behind them, which can hold user names. When something fails, when the device goes idle and at the end, also a zip of the device's logs. Passwords, keys and tokens in the session are masked; the log files in the zip are uploaded as they are. No serial number is uploaded.";

        static string YourChoiceText(OwnStorage own)
        {
            string where = own == null ? "your Azure storage account" : "your Azure storage account " + own.Account + (own.Region.Length > 0 ? ", in " + own.Region : "");
            int days = own != null ? own.RetentionDays : StorageSecurity.DefaultDays;
            string delete = own != null && own.RuleDays > 0
                ? "Data is deleted after " + own.RuleDays + " days."
                : "Data is kept until it is deleted: Check security in Settings > Your storage adds the rule that deletes it after " + days + " days.";
            return "The device data of this customer goes to a private container only for them in " + where + ". The devices can only add files there; only TrackVice on this PC can read them. " + delete + " You can stop it at any time with Remove customer.";
        }

        // The question for a name that exists, with its two answers.
        SettingRow ExistsRow()
        {
            Customer have = ExistingName.Find(book, run.Customer);
            string name = have != null ? have.Name : run.Customer;
            FlatButton update = SmallButton(ExistingName.UpdateButton(name), delegate
            {
                confirmed = name;
                asking = false;
                Next();
            });
            update.Primary = true;
            FlatButton another = SmallButton(ExistingName.ChooseAnother, delegate
            {
                asking = false;
                Rebuild();
                Nav();
                tName.Focus();
                tName.SelectAll();
            });
            return Row(Glyphs.Info, ExistingName.Title(name), ExistingName.Detail(name), new ButtonStrip(update, another));
        }

        bool TakeCustomer()
        {
            run.Customer = tName.Text.Trim();
            run.Alert = tAlert.Text.Trim();
            run.AutoTimeZone = gZone.On;
            run.OwnAzure = cTheirs.IsChosen;
            string p = run.Problem(book);
            if (p != null) { note.Say(p, true); return false; }
            if (ExistingName.MustAsk(book, run.Customer, confirmed))
            {
                asking = true;
                note.Say("", false);
                Rebuild();
                Nav();
                return false;
            }
            if (tAccount.Text.Length == 0) tAccount.Text = Arm.Suggest(run.Customer, 0);
            return true;
        }

        // ---------- 2. sign in ----------

        void BuildSignIn(List<Control> l)
        {
            l.Add(new SectionTitle("2. Sign in"));
            l.Add(Row(Glyphs.Info, "What this does", "Signs in to the customer's tenant, so Create can add the device group and the platform script in Intune. Signing in changes nothing.", null));
            l.Add(Row(Glyphs.Shield, "Tenant", "Optional. The tenant ID or domain, such as contoso.onmicrosoft.com. Empty signs in to the tenant of the account you use.", tTenant));
            bGraph.Text = IntuneReady ? "Sign in again" : pending != null && armIn == null ? "Copy code and open" : "Sign in";
            l.Add(Row(Glyphs2.Sync, "Sign in to Intune", graphText.Length > 0 ? graphText : "With a code in the browser, with DeviceManagementScripts.ReadWrite.All and Group.ReadWrite.All.", bGraph));
            if (!IntuneReady) l.Add(Row(Glyphs2.Help, "Skip: I add the script to Intune myself", "Create then makes the storage, the customer code and the script, and saves the script on this PC.", bSkip));
            if (!run.OwnAzure && NeedOwnKey)
            {
                l.Add(new SectionTitle("Your storage"));
                l.Add(OwnSignInRow(own, "The account key of " + own.Account + " is not kept on this PC. Sign in to your own Azure to read it for this customer; it is not kept."));
            }
            if (!run.OwnAzure) return;
            l.Add(new SectionTitle("The customer's Azure"));
            bArm.Text = armIn != null && armIn.SignedIn ? "Sign in again" : pending != null && armIn != null && !armIn.SignedIn ? "Copy code and open" : "Sign in";
            l.Add(Row(Glyphs2.Sync, "Sign in to Azure", armText.Length > 0 ? armText : "To the same tenant, to make the storage in the customer's subscription. Reads the subscriptions; nothing is changed.", bArm));
            if (armIn == null || !armIn.SignedIn || subs.Count == 0) return;
            AzureSubscription sub = cSub.SelectedItem as AzureSubscription;
            l.Add(Row(Glyphs2.Settings, "Subscription", sub != null ? "ID " + sub.Id : "", cSub));
            l.Add(Row(Glyphs2.Folder, "Account", "A new account just for TrackVice, or an existing one that gets the containers.", sKind));
            if (sKind.Selected == 0)
            {
                l.Add(Row(Glyphs2.Folder, "Resource group", "Pick one, or type a name to make a new group.", cGroup));
                l.Add(Row(Glyphs2.Apps, "Name", "3 to 24 lowercase letters and digits, unique in all of Azure.", tAccount));
                l.Add(Row(Glyphs.Info, "Region", "Where the customer's data is kept.", cRegion));
            }
            else l.Add(Row(Glyphs2.Folder, "Existing account", accounts.Count == 0 ? "This subscription has no storage accounts." : "Its settings are kept.", cAccount));
        }

        void SignInIntune()
        {
            if (busy && pending != null && armIn == null) { ShowCode(pending); return; }
            if (busy) return;
            if (armIn != null && !armIn.SignedIn) armIn = null;
            if (ownIn != null && !ownIn.SignedIn) ownIn = null;
            Customer have = book.Find(run.Customer);
            WebSignIn s = SignIns.Get(web, have != null ? have.GraphClientId : null, tTenant.Text, IntuneSetup.Scopes);
            graphIn = s;
            skipped = false;
            // Groups found in another tenant do not apply.
            found = null;
            run.Chosen = null;
            searchText = "";
            SignIn(s, delegate (string text) { graphText = text; }, null, delegate
            {
                graphText += " Create can add the group and the platform script.";
            }, delegate { graphText = ""; graphIn = null; });
        }

        void SignInAzure()
        {
            if (busy && pending != null && armIn != null) { ShowCode(pending); return; }
            if (busy) return;
            WebSignIn s = SignIns.Get(web, Arm.ClientId, tTenant.Text, Arm.Scope);
            armIn = s;
            subs = new List<AzureSubscription>();
            List<AzureSubscription> got = null;
            SignIn(s, delegate (string text) { armText = text; }, delegate { got = new Arm(web, s.Token).Subscriptions(); }, delegate
            {
                subs = got ?? new List<AzureSubscription>();
                if (subs.Count == 0) armText += " The account has no enabled subscriptions in this tenant.";
                filling = true;
                cSub.Items.Clear();
                foreach (AzureSubscription x in subs) cSub.Items.Add(x);
                filling = false;
                if (subs.Count > 0) cSub.SelectedIndex = 0;
            }, delegate { armText = ""; armIn = null; });
        }

        Arm ArmApi()
        {
            Arm a = new Arm(web, armIn.Token);
            if (demo) a.Pause = delegate { };
            return a;
        }

        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
        string loadedSub = "";

        // Does not depend on the box raising its events: what the box shows is read when the window is free.
        void WatchSubscription()
        {
            if (busy || armIn == null || !armIn.SignedIn || subs.Count == 0) return;
            AzureSubscription s = cSub.SelectedItem as AzureSubscription;
            if (s == null || s.Id == loadedSub) return;
            LoadSubscription();
        }

        void LoadSubscription()
        {
            AzureSubscription s = cSub.SelectedItem as AzureSubscription;
            if (s == null || armIn == null || !armIn.SignedIn) return;
            if (busy) return;
            loadedSub = s.Id;
            List<KeyValuePair<string, string>> groups = null;
            List<AzureAccount> accts = null;
            Work("Reading " + s.Name + "...", delegate
            {
                Arm a = ArmApi();
                groups = a.ResourceGroups(s.Id);
                accts = a.StorageAccounts(s.Id);
            }, delegate
            {
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
            }, null);
        }

        bool TakeSignIn()
        {
            if (!CanLeave()) { note.Say(run.OwnAzure && !AzureReady ? "Sign in to the customer's Azure and pick the storage account." : !run.OwnAzure && NeedOwnKey && !OwnSignedIn ? "Sign in to Azure for your storage." : "Sign in to Intune, or skip it.", true); return false; }
            run.Tenant = tTenant.Text.Trim();
            if (IntuneReady && run.Tenant.Length == 0)
            {
                try { run.Tenant = AddCustomerUi_Tenant(graphIn); }
                catch (Exception) { }
            }
            if (run.OwnAzure)
            {
                NewCustomerRun st = run.Storage ?? new NewCustomerRun();
                if (!st.StorageReady)
                {
                    st.Customer = run.Customer;
                    st.Subscription = (AzureSubscription)cSub.SelectedItem;
                    st.NewAccount = sKind.Selected == 0;
                    if (st.NewAccount)
                    {
                        string rule = Arm.NameRule(tAccount.Text.Trim());
                        if (rule != null) { note.Say(rule, true); return false; }
                        string g = (cGroup.Text ?? "").Trim();
                        if (g.Length == 0) { note.Say("Pick or enter a resource group.", true); return false; }
                        st.AccountName = tAccount.Text.Trim();
                        st.ResourceGroup = g;
                        st.NewGroup = !cGroup.Items.Contains(g);
                        string region = (cRegion.Text ?? "").Trim().ToLowerInvariant().Replace(" ", "");
                        st.Region = region.Length > 0 ? region : Arm.DefaultRegion;
                    }
                    else st.Account = (AzureAccount)cAccount.SelectedItem;
                }
                st.LinkDays = AddCustomerRun.LinkDays;
                run.Storage = st;
            }
            return true;
        }

        // The tenant the Intune sign-in went to: its ID from the token, else the domain of the account.
        static string AddCustomerUi_Tenant(WebSignIn s)
        {
            string t = AddCustomerRun.TenantOf(s.Token());
            if (!string.IsNullOrEmpty(t)) return t;
            string a = s.Account ?? "";
            int at = a.IndexOf('@');
            return at > 0 ? a.Substring(at + 1) : "";
        }

        // ---------- 3. create ----------

        void BuildCreate(List<Control> l)
        {
            l.Add(new SectionTitle("3. Create"));
            planRow = Row(Glyphs.Info, "What Create makes", PlanText(), null);
            l.Add(planRow);
            if (IntuneReady)
            {
                l.Add(Row(Glyphs2.Apps, "Add the platform script " + AddCustomerRun.ScriptName, "System context, no signature check, 64-bit PowerShell. When a script with that name is there, TrackVice asks before it replaces its content.", gScript));
                if (run.AddScript) BuildAssign(l);
            }
            l.Add(Row(Glyphs.Save, "Saved on this PC", "The customer " + run.Customer + (run.Tenant.Length > 0 ? " with the tenant " + run.Tenant : "") + " and the viewer link, encrypted for your Windows account. The customer code, encrypted. The script in " + folder + ".", null));
        }

        void Create()
        {
            if (busy) return;
            own = OwnStorage.Load(secrets);
            if (own == null) { note.Say("Your storage is not set up. Set it up in Settings > Your storage.", true); return; }
            string problem = run.CreateProblem(IntuneReady);
            if (problem != null) { note.Say(problem, true); return; }
            string key = AddCustomerUi.PublicKey(keys);
            bool intune = IntuneReady && run.AddScript;
            Arm arm = run.OwnAzure ? ArmApi() : null;
            IntuneSetup api = intune ? new IntuneSetup(web, graphIn.Token) : null;
            run.IntuneError = "";
            Work("Creating...", delegate
            {
                DateTime now = DateTime.UtcNow;
                string keyProblem = OwnPublish.KeyProblem(web, own, key);
                if (keyProblem != null) throw new SetupException(keyProblem);
                if (run.OwnAzure) run.SetUpOwnAzure(arm, now, delegate (string p) { Ui(delegate { note.Say(p, false); }); });
                else if (run.Upload.Length == 0 || run.Container != run.ContainerName) run.MakeStorage(web, OwnWithKey(own), now);
                run.MakeCode(own, key);
                if (api != null)
                {
                    try { run.ToIntune(api, AskReplace); }
                    catch (Exception ex) { run.IntuneError = ex.Message; }
                }
            }, delegate
            {
                Save();
                rail.Current = ResultPage;
                page = ResultPage;
            }, null);
        }

        bool AskReplace()
        {
            if (demo) return true;
            bool yes = false;
            string q = "Intune in " + (run.Tenant.Length > 0 ? run.Tenant : "this tenant") + " has a platform script called " + AddCustomerRun.ScriptName + " already.\n\nReplace its content with the script for " + run.Customer + "? No keeps its content. Either way it is assigned to the group.";
            Invoke((MethodInvoker)delegate { yes = MessageBox.Show(this, q, "Add customer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; });
            return yes;
        }

        void Save()
        {
            if (before == null) before = book.Current == null ? null : book.Current.Copy();
            run.SaveTo(book);
            if (live)
            {
                book.Save();
                CustomerUi.Edited(before);
            }
            CustomerCodes.Save(secrets, run.Customer, run.Code.ToString());
            // A search for the account may have found its ID and region; the key stays unkept.
            if (NeedOwnKey) SaveOwn(own, secrets);
            if (demo) { run.ScriptPath = Path.Combine(folder, AgentScript.FileName(run.Customer)); return; }
            try { run.SaveScript(folder); }
            catch (Exception ex) { note.Say("The script could not be saved in " + folder + ": " + ex.Message, true); }
        }

        // ---------- result ----------

        void BuildResult(List<Control> l)
        {
            l.Add(new SectionTitle("Done"));
            l.Add(Row(Glyphs2.Device, run.Customer, "Saved in TrackVice with the viewer link, encrypted for your Windows account. Choose it at the top of Devices > Devices with the TrackVice agent.", null));
            l.Add(Row(Glyphs.Save, "Made", string.Join("\n", run.Done.ToArray()), null));
            if (run.IntuneError.Length > 0)
                l.Add(Row(Glyphs.Info, "Not done in Intune", run.IntuneError + "\nAdd the saved script as a platform script yourself, or use Customer code > Update the script in Intune later.", null));
            else if (!run.IntuneDone)
                l.Add(Row(Glyphs.Info, "Intune", "Nothing was changed in Intune. Add the saved script as a platform script: system context, no signature check, 64-bit PowerShell, assigned to a group of Autopilot devices.", null));
            else if (run.Group == null)
                l.Add(Row(Glyphs.Info, "Not assigned", "The platform script " + AddCustomerRun.ScriptName + " is in Intune without an assignment. Assign it to a group of devices in Intune. " + AssignHint, null));
            l.Add(new SectionTitle("Keep these like a password"));
            l.Add(Row(Glyphs.Shield, "Customer code", CustomerCodes.Masked(run.Code != null ? run.Code.ToString() : "") + "\nIt holds the link that uploads to the customer's container.", CopyButton("customer code", delegate { return run.Code.ToString(); })));
            FlatButton bOpen = SmallButton("Open", delegate { AddCustomerUi.OpenScript(run.ScriptPath); });
            bOpen.Enabled = File.Exists(run.ScriptPath);
            ButtonStrip s = new ButtonStrip(CopyButton("script", delegate { return run.Script; }), bOpen);
            l.Add(Row(Glyphs2.Code, "Platform script", run.ScriptPath + "\nThe script with the customer code in it.", s));
            l.Add(new SectionTitle("Next step"));
            string device = run.IntuneDone && run.Target == ScriptTarget.ChosenGroup && run.Group != null ? "one device of " + run.Customer + " that is in " + run.Group.Name : "one device of " + run.Customer;
            l.Add(Row(Glyphs2.Sync, "Enroll a test device", "Enroll or reset " + device + " with Autopilot. It appears in Devices > Devices with the TrackVice agent during device setup.", null));
        }
    }

    // The words next to the tick for automatic time zone, in Add customer and Customer code.
    static class AutoZoneUi
    {
        public const string Title = "Turn on automatic time zone on new devices";
        public const string Text = "Off by default. When it is on, the agent turns on Set time zone automatically when it is installed: it changes this device setting and allows Windows to use the device's location to find the zone, then asks Windows to sync the clock. Where one time zone fits all devices, the Intune setting for the time zone is better: see the guide.";
    }

    // A customer's code later: copy it, change the alert link, and put the new script in Intune.
    sealed class CustomerCodeDialog : GuideForm
    {
        readonly ISecretStore secrets;
        readonly string folder;
        readonly Customer customer;
        readonly CustomerBook book;
        readonly OwnStorage own;
        readonly string container;
        string code;
        readonly TextBox tAlert;
        readonly Toggle gZone;
        readonly FlatButton bSave, bGraph, bUpdate, bLinks, bRemove;
        WebSignIn graphIn;
        string saveText = "", graphText = "", updateText = "", linksText = "";

        public CustomerCodeDialog(Theme t, Customer c, CustomerBook book, IWeb web, ISecretStore secrets, string folder, bool isDemo) : base("Customer code for " + c.Name, t, web, isDemo)
        {
            customer = c;
            this.book = book;
            own = OwnStorage.Load(secrets);
            this.secrets = secrets;
            this.folder = folder;
            code = CustomerCodes.Load(secrets, c.Name) ?? "";
            Size = new Size(Dpi.S(800), Dpi.S(620));
            tAlert = Field(360, true);
            gZone = Kept(new Toggle());
            try
            {
                CustomerCode parsed = CustomerCode.Parse(code);
                tAlert.Text = parsed.Alert ?? "";
                gZone.On = parsed.AutoTimeZone;
            }
            catch (Exception) { }
            bSave = Button("Save", delegate { SaveAlert(); });
            bGraph = Button("Sign in", delegate { SignInIntune(); });
            bUpdate = Button("Update", delegate { UpdateIntune(); });
            bUpdate.Primary = true;
            bLinks = Button("New links", delegate { NewLinks(); });
            bRemove = Button("Remove customer\u2026", delegate { RemoveCustomer(); });
            string upload = null;
            try { upload = CustomerCode.Parse(code).Upload; }
            catch (Exception) { }
            container = CustomerAccess.ContainerOf(own, upload);
            Add(body);
            Rebuild();
        }

        public static bool Has(string customer)
        {
            return CustomerCodes.Load(YourStorageUi.Secrets, customer) != null;
        }

        public static void Open(IWin32Window owner, Theme t, Customer c)
        {
            using (CustomerCodeDialog d = new CustomerCodeDialog(t, c, CustomerUi.Book, YourStorageUi.Web, YourStorageUi.Secrets, AddCustomerUi.ScriptFolder, YourStorageUi.Demo))
            {
                d.Shown += delegate { if (SignIns.Has(d.web, d.customer.GraphClientId, d.customer.Tenant, IntuneScripts.ScriptScope)) d.SignInIntune(); };
                d.ShowDialog(owner);
            }
        }

        // A made-up customer with a code, for screenshots.
        public static CustomerCodeDialog ForDemo(Theme t)
        {
            MemorySecrets sec = YourStorageUi.DemoSecrets();
            IWeb w = new DemoStorageWeb(true);
            OwnStorage own = OwnStorage.Load(sec);
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            r.Alert = "https://contoso.example/workflows/trackvice-alerts";
            r.MakeStorage(w, own, DateTime.UtcNow);
            r.MakeCode(own, AddCustomerUi.PublicKey(YourStorageUi.DemoKeys()));
            CustomerCodes.Save(sec, "Contoso", r.Code.ToString());
            CustomerBook b = CustomerBook.Demo();
            CustomerCodeDialog d = new CustomerCodeDialog(t, b.Find("Contoso"), b, w, sec, AddCustomerUi.ScriptFolder, true);
            d.SignInIntune();
            return d;
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle(customer.Name));
            if (code.Length == 0)
            {
                l.Add(Row(Glyphs.Info, "No customer code", "This customer was not made with Add customer, so TrackVice has no customer code for it on this PC.", null));
                return;
            }
            l.Add(Row(Glyphs.Shield, "Customer code", CustomerCodes.Masked(code) + "\nKeep it like a password: it holds the link that uploads to the customer's container.", CopyButton("customer code", delegate { return code; })));
            l.Add(Row(Glyphs.Shield, "Alert link", "A Teams workflow link, or empty for no alerts.", tAlert));
            l.Add(Row(Glyphs.Info, AutoZoneUi.Title, AutoZoneUi.Text, gZone));
            l.Add(Row(Glyphs.Save, "Save", saveText.Length > 0 ? saveText : "Makes a new customer code with the alert link and the time zone setting, and saves the script again in " + folder + ". Devices use it when the script runs again.", bSave));
            l.Add(new SectionTitle("Intune"));
            bGraph.Text = graphIn != null && graphIn.SignedIn ? "Sign in again" : pending != null ? "Copy code and open" : "Sign in";
            l.Add(Row(Glyphs2.Sync, "Sign in to Intune", graphText.Length > 0 ? graphText : "To " + (customer.Tenant.Length > 0 ? customer.Tenant : "the tenant of the account you use") + ", with DeviceManagementScripts.ReadWrite.All. Signing in changes nothing.", bGraph));
            bUpdate.Enabled = graphIn != null && graphIn.SignedIn;
            l.Add(Row(Glyphs2.Apps, "Update the script in Intune", updateText.Length > 0 ? updateText : "Puts the script for this customer code in the platform script " + AddCustomerRun.ScriptName + ". Its assignments are kept.", bUpdate));
            l.Add(new SectionTitle("Links"));
            if (container != null)
            {
                if (!own.KeyKept) l.Add(OwnSignInRow(own, "The account key of " + own.Account + " is not kept on this PC. Sign in to read it for New links; it is not kept."));
                l.Add(Row(Glyphs2.Sync, "New links", linksText.Length > 0 ? linksText : "Replaces the access policies of " + container + " with new ones and signs a new upload link and viewer link, valid for 2 years. The old links stop within 30 seconds, so devices with the old customer code can no longer upload. Then update the script in Intune.", bLinks));
            }
            else l.Add(Row(Glyphs.Info, "New links", "The data of " + customer.Name + " is not in your storage, so TrackVice cannot make new links for it.", null));
            l.Add(Row(Glyphs.Shield, "Remove customer", "Stops every link of " + customer.Name + " at once and removes the customer from TrackVice. Deleting the container with its data is a separate choice.", bRemove));
        }

        void NewLinks()
        {
            if (busy || container == null) return;
            if (!own.KeyKept && !OwnSignedIn) { note.Say("Sign in to Azure first.", true); return; }
            if (!demo && MessageBox.Show(this, "Make new links for " + customer.Name + "? The old upload and viewer links stop within 30 seconds, and devices with the old customer code can no longer upload until they get the new script.", "New links", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            string old = code;
            CustomerRenewal r = null;
            Work("Making new links...", delegate { r = CustomerRenewal.Make(web, OwnWithKey(own), old, DateTime.UtcNow); }, delegate
            {
                code = r.Code.ToString();
                CustomerCodes.Save(secrets, customer.Name, code);
                Customer c = customer.Copy();
                c.AgentLocation = r.Links.Viewer;
                if (book != null && book.Find(customer.Name) != null)
                {
                    Customer before = book.Current == null ? null : book.Current.Copy();
                    book.Update(customer.Name, c);
                    if (book == CustomerUi.Book) { book.Save(); CustomerUi.Edited(before); }
                }
                linksText = "New links signed with the policies " + r.Links.UploadPolicy + " and " + r.Links.ViewerPolicy + ", valid until " + r.Links.ExpiresUtc.ToLocalTime().ToString("d MMMM yyyy") + ". They work within 30 seconds. The customer code is new.";
                if (r.OldLinksWithoutPolicy) linksText += " The old links were signed without a policy, so they keep working until they expire or the account key is rotated.";
                if (!demo)
                {
                    AddCustomerRun s = new AddCustomerRun();
                    s.Customer = customer.Name;
                    s.Script = CustomerScript.For(code);
                    try { linksText += " The script is in " + s.SaveScript(folder) + "."; }
                    catch (Exception ex) { linksText += " The script could not be saved: " + ex.Message; }
                }
                linksText += " Update the script in Intune to give it to new devices.";
                // Signed in to Intune already: offer the update at once, with its own question.
                if (!demo && graphIn != null && graphIn.SignedIn) BeginInvoke((MethodInvoker)delegate { UpdateIntune(); });
            }, null);
        }

        void RemoveCustomer()
        {
            if (busy) return;
            using (RemoveCustomerDialog d = new RemoveCustomerDialog(theme, customer, book ?? CustomerUi.Book, web, secrets, demo))
            {
                d.ShowDialog(this);
                if (d.Removed) Close();
            }
        }

        void SaveAlert()
        {
            CustomerCode c;
            try { c = CustomerCodeChange.WithSettings(code, tAlert.Text, gZone.On); }
            catch (Exception ex) { note.Say(ex.Message, true); return; }
            code = c.ToString();
            CustomerCodes.Save(secrets, customer.Name, code);
            saveText = "Saved a new customer code.";
            if (!demo)
            {
                AddCustomerRun r = new AddCustomerRun();
                r.Customer = customer.Name;
                r.Script = CustomerScript.For(code);
                try { saveText += " The script is in " + r.SaveScript(folder) + "."; }
                catch (Exception ex) { saveText += " The script could not be saved: " + ex.Message; }
            }
            saveText += " Update the script in Intune to give it to new devices.";
            Rebuild();
        }

        void SignInIntune()
        {
            if (busy && pending != null) { ShowCode(pending); return; }
            if (busy) return;
            WebSignIn s = SignIns.Get(web, customer.GraphClientId, customer.Tenant, IntuneScripts.ScriptScope);
            graphIn = s;
            SignIn(s, delegate (string text) { graphText = text; }, null, null, delegate { graphText = ""; graphIn = null; });
        }

        void UpdateIntune()
        {
            if (busy || graphIn == null || !graphIn.SignedIn) return;
            if (!demo && MessageBox.Show(this, "Replace the content of the platform script " + AddCustomerRun.ScriptName + " in Intune with the script for this customer code?", "Customer code", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            IntuneSetup api = new IntuneSetup(web, graphIn.Token);
            string c = code;
            Work("Updating the script in Intune...", delegate { CustomerCodeChange.UpdateIntune(api, customer.Name, c); }, delegate
            {
                updateText = "The platform script " + AddCustomerRun.ScriptName + " has the new content. Devices that run it from now on get the new customer code.";
            }, null);
        }
    }

    public sealed partial class MainForm
    {
        PublishNote publishNote;

        void OpenAddCustomer(bool demo)
        {
            if (demo)
            {
                using (AddCustomerDialog d = AddCustomerDialog.ForDemo(theme, 0, null)) d.ShowDialog(this);
                return;
            }
            if (YourStorageUi.Load() == null)
            {
                using (YourStorageDialog s = YourStorageDialog.ForLive(theme)) s.ShowDialog(this);
                if (YourStorageUi.Load() == null) return;
            }
            using (AddCustomerDialog d = AddCustomerDialog.ForLive(theme)) d.ShowDialog(this);
        }

        // At start: when your storage has an older TrackVice than this one, the status bar offers to publish it.
        void CheckYourStorage()
        {
            if (start.Shot != null) return;
            OwnStorage own = YourStorageUi.Load();
            if (own == null) return;
            string running = YourStorageUi.RunningVersion;
            IWeb w = YourStorageUi.Web;
            ISecretStore s = YourStorageUi.Secrets;
            Thread th = new Thread(delegate ()
            {
                string published = own.PublishedVersion;
                try
                {
                    published = OwnPublish.Published(w, own.AgentRead) ?? "";
                    if (published != own.PublishedVersion) { own.PublishedVersion = published; own.Save(s); }
                }
                catch (Exception) { }
                if (!OwnPublish.Needed(own, published, running)) return;
                try { BeginInvoke((MethodInvoker)delegate { ShowPublishNote(running); }); }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void ShowPublishNote(string running)
        {
            if (publishNote == null)
            {
                publishNote = new PublishNote();
                publishNote.Dock = DockStyle.Right;
                publishNote.PublishClicked += Safe.Wrap(delegate
                {
                    publishNote.Say("Publishing TrackVice " + running + " to your storage...", false);
                    YourStorageUi.PublishRunning(this, delegate (string msg, bool ok)
                    {
                        if (ok) publishNote.Visible = false;
                        else publishNote.Say(OwnPublish.Prompt(running), true);
                        Status(msg);
                    });
                });
                themed.Add(publishNote);
                statusBar.Controls.Add(publishNote);
                statusLeft.BringToFront();
                publishNote.ApplyTheme(theme);
            }
            publishNote.Say(OwnPublish.Prompt(running), true);
            publishNote.Visible = true;
            ShowPublishBanner(running);
            RegisterPublishTile();
        }

        Form ShotAddCustomer()
        {
            switch (start.Page)
            {
                case "addcustomer": return AddCustomerDialog.ForDemo(theme, start.Tab, start.Select);
                case "yourstorage": return YourStorageDialog.ForDemo(theme, start.Tab);
                case "yourstorage-settings":
                    // --select empty shows the section as it is before Your storage is set up.
                    if (start.Select == "empty") YourStorageUi.Secrets = new MemorySecrets();
                    else YourStorageUi.UseDemo();
                    SettingsDialog sd = new SettingsDialog(theme, SettingsCatalog.Storage);
                    // Scrolled to the Your storage section.
                    sd.Shown += delegate { sd.BeginInvoke((MethodInvoker)delegate { foreach (Control c in sd.Controls) { Panel p = c as Panel; if (p != null && p.AutoScroll) p.AutoScrollPosition = new Point(0, Dpi.S(start.Tab >= 0 ? start.Tab : 1500)); } }); };
                    return sd;
                case "customercode": return CustomerCodeDialog.ForDemo(theme);
                case "storagesecurity": return StorageSecurityDialog.ForDemo(theme);
                case "removecustomer": return RemoveCustomerDialog.ForDemo(theme);
                case "appregistration": return AppRegistrationDialog.ForDemo(theme, start.Tab);
                default: ShowPublishNote(YourStorageUi.RunningVersion); if (start.Select == "tspackage") ShotTsPackageLine(); return null;
            }
        }
    }
}
