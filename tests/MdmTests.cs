namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The formats here are taken from a real Autopilot enrollment, with made-up names and IDs.
    public static class MdmTests
    {
        const string App = "b2d58f03-1e4c-4a6f-9b7d-3c0e2f5a8b13", Portal = "c3e69a14-2f5d-4b70-8c8e-4d1f306b9c24";
        const string Script1 = "d4f7ab25-306e-4c81-9d9f-5e2041 7cad35", Zero = "00000000-0000-0000-0000-000000000000";
        const string Office = "e508bc36-417f-4d92-8ea0-6f31528dbe46", Msi = "f619cd47-5280-4ea3-9fb1-7042639ecf57";
        static readonly string ScriptA = Script1.Replace(" ", ""), ScriptB = "0a2ade58-6391-4fb4-80c2-8153740fd068";

        static LogLine L(string text, int n)
        {
            LogLine l = new LogLine();
            l.Text = text;
            l.Raw = text;
            l.Utc = new DateTime(2026, 11, 13, 11, 55, 0, DateTimeKind.Utc).AddSeconds(n);
            l.File = "test.log";
            l.LineNo = n;
            l.Thread = "7";
            return l;
        }

        static Session Feed(LogParser p, params string[] lines)
        {
            List<Obs> o = new List<Obs>();
            int n = 0;
            foreach (string s in lines) p.Read(L(s, ++n), o);
            return Build(o);
        }

        static Session Build(List<Obs> o)
        {
            Session ses = new Session();
            Tracker t = new Tracker(ses);
            t.ApplyAll(o);
            return ses;
        }

        public static void Run()
        {
            RegExport();
            AppLog();
            Scripts();
            Registry();
            Events();
            Names();
        }

        sealed class PagedGraph : IGraph
        {
            public int Calls;

            public Dictionary<string, object> Get(string url)
            {
                Calls++;
                string json = url == IntuneNames.ScriptsUrl
                    ? "{\"value\":[{\"id\":\"" + ScriptA + "\",\"displayName\":\"Set time zone\"},{\"id\":\"x\",\"displayName\":null}],\"@odata.nextLink\":\"https://graph.microsoft.com/beta/next\"}"
                    : "{\"value\":[{\"id\":\"" + ScriptB.ToUpperInvariant() + "\",\"displayName\":\"Install TrackVice agent\"}]}";
                return (Dictionary<string, object>)ImeJson.Parse(json);
            }
        }

        static void Names()
        {
            PagedGraph g = new PagedGraph();
            Dictionary<string, string> names = IntuneNames.Scripts(g);
            T.Eq(2, g.Calls, "script names are read page by page");
            T.Eq(2, names.Count, "a script without a name is left out");

            // A device session with two scripts, named by Intune, then replaced by a new upload from the device.
            Engine dev = Engine.ForDemo("autopilot");
            List<Obs> o = new List<Obs>();
            ImeLogParser ime = new ImeLogParser("IntuneManagementExtension");
            ime.Read(L("[PowerShell] Processing policy with id = " + ScriptA + " for user " + Zero, 1), o);
            ime.Read(L("[PowerShell] Processing policy with id = " + ScriptB + " for user " + Zero, 2), o);
            Batch b = new Batch();
            b.Obs.AddRange(o);
            dev.Apply(b);
            T.Eq("Platform script " + ScriptA.Substring(0, 8), dev.S.Find(MdmWatch.ScriptKey(ScriptA)).Name, "a script is named by its ID until Intune is asked");
            T.Eq(2, dev.AddNames(names), "both scripts get their name");
            T.Eq("Set time zone", dev.S.Find(MdmWatch.ScriptKey(ScriptA)).Name, "the name from Intune");
            T.Eq("Install TrackVice agent", dev.S.Find(MdmWatch.ScriptKey(ScriptB)).Name, "IDs match whatever their case");
            T.Eq(0, dev.AddNames(names), "names are set once");

            Engine fresh = Engine.ForDemo("autopilot");
            fresh.Apply(b);
            Batch upload = new Batch();
            upload.Replace = Session.FromJson(fresh.S.ToJson());
            dev.Apply(upload);
            T.Eq("Set time zone", dev.S.Find(MdmWatch.ScriptKey(ScriptA)).Name, "the names survive a new upload from the device");

            EventWatch w = new EventWatch();
            T.Check(w.FromEnrollment(new List<object> { "p", "a", "ANY" }), "without known enrollments every policy counts");
            w.Enrollments.Add("265C9166-0000-0000-0000-3DFF5AA66768");
            T.Check(w.FromEnrollment(new List<object> { "p", "a", "265c9166-0000-0000-0000-3dff5aa66768", "device", "1" }), "a policy from the device's MDM enrollment");
            T.Check(!w.FromEnrollment(new List<object> { "p", "a", "fc01e91f-0000-0000-0000-0b2e5fbedf64", "device", "1" }), "a policy from the Windows image is left out");
            MemoryRegistry r = new MemoryRegistry();
            r.Sz(EventWatch.EnrollmentsKey + @"\265C9166-0000-0000-0000-3DFF5AA66768", "ProviderID", "MS DM Server");
            r.Dword(EventWatch.EnrollmentsKey + @"\18DCFFD4-0000-0000-0000-4266FDBB8E49", "EnrollmentType", 1);
            T.Eq(1, EventWatch.ReadEnrollments(r).Count, "only enrollments with a management server count");
        }

        static void RegExport()
        {
            MemoryRegistry r = new MemoryRegistry();
            RegFile.Parse("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Test]\r\n@=\"default \\\"quoted\\\"\"\r\n\"Number\"=dword:00000046\r\n\"Path\"=\"C:\\\\Temp\\\\a.txt\"\r\n"
                + "\"Time\"=hex(b):00,80,3e,d5,de,b1,9d,01\r\n\"Multi\"=hex(7):61,00,00,00,62,00,\\\r\n  00,00,00,00\r\n\"Long\"=\"line one\r\nline two\"\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Test\\Empty]\r\n", r);
            T.Eq("70", r.Get(@"HKLM\SOFTWARE\Test", "Number").Data, "reg export: dword is decimal");
            T.Eq(@"C:\Temp\a.txt", r.Get(@"HKLM\SOFTWARE\Test", "Path").Data, "reg export: escaped string");
            T.Eq("default \"quoted\"", r.Get(@"HKLM\SOFTWARE\Test", "").Data, "reg export: default value");
            T.Eq("116444736000000000", r.Get(@"HKLM\SOFTWARE\Test", "Time").Data, "reg export: qword");
            T.Eq("a; b", r.Get(@"HKLM\SOFTWARE\Test", "Multi").Data, "reg export: multi-string over two lines");
            T.Eq("line one\nline two", r.Get(@"HKLM\SOFTWARE\Test", "Long").Data, "reg export: string over two lines");
            T.Eq(1, r.SubKeys(@"HKLM\SOFTWARE\Test").Length, "reg export: a key without values exists");
        }

        static void AppLog()
        {
            Session s = Feed(new ImeLogParser("AppWorkload"),
                "[Win32App][EspAppLockInCalculator] Processing subgraph with app ids: " + App,
                "[Win32App][SimulatedExecutionActionHandler] Handler invoked with execution type: Install for policy with id: " + App + " and version: 1.",
                "[Win32App][ReportingManager] Not sending status update for user with id: " + Zero + " and app: " + App + " because there is not enough data to construct a status report.",
                "[Win32App][ReportingManager] Detection state for app with id: " + App + " has been updated. Report delta: {\"DetectionState\":{\"OldValue\":null,\"NewValue\":\"NotInstalled\"}}",
                "[Win32App][ReportingManager] Download state for app with id: " + App + " has been updated. Report delta: {\"EnforcementState\":{\"OldValue\":null,\"NewValue\":\"InProgress\"}}",
                "[Win32App][RealTimeStatusManager] Not sending status update for user with id: " + Zero + " and app: " + App + " because there is currently no subscriber for status updates.",
                "[Win32App][ReportingManager] Download state for app with id: " + App + " has been updated. Report delta: {\"EnforcementState\":{\"OldValue\":\"InProgress\",\"NewValue\":\"InProgressDownloadCompleted\"}}",
                "[Win32App][ExecutionActionHandler] Handler invoked with execution type: Install for policy with id: " + App + " and version: 1.",
                "[Win32App] ===Step=== InstallBehavior RegularWin32App, Intent 3, UninstallCommandLine cmd.exe /c del x.tag",
                "powershell.exe -noprofile -executionpolicy bypass -file .\\Install.ps1",
                "[Win32App] Launch Win32AppInstaller in machine session");
            T.Check(s.Find(IntuneKeys.App(Zero)) == null, "the user ID of a device session is not an app");
            Session push = Feed(new ImeLogParser("AppWorkload"),
                "[Win32App] PushNotification workload thread start with id: 98c0bcc8-d800-4dd9-976a-c48f62c4f749",
                "[Win32Apps] App policy with id: " + Portal + " will not be evaluated as it has user install context and this is a userless check-in. The app will be reported as not applicable.");
            T.Eq(1, push.Items.Count, "the ID of a workload thread is not an app");
            T.Check(push.Items[0].State == State.NotApplicable, "a user app in the device phase is not applicable, not unknown");
            T.Eq(1, s.Items.Count, "one app from the real line formats");
            Item i = s.Find(IntuneKeys.App(App));
            T.Check(i != null && i.State == State.Installing, "download completed means installing");
            T.Eq(@"powershell.exe -noprofile -executionpolicy bypass -file .\Install.ps1", i != null ? i.Get("Command line") : null, "the install command from the line after the step");
            bool noise = false, simulated = false;
            if (i != null) foreach (TEvent e in i.Events) { if (e.Text.Contains("Not sending")) noise = true; if (e.TimeUtc.Second == 2) simulated = true; }
            T.Check(!noise, "reporting bookkeeping is not an event");
            T.Check(!simulated, "the simulated pass is not an install");

            Session p = Feed(new ImeLogParser("AppWorkload"),
                "[Win32App][ReportingManager] Applicability state for app with id: " + Portal + " has been updated. Report delta: {\"ApplicabilityState\":{\"OldValue\":null,\"NewValue\":\"AppUnsupportedDueToUnknownReason\"}}");
            Item pi = p.Find(IntuneKeys.App(Portal));
            T.Check(pi != null && pi.State == State.NotApplicable, "an app that does not apply in the device phase");
        }

        static void Scripts()
        {
            List<Obs> o = new List<Obs>();
            ImeLogParser ime = new ImeLogParser("IntuneManagementExtension"), exec = new ImeLogParser("AgentExecutor");
            string file = @"C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\" + Zero + "_";
            ime.Read(L("[PowerShell] Processing policy with id = " + ScriptA + " for user " + Zero, 1), o);
            exec.Read(L("Adding argument powershell with value " + file + ScriptA + ".ps1 to the named argument list.", 2), o);
            exec.Read(L(@"PowerShell path is C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", 3), o);
            exec.Read(L("Powershell exit code is 8", 4), o);
            exec.Read(L("error from script =" + file + ScriptA + ".ps1 : \n[SETUP_FAILED]\n    + CategoryInfo : NotInstalled", 5), o);
            exec.Read(L("write output done. output = Transcript started\n, error = something", 6), o);
            exec.Read(L("Agent executor completed.", 7), o);
            ime.Read(L("[PowerShell] User Id = " + Zero + ", Policy id = " + ScriptA + ", policy result = Failed", 8), o);
            ime.Read(L("[PowerShell] Processing policy with id = " + ScriptB + " for user " + Zero, 9), o);
            exec.Read(L("Adding argument powershell with value " + file + ScriptB + ".ps1 to the named argument list.", 10), o);
            exec.Read(L(@"PowerShell path is C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe", 11), o);
            exec.Read(L("Powershell exit code is 0", 12), o);
            exec.Read(L("error from script =\n", 13), o);
            exec.Read(L("write output done. output = \n, error = \n", 14), o);
            ime.Read(L("[PowerShell] User Id = " + Zero + ", Policy id = " + ScriptB + ", policy result = Success", 15), o);
            Session s = Build(o);
            T.Eq(2, s.Items.Count, "two platform scripts");
            Item a = s.Find(MdmWatch.ScriptKey(ScriptA)), b = s.Find(MdmWatch.ScriptKey(ScriptB));
            T.Check(a != null && a.State == State.Failed, "the failed script is failed");
            T.Check(b != null && b.State == State.Succeeded, "the other script succeeded");
            T.Eq(Kinds.Script, a != null ? a.Kind : null, "a script is a script");
            T.Eq(Phases.Scripts, a != null ? a.PhaseKey : null, "scripts have their own phase");
            T.Eq("8", a != null ? a.Get("PowerShell exit code") : null, "the script's exit code");
            T.Eq("32-bit", b != null ? b.Get("PowerShell host") : null, "the PowerShell host");
            bool error = false, output = false;
            if (a != null) foreach (TEvent e in a.Events) { if (e.Text.StartsWith("Script error:") && e.Text.Contains("[SETUP_FAILED]")) error = true; if (e.Text == "Script output: Transcript started") output = true; }
            T.Check(error, "the script's error text is an event");
            T.Check(output, "the script's output is an event, without the error part");
            T.Eq(3, b != null ? b.Events.Count : 0, "an empty output and error add no events");
        }

        static void Registry()
        {
            MemoryRegistry r = new MemoryRegistry();
            r.Clock = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            string d = MdmWatch.Expected;
            r.Dword(d + @"\ExpectedPolicies\2026-11-13T11:54:27.373Z", "./Device/Vendor/MSFT/Policy/Config/Update/DeferQualityUpdatesPeriodInDays", 1);
            r.Dword(d + @"\ExpectedPolicies\2026-11-13T11:54:27.373Z", "./Vendor/MSFT/DMClient/Provider/MS%20DM%20Server/EntDMID", 1);
            r.Dword(d + @"\ExpectedPolicies", "LastCountOfCompletedUris", 2);
            r.Dword(d + @"\ExpectedNetworkProfiles\2026-11-13T11:54:27.389Z", "./Device/Vendor/MSFT/WiFi/Profile/Contoso", 1);
            r.Dword(d + @"\ExpectedMSIAppPackages\2026-11-13T11:57:23.322Z", "./Vendor/MSFT/Office/Installation/" + Office + "/FinalStatus", 0);
            r.Dword(d + @"\ExpectedMSIAppPackages\2026-11-13T11:57:58.521Z", "./Vendor/MSFT/Office/Installation/" + Office + "/FinalStatus", 1);
            r.Dword(d + @"\Sidecar\2026-11-13T11:57:23.330Z", "./Device/Vendor/MSFT/EnrollmentStatusTracking/Setup/Apps/Tracking/Sidecar/Win32App_" + App + "_1", 2);
            r.Sz(MdmWatch.Office + "\\" + Office, "", "<Configuration><Add Channel=\"Deferred\" OfficeClientEdition=\"64\"><Product ID=\"O365ProPlusRetail\"><Language ID=\"da-dk\" /></Product><Product ID=\"VisioProRetail\" /></Add></Configuration>");
            r.Dword(MdmWatch.Office + "\\" + Office, "Status", 0);
            r.Dword(MdmWatch.Office + "\\" + Office, "FinalStatus", 70);
            string msi = MdmWatch.Msi + @"\S-0-0-00-0000000000-0000000000-000000000-000\MSI\";
            foreach (string k in new string[] { "{{" + Msi + "}}", "{" + Msi + "}" })
            {
                r.Dword(msi + k, "Status", 70);
                r.Dword(msi + k, "LastError", 0);
                r.Sz(msi + k, "CurrentDownloadUrl", "https://example.invalid/IntuneWindowsAgent.msi");
                r.Sz(msi + k, "ProductVersion", "1.108.101.0");
            }
            string other = "17a2de58-6391-4fb4-80c2-8153740fd068";
            r.Dword(msi + "{" + other + "}", "Status", 60);
            r.Dword(msi + "{" + other + "}", "LastError", 1603);
            r.Sz(MdmWatch.Uninstall + "\\{" + other + "}", "DisplayName", "Contoso Agent");
            r.Sz(MdmWatch.Scripts + "\\" + Zero + "\\" + ScriptA, "Result", "Failed");
            r.Dword(MdmWatch.Scripts + "\\" + Zero + "\\" + ScriptA, "ErrorCode", 99);
            r.Sz(MdmWatch.Scripts + "\\" + Zero + "\\" + ScriptA, "RunAsAccount", "System");
            r.Sz(MdmWatch.Scripts + "\\" + Zero + "\\" + ScriptB, "Result", "Success");

            MdmWatch w = new MdmWatch();
            DateTime now = new DateTime(2026, 11, 13, 12, 30, 0, DateTimeKind.Utc);
            List<Obs> first = w.Read(r, now);
            Session s = Build(first);
            T.Eq(0, w.Read(r, now).Count, "an unchanged registry adds nothing");

            Item pol = s.Find("esp:expected:./device/vendor/msft/policy/config/update/deferqualityupdatesperiodindays");
            T.Check(pol != null && pol.State == State.Succeeded && pol.PhaseKey == Phases.DeviceSetup, "a policy the ESP waits for, in device setup");
            T.Eq("Update: DeferQualityUpdatesPeriodInDays", pol != null ? pol.Name : null, "the policy is named by its area and setting");
            T.Eq("DMClient / Provider / MS DM Server / EntDMID", MdmWatch.UriName("./Vendor/MSFT/DMClient/Provider/MS%20DM%20Server/EntDMID"), "other settings are named by their path");
            Item wifi = s.Find("esp:expected:./device/vendor/msft/wifi/profile/contoso");
            T.Check(wifi != null && wifi.Name == "Wi-Fi: Contoso" && wifi.Kind == Kinds.Profile, "the Wi-Fi profile the ESP waits for");

            Item off = s.Find("office:" + Office);
            T.Check(off != null && off.State == State.Succeeded, "Microsoft 365 Apps installed");
            T.Eq("Microsoft 365 Apps for enterprise + Visio", off != null ? off.Name : null, "Office is named by its products");
            T.Eq(Phases.DeviceSetup, off != null ? off.PhaseKey : null, "Office is in device setup because the ESP waits for it");
            T.Eq(new DateTime(2026, 11, 13, 11, 57, 23, 322, DateTimeKind.Utc), off != null ? off.FirstUtc : DateTime.MinValue, "the ESP's own time is when it started waiting");
            T.Eq("64-bit", off != null ? off.Get("Architecture") : null, "Office architecture");
            bool waited = false;
            if (off != null) foreach (TEvent e in off.Events) if (e.Text.Contains("waiting for")) waited = true;
            T.Check(waited, "the wait and the arrival are both events");

            Item ime = s.Find("msi:" + Msi);
            T.Check(ime != null && ime.State == State.Succeeded && ime.Events.Count == 1, "an MSI saved under two key names is one item with one event");
            T.Eq("Intune Management Extension (IntuneWindowsAgent.msi)", ime != null ? ime.Name : null, "the agent's own MSI is named");
            Item bad = s.Find("msi:" + other);
            T.Check(bad != null && bad.State == State.Failed && bad.Name == "Contoso Agent", "a failed MSI app with its name from the uninstall key");
            string label;
            T.Eq(State.Downloading, MdmWatch.MsiState(20, out label), "MSI status 20 is downloading");
            T.Eq(State.Installing, MdmWatch.MsiState(50, out label), "MSI status 50 is installing");

            Item sa = s.Find(MdmWatch.ScriptKey(ScriptA)), sb = s.Find(MdmWatch.ScriptKey(ScriptB));
            T.Check(sa != null && sa.State == State.Failed && sb != null && sb.State == State.Succeeded, "script results from the registry");
            T.Eq("System", sa != null ? sa.Get("Runs as") : null, "the account a script runs as");
            DateTime n2 = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            T.Eq((DateTime?)new DateTime(2026, 11, 13, 11, 57, 9, DateTimeKind.Utc), MdmWatch.LocalStamp("13/11/2026 11.57.09", n2), "a Danish time stamp");
            T.Eq((DateTime?)new DateTime(2026, 11, 13, 11, 57, 9, DateTimeKind.Utc), MdmWatch.LocalStamp("11/13/2026 11:57:09 AM", n2), "a US time stamp");
            T.Eq((DateTime?)new DateTime(2026, 3, 4, 23, 5, 0, DateTimeKind.Utc), MdmWatch.LocalStamp("04-03-2026 23:05:00", n2), "day first when both readings are possible");
            T.Eq((DateTime?)new DateTime(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc), MdmWatch.LocalStamp("11/12/2026 1:00:00 PM", n2), "month first when day first would be in the future");
            T.Check(MdmWatch.LocalStamp("soon", n2) == null, "something that is not a time");
            T.Check(s.Find("esp:expected:./device/vendor/msft/enrollmentstatustracking/setup/apps/tracking/sidecar/win32app_" + App + "_1") == null, "Win32 apps are not listed twice");

            // The Office install fails later: only the change is reported.
            r.Dword(MdmWatch.Office + "\\" + Office, "FinalStatus", 60);
            r.Dword(MdmWatch.Office + "\\" + Office, "Status", 17002);
            List<Obs> next = w.Read(r, now);
            T.Check(next.Count == 1 && next[0].State == State.Failed && next[0].ExitCode == 17002, "a failed Office install carries its status");
        }

        static void Events()
        {
            DateTime t = new DateTime(2026, 11, 13, 11, 51, 35, DateTimeKind.Utc);
            Obs a = MdmEvents.Map(814, new List<object> { "ConfigDeviceHealthMonitoringScope", "DeviceHealthMonitoring", "265C9166-0000-0000-0000-3DFF5AA66768", "Device", "BootPerformance", "0x00000006", "0x00000000" }, t, "MDM/Admin", 12, null);
            T.Check(a != null && a.ItemKey == "mdm:area:devicehealthmonitoring" && a.ItemName == "DeviceHealthMonitoring", "a string policy goes under its area");
            T.Eq("ConfigDeviceHealthMonitoringScope = BootPerformance", a != null ? a.Text : null, "the policy and its value");
            Obs b = MdmEvents.Map(813, new List<object> { "DeferQualityUpdatesPeriodInDays", "Update", "265C9166-0000-0000-0000-3DFF5AA66768", "device", "0x00000004", "0x00000001", "0x00000000" }, t, "MDM/Admin", 13, null);
            T.Eq("DeferQualityUpdatesPeriodInDays = 4", b != null ? b.Text : null, "an integer policy shows its value in decimal");
            Obs c = MdmEvents.Map(813, new List<object> { "DeferQualityUpdatesPeriodInDays", "Update", "x", "device", (uint)7, (uint)1, (uint)0 }, t, "MDM/Admin", 14, null);
            T.Eq("DeferQualityUpdatesPeriodInDays = 7", c != null ? c.Text : null, "an integer policy as the event log gives it");
            T.Check(MdmEvents.Map(813, new List<object> { "Power/Policy/Settings/Processor/CPMinCores/DcValue", "knobs", "x", "device", "0x4", "0x1", "0x0" }, t, "MDM/Admin", 15, null) == null, "defaults of the Windows image are left out");
            T.Check(MdmEvents.Map(404, new List<object> { "a", "b", "c", "d", "e" }, t, "MDM/Admin", 16, null) == null, "other events are not policies");
            T.Check(MdmEvents.Map(814, new List<object> { "a" }, t, "MDM/Admin", 17, null) == null, "a short event is skipped");
            Session s = Build(new List<Obs> { a, b, c });
            T.Eq(2, s.Items.Count, "one item per policy area");
            Item up = s.Find("mdm:area:update");
            T.Check(up != null && up.Events.Count == 2 && up.State == State.Succeeded && up.PhaseKey == Phases.Policies, "every setting is an event under its area");
        }
    }
}
