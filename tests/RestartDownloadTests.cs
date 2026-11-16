namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class RestartDownloadTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 14, 22, 30, 0, DateTimeKind.Utc);
        static long rec;

        static SysEvent E(string provider, int id, double sec, params object[] props)
        {
            SysEvent e = new SysEvent();
            e.Provider = provider;
            e.Id = id;
            e.TimeUtc = T0.AddSeconds(sec);
            e.Record = ++rec;
            e.Props.AddRange(props);
            return e;
        }

        // Event 1074 as User32 writes it: process (computer), computer, reason, reason code, type, comment, user.
        static SysEvent Request(double sec, string process, string reason, string user)
        {
            return E("User32", 1074, sec, process + " (CONTOSO-PC01)", "CONTOSO-PC01", reason, "0x80040002", "restart", "", user);
        }

        public static void Run()
        {
            Requesters();
            Grouping();
            Unexpected();
            Watch();
            Numbers();
            Parsing();
            SlowFinding();
            Fleet();
            CompareRestarts();
            OldHistory();
            NeverStarted();
        }

        // The System log keeps restarts from long before the enrollment. Only those after the floor count.
        static void OldHistory()
        {
            RestartWatch w = new RestartWatch();
            w.Floor = T0;
            List<Obs> o = w.Add(new SysEvent[]
            {
                E("Microsoft-Windows-Kernel-General", 13, -86400 * 300.0), E("Microsoft-Windows-Kernel-General", 12, -86400 * 300.0 + 60),
                E("Microsoft-Windows-Kernel-General", 13, 600), E("Microsoft-Windows-Kernel-General", 12, 660),
            });
            HashSet<string> keys = new HashSet<string>();
            foreach (Obs x in o) keys.Add(x.ItemKey);
            T.Eq(1, keys.Count, "restarts: one from before the floor is left out");
        }

        // A restart that was asked for but Windows never started again: closed by the next one, or by time on the device.
        static void NeverStarted()
        {
            List<Restart> rs = Restarts.Build(new SysEvent[]
            {
                Request(0, @"C:\Windows\system32\winlogon.exe", "Other (Unplanned)", "CONTOSO\\anna"),
                Request(7200, @"C:\Windows\system32\winlogon.exe", "Other (Unplanned)", "CONTOSO\\anna"),
                E("Microsoft-Windows-Kernel-General", 13, 7210), E("Microsoft-Windows-Kernel-General", 12, 7260),
            });
            T.Eq(2, rs.Count, "restarts: a request hours before the next is its own restart");
            T.Check(!rs[0].BootUtc.HasValue && rs[1].BootUtc.HasValue, "restarts: only the second started again");

            RestartWatch w = new RestartWatch();
            State? first = null, second = null;
            foreach (Obs x in w.Add(new SysEvent[] { E("User32", 1074, 10000, "p (C)", "C", "r", "0x0", "restart", "", "u"), E("Microsoft-Windows-Kernel-General", 13, 10010),
                E("User32", 1074, 20000, "p (C)", "C", "r", "0x0", "restart", "", "u"), E("Microsoft-Windows-Kernel-General", 13, 20010), E("Microsoft-Windows-Kernel-General", 12, 20060) }))
            {
                if (!x.State.HasValue) continue;
                if (x.ItemKey == "restart:" + T0.AddSeconds(10000).ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture)) first = x.State;
                else second = x.State;
            }
            T.Check(first == State.Skipped, "restarts: one without a start is closed by the next restart");
            T.Check(second == State.Succeeded, "restarts: the next one is done");

            RestartWatch live = new RestartWatch();
            live.Clock = delegate { return T0.AddSeconds(30000 + 3600); };
            bool closed = false;
            foreach (Obs x in live.Add(new SysEvent[] { E("Microsoft-Windows-Kernel-General", 13, 30000) }))
                if (x.State == State.Skipped) closed = true;
            T.Check(closed, "restarts: on the device, one without a start is closed after half an hour");
            RestartWatch fresh = new RestartWatch();
            fresh.Clock = delegate { return T0.AddSeconds(40000 + 60); };
            foreach (Obs x in fresh.Add(new SysEvent[] { E("Microsoft-Windows-Kernel-General", 13, 40000) }))
                T.Check(x.State != State.Skipped, "restarts: a restart a minute ago is still going on");
        }

        static Session WithRestart(string computer, DateTime start, double restartAfter)
        {
            Session s = new Session();
            s.Computer = computer;
            Item app = new Item();
            app.Key = "app-1";
            app.Name = "Contoso VPN Client 5.2";
            app.Kind = Kinds.Win32App;
            app.PhaseKey = Phases.IntuneApps;
            app.State = State.Succeeded;
            app.FirstUtc = app.StateUtc = app.LastUtc = start;
            s.Items.Add(app);
            DateTime at = start.AddSeconds(restartAfter);
            Item r = new Item();
            r.Key = "restart:" + at.ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            r.Name = "Restart at " + at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            r.Kind = Kinds.Restart;
            r.PhaseKey = Phases.Restarts;
            r.State = State.Succeeded;
            r.FirstUtc = r.StateUtc = r.LastUtc = at;
            s.Items.Add(r);
            s.ClockUtc = at.AddMinutes(5);
            return s;
        }

        // Restarts are named after their time, so two enrollments never share a name for them.
        static void CompareRestarts()
        {
            Session a = WithRestart("CONTOSO-PC01", T0, 600);
            Session b = WithRestart("CONTOSO-PC02", T0.AddDays(-1), 1500);
            int restartRows = 0;
            foreach (DiffRow row in SessionDiff.Compare(a, b))
                if (!row.IsFinding && ((row.ItemA != null && row.ItemA.Kind == Kinds.Restart) || (row.ItemB != null && row.ItemB.Kind == Kinds.Restart)))
                {
                    restartRows++;
                    T.Check(row.ItemA != null && row.ItemB != null, "compare: the first restart on each side is one row");
                }
            T.Eq(1, restartRows, "compare: restarts at different times are paired");
            Reference r = Reference.From(b, b.ClockUtc, "Contoso", "test");
            ReferenceResult res = ReferenceCheck.Check(a, a.ClockUtc, r);
            T.Eq(0, res.Extra.Count + res.Missing.Count, "reference: a restart at another time is not extra or missing");
        }

        static void Requesters()
        {
            T.Eq("Microsoft Intune Management Extension", Restarts.Requester(@"C:\Program Files (x86)\Microsoft Intune Management Extension\Microsoft.Management.Services.IntuneWindowsAgent.exe", "Application: Installation (Planned)", "", @"NT AUTHORITY\SYSTEM"), "IME restart");
            T.Eq("TrustedInstaller / Windows Update", Restarts.Requester(@"C:\Windows\servicing\TrustedInstaller.exe", "Operating System: Upgrade (Planned)", "", @"NT AUTHORITY\SYSTEM"), "servicing restart");
            T.Eq("TrustedInstaller / Windows Update", Restarts.Requester(@"C:\Windows\System32\svchost.exe", "Operating System: Service pack (Planned)", "", @"NT AUTHORITY\SYSTEM"), "update restart from svchost");
            T.Eq("Enrollment Status Page", Restarts.Requester(@"C:\Windows\System32\CloudExperienceHostBroker.exe", "Other (Planned)", "", @"NT AUTHORITY\SYSTEM"), "ESP restart");
            T.Eq("smsts / task sequence", Restarts.Requester(@"C:\Windows\CCM\TSManager.exe", "", "", @"NT AUTHORITY\SYSTEM"), "task sequence restart");
            T.Eq("user", Restarts.Requester(@"C:\Windows\System32\winlogon.exe", "Other (Unplanned)", "", @"CONTOSO\anna.hansen"), "user restart");
            T.Eq("winlogon.exe", Restarts.Requester(@"C:\Windows\System32\winlogon.exe", "", "", @"NT AUTHORITY\SYSTEM"), "winlogon as system is not a user");
            T.Eq("contosotool.exe", Restarts.Requester(@"C:\Tools\ContosoTool.exe", "", "", ""), "other process by its file name");
            string f = Restarts.Filter();
            T.Check(f.Contains("EventID=1074") && f.Contains("EventID=6008") && f.Contains("EventID=41") && f.Contains("Kernel-General") && !f.Contains("Level"), "the query asks for the restart IDs only");
        }

        static void Grouping()
        {
            List<SysEvent> l = new List<SysEvent>
            {
                Request(60, @"C:\Program Files (x86)\Microsoft Intune Management Extension\Microsoft.Management.Services.IntuneWindowsAgent.exe", "Application: Installation (Planned)", @"NT AUTHORITY\SYSTEM"),
                E("Microsoft-Windows-Kernel-General", 13, 66, "2026-11-14T22:31:05.500000000Z"),
                E("EventLog", 6006, 67),
                E("Microsoft-Windows-Kernel-General", 12, 160, 10, 0, 26100, 0, 0, 0, "2026-11-14T22:32:35.000000000Z"),
                E("EventLog", 6005, 170),
                // A second, user restart later.
                Request(3600, @"C:\Windows\System32\winlogon.exe", "Other (Unplanned)", @"CONTOSO\anna.hansen"),
                E("Microsoft-Windows-Kernel-General", 13, 3602, T0.AddSeconds(3602)),
                E("Microsoft-Windows-Kernel-General", 12, 3700, T0.AddSeconds(3690)),
            };
            List<Restart> rs = Restarts.Build(l);
            T.Eq(2, rs.Count, "two restarts");
            Restart r = rs[0];
            T.Eq("Microsoft Intune Management Extension", r.Requester, "first restart asked for by the IME");
            T.Eq(T0.AddSeconds(65.5), r.StopUtc, "stop from StopTime of event 13");
            T.Eq(T0.AddSeconds(155), r.BootUtc, "start from StartTime of event 12");
            T.Eq(TimeSpan.FromSeconds(89.5), r.Downtime, "downtime from stop to start");
            T.Eq(5, r.Events.Count, "6005 belongs to the same start");
            T.Eq("Restart at 22:31", Restarts.Name(r, TimeZoneInfo.Utc), "named by the stop time");
            T.Check(!r.Unexpected, "a clean restart");
            T.Eq("user", rs[1].Requester, "second restart by the user");
            T.Eq(TimeSpan.FromSeconds(88), rs[1].Downtime, "time properties as DateTime");
            List<Pair> info = Restarts.Info(r);
            T.Check(info.Exists(delegate (Pair p) { return p.Key == "Downtime" && p.Value == "1 min 29 s"; }), "downtime in the details");
            T.Check(info.Exists(delegate (Pair p) { return p.Key == "Reason" && p.Value == "Application: Installation (Planned) (0x80040002)"; }), "reason with its code");

            // Unexpected events with no start before them in the history are left out.
            T.Eq(0, Restarts.Build(new SysEvent[] { E("EventLog", 6008, 10, "22:29:00", "11/14/2026") }).Count, "orphan 6008 left out");
        }

        static void Unexpected()
        {
            Func<string, DateTime?> keep = Restarts.LocalToUtc;
            try
            {
                Restarts.LocalToUtc = delegate (string text)
                {
                    DateTime u;
                    return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out u) ? u : (DateTime?)null;
                };
                List<SysEvent> l = new List<SysEvent>
                {
                    E("Microsoft-Windows-Kernel-General", 12, 300),
                    E("EventLog", 6005, 305),
                    E("EventLog", 6008, 330, "\u200e22:31:40", "\u200e11/14/2026"),
                    E("Microsoft-Windows-Kernel-Power", 41, 340, "307", "0", "0", "0", "0", "false", "0"),
                };
                List<Restart> rs = Restarts.Build(l);
                T.Eq(1, rs.Count, "one unexpected restart");
                Restart r = rs[0];
                T.Check(r.Unexpected, "marked unexpected");
                T.Eq("unexpected, no clean shutdown", r.Requester, "nobody asked for it");
                T.Eq(T0.AddSeconds(100), r.StopUtc, "stop time from event 6008");
                T.Eq(TimeSpan.FromSeconds(200), r.Downtime, "downtime of the unexpected stop");
                T.Eq(307L, r.BugCheck, "bug check code from event 41, in decimal");
                T.Check(Restarts.Text(l[3], r).Contains("0x00000133"), "bug check shown in hex");

                Session s = new Session();
                Tracker t = new Tracker(s);
                RestartWatch w = new RestartWatch();
                w.Zone = TimeZoneInfo.Utc;
                t.ApplyAll(w.Add(l));
                Item i = s.Items.Find(delegate (Item x) { return x.PhaseKey == Phases.Restarts; });
                T.Check(i != null && i.State == State.Succeeded, "unexpected restart item done");
                T.Eq(T0.AddSeconds(100), i != null ? i.StartUtc : null, "bar starts at the unexpected stop");
                T.Check(s.Events.Exists(delegate (TEvent e) { return e.Severity == 2 && e.Text.StartsWith("Restarted after a stop error"); }), "event 41 is an error on the timeline");
            }
            finally { Restarts.LocalToUtc = keep; }
        }

        static void Watch()
        {
            Session s = new Session();
            Tracker t = new Tracker(s);
            RestartWatch w = new RestartWatch();
            w.Zone = TimeZoneInfo.Utc;
            SysEvent req = Request(60, @"C:\Windows\CCM\TSManager.exe", "", @"NT AUTHORITY\SYSTEM");
            SysEvent stop = E("Microsoft-Windows-Kernel-General", 13, 66);
            t.ApplyAll(w.Add(new SysEvent[] { req, stop }));
            Item i = s.Items.Count == 1 ? s.Items[0] : null;
            T.Check(i != null && i.PhaseKey == Phases.Restarts && i.Kind == Kinds.Restart, "restart item in the Restarts phase");
            T.Eq("Restart at 22:31", i != null ? i.Name : null, "restart name");
            T.Eq(State.Installing, i != null ? i.State : State.Unknown, "restarting until Windows starts again");
            T.Eq("smsts / task sequence", i != null ? i.Get("Requested by") : null, "requester in the details");
            int events = s.Events.Count;
            t.ApplyAll(w.Add(new SysEvent[] { req, stop }));
            T.Eq(events, s.Events.Count, "the same events are not added twice");
            t.ApplyAll(w.Add(new SysEvent[] { E("Microsoft-Windows-Kernel-General", 12, 126), E("EventLog", 6005, 130) }));
            T.Eq(1, s.Items.Count, "the start completes the same restart");
            T.Eq(State.Succeeded, i.State, "restart done after the start");
            T.Eq(T0.AddSeconds(66), i.StartUtc, "bar from the stop");
            T.Eq(T0.AddSeconds(126), i.EndUtc, "bar to the start");
            T.Eq("1 min 00 s", i.Get("Downtime"), "downtime added when Windows started");
            T.Check(Phases.Order(Phases.Restarts) > Phases.Order(Phases.Scripts) && Phases.Order(Phases.Restarts) < Phases.Order(Phases.Policies), "Restarts last before Policies");

            List<GanttTick> marks = GanttLayout.RestartMarks(s, TimeZoneInfo.Utc);
            T.Eq(1, marks.Count, "one restart marker");
            T.Eq(T0.AddSeconds(66), marks[0].Utc, "marker at the stop");
            T.Eq("Restart 22:31", marks[0].Label, "marker label");
            string html = Report.Html(s, T0.AddHours(1));
            T.Check(html.Contains("class=\"grst") && html.Contains("<span>Restart "), "restart marker in the report's Gantt");
            Finder.Run(s, T0.AddHours(1), false);
            T.Check(!s.Findings.Exists(delegate (Finding f) { return f.ItemKey == i.Key; }), "a restart gives no finding by itself");
        }

        static void Numbers()
        {
            T.Eq(2742L, DownloadLog.Number("2,742.00"), "size with thousands and decimals");
            T.Eq(2742L, DownloadLog.Number("2.742,00"), "size in a culture with a decimal comma");
            T.Eq(1234567L, DownloadLog.Number("1,234,567"), "size without decimals");
            T.Eq(5055L, DownloadLog.Number("5055"), "plain number");
            T.Eq("290 MB", DownloadLog.Bytes(304087040), "bytes as MB");
            T.Eq("2.7 KB", DownloadLog.Bytes(2742), "bytes as KB");
            T.Eq("1.5 GB", DownloadLog.Bytes(1610612736), "bytes as GB");
            T.Eq("4.6 Mbit/s", Downloads.Speed(4.61), "slow speed with a decimal");
            T.Eq("38 Mbit/s", Downloads.Speed(38.2), "fast speed without");
        }

        static int n;

        static LogLine L(string text, double sec)
        {
            LogLine l = new LogLine();
            l.Text = text;
            l.Raw = text;
            l.Utc = T0.AddSeconds(sec);
            l.File = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log";
            l.LineNo = ++n;
            l.Thread = "33";
            return l;
        }

        // Lines in the shape AppWorkload.log has them, for a made-up app.
        static List<LogLine> Lines(string id, string name, long bytes, double seconds, bool peers)
        {
            string ms = (seconds * 1000).ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture);
            List<LogLine> l = new List<LogLine>
            {
                L("[Win32App][DownloadActionHandler] Handler invoked for policy with id: " + id + " and version: 1", 0),
                L("[Win32App] Downloading app on session 0. App: " + id, 0.1),
                L("[Win32App] Content cache miss for app (id = " + id + ", name = " + name + "), start downloading...", 0.6),
                L("[Win32App DO] start creating a new download job, FileId = 11111111-2222-4333-8444-555555555555_" + id + "_1, remote URL = https://contoso.example/" + id + ".intunewin.bin", 0.6),
                L("[Win32App DO] DO Job Id is 0f1e2d3c-4b5a-4968-8776-655443322110", 0.6),
                L("[StatusService] Downloading app (id = " + id + ", name " + name + ") via DO, bytes 0/0 for user 00000000-0000-0000-0000-000000000000", 0.7),
                L("[StatusService] Downloading app (id = " + id + ", name " + name + ") via DO, bytes " + (bytes / 2) + "/" + bytes + " for user 00000000-0000-0000-0000-000000000000", 0.6 + seconds / 2),
                L("[StatusService] Downloading app (id = " + id + ", name " + name + ") via DO, bytes " + bytes + "/" + bytes + " for user 00000000-0000-0000-0000-000000000000", 0.6 + seconds),
            };
            if (peers) l.Add(L("[Win32App DO] Job statistics: BytesFromPeers=" + (bytes * 3 / 4) + ", BytesFromHttp=" + (bytes / 4), 0.6 + seconds));
            l.Add(L("[Win32App DO] DO download and decryption is successfully done", 0.7 + seconds));
            l.Add(L("[Win32App] Downloaded file size " + bytes.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture), 0.7 + seconds));
            l.Add(L("[Win32App] Downloaded file time " + ms, 0.7 + seconds));
            l.Add(L("[Win32App][ReportingManager] Download state for app with id: " + id + " has been updated. Report delta: {\"EnforcementState\":{\"OldValue\":\"InProgress\",\"NewValue\":\"InProgressDownloadCompleted\"}}", 0.8 + seconds));
            return l;
        }

        static Session Feed(params List<LogLine>[] apps)
        {
            ImeLogParser p = new ImeLogParser("AppWorkload");
            List<Obs> o = new List<Obs>();
            foreach (List<LogLine> lines in apps) foreach (LogLine l in lines) p.Read(l, o);
            Session s = new Session();
            new Tracker(s).ApplyAll(o);
            return s;
        }

        const string AppA = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", AppB = "1b2c3d4e-5f6a-4b7c-9d8e-0f1a2b3c4d5e", AppC = "2c3d4e5f-6a7b-4c8d-ae9f-1a2b3c4d5e6f";

        static void Parsing()
        {
            Session s = Feed(Lines(AppA, "Contoso Agent", 2097152, 4, false));
            Item i = s.Find(IntuneKeys.App(AppA));
            T.Check(i != null, "download lines make the app item");
            if (i == null) return;
            T.Eq("2 MB", i.Get("Download size"), "download size");
            T.Eq("4 s", i.Get("Download time"), "download time from the agent's milliseconds");
            T.Eq("4.2 Mbit/s", i.Get("Download speed"), "average download speed");
            T.Eq("Delivery Optimization", i.Get("Downloaded with"), "downloaded with Delivery Optimization");
            T.Eq(null, i.Get("From peers"), "no peer bytes when no line names them");
            T.Check(i.Events.Exists(delegate (TEvent e) { return e.Text == "Downloaded 50 % of 2 MB"; }), "progress in bytes");
            T.Check(i.Events.Exists(delegate (TEvent e) { return e.Text == "Downloaded 2 MB in 4 s, 4.2 Mbit/s"; }), "download summary on the timeline");
            T.Eq(State.Installing, i.State, "download completed, install next");
            T.Eq(1, i.Attempts.Count, "one attempt for one download");

            Session p = Feed(Lines(AppB, "Contoso Suite", 104857600, 60, true));
            Item b = p.Find(IntuneKeys.App(AppB));
            T.Eq("75 MB (75 %)", b != null ? b.Get("From peers") : null, "bytes from peers when the log names them");
            T.Eq("25 MB (25 %)", b != null ? b.Get("From HTTP") : null, "bytes from HTTP when the log names them");
            T.Eq(60.0, Downloads.Seconds(b), "seconds read back");
            T.Eq(3840.0, Downloads.Seconds(new Item { Info = new List<Pair> { new Pair("Download time", "1 h 04 min") } }), "hours read back");
        }

        static void SlowFinding()
        {
            // 300 MB in 10 minutes is 4.2 Mbit/s: slow. 300 MB in 4 minutes is not long enough. 700 MB in 6 minutes is fast enough.
            Session s = Feed(Lines(AppA, "Contoso Big", 314572800, 600, false), Lines(AppB, "Contoso Short", 104857600, 240, false), Lines(AppC, "Contoso Fast", 734003200, 360, false));
            Finder.Run(s, T0.AddHours(1), false);
            List<Finding> slow = s.Findings.FindAll(delegate (Finding f) { return f.Kind == FindingKinds.SlowDownload; });
            T.Eq(1, slow.Count, "one slow download");
            if (slow.Count == 0) return;
            T.Eq(IntuneKeys.App(AppA), slow[0].ItemKey, "the slow app");
            T.Check(slow[0].Title.Contains("4.2 Mbit/s"), "speed in the title");
            T.Check(slow[0].Message.Contains("10 min 00 s") && slow[0].Message.Contains("300 MB"), "time and size in the message");
            T.Check(slow[0].Hint.Contains("Delivery Optimization") && slow[0].Hint.Contains("proxy"), "hint about network, proxy and Delivery Optimization");
            T.Check(slow[0].Evidence != null && slow[0].Evidence.Text.Contains("Downloaded file time"), "evidence is the agent's line");
        }

        static void Fleet()
        {
            Session a = Feed(Lines(AppA, "Contoso Agent", 104857600, 100, false));
            Session b = Feed(Lines(AppA, "Contoso Agent", 104857600, 300, false));
            a.Computer = "CONTOSO-PC01";
            b.Computer = "CONTOSO-PC02";
            FleetStats st = FleetStats.Build(new Session[] { a, b }, T0.AddHours(1));
            FleetItem f = st.Items.Find(delegate (FleetItem x) { return x.Kind == Kinds.Win32App; });
            T.Check(f != null && f.Downloads == 2, "both downloads counted");
            T.Eq(200.0, f != null ? Math.Round(f.AvgDownloadSeconds) : 0, "average download seconds");
            T.Check(f != null && Math.Abs(f.AvgDownloadMbit - (8.4 + 2.8) / 2) < 0.2, "average download speed");

            Session r = new Session();
            new Tracker(r).ApplyAll(new RestartWatch().Add(new SysEvent[] { E("Microsoft-Windows-Kernel-General", 13, 10), E("Microsoft-Windows-Kernel-General", 12, 60) }));
            FleetStats rs = FleetStats.Build(new Session[] { r }, T0.AddHours(1));
            T.Eq(0, rs.Items.Count, "restarts are not counted as items in Statistics");
        }
    }
}
