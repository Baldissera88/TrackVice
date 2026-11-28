namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The pre-flight check: before a device is deployed, would Intune deploy it without trouble? This file holds
    // the evaluation only. It takes plain objects (what Graph said about the device, the Enrollment Status Pages and
    // the apps) and gives findings, so it can be tested without a tenant. PreflightGraph.cs reads the objects.

    public enum PfLevel { Ok, Warn, Fail }

    public enum PfVerdict { Ready, Check, WillFail }

    // One line of the checklist: what was found, in a plain sentence, and what to do about it.
    public sealed class PfFinding
    {
        public PfLevel Level;
        public string Group, Check, Text, Fix;

        public PfFinding(PfLevel level, string group, string check, string text, string fix)
        {
            Level = level;
            Group = group;
            Check = check;
            Text = text;
            Fix = fix;
        }
    }

    // What Intune and Entra say about one device, by serial number.
    public sealed class PfDevice
    {
        public string Serial;
        public bool Found;
        // Several Autopilot records have this serial number.
        public int Matches = 1;
        // The registration could not be read, with a sentence that says why.
        public string Problem;
        public string GroupTag, Manufacturer, Model, User, EnrollmentState, ProfileName, EntraId;
        // assignedInSync, assignedOutOfSync, pending, failed, notAssigned and so on, as Autopilot writes it.
        public string AssignmentStatus;
        public DateTime? LastContactUtc;
        // The deployment profile could not be read (a sentence), or says the device is for shared use.
        public string ProfileProblem;
        public bool SelfDeploying;
        // The groups of the Entra device, by ID. Null when they were not read, with GroupsProblem saying why.
        public Dictionary<string, string> Groups;
        public string GroupsProblem;
        // Reading groups was turned off in the dialog, so nothing is missing from the sign-in.
        public bool GroupsOff;
        // The groups of the assigned user. Null when there is no user or they were not read.
        public Dictionary<string, string> UserGroups;
        public string UserGroupsProblem;

        public string Title
        {
            get
            {
                string t = ((Manufacturer ?? "") + " " + (Model ?? "")).Trim();
                return t.Length > 0 ? t : Serial;
            }
        }

        public bool HasUser
        {
            get { return !string.IsNullOrEmpty(User); }
        }
    }

    // One assignment of an app: an intent for a target.
    public sealed class PfAssign
    {
        public const string Required = "required", Available = "available", Uninstall = "uninstall";
        // required, available, uninstall or availableWithoutEnrollment.
        public string Intent;
        // EspTarget.AllDevices, AllUsers, Group or Exclude.
        public string Kind, GroupId;
        // The assignment filter on this assignment, or null. A filter is evaluated when the device checks in, not here.
        public string FilterId;

        public PfAssign(string intent, string kind, string groupId)
        {
            Intent = (intent ?? "").ToLowerInvariant();
            Kind = kind;
            GroupId = groupId;
        }

        public bool Filtered
        {
            get { return !string.IsNullOrEmpty(FilterId); }
        }
    }

    // A relationship of an app with a child app: it needs it (dependency) or it replaces it (supersedence).
    public sealed class PfRel
    {
        public bool Supersedence;
        public string TargetId, TargetName;
        // For a dependency: detect (the child must be there already) or autoInstall. For supersedence: update or replace.
        public string Type;

        public bool AutoInstall
        {
            get { return !Supersedence && string.Equals(Type, "autoInstall", StringComparison.OrdinalIgnoreCase); }
        }
    }

    public sealed class PfApp
    {
        public string Id, Name, Type;
        // A Win32 app that installs for the signed-in user and not for the device.
        public bool RunAsUser;
        public readonly List<PfAssign> Assignments = new List<PfAssign>();

        // win32CatalogApp is the Win32 app of the Enterprise App Catalog.
        public bool IsWin32
        {
            get { return Has("win32LobApp") || Has("win32CatalogApp"); }
        }

        // A line-of-business MSI, which Intune installs through the MDM channel and not through the management extension.
        public bool IsMsi
        {
            get { return Has("windowsMobileMSI"); }
        }

        bool Has(string type)
        {
            return (Type ?? "").IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool Assigned
        {
            get { return Assignments.Count > 0; }
        }

        // Apps of other platforms are left out, so an iOS or Android app never shows up for a Windows device.
        public static bool IsWindowsType(string odataType)
        {
            string t = (odataType ?? "").ToLowerInvariant();
            foreach (string w in new string[] { "win32lobapp", "win32catalogapp", "windowsmobilemsi", "windowsappx", "windowsuniversalappx", "windowsstoreapp", "microsoftstoreforbusinessapp", "wingetapp", "officesuiteapp", "windowsmicrosoftedgeapp", "windowswebapp" })
                if (t.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    // What earlier deployments say about how long things take, in seconds. Empty when there is no history.
    public sealed class PfTimes
    {
        public readonly Dictionary<string, double> AppSeconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // The median time of the device and account setup phases together, 0 when not known.
        public double EspSeconds;
        public int Runs;
    }

    // What the tenant has: Enrollment Status Pages, apps with their assignments and relationships.
    public sealed class PfTenant
    {
        public List<EspConfig> Esps;
        public string EspProblem;
        // Assigned apps by ID. Null when they were not read, with AppsProblem saying why.
        public Dictionary<string, PfApp> Apps;
        public string AppsProblem;
        // Reading apps was turned off in the dialog.
        public bool AppsOff;
        // Assignment filters by ID, read only when an app uses one. Null when they were not read.
        public Dictionary<string, string> Filters;
        public string FiltersProblem;
        // Relationships by app ID, for the apps that were looked at.
        public readonly Dictionary<string, List<PfRel>> Relations = new Dictionary<string, List<PfRel>>(StringComparer.OrdinalIgnoreCase);
        public string RelationsProblem;
        public PfTimes Times;

        public PfApp App(string id)
        {
            PfApp a;
            return Apps != null && id != null && Apps.TryGetValue(id.Trim('{', '}'), out a) ? a : null;
        }
    }

    public sealed class PfResult
    {
        public string Serial, Title;
        public readonly List<PfFinding> Findings = new List<PfFinding>();
        // What Intune knows about the device, as label and value: group tag, profile, user and so on.
        public readonly List<string[]> Facts = new List<string[]>();

        public PfFinding Add(PfLevel level, string group, string check, string text, string fix)
        {
            PfFinding f = new PfFinding(level, group, check, text, fix);
            Findings.Add(f);
            return f;
        }

        public int Count(PfLevel level)
        {
            int n = 0;
            foreach (PfFinding f in Findings) if (f.Level == level) n++;
            return n;
        }

        public PfVerdict Verdict
        {
            get { return Count(PfLevel.Fail) > 0 ? PfVerdict.WillFail : Count(PfLevel.Warn) > 0 ? PfVerdict.Check : PfVerdict.Ready; }
        }

        public string VerdictText
        {
            get { return Preflight.VerdictText(Verdict); }
        }
    }

    public static class Preflight
    {
        public const string GroupAutopilot = "Windows Autopilot", GroupGroups = "Groups", GroupEsp = "Enrollment Status Page", GroupApps = "Required apps";

        // The permissions the check can use, for the header of the dialog. Each check goes on without the ones it lacks.
        public const string Permissions = "DeviceManagementServiceConfig.Read.All, DeviceManagementApps.Read.All, DeviceManagementConfiguration.Read.All, GroupMember.Read.All, Device.Read.All";

        // Each permission with what the check reads with it.
        public static readonly string[][] PermissionUse =
        {
            new string[] { "DeviceManagementServiceConfig.Read.All", "Autopilot devices, deployment profiles and Enrollment Status Pages" },
            new string[] { "DeviceManagementApps.Read.All", "Apps with their assignments, dependencies and supersedence" },
            new string[] { "DeviceManagementConfiguration.Read.All", "The names of assignment filters" },
            new string[] { "Device.Read.All", "The groups of the device" },
            new string[] { "GroupMember.Read.All", "Group names, and the groups of the assigned user" }
        };

        public static string VerdictText(PfVerdict v)
        {
            return v == PfVerdict.WillFail ? "Will fail" : v == PfVerdict.Check ? "Check" : "Ready";
        }

        static string N(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        static string Plural(int n, string one, string many)
        {
            return N(n) + " " + (n == 1 ? one : many);
        }

        public static string Minutes(double seconds)
        {
            int m = (int)Math.Round(seconds / 60.0);
            if (m < 1) m = 1;
            return N(m) + " min";
        }

        // ---------- which apps a device gets ----------

        // The apps that are required for the device, directly or through its user. Windows apps only.
        // groupsKnown is false when the device's groups were not read, so only apps for all devices or all users show.
        public static List<PfApp> RequiredFor(PfDevice d, PfTenant t, out bool groupsKnown)
        {
            List<PfApp> list = new List<PfApp>();
            groupsKnown = d.Groups != null;
            if (t.Apps == null) return list;
            foreach (PfApp a in t.Apps.Values)
            {
                bool viaDevice;
                if (Applies(a, PfAssign.Required, d, out viaDevice)) list.Add(a);
            }
            list.Sort(delegate (PfApp x, PfApp y) { return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        // True when an assignment with this intent reaches the device or its user, and no exclusion keeps it out.
        // viaDevice tells that the app is assigned to the device (all devices or a device group) and not to the user.
        public static bool Applies(PfApp a, string intent, PfDevice d, out bool viaDevice)
        {
            viaDevice = false;
            List<PfAssign> hits = Hits(a, intent, d);
            foreach (PfAssign x in hits)
                if (x.Kind == EspTarget.AllDevices || (x.Kind == EspTarget.Group && InGroup(d.Groups, x.GroupId))) viaDevice = true;
            return hits.Count > 0;
        }

        // The assignments with this intent that reach the device or its user. None when a group of the device or the user
        // is excluded from the intent. In self-deploying mode nobody signs in, so nothing reaches the device through a user.
        static List<PfAssign> Hits(PfApp a, string intent, PfDevice d)
        {
            List<PfAssign> hits = new List<PfAssign>();
            if (!PfApp.IsWindowsType(a.Type)) return hits;
            bool user = !d.SelfDeploying;
            foreach (PfAssign x in a.Assignments)
                if (x.Kind == EspTarget.Exclude && x.Intent == intent && x.GroupId != null && (InGroup(d.Groups, x.GroupId) || (user && InGroup(d.UserGroups, x.GroupId)))) return hits;
            foreach (PfAssign x in a.Assignments)
            {
                if (x.Intent != intent) continue;
                if (x.Kind == EspTarget.AllDevices) hits.Add(x);
                else if (x.Kind == EspTarget.AllUsers) { if (user) hits.Add(x); }
                else if (x.Kind == EspTarget.Group && x.GroupId != null && (InGroup(d.Groups, x.GroupId) || (user && InGroup(d.UserGroups, x.GroupId)))) hits.Add(x);
            }
            return hits;
        }

        static bool InGroup(Dictionary<string, string> groups, string id)
        {
            return groups != null && id != null && groups.ContainsKey(id);
        }

        // ---------- the check ----------

        public static PfResult Evaluate(PfDevice d, PfTenant t)
        {
            PfResult r = new PfResult();
            r.Serial = d.Serial;
            r.Title = d.Title;
            if (!Autopilot(r, d)) return r;
            Facts(r, d);
            GroupLines(r, d);
            EspConfig esp = Esp(r, d, t, !d.SelfDeploying);
            Apps(r, d, t, esp);
            InOrder(r);
            return r;
        }

        // The lines of one part together, in the order the parts are checked.
        static void InOrder(PfResult r)
        {
            List<PfFinding> sorted = new List<PfFinding>();
            foreach (string g in new string[] { GroupAutopilot, GroupGroups, GroupEsp, GroupApps })
                foreach (PfFinding f in r.Findings) if (f.Group == g) sorted.Add(f);
            foreach (PfFinding f in r.Findings) if (!sorted.Contains(f)) sorted.Add(f);
            r.Findings.Clear();
            r.Findings.AddRange(sorted);
        }

        public static List<PfResult> EvaluateAll(List<PfDevice> devices, PfTenant t)
        {
            List<PfResult> l = new List<PfResult>();
            foreach (PfDevice d in devices) l.Add(Evaluate(d, t));
            return l;
        }

        static void Fact(PfResult r, string label, string value)
        {
            if (!string.IsNullOrEmpty(value)) r.Facts.Add(new string[] { label, value });
        }

        static void Facts(PfResult r, PfDevice d)
        {
            Fact(r, "Group tag", d.GroupTag);
            Fact(r, "Profile", d.ProfileName);
            Fact(r, "User", d.User);
            Fact(r, "Enrollment", Words(d.EnrollmentState));
            if (d.LastContactUtc.HasValue) Fact(r, "Last contact", Txt.Local(d.LastContactUtc.Value));
        }

        // "notContacted" as "Not contacted".
        static string Words(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Txt.SplitCamel(s).ToLowerInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // False when there is nothing more to check, because the device is not in Autopilot or could not be read.
        static bool Autopilot(PfResult r, PfDevice d)
        {
            const string G = GroupAutopilot;
            if (d.Problem != null)
            {
                r.Add(PfLevel.Warn, G, "Registered in Autopilot", d.Problem, "Sign in with an account that can read Autopilot devices and consent to DeviceManagementServiceConfig.Read.All, then check again.");
                return false;
            }
            if (!d.Found)
            {
                r.Add(PfLevel.Fail, G, "Registered in Autopilot", "Not registered in Windows Autopilot, so the device gets no Autopilot profile and starts the normal Windows setup.", "Import its hardware hash in Intune (Devices > Enrollment > Windows Autopilot), then check again.");
                return false;
            }
            if (d.Matches > 1)
                r.Add(PfLevel.Warn, G, "Registered in Autopilot", "Registered " + N(d.Matches) + " times. The checks use the first record.", "Delete the extra records in Intune.");
            else r.Add(PfLevel.Ok, G, "Registered in Autopilot", "Registered in Windows Autopilot" + (string.IsNullOrEmpty(d.GroupTag) ? ", with no group tag." : ", group tag " + d.GroupTag + "."), null);

            string st = (d.AssignmentStatus ?? "").ToLowerInvariant();
            string profile = string.IsNullOrEmpty(d.ProfileName) ? "A deployment profile" : "The profile " + d.ProfileName;
            if (st == "notassigned")
            {
                string where = d.Groups != null && d.Groups.Count > 0 ? "one of its groups (" + Names(d.Groups, 3) + ")" : !string.IsNullOrEmpty(d.GroupTag) ? "a group that holds the devices with group tag " + d.GroupTag : "a group that holds the device";
                r.Add(PfLevel.Fail, G, "Deployment profile", "No Autopilot deployment profile is assigned, so the device would not run Autopilot.", "Assign an Autopilot profile to " + where + ".");
            }
            else if (st == "failed")
                r.Add(PfLevel.Fail, G, "Deployment profile", "Assigning the deployment profile failed.", "Open the device in Intune for the reason, then assign the profile again.");
            else if (st == "pending")
                r.Add(PfLevel.Warn, G, "Deployment profile", profile + " is still being assigned.", "Wait until Intune shows the profile as assigned before the device is started.");
            // Graph spells this value "assignedUnkownSyncState".
            else if (st == "assignedoutofsync" || st == "assignedunkownsyncstate" || st == "assignedunknownsyncstate")
                r.Add(PfLevel.Warn, G, "Deployment profile", profile + " is assigned, but Autopilot may not have the latest version yet.", "Wait a few minutes, or sync the Autopilot devices in Intune, and check again.");
            else if (st == "assignedinsync")
                r.Add(PfLevel.Ok, G, "Deployment profile", profile + " is assigned." + (d.SelfDeploying ? " It is a self-deploying profile." : ""), null);
            else r.Add(PfLevel.Warn, G, "Deployment profile", "Intune does not say whether a profile is assigned" + (st.Length > 0 ? " (" + Txt.SplitCamel(d.AssignmentStatus).ToLowerInvariant() + ")." : "."), "Open the device in Intune and look at its profile status.");
            if (d.ProfileProblem != null)
                r.Add(PfLevel.Warn, G, "Profile details", d.ProfileProblem, "Consent to DeviceManagementServiceConfig.Read.All to see the profile.");

            string state = (d.EnrollmentState ?? "").ToLowerInvariant();
            if (state == "blocked") r.Add(PfLevel.Fail, G, "Enrollment state", "Intune blocks the enrollment of this device.", "Check the enrollment restrictions and the device in Intune.");
            else if (state == "failed") r.Add(PfLevel.Warn, G, "Enrollment state", "The last enrollment of this device failed.", "Look at that enrollment in Intune before starting the device again.");
            else if (state == "enrolled") r.Add(PfLevel.Ok, G, "Enrollment state", "Intune lists it as enrolled. After a reset it enrolls again with the same profile.", null);
            else if (state == "pendingreset") r.Add(PfLevel.Ok, G, "Enrollment state", "A reset is pending.", null);
            else r.Add(PfLevel.Ok, G, "Enrollment state", "Not enrolled yet" + (d.LastContactUtc.HasValue ? ", last contact " + Txt.Local(d.LastContactUtc.Value) + "." : "."), null);

            if (d.HasUser) r.Add(PfLevel.Ok, G, "Assigned user", d.User + " is assigned to the device.", null);
            return true;
        }

        static string Names(Dictionary<string, string> groups, int max)
        {
            return Some(new List<string>(groups.Values), max);
        }

        // The first few names in order, "and 3 more" for the rest.
        static string Some(List<string> names, int max)
        {
            names.Sort(StringComparer.OrdinalIgnoreCase);
            string s = string.Join(", ", names.GetRange(0, Math.Min(max, names.Count)).ToArray());
            return names.Count > max ? s + " and " + N(names.Count - max) + " more" : s;
        }

        // Without GroupMember.Read.All Graph gives the groups by ID only.
        static bool Unnamed(Dictionary<string, string> groups)
        {
            if (groups == null || groups.Count == 0) return false;
            foreach (KeyValuePair<string, string> kv in groups) if (!string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        static void GroupLines(PfResult r, PfDevice d)
        {
            const string G = GroupGroups;
            const string turnOn = "Turn on Groups in this dialog and check again.";
            if (string.IsNullOrEmpty(d.EntraId))
                r.Add(PfLevel.Warn, G, "Entra device", "There is no Entra device object yet, so no group can hold the device and nothing is assigned to it.", "Wait for Autopilot to create the device object after the import, then check again.");
            else if (d.Groups == null)
                r.Add(PfLevel.Warn, G, "Device groups", (d.GroupsProblem ?? "The groups of the device were not read.") + " Apps and Enrollment Status Pages that target groups were not checked.", d.GroupsOff ? turnOn : "Consent to Device.Read.All and GroupMember.Read.All for the sign-in, then check again.");
            else if (d.Groups.Count == 0)
                r.Add(PfLevel.Warn, G, "Device groups", "The device is in no group, so nothing that targets a group reaches it.", "Add the device to the group that carries its apps, or check the dynamic rule on its group tag" + (string.IsNullOrEmpty(d.GroupTag) ? " (it has none)." : " (" + d.GroupTag + ")."));
            else if (Unnamed(d.Groups))
                r.Add(PfLevel.Ok, G, "Device groups", "In " + Plural(d.Groups.Count, "group", "groups") + ". Their names were not given: that needs GroupMember.Read.All. The checks use their IDs.", null);
            else r.Add(PfLevel.Ok, G, "Device groups", "In " + Plural(d.Groups.Count, "group", "groups") + ": " + Names(d.Groups, 4) + ".", null);

            if (!d.HasUser || d.SelfDeploying || string.IsNullOrEmpty(d.EntraId)) return;
            if (d.UserGroups != null) r.Add(PfLevel.Ok, G, "User groups", d.User + " is in " + Plural(d.UserGroups.Count, "group", "groups") + ".", null);
            else if (d.UserGroupsProblem != null)
                r.Add(PfLevel.Warn, G, "User groups", d.UserGroupsProblem + " Apps that target the user's groups were not checked.", d.GroupsOff ? turnOn : "Consent to GroupMember.Read.All for the sign-in (Graph may also ask for User.Read.All), then check again.");
        }

        // ---------- Enrollment Status Page ----------

        static EspConfig Esp(PfResult r, PfDevice d, PfTenant t, bool userDriven)
        {
            const string G = GroupEsp;
            if (t.Esps == null)
            {
                r.Add(PfLevel.Warn, G, "Which ESP applies", (t.EspProblem ?? "The Enrollment Status Pages were not read."), "Consent to DeviceManagementServiceConfig.Read.All for the sign-in, then check again.");
                return null;
            }
            EspPick pick = ReadinessCheck.Pick(t.Esps, d.Groups, userDriven);
            if (pick.Config == null)
            {
                r.Add(PfLevel.Warn, G, "Which ESP applies", ("No Enrollment Status Page applies, so setup does not wait for apps. " + (pick.Note ?? "")).Trim(), "Create an ESP and assign it, if the device should wait for its apps.");
                return null;
            }
            EspConfig c = pick.Config;
            r.Add(pick.Unresolved ? PfLevel.Warn : PfLevel.Ok, G, "Which ESP applies", c.Name + ": " + pick.Reason + (pick.Unresolved ? " " + pick.Note : ""),
                pick.Unresolved ? (d.GroupsOff ? "Turn on Groups in this dialog to see which ESP the device gets." : "Read the device's groups (Device.Read.All) to see which ESP it gets.") : null);
            if (!c.Show)
            {
                r.Add(PfLevel.Ok, G, "Progress", "This ESP is not shown, so setup does not wait for apps.", null);
                return null;
            }
            string block = c.AppIds.Count == 0 ? "It blocks device use until all required apps and profiles are installed." : "It blocks device use until " + Plural(c.AppIds.Count, "selected app is", "selected apps are") + " installed.";
            r.Add(PfLevel.Ok, G, "Blocking", block + " Time limit " + N(c.TimeoutMinutes) + " min." + (c.AllowUse ? " The user can continue when an installation fails." : " The device stays on the ESP when an installation fails."), null);
            return c;
        }

        // ---------- apps ----------

        static void Apps(PfResult r, PfDevice d, PfTenant t, EspConfig esp)
        {
            const string G = GroupApps;
            if (t.Apps == null)
            {
                r.Add(PfLevel.Warn, G, "Required apps", (t.AppsProblem ?? "The apps were not read.") + " The apps, their dependencies and the time the ESP needs were not checked.",
                    t.AppsOff ? "Turn on Apps in this dialog and check again." : "Consent to DeviceManagementApps.Read.All for the sign-in, then check again.");
                return;
            }
            bool groupsKnown;
            List<PfApp> required = RequiredFor(d, t, out groupsKnown);
            if (!groupsKnown)
                r.Add(PfLevel.Warn, G, "Apps for groups", "Only the apps for all devices and all users could be listed, because the device's groups were not read.", d.GroupsOff ? "Turn on Groups in this dialog to see the rest." : "Read the device's groups (Device.Read.All, GroupMember.Read.All) to see the rest.");
            r.Add(PfLevel.Ok, G, "Required apps", required.Count == 0 ? "No Windows app is required for this device." : Plural(required.Count, "Windows app is", "Windows apps are") + " required: " + Join(required, 5) + ".", null);

            FilterLine(r, d, t, required);
            foreach (PfApp a in required) Conflicts(r, d, a);
            Mixed(r, required);
            Relations(r, d, t, required);
            if (esp != null) EspTime(r, d, t, esp, required);
        }

        static string Join(List<PfApp> apps, int max)
        {
            List<string> names = new List<string>();
            foreach (PfApp a in apps) names.Add(a.Name);
            return Some(names, max);
        }

        // Apps that reach the device only through assignments with a filter. Whether a filter lets the device through is
        // decided from the properties of the enrolled device, which are not there before it is deployed.
        static void FilterLine(PfResult r, PfDevice d, PfTenant t, List<PfApp> required)
        {
            List<PfApp> filtered = new List<PfApp>();
            List<string> filters = new List<string>();
            foreach (PfApp a in required)
            {
                bool all = true;
                List<PfAssign> hits = Hits(a, PfAssign.Required, d);
                foreach (PfAssign x in hits) if (!x.Filtered) all = false;
                if (!all || hits.Count == 0) continue;
                filtered.Add(a);
                foreach (PfAssign x in hits)
                {
                    string name;
                    if (t.Filters != null && t.Filters.TryGetValue(x.FilterId, out name) && !filters.Contains(name)) filters.Add(name);
                }
            }
            if (filtered.Count == 0) return;
            string text = Plural(filtered.Count, "of them is", "of them are") + " assigned with a filter (" + Join(filtered, 3) + ")";
            if (filters.Count > 0) text += ": " + Some(filters, 3);
            text += ". A filter is evaluated when the device checks in, so this check cannot tell whether it lets the device through.";
            if (t.Filters == null && t.FiltersProblem != null) text += " " + t.FiltersProblem;
            r.Add(PfLevel.Ok, GroupApps, "Assignment filters", text, null);
        }

        // An app that is required and also set to uninstall for the same device. Intune resolves that to required.
        static void Conflicts(PfResult r, PfDevice d, PfApp a)
        {
            bool viaDevice;
            if (Applies(a, PfAssign.Uninstall, d, out viaDevice))
                r.Add(PfLevel.Warn, GroupApps, a.Name, a.Name + " is both required and set to uninstall for this device. Intune resolves the conflict to required, so it is installed and the uninstall does nothing here.", "Remove one of the two assignments of " + a.Name + ", or exclude the device's group from the uninstall.");
        }

        // Microsoft advises against line-of-business MSI apps next to Win32 apps during Autopilot: both installers can run
        // at the same time and one of them then fails.
        static void Mixed(PfResult r, List<PfApp> required)
        {
            List<PfApp> msi = new List<PfApp>();
            bool win32 = false;
            foreach (PfApp a in required)
            {
                if (a.IsMsi) msi.Add(a);
                else if (a.IsWin32) win32 = true;
            }
            if (msi.Count == 0 || !win32) return;
            r.Add(PfLevel.Warn, GroupApps, "App types", Plural(msi.Count, "line-of-business MSI app is", "line-of-business MSI apps are") + " required next to Win32 apps (" + Join(msi, 3) + "). During Autopilot the two kinds can install at the same time, and one of them then fails.",
                "Package " + (msi.Count == 1 ? msi[0].Name : "the MSI apps") + " as a Win32 app (.intunewin).");
        }

        static void Relations(PfResult r, PfDevice d, PfTenant t, List<PfApp> required)
        {
            if (t.RelationsProblem != null)
            {
                r.Add(PfLevel.Warn, GroupApps, "Dependencies", t.RelationsProblem + " Dependencies and supersedence were not checked.", "Consent to DeviceManagementApps.Read.All for the sign-in, then check again.");
                return;
            }
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> auto = new List<string>();
            foreach (PfApp a in required)
            {
                List<PfRel> rels;
                if (!t.Relations.TryGetValue(a.Id, out rels)) continue;
                foreach (PfRel x in rels)
                {
                    if (x.Supersedence || string.IsNullOrEmpty(x.TargetId)) continue;
                    if (!done.Add(a.Id + ">" + x.TargetId)) continue;
                    PfApp child = t.App(x.TargetId);
                    string name = child != null ? child.Name : (x.TargetName ?? x.TargetId);
                    bool viaDevice;
                    bool isRequired = child != null && Applies(child, PfAssign.Required, d, out viaDevice);
                    if (child != null && !isRequired && Applies(child, PfAssign.Uninstall, d, out viaDevice))
                        r.Add(PfLevel.Warn, GroupApps, a.Name, a.Name + " needs " + name + ", which is set to uninstall for this device. The two assignments work against each other.", "Remove the uninstall assignment of " + name + " or the dependency on it.");
                    else if (isRequired) continue;
                    else if (x.AutoInstall)
                    {
                        // Intune installs such a dependency by itself; it needs no assignment of its own.
                        if (child == null || !child.Assigned) auto.Add(name + " (for " + a.Name + ")");
                    }
                    else if (child == null || !child.Assigned)
                        r.Add(PfLevel.Fail, GroupApps, a.Name, a.Name + " needs " + name + " to be there already (the dependency is not installed automatically), and " + name + " is not assigned to anyone, so " + a.Name + " will not install.", "Set the dependency to install automatically, or assign " + name + " as required to the device.");
                    else if (d.Groups == null)
                        r.Add(PfLevel.Warn, GroupApps, a.Name, a.Name + " needs " + name + " to be there already (the dependency is not installed automatically). Whether " + name + " is required for this device could not be told without its groups.", "Read the device's groups, or set the dependency to install automatically.");
                    else
                        r.Add(PfLevel.Fail, GroupApps, a.Name, a.Name + " needs " + name + " to be there already (the dependency is not installed automatically), and " + name + " is not required for this device, so " + a.Name + " will not install during the deployment.", "Set the dependency to install automatically, or assign " + name + " as required to a group the device is in.");
                }
            }
            if (auto.Count > 0)
                r.Add(PfLevel.Ok, GroupApps, "Dependencies", Plural(auto.Count, "dependency is", "dependencies are") + " not assigned on " + (auto.Count == 1 ? "its" : "their") + " own, which is fine: Intune installs " + (auto.Count == 1 ? "it" : "them") + " first. " + Some(auto, 4) + ".", null);
            Loops(r, t, required);
        }

        // Supersedence that leads back to the app it started from.
        static void Loops(PfResult r, PfTenant t, List<PfApp> required)
        {
            HashSet<string> reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PfApp a in required)
            {
                List<string> path = new List<string>();
                List<string> loop = FindLoop(t, a.Id, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (loop == null) continue;
                List<string> sorted = new List<string>(loop);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                if (!reported.Add(string.Join("|", sorted.ToArray()))) continue;
                List<string> names = new List<string>();
                foreach (string id in loop) { PfApp x = t.App(id); names.Add(x != null ? x.Name : id); }
                names.Add(names[0]);
                r.Add(PfLevel.Warn, GroupApps, "Supersedence", "Supersedence goes in a circle: " + string.Join(" > ", names.ToArray()) + ". Each app replaces the other, so which one ends up installed is not certain.", "Remove the supersedence in one direction.");
            }
        }

        static List<string> FindLoop(PfTenant t, string id, List<string> path, HashSet<string> finished)
        {
            int at = IndexOf(path, id);
            if (at >= 0) return path.GetRange(at, path.Count - at);
            if (finished.Contains(id)) return null;
            path.Add(id);
            List<PfRel> rels;
            if (t.Relations.TryGetValue(id, out rels))
                foreach (PfRel x in rels)
                {
                    if (!x.Supersedence || string.IsNullOrEmpty(x.TargetId)) continue;
                    List<string> loop = FindLoop(t, x.TargetId.Trim('{', '}'), path, finished);
                    if (loop != null) return loop;
                }
            path.RemoveAt(path.Count - 1);
            finished.Add(id);
            return null;
        }

        static int IndexOf(List<string> l, string id)
        {
            for (int i = 0; i < l.Count; i++) if (string.Equals(l[i], id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // What the ESP waits for, how long that usually takes and whether it fits in the time limit.
        static void EspTime(PfResult r, PfDevice d, PfTenant t, EspConfig esp, List<PfApp> required)
        {
            const string G = GroupEsp;
            bool selected = esp.AppIds.Count > 0;
            List<PfApp> blocking = new List<PfApp>();
            List<PfApp> notHere = new List<PfApp>();
            int unknown = 0;
            if (!selected) blocking.AddRange(required);
            else
                foreach (string id in esp.AppIds)
                {
                    PfApp a = t.App(id);
                    bool viaDevice;
                    if (a == null) unknown++;
                    // Without the groups it cannot be told, so the app counts.
                    else if (d.Groups == null || Applies(a, PfAssign.Required, d, out viaDevice)) blocking.Add(a);
                    else notHere.Add(a);
                }
            if (unknown > 0)
                r.Add(PfLevel.Warn, G, "Blocking apps", Plural(unknown, "app in the blocking list is", "apps in the blocking list are") + " not assigned to anyone or no longer in Intune, so the ESP does not wait for " + (unknown == 1 ? "it" : "them") + ".", "Take " + (unknown == 1 ? "it" : "them") + " out of the blocking list of " + esp.Name + ".");
            if (notHere.Count > 0)
                r.Add(PfLevel.Ok, G, "Blocking apps", Plural(notHere.Count, "app in the blocking list is", "apps in the blocking list are") + " not required for this device, so the ESP does not wait for " + (notHere.Count == 1 ? "it" : "them") + ": " + Join(notHere, 4) + ".", null);
            List<string> names = new List<string>();
            foreach (PfApp a in blocking)
            {
                names.Add(a.Name);
                bool viaDevice;
                if (!selected || !a.IsWin32 || !a.RunAsUser || !Applies(a, PfAssign.Required, d, out viaDevice) || !viaDevice) continue;
                string fix = "Change the install behaviour of " + a.Name + " to System, or take it out of the blocking list.";
                if (d.SelfDeploying)
                    r.Add(PfLevel.Fail, G, a.Name, a.Name + " installs for the user, but it is assigned to the device and is in the blocking list. Nobody signs in during a self-deploying deployment, so it never installs and the ESP waits until its time limit of " + N(esp.TimeoutMinutes) + " min.", fix);
                else
                    r.Add(PfLevel.Warn, G, a.Name, a.Name + " installs for the user, but it is assigned to the device and is in the blocking list. It cannot install in the device phase, before anyone has signed in, and with pre-provisioning it holds the ESP until its time limit.", fix);
            }
            int n = blocking.Count;
            if (n == 0)
            {
                r.Add(PfLevel.Ok, G, "Time", "The ESP waits for no app on this device; the time limit is " + N(esp.TimeoutMinutes) + " min.", null);
                return;
            }
            double seconds = 0;
            int known = 0;
            if (t.Times != null)
            {
                foreach (string name in names)
                {
                    double s;
                    if (t.Times.AppSeconds.TryGetValue(name, out s)) { seconds += s; known++; }
                }
                // When few of the apps are known by name, the time the ESP phases took as a whole says more.
                if (known == 0 || (!selected && known < names.Count / 2.0)) { seconds = t.Times.EspSeconds; known = seconds > 0 ? names.Count : 0; }
            }
            string head = "ESP blocks on " + Plural(n, "app", "apps");
            if (seconds <= 0 || t.Times == null)
            {
                r.Add(PfLevel.Ok, G, "Time", head + "; timeout is " + N(esp.TimeoutMinutes) + " min. There are no earlier deployments here to say how long they take.", null);
                return;
            }
            string usually = (known < names.Count ? "usually at least " : "usually ") + Minutes(seconds);
            double limit = esp.TimeoutMinutes * 60.0;
            string basis = " Median of " + Plural(t.Times.Runs, "earlier deployment", "earlier deployments") + (known < names.Count ? ", which had " + N(known) + " of the apps." : ".");
            string line = head + ", " + usually + "; timeout is " + N(esp.TimeoutMinutes) + " min.";
            if (seconds >= limit)
                r.Add(PfLevel.Fail, G, "Time", line + basis, "Raise the timeout of " + esp.Name + " above " + Minutes(seconds * 1.3) + ", or block on fewer apps.");
            else if (seconds > limit * 0.75)
                r.Add(PfLevel.Warn, G, "Time", line + " That leaves little room." + basis, "Raise the timeout of " + esp.Name + ", or block on fewer apps.");
            else r.Add(PfLevel.Ok, G, "Time", line + basis, null);
        }

        // ---------- as text ----------

        public static string Mark(PfLevel l)
        {
            return l == PfLevel.Fail ? "✕" : l == PfLevel.Warn ? "!" : "✓";
        }

        public static void Count(List<PfResult> results, out int ready, out int check, out int fail)
        {
            ready = check = fail = 0;
            foreach (PfResult r in results)
            {
                if (r.Verdict == PfVerdict.Ready) ready++;
                else if (r.Verdict == PfVerdict.Check) check++;
                else fail++;
            }
        }

        // Names of apps, groups and profiles come from Intune: one line each, so a name cannot add lines of its own,
        // such as a "✓" line that looks like a passed check.
        public static string Flat(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder b = new StringBuilder(s.Length);
            foreach (char c in s) b.Append(char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c);
            return b.ToString();
        }

        public static string ToText(List<PfResult> results, string customer, DateTime nowUtc)
        {
            StringBuilder b = new StringBuilder();
            b.Append("Pre-flight check").Append(string.IsNullOrEmpty(customer) ? "" : ", " + Flat(customer)).Append(", ").Append(nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\r\n");
            int ready, check, fail;
            Count(results, out ready, out check, out fail);
            b.Append(Plural(results.Count, "device", "devices")).Append(": ").Append(N(ready)).Append(" ready, ").Append(N(check)).Append(" to check, ").Append(N(fail)).Append(" will fail\r\n");
            foreach (PfResult r in results)
            {
                b.Append("\r\n").Append(Flat(r.Serial));
                if (r.Title != r.Serial) b.Append(" (").Append(Flat(r.Title)).Append(")");
                b.Append(": ").Append(r.VerdictText.ToUpperInvariant()).Append("\r\n");
                if (r.Facts.Count > 0)
                {
                    List<string> facts = new List<string>();
                    foreach (string[] f in r.Facts) facts.Add(f[0] + ": " + Flat(f[1]));
                    b.Append("  ").Append(string.Join(", ", facts.ToArray())).Append("\r\n");
                }
                string group = null;
                foreach (PfFinding f in r.Findings)
                {
                    if (f.Group != group) { group = f.Group; b.Append("  ").Append(group).Append("\r\n"); }
                    b.Append("    ").Append(Mark(f.Level)).Append(' ').Append(Flat(f.Text)).Append("\r\n");
                    if (f.Fix != null && f.Level != PfLevel.Ok) b.Append("        Fix: ").Append(Flat(f.Fix)).Append("\r\n");
                }
            }
            return b.ToString();
        }
    }
}
