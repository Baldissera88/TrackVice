namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    // ---------- the reference enrollment of a customer ----------

    // One item of the reference: what it was, how it ended and how long it took. No events, so the file stays small.
    [DataContract(Name = "Item", Namespace = "")]
    public sealed class ReferenceItem
    {
        [DataMember(Order = 1)] public string Key;
        [DataMember(Order = 2, EmitDefaultValue = false)] public string Name;
        [DataMember(Order = 3)] public string Kind;
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Origin;
        [DataMember(Order = 5)] public string Phase;
        [DataMember(Order = 6, EmitDefaultValue = false)] public string PhaseName;
        [DataMember(Order = 7)] public int PhaseOrder;
        [DataMember(Order = 8)] public string State;
        [DataMember(Order = 9, EmitDefaultValue = false)] public long? ExitCode;
        // Seconds from the start of the enrollment to the start of the item, and how long it ran.
        [DataMember(Order = 10, EmitDefaultValue = false)] public double? Offset;
        [DataMember(Order = 11, EmitDefaultValue = false)] public double? Seconds;
        // Required, Available, Dependency or Uninstall, for apps. An app offered in Company Portal is never missing.
        [DataMember(Order = 12, EmitDefaultValue = false)] public string Assignment;
    }

    [DataContract(Name = "Reference", Namespace = "")]
    public sealed class Reference
    {
        public const string DocKind = "TrackVice reference";

        [DataMember(Order = 1)] public string Kind = DocKind;
        [DataMember(Order = 2)] public int Version = 1;
        [DataMember(Order = 3)] public string Customer;
        [DataMember(Order = 4)] public string Computer;
        [DataMember(Order = 5, EmitDefaultValue = false)] public string Deployment;
        [DataMember(Order = 6, EmitDefaultValue = false)] public string Profile;
        // Where the enrollment came from: this device, a saved session file, an agent upload or a demo.
        [DataMember(Order = 7, EmitDefaultValue = false)] public string Source;
        [DataMember(Order = 8)] public DateTime MarkedUtc;
        [DataMember(Order = 9)] public DateTime StartUtc;
        [DataMember(Order = 10)] public List<ReferenceItem> Items = new List<ReferenceItem>();

        // Trims a session to what the comparison needs.
        public static Reference From(Session s, DateTime nowUtc, string customer, string source)
        {
            Reference r = new Reference();
            r.Customer = customer;
            r.Computer = s.Computer;
            r.Deployment = s.Items.Count > 0 ? s.Kind : null;
            r.Profile = s.GetInfo("Autopilot profile") ?? s.GetInfo("Task sequence");
            r.Source = source;
            r.MarkedUtc = DateTime.UtcNow;
            DateTime start = s.FirstUtc ?? nowUtc;
            r.StartUtc = start;
            foreach (Item i in s.Items)
            {
                ReferenceItem x = new ReferenceItem();
                x.Key = i.Key;
                x.Name = string.IsNullOrEmpty(i.Name) ? null : i.Name;
                x.Kind = i.Kind;
                x.Origin = i.Origin;
                x.Phase = i.PhaseKey;
                Phase p = s.FindPhase(i.PhaseKey);
                if (p != null)
                {
                    x.PhaseName = p.Name;
                    x.PhaseOrder = p.Order;
                }
                x.State = i.StateName;
                x.ExitCode = i.ExitCode;
                x.Assignment = AppGroups.Assignment(i);
                if (i.StartUtc.HasValue) x.Offset = i.StartUtc.Value > start ? OffTime.Active(s, start, i.StartUtc.Value).TotalSeconds : 0;
                TimeSpan? d = i.Duration(nowUtc);
                if (d.HasValue) x.Seconds = d.Value.TotalSeconds;
                r.Items.Add(x);
            }
            return r;
        }

        // A session made from the reference, for SessionDiff and the Compare dialog. Its clock is the end of the enrollment.
        public Session ToSession(string computerName)
        {
            Session s = new Session();
            s.Mode = "Reference";
            s.Computer = computerName ?? Computer;
            s.SourcePath = Source;
            if (!string.IsNullOrEmpty(Profile)) s.SetInfo("Profile", Profile);
            DateTime end = StartUtc;
            Dictionary<string, Phase> phases = new Dictionary<string, Phase>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (ReferenceItem x in Items)
            {
                if (string.IsNullOrEmpty(x.Key)) continue;
                string pk = x.Phase ?? "";
                if (!phases.ContainsKey(pk))
                {
                    Phase p = new Phase();
                    p.Key = pk;
                    p.Name = pk == Phases.IntuneApps ? Phases.Name(pk) : x.PhaseName ?? Phases.Name(pk);
                    p.Order = x.PhaseOrder != 0 ? x.PhaseOrder : Phases.Order(pk);
                    phases[pk] = p;
                    s.Phases.Add(p);
                }
                Item i = new Item();
                i.Key = x.Key;
                i.Name = x.Name;
                i.Kind = x.Kind;
                i.Origin = x.Origin;
                i.PhaseKey = pk;
                i.StateName = x.State;
                i.ExitCode = x.ExitCode;
                if (x.Assignment != null) i.Set(AppGroups.AssignmentKey, x.Assignment);
                i.Order = order++;
                DateTime from = StartUtc.AddSeconds(x.Offset ?? 0);
                i.FirstUtc = from;
                i.LastUtc = from;
                i.StateUtc = from;
                if (x.Seconds.HasValue)
                {
                    i.StartUtc = from;
                    i.EndUtc = from.AddSeconds(x.Seconds.Value);
                    i.LastUtc = i.EndUtc.Value;
                }
                if (i.LastUtc > end) end = i.LastUtc;
                s.Items.Add(i);
            }
            s.ClockUtc = end;
            s.Rebuild();
            return s;
        }

        static DataContractJsonSerializer Serializer()
        {
            DataContractJsonSerializerSettings st = new DataContractJsonSerializerSettings();
            st.DateTimeFormat = new DateTimeFormat("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffK");
            return new DataContractJsonSerializer(typeof(Reference), st);
        }

        public string ToJson()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (System.Xml.XmlDictionaryWriter w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    Serializer().WriteObject(w, this);
                    w.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // Null when the text is not a TrackVice reference.
        public static Reference FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    Reference r = (Reference)Serializer().ReadObject(ms);
                    if (r == null || r.Kind != DocKind) return null;
                    if (r.Items == null) r.Items = new List<ReferenceItem>();
                    return r;
                }
            }
            catch (Exception) { return null; }
        }
    }

    // Where the references are kept: one small file per customer.
    public interface IReferenceFiles
    {
        // Null when there is no such file.
        string Read(string name);
        void Write(string name, string text);
        bool Delete(string name);
    }

    public sealed class MemoryReferenceFiles : IReferenceFiles
    {
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Read(string name)
        {
            string t;
            return Files.TryGetValue(name, out t) ? t : null;
        }

        public void Write(string name, string text) { Files[name] = text; }

        public bool Delete(string name) { return Files.Remove(name); }
    }

    // %LOCALAPPDATA%\TrackVice\References, for this Windows user.
    public sealed class FolderReferenceFiles : IReferenceFiles
    {
        readonly string root;

        public FolderReferenceFiles() : this(DefaultRoot) { }

        public FolderReferenceFiles(string root)
        {
            this.root = root;
        }

        public static string DefaultRoot
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"), "References"); }
        }

        public string Read(string name)
        {
            string p = Path.Combine(root, name);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }

        public void Write(string name, string text)
        {
            Directory.CreateDirectory(root);
            string p = Path.Combine(root, name), tmp = p + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }

        public bool Delete(string name)
        {
            string p = Path.Combine(root, name);
            if (!File.Exists(p)) return false;
            File.Delete(p);
            return true;
        }
    }

    // The reference of each customer, keyed by the customer's name.
    public sealed class ReferenceStore
    {
        readonly IReferenceFiles files;

        public ReferenceStore(IReferenceFiles files)
        {
            this.files = files;
        }

        // A file name for a customer: its name in safe characters, and a hash so that names that differ only in
        // characters that were replaced do not share a file. Case does not matter, like customer names.
        public static string FileName(string customer)
        {
            string n = (customer ?? "").Trim().ToLowerInvariant();
            StringBuilder b = new StringBuilder();
            foreach (char c in n)
            {
                if (b.Length >= 40) break;
                b.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' ? c : '_');
            }
            uint h = 2166136261;
            foreach (char c in n) { h ^= c; h *= 16777619; }
            return (b.Length == 0 ? "customer" : b.ToString()) + "-" + h.ToString("x8", CultureInfo.InvariantCulture) + ".json";
        }

        static string Name(string customer)
        {
            return string.IsNullOrEmpty(customer) || customer.Trim().Length == 0 ? CustomerBook.DefaultName : customer.Trim();
        }

        public Reference Get(string customer)
        {
            string c = Name(customer);
            Reference r = Reference.FromJson(files.Read(FileName(c)));
            if (r != null && r.Customer != null && !string.Equals(r.Customer.Trim(), c, StringComparison.OrdinalIgnoreCase)) return null;
            return r;
        }

        public void Set(string customer, Reference r)
        {
            r.Customer = Name(customer);
            files.Write(FileName(r.Customer), r.ToJson());
        }

        public bool Clear(string customer)
        {
            return files.Delete(FileName(Name(customer)));
        }
    }

    // ---------- comparing an enrollment with the reference ----------

    public sealed class ReferenceResult
    {
        public Reference Reference;
        public readonly List<string> Missing = new List<string>(), Extra = new List<string>(), FailedHere = new List<string>(), Slower = new List<string>();
        // Something still runs or waits, so items the reference has may still come.
        public bool Open;

        public int Count
        {
            get { return Missing.Count + Extra.Count + FailedHere.Count + Slower.Count; }
        }

        public bool Matches
        {
            get { return Count == 0; }
        }

        // The words of the badge in the main window and of the Reference column in Statistics.
        public string Badge
        {
            get
            {
                if (Matches) return Open ? "Matches the reference so far" : "Matches the reference";
                return Count == 1 ? "1 difference" : Count.ToString(CultureInfo.InvariantCulture) + " differences";
            }
        }
    }

    public static class ReferenceCheck
    {
        public const string FindingKind = "Reference";
        public const string FindingTitle = "Differs from the reference";
        const int MaxNames = 10;

        static bool Skipped(Item i)
        {
            return i.State == State.Skipped || i.State == State.NotApplicable || AppGroups.IsAvailable(i);
        }

        // Items that may still change. After the technician phase of pre-provisioning, account setup waits for the user,
        // so its waiting items do not keep the enrollment open.
        public static int StillOpen(Session s)
        {
            Item pp = s.Find(AutopilotEvents.PreProvKey);
            bool technicianDone = pp != null && pp.State == State.Succeeded;
            int n = 0;
            foreach (Item i in s.Items)
            {
                if (States.IsActive(i.State)) { n++; continue; }
                if (i.State != State.Pending && i.State != State.Unknown) continue;
                if (technicianDone && i.PhaseKey == Phases.AccountSetup) continue;
                n++;
            }
            return n;
        }

        public static ReferenceResult Check(Session s, DateTime nowUtc, Reference r)
        {
            ReferenceResult res = new ReferenceResult();
            res.Reference = r;
            if (r == null || s == null) return res;
            res.Open = StillOpen(s) > 0;
            Session b = r.ToSession(null);
            foreach (DiffRow row in SessionDiff.Compare(s, nowUtc, b, b.ClockUtc))
            {
                if (row.IsFinding) continue;
                Item here = row.ItemA, there = row.ItemB;
                if (here == null)
                {
                    // While the enrollment still runs, an item the reference has may just not have started yet.
                    if (!Skipped(there) && !res.Open) res.Missing.Add(row.Name);
                    continue;
                }
                if (there == null)
                {
                    if (!Skipped(here)) res.Extra.Add(row.Name);
                    continue;
                }
                if (here.State == State.Failed)
                {
                    if (there.State != State.Failed)
                        res.FailedHere.Add(row.Name + (here.ExitCode.HasValue && here.ExitCode.Value != 0 ? " (" + Errors.Short(here.ExitCode) + ")" : ""));
                    continue;
                }
                if (row.SecondsA.HasValue && row.SecondsB.HasValue)
                {
                    double a = row.SecondsA.Value, was = row.SecondsB.Value;
                    if (a - was >= SessionDiff.SlowerSeconds && a >= was * SessionDiff.SlowerFactor)
                        res.Slower.Add(row.Name + " (" + Txt.DurationShort(TimeSpan.FromSeconds(a)) + ", " + Txt.DurationShort(TimeSpan.FromSeconds(was)) + " in the reference)");
                }
            }
            return res;
        }

        // Replaces the reference finding of the session. Returns the result, or null when there is no reference.
        public static ReferenceResult Apply(Session s, DateTime nowUtc, Reference r)
        {
            s.Findings.RemoveAll(delegate (Finding f) { return f.Kind == FindingKind; });
            if (r == null || s.Items.Count == 0) return null;
            ReferenceResult res = Check(s, nowUtc, r);
            if (!res.Matches) s.Findings.Add(ToFinding(res, nowUtc));
            return res;
        }

        public static Finding ToFinding(ReferenceResult res, DateTime nowUtc)
        {
            Finding f = new Finding();
            f.Kind = FindingKind;
            f.Severity = 1;
            f.Title = FindingTitle;
            f.TimeUtc = nowUtc;
            StringBuilder m = new StringBuilder();
            m.Append(res.Count == 1 ? "1 difference" : res.Count + " differences").Append(" from the reference enrollment of ").Append(res.Reference.Computer).Append('.');
            Part(m, "Missing here", res.Missing);
            Part(m, "Extra here", res.Extra);
            Part(m, "Failed only here", res.FailedHere);
            Part(m, "Clearly slower", res.Slower);
            f.Message = m.ToString();
            f.Hint = "The reference was set " + Txt.Local(res.Reference.MarkedUtc) + ". If the differences are expected, use this enrollment as the reference from the Devices menu. To see every item side by side, compare with the reference.";
            return f;
        }

        static void Part(StringBuilder m, string label, List<string> names)
        {
            if (names.Count == 0) return;
            List<string> l = names.Count > MaxNames ? names.GetRange(0, MaxNames) : names;
            m.Append(' ').Append(label).Append(": ").Append(string.Join(", ", l.ToArray()));
            if (names.Count > MaxNames) m.Append(" and ").Append(names.Count - MaxNames).Append(" more");
            m.Append('.');
        }

        // The badge text for each session, for the Reference column in Statistics. Empty when there is no reference.
        public static Dictionary<string, string> Badges(Reference r, IList<Session> sessions, IList<string> keys)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (r == null || sessions == null || keys == null) return d;
            for (int i = 0; i < sessions.Count && i < keys.Count; i++)
            {
                Session s = sessions[i];
                if (s == null || keys[i] == null || s.Items.Count == 0) continue;
                DateTime now = s.ClockUtc.Year > 2000 ? s.ClockUtc : DateTime.UtcNow;
                d[keys[i]] = Check(s, now, r).Badge;
            }
            return d;
        }
    }
}
