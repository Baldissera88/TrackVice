namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;

    // ---------- the signed release manifest ----------

    // agent/latest.json: which TrackVice.exe the agents should run. Signed by the administrator, so a device that can
    // write to the container cannot make other devices run something else.
    public sealed class UpdateManifest
    {
        public const string Name = "agent/latest.json";
        public const int KeyBits = 3072;
        public const long MaxSize = 200L * 1024 * 1024;

        public string Version;
        public string Sha256;
        public long Size;
        public string Blob;
        public DateTime PublishedUtc;
        public string Signature;

        public static string BlobFor(string version)
        {
            return "agent/TrackVice-" + version + ".exe";
        }

        static string Iso(DateTime t)
        {
            return t.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        // The exact text that is signed. Every field is in it.
        public byte[] Payload()
        {
            string s = "TrackVice agent update\n"
                + "version=" + Version + "\n"
                + "sha256=" + (Sha256 ?? "").ToUpperInvariant() + "\n"
                + "size=" + Size.ToString(CultureInfo.InvariantCulture) + "\n"
                + "blob=" + Blob + "\n"
                + "published=" + Iso(PublishedUtc) + "\n";
            return Encoding.UTF8.GetBytes(s);
        }

        // What is wrong with the fields, or null. A manifest that names another blob than its version is refused,
        // so it cannot point the agent at any other file in the container.
        public string Problem()
        {
            if (!SemVer.IsValid(Version)) return "The version \"" + Version + "\" is not a version number.";
            if (Sha256 == null || !Regex.IsMatch(Sha256, "^[0-9A-Fa-f]{64}$")) return "The SHA-256 is not 64 hexadecimal characters.";
            if (Size <= 0 || Size > MaxSize) return "The size " + Size + " is not possible.";
            if (Blob != BlobFor(Version)) return "The file name " + Blob + " does not match the version " + Version + ".";
            return null;
        }

        public void Sign(RSACryptoServiceProvider key)
        {
            Signature = Convert.ToBase64String(key.SignData(Payload(), "SHA256"));
        }

        public bool Verify(string publicKey)
        {
            if (string.IsNullOrEmpty(Signature)) return false;
            byte[] sig;
            try { sig = Convert.FromBase64String(Signature); }
            catch (FormatException) { return false; }
            using (RSACryptoServiceProvider rsa = UpdateKeys.FromPublic(publicKey))
                return rsa.VerifyData(Payload(), "SHA256", sig);
        }

        [DataContract(Namespace = "")]
        sealed class Doc
        {
            [DataMember(Name = "version", Order = 1)] public string Version;
            [DataMember(Name = "sha256", Order = 2)] public string Sha256;
            [DataMember(Name = "size", Order = 3)] public long Size;
            [DataMember(Name = "blob", Order = 4)] public string Blob;
            [DataMember(Name = "published", Order = 5)] public string Published;
            [DataMember(Name = "signature", Order = 6)] public string Signature;
        }

        public byte[] ToJson()
        {
            Doc d = new Doc();
            d.Version = Version;
            d.Sha256 = Sha256;
            d.Size = Size;
            d.Blob = Blob;
            d.Published = Iso(PublishedUtc);
            d.Signature = Signature;
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Doc)).WriteObject(ms, d);
                return ms.ToArray();
            }
        }

        public static UpdateManifest Parse(byte[] json)
        {
            Doc d;
            try
            {
                using (MemoryStream ms = new MemoryStream(json)) d = (Doc)new DataContractJsonSerializer(typeof(Doc)).ReadObject(ms);
            }
            catch (Exception ex) { throw new StoreException("agent/latest.json could not be read: " + ex.Message); }
            if (d == null) throw new StoreException("agent/latest.json is empty.");
            UpdateManifest m = new UpdateManifest();
            m.Version = d.Version;
            m.Sha256 = d.Sha256;
            m.Size = d.Size;
            m.Blob = d.Blob;
            m.Signature = d.Signature;
            DateTime t;
            if (DateTime.TryParse(d.Published, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) m.PublishedUtc = t;
            return m;
        }
    }

    // ---------- keys ----------

    // Where the administrator's private signing key is kept.
    public interface IUpdateKeyStore
    {
        string Where { get; }
        byte[] Load();
        void Save(byte[] privateBlob);
    }

    // A file under %LOCALAPPDATA%\TrackVice, encrypted with DPAPI for this Windows user. It is kept apart from the
    // settings, so resetting the settings does not lose the key the devices trust.
    public sealed class DpapiKeyStore : IUpdateKeyStore
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TrackVice agent update key");
        readonly string path;

        public DpapiKeyStore() : this(Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"), "agent-update-key.bin"))
        {
        }

        public DpapiKeyStore(string file)
        {
            path = file;
        }

        public string Where
        {
            get { return path; }
        }

        public byte[] Load()
        {
            if (!File.Exists(path)) return null;
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }

        public void Save(byte[] privateBlob)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, ProtectedData.Protect(privateBlob, Entropy, DataProtectionScope.CurrentUser));
        }
    }

    public sealed class MemoryKeyStore : IUpdateKeyStore
    {
        public byte[] Blob;

        public string Where
        {
            get { return "memory"; }
        }

        public byte[] Load()
        {
            return Blob;
        }

        public void Save(byte[] privateBlob)
        {
            Blob = privateBlob;
        }
    }

    public static class UpdateKeys
    {
        static RSACryptoServiceProvider NewProvider(int bits)
        {
            // Type 24 is the provider with SHA-256. The key is not kept in a Windows key container.
            CspParameters p = new CspParameters(24);
            RSACryptoServiceProvider rsa = bits > 0 ? new RSACryptoServiceProvider(bits, p) : new RSACryptoServiceProvider(p);
            rsa.PersistKeyInCsp = false;
            return rsa;
        }

        public static RSACryptoServiceProvider Create()
        {
            return NewProvider(UpdateManifest.KeyBits);
        }

        public static string PublicKey(RSACryptoServiceProvider rsa)
        {
            return Convert.ToBase64String(rsa.ExportCspBlob(false));
        }

        // A short form of the public key, to compare what a device has with what this PC signs with.
        public static string Fingerprint(string publicKey)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(Encoding.ASCII.GetBytes(publicKey ?? ""));
                return BitConverter.ToString(d, 0, 8).Replace("-", " ");
            }
        }

        public static RSACryptoServiceProvider FromPublic(string publicKey)
        {
            byte[] blob;
            try { blob = Convert.FromBase64String((publicKey ?? "").Trim()); }
            catch (FormatException) { throw new ArgumentException("The update key is not base64. Copy it again from TrackVice."); }
            RSACryptoServiceProvider rsa = NewProvider(0);
            try { rsa.ImportCspBlob(blob); }
            catch (CryptographicException) { rsa.Dispose(); throw new ArgumentException("The update key is not an RSA public key. Copy it again from TrackVice."); }
            if (rsa.KeySize < UpdateManifest.KeyBits)
            {
                rsa.Dispose();
                throw new ArgumentException("The update key is shorter than " + UpdateManifest.KeyBits + " bits.");
            }
            return rsa;
        }

        // Null when the key can be used, otherwise why not.
        public static string Problem(string publicKey)
        {
            try
            {
                using (FromPublic(publicKey)) return null;
            }
            catch (ArgumentException ex) { return ex.Message; }
        }

        public static RSACryptoServiceProvider Load(IUpdateKeyStore store)
        {
            byte[] blob = store.Load();
            if (blob == null) return null;
            RSACryptoServiceProvider rsa = NewProvider(0);
            rsa.ImportCspBlob(blob);
            return rsa;
        }

        public static RSACryptoServiceProvider LoadOrCreate(IUpdateKeyStore store, out bool created)
        {
            created = false;
            RSACryptoServiceProvider rsa = Load(store);
            if (rsa != null) return rsa;
            rsa = Create();
            store.Save(rsa.ExportCspBlob(true));
            created = true;
            return rsa;
        }
    }

    // ---------- versions ----------

    // Versions such as 1.0.5 or 1.1.0-beta2. A version with a suffix comes before the same version without one.
    public static class SemVer
    {
        static readonly Regex Shape = new Regex(@"^\d{1,9}(\.\d{1,9}){0,3}(-[0-9A-Za-z][0-9A-Za-z.]*)?$");

        public static bool IsValid(string v)
        {
            return v != null && Shape.IsMatch(v);
        }

        public static int Compare(string a, string b)
        {
            string pa, pb;
            int[] na = Parts(a, out pa), nb = Parts(b, out pb);
            for (int i = 0; i < 4; i++)
                if (na[i] != nb[i]) return na[i] < nb[i] ? -1 : 1;
            if (pa == pb) return 0;
            if (pa == null) return 1;
            if (pb == null) return -1;
            return Math.Sign(string.CompareOrdinal(pa, pb));
        }

        static int[] Parts(string v, out string pre)
        {
            pre = null;
            v = (v ?? "").Trim();
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            int dash = v.IndexOf('-');
            if (dash >= 0)
            {
                pre = v.Substring(dash + 1);
                v = v.Substring(0, dash);
            }
            int[] n = new int[4];
            string[] s = v.Split('.');
            for (int i = 0; i < s.Length && i < 4; i++) int.TryParse(s[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]);
            return n;
        }

        // The version a TrackVice.exe says it is: its product version, which is the informational version.
        public static string OfFile(string path)
        {
            try
            {
                string v = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                if (IsValid(v)) return v;
            }
            catch (Exception) { }
            try
            {
                Version v = System.Reflection.AssemblyName.GetAssemblyName(path).Version;
                return v.Revision == 0 ? v.ToString(3) : v.ToString();
            }
            catch (Exception) { return null; }
        }
    }

    // ---------- the decision and the download ----------

    public enum UpdateDecision
    {
        NoKey,
        NoLink,
        Invalid,
        Same,
        Older,
        Newer
    }

    public sealed class UpdateCheck
    {
        public UpdateDecision Decision;
        public UpdateManifest Manifest;
        // The checked new TrackVice.exe, when it was downloaded.
        public string Path;
        public string Message;
    }

    public static class AgentUpdate
    {
        public static readonly TimeSpan Every = TimeSpan.FromHours(6);
        public const string HelperName = "TrackVice-apply.exe";
        public const string StageFolder = "update";
        // Written before the helper starts: the version it installs. Still there and not the running version at the
        // next start means the update failed, and the next check waits its 6 hours instead of trying again at once.
        public const string TriedFile = "update-tried.txt";

        public static UpdateDecision Decide(string current, string key, string link, string published)
        {
            if (string.IsNullOrWhiteSpace(key)) return UpdateDecision.NoKey;
            if (string.IsNullOrWhiteSpace(link)) return UpdateDecision.NoLink;
            if (!SemVer.IsValid(published)) return UpdateDecision.Invalid;
            int c = SemVer.Compare(published, current);
            return c > 0 ? UpdateDecision.Newer : c == 0 ? UpdateDecision.Same : UpdateDecision.Older;
        }

        public static string Sha256Hex(byte[] data)
        {
            using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "");
        }

        public static string Sha256OfFile(string path)
        {
            using (SHA256 h = SHA256.Create())
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "");
        }

        // Reads agent/latest.json, checks its signature and version, and downloads and checks the exe it names.
        // Nothing is written unless every check passes.
        public static UpdateCheck Check(ISessionStore store, string publicKey, string current, string stageDir)
        {
            UpdateCheck r = new UpdateCheck();
            r.Decision = UpdateDecision.Invalid;
            UpdateManifest m;
            try { m = UpdateManifest.Parse(store.Get(UpdateManifest.Name)); }
            catch (Exception ex)
            {
                r.Message = "No update information in " + store.Title + ": " + ex.Message;
                return r;
            }
            r.Manifest = m;
            bool signed;
            try { signed = m.Verify(publicKey); }
            catch (Exception ex)
            {
                r.Message = "The update key cannot be used: " + ex.Message;
                return r;
            }
            if (!signed)
            {
                r.Message = "agent/latest.json is not signed with the update key of this device. It is ignored.";
                return r;
            }
            string problem = m.Problem();
            if (problem != null)
            {
                r.Message = "agent/latest.json is signed but not valid: " + problem;
                return r;
            }
            r.Decision = Decide(current, publicKey, store.Title, m.Version);
            if (r.Decision == UpdateDecision.Same)
            {
                r.Message = "The agent runs the published version " + m.Version + ".";
                return r;
            }
            if (r.Decision == UpdateDecision.Older)
            {
                r.Message = "The published version " + m.Version + " is older than " + current + ". It is not installed.";
                return r;
            }
            byte[] exe;
            try { exe = store.Get(m.Blob); }
            catch (Exception ex)
            {
                r.Decision = UpdateDecision.Invalid;
                r.Message = "Version " + m.Version + " could not be downloaded: " + ex.Message;
                return r;
            }
            if (exe.LongLength != m.Size)
            {
                r.Decision = UpdateDecision.Invalid;
                r.Message = m.Blob + " has " + exe.LongLength + " bytes, the signed size is " + m.Size + ". It is not installed.";
                return r;
            }
            string hash = Sha256Hex(exe);
            if (!string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                r.Decision = UpdateDecision.Invalid;
                r.Message = m.Blob + " has SHA-256 " + hash + ", the signed one is " + m.Sha256.ToUpperInvariant() + ". It is not installed.";
                return r;
            }
            Directory.CreateDirectory(stageDir);
            string path = System.IO.Path.Combine(stageDir, "TrackVice-" + m.Version + ".exe");
            File.WriteAllBytes(path, exe);
            r.Path = path;
            r.Message = "Version " + m.Version + " is downloaded and its signature, size and SHA-256 are correct.";
            return r;
        }
    }

    // ---------- the agent side ----------

    // What the agent needs to hand an update to the helper. Faked in tests.
    public interface IUpdateLauncher
    {
        void CopySelf(string to);
        void StartDetached(string exe, string arguments);
    }

    // Checks for an update at start and every 6 hours while the installed agent runs.
    public sealed class AgentSelfUpdate
    {
        readonly ISessionStore store;
        readonly string key, current, dir;
        readonly Action<string> log;
        readonly IUpdateLauncher launcher;
        DateTime nextUtc = DateTime.MinValue;
        bool waitFirst;

        public AgentSelfUpdate(ISessionStore store, string publicKey, string current, string installDir, IUpdateLauncher launcher, Action<string> log)
        {
            this.store = store;
            key = publicKey;
            this.current = current;
            dir = installDir;
            this.launcher = launcher;
            this.log = log ?? delegate (string s) { };
        }

        public DateTime NextUtc
        {
            get { return nextUtc; }
        }

        // Null when self-update is off: no key, no link, or a key or link that cannot be used. Says why in the log.
        public static AgentSelfUpdate For(string publicKey, string link, string current, string installDir, IUpdateLauncher launcher, Action<string> log)
        {
            CleanUp(installDir);
            UpdateDecision d = AgentUpdate.Decide(current, publicKey, link, current);
            if (d == UpdateDecision.NoKey) return null;
            if (d == UpdateDecision.NoLink)
            {
                log("Self-update is off: there is an update key but no update link.");
                return null;
            }
            string problem = UpdateKeys.Problem(publicKey);
            if (problem != null)
            {
                log("Self-update is off: " + problem);
                return null;
            }
            ISessionStore st;
            try { st = Stores.Open(link); }
            catch (Exception ex)
            {
                log("Self-update is off: " + ex.Message);
                return null;
            }
            string tried = Tried(installDir);
            AgentSelfUpdate u = new AgentSelfUpdate(st, publicKey, current, installDir, launcher, log);
            if (tried != null && tried != current)
            {
                u.waitFirst = true;
                log("The update to " + tried + " did not finish. Self-update is on and checks " + st.Title + " again in " + AgentUpdate.Every.TotalHours + " hours.");
            }
            else log("Self-update is on. It checks " + st.Title + " now and every " + AgentUpdate.Every.TotalHours + " hours.");
            return u;
        }

        // The version the last update started to install, or null. The file is removed once read.
        static string Tried(string installDir)
        {
            try
            {
                string f = Path.Combine(installDir, AgentUpdate.TriedFile);
                if (!File.Exists(f)) return null;
                string v = File.ReadAllText(f).Trim();
                File.Delete(f);
                return v;
            }
            catch (Exception) { return null; }
        }

        // Removes what an earlier update left. The helper may still be closing, so this only tries.
        public static void CleanUp(string installDir)
        {
            try
            {
                string helper = Path.Combine(installDir, AgentUpdate.HelperName);
                if (File.Exists(helper)) File.Delete(helper);
                string stage = Path.Combine(installDir, AgentUpdate.StageFolder);
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
            catch (Exception) { }
        }

        // True when a newer version was downloaded and the helper was started. The agent then makes its last upload and exits.
        public bool Cycle(DateTime nowUtc)
        {
            if (nowUtc < nextUtc) return false;
            nextUtc = nowUtc + AgentUpdate.Every;
            if (waitFirst)
            {
                waitFirst = false;
                return false;
            }
            UpdateCheck r;
            try { r = AgentUpdate.Check(store, key, current, Path.Combine(dir, AgentUpdate.StageFolder)); }
            catch (Exception ex)
            {
                log("Update check failed: " + ex.Message);
                return false;
            }
            log("Update check: " + r.Message);
            if (r.Path == null) return false;
            try
            {
                string helper = Path.Combine(dir, AgentUpdate.HelperName);
                File.WriteAllText(Path.Combine(dir, AgentUpdate.TriedFile), r.Manifest.Version);
                launcher.CopySelf(helper);
                launcher.StartDetached(helper, "/agent-update-apply \"" + r.Path + "\" /sha256 " + r.Manifest.Sha256.ToUpperInvariant());
                log("The update helper is started. The agent stops so version " + r.Manifest.Version + " can replace it.");
                return true;
            }
            catch (Exception ex)
            {
                try { File.Delete(Path.Combine(dir, AgentUpdate.TriedFile)); } catch (Exception) { }
                log("The update could not be started, the agent goes on with " + current + ": " + ex.Message);
                return false;
            }
        }
    }

    // ---------- the helper that replaces the agent ----------

    // What the helper does to the device. Faked in tests, so no process is started or stopped there.
    public interface IUpdateOps
    {
        bool Exists(string path);
        string Hash(string path);
        // True when the agent has ended within the time.
        bool WaitForAgentExit(int ms);
        // Stops the agent processes of the target exe. Returns a description of those still running, or null.
        string StopAgents(string target);
        // Puts source in place of target and keeps the old file as a backup. Throws when it cannot.
        void Replace(string source, string target);
        void Restore(string target);
        void StartTask();
        void Delete(string path);
        void Sleep(int ms);
    }

    public static class UpdateApply
    {
        public const int WaitMs = 60000;
        public const int Tries = 10;

        // Waits for the agent to exit, puts the new exe in its place and starts the task again.
        // On any failure the task is started with the exe that was there, so the agent keeps running.
        public static int Run(IUpdateOps ops, string newExe, string target, string sha256, Action<string> log)
        {
            log("Update helper started for " + newExe + ".");
            if (!ops.Exists(newExe)) return Fail(ops, log, "The new TrackVice.exe is not there.", null);
            // The hash is what ties the file to the signed manifest, so without it nothing is replaced.
            if (string.IsNullOrEmpty(sha256)) return Fail(ops, log, "No SHA-256 was given for the new TrackVice.exe.", newExe);
            if (!string.Equals(ops.Hash(newExe), sha256, StringComparison.OrdinalIgnoreCase))
                return Fail(ops, log, "The new TrackVice.exe does not have the SHA-256 that was checked.", newExe);
            if (!ops.WaitForAgentExit(WaitMs))
            {
                string still = ops.StopAgents(target);
                if (still != null || !ops.WaitForAgentExit(15000))
                {
                    log("The agent did not stop" + (still != null ? ": " + still : "") + ". The update is not installed.");
                    ops.Delete(newExe);
                    return 1;
                }
            }
            Exception last = null;
            for (int i = 0; i < Tries; i++)
            {
                try
                {
                    ops.Replace(newExe, target);
                    last = null;
                    break;
                }
                catch (Exception ex) { last = ex; }
                ops.Sleep(1000);
            }
            if (last != null) return Fail(ops, log, "TrackVice.exe could not be replaced: " + last.Message, newExe);
            if (!string.Equals(ops.Hash(target), sha256, StringComparison.OrdinalIgnoreCase))
            {
                ops.Restore(target);
                return Fail(ops, log, "The replaced TrackVice.exe does not have the checked SHA-256. The earlier one is put back.", newExe);
            }
            ops.Delete(newExe);
            ops.StartTask();
            log("TrackVice.exe is replaced and the agent is started again.");
            return 0;
        }

        static int Fail(IUpdateOps ops, Action<string> log, string why, string delete)
        {
            log(why + " The agent is started again with the version it had.");
            if (delete != null) ops.Delete(delete);
            ops.StartTask();
            return 1;
        }
    }

    // ---------- finding the running agent ----------

    public sealed class AgentProc
    {
        public int Id;
        public string Name;
        // Null when Windows did not give the path.
        public string Path;
        public string CommandLine;

        public string Describe()
        {
            return Name + " (process " + Id + (Path != null ? ", " + Path : "") + ")";
        }
    }

    public interface IProcessSource
    {
        List<AgentProc> Find(string processName);
        void Kill(int id);
    }

    public static class AgentProcesses
    {
        // The processes that run target. When the path could not be read, the command line decides.
        public static List<AgentProc> Matching(IEnumerable<AgentProc> all, string target, int self)
        {
            List<AgentProc> l = new List<AgentProc>();
            foreach (AgentProc p in all)
            {
                if (p.Id == self) continue;
                if (p.Path != null)
                {
                    if (string.Equals(p.Path, target, StringComparison.OrdinalIgnoreCase)) l.Add(p);
                }
                else if (Runs(p.CommandLine, target)) l.Add(p);
            }
            return l;
        }

        static bool Runs(string commandLine, string target)
        {
            if (string.IsNullOrEmpty(commandLine)) return false;
            string c = commandLine.TrimStart();
            if (c.StartsWith("\""))
            {
                int end = c.IndexOf('"', 1);
                c = end > 0 ? c.Substring(1, end - 1) : c.Substring(1);
            }
            else if (c.Length > target.Length && c[target.Length] == ' ') c = c.Substring(0, target.Length);
            return string.Equals(c, target, StringComparison.OrdinalIgnoreCase);
        }

        // Stops every process that runs target. Returns those still running, described, or null.
        public static string Stop(IProcessSource source, string target, int self)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(target);
            foreach (AgentProc p in Matching(source.Find(name), target, self))
            {
                try { source.Kill(p.Id); } catch (Exception) { }
            }
            return Describe(Matching(source.Find(name), target, self));
        }

        public static string Describe(List<AgentProc> l)
        {
            if (l.Count == 0) return null;
            List<string> s = new List<string>();
            foreach (AgentProc p in l) s.Add(p.Describe());
            return string.Join(", ", s.ToArray());
        }

        // The message when /agent-install cannot replace the running agent.
        public static string ReplaceFailed(string target, string reason, string running)
        {
            return "The running agent could not be replaced: " + reason
                + (running != null ? " " + running + " still runs " + target + "." : " Another program has " + target + " open.")
                + " Restart the device and run the install again.";
        }
    }

    // Processes on Windows. The path comes from the process itself, or from WMI when that is not allowed.
    public sealed class WinProcessSource : IProcessSource
    {
        public List<AgentProc> Find(string processName)
        {
            List<AgentProc> l = new List<AgentProc>();
            bool missing = false;
            foreach (Process p in Process.GetProcessesByName(processName))
            {
                AgentProc a = new AgentProc();
                a.Id = p.Id;
                a.Name = processName + ".exe";
                try { a.Path = p.MainModule.FileName; }
                catch (Exception) { missing = true; }
                p.Dispose();
                l.Add(a);
            }
            if (missing) FromWmi(processName, l);
            return l;
        }

        static void FromWmi(string processName, List<AgentProc> l)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '" + processName.Replace("'", "") + ".exe'"))
                using (System.Management.ManagementObjectCollection c = s.Get())
                    foreach (System.Management.ManagementBaseObject o in c)
                    {
                        int id = Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture);
                        foreach (AgentProc a in l)
                            if (a.Id == id && a.Path == null)
                            {
                                a.Path = o["ExecutablePath"] as string;
                                a.CommandLine = o["CommandLine"] as string;
                            }
                        o.Dispose();
                    }
            }
            catch (Exception) { }
        }

        public void Kill(int id)
        {
            using (Process p = Process.GetProcessById(id))
            {
                p.Kill();
                p.WaitForExit(5000);
            }
        }
    }

    // ---------- Windows implementations ----------

    public sealed class WinUpdateLauncher : IUpdateLauncher
    {
        readonly string self;

        public WinUpdateLauncher(string selfPath)
        {
            self = selfPath;
        }

        public void CopySelf(string to)
        {
            File.Copy(self, to, true);
        }

        // Started through WMI, so the helper is not part of the scheduled task and lives on when the agent exits.
        public void StartDetached(string exe, string arguments)
        {
            string cmd = "\"" + exe + "\" " + arguments;
            try
            {
                using (System.Management.ManagementClass mc = new System.Management.ManagementClass(@"root\cimv2:Win32_Process"))
                {
                    object rc = mc.InvokeMethod("Create", new object[] { cmd, System.IO.Path.GetDirectoryName(exe), null, null });
                    if (Convert.ToInt32(rc, CultureInfo.InvariantCulture) == 0) return;
                }
            }
            catch (Exception) { }
            ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi).Dispose();
        }
    }

    public sealed class WinUpdateOps : IUpdateOps
    {
        readonly Func<string, int> schtasks;

        public WinUpdateOps(Func<string, int> schtasks)
        {
            this.schtasks = schtasks;
        }

        public bool Exists(string path)
        {
            return File.Exists(path);
        }

        public string Hash(string path)
        {
            return AgentUpdate.Sha256OfFile(path);
        }

        public bool WaitForAgentExit(int ms)
        {
            try
            {
                using (Mutex m = new Mutex(false, @"Global\TrackViceAgent"))
                {
                    bool got;
                    try { got = m.WaitOne(ms); }
                    catch (AbandonedMutexException) { got = true; }
                    if (got) m.ReleaseMutex();
                    return got;
                }
            }
            catch (Exception) { return false; }
        }

        public string StopAgents(string target)
        {
            return AgentProcesses.Stop(new WinProcessSource(), target, Process.GetCurrentProcess().Id);
        }

        public void Replace(string source, string target)
        {
            string tmp = target + ".new";
            File.Copy(source, tmp, true);
            File.Replace(tmp, target, target + ".old");
        }

        public void Restore(string target)
        {
            try { File.Copy(target + ".old", target, true); } catch (Exception) { }
        }

        public void StartTask()
        {
            schtasks("/Run /TN \"" + Agent.TaskName + "\"");
        }

        public void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        public void Sleep(int ms)
        {
            Thread.Sleep(ms);
        }
    }

    // ---------- publishing, on the administrator's PC ----------

    public static class UpdatePublisher
    {
        // Uploads the exe first and the signed manifest last, so a device never reads a manifest whose exe is not there.
        public static UpdateManifest Publish(ISessionStore to, byte[] exe, string version, RSACryptoServiceProvider key, DateTime nowUtc)
        {
            if (!SemVer.IsValid(version)) throw new ArgumentException("\"" + version + "\" is not a version number.");
            if (key.KeySize < UpdateManifest.KeyBits) throw new ArgumentException("The signing key is shorter than " + UpdateManifest.KeyBits + " bits.");
            UpdateManifest m = new UpdateManifest();
            m.Version = version;
            m.Sha256 = AgentUpdate.Sha256Hex(exe);
            m.Size = exe.LongLength;
            m.Blob = UpdateManifest.BlobFor(version);
            m.PublishedUtc = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            string problem = m.Problem();
            if (problem != null) throw new ArgumentException(problem);
            m.Sign(key);
            to.Put(m.Blob, exe, null);
            to.Put(UpdateManifest.Name, m.ToJson(), null);
            return m;
        }

        // TrackVice.exe /publish-agent /to <link or folder> [/exe <path>]
        public static int Cli(string to, string exePath, IUpdateKeyStore keys)
        {
            if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("Give where to publish: /publish-agent /to <SAS link with Write, or a folder>");
            if (!File.Exists(exePath)) throw new FileNotFoundException("TrackVice.exe was not found: " + exePath);
            string version = SemVer.OfFile(exePath);
            if (version == null) throw new ArgumentException("The version of " + exePath + " could not be read.");
            ISessionStore store = Stores.Open(to);
            bool created;
            using (RSACryptoServiceProvider key = UpdateKeys.LoadOrCreate(keys, out created))
            {
                string pub = UpdateKeys.PublicKey(key);
                if (created) Console.WriteLine("A new signing key was made and saved in " + keys.Where + ".");
                UpdateManifest m = Publish(store, File.ReadAllBytes(exePath), version, key, DateTime.UtcNow);
                Console.WriteLine("Published TrackVice " + m.Version + " to " + store.Title + " as " + m.Blob + ", with a signed " + UpdateManifest.Name + ".");
                Console.WriteLine("Agents with this update key install it within 6 hours. Put the key in $UpdateKey in Install-TrackViceAgent.ps1:");
                Console.WriteLine(pub);
            }
            return 0;
        }
    }
}
