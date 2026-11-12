namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // One line a log gets, or one registry value that changes, at a time in seconds from the start of the scenario.
    public sealed class DemoStep
    {
        public double At;
        public string Log;
        public string Component;
        public int Type = 1;
        public string Thread = "4";
        public string Text;
        public string RegPath;
        public int RegValue;
    }

    public sealed class DemoScenario
    {
        public string Name;
        public string Computer, User, Os;
        public double StartAt, Length;
        public readonly List<DemoStep> Steps = new List<DemoStep>();
        public readonly List<Pair> Info = new List<Pair>();
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        double at;

        public DemoScenario At(double minutes)
        {
            at = minutes * 60;
            return this;
        }

        public DemoScenario After(double seconds)
        {
            at += seconds;
            return this;
        }

        public DemoScenario Line(string log, string component, string text)
        {
            return Line(log, component, 1, text);
        }

        public DemoScenario Line(string log, string component, int type, string text)
        {
            DemoStep s = new DemoStep();
            s.At = at;
            s.Log = log;
            s.Component = component;
            s.Type = type;
            s.Text = text;
            Steps.Add(s);
            at += 0.4;
            return this;
        }

        public DemoScenario Reg(string relPath, int state)
        {
            DemoStep s = new DemoStep();
            s.At = at;
            s.RegPath = EspWatch.Root + "\\" + relPath;
            s.RegValue = state;
            Steps.Add(s);
            at += 0.2;
            return this;
        }

        public void Sort()
        {
            List<DemoStep> l = new List<DemoStep>(Steps);
            for (int i = 0; i < l.Count; i++) l[i].Thread = l[i].Thread ?? "4";
            l.Sort(delegate (DemoStep a, DemoStep b) { return a.At.CompareTo(b.At); });
            Steps.Clear();
            Steps.AddRange(l);
            Length = 0;
            foreach (DemoStep s in Steps) Length = Math.Max(Length, s.At);
        }
    }

    // Plays a scenario: writes each line in the real log format and reads it back through the real parsers,
    // and changes an in-memory registry that the real ESP reader reads.
    public sealed class DemoPlayer
    {
        public readonly DemoScenario Scenario;
        public readonly DateTime StartUtc;
        readonly MemoryRegistry reg = new MemoryRegistry();
        readonly EspWatch esp = new EspWatch();
        readonly Dictionary<string, LogParser> parsers = new Dictionary<string, LogParser>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> lineNos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int next;
        public double Position;

        public DemoPlayer(DemoScenario sc, DateTime startUtc)
        {
            Scenario = sc;
            StartUtc = startUtc;
            esp.Names = delegate (string id) { string n; return sc.Names.TryGetValue(id, out n) ? n : null; };
        }

        public DateTime Clock
        {
            get { return StartUtc.AddSeconds(Position); }
        }

        public bool Done
        {
            get { return next >= Scenario.Steps.Count; }
        }

        public Batch AdvanceTo(double seconds)
        {
            Batch b = new Batch();
            if (Position == 0 && next == 0)
            {
                b.Names = new Dictionary<string, string>(Scenario.Names, StringComparer.OrdinalIgnoreCase);
                b.Info.AddRange(Scenario.Info);
            }
            while (next < Scenario.Steps.Count && Scenario.Steps[next].At <= seconds)
            {
                DemoStep s = Scenario.Steps[next++];
                DateTime t = StartUtc.AddSeconds(s.At);
                if (s.RegPath != null)
                {
                    reg.Clock = t;
                    reg.Dword(s.RegPath, "InstallationState", s.RegValue);
                    b.Obs.AddRange(esp.Read(reg, t));
                    continue;
                }
                Feed(s, t, b);
            }
            Position = Math.Max(Position, seconds);
            return b;
        }

        void Feed(DemoStep s, DateTime t, Batch b)
        {
            string baseName = s.Log.StartsWith("smsts", StringComparison.OrdinalIgnoreCase) ? "smsts" : s.Log;
            LogParser p;
            if (!parsers.TryGetValue(baseName, out p))
            {
                p = LogParser.For(baseName);
                parsers[baseName] = p;
            }
            string path;
            if (!Scenario.Paths.TryGetValue(s.Log, out path)) path = s.Log + ".log";
            string raw = Format(s, t);
            int ln;
            lineNos.TryGetValue(path, out ln);
            int used;
            foreach (LogLine l in CmLog.Parse(raw + "\r\n", path, ref ln, true, TimeZoneInfo.Local, out used)) p.Read(l, b.Obs);
            lineNos[path] = ln;
            if (!b.Sources.Contains(path)) b.Sources.Add(path);
        }

        // Writes the scenario's log lines to real files in a folder, named as on a device. Used by the tests to read a demo as a log folder.
        public static void WriteLogs(DemoScenario sc, DateTime startUtc, string folder)
        {
            System.Collections.Generic.Dictionary<string, StringBuilder> files = new System.Collections.Generic.Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            foreach (DemoStep s in sc.Steps)
            {
                if (s.RegPath != null) continue;
                string path;
                if (!sc.Paths.TryGetValue(s.Log, out path)) path = s.Log + ".log";
                string name = path.Substring(path.LastIndexOf('\\') + 1);
                string sub = s.Log == "smsts-pe" ? "pe" : s.Log == "smsts-os" ? "os" : "";
                string key = System.IO.Path.Combine(System.IO.Path.Combine(folder, sub), name);
                StringBuilder b;
                if (!files.TryGetValue(key, out b)) { b = new StringBuilder(); files[key] = b; }
                b.Append(Format(s, startUtc.AddSeconds(s.At))).Append("\r\n");
            }
            foreach (System.Collections.Generic.KeyValuePair<string, StringBuilder> kv in files)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(kv.Key));
                System.IO.File.WriteAllText(kv.Key, kv.Value.ToString(), new UTF8Encoding(false));
            }
        }

        // The IME writes local time with seven decimals and no bias; ConfigMgr writes milliseconds and the bias.
        static string Format(DemoStep s, DateTime utc)
        {
            DateTime local = utc.ToLocalTime();
            bool ime = s.Log == "IntuneManagementExtension" || s.Log == "AppWorkload" || s.Log == "AgentExecutor";
            string time;
            if (ime) time = local.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            else
            {
                int bias = -(int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(utc).TotalMinutes);
                time = local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + (bias < 0 ? "-" : "+") + Math.Abs(bias).ToString("000", CultureInfo.InvariantCulture);
            }
            string date = local.ToString(ime ? "M-d-yyyy" : "MM-dd-yyyy", CultureInfo.InvariantCulture);
            StringBuilder b = new StringBuilder();
            b.Append("<![LOG[").Append(s.Text).Append("]LOG]!><time=\"").Append(time).Append("\" date=\"").Append(date)
             .Append("\" component=\"").Append(s.Component).Append("\" context=\"\" type=\"").Append(s.Type.ToString(CultureInfo.InvariantCulture))
             .Append("\" thread=\"").Append(s.Thread).Append("\" file=\"\">");
            return b.ToString();
        }
    }

    public static class Demo
    {
        const string Ime = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\";
        const string Ccm = @"C:\Windows\CCM\Logs\";
        const string Sid = "S-1-12-1-2841156872-1293587611-3128474290-1572936428";

        public static DemoScenario Get(string name)
        {
            return name != null && name.Equals("osd", StringComparison.OrdinalIgnoreCase) ? Osd() : Autopilot();
        }

        // ---------- Autopilot: user-driven Entra join, one app retried, one failed, one that never finishes ----------

        public static DemoScenario Autopilot()
        {
            DemoScenario d = new DemoScenario();
            d.Name = "autopilot";
            d.Computer = "CONTOSO-LT0417";
            d.User = "anna.hansen@contoso.com";
            d.Os = "Windows 11 Enterprise 24H2 (26100.7171)";
            d.Info.Add(new Pair("Autopilot profile", "Contoso standard user-driven"));
            d.Info.Add(new Pair("Tenant", "contoso.onmicrosoft.com"));
            d.Info.Add(new Pair("Join type", "Entra join"));
            d.Info.Add(new Pair("ESP time limit (min)", "60"));
            d.Paths["AppWorkload"] = Ime + "AppWorkload.log";
            d.Paths["IntuneManagementExtension"] = Ime + "IntuneManagementExtension.log";

            string m365 = "3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01", vpn = "a1c47e92-0d3b-4f5e-8a6c-2b9d1e4f7a02", zip = "6e2b9d14-7c8a-4b3f-a5d1-9e0f2c6b8d03";
            string teams = "9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f04", acrobat = "4d7f1a3c-8e2b-4c6d-9a5f-0b3e7d1c5a05", sheet = "c5e8a2d6-1f4b-4a9c-8d3e-6b0f2a7c4e06";
            string cp = "e7b1d4f8-3c6a-4e2d-a9b5-8f0c1d3e6a07";
            d.Names[m365] = "Microsoft 365 Apps for enterprise";
            d.Names[vpn] = "Contoso VPN Client 5.2";
            d.Names[zip] = "7-Zip 24.08";
            d.Names[teams] = "Microsoft Teams";
            d.Names[acrobat] = "Adobe Acrobat Reader";
            d.Names[sheet] = "Contoso Timesheet";
            d.Names[cp] = "Company Portal";

            string prep = @"Device\DevicePreparation\";
            string dev = @"Device\Setup\Apps\Tracking\Sidecar\";
            string user = Sid + @"\Apps\Tracking\Sidecar\";

            // Device preparation
            d.At(0).Reg(prep + @"PolicyProviders\SecurityBaseline", 2).Reg(prep + "SidecarProvisioning", 1);
            d.At(0.9).Reg(prep + @"PolicyProviders\SecurityBaseline", 3).Reg(prep + "SidecarProvisioning", 2);
            d.At(1.3).Line("IntuneManagementExtension", "IntuneManagementExtension", "[Win32App] Intune Management Extension started, ESP mode: DevicePreparation");
            d.At(2.6).Reg(prep + "SidecarProvisioning", 3);

            // Device setup: the ESP lists what it waits for
            d.At(3.0).Reg(@"Device\Setup\Certificates\Contoso Device Certificate", 1).Reg(@"Device\Setup\Profiles\Wi-Fi Contoso Corp", 1)
             .Reg(dev + "Win32App_" + m365 + "_1", 1).Reg(dev + "Win32App_" + vpn + "_1", 1).Reg(dev + "Win32App_" + zip + "_1", 1)
             .Reg(@"Device\Setup\Apps\Tracking\ModernApp\ModernApp_" + cp, 1);
            d.At(3.4).Reg(@"Device\Setup\Certificates\Contoso Device Certificate", 2).Reg(@"Device\Setup\Profiles\Wi-Fi Contoso Corp", 2);
            d.At(4.1).Reg(@"Device\Setup\Profiles\Wi-Fi Contoso Corp", 3);
            d.At(4.8).Reg(@"Device\Setup\Certificates\Contoso Device Certificate", 3);

            // Microsoft 365 Apps: a big download through Delivery Optimization
            ImeApp(d, 3.6, m365, dev + "Win32App_" + m365 + "_1", 1, new int[] { 6, 19, 34, 51, 67, 82, 94, 100 }, 1.1, 6.5, "0");
            // 7-Zip
            ImeApp(d, 11.5, zip, dev + "Win32App_" + zip + "_1", 2, new int[] { 100 }, 0.2, 0.6, "0");
            // Contoso VPN Client: another install is running twice (1618), then it works
            d.At(12.6).Reg(dev + "Win32App_" + vpn + "_1", 2);
            Download(d, 12.6, vpn, new int[] { 48, 100 }, 0.3);
            for (int k = 0; k < 3; k++)
            {
                double s = 13.4 + k * 1.6;
                d.At(s).Line("AppWorkload", "AppWorkload", "[Win32App][ExecutionActionHandler] Handler invoked for policy with id: " + vpn + ", starting to execute install command.")
                 .Line("AppWorkload", "AppWorkload", "[Win32App][Win32AppExecutionExecutor] Launch Win32AppInstaller in machine session for app with id: " + vpn + ". Command line: msiexec /i \"ContosoVPN-5.2.1.msi\" /qn /norestart");
                if (k < 2)
                {
                    d.At(s + 0.7).Line("AppWorkload", "AppWorkload", "[Win32App][Win32AppExecutionExecutor] app with id: " + vpn + " lpExitCode 1618")
                     .Line("AppWorkload", "AppWorkload", 2, "[Win32App][ReportingManager] Execution state for app with id: " + vpn + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Error\",\"ErrorCode\":\"null -> 1618\"}");
                    d.After(20).Line("AppWorkload", "AppWorkload", "[Win32App][ActionProcessor] Retry for app with id: " + vpn + " in 60 seconds: another installation is in progress (1618).");
                }
                else
                {
                    d.At(s + 0.9).Line("AppWorkload", "AppWorkload", "[Win32App][Win32AppExecutionExecutor] app with id: " + vpn + " lpExitCode 0")
                     .Line("AppWorkload", "AppWorkload", "[Win32App][DetectionActionHandler] Detection for policy with id: " + vpn + " resulted in action status: Success and detection state: Detected.")
                     .Line("AppWorkload", "AppWorkload", "[Win32App][ReportingManager] Execution state for app with id: " + vpn + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Success\",\"ErrorCode\":\"1618 -> 0\"}");
                    d.Reg(dev + "Win32App_" + vpn + "_1", 3);
                }
            }
            // Company Portal from the Store
            d.At(17.2).Reg(@"Device\Setup\Apps\Tracking\ModernApp\ModernApp_" + cp, 2);
            d.At(18.9).Reg(@"Device\Setup\Apps\Tracking\ModernApp\ModernApp_" + cp, 3);

            // Account setup after sign-in
            d.At(21.5).Reg(Sid + @"\Setup\Policies\UserPolicies", 2).Reg(user + "Win32App_" + teams + "_1", 1).Reg(user + "Win32App_" + sheet + "_1", 1).Reg(user + "Win32App_" + acrobat + "_1", 1);
            d.At(22.4).Reg(Sid + @"\Setup\Policies\UserPolicies", 3);
            ImeApp(d, 22.6, teams, user + "Win32App_" + teams + "_1", 3, new int[] { 22, 58, 100 }, 0.6, 1.4, "0");
            // Contoso Timesheet: a fatal error in the MSI
            d.At(27.0).Reg(user + "Win32App_" + sheet + "_1", 2);
            Download(d, 27.0, sheet, new int[] { 100 }, 0.2);
            d.At(27.5).Line("AppWorkload", "AppWorkload", "[Win32App][ExecutionActionHandler] Handler invoked for policy with id: " + sheet + ", starting to execute install command.")
             .Line("AppWorkload", "AppWorkload", "[Win32App][Win32AppExecutionExecutor] Launch Win32AppInstaller in user session for app with id: " + sheet + ". Command line: msiexec /i \"Timesheet.msi\" /qn ALLUSERS=\"\"");
            d.At(28.6).Line("AppWorkload", "AppWorkload", 3, "[Win32App][Win32AppExecutionExecutor] Admin did NOT set mapping for lpExitCode: 1603 of app: " + sheet + ".")
             .Line("AppWorkload", "AppWorkload", 3, "[Win32App][ReportingManager] Execution state for app with id: " + sheet + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Error\",\"ErrorCode\":\"null -> 1603\"}");
            d.Reg(user + "Win32App_" + sheet + "_1", 4);
            // Adobe Acrobat Reader: downloads, starts, and never finishes
            d.At(29.0).Reg(user + "Win32App_" + acrobat + "_1", 2);
            Download(d, 29.0, acrobat, new int[] { 9, 27, 46, 63, 81, 100 }, 0.45);
            d.At(32.2).Line("AppWorkload", "AppWorkload", "[Win32App][ExecutionActionHandler] Handler invoked for policy with id: " + acrobat + ", starting to execute install command.")
             .Line("AppWorkload", "AppWorkload", "[Win32App][Win32AppExecutionExecutor] Launch Win32AppInstaller in machine session for app with id: " + acrobat + ". Command line: AcroRdrDCx642500120432_MUI.exe /sAll /rs /msi EULA_ACCEPT=YES");
            for (int k = 1; k <= 11; k++)
                d.At(32.2 + k * 5).Line("AppWorkload", "AppWorkload", 2, "[Win32App][Win32AppExecutionExecutor] Waiting for app with id: " + acrobat + ": the installer process is still running after " + (k * 5) + " minutes.");
            d.At(82.0).Line("IntuneManagementExtension", "IntuneManagementExtension", 2, "[Win32App] ESP time limit reached for app with id: " + acrobat + " while the installer process is still running.");
            d.Sort();
            d.StartAt = 30.6 * 60;
            return d;
        }

        static void Download(DemoScenario d, double startMin, string id, int[] pcts, double stepMin)
        {
            d.At(startMin).Line("AppWorkload", "AppWorkload", "[Win32App][DownloadActionHandler] Starting content download for app with id: " + id + ".");
            double t = startMin;
            foreach (int p in pcts)
            {
                t += stepMin;
                d.At(t).Line("AppWorkload", "AppWorkload", "[Win32App DO] DO job progress for app id: " + id + ", downloaded " + p + " %");
            }
            d.At(t + 0.1).Line("AppWorkload", "AppWorkload", "[Win32App][DownloadActionHandler] Content is downloaded and hash validation passed for app with id: " + id + ".");
        }

        static void ImeApp(DemoScenario d, double startMin, string id, string espPath, int thread, int[] pcts, double stepMin, double installMin, string exit)
        {
            d.At(startMin).Reg(espPath, 2);
            Download(d, startMin, id, pcts, stepMin);
            double s = startMin + pcts.Length * stepMin + 0.3;
            d.At(s).Line("AppWorkload", "AppWorkload", "[Win32App][ExecutionActionHandler] Handler invoked for policy with id: " + id + ", starting to execute install command.")
             .Line("AppWorkload", "AppWorkload", "[Win32App][Win32AppExecutionExecutor] Launch Win32AppInstaller in machine session for app with id: " + id + ".");
            d.At(s + installMin).Line("AppWorkload", "AppWorkload", "[Win32App][Win32AppExecutionExecutor] app with id: " + id + " lpExitCode " + exit)
             .Line("AppWorkload", "AppWorkload", "[Win32App][DetectionActionHandler] Detection for policy with id: " + id + " resulted in action status: Success and detection state: Detected.")
             .Line("AppWorkload", "AppWorkload", "[Win32App][ReportingManager] Execution state for app with id: " + id + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Success\",\"ErrorCode\":\"null -> 0\"}");
            d.Reg(espPath, 3);
        }

        // ---------- SCCM task sequence: WinPE, full OS, applications, updates, and an app that is not detected ----------

        public static DemoScenario Osd()
        {
            DemoScenario d = new DemoScenario();
            d.Name = "osd";
            d.Computer = "CONTOSO-PC117";
            d.User = "SYSTEM";
            d.Os = "Windows 11 Enterprise 24H2 (26100.7171)";
            d.Paths["smsts-pe"] = @"X:\Windows\Temp\SMSTSLog\smsts.log";
            d.Paths["smsts-os"] = @"C:\_SMSTaskSequence\Logs\Smstslog\smsts.log";
            d.Paths["smsts"] = Ccm + @"SMSTSLog\smsts.log";
            foreach (string n in new string[] { "AppEnforce", "CAS", "UpdatesDeployment", "AppDiscovery" }) d.Paths[n] = Ccm + n + ".log";

            d.At(0).Line("smsts-pe", "TSManager", "Set a global environment variable _SMSTSInWinPE=TRUE")
             .Line("smsts-pe", "TSManager", "Set a global environment variable _SMSTSPackageName=Windows 11 24H2 Standard")
             .Line("smsts-pe", "TSManager", "Set a global environment variable _SMSTSPackageID=P0100231")
             .Line("smsts-pe", "TSManager", "Set a global environment variable _SMSTSAdvertID=P0120006");
            double t = 0.2;
            Step(d, "smsts-pe", ref t, 1, "Partition Disk 0 - UEFI", 0.6, "osddiskpart.exe", 0);
            Step(d, "smsts-pe", ref t, 2, "Apply Operating System", 7.8, "osdapplyos.exe /image:P0100212,1 /target:C: /runfromnet:False", 0);
            Step(d, "smsts-pe", ref t, 3, "Apply Windows Settings", 0.2, "osdwinsettings.exe /config", 0);
            Step(d, "smsts-pe", ref t, 4, "Apply Network Settings", 0.1, "osdnetsettings.exe configure", 0);
            Step(d, "smsts-pe", ref t, 5, "Apply Device Drivers - Dell Latitude 7450", 3.4, "osddriverclient.exe /auto /bestmatch:True", 0);
            Step(d, "smsts-pe", ref t, 6, "Setup Windows and Configuration Manager", 1.1, "OSDSetupWindows.exe /install", 0);
            d.At(t).Line("smsts-pe", "TSManager", "Rebooting system to apply Windows setup");
            t += 6.5;
            d.At(t).Line("smsts-os", "TSManager", "Set a global environment variable _SMSTSInWinPE=FALSE");
            t += 0.3;
            Step(d, "smsts-os", ref t, 7, "Install Microsoft 365 Apps", 0, "smsappinstall.exe /app:ScopeId_7E3A/Application_1b2c", -1);
            double appStart = t;
            // The application install the step runs
            Content(d, appStart, "Content_6f2c1d0e-8a1b-4b7e-9d2a-3c4f5e6a7b81.1", 2.4);
            Enforce(d, appStart + 2.6, "Microsoft 365 Apps - Click-to-Run", "ScopeId_7E3A/DeploymentType_5d1e", "setup.exe /configure configuration.xml", 0, "Success", true, 9.2);
            t = appStart + 12.1;
            d.At(t).Line("smsts-os", "TSManager", "Successfully completed the action (Install Microsoft 365 Apps) with the exit win32 code 0");
            t += 0.3;
            Step(d, "smsts-os", ref t, 8, "Install Language Pack da-DK", 0.8, "DISM.exe /Online /Add-Package /PackagePath:.\\da-DK", 0);
            d.At(t).Line("smsts-os", "TSManager", "The action (Install Language Pack fr-FR) has been skipped because the condition is evaluated to be false");
            t += 0.3;
            // Software updates
            Step(d, "smsts-os", ref t, 10, "Install Software Updates", 0, "smsappinstall.exe /updates", -1);
            double u = t;
            d.At(u).Line("UpdatesDeployment", "UpdatesDeploymentAgent", "Update (Site_1F2E3D4C/SUM_8b2f1c3d-4e5a-46b7-8c9d-0e1f2a3b4c5d) Name (2026-11 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5068861)) ArticleID (5068861) added to the targeted list of deployment ({AB12CD34-0000-4000-8000-000000000001})")
             .Line("UpdatesDeployment", "UpdatesDeploymentAgent", "Update (Site_1F2E3D4C/SUM_1a2b3c4d-5e6f-4789-a0b1-c2d3e4f5a6b7) Name (2026-11 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11 24H2 (KB5068712)) ArticleID (5068712) added to the targeted list of deployment ({AB12CD34-0000-4000-8000-000000000001})");
            int[] steps = { 0, 15, 40, 70, 100 };
            for (int k = 0; k < steps.Length; k++)
                d.At(u + 0.3 + k * 1.1).Line("UpdatesDeployment", "UpdatesDeploymentAgent", "Update (Site_1F2E3D4C/SUM_8b2f1c3d-4e5a-46b7-8c9d-0e1f2a3b4c5d) Progress: Status = ciStateDownloading, PercentComplete = " + steps[k] + ", Result = 0x0");
            d.At(u + 5.8).Line("UpdatesDeployment", "UpdatesDeploymentAgent", "Update (Site_1F2E3D4C/SUM_1a2b3c4d-5e6f-4789-a0b1-c2d3e4f5a6b7) Progress: Status = ciStateInstalling, PercentComplete = 0, Result = 0x0");
            d.At(u + 7.4).Line("UpdatesDeployment", "UpdatesDeploymentAgent", "Update (Site_1F2E3D4C/SUM_1a2b3c4d-5e6f-4789-a0b1-c2d3e4f5a6b7) Progress: Status = ciStateInstallComplete, PercentComplete = 100, Result = 0x0");
            d.At(u + 7.6).Line("UpdatesDeployment", "UpdatesDeploymentAgent", "Update (Site_1F2E3D4C/SUM_8b2f1c3d-4e5a-46b7-8c9d-0e1f2a3b4c5d) Progress: Status = ciStateInstalling, PercentComplete = 0, Result = 0x0");
            d.At(u + 16.9).Line("UpdatesDeployment", "UpdatesDeploymentAgent", "Update (Site_1F2E3D4C/SUM_8b2f1c3d-4e5a-46b7-8c9d-0e1f2a3b4c5d) Progress: Status = ciStatePendingSoftReboot, PercentComplete = 100, Result = 0x0");
            t = u + 17.2;
            d.At(t).Line("smsts-os", "TSManager", "Successfully completed the action (Install Software Updates) with the exit win32 code 0");
            t += 0.2;
            Step(d, "smsts-os", ref t, 11, "Restart Computer", 0.1, "smsboot.exe /target:HD", 0);
            d.At(t).Line("smsts-os", "TSManager", "Rebooting system to finish installing updates");
            t += 3.2;
            // Applications, after the restart: smsts.log now lives in the CCM log folder
            Step(d, "smsts", ref t, 12, "Install Applications", 0, "smsappinstall.exe /app:ScopeId_7E3A/Application_2c3d,ScopeId_7E3A/Application_3d4e,ScopeId_7E3A/Application_4e5f", -1);
            double a = t;
            Content(d, a, "Content_1d2e3f40-5a6b-4c7d-8e9f-0a1b2c3d4e51.3", 0.6);
            Enforce(d, a + 0.8, "Notepad++ (x64) - MSI", "ScopeId_7E3A/DeploymentType_6e7f", "msiexec /i \"npp.8.7.1.Installer.x64.msi\" /qn", 0, "Success", true, 0.7);
            Content(d, a + 1.8, "Content_2e3f4051-6b7c-4d8e-9f0a-1b2c3d4e5f62.1", 1.2);
            Enforce(d, a + 3.2, "Contoso CRM Client - MSI", "ScopeId_7E3A/DeploymentType_7f80", "msiexec /i \"ContosoCRM.msi\" /qn /norestart", 0, "Success", false, 2.1);
            Content(d, a + 5.6, "Content_3f405162-7c8d-4e9f-a01b-2c3d4e5f6a73.2", 4.6);
            Enforce(d, a + 10.4, "Contoso Design Suite - Setup", "ScopeId_7E3A/DeploymentType_8091", "setup.exe /quiet /norestart", 0, "Success", true, 14.0);
            t = a + 24.8;
            d.At(t).Line("smsts", "InstallApplication", 3, "Install Policy for ScopeId_7E3A/Application_3d4e failed: the application was not detected after installation completed (0x87D00324)")
             .Line("smsts", "TSManager", 3, "Failed to run the action: Install Applications. Error 0x87D00324 (Error: 87D00324; Source: CCM)")
             .Line("smsts", "TSManager", 3, "Task sequence execution failed with error code 87D00324");
            d.Sort();
            d.StartAt = 34.0 * 60;
            return d;
        }

        static void Step(DemoScenario d, string log, ref double t, int ip, string name, double minutes, string cmd, int exit)
        {
            d.At(t).Line(log, "TSManager", "Start executing an instruction. Instruction name: " + name + ". Instruction pointer: " + ip)
             .Line(log, "TSManager", "Set a global environment variable _SMSTSCurrentActionName=" + name)
             .Line(log, "TSManager", "Executing command line: " + cmd);
            if (exit < 0) return;
            t += minutes;
            d.At(t).Line(log, "TSManager", "Process completed with exit code " + exit)
             .Line(log, "TSManager", "Successfully completed the action (" + name + ") with the exit win32 code " + exit);
            t += 0.15;
        }

        static void Content(DemoScenario d, double t, string id, double minutes)
        {
            d.At(t).Line("CAS", "ContentAccess", "Download started for content " + id + " under context System");
            d.At(t + minutes).Line("CAS", "ContentAccess", "Download completed for content " + id + " under context System")
             .Line("CAS", "ContentAccess", "Saved Content ID Mapping " + id + ", C:\\Windows\\ccmcache\\" + (char)('a' + (id.Length % 20)));
        }

        static void Enforce(DemoScenario d, double t, string dt, string id, string cmd, int exit, string kind, bool detected, double minutes)
        {
            d.At(t).Line("AppEnforce", "AppEnforce", "+++ Starting Install enforcement for App DT \"" + dt + "\" ApplicationDeliveryType - " + id + ", Revision - 4, ContentPath - C:\\Windows\\ccmcache\\4, Execution Context - System")
             .Line("AppEnforce", "AppEnforce", "+++ Application not discovered. [AppDT Id: " + id + ", Revision: 4]")
             .Line("AppEnforce", "AppEnforce", "    Executing Command line: \"C:\\WINDOWS\\system32\\" + cmd + "\" with system context")
             .Line("AppEnforce", "AppEnforce", "    Waiting for process 4116 to finish.  Timeout = 120 minutes.");
            d.At(t + minutes).Line("AppEnforce", "AppEnforce", "    Process 4116 terminated with exitcode: " + exit)
             .Line("AppEnforce", "AppEnforce", "    Looking for exit code " + exit + " in exit codes table...")
             .Line("AppEnforce", "AppEnforce", "    Matched exit code " + exit + " to a " + kind + " entry in exit codes table.");
            if (detected) d.Line("AppEnforce", "AppEnforce", "+++ Discovered application [AppDT Id: " + id + ", Revision: 4]");
            else d.Line("AppEnforce", "AppEnforce", 3, "+++ Application not discovered. [AppDT Id: " + id + ", Revision: 4]");
            d.Line("AppEnforce", "AppEnforce", "++++++ App enforcement completed (" + (int)(minutes * 60) + " seconds) for App DT \"" + dt + "\" [" + id + "], Revision: 4, User SID: ] ++++++");
        }
    }
}
