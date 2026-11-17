namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    // The report over many enrollments, from the sessions agents uploaded.
    public static partial class Report
    {
        public static string FleetHtml(FleetStats st, string title, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(title)) title = "Enrollments";
            StringBuilder b = new StringBuilder();
            Head(b, "TrackVice report - " + title);

            DateTime? first = null, last = null;
            int running = 0;
            foreach (FleetRun r in st.Runs)
            {
                if (r.StartUtc.HasValue && (first == null || r.StartUtc.Value < first.Value)) first = r.StartUtc;
                DateTime? e = r.EndUtc ?? r.StartUtc;
                if (e.HasValue && (last == null || e.Value > last.Value)) last = e;
                if (!r.Finished && r.Failed == 0) running++;
            }
            b.Append("<header><div class=\"kicker\">Enrollment statistics</div><h1>").Append(H(title)).Append("</h1><div class=\"sub\">").Append(Int(st.Runs.Count)).Append(st.Runs.Count == 1 ? " enrollment" : " enrollments");
            if (first.HasValue) b.Append(" &middot; ").Append(H(Day(first.Value))).Append(last.HasValue && Day(last.Value) != Day(first.Value) ? " to " + H(Day(last.Value)) : "");
            b.Append("</div></header>");

            b.Append("<div class=\"lead\">");
            if (st.Runs.Count == 0) b.Append("<p>No enrollments were found. There is nothing to report yet.</p>");
            else
            {
                b.Append("<p>").Append(Int(st.Runs.Count)).Append(st.Runs.Count == 1 ? " enrollment" : " enrollments").Append(" in this report. ").Append(Int(st.Finished)).Append(" finished");
                if (st.Finished > 0)
                    b.Append(": on average they took ").Append(H(Txt.Duration(TimeSpan.FromSeconds(st.AvgSeconds)))).Append(", half of them took less than ").Append(H(Txt.Duration(TimeSpan.FromSeconds(st.MedianSeconds))))
                     .Append(" and the longest took ").Append(H(Txt.Duration(TimeSpan.FromSeconds(st.LongestSeconds))));
                b.Append(".");
                if (running > 0) b.Append(" ").Append(Int(running)).Append(running == 1 ? " is" : " are").Append(" still running.");
                b.Append("</p><p>");
                if (st.WithFailures > 0) b.Append("<b class=\"bad\">").Append(Int(st.WithFailures)).Append(st.WithFailures == 1 ? " enrollment" : " enrollments").Append(" had at least one failed item.</b>");
                else b.Append("<b class=\"ok\">No enrollment had a failed item.</b>");
                b.Append("</p>");
            }
            b.Append("</div>");

            b.Append("<div class=\"chips\">");
            Chip(b, Int(st.Runs.Count), "Enrollments", null);
            Chip(b, Int(st.Finished), "Finished", "ok");
            Chip(b, Int(st.WithFailures), "With failures", st.WithFailures > 0 ? "bad" : null);
            if (st.Finished > 0)
            {
                Chip(b, Txt.DurationShort(TimeSpan.FromSeconds(st.AvgSeconds)), "Average", null);
                Chip(b, Txt.DurationShort(TimeSpan.FromSeconds(st.MedianSeconds)), "Median", null);
                Chip(b, Txt.DurationShort(TimeSpan.FromSeconds(st.LongestSeconds)), "Longest", null);
            }
            b.Append("</div>");

            Slowest(b, st);
            Failing(b, st);

            b.Append("<section class=\"pb\"><h2>Duration over time</h2>");
            Chart(b, st);
            b.Append("<h2>All enrollments</h2>");
            if (st.Runs.Count == 0) b.Append("<p class=\"t3\">None.</p>");
            else
            {
                bool profiles = false;
                foreach (FleetRun r in st.Runs) if (!string.IsNullOrEmpty(r.Profile)) profiles = true;
                b.Append("<div class=\"card\"><div class=\"tw\"><table><thead><tr><th>Device</th><th>Deployment</th>").Append(profiles ? "<th>Profile or task sequence</th>" : "").Append("<th style=\"width:14%\">Started</th><th style=\"width:11%\">Duration</th><th style=\"width:8%\">Items</th><th style=\"width:10%\">Result</th><th>Slowest part</th></tr></thead><tbody>");
                foreach (FleetRun r in st.Runs)
                {
                    string res = r.Failed > 0 ? "Failed" : r.Finished ? "Done" : "Running";
                    string css = r.Failed > 0 ? "bad" : r.Finished ? "ok" : "run";
                    b.Append("<tr><td class=\"nw\">").Append(H(r.Computer)).Append("</td><td class=\"t3\">").Append(H(r.Kind)).Append("</td>").Append(profiles ? "<td>" + H(r.Profile) + "</td>" : "").Append("<td class=\"nw\">")
                     .Append(r.StartUtc.HasValue ? H(Txt.Local(r.StartUtc.Value).Substring(0, 16)) : "").Append("</td><td class=\"nw\">").Append(r.Seconds > 0 ? H(Txt.Duration(TimeSpan.FromSeconds(r.Seconds))) : "")
                     .Append("</td><td class=\"nw\">").Append(Int(r.Done)).Append(" of ").Append(Int(r.Items)).Append("</td><td class=\"").Append(css).Append("\">").Append(res);
                    if (r.Failed > 0) b.Append(" (").Append(Int(r.Failed)).Append(")");
                    b.Append("</td><td>").Append(H(r.Slowest));
                    if (r.SlowestSeconds > 0) b.Append(" <span class=\"t3 nw\">").Append(H(Txt.Duration(TimeSpan.FromSeconds(r.SlowestSeconds)))).Append("</span>");
                    b.Append("</td></tr>");
                }
                b.Append("</tbody></table></div></div>");
            }
            b.Append("</section>");
            Foot(b, "Report on " + Int(st.Runs.Count) + (st.Runs.Count == 1 ? " enrollment" : " enrollments") + " uploaded by the TrackVice agent. Policies and profiles are not counted in the times.", nowUtc);
            return b.ToString();
        }

        static void Slowest(StringBuilder b, FleetStats st)
        {
            List<FleetItem> l = new List<FleetItem>();
            foreach (FleetItem f in st.Items) if (f.Timed > 0 && f.AvgSeconds >= 1 && l.Count < 10) l.Add(f);
            b.Append("<h2>Slowest items</h2>");
            if (l.Count == 0)
            {
                b.Append("<p class=\"t3\">No item has finished yet.</p>");
                return;
            }
            double max = l[0].AvgSeconds;
            foreach (FleetItem f in l) if (f.AvgSeconds > max) max = f.AvgSeconds;
            b.Append("<p class=\"t3 small\">The ten items that took longest on average, over the devices where they finished.</p><div class=\"card keep\">");
            foreach (FleetItem f in l)
            {
                b.Append("<div class=\"hrow\"><div class=\"hname\" title=\"").Append(H(f.Name + " (" + f.Kind + ")")).Append("\">").Append(H(f.Name)).Append("</div><div class=\"htrack\"><b style=\"width:")
                 .Append(Pct(max > 0 ? f.AvgSeconds * 100 / max : 0)).Append("%\"></b></div><div class=\"hval\"><b>").Append(H(Txt.Duration(TimeSpan.FromSeconds(f.AvgSeconds)))).Append("</b> on average, longest ")
                 .Append(H(Txt.Duration(TimeSpan.FromSeconds(f.MaxSeconds))));
                if (!string.IsNullOrEmpty(f.SlowestOn)) b.Append(" on ").Append(H(f.SlowestOn));
                b.Append(", ").Append(Int(f.Timed)).Append(f.Timed == 1 ? " device" : " devices").Append("</div></div>");
            }
            b.Append("</div>");
        }

        static void Failing(StringBuilder b, FleetStats st)
        {
            List<FleetItem> l = new List<FleetItem>();
            foreach (FleetItem f in st.Items) if (f.Failed > 0) l.Add(f);
            b.Append("<h2>What fails most</h2>");
            if (l.Count == 0)
            {
                b.Append("<p class=\"t3\">No item failed.</p>");
                return;
            }
            l.Sort(delegate (FleetItem x, FleetItem y)
            {
                int c = y.Failed.CompareTo(x.Failed);
                if (c != 0) return c;
                c = y.FailRate.CompareTo(x.FailRate);
                return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            if (l.Count > 15) l.RemoveRange(15, l.Count - 15);
            b.Append("<div class=\"card\"><div class=\"tw\"><table><thead><tr><th>Item</th><th style=\"width:12%\">Kind</th><th style=\"width:17%\">Failure rate</th><th style=\"width:15%\">Failed on</th><th style=\"width:18%\">For example</th></tr></thead><tbody>");
            foreach (FleetItem f in l)
            {
                b.Append("<tr><td>").Append(H(f.Name)).Append("</td><td class=\"t3\">").Append(H(f.Kind)).Append("</td><td class=\"nw\">").Append(Num(f.FailRate)).Append(" %<span class=\"rbar\"><b style=\"width:")
                 .Append(Pct(f.FailRate)).Append("%\"></b></span></td><td class=\"nw\">").Append(Int(f.Failed)).Append(" of ").Append(Int(f.Done + f.Failed)).Append(f.Done + f.Failed == 1 ? " device" : " devices")
                 .Append("</td><td>").Append(H(f.FailedOn)).Append("</td></tr>");
            }
            b.Append("</tbody></table></div></div>");
        }

        static double NiceStep(double raw)
        {
            double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 180, 240, 360, 720, 1440 };
            foreach (double s in steps) if (s >= raw) return s;
            return Math.Ceiling(raw / 1440) * 1440;
        }

        static string F(double v)
        {
            return Num(v);
        }

        // Each finished enrollment as a dot: when it started, and how many minutes it took.
        static void Chart(StringBuilder b, FleetStats st)
        {
            List<FleetRun> pts = new List<FleetRun>();
            foreach (FleetRun r in st.Runs) if (r.StartUtc.HasValue && r.Seconds > 0 && (r.Finished || r.Failed > 0)) pts.Add(r);
            if (pts.Count == 0)
            {
                b.Append("<p class=\"t3\">No finished enrollment to show yet.</p>");
                return;
            }
            pts.Sort(delegate (FleetRun x, FleetRun y) { return x.StartUtc.Value.CompareTo(y.StartUtc.Value); });
            DateTime x0 = pts[0].StartUtc.Value, x1 = pts[pts.Count - 1].StartUtc.Value;
            if (x1 - x0 < TimeSpan.FromHours(1))
            {
                DateTime mid = x0 + TimeSpan.FromTicks((x1 - x0).Ticks / 2);
                x0 = mid.AddMinutes(-30);
                x1 = mid.AddMinutes(30);
            }
            double maxMin = 0;
            foreach (FleetRun r in pts) maxMin = Math.Max(maxMin, r.Seconds / 60);
            double step = NiceStep(maxMin / 4);
            double yMax = Math.Max(step, Math.Ceiling(maxMin / step) * step);
            const double L = 70, R = 970, T = 14, B = 262;
            b.Append("<div class=\"card keep\"><svg class=\"chart\" viewBox=\"0 0 1000 300\" role=\"img\" aria-label=\"Duration of each finished enrollment by the time it started\">");
            for (double v = 0; v <= yMax + 0.001; v += step)
            {
                double y = B - v / yMax * (B - T);
                b.Append("<line class=\"").Append(v == 0 ? "ax" : "gl").Append("\" x1=\"").Append(F(L)).Append("\" x2=\"").Append(F(R)).Append("\" y1=\"").Append(F(y)).Append("\" y2=\"").Append(F(y)).Append("\"></line>");
                b.Append("<text x=\"").Append(F(L - 8)).Append("\" y=\"").Append(F(y + 4)).Append("\" text-anchor=\"end\">").Append(H(v >= 60 && v % 60 == 0 ? F(v / 60) + " h" : F(v) + " min")).Append("</text>");
            }
            bool sameDay = Day(x0) == Day(x1);
            for (int k = 0; k <= 4; k++)
            {
                DateTime t = x0 + TimeSpan.FromTicks((x1 - x0).Ticks / 4 * k);
                double x = L + (R - L) * k / 4;
                string anchor = k == 0 ? "start" : k == 4 ? "end" : "middle";
                b.Append("<text x=\"").Append(F(x)).Append("\" y=\"").Append(F(B + 22)).Append("\" text-anchor=\"").Append(anchor).Append("\">")
                 .Append(H(t.ToLocalTime().ToString(sameDay ? "HH:mm" : "d MMM HH:mm", Inv))).Append("</text>");
            }
            StringBuilder line = new StringBuilder();
            StringBuilder dots = new StringBuilder();
            foreach (FleetRun r in pts)
            {
                double x = L + (R - L) * At(r.StartUtc.Value, x0, x1) / 100;
                double y = B - Math.Min(1, r.Seconds / 60 / yMax) * (B - T);
                if (line.Length > 0) line.Append(' ');
                line.Append(F(x)).Append(',').Append(F(y));
                dots.Append("<circle class=\"").Append(r.Failed > 0 ? "bad" : "ok").Append("\" cx=\"").Append(F(x)).Append("\" cy=\"").Append(F(y)).Append("\" r=\"5\"><title>")
                    .Append(H(r.Computer + ", " + Txt.Local(r.StartUtc.Value) + ", " + Txt.Duration(TimeSpan.FromSeconds(r.Seconds)) + (r.Failed > 0 ? ", failed" : ""))).Append("</title></circle>");
            }
            if (pts.Count > 1) b.Append("<polyline class=\"trend\" points=\"").Append(line.ToString()).Append("\"></polyline>");
            b.Append(dots.ToString()).Append("</svg></div>");
            b.Append("<div class=\"legend\" style=\"margin-top:8px\"><span><i class=\"ok\"></i>Finished</span><span><i class=\"bad\"></i>Had a failed item</span><span>Enrollments that still run are not shown.</span></div>");
        }

        // /fleet-report: reads every session the agents uploaded to a location and writes the report.
        public static int FleetCli(string outPath, string upload, int hours, string title)
        {
            if (string.IsNullOrEmpty(upload)) throw new ArgumentException("/fleet-report needs /upload <location>: the storage container SAS link or folder the agents upload to.");
            AgentCatalog c = new AgentCatalog(Stores.Open(upload));
            DateTime now = DateTime.UtcNow;
            List<Session> sessions = DeviceSides.Filter(c.LoadAll(hours == 0 ? DateTime.MinValue : now.AddHours(-hours), 5000, null), DeviceSides.CliFilter);
            FleetStats st = FleetStats.Build(sessions, now);
            File.WriteAllText(outPath, FleetHtml(st, title, now), new UTF8Encoding(false));
            Console.WriteLine(Int(st.Runs.Count) + " enrollments, " + Int(st.Finished) + " finished, " + Int(st.WithFailures) + " with failed items. Report saved to " + outPath);
            return st.WithFailures > 0 ? 2 : 0;
        }
    }
}
