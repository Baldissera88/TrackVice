namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Globalization;
    using System.Windows.Forms;

    // The questions of VaultSetup as message boxes. Each has No as the default. It may be asked from a thread away from the window.
    sealed class VaultSetupAsk : IVaultSetupAsk
    {
        readonly Form owner;

        public VaultSetupAsk(Form owner)
        {
            this.owner = owner;
        }

        bool Yes(string text, string title)
        {
            if (owner != null && owner.InvokeRequired)
            {
                bool answer = false;
                owner.Invoke((MethodInvoker)delegate { answer = Yes(text, title); });
                return answer;
            }
            return MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        public bool ReplaceVaultKey(string vaultFingerprint, string localFingerprint)
        {
            string text = VaultSetup.AskReplaceVault + "\n\n"
                + (vaultFingerprint == null ? "This account cannot read the key in the vault, so TrackVice cannot tell whether it is the same as the one on this PC." : "Update key in the vault: " + vaultFingerprint)
                + "\nUpdate key on this PC: " + localFingerprint + "\n\nReplace the key in the vault? No leaves it as it is.";
            return Yes(text, "Save my setup to the vault");
        }

        public bool ReplaceLocalKey(string localFingerprint, string vaultFingerprint)
        {
            string text = VaultSetup.AskReplaceLocal + "\n\nUpdate key on this PC: " + localFingerprint + "\nUpdate key in the vault: " + vaultFingerprint + "\n\nReplace the key on this PC? No keeps it.";
            return Yes(text, "Get my setup from the vault");
        }

        public bool UseVaultStorage(OwnStorage local, OwnStorage vault)
        {
            string text = "The vault has storage settings for " + vault.Account + ", with links that run until " + Day(vault.ExpiresUtc) + ".\n"
                + (local == null ? "" : local.ExpiresUtc == default(DateTime) ? "The settings on this PC have no expiry.\n" : "The settings on this PC run until " + Day(local.ExpiresUtc) + ".\n")
                + "\nUse the settings of the vault on this PC?";
            return Yes(text, "Get my setup from the vault");
        }

        static string Day(DateTime t)
        {
            return t == default(DateTime) ? "no known date" : t.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    // Get my setup from the vault, for the places that have no Key Vault section of their own: Publish and Get started.
    public static class VaultSetupUi
    {
        // Screenshots show the link as if a vault address was saved.
        public static bool DemoAddress;
        public const string GetStartedId = "vaultsetup";

        public static bool HasAddress()
        {
            if (DemoAddress) return true;
            string problem;
            return VaultAddress.Parse(Prefs.Get("VaultAddress", null), out problem) != null;
        }

        // Signs in with the sign-in kept in Settings > Customers, gets the setup and returns what happened in words.
        public static string RunGet(Form owner, IUpdateKeyStore keys, ISecretStore secrets)
        {
            string problem;
            VaultTarget target = VaultAddress.Parse(Prefs.Get("VaultAddress", null), out problem);
            if (target == null) return problem;
            ISettingStore settings = new PrefsSettingStore();
            ISecretStore tokens = new PrefsSecrets();
            string rt = tokens.Load("VaultToken");
            if (string.IsNullOrEmpty(rt) || settings.Get("VaultLogin") != target.LoginHost)
                return "Sign in to the Key Vault first, in Settings > Customers.";
            Customer c = CustomerUi.Book.Current;
            string clientId = c != null && GraphClient.IsGuid(c.GraphClientId) ? c.GraphClientId : null;
            IWeb web = new HttpWeb();
            VaultSignIn s = new VaultSignIn(web, target, clientId, null);
            s.Resume(rt, settings.Get("VaultAccount"));
            s.TokenChanged = delegate (string t) { tokens.Save("VaultToken", t); };
            Cursor was = owner.Cursor;
            try
            {
                owner.Cursor = Cursors.WaitCursor;
                return new VaultSetup(new VaultHttp(web), delegate { return s.Token(); }, target).Get(secrets, keys, new VaultSetupAsk(owner)).Text;
            }
            catch (Exception ex) { return "Could not get the setup from the vault. " + ex.Message; }
            finally { owner.Cursor = was; }
        }
    }

    // A line of text that works as a link.
    sealed class LinkLine : FlatControl, IMeasured
    {
        readonly string text;

        public LinkLine(string text)
        {
            this.text = text;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Measure(int width)
        {
            return Fonts.Ui.Height + Dpi.S(4);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : T.Bg);
            Font f = new Font(Fonts.Ui, hover || Focused ? FontStyle.Underline : FontStyle.Regular);
            try { Draw.Text(e.Graphics, text, f, new Rectangle(Dpi.S(4), 0, Width - Dpi.S(4), Height), T.Accent, Draw.Line); }
            finally { f.Dispose(); }
        }
    }

    public sealed partial class GetStartedDialog
    {
        // Under step 1, when Your storage is not set up and a vault address is saved.
        void AddVaultLink(GetStartedStep first)
        {
            if (first.Id != GetStarted.Storage || first.Status != StepStatus.ToDo || !VaultSetupUi.HasAddress()) return;
            LinkLine l = new LinkLine("Already set up on another PC? Get it from your Key Vault");
            l.Click += Safe.Wrap(delegate { Chosen = VaultSetupUi.GetStartedId; Close(); });
            Add(l);
        }
    }

    // The setup rows of the Key Vault section: save and get the setup, what the vault holds, and who may do what.
    sealed partial class VaultRows
    {
        FlatButton bSetSave, bSetGet, bSetCheck;
        ButtonStrip[] setupStrips = new ButtonStrip[0];
        string setupStatus = "Not read yet. Check the vault to see what it holds.";

        List<SettingRow> SetupRows()
        {
            if (demo && setupStatus.StartsWith("Not read", StringComparison.Ordinal)) setupStatus = "The vault holds: storage account contosoagents, update key 3A 5F 91 C2 0B 7E 44 D8.";
            if (bSetSave == null)
            {
                bSetSave = Button("Save my setup to the vault", delegate { SetupSave(); });
                bSetGet = Button("Get my setup from the vault", delegate { SetupGet(); });
                bSetCheck = Button("Check the vault", delegate { SetupCheck(); });
            }
            foreach (ButtonStrip s in setupStrips) s.Controls.Clear();
            ButtonStrip strip = new ButtonStrip(bSetSave, bSetGet, bSetCheck);
            setupStrips = new ButtonStrip[] { strip };
            List<SettingRow> l = new List<SettingRow>();
            SettingRow main = new SettingRow(Glyphs.Save, "My setup in the vault",
                "Your storage settings and the update key of this PC, as two secrets in the vault. A PC you sign in on gets them, and can then publish. Save needs the role Key Vault Secrets Officer. Nothing in the vault is deleted.", strip);
            main.WrapAccessory = true;
            l.Add(main);
            l.Add(new SettingRow(Glyphs.Info, "What the vault holds", setupStatus, null));
            l.Add(new SettingRow(Glyphs.Shield, "Who may do what", VaultSetup.RoleNote + " Roles can be set per secret in Azure role-based access control.", null));
            bSetSave.Enabled = bSetGet.Enabled = bSetCheck.Enabled = !busy;
            return l;
        }

        void ThemeSetup(Theme t)
        {
            if (bSetSave == null) return;
            bSetSave.ApplyTheme(t);
            bSetGet.ApplyTheme(t);
            bSetCheck.ApplyTheme(t);
        }

        VaultSetup SetupStore(VaultTarget target)
        {
            if (signIn == null || !signIn.SignedIn) return null;
            VaultSignIn s = signIn;
            IVaultHttp http = demo ? (IVaultHttp)demoVault : new VaultHttp(web);
            return new VaultSetup(http, delegate { return s.Token(); }, target);
        }

        IUpdateKeyStore SetupKeys()
        {
            return demo ? new MemoryKeyStore() : YourStorageUi.Keys;
        }

        static string Look(VaultSetup vs)
        {
            try { return vs.Describe(); }
            catch (Exception ex) { return ex.Message; }
        }

        void SetupDone(object o)
        {
            object[] a = (object[])o;
            VaultSetupResult r = (VaultSetupResult)a[0];
            setupStatus = (string)a[1];
            Build();
            refresh();
            Say(r.Text, false);
        }

        void SetupSave()
        {
            if (busy) return;
            if (demo) { Say("This is a demo. Nothing is sent to a vault.", false); return; }
            VaultTarget target = Target();
            if (target == null) return;
            VaultSetup vs = SetupStore(target);
            if (vs == null) { Say("Sign in first.", true); return; }
            OwnStorage own = OwnStorage.Load(secrets);
            IUpdateKeyStore keys = SetupKeys();
            Say("Saving your setup to the vault...", false);
            Work(delegate
            {
                VaultSetupResult r = vs.Save(own, keys, new VaultSetupAsk(owner));
                return new object[] { r, Look(vs) };
            }, delegate (object o) { SetupDone(o); }, null);
        }

        void SetupGet()
        {
            if (busy) return;
            if (demo) { Say("This is a demo. Nothing is read from a vault.", false); return; }
            VaultTarget target = Target();
            if (target == null) return;
            VaultSetup vs = SetupStore(target);
            if (vs == null) { Say("Sign in first.", true); return; }
            ISecretStore keep = secrets;
            IUpdateKeyStore keys = SetupKeys();
            Say("Reading your setup from the vault...", false);
            Work(delegate
            {
                VaultSetupResult r = vs.Get(keep, keys, new VaultSetupAsk(owner));
                return new object[] { r, Look(vs) };
            }, delegate (object o) { SetupDone(o); }, null);
        }

        void SetupCheck()
        {
            if (busy) return;
            if (demo) { Say("This is a demo. Nothing is read from a vault.", false); return; }
            VaultTarget target = Target();
            if (target == null) return;
            VaultSetup vs = SetupStore(target);
            if (vs == null) { Say("Sign in first.", true); return; }
            Say("Reading the vault...", false);
            Work(delegate { return Look(vs); }, delegate (object o)
            {
                setupStatus = (string)o;
                Build();
                refresh();
                Say("Read the vault.", false);
            }, null);
        }
    }

    // Publish: the button that appears under the message about another update key.
    public sealed partial class PublishDialog
    {
        Collapsible vaultRow;
        public const string OtherKeyMarker = "is signed with another update key than the one on this PC";

        // A row with the button "Get the update key from the vault", shown when the message says the key is another one.
        void AddVaultRow()
        {
            FlatButton b = SmallButton("Get the update key from the vault", delegate { GetKeyFromVault(); });
            vaultRow = new Collapsible(new SettingRow(Glyphs.Shield, "The update key is in your Key Vault",
                "Another PC made the key that signed the published version. Get it from the vault to publish from this PC. If this PC has another key, you are asked first.", b));
            vaultRow.Shown = false;
            Add(vaultRow);
        }

        void ShowVaultRow(string message)
        {
            if (vaultRow == null) return;
            vaultRow.Shown = VaultSetupUi.DemoAddress || (message != null && message.IndexOf(OtherKeyMarker, StringComparison.Ordinal) >= 0 && VaultSetupUi.HasAddress());
        }

        void GetKeyFromVault()
        {
            string text = VaultSetupUi.RunGet(this, keys, YourStorageUi.Secrets);
            ShowKey();
            note = null;
            Say(text);
        }
    }
}
