namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // The parts of a guide that read the account key of your storage when it is not kept, and check its security.
    abstract partial class GuideForm
    {
        protected WebSignIn ownIn;
        protected string ownText = "";
        FlatButton bOwnIn;
        string ownTenant = "";

        protected bool OwnSignedIn
        {
            get { return ownIn != null && ownIn.SignedIn; }
        }

        protected SettingRow OwnSignInRow(OwnStorage own, string why)
        {
            ownTenant = own != null ? own.Tenant : "";
            if (bOwnIn == null) bOwnIn = Button("Sign in", delegate { SignInOwn(); });
            bOwnIn.Text = OwnSignedIn ? "Sign in again" : pending != null && ownIn != null ? "Copy code and open" : "Sign in";
            bOwnIn.AutoWidth();
            bOwnIn.Width += Dpi.S(8);
            return Row(Glyphs2.Sync, "Sign in to Azure", ownText.Length > 0 ? ownText : why, bOwnIn);
        }

        protected void SignInOwn()
        {
            if (busy && pending != null && ownIn != null && !ownIn.SignedIn) { ShowCode(pending); return; }
            if (busy) return;
            WebSignIn s = SignIns.Get(web, Arm.ClientId, ownTenant, Arm.Scope);
            ownIn = s;
            SignIn(s, delegate (string t) { ownText = t; }, null, null, delegate { ownText = ""; ownIn = null; });
        }

        protected Arm OwnArm()
        {
            Arm a = new Arm(web, ownIn.Token);
            if (demo) a.Pause = delegate { };
            return a;
        }

        // In a running step: your storage with its key, read through the sign-in when it is not kept.
        protected OwnStorage OwnWithKey(OwnStorage own)
        {
            if (own.KeyKept) return own;
            if (!OwnSignedIn) throw new SetupException("Sign in to Azure first. The account key of your storage is not kept on this PC.");
            return StorageFinder.WithKey(OwnArm(), own, DateTime.UtcNow);
        }

        // ---------- security check ----------

        protected SecurityCheck check;

        // Reads the check in a running step. own gets the account's ID, region and the state of the delete rule.
        protected void ReadSecurity(Arm arm, OwnStorage own, AzureAccount account)
        {
            AzureAccount a = account ?? StorageFinder.Locate(arm, own);
            SecurityCheck c = new StorageSecurity(arm).Read(a);
            own.Id = a.Id ?? own.Id;
            if (!string.IsNullOrEmpty(a.Location)) own.Region = a.Location;
            own.RuleDays = c.RuleThere ? c.RuleDays : 0;
            check = c;
        }

        protected void AddSecurityRows(List<Control> l, OwnStorage own, Func<Arm> arm, ISecretStore secrets)
        {
            if (check == null) return;
            l.Add(new SectionTitle("Security of " + check.Account.Name));
            l.Add(Row(Glyphs.Info, "Before a change", "The account may also be used for other things. TrackVice asks before each change, changes only that setting, and never changes network rules or keys.", null));
            foreach (SecurityItem i in check.Items(own.RetentionDays))
            {
                SecurityItem one = i;
                if (i.Ok || i.Info)
                {
                    SettingRow r = Row(Glyphs.Shield, i.Title, i.Line, null);
                    r.Value = i.Info ? (check.SharedKeyOn ? "On" : "Off") : "In order";
                    l.Add(r);
                    continue;
                }
                FlatButton b = SmallButton(i.Button, delegate { FixSecurity(one, own, arm, secrets); });
                l.Add(Row(Glyphs.Shield, i.Title, i.Line, b));
            }
        }

        void FixSecurity(SecurityItem item, OwnStorage own, Func<Arm> arm, ISecretStore secrets)
        {
            if (busy || check == null) return;
            AzureAccount a = check.Account;
            string q = "Change the storage account " + a.Name + ": " + item.Change + "?\n\nThe account may also be used for other things. Only this setting is changed.";
            if (!demo && MessageBox.Show(this, q, "Check security", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Arm api = arm();
            int days = own.RetentionDays;
            Work("Changing " + a.Name + "...", delegate
            {
                new StorageSecurity(api).Fix(a, item.Id, days);
                ReadSecurity(api, own, a);
            }, delegate { SaveOwn(own, secrets); note.Say(item.Title + ": changed.", false); }, null);
        }

        // Saves what the check found, keeping the key as it is kept now.
        protected void SaveOwn(OwnStorage found, ISecretStore secrets)
        {
            if (demo) return;
            OwnStorage kept = OwnStorage.Load(secrets);
            if (kept == null || kept.Account != found.Account) return;
            kept.Id = found.Id;
            kept.Region = found.Region;
            kept.RuleDays = found.RuleDays;
            kept.Save(secrets);
        }
    }

    // Settings > Your storage > Check security: sign in to Azure, read the account, and fix one setting at a time.
    sealed class StorageSecurityDialog : GuideForm
    {
        readonly ISecretStore secrets;
        readonly OwnStorage own;
        readonly FlatButton bAgain;

        public StorageSecurityDialog(Theme t, IWeb web, ISecretStore secrets, bool isDemo) : base("Check security", t, web, isDemo)
        {
            this.secrets = secrets;
            own = OwnStorage.Load(secrets);
            Size = new Size(Dpi.S(860), Dpi.S(800));
            bAgain = Button("Check again", delegate { Check(); });
            Add(body);
            Rebuild();
        }

        public static StorageSecurityDialog ForLive(Theme t)
        {
            StorageSecurityDialog d = new StorageSecurityDialog(t, YourStorageUi.Web, YourStorageUi.Secrets, false);
            d.Shown += delegate
            {
                OwnStorage own = OwnStorage.Load(YourStorageUi.Secrets);
                if (SignIns.Has(d.web, Arm.ClientId, own != null ? own.Tenant : "", Arm.Scope)) d.SignInOwn();
            };
            return d;
        }

        public static StorageSecurityDialog ForDemo(Theme t)
        {
            StorageSecurityDialog d = new StorageSecurityDialog(t, new DemoStorageWeb(true), YourStorageUi.DemoSecrets(), true);
            d.SignInOwn();
            d.note.Say("", false);
            return d;
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle("Check security"));
            if (own == null)
            {
                l.Add(Row(Glyphs.Info, "Your storage is not set up", "Set it up in Settings > Your storage first.", null));
                return;
            }
            l.Add(Row(Glyphs.Info, "What this does", "Reads the storage account " + own.Account + " through Azure: anonymous blob access, HTTPS only, the minimum TLS version, soft delete of blobs and containers, and the lifecycle rule that deletes old uploads. Reading changes nothing. A setting that is not in order has a button that changes only that setting, after you confirm.", null));
            l.Add(OwnSignInRow(own, "With a code in the browser, as an account with Storage Account Contributor on " + own.Account + "."));
            if (check != null)
            {
                AddSecurityRows(l, own, OwnArm, secrets);
                l.Add(Row(Glyphs2.Sync, "Check again", "Reads the account again.", bAgain));
            }
        }

        protected override void Nav()
        {
            // A sign-in that has just finished reads the account once.
            if (!busy && OwnSignedIn && check == null && !checking) Check();
        }

        bool checking;

        void Check()
        {
            if (busy || !OwnSignedIn || own == null) return;
            checking = true;
            Arm arm = OwnArm();
            Work("Reading " + own.Account + "...", delegate { ReadSecurity(arm, own, null); }, delegate { checking = false; SaveOwn(own, secrets); }, delegate { checking = false; });
        }
    }

    // Removes a customer: its access policies, so every link of it stops, and the container only when that is ticked.
    sealed class RemoveCustomerDialog : GuideForm
    {
        readonly Customer customer;
        readonly CustomerBook book;
        readonly ISecretStore secrets;
        readonly bool live;
        readonly OwnStorage own;
        readonly string container;
        readonly bool oldLinks;
        readonly Toggle gDelete;
        readonly FlatButton bRemove;
        List<string> done;

        public bool Removed
        {
            get { return done != null; }
        }

        public RemoveCustomerDialog(Theme t, Customer c, CustomerBook book, IWeb web, ISecretStore secrets, bool isDemo) : base("Remove " + c.Name, t, web, isDemo)
        {
            customer = c;
            this.book = book;
            this.secrets = secrets;
            live = book == CustomerUi.Book;
            own = OwnStorage.Load(secrets);
            string code = CustomerCodes.Load(secrets, c.Name);
            string upload = null;
            try { if (code != null) upload = CustomerCode.Parse(code).Upload; }
            catch (Exception) { }
            container = CustomerAccess.ContainerOf(own, upload) ?? CustomerAccess.ContainerOf(own, c.AgentLocation);
            oldLinks = container != null && !CustomerAccess.UsesPolicy(upload ?? c.AgentLocation);
            Size = new Size(Dpi.S(820), Dpi.S(700));
            gDelete = Kept(new Toggle());
            bRemove = Button("Remove", delegate { Remove(); });
            bRemove.Primary = true;
            Add(body);
            Rebuild();
        }

        public static RemoveCustomerDialog ForDemo(Theme t)
        {
            MemorySecrets sec = YourStorageUi.DemoSecrets();
            IWeb w = new DemoStorageWeb(true);
            OwnStorage o = OwnStorage.Load(sec);
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            r.MakeStorage(w, o, DateTime.UtcNow);
            r.MakeCode(o, AddCustomerUi.PublicKey(YourStorageUi.DemoKeys()));
            CustomerCodes.Save(sec, "Contoso", r.Code.ToString());
            CustomerBook b = CustomerBook.Demo();
            Customer c = b.Find("Contoso").Copy();
            c.AgentLocation = r.Viewer;
            b.Update("Contoso", c);
            return new RemoveCustomerDialog(t, b.Find("Contoso"), b, w, sec, true);
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle("Remove " + customer.Name));
            if (done != null)
            {
                l.Add(Row(Glyphs.Save, "Done", string.Join("\n", done.ToArray()), null));
                l.Add(Row(Glyphs.Info, "Devices", "Installed agents can no longer upload, and remove themselves when their time is up. Remove the platform script " + AddCustomerRun.ScriptName + " or its assignment in the customer's Intune.", null));
                return;
            }
            if (container == null)
            {
                l.Add(Row(Glyphs.Info, "The data is not in your storage", "The uploads of " + customer.Name + " go to " + (CustomerUi.Where(customer).Length > 0 ? CustomerUi.Where(customer) : "a place TrackVice did not make") + ". TrackVice cannot stop those links. Remove forgets the customer and its customer code on this PC. To stop the links, the customer deletes the storage account or rotates its account key.", null));
                l.Add(Row(Glyphs.Shield, "Remove customer", "Forgets " + customer.Name + " on this PC. Nothing in Azure is changed.", bRemove));
                return;
            }
            l.Add(Row(Glyphs.Info, "What Remove does", "Removes the access policies " + CustomerAccess.Upload + " and " + CustomerAccess.Viewer + " from the container " + container + " in " + own.Account + ". Every link of " + customer.Name + " stops within 30 seconds: devices can no longer upload, and the viewer link can no longer read. Then the customer and its customer code are removed from TrackVice on this PC.", null));
            if (oldLinks)
                l.Add(Row(Glyphs.Shield, "Links without a policy", "The links of " + customer.Name + " were signed without an access policy, so removing the policies does not stop them. Delete the container to stop them, or rotate the account key, which stops the links of every customer.", null));
            l.Add(Row(Glyphs2.Folder, "Also delete the container " + container + " with all its data", "Every session and log zip of " + customer.Name + " is deleted. With container soft delete on, Azure can bring it back for 7 days. Off: the data stays until the lifecycle rule deletes it.", gDelete));
            if (!own.KeyKept) l.Add(OwnSignInRow(own, "The account key of " + own.Account + " is not kept on this PC. Sign in to read it for this removal; it is not kept."));
            l.Add(Row(Glyphs.Shield, "Remove customer", "Asks once more, then removes.", bRemove));
        }

        void Remove()
        {
            if (busy) return;
            bool delete = gDelete.On && container != null;
            string q = container == null
                ? "Forget " + customer.Name + " and its customer code on this PC?"
                : "Remove " + customer.Name + "? Every link of " + customer.Name + " stops within 30 seconds." + (delete ? "\n\nThe container " + container + " and all its data are deleted." : "\n\nThe container " + container + " and its data are kept.");
            if (!demo && MessageBox.Show(this, q, "Remove customer", MessageBoxButtons.OKCancel, delete ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;
            if (container != null && !own.KeyKept && !OwnSignedIn) { note.Say("Sign in to Azure first.", true); return; }
            List<string> lines = new List<string>();
            Work("Removing " + customer.Name + "...", delegate
            {
                if (container != null) lines.AddRange(CustomerAccess.Remove(web, OwnWithKey(own), container, delete, DateTime.UtcNow));
            }, delegate
            {
                Customer before = book.Current == null ? null : book.Current.Copy();
                book.Remove(customer.Name);
                if (live)
                {
                    book.Save();
                    CustomerUi.Edited(before);
                }
                CustomerCodes.Save(secrets, customer.Name, null);
                lines.Add("Removed " + customer.Name + " and its customer code from TrackVice on this PC");
                done = lines;
                bClose.Primary = true;
                bClose.Invalidate();
            }, null);
        }
    }

    // A choice with a title and a description, as a card. Cards in one group work like option buttons.
    sealed class ChoiceCard : FlatControl, IMeasured
    {
        public string Title = "", Desc = "";
        bool chosen;
        public List<ChoiceCard> Group;
        public event EventHandler Chosen;

        public ChoiceCard(string title, string desc)
        {
            Title = title;
            Desc = desc;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public bool IsChosen
        {
            get { return chosen; }
            set { chosen = value; Invalidate(); }
        }

        int TextW(int width)
        {
            return Math.Max(Dpi.S(80), width - Dpi.S(56) - Dpi.S(20));
        }

        public int Measure(int width)
        {
            return Dpi.S(14) + Fonts.UiSemibold.Height + Dpi.S(4) + Draw.WrapHeight(Desc, Fonts.Small, TextW(width)) + Dpi.S(14);
        }

        void Choose()
        {
            if (Group != null) foreach (ChoiceCard c in Group) c.IsChosen = c == this;
            else IsChosen = true;
            if (Chosen != null) Chosen(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            Choose();
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { e.Handled = true; Choose(); }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(7), chosen ? Theme.Mix(T.Card, T.Accent, T.IsDark ? 0.12f : 0.06f) : hover ? T.Hover : T.Card);
            Shapes.Stroke(g, r, Dpi.S(7), chosen ? T.Accent : T.Divider, chosen ? Math.Max(1f, Dpi.S(1.5f)) : 1f);
            int d = Dpi.S(18);
            int cy = Dpi.S(14) + Fonts.UiSemibold.Height / 2;
            RectangleF dot = new RectangleF(Dpi.S(20), cy - d / 2f, d, d);
            using (Pen p = new Pen(chosen ? T.Accent : T.Text2, Math.Max(1f, Dpi.S(1.5f)))) g.DrawEllipse(p, dot);
            if (chosen)
            {
                float k = d * 0.5f;
                using (SolidBrush b = new SolidBrush(T.Accent)) g.FillEllipse(b, dot.X + (d - k) / 2f, dot.Y + (d - k) / 2f, k, k);
            }
            int tx = Dpi.S(56), tw = TextW(Width);
            Draw.Text(g, Title, Fonts.UiSemibold, new Rectangle(tx, Dpi.S(14), tw, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
            int y = Dpi.S(14) + Fonts.UiSemibold.Height + Dpi.S(4);
            Draw.Text(g, Desc, Fonts.Small, new Rectangle(tx, y, tw, Height - y), T.Text2, Draw.Wrap);
            if (Focused && ShowFocusCues) Shapes.Stroke(g, RectangleF.Inflate(r, -2, -2), Dpi.S(6), T.Text, 1f);
        }
    }
    // Publish when the account key is not kept: sign in to Azure, read the key for this publish, and forget it.
    sealed class OwnPublishDialog : GuideForm
    {
        readonly ISecretStore secrets;
        readonly IUpdateKeyStore keys;
        readonly OwnStorage own;
        readonly FlatButton bPublish;
        public string Message = "";
        public bool Ok;

        public OwnPublishDialog(Theme t, IWeb web, ISecretStore secrets, IUpdateKeyStore keys, bool isDemo) : base("Publish to your storage", t, web, isDemo)
        {
            this.secrets = secrets;
            this.keys = keys;
            own = OwnStorage.Load(secrets);
            Size = new Size(Dpi.S(780), Dpi.S(480));
            bPublish = Button("Publish", delegate { Publish(); });
            bPublish.Primary = true;
            Add(body);
            Rebuild();
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle("Publish TrackVice " + YourStorageUi.RunningVersion));
            if (own == null) { l.Add(Row(Glyphs.Info, "Your storage is not set up", "Set it up in Settings > Your storage first.", null)); return; }
            l.Add(OwnSignInRow(own, "The account key of " + own.Account + " is not kept on this PC. Sign in to read it for this publish; it is not kept."));
            l.Add(Row(Glyphs.Save, "Publish", Message.Length > 0 ? Message : "Uploads this TrackVice with a signed agent/latest.json to " + Arm.AgentFiles + ". Agents install it within 6 hours.", bPublish));
        }

        void Publish()
        {
            if (busy || own == null || Ok) return;
            if (!OwnSignedIn) { note.Say("Sign in to Azure first.", true); return; }
            string msg = "";
            bool ok = false;
            Work("Publishing...", delegate
            {
                OwnStorage withKey = OwnWithKey(own);
                ok = YourStorageUi.PublishWith(web, withKey, own, secrets, keys, demo, out msg);
            }, delegate { Message = msg; Ok = ok; if (!ok) note.Say(msg, true); }, null);
        }
    }
}
