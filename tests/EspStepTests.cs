namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    // FirstSync as a real enrollment wrote it, with made-up IDs: device setup continued anyway, then account setup.
    public static class EspStepTests
    {
        const string Reg = "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Enrollments\\1A2B3C4D-0000-4000-8000-000000000001\\FirstSync]\r\n"
            + "\"SyncFailureTimeout\"=dword:0000003c\r\n\"IsServerProvisioningDone\"=dword:00000001\r\n"
            + "\"Timestamp\"=hex:5b,3e,cc,46,7e,74,dd,01\r\n\"NetworkingDuration\"=dword:00000005\r\n\"CertificatesDuration\"=dword:00000005\r\n"
            + "\"PolicyDuration\"=dword:00000005\r\n\"ApplicationsDuration\"=dword:000002b2\r\n\"WasContinuedAnyway\"=dword:00000001\r\n\r\n"
            + "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Enrollments\\1A2B3C4D-0000-4000-8000-000000000001\\FirstSync\\S-1-12-1-1111111111-2222222222-3333333333-4444444444]\r\n"
            + "\"Timestamp\"=hex:d4,02,c7,6f,7f,74,dd,01\r\n\"ApplicationsDuration\"=dword:000000be\r\n\"CertificatesDuration\"=dword:00000023\r\n"
            + "\"PolicyDuration\"=dword:00000023\r\n\"NetworkingDuration\"=dword:00000023\r\n\"IsServerProvisioningDone\"=dword:00000001\r\n";

        static string Hex(DateTime t)
        {
            byte[] b = BitConverter.GetBytes(t.ToFileTimeUtc());
            string[] h = new string[8];
            for (int i = 0; i < 8; i++) h[i] = b[i].ToString("x2");
            return string.Join(",", h);
        }

        public static void Run()
        {
            MemoryRegistry r = new MemoryRegistry();
            RegFile.Parse(Reg, r);
            DateTime now = new DateTime(2026, 11, 14, 22, 0, 0, DateTimeKind.Utc);
            EspFirstSync w = new EspFirstSync();
            List<Obs> o = w.Read(r, now);
            T.Eq(0, w.Read(r, now).Count, "an unchanged FirstSync adds nothing");
            Session s = new Session();
            new Tracker(s).ApplyAll(o);
            Item devApps = s.Find("esp:step:device:apps");
            T.Check(devApps != null && devApps.PhaseKey == Phases.DeviceSetup && devApps.State == State.Succeeded, "device setup apps are done");
            T.Eq(new DateTime(2026, 11, 14, 21, 22, 39, DateTimeKind.Utc), devApps != null ? new DateTime(devApps.FirstUtc.Ticks - devApps.FirstUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc) : DateTime.MinValue, "the phase starts at its timestamp");
            T.Eq(TimeSpan.FromSeconds(690), devApps != null ? devApps.Duration(now).Value : TimeSpan.Zero, "and the step took its duration");
            Item userApps = s.Find("esp:step:user:S-1-12-1-1111111111-2222222222-3333333333-4444444444:apps");
            T.Check(userApps != null && userApps.PhaseKey == Phases.AccountSetup && userApps.State == State.Succeeded, "account setup apps are done");
            T.Eq(TimeSpan.FromSeconds(190), userApps != null ? userApps.Duration(now).Value : TimeSpan.Zero, "account setup apps took 3 min 10 s");
            Item pol = s.Find("esp:step:user:S-1-12-1-1111111111-2222222222-3333333333-4444444444:policies");
            T.Eq("Security policies", pol != null ? pol.Name : null, "the steps have the names the ESP shows");
            Item cont = s.Find("esp:continued");
            T.Check(cont != null && cont.PhaseKey == Phases.DeviceSetup, "Continue anyway is shown");
            T.Check(EspFirstSync.FileTime(new RegValue("REG_BINARY", "5B 3E CC 46 7E 74 DD 01")).HasValue, "a FILETIME read from the live registry");
            T.Check(!EspFirstSync.FileTime(new RegValue("REG_BINARY", "00")).HasValue, "a value that is not a FILETIME");

            // Account setup that has just started: every step has counted 10 s, written 10 s after the start.
            DateTime st = new DateTime(2026, 11, 14, 21, 55, 58, DateTimeKind.Utc);
            T.Check(EspFirstSync.Running(st, 10, st.AddSeconds(10), st.AddSeconds(15)), "a step whose count reaches the last write a moment ago is running");
            T.Check(!EspFirstSync.Running(st, 10, st.AddSeconds(200), st.AddSeconds(205)), "a step that stopped counting while others went on is done");
            T.Check(!EspFirstSync.Running(st, 190, st.AddSeconds(190), st.AddSeconds(600)), "the last step is done when nothing has been written for a while");
            MemoryRegistry live = new MemoryRegistry();
            live.Clock = st.AddSeconds(10);
            string fs = @"HKLM\SOFTWARE\Microsoft\Enrollments\1A2B3C4D-0000-4000-8000-000000000002\FirstSync";
            live.Dword(fs, "SyncFailureTimeout", 60);
            live.Sz(fs + @"\S-1-12-1-1-2-3-4", "Timestamp", "");
            RegFile.Parse("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Enrollments\\1A2B3C4D-0000-4000-8000-000000000002\\FirstSync\\S-1-12-1-1-2-3-4]\r\n\"Timestamp\"=hex:" + Hex(st) + "\r\n\"PolicyDuration\"=dword:0000000a\r\n\"ApplicationsDuration\"=dword:0000000a\r\n", live);
            EspFirstSync lw = new EspFirstSync();
            Session ls = new Session();
            Tracker lt = new Tracker(ls);
            lt.ApplyAll(lw.Read(live, st.AddSeconds(15)));
            Item la = ls.Find("esp:step:user:S-1-12-1-1-2-3-4:apps");
            T.Check(la != null && la.State == State.Installing, "account setup apps are running while the ESP still counts");
            live.Clock = st.AddSeconds(200);
            live.Dword(fs + @"\S-1-12-1-1-2-3-4", "ApplicationsDuration", 200);
            lt.ApplyAll(lw.Read(live, st.AddSeconds(205)));
            Item lp = ls.Find("esp:step:user:S-1-12-1-1-2-3-4:policies");
            T.Check(lp != null && lp.State == State.Succeeded, "policies are done once their count stopped");
            T.Check(la.State == State.Installing, "apps still run while their count goes on");
            lt.ApplyAll(lw.Read(live, st.AddSeconds(400)));
            T.Check(la.State == State.Succeeded, "and are done when nothing more is written");

            string dir = Path.Combine(Path.GetTempPath(), "trackvice-same-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string a = Path.Combine(dir, "a.exe"), b = Path.Combine(dir, "b.exe"), c = Path.Combine(dir, "c.exe");
                File.WriteAllBytes(a, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(b, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(c, new byte[] { 1, 2, 4 });
                T.Check(Agent.SameFile(a, b), "the same exe needs no copy, so a running agent is left alone");
                T.Check(!Agent.SameFile(a, c), "a new exe is copied");
                T.Check(!Agent.SameFile(a, Path.Combine(dir, "missing.exe")), "no exe installed yet");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
