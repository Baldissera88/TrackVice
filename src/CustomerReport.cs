namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net;
    using System.Text;

    // What the customer is told about the end of a deployment.
    public static class ReportResult
    {
        public const string Done = "Done", DoneWithFailures = "Done with failures", Failed = "Failed", Running = "In progress", NotFinished = "Not finished", None = "No data";

        public static string Css(string result)
        {
            switch (result)
            {
                case Done: return "ok";
                case DoneWithFailures: return "warn";
                case Failed: return "bad";
                case Running: return "run";
                default: return "idle";
            }
        }
    }

    public sealed class CustomerReportOptions
    {
        public string Customer;
        // "Prepared by <name>" in the footer.
        public string PreparedBy;
        // The TrackVice version in the footer. The version of this program when empty.
        public string Version;
        // The line at the end of the page. A batch report with a page per device has one at the end of the whole report instead.
        public bool Footer = true;
    }

    // The report of one device for the customer: a header with the result, a time line, the apps, scripts and policies
    // (failed first, then slow, then the rest), what was found, and the restarts and outages. Built from the session only,
    // always with a light theme and for A4, and every text is masked the way Copy for ticket masks it.
    public static class CustomerReport
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // An item that took longer than this, and longer than three times the middle item, is slow.
        public static readonly TimeSpan SlowAbove = TimeSpan.FromMinutes(2);
        // The most policies listed by name; the others are counted.
        public const int MaxListed = 60;

        // ---------- text ----------

        public static string H(string s)
        {
            return WebUtility.HtmlEncode(ShareMask.Text(s ?? ""));
        }

        // Escaped but not masked, for texts that are not from the device.
        static string E(string s)
        {
            return WebUtility.HtmlEncode(s ?? "");
        }

        static string Int(int v)
        {
            return v.ToString(Inv);
        }

        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            return v.ToString("0.#", Inv);
        }

        public static string Version()
        {
            object[] a = typeof(CustomerReport).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            return a.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)a[0]).InformationalVersion : typeof(CustomerReport).Assembly.GetName().Version.ToString();
        }

        public static string When(DateTime? utc)
        {
            return utc.HasValue ? utc.Value.ToLocalTime().ToString("d MMM yyyy HH:mm", Inv) : "";
        }

        public static string Day(DateTime utc)
        {
            return utc.ToLocalTime().ToString("d MMMM yyyy", Inv);
        }

        static string Clock(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm", Inv);
        }

        static string Zone(DateTime utc)
        {
            TimeSpan o = TimeZoneInfo.Local.GetUtcOffset(utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc);
            return "UTC" + (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString("hh\\:mm", Inv);
        }

        // ---------- what happened ----------

        static bool Waiting(Item i)
        {
            return !i.Expected && (i.State == State.Pending || i.State == State.Unknown);
        }

        // The last activity of the deployment, or now while something still runs.
        public static DateTime End(Session s, DateTime nowUtc)
        {
            DateTime? first = s.FirstUtc;
            DateTime end = first ?? nowUtc;
            bool open = false;
            foreach (Item i in s.Items)
            {
                if (i.Expected) continue;
                if (States.IsActive(i.State) || Waiting(i)) open = true;
                DateTime e = GanttLayout.BarEnd(i, nowUtc);
                if (e > end) end = e;
            }
            if (open && nowUtc > end) end = nowUtc;
            return end;
        }

        // The device could not be set up: the task sequence, or the steps the user waits for, failed.
        static bool CoreFailed(Session s)
        {
            string ts = s.GetInfo("Task sequence result");
            if (ts != null && ts.StartsWith("Failed", StringComparison.Ordinal)) return true;
            foreach (Item i in s.Items)
            {
                if (i.State != State.Failed) continue;
                string k = i.PhaseKey ?? "";
                if (k.StartsWith("ts:", StringComparison.Ordinal) || Phases.IsEsp(k) || k == "intune:autopilot") return true;
            }
            foreach (Finding f in s.Findings)
                if (f.Severity >= 2 && (f.Kind == FindingKinds.EspTimeLimit || f.Kind == FindingKinds.TaskSequence)) return true;
            return false;
        }

        public static string ResultOf(Session s)
        {
            if (s == null || s.Items.Count == 0) return ReportResult.None;
            if (CoreFailed(s)) return ReportResult.Failed;
            bool open = false;
            int failed = 0, real = 0;
            foreach (Item i in s.Items)
            {
                if (i.Expected || i.Key == SccmFeed.RunKey) continue;
                real++;
                if (States.IsActive(i.State) || Waiting(i)) open = true;
                if (i.State == State.Failed) failed++;
            }
            if (real == 0) return ReportResult.None;
            if (open) return ReportResult.Running;
            return failed > 0 ? ReportResult.DoneWithFailures : ReportResult.Done;
        }

        // How long the deployment took without the time the device was off, or null when it never started.
        public static TimeSpan? Total(Session s, DateTime nowUtc)
        {
            DateTime? first = s.FirstUtc;
            if (!first.HasValue) return null;
            DateTime end = End(s, nowUtc);
            if (end <= first.Value) return TimeSpan.Zero;
            return OffTime.Active(s, first.Value, end);
        }

        // The group of the report an item belongs to; null for what the report leaves out.
        public static string GroupOf(Item i)
        {
            if (i.Key == SccmFeed.RunKey || i.Kind == Kinds.Restart || i.PhaseKey == Phases.Restarts) return null;
            if (i.Kind == Kinds.Script) return "Scripts";
            if (i.Kind == Kinds.Policy || i.Kind == Kinds.Profile || i.Kind == Kinds.Certificate || i.PhaseKey == Phases.Policies) return "Policies";
            if (AppGroups.IsApp(i) || i.Kind == Kinds.Application || i.Kind == Kinds.Package || i.Kind == Kinds.Update || i.Kind == Kinds.Content) return "Apps";
            return "Steps";
        }

        public static readonly string[] Groups = { "Apps", "Scripts", "Policies", "Steps" };

        static double Median(List<double> l)
        {
            if (l.Count == 0) return 0;
            l.Sort();
            return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        }

        // The seconds above which an item of this group is slow.
        public static double SlowSeconds(List<Item> items, DateTime nowUtc)
        {
            List<double> l = new List<double>();
            foreach (Item i in items)
            {
                TimeSpan? d = i.Duration(nowUtc);
                if (d.HasValue && d.Value.TotalSeconds >= 1) l.Add(d.Value.TotalSeconds);
            }
            return Math.Max(SlowAbove.TotalSeconds, Median(l) * 3);
        }

        static double Seconds(Item i, DateTime nowUtc)
        {
            TimeSpan? d = i.Duration(nowUtc);
            return d.HasValue ? d.Value.TotalSeconds : 0;
        }

        // Failed items first, then the slow ones, then the rest. Inside each part the longest first.
        public static List<Item> Ordered(List<Item> items, DateTime nowUtc)
        {
            double slow = SlowSeconds(items, nowUtc);
            List<KeyValuePair<int, Item>> l = new List<KeyValuePair<int, Item>>();
            for (int k = 0; k < items.Count; k++) l.Add(new KeyValuePair<int, Item>(k, items[k]));
            l.Sort(delegate (KeyValuePair<int, Item> a, KeyValuePair<int, Item> b)
            {
                int ra = Rank(a.Value, slow, nowUtc), rb = Rank(b.Value, slow, nowUtc);
                if (ra != rb) return ra.CompareTo(rb);
                int c = Seconds(b.Value, nowUtc).CompareTo(Seconds(a.Value, nowUtc));
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            List<Item> r = new List<Item>();
            foreach (KeyValuePair<int, Item> p in l) r.Add(p.Value);
            return r;
        }

        static int Rank(Item i, double slow, DateTime nowUtc)
        {
            if (i.State == State.Failed) return 0;
            return Seconds(i, nowUtc) >= slow ? 1 : 2;
        }

        // ---------- the page ----------

        public static string Html(Session s, DateTime nowUtc, CustomerReportOptions o)
        {
            if (o == null) o = new CustomerReportOptions();
            StringBuilder b = new StringBuilder();
            Head(b, "Deployment report - " + (s.Computer ?? ""));
            Section(b, s, nowUtc, o, false, null, o.Footer);
            Tail(b);
            return b.ToString();
        }

        public static void Head(StringBuilder b, string title)
        {
            b.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">").Append(Report.CspMeta).Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
             .Append(H(title)).Append("</title><style>").Append(Css).Append("</style></head><body><main>");
        }

        public static void Tail(StringBuilder b)
        {
            b.Append("</main></body></html>");
        }

        public static void Footer(StringBuilder b, CustomerReportOptions o, DateTime nowUtc)
        {
            string name = (o.PreparedBy ?? "").Trim();
            string ver = string.IsNullOrEmpty(o.Version) ? Version() : o.Version;
            b.Append("<footer>Prepared by ").Append(E(name.Length > 0 ? name : "TrackVice")).Append(" with TrackVice ").Append(E(ver)).Append(" on ").Append(E(Day(nowUtc)))
             .Append(". Times are local time (").Append(E(Zone(nowUtc))).Append(").</footer>");
        }

        // One device. A page of its own in a batch report, where newPage is true.
        public static void Section(StringBuilder b, Session s, DateTime nowUtc, CustomerReportOptions o, bool newPage)
        {
            Section(b, s, nowUtc, o, newPage, null, false);
        }

        // id: what a link in the device table of a batch report points to. With the footer, the footer is kept on the
        // page of the last finding or of the restarts, so it never stands alone on a page of its own.
        public static void Section(StringBuilder b, Session s, DateTime nowUtc, CustomerReportOptions o, bool newPage, string id, bool footer)
        {
            string result = ResultOf(s);
            string css = ReportResult.Css(result);
            DateTime? first = s.FirstUtc;
            bool running = result == ReportResult.Running;
            TimeSpan? total = Total(s, nowUtc);
            DateTime end = End(s, nowUtc);

            b.Append("<section class=\"dev").Append(newPage ? " pb" : "").Append("\"").Append(string.IsNullOrEmpty(id) ? "" : " id=\"" + E(id) + "\"").Append("><div class=\"top\"><span class=\"brand\">Deployment report</span><span class=\"brand\">TrackVice</span></div>");
            b.Append("<div class=\"head\"><div><div class=\"cust\">").Append(E(string.IsNullOrEmpty(o.Customer) ? "Deployment report" : o.Customer)).Append("</div><h1>").Append(H(s.Computer)).Append("</h1></div>")
             .Append("<div class=\"badge ").Append(css).Append("\">").Append(E(result)).Append("</div></div>");

            string serial = s.GetInfo(DeviceFacts.Serial);
            string model = null;
            foreach (Pair p in DeviceFacts.Card(s)) if (p.Key == "Maker and model") model = p.Value;
            string profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            b.Append("<dl class=\"meta\">");
            Meta(b, "Serial number", serial);
            Meta(b, "Model", model);
            Meta(b, "User", Who.Label(s));
            Meta(b, s.GetInfo("Task sequence") != null ? "Task sequence" : "Autopilot profile", profile);
            Meta(b, "Started", When(first));
            Meta(b, running ? "Last activity" : "Finished", first.HasValue ? When(end) : "");
            Meta(b, running ? "Running for" : "Total time", total.HasValue ? Txt.Duration(total) : "");
            b.Append("</dl>");

            Summary(b, s, result, nowUtc);
            Timeline(b, s, nowUtc);
            Tables(b, s, nowUtc);
            int last = FindingsBlock(b, s);
            int ev = b.Length;
            Events(b, s, nowUtc);
            if (b.Length > ev) last = ev;
            if (footer)
            {
                if (last >= 0) b.Insert(last, "<div class=\"keep\">");
                Footer(b, o, nowUtc);
                if (last >= 0) b.Append("</div>");
            }
            b.Append("</section>");
        }

        static void Meta(StringBuilder b, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            b.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(H(value)).Append("</dd></div>");
        }

        static void Summary(StringBuilder b, Session s, string result, DateTime nowUtc)
        {
            int done = 0, failed = 0, n = 0, waiting = 0;
            foreach (Item i in s.Items)
            {
                if (GroupOf(i) == null || i.Expected) continue;
                n++;
                if (States.IsComplete(i.State)) done++;
                else if (i.State == State.Failed) failed++;
                else waiting++;
            }
            b.Append("<p class=\"lead\">");
            if (n == 0) b.Append("Nothing was found for this device.");
            else
            {
                b.Append(Int(done)).Append(" of ").Append(Int(n)).Append(n == 1 ? " item" : " items").Append(" finished");
                if (failed > 0) b.Append(", <b class=\"bad\">").Append(Int(failed)).Append(" failed</b>");
                if (waiting > 0) b.Append(", ").Append(Int(waiting)).Append(" still waiting or running");
                b.Append(".");
                if (result == ReportResult.Failed) b.Append(" The deployment did not complete.");
                else if (result == ReportResult.DoneWithFailures) b.Append(" The device is set up; the items that failed are listed below.");
                else if (result == ReportResult.Done) b.Append(" Everything was set up without errors.");
            }
            b.Append("</p>");
        }

        // ---------- time line ----------

        static string SvgCss(State st)
        {
            switch (st)
            {
                case State.Failed: return "bad";
                case State.Succeeded: return "ok";
                case State.WaitingReboot: return "warn";
                case State.Downloading: case State.Installing: case State.Detecting: return "run";
                default: return "idle";
            }
        }

        static string Px(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            return v.ToString("0.#", Inv);
        }

        static void Timeline(StringBuilder b, Session s, DateTime nowUtc)
        {
            DateTime? first = s.FirstUtc;
            if (!first.HasValue) return;
            DateTime from = first.Value, to = End(s, nowUtc);
            if (to - from < TimeSpan.FromMinutes(1)) to = from + TimeSpan.FromMinutes(1);
            double span = (to - from).TotalSeconds;
            List<Phase> phases = new List<Phase>();
            foreach (Phase p in s.OrderedPhases)
            {
                if (p.Key == Phases.Restarts) continue;
                bool any = false;
                foreach (Item i in p.Items) if (GroupOf(i) != null && !i.Expected) any = true;
                if (any && p.StartUtc.HasValue) phases.Add(p);
            }
            if (phases.Count == 0) return;
            const double trackX = 150, trackW = 480, rowH = 26, top = 22;
            double h = top + phases.Count * rowH + 8;
            b.Append("<h2>Phases</h2><svg class=\"tl\" viewBox=\"0 0 720 ").Append(Px(h)).Append("\" role=\"img\" aria-label=\"Phases of the deployment over time\">");
            b.Append("<text x=\"").Append(Px(trackX)).Append("\" y=\"12\" class=\"ax\">").Append(E(Clock(from))).Append("</text>");
            b.Append("<text x=\"").Append(Px(trackX + trackW)).Append("\" y=\"12\" class=\"ax\" text-anchor=\"end\">").Append(E(Clock(to))).Append("</text>");
            b.Append("<line x1=\"").Append(Px(trackX)).Append("\" x2=\"").Append(Px(trackX)).Append("\" y1=\"").Append(Px(top - 4)).Append("\" y2=\"").Append(Px(h - 4)).Append("\" class=\"gl\"/>");
            for (int r = 0; r < phases.Count; r++)
                b.Append("<rect x=\"").Append(Px(trackX)).Append("\" y=\"").Append(Px(top + r * rowH + 3)).Append("\" width=\"").Append(Px(trackW)).Append("\" height=\"16\" rx=\"4\" class=\"trk\"/>");
            b.Append("<line x1=\"").Append(Px(trackX + trackW)).Append("\" x2=\"").Append(Px(trackX + trackW)).Append("\" y1=\"").Append(Px(top - 4)).Append("\" y2=\"").Append(Px(h - 4)).Append("\" class=\"gl\"/>");
            foreach (GanttTick k in GanttLayout.RestartMarks(s, TimeZoneInfo.Local))
            {
                if (k.Utc < from || k.Utc > to) continue;
                double x = trackX + (k.Utc - from).TotalSeconds / span * trackW;
                b.Append("<line x1=\"").Append(Px(x)).Append("\" x2=\"").Append(Px(x)).Append("\" y1=\"").Append(Px(top - 4)).Append("\" y2=\"").Append(Px(h - 4)).Append("\" class=\"rs\"/>");
            }
            for (int r = 0; r < phases.Count; r++)
            {
                Phase p = phases[r];
                double y = top + r * rowH;
                DateTime ps = p.StartUtc.Value, pe = p.EndUtc ?? nowUtc;
                if (pe < ps) pe = ps;
                if (pe > to) pe = to;
                if (ps < from) ps = from;
                double x1 = trackX + (ps - from).TotalSeconds / span * trackW;
                double w = Math.Max(3, (pe - ps).TotalSeconds / span * trackW);
                if (x1 + w > trackX + trackW) w = trackX + trackW - x1;
                string dur = Txt.DurationShort(p.Duration(nowUtc));
                b.Append("<text x=\"0\" y=\"").Append(Px(y + 14)).Append("\" class=\"lb\">").Append(H(Txt.Short(p.Name, 26))).Append("</text>");
                b.Append("<rect x=\"").Append(Px(x1)).Append("\" y=\"").Append(Px(y + 3)).Append("\" width=\"").Append(Px(w)).Append("\" height=\"16\" rx=\"4\" class=\"bar ").Append(SvgCss(p.State)).Append("\"/>");
                b.Append("<text x=\"720\" y=\"").Append(Px(y + 15)).Append("\" class=\"dur\" text-anchor=\"end\">").Append(E(dur)).Append("</text>");
            }
            b.Append("</svg>");
            if (GanttLayout.RestartMarks(s, TimeZoneInfo.Local).Count > 0) b.Append("<p class=\"note\">Vertical amber lines are restarts of the device.</p>");
        }

        // ---------- tables ----------

        static void Tables(StringBuilder b, Session s, DateTime nowUtc)
        {
            Dictionary<string, List<Item>> by = new Dictionary<string, List<Item>>();
            foreach (string g in Groups) by[g] = new List<Item>();
            foreach (Item i in s.Items)
            {
                string g = GroupOf(i);
                if (g == null || i.Expected) continue;
                by[g].Add(i);
            }
            foreach (string g in Groups)
            {
                List<Item> items = by[g];
                if (items.Count == 0) continue;
                int bad = 0, ok = 0;
                foreach (Item i in items) { if (i.State == State.Failed) bad++; else if (States.IsComplete(i.State)) ok++; }
                b.Append("<h2>").Append(E(g)).Append("<span class=\"cnt\">").Append(Int(ok)).Append(" of ").Append(Int(items.Count)).Append(" done");
                if (bad > 0) b.Append(", <b class=\"bad\">").Append(Int(bad)).Append(" failed</b>");
                b.Append("</span></h2>");
                List<Item> ordered = Ordered(items, nowUtc);
                if (g == "Policies")
                {
                    // Policies arrive in an instant: only the ones that did not work are of interest.
                    List<Item> shown = ordered.FindAll(delegate (Item i) { return i.State == State.Failed; });
                    if (shown.Count == 0)
                    {
                        b.Append("<p class=\"note\">All ").Append(Int(items.Count)).Append(items.Count == 1 ? " policy was" : " policies were").Append(" applied").Append(bad == 0 ? " without errors." : ".").Append("</p>");
                        continue;
                    }
                    Table(b, shown, nowUtc, MaxListed, SlowSeconds(items, nowUtc), null);
                    if (items.Count > shown.Count) b.Append("<p class=\"note\">The other ").Append(Int(items.Count - shown.Count)).Append(" policies were applied.</p>");
                    continue;
                }
                Table(b, ordered, nowUtc, 200, SlowSeconds(items, nowUtc), g == "Steps" ? s : null);
            }
        }

        // With a session the phase of each item is named after it, since steps of two phases can have the same name.
        static void Table(StringBuilder b, List<Item> items, DateTime nowUtc, int max, double slow, Session phasesOf)
        {
            b.Append("<table class=\"fx\"><colgroup><col><col style=\"width:120px\"><col style=\"width:84px\"><col style=\"width:116px\"></colgroup><thead><tr><th>Name</th><th class=\"st\">State</th><th class=\"num\">Attempts</th><th class=\"num\">Duration</th></tr></thead><tbody>");
            int n = 0;
            foreach (Item i in items)
            {
                if (n++ >= max) break;
                State st = i.State;
                bool isSlow = st != State.Failed && Seconds(i, nowUtc) >= slow;
                b.Append("<tr><td>").Append(H(i.DisplayName));
                Phase ph = phasesOf != null && i.PhaseKey != null ? phasesOf.FindPhase(i.PhaseKey) : null;
                if (ph != null && !string.IsNullOrEmpty(ph.Name) && !string.Equals(ph.Name, i.DisplayName, StringComparison.OrdinalIgnoreCase)) b.Append("<span class=\"ph\">").Append(H(ph.Name)).Append("</span>");
                b.Append("</td><td class=\"st ").Append(SvgCss(st)).Append("\">").Append(H(AppGroups.StateLabel(i))).Append("</td><td class=\"num\">")
                 .Append(i.Attempts.Count > 0 ? Int(i.Attempts.Count) : "").Append("</td><td class=\"num").Append(isSlow ? " warn" : "").Append("\">").Append(H(Txt.Duration(i.Duration(nowUtc)))).Append("</td></tr>");
                if (st == State.Failed)
                {
                    string why = null;
                    if (i.ExitCode.HasValue && i.ExitCode.Value != 0) why = Errors.Describe(i.ExitCode.Value);
                    if (!string.IsNullOrEmpty(why)) b.Append("<tr class=\"why\"><td colspan=\"4\">").Append(H(why)).Append("</td></tr>");
                }
            }
            b.Append("</tbody></table>");
            if (items.Count > max) b.Append("<p class=\"note\">").Append(Int(items.Count - max)).Append(" more are not listed.</p>");
        }

        // ---------- findings, restarts and outages ----------

        // Returns where the last finding starts in the text, or -1 when it is not the end of the block.
        static int FindingsBlock(StringBuilder b, Session s)
        {
            if (s.Findings.Count == 0) return -1;
            int last = -1;
            List<Finding> l = new List<Finding>(s.Findings);
            l.Sort(delegate (Finding x, Finding y) { return y.Severity.CompareTo(x.Severity); });
            b.Append("<h2>What we found</h2>");
            int n = 0;
            foreach (Finding f in l)
            {
                if (n++ >= 12) break;
                string c = f.Severity >= 2 ? "bad" : f.Severity == 1 ? "warn" : "idle";
                last = b.Length;
                b.Append("<div class=\"finding ").Append(c).Append("\"><b>").Append(H(f.Title)).Append("</b>");
                if (!string.IsNullOrEmpty(f.Message)) b.Append("<p>").Append(H(f.Message)).Append("</p>");
                if (!string.IsNullOrEmpty(f.Hint)) b.Append("<p class=\"hint\">").Append(H(f.Hint)).Append("</p>");
                b.Append("</div>");
            }
            if (l.Count > 12) b.Append("<p class=\"note\">").Append(Int(l.Count - 12)).Append(" more findings are not listed.</p>");
            return l.Count > 12 ? -1 : last;
        }

        static void Events(StringBuilder b, Session s, DateTime nowUtc)
        {
            List<string[]> rows = new List<string[]>();
            List<DateTime> at = new List<DateTime>();
            Phase rp = s.FindPhase(Phases.Restarts);
            if (rp != null)
            {
                List<Item> items = new List<Item>(rp.Items);
                items.Sort(delegate (Item x, Item y) { return GanttLayout.BarStart(x).CompareTo(GanttLayout.BarStart(y)); });
                foreach (Item i in items)
                {
                    TimeSpan? d = i.Duration(nowUtc);
                    at.Add(GanttLayout.BarStart(i));
                    rows.Add(new string[] { Clock(GanttLayout.BarStart(i)), "Restart", d.HasValue && d.Value.TotalSeconds >= 1 ? Txt.Duration(d) : "", i.DisplayName });
                }
            }
            foreach (NetOutage o in s.Outages)
            {
                at.Add(o.FromUtc);
                rows.Add(new string[] { Clock(o.FromUtc), "Network outage", Txt.Duration(o.Length), o.Reason ?? "" });
            }
            if (rows.Count == 0) return;
            // By the time they happened, also over midnight, and as found when two have the same time.
            int[] order = new int[rows.Count];
            for (int k = 0; k < order.Length; k++) order[k] = k;
            Array.Sort(order, delegate (int x, int y) { int c = at[x].CompareTo(at[y]); return c != 0 ? c : x.CompareTo(y); });
            List<string[]> sorted = new List<string[]>();
            foreach (int k in order) sorted.Add(rows[k]);
            rows = sorted;
            b.Append("<h2>Restarts and network outages</h2><table><thead><tr><th class=\"tm\">Time</th><th>What</th><th class=\"num\">Length</th><th>Details</th></tr></thead><tbody>");
            int n = 0;
            foreach (string[] r in rows)
            {
                if (n++ >= 40) break;
                b.Append("<tr><td class=\"tm\">").Append(E(r[0])).Append("</td><td>").Append(E(r[1])).Append("</td><td class=\"num\">").Append(E(r[2])).Append("</td><td>").Append(H(r[3])).Append("</td></tr>");
            }
            b.Append("</tbody></table>");
            if (rows.Count > 40) b.Append("<p class=\"note\">").Append(Int(rows.Count - 40)).Append(" more are not listed.</p>");
        }

        // ---------- style ----------

        public const string Css =
            "*{box-sizing:border-box}html{-webkit-print-color-adjust:exact;print-color-adjust:exact}" +
            "body{margin:0;background:#fff;color:#1b2230;font:13px/1.5 'Segoe UI',system-ui,-apple-system,'Helvetica Neue',Arial,sans-serif;overflow-wrap:anywhere}" +
            "main{max-width:818px;margin:0 auto;padding:28px 24px 36px}" +
            ".top{display:flex;justify-content:space-between;border-bottom:1px solid #e2e6ed;padding-bottom:8px;margin-bottom:22px}" +
            ".brand{font-size:10.5px;font-weight:600;letter-spacing:.09em;text-transform:uppercase;color:#7b8494}" +
            ".head{display:flex;justify-content:space-between;align-items:flex-start;gap:20px}" +
            ".cust{font-size:30px;line-height:1.15;font-weight:700;color:#14233f;letter-spacing:-.01em}" +
            "h1{font-size:19px;font-weight:600;margin:4px 0 0;color:#394559}" +
            ".badge{flex:none;border-radius:12px;padding:12px 22px;font-size:22px;font-weight:700;line-height:1.1;text-align:center;white-space:nowrap}" +
            ".badge.ok{background:#e4f4ec;color:#146c43;border:1.5px solid #a9dcc3}.badge.warn{background:#fdf1d9;color:#8a5a00;border:1.5px solid #ecca7e}" +
            ".badge.bad{background:#fbe6e6;color:#a42424;border:1.5px solid #eba9a9}.badge.run{background:#e5eefb;color:#1d4f9c;border:1.5px solid #adc6ec}.badge.idle{background:#eef0f3;color:#5c6574;border:1.5px solid #d3d8df}" +
            ".meta{display:grid;grid-template-columns:repeat(3,1fr);gap:12px 20px;margin:22px 0 0;padding:14px 16px;background:#f6f8fb;border:1px solid #e5e9f0;border-radius:10px}" +
            ".meta div{min-width:0}.meta dt{font-size:10.5px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:#7b8494}.meta dd{margin:1px 0 0;font-size:13px;font-weight:600;color:#1b2230}" +
            ".lead{font-size:14px;margin:18px 0 0;color:#2c3648}" +
            "h2{font-size:14px;font-weight:700;margin:26px 0 8px;color:#14233f;break-after:avoid}h2 .cnt{font-size:12px;font-weight:400;color:#6b7380;margin-left:10px}" +
            "table{border-collapse:collapse;width:100%;font-size:12.5px}th{text-align:left;font-size:10.5px;font-weight:600;letter-spacing:.05em;text-transform:uppercase;color:#7b8494;padding:6px 10px;border-bottom:1.5px solid #d9dee6}" +
            "table.fx{table-layout:fixed}td{padding:6px 10px;border-bottom:1px solid #edf0f4;vertical-align:top}tr{break-inside:avoid}thead{display:table-header-group}" +
            ".num{text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}.st{white-space:nowrap;font-weight:600}.tm{white-space:nowrap;width:70px}" +
            ".ok{color:#146c43}.warn{color:#8a5a00}.bad{color:#a42424}.run{color:#1d4f9c}.idle{color:#6b7380}" +
            "tr.why td{background:#fdf4f4;color:#7a2b2b;font-size:12px;padding:3px 10px 6px 22px}" +
            ".ph{color:#7b8494;font-size:11.5px;margin-left:8px}a{color:inherit;text-decoration:none}" +
            ".note{font-size:11.5px;color:#6b7380;margin:6px 0 0}" +
            ".finding{border-left:4px solid #b9c0cb;background:#f6f8fb;border-radius:0 8px 8px 0;padding:8px 14px;margin:0 0 8px;break-inside:avoid}.finding.bad{border-color:#d24a4a;background:#fdf4f4}.finding.warn{border-color:#d9a21b;background:#fffaf0}" +
            ".finding p{margin:3px 0 0;color:#2c3648}.finding p.hint{color:#5c6574}" +
            "svg.tl{display:block;width:100%;height:auto;font-family:inherit}.tl text{font-size:11.5px;fill:#2c3648}.tl .ax{fill:#6b7380;font-size:11px}.tl .dur{fill:#2c3648;font-size:11.5px;font-weight:600}.tl .gl{stroke:#e2e6ed}.tl .trk{fill:#f1f3f7}.tl .rs{stroke:#d9a21b;stroke-width:1.5;stroke-dasharray:3 3}" +
            ".tl .bar.ok{fill:#3aa575}.tl .bar.bad{fill:#d24a4a}.tl .bar.warn{fill:#e0a526}.tl .bar.run{fill:#4b86d6}.tl .bar.idle{fill:#b9c0cb}" +
            ".keep{break-inside:avoid}footer{break-before:avoid;break-inside:avoid;margin-top:34px;padding-top:10px;border-top:1px solid #e2e6ed;font-size:11px;color:#7b8494}" +
            ".kpis{display:grid;grid-template-columns:repeat(3,1fr);gap:12px;margin:22px 0 0}.kpi{border:1px solid #e5e9f0;background:#f6f8fb;border-radius:10px;padding:12px 16px;break-inside:avoid}" +
            ".kpi b{display:block;font-size:26px;line-height:1.2;font-weight:700;color:#14233f;font-variant-numeric:tabular-nums}.kpi.ok b{color:#146c43}.kpi.bad b{color:#a42424}.kpi span{font-size:11.5px;color:#6b7380}.kpi small{display:block;font-size:11px;color:#6b7380;margin-top:2px}" +
            "svg.days{display:block;width:100%;height:auto;font-family:inherit}.days text{font-size:11px;fill:#6b7380}.days .gl{stroke:#e8ebf0}.days .ok{fill:#3aa575}.days .warn{fill:#e0a526}.days .bad{fill:#d24a4a}.days .run{fill:#4b86d6}.days .idle{fill:#b9c0cb}" +
            ".legend{display:flex;flex-wrap:wrap;gap:4px 16px;font-size:11.5px;color:#6b7380;margin:4px 0 0}.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px;vertical-align:-1px}" +
            "i.ok{background:#3aa575}i.warn{background:#e0a526}i.bad{background:#d24a4a}i.run{background:#4b86d6}i.idle{background:#b9c0cb}" +
            "@page{size:A4;margin:14mm}" +
            "@media print{main{max-width:none;padding:0}.pb{break-before:page}h2{break-after:avoid}}" +
            "@media screen{.pb{margin-top:44px;padding-top:28px;border-top:1px dashed #cfd5de}}";
    }
}
