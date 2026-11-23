namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    public enum WallState
    {
        Running,
        Silent,
        Finished,
        Failed
    }

    public enum WallFilter
    {
        All,
        Running,
        Failed,
        FinishedToday
    }

    // One device on the wall, made from the metadata of its upload only.
    public sealed class WallTile
    {
        public string Key, Device, Kind, Profile, Current, Names;
        public string Version;
        public int Items, Done, FailedCount, Active;
        public DateTime? StartUtc, EndUtc, OffUtc;
        public DateTime LastUtc;
        public bool Idle;
        public WallState State;
        public StoreEntry Entry;

        public double Progress
        {
            get { return Items <= 0 ? 0 : Math.Max(0, Math.Min(1.0, (double)Done / Items)); }
        }

        // Rounded down, so a device is only at 100 % when everything is done.
        public int Percent
        {
            get { return (int)Math.Floor(Progress * 100 + 1e-9); }
        }

        public bool Final
        {
            get { return (State == WallState.Finished || State == WallState.Failed) && Active == 0; }
        }

        public string Deployment
        {
            get
            {
                if (Kind != null && Profile != null) return Kind + " · " + Profile;
                return Kind ?? Profile ?? "";
            }
        }

        public TimeSpan? Elapsed(DateTime nowUtc)
        {
            if (!StartUtc.HasValue) return null;
            DateTime end = Final && EndUtc.HasValue ? EndUtc.Value : nowUtc;
            return end < StartUtc.Value ? TimeSpan.Zero : end - StartUtc.Value;
        }

        // The line under the progress bar: what runs now, or how it ended.
        public string Line(DateTime nowUtc)
        {
            switch (State)
            {
                case WallState.Silent:
                    if (OffUtc.HasValue) return OffTime.ListText(OffUtc, nowUtc);
                    return "No upload for " + Txt.DurationShort(nowUtc - LastUtc);
                case WallState.Finished:
                    return "Done, " + Items + " item" + (Items == 1 ? "" : "s");
                case WallState.Failed:
                    return FailedCount + " failed" + (Current != null ? " · " + Current : "");
                default:
                    if (Current != null) return Current;
                    if (Items == 0) return "Nothing has started yet";
                    return Done + " of " + Items + " done, the rest is waiting";
            }
        }

        // When the device last did something worth sorting by. Devices that upload now come first.
        public DateTime Activity(DateTime nowUtc)
        {
            if (State == WallState.Running) return nowUtc;
            if (Final && EndUtc.HasValue) return EndUtc.Value;
            return LastUtc;
        }

        public bool Matches(string q)
        {
            if (string.IsNullOrEmpty(q)) return true;
            foreach (string s in new string[] { Device, Kind, Profile, Current, Names, Version })
                if (s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }

    // The counts at the top of the wall.
    public sealed class WallSummary
    {
        public int Running, Silent, FinishedToday, FailedToday;
        public TimeSpan? Average;

        public string Text()
        {
            List<string> parts = new List<string>();
            parts.Add(Running + " running");
            if (Silent > 0) parts.Add(Silent + " with no upload");
            parts.Add(FinishedToday + " finished today");
            parts.Add(FailedToday + " failed today");
            if (Average.HasValue) parts.Add("average " + Txt.DurationShort(Average) + " today");
            return string.Join("  ·  ", parts.ToArray());
        }
    }

    // The tiles of the wall: built from the list, filtered, sorted and aged out.
    public sealed class WallBoard
    {
        public static int SilentMinutes = 5;
        // When this window first saw a device finished, for uploads from agents that do not say when they ended.
        readonly Dictionary<string, DateTime> seenFinal = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public List<WallTile> Tiles = new List<WallTile>();

        public void Update(List<StoreEntry> entries, DateTime nowUtc)
        {
            List<WallTile> l = new List<WallTile>();
            foreach (StoreEntry e in entries)
            {
                if (e.Name == null || !e.Name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)) continue;
                WallTile t = WallModel.Tile(e, nowUtc);
                if (t.Final && !t.EndUtc.HasValue)
                {
                    DateTime seen;
                    if (!seenFinal.TryGetValue(t.Key, out seen)) { seen = nowUtc; seenFinal[t.Key] = seen; }
                    t.EndUtc = seen;
                }
                l.Add(t);
            }
            Tiles = l;
        }

        public List<WallTile> Shown(WallFilter filter, string search, DateTime nowUtc, DateTime todayStartUtc, TimeSpan keepFinished)
        {
            List<WallTile> l = new List<WallTile>();
            foreach (WallTile t in Tiles)
            {
                if (!t.Matches(search == null ? null : search.Trim())) continue;
                bool keep;
                switch (filter)
                {
                    case WallFilter.Running: keep = (t.State == WallState.Running || t.State == WallState.Silent) && WallModel.Visible(t, nowUtc, todayStartUtc, keepFinished); break;
                    case WallFilter.Failed: keep = t.State == WallState.Failed && WallModel.Visible(t, nowUtc, todayStartUtc, keepFinished); break;
                    case WallFilter.FinishedToday: keep = t.State == WallState.Finished && WallModel.Today(t, todayStartUtc); break;
                    default: keep = WallModel.Visible(t, nowUtc, todayStartUtc, keepFinished); break;
                }
                if (keep) l.Add(t);
            }
            WallModel.Sort(l, nowUtc);
            return l;
        }

        public WallSummary Summary(DateTime nowUtc, DateTime todayStartUtc)
        {
            return WallModel.Summarize(Tiles, nowUtc, todayStartUtc);
        }
    }

    public static class WallModel
    {
        // When the last item of a session ended, or null while anything still runs or waits.
        public static DateTime? Ended(Session s)
        {
            if (s == null || s.Items.Count == 0) return null;
            DateTime? m = null;
            foreach (Item i in s.Items)
            {
                if (!States.IsFinal(i.State) && i.State != State.WaitingReboot) return null;
                DateTime t = i.EndUtc ?? i.LastUtc;
                if (m == null || t > m.Value) m = t;
            }
            return m;
        }

        static string Str(StoreEntry e, string key)
        {
            string v;
            return e.Meta.TryGetValue(key, out v) && v != null && v.Length > 0 ? v : null;
        }

        static int Int(StoreEntry e, string key)
        {
            string v;
            int n;
            return e.Meta.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        static DateTime? Time(StoreEntry e, string key)
        {
            string v;
            DateTime t;
            if (e.Meta.TryGetValue(key, out v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return null;
        }

        public static WallTile Tile(StoreEntry e, DateTime nowUtc)
        {
            WallTile t = new WallTile();
            t.Entry = e;
            t.Key = e.Name;
            t.Device = Str(e, "computer") ?? Path.GetFileName(e.Name);
            t.Kind = Str(e, "kind");
            t.Profile = Str(e, "profile");
            t.Current = Str(e, "current");
            t.Names = Str(e, "names");
            t.Version = Str(e, "version");
            t.Items = Int(e, "items");
            t.Done = Int(e, "done");
            t.FailedCount = Int(e, "failed");
            t.Active = Int(e, "active");
            t.Idle = Str(e, "idle") == "1";
            t.StartUtc = Time(e, "started");
            t.LastUtc = Time(e, "updated") ?? e.ModifiedUtc;
            if (nowUtc - t.LastUtc > TimeSpan.FromMinutes(2)) t.OffUtc = Time(e, OffTime.MetaKey);
            if (t.FailedCount > 0) t.State = WallState.Failed;
            else if (t.Items > 0 && t.Active == 0 && t.Done >= t.Items) t.State = WallState.Finished;
            else if (!t.Idle && nowUtc - t.LastUtc > TimeSpan.FromMinutes(WallBoard.SilentMinutes)) t.State = WallState.Silent;
            else t.State = WallState.Running;
            if (t.Final) t.EndUtc = Time(e, "ended");
            return t;
        }

        // Finished devices stay for a while, failed ones until midnight, the rest while they upload today.
        public static bool Visible(WallTile t, DateTime nowUtc, DateTime todayStartUtc, TimeSpan keepFinished)
        {
            switch (t.State)
            {
                case WallState.Finished:
                    return nowUtc - (t.EndUtc ?? t.LastUtc) <= keepFinished;
                case WallState.Failed:
                    return Today(t, todayStartUtc);
                case WallState.Silent:
                    return t.LastUtc >= todayStartUtc;
                default:
                    return true;
            }
        }

        // Ended today, or for a device that still runs, uploaded today.
        public static bool Today(WallTile t, DateTime todayStartUtc)
        {
            DateTime when = t.Final && t.EndUtc.HasValue ? t.EndUtc.Value : t.LastUtc;
            return when >= todayStartUtc;
        }

        public static void Sort(List<WallTile> l, DateTime nowUtc)
        {
            l.Sort(delegate (WallTile a, WallTile b)
            {
                int c = b.Activity(nowUtc).CompareTo(a.Activity(nowUtc));
                if (c != 0) return c;
                c = (b.StartUtc ?? DateTime.MinValue).CompareTo(a.StartUtc ?? DateTime.MinValue);
                if (c != 0) return c;
                return string.Compare(a.Device, b.Device, StringComparison.OrdinalIgnoreCase);
            });
        }

        public static WallSummary Summarize(List<WallTile> tiles, DateTime nowUtc, DateTime todayStartUtc)
        {
            WallSummary s = new WallSummary();
            long ticks = 0;
            int timed = 0;
            foreach (WallTile t in tiles)
            {
                if (t.State == WallState.Running) s.Running++;
                else if (t.State == WallState.Silent && t.LastUtc >= todayStartUtc) s.Silent++;
                else if (t.State == WallState.Failed && Today(t, todayStartUtc)) s.FailedToday++;
                else if (t.State == WallState.Finished && Today(t, todayStartUtc))
                {
                    s.FinishedToday++;
                    TimeSpan? d = t.Elapsed(nowUtc);
                    if (d.HasValue) { ticks += d.Value.Ticks; timed++; }
                }
            }
            if (timed > 0) s.Average = TimeSpan.FromTicks(ticks / timed);
            return s;
        }

        // The start of today on this PC, in UTC.
        public static DateTime TodayStartUtc(DateTime nowUtc)
        {
            return nowUtc.ToLocalTime().Date.ToUniversalTime();
        }
    }

    // Where each tile goes: as many columns as fit the width, rows made lower when there are many devices.
    public sealed class WallGrid
    {
        public int Columns, Rows, TileW, TileH, Gap, Shown, Hidden;

        public static WallGrid Fit(int count, int width, int height, int minTileW, int tileH, int minTileH, int gap)
        {
            WallGrid g = new WallGrid();
            g.Gap = gap;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            g.Columns = Math.Max(1, (width + gap) / (minTileW + gap));
            g.TileW = Math.Max(1, (width - gap * (g.Columns - 1)) / g.Columns);
            g.TileH = tileH;
            int need = Math.Max(1, (count + g.Columns - 1) / g.Columns);
            int fit = Math.Max(1, (height + gap) / (tileH + gap));
            if (need > fit)
            {
                int h = (height - gap * (need - 1)) / need;
                if (h >= minTileH) { g.TileH = h; fit = need; }
                else { g.TileH = minTileH; fit = Math.Max(1, (height + gap) / (minTileH + gap)); }
            }
            g.Rows = Math.Min(need, fit);
            g.Shown = Math.Min(count, g.Columns * fit);
            g.Hidden = count - g.Shown;
            return g;
        }

        public void Cell(int i, out int x, out int y)
        {
            x = (i % Columns) * (TileW + Gap);
            y = (i / Columns) * (TileH + Gap);
        }
    }

    // The demo devices with the agent, and a few that finished, failed or stopped uploading earlier today.
    public sealed class WallDemoStore : ISessionStore
    {
        readonly DemoAgentStore live = new DemoAgentStore();
        readonly List<StoreEntry> fixedEntries = new List<StoreEntry>();
        readonly Dictionary<string, byte[]> fixedData = new Dictionary<string, byte[]>();

        public WallDemoStore()
        {
            DateTime now = DateTime.UtcNow;
            Add("autopilot", "CONTOSO-LT0371", 9999 * 60, true, now.AddMinutes(-68), now.AddMinutes(-21), now.AddSeconds(-40));
            Add("autopilot", "CONTOSO-LT0362", 9999 * 60, true, now.AddMinutes(-104), now.AddMinutes(-52), now.AddMinutes(-2));
            Add("osd", "CONTOSO-PC181", 9999 * 60, false, now.AddMinutes(-96), now.AddMinutes(-37), now.AddMinutes(-1));
            Add("autopilot", "CONTOSO-LT0429", 9 * 60, false, now.AddMinutes(-19), null, now.AddMinutes(-7));
            Add("autopilot", "CONTOSO-LT0340", 9999 * 60, true, now.AddMinutes(-240), now.AddMinutes(-185), now.AddMinutes(-4));
        }

        void Add(string scenario, string computer, double seconds, bool allDone, DateTime started, DateTime? ended, DateTime updated)
        {
            Engine e = Engine.ForDemo(scenario);
            e.Apply(e.DemoTo(seconds));
            Session s = e.S;
            s.Computer = computer;
            AgentVersions.Demo(s, computer, DateTime.UtcNow);
            if (allDone)
            {
                // A made-up device on which everything went well.
                foreach (Item i in s.Items)
                    if (!States.IsComplete(i.State)) { i.State = State.Succeeded; i.Percent = null; i.ExitCode = 0; i.EndUtc = i.LastUtc; }
                s.Findings.Clear();
            }
            string name = AgentData.BlobName(computer.ToLowerInvariant());
            StoreEntry en = new StoreEntry();
            en.Name = name;
            en.ModifiedUtc = updated;
            foreach (KeyValuePair<string, string> kv in AgentData.Summary(s, updated, ended.HasValue)) if (kv.Value != null) en.Meta[kv.Key] = kv.Value;
            en.Meta["started"] = Iso(started);
            if (ended.HasValue) en.Meta["ended"] = Iso(ended.Value);
            else en.Meta.Remove("ended");
            fixedEntries.Add(en);
            fixedData[name] = AgentData.Pack(s);
        }

        static string Iso(DateTime t)
        {
            return t.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        public string Title
        {
            get { return "Demo data"; }
        }

        public void Put(string name, byte[] data, Dictionary<string, string> meta)
        {
        }

        public List<StoreEntry> List(string prefix)
        {
            List<StoreEntry> l = live.List(prefix);
            if (prefix == null || prefix.StartsWith(AgentData.Prefix, StringComparison.OrdinalIgnoreCase)) l.AddRange(fixedEntries);
            return l;
        }

        public byte[] Get(string name)
        {
            byte[] d;
            if (name != null && fixedData.TryGetValue(name, out d)) return d;
            return live.Get(name);
        }
    }
}
