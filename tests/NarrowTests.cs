namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class NarrowTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        static readonly DateTime TodayStart = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Fold();
            Columns();
            Counting();
            Repeats();
            FiltersAndLoader();
        }

        static void Fold()
        {
            int[] w = { 40, 40, 100, 80, 80, 80 };
            int[] order = { 5, 4, 3 };
            bool[] none = NarrowFit.Hidden(w, order, 70, 500);
            T.Check(!none[3] && !none[4] && !none[5], "fit: nothing folds when all fit");
            bool[] one = NarrowFit.Hidden(w, order, 70, 415);
            T.Check(one[5] && !one[4] && !one[3], "fit: the first in the fold order goes first, and More takes room");
            bool[] two = NarrowFit.Hidden(w, order, 70, 330);
            T.Check(two[5] && two[4] && !two[3], "fit: more fold as the room shrinks");
            bool[] all = NarrowFit.Hidden(w, order, 70, 100);
            T.Check(all[3] && all[4] && all[5] && !all[0] && !all[2], "fit: items outside the order always stay");
            T.Eq(1040, NarrowFit.Capped(1200, 1040), "fit: a dialog is cut to the screen");
            T.Eq(700, NarrowFit.Capped(700, 1040), "fit: a small dialog stays");
            T.Eq(240, NarrowFit.Inside(500, 800, 0, 1040), "fit: a window past the edge moves back");
            T.Eq(0, NarrowFit.Inside(-30, 800, 0, 1040), "fit: a window before the edge moves in");
        }

        static void Columns()
        {
            T.Check(!NarrowFit.TwoColumns(700, 400, 14, 56), "overview: one column when narrow");
            T.Check(!NarrowFit.TwoColumns(900, 400, 14, 56), "overview: one column when the right column would be under the minimum");
            T.Check(NarrowFit.TwoColumns(1000, 400, 14, 56), "overview: two columns when there is room");
        }

        static OverviewDevice Run(string customer, string kind, string device, double daysAgo, State st, string key)
        {
            OverviewDevice o = new OverviewDevice();
            o.Customer = customer;
            o.Kind = kind;
            RemoteDeployment d = new RemoteDeployment();
            d.Key = key;
            d.Device = device;
            d.User = "Autopilot";
            d.State = st;
            d.StartUtc = Now.AddDays(-daysAgo);
            d.LastUtc = Now.AddDays(-daysAgo).AddMinutes(40);
            o.D = d;
            return o;
        }

        static OverviewSnapshot Snap(params OverviewDevice[] runs)
        {
            OverviewSnapshot s = new OverviewSnapshot();
            s.Runs.AddRange(runs);
            return s;
        }

        static void Counting()
        {
            OverviewSnapshot s = Snap(
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC01", 0.1, State.Succeeded, "a1"),
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC02", 2, State.Failed, "a2"),
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC02", 20, State.Succeeded, "a3"),
                Run("Contoso", OverviewKinds.Agents, "contoso-pc03", 29, State.Succeeded, "a4"),
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC03", 200, State.Succeeded, "a5"));
            DeployCounts c = OverviewCounts.Build(s, new OverviewFilter(), Now, TodayStart);
            T.Eq(1, c.Find(DeployCounts.Today).Runs, "counts: one run today");
            T.Eq(2, c.Find(DeployCounts.Week).Runs, "counts: two runs in 7 days");
            T.Eq(4, c.Find(DeployCounts.Month).Runs, "counts: four runs in 30 days");
            T.Eq(5, c.Find(DeployCounts.All).Runs, "counts: five runs in all");
            T.Eq(3, c.Find(DeployCounts.All).Devices, "counts: three devices, names compared without case");
            T.Eq(3, c.Find(DeployCounts.Month).Devices, "counts: four runs, but the same device twice is one device");
            T.Eq("5 deployments on 3 devices", c.Find(DeployCounts.All).Summary, "counts: summary text");
            T.Eq("1 deployment on 1 device", c.Find(DeployCounts.Today).Summary, "counts: summary text, singular");
            T.Eq(1, c.Find(DeployCounts.Today).Rows.Count, "counts: the rows are the runs behind the number");

            T.Eq(0, OverviewCounts.Build(new OverviewSnapshot(), null, Now, TodayStart).Find(DeployCounts.All).Runs, "counts: nothing read gives zero");
        }

        static void Repeats()
        {
            OverviewSnapshot s = Snap(
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC01", 1, State.Failed, "a1"),
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC01", 9, State.Succeeded, "a2"),
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC01", 40, State.Failed, "a3"),
                Run("Contoso", OverviewKinds.Sccm, "CONTOSO-PC02", 3, State.Succeeded, "s1"),
                Run("Contoso", OverviewKinds.Sccm, "CONTOSO-PC02", 5, State.Failed, "s2"),
                Run("Fabrikam", OverviewKinds.Agents, "CONTOSO-PC02", 6, State.Succeeded, "f1"),
                Run("Fabrikam", OverviewKinds.Agents, "FAB-LT01", 7, State.Succeeded, "f2"));
            DeployCounts c = OverviewCounts.Build(s, new OverviewFilter(), Now, TodayStart);
            T.Eq(2, c.Repeats.Count, "repeats: two devices were deployed more than once");
            T.Eq("CONTOSO-PC01", c.Repeats[0].Device, "repeats: most times first");
            T.Eq(3, c.Repeats[0].Times, "repeats: three times");
            T.Eq(State.Failed, c.Repeats[0].Last.D.State, "repeats: the last result is the newest run's");
            T.Eq(2, c.Repeats[1].Times, "repeats: a device of another customer with the same name is not the same device");
            T.Eq(2, c.RepeatDevices, "repeats: count of devices");
        }

        static void FiltersAndLoader()
        {
            OverviewSnapshot s = Snap(
                Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC01", 1, State.Succeeded, "a1"),
                Run("Contoso", OverviewKinds.Sccm, "CONTOSO-PC01", 2, State.Succeeded, "s1"),
                Run("Fabrikam", OverviewKinds.Agents, "FAB-LT01", 3, State.Succeeded, "f1"));
            OverviewFilter f = new OverviewFilter();
            f.Customer = "contoso";
            T.Eq(2, OverviewCounts.Build(s, f, Now, TodayStart).Find(DeployCounts.All).Runs, "filter: one customer");
            f.Side = DeviceSides.Sccm;
            T.Eq(1, OverviewCounts.Build(s, f, Now, TodayStart).Find(DeployCounts.All).Runs, "filter: one customer and the SCCM side");
            f.Customer = null;
            f.Side = DeviceSides.Both;
            T.Eq(3, OverviewCounts.Build(s, f, Now, TodayStart).Find(DeployCounts.All).Runs, "filter: all customers, both sides");
            T.Eq(1, OverviewCounts.Build(s, f, Now, TodayStart).Repeats.Count, "filter: the same device on both sides counts as deployed twice");

            // A snapshot made without runs counts its devices.
            OverviewSnapshot old = new OverviewSnapshot();
            old.Devices.Add(Run("Contoso", OverviewKinds.Agents, "CONTOSO-PC09", 1, State.Succeeded, "x"));
            T.Eq(1, OverviewCounts.Build(old, null, Now, TodayStart).Find(DeployCounts.All).Runs, "snapshot without runs: devices are counted");

            // The loader reads every run when the source can, and keeps the sessions of the week.
            OverviewSnapshot demo = new OverviewLoader(OverviewDemo.Sources(DateTime.UtcNow)).Read(DateTime.UtcNow, true);
            OverviewData d = OverviewModel.Build(demo, new OverviewFilter(), DateTime.UtcNow, DateTime.Today.ToUniversalTime(), null);
            T.Check(d.Counts.Find(DeployCounts.All).Runs > d.Counts.Find(DeployCounts.Week).Runs, "demo: older runs are counted in All");
            T.Check(d.Counts.Repeats.Count > 0, "demo: some devices were deployed more than once");
        }
    }
}
