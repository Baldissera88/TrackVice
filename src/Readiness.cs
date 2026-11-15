namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    public enum ReadyLevel { Ok, Warning, Problem }

    // One check of one device, with one plain sentence.
    public sealed class ReadyLine
    {
        public string Group, Check, Text;
        public ReadyLevel Level;

        public ReadyLine(string group, string check, ReadyLevel level, string text)
        {
            Group = group;
            Check = check;
            Level = level;
            Text = text;
        }

        public string LevelText
        {
            get { return ReadyLines.LevelText(Level); }
        }
    }

    // A device that is about to be enrolled, and what was found for it.
    public sealed class ReadyDevice
    {
        public string Serial, Title;
        public readonly List<ReadyLine> Lines = new List<ReadyLine>();

        public ReadyLine Add(string group, string check, ReadyLevel level, string text)
        {
            ReadyLine l = new ReadyLine(group, check, level, text);
            Lines.Add(l);
            return l;
        }

        public int Count(ReadyLevel level)
        {
            int n = 0;
            foreach (ReadyLine l in Lines) if (l.Level == level) n++;
            return n;
        }

        public ReadyLevel Worst
        {
            get
            {
                ReadyLevel w = ReadyLevel.Ok;
                foreach (ReadyLine l in Lines) if (l.Level > w) w = l.Level;
                return w;
            }
        }

        // "Ready", "2 warnings" or "1 problem, 1 warning".
        public string Summary
        {
            get
            {
                int p = Count(ReadyLevel.Problem), w = Count(ReadyLevel.Warning);
                List<string> parts = new List<string>();
                if (p > 0) parts.Add(p.ToString(CultureInfo.InvariantCulture) + (p == 1 ? " problem" : " problems"));
                if (w > 0) parts.Add(w.ToString(CultureInfo.InvariantCulture) + (w == 1 ? " warning" : " warnings"));
                return parts.Count == 0 ? "Ready" : string.Join(", ", parts.ToArray());
            }
        }
    }

    public static class ReadyLines
    {
        public const string Autopilot = "Windows Autopilot";
        public const string Profile = "Deployment profile";
        public const string Esp = "Enrollment Status Page";
        public const string Prediction = "Prediction";

        public static string LevelText(ReadyLevel l)
        {
            return l == ReadyLevel.Problem ? "Problem" : l == ReadyLevel.Warning ? "Warning" : "OK";
        }

        // Every line of every device, with the device first.
        public static string ToCsv(List<ReadyDevice> devices)
        {
            StringBuilder b = new StringBuilder("Serial number,Device,Part,Check,Result,Details\r\n");
            foreach (ReadyDevice d in devices)
                foreach (ReadyLine l in d.Lines)
                    b.Append(Q(d.Serial)).Append(',').Append(Q(d.Title)).Append(',').Append(Q(l.Group)).Append(',').Append(Q(l.Check)).Append(',')
                     .Append(Q(l.LevelText)).Append(',').Append(Q(l.Text)).Append("\r\n");
            return b.ToString();
        }

        static string Q(string s)
        {
            s = s ?? "";
            // Keep spreadsheets from running a value as a formula.
            if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        // Serial numbers typed one per line, or pasted from a CSV such as a hardware hash export. A header row
        // with a column named like "serial" picks that column; otherwise the first value of each line is taken.
        public static List<string> ParseSerials(string text)
        {
            List<string> list = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return list;
            int col = -1;
            bool first = true;
            foreach (string raw in text.Replace("\r", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] cells = line.Split(new char[] { ',', ';', '\t' });
                for (int i = 0; i < cells.Length; i++) cells[i] = cells[i].Trim().Trim('"').Trim();
                if (first)
                {
                    first = false;
                    for (int i = 0; i < cells.Length; i++)
                        if (cells[i].IndexOf("serial", StringComparison.OrdinalIgnoreCase) >= 0) { col = i; break; }
                    if (col >= 0) continue;
                }
                string v = col >= 0 ? (col < cells.Length ? cells[col] : "") : cells[0];
                if (v.Length == 0 || v.Length > 64 || v.IndexOf(' ') >= 0) continue;
                if (seen.Add(v)) list.Add(v);
            }
            return list;
        }
    }

    // One Enrollment Status Page as Intune has it.
    public sealed class EspConfig
    {
        public string Id, Name;
        public int Priority;
        public bool IsDefault, Show, AllowReset, AllowUse;
        public int TimeoutMinutes = 60;
        public readonly List<string> AppIds = new List<string>();
        public readonly List<EspTarget> Targets = new List<EspTarget>();
        public bool AssignmentsRead;

        public bool HasGroupTargets
        {
            get
            {
                foreach (EspTarget t in Targets) if (t.Kind == EspTarget.Group) return true;
                return false;
            }
        }
    }

    public sealed class EspTarget
    {
        public const string AllDevices = "allDevices", AllUsers = "allUsers", Group = "group", Exclude = "exclude";
        public string Kind, GroupId;
    }

    // A device in the Windows Autopilot list.
    public sealed class AutopilotEntry
    {
        public string Serial, Manufacturer, Model, GroupTag, User, Profile, State;
        public DateTime? LastContactUtc;
    }

    // Which ESP a device gets, and why.
    public sealed class EspPick
    {
        public EspConfig Config;
        public string Reason;
        // Some ESPs are assigned to groups, and the device's groups were not known.
        public bool Unresolved;
        public string Note;
    }

    // Checks devices before they are enrolled: are they registered in Windows Autopilot, which deployment profile
    // and Enrollment Status Page they get, and whether that ESP is likely to run into its time limit, going by the
    // enrollments the agents uploaded before. Read-only.
    public sealed class ReadinessCheck
    {
        public const string Scope = "https://graph.microsoft.com/DeviceManagementServiceConfig.Read.All";
        public const string IdentitiesUrl = "https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeviceIdentities";
        public const string ConfigsUrl = "https://graph.microsoft.com/beta/deviceManagement/deviceEnrollmentConfigurations";
        public const string AppUrl = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/";
        public const string DevicesUrl = "https://graph.microsoft.com/v1.0/devices";
        const string EspType = "#microsoft.graph.windows10EnrollmentCompletionPageConfiguration";
        const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        const int MaxPages = 50;

        readonly IGraph graph;
        // Sessions the agents uploaded, for the prediction. Null when no upload location is connected.
        public List<Session> History;
        public DateTime NowUtc = DateTime.UtcNow;

        List<EspConfig> esps;
        string espProblem;
        readonly Dictionary<string, string> appNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> missingApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string appProblem;
        FleetStats fleet;

        public ReadinessCheck(IGraph g)
        {
            graph = g;
        }

        public static string FindUrl(string serial)
        {
            string filter = "contains(serialNumber,'" + serial.Replace("'", "''") + "')";
            return IdentitiesUrl + "?$filter=" + Uri.EscapeDataString(filter);
        }

        public static string ProfileUrl(string identityId, bool intended)
        {
            return IdentitiesUrl + "/" + Uri.EscapeDataString(identityId) + (intended ? "/intendedDeploymentProfile" : "/deploymentProfile");
        }

        public static string AssignmentsUrl(string configId)
        {
            return ConfigsUrl + "/" + Uri.EscapeDataString(configId) + "/assignments";
        }

        public static string GroupsUrl(string entraDeviceId)
        {
            return DevicesUrl + "(deviceId='" + Uri.EscapeDataString(entraDeviceId.Replace("'", "''")) + "')/transitiveMemberOf?$select=id,displayName";
        }

        // Every device registered in Windows Autopilot, to pick from. Stops after max devices.
        public static List<AutopilotEntry> ListIdentities(IGraph g, int max)
        {
            List<AutopilotEntry> list = new List<AutopilotEntry>();
            string url = IdentitiesUrl;
            for (int page = 0; page < MaxPages && url != null && list.Count < max; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> x in Values(r))
                {
                    string serial = (Http.Str(x, "serialNumber") ?? "").Trim();
                    if (serial.Length == 0 || list.Count >= max) continue;
                    AutopilotEntry e = new AutopilotEntry();
                    e.Serial = serial;
                    e.Manufacturer = Http.Str(x, "manufacturer") ?? "";
                    e.Model = Http.Str(x, "model") ?? "";
                    e.GroupTag = Http.Str(x, "groupTag") ?? "";
                    e.User = Http.Str(x, "userPrincipalName") ?? "";
                    e.Profile = Words(Http.Str(x, "deploymentProfileAssignmentStatus"));
                    e.State = Words(Http.Str(x, "enrollmentState"));
                    e.LastContactUtc = IntuneCatalog.Time(x, "lastContactedDateTime");
                    list.Add(e);
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            list.Sort(delegate (AutopilotEntry a, AutopilotEntry b) { return string.Compare(a.Serial, b.Serial, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        // "assignedInSync" as "Assigned in sync".
        static string Words(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Txt.SplitCamel(s).ToLowerInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        public List<ReadyDevice> Run(IEnumerable<string> serials, Action<int, int> progress)
        {
            List<string> list = new List<string>(serials);
            List<ReadyDevice> result = new List<ReadyDevice>();
            for (int i = 0; i < list.Count; i++)
            {
                if (progress != null) progress(i, list.Count);
                result.Add(Check(list[i]));
            }
            if (progress != null) progress(list.Count, list.Count);
            return result;
        }

        public ReadyDevice Check(string serial)
        {
            ReadyDevice d = new ReadyDevice();
            d.Serial = serial;
            d.Title = serial;
            Dictionary<string, object> id;
            try { id = FindIdentity(d, serial); }
            catch (Exception ex)
            {
                d.Add(ReadyLines.Autopilot, "Registered in Windows Autopilot", ReadyLevel.Problem, IntuneLookup.Problem("Autopilot devices", Scope, ex));
                return d;
            }
            if (id == null) return d;
            Identity(d, id);

            Dictionary<string, object> profile = null;
            string assignment = (Http.Str(id, "deploymentProfileAssignmentStatus") ?? "").ToLowerInvariant();
            string profileProblem = null;
            if (assignment != "notassigned")
            {
                try { profile = ReadProfile(Http.Str(id, "id")); }
                catch (Exception ex) { profileProblem = IntuneLookup.Problem("The deployment profile", Scope, ex).Replace(" were not read", " was not read"); }
            }
            bool userDriven = true, hybrid = false;
            if (profile != null) Profile(d, profile, serial, out userDriven, out hybrid);
            else if (profileProblem != null) d.Add(ReadyLines.Profile, "Deployment profile", ReadyLevel.Warning, profileProblem);
            else if (assignment == "notassigned") d.Add(ReadyLines.Profile, "Deployment profile", ReadyLevel.Problem, "No deployment profile is assigned, so the device starts the normal Windows setup. Assign a profile to a group the device is in.");
            else d.Add(ReadyLines.Profile, "Deployment profile", ReadyLevel.Warning, "Intune returned no deployment profile for the device yet.");

            EspConfig esp = Esp(d, id, userDriven, hybrid);
            Predict(d, esp);
            return d;
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

        // ---------- Windows Autopilot ----------

        Dictionary<string, object> FindIdentity(ReadyDevice d, string serial)
        {
            List<Dictionary<string, object>> found = new List<Dictionary<string, object>>();
            string url = FindUrl(serial);
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = graph.Get(url);
                found.AddRange(Values(r));
                url = Http.Str(r, "@odata.nextLink");
            }
            List<Dictionary<string, object>> exact = new List<Dictionary<string, object>>();
            List<string> partial = new List<string>();
            foreach (Dictionary<string, object> x in found)
            {
                string s = Http.Str(x, "serialNumber");
                if (string.Equals((s ?? "").Trim(), serial, StringComparison.OrdinalIgnoreCase)) exact.Add(x);
                else if (s != null && partial.Count < 5) partial.Add(s);
            }
            if (exact.Count == 0)
            {
                string more = partial.Count > 0 ? " Serial numbers that contain it: " + string.Join(", ", partial.ToArray()) + "." : "";
                d.Add(ReadyLines.Autopilot, "Registered in Windows Autopilot", ReadyLevel.Problem, "Not registered in Windows Autopilot, so the device gets no Autopilot profile. Import its hardware hash first." + more);
                return null;
            }
            if (exact.Count > 1)
                d.Add(ReadyLines.Autopilot, "Registered in Windows Autopilot", ReadyLevel.Warning, "Registered " + exact.Count.ToString(CultureInfo.InvariantCulture) + " times. The checks below use the first one; remove the others in Intune.");
            else d.Add(ReadyLines.Autopilot, "Registered in Windows Autopilot", ReadyLevel.Ok, "Registered in Windows Autopilot.");
            return exact[0];
        }

        static void Identity(ReadyDevice d, Dictionary<string, object> id)
        {
            string make = Http.Str(id, "manufacturer"), model = Http.Str(id, "model");
            string hw = ((make ?? "") + " " + (model ?? "")).Trim();
            if (hw.Length > 0) d.Title = hw;
            d.Add(ReadyLines.Autopilot, "Manufacturer and model", ReadyLevel.Ok, hw.Length > 0 ? hw + "." : "Intune has no manufacturer or model for it.");

            string tag = Http.Str(id, "groupTag");
            d.Add(ReadyLines.Autopilot, "Group tag", ReadyLevel.Ok, string.IsNullOrEmpty(tag) ? "No group tag. Groups that are built on the group tag do not include the device." : tag + ".");

            string user = Http.Str(id, "userPrincipalName");
            d.Add(ReadyLines.Autopilot, "Assigned user", ReadyLevel.Ok, string.IsNullOrEmpty(user) ? "No user is assigned. Anyone in the tenant can sign in during setup." : user + " is assigned and is shown on the sign-in page.");

            DateTime? contact = IntuneCatalog.Time(id, "lastContactedDateTime");
            d.Add(ReadyLines.Autopilot, "Last contact", ReadyLevel.Ok, contact.HasValue ? "Last contacted Autopilot " + Txt.Local(contact.Value) + "." : "Has not contacted Autopilot yet.");

            string state = (Http.Str(id, "enrollmentState") ?? "").ToLowerInvariant();
            if (state == "enrolled") d.Add(ReadyLines.Autopilot, "Enrollment state", ReadyLevel.Ok, "Intune lists it as enrolled. After a reset it enrolls again with the same profile.");
            else if (state == "failed") d.Add(ReadyLines.Autopilot, "Enrollment state", ReadyLevel.Warning, "Its last enrollment failed, as Intune records it.");
            else if (state == "blocked") d.Add(ReadyLines.Autopilot, "Enrollment state", ReadyLevel.Problem, "Intune blocks the enrollment of this device. Check the enrollment restrictions and the device's state in Intune.");
            else if (state == "pendingreset") d.Add(ReadyLines.Autopilot, "Enrollment state", ReadyLevel.Ok, "A reset is pending.");
            else d.Add(ReadyLines.Autopilot, "Enrollment state", ReadyLevel.Ok, "Not enrolled yet.");

            string st = Http.Str(id, "deploymentProfileAssignmentStatus");
            string detail = Http.Str(id, "deploymentProfileAssignmentDetailedStatus");
            string extra = !string.IsNullOrEmpty(detail) && !string.Equals(detail, "none", StringComparison.OrdinalIgnoreCase) ? " Intune says: " + Txt.SplitCamel(detail).ToLowerInvariant() + "." : "";
            DateTime? assigned = IntuneCatalog.Time(id, "deploymentProfileAssignedDateTime");
            string when = assigned.HasValue ? ", " + Txt.Local(assigned.Value) : "";
            switch ((st ?? "").ToLowerInvariant())
            {
                case "assignedinsync": d.Add(ReadyLines.Autopilot, "Profile assignment", ReadyLevel.Ok, "A deployment profile is assigned" + when + "." + extra); break;
                case "assignedoutofsync":
                case "assignedunkownsyncstate": d.Add(ReadyLines.Autopilot, "Profile assignment", ReadyLevel.Warning, "A deployment profile is assigned, but Autopilot may not have the latest version yet. That can take a few minutes." + extra); break;
                case "pending": d.Add(ReadyLines.Autopilot, "Profile assignment", ReadyLevel.Warning, "The profile is still being assigned. Wait until Intune shows it as assigned before you start the device." + extra); break;
                case "failed": d.Add(ReadyLines.Autopilot, "Profile assignment", ReadyLevel.Problem, "Assigning the deployment profile failed." + extra); break;
                case "notassigned": d.Add(ReadyLines.Autopilot, "Profile assignment", ReadyLevel.Problem, "No deployment profile is assigned." + extra); break;
                default: d.Add(ReadyLines.Autopilot, "Profile assignment", ReadyLevel.Warning, "Intune does not know the state of the profile assignment." + extra); break;
            }

            string entra = EntraId(id);
            if (entra != null) d.Add(ReadyLines.Autopilot, "Entra device object", ReadyLevel.Ok, "The Entra device object is there (" + entra + "), so groups can include the device before it is enrolled.");
            else d.Add(ReadyLines.Autopilot, "Entra device object", ReadyLevel.Warning, "No Entra device object yet. Autopilot creates it after the import; until then groups cannot include the device and no profile is assigned.");
        }

        public static string EntraId(Dictionary<string, object> id)
        {
            string e = Http.Str(id, "azureAdDeviceId");
            if (string.IsNullOrEmpty(e) || e == ZeroGuid) e = Http.Str(id, "azureActiveDirectoryDeviceId");
            return string.IsNullOrEmpty(e) || e == ZeroGuid ? null : e;
        }

        // ---------- deployment profile ----------

        Dictionary<string, object> ReadProfile(string identityId)
        {
            Dictionary<string, object> p = One(graph.Get(ProfileUrl(identityId, false)));
            if (p != null && !string.IsNullOrEmpty(Http.Str(p, "displayName"))) return p;
            try
            {
                p = One(graph.Get(ProfileUrl(identityId, true)));
                if (p != null && !string.IsNullOrEmpty(Http.Str(p, "displayName"))) return p;
            }
            catch (GraphException) { }
            return null;
        }

        // A single object, also when it comes wrapped in "value".
        static Dictionary<string, object> One(Dictionary<string, object> r)
        {
            Dictionary<string, object> inner = ImeJson.Get(r, "value") as Dictionary<string, object>;
            return inner ?? r;
        }

        public static void Profile(ReadyDevice d, Dictionary<string, object> p, string serial, out bool userDriven, out bool hybrid)
        {
            string type = Http.Str(p, "@odata.type") ?? "";
            hybrid = type.IndexOf("activeDirectoryWindowsAutopilot", StringComparison.OrdinalIgnoreCase) >= 0;
            d.Add(ReadyLines.Profile, "Name", ReadyLevel.Ok, Http.Str(p, "displayName") + ".");
            string join = hybrid ? "Microsoft Entra hybrid joined" : "Microsoft Entra joined";
            if (hybrid && Bool(p, "hybridAzureADJoinSkipConnectivityCheck") == true) join += ", without the check for a domain controller";
            d.Add(ReadyLines.Profile, "Join type", ReadyLevel.Ok, join + ".");

            Dictionary<string, object> oobe = ImeJson.Get(p, "outOfBoxExperienceSetting") as Dictionary<string, object>;
            if (oobe == null) oobe = ImeJson.Get(p, "outOfBoxExperienceSettings") as Dictionary<string, object>;
            // In Graph, self-deploying mode is a profile for shared use.
            bool self = string.Equals(Http.Str(oobe, "deviceUsageType"), "shared", StringComparison.OrdinalIgnoreCase);
            userDriven = !self;
            bool? pre = Bool(p, "preprovisioningAllowed");
            if (pre == null) pre = Bool(p, "enableWhiteGlove");
            string mode = self ? "Self-deploying" : "User-driven";
            mode += pre == true ? ", and pre-provisioning is allowed" : ", pre-provisioning is not allowed";
            if (self && hybrid)
                d.Add(ReadyLines.Profile, "Deployment mode", ReadyLevel.Problem, mode + ". Self-deploying mode does not support hybrid join.");
            else d.Add(ReadyLines.Profile, "Deployment mode", ReadyLevel.Ok, mode + ".");

            string template = Http.Str(p, "deviceNameTemplate");
            if (hybrid) d.Add(ReadyLines.Profile, "Device name", ReadyLevel.Ok, "Hybrid join takes the name from the domain join profile, not from this profile.");
            else if (string.IsNullOrEmpty(template)) d.Add(ReadyLines.Profile, "Device name", ReadyLevel.Ok, "No name template, so Windows gives the device a random name.");
            else
            {
                int n = NameLength(template, serial);
                if (n > 15) d.Add(ReadyLines.Profile, "Device name", ReadyLevel.Warning, "Template " + template + ". With this serial number the name would be " + n.ToString(CultureInfo.InvariantCulture) + " characters, and device names have at most 15, so it is cut short.");
                else d.Add(ReadyLines.Profile, "Device name", ReadyLevel.Ok, "Template " + template + ", which gives " + Name(template, serial) + ".");
            }
        }

        static bool? Bool(Dictionary<string, object> d, string key)
        {
            object v = ImeJson.Get(d, key);
            if (v is bool) return (bool)v;
            return null;
        }

        // The name a template gives with this serial number. %RAND:n% shows as n question marks.
        public static string Name(string template, string serial)
        {
            StringBuilder b = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                if (template[i] == '%')
                {
                    int end = template.IndexOf('%', i + 1);
                    if (end > i)
                    {
                        string token = template.Substring(i + 1, end - i - 1);
                        if (string.Equals(token, "SERIAL", StringComparison.OrdinalIgnoreCase)) { b.Append(serial); i = end + 1; continue; }
                        if (token.StartsWith("RAND:", StringComparison.OrdinalIgnoreCase))
                        {
                            int n;
                            if (int.TryParse(token.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0 && n < 16) { b.Append('?', n); i = end + 1; continue; }
                        }
                    }
                }
                b.Append(template[i]);
                i++;
            }
            return b.ToString();
        }

        public static int NameLength(string template, string serial)
        {
            return Name(template, serial).Length;
        }

        // ---------- Enrollment Status Page ----------

        EspConfig Esp(ReadyDevice d, Dictionary<string, object> id, bool userDriven, bool hybrid)
        {
            if (esps == null && espProblem == null)
            {
                try { esps = ReadEsps(graph); }
                catch (Exception ex) { espProblem = IntuneLookup.Problem("The Enrollment Status Pages", Scope, ex); }
            }
            if (esps == null)
            {
                d.Add(ReadyLines.Esp, "Which ESP applies", ReadyLevel.Warning, espProblem);
                return null;
            }
            string entra = EntraId(id);
            Dictionary<string, string> groups = null;
            string groupProblem = null;
            if (entra == null) groups = new Dictionary<string, string>();
            else
            {
                try { groups = Groups(graph, entra); }
                catch (GraphException ex)
                {
                    if (ex.Status == 404) groups = new Dictionary<string, string>();
                    else groupProblem = ex.Status == 403 || ex.Status == 401
                        ? "the sign-in cannot read the device's groups, which needs Device.Read.All"
                        : "the device's groups could not be read (" + GraphClient.Clean(ex.Message) + ")";
                }
                catch (Exception ex) { groupProblem = "the device's groups could not be read (" + ex.Message + ")"; }
            }
            EspPick pick = Pick(esps, groups, userDriven);
            if (pick.Config == null)
            {
                d.Add(ReadyLines.Esp, "Which ESP applies", ReadyLevel.Warning, "No Enrollment Status Page applies, so setup does not wait for apps. " + (pick.Note ?? ""));
                return null;
            }
            EspConfig c = pick.Config;
            string text = c.Name + (c.IsDefault ? "" : " (priority " + c.Priority.ToString(CultureInfo.InvariantCulture) + ")") + ": " + pick.Reason;
            if (pick.Unresolved)
            {
                d.Add(ReadyLines.Esp, "Which ESP applies", ReadyLevel.Ok, text + " That is the one that applies by default.");
                d.Add(ReadyLines.Esp, "Targeted ESPs", ReadyLevel.Warning, "Targeted ESPs could not be resolved: " + (groupProblem ?? "the device's groups are not known") + ". " + pick.Note);
            }
            else
            {
                d.Add(ReadyLines.Esp, "Which ESP applies", ReadyLevel.Ok, text);
                if (!string.IsNullOrEmpty(pick.Note)) d.Add(ReadyLines.Esp, "Other ESPs", ReadyLevel.Ok, pick.Note);
            }
            if (!c.Show)
            {
                d.Add(ReadyLines.Esp, "Shown", ReadyLevel.Ok, "This ESP is not shown, so setup does not wait for apps.");
                return null;
            }
            d.Add(ReadyLines.Esp, "Time limit", ReadyLevel.Ok, c.TimeoutMinutes.ToString(CultureInfo.InvariantCulture) + " minutes." + (hybrid ? " With hybrid join the ESP waits up to 40 minutes longer for the device to appear in Entra." : ""));
            BlockingApps(d, c);
            d.Add(ReadyLines.Esp, "Reset on failure", ReadyLevel.Ok, c.AllowReset ? "Allowed: the user can reset the device when an installation fails." : "Not allowed: the user cannot reset the device from the ESP.");
            d.Add(ReadyLines.Esp, "Use the device anyway", ReadyLevel.Ok, c.AllowUse ? "Allowed: the user can continue to the desktop when an installation fails." : "Not allowed: the device stays on the ESP when an installation fails.");
            return c;
        }

        void BlockingApps(ReadyDevice d, EspConfig c)
        {
            if (c.AppIds.Count == 0)
            {
                d.Add(ReadyLines.Esp, "Blocking apps", ReadyLevel.Ok, "Every required app blocks: the ESP waits for all apps assigned to the device and the user.");
                return;
            }
            List<string> names = new List<string>();
            int unnamed = 0;
            List<string> gone = new List<string>();
            foreach (string app in c.AppIds)
            {
                string name = AppName(app);
                if (name != null) names.Add(name);
                else if (missingApps.Contains(app)) gone.Add(app);
                else unnamed++;
            }
            string text = c.AppIds.Count.ToString(CultureInfo.InvariantCulture) + (c.AppIds.Count == 1 ? " app" : " apps");
            if (names.Count > 0) text += ": " + string.Join(", ", names.ToArray());
            if (unnamed > 0) text += (names.Count > 0 ? ", and " : ", ") + unnamed.ToString(CultureInfo.InvariantCulture) + " without a name";
            text += ".";
            d.Add(ReadyLines.Esp, "Blocking apps", ReadyLevel.Ok, text);
            if (unnamed > 0 && appProblem != null) d.Add(ReadyLines.Esp, "App names", ReadyLevel.Warning, appProblem);
            if (gone.Count > 0)
                d.Add(ReadyLines.Esp, "Deleted apps", ReadyLevel.Warning, gone.Count.ToString(CultureInfo.InvariantCulture) + (gone.Count == 1 ? " blocking app is" : " blocking apps are") + " no longer in Intune (" + string.Join(", ", gone.ToArray()) + "). The ESP list still holds " + (gone.Count == 1 ? "it" : "them") + ".");
        }

        string AppName(string id)
        {
            string name;
            if (appNames.TryGetValue(id, out name)) return name;
            if (missingApps.Contains(id) || appProblem != null) return null;
            try
            {
                Dictionary<string, object> r = graph.Get(AppUrl + Uri.EscapeDataString(id) + "?$select=id,displayName");
                name = Http.Str(r, "displayName");
                if (!string.IsNullOrEmpty(name)) appNames[id] = name;
                return name;
            }
            catch (GraphException ex)
            {
                if (ex.Status == 404) missingApps.Add(id);
                else appProblem = IntuneLookup.Problem("App names", LookupChoice.AppScope, ex);
                return null;
            }
        }

        public static List<EspConfig> ReadEsps(IGraph g)
        {
            List<EspConfig> list = new List<EspConfig>();
            string url = ConfigsUrl;
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> x in Values(r))
                {
                    EspConfig c = ToEsp(x);
                    if (c != null) list.Add(c);
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            foreach (EspConfig c in list)
            {
                if (c.IsDefault) continue;
                string next = AssignmentsUrl(c.Id);
                for (int page = 0; page < MaxPages && next != null; page++)
                {
                    Dictionary<string, object> r = g.Get(next);
                    foreach (Dictionary<string, object> a in Values(r))
                    {
                        EspTarget t = ToTarget(ImeJson.Get(a, "target") as Dictionary<string, object>);
                        if (t != null) c.Targets.Add(t);
                    }
                    next = Http.Str(r, "@odata.nextLink");
                }
                c.AssignmentsRead = true;
            }
            list.Sort(delegate (EspConfig a, EspConfig b) { return a.Priority.CompareTo(b.Priority); });
            return list;
        }

        public static EspConfig ToEsp(Dictionary<string, object> x)
        {
            string type = Http.Str(x, "@odata.type");
            if (!string.Equals(type, EspType, StringComparison.OrdinalIgnoreCase)) return null;
            EspConfig c = new EspConfig();
            c.Id = Http.Str(x, "id");
            if (string.IsNullOrEmpty(c.Id)) return null;
            c.Name = Http.Str(x, "displayName") ?? "Enrollment Status Page";
            long? prio = ImeJson.Long(ImeJson.Get(x, "priority"));
            c.Priority = prio.HasValue ? (int)prio.Value : 0;
            string kind = Http.Str(x, "deviceEnrollmentConfigurationType") ?? "";
            c.IsDefault = kind.StartsWith("default", StringComparison.OrdinalIgnoreCase) || c.Id.EndsWith("_DefaultWindows10EnrollmentCompletionPageConfiguration", StringComparison.OrdinalIgnoreCase);
            c.Show = Bool(x, "showInstallationProgress") == true;
            c.AllowReset = Bool(x, "allowDeviceResetOnInstallFailure") == true;
            c.AllowUse = Bool(x, "allowDeviceUseOnInstallFailure") == true;
            long? t = ImeJson.Long(ImeJson.Get(x, "installProgressTimeoutInMinutes"));
            if (t.HasValue && t.Value > 0) c.TimeoutMinutes = (int)t.Value;
            List<object> apps = ImeJson.Get(x, "selectedMobileAppIds") as List<object>;
            if (apps != null)
                foreach (object o in apps)
                {
                    string s = o as string;
                    if (!string.IsNullOrEmpty(s)) c.AppIds.Add(s.Trim('{', '}'));
                }
            return c;
        }

        // Assignment targets as Intune writes them: groupAssignmentTarget with groupId, exclusionGroupAssignmentTarget,
        // allDevicesAssignmentTarget and allLicensedUsersAssignmentTarget.
        public static EspTarget ToTarget(Dictionary<string, object> t)
        {
            if (t == null) return null;
            string type = (Http.Str(t, "@odata.type") ?? "").ToLowerInvariant();
            EspTarget e = new EspTarget();
            e.GroupId = Http.Str(t, "groupId") ?? Http.Str(t, "entraObjectId");
            if (type.Contains("exclusiongroup")) e.Kind = EspTarget.Exclude;
            else if (type.Contains("alldevices")) e.Kind = EspTarget.AllDevices;
            else if (type.Contains("alllicensedusers")) e.Kind = EspTarget.AllUsers;
            else if (!string.IsNullOrEmpty(e.GroupId)) e.Kind = EspTarget.Group;
            else return null;
            return e;
        }

        // The groups the Entra device is in, directly or through other groups: ID to name.
        public static Dictionary<string, string> Groups(IGraph g, string entraDeviceId)
        {
            Dictionary<string, string> groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string url = GroupsUrl(entraDeviceId);
            for (int page = 0; page < MaxPages && url != null; page++)
            {
                Dictionary<string, object> r = g.Get(url);
                foreach (Dictionary<string, object> x in Values(r))
                {
                    string gid = Http.Str(x, "id");
                    if (!string.IsNullOrEmpty(gid)) groups[gid] = Http.Str(x, "displayName") ?? gid;
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            return groups;
        }

        // The ESP the device gets, the way Intune picks it: the highest priority ESP assigned to the device, then
        // (only with a user) the highest priority ESP assigned to the user, then the default ESP. groups is null when
        // the device's groups are not known.
        public static EspPick Pick(List<EspConfig> esps, Dictionary<string, string> groups, bool userDriven)
        {
            EspPick p = new EspPick();
            List<EspConfig> sorted = new List<EspConfig>(esps);
            sorted.Sort(delegate (EspConfig a, EspConfig b) { return a.Priority.CompareTo(b.Priority); });
            EspConfig def = null;
            List<string> grouped = new List<string>();
            foreach (EspConfig c in sorted)
            {
                if (c.IsDefault) { if (def == null) def = c; continue; }
                if (Excluded(c, groups)) continue;
                string why = DeviceTarget(c, groups);
                if (why != null && p.Config == null) { p.Config = c; p.Reason = why; }
                else if (why == null && c.HasGroupTargets) grouped.Add(c.Name);
            }
            if (p.Config != null)
            {
                if (groups == null && Higher(sorted, p.Config))
                {
                    p.Unresolved = true;
                    p.Note = "An ESP with a higher priority applies instead when the device is in its group: " + string.Join(", ", HigherNames(sorted, p.Config).ToArray()) + ".";
                }
                return p;
            }
            if (userDriven)
            {
                foreach (EspConfig c in sorted)
                {
                    if (c.IsDefault || Excluded(c, groups)) continue;
                    foreach (EspTarget t in c.Targets)
                        if (t.Kind == EspTarget.AllUsers) { p.Config = c; p.Reason = "assigned to all users, and no ESP is assigned to the device."; break; }
                    if (p.Config != null) break;
                }
            }
            if (p.Config == null && def != null)
            {
                p.Config = def;
                p.Reason = !userDriven ? "self-deploying mode only uses ESPs assigned to devices, and none is." : "no other ESP is assigned to the device or to all users.";
            }
            if (grouped.Count > 0)
            {
                if (groups == null)
                {
                    p.Unresolved = true;
                    p.Note = "ESPs assigned to groups: " + string.Join(", ", grouped.ToArray()) + ". One of them applies instead when the device" + (userDriven ? " or the user" : "") + " is in its group.";
                }
                else if (userDriven)
                    p.Note = "Assigned to groups the device is not in: " + string.Join(", ", grouped.ToArray()) + ". One of them applies instead when the user who signs in is in its group.";
            }
            return p;
        }

        static bool Excluded(EspConfig c, Dictionary<string, string> groups)
        {
            if (groups == null) return false;
            foreach (EspTarget t in c.Targets) if (t.Kind == EspTarget.Exclude && t.GroupId != null && groups.ContainsKey(t.GroupId)) return true;
            return false;
        }

        static string DeviceTarget(EspConfig c, Dictionary<string, string> groups)
        {
            foreach (EspTarget t in c.Targets) if (t.Kind == EspTarget.AllDevices) return "assigned to all devices.";
            if (groups == null) return null;
            foreach (EspTarget t in c.Targets)
            {
                string name;
                if (t.Kind == EspTarget.Group && t.GroupId != null && groups.TryGetValue(t.GroupId, out name)) return "assigned to the group " + name + ", which the device is in.";
            }
            return null;
        }

        static bool Higher(List<EspConfig> sorted, EspConfig picked)
        {
            return HigherNames(sorted, picked).Count > 0;
        }

        static List<string> HigherNames(List<EspConfig> sorted, EspConfig picked)
        {
            List<string> l = new List<string>();
            foreach (EspConfig c in sorted)
            {
                if (c == picked) break;
                if (!c.IsDefault && c.HasGroupTargets) l.Add(c.Name);
            }
            return l;
        }

        // ---------- prediction from earlier enrollments ----------

        void Predict(ReadyDevice d, EspConfig esp)
        {
            if (esp == null) return;
            if (History == null)
            {
                d.Add(ReadyLines.Prediction, "Earlier enrollments", ReadyLevel.Ok, "No upload location of the agents is connected, so there is no prediction from earlier enrollments.");
                return;
            }
            if (fleet == null) fleet = FleetStats.Build(History, NowUtc);
            List<string> names = new List<string>();
            foreach (string app in esp.AppIds)
            {
                string n;
                if (appNames.TryGetValue(app, out n)) names.Add(n);
            }
            if (esp.AppIds.Count > 0 && names.Count == 0)
            {
                d.Add(ReadyLines.Prediction, "Earlier enrollments", ReadyLevel.Ok, "The blocking apps have no names, so they cannot be matched with earlier enrollments.");
                return;
            }
            Prediction pr = Prediction.Make(History, fleet, esp.AppIds.Count == 0 ? null : names, NowUtc);
            pr.AddTo(d, esp.TimeoutMinutes);
        }
    }

    // What earlier enrollments say about the apps an ESP waits for.
    public sealed class Prediction
    {
        public sealed class App
        {
            public string Name;
            public double MedianSeconds;
            public int Timed, Done, Failed;

            public double FailRate
            {
                get { return Done + Failed == 0 ? 0 : Failed * 100.0 / (Done + Failed); }
            }
        }

        public readonly List<App> Apps = new List<App>();
        // Enrollments that had at least one of the apps, and the median of the time the apps took together in each.
        public int Runs;
        public double MedianSumSeconds;
        public bool AllApps;

        static bool IsApp(Item i)
        {
            return i.Kind == Kinds.Win32App || i.Kind == Kinds.MsiApp || i.Kind == Kinds.StoreApp || i.Kind == Kinds.Office || i.Kind == Kinds.Application;
        }

        static bool InEsp(Item i)
        {
            return i.PhaseKey == Phases.DeviceSetup || i.PhaseKey == Phases.AccountSetup;
        }

        // names is null when every required app blocks: then the apps the ESP waited for in each enrollment count.
        public static Prediction Make(List<Session> history, FleetStats fleet, List<string> names, DateTime nowUtc)
        {
            Prediction p = new Prediction();
            p.AllApps = names == null;
            HashSet<string> wanted = names == null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<double>> times = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            List<double> sums = new List<double>();
            foreach (Session s in history)
            {
                if (s == null) continue;
                double sum = 0;
                bool any = false;
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Item i in s.Items)
                {
                    if (!IsApp(i)) continue;
                    if (wanted != null ? !wanted.Contains(i.DisplayName) : !InEsp(i)) continue;
                    if (!seen.Add(i.DisplayName)) continue;
                    State st = i.State;
                    if (st != State.Succeeded && st != State.Failed) continue;
                    TimeSpan? dur = i.Duration(nowUtc);
                    if (!dur.HasValue) continue;
                    any = true;
                    sum += dur.Value.TotalSeconds;
                    List<double> l;
                    if (!times.TryGetValue(i.DisplayName, out l)) { l = new List<double>(); times[i.DisplayName] = l; }
                    l.Add(dur.Value.TotalSeconds);
                }
                if (any) sums.Add(sum);
            }
            p.Runs = sums.Count;
            p.MedianSumSeconds = Median(sums);
            foreach (KeyValuePair<string, List<double>> kv in times)
            {
                App a = new App();
                a.Name = kv.Key;
                a.MedianSeconds = Median(kv.Value);
                a.Timed = kv.Value.Count;
                // The fail rate comes from the statistics, over every enrollment where the app finished.
                foreach (FleetItem f in fleet.Items)
                    if (string.Equals(f.Name, kv.Key, StringComparison.OrdinalIgnoreCase)) { a.Done += f.Done; a.Failed += f.Failed; }
                p.Apps.Add(a);
            }
            p.Apps.Sort(delegate (App x, App y) { return y.MedianSeconds.CompareTo(x.MedianSeconds); });
            return p;
        }

        public static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            List<double> v = new List<double>(values);
            v.Sort();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        public void AddTo(ReadyDevice d, int limitMinutes)
        {
            if (Runs == 0)
            {
                d.Add(ReadyLines.Prediction, "Earlier enrollments", ReadyLevel.Ok, AllApps ? "The uploads have no enrollment where the ESP waited for apps." : "None of the blocking apps is in the uploaded enrollments.");
                return;
            }
            List<string> parts = new List<string>();
            foreach (App a in Apps) if (parts.Count < 6) parts.Add(a.Name + " " + Txt.DurationShort(TimeSpan.FromSeconds(a.MedianSeconds)));
            string typical = "Typical time in " + Runs.ToString(CultureInfo.InvariantCulture) + (Runs == 1 ? " earlier enrollment: " : " earlier enrollments: ") + string.Join(", ", parts.ToArray()) + (Apps.Count > parts.Count ? ", and more" : "") + ".";
            d.Add(ReadyLines.Prediction, "Blocking apps", ReadyLevel.Ok, typical);
            double limit = limitMinutes * 60.0;
            string sum = Txt.DurationShort(TimeSpan.FromSeconds(MedianSumSeconds));
            double share = limit > 0 ? MedianSumSeconds * 100 / limit : 0;
            if (limit > 0 && MedianSumSeconds > limit * 0.75)
                d.Add(ReadyLines.Prediction, "Time limit", MedianSumSeconds > limit ? ReadyLevel.Problem : ReadyLevel.Warning,
                    "Together they usually take " + sum + ", " + share.ToString("0", CultureInfo.InvariantCulture) + " % of the time limit of " + limitMinutes.ToString(CultureInfo.InvariantCulture) + " minutes. Raise the limit or block on fewer apps.");
            else d.Add(ReadyLines.Prediction, "Time limit", ReadyLevel.Ok, "Together they usually take " + sum + ", well within the time limit of " + limitMinutes.ToString(CultureInfo.InvariantCulture) + " minutes.");
            foreach (App a in Apps)
            {
                if (a.Done + a.Failed == 0 || a.FailRate <= 10) continue;
                d.Add(ReadyLines.Prediction, a.Name, ReadyLevel.Warning, "Failed on " + a.Failed.ToString(CultureInfo.InvariantCulture) + " of " + (a.Done + a.Failed).ToString(CultureInfo.InvariantCulture) + " recent enrollments (" + a.FailRate.ToString("0", CultureInfo.InvariantCulture) + " %). When a blocking app fails, the ESP stops with an error.");
            }
        }
    }
}
