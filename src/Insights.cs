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
        // Every pattern gives up after this. The text comes from logs and uploads, so it can be shaped to be slow.
        static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
        const RegexOptions Ci = RegexOptions.Compiled | RegexOptions.IgnoreCase;
        // A quoted value whose closing quote was cut off, as in a shortened command line: the rest of the line.
        const string Unclosed = @"|""[^""\r\n]{1,8192}(?=[\r\n]|$)|'[^'\r\n]{1,8192}(?=[\r\n]|$)";
        // name=value, name: value, "name": "value", with the value bare or quoted.
        static readonly Regex RxPair = new Regex(@"(?<n>\b(?:" + Names + @"))(?<s>""?\s*[=:]\s*)(?<v>""[^""]{0,8192}""|'[^']{0,8192}'" + Unclosed + @"|[^\s""'&;,]+)", Ci, Limit);
        // -Password value, --token value, /password value.
        static readonly Regex RxSwitch = new Regex(@"(?<n>(?:(?<![\w-])--?|(?<![\w/])/)(?:" + Names + @"))(?<s>\s+)(?<v>""[^""]{0,8192}""|'[^']{0,8192}'" + Unclosed + @"|[^\s""'\-/][^\s]*)", Ci, Limit);
        // Authorization: Bearer eyJ..., Basic dXNlcjpwYXNz.
        static readonly Regex RxBearer = new Regex(@"(?<n>\b(?i:Bearer|Basic))(?<s>\s+)(?<v>(?=[A-Za-z0-9\-._~+/]*[0-9+/.])[A-Za-z0-9\-._~+/]{12,}=*)", RegexOptions.Compiled, Limit);
        // https://user:password@host. The parts have a length limit: without one, a long line of "a.a.a." took minutes.
        static readonly Regex RxUserInfo = new Regex(@"(?<n>\b[a-z][a-z0-9+.\-]{0,31}://[^\s/:@]{1,256})(?<s>:)(?<v>[^\s/@]{1,256})(?=@)", Ci, Limit);
        // ConvertTo-SecureString "text" -AsPlainText, also with -AsPlainText -Force before the text, and net user name password.
        static readonly Regex RxSecure = new Regex(@"(?<n>ConvertTo-SecureString(?:\s+-(?:AsPlainText|Force)(?::\$?true)?)*(?:\s+-String)?)(?<s>\s+)(?<v>""[^""]{0,8192}""|'[^']{0,8192}'|[^\s""'\-$][^\s]*)", Ci, Limit);
        // "text" | ConvertTo-SecureString -AsPlainText.
        static readonly Regex RxSecurePipe = new Regex(@"(?<v>""[^""\r\n]{1,1024}""|'[^'\r\n]{1,1024}')(?<s>\s*\|\s*)(?<n>ConvertTo-SecureString\b)", Ci, Limit);
        static readonly Regex RxNetUser = new Regex(@"(?<n>\bnet(?:1|\.exe)?\s+user\s+(?:""[^""]{1,1024}""|\S+))(?<s>\s+)(?<v>""[^""]{0,8192}""|[^\s/""*][^\s]*)", Ci, Limit);
        // net use [drive:] \\server\share password /user:name. The password is the bare word after the share.
        static readonly Regex RxNetUse = new Regex(@"(?<n>\bnet(?:1|\.exe)?\s+use\s+(?:[A-Za-z]:\s+|\*\s+)?(?:""\\\\[^""]{1,1024}""|\\\\\S+)(?:\s+/\S+)*)(?<s>\s+)(?<v>""[^""]{0,8192}""|[^\s/""*][^\s]*)", Ci, Limit);
        // schtasks /RP password, psexec -p password, cmdkey /pass:password.
        static readonly Regex RxTaskPass = new Regex(@"(?<n>(?<=\bschtasks(?:\.exe)?""?\s[^\r\n]{0,4096})(?<=\s)/R?P)(?<s>\s+)(?<v>""[^""]{0,8192}""|[^\s/""*][^\s]*)", Ci, Limit);
        static readonly Regex RxPsexecPass = new Regex(@"(?<n>(?<=\bpsexec(?:64)?(?:\.exe)?""?\s[^\r\n]{0,4096})(?<=\s)-p)(?<s>\s+)(?<v>""[^""]{0,8192}""|[^\s\-""][^\s]*)", Ci, Limit);
        static readonly Regex RxCmdkeyPass = new Regex(@"(?<n>(?<=\bcmdkey(?:\.exe)?""?\s[^\r\n]{0,4096})(?<=\s)/pass)(?<s>:)(?<v>""[^""]{0,8192}""|[^\s""]+)", Ci, Limit);
        // powershell -EncodedCommand <Base64>, and every short form PowerShell takes for it.
        static readonly Regex RxEncoded = new Regex(@"(?<n>(?<=\s)[-/](?:e|ec|en|enc|enco|encod|encode|encoded|encodedc|encodedco|encodedcom|encodedcomm|encodedcomma|encodedcomman|encodedcommand))(?<s>\s+)(?<v>[A-Za-z0-9+/]{16,}={0,2})(?![\w+/=])", Ci, Limit);
        // <Password>text</Password>, and <AdministratorPassword><Value>text</Value> or <ProductKey><Key>text</Key> as an unattend file has it.
        // A customer code, tv1: and base64url: it holds the upload link of a customer, as in TrackVice.exe /agent-install
        // /customer "tv1:...". The prefix stays, so it is still clear what was there. ShareMask uses the same pattern.
        public const string CodePattern = @"(?<![A-Za-z0-9])(?<n>tv1:)(?<s>)(?<v>[A-Za-z0-9_\-]{16,})";
        static readonly Regex RxCode = new Regex(CodePattern, Ci, Limit);
        static readonly Regex RxXml = new Regex(@"(?<n><(?<t>[\w.:-]{0,40}(?:passw(?:or)?d|pwd|secret|token|api_?key|access_?key|account_?key|license_?key|product_?key|pidkey)[\w.-]{0,40})>(?:\s*<(?:Value|Key)>)?)(?<s>)(?<v>[^<]{1,4096})(?=<)", Ci, Limit);
        // Settings about passwords and keys, such as PasswordExpirationDays, are not secrets themselves.
        static readonly Regex RxSetting = new Regex(@"(?:Enabled|Disabled|Length|Complexity|History|Age|Expiration|Expiry|Expires|ExpirationDays|Required|Policy|Count|Type|Mode|Changed|Prompt|Hint|Lifetime|Url|Uri|Path|File|Name|Id)$", Ci, Limit);
        static readonly Regex RxPlain = new Regex(@"^(?:\$\w[\w:]*|%\w+%|\$\{[^}]+\}|true|false|\$true|\$false|\$null|null|none|yes|no|\*+|<[^>]*>)$", Ci, Limit);
        public const string Hidden = "***";
        // What a text too complex to check in time becomes: it may hold a secret, so none of it is kept.
        public const string Unchecked = "*** (hidden: too long or complex to check for passwords)";

        static string Hide(Match m)
        {
            string n = m.Groups["n"].Value, v = m.Groups["v"].Value;
            string bare = v.Trim('"', '\'');
            // Only white space, such as between an element and the one inside it, is nothing to hide.
            if (bare.Trim().Length == 0 || bare == Hidden || RxPlain.IsMatch(bare)) return m.Value;
            string word = m.Groups["t"].Success ? m.Groups["t"].Value : n.TrimStart('-', '/');
            if (RxSetting.IsMatch(word)) return m.Value;
            string q = v.Length > 1 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0] ? v.Substring(0, 1) : "";
            return n + m.Groups["s"].Value + q + Hidden + q;
        }

        // The same for "text" | ConvertTo-SecureString, where the text comes first.
        static string HideBefore(Match m)
        {
            string v = m.Groups["v"].Value;
            string bare = v.Substring(1, v.Length - 2);
            if (bare.Length == 0 || bare == Hidden || RxPlain.IsMatch(bare)) return m.Value;
            return v.Substring(0, 1) + Hidden + v.Substring(0, 1) + m.Groups["s"].Value + m.Groups["n"].Value;
        }

        public static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 6) return s;
            MatchEvaluator h = Hide;
            string r;
            try
            {
                r = RxPair.Replace(s, h);
                r = RxSwitch.Replace(r, h);
                r = RxBearer.Replace(r, h);
                r = RxUserInfo.Replace(r, h);
                r = RxSecure.Replace(r, h);
                r = RxSecurePipe.Replace(r, HideBefore);
                r = RxNetUser.Replace(r, h);
                r = RxNetUse.Replace(r, h);
                r = RxTaskPass.Replace(r, h);
                r = RxPsexecPass.Replace(r, h);
                r = RxCmdkeyPass.Replace(r, h);
                r = RxEncoded.Replace(r, h);
                r = RxXml.Replace(r, h);
                if (r.IndexOf("tv1:", StringComparison.OrdinalIgnoreCase) >= 0) r = RxCode.Replace(r, h);
            }
            catch (RegexMatchTimeoutException) { return Unchecked; }
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
                // Titles carry the names of items, which come from the logs.
                string t = Mask(f.Title);
                if (t != f.Title) { f.Title = t; n++; }
                string h = Mask(f.Hint);
                if (h != f.Hint) { f.Hint = h; n++; }
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
        // Win32 app downloads: their time, and the speed where it is known.
        public int Downloads, Speeds;
        public double DownloadSeconds, DownloadMbit;

        public double AvgDownloadSeconds
        {
            get { return Downloads == 0 ? 0 : DownloadSeconds / Downloads; }
        }

        public double AvgDownloadMbit
        {
            get { return Speeds == 0 ? 0 : DownloadMbit / Speeds; }
        }

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
            if (i.PhaseKey == Phases.Restarts) return false;
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
            if (first.HasValue && last.HasValue && last.Value > first.Value) r.Seconds = OffTime.Active(s, first.Value, last.Value).TotalSeconds;
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
                    TrackVice.Downloads.Count(f, i);
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
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
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
                // The alert link is a secret: a redirect is not followed with it, and is not taken as accepted.
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    int status = (int)resp.StatusCode;
                    if (status < 200 || status > 299) throw new StoreException("The alert was not accepted (HTTP " + status + "). TrackVice does not follow a redirect with the alert address.");
                }
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

        // Lists which file in the bundle is which log on the device: one "name<TAB>device path" per line.
        public const string IndexName = "trackvice-paths.txt";
        public const long MaxUnpackedBytes = 1024L * 1024 * 1024;

        // Unpacks a bundle into a folder and returns how many files it held.
        public static int Unpack(byte[] zip, string folder)
        {
            return Unpack(zip, folder, null);
        }

        // Same, and fills written with each entry's name in the zip and the file it was written to.
        // The zip comes from the storage, where anyone with the upload link can write, so nothing in it is trusted:
        // not its names, not its sizes and not how many files it says it holds.
        public static int Unpack(byte[] zip, string folder, Dictionary<string, string> written)
        {
            if (zip == null || zip.Length == 0) throw new InvalidDataException("The log bundle is empty.");
            Directory.CreateDirectory(folder);
            int n = 0;
            long total = 0;
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (MemoryStream ms = new MemoryStream(zip))
            using (ZipArchive a = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                // Checked before anything is written: a bundle of the agent holds some tens of files.
                if (a.Entries.Count > MaxEntries) throw new InvalidDataException("The log bundle holds more than " + MaxEntries + " files. It was not unpacked.");
                long claimed = 0;
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    if (e.Length < 0 || e.Length > MaxUnpackedBytes) throw new InvalidDataException(TooLarge);
                    claimed += e.Length;
                    if (claimed > MaxUnpackedBytes) throw new InvalidDataException(TooLarge);
                }
                // How many files took each name, so the next free "name (k)" is found without trying them all again.
                Dictionary<string, int> taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    // Only the last part of the name is used, so an entry cannot write outside the folder.
                    string name = SafeName(e.FullName);
                    if (name == null) continue;
                    string dest = Path.GetFullPath(Path.Combine(root, name));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    // Two entries with the same name both stay, the second as "name (2)".
                    int k;
                    if (!taken.TryGetValue(name, out k)) k = 1;
                    string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
                    while (true)
                    {
                        dest = Path.Combine(root, k == 1 ? name : stem + " (" + k + ")" + ext);
                        k++;
                        if (!File.Exists(dest)) break;
                    }
                    taken[name] = k;
                    using (Stream src = e.Open())
                    using (FileStream dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                    {
                        byte[] buf = new byte[81920];
                        int r;
                        while ((r = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += r;
                            if (total > MaxUnpackedBytes) throw new InvalidDataException(TooLarge);
                            dst.Write(buf, 0, r);
                        }
                    }
                    if (written != null && !written.ContainsKey(e.FullName)) written[e.FullName] = dest;
                    n++;
                }
            }
            return n;
        }

        const string TooLarge = "The log bundle is larger than 1 GB when unpacked. It was not unpacked.";
        // More files than this in one bundle and it is not unpacked.
        public const int MaxEntries = 2000;
        // The longest file name that is written. A longer one is cut, and keeps its extension.
        public const int MaxNameChars = 120;
        // What a bundle holds: logs, text, the session and the cab. A file of any other type, such as a program, a script
        // or a shortcut, is written with ".txt" after its name, so nothing in the folder runs when it is opened there.
        static readonly string[] KnownTypes = new string[] { ".log", ".lo_", ".txt", ".json", ".cab", ".csv", ".evtx", ".etl" };

        // CON, NUL, COM1 and the like are devices in every folder, with or without an extension.
        static bool IsDevice(string name)
        {
            int dot = name.IndexOf('.');
            string stem = (dot >= 0 ? name.Substring(0, dot) : name).Trim().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" || stem == "CLOCK$") return true;
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
                return (stem[3] >= '0' && stem[3] <= '9') || stem[3] == '\u00b9' || stem[3] == '\u00b2' || stem[3] == '\u00b3';
            return false;
        }

        // The file name part of a zip entry, cut at both kinds of slash, without a drive or characters Windows does not allow.
        // It is never the name of a device, never longer than MaxNameChars, and never of a type that Windows runs.
        public static string SafeName(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return null;
            string name = entry;
            int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (cut >= 0) name = name.Substring(cut + 1);
            cut = name.LastIndexOf(':');
            if (cut >= 0) name = name.Substring(cut + 1);
            StringBuilder b = new StringBuilder();
            // Format characters, such as the one that writes right to left, would show a name other than the real one.
            foreach (char c in name) b.Append(c < 32 || c == 127 || "<>\"|?*".IndexOf(c) >= 0 || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? '_' : c);
            name = b.ToString().Trim().TrimEnd('.', ' ');
            if (name.Length == 0) return null;
            if (IsDevice(name)) name = "_" + name;
            int dot = name.LastIndexOf('.');
            string ext = dot > 0 ? name.Substring(dot) : "";
            if (dot == 0 || (ext.Length > 0 && Array.IndexOf(KnownTypes, ext.ToLowerInvariant()) < 0))
            {
                name += ".txt";
                ext = ".txt";
            }
            if (name.Length > MaxNameChars)
            {
                string stem = name.Substring(0, MaxNameChars - ext.Length).TrimEnd('.', ' ');
                name = (stem.Length > 0 ? stem : "_") + ext;
            }
            return name;
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
