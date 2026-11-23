namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;

    public sealed class ReportOptions
    {
        // Shown at the top instead of the device name.
        public string Title;
        // Shown under the title instead of the kind of deployment, the Windows version and the user.
        public string Subtitle;
        // The list of every event at the end.
        public bool IncludeTimeline = true;
        // The Policies phase in the time line and the tables. Without it, one line counts them.
        public bool IncludePolicies = true;
    }

    public static partial class Report
    {
        static string H(string s)
        {
            return WebUtility.HtmlEncode(s ?? "");
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string Css(State s)
        {
            switch (s)
            {
                case State.Failed: return "bad";
                case State.Succeeded: return "ok";
                case State.WaitingReboot: return "warn";
                case State.Downloading: case State.Installing: case State.Detecting: return "run";
                default: return "idle";
            }
        }

        // A percentage for a style attribute: always 0 to 100, never NaN, with a dot as the decimal sign.
        public static string Pct(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            v = Math.Max(0, Math.Min(100, v));
            return v.ToString("0.###", Inv);
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            return v.ToString("0.#", Inv);
        }

        static string Int(int v)
        {
            return v.ToString(Inv);
        }

        static string Day(DateTime utc)
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

        static string ToolVersion()
        {
            object[] a = typeof(Report).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            return a.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)a[0]).InformationalVersion : typeof(Report).Assembly.GetName().Version.ToString();
        }

        // "A", "A and B", "A, B and C".
        static string Join(List<string> l)
        {
            if (l.Count == 0) return "";
            if (l.Count == 1) return l[0];
            return string.Join(", ", l.GetRange(0, l.Count - 1).ToArray()) + " and " + l[l.Count - 1];
        }

        const string Style =
            ":root{color-scheme:light dark;--bg:#f3f5f8;--card:#fff;--text:#14171c;--t2:#424852;--t3:#69707b;--line:#e2e6ec;--grid:#eef1f5;--ok:#0f7b0f;--bad:#c42b1c;--warn:#9d5d00;--run:#005fb8;--okb:#4a9e3f;--badb:#d13438;--warnb:#d08a00;--runb:#2b7cd3;--idleb:#b3b9c2;--phb:#8b929c}" +
            "@media (prefers-color-scheme:dark){:root{--bg:#17191d;--card:#22252b;--text:#f3f5f8;--t2:#d1d5dc;--t3:#9ea4ae;--line:#353941;--grid:#2a2e35;--ok:#6ccb5f;--bad:#ff99a4;--warn:#fce100;--run:#60cdff;--okb:#5fb854;--badb:#f0626c;--warnb:#e0b000;--runb:#4ea8f0;--idleb:#5d636d;--phb:#7d848f}}" +
            "*{box-sizing:border-box}body{margin:0;overflow-wrap:anywhere;background:var(--bg);color:var(--text);font:14px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif}main{max-width:1180px;margin:0 auto;padding:32px 24px 40px}" +
            ".kicker{font-size:11.5px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:var(--t3)}h1{font-size:28px;line-height:1.2;margin:4px 0 0;overflow-wrap:anywhere}h2{font-size:17px;margin:32px 0 10px}" +
            ".sub{color:var(--t3);font-size:13px;margin-top:4px;overflow-wrap:anywhere}.lead{font-size:15px;line-height:1.6;max-width:900px;margin-top:18px}.lead p{margin:0 0 8px}" +
            ".chips{display:flex;flex-wrap:wrap;gap:10px;margin-top:18px}.chip{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:8px 14px;min-width:110px}.chip b{display:block;font-size:22px;line-height:1.3}.chip span{color:var(--t3);font-size:12px}" +
            ".card{background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}.tw{overflow-x:auto}table{border-collapse:collapse;width:100%}" +
            "th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);vertical-align:top;overflow-wrap:anywhere}th{color:var(--t2);font-weight:600;font-size:12.5px;overflow-wrap:normal}tbody tr:last-child td{border-bottom:0}" +
            ".nw{white-space:nowrap}.ok{color:var(--ok)}.bad{color:var(--bad)}.warn{color:var(--warn)}.run{color:var(--run)}.idle,.t3{color:var(--t3)}.mono{font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px}.small{font-size:12px}" +
            ".phase{display:flex;justify-content:space-between;align-items:baseline;gap:12px;padding:10px 12px;border-bottom:1px solid var(--line);font-weight:600}.phase span{font-weight:400;color:var(--t3);font-size:12.5px}.stack>.card{margin-bottom:12px}" +
            ".finding{padding:10px 14px;border-bottom:1px solid var(--line)}.finding:last-child{border-bottom:0}.finding p{margin:4px 0 0}.tag{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.05em;margin-right:8px}" +
            "tr.ev td{background:var(--grid);font-size:12px;padding:6px 10px 8px 22px}tr.ev div{margin:1px 0}" +
            ".legend{display:flex;flex-wrap:wrap;gap:14px;font-size:12px;color:var(--t3);margin:0 0 8px}.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px;vertical-align:-1px}" +
            ".gantt{--gn:250px}.gbody{position:relative}.grow{display:flex;align-items:center;border-top:1px solid var(--grid)}.gname{flex:0 0 var(--gn);width:var(--gn);padding:2px 10px;font-size:12.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
            ".gtrack{position:relative;flex:1 1 auto;height:22px;margin-right:16px}.ghead{background:var(--grid)}.ghead .gname{font-weight:600}.gaxis{display:flex;border-bottom:1px solid var(--line)}.gaxis .gtrack{height:26px}" +
            ".gaxis span{position:absolute;top:5px;transform:translateX(-50%);font-size:11px;color:var(--t3);white-space:nowrap}.ggrid{position:absolute;top:0;bottom:0;left:var(--gn);right:16px;pointer-events:none}.ggrid i{position:absolute;top:0;bottom:0;border-left:1px dashed var(--line)}" +
            ".gbar,.legend i{background:var(--idleb)}.gbar{position:absolute;top:6px;height:10px;border-radius:3px;min-width:3px}.gbar.ok,.legend i.ok{background:var(--okb)}.gbar.bad,.legend i.bad{background:var(--badb)}.gbar.warn,.legend i.warn{background:var(--warnb)}" +
            ".gbar.run,.legend i.run{background-color:var(--runb);background-image:repeating-linear-gradient(135deg,rgba(255,255,255,.4) 0 3px,transparent 3px 7px)}.gbar.ph{top:9px;height:4px;background:var(--phb)}" +
            ".gdur{position:absolute;top:3px;font-size:11px;font-style:normal;color:var(--t3);white-space:nowrap;padding-left:6px}.gdur.l{padding:0 6px 0 0;transform:translateX(-100%)}.gdur.in{padding:0 6px;margin-left:-4px;transform:translateX(-100%);color:var(--text);background:var(--card);border-radius:3px;top:3px}" +
            ".ggrid i.grst{border-left:1.5px solid var(--warn);z-index:1}.grst span{position:absolute;top:4px;left:4px;font-size:11px;font-style:normal;color:var(--warn);background:var(--card);border:1px solid var(--warn);border-radius:3px;padding:0 4px;white-space:nowrap}.grst.l span{left:auto;right:4px}" +
            ".hrow{display:flex;align-items:center;gap:12px;padding:6px 12px;border-top:1px solid var(--grid)}.hrow:first-child{border-top:0}.hname{flex:0 0 230px;width:230px;font-size:12.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}" +
            ".htrack{position:relative;flex:1 1 auto;height:14px}.htrack b{position:absolute;left:0;top:2px;height:10px;border-radius:3px;background:var(--runb);min-width:3px}.hval{flex:0 0 400px;font-size:12px;color:var(--t3)}" +
            ".rbar{display:inline-block;width:70px;height:6px;border-radius:3px;background:var(--grid);margin-left:8px;vertical-align:1px;position:relative;overflow:hidden}.rbar b{position:absolute;left:0;top:0;bottom:0;background:var(--badb)}" +
            ".chart{display:block;width:100%;height:auto;padding:8px 4px}.chart text{fill:var(--t3);font-size:12px;font-family:inherit}.chart .gl{stroke:var(--grid)}.chart .ax{stroke:var(--line)}.chart .trend{fill:none;stroke:var(--phb);stroke-width:1.5}.chart .ok{fill:var(--okb)}.chart .bad{fill:var(--badb)}" +
            "footer{margin-top:36px;padding-top:12px;border-top:1px solid var(--line);color:var(--t3);font-size:12px}footer p{margin:2px 0}" +
            "@media (max-width:760px){main{padding:20px 16px}.gantt{--gn:130px}.hrow{flex-wrap:wrap}.hname{flex-basis:100%;width:auto}.hval{flex-basis:100%}}" +
            "@media print{:root{--bg:#fff;--card:#fff;--text:#000;--t2:#222;--t3:#555;--line:#c8ccd2;--grid:#eef0f3;--ok:#0f7b0f;--bad:#c42b1c;--warn:#9d5d00;--run:#005fb8;--okb:#4a9e3f;--badb:#d13438;--warnb:#d08a00;--runb:#2b7cd3;--idleb:#b3b9c2;--phb:#8b929c}" +
            "*{-webkit-print-color-adjust:exact;print-color-adjust:exact}body{background:#fff;font-size:11.5px}main{max-width:none;padding:0}.card{overflow:visible;border-radius:0}.tw{overflow:visible}" +
            "tr,.finding,.grow,.hrow,.chip,.chart{break-inside:avoid}thead{display:table-header-group}h2{break-after:avoid}.keep{break-inside:avoid}.pb{break-before:page}.gantt{--gn:180px}.hname{flex-basis:170px;width:170px}.hval{flex-basis:250px}}" +
            "@page{margin:14mm}";

        static void Head(StringBuilder b, string pageTitle)
        {
            b.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            b.Append("<title>").Append(H(pageTitle)).Append("</title><style>").Append(Style).Append("</style></head><body><main>");
        }

        static void Foot(StringBuilder b, string about, DateTime nowUtc)
        {
            b.Append("<footer><p>").Append(H(about)).Append("</p><p>Made ").Append(H(Txt.Local(nowUtc))).Append(" with TrackVice ").Append(H(ToolVersion()))
             .Append(". Times are in the local time of the PC that made this report (").Append(H(Zone(nowUtc))).Append(").</p><p>TrackVice by Daniel L. Baldissera &middot; MIT license</p></footer></main></body></html>");
        }

        static void Chip(StringBuilder b, string value, string label, string css)
        {
            b.Append("<div class=\"chip\"><b").Append(css != null ? " class=\"" + css + "\"" : "").Append(">").Append(H(value)).Append("</b><span>").Append(H(label)).Append("</span></div>");
        }

        // Items whose time says something: policies and profiles arrive in an instant.
        static bool Timed(Item i)
        {
            return i.PhaseKey != Phases.Policies && i.Kind != Kinds.Policy && i.Kind != Kinds.Profile && i.Key != SccmFeed.RunKey;
        }

        static bool Waiting(Item i)
        {
            return i.State == State.Pending || i.State == State.Unknown;
        }

        public static string Html(Session s, DateTime nowUtc)
        {
            return Html(s, nowUtc, new ReportOptions());
        }

        public static string Html(Session s, DateTime nowUtc, ReportOptions o)
        {
            if (o == null) o = new ReportOptions();
            string title = string.IsNullOrEmpty(o.Title) ? s.Computer : o.Title;
            StringBuilder b = new StringBuilder();
            Head(b, "TrackVice report - " + title);
            b.Append("<header><div class=\"kicker\">Deployment report</div><h1>").Append(H(title)).Append("</h1><div class=\"sub\">");
            if (!string.IsNullOrEmpty(o.Subtitle)) b.Append(H(o.Subtitle));
            else
            {
                if (!string.IsNullOrEmpty(o.Title)) b.Append(H(s.Computer)).Append(" &middot; ");
                b.Append(H(s.Kind));
                if (!string.IsNullOrEmpty(s.OsVersion)) b.Append(" &middot; ").Append(H(s.OsVersion));
                if (!string.IsNullOrEmpty(Who.Label(s) ?? s.User)) b.Append(" &middot; ").Append(H(Who.Label(s) ?? s.User));
            }
            b.Append("</div></header>");

            Summary(b, s, nowUtc);

            b.Append("<div class=\"chips\">");
            Chip(b, Int(s.Items.Count), "Items", null);
            Chip(b, Int(s.CountComplete), "Done", "ok");
            Chip(b, Int(s.CountActive), "In progress", "run");
            Chip(b, Int(s.Count(State.Failed)), "Failed", s.Count(State.Failed) > 0 ? "bad" : null);
            DateTime? first = s.FirstUtc;
            if (first.HasValue) Chip(b, Txt.DurationShort(End(s, nowUtc) - first.Value), "Duration", null);
            b.Append("</div>");

            if (s.Findings.Count > 0)
            {
                b.Append("<h2>Findings</h2><div class=\"card\">");
                foreach (Finding f in s.Findings)
                {
                    b.Append("<div class=\"finding\"><span class=\"tag ").Append(f.Severity >= 2 ? "bad" : f.Severity == 1 ? "warn" : "t3").Append("\">").Append(H(f.SeverityName)).Append("</span><b>").Append(H(f.Title)).Append("</b>");
                    if (!string.IsNullOrEmpty(f.Message)) b.Append("<p>").Append(H(f.Message)).Append("</p>");
                    if (!string.IsNullOrEmpty(f.Hint)) b.Append("<p class=\"t3\">").Append(H(f.Hint)).Append("</p>");
                    if (f.Evidence != null) b.Append("<p class=\"mono t3\">").Append(H(f.Evidence.Where)).Append(": ").Append(H(Txt.Short(f.Evidence.Text, 300))).Append("</p>");
                    b.Append("</div>");
                }
                b.Append("</div>");
            }

            if (s.Info.Count > 0)
            {
                b.Append("<h2>Deployment</h2><div class=\"card keep\"><table><tbody>");
                foreach (Pair p in s.Info) if (!AgentVersions.IsKey(p.Key)) b.Append("<tr><th style=\"width:30%\">").Append(H(p.Key)).Append("</th><td>").Append(H(p.Value)).Append("</td></tr>");
                foreach (string[] row in AgentVersions.ReportRows(s)) b.Append("<tr><th style=\"width:30%\">").Append(H(row[0])).Append("</th><td>").Append(H(row[1])).Append("</td></tr>");
                b.Append("</tbody></table></div>");
            }

            if (s.Items.Count > 0)
            {
                b.Append("<section class=\"pb\"><h2>Time line</h2>");
                Gantt(b, s, nowUtc, o);
                b.Append("</section>");
                Details(b, s, nowUtc, o);
            }

            if (o.IncludeTimeline && s.Events.Count > 0)
            {
                b.Append("<section class=\"pb\"><h2>Events</h2><div class=\"card\"><div class=\"tw\"><table><thead><tr><th style=\"width:17%\">Time</th><th style=\"width:14%\">Source</th><th style=\"width:22%\">Item</th><th>Event</th></tr></thead><tbody>");
                int n = 0;
                for (int k = s.Events.Count - 1; k >= 0 && n < 3000; k--, n++)
                {
                    TEvent e = s.Events[k];
                    Item it = s.Find(e.ItemKey);
                    b.Append("<tr><td class=\"mono nw\">").Append(H(Txt.Local(e.TimeUtc))).Append("</td><td class=\"t3\">").Append(H(e.Source)).Append("</td><td>").Append(H(it != null ? it.DisplayName : "")).Append("</td><td class=\"")
                     .Append(e.Severity >= 2 ? "bad" : e.Severity == 1 ? "warn" : "").Append("\">").Append(H(Txt.Short(e.Text, 400))).Append("</td></tr>");
                }
                b.Append("</tbody></table></div></div>");
                if (s.Events.Count > 3000) b.Append("<p class=\"t3\">Only the newest 3,000 of ").Append(Int(s.Events.Count)).Append(" events are shown. The CSV export has all of them.</p>");
                b.Append("</section>");
            }
            if (s.Notes.Count > 0)
            {
                b.Append("<h2>Notes</h2><ul>");
                foreach (string note in s.Notes) b.Append("<li>").Append(H(note)).Append("</li>");
                b.Append("</ul>");
            }
            Foot(b, "Report on " + s.Computer + (string.IsNullOrEmpty(s.AppVersion) ? "" : ", read by TrackVice " + s.AppVersion) + ".", nowUtc);
            return b.ToString();
        }

        // The last activity of the session, or now while something still runs or waits.
        static DateTime End(Session s, DateTime nowUtc)
        {
            DateTime? first = s.FirstUtc;
            DateTime end = first ?? nowUtc;
            bool open = false;
            foreach (Item i in s.Items)
            {
                if (States.IsActive(i.State) || Waiting(i)) open = true;
                DateTime e = GanttLayout.BarEnd(i, nowUtc);
                if (e > end) end = e;
            }
            if (open && nowUtc > end) end = nowUtc;
            return end;
        }

        static void Summary(StringBuilder b, Session s, DateTime nowUtc)
        {
            b.Append("<div class=\"lead\">");
            if (s.Items.Count == 0)
            {
                b.Append("<p>No deployment was found on ").Append(H(s.Computer)).Append(". There is nothing to report yet.</p></div>");
                return;
            }
            DateTime start = s.FirstUtc.Value;
            DateTime end = End(s, nowUtc);
            TimeSpan total = end - start;
            int failed = s.Count(State.Failed), active = s.CountActive, waiting = 0, done = s.CountComplete, n = s.Items.Count;
            foreach (Item i in s.Items) if (Waiting(i)) waiting++;
            bool running = active > 0 || waiting > 0;

            b.Append("<p>").Append(H(s.Kind)).Append(" on ").Append(H(s.Computer)).Append(". It started on ").Append(H(Day(start))).Append(" at ").Append(H(Clock(start)));
            if (running) b.Append(" and has run for ").Append(H(Txt.Duration(total))).Append(" so far.");
            else
            {
                b.Append(" and ended ");
                if (Day(end) != Day(start)) b.Append("on ").Append(H(Day(end))).Append(" ");
                b.Append("at ").Append(H(Clock(end))).Append(", after ").Append(H(Txt.Duration(total))).Append(".");
            }
            b.Append("</p><p>");
            if (failed > 0)
            {
                List<string> names = new List<string>();
                foreach (Item i in s.Items) if (i.State == State.Failed && names.Count < 3) names.Add(Txt.Short(i.DisplayName, 60));
                if (failed > names.Count) names.Add(Int(failed - names.Count) + " more");
                b.Append("<b class=\"bad\">Result: failed.</b> ").Append(Int(failed)).Append(" of ").Append(Int(n)).Append(failed == 1 ? " item failed: " : " items failed: ").Append(H(Join(names))).Append(".");
                if (running) b.Append(" Others are still running.");
            }
            else if (running)
            {
                List<string> parts = new List<string>();
                parts.Add(Int(done) + " are done");
                if (active > 0) parts.Add(Int(active) + " in progress");
                if (waiting > 0) parts.Add(Int(waiting) + " waiting");
                b.Append("<b class=\"run\">Result: still running.</b> Of ").Append(Int(n)).Append(" items, ").Append(H(Join(parts))).Append(".");
            }
            else b.Append("<b class=\"ok\">Result: done.</b> ").Append(n == 1 ? "The one item finished" : "All " + Int(n) + " items finished").Append(" without errors.");
            b.Append("</p>");

            List<Item> timed = new List<Item>();
            foreach (Item i in s.Items)
            {
                if (!Timed(i)) continue;
                TimeSpan? d = i.Duration(nowUtc);
                if (d.HasValue && d.Value.TotalSeconds >= 1) timed.Add(i);
            }
            timed.Sort(delegate (Item x, Item y) { return y.Duration(nowUtc).Value.CompareTo(x.Duration(nowUtc).Value); });
            if (timed.Count > 3) timed.RemoveRange(3, timed.Count - 3);
            if (timed.Count > 0)
            {
                List<string> parts = new List<string>();
                foreach (Item i in timed)
                {
                    TimeSpan d = i.Duration(nowUtc).Value;
                    string p = H(Txt.Short(i.DisplayName, 60)) + " (" + H(Txt.Duration(d));
                    if (total.Ticks > 0)
                    {
                        double share = Math.Min(100, d.Ticks * 100.0 / total.Ticks);
                        p += ", " + (share < 1 ? "under 1" : Math.Round(share).ToString("0", Inv)) + " % of the time";
                    }
                    parts.Add(p + ")");
                }
                b.Append("<p>").Append(timed.Count == 1 ? "The slowest item was " : "The slowest items were ").Append(Join(parts)).Append(".</p>");
            }
            b.Append("</div>");
        }

        // Where a time falls between from and to, in percent.
        static double At(DateTime t, DateTime from, DateTime to)
        {
            double span = (to - from).Ticks;
            return span <= 0 ? 0 : (t - from).Ticks * 100.0 / span;
        }

        static void Gantt(StringBuilder b, Session s, DateTime nowUtc, ReportOptions o)
        {
            List<GanttRow> all = GanttLayout.Rows(s, nowUtc, o.IncludePolicies, false);
            if (all.Count == 0)
            {
                b.Append("<p class=\"t3\">Nothing to show.</p>");
                return;
            }
            bool live = false;
            foreach (Item i in s.Items) if (States.IsActive(i.State)) live = true;
            DateTime from, to;
            GanttLayout.Range(all, nowUtc, live, out from, out to);
            List<GanttTick> ticks = GanttLayout.Ticks(from, to, GanttLayout.Step(to - from, 860, 80), TimeZoneInfo.Local);

            b.Append("<div class=\"legend\"><span><i class=\"ok\"></i>Done</span><span><i class=\"bad\"></i>Failed</span><span><i class=\"run\"></i>In progress</span><span><i class=\"warn\"></i>Restart needed</span><span><i></i>Waiting, skipped or not applicable</span></div>");
            b.Append("<div class=\"card gantt\"><div class=\"gaxis\"><div class=\"gname t3 small\">").Append(H(Day(from))).Append("</div><div class=\"gtrack\">");
            foreach (GanttTick k in ticks)
            {
                double x = At(k.Utc, from, to);
                if (x < 3 || x > 97) continue;
                b.Append("<span style=\"left:").Append(Pct(x)).Append("%\">").Append(H(k.Label)).Append("</span>");
            }
            b.Append("</div></div><div class=\"gbody\"><div class=\"ggrid\">");
            foreach (GanttTick k in ticks) b.Append("<i style=\"left:").Append(Pct(At(k.Utc, from, to))).Append("%\"></i>");
            RestartMarks(b, s, from, to);
            b.Append("</div>");

            foreach (GanttRow r in all)
            {
                double left = At(r.StartUtc, from, to), right = At(r.EndUtc, from, to);
                if (r.IsHeader)
                {
                    string label = Txt.Duration(r.Duration);
                    if (r.Phase.Key == Phases.Policies)
                    {
                        int n = r.Phase.Items.Count, bad = r.Phase.FailedCount;
                        label = Int(n) + (n == 1 ? " policy" : " policies") + (bad > 0 ? ", " + Int(bad) + " failed" : "") + ", " + label;
                    }
                    GRow(b, " ghead", r.Phase.Name, r.Phase.Name, left, right - left, "ph", label);
                    continue;
                }
                if (r.Phase.Key == Phases.Policies) continue;
                Item i = r.Item;
                State st = i.State;
                string dur = Txt.Duration(r.Duration);
                if (States.IsActive(st)) dur = "running, " + dur;
                else if (Waiting(i) && !i.StartUtc.HasValue) dur = "waiting";
                GRow(b, "", i.DisplayName, i.DisplayName + " - " + States.Label(st) + (dur.Length > 0 ? ", " + dur : ""), left, right - left, Css(st), dur);
            }
            b.Append("</div></div>");
        }

        // A thin line where the device restarted, with a small label.
        static void RestartMarks(StringBuilder b, Session s, DateTime from, DateTime to)
        {
            foreach (GanttTick k in GanttLayout.RestartMarks(s, TimeZoneInfo.Local))
            {
                if (k.Utc < from || k.Utc > to) continue;
                double x = At(k.Utc, from, to);
                b.Append("<i class=\"grst").Append(x > 85 ? " l" : "").Append("\" style=\"left:").Append(Pct(x)).Append("%\"><span>").Append(H(k.Label)).Append("</span></i>");
            }
        }

        static void GRow(StringBuilder b, string cls, string name, string tip, double left, double width, string css, string dur)
        {
            if (double.IsNaN(left) || double.IsInfinity(left)) left = 0;
            if (double.IsNaN(width) || double.IsInfinity(width)) width = 0;
            left = Math.Max(0, Math.Min(100, left));
            width = Math.Max(0, Math.Min(100 - left, width));
            double end = left + width;
            b.Append("<div class=\"grow").Append(cls).Append("\"><div class=\"gname\" title=\"").Append(H(tip)).Append("\">").Append(H(name)).Append("</div><div class=\"gtrack\">");
            b.Append("<b class=\"gbar ").Append(css).Append("\" style=\"left:").Append(Pct(left)).Append("%;width:").Append(Pct(width)).Append("%\"></b>");
            if (!string.IsNullOrEmpty(dur))
            {
                // After the bar when there is room, before it when it ends near the right edge, inside it when it fills the row.
                if (end <= 80) b.Append("<em class=\"gdur\" style=\"left:").Append(Pct(end)).Append("%\">");
                else if (left >= 20) b.Append("<em class=\"gdur l\" style=\"left:").Append(Pct(left)).Append("%\">");
                else b.Append("<em class=\"gdur in\" style=\"left:").Append(Pct(end)).Append("%\">");
                b.Append(H(dur)).Append("</em>");
            }
            b.Append("</div></div>");
        }

        static void Details(StringBuilder b, Session s, DateTime nowUtc, ReportOptions o)
        {
            b.Append("<section class=\"pb\"><h2>Details per phase</h2><div class=\"stack\">");
            foreach (Phase p in s.OrderedPhases)
            {
                if (p.Key == Phases.Policies && !o.IncludePolicies)
                {
                    b.Append("<p class=\"t3\">Policies: ").Append(Int(p.Items.Count)).Append(" in total, ").Append(Int(p.Done)).Append(" done, ").Append(Int(p.FailedCount)).Append(" failed. They are left out of this report.</p>");
                    continue;
                }
                b.Append("<div class=\"card").Append(p.Items.Count <= 20 ? " keep" : "").Append("\"><div class=\"phase\"><div class=\"").Append(Css(p.State)).Append("\">").Append(H(p.Name)).Append("</div><span>")
                 .Append(Int(p.Done)).Append(" of ").Append(Int(p.Items.Count)).Append(" done");
                if (p.FailedCount > 0) b.Append(", ").Append(Int(p.FailedCount)).Append(" failed");
                TimeSpan? d = p.Duration(nowUtc);
                if (d.HasValue) b.Append(" &middot; ").Append(H(Txt.Duration(d)));
                b.Append("</span></div><div class=\"tw\"><table><thead><tr><th>Item</th><th style=\"width:11%\">Kind</th><th style=\"width:12%\">State</th><th style=\"width:9%\">Start</th><th style=\"width:11%\">Duration</th><th style=\"width:6%\">Tries</th><th style=\"width:20%\">Exit code</th></tr></thead><tbody>");
                foreach (Item i in p.Items)
                {
                    State st = i.State;
                    b.Append("<tr><td>").Append(H(i.DisplayName)).Append("</td><td class=\"t3\">").Append(H(AppGroups.KindText(i))).Append("</td><td class=\"").Append(Css(st)).Append("\">").Append(H(AppGroups.StateLabel(i)));
                    if (States.IsActive(st) && i.Percent.HasValue) b.Append(" ").Append(Int(Math.Max(0, Math.Min(100, i.Percent.Value)))).Append(" %");
                    b.Append("</td><td class=\"nw\">").Append(i.StartUtc.HasValue ? H(Txt.LocalTime(i.StartUtc.Value)) : "").Append("</td><td class=\"nw\">").Append(H(Txt.Duration(i.Duration(nowUtc))))
                     .Append("</td><td>").Append(i.Attempts.Count > 0 ? Int(i.Attempts.Count) : "").Append("</td><td><span class=\"mono\">").Append(H(Errors.Short(i.ExitCode))).Append("</span>");
                    if (i.ExitCode.HasValue && i.ExitCode.Value != 0)
                    {
                        string m = Errors.Meaning(i.ExitCode.Value);
                        if (m != null) b.Append("<div class=\"t3 small\">").Append(H(m)).Append("</div>");
                    }
                    b.Append("</td></tr>");
                    if (st == State.Failed && i.Events.Count > 0)
                    {
                        b.Append("<tr class=\"ev\"><td colspan=\"7\">");
                        for (int k = Math.Max(0, i.Events.Count - 5); k < i.Events.Count; k++)
                        {
                            TEvent e = i.Events[k];
                            b.Append("<div><span class=\"mono t3\">").Append(H(Txt.LocalTime(e.TimeUtc))).Append("</span> &nbsp;<span").Append(e.Severity >= 2 ? " class=\"bad\"" : "").Append(">").Append(H(Txt.Short(e.Text, 300))).Append("</span></div>");
                        }
                        b.Append("</td></tr>");
                    }
                }
                b.Append("</tbody></table></div></div>");
            }
            b.Append("</div></section>");
        }

        public static void Csv(Session s, string path)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("Time (local),Source,Severity,Phase,Item,Kind,State,Event,Evidence");
                foreach (TEvent e in s.Events)
                {
                    Item i = s.Find(e.ItemKey);
                    Phase p = i != null ? s.FindPhase(i.PhaseKey) : null;
                    w.WriteLine(string.Join(",", new string[]
                    {
                        Q(Txt.Local(e.TimeUtc)), Q(e.Source), Q(e.Severity >= 2 ? "Error" : e.Severity == 1 ? "Warning" : "Info"),
                        Q(p != null ? p.Name : ""), Q(i != null ? i.DisplayName : ""), Q(i != null ? i.Kind : ""),
                        Q(e.StateName != null ? States.Label(e.State) : ""), Q(e.Text), Q(e.Evidence != null ? e.Evidence.Where : "")
                    }));
                }
            }
        }

        static string Q(string v)
        {
            v = v ?? "";
            // Keep spreadsheets from running a value as a formula.
            if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        // A zip with the session and the log files its evidence points to, for sending to someone else.
        // Logs that are worth having even when the session did not point at them: every IME log, and the task sequence logs.
        public static readonly string[][] ExtraLogs =
        {
            new string[] { @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs", "*.log" },
            new string[] { @"C:\Windows\CCM\Logs", "smsts*.log" },
            new string[] { @"X:\Windows\Temp\SMSTSLog", "*.log" },
        };

        // A log above this size goes into the bundle as its last part, which holds the newest lines.
        public const long MaxLogBytes = 20L * 1024 * 1024;

        public static int Bundle(Session s, string zipPath)
        {
            return Bundle(s, zipPath, ExtraLogs);
        }

        public static int Bundle(Session s, string zipPath, string[][] extra)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "TrackVice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            int files = 0;
            try
            {
                s.Save(Path.Combine(tmp, "session.json"));
                StringBuilder index = new StringBuilder();
                HashSet<string> logs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TEvent e in s.Events) if (e.Evidence != null && e.Evidence.IsLogFile) logs.Add(e.Evidence.Path);
                if (extra != null)
                    foreach (string[] x in extra)
                    {
                        try
                        {
                            if (Directory.Exists(x[0]))
                                foreach (string f in Directory.GetFiles(x[0], x[1])) logs.Add(Path.GetFullPath(f));
                        }
                        catch (Exception) { }
                    }
                foreach (string log in logs)
                {
                    try
                    {
                        if (!File.Exists(log)) continue;
                        string name = Path.GetFileName(log);
                        string dest = Path.Combine(tmp, name);
                        int k = 2;
                        while (File.Exists(dest)) dest = Path.Combine(tmp, Path.GetFileNameWithoutExtension(name) + " (" + k++ + ")" + Path.GetExtension(name));
                        using (FileStream src = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (FileStream dst = File.Create(dest))
                        {
                            if (src.Length > MaxLogBytes) src.Seek(-MaxLogBytes, SeekOrigin.End);
                            src.CopyTo(dst);
                        }
                        index.Append(Path.GetFileName(dest)).Append('\t').Append(log).Append("\r\n");
                        files++;
                    }
                    catch (Exception) { }
                }
                if (files > 0) File.WriteAllText(Path.Combine(tmp, LogBundle.IndexName), index.ToString(), new UTF8Encoding(false));
                if (File.Exists(zipPath)) File.Delete(zipPath);
                Zip.FromFolder(tmp, zipPath);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (Exception) { }
            }
            return files;
        }
    }

    // Writes a folder's files into a zip with System.IO.Compression, which ships with .NET Framework 4.5.
    public static class Zip
    {
        public static void FromFolder(string folder, string zipPath)
        {
            using (FileStream fs = File.Create(zipPath))
            using (System.IO.Compression.ZipArchive z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (string f in Directory.GetFiles(folder))
                {
                    System.IO.Compression.ZipArchiveEntry e = z.CreateEntry(Path.GetFileName(f), System.IO.Compression.CompressionLevel.Optimal);
                    using (Stream w = e.Open())
                    using (FileStream r = File.OpenRead(f)) r.CopyTo(w);
                }
            }
        }
    }
}
