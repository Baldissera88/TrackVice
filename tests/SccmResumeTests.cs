namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // Task sequence runs that resume after a restart, warnings without a step (11910), applications from a
    // variable list (11904 / 11905), and the newer run a followed run can switch to.
    public static class SccmResumeTests
    {
        const long Res = 16777512;
        const string Adv = "P0120044", Pkg = "P0100410";
        const string Started = "The task sequence execution engine started execution of a task sequence.";
        const string Group = "The task sequence execution engine started a group.";
        const string Done = "The task sequence execution engine successfully completed an action.";
        const string Begin = "The task sequence execution engine started executing an action.";
        const string Reboot = "The task sequence execution engine performed a system reboot initiated by the action (Wait for restart) in the group (Troubleshoot).";
        const string Completed = "The task sequence execution engine successfully completed the task sequence.";
        const string AppDone = "The task sequence successfully installed application that was specified through TS variable.";
        const string AppFail = "The task sequence failed to install application that was specified through TS variable.";

        sealed class Site : ISccmQuery
        {
            public readonly List<Dictionary<string, object>> Rows = new List<Dictionary<string, object>>();

            public Dictionary<string, object> Add(DateTime t, long msg, string name, string action, int step, string group)
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
                r["GroupName"] = group;
                r["ExitCode"] = 0L;
                Rows.Add(r);
                return r;
            }

            public List<Dictionary<string, object>> Query(string wql)
            {
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                if (wql.Contains("FROM SMS_R_System"))
                {
                    Dictionary<string, object> x = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    x["ResourceID"] = Res;
                    x["Name"] = "CONTOSO-PC31";
                    l.Add(x);
                    return l;
                }
                if (wql.Contains("FROM SMS_TaskSequencePackage")) return l;
                foreach (Dictionary<string, object> r in Rows) l.Add(new Dictionary<string, object>(r, StringComparer.OrdinalIgnoreCase));
                return l;
            }
        }

        static readonly DateTime T0 = new DateTime(2026, 11, 17, 11, 40, 0, DateTimeKind.Utc);

        // As seen on a site: the run restarts by "Wait for restart" and sends 11140 again when it resumes in full Windows.
        static Site Resume()
        {
            Site s = new Site();
            s.Add(T0, 11140, Started, "", 0, "");
            s.Add(T0, 11124, Group, "Prepare", 0, "Prepare");
            s.Add(T0.AddMinutes(2), 11134, Done, "Set Variables", 1, "Prepare");
            s.Add(T0.AddMinutes(20), 11134, Done, "Install Contoso Agent", 7, "Install");
            s.Add(T0.AddMinutes(30), 11124, Group, "Troubleshoot", 8, "Troubleshoot");
            s.Add(new DateTime(2026, 11, 17, 12, 53, 15, DateTimeKind.Utc), 11144, Reboot, "Wait for restart", 9, "Troubleshoot");
            s.Add(new DateTime(2026, 11, 17, 12, 53, 33, DateTimeKind.Utc), 11140, Started, "", 0, "");
            s.Add(new DateTime(2026, 11, 17, 12, 53, 34, DateTimeKind.Utc), 11124, Group, "Last Step", 10, "Last Step");
            s.Add(new DateTime(2026, 11, 17, 12, 53, 46, DateTimeKind.Utc), 11134, Done, "Write Last Step", 11, "Last Step");
            s.Add(new DateTime(2026, 11, 17, 12, 53, 46, DateTimeKind.Utc), 11143, Completed, "", 0, "");
            return s;
        }

        public static void Run()
        {
            Resumes();
            TwoRuns();
            Gaps();
            Bypass();
            Apps();
            Newer();
        }

        static SccmCatalog Catalog(Site s)
        {
            SccmCatalog c = new SccmCatalog(s, "CM01.contoso.com");
            c.Dmtf = SccmWmi.DmtfZero;
            c.EarlierRuns = true;
            return c;
        }

        static Engine Watch(IRemoteCatalog c, RemoteDeployment d)
        {
            Engine e = Engine.ForRemote(c.Open(d), d);
            e.Apply(e.Poll());
            return e;
        }

        static void Resumes()
        {
            Site s = Resume();
            T.Eq(1, SccmCatalog.Runs(new List<Dictionary<string, object>>(s.Rows)).Count, "SCCM resume: a start after a restart in full Windows continues the run");
            SccmCatalog c = Catalog(s);
            List<RemoteDeployment> l = c.List(T0.AddHours(-1));
            T.Eq(1, l.Count, "SCCM resume: one row in the list");
            if (l.Count != 1) return;
            T.Eq(State.Succeeded, l[0].State, "SCCM resume: the run ends Succeeded");
            T.Eq((DateTime?)T0, l[0].StartUtc, "SCCM resume: the run keeps its first start");
            Engine e = Watch(c, l[0]);
            T.Check(e.S.Find("ts:11:Write Last Step") != null, "SCCM resume: the feed reads the rows after the resume");
            Item whole = e.S.Find(SccmFeed.RunKey);
            T.Check(whole != null && whole.State == State.Succeeded, "SCCM resume: the followed run sees its end");
            T.Check(!((SccmFeed)e.Remote).NewerRunUtc.HasValue, "SCCM resume: a resume is not a newer run");
        }

        static void TwoRuns()
        {
            Site s = Resume();
            DateTime t = T0.AddHours(2);
            s.Add(t, 11140, Started, "", 0, "");
            s.Add(t, 11124, Group, "Prepare", 0, "Prepare");
            s.Add(t.AddMinutes(1), 11134, Done, "Set Variables", 1, "Prepare");
            List<List<Dictionary<string, object>>> runs = SccmCatalog.Runs(new List<Dictionary<string, object>>(s.Rows));
            T.Eq(2, runs.Count, "SCCM resume: a start after a completed run is a new run");

            // No end message, but the steps start again from the beginning.
            Site u = new Site();
            u.Add(T0, 11140, Started, "", 0, "");
            u.Add(T0.AddMinutes(1), 11134, Done, "Partition Disk", 1, "Prepare");
            u.Add(T0.AddMinutes(9), 11134, Done, "Apply Operating System", 2, "Install");
            u.Add(T0.AddMinutes(9), 11134, Done, "Apply Windows Settings", 3, "Install");
            u.Add(T0.AddMinutes(12), 11140, Started, "", 0, "");
            u.Add(T0.AddMinutes(12), 11124, Group, "Prepare", 0, "Prepare");
            u.Add(T0.AddMinutes(13), 11134, Done, "Partition Disk", 1, "Prepare");
            runs = SccmCatalog.Runs(u.Rows);
            T.Eq(2, runs.Count, "SCCM resume: steps that start again from the beginning make a new run");
            T.Eq(3, runs.Count == 2 ? runs[1].Count : 0, "SCCM resume: the new run holds its own rows");
        }

        static void Gaps()
        {
            Site s = new Site();
            s.Add(T0, 11140, Started, "", 0, "");
            s.Add(T0.AddMinutes(5), 11134, Done, "Install Contoso Agent", 4, "Install");
            s.Add(T0.AddHours(5), 11140, Started, "", 0, "");
            s.Add(T0.AddHours(5).AddMinutes(1), 11134, Done, "Install Contoso Tools", 6, "Install");
            T.Eq(2, SccmCatalog.Runs(s.Rows).Count, "SCCM resume: a start after more than 4 hours of silence is a new run");
        }

        // Six Run PowerShell Script steps, each with a warning 11910 just before it, Step 0 and no group.
        static void Bypass()
        {
            Site s = new Site();
            s.Add(T0, 11140, Started, "", 0, "");
            for (int i = 0; i < 6; i++)
            {
                DateTime t = T0.AddMinutes(1 + i);
                s.Add(t, 11910, null, "Run PowerShell Script", 0, "");
                s.Add(t.AddSeconds(20), 11134, Done, "Run PowerShell Script", 3 + i, "Configure");
            }
            s.Add(T0.AddMinutes(10), 11143, Completed, "", 0, "");
            SccmCatalog c = Catalog(s);
            List<RemoteDeployment> l = c.List(T0.AddHours(-1));
            if (l.Count != 1) { T.Check(false, "SCCM 11910: one run"); return; }
            T.Eq(State.Succeeded, l[0].State, "SCCM 11910: the run is completed in the list");
            Engine e = Watch(c, l[0]);
            Session x = e.S;
            T.Eq(7, x.Items.Count, "SCCM 11910: six steps and the run, no items of their own for the warnings");
            bool unknown = false, warned = true;
            foreach (Item i in x.Items)
            {
                if (i.State == State.Unknown || i.Key.StartsWith("ts:0:")) unknown = true;
                if (i.Key.StartsWith("ts:") && i.Name == "Run PowerShell Script")
                {
                    bool w = false;
                    foreach (TEvent ev in i.Events) if (ev.Severity == 1 && ev.Text == SccmLoose.BypassText) w = true;
                    if (!w || i.State != State.Succeeded || i.PhaseKey != "ts:group:configure") warned = false;
                }
            }
            T.Check(!unknown, "SCCM 11910: no Unknown item named after the step");
            T.Check(warned, "SCCM 11910: each step has its warning and stays in its group");
            Item whole = x.Find(SccmFeed.RunKey);
            T.Check(whole != null && whole.State == State.Succeeded && x.CountActive == 0 && x.Count(State.Pending) == 0, "SCCM 11910: the completed run is not kept waiting");
            Finder.Run(x, T0.AddMinutes(11), false);
            Finding f = null;
            foreach (Finding k in x.Findings) if (k.Kind == SccmLoose.FindingKind) f = k;
            T.Check(f != null && f.Severity == 0 && f.Title == "Runs PowerShell with execution policy Bypass", "SCCM 11910: a mild finding");

            // While the step has not reported yet, the warning waits; the run's end attaches it to the task sequence.
            Site w2 = new Site();
            w2.Add(T0, 11140, Started, "", 0, "");
            w2.Add(T0.AddMinutes(1), 11910, null, "Run PowerShell Script", 0, "");
            Batch b = new Batch();
            HashSet<string> seen = new HashSet<string>();
            SccmFeed.Map(SortedCopy(w2.Rows), seen, b);
            bool any = false;
            foreach (Obs o in b.Obs) if (o.ItemKey != null && o.ItemKey != SccmFeed.RunKey) any = true;
            T.Check(!any, "SCCM 11910: a warning before its step makes no item");
            w2.Add(T0.AddMinutes(2), 11143, Completed, "", 0, "");
            b = new Batch();
            SccmFeed.Map(SortedCopy(w2.Rows), seen, b);
            bool onRun = false;
            foreach (Obs o in b.Obs) if (o.ItemKey == SccmFeed.RunKey && o.Severity == 1 && o.State == null) onRun = true;
            T.Check(onRun, "SCCM 11910: without a step it is a warning on the task sequence");

            // Any other message without a name, step or group is an event, not an item.
            Site u = new Site();
            u.Add(T0, 11140, Started, "", 0, "");
            u.Add(T0.AddMinutes(1), 11999, null, "Format Disk", 0, "");
            u.Add(T0.AddMinutes(2), 11134, Done, "Format Disk", 2, "Prepare");
            b = new Batch();
            SccmFeed.Map(SortedCopy(u.Rows), new HashSet<string>(), b);
            int onStep = 0;
            foreach (Obs o in b.Obs) if (o.ItemKey == "ts:2:Format Disk") onStep++;
            T.Eq(2, onStep, "SCCM loose: an unknown message joins the step with the same name");
        }

        static List<Dictionary<string, object>> SortedCopy(List<Dictionary<string, object>> rows)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>(rows);
            SccmCatalog.SortRows(l);
            return l;
        }

        static Dictionary<string, object> App(Site s, DateTime t, long msg, string name, string guid, long exit)
        {
            Dictionary<string, object> r = s.Add(t, msg, msg == 11904 ? AppDone : AppFail, "Install Contoso Application Group", 0, "");
            r["ActionOutput"] = name + "ScopeId_1A2B3C4D-0000-4000-8000-00000000AB12/Application_" + guid;
            r["ExitCode"] = exit;
            return r;
        }

        static void Apps()
        {
            Site s = new Site();
            s.Add(T0, 11140, Started, "", 0, "");
            s.Add(T0.AddMinutes(1), 11138, Begin, "Install Contoso Application Group", 12, "Applications");
            App(s, T0.AddMinutes(3), 11904, "Contoso Notes", "11111111-2222-4333-8444-555555555501", 0);
            App(s, T0.AddMinutes(7), 11904, "Fabrikam Viewer 4.2", "11111111-2222-4333-8444-555555555502", 0);
            T.Eq(TsMessage.AppInstalled, TsMessage.Of(s.Rows[2]).Kind, "SCCM apps: 11904 is an installed application");
            SccmCatalog c = Catalog(s);
            List<RemoteDeployment> l = c.List(T0.AddHours(-1));
            if (l.Count != 1) { T.Check(false, "SCCM apps: one run"); return; }
            Engine e = Watch(c, l[0]);
            Session x = e.S;
            Item a1 = x.Find(SccmApps.Key("ScopeId_1A2B3C4D-0000-4000-8000-00000000AB12/Application_11111111-2222-4333-8444-555555555501"));
            Item a2 = x.Find(SccmApps.Key("ScopeId_1A2B3C4D-0000-4000-8000-00000000AB12/Application_11111111-2222-4333-8444-555555555502"));
            T.Check(a1 != null && a1.Name == "Contoso Notes" && a1.State == State.Succeeded, "SCCM apps: each application is its own item, done");
            T.Check(a2 != null && a2.PhaseKey == "ts:group:applications", "SCCM apps: under the group of its step");
            if (a2 != null) T.Eq((TimeSpan?)TimeSpan.FromMinutes(4), a2.Duration(T0.AddHours(1)), "SCCM apps: an application takes the time since the one before");
            Item step = x.Find("ts:12:Install Contoso Application Group");
            string last = step != null && step.Events.Count > 0 ? step.Events[step.Events.Count - 1].Text : "";
            T.Check(step != null && step.State == State.Installing && last.Contains("2 done") && last.Contains("Fabrikam Viewer 4.2"), "SCCM apps: the step says how many are done while it runs");
            bool unknown = false;
            foreach (Item i in x.Items) if (i.State == State.Unknown || i.Key.StartsWith("ts:0:")) unknown = true;
            T.Check(!unknown, "SCCM apps: no Unknown item named after the step");

            App(s, T0.AddMinutes(9), 11905, "Contoso Sync", "11111111-2222-4333-8444-555555555503", unchecked((uint)0x87D00324));
            s.Add(T0.AddMinutes(9).AddSeconds(5), 11135, "The task sequence execution engine failed executing an action.", "Install Contoso Application Group", 12, "Applications");
            e.Apply(e.Poll());
            Item a3 = e.S.Find(SccmApps.Key("ScopeId_1A2B3C4D-0000-4000-8000-00000000AB12/Application_11111111-2222-4333-8444-555555555503"));
            T.Check(a3 != null && a3.State == State.Failed && a3.ExitCode == unchecked((int)0x87D00324), "SCCM apps: 11905 fails the application with its code");
            T.Check(step != null && step.State == State.Failed, "SCCM apps: the step ends as it reports");
        }

        static void Newer()
        {
            Site s = Resume();
            SccmCatalog c = Catalog(s);
            List<RemoteDeployment> l = c.List(T0.AddHours(-1));
            if (l.Count != 1) return;
            Engine e = Watch(c, l[0]);
            SccmFeed feed = (SccmFeed)e.Remote;
            DateTime t = T0.AddHours(3);
            s.Add(t, 11140, Started, "", 0, "");
            s.Add(t.AddMinutes(1), 11134, Done, "Set Variables", 1, "Prepare");
            e.Apply(e.Poll());
            T.Eq((DateTime?)t, feed.NewerRunUtc, "SCCM newer run: the feed sees the newer run");
            T.Check(e.S.Find("ts:11:Write Last Step") != null && e.S.Find(SccmFeed.RunKey).State == State.Succeeded, "SCCM newer run: the followed run is unchanged");
            RemoteDeployment d = feed.NewerRun();
            T.Check(d != null && d.Key == Res + "|" + Adv + "|" + t.Ticks && d.Device == "CONTOSO-PC31", "SCCM newer run: the newer run has the key the list gives it");
            List<RemoteDeployment> again = c.List(T0.AddHours(-1));
            T.Check(again.Count == 2 && again[0].Key == d.Key, "SCCM newer run: the list of runs is the same as before");
            Engine n = Engine.ForRemote(feed.ForRun(t), d);
            n.Apply(n.Poll());
            T.Check(n.S.Find("ts:1:Set Variables") != null && n.S.Find("ts:11:Write Last Step") == null, "SCCM newer run: following it reads only the newer run");
            T.Eq("A newer run started at " + t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + ".", NewerRunText.Line(t, t.AddMinutes(5)), "SCCM newer run: the line");
        }
    }
}
