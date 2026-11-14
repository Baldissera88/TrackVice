namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Net.NetworkInformation;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using Microsoft.Win32;

    // ---------- the agent in an SCCM task sequence ----------

    // What identifies the device across WinPE, full Windows and a new computer name.
    public interface IDeviceIdSource
    {
        string SmbiosUuid();
        List<string> WiredMacs();
    }

    public sealed class WmiDeviceId : IDeviceIdSource
    {
        public string SmbiosUuid()
        {
            try
            {
                using (System.Management.ManagementObjectSearcher q = new System.Management.ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                    foreach (System.Management.ManagementBaseObject o in q.Get())
                    {
                        string v = o["UUID"] as string;
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
            }
            catch (Exception) { }
            return null;
        }

        public List<string> WiredMacs()
        {
            List<string> l = new List<string>();
            try
            {
                foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.NetworkInterfaceType != NetworkInterfaceType.Ethernet && n.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet) continue;
                    byte[] a = n.GetPhysicalAddress().GetAddressBytes();
                    if (a.Length == 6) l.Add(BitConverter.ToString(a).Replace("-", ""));
                }
            }
            catch (Exception) { }
            return l;
        }
    }

    public static class TsId
    {
        // UUIDs that firmware reports when it has no real one.
        static readonly string[] Bogus = { "00000000-0000-0000-0000-000000000000", "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", "03000200-0400-0500-0006-000700080009" };

        // The ID given with /id, else one from the SMBIOS UUID, else from the lowest wired MAC address. Null when there is none.
        // The hardware values are hashed, so the name of the upload does not show them.
        public static string Derive(string given, IDeviceIdSource src)
        {
            string g = Clean(given);
            if (g != null) return g;
            if (src == null) return null;
            string uuid = (src.SmbiosUuid() ?? "").Trim().Trim('{', '}').ToUpperInvariant();
            if (uuid.Length >= 32 && Array.IndexOf(Bogus, uuid) < 0) return "ts-" + Hash("uuid:" + uuid);
            List<string> macs = src.WiredMacs() ?? new List<string>();
            string best = null;
            foreach (string m in macs)
            {
                string v = (m ?? "").Replace(":", "").Replace("-", "").ToUpperInvariant();
                if (v.Length != 12 || v == "000000000000") continue;
                if (best == null || string.CompareOrdinal(v, best) < 0) best = v;
            }
            return best != null ? "ts-" + Hash("mac:" + best) : null;
        }

        // Keeps letters, digits, dash, dot and underscore, so the ID is a safe name for a file or blob.
        public static string Clean(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            StringBuilder b = new StringBuilder();
            foreach (char c in id.Trim())
            {
                if ((char.IsLetterOrDigit(c) && c < 128) || c == '-' || c == '.' || c == '_') b.Append(c);
                else if (b.Length > 0 && b[b.Length - 1] != '-') b.Append('-');
            }
            string s = b.ToString().Trim('-', '.');
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Length > 0 ? s : null;
        }

        static string Hash(string s)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(h, 0, 10).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    // What the agent keeps between WinPE and full Windows: the session ID and every computer name the device had.
    public sealed class TsState
    {
        public string Id;
        public readonly List<string> Names = new List<string>();

        // Adds the name when it is new. Returns true when it was.
        public bool Remember(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (string n in Names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return false;
            Names.Add(name.Trim());
            return true;
        }

        // The names before the current one, oldest first.
        public string Earlier(string current)
        {
            List<string> l = new List<string>();
            foreach (string n in Names) if (!string.Equals(n, current, StringComparison.OrdinalIgnoreCase)) l.Add(n);
            return l.Count > 0 ? string.Join(", ", l.ToArray()) : null;
        }

        public string ToText()
        {
            StringBuilder b = new StringBuilder();
            if (Id != null) b.Append("id=").Append(Id).Append("\r\n");
            foreach (string n in Names) b.Append("name=").Append(n).Append("\r\n");
            return b.ToString();
        }

        public static TsState Parse(string text)
        {
            TsState s = new TsState();
            foreach (string line in (text ?? "").Split('\n'))
            {
                string l = line.Trim();
                if (l.StartsWith("id=", StringComparison.Ordinal)) s.Id = TsId.Clean(l.Substring(3));
                else if (l.StartsWith("name=", StringComparison.Ordinal)) s.Remember(l.Substring(5));
            }
            return s;
        }

        // The first folder with a state file wins.
        public static TsState Load(List<string> folders)
        {
            foreach (string f in folders)
            {
                try
                {
                    string p = Path.Combine(f, "state.txt");
                    if (File.Exists(p)) return Parse(File.ReadAllText(p));
                }
                catch (Exception) { }
            }
            return new TsState();
        }

        public void Save(List<string> folders)
        {
            foreach (string f in folders)
            {
                try
                {
                    Directory.CreateDirectory(f);
                    File.WriteAllText(Path.Combine(f, "state.txt"), ToText());
                }
                catch (Exception) { }
            }
        }

        public static void Delete(List<string> folders)
        {
            foreach (string f in folders)
            {
                try { File.Delete(Path.Combine(f, "state.txt")); } catch (Exception) { }
            }
        }
    }

    // Decides when the task sequence is over, so the agent can make its last upload and stop.
    public static class TsEnd
    {
        // Null while the task sequence goes on, else why it is over: smsts.log said so, or no step has run for quietMinutes.
        public static string Check(Session s, DateTime nowUtc, DateTime agentStartUtc, int quietMinutes)
        {
            bool any = false;
            DateTime lastStep = DateTime.MinValue;
            foreach (Item i in s.Items)
            {
                if (i.Kind != Kinds.Step) continue;
                if (States.IsActive(i.State)) return null;
                any = true;
                if (i.LastUtc > lastStep) lastStep = i.LastUtc;
            }
            string ended = s.GetInfo("Task sequence ended");
            DateTime t;
            if (ended != null && DateTime.TryParse(ended, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
            {
                // An end from an earlier run does not count when a step ran after it.
                if (lastStep <= t.AddMinutes(1)) return "The task sequence ended: " + (s.GetInfo("Task sequence result") ?? "no result") + ".";
            }
            if (!any || quietMinutes <= 0) return null;
            TimeSpan quiet = TimeSpan.FromMinutes(quietMinutes);
            if (nowUtc - agentStartUtc >= quiet && nowUtc - lastStep >= quiet)
                return "No task sequence step has run for " + quietMinutes + " minutes.";
            return null;
        }
    }

    // Starting and stopping the agent from task sequence steps. The host does the real work, so tests can fake it.
    public interface IAgentHost
    {
        bool Running();
        void Launch(string args);
        void RequestStop();
        bool WaitGone(int ms);
    }

    public static class AgentControl
    {
        public static int Start(AgentOptions o, IAgentHost h, Action<string> say)
        {
            if (string.IsNullOrWhiteSpace(o.Upload)) throw new ArgumentException("Give the upload location: /agent-start /upload <SAS link or folder>");
            Stores.Open(o.Upload);
            if (h.Running())
            {
                say("The TrackVice agent is already running.");
                return 0;
            }
            h.Launch(ChildArgs(o));
            say("The TrackVice agent started. It stops by itself when the task sequence ends, or with /agent-stop.");
            return 0;
        }

        public static int Stop(IAgentHost h, int waitMs, Action<string> say)
        {
            if (!h.Running())
            {
                say("The TrackVice agent is not running.");
                return 0;
            }
            h.RequestStop();
            if (h.WaitGone(waitMs))
            {
                say("The TrackVice agent made its last upload and stopped.");
                return 0;
            }
            say("The TrackVice agent did not stop within " + (waitMs / 1000) + " seconds. An agent installed with /agent-install does not stop this way.");
            return 1;
        }

        public static string ChildArgs(AgentOptions o)
        {
            StringBuilder b = new StringBuilder("/agent /ts /upload ").Append(Quote(o.Upload));
            if (!string.IsNullOrWhiteSpace(o.Id)) b.Append(" /id ").Append(Quote(o.Id));
            if (o.KeepHours != new AgentOptions().KeepHours) b.Append(" /keep ").Append(o.KeepHours.ToString(CultureInfo.InvariantCulture));
            if (!o.Mask) b.Append(" /no-mask");
            return b.ToString();
        }

        static string Quote(string s)
        {
            return "\"" + (s ?? "").Trim().Replace("\"", "") + "\"";
        }
    }

    // The real host: a named mutex says the agent runs, a named event or a file asks it to stop.
    public sealed class WinAgentHost : IAgentHost
    {
        public const string MutexName = @"Global\TrackViceAgent";
        public const string StopName = @"Global\TrackViceAgentStop";
        readonly string exe;

        public WinAgentHost(string exePath)
        {
            exe = exePath;
        }

        public bool Running()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(MutexName, out m)) return false;
            m.Dispose();
            return true;
        }

        public void Launch(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            Process.Start(psi).Dispose();
        }

        public void RequestStop()
        {
            try
            {
                Directory.CreateDirectory(TsAgent.LocalFolder);
                File.WriteAllText(TsAgent.StopFile, "stop");
            }
            catch (Exception) { }
            EventWaitHandle ev;
            if (EventWaitHandle.TryOpenExisting(StopName, out ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }

        public bool WaitGone(int ms)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (!Running()) return true;
                Thread.Sleep(500);
            }
            return !Running();
        }
    }

    // The agent run by /agent-start in a task sequence: no install, one session from WinPE to the end, and it ends with the task sequence.
    public static class TsAgent
    {
        public static int QuietMinutes = 30;

        public static string LocalFolder
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), "TrackVice"); }
        }

        public static string StopFile
        {
            get { return Path.Combine(LocalFolder, "stop"); }
        }

        public static bool InWinPE
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT")) return k != null;
                }
                catch (Exception) { return false; }
            }
        }

        // Where the state is kept: this Windows, and in WinPE also the Windows being installed, so it is there after the restart.
        public static List<string> StateFolders()
        {
            List<string> l = new List<string>();
            l.Add(LocalFolder);
            if (!InWinPE) return l;
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        string root = d.RootDirectory.FullName;
                        if (string.Equals(root, Path.GetPathRoot(LocalFolder), StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(Path.Combine(root, @"Windows\System32\config\SYSTEM"))) l.Add(Path.Combine(root, @"Windows\Temp\TrackVice"));
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return l;
        }

        static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(LocalFolder);
                string path = Path.Combine(LocalFolder, "agent.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine);
            }
            catch (Exception) { }
        }

        static bool StopAsked(EventWaitHandle ev)
        {
            try { if (ev != null && ev.WaitOne(0)) return true; } catch (Exception) { }
            try { return File.Exists(StopFile); } catch (Exception) { return false; }
        }

        public static int Run(AgentOptions o)
        {
            bool created;
            using (Mutex one = new Mutex(true, WinAgentHost.MutexName, out created))
            {
                if (!created)
                {
                    Console.WriteLine("The TrackVice agent is already running.");
                    return 0;
                }
                EventWaitHandle stop = null;
                try { stop = new EventWaitHandle(false, EventResetMode.ManualReset, WinAgentHost.StopName); } catch (Exception ex) { Log("No stop event: " + ex.Message); }
                try { File.Delete(StopFile); } catch (Exception) { }
                try { return Loop(o, stop); }
                finally { if (stop != null) stop.Dispose(); }
            }
        }

        static int Loop(AgentOptions o, EventWaitHandle stop)
        {
            bool pe = InWinPE;
            ISessionStore store = Stores.Open(o.Upload);
            List<string> folders = StateFolders();
            TsState state = TsState.Load(folders);
            string sid = TsId.Derive(o.Id, new WmiDeviceId()) ?? state.Id ?? Guid.NewGuid().ToString("N");
            state.Id = sid;
            state.Remember(Environment.MachineName);
            state.Save(folders);
            DateTime started = DateTime.UtcNow, until = started.AddHours(o.KeepHours);
            Log("Agent " + Program.Version + " started " + (pe ? "in WinPE" : "in Windows") + ", session " + sid + ", uploading to " + store.Title);

            Agent a = new Agent(store, sid, started);
            bool mask = o.Mask;
            LogUploads logs = new LogUploads(store, AgentData.BlobName(sid), delegate (Session x) { return LogBundle.Make(mask ? Secrets.MaskedCopy(x) : x); }, Log);
            string shownError = null;
            int cycles = 0;
            using (Engine e = Engine.ForLive(0, !pe, !pe, 2000))
            {
                while (true)
                {
                    DateTime now = DateTime.UtcNow;
                    try { e.Apply(e.Poll()); }
                    catch (Exception ex) { Log("Reading failed: " + ex.Message); }
                    // The name changes when WinPE gives way to Windows, and may change again later.
                    string name = Environment.MachineName;
                    if (++cycles % 15 == 0 && pe)
                    {
                        List<string> f = StateFolders();
                        if (f.Count != folders.Count) { folders = f; state.Save(folders); }
                    }
                    if (state.Remember(name)) state.Save(folders);
                    e.S.Computer = name;
                    string earlier = state.Earlier(name);
                    if (earlier != null) e.S.SetInfo("Earlier names", earlier);

                    string why = StopAsked(stop) ? "Asked to stop." : TsEnd.Check(e.S, now, started, QuietMinutes);
                    if (why == null && now >= until) why = "Time is up after " + o.KeepHours + " hours.";
                    bool last = o.Once || why != null;
                    if (a.Cycle(e.S, now, o, last) && a.Uploads == 1) Log("First upload done, " + e.S.Items.Count + " items.");
                    if (a.LastError != shownError)
                    {
                        shownError = a.LastError;
                        Log(shownError != null ? "Upload failed: " + shownError : "Upload works again.");
                    }
                    logs.Cycle(e.S, a.Idle(e.S, now, o.IdleMinutes), last, now);
                    if (last)
                    {
                        if (why != null) Log(why + " Last upload " + (a.LastError == null ? "done." : "failed: " + a.LastError));
                        // A finished task sequence: the next one on this device starts with fresh names.
                        if (why != null && why.StartsWith("The task sequence ended", StringComparison.Ordinal)) TsState.Delete(folders);
                        break;
                    }
                    if (stop != null) stop.WaitOne(2000);
                    else Thread.Sleep(2000);
                }
            }
            return a.LastError == null ? 0 : 1;
        }
    }
}
