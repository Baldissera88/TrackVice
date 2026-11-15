namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class WallTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Utc);
        static readonly DateTime Today = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        static readonly TimeSpan Keep = TimeSpan.FromHours(2);

        public static void Run()
        {
            Tiles();
            Ageing();
            Filters();
            Sorting();
            Summary();
            Grid();
            Ended();
            Demo();
        }

        static string Iso(DateTime t)
        {
            return t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }

        static StoreEntry Entry(string pc, int items, int done, int failed, int active, DateTime started, DateTime updated, DateTime? ended)
        {
            StoreEntry e = new StoreEntry();
            e.Name = "sessions/" + pc.ToLowerInvariant() + ".json.gz";
            e.ModifiedUtc = updated;
            e.Meta["computer"] = pc;
            e.Meta["kind"] = "Autopilot";
            e.Meta["profile"] = "Contoso standard";
            e.Meta["items"] = items.ToString();
            e.Meta["done"] = done.ToString();
            e.Meta["failed"] = failed.ToString();
            e.Meta["active"] = active.ToString();
            e.Meta["started"] = Iso(started);
            e.Meta["updated"] = Iso(updated);
            e.Meta["idle"] = "0";
            if (active > 0) e.Meta["current"] = "Installing Company Portal";
            if (ended.HasValue) e.Meta["ended"] = Iso(ended.Value);
            return e;
        }

        static WallBoard Board(params StoreEntry[] entries)
        {
            WallBoard b = new WallBoard();
            b.Update(new List<StoreEntry>(entries), Now);
            return b;
        }

        static WallTile Find(List<WallTile> l, string pc)
        {
            foreach (WallTile t in l) if (t.Device == pc) return t;
            return null;
        }

        static void Tiles()
        {
            WallTile run = WallModel.Tile(Entry("CONTOSO-PC01", 20, 7, 0, 1, Now.AddMinutes(-24), Now.AddSeconds(-20), null), Now);
            T.Eq(WallState.Running, run.State, "wall: uploading device runs");
            T.Eq(35, run.Percent, "wall: percent is done of items");
            T.Eq("Installing Company Portal", run.Line(Now), "wall: running line is the current item");
            T.Eq("Autopilot · Contoso standard", run.Deployment, "wall: deployment and profile");
            T.Eq(24.0, run.Elapsed(Now).Value.TotalMinutes, "wall: elapsed since start");

            WallTile silent = WallModel.Tile(Entry("CONTOSO-PC02", 20, 7, 0, 1, Now.AddMinutes(-30), Now.AddMinutes(-6), null), Now);
            T.Eq(WallState.Silent, silent.State, "wall: no upload for 6 minutes is silent");
            T.Eq("No upload for 6 min", silent.Line(Now), "wall: silent line");

            StoreEntry idle = Entry("CONTOSO-PC03", 20, 19, 0, 0, Now.AddMinutes(-90), Now.AddMinutes(-8), null);
            idle.Meta["idle"] = "1";
            T.Eq(WallState.Running, WallModel.Tile(idle, Now).State, "wall: an idle agent that uploads less often is not silent");

            WallTile done = WallModel.Tile(Entry("CONTOSO-PC04", 12, 12, 0, 0, Now.AddMinutes(-60), Now, Now.AddMinutes(-10)), Now);
            T.Eq(WallState.Finished, done.State, "wall: all done is finished");
            T.Eq(100, done.Percent, "wall: finished is 100 %");
            T.Eq(50.0, done.Elapsed(Now).Value.TotalMinutes, "wall: a finished device's time stops when it ended");
            T.Eq("Done, 12 items", done.Line(Now), "wall: finished line");

            WallTile failed = WallModel.Tile(Entry("CONTOSO-PC05", 12, 9, 2, 1, Now.AddMinutes(-60), Now, null), Now);
            T.Eq(WallState.Failed, failed.State, "wall: a failure makes the tile failed while it runs");
            T.Eq("2 failed · Installing Company Portal", failed.Line(Now), "wall: failed line has the count");
            T.Check(!failed.Final, "wall: a failed device that still runs is not final");

            WallTile old = WallModel.Tile(Entry("CONTOSO-PC06", 12, 12, 0, 0, Now.AddMinutes(-20), Now.AddMinutes(-20), null), Now);
            T.Eq(WallState.Finished, old.State, "wall: a finished device that stopped uploading is not silent");

            StoreEntry bare = new StoreEntry();
            bare.Name = "sessions/abc.json.gz";
            bare.ModifiedUtc = Now;
            WallTile b = WallModel.Tile(bare, Now);
            T.Eq("abc.json.gz", b.Device, "wall: no metadata falls back to the file name");
            T.Eq("Nothing has started yet", b.Line(Now), "wall: nothing started");
            T.Eq(0, b.Percent, "wall: no items is 0 %");
        }

        static void Ageing()
        {
            WallTile recent = WallModel.Tile(Entry("CONTOSO-PC10", 5, 5, 0, 0, Now.AddHours(-3), Now, Now.AddMinutes(-119)), Now);
            WallTile aged = WallModel.Tile(Entry("CONTOSO-PC11", 5, 5, 0, 0, Now.AddHours(-3), Now, Now.AddMinutes(-121)), Now);
            T.Check(WallModel.Visible(recent, Now, Today, Keep), "wall: finished stays for two hours");
            T.Check(!WallModel.Visible(aged, Now, Today, Keep), "wall: finished goes after two hours");
            T.Check(WallModel.Visible(aged, Now, Today, TimeSpan.FromHours(4)), "wall: the time finished devices stay can be changed");

            WallTile failedMorning = WallModel.Tile(Entry("CONTOSO-PC12", 5, 3, 2, 0, Now.AddHours(-14), Now, Now.AddHours(-13)), Now);
            WallTile failedYesterday = WallModel.Tile(Entry("CONTOSO-PC13", 5, 3, 2, 0, Now.AddHours(-17), Now, Now.AddHours(-16)), Now);
            T.Check(WallModel.Visible(failedMorning, Now, Today, Keep), "wall: failed stays until midnight");
            T.Check(!WallModel.Visible(failedYesterday, Now, Today, Keep), "wall: failed yesterday is gone");

            WallTile silentYesterday = WallModel.Tile(Entry("CONTOSO-PC14", 5, 3, 0, 1, Now.AddHours(-17), Now.AddHours(-16), null), Now);
            T.Check(!WallModel.Visible(silentYesterday, Now, Today, Keep), "wall: a device silent since yesterday is gone");

            // An agent that does not say when it ended: the wall counts from when it first saw it finished.
            WallBoard b = new WallBoard();
            List<StoreEntry> l = new List<StoreEntry>();
            l.Add(Entry("CONTOSO-PC15", 5, 5, 0, 0, Now.AddHours(-1), Now, null));
            b.Update(l, Now);
            T.Eq(Now, b.Tiles[0].EndUtc.Value, "wall: first seen finished");
            l[0].Meta["updated"] = Iso(Now.AddHours(3));
            b.Update(l, Now.AddHours(3));
            T.Eq(Now, b.Tiles[0].EndUtc.Value, "wall: first seen time is kept");
            T.Eq(0, b.Shown(WallFilter.All, null, Now.AddHours(3), Today, Keep).Count, "wall: and it ages out from then");
        }

        static void Filters()
        {
            WallBoard b = Board(
                Entry("CONTOSO-PC20", 10, 4, 0, 1, Now.AddMinutes(-10), Now, null),
                Entry("CONTOSO-PC21", 10, 4, 0, 1, Now.AddMinutes(-40), Now.AddMinutes(-9), null),
                Entry("CONTOSO-PC22", 10, 8, 2, 0, Now.AddMinutes(-80), Now, Now.AddMinutes(-30)),
                Entry("CONTOSO-PC23", 10, 10, 0, 0, Now.AddMinutes(-80), Now, Now.AddMinutes(-20)),
                Entry("CONTOSO-PC24", 10, 10, 0, 0, Now.AddHours(-6), Now, Now.AddHours(-5)),
                Entry("CONTOSO-PC25", 10, 10, 0, 0, Now.AddHours(-20), Now, Now.AddHours(-19)));
            T.Eq(4, b.Shown(WallFilter.All, null, Now, Today, Keep).Count, "wall: all without the aged out");
            T.Eq(2, b.Shown(WallFilter.Running, null, Now, Today, Keep).Count, "wall: running includes silent");
            T.Eq(1, b.Shown(WallFilter.Failed, null, Now, Today, Keep).Count, "wall: failed");
            T.Eq(2, b.Shown(WallFilter.FinishedToday, null, Now, Today, Keep).Count, "wall: finished today keeps the aged out of today");
            T.Eq(1, b.Shown(WallFilter.All, "pc22", Now, Today, Keep).Count, "wall: search by device");
            T.Eq(2, b.Shown(WallFilter.All, "company portal", Now, Today, Keep).Count, "wall: search by current item");
            T.Eq(0, b.Shown(WallFilter.Failed, "pc20", Now, Today, Keep).Count, "wall: search and filter together");

            StoreEntry notSession = Entry("CONTOSO-PC26", 1, 1, 0, 0, Now, Now, Now);
            notSession.Name = "logs/contoso-pc26.zip";
            T.Eq(0, Board(notSession).Tiles.Count, "wall: only sessions become tiles");
        }

        static void Sorting()
        {
            WallBoard b = Board(
                Entry("CONTOSO-PC30", 10, 10, 0, 0, Now.AddMinutes(-80), Now, Now.AddMinutes(-20)),
                Entry("CONTOSO-PC31", 10, 4, 0, 1, Now.AddMinutes(-40), Now.AddSeconds(-50), null),
                Entry("CONTOSO-PC32", 10, 4, 0, 1, Now.AddMinutes(-10), Now.AddSeconds(-10), null),
                Entry("CONTOSO-PC33", 10, 4, 0, 1, Now.AddMinutes(-50), Now.AddMinutes(-8), null),
                Entry("CONTOSO-PC34", 10, 8, 2, 0, Now.AddMinutes(-80), Now, Now.AddMinutes(-3)));
            List<WallTile> l = b.Shown(WallFilter.All, null, Now, Today, Keep);
            string order = "";
            foreach (WallTile t in l) order += t.Device.Substring(10) + " ";
            T.Eq("32 31 34 33 30 ", order, "wall: uploading devices first by start, then by last activity");
        }

        static void Summary()
        {
            WallBoard b = Board(
                Entry("CONTOSO-PC40", 10, 4, 0, 1, Now.AddMinutes(-10), Now, null),
                Entry("CONTOSO-PC41", 10, 4, 0, 1, Now.AddMinutes(-40), Now.AddMinutes(-9), null),
                Entry("CONTOSO-PC42", 10, 8, 2, 0, Now.AddMinutes(-80), Now, Now.AddMinutes(-30)),
                Entry("CONTOSO-PC43", 10, 10, 0, 0, Now.AddMinutes(-80), Now, Now.AddMinutes(-20)),
                Entry("CONTOSO-PC44", 10, 10, 0, 0, Now.AddHours(-6), Now, Now.AddHours(-5)),
                Entry("CONTOSO-PC45", 10, 10, 0, 0, Now.AddHours(-20), Now, Now.AddHours(-19)));
            WallSummary s = b.Summary(Now, Today);
            T.Eq(1, s.Running, "wall summary: running");
            T.Eq(1, s.Silent, "wall summary: silent");
            T.Eq(2, s.FinishedToday, "wall summary: finished today");
            T.Eq(1, s.FailedToday, "wall summary: failed today");
            T.Eq(60.0, s.Average.Value.TotalMinutes, "wall summary: average of today's finished");
            T.Eq("1 running  ·  1 with no upload  ·  2 finished today  ·  1 failed today  ·  average 1 h 00 min today", s.Text(), "wall summary text");
            T.Eq("0 running  ·  0 finished today  ·  0 failed today", new WallSummary().Text(), "wall summary when empty");
        }

        static void Grid()
        {
            WallGrid g = WallGrid.Fit(5, 1800, 900, 360, 200, 120, 16);
            T.Eq(4, g.Columns, "wall grid: columns by width");
            T.Eq(438, g.TileW, "wall grid: tiles fill the width");
            T.Eq(2, g.Rows, "wall grid: rows");
            T.Eq(200, g.TileH, "wall grid: room enough keeps the height");
            int x, y;
            g.Cell(5, out x, out y);
            T.Eq(454, x, "wall grid: second column");
            T.Eq(216, y, "wall grid: second row");
            g.Cell(4, out x, out y);
            T.Eq(0, x, "wall grid: wraps to the first column");

            WallGrid many = WallGrid.Fit(20, 1800, 900, 360, 200, 120, 16);
            T.Eq(5, many.Rows, "wall grid: many devices use more rows");
            T.Eq(167, many.TileH, "wall grid: and lower tiles");
            T.Eq(0, many.Hidden, "wall grid: all fit");

            WallGrid tooMany = WallGrid.Fit(60, 1800, 900, 360, 200, 120, 16);
            T.Eq(120, tooMany.TileH, "wall grid: not lower than the smallest");
            T.Eq(6, tooMany.Rows, "wall grid: rows that fit");
            T.Eq(24, tooMany.Shown, "wall grid: shown");
            T.Eq(36, tooMany.Hidden, "wall grid: the rest is counted");

            WallGrid narrow = WallGrid.Fit(3, 300, 700, 360, 200, 120, 16);
            T.Eq(1, narrow.Columns, "wall grid: at least one column");
            T.Eq(300, narrow.TileW, "wall grid: one column takes the width");
        }

        static void Ended()
        {
            Session s = new Session();
            T.Check(!WallModel.Ended(s).HasValue, "wall: an empty session has not ended");
            Item a = new Item();
            a.Key = "a";
            a.State = State.Succeeded;
            a.LastUtc = Now.AddMinutes(-30);
            a.EndUtc = Now.AddMinutes(-31);
            Item b = new Item();
            b.Key = "b";
            b.State = State.Installing;
            b.LastUtc = Now.AddMinutes(-5);
            s.Items.Add(a);
            s.Items.Add(b);
            T.Check(!WallModel.Ended(s).HasValue, "wall: a session with a running item has not ended");
            b.State = State.Failed;
            T.Eq(Now.AddMinutes(-5), WallModel.Ended(s).Value, "wall: ended with the last item");
            T.Eq(Iso(Now.AddMinutes(-5)), AgentData.Summary(s, Now, false)["ended"], "wall: the upload says when the session ended");
        }

        static void Demo()
        {
            WallDemoStore st = new WallDemoStore();
            WallBoard b = new WallBoard();
            DateTime now = DateTime.UtcNow;
            b.Update(st.List(AgentData.Prefix), now);
            T.Eq(10, b.Tiles.Count, "wall demo: ten devices");
            int finished = 0, silent = 0;
            foreach (WallTile t in b.Tiles) { if (t.State == WallState.Finished) finished++; if (t.State == WallState.Silent) silent++; }
            T.Check(finished >= 2, "wall demo: some devices finished");
            T.Eq(1, silent, "wall demo: one device stopped uploading");
            WallTile lt = Find(b.Tiles, "CONTOSO-LT0371");
            T.Check(lt != null && AgentData.Unpack(st.Get(lt.Key)).Computer == "CONTOSO-LT0371", "wall demo: a tile opens its own session");
        }
    }
}
