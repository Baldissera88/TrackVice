namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;

    // ---------- secrets ----------

    // Hides passwords, keys and tokens in command lines, script output and log lines before a session leaves the device.
    public static class Secrets
    {
        const string Names = @"\w*passw(?:or)?d\w*|\w*passwd|\w*pwd|\w*secret\w*|\w*token|api_?key|access_?key|license_?key|product_?key|pidkey|serial_?key|client_?secret|sig";
        // name=value, name: value, with the value bare or quoted.
        static readonly Regex RxPair = new Regex(@"(?<n>\b(?:" + Names + @"))(?<s>\s*[=:]\s*)(?<v>""[^""]*""|'[^']*'|[^\s""'&;,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // -Password value, --token value, /password value.
        static readonly Regex RxSwitch = new Regex(@"(?<n>(?:--?|/)(?:" + Names + @"))(?<s>\s+)(?<v>""[^""]*""|'[^']*'|[^\s""'-/][^\s]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public const string Hidden = "***";

        public static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 6) return s;
            string r = RxPair.Replace(s, delegate (Match m) { return m.Groups["v"].Value == Hidden ? m.Value : m.Groups["n"].Value + m.Groups["s"].Value + Hidden; });
            return RxSwitch.Replace(r, delegate (Match m) { return m.Groups["v"].Value == Hidden ? m.Value : m.Groups["n"].Value + m.Groups["s"].Value + Hidden; });
        }

        // Masks every text of a session in place. Returns how many texts changed.
        public static int MaskSession(Session s)
        {
            int n = 0;
            foreach (Item i in s.Items)
                foreach (Pair p in i.Info)
                {
                    string m = Mask(p.Value);
                    if (!ReferenceEquals(m, p.Value) && m != p.Value) { p.Value = m; n++; }
                }
            foreach (TEvent e in s.Events)
            {
                string t = Mask(e.Text);
                if (t != e.Text) { e.Text = t; n++; }
                if (e.Evidence != null)
                {
                    string x = Mask(e.Evidence.Text);
                    if (x != e.Evidence.Text) { e.Evidence.Text = x; n++; }
                }
            }
            foreach (Finding f in s.Findings)
            {
                f.Message = Mask(f.Message);
                if (f.Evidence != null) f.Evidence.Text = Mask(f.Evidence.Text);
            }
            return n;
        }
    }

    // ---------- statistics over many enrollments ----------

    // One app, script or step, over every enrollment it was part of.
    public sealed class FleetItem
    {
        public string Name, Kind;
        public int Devices, Done, Failed, Running;
        public double TotalSeconds, MaxSeconds;
        public int Timed;
        public string SlowestOn, FailedOn;

        public double AvgSeconds
        {
            get { return Timed == 0 ? 0 : TotalSeconds / Timed; }
        }

        public double FailRate
        {
            get { return Devices == 0 ? 0 : Failed * 100.0 / Devices; }
        }
    }

    // One enrollment.
    public sealed class FleetRun
    {
        public string Computer, Kind, Profile, Slowest, Key;
        public DateTime? StartUtc, EndUtc;
        public int Items, Done, Failed, Running;
        public double Seconds, SlowestSeconds;
    }

    public sealed class FleetStats
    {
        public readonly List<FleetItem> Items = new List<FleetItem>();
        public readonly List<FleetRun> Runs = new List<FleetRun>();
        public double AvgSeconds, MedianSeconds, LongestSeconds;
        public int Finished, WithFailures;

        // Policies arrive in an instant and say nothing about time, so they are left out of the timing.
        static bool Timed(Item i)
        {
            return i.PhaseKey != Phases.Policies && i.Key != SccmFeed.RunKey;
        }

        public static FleetRun RunOf(Session s, DateTime nowUtc)
        {
            FleetRun r = new FleetRun();
            r.Computer = s.Computer;
            r.Kind = s.Kind;
            r.Profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            DateTime? first = null, last = null;
            foreach (Item i in s.Items)
            {
                if (!Timed(i)) continue;
                r.Items++;
                if (States.IsComplete(i.State)) r.Done++;
                if (i.State == State.Failed) r.Failed++;
                if (States.IsActive(i.State)) r.Running++;
                DateTime a = i.StartUtc ?? i.FirstUtc;
                DateTime b = i.EndUtc ?? (States.IsFinal(i.State) ? i.LastUtc : (s.ClockUtc.Year > 2000 ? s.ClockUtc : nowUtc));
                if (a.Year < 2001) continue;
                if (first == null || a < first.Value) first = a;
                if (last == null || b > last.Value) last = b;
                TimeSpan? d = i.Duration(b);
                if (d.HasValue && d.Value.TotalSeconds > r.SlowestSeconds)
                {
                    r.SlowestSeconds = d.Value.TotalSeconds;
                    r.Slowest = i.DisplayName;
                }
            }
            r.StartUtc = first;
            r.EndUtc = last;
            if (first.HasValue && last.HasValue && last.Value > first.Value) r.Seconds = (last.Value - first.Value).TotalSeconds;
            return r;
        }

        public static FleetStats Build(IEnumerable<Session> sessions, DateTime nowUtc)
        {
            FleetStats st = new FleetStats();
            Dictionary<string, FleetItem> byName = new Dictionary<string, FleetItem>(StringComparer.OrdinalIgnoreCase);
            List<double> totals = new List<double>();
            foreach (Session s in sessions)
            {
                FleetRun run = RunOf(s, nowUtc);
                st.Runs.Add(run);
                if (run.Failed > 0) st.WithFailures++;
                if (run.Running == 0 && run.Items > 0 && run.Seconds > 0)
                {
                    st.Finished++;
                    totals.Add(run.Seconds);
                }
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Item i in s.Items)
                {
                    if (!Timed(i)) continue;
                    string key = i.Kind + "|" + i.DisplayName;
                    if (!seen.Add(key)) continue;
                    FleetItem f;
                    if (!byName.TryGetValue(key, out f))
                    {
                        f = new FleetItem();
                        f.Name = i.DisplayName;
                        f.Kind = i.Kind;
                        byName[key] = f;
                        st.Items.Add(f);
                    }
                    f.Devices++;
                    if (i.State == State.Failed)
                    {
                        f.Failed++;
                        if (f.FailedOn == null) f.FailedOn = s.Computer;
                    }
                    else if (States.IsComplete(i.State)) f.Done++;
                    else if (States.IsActive(i.State)) f.Running++;
                    if (!States.IsFinal(i.State)) continue;
                    TimeSpan? d = i.Duration(nowUtc);
                    if (!d.HasValue) continue;
                    f.Timed++;
                    f.TotalSeconds += d.Value.TotalSeconds;
                    if (d.Value.TotalSeconds >= f.MaxSeconds)
                    {
                        f.MaxSeconds = d.Value.TotalSeconds;
                        f.SlowestOn = s.Computer;
                    }
                }
            }
            st.Items.Sort(delegate (FleetItem a, FleetItem b) { return b.AvgSeconds.CompareTo(a.AvgSeconds); });
            st.Runs.Sort(delegate (FleetRun a, FleetRun b) { return (b.StartUtc ?? DateTime.MinValue).CompareTo(a.StartUtc ?? DateTime.MinValue); });
            if (totals.Count > 0)
            {
                totals.Sort();
                double sum = 0;
                foreach (double t in totals) sum += t;
                st.AvgSeconds = sum / totals.Count;
                st.MedianSeconds = totals.Count % 2 == 1 ? totals[totals.Count / 2] : (totals[totals.Count / 2 - 1] + totals[totals.Count / 2]) / 2;
                st.LongestSeconds = totals[totals.Count - 1];
            }
            return st;
        }

        public string ToCsv()
        {
            StringBuilder b = new StringBuilder("Name;Kind;Devices;Done;Failed;Fail rate %;Average s;Longest s;Slowest on\r\n");
            foreach (FleetItem f in Items)
                b.Append(Q(f.Name)).Append(';').Append(Q(f.Kind)).Append(';').Append(f.Devices).Append(';').Append(f.Done).Append(';').Append(f.Failed).Append(';')
                    .Append(f.FailRate.ToString("0.#", CultureInfo.InvariantCulture)).Append(';').Append(f.AvgSeconds.ToString("0", CultureInfo.InvariantCulture)).Append(';')
                    .Append(f.MaxSeconds.ToString("0", CultureInfo.InvariantCulture)).Append(';').Append(Q(f.SlowestOn)).Append("\r\n");
            return b.ToString();
        }

        static string Q(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }
    }

    // ---------- alerts ----------

    public sealed class Alert
    {
        public string Key, Title, Text, Level;
        public List<Pair> Facts = new List<Pair>();
    }

    // Decides what is worth telling someone about, each thing once.
    public sealed class AlertWatch
    {
        readonly HashSet<string> sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sawActive;

        public AlertWatch(IEnumerable<string> alreadySent)
        {
            if (alreadySent != null) foreach (string k in alreadySent) sent.Add(k);
        }

        public IEnumerable<string> Sent
        {
            get { return sent; }
        }

        static Alert Make(Session s, string key, string level, string title, string text)
        {
            Alert a = new Alert();
            a.Key = key;
            a.Level = level;
            a.Title = title;
            a.Text = text;
            a.Facts.Add(new Pair("Device", s.Computer));
            string profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            if (profile != null) a.Facts.Add(new Pair("Deployment", profile));
            a.Facts.Add(new Pair("Progress", s.CountComplete + " of " + s.Items.Count + " done, " + s.Count(State.Failed) + " failed"));
            return a;
        }

        // The alerts that are new since the last call.
        public List<Alert> Check(Session s, bool idle)
        {
            List<Alert> list = new List<Alert>();
            foreach (Finding f in s.Findings)
            {
                if (f.Severity < 1 || f.Kind == FindingKinds.MdmErrors || f.Kind == FindingKinds.NoUploads) continue;
                string key = f.Kind + "|" + (f.ItemKey ?? "") + "|" + f.Title;
                if (!sent.Add(key)) continue;
                Alert a = Make(s, key, f.Severity >= 2 ? "error" : "warning", s.Computer + ": " + f.Title, f.Message);
                if (!string.IsNullOrEmpty(f.Hint)) a.Facts.Add(new Pair("What to do", f.Hint));
                list.Add(a);
            }
            if (s.CountActive > 0) sawActive = true;
            // Finished: something ran while this agent watched, and now nothing does.
            if (idle && sawActive && s.Items.Count > 0 && s.CountActive == 0)
            {
                DateTime? first = s.FirstUtc;
                string key = "finished|" + (first.HasValue ? first.Value.ToString("yyyyMMddHH", CultureInfo.InvariantCulture) : "");
                if (sent.Add(key))
                {
                    int failed = s.Count(State.Failed);
                    FleetRun run = FleetStats.RunOf(s, DateTime.UtcNow);
                    Alert a = Make(s, key, failed > 0 ? "error" : "good", s.Computer + (failed > 0 ? ": finished with " + failed + " failed" : ": enrollment finished"),
                        "Nothing has been running for a while." + (run.Seconds > 0 ? " It took " + Txt.DurationShort(TimeSpan.FromSeconds(run.Seconds)) + "." : "")
                        + (run.Slowest != null ? " The slowest part was " + run.Slowest + " (" + Txt.DurationShort(TimeSpan.FromSeconds(run.SlowestSeconds)) + ")." : ""));
                    list.Add(a);
                }
            }
            return list;
        }
    }

    // Posts an alert to a webhook: a Teams workflow ("When a Teams webhook request is received"), or anything that takes JSON with a text.
    public static class AlertSender
    {
        public static string Json(string s)
        {
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') { }
                else if (c == '\t') b.Append(' ');
                else if (c < ' ') { }
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        // An adaptive card for Teams, with the same words as plain text for everything else.
        public static string Payload(Alert a)
        {
            string color = a.Level == "error" ? "Attention" : a.Level == "warning" ? "Warning" : "Good";
            StringBuilder plain = new StringBuilder(a.Title).Append("\n").Append(a.Text);
            StringBuilder facts = new StringBuilder();
            foreach (Pair p in a.Facts)
            {
                if (facts.Length > 0) facts.Append(',');
                facts.Append("{\"title\":").Append(Json(p.Key)).Append(",\"value\":").Append(Json(p.Value)).Append('}');
                plain.Append("\n").Append(p.Key).Append(": ").Append(p.Value);
            }
            return "{\"type\":\"message\",\"text\":" + Json(plain.ToString()) + ",\"attachments\":[{\"contentType\":\"application/vnd.microsoft.card.adaptive\",\"contentUrl\":null,\"content\":{"
                + "\"$schema\":\"http://adaptivecards.io/schemas/adaptive-card.json\",\"type\":\"AdaptiveCard\",\"version\":\"1.4\",\"body\":["
                + "{\"type\":\"TextBlock\",\"size\":\"Medium\",\"weight\":\"Bolder\",\"wrap\":true,\"color\":\"" + color + "\",\"text\":" + Json(a.Title) + "},"
                + "{\"type\":\"TextBlock\",\"wrap\":true,\"text\":" + Json(a.Text) + "},"
                + "{\"type\":\"FactSet\",\"facts\":[" + facts + "]},"
                + "{\"type\":\"TextBlock\",\"isSubtle\":true,\"size\":\"Small\",\"text\":\"TrackVice " + Program.Version + "\"}]}}]}";
        }

        public static void Send(string url, Alert a)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new StoreException("The alert address must start with https://");
            HttpWebRequest req = Http.Request(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            byte[] body = Encoding.UTF8.GetBytes(Payload(a));
            req.ContentLength = body.Length;
            try
            {
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (req.GetResponse()) { }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                throw new StoreException("The alert was not accepted" + (resp != null ? " (HTTP " + (int)resp.StatusCode + ")" : "") + ": " + ex.Message);
            }
        }
    }

    // ---------- the device's logs ----------

    public static class LogBundle
    {
        public const string Prefix = "logs/";
        public const long MaxBytes = 60L * 1024 * 1024;

        public static string NameFor(string sessionBlob)
        {
            string id = Path.GetFileName(sessionBlob ?? "");
            int dot = id.IndexOf('.');
            if (dot > 0) id = id.Substring(0, dot);
            return Prefix + id + ".zip";
        }

        // The session and every log it read, as one zip. Null when there is nothing to send or it is too large.
        public static byte[] Make(Session s)
        {
            string zip = Path.Combine(Path.GetTempPath(), "TrackVice-logs-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Report.Bundle(s, zip);
                FileInfo f = new FileInfo(zip);
                if (!f.Exists || f.Length > MaxBytes) return null;
                return File.ReadAllBytes(zip);
            }
            finally
            {
                try { File.Delete(zip); } catch (Exception) { }
            }
        }

        // Unpacks a bundle into a folder and returns how many files it held.
        public static int Unpack(byte[] zip, string folder)
        {
            Directory.CreateDirectory(folder);
            int n = 0;
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (MemoryStream ms = new MemoryStream(zip))
            using (ZipArchive a = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    // Only the file name is used, so an entry cannot write outside the folder.
                    string dest = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(e.Name)));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream src = e.Open())
                    using (FileStream dst = File.Create(dest)) src.CopyTo(dst);
                    n++;
                }
            }
            return n;
        }
    }

    // ---------- plain words for why something failed ----------

    public static class Explain
    {
        // What a failed platform script said, for its finding.
        public static string ScriptError(Item i)
        {
            for (int k = i.Events.Count - 1; k >= 0; k--)
                if (i.Events[k].Text != null && i.Events[k].Text.StartsWith("Script error:", StringComparison.Ordinal)) return i.Events[k].Text.Substring(13).Trim();
            return null;
        }

        // A hint for a failure that the exit code alone does not explain.
        public static string Hint(Item i)
        {
            if (i.Kind == Kinds.Script)
                return "Run the script as SYSTEM on a test device to see the same error, for example with PsExec -s. The script's own output is in the events of this item.";
            if (i.Kind == Kinds.Office)
                return "Look in %windir%\\Temp and %temp% for the Office Click-to-Run setup logs, and check that the device reaches the Office CDN.";
            if (i.Kind == Kinds.MsiApp)
                return "The MSI ran in the device context without a user interface. Install it by hand with msiexec /i <file> /qn /l*v log.txt to see where it stops.";
            return null;
        }
    }
}
