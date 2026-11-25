namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    // A name with a number of seconds, for the phases and items of one finished deployment.
    [DataContract(Name = "Value", Namespace = "")]
    public sealed class HistoryValue
    {
        [DataMember(Order = 1)] public string Key;
        [DataMember(Order = 2)] public double Seconds;
    }

    // What TrackVice keeps of one finished deployment: how long it took in all, per phase and per item, and its restarts.
    [DataContract(Name = "Run", Namespace = "")]
    public sealed class HistoryRun
    {
        [DataMember(Order = 1)] public string Id;
        [DataMember(Order = 2)] public string Customer;
        [DataMember(Order = 3)] public string Kind;
        [DataMember(Order = 4)] public string Profile;
        [DataMember(Order = 5)] public DateTime EndUtc;
        // From the start of the deployment to the end of its last item, without the time the device was off.
        [DataMember(Order = 6)] public double Total;
        [DataMember(Order = 7)] public int Restarts;
        // The average time one restart took, 0 when none.
        [DataMember(Order = 8)] public double RestartSeconds;
        [DataMember(Order = 9)] public List<HistoryValue> Phases = new List<HistoryValue>();
        [DataMember(Order = 10)] public List<HistoryValue> Items = new List<HistoryValue>();
    }

    [DataContract(Name = "Key", Namespace = "")]
    public sealed class HistoryKeyDoc
    {
        [DataMember(Order = 1)] public string Key;
        [DataMember(Order = 2)] public List<HistoryRun> Runs = new List<HistoryRun>();
    }

    [DataContract(Name = "DeploymentHistory", Namespace = "")]
    public sealed class HistoryDoc
    {
        [DataMember(Order = 1)] public string Kind = DeploymentHistory.DocKind;
        [DataMember(Order = 2)] public int Version = 1;
        [DataMember(Order = 3)] public List<HistoryKeyDoc> Keys = new List<HistoryKeyDoc>();
    }

    // What the earlier deployments of one kind say, by their middle values and their spread.
    public sealed class HistoryStats
    {
        public int Count;
        // The profile or task sequence these deployments are of, or null when they are all of the customer and kind.
        public string Profile;
        // The middle total, and the 20th and 80th percentile of the totals, in seconds.
        public double Total, Low, High;
        // The middle restart count of a deployment and the middle time of one restart.
        public double Restarts, RestartSeconds;
        public readonly Dictionary<string, double> Phases = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> Items = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Three or more deployments tell something about the spread; one or two only about the middle.
        public bool HasSpread
        {
            get { return Count >= DeploymentHistory.MinForSpread; }
        }
    }

    // The finished deployments TrackVice has seen, per customer and kind of deployment (the Autopilot profile or the task
    // sequence when known). From them the time left and the overall percentage get their expected times. Kept in
    // %LOCALAPPDATA%\TrackVice\history.json, the newest 20 per key.
    public sealed class DeploymentHistory
    {
        public const string DocKind = "TrackVice deployment history";
        public const int MaxPerKey = 20;
        // This many earlier deployments of the exact profile are needed before only they are used; else the kind of the customer.
        public const int MinForProfile = 3;
        public const int MinForSpread = 3;
        // Deployments with outliers are only trimmed when there are enough to tell.
        public const int MinForTrim = 5;
        // A deployment that was off for longer than this, or for more than it ran, is not a typical one.
        public static readonly TimeSpan MaxOff = TimeSpan.FromHours(1);
        public static readonly TimeSpan MinTotal = TimeSpan.FromMinutes(2), MaxTotal = TimeSpan.FromHours(24);
        const int MaxRestartSeconds = 900;

        readonly object gate = new object();
        readonly Dictionary<string, List<HistoryRun>> byKey = new Dictionary<string, List<HistoryRun>>(StringComparer.OrdinalIgnoreCase);
        public bool Dirty;
        // The history of this PC, once the window has loaded it, so Overview uses the same one.
        public static DeploymentHistory Shared;

        public static string DefaultPath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"), "history.json"); }
        }

        public int Count
        {
            get
            {
                lock (gate)
                {
                    int n = 0;
                    foreach (List<HistoryRun> l in byKey.Values) n += l.Count;
                    return n;
                }
            }
        }

        public static string KeyOf(string customer, string kind, string profile)
        {
            return (customer ?? "").Trim() + "|" + (kind ?? "").Trim() + "|" + (profile ?? "").Trim();
        }

        static string Prefix(string customer, string kind)
        {
            return (customer ?? "").Trim() + "|" + (kind ?? "").Trim() + "|";
        }

        static string ProfileOf(Session s)
        {
            return s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
        }

        // ---------- measuring a finished deployment ----------

        static bool Timed(Item i)
        {
            if (i.PhaseKey == Phases.Restarts || i.Kind == Kinds.Restart) return false;
            return i.PhaseKey != Phases.Policies && i.Kind != Kinds.Policy && i.Kind != Kinds.Profile && i.Key != SccmFeed.RunKey;
        }

        static bool IsRestart(Item i)
        {
            return i.Kind == Kinds.Restart || i.PhaseKey == Phases.Restarts;
        }

        // The numbers of a deployment that finished well, or null when it is not one to learn from: it failed, still runs, is
        // too short or too long, or the device was off for a long time in it.
        public static HistoryRun Measure(Session s, string customer, DateTime nowUtc)
        {
            if (s == null || s.Items.Count == 0) return null;
            if ((s.GetInfo("Task sequence result") ?? "").StartsWith("Failed", StringComparison.Ordinal)) return null;
            foreach (Item i in s.Items)
            {
                if (i.State == State.Failed || States.IsActive(i.State)) return null;
                if (!States.IsComplete(i.State)) return null;
            }
            DateTime? a = EnrollmentAnchor.Of(s) ?? s.FirstUtc;
            if (!a.HasValue) return null;
            DateTime start = a.Value, end = DateTime.MinValue;
            foreach (Item i in s.Items)
            {
                if (!Timed(i) || !States.IsComplete(i.State)) continue;
                DateTime e = i.EndUtc ?? i.LastUtc;
                if (e.Year > 2000 && e > end) end = e;
            }
            if (end <= start) return null;
            TimeSpan wall = end - start;
            TimeSpan active = OffTime.Active(s, start, end);
            TimeSpan off = wall - active;
            if (off > MaxOff || off > active) return null;
            if (active < MinTotal || active > MaxTotal) return null;

            HistoryRun r = new HistoryRun();
            r.Id = (s.Computer ?? "") + "|" + start.Ticks.ToString(CultureInfo.InvariantCulture);
            r.Customer = (customer ?? "").Trim();
            r.Kind = s.Kind;
            r.Profile = ProfileOf(s);
            r.EndUtc = end;
            r.Total = Math.Round(active.TotalSeconds);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double restartSum = 0;
            int restartTimed = 0;
            foreach (Item i in s.Items)
            {
                if (i.State == State.Skipped || i.State == State.NotApplicable) continue;
                if (IsRestart(i))
                {
                    r.Restarts++;
                    TimeSpan? rd = i.Duration(end);
                    if (rd.HasValue && rd.Value.TotalSeconds >= 1 && rd.Value.TotalSeconds <= MaxRestartSeconds)
                    {
                        restartSum += rd.Value.TotalSeconds;
                        restartTimed++;
                    }
                    continue;
                }
                if (!Timed(i)) continue;
                TimeSpan? d = i.Duration(end);
                if (!d.HasValue || d.Value.TotalSeconds < 1) continue;
                if (seen.Add(i.Kind + "|" + i.DisplayName)) r.Items.Add(new HistoryValue { Key = i.Kind + "|" + i.DisplayName, Seconds = Math.Round(d.Value.TotalSeconds) });
            }
            if (restartTimed > 0) r.RestartSeconds = Math.Round(restartSum / restartTimed);
            foreach (Phase p in s.Phases)
            {
                if (p.Key == Phases.Restarts || p.Key == Phases.Policies || p.Items.Count == 0) continue;
                TimeSpan? pd = p.Duration(end);
                if (pd.HasValue && pd.Value.TotalSeconds >= 1) r.Phases.Add(new HistoryValue { Key = p.Key, Seconds = Math.Round(pd.Value.TotalSeconds) });
            }
            return r;
        }

        // The numbers of the reference enrollment of a customer. Null when it is too small or too short.
        public static HistoryRun Measure(Reference r, string customer)
        {
            if (r == null || r.Items.Count < 3) return null;
            double end = 0;
            int restarts = 0;
            double restartSum = 0;
            Dictionary<string, double[]> phases = new Dictionary<string, double[]>();
            HistoryRun h = new HistoryRun();
            foreach (ReferenceItem x in r.Items)
            {
                State st = States.Parse(x.State);
                if (st == State.Failed) return null;
                bool restart = x.Kind == Kinds.Restart || x.Phase == Phases.Restarts;
                if (restart)
                {
                    if (st == State.Skipped) continue;
                    restarts++;
                    if (x.Seconds.HasValue && x.Seconds.Value >= 1 && x.Seconds.Value <= MaxRestartSeconds) restartSum += x.Seconds.Value;
                    continue;
                }
                if (x.Phase == Phases.Policies || x.Kind == Kinds.Policy || x.Kind == Kinds.Profile) continue;
                if (st != State.Succeeded && st != State.WaitingReboot) continue;
                if (!x.Offset.HasValue) continue;
                double e = x.Offset.Value + (x.Seconds ?? 0);
                end = Math.Max(end, e);
                string name = string.IsNullOrEmpty(x.Name) ? x.Key : x.Name;
                if (x.Seconds.HasValue && x.Seconds.Value >= 1 && !string.IsNullOrEmpty(name))
                    h.Items.Add(new HistoryValue { Key = x.Kind + "|" + name, Seconds = Math.Round(x.Seconds.Value) });
                if (!string.IsNullOrEmpty(x.Phase))
                {
                    double[] p;
                    if (!phases.TryGetValue(x.Phase, out p)) phases[x.Phase] = p = new double[] { x.Offset.Value, e };
                    else { p[0] = Math.Min(p[0], x.Offset.Value); p[1] = Math.Max(p[1], e); }
                }
            }
            if (end < MinTotal.TotalSeconds || end > MaxTotal.TotalSeconds) return null;
            h.Id = "ref|" + (r.Computer ?? "") + "|" + r.StartUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            h.Customer = (customer ?? r.Customer ?? "").Trim();
            h.Kind = r.Deployment;
            h.Profile = r.Profile;
            h.EndUtc = r.StartUtc.AddSeconds(end);
            h.Total = Math.Round(end);
            h.Restarts = restarts;
            h.RestartSeconds = restarts > 0 && restartSum > 0 ? Math.Round(restartSum / restarts) : 0;
            foreach (KeyValuePair<string, double[]> p in phases) h.Phases.Add(new HistoryValue { Key = p.Key, Seconds = Math.Round(p.Value[1] - p.Value[0]) });
            return h;
        }

        // ---------- keeping them ----------

        // Adds a deployment, or replaces the one with the same id. Keeps the newest MaxPerKey of its key. True when changed.
        public bool Add(HistoryRun r)
        {
            if (r == null || string.IsNullOrEmpty(r.Id) || r.Total <= 0 || string.IsNullOrEmpty(r.Kind)) return false;
            string key = KeyOf(r.Customer, r.Kind, r.Profile);
            lock (gate)
            {
                List<HistoryRun> l;
                if (!byKey.TryGetValue(key, out l)) byKey[key] = l = new List<HistoryRun>();
                foreach (HistoryRun x in l)
                    if (x.Id == r.Id) return false;
                l.Add(r);
                l.Sort(delegate (HistoryRun a, HistoryRun b) { return b.EndUtc.CompareTo(a.EndUtc); });
                if (l.Count > MaxPerKey) l.RemoveRange(MaxPerKey, l.Count - MaxPerKey);
                Dirty = true;
                return true;
            }
        }

        public bool Learn(Session s, string customer, DateTime nowUtc)
        {
            return Add(Measure(s, customer, nowUtc));
        }

        public bool Learn(Reference r, string customer)
        {
            return Add(Measure(r, customer));
        }

        // The finished, successful deployments Overview read. A replaced or failed one is not a typical deployment.
        public int Learn(IEnumerable<OverviewDevice> devices, DateTime nowUtc)
        {
            int n = 0;
            if (devices == null) return 0;
            foreach (OverviewDevice o in devices)
            {
                if (o == null || o.S == null || o.Replaced || o.HasFailed || !o.Finished(nowUtc)) continue;
                if (Learn(o.S, o.Customer, nowUtc)) n++;
            }
            return n;
        }

        // ---------- what they say ----------

        public static double Percentile(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            double pos = Math.Max(0, Math.Min(1, p)) * (sorted.Count - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        public static double Median(List<double> values)
        {
            List<double> l = new List<double>(values);
            l.Sort();
            return Percentile(l, 0.5);
        }

        // The deployments of this profile when there are enough, else all of the customer and kind. Empty when there are none.
        public List<HistoryRun> Select(string customer, string kind, string profile, out string used)
        {
            List<HistoryRun> runs = new List<HistoryRun>();
            used = null;
            lock (gate)
            {
                List<HistoryRun> exact;
                if (!string.IsNullOrEmpty(profile) && byKey.TryGetValue(KeyOf(customer, kind, profile), out exact) && exact.Count >= MinForProfile)
                {
                    runs.AddRange(exact);
                    used = profile;
                }
                else
                {
                    string pre = Prefix(customer, kind);
                    foreach (KeyValuePair<string, List<HistoryRun>> k in byKey)
                        if (k.Key.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) runs.AddRange(k.Value);
                    // Of one profile only, so the name still is that of the runs.
                    string only = null;
                    bool same = runs.Count > 0;
                    foreach (HistoryRun x in runs)
                    {
                        if (only == null) only = x.Profile ?? "";
                        else if (!string.Equals(only, x.Profile ?? "", StringComparison.OrdinalIgnoreCase)) same = false;
                    }
                    if (same && !string.IsNullOrEmpty(only)) used = only;
                }
            }
            return runs;
        }

        // The earlier deployments of this kind, newest first, for the apps they usually had.
        public List<HistoryRun> Recent(string customer, string kind, string profile)
        {
            string used;
            List<HistoryRun> runs = Select(customer, kind, profile, out used);
            runs.Sort(delegate (HistoryRun a, HistoryRun b) { return b.EndUtc.CompareTo(a.EndUtc); });
            return runs;
        }

        public HistoryStats Get(string customer, string kind, string profile)
        {
            string used;
            List<HistoryRun> runs = Select(customer, kind, profile, out used);
            if (runs.Count == 0) return null;
            return Stats(runs, used);
        }

        public static HistoryStats Stats(List<HistoryRun> runs, string profile)
        {
            runs = new List<HistoryRun>(runs);
            runs.Sort(delegate (HistoryRun a, HistoryRun b) { return b.EndUtc.CompareTo(a.EndUtc); });
            if (runs.Count > MaxPerKey) runs.RemoveRange(MaxPerKey, runs.Count - MaxPerKey);
            if (runs.Count >= MinForTrim)
            {
                List<double> t = new List<double>();
                foreach (HistoryRun r in runs) t.Add(r.Total);
                double m = Median(t);
                List<HistoryRun> keep = new List<HistoryRun>();
                foreach (HistoryRun r in runs) if (r.Total <= m * 3 && r.Total >= m / 3) keep.Add(r);
                if (keep.Count >= 3) runs = keep;
            }
            HistoryStats h = new HistoryStats();
            h.Count = runs.Count;
            h.Profile = profile;
            List<double> totals = new List<double>(), restarts = new List<double>(), restartSecs = new List<double>();
            Dictionary<string, List<double>> phases = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<double>> items = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            foreach (HistoryRun r in runs)
            {
                totals.Add(r.Total);
                restarts.Add(r.Restarts);
                if (r.RestartSeconds > 0) restartSecs.Add(r.RestartSeconds);
                foreach (HistoryValue v in r.Phases) Put(phases, v);
                foreach (HistoryValue v in r.Items) Put(items, v);
            }
            totals.Sort();
            h.Total = Percentile(totals, 0.5);
            h.High = Math.Max(h.Total, Percentile(totals, 0.8));
            h.Low = Math.Min(h.Total, Percentile(totals, 0.2));
            h.Restarts = Median(restarts);
            h.RestartSeconds = restartSecs.Count > 0 ? Median(restartSecs) : 0;
            foreach (KeyValuePair<string, List<double>> p in phases) h.Phases[p.Key] = Median(p.Value);
            foreach (KeyValuePair<string, List<double>> p in items) h.Items[p.Key] = Median(p.Value);
            return h;
        }

        static void Put(Dictionary<string, List<double>> d, HistoryValue v)
        {
            if (v == null || string.IsNullOrEmpty(v.Key) || v.Seconds <= 0) return;
            List<double> l;
            if (!d.TryGetValue(v.Key, out l)) d[v.Key] = l = new List<double>();
            l.Add(v.Seconds);
        }

        // ---------- the file ----------

        static DataContractJsonSerializer Serializer()
        {
            return new DataContractJsonSerializer(typeof(HistoryDoc));
        }

        public string ToJson()
        {
            HistoryDoc d = new HistoryDoc();
            lock (gate)
            {
                List<string> keys = new List<string>(byKey.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string k in keys)
                {
                    HistoryKeyDoc kd = new HistoryKeyDoc();
                    kd.Key = k;
                    kd.Runs.AddRange(byKey[k]);
                    d.Keys.Add(kd);
                }
            }
            using (MemoryStream ms = new MemoryStream())
            {
                Serializer().WriteObject(ms, d);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // An empty history for text that is not one.
        public static DeploymentHistory FromJson(string json)
        {
            DeploymentHistory h = new DeploymentHistory();
            if (string.IsNullOrWhiteSpace(json)) return h;
            try
            {
                HistoryDoc d;
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) d = (HistoryDoc)Serializer().ReadObject(ms);
                if (d == null || d.Kind != DocKind || d.Keys == null) return h;
                foreach (HistoryKeyDoc k in d.Keys)
                {
                    if (k == null || k.Runs == null) continue;
                    foreach (HistoryRun r in k.Runs)
                    {
                        if (r == null) continue;
                        if (r.Phases == null) r.Phases = new List<HistoryValue>();
                        if (r.Items == null) r.Items = new List<HistoryValue>();
                        h.Add(r);
                    }
                }
            }
            catch (Exception) { return new DeploymentHistory(); }
            h.Dirty = false;
            return h;
        }

        // Never throws: a missing, broken or unreadable file is an empty history.
        public static DeploymentHistory Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new DeploymentHistory();
                return FromJson(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception) { return new DeploymentHistory(); }
        }

        // Writes to a temporary file first, so a failed write keeps the old file. False when it could not be saved.
        public bool Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                Dirty = false;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
