namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // Names read from Intune, kept on this PC per customer so they are there at the next start without a sign-in.
    // Names of scripts and apps are not secret; the sign-in itself is kept apart, encrypted.
    public sealed class NameCache
    {
        readonly string folder;

        public NameCache(string folder)
        {
            this.folder = folder;
        }

        public static string DefaultFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice", "Names"); }
        }

        static string FileName(string key)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in (key ?? "").Trim().ToLowerInvariant())
                b.Append(char.IsLetterOrDigit(c) ? c : '_');
            if (b.Length == 0) b.Append("default");
            // The hash keeps two names that only differ in characters replaced above apart.
            int h = 17;
            foreach (char c in (key ?? "").ToLowerInvariant()) h = unchecked(h * 31 + c);
            return b + "-" + ((uint)h).ToString("x8", CultureInfo.InvariantCulture) + ".txt";
        }

        string PathOf(string key)
        {
            return Path.Combine(folder, FileName(key));
        }

        // The names and when they were read. An empty set when nothing is kept.
        public Dictionary<string, string> Load(string key, out DateTime readUtc)
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            readUtc = DateTime.MinValue;
            try
            {
                string path = PathOf(key);
                if (!File.Exists(path)) return names;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.StartsWith("#read\t", StringComparison.Ordinal))
                    {
                        DateTime t;
                        if (DateTime.TryParse(line.Substring(6), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) readUtc = t;
                        continue;
                    }
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && tab < line.Length - 1) names[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            catch (Exception) { }
            return names;
        }

        // Adds names to what is kept. Returns false when the file could not be written.
        public bool Save(string key, Dictionary<string, string> found, DateTime readUtc)
        {
            try
            {
                DateTime old;
                Dictionary<string, string> all = Load(key, out old);
                foreach (KeyValuePair<string, string> kv in found)
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) all[kv.Key] = kv.Value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                Directory.CreateDirectory(folder);
                StringBuilder b = new StringBuilder();
                b.Append("#read\t").Append(readUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append("\r\n");
                foreach (KeyValuePair<string, string> kv in all) b.Append(kv.Key).Append('\t').Append(kv.Value).Append("\r\n");
                string path = PathOf(key), tmp = path + ".tmp";
                File.WriteAllText(tmp, b.ToString(), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception) { return false; }
        }

        public void Delete(string key)
        {
            try { File.Delete(PathOf(key)); }
            catch (Exception) { }
        }

        // IDs in a session that still show as an ID because their name is not known: platform scripts, apps and remediations.
        public static List<string> Unknown(Session s, Dictionary<string, string> known)
        {
            List<string> ids = new List<string>();
            if (s == null) return ids;
            foreach (Item i in s.Items)
            {
                string id = null;
                if (i.Key.StartsWith("script:", StringComparison.Ordinal) && i.Name == MdmWatch.ScriptName(i.Key.Substring(7))) id = i.Key.Substring(7);
                else if (i.Key.StartsWith("intune:", StringComparison.Ordinal) && GraphClient.IsGuid(i.Key.Substring(7)) && (string.IsNullOrEmpty(i.Name) || i.Name == i.Key || i.Name.Equals(i.Key.Substring(7), StringComparison.OrdinalIgnoreCase))) id = i.Key.Substring(7);
                else if (i.Key.StartsWith("remediation:", StringComparison.Ordinal) && (string.IsNullOrEmpty(i.Name) || i.Name.IndexOf(i.Key.Substring(12, Math.Min(8, i.Key.Length - 12)), StringComparison.OrdinalIgnoreCase) >= 0)) id = i.Key.Substring(12);
                if (id != null && (known == null || !known.ContainsKey(id)) && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }
    }
}
