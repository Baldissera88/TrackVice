namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Windows.Forms;

    // Publishes a TrackVice.exe for the agents to update themselves to: the exe and a signed agent/latest.json, to every
    // place where the agents of the customers check for updates.
    public sealed partial class PublishDialog : PageForm
    {
        sealed class PlaceRow
        {
            public UpdateLocation Place;
            public Toggle Tick;
            public SettingRow Row, LinkRow;
            public Collapsible LinkBox;
            public TextBox LinkText;
        }

        readonly IUpdateKeyStore keys;
        readonly Action<string> saveLink;
        readonly Action<UpdateLocation, string> savePlaceLink;
        readonly Func<string, ISessionStore> open;
        readonly List<UpdateLocation> places;
        readonly bool reads;
        readonly Dictionary<string, bool> ticks = new Dictionary<string, bool>();
        readonly List<PlaceRow> rows = new List<PlaceRow>();
        readonly SettingRow keyRow, keyDetails, exeRow, publishRow, noneRow, haveRow, otherRow, boxRow;
        readonly Collapsible detailsBox, noneBox, haveBox, boxBox;
        readonly FlatButton bKey, bCopy, bPublish;
        readonly Toggle tOther;
        readonly TextBox link;
        string exePath, publicKey, note;
        bool busy, touched, building, reading, getting;
        readonly ManualResetEvent cancel = new ManualResetEvent(false);
        // What happened when access was asked for, by place. It stays under the row.
        readonly Dictionary<string, string> says = new Dictionary<string, string>();

        // True when the person chose Set up Your storage: the caller closes this dialog and opens the guide.
        public bool WantsSetUp;

        // places: where the agents of the customers check for updates. open: reads and writes a link or folder.
        // reads: read what is published in each place now. saveLink keeps the link of Another link or folder for the customer in use.
        public PublishDialog(Theme t, IUpdateKeyStore keyStore, string exe, List<UpdateLocation> places, Func<string, ISessionStore> open, bool reads, Action<string> saveLink, Action<UpdateLocation, string> savePlaceLink) : base("Publish agent update", t)
        {
            keys = keyStore;
            exePath = exe;
            this.places = places ?? new List<UpdateLocation>();
            this.open = open;
            this.reads = reads;
            this.saveLink = saveLink;
            this.savePlaceLink = savePlaceLink;
            Size = new Size(Dpi.S(860), Dpi.S(840));
            FormClosed += delegate { cancel.Set(); };

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

            tOther = new Toggle();
            tOther.Changed += delegate { if (!building) { note = null; BuildAll(); } };
            otherRow = new SettingRow(Glyphs2.Folder, "Another link or folder", "A place that is not listed above: the link of the container the agents read updates from, with Create and Write, or a folder.", tOther);
            FlatButton bSetUp = SmallButton("Set up Your storage", delegate { WantsSetUp = true; Close(); });
            bSetUp.Primary = true;
            noneRow = new SettingRow(Glyphs2.Folder, "No update location yet", "Your storage is a storage account in your own Azure. Agents download TrackVice updates from it. Set it up once and Publish needs nothing more. A customer made with its own storage or a share is listed here too.", bSetUp);
            FlatButton bHave = SmallButton("I have a link or folder already", delegate { SetAnother(true); });
            haveRow = new SettingRow(Glyphs2.Folder, "Already have a place to publish to?", "", bHave);
            boxRow = new SettingRow(Glyphs2.Folder, "Link or folder", "The link of the container the agents read updates from, with Create and Write, or a folder. Use a container only you write to, such as trackvice-agent. It is saved encrypted for the customer in use.", link);
            noneBox = new Collapsible(noneRow);
            haveBox = new Collapsible(haveRow);
            boxBox = new Collapsible(boxRow);

            bPublish = SmallButton("Publish", delegate { Run(true); });
            bPublish.Primary = true;
            publishRow = new SettingRow(Glyphs2.Sync, "Publish", "", new ButtonStrip(PublishExplainDialog.Button(t), bPublish));

            ShowKey();
            ShowExe0();
            if (reads && open != null) ReadAll();
            else ApplyDefaults();
        }

        public static PublishDialog ForCustomer(Theme t)
        {
            string name = "AgentUpdateStore." + (CustomerUi.CurrentName ?? CustomerBook.DefaultName);
            return new PublishDialog(t, new DpapiKeyStore(), Program.Location, UpdatePlacesUi.Build(), UpdateLocations.OpenWith(YourStorageUi.Web), true,
                delegate (string v) { Secret.Save(name, v); },
                delegate (UpdateLocation p, string v) { UpdatePlacesUi.SaveLink(p, v); });
        }

        // Made-up key and places, for screenshots. page is publish (three places), publish-none or publish-link (no place, box shown).
        public static PublishDialog ForDemo(Theme t, string page)
        {
            MemoryKeyStore ks = new MemoryKeyStore();
            bool created;
            using (UpdateKeys.LoadOrCreate(ks, out created)) { }
            bool none = page == "publish-none" || page == "publish-link";
            Dictionary<string, Dictionary<string, byte[]>> files = new Dictionary<string, Dictionary<string, byte[]>>();
            Func<string, ISessionStore> demoOpen = delegate (string where)
            {
                Dictionary<string, byte[]> f;
                if (!files.TryGetValue(where, out f)) { f = new Dictionary<string, byte[]>(); files[where] = f; }
                return new UpdatePlacesUi.DemoStore(where, f);
            };
            List<UpdateLocation> places = none ? new List<UpdateLocation>() : UpdatePlacesUi.DemoPlaces(YourStorageUi.RunningVersion);
            PublishDialog d = new PublishDialog(t, ks, Program.Location, places, demoOpen, false, null, delegate (UpdateLocation p, string v) { });
            if (page == "publish-link") d.SetAnother(true);
            if (page == "publish-vault") { VaultSetupUi.DemoAddress = true; d.Say("Not published: latest.json in contosoagents " + OtherKeyMarker + ", so devices would refuse what this PC makes."); }
            return d;
        }

        string ExeVersion()
        {
            return File.Exists(exePath) ? SemVer.OfFile(exePath) : null;
        }

        // The places that are behind this TrackVice.exe and that this PC can write to, until the person turns something on or off.
        void ApplyDefaults()
        {
            if (!touched)
            {
                string v = ExeVersion();
                ticks.Clear();
                foreach (UpdateLocation p in places) ticks[p.Key] = v != null && p.Wanted(v);
            }
            BuildAll();
        }

        // Reads agent/latest.json of every place, away from the window.
        void ReadAll()
        {
            reading = true;
            string pub = publicKey;
            List<UpdateLocation> list = new List<UpdateLocation>(places);
            Func<string, ISessionStore> o = open;
            Thread th = new Thread(delegate ()
            {
                try
                {
                    foreach (UpdateLocation p in list) UpdateLocations.Refresh(p, o, pub);
                    UpdateLocations.Remember(list, DateTime.UtcNow);
                }
                catch (Exception) { }
                try
                {
                    BeginInvoke((MethodInvoker)delegate { reading = false; ApplyDefaults(); });
                }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
            BuildAll();
        }

        bool AnotherOn
        {
            get { return tOther != null && tOther.On; }
        }

        void SetAnother(bool on)
        {
            building = true;
            tOther.On = on;
            building = false;
            note = null;
            BuildAll();
        }

        string DescOf(UpdateLocation p)
        {
            List<string> s = new List<string>();
            if (p.Customers.Count > 0) s.Add("Used by " + string.Join(", ", p.Customers.ToArray()) + ".");
            else if (p.IsYourStorage) s.Add("Customers made with Your storage use it.");
            s.Add("Published there: " + (reading && p.State == UpdateState.NotRead ? "reading..." : p.VersionText) + ".");
            if (p.CanWrite) s.Add(p.IsFolder ? "This PC writes to the folder." : "This PC can publish here.");
            else s.Add("Cannot publish from this PC yet. " + p.Missing);
            if (!p.Confirmed) s.Add("Taken from the viewer link of the customer: its agents are expected to check here.");
            string said;
            if (says.TryGetValue(p.Key, out said) && said.Length > 0) return string.Join(" ", s.ToArray()) + "\n" + said;
            return string.Join(" ", s.ToArray());
        }

        // Puts every row of the page in again, from the places and what was turned on.
        void BuildAll()
        {
            building = true;
            foreach (PlaceRow r in rows) if (r.Tick != null) ticks[r.Place.Key] = r.Tick.On;
            ClearItems();
            rows.Clear();
            Add(new SectionTitle("Signing key"));
            Add(keyRow);
            Add(detailsBox);
            Add(new SectionTitle("Version to publish"));
            Add(exeRow);
            Add(new SectionTitle("Publish to"));
            foreach (UpdateLocation p in places)
            {
                PlaceRow r = MakeRow(p);
                rows.Add(r);
                Add(r.Row);
                Add(r.LinkBox);
            }
            noneBox.Shown = haveBox.Shown = places.Count == 0 && !AnotherOn;
            Add(noneBox);
            Add(haveBox);
            if (places.Count > 0) Add(otherRow);
            boxBox.Shown = AnotherOn;
            Add(boxBox);
            Add(publishRow);
            AddVaultRow();
            building = false;
            Refresh2();
        }

        PlaceRow MakeRow(UpdateLocation p)
        {
            PlaceRow r = new PlaceRow();
            r.Place = p;
            Control accessory;
            if (p.CanWrite)
            {
                r.Tick = new Toggle();
                bool on;
                ticks.TryGetValue(p.Key, out on);
                r.Tick.On = on;
                r.Tick.Changed += delegate { if (!building) { touched = true; note = null; Refresh2(); } };
                accessory = r.Tick;
            }
            else
            {
                FlatButton get = SmallButton("Sign in to Azure and get access", delegate { GetAccess(p, false); });
                get.Primary = true;
                get.Enabled = !getting;
                FlatButton add = SmallButton("Add a link", delegate { r.LinkBox.Shown = !r.LinkBox.Shown; Relayout(); if (r.LinkBox.Shown) r.LinkText.Focus(); });
                accessory = new ButtonStrip(get, add);
            }
            r.Row = new SettingRow(Glyphs2.Folder, p.Long, DescOf(p), accessory);
            r.LinkText = new TextBox();
            r.LinkText.Width = Dpi.S(300);
            r.LinkText.Font = Fonts.Mono;
            r.LinkText.BorderStyle = BorderStyle.FixedSingle;
            r.LinkText.BackColor = theme.Field;
            r.LinkText.ForeColor = theme.Text;
            FlatButton save = SmallButton("Use this link", delegate { UseLink(r); });
            string who = p.Customers.Count > 0 ? string.Join(", ", p.Customers.ToArray()) : "this location";
            r.LinkRow = new SettingRow(Glyphs.Shield, "Link for " + p.Container, "Make a link for " + p.Container + " in " + p.Account + " and paste it here. It is saved encrypted for " + who + ".\n" + PublishAccess.ManualSteps(p), new ButtonStrip(r.LinkText, save));
            r.LinkBox = new Collapsible(r.LinkRow);
            r.LinkBox.Shown = false;
            return r;
        }

        void OnUi(Action a)
        {
            try { if (!IsDisposed) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) a(); }); }
            catch (Exception) { }
        }

        void SayAbout(UpdateLocation p, string text)
        {
            says[p.Key] = text;
            foreach (PlaceRow r in rows) if (r.Place == p) { r.Row.Desc = DescOf(p); r.Row.Invalidate(); }
            Relayout();
        }

        // Signs in to Azure (an earlier sign-in of this run is used again), finds the storage account, and makes and saves the link.
        void GetAccess(UpdateLocation p, bool createContainer)
        {
            if (getting) return;
            getting = true;
            SayAbout(p, createContainer ? "Creating the container..." : "Signing in to Azure...");
            IWeb web = YourStorageUi.Web;
            Action<UpdateLocation, string> save = savePlaceLink;
            Thread th = new Thread(delegate ()
            {
                AccessResult res = null;
                string err = null;
                try
                {
                    WebSignIn s = SignIns.Get(web, Arm.ClientId, "", Arm.Scope);
                    if (!s.SignedIn && !s.Quiet())
                    {
                        DeviceCode d = s.Start();
                        OnUi(delegate
                        {
                            Menus.Copy(d.UserCode);
                            try { System.Diagnostics.Process.Start(d.VerificationUri); }
                            catch (Exception) { }
                            SayAbout(p, "Open " + d.VerificationUri + " and enter the code " + d.UserCode + " (it is copied). TrackVice waits for the sign-in.");
                        });
                        s.Wait(d, cancel);
                    }
                    OnUi(delegate { SayAbout(p, "Signed in" + (string.IsNullOrEmpty(s.Account) ? "" : " as " + s.Account) + ". Looking for the storage account " + p.Account + "..."); });
                    res = PublishAccess.Get(new Arm(web, s.Token), s.Account, p, createContainer, DateTime.UtcNow, save);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { err = ex.Message; }
                OnUi(delegate { getting = false; GotAccess(p, res, err); });
            });
            th.IsBackground = true;
            th.Start();
        }

        void GotAccess(UpdateLocation p, AccessResult res, string err)
        {
            if (res == null)
            {
                says[p.Key] = err + "\nOr make the link by hand: use Add a link.";
                BuildAll();
                OpenLinkBox(p);
                return;
            }
            if (res.Outcome == AccessOutcome.Saved)
            {
                says.Remove(p.Key);
                touched = false;
                note = null;
                if (reads && open != null) ReadAll();
                else ApplyDefaults();
                Say(res.Message + " Reading what is published there...");
                return;
            }
            if (res.Outcome == AccessOutcome.AskContainer)
            {
                if (MessageBox.Show(this, res.Message, "Publish agent update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { GetAccess(p, true); return; }
                says[p.Key] = "The container " + p.Container + " was not created. Nothing was changed in Azure.";
                BuildAll();
                return;
            }
            says[p.Key] = res.Message;
            BuildAll();
            OpenLinkBox(p);
        }

        void OpenLinkBox(UpdateLocation p)
        {
            foreach (PlaceRow r in rows) if (r.Place == p && r.LinkBox != null) { r.LinkBox.Shown = true; r.LinkText.Focus(); }
            Relayout();
        }

        void UseLink(PlaceRow r)
        {
            string problem = UpdateLocations.WriteLinkProblem(r.Place, r.LinkText.Text, DateTime.UtcNow);
            if (problem != null) { r.LinkRow.Desc = problem; r.LinkRow.Invalidate(); Relayout(); return; }
            UpdateLocations.UseWriteLink(r.Place, r.LinkText.Text);
            says.Remove(r.Place.Key);
            if (savePlaceLink != null) savePlaceLink(r.Place, r.LinkText.Text.Trim());
            touched = false;
            note = null;
            if (reads && open != null) ReadAll();
            else ApplyDefaults();
        }

        // The places that are turned on, and the other link or folder when it is on. problem says what is wrong with the latter.
        List<UpdateLocation> Chosen(out string problem)
        {
            problem = null;
            List<UpdateLocation> l = new List<UpdateLocation>();
            foreach (PlaceRow r in rows) if (r.Tick != null && r.Tick.On) l.Add(r.Place);
            if (AnotherOn)
            {
                UpdateLocation o = UpdateLocations.FromLink(link.Text);
                if (link.Text.Trim().Length == 0) problem = "Enter a link or a folder to publish to, or turn Another link or folder off.";
                else if (o == null) problem = "Enter a link that starts with https://, or a folder such as \\\\server\\share\\agent.";
                else if (!o.CanWrite) problem = o.Missing;
                else l.Add(o);
            }
            return l;
        }

        // Shows the Publish button and its reason.
        void Refresh2()
        {
            string problem;
            List<UpdateLocation> chosen = Chosen(out problem);
            bool ok = File.Exists(exePath) && SemVer.OfFile(exePath) != null;
            bPublish.Enabled = ok && chosen.Count > 0 && !busy && problem == null;
            string idle = places.Count == 0 && !AnotherOn ? "Set up Your storage, or use a link or folder you have, to publish." : "Turn on at least one place.";
            publishRow.Desc = note ?? (problem != null ? problem : !ok ? "The version of TrackVice.exe could not be read. Choose another file." : chosen.Count == 0 ? idle
                : "Uploads TrackVice-<version>.exe and agent/latest.json, signed with the update key, to " + chosen.Count + (chosen.Count == 1 ? " place" : " places") + ". Agents check every 6 hours and never install an older version.");
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

        void ShowExe0()
        {
            string v = ExeVersion();
            exeRow.Desc = exePath + (v != null ? " · version " + v : " · the version could not be read");
            exeRow.Invalidate();
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
            touched = false;
            ShowExe0();
            ApplyDefaults();
        }

        void Say(string text)
        {
            note = text;
            publishRow.Desc = text;
            publishRow.Invalidate();
            ShowVaultRow(text);
            Relayout();
        }

        // Creating a 3072-bit key and uploading take a few seconds, so both run away from the window.
        void Run(bool publish)
        {
            if (busy) return;
            string problem = null;
            List<UpdateLocation> chosen = new List<UpdateLocation>();
            if (publish)
            {
                chosen = Chosen(out problem);
                if (problem == null && chosen.Count == 0) problem = "Turn on at least one place to publish to.";
                if (problem != null) { Say(problem); return; }
            }
            bool other = AnotherOn;
            string exe = exePath;
            busy = true;
            bPublish.Enabled = bKey.Enabled = false;
            Say(publish ? "Publishing..." : "Creating the key...");
            Thread th = new Thread(delegate ()
            {
                string result;
                List<PublishLine> lines = null;
                string version = null;
                try
                {
                    bool created;
                    using (RSACryptoServiceProvider key = UpdateKeys.LoadOrCreate(keys, out created))
                    {
                        if (!publish) result = "The key is created. Copy it into $UpdateKey before you publish.";
                        else
                        {
                            version = SemVer.OfFile(exe);
                            if (version == null) throw new ArgumentException("The version of " + exe + " could not be read.");
                            lines = UpdateLocations.PublishTo(chosen, File.ReadAllBytes(exe), version, key, DateTime.UtcNow, open ?? UpdateLocations.OpenWith(YourStorageUi.Web));
                            result = UpdateLocations.Summary(lines, version);
                            if (created) result += " A new key was made: copy it into $UpdateKey, or the agents ignore this version.";
                            foreach (PublishLine l in lines) result += "\n" + l.Full;
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
                        if (lines != null) Published(lines, version, other);
                        ShowKey();
                        ShowExe0();
                        BuildAll();
                        Say(result);
                    });
                }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        // Keeps what changed: the version in Your storage, the link of Another link or folder, and what Overview shows.
        void Published(List<PublishLine> lines, string version, bool other)
        {
            foreach (PublishLine l in lines)
            {
                if (l.Outcome != PublishOutcome.Published) continue;
                if (l.Place.IsYourStorage && !YourStorageUi.Demo)
                {
                    try
                    {
                        OwnStorage own = YourStorageUi.Load();
                        if (own != null) { own.PublishedVersion = version; own.Save(YourStorageUi.Secrets); }
                    }
                    catch (Exception) { }
                }
                if (other && saveLink != null && l.Place.Customers.Count == 0 && !l.Place.IsYourStorage && !places.Contains(l.Place) && l.Place.WriteLink.Length > 0)
                    saveLink(l.Place.WriteLink);
            }
            UpdateLocations.Remember(places, DateTime.UtcNow);
        }
    }
}
