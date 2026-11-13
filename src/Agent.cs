namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Xml;
    using Microsoft.Win32;

    // ---------- where sessions are uploaded ----------

    public sealed class StoreEntry
    {
        public string Name;
        public DateTime ModifiedUtc;
        public long Size;
        public readonly Dictionary<string, string> Meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class StoreException : Exception
    {
        public StoreException(string message) : base(message) { }
    }

    // A place devices upload their sessions to, and the viewer lists and reads them from.
    public interface ISessionStore
    {
        string Title { get; }
        void Put(string name, byte[] data, Dictionary<string, string> meta);
        List<StoreEntry> List(string prefix);
        byte[] Get(string name);
    }

    public static class Stores
    {
        public static bool IsUrl(string location)
        {
            return location != null && location.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        public static ISessionStore Open(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) throw new StoreException("No upload location is set.");
            location = location.Trim().Trim('"');
            if (IsUrl(location)) return new AzureStore(location);
            if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) throw new StoreException("The upload location must use https.");
            return new FolderStore(location);
        }
    }

    // A folder or a share. Each session is a file with a small .meta file beside it.
    public sealed class FolderStore : ISessionStore
    {
        readonly string root;

        public FolderStore(string folder)
        {
            root = folder;
        }

        public string Title
        {
            get { return root; }
        }

        string PathOf(string name)
        {
            return Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        }

        public void Put(string name, byte[] data, Dictionary<string, string> meta)
        {
            string path = PathOf(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            StringBuilder sb = new StringBuilder();
            if (meta != null)
                foreach (KeyValuePair<string, string> kv in meta) sb.Append(kv.Key).Append('=').Append(Uri.EscapeDataString(kv.Value ?? "")).Append('\n');
            Replace(path, data);
            Replace(path + ".meta", Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // Writes beside the file and swaps, so a reader never sees half a file.
        static void Replace(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch (Exception) { File.Delete(path); }
            }
            File.Move(tmp, path);
        }

        public List<StoreEntry> List(string prefix)
        {
            List<StoreEntry> l = new List<StoreEntry>();
            if (!Directory.Exists(root)) throw new StoreException("The folder " + root + " was not found.");
            string dir = PathOf(prefix.TrimEnd('/'));
            if (!Directory.Exists(dir)) return l;
            foreach (string f in Directory.GetFiles(dir))
            {
                if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                StoreEntry e = new StoreEntry();
                e.Name = prefix.TrimEnd('/') + "/" + Path.GetFileName(f);
                try
                {
                    FileInfo fi = new FileInfo(f);
                    e.ModifiedUtc = fi.LastWriteTimeUtc;
                    e.Size = fi.Length;
                    if (File.Exists(f + ".meta"))
                        foreach (string line in File.ReadAllLines(f + ".meta"))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0) e.Meta[line.Substring(0, eq)] = Uri.UnescapeDataString(line.Substring(eq + 1));
                        }
                }
                catch (IOException) { continue; }
                l.Add(e);
            }
            return l;
        }

        public byte[] Get(string name)
        {
            string path = PathOf(name);
            if (!File.Exists(path)) throw new StoreException("That session is no longer in " + root + ".");
            return File.ReadAllBytes(path);
        }
    }

    // An Azure Storage container, reached with a shared access signature (SAS) link to the container.
    // A device needs Create and Write. The viewer needs Read and List.
    public sealed class AzureStore : ISessionStore
    {
        readonly string container;
        readonly string sas;

        public AzureStore(string url)
        {
            int q = url.IndexOf('?');
            if (q < 0) throw new StoreException("The link has no access token. Use the container's SAS link, which ends with ?sv=...&sig=...");
            container = url.Substring(0, q).TrimEnd('/');
            sas = url.Substring(q + 1).TrimStart('?', '&');
            Uri u;
            if (!Uri.TryCreate(container, UriKind.Absolute, out u) || u.AbsolutePath.Trim('/').Length == 0)
                throw new StoreException("The link must point at a container: https://<account>.blob.core.windows.net/<container>?<token>");
            if (u.AbsolutePath.Trim('/').IndexOf('/') >= 0)
                throw new StoreException("The link points at a file in the container. Use the SAS link of the container itself.");
        }

        public string Title
        {
            get { return container; }
        }

        public void Put(string name, byte[] data, Dictionary<string, string> meta)
        {
            HttpWebRequest req = Http.Request(container + "/" + name + "?" + sas);
            req.Method = "PUT";
            req.Headers["x-ms-blob-type"] = "BlockBlob";
            req.ContentType = "application/octet-stream";
            if (meta != null)
                foreach (KeyValuePair<string, string> kv in meta)
                    if (!string.IsNullOrEmpty(kv.Value)) req.Headers["x-ms-meta-" + kv.Key] = Uri.EscapeDataString(kv.Value);
            req.ContentLength = data.Length;
            try
            {
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (req.GetResponse()) { }
            }
            catch (WebException ex) { throw Explain(ex, "upload"); }
        }

        public List<StoreEntry> List(string prefix)
        {
            List<StoreEntry> l = new List<StoreEntry>();
            string marker = null;
            for (int page = 0; page < 50; page++)
            {
                string url = container + "?restype=container&comp=list&include=metadata&prefix=" + Uri.EscapeDataString(prefix)
                    + (marker != null ? "&marker=" + Uri.EscapeDataString(marker) : "") + "&" + sas;
                string xml = Encoding.UTF8.GetString(Fetch(url, "list"));
                l.AddRange(ParseList(xml, out marker));
                if (string.IsNullOrEmpty(marker)) break;
            }
            return l;
        }

        public byte[] Get(string name)
        {
            return Fetch(container + "/" + name + "?" + sas, "read");
        }

        byte[] Fetch(string url, string what)
        {
            HttpWebRequest req = Http.Request(url);
            req.Method = "GET";
            try
            {
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (WebException ex) { throw Explain(ex, what); }
        }

        // The answer of List Blobs, with the metadata of every blob.
        public static List<StoreEntry> ParseList(string xml, out string marker)
        {
            List<StoreEntry> l = new List<StoreEntry>();
            marker = null;
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            // The answer starts with a byte order mark.
            doc.LoadXml(xml.TrimStart('﻿'));
            foreach (XmlNode b in doc.SelectNodes("/EnumerationResults/Blobs/Blob"))
            {
                StoreEntry e = new StoreEntry();
                XmlNode n = b.SelectSingleNode("Name");
                if (n == null) continue;
                e.Name = n.InnerText;
                XmlNode m = b.SelectSingleNode("Properties/Last-Modified");
                DateTime t;
                if (m != null && DateTime.TryParse(m.InnerText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) e.ModifiedUtc = t;
                XmlNode len = b.SelectSingleNode("Properties/Content-Length");
                long size;
                if (len != null && long.TryParse(len.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) e.Size = size;
                XmlNode meta = b.SelectSingleNode("Metadata");
                if (meta != null)
                    foreach (XmlNode k in meta.ChildNodes)
                        if (k.NodeType == XmlNodeType.Element)
                        {
                            try { e.Meta[k.Name] = Uri.UnescapeDataString(k.InnerText); }
                            catch (UriFormatException) { e.Meta[k.Name] = k.InnerText; }
                        }
                l.Add(e);
            }
            XmlNode next = doc.SelectSingleNode("/EnumerationResults/NextMarker");
            if (next != null && next.InnerText.Length > 0) marker = next.InnerText;
            return l;
        }

        StoreException Explain(WebException ex, string what)
        {
            HttpWebResponse resp = ex.Response as HttpWebResponse;
            if (resp == null) return new StoreException("Could not reach " + new Uri(container).Host + ": " + ex.Message);
            int status = (int)resp.StatusCode;
            string code = resp.Headers["x-ms-error-code"] ?? "";
            try { resp.Close(); } catch (Exception) { }
            if (status == 403 && code == "AuthorizationPermissionMismatch")
                return new StoreException("The link does not allow TrackVice to " + what + ". The viewer needs a SAS with Read and List, a device one with Create and Write.");
            if (status == 403)
                return new StoreException("The storage account refused the link (" + (code.Length > 0 ? code : "HTTP 403") + "). It may have expired, be mistyped, or the storage firewall may block this network.");
            if (status == 404 && what == "read") return new StoreException("That session is no longer in the container.");
            if (status == 404) return new StoreException("The container was not found. Check the link.");
            return new StoreException("The storage account answered HTTP " + status + (code.Length > 0 ? " (" + code + ")" : "") + ".");
        }
    }

    // ---------- the session, packed and summarised ----------

    public static class AgentData
    {
        public const string Prefix = "sessions/";

        public static string BlobName(string id)
        {
            return Prefix + id + ".json.gz";
        }

        public static byte[] Pack(Session s)
        {
            byte[] json = Encoding.UTF8.GetBytes(s.ToJson());
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(json, 0, json.Length);
                return ms.ToArray();
            }
        }

        public static Session Unpack(byte[] data)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Decompress))
                using (MemoryStream o = new MemoryStream())
                {
                    gz.CopyTo(o);
                    return Session.FromJson(Encoding.UTF8.GetString(o.ToArray()));
                }
            }
            catch (Exception ex)
            {
                if (ex is StoreException) throw;
                throw new StoreException("That file is not a TrackVice session: " + ex.Message);
            }
        }

        static string Iso(DateTime t)
        {
            return t.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        static DateTime? Time(StoreEntry e, string key)
        {
            string v;
            DateTime t;
            if (e.Meta.TryGetValue(key, out v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return null;
        }

        static int Int(StoreEntry e, string key)
        {
            string v;
            int n;
            return e.Meta.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        static string Str(StoreEntry e, string key)
        {
            string v;
            return e.Meta.TryGetValue(key, out v) && v.Length > 0 ? v : null;
        }

        // What changes when the session changes, to upload only then.
        public static string Signature(Session s)
        {
            long pct = 0;
            int states = 0;
            foreach (Item i in s.Items) { pct += i.Percent ?? 0; states = states * 31 + (int)i.State; }
            return s.Items.Count + "|" + s.Events.Count + "|" + s.Findings.Count + "|" + pct + "|" + states + "|" + s.Computer;
        }

        // The few values the list shows, saved with the session so the list needs no download.
        public static Dictionary<string, string> Summary(Session s, DateTime nowUtc, bool idle)
        {
            Dictionary<string, string> m = new Dictionary<string, string>();
            m["computer"] = s.Computer;
            m["kind"] = s.Kind;
            m["profile"] = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            m["tenant"] = s.GetInfo("Tenant");
            m["items"] = s.Items.Count.ToString(CultureInfo.InvariantCulture);
            m["done"] = s.CountComplete.ToString(CultureInfo.InvariantCulture);
            m["failed"] = s.Count(State.Failed).ToString(CultureInfo.InvariantCulture);
            m["active"] = s.CountActive.ToString(CultureInfo.InvariantCulture);
            Item cur = null;
            foreach (Item i in s.Items)
                if (States.IsActive(i.State) && i.Key != SccmFeed.RunKey && (cur == null || i.LastUtc > cur.LastUtc)) cur = i;
            if (cur != null) m["current"] = Txt.Short(States.Label(cur.State) + " " + cur.DisplayName, 120);
            DateTime? first = s.FirstUtc;
            if (first.HasValue) m["started"] = Iso(first.Value);
            m["updated"] = Iso(nowUtc);
            m["idle"] = idle ? "1" : "0";
            m["version"] = s.AppVersion;
            return m;
        }

        public static RemoteDeployment ToDeployment(StoreEntry e, DateTime nowUtc)
        {
            RemoteDeployment d = new RemoteDeployment();
            d.Key = e.Name;
            d.Device = Str(e, "computer") ?? Path.GetFileName(e.Name);
            d.Profile = Str(e, "profile");
            d.User = Str(e, "kind");
            d.Detail = Str(e, "tenant");
            d.StartUtc = Time(e, "started");
            d.LastUtc = Time(e, "updated") ?? e.ModifiedUtc;
            int items = Int(e, "items"), done = Int(e, "done"), failed = Int(e, "failed"), active = Int(e, "active");
            bool idle = Str(e, "idle") == "1";
            bool silent = !idle && nowUtc - d.LastUtc > TimeSpan.FromMinutes(5);
            string progress = done + " of " + items + " done";
            if (items == 0)
            {
                d.State = State.Pending;
                d.StateText = "Nothing has started yet";
            }
            else if (active > 0)
            {
                d.State = failed > 0 ? State.Failed : State.Installing;
                d.StateText = progress + (failed > 0 ? ", " + failed + " failed" : "") + (Str(e, "current") != null ? " · " + Str(e, "current") : "");
            }
            else if (failed > 0)
            {
                d.State = State.Failed;
                d.StateText = failed + " failed, " + progress;
            }
            else
            {
                d.State = done >= items ? State.Succeeded : State.Pending;
                d.StateText = done >= items ? "Done, " + items + " item" + (items == 1 ? "" : "s") : progress + ", the rest is waiting";
            }
            if (silent)
            {
                if (d.State == State.Installing) d.State = State.Unknown;
                d.StateText = "No upload for " + Txt.DurationShort(nowUtc - d.LastUtc) + " · " + d.StateText;
            }
            if (!States.IsActive(d.State) && d.State != State.Unknown) d.EndUtc = d.LastUtc;
            return d;
        }
    }

    // ---------- the viewer side ----------

    // Lists the sessions devices have uploaded.
    public sealed class AgentCatalog : IRemoteCatalog
    {
        readonly ISessionStore store;

        public AgentCatalog(ISessionStore s)
        {
            store = s;
        }

        public string Title
        {
            get { return "the uploaded sessions"; }
        }

        public string Source
        {
            get { return store.Title; }
        }

        public int RefreshSeconds
        {
            get { return 15; }
        }

        public List<RemoteDeployment> List(DateTime sinceUtc)
        {
            DateTime now = DateTime.UtcNow;
            List<RemoteDeployment> l = new List<RemoteDeployment>();
            foreach (StoreEntry e in store.List(AgentData.Prefix))
            {
                if (!e.Name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)) continue;
                RemoteDeployment d = AgentData.ToDeployment(e, now);
                if (d.LastUtc >= sinceUtc) l.Add(d);
            }
            l.Sort(delegate (RemoteDeployment a, RemoteDeployment b) { return b.LastUtc.CompareTo(a.LastUtc); });
            return l;
        }

        public IRemoteFeed Open(RemoteDeployment d)
        {
            return new AgentFeed(store, d.Key);
        }
    }

    // Follows one uploaded session. Each read that finds a newer upload replaces the whole session.
    public sealed class AgentFeed : IRemoteFeed
    {
        readonly ISessionStore store;
        readonly string name;
        int lastLength = -1;
        int lastHash;

        public AgentFeed(ISessionStore s, string blob)
        {
            store = s;
            name = blob;
        }

        public string Mode
        {
            get { return Modes.Agent; }
        }

        public int PollMs
        {
            get { return 5000; }
        }

        public Batch Read(DateTime nowUtc)
        {
            byte[] data = store.Get(name);
            int hash = 17;
            foreach (byte b in data) hash = unchecked(hash * 31 + b);
            Batch batch = new Batch();
            if (data.Length == lastLength && hash == lastHash) return batch;
            lastLength = data.Length;
            lastHash = hash;
            batch.Replace = AgentData.Unpack(data);
            batch.Sources.Add(name);
            return batch;
        }
    }

    // ---------- the device side ----------

    public sealed class AgentOptions
    {
        public string Upload;
        public int KeepHours = 48;
        public int IdleMinutes = 30;
        public int UploadSeconds = 10;
        public int HeartbeatSeconds = 30;
        public bool Once;
    }

    // Runs on the device while it deploys: reads it like the window does, and uploads the session as it changes.
    public sealed class Agent
    {
        public const string Key = @"SOFTWARE\TrackVice\Agent";
        public const string TaskName = "TrackVice Agent";

        readonly ISessionStore store;
        readonly string id;
        string lastSignature;
        DateTime lastChange, lastUpload = DateTime.MinValue;
        public int Uploads;
        public string LastError;

        public Agent(ISessionStore s, string sessionId, DateTime nowUtc)
        {
            store = s;
            id = sessionId;
            lastChange = nowUtc;
        }

        public bool Idle(Session s, DateTime nowUtc, int idleMinutes)
        {
            return s.Items.Count > 0 && s.CountActive == 0 && nowUtc - lastChange >= TimeSpan.FromMinutes(idleMinutes);
        }

        // Uploads when the session changed, and now and then when it did not, so the list shows the agent is alive.
        public bool Cycle(Session s, DateTime nowUtc, AgentOptions o, bool force)
        {
            string sig = AgentData.Signature(s);
            bool changed = sig != lastSignature;
            if (changed) lastChange = nowUtc;
            bool idle = Idle(s, nowUtc, o.IdleMinutes);
            double since = (nowUtc - lastUpload).TotalSeconds;
            int heartbeat = idle ? Math.Max(o.HeartbeatSeconds, 300) : o.HeartbeatSeconds;
            if (!force && !(changed && since >= o.UploadSeconds) && since < heartbeat)
            {
                // A change inside the upload interval is sent at the next cycle.
                if (changed) lastSignature = null;
                return false;
            }
            lastSignature = sig;
            try
            {
                s.SavedUtc = nowUtc;
                store.Put(AgentData.BlobName(id), AgentData.Pack(s), AgentData.Summary(s, nowUtc, idle));
                lastUpload = nowUtc;
                Uploads++;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                // Try again at the next heartbeat, not every cycle.
                lastUpload = nowUtc;
                LastError = ex.Message;
                return false;
            }
        }

        // ---- running as the scheduled task ----

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TrackVice Agent"); }
        }

        static string LogPath
        {
            get { return Path.Combine(InstallDir, "agent.log"); }
        }

        public static void Log(string text)
        {
            try
            {
                string path = LogPath;
                if (!Directory.Exists(Path.GetDirectoryName(path))) return;
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine);
            }
            catch (Exception) { }
        }

        static string Reg(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Key))
                    return k == null ? null : k.GetValue(name) as string;
            }
            catch (Exception) { return null; }
        }

        static void SetReg(string name, string value)
        {
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(Key)) k.SetValue(name, value);
        }

        public static int Run(AgentOptions o)
        {
            string upload = o.Upload ?? Reg("Upload");
            if (string.IsNullOrWhiteSpace(upload))
            {
                Console.Error.WriteLine("TrackVice: no upload location. Use /agent /upload <SAS link or folder>, or install the agent with /agent-install.");
                return 1;
            }
            bool created;
            using (Mutex one = new Mutex(true, @"Global\TrackViceAgent", out created))
            {
                if (!created)
                {
                    Console.WriteLine("The TrackVice agent is already running.");
                    return 0;
                }
                ISessionStore store = Stores.Open(upload);
                bool installed = o.Upload == null;
                string sid = installed ? Reg("SessionId") : null;
                if (string.IsNullOrEmpty(sid))
                {
                    sid = Guid.NewGuid().ToString("N");
                    if (installed) SetReg("SessionId", sid);
                }
                DateTime started = DateTime.UtcNow;
                DateTime t;
                if (installed && DateTime.TryParse(Reg("StartedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) started = t;
                else if (installed) SetReg("StartedUtc", started.ToString("o", CultureInfo.InvariantCulture));
                int keep;
                if (installed && int.TryParse(Reg("KeepHours"), NumberStyles.Integer, CultureInfo.InvariantCulture, out keep) && keep > 0) o.KeepHours = keep;
                DateTime until = started.AddHours(o.KeepHours);
                Log("Agent " + Program.Version + " started, session " + sid + ", uploading to " + store.Title + " until " + until.ToString("u", CultureInfo.InvariantCulture));

                Agent a = new Agent(store, sid, DateTime.UtcNow);
                string shownError = null;
                // Everything since Windows was installed: after a restart the session is read again in full.
                using (Engine e = Engine.ForLive(0, true, true, 2000))
                {
                    while (true)
                    {
                        DateTime now = DateTime.UtcNow;
                        try { e.Apply(e.Poll()); }
                        catch (Exception ex) { Log("Reading failed: " + ex.Message); }
                        bool last = o.Once || now >= until;
                        if (a.Cycle(e.S, now, o, last) && a.Uploads == 1) Log("First upload done, " + e.S.Items.Count + " items.");
                        if (a.LastError != shownError)
                        {
                            shownError = a.LastError;
                            Log(shownError != null ? "Upload failed: " + shownError : "Upload works again.");
                        }
                        if (last) break;
                        Thread.Sleep(a.Idle(e.S, now, o.IdleMinutes) ? 15000 : 2000);
                    }
                }
                if (installed && !o.Once)
                {
                    Log("Time is up after " + o.KeepHours + " hours. Removing the agent.");
                    Remove();
                }
                return a.LastError == null ? 0 : 1;
            }
        }

        // ---- installing ----

        // Copies TrackVice to Program Files, saves where to upload, and registers a task that runs the agent
        // as SYSTEM at every start. Returns at once, so it does not hold up the Enrollment Status Page.
        public static int Install(AgentOptions o, string exePath)
        {
            if (string.IsNullOrWhiteSpace(o.Upload)) throw new ArgumentException("Give the upload location: /agent-install /upload <SAS link or folder>");
            if (!Program.IsAdmin) throw new UnauthorizedAccessException("Installing the agent needs administrator rights.");
            Stores.Open(o.Upload);
            string dir = InstallDir;
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "TrackVice.exe");
            if (!string.Equals(Path.GetFullPath(exePath), target, StringComparison.OrdinalIgnoreCase))
            {
                Schtasks("/End /TN \"" + TaskName + "\"");
                Thread.Sleep(500);
                File.Copy(exePath, target, true);
            }
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(Key))
            {
                // The upload link is only for administrators and SYSTEM to read.
                try
                {
                    System.Security.AccessControl.RegistrySecurity sec = new System.Security.AccessControl.RegistrySecurity();
                    sec.SetAccessRuleProtection(true, false);
                    foreach (System.Security.Principal.WellKnownSidType w in new System.Security.Principal.WellKnownSidType[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
                        sec.AddAccessRule(new System.Security.AccessControl.RegistryAccessRule(new System.Security.Principal.SecurityIdentifier(w, null),
                            System.Security.AccessControl.RegistryRights.FullControl,
                            System.Security.AccessControl.InheritanceFlags.ContainerInherit, System.Security.AccessControl.PropagationFlags.None,
                            System.Security.AccessControl.AccessControlType.Allow));
                    k.SetAccessControl(sec);
                }
                catch (Exception ex) { Log("Could not restrict the registry key: " + ex.Message); }
                k.SetValue("Upload", o.Upload.Trim());
                k.SetValue("KeepHours", o.KeepHours.ToString(CultureInfo.InvariantCulture));
                k.SetValue("Version", Program.Version);
                k.DeleteValue("SessionId", false);
                k.DeleteValue("StartedUtc", false);
            }
            string xml = Path.Combine(dir, "task.xml");
            File.WriteAllText(xml, TaskXml(target), Encoding.Unicode);
            try
            {
                int rc = Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + xml + "\" /F");
                if (rc != 0) throw new InvalidOperationException("The scheduled task could not be created (schtasks returned " + rc + ").");
            }
            finally
            {
                try { File.Delete(xml); } catch (Exception) { }
            }
            Schtasks("/Run /TN \"" + TaskName + "\"");
            Log("Agent " + Program.Version + " installed.");
            Console.WriteLine("The TrackVice agent is installed and running. It removes itself after " + o.KeepHours + " hours.");
            return 0;
        }

        public static string TaskXml(string exe)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
                + "  <RegistrationInfo><Description>Uploads the deployment progress of this device for TrackVice. Removes itself when its time is up.</Description></RegistrationInfo>\r\n"
                + "  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>\r\n"
                + "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n"
                + "  <Settings>\r\n"
                + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
                + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
                + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
                + "    <StartWhenAvailable>true</StartWhenAvailable>\r\n"
                + "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n"
                + "    <Enabled>true</Enabled>\r\n"
                + "  </Settings>\r\n"
                + "  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>/agent</Arguments></Exec></Actions>\r\n"
                + "</Task>\r\n";
        }

        // Removes the task and the saved upload link. The files stay until the folder is deleted.
        public static int Remove()
        {
            Schtasks("/Delete /TN \"" + TaskName + "\" /F");
            try { Registry.LocalMachine.DeleteSubKeyTree(Key, false); }
            catch (Exception ex) { Log("Could not remove the registry key: " + ex.Message); }
            Log("Agent removed.");
            return 0;
        }

        static int Schtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(30000)) return -1;
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log("schtasks failed: " + ex.Message);
                return -1;
            }
        }
    }

    // ---------- demo ----------

    // Made-up devices whose sessions move on while you watch, played through the real parsers.
    public sealed class DemoAgentStore : ISessionStore
    {
        sealed class Device
        {
            public string Id;
            public Engine Engine;
        }

        readonly List<Device> devices = new List<Device>();
        readonly object gate = new object();

        public DemoAgentStore()
        {
            Add("autopilot", "CONTOSO-LT0417", 14 * 60);
            Add("osd", "CONTOSO-PC117", 22 * 60);
            Add("autopilot", "CONTOSO-LT0433", 30 * 60);
            Add("osd", "CONTOSO-PC204", 6 * 60);
            Add("autopilot", "CONTOSO-LT0398", 4 * 60);
        }

        void Add(string scenario, string computer, double seconds)
        {
            Device d = new Device();
            d.Id = computer.ToLowerInvariant();
            d.Engine = Engine.ForDemo(scenario);
            d.Engine.DemoSpeed = 6;
            d.Engine.Apply(d.Engine.DemoTo(seconds));
            d.Engine.S.Computer = computer;
            devices.Add(d);
        }

        public string Title
        {
            get { return "Demo data"; }
        }

        public void Put(string name, byte[] data, Dictionary<string, string> meta)
        {
        }

        void Advance(Device d)
        {
            string computer = d.Engine.S.Computer;
            d.Engine.Apply(d.Engine.Poll());
            d.Engine.S.Computer = computer;
            d.Engine.S.SavedUtc = DateTime.UtcNow;
        }

        public List<StoreEntry> List(string prefix)
        {
            List<StoreEntry> l = new List<StoreEntry>();
            lock (gate)
                foreach (Device d in devices)
                {
                    Advance(d);
                    StoreEntry e = new StoreEntry();
                    e.Name = AgentData.BlobName(d.Id);
                    e.ModifiedUtc = DateTime.UtcNow;
                    foreach (KeyValuePair<string, string> kv in AgentData.Summary(d.Engine.S, DateTime.UtcNow, false)) if (kv.Value != null) e.Meta[kv.Key] = kv.Value;
                    l.Add(e);
                }
            return l;
        }

        public byte[] Get(string name)
        {
            lock (gate)
                foreach (Device d in devices)
                    if (AgentData.BlobName(d.Id) == name)
                    {
                        Advance(d);
                        return AgentData.Pack(d.Engine.S);
                    }
            throw new StoreException("That session is no longer there.");
        }
    }
}
