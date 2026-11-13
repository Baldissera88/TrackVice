namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class Modes
    {
        public const string Live = "Live";
        public const string Folder = "Folder";
        public const string File = "File";
        public const string Demo = "Demo";
        public const string Intune = "Intune";
        public const string Sccm = "SCCM";
        public const string Agent = "Agent";
    }

    // Holds the session and where it comes from. Poll can run on a background thread; Apply runs on the UI thread.
    public sealed class Engine : IDisposable
    {
        public Session S;
        public Tracker T;
        public string Mode;
        LiveCollector live;
        DemoPlayer demo;
        public double DemoSpeed = 10;
        DateTime lastPoll = DateTime.MinValue;
        readonly HashSet<string> notes = new HashSet<string>();
        readonly HashSet<string> sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Engine ForLive(int hours)
        {
            return ForLive(hours, true, true, 2000);
        }

        // hours 0 reads everything since Windows was installed.
        public static Engine ForLive(int hours, bool wmi, bool events, int pollMs)
        {
            Engine e = new Engine();
            e.Mode = Modes.Live;
            e.S = NewSession(Modes.Live);
            e.S.SinceUtc = SinceFor(hours);
            e.live = new LiveCollector(e.S.SinceUtc);
            e.live.UseWmi = wmi;
            e.live.UseEvents = events;
            e.pollMs = Math.Max(500, pollMs);
            e.T = new Tracker(e.S);
            return e;
        }

        public static DateTime SinceFor(int hours)
        {
            DateTime now = DateTime.UtcNow;
            if (hours > 0) return now.AddHours(-hours);
            DateTime? installed = null;
            try
            {
                using (LiveRegistry reg = new LiveRegistry()) installed = AutopilotInfo.WindowsInstalled(reg);
            }
            catch (Exception) { }
            // A day before the install date, so the task sequence that installed Windows is read too.
            return installed.HasValue && installed.Value < now ? installed.Value.AddDays(-1) : now.AddDays(-30);
        }

        int pollMs = 2000;

        // Set when the deployment on this device started before the history TrackVice reads: the time it started.
        public DateTime? StartedBeforeHistory;

        public static Engine ForFolder(string folder)
        {
            Engine e = new Engine();
            e.Mode = Modes.Folder;
            e.S = NewSession(Modes.Folder);
            e.S.SourcePath = folder;
            e.S.Computer = Path.GetFileName(folder.TrimEnd('\\', '/'));
            e.S.OsVersion = "Logs from a folder";
            e.S.User = null;
            e.T = new Tracker(e.S);
            return e;
        }

        public static Engine ForFile(string path)
        {
            Engine e = new Engine();
            e.Mode = Modes.File;
            e.S = Session.Load(path);
            e.T = new Tracker(e.S);
            return e;
        }

        // Another device, through Intune or SCCM.
        public static Engine ForRemote(IRemoteFeed feed, RemoteDeployment d)
        {
            Engine e = new Engine();
            e.Mode = feed.Mode;
            e.remote = feed;
            e.S = new Session();
            e.S.Mode = feed.Mode;
            e.S.AppVersion = Program.Version;
            e.S.Computer = d.Device;
            e.S.User = feed.Mode == Modes.Intune ? d.User : null;
            e.S.OsVersion = null;
            e.S.SourcePath = d.Key;
            e.pollMs = feed.PollMs;
            e.T = new Tracker(e.S);
            return e;
        }

        IRemoteFeed remote;
        DateTime replacedAt, clockAtUpload;

        // When the device last uploaded the session that is shown.
        public DateTime LastUploadUtc;

        public bool IsRemote
        {
            get { return remote != null; }
        }

        public static Engine ForDemo(string name)
        {
            Engine e = new Engine();
            e.Mode = Modes.Demo;
            DemoScenario sc = Demo.Get(name);
            // The scenario starts so that its starting point is now.
            e.demo = new DemoPlayer(sc, DateTime.UtcNow.AddSeconds(-sc.StartAt));
            e.S = NewSession(Modes.Demo);
            e.S.Computer = sc.Computer;
            e.S.User = sc.User;
            e.S.OsVersion = sc.Os;
            e.S.Elevated = true;
            e.S.SourcePath = sc.Name;
            e.T = new Tracker(e.S);
            return e;
        }

        static Session NewSession(string mode)
        {
            Session s = new Session();
            s.Mode = mode;
            s.AppVersion = Program.Version;
            s.Computer = Environment.MachineName;
            try { s.User = Environment.UserDomainName + "\\" + Environment.UserName; } catch (Exception) { }
            s.OsVersion = Program.OsVersion();
            s.Elevated = Program.IsAdmin;
            return s;
        }

        public bool CanWatch
        {
            get { return Mode == Modes.Live || remote != null || (Mode == Modes.Demo && demo != null && !demo.Done); }
        }

        public DemoPlayer Player
        {
            get { return demo; }
        }

        // The time durations are measured against: the device clock, the demo clock, or the time a saved session was saved.
        public DateTime Now
        {
            get
            {
                if (Mode == Modes.Demo && demo != null) return demo.Clock;
                if (Mode == Modes.Agent)
                {
                    // The device's own clock at its last upload, moved on a little so durations keep counting between uploads.
                    if (clockAtUpload.Year < 2001) return DateTime.UtcNow;
                    TimeSpan since = DateTime.UtcNow - replacedAt;
                    return clockAtUpload + (since > TimeSpan.FromSeconds(90) ? TimeSpan.FromSeconds(90) : since < TimeSpan.Zero ? TimeSpan.Zero : since);
                }
                if (Mode == Modes.Live || remote != null) return DateTime.UtcNow;
                if (S.ClockUtc != DateTime.MinValue && S.ClockUtc.Year > 2000) return S.ClockUtc;
                DateTime last = DateTime.MinValue;
                foreach (TEvent e in S.Events) if (e.TimeUtc > last) last = e.TimeUtc;
                return last == DateTime.MinValue ? DateTime.UtcNow : last;
            }
        }

        public int PollIntervalMs
        {
            get { return Mode == Modes.Demo ? 400 : pollMs; }
            set { pollMs = Math.Max(500, value); }
        }

        // Reads what is new. The first call reads everything.
        public Batch Poll()
        {
            DateTime now = DateTime.UtcNow;
            Batch b;
            switch (Mode)
            {
                case Modes.Live:
                    b = live.Read(now);
                    break;
                case Modes.Demo:
                    double target = lastPoll == DateTime.MinValue ? demo.Scenario.StartAt : demo.Position + (now - lastPoll).TotalSeconds * DemoSpeed;
                    b = demo.AdvanceTo(target);
                    break;
                case Modes.Intune:
                case Modes.Sccm:
                case Modes.Agent:
                    b = remote.Read(now);
                    break;
                case Modes.Folder:
                    b = lastPoll == DateTime.MinValue ? FolderReader.Read(S.SourcePath, TimeZoneInfo.Local) : new Batch();
                    break;
                default:
                    b = new Batch();
                    break;
            }
            lastPoll = now;
            return b;
        }

        // Jumps the demo to a point, for screenshots and tests.
        public Batch DemoTo(double seconds)
        {
            lastPoll = DateTime.UtcNow;
            return demo.AdvanceTo(seconds);
        }

        // Names from Intune, kept so they survive a new upload from the device.
        readonly Dictionary<string, string> extraNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int AddNames(Dictionary<string, string> found)
        {
            foreach (KeyValuePair<string, string> kv in found) extraNames[kv.Key] = kv.Value;
            int before = T.Changes;
            T.AddNames(extraNames);
            return T.Changes - before;
        }

        public void Apply(Batch b)
        {
            if (b == null) return;
            try { ApplyBatch(b); }
            finally { if (extraNames.Count > 0) T.AddNames(extraNames); }
        }

        void ApplyBatch(Batch b)
        {
            if (b.Replace != null)
            {
                string key = S.SourcePath;
                S = b.Replace;
                S.Mode = Mode;
                S.SourcePath = key;
                T = new Tracker(S);
                clockAtUpload = S.ClockUtc;
                replacedAt = DateTime.UtcNow;
                LastUploadUtc = S.SavedUtc;
            }
            foreach (Pair p in b.Info) S.SetInfo(p.Key, p.Value);
            if (remote != null && Mode != Modes.Agent && S.GetInfo("Windows") != null) S.OsVersion = "Windows " + S.GetInfo("Windows");
            if (b.Names != null) T.AddNames(b.Names);
            T.ApplyAll(b.Obs);
            foreach (Pair a in b.Aliases) T.Alias(a.Key, a.Value);
            if (b.Names != null) T.AddNames(b.Names);
            foreach (string n in b.Notes) if (notes.Add(n)) S.Notes.Add(n);
            foreach (string src in b.Sources) sources.Add(src);
            if (Mode == Modes.Agent)
            {
                if (b.Replace != null) Finder.Run(S, clockAtUpload, true);
                return;
            }
            S.ClockUtc = Now;
            if (Mode == Modes.Live && StartedBeforeHistory == null)
            {
                DateTime? first = S.FirstUtc;
                if (first.HasValue && first.Value < S.SinceUtc.AddMinutes(-30))
                {
                    StartedBeforeHistory = first;
                    S.Notes.Add("The deployment on this device started " + Txt.Local(first.Value) + ", before the history TrackVice reads (from " + Txt.Local(S.SinceUtc) + "). Older log lines were not read, so those items only show the state saved in the registry. To read everything, set History to read to Since Windows was installed in Settings.");
                }
            }
            Finder.Run(S, Now, Mode == Modes.Live || Mode == Modes.Demo || Mode == Modes.Sccm);
        }

        public int SourceCount
        {
            get { return sources.Count; }
        }

        public List<string> Sources
        {
            get { return new List<string>(sources); }
        }

        public void Dispose()
        {
            if (live != null) live.Dispose();
        }
    }
}
