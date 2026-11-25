namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    public static class OverviewKinds
    {
        public const string Agents = "agents", Sccm = "sccm";
    }

    // One place Overview reads devices from: the agent uploads of a customer, or the task sequence runs of an SCCM site.
    public interface IOverviewSource
    {
        string Customer { get; }
        string Kind { get; }
        // How often the list is read again at most.
        int RefreshSeconds { get; }
        List<RemoteDeployment> List(DateTime sinceUtc);
        // The whole session, or null when the source has none, as with SCCM runs.
        Session Load(RemoteDeployment d);
        // The catalog that opens a device of this source.
        IRemoteCatalog Catalog { get; }
    }

    // A source over a device list. The catalog is made on first use, on the reading thread, since connecting to an
    // SMS Provider can take a while.
    public sealed class CatalogSource : IOverviewSource, IOverviewRuns
    {
        readonly string customer, kind;
        readonly Func<IRemoteCatalog> make;
        readonly object gate = new object();
        IRemoteCatalog catalog;

        public CatalogSource(string customer, string kind, Func<IRemoteCatalog> make)
        {
            this.customer = customer;
            this.kind = kind;
            this.make = make;
        }

        public CatalogSource(string customer, string kind, IRemoteCatalog catalog)
        {
            this.customer = customer;
            this.kind = kind;
            this.catalog = catalog;
        }

        public string Customer
        {
            get { return customer; }
        }

        public string Kind
        {
            get { return kind; }
        }

        public int RefreshSeconds
        {
            get { return kind == OverviewKinds.Sccm ? 300 : 60; }
        }

        public IRemoteCatalog Catalog
        {
            get
            {
                lock (gate)
                {
                    if (catalog == null && make != null) catalog = make();
                    return catalog;
                }
            }
        }

        public List<RemoteDeployment> List(DateTime sinceUtc)
        {
            IRemoteCatalog c = Catalog;
            return c == null ? new List<RemoteDeployment>() : c.List(sinceUtc);
        }

        // Every run: an SCCM site is asked for earlier runs of a device too, and only as far back as OverviewCounts.SccmHistory.
        public List<RemoteDeployment> Runs(DateTime sinceUtc)
        {
            IRemoteCatalog c = Catalog;
            if (c == null) return new List<RemoteDeployment>();
            SccmCatalog sc = c as SccmCatalog;
            if (sc == null) return c.List(sinceUtc);
            bool was = sc.EarlierRuns;
            sc.EarlierRuns = true;
            try { return sc.List(sinceUtc); }
            finally { sc.EarlierRuns = was; }
        }

        public Session Load(RemoteDeployment d)
        {
            AgentCatalog a = Catalog as AgentCatalog;
            return a == null ? null : a.Load(d.Key);
        }
    }

    // A device on Overview: its row in a list, and its session when one was read.
    public sealed class OverviewDevice
    {
        public string Customer;
        public string Kind;
        public RemoteDeployment D;
        public Session S;
        public IOverviewSource Source;
        // A newer deployment of the same device exists, so this one will never finish: the VM or PC was reset or reinstalled.
        public bool Replaced;
        // A newer deployment of the same device exists, finished or not: this one's agent is gone, so it is history.
        public bool Superseded;

        // Intune or SCCM. A device with the agent in a task sequence is SCCM.
        public string Side
        {
            get { return Kind == OverviewKinds.Sccm ? DeviceSides.Sccm : DeviceSides.OfKind(S != null ? S.Kind : D.User); }
        }

        // The agent has not uploaded for longer than it ever waits.
        public bool Silent
        {
            get { return !Replaced && !Superseded && Kind == OverviewKinds.Agents && !States.IsFinal(D.State) && !D.EndUtc.HasValue && D.StateText != null && D.StateText.StartsWith("No upload for", StringComparison.Ordinal); }
        }

        public bool HasFailed
        {
            get { return D.State == State.Failed || (S != null && S.Count(State.Failed) > 0); }
        }

        // Deploying now: something runs, or it has just started and waits.
        public bool Running(DateTime nowUtc)
        {
            if (Replaced || Silent) return false;
            if ((D.StateText ?? "").StartsWith("Off since", StringComparison.Ordinal)) return false;
            if (S != null && S.CountActive > 0) return true;
            if (States.IsActive(D.State)) return true;
            return D.State == State.Pending && !D.EndUtc.HasValue && nowUtc - D.LastUtc < TimeSpan.FromMinutes(15);
        }

        // Done or failed, with nothing running any more.
        public bool Finished(DateTime nowUtc)
        {
            if (Replaced || Running(nowUtc) || Silent) return false;
            if (S != null && S.Items.Count > 0) return S.CountActive == 0 && (S.Count(State.Failed) > 0 || S.CountComplete == S.Items.Count);
            return D.State == State.Succeeded || D.State == State.Failed;
        }

        public DateTime EndUtc
        {
            get { return D.EndUtc ?? D.LastUtc; }
        }
    }

    public sealed class OverviewSnapshot
    {
        public readonly List<OverviewDevice> Devices = new List<OverviewDevice>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Customers = new List<string>();
        // Every run of every source, also older ones and repeats on one device, for the deployment counts. Empty when not read.
        public readonly List<OverviewDevice> Runs = new List<OverviewDevice>();
        public bool Agents, Sccm;
        public DateTime ReadUtc;
    }

    // Reads every source that is set up, off the UI thread. A list is read again only after its source's refresh time,
    // and a session only when the device uploaded since.
    public sealed class OverviewLoader
    {
        public const int MaxSessions = 150;
        public static readonly TimeSpan Window = TimeSpan.FromDays(7);

        sealed class Listed
        {
            public DateTime ReadUtc;
            public List<RemoteDeployment> Rows;
        }

        sealed class Loaded
        {
            public DateTime LastUtc;
            public Session S;
        }

        readonly List<IOverviewSource> sources;
        readonly Dictionary<IOverviewSource, Listed> lists = new Dictionary<IOverviewSource, Listed>();
        readonly Dictionary<IOverviewSource, Listed> runLists = new Dictionary<IOverviewSource, Listed>();
        readonly Dictionary<string, Loaded> sessions = new Dictionary<string, Loaded>();
        public int SessionReads;

        public OverviewLoader(IEnumerable<IOverviewSource> sources)
        {
            this.sources = new List<IOverviewSource>(sources ?? new IOverviewSource[0]);
        }

        public List<IOverviewSource> Sources
        {
            get { return new List<IOverviewSource>(sources); }
        }

        public static string KindName(string kind)
        {
            return kind == OverviewKinds.Sccm ? "task sequences in SCCM" : "devices with the agent";
        }

        public OverviewSnapshot Read(DateTime nowUtc, bool force)
        {
            OverviewSnapshot snap = new OverviewSnapshot();
            snap.ReadUtc = nowUtc;
            HashSet<string> used = new HashSet<string>();
            foreach (IOverviewSource src in sources)
            {
                if (src.Kind == OverviewKinds.Sccm) snap.Sccm = true; else snap.Agents = true;
                if (!string.IsNullOrEmpty(src.Customer) && !snap.Customers.Contains(src.Customer)) snap.Customers.Add(src.Customer);
                Listed l;
                lists.TryGetValue(src, out l);
                if (force || l == null || nowUtc - l.ReadUtc >= TimeSpan.FromSeconds(src.RefreshSeconds))
                {
                    try
                    {
                        List<RemoteDeployment> rows = src.List(nowUtc - Window);
                        l = new Listed();
                        l.ReadUtc = nowUtc;
                        l.Rows = rows ?? new List<RemoteDeployment>();
                        lists[src] = l;
                    }
                    catch (Exception ex)
                    {
                        snap.Problems.Add((string.IsNullOrEmpty(src.Customer) ? "" : src.Customer + ", ") + KindName(src.Kind) + ": " + ex.Message);
                    }
                }
                if (l == null) continue;
                List<RemoteDeployment> sorted = new List<RemoteDeployment>(l.Rows);
                sorted.Sort(delegate (RemoteDeployment a, RemoteDeployment b) { return b.LastUtc.CompareTo(a.LastUtc); });
                int n = 0;
                int first = snap.Devices.Count;
                foreach (RemoteDeployment d in sorted)
                {
                    OverviewDevice o = new OverviewDevice();
                    o.Customer = src.Customer;
                    o.Kind = src.Kind;
                    o.D = d;
                    o.Source = src;
                    if (src.Kind == OverviewKinds.Agents && n++ < MaxSessions)
                    {
                        string key = (src.Customer ?? "") + "|" + d.Key;
                        used.Add(key);
                        Loaded have;
                        if (sessions.TryGetValue(key, out have) && have.LastUtc == d.LastUtc) o.S = have.S;
                        else
                        {
                            try
                            {
                                SessionReads++;
                                o.S = src.Load(d);
                                have = new Loaded();
                                have.LastUtc = d.LastUtc;
                                have.S = o.S;
                                sessions[key] = have;
                            }
                            catch (Exception) { }
                        }
                    }
                    snap.Devices.Add(o);
                }
                AddRuns(snap, src, snap.Devices.GetRange(first, snap.Devices.Count - first), nowUtc, force);
            }
            List<string> drop = new List<string>();
            foreach (string k in sessions.Keys) if (!used.Contains(k)) drop.Add(k);
            foreach (string k in drop) sessions.Remove(k);
            OverviewModel.MarkReplaced(snap.Devices);
            return snap;
        }

        // Older runs are read less often than the week: the list is long and the numbers change slowly.
        static int RunsRefresh(IOverviewSource src)
        {
            return src.Kind == OverviewKinds.Sccm ? 900 : 300;
        }

        // Puts every run of the source in the snapshot. A run that is also in the week is the same object, so it keeps its
        // session. A source that cannot give older runs gives the devices it listed.
        void AddRuns(OverviewSnapshot snap, IOverviewSource src, List<OverviewDevice> own, DateTime nowUtc, bool force)
        {
            IOverviewRuns rs = src as IOverviewRuns;
            List<RemoteDeployment> rows = null;
            if (rs != null)
            {
                Listed l;
                runLists.TryGetValue(src, out l);
                if (force || l == null || nowUtc - l.ReadUtc >= TimeSpan.FromSeconds(RunsRefresh(src)))
                {
                    try
                    {
                        List<RemoteDeployment> r = rs.Runs(src.Kind == OverviewKinds.Sccm ? nowUtc - OverviewCounts.SccmHistory : DateTime.MinValue);
                        l = new Listed();
                        l.ReadUtc = nowUtc;
                        l.Rows = r ?? new List<RemoteDeployment>();
                        runLists[src] = l;
                    }
                    catch (Exception ex)
                    {
                        snap.Problems.Add((string.IsNullOrEmpty(src.Customer) ? "" : src.Customer + ", ") + KindName(src.Kind) + ", older deployments: " + ex.Message);
                    }
                }
                if (l != null) rows = l.Rows;
            }
            if (rows == null)
            {
                snap.Runs.AddRange(own);
                return;
            }
            Dictionary<string, OverviewDevice> week = new Dictionary<string, OverviewDevice>();
            foreach (OverviewDevice o in own) if (o.D.Key != null) week[o.D.Key] = o;
            HashSet<string> seen = new HashSet<string>();
            foreach (RemoteDeployment d in rows)
            {
                OverviewDevice o;
                if (d.Key == null || !week.TryGetValue(d.Key, out o))
                {
                    o = new OverviewDevice();
                    o.Customer = src.Customer;
                    o.Kind = src.Kind;
                    o.D = d;
                    o.Source = src;
                }
                if (d.Key != null) seen.Add(d.Key);
                snap.Runs.Add(o);
            }
            foreach (OverviewDevice o in own) if (o.D.Key == null || !seen.Contains(o.D.Key)) snap.Runs.Add(o);
        }
    }

    // All customers or one, and Intune, SCCM or both.
    public sealed class OverviewFilter
    {
        public string Customer;
        public string Side = DeviceSides.Both;

        public bool Shows(OverviewDevice o)
        {
            if (!string.IsNullOrEmpty(Customer) && !string.Equals(Customer, o.Customer, StringComparison.OrdinalIgnoreCase)) return false;
            return DeviceSides.Matches(o.Side, Side);
        }
    }

    // A device deploying now: how far it is, the phase it is in and what it waits for.
    public sealed class NowRow
    {
        public OverviewDevice Dev;
        public double? Percent;
        public string Phase, WaitsFor;
        // The time left of the deployment, short: "~25 min left" or "longer than usual". Null when there is no estimate.
        public string Left;
        public bool Failed;
    }

    public sealed class PeriodStats
    {
        public int Finished, Succeeded, Failed;
        public double AvgSeconds;
        public readonly List<OverviewDevice> FinishedDevices = new List<OverviewDevice>();
        public readonly List<OverviewDevice> FailedDevices = new List<OverviewDevice>();

        // Out of the finished devices, or null when none finished.
        public double? SuccessRate
        {
            get { return Finished == 0 ? (double?)null : Succeeded * 100.0 / Finished; }
        }
    }

    // An app or step over the last 7 days, with the devices behind it.
    public sealed class StepStat
    {
        public string Name, Kind;
        public double AvgSeconds;
        public int Failed, Devices;
        public readonly List<OverviewDevice> On = new List<OverviewDevice>();
    }

    // A tile under Needs attention. Open, when set, runs on the UI thread when the tile is clicked; otherwise the
    // tile opens the list of its devices.
    public sealed class AttentionTile
    {
        public string Key;
        // 0 a note, 1 a warning, 2 an error.
        public int Level;
        public string Title, Text;
        public int Count;
        public string ActionText;
        public Action Open;
        // Lower comes first. The tiles Overview makes itself use 10 to 40.
        public int Order = 50;
        public readonly List<OverviewDevice> Devices = new List<OverviewDevice>();
    }

    public interface IAttentionProvider
    {
        // Called off the UI thread at each refresh of Overview. Return no tiles when there is nothing to show.
        IEnumerable<AttentionTile> Tiles();
    }

    // Other parts of TrackVice add their own tiles to Needs attention here, such as a newer version to publish.
    public static class OverviewAttention
    {
        sealed class FuncProvider : IAttentionProvider
        {
            public Func<IEnumerable<AttentionTile>> F;
            public IEnumerable<AttentionTile> Tiles() { return F(); }
        }

        static readonly List<IAttentionProvider> providers = new List<IAttentionProvider>();

        public static void Register(IAttentionProvider p)
        {
            if (p == null) return;
            lock (providers) if (!providers.Contains(p)) providers.Add(p);
        }

        public static IAttentionProvider Register(Func<IEnumerable<AttentionTile>> tiles)
        {
            if (tiles == null) return null;
            FuncProvider p = new FuncProvider();
            p.F = tiles;
            Register(p);
            return p;
        }

        public static void Unregister(IAttentionProvider p)
        {
            lock (providers) providers.Remove(p);
        }

        // The tiles of every provider. A provider that fails is left out.
        public static List<AttentionTile> Collect()
        {
            List<IAttentionProvider> all;
            lock (providers) all = new List<IAttentionProvider>(providers);
            List<AttentionTile> tiles = new List<AttentionTile>();
            foreach (IAttentionProvider p in all)
            {
                try
                {
                    IEnumerable<AttentionTile> t = p.Tiles();
                    if (t != null) foreach (AttentionTile x in t) if (x != null) tiles.Add(x);
                }
                catch (Exception) { }
            }
            return tiles;
        }
    }

    // Something not set up yet, with the button that sets it up.
    public sealed class SetupNeed
    {
        public string Id, Title, Text, ActionText;
    }

    public sealed class OverviewData
    {
        public readonly List<NowRow> Now = new List<NowRow>();
        public readonly PeriodStats Today = new PeriodStats();
        public readonly PeriodStats Week = new PeriodStats();
        public readonly List<StepStat> Slowest = new List<StepStat>();
        public readonly List<StepStat> FailingMost = new List<StepStat>();
        public readonly List<AttentionTile> Attention = new List<AttentionTile>();
        public readonly List<OverviewDevice> Devices = new List<OverviewDevice>();
        public DeployCounts Counts = new DeployCounts();
        public bool HasAgents, HasSccm;

        public bool HasSources
        {
            get { return HasAgents || HasSccm; }
        }
    }

    public static class OverviewModel
    {
        public const string Storage = "storage", Customer = "customer", Sccm = "sccm";
        public const int TopSteps = 5;

        public static OverviewData Build(OverviewSnapshot snap, OverviewFilter f, DateTime nowUtc, DateTime todayStartUtc, IEnumerable<AttentionTile> more)
        {
            OverviewData data = new OverviewData();
            if (f == null) f = new OverviewFilter();
            if (snap != null)
            {
                MarkReplaced(snap.Devices);
                data.HasAgents = snap.Agents;
                data.HasSccm = snap.Sccm;
                foreach (OverviewDevice o in snap.Devices) if (f.Shows(o)) data.Devices.Add(o);
            }
            if (f.Side == DeviceSides.Intune) data.HasSccm = false;
            DateTime weekStart = nowUtc - OverviewLoader.Window;
            List<Session> weekSessions = new List<Session>();
            List<OverviewDevice> weekWithSession = new List<OverviewDevice>();
            List<Session> known = new List<Session>();
            foreach (OverviewDevice o in data.Devices) if (o.S != null && o.D.LastUtc >= weekStart) known.Add(o.S);
            ExpectedTimes history = ExpectedTimes.From(known, null, null, nowUtc);
            // The finished deployments seen so far (and read now) give each running device its expected total and time left.
            DeploymentHistory finished = DeploymentHistory.Shared ?? new DeploymentHistory();
            if (snap != null) finished.Learn(snap.Devices, nowUtc);
            foreach (OverviewDevice o in data.Devices)
            {
                if (o.Running(nowUtc))
                {
                    ExpectedTimes own = history;
                    if (o.S != null && o.S.Items.Count > 0)
                    {
                        HistoryStats hs = finished.Get(o.Customer ?? "", o.S.Kind, o.S.GetInfo("Task sequence") ?? o.S.GetInfo("Autopilot profile"));
                        if (hs != null) own = history.WithHistory(hs);
                    }
                    data.Now.Add(NowOf(o, own, nowUtc));
                }
                if (o.Finished(nowUtc))
                {
                    DateTime end = o.EndUtc;
                    if (end >= weekStart) Count(data.Week, o, nowUtc);
                    if (end >= todayStartUtc) Count(data.Today, o, nowUtc);
                }
                if (o.S != null && o.D.LastUtc >= weekStart)
                {
                    weekSessions.Add(o.S);
                    weekWithSession.Add(o);
                }
            }
            data.Now.Sort(delegate (NowRow a, NowRow b)
            {
                if (a.Failed != b.Failed) return a.Failed ? -1 : 1;
                return b.Dev.D.LastUtc.CompareTo(a.Dev.D.LastUtc);
            });
            data.Counts = OverviewCounts.Build(snap, f, nowUtc, todayStartUtc);
            Average(data.Week, nowUtc);
            Average(data.Today, nowUtc);
            Steps(data, weekSessions, weekWithSession, nowUtc);
            Attention(data, nowUtc);
            data.Attention.AddRange(AgentVersions.Tiles(data, nowUtc, AgentVersions.PublishedNow(), AgentVersions.PlacesNow(), AgentVersions.RunningNow()));
            if (more != null) foreach (AttentionTile t in more) if (t != null) data.Attention.Add(t);
            data.Attention.Sort(delegate (AttentionTile a, AttentionTile b)
            {
                int c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : b.Level.CompareTo(a.Level);
            });
            return data;
        }

        public const string ReplacedText = "Replaced by a newer deployment";

        // A deployment that never finished, and that the same device started over after a reset or a reinstall, is not
        // an agent that stopped sending: the newer deployment is the device now. Same customer, and the same device name,
        // the same Intune, Entra or serial number, or a name the newer one lists as an earlier name of the device.
        public static void MarkReplaced(IList<OverviewDevice> devices)
        {
            if (devices == null) return;
            foreach (OverviewDevice o in devices)
            {
                if (o.Kind != OverviewKinds.Agents || o.D == null) continue;
                bool finished = States.IsFinal(o.D.State) || o.D.EndUtc.HasValue;
                DateTime start = o.D.StartUtc ?? o.D.LastUtc;
                foreach (OverviewDevice p in devices)
                {
                    if (ReferenceEquals(p, o) || p.Kind != OverviewKinds.Agents || p.D == null) continue;
                    if (!string.Equals(p.Customer ?? "", o.Customer ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((p.D.StartUtc ?? p.D.LastUtc) <= start || p.D.LastUtc <= o.D.LastUtc) continue;
                    if (!SameDevice(o, p)) continue;
                    o.Superseded = true;
                    if (finished) break;
                    o.Replaced = true;
                    string t = o.D.StateText ?? "";
                    if (!t.StartsWith(ReplacedText, StringComparison.Ordinal))
                    {
                        int dot = t.IndexOf(" \u00b7 ", StringComparison.Ordinal);
                        o.D.StateText = ReplacedText + (t.StartsWith("No upload for", StringComparison.Ordinal) && dot >= 0 ? t.Substring(dot) : t.Length > 0 ? " \u00b7 " + t : "");
                    }
                    break;
                }
            }
        }

        static bool SameDevice(OverviewDevice a, OverviewDevice b)
        {
            if (!string.IsNullOrEmpty(a.D.Device) && string.Equals(a.D.Device, b.D.Device, StringComparison.OrdinalIgnoreCase)) return true;
            string[] ids = { DeviceFacts.IntuneId, DeviceFacts.EntraId, DeviceFacts.Serial };
            if (a.S != null && b.S != null)
                foreach (string k in ids)
                {
                    string x = a.S.GetInfo(k), y = b.S.GetInfo(k);
                    if (!string.IsNullOrEmpty(x) && string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
                }
            if (!string.IsNullOrEmpty(a.D.Device) && !string.IsNullOrEmpty(b.D.Serial))
                foreach (string n in b.D.Serial.Split(new char[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    if (string.Equals(n.Trim(), a.D.Device, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static NowRow NowOf(OverviewDevice o)
        {
            return NowOf(o, null, DateTime.UtcNow);
        }

        public static NowRow NowOf(OverviewDevice o, ExpectedTimes history, DateTime nowUtc)
        {
            NowRow r = new NowRow();
            r.Dev = o;
            r.Failed = o.HasFailed;
            Session s = o.S;
            if (s != null && s.Items.Count > 0)
            {
                r.Percent = OverallProgress.Percent(s, nowUtc, history);
                if (!r.Failed)
                {
                    DeploymentEstimate est = null;
                    try { est = DeploymentEstimate.Make(s, nowUtc, AutopilotFlow.BoxedTime(s, nowUtc), history); }
                    catch (Exception) { est = null; }
                    if (est != null) r.Left = est.Brief;
                }
                Item cur = null;
                foreach (Item i in s.Items)
                    if (States.IsActive(i.State) && i.Key != SccmFeed.RunKey && (cur == null || i.LastUtc > cur.LastUtc)) cur = i;
                if (cur == null)
                    foreach (Item i in s.Items)
                        if (i.State == State.Pending && i.Key != SccmFeed.RunKey) { cur = i; break; }
                if (cur != null)
                {
                    Phase ph = s.FindPhase(cur.PhaseKey);
                    r.Phase = ph != null ? ph.Name : null;
                    // A task sequence step already says what it does, such as Apply Operating System.
                    bool step = (cur.PhaseKey ?? "").StartsWith("ts:", StringComparison.Ordinal);
                    r.WaitsFor = step ? cur.DisplayName : States.Label(cur.State) + " " + cur.DisplayName;
                }
            }
            else if (o.Kind == OverviewKinds.Sccm)
            {
                r.Phase = "Task sequence";
                string t = o.D.StateText ?? "";
                const string last = "Running, last step: ";
                r.WaitsFor = t.StartsWith(last, StringComparison.Ordinal) ? t.Substring(last.Length) : t;
            }
            else r.WaitsFor = o.D.StateText;
            return r;
        }

        static void Count(PeriodStats p, OverviewDevice o, DateTime nowUtc)
        {
            p.Finished++;
            p.FinishedDevices.Add(o);
            if (o.HasFailed)
            {
                p.Failed++;
                p.FailedDevices.Add(o);
            }
            else p.Succeeded++;
        }

        static void Average(PeriodStats p, DateTime nowUtc)
        {
            double sum = 0;
            int n = 0;
            foreach (OverviewDevice o in p.FinishedDevices)
            {
                double sec = 0;
                if (o.S != null)
                {
                    FleetRun run = FleetStats.RunOf(o.S, nowUtc);
                    sec = run.Seconds;
                }
                if (sec <= 0)
                {
                    TimeSpan? d = o.D.Duration(nowUtc);
                    sec = d.HasValue ? d.Value.TotalSeconds : 0;
                }
                if (sec <= 0) continue;
                sum += sec;
                n++;
            }
            p.AvgSeconds = n == 0 ? 0 : sum / n;
        }

        static void Steps(OverviewData data, List<Session> sessions, List<OverviewDevice> devices, DateTime nowUtc)
        {
            if (sessions.Count == 0) return;
            FleetStats st = FleetStats.Build(sessions, nowUtc);
            List<FleetItem> slow = new List<FleetItem>();
            foreach (FleetItem i in st.Items) if (i.Timed > 0) slow.Add(i);
            slow.Sort(delegate (FleetItem a, FleetItem b) { return b.AvgSeconds.CompareTo(a.AvgSeconds); });
            List<FleetItem> fail = new List<FleetItem>();
            foreach (FleetItem i in st.Items) if (i.Failed > 0) fail.Add(i);
            fail.Sort(delegate (FleetItem a, FleetItem b)
            {
                int c = b.Failed.CompareTo(a.Failed);
                return c != 0 ? c : b.FailRate.CompareTo(a.FailRate);
            });
            for (int k = 0; k < slow.Count && k < TopSteps; k++) data.Slowest.Add(Stat(slow[k], devices, false));
            for (int k = 0; k < fail.Count && k < TopSteps; k++) data.FailingMost.Add(Stat(fail[k], devices, true));
        }

        static StepStat Stat(FleetItem f, List<OverviewDevice> devices, bool failedOnly)
        {
            StepStat s = new StepStat();
            s.Name = f.Name;
            s.Kind = f.Kind;
            s.AvgSeconds = f.AvgSeconds;
            s.Failed = f.Failed;
            s.Devices = f.Devices;
            foreach (OverviewDevice o in devices)
            {
                foreach (Item i in o.S.Items)
                {
                    if (i.Kind != f.Kind || !string.Equals(i.DisplayName, f.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (failedOnly && i.State != State.Failed) continue;
                    s.On.Add(o);
                    break;
                }
            }
            return s;
        }

        // Findings that are not already a tile of their own: failures, no progress and silence have theirs.
        public static bool WorthActing(Finding f)
        {
            if (f == null || f.Severity < 1) return false;
            return f.Kind != FindingKinds.Failed && f.Kind != FindingKinds.NoUploads && f.Kind != FindingKinds.Stuck
                && f.Kind != FindingKinds.TaskSequence && f.Kind != FindingKinds.MdmErrors;
        }

        static void Attention(OverviewData data, DateTime nowUtc)
        {
            DateTime weekStart = nowUtc - OverviewLoader.Window;
            AttentionTile failed = Tile("failed", 2, 10, "Failed devices");
            AttentionTile stuck = Tile("stuck", 1, 20, "No progress");
            AttentionTile silent = Tile("silent", 1, 30, "Agents that stopped sending");
            AttentionTile findings = Tile("findings", 1, 40, "Findings to act on");
            foreach (OverviewDevice o in data.Devices)
            {
                if (o.D.LastUtc < weekStart) continue;
                if (o.HasFailed && !o.Superseded) failed.Devices.Add(o);
                if (o.Silent && nowUtc - o.D.LastUtc < TimeSpan.FromDays(2)) silent.Devices.Add(o);
                bool noProgress = o.Kind == OverviewKinds.Sccm && o.D.State == State.Unknown && (o.D.StateText ?? "").StartsWith("No status since", StringComparison.Ordinal);
                bool worth = false;
                if (o.S != null)
                    foreach (Finding f in o.S.Findings)
                    {
                        if (f.Kind == FindingKinds.Stuck && o.Running(nowUtc)) noProgress = true;
                        if (WorthActing(f)) worth = true;
                    }
                if (noProgress) stuck.Devices.Add(o);
                if (worth && !o.HasFailed) findings.Devices.Add(o);
            }
            Finish(failed, "failed in the last 7 days.", "Look at what failed first: the error is decoded on each device.");
            Finish(stuck, "running without anything new for a while.", "An install may hang, or the device waits for something it does not get.");
            Finish(silent, "stopped uploading in the middle of a deployment.", "The device is off, restarting, asleep or without network.");
            Finish(findings, "with findings such as retries, the ESP time limit or a slow download.", null);
            foreach (AttentionTile t in new AttentionTile[] { failed, stuck, silent, findings })
                if (t.Count > 0) data.Attention.Add(t);
        }

        static AttentionTile Tile(string key, int level, int order, string title)
        {
            AttentionTile t = new AttentionTile();
            t.Key = key;
            t.Level = level;
            t.Order = order;
            t.Title = title;
            t.ActionText = "Show the devices";
            return t;
        }

        static void Finish(AttentionTile t, string what, string hint)
        {
            t.Devices.Sort(delegate (OverviewDevice a, OverviewDevice b) { return b.D.LastUtc.CompareTo(a.D.LastUtc); });
            t.Count = t.Devices.Count;
            if (t.Count == 0) return;
            string names = t.Devices[0].D.Device + (t.Count > 1 ? " and " + (t.Count - 1) + " more" : "");
            t.Text = (t.Count == 1 ? "1 device " : t.Count + " devices ") + what + " " + names + "." + (hint != null ? " " + hint : "");
        }

        // What is missing to see devices on Overview. Under both sides, a side shows only when something of it is set up
        // or nothing is set up at all, so someone who uses only SCCM is not asked for Your storage.
        public static List<SetupNeed> SetupNeeds(bool storage, bool customer, bool sccm, string side)
        {
            List<SetupNeed> l = new List<SetupNeed>();
            bool intuneAny = storage || customer;
            bool showIntune = side == DeviceSides.Intune || (side != DeviceSides.Sccm && (intuneAny || !sccm));
            bool showSccm = side == DeviceSides.Sccm || (side != DeviceSides.Intune && !intuneAny);
            if (showIntune)
            {
                if (!storage) l.Add(Need(Storage, "Your storage", "One storage account in your Azure. Devices download TrackVice from it and upload what they see to it.", "Set up Your storage"));
                if (!customer) l.Add(Need(Customer, "A first customer", "A customer or your own tenant, with the platform script that installs the agent on new devices.", "Add customer"));
            }
            if (showSccm && !sccm) l.Add(Need(Sccm, "An SCCM site", "The SMS Provider of your site, to see task sequences from the status messages they send.", "Enter the SMS Provider"));
            return l;
        }

        static SetupNeed Need(string id, string title, string text, string action)
        {
            SetupNeed n = new SetupNeed();
            n.Id = id;
            n.Title = title;
            n.Text = text;
            n.ActionText = action;
            return n;
        }

        // 1 h 04 min, or a dash when there is nothing to show.
        public static string Time(double seconds)
        {
            return seconds <= 0 ? "-" : Txt.DurationShort(TimeSpan.FromSeconds(seconds));
        }

        public static string Rate(double? percent)
        {
            return percent.HasValue ? Math.Round(percent.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %" : "-";
        }
    }
}
