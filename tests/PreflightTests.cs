namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The pre-flight check, with made-up Graph answers in the documented shapes.
    public static class PreflightTests
    {
        const string GroupA = "c0a80001-1111-4a2b-8c3d-00000000000a", GroupB = "c0a80001-1111-4a2b-8c3d-00000000000b";
        const string App1 = "a1000000-0000-4000-8000-000000000001", App2 = "a1000000-0000-4000-8000-000000000002", App3 = "a1000000-0000-4000-8000-000000000003";
        const string Entra = "d1000000-0000-4000-8000-0000000000aa";

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
                if (Answers[best].StartsWith("!")) throw new GraphException("Refused.", int.Parse(Answers[best].Substring(1)));
                return (Dictionary<string, object>)ImeJson.Parse(Answers[best]);
            }
        }

        static string Identity(string serial, string status, string user)
        {
            return "{\"value\":[{\"id\":\"i1\",\"serialNumber\":\"" + serial + "\",\"manufacturer\":\"HP\",\"model\":\"EliteBook\",\"groupTag\":\"Sales\",\"userPrincipalName\":\"" + user + "\","
                + "\"deploymentProfileAssignmentStatus\":\"" + status + "\",\"enrollmentState\":\"notContacted\",\"azureAdDeviceId\":\"" + Entra + "\",\"lastContactedDateTime\":\"0001-01-01T00:00:00Z\"}]}";
        }

        static string Esp(int timeout, string apps)
        {
            return "{\"value\":[{\"@odata.type\":\"#microsoft.graph.windows10EnrollmentCompletionPageConfiguration\",\"id\":\"e1_Default\",\"displayName\":\"All users and all devices\",\"priority\":0,"
                + "\"deviceEnrollmentConfigurationType\":\"defaultWindows10EnrollmentCompletionPageConfiguration\",\"showInstallationProgress\":true,\"installProgressTimeoutInMinutes\":" + timeout
                + ",\"selectedMobileAppIds\":[" + apps + "]}]}";
        }

        static string Asg(string intent, string target, string group)
        {
            return Asg(intent, target, group, null);
        }

        static string Asg(string intent, string target, string group, string filter)
        {
            return "{\"intent\":\"" + intent + "\",\"target\":{\"@odata.type\":\"#microsoft.graph." + target + "\"" + (group != null ? ",\"groupId\":\"" + group + "\"" : "")
                + (filter != null ? ",\"deviceAndAppManagementAssignmentFilterId\":\"" + filter + "\",\"deviceAndAppManagementAssignmentFilterType\":\"include\"" : ",\"deviceAndAppManagementAssignmentFilterId\":null,\"deviceAndAppManagementAssignmentFilterType\":\"none\"") + "}}";
        }

        static string App(string id, string type, string name, bool user, params string[] assignments)
        {
            return "{\"@odata.type\":\"#microsoft.graph." + type + "\",\"id\":\"" + id + "\",\"displayName\":\"" + name + "\",\"installExperience\":{\"runAsAccount\":\"" + (user ? "user" : "system") + "\"},\"assignments\":[" + string.Join(",", assignments) + "]}";
        }

        static string Group(params string[] ids)
        {
            List<string> l = new List<string>();
            foreach (string id in ids) l.Add("{\"id\":\"" + id + "\",\"displayName\":\"Group " + id.Substring(id.Length - 1) + "\"}");
            return "{\"value\":[" + string.Join(",", l.ToArray()) + "]}";
        }

        static string Rel(string type, string kind, string target)
        {
            return "{\"@odata.type\":\"#microsoft.graph." + type + "\",\"targetId\":\"" + target + "\",\"targetType\":\"child\",\"" + (type == "mobileAppDependency" ? "dependencyType" : "supersedenceType") + "\":\"" + kind + "\"}";
        }

        // A tenant with one good device, answered by the start of the URL.
        static FakeGraph Tenant(string status, string apps, string esp)
        {
            FakeGraph g = new FakeGraph();
            g.Answers[ReadinessCheck.IdentitiesUrl + "?"] = Identity("SN1", status, "");
            g.Answers[ReadinessCheck.IdentitiesUrl + "/i1/deploymentProfile"] = "{\"displayName\":\"Standard\",\"outOfBoxExperienceSetting\":{\"deviceUsageType\":\"singleUser\"}}";
            g.Answers[ReadinessCheck.DevicesUrl + "("] = Group(GroupA);
            g.Answers[ReadinessCheck.ConfigsUrl] = esp;
            g.Answers[PreflightRun.AppsUrl] = "{\"value\":[" + apps + "]}";
            g.Answers[PreflightRun.AppBase] = "{\"value\":[]}";
            return g;
        }

        static PfFinding Find(PfResult r, string check)
        {
            foreach (PfFinding f in r.Findings) if (f.Check == check) return f;
            return null;
        }

        static PfFinding Find(PfResult r, PfLevel level, string textPart)
        {
            foreach (PfFinding f in r.Findings) if (f.Level == level && f.Text.IndexOf(textPart, StringComparison.Ordinal) >= 0) return f;
            return null;
        }

        public static void Run()
        {
            ReadyDevice();
            NotRegistered();
            ProfileStates();
            GroupsRefused();
            Targets();
            Dependencies();
            Loops();
            Conflicts();
            UserContext();
            MixedAndFilters();
            EspTime();
            AppsRefused();
            AsText();
            DemoTenant();
        }

        static void ReadyDevice()
        {
            FakeGraph g = Tenant("assignedInSync", App(App1, "win32LobApp", "Portal", false, Asg("required", "allDevicesAssignmentTarget", null)), Esp(60, "\"" + App1 + "\""));
            PfResult r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Ready, r.Verdict, "a registered device with a profile, groups and apps is ready");
            T.Eq("HP EliteBook", r.Title, "the title is the make and model");
            T.Check(Find(r, PfLevel.Ok, "Standard") != null, "the profile name shows");
            T.Check(Find(r, PfLevel.Ok, "Portal") != null, "the required app is listed");
            T.Check(g.Asked.Exists(delegate (string u) { return Uri.UnescapeDataString(u).Contains("windowsAutopilotDeviceIdentities?$filter=contains(serialNumber,'SN1')"); }), "the device is found with contains(serialNumber)");
            T.Check(g.Asked.Exists(delegate (string u) { return u.Contains("transitiveMemberOf"); }), "the groups are read");
        }

        static void NotRegistered()
        {
            FakeGraph g = Tenant("assignedInSync", "", Esp(60, ""));
            g.Answers[ReadinessCheck.IdentitiesUrl + "?"] = "{\"value\":[]}";
            PfResult r = new PreflightRun(g).Check("NOPE");
            T.Eq(PfVerdict.WillFail, r.Verdict, "a device that is not in Autopilot will fail");
            T.Eq(1, r.Findings.Count, "nothing more is checked");
            T.Check(r.Findings[0].Fix.Contains("hardware hash"), "the fix is to import the hardware hash");

            g.Answers[ReadinessCheck.IdentitiesUrl + "?"] = "!403";
            r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "a refused read is something to check, not a failure");
            T.Check(r.Findings[0].Text.Contains("DeviceManagementServiceConfig.Read.All"), "the missing permission is named");
        }

        static void ProfileStates()
        {
            string[] ok = { "assignedInSync" };
            PfResult r = new PreflightRun(Tenant(ok[0], "", Esp(60, ""))).Check("SN1");
            T.Eq(PfVerdict.Ready, r.Verdict, "assigned in sync is ready");

            PfResult na = new PreflightRun(Tenant("notAssigned", "", Esp(60, ""))).Check("SN1");
            T.Eq(PfVerdict.WillFail, na.Verdict, "no profile assigned will fail");
            PfFinding f = Find(na, "Deployment profile");
            T.Check(f.Fix.StartsWith("Assign an Autopilot profile to one of its groups (Group a)"), "the fix names the group of the device: " + f.Fix);

            T.Eq(PfVerdict.Check, new PreflightRun(Tenant("pending", "", Esp(60, ""))).Check("SN1").Verdict, "a profile that is pending needs a second look");
            T.Eq(PfVerdict.WillFail, new PreflightRun(Tenant("failed", "", Esp(60, ""))).Check("SN1").Verdict, "a failed assignment will fail");
            T.Eq(PfVerdict.Check, new PreflightRun(Tenant("assignedOutOfSync", "", Esp(60, ""))).Check("SN1").Verdict, "out of sync needs a second look");
        }

        static void GroupsRefused()
        {
            FakeGraph g = Tenant("assignedInSync", App(App1, "win32LobApp", "Portal", false, Asg("required", "allDevicesAssignmentTarget", null), Asg("required", "groupAssignmentTarget", GroupA)), Esp(60, ""));
            g.Answers[ReadinessCheck.DevicesUrl + "("] = "!403";
            PfResult r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "groups that cannot be read give a warning");
            PfFinding f = Find(r, "Device groups");
            T.Check(f != null && f.Text.Contains("Device.Read.All"), "the missing permission is named");
            T.Check(Find(r, PfLevel.Ok, "Portal") != null, "the checks go on: apps for all devices still show");

            PreflightRun off = new PreflightRun(Tenant("assignedInSync", "", Esp(60, "")));
            off.ReadGroups = false;
            PfFinding offLine = Find(off.Check("SN1"), "Device groups");
            T.Check(offLine.Text.Contains("turned off") && offLine.Fix.Contains("Turn on Groups"), "groups turned off say so, and the fix is to turn them on");

            // Without GroupMember.Read.All Graph gives the groups without their names: the IDs still match.
            g = Tenant("assignedInSync", App(App1, "win32LobApp", "Portal", false, Asg("required", "groupAssignmentTarget", GroupA)), Esp(60, ""));
            g.Answers[ReadinessCheck.DevicesUrl + "("] = "{\"value\":[{\"@odata.type\":\"#microsoft.graph.group\",\"id\":\"" + GroupA + "\",\"displayName\":null}]}";
            r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Ready, r.Verdict, "groups without names are no problem");
            T.Check(Find(r, "Device groups").Text.Contains("GroupMember.Read.All"), "the line says which permission gives the names");
            T.Check(Find(r, PfLevel.Ok, "Portal") != null, "an app for the group is still found by the ID");

            PreflightRun noApps = new PreflightRun(Tenant("assignedInSync", "", Esp(60, "")));
            noApps.ReadAppList = false;
            r = noApps.Check("SN1");
            T.Check(Find(r, "Required apps").Fix.Contains("Turn on Apps"), "apps turned off say so");
            T.Check(!PreflightRun.Scopes(false, false).Contains(LookupChoice.AppScope) && PreflightRun.Scopes(true, true).Contains(PreflightRun.DeviceScope), "the sign-in asks only for what is turned on");
        }

        static void Targets()
        {
            PfDevice d = new PfDevice();
            d.Serial = "S";
            d.Found = true;
            d.Groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            d.Groups[GroupA] = "A";
            PfApp a = new PfApp();
            a.Id = App1;
            a.Name = "X";
            a.Type = "#microsoft.graph.win32LobApp";
            a.Assignments.Add(new PfAssign("required", EspTarget.Group, GroupA));
            bool via;
            T.Check(PfApp_Applies(a, d), "a group the device is in applies");
            a.Assignments.Add(new PfAssign("required", EspTarget.Exclude, GroupA));
            T.Check(!PfApp_Applies(a, d), "an exclusion of that group wins");
            a.Assignments.Clear();
            a.Assignments.Add(new PfAssign("required", EspTarget.Group, GroupB));
            T.Check(!PfApp_Applies(a, d), "a group the device is not in does not apply");
            a.Assignments.Clear();
            a.Assignments.Add(new PfAssign("required", EspTarget.AllUsers, null));
            T.Check(Preflight.Applies(a, "required", d, out via) && !via, "all users applies through the user");
            a.Assignments.Clear();
            a.Assignments.Add(new PfAssign("available", EspTarget.AllDevices, null));
            T.Check(!PfApp_Applies(a, d), "available is not required");
            a.Type = "#microsoft.graph.iosStoreApp";
            a.Assignments.Clear();
            a.Assignments.Add(new PfAssign("required", EspTarget.AllDevices, null));
            T.Check(!PfApp_Applies(a, d), "apps of other platforms are left out");
            T.Check(PfApp.IsWindowsType("#microsoft.graph.officeSuiteApp") && PfApp.IsWindowsType("#microsoft.graph.winGetApp") && !PfApp.IsWindowsType("#microsoft.graph.androidStoreApp"), "the Windows app types");

            d.UserGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            d.UserGroups[GroupB] = "B";
            a.Type = "#microsoft.graph.win32LobApp";
            a.Assignments.Clear();
            a.Assignments.Add(new PfAssign("required", EspTarget.Group, GroupB));
            T.Check(Preflight.Applies(a, "required", d, out via) && !via, "a group of the user applies, not through the device");
            a.Assignments.Add(new PfAssign("required", EspTarget.Exclude, GroupB));
            T.Check(!PfApp_Applies(a, d), "an exclusion of a group of the user wins too");
            a.Assignments.Clear();
            a.Assignments.Add(new PfAssign("required", EspTarget.Group, GroupA));
            a.Assignments.Add(new PfAssign("available", EspTarget.Exclude, GroupA));
            T.Check(PfApp_Applies(a, d), "an exclusion counts for its own intent only");

            // Nobody signs in during a self-deploying deployment, so nothing comes through a user.
            d.SelfDeploying = true;
            a.Assignments.Clear();
            a.Assignments.Add(new PfAssign("required", EspTarget.AllUsers, null));
            T.Check(!PfApp_Applies(a, d), "all users does not reach a self-deploying device");
            a.Assignments.Add(new PfAssign("required", EspTarget.AllDevices, null));
            T.Check(PfApp_Applies(a, d), "all devices does");
            d.SelfDeploying = false;

            // Groups that were not read: only all devices and all users can be said.
            PfDevice blind = new PfDevice();
            blind.Found = true;
            PfApp g = new PfApp();
            g.Id = App2;
            g.Name = "G";
            g.Type = "#microsoft.graph.win32LobApp";
            g.Assignments.Add(new PfAssign("required", EspTarget.Group, GroupA));
            T.Check(!PfApp_Applies(g, blind), "a group app is not guessed without the groups");
        }

        static bool PfApp_Applies(PfApp a, PfDevice d)
        {
            bool via;
            return Preflight.Applies(a, "required", d, out via);
        }

        static void Dependencies()
        {
            string apps = App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null))
                + "," + App(App3, "win32LobApp", "Runtime", false, Asg("required", "groupAssignmentTarget", GroupA));
            FakeGraph g = Tenant("assignedInSync", apps, Esp(60, ""));
            g.Answers[PreflightRun.RelationsUrl(App1)] = "{\"value\":[" + Rel("mobileAppDependency", "detect", App3) + "]}";
            PfResult r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Ready, r.Verdict, "a dependency that is required for the device is fine");
            T.Check(g.Asked.Contains(PreflightRun.RelationsUrl(App1)), "the relationships are read from mobileApps/{id}/relationships");

            // A dependency that Intune installs by itself needs no assignment: it is said, and it is no warning.
            g.Answers[PreflightRun.RelationsUrl(App1)] = "{\"value\":[" + Rel("mobileAppDependency", "autoInstall", App2) + "]}";
            r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Ready, r.Verdict, "an unassigned auto-install dependency is fine");
            T.Check(Find(r, "Dependencies") != null && Find(r, "Dependencies").Text.Contains("not assigned on its own"), "and it is listed");

            // A dependency that must be there already and is not assigned: the app cannot install.
            g.Answers[PreflightRun.RelationsUrl(App1)] = "{\"value\":[" + Rel("mobileAppDependency", "detect", App2) + "]}";
            r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.WillFail, r.Verdict, "an unassigned detect dependency will fail");
            T.Check(Find(r, PfLevel.Fail, "not assigned to anyone").Fix.Contains("install automatically"), "the fix is said");

            // Assigned, but only as available: it is not there during the deployment.
            apps = App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null))
                + "," + App(App3, "win32LobApp", "Runtime", false, Asg("available", "allLicensedUsersAssignmentTarget", null));
            g = Tenant("assignedInSync", apps, Esp(60, ""));
            g.Answers[PreflightRun.RelationsUrl(App1)] = "{\"value\":[" + Rel("mobileAppDependency", "detect", App3) + "]}";
            r = new PreflightRun(g).Check("SN1");
            T.Check(Find(r, PfLevel.Fail, "not required for this device") != null, "a detect dependency that is only available will fail");
            g.Answers[ReadinessCheck.DevicesUrl + "("] = "!403";
            r = new PreflightRun(g).Check("SN1");
            T.Check(Find(r, PfLevel.Fail, "Runtime") == null && Find(r, PfLevel.Warn, "could not be told") != null, "without the groups it is a question, not a failure");

            // A dependency that is removed from the same device.
            apps = App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null))
                + "," + App(App3, "win32LobApp", "Runtime", false, Asg("uninstall", "groupAssignmentTarget", GroupA));
            g = Tenant("assignedInSync", apps, Esp(60, ""));
            g.Answers[PreflightRun.RelationsUrl(App1)] = "{\"value\":[" + Rel("mobileAppDependency", "autoInstall", App3) + "]}";
            r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "a dependency set to uninstall is flagged");
            T.Check(Find(r, PfLevel.Warn, "set to uninstall") != null, "and said");

            // Relationships refused: said once, the rest goes on.
            g = Tenant("assignedInSync", App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null)), Esp(60, ""));
            g.Answers[PreflightRun.AppBase] = "!403";
            r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "relationships that cannot be read give a warning");
            T.Check(Find(r, "Dependencies").Text.Contains("DeviceManagementApps.Read.All"), "the permission is named");

            // Relationships that point back at the app (the parent side) are not its dependencies.
            g = Tenant("assignedInSync", App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null)), Esp(60, ""));
            g.Answers[PreflightRun.RelationsUrl(App1)] = "{\"value\":[" + Rel("mobileAppDependency", "detect", App2).Replace("\"child\"", "\"parent\"") + "]}";
            T.Eq(PfVerdict.Ready, new PreflightRun(g).Check("SN1").Verdict, "an app that depends on this one is not its dependency");
        }

        static void Loops()
        {
            string apps = App(App1, "win32LobApp", "Shell 1", false, Asg("required", "allDevicesAssignmentTarget", null))
                + "," + App(App2, "win32LobApp", "Shell 2", false, Asg("required", "allDevicesAssignmentTarget", null));
            FakeGraph g = Tenant("assignedInSync", apps, Esp(60, ""));
            g.Answers[PreflightRun.RelationsUrl(App1)] = "{\"value\":[" + Rel("mobileAppSupersedence", "update", App2) + "]}";
            g.Answers[PreflightRun.RelationsUrl(App2)] = "{\"value\":[" + Rel("mobileAppSupersedence", "update", App1) + "]}";
            PfResult r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "supersedence in a circle is flagged");
            int n = 0;
            foreach (PfFinding f in r.Findings) if (f.Check == "Supersedence") n++;
            T.Eq(1, n, "the circle is reported once, not once per app");
            T.Check(Find(r, "Supersedence").Text.Contains("Shell 1 > Shell 2 > Shell 1"), "with the apps in it: " + Find(r, "Supersedence").Text);

            g.Answers[PreflightRun.RelationsUrl(App2)] = "{\"value\":[]}";
            T.Eq(PfVerdict.Ready, new PreflightRun(g).Check("SN1").Verdict, "supersedence in one direction is fine");
        }

        static void Conflicts()
        {
            string apps = App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null), Asg("uninstall", "groupAssignmentTarget", GroupA));
            PfResult r = new PreflightRun(Tenant("assignedInSync", apps, Esp(60, ""))).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "required and uninstall for the same device is flagged");
            T.Check(Find(r, PfLevel.Warn, "both required and set to uninstall") != null, "the conflict is said in a sentence");

            apps = App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null), Asg("uninstall", "groupAssignmentTarget", GroupB));
            T.Eq(PfVerdict.Ready, new PreflightRun(Tenant("assignedInSync", apps, Esp(60, ""))).Check("SN1").Verdict, "an uninstall for a group the device is not in is no conflict");
        }

        static void UserContext()
        {
            string apps = App(App1, "win32LobApp", "Reader", true, Asg("required", "groupAssignmentTarget", GroupA));
            PfResult r = new PreflightRun(Tenant("assignedInSync", apps, Esp(60, "\"" + App1 + "\""))).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "a user-context Win32 app for the device in the blocking list is flagged");
            T.Check(Find(r, PfLevel.Warn, "installs for the user") != null, "the reason is said");

            // In self-deploying mode nobody signs in, so it never installs.
            FakeGraph self = Tenant("assignedInSync", apps, Esp(60, "\"" + App1 + "\""));
            self.Answers[ReadinessCheck.IdentitiesUrl + "/i1/deploymentProfile"] = "{\"displayName\":\"Kiosk\",\"outOfBoxExperienceSetting\":{\"deviceUsageType\":\"shared\"}}";
            r = new PreflightRun(self).Check("SN1");
            T.Eq(PfVerdict.WillFail, r.Verdict, "with a self-deploying profile it will fail");
            T.Check(Find(r, PfLevel.Fail, "installs for the user").Fix.Contains("System"), "the fix is the install behaviour");

            r = new PreflightRun(Tenant("assignedInSync", apps, Esp(60, ""))).Check("SN1");
            T.Check(Find(r, PfLevel.Warn, "installs for the user") == null, "not in a blocking list: nothing to say");

            string sys = App(App1, "win32LobApp", "Reader", false, Asg("required", "groupAssignmentTarget", GroupA));
            T.Eq(PfVerdict.Ready, new PreflightRun(Tenant("assignedInSync", sys, Esp(60, "\"" + App1 + "\""))).Check("SN1").Verdict, "the same app installing for the system is fine");

            // An app in the blocking list that is not required for the device is only a note, and an ID Intune no longer has a warning.
            string other = App(App1, "win32LobApp", "Reader", false, Asg("required", "groupAssignmentTarget", GroupB));
            r = new PreflightRun(Tenant("assignedInSync", other, Esp(60, "\"" + App1 + "\""))).Check("SN1");
            T.Eq(PfVerdict.Ready, r.Verdict, "a blocking app the device does not get is no problem");
            T.Check(Find(r, PfLevel.Ok, "not required for this device") != null, "but it is noted");
            T.Check(Find(r, "Time").Text.Contains("waits for no app"), "and the ESP waits for nothing: " + Find(r, "Time").Text);
            r = new PreflightRun(Tenant("assignedInSync", other, Esp(60, "\"" + App2 + "\""))).Check("SN1");
            T.Check(Find(r, PfLevel.Warn, "no longer in Intune") != null, "a blocking app that is gone is a warning");
        }

        static void MixedAndFilters()
        {
            string apps = App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null))
                + "," + App(App2, "windowsMobileMSI", "Viewer", false, Asg("required", "allDevicesAssignmentTarget", null));
            PfResult r = new PreflightRun(Tenant("assignedInSync", apps, Esp(60, ""))).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "an MSI app next to Win32 apps is flagged");
            T.Check(Find(r, "App types").Fix.Contains("Package Viewer as a Win32 app"), "the fix names the app: " + Find(r, "App types").Fix);

            // An assignment with a filter: the filter is named when it can be read, and never evaluated.
            const string filter = "f1000000-0000-4000-8000-000000000001";
            apps = App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null, filter));
            FakeGraph g = Tenant("assignedInSync", apps, Esp(60, ""));
            g.Answers[PreflightRun.FiltersUrl] = "{\"value\":[{\"id\":\"" + filter + "\",\"displayName\":\"Corporate laptops\"}]}";
            r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Ready, r.Verdict, "a filter is no problem by itself");
            T.Check(Find(r, "Assignment filters").Text.Contains("Corporate laptops"), "the filter is named");
            g.Answers[PreflightRun.FiltersUrl] = "!403";
            r = new PreflightRun(g).Check("SN1");
            T.Check(Find(r, "Assignment filters").Text.Contains("DeviceManagementConfiguration.Read.All"), "without the permission the line says which one gives the names");
            g = Tenant("assignedInSync", App(App1, "win32LobApp", "Client", false, Asg("required", "allDevicesAssignmentTarget", null)), Esp(60, ""));
            new PreflightRun(g).Check("SN1");
            T.Check(!g.Asked.Contains(PreflightRun.FiltersUrl), "the filters are not read when no app uses one");
        }

        static void EspTime()
        {
            string apps = App(App1, "win32LobApp", "Portal", false, Asg("required", "allDevicesAssignmentTarget", null))
                + "," + App(App2, "win32LobApp", "Office", false, Asg("required", "allDevicesAssignmentTarget", null));
            PfTimes times = new PfTimes();
            times.Runs = 5;
            times.AppSeconds["Portal"] = 600;
            times.AppSeconds["Office"] = 1440;
            PreflightRun run = new PreflightRun(Tenant("assignedInSync", apps, Esp(60, "\"" + App1 + "\",\"" + App2 + "\"")));
            run.Times = times;
            PfResult r = run.Check("SN1");
            PfFinding f = Find(r, "Time");
            T.Check(f.Text.StartsWith("ESP blocks on 2 apps, usually 34 min; timeout is 60 min."), "the time reads like the example: " + f.Text);
            T.Eq(PfLevel.Ok, f.Level, "34 of 60 min leaves room");

            run = new PreflightRun(Tenant("assignedInSync", apps, Esp(40, "\"" + App1 + "\",\"" + App2 + "\"")));
            run.Times = times;
            T.Eq(PfLevel.Warn, Find(run.Check("SN1"), "Time").Level, "34 of 40 min leaves little room");

            // An app the earlier deployments did not have: the time is a lower bound.
            PfTimes part = new PfTimes();
            part.Runs = 5;
            part.AppSeconds["Office"] = 1440;
            run = new PreflightRun(Tenant("assignedInSync", apps, Esp(60, "\"" + App1 + "\",\"" + App2 + "\"")));
            run.Times = part;
            T.Check(Find(run.Check("SN1"), "Time").Text.Contains("usually at least 24 min"), "a time that misses an app is a lower bound");

            run = new PreflightRun(Tenant("assignedInSync", apps, Esp(30, "\"" + App1 + "\",\"" + App2 + "\"")));
            run.Times = times;
            r = run.Check("SN1");
            T.Eq(PfLevel.Fail, Find(r, "Time").Level, "more than the limit is a failure");
            T.Check(Find(r, "Time").Fix.Contains("Raise the timeout"), "the fix is to raise the timeout");

            run = new PreflightRun(Tenant("assignedInSync", apps, Esp(60, "")));
            r = run.Check("SN1");
            T.Check(Find(r, "Time").Text.StartsWith("ESP blocks on 2 apps; timeout is 60 min.") && Find(r, "Time").Text.Contains("no earlier deployments"), "without history the time is not guessed: " + Find(r, "Time").Text);
            T.Eq(PfLevel.Ok, Find(r, "Time").Level, "and it is no problem");

            // The medians of a deployment history.
            DeploymentHistory h = new DeploymentHistory();
            for (int i = 0; i < 4; i++)
            {
                HistoryRun hr = new HistoryRun();
                hr.Id = "r" + i;
                hr.Customer = "Contoso";
                hr.Kind = PreflightRun.HistoryKind;
                hr.Profile = "P";
                hr.EndUtc = DateTime.UtcNow.AddDays(-i);
                hr.Total = 3000;
                hr.Items.Add(new HistoryValue { Key = Kinds.Win32App + "|Portal", Seconds = 100 + i });
                hr.Phases.Add(new HistoryValue { Key = Phases.DeviceSetup, Seconds = 1200 });
                h.Add(hr);
            }
            PfTimes from = PreflightRun.TimesFrom(h, "Contoso");
            T.Check(from != null && from.Runs == 4 && from.AppSeconds.ContainsKey("Portal"), "the times come from the history of the customer");
            T.Eq(1200.0, from.EspSeconds, "and the ESP phase median");
            T.Check(PreflightRun.TimesFrom(h, "Fabrikam") == null, "another customer has no times");
            T.Check(PreflightRun.TimesFrom(null, "Contoso") == null, "no history, no times");
        }

        static void AppsRefused()
        {
            FakeGraph g = Tenant("assignedInSync", "", Esp(60, ""));
            g.Answers[PreflightRun.AppsUrl] = "!403";
            PfResult r = new PreflightRun(g).Check("SN1");
            T.Eq(PfVerdict.Check, r.Verdict, "apps that cannot be read give a warning");
            T.Check(Find(r, "Required apps").Text.Contains("DeviceManagementApps.Read.All"), "the permission is named");
            T.Check(Find(r, "Which ESP applies") != null, "the other checks still ran");

            g = Tenant("assignedInSync", "", Esp(60, ""));
            g.Answers[ReadinessCheck.ConfigsUrl] = "!403";
            r = new PreflightRun(g).Check("SN1");
            T.Check(Find(r, "Which ESP applies").Text.Contains("DeviceManagementServiceConfig.Read.All"), "a refused ESP read names its permission");
            T.Check(Find(r, "Required apps") != null, "and the apps were still checked");
        }

        static void AsText()
        {
            PfResult a = new PfResult();
            a.Serial = "SN1";
            a.Title = "HP EliteBook";
            a.Add(PfLevel.Ok, "g", "c", "Fine.", null);
            a.Add(PfLevel.Fail, "g", "c", "Broken.", "Mend it.");
            PfResult b = new PfResult();
            b.Serial = "SN2";
            b.Title = "SN2";
            b.Add(PfLevel.Ok, "g", "c", "Fine.", null);
            string text = Preflight.ToText(new List<PfResult> { a, b }, "Contoso", new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc));
            T.Check(text.Contains("SN1 (HP EliteBook): WILL FAIL"), "the summary row of a failing device");
            T.Check(text.Contains("SN2: READY"), "the summary row of a ready device");
            T.Check(text.Contains("✕ Broken.") && text.Contains("Fix: Mend it.") && text.Contains("✓ Fine."), "marks and fixes");
            T.Check(text.Contains("2 devices: 1 ready, 0 to check, 1 will fail"), "the total");
            T.Check(text.IndexOf("Fix: Mend it.") > text.IndexOf("Broken."), "the fix follows its line");
        }

        // The made-up tenant of the demo goes through the same code and shows every kind of verdict.
        static void DemoTenant()
        {
            PreflightRun run = new PreflightRun(new DemoPreflightGraph(DateTime.UtcNow));
            run.Times = DemoPreflightGraph.Times();
            Dictionary<string, PfVerdict> want = new Dictionary<string, PfVerdict>();
            want["5CG4321ABD"] = PfVerdict.Ready;
            want["5CG4321ABC"] = PfVerdict.Check;
            want["5CG4321AA7"] = PfVerdict.WillFail;
            want["5CG4321B02"] = PfVerdict.WillFail;
            want["8CC2207KQ1"] = PfVerdict.Check;
            want["5CG9999XYZ"] = PfVerdict.WillFail;
            foreach (KeyValuePair<string, PfVerdict> kv in want)
            {
                PfResult r = run.Check(kv.Key);
                T.Eq(kv.Value, r.Verdict, "demo verdict of " + kv.Key);
            }
            PfResult abc = run.Check("5CG4321ABC");
            T.Check(Find(abc, "User groups") != null && Find(abc, "User groups").Text.Contains("GroupMember.Read.All"), "the user's groups are refused in the demo and the permission is named");
            PfResult aa7 = run.Check("5CG4321AA7");
            T.Check(Find(aa7, PfLevel.Fail, "Contoso ERP Client needs Microsoft Visual C++ 2015-2022") != null, "the demo pilot device has a dependency that is not there");
            T.Check(Find(aa7, "App types") != null, "and an MSI app next to Win32 apps");
            T.Check(Find(run.Check("5CG4321ABD"), "Dependencies").Text.Contains("Contoso Runtime 2022"), "the demo names the dependency Intune installs by itself");
            bool phone = false;
            foreach (PfFinding f in run.Check("5CG4321ABD").Findings) if (f.Text.Contains("Outlook for iOS")) phone = true;
            T.Check(!phone, "the iOS app of the demo is left out");
            T.Check(Find(run.Check("8CC2207KQ1"), "Supersedence") != null, "the demo kiosk has a supersedence circle");
            T.Check(Find(run.Check("5CG4321ABD"), "Time").Text.StartsWith("ESP blocks on 4 apps, usually 39 min; timeout is 60 min."), "the demo time: " + Find(run.Check("5CG4321ABD"), "Time").Text);
        }
    }
}
