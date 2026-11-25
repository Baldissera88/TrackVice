namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // Where the list of expected apps comes from.
    public static class ExpectedSources
    {
        public const string Intune = "Intune";
        public const string History = "History";
    }

    // One app the deployment is expected to bring.
    public sealed class ExpectedApp
    {
        // The Intune mobile app ID, the same ID the Intune Management Extension logs. Null when the source only knows names.
        public string Id;
        public string Name;
        // Kinds.Win32App when the source does not say.
        public string Kind;
        // AppGroups.Device or AppGroups.User when known, else null.
        public string Target;
        // AppGroups.Required or AppGroups.Uninstall.
        public string Assignment = AppGroups.Required;
        // From history: in how many of how many earlier deployments the app was seen.
        public int Seen, Of;
    }

    // The apps a deployment is expected to bring, from Intune or from earlier deployments.
    public sealed class ExpectedSet
    {
        public string Source;
        // The earlier deployments it was made from (history).
        public int Runs;
        public DateTime ReadUtc;
        public readonly List<ExpectedApp> Apps = new List<ExpectedApp>();

        // IDs and names, so a device that only knows an app by its ID shows the name.
        public Dictionary<string, string> Names()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExpectedApp a in Apps) if (!string.IsNullOrEmpty(a.Id) && !string.IsNullOrEmpty(a.Name)) d[a.Id] = a.Name;
            return d;
        }

        // "Intune" or "from 5 earlier deployments".
        public string SourceText
        {
            get
            {
                if (Source == ExpectedSources.Intune) return "Intune";
                return "from " + Runs.ToString(CultureInfo.InvariantCulture) + (Runs == 1 ? " earlier deployment" : " earlier deployments");
            }
        }

        // "Expected: 9 apps (Intune)".
        public string Header
        {
            get { return "Expected: " + Apps.Count.ToString(CultureInfo.InvariantCulture) + (Apps.Count == 1 ? " app" : " apps") + " (" + SourceText + ")"; }
        }
    }

    // What Detach took out of a session, to put back.
    public sealed class ExpectedHold
    {
        public readonly List<Item> Items = new List<Item>();
        public readonly List<Finding> Findings = new List<Finding>();
    }

    // Adds the apps a deployment is expected to bring, but that the device has not shown yet, to the session as waiting items,
    // so that "2 of 2" does not grow to "8 of 9" while the user apps arrive one by one. It is a separate step on top of what
    // the readers found: it runs after Finder.Run, takes its own items out again before anything is read, saved or learned,
    // and never touches the Enrollment Status Page logic. The items exist only in the viewer.
    public static class ExpectedApps
    {
        public const string KeyPrefix = "expected:";
        public const string WhyKey = "Expected";
        // Placeholders sort after every item a reader made.
        public const int OrderBase = 1000000;
        // With nothing new for this long a deployment that watches is taken as done.
        public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);
        // A device that waits for a user to sign in is given this long before the apps still missing are given up.
        public static readonly TimeSpan UserWait = TimeSpan.FromMinutes(60);

        // A history source needs this share of the earlier deployments, and at least MinRuns of them.
        public const double Threshold = 0.6;
        public const int MinRuns = 2, MaxRuns = 10;

        static readonly string[] AppKinds = { Kinds.Win32App, Kinds.MsiApp, Kinds.StoreApp, Kinds.Office };

        public static string Key(ExpectedApp a)
        {
            return KeyPrefix + (string.IsNullOrEmpty(a.Id) ? "name:" + (a.Name ?? "").Trim().ToLowerInvariant() : a.Id.Trim('{', '}').ToLowerInvariant());
        }

        // ---------- what earlier deployments say ----------

        // The apps seen in at least Threshold of the newest earlier deployments, but only with MinRuns of them.
        public static ExpectedSet FromHistory(List<HistoryRun> runs)
        {
            if (runs == null) return null;
            List<HistoryRun> l = new List<HistoryRun>(runs);
            l.Sort(delegate (HistoryRun a, HistoryRun b) { return b.EndUtc.CompareTo(a.EndUtc); });
            if (l.Count > MaxRuns) l.RemoveRange(MaxRuns, l.Count - MaxRuns);
            if (l.Count < MinRuns) return null;
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ExpectedApp> first = new Dictionary<string, ExpectedApp>(StringComparer.OrdinalIgnoreCase);
            foreach (HistoryRun r in l)
            {
                HashSet<string> own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (r.Items == null) continue;
                foreach (HistoryValue v in r.Items)
                {
                    if (v == null || string.IsNullOrEmpty(v.Key)) continue;
                    int bar = v.Key.IndexOf('|');
                    if (bar <= 0) continue;
                    string kind = v.Key.Substring(0, bar), name = v.Key.Substring(bar + 1).Trim();
                    if (name.Length == 0 || Array.IndexOf(AppKinds, kind) < 0) continue;
                    string k = name;
                    if (!own.Add(k)) continue;
                    int n;
                    seen.TryGetValue(k, out n);
                    seen[k] = n + 1;
                    if (!first.ContainsKey(k)) first[k] = new ExpectedApp { Name = name, Kind = kind };
                }
            }
            ExpectedSet set = new ExpectedSet();
            set.Source = ExpectedSources.History;
            set.Runs = l.Count;
            foreach (KeyValuePair<string, int> kv in seen)
            {
                if (kv.Value < Math.Ceiling(Threshold * l.Count - 1e-9)) continue;
                ExpectedApp a = first[kv.Key];
                a.Seen = kv.Value;
                a.Of = l.Count;
                set.Apps.Add(a);
            }
            set.Apps.Sort(delegate (ExpectedApp a, ExpectedApp b)
            {
                int c = b.Seen.CompareTo(a.Seen);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return set.Apps.Count == 0 ? null : set;
        }

        // ---------- matching ----------

        // The item a reader made for this expected app, or null. By ID first (the IME logs the Intune app ID), else by name.
        public static Item Seen(Session s, ExpectedApp a)
        {
            string id = string.IsNullOrEmpty(a.Id) ? null : a.Id.Trim('{', '}').ToLowerInvariant();
            if (id != null)
            {
                Item k = s.Find(IntuneKeys.App(id));
                if (k != null && !k.Expected) return k;
                foreach (Item i in s.Items)
                    if (!i.Expected && string.Equals((i.Get("ID") ?? "").Trim('{', '}'), id, StringComparison.OrdinalIgnoreCase)) return i;
            }
            string name = (a.Name ?? "").Trim();
            if (name.Length == 0) return null;
            foreach (Item i in s.Items)
                if (!i.Expected && AppGroups.IsApp(i) && string.Equals((i.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            return null;
        }

        // ---------- the merge ----------

        public static bool Has(Session s)
        {
            if (s == null) return false;
            foreach (Item i in s.Items) if (i.Expected) return true;
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.ExpectedMissing) return true;
            return false;
        }

        // Takes every placeholder and its findings out of the session.
        public static ExpectedHold Detach(Session s)
        {
            ExpectedHold h = new ExpectedHold();
            if (s == null) return h;
            foreach (Item i in s.Items) if (i.Expected) h.Items.Add(i);
            foreach (Item i in h.Items)
            {
                s.Items.Remove(i);
                s.Unindex(i.Key);
                Phase p = s.FindPhase(i.PhaseKey);
                if (p != null) p.Items.Remove(i);
            }
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.ExpectedMissing) h.Findings.Add(f);
            s.Findings.RemoveAll(delegate (Finding f) { return f.Kind == FindingKinds.ExpectedMissing; });
            return h;
        }

        // Puts what Detach took out back.
        public static void Attach(Session s, ExpectedHold h)
        {
            if (s == null || h == null) return;
            foreach (Item i in h.Items)
            {
                s.Items.Add(i);
                s.Index(i);
                Phase p = s.FindPhase(i.PhaseKey);
                if (p != null) { p.Items.Add(i); p.Items.Sort(Session.ItemOrder); }
            }
            foreach (Finding f in h.Findings) s.Findings.Add(f);
        }

        public static void Strip(Session s)
        {
            Detach(s);
            if (s != null) s.ExpectedText = null;
        }

        // A short text that changes whenever what the viewer shows of the expected apps changes.
        public static string Signature(Session s)
        {
            if (s == null) return "";
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (Item i in s.Items) if (i.Expected) b.Append(i.Key).Append(';');
            foreach (Finding f in s.Findings) if (f.Kind == FindingKinds.ExpectedMissing) b.Append(f.Title).Append(';');
            b.Append(s.ExpectedText);
            return b.ToString();
        }

        // The newest time any item the readers made was heard from.
        static DateTime LastHeard(Session s)
        {
            DateTime m = DateTime.MinValue;
            foreach (Item i in s.Items)
            {
                if (i.Expected) continue;
                DateTime t = i.EndUtc ?? i.LastUtc;
                if (i.LastUtc > t) t = i.LastUtc;
                if (t.Year > 2000 && t > m) m = t;
            }
            return m;
        }

        // A user app or the account setup has been seen: the user has signed in.
        public static bool UserStarted(Session s)
        {
            foreach (Item i in s.Items)
            {
                if (i.Expected) continue;
                if (i.PhaseKey == Phases.AccountSetup || (AppGroups.IsApp(i) && AppGroups.Target(i) == AppGroups.User && i.StartUtc.HasValue)) return true;
            }
            return false;
        }

        static bool IsEsp(Session s)
        {
            foreach (Phase p in s.Phases)
            {
                if (p.Items.Count == 0) continue;
                if (p.Key.StartsWith("esp:", StringComparison.Ordinal) || p.Key.StartsWith("intune:esp", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // The deployment has nothing more to bring: everything that was seen is complete or failed and, when the viewer watches,
        // nothing new has come for a while. An Autopilot device that waits for its user to sign in is not done until the user
        // phase has been seen, or an hour has passed.
        public static bool IsDone(Session s, DateTime nowUtc, bool watching)
        {
            bool any = false;
            foreach (Item i in s.Items)
            {
                if (i.Expected) continue;
                any = true;
                if (States.IsActive(i.State)) return false;
                if (!States.IsComplete(i.State) && i.State != State.Failed) return false;
            }
            if (!any) return false;
            if (!watching) return true;
            DateTime last = LastHeard(s);
            TimeSpan quiet = last.Year > 2000 ? nowUtc - last : TimeSpan.Zero;
            if (quiet < Quiet) return false;
            if (quiet >= UserWait) return true;
            return !IsEsp(s) || UserStarted(s);
        }

        // Adds the placeholders for the expected apps that no reader has shown, or, when the deployment is done, the finding for
        // the Intune ones that never came. Returns how many placeholders there are. Call it after Finder.Run.
        public static int Merge(Session s, ExpectedSet set, DateTime nowUtc, bool watching)
        {
            Strip(s);
            if (s == null || set == null || set.Apps.Count == 0) return 0;
            DateTime anchor = LastHeard(s);
            if (anchor == DateTime.MinValue) return 0;
            List<ExpectedApp> missing = new List<ExpectedApp>();
            foreach (ExpectedApp a in set.Apps)
                if (Seen(s, a) == null) missing.Add(a);
            s.ExpectedText = set.Header;
            bool done = IsDone(s, nowUtc, watching);
            if (done)
            {
                bool failed = s.Count(State.Failed) > 0;
                if (missing.Count > 0) s.ExpectedText += ", " + missing.Count.ToString(CultureInfo.InvariantCulture) + " not seen on the device";
                if (set.Source != ExpectedSources.Intune || failed) return 0;
                foreach (ExpectedApp a in missing)
                {
                    if (a.Assignment != AppGroups.Required) continue;
                    Finding f = new Finding();
                    f.Kind = FindingKinds.ExpectedMissing;
                    f.Severity = 1;
                    f.Title = "Not installed: " + a.Name;
                    f.Message = "Intune has " + a.Name + " assigned as required to this device or its user, but it was not seen on the device by the time the deployment was done.";
                    f.Hint = "Check the assignment filter and the requirement rules of the app in Intune, sync the device, and look at the install status of the app for this device.";
                    f.TimeUtc = anchor;
                    s.Findings.Add(f);
                }
                return 0;
            }
            int n = 0;
            foreach (ExpectedApp a in missing)
            {
                Item i = new Item();
                i.Key = Key(a);
                i.Name = a.Name;
                i.Kind = string.IsNullOrEmpty(a.Kind) ? Kinds.Win32App : a.Kind;
                i.Origin = Origins.Intune;
                i.PhaseKey = Phases.IntuneApps;
                i.State = State.Pending;
                i.StateUtc = anchor;
                i.FirstUtc = anchor;
                i.LastUtc = anchor;
                i.Order = OrderBase + n;
                i.Expected = true;
                i.ExpectedSource = set.Source;
                i.Set(WhyKey, Why(a, set));
                if (!string.IsNullOrEmpty(a.Id)) i.Set("ID", a.Id.Trim('{', '}').ToLowerInvariant());
                i.Set(AppGroups.AssignmentKey, a.Assignment);
                i.Set(AppGroups.TargetKey, a.Target);
                Phase p = s.FindPhase(Phases.IntuneApps);
                if (p == null)
                {
                    p = new Phase();
                    p.Key = Phases.IntuneApps;
                    p.Name = Phases.Name(Phases.IntuneApps);
                    p.Order = Phases.Order(Phases.IntuneApps);
                    s.Phases.Add(p);
                    s.Index(p);
                }
                s.Items.Add(i);
                s.Index(i);
                p.Items.Add(i);
                n++;
            }
            foreach (Phase p in s.Phases) if (p.Key == Phases.IntuneApps) p.Items.Sort(Session.ItemOrder);
            if (n > 0) s.ExpectedText += ", " + n.ToString(CultureInfo.InvariantCulture) + " not on the device yet";
            return n;
        }

        public static string Why(ExpectedApp a, ExpectedSet set)
        {
            if (set.Source == ExpectedSources.Intune)
                return a.Assignment == AppGroups.Uninstall ? "Assigned in Intune to be removed, not seen on the device yet" : "Assigned in Intune, not on the device yet";
            return "Usually installed (in " + a.Seen.ToString(CultureInfo.InvariantCulture) + " of " + a.Of.ToString(CultureInfo.InvariantCulture) + " earlier deployments)";
        }
    }
}
