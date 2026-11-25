namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    // Adds plain words on why things failed to the findings the Finder made, and the findings that need the whole session.
    public static class Why
    {
        public static double AheadShare = 0.75;
        public static TimeSpan RestartAfter = TimeSpan.FromMinutes(5);
        public static int SameCodeCount = 3;

        // Error text that usually comes from a script written for 64-bit PowerShell that ran in the 32-bit host,
        // where System32 and HKLM\SOFTWARE are redirected and some modules are missing.
        static readonly Regex RxBitness = new Regex(@"System32|SysNative|HKLM:\\?SOFTWARE|HKEY_LOCAL_MACHINE\\SOFTWARE|is not recognized as the name of a cmdlet|module .{0,80}(was not loaded|could not be (found|loaded))|Could not find a part of the path|Cannot find path|dism|WindowsCapability|WindowsOptionalFeature|BitLocker|manage-bde", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RxDetection = new Regex(@"^Detection\b|detection state|not detected|not discovered|Discovered application|Last detection", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void Apply(Session s, List<Finding> list, DateTime nowUtc)
        {
            foreach (Finding f in list)
            {
                Item i = s.Find(f.ItemKey);
                if (i == null) continue;
                if (f.Kind == FindingKinds.Failed) Failed(f, i);
                else if (f.Kind == FindingKinds.NotDetected) NotDetected(f, i);
            }
            EspAhead(s, list, nowUtc);
            WaitingRestart(s, list, nowUtc);
            SameCode(s, list);
            list.Sort(Order);
        }

        // Severity first, a common cause before the items it covers, then time.
        static int Order(Finding a, Finding b)
        {
            int c = b.Severity.CompareTo(a.Severity);
            if (c != 0) return c;
            bool ca = a.Kind == FindingKinds.CommonCause, cb = b.Kind == FindingKinds.CommonCause;
            if (ca != cb) return ca ? -1 : 1;
            return a.TimeUtc.CompareTo(b.TimeUtc);
        }

        static void Failed(Finding f, Item i)
        {
            if (i.Kind == Kinds.Script) Script(f, i);
            else if (i.Kind == Kinds.Office) Office(f, i);
            else if (i.Kind == Kinds.MsiApp) Msi(f, i);
        }

        public static bool LikelyBitnessMismatch(string host, string error)
        {
            return host == "32-bit" && !string.IsNullOrEmpty(error) && RxBitness.IsMatch(error);
        }

        static void Script(Finding f, Item i)
        {
            string err = Explain.ScriptError(i);
            string exit = i.Get("PowerShell exit code");
            if (exit == null && i.ExitCode.HasValue && i.ExitCode.Value != 0) exit = Errors.Short(i.ExitCode);
            string host = i.Get("PowerShell host");
            string msg = "The script failed" + (exit != null ? " with exit code " + exit : "") + (host != null ? " in " + host + " PowerShell" : "") + ".";
            msg += err != null ? " It said: \"" + Txt.Short(err, 300) + "\"" : " No error text from the script was seen.";
            if (LikelyBitnessMismatch(host, err))
            {
                msg += " This error often means the script was written for 64-bit PowerShell but ran in the 32-bit host.";
                f.Hint = "Set \"Run script in 64 bit PowerShell Host\" to Yes in the script's settings, or start the 64-bit host from the script through %windir%\\SysNative.";
            }
            else f.Hint = Explain.Hint(i);
            f.Message = msg + ScriptRerun.Extra(i);
        }

        static void Office(Finding f, Item i)
        {
            long code = i.ExitCode ?? 0;
            string meaning = code != 0 ? ErrorBase.OfficeMeaning(code) : null;
            string msg = "Microsoft 365 Apps setup failed";
            if (code != 0) msg += " with status " + code.ToString(CultureInfo.InvariantCulture);
            f.Message = msg + "." + (meaning != null ? " " + meaning : "");
            f.Hint = (code != 0 ? ErrorBase.OfficeNext(code) : null) ?? Explain.Hint(i);
        }

        static void Msi(Finding f, Item i)
        {
            string label = null;
            for (int k = i.Events.Count - 1; k >= 0 && label == null; k--)
            {
                string t = i.Events[k].Text;
                if (t == null || !t.StartsWith("MSI: ", StringComparison.Ordinal)) continue;
                label = t.Substring(5);
                int cut = label.IndexOf(", ", StringComparison.Ordinal);
                if (cut > 0) label = label.Substring(0, cut);
            }
            long code = i.ExitCode ?? 0;
            f.Message = "The MDM client reported " + (label != null ? "\"" + label + "\"" : "a failure") + (code != 0 ? " with " + Errors.Describe(code) : "") + ".";
            f.Hint = (code != 0 ? Errors.Hint(code) : null) ?? Explain.Hint(i);
        }

        static void NotDetected(Finding f, Item i)
        {
            for (int k = i.Events.Count - 1; k >= 0; k--)
            {
                TEvent e = i.Events[k];
                if (e.Text == null || !RxDetection.IsMatch(e.Text)) continue;
                string where = e.Evidence != null ? e.Evidence.Where : e.Source;
                f.Message += " Detection evidence seen: " + (string.IsNullOrEmpty(where) ? "" : where + " says ") + "\"" + Txt.Short(e.Text, 160) + "\".";
                if (e.Evidence != null) f.Evidence = e.Evidence;
                return;
            }
            string last = i.Get("Last detection");
            f.Message += last != null ? " The last detection state seen: " + last + "." : " No detection result was seen in the sources read.";
        }

        static void EspAhead(Session s, List<Finding> list, DateTime nowUtc)
        {
            int limit;
            if (!int.TryParse(s.GetInfo("ESP time limit (min)"), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit <= 0) return;
            foreach (string key in new string[] { Phases.DeviceSetup, Phases.AccountSetup })
            {
                Phase p = s.FindPhase(key);
                if (p == null || p.Items.Count == 0 || p.EndUtc != null) continue;
                DateTime? start = p.StartUtc;
                if (start == null) continue;
                double ran = (nowUtc - start.Value).TotalMinutes;
                if (ran < limit * AheadShare || ran >= limit) continue;
                List<string> running = new List<string>();
                Item first = null;
                foreach (Item i in p.Items)
                {
                    if (States.IsComplete(i.State) || i.State == State.Failed) continue;
                    running.Add(i.DisplayName + " (" + States.Label(i.State).ToLowerInvariant() + ")");
                    if (first == null) first = i;
                }
                if (running.Count == 0) continue;
                int left = Math.Max(1, (int)Math.Ceiling(limit - ran));
                Finding f = new Finding();
                f.Kind = FindingKinds.EspTimeLimitAhead;
                f.Severity = 1;
                f.ItemKey = first.Key;
                f.Title = p.Name + " is near the ESP time limit of " + limit + " min";
                f.Message = p.Name + " has run for " + (int)ran + " min, so about " + left + " min are left. Still running: " + string.Join(", ", running.ToArray()) + ". When the limit passes, the Enrollment Status Page shows an error and setup stops, unless the profile lets the user continue anyway.";
                f.Hint = "Look at what the items still running are doing. If they are only slow, raise the time limit in the ESP profile.";
                f.TimeUtc = start.Value.AddMinutes(limit * AheadShare);
                f.Evidence = first.LastEvidence;
                list.Add(f);
            }
        }

        static void WaitingRestart(Session s, List<Finding> list, DateTime nowUtc)
        {
            foreach (Phase p in s.Phases)
            {
                bool active = false;
                List<Item> waiting = new List<Item>();
                foreach (Item i in p.Items)
                {
                    if (States.IsActive(i.State)) active = true;
                    if (i.State == State.Pending || i.State == State.Unknown) waiting.Add(i);
                }
                if (active || waiting.Count == 0) continue;
                foreach (Item r in p.Items)
                {
                    if (r.State != State.WaitingReboot || nowUtc - r.StateUtc < RestartAfter) continue;
                    bool moved = false;
                    foreach (Item w in waiting) if (nowUtc - w.LastUtc < RestartAfter) moved = true;
                    if (moved) continue;
                    List<string> names = new List<string>();
                    foreach (Item w in waiting) names.Add(w.DisplayName);
                    list.RemoveAll(delegate (Finding x) { return x.Kind == FindingKinds.Reboot && x.ItemKey == r.Key; });
                    Finding f = new Finding();
                    f.Kind = FindingKinds.WaitingRestart;
                    f.Severity = 1;
                    f.ItemKey = r.Key;
                    f.Title = "Waiting for a restart after " + r.DisplayName;
                    f.Message = r.DisplayName + " asked for a restart " + Txt.DurationShort(nowUtc - r.StateUtc) + " ago, and " + waiting.Count + (waiting.Count == 1 ? " item" : " items") + " in " + p.Name + " still wait: " + Txt.Short(string.Join(", ", names.ToArray()), 200) + ". They usually start only after the restart.";
                    f.Hint = "Restart the device if nothing else does. The Enrollment Status Page and task sequences normally restart by themselves, so a long wait often means the restart was blocked or postponed.";
                    f.TimeUtc = r.StateUtc.Add(RestartAfter);
                    f.Evidence = r.LastEvidence;
                    list.Add(f);
                    break;
                }
            }
        }

        static void SameCode(Session s, List<Finding> list)
        {
            Dictionary<string, List<Item>> groups = new Dictionary<string, List<Item>>();
            List<string> order = new List<string>();
            foreach (Item i in s.Items)
            {
                if (i.State != State.Failed || !i.ExitCode.HasValue || i.ExitCode.Value == 0 || i.Key == SccmFeed.RunKey) continue;
                string key = (i.PhaseKey ?? "") + "|" + i.ExitCode.Value.ToString(CultureInfo.InvariantCulture);
                List<Item> g;
                if (!groups.TryGetValue(key, out g)) { g = new List<Item>(); groups[key] = g; order.Add(key); }
                g.Add(i);
            }
            foreach (string key in order)
            {
                List<Item> g = groups[key];
                if (g.Count < SameCodeCount) continue;
                long code = g[0].ExitCode.Value;
                Phase p = s.FindPhase(g[0].PhaseKey);
                List<string> names = new List<string>();
                DateTime first = DateTime.MaxValue;
                foreach (Item i in g)
                {
                    names.Add(i.DisplayName);
                    DateTime t = i.EndUtc ?? i.LastUtc;
                    if (t < first) first = t;
                }
                Finding f = new Finding();
                f.Kind = FindingKinds.CommonCause;
                f.Severity = 2;
                f.ItemKey = g[0].Key;
                f.Title = g.Count + " items " + (p != null ? "in " + p.Name + " " : "") + "failed with " + Errors.Short(code);
                f.Message = Txt.Short(string.Join(", ", names.ToArray()), 300) + " all failed with " + Errors.Describe(code) + ". The same code on several items usually points at one cause on the device, such as the network, disk space or another install, not at each app.";
                f.Hint = Errors.Hint(code) ?? "Look for the common cause first. The items usually succeed on their next try once it is fixed.";
                f.TimeUtc = first;
                foreach (Finding x in list) if (x.ItemKey == g[0].Key && x.Evidence != null) { f.Evidence = x.Evidence; break; }
                list.Add(f);
            }
        }
    }
}
