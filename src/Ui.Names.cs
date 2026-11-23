namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Windows.Forms;

    // Picks what to read from Intune. Each lookup needs a permission of its own, and the sign-in asks only for
    // the ones ticked here.
    public sealed class NamesDialog : PageForm
    {
        public bool Accepted;
        readonly FlatButton bRead = new FlatButton("Read from Intune", Glyphs.Refresh);
        readonly LookupChoice choice = Saved();

        public NamesDialog(Theme t, string deviceName) : base("Names from Intune", t)
        {
            Size = new System.Drawing.Size(Dpi.S(720), Dpi.S(640));
            string device = string.IsNullOrEmpty(deviceName) ? "the device you watch" : deviceName;
            Add(new SectionTitle("What to read"));
            Add(Row(Glyphs2.Code, "Script names", "Platform scripts show their name instead of the start of their ID. Needs DeviceManagementScripts.Read.All.", choice.Scripts, delegate (bool on) { choice.Scripts = on; }));
            Add(Row(Glyphs2.Apps, "App names", "Intune apps that the device only knows by ID show their name. Needs DeviceManagementApps.Read.All.", choice.Apps, delegate (bool on) { choice.Apps = on; }));
            Add(Row(Glyphs.Shield, "Remediation names", "Remediations show their name instead of the start of their ID. Needs DeviceManagementScripts.Read.All.", choice.Remediations, delegate (bool on) { choice.Remediations = on; }));
            Add(Row(Glyphs2.Device, "Configuration profiles of this device", "Every configuration profile Intune has for " + device + ", with its state: succeeded, error, conflict or not applicable. This is what the device last reported to Intune. Needs DeviceManagementConfiguration.Read.All.", choice.Profiles, delegate (bool on) { choice.Profiles = on; }));
            Add(new SectionTitle("Sign-in"));
            Add(new SettingRow(Glyphs.Info, "Read-only, one permission per lookup", "TrackVice signs in to Microsoft Graph with a device code and asks only for the permissions ticked here. Each one needs consent in your tenant. A lookup without its permission fails on its own and the others still apply. Names are kept and used for devices you open later.", null));

            bClose.Primary = false;
            bClose.ApplyTheme(t);
            bRead.Primary = true;
            bRead.Width = Math.Max(bRead.Width, Dpi.S(150));
            bRead.Dock = DockStyle.Right;
            bRead.ApplyTheme(t);
            bRead.Enabled = choice.Any;
            bRead.Click += Safe.Wrap(delegate
            {
                Save(choice);
                Accepted = true;
                Close();
            });
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            footer.Controls.Add(gap);
            footer.Controls.Add(bRead);
        }

        delegate void Set(bool on);

        SettingRow Row(string glyph, string title, string desc, bool on, Set set)
        {
            Toggle tg = new Toggle();
            tg.On = on;
            tg.Changed += Safe.Wrap(delegate
            {
                set(tg.On);
                bRead.Enabled = choice.Any;
            });
            return new SettingRow(glyph, title, desc, tg);
        }

        // The choice last made. Before this dialog existed, "IntuneNames" meant script names only.
        public static LookupChoice Saved()
        {
            LookupChoice c = new LookupChoice();
            c.Scripts = Prefs.GetBool("IntuneNames.Scripts", true);
            c.Apps = Prefs.GetBool("IntuneNames.Apps", false);
            c.Remediations = Prefs.GetBool("IntuneNames.Remediations", false);
            c.Profiles = Prefs.GetBool("IntuneNames.Profiles", false);
            return c;
        }

        public static void Save(LookupChoice c)
        {
            Prefs.SetBool("IntuneNames.Scripts", c.Scripts);
            Prefs.SetBool("IntuneNames.Apps", c.Apps);
            Prefs.SetBool("IntuneNames.Remediations", c.Remediations);
            Prefs.SetBool("IntuneNames.Profiles", c.Profiles);
            Prefs.Set("IntuneNames", c.Any ? "1" : "0");
        }

        // The permissions an Intune sign-in asks for besides the one it always needs, with a leading space.
        public static string ExtraScopes()
        {
            if (Prefs.Get("IntuneNames", "0") != "1") return "";
            string s = "";
            foreach (string scope in Saved().Scopes()) s += " " + scope;
            return s;
        }

        public static bool Has(GraphClient g, string scope)
        {
            return g != null && g.Scopes.IndexOf(scope, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The ticked lookups this sign-in has the permission for.
        public static LookupChoice Allowed(GraphClient g)
        {
            LookupChoice c = Saved();
            bool scripts = Has(g, GraphClient.ScriptScope);
            c.Scripts &= scripts;
            c.Remediations &= scripts;
            c.Apps &= Has(g, LookupChoice.AppScope);
            c.Profiles &= Has(g, LookupChoice.ConfigScope);
            return c;
        }
    }

    public sealed partial class MainForm
    {
        Dictionary<string, string> intuneNames;
        Engine profilesFor, diffFor;
        SnapshotDiff profileDiff;

        // A device knows scripts, remediations and some apps by ID only. Their names, and the configuration
        // profiles of the watched device, are read from Intune with the permissions picked in the dialog.
        void NamesFromIntune()
        {
            using (NamesDialog d = new NamesDialog(theme, engine != null ? engine.S.Computer : null))
            {
                d.ShowDialog(this);
                if (!d.Accepted) return;
            }
            LookupChoice want = NamesDialog.Saved();
            bool missing = false;
            foreach (string s in want.Scopes()) if (!NamesDialog.Has(graph, s)) missing = true;
            if (graph == null || !graph.SignedIn || missing)
            {
                intuneCatalog = null;
                graph = null;
                using (RemoteDialog d = new RemoteDialog(RemoteDialog.Intune, theme, null, null, false))
                {
                    d.Text = "Sign in to read from Intune";
                    d.NamesOnly = true;
                    d.ShowDialog(this);
                    intuneCatalog = d.Catalog;
                    graph = d.Graph;
                }
                KeepSignIn(graph);
                if (graph == null || !graph.SignedIn)
                {
                    Status("Not signed in, so nothing was read from Intune.");
                    return;
                }
            }
            profilesFor = null;
            LoadIntuneNames(true);
        }

        // ---- kept between starts ----

        readonly NameCache nameCache = new NameCache(NameCache.DefaultFolder);
        DateTime namesReadUtc, unknownAskedUtc;
        readonly HashSet<string> unknownAsked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string IntuneKey
        {
            get { return CustomerUi.CurrentName ?? "Default"; }
        }

        // The names and the sign-in kept for the customer in use. Names older than a day are read again in the background.
        void RestoreIntune()
        {
            string key = IntuneKey;
            DateTime read;
            Dictionary<string, string> kept = nameCache.Load(key, out read);
            namesReadUtc = read;
            if (kept.Count > 0)
            {
                intuneNames = kept;
                AgentCatalog.KnownNames = new Dictionary<string, string>(kept, StringComparer.OrdinalIgnoreCase);
                if (engine != null && engine.AddNames(kept) > 0) Bind();
            }
            if (graph == null)
            {
                string rt = Secret.Load("GraphToken|" + key);
                if (rt.Length > 0)
                    graph = GraphClient.Resume(Prefs.Get("GraphClient|" + key, null), Prefs.Get("GraphTenantOf|" + key, null), Prefs.Get("GraphScopes|" + key, null), rt, Prefs.Get("GraphAccount|" + key, null));
            }
            KeepSignIn(graph);
            if (graph != null && DateTime.UtcNow - namesReadUtc > TimeSpan.FromDays(1)) LoadIntuneNames(false);
        }

        // Keeps a sign-in for the customer in use, encrypted for this Windows user, and follows each new token.
        void KeepSignIn(GraphClient g)
        {
            if (g == null || !g.SignedIn) return;
            string key = IntuneKey;
            g.TokenChanged = delegate (string rt) { SaveSignIn(key, g, rt); };
            string now = g.RefreshToken;
            if (!string.IsNullOrEmpty(now)) SaveSignIn(key, g, now);
        }

        static void SaveSignIn(string key, GraphClient g, string rt)
        {
            Secret.Save("GraphToken|" + key, rt);
            Prefs.Set("GraphScopes|" + key, g.Scopes);
            Prefs.Set("GraphAccount|" + key, g.Account);
            Prefs.Set("GraphClient|" + key, g.ClientId);
            Prefs.Set("GraphTenantOf|" + key, g.Tenant);
        }

        // Settings > Sign out: the kept sign-in and names of the customer in use are deleted.
        public static void SignOutIntune()
        {
            string key = IntuneKey;
            Prefs.Set("GraphToken|" + key, null);
            Prefs.Set("GraphScopes|" + key, null);
            Prefs.Set("GraphAccount|" + key, null);
            new NameCache(NameCache.DefaultFolder).Delete(key);
            SignIns.Clear();
            SignedOut = true;
        }

        public static bool SignedOut;

        public static string KeptAccount
        {
            get { return Secret.Load("GraphToken|" + IntuneKey).Length > 0 ? (Prefs.Get("GraphAccount|" + IntuneKey, null) ?? "an account") : null; }
        }

        // A script, app or remediation that shows as an ID asks Intune for names, at most every few minutes and once per ID.
        void FetchUnknownNames()
        {
            if (engine == null || engine.Mode == Modes.Demo || graph == null || !graph.SignedIn || Prefs.Get("IntuneNames", "0") != "1") return;
            if (DateTime.UtcNow - unknownAskedUtc < TimeSpan.FromMinutes(3)) return;
            List<string> ids = NameCache.Unknown(engine.S, intuneNames);
            bool fresh = false;
            foreach (string id in ids) if (unknownAsked.Add(id)) fresh = true;
            if (!fresh) return;
            unknownAskedUtc = DateTime.UtcNow;
            LoadIntuneNames(false);
        }

        // Names and profiles read from one customer's tenant are not used for another.
        void ForgetIntuneNames()
        {
            intuneNames = null;
            AgentCatalog.KnownNames = null;
            unknownAsked.Clear();
            profilesFor = null;
            profileDiff = null;
            diffFor = null;
        }

        // Reads what was picked, for the device that is open now. Runs again at every Intune sign-in.
        void LoadIntuneNames(bool say)
        {
            GraphClient g = graph;
            if (g == null || !g.SignedIn || Prefs.Get("IntuneNames", "0") != "1") return;
            LookupChoice c = NamesDialog.Allowed(g);
            Engine e = engine;
            if (e == null || e.Mode == Modes.Demo) c.Profiles = false;
            else profilesFor = e;
            if (!c.Any) return;
            Run(g, c, e, say);
        }

        // A device opened later gets the names already read; its profiles are read for it.
        void IntuneOnLoad(Engine e)
        {
            FetchUnknownNames();
            GraphClient g = graph;
            if (e == null || e == profilesFor || e.Mode == Modes.Demo || g == null || !g.SignedIn || Prefs.Get("IntuneNames", "0") != "1") return;
            LookupChoice c = new LookupChoice();
            c.Profiles = NamesDialog.Allowed(g).Profiles;
            if (!c.Profiles) return;
            profilesFor = e;
            Run(g, c, e, false);
        }

        void Run(GraphClient g, LookupChoice c, Engine e, bool say)
        {
            string device = e != null ? e.S.Computer : null;
            if (say) Status("Reading from Intune…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                LookupResult r = null;
                try { r = IntuneLookup.Run(g, c, device, DateTime.UtcNow); }
                catch (Exception ex) { r = new LookupResult(); r.Problems.Add(ex.Message); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate { Safe.Run(delegate { Found(r, e, say); }); });
                }
                catch (Exception) { }
            });
        }

        void Found(LookupResult r, Engine e, bool say)
        {
            if (r.Names.Count > 0)
            {
                if (intuneNames == null) intuneNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> kv in r.Names) intuneNames[kv.Key] = kv.Value;
                AgentCatalog.KnownNames = new Dictionary<string, string>(intuneNames, StringComparer.OrdinalIgnoreCase);
                namesReadUtc = DateTime.UtcNow;
                nameCache.Save(IntuneKey, r.Names, namesReadUtc);
            }
            int n = engine != null && r.Names.Count > 0 ? engine.AddNames(r.Names) : 0;
            if (r.Profiles.Count > 0 && e != null && e == engine)
            {
                if (profileDiff == null || diffFor != e) { profileDiff = new SnapshotDiff(); diffFor = e; }
                List<Obs> changed = new List<Obs>();
                foreach (Obs o in r.Profiles) if (profileDiff.Changed(o.ItemKey, o.State + "|" + o.ItemName)) changed.Add(o);
                IntuneLookup.ApplyProfiles(e, changed);
                if (changed.Count > 0) Bind();
            }
            if (n > 0) Bind();
            string text = r.Summary();
            if ((say || n > 0 || r.Profiles.Count > 0 || r.Problems.Count > 0) && text.Length > 0) Status(text + (r.Names.Count > 0 ? " " + n + " named here." : ""));
        }
    }
}
