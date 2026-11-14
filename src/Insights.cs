namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;

    // ---------- secrets ----------

    // Hides passwords, keys and tokens in command lines, script output and log lines before a session leaves the device.
    public static class Secrets
    {
        const string Names = @"\w*passw(?:or)?d\w*|\w*passwd|\w*pwd|\w*secret\w*|\w*token|\w*api_?key|access_?key|account_?key|shared_?access_?key|license_?key|product_?key|pidkey|serial_?key|client_?secret|sig";
        // name=value, name: value, "name": "value", with the value bare or quoted.
        static readonly Regex RxPair = new Regex(@"(?<n>\b(?:" + Names + @"))(?<s>""?\s*[=:]\s*)(?<v>""[^""]*""|'[^']*'|[^\s""'&;,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // -Password value, --token value, /password value.
        static readonly Regex RxSwitch = new Regex(@"(?<n>(?:(?<![\w-])--?|(?<![\w/])/)(?:" + Names + @"))(?<s>\s+)(?<v>""[^""]*""|'[^']*'|[^\s""'\-/][^\s]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Authorization: Bearer eyJ..., Basic dXNlcjpwYXNz.
        static readonly Regex RxBearer = new Regex(@"(?<n>\b(?i:Bearer|Basic))(?<s>\s+)(?<v>(?=[A-Za-z0-9\-._~+/]*[0-9+/.])[A-Za-z0-9\-._~+/]{12,}=*)", RegexOptions.Compiled);
        // https://user:password@host
        static readonly Regex RxUserInfo = new Regex(@"(?<n>\b[a-z][a-z0-9+.\-]*://[^\s/:@]+)(?<s>:)(?<v>[^\s/@]+)(?=@)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // ConvertTo-SecureString "text" -AsPlainText, and net user name password.
        static readonly Regex RxSecure = new Regex(@"(?<n>ConvertTo-SecureString(?:\s+-String)?)(?<s>\s+)(?<v>""[^""]*""|'[^']*'|[^\s""'\-$][^\s]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxNetUser = new Regex(@"(?<n>\bnet(?:1|\.exe)?\s+user\s+(?:""[^""]+""|\S+))(?<s>\s+)(?<v>""[^""]*""|[^\s/""*][^\s]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Settings about passwords and keys, such as PasswordExpirationDays, are not secrets themselves.
        static readonly Regex RxSetting = new Regex(@"(?:Enabled|Disabled|Length|Complexity|History|Age|Expiration|Expiry|Expires|ExpirationDays|Required|Policy|Count|Type|Mode|Changed|Prompt|Hint|Lifetime|Url|Uri|Path|File|Name|Id)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxPlain = new Regex(@"^(?:\$\w[\w:]*|%\w+%|\$\{[^}]+\}|true|false|\$true|\$false|\$null|null|none|yes|no|\*+|<[^>]*>)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public const string Hidden = "***";

        static string Hide(Match m)
        {
            string n = m.Groups["n"].Value, v = m.Groups["v"].Value;
            string bare = v.Trim('"', '\'');
            if (bare.Length == 0 || bare == Hidden || RxPlain.IsMatch(bare)) return m.Value;
            string word = n.TrimStart('-', '/');
            if (RxSetting.IsMatch(word)) return m.Value;
            string q = v.Length > 1 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0] ? v.Substring(0, 1) : "";
            return n + m.Groups["s"].Value + q + Hidden + q;
        }

        public static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 6) return s;
            MatchEvaluator h = Hide;
            string r = RxPair.Replace(s, h);
            r = RxSwitch.Replace(r, h);
            r = RxBearer.Replace(r, h);
            r = RxUserInfo.Replace(r, h);
            r = RxSecure.Replace(r, h);
            r = RxNetUser.Replace(r, h);
            return r == s ? s : r;
        }

        static int MaskPairs(List<Pair> list)
        {
            int n = 0;
            if (list == null) return 0;
            foreach (Pair p in list)
            {
                string m = Mask(p.Value);
                if (m != p.Value) { p.Value = m; n++; }
            }
            return n;
        }

        static int MaskEvidence(Evidence e)
        {
            if (e == null) return 0;
            string x = Mask(e.Text);
            if (x == e.Text) return 0;
            e.Text = x;
            return 1;
        }

        // Masks every text of a session in place. Returns how many texts changed.
        public static int MaskSession(Session s)
        {
            int n = MaskPairs(s.Info);
            for (int k = 0; k < s.Notes.Count; k++)
            {
                string m = Mask(s.Notes[k]);
                if (m != s.Notes[k]) { s.Notes[k] = m; n++; }
            }
            foreach (Item i in s.Items)
            {
                n += MaskPairs(i.Info);
                foreach (Attempt a in i.Attempts)
                {
                    string c = Mask(a.Command);
                    if (c != a.Command) { a.Command = c; n++; }
                }
            }
            foreach (TEvent e in s.Events)
            {
                string t = Mask(e.Text);
                if (t != e.Text) { e.Text = t; n++; }
                n += MaskEvidence(e.Evidence);
            }
            foreach (Finding f in s.Findings)
            {
                string m = Mask(f.Message);
                if (m != f.Message) { f.Message = m; n++; }
                n += MaskEvidence(f.Evidence);
            }
            return n;
        }

        // A masked copy, so the session the agent keeps reading stays as it is.
        public static Session MaskedCopy(Session s)
        {
            Session c = Session.FromJson(s.ToJson());
            MaskSession(c);
            return c;
        }
    }

    // ---------- statistics over many enrollments ----------

    // One app, script or step, over every enrollment it was part of.
    public sealed class FleetItem
    {
        public string Name, Kind;
        public int Devices, Done, Failed, Running;
        public double TotalSeconds, MaxSeconds;
        public int Timed;
        public string SlowestOn, FailedOn;

        public double AvgSeconds
        {
            get { return Timed == 0 ? 0 : TotalSeconds / Timed; }
        }

        // Out of the devices where it has finished, so devices still working on it do not lower the rate.
        public double FailRate
        {
            get { return Done + Failed == 0 ? 0 : Failed * 100.0 / (Done + Failed); }
        }
    }

    // One enrollment.
    public sealed class FleetRun
    {
        public string Computer, Kind, Profile, Slowest, Key;
        public DateTime? StartUtc, EndUtc;
        public int Items, Done, Failed, Running, Waiting;
        public double Seconds, SlowestSeconds;

        // Nothing runs and nothing waits any more.
        public bool Finished
        {
            get { return Items > 0 && Running == 0 && Waiting == 0 && Seconds > 0; }
        }
    }

    public sealed class FleetStats
    {
        public readonly List<FleetItem> Items = new List<FleetItem>();
        public readonly List<FleetRun> Runs = new List<FleetRun>();
        public double AvgSeconds, MedianSeconds, LongestSeconds;
        public int Finished, WithFailures;

        // Policies and profiles arrive in an instant and say nothing about time, so they are left out.
        static bool Timed(Item i)
        {
            return i.PhaseKey != Phases.Policies && i.Kind != Kinds.Policy && i.Kind != Kinds.Profile && i.Key != SccmFeed.RunKey;
        }

        // Only items that ran to an end count towards the times. Skipped and not applicable ones did not run.
        static bool Ran(Item i)
        {
            State st = i.State;
            return st == State.Succeeded || st == State.Failed || (st == State.WaitingReboot && i.EndUtc.HasValue);
        }

        public static FleetRun RunOf(Session s, DateTime nowUtc)
        {
            FleetRun r = new FleetRun();
            r.Computer = s.Computer;
            r.Kind = s.Kind;
            r.Profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            DateTime? first = null, last = null;
            foreach (Item i in s.Items)
            {
                if (!Timed(i)) continue;
                r.Items++;
                if (States.IsComplete(i.State)) r.Done++;
                if (i.State == State.Failed) r.Failed++;
                if (States.IsActive(i.State)) r.Running++;
                if (i.State == State.Pending) r.Waiting++;
                DateTime a = i.StartUtc ?? i.FirstUtc;
                DateTime b = i.EndUtc ?? (States.IsFinal(i.State) ? i.LastUtc : (s.ClockUtc.Year > 2000 ? s.ClockUtc : nowUtc));
                if (a.Year < 2001) continue;
                if (first == null || a < first.Value) first = a;
                if (last == null || b > last.Value) last = b;
                TimeSpan? d = i.Duration(b);
                if (d.HasValue && d.Value.TotalSeconds > r.SlowestSeconds)
                {
                    r.SlowestSeconds = d.Value.TotalSeconds;
                    r.Slowest = i.DisplayName;
                }
            }
            r.StartUtc = first;
            r.EndUtc = last;
            if (first.HasValue && last.HasValue && last.Value > first.Value) r.Seconds = (last.Value - first.Value).TotalSeconds;
            return r;
        }

        public static FleetStats Build(IEnumerable<Session> sessions, DateTime nowUtc)
        {
            return Build(sessions, null, nowUtc);
        }

        // keys, when given, holds the upload name of each session in the same order, kept on its run.
        public static FleetStats Build(IEnumerable<Session> sessions, IList<string> keys, DateTime nowUtc)
        {
            FleetStats st = new FleetStats();
            Dictionary<string, FleetItem> byName = new Dictionary<string, FleetItem>(StringComparer.OrdinalIgnoreCase);
            List<double> totals = new List<double>();
            int n = 0;
            foreach (Session s in sessions)
            {
                if (s == null) { n++; continue; }
                FleetRun run = RunOf(s, nowUtc);
                if (keys != null && n < keys.Count) run.Key = keys[n];
                n++;
                st.Runs.Add(run);
                if (run.Failed > 0) st.WithFailures++;
                if (run.Finished)
                {
                    st.Finished++;
                    totals.Add(run.Seconds);
                }
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Item i in s.Items)
                {
                    if (!Timed(i)) continue;
                    string key = i.Kind + "|" + i.DisplayName;
                    if (!seen.Add(key)) continue;
                    FleetItem f;
                    if (!byName.TryGetValue(key, out f))
                    {
                        f = new FleetItem();
                        f.Name = i.DisplayName;
                        f.Kind = i.Kind;
                        byName[key] = f;
                        st.Items.Add(f);
                    }
                    f.Devices++;
                    if (i.State == State.Failed)
                    {
                        f.Failed++;
                        if (f.FailedOn == null) f.FailedOn = s.Computer;
                    }
                    else if (States.IsComplete(i.State)) f.Done++;
                    else if (States.IsActive(i.State)) f.Running++;
                    if (!Ran(i)) continue;
                    TimeSpan? d = i.Duration(nowUtc);
                    if (!d.HasValue) continue;
                    f.Timed++;
                    f.TotalSeconds += d.Value.TotalSeconds;
                    if (f.SlowestOn == null || d.Value.TotalSeconds > f.MaxSeconds)
                    {
                        f.MaxSeconds = d.Value.TotalSeconds;
                        f.SlowestOn = s.Computer;
                    }
                }
            }
            st.Items.Sort(delegate (FleetItem a, FleetItem b)
            {
                int c = b.AvgSeconds.CompareTo(a.AvgSeconds);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            st.Runs.Sort(delegate (FleetRun a, FleetRun b) { return (b.StartUtc ?? DateTime.MinValue).CompareTo(a.StartUtc ?? DateTime.MinValue); });
            if (totals.Count > 0)
            {
                totals.Sort();
                double sum = 0;
                foreach (double t in totals) sum += t;
                st.AvgSeconds = sum / totals.Count;
                st.MedianSeconds = totals.Count % 2 == 1 ? totals[totals.Count / 2] : (totals[totals.Count / 2 - 1] + totals[totals.Count / 2]) / 2;
                st.LongestSeconds = totals[totals.Count - 1];
            }
            return st;
        }

        // The apps, scripts and steps, one per line.
        public string ToCsv()
        {
            StringBuilder b = new StringBuilder("Name,Kind,Devices,Done,Failed,Fail rate %,Average s,Longest s,Slowest on\r\n");
            foreach (FleetItem f in Items)
                b.Append(Q(f.Name)).Append(',').Append(Q(f.Kind)).Append(',').Append(f.Devices).Append(',').Append(f.Done).Append(',').Append(f.Failed).Append(',')
                    .Append(N(f.FailRate, "0.#")).Append(',').Append(N(f.AvgSeconds, "0")).Append(',')
                    .Append(N(f.MaxSeconds, "0")).Append(',').Append(Q(f.SlowestOn)).Append("\r\n");
            return b.ToString();
        }

        // The enrollments, one per line. Times are UTC.
        public string RunsToCsv()
        {
            StringBuilder b = new StringBuilder("Device,Deployment,Profile,Started (UTC),Ended (UTC),Duration s,Finished,Items,Done,Failed,Slowest part,Slowest part s\r\n");
            foreach (FleetRun r in Runs)
                b.Append(Q(r.Computer)).Append(',').Append(Q(r.Kind)).Append(',').Append(Q(r.Profile)).Append(',')
                    .Append(Q(r.StartUtc.HasValue ? r.StartUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "")).Append(',')
                    .Append(Q(r.EndUtc.HasValue ? r.EndUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "")).Append(',')
                    .Append(N(r.Seconds, "0")).Append(',').Append(r.Finished ? "Yes" : "No").Append(',')
                    .Append(r.Items).Append(',').Append(r.Done).Append(',').Append(r.Failed).Append(',')
                    .Append(Q(r.Slowest)).Append(',').Append(N(r.SlowestSeconds, "0")).Append("\r\n");
            return b.ToString();
        }

        static string N(double v, string format)
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        static string Q(string s)
        {
            s = s ?? "";
            // Keep spreadsheets from running a value as a formula.
            if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }
    }

    // ---------- two enrollments side by side ----------

    public sealed class DiffRow
    {
        public string Name, Kind, StateA, StateB, Note;
        public double? SecondsA, SecondsB;
        // 0 the same, 1 worth a look, 2 the difference that matters.
        public int Weight;
    }

    public static class SessionDiff
    {
        static string Sig(Item i)
        {
            return i.Kind + "|" + i.DisplayName;
        }

        static double? Seconds(Item i, Session s)
        {
            if (i == null) return null;
            TimeSpan? d = i.Duration(s.ClockUtc.Year > 2000 ? s.ClockUtc : DateTime.UtcNow);
            return d.HasValue ? d.Value.TotalSeconds : (double?)null;
        }

        // What differs between two enrollments: items only one has, different results, and clearly different durations.
        public static List<DiffRow> Compare(Session a, Session b)
        {
            Dictionary<string, Item> inB = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (Item i in b.Items) if (!inB.ContainsKey(Sig(i))) inB[Sig(i)] = i;
            List<DiffRow> rows = new List<DiffRow>();
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Item i in a.Items)
            {
                if (!done.Add(Sig(i))) continue;
                Item j;
                inB.TryGetValue(Sig(i), out j);
                rows.Add(Row(i, j, a, b));
            }
            foreach (Item j in b.Items)
            {
                if (!done.Add(Sig(j))) continue;
                rows.Add(Row(null, j, a, b));
            }
            rows.Sort(delegate (DiffRow x, DiffRow y) { int c = y.Weight.CompareTo(x.Weight); return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); });
            return rows;
        }

        static DiffRow Row(Item i, Item j, Session a, Session b)
        {
            DiffRow r = new DiffRow();
            Item any = i ?? j;
            r.Name = any.DisplayName;
            r.Kind = any.Kind;
            r.StateA = i != null ? States.Label(i.State) : "";
            r.StateB = j != null ? States.Label(j.State) : "";
            r.SecondsA = Seconds(i, a);
            r.SecondsB = Seconds(j, b);
            if (i == null) { r.Note = "Only on " + b.Computer; r.Weight = 2; }
            else if (j == null) { r.Note = "Only on " + a.Computer; r.Weight = 2; }
            else if (i.State != j.State)
            {
                bool fail = i.State == State.Failed || j.State == State.Failed;
                r.Note = fail ? "Failed on " + (i.State == State.Failed ? a.Computer : b.Computer) + (FailCode(i.State == State.Failed ? i : j)) : "Different result";
                r.Weight = fail ? 2 : 1;
            }
            else if (r.SecondsA.HasValue && r.SecondsB.HasValue)
            {
                double lo = Math.Min(r.SecondsA.Value, r.SecondsB.Value), hi = Math.Max(r.SecondsA.Value, r.SecondsB.Value);
                // A difference only counts when it is both large and long enough to notice.
                if (hi - lo >= 60 && hi >= lo * 1.5)
                {
                    r.Note = Txt.DurationShort(TimeSpan.FromSeconds(hi - lo)) + " longer on " + (r.SecondsA.Value > r.SecondsB.Value ? a.Computer : b.Computer);
                    r.Weight = 1;
                }
            }
            return r;
        }

        static string FailCode(Item i)
        {
            return i.ExitCode.HasValue && i.ExitCode.Value != 0 ? ", " + Errors.Short(i.ExitCode) : "";
        }
    }

    // ---------- alerts ----------

    public sealed class Alert
    {
        public string Key, Title, Text, Level;
        public List<Pair> Facts = new List<Pair>();
    }

    // Decides what is worth telling someone about, each thing once.
    public sealed class AlertWatch
    {
        readonly HashSet<string> sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sawActive;

        // Failures that ended before this time are history, not news: a device enrolled long ago has plenty.
        public DateTime OlderIgnoredUtc = DateTime.MinValue;

        public AlertWatch(IEnumerable<string> alreadySent)
        {
            if (alreadySent != null) foreach (string k in alreadySent) sent.Add(k);
        }

        public IEnumerable<string> Sent
        {
            get { return sent; }
        }

        static Alert Make(Session s, string key, string level, string title, string text)
        {
            Alert a = new Alert();
            a.Key = key;
            a.Level = level;
            a.Title = title;
            a.Text = text;
            a.Facts.Add(new Pair("Device", s.Computer));
            string profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            if (profile != null) a.Facts.Add(new Pair("Deployment", profile));
            a.Facts.Add(new Pair("Progress", s.CountComplete + " of " + s.Items.Count + " done, " + s.Count(State.Failed) + " failed"));
            return a;
        }

        // Titles such as "tried 3 times" or "no progress for 12 min" change as time passes, so an item's finding is known by its kind and item.
        public static string KeyOf(Finding f)
        {
            return f.Kind + "|" + (string.IsNullOrEmpty(f.ItemKey) ? f.Title ?? "" : f.ItemKey);
        }

        // Only errors are sent: failed items, items not detected after install, a failed task sequence, the ESP time limit.
        public static bool Worthy(Finding f)
        {
            return f.Severity >= 2 && f.Kind != FindingKinds.MdmErrors && f.Kind != FindingKinds.NoUploads;
        }

        DateTime When(Session s, Finding f)
        {
            Item i = string.IsNullOrEmpty(f.ItemKey) ? null : s.Find(f.ItemKey);
            if (i != null)
            {
                DateTime t = i.EndUtc ?? i.LastUtc;
                return t > f.TimeUtc ? t : f.TimeUtc;
            }
            return f.TimeUtc;
        }

        public List<Alert> Check(Session s, bool idle)
        {
            return Check(s, idle, DateTime.UtcNow);
        }

        // The alerts that are new since the last call.
        public List<Alert> Check(Session s, bool idle, DateTime nowUtc)
        {
            List<Alert> list = new List<Alert>();
            foreach (Finding f in s.Findings)
            {
                if (!Worthy(f)) continue;
                string key = KeyOf(f);
                if (sent.Contains(key)) continue;
                // Not remembered, so an old item that fails again later is still reported.
                DateTime when = When(s, f);
                if (when.Year > 2000 && when < OlderIgnoredUtc) continue;
                sent.Add(key);
                Alert a = Make(s, key, "error", s.Computer + ": " + f.Title, f.Message);
                if (!string.IsNullOrEmpty(f.Hint)) a.Facts.Add(new Pair("What to do", f.Hint));
                list.Add(a);
            }
            if (s.CountActive > 0) sawActive = true;
            // Finished: something ran while this agent watched, and now nothing does.
            if (idle && sawActive && s.Items.Count > 0 && s.CountActive == 0)
            {
                sawActive = false;
                DateTime? first = s.FirstUtc;
                // Account setup after pre-provisioning adds items, and is a second finish worth a message.
                string key = "finished|" + (first.HasValue ? first.Value.ToString("yyyyMMddHH", CultureInfo.InvariantCulture) : "") + "|" + s.Items.Count;
                if (sent.Add(key))
                {
                    int failed = s.Count(State.Failed);
                    FleetRun run = FleetStats.RunOf(s, nowUtc);
                    Alert a = Make(s, key, failed > 0 ? "error" : "good", s.Computer + (failed > 0 ? ": finished with " + failed + " failed" : ": deployment finished"),
                        "Nothing has been running for a while." + (run.Seconds > 0 ? " It took " + Txt.DurationShort(TimeSpan.FromSeconds(run.Seconds)) + "." : "")
                        + (run.Slowest != null ? " The slowest part was " + run.Slowest + " (" + Txt.DurationShort(TimeSpan.FromSeconds(run.SlowestSeconds)) + ")." : ""));
                    list.Add(a);
                }
            }
            return list;
        }
    }

    // Posts an alert to a webhook: a Teams workflow ("When a Teams webhook request is received"), or anything that takes JSON with a text.
    public static class AlertSender
    {
        public const int TimeoutMs = 15000;
        // Teams refuses cards above about 28 KB, so long texts are cut.
        const int MaxText = 2000, MaxFact = 500;

        public static string Json(string s)
        {
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') { }
                else if (c == '\t') b.Append("\\t");
                else if (c < ' ' || c == '\u2028' || c == '\u2029') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        static string Cut(string s, int max)
        {
            if (s == null || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 3) + "...";
        }

        // An adaptive card for Teams, with the same words as plain text for everything else.
        public static string Payload(Alert a)
        {
            string color = a.Level == "error" ? "Attention" : a.Level == "warning" ? "Warning" : "Good";
            string title = Cut(a.Title, MaxFact), text = Cut(a.Text, MaxText);
            StringBuilder plain = new StringBuilder(title).Append("\n").Append(text);
            StringBuilder facts = new StringBuilder();
            foreach (Pair p in a.Facts)
            {
                if (facts.Length > 0) facts.Append(',');
                string v = Cut(p.Value, MaxFact);
                facts.Append("{\"title\":").Append(Json(p.Key)).Append(",\"value\":").Append(Json(v)).Append('}');
                plain.Append("\n").Append(p.Key).Append(": ").Append(v);
            }
            return "{\"type\":\"message\",\"text\":" + Json(plain.ToString()) + ",\"attachments\":[{\"contentType\":\"application/vnd.microsoft.card.adaptive\",\"contentUrl\":null,\"content\":{"
                + "\"$schema\":\"http://adaptivecards.io/schemas/adaptive-card.json\",\"type\":\"AdaptiveCard\",\"version\":\"1.4\",\"body\":["
                + "{\"type\":\"TextBlock\",\"size\":\"Medium\",\"weight\":\"Bolder\",\"wrap\":true,\"color\":\"" + color + "\",\"text\":" + Json(title) + "},"
                + "{\"type\":\"TextBlock\",\"wrap\":true,\"text\":" + Json(text) + "},"
                + "{\"type\":\"FactSet\",\"facts\":[" + facts + "]},"
                + "{\"type\":\"TextBlock\",\"isSubtle\":true,\"size\":\"Small\",\"wrap\":true,\"text\":" + Json("TrackVice " + Program.Version) + "}]}}]}";
        }

        static bool IsLoopback(Uri u)
        {
            return u.IsLoopback;
        }

        // Null when the address can be used. Alerts go over https; plain http only to this computer, for testing.
        public static string Problem(string url)
        {
            Uri u;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return "The alert address is not a web address.";
            if (u.Scheme == Uri.UriSchemeHttps) return null;
            if (u.Scheme == Uri.UriSchemeHttp && IsLoopback(u)) return null;
            return "The alert address must start with https://";
        }

        public static void Send(string url, Alert a)
        {
            string problem = Problem(url);
            if (problem != null) throw new StoreException(problem);
            url = url.Trim();
            HttpWebRequest req = Http.Request(url);
            if (IsLoopback(new Uri(url))) req.Proxy = null;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.KeepAlive = false;
            byte[] body = Encoding.UTF8.GetBytes(Payload(a));
            req.ContentLength = body.Length;
            try
            {
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (req.GetResponse()) { }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null) resp.Close();
                throw new StoreException("The alert was not accepted" + (resp != null ? " (HTTP " + (int)resp.StatusCode + ")" : "") + ": " + ex.Message);
            }
        }
    }

    // ---------- the device's logs ----------

    public static class LogBundle
    {
        public const string Prefix = "logs/";
        public const long MaxBytes = 60L * 1024 * 1024;

        public static string NameFor(string sessionBlob)
        {
            string id = Path.GetFileName(sessionBlob ?? "");
            int dot = id.IndexOf('.');
            if (dot > 0) id = id.Substring(0, dot);
            return Prefix + id + ".zip";
        }

        // The session and every log it read, as one zip. Null when there is nothing to send or it is too large.
        public static byte[] Make(Session s)
        {
            string zip = Path.Combine(Path.GetTempPath(), "TrackVice-logs-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Report.Bundle(s, zip);
                FileInfo f = new FileInfo(zip);
                if (!f.Exists || f.Length > MaxBytes) return null;
                return File.ReadAllBytes(zip);
            }
            finally
            {
                try { File.Delete(zip); } catch (Exception) { }
            }
        }

        // Unpacks a bundle into a folder and returns how many files it held.
        public static int Unpack(byte[] zip, string folder)
        {
            Directory.CreateDirectory(folder);
            int n = 0;
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (MemoryStream ms = new MemoryStream(zip))
            using (ZipArchive a = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    // Only the file name is used, so an entry cannot write outside the folder.
                    string dest = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(e.Name)));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream src = e.Open())
                    using (FileStream dst = File.Create(dest)) src.CopyTo(dst);
                    n++;
                }
            }
            return n;
        }
    }

    // ---------- plain words for why something failed ----------

    public static class Explain
    {
        // What a failed platform script said, for its finding.
        public static string ScriptError(Item i)
        {
            for (int k = i.Events.Count - 1; k >= 0; k--)
                if (i.Events[k].Text != null && i.Events[k].Text.StartsWith("Script error:", StringComparison.Ordinal)) return i.Events[k].Text.Substring(13).Trim();
            return null;
        }

        // A hint for a failure that the exit code alone does not explain.
        public static string Hint(Item i)
        {
            if (i.Kind == Kinds.Script)
                return "Run the script as SYSTEM on a test device to see the same error, for example with PsExec -s. The script's own output is in the events of this item.";
            if (i.Kind == Kinds.Office)
                return "Look in %windir%\\Temp and %temp% for the Office Click-to-Run setup logs, and check that the device reaches the Office CDN.";
            if (i.Kind == Kinds.MsiApp)
                return "The MSI ran in the device context without a user interface. Install it by hand with msiexec /i <file> /qn /l*v log.txt to see where it stops.";
            return null;
        }
    }
}
