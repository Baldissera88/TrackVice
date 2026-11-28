namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    // Who an Intune app is for and why it installs, and the group it has in the Apps phase.
    // Apps the ESP waits for belong to Device setup and Account setup, which count them; the Progress list shows them
    // with every other app under Apps, in the first two groups.
    public static class AppGroups
    {
        public const string TargetKey = "Target";
        public const string AssignmentKey = "Assignment";
        public const string Device = "Device", User = "User";
        public const string Required = "Required", Available = "Available", Dependency = "Dependency", Uninstall = "Uninstall";

        public const string DeviceWaits = "Device setup waits for";
        public const string AccountWaits = "Account setup waits for";
        public const string DeviceAfter = "Device, after the ESP";
        public const string UserAfter = "User, after the ESP";
        public const string InCompanyPortal = "Available in Company Portal";
        public const string Dependencies = "Dependencies";
        public const string Uninstalls = "Uninstall";
        public const string ExpectedApps = "Expected, not on the device yet";
        public static readonly string[] Order = { DeviceWaits, AccountWaits, DeviceAfter, UserAfter, ExpectedApps, InCompanyPortal, Dependencies, Uninstalls };

        const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

        public static bool IsApp(Item i)
        {
            if (i == null) return false;
            return i.Kind == Kinds.Win32App || i.Kind == Kinds.MsiApp || i.Kind == Kinds.StoreApp || i.Kind == Kinds.Office;
        }

        // The intent as the IME registry names it ("Not targeted (dependency)", "Available"), or its number.
        public static string AssignmentOf(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim().ToLowerInvariant();
            if (s == "0" || s.Contains("dependency") || s.StartsWith("not targeted")) return Dependency;
            if (s == "1" || s.StartsWith("available")) return Available;
            if (s == "3" || s.StartsWith("required")) return Required;
            if (s == "4" || s.StartsWith("uninstall")) return Uninstall;
            return null;
        }

        // The user an app is installed for: the all-zero ID, or System, is the device.
        public static string TargetOfUser(string user)
        {
            if (string.IsNullOrEmpty(user)) return null;
            string u = user.Trim('{', '}', ' ');
            return u == ZeroGuid || u.Equals("System", StringComparison.OrdinalIgnoreCase) ? Device : User;
        }

        // TargetType in the app policies the agent gets: 1 user, 2 device.
        public static string TargetOfType(string n)
        {
            return n == "1" ? User : n == "2" ? Device : null;
        }

        public static string Target(Item i)
        {
            if (i == null) return null;
            string t = i.Get(TargetKey);
            if (t != null && t.Equals(User, StringComparison.OrdinalIgnoreCase)) return User;
            if (t != null && t.Equals(Device, StringComparison.OrdinalIgnoreCase)) return Device;
            if (i.PhaseKey == Phases.AccountSetup) return User;
            if (i.PhaseKey == Phases.DeviceSetup) return Device;
            return null;
        }

        public static string Assignment(Item i)
        {
            return i == null ? null : AssignmentOf(i.Get(AssignmentKey));
        }

        public static bool IsAvailable(Item i)
        {
            return IsApp(i) && Assignment(i) == Available;
        }

        // Offered in Company Portal and not being installed: shown, but nothing waits for it.
        public static bool Idle(Item i)
        {
            if (!IsAvailable(i)) return false;
            State s = i.State;
            return s == State.Skipped || s == State.Pending || s == State.Unknown;
        }

        public static string Group(Item i)
        {
            if (i == null || !IsApp(i)) return null;
            if (i.Expected) return ExpectedApps;
            if (i.PhaseKey == Phases.DeviceSetup) return DeviceWaits;
            if (i.PhaseKey == Phases.AccountSetup) return AccountWaits;
            if (i.PhaseKey != Phases.IntuneApps) return null;
            string a = Assignment(i);
            if (a == Uninstall) return Uninstalls;
            if (a == Dependency) return Dependencies;
            if (a == Available) return InCompanyPortal;
            return Target(i) == User ? UserAfter : DeviceAfter;
        }

        // "Device", "Required": the words after the kind in the list.
        public static List<string> Tags(Item i)
        {
            List<string> l = new List<string>();
            if (!IsApp(i)) return l;
            string t = Target(i), a = Assignment(i);
            if (t != null) l.Add(t);
            if (a != null) l.Add(a);
            return l;
        }

        public static string KindText(Item i)
        {
            List<string> l = Tags(i);
            if (l.Count == 0) return i.Kind;
            return (string.IsNullOrEmpty(i.Kind) ? "" : i.Kind + " · ") + string.Join(" · ", l.ToArray());
        }

        public static string StateLabel(Item i)
        {
            if (i != null && i.Expected && i.State == State.Pending) return "Expected";
            return Idle(i) ? "Available" : TsPlan.DisabledLabel(i) ?? States.Label(i.State);
        }

        public static bool IsAppsStep(Item i)
        {
            return i != null && i.Key != null && i.Key.StartsWith("esp:step:", StringComparison.OrdinalIgnoreCase) && i.Key.EndsWith(":apps", StringComparison.OrdinalIgnoreCase);
        }

        // What the ESP step Apps waits for: "3 of 5 · waiting for Contoso VPN Client". Null when its phase has no apps.
        public static string StepText(Phase p, Item step)
        {
            if (p == null || step == null) return null;
            List<Item> apps = new List<Item>();
            foreach (Item i in p.Items) if (i != step && IsApp(i)) apps.Add(i);
            if (apps.Count == 0) return null;
            int done = 0, failed = 0;
            List<Item> waiting = new List<Item>();
            foreach (Item i in apps)
            {
                if (States.IsComplete(i.State)) done++;
                else if (i.State == State.Failed) failed++;
                else if (States.IsActive(i.State)) waiting.Insert(CountActive(waiting), i);
                else waiting.Add(i);
            }
            string s = done + " of " + apps.Count;
            if (failed > 0) s += " · " + failed + " failed";
            if (waiting.Count > 0)
            {
                s += " · waiting for " + waiting[0].DisplayName;
                if (waiting.Count > 1) s += " and " + (waiting.Count - 1) + " more";
            }
            else if (failed == 0) s += " done";
            return s;
        }

        static int CountActive(List<Item> l)
        {
            int n = 0;
            foreach (Item i in l) if (States.IsActive(i.State)) n++;
            return n;
        }

        // App policies as the agent logs them: "Get policies = [{"Id":"...","Name":"...","Version":1,"Intent":3,"TargetType":2,...}]".
        static readonly Regex RxPolicy = new Regex("\"Id\"\\s*:\\s*\"(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\"(?<mid>(?:(?!\"Id\"\\s*:).){0,800}?)\"Intent\"\\s*:\\s*(?<i>\\d+)\\s*,\\s*\"TargetType\"\\s*:\\s*(?<t>\\d+)", RegexOptions.Compiled);
        static readonly Regex RxName = new Regex("^\\s*,\\s*\"Name\"\\s*:\\s*\"(?<n>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        public sealed class Policy
        {
            public string Id, Name, Assignment, Target;
        }

        public static List<Policy> Policies(string text)
        {
            List<Policy> l = new List<Policy>();
            if (string.IsNullOrEmpty(text) || text.IndexOf("Get policies", StringComparison.OrdinalIgnoreCase) < 0) return l;
            foreach (Match m in RxPolicy.Matches(text))
            {
                Policy p = new Policy();
                p.Id = m.Groups["id"].Value.ToLowerInvariant();
                p.Assignment = AssignmentOf(m.Groups["i"].Value);
                p.Target = TargetOfType(m.Groups["t"].Value);
                Match n = RxName.Match(m.Groups["mid"].Value);
                if (n.Success)
                {
                    // An escape JSON does not have would throw and stop the whole log from being read: the name is kept as written.
                    try { p.Name = Regex.Unescape(n.Groups["n"].Value); }
                    catch (ArgumentException) { p.Name = n.Groups["n"].Value; }
                }
                l.Add(p);
            }
            return l;
        }

        // Lines that only mark where the agent's work starts or stops: "----- Lock DoWorkInternal -----".
        static readonly Regex RxSeparator = new Regex(@"^(?:\[[^\]]{1,40}\]\s*)*(?:(?>[-=*]{3,}).{0,80}?[-=*]{3,}|[-=*]{3,}|(?:Un)?[Ll]ock DoWorkInternal)\s*$", RegexOptions.Compiled);

        public static bool IsSeparator(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            return t.Length <= 512 && RxSeparator.IsMatch(t);
        }
    }
}
