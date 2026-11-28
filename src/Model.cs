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
        public const string Script = "Script";
        public const string Office = "Office";
        public const string Esp = "ESP";
        public const string Restart = "Restart";
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
        public const string Scripts = "intune:scripts";
        public const string Policies = "mdm:policies";
        public const string Restarts = "win:restarts";

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
                case IntuneApps: return "Apps";
                case Scripts: return "Platform scripts";
                case Policies: return "Policies";
                case Restarts: return "Restarts";
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
                case Scripts: return 92;
                case Policies: return 95;
                case Restarts: return 94;
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
        // "User" or "Device": whose context a platform script ran in.
        [DataMember] public string Context;

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
        // The times the device was off, set by OffTime.Mark, not saved.
        public OffSpans Off;
        // An app the deployment is expected to bring that the device has not shown yet (ExpectedApps). It exists only in the
        // viewer, is never saved or uploaded, and ExpectedSource says where the expectation comes from.
        public bool Expected;
        public string ExpectedSource;

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
            if (Off != null) d = Off.Active(this, StartUtc.Value, end, d);
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

        // Only apps that are expected and not on the device yet: the phase does not say what kind of deployment this is.
        public bool AllExpected
        {
            get
            {
                if (Items.Count == 0) return false;
                foreach (Item i in Items) if (!i.Expected) return false;
                return true;
            }
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
            if (e > s.Value && Items[0].Off != null) return OffTime.Active(Items[0].Off, s.Value, e, this);
            return e < s.Value ? TimeSpan.Zero : e - s.Value;
        }
    }

    public static class FindingKinds
    {
        public const string Failed = "Failed";
        public const string NoUploads = "No uploads";
        public const string NotDetected = "Not detected after install";
        public const string Retries = "Retry loop";
        public const string Stuck = "No progress";
        public const string Reboot = "Restart needed";
        public const string EspTimeLimit = "ESP time limit";
        public const string TaskSequence = "Task sequence failed";
        public const string MdmErrors = "MDM errors";
        public const string EspTimeLimitAhead = "ESP time limit ahead";
        public const string WaitingRestart = "Waiting for a restart";
        public const string CommonCause = "Common cause";
        public const string SlowDownload = "Slow download";
        public const string ScriptRerun = "Script run again";
        public const string ScriptUsers = "Script runs for users";
        public const string ExpectedMissing = "Not installed";
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
        // Times the agent could not reach the storage; older sessions have none.
        [DataMember] public List<NetOutage> Outages = new List<NetOutage>();

        // "Expected: 9 apps (Intune)", set by ExpectedApps for the viewer. Not saved.
        public string ExpectedText;

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
                    if (p.Items.Count == 0 || p.AllExpected) continue;
                    if (p.Key.StartsWith("ts:")) ts = true;
                    else if (p.Key.StartsWith("esp:")) esp = true;
                    else if (p.Key.StartsWith("cm:")) cm = true;
                    else if (p.Key == "intune:autopilot" || p.Key.StartsWith("intune:esp")) esp = true;
                    else if (p.Key.StartsWith("intune:") || p.Key.StartsWith("mdm:")) intune = true;
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
                // Sessions saved before the phase was renamed.
                if (p.Key == TrackVice.Phases.IntuneApps) p.Name = TrackVice.Phases.Name(p.Key);
            }
            foreach (Item i in Items)
            {
                i.Events.Clear();
                byKey[i.Key] = i;
                // Sessions saved, or uploaded by agents, from before apps offered in Company Portal stopped counting as waiting.
                if ((i.State == State.Pending || i.State == State.Unknown) && AppGroups.IsAvailable(i)) i.State = State.Skipped;
                Phase p = FindPhase(i.PhaseKey);
                if (p != null) p.Items.Add(i);
            }
            foreach (TEvent e in Events)
            {
                Item i = Find(e.ItemKey);
                if (i != null) i.Events.Add(e);
            }
            foreach (Phase p in Phases) p.Items.Sort(ItemOrder);
            EnrollmentAnchor.Apply(this);
            OffTime.Mark(this);
        }

        public static int ItemOrder(Item a, Item b)
        {
            return a.Order.CompareTo(b.Order);
        }

        public void Save(string path)
        {
            SavedUtc = DateTime.UtcNow;
            ExpectedHold hold = ExpectedApps.Detach(this);
            try
            {
                using (FileStream fs = File.Create(path))
                using (System.Xml.XmlDictionaryWriter w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, true, true, "  "))
                {
                    Serializer().WriteObject(w, this);
                }
            }
            finally { ExpectedApps.Attach(this, hold); }
        }

        // The most a session may be as JSON, also after it is unpacked: a file or an upload larger than this is refused
        // before it is read into memory.
        public static int MaxJsonBytes = 64 * 1024 * 1024;
        // The most objects one session may hold, so a small file of empty entries cannot fill the memory.
        public const int MaxObjects = 10 * 1000 * 1000;

        static string TooLarge()
        {
            return "The session is larger than " + (MaxJsonBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB.";
        }

        public static Session Load(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            {
                if (fs.Length > MaxJsonBytes) throw new InvalidDataException(TooLarge());
                Session s = Read(fs);
                s.Mode = "File";
                s.SourcePath = path;
                return s;
            }
        }

        public static Session FromJson(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            if (json.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw new InvalidDataException(TooLarge());
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return Read(ms);
            }
        }

        // A session as devices upload it: gzip of the JSON. It is unpacked no further than MaxJsonBytes, so a small
        // upload that unpacks to gigabytes is refused instead of read.
        public static Session FromGzip(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length > MaxJsonBytes) throw new InvalidDataException(TooLarge());
            using (MemoryStream ms = new MemoryStream(data))
            using (System.IO.Compression.GZipStream gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (MemoryStream o = new MemoryStream())
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                {
                    if (o.Length + n > MaxJsonBytes) throw new InvalidDataException(TooLarge());
                    o.Write(buf, 0, n);
                }
                o.Position = 0;
                return Read(o);
            }
        }

        // A session is a few levels deep. Text nested deeper than this is refused instead of read, so it cannot run the
        // reader out of stack.
        public const int MaxDepth = 64;

        static Session Read(Stream json)
        {
            System.Xml.XmlDictionaryReaderQuotas q = new System.Xml.XmlDictionaryReaderQuotas();
            q.MaxDepth = MaxDepth;
            q.MaxStringContentLength = MaxJsonBytes;
            q.MaxArrayLength = MaxJsonBytes;
            q.MaxBytesPerRead = MaxJsonBytes;
            q.MaxNameTableCharCount = MaxJsonBytes;
            Session s;
            using (System.Xml.XmlDictionaryReader r = JsonReaderWriterFactory.CreateJsonReader(json, q))
                s = (Session)Serializer().ReadObject(r);
            if (s == null) throw new InvalidDataException("The text holds no session.");
            s.Fix();
            return s;
        }

        public string ToJson()
        {
            ExpectedHold hold = ExpectedApps.Detach(this);
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    Serializer().WriteObject(ms, this);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            finally { ExpectedApps.Attach(this, hold); }
        }

        // The longest name, key and text a session read from a file or from storage keeps. Longer ones are cut.
        public const int MaxName = 512, MaxKey = 2048, MaxText = 32768;
        // The most entries of each kind a session read keeps. A real deployment has a few hundred items and some thousand
        // events; far more only makes the window slow, so the rest is left out.
        public const int MaxItems = 10000, MaxEvents = 500000, MaxEntries = 20000, MaxNames = 100000, MaxPerItem = 1000;

        // Keeps the first max entries, or with newest the last ones.
        static void Cap<TE>(List<TE> l, int max, bool newest)
        {
            if (l.Count <= max) return;
            if (newest) l.RemoveRange(0, l.Count - max);
            else l.RemoveRange(max, l.Count - max);
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
            if (Outages == null) Outages = new List<NetOutage>();
            Sanitize();
            Rebuild();
        }

        // A session that is read comes from another device, or from whoever can write where devices upload. Entries that
        // are missing, names with characters that change how text reads, absurd lengths and times at the end of the
        // calendar are taken out here, once, so the rest of TrackVice can show and count a session as it is.
        void Sanitize()
        {
            AppVersion = Txt.Clean(AppVersion, 64, false);
            Computer = Txt.Clean(Computer, MaxName, false);
            User = Txt.Clean(User, MaxName, false);
            OsVersion = Txt.Clean(OsVersion, MaxName, false);
            Mode = Txt.Clean(Mode, 64, false);
            SourcePath = Txt.Clean(SourcePath, MaxKey, false);
            SinceUtc = Txt.Sane(SinceUtc);
            SavedUtc = Txt.Sane(SavedUtc);
            ClockUtc = Txt.Sane(ClockUtc);
            Cap(Info, MaxEntries, false);
            Cap(Names, MaxNames, false);
            Cap(Aliases, MaxNames, false);
            CleanPairs(Info, false);
            CleanPairs(Names, true);
            CleanPairs(Aliases, true);
            Phases.RemoveAll(delegate (Phase p) { return p == null || string.IsNullOrEmpty(p.Key); });
            Cap(Phases, MaxEntries, false);
            foreach (Phase p in Phases)
            {
                p.Key = Txt.Clean(p.Key, MaxKey, false);
                p.Name = Txt.Clean(p.Name, MaxName, false);
            }
            Items.RemoveAll(delegate (Item i) { return i == null || string.IsNullOrEmpty(i.Key); });
            Cap(Items, MaxItems, false);
            foreach (Item i in Items)
            {
                i.Key = Txt.Clean(i.Key, MaxKey, false);
                i.Name = Txt.Clean(i.Name, MaxName, false);
                i.Kind = Txt.Clean(i.Kind, MaxName, false);
                i.Origin = Txt.Clean(i.Origin, MaxName, false);
                i.PhaseKey = Txt.Clean(i.PhaseKey, MaxKey, false);
                i.StateName = Txt.Clean(i.StateName, 64, false);
                if (i.Percent.HasValue) i.Percent = Math.Max(0, Math.Min(100, i.Percent.Value));
                i.StateUtc = Txt.Sane(i.StateUtc);
                i.FirstUtc = Txt.Sane(i.FirstUtc);
                i.LastUtc = Txt.Sane(i.LastUtc);
                i.StartUtc = Txt.Sane(i.StartUtc);
                i.EndUtc = Txt.Sane(i.EndUtc);
                if (i.Attempts == null) i.Attempts = new List<Attempt>();
                i.Attempts.RemoveAll(delegate (Attempt a) { return a == null; });
                Cap(i.Attempts, MaxPerItem, true);
                foreach (Attempt a in i.Attempts)
                {
                    a.StartUtc = Txt.Sane(a.StartUtc);
                    a.EndUtc = Txt.Sane(a.EndUtc);
                    a.StateName = Txt.Clean(a.StateName, 64, false);
                    a.Command = Txt.Clean(a.Command, MaxText, true);
                    a.Context = Txt.Clean(a.Context, 64, false);
                }
                if (i.Info == null) i.Info = new List<Pair>();
                Cap(i.Info, MaxPerItem, false);
                CleanPairs(i.Info, false);
            }
            Events.RemoveAll(delegate (TEvent e) { return e == null; });
            Cap(Events, MaxEvents, true);
            foreach (TEvent e in Events)
            {
                e.TimeUtc = Txt.Sane(e.TimeUtc);
                e.Source = Txt.Clean(e.Source, MaxName, false);
                e.Text = Txt.Clean(e.Text, MaxText, true);
                e.StateName = Txt.Clean(e.StateName, 64, false);
                e.ItemKey = Txt.Clean(e.ItemKey, MaxKey, false);
                Clean(e.Evidence);
            }
            Findings.RemoveAll(delegate (Finding f) { return f == null; });
            Cap(Findings, MaxEntries, false);
            foreach (Finding f in Findings)
            {
                f.Kind = Txt.Clean(f.Kind, MaxName, false);
                f.ItemKey = Txt.Clean(f.ItemKey, MaxKey, false);
                f.Title = Txt.Clean(f.Title, MaxName, false);
                f.Message = Txt.Clean(f.Message, MaxText, true);
                f.Hint = Txt.Clean(f.Hint, MaxText, true);
                f.TimeUtc = Txt.Sane(f.TimeUtc);
                Clean(f.Evidence);
            }
            Notes.RemoveAll(delegate (string n) { return n == null; });
            Cap(Notes, MaxEntries, false);
            for (int k = 0; k < Notes.Count; k++) Notes[k] = Txt.Clean(Notes[k], MaxText, true);
            Outages.RemoveAll(delegate (NetOutage o) { return o == null; });
            Cap(Outages, MaxEntries, true);
            foreach (NetOutage o in Outages)
            {
                o.FromUtc = Txt.Sane(o.FromUtc);
                o.ToUtc = Txt.Sane(o.ToUtc);
                o.Reason = Txt.Clean(o.Reason, MaxName, false);
            }
        }

        static void Clean(Evidence e)
        {
            if (e == null) return;
            e.Kind = Txt.Clean(e.Kind, MaxName, false);
            e.Path = Txt.Clean(e.Path, MaxKey, false);
            e.Text = Txt.Clean(e.Text, MaxText, true);
        }

        // keys: both sides are keys of items, kept as they are apart from their length.
        static void CleanPairs(List<Pair> l, bool keys)
        {
            l.RemoveAll(delegate (Pair p) { return p == null; });
            foreach (Pair p in l)
            {
                p.Key = Txt.Clean(p.Key, keys ? MaxKey : MaxName, false);
                p.Value = Txt.Clean(p.Value, keys ? MaxKey : MaxText, !keys);
            }
        }

        static DataContractJsonSerializer Serializer()
        {
            DataContractJsonSerializerSettings s = new DataContractJsonSerializerSettings();
            s.DateTimeFormat = new DateTimeFormat("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffK");
            s.UseSimpleDictionaryFormat = true;
            s.MaxItemsInObjectGraph = MaxObjects;
            return new DataContractJsonSerializer(typeof(Session), s);
        }
    }

    public static class Txt
    {
        // Characters that change how the text around them reads: the marks and overrides for text direction, and the
        // spaces without width that make two names look the same.
        static bool Hidden(char c)
        {
            return c == '\u061C' || c == '\u200B' || c == '\u200E' || c == '\u200F' || (c >= '\u202A' && c <= '\u202E') || c == '\u2060'
                || (c >= '\u2066' && c <= '\u2069') || c == '\uFEFF' || c == '\uFFFE' || c == '\uFFFF';
        }

        static bool Control(char c)
        {
            return c < ' ' || (c >= '\u007F' && c <= '\u009F') || c == '\u2028' || c == '\u2029';
        }

        // Text that came from a device, a log or a storage account, safe to show in a list: without control characters
        // and without the marks that reverse or reorder text, and no longer than max. lines keeps tabs and line breaks,
        // for text that is shown over several lines. Null stays null.
        public static string Clean(string s, int max, bool lines)
        {
            if (s == null) return null;
            bool bad = s.Length > max;
            for (int i = 0; i < s.Length && !bad; i++)
            {
                char c = s[i];
                if (Hidden(c)) bad = true;
                else if (Control(c) && !(lines && (c == '\t' || c == '\r' || c == '\n'))) bad = true;
            }
            if (!bad) return s;
            StringBuilder b = new StringBuilder(Math.Min(s.Length, max));
            bool cut = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (Hidden(c)) continue;
                if (Control(c) && !(lines && (c == '\t' || c == '\r' || c == '\n'))) c = ' ';
                if (b.Length >= max) { cut = true; break; }
                b.Append(c);
            }
            if (cut)
            {
                b.Length = Math.Max(0, max - 1);
                // Half of a character outside the basic plane is not left at the end.
                if (b.Length > 0 && char.IsHighSurrogate(b[b.Length - 1])) b.Length--;
                b.Append('\u2026');
            }
            return b.ToString();
        }

        // The last time TrackVice counts with. A time a device claims beyond it is moved back to it, so adding to it
        // cannot run off the end of the calendar.
        public static readonly DateTime LastTime = new DateTime(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DateTime Sane(DateTime t)
        {
            return t.Year >= 9000 ? DateTime.SpecifyKind(LastTime, t.Kind) : t;
        }

        public static DateTime? Sane(DateTime? t)
        {
            return t.HasValue ? Sane(t.Value) : t;
        }

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
            if (d.TotalHours >= 48) return ((int)d.TotalDays).ToString(CultureInfo.InvariantCulture) + " days ago";
            return DurationShort(d) + " ago";
        }
    }
}
