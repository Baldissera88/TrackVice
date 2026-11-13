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
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery(wql), opt))
            using (ManagementObjectCollection c = s.Get())
            {
                foreach (ManagementBaseObject o in c)
                {
                    Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData p in o.Properties)
                    {
                        object v = p.Value;
                        if (v != null && p.Type == CimType.DateTime)
                        {
                            try { v = ManagementDateTimeConverter.ToDateTime((string)v).ToUniversalTime(); }
                            catch (Exception) { v = null; }
                        }
                        row[p.Name] = v;
                    }
                    rows.Add(row);
                    o.Dispose();
                }
            }
            return rows;
        }

        public static string Dmtf(DateTime utc)
        {
            return ManagementDateTimeConverter.ToDmtfDateTime(utc.ToLocalTime());
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

        public List<RemoteDeployment> List(DateTime sinceUtc)
        {
            List<Dictionary<string, object>> rows = q.Query("SELECT ResourceID, AdvertisementID, PackageID, ExecutionTime, LastStatusMsgID, LastStatusMsgName, ExitCode, Step, ActionName FROM SMS_TaskSequenceExecutionStatus WHERE ExecutionTime >= '" + Dmtf(sinceUtc) + "'");
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
            foreach (KeyValuePair<string, List<Dictionary<string, object>>> g in groups)
            {
                RemoteDeployment d = Summarize(g.Key, g.Value, now);
                long id = Int(g.Value[0], "ResourceID");
                string n;
                d.Device = names.TryGetValue(id, out n) ? n : "Resource " + id;
                string pkg = Str(g.Value[0], "PackageID");
                d.Profile = pkg != null && tsNames.TryGetValue(pkg, out n) ? n : pkg;
                d.User = Str(g.Value[0], "AdvertisementID");
                list.Add(d);
            }
            list.Sort(IntuneCatalog.Order);
            return list;
        }

        // The state of one run from its status messages: done, failed, running, or silent for too long.
        public static RemoteDeployment Summarize(string key, List<Dictionary<string, object>> rows, DateTime nowUtc)
        {
            RemoteDeployment d = new RemoteDeployment();
            d.Key = key;
            rows.Sort(RowOrder);
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
            if (st == State.Installing)
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
            return new SccmFeed(q, long.Parse(k[0], CultureInfo.InvariantCulture), k.Length > 1 ? k[1] : null, d.Device, d.Profile, Dmtf);
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

        public static int RowOrder(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            int c = (Time(a) ?? DateTime.MinValue).CompareTo(Time(b) ?? DateTime.MinValue);
            return c != 0 ? c : Int(a, "Step").CompareTo(Int(b, "Step"));
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
        public const int Other = 0, StepStarted = 1, StepDone = 2, StepFailed = 3, StepSkipped = 4, TsStarted = 5, TsDone = 6, TsFailed = 7, Reboot = 8, GroupEvent = 9;
        public int Kind;

        public static TsMessage Of(Dictionary<string, object> r)
        {
            TsMessage m = new TsMessage();
            long id = SccmCatalog.Int(r, "LastStatusMsgID");
            string n = (SccmCatalog.Str(r, "LastStatusMsgName") ?? "").ToLowerInvariant();
            bool action = !string.IsNullOrEmpty(SccmCatalog.Str(r, "ActionName")) || n.Contains("action") || n.Contains("instruction");
            if (id == 11143 || n.Contains("completed the task sequence") || n.Contains("successfully completed a task sequence")) m.Kind = TsDone;
            else if (n.Contains("reboot") || n.Contains("restart")) m.Kind = Reboot;
            else if (!action && n.Contains("task sequence") && (n.Contains("failed") || n.Contains("aborted"))) m.Kind = TsFailed;
            else if (!action && n.Contains("task sequence") && (n.Contains("started") || n.Contains("resumed"))) m.Kind = TsStarted;
            else if (n.Contains("group")) m.Kind = GroupEvent;
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
        DateTime after = DateTime.MinValue;
        readonly HashSet<string> seen = new HashSet<string>();
        bool first = true;

        public SccmFeed(ISccmQuery query, long resource, string advertisement, string deviceName, string taskSequence, Func<DateTime, string> toDmtf)
        {
            q = query;
            resourceId = resource;
            deployment = advertisement;
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
                + (after > DateTime.MinValue ? " AND ExecutionTime >= '" + dmtf(after.AddMinutes(-1)) + "'" : "");
            List<Dictionary<string, object>> rows = q.Query(wql);
            rows.Sort(SccmCatalog.RowOrder);
            Batch b = new Batch();
            if (first)
            {
                b.Info.Add(new Pair("Source", "SCCM status messages"));
                b.Info.Add(new Pair("Device", device));
                if (!string.IsNullOrEmpty(tsName)) b.Info.Add(new Pair("Task sequence", tsName));
                if (!string.IsNullOrEmpty(deployment)) b.Info.Add(new Pair("Deployment ID", deployment));
                b.Notes.Add("This task sequence is read from the status messages it sends to the site. They arrive a little after each step, and only steps that report are shown. Run TrackVice on the device to see smsts.log line by line.");
                first = false;
            }
            Map(rows, seen, b);
            foreach (Dictionary<string, object> r in rows)
            {
                DateTime? t = SccmCatalog.Time(r);
                if (t.HasValue && t.Value > after) after = t.Value;
            }
            return b;
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
