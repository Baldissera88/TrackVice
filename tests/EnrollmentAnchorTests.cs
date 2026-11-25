namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The deployment began 2026-11-25 09:30. One app carries a time from the image it was installed on, six months before.
    public static class EnrollmentAnchorTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 25, 9, 30, 0, DateTimeKind.Utc);
        static readonly DateTime May = new DateTime(2026, 5, 18, 10, 46, 49, DateTimeKind.Utc);

        public static void Run()
        {
            StartIgnoresImageTime();
            ObservationsIgnoreImageTime();
            AnchorIsStable();
            OverallIsTimeWeighted();
            OverallNeverFullWhileRunning();
            OverallUsesHistory();
            OverallDoneIsFull();
            TimeLeftNotBelowRunning();
            ReplacedIsNotSilent();
            NewerIsNotReplaced();
            ManyReinstallsCountOnce();
        }

        static Item Add(Session s, string key, string name, string kind, string phase, State st, DateTime start, DateTime? end)
        {
            Item i = new Item();
            i.Key = key;
            i.Name = name;
            i.Kind = kind;
            i.PhaseKey = phase;
            i.State = st;
            i.StartUtc = start;
            i.FirstUtc = start;
            i.EndUtc = end;
            i.LastUtc = end ?? start;
            i.Order = s.Items.Count;
            s.Items.Add(i);
            return i;
        }

        static Session Fake()
        {
            Session s = new Session();
            s.Computer = "MANSOFT-62132";
            foreach (string k in new string[] { Phases.DeviceSetup, Phases.IntuneApps, Phases.Policies })
            {
                Phase p = new Phase();
                p.Key = k;
                p.Name = Phases.Name(k);
                p.Order = Phases.Order(k);
                s.Phases.Add(p);
            }
            Add(s, "esp:device", "Device setup", Kinds.Esp, Phases.DeviceSetup, State.Succeeded, T0, T0.AddMinutes(20));
            Add(s, "p1", "Policy one", Kinds.Policy, Phases.Policies, State.Succeeded, T0.AddMinutes(1), T0.AddMinutes(1));
            Add(s, "a1", "Contoso VPN", Kinds.Win32App, Phases.IntuneApps, State.Succeeded, T0.AddMinutes(5), T0.AddMinutes(9));
            // The Office key came from the template, written in May; the install itself ran today.
            Add(s, "a2", "Microsoft 365 Apps", Kinds.Win32App, Phases.IntuneApps, State.Succeeded, May, T0.AddMinutes(25));
            TEvent old = new TEvent();
            old.TimeUtc = May;
            old.ItemKey = "a2";
            old.Text = "IME registry: installed";
            TEvent real = new TEvent();
            real.TimeUtc = T0.AddMinutes(10);
            real.ItemKey = "a2";
            real.Text = "Installing";
            s.Events.Add(old);
            s.Events.Add(real);
            Add(s, "a3", "Teams", Kinds.Win32App, Phases.IntuneApps, State.Installing, T0.AddMinutes(26), null);
            s.ClockUtc = T0.AddMinutes(27);
            s.Rebuild();
            return s;
        }

        static void StartIgnoresImageTime()
        {
            Session s = Fake();
            DateTime now = T0.AddMinutes(27);
            T.Eq(T0, s.FirstUtc.Value, "anchor: the session starts at the enrollment, not at the image time");
            Phase apps = s.FindPhase(Phases.IntuneApps);
            T.Eq(T0.AddMinutes(5), apps.StartUtc.Value, "anchor: the Apps phase starts with its first real app");
            TimeSpan d = apps.Duration(now).Value;
            T.Check(d < TimeSpan.FromHours(1) && d > TimeSpan.FromMinutes(10), "anchor: the Apps phase lasts minutes, not months (" + d + ")");
            TimeSpan total = now - s.FirstUtc.Value;
            T.Check(total == TimeSpan.FromMinutes(27), "anchor: the overall time is minutes");
            Item o = s.Find("a2");
            T.Eq(T0.AddMinutes(10), o.StartUtc.Value, "anchor: the app starts at its first event of this deployment");
            T.Eq(TimeSpan.FromMinutes(15), o.Duration(now).Value, "anchor: the app duration is minutes");
            T.Check(o.Get(EnrollmentAnchor.OlderInfo) != null && o.Get(EnrollmentAnchor.OlderInfo).Contains("2026"), "anchor: the recorded time stays in the details");
            T.Check(o.Events.Exists(delegate (TEvent e) { return e.TimeUtc == May; }), "anchor: the event keeps its own time");
            T.Check(s.Notes.Exists(delegate (string n) { return n.Contains("before this deployment began"); }), "anchor: a note says why");
            FleetRun run = FleetStats.RunOf(s, now);
            T.Check(run.Seconds < 3600, "anchor: the fleet run lasts minutes");
        }

        static void ObservationsIgnoreImageTime()
        {
            List<Obs> list = new List<Obs>();
            Obs esp = new Obs();
            esp.TimeUtc = T0;
            esp.ItemKey = "esp:device";
            esp.ItemKind = Kinds.Esp;
            esp.PhaseKey = Phases.DeviceSetup;
            esp.State = State.Installing;
            esp.Text = "ESP";
            list.Add(esp);
            Obs app = new Obs();
            app.TimeUtc = May;
            app.ItemKey = "a2";
            app.ItemKind = Kinds.Win32App;
            app.PhaseKey = Phases.IntuneApps;
            app.State = State.Installing;
            app.Text = "IME registry";
            list.Add(app);
            Session s = new Session();
            Tracker t = new Tracker(s);
            t.ApplyAll(list);
            T.Eq(T0, s.FirstUtc.Value, "anchor: applying observations starts at the enrollment");
            DateTime? floor = EnrollmentAnchor.Of(list, T0.AddDays(-300), T0.AddMinutes(5));
            T.Eq(T0, floor.Value, "anchor: the restart floor is not pulled back by an app");
        }

        static void AnchorIsStable()
        {
            Session s = Fake();
            T.Eq(0, EnrollmentAnchor.Apply(s), "anchor: running it again changes nothing");
            T.Eq(T0, s.FirstUtc.Value, "anchor: the start stays");
            Session json = Session.FromJson(s.ToJson());
            T.Eq(T0, json.FirstUtc.Value, "anchor: the start survives an upload");
        }

        static Session Big(State office, int officeMin, bool lastDone)
        {
            Session s = new Session();
            s.Computer = "BIG";
            Phase p = new Phase();
            p.Key = Phases.IntuneApps;
            p.Name = "Apps";
            s.Phases.Add(p);
            for (int k = 0; k < 30; k++) Add(s, "p" + k, "Policy " + k, Kinds.Policy, Phases.IntuneApps, State.Succeeded, T0.AddSeconds(k * 5), T0.AddSeconds(k * 5 + 1));
            for (int k = 0; k < 13; k++) Add(s, "w" + k, "App " + k, Kinds.Win32App, Phases.IntuneApps, State.Succeeded, T0.AddMinutes(k * 4), T0.AddMinutes(k * 4 + 3));
            Add(s, "off", "Microsoft 365 Apps", Kinds.Win32App, Phases.IntuneApps, office, T0.AddMinutes(53), States.IsFinal(office) ? (DateTime?)T0.AddMinutes(53 + officeMin) : null);
            Add(s, "last", "Last app", Kinds.Win32App, Phases.IntuneApps, lastDone ? State.Succeeded : State.Pending, T0.AddMinutes(80), lastDone ? (DateTime?)T0.AddMinutes(83) : null);
            s.Rebuild();
            return s;
        }

        static void OverallIsTimeWeighted()
        {
            Session s = Big(State.Installing, 0, false);
            T.Eq(45, s.Items.Count, "overall: 45 items");
            T.Eq(43, s.CountComplete, "overall: 43 done");
            double pct = OverallProgress.Percent(s, T0.AddMinutes(56), null);
            T.Check(pct < 90, "overall: 43 of 45 with Office 3 min into about 20 is clearly below 90 (" + pct + ")");
            T.Check(pct > 50, "overall: but not absurdly low (" + pct + ")");
            T.Check(s.FindPhase(Phases.IntuneApps).Progress > 0.95, "overall: the phase bar stays count based");
        }

        static void OverallNeverFullWhileRunning()
        {
            Session s = Big(State.Installing, 0, true);
            double late = OverallProgress.Fraction(s, T0.AddHours(5), null);
            T.Check(late <= 0.99 && late >= 0.9, "overall: a running item stops at 99 at most (" + late + ")");
            T.Check(OverallProgress.Percent(s, T0.AddHours(5), null) <= 99, "overall: shown as 99 at most");
            Session waiting = Big(State.Succeeded, 10, false);
            T.Check(OverallProgress.Fraction(waiting, T0.AddHours(2), null) <= 0.99, "overall: a waiting item keeps it below 100");
        }

        static void OverallUsesHistory()
        {
            Session s = Big(State.Installing, 0, false);
            Session other = new Session();
            other.Computer = "OTHER";
            Add(other, "off", "Microsoft 365 Apps", Kinds.Win32App, Phases.IntuneApps, State.Succeeded, T0, T0.AddMinutes(23));
            ExpectedTimes h = ExpectedTimes.From(new Session[] { other, s }, "BIG", null, T0.AddHours(1));
            double a = OverallProgress.Fraction(s, T0.AddMinutes(56), null), b = OverallProgress.Fraction(s, T0.AddMinutes(56), h);
            T.Check(b < a, "overall: a history of 23 min for Office lowers the share (" + a + " to " + b + ")");
            T.Eq(23 * 60.0, OverallProgress.Expected(s.Find("off"), h), "overall: the expected time is the average of the other devices");
            T.Eq(OverallProgress.OfficeSeconds, OverallProgress.Expected(s.Find("off"), null), "overall: Office has its own default");
            T.Eq(5.0, OverallProgress.Expected(s.Find("p1"), null), "overall: a policy takes seconds");
        }

        static void OverallDoneIsFull()
        {
            Session s = Big(State.Succeeded, 10, true);
            T.Eq(100.0, OverallProgress.Percent(s, T0.AddHours(2), null), "overall: everything done is 100");
            Item f = s.Find("last");
            f.State = State.Failed;
            T.Check(OverallProgress.Fraction(s, T0.AddHours(2), null) <= 0.99, "overall: a failure is not a finished deployment");
        }

        static void TimeLeftNotBelowRunning()
        {
            Session s = Big(State.Installing, 0, false);
            TimeLeftEstimate e = new TimeLeftEstimate();
            e.Left = TimeSpan.FromMinutes(2);
            e.Basis = "x";
            OverallProgress.Adjust(e, s, T0.AddMinutes(56), null);
            T.Check(e.Left >= TimeSpan.FromMinutes(16), "overall: the time left is not less than the running Office needs (" + e.Left + ")");
        }

        static OverviewDevice Dev(string device, State st, int startMinAgo, int lastMinAgo, string text)
        {
            DateTime now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
            OverviewDevice o = new OverviewDevice();
            o.Customer = "Mansoft";
            o.Kind = OverviewKinds.Agents;
            RemoteDeployment d = new RemoteDeployment();
            d.Key = "sessions/" + device + startMinAgo + ".json.gz";
            d.Device = device;
            d.State = st;
            d.StateText = text;
            d.StartUtc = now.AddMinutes(-startMinAgo);
            d.LastUtc = now.AddMinutes(-lastMinAgo);
            o.D = d;
            return o;
        }

        static AttentionTile Tile(OverviewData d, string key)
        {
            foreach (AttentionTile t in d.Attention) if (t.Key == key) return t;
            return null;
        }

        static void ReplacedIsNotSilent()
        {
            DateTime now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
            OverviewSnapshot snap = new OverviewSnapshot();
            snap.Agents = true;
            OverviewDevice old = Dev("MANSOFT-62132", State.Unknown, 24 * 60, 600, "No upload for 10 h · 2 of 3 done");
            OverviewDevice fresh = Dev("MANSOFT-62132", State.Installing, 40, 0, "5 of 20 done");
            OverviewDevice renamed = Dev("MANSOFT-OLDNAME", State.Unknown, 30 * 60, 700, "No upload for 11 h · 1 of 3 done");
            OverviewDevice renamedNew = Dev("MANSOFT-NEWNAME", State.Installing, 20, 0, "2 of 9 done");
            renamedNew.D.Serial = "MANSOFT-OLDNAME";
            OverviewDevice other = Dev("OTHER-PC", State.Unknown, 24 * 60, 600, "No upload for 10 h · 2 of 3 done");
            snap.Devices.AddRange(new OverviewDevice[] { old, fresh, renamed, renamedNew, other });
            OverviewData d = OverviewModel.Build(snap, new OverviewFilter(), now, now.AddHours(-12), null);
            AttentionTile silent = Tile(d, "silent");
            T.Check(silent != null && silent.Count == 1 && silent.Devices[0].D.Device == "OTHER-PC", "replaced: only the device with no newer deployment stopped sending");
            T.Check(old.Replaced && !fresh.Replaced && renamed.Replaced && !other.Replaced, "replaced: marked on the old ones");
            T.Check(old.D.StateText.StartsWith(OverviewModel.ReplacedText, StringComparison.Ordinal) && old.D.StateText.Contains("2 of 3 done"), "replaced: the list says so (" + old.D.StateText + ")");
            T.Check(!old.Running(now) && !old.Finished(now), "replaced: neither running nor finished");
        }

        static void NewerIsNotReplaced()
        {
            OverviewSnapshot snap = new OverviewSnapshot();
            snap.Agents = true;
            OverviewDevice a = Dev("PC-1", State.Unknown, 60, 50, "No upload for 50 min · 1 of 3 done");
            OverviewDevice done = Dev("PC-1", State.Succeeded, 300, 250, "Done, 3 items");
            done.D.EndUtc = done.D.LastUtc;
            snap.Devices.Add(a);
            snap.Devices.Add(done);
            OverviewModel.MarkReplaced(snap.Devices);
            T.Check(!a.Replaced && !done.Replaced, "replaced: an older finished deployment does not replace a newer one, and a finished one is left alone");
            T.Check(!a.Superseded && done.Superseded, "superseded: the older finished deployment is history");
        }

        static void ManyReinstallsCountOnce()
        {
            DateTime now = new DateTime(2026, 11, 25, 21, 0, 0, DateTimeKind.Utc);
            OverviewSnapshot snap = new OverviewSnapshot();
            snap.Agents = true;
            OverviewDevice failedOld = Dev("MANSOFT-62132", State.Failed, 300, 72, "No upload for 1 h 12 min · 1 failed");
            failedOld.D.EndUtc = failedOld.D.LastUtc;
            OverviewDevice doneOld = Dev("MANSOFT-62132", State.Succeeded, 200, 55, "No upload for 55 min · Done, 58 items");
            doneOld.D.EndUtc = doneOld.D.LastUtc;
            OverviewDevice stopped = Dev("MANSOFT-62132", State.Unknown, 100, 13, "No upload for 13 min · 20 of 64 done");
            OverviewDevice live = Dev("MANSOFT-62132", State.Installing, 30, 0, "56 of 64 done");
            OverviewDevice doneElsewhere = Dev("PC-9", State.Succeeded, 300, 200, "No upload for 3 h · Done, 40 items");
            doneElsewhere.D.EndUtc = doneElsewhere.D.LastUtc;
            snap.Devices.AddRange(new OverviewDevice[] { failedOld, doneOld, stopped, live, doneElsewhere });
            OverviewData d = OverviewModel.Build(snap, new OverviewFilter(), now, now.AddHours(-12), null);
            T.Check(Tile(d, "silent") == null, "reinstalls: no agent counts as stopped (old ones are history, a finished one is not in the middle)");
            T.Check(Tile(d, "failed") == null, "reinstalls: a failure replaced by a newer deployment of the device is not a failed device");
            T.Check(failedOld.Superseded && doneOld.Superseded && stopped.Superseded && stopped.Replaced && !live.Superseded && !doneElsewhere.Superseded, "reinstalls: every older deployment of the device is superseded");
        }
    }
}
