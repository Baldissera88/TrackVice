namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class WhyTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

        static Obs O(string key, string kind, string phase, double min, State? st)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddMinutes(min);
            o.Source = "test";
            o.ItemKey = key;
            o.ItemName = "Contoso " + key;
            o.ItemKind = kind;
            o.PhaseKey = phase;
            o.State = st;
            o.Text = "at " + min;
            return o;
        }

        static Finding Get(Session s, string kind)
        {
            foreach (Finding f in s.Findings) if (f.Kind == kind) return f;
            return null;
        }

        static int CountKind(Session s, string kind)
        {
            int n = 0;
            foreach (Finding f in s.Findings) if (f.Kind == kind) n++;
            return n;
        }

        public static void Run()
        {
            // Knowledge base.
            T.Check(Errors.Meaning(1605) != null, "1605 has a meaning");
            T.Check(Errors.Meaning(unchecked((int)0x8007064C)) == ErrorBase.Meaning(1612), "0x8007064C falls back to 1612");
            T.Check(Errors.Hint(unchecked((int)0x80190194)) != null, "HTTP 404 has a next step");
            T.Check(Errors.Hint(unchecked((int)0x80091007)).Contains("redistribute"), "hash mismatch says redistribute");
            T.Check(ErrorBase.OfficeMeaning(17006).Contains("Running Office apps"), "Office 17006");
            T.Eq(null, ErrorBase.Meaning(0x12345), "unknown code has no meaning");
            T.Eq("A fatal error inside the installer. Add /l*v to the msiexec command line and look for \"return value 3\" in the MSI log.", Errors.Hint(1603), "existing hint wins");

            // A failed platform script that ran in the 32-bit host.
            Session s = new Session();
            Tracker t = new Tracker(s);
            t.Apply(O("script", Kinds.Script, Phases.Scripts, 0, State.Installing));
            Obs x = O("script", Kinds.Script, Phases.Scripts, 1, null);
            x.ExitCode = 1;
            x.AddInfo("PowerShell exit code", "1").AddInfo("PowerShell host", "32-bit");
            t.Apply(x);
            Obs er = O("script", Kinds.Script, Phases.Scripts, 1.1, null);
            er.Severity = 2;
            er.Text = "Script error: Cannot find path 'HKLM:\\SOFTWARE\\Contoso\\Agent' because it does not exist.";
            t.Apply(er);
            t.Apply(O("script", Kinds.Script, Phases.Scripts, 1.2, State.Failed));
            Finder.Run(s, T0.AddMinutes(5), false);
            Finding sf = Get(s, FindingKinds.Failed);
            T.Check(sf != null && sf.Message.Contains("Cannot find path 'HKLM:\\SOFTWARE\\Contoso\\Agent'"), "script error is quoted");
            T.Check(sf != null && sf.Message.Contains("exit code 1") && sf.Message.Contains("32-bit"), "script exit code and host");
            T.Check(sf != null && sf.Hint.Contains("64 bit PowerShell Host"), "32/64-bit mismatch warned");
            T.Check(!Why.LikelyBitnessMismatch("64-bit", "Cannot find path 'HKLM:\\SOFTWARE\\Contoso'"), "no mismatch in the 64-bit host");

            // Office and MSI items.
            Session o = new Session();
            Tracker to = new Tracker(o);
            Obs of = O("office", Kinds.Office, Phases.IntuneApps, 1, State.Failed);
            of.ExitCode = 17004;
            to.Apply(of);
            Obs ms = O("msi", Kinds.MsiApp, Phases.IntuneApps, 2, State.Failed);
            ms.ExitCode = unchecked((int)0x80070070);
            ms.Text = "MSI: install failed, 0x80070070";
            to.Apply(ms);
            Finder.Run(o, T0.AddMinutes(5), false);
            Finding off = null, msf = null;
            foreach (Finding f in o.Findings)
            {
                if (f.ItemKey == "office") off = f;
                if (f.ItemKey == "msi") msf = f;
            }
            T.Check(off != null && off.Message.Contains("status 17004") && off.Message.Contains("CDN"), "Office status explained");
            T.Check(off != null && off.Hint.Contains("language"), "Office next step");
            T.Check(msf != null && msf.Message.Contains("\"install failed\"") && msf.Message.Contains("not enough space"), "MSI status and LastError explained");

            // ESP time limit ahead.
            Session e = new Session();
            Tracker te = new Tracker(e);
            te.Apply(O("slow", Kinds.Win32App, Phases.DeviceSetup, 0, State.Installing));
            e.SetInfo("ESP time limit (min)", "60");
            Finder.Run(e, T0.AddMinutes(40), true);
            T.Check(Get(e, FindingKinds.EspTimeLimitAhead) == null, "not near the limit at 40 of 60 min");
            Finder.Run(e, T0.AddMinutes(50), true);
            Finding ah = Get(e, FindingKinds.EspTimeLimitAhead);
            T.Check(ah != null && ah.Message.Contains("10 min are left") && ah.Message.Contains("Contoso slow"), "near the limit at 50 of 60 min");
            Finder.Run(e, T0.AddMinutes(61), true);
            T.Check(Get(e, FindingKinds.EspTimeLimitAhead) == null && Get(e, FindingKinds.EspTimeLimit) != null, "never together with ran past");
            te.Apply(O("slow", Kinds.Win32App, Phases.DeviceSetup, 52, State.Succeeded));
            Finder.Run(e, T0.AddMinutes(55), true);
            T.Check(Get(e, FindingKinds.EspTimeLimitAhead) == null, "gone when the phase ends");

            // Waiting for a restart.
            Session r = new Session();
            Tracker tr = new Tracker(r);
            tr.Apply(O("a", Kinds.Application, Phases.SccmApps, 0, State.Pending));
            tr.Apply(O("b", Kinds.Application, Phases.SccmApps, 0, State.Pending));
            tr.Apply(O("first", Kinds.Application, Phases.SccmApps, 1, State.Installing));
            tr.Apply(O("first", Kinds.Application, Phases.SccmApps, 2, State.WaitingReboot));
            Finder.Run(r, T0.AddMinutes(4), true);
            T.Check(Get(r, FindingKinds.WaitingRestart) == null && Get(r, FindingKinds.Reboot) != null, "a short wait is only a restart note");
            Finder.Run(r, T0.AddMinutes(12), true);
            Finding wr = Get(r, FindingKinds.WaitingRestart);
            T.Check(wr != null && wr.Message.Contains("2 items") && wr.ItemKey == "first", "waiting for a restart found");
            T.Check(Get(r, FindingKinds.Reboot) == null, "restart note replaced");

            // Three items failing with the same code in one phase.
            Session c = new Session();
            Tracker tc = new Tracker(c);
            for (int k = 0; k < 3; k++)
            {
                Obs f = O("app" + k, Kinds.Win32App, Phases.AccountSetup, 10 + k, State.Failed);
                f.ExitCode = unchecked((int)0x80072EE7);
                tc.Apply(f);
            }
            Obs other = O("other", Kinds.Win32App, Phases.AccountSetup, 1, State.Failed);
            other.ExitCode = 1603;
            tc.Apply(other);
            Finder.Run(c, T0.AddMinutes(20), false);
            T.Eq(FindingKinds.CommonCause, c.Findings[0].Kind, "common cause ranked first");
            T.Check(c.Findings[0].Message.Contains("could not be resolved"), "common cause explained");
            T.Eq(4, CountKind(c, FindingKinds.Failed), "separate failures kept");
            T.Eq(1, CountKind(c, FindingKinds.CommonCause), "one common cause");

            // Not detected after a 0 exit names the detection result.
            Session d = new Session();
            Tracker td = new Tracker(d);
            td.Apply(O("crm", Kinds.Application, Phases.SccmApps, 0, State.Installing));
            Obs det = O("crm", Kinds.Application, Phases.SccmApps, 1, null);
            det.Text = "Detection: not installed";
            det.Evidence = new Evidence(EvidenceKinds.LogLine, "C:\\Windows\\CCM\\Logs\\AppDiscovery.log", 7, "+++ Application not discovered.");
            td.Apply(det);
            Obs nd = O("crm", Kinds.Application, Phases.SccmApps, 1.5, State.Failed);
            nd.ExitCode = unchecked((int)0x87D00324);
            td.Apply(nd);
            Finder.Run(d, T0.AddMinutes(5), false);
            Finding ndf = Get(d, FindingKinds.NotDetected);
            T.Check(ndf != null && ndf.Message.Contains("Detection: not installed") && ndf.Message.Contains("AppDiscovery.log, line 7 says"), "detection evidence named");
        }
    }
}
