namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    public static class ReportTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Empty();
            OnlyPolicies();
            Escaping();
            RunningAndZeroLength();
            ManyLongItems();
            Options();
            FleetEmpty();
            FleetMany();
            Pct();
        }

        static Obs O(string key, string name, string kind, string phase, double min, State? st, long? exit)
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
            o.Text = "at minute " + min.ToString(CultureInfo.InvariantCulture);
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

        static readonly string[] Void = { "meta", "br", "hr", "img", "input", "link" };

        // Every opened tag is closed in order, outside the style sheet.
        static bool Balanced(string html, out string why)
        {
            why = null;
            html = Regex.Replace(html, "<style>.*?</style>", "", RegexOptions.Singleline);
            Stack<string> open = new Stack<string>();
            foreach (Match m in Regex.Matches(html, "<(/?)([a-zA-Z][a-zA-Z0-9]*)[^>]*>"))
            {
                string tag = m.Groups[2].Value.ToLowerInvariant();
                if (Array.IndexOf(Void, tag) >= 0) continue;
                if (m.Groups[1].Value.Length == 0) { open.Push(tag); continue; }
                if (open.Count == 0 || open.Peek() != tag)
                {
                    why = "</" + tag + "> closes " + (open.Count == 0 ? "nothing" : "<" + open.Peek() + ">");
                    return false;
                }
                open.Pop();
            }
            if (open.Count > 0) why = "<" + open.Peek() + "> is not closed";
            return open.Count == 0;
        }

        static void Sound(string html, string what)
        {
            string why;
            T.Check(Balanced(html, out why), what + ": balanced tags" + (why != null ? " (" + why + ")" : ""));
            T.Check(html.IndexOf("NaN", StringComparison.Ordinal) < 0, what + ": no NaN");
            T.Check(html.IndexOf("Infinity", StringComparison.Ordinal) < 0, what + ": no Infinity");
            bool inRange = true;
            foreach (Match m in Regex.Matches(html, "(?:left|width|right):(-?[0-9.]+)%"))
            {
                double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (v < 0 || v > 100) inRange = false;
            }
            T.Check(inRange, what + ": offsets within 0 to 100");
            T.Check(!Regex.IsMatch(html, "(src|href)=\"(https?:)?//|url\\(", RegexOptions.IgnoreCase), what + ": no external resources");
            T.Check(html.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0, what + ": no scripts");
        }

        static void Empty()
        {
            Session s = S("CONTOSO-PC01");
            string h = Report.Html(s, T0);
            Sound(h, "empty session");
            T.Check(h.Contains("nothing to report"), "empty session says so");
        }

        static void OnlyPolicies()
        {
            List<Obs> l = new List<Obs>();
            for (int k = 0; k < 40; k++) l.Add(O("pol" + k, "Contoso policy " + k, Kinds.Policy, Phases.Policies, k * 0.1, State.Succeeded, null));
            Session s = S("CONTOSO-PC02", l.ToArray());
            string h = Report.Html(s, T0.AddMinutes(10));
            Sound(h, "only policies");
            T.Check(h.Contains("40 policies"), "policies summed up in one Gantt line");
            T.Check(h.Contains("Result: done."), "done");
            T.Check(!h.Contains("slowest item"), "policies are not slowest items");
        }

        static void Escaping()
        {
            Session s = S("CONTOSO-<PC>&\"03",
                O("x", "<script>alert(\"x\")</script> & co", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null),
                O("x", "<script>alert(\"x\")</script> & co", Kinds.Win32App, Phases.IntuneApps, 4, State.Failed, 1603));
            string h = Report.Html(s, T0.AddMinutes(5), new ReportOptions { Title = "Contoso <b>\"Pilot\"</b> & co" });
            Sound(h, "escaping");
            T.Check(!h.Contains("<PC>"), "device name escaped");
            T.Check(h.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; co"), "item name escaped");
            T.Check(h.Contains("Contoso &lt;b&gt;&quot;Pilot&quot;&lt;/b&gt; &amp; co"), "title escaped");
            T.Check(h.Contains("Result: failed."), "failed result");
            T.Check(h.Contains("1603"), "exit code shown");

            FleetStats st = FleetStats.Build(new Session[] { s }, T0.AddMinutes(5));
            string f = Report.FleetHtml(st, "<i>&\"</i>", T0.AddMinutes(5));
            Sound(f, "fleet escaping");
            T.Check(!f.Contains("<i>&"), "fleet title escaped");
            T.Check(!f.Contains("<PC>"), "fleet device escaped");
        }

        static void RunningAndZeroLength()
        {
            Session s = S("CONTOSO-PC04",
                O("a", "Contoso Agent", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null),
                O("b", "Contoso Blip", Kinds.Win32App, Phases.IntuneApps, 2, State.Installing, null),
                O("b", "Contoso Blip", Kinds.Win32App, Phases.IntuneApps, 2, State.Succeeded, 0),
                O("c", "Contoso Later", Kinds.Win32App, Phases.IntuneApps, 3, State.Pending, null));
            string h = Report.Html(s, T0.AddMinutes(30));
            Sound(h, "running and zero length");
            T.Check(h.Contains("running, 30 min 00 s"), "running bar runs to now");
            T.Check(h.Contains("Result: still running."), "still running");
            T.Check(h.Contains("has run for 30 min 00 s so far"), "duration so far");
        }

        static void ManyLongItems()
        {
            List<Obs> l = new List<Obs>();
            string longName = new string('W', 240) + " Contoso";
            for (int k = 0; k < 300; k++)
            {
                string key = "app" + k;
                l.Add(O(key, longName + k, Kinds.Win32App, k % 2 == 0 ? Phases.DeviceSetup : Phases.AccountSetup, k, State.Installing, null));
                if (k % 7 != 0) l.Add(O(key, longName + k, Kinds.Win32App, k % 2 == 0 ? Phases.DeviceSetup : Phases.AccountSetup, k + (k % 5), k % 11 == 0 ? State.Failed : State.Succeeded, k % 11 == 0 ? 0x87D00324 : 0));
            }
            Session s = S("CONTOSO-PC05", l.ToArray());
            string h = Report.Html(s, T0.AddMinutes(400));
            Sound(h, "300 items");
            T.Check(h.Contains("The slowest items were"), "three slowest named");
            T.Check(h.Contains("detection method") || h.Contains("not detected"), "exit code meaning shown");
        }

        static void Options()
        {
            Session s = S("CONTOSO-PC06",
                O("a", "Contoso App", Kinds.Win32App, Phases.IntuneApps, 0, State.Installing, null),
                O("a", "Contoso App", Kinds.Win32App, Phases.IntuneApps, 3, State.Succeeded, 0),
                O("p", "Contoso Secret Policy", Kinds.Policy, Phases.Policies, 1, State.Succeeded, null));
            ReportOptions o = new ReportOptions();
            o.Title = "Contoso pilot";
            o.Subtitle = "Week 46";
            o.IncludeTimeline = false;
            o.IncludePolicies = false;
            string h = Report.Html(s, T0.AddMinutes(5), o);
            Sound(h, "options");
            T.Check(h.Contains("<h1>Contoso pilot</h1>"), "title");
            T.Check(h.Contains("Week 46"), "subtitle");
            T.Check(!h.Contains("<h2>Events</h2>"), "no event list");
            T.Check(!h.Contains("Contoso Secret Policy"), "policies left out");
            T.Check(Report.Html(s, T0.AddMinutes(5)).Contains("<h2>Events</h2>"), "event list by default");
        }

        static void FleetEmpty()
        {
            FleetStats st = FleetStats.Build(new Session[0], T0);
            string h = Report.FleetHtml(st, null, T0);
            Sound(h, "empty fleet");
            T.Check(h.Contains("No enrollments were found"), "empty fleet says so");
        }

        static void FleetMany()
        {
            List<Session> l = new List<Session>();
            for (int k = 0; k < 25; k++)
            {
                double at = k * 60;
                State end = k % 6 == 0 ? State.Failed : State.Succeeded;
                Session s = S("CONTOSO-LT" + k.ToString("000", CultureInfo.InvariantCulture),
                    O("a", "Contoso App", Kinds.Win32App, Phases.IntuneApps, at, State.Installing, null),
                    O("a", "Contoso App", Kinds.Win32App, Phases.IntuneApps, at + 5 + k % 4, end, end == State.Failed ? 1603 : 0),
                    O("b", "Contoso Tool", Kinds.Win32App, Phases.IntuneApps, at + 1, State.Installing, null));
                if (k > 2) S2(s, at);
                l.Add(s);
            }
            l.Add(null);
            FleetStats st = FleetStats.Build(l, T0.AddDays(2));
            string h = Report.FleetHtml(st, "Contoso", T0.AddDays(2));
            Sound(h, "fleet");
            T.Check(h.Contains("<svg"), "chart drawn");
            T.Check(h.Contains("What fails most"), "failures listed");
            T.Check(h.Contains("CONTOSO-LT000"), "example device");
            T.Check(!Regex.IsMatch(h, "c[xy]=\"[^0-9]"), "dots have numbers");

            // One enrollment only: the time axis still has a width.
            FleetStats one = FleetStats.Build(new Session[] { l[3] }, T0.AddDays(2));
            Sound(Report.FleetHtml(one, null, T0.AddDays(2)), "fleet of one");
        }

        static void S2(Session s, double at)
        {
            new Tracker(s).ApplyAll(new List<Obs> { O("b", "Contoso Tool", Kinds.Win32App, Phases.IntuneApps, at + 4, State.Succeeded, 0) });
        }

        static void Pct()
        {
            T.Eq("0", Report.Pct(double.NaN), "NaN is 0");
            T.Eq("0", Report.Pct(double.PositiveInfinity), "infinity is 0");
            T.Eq("0", Report.Pct(-3), "negative is 0");
            T.Eq("12.5", Report.Pct(12.5), "dot as decimal sign");
        }
    }
}
