namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // Task sequence runs from SCCM status messages, with rows as a site gives them: three runs of one deployment on one device.
    public static class SccmRunTests
    {
        const long Res = 16777400;
        const string Adv = "P0120099", Pkg = "P0100300";
        const string Started = "The task sequence execution engine started execution of a task sequence.";
        const string Group = "The task sequence execution engine started a group.";
        const string Done = "The task sequence execution engine successfully completed an action.";
        const string Begin = "The task sequence execution engine started executing an action.";
        const string TsFail = "The task sequence execution engine failed execution of a task sequence.";

        // A provider that filters on ResourceID, AdvertisementID and ExecutionTime. It reads the time in the WHERE
        // as local time on a site five hours behind UTC, which is the case the extra margin is for.
        sealed class Site : ISccmQuery
        {
            public readonly List<Dictionary<string, object>> Rows = new List<Dictionary<string, object>>();
            public readonly List<string> Asked = new List<string>();
            public int OffsetHours = -5;

            public void Add(DateTime t, long msg, string name, string action, int step)
            {
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                r["ResourceID"] = Res;
                r["AdvertisementID"] = Adv;
                r["PackageID"] = Pkg;
                r["ExecutionTime"] = t;
                r["LastStatusMsgID"] = msg;
                r["LastStatusMsgName"] = name;
                r["ActionName"] = action;
                r["Step"] = (long)step;
                r["GroupName"] = "Install";
                r["ExitCode"] = 0L;
                Rows.Add(r);
            }

            public List<Dictionary<string, object>> Query(string wql)
            {
                Asked.Add(wql);
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                if (wql.Contains("FROM SMS_R_System"))
                {
                    Dictionary<string, object> x = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    x["ResourceID"] = Res;
                    x["Name"] = "CONTOSO-PC07";
                    l.Add(x);
                    return l;
                }
                if (wql.Contains("FROM SMS_TaskSequencePackage")) return l;
                DateTime? from = null;
                int i = wql.IndexOf("ExecutionTime >= '", StringComparison.Ordinal);
                if (i >= 0) from = DateTime.SpecifyKind(DateTime.ParseExact(wql.Substring(i + 18, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture), DateTimeKind.Utc).AddHours(-OffsetHours);
                foreach (Dictionary<string, object> r in Rows)
                {
                    if (from.HasValue && (DateTime)r["ExecutionTime"] < from.Value) continue;
                    if (wql.Contains("ResourceID = ") && !wql.Contains("ResourceID = " + Res)) continue;
                    l.Add(new Dictionary<string, object>(r, StringComparer.OrdinalIgnoreCase));
                }
                return l;
            }
        }

        static readonly DateTime Day1 = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        static readonly DateTime Run1 = Day1.AddMinutes(111), Run2 = Day1.AddMinutes(120), Run3 = Day1.AddHours(23).AddMinutes(19);

        // The group message comes before the start in the same second, as rows from the provider are in no set order.
        static Site Make()
        {
            Site s = new Site();
            s.Add(Run1, 11124, Group, "", 0);
            s.Add(Run1, 11140, Started, "", 0);
            s.Add(Run1.AddMinutes(1), 11134, Done, "Partition Disk 0 - UEFI", 1);
            s.Add(Run1.AddMinutes(4), 11171, TsFail, "", 0);
            s.Add(Run2, 11124, Group, "", 0);
            s.Add(Run2, 11140, Started, "", 0);
            s.Add(Run2.AddMinutes(1), 11134, Done, "Apply Operating System", 2);
            s.Add(Run2.AddMinutes(5), 11134, Done, "Apply Windows Settings", 3);
            s.Add(Run3, 11124, Group, "", 0);
            s.Add(Run3, 11140, Started, "", 0);
            s.Add(Run3.AddMinutes(1), 11134, Done, "Format Disk", 1);
            s.Add(Run3.AddMinutes(1), 11138, Begin, "Apply Operating System", 2);
            s.Add(Run3.AddMinutes(10), 11134, Done, "Apply Operating System", 2);
            s.Add(Run3.AddMinutes(10), 11138, Begin, "Apply Device Drivers", 4);
            return s;
        }

        public static void Run()
        {
            Order();
            List();
            Open();
            Split();
        }

        static void Order()
        {
            Dictionary<string, object> g = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            g["LastStatusMsgID"] = 11124L;
            g["LastStatusMsgName"] = Group;
            T.Eq(TsMessage.GroupEvent, TsMessage.Of(g).Kind, "SCCM runs: a group that starts is not the task sequence starting");
            Site s = Make();
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>(s.Rows.GetRange(0, 2));
            SccmCatalog.SortRows(rows);
            T.Eq(11140L, SccmCatalog.Int(rows[0], "LastStatusMsgID"), "SCCM runs: the start sorts before the group in the same second");
            Dictionary<string, object> end = s.Rows[3];
            end["ExecutionTime"] = Run1.AddMinutes(1);
            rows = new List<Dictionary<string, object>> { end, s.Rows[2] };
            SccmCatalog.SortRows(rows);
            T.Eq(11171L, SccmCatalog.Int(rows[1], "LastStatusMsgID"), "SCCM runs: the end sorts after a step in the same second");
        }

        static SccmCatalog Catalog(Site s)
        {
            SccmCatalog c = new SccmCatalog(s, "CM01.contoso.com");
            c.Dmtf = SccmWmi.DmtfZero;
            return c;
        }

        static void List()
        {
            Site s = Make();
            SccmCatalog c = Catalog(s);
            c.EarlierRuns = true;
            List<RemoteDeployment> l = c.List(Run1.AddHours(-1));
            T.Eq(3, l.Count, "SCCM runs: one row per run");
            if (l.Count != 3) return;
            T.Eq((DateTime?)Run3, l[0].StartUtc, "SCCM runs: the newest run first");
            T.Eq((DateTime?)Run2, l[1].StartUtc, "SCCM runs: the second run starts at its own start");
            T.Eq((DateTime?)Run1, l[2].StartUtc, "SCCM runs: the first run starts at its own start");
            T.Check(l[0].Key != l[1].Key && l[1].Key != l[2].Key && l[0].Key.EndsWith("|" + Run3.Ticks), "SCCM runs: each run has its own key with its start");
            T.Check(l[0].State == State.Installing && l[0].StateText.Contains("Apply Device Drivers"), "SCCM runs: a run still going is running, with its last step");
            DateTime now = Run3.AddMinutes(16);
            T.Eq((TimeSpan?)TimeSpan.FromMinutes(16), l[0].Duration(now), "SCCM runs: the duration counts from the run's own start");
            T.Eq(State.Failed, l[2].State, "SCCM runs: the first run failed");
            T.Check(l[1].State == State.Unknown && l[1].StateText.StartsWith("No result before the next run"), "SCCM runs: a run without an end before the next one has no result");
            T.Eq((TimeSpan?)TimeSpan.FromMinutes(5), l[1].Duration(now), "SCCM runs: such a run ends at its last status");
            T.Eq("CONTOSO-PC07", l[0].Device, "SCCM runs: device name");

            c.EarlierRuns = false;
            l = c.List(Run1.AddHours(-1));
            T.Check(l.Count == 1 && l[0].StartUtc == Run3, "SCCM runs: by default only the newest run");
            T.Eq(2, c.Earlier, "SCCM runs: the earlier runs are counted");

            l = c.List(Run3.AddHours(-2));
            T.Check(l.Count == 1 && l[0].StartUtc == Run3 && l[0].State == State.Installing, "SCCM runs: rows from a site that reads the time as its local time are not lost");
            bool margin = false;
            foreach (string w in s.Asked) if (w.Contains("ExecutionTime >= '" + SccmWmi.DmtfZero(Run3.AddHours(-2) - SccmCatalog.Margin) + "'")) margin = true;
            T.Check(margin, "SCCM runs: the list asks for a margin before the period");
        }

        static Session Watch(IRemoteCatalog c, RemoteDeployment d)
        {
            Engine e = Engine.ForRemote(c.Open(d), d);
            e.Apply(e.Poll());
            return e.S;
        }

        static void Open()
        {
            Site s = Make();
            SccmCatalog c = Catalog(s);
            c.EarlierRuns = true;
            List<RemoteDeployment> l = c.List(Run1.AddHours(-1));
            if (l.Count != 3) return;
            Session two = Watch(c, l[1]);
            T.Check(two.Find("ts:2:Apply Operating System") != null && two.Find("ts:3:Apply Windows Settings") != null, "SCCM runs: Open reads the steps of its run");
            T.Check(two.Find("ts:1:Partition Disk 0 - UEFI") == null && two.Find("ts:1:Format Disk") == null, "SCCM runs: Open reads no steps of other runs");
            T.Check(two.GetInfo("Task sequence result") == null, "SCCM runs: the failure of the run before is not in this run");

            Engine e = Engine.ForRemote(c.Open(l[0]), l[0]);
            e.Apply(e.Poll());
            T.Check(e.S.Find("ts:1:Format Disk") != null && e.S.Find("ts:3:Apply Windows Settings") == null, "SCCM runs: the newest run reads only its own rows");
            // A status message kept on the device during a restart arrives later with the time of its step, before the last row read.
            s.Add(Run3.AddSeconds(30), 11134, Done, "Partition Disk 0 - UEFI", 0);
            e.Apply(e.Poll());
            T.Check(e.S.Find("ts:0:Partition Disk 0 - UEFI") != null, "SCCM runs: a status message that arrives late is still read");
            Item whole = e.S.Find(SccmFeed.RunKey);
            T.Check(whole != null && whole.State == State.Installing, "SCCM runs: the newest run is running");
        }

        static void Split()
        {
            Site s = new Site();
            DateTime t = Run1;
            s.Add(t.AddMinutes(-5), 11134, Done, "Apply Operating System", 2);
            s.Add(t, 11140, Started, "", 0);
            s.Add(t.AddMinutes(10), 11144, "The task sequence execution engine performed a reboot.", "", 0);
            s.Add(t.AddMinutes(13), 11140, Started, "", 0);
            s.Add(t.AddMinutes(14), 11134, Done, "Setup Windows and Configuration Manager", 5);
            List<List<Dictionary<string, object>>> runs = SccmCatalog.Runs(s.Rows);
            T.Eq(2, runs.Count, "SCCM runs: rows before the first start are a run of their own");
            T.Eq(4, runs.Count > 1 ? runs[1].Count : 0, "SCCM runs: a start right after a restart continues the run");
            T.Check(SccmCatalog.RunAt(runs, t.AddMinutes(-5)) == runs[0] && SccmCatalog.RunAt(runs, t) == runs[1], "SCCM runs: the run at a time");
        }
    }
}
