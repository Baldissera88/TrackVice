namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    // One field that goes along with a report or a wish. Shown on the page before anything leaves, then put in the link.
    public sealed class FeedbackField
    {
        // The id of the field in the GitHub issue form, which is also its name in the link.
        public string Id;
        public string Label;
        public string Value;
        // Typed by the user, not filled in by TrackVice.
        public bool Typed;

        public FeedbackField(string id, string label, string value, bool typed)
        {
            Id = id;
            Label = label;
            Value = value ?? "";
            Typed = typed;
        }
    }

    // Links that open a pre-filled GitHub issue or e-mail. TrackVice sends nothing itself: the browser or mail program
    // opens with the text, and the user sends it.
    public static class Feedback
    {
        public const string Email = "dbal.itx@gmail.com";
        public const string RepoUrl = "https://github.com/" + AppUpdate.Repo;
        public const string IssuesNew = RepoUrl + "/issues/new";
        public const string Discussions = RepoUrl + "/discussions";
        public const string BugTemplate = "bug_report.yml";
        public const string FeatureTemplate = "feature_request.yml";
        // GitHub documents no length, only "414 URI Too Long" above its server limit, which is about 8 KB. This stays well below.
        public const int MaxIssueUrl = 6000;
        // Mail programs on Windows cut mailto: links of about 2,000 characters.
        public const int MaxMailUrl = 2000;
        public const string CutNote = "[The rest did not fit in the link.]";

        // Percent-encodes as RFC 3986 says, for a query value or a mailto: header. Spaces become %20, not +.
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length * 2);
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            foreach (byte c in bytes)
            {
                bool plain = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~';
                if (plain) b.Append((char)c);
                else b.Append('%').Append(c.ToString("X2", CultureInfo.InvariantCulture));
            }
            return b.ToString();
        }

        // https://github.com/<repo>/issues/new?template=<form>&title=...&<field id>=... GitHub fills the issue form's fields by
        // their id. Values that make the link longer than max are cut, the longest first.
        public static string IssueUrl(string template, string title, List<FeedbackField> fields, int max)
        {
            List<string[]> q = new List<string[]>();
            q.Add(new string[] { "template", template });
            q.Add(new string[] { "title", Short(title, 100) });
            foreach (FeedbackField f in fields) q.Add(new string[] { f.Id, Lines(f.Value) });
            return Fit(IssuesNew + "?", q, 2, "&", max);
        }

        // mailto:dbal.itx@gmail.com?subject=...&body=... with the body cut to fit max.
        public static string MailUrl(string subject, string body, int max)
        {
            List<string[]> q = new List<string[]>();
            q.Add(new string[] { "subject", Short(subject, 120) });
            q.Add(new string[] { "body", (body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n") });
            return Fit("mailto:" + Email + "?", q, 1, "&", max);
        }

        // The fields as plain text, for an e-mail or the clipboard.
        public static string Body(List<FeedbackField> fields)
        {
            StringBuilder b = new StringBuilder();
            foreach (FeedbackField f in fields)
            {
                b.Append(f.Label).Append(":\n");
                string v = Lines(f.Value).Trim();
                b.Append(v.Length > 0 ? v : "-").Append("\n\n");
            }
            return b.ToString().TrimEnd() + "\n";
        }

        static string Lines(string s)
        {
            return (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }

        public static string Short(string s, int max)
        {
            s = (s ?? "").Trim();
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl).Trim();
            return s.Length <= max ? s : s.Substring(0, max - 3).TrimEnd() + "...";
        }

        static string Join(string head, List<string[]> q, string sep)
        {
            StringBuilder b = new StringBuilder(head);
            bool first = true;
            foreach (string[] p in q)
            {
                if (p[1].Length == 0 && p[0] != "body") continue;
                if (!first) b.Append(sep);
                b.Append(p[0]).Append('=').Append(Escape(p[1]));
                first = false;
            }
            return b.ToString();
        }

        // Cuts the longest values from index from on, one at a time, until the link fits. Each cut value ends with CutNote.
        static string Fit(string head, List<string[]> q, int from, string sep, int max)
        {
            string url = Join(head, q, sep);
            int guard = 0;
            while (url.Length > max && guard++ < 64)
            {
                int longest = -1, len = 0;
                for (int i = from; i < q.Count; i++)
                {
                    int l = Escape(q[i][1]).Length;
                    if (l > len && q[i][1] != CutNote) { len = l; longest = i; }
                }
                if (longest < 0) break;
                string v = q[longest][1];
                int room = len - (url.Length - max);
                q[longest][1] = Prefix(v, room);
                url = Join(head, q, sep);
            }
            return url;
        }

        // The longest start of v that, with CutNote after it, takes at most room characters once encoded.
        static string Prefix(string v, int room)
        {
            if (Escape(CutNote).Length > room) return CutNote;
            int lo = 0, hi = v.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Escape(Cut(v, mid)).Length <= room) lo = mid;
                else hi = mid - 1;
            }
            return Cut(v, lo);
        }

        static string Cut(string v, int n)
        {
            if (n > 0 && n < v.Length && char.IsHighSurrogate(v[n - 1])) n--;
            string s = v.Substring(0, n).TrimEnd();
            return s.Length == 0 ? CutNote : s + "\n" + CutNote;
        }

        static readonly Regex RxLink = new Regex(@"\b[a-z][a-z0-9+.\-]*://[^\s""'<>]+", RegexOptions.IgnoreCase);
        static readonly Regex RxGuid = new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase);
        static readonly Regex RxMail = new Regex(@"\b[\w.+\-]+@[\w\-]+(\.[\w\-]+)+\b");

        // Takes out what could name a customer, tenant or device from text TrackVice adds by itself: links, IDs, e-mail
        // addresses, the names given, and secrets as the agent masks them.
        public static string Scrub(string text, IEnumerable<string> names)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = Secrets.Mask(text);
            s = RxLink.Replace(s, "<link>");
            s = RxMail.Replace(s, "<e-mail>");
            s = RxGuid.Replace(s, "<id>");
            if (names != null)
            {
                List<string> l = new List<string>();
                foreach (string n in names) if (!string.IsNullOrEmpty(n) && n.Trim().Length >= 3) l.Add(n.Trim());
                // Longer names first, so a name inside another is not left half.
                l.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
                foreach (string n in l) s = Regex.Replace(s, Regex.Escape(n), "<name>", RegexOptions.IgnoreCase);
            }
            return s;
        }

        // What the user looks at, in words: this PC, devices with the agent, SCCM, a file.
        public static string ModeName(string mode, bool remote)
        {
            switch (mode)
            {
                case Modes.Live: return "This PC";
                case Modes.Agent: return "Devices with the TrackVice agent";
                case Modes.Intune: return "Autopilot in Intune";
                case Modes.Sccm: return "Task sequences in SCCM";
                case Modes.File: return remote ? "Another device" : "A saved session";
                case Modes.Folder: return "A folder of logs";
                case Modes.Demo: return "A demo";
                default: return "Nothing open";
            }
        }
    }

    // The errors TrackVice showed in this window, newest first. TrackVice keeps no log file of its own, so this is its log.
    public static class ProblemLog
    {
        public const int Keep = 10;
        static readonly List<string> lines = new List<string>();
        static readonly object gate = new object();

        public static void Add(DateTime local, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string line = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text.Replace("\r", " ").Replace("\n", " ").Trim();
            lock (gate)
            {
                lines.Insert(0, line);
                if (lines.Count > Keep) lines.RemoveRange(Keep, lines.Count - Keep);
            }
        }

        public static List<string> Recent()
        {
            lock (gate) return new List<string>(lines);
        }

        public static void Clear()
        {
            lock (gate) lines.Clear();
        }
    }

    // Help > Report a problem: what the user types, and what TrackVice fills in and shows before anything leaves.
    public sealed class ProblemReport
    {
        public string What = "", Expected = "", Steps = "";
        public string Version = "", Windows = "", Mode = "";
        public List<string> Errors = new List<string>();

        public static ProblemReport Make(string version, string windows, string mode, List<string> errors, IEnumerable<string> names)
        {
            ProblemReport r = new ProblemReport();
            r.Version = version ?? "";
            r.Windows = windows ?? "";
            r.Mode = mode ?? "";
            if (errors != null) foreach (string e in errors) r.Errors.Add(Feedback.Scrub(e, names));
            return r;
        }

        public string ErrorText
        {
            get { return Errors.Count == 0 ? "None in this session." : string.Join("\n", Errors.ToArray()); }
        }

        // What TrackVice adds, in the order the page shows it.
        public List<FeedbackField> Filled()
        {
            List<FeedbackField> l = new List<FeedbackField>();
            l.Add(new FeedbackField("version", "TrackVice version", Version, false));
            l.Add(new FeedbackField("windows", "Windows version", Windows, false));
            l.Add(new FeedbackField("mode", "What was open", Mode, false));
            l.Add(new FeedbackField("errors", "Last errors in TrackVice", ErrorText, false));
            return l;
        }

        public List<FeedbackField> Fields()
        {
            List<FeedbackField> l = new List<FeedbackField>();
            l.Add(new FeedbackField("what-happened", "What happened", What, true));
            l.Add(new FeedbackField("expected", "What you expected", Expected, true));
            l.Add(new FeedbackField("steps", "Steps to repeat it", Steps, true));
            l.AddRange(Filled());
            return l;
        }

        public string Title
        {
            get { string s = Feedback.Short(What, 80); return "[Bug]: " + (s.Length > 0 ? s : ""); }
        }

        public string IssueUrl()
        {
            return Feedback.IssueUrl(Feedback.BugTemplate, Title, Fields(), Feedback.MaxIssueUrl);
        }

        public string MailUrl()
        {
            return Feedback.MailUrl("TrackVice problem: " + Feedback.Short(What, 80), Feedback.Body(Fields()), Feedback.MaxMailUrl);
        }
    }

    // Help > Suggest a feature.
    public sealed class FeatureWish
    {
        public string What = "", Why = "", How = "";

        public List<FeedbackField> Fields()
        {
            List<FeedbackField> l = new List<FeedbackField>();
            l.Add(new FeedbackField("what", "What you would like", What, true));
            l.Add(new FeedbackField("why", "Why", Why, true));
            l.Add(new FeedbackField("how", "How you would use it", How, true));
            return l;
        }

        public string Title
        {
            get { return "[Feature]: " + Feedback.Short(What, 80); }
        }

        public string IssueUrl()
        {
            return Feedback.IssueUrl(Feedback.FeatureTemplate, Title, Fields(), Feedback.MaxIssueUrl);
        }

        public string MailUrl()
        {
            return Feedback.MailUrl("TrackVice wish: " + Feedback.Short(What, 80), Feedback.Body(Fields()), Feedback.MaxMailUrl);
        }
    }

    // Help > Tell the author you use TrackVice. Every field may stay empty.
    public sealed class UsageNote
    {
        public string Name = "", Company = "", Email = "", Use = "";
        public string Version = "";

        public List<FeedbackField> Fields()
        {
            List<FeedbackField> l = new List<FeedbackField>();
            l.Add(new FeedbackField("name", "Name", Name, true));
            l.Add(new FeedbackField("company", "Company", Company, true));
            l.Add(new FeedbackField("email", "E-mail", Email, true));
            l.Add(new FeedbackField("use", "What I use it for", Use, true));
            l.Add(new FeedbackField("version", "TrackVice version", Version, false));
            return l;
        }

        public string Text()
        {
            return "I use TrackVice.\n\n" + Feedback.Body(Fields());
        }

        public string MailUrl()
        {
            return Feedback.MailUrl("I use TrackVice", Text(), Feedback.MaxMailUrl);
        }
    }

    // The Publish banner and the Needs attention tile: shown only when a version is published and the running one is newer.
    // Neither publishes anything; the action runs only when the user clicks it.
    public sealed class PublishTile
    {
        public bool Show;
        public string Text = "", Action = "Publish", Explain = PublishBanner.ExplainTitle;
        public Action Publish;
    }

    public static class PublishBanner
    {
        public const string ExplainTitle = "What does Publish do?";

        // published is the version in agent/latest.json of your storage. Nothing published, or no storage, shows no banner:
        // the status bar and Settings offer the first version.
        public static bool Shown(bool hasStorage, string published, string running, bool later)
        {
            if (!hasStorage || later) return false;
            if (!SemVer.IsValid(published) || !SemVer.IsValid(running)) return false;
            return SemVer.Compare(published, running) < 0;
        }

        public static string Text(string running, string published)
        {
            return "TrackVice " + running + " is installed. Your devices still get " + published + ".";
        }

        public static string Done(string version)
        {
            return "Published " + version + ". New devices get it now; installed agents update within 6 hours.";
        }

        // After a publish: the places that took it.
        public static string Done(string version, IList<string> places)
        {
            return UpdateLocations.Done(version, places) + " New devices with a customer code get it now.";
        }

        // What the Needs attention tile on Overview needs. publish is only kept, never called here.
        public static PublishTile Tile(bool hasStorage, string published, string running, Action publish)
        {
            PublishTile t = new PublishTile();
            t.Show = Shown(hasStorage, published, running, false);
            t.Text = t.Show ? Text(running, published) : "";
            t.Publish = publish;
            return t;
        }
    }

    // When the Help pages that open a browser or mail program, or ask GitHub, are not offered. The same device checks as the
    // update check, except the policy, which only turns off updates.
    public static class FeedbackGate
    {
        public static string Reason(UpdateContext c)
        {
            if (c == null || !c.Window) return "Only in the TrackVice window.";
            if (c.WinPE) return "Not in Windows PE.";
            if (c.System) return "Not while TrackVice runs as SYSTEM.";
            if (c.TaskSequence) return "Not while a task sequence runs on this device.";
            if (c.Oobe) return "Not during Windows setup or the Enrollment Status Page.";
            if (c.Deploying) return "Not while TrackVice follows a deployment on this device.";
            return null;
        }
    }

    // "What does Publish do?", as the code does it. docs/AGENT.md has the same text.
    public static class PublishExplain
    {
        public static string[][] Sections()
        {
            string hours = AgentUpdate.Every.TotalHours.ToString(CultureInfo.InvariantCulture);
            return new string[][]
            {
                new string[] { "What is sent", "This TrackVice.exe, as agent/TrackVice-<version>.exe, and agent/latest.json, which names that version, its size and its SHA-256 and is signed with the update key on this PC. The exe goes first and agent/latest.json last. Nothing else is sent, and only to " + Arm.AgentFiles + " in your storage." },
                new string[] { "Who gets it, and when", "New devices: the platform script reads agent/latest.json when it runs at enrollment, checks the signature, size and SHA-256, and installs that version. Installed agents: they check at their start and every " + hours + " hours, and install it when it is newer than their own and signed with the key in their customer code. The platform script in Intune is not changed. The agent in an SCCM task sequence does not update itself: its package decides the version." },
                new string[] { "What is not affected", "Your customers' containers with their sessions and logs, the customer codes and links, the scripts in Intune, and GitHub." },
                new string[] { "How to go back", "Installed agents never install an older version, so they keep the new one until they remove themselves: " + AgentLifetime.DefaultKeepHours + " hours after their deployment is done, and at the latest " + AgentLifetime.DefaultMaxDays + " days after they were installed, unless the customer code sets other times. For new devices, publish the older version from the command line on this PC, which has the update key: TrackVice.exe /publish-agent /to \"<link to " + Arm.AgentFiles + " with Create and Write>\" /exe \"<the older TrackVice.exe>\". Make the link in the Azure portal. Publish in the window only offers a version newer than the published one." },
            };
        }
    }
}
