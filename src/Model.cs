namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    // One state for every source. Each reader maps its own codes to it and keeps the original code on the event.
    public enum State
    {
        Unknown = 0,
        Pending = 1,
        Downloading = 2,
        Installing = 3,
        Detecting = 4,
        WaitingReboot = 5,
        Succeeded = 6,
        Failed = 7,
        Skipped = 8,
        NotApplicable = 9,
    }

    public static class States
    {
        public static string Label(State s)
        {
            switch (s)
            {
                case State.Pending: return "Waiting";
                case State.Downloading: return "Downloading";
                case State.Installing: return "Installing";
                case State.Detecting: return "Detecting";
                case State.WaitingReboot: return "Restart needed";
                case State.Succeeded: return "Done";
                case State.Failed: return "Failed";
                case State.Skipped: return "Skipped";
                case State.NotApplicable: return "Not applicable";
                default: return "Unknown";
            }
        }

        // Downloading, installing or detecting: something is happening now.
        public static bool IsActive(State s)
        {
            return s == State.Downloading || s == State.Installing || s == State.Detecting;
        }

        // The item will not change by itself any more.
        public static bool IsFinal(State s)
        {
            return s == State.Succeeded || s == State.Failed || s == State.Skipped || s == State.NotApplicable;
        }

        // Counts as finished in progress: done, skipped, not applicable, or done but waiting for a restart.
        public static bool IsComplete(State s)
        {
            return s == State.Succeeded || s == State.Skipped || s == State.NotApplicable || s == State.WaitingReboot;
        }

        public static State Parse(string s)
        {
            State st;
            return s != null && Enum.IsDefined(typeof(State), s) && Enum.TryParse(s, out st) ? st : State.Unknown;
        }
    }

    public static class Kinds
    {
        public const string Step = "Step";
        public const string Application = "Application";
        public const string Package = "Package";
        public const string Update = "Update";
        public const string Content = "Content";
        public const string Win32App = "Win32 app";
        public const string MsiApp = "MSI app";
        public const string StoreApp = "Store app";
        public const string Policy = "Policy";
        public const string Certificate = "Certificate";
        public const string Profile = "Profile";
        public const string Enrollment = "Enrollment";
        public const string Agent = "Agent";
    }

    public static class Origins
    {
        public const string Sccm = "SCCM";
        public const string Intune = "Intune";
        public const string Windows = "Windows";
    }

    // Phases in the order a deployment goes through them. Task sequence groups get their own order from 100.
    public static class Phases
    {
        public const string WinPE = "ts:winpe";
        public const string FullOs = "ts:fullos";
        public const string TaskSequence = "ts:main";
        public const string DevicePreparation = "esp:deviceprep";
        public const string DeviceSetup = "esp:device";
        public const string AccountSetup = "esp:account";
        public const string SccmApps = "cm:apps";
        public const string SccmPackages = "cm:packages";
        public const string SccmUpdates = "cm:updates";
        public const string Content = "cm:content";
        public const string IntuneApps = "intune:apps";

        public static string Name(string key)
        {
            switch (key)
            {
                case WinPE: return "Windows PE";
                case FullOs: return "Full Windows";
                case TaskSequence: return "Task sequence";
                case DevicePreparation: return "Device preparation";
                case DeviceSetup: return "Device setup";
                case AccountSetup: return "Account setup";
                case SccmApps: return "Applications";
                case SccmPackages: return "Packages";
                case SccmUpdates: return "Software updates";
                case Content: return "Content downloads";
                case IntuneApps: return "Intune apps";
                default: return key;
            }
        }

        public static int Order(string key)
        {
            switch (key)
            {
                case WinPE: return 10;
                case TaskSequence: return 15;
                case FullOs: return 20;
                case DevicePreparation: return 30;
                case DeviceSetup: return 40;
                case AccountSetup: return 50;
                case Content: return 60;
                case SccmApps: return 70;
                case SccmPackages: return 75;
                case SccmUpdates: return 80;
                case IntuneApps: return 90;
                default: return 200;
            }
        }

        // ESP phases decide where an item belongs, over the phase a log or WMI reader guessed.
        public static bool IsEsp(string key)
        {
            return key == DevicePreparation || key == DeviceSetup || key == AccountSetup;
        }
    }

    public static class EvidenceKinds
    {
        public const string LogLine = "Log line";
        public const string Registry = "Registry value";
        public const string Wmi = "WMI instance";
        public const string EventRecord = "Event record";
        public const string Graph = "Microsoft Graph";
        public const string StatusMessage = "Status message";
    }

    [DataContract]
    public sealed class Evidence
    {
        [DataMember] public string Kind;
        [DataMember] public string Path;
        [DataMember] public int Line;
        [DataMember] public string Text;

        public Evidence() { }

        public Evidence(string kind, string path, int line, string text)
        {
            Kind = kind;
            Path = path;
            Line = line;
            Text = text;
        }

        public string Where
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return Kind;
                if (Kind == EvidenceKinds.LogLine) return Txt.FileName(Path) + (Line > 0 ? ", line " + Line.ToString(CultureInfo.InvariantCulture) : "");
                if (Kind == EvidenceKinds.EventRecord) return Path + (Line > 0 ? ", record " + Line.ToString(CultureInfo.InvariantCulture) : "");
                return Path;
            }
        }

        public bool IsLogFile
        {
            get { return Kind == EvidenceKinds.LogLine && !string.IsNullOrEmpty(Path); }
        }
    }

    [DataContract]
    public sealed class Pair
    {
        [DataMember] public string Key;
        [DataMember] public string Value;

        public Pair() { }

        public Pair(string k, string v)
        {
            Key = k;
            Value = v;
        }
    }

    [DataContract]
    public sealed class TEvent
    {
        [DataMember] public DateTime TimeUtc;
        [DataMember] public string Source;
        [DataMember] public int Severity;
        [DataMember] public string Text;
        [DataMember] public string StateName;
        [DataMember] public string ItemKey;
        [DataMember] public Evidence Evidence;
        [DataMember] public long Seq;

        public State State
        {
            get { return States.Parse(StateName); }
        }
    }

    [DataContract]
    public sealed class Attempt
    {
        [DataMember] public int Index;
        [DataMember] public DateTime StartUtc;
        [DataMember] public DateTime? EndUtc;
        [DataMember] public string StateName;
        [DataMember] public long? ExitCode;
        [DataMember] public bool Reboot;
        [DataMember] public string Command;

        public State State
        {
            get { return States.Parse(StateName); }
            set { StateName = value.ToString(); }
        }
    }

    [DataContract]
    public sealed class Item
    {
        [DataMember] public string Key;
        [DataMember] public string Name;
        [DataMember] public string Kind;
        [DataMember] public string Origin;
        [DataMember] public string PhaseKey;
        [DataMember] public string StateName;
        [DataMember] public DateTime StateUtc;
        [DataMember] public int? Percent;
        [DataMember] public long? ExitCode;
        [DataMember] public bool Reboot;
        [DataMember] public DateTime FirstUtc;
        [DataMember] public DateTime LastUtc;
        [DataMember] public DateTime? StartUtc;
        [DataMember] public DateTime? EndUtc;
        [DataMember] public int Order;
        [DataMember] public List<Attempt> Attempts = new List<Attempt>();
        [DataMember] public List<Pair> Info = new List<Pair>();

        List<TEvent> events;

        // Filled when the session is built or loaded, not saved.
        public List<TEvent> Events
        {
            get { if (events == null) events = new List<TEvent>(); return events; }
        }

        public State State
        {
            get { return States.Parse(StateName); }
            set { StateName = value.ToString(); }
        }

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Key : Name; }
        }

        public string Get(string key)
        {
            foreach (Pair p in Info) if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (Pair p in Info)
            {
                if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    p.Value = value;
                    return;
                }
            }
            Info.Add(new Pair(key, value));
        }

        public Attempt Current
        {
            get { return Attempts.Count == 0 ? null : Attempts[Attempts.Count - 1]; }
        }

        public int FailedAttempts
        {
            get
            {
                int n = 0;
                foreach (Attempt a in Attempts) if (a.State == State.Failed) n++;
                return n;
            }
        }

        // How long the item has run: from its first activity to its end, or to now while it runs.
        public TimeSpan? Duration(DateTime nowUtc)
        {
            if (StartUtc == null) return null;
            DateTime end = EndUtc ?? (States.IsFinal(State) ? LastUtc : nowUtc);
            TimeSpan d = end - StartUtc.Value;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }

        public Evidence LastEvidence
        {
            get
            {
                for (int i = Events.Count - 1; i >= 0; i--) if (Events[i].Evidence != null) return Events[i].Evidence;
                return null;
            }
        }

        // The newest log file this item has a line in, for Open in LogVice.
        public string LogFile
        {
            get
            {
                for (int i = Events.Count - 1; i >= 0; i--)
                {
                    Evidence e = Events[i].Evidence;
                    if (e != null && e.IsLogFile) return e.Path;
                }
                return null;
            }
        }
    }

    [DataContract]
    public sealed class Phase
    {
        [DataMember] public string Key;
        [DataMember] public string Name;
        [DataMember] public int Order;

        List<Item> items;

        // Filled when the session is built or loaded, not saved.
        public List<Item> Items
        {
            get { if (items == null) items = new List<Item>(); return items; }
        }

        public State State
        {
            get
            {
                if (Items.Count == 0) return State.Unknown;
                bool active = false, pending = false, reboot = false, all = true;
                foreach (Item i in Items)
                {
                    State s = i.State;
                    if (s == State.Failed) return State.Failed;
                    if (States.IsActive(s)) active = true;
                    if (s == State.Pending || s == State.Unknown) pending = true;
                    if (s == State.WaitingReboot) reboot = true;
                    if (!States.IsComplete(s)) all = false;
                }
                if (active) return State.Installing;
                if (all) return reboot ? State.WaitingReboot : State.Succeeded;
                if (pending) return State.Pending;
                return State.Unknown;
            }
        }

        public int Count(Func<Item, bool> f)
        {
            int n = 0;
            foreach (Item i in Items) if (f(i)) n++;
            return n;
        }

        public int Done
        {
            get { return Count(delegate (Item i) { return States.IsComplete(i.State); }); }
        }

        public int FailedCount
        {
            get { return Count(delegate (Item i) { return i.State == State.Failed; }); }
        }

        // Share of items complete, with the progress of running items counted in.
        public double Progress
        {
            get
            {
                if (Items.Count == 0) return 0;
                double sum = 0;
                foreach (Item i in Items)
                {
                    if (States.IsComplete(i.State) || i.State == State.Failed) sum += 1;
                    else if (States.IsActive(i.State)) sum += i.Percent.HasValue ? Math.Min(99, Math.Max(0, i.Percent.Value)) / 100.0 * 0.9 : 0.35;
                }
                return sum / Items.Count;
            }
        }

        public DateTime? StartUtc
        {
            get
            {
                DateTime? m = null;
                foreach (Item i in Items)
                {
                    DateTime t = i.StartUtc ?? i.FirstUtc;
                    if (m == null || t < m.Value) m = t;
                }
                return m;
            }
        }

        public DateTime? EndUtc
        {
            get
            {
                DateTime? m = null;
                foreach (Item i in Items)
                {
                    if (!States.IsFinal(i.State) && i.State != State.WaitingReboot) return null;
                    DateTime t = i.EndUtc ?? i.LastUtc;
                    if (m == null || t > m.Value) m = t;
                }
                return m;
            }
        }

        public TimeSpan? Duration(DateTime nowUtc)
        {
            DateTime? s = StartUtc;
            if (s == null) return null;
            DateTime e = EndUtc ?? nowUtc;
            return e < s.Value ? TimeSpan.Zero : e - s.Value;
        }
    }

    public static class FindingKinds
    {
        public const string Failed = "Failed";
        public const string NotDetected = "Not detected after install";
        public const string Retries = "Retry loop";
        public const string Stuck = "No progress";
        public const string Reboot = "Restart needed";
        public const string EspTimeLimit = "ESP time limit";
        public const string TaskSequence = "Task sequence failed";
        public const string MdmErrors = "MDM errors";
    }

    [DataContract]
    public sealed class Finding
    {
        [DataMember] public string Kind;
        [DataMember] public int Severity;
        [DataMember] public string ItemKey;
        [DataMember] public string Title;
        [DataMember] public string Message;
        [DataMember] public string Hint;
        [DataMember] public DateTime TimeUtc;
        [DataMember] public Evidence Evidence;

        public string SeverityName
        {
            get { return Severity >= 2 ? "Error" : Severity == 1 ? "Warning" : "Info"; }
        }
    }

    [DataContract]
    public sealed class Session
    {
        public const int FormatVersion = 1;

        [DataMember] public int Format = FormatVersion;
        [DataMember] public string AppVersion;
        [DataMember] public string Computer;
        [DataMember] public string User;
        [DataMember] public string OsVersion;
        [DataMember] public bool Elevated;
        [DataMember] public string Mode;
        [DataMember] public string SourcePath;
        [DataMember] public DateTime SinceUtc;
        [DataMember] public DateTime SavedUtc;
        [DataMember] public DateTime ClockUtc;
        [DataMember] public List<Pair> Info = new List<Pair>();
        [DataMember] public List<Phase> Phases = new List<Phase>();
        [DataMember] public List<Item> Items = new List<Item>();
        [DataMember] public List<TEvent> Events = new List<TEvent>();
        [DataMember] public List<Finding> Findings = new List<Finding>();
        [DataMember] public List<string> Notes = new List<string>();
        [DataMember] public List<Pair> Names = new List<Pair>();
        [DataMember] public List<Pair> Aliases = new List<Pair>();

        Dictionary<string, Item> keys;
        Dictionary<string, Phase> phaseKeys;

        Dictionary<string, Item> byKey
        {
            get { if (keys == null) keys = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase); return keys; }
        }

        Dictionary<string, Phase> phaseByKey
        {
            get { if (phaseKeys == null) phaseKeys = new Dictionary<string, Phase>(StringComparer.OrdinalIgnoreCase); return phaseKeys; }
        }

        public string GetInfo(string key)
        {
            foreach (Pair p in Info) if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }

        public void SetInfo(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return;
            foreach (Pair p in Info)
            {
                if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    p.Value = value;
                    return;
                }
            }
            Info.Add(new Pair(key, value));
        }

        public Item Find(string key)
        {
            Item i;
            return key != null && byKey.TryGetValue(key, out i) ? i : null;
        }

        public Phase FindPhase(string key)
        {
            Phase p;
            return key != null && phaseByKey.TryGetValue(key, out p) ? p : null;
        }

        internal void Index(Item i)
        {
            byKey[i.Key] = i;
        }

        internal void Unindex(string key)
        {
            byKey.Remove(key);
        }

        internal void Index(Phase p)
        {
            phaseByKey[p.Key] = p;
        }

        public IEnumerable<Phase> OrderedPhases
        {
            get
            {
                List<Phase> l = new List<Phase>(Phases);
                l.RemoveAll(delegate (Phase p) { return p.Items.Count == 0; });
                l.Sort(delegate (Phase a, Phase b)
                {
                    int c = a.Order.CompareTo(b.Order);
                    if (c != 0) return c;
                    DateTime x = a.StartUtc ?? DateTime.MaxValue, y = b.StartUtc ?? DateTime.MaxValue;
                    return x.CompareTo(y);
                });
                return l;
            }
        }

        public int Count(State s)
        {
            int n = 0;
            foreach (Item i in Items) if (i.State == s) n++;
            return n;
        }

        public int CountActive
        {
            get
            {
                int n = 0;
                foreach (Item i in Items) if (States.IsActive(i.State)) n++;
                return n;
            }
        }

        public int CountComplete
        {
            get
            {
                int n = 0;
                foreach (Item i in Items) if (States.IsComplete(i.State)) n++;
                return n;
            }
        }

        public DateTime? FirstUtc
        {
            get
            {
                DateTime? m = null;
                foreach (Item i in Items)
                {
                    DateTime t = i.StartUtc ?? i.FirstUtc;
                    if (m == null || t < m.Value) m = t;
                }
                return m;
            }
        }

        // What kind of deployment this looks like, from the phases it has.
        public string Kind
        {
            get
            {
                List<string> parts = new List<string>();
                bool ts = false, esp = false, cm = false, intune = false;
                foreach (Phase p in Phases)
                {
                    if (p.Items.Count == 0) continue;
                    if (p.Key.StartsWith("ts:")) ts = true;
                    else if (p.Key.StartsWith("esp:")) esp = true;
                    else if (p.Key.StartsWith("cm:")) cm = true;
                    else if (p.Key == "intune:autopilot" || p.Key.StartsWith("intune:esp")) esp = true;
                    else if (p.Key.StartsWith("intune:")) intune = true;
                }
                if (ts) parts.Add("Task sequence");
                if (esp) parts.Add("Autopilot");
                if (cm && !ts) parts.Add("SCCM software");
                else if (cm) parts.Add("SCCM software");
                if (intune && !esp) parts.Add("Intune apps");
                return parts.Count == 0 ? "Nothing yet" : string.Join(" + ", parts.ToArray());
            }
        }

        // Builds the lookups and the per-item event lists after loading or bulk changes.
        public void Rebuild()
        {
            byKey.Clear();
            phaseByKey.Clear();
            foreach (Phase p in Phases)
            {
                p.Items.Clear();
                phaseByKey[p.Key] = p;
            }
            foreach (Item i in Items)
            {
                i.Events.Clear();
                byKey[i.Key] = i;
                Phase p = FindPhase(i.PhaseKey);
                if (p != null) p.Items.Add(i);
            }
            foreach (TEvent e in Events)
            {
                Item i = Find(e.ItemKey);
                if (i != null) i.Events.Add(e);
            }
            foreach (Phase p in Phases) p.Items.Sort(ItemOrder);
        }

        public static int ItemOrder(Item a, Item b)
        {
            return a.Order.CompareTo(b.Order);
        }

        public void Save(string path)
        {
            SavedUtc = DateTime.UtcNow;
            using (FileStream fs = File.Create(path))
            using (System.Xml.XmlDictionaryWriter w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, true, true, "  "))
            {
                Serializer().WriteObject(w, this);
            }
        }

        public static Session Load(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            {
                Session s = (Session)Serializer().ReadObject(fs);
                s.Fix();
                s.Mode = "File";
                s.SourcePath = path;
                return s;
            }
        }

        public static Session FromJson(string json)
        {
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                Session s = (Session)Serializer().ReadObject(ms);
                s.Fix();
                return s;
            }
        }

        public string ToJson()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                Serializer().WriteObject(ms, this);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        void Fix()
        {
            if (Info == null) Info = new List<Pair>();
            if (Phases == null) Phases = new List<Phase>();
            if (Items == null) Items = new List<Item>();
            if (Events == null) Events = new List<TEvent>();
            if (Findings == null) Findings = new List<Finding>();
            if (Notes == null) Notes = new List<string>();
            if (Names == null) Names = new List<Pair>();
            if (Aliases == null) Aliases = new List<Pair>();
            foreach (Item i in Items)
            {
                if (i.Attempts == null) i.Attempts = new List<Attempt>();
                if (i.Info == null) i.Info = new List<Pair>();
            }
            Rebuild();
        }

        static DataContractJsonSerializer Serializer()
        {
            DataContractJsonSerializerSettings s = new DataContractJsonSerializerSettings();
            s.DateTimeFormat = new DateTimeFormat("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffK");
            s.UseSimpleDictionaryFormat = true;
            return new DataContractJsonSerializer(typeof(Session), s);
        }
    }

    public static class Txt
    {
        public static string SplitCamel(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder b = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_') { b.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c))
                {
                    char p = s[i - 1];
                    bool nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
                    if (char.IsLower(p) || char.IsDigit(p) || (char.IsUpper(p) && nextLower)) b.Append(' ');
                }
                b.Append(c);
            }
            return b.ToString().Replace("  ", " ").Trim();
        }

        // The file name of a Windows path, also when TrackVice runs its tests on another system.
        public static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        public static string Short(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        // 45 s, 3 min 12 s, 1 h 04 min.
        public static string Duration(TimeSpan? d)
        {
            if (d == null) return "";
            TimeSpan t = d.Value;
            if (t.TotalSeconds < 60) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
            if (t.TotalMinutes < 60) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min " + t.Seconds.ToString("00", CultureInfo.InvariantCulture) + " s";
            return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + " min";
        }

        // 45 s, 3 min, 1 h 04 min: shorter, for chips and the phase rail.
        public static string DurationShort(TimeSpan? d)
        {
            if (d == null) return "";
            TimeSpan t = d.Value;
            if (t.TotalSeconds < 60) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
            if (t.TotalMinutes < 60) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
            return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + " min";
        }

        public static string Local(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string LocalTime(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string Ago(DateTime utc, DateTime nowUtc)
        {
            TimeSpan d = nowUtc - utc;
            if (d.TotalSeconds < 5) return "just now";
            return DurationShort(d) + " ago";
        }
    }
}
