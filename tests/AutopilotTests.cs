namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // Made-up events and registry in the documented formats: the Autopilot event log, offline domain join,
    // hybrid join, pre-provisioning and account setup.
    public static class AutopilotTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);
        const string AppA = "1b2c3d4e-0001-4a6f-9b7d-3c0e2f5a8b13", AppB = "1b2c3d4e-0002-4b70-8c8e-4d1f306b9c24";
        const string Sid = "S-1-12-1-1111111111-2222222222-3333333333-4444444444";

        static List<Obs> M(string channel, int id, string message, int minutes, params object[] props)
        {
            return AutopilotEvents.Map(channel, id, new List<object>(props), message, T0.AddMinutes(minutes), id);
        }

        static Obs One(string channel, int id, string message, int minutes, params object[] props)
        {
            List<Obs> l = M(channel, id, message, minutes, props);
            return l.Count == 1 ? l[0] : null;
        }

        static Session Build(List<Obs> o)
        {
            Session s = new Session();
            new Tracker(s).ApplyAll(o);
            return s;
        }

        public static void Run()
        {
            Filters();
            Profile();
            Tpm();
            Hybrid();
            PreProvisioning();
            DevicePreparation();
            UserPhase();
        }

        static void Filters()
        {
            T.Eq("(Level>=0)", AutopilotEvents.Filter(AutopilotEvents.Autopilot, "all"), "the Autopilot log is read whole");
            T.Eq("(Level=1 or Level=2 or Level=3)", AutopilotEvents.Filter("Microsoft-Windows-AAD/Operational", "errors"), "other logs only for errors and warnings");
            string mdm = AutopilotEvents.Filter(AutopilotEvents.MdmAdmin, "errors+policy");
            T.Check(mdm.Contains("Level=2") && mdm.Contains("EventID=814") && mdm.Contains("EventID=111") && mdm.Contains("EventID=107") && mdm.Contains("EventID=72"), "the MDM log adds policies and the domain join events: " + mdm);
            T.Eq("(EventID=62407)", AutopilotEvents.Filter(AutopilotEvents.ShellCore, "ids"), "the setup screens only for their one event");
        }

        static void Profile()
        {
            Obs a = One(AutopilotEvents.Autopilot, 153, "AutopilotManager reported the state changed from ProfileState_Unknown to ProfileState_Available.", 1, (uint)0, (uint)2);
            T.Check(a != null && a.Text.StartsWith("Autopilot profile downloaded") && a.ItemKey == null && a.Severity == 0, "profile available is a plain line on the timeline");
            Obs b = One(AutopilotEvents.Autopilot, 101, null, 1, "CloudAssignedOobeConfig", 1310);
            T.Eq("Autopilot profile setting: CloudAssignedOobeConfig = 1310", b != null ? b.Text : null, "a profile setting in plain words");
            Obs c = One(AutopilotEvents.Autopilot, 707, null, 2, "0x80180014", "ZtdDeviceIsNotRegistered", "cv");
            T.Check(c != null && c.Severity == 2 && c.Text.Contains("0x80180014"), "a failed profile download is an error with its code");
            T.Eq(0, M(AutopilotEvents.Autopilot, 999, "Something else", 1).Count, "an unknown event is left to the timeline as it is");
        }

        static void Tpm()
        {
            List<Obs> o = new List<Obs>();
            o.AddRange(M(AutopilotEvents.Autopilot, 232, null, 1));
            o.AddRange(M(AutopilotEvents.Autopilot, 229, null, 3, "0x00000000", (ulong)95000));
            o.AddRange(M(AutopilotEvents.Autopilot, 178, null, 4, 1));
            o.AddRange(M(AutopilotEvents.Autopilot, 192, null, 5, -2145910764, 30000, 1, 3));
            o.AddRange(M(AutopilotEvents.Autopilot, 303, null, 7, (uint)9));
            Session s = Build(o);
            Item tpm = s.Find(AutopilotEvents.TpmKey), en = s.Find(AutopilotEvents.EnrollKey);
            T.Check(tpm != null && tpm.State == State.Succeeded && tpm.PhaseKey == Phases.DevicePreparation, "TPM attestation is an item in Device preparation");
            T.Check(tpm != null && tpm.Events.Count == 2 && tpm.Events[1].Text == "TPM attestation finished in " + Txt.DurationShort(TimeSpan.FromSeconds(95)) + ".", "with how long it took: " + (tpm != null ? tpm.Events[1].Text : ""));
            T.Check(en != null && en.State == State.Succeeded && en.Events.Count == 3 && en.Events[1].Severity == 1, "device enrollment with its retry as a warning");

            Obs f = One(AutopilotEvents.Autopilot, 230, null, 9, unchecked((int)0x800705B4), (ulong)600000);
            T.Check(f != null && f.State == State.Failed && f.Severity == 2 && f.ExitCode == unchecked((int)0x800705B4) && f.Text.Contains("0x800705B4"), "a TPM attestation failure keeps its code");
        }

        static void Hybrid()
        {
            List<Obs> o = M(AutopilotEvents.MdmAdmin, 111, "Offline Domain Join: Starting wait for offline domain join blob.", 10, true);
            T.Eq(2, o.Count, "the wait for the blob also says whether a domain controller is needed");
            o.AddRange(M(AutopilotEvents.MdmAdmin, 107, null, 14, 0));
            o.AddRange(M(AutopilotEvents.MdmAdmin, 105, null, 15, 0));
            o.AddRange(M(AutopilotEvents.MdmAdmin, 100, null, 25, 600000, unchecked((int)0x8007054B)));
            o.AddRange(M(AutopilotEvents.Registration, 304, null, 26, -2145648611, "", "Managed", "Join", ""));
            Session s = Build(o);
            Item odj = s.Find(AutopilotEvents.OdjKey), dc = s.Find(AutopilotEvents.DcKey), join = s.Find(AutopilotEvents.JoinKey);
            T.Check(odj != null && odj.State == State.Succeeded, "the blob was applied");
            T.Check(dc != null && dc.State == State.Failed && dc.Events[dc.Events.Count - 1].Text.StartsWith("No domain controller could be reached in " + Txt.DurationShort(TimeSpan.FromMinutes(10)) + ": error 0x8007054B"), "no domain controller within the wait");
            T.Check(join != null && join.State == State.Pending, "a failed registration only waits, since Windows tries again");
            Finder.Run(s, T0.AddMinutes(30), true);
            Finding hy = null;
            foreach (Finding f in s.Findings) if (f.Kind == AutopilotFlow.HybridJoin) hy = f;
            T.Check(hy != null && hy.ItemKey == AutopilotEvents.DcKey && hy.Severity == 2 && hy.Hint.Contains("Skip AD connectivity check"), "a hybrid join finding for the domain controller");
            bool generic = false;
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.Failed && f.ItemKey == AutopilotEvents.DcKey) generic = true;
            T.Check(!generic, "it replaces the general failed finding");

            Session t = Build(M(AutopilotEvents.MdmAdmin, 110, null, 40, 3));
            Finder.Run(t, T0.AddMinutes(41), true);
            T.Check(t.Findings.Count == 1 && t.Findings[0].Title.Contains("domain join blob") && t.Findings[0].Hint.Contains("ODJConnectorService"), "a timed out wait for the blob points to the connector");

            Obs skip = M(AutopilotEvents.MdmAdmin, 111, null, 10, false)[1];
            T.Check(skip.ItemKey == AutopilotEvents.DcKey && skip.State == State.NotApplicable, "the domain controller check can be skipped");
            T.Eq(0, M(AutopilotEvents.ShellCore, 62407, "CloudExperienceHost Web App Event 2. Name: 'OobeSomethingElse', Value: '1'.", 1).Count, "other setup screen events are ignored");
        }

        static List<Obs> Technician()
        {
            List<Obs> o = new List<Obs>();
            Obs esp = new Obs();
            esp.TimeUtc = T0;
            esp.Source = "ESP";
            esp.ItemKey = IntuneKeys.App(AppA);
            esp.ItemKind = Kinds.Win32App;
            esp.PhaseKey = Phases.DeviceSetup;
            esp.State = State.Installing;
            o.Add(esp);
            Obs done = new Obs();
            done.TimeUtc = T0.AddMinutes(30);
            done.Source = "ESP";
            done.ItemKey = IntuneKeys.App(AppA);
            done.State = State.Succeeded;
            o.Add(done);
            // An item that was still busy when the technician finished.
            Obs busy = new Obs();
            busy.TimeUtc = T0.AddMinutes(39);
            busy.Source = "IME";
            busy.ItemKey = IntuneKeys.App(AppB);
            busy.PhaseKey = Phases.IntuneApps;
            busy.State = State.Installing;
            o.Add(busy);
            return o;
        }

        static void PreProvisioning()
        {
            string msg = "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_WhiteGlove_Success', Value: 'true'.";
            List<Obs> o = Technician();
            o.AddRange(M(AutopilotEvents.ShellCore, 62407, msg, 40, "CommercialOOBE_ESPProgress_WhiteGlove_Success", "true"));
            Session s = Build(o);
            Tracker tr = new Tracker(s);
            DateTime boxed = T0.AddDays(3);
            Finder.Run(s, boxed, true);
            T.Eq("Technician phase finished, waiting for the user", s.GetInfo("Pre-provisioning"), "pre-provisioning is recognised");
            T.Check(!HasStuck(s), "no 'no progress' while the device waits in its box");
            T.Eq(boxed - T0.AddMinutes(40), AutopilotFlow.BoxedTime(s, boxed), "the boxed time is left out of the running time");

            // The user starts the device two days later.
            Obs user = new Obs();
            user.TimeUtc = T0.AddDays(2);
            user.Source = "ESP";
            user.ItemKey = "esp:esp:account:x";
            user.ItemName = "Account setup policy";
            user.PhaseKey = Phases.AccountSetup;
            user.State = State.Installing;
            tr.Apply(user);
            DateTime soon = T0.AddDays(2).AddMinutes(10);
            Finder.Run(s, soon, true);
            string info = s.GetInfo("Pre-provisioning") ?? "";
            T.Check(info.StartsWith("Technician phase finished ") && info.Contains("user phase started"), "the user phase is noted: " + info);
            T.Eq(TimeSpan.FromDays(2) - TimeSpan.FromMinutes(40), AutopilotFlow.BoxedTime(s, soon), "the gap ends when the user starts");
            T.Check(!HasStuck(s), "the boxed days do not count as silence");
            Finder.Run(s, T0.AddDays(2).AddMinutes(20), true);
            T.Check(HasStuck(s), "silence after the user started does count");

            Session plain = Build(Technician());
            Finder.Run(plain, boxed, true);
            T.Check(HasStuck(plain) && plain.GetInfo("Pre-provisioning") == null, "without pre-provisioning the silence is reported");

            Session failed = Build(M(AutopilotEvents.ShellCore, 62407, null, 40, "CommercialOOBE_ESPProgress_WhiteGlove_Failed", "true"));
            Item pp = failed.Find(AutopilotEvents.PreProvKey);
            T.Check(pp != null && pp.State == State.Failed && failed.GetInfo("Pre-provisioning") == "Technician phase failed", "a failed technician phase");
        }

        static bool HasStuck(Session s)
        {
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.Stuck) return true;
            return false;
        }

        static void DevicePreparation()
        {
            Session s = Build(M(AutopilotEvents.Autopilot, 410, null, 1));
            Finder.Run(s, T0.AddMinutes(2), true);
            T.Check(s.GetInfo(AutopilotEvents.DppInfo) != null && (s.GetInfo(AutopilotFlow.DevicePrepInfo) ?? "").Contains("not the page's own progress"), "device preparation is recognised and says what is not shown");

            List<Obs> o = M(AutopilotEvents.Autopilot, 410, null, 1);
            o.AddRange(Technician());
            Session e = Build(o);
            Finder.Run(e, T0.AddMinutes(45), true);
            T.Check(e.GetInfo(AutopilotFlow.DevicePrepInfo) == null, "not with ESP items, which device preparation does not have");
        }

        // The ESP registry as device setup leaves it, then as account setup fills it in.
        static void UserPhase()
        {
            MemoryRegistry r = new MemoryRegistry();
            string root = EspWatch.Root, diag = MdmWatch.Expected;
            r.Clock = T0.AddMinutes(20);
            r.Dword(root + @"\Device\Setup\Apps\Tracking\Sidecar\Win32App_" + AppA + "_1", "InstallationState", 3);
            r.Dword(diag + @"\ExpectedPolicies\2026-11-13T08:05:00.000Z", "./Device/Vendor/MSFT/Policy/Config/Update/DeferQualityUpdatesPeriodInDays", 1);
            EspWatch esp = new EspWatch();
            MdmWatch mdm = new MdmWatch();
            Session s = new Session();
            Tracker tr = new Tracker(s);
            List<Obs> o = esp.Read(r, T0.AddMinutes(25));
            o.AddRange(mdm.Read(r, T0.AddMinutes(25)));
            tr.ApplyAll(o);
            T.Check(s.FindPhase(Phases.AccountSetup) == null, "no account setup during device setup");

            r.Clock = T0.AddMinutes(40);
            r.Dword(root + "\\" + Sid + @"\Apps\Tracking\Sidecar\Win32App_" + AppB + "_1", "InstallationState", 2);
            r.Dword(diag + "\\" + Sid + @"\ExpectedModernAppPackages\2026-11-13T08:41:00.000Z", "./User/Vendor/MSFT/EnterpriseModernAppManagement/AppInstallation/Contoso.Notes_8wekyb3d8bbwe/HostedInstall", 0);
            o = esp.Read(r, T0.AddMinutes(42));
            o.AddRange(mdm.Read(r, T0.AddMinutes(42)));
            tr.ApplyAll(o);
            Item b = s.Find(IntuneKeys.App(AppB)), store = s.Find("esp:user:./user/vendor/msft/enterprisemodernappmanagement/appinstallation/contoso.notes_8wekyb3d8bbwe/hostedinstall");
            T.Check(b != null && b.PhaseKey == Phases.AccountSetup && b.State == State.Installing, "a user app under the user's SID is in account setup");
            T.Check(store != null && store.PhaseKey == Phases.AccountSetup && store.State == State.Installing && store.Name == "Contoso.Notes_8wekyb3d8bbwe", "a Store app account setup waits for, from the diagnostics under the SID");
            T.Eq("Account setup", store != null ? store.Get("ESP phase") : null, "its ESP phase says account setup");

            r.Clock = T0.AddMinutes(55);
            r.Dword(root + "\\" + Sid + @"\Apps\Tracking\Sidecar\Win32App_" + AppB + "_1", "InstallationState", 3);
            r.Dword(diag + "\\" + Sid + @"\ExpectedModernAppPackages\2026-11-13T08:54:00.000Z", "./User/Vendor/MSFT/EnterpriseModernAppManagement/AppInstallation/Contoso.Notes_8wekyb3d8bbwe/HostedInstall", 1);
            o = esp.Read(r, T0.AddMinutes(56));
            o.AddRange(mdm.Read(r, T0.AddMinutes(56)));
            tr.ApplyAll(o);
            Phase dev = s.FindPhase(Phases.DeviceSetup), acc = s.FindPhase(Phases.AccountSetup);
            T.Check(b.State == State.Succeeded && store.State == State.Succeeded, "both finish in account setup");
            T.Check(acc != null && acc.Items.Count == 2 && acc.EndUtc.HasValue && dev != null && dev.Items.Count == 2, "account setup is complete with its own items");
            T.Check(acc != null && dev != null && acc.StartUtc.HasValue && dev.EndUtc.HasValue && acc.StartUtc.Value >= dev.EndUtc.Value, "account setup comes after device setup");
        }
    }
}
