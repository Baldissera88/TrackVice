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

        public static readonly string[] Pages = { "addcustomer", "yourstorage", "yourstorage-settings", "customercode", "publishnote" };

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
    sealed class AddCustomerDialog : GuideForm
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
        readonly TextBox tName, tAlert, tTenant, tAccount;
        readonly ComboBox cSub, cGroup, cRegion, cAccount;
        readonly Segmented sKind;
        readonly Toggle gOwn, gGroup, gScript;
        readonly FlatButton bGraph, bSkip, bArm;
        OwnStorage own;
        WebSignIn graphIn, armIn;
        string graphText = "", armText = "";
        bool skipped;
        int page;
        Customer before;
        List<AzureSubscription> subs = new List<AzureSubscription>();
        List<AzureAccount> accounts = new List<AzureAccount>();
        bool filling;

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
            gOwn = Kept(new Toggle());
            gGroup = Kept(new Toggle());
            gGroup.On = true;
            gScript = Kept(new Toggle());
            gScript.On = true;
            bGraph = Button("Sign in", delegate { SignInIntune(); });
            bSkip = Button("Skip", delegate { if (busy) return; skipped = true; Next(); });
            bArm = Button("Sign in", delegate { SignInAzure(); });
            gGroup.Changed += delegate { run.MakeGroup = gGroup.On; Rebuild(); };
            gScript.Changed += delegate { run.AddScript = gScript.On; Rebuild(); };
            sKind.Changed += delegate { Rebuild(); Nav(); };
            cSub.SelectedIndexChanged += delegate { if (!filling) LoadSubscription(); };
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
            if (select == "own") d.gOwn.On = true;
            if (tab >= 1) d.Next();
            if (tab >= 1 && d.run.OwnAzure) { d.tAccount.Text = "northwindtrackvice"; d.SignInAzure(); }
            if (tab >= 2) { d.SignInIntune(); d.Next(); }
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

        bool CanLeave()
        {
            if (page == 1) return (IntuneReady || skipped) && (!run.OwnAzure || AzureReady);
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
            l.Add(Row(Glyphs2.Device, "Customer name", "Shown at the top of the device lists. When a customer with this name exists, its tenant and upload location are replaced.", tName));
            l.Add(Row(Glyphs2.Folder, "Store this customer's data in their own Azure", "Off: the uploads go to your storage. On: a storage account in the customer's Azure subscription is made, as New customer with links does. Devices download TrackVice from your storage either way.", gOwn));
            l.Add(Row(Glyphs.Shield, "Alert link (optional)", "A Teams workflow link. The agent posts to the channel when something fails and when a deployment is finished. Anyone with the link can post to the channel.", tAlert));
        }

        bool TakeCustomer()
        {
            run.Customer = tName.Text.Trim();
            run.Alert = tAlert.Text.Trim();
            run.OwnAzure = gOwn.On;
            string p = run.Problem(book);
            if (p != null) { note.Say(p, true); return false; }
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
            Customer have = book.Find(run.Customer);
            WebSignIn s = new WebSignIn(web, have != null ? have.GraphClientId : null, tTenant.Text, IntuneSetup.Scopes);
            graphIn = s;
            skipped = false;
            SignIn(s, delegate (string text) { graphText = text; }, null, delegate
            {
                graphText += " Create can add the group and the platform script.";
            }, delegate { graphText = ""; graphIn = null; });
        }

        void SignInAzure()
        {
            if (busy && pending != null && armIn != null) { ShowCode(pending); return; }
            if (busy) return;
            WebSignIn s = new WebSignIn(web, Arm.ClientId, tTenant.Text, Arm.Scope);
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

        void LoadSubscription()
        {
            AzureSubscription s = cSub.SelectedItem as AzureSubscription;
            if (s == null || armIn == null || !armIn.SignedIn) return;
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
            if (!CanLeave()) { note.Say(run.OwnAzure && !AzureReady ? "Sign in to the customer's Azure and pick the storage account." : "Sign in to Intune, or skip it.", true); return false; }
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
            List<string> plan = run.Plan(own, IntuneReady);
            l.Add(Row(Glyphs.Info, "What Create makes", string.Join("\n", plan.ToArray()), null));
            if (IntuneReady)
            {
                l.Add(Row(Glyphs2.Device, "Create the group " + AddCustomerRun.GroupName, "A dynamic device group with the rule " + AddCustomerRun.GroupRule + ". The group with that name is used when it is there.", gGroup));
                l.Add(Row(Glyphs2.Apps, "Add the platform script " + AddCustomerRun.ScriptName + " and assign it to that group", "System context, no signature check, 64-bit PowerShell. When a script with that name is there, TrackVice asks before it replaces its content.", gScript));
            }
            l.Add(Row(Glyphs.Save, "Saved on this PC", "The customer " + run.Customer + (run.Tenant.Length > 0 ? " with the tenant " + run.Tenant : "") + " and the viewer link, encrypted for your Windows account. The customer code, encrypted. The script in " + folder + ".", null));
        }

        void Create()
        {
            if (busy) return;
            own = OwnStorage.Load(secrets);
            if (own == null) { note.Say("Your storage is not set up. Set it up in Settings > Your storage.", true); return; }
            string key = AddCustomerUi.PublicKey(keys);
            bool intune = IntuneReady && (run.MakeGroup || run.AddScript);
            Arm arm = run.OwnAzure ? ArmApi() : null;
            IntuneSetup api = intune ? new IntuneSetup(web, graphIn.Token) : null;
            run.IntuneError = "";
            Work("Creating...", delegate
            {
                DateTime now = DateTime.UtcNow;
                if (run.OwnAzure)
                {
                    NewCustomerRun st = run.Storage;
                    int from = st.Done.Count;
                    if (!st.StorageReady) st.SetUpStorage(arm, delegate (string p) { Ui(delegate { note.Say(p, false); }); });
                    if (st.Links == null) { Ui(delegate { note.Say("Signing the links...", false); }); st.MakeLinks(arm, now); }
                    run.Done.AddRange(st.Done.GetRange(from, st.Done.Count - from));
                    run.UseLinks(st.Links);
                }
                else if (run.Upload.Length == 0) run.MakeStorage(web, own, now);
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
            l.Add(new SectionTitle("Keep these like a password"));
            l.Add(Row(Glyphs.Shield, "Customer code", CustomerCodes.Masked(run.Code != null ? run.Code.ToString() : "") + "\nIt holds the link that uploads to the customer's container.", CopyButton("customer code", delegate { return run.Code.ToString(); })));
            FlatButton bOpen = SmallButton("Open", delegate { AddCustomerUi.OpenScript(run.ScriptPath); });
            bOpen.Enabled = File.Exists(run.ScriptPath);
            ButtonStrip s = new ButtonStrip(CopyButton("script", delegate { return run.Script; }), bOpen);
            l.Add(Row(Glyphs2.Code, "Platform script", run.ScriptPath + "\nThe script with the customer code in it.", s));
            l.Add(new SectionTitle("Next step"));
            l.Add(Row(Glyphs2.Sync, "Enroll a test device", "Enroll or reset one device of " + run.Customer + " with Autopilot. It appears in Devices > Devices with the TrackVice agent during device setup.", null));
        }
    }

    // A customer's code later: copy it, change the alert link, and put the new script in Intune.
    sealed class CustomerCodeDialog : GuideForm
    {
        readonly ISecretStore secrets;
        readonly string folder;
        readonly Customer customer;
        string code;
        readonly TextBox tAlert;
        readonly FlatButton bSave, bGraph, bUpdate;
        WebSignIn graphIn;
        string saveText = "", graphText = "", updateText = "";

        public CustomerCodeDialog(Theme t, Customer c, IWeb web, ISecretStore secrets, string folder, bool isDemo) : base("Customer code for " + c.Name, t, web, isDemo)
        {
            customer = c;
            this.secrets = secrets;
            this.folder = folder;
            code = CustomerCodes.Load(secrets, c.Name) ?? "";
            Size = new Size(Dpi.S(800), Dpi.S(620));
            tAlert = Field(360, true);
            try { tAlert.Text = CustomerCode.Parse(code).Alert ?? ""; }
            catch (Exception) { }
            bSave = Button("Save", delegate { SaveAlert(); });
            bGraph = Button("Sign in", delegate { SignInIntune(); });
            bUpdate = Button("Update", delegate { UpdateIntune(); });
            bUpdate.Primary = true;
            Add(body);
            Rebuild();
        }

        public static bool Has(string customer)
        {
            return CustomerCodes.Load(YourStorageUi.Secrets, customer) != null;
        }

        public static void Open(IWin32Window owner, Theme t, Customer c)
        {
            using (CustomerCodeDialog d = new CustomerCodeDialog(t, c, YourStorageUi.Web, YourStorageUi.Secrets, AddCustomerUi.ScriptFolder, YourStorageUi.Demo)) d.ShowDialog(owner);
        }

        // A made-up customer with a code, for screenshots.
        public static CustomerCodeDialog ForDemo(Theme t)
        {
            MemorySecrets sec = YourStorageUi.DemoSecrets();
            IWeb w = new DemoAddCustomerWeb();
            OwnStorage own = OwnStorage.Load(sec);
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            r.Alert = "https://contoso.example/workflows/trackvice-alerts";
            r.MakeStorage(w, own, DateTime.UtcNow);
            r.MakeCode(own, AddCustomerUi.PublicKey(YourStorageUi.DemoKeys()));
            CustomerCodes.Save(sec, "Contoso", r.Code.ToString());
            CustomerBook b = CustomerBook.Demo();
            CustomerCodeDialog d = new CustomerCodeDialog(t, b.Find("Contoso"), w, sec, AddCustomerUi.ScriptFolder, true);
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
            l.Add(Row(Glyphs.Save, "Save the alert link", saveText.Length > 0 ? saveText : "Makes a new customer code with this link and saves the script again in " + folder + ". Devices use it when the script runs again.", bSave));
            l.Add(new SectionTitle("Intune"));
            bGraph.Text = graphIn != null && graphIn.SignedIn ? "Sign in again" : pending != null ? "Copy code and open" : "Sign in";
            l.Add(Row(Glyphs2.Sync, "Sign in to Intune", graphText.Length > 0 ? graphText : "To " + (customer.Tenant.Length > 0 ? customer.Tenant : "the tenant of the account you use") + ", with DeviceManagementScripts.ReadWrite.All. Signing in changes nothing.", bGraph));
            bUpdate.Enabled = graphIn != null && graphIn.SignedIn;
            l.Add(Row(Glyphs2.Apps, "Update the script in Intune", updateText.Length > 0 ? updateText : "Puts the script for this customer code in the platform script " + AddCustomerRun.ScriptName + ". Its assignments are kept.", bUpdate));
        }

        void SaveAlert()
        {
            CustomerCode c;
            try { c = CustomerCodeChange.WithAlert(code, tAlert.Text); }
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
            WebSignIn s = new WebSignIn(web, customer.GraphClientId, customer.Tenant, IntuneScripts.ScriptScope);
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
        }

        Form ShotAddCustomer()
        {
            switch (start.Page)
            {
                case "addcustomer": return AddCustomerDialog.ForDemo(theme, start.Tab, start.Select);
                case "yourstorage": return YourStorageDialog.ForDemo(theme, start.Tab);
                case "yourstorage-settings":
                    YourStorageUi.UseDemo();
                    SettingsDialog sd = new SettingsDialog(theme);
                    // Scrolled to the Your storage section.
                    sd.Shown += delegate { sd.BeginInvoke((MethodInvoker)delegate { foreach (Control c in sd.Controls) { Panel p = c as Panel; if (p != null && p.AutoScroll) p.AutoScrollPosition = new Point(0, Dpi.S(start.Tab >= 0 ? start.Tab : 1500)); } }); };
                    return sd;
                case "customercode": return CustomerCodeDialog.ForDemo(theme);
                default: ShowPublishNote(YourStorageUi.RunningVersion); return null;
            }
        }
    }
}
