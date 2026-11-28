namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;

    // Reads from Microsoft Graph and from the site, and what is made of them: links to next pages, values put in
    // filters and queries, names of saved files, and log lines from an upload that try to make the patterns slow.
    public static class GraphReadTests
    {
        const string Evil = "https://evil.example.com/collect";

        sealed class FakeGraph : IGraph
        {
            public readonly Dictionary<string, string> Answers = new Dictionary<string, string>();
            public readonly List<string> Asked = new List<string>();

            public Dictionary<string, object> Get(string url)
            {
                Asked.Add(url);
                string best = null;
                foreach (string k in Answers.Keys) if (url.StartsWith(k, StringComparison.Ordinal) && (best == null || k.Length > best.Length)) best = k;
                if (best == null) throw new GraphException("Not here.", 404);
                return (Dictionary<string, object>)ImeJson.Parse(Answers[best]);
            }

            public bool WentTo(string host)
            {
                foreach (string u in Asked) if (u.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
        }

        sealed class Site : ISccmQuery, ISccmGet
        {
            public readonly List<string> Wql = new List<string>();

            public List<Dictionary<string, object>> Query(string wql)
            {
                Wql.Add(wql);
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (wql.Contains("FROM SMS_ApplicationAssignment")) { r["CollectionName"] = "All laptops"; r["OfferTypeID"] = 0L; }
                else { r["CI_ID"] = 17L; r["LocalizedDisplayName"] = "x"; }
                l.Add(r);
                return l;
            }

            public Dictionary<string, object> GetInstance(string className, string keyName, string keyValue)
            {
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                r["LocalizedDisplayName"] = "Driver's Kit \\ Contoso";
                return r;
            }
        }

        static string Page(string items, string next)
        {
            return "{\"value\":[" + items + "]" + (next != null ? ",\"@odata.nextLink\":\"" + next + "\"" : "") + "}";
        }

        public static void Run()
        {
            Links();
            NextPages();
            Serials();
            Patterns();
            Files();
            Wql();
            Text();
        }

        static void Links()
        {
            T.Check(GraphLinks.Trusted("https://graph.microsoft.com/beta/deviceManagement/managedDevices?$skiptoken=x"), "graph links: Graph over HTTPS");
            T.Check(GraphLinks.Trusted("https://GRAPH.microsoft.com:443/v1.0/users"), "graph links: case and the default port");
            T.Check(!GraphLinks.Trusted("http://graph.microsoft.com/beta/x"), "graph links: not over HTTP");
            T.Check(!GraphLinks.Trusted(Evil), "graph links: not another host");
            T.Check(!GraphLinks.Trusted("https://graph.microsoft.com.evil.example.com/x"), "graph links: not a host that starts like Graph");
            T.Check(!GraphLinks.Trusted("https://graph.microsoft.com@evil.example.com/x"), "graph links: not a user name in front of another host");
            T.Check(!GraphLinks.Trusted("https://graph.microsoft.com:8443/x"), "graph links: not another port");
            T.Check(!GraphLinks.Trusted("/beta/x") && !GraphLinks.Trusted(null), "graph links: not a relative link or none");
        }

        // A next page that leaves Microsoft Graph is never asked for: the sign-in token goes with every request.
        static void NextPages()
        {
            const string Dev = "11111111-2222-3333-4444-555555555555";
            FakeGraph g = new FakeGraph();
            g.Answers[IntuneNames.ScriptsUrl] = Page("{\"id\":\"" + Dev + "\",\"displayName\":\"Map drive\"}", Evil);
            g.Answers[IntuneLookup.AppsUrl] = Page("{\"id\":\"" + Dev + "\",\"displayName\":\"Viewer\"}", Evil);
            g.Answers[IntuneLookup.DevicesUrl + "?"] = Page("{\"id\":\"" + Dev + "\",\"deviceName\":\"PC1\"}", Evil);
            LookupChoice c = new LookupChoice();
            c.Scripts = true;
            c.Apps = true;
            c.Profiles = true;
            LookupResult r = IntuneLookup.Run(g, c, "PC1", DateTime.UtcNow);
            T.Check(!g.WentTo("evil.example.com"), "next pages: names and profiles never follow a link off Graph");
            T.Eq(3, r.Problems.Count, "next pages: each lookup with such a link says so");
            T.Check(r.Summary().IndexOf("graph.microsoft.com", StringComparison.Ordinal) >= 0, "next pages: the problem says where links may go");

            g = new FakeGraph();
            g.Answers["https://graph.microsoft.com/beta/deviceManagement/managedDevices?"] = Page("{\"id\":\"" + Dev + "\",\"userId\":\"" + Dev + "\"}", Evil);
            ExpectedQuery q = new ExpectedQuery();
            q.Serial = "5CG2041KQZ";
            bool threw = false;
            try { string note; ExpectedIntune.Read(g, q, DateTime.UtcNow, out note); }
            catch (GraphException) { threw = true; }
            T.Check(threw && !g.WentTo("evil.example.com"), "next pages: expected apps do not follow a link off Graph");

            g = new FakeGraph();
            g.Answers[PreflightRun.AppsUrl] = Page("", Evil);
            threw = false;
            try { PreflightRun.ReadApps(g); }
            catch (GraphException) { threw = true; }
            T.Check(threw && !g.WentTo("evil.example.com"), "next pages: pre-flight apps do not follow a link off Graph");

            g = new FakeGraph();
            g.Answers[PreflightRun.RelationsUrl(Dev)] = Page("", "http://graph.microsoft.com/beta/next");
            threw = false;
            try { PreflightRun.ReadRelations(g, Dev); }
            catch (GraphException) { threw = true; }
            T.Check(threw && g.Asked.Count == 1, "next pages: pre-flight relationships do not follow a link over HTTP");

            g = new FakeGraph();
            g.Answers[ReadinessCheck.FindUrl("5CG2041KQZ")] = Page("{\"id\":\"a\",\"serialNumber\":\"5CG2041KQZ\"}", Evil);
            PfDevice d = new PreflightRun(g).ReadDevice("5CG2041KQZ");
            T.Check(d.Problem != null && !d.Found && !g.WentTo("evil.example.com"), "next pages: the pre-flight device read does not follow a link off Graph");
        }

        static void Serials()
        {
            FakeGraph g = new FakeGraph();
            PfDevice d = new PreflightRun(g).ReadDevice("");
            T.Check(d.Problem != null && g.Asked.Count == 0, "serials: an empty serial number does not list every Autopilot device");
            d = new PreflightRun(g).ReadDevice("5CG\r\n2041");
            T.Check(d.Problem != null && g.Asked.Count == 0, "serials: a serial number with a line break is not looked up");

            string url = ReadinessCheck.FindUrl("x') or (true");
            T.Check(Uri.UnescapeDataString(url).Contains("'x'') or (true')"), "serials: a quote stays inside the filter string");
            url = ExpectedIntune.FindUrl("serialNumber", "x' or deviceName ne '");
            T.Check(Uri.UnescapeDataString(url).Contains("serialNumber eq 'x'' or deviceName ne '''"), "serials: a quote in the expected apps filter is doubled");

            Session s = new Session();
            s.Computer = "PC1\u0000";
            s.SetInfo(DeviceFacts.Serial, "5CG\n2041");
            s.SetInfo(DeviceFacts.User, "anna@contoso.com");
            ExpectedQuery q = ExpectedQuery.Of(s);
            T.Check(q.Serial == null && q.DeviceName == null && q.UserUpn == "anna@contoso.com", "serials: what a device says about itself is asked about only when it looks right");
        }

        static long Time(Action a)
        {
            Stopwatch w = Stopwatch.StartNew();
            a();
            return w.ElapsedMilliseconds;
        }

        static string Repeat(string s, int length)
        {
            StringBuilder b = new StringBuilder();
            while (b.Length < length) b.Append(s);
            return b.ToString();
        }

        static LogLine Line(string text)
        {
            LogLine l = new LogLine();
            l.Text = text;
            l.Raw = text;
            l.Utc = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
            l.File = "AppWorkload.log";
            l.Thread = "1";
            l.LineNo = 1;
            return l;
        }

        // Each of these lines took from seconds to many minutes before; a line in an upload must not hang the window.
        static void Patterns()
        {
            const string App = "0a2ade58-6391-4fb4-80c2-8153740fd068";
            string spaces = new string(' ', 20000);
            string[] lines =
            {
                "[Win32App] app id" + spaces + "x",
                "[Win32App] app id " + App + " \"EnforcementState\":\"" + spaces + "x",
                "[Win32App] app id " + App + " " + Repeat("resulted in ", 20000),
                "[Win32App] app id " + App + " " + Repeat("requirement rule ", 20000),
                "[Win32App] app id " + App + spaces + "x",
                "[Win32App] app id " + App + " " + new string('-', 20000) + "x",
                "[Win32App] Downloaded file size" + spaces + "x",
                "[Win32App] Downloaded file time" + spaces + "x"
            };
            foreach (string t in lines)
            {
                ImeLogParser p = new ImeLogParser("AppWorkload");
                List<Obs> o = new List<Obs>();
                long ms = Time(delegate { p.Read(Line(t), o); });
                T.Check(ms < 5000, "patterns: a crafted line of " + t.Length + " characters is read quickly (" + ms + " ms): " + t.Substring(0, 40));
            }
            DownloadLog progress = new DownloadLog();
            long pr = Time(delegate { progress.Read(Line(Repeat("Downloading app (id = " + App + ", name ", 30000)), App, delegate { return new Obs(); }, new List<Obs>()); });
            T.Check(pr < 2000, "patterns: a download line that starts many times is read quickly (" + pr + " ms)");
            long sep = Time(delegate { AppGroups.IsSeparator(new string('-', 5000) + "x"); });
            T.Check(sep < 2000, "patterns: a long run of dashes is no slow separator (" + sep + " ms)");
            HealthScriptsParser h = new HealthScriptsParser();
            string many = Repeat("[HS] new result = {", 400000);
            long hs = Time(delegate { h.Read(Line(many), new List<Obs>()); });
            T.Check(hs < 5000, "patterns: a remediation line that starts a result many times is read quickly (" + hs + " ms)");

            List<AppGroups.Policy> pol = AppGroups.Policies("Get policies = [{\"Id\":\"" + App + "\",\"Name\":\"Bad \\q name\",\"Version\":1,\"Intent\":3,\"TargetType\":2},{\"Id\":\"" + App.Replace('0', '1') + "\",\"Name\":\"Caf\\u00e9\",\"Version\":1,\"Intent\":1,\"TargetType\":1}]");
            T.Check(pol.Count == 2 && pol[0].Name == "Bad \\q name" && pol[1].Name == "Caf\u00e9", "patterns: a policy name with an odd escape does not stop the log");

            // What the patterns found before, they still find.
            ImeLogParser q = new ImeLogParser("AppWorkload");
            List<Obs> found = new List<Obs>();
            q.Read(Line("[Win32App][ReportingManager] Report delta for app with id: " + App + " \"EnforcementState\":\"InProgress -> Success\""), found);
            T.Check(found.Count == 1 && found[0].State == State.Succeeded, "patterns: an enforcement delta still reads");
            found.Clear();
            q.Read(Line("[Win32App] app with id: " + App + " detection resulted in NotDetected"), found);
            T.Check(found.Count == 1 && found[0].Text == "Detection: not installed", "patterns: a detection result still reads");
            T.Check(AppGroups.IsSeparator("------ Lock DoWorkInternal ------") && AppGroups.IsSeparator("[Win32App] ------"), "patterns: separators still read");
            T.Eq("{\"a\":{\"b\":1}}", HealthScriptsParser.ResultJson("x [HS] new result = {\"a\":{\"b\":1}} tail"), "patterns: the result JSON ends at the last brace");
            T.Eq("{\"c\":2}", HealthScriptsParser.ResultJson("[HS] new result = {open\n[HS] new result = {\"c\":2}"), "patterns: a result with no end goes on with the next line");
            T.Check(HealthScriptsParser.ResultJson("[HS] new result = {") == null, "patterns: a result with no end is none");
            DownloadLog dl = new DownloadLog();
            found.Clear();
            dl.Read(Line("Downloading app (id = " + App + ", name Contoso (x64)) via DO, bytes 50/100"), App, delegate { return new Obs(); }, found);
            T.Check(found.Count == 1 && found[0].Percent == 50, "patterns: download progress still reads");
            found.Clear();
            dl.Read(Line("[Win32App] Downloaded file size 2,742.00"), App, delegate { return new Obs(); }, found);
            T.Check(found.Count == 1 && found[0].Info != null && found[0].Info.Exists(delegate (Pair x) { return x.Key == "Download size"; }), "patterns: the download size still reads");
        }

        static void Files()
        {
            T.Check(DlNames.Safe("NUL.notes", ".ps1").StartsWith("_", StringComparison.Ordinal), "files: a reserved name with a dot");
            T.Check(DlNames.Safe("com1.tar", ".txt").StartsWith("_", StringComparison.Ordinal), "files: a reserved port name with a dot");
            T.Check(DlNames.Safe("CONIN$", ".ps1").StartsWith("_", StringComparison.Ordinal) && DlNames.Safe("LPT\u00B9", ".ps1").StartsWith("_", StringComparison.Ordinal), "files: the console and superscript device names");
            T.Eq("Console.ps1", DlNames.Safe("Console", ".ps1"), "files: a name that only starts like a device");
            string rtl = DlNames.Safe("Invoice\u202Etxt.", ".ps1");
            T.Check(rtl.IndexOf('\u202E') < 0 && rtl.EndsWith(".ps1", StringComparison.Ordinal), "files: no right-to-left mark that hides the extension");
            T.Check(DlNames.Safe("a\u0085b\u2028c\u200Bd", ".txt").IndexOfAny(new char[] { '\u0085', '\u2028', '\u200B' }) < 0, "files: no invisible line or format characters");
            string longName = new string('a', 79) + "\U0001F600" + "tail";
            string cut = DlNames.Safe(longName, ".txt");
            T.Check(!char.IsHighSurrogate(cut[cut.Length - 5]), "files: a long name is not cut in the middle of a character");

            string line = DlLog.Line(new DateTime(2026, 11, 3, 9, 5, 7), "CONTOSO\\anna", "Contoso", "Map\u2028drive\u0085x", "f.ps1");
            T.Check(line.IndexOf('\u2028') < 0 && line.IndexOf('\u0085') < 0, "files: the download log keeps one line per download");

            string dir = Path.Combine(Path.GetTempPath(), "tv-dlsave-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string f = Path.Combine(dir, "x.ps1");
                bool marked = DlSave.Write(f, "Write-Output 1");
                byte[] b = File.ReadAllBytes(f);
                T.Check(b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF && Encoding.UTF8.GetString(b, 3, b.Length - 3) == "Write-Output 1", "files: the text is saved as before");
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) T.Check(!marked, "files: no mark where Windows streams are not there");
                T.Check(DlSave.ZoneText.Contains("ZoneId=3") && DlSave.ZoneStream == ":Zone.Identifier", "files: the mark says the internet zone");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        static void Wql()
        {
            T.Eq("'Driver\\'s Kit \\\\ Contoso'", AppText.Wql("Driver's Kit \\ Contoso"), "wql: quote and backslash escaped the WQL way");
            T.Eq("'x\\\\\\' OR 1=1'", AppText.Wql("x\\' OR 1=1"), "wql: a backslash before a quote cannot end the string");
            Site site = new Site();
            DlTarget t = new DlTarget();
            t.Kind = DlKind.SccmApp;
            t.Id = "ScopeId_ABC/Application_11111111-2222-3333-4444-555555555555";
            new DownloadReader(null, site).Read(t);
            string asked = null;
            foreach (string w in site.Wql) if (w.Contains("SMS_ApplicationAssignment")) asked = w;
            T.Eq("SELECT CollectionName, OfferTypeID FROM SMS_ApplicationAssignment WHERE ApplicationName = 'Driver\\'s Kit \\\\ Contoso'", asked, "wql: the assignments of an app with a quote in its name");
        }

        static void Text()
        {
            PfResult r = new PfResult();
            r.Serial = "5CG2041KQZ";
            r.Title = "Contoso\r\n    \u2713 Fake";
            r.Add(PfLevel.Fail, Preflight.GroupApps, "App", "Viewer\r\n    \u2713 Everything is fine", "Fix\nit");
            string text = Preflight.ToText(new List<PfResult> { r }, "Contoso", DateTime.UtcNow);
            T.Check(text.IndexOf("\n    \u2713", StringComparison.Ordinal) < 0, "text: a name from Intune adds no line of its own to the copied checklist");
            T.Check(text.Contains("Viewer") && text.Contains("Everything is fine"), "text: the name is still there");
        }
    }
}
