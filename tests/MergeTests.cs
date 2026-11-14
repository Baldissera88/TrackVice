namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    // Edge cases where the 1.0.0 features meet: empty sessions and stores, and options shared between them.
    public static class MergeTests
    {
        public static void Run()
        {
            CompareEmpty();
            StatsEmptyStore();
            GanttEmpty();
            FleetReportEmptyFolder();
            DeviceLogsNone();
            TsAgentAlerts();
        }

        static void CompareEmpty()
        {
            Session a = new Session();
            Session b = new Session();
            DateTime now = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
            List<DiffRow> rows = SessionDiff.Compare(a, now, b, now);
            T.Eq(0, rows.Count, "two empty sessions have no rows to compare");
            DiffSummary m = SessionDiff.Summarize(a, now, b, now, rows);
            T.Check(m != null, "two empty sessions still have a summary");
            T.Check(SessionDiff.ToCsv(rows, m).Length > 0, "two empty sessions still give a CSV header");
            rows = SessionDiff.Compare(a, now, DemoCompare.B(), now);
            T.Check(rows.Count > 0, "an empty session against a full one lists the other's items");
            SessionDiff.Summarize(a, now, DemoCompare.B(), now, rows);
        }

        static void StatsEmptyStore()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                AgentCatalog c = new AgentCatalog(new FolderStore(dir));
                T.Eq(0, c.List(DateTime.MinValue).Count, "an empty upload folder lists nothing");
                List<Session> sessions = c.LoadAll(DateTime.MinValue, 100, null);
                T.Eq(0, sessions.Count, "an empty upload folder loads nothing");
                FleetStats st = FleetStats.Build(sessions, new List<string>(), DateTime.UtcNow);
                T.Eq(0, st.Runs.Count, "statistics over nothing have no runs");
                T.Eq(0, st.Items.Count, "statistics over nothing have no items");
                T.Check(st.ToCsv().Length > 0 && st.RunsToCsv().Length > 0, "statistics over nothing still export a header");
                T.Check(Report.FleetHtml(st, null, DateTime.UtcNow).Length > 0, "a fleet report over nothing is written");
                T.Check(!c.HasLogs("sessions/none.json.gz"), "an empty upload folder has no logs");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        static void GanttEmpty()
        {
            Session s = new Session();
            DateTime now = DateTime.UtcNow;
            List<GanttRow> rows = GanttLayout.Rows(s, now, true, false);
            T.Eq(0, rows.Count, "a session with no items has no Gantt rows");
            DateTime from, to;
            GanttLayout.Range(rows, now, false, out from, out to);
            T.Check(to > from, "the Gantt range of no rows is not empty");
            GanttLayout.Ticks(from, to, GanttLayout.Step(to - from, 800, 84), TimeZoneInfo.Local);
            T.Eq(-1, GanttLayout.RowAt(40, 30, 0, 26, 0), "no row under the pointer when there are none");
            T.Eq(0, GanttLayout.ClampTop(100, 0, 26, 400), "no rows do not scroll");
        }

        static void FleetReportEmptyFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-fleet-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string html = Path.Combine(dir, "fleet.html");
            TextWriter keep = Console.Out;
            try
            {
                Console.SetOut(TextWriter.Null);
                T.Eq(0, Report.FleetCli(html, dir, 0, null), "a fleet report over an empty folder finds no failures");
                T.Check(File.Exists(html), "a fleet report over an empty folder is saved");
            }
            finally
            {
                Console.SetOut(keep);
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void DeviceLogsNone()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-nologs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                AgentCatalog c = new AgentCatalog(new FolderStore(dir));
                T.Eq(null, DeviceLogs.Download(c, "sessions/none.json.gz", "CONTOSO-PC01", Path.Combine(dir, "copy"), DateTime.UtcNow), "no logs uploaded gives no copy");
                T.Eq(null, DeviceLogs.Map(null, @"C:\Windows\CCM\Logs\smsts.log"), "a log maps to nothing without a copy");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        // The agent in a task sequence takes /alert like the other agents, and refuses an address that is not https.
        static void TsAgentAlerts()
        {
            AgentOptions o = new AgentOptions();
            o.Upload = @"\\server\share\TrackVice";
            o.Alert = "https://contoso.example/hook?sig=abc";
            T.Check(AgentControl.ChildArgs(o).Contains(" /alert \"https://contoso.example/hook?sig=abc\""), "the alert address is passed to the agent in the task sequence");
            o.Alert = "http://contoso.example/hook";
            bool threw = false;
            try { AgentControl.Start(o, null, delegate (string s) { }); }
            catch (ArgumentException) { threw = true; }
            T.Check(threw, "start with an alert address that is not https is refused");
        }
    }
}
