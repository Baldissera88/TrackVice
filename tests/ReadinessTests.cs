namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The check before enrollment, against a made-up tenant behind a fake Graph, in the shapes Graph documents.
    public static class ReadinessTests
    {
        const string IdA = "11111111-2222-4333-8444-555555555501";
        const string EntraA = "21111111-2222-4333-8444-555555555501";
        const string GroupSales = "31111111-2222-4333-8444-555555555501", GroupOther = "31111111-2222-4333-8444-555555555502";
        const string AppVpn = "41111111-2222-4333-8444-555555555501", AppOffice = "41111111-2222-4333-8444-555555555502";
        const string EspSales = "51111111-2222-4333-8444-555555555501_Windows10EnrollmentCompletionPageConfiguration";
        const string EspOther = "51111111-2222-4333-8444-555555555502_Windows10EnrollmentCompletionPageConfiguration";
        const string EspDefault = "51111111-2222-4333-8444-555555555500_DefaultWindows10EnrollmentCompletionPageConfiguration";

        // Answers by the start of the URL; the longest match wins. A URL with no answer is refused.
        sealed class FakeGraph : IGraph
        {
            public readonly Dictionary<string, string> Answers = new Dictionary<string, string>();
            public readonly List<string> Asked = new List<string>();
            public int Refuse = 403;

            public Dictionary<string, object> Get(string url)
            {
                Asked.Add(url);
                string best = null;
                foreach (string k in Answers.Keys) if (url.StartsWith(k, StringComparison.Ordinal) && (best == null || k.Length > best.Length)) best = k;
                if (best == null) throw new GraphException("Access denied.", Refuse);
                return (Dictionary<string, object>)ImeJson.Parse(Answers[best]);
            }
        }

        static string Identity(string serial)
        {
            return "{\"@odata.type\":\"#microsoft.graph.windowsAutopilotDeviceIdentity\",\"id\":\"" + IdA + "\",\"serialNumber\":\"" + serial + "\",\"manufacturer\":\"Contoso\",\"model\":\"Laptop 7\"," +
                "\"groupTag\":\"Sales\",\"userPrincipalName\":\"\",\"deploymentProfileAssignmentStatus\":\"assignedInSync\",\"deploymentProfileAssignmentDetailedStatus\":\"none\"," +
                "\"enrollmentState\":\"notContacted\",\"lastContactedDateTime\":\"0001-01-01T00:00:00Z\",\"azureAdDeviceId\":\"" + EntraA + "\",\"azureActiveDirectoryDeviceId\":\"" + EntraA + "\"}";
        }

        static string Esp(string id, string name, int priority, string type, int timeout, string apps)
        {
            return "{\"@odata.type\":\"#microsoft.graph.windows10EnrollmentCompletionPageConfiguration\",\"id\":\"" + id + "\",\"displayName\":\"" + name + "\",\"priority\":" + priority +
                ",\"deviceEnrollmentConfigurationType\":\"" + type + "\",\"showInstallationProgress\":true,\"installProgressTimeoutInMinutes\":" + timeout +
                ",\"allowDeviceResetOnInstallFailure\":true,\"allowDeviceUseOnInstallFailure\":false,\"selectedMobileAppIds\":[" + apps + "]}";
        }

        static string Group(string type, string group)
        {
            return "{\"value\":[{\"@odata.type\":\"#microsoft.graph.enrollmentConfigurationAssignment\",\"id\":\"x\",\"target\":{\"@odata.type\":\"#microsoft.graph." + type + "\"" +
                (group != null ? ",\"groupId\":\"" + group + "\"" : "") + "}}]}";
        }

        static FakeGraph Tenant()
        {
            FakeGraph g = new FakeGraph();
            g.Answers[ReadinessCheck.FindUrl("CON1234567")] = "{\"value\":[" + Identity("CON1234567") + "," + Identity("CON12345678").Replace(IdA, "other") + "]}";
            g.Answers[ReadinessCheck.ProfileUrl(IdA, false)] = "{\"@odata.type\":\"#microsoft.graph.azureADWindowsAutopilotDeploymentProfile\",\"id\":\"p1\",\"displayName\":\"Contoso user-driven\"," +
                "\"deviceNameTemplate\":\"CONTOSO-%SERIAL%\",\"preprovisioningAllowed\":true,\"outOfBoxExperienceSetting\":{\"deviceUsageType\":\"singleUser\",\"userType\":\"standard\"}}";
            g.Answers[ReadinessCheck.ConfigsUrl] = "{\"value\":[" +
                Esp(EspOther, "Other ESP", 1, "windows10EnrollmentCompletionPageConfiguration", 30, "") + "," +
                Esp(EspSales, "Sales ESP", 2, "windows10EnrollmentCompletionPageConfiguration", 40, "\"" + AppVpn + "\",\"" + AppOffice + "\"") + "," +
                Esp(EspDefault, "All users and all devices", 0, "defaultWindows10EnrollmentCompletionPageConfiguration", 60, "") + "," +
                "{\"@odata.type\":\"#microsoft.graph.deviceEnrollmentLimitConfiguration\",\"id\":\"limit\",\"displayName\":\"Limit\",\"priority\":0}]}";
            g.Answers[ReadinessCheck.AssignmentsUrl(EspSales)] = Group("groupAssignmentTarget", GroupSales);
            g.Answers[ReadinessCheck.AssignmentsUrl(EspOther)] = Group("groupAssignmentTarget", GroupOther);
            g.Answers[ReadinessCheck.AppUrl + AppVpn] = "{\"id\":\"" + AppVpn + "\",\"displayName\":\"Contoso VPN\"}";
            g.Answers[ReadinessCheck.AppUrl + AppOffice] = "{\"id\":\"" + AppOffice + "\",\"displayName\":\"Contoso Office\"}";
            g.Answers[ReadinessCheck.GroupsUrl(EntraA)] = "{\"value\":[{\"@odata.type\":\"#microsoft.graph.group\",\"id\":\"" + GroupSales + "\",\"displayName\":\"Autopilot - Sales\"}]}";
            return g;
        }

        public static void Run()
        {
            Serials();
            Names();
            Registered();
            NotRegistered();
            GroupsUnknown();
            NoPermission();
            Picking();
            Predicted();
            Exports();
            Demo();
            Listed();
        }

        static ReadyLine Line(ReadyDevice d, string check)
        {
            foreach (ReadyLine l in d.Lines) if (l.Check == check) return l;
            return null;
        }

        static void Serials()
        {
            List<string> l = ReadyLines.ParseSerials("CON1\r\n\r\ncon1\nCON2\n");
            T.Eq(2, l.Count, "serials one per line, without empty lines and doubles");
            l = ReadyLines.ParseSerials("Device Serial Number,Windows Product ID,Hardware Hash\r\nCON3,,T0FBQUFB\r\n\"CON4\",,T0FBQUFC\r\n");
            T.Eq(2, l.Count, "a hardware hash CSV gives its serial number column");
            T.Eq("CON4", l[1], "quotes are taken off a serial number");
            l = ReadyLines.ParseSerials("PC-A;Sales\nPC-B;Sales");
            T.Eq("PC-B", l[1], "without a header the first value of a line is the serial number");
        }

        static void Names()
        {
            T.Eq("CONTOSO-CON1234567", ReadinessCheck.Name("CONTOSO-%SERIAL%", "CON1234567"), "the name template takes the serial number");
            T.Eq(10, ReadinessCheck.NameLength("KIOSK%RAND:5%", "X"), "a random part counts its digits");
        }

        static void Registered()
        {
            FakeGraph g = Tenant();
            ReadinessCheck c = new ReadinessCheck(g);
            ReadyDevice d = c.Check("CON1234567");
            T.Eq(ReadyLevel.Ok, Line(d, "Registered in Windows Autopilot").Level, "a serial number Autopilot has is registered, not the longer one that contains it");
            T.Eq("Contoso Laptop 7", d.Title, "the device is shown by its manufacturer and model");
            T.Check(Line(d, "Group tag").Text.StartsWith("Sales"), "the group tag is shown");
            T.Eq(ReadyLevel.Ok, Line(d, "Entra device object").Level, "the Entra device object is there");
            T.Check(Line(d, "Name").Text.StartsWith("Contoso user-driven"), "the deployment profile is named");
            T.Check(Line(d, "Join type").Text.StartsWith("Microsoft Entra joined"), "an Entra profile is Entra joined");
            T.Check(Line(d, "Deployment mode").Text.StartsWith("User-driven, and pre-provisioning is allowed"), "user-driven with pre-provisioning");
            T.Eq(ReadyLevel.Warning, Line(d, "Device name").Level, "a name over 15 characters is a warning");
            ReadyLine esp = Line(d, "Which ESP applies");
            T.Check(esp.Text.StartsWith("Sales ESP (priority 2): assigned to the group Autopilot - Sales"), "the ESP of the device's group applies, not one of a group it is not in: " + esp.Text);
            T.Check(Line(d, "Time limit").Text.StartsWith("40 minutes"), "the ESP time limit is shown");
            T.Check(Line(d, "Blocking apps").Text.Contains("Contoso VPN, Contoso Office"), "the blocking apps have their names");
            T.Check(Line(d, "Reset on failure").Text.StartsWith("Allowed"), "reset on failure is shown");
            T.Check(Line(d, "Use the device anyway").Text.StartsWith("Not allowed"), "use anyway is shown");
            T.Check(Line(d, "Earlier enrollments").Text.Contains("No upload location"), "without uploads there is no prediction");
            int before = g.Asked.Count;
            c.Check("CON1234567");
            int esps = 0;
            foreach (string u in g.Asked.GetRange(before, g.Asked.Count - before)) if (u.StartsWith(ReadinessCheck.ConfigsUrl, StringComparison.Ordinal) || u.StartsWith(ReadinessCheck.AppUrl, StringComparison.Ordinal)) esps++;
            T.Eq(0, esps, "the ESPs and app names are read once for all devices");
        }

        static void NotRegistered()
        {
            FakeGraph g = Tenant();
            g.Answers[ReadinessCheck.FindUrl("CON9")] = "{\"value\":[" + Identity("CON1234567") + "]}";
            ReadyDevice d = new ReadinessCheck(g).Check("CON9");
            T.Eq(1, d.Lines.Count, "a device that is not registered has one line");
            T.Eq(ReadyLevel.Problem, d.Worst, "not registered is a problem");
            T.Check(d.Lines[0].Text.Contains("CON1234567"), "serial numbers that only contain it are named");
        }

        static void GroupsUnknown()
        {
            FakeGraph g = Tenant();
            g.Answers.Remove(ReadinessCheck.GroupsUrl(EntraA));
            ReadyDevice d = new ReadinessCheck(g).Check("CON1234567");
            T.Check(Line(d, "Which ESP applies").Text.StartsWith("All users and all devices"), "without the groups, the default ESP applies by default");
            ReadyLine t = Line(d, "Targeted ESPs");
            T.Check(t != null && t.Level == ReadyLevel.Warning && t.Text.Contains("Device.Read.All") && t.Text.Contains("Other ESP") && t.Text.Contains("Sales ESP"), "targeted ESPs that could not be resolved are named, with the permission");
        }

        static void NoPermission()
        {
            FakeGraph g = new FakeGraph();
            ReadyDevice d = new ReadinessCheck(g).Check("CON1234567");
            T.Eq(ReadyLevel.Problem, d.Worst, "without the permission the device check fails");
            T.Check(d.Lines[0].Text.Contains("DeviceManagementServiceConfig.Read.All"), "the missing permission is named: " + d.Lines[0].Text);

            g = Tenant();
            g.Answers.Remove(ReadinessCheck.AppUrl + AppVpn);
            g.Answers.Remove(ReadinessCheck.AppUrl + AppOffice);
            d = new ReadinessCheck(g).Check("CON1234567");
            ReadyLine names = Line(d, "App names");
            T.Check(names != null && names.Text.Contains("DeviceManagementApps.Read.All"), "app names fail on their own and name their permission");
            T.Check(Line(d, "Blocking apps").Text.StartsWith("2 apps"), "the blocking apps are still counted");

            g = Tenant();
            g.Refuse = 404;
            g.Answers.Remove(ReadinessCheck.AppUrl + AppOffice);
            d = new ReadinessCheck(g).Check("CON1234567");
            T.Check(Line(d, "Deleted apps") != null, "a blocking app that was deleted from Intune is a warning");
        }

        static EspConfig Config(string name, int prio, string kind, string group)
        {
            EspConfig c = new EspConfig();
            c.Id = name;
            c.Name = name;
            c.Priority = prio;
            if (kind != null)
            {
                EspTarget t = new EspTarget();
                t.Kind = kind;
                t.GroupId = group;
                c.Targets.Add(t);
            }
            return c;
        }

        static void Picking()
        {
            EspConfig def = Config("Default", 0, null, null);
            def.IsDefault = true;
            List<EspConfig> l = new List<EspConfig>();
            l.Add(def);
            l.Add(Config("Users", 1, EspTarget.AllUsers, null));
            l.Add(Config("Devices", 3, EspTarget.AllDevices, null));
            l.Add(Config("Group", 2, EspTarget.Group, GroupSales));
            Dictionary<string, string> none = new Dictionary<string, string>();
            Dictionary<string, string> sales = new Dictionary<string, string>();
            sales[GroupSales] = "Sales";
            T.Eq("Group", ReadinessCheck.Pick(l, sales, true).Config.Name, "the highest priority ESP assigned to the device wins");
            T.Eq("Devices", ReadinessCheck.Pick(l, none, true).Config.Name, "an ESP for the device comes before one for users");
            l.RemoveAt(2);
            T.Eq("Users", ReadinessCheck.Pick(l, none, true).Config.Name, "with no ESP for the device, the one for users applies");
            T.Eq("Default", ReadinessCheck.Pick(l, none, false).Config.Name, "self-deploying ignores ESPs for users");
            EspPick p = ReadinessCheck.Pick(l, null, true);
            T.Check(p.Unresolved && p.Config.Name == "Users", "with unknown groups the pick is what applies by default and is marked unresolved");
            EspConfig ex = Config("Excluded", 0, EspTarget.AllDevices, null);
            EspTarget t = new EspTarget();
            t.Kind = EspTarget.Exclude;
            t.GroupId = GroupSales;
            ex.Targets.Add(t);
            l.Add(ex);
            T.Eq("Group", ReadinessCheck.Pick(l, sales, true).Config.Name, "an excluded group keeps an ESP away");
        }

        static Session Run(string computer, DateTime start, int vpn, int office, bool vpnFailed)
        {
            Session s = new Session();
            s.Computer = computer;
            AddItem(s, "Contoso VPN", start, vpn, vpnFailed);
            AddItem(s, "Contoso Office", start.AddSeconds(vpn), office, false);
            return s;
        }

        static void AddItem(Session s, string name, DateTime start, int seconds, bool failed)
        {
            Item i = new Item();
            i.Key = name;
            i.Name = name;
            i.Kind = Kinds.Win32App;
            i.PhaseKey = Phases.DeviceSetup;
            i.State = failed ? State.Failed : State.Succeeded;
            i.FirstUtc = start;
            i.StartUtc = start;
            i.EndUtc = start.AddSeconds(seconds);
            i.LastUtc = i.EndUtc.Value;
            s.Items.Add(i);
        }

        static void Predicted()
        {
            DateTime t = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
            List<Session> h = new List<Session>();
            h.Add(Run("CONTOSO-PC01", t, 600, 1500, false));
            h.Add(Run("CONTOSO-PC02", t, 300, 1500, true));
            h.Add(Run("CONTOSO-PC03", t, 900, 1800, false));
            Prediction p = Prediction.Make(h, FleetStats.Build(h, t), new List<string>(new string[] { "Contoso VPN", "Contoso Office" }), t);
            T.Eq(3, p.Runs, "every enrollment with a blocking app counts");
            T.Eq(2100.0, p.MedianSumSeconds, "the median of the summed times of the blocking apps");

            FakeGraph g = Tenant();
            ReadinessCheck c = new ReadinessCheck(g);
            c.History = h;
            c.NowUtc = t;
            ReadyDevice d = c.Check("CON1234567");
            T.Eq(ReadyLevel.Warning, LastLimit(d).Level, "35 minutes of blocking apps is over 75 % of a 40 minute limit");
            ReadyLine vpn = Line(d, "Contoso VPN");
            T.Check(vpn != null && vpn.Level == ReadyLevel.Warning && vpn.Text.StartsWith("Failed on 1 of 3"), "an app that failed on more than 10 % of enrollments is a warning");
            T.Check(Line(d, "Contoso Office") == null, "an app that did not fail is not listed as failing");
            T.Check(Line(d, "Blocking apps") != null, "the typical times are shown");
        }

        // The time limit line of the prediction, which comes after the one of the ESP.
        static ReadyLine LastLimit(ReadyDevice d)
        {
            ReadyLine last = null;
            foreach (ReadyLine l in d.Lines) if (l.Check == "Time limit" && l.Group == ReadyLines.Prediction) last = l;
            return last;
        }

        static void Exports()
        {
            ReadyDevice d = new ReadinessCheck(Tenant()).Check("CON1234567");
            List<ReadyDevice> l = new List<ReadyDevice>();
            l.Add(d);
            string csv = ReadyLines.ToCsv(l);
            T.Check(csv.StartsWith("Serial number,Device,Part,Check,Result,Details"), "the CSV has a header");
            T.Eq(d.Lines.Count + 2, csv.Split('\n').Length, "one CSV line per check");
            string html = Report.ReadinessHtml(l, "Contoso", DateTime.UtcNow);
            T.Check(html.Contains("CON1234567") && html.Contains("Sales ESP"), "the HTML report has the device and its checks");
        }

        static void Demo()
        {
            DateTime now = DateTime.UtcNow;
            ReadinessCheck c = new ReadinessCheck(new DemoReadinessGraph(now));
            c.History = DemoReadinessGraph.History(now);
            List<ReadyDevice> l = c.Run(ReadyLines.ParseSerials(DemoReadinessGraph.Serials), null);
            T.Eq(5, l.Count, "the demo checks five devices");
            T.Eq(ReadyLevel.Warning, l[0].Worst, "the first demo device has warnings from the prediction");
            T.Eq(ReadyLevel.Problem, l[2].Worst, "the demo device without a profile has a problem");
            T.Eq(ReadyLevel.Problem, l[4].Worst, "the demo device that is not registered has a problem");
            T.Check(Line(l[3], "Which ESP applies").Text.StartsWith("Contoso ESP - kiosks"), "the kiosk gets the ESP of its group");
        }

        static void Listed()
        {
            FakeGraph g = new FakeGraph();
            g.Answers[ReadinessCheck.IdentitiesUrl] = "{\"value\":[" + Identity("CON2") + "],\"@odata.nextLink\":\"" + ReadinessCheck.IdentitiesUrl + "?$skiptoken=page2\"}";
            g.Answers[ReadinessCheck.IdentitiesUrl + "?$skiptoken=page2"] = "{\"value\":[" + Identity("CON1") + "," + Identity("") + "]}";
            List<AutopilotEntry> l = ReadinessCheck.ListIdentities(g, 100);
            T.Eq(2, l.Count, "the Autopilot list is read page by page, without devices that have no serial number");
            T.Eq("CON1", l[0].Serial, "the Autopilot list is sorted by serial number");
            T.Eq("Assigned in sync", l[0].Profile, "the profile assignment is shown in words");
            T.Eq(1, ReadinessCheck.ListIdentities(g, 1).Count, "the Autopilot list stops at the most it may hold");
        }
    }
}
