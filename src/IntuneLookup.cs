namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // What to read from Intune. Each lookup needs a read permission of its own, so each is optional.
    public sealed class LookupChoice
    {
        public bool Scripts, Apps, Remediations, Profiles;

        public const string AppScope = "https://graph.microsoft.com/DeviceManagementApps.Read.All";
        public const string ConfigScope = "https://graph.microsoft.com/DeviceManagementConfiguration.Read.All";

        public bool Any
        {
            get { return Scripts || Apps || Remediations || Profiles; }
        }

        // The scopes the ticked lookups need, besides the one every Intune sign-in asks for.
        public List<string> Scopes()
        {
            List<string> l = new List<string>();
            if (Scripts || Remediations) l.Add(GraphClient.ScriptScope);
            if (Apps) l.Add(AppScope);
            if (Profiles) l.Add(ConfigScope);
            return l;
        }

        public static string Permission(string scope)
        {
            int i = scope.LastIndexOf('/');
            return i >= 0 ? scope.Substring(i + 1) : scope;
        }
    }

    // What one run of the lookups found. A lookup that failed adds a problem and leaves the others alone.
    public sealed class LookupResult
    {
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<Obs> Profiles = new List<Obs>();
        public readonly List<string> Problems = new List<string>();
        public int Scripts = -1, Apps = -1, Remediations = -1;
        public string DeviceNote;
        public bool ProfilesRead;

        // One short line for the status bar.
        public string Summary()
        {
            List<string> parts = new List<string>();
            if (Scripts >= 0) parts.Add(Count(Scripts, "script name"));
            if (Apps >= 0) parts.Add(Count(Apps, "app name"));
            if (Remediations >= 0) parts.Add(Count(Remediations, "remediation name"));
            if (ProfilesRead) parts.Add(Count(Profiles.Count, "configuration profile") + " for this device");
            string s = parts.Count > 0 ? "Read from Intune: " + string.Join(", ", parts.ToArray()) + "." : "";
            if (DeviceNote != null) s += (s.Length > 0 ? " " : "") + DeviceNote;
            foreach (string p in Problems) s += (s.Length > 0 ? " " : "") + p;
            return s;
        }

        static string Count(int n, string what)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + what + (n == 1 ? "" : "s");
        }
    }

    // Reads names and configuration profile states from Intune through Microsoft Graph. Read-only.
    public static class IntuneLookup
    {
        public const string AppsUrl = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$select=id,displayName";
        public const string RemediationsUrl = "https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts?$select=id,displayName";
        public const string DevicesUrl = "https://graph.microsoft.com/beta/deviceManagement/managedDevices";
        public const string ProfilePhase = "intune:profiles";
        public const string ProfilePhaseName = "Configuration profiles";
        const int MaxPages = 200;

        public static LookupResult Run(IGraph g, LookupChoice c, string deviceName, DateTime nowUtc)
        {
            LookupResult r = new LookupResult();
            if (c.Scripts)
            {
                try { r.Scripts = Merge(r.Names, IntuneNames.Scripts(g)); }
                catch (Exception ex) { r.Problems.Add(Problem("Script names", GraphClient.ScriptScope, ex)); }
            }
            if (c.Apps)
            {
                try { r.Apps = Merge(r.Names, Names(g, AppsUrl)); }
                catch (Exception ex) { r.Problems.Add(Problem("App names", LookupChoice.AppScope, ex)); }
            }
            if (c.Remediations)
            {
                try { r.Remediations = Merge(r.Names, Names(g, RemediationsUrl)); }
                catch (Exception ex) { r.Problems.Add(Problem("Remediation names", GraphClient.ScriptScope, ex)); }
            }
            if (c.Profiles)
            {
                if (string.IsNullOrEmpty(deviceName)) r.DeviceNote = "The device has no name yet, so its configuration profiles were not read.";
                else
                {
                    try
                    {
                        string note;
                        string id = FindDevice(g, deviceName, out note);
                        r.DeviceNote = note;
                        if (id != null)
                        {
                            r.Profiles.AddRange(Profiles(g, id, nowUtc));
                            r.ProfilesRead = true;
                        }
                    }
                    catch (Exception ex) { r.Problems.Add(Problem("Configuration profiles", LookupChoice.ConfigScope, ex)); }
                }
            }
            return r;
        }

        static int Merge(Dictionary<string, string> into, Dictionary<string, string> found)
        {
            foreach (KeyValuePair<string, string> kv in found) into[kv.Key] = kv.Value;
            return found.Count;
        }

        // A refusal names the permission that is missing; other errors keep Graph's own message.
        public static string Problem(string what, string scope, Exception ex)
        {
            GraphException ge = ex as GraphException;
            if (ge != null && ge.Status == 403)
                return what + " were not read: access denied. The sign-in needs consent for " + LookupChoice.Permission(scope) + ", and the account an Intune role that can read them.";
            if (ge != null && ge.Status >= 500)
                return what + " were not read: Intune answered with an error (" + GraphClient.Clean(ex.Message) + "). It is usually a passing problem on the Intune side; they are read again later.";
            return what + " were not read: " + GraphClient.Clean(ex.Message);
        }

        // Every id and displayName in a list, page by page.
        public static Dictionary<string, string> Names(IGraph g, string url)
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> d in Values(r))
                {
                    string id = Http.Str(d, "id"), name = Http.Str(d, "displayName");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) names[id.Trim('{', '}')] = name;
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            return names;
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

        public static string DeviceUrl(string deviceName)
        {
            string filter = "deviceName eq '" + deviceName.Replace("'", "''") + "'";
            return DevicesUrl + "?$filter=" + Uri.EscapeDataString(filter) + "&$select=id,deviceName,lastSyncDateTime";
        }

        // The Intune ID of the device with this name. Device names are not unique in Intune, so with more than
        // one match the device that synced last is taken, and the note says so.
        public static string FindDevice(IGraph g, string deviceName, out string note)
        {
            note = null;
            List<Dictionary<string, object>> found = new List<Dictionary<string, object>>();
            string url = DeviceUrl(deviceName);
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                found.AddRange(Values(r));
                url = Http.Str(r, "@odata.nextLink");
            }
            if (found.Count == 0)
            {
                note = "Intune has no device named " + deviceName + ", so no configuration profiles were read.";
                return null;
            }
            Dictionary<string, object> best = null;
            DateTime bestSync = DateTime.MinValue;
            foreach (Dictionary<string, object> d in found)
            {
                DateTime? sync = ImeTime.Parse(Http.Str(d, "lastSyncDateTime"));
                DateTime s = sync ?? DateTime.MinValue;
                if (best == null || s > bestSync) { best = d; bestSync = s; }
            }
            if (found.Count > 1)
                note = "Intune has " + found.Count.ToString(CultureInfo.InvariantCulture) + " devices named " + deviceName + ". The profiles are from the one that synced last" + (bestSync > DateTime.MinValue ? ", " + Txt.Local(bestSync) : "") + ".";
            return Http.Str(best, "id");
        }

        public static string ProfilesUrl(string deviceId)
        {
            return DevicesUrl + "/" + Uri.EscapeDataString(deviceId) + "/deviceConfigurationStates";
        }

        public static string ProfileKey(string id)
        {
            return "intuneprofile:" + (id ?? "").ToLowerInvariant();
        }

        // One observation per configuration profile, with the state Intune last recorded for the device.
        public static List<Obs> Profiles(IGraph g, string deviceId, DateTime nowUtc)
        {
            List<Obs> list = new List<Obs>();
            string url = ProfilesUrl(deviceId);
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> d in Values(r))
                {
                    string id = Http.Str(d, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = Http.Str(d, "displayName");
                    string raw = Http.Str(d, "state") ?? "unknown";
                    string label;
                    State st = ProfileState(raw, out label);
                    Obs o = new Obs();
                    o.TimeUtc = nowUtc;
                    o.Source = "Intune";
                    o.ItemKey = ProfileKey(id);
                    o.ItemName = string.IsNullOrEmpty(name) ? "Configuration profile " + (id.Length >= 8 ? id.Substring(0, 8) : id) : name;
                    o.WeakName = string.IsNullOrEmpty(name);
                    o.ItemKind = Kinds.Profile;
                    o.Origin = Origins.Intune;
                    o.PhaseKey = ProfilePhase;
                    o.PhaseName = ProfilePhaseName;
                    o.PhaseOrder = 94;
                    o.State = st;
                    if (st == State.Failed) o.Severity = 2;
                    o.Text = "Intune reports: " + label;
                    o.AddInfo("ID", id.ToLowerInvariant());
                    o.AddInfo("State in Intune", label);
                    o.AddInfo("Reported by", "Intune, read " + Txt.Local(nowUtc) + ". This is what the device last reported to Intune.");
                    o.AddInfo("Platform", Http.Str(d, "platformType"));
                    o.AddInfo("Settings", Http.Str(d, "settingCount"));
                    o.AddInfo("Version", Http.Str(d, "version"));
                    o.Evidence = new Evidence(EvidenceKinds.Graph, ProfilesUrl(deviceId), 0, "displayName = " + name + ", state = " + raw);
                    list.Add(o);
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            return list;
        }

        // complianceStatus in Graph: unknown, notApplicable, compliant, remediated, nonCompliant, error, conflict, notAssigned.
        public static State ProfileState(string raw, out string label)
        {
            switch ((raw ?? "").ToLowerInvariant())
            {
                case "compliant":
                case "remediated": label = "Succeeded"; return State.Succeeded;
                case "error":
                case "noncompliant": label = "Error"; return State.Failed;
                case "conflict": label = "Conflict"; return State.Failed;
                case "notapplicable": label = "Not applicable"; return State.NotApplicable;
                case "notassigned": label = "Not assigned"; return State.NotApplicable;
                default: label = "Pending"; return State.Pending;
            }
        }

        // Applies profiles to a session and keeps them, so they are applied again after a new upload from the device.
        public static void ApplyProfiles(Engine e, List<Obs> profiles)
        {
            if (e == null || profiles == null || profiles.Count == 0) return;
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Obs o in profiles) keys.Add(o.ItemKey);
            e.Kept.RemoveAll(delegate (Obs o) { return keys.Contains(o.ItemKey); });
            e.Kept.AddRange(profiles);
            Batch b = new Batch();
            b.Obs.AddRange(profiles);
            e.Apply(b);
        }

        // A name a device gave an item when it knew only its ID: the ID itself, or a label with the start of it.
        public static bool WeakName(Item i, string id)
        {
            if (string.IsNullOrEmpty(i.Name) || string.IsNullOrEmpty(id)) return true;
            if (string.Equals(i.Name, id, StringComparison.OrdinalIgnoreCase) || i.Name == i.Key) return true;
            string start = id.Length >= 8 ? id.Substring(0, 8) : id;
            return i.Name.IndexOf(start, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
