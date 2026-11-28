namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;

    // What TrackVice writes out for other people: reports keep to themselves, CSV cells never run, secrets and customer
    // codes are masked, bundles only take this PC's logs, and Edge only prints a copy TrackVice made.
    public static class ShareSecurityTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);
        const string Code = "tv1:eyJuIjoiQ29udG9zbyIsInUiOiJodHRwczovL2EuYmxvYi5jb3JlLndpbmRvd3MubmV0L2M_c2lnPWFiYyJ9";
        const string Sas = "https://acct.blob.core.windows.net/c?sv=2024-11-04&sr=c&sp=cw&sig=SasSecretAbc123%2BXyz%3D";
        const string Evil = "\"><script>alert(1)</script><img src=x onerror=alert(2)><a href=\"javascript:alert(3)\">";

        public static void Run()
        {
            Mask();
            Csp();
            Injection();
            ReportMasking();
            CsvCells();
            CompareCsv();
            Bundle();
            BundlePaths();
            FeedbackMasking();
            PdfOwnCopy();
        }

        static Obs O(string key, string name, double min, State st, string text)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddMinutes(min);
            o.Source = "test";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = Kinds.Win32App;
            o.PhaseKey = Phases.DeviceSetup;
            o.State = st;
            o.Text = text;
            return o;
        }

        // A session with hostile names and secrets in its texts, as an upload could carry them.
        static Session Hostile()
        {
            Session s = new Session();
            s.Computer = "PC" + Evil;
            s.OsVersion = "Windows 11" + Evil;
            s.User = "anna@contoso.com";
            Tracker t = new Tracker(s);
            t.ApplyAll(new List<Obs>
            {
                O("a1", "App" + Evil, 0, State.Installing, "setup.exe /password Hunter2Secret /quiet"),
                O("a1", "App" + Evil, 5, State.Failed, "TrackVice.exe /agent-install /customer \"" + Code + "\""),
                O("a2", "=HYPERLINK(\"http://x\")", 1, State.Installing, "Downloading from " + Sas),
                O("a2", "=HYPERLINK(\"http://x\")", 9, State.Succeeded, "Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOi.abcdefghijk.lmnopq"),
            });
            Finding f = new Finding();
            f.Kind = FindingKinds.Failed;
            f.Severity = 2;
            f.Title = "Failed" + Evil;
            f.Message = "The command was msiexec /i x.msi PASSWORD=\"Hunter2Secret\" with " + Code;
            f.Hint = "Look" + Evil;
            s.Findings.Add(f);
            s.Notes.Add("note " + Code);
            s.Info.Add(new Pair("Upload" + Evil, Sas));
            return s;
        }

        static bool Leaks(string text)
        {
            return text.Contains("Hunter2Secret") || text.Contains(Code.Substring(4)) || text.Contains("SasSecretAbc123") || text.Contains("abcdefghijk");
        }

        static void Mask()
        {
            T.Eq("x /customer \"tv1:***\"", ShareMask.Text("x /customer \"" + Code + "\""), "mask: customer code");
            T.Check(!Leaks(ShareMask.Text(Sas)), "mask: SAS signature");
            T.Check(!Leaks(ShareMask.Text("Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOi.abcdefghijk.lmnopq")), "mask: bearer token");
            T.Check(!Leaks(ShareMask.Text("setup.exe /password Hunter2Secret /quiet")), "mask: password in a command line");
            T.Eq("tv1: is the prefix", ShareMask.Text("tv1: is the prefix"), "mask: the word alone stays");
            T.Eq("Contoso VPN 2.1", ShareMask.Text("Contoso VPN 2.1"), "mask: plain text stays");
            T.Check(ShareMask.Text(null) == null && ShareMask.Text("") == "", "mask: null and empty");
            string t = Ticket.Text(Hostile(), T0.AddHours(1), TimeSpan.Zero, null, "see " + Code);
            T.Check(!Leaks(t) && t.Contains("tv1:***"), "ticket: customer code masked: " + t);
            T.Eq("call with tv1:***", DeviceNotes.Clean("call with " + Code), "device note: customer code masked");
        }

        static string Head(string html)
        {
            int i = html.IndexOf("</head>", StringComparison.Ordinal);
            return i < 0 ? "" : html.Substring(0, i);
        }

        static void Csp()
        {
            Session s = Hostile();
            DateTime now = T0.AddHours(1);
            List<KeyValuePair<string, string>> pages = new List<KeyValuePair<string, string>>();
            pages.Add(new KeyValuePair<string, string>("report", Report.Html(s, now)));
            pages.Add(new KeyValuePair<string, string>("fleet", Report.FleetHtml(FleetStats.Build(new List<Session> { s }, now), "Fleet" + Evil, now)));
            pages.Add(new KeyValuePair<string, string>("readiness", Report.ReadinessHtml(new List<ReadyDevice>(), "Intune" + Evil, now)));
            pages.Add(new KeyValuePair<string, string>("ship", Report.ShipHtml(s, now, null)));
            CustomerReportOptions co = new CustomerReportOptions();
            co.Customer = "Contoso" + Evil;
            co.PreparedBy = "Me" + Evil;
            pages.Add(new KeyValuePair<string, string>("customer", CustomerReport.Html(s, now, co)));
            BatchOptions bo = new BatchOptions();
            bo.Customer = "Contoso" + Evil;
            bo.PeriodText = "Today" + Evil;
            bo.FromDay = T0.Date;
            bo.ToDay = T0.Date;
            pages.Add(new KeyValuePair<string, string>("batch", CustomerReportBatch.Html(new List<BatchRow>(), bo, now)));
            foreach (KeyValuePair<string, string> p in pages)
            {
                string head = Head(p.Value);
                int csp = head.IndexOf(Report.CspMeta, StringComparison.Ordinal);
                T.Check(csp > 0 && csp < head.IndexOf("<title>", StringComparison.Ordinal), p.Key + ": a Content-Security-Policy before anything else loads");
                T.Check(Report.CspMeta.Contains("default-src 'none'") && !Report.CspMeta.Contains("script-src") && !Report.CspMeta.Contains("http:") && !Report.CspMeta.Contains("https:") && !Report.CspMeta.Contains("*"), p.Key + ": the policy forbids scripts and remote loads");
            }
        }

        static void Injection()
        {
            Session s = Hostile();
            DateTime now = T0.AddHours(1);
            CustomerReportOptions co = new CustomerReportOptions();
            co.Customer = "Contoso" + Evil;
            co.PreparedBy = "Me" + Evil;
            string[] pages =
            {
                Report.Html(s, now), Report.ShipHtml(s, now, null), CustomerReport.Html(s, now, co),
                Report.FleetHtml(FleetStats.Build(new List<Session> { s }, now), "Fleet" + Evil, now),
            };
            foreach (string html in pages)
            {
                string lower = html.ToLowerInvariant();
                T.Check(lower.IndexOf("<script", StringComparison.Ordinal) < 0 && lower.IndexOf("<img", StringComparison.Ordinal) < 0 && lower.IndexOf("href=\"javascript", StringComparison.Ordinal) < 0,
                    "injection: hostile names stay text");
                // In tags only: the hostile text itself, escaped, still reads "src=" as text.
                T.Check(!System.Text.RegularExpressions.Regex.IsMatch(lower, "<[a-z][a-z0-9]*(\\s+[a-z\\-:]+(=\"[^\"]*\")?)*\\s+(src|href|action|formaction|srcset|xlink:href)\\s*=") && lower.IndexOf("url(", StringComparison.Ordinal) < 0 && lower.IndexOf("@import", StringComparison.Ordinal) < 0,
                    "injection: nothing is loaded from elsewhere");
            }
        }

        static void ReportMasking()
        {
            Session s = Hostile();
            DateTime now = T0.AddHours(1);
            string html = Report.Html(s, now);
            T.Check(!Leaks(html), "report: secrets and customer codes masked in the HTML report");
            T.Check(html.Contains("tv1:***") && html.Contains("/password ***"), "report: the masked texts are still there");
            T.Check(!Leaks(Report.ShipHtml(s, now, null)), "ship report: masked");
            T.Check(!Leaks(CustomerReport.Html(s, now, new CustomerReportOptions())), "customer report: customer code masked");
            string dir = Path.Combine(Path.GetTempPath(), "tv-share-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string csv = Path.Combine(dir, "e.csv");
                Report.Csv(s, csv);
                T.Check(!Leaks(File.ReadAllText(csv)), "events csv: masked");
                List<Session> l = new List<Session> { s };
                string data = DataExport.EnrollmentsCsv(l, null, now) + DataExport.ItemsCsv(l, null, now) + DataExport.FindingsCsv(l, null);
                T.Check(!Leaks(data), "data export: masked");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void CsvCells()
        {
            T.Eq("\"'\t=1+1\"", Report.Q("\t=1+1"), "csv: a leading tab is not a formula");
            T.Eq("\"' =1+1\"", Report.Q("\r=1+1"), "csv: a leading carriage return is not a formula");
            T.Eq("\"'=1+1\"", Report.Q("=1+1"), "csv: a leading = is not a formula");
            T.Eq("\"Contoso\"", Report.Q("Contoso"), "csv: plain text");
            T.Eq("\"\"", Report.Q(null), "csv: empty");
        }

        static void CompareCsv()
        {
            Session a = Hostile();
            Session b = Hostile();
            a.Computer = "PC1\r\n=cmd|' /C calc'!A0";
            b.Computer = "PC2";
            List<DiffRow> rows = SessionDiff.Compare(a, T0.AddHours(1), b, T0.AddHours(2));
            DiffSummary m = SessionDiff.Summarize(a, T0.AddHours(1), b, T0.AddHours(2), rows);
            string csv = SessionDiff.ToCsv(rows, m);
            string[] lines = csv.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool startsBad = false;
            foreach (string line in lines) if (line.Length > 0 && "=+-@\t\r".IndexOf(line[0]) >= 0) startsBad = true;
            T.Check(!startsBad, "compare csv: a device name cannot start a line of its own");
            T.Eq(rows.Count + 1, lines.Length, "compare csv: one line per row and the header");
            T.Check(csv.Contains("\"'=HYPERLINK("), "compare csv: an item name that is a formula gets a '");
            T.Check(!csv.Contains(";\"=") && !csv.Contains(";\"+") && !csv.Contains(";\"@"), "compare csv: no cell starts with a formula");
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "tv-bundle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static List<string> Entries(string zip)
        {
            List<string> names = new List<string>();
            using (FileStream fs = File.OpenRead(zip))
            using (ZipArchive a = new ZipArchive(fs, ZipArchiveMode.Read)) foreach (ZipArchiveEntry e in a.Entries) names.Add(e.Name);
            return names;
        }

        static Session WithLog(string mode, string computer, string log)
        {
            Session s = new Session();
            s.Mode = mode;
            s.Computer = computer;
            TEvent e = new TEvent();
            e.TimeUtc = T0;
            e.Text = "x";
            e.Evidence = new Evidence(EvidenceKinds.LogLine, log, 1, "x");
            s.Events.Add(e);
            return s;
        }

        static void Bundle()
        {
            string dir = TempDir();
            try
            {
                string log = Path.Combine(dir, "Private.log");
                File.WriteAllText(log, "this PC's own log");
                string extraDir = Path.Combine(dir, "ime");
                Directory.CreateDirectory(extraDir);
                File.WriteAllText(Path.Combine(extraDir, "IntuneManagementExtension.log"), "this PC's IME");
                string[][] extra = { new string[] { extraDir, "*.log" } };
                string zip = Path.Combine(dir, "b.zip");

                // An agent upload names files of its device: none of this PC's files go into its bundle.
                int n = Report.Bundle(WithLog(Modes.Agent, "CONTOSO-PC01", log), zip, extra);
                List<string> names = Entries(zip);
                T.Eq(0, n, "bundle of an upload: no files of this PC");
                T.Check(names.Contains("session.json") && !names.Contains("Private.log") && !names.Contains("IntuneManagementExtension.log"), "bundle of an upload: session only: " + string.Join(",", names.ToArray()));
                foreach (string mode in new string[] { Modes.Intune, Modes.Sccm, Modes.Demo, "Reference" })
                    T.Eq(0, Report.Bundle(WithLog(mode, "CONTOSO-PC01", log), zip, extra), "bundle of a " + mode + " session: no files of this PC");
                // A saved session of another PC is the same.
                T.Eq(0, Report.Bundle(WithLog(Modes.File, "OTHER-PC-" + Guid.NewGuid().ToString("N").Substring(0, 6), log), zip, extra), "bundle of another PC's saved session: no files of this PC");
                // This PC, live, from a folder, or a saved session of this PC: its logs are bundled as before.
                T.Eq(2, Report.Bundle(WithLog(Modes.Live, Environment.MachineName, log), zip, extra), "bundle of this PC: its logs");
                T.Eq(2, Report.Bundle(WithLog(Modes.File, Environment.MachineName, log), zip, extra), "bundle of a saved session of this PC: its logs");
                Session folder = WithLog(Modes.Folder, "logs", log);
                folder.SourcePath = dir;
                T.Eq(2, Report.Bundle(folder, zip, extra), "bundle of a folder of logs: its logs");
                // Only log files, even on this PC.
                string other = Path.Combine(dir, "secrets.json");
                File.WriteAllText(other, "{}");
                T.Eq(0, Report.Bundle(WithLog(Modes.Live, Environment.MachineName, other), zip, null), "bundle: a file that is not a log is not taken");
                // The session in the viewer's bundle is masked; the mode survives the masked copy.
                Session live = WithLog(Modes.Live, Environment.MachineName, log);
                live.Events[0].Text = "setup.exe /password Hunter2Secret";
                Session masked = Secrets.MaskedCopy(live);
                T.Eq(Modes.Live, masked.Mode, "masked copy keeps the mode");
                Report.Bundle(masked, zip, null);
                using (FileStream fs = File.OpenRead(zip))
                using (ZipArchive a = new ZipArchive(fs, ZipArchiveMode.Read))
                using (StreamReader r = new StreamReader(a.GetEntry("session.json").Open()))
                    T.Check(!r.ReadToEnd().Contains("Hunter2Secret"), "bundle of a masked copy: the session is masked");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void BundlePaths()
        {
            Session live = new Session();
            live.Mode = Modes.Live;
            T.Check(Report.LogPathOk(live, @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\IntuneManagementExtension.log"), "log path: IME log");
            T.Check(Report.LogPathOk(live, @"C:\Windows\CCM\Logs\AppEnforce.lo_"), "log path: rolled CCM log");
            T.Check(!Report.LogPathOk(live, @"\\attacker.example\share\x.log"), "log path: no share from a session (would send the user's credentials)");
            T.Check(!Report.LogPathOk(live, "//attacker.example/share/x.log"), "log path: no share with forward slashes");
            T.Check(!Report.LogPathOk(live, @"\\?\UNC\attacker.example\share\x.log"), "log path: no device path");
            T.Check(!Report.LogPathOk(live, @"\\.\C:\x.log"), "log path: no device path with a dot");
            T.Check(!Report.LogPathOk(live, @"C:\Users\admin\x.log:secret"), "log path: no stream");
            T.Check(!Report.LogPathOk(live, @"C:\Windows\CCM\Logs\..\..\..\Users\admin\x.log"), "log path: no ..");
            T.Check(!Report.LogPathOk(live, @"C:\Users\admin\AppData\Local\TrackVice\customers.dat"), "log path: only log files");
            T.Check(!Report.LogPathOk(live, @"x.log"), "log path: no relative path");
            T.Check(!Report.LogPathOk(live, "a") && !Report.LogPathOk(live, null) && !Report.LogPathOk(live, ""), "log path: short or empty");
            Session folder = new Session();
            folder.Mode = Modes.Folder;
            folder.SourcePath = @"\\fileserver\logs\PC01";
            T.Check(Report.LogPathOk(folder, @"\\fileserver\logs\PC01\smsts.log"), "log path: a share inside the folder of logs that was opened");
            T.Check(!Report.LogPathOk(folder, @"\\fileserver\logs\PC02\smsts.log"), "log path: not a share outside it");
            T.Check(!Report.LogPathOk(folder, @"\\fileserver\logs\PC01\..\PC02\smsts.log"), "log path: not out of it with ..");
            Session file = new Session();
            file.Mode = Modes.File;
            file.SourcePath = @"\\attacker.example\share";
            T.Check(!Report.LogPathOk(file, @"\\attacker.example\share\x.log"), "log path: a saved session cannot open a share");
            T.Check(!Report.ExtraDirOk(@"X:\Windows\Temp\SMSTSLog", "C:"), "extra logs: X: on a running Windows is not read");
            T.Check(Report.ExtraDirOk(@"X:\Windows\Temp\SMSTSLog", "X:"), "extra logs: X: in Windows PE is read");
            T.Check(Report.ExtraDirOk(@"C:\Windows\CCM\Logs", "C:"), "extra logs: CCM logs");
            T.Check(!Report.ReadHere(null), "read here: no session");
        }

        static void FeedbackMasking()
        {
            ProblemReport r = ProblemReport.Make("2.1.0", "Windows 11", "This PC", new List<string> { "Upload failed: " + Code }, null);
            r.What = "It failed with setup.exe /password Hunter2Secret and " + Code;
            r.Steps = "Ran TrackVice.exe /agent-install /customer " + Code;
            string issue = Uri.UnescapeDataString(r.IssueUrl());
            string mail = Uri.UnescapeDataString(r.MailUrl());
            T.Check(!Leaks(issue) && !Leaks(mail), "bug report: typed secrets and customer codes are masked in the links");
            T.Check(!Leaks(Feedback.Body(r.Fields())), "bug report: and in the copied text");
            T.Check(!Leaks(r.Title), "bug report: and in the title");
            FeatureWish w = new FeatureWish();
            w.What = "Accept " + Code + " on the command line";
            T.Check(!Leaks(Uri.UnescapeDataString(w.IssueUrl())), "feature wish: customer code masked");
            T.Check(!Leaks(Feedback.Scrub("Read failed for " + Code, null)), "scrub: customer code masked");
            T.Check(r.IssueUrl().Length <= Feedback.MaxIssueUrl && r.MailUrl().Length <= Feedback.MaxMailUrl, "bug report: links within their caps");
        }

        // Edge only opens a file:// copy of the report in a new temp folder of its own, writes the PDF there, and that
        // folder, with Edge's profile, is gone afterwards. The report's own folder (here a name with a space and a #) is
        // never handed to Edge.
        static void PdfOwnCopy()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv pdf #" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string html = Path.Combine(dir, "r.html"), pdf = Path.Combine(dir, "r.pdf");
                File.WriteAllText(html, "<html>report</html>");
                string args = null, page = null, work = null, copied = null;
                bool profileInside = false;
                PdfResult r = PdfPrint.Make("msedge.exe", html, pdf, delegate (string exe, string a, int ms)
                {
                    args = a;
                    page = a.Substring(a.LastIndexOf(" \"", StringComparison.Ordinal) + 2).TrimEnd('"');
                    string outPdf = CustomerReportTests.PdfOut(a);
                    work = Path.GetDirectoryName(outPdf);
                    profileInside = a.Contains("--user-data-dir=\"" + Path.Combine(work, PdfPrint.ProfileName) + "\"");
                    copied = File.ReadAllText(new Uri(page).LocalPath);
                    File.WriteAllText(outPdf, "%PDF-1.4");
                    return 0;
                });
                T.Check(r.Ok, "pdf: made from the copy");
                T.Check(page != null && page.StartsWith("file:///", StringComparison.Ordinal) && !page.Contains(" ") && !page.Contains("#"), "pdf: Edge gets an escaped file:// address: " + page);
                T.Check(args != null && !args.Contains(dir), "pdf: the folder the report was saved to is not passed to Edge");
                T.Eq("<html>report</html>", copied, "pdf: the copy holds the report");
                T.Check(work != null && Path.GetFileName(work).StartsWith("TrackVice-pdf-", StringComparison.Ordinal) && !work.StartsWith(dir, StringComparison.Ordinal), "pdf: Edge works in a new temp folder: " + work);
                T.Check(profileInside, "pdf: Edge's profile is inside that folder");
                T.Check(work != null && !Directory.Exists(work), "pdf: the temp folder and profile are removed");
                T.Check(args != null && args.Contains("--disable-background-networking") && args.Contains("--disable-extensions") && args.Contains("--no-first-run"), "pdf: Edge is kept offline");
                T.Eq("%PDF-1.4", File.Exists(pdf) ? File.ReadAllText(pdf) : null, "pdf: copied to where the report was saved");
                File.Delete(pdf);
                bool started = false;
                r = PdfPrint.Make("msedge.exe", Path.Combine(dir, "missing.html"), pdf, delegate (string exe, string a, int ms) { started = true; return 0; });
                T.Check(!r.Ok && !started && !File.Exists(pdf), "pdf: no report, no Edge and no PDF");
                T.Check(PdfPrint.FindEdge(delegate (string p) { return true; }, "relative", null) == null, "pdf: Edge only under a full Program Files path");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
