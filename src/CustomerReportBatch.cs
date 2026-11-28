namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // One device in a batch report: what the table needs, and its session when one was read, for the page of the device.
    public sealed class BatchRow
    {
        public string Device, User, Profile, Result;
        public DateTime? StartUtc, EndUtc;
        public double Seconds;
        public Session S;
        // The device in Overview, which can give the session when it was not read.
        public OverviewDevice Dev;

        public bool Finished
        {
            get { return Result == ReportResult.Done || Result == ReportResult.DoneWithFailures || Result == ReportResult.Failed; }
        }
    }

    public sealed class BatchOptions
    {
        public string Customer, PreparedBy, Version;
        // "Last 7 days", "Today" or "3 Nov 2026 to 9 Nov 2026".
        public string PeriodText;
        // What else the list was narrowed to: "Intune".
        public string FilterText;
        // The days of the chart, as dates of the local time: first and last day, both included.
        public DateTime FromDay, ToDay;
        // A page for every device that has a session.
        public bool PerDevice;
    }

    // The report over many devices for the customer: the numbers, a bar for every day, the devices, what failed most and
    // what took longest, and optionally the report of every device on a page of its own.
    public static class CustomerReportBatch
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const int MaxDays = 62, MaxTable = 500, TopItems = 8;
        // A run that has not finished after this long is not running any more.
        public static readonly TimeSpan Stale = TimeSpan.FromHours(12);

        static string E(string s)
        {
            return CustomerReport.H(s);
        }

        static string Int(int v)
        {
            return v.ToString(Inv);
        }

        public static BatchRow RowOf(OverviewDevice o, DateTime nowUtc)
        {
            BatchRow r = new BatchRow();
            r.Dev = o;
            r.Device = o.D.Device;
            Fill(r, o.S, nowUtc);
            return r;
        }

        // Sets the numbers of the row from the list entry of its device and from the session, when there is one.
        public static void Fill(BatchRow r, Session s, DateTime nowUtc)
        {
            OverviewDevice o = r.Dev;
            r.S = s;
            r.Profile = o.D.Profile;
            r.StartUtc = o.D.StartUtc;
            r.EndUtc = o.D.EndUtc;
            TimeSpan? d = o.D.Duration(nowUtc);
            r.Seconds = d.HasValue ? d.Value.TotalSeconds : 0;
            r.User = o.D.EnrollUser;
            if (s != null && s.Items.Count > 0)
            {
                r.Result = CustomerReport.ResultOf(s);
                FleetRun run = FleetStats.RunOf(s, nowUtc);
                if (run.StartUtc.HasValue) r.StartUtc = run.StartUtc;
                if (run.EndUtc.HasValue) r.EndUtc = run.EndUtc;
                if (run.Seconds > 0) r.Seconds = run.Seconds;
                if (string.IsNullOrEmpty(r.Profile)) r.Profile = run.Profile;
                if (string.IsNullOrEmpty(r.User)) r.User = Who.Label(s);
            }
            else if (o.D.State == State.Succeeded) r.Result = ReportResult.Done;
            else if (o.D.State == State.Failed) r.Result = ReportResult.Failed;
            else r.Result = ReportResult.Running;
            if (r.Result == ReportResult.Running)
            {
                bool old = nowUtc - (r.EndUtc ?? o.D.LastUtc) > Stale;
                if (o.Replaced || o.Silent || old) r.Result = ReportResult.NotFinished;
            }
        }

        // The devices of the filter that started in the period [fromUtc, toUtc), oldest first. The snapshot's runs are used
        // when it has them, since they reach further back than the week.
        public static List<BatchRow> Rows(OverviewSnapshot snap, OverviewFilter filter, DateTime fromUtc, DateTime toUtc, DateTime nowUtc)
        {
            List<BatchRow> l = new List<BatchRow>();
            if (snap == null) return l;
            List<OverviewDevice> src = snap.Runs.Count > 0 ? snap.Runs : snap.Devices;
            foreach (OverviewDevice o in src)
            {
                if (filter != null && !filter.Shows(o)) continue;
                BatchRow r = RowOf(o, nowUtc);
                DateTime t = r.StartUtc ?? o.D.LastUtc;
                if (t < fromUtc || t >= toUtc) continue;
                l.Add(r);
            }
            l.Sort(delegate (BatchRow a, BatchRow b)
            {
                int c = (a.StartUtc ?? DateTime.MaxValue).CompareTo(b.StartUtc ?? DateTime.MaxValue);
                return c != 0 ? c : string.Compare(a.Device, b.Device, StringComparison.OrdinalIgnoreCase);
            });
            return l;
        }

        // The value at the share p (0 to 1) of the sorted list, by the nearest rank.
        public static double Percentile(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            int rank = (int)Math.Ceiling(p * sorted.Count);
            if (rank < 1) rank = 1;
            if (rank > sorted.Count) rank = sorted.Count;
            return sorted[rank - 1];
        }

        public sealed class Numbers
        {
            public int Devices, Done, WithFailures, Failed, Unfinished;
            public double Median, P90;
            // Of the devices that finished or failed, the share that finished with nothing failed. -1 when none finished.
            public double Rate = -1;
        }

        public static Numbers Count(List<BatchRow> rows)
        {
            Numbers n = new Numbers();
            List<double> secs = new List<double>();
            foreach (BatchRow r in rows)
            {
                n.Devices++;
                if (r.Result == ReportResult.Done) n.Done++;
                else if (r.Result == ReportResult.DoneWithFailures) n.WithFailures++;
                else if (r.Result == ReportResult.Failed) n.Failed++;
                else n.Unfinished++;
                if (r.Finished && r.Seconds > 0) secs.Add(r.Seconds);
            }
            int ended = n.Done + n.WithFailures + n.Failed;
            if (ended > 0) n.Rate = n.Done * 100.0 / ended;
            secs.Sort();
            if (secs.Count > 0)
            {
                n.Median = secs.Count % 2 == 1 ? secs[secs.Count / 2] : (secs[secs.Count / 2 - 1] + secs[secs.Count / 2]) / 2;
                n.P90 = Percentile(secs, 0.9);
            }
            return n;
        }

        public static string Html(List<BatchRow> rows, BatchOptions o, DateTime nowUtc)
        {
            if (o == null) o = new BatchOptions();
            if (rows == null) rows = new List<BatchRow>();
            CustomerReportOptions co = new CustomerReportOptions();
            co.Customer = o.Customer;
            co.PreparedBy = o.PreparedBy;
            co.Version = o.Version;
            co.Footer = false;
            Numbers n = Count(rows);
            StringBuilder b = new StringBuilder();
            CustomerReport.Head(b, "Deployment report - " + (o.Customer ?? ""));
            b.Append("<section class=\"dev\"><div class=\"top\"><span class=\"brand\">Deployment report</span><span class=\"brand\">TrackVice</span></div>");
            b.Append("<div class=\"head\"><div><div class=\"cust\">").Append(CustomerReport.H(string.IsNullOrEmpty(o.Customer) ? "Deployment report" : o.Customer)).Append("</div><h1>")
             .Append(CustomerReport.H(o.PeriodText ?? "")).Append("</h1>");
            if (!string.IsNullOrEmpty(o.FilterText)) b.Append("<div class=\"note\">").Append(CustomerReport.H(o.FilterText)).Append("</div>");
            b.Append("</div></div>");

            b.Append("<div class=\"kpis\">");
            Kpi(b, Int(n.Devices), "Devices", n.Unfinished > 0 ? Int(n.Unfinished) + " not finished" : null, null);
            Kpi(b, Int(n.Done), "Done", n.WithFailures > 0 ? Int(n.WithFailures) + " more done with failures" : null, n.Done > 0 ? "ok" : null);
            Kpi(b, Int(n.Failed), "Failed", null, n.Failed > 0 ? "bad" : null);
            Kpi(b, n.Rate < 0 ? "–" : Math.Round(n.Rate).ToString("0", Inv) + " %", "Success rate", "Done with no failed item", null);
            Kpi(b, n.Median > 0 ? Txt.DurationShort(TimeSpan.FromSeconds(n.Median)) : "–", "Median time", null, null);
            Kpi(b, n.P90 > 0 ? Txt.DurationShort(TimeSpan.FromSeconds(n.P90)) : "–", "90 % finished within", null, null);
            b.Append("</div>");

            if (rows.Count == 0) b.Append("<p class=\"lead\">No deployments were found for this period.</p>");
            else
            {
                b.Append("<h2>Devices per day</h2>");
                DayChart(b, rows, o);
                Devices(b, rows, o.PerDevice);
                Items(b, rows, nowUtc);
            }
            if (o.PerDevice)
            {
                int without = rows.FindAll(delegate (BatchRow r) { return !HasPage(r); }).Count;
                if (without > 0 && without < rows.Count) b.Append("<p class=\"note\">").Append(Int(without)).Append(without == 1 ? " device has no details for a page of its own.</p>" : " devices have no details for pages of their own.</p>");
            }
            b.Append("</section>");
            int lastPage = -1;
            if (o.PerDevice)
                for (int k = 0; k < rows.Count; k++) if (HasPage(rows[k])) lastPage = k;
            for (int k = 0; k <= lastPage; k++)
                if (HasPage(rows[k])) CustomerReport.Section(b, rows[k].S, nowUtc, co, true, "d" + Int(k + 1), k == lastPage);
            if (lastPage < 0) CustomerReport.Footer(b, co, nowUtc);
            CustomerReport.Tail(b);
            return b.ToString();
        }

        static bool HasPage(BatchRow r)
        {
            return r.S != null && r.S.Items.Count > 0;
        }

        static void Kpi(StringBuilder b, string value, string label, string sub, string css)
        {
            b.Append("<div class=\"kpi").Append(css == null ? "" : " " + css).Append("\"><b>").Append(CustomerReport.H(value)).Append("</b><span>").Append(CustomerReport.H(label)).Append("</span>");
            if (!string.IsNullOrEmpty(sub)) b.Append("<small>").Append(CustomerReport.H(sub)).Append("</small>");
            b.Append("</div>");
        }

        // A bar per day, stacked: done, done with failures, failed, and not finished.
        static void DayChart(StringBuilder b, List<BatchRow> rows, BatchOptions o)
        {
            DateTime from = o.FromDay.Date, to = o.ToDay.Date;
            if (to < from) to = from;
            if ((to - from).TotalDays + 1 > MaxDays) from = to.AddDays(-(MaxDays - 1));
            int days = (int)(to - from).TotalDays + 1;
            int[][] c = new int[days][];
            for (int k = 0; k < days; k++) c[k] = new int[4];
            int max = 1;
            foreach (BatchRow r in rows)
            {
                if (!r.StartUtc.HasValue) continue;
                int k = (int)(r.StartUtc.Value.ToLocalTime().Date - from).TotalDays;
                if (k < 0 || k >= days) continue;
                int part = r.Result == ReportResult.Done ? 0 : r.Result == ReportResult.DoneWithFailures ? 1 : r.Result == ReportResult.Failed ? 2 : 3;
                c[k][part]++;
            }
            for (int k = 0; k < days; k++) max = Math.Max(max, c[k][0] + c[k][1] + c[k][2] + c[k][3]);
            double top = max <= 4 ? max : Math.Ceiling(max / 4.0) * 4;
            const double left = 34, width = 680, height = 170, bottom = 24, topPad = 18;
            double plotH = height - bottom - topPad;
            double slot = (width - left) / days;
            double bw = Math.Max(3, Math.Min(34, slot * 0.7));
            b.Append("<svg class=\"days\" viewBox=\"0 0 720 ").Append(Px(height)).Append("\" role=\"img\" aria-label=\"Devices per day\">");
            int ticks = top <= 4 ? (int)top : 4;
            for (int t = 0; t <= ticks; t++)
            {
                double v = ticks == 0 ? 0 : top * t / ticks;
                double y = topPad + plotH - v / top * plotH;
                b.Append("<line x1=\"").Append(Px(left)).Append("\" x2=\"").Append(Px(width + 10)).Append("\" y1=\"").Append(Px(y)).Append("\" y2=\"").Append(Px(y)).Append("\" class=\"gl\"/>");
                b.Append("<text x=\"").Append(Px(left - 6)).Append("\" y=\"").Append(Px(y + 4)).Append("\" text-anchor=\"end\">").Append(Math.Round(v).ToString("0", Inv)).Append("</text>");
            }
            int every = days <= 10 ? 1 : days <= 20 ? 2 : days <= 35 ? 5 : 7;
            string[] cls = { "ok", "warn", "bad", "idle" };
            for (int k = 0; k < days; k++)
            {
                double x = left + slot * k + (slot - bw) / 2;
                double y = topPad + plotH;
                for (int part = 0; part < 4; part++)
                {
                    if (c[k][part] == 0) continue;
                    double h = c[k][part] / top * plotH;
                    y -= h;
                    b.Append("<rect x=\"").Append(Px(x)).Append("\" y=\"").Append(Px(y)).Append("\" width=\"").Append(Px(bw)).Append("\" height=\"").Append(Px(h)).Append("\" class=\"").Append(cls[part]).Append("\"/>");
                }
                int sum = c[k][0] + c[k][1] + c[k][2] + c[k][3];
                if (sum > 0 && bw >= 14) b.Append("<text x=\"").Append(Px(x + bw / 2)).Append("\" y=\"").Append(Px(y - 4)).Append("\" text-anchor=\"middle\">").Append(Int(sum)).Append("</text>");
                if (k % every == 0)
                    b.Append("<text x=\"").Append(Px(x + bw / 2)).Append("\" y=\"").Append(Px(height - 6)).Append("\" text-anchor=\"middle\">").Append(from.AddDays(k).ToString(days > 10 ? "d MMM" : "ddd d", Inv)).Append("</text>");
            }
            b.Append("</svg><div class=\"legend\"><span><i class=\"ok\"></i>Done</span><span><i class=\"warn\"></i>Done with failures</span><span><i class=\"bad\"></i>Failed</span><span><i class=\"idle\"></i>Not finished</span></div>");
        }

        static string Px(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            return v.ToString("0.#", Inv);
        }

        // With pages, the name of a device leads to its page.
        static void Devices(StringBuilder b, List<BatchRow> rows, bool pages)
        {
            b.Append("<h2>Devices<span class=\"cnt\">").Append(Int(rows.Count)).Append("</span></h2><table><thead><tr><th>Device</th><th>User</th><th>Profile</th><th>Started</th><th class=\"num\">Time</th><th>Result</th></tr></thead><tbody>");
            int n = 0;
            foreach (BatchRow r in rows)
            {
                if (n++ >= MaxTable) break;
                b.Append("<tr><td>");
                if (pages && HasPage(r)) b.Append("<a href=\"#d").Append(Int(n)).Append("\">").Append(E(r.Device)).Append("</a>");
                else b.Append(E(r.Device));
                b.Append("</td><td>").Append(E(r.User)).Append("</td><td>").Append(E(r.Profile)).Append("</td><td class=\"tm\" style=\"width:auto\">")
                 .Append(CustomerReport.H(CustomerReport.When(r.StartUtc))).Append("</td><td class=\"num\">").Append(r.Seconds > 0 && r.Finished ? CustomerReport.H(Txt.DurationShort(TimeSpan.FromSeconds(r.Seconds))) : "")
                 .Append("</td><td class=\"st ").Append(ReportResult.Css(r.Result)).Append("\">").Append(CustomerReport.H(r.Result)).Append("</td></tr>");
            }
            b.Append("</tbody></table>");
            if (rows.Count > MaxTable) b.Append("<p class=\"note\">The first ").Append(Int(MaxTable)).Append(" of ").Append(Int(rows.Count)).Append(" devices are listed.</p>");
        }

        // Which items failed most and which took longest, over the devices whose session was read.
        static void Items(StringBuilder b, List<BatchRow> rows, DateTime nowUtc)
        {
            List<Session> sessions = new List<Session>();
            foreach (BatchRow r in rows) if (r.S != null && r.S.Items.Count > 0) sessions.Add(r.S);
            if (sessions.Count == 0) return;
            FleetStats st = FleetStats.Build(sessions, nowUtc);
            List<FleetItem> fail = st.Items.FindAll(delegate (FleetItem f) { return f.Failed > 0; });
            fail.Sort(delegate (FleetItem x, FleetItem y)
            {
                int c = y.Failed.CompareTo(x.Failed);
                return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            List<FleetItem> slow = st.Items.FindAll(delegate (FleetItem f) { return f.Timed > 0 && f.AvgSeconds >= 1; });
            slow.Sort(delegate (FleetItem x, FleetItem y)
            {
                int c = y.AvgSeconds.CompareTo(x.AvgSeconds);
                return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            b.Append("<h2>Most failing items</h2>");
            if (fail.Count == 0) b.Append("<p class=\"note\">No item failed on any device.</p>");
            else
            {
                b.Append("<table><thead><tr><th>Item</th><th>Type</th><th class=\"num\">Failed on</th><th>Example</th></tr></thead><tbody>");
                for (int k = 0; k < fail.Count && k < TopItems; k++)
                    b.Append("<tr><td>").Append(E(fail[k].Name)).Append("</td><td>").Append(E(fail[k].Kind)).Append("</td><td class=\"num bad\">").Append(Int(fail[k].Failed)).Append(" of ").Append(Int(fail[k].Devices)).Append("</td><td>").Append(E(fail[k].FailedOn)).Append("</td></tr>");
                b.Append("</tbody></table>");
            }
            b.Append("<h2>Slowest steps</h2>");
            if (slow.Count == 0) b.Append("<p class=\"note\">No times were measured.</p>");
            else
            {
                b.Append("<table><thead><tr><th>Item</th><th>Type</th><th class=\"num\">Average</th><th class=\"num\">Longest</th><th>Longest on</th></tr></thead><tbody>");
                for (int k = 0; k < slow.Count && k < TopItems; k++)
                    b.Append("<tr><td>").Append(E(slow[k].Name)).Append("</td><td>").Append(E(slow[k].Kind)).Append("</td><td class=\"num\">").Append(E(Txt.Duration(TimeSpan.FromSeconds(slow[k].AvgSeconds)))).Append("</td><td class=\"num\">")
                     .Append(E(Txt.Duration(TimeSpan.FromSeconds(slow[k].MaxSeconds)))).Append("</td><td>").Append(E(slow[k].SlowestOn)).Append("</td></tr>");
                b.Append("</tbody></table>");
            }
            if (sessions.Count < rows.Count) b.Append("<p class=\"note\">Items are counted over the ").Append(Int(sessions.Count)).Append(" of ").Append(Int(rows.Count)).Append(" devices whose details were read.</p>");
        }
    }
}
