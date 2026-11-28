namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class WallTimeLeftTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Usual();
            Left();
            Over();
            Nothing();
        }

        static WallTile Tile(string pc, string kind, string profile, WallState state, int startedMinAgo, int? tookMin)
        {
            WallTile t = new WallTile();
            t.Key = pc;
            t.Device = pc;
            t.Kind = kind;
            t.Profile = profile;
            t.State = state;
            t.Items = 10;
            t.Done = state == WallState.Finished ? 10 : 4;
            t.Active = state == WallState.Running ? 1 : 0;
            t.StartUtc = Now.AddMinutes(-startedMinAgo);
            if (tookMin.HasValue) t.EndUtc = t.StartUtc.Value.AddMinutes(tookMin.Value);
            t.LastUtc = Now;
            return t;
        }

        static void Usual()
        {
            WallTile run = Tile("PC01", "Autopilot", "Standard", WallState.Running, 20, null);
            List<WallTile> l = new List<WallTile>
            {
                run,
                Tile("PC02", "Autopilot", "Standard", WallState.Finished, 200, 40),
                Tile("PC03", "Autopilot", "standard", WallState.Finished, 200, 60),
                Tile("PC04", "Autopilot", "Standard", WallState.Finished, 200, 50),
                Tile("PC05", "Autopilot", "Kiosk", WallState.Finished, 200, 10),
                Tile("PC06", "Task sequence", "Standard", WallState.Finished, 200, 90),
            };
            WallTile failed = Tile("PC07", "Autopilot", "Standard", WallState.Failed, 200, 5);
            failed.FailedCount = 1;
            l.Add(failed);
            int runs;
            TimeSpan? u = WallTimeLeft.UsualTime(l, run, Now, out runs);
            T.Eq(3, runs, "wall time left: only finished devices with the same kind and profile count");
            T.Eq(50.0, u.Value.TotalMinutes, "wall time left: the usual time is the median");
            l.RemoveAt(3);
            u = WallTimeLeft.UsualTime(l, run, Now, out runs);
            T.Eq(50.0, u.Value.TotalMinutes, "wall time left: an even count takes the middle two");
        }

        static void Left()
        {
            WallTile run = Tile("PC01", "Autopilot", "Standard", WallState.Running, 20, null);
            List<WallTile> l = new List<WallTile> { run, Tile("PC02", "Autopilot", "Standard", WallState.Finished, 200, 50) };
            WallTimeLeft w = WallTimeLeft.For(run, l, Now);
            T.Check(w != null && !w.Over, "wall time left: a running device gets a time left");
            T.Eq(30.0, w.Left.TotalMinutes, "wall time left: usual minus elapsed");
            T.Eq("about 30 min left", w.Words, "wall time left: words");
            T.Eq(Now.AddMinutes(30), w.FinishUtc, "wall time left: finish time");
            T.Check(w.Detail.StartsWith("done ~"), "wall time left: detail is the finish clock");
        }

        static void Over()
        {
            WallTile run = Tile("PC01", "Autopilot", "Standard", WallState.Running, 70, null);
            List<WallTile> l = new List<WallTile> { run, Tile("PC02", "Autopilot", "Standard", WallState.Finished, 200, 50) };
            WallTimeLeft w = WallTimeLeft.For(run, l, Now);
            T.Check(w != null && w.Over, "wall time left: past the usual time is longer than usual");
            T.Eq("Longer than usual", w.Words, "wall time left: over words");
            T.Eq("usually 50 min", w.Detail, "wall time left: over detail");
        }

        static void Nothing()
        {
            WallTile run = Tile("PC01", "Autopilot", "Standard", WallState.Running, 20, null);
            T.Check(WallTimeLeft.For(run, new List<WallTile> { run }, Now) == null, "wall time left: nothing finished, nothing said");
            WallTile done = Tile("PC02", "Autopilot", "Standard", WallState.Finished, 200, 50);
            T.Check(WallTimeLeft.For(done, new List<WallTile> { run, done, Tile("PC03", "Autopilot", "Standard", WallState.Finished, 200, 40) }, Now) == null, "wall time left: a finished device has none");
            WallTile silent = Tile("PC04", "Autopilot", "Standard", WallState.Silent, 20, null);
            T.Check(WallTimeLeft.For(silent, new List<WallTile> { silent, done }, Now) == null, "wall time left: a device that stopped uploading has none");
        }
    }
}
