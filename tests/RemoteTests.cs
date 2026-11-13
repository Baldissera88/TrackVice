namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class RemoteTests
    {
        static RemoteDeployment Find(List<RemoteDeployment> l, string device)
        {
            foreach (RemoteDeployment d in l) if (d.Device == device) return d;
            return null;
        }

        static Session Watch(IRemoteCatalog c, RemoteDeployment d)
        {
            Engine e = Engine.ForRemote(c.Open(d), d);
            e.Apply(e.Poll());
            return e.S;
        }

        public static void Run()
        {
            Intune();
            Sccm();
            Messages();
        }

        // A tenant where Graph only answers for the list: one deployment by ID and its policy details are refused.
        sealed class ListOnlyGraph : IGraph
        {
            readonly IGraph inner;
            public ListOnlyGraph(IGraph g) { inner = g; }

            public Dictionary<string, object> Get(string url)
            {
                if (url != IntuneCatalog.EventsUrl) throw new GraphException("Resource not found for the segment.", 400);
                return inner.Get(url);
            }
        }

        static void IntuneListOnly()
        {
            IntuneCatalog c = new IntuneCatalog(new ListOnlyGraph(new DemoGraph(DateTime.UtcNow)));
            List<RemoteDeployment> l = c.List(DateTime.UtcNow.AddDays(-30));
            Session s = Watch(c, Find(l, "CONTOSO-LT0388"));
            T.Check(s.Items.Count >= 3, "Intune, list only: the deployment still shows its phases");
            Item whole = s.Find("intune:deployment");
            T.Check(whole != null && whole.State == State.Succeeded, "Intune, list only: the deployment as a whole");
            bool told = false;
            foreach (string n in s.Notes) if (n.Contains("could not be read")) told = true;
            T.Check(told, "Intune, list only: says the apps and policies could not be read");

            bool threw = false;
            try { new IntuneFeed(new ListOnlyGraph(new DemoGraph(DateTime.UtcNow)), "no-such-id").Read(DateTime.UtcNow); }
            catch (GraphException) { threw = true; }
            T.Check(threw, "Intune: an unknown deployment is an error, not an empty window");
        }

        static void Intune()
        {
            IntuneListOnly();
            IntuneCatalog c = new IntuneCatalog(new DemoGraph(DateTime.UtcNow));
            List<RemoteDeployment> l = c.List(DateTime.UtcNow.AddDays(-30));
            T.Eq(7, l.Count, "Intune: every deployment in 30 days");
            T.Check(l.Count > 1 && States.IsActive(l[0].State) && States.IsActive(l[1].State), "Intune: running deployments first");
            T.Eq(4, c.List(DateTime.UtcNow.AddHours(-24)).Count, "Intune: 24 hours");
            RemoteDeployment failed = Find(l, "CONTOSO-LT0399");
            T.Check(failed != null && failed.State == State.Failed && failed.Detail.Contains("0x80070643"), "Intune: failed deployment with details");

            Session s = Watch(c, Find(l, "CONTOSO-LT0421"));
            T.Eq("CONTOSO-LT0421", s.Computer, "Intune: device name");
            T.Eq("Contoso standard user-driven", s.GetInfo("Autopilot profile"), "Intune: profile");
            Item dev = null, teams = null, sheet = null;
            foreach (Item i in s.Items)
            {
                if (i.Name == "Device setup") dev = i;
                if (i.Name == "Microsoft Teams") teams = i;
                if (i.Name == "Contoso Timesheet") sheet = i;
            }
            T.Check(dev != null && dev.State == State.Succeeded, "Intune: device setup done");
            T.Eq((TimeSpan?)TimeSpan.FromMinutes(19), dev != null ? dev.Duration(DateTime.UtcNow) : null, "Intune: device setup took 19 min");
            T.Check(teams != null && teams.State == State.Pending && teams.PhaseKey == "intune:esp-apps", "Intune: app the ESP waits for, not installed yet");
            T.Check(sheet != null && sheet.PhaseKey == Phases.IntuneApps, "Intune: app outside the ESP");
            int events = s.Events.Count;

            // Reading the same state again adds nothing.
            SnapshotDiff diff = new SnapshotDiff();
            DemoGraph g = new DemoGraph(DateTime.UtcNow);
            string id = Find(l, "CONTOSO-LT0399").Key;
            Dictionary<string, object> ev = g.Get(IntuneCatalog.EventsUrl + "/" + id);
            List<Dictionary<string, object>> det = new List<Dictionary<string, object>>();
            foreach (object o in (List<object>)g.Get(IntuneCatalog.EventsUrl + "/" + id + "/policyStatusDetails")["value"]) det.Add((Dictionary<string, object>)o);
            Batch b1 = IntuneFeed.Map(ev, det, DateTime.UtcNow, diff);
            Batch b2 = IntuneFeed.Map(ev, det, DateTime.UtcNow, diff);
            T.Check(b1.Obs.Count > 3, "Intune: first read has observations");
            T.Eq(0, b2.Obs.Count, "Intune: an unchanged read adds nothing");
            Session fs = new Session();
            new Tracker(fs).ApplyAll(b1.Obs);
            Finder.Run(fs, DateTime.UtcNow, false);
            bool vpnFailed = false;
            foreach (Finding f in fs.Findings) if (f.Title.Contains("Contoso VPN Client") && f.Kind == FindingKinds.Failed) vpnFailed = true;
            T.Check(vpnFailed, "Intune: failed app becomes a finding");
            T.Check(events > 5, "Intune: events on the timeline");

            T.Eq((TimeSpan?)new TimeSpan(1, 2, 3), IntuneFeed.Dur("PT1H2M3S"), "ISO duration");
            T.Eq((TimeSpan?)null, IntuneFeed.Dur("PT0S"), "zero duration is no duration");
            T.Check(IntuneCatalog.Time(new Dictionary<string, object> { { "t", "0001-01-01T00:00:00Z" } }, "t") == null, "Graph empty time");
        }

        static void Sccm()
        {
            SccmCatalog c = new SccmCatalog(new DemoSccm(DateTime.UtcNow), "demo");
            c.Dmtf = delegate (DateTime t) { return t.ToString("yyyyMMddHHmmss") + ".000000+000"; };
            List<RemoteDeployment> l = c.List(DateTime.UtcNow.AddDays(-3));
            T.Eq(4, l.Count, "SCCM: four task sequence runs");
            RemoteDeployment run = Find(l, "CONTOSO-PC121"), done = Find(l, "CONTOSO-PC118"), fail = Find(l, "CONTOSO-PC117");
            T.Check(run != null && run.State == State.Installing && run.StateText.Contains("Setup Windows"), "SCCM: running, with its last step");
            T.Check(done != null && done.State == State.Succeeded, "SCCM: completed (11143)");
            T.Check(fail != null && fail.State == State.Failed, "SCCM: failed");
            T.Eq("Windows 11 24H2 Standard", run != null ? run.Profile : null, "SCCM: task sequence name");

            Session s = Watch(c, fail);
            T.Eq("CONTOSO-PC117", s.Computer, "SCCM: device");
            Item apps = s.Find("ts:11:Install Applications");
            T.Check(apps != null && apps.State == State.Failed && Errors.IsNotDetected(apps.ExitCode ?? 0), "SCCM: failed step with its code");
            Item skip = s.Find("ts:8:Install Language Pack fr-FR");
            T.Check(skip != null && skip.State == State.Skipped, "SCCM: skipped step");
            T.Check(s.FindPhase("ts:group:install windows") != null && s.FindPhase("ts:group:install windows").Items.Count == 4, "SCCM: groups become phases");
            T.Check((s.GetInfo("Task sequence result") ?? "").StartsWith("Failed"), "SCCM: task sequence failed");
            bool tsFinding = false;
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.TaskSequence) tsFinding = true;
            T.Check(tsFinding, "SCCM: task sequence failure finding");

            Session r = Watch(c, run);
            Item whole = r.Find(SccmFeed.RunKey);
            T.Check(whole != null && whole.State == State.Installing, "SCCM: a running task sequence shows as running");
            T.Check(r.CountActive == 1 && r.CountComplete < r.Items.Count, "SCCM: a running task sequence is not 100 % done");
            T.Eq(0, r.Findings.Count, "SCCM: a running task sequence has no findings");
            Item wholeFail = s.Find(SccmFeed.RunKey), wholeDone = Watch(c, done).Find(SccmFeed.RunKey);
            T.Check(wholeFail != null && wholeFail.State == State.Failed, "SCCM: the run fails with the task sequence");
            T.Check(wholeDone != null && wholeDone.State == State.Succeeded, "SCCM: the run completes with the task sequence");
            int runFindings = 0;
            foreach (Finding f in s.Findings) if (f.ItemKey == SccmFeed.RunKey) runFindings++;
            T.Eq(0, runFindings, "SCCM: the run item adds no second finding");

            Engine e = Engine.ForRemote(c.Open(done), done);
            e.Apply(e.Poll());
            int n = e.S.Events.Count;
            e.Apply(e.Poll());
            T.Eq(n, e.S.Events.Count, "SCCM: a second read adds no duplicates");
        }

        static void Messages()
        {
            Func<long, string, string, int> k = delegate (long id, string name, string action)
            {
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                r["LastStatusMsgID"] = id;
                r["LastStatusMsgName"] = name;
                r["ActionName"] = action;
                return TsMessage.Of(r).Kind;
            };
            T.Eq(TsMessage.TsDone, k(11143, "", ""), "11143 is done");
            T.Eq(TsMessage.StepDone, k(0, "The task sequence execution engine successfully completed an action.", "Format"), "step done");
            T.Eq(TsMessage.StepFailed, k(0, "The task sequence execution engine failed executing an action.", "Format"), "step failed");
            T.Eq(TsMessage.StepSkipped, k(0, "The task sequence execution engine skipped the action because the condition was evaluated to be false.", "Format"), "step skipped");
            T.Eq(TsMessage.TsFailed, k(0, "The task sequence execution engine failed execution of a task sequence.", ""), "task sequence failed");
            T.Eq(TsMessage.TsStarted, k(0, "The task sequence execution engine started execution of a task sequence.", ""), "task sequence started");
            T.Eq(TsMessage.Reboot, k(0, "The task sequence execution engine performed a reboot.", ""), "reboot");
        }
    }
}
