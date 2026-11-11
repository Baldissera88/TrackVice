namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class TrackerTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

        static Obs O(string key, double min, State? st)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddMinutes(min);
            o.Source = "test";
            o.ItemKey = key;
            o.ItemName = key.ToUpperInvariant();
            o.ItemKind = Kinds.Application;
            o.PhaseKey = Phases.SccmApps;
            o.State = st;
            o.Text = "at " + min;
            return o;
        }

        public static void Run()
        {
            // Out of order input is applied in time order.
            Session s = new Session();
            Tracker t = new Tracker(s);
            t.ApplyAll(new List<Obs> { O("a", 5, State.Succeeded), O("a", 1, State.Installing) });
            Item a = s.Find("a");
            T.Eq(State.Succeeded, a.State, "later state wins");
            T.Eq(T0.AddMinutes(1), a.StartUtc.Value, "start from the first activity");
            T.Eq(TimeSpan.FromMinutes(4), a.Duration(T0.AddHours(1)).Value, "duration start to end");
            T.Eq(T0.AddMinutes(1), s.Events[0].TimeUtc, "events sorted");

            // A late, older observation does not undo a newer state.
            t.Apply(O("a", 3, State.Installing));
            T.Eq(State.Succeeded, a.State, "older state ignored");

            // Running items count up to now.
            t.Apply(O("b", 10, State.Downloading));
            T.Eq(TimeSpan.FromMinutes(5), s.Find("b").Duration(T0.AddMinutes(15)).Value, "running duration");

            // Aliases merge a deployment type into its application.
            t.Apply(O("dt", 20, State.Installing));
            t.Apply(O("app", 21, State.Pending));
            t.Alias("dt", "app");
            T.Check(s.Find("dt") == null, "alias removes the old key");
            Item merged = s.Find("app");
            T.Eq(State.Pending, merged.State, "newer state kept on merge");
            T.Eq(2, merged.Events.Count, "events merged");
            t.Apply(O("dt", 22, State.Succeeded));
            T.Eq(State.Succeeded, s.Find("app").State, "later observations follow the alias");

            // Phases and progress.
            Phase p = s.FindPhase(Phases.SccmApps);
            T.Check(p != null && p.Items.Count == 3, "phase has its items");
            T.Eq(State.Installing, p.State, "phase is running while an item runs");

            // Findings.
            Session f = new Session();
            Tracker tf = new Tracker(f);
            for (int k = 0; k < 3; k++)
            {
                Obs st = O("x", k * 10, State.Installing);
                st.NewAttempt = true;
                st.Command = "setup.exe";
                tf.Apply(st);
                Obs end = O("x", k * 10 + 1, k < 2 ? State.Failed : State.Succeeded);
                if (k < 2) end.ExitCode = 1618;
                tf.Apply(end);
            }
            tf.Apply(O("stuck", 0, State.Downloading));
            Finder.Run(f, T0.AddMinutes(30), true);
            T.Check(Has(f, FindingKinds.Retries), "retry loop found");
            T.Check(Has(f, FindingKinds.Stuck), "no progress found while live");
            Finder.Run(f, T0.AddMinutes(30), false);
            T.Check(!Has(f, FindingKinds.Stuck), "no progress is not judged on a saved session");

            // ESP time limit.
            Session e = new Session();
            Tracker te = new Tracker(e);
            Obs ea = O("slow", 0, State.Installing);
            ea.PhaseKey = Phases.DeviceSetup;
            te.Apply(ea);
            e.SetInfo("ESP time limit (min)", "60");
            Finder.Run(e, T0.AddMinutes(59), true);
            T.Check(!Has(e, FindingKinds.EspTimeLimit), "within the ESP time limit");
            Finder.Run(e, T0.AddMinutes(61), true);
            T.Check(Has(e, FindingKinds.EspTimeLimit), "past the ESP time limit");

            // Save and load.
            s.SetInfo("Task sequence", "Win 11");
            string json = s.ToJson();
            Session back = Session.FromJson(json);
            T.Eq(s.Items.Count, back.Items.Count, "items round trip");
            T.Eq(s.Events.Count, back.Events.Count, "events round trip");
            T.Eq("Win 11", back.GetInfo("Task sequence"), "info round trip");
            Item ba = back.Find("a");
            T.Check(ba != null && ba.State == State.Succeeded && ba.Events.Count == s.Find("a").Events.Count, "item state and events round trip");
            T.Check(back.FindPhase(Phases.SccmApps) != null && back.FindPhase(Phases.SccmApps).Items.Count == 3, "phases rebuilt after load");
            T.Eq(s.Find("a").StartUtc, ba != null ? ba.StartUtc : null, "times round trip");
        }

        static bool Has(Session s, string kind)
        {
            foreach (Finding f in s.Findings) if (f.Kind == kind) return true;
            return false;
        }
    }
}
