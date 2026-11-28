namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // What the viewer knows about the device it asks Intune about.
    public sealed class ExpectedQuery
    {
        // The Intune device ID the device itself read from its MDM enrollment (EntDMID), when known.
        public string IntuneDeviceId;
        public string Serial, DeviceName;
        // The enrolling user's UPN, for a device Intune does not know a primary user of yet.
        public string UserUpn;

        public bool Usable
        {
            get { return GraphClient.IsGuid(IntuneDeviceId) || !string.IsNullOrEmpty(Serial) || !string.IsNullOrEmpty(DeviceName); }
        }

        public static ExpectedQuery Of(Session s)
        {
            ExpectedQuery q = new ExpectedQuery();
            if (s == null) return q;
            q.IntuneDeviceId = s.GetInfo(DeviceFacts.IntuneId);
            // The device says these about itself, so they are only asked about when they look like what they should be.
            q.Serial = GraphLinks.Reported(s.GetInfo(DeviceFacts.Serial) ?? s.GetInfo("Serial"), 128);
            q.DeviceName = GraphLinks.Reported(s.Computer, 128);
            string upn = GraphLinks.Reported(s.GetInfo(DeviceFacts.User), 256);
            if (upn != null && upn.IndexOf('@') > 0) q.UserUpn = upn;
            return q;
        }
    }

    // Reads the apps Intune has assigned as required to a device and its user, with Microsoft Graph. Read-only.
    //
    //   GET beta/deviceManagement/managedDevices/{id}                       the device and its primary user (userId)
    //   GET beta/users/{userId}/mobileAppIntentAndStates/{managedDeviceId}  the apps with their intent for this device and user
    //
    // The second call is what the Intune admin center reads for the apps of a device. It lists what Intune will deliver to the
    // device and the user, also what the device has not asked for yet. Computing it from the assignments and the group
    // memberships of the device and the user would need permission to read groups and directory objects, which TrackVice does
    // not ask for, so there is no such fallback.
    public static class ExpectedIntune
    {
        const string Beta = "https://graph.microsoft.com/beta/";
        const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        const int MaxPages = 50;

        public static string DeviceUrl(string intuneDeviceId)
        {
            return Beta + "deviceManagement/managedDevices/" + Uri.EscapeDataString(intuneDeviceId) + "?$select=id,deviceName,userId,userPrincipalName,serialNumber,lastSyncDateTime";
        }

        public static string FindUrl(string property, string value)
        {
            string filter = property + " eq '" + value.Replace("'", "''") + "'";
            return Beta + "deviceManagement/managedDevices?$filter=" + Uri.EscapeDataString(filter) + "&$select=id,deviceName,userId,userPrincipalName,serialNumber,lastSyncDateTime";
        }

        public static string IntentsUrl(string userId, string managedDeviceId)
        {
            return Beta + "users/" + Uri.EscapeDataString(userId) + "/mobileAppIntentAndStates/" + Uri.EscapeDataString(managedDeviceId);
        }

        public static string IntentsListUrl(string userId)
        {
            return Beta + "users/" + Uri.EscapeDataString(userId) + "/mobileAppIntentAndStates";
        }

        public static string UserUrl(string upn)
        {
            return Beta + "users/" + Uri.EscapeDataString(upn) + "?$select=id";
        }

        // The expected apps of the device, or null with a note that says why not. Throws GraphException for a refusal.
        public static ExpectedSet Read(IGraph g, ExpectedQuery q, DateTime nowUtc, out string note)
        {
            note = null;
            if (q == null || !q.Usable)
            {
                note = "The device is not known well enough to find it in Intune yet.";
                return null;
            }
            Dictionary<string, object> dev = FindDevice(g, q);
            if (dev == null)
            {
                note = "Intune does not have this device yet.";
                return null;
            }
            string deviceId = Http.Str(dev, "id");
            string userId = Http.Str(dev, "userId");
            if (string.IsNullOrEmpty(userId) || userId == ZeroGuid)
            {
                userId = null;
                string upn = Http.Str(dev, "userPrincipalName") ?? q.UserUpn;
                if (!string.IsNullOrEmpty(upn))
                {
                    try { userId = Http.Str(g.Get(UserUrl(upn)), "id"); }
                    catch (GraphException) { }
                }
            }
            if (string.IsNullOrEmpty(userId))
            {
                note = "Intune has no user for this device yet, so it cannot say which apps are assigned.";
                return null;
            }
            Dictionary<string, object> r = Intents(g, userId, deviceId);
            if (r == null)
            {
                note = "Intune has no app list for this device and user yet.";
                return null;
            }
            ExpectedSet set = Parse(r);
            set.ReadUtc = nowUtc;
            return set;
        }

        static Dictionary<string, object> FindDevice(IGraph g, ExpectedQuery q)
        {
            if (GraphClient.IsGuid(q.IntuneDeviceId))
            {
                try { return g.Get(DeviceUrl(q.IntuneDeviceId.Trim('{', '}'))); }
                catch (GraphException ex) { if (ex.Status != 404 && ex.Status != 400) throw; }
            }
            if (!string.IsNullOrEmpty(q.Serial))
            {
                Dictionary<string, object> d = Newest(g, FindUrl("serialNumber", q.Serial));
                if (d != null) return d;
            }
            if (!string.IsNullOrEmpty(q.DeviceName)) return Newest(g, FindUrl("deviceName", q.DeviceName));
            return null;
        }

        // Of the devices a filter finds, the one that synced last.
        static Dictionary<string, object> Newest(IGraph g, string url)
        {
            Dictionary<string, object> best = null;
            DateTime bestSync = DateTime.MinValue;
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> d in Values(r))
                {
                    DateTime s = ImeTime.Parse(Http.Str(d, "lastSyncDateTime")) ?? DateTime.MinValue;
                    if (best == null || s > bestSync) { best = d; bestSync = s; }
                }
                url = GraphLinks.Next(r);
            }
            return best;
        }

        // The entity of this device. Intune keys it by the managed device ID; when that is refused the user's list is read.
        static Dictionary<string, object> Intents(IGraph g, string userId, string deviceId)
        {
            try { return g.Get(IntentsUrl(userId, deviceId)); }
            catch (GraphException ex) { if (ex.Status != 404 && ex.Status != 400) throw; }
            Dictionary<string, object> list = g.Get(IntentsListUrl(userId));
            foreach (Dictionary<string, object> d in Values(list))
                if (string.Equals(Http.Str(d, "managedDeviceIdentifier") ?? Http.Str(d, "id"), deviceId, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
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

        // mobileAppIntent: available, notAvailable, requiredInstall, requiredUninstall, requiredAndAvailableInstall,
        // availableInstallWithoutEnrollment, exclude. The required ones are what the device will be given.
        public static string AssignmentOf(string intent)
        {
            switch ((intent ?? "").Trim().ToLowerInvariant())
            {
                case "requiredinstall":
                case "requiredandavailableinstall": return AppGroups.Required;
                case "requireduninstall": return AppGroups.Uninstall;
                default: return null;
            }
        }

        // mobileAppInstallState: installed, failed, notInstalled, uninstallFailed, pendingInstall, unknown, notApplicable.
        // An app that is already installed or does not apply is not something the deployment still has to bring.
        static bool Open(string assignment, string state)
        {
            string s = (state ?? "").Trim().ToLowerInvariant();
            if (assignment == AppGroups.Uninstall) return s == "installed" || s == "uninstallfailed";
            return s != "installed" && s != "notapplicable";
        }

        // The mobileAppList of one mobileAppIntentAndState, or of the first of a list of them.
        public static ExpectedSet Parse(Dictionary<string, object> r)
        {
            ExpectedSet set = new ExpectedSet();
            set.Source = ExpectedSources.Intune;
            List<object> apps = ImeJson.Get(r, "mobileAppList") as List<object>;
            if (apps == null)
            {
                List<Dictionary<string, object>> v = Values(r);
                if (v.Count > 0) apps = ImeJson.Get(v[0], "mobileAppList") as List<object>;
            }
            if (apps == null) return set;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object o in apps)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                string id = (Http.Str(d, "applicationId") ?? "").Trim('{', '}').ToLowerInvariant();
                string name = Http.Str(d, "displayName");
                string assignment = AssignmentOf(Http.Str(d, "mobileAppIntent"));
                if (id.Length == 0 || assignment == null || !Open(assignment, Http.Str(d, "installState"))) continue;
                if (!seen.Add(id)) continue;
                ExpectedApp a = new ExpectedApp();
                a.Id = id;
                a.Name = string.IsNullOrEmpty(name) ? "App " + id.Substring(0, Math.Min(8, id.Length)) : name.Trim();
                a.Assignment = assignment;
                set.Apps.Add(a);
            }
            return set;
        }

        // A refusal says which permission may be missing.
        public static string Problem(Exception ex)
        {
            GraphException ge = ex as GraphException;
            if (ge != null && ge.Status == 403)
                return "Expected apps were not read: access denied. The sign-in may need consent for " + LookupChoice.Permission(LookupChoice.AppScope) + " (tick App names under Names from Intune and sign in again), and the account an Intune role that can read apps.";
            return "Expected apps were not read: " + GraphClient.Clean(ex.Message);
        }
    }
}
