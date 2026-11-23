namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // The Key Vault rows of the customers page: the vault address, sign in, get and save.
    public static class CustomersVaultUi
    {
        // Screenshots use a vault in memory and a made-up account.
        public static bool Demo;

        public static Control Rows(Form owner, Theme t, CustomerBook book, bool live, Action refresh, FooterNote note)
        {
            return new VaultRows(owner, t, book, live, refresh, note);
        }

        public static Form ForShot(Theme t)
        {
            return ForShot(t, false);
        }

        // end: scrolled to the end, where the setup rows are.
        public static Form ForShot(Theme t, bool end)
        {
            Demo = true;
            CustomersPage p = new CustomersPage(t, CustomerBook.Demo());
            p.Size = new Size(Dpi.S(820), Dpi.S(940));
            if (end) p.Shown += delegate { p.ScrollToEnd(); };
            return p;
        }
    }

    sealed partial class VaultRows : Panel, IThemed, IMeasured
    {
        readonly Form owner;
        readonly CustomerBook book;
        readonly bool live, demo;
        readonly Action refresh;
        readonly FooterNote note;
        readonly CustomerRows inner = new CustomerRows();
        readonly TextBox address = new TextBox();
        readonly FlatButton bSign, bOut, bGet, bSave;
        readonly ISettingStore settings;
        readonly ISecretStore secrets;
        readonly IWeb web;
        readonly ManualResetEvent cancel = new ManualResetEvent(false);
        readonly MemoryVault demoVault;
        Theme theme;
        VaultSignIn signIn;
        string signInHost;
        bool busy;
        string status = "";
        ButtonStrip[] strips = new ButtonStrip[0];

        public VaultRows(Form owner, Theme t, CustomerBook book, bool live, Action refresh, FooterNote note)
        {
            this.owner = owner;
            this.book = book;
            this.live = live;
            this.refresh = refresh;
            this.note = note;
            theme = t;
            demo = CustomersVaultUi.Demo;
            if (demo)
            {
                settings = new MemorySettingStore();
                secrets = new MemorySecrets();
                web = new DemoAppRegistrationWeb(false);
                demoVault = DemoVault();
            }
            else
            {
                settings = new PrefsSettingStore();
                secrets = new PrefsSecrets();
                web = new HttpWeb();
            }
            Controls.Add(inner);
            address.Width = Dpi.S(280);
            address.Font = Fonts.Mono;
            address.BorderStyle = BorderStyle.FixedSingle;
            address.Text = demo ? "contoso-vault" : (settings.Get("VaultAddress") ?? "");
            address.Leave += delegate { settings.Set("VaultAddress", address.Text.Trim().Length == 0 ? null : address.Text.Trim()); };
            bSign = Button("Sign in", delegate { StartSignIn(); });
            bOut = Button("Sign out", delegate { SignOut(); });
            bGet = Button("Get customers", delegate { Get(); });
            bSave = Button("Save customers…", delegate { Save(); });
            Restore();
            if (demo) status = "Signed in as ada@contoso.com. Last sync: 12 November 2026 14:05.";
            else status = Describe();
            ApplyTheme(t);
            Build();
            owner.FormClosed += delegate { cancel.Set(); };
        }

        FlatButton Button(string text, EventHandler click)
        {
            FlatButton b = new FlatButton(text, null);
            b.Compact = true;
            b.AutoWidth();
            b.Width += Dpi.S(8);
            b.Click += Safe.Wrap(click);
            return b;
        }

        // ---------- rows ----------

        void Build()
        {
            foreach (ButtonStrip s in strips) s.Controls.Clear();
            bSign.Visible = signIn == null || !signIn.SignedIn;
            bOut.Visible = !bSign.Visible;
            ButtonStrip first = new ButtonStrip(address, bSign, bOut);
            first.Fit();
            ButtonStrip getStrip = new ButtonStrip(bGet);
            ButtonStrip saveStrip = new ButtonStrip(bSave);
            strips = new ButtonStrip[] { first, getStrip, saveStrip };
            List<SettingRow> l = new List<SettingRow>();
            SettingRow vault = new SettingRow(Glyphs.Shield, "Key Vault",
                "Your own Azure Key Vault holds one secret per customer, so a new PC gets the same customers after you sign in. Enter the name of the vault or its address. The vault must use Azure role-based access control: the role Key Vault Secrets User reads, Key Vault Secrets Officer adds and changes.", first);
            vault.WrapAccessory = true;
            l.Add(vault);
            l.Add(new SettingRow(Glyphs2.Sync, "Get customers from the vault", "Adds the customers of the vault and updates the ones with the same name on this PC, the way Import does. Nothing is removed.", getStrip));
            l.Add(new SettingRow(Glyphs.Save, "Save customers to the vault", "You pick the customers. It needs the role Key Vault Secrets Officer. The storage links and customer codes go into the secrets. A secret that changed after your last sync is only written over when you agree.", saveStrip));
            l.Add(new SettingRow(Glyphs.Info, "Status", status.Length == 0 ? "Not signed in." : status, null));
            l.AddRange(SetupRows());
            inner.SetRows(l);
            foreach (SettingRow r in l) r.ApplyTheme(theme);
            bSign.Enabled = bGet.Enabled = bSave.Enabled = !busy;
        }

        void Say(string text, bool error)
        {
            note.Say(text, error);
        }

        void SetStatus(string text)
        {
            status = text;
            Build();
            refresh();
        }

        string Describe()
        {
            string who = signIn != null && signIn.SignedIn ? "Signed in as " + (signIn.Account ?? "an account") + "." : "Not signed in.";
            DateTime? last = VaultCustomers.ReadTime(settings.Get("VaultSynced"));
            return who + (last.HasValue ? " Last sync: " + last.Value.ToLocalTime().ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture) + "." : " Not synced yet.");
        }

        public int Measure(int width)
        {
            int h = inner.Measure(width);
            inner.SetBounds(0, 0, width, h);
            return h;
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            address.BackColor = t.Field;
            address.ForeColor = t.Text;
            bSign.ApplyTheme(t);
            bOut.ApplyTheme(t);
            bGet.ApplyTheme(t);
            bSave.ApplyTheme(t);
            ThemeSetup(t);
            inner.ApplyTheme(t);
        }

        // ---------- sign-in ----------

        VaultTarget Target()
        {
            string problem;
            VaultTarget t = VaultAddress.Parse(address.Text, out problem);
            if (t == null) Say(problem, true);
            else settings.Set("VaultAddress", address.Text.Trim());
            return t;
        }

        // The app of the customer in use when it has one, otherwise Microsoft Graph Command Line Tools.
        string ClientId()
        {
            Customer c = book.Current;
            return c != null && GraphClient.IsGuid(c.GraphClientId) ? c.GraphClientId : null;
        }

        // The sign-in kept from an earlier start, when it is for the same cloud.
        void Restore()
        {
            if (demo)
            {
                string p;
                VaultTarget t = VaultAddress.Parse(address.Text, out p);
                signIn = new VaultSignIn(web, t, null, null);
                signIn.Resume("demo", "ada@contoso.com");
                signInHost = t.LoginHost;
                return;
            }
            string rt = secrets.Load("VaultToken");
            if (string.IsNullOrEmpty(rt)) return;
            string problem;
            VaultTarget target = VaultAddress.Parse(address.Text, out problem);
            if (target == null || settings.Get("VaultLogin") != target.LoginHost) return;
            MakeSignIn(target);
            signIn.Resume(rt, settings.Get("VaultAccount"));
        }

        void MakeSignIn(VaultTarget target)
        {
            signIn = new VaultSignIn(web, target, ClientId(), null);
            signInHost = target.LoginHost;
            VaultSignIn s = signIn;
            s.TokenChanged = delegate (string rt)
            {
                secrets.Save("VaultToken", rt);
                settings.Set("VaultLogin", signInHost);
                settings.Set("VaultAccount", s.Account);
            };
        }

        void StartSignIn()
        {
            if (busy) return;
            VaultTarget target = Target();
            if (target == null) return;
            MakeSignIn(target);
            VaultSignIn s = signIn;
            busy = true;
            Say("Asking for a sign-in code...", false);
            Build();
            Work(delegate
            {
                DeviceCode d = s.Start();
                Post(delegate
                {
                    bool copied = Menus.Copy(d.UserCode);
                    if (!demo) { try { Process.Start(d.VerificationUri); } catch (Exception) { Menus.Copy(d.VerificationUri); } }
                    Say("Open " + d.VerificationUri + " and enter the code " + d.UserCode + (copied ? ". The code is copied." : ".") + " TrackVice waits for the sign-in.", false);
                });
                s.Wait(d, cancel);
                return null;
            }, delegate (object o)
            {
                settings.Set("VaultLogin", signInHost);
                settings.Set("VaultAccount", s.Account);
                secrets.Save("VaultToken", s.RefreshToken);
                Say("Signed in as " + (s.Account ?? "an account") + ".", false);
                SetStatus(Describe());
            }, delegate { signIn = null; });
        }

        void SignOut()
        {
            signIn = null;
            secrets.Save("VaultToken", null);
            settings.Set("VaultLogin", null);
            settings.Set("VaultAccount", null);
            Say("Signed out of the Key Vault. The customers on this PC are kept.", false);
            SetStatus(Describe());
        }

        // ---------- work away from the window ----------

        void Post(Action a)
        {
            if (demo || !owner.InvokeRequired) { a(); return; }
            try { owner.BeginInvoke((MethodInvoker)delegate { if (!owner.IsDisposed) a(); }); }
            catch (Exception) { }
        }

        // Runs job away from the window and done back in it. In the demo both run at once.
        void Work(Func<object> job, Action<object> done, Action failed)
        {
            busy = true;
            Build();
            if (demo)
            {
                Finish(job, null, done, failed);
                return;
            }
            Thread th = new Thread(delegate ()
            {
                object result = null;
                Exception error = null;
                try { result = job(); }
                catch (Exception ex) { error = ex; }
                Post(delegate { Finish(null, new object[] { result, error }, done, failed); });
            });
            th.IsBackground = true;
            th.Start();
        }

        void Finish(Func<object> demoJob, object[] outcome, Action<object> done, Action failed)
        {
            object result = null;
            Exception error = null;
            if (demoJob != null)
            {
                try { result = demoJob(); }
                catch (Exception ex) { error = ex; }
            }
            else
            {
                result = outcome[0];
                error = (Exception)outcome[1];
            }
            busy = false;
            if (error is OperationCanceledException) { Build(); return; }
            if (error != null)
            {
                if (failed != null) failed();
                Say(error.Message, true);
                Build();
                return;
            }
            Build();
            done(result);
        }

        KeyVaultStore Store(VaultTarget target)
        {
            if (signIn == null || !signIn.SignedIn) return null;
            VaultSignIn s = signIn;
            IVaultHttp http = demo ? (IVaultHttp)demoVault : new VaultHttp(web);
            return new KeyVaultStore(http, delegate { return s.Token(); }, target);
        }

        // ---------- get ----------

        void Get()
        {
            if (busy) return;
            VaultTarget target = Target();
            if (target == null) return;
            KeyVaultStore store = Store(target);
            if (store == null) { Say("Sign in first.", true); return; }
            DateTime started = DateTime.UtcNow;
            Say("Reading the customers of the vault...", false);
            Work(delegate
            {
                List<VaultSecret> got = new List<VaultSecret>();
                foreach (VaultSecret s in store.List())
                {
                    VaultSecret full = store.Get(s.Name);
                    if (full != null) got.Add(full);
                }
                return got;
            }, delegate (object o)
            {
                List<VaultSecret> got = (List<VaultSecret>)o;
                Customer before = book.Current == null ? null : book.Current.Copy();
                VaultGetResult r = VaultCustomers.Merge(book, got, secrets);
                if (live)
                {
                    book.Save();
                    CustomerUi.Edited(before);
                }
                settings.Set("VaultSynced", VaultCustomers.WriteTime(started));
                string text = got.Count == 0
                    ? "The vault has no customers yet."
                    : "Got from the vault: " + r.Added + " new, " + r.Updated + " updated." + (r.Skipped > 0 ? " " + r.Skipped + " could not be read." : "");
                status = Describe();
                Build();
                refresh();
                Say(text, false);
            }, null);
        }

        // ---------- save ----------

        sealed class Plan
        {
            public Customer Customer;
            public string Name, Value;
            public SaveDecision Decision;
        }

        void Save()
        {
            if (busy) return;
            VaultTarget target = Target();
            if (target == null) return;
            KeyVaultStore store = Store(target);
            if (store == null) { Say("Sign in first.", true); return; }
            if (book.Count == 0) { Say("There are no customers to save.", true); return; }
            List<Customer> picked;
            using (VaultPickDialog d = new VaultPickDialog(theme, book))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                picked = d.Picked;
            }
            if (picked.Count == 0) { Say("No customer was picked.", true); return; }
            DateTime? last = VaultCustomers.ReadTime(settings.Get("VaultSynced"));
            Say("Checking the vault...", false);
            Work(delegate
            {
                List<VaultSecret> all = store.List();
                List<Plan> plans = new List<Plan>();
                foreach (Customer c in picked)
                {
                    Plan p = new Plan();
                    p.Customer = c;
                    p.Name = VaultCustomers.SecretName(c.Name);
                    p.Value = VaultCustomers.ToValue(c, CustomerCodes.Load(secrets, c.Name));
                    p.Decision = VaultCustomers.Decide(p.Value, store.Get(p.Name), last);
                    plans.Add(p);
                }
                return new object[] { all, plans };
            }, delegate (object o)
            {
                object[] a = (object[])o;
                Write(store, (List<VaultSecret>)a[0], (List<Plan>)a[1], last);
            }, null);
        }

        void Write(KeyVaultStore store, List<VaultSecret> listed, List<Plan> plans, DateTime? last)
        {
            List<string> newer = new List<string>();
            foreach (Plan p in plans) if (p.Decision == SaveDecision.Newer) newer.Add(p.Customer.Name);
            bool overwrite = false;
            if (newer.Count > 0)
            {
                DialogResult r = MessageBox.Show(owner, "These secrets were changed in the vault after your last sync: " + string.Join(", ", newer.ToArray()) + ".\n\nWrite over them with the details on this PC? Yes writes over them. No leaves them as they are and saves the others.", "Save customers to the vault", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { Say("Nothing was saved.", false); return; }
                overwrite = r == DialogResult.Yes;
            }
            List<Plan> todo = new List<Plan>();
            int same = 0, left = 0;
            foreach (Plan p in plans)
            {
                if (p.Decision == SaveDecision.Same) same++;
                else if (p.Decision == SaveDecision.Newer && !overwrite) left++;
                else todo.Add(p);
            }
            if (todo.Count == 0)
            {
                Say(left > 0 ? "Nothing was saved. The secrets that changed in the vault were left as they are." : "The vault has these customers already.", false);
                return;
            }
            Say("Saving to the vault...", false);
            Work(delegate
            {
                foreach (Plan p in todo) store.Put(p.Name, p.Customer.Name, p.Value);
                return null;
            }, delegate (object o)
            {
                List<string> written = new List<string>();
                foreach (Plan p in todo) written.Add(p.Name);
                // The last sync only moves on when nobody else changed a secret since, so a change of someone else is not hidden.
                if (VaultCustomers.AllSeen(listed, written, last)) settings.Set("VaultSynced", VaultCustomers.WriteTime(DateTime.UtcNow));
                status = Describe();
                Build();
                refresh();
                Say("Saved " + todo.Count + " customer" + (todo.Count == 1 ? "" : "s") + " to the vault." + (same > 0 ? " " + same + " were the same already." : "") + (left > 0 ? " " + left + " left as they are in the vault." : ""), false);
            }, null);
        }

        // ---------- demo ----------

        static MemoryVault DemoVault()
        {
            MemoryVault v = new MemoryVault();
            DateTime t = DateTime.UtcNow.AddDays(-9);
            CustomerBook b = CustomerBook.Demo();
            foreach (Customer c in b.All) v.Add(VaultCustomers.SecretName(c.Name), c.Name, VaultCustomers.ToValue(c, null), t);
            return v;
        }
    }

    // Asks which customers to save to the vault.
    sealed class VaultPickDialog : PageForm
    {
        readonly List<Customer> customers = new List<Customer>();
        readonly List<Toggle> toggles = new List<Toggle>();

        public VaultPickDialog(Theme t, CustomerBook book) : base("Save customers to the vault", t)
        {
            Size = new Size(Dpi.S(700), Dpi.S(560));
            Add(new SectionTitle("Customers"));
            foreach (Customer c in book.All)
            {
                Toggle g = new Toggle();
                customers.Add(c);
                toggles.Add(g);
                Add(new SettingRow(Glyphs2.Device, c.Name, "Saved as the secret " + VaultCustomers.SecretName(c.Name) + ". The storage link and the customer code are saved with it.", g));
            }
            bClose.Text = "Cancel";
            bClose.Primary = false;
            bClose.ApplyTheme(t);
            FlatButton ok = new FlatButton("Save", Glyphs.Save);
            ok.Primary = true;
            ok.Width = Math.Max(ok.Width, Dpi.S(96));
            ok.Dock = DockStyle.Right;
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            ok.ApplyTheme(t);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            footer.Controls.Add(gap);
            footer.Controls.Add(ok);
            AcceptButton = null;
        }

        public List<Customer> Picked
        {
            get
            {
                List<Customer> l = new List<Customer>();
                for (int i = 0; i < customers.Count; i++) if (toggles[i].On) l.Add(customers[i]);
                return l;
            }
        }
    }
}
