namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // The made-up Contoso tenant of the check before enrollment, with apps added: the same Graph answers as Intune
    // gives, so the demo and the screenshots go through the same code as a real tenant. Its six devices show every
    // verdict: one is ready, one has a user whose groups cannot be read, one is in a pilot group with a dependency that
    // is not there, one has no profile, a kiosk has apps that supersede each other, and one is not registered.
    public sealed class DemoPreflightGraph : IGraph
    {
        public const string Serials = "5CG4321ABD\r\n5CG4321ABC\r\n5CG4321AA7\r\n5CG4321B02\r\n8CC2207KQ1\r\n5CG9999XYZ";

        const string GroupSales = "c0a80001-1111-4a2b-8c3d-000000000001", GroupKiosk = "c0a80001-1111-4a2b-8c3d-000000000002", GroupPilot = "c0a80001-1111-4a2b-8c3d-000000000004";
        const string AppPortal = "a0000000-0000-4000-8000-000000000001", AppVpn = "a0000000-0000-4000-8000-000000000002";
        const string AppOffice = "a0000000-0000-4000-8000-000000000003", AppReader = "a0000000-0000-4000-8000-000000000004";
        const string AppRuntime = "a0000000-0000-4000-8000-000000000005", AppShell1 = "a0000000-0000-4000-8000-000000000006", AppShell2 = "a0000000-0000-4000-8000-000000000007";
        const string AppPhone = "a0000000-0000-4000-8000-000000000008", AppErp = "a0000000-0000-4000-8000-000000000009";
        const string AppVc = "a0000000-0000-4000-8000-00000000000a", AppCad = "a0000000-0000-4000-8000-00000000000b";
        const string GroupSalesUsers = "c0a80001-1111-4a2b-8c3d-000000000005";
        const string PilotDevice = "d1000000-0000-4000-8000-000000000002";

        readonly DemoReadinessGraph inner;

        public DemoPreflightGraph(DateTime nowUtc)
        {
            inner = new DemoReadinessGraph(nowUtc);
        }

        // How long the apps took in earlier deployments of Contoso.
        public static PfTimes Times()
        {
            PfTimes t = new PfTimes();
            t.Runs = 12;
            t.AppSeconds["Company Portal"] = 60;
            t.AppSeconds["Contoso VPN Client 5.2"] = 300;
            t.AppSeconds["Microsoft 365 Apps for enterprise"] = 1380;
            t.AppSeconds["Adobe Acrobat Reader"] = 600;
            return t;
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

        static Dictionary<string, object> Assign(string intent, string type, string group)
        {
            Dictionary<string, object> t = D("@odata.type", "#microsoft.graph." + type);
            if (group != null) t["groupId"] = group;
            return D("@odata.type", "#microsoft.graph.mobileAppAssignment", "id", Guid.NewGuid().ToString(), "intent", intent, "target", t);
        }

        static Dictionary<string, object> App(string id, string type, string name, bool user, params object[] assignments)
        {
            Dictionary<string, object> a = D("@odata.type", "#microsoft.graph." + type, "id", id, "displayName", name, "isAssigned", true, "assignments", L(assignments));
            if (type == "win32LobApp") a["installExperience"] = D("runAsAccount", user ? "user" : "system");
            return a;
        }

        static Dictionary<string, object> Rel(string type, string kind, string target, string name)
        {
            Dictionary<string, object> r = D("@odata.type", "#microsoft.graph." + type, "id", Guid.NewGuid().ToString(), "targetId", target, "targetDisplayName", name, "targetType", "child");
            if (type == "mobileAppDependency") r["dependencyType"] = kind;
            else r["supersedenceType"] = kind;
            return r;
        }

        public Dictionary<string, object> Get(string url)
        {
            if (url == PreflightRun.AppsUrl)
                return D("value", L(
                    App(AppPortal, "win32LobApp", "Company Portal", false, Assign("required", "allDevicesAssignmentTarget", null)),
                    App(AppVpn, "win32LobApp", "Contoso VPN Client 5.2", false, Assign("required", "groupAssignmentTarget", GroupSales)),
                    App(AppOffice, "officeSuiteApp", "Microsoft 365 Apps for enterprise", false, Assign("required", "allDevicesAssignmentTarget", null)),
                    App(AppReader, "win32LobApp", "Adobe Acrobat Reader", false, Assign("required", "groupAssignmentTarget", GroupSales)),
                    App(AppErp, "win32LobApp", "Contoso ERP Client", false, Assign("required", "groupAssignmentTarget", GroupPilot)),
                    App(AppVc, "win32LobApp", "Microsoft Visual C++ 2015-2022", false, Assign("available", "allLicensedUsersAssignmentTarget", null)),
                    App(AppCad, "windowsMobileMSI", "Contoso CAD Viewer", false, Assign("required", "groupAssignmentTarget", GroupPilot)),
                    App(AppShell1, "win32LobApp", "Contoso Kiosk Shell 1", false, Assign("required", "groupAssignmentTarget", GroupKiosk)),
                    App(AppShell2, "win32LobApp", "Contoso Kiosk Shell 2", false, Assign("required", "groupAssignmentTarget", GroupKiosk)),
                    App(AppPhone, "iosStoreApp", "Outlook for iOS", false, Assign("required", "allLicensedUsersAssignmentTarget", null))));
            if (url.StartsWith(PreflightRun.AppBase, StringComparison.Ordinal) && url.EndsWith("/relationships", StringComparison.Ordinal))
            {
                string id = url.Substring(PreflightRun.AppBase.Length, url.Length - PreflightRun.AppBase.Length - "/relationships".Length);
                // The runtime is not assigned on its own: Intune installs it with the VPN client.
                if (id == AppVpn) return D("value", L(Rel("mobileAppDependency", "autoInstall", AppRuntime, "Contoso Runtime 2022")));
                if (id == AppErp) return D("value", L(Rel("mobileAppDependency", "detect", AppVc, "Microsoft Visual C++ 2015-2022")));
                if (id == AppShell1) return D("value", L(Rel("mobileAppSupersedence", "update", AppShell2, "Contoso Kiosk Shell 2")));
                if (id == AppShell2) return D("value", L(Rel("mobileAppSupersedence", "update", AppShell1, "Contoso Kiosk Shell 1")));
                return D("value", L());
            }
            if (url.StartsWith(PreflightRun.UsersUrl, StringComparison.Ordinal))
            {
                if (url.Contains("mads.jensen")) throw new GraphException("Insufficient privileges to complete the operation.", 403);
                return D("value", L(D("@odata.type", "#microsoft.graph.group", "id", GroupSalesUsers, "displayName", "Sales users")));
            }
            Dictionary<string, object> r = inner.Get(url);
            if (url.StartsWith(ReadinessCheck.DevicesUrl + "(", StringComparison.Ordinal) && url.Contains(PilotDevice))
                ((List<object>)r["value"]).Add(D("@odata.type", "#microsoft.graph.group", "id", GroupPilot, "displayName", "Autopilot - Pilot"));
            return r;
        }
    }
}
