namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The Apps phase and its groups, the ESP step Apps, ESP steps Windows never closed, log separator lines,
    // and platform scripts whose output has warnings. All IDs and names are made up.
    public static class AppGroupTests
    {
        const string Zero = "00000000-0000-0000-0000-000000000000";
        const string UserId = "7c1d2e3f-4a5b-4c6d-8e9f-0a1b2c3d4e5f";
        const string Vpn = "a1c47e92-0d3b-4f5e-8a6c-2b9d1e4f7a02", Expenses = "5a9d2c7e-1b4f-4c8a-8d3e-7f0b2c6e9a10";
        const string Viewer = "b3f7e1a9-6c2d-4a5b-9f8e-1d3c5a7b9e11", Runtime = "8c1e4a7f-3d9b-4f2a-9e6c-5b7d0a2c4e09", Old = "d1a5c9e3-4f7b-4e2a-8c6d-0b9e3f5a7c12";
        const string Script = "4e8a1c3d-5b7f-4a9e-8c2d-1f3b5d7e9a01";
        static readonly DateTime T0 = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

        static LogLine L(string text, double sec, int type)
        {
            LogLine l = new LogLine();
            l.Text = text;
            l.Raw = text;
            l.Utc = T0.AddSeconds(sec);
            l.File = "AppWorkload.log";
            l.LineNo = (int)sec;
            l.Thread = "7";
            l.Severity = type >= 3 ? 2 : type == 2 ? 1 : 0;
            return l;
        }

        static string Policy(string id, string name, int intent, int target)
        {
            return "{\"Id\":\"" + id + "\",\"Name\":\"" + name + "\",\"Version\":1,\"Intent\":" + intent + ",\"TargetType\":" + target + ",\"DetectionRule\":null}";
        }

        public static void Run()
        {
            Groups();
            Separators();
            Scripts();
            Steps();
            Saved();
        }

        static void Groups()
        {
            T.Eq(AppGroups.Dependency, AppGroups.AssignmentOf("Not targeted (dependency)"), "intent 0 as the IME registry names it is a dependency");
            T.Eq(AppGroups.Available, AppGroups.AssignmentOf("1"), "intent 1 is available");
            T.Eq(AppGroups.Uninstall, AppGroups.AssignmentOf("Uninstall"), "intent 4 is uninstall");
            T.Eq(AppGroups.Device, AppGroups.TargetOfUser("System"), "the System user key is the device");
            T.Eq(AppGroups.Device, AppGroups.TargetOfUser(Zero), "the all-zero user is the device");
            T.Eq(AppGroups.User, AppGroups.TargetOfUser(UserId), "another user ID is a user");

            ImeLogParser p = new ImeLogParser("AppWorkload");
            List<Obs> o = new List<Obs>();
            p.Read(L("[Win32App] Get policies = [" + Policy(Vpn, "Contoso VPN Client", 3, 2) + "," + Policy(Expenses, "Contoso Expenses", 3, 1) + ","
                + Policy(Viewer, "Contoso Viewer", 1, 1) + "," + Policy(Runtime, "Contoso Runtime", 0, 2) + "," + Policy(Old, "Contoso Old Tool", 4, 2) + "]", 1, 1), o);
            foreach (string id in new string[] { Vpn, Expenses, Runtime, Old })
            {
                p.Read(L("[Win32App][DownloadActionHandler] Starting content download for app with id: " + id + ".", 10, 1), o);
                p.Read(L("[Win32App][ReportingManager] Execution state for app with id: " + id + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Success\",\"ErrorCode\":\"null -> 0\"}", 20, 1), o);
            }
            Session s = new Session();
            new Tracker(s).ApplyAll(o);
            Item vpn = s.Find(IntuneKeys.App(Vpn)), exp = s.Find(IntuneKeys.App(Expenses)), viewer = s.Find(IntuneKeys.App(Viewer));
            Item rt = s.Find(IntuneKeys.App(Runtime)), old = s.Find(IntuneKeys.App(Old));
            T.Check(viewer != null && viewer.Name == "Contoso Viewer", "an app offered in Company Portal is shown from the app policies");
            T.Eq(State.Skipped, viewer != null ? viewer.State : State.Unknown, "and waits for nobody while it is not installed");
            T.Eq("Available", viewer != null ? AppGroups.StateLabel(viewer) : null, "its state reads Available");
            T.Eq(AppGroups.DeviceAfter, AppGroups.Group(vpn), "a required device app the ESP does not wait for");
            T.Eq(AppGroups.UserAfter, AppGroups.Group(exp), "a required user app the ESP does not wait for");
            T.Eq(AppGroups.InCompanyPortal, AppGroups.Group(viewer), "an available app");
            T.Eq(AppGroups.Dependencies, AppGroups.Group(rt), "a dependency");
            T.Eq(AppGroups.Uninstalls, AppGroups.Group(old), "an uninstall");
            T.Eq("Win32 app · User · Available", viewer != null ? AppGroups.KindText(viewer) : null, "the tags after the kind");
            T.Eq(IntuneKeys.App(Vpn), vpn != null ? vpn.Key : null, "item keys stay as they were");
            Phase apps = s.FindPhase(Phases.IntuneApps);
            T.Eq("Apps", apps != null ? apps.Name : null, "the phase is called Apps");
            T.Eq(State.Succeeded, apps != null ? apps.State : State.Unknown, "an available app does not keep the phase waiting");
            T.Eq(0, s.Count(State.Pending) + s.Count(State.Unknown), "and is not counted as waiting");
            T.Check(ShipCheck.Judge(s, null).Ready, "and does not make the device not ready");

            // The user line names who the app is for when no policy did.
            ImeLogParser q = new ImeLogParser("AppWorkload");
            List<Obs> o2 = new List<Obs>();
            q.Read(L("[Win32App][DownloadActionHandler] Starting content download for app with id: " + Expenses + ".", 1, 1), o2);
            q.Read(L("[Win32App][ReportingManager] Not sending status update for user with id: " + UserId + " and app: " + Expenses + " because there is currently no subscription.", 2, 1), o2);
            Session s2 = new Session();
            new Tracker(s2).ApplyAll(o2);
            T.Eq(AppGroups.User, AppGroups.Target(s2.Find(IntuneKeys.App(Expenses))), "the IME log line for a user and an app sets the target");

            // The ESP step Apps says what it waits for, from the apps in its phase.
            Session e = new Session();
            Tracker t = new Tracker(e);
            List<Obs> eo = new List<Obs>();
            eo.Add(Esp("esp:step:device:apps", "Apps", Kinds.Esp, State.Installing, 0));
            string[] names = { "Contoso Agent", "Contoso Tool", "Contoso Viewer", "Contoso VPN Client", "Contoso Zip" };
            State[] states = { State.Succeeded, State.Succeeded, State.Succeeded, State.Installing, State.Pending };
            for (int k = 0; k < names.Length; k++) eo.Add(Esp("intune:app" + k, names[k], Kinds.Win32App, states[k], k + 1));
            t.ApplyAll(eo);
            Phase dev = e.FindPhase(Phases.DeviceSetup);
            T.Eq("3 of 5 · waiting for Contoso VPN Client and 1 more", AppGroups.StepText(dev, e.Find("esp:step:device:apps")), "the ESP step Apps names the app it waits for");
        }

        static Obs Esp(string key, string name, string kind, State st, int sec)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddSeconds(sec);
            o.Source = "ESP";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = kind;
            o.Origin = Origins.Intune;
            o.PhaseKey = Phases.DeviceSetup;
            o.State = st;
            o.Text = "ESP: " + States.Label(st);
            return o;
        }

        static void Separators()
        {
            T.Check(AppGroups.IsSeparator("[Win32App] --------------------- Unlock DoWorkInternal ---------------------"), "a dashed separator line");
            T.Check(AppGroups.IsSeparator("===================== application poller starts. ====================="), "an equals separator line");
            T.Check(!AppGroups.IsSeparator("[Win32App][ReportingManager] Execution state for app has been updated"), "an ordinary line is no separator");
            ImeLogParser p = new ImeLogParser("AppWorkload");
            List<Obs> o = new List<Obs>();
            p.Read(L("[Win32App][DownloadActionHandler] Starting content download for app with id: " + Vpn + ".", 1, 1), o);
            p.Read(L("[Win32App] --------------------- Unlock DoWorkInternal ---------------------", 2, 2), o);
            p.Read(L("[Win32App] Lock DoWorkInternal", 3, 2), o);
            bool bad = false;
            foreach (Obs x in o) if (x.Text != null && x.Text.IndexOf("DoWorkInternal", StringComparison.OrdinalIgnoreCase) >= 0) bad = true;
            T.Check(!bad, "a separator line does not become the text of an app");
        }

        static void Scripts()
        {
            string file = @"C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\";
            ImeLogParser ime = new ImeLogParser("IntuneManagementExtension"), exec = new ImeLogParser("AgentExecutor");
            List<Obs> o = new List<Obs>();
            // The first run for the device: warnings in the output and in the errors, exit code 0, no result line yet.
            ime.Read(L("[PowerShell] Processing policy with id = " + Script + " for user " + Zero, 1, 1), o);
            exec.Read(L("Adding argument powershell with value " + file + Zero + "_" + Script + ".ps1 to the named argument list.", 2, 1), o);
            exec.Read(L(@"PowerShell path is C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", 3, 1), o);
            exec.Read(L("Powershell exit code is 0", 4, 1), o);
            exec.Read(L("error from script =WARNING: The module Contoso.Tools is out of date.\n", 5, 1), o);
            exec.Read(L("write output done. output = WARNING: Contoso settings take effect after the next sign-in.\n\n, error = \n", 6, 1), o);
            exec.Read(L("Agent executor completed.", 7, 1), o);
            Session s = new Session();
            Tracker t = new Tracker(s);
            t.ApplyAll(o);
            Item i = s.Find(MdmWatch.ScriptKey(Script));
            T.Eq(State.Succeeded, i != null ? i.State : State.Unknown, "a script with warnings and exit code 0 ends done");
            bool error = false;
            if (i != null) foreach (TEvent e in i.Events) if (e.Severity >= 2) error = true;
            T.Check(!error, "and its warnings are not errors");
            // A second run for the user, which the IME result line does not close: the exit code does.
            o.Clear();
            ime.Read(L("[PowerShell] Processing policy with id = " + Script + " for user " + UserId, 60, 1), o);
            exec.Read(L("Adding argument powershell with value " + file + UserId + "_" + Script + ".ps1 to the named argument list.", 61, 1), o);
            exec.Read(L(@"PowerShell path is C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", 62, 1), o);
            exec.Read(L("Powershell exit code is 0", 63, 1), o);
            exec.Read(L("write output done. output = WARNING: Contoso settings take effect after the next sign-in.\n, error = \n", 64, 1), o);
            exec.Read(L("Agent executor completed.", 65, 1), o);
            t.ApplyAll(o);
            T.Eq(2, i != null ? i.Attempts.Count : 0, "the second run is a second attempt");
            T.Eq(State.Succeeded, i != null ? i.State : State.Unknown, "and it closes as well");
            // A third run that exits with 1 fails at once, and an error that is not a warning fails a run with exit code 0.
            o.Clear();
            exec.Read(L("Adding argument powershell with value " + file + UserId + "_" + Script + ".ps1 to the named argument list.", 120, 1), o);
            exec.Read(L(@"PowerShell path is C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe", 121, 1), o);
            exec.Read(L("Powershell exit code is 1", 122, 1), o);
            exec.Read(L("Agent executor completed.", 123, 1), o);
            t.ApplyAll(o);
            T.Eq(State.Failed, i != null ? i.State : State.Unknown, "a run that exits with 1 fails");
            o.Clear();
            exec.Read(L("Adding argument powershell with value " + file + UserId + "_" + Script + ".ps1 to the named argument list.", 180, 1), o);
            exec.Read(L(@"PowerShell path is C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", 181, 1), o);
            exec.Read(L("Powershell exit code is 0", 182, 1), o);
            exec.Read(L("error from script =Set-ItemProperty : Cannot find path 'HKCU:\\Software\\Contoso' because it does not exist.\n", 183, 1), o);
            exec.Read(L("Agent executor completed.", 184, 1), o);
            t.ApplyAll(o);
            T.Eq(State.Failed, i != null ? i.State : State.Unknown, "error text that is not a warning fails a run with exit code 0");
            T.Eq(4, i != null ? i.Attempts.Count : 0, "every run is an attempt");
            T.Check(ScriptLog.OnlyWarnings("WARNING: one\r\n\r\nWARNING: two"), "warnings only");
            T.Check(!ScriptLog.OnlyWarnings("WARNING: one\nAccess is denied."), "a warning and an error");
        }

        const string Sid = "S-1-12-1-1111111111-2222222222-3333333333-4444444444";
        const string Fs = @"HKLM\SOFTWARE\Microsoft\Enrollments\1A2B3C4D-0000-4000-8000-000000000003\FirstSync";

        static string Hex(DateTime t)
        {
            byte[] b = BitConverter.GetBytes(t.ToFileTimeUtc());
            string[] h = new string[8];
            for (int i = 0; i < 8; i++) h[i] = b[i].ToString("x2");
            return string.Join(",", h);
        }

        // Account setup whose steps Windows never closed: no ...Duration value is ever written.
        static Session Account(MemoryRegistry r, bool continued, out Tracker t)
        {
            RegFile.Parse("[" + Fs.Replace("HKLM", "HKEY_LOCAL_MACHINE") + "]\r\n\"SyncFailureTimeout\"=dword:0000003c\r\n\"Timestamp\"=hex:" + Hex(T0) + "\r\n"
                + "\"PolicyDuration\"=dword:0000000a\r\n\"CertificatesDuration\"=dword:0000000a\r\n\"NetworkingDuration\"=dword:0000000a\r\n\"ApplicationsDuration\"=dword:0000012c\r\n"
                + (continued ? "\"WasContinuedAnyway\"=dword:00000001\r\n" : "")
                + "\r\n[" + Fs.Replace("HKLM", "HKEY_LOCAL_MACHINE") + "\\" + Sid + "]\r\n\"Timestamp\"=hex:" + Hex(T0.AddMinutes(20)) + "\r\n", r);
            Session s = new Session();
            t = new Tracker(s);
            t.ApplyAll(new EspFirstSync().Read(r, T0.AddMinutes(21)));
            return s;
        }

        static void Steps()
        {
            string apps = "esp:step:user:" + Sid + ":apps", pol = "esp:step:user:" + Sid + ":policies";
            Tracker t;

            // Nothing says account setup is over: the steps keep running.
            Session s = Account(new MemoryRegistry(), false, out t);
            Finder.Run(s, T0.AddHours(3), true);
            T.Eq(State.Installing, s.Find(apps) != null ? s.Find(apps).State : State.Unknown, "an account setup step without its time runs on while nothing says the phase is over");
            T.Check(s.Find("esp:step:device:apps").State == State.Succeeded, "device setup steps with their time are done");

            // Every app account setup waited for has ended: the steps are skipped, but only after a while.
            List<Obs> o = new List<Obs>();
            Obs a = Esp("intune:" + Expenses, "Contoso Expenses", Kinds.Win32App, State.Installing, 21 * 60);
            a.PhaseKey = Phases.AccountSetup;
            o.Add(a);
            Obs b = Esp("intune:" + Expenses, "Contoso Expenses", Kinds.Win32App, State.Succeeded, 25 * 60);
            b.PhaseKey = Phases.AccountSetup;
            o.Add(b);
            t.ApplyAll(o);
            Finder.Run(s, T0.AddMinutes(26), true);
            T.Eq(State.Installing, s.Find(apps).State, "a moment after the last app ended, the step may still get its time");
            Finder.Run(s, T0.AddMinutes(40), true);
            Item st = s.Find(apps);
            T.Eq(State.Skipped, st.State, "once the apps of account setup have all ended, the step Apps ends");
            T.Eq(State.Skipped, s.Find(pol).State, "and so do the other steps");
            T.Eq(T0.AddMinutes(25), st.EndUtc ?? DateTime.MinValue, "at the time the last app ended");
            T.Check(st.Events.Count > 0 && st.Events[st.Events.Count - 1].Text.StartsWith("Everything account setup waited for has ended"), "with the reason as its last event");
            bool stuck = false;
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.Stuck && f.ItemKey == apps) stuck = true;
            T.Check(!stuck, "an ESP step has no finding for no progress");

            // Continue anyway in device setup and a user who signed in.
            Session c = Account(new MemoryRegistry(), true, out t);
            Finder.Run(c, T0.AddMinutes(30), true);
            T.Eq(State.Skipped, c.Find(apps).State, "after Continue anyway with the user signed in, account setup steps without their time end");
            T.Check(c.Find(apps).Events[c.Find(apps).Events.Count - 1].Text.Contains("Continue anyway"), "and say so");

            // The ESP key of the user says account setup is done.
            MemoryRegistry r = new MemoryRegistry();
            r.Dword(EspWatch.Root + "\\" + Sid + @"\Setup", "HasProvisioningCompleted", -1);
            Session d = Account(r, false, out t);
            T.Eq(EspSteps.AccountDoneText, d.GetInfo(EspSteps.AccountDoneInfo), "the ESP key of the user says account setup is done");
            Finder.Run(d, T0.AddMinutes(21), true);
            T.Eq(State.Skipped, d.Find(apps).State, "then the steps without their time end at once");

            // A step with its time is left to EspFirstSync.
            MemoryRegistry r2 = new MemoryRegistry();
            r2.Dword(EspWatch.Root + "\\" + Sid + @"\Setup", "HasProvisioningCompleted", 1);
            Session e = Account(r2, false, out t);
            r2.Clock = T0.AddMinutes(21);
            r2.Dword(Fs + "\\" + Sid, "ApplicationsDuration", 50);
            t.ApplyAll(new EspFirstSync().Read(r2, T0.AddMinutes(21)));
            Finder.Run(e, T0.AddMinutes(21), true);
            T.Eq(State.Installing, e.Find(apps).State, "a step whose time Windows still counts is not skipped");
        }

        static void Saved()
        {
            Session s = new Session();
            Phase p = new Phase();
            p.Key = Phases.IntuneApps;
            p.Name = "Intune apps";
            p.Order = 90;
            s.Phases.Add(p);
            Item i = new Item();
            i.Key = IntuneKeys.App(Viewer);
            i.Kind = Kinds.Win32App;
            i.PhaseKey = Phases.IntuneApps;
            i.State = State.Pending;
            i.Set(AppGroups.AssignmentKey, "Available");
            s.Items.Add(i);
            Session back = Session.FromJson(s.ToJson());
            T.Eq("Apps", back.FindPhase(Phases.IntuneApps).Name, "a saved session with Intune apps shows Apps");
            T.Eq(State.Skipped, back.Items[0].State, "and an available app in it no longer waits");

            // In the reference enrollment the user installed it from Company Portal.
            back.Items[0].State = State.Succeeded;
            Reference r = Reference.From(back, T0, "Contoso", "this device");
            Session none = new Session();
            ReferenceResult res = ReferenceCheck.Check(none, T0, r);
            T.Eq(0, res.Missing.Count, "an available app of the reference is never missing");
        }
    }
}
