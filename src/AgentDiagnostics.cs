namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Security.AccessControl;
    using System.Security.Principal;
    using System.Text;
    using System.Threading;

    // ---------- the programs the agent may start ----------

    // Everything "Collect diagnostics" can start on a device. There is nothing else: a request only says which of the
    // parts it wants, and no text of a request is ever part of a program name, an argument or a path.
    public enum DiagTool
    {
        Dsreg,
        ImeWin32Apps,
        ImeSideCarPolicies,
        MdmDiagnostics
    }

    public static class DiagCommands
    {
        public const string MdmArea = "Autopilot;DeviceEnrollment;DeviceProvisioning;TPM";
        public const string CabName = "MDMDiagnostics.cab";
        public const int MdmSeconds = 120;
        public const string Win32AppsKey = @"HKLM\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps";
        public const string SideCarKey = @"HKLM\SOFTWARE\Microsoft\IntuneManagementExtension\SideCarPolicies";

        // The file name in the Windows system folder.
        public static string Exe(DiagTool t)
        {
            switch (t)
            {
                case DiagTool.Dsreg: return "dsregcmd.exe";
                case DiagTool.ImeWin32Apps:
                case DiagTool.ImeSideCarPolicies: return "reg.exe";
                case DiagTool.MdmDiagnostics: return "MdmDiagnosticsTool.exe";
                default: throw new ArgumentException("Not a program TrackVice starts.");
            }
        }

        public static string RegKey(DiagTool t)
        {
            return t == DiagTool.ImeWin32Apps ? Win32AppsKey : t == DiagTool.ImeSideCarPolicies ? SideCarKey : null;
        }

        // The arguments. cabFolder is the agent's own temporary folder and only matters to MdmDiagnostics.
        public static string Arguments(DiagTool t, string cabFolder)
        {
            switch (t)
            {
                case DiagTool.Dsreg: return "/status";
                case DiagTool.ImeWin32Apps:
                case DiagTool.ImeSideCarPolicies: return "query \"" + RegKey(t) + "\" /s";
                case DiagTool.MdmDiagnostics:
                    if (string.IsNullOrEmpty(cabFolder) || cabFolder.IndexOf('"') >= 0) throw new ArgumentException("The folder for the cab cannot be used.");
                    return "-area \"" + MdmArea + "\" -cab \"" + Path.Combine(cabFolder, CabName) + "\"";
                default: throw new ArgumentException("Not a program TrackVice starts.");
            }
        }

        public static int TimeoutMs(DiagTool t)
        {
            return t == DiagTool.MdmDiagnostics ? MdmSeconds * 1000 : t == DiagTool.Dsreg ? 30000 : 60000;
        }

        // The longest the programs can take together, with room to spare.
        public static TimeSpan Longest
        {
            get { return TimeSpan.FromMilliseconds(TimeoutMs(DiagTool.Dsreg) + 2 * TimeoutMs(DiagTool.ImeWin32Apps) + TimeoutMs(DiagTool.MdmDiagnostics)) + TimeSpan.FromMinutes(2); }
        }
    }

    // Starts the programs of DiagCommands. Faked in tests, so nothing is started there.
    public interface IDiagRunner
    {
        bool Exists(DiagTool tool);
        // Runs the program and returns its exit code and what it wrote. Throws TimeoutException, after stopping it, when it
        // runs longer than DiagCommands.TimeoutMs.
        int Run(DiagTool tool, string cabFolder, out string output);
    }

    public sealed class WinDiagRunner : IDiagRunner
    {
        // More text than this from one program is cut.
        public const int MaxOutputChars = 8 * 1024 * 1024;

        // Always the program in the Windows system folder, by its full path: never one found through PATH or the current folder.
        static string PathOf(DiagTool tool)
        {
            return Path.Combine(Environment.SystemDirectory, DiagCommands.Exe(tool));
        }

        public bool Exists(DiagTool tool)
        {
            return File.Exists(PathOf(tool));
        }

        // Adds a line of output, and never more than max characters in all: one very long line is cut too.
        public static void AddLine(StringBuilder sb, string line, int max)
        {
            int left = max - sb.Length;
            if (left <= 0 || line == null) return;
            if (line.Length > left) sb.Append(line, 0, left);
            else sb.AppendLine(line);
        }

        public int Run(DiagTool tool, string cabFolder, out string output)
        {
            int timeoutMs = DiagCommands.TimeoutMs(tool);
            ProcessStartInfo psi = new ProcessStartInfo(PathOf(tool), DiagCommands.Arguments(tool, cabFolder));
            // No shell: the arguments go to the program as they are.
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Environment.SystemDirectory;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            StringBuilder sb = new StringBuilder();
            object gate = new object();
            using (Process p = Process.Start(psi))
            {
                DataReceivedEventHandler got = delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) AddLine(sb, e.Data, MaxOutputChars);
                };
                p.OutputDataReceived += got;
                p.ErrorDataReceived += got;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch (Exception) { }
                    throw new TimeoutException(DiagCommands.Exe(tool) + " did not finish in " + (timeoutMs / 1000) + " seconds.");
                }
                // Lets the readers deliver what is left.
                p.WaitForExit();
                lock (gate) output = sb.ToString();
                return p.ExitCode;
            }
        }
    }

    // ---------- the agent ----------

    // The agent's side of "Collect diagnostics". It reads agent/requests/<session id>.json from the place it checks for
    // updates, follows a request that is signed with the update key, collects what the device can tell and uploads it as a
    // log bundle of its own. The progress goes into the session, so the viewer sees it through the normal upload.
    //
    // A request is followed once. Its id is written to the state file before anything is collected; when that cannot be
    // written the request is not followed, so a request can never be used again after a restart.
    public sealed class AgentDiagnostics
    {
        public static readonly TimeSpan Deploying = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan After = TimeSpan.FromMinutes(10);
        public const string StateFile = "diagnostics.txt";
        public const string TempFolder = "diagnostics-tmp";
        const string NewSuffix = ".new";
        public const string NotesName = "diagnostics-notes.txt";
        public const string DsregName = "dsregcmd-status.txt";
        public const string ImeName = "ime-registry.txt";
        public const string Interrupted = "The agent restarted before the diagnostics were uploaded. Ask again.";
        // How many ids of followed requests are kept, and how many different refused files are logged.
        public const int KeepHandled = 500, TellAtMost = 20;

        readonly ISessionStore requests, upload;
        readonly string key, sid, statePath, tempDir;
        readonly bool mask;
        readonly Func<Session, byte[]> logs;
        readonly IDiagRunner runner;
        readonly Action<string> log;
        readonly List<string> handled = new List<string>();
        readonly HashSet<string> told = new HashSet<string>();
        readonly List<DiagnosticsEntry> entries = new List<DiagnosticsEntry>();
        DateTime nextUtc = DateTime.MinValue;
        DiagnosticsRequest active;
        // 1 received, 2 collecting is shown, 3 the programs run.
        int phase;
        string readError;
        bool stateBroken;
        Extras work;
        Thread worker;
        DateTime workStartedUtc;
        // False in tests: the programs then run inside Cycle.
        public bool Background = true;

        // requests: where the agent reads updates. upload: where it uploads sessions and logs. logs: the log bundle the agent uploads
        // anyway, or null when there is nothing to send or it is too large. statePath may be null: the requests that were
        // followed are then remembered until the agent ends only.
        public AgentDiagnostics(ISessionStore requests, ISessionStore upload, string publicKey, string sessionId, Func<Session, byte[]> logs, bool mask, IDiagRunner runner, string statePath, string tempDir, Action<string> log)
        {
            this.requests = requests;
            this.upload = upload;
            key = publicKey;
            sid = sessionId;
            this.logs = logs;
            this.mask = mask;
            this.runner = runner;
            this.statePath = statePath;
            this.tempDir = tempDir;
            this.log = log ?? delegate (string s) { };
            LoadState();
        }

        // Null when there is no update key or link: the agent then has nowhere to read requests from, and no key to check them with.
        public static AgentDiagnostics For(string publicKey, string link, ISessionStore upload, string sessionId, Func<Session, byte[]> logs, bool mask, string installDir, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(link)) return null;
            if (UpdateKeys.Problem(publicKey) != null) return null;
            if (!DiagnosticsRequest.IsSessionId(sessionId)) return null;
            ISessionStore st;
            try { st = Stores.Open(link); }
            catch (Exception) { return null; }
            // The folder of the agent, which only administrators and SYSTEM can write to.
            string tmp = Path.Combine(installDir, TempFolder);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception) { }
            return new AgentDiagnostics(st, upload, publicKey, sessionId, logs, mask, new WinDiagRunner(), Path.Combine(installDir, StateFile), tmp, log);
        }

        // A request is being followed. The agent does not update itself meanwhile.
        public bool Busy
        {
            get { return active != null; }
        }

        public List<DiagnosticsEntry> Entries
        {
            get { return entries; }
        }

        // ---- what is remembered over a restart ----

        void LoadState()
        {
            if (statePath == null) return;
            try
            {
                // A stop while the state was saved can leave the second file beside the first, or only the second. The
                // second holds the newest id, so the ids of both are read: the requests that were followed stay followed.
                string next = statePath + NewSuffix;
                bool main = File.Exists(statePath), second = File.Exists(next);
                if (!main && !second) return;
                bool changed = second;
                if (main && second)
                    foreach (string l in File.ReadAllLines(next, Encoding.UTF8)) Handled(l);
                foreach (string l in File.ReadAllLines(main ? statePath : next, Encoding.UTF8))
                {
                    if (Handled(l)) continue;
                    if (l.StartsWith("entry ", StringComparison.Ordinal))
                    {
                        foreach (DiagnosticsEntry e in DiagnosticsStatus.Decode(l.Substring(6)))
                        {
                            // What was under way when the agent stopped is not taken up again: the request counts as followed.
                            if (e.State == DiagnosticsStatus.Received || e.State == DiagnosticsStatus.Collecting)
                            {
                                e.State = DiagnosticsStatus.Failed;
                                e.Note = Interrupted;
                                changed = true;
                            }
                            entries.Add(e);
                        }
                    }
                }
                if (changed) SaveState();
            }
            catch (Exception ex)
            {
                // Without the list a request could be followed twice, so none is followed until the agent starts again.
                stateBroken = true;
                log("Diagnostics requests are not followed: the list of the ones that were followed could not be read from " + statePath + ": " + ex.Message);
            }
        }

        // A "handled <id>" line: the id is remembered. True when the line is one.
        bool Handled(string l)
        {
            if (!l.StartsWith("handled ", StringComparison.Ordinal)) return false;
            string id = l.Substring(8).Trim();
            if (DiagnosticsRequest.IsRequestId(id) && !handled.Contains(id)) handled.Add(id);
            return true;
        }

        // Writes the whole state through a second file, so a stop half way leaves the old one.
        bool SaveState()
        {
            if (statePath == null) return true;
            try
            {
                StringBuilder b = new StringBuilder();
                int from = Math.Max(0, handled.Count - KeepHandled);
                for (int k = from; k < handled.Count; k++) b.Append("handled ").Append(handled[k]).Append("\r\n");
                int first = Math.Max(0, entries.Count - DiagnosticsStatus.Keep);
                for (int k = first; k < entries.Count; k++)
                {
                    List<DiagnosticsEntry> one = new List<DiagnosticsEntry>();
                    one.Add(entries[k]);
                    b.Append("entry ").Append(DiagnosticsStatus.Encode(one)).Append("\r\n");
                }
                string tmp = statePath + NewSuffix;
                File.WriteAllText(tmp, b.ToString(), new UTF8Encoding(false));
                // Replaced in one step: there is no moment without a list, which would let a request be followed again.
                if (File.Exists(statePath)) File.Replace(tmp, statePath, null);
                else File.Move(tmp, statePath);
                return true;
            }
            catch (Exception ex)
            {
                log("Could not save the diagnostics requests to " + statePath + ": " + ex.Message);
                return false;
            }
        }

        // Writes the progress into the session, to go up with the next upload.
        public void Stamp(Session s)
        {
            DiagnosticsStatus.Write(s, entries);
        }

        void Set(string id, string state, DateTime nowUtc, string blob, string note)
        {
            DiagnosticsEntry e = null;
            foreach (DiagnosticsEntry x in entries) if (x.Id == id) e = x;
            if (e == null)
            {
                e = new DiagnosticsEntry();
                e.Id = id;
                entries.Add(e);
            }
            e.State = state;
            e.Utc = nowUtc;
            if (blob != null) e.Blob = blob;
            if (note != null) e.Note = note;
            SaveState();
        }

        void Tell(byte[] raw, string why)
        {
            // Once for each different file: a request that stays in the container is not logged again at every check,
            // and someone who keeps writing new files there cannot fill the log.
            if (told.Count >= TellAtMost) return;
            if (!told.Add(AgentUpdate.Sha256Hex(raw))) return;
            log("Diagnostics request ignored: " + Short(why) + (told.Count == TellAtMost ? " Further requests that are ignored are not logged." : ""));
        }

        // ---- the cycle ----

        // Called at every cycle of the agent. True when the progress changed: the caller then uploads the session at once.
        // The work is done in steps over several cycles, so each step reaches the viewer before the next begins, and the
        // programs run beside the agent, which goes on reading and uploading.
        public bool Cycle(Session s, bool deploying, DateTime nowUtc)
        {
            try
            {
                if (active != null) return Advance(s, nowUtc);
                // A clock that was set back does not keep the agent from reading requests until the old time comes again.
                if (nextUtc - nowUtc > After) nextUtc = nowUtc;
                if (stateBroken || nowUtc < nextUtc) return false;
                nextUtc = nowUtc + (deploying ? Deploying : After);
                string name = DiagnosticsRequest.NameFor(sid);
                byte[] raw;
                try { raw = requests.Get(name); }
                catch (StoreMissingException)
                {
                    readError = null;
                    return false;
                }
                catch (Exception ex)
                {
                    if (readError != ex.Message) log("Diagnostics request check: could not read " + name + " in " + requests.Title + ": " + ex.Message);
                    readError = ex.Message;
                    return false;
                }
                readError = null;
                if (raw == null) return false;
                DiagnosticsRequest r;
                string why = DiagnosticsRequest.Refuse(raw, key, sid, nowUtc, handled, out r);
                if (why != null)
                {
                    Tell(raw, why);
                    return false;
                }
                // Remembered first. A request that cannot be remembered is not followed.
                handled.Add(r.RequestId);
                if (!SaveState())
                {
                    stateBroken = true;
                    log("Diagnostics request " + r.RequestId + " is not followed, and no other until the agent starts again: it could not be remembered, so it could be followed twice.");
                    return false;
                }
                active = r;
                phase = 1;
                Set(r.RequestId, DiagnosticsStatus.Received, nowUtc, "", "");
                log("Diagnostics request " + r.RequestId + " received.");
                return true;
            }
            catch (Exception ex)
            {
                log("Diagnostics request failed: " + ex.Message);
                if (active != null)
                {
                    string id = active.RequestId;
                    Drop();
                    try { Set(id, DiagnosticsStatus.Failed, nowUtc, null, Short(ex.Message)); } catch (Exception) { }
                    return true;
                }
                return false;
            }
        }

        void Drop()
        {
            active = null;
            phase = 0;
            work = null;
            worker = null;
        }

        bool Advance(Session s, DateTime nowUtc)
        {
            if (phase == 1)
            {
                phase = 2;
                Set(active.RequestId, DiagnosticsStatus.Collecting, nowUtc, null, "");
                return true;
            }
            if (phase == 2)
            {
                phase = 3;
                workStartedUtc = nowUtc;
                Extras x = new Extras();
                x.Folder = Path.Combine(tempDir, "TrackVice-diag-" + Guid.NewGuid().ToString("N"));
                work = x;
                DiagnosticsRequest r = active;
                if (Background)
                {
                    worker = new Thread(delegate () { RunTools(r, x); });
                    worker.IsBackground = true;
                    worker.Name = "TrackVice diagnostics";
                    worker.Start();
                    return false;
                }
                RunTools(r, x);
            }
            Extras done = work;
            if (!done.Done)
            {
                if (nowUtc < workStartedUtc) workStartedUtc = nowUtc;
                if (nowUtc - workStartedUtc < DiagCommands.Longest) return false;
                // Every program has its own time limit, so this is not expected. The thread is left to end by itself.
                string id = active.RequestId;
                Drop();
                Set(id, DiagnosticsStatus.Failed, nowUtc, null, "Collecting took more than " + (int)DiagCommands.Longest.TotalMinutes + " minutes and was given up.");
                log("Diagnostics not uploaded: collecting took too long.");
                return true;
            }
            DiagnosticsRequest req = active;
            Drop();
            Finish(s, req, done, nowUtc);
            return true;
        }

        static string Short(string s)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return s.Length > 240 ? s.Substring(0, 240) + "..." : s;
        }

        string MaskText(string text)
        {
            StringBuilder b = new StringBuilder();
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n')) b.Append(mask ? Secrets.Mask(line) : line).Append("\r\n");
            return b.ToString();
        }

        // A value line of reg query, "    name    REG_SZ    value": the value is hidden when the name is one of a secret,
        // by the same rules as name=value in a log line.
        public static string MaskRegLine(string line)
        {
            if (line == null || !line.StartsWith("    ", StringComparison.Ordinal)) return Secrets.Mask(line);
            int type = line.IndexOf("    REG_", 4, StringComparison.Ordinal);
            if (type < 0) return Secrets.Mask(line);
            int value = line.IndexOf("    ", type + 4, StringComparison.Ordinal);
            if (value < 0) return line;
            string name = line.Substring(4, type - 4).Trim();
            string v = line.Substring(value + 4);
            if (v.Length == 0) return line;
            string pair = name.Replace(' ', '_') + "=\"" + v.Replace('"', '\'') + "\"";
            if (Secrets.Mask(pair) != pair) return line.Substring(0, value + 4) + Secrets.Hidden;
            return Secrets.Mask(line);
        }

        string MaskReg(string text)
        {
            StringBuilder b = new StringBuilder();
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n')) b.Append(mask ? MaskRegLine(line) : line).Append("\r\n");
            return b.ToString();
        }

        // What the programs gave. Filled by RunTools, on a thread of its own, and read once Done is set.
        sealed class Extras
        {
            public string Folder;
            public readonly List<KeyValuePair<string, byte[]>> Files = new List<KeyValuePair<string, byte[]>>();
            public readonly List<string> Notes = new List<string>();
            public byte[] Cab;
            public volatile bool Done;
        }

        static byte[] Utf8(string s)
        {
            return new UTF8Encoding(false).GetBytes(s);
        }

        // ---- collecting ----

        // Runs the programs the request asks for. Never throws.
        void RunTools(DiagnosticsRequest r, Extras x)
        {
            try
            {
                if (r.Wants(DiagnosticsRequest.Dsreg)) GetDsreg(x);
                if (r.Wants(DiagnosticsRequest.Ime)) GetIme(x);
                if (r.Wants(DiagnosticsRequest.MdmDiag)) GetMdm(x);
            }
            catch (Exception ex) { x.Notes.Add("Collecting stopped early: " + Short(ex.Message)); }
            finally
            {
                try { if (Directory.Exists(x.Folder)) Directory.Delete(x.Folder, true); } catch (Exception) { }
                x.Done = true;
            }
        }

        void GetDsreg(Extras x)
        {
            try
            {
                string o;
                int code = runner.Run(DiagTool.Dsreg, null, out o);
                x.Files.Add(new KeyValuePair<string, byte[]>(DsregName, Utf8(MaskText(o))));
                if (code != 0) x.Notes.Add("dsregcmd /status ended with code " + code + ".");
            }
            catch (Exception ex) { x.Notes.Add("dsregcmd /status was skipped: " + Short(ex.Message)); }
        }

        void GetIme(Extras x)
        {
            StringBuilder b = new StringBuilder();
            foreach (DiagTool t in new DiagTool[] { DiagTool.ImeWin32Apps, DiagTool.ImeSideCarPolicies })
            {
                string k = DiagCommands.RegKey(t);
                b.Append("; ").Append(k).Append("\r\n");
                try
                {
                    string o;
                    int code = runner.Run(t, null, out o);
                    if (code != 0) b.Append("; not found or not readable (code ").Append(code).Append(")\r\n");
                    else b.Append(MaskReg(o));
                }
                catch (Exception ex)
                {
                    b.Append("; skipped: ").Append(Short(ex.Message)).Append("\r\n");
                    x.Notes.Add("The registry key " + Txt.FileName(k) + " was skipped: " + Short(ex.Message));
                }
                b.Append("\r\n");
            }
            x.Files.Add(new KeyValuePair<string, byte[]>(ImeName, Utf8(b.ToString())));
        }

        void GetMdm(Extras x)
        {
            const string none = ", so there is no MDM diagnostics cab.";
            if (!runner.Exists(DiagTool.MdmDiagnostics))
            {
                x.Notes.Add("MdmDiagnosticsTool.exe is not on this device" + none);
                return;
            }
            try
            {
                MakePrivateFolder(x.Folder);
                string cab = Path.Combine(x.Folder, DiagCommands.CabName);
                string o;
                int code = runner.Run(DiagTool.MdmDiagnostics, x.Folder, out o);
                if (code != 0) x.Notes.Add("MdmDiagnosticsTool.exe ended with code " + code + none);
                else if (!File.Exists(cab)) x.Notes.Add("MdmDiagnosticsTool.exe made no cab file.");
                // Not read into memory when it cannot go up anyway.
                else if (new FileInfo(cab).Length > LogBundle.MaxBytes) x.Notes.Add(CabLeftOut);
                else x.Cab = File.ReadAllBytes(cab);
            }
            catch (TimeoutException) { x.Notes.Add("MdmDiagnosticsTool.exe did not finish in " + DiagCommands.MdmSeconds + " seconds and was stopped" + none); }
            catch (Exception ex) { x.Notes.Add("MdmDiagnosticsTool.exe was skipped: " + Short(ex.Message)); }
        }

        // The folder the cab is written to. The cab is not masked, and the folder of the agent can be read by every user
        // of the device, so this folder is for SYSTEM and administrators only, and for the account the agent runs as.
        // When that cannot be set the folder is not made, and there is no cab.
        public static void MakePrivateFolder(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Directory.CreateDirectory(path);
                return;
            }
            DirectorySecurity sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            List<SecurityIdentifier> who = new List<SecurityIdentifier>();
            who.Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            who.Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                if (me.User != null && !who.Contains(me.User)) who.Add(me.User);
            foreach (SecurityIdentifier sid in who)
                sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(path)) throw new IOException("The folder for the cab is there already.");
            Directory.CreateDirectory(path, sec);
        }

        static string Limit
        {
            get { return (LogBundle.MaxBytes / (1024 * 1024)) + " MB"; }
        }

        static string CabLeftOut
        {
            get { return "The MDM diagnostics cab was left out: with it the bundle is above " + Limit + "."; }
        }

        // ---- the bundle ----

        // Puts the bundle together and uploads it. Runs in the agent's own cycle, as the upload of the logs does.
        void Finish(Session s, DiagnosticsRequest r, Extras x, DateTime nowUtc)
        {
            try
            {
                // The log bundle of the agent, or only the session when there are no logs or too many.
                byte[] bundle = null;
                if (r.Wants(DiagnosticsRequest.Logs))
                {
                    try { bundle = logs == null ? null : logs(s); }
                    catch (Exception ex) { x.Notes.Add("The logs could not be collected: " + Short(ex.Message)); }
                    if (bundle != null && bundle.Length > LogBundle.MaxBytes) bundle = null;
                    if (bundle == null) x.Notes.Add("The log files were left out: there are none to send, or they are more than " + Limit + ". The session is in the bundle.");
                }
                // The smallest bundle: the session and the text files.
                byte[] sessionOnly = DeviceLogs.Build(mask ? Secrets.MaskedCopy(s) : s, new List<KeyValuePair<string, string>>());

                // Tried in this order: the logs with the cab, the logs, the session with the cab, the session.
                byte[] zip = null;
                List<string> said = null;
                for (int k = 0; k < 4 && zip == null; k++)
                {
                    bool withLogs = k < 2, withCab = k % 2 == 0;
                    if ((withLogs && bundle == null) || (withCab && x.Cab == null)) continue;
                    List<string> more = new List<string>();
                    if (!withLogs && bundle != null) more.Add("The log files were left out: with them the bundle is above " + Limit + ". The session is in the bundle.");
                    if (!withCab && x.Cab != null) more.Add(CabLeftOut);
                    byte[] z = Assemble(withLogs ? bundle : sessionOnly, x, withCab, more, r, nowUtc);
                    if (z.Length > LogBundle.MaxBytes) continue;
                    zip = z;
                    said = more;
                }
                if (zip == null)
                {
                    Set(r.RequestId, DiagnosticsStatus.Failed, nowUtc, null, "The bundle is above " + Limit + " even without the logs and the cab.");
                    log("Diagnostics not uploaded: the bundle is too large.");
                    return;
                }
                x.Notes.AddRange(said);
                string name = DiagnosticsRequest.BundleName(sid, r.RequestId);
                upload.Put(name, zip, null);
                string note = string.Join(" ", x.Notes.ToArray());
                Set(r.RequestId, DiagnosticsStatus.Uploaded, nowUtc, name, Short(note));
                log("Diagnostics uploaded, " + (zip.Length / 1024) + " KB." + (note.Length > 0 ? " " + note : ""));
            }
            catch (Exception ex)
            {
                Set(r.RequestId, DiagnosticsStatus.Failed, nowUtc, null, Short(ex.Message));
                log("Diagnostics not uploaded: " + ex.Message);
            }
        }

        // The bundle with the extra files added. The names of the extras are added to the index of the bundle, so the viewer maps them.
        static byte[] Assemble(byte[] baseZip, Extras x, bool withCab, List<string> more, DiagnosticsRequest r, DateTime nowUtc)
        {
            StringBuilder index = new StringBuilder();
            foreach (KeyValuePair<string, byte[]> f in x.Files) index.Append(f.Key).Append('\t').Append("diagnostics\\").Append(f.Key).Append("\r\n");
            index.Append(NotesName).Append('\t').Append("diagnostics\\").Append(NotesName).Append("\r\n");
            if (withCab) index.Append(DiagCommands.CabName).Append('\t').Append("diagnostics\\").Append(DiagCommands.CabName).Append("\r\n");

            StringBuilder notes = new StringBuilder();
            notes.Append("TrackVice diagnostics\r\n");
            notes.Append("Request: ").Append(r.RequestId).Append("\r\n");
            notes.Append("Asked: ").Append(r.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\r\n");
            notes.Append("Collected: ").Append(nowUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\r\n");
            notes.Append("Agent: ").Append(Program.Version).Append("\r\n");
            foreach (string n in x.Notes) notes.Append(n).Append("\r\n");
            foreach (string n in more) notes.Append(n).Append("\r\n");
            if (withCab) notes.Append(DiagCommands.CabName).Append(" is from MdmDiagnosticsTool.exe -area \"").Append(DiagCommands.MdmArea).Append("\". It is as Windows made it: nothing in it is masked.\r\n");

            using (MemoryStream o = new MemoryStream())
            {
                using (ZipArchive dst = new ZipArchive(o, ZipArchiveMode.Create, true))
                using (ZipArchive src = new ZipArchive(new MemoryStream(baseZip), ZipArchiveMode.Read))
                {
                    bool indexed = false;
                    foreach (ZipArchiveEntry e in src.Entries)
                    {
                        byte[] data;
                        using (Stream s = e.Open())
                        using (MemoryStream m = new MemoryStream())
                        {
                            s.CopyTo(m);
                            data = m.ToArray();
                        }
                        if (string.Equals(e.FullName, LogBundle.IndexName, StringComparison.OrdinalIgnoreCase))
                        {
                            indexed = true;
                            string old = new UTF8Encoding(false).GetString(data);
                            if (old.Length > 0 && !old.EndsWith("\n", StringComparison.Ordinal)) old += "\r\n";
                            data = Utf8(old + index);
                        }
                        Put(dst, e.FullName, data, CompressionLevel.Optimal);
                    }
                    if (!indexed) Put(dst, LogBundle.IndexName, Utf8(index.ToString()), CompressionLevel.Optimal);
                    foreach (KeyValuePair<string, byte[]> f in x.Files) Put(dst, f.Key, f.Value, CompressionLevel.Optimal);
                    Put(dst, NotesName, Utf8(notes.ToString()), CompressionLevel.Optimal);
                    // The cab is compressed already.
                    if (withCab) Put(dst, DiagCommands.CabName, x.Cab, CompressionLevel.NoCompression);
                }
                return o.ToArray();
            }
        }

        static void Put(ZipArchive z, string name, byte[] data, CompressionLevel level)
        {
            using (Stream w = z.CreateEntry(name, level).Open()) w.Write(data, 0, data.Length);
        }
    }
}
