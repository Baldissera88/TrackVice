namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The apps a deployment is expected to bring: read from Intune with a made-up tenant behind a fake Graph, taken from earlier
    // deployments, and merged into a session as waiting items.
    public static class ExpectedAppsTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        const string Dev = "5fad6071-8293-4eaf-8ab1-3c4d5e6f7081", User = "70cf8293-a4b5-4c01-9cd3-5e6f70819203";
        const string A1 = "11111111-aaaa-4aaa-8aaa-000000000001", A2 = "11111111-aaaa-4aaa-8aaa-000000000002", A3 = "11111111-aaaa-4aaa-8aaa-000000000003";
        const string A4 = "11111111-aaaa-4aaa-8aaa-000000000004", A5 = "11111111-aaaa-4aaa-8aaa-000000000005", A6 = "11111111-aaaa-4aaa-8aaa-000000000006";

        sealed class FakeGraph : IGraph
        {
            public readonly Dictionary<string, string> Answers = new Dictionary<string, string>();
            public readonly Dictionary<string, int> Refused = new Dictionary<string, int>();
            public readonly List<string> Asked = new List<string>();

            public Dictionary<string, object> Get(string url)
            {
                Asked.Add(url);
                foreach (KeyValuePair<string, int> r in Refused)
                    if (url.StartsWith(r.Key, StringComparison.Ordinal)) throw new GraphException("Refused.", r.Value);
                string best = null;
                foreach (string k in Answers.Keys) if (url.StartsWith(k, StringComparison.Ordinal) && (best == null || k.Length > best.Length)) best = k;
                if (best == null) throw new GraphException("Access denied.", 403);
                return (Dictionary<string, object>)ImeJson.Parse(Answers[best]);
            }
        }

        static string App(string id, string name, string intent, string state)
        {
            return "{\"applicationId\":\"" + id + "\",\"displayName\":\"" + name + "\",\"displayVersion\":\"1.0\",\"mobileAppIntent\":\"" + intent + "\",\"installState\":\"" + state + "\"}";
        }

        static string IntentJson()
        {
            return "{\"id\":\"" + Dev + "\",\"managedDeviceIdentifier\":\"" + Dev + "\",\"userId\":\"" + User + "\",\"mobileAppList\":["
                + App(A1, "Microsoft 365 Apps", "requiredInstall", "notInstalled") + ","
                + App(A2.ToUpperInvariant(), "7-Zip", "requiredAndAvailableInstall", "pendingInstall") + ","
                + App(A3, "Company Portal", "requiredInstall", "installed") + ","
                + App(A4, "Paint.NET", "availableInstall", "notInstalled") + ","
                + App(A5, "Old VPN", "requiredUninstall", "installed") + ","
                + App(A6, "Not for Windows", "requiredInstall", "notApplicable") + ","
                + App(A1, "Microsoft 365 Apps", "requiredInstall", "notInstalled") + "]}";
        }

        static FakeGraph Tenant()
        {
            FakeGraph g = new FakeGraph();
            g.Answers[ExpectedIntune.DeviceUrl(Dev)] = "{\"id\":\"" + Dev + "\",\"deviceName\":\"PC1\",\"userId\":\"" + User + "\"}";
            g.Answers[ExpectedIntune.IntentsUrl(User, Dev)] = IntentJson();
            return g;
        }

        public static void Run()
        {
            Parsing();
            ReadingIntune();
            Refusals();
            History();
            Merging();
            Replacing();
            Counting();
            Finished();
            WhenDone();
            NotSaved();
            InTheEngine();
        }

        static ExpectedQuery ById()
        {
            ExpectedQuery q = new ExpectedQuery();
            q.IntuneDeviceId = Dev;
            q.DeviceName = "PC1";
            return q;
        }

        static void Parsing()
        {
            ExpectedSet set = ExpectedIntune.Parse((Dictionary<string, object>)ImeJson.Parse(IntentJson()));
            T.Eq(ExpectedSources.Intune, set.Source, "the source is Intune");
            List<string> ids = new List<string>();
            foreach (ExpectedApp a in set.Apps) ids.Add(a.Id + "=" + a.Assignment);
            T.Eq(A1 + "=Required|" + A2 + "=Required|" + A5 + "=Uninstall", string.Join("|", ids.ToArray()), "required apps, the required removal, not installed, available, or not applicable ones, and no repeats");
            T.Eq("7-Zip", set.Apps[1].Name, "the name comes from Intune");
            T.Eq(3, set.Names().Count, "names by ID");
            T.Eq("Expected: 3 apps (Intune)", set.Header, "the header of an Intune list");
            // A list of states, as the collection returns it.
            ExpectedSet inList = ExpectedIntune.Parse((Dictionary<string, object>)ImeJson.Parse("{\"value\":[" + IntentJson() + "]}"));
            T.Eq(3, inList.Apps.Count, "the first entry of a list is read the same way");
            T.Eq(0, ExpectedIntune.Parse((Dictionary<string, object>)ImeJson.Parse("{\"mobileAppList\":[]}")).Apps.Count, "an empty list");
            T.Eq(0, ExpectedIntune.Parse((Dictionary<string, object>)ImeJson.Parse("{}")).Apps.Count, "no list at all");
            T.Eq(AppGroups.Required, ExpectedIntune.AssignmentOf("RequiredInstall"), "intents are not case sensitive");
            T.Check(ExpectedIntune.AssignmentOf("available") == null && ExpectedIntune.AssignmentOf("exclude") == null, "available and excluded are not expected");
        }

        static void ReadingIntune()
        {
            FakeGraph g = Tenant();
            string note;
            ExpectedSet set = ExpectedIntune.Read(g, ById(), T0, out note);
            T.Check(set != null && set.Apps.Count == 3 && note == null, "the apps of the device, read by its Intune ID");
            T.Eq(2, g.Asked.Count, "two calls: the device and its apps");
            T.Check(g.Asked[1].Contains("/users/" + User + "/mobileAppIntentAndStates/" + Dev), "the apps are read for the user and the device");

            // By serial number, when the device does not know its Intune ID. The one that synced last is taken.
            g = new FakeGraph();
            g.Answers[ExpectedIntune.FindUrl("serialNumber", "SN1")] = "{\"value\":[{\"id\":\"old\",\"userId\":\"" + User + "\",\"lastSyncDateTime\":\"2026-01-01T00:00:00Z\"},{\"id\":\"" + Dev + "\",\"userId\":\"" + User + "\",\"lastSyncDateTime\":\"2026-11-01T00:00:00Z\"}]}";
            g.Answers[ExpectedIntune.IntentsUrl(User, Dev)] = IntentJson();
            ExpectedQuery q = new ExpectedQuery();
            q.Serial = "SN1";
            T.Eq(3, ExpectedIntune.Read(g, q, T0, out note).Apps.Count, "found by serial number");

            // By name when the serial number finds nothing, and the user from the UPN when the device has none yet.
            g = new FakeGraph();
            g.Answers[ExpectedIntune.FindUrl("serialNumber", "SN1")] = "{\"value\":[]}";
            g.Answers[ExpectedIntune.FindUrl("deviceName", "PC1")] = "{\"value\":[{\"id\":\"" + Dev + "\",\"userId\":\"00000000-0000-0000-0000-000000000000\",\"userPrincipalName\":\"anna@contoso.com\"}]}";
            g.Answers[ExpectedIntune.UserUrl("anna@contoso.com")] = "{\"id\":\"" + User + "\"}";
            g.Answers[ExpectedIntune.IntentsUrl(User, Dev)] = IntentJson();
            q = new ExpectedQuery();
            q.Serial = "SN1";
            q.DeviceName = "PC1";
            T.Eq(3, ExpectedIntune.Read(g, q, T0, out note).Apps.Count, "found by name, with the user looked up by UPN");

            // The entity is keyed differently: the user's list is read and the device picked from it.
            g = Tenant();
            g.Answers.Remove(ExpectedIntune.IntentsUrl(User, Dev));
            g.Refused[ExpectedIntune.IntentsUrl(User, Dev)] = 404;
            g.Answers[ExpectedIntune.IntentsListUrl(User)] = "{\"value\":[{\"managedDeviceIdentifier\":\"other\",\"mobileAppList\":[]}," + IntentJson().Replace("{\"id\"", "{\"id\"") + "]}";
            T.Eq(3, ExpectedIntune.Read(g, ById(), T0, out note).Apps.Count, "the list of the user when the entity is not found");

            g = new FakeGraph();
            g.Refused[ExpectedIntune.DeviceUrl(Dev)] = 404;
            g.Answers[ExpectedIntune.FindUrl("deviceName", "PC1")] = "{\"value\":[]}";
            T.Check(ExpectedIntune.Read(g, ById(), T0, out note) == null && note != null, "a device Intune does not have gives a note, not an error");
            T.Check(ExpectedIntune.Read(g, new ExpectedQuery(), T0, out note) == null && note != null, "nothing to find the device by gives a note");
        }

        static void Refusals()
        {
            FakeGraph g = new FakeGraph();
            string note = null;
            try { ExpectedIntune.Read(g, ById(), T0, out note); T.Check(false, "a refusal is raised"); }
            catch (GraphException ex)
            {
                T.Eq(403, ex.Status, "the refusal keeps its status");
                T.Check(ExpectedIntune.Problem(ex).Contains("DeviceManagementApps.Read.All"), "the problem names the permission");
            }
        }

        static HistoryRun Run(int daysAgo, params string[] apps)
        {
            HistoryRun r = new HistoryRun();
            r.Id = "run" + daysAgo;
            r.Customer = "Contoso";
            r.Kind = "Autopilot";
            r.Profile = "Standard";
            r.EndUtc = T0.AddDays(-daysAgo);
            r.Total = 1800;
            foreach (string a in apps) r.Items.Add(new HistoryValue { Key = a, Seconds = 60 });
            return r;
        }

        static string Names(ExpectedSet s)
        {
            List<string> l = new List<string>();
            foreach (ExpectedApp a in s.Apps) l.Add(a.Name + " " + a.Seen + "/" + a.Of);
            return string.Join("|", l.ToArray());
        }

        static void History()
        {
            string z = Kinds.Win32App + "|7-Zip", o = Kinds.Office + "|Microsoft 365 Apps", p = Kinds.Win32App + "|Paint.NET", f = Kinds.Win32App + "|FileZilla";
            string pol = Kinds.Policy + "|Some policy", sc = Kinds.Application + "|SCCM app";
            T.Check(ExpectedApps.FromHistory(null) == null, "no history, no list");
            T.Check(ExpectedApps.FromHistory(new List<HistoryRun> { Run(1, z) }) == null, "one earlier deployment is too few");
            // Two deployments: an app has to be in both (1 of 2 is 50 %, under 60 %).
            ExpectedSet two = ExpectedApps.FromHistory(new List<HistoryRun> { Run(1, z, o, p), Run(2, z, o, pol, sc) });
            T.Eq("7-Zip 2/2|Microsoft 365 Apps 2/2", Names(two), "of two, only what both had; policies and SCCM applications are not apps");
            // Five deployments: 3 of 5 is exactly 60 %, 2 of 5 is under.
            ExpectedSet five = ExpectedApps.FromHistory(new List<HistoryRun> { Run(1, z, o, p, f), Run(2, z, o, p), Run(3, z, o, p), Run(4, z, o, f), Run(5, z, o) });
            T.Eq("7-Zip 5/5|Microsoft 365 Apps 5/5|Paint.NET 3/5", Names(five), "60 % of five is enough, 40 % is not");
            T.Eq(5, five.Runs, "it knows how many deployments it is from");
            T.Eq("Expected: 3 apps (from 5 earlier deployments)", five.Header, "the header of a history list");
            T.Eq(AppGroups.Required, five.Apps[0].Assignment, "history apps are required");
            // Only the newest ten count.
            List<HistoryRun> many = new List<HistoryRun>();
            for (int d = 1; d <= 10; d++) many.Add(Run(d, z));
            many.Add(Run(11, z, f)); many.Add(Run(12, z, f)); many.Add(Run(13, z, f)); many.Add(Run(14, z, f));
            T.Eq("7-Zip 10/10", Names(ExpectedApps.FromHistory(many)), "only the newest ten are looked at");
            T.Check(ExpectedApps.FromHistory(new List<HistoryRun> { Run(1, pol), Run(2, pol) }) == null, "no apps in them, no list");

            // From the history of a customer and profile.
            DeploymentHistory h = new DeploymentHistory();
            h.Add(Run(1, z, o)); h.Add(Run(2, z, o)); h.Add(Run(3, z));
            HistoryRun other = Run(4, f); other.Customer = "Fabrikam"; h.Add(other);
            T.Eq(3, h.Recent("Contoso", "Autopilot", "Standard").Count, "the runs of the customer, newest first");
            T.Eq("7-Zip 3/3|Microsoft 365 Apps 2/3", Names(ExpectedApps.FromHistory(h.Recent("Contoso", "Autopilot", "Standard"))), "two of three is enough");
            T.Eq(0, h.Recent("Contoso", "SCCM", "Standard").Count, "another kind has none");
        }

        // A session of a user-driven Autopilot after the device phase: two device apps done, the user not signed in yet.
        static Session Device(DateTime now)
        {
            Session s = new Session();
            s.Computer = "PC1";
            foreach (string k in new string[] { Phases.DeviceSetup, Phases.AccountSetup, Phases.IntuneApps })
            {
                Phase p = new Phase();
                p.Key = k;
                p.Name = Phases.Name(k);
                p.Order = Phases.Order(k);
                s.Phases.Add(p);
            }
            Add(s, IntuneKeys.App(A1), "Contoso VPN", A1, Phases.DeviceSetup, State.Succeeded, now.AddMinutes(-20), now.AddMinutes(-5));
            Add(s, "ime", "Intune Management Extension", null, Phases.DeviceSetup, State.Succeeded, now.AddMinutes(-20), now.AddMinutes(-18));
            s.Rebuild();
            return s;
        }

        static Item Add(Session s, string key, string name, string id, string phase, State st, DateTime start, DateTime? end)
        {
            Item i = new Item();
            i.Key = key;
            i.Name = name;
            i.Kind = Kinds.Win32App;
            i.PhaseKey = phase;
            i.State = st;
            i.StartUtc = start;
            i.FirstUtc = start;
            i.EndUtc = end;
            i.LastUtc = end ?? start;
            i.Order = s.Items.Count + 1;
            if (id != null) i.Set("ID", id);
            s.Items.Add(i);
            s.Index(i);
            Phase p = s.FindPhase(phase);
            if (p != null) p.Items.Add(i);
            return i;
        }

        static ExpectedSet Intune(params string[] ids)
        {
            ExpectedSet set = new ExpectedSet();
            set.Source = ExpectedSources.Intune;
            foreach (string id in ids) set.Apps.Add(new ExpectedApp { Id = id, Name = "App " + id.Substring(id.Length - 1) });
            return set;
        }

        static void Merging()
        {
            DateTime now = T0;
            Session s = Device(now);
            int before = s.Items.Count;
            // The first app is on the device already (by its ID); the others are not.
            ExpectedSet set = Intune(A1, A2, A3, A4);
            int n = ExpectedApps.Merge(s, set, now, true);
            T.Eq(3, n, "three apps are waiting");
            T.Eq(before + 3, s.Items.Count, "they are items of the session");
            Phase apps = s.FindPhase(Phases.IntuneApps);
            T.Eq(3, apps.Items.Count, "in the Apps phase");
            T.Eq(0, apps.Done, "none of them done");
            foreach (Item i in apps.Items)
            {
                T.Eq(State.Pending, i.State, i.Name + " is waiting");
                T.Check(i.Expected && i.Key.StartsWith(ExpectedApps.KeyPrefix, StringComparison.Ordinal), i.Name + " is marked as expected");
                T.Eq("Intune", i.ExpectedSource, i.Name + " says where it comes from");
                T.Eq("Assigned in Intune, not on the device yet", i.Get(ExpectedApps.WhyKey), i.Name + " says why it is listed");
                T.Eq(0, i.Events.Count, i.Name + " has no timeline events");
                T.Eq(AppGroups.ExpectedApps, AppGroups.Group(i), i.Name + " has its own group");
                T.Eq("Expected", AppGroups.StateLabel(i), i.Name + " is labelled as expected");
            }
            T.Eq(0, s.Events.Count, "the timeline has none of them");
            T.Eq("Expected: 4 apps (Intune), 3 not on the device yet", s.ExpectedText, "the line for the Session tab");
            T.Eq(0, s.Count(State.Failed), "none is failed");
            T.Eq("Autopilot", s.Kind, "the kind of deployment does not change");
            // Merging again does not add them twice, and an empty or missing list takes them out.
            ExpectedApps.Merge(s, set, now, true);
            T.Eq(before + 3, s.Items.Count, "merging again changes nothing");
            T.Eq(0, ExpectedApps.Merge(s, null, now, true), "option off: no list");
            T.Eq(before, s.Items.Count, "option off: no placeholders");
            T.Check(s.ExpectedText == null && s.FindPhase(Phases.IntuneApps).Items.Count == 0, "option off: nothing left of them");
            T.Eq(0, ExpectedApps.Merge(s, new ExpectedSet(), now, true), "an empty list adds nothing");
            T.Eq(0, ExpectedApps.Merge(new Session(), set, now, true), "nothing is expected of a device that has shown nothing");
            // Uninstalls are listed too, with their own wording.
            ExpectedSet un = Intune(A5);
            un.Apps[0].Assignment = AppGroups.Uninstall;
            ExpectedApps.Merge(s, un, now, true);
            T.Eq("Assigned in Intune to be removed, not seen on the device yet", s.Find(ExpectedApps.Key(un.Apps[0])).Get(ExpectedApps.WhyKey), "an app to remove says so");
            // History placeholders say how often they were seen.
            ExpectedSet hist = ExpectedApps.FromHistory(new List<HistoryRun> { Run(1, Kinds.Win32App + "|FileZilla"), Run(2, Kinds.Win32App + "|FileZilla"), Run(3, Kinds.Win32App + "|FileZilla"), Run(4), Run(5) });
            T.Eq("FileZilla 3/5", hist == null ? "none" : Names(hist), "3 of 5 earlier deployments with the app is exactly 60 %");
        }

        static void Replacing()
        {
            DateTime now = T0;
            Session s = Device(now);
            ExpectedSet set = Intune(A2, A3, A4);
            set.Apps.Add(new ExpectedApp { Name = "FileZilla", Kind = Kinds.Win32App });
            ExpectedApps.Merge(s, set, now, true);
            T.Eq(4, s.FindPhase(Phases.IntuneApps).Items.Count, "four waiting");
            // A real item with the app's ID replaces its placeholder.
            Add(s, IntuneKeys.App(A3), "App 3", A3, Phases.IntuneApps, State.Installing, now, null).Order = 50;
            ExpectedApps.Merge(s, set, now, true);
            T.Eq(4, s.FindPhase(Phases.IntuneApps).Items.Count, "the real item and three placeholders");
            T.Check(!Order(s).Contains(ExpectedApps.KeyPrefix + A3), "the placeholder of the app that arrived is gone");
            T.Check(s.Find(IntuneKeys.App(A3)) != null && !s.Find(IntuneKeys.App(A3)).Expected, "the real item stays");
            // The real item sorts before the placeholders; the placeholders keep the order of the list.
            T.Eq(IntuneKeys.App(A3) + "|" + ExpectedApps.KeyPrefix + A2 + "|" + ExpectedApps.KeyPrefix + A4 + "|" + ExpectedApps.KeyPrefix + "name:filezilla", Order(s), "real items first, then the placeholders in list order");
            // By name when the ID is not known: a device that logged an app by name only, or an ID that differs.
            Add(s, "other", "filezilla", null, Phases.IntuneApps, State.Pending, now, null);
            ExpectedApps.Merge(s, set, now, true);
            T.Check(!Order(s).Contains("name:filezilla"), "an item with the same name replaces the placeholder");
            Item byId = Add(s, "intune:something", "Different name", A2, Phases.IntuneApps, State.Downloading, now, null);
            ExpectedApps.Merge(s, set, now, true);
            T.Check(!Order(s).Contains(ExpectedApps.KeyPrefix + A2), "an item whose ID is the app's replaces the placeholder, whatever its name");
            T.Check(!byId.Expected, "and is not touched");
            // A placeholder is never taken for a real item.
            T.Eq(1, CountExpected(s), "one placeholder left");
        }

        static string Order(Session s)
        {
            List<string> l = new List<string>();
            foreach (Item i in s.FindPhase(Phases.IntuneApps).Items) l.Add(i.Key);
            // Only the items of this test: the ones with an app ID or a placeholder.
            l.RemoveAll(delegate (string k) { return k == "other" || k == "intune:something" || k == "ime"; });
            return string.Join("|", l.ToArray());
        }

        static int CountExpected(Session s)
        {
            int n = 0;
            foreach (Item i in s.Items) if (i.Expected) n++;
            return n;
        }

        static void Counting()
        {
            DateTime now = T0;
            Session s = Device(now);
            double without = OverallProgress.Fraction(s, now, null);
            T.Check(without >= 0.99 - 1e-9 || without == 1, "without the option two of two is all done");
            T.Eq("2 of 2 done", s.FindPhase(Phases.DeviceSetup).Done + " of " + s.FindPhase(Phases.DeviceSetup).Items.Count + " done", "the device phase");
            ExpectedApps.Merge(s, Intune(A2, A3, A4, A5, A6), now, true);
            Phase apps = s.FindPhase(Phases.IntuneApps);
            T.Eq(5, apps.Items.Count, "five in the Apps phase");
            T.Eq("0 of 5 done", apps.Done + " of " + apps.Items.Count + " done", "the Apps phase counts them");
            T.Eq(7, s.Items.Count, "the session counts them");
            double with = OverallProgress.Fraction(s, now, null);
            // Two items of 300 s done, five of 300 s waiting: 2 of 7, as the whole list says from the start.
            T.Check(Math.Abs(with - 2.0 / 7.0) < 0.001, "the overall progress counts them as waiting (" + with + ")");
            T.Check(with < without, "the percentage does not jump up to 100 and down again");
            T.Check(TimeLeft.StillRuns(s), "the deployment still runs");
            DeploymentEstimate e = DeploymentEstimate.Make(s, now, TimeSpan.Zero, null);
            T.Check(e != null && e.Left >= TimeSpan.FromMinutes(24), "the time left covers five apps of about five minutes");
            Session plain = Device(now);
            T.Check(DeploymentEstimate.Make(plain, now, TimeSpan.Zero, null) == null, "without them nothing is left");
            // Expected times from history are used for the placeholders.
            ExpectedTimes times = new ExpectedTimes();
            times.Set(Kinds.Win32App, "App 2", 1200);
            T.Check(OverallProgress.Expected(s.Find(ExpectedApps.Key(new ExpectedApp { Id = A2 })), times) == 1200, "a placeholder is weighed by what that app took before");
            T.Eq(0, s.Count(State.Failed), "none failed");
            T.Eq(5, s.Count(State.Pending), "all waiting");
        }

        static void Finished(string source, bool expectFinding)
        {
            DateTime now = T0;
            Session s = Device(now);
            ExpectedSet set = source == ExpectedSources.Intune ? Intune(A1, A2, A3) : Intune(A1, A2, A3);
            set.Source = source;
            set.Runs = 5;
            foreach (ExpectedApp a in set.Apps) { a.Seen = 4; a.Of = 5; }
            // Done: everything seen is complete, the user phase was seen, and nothing came for 20 minutes.
            Item user = Add(s, "intune:user", "User app", A4, Phases.AccountSetup, State.Succeeded, now.AddMinutes(-40), now.AddMinutes(-30));
            user.Set(AppGroups.TargetKey, AppGroups.User);
            ExpectedApps.Merge(s, set, now.AddMinutes(20), true);
            T.Eq(0, CountExpected(s), source + ": a finished deployment lists no waiting apps");
            int findings = 0;
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.ExpectedMissing) findings++;
            T.Eq(expectFinding ? 2 : 0, findings, source + ": findings for the apps that never came");
            if (expectFinding)
            {
                T.Eq(1, s.Findings[0].Severity, "a warning");
                T.Check(s.Findings[0].Title.StartsWith("Not installed: ", StringComparison.Ordinal), "it says not installed");
                T.Check(s.ExpectedText.Contains("2 not seen"), "the line says how many were not seen");
            }
        }

        static void Finished()
        {
            Finished(ExpectedSources.Intune, true);
            Finished(ExpectedSources.History, false);
            // A failed deployment says nothing about what should have been there.
            DateTime now = T0;
            Session s = Device(now);
            Add(s, "intune:bad", "Bad", A4, Phases.IntuneApps, State.Failed, now.AddMinutes(-40), now.AddMinutes(-30));
            ExpectedApps.Merge(s, Intune(A2), now.AddHours(2), true);
            T.Eq(0, s.Findings.Count, "a failed deployment gets no not-installed finding");
            T.Eq(0, CountExpected(s), "and no waiting apps");
            // An uninstall that did not happen is not a finding.
            s = Device(now);
            ExpectedSet un = Intune(A2);
            un.Apps[0].Assignment = AppGroups.Uninstall;
            ExpectedApps.Merge(s, un, now.AddHours(2), true);
            T.Eq(0, s.Findings.Count, "an app to remove that was not seen is no finding");
            // A saved session was read once: it is done as it is.
            s = Device(now);
            ExpectedApps.Merge(s, Intune(A2), now, false);
            T.Eq(0, CountExpected(s), "a saved session has nothing more coming");
            T.Eq(1, s.Findings.Count, "and says what was never installed");
        }

        static void WhenDone()
        {
            DateTime now = T0;
            Session s = Device(now);
            // Watched, device phase done 5 minutes ago: not quiet long enough.
            T.Check(!ExpectedApps.IsDone(s, now, true), "5 minutes of quiet is not done");
            // 20 minutes of quiet, but an Autopilot device still waits for its user.
            T.Check(!ExpectedApps.IsDone(s, now.AddMinutes(20), true), "an Autopilot device that waits for its user is not done");
            T.Check(ExpectedApps.IsDone(s, now.AddMinutes(70), true), "after an hour the wait is over");
            T.Check(ExpectedApps.IsDone(s, now, false), "a saved session is done as it is");
            // Something runs: not done, whatever the time.
            Add(s, "intune:run", "Running", A4, Phases.IntuneApps, State.Installing, now, null);
            T.Check(!ExpectedApps.IsDone(s, now.AddHours(5), true), "an installing app means it is not done");
            T.Check(!ExpectedApps.IsDone(new Session(), now, true), "an empty session is not done");
            // The user phase seen: 15 minutes of quiet is enough.
            Session u = Device(now);
            Item x = Add(u, "intune:u", "User app", A4, Phases.AccountSetup, State.Succeeded, now.AddMinutes(-12), now.AddMinutes(-10));
            x.Set(AppGroups.TargetKey, AppGroups.User);
            T.Check(!ExpectedApps.IsDone(u, now, true), "10 minutes after the user apps is not yet");
            T.Check(ExpectedApps.IsDone(u, now.AddMinutes(10), true), "15 minutes after the user apps it is");
            // The waiting placeholders themselves do not keep a deployment from being done.
            ExpectedApps.Merge(u, Intune(A2), now, true);
            T.Eq(1, CountExpected(u), "one waits");
            ExpectedApps.Merge(u, Intune(A2), now.AddMinutes(30), true);
            T.Eq(0, CountExpected(u), "half an hour later it is given up");
        }

        static void NotSaved()
        {
            DateTime now = T0;
            Session s = Device(now);
            ExpectedApps.Merge(s, Intune(A2, A3), now, true);
            string json = s.ToJson();
            T.Check(!json.Contains(ExpectedApps.KeyPrefix) && !json.Contains("Expected"), "the saved or uploaded session has no placeholders");
            T.Eq(2, CountExpected(s), "and the viewer keeps them");
            T.Eq(2, s.FindPhase(Phases.IntuneApps).Items.Count, "also in the phase");
            Session back = Session.FromJson(json);
            T.Eq(0, back.FindPhase(Phases.IntuneApps).Items.Count, "a loaded session has none");
            // History does not learn from a deployment that still has placeholders.
            T.Check(DeploymentHistory.Measure(s, "Contoso", now.AddHours(1)) == null, "a session with waiting placeholders is not learned from");
        }

        static void InTheEngine()
        {
            Engine e = Engine.ForDemo("autopilot");
            e.Apply(e.DemoTo(20.5 * 60));
            int plain = e.S.Items.Count;
            T.Check(e.SetExpected(Demo.Autopilot().Expected), "the demo list changes the session");
            int waiting = CountExpected(e.S);
            T.Check(waiting >= 4, "the demo has waiting apps (" + waiting + ")");
            T.Eq(plain + waiting, e.S.Items.Count, "they are in the items");
            T.Check(e.S.ExpectedText != null && e.S.ExpectedText.StartsWith("Expected: 10 apps (Intune)", StringComparison.Ordinal), "the demo line");
            // New batches from the readers do not duplicate them, and the real items replace them as they come.
            e.Apply(e.DemoTo(24 * 60));
            int later = CountExpected(e.S);
            T.Check(later < waiting, "as the user apps arrive, their placeholders go (" + later + " of " + waiting + ")");
            T.Eq(later, CountExpected(e.S), "none twice");
            T.Check(!e.RefreshExpected(), "nothing changes while nothing happened");
            T.Check(e.SetExpected(null), "option off takes them out");
            T.Eq(0, CountExpected(e.S), "none left");
            e.Apply(e.DemoTo(30 * 60));
            T.Eq(0, CountExpected(e.S), "and new batches do not bring them back");
        }
    }
}
