namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;

    public static class CustomerReportTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Results();
            Numbers();
            Escaping();
            Masking();
            Ordering();
            Footer();
            Batch();
            BatchRows();
            Pdf();
        }

        static Obs O(string key, string name, string kind, string phase, double min, State st, long? exit)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddMinutes(min);
            o.Source = "test";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = kind;
            o.PhaseKey = phase;
            o.State = st;
            o.ExitCode = exit;
            o.Text = "at minute " + min;
            return o;
        }

        static Session S(string computer, params Obs[] obs)
        {
            Session s = new Session();
            s.Computer = computer;
            s.OsVersion = "Windows 11 Enterprise";
            Tracker t = new Tracker(s);
            t.ApplyAll(new List<Obs>(obs));
            return s;
        }

        static Session Good(string name)
        {
            return S(name,
                O("a", "Company Portal", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null),
                O("a", "Company Portal", Kinds.Win32App, Phases.IntuneApps, 5, State.Succeeded, 0),
                O("b", "Microsoft 365 Apps", Kinds.Win32App, Phases.IntuneApps, 5, State.Installing, null),
                O("b", "Microsoft 365 Apps", Kinds.Win32App, Phases.IntuneApps, 25, State.Succeeded, 0));
        }

        static CustomerReportOptions Opt()
        {
            CustomerReportOptions o = new CustomerReportOptions();
            o.Customer = "Contoso";
            o.PreparedBy = "Anna Hansen";
            o.Version = "9.9.9";
            return o;
        }

        static void Results()
        {
            DateTime now = T0.AddMinutes(40);
            T.Eq(ReportResult.Done, CustomerReport.ResultOf(Good("A")), "all done");
            Session withFail = S("B",
                O("a", "Company Portal", Kinds.Win32App, Phases.IntuneApps, 0, State.Succeeded, 0),
                O("b", "Fabrikam CRM", Kinds.Win32App, Phases.IntuneApps, 1, State.Failed, 1603));
            T.Eq(ReportResult.DoneWithFailures, CustomerReport.ResultOf(withFail), "a failed app after setup");
            Session esp = S("C",
                O("a", "Company Portal", Kinds.Win32App, Phases.AccountSetup, 0, State.Succeeded, 0),
                O("b", "Fabrikam CRM", Kinds.Win32App, Phases.AccountSetup, 1, State.Failed, 1603));
            T.Eq(ReportResult.Failed, CustomerReport.ResultOf(esp), "a failed app the ESP waits for");
            Session running = S("D", O("a", "Company Portal", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null));
            T.Eq(ReportResult.Running, CustomerReport.ResultOf(running), "something still installs");
            T.Eq(ReportResult.None, CustomerReport.ResultOf(S("E")), "nothing found");
            string h = CustomerReport.Html(esp, now, Opt());
            T.Check(h.Contains("class=\"badge bad\">Failed<"), "failed badge");
            h = CustomerReport.Html(withFail, now, Opt());
            T.Check(h.Contains("class=\"badge warn\">Done with failures<"), "done with failures badge");
            h = CustomerReport.Html(Good("F"), now, Opt());
            T.Check(h.Contains("class=\"badge ok\">Done<"), "done badge");
        }

        static void Numbers()
        {
            Session s = Good("CONTOSO-LT0417");
            s.SetInfo(DeviceFacts.Serial, "5CG2041KQZ");
            s.SetInfo("Autopilot profile", "Contoso user-driven");
            string h = CustomerReport.Html(s, T0.AddMinutes(40), Opt());
            T.Check(h.Contains("<div class=\"cust\">Contoso</div>"), "customer name");
            T.Check(h.Contains("<h1>CONTOSO-LT0417</h1>"), "device name");
            T.Check(h.Contains("5CG2041KQZ"), "serial");
            T.Check(h.Contains("Contoso user-driven"), "profile");
            T.Check(h.Contains("25 min 00 s"), "total time");
            T.Check(h.Contains("20 min 00 s"), "duration of the slow app");
            T.Check(h.Contains("2 of 2 items finished"), "item count");
            T.Check(h.Contains("<svg class=\"tl\""), "time line is an inline svg");
            T.Check(!h.Contains("<script"), "no script");
            T.Check(!Regex.IsMatch(h, "NaN|Infinity"), "no NaN");
        }

        static void Escaping()
        {
            Session s = S("PC-<b>&\"1",
                O("x", "<script>alert(\"x\")</script> & co", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null),
                O("x", "<script>alert(\"x\")</script> & co", Kinds.Win32App, Phases.IntuneApps, 4, State.Failed, 1603));
            CustomerReportOptions o = Opt();
            o.Customer = "Contoso <i>&</i>";
            string h = CustomerReport.Html(s, T0.AddMinutes(5), o);
            T.Check(!h.Contains("<script>alert"), "item name escaped");
            T.Check(h.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; co"), "item name text");
            T.Check(h.Contains("PC-&lt;b&gt;&amp;&quot;1"), "device name escaped");
            T.Check(h.Contains("Contoso &lt;i&gt;&amp;&lt;/i&gt;"), "customer escaped");
        }

        static void Masking()
        {
            Session s = S("PC-M",
                O("x", "Setup.exe -Password hunter2xyz", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null),
                O("x", "Setup.exe -Password hunter2xyz", Kinds.Win32App, Phases.IntuneApps, 2, State.Failed, 1));
            Finding f = new Finding();
            f.Title = "Install failed";
            f.Message = "The command used token=abcdef123456 and failed.";
            f.Severity = 2;
            s.Findings.Add(f);
            s.Notes.Add("client_secret=topsecretvalue");
            string h = CustomerReport.Html(s, T0.AddMinutes(5), Opt());
            T.Check(!h.Contains("hunter2xyz"), "password in an item name is masked");
            T.Check(!h.Contains("abcdef123456"), "token in a finding is masked");
            T.Check(h.Contains("***"), "the mask is shown");
        }

        static void Ordering()
        {
            Session s = S("PC-O",
                O("a", "Fast app", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null),
                O("a", "Fast app", Kinds.Win32App, Phases.IntuneApps, 1, State.Succeeded, 0),
                O("b", "Slow app", Kinds.Win32App, Phases.IntuneApps, 1, State.Installing, null),
                O("b", "Slow app", Kinds.Win32App, Phases.IntuneApps, 31, State.Succeeded, 0),
                O("c", "Failed app", Kinds.Win32App, Phases.IntuneApps, 31, State.Installing, null),
                O("c", "Failed app", Kinds.Win32App, Phases.IntuneApps, 31.1, State.Failed, 1603),
                O("d", "Middle app", Kinds.Win32App, Phases.IntuneApps, 31, State.Installing, null),
                O("d", "Middle app", Kinds.Win32App, Phases.IntuneApps, 33, State.Succeeded, 0));
            DateTime now = T0.AddMinutes(40);
            string h = CustomerReport.Html(s, now, Opt());
            int failed = h.IndexOf("<td>Failed app</td>", StringComparison.Ordinal), slow = h.IndexOf("<td>Slow app</td>", StringComparison.Ordinal);
            int middle = h.IndexOf("<td>Middle app</td>", StringComparison.Ordinal), fast = h.IndexOf("<td>Fast app</td>", StringComparison.Ordinal);
            T.Check(failed > 0 && slow > failed, "failed before slow");
            T.Check(middle > slow && fast > middle, "slow before the rest, the longest first");
            List<Item> o = CustomerReport.Ordered(new List<Item>(s.Items), now);
            T.Eq("Failed app", o[0].DisplayName, "Ordered: failed first");
            T.Eq("Slow app", o[1].DisplayName, "Ordered: slow second");
            T.Check(h.Contains("A fatal error"), "the meaning of the exit code");
        }

        static void Footer()
        {
            CustomerReportOptions o = Opt();
            o.PreparedBy = "Anna <b>";
            string h = CustomerReport.Html(Good("PC-F"), T0.AddMinutes(40), o);
            T.Check(h.Contains("Prepared by Anna &lt;b&gt; with TrackVice 9.9.9 on "), "footer text");
            T.Eq(1, Regex.Matches(h, "<footer>").Count, "one footer on a device report");
            o.Footer = false;
            T.Check(!CustomerReport.Html(Good("PC-F"), T0.AddMinutes(40), o).Contains("<footer>"), "no footer when not wanted");

            // Outages are listed by when they happened, also over midnight, and the footer stays with them.
            Session s = Good("PC-N");
            NetOutage late = new NetOutage(), early = new NetOutage();
            DateTime evening = DateTime.SpecifyKind(new DateTime(2026, 11, 12, 23, 50, 0), DateTimeKind.Local).ToUniversalTime();
            late.FromUtc = evening.AddMinutes(20); late.ToUtc = evening.AddMinutes(25); late.Reason = "second outage";
            early.FromUtc = evening; early.ToUtc = evening.AddMinutes(3); early.Reason = "first outage";
            s.Outages.Add(late);
            s.Outages.Add(early);
            o.Footer = true;
            h = CustomerReport.Html(s, evening.AddHours(1), o);
            int a = h.IndexOf("first outage", StringComparison.Ordinal), z = h.IndexOf("second outage", StringComparison.Ordinal);
            T.Check(a > 0 && z > a, "outages in the order they happened");
            T.Check(h.IndexOf("<div class=\"keep\"><h2>Restarts and network outages", StringComparison.Ordinal) > 0 && h.Contains("</footer></div></section>"), "footer kept with the last block");
        }

        static BatchRow Row(string device, string result, double hours, double secs, Session s)
        {
            BatchRow r = new BatchRow();
            r.Device = device;
            r.Result = result;
            r.StartUtc = T0.AddHours(hours);
            r.Seconds = secs;
            r.S = s;
            r.User = "user@contoso.com";
            r.Profile = "Contoso user-driven";
            return r;
        }

        static void Batch()
        {
            List<double> sorted = new List<double> { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
            T.Eq(100.0, CustomerReportBatch.Percentile(sorted, 0.9) + 10, "p90 by the nearest rank");
            T.Eq(0.0, CustomerReportBatch.Percentile(new List<double>(), 0.9), "p90 of nothing");

            List<BatchRow> rows = new List<BatchRow>();
            rows.Add(Row("LT-1", ReportResult.Done, 0, 1200, Good("LT-1")));
            rows.Add(Row("LT-2", ReportResult.Done, 1, 1800, Good("LT-2")));
            rows.Add(Row("LT-3", ReportResult.DoneWithFailures, 2, 2400, Good("LT-3")));
            rows.Add(Row("LT-<4>&", ReportResult.Failed, 26, 3000, Good("LT-<4>&")));
            rows.Add(Row("LT-5", ReportResult.Running, 27, 0, null));
            CustomerReportBatch.Numbers n = CustomerReportBatch.Count(rows);
            T.Eq(5, n.Devices, "devices");
            T.Eq(2, n.Done, "done");
            T.Eq(1, n.WithFailures, "with failures");
            T.Eq(1, n.Failed, "failed");
            T.Eq(1, n.Unfinished, "unfinished");
            T.Eq(50.0, n.Rate, "success rate counts only clean runs of those that ended");
            T.Eq(2100.0, n.Median, "median of the finished");
            T.Eq(3000.0, n.P90, "p90 of the finished");

            BatchOptions o = new BatchOptions();
            o.Customer = "Fabrikam";
            o.PeriodText = "Last 7 days";
            o.FromDay = T0.ToLocalTime().Date;
            o.ToDay = T0.ToLocalTime().Date.AddDays(2);
            string h = CustomerReportBatch.Html(rows, o, T0.AddDays(2));
            T.Check(h.Contains("<div class=\"cust\">Fabrikam</div>"), "customer");
            T.Check(h.Contains("<b>5</b><span>Devices</span>"), "devices KPI");
            T.Check(h.Contains("<b>50 %</b><span>Success rate</span>"), "success rate KPI");
            T.Check(h.Contains("<b>35 min</b><span>Median time</span>"), "median KPI");
            T.Check(h.Contains("<b>50 min</b>"), "p90 KPI");
            T.Check(h.Contains("<svg class=\"days\""), "per-day chart");
            T.Check(!h.Contains("LT-<4>"), "device name escaped");
            T.Check(h.Contains("LT-&lt;4&gt;&amp;"), "device name text");
            T.Check(Regex.Matches(h, "class=\"dev pb\"").Count == 0, "no device pages unless asked");
            T.Check(h.Contains("Company Portal") && h.Contains("Slowest steps"), "slowest steps");
            o.PerDevice = true;
            h = CustomerReportBatch.Html(rows, o, T0.AddDays(2));
            T.Eq(4, Regex.Matches(h, "class=\"dev pb\"").Count, "a page per device with a session");
            T.Check(h.Contains("1 device has no details"), "devices without details are counted");
            T.Check(h.Contains("<a href=\"#d4\">LT-&lt;4&gt;&amp;</a>") && h.Contains("class=\"dev pb\" id=\"d4\""), "the table leads to the page of the device");
            T.Check(!h.Contains("href=\"#d5\""), "no link without a page");
            T.Check(h.IndexOf("<footer>", StringComparison.Ordinal) > h.LastIndexOf("class=\"dev pb\"", StringComparison.Ordinal), "the footer ends the last page");
            T.Eq(1, Regex.Matches(h, "<footer>").Count, "one footer");
            string empty = CustomerReportBatch.Html(new List<BatchRow>(), o, T0);
            T.Check(empty.Contains("No deployments were found"), "empty period");
            T.Check(!Regex.IsMatch(h + empty, "NaN|Infinity"), "no NaN in batch");
        }

        static void BatchRows()
        {
            OverviewSnapshot snap = new OverviewSnapshot();
            snap.Devices.Add(Dev("Contoso", "A", T0, State.Succeeded));
            snap.Devices.Add(Dev("Contoso", "B", T0.AddDays(-3), State.Failed));
            snap.Devices.Add(Dev("Fabrikam", "C", T0, State.Succeeded));
            OverviewFilter f = new OverviewFilter();
            f.Customer = "Contoso";
            List<BatchRow> r = CustomerReportBatch.Rows(snap, f, T0.AddDays(-1), T0.AddDays(1), T0.AddDays(1));
            T.Eq(1, r.Count, "the filter and the period");
            T.Eq("A", r[0].Device, "device");
            T.Eq(ReportResult.Done, r[0].Result, "result from the list entry");
            r = CustomerReportBatch.Rows(snap, null, T0.AddDays(-7), T0.AddDays(1), T0.AddDays(1));
            T.Eq(3, r.Count, "no filter");
            T.Eq("B", r[0].Device, "oldest first");
            T.Eq(ReportResult.Failed, r[0].Result, "failed from the list entry");
        }

        static OverviewDevice Dev(string customer, string name, DateTime start, State st)
        {
            OverviewDevice o = new OverviewDevice();
            o.Customer = customer;
            o.Kind = OverviewKinds.Agents;
            o.D = new RemoteDeployment();
            o.D.Device = name;
            o.D.StartUtc = start;
            o.D.EndUtc = start.AddMinutes(30);
            o.D.LastUtc = start.AddMinutes(30);
            o.D.State = st;
            return o;
        }

        static void Pdf()
        {
            Func<string, bool> has = delegate (string p) { return p.Replace('\\', '/').StartsWith("/pf86/", StringComparison.Ordinal); };
            string edge = PdfPrint.FindEdge(has, "/pf86", "/pf");
            T.Check(edge != null && edge.Replace('\\', '/').EndsWith("Microsoft/Edge/Application/msedge.exe"), "Edge found under Program Files (x86)");
            T.Check(PdfPrint.FindEdge(delegate (string p) { return false; }, "/pf86", "/pf") == null, "no Edge");
            string a = PdfPrint.Arguments("/t/in.html", "/t/out.pdf", null);
            T.Eq("--headless --disable-gpu --no-pdf-header-footer --print-to-pdf=\"/t/out.pdf\" \"/t/in.html\"", a, "Edge arguments");

            string dir = Path.Combine(Path.GetTempPath(), "tv-pdf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string html = Path.Combine(dir, "r.html"), pdf = Path.Combine(dir, "r.pdf");
                File.WriteAllText(html, "<html></html>");
                string seen = null;
                PdfResult r = PdfPrint.Make("msedge.exe", html, pdf, delegate (string exe, string args, int ms)
                {
                    seen = exe + "|" + args + "|" + ms;
                    File.WriteAllText(pdf, "%PDF-1.4");
                    return 0;
                });
                T.Check(r.Ok, "PDF made");
                T.Check(seen != null && seen.StartsWith("msedge.exe|--headless --disable-gpu --no-pdf-header-footer", StringComparison.Ordinal) && seen.EndsWith("|60000"), "runner got the arguments and 60 s");
                File.Delete(pdf);
                r = PdfPrint.Make("msedge.exe", html, pdf, delegate (string exe, string args, int ms) { return (int?)null; });
                T.Check(!r.Ok && r.Message.Contains("60 seconds") && r.Message.Contains("HTML"), "timeout keeps the HTML and says so");
                r = PdfPrint.Make("msedge.exe", html, pdf, delegate (string exe, string args, int ms) { return 1; });
                T.Check(!r.Ok && r.Message.Contains("code 1"), "exit code without a PDF");
                r = PdfPrint.Make("msedge.exe", html, pdf, delegate (string exe, string args, int ms) { throw new InvalidOperationException("no way"); });
                T.Check(!r.Ok && r.Message.Contains("no way"), "start failure");
                r = PdfPrint.Make(null, html, pdf, null);
                T.Check(!r.Ok && r.Message.Contains("not found"), "no Edge");
                File.WriteAllText(pdf, "stale");
                r = PdfPrint.Make("msedge.exe", html, pdf, delegate (string exe, string args, int ms) { return 0; });
                T.Check(!r.Ok, "a stale PDF is not taken for a new one");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
