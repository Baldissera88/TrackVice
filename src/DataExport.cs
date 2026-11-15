namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // Flat tables of uploaded enrollments for Power BI and other tools: one CSV per table, with a README that
    // describes the columns. Times are ISO 8601 UTC and numbers use the invariant culture.
    public static class DataExport
    {
        public const string EnrollmentsFile = "Enrollments.csv";
        public const string ItemsFile = "Items.csv";
        public const string FindingsFile = "Findings.csv";
        public const string ReadmeFile = "README.txt";

        public sealed class Result
        {
            public int Enrollments, Items, Findings;
        }

        // The session ID is the upload's file name without its folder and extension, so it is the same in every export.
        public static string SessionId(string key, Session s)
        {
            if (!string.IsNullOrEmpty(key))
            {
                string n = key;
                int k = n.LastIndexOf('/');
                if (k >= 0) n = n.Substring(k + 1);
                if (n.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 8);
                return n;
            }
            DateTime start = s.SinceUtc.Year > 2000 ? s.SinceUtc : s.SavedUtc;
            return (s.Computer ?? "device") + "-" + start.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        public static string EnrollmentsCsv(IList<Session> sessions, IList<string> keys, DateTime nowUtc)
        {
            StringBuilder b = new StringBuilder();
            Row(b, "SessionId", "Device", "User", "Deployment", "Profile", "Tenant", "JoinType", "OsVersion", "AgentVersion",
                "StartUtc", "EndUtc", "DurationSeconds", "Finished", "Succeeded", "Items", "Done", "Failed", "Running", "Waiting",
                "Findings", "Errors", "SlowestItem", "SlowestItemSeconds", "LastUploadUtc");
            for (int n = 0; n < sessions.Count; n++)
            {
                Session s = sessions[n];
                if (s == null) continue;
                FleetRun r = FleetStats.RunOf(s, nowUtc);
                int errors = 0;
                foreach (Finding f in s.Findings) if (f.Severity >= 2) errors++;
                Row(b, Text(SessionId(Key(keys, n), s)), Text(s.Computer), Text(s.User), Text(s.Kind), Text(r.Profile), Text(s.GetInfo("Tenant")),
                    Text(s.GetInfo("Join type")), Text(s.OsVersion), Text(s.AppVersion),
                    Time(r.StartUtc), Time(r.EndUtc), Num(r.Seconds), Bool(r.Finished), Bool(r.Finished && r.Failed == 0),
                    Int(r.Items), Int(r.Done), Int(r.Failed), Int(r.Running), Int(r.Waiting),
                    Int(s.Findings.Count), Int(errors), Text(r.Slowest), Num(r.SlowestSeconds), Time(Saved(s)));
            }
            return b.ToString();
        }

        public static string ItemsCsv(IList<Session> sessions, IList<string> keys, DateTime nowUtc)
        {
            StringBuilder b = new StringBuilder();
            Row(b, "SessionId", "Device", "Phase", "Kind", "Item", "ItemKey", "Source", "State", "StartUtc", "EndUtc", "DurationSeconds",
                "ExitCode", "ExitCodeHex", "Attempts", "FailedAttempts", "RestartNeeded");
            for (int n = 0; n < sessions.Count; n++)
            {
                Session s = sessions[n];
                if (s == null) continue;
                string id = SessionId(Key(keys, n), s);
                DateTime now = s.ClockUtc.Year > 2000 ? s.ClockUtc : nowUtc;
                foreach (Item i in s.Items)
                {
                    Phase ph = s.FindPhase(i.PhaseKey);
                    TimeSpan? d = i.Duration(now);
                    Row(b, Text(id), Text(s.Computer), Text(ph != null ? ph.Name : Phases.Name(i.PhaseKey)), Text(i.Kind), Text(i.DisplayName), Text(i.Key),
                        Text(i.Origin), Text(States.Label(i.State)), Time(i.StartUtc), Time(i.EndUtc), d.HasValue ? Num(d.Value.TotalSeconds) : "",
                        i.ExitCode.HasValue ? i.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : "",
                        i.ExitCode.HasValue ? Text("0x" + ((uint)(i.ExitCode.Value & 0xFFFFFFFF)).ToString("X8", CultureInfo.InvariantCulture)) : "",
                        Int(i.Attempts.Count), Int(i.FailedAttempts), Bool(i.Reboot));
                }
            }
            return b.ToString();
        }

        public static string FindingsCsv(IList<Session> sessions, IList<string> keys)
        {
            StringBuilder b = new StringBuilder();
            Row(b, "SessionId", "Device", "Severity", "Kind", "Title", "Item", "ItemKey", "TimeUtc", "Message", "Hint");
            for (int n = 0; n < sessions.Count; n++)
            {
                Session s = sessions[n];
                if (s == null) continue;
                string id = SessionId(Key(keys, n), s);
                Dictionary<string, Item> items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                foreach (Item x in s.Items) if (x.Key != null) items[x.Key] = x;
                foreach (Finding f in s.Findings)
                {
                    Item i = null;
                    if (f.ItemKey != null) items.TryGetValue(f.ItemKey, out i);
                    Row(b, Text(id), Text(s.Computer), Text(f.SeverityName), Text(f.Kind), Text(f.Title), Text(i != null ? i.DisplayName : null), Text(f.ItemKey),
                        Time(f.TimeUtc.Year > 2000 ? (DateTime?)f.TimeUtc : null), Text(f.Message), Text(f.Hint));
                }
            }
            return b.ToString();
        }

        // Writes the four files into the folder, replacing earlier ones.
        public static Result Write(string folder, IList<Session> sessions, IList<string> keys, DateTime nowUtc, string source)
        {
            Directory.CreateDirectory(folder);
            Encoding utf8 = new UTF8Encoding(true);
            File.WriteAllText(Path.Combine(folder, EnrollmentsFile), EnrollmentsCsv(sessions, keys, nowUtc), utf8);
            File.WriteAllText(Path.Combine(folder, ItemsFile), ItemsCsv(sessions, keys, nowUtc), utf8);
            File.WriteAllText(Path.Combine(folder, FindingsFile), FindingsCsv(sessions, keys), utf8);
            File.WriteAllText(Path.Combine(folder, ReadmeFile), Readme(nowUtc, source), utf8);
            Result r = new Result();
            foreach (Session s in sessions)
            {
                if (s == null) continue;
                r.Enrollments++;
                r.Items += s.Items.Count;
                r.Findings += s.Findings.Count;
            }
            return r;
        }

        // Every session uploaded since a time, newest first, with the name of its upload.
        public static void Load(AgentCatalog c, DateTime sinceUtc, int max, List<Session> sessions, List<string> keys)
        {
            List<RemoteDeployment> list = c.List(sinceUtc);
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            foreach (RemoteDeployment d in list)
            {
                Session s = null;
                try { s = c.Load(d.Key); }
                catch (Exception) { }
                if (s == null) continue;
                sessions.Add(s);
                keys.Add(d.Key);
            }
        }

        public static int Cli(string folder, string upload, int hours)
        {
            if (string.IsNullOrEmpty(upload)) throw new ArgumentException("/export-data needs /upload <location>: the storage container SAS link or folder the agents upload to.");
            AgentCatalog c = new AgentCatalog(Stores.Open(upload));
            DateTime now = DateTime.UtcNow;
            List<Session> sessions = new List<Session>();
            List<string> keys = new List<string>();
            Load(c, hours == 0 ? DateTime.MinValue : now.AddHours(-hours), 5000, sessions, keys);
            Result r = Write(folder, sessions, keys, now, hours == 0 ? "every upload" : "the uploads of the last " + hours + " hours");
            Console.WriteLine(Int(r.Enrollments) + " enrollments, " + Int(r.Items) + " items and " + Int(r.Findings) + " findings written to " + Path.GetFullPath(folder));
            return 0;
        }

        public static string Readme(DateTime nowUtc, string source)
        {
            StringBuilder b = new StringBuilder();
            b.Append("TrackVice data export\r\n");
            b.Append("=====================\r\n\r\n");
            b.Append("Written ").Append(nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(" from ").Append(source ?? "the uploaded sessions").Append(".\r\n\r\n");
            b.Append("Each file is a table, CSV as in RFC 4180, UTF-8, with a header row. Times are UTC in ISO 8601\r\n");
            b.Append("(2026-11-02T08:00:00Z), numbers use a dot for decimals, and true and false are written as such.\r\n");
            b.Append("Text that starts with = + - or @ gets a ' in front, so a spreadsheet does not run it as a formula.\r\n");
            b.Append("SessionId links the tables: one enrollment has many items and many findings.\r\n\r\n");
            Table(b, EnrollmentsFile, "One row per uploaded session.",
                "SessionId", "The upload the session came from",
                "Device", "Computer name",
                "User", "The user signed in on the device, when known",
                "Deployment", "Autopilot, task sequence, SCCM or Intune",
                "Profile", "The Autopilot profile or the task sequence",
                "Tenant", "The tenant the device joined",
                "JoinType", "Entra join or hybrid Entra join",
                "OsVersion", "Windows version",
                "AgentVersion", "The TrackVice version that uploaded",
                "StartUtc, EndUtc", "First and last activity of the apps, scripts and steps",
                "DurationSeconds", "EndUtc minus StartUtc",
                "Finished", "Nothing runs and nothing waits any more",
                "Succeeded", "Finished and no item failed",
                "Items, Done, Failed, Running, Waiting", "Counts of apps, scripts and steps. Policies and profiles are not counted",
                "Findings, Errors", "All findings, and those with severity Error",
                "SlowestItem, SlowestItemSeconds", "The item that took longest",
                "LastUploadUtc", "When the device last uploaded the session");
            Table(b, ItemsFile, "One row per item per session, policies included.",
                "SessionId, Device", "As in Enrollments.csv",
                "Phase", "Such as Device setup, Account setup or Windows PE",
                "Kind", "Win32 app, MSI app, Script, Policy, Step, Application and so on",
                "Item", "Name of the app, script, step or policy",
                "ItemKey", "The ID TrackVice keeps for the item",
                "Source", "SCCM, Intune or Windows",
                "State", "Succeeded, Failed, Installing, Waiting and so on",
                "StartUtc, EndUtc", "When the item started and ended. Empty when unknown",
                "DurationSeconds", "How long it ran, or has run so far",
                "ExitCode, ExitCodeHex", "The last exit code, as a number and as 0x hex",
                "Attempts, FailedAttempts", "How often it was tried and how often that failed",
                "RestartNeeded", "The item asked for a restart");
            Table(b, FindingsFile, "One row per finding per session.",
                "SessionId, Device", "As in Enrollments.csv",
                "Severity", "Error, Warning or Info",
                "Kind, Title", "What kind of finding it is",
                "Item, ItemKey", "The item it is about, when there is one. ItemKey links to Items.csv",
                "TimeUtc", "When it was found",
                "Message, Hint", "What happened and what to do next");
            b.Append("Power BI\r\n--------\r\n");
            b.Append("Get data > Text/CSV opens one file. To load every export in a folder, use Get data > Folder, choose the\r\n");
            b.Append("folder, filter Name on the file name (for example Items.csv) and Combine & Transform. Do it once per table.\r\n");
            b.Append("Set the *Utc columns to Date/Time/Timezone and the counts and seconds to Whole or Decimal number.\r\n");
            b.Append("Relate Items and Findings to Enrollments on SessionId (many to one).\r\n");
            b.Append("Several exports can overlap: remove duplicate rows on SessionId (Enrollments) or SessionId + ItemKey (Items).\r\n");
            return b.ToString();
        }

        static void Table(StringBuilder b, string file, string about, params string[] cols)
        {
            b.Append(file).Append("\r\n").Append(new string('-', file.Length)).Append("\r\n").Append(about).Append("\r\n\r\n");
            for (int k = 0; k + 1 < cols.Length; k += 2) b.Append("  ").Append(cols[k].PadRight(40)).Append(cols[k + 1]).Append("\r\n");
            b.Append("\r\n");
        }

        static string Key(IList<string> keys, int n)
        {
            return keys != null && n < keys.Count ? keys[n] : null;
        }

        static DateTime? Saved(Session s)
        {
            return s.SavedUtc.Year > 2000 ? (DateTime?)s.SavedUtc : null;
        }

        static void Row(StringBuilder b, params string[] cells)
        {
            for (int k = 0; k < cells.Length; k++)
            {
                if (k > 0) b.Append(',');
                b.Append(cells[k]);
            }
            b.Append("\r\n");
        }

        // A text cell: always quoted, quotes doubled, line breaks made spaces so each record is one line, and a ' before
        // a leading = + - @ tab or carriage return so spreadsheets do not run it as a formula.
        public static string Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if ("=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        public static string Time(DateTime? utc)
        {
            if (!utc.HasValue || utc.Value.Year < 2001) return "";
            DateTime t = utc.Value.Kind == DateTimeKind.Local ? utc.Value.ToUniversalTime() : utc.Value;
            return t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        static string Num(double v)
        {
            return Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string Int(int v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        static string Bool(bool v)
        {
            return v ? "true" : "false";
        }
    }
}
