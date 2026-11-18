namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class DataExportTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        const string AppId = "4f1c2b7a-1d2e-4c3b-9a8f-0e1d2c3b4a59";

        public static void Run()
        {
            CsvText();
            CsvTables();
            CsvFiles();
            DemoUploads();
        }

        static void DemoUploads()
        {
            List<Session> sessions = new List<Session>();
            List<string> keys = new List<string>();
            DataExport.Load(new AgentCatalog(new DemoAgentStore()), DateTime.MinValue, 100, sessions, keys);
            T.Check(sessions.Count > 0 && sessions.Count == keys.Count, "demo uploads load with their keys");
            string[] e = Lines(DataExport.EnrollmentsCsv(sessions, keys, DateTime.UtcNow));
            T.Eq(sessions.Count + 1, e.Length, "one row per demo enrollment");
            int cols = e[0].Split(',').Length;
            foreach (string line in e) T.Check(!line.Contains("\n"), "one line per record");
            T.Check(e[1].StartsWith("\"") && !e[1].StartsWith("\"sessions/"), "session id without the folder: " + e[1]);
            string items = DataExport.ItemsCsv(sessions, keys, DateTime.UtcNow);
            T.Check(Lines(items).Length > sessions.Count, "items of the demo enrollments");
            T.Eq(24, cols, "enrollment columns");
        }

        static void CsvText()
        {
            T.Eq("\"plain\"", DataExport.Text("plain"), "quoted");
            T.Eq("\"say \"\"hi\"\"\"", DataExport.Text("say \"hi\""), "quotes doubled");
            T.Eq("\"a, b\"", DataExport.Text("a, b"), "comma inside quotes");
            T.Eq("\"'=HYPERLINK(1)\"", DataExport.Text("=HYPERLINK(1)"), "formula =");
            T.Eq("\"'+1\"", DataExport.Text("+1"), "formula +");
            T.Eq("\"'-2\"", DataExport.Text("-2"), "formula -");
            T.Eq("\"'@SUM(A1)\"", DataExport.Text("@SUM(A1)"), "formula @");
            T.Eq("\"'\tx\"", DataExport.Text("\tx"), "leading tab");
            T.Eq("\"one two three\"", DataExport.Text("one\r\ntwo\nthree"), "line breaks become spaces");
            T.Eq("", DataExport.Text(null), "null is empty");
            T.Eq("2026-11-02T08:00:00Z", DataExport.Time(T0), "ISO 8601 UTC");
            T.Eq("", DataExport.Time(null), "no time");
            T.Eq("abc-123", DataExport.SessionId("sessions/abc-123.json.gz", new Session()), "session id from the upload");
        }

        static Session Sample()
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            s.User = "megan@contoso.com";
            s.AppVersion = "1.0.5";
            s.ClockUtc = T0.AddHours(1);
            s.SavedUtc = T0.AddMinutes(50);
            s.SetInfo("Autopilot profile", "Contoso, standard");
            s.SetInfo("Tenant", "contoso.onmicrosoft.com");
            Phase ph = new Phase();
            ph.Key = Phases.DeviceSetup;
            ph.Name = "Device setup";
            s.Phases.Add(ph);
            Item a = new Item();
            a.Key = "win32:" + AppId;
            a.Name = "=Contoso VPN";
            a.Kind = Kinds.Win32App;
            a.Origin = "Intune";
            a.PhaseKey = Phases.DeviceSetup;
            a.State = State.Failed;
            a.ExitCode = -2147024891;
            a.FirstUtc = T0;
            a.StartUtc = T0.AddMinutes(2);
            a.EndUtc = T0.AddMinutes(12.5);
            a.LastUtc = a.EndUtc.Value;
            Attempt at = new Attempt();
            at.Index = 1;
            at.StartUtc = T0.AddMinutes(2);
            at.State = State.Failed;
            a.Attempts.Add(at);
            s.Items.Add(a);
            ph.Items.Add(a);
            Item b = new Item();
            b.Key = "win32:9c0e2a11-0000-4000-8000-000000000002";
            b.Name = "Fabrikam \"Notes\", 2026";
            b.Kind = Kinds.Win32App;
            b.PhaseKey = Phases.DeviceSetup;
            b.State = State.Succeeded;
            b.ExitCode = 0;
            b.FirstUtc = T0;
            b.StartUtc = T0;
            b.EndUtc = T0.AddMinutes(1);
            b.LastUtc = b.EndUtc.Value;
            s.Items.Add(b);
            ph.Items.Add(b);
            Finding f = new Finding();
            f.Kind = "Failed";
            f.Severity = 2;
            f.ItemKey = a.Key;
            f.Title = "Contoso VPN failed";
            f.Message = "Access is denied.\nThe installer stopped.";
            f.TimeUtc = T0.AddMinutes(12.5);
            s.Findings.Add(f);
            return s;
        }

        static string[] Lines(string csv)
        {
            return csv.TrimEnd('\r', '\n').Split(new string[] { "\r\n" }, StringSplitOptions.None);
        }

        static void CsvTables()
        {
            List<Session> sessions = new List<Session> { Sample(), null };
            List<string> keys = new List<string> { "sessions/contoso-pc01-1.json.gz" };
            string[] e = Lines(DataExport.EnrollmentsCsv(sessions, keys, T0.AddHours(3)));
            T.Eq(2, e.Length, "one enrollment row, null sessions skipped");
            T.Check(e[0].StartsWith("SessionId,Device,User,Deployment,Profile,Tenant,"), "enrollment header");
            T.Check(e[1].StartsWith("\"contoso-pc01-1\",\"CONTOSO-PC01\",\"megan@contoso.com\","), "enrollment row: " + e[1]);
            T.Check(e[1].Contains(",\"Contoso, standard\",\"contoso.onmicrosoft.com\","), "profile with a comma is quoted");
            T.Check(e[1].Contains(",2026-11-02T08:00:00Z,2026-11-02T08:12:30Z,750,true,false,2,1,1,0,0,1,1,"), "times, duration, finished, counts: " + e[1]);
            T.Check(e[1].EndsWith(",2026-11-02T08:50:00Z"), "last upload");

            string[] it = Lines(DataExport.ItemsCsv(sessions, keys, T0.AddHours(3)));
            T.Eq(3, it.Length, "two item rows");
            T.Eq("SessionId,Device,Phase,Kind,Item,ItemKey,Source,State,StartUtc,EndUtc,DurationSeconds,ExitCode,ExitCodeHex,Attempts,FailedAttempts,RestartNeeded", it[0], "items header");
            T.Check(it[1].Contains(",\"Device setup\",\"Win32 app\",\"'=Contoso VPN\","), "formula-safe item name: " + it[1]);
            T.Check(it[1].Contains(",2026-11-02T08:02:00Z,2026-11-02T08:12:30Z,630,-2147024891,\"0x80070005\",1,1,false"), "times, seconds, exit code: " + it[1]);
            T.Check(it[2].Contains(",\"Fabrikam \"\"Notes\"\", 2026\","), "quotes and comma in a name: " + it[2]);
            T.Check(it[2].Contains(",60,0,\"0x00000000\","), "success exit code");

            string[] f = Lines(DataExport.FindingsCsv(sessions, keys));
            T.Eq(2, f.Length, "one finding row");
            T.Check(f[1].StartsWith("\"contoso-pc01-1\",\"CONTOSO-PC01\",\"Error\",\"Failed\",\"Contoso VPN failed\",\"'=Contoso VPN\",\"win32:"), "finding row: " + f[1]);
            T.Check(f[1].Contains("\"Access is denied. The installer stopped.\""), "message on one line");
        }

        static void CsvFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                DataExport.Result r = DataExport.Write(dir, new List<Session> { Sample() }, null, T0.AddHours(3), "a test folder");
                T.Eq(1, r.Enrollments, "written enrollments");
                T.Eq(2, r.Items, "written items");
                T.Eq(1, r.Findings, "written findings");
                foreach (string n in new string[] { "Enrollments.csv", "Items.csv", "Findings.csv", "README.txt" }) T.Check(File.Exists(Path.Combine(dir, n)), n + " written");
                byte[] head = File.ReadAllBytes(Path.Combine(dir, "Items.csv"));
                T.Check(head.Length > 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF, "UTF-8 with BOM");
                string readme = File.ReadAllText(Path.Combine(dir, "README.txt"));
                T.Check(readme.Contains("Get data > Folder") && readme.Contains("SessionId") && readme.Contains("ExitCodeHex"), "README describes columns and folder import");
                string en = File.ReadAllText(Path.Combine(dir, "Enrollments.csv"));
                T.Check(en.Contains("\"CONTOSO-PC01-20"), "session id without an upload name");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
