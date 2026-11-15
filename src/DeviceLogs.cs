namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text;

    // A device's logs as the agent uploaded them, unpacked on this PC.
    public sealed class DeviceLogCopy
    {
        public string Folder;
        public string Computer;
        public int Logs;
        public DateTime UploadedUtc;
        // Each log's path on the device, as the session names it, and the unpacked file.
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // Downloads the zip of logs the agent uploads beside its session, and keeps it under %LOCALAPPDATA%\TrackVice\Logs.
    public static class DeviceLogs
    {
        public const int KeepDays = 30;
        public const string NotYet = "The device has not uploaded its logs yet. The agent uploads them when something fails, when the device goes idle and when it ends.";
        const string Partial = ".new";

        public static string Root
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"), "Logs"); }
        }

        // "<computer>-<session id>", with only characters that are safe in a folder name.
        public static string FolderName(string computer, string sessionBlob)
        {
            string id = LogBundle.NameFor(sessionBlob).Substring(LogBundle.Prefix.Length);
            id = id.Substring(0, id.Length - 4);
            string c = Clean(computer);
            return (c.Length > 0 ? c : "device") + "-" + Clean(id);
        }

        static string Clean(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char ch in s ?? "") b.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            return b.ToString();
        }

        // Downloads and unpacks the device's logs, replacing an older copy. Null when the device has not uploaded them.
        public static DeviceLogCopy Download(AgentCatalog catalog, string sessionBlob, string computer, string root, DateTime nowUtc)
        {
            StoreEntry e = catalog.FindLogs(sessionBlob);
            if (e == null) return null;
            byte[] zip = catalog.Store.Get(e.Name);
            Directory.CreateDirectory(root);
            Remove(root, nowUtc);
            string folder = Path.Combine(root, FolderName(computer, sessionBlob));
            string tmp = folder + Partial;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Dictionary<string, string> written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { LogBundle.Unpack(zip, tmp, written); }
            catch (Exception)
            {
                try { Directory.Delete(tmp, true); } catch (Exception) { }
                throw;
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(tmp, folder);
            Directory.SetLastWriteTimeUtc(folder, nowUtc);

            DeviceLogCopy c = new DeviceLogCopy();
            c.Folder = folder;
            c.Computer = computer;
            c.UploadedUtc = e.ModifiedUtc;
            string tmpRoot = Path.GetFullPath(tmp);
            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in written)
            {
                string moved = Path.Combine(folder, kv.Value.Substring(tmpRoot.Length).TrimStart(Path.DirectorySeparatorChar));
                files[kv.Key] = moved;
                if (!string.Equals(kv.Key, "session.json", StringComparison.OrdinalIgnoreCase) && !string.Equals(kv.Key, LogBundle.IndexName, StringComparison.OrdinalIgnoreCase)) c.Logs++;
            }
            MapFiles(c, files);
            return c;
        }

        // Ties each device path to its unpacked file: from the bundle's index, or for an older bundle without one,
        // by naming the session's logs the way the bundle did ("name", "name (2)", ...).
        static void MapFiles(DeviceLogCopy c, Dictionary<string, string> files)
        {
            string index;
            if (files.TryGetValue(LogBundle.IndexName, out index))
            {
                foreach (string line in File.ReadAllLines(index))
                {
                    int tab = line.IndexOf('\t');
                    string file;
                    if (tab > 0 && files.TryGetValue(line.Substring(0, tab), out file)) c.Files[line.Substring(tab + 1).Trim()] = file;
                }
                return;
            }
            string json;
            if (!files.TryGetValue("session.json", out json)) return;
            Session s;
            try { s = Session.Load(json); }
            catch (Exception) { return; }
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TEvent ev in s.Events)
            {
                if (ev.Evidence == null || !ev.Evidence.IsLogFile || c.Files.ContainsKey(ev.Evidence.Path)) continue;
                string name = Txt.FileName(ev.Evidence.Path);
                string pick = name;
                int k = 2;
                while (used.Contains(pick)) pick = Path.GetFileNameWithoutExtension(name) + " (" + k++ + ")" + Path.GetExtension(name);
                used.Add(pick);
                string file;
                if (files.TryGetValue(pick, out file)) c.Files[ev.Evidence.Path] = file;
            }
        }

        // The unpacked file for a log path on the device, or null when the bundle does not have it.
        public static string Map(DeviceLogCopy c, string devicePath)
        {
            if (c == null || string.IsNullOrEmpty(devicePath)) return null;
            string file;
            if (c.Files.TryGetValue(devicePath, out file)) return file;
            // A path written differently still matches when only one log in the bundle has that name.
            string name = Txt.FileName(devicePath);
            string only = null;
            foreach (KeyValuePair<string, string> kv in c.Files)
            {
                if (!string.Equals(Txt.FileName(kv.Key), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (only != null) return null;
                only = kv.Value;
            }
            return only;
        }

        // Deletes copies that were downloaded more than 30 days ago, and copies a download left half done. Returns how many.
        public static int Remove(string root, DateTime nowUtc)
        {
            int n = 0;
            if (!Directory.Exists(root)) return 0;
            foreach (string d in Directory.GetDirectories(root))
            {
                try
                {
                    if (d.EndsWith(Partial, StringComparison.OrdinalIgnoreCase) || Directory.GetLastWriteTimeUtc(d) < nowUtc.AddDays(-KeepDays))
                    {
                        Directory.Delete(d, true);
                        n++;
                    }
                }
                catch (Exception) { }
            }
            return n;
        }

        // A bundle in the format the agent uploads: session.json, the logs, and the index of their device paths.
        public static byte[] Build(Session s, IList<KeyValuePair<string, string>> logs)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZipArchive z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    string json = Path.Combine(Path.GetTempPath(), "TrackVice-" + Guid.NewGuid().ToString("N") + ".json");
                    try
                    {
                        s.Save(json);
                        Add(z, "session.json", File.ReadAllBytes(json));
                    }
                    finally
                    {
                        try { File.Delete(json); } catch (Exception) { }
                    }
                    HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    StringBuilder index = new StringBuilder();
                    foreach (KeyValuePair<string, string> log in logs)
                    {
                        string name = Txt.FileName(log.Key);
                        string pick = name;
                        int k = 2;
                        while (used.Contains(pick)) pick = Path.GetFileNameWithoutExtension(name) + " (" + k++ + ")" + Path.GetExtension(name);
                        used.Add(pick);
                        Add(z, pick, File.ReadAllBytes(log.Value));
                        index.Append(pick).Append('\t').Append(log.Key).Append("\r\n");
                    }
                    Add(z, LogBundle.IndexName, new UTF8Encoding(false).GetBytes(index.ToString()));
                }
                return ms.ToArray();
            }
        }

        static void Add(ZipArchive z, string name, byte[] data)
        {
            using (Stream w = z.CreateEntry(name, CompressionLevel.Optimal).Open()) w.Write(data, 0, data.Length);
        }

        // The demo logs of a scenario, under the device paths the demo session names them by.
        public static byte[] DemoBundle(string scenario, Session s, DateTime startUtc)
        {
            DemoScenario sc = Demo.Get(scenario);
            string tmp = Path.Combine(Path.GetTempPath(), "TrackVice-demo-" + Guid.NewGuid().ToString("N"));
            try
            {
                DemoPlayer.WriteLogs(sc, startUtc.AddSeconds(-sc.StartAt), tmp);
                List<KeyValuePair<string, string>> logs = new List<KeyValuePair<string, string>>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DemoStep st in sc.Steps)
                {
                    if (st.RegPath != null) continue;
                    if (st.Event != null) continue;
                    // The same rule as DemoPlayer.WriteLogs, which puts the WinPE and full OS smsts.log in folders of their own.
                    string path;
                    if (!sc.Paths.TryGetValue(st.Log, out path)) path = st.Log + ".log";
                    if (!seen.Add(path)) continue;
                    string sub = st.Log == "smsts-pe" ? "pe" : st.Log == "smsts-os" ? "os" : "";
                    string file = Path.Combine(Path.Combine(tmp, sub), path.Substring(path.LastIndexOf('\\') + 1));
                    if (File.Exists(file)) logs.Add(new KeyValuePair<string, string>(path, file));
                }
                return Build(s, logs);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (Exception) { }
            }
        }
    }
}
