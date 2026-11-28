namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Net.NetworkInformation;
    using System.Security.AccessControl;
    using System.Security.Cryptography;
    using System.Security.Principal;
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
            if (o.Alert != null && AlertSender.Problem(o.Alert) != null) throw new ArgumentException(AlertSender.Problem(o.Alert));
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
            if (!string.IsNullOrWhiteSpace(o.Alert)) b.Append(" /alert ").Append(Quote(o.Alert));
            return b.ToString();
        }

        // One argument in quotes. Backslashes before the closing quote are doubled, so a folder such as \\server\share\ does not
        // escape the quote and take the next arguments into its value.
        public static string Quote(string s)
        {
            string v = (s ?? "").Trim().Replace("\"", "");
            int tail = 0;
            while (tail < v.Length && v[v.Length - 1 - tail] == '\\') tail++;
            return "\"" + v + new string('\\', tail) + "\"";
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

        // Only a mutex of SYSTEM or the administrators is the agent. Any user can make one by that name first.
        public bool Running()
        {
            Mutex m;
            try
            {
                if (!Mutex.TryOpenExisting(MutexName, MutexRights.Synchronize | MutexRights.ReadPermissions, out m)) return false;
            }
            catch (UnauthorizedAccessException) { return false; }
            using (m) return AgentGuard.Trusted(m);
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
                if (AgentFolder.Secure(TsAgent.LocalFolder, new WinFolderAcl(), null)) File.WriteAllText(TsAgent.StopFile, "stop");
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
        // Set once Run made LocalFolder SYSTEM's own. Until then, and when it could not, nothing is written there.
        static bool folderReady;

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
                if (!folderReady) return;
                string path = Path.Combine(LocalFolder, "agent.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine);
            }
            catch (Exception) { }
        }

        static bool StopAsked(EventWaitHandle ev)
        {
            try { if (ev != null && ev.WaitOne(0)) return true; } catch (Exception) { }
            try { return folderReady && File.Exists(StopFile); } catch (Exception) { return false; }
        }

        // The state folders the agent may write to as SYSTEM: each made SYSTEM's own first. One that cannot be is left out.
        static List<string> Secured(List<string> folders, HashSet<string> done)
        {
            List<string> l = new List<string>();
            foreach (string f in folders)
            {
                if (done.Contains(f) || AgentFolder.Secure(f, new WinFolderAcl(), Log))
                {
                    done.Add(f);
                    l.Add(f);
                }
            }
            return l;
        }

        public static int Run(AgentOptions o)
        {
            bool created;
            using (Mutex one = AgentGuard.Single(WinAgentHost.MutexName, out created))
            {
                if (!created)
                {
                    Console.WriteLine("The TrackVice agent is already running.");
                    return 0;
                }
                // Windows\Temp lets every user add files and folders. The agent writes and deletes in its folder as SYSTEM.
                folderReady = AgentFolder.Secure(LocalFolder, new WinFolderAcl(), null);
                if (one == null) Log("Another program holds the name of the agent's mutex. It is not the agent's: going on without it.");
                EventWaitHandle stop = AgentGuard.StopEvent(WinAgentHost.StopName, Log);
                try { if (folderReady) File.Delete(StopFile); } catch (Exception) { }
                try { return Loop(o, stop); }
                finally { if (stop != null) stop.Dispose(); }
            }
        }

        static int Loop(AgentOptions o, EventWaitHandle stop)
        {
            bool pe = InWinPE;
            ISessionStore store = Stores.Open(o.Upload);
            HashSet<string> secured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> folders = Secured(StateFolders(), secured);
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
            AgentAlerts alerts = null;
            if (!string.IsNullOrWhiteSpace(o.Alert))
            {
                string url = o.Alert.Trim();
                // Sent alerts are remembered in the agent's folder, so a restart in Windows does not send them again.
                alerts = new AgentAlerts(folderReady ? AgentAlerts.StatePath(LocalFolder) : null, sid, started.AddHours(-2), delegate (Alert x) { AlertSender.Send(url, x); }, Log);
                alerts.Mask = o.Mask;
            }
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
                        List<string> f = Secured(StateFolders(), secured);
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
                    bool idle = a.Idle(e.S, now, o.IdleMinutes);
                    if (alerts != null) alerts.Cycle(e.S, idle, now);
                    logs.Cycle(e.S, idle, last, now);
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

    // ---------- what a standard user on the device must not be able to do to the agent ----------

    public static class AgentGuard
    {
        // SYSTEM or the administrators. A standard user cannot make either the owner of what they create.
        public static bool TrustedOwner(IdentityReference owner)
        {
            SecurityIdentifier sid = owner as SecurityIdentifier;
            return sid != null && (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }

        public static bool Trusted(Mutex m)
        {
            try { return TrustedOwner(m.GetAccessControl().GetOwner(typeof(SecurityIdentifier))); }
            catch (Exception) { return false; }
        }

        static bool Trusted(EventWaitHandle e)
        {
            try { return TrustedOwner(e.GetAccessControl().GetOwner(typeof(SecurityIdentifier))); }
            catch (Exception) { return false; }
        }

        // The agent's single-instance mutex. alone is false only when a real agent holds it. A mutex by that name that a user
        // made first is not the agent's: then the result is null and alone is true, so a user cannot keep the agent from starting.
        public static Mutex Single(string name, out bool alone)
        {
            Mutex m = null;
            try
            {
                m = new Mutex(true, name, out alone);
                if (alone || Trusted(m)) return m;
            }
            catch (UnauthorizedAccessException) { }
            if (m != null) m.Dispose();
            alone = true;
            return null;
        }

        // The event /agent-stop sets. One a user made first is not used, so a user cannot stop the agent; the stop file in the
        // agent's own folder still works.
        public static EventWaitHandle StopEvent(string name, Action<string> log)
        {
            try
            {
                bool made;
                EventWaitHandle e = new EventWaitHandle(false, EventResetMode.ManualReset, name, out made);
                if (made || Trusted(e)) return e;
                e.Dispose();
                if (log != null) log("Another program made the stop event of the agent. It is not used.");
            }
            catch (Exception ex) { if (log != null) log("No stop event: " + ex.Message); }
            return null;
        }
    }

    // Who owns a file or folder, and making a folder SYSTEM's and the administrators' alone. Tests fake it.
    public interface IFolderAcl
    {
        bool Trusted(string path);
        void Restrict(string folder);
    }

    public sealed class WinFolderAcl : IFolderAcl
    {
        public bool Trusted(string path)
        {
            FileSystemSecurity sec = Directory.Exists(path) ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Owner) : File.GetAccessControl(path, AccessControlSections.Owner);
            return AgentGuard.TrustedOwner(sec.GetOwner(typeof(SecurityIdentifier)));
        }

        public void Restrict(string folder)
        {
            DirectorySecurity sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            foreach (WellKnownSidType w in new WellKnownSidType[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(w, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(folder, sec);
        }
    }

    // A folder the agent writes to and deletes in as SYSTEM, in a place where users can add files and folders (Windows\Temp).
    public static class AgentFolder
    {
        public static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        static bool There(string path)
        {
            return File.Exists(path) || Directory.Exists(path) || IsLink(path);
        }

        // Removes a file or folder without following a link: a link is removed itself, never what it points at.
        public static void Remove(string path)
        {
            FileAttributes a = File.GetAttributes(path);
            bool dir = (a & FileAttributes.Directory) != 0;
            if ((a & FileAttributes.ReparsePoint) != 0)
            {
                if (dir) Directory.Delete(path);
                else File.Delete(path);
                return;
            }
            if (!dir)
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            foreach (string e in Directory.GetFileSystemEntries(path)) Remove(e);
            Directory.Delete(path);
        }

        // Makes the folder, or keeps it when SYSTEM or the administrators made it, and leaves only SYSTEM and the
        // administrators access to it. A folder a user made or a link is removed first, and so is anything in it a user made
        // or that is a link, so that nothing the agent writes or deletes there can be pointed elsewhere. False when the
        // folder cannot be made safe: then the agent writes nothing there.
        public static bool Secure(string folder, IFolderAcl acl, Action<string> log)
        {
            try
            {
                if (There(folder) && (IsLink(folder) || !Directory.Exists(folder) || !acl.Trusted(folder)))
                {
                    Remove(folder);
                    if (log != null) log("Removed " + folder + ": it was not made by the agent.");
                }
                Directory.CreateDirectory(folder);
                if (IsLink(folder)) return false;
                acl.Restrict(folder);
                foreach (string e in Directory.GetFileSystemEntries(folder))
                    if (IsLink(e) || !acl.Trusted(e))
                    {
                        Remove(e);
                        if (log != null) log("Removed " + Path.GetFileName(e) + " from the agent's folder: it was not made by the agent.");
                    }
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("The folder " + folder + " cannot be used: " + ex.Message);
                return false;
            }
        }
    }
}
