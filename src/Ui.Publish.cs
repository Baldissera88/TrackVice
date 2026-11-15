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
        readonly SettingRow keyRow, exeRow, publishRow;
        readonly FlatButton bKey, bPublish;
        readonly TextBox link;
        string exePath, publicKey;
        bool busy;

        public PublishDialog(Theme t, IUpdateKeyStore keyStore, string exe, string savedLink, Action<string> save) : base("Publish agent update", t)
        {
            keys = keyStore;
            saveLink = save;
            exePath = exe;

            // Copies the public key, or creates the key pair when there is none yet.
            bKey = SmallButton("Create key", delegate
            {
                if (publicKey != null) Menus.Copy(publicKey);
                else Run(false);
            });
            keyRow = new SettingRow(Glyphs.Shield, "Update key", "", bKey);

            FlatButton bBrowse = SmallButton("Browse", delegate { Browse(); });
            exeRow = new SettingRow(Glyphs2.Apps, "TrackVice.exe", "", bBrowse);

            link = new TextBox();
            link.Width = Dpi.S(300);
            link.Font = Fonts.Mono;
            link.BorderStyle = BorderStyle.FixedSingle;
            link.Text = savedLink ?? "";

            bPublish = SmallButton("Publish", delegate { Run(true); });
            bPublish.Primary = true;
            publishRow = new SettingRow(Glyphs2.Sync, "Publish", "Uploads TrackVice-<version>.exe and agent/latest.json, signed with the update key. Agents check every 6 hours and never install an older version.", bPublish);

            Add(new SectionTitle("Signing key"));
            Add(keyRow);
            Add(new SectionTitle("Version to publish"));
            Add(exeRow);
            Add(new SettingRow(Glyphs2.Folder, "Publish to", "The SAS link of the container the agents read updates from, with Create and Write, or a folder. Use a container only you write to, such as trackvice-agent. It is saved encrypted for the customer in use.", link));
            Add(publishRow);
            ShowKey();
            ShowExe();
        }

        public static PublishDialog ForCustomer(Theme t)
        {
            string name = "AgentUpdateStore." + (CustomerUi.CurrentName ?? CustomerBook.DefaultName);
            return new PublishDialog(t, new DpapiKeyStore(), Program.Location, Secret.Load(name), delegate (string v) { Secret.Save(name, v); });
        }

        // Made-up key and no saved link, for screenshots.
        public static PublishDialog ForDemo(Theme t)
        {
            MemoryKeyStore ks = new MemoryKeyStore();
            bool created;
            using (UpdateKeys.LoadOrCreate(ks, out created)) { }
            return new PublishDialog(t, ks, Program.Location, "", null);
        }

        void ShowKey()
        {
            RSACryptoServiceProvider k = null;
            try { k = UpdateKeys.Load(keys); }
            catch (Exception) { }
            publicKey = k != null ? UpdateKeys.PublicKey(k) : null;
            if (k != null) k.Dispose();
            bKey.Text = publicKey != null ? "Copy" : "Create key";
            bKey.Glyph = publicKey != null ? Glyphs.Copy : null;
            bKey.AutoWidth();
            bKey.Width += Dpi.S(8);
            keyRow.Desc = publicKey != null
                ? "Fingerprint " + UpdateKeys.Fingerprint(publicKey) + ". Copy the public key into $UpdateKey in Install-TrackViceAgent.ps1. The private key stays on this PC, encrypted for your Windows account."
                : "No key yet. Create it here, or it is made when you publish the first time. Devices only install a version signed with it.";
            Relayout();
        }

        void ShowExe()
        {
            string v = File.Exists(exePath) ? SemVer.OfFile(exePath) : null;
            exeRow.Desc = exePath + (v != null ? " · version " + v : " · the version could not be read");
            bPublish.Enabled = v != null && !busy;
            Relayout();
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
            publishRow.Desc = text;
            publishRow.Invalidate();
            Relayout();
        }

        // Creating a 3072-bit key and uploading take a few seconds, so both run away from the window.
        void Run(bool publish)
        {
            if (busy) return;
            string to = link.Text.Trim().Trim('"');
            string exe = exePath;
            if (publish && to.Length == 0)
            {
                Say("Enter where to publish: the container SAS link with Create and Write, or a folder.");
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
                            if (saveLink != null) saveLink(to);
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
