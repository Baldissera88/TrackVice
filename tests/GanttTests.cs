namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class GanttTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

        static Obs O(string key, string phase, double sec, State? st)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddSeconds(sec);
            o.Source = "test";
            o.ItemKey = key;
            o.ItemName = "Contoso " + key;
            o.ItemKind = Kinds.Application;
            o.PhaseKey = phase;
            o.State = st;
            o.Text = "at " + sec;
            return o;
        }

        public static void Run()
        {
            Session s = new Session();
            Tracker t = new Tracker(s);
            t.ApplyAll(new List<Obs>
            {
                O("late", Phases.SccmApps, 120, State.Installing),
                O("early", Phases.SccmApps, 10, State.Installing),
                O("early", Phases.SccmApps, 70, State.Succeeded),
                O("blip", Phases.SccmApps, 30, State.Succeeded),
                O("pol", Phases.Policies, 5, State.Succeeded),
            });
            DateTime now = T0.AddSeconds(300);

            // Bars: a final item ends at its end, a running one at now.
            Item early = s.Find("early"), late = s.Find("late");
            T.Eq(T0.AddSeconds(10), GanttLayout.BarStart(early), "bar starts at the start");
            T.Eq(T0.AddSeconds(70), GanttLayout.BarEnd(early, now), "final bar ends at its end");
            T.Eq(now, GanttLayout.BarEnd(late, now), "running bar ends now");

            // Policies hidden, rows by start time under one header.
            List<GanttRow> rows = GanttLayout.Rows(s, now, false, false);
            T.Eq(4, rows.Count, "header and three items");
            T.Check(rows[0].IsHeader, "phase header first");
            T.Eq("early", rows[1].Item.Key, "earliest first");
            T.Eq("blip", rows[2].Item.Key, "then by start");
            T.Eq("late", rows[3].Item.Key, "latest last");
            T.Check(rows[2].Instant, "a zero length item is instant");
            T.Eq(now, rows[0].EndUtc, "header spans its items");

            List<GanttRow> all = GanttLayout.Rows(s, now, true, false);
            T.Eq(6, all.Count, "policies shown on request");
            T.Check(all.Exists(delegate (GanttRow r) { return r.IsHeader && r.Phase.Key == Phases.Policies; }), "policies header");

            List<GanttRow> timed = GanttLayout.Rows(s, now, true, true);
            T.Eq(3, timed.Count, "only what took time drops instant items and empty phases");
            T.Check(!timed.Exists(delegate (GanttRow r) { return r.Item != null && r.Item.Key == "blip"; }), "blip hidden");

            // Range covers the bars and now.
            DateTime from, to;
            GanttLayout.Range(rows, T0.AddSeconds(600), true, out from, out to);
            T.Eq(T0.AddSeconds(10), from, "range from the first bar");
            T.Check(to >= T0.AddSeconds(600), "range reaches now when live");
            GanttLayout.Range(new List<GanttRow>(), now, false, out from, out to);
            T.Check(to - from >= TimeSpan.FromMinutes(1), "empty range is at least a minute");

            // Tick steps: about one every 80 px.
            T.Eq(TimeSpan.FromSeconds(10), GanttLayout.Step(TimeSpan.FromMinutes(1), 800, 80), "one minute over 800 px");
            T.Eq(TimeSpan.FromMinutes(10), GanttLayout.Step(TimeSpan.FromHours(1), 800, 80), "one hour over 800 px");
            T.Eq(TimeSpan.FromHours(1), GanttLayout.Step(TimeSpan.FromHours(10), 800, 80), "ten hours over 800 px");
            T.Eq(TimeSpan.FromDays(1), GanttLayout.Step(TimeSpan.FromDays(30), 800, 80), "longest step");

            // Ticks fall on round local times.
            TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Contoso", TimeSpan.FromMinutes(90), "Contoso", "Contoso");
            List<GanttTick> ticks = GanttLayout.Ticks(T0.AddSeconds(7), T0.AddMinutes(30), TimeSpan.FromMinutes(10), zone);
            T.Eq(3, ticks.Count, "three ticks in half an hour");
            T.Eq("11:40", ticks[0].Label, "first tick on a round local time");
            T.Eq(T0.AddMinutes(10), ticks[0].Utc, "tick time in UTC");
            T.Eq("11:30:10", GanttLayout.Ticks(T0.AddSeconds(7), T0.AddSeconds(20), TimeSpan.FromSeconds(5), zone)[0].Label, "seconds when the step is under a minute");

            // Bar maths and hit testing.
            T.Eq(150f, GanttLayout.X(T0.AddMinutes(30), T0, T0.AddHours(1), 100, 100), "x halfway");
            T.Eq(100f, GanttLayout.X(T0, T0, T0, 100, 100), "x of an empty span");
            T.Eq(-1, GanttLayout.RowAt(10, 30, 0, 26, 5), "axis is no row");
            T.Eq(0, GanttLayout.RowAt(31, 30, 0, 26, 5), "first row");
            T.Eq(2, GanttLayout.RowAt(31, 30, 52, 26, 5), "scrolled");
            T.Eq(-1, GanttLayout.RowAt(500, 30, 0, 26, 5), "below the rows");
            T.Eq(30, GanttLayout.ClampTop(100, 5, 26, 100), "top clamped to the content");
            T.Eq(0, GanttLayout.ClampTop(-5, 5, 26, 500), "top not negative");
            T.Eq(206f, GanttLayout.LabelX(200, 40, 300, 6), "label after the bar");
            T.Eq(-1f, GanttLayout.LabelX(280, 40, 300, 6), "no room for the label");

            // 300 items stay quick to lay out.
            Session big = new Session();
            Tracker bt = new Tracker(big);
            List<Obs> many = new List<Obs>();
            for (int k = 0; k < 300; k++) many.Add(O("app" + k, Phases.SccmApps, (k * 37) % 900, State.Succeeded));
            bt.ApplyAll(many);
            DateTime w = DateTime.UtcNow;
            for (int k = 0; k < 20; k++) GanttLayout.Rows(big, now, true, false);
            T.Check(DateTime.UtcNow - w < TimeSpan.FromSeconds(2), "300 items lay out quickly");
        }
    }
}
