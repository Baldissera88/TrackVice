namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class OverviewTests
    {
        // The demo sessions run on the clock, so the tests do too.
        static readonly DateTime Now = Whole(DateTime.UtcNow);
        static Session running;

        static DateTime Whole(DateTime t)
        {
            return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
        static readonly DateTime Today = Now.AddHours(-6);

        public static void Run()
        {
            StoreKeepsNewestFirst();
            StorePinsAndRemoves();
            StoreSavesAndReads();
            StoreKeepsNoLinks();
            StoreTrims();
            SearchFindsEveryField();
            SearchNeedsEveryWord();
            SearchRanksAndMerges();
            HistoryBackAndForward();
            HistoryDropsWhatWasAhead();
            HistoryReplacesSamePlace();
            HistoryIsCapped();
            NowListsFailedFirst();
            PeriodCounts();
            StepsFromSessions();
            AttentionTiles();
            ProvidersAddTiles();
            FilterByCustomerAndSide();
            SetupNeedsBySide();
            LoaderReadsSessionsOnce();
            LoaderReportsProblems();
            RecentStateOfSession();
        }

        static RecentDevice R(string source, string customer, string device, string id, int minutesAgo)
        {
            RecentDevice d = new RecentDevice();
            d.Source = source;
            d.Customer = customer;
            d.Device = device;
            d.Id = id;
            d.LastSeenUtc = Now.AddMinutes(-minutesAgo);
            return d;
        }

        static void StoreKeepsNewestFirst()
        {
            RecentStore s = new RecentStore(null);
            s.Touch(R(RecentSources.Agent, "Contoso", "CONTOSO-PC01", "sessions/a.json.gz", 30));
            s.Touch(R(RecentSources.Sccm, "Contoso", "CONTOSO-PC02", "16777301|P0120006|1", 10));
            s.Touch(R(RecentSources.ThisPc, "", "CONTOSO-ADM01", "", 20));
            List<RecentDevice> all = s.All;
            T.Eq(3, all.Count, "recent: three devices");
            T.Eq("CONTOSO-PC02", all[0].Device, "recent: newest first");
            T.Eq("CONTOSO-PC01", all[2].Device, "recent: oldest last");
            RecentDevice again = R(RecentSources.Agent, "Contoso", "CONTOSO-PC01", "sessions/a.json.gz", 1);
            again.StateText = "3 of 9 done";
            s.Touch(again);
            all = s.All;
            T.Eq(3, all.Count, "recent: opening again does not add a row");
            T.Eq("CONTOSO-PC01", all[0].Device, "recent: opening again moves it to the top");
            T.Eq("3 of 9 done", all[0].StateText, "recent: the state is updated");
            T.Check(s.Find(again.Key) != null, "recent: found by key");
        }

        static void StorePinsAndRemoves()
        {
            RecentStore s = new RecentStore(null);
            RecentDevice old = R(RecentSources.Agent, "Fabrikam", "FAB-LT001", "sessions/fab-lt001.json.gz", 600);
            s.Touch(old);
            s.Touch(R(RecentSources.Agent, "Fabrikam", "FAB-LT002", "sessions/fab-lt002.json.gz", 5));
            T.Check(s.Pin(old.Key, true), "recent: pin");
            T.Eq("FAB-LT001", s.All[0].Device, "recent: a pinned device is first");
            RecentDevice newer = R(RecentSources.Agent, "Fabrikam", "FAB-LT001", "sessions/fab-lt001.json.gz", 0);
            s.Touch(newer);
            T.Check(s.All[0].Pinned, "recent: opening a pinned device keeps the pin");
            T.Check(s.Pin(old.Key, false), "recent: unpin");
            T.Check(!s.All[0].Pinned, "recent: unpinned");
            T.Check(s.Remove(old.Key), "recent: remove");
            T.Eq(1, s.All.Count, "recent: one left");
            T.Check(!s.Remove(old.Key), "recent: removing twice does nothing");
            T.Check(!s.Pin("nothing", true), "recent: pinning an unknown key does nothing");
        }

        static void StoreSavesAndReads()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-recent-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "recent.txt");
            try
            {
                RecentStore s = new RecentStore(path);
                RecentDevice d = R(RecentSources.Intune, "Contoso", "CONTOSO-LT0417", "6d9a1f0e-3b2c-4c1d-9e8f-0a1b2c3d4e5f", 3);
                d.Serial = "5CG1234XYZ";
                d.User = "anna.hansen@contoso.com";
                d.Profile = "Contoso standard user-driven";
                d.State = State.Failed;
                d.StateText = "Failed\twith a tab";
                s.Touch(d);
                s.Touch(R(RecentSources.File, "", "CONTOSO-PC09", @"C:\Temp\CONTOSO-PC09.json", 2));
                s.Pin(d.Key, true);
                T.Check(File.Exists(path), "recent: the file is written");
                RecentStore again = new RecentStore(path);
                List<RecentDevice> all = again.All;
                T.Eq(2, all.Count, "recent: read back");
                RecentDevice r = all[0];
                T.Eq("CONTOSO-LT0417", r.Device, "recent: pinned first after reading");
                T.Check(r.Pinned, "recent: the pin is kept");
                T.Eq("5CG1234XYZ", r.Serial, "recent: serial kept");
                T.Eq("anna.hansen@contoso.com", r.User, "recent: user kept");
                T.Eq(State.Failed, r.State, "recent: state kept");
                T.Eq("Failed with a tab", r.StateText, "recent: a tab does not break the line");
                T.Eq(d.LastSeenUtc, r.LastSeenUtc, "recent: time kept");
                T.Eq(@"C:\Temp\CONTOSO-PC09.json", all[1].Id, "recent: a file path is kept");
                T.Check(RecentStore.Parse("#comment") == null, "recent: a comment is not a device");
                T.Check(RecentStore.Parse("too\tfew") == null, "recent: a short line is not a device");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void StoreKeepsNoLinks()
        {
            RecentStore s = new RecentStore(null);
            T.Check(s.Touch(R(RecentSources.Agent, "Contoso", "CONTOSO-PC01", "https://contosoagents.blob.core.windows.net/x?sv=1&sig=abc", 1)) == null, "recent: a link is never kept");
            T.Check(s.Touch(R("other", "Contoso", "CONTOSO-PC01", "x", 1)) == null, "recent: an unknown source is not kept");
            RecentDevice d = R(RecentSources.Folder, "", "CONTOSO-PC03", @"\\FS01\logs", 1);
            d.StateText = "password=Secret123 found";
            RecentDevice kept = s.Touch(d);
            T.Check(kept.StateText.IndexOf("Secret123", StringComparison.Ordinal) < 0, "recent: a secret is masked");
            T.Eq(1, s.All.Count, "recent: only the good one is kept");
        }

        static void StoreTrims()
        {
            RecentStore s = new RecentStore(null);
            RecentDevice pinned = R(RecentSources.Agent, "Contoso", "CONTOSO-OLD", "sessions/old.json.gz", 100000);
            s.Touch(pinned);
            s.Pin(pinned.Key, true);
            for (int i = 0; i < RecentStore.MaxUnpinned + 10; i++) s.Touch(R(RecentSources.Agent, "Contoso", "CONTOSO-PC" + i, "sessions/pc" + i + ".json.gz", 1000 - i));
            List<RecentDevice> all = s.All;
            T.Eq(RecentStore.MaxUnpinned + 1, all.Count, "recent: at most the limit, plus the pinned");
            T.Check(all[0].Pinned, "recent: an old pinned device stays");
            bool oldest = false;
            foreach (RecentDevice d in all) if (d.Device == "CONTOSO-PC0") oldest = true;
            T.Check(!oldest, "recent: the oldest unpinned is dropped");
        }

        static OverviewDevice Dev(string customer, string kind, string device, State st, int startMinAgo, int lastMinAgo, Session s)
        {
            OverviewDevice o = new OverviewDevice();
            o.Customer = customer;
            o.Kind = kind;
            RemoteDeployment d = new RemoteDeployment();
            d.Key = kind == OverviewKinds.Sccm ? "16777" + device.Length + "|P0120006|" + startMinAgo : "sessions/" + device.ToLowerInvariant() + ".json.gz";
            d.Device = device;
            d.State = st;
            d.StateText = States.Label(st);
            d.StartUtc = Now.AddMinutes(-startMinAgo);
            d.LastUtc = Now.AddMinutes(-lastMinAgo);
            if (States.IsFinal(st)) d.EndUtc = d.LastUtc;
            d.User = kind == OverviewKinds.Sccm ? "Required" : "Autopilot";
            d.Profile = kind == OverviewKinds.Sccm ? "Windows 11 Enterprise" : "Contoso standard user-driven";
            o.D = d;
            o.S = s;
            return o;
        }

        static void SearchFindsEveryField()
        {
            RecentDevice a = R(RecentSources.Intune, "Contoso", "CONTOSO-LT0417", "id-1", 5);
            a.Serial = "5CG1234XYZ";
            a.User = "anna.hansen@contoso.com";
            a.Profile = "Contoso standard user-driven";
            List<RecentDevice> recent = new List<RecentDevice>();
            recent.Add(a);
            List<OverviewDevice> loaded = new List<OverviewDevice>();
            loaded.Add(Dev("Woodgrove Bank", OverviewKinds.Sccm, "WGB-PC0042", State.Installing, 30, 1, null));
            T.Eq(1, DeviceSearch.Search("lt0417", recent, loaded, 0).Count, "search: device name");
            T.Eq(1, DeviceSearch.Search("5cg1234", recent, loaded, 0).Count, "search: serial number");
            T.Eq(1, DeviceSearch.Search("anna.hansen", recent, loaded, 0).Count, "search: user");
            T.Eq(1, DeviceSearch.Search("user-driven", recent, loaded, 0).Count, "search: Autopilot profile");
            List<FoundDevice> ts = DeviceSearch.Search("windows 11 enterprise", recent, loaded, 0);
            T.Eq(1, ts.Count, "search: task sequence of a loaded run");
            T.Eq(RecentSources.Sccm, ts[0].Source, "search: the run is from SCCM");
            T.Check(ts[0].Loaded != null, "search: a loaded run opens without looking it up");
            T.Eq(1, DeviceSearch.Search("woodgrove", recent, loaded, 0).Count, "search: customer");
            T.Eq(0, DeviceSearch.Search("fabrikam", recent, loaded, 0).Count, "search: nothing");
            T.Eq(1, DeviceSearch.Search("", recent, loaded, 0).Count, "search: empty shows the recent devices only");
        }

        static void SearchNeedsEveryWord()
        {
            RecentDevice a = R(RecentSources.Agent, "Contoso", "CONTOSO-PC01", "sessions/pc01.json.gz", 5);
            a.Profile = "Windows 11 Enterprise";
            RecentDevice b = R(RecentSources.Agent, "Fabrikam", "FAB-PC01", "sessions/fab-pc01.json.gz", 5);
            b.Profile = "Windows 11 Enterprise";
            List<RecentDevice> l = new List<RecentDevice>();
            l.Add(a);
            l.Add(b);
            T.Eq(2, DeviceSearch.Search("pc01", l, null, 0).Count, "search: one word, two devices");
            T.Eq(1, DeviceSearch.Search("pc01 fabrikam", l, null, 0).Count, "search: every word must be found");
            T.Eq(1, DeviceSearch.Search("pc01", l, null, 1).Count, "search: at most max");
        }

        static void SearchRanksAndMerges()
        {
            RecentDevice exact = R(RecentSources.Agent, "Contoso", "PC01", "sessions/pc01.json.gz", 50);
            RecentDevice longer = R(RecentSources.Agent, "Contoso", "CONTOSO-PC01", "sessions/contoso-pc01.json.gz", 1);
            RecentDevice pinned = R(RecentSources.Agent, "Contoso", "LAB-PC01", "sessions/lab-pc01.json.gz", 90);
            pinned.Pinned = true;
            List<RecentDevice> l = new List<RecentDevice>();
            l.Add(longer);
            l.Add(pinned);
            l.Add(exact);
            List<FoundDevice> hits = DeviceSearch.Search("pc01", l, null, 0);
            T.Eq("PC01", hits[0].Device, "search: the exact name first");
            T.Eq("LAB-PC01", hits[1].Device, "search: then pinned");
            T.Eq("CONTOSO-PC01", hits[2].Device, "search: then the rest");
            OverviewDevice fresh = Dev("Contoso", OverviewKinds.Agents, "CONTOSO-PC01", State.Failed, 30, 0, null);
            fresh.D.Key = "sessions/contoso-pc01.json.gz";
            List<OverviewDevice> loaded = new List<OverviewDevice>();
            loaded.Add(fresh);
            List<FoundDevice> merged = DeviceSearch.Merge(l, loaded);
            T.Eq(3, merged.Count, "search: a device recent and loaded shows once");
            FoundDevice m = null;
            foreach (FoundDevice f in merged) if (f.Device == "CONTOSO-PC01") m = f;
            T.Eq(State.Failed, m.State, "search: the loaded state is used");
            T.Check(m.Recent && m.Loaded != null, "search: recent and loaded together");
        }

        static NavEntry Dev(string name)
        {
            return NavEntry.ForDevice(R(RecentSources.Agent, "Contoso", name, "sessions/" + name + ".json.gz", 0), name);
        }

        static void HistoryBackAndForward()
        {
            NavHistory h = new NavHistory();
            T.Check(!h.CanBack && !h.CanForward && h.Current == null, "history: empty");
            h.Visit(NavEntry.ForOverview());
            h.Visit(Dev("CONTOSO-PC01"));
            h.Visit(NavEntry.ForList("recent", "Recent devices", null));
            T.Check(h.CanBack && !h.CanForward, "history: can go back");
            T.Eq(NavEntry.Device, h.Back().Kind, "history: back to the device");
            T.Eq(NavEntry.Overview, h.Back().Kind, "history: back to Overview");
            T.Check(h.Back() == null, "history: nothing before the first");
            T.Eq(NavEntry.Overview, h.Current.Kind, "history: stays on the first");
            T.Eq(NavEntry.Device, h.Forward().Kind, "history: forward");
            T.Eq(NavEntry.List, h.Forward().Kind, "history: forward again");
            T.Check(h.Forward() == null, "history: nothing after the last");
        }

        static void HistoryDropsWhatWasAhead()
        {
            NavHistory h = new NavHistory();
            h.Visit(NavEntry.ForOverview());
            h.Visit(Dev("CONTOSO-PC01"));
            h.Visit(Dev("CONTOSO-PC02"));
            h.Back();
            h.Back();
            h.Visit(Dev("CONTOSO-PC03"));
            T.Eq(2, h.Count, "history: what was ahead is dropped");
            T.Check(!h.CanForward, "history: nothing ahead");
            T.Eq("CONTOSO-PC03", h.Current.Title, "history: the new place");
        }

        static void HistoryReplacesSamePlace()
        {
            NavHistory h = new NavHistory();
            h.Visit(NavEntry.ForOverview());
            h.Visit(NavEntry.ForOverview());
            T.Eq(1, h.Count, "history: the same place twice is one");
            h.Visit(Dev("CONTOSO-PC01"));
            h.Visit(Dev("CONTOSO-PC01"));
            T.Eq(2, h.Count, "history: the same device twice is one");
            h.Visit(NavEntry.ForList("tile:failed", "Failed devices", null));
            h.Visit(NavEntry.ForList("tile:stuck", "No progress", null));
            T.Eq(4, h.Count, "history: two lists are two places");
        }

        static void HistoryIsCapped()
        {
            NavHistory h = new NavHistory();
            for (int i = 0; i < NavHistory.Max + 20; i++) h.Visit(Dev("CONTOSO-PC" + i));
            T.Eq(NavHistory.Max, h.Count, "history: at most the limit");
            T.Eq("CONTOSO-PC" + (NavHistory.Max + 19), h.Current.Title, "history: the newest is current");
        }

        static Session Demo(string name, double seconds, string computer)
        {
            Engine e = Engine.ForDemo(name);
            e.Apply(e.DemoTo(seconds));
            e.S.Computer = computer;
            return e.S;
        }

        // A finished Autopilot enrollment where one app failed.
        static Session Finished(string computer)
        {
            Session s = new Session();
            s.Computer = computer;
            s.ClockUtc = Now.AddMinutes(-30);
            Item(s, "Contoso VPN Client", State.Succeeded, 90, 8);
            Item(s, "Microsoft Teams", State.Succeeded, 80, 4);
            Item(s, "Contoso Timesheet", State.Failed, 70, 6);
            return s;
        }

        static void Item(Session s, string name, State st, int fromMinAgo, int minutes)
        {
            Item i = new Item();
            i.Key = "intune:" + name.ToLowerInvariant();
            i.Name = name;
            i.Kind = Kinds.Win32App;
            i.PhaseKey = Phases.AccountSetup;
            i.State = st;
            i.StartUtc = Now.AddMinutes(-fromMinAgo);
            i.FirstUtc = i.StartUtc.Value;
            i.LastUtc = Now.AddMinutes(-fromMinAgo + minutes);
            i.EndUtc = i.LastUtc;
            s.Items.Add(i);
        }

        static OverviewSnapshot Snapshot()
        {
            OverviewSnapshot s = new OverviewSnapshot();
            s.Agents = true;
            s.Sccm = true;
            if (running == null)
            {
                running = Demo("autopilot", 10 * 60, "CONTOSO-LT0417");
                Item(running, "Contoso Timesheet", State.Failed, 8, 1);
            }
            OverviewDevice a = Dev("Contoso", OverviewKinds.Agents, "CONTOSO-LT0417", State.Installing, 10, 5, running);
            a.D.User = running.Kind;
            s.Devices.Add(a);
            Session failedDone = Finished("CONTOSO-LT0433");
            OverviewDevice b = Dev("Contoso", OverviewKinds.Agents, "CONTOSO-LT0433", State.Failed, 90, 30, failedDone);
            b.D.User = failedDone.Kind;
            s.Devices.Add(b);
            s.Devices.Add(Dev("Woodgrove Bank", OverviewKinds.Sccm, "WGB-PC0042", State.Installing, 20, 1, null));
            OverviewDevice c = Dev("Woodgrove Bank", OverviewKinds.Sccm, "WGB-PC0050", State.Failed, 25, 2, null);
            c.D.StateText = "Running, last step: Install Applications";
            c.D.State = State.Installing;
            c.D.EndUtc = null;
            s.Devices.Add(c);
            s.Devices.Add(Dev("Woodgrove Bank", OverviewKinds.Sccm, "WGB-PC0001", State.Succeeded, 3 * 24 * 60, 3 * 24 * 60 - 70, null));
            s.Devices.Add(Dev("Woodgrove Bank", OverviewKinds.Sccm, "WGB-PC0002", State.Succeeded, 200, 140, null));
            OverviewDevice silent = Dev("Contoso", OverviewKinds.Agents, "CONTOSO-PC117", State.Unknown, 60, 40, null);
            silent.D.StateText = "No upload for 40 min · 5 of 12 done";
            s.Devices.Add(silent);
            OverviewDevice stuck = Dev("Woodgrove Bank", OverviewKinds.Sccm, "WGB-PC0060", State.Unknown, 600, 500, null);
            stuck.D.StateText = "No status since 2026-11-18 03:40:00";
            stuck.D.EndUtc = null;
            s.Devices.Add(stuck);
            s.Devices.Add(Dev("Woodgrove Bank", OverviewKinds.Sccm, "WGB-PC-OLD", State.Failed, 10 * 24 * 60, 10 * 24 * 60 - 60, null));
            return s;
        }

        static void NowListsFailedFirst()
        {
            OverviewData d = OverviewModel.Build(Snapshot(), new OverviewFilter(), Now, Today, null);
            T.Eq(3, d.Now.Count, "overview: three devices deploy now");
            T.Eq("CONTOSO-LT0417", d.Now.Count > 0 ? d.Now[0].Dev.D.Device : null, "overview: a device with a failure comes first");
            NowRow a = d.Now[0];
            T.Check(a.Percent.HasValue && a.Percent.Value > 0 && a.Percent.Value < 100, "overview: percent from the session (" + a.Percent + ")");
            T.Check(!string.IsNullOrEmpty(a.Phase), "overview: the phase of what runs");
            T.Check(!string.IsNullOrEmpty(a.WaitsFor), "overview: what it waits for");
            NowRow sccm = null;
            foreach (NowRow r in d.Now) if (r.Dev.D.Device == "WGB-PC0050") sccm = r;
            T.Eq("Install Applications", sccm.WaitsFor, "overview: the last step of a task sequence");
            T.Eq("Task sequence", sccm.Phase, "overview: a task sequence has one phase");
            T.Check(!sccm.Percent.HasValue, "overview: no percent without a session");
        }

        static void PeriodCounts()
        {
            OverviewData d = OverviewModel.Build(Snapshot(), new OverviewFilter(), Now, Today, null);
            T.Eq(3, d.Week.Finished, "overview: finished in 7 days");
            T.Eq(1, d.Week.Failed, "overview: failed in 7 days");
            T.Eq(2, d.Week.Succeeded, "overview: succeeded in 7 days");
            T.Check(Math.Abs(d.Week.SuccessRate.Value - 200.0 / 3) < 0.01, "overview: success rate");
            T.Eq(2, d.Today.Finished, "overview: finished today");
            T.Check(d.Week.AvgSeconds > 0, "overview: average time");
            T.Eq("67 %", OverviewModel.Rate(d.Week.SuccessRate), "overview: rate text");
            T.Eq("-", OverviewModel.Rate(null), "overview: no rate");
            T.Eq("-", OverviewModel.Time(0), "overview: no time");
            T.Eq("1 h 10 min", OverviewModel.Time(70 * 60), "overview: time text");
        }

        static void StepsFromSessions()
        {
            OverviewData d = OverviewModel.Build(Snapshot(), new OverviewFilter(), Now, Today, null);
            T.Check(d.Slowest.Count > 0 && d.Slowest.Count <= OverviewModel.TopSteps, "overview: slowest steps");
            for (int i = 1; i < d.Slowest.Count; i++) T.Check(d.Slowest[i - 1].AvgSeconds >= d.Slowest[i].AvgSeconds, "overview: slowest first");
            T.Check(d.Slowest.Count == 0 || d.Slowest[0].On.Count > 0, "overview: a slow step links to its devices");
            T.Check(d.FailingMost.Count > 0, "overview: steps that fail");
            bool timesheet = false;
            foreach (StepStat s in d.FailingMost)
                if (s.Name.IndexOf("Timesheet", StringComparison.Ordinal) >= 0)
                {
                    timesheet = true;
                    bool on = false;
                    foreach (OverviewDevice o in s.On) if (o.D.Device == "CONTOSO-LT0433") on = true;
                    T.Check(on, "overview: the failing step links to the devices it failed on");
                }
            T.Check(timesheet, "overview: the app that failed is listed");
        }

        static AttentionTile Tile(OverviewData d, string key)
        {
            foreach (AttentionTile t in d.Attention) if (t.Key == key) return t;
            return null;
        }

        static void AttentionTiles()
        {
            OverviewData d = OverviewModel.Build(Snapshot(), new OverviewFilter(), Now, Today, null);
            AttentionTile failed = Tile(d, "failed");
            T.Check(failed != null && failed.Count == 2, "attention: failed devices in 7 days, not older");
            T.Eq(2, failed.Level, "attention: failures are errors");
            T.Check(failed.Text.IndexOf("devices failed", StringComparison.Ordinal) >= 0, "attention: plain text");
            AttentionTile silent = Tile(d, "silent");
            T.Check(silent != null && silent.Count == 1 && silent.Devices[0].D.Device == "CONTOSO-PC117", "attention: an agent that stopped sending");
            AttentionTile stuck = Tile(d, "stuck");
            bool wgb = false;
            if (stuck != null) foreach (OverviewDevice o in stuck.Devices) if (o.D.Device == "WGB-PC0060") wgb = true;
            T.Check(wgb, "attention: a run with no status");
            T.Eq("failed", d.Attention[0].Key, "attention: failures first");
            OverviewData empty = OverviewModel.Build(new OverviewSnapshot(), new OverviewFilter(), Now, Today, null);
            T.Eq(0, empty.Attention.Count, "attention: nothing without devices");
            T.Check(!empty.HasSources, "overview: no sources");
            Finding f = new Finding();
            f.Kind = FindingKinds.Retries;
            f.Severity = 1;
            T.Check(OverviewModel.WorthActing(f), "attention: a retry loop is worth acting on");
            f.Kind = FindingKinds.Failed;
            f.Severity = 2;
            T.Check(!OverviewModel.WorthActing(f), "attention: a failure has its own tile");
        }

        sealed class PublishLike : IAttentionProvider
        {
            public IEnumerable<AttentionTile> Tiles()
            {
                AttentionTile t = new AttentionTile();
                t.Key = "publish";
                t.Title = "Publish 9.9.9";
                t.Text = "TrackVice 9.9.9 is installed. Your devices still get 9.9.8.";
                t.ActionText = "Publish";
                t.Order = 5;
                return new AttentionTile[] { t };
            }
        }

        sealed class Broken : IAttentionProvider
        {
            public IEnumerable<AttentionTile> Tiles() { throw new InvalidOperationException("broken"); }
        }

        static void ProvidersAddTiles()
        {
            PublishLike p = new PublishLike();
            Broken b = new Broken();
            OverviewAttention.Register(p);
            OverviewAttention.Register(p);
            OverviewAttention.Register(b);
            IAttentionProvider f = OverviewAttention.Register(delegate { return new AttentionTile[0]; });
            try
            {
                List<AttentionTile> tiles = OverviewAttention.Collect();
                T.Eq(1, tiles.Count, "attention: a provider registered twice counts once, a broken one is left out");
                OverviewData d = OverviewModel.Build(Snapshot(), new OverviewFilter(), Now, Today, tiles);
                T.Eq("publish", d.Attention[0].Key, "attention: a provider's tile goes by its order");
            }
            finally
            {
                OverviewAttention.Unregister(p);
                OverviewAttention.Unregister(b);
                OverviewAttention.Unregister(f);
            }
            T.Eq(0, OverviewAttention.Collect().Count, "attention: unregistered");
        }

        static void FilterByCustomerAndSide()
        {
            OverviewFilter f = new OverviewFilter();
            f.Customer = "Contoso";
            OverviewData d = OverviewModel.Build(Snapshot(), f, Now, Today, null);
            foreach (OverviewDevice o in d.Devices) T.Eq("Contoso", o.Customer, "filter: one customer");
            T.Eq(3, d.Devices.Count, "filter: the devices of Contoso");
            f.Customer = null;
            f.Side = DeviceSides.Sccm;
            d = OverviewModel.Build(Snapshot(), f, Now, Today, null);
            bool agents = false;
            foreach (OverviewDevice o in d.Devices) if (o.Kind == OverviewKinds.Agents && o.Side == DeviceSides.Intune) agents = true;
            T.Check(!agents, "filter: SCCM leaves Autopilot out");
            f.Side = DeviceSides.Intune;
            d = OverviewModel.Build(Snapshot(), f, Now, Today, null);
            foreach (OverviewDevice o in d.Devices) T.Check(o.Kind != OverviewKinds.Sccm, "filter: Intune leaves SCCM runs out");
            T.Check(!d.HasSccm, "filter: Intune has no SCCM source");
        }

        static void SetupNeedsBySide()
        {
            T.Eq(3, OverviewModel.SetupNeeds(false, false, false, DeviceSides.Both).Count, "setup: all missing");
            T.Eq(2, OverviewModel.SetupNeeds(false, false, false, DeviceSides.Intune).Count, "setup: Intune leaves SCCM out");
            List<SetupNeed> sccm = OverviewModel.SetupNeeds(false, false, false, DeviceSides.Sccm);
            T.Eq(1, sccm.Count, "setup: SCCM needs only the site");
            T.Eq(OverviewModel.Sccm, sccm[0].Id, "setup: the SMS Provider");
            T.Eq(0, OverviewModel.SetupNeeds(true, true, true, DeviceSides.Both).Count, "setup: nothing missing");
            T.Eq(0, OverviewModel.SetupNeeds(false, false, true, DeviceSides.Both).Count, "setup: only SCCM in use asks for nothing of Intune");
            T.Eq(1, OverviewModel.SetupNeeds(true, false, false, DeviceSides.Both).Count, "setup: Intune half done asks for the customer, not SCCM");
        }

        sealed class FakeSource : IOverviewSource
        {
            public string Customer { get { return "Contoso"; } }
            public string Kind { get { return OverviewKinds.Agents; } }
            public int RefreshSeconds { get { return 60; } }
            public IRemoteCatalog Catalog { get { return null; } }
            public List<RemoteDeployment> Rows = new List<RemoteDeployment>();
            public int Lists, Loads;
            public bool Fail;

            public List<RemoteDeployment> List(DateTime sinceUtc)
            {
                Lists++;
                if (Fail) throw new StoreException("The link has expired.");
                return new List<RemoteDeployment>(Rows);
            }

            public Session Load(RemoteDeployment d)
            {
                Loads++;
                Session s = new Session();
                s.Computer = d.Device;
                return s;
            }
        }

        static RemoteDeployment Row(string device, DateTime last)
        {
            RemoteDeployment d = new RemoteDeployment();
            d.Key = "sessions/" + device.ToLowerInvariant() + ".json.gz";
            d.Device = device;
            d.LastUtc = last;
            d.State = State.Installing;
            return d;
        }

        static void LoaderReadsSessionsOnce()
        {
            FakeSource src = new FakeSource();
            src.Rows.Add(Row("CONTOSO-PC01", Now.AddMinutes(-5)));
            src.Rows.Add(Row("CONTOSO-PC02", Now.AddMinutes(-1)));
            OverviewLoader l = new OverviewLoader(new IOverviewSource[] { src });
            OverviewSnapshot s = l.Read(Now, false);
            T.Eq(2, s.Devices.Count, "loader: two devices");
            T.Eq("CONTOSO-PC02", s.Devices[0].D.Device, "loader: newest first");
            T.Check(s.Devices[0].S != null, "loader: the session is read");
            T.Check(s.Agents && !s.Sccm, "loader: what is set up");
            T.Eq("Contoso", s.Customers[0], "loader: the customers");
            T.Eq(2, src.Loads, "loader: two sessions read");
            l.Read(Now.AddSeconds(10), false);
            T.Eq(1, src.Lists, "loader: the list is not read again before its time");
            src.Rows[0] = Row("CONTOSO-PC01", Now.AddMinutes(1));
            l.Read(Now.AddSeconds(70), false);
            T.Eq(2, src.Lists, "loader: the list is read again after its time");
            T.Eq(3, src.Loads, "loader: only the session that changed is read again");
            l.Read(Now.AddSeconds(75), true);
            T.Eq(3, src.Lists, "loader: Refresh reads at once");
        }

        static void LoaderReportsProblems()
        {
            FakeSource src = new FakeSource();
            src.Rows.Add(Row("CONTOSO-PC01", Now.AddMinutes(-5)));
            OverviewLoader l = new OverviewLoader(new IOverviewSource[] { src });
            l.Read(Now, false);
            src.Fail = true;
            OverviewSnapshot s = l.Read(Now.AddMinutes(5), false);
            T.Eq(1, s.Problems.Count, "loader: a source that fails is reported");
            T.Check(s.Problems[0].StartsWith("Contoso, devices with the agent: ", StringComparison.Ordinal), "loader: which source failed (" + s.Problems[0] + ")");
            T.Eq(1, s.Devices.Count, "loader: the last list is kept when reading fails");
            OverviewSnapshot none = new OverviewLoader(null).Read(Now, false);
            T.Check(!none.Agents && !none.Sccm && none.Devices.Count == 0, "loader: nothing is read when nothing is set up");
        }

        static void RecentStateOfSession()
        {
            State st;
            string text;
            RecentStore.StateOf(Finished("CONTOSO-LT0433"), out st, out text);
            T.Eq(State.Failed, st, "recent: a failed session");
            T.Check(text.IndexOf("failed", StringComparison.Ordinal) > 0, "recent: says how many failed (" + text + ")");
            RecentStore.StateOf(new Session(), out st, out text);
            T.Eq("Nothing found yet", text, "recent: an empty session");
        }
    }
}
