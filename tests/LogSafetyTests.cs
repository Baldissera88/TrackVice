namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // Log, registry and session text is not trusted: crafted input must not stall or stop the agent, and secrets in it are hidden.
    public static class LogSafetyTests
    {
        public static void Run()
        {
            MaskShapes();
            MaskSlowInput();
            MaskedTitles();
            CsvLeadingCharacters();
            SimpleFormat();
            CrazyTimes();
            SafeParser();
            SmstsSlowLine();
            PlantedSmsts();
            ImeNamesSlowInput();
            FirstSyncHugeDuration();
            SccmAppsSlowInput();
        }

        static string Repeat(string s, int n)
        {
            StringBuilder b = new StringBuilder(s.Length * n);
            for (int i = 0; i < n; i++) b.Append(s);
            return b.ToString();
        }

        static void Hidden(string input, string secret, string what)
        {
            string m = Secrets.Mask(input);
            T.Check(m.IndexOf(secret, StringComparison.Ordinal) < 0 && m.Contains(Secrets.Hidden), "mask hides: " + what + " -> " + m);
        }

        static void Kept(string input, string what)
        {
            T.Eq(input, Secrets.Mask(input), "mask keeps: " + what);
        }

        static void MaskShapes()
        {
            Hidden("powershell.exe -NoProfile -EncodedCommand SQBuAHYAbwBrAGUALQBXAGUAYgBSAGUAcQB1AGUAcwB0AA==", "SQBuAHYAbwBrAGUA", "-EncodedCommand");
            Hidden("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -enc SQBuAHYAbwBrAGUALQBXAGUAYgBSAGUAcQB1AGUAcwB0AA", "SQBuAHYAbwBrAGUA", "-enc");
            Hidden("pwsh -e SQBuAHYAbwBrAGUALQBXAGUAYgBSAGUAcQB1AGUAcwB0AA== -NoLogo", "SQBuAHYAbwBrAGUA", "-e");
            Hidden("net use Z: \\\\fs01\\share Sommer2026! /user:CONTOSO\\svc", "Sommer2026!", "net use password");
            Hidden("cmd /c net use \\\\fs01\\ipc$ /user:CONTOSO\\svc Vinter2026", "Vinter2026", "net use password after /user");
            Hidden("schtasks /Create /TN Job /TR c:\\x.cmd /RU CONTOSO\\svc /RP Herbst2026 /SC ONCE", "Herbst2026", "schtasks /RP");
            Hidden("schtasks.exe /Query /S srv01 /U admin /P Fruehling1", "Fruehling1", "schtasks /P");
            Hidden("PsExec64.exe \\\\pc01 -u CONTOSO\\adm -p Geheim2026 -s cmd", "Geheim2026", "psexec -p");
            Hidden("cmdkey /generic:TERMSRV/pc01 /user:CONTOSO\\adm /pass:Kodeord2026", "Kodeord2026", "cmdkey /pass:");
            Hidden("<AdministratorPassword><Value>UnattendPw1</Value><PlainText>true</PlainText></AdministratorPassword>", "UnattendPw1", "unattend password");
            Hidden("<Password>XmlPw2026</Password>", "XmlPw2026", "XML password");
            Hidden("<ProductKey><Key>AAAAA-BBBBB-CCCCC-DDDDD-EEEEE</Key></ProductKey>", "AAAAA-BBBBB", "unattend product key");
            Hidden("$p = ConvertTo-SecureString -AsPlainText \"Winter2026x\" -Force", "Winter2026x", "ConvertTo-SecureString -AsPlainText first");
            Hidden("$p = 'Pipe2026x' | ConvertTo-SecureString -AsPlainText -Force", "Pipe2026x", "text piped to ConvertTo-SecureString");
            // "Running " and the first 200 characters of a command line can end inside the quotes.
            Hidden("Running msiexec /i app.msi /qn SERVICE_PASSWORD=\"Secr3tCut", "Secr3tCut", "a quoted value cut off at the end");
            Hidden("setup.exe -Password 'Secr3tCut\r\nnext line", "Secr3tCut", "a quoted switch value cut off at the end of a line");

            Kept("powershell.exe -ExecutionPolicy Bypass -File C:\\Scripts\\install.ps1", "execution policy");
            Kept("tool.exe -e abc", "a short -e value");
            Kept("net use Z: \\\\fs01\\share /persistent:no", "net use without a password");
            Kept("net use Z: \\\\fs01\\share *", "net use that asks for the password");
            Kept("schtasks /Create /TN Job /RU SYSTEM /SC ONSTART", "schtasks without a password");
            Kept("psexec -s -i cmd.exe", "psexec without a password");
            Kept("<PasswordLength>14</PasswordLength>", "a password setting in XML");
            Kept("<PlainText>true</PlainText>", "an XML flag");
            Kept("$p = $text | ConvertTo-SecureString -AsPlainText -Force", "a variable piped to ConvertTo-SecureString");
            Kept("ConvertTo-SecureString -AsPlainText $pw -Force", "a variable after -AsPlainText");
        }

        static void MaskSlowInput()
        {
            // "https://user:password@" was looked for from every letter to the end of the line: minutes for a long line.
            string dots = Repeat("a.", 40000);
            Stopwatch w = Stopwatch.StartNew();
            string m = Secrets.Mask(dots);
            T.Check(w.Elapsed.TotalSeconds < 5, "mask of a long line of a.a.a. takes " + w.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            T.Eq(dots, m, "a long line without secrets stays as it is");

            // Quotes that are never closed: each one was read to the end of the line.
            string open = Repeat("password=\"", 30001) + "Hunter2x";
            w = Stopwatch.StartNew();
            m = Secrets.Mask(open);
            T.Check(w.Elapsed.TotalSeconds < 15, "mask of unclosed quotes takes " + w.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            T.Check(m.IndexOf("Hunter2x", StringComparison.Ordinal) < 0, "a text too complex to check keeps no secret");
        }

        static void MaskedTitles()
        {
            Session s = new Session();
            Finding f = new Finding();
            f.Kind = FindingKinds.Failed;
            f.Severity = 2;
            f.Title = "setup.exe PASSWORD=Hunter2x failed";
            f.Message = "Exit code 1";
            f.Hint = "Run setup.exe /apikey 9f8e7d6c5b4a by hand";
            s.Findings.Add(f);
            string json = Secrets.MaskedCopy(s).ToJson();
            T.Check(json.IndexOf("Hunter2x", StringComparison.Ordinal) < 0, "masked copy hides a secret in a finding title");
            T.Check(json.IndexOf("9f8e7d6c5b4a", StringComparison.Ordinal) < 0, "masked copy hides a secret in a finding hint");
        }

        static void CsvLeadingCharacters()
        {
            FleetStats st = new FleetStats();
            FleetItem a = new FleetItem();
            a.Name = "\t=HYPERLINK(\"http://x\")";
            a.Kind = Kinds.Application;
            st.Items.Add(a);
            string csv = st.ToCsv();
            T.Check(csv.Contains("\"'\t=HYPERLINK"), "a value that starts with a tab and a formula is kept as text in the CSV");
        }

        static List<LogLine> Lines(string text)
        {
            int ln = 0, used;
            return CmLog.Parse(text, "test.log", ref ln, true, TimeZoneInfo.Utc, out used);
        }

        static void SimpleFormat()
        {
            List<LogLine> l = Lines("Installing app   $$<AppEnforce><11-13-2026 10:02:03.123-060><thread=12 (0xC)>\r\n");
            T.Eq(1, l.Count, "one simple line");
            T.Eq("Installing app", l[0].Text, "simple line text");
            T.Eq("AppEnforce", l[0].Component, "simple line component");
            T.Eq("12 (0xC)", l[0].Thread, "simple line thread");
            T.Check(l[0].Utc.HasValue && l[0].Utc.Value == new DateTime(2026, 11, 13, 9, 2, 3, 123, DateTimeKind.Utc), "simple line time");
            l = Lines("a > b $$<Comp><11-13-2026 10:02:03.000+000><thread=1>\r\n");
            T.Eq("a > b", l[0].Text, "a simple line with > in its text");
            T.Eq("Comp", l[0].Component, "its component");
            l = Lines("$$<Comp><11-13-2026 10:02:03.000+000><thread=1>\r\n");
            T.Eq("", l[0].Text, "a simple line without text");
            l = Lines("plain text $$<not a tail\r\n");
            T.Eq("plain text $$<not a tail", l[0].Text, "a plain line stays as it is");
            T.Eq(null, l[0].Component, "a plain line has no component");

            // A long run of spaces was tried from every position: minutes for one line.
            string spaces = "x" + new string(' ', 80000) + "y\r\n" + Repeat("$$<", 40000) + "\r\n";
            Stopwatch w = Stopwatch.StartNew();
            l = Lines(spaces);
            T.Check(w.Elapsed.TotalSeconds < 3, "long plain lines take " + w.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            T.Eq(2, l.Count, "both long lines are read");
        }

        static void CrazyTimes()
        {
            string[] times = { "99999999:00:00.000+000", "10:00:00.000+2147483647", "10:99999999:00.000", "10:00:1e300", "23:59:59.000-1440" };
            foreach (string t in times)
            {
                string line = "<![LOG[hello]LOG]!><time=\"" + t + "\" date=\"12-31-9999\" component=\"c\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n";
                List<LogLine> l = null;
                try { l = Lines(line); }
                catch (Exception ex) { T.Check(false, "a crafted time stops the log: " + t + ": " + ex.GetType().Name); continue; }
                T.Check(l.Count == 1 && l[0].Text == "hello", "a line with time " + t + " is still read");
            }
            T.Check(CmLog.Time("11-13-2026", "10:02:03.000+000", TimeZoneInfo.Utc) == new DateTime(2026, 11, 13, 10, 2, 3, DateTimeKind.Utc), "an ordinary time is still read");
        }

        sealed class Throwing : LogParser
        {
            public override string Source
            {
                get { return "test"; }
            }

            public override void Read(LogLine l, List<Obs> output)
            {
                output.Add(Obs.Log(l, Source));
                if (l.Text == "bad") throw new InvalidOperationException("crafted");
            }
        }

        static void SafeParser()
        {
            List<Obs> output = new List<Obs>();
            Throwing p = new Throwing();
            LogLine good = new LogLine();
            good.Text = "good";
            LogLine bad = new LogLine();
            bad.Text = "bad";
            T.Check(LogParser.Safe(p, good, output), "a good line is read");
            T.Check(!LogParser.Safe(p, bad, output), "a line the parser throws on is skipped");
            T.Check(LogParser.Safe(p, good, output), "the line after it is read");
            T.Eq(2, output.Count, "the skipped line leaves nothing half done");
        }

        static void SmstsSlowLine()
        {
            LogLine l = new LogLine();
            l.Text = Repeat("Start executing an instruction. Instruction name: ", 2000);
            List<Obs> output = new List<Obs>();
            Stopwatch w = Stopwatch.StartNew();
            LogParser.Safe(new SmstsParser(), l, output);
            T.Check(w.Elapsed.TotalSeconds < 5, "an smsts.log line that repeats the step start takes " + w.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            l.Text = "Start executing an instruction. Instruction name: Format Disk. Instruction pointer: 3";
            output.Clear();
            T.Check(LogParser.Safe(new SmstsParser(), l, output) && output.Count == 1 && output[0].ItemName == "Format Disk", "an ordinary step start is still read");
        }

        static void PlantedSmsts()
        {
            string dir = Path.Combine(Path.GetTempPath(), "trackvice-planted-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                DateTime now = DateTime.UtcNow;
                string line = "<![LOG[Start executing an instruction. Instruction name: Planted Step. Instruction pointer: 1]LOG]!><time=\""
                    + now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+000\" date=\"" + now.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture)
                    + "\" component=\"TSManager\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n";
                string path = Path.Combine(dir, "smsts.log");
                File.WriteAllText(path, line, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "smsts-20261101-101010.log"), line.Replace("Planted Step", "Planted Old Step"), new UTF8Encoding(false));
                List<string> paths = new List<string> { path };

                foreach (bool trusted in new bool[] { false, true })
                {
                    LiveCollector c = new LiveCollector(now.AddHours(-1));
                    c.UseWmi = false;
                    c.UseEvents = false;
                    c.SmstsSource = delegate { return paths; };
                    c.SmstsTrust = delegate (FileStream fs) { return trusted; };
                    Batch b = c.Read(now);
                    int steps = 0;
                    foreach (Obs o in b.Obs) if (o.ItemKind == Kinds.Step && o.ItemName != null && o.ItemName.StartsWith("Planted", StringComparison.Ordinal)) steps++;
                    if (trusted) T.Eq(2, steps, "an smsts.log owned by SYSTEM or the administrators is read, with its older copy");
                    else
                    {
                        T.Eq(0, steps, "an smsts.log anyone could have written is not read, nor its older copy");
                        bool noted = false;
                        foreach (string n in b.Notes) if (n.Contains("not owned by SYSTEM")) noted = true;
                        T.Check(noted, "the session says why the smsts.log was not read");
                    }
                    c.Dispose();
                }
                T.Check(LogTrust.IsAdminSid("S-1-5-18") && LogTrust.IsAdminSid("S-1-5-32-544") && !LogTrust.IsAdminSid("S-1-5-21-1-2-3-1001") && !LogTrust.IsAdminSid("S-1-5-32-545"), "only SYSTEM, administrators and TrustedInstaller count as owners");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void ImeNamesSlowInput()
        {
            // Each marker searched and copied the rest of the text again.
            string text = Repeat("Get policies =[", 40000);
            Stopwatch w = Stopwatch.StartNew();
            Dictionary<string, string> names = ImeLog.ParseNames(text);
            T.Check(w.Elapsed.TotalSeconds < 5, "IME names from a line that repeats the marker take " + w.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            T.Eq(0, names.Count, "no names from it");

            string two = "<![LOG[Get policies = [{\"Id\":\"11111111-2222-3333-4444-555555555555\",\"Name\":\"Contoso One\"}] and Get policies = [{\"Id\":\"66666666-7777-8888-9999-000000000000\",\"Name\":\"Contoso Two\"}]]LOG]!><time=\"10:00:00.0000000\" date=\"11-13-2026\" component=\"IME\">\r\n"
                + "<![LOG[Get policies = [{\"Id\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"Name\":\"Contoso Three\"}]]LOG]!><time=\"10:00:01.0000000\" date=\"11-13-2026\" component=\"IME\">\r\n";
            names = ImeLog.ParseNames(two);
            T.Eq("Contoso One", Get(names, "11111111-2222-3333-4444-555555555555"), "first marker in a line");
            T.Eq("Contoso Two", Get(names, "66666666-7777-8888-9999-000000000000"), "second marker in the same line");
            T.Eq("Contoso Three", Get(names, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "marker in the next line");
        }

        static string Get(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : null;
        }

        static void FirstSyncHugeDuration()
        {
            const string Fs = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Enrollments\1A2B3C4D-0000-4000-8000-000000000009\FirstSync";
            DateTime t0 = new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);
            byte[] ft = BitConverter.GetBytes(t0.ToFileTimeUtc());
            string[] h = new string[8];
            for (int i = 0; i < 8; i++) h[i] = ft[i].ToString("x2");
            MemoryRegistry r = new MemoryRegistry();
            RegFile.Parse("[" + Fs + "]\r\n\"SyncFailureTimeout\"=dword:0000003c\r\n\"Timestamp\"=hex:" + string.Join(",", h) + "\r\n"
                + "\"PolicyDuration\"=hex(b):ff,ff,ff,ff,ff,ff,ff,7f\r\n\"CertificatesDuration\"=dword:0000000a\r\n", r);
            List<Obs> l = null;
            try { l = new EspFirstSync().Read(r, t0.AddMinutes(30)); }
            catch (Exception ex) { T.Check(false, "a huge ESP step time throws: " + ex.Message); return; }
            bool certs = false;
            foreach (Obs o in l) if (o.ItemKey == "esp:step:device:certificates" && o.State == State.Succeeded) certs = true;
            T.Check(certs, "a huge time on one ESP step does not lose the other steps");
        }

        static void SccmAppsSlowInput()
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ActionOutput"] = "x" + new string(' ', 60000) + "y";
            r["LastStatusMsgName"] = Repeat("application ", 6000);
            string name, id;
            Stopwatch w = Stopwatch.StartNew();
            bool ok = SccmApps.Parse(r, out name, out id);
            T.Check(!ok && w.Elapsed.TotalSeconds < 3, "a long status message takes " + w.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            r["ActionOutput"] = "Contoso App (ScopeId_1A2B3C4D-0000/Application_1234abcd-0000-1111-2222-333344445555)";
            T.Check(SccmApps.Parse(r, out name, out id) && name == "Contoso App" && id == "ScopeId_1A2B3C4D-0000/Application_1234abcd-0000-1111-2222-333344445555", "an ordinary application status is still read");
        }
    }
}
