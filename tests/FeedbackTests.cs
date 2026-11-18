namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public static class FeedbackTests
    {
        public static void Run()
        {
            Func<string> gate = AppUpdate.Gate;
            Func<string, string> fetch = AppUpdate.Fetch;
            try
            {
                Encoding();
                IssueLinks();
                MailLinks();
                FieldsShown();
                Scrubbing();
                Log();
                Banner();
                Gate();
                Downloads();
                Explain();
                Templates();
            }
            finally
            {
                AppUpdate.Gate = gate;
                AppUpdate.Fetch = fetch;
                ProblemLog.Clear();
            }
        }

        static void Encoding()
        {
            T.Eq("a%20b%0Ac%26d%3De%2Bf", Feedback.Escape("a b\nc&d=e+f"), "spaces, new lines, & = and + are encoded");
            T.Eq("S%C3%B8lr%C3%B8d", Feedback.Escape("Sølrød"), "non-ASCII is UTF-8, percent-encoded");
            T.Eq("A-z_0.9~", Feedback.Escape("A-z_0.9~"), "unreserved characters stay");
            T.Eq("%F0%9F%93%A6", Feedback.Escape("\U0001F4E6"), "a character outside the BMP is one UTF-8 sequence");
        }

        static Dictionary<string, string> Query(string url)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            int q = url.IndexOf('?');
            foreach (string p in url.Substring(q + 1).Split('&'))
            {
                int eq = p.IndexOf('=');
                d[p.Substring(0, eq)] = Uri.UnescapeDataString(p.Substring(eq + 1));
            }
            return d;
        }

        static ProblemReport Sample()
        {
            ProblemReport r = ProblemReport.Make("1.4.0", "Windows 11 Enterprise 24H2 (26100.2605)", Feedback.ModeName(Modes.Live, false), new List<string> { "2026-11-18 08:51:59  It failed" }, null);
            r.What = "The list stays empty & the wall too.\nSecond line";
            r.Expected = "Devices";
            r.Steps = "1. Open\n2. Switch";
            return r;
        }

        static void IssueLinks()
        {
            ProblemReport r = Sample();
            string url = r.IssueUrl();
            T.Check(url.StartsWith("https://github.com/Baldissera88/TrackVice/issues/new?template=bug_report.yml&title="), "the issue link uses the bug form: " + url);
            Dictionary<string, string> q = Query(url);
            T.Eq("[Bug]: The list stays empty & the wall too.", q["title"], "the title is the first line of what happened");
            T.Eq(r.What, q["what-happened"], "what happened goes in its field, with its new line");
            T.Eq("1. Open\n2. Switch", q["steps"], "steps keep their lines");
            T.Eq("1.4.0", q["version"], "the version field");
            T.Check(!q.ContainsKey("body"), "no body parameter: the issue form fills its fields by id");
            T.Check(url.Length <= Feedback.MaxIssueUrl, "a short report fits");

            // A long log is cut so the link stays below the limit, and the short fields stay whole.
            StringBuilder big = new StringBuilder();
            for (int i = 0; i < 400; i++) big.Append("2026-11-18 08:00:").Append(i % 60).Append("  The upload failed with <link> (403) ø\n");
            ProblemReport lr = ProblemReport.Make("1.4.0", "Windows 11", "This PC", new List<string> { big.ToString() }, null);
            lr.What = "Short";
            lr.Steps = new string('x', 3000);
            string lu = lr.IssueUrl();
            T.Check(lu.Length <= Feedback.MaxIssueUrl, "a long report is cut to " + Feedback.MaxIssueUrl + " characters, got " + lu.Length);
            T.Check(lu.Length > Feedback.MaxIssueUrl - 200, "the cut uses the room there is: " + lu.Length);
            Dictionary<string, string> lq = Query(lu);
            T.Eq("Short", lq["what-happened"], "a short field is not cut");
            T.Check(lq["errors"].EndsWith(Feedback.CutNote), "the long log ends with the note that it was cut");
            T.Check(lq["errors"].StartsWith("2026-11-18 08:00:0  The upload"), "the cut keeps the start of the log");
            T.Check(lu.IndexOf("%EF%BF%BD", StringComparison.Ordinal) < 0, "no broken characters");

            // Characters outside the BMP are never split in two.
            ProblemReport er = ProblemReport.Make("1.4.0", "", "", null, null);
            StringBuilder emoji = new StringBuilder();
            for (int i = 0; i < 2000; i++) emoji.Append("\U0001F4E6");
            er.What = emoji.ToString();
            string eu = er.IssueUrl();
            string w = Query(eu)["what-happened"];
            bool whole = true;
            for (int i = 0; i < w.Length; i++) if (char.IsHighSurrogate(w[i]) && (i + 1 >= w.Length || !char.IsLowSurrogate(w[i + 1]))) whole = false;
            T.Check(whole && eu.Length <= Feedback.MaxIssueUrl, "a cut never splits a surrogate pair");

            FeatureWish f = new FeatureWish();
            f.What = "Filter the wall by profile";
            f.Why = "Three profiles";
            string fu = f.IssueUrl();
            T.Check(fu.StartsWith(Feedback.IssuesNew + "?template=feature_request.yml&title=%5BFeature%5D%3A%20Filter"), "the feature link uses the feature form: " + fu);
            T.Check(!Query(fu).ContainsKey("how"), "an empty field is left out of the link");
        }

        static void MailLinks()
        {
            ProblemReport r = Sample();
            string m = r.MailUrl();
            T.Check(m.StartsWith("mailto:dbal.itx@gmail.com?subject=TrackVice%20problem%3A%20"), "the e-mail goes to the author: " + m);
            Dictionary<string, string> q = Query(m);
            T.Check(q["body"].Contains("What happened:\r\nThe list stays empty & the wall too.\r\nSecond line"), "the body has the fields with CRLF lines");
            T.Check(q["body"].Contains("TrackVice version:\r\n1.4.0"), "the body has what TrackVice filled in");
            ProblemReport lr = Sample();
            lr.Steps = new string('y', 5000);
            string lm = lr.MailUrl();
            T.Check(lm.Length <= Feedback.MaxMailUrl, "a long e-mail link is cut to " + Feedback.MaxMailUrl + ", got " + lm.Length);
            T.Check(Query(lm)["body"].Contains(Feedback.CutNote), "the cut e-mail says so");

            UsageNote n = new UsageNote();
            n.Use = "Autopilot at customers";
            n.Version = "1.4.0";
            string nu = n.MailUrl();
            T.Check(nu.StartsWith("mailto:" + Feedback.Email + "?subject=I%20use%20TrackVice&body="), "tell the author opens an e-mail");
            T.Check(Query(nu)["body"].Contains("Name:\r\n-"), "an empty optional field shows as -");
        }

        static void FieldsShown()
        {
            ProblemReport r = Sample();
            List<string> shown = new List<string>();
            foreach (FeedbackField f in r.Fields()) shown.Add(f.Id);
            T.Eq("what-happened,expected,steps,version,windows,mode,errors", string.Join(",", shown.ToArray()), "the fields of a report");
            List<string> filled = new List<string>();
            foreach (FeedbackField f in r.Filled()) { filled.Add(f.Id); T.Check(!f.Typed, f.Id + " is filled in by TrackVice"); }
            T.Eq("version,windows,mode,errors", string.Join(",", filled.ToArray()), "what the page lists as filled in");
            // Everything in the link is a field the page shows; nothing else goes along.
            foreach (string key in Query(r.IssueUrl()).Keys)
                T.Check(key == "template" || key == "title" || shown.Contains(key), "the link only holds fields the page shows: " + key);
            string body = Feedback.Body(r.Fields());
            foreach (FeedbackField f in r.Fields()) T.Check(body.Contains(f.Label + ":"), "the e-mail lists " + f.Label);
            T.Eq("None in this session.", ProblemReport.Make("1", "", "", null, null).ErrorText, "no errors is said in words");
        }

        static void Scrubbing()
        {
            string s = Feedback.Scrub("CONTOSO-PC01 at Contoso: https://contosotrackvice.blob.core.windows.net/tv-contoso?sv=2024&sig=abc failed for 6d2f1c7e-0b5a-4f1e-9a43-3c2b8e7d9f10, ask anna.hansen@contoso.com, password=Secret123", new string[] { "CONTOSO-PC01", "Contoso", "", null, "ab" });
            T.Eq("<name> at <name>: <link> failed for <id>, ask <e-mail>, password=***", s, "names, links, IDs, e-mail addresses and secrets are taken out");
            ProblemReport r = ProblemReport.Make("1.4.0", "", "", new List<string> { "CONTOSO-PC01 has no session" }, new string[] { "CONTOSO-PC01" });
            T.Eq("<name> has no session", r.Errors[0], "errors in a report are scrubbed");
            T.Eq("This PC", Feedback.ModeName(Modes.Live, false), "mode: this PC");
            T.Eq("Devices with the TrackVice agent", Feedback.ModeName(Modes.Agent, true), "mode: agent devices");
            T.Eq("Task sequences in SCCM", Feedback.ModeName(Modes.Sccm, true), "mode: SCCM");
            T.Eq("A saved session", Feedback.ModeName(Modes.File, false), "mode: a file");
        }

        static void Log()
        {
            ProblemLog.Clear();
            for (int i = 0; i < 15; i++) ProblemLog.Add(new DateTime(2026, 11, 18, 8, 0, i), "Error " + i + "\r\nsecond line");
            List<string> l = ProblemLog.Recent();
            T.Eq(ProblemLog.Keep, l.Count, "the log keeps the last errors only");
            T.Eq("2026-11-18 08:00:14  Error 14  second line", l[0], "newest first, on one line");
            ProblemLog.Clear();
        }

        static void Banner()
        {
            T.Check(PublishBanner.Shown(true, "1.1.2", "1.4.0", false), "shown when the published version is older");
            T.Check(!PublishBanner.Shown(true, "1.4.0", "1.4.0", false), "not when the same version is published");
            T.Check(!PublishBanner.Shown(true, "1.5.0", "1.4.0", false), "not when a newer version is published");
            T.Check(!PublishBanner.Shown(true, "", "1.4.0", false), "not when nothing is published");
            T.Check(!PublishBanner.Shown(true, "garbage", "1.4.0", false), "not with a version that cannot be read");
            T.Check(!PublishBanner.Shown(false, "1.1.2", "1.4.0", false), "not without your storage");
            T.Check(!PublishBanner.Shown(true, "1.1.2", "1.4.0", true), "not after Later");
            T.Check(PublishBanner.Shown(true, "1.4.0-beta", "1.4.0", false), "a pre-release before the running version counts as older");
            T.Eq("TrackVice 1.4.0 is installed. Your devices still get 1.1.2.", PublishBanner.Text("1.4.0", "1.1.2"), "banner text");
            T.Eq("Published 1.4.0. New devices get it now; installed agents update within 6 hours.", PublishBanner.Done("1.4.0"), "text after Publish");

            int published = 0;
            PublishTile t = PublishBanner.Tile(true, "1.1.2", "1.4.0", delegate { published++; });
            T.Check(t.Show && t.Text == PublishBanner.Text("1.4.0", "1.1.2") && t.Action == "Publish", "the tile has what it needs");
            T.Eq(0, published, "the tile never publishes by itself");
            t.Publish();
            T.Eq(1, published, "its action publishes when clicked");
            PublishTile none = PublishBanner.Tile(true, "1.4.0", "1.4.0", delegate { published++; });
            T.Check(!none.Show && none.Text.Length == 0, "no tile when nothing needs publishing");
            T.Eq(1, published, "making a tile publishes nothing");
        }

        static void Gate()
        {
            UpdateContext c = new UpdateContext();
            T.Eq("Only in the TrackVice window.", FeedbackGate.Reason(c), "never on the command line or as the agent");
            c.Window = true;
            T.Eq(null, FeedbackGate.Reason(c), "offered in the window");
            c.Policy = true;
            T.Eq(null, FeedbackGate.Reason(c), "the update policy does not turn off Help");
            c.Oobe = true;
            T.Check(FeedbackGate.Reason(c) != null, "not during OOBE or the ESP");
            c.Oobe = false;
            c.WinPE = true;
            T.Check(FeedbackGate.Reason(c) != null, "not in Windows PE");
            c.WinPE = false;
            c.System = true;
            T.Check(FeedbackGate.Reason(c) != null, "not as SYSTEM");
            c.System = false;
            c.TaskSequence = true;
            T.Check(FeedbackGate.Reason(c) != null, "not while a task sequence runs");
            c.TaskSequence = false;
            c.Deploying = true;
            T.Check(FeedbackGate.Reason(c) != null, "not while this device deploys");
        }

        const string Releases = "[" +
            "{\"tag_name\":\"v1.3.2\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-14T09:00:00Z\",\"author\":{\"login\":\"someone\",\"name\":\"x\"}," +
            "\"assets\":[{\"name\":\"TrackVice.msi\",\"uploader\":{\"login\":\"someone\"},\"download_count\":341},{\"name\":\"TrackVice.exe\",\"download_count\":204}]}," +
            "{\"tag_name\":\"v1.4.0\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-11-02T09:00:00Z\",\"assets\":[{\"name\":\"TrackVice.msi\",\"download_count\":212}]}," +
            "{\"tag_name\":\"v1.5.0\",\"draft\":true,\"prerelease\":false,\"assets\":[{\"name\":\"TrackVice.msi\",\"download_count\":9}]}," +
            "{\"tag_name\":\"1.4.1-beta\",\"draft\":false,\"prerelease\":true,\"published_at\":\"2026-11-10T09:00:00Z\",\"assets\":[]}" +
            "]";

        static void Downloads()
        {
            DownloadCounts c = new DownloadCounts();
            DownloadCounts.Parse(Releases, c);
            T.Eq(null, c.Error, "the releases are read");
            T.Eq(3, c.Releases.Count, "drafts are left out");
            T.Eq("1.4.1-beta,1.4.0,1.3.2", c.Releases[0].Version + "," + c.Releases[1].Version + "," + c.Releases[2].Version, "newest version first, v taken off");
            T.Check(c.Releases[0].Prerelease, "a pre-release is marked");
            T.Eq(545L, c.Releases[2].Total, "the files of a release are added up");
            T.Eq("TrackVice.msi", c.Releases[2].Files[0].Key, "each file is listed");
            T.Eq(757L, c.Total, "the total over every release");
            T.Eq(new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc), c.Releases[1].PublishedUtc.Value, "the publish time is UTC");
            DownloadCounts bad = new DownloadCounts();
            DownloadCounts.Parse("{\"message\":\"Not Found\"}", bad);
            T.Check(bad.Error != null && bad.Releases.Count == 0, "a reply that is not a list says so");

            int asked = 0;
            AppUpdate.Fetch = delegate (string url) { asked++; T.Eq(DownloadCounts.ApiUrl, url, "the releases API"); return Releases; };
            AppUpdate.Gate = delegate { return UpdateGate.NoWindow; };
            DownloadCounts blocked = DownloadCounts.Read();
            T.Check(blocked.Error != null && asked == 0, "no question to GitHub on the command line or as the agent");
            AppUpdate.Gate = delegate { return "No update check while a task sequence runs on this device."; };
            blocked = DownloadCounts.Read();
            T.Eq("TrackVice does not ask GitHub while a task sequence runs on this device.", blocked.Error, "the reason is said for Downloads");
            T.Eq(0, asked, "no question to GitHub on a deploying device");
            AppUpdate.Gate = delegate { return null; };
            DownloadCounts ok = DownloadCounts.Read();
            T.Check(ok.Error == null && ok.Releases.Count == 3 && asked == 1, "the window asks GitHub once");
            T.Eq(5, DownloadCounts.Demo().Releases.Count, "demo counts");
        }

        static void Explain()
        {
            StringBuilder all = new StringBuilder();
            foreach (string[] s in PublishExplain.Sections()) all.Append(s[0]).Append(": ").Append(s[1]).Append('\n');
            string t = all.ToString();
            T.Check(t.Contains("every 6 hours"), "the explanation uses the agents' update interval");
            T.Check(t.Contains("48 hours") && t.Contains("30 days"), "the explanation uses the agents' lifetime");
            T.Check(t.Contains("never install an older version"), "the explanation says agents do not go back");
            T.Check(t.Contains("/publish-agent /to"), "the explanation names the command line");
            string doc = File.ReadAllText(Path.Combine("docs", "AGENT.md"));
            foreach (string[] s in PublishExplain.Sections())
                T.Check(doc.Contains(s[1].Replace("<", "&lt;").Replace(">", "&gt;")) || doc.Contains(s[1]), "docs/AGENT.md has the text of " + s[0]);
        }

        static void Templates()
        {
            string dir = Path.Combine(".github", "ISSUE_TEMPLATE");
            string bug = File.ReadAllText(Path.Combine(dir, Feedback.BugTemplate));
            foreach (FeedbackField f in Sample().Fields()) T.Check(bug.Contains("id: " + f.Id + "\n"), "the bug form has the field " + f.Id);
            string feat = File.ReadAllText(Path.Combine(dir, Feedback.FeatureTemplate));
            foreach (FeedbackField f in new FeatureWish().Fields()) T.Check(feat.Contains("id: " + f.Id + "\n"), "the feature form has the field " + f.Id);
            T.Check(File.Exists(Path.Combine(dir, "config.yml")), "the template chooser has a config");
        }
    }
}
