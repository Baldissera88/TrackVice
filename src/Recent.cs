namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // Where a device in the recent list was opened from, and so how to open it again.
    public static class RecentSources
    {
        public const string Agent = "agent", Sccm = "sccm", Intune = "intune", ThisPc = "this-pc", File = "file", Folder = "folder";

        public static string Name(string source)
        {
            switch (source)
            {
                case Agent: return "Agent";
                case Sccm: return "SCCM";
                case Intune: return "Autopilot in Intune";
                case ThisPc: return "This PC";
                case File: return "Saved session";
                case Folder: return "Log folder";
                default: return source ?? "";
            }
        }

        public static bool Known(string source)
        {
            return source == Agent || source == Sccm || source == Intune || source == ThisPc || source == File || source == Folder;
        }
    }

    // A device that was opened or followed on this PC. It holds only what is needed to open it again and to find it:
    // never a link, a key or a sign-in.
    public sealed class RecentDevice
    {
        public string Source = "";
        public string Customer = "";
        public string Device = "";
        // The upload name of an agent session, the SCCM run key, the Autopilot event ID, or the path of a file or folder.
        public string Id = "";
        public string Serial = "";
        public string User = "";
        public string Profile = "";
        public State State;
        public string StateText = "";
        public DateTime LastSeenUtc;
        public bool Pinned;

        public string Key
        {
            get { return KeyOf(Source, Customer, Id, Device); }
        }

        public static string KeyOf(string source, string customer, string id, string device)
        {
            string what = string.IsNullOrEmpty(id) ? device : id;
            return ((source ?? "") + "|" + (customer ?? "") + "|" + (what ?? "")).ToLowerInvariant();
        }

        public RecentDevice Copy()
        {
            return (RecentDevice)MemberwiseClone();
        }
    }

    // The recent devices of this Windows user, in %LOCALAPPDATA%\TrackVice\recent.txt. Pinned devices come first,
    // then the newest. With no path the list is kept in memory only, for the demo and the tests.
    public sealed class RecentStore
    {
        public const int MaxUnpinned = 100;
        const string Header = "#TrackVice recent devices 1";
        readonly string path;
        readonly object gate = new object();
        List<RecentDevice> list;

        public RecentStore(string path)
        {
            this.path = path;
        }

        public static string DefaultPath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"), "recent.txt"); }
        }

        public string FilePath
        {
            get { return path; }
        }

        // Pinned first, then the newest.
        public List<RecentDevice> All
        {
            get
            {
                lock (gate)
                {
                    Ensure();
                    List<RecentDevice> l = new List<RecentDevice>();
                    foreach (RecentDevice d in list) l.Add(d.Copy());
                    Order(l);
                    return l;
                }
            }
        }

        public RecentDevice Find(string key)
        {
            lock (gate)
            {
                Ensure();
                foreach (RecentDevice d in list) if (d.Key == key) return d.Copy();
                return null;
            }
        }

        public static void Order(List<RecentDevice> l)
        {
            l.Sort(delegate (RecentDevice a, RecentDevice b)
            {
                if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
                return b.LastSeenUtc.CompareTo(a.LastSeenUtc);
            });
        }

        // Adds the device, or updates it when it is there. A pin stays. Returns the entry as kept.
        public RecentDevice Touch(RecentDevice d)
        {
            RecentDevice clean = Clean(d);
            if (clean == null) return null;
            lock (gate)
            {
                Ensure();
                RecentDevice have = null;
                foreach (RecentDevice x in list) if (x.Key == clean.Key) { have = x; break; }
                if (have != null)
                {
                    clean.Pinned = have.Pinned;
                    if (clean.Serial.Length == 0) clean.Serial = have.Serial;
                    if (clean.User.Length == 0) clean.User = have.User;
                    if (clean.Profile.Length == 0) clean.Profile = have.Profile;
                    if (clean.StateText.Length == 0) { clean.StateText = have.StateText; clean.State = have.State; }
                    list.Remove(have);
                }
                list.Add(clean);
                Trim();
                Save();
                return clean.Copy();
            }
        }

        public bool Pin(string key, bool on)
        {
            lock (gate)
            {
                Ensure();
                foreach (RecentDevice x in list)
                {
                    if (x.Key != key) continue;
                    x.Pinned = on;
                    Trim();
                    Save();
                    return true;
                }
                return false;
            }
        }

        public bool Remove(string key)
        {
            lock (gate)
            {
                Ensure();
                int n = list.RemoveAll(delegate (RecentDevice x) { return x.Key == key; });
                if (n > 0) Save();
                return n > 0;
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                list = new List<RecentDevice>();
                Save();
            }
        }

        void Trim()
        {
            Order(list);
            int unpinned = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Pinned) continue;
                unpinned++;
                if (unpinned > MaxUnpinned) { list.RemoveAt(i); i--; }
            }
        }

        // What is kept: no line breaks or tabs, nothing that looks like a secret, and never a link with a key in it.
        static RecentDevice Clean(RecentDevice d)
        {
            if (d == null || !RecentSources.Known(d.Source)) return null;
            RecentDevice c = d.Copy();
            c.Customer = Field(c.Customer);
            c.Device = Field(c.Device);
            c.Id = Field(c.Id);
            c.Serial = Field(c.Serial);
            c.User = Field(c.User);
            c.Profile = Field(c.Profile);
            c.StateText = Field(c.StateText);
            if (c.Id.IndexOf("://", StringComparison.Ordinal) >= 0 || c.Id.IndexOf("sig=", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (c.Device.Length == 0 && c.Id.Length == 0) return null;
            if (c.LastSeenUtc.Kind != DateTimeKind.Utc) c.LastSeenUtc = DateTime.SpecifyKind(c.LastSeenUtc, DateTimeKind.Utc);
            return c;
        }

        static string Field(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return Txt.Short(Secrets.Mask(t), 300);
        }

        void Ensure()
        {
            if (list != null) return;
            list = new List<RecentDevice>();
            if (path == null) return;
            try
            {
                if (!System.IO.File.Exists(path)) return;
                foreach (string line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
                {
                    RecentDevice d = Parse(line);
                    if (d != null && Find(list, d.Key) == null) list.Add(d);
                }
            }
            catch (Exception) { }
        }

        static RecentDevice Find(List<RecentDevice> l, string key)
        {
            foreach (RecentDevice d in l) if (d.Key == key) return d;
            return null;
        }

        public static RecentDevice Parse(string line)
        {
            if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) return null;
            string[] p = line.Split('\t');
            if (p.Length < 11) return null;
            RecentDevice d = new RecentDevice();
            d.Source = p[0];
            d.Customer = p[1];
            d.Device = p[2];
            d.Id = p[3];
            d.Serial = p[4];
            d.User = p[5];
            d.Profile = p[6];
            d.State = States.Parse(p[7]);
            d.StateText = p[8];
            DateTime t;
            if (DateTime.TryParse(p[9], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) d.LastSeenUtc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            d.Pinned = p[10] == "1";
            return Clean(d);
        }

        public static string Format(RecentDevice d)
        {
            return string.Join("\t", new string[]
            {
                d.Source, d.Customer, d.Device, d.Id, d.Serial, d.User, d.Profile, d.State.ToString(), d.StateText,
                d.LastSeenUtc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture), d.Pinned ? "1" : "0"
            });
        }

        void Save()
        {
            if (path == null) return;
            try
            {
                StringBuilder b = new StringBuilder(Header).Append("\r\n");
                foreach (RecentDevice d in list) b.Append(Format(d)).Append("\r\n");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, b.ToString(), Encoding.UTF8);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(tmp, path);
            }
            catch (Exception) { }
        }

        // The state of a session in a few words, for the list.
        public static void StateOf(Session s, out State state, out string text)
        {
            state = State.Unknown;
            text = "";
            if (s == null || s.Items.Count == 0) { text = "Nothing found yet"; return; }
            int failed = s.Count(State.Failed);
            if (s.CountActive > 0) state = State.Installing;
            else if (failed > 0 || (s.GetInfo("Task sequence result") ?? "").StartsWith("Failed", StringComparison.Ordinal)) state = State.Failed;
            else if (s.CountComplete == s.Items.Count) state = State.Succeeded;
            else state = State.Pending;
            text = s.CountComplete + " of " + s.Items.Count + " done" + (failed > 0 ? ", " + failed + " failed" : "");
        }
    }

    // A device the search found: from the recent list, the agent uploads or the SCCM runs that are loaded.
    public sealed class FoundDevice
    {
        public string Source = "", Customer = "", Device = "", Id = "", Serial = "", User = "", Profile = "", StateText = "";
        // The note kept on this PC for the device, or "".
        public string Note = "";
        public State State;
        public DateTime LastUtc;
        public bool Pinned, Recent;
        // Set when the device came from a loaded list, so it opens without looking it up again.
        public OverviewDevice Loaded;

        public string Key
        {
            get { return RecentDevice.KeyOf(Source, Customer, Id, Device); }
        }

        public static FoundDevice Of(RecentDevice r)
        {
            FoundDevice f = new FoundDevice();
            f.Source = r.Source;
            f.Customer = r.Customer;
            f.Device = r.Device;
            f.Id = r.Id;
            f.Serial = r.Serial;
            f.User = r.User;
            f.Profile = r.Profile;
            f.State = r.State;
            f.StateText = r.StateText;
            f.LastUtc = r.LastSeenUtc;
            f.Pinned = r.Pinned;
            f.Recent = true;
            return f;
        }

        public static FoundDevice Of(OverviewDevice o)
        {
            FoundDevice f = new FoundDevice();
            RemoteDeployment d = o.D;
            f.Source = o.Kind == OverviewKinds.Sccm ? RecentSources.Sccm : RecentSources.Agent;
            f.Customer = o.Customer ?? "";
            f.Device = d.Device ?? "";
            f.Id = d.Key ?? "";
            f.Serial = d.Serial ?? "";
            f.User = Who.Label(o.S) ?? d.EnrollUser ?? "";
            if (f.Serial.Length == 0 && o.S != null) f.Serial = o.S.GetInfo(DeviceFacts.Serial) ?? "";
            f.Profile = d.Profile ?? "";
            f.State = d.State;
            f.StateText = d.StateText ?? "";
            f.LastUtc = d.LastUtc;
            f.Loaded = o;
            return f;
        }

        public RecentDevice ToRecent()
        {
            RecentDevice r = new RecentDevice();
            r.Source = Source;
            r.Customer = Customer;
            r.Device = Device;
            r.Id = Id;
            r.Serial = Serial;
            r.User = User;
            r.Profile = Profile;
            r.State = State;
            r.StateText = StateText;
            r.LastSeenUtc = LastUtc;
            return r;
        }
    }

    // One search box over the recent devices and the devices already loaded, across customers and Intune / SCCM:
    // device name, serial number, user, task sequence or Autopilot profile. Every word must be found.
    public static class DeviceSearch
    {
        public static bool Matches(FoundDevice f, string query)
        {
            if (string.IsNullOrEmpty(query) || query.Trim().Length == 0) return true;
            foreach (string w in query.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Has(f.Device, w) && !Has(f.Serial, w) && !Has(f.User, w) && !Has(f.Profile, w) && !Has(f.Customer, w) && !Has(f.Note, w)) return false;
            }
            return true;
        }

        static bool Has(string s, string w)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The recent devices and the loaded ones together, each once. A loaded device keeps its pin and gives its fresh state.
        public static List<FoundDevice> Merge(IEnumerable<RecentDevice> recent, IEnumerable<OverviewDevice> loaded)
        {
            List<FoundDevice> all = new List<FoundDevice>();
            Dictionary<string, FoundDevice> byKey = new Dictionary<string, FoundDevice>();
            if (recent != null)
                foreach (RecentDevice r in recent)
                {
                    FoundDevice f = FoundDevice.Of(r);
                    if (byKey.ContainsKey(f.Key)) continue;
                    byKey[f.Key] = f;
                    all.Add(f);
                }
            if (loaded != null)
                foreach (OverviewDevice o in loaded)
                {
                    FoundDevice f = FoundDevice.Of(o);
                    FoundDevice have;
                    if (byKey.TryGetValue(f.Key, out have))
                    {
                        have.Loaded = o;
                        have.State = f.State;
                        have.StateText = f.StateText;
                        if (f.LastUtc > have.LastUtc) have.LastUtc = f.LastUtc;
                        if (have.Serial.Length == 0) have.Serial = f.Serial;
                        if (have.User.Length == 0) have.User = f.User;
                        if (have.Profile.Length == 0) have.Profile = f.Profile;
                        continue;
                    }
                    byKey[f.Key] = f;
                    all.Add(f);
                }
            foreach (FoundDevice f in all) f.Note = DeviceNotes.Of(f.Device);
            return all;
        }

        // The devices that match, best first: the exact device name, then a name that starts with the words, then pinned,
        // then recent, then the newest.
        public static List<FoundDevice> Search(string query, IEnumerable<RecentDevice> recent, IEnumerable<OverviewDevice> loaded, int max)
        {
            string q = (query ?? "").Trim();
            List<FoundDevice> hits = new List<FoundDevice>();
            foreach (FoundDevice f in Merge(recent, loaded))
            {
                if (q.Length == 0 && !f.Recent) continue;
                if (Matches(f, q)) hits.Add(f);
            }
            hits.Sort(delegate (FoundDevice a, FoundDevice b)
            {
                int c = Rank(b, q).CompareTo(Rank(a, q));
                return c != 0 ? c : b.LastUtc.CompareTo(a.LastUtc);
            });
            if (max > 0 && hits.Count > max) hits.RemoveRange(max, hits.Count - max);
            return hits;
        }

        static int Rank(FoundDevice f, string q)
        {
            int r = 0;
            if (q.Length > 0 && string.Equals(f.Device, q, StringComparison.OrdinalIgnoreCase)) r += 8;
            else if (q.Length > 0 && f.Device.StartsWith(q, StringComparison.OrdinalIgnoreCase)) r += 4;
            if (f.Pinned) r += 2;
            if (f.Recent) r += 1;
            return r;
        }
    }

    // What was shown in the main window, for Back and Forward: Overview, a device, or a list.
    public sealed class NavEntry
    {
        public const string Overview = "overview", Device = "device", List = "list";
        public string Kind;
        public string Title;
        // For a device: how to open it again.
        public RecentDevice Target;
        // For a list: what it holds, such as "recent" or the key of a tile.
        public string ListId;
        public object Tag;

        public string Key
        {
            get
            {
                if (Kind == Device) return Kind + "|" + (Target != null ? Target.Key : Title ?? "");
                if (Kind == List) return Kind + "|" + (ListId ?? "");
                return Kind ?? "";
            }
        }

        public static NavEntry ForOverview()
        {
            NavEntry e = new NavEntry();
            e.Kind = Overview;
            e.Title = "Overview";
            return e;
        }

        public static NavEntry ForDevice(RecentDevice target, string title)
        {
            NavEntry e = new NavEntry();
            e.Kind = Device;
            e.Target = target;
            e.Title = title ?? (target != null ? target.Device : "");
            return e;
        }

        public static NavEntry ForList(string id, string title, object tag)
        {
            NavEntry e = new NavEntry();
            e.Kind = List;
            e.ListId = id;
            e.Title = title;
            e.Tag = tag;
            return e;
        }
    }

    // A browser-like history. Visiting a new place drops what was ahead; visiting the place already shown replaces it.
    public sealed class NavHistory
    {
        public const int Max = 50;
        readonly List<NavEntry> items = new List<NavEntry>();
        int pos = -1;

        public NavEntry Current
        {
            get { return pos >= 0 && pos < items.Count ? items[pos] : null; }
        }

        public int Count
        {
            get { return items.Count; }
        }

        public bool CanBack
        {
            get { return pos > 0; }
        }

        public bool CanForward
        {
            get { return pos >= 0 && pos < items.Count - 1; }
        }

        public void Visit(NavEntry e)
        {
            if (e == null) return;
            NavEntry cur = Current;
            if (cur != null && cur.Key == e.Key) { items[pos] = e; return; }
            if (pos < items.Count - 1) items.RemoveRange(pos + 1, items.Count - pos - 1);
            items.Add(e);
            while (items.Count > Max) items.RemoveAt(0);
            pos = items.Count - 1;
        }

        public NavEntry Back()
        {
            if (!CanBack) return null;
            pos--;
            return Current;
        }

        public NavEntry Forward()
        {
            if (!CanForward) return null;
            pos++;
            return Current;
        }

        public void Clear()
        {
            items.Clear();
            pos = -1;
        }
    }
}
