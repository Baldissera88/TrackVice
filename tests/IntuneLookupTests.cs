namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // Names and configuration profiles from Intune, with a made-up tenant behind a fake Graph.
    // The remediation formats are built from public descriptions of the IME, not from a device.
    public static class IntuneLookupTests
    {
        const string AppA = "1b6f2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", AppB = "2c7a3d4e-5f60-4b7c-9d8e-0f1a2b3c4d5e";
        const string Rem = "3d8b4e5f-6071-4c8d-8e9f-1a2b3c4d5e6f", Script = "4e9c5f60-7182-4d9e-9fa0-2b3c4d5e6f70";
        const string Zero = "00000000-0000-0000-0000-000000000000";
        const string DevId = "5fad6071-8293-4eaf-8ab1-3c4d5e6f7081", OldDevId = "60be7182-93a4-4fb0-9bc2-4d5e6f708192";

        // Answers by the start of the URL. A URL with no answer is refused, as Graph does without the permission.
        sealed class FakeGraph : IGraph
        {
            public readonly Dictionary<string, string> Answers = new Dictionary<string, string>();
            public readonly List<string> Asked = new List<string>();

            public Dictionary<string, object> Get(string url)
            {
                Asked.Add(url);
                string best = null;
                foreach (string k in Answers.Keys) if (url.StartsWith(k, StringComparison.Ordinal) && (best == null || k.Length > best.Length)) best = k;
                if (best == null) throw new GraphException("Access denied.", 403);
                return (Dictionary<string, object>)ImeJson.Parse(Answers[best]);
            }
        }

        static string Item(string id, string name)
        {
            return "{\"id\":\"" + id + "\",\"displayName\":" + (name == null ? "null" : "\"" + name + "\"") + "}";
        }

        static FakeGraph Tenant()
        {
            FakeGraph g = new FakeGraph();
            g.Answers[IntuneNames.ScriptsUrl] = "{\"value\":[" + Item(Script, "Set time zone") + "]}";
            g.Answers[IntuneLookup.AppsUrl] = "{\"value\":[" + Item(AppA.ToUpperInvariant(), "Contoso VPN") + "," + Item("x", null) + "],\"@odata.nextLink\":\"https://graph.microsoft.com/beta/apps-page-2\"}";
            g.Answers["https://graph.microsoft.com/beta/apps-page-2"] = "{\"value\":[" + Item(AppB, "Contoso Notes") + "]}";
            g.Answers[IntuneLookup.RemediationsUrl] = "{\"value\":[" + Item(Rem, "Clear the Teams cache") + "]}";
            return g;
        }

        public static void Run()
        {
            Paging();
            OneFails();
            Applied();
            Remediations();
            Devices();
            Profiles();
        }

        static LookupChoice All()
        {
            LookupChoice c = new LookupChoice();
            c.Scripts = c.Apps = c.Remediations = true;
            return c;
        }

        static void Paging()
        {
            FakeGraph g = Tenant();
            Dictionary<string, string> apps = IntuneLookup.Names(g, IntuneLookup.AppsUrl);
            T.Eq(2, g.Asked.Count, "app names are read page by page");
            T.Eq(2, apps.Count, "an app without a name is left out");
            LookupChoice c = new LookupChoice();
            c.Apps = true;
            T.Eq(1, c.Scopes().Count, "only the ticked lookup's permission is asked for");
            T.Check(c.Scopes()[0].EndsWith("DeviceManagementApps.Read.All"), "apps need DeviceManagementApps.Read.All");
            c.Scripts = c.Remediations = true;
            T.Eq(2, c.Scopes().Count, "scripts and remediations share one permission");
        }

        static void OneFails()
        {
            FakeGraph g = Tenant();
            g.Answers.Remove(IntuneLookup.AppsUrl);
            LookupResult r = IntuneLookup.Run(g, All(), null, DateTime.UtcNow);
            T.Eq(1, r.Problems.Count, "one refused lookup is one problem");
            T.Check(r.Problems[0].Contains("DeviceManagementApps.Read.All"), "a refusal names the missing permission");
            T.Eq("Set time zone", r.Names[Script], "script names still read");
            T.Eq("Clear the Teams cache", r.Names[Rem], "remediation names still read");
            T.Eq(-1, r.Apps, "no app count when apps failed");
            GraphException other = new GraphException("Microsoft Graph answered HTTP 500.", 500);
            T.Check(IntuneLookup.Problem("App names", LookupChoice.AppScope, other).Contains("HTTP 500"), "other errors keep their message");
        }

        static Obs AppObs(string id, string name)
        {
            Obs o = new Obs();
            o.TimeUtc = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
            o.Source = "ESP";
            o.ItemKey = IntuneKeys.App(id);
            o.ItemName = name;
            o.WeakName = name != null;
            o.ItemKind = Kinds.Win32App;
            o.Origin = Origins.Intune;
            o.PhaseKey = Phases.IntuneApps;
            o.State = State.Installing;
            o.AddInfo("ID", id);
            return o;
        }

        static void Applied()
        {
            FakeGraph g = Tenant();
            LookupResult r = IntuneLookup.Run(g, All(), null, DateTime.UtcNow);
            Engine dev = Engine.ForDemo("autopilot");
            Batch b = new Batch();
            b.Obs.Add(AppObs(AppA, null));
            b.Obs.Add(AppObs(AppB, "Win32 app " + AppB.Substring(0, 8)));
            dev.Apply(b);
            T.Eq(IntuneKeys.App(AppA), dev.S.Find(IntuneKeys.App(AppA)).DisplayName, "an app is shown by its key until Intune is asked");
            T.Eq(2, dev.AddNames(r.Names), "both apps get their name");
            T.Eq("Contoso VPN", dev.S.Find(IntuneKeys.App(AppA)).Name, "IDs match whatever their case");
            T.Eq("Contoso Notes", dev.S.Find(IntuneKeys.App(AppB)).Name, "a weak name is replaced");
            Batch again = new Batch();
            again.Obs.Add(AppObs(AppB, "Win32 app " + AppB.Substring(0, 8)));
            dev.Apply(again);
            T.Eq("Contoso Notes", dev.S.Find(IntuneKeys.App(AppB)).Name, "a later weak name does not undo it");
        }

        static MemoryRegistry Device(int status)
        {
            MemoryRegistry reg = new MemoryRegistry();
            reg.Sz(ImeCollector.ReportsKey + "\\" + Zero + "\\" + Rem + "_1", "Result",
                "{\"Info\":{\"FirstDetectExitCode\":1,\"RemediationExitCode\":0,\"LastDetectExitCode\":0},\"RemediationStatus\":" + status + ",\"ErrorCode\":0}");
            reg.Sz(ImeCollector.ExecutionKey + "\\" + Zero + "\\" + Rem, "LastExecution", "2026-11-14T08:30:00.0000000Z");
            return reg;
        }

        static void Remediations()
        {
            DateTime now = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
            RemediationWatch w = new RemediationWatch();
            List<Obs> o = w.Read(Device(1), now);
            T.Eq(1, o.Count, "one remediation from the registry");
            T.Eq(RemediationKeys.Key(Rem), o[0].ItemKey, "keyed by its policy ID");
            T.Eq(State.Succeeded, o[0].State.Value, "an issue fixed is a success");
            T.Eq(0, w.Read(Device(1), now).Count, "the same state read again adds nothing");
            T.Eq(State.Failed, new RemediationWatch().Read(Device(3), now)[0].State.Value, "a remediation script error is a failure");

            HealthScriptsParser p = new HealthScriptsParser();
            List<Obs> lo = new List<Obs>();
            LogLine l = new LogLine();
            l.Text = "[HS] new result = {\"PolicyId\":\"" + Rem.ToUpperInvariant() + "\",\"UserId\":\"" + Zero + "\",\"Result\":3,\"Info\":{\"FirstDetectExitCode\":1,\"RemediationExitCode\":1,\"LastDetectExitCode\":1},\"RemediationStatus\":3,\"ErrorCode\":0}";
            l.Raw = l.Text;
            l.Utc = now;
            l.File = "HealthScripts.log";
            l.LineNo = 4;
            p.Read(l, lo);
            T.Eq(1, lo.Count, "a result line in HealthScripts.log");
            T.Eq(RemediationKeys.Key(Rem), lo[0].ItemKey, "the log and the registry name the same item");
            T.Eq(State.Failed, lo[0].State.Value, "a failed remediation from the log");
            l.Text = "[HS] inspect daily schedule for policy " + Rem + ", UTC = False, Interval = 1, Time = 08:00";
            lo.Clear();
            p.Read(l, lo);
            T.Eq(1, lo.Count, "a schedule line goes on the remediation's timeline");
            T.Check(!lo[0].State.HasValue, "a schedule line has no state");
            T.Check(LogParser.For("HealthScripts") is HealthScriptsParser, "HealthScripts.log is read");

            // A device session with the remediation, named by Intune, then replaced by a new upload from the device.
            Engine dev = Engine.ForDemo("autopilot");
            Batch b = new Batch();
            b.Obs.AddRange(new RemediationWatch().Read(Device(1), now));
            dev.Apply(b);
            T.Eq("Remediation " + Rem.Substring(0, 8), dev.S.Find(RemediationKeys.Key(Rem)).Name, "a remediation is named by its ID until Intune is asked");
            LookupResult r = IntuneLookup.Run(Tenant(), All(), null, now);
            T.Eq(1, dev.AddNames(r.Names), "the remediation gets its name");
            Engine fresh = Engine.ForDemo("autopilot");
            fresh.Apply(b);
            Batch upload = new Batch();
            upload.Replace = Session.FromJson(fresh.S.ToJson());
            dev.Apply(upload);
            T.Eq("Clear the Teams cache", dev.S.Find(RemediationKeys.Key(Rem)).Name, "the name survives a new upload from the device");
        }

        static string DeviceRow(string id, string sync)
        {
            return "{\"id\":\"" + id + "\",\"deviceName\":\"CONTOSO-PC01\",\"lastSyncDateTime\":\"" + sync + "\"}";
        }

        static void Devices()
        {
            string url = IntuneLookup.DeviceUrl("CONTOSO-PC01");
            T.Check(!url.Contains(" ") && Uri.UnescapeDataString(url).Contains("$filter=deviceName eq 'CONTOSO-PC01'"), "the filter is escaped");
            T.Check(Uri.UnescapeDataString(IntuneLookup.DeviceUrl("O'NEIL-PC")).Contains("'O''NEIL-PC'"), "a quote in a name is doubled");
            string note;
            FakeGraph g = new FakeGraph();
            g.Answers[IntuneLookup.DevicesUrl + "?"] = "{\"value\":[]}";
            T.Eq(null, IntuneLookup.FindDevice(g, "CONTOSO-PC01", out note), "no device with that name");
            T.Check(note != null && note.Contains("no device named CONTOSO-PC01"), "and the note says so");
            g.Answers[IntuneLookup.DevicesUrl + "?"] = "{\"value\":[" + DeviceRow(DevId, "2026-11-14T08:00:00Z") + "]}";
            T.Eq(DevId, IntuneLookup.FindDevice(g, "CONTOSO-PC01", out note), "one device with that name");
            T.Eq(null, note, "one match needs no note");
            g.Answers[IntuneLookup.DevicesUrl + "?"] = "{\"value\":[" + DeviceRow(OldDevId, "2026-10-01T08:00:00Z") + "," + DeviceRow(DevId, "2026-11-14T08:00:00Z") + "]}";
            T.Eq(DevId, IntuneLookup.FindDevice(g, "CONTOSO-PC01", out note), "with two matches the one that synced last");
            T.Check(note != null && note.Contains("2 devices named"), "and the note says there are two");
        }

        static void Profiles()
        {
            DateTime now = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
            FakeGraph g = Tenant();
            g.Answers[IntuneLookup.DevicesUrl + "?"] = "{\"value\":[" + DeviceRow(DevId, "2026-11-14T08:00:00Z") + "]}";
            g.Answers[IntuneLookup.ProfilesUrl(DevId)] = "{\"value\":["
                + "{\"id\":\"p1\",\"displayName\":\"Contoso Wi-Fi\",\"state\":\"compliant\",\"platformType\":\"windows10AndLater\",\"settingCount\":3},"
                + "{\"id\":\"p2\",\"displayName\":\"Contoso BitLocker\",\"state\":\"error\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/beta/profiles-page-2\"}";
            g.Answers["https://graph.microsoft.com/beta/profiles-page-2"] = "{\"value\":["
                + "{\"id\":\"p3\",\"displayName\":\"Contoso Edge\",\"state\":\"conflict\"},{\"id\":\"p4\",\"displayName\":\"Contoso macOS\",\"state\":\"notApplicable\"}]}";
            LookupChoice c = new LookupChoice();
            c.Profiles = true;
            LookupResult r = IntuneLookup.Run(g, c, "CONTOSO-PC01", now);
            T.Eq(0, r.Problems.Count, "profiles read without problems");
            T.Eq(4, r.Profiles.Count, "one item per profile, over two pages");
            T.Eq(State.Succeeded, r.Profiles[0].State.Value, "compliant is succeeded");
            T.Eq(State.Failed, r.Profiles[1].State.Value, "error is failed");
            T.Eq("Intune reports: Conflict", r.Profiles[2].Text, "conflict says so");
            T.Eq(State.NotApplicable, r.Profiles[3].State.Value, "not applicable");
            T.Eq(EvidenceKinds.Graph, r.Profiles[0].Evidence.Kind, "the evidence is Graph");

            Engine dev = Engine.ForDemo("autopilot");
            IntuneLookup.ApplyProfiles(dev, r.Profiles);
            Phase p = dev.S.FindPhase(IntuneLookup.ProfilePhase);
            T.Check(p != null && p.Name == "Configuration profiles", "a phase of their own");
            T.Eq(4, p == null ? 0 : p.Items.Count, "with every profile");
            Batch upload = new Batch();
            upload.Replace = Session.FromJson(Engine.ForDemo("autopilot").S.ToJson());
            dev.Apply(upload);
            T.Check(dev.S.Find(IntuneLookup.ProfileKey("p2")) != null, "profiles survive a new upload from the device");
            IntuneLookup.ApplyProfiles(dev, IntuneLookup.Run(g, c, "CONTOSO-PC01", now).Profiles);
            T.Eq(4, dev.Kept.Count, "a second read replaces the kept profiles");

            g.Answers.Remove(IntuneLookup.ProfilesUrl(DevId));
            LookupResult denied = IntuneLookup.Run(g, c, "CONTOSO-PC01", now);
            T.Check(denied.Problems.Count == 1 && denied.Problems[0].Contains("DeviceManagementConfiguration.Read.All"), "profiles name their permission when refused");
        }
    }
}
