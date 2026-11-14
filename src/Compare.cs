namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // ---------- two enrollments side by side ----------

    public sealed class DiffRow
    {
        public const string FindingKind = "Finding";
        public string Name, Kind, StateA, StateB, Note;
        public double? SecondsA, SecondsB;
        // 0 the same, 1 worth a look, 2 the difference that matters.
        public int Weight;
        // The items behind the row, null on the side that does not have it. Findings have none.
        public Item ItemA, ItemB;

        public bool IsFinding
        {
            get { return Kind == FindingKind; }
        }
    }

    // Both sessions as a whole: what the header of the comparison shows.
    public sealed class DiffSummary
    {
        public string NameA, NameB, KindA, KindB;
        public DateTime? StartA, StartB;
        public TimeSpan? TotalA, TotalB;
        public int ItemsA, ItemsB, DoneA, DoneB, FailedA, FailedB;
        // Items that differ, and items that failed on one side only.
        public int Differ, FailedOnlyA, FailedOnlyB;
        public string Sentence;
    }

    public static class SessionDiff
    {
        static string Sig(Item i)
        {
            return i.Kind + "|" + i.DisplayName;
        }

        static string KeySig(Item i)
        {
            return i.Kind + "|" + i.Key;
        }

        static DateTime NowOf(Session s)
        {
            return s.ClockUtc.Year > 2000 ? s.ClockUtc : DateTime.UtcNow;
        }

        static double? Seconds(Item i, DateTime now)
        {
            if (i == null) return null;
            TimeSpan? d = i.Duration(now);
            return d.HasValue ? d.Value.TotalSeconds : (double?)null;
        }

        // The names the two sides go by. Two sessions of one device are told apart as A and B.
        public static string NameA(Session a, Session b)
        {
            string n = string.IsNullOrEmpty(a.Computer) ? "A" : a.Computer;
            return string.Equals(a.Computer, b.Computer, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.Computer) ? n + " (A)" : n;
        }

        public static string NameB(Session a, Session b)
        {
            string n = string.IsNullOrEmpty(b.Computer) ? "B" : b.Computer;
            return string.Equals(a.Computer, b.Computer, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(b.Computer) ? n + " (B)" : n;
        }

        public static List<DiffRow> Compare(Session a, Session b)
        {
            return Compare(a, NowOf(a), b, NowOf(b));
        }

        // What differs between two enrollments: items only one has, different results, exit codes and attempts,
        // clearly different durations, and findings only one side has. Items match by ID first, then by name.
        public static List<DiffRow> Compare(Session a, DateTime nowA, Session b, DateTime nowB)
        {
            string na = NameA(a, b), nb = NameB(a, b);
            Dictionary<string, Item> byKey = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Item> byName = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (Item j in b.Items)
            {
                if (!byKey.ContainsKey(KeySig(j))) byKey[KeySig(j)] = j;
                if (!byName.ContainsKey(Sig(j))) byName[Sig(j)] = j;
            }
            List<DiffRow> rows = new List<DiffRow>();
            HashSet<Item> used = new HashSet<Item>();
            HashSet<string> seenA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Item i in a.Items)
            {
                if (!seenA.Add(KeySig(i))) continue;
                Item j;
                if (!byKey.TryGetValue(KeySig(i), out j) || used.Contains(j))
                    if (!byName.TryGetValue(Sig(i), out j) || used.Contains(j)) j = null;
                if (j != null) used.Add(j);
                rows.Add(Row(i, j, nowA, nowB, na, nb));
            }
            HashSet<string> seenB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Item j in b.Items)
            {
                if (used.Contains(j) || !seenB.Add(KeySig(j))) continue;
                rows.Add(Row(null, j, nowA, nowB, na, nb));
            }
            AddFindings(rows, a, b, na, false);
            AddFindings(rows, b, a, nb, true);
            rows.Sort(Order);
            return rows;
        }

        // Weighted rows first, findings after the items of the same weight, then by name.
        public static int Order(DiffRow x, DiffRow y)
        {
            int c = y.Weight.CompareTo(x.Weight);
            if (c != 0) return c;
            c = x.IsFinding.CompareTo(y.IsFinding);
            return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        static DiffRow Row(Item i, Item j, DateTime nowA, DateTime nowB, string na, string nb)
        {
            DiffRow r = new DiffRow();
            r.ItemA = i;
            r.ItemB = j;
            // A name beats an ID when only one side knows the name.
            Item named = i != null && !string.IsNullOrEmpty(i.Name) ? i : j != null && !string.IsNullOrEmpty(j.Name) ? j : i ?? j;
            r.Name = named.DisplayName;
            r.Kind = named.Kind;
            r.StateA = i != null ? States.Label(i.State) : "";
            r.StateB = j != null ? States.Label(j.State) : "";
            r.SecondsA = Seconds(i, nowA);
            r.SecondsB = Seconds(j, nowB);
            List<string> notes = new List<string>();
            int w = 0;
            if (i == null || j == null)
            {
                Item one = i ?? j;
                notes.Add("Only on " + (i != null ? na : nb));
                w = one.State == State.NotApplicable || one.State == State.Skipped ? 1 : 2;
            }
            else if (i.State != j.State)
            {
                if (i.State == State.Failed || j.State == State.Failed)
                {
                    bool onA = i.State == State.Failed;
                    notes.Add("Failed on " + (onA ? na : nb) + FailCode(onA ? i : j));
                    w = 2;
                }
                else if (States.IsActive(i.State) != States.IsActive(j.State))
                {
                    notes.Add("Still running on " + (States.IsActive(i.State) ? na : nb));
                    w = 1;
                }
                else
                {
                    notes.Add("Different result");
                    w = 1;
                }
            }
            else
            {
                if (i.State == State.Failed && i.ExitCode != j.ExitCode)
                {
                    notes.Add("Exit code " + Code(i.ExitCode) + " on " + na + ", " + Code(j.ExitCode) + " on " + nb);
                    w = 1;
                }
                int ca = i.Attempts.Count, cb = j.Attempts.Count;
                if (ca != cb && Math.Max(ca, cb) > 1)
                {
                    notes.Add(Math.Max(ca, cb) + " attempts on " + (ca > cb ? na : nb) + ", " + Math.Min(ca, cb) + " on " + (ca > cb ? nb : na));
                    w = 1;
                }
                if (r.SecondsA.HasValue && r.SecondsB.HasValue)
                {
                    double lo = Math.Min(r.SecondsA.Value, r.SecondsB.Value), hi = Math.Max(r.SecondsA.Value, r.SecondsB.Value);
                    // A difference only counts when it is both large and long enough to notice.
                    if (hi - lo >= 60 && hi >= lo * 1.5)
                    {
                        notes.Add(Txt.DurationShort(TimeSpan.FromSeconds(hi - lo)) + " longer on " + (r.SecondsA.Value > r.SecondsB.Value ? na : nb));
                        w = 1;
                    }
                }
            }
            if (i != null && j != null && !string.IsNullOrEmpty(i.Name) && !string.IsNullOrEmpty(j.Name) && !string.Equals(i.Name, j.Name, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("Named " + j.Name + " on " + nb);
                w = Math.Max(w, 1);
            }
            r.Weight = w;
            r.Note = string.Join("; ", notes.ToArray());
            return r;
        }

        static string FindingSig(Finding f, Session s)
        {
            Item i = f.ItemKey != null ? s.Find(f.ItemKey) : null;
            return f.Kind + "|" + (i != null ? i.Kind + "|" + i.DisplayName : f.Title);
        }

        // Findings that only one side has. Failed items already have a row of their own.
        static void AddFindings(List<DiffRow> rows, Session from, Session other, string name, bool isB)
        {
            HashSet<string> there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Finding f in other.Findings) there.Add(FindingSig(f, other));
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Finding f in from.Findings)
            {
                if (f.Kind == FindingKinds.Failed || f.Kind == FindingKinds.TaskSequence) continue;
                string sig = FindingSig(f, from);
                if (there.Contains(sig) || !done.Add(sig)) continue;
                DiffRow r = new DiffRow();
                r.Name = f.Title;
                r.Kind = DiffRow.FindingKind;
                if (isB) r.StateB = f.SeverityName; else r.StateA = f.SeverityName;
                r.StateA = r.StateA ?? "";
                r.StateB = r.StateB ?? "";
                r.Note = "Finding only on " + name;
                r.Weight = 1;
                rows.Add(r);
            }
        }

        static string Code(long? c)
        {
            return c.HasValue ? Errors.Short(c) : "none";
        }

        static string FailCode(Item i)
        {
            return i.ExitCode.HasValue && i.ExitCode.Value != 0 ? ", " + Errors.Short(i.ExitCode) : "";
        }

        // From the first item's start to the last item's end, or to now while something still runs.
        public static TimeSpan? Total(Session s, DateTime now)
        {
            DateTime? first = s.FirstUtc;
            if (first == null) return null;
            DateTime end = first.Value;
            foreach (Item i in s.Items)
            {
                DateTime e = i.EndUtc ?? (States.IsFinal(i.State) || i.State == State.WaitingReboot ? i.LastUtc : States.IsActive(i.State) ? now : i.LastUtc);
                if (e > end) end = e;
            }
            return end - first.Value;
        }

        public static DiffSummary Summarize(Session a, DateTime nowA, Session b, DateTime nowB, List<DiffRow> rows)
        {
            DiffSummary m = new DiffSummary();
            m.NameA = NameA(a, b);
            m.NameB = NameB(a, b);
            m.KindA = a.Kind;
            m.KindB = b.Kind;
            m.StartA = a.FirstUtc;
            m.StartB = b.FirstUtc;
            m.TotalA = Total(a, nowA);
            m.TotalB = Total(b, nowB);
            m.ItemsA = a.Items.Count;
            m.ItemsB = b.Items.Count;
            m.DoneA = a.Count(State.Succeeded);
            m.DoneB = b.Count(State.Succeeded);
            m.FailedA = a.Count(State.Failed);
            m.FailedB = b.Count(State.Failed);
            foreach (DiffRow r in rows)
            {
                if (r.IsFinding || r.Weight == 0) continue;
                m.Differ++;
                bool fa = r.ItemA != null && r.ItemA.State == State.Failed, fb = r.ItemB != null && r.ItemB.State == State.Failed;
                if (fa && !fb) m.FailedOnlyA++;
                if (fb && !fa) m.FailedOnlyB++;
            }
            m.Sentence = Sentence(m);
            return m;
        }

        static string Sentence(DiffSummary m)
        {
            StringBuilder s = new StringBuilder();
            string longer = null;
            if (m.TotalA.HasValue && m.TotalB.HasValue)
            {
                TimeSpan d = m.TotalA.Value - m.TotalB.Value;
                if (Math.Abs(d.TotalSeconds) >= 60)
                {
                    longer = d > TimeSpan.Zero ? m.NameA : m.NameB;
                    s.Append(longer).Append(" took ").Append(Txt.DurationShort(d.Duration())).Append(" longer. ");
                }
                else s.Append("Both took about as long. ");
            }
            if (m.Differ == 0) s.Append("No item differs.");
            else
            {
                s.Append(m.Differ).Append(m.Differ == 1 ? " item differs" : " items differ");
                AppendFailed(s, m.FailedOnlyA, m.NameA, longer);
                AppendFailed(s, m.FailedOnlyB, m.NameB, longer);
                s.Append('.');
            }
            return s.ToString();
        }

        static void AppendFailed(StringBuilder s, int n, string name, string longer)
        {
            if (n == 0) return;
            s.Append(", ").Append(n).Append(" failed only ").Append(name == longer ? "there" : "on " + name);
        }

        public static string ToCsv(List<DiffRow> rows, DiffSummary m)
        {
            StringBuilder b = new StringBuilder();
            b.Append("Item;Kind;State on ").Append(Clean(m.NameA)).Append(";Seconds on ").Append(Clean(m.NameA))
             .Append(";State on ").Append(Clean(m.NameB)).Append(";Seconds on ").Append(Clean(m.NameB)).Append(";Difference;Weight\r\n");
            foreach (DiffRow r in rows)
                b.Append(Q(r.Name)).Append(';').Append(Q(r.Kind)).Append(';').Append(Q(r.StateA)).Append(';').Append(Sec(r.SecondsA)).Append(';')
                 .Append(Q(r.StateB)).Append(';').Append(Sec(r.SecondsB)).Append(';').Append(Q(r.Note)).Append(';').Append(r.Weight).Append("\r\n");
            return b.ToString();
        }

        static string Clean(string s)
        {
            return (s ?? "").Replace(";", " ").Replace("\"", "");
        }

        static string Sec(double? s)
        {
            return s.HasValue ? s.Value.ToString("0", CultureInfo.InvariantCulture) : "";
        }

        static string Q(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }
    }

    // Two plays of the Autopilot demo. The second is another device that was slower, and where one app failed.
    public static class DemoCompare
    {
        public const string ComputerB = "CONTOSO-LT0532";

        public static Session A()
        {
            return Play(Demo.Autopilot(), DateTime.UtcNow.AddHours(-2));
        }

        public static Session B()
        {
            DemoScenario sc = Demo.Autopilot();
            sc.Computer = ComputerB;
            sc.User = "jonas.berg@contoso.com";
            string m365 = IdOf(sc, "Microsoft 365 Apps for enterprise"), zip = IdOf(sc, "7-Zip 24.08");
            // Microsoft 365 Apps finishes 14 minutes later, and everything after it moves with it.
            double done = double.MaxValue;
            foreach (DemoStep s in sc.Steps) if (s.Text != null && s.Text.Contains(m365) && s.Text.Contains("lpExitCode")) done = Math.Min(done, s.At);
            foreach (DemoStep s in sc.Steps) if (s.At >= done) s.At += 14 * 60;
            // 7-Zip fails with a fatal MSI error.
            List<DemoStep> keep = new List<DemoStep>();
            foreach (DemoStep s in sc.Steps)
            {
                if (s.RegPath != null && s.RegPath.Contains(zip) && s.RegValue == 3) s.RegValue = 4;
                else if (s.Text != null && s.Text.Contains(zip))
                {
                    if (s.Text.Contains("lpExitCode 0")) { s.Text = "[Win32App][Win32AppExecutionExecutor] Admin did NOT set mapping for lpExitCode: 1603 of app: " + zip + "."; s.Type = 3; }
                    else if (s.Text.Contains("Detection for policy")) continue;
                    else if (s.Text.Contains("InProgress -> Success")) { s.Text = "[Win32App][ReportingManager] Execution state for app with id: " + zip + " has been updated. Report delta: {\"EnforcementState\":\"InProgress -> Error\",\"ErrorCode\":\"null -> 1603\"}"; s.Type = 3; }
                }
                keep.Add(s);
            }
            sc.Steps.Clear();
            sc.Steps.AddRange(keep);
            sc.Sort();
            return Play(sc, DateTime.UtcNow.AddHours(-26));
        }

        static string IdOf(DemoScenario sc, string name)
        {
            foreach (KeyValuePair<string, string> kv in sc.Names) if (kv.Value == name) return kv.Key;
            return "-";
        }

        // Plays a scenario to its end, the way the demo engine does, and returns the session.
        static Session Play(DemoScenario sc, DateTime startUtc)
        {
            DemoPlayer p = new DemoPlayer(sc, startUtc);
            Session s = new Session();
            s.Mode = Modes.Demo;
            s.AppVersion = Program.Version;
            s.Computer = sc.Computer;
            s.User = sc.User;
            s.OsVersion = sc.Os;
            s.Elevated = true;
            s.SourcePath = sc.Name;
            Tracker t = new Tracker(s);
            Batch b = p.AdvanceTo(sc.Length + 1);
            foreach (Pair i in b.Info) s.SetInfo(i.Key, i.Value);
            if (b.Names != null) t.AddNames(b.Names);
            t.ApplyAll(b.Obs);
            foreach (Pair a in b.Aliases) t.Alias(a.Key, a.Value);
            if (b.Names != null) t.AddNames(b.Names);
            foreach (string n in b.Notes) s.Notes.Add(n);
            s.ClockUtc = p.Clock;
            Finder.Run(s, p.Clock, true);
            return s;
        }
    }
}
