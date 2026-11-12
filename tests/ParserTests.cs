namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class ParserTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        static int n;

        static LogLine L(string text)
        {
            LogLine l = new LogLine();
            l.Text = text;
            l.Utc = T0.AddSeconds(++n);
            l.File = "x.log";
            l.LineNo = n;
            l.Thread = "7";
            return l;
        }

        static Session Feed(LogParser p, params string[] lines)
        {
            List<Obs> o = new List<Obs>();
            foreach (string s in lines) p.Read(L(s), o);
            Session ses = new Session();
            Tracker t = new Tracker(ses);
            t.ApplyAll(o);
            return ses;
        }

        public static void Run()
        {
            Smsts();
            AppEnforce();
            Others();
            Ime();
            Esp();
            ErrorCodes();
        }

        static void Smsts()
        {
            SmstsParser p = new SmstsParser();
            Session s = Feed(p,
                "Set a global environment variable _SMSTSPackageName=Win 11",
                "Set a global environment variable _SMSTSInWinPE=TRUE",
                "Start executing an instruction. Instruction name: Apply Operating System. Instruction pointer: 4",
                "Executing command line: osdapplyos.exe /image:P01,1",
                "Process completed with exit code 0",
                "Successfully completed the action (Apply Operating System) with the exit win32 code 0",
                "Set a global environment variable _SMSTSInWinPE=FALSE",
                "Start executing an instruction. Instruction name: Install Apps. Instruction pointer: 9",
                "Failed to run the action: Install Apps. Error 0x87D00324 (Error: 87D00324; Source: CCM)",
                "The action (Install fr-FR) has been skipped because the condition is evaluated to be false",
                "Task sequence execution failed with error code 80004005");
            T.Eq("Win 11", s.GetInfo("Task sequence"), "task sequence name");
            Item os = s.Find("ts:4:Apply Operating System");
            T.Check(os != null && os.State == State.Succeeded, "step done");
            T.Eq(Phases.WinPE, os != null ? os.PhaseKey : null, "step in Windows PE");
            T.Eq("osdapplyos.exe /image:P01,1", os != null ? os.Get("Command line") : null, "command line");
            Item apps = s.Find("ts:9:Install Apps");
            T.Check(apps != null && apps.State == State.Failed, "failed step");
            T.Eq((long?)unchecked((int)0x87D00324), apps != null ? apps.ExitCode : null, "failed step code");
            T.Eq(Phases.FullOs, apps != null ? apps.PhaseKey : null, "step in full Windows");
            Item skip = s.Find("ts:Install fr-FR");
            T.Check(skip != null && skip.State == State.Skipped, "skipped step");
            T.Check((s.GetInfo("Task sequence result") ?? "").StartsWith("Failed"), "task sequence failed");

            // The same line from two copies of smsts.log counts once.
            SmstsParser p2 = new SmstsParser();
            List<Obs> o = new List<Obs>();
            LogLine a = L("Start executing an instruction. Instruction name: Format. Instruction pointer: 1");
            LogLine b = L("x");
            b.Text = a.Text;
            b.Utc = a.Utc;
            b.File = "copy.log";
            p2.Read(a, o);
            p2.Read(b, o);
            T.Eq(1, o.Count, "duplicate smsts line skipped");
        }

        static void AppEnforce()
        {
            string id = "ScopeId_ABC/DeploymentType_123";
            Session s = Feed(new AppEnforceParser(),
                "+++ Starting Install enforcement for App DT \"CRM - MSI\" ApplicationDeliveryType - " + id + ", Revision - 4, ContentPath - C:\\Windows\\ccmcache\\4, Execution Context - System",
                "+++ Application not discovered. [AppDT Id: " + id + ", Revision: 4]",
                "    Executing Command line: \"C:\\WINDOWS\\system32\\msiexec.exe\" /i crm.msi /qn with system context",
                "    Process 4116 terminated with exitcode: 0",
                "    Matched exit code 0 to a Success entry in exit codes table.",
                "+++ Application not discovered. [AppDT Id: " + id + ", Revision: 4]",
                "++++++ App enforcement completed (126 seconds) for App DT \"CRM - MSI\" [" + id + "], Revision: 4, User SID: ] ++++++");
            Item i = s.Find(SccmKeys.App(id));
            T.Check(i != null, "application item");
            T.Check(i != null && i.State == State.Failed, "not detected after install fails");
            T.Check(i != null && Errors.IsNotDetected(i.ExitCode ?? 0), "not detected code");
            T.Eq("0", i != null ? i.Get("Installer exit code") : null, "installer exit code kept");
            T.Eq("\"C:\\WINDOWS\\system32\\msiexec.exe\" /i crm.msi /qn", i != null ? i.Get("Command line") : null, "AppEnforce command line");
            Finder.Run(s, T0.AddHours(1), false);
            T.Check(s.Findings.Count > 0 && s.Findings[0].Kind == FindingKinds.NotDetected, "not detected finding");
            T.Check(s.Findings.Count > 0 && s.Findings[0].Message.Contains("exit code 0"), "finding names the installer exit code");

            // Detected before install: already installed.
            Session s2 = Feed(new AppEnforceParser(),
                "+++ Starting Install enforcement for App DT \"7-Zip\" ApplicationDeliveryType - ScopeId_A/DeploymentType_9, Revision - 1, ContentPath - C:\\x, Execution Context - System",
                "+++ Discovered application [AppDT Id: ScopeId_A/DeploymentType_9, Revision: 1]");
            Item z = s2.Find(SccmKeys.App("ScopeId_A/DeploymentType_9"));
            T.Check(z != null && z.State == State.Succeeded, "already installed counts as done");

            // A soft reboot exit code means a restart is needed.
            Session s3 = Feed(new AppEnforceParser(),
                "+++ Starting Install enforcement for App DT \"Drv\" ApplicationDeliveryType - ScopeId_A/DeploymentType_8, Revision - 1, ContentPath - C:\\x, Execution Context - System",
                "    Matched exit code 3010 to a SoftReboot entry in exit codes table.",
                "+++ Discovered application [AppDT Id: ScopeId_A/DeploymentType_8, Revision: 1]");
            Item d = s3.Find(SccmKeys.App("ScopeId_A/DeploymentType_8"));
            T.Check(d != null && d.State == State.WaitingReboot, "3010 waits for a restart");
        }

        static void Others()
        {
            Session s = Feed(new ExecmgrParser(),
                "Execution Request for advert P0120034 package P0100012 program Install state change from NotExist to WaitingContent",
                "Execution Request for advert P0120034 package P0100012 program Install state change from WaitingContent to Running",
                "Execution is complete for program Install. The exit code is 1603, the execution status is FailureNonRetry");
            Item p = s.Find(SccmKeys.Package("P0100012", "Install"));
            T.Check(p != null && p.State == State.Failed && p.ExitCode == 1603, "execmgr failure with exit code");

            Session c = Feed(new CasParser(),
                "Download started for content Content_abc.1 under context System",
                "Download completed for content Content_abc.1 under context System");
            Item ci = c.Find("content:content_abc.1");
            T.Check(ci != null && ci.State == State.Succeeded, "content downloaded");

            Session u = Feed(new UpdatesParser("UpdatesDeployment"),
                "Update (Site_1/SUM_aaaa) Name (2026-11 CU (KB1)) ArticleID (5068861) added to the targeted list of deployment ({x})",
                "Update (Site_1/SUM_aaaa) Progress: Status = ciStateDownloading, PercentComplete = 40, Result = 0x0",
                "Update (Site_1/SUM_aaaa) Progress: Status = ciStateError, PercentComplete = 0, Result = 0x80240022");
            Item ui = u.Find(SccmKeys.Update("SUM_aaaa"));
            T.Eq("2026-11 CU (KB1)", ui != null ? ui.Name : null, "update name with brackets");
            T.Check(ui != null && ui.State == State.Failed && ui.ExitCode.HasValue, "update failure");
            T.Eq("KB5068861", ui != null ? ui.Get("Article") : null, "article");

            Session ie = Feed(new AppIntentEvalParser(),
                "ScopeId_A/DeploymentType_1 :- Current State = Installed, Applicability = Applicable, ResolvedState = Installed, ConfigureState = NotNeeded, Title = Notepad",
                "ScopeId_A/DeploymentType_2 :- Current State = NotInstalled, Applicability = Applicable, ResolvedState = Available, ConfigureState = NotNeeded, Title = Optional");
            T.Eq(1, ie.Items.Count, "available apps that are not installed are left out");

            T.Eq((State?)State.Installing, EvalStates.Map(7), "WMI installing");
            T.Eq((State?)State.Failed, EvalStates.Map(13), "WMI error");
            T.Eq((State?)State.WaitingReboot, EvalStates.Map(8), "WMI soft reboot");
        }

        static void Ime()
        {
            string id = "a1c47e92-0d3b-4f5e-8a6c-2b9d1e4f7a02";
            Session s = Feed(new ImeLogParser("AppWorkload"),
                "[Win32App][DownloadActionHandler] Starting content download for app with id: " + id + ".",
                "[Win32App DO] DO job progress for app id: " + id + ", downloaded 48 %",
                "[Win32App][Win32AppExecutionExecutor] Launch Win32AppInstaller in machine session for app with id: " + id + ". Command line: msiexec /i a.msi /qn",
                "[Win32App][Win32AppExecutionExecutor] app with id: " + id + " lpExitCode 1618",
                "[Win32App][ReportingManager] Execution state for app with id: " + id + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Error\",\"ErrorCode\":\"null -> 1618\"}",
                "[Win32App][ExecutionActionHandler] Handler invoked for policy with id: " + id + ", starting to execute install command.",
                "[Win32App][Win32AppExecutionExecutor] app with id: " + id + " lpExitCode 0",
                "[Win32App][ReportingManager] Execution state for app with id: " + id + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Success\",\"ErrorCode\":\"1618 -> 0\"}");
            Item i = s.Find(IntuneKeys.App(id));
            T.Check(i != null && i.State == State.Succeeded, "IME app succeeded in the end");
            T.Eq(2, i != null ? i.Attempts.Count : 0, "IME retry is a second attempt");
            T.Eq(1, i != null ? i.FailedAttempts : 0, "one failed attempt");
            T.Eq("msiexec /i a.msi /qn", i != null ? i.Get("Command line") : null, "IME command line");
            bool plain = true;
            if (i != null) foreach (TEvent e in i.Events) if (e.Text.Contains(id)) plain = false;
            T.Check(plain, "IME events read as sentences without the app ID");

            Session nd = Feed(new ImeLogParser("AppWorkload"),
                "[Win32App][Win32AppExecutionExecutor] Admin did NOT set mapping for lpExitCode: 1603 of app: " + id + ".");
            Item f = nd.Find(IntuneKeys.App(id));
            T.Check(f != null && f.State == State.Failed && f.ExitCode == 1603, "unmapped exit code fails");
        }

        static void Esp()
        {
            MemoryRegistry reg = new MemoryRegistry();
            string sid = "S-1-12-1-1-2-3-4";
            reg.Dword(EspWatch.Root + @"\Device\DevicePreparation\SidecarProvisioning", "InstallationState", 2);
            reg.Dword(EspWatch.Root + @"\Device\Setup\Apps\Tracking\Sidecar\Win32App_3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01_1", "InstallationState", 3);
            reg.Dword(EspWatch.Root + @"\" + sid + @"\Apps\Tracking\Sidecar\Win32App_9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f04_1", "InstallationState", 4);
            reg.Dword(EspWatch.Root + @"\ESPTrackingInfo\Diagnostics\Something", "InstallationState", 2);
            EspWatch w = new EspWatch();
            w.Names = delegate (string id) { return id == "3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01" ? "Office" : null; };
            List<Obs> o = w.Read(reg, T0);
            T.Eq(3, o.Count, "three ESP items, diagnostics left out");
            Session s = new Session();
            Tracker t = new Tracker(s);
            t.ApplyAll(o);
            Item office = s.Find(IntuneKeys.App("3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01"));
            T.Eq("Office", office != null ? office.Name : null, "ESP name from the IME names");
            T.Eq(Phases.DeviceSetup, office != null ? office.PhaseKey : null, "device setup phase");
            Item user = s.Find(IntuneKeys.App("9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f04"));
            T.Check(user != null && user.PhaseKey == Phases.AccountSetup && user.State == State.Failed, "account setup app failed");
            T.Check(s.FindPhase(Phases.DevicePreparation) != null, "device preparation phase");
            T.Eq(0, w.Read(reg, T0.AddSeconds(5)).Count, "unchanged ESP state is not repeated");
            reg.Dword(EspWatch.Root + @"\Device\DevicePreparation\SidecarProvisioning", "InstallationState", 3);
            T.Eq(1, w.Read(reg, T0.AddSeconds(10)).Count, "a changed ESP state is reported");

            // An item an IME log put under Intune apps moves to its ESP phase.
            Session s2 = new Session();
            Tracker t2 = new Tracker(s2);
            List<Obs> log = new List<Obs>();
            new ImeLogParser("AppWorkload").Read(L("[Win32App][DownloadActionHandler] Starting content download for app with id: 3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01."), log);
            t2.ApplyAll(log);
            T.Eq(Phases.IntuneApps, s2.Items.Count > 0 ? s2.Items[0].PhaseKey : null, "IME log alone: Intune apps");
            t2.ApplyAll(new EspWatch().Read(reg, T0.AddMinutes(1)));
            Item moved = s2.Find(IntuneKeys.App("3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01"));
            T.Eq(Phases.DeviceSetup, moved != null ? moved.PhaseKey : null, "ESP moves it to device setup");
            T.Check(s2.FindPhase(Phases.IntuneApps) != null && s2.FindPhase(Phases.IntuneApps).Items.Count == 0, "and out of Intune apps");

            // A device that finished its ESP long ago: states carry the time the key was written, not the time it was read.
            MemoryRegistry old = new MemoryRegistry();
            DateTime then = T0.AddDays(-40);
            old.Clock = then;
            old.Dword(EspWatch.Root + @"\Device\Setup\Apps\Tracking\Sidecar\Win32App_3f8a2c1e-5b6d-4a7e-9c21-7d4e5f6a8b01_1", "InstallationState", 3);
            old.Clock = then.AddMinutes(20);
            old.Dword(EspWatch.Root + @"\" + sid + @"\Apps\Tracking\Sidecar\Win32App_9b3e5f71-2a4c-4d8e-b6f0-1c7a3e5d9f04_1", "InstallationState", 2);
            List<Obs> oo = new EspWatch().Read(old, T0);
            T.Check(oo.Count == 2 && oo[0].TimeUtc == then, "ESP state gets the key's write time");
            Session so = new Session();
            new Tracker(so).ApplyAll(oo);
            T.Eq((DateTime?)then, so.FirstUtc, "an old deployment starts when it really started");
            Finder.Run(so, T0, true);
            bool stale = false;
            foreach (Finding f in so.Findings) if (f.Kind == FindingKinds.Stuck && f.Title.Contains("has not changed since")) stale = true;
            T.Check(stale, "an install left over from long ago is called out as stale, not as stuck now");
            T.Check(new EspWatch().Read(new MemoryRegistry(), T0).Count == 0, "no ESP keys, no items");

            T.Eq(State.Installing, EspWatch.InstallationState("2"), "InstallationState 2");
            T.Eq(State.Failed, EspWatch.InstallationState("4"), "InstallationState 4");
            T.Eq(State.Succeeded, EspWatch.InstallationState("Completed"), "InstallationState as text");
        }

        static void ErrorCodes()
        {
            T.Eq((long?)unchecked((int)0x87D00324), Errors.Parse("0x87D00324"), "hex with 0x");
            T.Eq((long?)unchecked((int)0x87D00324), Errors.Parse("87D00324"), "hex without 0x");
            T.Eq((long?)unchecked((int)0x80004005), Errors.Parse("80004005"), "8-digit hex starting with 8");
            T.Eq((long?)1603, Errors.Parse("1603"), "decimal");
            T.Eq((long?)(-2147467259), Errors.Parse("-2147467259"), "negative decimal");
            T.Eq("0x87D00324", Errors.Short(unchecked((int)0x87D00324)), "short hex");
            T.Eq("1603", Errors.Short(1603), "short decimal");
            T.Check(Errors.Describe(1603).Contains("fatal error"), "1603 meaning");
            T.Check(Errors.Describe(unchecked((int)0x87D00607)).Contains("distribution point"), "content not found meaning");
            T.Check(Errors.Hint(1603) != null, "1603 hint");
        }
    }
}
