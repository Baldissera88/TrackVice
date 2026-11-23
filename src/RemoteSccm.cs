namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Management;
    using System.Text;

    // Reads SCCM through the SMS Provider over WMI, with the signed-in Windows account, like the console does.
    // Any role that can see the devices and task sequence deployments is enough; TrackVice only reads.
    public interface ISccmQuery
    {
        List<Dictionary<string, object>> Query(string wql);
    }

    public sealed class SccmWmi : ISccmQuery
    {
        public readonly string Server;
        public string SiteCode;
        ManagementScope scope;

        public SccmWmi(string server, string siteCode)
        {
            Server = string.IsNullOrEmpty(server) ? "." : server.Trim().TrimStart('\\');
            SiteCode = string.IsNullOrEmpty(siteCode) ? null : siteCode.Trim().ToUpperInvariant();
        }

        static ConnectionOptions Options()
        {
            ConnectionOptions o = new ConnectionOptions();
            o.Impersonation = ImpersonationLevel.Impersonate;
            o.Authentication = AuthenticationLevel.PacketPrivacy;
            o.EnablePrivileges = true;
            o.Timeout = TimeSpan.FromSeconds(30);
            return o;
        }

        // Connects, and finds the site code from SMS_ProviderLocation when it was not given.
        public void Connect()
        {
            if (SiteCode == null)
            {
                ManagementScope root = new ManagementScope(@"\\" + Server + @"\root\SMS", Options());
                root.Connect();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(root, new ObjectQuery("SELECT SiteCode FROM SMS_ProviderLocation WHERE ProviderForLocalSite = true")))
                using (ManagementObjectCollection c = s.Get())
                {
                    foreach (ManagementBaseObject o in c)
                    {
                        SiteCode = Convert.ToString(o["SiteCode"], CultureInfo.InvariantCulture);
                        o.Dispose();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(SiteCode)) throw new InvalidOperationException(Server + " is not an SMS Provider for a site. Enter the server that runs the SMS Provider, or the site code.");
            }
            scope = new ManagementScope(@"\\" + Server + @"\root\SMS\site_" + SiteCode, Options());
            scope.Connect();
        }

        public List<Dictionary<string, object>> Query(string wql)
        {
            if (scope == null) Connect();
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            EnumerationOptions opt = new EnumerationOptions();
            opt.Timeout = TimeSpan.FromSeconds(60);
            opt.ReturnImmediately = false;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery(wql), opt))
                using (ManagementObjectCollection c = s.Get())
                {
                    foreach (ManagementBaseObject o in c)
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (PropertyData p in o.Properties)
                        {
                            object v = p.Value;
                            if (v != null && p.Type == CimType.DateTime) v = FromDmtf(v as string);
                            row[p.Name] = v;
                        }
                        rows.Add(row);
                        o.Dispose();
                    }
                }
            }
            catch (ManagementException ex)
            {
                // "Generic failure" alone says nothing. The SMS Provider puts the reason in the extended error.
                string why = null;
                try { if (ex.ErrorInformation != null) why = Convert.ToString(ex.ErrorInformation["Description"], CultureInfo.InvariantCulture); }
                catch (Exception) { }
                throw new InvalidOperationException(ex.Message.Trim() + (string.IsNullOrEmpty(why) ? "" : ": " + why.Trim()) + " (query: " + wql + ")", ex);
            }
            return rows;
        }

        // The SMS Provider keeps times in UTC and writes them without a time zone: 20261113121500.000000+***.
        public static string Dmtf(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".000000+***";
        }

        // The same time with a zero offset, for a provider that does not take the form above.
        public static string DmtfZero(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".000000+000";
        }

        // A time from the SMS Provider as UTC. Without a time zone (+***) it is UTC already; with an offset in minutes it is local to that offset.
        public static object FromDmtf(string s)
        {
            if (s == null || s.Length < 14) return null;
            DateTime t;
            if (!DateTime.TryParseExact(s.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            t = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            int offset;
            if (s.Length >= 25 && (s[21] == '+' || s[21] == '-') && int.TryParse(s.Substring(22, 3), NumberStyles.None, CultureInfo.InvariantCulture, out offset))
                t = t.AddMinutes(s[21] == '+' ? -offset : offset);
            return t;
        }
    }

    // Task sequences on other devices, from the status message every step sends to the site (SMS_TaskSequenceExecutionStatus).
    public sealed class SccmCatalog : IRemoteCatalog
    {
        readonly ISccmQuery q;
        readonly string server;
        public Func<DateTime, string> Dmtf = SccmWmi.Dmtf;

        public SccmCatalog(ISccmQuery query, string serverName)
        {
            q = query;
            server = serverName;
        }

        public string Title
        {
            get { return "Task sequences in SCCM"; }
        }

        public string Source
        {
            get
            {
                SccmWmi w = q as SccmWmi;
                return w != null ? "Site " + w.SiteCode + " on " + w.Server : server;
            }
        }

        public int RefreshSeconds
        {
            get { return 30; }
        }

        // Also lists the runs before the newest one of each device and deployment.
        public bool EarlierRuns;
        // How many earlier runs the last List left out.
        public int Earlier;

        // The SMS Provider can compare the time in a WHERE as local time on the site, so each query asks for this much more
        // and the rows are cut into runs here.
        public static readonly TimeSpan Margin = TimeSpan.FromHours(14);

        public List<RemoteDeployment> List(DateTime sinceUtc)
        {
            const string select = "SELECT ResourceID, AdvertisementID, PackageID, ExecutionTime, LastStatusMsgID, LastStatusMsgName, ExitCode, Step, ActionName FROM SMS_TaskSequenceExecutionStatus WHERE ExecutionTime >= '";
            DateTime from = sinceUtc - Margin;
            List<Dictionary<string, object>> rows;
            try { rows = q.Query(select + Dmtf(from) + "'"); }
            catch (InvalidOperationException)
            {
                // Some providers only take a time with an offset. The one that works is kept for the device view.
                if (Dmtf != SccmWmi.Dmtf) throw;
                rows = q.Query(select + SccmWmi.DmtfZero(from) + "'");
                Dmtf = SccmWmi.DmtfZero;
            }
            Dictionary<string, List<Dictionary<string, object>>> groups = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> r in rows)
            {
                string k = Int(r, "ResourceID") + "|" + Str(r, "AdvertisementID");
                List<Dictionary<string, object>> l;
                if (!groups.TryGetValue(k, out l)) { l = new List<Dictionary<string, object>>(); groups[k] = l; }
                l.Add(r);
            }
            List<long> ids = new List<long>();
            List<string> pkgs = new List<string>();
            foreach (List<Dictionary<string, object>> l in groups.Values)
            {
                long id = Int(l[0], "ResourceID");
                string pkg = Str(l[0], "PackageID");
                if (!ids.Contains(id)) ids.Add(id);
                if (pkg != null && !pkgs.Contains(pkg)) pkgs.Add(pkg);
            }
            Dictionary<long, string> names = Names(ids);
            Dictionary<string, string> tsNames = TaskSequenceNames(pkgs);
            List<RemoteDeployment> list = new List<RemoteDeployment>();
            DateTime now = DateTime.UtcNow;
            int earlier = 0;
            foreach (KeyValuePair<string, List<Dictionary<string, object>>> g in groups)
            {
                List<List<Dictionary<string, object>>> runs = Runs(g.Value);
                List<RemoteDeployment> shown = new List<RemoteDeployment>();
                for (int i = 0; i < runs.Count; i++)
                {
                    DateTime? next = i + 1 < runs.Count ? Time(runs[i + 1][0]) : null;
                    RemoteDeployment d = Summarize(g.Key + "|" + RunTicks(runs[i]), runs[i], now, next);
                    // Runs that only fall in the extra margin are not in the period asked for.
                    if (d.LastUtc < sinceUtc) continue;
                    long id = Int(g.Value[0], "ResourceID");
                    string n;
                    d.Device = names.TryGetValue(id, out n) ? n : "Resource " + id;
                    string pkg = Str(g.Value[0], "PackageID");
                    d.Profile = pkg != null && tsNames.TryGetValue(pkg, out n) ? n : pkg;
                    d.User = Str(g.Value[0], "AdvertisementID");
                    shown.Add(d);
                }
                if (shown.Count == 0) continue;
                if (EarlierRuns) list.AddRange(shown);
                else list.Add(shown[shown.Count - 1]);
                earlier += shown.Count - 1;
            }
            Earlier = earlier;
            list.Sort(IntuneCatalog.Order);
            return list;
        }

        static string RunTicks(List<Dictionary<string, object>> run)
        {
            DateTime? t = Time(run[0]);
            return (t.HasValue ? t.Value.Ticks : 0).ToString(CultureInfo.InvariantCulture);
        }

        // Cuts the rows of one device and deployment into runs. Rows before the first start are a run of their own.
        // A later start (11140) only begins a new run when the run before has ended, when the steps after it start again
        // from the beginning, or after more than 4 hours of silence. Otherwise it is the task sequence resuming after a restart.
        public static List<List<Dictionary<string, object>>> Runs(List<Dictionary<string, object>> rows)
        {
            SortRows(rows);
            List<List<Dictionary<string, object>>> runs = new List<List<Dictionary<string, object>>>();
            List<Dictionary<string, object>> cur = null;
            bool started = false, ended = false;
            long top = 0;
            DateTime? last = null;
            for (int i = 0; i < rows.Count; i++)
            {
                Dictionary<string, object> r = rows[i];
                int kind = TsMessage.Of(r).Kind;
                bool start = kind == TsMessage.TsStarted && IsStart(r);
                DateTime? t = Time(r);
                if (cur == null || (start && (!started || ended || Gap(last, t) || FirstStep(rows, i) < top)))
                {
                    cur = new List<Dictionary<string, object>>();
                    runs.Add(cur);
                    started = start;
                    ended = false;
                    top = 0;
                }
                cur.Add(r);
                if (kind == TsMessage.TsDone || kind == TsMessage.TsFailed) ended = true;
                if (IsStepRow(kind)) top = Math.Max(top, Int(r, "Step"));
                if (t.HasValue) last = t;
            }
            return runs;
        }

        public static readonly TimeSpan RunGap = TimeSpan.FromHours(4);

        static bool Gap(DateTime? last, DateTime? t)
        {
            return last.HasValue && t.HasValue && t.Value - last.Value > RunGap;
        }

        static bool IsStepRow(int kind)
        {
            return kind == TsMessage.StepStarted || kind == TsMessage.StepDone || kind == TsMessage.StepFailed || kind == TsMessage.StepSkipped || kind == TsMessage.GroupEvent;
        }

        // The first step number above 0 reported after the start at index i, or long.MaxValue when none has reported yet.
        // Step 0 is left out: the start itself, the first group and messages without a step all carry it.
        static long FirstStep(List<Dictionary<string, object>> rows, int i)
        {
            for (int j = i + 1; j < rows.Count; j++)
            {
                int kind = TsMessage.Of(rows[j]).Kind;
                if (kind == TsMessage.TsStarted) break;
                long step = Int(rows[j], "Step");
                if (IsStepRow(kind) && step > 0) return step;
            }
            return long.MaxValue;
        }

        // The engine starting a task sequence, not resuming one.
        static bool IsStart(Dictionary<string, object> r)
        {
            return (Str(r, "LastStatusMsgName") ?? "").IndexOf("resumed", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // The run that holds the given time: the last one that began at or before it.
        public static List<Dictionary<string, object>> RunAt(List<List<Dictionary<string, object>>> runs, DateTime? atUtc)
        {
            if (runs.Count == 0) return new List<Dictionary<string, object>>();
            if (!atUtc.HasValue) return runs[runs.Count - 1];
            List<Dictionary<string, object>> pick = runs[0];
            foreach (List<Dictionary<string, object>> run in runs)
            {
                DateTime? t = Time(run[0]);
                if (t.HasValue && t.Value <= atUtc.Value) pick = run;
            }
            return pick;
        }

        public static RemoteDeployment Summarize(string key, List<Dictionary<string, object>> rows, DateTime nowUtc)
        {
            return Summarize(key, rows, nowUtc, null);
        }

        // The state of one run from its status messages: done, failed, running, or silent for too long.
        // A run that never reported its end before the next run started has no result.
        public static RemoteDeployment Summarize(string key, List<Dictionary<string, object>> rows, DateTime nowUtc, DateTime? nextStartUtc)
        {
            RemoteDeployment d = new RemoteDeployment();
            d.Key = key;
            SortRows(rows);
            Dictionary<string, object> lastRow = rows[rows.Count - 1];
            d.StartUtc = Time(rows[0]);
            d.LastUtc = Time(lastRow) ?? nowUtc;
            State st = State.Installing;
            string text = "Running";
            foreach (Dictionary<string, object> r in rows)
            {
                TsMessage m = TsMessage.Of(r);
                if (m.Kind == TsMessage.TsDone) { st = State.Succeeded; text = "Completed"; d.EndUtc = Time(r); }
                else if (m.Kind == TsMessage.TsFailed) { st = State.Failed; text = "Failed"; d.EndUtc = Time(r); d.Detail = Str(r, "LastStatusMsgName"); }
                else if (m.Kind == TsMessage.TsStarted) { st = State.Installing; text = "Running"; d.EndUtc = null; d.Detail = null; }
            }
            if (st == State.Installing && nextStartUtc.HasValue)
            {
                st = State.Unknown;
                text = "No result before the next run, " + Txt.Local(nextStartUtc.Value).Substring(0, 16);
                d.EndUtc = d.LastUtc;
            }
            else if (st == State.Installing)
            {
                string action = null;
                for (int i = rows.Count - 1; i >= 0 && string.IsNullOrEmpty(action); i--) action = Str(rows[i], "ActionName");
                text = string.IsNullOrEmpty(action) ? "Running" : "Running, last step: " + action;
                if (nowUtc - d.LastUtc > TimeSpan.FromHours(4)) { st = State.Unknown; text = "No status since " + Txt.Local(d.LastUtc); }
            }
            d.State = st;
            d.StateText = text;
            return d;
        }

        public IRemoteFeed Open(RemoteDeployment d)
        {
            string[] k = d.Key.Split('|');
            long ticks = 0;
            DateTime? run = null;
            if (k.Length > 2 && long.TryParse(k[2], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks > 0) run = new DateTime(ticks, DateTimeKind.Utc);
            else if (d.StartUtc.HasValue) run = d.StartUtc;
            return new SccmFeed(q, long.Parse(k[0], CultureInfo.InvariantCulture), k.Length > 1 ? k[1] : null, run, d.Device, d.Profile, Dmtf);
        }

        Dictionary<long, string> Names(List<long> ids)
        {
            Dictionary<long, string> map = new Dictionary<long, string>();
            for (int i = 0; i < ids.Count; i += 40)
            {
                StringBuilder w = new StringBuilder();
                for (int k = i; k < Math.Min(ids.Count, i + 40); k++) w.Append(w.Length > 0 ? " OR " : "").Append("ResourceID = ").Append(ids[k].ToString(CultureInfo.InvariantCulture));
                try
                {
                    foreach (Dictionary<string, object> r in q.Query("SELECT ResourceID, Name FROM SMS_R_System WHERE " + w)) map[Int(r, "ResourceID")] = Str(r, "Name");
                }
                catch (Exception) { }
            }
            return map;
        }

        Dictionary<string, string> TaskSequenceNames(List<string> pkgs)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pkgs.Count; i += 40)
            {
                StringBuilder w = new StringBuilder();
                for (int k = i; k < Math.Min(pkgs.Count, i + 40); k++) w.Append(w.Length > 0 ? " OR " : "").Append("PackageID = '").Append(pkgs[k].Replace("'", "")).Append("'");
                try
                {
                    foreach (Dictionary<string, object> r in q.Query("SELECT PackageID, Name FROM SMS_TaskSequencePackage WHERE " + w)) map[Str(r, "PackageID")] = Str(r, "Name");
                }
                catch (Exception) { }
            }
            return map;
        }

        // In time order. Within the same second the start of the task sequence comes first and its end last,
        // as the start (11140) and the first group (11124) are both step 0 at the same time.
        public static int RowOrder(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            int c = (Time(a) ?? DateTime.MinValue).CompareTo(Time(b) ?? DateTime.MinValue);
            if (c != 0) return c;
            c = Rank(a).CompareTo(Rank(b));
            return c != 0 ? c : Int(a, "Step").CompareTo(Int(b, "Step"));
        }

        static int Rank(Dictionary<string, object> r)
        {
            int k = TsMessage.Of(r).Kind;
            return k == TsMessage.TsStarted ? 0 : k == TsMessage.TsDone || k == TsMessage.TsFailed ? 2 : 1;
        }

        // Sorts with RowOrder and keeps rows that compare equal in the order the provider gave them.
        public static void SortRows(List<Dictionary<string, object>> rows)
        {
            List<KeyValuePair<int, Dictionary<string, object>>> l = new List<KeyValuePair<int, Dictionary<string, object>>>();
            for (int i = 0; i < rows.Count; i++) l.Add(new KeyValuePair<int, Dictionary<string, object>>(i, rows[i]));
            l.Sort(delegate (KeyValuePair<int, Dictionary<string, object>> a, KeyValuePair<int, Dictionary<string, object>> b)
            {
                int c = RowOrder(a.Value, b.Value);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            rows.Clear();
            foreach (KeyValuePair<int, Dictionary<string, object>> p in l) rows.Add(p.Value);
        }

        public static DateTime? Time(Dictionary<string, object> r)
        {
            object v;
            if (!r.TryGetValue("ExecutionTime", out v) || !(v is DateTime)) return null;
            return DateTime.SpecifyKind((DateTime)v, DateTimeKind.Utc);
        }

        public static string Str(Dictionary<string, object> r, string k)
        {
            object v;
            return r.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        public static long Int(Dictionary<string, object> r, string k)
        {
            object v;
            if (!r.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }
    }

    // What a task sequence status message says, from its ID and its name. The IDs below 11143 vary between
    // versions, so the name decides; 11143 is the task sequence completing.
    public sealed class TsMessage
    {
        public const int Other = 0, StepStarted = 1, StepDone = 2, StepFailed = 3, StepSkipped = 4, TsStarted = 5, TsDone = 6, TsFailed = 7, Reboot = 8, GroupEvent = 9, BypassWarning = 10, AppInstalled = 11, AppFailed = 12;
        // A warning from Task Sequence Action, sent by a Run PowerShell Script step with execution policy Bypass just before it runs.
        public const long Bypass = 11910;
        public int Kind;

        public static TsMessage Of(Dictionary<string, object> r)
        {
            TsMessage m = new TsMessage();
            long id = SccmCatalog.Int(r, "LastStatusMsgID");
            string n = (SccmCatalog.Str(r, "LastStatusMsgName") ?? "").ToLowerInvariant();
            bool action = !string.IsNullOrEmpty(SccmCatalog.Str(r, "ActionName")) || n.Contains("action") || n.Contains("instruction");
            if (id == 11143 || n.Contains("completed the task sequence") || n.Contains("successfully completed a task sequence")) m.Kind = TsDone;
            else if (id == Bypass) m.Kind = BypassWarning;
            // An application from a task sequence variable list. The text names the group of the step, so it comes before the group test.
            else if (id == SccmApps.Installed || (n.Contains("installed application") && n.Contains("ts variable"))) m.Kind = AppInstalled;
            else if (id == SccmApps.Failed || (n.Contains("failed to install application") && n.Contains("ts variable"))) m.Kind = AppFailed;
            else if (n.Contains("reboot") || n.Contains("restart")) m.Kind = Reboot;
            // Before the task sequence itself: "The task sequence execution engine started a group" (11124) is not a start.
            else if (n.Contains("group")) m.Kind = GroupEvent;
            else if (!action && n.Contains("task sequence") && (n.Contains("failed") || n.Contains("aborted"))) m.Kind = TsFailed;
            else if (!action && n.Contains("task sequence") && (n.Contains("started") || n.Contains("resumed"))) m.Kind = TsStarted;
            else if (n.Contains("skipped")) m.Kind = StepSkipped;
            else if (n.Contains("failed")) m.Kind = StepFailed;
            else if (n.Contains("successfully completed") || n.Contains("completed an action") || n.Contains("completed the action")) m.Kind = StepDone;
            else if (n.Contains("started") || n.Contains("executing") || n.Contains("start executing")) m.Kind = StepStarted;
            return m;
        }
    }

    public sealed class SccmFeed : IRemoteFeed
    {
        readonly ISccmQuery q;
        readonly long resourceId;
        readonly string deployment, device, tsName;
        readonly Func<DateTime, string> dmtf;
        readonly DateTime? runUtc;
        readonly HashSet<string> seen = new HashSet<string>();
        bool first = true;

        // Follows the run that began at runStartUtc, up to the next start on the same device and deployment.
        public SccmFeed(ISccmQuery query, long resource, string advertisement, DateTime? runStartUtc, string deviceName, string taskSequence, Func<DateTime, string> toDmtf)
        {
            q = query;
            resourceId = resource;
            deployment = advertisement;
            runUtc = runStartUtc;
            device = deviceName;
            tsName = taskSequence;
            dmtf = toDmtf;
        }

        public string Mode
        {
            get { return Modes.Sccm; }
        }

        public int PollMs
        {
            get { return 15000; }
        }

        public Batch Read(DateTime nowUtc)
        {
            string wql = "SELECT * FROM SMS_TaskSequenceExecutionStatus WHERE ResourceID = " + resourceId.ToString(CultureInfo.InvariantCulture)
                + (deployment != null ? " AND AdvertisementID = '" + deployment.Replace("'", "") + "'" : "")
                + (runUtc.HasValue ? " AND ExecutionTime >= '" + dmtf(runUtc.Value - SccmCatalog.Margin) + "'" : "");
            // The whole run is read each time: a device sends the status messages it kept during Windows PE and restarts later,
            // with the time of each step, so reading only what is newer than the last row would miss them.
            List<List<Dictionary<string, object>>> runs = SccmCatalog.Runs(q.Query(wql));
            List<Dictionary<string, object>> rows = SccmCatalog.RunAt(runs, runUtc);
            int at = runs.IndexOf(rows);
            NewerRunUtc = at >= 0 && at < runs.Count - 1 ? SccmCatalog.Time(runs[runs.Count - 1][0]) : null;
            Batch b = new Batch();
            if (first)
            {
                b.Info.Add(new Pair("Source", "SCCM status messages"));
                b.Info.Add(new Pair("Device", device));
                if (!string.IsNullOrEmpty(tsName)) b.Info.Add(new Pair("Task sequence", tsName));
                if (!string.IsNullOrEmpty(deployment)) b.Info.Add(new Pair("Deployment ID", deployment));
                string siteUser = SiteUser.Of(q, resourceId);
                if (siteUser != null) b.Info.Add(new Pair(DeviceFacts.SiteUser, siteUser));
                b.Notes.Add("This task sequence is read from the status messages it sends to the site. They arrive a little after each step, and only steps that report are shown. Run TrackVice on the device to see smsts.log line by line.");
                first = false;
            }
            Map(rows, seen, b);
            return b;
        }

        // When the newest run of the same device and deployment began, if it is newer than the run followed.
        public DateTime? NewerRunUtc;

        // The newest run, to follow it in place of this one.
        public RemoteDeployment NewerRun()
        {
            if (!NewerRunUtc.HasValue) return null;
            RemoteDeployment d = new RemoteDeployment();
            d.Key = resourceId.ToString(CultureInfo.InvariantCulture) + "|" + deployment + "|" + NewerRunUtc.Value.Ticks.ToString(CultureInfo.InvariantCulture);
            d.Device = device;
            d.Profile = tsName;
            d.User = deployment;
            d.StartUtc = NewerRunUtc;
            d.LastUtc = NewerRunUtc.Value;
            d.State = State.Installing;
            return d;
        }

        public SccmFeed ForRun(DateTime startUtc)
        {
            return new SccmFeed(q, resourceId, deployment, startUtc, device, tsName, dmtf);
        }

        public const string RunKey = "ts:run";

        static Obs Run(DateTime t, State st, long? code, string text)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "Status message";
            o.ItemKey = RunKey;
            o.ItemName = "Whole task sequence";
            o.ItemKind = Kinds.Step;
            o.Origin = Origins.Sccm;
            o.PhaseKey = Phases.TaskSequence;
            o.State = st;
            o.ExitCode = code;
            o.Text = text;
            if (st == State.Failed) o.Severity = 2;
            return o;
        }

        // Turns status message rows into observations. Rows already seen are skipped. Shared with the demo and the tests.
        public static void Map(List<Dictionary<string, object>> rows, HashSet<string> seen, Batch b)
        {
            bool fresh = seen.Count == 0;
            Dictionary<Dictionary<string, object>, Dictionary<string, object>> attach = SccmLoose.Match(rows);
            bool ended = false;
            DateTime? lastTime = null;
            foreach (Dictionary<string, object> r in rows)
            {
                int k = TsMessage.Of(r).Kind;
                if (k == TsMessage.TsDone || k == TsMessage.TsFailed) ended = true;
                DateTime? rt = SccmCatalog.Time(r);
                if (rt.HasValue && (!lastTime.HasValue || rt.Value > lastTime.Value)) lastTime = rt;
            }
            foreach (Dictionary<string, object> r in rows)
            {
                DateTime? t = SccmCatalog.Time(r);
                if (fresh)
                {
                    // Status messages only arrive after a step, so one item stands for the run itself: it shows as running until the task sequence reports its end.
                    fresh = false;
                    b.Obs.Add(Run(t ?? DateTime.UtcNow, State.Installing, null, "The task sequence is running"));
                }
                string action = SccmCatalog.Str(r, "ActionName");
                long step = SccmCatalog.Int(r, "Step");
                long msg = SccmCatalog.Int(r, "LastStatusMsgID");
                string msgName = SccmCatalog.Str(r, "LastStatusMsgName") ?? ("Status message " + msg);
                string sig = (t.HasValue ? t.Value.Ticks : 0) + "|" + step + "|" + msg + "|" + action;
                if (SccmApps.Is(TsMessage.Of(r).Kind))
                {
                    if (!seen.Add(sig + "|" + SccmCatalog.Str(r, "ActionOutput"))) continue;
                    SccmApps.Add(rows, r, ended, b);
                    continue;
                }
                if (SccmLoose.Is(r))
                {
                    // A message without a step or group is kept until the step with its action name reports, so it can join that step.
                    Dictionary<string, object> to;
                    attach.TryGetValue(r, out to);
                    bool late = t.HasValue && lastTime.HasValue && lastTime.Value - t.Value > SccmCatalog.RunGap;
                    if (to == null && !ended && !late) continue;
                    if (!seen.Add(sig)) continue;
                    b.Obs.Add(SccmLoose.Obs(r, to, t ?? DateTime.UtcNow));
                    continue;
                }
                if (!seen.Add(sig)) continue;
                long exit = SccmCatalog.Int(r, "ExitCode");
                long? code = exit == 0 ? (long?)null : unchecked((int)(uint)exit);
                string output = SccmCatalog.Str(r, "ActionOutput");
                string group = SccmCatalog.Str(r, "GroupName");
                TsMessage m = TsMessage.Of(r);
                Obs o = new Obs();
                o.TimeUtc = t ?? DateTime.UtcNow;
                o.Source = "Status message";
                o.Text = msgName + (code.HasValue ? " (" + Errors.Short(code) + ")" : "");
                o.Evidence = new Evidence(EvidenceKinds.StatusMessage, "SMS_TaskSequenceExecutionStatus, message " + msg, (int)Math.Min(int.MaxValue, msg), msgName + (string.IsNullOrEmpty(output) ? "" : "\r\n" + output));
                switch (m.Kind)
                {
                    case TsMessage.TsDone:
                        o.AddSessionInfo("Task sequence result", "Succeeded");
                        o.AddSessionInfo("Task sequence ended", o.TimeUtc.ToString("o", CultureInfo.InvariantCulture));
                        b.Obs.Add(o);
                        b.Obs.Add(Run(o.TimeUtc, State.Succeeded, null, "The task sequence completed"));
                        continue;
                    case TsMessage.TsFailed:
                        o.Severity = 2;
                        o.AddSessionInfo("Task sequence result", "Failed" + (code.HasValue ? ": " + Errors.Describe(code.Value) : ""));
                        o.AddSessionInfo("Task sequence ended", o.TimeUtc.ToString("o", CultureInfo.InvariantCulture));
                        b.Obs.Add(o);
                        b.Obs.Add(Run(o.TimeUtc, State.Failed, code, "The task sequence failed"));
                        continue;
                    case TsMessage.TsStarted:
                    case TsMessage.Reboot:
                    case TsMessage.GroupEvent:
                        if (m.Kind == TsMessage.Reboot) o.Severity = 1;
                        b.Obs.Add(o);
                        if (string.IsNullOrEmpty(action)) continue;
                        break;
                }
                if (string.IsNullOrEmpty(action)) { b.Obs.Add(o); continue; }
                // The same key as smsts.log uses, so a saved session from the device and this view look alike.
                o.ItemKey = "ts:" + step.ToString(CultureInfo.InvariantCulture) + ":" + action;
                o.ItemName = action;
                o.ItemKind = Kinds.Step;
                o.Origin = Origins.Sccm;
                if (!string.IsNullOrEmpty(group))
                {
                    o.PhaseKey = "ts:group:" + group.ToLowerInvariant();
                    o.PhaseName = group;
                    o.PhaseOrder = 100 + (int)Math.Min(10000, step);
                }
                else o.PhaseKey = Phases.TaskSequence;
                o.AddInfo("Step", step.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(group)) o.AddInfo("Group", group);
                if (!string.IsNullOrEmpty(output)) o.AddInfo("Output", Txt.Short(output, 2000));
                if (m.Kind == TsMessage.BypassWarning) SccmLoose.MarkBypass(o, msgName);
                switch (m.Kind)
                {
                    case TsMessage.StepStarted: o.State = State.Installing; o.NewAttempt = true; break;
                    case TsMessage.StepDone:
                        o.State = State.Succeeded;
                        o.ExitCode = exit;
                        if (exit == 3010 || exit == 1641) o.Reboot = true;
                        break;
                    case TsMessage.StepFailed: o.State = State.Failed; o.ExitCode = code; o.Severity = 2; break;
                    case TsMessage.StepSkipped: o.State = State.Skipped; break;
                }
                if (m.Kind != TsMessage.TsStarted && m.Kind != TsMessage.Reboot && m.Kind != TsMessage.GroupEvent) b.Obs.Add(o);
            }
        }
    }
}
