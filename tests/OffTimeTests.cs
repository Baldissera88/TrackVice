namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // A deployment stopped by a shutdown in the evening and started again the next morning: started 00:43,
    // shut down 00:56, started 09:57, done 10:11. Times are UTC, so names read the same on any machine.
    public static class OffTimeTests
    {
        static readonly DateTime D = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        const string Vpn = "3b0d6e21-1111-4a2b-9c3d-0e1f2a3b4c01", Office = "3b0d6e21-2222-4a2b-9c3d-0e1f2a3b4c02", Portal = "3b0d6e21-3333-4a2b-9c3d-0e1f2a3b4c03";
        static long rec;

        static DateTime At(int h, int m, int s)
        {
            return D.AddHours(h).AddMinutes(m).AddSeconds(s);
        }

        static SysEvent E(string provider, int id, DateTime t, params object[] props)
        {
            SysEvent e = new SysEvent();
            e.Provider = provider;
            e.Id = id;
            e.TimeUtc = t;
            e.Record = ++rec;
            e.Props.AddRange(props);
            return e;
        }

        static Obs App(string id, string name, State st, DateTime t)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "ESP";
            o.ItemKey = IntuneKeys.App(id);
            o.ItemName = name;
            o.ItemKind = Kinds.Win32App;
            o.PhaseKey = Phases.DeviceSetup;
            o.State = st;
            o.Text = States.Label(st);
            return o;
        }

        static List<SysEvent> Shutdown()
        {
            return new List<SysEvent>
            {
                E("User32", 1074, At(0, 56, 2), @"C:\Windows\explorer.exe (CONTOSO-PC01)", "CONTOSO-PC01", "Other (Unplanned)", "0x0", "power off", "", @"CONTOSO\adele"),
                E("Microsoft-Windows-Kernel-General", 13, At(0, 56, 10)),
            };
        }

        static List<SysEvent> Start()
        {
            return new List<SysEvent> { E("Microsoft-Windows-Kernel-General", 12, At(9, 57, 20)), E("EventLog", 6005, At(9, 57, 35)) };
        }

        // Before the shutdown only, as the last upload before the device went off has it.
        public static Session Evening()
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            Tracker t = new Tracker(s);
            RestartWatch w = new RestartWatch();
            w.Zone = TimeZoneInfo.Utc;
            t.ApplyAll(new List<Obs>
            {
                App(Vpn, "Contoso VPN Client", State.Installing, At(0, 43, 30)),
                App(Vpn, "Contoso VPN Client", State.Succeeded, At(0, 49, 0)),
                App(Office, "Microsoft 365 Apps", State.Installing, At(0, 52, 0)),
                App(Portal, "Company Portal", State.Pending, At(0, 43, 30)),
            });
            t.ApplyAll(w.Add(Shutdown()));
            s.SavedUtc = At(0, 56, 9);
            s.ClockUtc = At(0, 56, 9);
            Finder.Run(s, At(0, 56, 9), true);
            return s;
        }

        // The whole deployment, read after the start in the morning.
        public static Session Whole()
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            Tracker t = new Tracker(s);
            RestartWatch w = new RestartWatch();
            w.Zone = TimeZoneInfo.Utc;
            List<SysEvent> all = Shutdown();
            all.AddRange(Start());
            t.ApplyAll(new List<Obs>
            {
                App(Vpn, "Contoso VPN Client", State.Installing, At(0, 43, 30)),
                App(Vpn, "Contoso VPN Client", State.Succeeded, At(0, 49, 0)),
                App(Office, "Microsoft 365 Apps", State.Installing, At(0, 52, 0)),
                App(Office, "Microsoft 365 Apps", State.Succeeded, At(10, 5, 0)),
                App(Portal, "Company Portal", State.Installing, At(10, 5, 30)),
                App(Portal, "Company Portal", State.Succeeded, At(10, 11, 0)),
            });
            t.ApplyAll(w.Add(all));
            s.ClockUtc = At(10, 11, 0);
            s.SavedUtc = At(10, 11, 0);
            Finder.Run(s, At(10, 11, 0), true);
            return s;
        }

        public static void Run()
        {
            Sums();
            Header();
            RestartItem();
            Elsewhere();
            Gantt();
            Rules();
            Lists();
        }

        static Item RestartOf(Session s)
        {
            return s.Items.Find(delegate (Item i) { return i.Kind == Kinds.Restart; });
        }

        static void Sums()
        {
            Session s = Whole();
            DateTime first = s.FirstUtc.Value, end = At(10, 11, 0);
            T.Eq(At(0, 43, 30), first, "Off time: the deployment starts 00:43:30");
            T.Eq(TimeSpan.FromSeconds(9 * 3600 + 70), OffTime.Off(s, first, end), "Off time: off from 00:56:10 to 09:57:20");
            T.Eq(TimeSpan.FromSeconds(26 * 60 + 20), OffTime.Active(s, first, end), "Off time: 26 min 20 s of deployment time");
            Phase rp = s.Phases.Find(delegate (Phase x) { return x.Key == Phases.Restarts; });
            T.Eq(RestartOf(s).Duration(end), rp.Duration(end).Value, "Off time: the Restarts phase takes the shutdown and the start, not the night");
            foreach (GanttRow gr in GanttLayout.Rows(s, end, true, false))
                if (gr.Item == null ? gr.Phase == rp : gr.Item == RestartOf(s)) T.Eq(RestartOf(s).Duration(end), gr.Duration, "Off time: the Gantt row of the restart shows its own time");
            Item office = s.Find(IntuneKeys.App(Office));
            T.Eq(TimeSpan.FromSeconds(11 * 60 + 50), office.Duration(end), "Off time: an app that ran across the night took 11 min 50 s");
            foreach (GanttRow gr in GanttLayout.Rows(s, end, true, false))
                if (gr.Item == office) T.Eq(TimeSpan.FromSeconds(11 * 60 + 50), gr.Duration, "Off time: the Gantt bar label leaves the night out");
            Phase p = s.FindPhase(Phases.DeviceSetup);
            T.Eq(TimeSpan.FromSeconds(26 * 60 + 20), p.Duration(end), "Off time: the phase time leaves the night out");
            // A restart of a few minutes is not time off.
            Session quick = new Session();
            RestartWatch w = new RestartWatch();
            w.Zone = TimeZoneInfo.Utc;
            new Tracker(quick).ApplyAll(w.Add(new SysEvent[] { E("Microsoft-Windows-Kernel-General", 13, At(1, 0, 0)), E("Microsoft-Windows-Kernel-General", 12, At(1, 2, 0)) }));
            OffTime.Mark(quick);
            T.Eq(TimeSpan.Zero, OffTime.Off(quick, At(0, 0, 0), At(2, 0, 0)), "Off time: a 2 minute restart is not time off");
            T.Eq(TimeSpan.FromMinutes(2), RestartOf(quick).Duration(At(2, 0, 0)), "Off time: a short restart keeps its downtime as its time");
            // A saved session read again keeps the same sums.
            Session again = Session.FromJson(s.ToJson());
            T.Eq(TimeSpan.FromSeconds(11 * 60 + 50), again.Find(IntuneKeys.App(Office)).Duration(end), "Off time: a loaded session leaves the night out too");
        }

        static void Header()
        {
            Session s = Whole();
            T.Eq("26 min · off 9 h 01 min", OffTime.Elapsed(s, At(10, 11, 0)), "Off time: the header shows the active time and the time off");
            Session e = Evening();
            T.Eq("12 min", OffTime.Elapsed(e, At(0, 56, 9)), "Off time: no time off before the shutdown");
        }

        static void RestartItem()
        {
            Session s = Whole();
            Item r = RestartOf(s);
            T.Eq("Off from 00:56 to 09:57 (9 h 01 min)", r != null ? r.Name : null, "Off time: the restart item says when the device was off");
            T.Eq(TimeSpan.FromSeconds(23), r.Duration(At(10, 11, 0)), "Off time: its own time is the shutdown (8 s) and the start (15 s)");
            T.Eq("9 h 01 min", r.Get("Time off"), "Off time: the details say how long it was off");
            T.Check((r.Get("Shutdown and start") ?? "").StartsWith("23 s"), "Off time: the details give the shutdown and start");
            List<GanttTick> marks = GanttLayout.RestartMarks(s, TimeZoneInfo.Utc);
            T.Eq("Off 00:56", marks.Count == 1 ? marks[0].Label : null, "Off time: the Gantt mark says Off");
            Session e = Evening();
            T.Eq("Restart at 00:56", RestartOf(e).Name, "Off time: before the start it is still a restart");
        }

        static void Elsewhere()
        {
            Session s = Whole();
            DateTime end = At(10, 11, 0);
            FleetRun run = FleetStats.RunOf(s, end);
            T.Eq(1580.0, run.Seconds, "Off time: Statistics counts 26 min 20 s");
            FleetStats st = FleetStats.Build(new Session[] { s }, end);
            T.Eq(1580.0, st.AvgSeconds, "Off time: the average of finished runs leaves the night out");
            T.Eq(TimeSpan.FromSeconds(1580), SessionDiff.Total(s, end), "Off time: Compare's total leaves the night out");
            Reference r = Reference.From(s, end, "Contoso", "test");
            ReferenceItem portal = r.Items.Find(delegate (ReferenceItem x) { return x.Key == IntuneKeys.App(Portal); });
            T.Eq(12 * 60 + 40 + 8 * 60 + 10.0, portal.Offset, "Off time: the reference's start offset leaves the night out");
            string html = Report.Html(s, end);
            T.Check(html.Contains("after 26 min 20 s"), "Off time: the report says how long it took without the night");
            T.Check(html.Contains("off for 9 h 01 min"), "Off time: the report says how long the device was off");
            T.Check(DataExport.ItemsCsv(new Session[] { s }, null, end).Contains(",710,") || DataExport.ItemsCsv(new Session[] { s }, null, end).Contains(",710.0"), "Off time: the data export's duration leaves the night out");
        }

        static void Gantt()
        {
            Session s = Whole();
            DateTime now = At(10, 11, 0);
            List<GanttRow> rows = GanttLayout.Rows(s, now, false, false);
            DateTime from, to;
            GanttLayout.Range(rows, now, false, out from, out to);
            GanttAxis open = GanttAxis.For(s, from, to, now, false);
            T.Eq(1, open.Bands.Count, "Gantt: one band of time off");
            T.Eq(At(0, 56, 10), open.Bands[0].FromUtc, "Gantt: the band starts at the stop");
            T.Eq(At(9, 57, 20), open.Bands[0].ToUtc, "Gantt: the band ends at the start");
            T.Eq("Off 9 h 01 min", GanttAxis.Label(open.Bands[0]), "Gantt: the band's label");
            double wide = open.At(At(9, 57, 20)) - open.At(At(0, 56, 10));
            T.Check(wide > 0.9, "Gantt: not collapsed, the night takes most of the chart");
            GanttAxis shut = GanttAxis.For(s, from, to, now, true);
            double narrow = shut.At(At(9, 57, 20)) - shut.At(At(0, 56, 10));
            T.Check(Math.Abs(narrow - GanttAxis.BandShare) < 1e-6, "Gantt: collapsed, the night is a narrow band");
            T.Check(Math.Abs(shut.At(to) - 1) < 1e-6 && Math.Abs(shut.At(from)) < 1e-6, "Gantt: collapsed, the chart still runs from 0 to 1");
            T.Check(shut.At(At(0, 50, 0)) < shut.At(At(0, 52, 0)) && shut.At(At(10, 0, 0)) < shut.At(At(10, 5, 0)), "Gantt: collapsed, time still runs left to right");
            foreach (GanttTick k in shut.Ticks(800, 80, TimeZoneInfo.Utc)) T.Check(!shut.InBand(k.Utc), "Gantt: no tick inside a collapsed band (" + k.Label + ")");
            Session e = Evening();
            T.Eq(0, GanttAxis.For(e, At(0, 40, 0), At(1, 0, 0), At(1, 0, 0), true).Bands.Count, "Gantt: no band before the device was off for 15 min");
        }

        static void Rules()
        {
            // Morning, just after the start: the app that was installing at the shutdown has said nothing for 9 hours, but 9 of them the device was off.
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            Tracker t = new Tracker(s);
            RestartWatch w = new RestartWatch();
            w.Zone = TimeZoneInfo.Utc;
            List<SysEvent> all = Shutdown();
            all.AddRange(Start());
            t.ApplyAll(new List<Obs> { App(Office, "Microsoft 365 Apps", State.Installing, At(0, 52, 0)) });
            t.ApplyAll(w.Add(all));
            Finder.Run(s, At(9, 59, 0), true);
            T.Check(!s.Findings.Exists(delegate (Finding f) { return f.Kind == FindingKinds.Stuck; }), "Rules: no No progress finding for the time the device was off");
            Finder.Run(s, At(10, 20, 0), true);
            T.Check(s.Findings.Exists(delegate (Finding f) { return f.Kind == FindingKinds.Stuck && f.ItemKey == IntuneKeys.App(Office); }), "Rules: No progress again 15 min after the start");

            // The agent's alerts: nothing is sent for the night, and the finish says 26 min.
            Session whole = Whole();
            AlertWatch aw = new AlertWatch(null);
            aw.Check(Evening(), false, At(0, 56, 9));
            List<Alert> sent = aw.Check(whole, true, At(10, 41, 0));
            T.Eq(1, sent.Count, "Rules: one alert, the finish");
            T.Check(sent.Count == 1 && sent[0].Text.Contains("It took 26 min."), "Rules: the finish alert counts 26 min");

            // A device that shut down: off since 00:56, not stalled.
            Session e = Evening();
            T.Eq(At(0, 56, 10), OffTime.OffSince(e), "Rules: the device is off since the stop");
            T.Eq(null, OffTime.OffSince(whole), "Rules: not off after the start");
            e.Findings.Add(new Finding { Kind = FindingKinds.NoUploads, Severity = 1, Title = "CONTOSO-PC01 has stopped uploading" });
            OffTime.Silent(e, At(0, 56, 9), At(3, 0, 0));
            Finding n = e.Findings.Find(delegate (Finding f) { return f.Kind == FindingKinds.NoUploads; });
            T.Eq(0, n.Severity, "Rules: an agent whose device is off is not a warning");
            T.Check(n.Title.StartsWith("CONTOSO-PC01 is off since "), "Rules: the finding says the device is off");
            DateTime? told = null;
            T.Check(OffTime.ShuttingDown(e, ref told), "Rules: the agent uploads at once when the shutdown starts");
            T.Check(!OffTime.ShuttingDown(e, ref told), "Rules: and only once");
            // A crash is not a clean shutdown: no Off since.
            Session crash = new Session();
            RestartWatch cw = new RestartWatch();
            cw.Zone = TimeZoneInfo.Utc;
            new Tracker(crash).ApplyAll(cw.Add(new SysEvent[] { E("Microsoft-Windows-Kernel-General", 12, At(5, 0, 0)), E("Microsoft-Windows-Kernel-Power", 41, At(5, 0, 5), "0") }));
            T.Eq(null, OffTime.OffSince(crash), "Rules: a start after a crash is not off");

            // The agent is not removed in the morning for the hours the device was off.
            AgentLifetime life = new AgentLifetime(At(0, 40, 0), 2, 30);
            life.Check(Evening(), true, At(0, 56, 9));
            LifetimeDecision d = life.Check(whole, true, At(10, 11, 30));
            T.Check(!d.Remove, "Rules: the agent stays after a night off");
        }

        static void Lists()
        {
            Session e = Evening();
            Dictionary<string, string> meta = AgentData.Summary(e, At(0, 56, 9), false);
            T.Eq("2026-11-18T00:56:10Z", meta.ContainsKey(OffTime.MetaKey) ? meta[OffTime.MetaKey] : null, "Lists: the upload says when the device went off");
            StoreEntry se = new StoreEntry();
            se.Name = "sessions/contoso-pc01.json.gz";
            se.ModifiedUtc = At(0, 56, 9);
            foreach (KeyValuePair<string, string> kv in meta) if (kv.Value != null) se.Meta[kv.Key] = kv.Value;
            DateTime now = At(3, 0, 0);
            RemoteDeployment d = AgentData.ToDeployment(se, now);
            T.Check(d.StateText.StartsWith("Off since " + OffTime.At(At(0, 56, 10), now)), "Lists: the agent list says Off since (" + d.StateText + ")");
            T.Check(d.State != State.Installing && d.State != State.Unknown, "Lists: an off device is not running or stalled");
            OverviewDevice o = new OverviewDevice();
            o.Kind = OverviewKinds.Agents;
            o.D = d;
            o.S = e;
            T.Check(!o.Silent && !o.Running(now), "Lists: Overview counts it neither as stopped sending nor as deploying now");
            WallTile tile = WallModel.Tile(se, now);
            T.Check(tile.Line(now).StartsWith("Off since"), "Lists: the wall says Off since");
            Dictionary<string, string> m2 = AgentData.Summary(Whole(), At(10, 11, 0), false);
            T.Check(!m2.ContainsKey(OffTime.MetaKey), "Lists: no off time once Windows started again");
            T.Check(OffTime.ListText(At(0, 56, 10), At(0, 58, 0)).StartsWith("Off since "), "Lists: the words");
        }
    }
}
