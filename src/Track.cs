namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // One thing a reader saw: a log line, a registry value, a WMI instance or an event record, already interpreted.
    public sealed class Obs
    {
        public DateTime TimeUtc;
        public string Source;
        public int Severity;
        public string Text;
        public Evidence Evidence;

        public string ItemKey;
        public string ItemName;
        public string ItemKind;
        public string Origin;
        public string PhaseKey;
        public string PhaseName;
        public int PhaseOrder;

        public State? State;
        public int? Percent;
        public long? ExitCode;
        public bool Reboot;
        public string Command;
        public bool NewAttempt;
        // Only update an item that another source already made, for example a detection run over every app.
        public bool OnlyIfExists;
        // The name is a fallback: an existing name wins.
        public bool WeakName;
        public List<Pair> Info;
        public List<Pair> SessionInfo;

        public long Seq;

        public Obs AddInfo(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return this;
            if (Info == null) Info = new List<Pair>();
            Info.Add(new Pair(key, value));
            return this;
        }

        public Obs AddSessionInfo(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return this;
            if (SessionInfo == null) SessionInfo = new List<Pair>();
            SessionInfo.Add(new Pair(key, value));
            return this;
        }

        public static Obs Log(LogLine l, string source)
        {
            Obs o = new Obs();
            o.TimeUtc = l.Utc ?? DateTime.MinValue;
            o.Source = source;
            o.Text = l.Text;
            o.Severity = l.Severity;
            o.Evidence = new Evidence(EvidenceKinds.LogLine, l.File, l.LineNo, l.Raw);
            return o;
        }
    }

    // Builds the session from observations. Runs on one thread only.
    public sealed class Tracker
    {
        public readonly Session S;
        long seq;
        int order;
        readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int Changes;

        public Tracker(Session s)
        {
            S = s;
            foreach (Pair p in s.Names) names[p.Key] = p.Value;
            foreach (Pair p in s.Aliases) aliases[p.Key] = p.Value;
            foreach (Item i in s.Items) order = Math.Max(order, i.Order);
            foreach (TEvent e in s.Events) seq = Math.Max(seq, e.Seq);
        }

        // Applies a batch in time order. Lines without a time keep their place after the line before them.
        public void ApplyAll(List<Obs> list)
        {
            if (list == null || list.Count == 0) return;
            for (int i = 0; i < list.Count; i++) list[i].Seq = i;
            DateTime last = DateTime.MinValue;
            foreach (Obs o in list)
            {
                if (o.TimeUtc == DateTime.MinValue) o.TimeUtc = last;
                else last = o.TimeUtc;
            }
            List<Obs> sorted = new List<Obs>(list);
            sorted.Sort(delegate (Obs a, Obs b)
            {
                int c = a.TimeUtc.CompareTo(b.TimeUtc);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });
            foreach (Obs o in sorted) Apply(o);
        }

        // Names for IDs (Intune app IDs). Renames items that only had their ID.
        public void AddNames(Dictionary<string, string> found)
        {
            if (found == null) return;
            foreach (KeyValuePair<string, string> kv in found)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                string old;
                if (names.TryGetValue(kv.Key, out old) && old == kv.Value) continue;
                names[kv.Key] = kv.Value;
                bool set = false;
                foreach (Pair p in S.Names) if (string.Equals(p.Key, kv.Key, StringComparison.OrdinalIgnoreCase)) { p.Value = kv.Value; set = true; }
                if (!set) S.Names.Add(new Pair(kv.Key, kv.Value));
            }
            foreach (Item i in S.Items)
            {
                string id = i.Get("ID");
                string n;
                if (id != null && names.TryGetValue(id, out n) && (string.IsNullOrEmpty(i.Name) || i.Name == id || i.Name == i.Key))
                {
                    i.Name = n;
                    Changes++;
                }
            }
        }

        public string NameFor(string id)
        {
            string n;
            return id != null && names.TryGetValue(id, out n) ? n : null;
        }

        // Treats an item key as another: an SCCM deployment type that belongs to an application, for example.
        public void Alias(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
            string cur;
            if (aliases.TryGetValue(from, out cur) && string.Equals(cur, to, StringComparison.OrdinalIgnoreCase)) return;
            aliases[from] = to;
            S.Aliases.RemoveAll(delegate (Pair p) { return string.Equals(p.Key, from, StringComparison.OrdinalIgnoreCase); });
            S.Aliases.Add(new Pair(from, to));
            Item src = S.Find(from);
            if (src == null) return;
            Item dst = S.Find(to);
            if (dst == null)
            {
                S.Unindex(src.Key);
                src.Key = to;
                S.Index(src);
                foreach (TEvent e in src.Events) e.ItemKey = to;
                Changes++;
                return;
            }
            Merge(src, dst);
        }

        void Merge(Item src, Item dst)
        {
            foreach (TEvent e in src.Events)
            {
                e.ItemKey = dst.Key;
                dst.Events.Add(e);
            }
            dst.Events.Sort(EventOrder);
            foreach (Attempt a in src.Attempts) dst.Attempts.Add(a);
            dst.Attempts.Sort(delegate (Attempt a, Attempt b) { return a.StartUtc.CompareTo(b.StartUtc); });
            for (int k = 0; k < dst.Attempts.Count; k++) dst.Attempts[k].Index = k + 1;
            foreach (Pair p in src.Info) if (dst.Get(p.Key) == null) dst.Info.Add(p);
            if (src.StateUtc > dst.StateUtc)
            {
                dst.StateName = src.StateName;
                dst.StateUtc = src.StateUtc;
                if (src.ExitCode.HasValue) dst.ExitCode = src.ExitCode;
                if (src.Percent.HasValue) dst.Percent = src.Percent;
            }
            if (src.FirstUtc < dst.FirstUtc) dst.FirstUtc = src.FirstUtc;
            if (src.LastUtc > dst.LastUtc) dst.LastUtc = src.LastUtc;
            if (src.StartUtc.HasValue && (dst.StartUtc == null || src.StartUtc < dst.StartUtc)) dst.StartUtc = src.StartUtc;
            if (src.EndUtc.HasValue && (dst.EndUtc == null || src.EndUtc > dst.EndUtc)) dst.EndUtc = src.EndUtc;
            dst.Reboot |= src.Reboot;
            dst.Order = Math.Min(dst.Order, src.Order);
            S.Items.Remove(src);
            S.Unindex(src.Key);
            Phase ph = S.FindPhase(src.PhaseKey);
            if (ph != null) ph.Items.Remove(src);
            Changes++;
        }

        static int EventOrder(TEvent a, TEvent b)
        {
            int c = a.TimeUtc.CompareTo(b.TimeUtc);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }

        public void Apply(Obs o)
        {
            if (o == null) return;
            if (o.SessionInfo != null) foreach (Pair p in o.SessionInfo) S.SetInfo(p.Key, p.Value);
            Item item = null;
            if (!string.IsNullOrEmpty(o.ItemKey))
            {
                string key = o.ItemKey, alias;
                if (aliases.TryGetValue(key, out alias)) key = alias;
                item = S.Find(key);
                if (item == null)
                {
                    if (o.OnlyIfExists) return;
                    item = NewItem(key, o);
                }
                Update(item, o);
            }
            if (o.Text == null && o.State == null) return;
            if (o.Text == null && item == null) return;
            TEvent e = new TEvent();
            e.TimeUtc = o.TimeUtc;
            e.Source = o.Source;
            e.Severity = o.Severity;
            e.Text = o.Text ?? (o.State.HasValue ? States.Label(o.State.Value) : "");
            e.StateName = o.State.HasValue ? o.State.Value.ToString() : null;
            e.ItemKey = item != null ? item.Key : null;
            e.Evidence = o.Evidence;
            e.Seq = ++seq;
            Insert(S.Events, e);
            if (item != null) Insert(item.Events, e);
            Changes++;
        }

        static void Insert(List<TEvent> list, TEvent e)
        {
            int i = list.Count;
            while (i > 0 && EventOrder(list[i - 1], e) > 0) i--;
            list.Insert(i, e);
        }

        Item NewItem(string key, Obs o)
        {
            Item i = new Item();
            i.Key = key;
            i.Name = o.ItemName;
            i.Kind = o.ItemKind;
            i.Origin = o.Origin;
            i.FirstUtc = o.TimeUtc;
            i.LastUtc = o.TimeUtc;
            i.StateUtc = DateTime.MinValue;
            i.Order = ++order;
            i.State = State.Unknown;
            S.Items.Add(i);
            S.Index(i);
            Phase p = EnsurePhase(o.PhaseKey ?? "other", o.PhaseName, o.PhaseOrder);
            i.PhaseKey = p.Key;
            p.Items.Add(i);
            return i;
        }

        public Phase EnsurePhase(string key, string name, int ord)
        {
            Phase p = S.FindPhase(key);
            if (p != null)
            {
                if (!string.IsNullOrEmpty(name) && p.Name != name && Phases.Name(key) == key) p.Name = name;
                return p;
            }
            p = new Phase();
            p.Key = key;
            p.Name = !string.IsNullOrEmpty(name) ? name : Phases.Name(key);
            p.Order = ord > 0 ? ord : Phases.Order(key);
            S.Phases.Add(p);
            S.Index(p);
            return p;
        }

        void Update(Item i, Obs o)
        {
            DateTime t = o.TimeUtc;
            if (t < i.FirstUtc || i.FirstUtc == DateTime.MinValue) i.FirstUtc = t;
            if (t > i.LastUtc) i.LastUtc = t;
            if (!string.IsNullOrEmpty(o.ItemName))
            {
                bool weakOld = string.IsNullOrEmpty(i.Name) || i.Name == i.Key || i.Name == i.Get("ID");
                if (!o.WeakName || weakOld) i.Name = o.ItemName;
            }
            else if (string.IsNullOrEmpty(i.Name))
            {
                string n = NameFor(i.Get("ID"));
                if (n != null) i.Name = n;
            }
            if (string.IsNullOrEmpty(i.Kind)) i.Kind = o.ItemKind;
            if (string.IsNullOrEmpty(i.Origin)) i.Origin = o.Origin;
            // An ESP phase is where the item really belongs.
            if (o.PhaseKey != null && Phases.IsEsp(o.PhaseKey) && i.PhaseKey != o.PhaseKey && !Phases.IsEsp(i.PhaseKey))
            {
                Phase old = S.FindPhase(i.PhaseKey);
                if (old != null) old.Items.Remove(i);
                Phase np = EnsurePhase(o.PhaseKey, o.PhaseName, o.PhaseOrder);
                i.PhaseKey = np.Key;
                np.Items.Add(i);
                np.Items.Sort(Session.ItemOrder);
            }
            if (o.Info != null)
            {
                foreach (Pair p in o.Info)
                {
                    i.Set(p.Key, p.Value);
                    if (p.Key == "ID" && string.IsNullOrEmpty(i.Name)) i.Name = NameFor(p.Value);
                }
            }

            Attempt a = i.Current;
            bool startsActivity = o.State.HasValue && States.IsActive(o.State.Value);
            // A start that another source already opened (the ESP said "in progress" just before the download began) is the same attempt.
            bool open = a != null && !a.EndUtc.HasValue && !a.ExitCode.HasValue && string.IsNullOrEmpty(a.Command);
            if ((o.NewAttempt && !open) || (startsActivity && (a == null || a.EndUtc.HasValue)))
            {
                a = new Attempt();
                a.Index = i.Attempts.Count + 1;
                a.StartUtc = t;
                a.State = State.Pending;
                i.Attempts.Add(a);
                if (o.NewAttempt)
                {
                    i.Percent = null;
                    i.Reboot = false;
                }
            }
            if (!string.IsNullOrEmpty(o.Command))
            {
                if (a != null) a.Command = o.Command;
                i.Set("Command line", o.Command);
            }
            if (o.ExitCode.HasValue)
            {
                i.ExitCode = o.ExitCode;
                if (a != null) a.ExitCode = o.ExitCode;
            }
            if (o.Reboot)
            {
                i.Reboot = true;
                if (a != null) a.Reboot = true;
            }
            if (o.Percent.HasValue) i.Percent = Math.Max(0, Math.Min(100, o.Percent.Value));
            if (o.State.HasValue && t >= i.StateUtc)
            {
                State s = o.State.Value;
                if (s == State.Succeeded && i.Reboot) s = State.WaitingReboot;
                // Download progress is not install progress.
                if (i.State == State.Downloading && (s == State.Installing || s == State.Detecting) && !o.Percent.HasValue) i.Percent = null;
                i.State = s;
                i.StateUtc = t;
                if (States.IsActive(s))
                {
                    if (i.StartUtc == null) i.StartUtc = a != null ? a.StartUtc : t;
                    i.EndUtc = null;
                }
                if (s == State.Succeeded || s == State.WaitingReboot) i.Percent = 100;
                if (a != null)
                {
                    a.State = s;
                    if (States.IsFinal(s) || s == State.WaitingReboot) a.EndUtc = t;
                }
                if (States.IsFinal(s) || s == State.WaitingReboot)
                {
                    if (i.StartUtc != null || a != null) i.EndUtc = t;
                }
            }
            Changes++;
        }
    }

    // Looks at the session and lists what needs attention. Runs again after every change.
    public static class Finder
    {
        public static TimeSpan StuckAfter = TimeSpan.FromMinutes(15);

        public static void Run(Session s, DateTime nowUtc, bool live)
        {
            List<Finding> list = new List<Finding>();
            foreach (Item i in s.Items)
            {
                // The run item of a remote task sequence fails with it; the Task sequence failed finding covers that.
                // It also only hears from the site when the task sequence ends, so silence is not a sign of trouble.
                if (i.Key == SccmFeed.RunKey) continue;
                if (i.State == State.Failed)
                {
                    long code = i.ExitCode ?? 0;
                    if (code != 0 && Errors.IsNotDetected(code) && i.Kind != Kinds.Step)
                        list.Add(F(FindingKinds.NotDetected, 2, i, i.DisplayName + " was not detected after install",
                            "The installer finished" + ExitText(i) + ", but the detection rule did not find the app afterwards.",
                            "Check the detection rule against what the installer really writes: the product code, file version or registry value."));
                    else
                        list.Add(F(FindingKinds.Failed, 2, i, i.DisplayName + " failed", FailText(i), Errors.Hint(code)));
                }
                if (i.Attempts.Count >= 3 && i.FailedAttempts >= 2)
                    list.Add(F(FindingKinds.Retries, 1, i, i.DisplayName + " was tried " + i.Attempts.Count + " times",
                        i.FailedAttempts + " of " + i.Attempts.Count + " attempts failed. The agent retries failed required installs, so the same error tends to come back.",
                        "Fix the cause of the first failure. The later attempts usually fail the same way."));
                if (i.State == State.WaitingReboot)
                    list.Add(F(FindingKinds.Reboot, 0, i, i.DisplayName + " needs a restart",
                        "It installed" + ExitText(i) + " and asked for a restart to finish.", null));
                if (live && States.IsActive(i.State) && nowUtc - i.LastUtc > TimeSpan.FromDays(1))
                    list.Add(F(FindingKinds.Stuck, 1, i, i.DisplayName + " has not changed since " + Txt.Local(i.LastUtc),
                        "It is still recorded as " + States.Label(i.State).ToLowerInvariant() + ", but nothing has changed for " + Txt.DurationShort(nowUtc - i.LastUtc) + ". The deployment it belonged to probably ended without it.",
                        "If the app should be on this device, check its install status in Intune or Software Center. The Enrollment Status Page keeps the last state it saw."));
                else if (live && States.IsActive(i.State) && nowUtc - i.LastUtc > StuckAfter)
                    list.Add(F(FindingKinds.Stuck, 1, i, "No progress on " + i.DisplayName + " for " + Txt.DurationShort(nowUtc - i.LastUtc),
                        States.Label(i.State) + " since " + Txt.LocalTime(i.StateUtc) + ", and no source has said anything about it since " + Txt.LocalTime(i.LastUtc) + ".",
                        i.State == State.Downloading ? "Check the content location and the download logs (CAS.log, DataTransferService.log, or Delivery Optimization for Intune)." : "The installer may be waiting for input. Check that the install command is silent."));
            }

            string tsResult = s.GetInfo("Task sequence result");
            if (tsResult != null && tsResult.StartsWith("Failed", StringComparison.Ordinal))
            {
                Finding f = new Finding();
                f.Kind = FindingKinds.TaskSequence;
                f.Severity = 2;
                f.Title = "The task sequence failed";
                f.Message = (s.GetInfo("Task sequence") ?? "The task sequence") + " ended with " + tsResult.Substring(6).Trim(' ', ':') + ".";
                f.Hint = "The first failed step is usually the cause. Steps after it did not run.";
                f.TimeUtc = ParseTime(s.GetInfo("Task sequence ended")) ?? nowUtc;
                list.Insert(0, f);
            }

            int limit;
            if (int.TryParse(s.GetInfo("ESP time limit (min)"), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) && limit > 0)
            {
                foreach (string key in new string[] { Phases.DeviceSetup, Phases.AccountSetup })
                {
                    Phase p = s.FindPhase(key);
                    if (p == null || p.Items.Count == 0) continue;
                    DateTime? start = p.StartUtc;
                    DateTime end = p.EndUtc ?? nowUtc;
                    if (start == null || (end - start.Value).TotalMinutes < limit) continue;
                    List<string> blocking = new List<string>();
                    Item first = null;
                    foreach (Item i in p.Items)
                    {
                        if (States.IsComplete(i.State)) continue;
                        blocking.Add(i.DisplayName + " (" + States.Label(i.State).ToLowerInvariant() + ")");
                        if (first == null) first = i;
                    }
                    if (blocking.Count == 0) continue;
                    Finding f = F(FindingKinds.EspTimeLimit, 2, first, p.Name + " ran past the ESP time limit of " + limit + " min",
                        "The Enrollment Status Page shows an error once its time limit passes. Still not done: " + string.Join(", ", blocking.ToArray()) + ".",
                        "Look at what the blocking items were doing. Raise the time limit in the ESP profile only if the installs are just slow.");
                    f.TimeUtc = start.Value.AddMinutes(limit);
                    list.Insert(0, f);
                }
            }

            int mdm = 0;
            TEvent firstMdm = null;
            foreach (TEvent e in s.Events)
            {
                if (e.Source == "MDM" && e.Severity >= 2)
                {
                    mdm++;
                    if (firstMdm == null) firstMdm = e;
                }
            }
            if (mdm > 0)
            {
                Finding f = new Finding();
                f.Kind = FindingKinds.MdmErrors;
                f.Severity = 1;
                f.Title = mdm + " MDM error" + (mdm == 1 ? "" : "s") + " in the event log";
                f.Message = "The DeviceManagement Admin channel logged errors while applying policy. The first: " + Txt.Short(firstMdm.Text, 220);
                f.Hint = "Open the item in PolicyVice or the MDM events in LogVice to see which setting failed.";
                f.TimeUtc = firstMdm.TimeUtc;
                f.Evidence = firstMdm.Evidence;
                list.Add(f);
            }

            list.Sort(delegate (Finding a, Finding b)
            {
                int c = b.Severity.CompareTo(a.Severity);
                return c != 0 ? c : a.TimeUtc.CompareTo(b.TimeUtc);
            });
            s.Findings = list;
        }

        static DateTime? ParseTime(string s)
        {
            DateTime t;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return null;
        }

        // The installer's own exit code, which is not the error the item failed with when detection failed afterwards.
        static string ExitText(Item i)
        {
            string x = i.Get("Installer exit code");
            if (x != null) return " with exit code " + x;
            if (i.ExitCode.HasValue && Errors.IsSuccess(i.ExitCode.Value)) return " with exit code " + i.ExitCode.Value.ToString(CultureInfo.InvariantCulture);
            return "";
        }

        static string FailText(Item i)
        {
            string where = i.Kind == Kinds.Step ? "The step" : "It";
            if (!i.ExitCode.HasValue || i.ExitCode.Value == 0) return where + " failed. Its events show what was reported.";
            return where + " failed with " + Errors.Describe(i.ExitCode.Value) + ".";
        }

        static Finding F(string kind, int sev, Item i, string title, string msg, string hint)
        {
            Finding f = new Finding();
            f.Kind = kind;
            f.Severity = sev;
            f.ItemKey = i != null ? i.Key : null;
            f.Title = title;
            f.Message = msg;
            f.Hint = hint;
            f.TimeUtc = i != null ? (i.EndUtc ?? i.LastUtc) : DateTime.UtcNow;
            if (i != null)
            {
                for (int k = i.Events.Count - 1; k >= 0; k--)
                {
                    TEvent e = i.Events[k];
                    if (e.Evidence != null && (e.Severity >= 2 || e.State == State.Failed)) { f.Evidence = e.Evidence; break; }
                }
                if (f.Evidence == null) f.Evidence = i.LastEvidence;
            }
            return f;
        }
    }

    public static class Errors
    {
        static readonly Dictionary<uint, string> Sccm = new Dictionary<uint, string>
        {
            { 0x87D00324, "The application was not detected after installation completed." },
            { 0x87D00325, "The application was still detected after uninstall completed." },
            { 0x87D00607, "The content for this deployment was not found on a distribution point." },
            { 0x87D00213, "The job timed out before it finished." },
            { 0x87D01106, "The program or its command line could not be found or is not valid." },
            { 0x80004005, "Unspecified error." },
            { 0x80070002, "The system cannot find the file specified." },
            { 0x80070003, "The system cannot find the path specified." },
            { 0x80070005, "Access is denied." },
            { 0x800705B4, "The operation timed out." },
        };

        static readonly Dictionary<uint, string> Hints = new Dictionary<uint, string>
        {
            { 0x87D00324, "The installer finished, but the detection method did not find the app. Check the detection method against what the installer writes." },
            { 0x87D00607, "Distribute the content to a distribution point in the device's boundary group, or allow fallback." },
            { 0x87D00213, "The installer ran longer than the maximum run time. Check that it is silent and does not wait for input." },
            { 0x80070002, "A file the step needs is missing: check the content, the command line and the working folder." },
            { 1603, "A fatal error inside the installer. Add /l*v to the msiexec command line and look for \"return value 3\" in the MSI log." },
            { 1618, "Another installation was running. The agent normally retries later." },
            { 0x80070643, "A fatal error inside the installer. Add /l*v to the msiexec command line and look for \"return value 3\" in the MSI log." },
        };

        public static bool IsNotDetected(long code)
        {
            uint u = unchecked((uint)code);
            return u == 0x87D00324 || u == 0x87D1041C;
        }

        public static bool IsSuccess(long code)
        {
            return ImeCodes.IsSuccess(code);
        }

        public static string Meaning(long code)
        {
            if (code < int.MinValue || code > uint.MaxValue) return null;
            uint u = unchecked((uint)code);
            string m;
            if (Sccm.TryGetValue(u, out m)) return m;
            return ImeCodes.Meaning(code);
        }

        // "0x87D00324: meaning", "1603 (0x00000643): meaning", "0".
        public static string Describe(long code)
        {
            if (code == 0) return "exit code 0";
            string m = Meaning(code);
            string s;
            if (code > 0 && code < 0x10000) s = "exit code " + code.ToString(CultureInfo.InvariantCulture);
            else if (code >= int.MinValue && code <= uint.MaxValue) s = "error 0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture);
            else s = "error " + code.ToString(CultureInfo.InvariantCulture);
            return m == null ? s : s + " (" + m.TrimEnd('.') + ")";
        }

        public static string Short(long? code)
        {
            if (!code.HasValue) return "";
            long c = code.Value;
            if (c >= 0 && c < 0x10000) return c.ToString(CultureInfo.InvariantCulture);
            if (c >= int.MinValue && c <= uint.MaxValue) return "0x" + unchecked((uint)c).ToString("X8", CultureInfo.InvariantCulture);
            return c.ToString(CultureInfo.InvariantCulture);
        }

        public static string Hint(long code)
        {
            if (code == 0 || code < int.MinValue || code > uint.MaxValue) return null;
            string h;
            if (Hints.TryGetValue(unchecked((uint)code), out h)) return h;
            return ImeCodes.Hint(code);
        }

        // Parses 0x80070002, -2147024894, 80070002 (as hex when it has letters or 8 digits) and 1603.
        public static long? Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim().TrimEnd('.', ',', ')');
            long n;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(s.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n) ? (long?)unchecked((int)(uint)n) : null;
            bool hexy = s.Length == 8 && System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9A-Fa-f]{8}$") && (s[0] == '8' || s[0] == 'C' || s[0] == 'c' || System.Text.RegularExpressions.Regex.IsMatch(s, "[A-Fa-f]"));
            if (hexy && long.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n)) return unchecked((int)(uint)n);
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return n;
            return null;
        }
    }
}
