namespace TrackVice
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Windows.Forms;

    // Publishes a TrackVice.exe for the agents to update themselves to: the exe and a signed agent/latest.json.
    public sealed class PublishDialog : PageForm
    {
        readonly IUpdateKeyStore keys;
        readonly Action<string> saveLink;
        readonly string saved, storageLink;
        readonly SettingRow keyRow, keyDetails, exeRow, publishRow, storageRow, noneRow, haveRow, boxRow;
        readonly Collapsible detailsBox, storageBox, noneBox, haveBox, boxBox;
        readonly FlatButton bKey, bCopy, bPublish;
        readonly TextBox link;
        string exePath, publicKey, note;
        bool busy, another;

        // True when the person chose Set up Your storage: the caller closes this dialog and opens the guide.
        public bool WantsSetUp;

        public PublishDialog(Theme t, IUpdateKeyStore keyStore, string exe, string savedLink, string storage, Action<string> save) : base("Publish agent update", t)
        {
            keys = keyStore;
            saveLink = save;
            exePath = exe;
            saved = savedLink ?? "";
            storageLink = storage ?? "";

            // Shows the details of the key, or creates the key pair when there is none yet.
            bKey = SmallButton("Details", delegate
            {
                if (publicKey != null) { detailsBox.Shown = !detailsBox.Shown; Relayout(); }
                else Run(false);
            });
            keyRow = new SettingRow(Glyphs.Shield, "Update key", "", bKey);
            bCopy = SmallButton("Copy", delegate { if (publicKey != null) Menus.Copy(publicKey); });
            bCopy.Glyph = Glyphs.Copy;
            bCopy.AutoWidth();
            bCopy.Width += Dpi.S(8);
            keyDetails = new SettingRow(Glyphs.Shield, "Public key", "", bCopy);
            detailsBox = new Collapsible(keyDetails);
            detailsBox.Shown = false;

            FlatButton bBrowse = SmallButton("Browse", delegate { Browse(); });
            exeRow = new SettingRow(Glyphs2.Apps, "TrackVice.exe", "", bBrowse);

            link = new TextBox();
            link.Width = Dpi.S(300);
            link.Font = Fonts.Mono;
            link.BorderStyle = BorderStyle.FixedSingle;
            link.BackColor = t.Field;
            link.ForeColor = t.Text;
            link.TextChanged += delegate { note = null; Refresh2(); };

            FlatButton bOther = SmallButton("Use another link or folder", delegate { SetAnother(true); });
            storageRow = new SettingRow(Glyphs2.Folder, "", "Agents read updates from the container trackvice-agent there.", bOther);
            FlatButton bSetUp = SmallButton("Set up Your storage", delegate { WantsSetUp = true; Close(); });
            bSetUp.Primary = true;
            noneRow = new SettingRow(Glyphs2.Folder, "Your storage is not set up", "Your storage is a storage account in your own Azure. Agents download TrackVice updates from it. Set it up once and Publish needs nothing more.", bSetUp);
            FlatButton bHave = SmallButton("I have a link or folder already", delegate { SetAnother(true); });
            haveRow = new SettingRow(Glyphs2.Folder, "Already have a place to publish to?", "", bHave);
            boxRow = new SettingRow(Glyphs2.Folder, "Link or folder", "The link of the container the agents read updates from, with Create and Write, or a folder. Use a container only you write to, such as trackvice-agent. It is saved encrypted for the customer in use.", link);
            storageBox = new Collapsible(storageRow);
            noneBox = new Collapsible(noneRow);
            haveBox = new Collapsible(haveRow);
            boxBox = new Collapsible(boxRow);

            bPublish = SmallButton("Publish", delegate { Run(true); });
            bPublish.Primary = true;
            publishRow = new SettingRow(Glyphs2.Sync, "Publish", "", new ButtonStrip(PublishExplainDialog.Button(t), bPublish));

            Add(new SectionTitle("Signing key"));
            Add(keyRow);
            Add(detailsBox);
            Add(new SectionTitle("Version to publish"));
            Add(exeRow);
            Add(new SectionTitle("Publish to"));
            Add(storageBox);
            Add(noneBox);
            Add(haveBox);
            Add(boxBox);
            Add(publishRow);

            // A saved folder is shown in the box, as it was before.
            if (PublishTarget.Decide(saved, storageLink, false, "").Kind == PublishTargetKind.UseLink) { another = true; link.Text = saved; }
            ShowKey();
            ShowExe();
        }

        public static PublishDialog ForCustomer(Theme t)
        {
            string name = "AgentUpdateStore." + (CustomerUi.CurrentName ?? CustomerBook.DefaultName);
            OwnStorage own = YourStorageUi.Load();
            return new PublishDialog(t, new DpapiKeyStore(), Program.Location, Secret.Load(name), own != null ? own.AgentWrite : "", delegate (string v) { Secret.Save(name, v); });
        }

        // Made-up key, for screenshots. page is publish (storage set up), publish-none or publish-link (not set up, box shown).
        public static PublishDialog ForDemo(Theme t, string page)
        {
            MemoryKeyStore ks = new MemoryKeyStore();
            bool created;
            using (UpdateKeys.LoadOrCreate(ks, out created)) { }
            string storage = page == "publish" || page == null ? "https://contosotrackvice.blob.core.windows.net/trackvice-agent?sv=2022-11-02&sp=cw&sig=made-up" : "";
            PublishDialog d = new PublishDialog(t, ks, Program.Location, "", storage, null);
            if (page == "publish-link") d.SetAnother(true);
            return d;
        }

        PublishTarget Target()
        {
            return PublishTarget.Decide(saved, storageLink, another, link.Text);
        }

        void SetAnother(bool on)
        {
            another = on;
            note = null;
            Refresh2();
        }

        // Shows the part of the page that fits where to publish to, and the Publish button with its reason.
        void Refresh2()
        {
            PublishTarget p = Target();
            storageBox.Shown = !another && p.Kind == PublishTargetKind.SetUp;
            noneBox.Shown = haveBox.Shown = !another && p.Kind == PublishTargetKind.NotSetUp;
            boxBox.Shown = another;
            storageRow.Title = "Publishing to your storage " + p.Account;
            storageRow.Invalidate();
            bool ok = File.Exists(exePath) && SemVer.OfFile(exePath) != null;
            bPublish.Enabled = ok && p.CanPublish && !busy;
            publishRow.Desc = note ?? (!p.CanPublish ? p.Reason : !ok ? "The version of TrackVice.exe could not be read. Choose another file." : "Uploads TrackVice-<version>.exe and agent/latest.json, signed with the update key. Agents check every 6 hours and never install an older version.");
            publishRow.Invalidate();
            Relayout();
        }

        void ShowKey()
        {
            RSACryptoServiceProvider k = null;
            try { k = UpdateKeys.Load(keys); }
            catch (Exception) { }
            publicKey = k != null ? UpdateKeys.PublicKey(k) : null;
            if (k != null) k.Dispose();
            bKey.Text = publicKey != null ? "Details" : "Create key";
            bKey.AutoWidth();
            bKey.Width += Dpi.S(8);
            keyRow.Desc = publicKey != null
                ? "Made and kept on this PC, encrypted for your Windows account."
                : "No key yet. Create it here, or it is made when you publish the first time. Devices only install a version signed with it.";
            keyDetails.Desc = publicKey != null
                ? "Fingerprint " + UpdateKeys.Fingerprint(publicKey) + ". With the script setup from links, copy the public key into $UpdateKey in Install-TrackViceAgent.ps1."
                : "";
            if (publicKey == null) detailsBox.Shown = false;
            keyRow.Invalidate();
            keyDetails.Invalidate();
            Relayout();
        }

        void ShowExe()
        {
            string v = File.Exists(exePath) ? SemVer.OfFile(exePath) : null;
            exeRow.Desc = exePath + (v != null ? " · version " + v : " · the version could not be read");
            exeRow.Invalidate();
            Refresh2();
        }

        void Browse()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "TrackVice|TrackVice*.exe|Programs|*.exe";
                d.Title = "Choose the TrackVice.exe to publish";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                exePath = d.FileName;
            }
            ShowExe();
        }

        void Say(string text)
        {
            note = text;
            publishRow.Desc = text;
            publishRow.Invalidate();
            Relayout();
        }

        // Creating a 3072-bit key and uploading take a few seconds, so both run away from the window.
        void Run(bool publish)
        {
            if (busy) return;
            PublishTarget target = Target();
            string to = target.Link;
            string exe = exePath;
            if (publish && !target.CanPublish)
            {
                Say(target.Reason);
                return;
            }
            busy = true;
            bPublish.Enabled = bKey.Enabled = false;
            Say(publish ? "Publishing..." : "Creating the key...");
            Thread th = new Thread(delegate ()
            {
                string result;
                try
                {
                    bool created;
                    using (RSACryptoServiceProvider key = UpdateKeys.LoadOrCreate(keys, out created))
                    {
                        if (!publish) result = "The key is created. Copy it into $UpdateKey before you publish.";
                        else
                        {
                            ISessionStore store = Stores.Open(to);
                            string version = SemVer.OfFile(exe);
                            if (version == null) throw new ArgumentException("The version of " + exe + " could not be read.");
                            UpdateManifest m = UpdatePublisher.Publish(store, File.ReadAllBytes(exe), version, key, DateTime.UtcNow);
                            if (saveLink != null && target.Kind == PublishTargetKind.UseLink) saveLink(to);
                            result = "Published version " + m.Version + " to " + store.Title + (created ? ". A new key was made: copy it into $UpdateKey, or the agents ignore this version." : ". Agents with this key install it within 6 hours.");
                        }
                    }
                }
                catch (Exception ex) { result = "Not published: " + ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        bKey.Enabled = true;
                        note = null;
                        ShowKey();
                        ShowExe();
                        Say(result);
                    });
                }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }
    }
}
