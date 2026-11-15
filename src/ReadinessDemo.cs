namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // A made-up Contoso tenant for the check before enrollment. It answers the same Graph calls as Intune, in the
    // documented shapes, so the demo and the screenshots go through the same code as a real tenant.
    public sealed class DemoReadinessGraph : IGraph
    {
        public const string Serials = "5CG4321ABC\r\n5CG4321AA7\r\n5CG4321B02\r\n8CC2207KQ1\r\n5CG9999XYZ";

        const string ProfileUser = "7c1e2b3a-0d4f-4e5a-9b6c-1d2e3f4a5b01", ProfileKiosk = "7c1e2b3a-0d4f-4e5a-9b6c-1d2e3f4a5b02";
        const string GroupSales = "c0a80001-1111-4a2b-8c3d-000000000001", GroupKiosk = "c0a80001-1111-4a2b-8c3d-000000000002", GroupAll = "c0a80001-1111-4a2b-8c3d-000000000003";
        const string AppPortal = "a0000000-0000-4000-8000-000000000001", AppVpn = "a0000000-0000-4000-8000-000000000002";
        const string AppOffice = "a0000000-0000-4000-8000-000000000003", AppReader = "a0000000-0000-4000-8000-000000000004";
        const string AppGone = "a0000000-0000-4000-8000-0000000000ff";

        readonly DateTime now;
        readonly List<Dictionary<string, object>> identities = new List<Dictionary<string, object>>();

        public DemoReadinessGraph(DateTime nowUtc)
        {
            now = nowUtc;
            identities.Add(Identity("e1000000-0000-4000-8000-000000000001", "5CG4321ABC", "HP", "EliteBook 840 G10", "Sales", "mads.jensen@contoso.com", "assignedInSync", "notContacted", "d1000000-0000-4000-8000-000000000001", ProfileUser, 2));
            identities.Add(Identity("e1000000-0000-4000-8000-000000000002", "5CG4321AA7", "HP", "EliteBook 840 G10", "Sales", null, "assignedOutOfSync", "enrolled", "d1000000-0000-4000-8000-000000000002", "7c1e2b3a-0d4f-4e5a-9b6c-1d2e3f4a5b03", 30));
            identities.Add(Identity("e1000000-0000-4000-8000-000000000003", "5CG4321B02", "HP", "EliteBook 860 G10", null, null, "notAssigned", "notContacted", "00000000-0000-0000-0000-000000000000", null, -1));
            identities.Add(Identity("e1000000-0000-4000-8000-000000000004", "8CC2207KQ1", "Lenovo", "ThinkCentre M70q Gen 4", "Kiosk", null, "assignedInSync", "notContacted", "d1000000-0000-4000-8000-000000000004", ProfileKiosk, 5));
            identities.Add(Identity("e1000000-0000-4000-8000-000000000005", "5CG4321ABD", "HP", "EliteBook 840 G10", "Sales", "sofie.lund@contoso.com", "assignedInSync", "enrolled", "d1000000-0000-4000-8000-000000000005", ProfileUser, 1));
        }

        static string Iso(DateTime t)
        {
            return t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        static Dictionary<string, object> D(params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static List<object> L(params object[] items)
        {
            return new List<object>(items);
        }

        Dictionary<string, object> Identity(string id, string serial, string make, string model, string tag, string user, string assignment, string state, string entra, string profile, double daysAgo)
        {
            Dictionary<string, object> d = D("@odata.type", "#microsoft.graph.windowsAutopilotDeviceIdentity", "id", id, "serialNumber", serial, "manufacturer", make, "model", model,
                "groupTag", tag ?? "", "userPrincipalName", user ?? "", "deploymentProfileAssignmentStatus", assignment, "deploymentProfileAssignmentDetailedStatus", "none",
                "enrollmentState", state, "azureAdDeviceId", entra, "azureActiveDirectoryDeviceId", entra,
                "lastContactedDateTime", daysAgo >= 0 ? Iso(now.AddDays(-daysAgo)) : "0001-01-01T00:00:00Z",
                "deploymentProfileAssignedDateTime", profile != null ? Iso(now.AddDays(-12)) : "0001-01-01T00:00:00Z");
            d["profile"] = profile;
            return d;
        }

        Dictionary<string, object> Profile(string id)
        {
            if (id == ProfileUser)
                return D("@odata.type", "#microsoft.graph.azureADWindowsAutopilotDeploymentProfile", "id", id, "displayName", "Contoso standard user-driven",
                    "deviceNameTemplate", "CON-%SERIAL%", "preprovisioningAllowed", true, "enableWhiteGlove", true,
                    "outOfBoxExperienceSetting", D("deviceUsageType", "singleUser", "userType", "standard"));
            if (id == ProfileKiosk)
                return D("@odata.type", "#microsoft.graph.azureADWindowsAutopilotDeploymentProfile", "id", id, "displayName", "Contoso kiosk self-deploying",
                    "deviceNameTemplate", "KIOSK-%RAND:5%", "preprovisioningAllowed", false, "enableWhiteGlove", false,
                    "outOfBoxExperienceSetting", D("deviceUsageType", "shared", "userType", "standard"));
            return D("@odata.type", "#microsoft.graph.azureADWindowsAutopilotDeploymentProfile", "id", id, "displayName", "Contoso field sales",
                "deviceNameTemplate", "CONTOSO-%SERIAL%", "preprovisioningAllowed", false, "enableWhiteGlove", false,
                "outOfBoxExperienceSetting", D("deviceUsageType", "singleUser", "userType", "standard"));
        }

        static Dictionary<string, object> Esp(string id, string name, int priority, bool def, int timeout, List<object> apps, bool reset, bool use)
        {
            return D("@odata.type", "#microsoft.graph.windows10EnrollmentCompletionPageConfiguration", "id", id, "displayName", name, "priority", (long)priority,
                "deviceEnrollmentConfigurationType", def ? "defaultWindows10EnrollmentCompletionPageConfiguration" : "windows10EnrollmentCompletionPageConfiguration",
                "showInstallationProgress", true, "installProgressTimeoutInMinutes", (long)timeout, "selectedMobileAppIds", apps,
                "allowDeviceResetOnInstallFailure", reset, "allowDeviceUseOnInstallFailure", use, "blockDeviceSetupRetryByUser", false);
        }

        static Dictionary<string, object> Target(string type, string group)
        {
            Dictionary<string, object> t = D("@odata.type", "#microsoft.graph." + type);
            if (group != null) t["groupId"] = group;
            return D("@odata.type", "#microsoft.graph.enrollmentConfigurationAssignment", "id", Guid.NewGuid().ToString(), "target", t);
        }

        public Dictionary<string, object> Get(string url)
        {
            if (url == ReadinessCheck.IdentitiesUrl)
            {
                List<object> all = new List<object>();
                foreach (Dictionary<string, object> d in identities) all.Add(Public(d));
                return D("value", all);
            }
            if (url.StartsWith(ReadinessCheck.IdentitiesUrl + "?", StringComparison.Ordinal))
            {
                string filter = Uri.UnescapeDataString(url.Substring(url.IndexOf("$filter=", StringComparison.Ordinal) + 8));
                int a = filter.IndexOf('\''), b = filter.LastIndexOf('\'');
                string serial = a >= 0 && b > a ? filter.Substring(a + 1, b - a - 1) : "";
                List<object> found = new List<object>();
                foreach (Dictionary<string, object> d in identities)
                    if (((string)d["serialNumber"]).IndexOf(serial, StringComparison.OrdinalIgnoreCase) >= 0) found.Add(Public(d));
                return D("value", found);
            }
            if (url.StartsWith(ReadinessCheck.IdentitiesUrl + "/", StringComparison.Ordinal))
            {
                foreach (Dictionary<string, object> d in identities)
                    if (url.IndexOf((string)d["id"], StringComparison.OrdinalIgnoreCase) >= 0 && d["profile"] != null) return Profile((string)d["profile"]);
                throw new GraphException("Not found.", 404);
            }
            if (url == ReadinessCheck.ConfigsUrl)
                return D("value", L(
                    Esp("e5000000-0000-4000-8000-000000000001_Windows10EnrollmentCompletionPageConfiguration", "Contoso ESP - block until required apps", 1, false, 60, L(AppPortal, AppVpn, AppOffice, AppReader), true, false),
                    Esp("e5000000-0000-4000-8000-000000000002_Windows10EnrollmentCompletionPageConfiguration", "Contoso ESP - kiosks", 2, false, 45, L(AppPortal, AppGone), false, false),
                    Esp("e5000000-0000-4000-8000-000000000003_Windows10EnrollmentCompletionPageConfiguration", "Contoso ESP - all users", 3, false, 60, L(), false, true),
                    Esp("e5000000-0000-4000-8000-000000000000_DefaultWindows10EnrollmentCompletionPageConfiguration", "All users and all devices", 0, true, 60, L(), false, true),
                    D("@odata.type", "#microsoft.graph.deviceEnrollmentLimitConfiguration", "id", "limit_DefaultLimit", "displayName", "All users", "priority", 0L, "limit", 5L)));
            if (url.StartsWith(ReadinessCheck.ConfigsUrl + "/", StringComparison.Ordinal))
            {
                if (url.Contains("e5000000-0000-4000-8000-000000000001")) return D("value", L(Target("groupAssignmentTarget", GroupSales)));
                if (url.Contains("e5000000-0000-4000-8000-000000000002")) return D("value", L(Target("groupAssignmentTarget", GroupKiosk)));
                if (url.Contains("e5000000-0000-4000-8000-000000000003")) return D("value", L(Target("allLicensedUsersAssignmentTarget", null)));
                return D("value", L());
            }
            if (url.StartsWith(ReadinessCheck.AppUrl, StringComparison.Ordinal))
            {
                string id = url.Substring(ReadinessCheck.AppUrl.Length);
                int q = id.IndexOf('?');
                if (q >= 0) id = id.Substring(0, q);
                string name = id == AppPortal ? "Company Portal" : id == AppVpn ? "Contoso VPN Client 5.2" : id == AppOffice ? "Microsoft 365 Apps for enterprise" : id == AppReader ? "Adobe Acrobat Reader" : null;
                if (name == null) throw new GraphException("Not found.", 404);
                return D("@odata.type", "#microsoft.graph.win32LobApp", "id", id, "displayName", name);
            }
            if (url.StartsWith(ReadinessCheck.DevicesUrl + "(", StringComparison.Ordinal))
            {
                List<object> groups = new List<object>();
                groups.Add(D("@odata.type", "#microsoft.graph.group", "id", GroupAll, "displayName", "All Autopilot devices"));
                if (url.Contains("d1000000-0000-4000-8000-000000000001") || url.Contains("d1000000-0000-4000-8000-000000000002") || url.Contains("d1000000-0000-4000-8000-000000000005"))
                    groups.Add(D("@odata.type", "#microsoft.graph.group", "id", GroupSales, "displayName", "Autopilot - Sales"));
                if (url.Contains("d1000000-0000-4000-8000-000000000004"))
                    groups.Add(D("@odata.type", "#microsoft.graph.group", "id", GroupKiosk, "displayName", "Autopilot - Kiosks"));
                return D("value", groups);
            }
            throw new GraphException("Not found.", 404);
        }

        static Dictionary<string, object> Public(Dictionary<string, object> d)
        {
            Dictionary<string, object> c = new Dictionary<string, object>(d, StringComparer.Ordinal);
            c.Remove("profile");
            return c;
        }

        // Earlier enrollments as the agents would have uploaded them, for the prediction: the apps the ESP waits for
        // with the times they took, and the VPN client failing now and then.
        public static List<Session> History(DateTime nowUtc)
        {
            Random r = new Random(421);
            List<Session> list = new List<Session>();
            for (int n = 0; n < 24; n++)
            {
                Session s = new Session();
                s.Computer = "CONTOSO-LT" + (300 + n * 5).ToString(CultureInfo.InvariantCulture);
                DateTime t = nowUtc.AddDays(-n - 1).AddMinutes(-r.Next(0, 600));
                t = AddApp(s, "Company Portal", t, 40 + r.Next(0, 50), false);
                t = AddApp(s, "Contoso VPN Client 5.2", t, 240 + r.Next(0, 240), n % 7 == 2);
                t = AddApp(s, "Microsoft 365 Apps for enterprise", t, 1200 + r.Next(0, 480), false);
                AddApp(s, "Adobe Acrobat Reader", t, 840 + r.Next(0, 360), false);
                list.Add(s);
            }
            return list;
        }

        static DateTime AddApp(Session s, string name, DateTime start, int seconds, bool failed)
        {
            Item i = new Item();
            i.Key = "demo:" + name.ToLowerInvariant();
            i.Name = name;
            i.Kind = Kinds.Win32App;
            i.Origin = Origins.Intune;
            i.PhaseKey = Phases.DeviceSetup;
            i.State = failed ? State.Failed : State.Succeeded;
            i.FirstUtc = start;
            i.StartUtc = start;
            i.EndUtc = start.AddSeconds(seconds);
            i.LastUtc = i.EndUtc.Value;
            s.Items.Add(i);
            return i.EndUtc.Value.AddSeconds(5);
        }
    }
}
