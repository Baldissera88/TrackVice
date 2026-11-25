namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    public static class DeploymentEstimateTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 25, 9, 30, 0, DateTimeKind.Utc);

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

        static Session Make(string computer, string profile)
        {
            Session s = new Session();
            s.Computer = computer;
            foreach (string k in new string[] { Phases.DeviceSetup, Phases.IntuneApps, Phases.Restarts })
            {
                Phase p = new Phase();
                p.Key = k;
                p.Name = Phases.Name(k);
                p.Order = Phases.Order(k);
                s.Phases.Add(p);
            }
            if (profile != null) s.SetInfo("Autopilot profile", profile);
            return s;
        }

        // ESP 2 min, App A 5, a restart of 2, App B 10, Microsoft 365 Apps 20, all times scaled; 39 min at scale 1.
        static Session Done(string computer, DateTime t0, double scale, string profile)
        {
            Session s = Make(computer, profile);
            DateTime t = t0;
            Add(s, "esp", "Device setup", Kinds.Esp, Phases.DeviceSetup, State.Succeeded, t, t.AddMinutes(2 * scale));
            t = t.AddMinutes(2 * scale);
            Add(s, "a", "App A", Kinds.Win32App, Phases.IntuneApps, State.Succeeded, t, t.AddMinutes(5 * scale));
            t = t.AddMinutes(5 * scale);
            Add(s, "r", "Restart", Kinds.Restart, Phases.Restarts, State.Succeeded, t, t.AddMinutes(2));
            t = t.AddMinutes(2);
            Add(s, "b", "App B", Kinds.Win32App, Phases.IntuneApps, State.Succeeded, t, t.AddMinutes(10 * scale));
            t = t.AddMinutes(10 * scale);
            Add(s, "o", "Microsoft 365 Apps", Kinds.Win32App, Phases.IntuneApps, State.Succeeded, t, t.AddMinutes(20 * scale));
            s.Rebuild();
            return s;
        }

        // The ESP is done, App A runs since 2 minutes, the others wait or are not there yet.
        static Session Running(DateTime t0, bool all)
        {
            Session s = Make("NEW-PC", "Standard");
            Add(s, "esp", "Device setup", Kinds.Esp, Phases.DeviceSetup, State.Succeeded, t0, t0.AddMinutes(2));
            Add(s, "a", "App A", Kinds.Win32App, Phases.IntuneApps, State.Installing, t0.AddMinutes(2), null);
            if (all)
            {
                Add(s, "b", "App B", Kinds.Win32App, Phases.IntuneApps, State.Pending, t0.AddMinutes(2), null);
                Add(s, "o", "Microsoft 365 Apps", Kinds.Win32App, Phases.IntuneApps, State.Pending, t0.AddMinutes(2), null);
            }
            s.Rebuild();
            return s;
        }

        static DeploymentHistory Five()
        {
            DeploymentHistory h = new DeploymentHistory();
            double[] scales = { 0.9, 1.0, 1.0, 1.1, 1.2 };
            for (int k = 0; k < scales.Length; k++)
                T.Check(h.Learn(Done("OLD-" + k, T0.AddDays(-5 + k), scales[k], "Standard"), "Mansoft", T0), "history: finished deployment " + k + " is learned");
            return h;
        }

        static ExpectedTimes With(HistoryStats st)
        {
            ExpectedTimes e = new ExpectedTimes();
            e.History = st;
            return e;
        }

        public static void Run()
        {
            NoHistory();
            WithHistory();
            HiddenItems();
            Floor();
            LongerThanUsual();
            Outliers();
            Profiles();
            Persistence();
            Reference();
            Texts();
        }

        static void NoHistory()
        {
            Session s = Running(T0, true);
            DeploymentEstimate e = DeploymentEstimate.Make(s, T0.AddMinutes(4), TimeSpan.Zero, null);
            T.Check(e != null && e.Rough && !e.Over && !e.HasRange, "estimate: without history it is rough and has no range");
            // App A has 3 of its 5 default minutes left, App B 5 and Microsoft 365 Apps 20.
            T.Eq(28 * 60.0, e.Left.TotalSeconds, "estimate: without history the defaults per kind add up");
            T.Eq("about 30 min left", TimeLeft.Words(e.Left), "estimate: without history, the words");
            T.Check(e.Basis.StartsWith("No history yet"), "estimate: the basis says there is no history");
            T.Check(e.Variants()[0].EndsWith("rough"), "estimate: the text says rough");
            T.Check(DeploymentEstimate.Make(s, T0.AddSeconds(10), TimeSpan.Zero, null) == null, "estimate: nothing in the first seconds");
            T.Check(DeploymentEstimate.Make(Done("X", T0, 1, null), T0.AddHours(2), TimeSpan.Zero, null) == null, "estimate: nothing when it is done");
        }

        static void WithHistory()
        {
            DeploymentHistory h = Five();
            T.Eq(5, h.Count, "history: five deployments");
            HistoryStats st = h.Get("Mansoft", "Autopilot", "Standard");
            T.Eq(5, st.Count, "stats: based on five");
            T.Eq("Standard", st.Profile, "stats: of the profile");
            T.Eq(2340.0, st.Total, "stats: the middle total is the median, not the mean");
            T.Check(Math.Abs(st.Low - 2295.6) < 1 && Math.Abs(st.High - 2606.4) < 1, "stats: 20th and 80th percentile (" + st.Low + ", " + st.High + ")");
            T.Eq(1200.0, st.Items["Win32 app|Microsoft 365 Apps".Replace("Win32 app", Kinds.Win32App)], "stats: the middle time of an item");
            T.Eq(1.0, st.Restarts, "stats: one restart");
            T.Eq(120.0, st.RestartSeconds, "stats: a restart takes two minutes");
            T.Check(st.Phases.Count >= 2, "stats: phases are kept");

            Session s = Running(T0, true);
            ExpectedTimes exp = With(st);
            DateTime now = T0.AddMinutes(4);
            DeploymentEstimate e = DeploymentEstimate.Make(s, now, TimeSpan.Zero, exp);
            T.Check(e != null && !e.Rough && !e.Over, "estimate: with history it is not rough");
            T.Check(Math.Abs(e.Left.TotalSeconds - 2100) < 15, "estimate: the point is the known items and the restart still to come (" + e.Left.TotalSeconds + ")");
            T.Eq("about 35 min left", TimeLeft.Words(e.Left), "estimate: the words");
            T.Check(e.HasRange && e.Low < e.Left && e.High > e.Left, "estimate: a range from the spread around the point");
            T.Check(e.RangeText.Contains("–"), "estimate: the range text");
            T.Eq(5, e.Runs, "estimate: five deployments");
            T.Check(e.Basis.StartsWith("Based on 5 earlier deployments of Standard"), "estimate: the basis names the count and the profile (" + e.Basis + ")");
            string clock = "done ~" + now.AddSeconds(e.Left.TotalSeconds).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            T.Check(Math.Abs(e.FinishUtc.Subtract(now.AddSeconds(e.Left.TotalSeconds)).TotalSeconds) < 1, "estimate: the finish time is now plus the point");
            string[] v = e.Variants();
            T.Eq(3, v.Length, "estimate: three texts, from the most to the least");
            T.Eq("about 35 min left · " + e.RangeText + " · " + e.ClockText, v[0], "estimate: point, range and clock");
            T.Eq("about 35 min left · " + e.ClockText, v[1], "estimate: the range goes first");
            T.Eq("about 35 min left", v[2], "estimate: the clock goes last");
            T.Eq(clock, e.ClockText, "estimate: the clock text");
            T.Eq("~35 min left", e.Brief, "estimate: the short text for a list row");
            T.Eq(e.Text, v[0], "estimate: Text is the longest");

            // One or two earlier deployments tell the middle, not the spread.
            DeploymentHistory two = new DeploymentHistory();
            two.Learn(Done("A", T0.AddDays(-3), 1, "Standard"), "Mansoft", T0);
            two.Learn(Done("B", T0.AddDays(-2), 1.1, "Standard"), "Mansoft", T0);
            DeploymentEstimate e2 = DeploymentEstimate.Make(s, now, TimeSpan.Zero, With(two.Get("Mansoft", "Autopilot", "Standard")));
            T.Check(e2 != null && !e2.HasRange && e2.Basis.StartsWith("Based on 2 earlier deployments"), "estimate: two deployments give no range");
            DeploymentHistory one = new DeploymentHistory();
            one.Learn(Done("A", T0.AddDays(-3), 1, "Standard"), "Mansoft", T0);
            T.Check(DeploymentEstimate.Make(s, now, TimeSpan.Zero, With(one.Get("Mansoft", "Autopilot", "Standard"))).Basis.StartsWith("Based on 1 earlier deployment of"), "estimate: one deployment, singular");

            // The overall percentage uses the same history.
            double with = OverallProgress.Fraction(s, now, exp), without = OverallProgress.Fraction(s, now, null);
            T.Check(with > 0 && with < 0.2, "overall: the share agrees with the time left (" + with + ")");
            T.Check(Math.Abs(e.Left.TotalSeconds - st.Total * (1 - with)) < 15, "overall: percentage and time left come from the same total");
            T.Check(without != with, "overall: history changes the share");
        }

        static void HiddenItems()
        {
            HistoryStats st = Five().Get("Mansoft", "Autopilot", "Standard");
            Session s = Running(T0, false);
            DateTime now = T0.AddMinutes(4);
            DeploymentEstimate e = DeploymentEstimate.Make(s, now, TimeSpan.Zero, With(st));
            // The known items need 3 min and a restart; what is not visible yet is covered by the total of earlier deployments.
            T.Check(e.Left > TimeSpan.FromMinutes(30), "estimate: items not visible yet are covered by the history total (" + e.Left + ")");
            T.Check(Math.Abs(e.Left.TotalSeconds - st.Total * (1 - OverallProgress.Fraction(s, now, With(st)))) < 15, "estimate: the historical total times what is left");
            DeploymentEstimate rough = DeploymentEstimate.Make(s, now, TimeSpan.Zero, null);
            T.Check(rough.Left < TimeSpan.FromMinutes(5), "estimate: without history only the known item counts");
        }

        static void Floor()
        {
            Session s = Running(T0, false);
            DeploymentEstimate e = DeploymentEstimate.Make(s, T0.AddMinutes(12), TimeSpan.Zero, null);
            T.Eq(60.0, e.Left.TotalSeconds, "estimate: a running item past its expected time still leaves a minute");
            T.Eq("about 1 min left", TimeLeft.Words(e.Left), "estimate: about 1 min");
            T.Check(e.FinishUtc > T0.AddMinutes(12), "estimate: never a finish time that has passed");
        }

        static void LongerThanUsual()
        {
            HistoryStats st = Five().Get("Mansoft", "Autopilot", "Standard");
            Session s = Running(T0, true);
            DeploymentEstimate e = DeploymentEstimate.Make(s, T0.AddMinutes(60), TimeSpan.Zero, With(st));
            T.Check(e != null && e.Over, "estimate: past the high end it is over");
            T.Eq("taking longer than usual (usually 40 min)", e.Variants()[0], "estimate: taking longer than usual, with the usual time");
            T.Eq("longer than usual (usually 40 min)", e.Variants()[1], "estimate: shorter");
            T.Eq("longer than usual", e.Variants()[2], "estimate: shortest");
            T.Eq("longer than usual", e.Brief, "estimate: list row");
            DeploymentEstimate ok = DeploymentEstimate.Make(s, T0.AddMinutes(42), TimeSpan.Zero, With(st));
            T.Check(!ok.Over && ok.Left >= TimeSpan.FromMinutes(1), "estimate: between the middle and the high end it is still an estimate");
            T.Check(DeploymentEstimate.Make(s, T0.AddMinutes(60), TimeSpan.FromMinutes(30), With(st)).Over == false, "estimate: time not counted is left out");
        }

        static void Outliers()
        {
            Session failed = Done("F", T0, 1, "Standard");
            failed.Find("b").State = State.Failed;
            T.Check(DeploymentHistory.Measure(failed, "Mansoft", T0) == null, "outliers: a failed deployment is not learned");
            T.Check(DeploymentHistory.Measure(Running(T0, true), "Mansoft", T0) == null, "outliers: a running one is not learned");
            Session off = Done("O", T0, 1, "Standard");
            // The device was off for 3 hours in a restart.
            Item r = off.Find("r");
            r.EndUtc = r.StartUtc.Value.AddHours(3);
            r.LastUtc = r.EndUtc.Value;
            foreach (Item i in off.Items) if (i.Key == "b" || i.Key == "o") { i.StartUtc = i.StartUtc.Value.AddHours(3); i.EndUtc = i.EndUtc.Value.AddHours(3); i.FirstUtc = i.StartUtc.Value; i.LastUtc = i.EndUtc.Value; }
            off.Rebuild();
            T.Check(DeploymentHistory.Measure(off, "Mansoft", T0) == null, "outliers: a deployment with a long time off is not learned");
            T.Check(DeploymentHistory.Measure(Done("D", T0, 1, "Standard"), "Mansoft", T0) != null, "outliers: a normal one is");

            DeploymentHistory h = new DeploymentHistory();
            double[] totals = { 2300, 2340, 2340, 2400, 2500, 20000 };
            for (int k = 0; k < totals.Length; k++) h.Add(Run("r" + k, "Standard", totals[k], T0.AddDays(-k)));
            HistoryStats st = h.Get("Mansoft", "Autopilot", "Standard");
            T.Eq(5, st.Count, "outliers: a total of three times the middle is ignored");
            T.Check(st.High < 3000, "outliers: and does not stretch the high end (" + st.High + ")");
            T.Check(!h.Add(Run("r1", "Standard", 2340, T0)), "history: the same deployment is not added twice");

            DeploymentHistory many = new DeploymentHistory();
            for (int k = 0; k < 25; k++) many.Add(Run("m" + k, "Standard", 2000 + k, T0.AddDays(-30 + k)));
            T.Eq(20, many.Count, "history: the newest 20 per key are kept");
            T.Eq(20, many.Get("Mansoft", "Autopilot", "Standard").Count, "history: and used");
        }

        static HistoryRun Run(string id, string profile, double total, DateTime end)
        {
            HistoryRun r = new HistoryRun();
            r.Id = id;
            r.Customer = "Mansoft";
            r.Kind = "Autopilot";
            r.Profile = profile;
            r.EndUtc = end;
            r.Total = total;
            r.Restarts = 1;
            r.RestartSeconds = 100;
            r.Items.Add(new HistoryValue { Key = Kinds.Win32App + "|App A", Seconds = 300 });
            r.Phases.Add(new HistoryValue { Key = Phases.IntuneApps, Seconds = 900 });
            return r;
        }

        static void Profiles()
        {
            DeploymentHistory h = new DeploymentHistory();
            for (int k = 0; k < 3; k++) h.Add(Run("a" + k, "Standard", 2400, T0.AddDays(-k)));
            for (int k = 0; k < 2; k++) h.Add(Run("b" + k, "Kiosk", 1200, T0.AddDays(-k - 5)));
            HistoryStats a = h.Get("Mansoft", "Autopilot", "Standard");
            T.Check(a.Count == 3 && a.Profile == "Standard" && a.Total == 2400, "profiles: three of the profile are used alone");
            HistoryStats b = h.Get("Mansoft", "Autopilot", "Kiosk");
            T.Check(b.Count == 5 && b.Profile == null, "profiles: two are too few, the kind of the customer is used");
            T.Check(h.Get("Mansoft", "Autopilot", null).Count == 5, "profiles: no profile known, the kind");
            T.Check(h.Get("Other", "Autopilot", "Standard") == null, "profiles: another customer has no history");
            T.Check(h.Get("Mansoft", "Task sequence", null) == null, "profiles: another kind has no history");
        }

        static void Persistence()
        {
            DeploymentHistory h = Five();
            DeploymentHistory back = DeploymentHistory.FromJson(h.ToJson());
            T.Eq(5, back.Count, "persistence: five come back");
            HistoryStats a = h.Get("Mansoft", "Autopilot", "Standard"), b = back.Get("Mansoft", "Autopilot", "Standard");
            T.Check(a.Total == b.Total && a.Low == b.Low && a.High == b.High && a.Items.Count == b.Items.Count && a.Phases.Count == b.Phases.Count, "persistence: the same numbers");
            T.Check(!back.Dirty, "persistence: just loaded is not changed");
            string dir = Path.Combine(Path.GetTempPath(), "trackvice-history-" + Guid.NewGuid().ToString("N"));
            try
            {
                string file = Path.Combine(dir, "sub", "history.json");
                T.Eq(0, DeploymentHistory.Load(file).Count, "persistence: a missing file is an empty history");
                T.Check(h.Save(file), "persistence: saved, also into a new folder");
                T.Check(h.Save(file) && !File.Exists(file + ".tmp"), "persistence: saved again, no temporary file left");
                T.Eq(5, DeploymentHistory.Load(file).Count, "persistence: loaded");
                File.WriteAllText(file, "{ broken");
                T.Eq(0, DeploymentHistory.Load(file).Count, "persistence: a broken file is an empty history");
                File.WriteAllText(file, "{\"Kind\":\"something else\"}");
                T.Eq(0, DeploymentHistory.Load(file).Count, "persistence: another document is an empty history");
                T.Check(!new DeploymentHistory().Save(""), "persistence: no path, no save");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        static void Reference()
        {
            Session s = Done("REF", T0, 1, "Standard");
            Reference r = TrackVice.Reference.From(s, T0.AddHours(2), "Mansoft", "test");
            HistoryRun run = DeploymentHistory.Measure(r, "Mansoft");
            T.Check(run != null && run.Kind == "Autopilot" && run.Profile == "Standard", "reference: measured as a deployment of its profile");
            T.Check(run != null && Math.Abs(run.Total - 2340) < 5 && run.Restarts == 1, "reference: the total and the restart");
            DeploymentHistory h = new DeploymentHistory();
            T.Check(h.Learn(r, "Mansoft") && !h.Learn(r, "Mansoft"), "reference: learned once");
            T.Eq(1, h.Get("Mansoft", "Autopilot", "Standard").Count, "reference: counts as one deployment");
        }

        static void Texts()
        {
            HistoryStats st = Five().Get("Mansoft", "Autopilot", "Standard");
            DeploymentEstimate e = DeploymentEstimate.Make(Running(T0, true), T0.AddMinutes(4), TimeSpan.Zero, With(st));
            string[] v = e.Variants();
            for (int k = 1; k < v.Length; k++) T.Check(v[k].Length < v[k - 1].Length, "texts: each is shorter than the one before");
            T.Eq(v[2], e.Fit(delegate (string t) { return t.Length <= 20; }), "texts: the first that fits");
            T.Eq(v[0], e.Fit(delegate (string t) { return true; }), "texts: all of it when there is room");
            T.Eq(v[2], e.Fit(delegate (string t) { return false; }), "texts: the shortest when nothing fits");
        }
    }
}
