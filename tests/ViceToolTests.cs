namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class ViceToolTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        const string AppId = "4f1c2b7a-1d2e-4c3b-9a8f-0e1d2c3b4a59";

        public static void Run()
        {
            MsiFromCommandLines();
            ImeCacheFolder();
            InstallerForWin32App();
            InstallerForSccmApp();
            InstallerNotMsi();
            FindPackVice();
            FindNotifyVice();
            AdminDistrustsUserPaths();
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

        sealed class FakeProbe : IToolProbe
        {
            public readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Dirs = new List<string>();
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Path;

            public bool FileExists(string path) { return path != null && Files.Contains(path); }
            public bool DirectoryExists(string path) { return Dirs.Exists(delegate (string d) { return string.Equals(d, path, StringComparison.OrdinalIgnoreCase); }); }

            public string[] Directories(string folder)
            {
                List<string> l = new List<string>();
                foreach (string d in Dirs)
                    if (d.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) && d.IndexOf('\\', folder.Length + 1) < 0) l.Add(d);
                return l.ToArray();
            }

            public string MachineValue(string key, string name)
            {
                string v;
                return Values.TryGetValue(key + "|" + (name ?? ""), out v) ? v : null;
            }

            public string WindowsDir { get { return @"C:\WINDOWS"; } }
            public string ProgramFiles { get { return @"C:\Program Files"; } }
            public string ProgramFilesX86 { get { return @"C:\Program Files (x86)"; } }
            public string Here { get { return @"D:\Tools\TrackVice"; } }
            public string PathVariable { get { return Path; } }
        }

        static void MsiFromCommandLines()
        {
            T.Eq("ContosoVPN-5.2.1.msi", CommandLines.MsiOf("msiexec /i \"ContosoVPN-5.2.1.msi\" /qn /norestart"), "msiexec /i quoted");
            T.Eq("Contoso.msi", CommandLines.MsiOf("\"C:\\WINDOWS\\system32\\msiexec.exe\" /i \"Contoso.msi\" /q"), "full msiexec path, as AppEnforce logs it");
            T.Eq("Timesheet.msi", CommandLines.MsiOf("msiexec.exe /qn /I Timesheet.msi ALLUSERS=\"\""), "/I after other options");
            T.Eq("Fabrikam Agent.msi", CommandLines.MsiOf("msiexec /package \"Fabrikam Agent.msi\" /quiet"), "/package with a space in the name");
            T.Eq("x64\\Contoso.msi", CommandLines.MsiOf("msiexec -i x64\\Contoso.msi -qn"), "dash options and a subfolder");
            T.Eq("Contoso.msi", CommandLines.MsiOf("msiexec /i\"Contoso.msi\" /qn"), "no space after /i");
            T.Eq("Contoso.msi", CommandLines.MsiOf("cmd.exe /c msiexec /i Contoso.msi /qn"), "behind cmd /c");
            T.Eq("Contoso.msi", CommandLines.MsiOf("Contoso.msi /quiet"), "the .msi itself");
            T.Eq(null, CommandLines.MsiOf("setup.exe /s /v\"/qn\" Contoso.msi"), "setup.exe wrapper ignored");
            T.Eq(null, CommandLines.MsiOf("AcroRdrDCx642500120432_MUI.exe /sAll /rs /msi EULA_ACCEPT=YES"), "exe with /msi ignored");
            T.Eq(null, CommandLines.MsiOf("powershell.exe -ExecutionPolicy Bypass -File Install.ps1 -Msi Contoso.msi"), "script ignored");
            T.Eq(null, CommandLines.MsiOf("msiexec /x {11111111-2222-3333-4444-555555555555} /qn"), "uninstall by product code");
            T.Eq(null, CommandLines.MsiOf("msiexec /i {11111111-2222-3333-4444-555555555555}"), "product code is no file");
            T.Eq(null, CommandLines.MsiOf(""), "empty");
            T.Eq(null, CommandLines.MsiOf(null), "null");
            List<string> parts = CommandLines.Split("\"C:\\Program Files\\PackVice\\PackVice.exe\" \"%1\"");
            T.Eq(2, parts.Count, "split count");
            T.Eq("C:\\Program Files\\PackVice\\PackVice.exe", parts[0], "split keeps spaces in quotes");
        }

        static void ImeCacheFolder()
        {
            string[] dirs = { @"C:\WINDOWS\IMECache\" + AppId + "_1", @"C:\WINDOWS\IMECache\" + AppId.ToUpperInvariant() + "_3", @"C:\WINDOWS\IMECache\other_9", @"C:\WINDOWS\IMECache\HealthScripts" };
            T.Eq(@"C:\WINDOWS\IMECache\" + AppId.ToUpperInvariant() + "_3", InstallerFinder.ImeFolder(dirs, AppId), "highest version, any case");
            T.Eq(null, InstallerFinder.ImeFolder(dirs, "9c0e2a11-0000-4000-8000-000000000001"), "no folder for another app");
        }

        static Item App(string kind, string cmd)
        {
            Item i = new Item();
            i.Key = "app";
            i.Name = "Contoso VPN";
            i.Kind = kind;
            i.Set("Command line", cmd);
            return i;
        }

        static void InstallerForWin32App()
        {
            FakeProbe p = new FakeProbe();
            Item i = App(Kinds.Win32App, "msiexec /i \"ContosoVPN-5.2.1.msi\" /qn");
            InstallerLookup l = InstallerFinder.For(i, p);
            T.Check(l != null && !l.Found && l.Reason.Contains("ID"), "win32 app without an ID says why");
            i.Set("ID", AppId);
            l = InstallerFinder.For(i, p);
            T.Check(l != null && !l.Found && l.Reason.Contains("IMECache"), "no cache folder says why: " + (l == null ? null : l.Reason));
            p.Dirs.Add(@"C:\WINDOWS\IMECache\" + AppId + "_2");
            l = InstallerFinder.For(i, p);
            T.Check(!l.Found && l.Reason.Contains("ContosoVPN-5.2.1.msi is not in"), "folder without the file says why");
            p.Files.Add(@"C:\WINDOWS\IMECache\" + AppId + @"_2\ContosoVPN-5.2.1.msi");
            l = InstallerFinder.For(i, p);
            T.Eq(@"C:\WINDOWS\IMECache\" + AppId + @"_2\ContosoVPN-5.2.1.msi", l.Path, "found in the IME cache");
        }

        static void InstallerForSccmApp()
        {
            FakeProbe p = new FakeProbe();
            Item i = App(Kinds.Application, "\"C:\\WINDOWS\\system32\\msiexec.exe\" /i \"Contoso Timesheet.msi\" /q");
            InstallerLookup l = InstallerFinder.For(i, p);
            T.Check(!l.Found && l.Reason.Contains("AppEnforce"), "no content path says why");
            i.Set("Content path", @"C:\WINDOWS\ccmcache\4");
            l = InstallerFinder.For(i, p);
            T.Check(!l.Found && l.Reason.Contains("not on this PC"), "cleaned cache says why");
            p.Dirs.Add(@"C:\WINDOWS\ccmcache\4");
            p.Files.Add(@"C:\WINDOWS\ccmcache\4\Contoso Timesheet.msi");
            l = InstallerFinder.For(i, p);
            T.Eq(@"C:\WINDOWS\ccmcache\4\Contoso Timesheet.msi", l.Path, "found in the ccmcache");
            Item abs = App(Kinds.Application, "msiexec /i \"\\\\fs01.contoso.com\\apps\\Contoso.msi\" /q");
            p.Files.Add(@"\\fs01.contoso.com\apps\Contoso.msi");
            T.Eq(@"\\fs01.contoso.com\apps\Contoso.msi", InstallerFinder.For(abs, p).Path, "a full path is used as it is");
            p.Files.Remove(@"\\fs01.contoso.com\apps\Contoso.msi");
            T.Eq(@"\\fs01.contoso.com\apps\Contoso.msi", InstallerFinder.For(abs, p).Path, "a share is not probed, so the window never waits for it");
            Item local = App(Kinds.Application, "msiexec /i \"D:\\Apps\\Contoso.msi\" /q");
            T.Check(!InstallerFinder.For(local, p).Found, "a full local path is checked");
        }

        static void InstallerNotMsi()
        {
            FakeProbe p = new FakeProbe();
            T.Eq(null, InstallerFinder.For(App(Kinds.Win32App, "setup.exe /quiet"), p), "setup.exe: no installer line");
            T.Eq(null, InstallerFinder.For(App(Kinds.Script, "msiexec /i a.msi"), p), "a script is not an app");
            T.Eq(null, InstallerFinder.For(App(Kinds.Win32App, null), p), "no command line");
        }

        static void FindPackVice()
        {
            FakeProbe p = new FakeProbe();
            T.Eq(null, ViceLocator.Find(ViceTool.PackVice, p, null, false), "nothing installed");
            p.Files.Add(@"C:\Program Files\PackVice\PackVice.exe");
            T.Eq(@"C:\Program Files\PackVice\PackVice.exe", ViceLocator.Find(ViceTool.PackVice, p, null, false), "Program Files");
            p.Values[@"Software\Classes\PackVice.Package\shell\open\command|"] = "\"E:\\Apps\\PackVice\\PackVice.exe\" \"%1\"";
            p.Files.Add(@"E:\Apps\PackVice\PackVice.exe");
            T.Eq(@"E:\Apps\PackVice\PackVice.exe", ViceLocator.Find(ViceTool.PackVice, p, null, false), "the .msi open command of the PackVice MSI");
            p.Files.Add(@"D:\Tools\TrackVice\PackVice.exe");
            p.Files.Add(@"D:\Chosen\PackVice.exe");
            T.Eq(@"D:\Chosen\PackVice.exe", ViceLocator.Find(ViceTool.PackVice, p, @"D:\Chosen\PackVice.exe", false), "the chosen path first");
            FakeProbe q = new FakeProbe();
            q.Path = @"C:\WINDOWS\system32;""D:\Portable"";";
            q.Files.Add(@"D:\Portable\PackVice.exe");
            T.Eq(@"D:\Portable\PackVice.exe", ViceLocator.Find(ViceTool.PackVice, q, null, false), "PATH");
            q.Files.Add(@"D:\Tools\TrackVice\PackVice.exe");
            T.Eq(@"D:\Tools\TrackVice\PackVice.exe", ViceLocator.Find(ViceTool.PackVice, q, null, false), "next to TrackVice.exe before PATH");
        }

        static void FindNotifyVice()
        {
            FakeProbe p = new FakeProbe();
            p.Values[@"Software\NotifyVice|InstallDir"] = @"C:\Program Files\NotifyVice\";
            p.Files.Add(@"C:\Program Files\NotifyVice\NotifyVice.exe");
            T.Eq(@"C:\Program Files\NotifyVice\NotifyVice.exe", ViceLocator.Find(ViceTool.NotifyVice, p, null, false), "InstallDir of the NotifyVice MSI");
            FakeProbe a = new FakeProbe();
            a.Values[@"Software\Microsoft\Windows\CurrentVersion\App Paths\NotifyVice.exe|"] = "\"F:\\NotifyVice\\NotifyVice.exe\"";
            a.Files.Add(@"F:\NotifyVice\NotifyVice.exe");
            T.Eq(@"F:\NotifyVice\NotifyVice.exe", ViceLocator.Find(ViceTool.NotifyVice, a, null, false), "App Paths");
            T.Eq(null, ViceLocator.Find(ViceTool.NotifyVice, new FakeProbe(), null, false), "NotifyVice not found");
        }

        static void AdminDistrustsUserPaths()
        {
            FakeProbe p = new FakeProbe();
            p.Path = @"C:\Users\Contoso\bin;C:\Program Files\Fabrikam";
            p.Files.Add(@"C:\Users\Contoso\bin\PackVice.exe");
            p.Files.Add(@"C:\Users\Contoso\Chosen\PackVice.exe");
            T.Eq(null, ViceLocator.Find(ViceTool.PackVice, p, @"C:\Users\Contoso\Chosen\PackVice.exe", true), "admin: user folders are not trusted");
            T.Eq(@"C:\Users\Contoso\Chosen\PackVice.exe", ViceLocator.Find(ViceTool.PackVice, p, @"C:\Users\Contoso\Chosen\PackVice.exe", false), "not admin: the chosen path");
            p.Files.Add(@"C:\Program Files\Fabrikam\PackVice.exe");
            T.Eq(@"C:\Program Files\Fabrikam\PackVice.exe", ViceLocator.Find(ViceTool.PackVice, p, null, true), "admin: PATH under Program Files");
            T.Check(!ViceLocator.UnderProgramFiles(@"C:\Program Files\..\Users\x.exe", p), "no way out with ..");
        }

        // ---------- data export ----------

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
