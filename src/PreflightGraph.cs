namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // Reads what the pre-flight check needs from Microsoft Graph. Only read calls.
    //
    //   GET beta/deviceManagement/windowsAutopilotDeviceIdentities?$filter=contains(serialNumber,'...')   the device
    //   GET beta/deviceManagement/windowsAutopilotDeviceIdentities/{id}/deploymentProfile                  its profile
    //   GET v1.0/devices(deviceId='...')/transitiveMemberOf?$select=id,displayName                         its groups
    //   GET v1.0/users/{upn}/transitiveMemberOf?$select=id,displayName                                     the groups of its user
    //   GET beta/deviceManagement/deviceEnrollmentConfigurations (and /assignments)                        the ESPs
    //   GET beta/deviceAppManagement/mobileApps?$filter=isAssigned eq true&$expand=assignments             the assigned apps
    //   GET beta/deviceAppManagement/mobileApps/{id}/relationships                                         dependencies, supersedence
    //   GET beta/deviceManagement/assignmentFilters?$select=id,displayName                                 filter names, when an app uses one
    //
    // Each read that is refused says which permission is missing, and the others go on.
    public sealed class PreflightRun
    {
        public const string AppsUrl = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$filter=isAssigned%20eq%20true&$expand=assignments";
        public const string AppBase = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/";
        public const string UsersUrl = "https://graph.microsoft.com/v1.0/users/";
        public const string FiltersUrl = "https://graph.microsoft.com/beta/deviceManagement/assignmentFilters?$select=id,displayName";
        // The kind a deployment history gives an Autopilot enrollment (Session.Kind).
        public const string HistoryKind = "Autopilot";
        const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        public const string GroupScope = "https://graph.microsoft.com/GroupMember.Read.All";
        public const string DeviceScope = "https://graph.microsoft.com/Device.Read.All";
        const int MaxPages = 50;
        const int MaxRelationCalls = 400;
        const int MaxDepth = 4;

        readonly IGraph graph;
        // Reads the groups of the device and of its user. Off when the sign-in has no consent for them.
        public bool ReadGroups = true;
        // Reads the apps with their assignments and relationships. Off when the sign-in has no consent for them.
        public bool ReadAppList = true;
        // How long things took before, when the window has a history of deployments.
        public PfTimes Times;
        PfTenant tenant;
        int relationCalls;
        readonly Dictionary<string, Dictionary<string, string>> userGroups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> userProblems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public PreflightRun(IGraph g)
        {
            graph = g;
        }

        // The scopes a sign-in needs for what the check reads: Autopilot and the ESPs always, apps and groups when picked.
        public static List<string> Scopes(bool apps, bool groups)
        {
            List<string> l = new List<string>();
            l.Add(ReadinessCheck.Scope);
            if (apps)
            {
                l.Add(LookupChoice.AppScope);
                l.Add(LookupChoice.ConfigScope);
            }
            if (groups)
            {
                l.Add(GroupScope);
                l.Add(DeviceScope);
            }
            return l;
        }

        public static string UserGroupsUrl(string upn)
        {
            return UsersUrl + Uri.EscapeDataString(upn) + "/transitiveMemberOf?$select=id,displayName";
        }

        public static string RelationsUrl(string appId)
        {
            return AppBase + Uri.EscapeDataString(appId) + "/relationships";
        }

        public PfResult Check(string serial)
        {
            PfDevice d = ReadDevice(serial);
            PfTenant t = Tenant();
            if (d.Found && t.Apps != null)
            {
                bool known;
                LoadRelations(t, Preflight.RequiredFor(d, t, out known));
            }
            return Preflight.Evaluate(d, t);
        }

        public List<PfResult> CheckAll(IEnumerable<string> serials, Action<int, int> progress)
        {
            List<string> list = new List<string>(serials);
            List<PfResult> l = new List<PfResult>();
            for (int i = 0; i < list.Count; i++)
            {
                if (progress != null) progress(i, list.Count);
                l.Add(Check(list[i]));
            }
            if (progress != null) progress(list.Count, list.Count);
            return l;
        }

        static List<Dictionary<string, object>> Values(Dictionary<string, object> r)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            List<object> list = ImeJson.Get(r, "value") as List<object>;
            if (list != null)
                foreach (object o in list)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null) l.Add(d);
                }
            return l;
        }

        // ---------- the device ----------

        public PfDevice ReadDevice(string serial)
        {
            PfDevice d = new PfDevice();
            d.Serial = serial;
            List<Dictionary<string, object>> exact = new List<Dictionary<string, object>>();
            try
            {
                string url = ReadinessCheck.FindUrl(serial);
                for (int page = 0; page < MaxPages && url != null; page++)
                {
                    Dictionary<string, object> r = graph.Get(url);
                    foreach (Dictionary<string, object> x in Values(r))
                        if (string.Equals((Http.Str(x, "serialNumber") ?? "").Trim(), serial, StringComparison.OrdinalIgnoreCase)) exact.Add(x);
                    url = Http.Str(r, "@odata.nextLink");
                }
            }
            catch (Exception ex)
            {
                d.Problem = IntuneLookup.Problem("The Autopilot registration", ReadinessCheck.Scope, ex).Replace(" were not read", " was not read");
                return d;
            }
            if (exact.Count == 0) return d;
            Dictionary<string, object> id = exact[0];
            d.Found = true;
            d.Matches = exact.Count;
            d.Manufacturer = Http.Str(id, "manufacturer");
            d.Model = Http.Str(id, "model");
            d.GroupTag = Http.Str(id, "groupTag");
            d.User = Http.Str(id, "userPrincipalName");
            d.EnrollmentState = Http.Str(id, "enrollmentState");
            d.AssignmentStatus = Http.Str(id, "deploymentProfileAssignmentStatus");
            d.LastContactUtc = IntuneCatalog.Time(id, "lastContactedDateTime");
            d.EntraId = ReadinessCheck.EntraId(id);
            string status = (d.AssignmentStatus ?? "").ToLowerInvariant();
            if (status != "notassigned") ReadProfile(d, Http.Str(id, "id"));
            ReadDeviceGroups(d);
            if (d.HasUser) ReadUserGroups(d);
            return d;
        }

        void ReadProfile(PfDevice d, string identityId)
        {
            if (string.IsNullOrEmpty(identityId)) return;
            try
            {
                Dictionary<string, object> p = One(graph.Get(ReadinessCheck.ProfileUrl(identityId, false)));
                if (string.IsNullOrEmpty(Http.Str(p, "displayName")))
                {
                    try { p = One(graph.Get(ReadinessCheck.ProfileUrl(identityId, true))); }
                    catch (GraphException) { p = null; }
                }
                if (p == null) return;
                d.ProfileName = Http.Str(p, "displayName");
                Dictionary<string, object> oobe = ImeJson.Get(p, "outOfBoxExperienceSetting") as Dictionary<string, object> ?? ImeJson.Get(p, "outOfBoxExperienceSettings") as Dictionary<string, object>;
                d.SelfDeploying = string.Equals(Http.Str(oobe, "deviceUsageType"), "shared", StringComparison.OrdinalIgnoreCase);
            }
            catch (GraphException ex)
            {
                if (ex.Status == 404) return;
                d.ProfileProblem = IntuneLookup.Problem("The deployment profile", ReadinessCheck.Scope, ex).Replace(" were not read", " was not read");
            }
        }

        static Dictionary<string, object> One(Dictionary<string, object> r)
        {
            Dictionary<string, object> inner = ImeJson.Get(r, "value") as Dictionary<string, object>;
            return inner ?? r;
        }

        void ReadDeviceGroups(PfDevice d)
        {
            // Without an Entra device object no group can hold the device: its groups are known, and there are none.
            if (string.IsNullOrEmpty(d.EntraId))
            {
                d.Groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return;
            }
            if (!ReadGroups)
            {
                d.GroupsOff = true;
                d.GroupsProblem = "The groups of the device were not read: Groups is turned off.";
                return;
            }
            try { d.Groups = ReadinessCheck.Groups(graph, d.EntraId); }
            catch (GraphException ex)
            {
                if (ex.Status == 404) d.Groups = new Dictionary<string, string>();
                else d.GroupsProblem = Refused("The device's groups were not read", ex, "Device.Read.All");
            }
            catch (Exception ex) { d.GroupsProblem = "The device's groups were not read: " + GraphClient.Clean(ex.Message) + "."; }
        }

        void ReadUserGroups(PfDevice d)
        {
            if (!ReadGroups)
            {
                d.GroupsOff = true;
                d.UserGroupsProblem = "The user's groups were not read: Groups is turned off.";
                return;
            }
            Dictionary<string, string> kept;
            string problem;
            if (userGroups.TryGetValue(d.User, out kept)) { d.UserGroups = kept; return; }
            if (userProblems.TryGetValue(d.User, out problem)) { d.UserGroupsProblem = problem; return; }
            try
            {
                kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string url = UserGroupsUrl(d.User);
                for (int page = 0; page < MaxPages && url != null; page++)
                {
                    Dictionary<string, object> r = graph.Get(url);
                    foreach (Dictionary<string, object> x in Values(r))
                    {
                        string gid = Http.Str(x, "id");
                        if (!string.IsNullOrEmpty(gid)) kept[gid] = Http.Str(x, "displayName") ?? gid;
                    }
                    url = Http.Str(r, "@odata.nextLink");
                }
                userGroups[d.User] = kept;
                d.UserGroups = kept;
            }
            catch (GraphException ex)
            {
                problem = ex.Status == 404 ? "The user " + d.User + " was not found in Entra." : Refused("The user's groups were not read", ex, "GroupMember.Read.All");
                userProblems[d.User] = problem;
                d.UserGroupsProblem = problem;
            }
        }

        static string Refused(string what, GraphException ex, string permission)
        {
            if (ex.Status == 403 || ex.Status == 401) return what + ": access denied. The sign-in needs consent for " + permission + ".";
            return what + ": " + GraphClient.Clean(ex.Message) + ".";
        }

        // ---------- the tenant ----------

        public PfTenant Tenant()
        {
            if (tenant != null) return tenant;
            PfTenant t = new PfTenant();
            t.Times = Times;
            try { t.Esps = ReadinessCheck.ReadEsps(graph); }
            catch (Exception ex) { t.EspProblem = IntuneLookup.Problem("The Enrollment Status Pages", ReadinessCheck.Scope, ex); }
            if (!ReadAppList)
            {
                t.AppsOff = true;
                t.AppsProblem = "The apps were not read: Apps is turned off.";
            }
            else
            {
                try { t.Apps = ReadApps(graph); }
                catch (Exception ex) { t.AppsProblem = IntuneLookup.Problem("The apps", LookupChoice.AppScope, ex); }
            }
            if (t.Apps != null && UsesFilter(t.Apps))
            {
                try { t.Filters = IntuneLookup.Names(graph, FiltersUrl); }
                catch (Exception ex)
                {
                    GraphException ge = ex as GraphException;
                    t.FiltersProblem = ge != null && (ge.Status == 403 || ge.Status == 401) ? "The names of the filters need DeviceManagementConfiguration.Read.All." : "The names of the filters were not read.";
                }
            }
            tenant = t;
            return t;
        }

        // The Windows apps that are assigned, by ID.
        public static Dictionary<string, PfApp> ReadApps(IGraph g)
        {
            Dictionary<string, PfApp> apps = new Dictionary<string, PfApp>(StringComparer.OrdinalIgnoreCase);
            string url = AppsUrl;
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> x in Values(r))
                {
                    PfApp a = ToApp(x);
                    if (a != null) apps[a.Id] = a;
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            return apps;
        }

        public static PfApp ToApp(Dictionary<string, object> x)
        {
            string type = Http.Str(x, "@odata.type");
            string id = (Http.Str(x, "id") ?? "").Trim('{', '}');
            if (id.Length == 0 || !PfApp.IsWindowsType(type)) return null;
            PfApp a = new PfApp();
            a.Id = id;
            a.Type = type;
            a.Name = Http.Str(x, "displayName") ?? "App " + id.Substring(0, Math.Min(8, id.Length));
            Dictionary<string, object> exp = ImeJson.Get(x, "installExperience") as Dictionary<string, object>;
            a.RunAsUser = string.Equals(Http.Str(exp, "runAsAccount"), "user", StringComparison.OrdinalIgnoreCase);
            List<object> list = ImeJson.Get(x, "assignments") as List<object>;
            if (list != null)
                foreach (object o in list)
                {
                    Dictionary<string, object> asg = o as Dictionary<string, object>;
                    EspTarget t = asg == null ? null : ReadinessCheck.ToTarget(ImeJson.Get(asg, "target") as Dictionary<string, object>);
                    if (t == null) continue;
                    PfAssign pa = new PfAssign(Http.Str(asg, "intent"), t.Kind, t.GroupId);
                    Dictionary<string, object> target = ImeJson.Get(asg, "target") as Dictionary<string, object>;
                    string filter = (Http.Str(target, "deviceAndAppManagementAssignmentFilterId") ?? "").Trim('{', '}');
                    string filterType = Http.Str(target, "deviceAndAppManagementAssignmentFilterType");
                    if (filter.Length > 0 && filter != ZeroGuid && !string.Equals(filterType, "none", StringComparison.OrdinalIgnoreCase)) pa.FilterId = filter;
                    a.Assignments.Add(pa);
                }
            return a;
        }

        static bool UsesFilter(Dictionary<string, PfApp> apps)
        {
            foreach (PfApp a in apps.Values)
                foreach (PfAssign x in a.Assignments) if (x.Filtered) return true;
            return false;
        }

        // The relationships of these apps, and of the apps they point to, a few steps deep.
        void LoadRelations(PfTenant t, List<PfApp> apps)
        {
            if (t.RelationsProblem != null) return;
            List<string> level = new List<string>();
            foreach (PfApp a in apps) level.Add(a.Id);
            for (int depth = 0; depth < MaxDepth && level.Count > 0; depth++)
            {
                List<string> next = new List<string>();
                foreach (string id in level)
                {
                    if (t.Relations.ContainsKey(id)) continue;
                    if (relationCalls >= MaxRelationCalls) return;
                    relationCalls++;
                    List<PfRel> rels;
                    try { rels = ReadRelations(graph, id); }
                    catch (Exception ex)
                    {
                        t.RelationsProblem = IntuneLookup.Problem("The app relationships", LookupChoice.AppScope, ex);
                        return;
                    }
                    t.Relations[id] = rels;
                    foreach (PfRel x in rels) if (!string.IsNullOrEmpty(x.TargetId)) next.Add(x.TargetId.Trim('{', '}'));
                }
                level = next;
            }
        }

        // Only the apps this app points to: its dependencies and the apps it supersedes.
        public static List<PfRel> ReadRelations(IGraph g, string appId)
        {
            List<PfRel> list = new List<PfRel>();
            string url = RelationsUrl(appId);
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> x in Values(r))
                {
                    string type = (Http.Str(x, "@odata.type") ?? "").ToLowerInvariant();
                    string side = Http.Str(x, "targetType");
                    if (side != null && !string.Equals(side, "child", StringComparison.OrdinalIgnoreCase)) continue;
                    PfRel rel = new PfRel();
                    if (type.Contains("supersedence")) { rel.Supersedence = true; rel.Type = Http.Str(x, "supersedenceType"); }
                    else if (type.Contains("dependency")) rel.Type = Http.Str(x, "dependencyType");
                    else continue;
                    rel.TargetId = (Http.Str(x, "targetId") ?? "").Trim('{', '}');
                    rel.TargetName = Http.Str(x, "targetDisplayName");
                    if (rel.TargetId.Length == 0) continue;
                    list.Add(rel);
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            return list;
        }

        // ---------- earlier deployments ----------

        // The median times of the deployments of this customer that the window has seen. Null when there are none.
        public static PfTimes TimesFrom(DeploymentHistory history, string customer)
        {
            if (history == null) return null;
            HistoryStats s = history.Get(customer ?? "", HistoryKind, null);
            if (s == null || s.Count == 0) return null;
            PfTimes t = new PfTimes();
            t.Runs = s.Count;
            foreach (KeyValuePair<string, double> kv in s.Items)
            {
                int bar = kv.Key.IndexOf('|');
                string name = bar >= 0 ? kv.Key.Substring(bar + 1) : kv.Key;
                double have;
                if (!t.AppSeconds.TryGetValue(name, out have) || kv.Value > have) t.AppSeconds[name] = kv.Value;
            }
            double v;
            if (s.Phases.TryGetValue(Phases.DeviceSetup, out v)) t.EspSeconds += v;
            if (s.Phases.TryGetValue(Phases.AccountSetup, out v)) t.EspSeconds += v;
            return t;
        }
    }
}
