namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Threading;
    using System.Windows.Forms;

    // The mouse back and forward buttons go Back and Forward in the main window, wherever the pointer is in it.
    sealed class XButtonFilter : IMessageFilter
    {
        const int WmXButtonUp = 0x020C;
        readonly Form form;
        readonly Action<bool> go;

        public XButtonFilter(Form form, Action<bool> go)
        {
            this.form = form;
            this.go = go;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmXButtonUp) return false;
            Control c = Control.FromHandle(m.HWnd);
            Form owner = c == null ? null : c as Form ?? c.FindForm();
            if (owner != form) return false;
            int button = (int)(((long)m.WParam >> 16) & 0xFFFF);
            if (button != 1 && button != 2) return false;
            go(button == 2);
            return true;
        }
    }

    public sealed partial class MainForm
    {
        OverviewPage overviewPage;
        DeviceListPage listPage;
        readonly NavHistory nav = new NavHistory();
        System.Windows.Forms.Timer overviewTimer;
        FlatButton bBack, bForward, bHome;
        XButtonFilter xbuttons;
        // navigating: a place is shown again from the history, so it is not added to it.
        bool navigating, demoOpen, startDone, overviewAtStart, pausedForPage, overviewLoading, overviewDemo;
        OverviewLoader overviewLoader;
        string overviewSig;
        OverviewSnapshot overviewSnap;
        List<AttentionTile> extraTiles = new List<AttentionTile>();
        DateTime overviewReadUtc = DateTime.MinValue;
        RecentStore recentStore;
        RecentDevice currentTarget;
        string savedState;
        DateTime savedAt;

        const string OpenOnPref = "OpenOn";

        RecentStore Recent
        {
            get { return recentStore ?? (recentStore = new RecentStore(RecentStore.DefaultPath)); }
        }

        // Back, Forward and Overview in the header, and the timer that refreshes Overview.
        void BuildNav()
        {
            bBack = Button("", Glyphs2.Back, delegate { NavGo(false); });
            bForward = Button("", "", delegate { NavGo(true); });
            bHome = Button("Overview", "", delegate { GoHome(true); });
            if (Fonts.Icons == null)
            {
                bBack.Text = "‹";
                bForward.Text = "›";
            }
            Tip(bBack, "Back (Alt+Left, or the mouse back button)");
            Tip(bForward, "Forward (Alt+Right)");
            Tip(bHome, "Overview: devices deploying now, the last 7 days, what needs attention and recent devices (Alt+Home)");
            FlatButton[] nb = { bBack, bForward, bHome };
            for (int i = 0; i < nb.Length; i++)
            {
                nb[i].Margin = new Padding(Dpi.S(2), 0, 0, 0);
                toolbar.Controls.Add(nb[i]);
                toolbar.Controls.SetChildIndex(nb[i], i);
            }
            bHome.Margin = new Padding(Dpi.S(2), 0, Dpi.S(10), 0);
            overviewTimer = new System.Windows.Forms.Timer();
            overviewTimer.Interval = 60000;
            overviewTimer.Tick += Safe.Wrap(delegate { if (OverviewVisible) RefreshOverview(false); });
            overviewTimer.Start();
            xbuttons = new XButtonFilter(this, delegate (bool forward) { Safe.Run(delegate { NavGo(forward); }); });
            Application.AddMessageFilter(xbuttons);
            FormClosed += delegate { overviewTimer.Stop(); Application.RemoveMessageFilter(xbuttons); };
            UpdateNav();
        }

        bool OverviewVisible
        {
            get { return overviewPage != null && overviewPage.Visible; }
        }

        bool ListVisible
        {
            get { return listPage != null && listPage.Visible; }
        }

        bool PageVisible
        {
            get { return OverviewVisible || ListVisible; }
        }

        void UpdateNav()
        {
            if (bBack == null) return;
            bBack.Enabled = nav.CanBack;
            bForward.Enabled = nav.CanForward;
            bHome.Checked = OverviewVisible;
            foreach (FlatButton b in new FlatButton[] { bBack, bForward, bHome }) b.Invalidate();
        }

        void NavGo(bool forward)
        {
            NavEntry e = forward ? nav.Forward() : nav.Back();
            if (e == null) { UpdateNav(); return; }
            navigating = true;
            try { Show(e); }
            finally { navigating = false; }
            UpdateNav();
        }

        void Show(NavEntry e)
        {
            if (e.Kind == NavEntry.Overview) GoHome(false);
            else if (e.Kind == NavEntry.List)
            {
                object[] t = e.Tag as object[];
                if (t != null) ShowList(e.ListId, e.Title, (string)t[0], (Func<string, List<FoundDevice>>)t[1], t[2] as RecentStore, (string)t[3]);
            }
            else if (engine != null && e.Tag == engine) ShowDeviceView();
            else if (e.Target != null) OpenTarget(e.Target, true);
            else
            {
                ShowDeviceView();
                Status("A demo is not kept in the history. The device shown is the one opened last.");
            }
        }

        // ---------- pages in the window ----------

        T Page<T>(ref T page) where T : Control, IThemed, new()
        {
            if (page != null) return page;
            page = new T();
            page.Visible = false;
            themed.Add(page);
            Controls.Add(page);
            Controls.SetChildIndex(page, 0);
            page.ApplyTheme(theme);
            return page;
        }

        void ShowPage(Control p, string title)
        {
            if (welcome != null) welcome.Visible = false;
            foreach (Control c in new Control[] { chips, rail, tabs, content, banner }) c.Visible = false;
            if (setupBar != null) setupBar.Visible = false;
            if (overviewPage != null && overviewPage != p) overviewPage.Visible = false;
            if (listPage != null && listPage != p) listPage.Visible = false;
            p.Visible = true;
            p.BringToFront();
            if (watching && engine != null) { pausedForPage = true; SetWatching(false); }
            PageTitle(title);
            UpdateNav();
            Invalidate(true);
        }

        void PageTitle(string title)
        {
            subtitle.Text = title;
            Text = title + " - TrackVice";
        }

        // Called at the end of each Bind, so a page keeps its own title while the device behind it is read.
        void KeepPageTitle()
        {
            if (OverviewVisible) PageTitle("Overview");
            else if (ListVisible && nav.Current != null) PageTitle(nav.Current.Title);
        }

        void HidePages()
        {
            if (overviewPage != null) overviewPage.Visible = false;
            if (listPage != null) listPage.Visible = false;
        }

        // Back to the device: the window as it was before a page was shown, and watching again if it was.
        void ShowDeviceView()
        {
            bool was = PageVisible;
            HidePages();
            if (!was) return;
            foreach (Control c in new Control[] { chips, rail, tabs, content }) c.Visible = true;
            banner.Visible = engine != null && engine.Mode == Modes.Live && !Program.IsAdmin;
            UpdateSetupBar();
            if (pausedForPage && engine != null) SetWatching(true);
            pausedForPage = false;
            if (engine != null) Bind();
            UpdateNav();
            Invalidate(true);
        }

        void GoHome(bool record)
        {
            Page(ref overviewPage);
            if (!hooked) HookOverview();
            ShowPage(overviewPage, "Overview");
            if (record && !navigating) nav.Visit(NavEntry.ForOverview());
            UpdateNav();
            if (!overviewDemo && DateTime.UtcNow - overviewReadUtc > TimeSpan.FromSeconds(50)) RefreshOverview(false);
            RenderOverview();
        }

        bool hooked;

        void HookOverview()
        {
            hooked = true;
            OverviewPage p = overviewPage;
            p.OpenDevice += delegate (OverviewDevice o) { Safe.Run(delegate { OpenOverviewDevice(o); }); };
            p.OpenFound += delegate (FoundDevice f) { Safe.Run(delegate { OpenFound(f); }); };
            p.OpenList += delegate (string id, string title, List<FoundDevice> rows) { Safe.Run(delegate { ShowFixedList(id, title, null, rows); }); };
            p.AttentionClicked += delegate (AttentionTile t) { Safe.Run(delegate { OpenAttention(t); }); };
            p.SearchFor += delegate (string q) { Safe.Run(delegate { OpenRecentList(q); }); };
            p.SetupClicked += delegate (string id) { Safe.Run(delegate { OverviewSetup(id); }); };
            p.RefreshClicked += Safe.Wrap(delegate { RefreshOverview(true); });
            p.FilterChanged += Safe.Wrap(delegate { RenderOverview(); });
            p.ThisPc += Safe.Wrap(delegate { Begin(Settings.NewLive(), true); });
            p.AllRecent += Safe.Wrap(delegate { OpenRecentList(""); });
        }

        void OpenAttention(AttentionTile t)
        {
            if (t.Open != null) { t.Open(); return; }
            ShowFixedList("tile:" + t.Key, t.Title, t.Text, OverviewPage.Devices(t.Devices));
        }

        void OverviewSetup(string id)
        {
            if (id == OverviewModel.Storage) OpenStep(GettingStarted.Storage);
            else if (id == OverviewModel.Customer) OpenAddCustomer(false);
            else if (id == OverviewModel.Sccm) OpenSccmSetup(false);
            overviewSig = null;
            if (OverviewVisible) RefreshOverview(true);
        }

        void ShowFixedList(string id, string title, string desc, List<FoundDevice> rows)
        {
            List<FoundDevice> all = rows ?? new List<FoundDevice>();
            Func<string, List<FoundDevice>> f = delegate (string q)
            {
                List<FoundDevice> l = new List<FoundDevice>();
                foreach (FoundDevice x in all) if (DeviceSearch.Matches(x, q)) l.Add(x);
                return l;
            };
            ShowList(id, title, desc ?? "The devices behind this part of Overview. Double-click one to follow it.", f, null, "");
        }

        // The recent devices, and a search over them and every device Overview has loaded.
        void OpenRecentList(string query)
        {
            RecentStore store = Recent;
            Func<string, List<FoundDevice>> f = delegate (string q) { return DeviceSearch.Search(q, store.All, LoadedDevices, 500); };
            ShowList("recent", "Recent devices",
                "Every device opened or followed on this PC, newest first, with pinned devices at the top. Type to search them and the devices Overview has loaded, across customers, Intune and SCCM: device name, serial number, user, profile or task sequence.",
                f, store, query);
        }

        List<OverviewDevice> LoadedDevices
        {
            get { return overviewSnap != null ? overviewSnap.Devices : new List<OverviewDevice>(); }
        }

        void ShowList(string id, string title, string desc, Func<string, List<FoundDevice>> rows, RecentStore store, string query)
        {
            bool fresh = listPage == null;
            Page(ref listPage);
            if (fresh)
            {
                listPage.Open += delegate (FoundDevice f) { Safe.Run(delegate { OpenFound(f); }); };
                listPage.Home += Safe.Wrap(delegate { GoHome(true); });
                listPage.Changed += Safe.Wrap(delegate { if (overviewPage != null && overviewSnap != null) RenderOverview(); });
            }
            listPage.Show(id, title, desc, rows, store, query);
            ShowPage(listPage, title);
            if (!navigating) nav.Visit(NavEntry.ForList(id, title, new object[] { desc, rows, store, query }));
            else if (nav.Current != null) nav.Current.Title = title;
            UpdateNav();
            listPage.Search.Focus();
            listPage.Search.SelectionStart = listPage.Search.Text.Length;
        }

        // ---------- reading for Overview ----------

        // One source per customer and place: the agent uploads, and the SCCM site when an SMS Provider is set.
        List<IOverviewSource> OverviewSources(out string sig)
        {
            List<IOverviewSource> l = new List<IOverviewSource>();
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            List<Customer> all = new List<Customer>();
            try { all = CustomerUi.Book.All; }
            catch (Exception) { }
            if (all.Count == 0)
            {
                Customer legacy = new Customer();
                legacy.Name = "";
                legacy.AgentLocation = Secret.Load("AgentStore");
                legacy.SccmServer = Prefs.Get("SccmServer", "");
                legacy.SccmSite = Prefs.Get("SccmSite", "");
                all.Add(legacy);
            }
            foreach (Customer c in all)
            {
                string name = c.Name, loc = c.AgentLocation, srv = c.SccmServer, site = c.SccmSite;
                if (!string.IsNullOrWhiteSpace(loc))
                {
                    l.Add(new CatalogSource(name, OverviewKinds.Agents, delegate { return new AgentCatalog(Stores.Open(loc)); }));
                    b.Append(name).Append("|a|").Append(loc.GetHashCode()).Append(';');
                }
                if (!string.IsNullOrWhiteSpace(srv))
                {
                    l.Add(new CatalogSource(name, OverviewKinds.Sccm, delegate { return ConnectSccm(srv, site); }));
                    b.Append(name).Append("|s|").Append(srv).Append('|').Append(site).Append(';');
                }
            }
            sig = b.ToString();
            return l;
        }

        static IRemoteCatalog ConnectSccm(string server, string site)
        {
            SccmWmi w = new SccmWmi(server, site);
            Exception err;
            if (!Guard.Run(delegate { w.Connect(); }, TimeSpan.FromSeconds(45), out err)) throw new TimeoutException(server + " did not answer within 45 seconds.");
            if (err != null) throw err;
            return new SccmCatalog(w, server);
        }

        void RefreshOverview(bool force)
        {
            if (overviewPage == null) return;
            if (overviewDemo) { RenderOverview(); return; }
            if (overviewLoading) return;
            string sig;
            List<IOverviewSource> sources = OverviewSources(out sig);
            if (overviewLoader == null || sig != overviewSig)
            {
                overviewLoader = new OverviewLoader(sources);
                overviewSig = sig;
            }
            OverviewLoader l = overviewLoader;
            if (sources.Count > 0) overviewPage.StatusText = "Reading…";
            overviewLoading = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                OverviewSnapshot snap = null;
                List<AttentionTile> tiles = null;
                try { snap = l.Read(DateTime.UtcNow, force); }
                catch (Exception ex) { snap = new OverviewSnapshot(); snap.Problems.Add(ex.Message); }
                tiles = OverviewAttention.Collect();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Safe.Run(delegate
                        {
                            overviewLoading = false;
                            if (l != overviewLoader) return;
                            overviewSnap = snap;
                            extraTiles = tiles;
                            overviewReadUtc = DateTime.UtcNow;
                            RenderOverview();
                        });
                    });
                }
                catch (Exception) { }
            });
        }

        void RenderOverview()
        {
            if (overviewPage == null) return;
            DateTime now = DateTime.UtcNow;
            OverviewSnapshot snap = overviewSnap;
            if (snap == null)
            {
                // Before the first read: the sources that are set up, with no devices yet.
                snap = new OverviewSnapshot();
                snap.Agents = (overviewSig ?? "").Contains("|a|");
                snap.Sccm = (overviewSig ?? "").Contains("|s|");
            }
            OverviewData d = OverviewModel.Build(snap, overviewPage.Filter, now, DateTime.Today.ToUniversalTime(), extraTiles);
            SideFacts f = Facts();
            List<SetupNeed> needs = OverviewModel.SetupNeeds(f.Storage, f.Customers, f.SccmServer, overviewPage.Filter.Side);
            overviewPage.SetCustomers(overviewSnap != null ? overviewSnap.Customers : null);
            overviewPage.SetData(d, Recent.All, needs, now);
            string st = overviewSnap == null ? (overviewLoading ? "Reading\u2026" : "") : overviewDemo ? (overviewSnap.Devices.Count > 0 ? "Demo data, made-up devices." : "") : "Updated " + Txt.LocalTime(overviewSnap.ReadUtc) + ".";
            if (overviewSnap != null && overviewSnap.Problems.Count > 0)
            {
                st += " " + overviewSnap.Problems.Count + " could not be read.";
                if (OverviewVisible) Status("Overview could not read " + string.Join("; ", overviewSnap.Problems.ToArray()));
            }
            overviewPage.StatusText = st;
        }

        // ---------- opening devices again ----------

        void OpenOverviewDevice(OverviewDevice o)
        {
            IRemoteCatalog c = null;
            try { c = o.Source != null ? o.Source.Catalog : null; }
            catch (Exception ex) { Status("Could not open " + o.D.Device + ": " + ex.Message); return; }
            if (c == null) { Status(o.D.Device + " is made-up demo data and has nothing behind it to open."); return; }
            watchedCustomer = string.IsNullOrEmpty(o.Customer) ? null : o.Customer;
            demoOpen = overviewDemo;
            Begin(Engine.ForRemote(c.Open(o.D), o.D), true);
        }

        void OpenFound(FoundDevice f)
        {
            if (f.Loaded != null) OpenOverviewDevice(f.Loaded);
            else OpenTarget(f.ToRecent(), false);
        }

        static RemoteDeployment DeploymentOf(RecentDevice r)
        {
            RemoteDeployment d = new RemoteDeployment();
            d.Key = r.Id;
            d.Device = r.Device;
            d.Profile = r.Profile;
            d.Serial = r.Serial;
            d.User = r.User;
            d.State = r.State;
            d.StateText = r.StateText;
            d.LastUtc = r.LastSeenUtc;
            return d;
        }

        static Customer CustomerOf(string name)
        {
            try { return string.IsNullOrEmpty(name) ? CustomerUi.Book.Current : CustomerUi.Book.Find(name); }
            catch (Exception) { return null; }
        }

        // Opens a device from the recent list or the history, by the keys kept for it.
        void OpenTarget(RecentDevice r, bool fromHistory)
        {
            foreach (OverviewDevice o in LoadedDevices)
                if (FoundDevice.Of(o).Key == r.Key) { OpenOverviewDevice(o); return; }
            Customer cu = CustomerOf(r.Customer);
            string who = string.IsNullOrEmpty(r.Customer) ? "" : " of " + r.Customer;
            switch (r.Source)
            {
                case RecentSources.ThisPc:
                    Begin(Settings.NewLive(), true);
                    return;
                case RecentSources.File:
                    if (!File.Exists(r.Id)) { Status(r.Id + " is not there any more."); return; }
                    try { Begin(Engine.ForFile(r.Id), false); }
                    catch (Exception ex) { Fail(new Exception("This is not a TrackVice session: " + ex.Message)); }
                    return;
                case RecentSources.Folder:
                    if (!Directory.Exists(r.Id)) { Status(r.Id + " is not there any more."); return; }
                    Begin(Engine.ForFolder(r.Id), false);
                    return;
                case RecentSources.Agent:
                    {
                        string loc = cu != null ? cu.AgentLocation : string.IsNullOrEmpty(r.Customer) ? Secret.Load("AgentStore") : null;
                        if (string.IsNullOrWhiteSpace(loc)) { Status("TrackVice does not know where the devices" + who + " upload to any more. Devices > Customers sets it."); return; }
                        AgentCatalog c = new AgentCatalog(Stores.Open(loc));
                        RemoteDeployment d = DeploymentOf(r);
                        watchedCustomer = cu != null ? cu.Name : null;
                        Begin(Engine.ForRemote(c.Open(d), d), true);
                        return;
                    }
                case RecentSources.Sccm:
                    {
                        string srv = cu != null && !string.IsNullOrEmpty(cu.SccmServer) ? cu.SccmServer : Prefs.Get("SccmServer", "");
                        string site = cu != null && !string.IsNullOrEmpty(cu.SccmServer) ? cu.SccmSite : Prefs.Get("SccmSite", "");
                        if (string.IsNullOrWhiteSpace(srv)) { Status("No SMS Provider is set" + who + ". Devices > SCCM / on-premises > SMS Provider connection sets it."); return; }
                        OpenSccmRun(r, srv, site, cu != null ? cu.Name : null, fromHistory);
                        return;
                    }
                case RecentSources.Intune:
                    {
                        RemoteDeployment d = DeploymentOf(r);
                        if (graph != null && (cu == null || cu == CustomerUi.Book.Current))
                        {
                            watchedCustomer = cu != null ? cu.Name : null;
                            Begin(Engine.ForRemote(new IntuneCatalog(graph).Open(d), d), true);
                            return;
                        }
                        Status("Sign in to Intune" + who + " and pick " + r.Device + " in the list.");
                        OpenRemote(RemoteDialog.Intune);
                        return;
                    }
            }
        }

        // Connecting to the SMS Provider can take a while, so it happens off the UI thread.
        void OpenSccmRun(RecentDevice r, string srv, string site, string customer, bool fromHistory)
        {
            Status("Connecting to " + srv + "…");
            busyBar.Running = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                IRemoteCatalog c = null;
                Exception err = null;
                try { c = ConnectSccm(srv, site); }
                catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Safe.Run(delegate
                        {
                            busyBar.Running = false;
                            if (err != null) { Status("Could not connect to " + srv + ": " + err.Message); return; }
                            RemoteDeployment d = DeploymentOf(r);
                            watchedCustomer = customer;
                            navigating = fromHistory;
                            try { Begin(Engine.ForRemote(c.Open(d), d), true); }
                            finally { navigating = false; }
                            UpdateNav();
                        });
                    });
                }
                catch (Exception) { }
            });
        }

        // ---------- what was opened ----------

        // The keys to open a device again, or null for a demo.
        RecentDevice TargetOf(Engine e)
        {
            RecentDevice r = new RecentDevice();
            r.LastSeenUtc = DateTime.UtcNow;
            switch (e.Mode)
            {
                case Modes.Live:
                    r.Source = RecentSources.ThisPc;
                    r.Device = Environment.MachineName;
                    return r;
                case Modes.File:
                    r.Source = RecentSources.File;
                    r.Id = e.S.SourcePath ?? "";
                    r.Device = !string.IsNullOrEmpty(e.S.Computer) ? e.S.Computer : Txt.FileName(r.Id);
                    return r;
                case Modes.Folder:
                    r.Source = RecentSources.Folder;
                    r.Id = e.S.SourcePath ?? "";
                    r.Device = !string.IsNullOrEmpty(e.S.Computer) ? e.S.Computer : Txt.FileName(r.Id.TrimEnd('\\', '/'));
                    return r;
                case Modes.Agent:
                    AgentFeed f = e.Remote as AgentFeed;
                    if (f == null || f.Store.GetType().Name.IndexOf("Demo", StringComparison.Ordinal) >= 0) return null;
                    r.Source = RecentSources.Agent;
                    break;
                case Modes.Sccm:
                    r.Source = RecentSources.Sccm;
                    break;
                case Modes.Intune:
                    r.Source = RecentSources.Intune;
                    r.User = e.S.User ?? "";
                    break;
                default:
                    return null;
            }
            r.Id = e.S.SourcePath ?? "";
            r.Device = e.S.Computer ?? "";
            r.Customer = watchedCustomer ?? "";
            return r;
        }

        // Called from Begin: the device goes into the history and the recent devices, and the pages make way for it.
        void NoteOpened(Engine e)
        {
            bool demo = demoOpen;
            demoOpen = false;
            if (PageVisible)
            {
                HidePages();
                foreach (Control c in new Control[] { chips, rail, tabs, content }) c.Visible = true;
                UpdateSetupBar();
                Invalidate(true);
            }
            pausedForPage = false;
            RecentDevice r = demo || e.Mode == Modes.Demo ? null : TargetOf(e);
            currentTarget = null;
            savedState = null;
            bool keep = r != null && start.Shot == null && !start.Wall && (e.Mode != Modes.Live || startDone);
            if (keep)
            {
                currentTarget = Recent.Touch(r);
            }
            if (navigating)
            {
                if (nav.Current != null && nav.Current.Kind == NavEntry.Device) nav.Current.Tag = e;
            }
            else
            {
                NavEntry n = NavEntry.ForDevice(r, e.S.Computer);
                n.Tag = e;
                nav.Visit(n);
            }
            UpdateNav();
        }

        // Called at the end of Bind: the state of the device in the recent list follows what is shown.
        void UpdateRecent()
        {
            KeepPageTitle();
            if (currentTarget == null || engine == null || !loadedOnce) return;
            State st;
            string text;
            RecentStore.StateOf(engine.S, out st, out text);
            string sig = st + "|" + text + "|" + engine.S.Computer;
            if (sig == savedState) return;
            if (st == currentTarget.State && DateTime.UtcNow - savedAt < TimeSpan.FromSeconds(30) && savedState != null) return;
            savedState = sig;
            savedAt = DateTime.UtcNow;
            currentTarget.State = st;
            currentTarget.StateText = text;
            currentTarget.LastSeenUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(engine.S.Computer)) currentTarget.Device = engine.S.Computer;
            string profile = engine.S.GetInfo("Task sequence") ?? engine.S.GetInfo("Autopilot profile");
            if (!string.IsNullOrEmpty(profile)) currentTarget.Profile = profile;
            if (!string.IsNullOrEmpty(engine.S.User)) currentTarget.User = engine.S.User;
            RecentDevice kept = Recent.Touch(currentTarget);
            if (kept != null) currentTarget = kept;
        }

        // ---------- start ----------

        // After the welcome: on a PC that does not deploy, Overview is the home page unless Settings says this PC.
        void MaybeOverview()
        {
            startDone = true;
            if (WelcomeVisible) return;
            bool forSomething = start.Demo != null || start.File != null || start.Folder != null || start.Remote != null || start.Wall || start.Compact || start.Page != null || start.Shot != null;
            if (Prefs.Get(OpenOnPref, "overview") == "device") return;
            if (WelcomeGate.Reason(StartContext(), forSomething, false) != null) return;
            overviewAtStart = true;
            nav.Clear();
            GoHome(true);
        }

        // After the first read of this PC: a deployment that runs here goes first.
        void OverviewAfterLoad(Engine e)
        {
            if (!overviewAtStart) return;
            overviewAtStart = false;
            if (!OverviewVisible || e.Mode != Modes.Live) return;
            if (UpdateGate.Deploying(e.S, DateTime.UtcNow))
            {
                ShowDeviceView();
                nav.Visit(NavEntry.ForDevice(TargetOf(e), e.S.Computer));
                UpdateNav();
                Status("A deployment runs on this device, so TrackVice follows it.");
                return;
            }
            if (watching) { pausedForPage = true; SetWatching(false); }
        }

        // When the welcome is closed without a choice.
        void AfterWelcome()
        {
            if (engine != null && engine.Mode == Modes.Live && !UpdateGate.Deploying(engine.S, DateTime.UtcNow)) GoHome(true);
        }

        // ---------- screenshots ----------

        // --page overview, overview-empty and recent (--select text searches the recent devices, or on overview opens a device), with made-up data.
        public static readonly string[] OverviewPages = { "overview", "overview-empty", "recent" };

        void ShotOverview()
        {
            DateTime now = DateTime.UtcNow;
            bool empty = start.Page == "overview-empty";
            overviewDemo = true;
            recentStore = empty ? new RecentStore(null) : OverviewDemo.Recent(now);
            shotFacts = new SideFacts();
            if (!empty) shotFacts.Storage = shotFacts.Customers = shotFacts.SccmServer = true;
            overviewSnap = new OverviewLoader(empty ? null : OverviewDemo.Sources(now)).Read(now, true);
            overviewReadUtc = now;
            GoHome(true);
            if (start.Page == "recent") OpenRecentList(start.Select ?? "");
            else if (start.Select != null)
                foreach (OverviewDevice o in overviewSnap.Devices)
                    if (o.D.Device.IndexOf(start.Select, StringComparison.OrdinalIgnoreCase) >= 0 && o.Source.Catalog != null) { OpenOverviewDevice(o); break; }
            ActiveControl = null;
            overviewPage.ClearHover();
        }
    }
}
