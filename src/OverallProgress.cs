namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    // How long items took on other devices of the same customer, or in the reference enrollment, by kind and name.
    public sealed class ExpectedTimes
    {
        readonly Dictionary<string, double> seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public static string KeyOf(string kind, string name)
        {
            return kind + "|" + name;
        }

        public int Count
        {
            get { return seconds.Count; }
        }

        public void Set(string kind, string name, double avgSeconds)
        {
            if (avgSeconds >= 1 && !string.IsNullOrEmpty(name)) seconds[KeyOf(kind, name)] = avgSeconds;
        }

        public double? Get(Item i)
        {
            double v;
            return seconds.TryGetValue(KeyOf(i.Kind, i.DisplayName), out v) ? v : (double?)null;
        }

        // The average of each item over the other devices (the one being shown is left out, so it does not predict itself),
        // and the reference enrollment for what no other device has.
        public static ExpectedTimes From(IEnumerable<Session> others, string exceptComputer, Reference reference, DateTime nowUtc)
        {
            ExpectedTimes t = new ExpectedTimes();
            List<Session> l = new List<Session>();
            if (others != null)
                foreach (Session s in others)
                    if (s != null && !string.Equals(s.Computer, exceptComputer, StringComparison.OrdinalIgnoreCase)) l.Add(s);
            if (l.Count > 0)
                foreach (FleetItem f in FleetStats.Build(l, nowUtc).Items)
                    if (f.Timed > 0) t.Set(f.Kind, f.Name, f.AvgSeconds);
            if (reference != null)
                foreach (ReferenceItem x in reference.Items)
                {
                    string name = string.IsNullOrEmpty(x.Name) ? x.Key : x.Name;
                    if (!x.Seconds.HasValue || x.Seconds.Value < 1) continue;
                    State st = States.Parse(x.State);
                    if (st != State.Succeeded && st != State.WaitingReboot) continue;
                    if (t.Get(new Item { Kind = x.Kind, Name = name }) == null) t.Set(x.Kind, name, x.Seconds.Value);
                }
            return t;
        }
    }

    // The overall progress of a deployment, weighted by how long each item is expected to take, so that one slow app
    // (Microsoft 365 Apps, about 20 minutes) counts for more than a policy that applies in a second.
    public static class OverallProgress
    {
        // What an item is expected to take when no device and no reference has timed it, by kind. In seconds.
        public static readonly KeyValuePair<string, double>[] DefaultSeconds = new KeyValuePair<string, double>[]
        {
            new KeyValuePair<string, double>(Kinds.Policy, 5),
            new KeyValuePair<string, double>(Kinds.Profile, 10),
            new KeyValuePair<string, double>(Kinds.Certificate, 20),
            new KeyValuePair<string, double>(Kinds.Script, 60),
            new KeyValuePair<string, double>(Kinds.Win32App, 300),
            new KeyValuePair<string, double>(Kinds.MsiApp, 180),
            new KeyValuePair<string, double>(Kinds.StoreApp, 180),
            new KeyValuePair<string, double>(Kinds.Office, OfficeSeconds),
            new KeyValuePair<string, double>(Kinds.Application, 300),
            new KeyValuePair<string, double>(Kinds.Package, 120),
            new KeyValuePair<string, double>(Kinds.Update, 300),
            new KeyValuePair<string, double>(Kinds.Content, 120),
            new KeyValuePair<string, double>(Kinds.Enrollment, 60),
            new KeyValuePair<string, double>(Kinds.Esp, 30),
            new KeyValuePair<string, double>(Kinds.Agent, 60),
            new KeyValuePair<string, double>(Kinds.Restart, 120),
            new KeyValuePair<string, double>(Kinds.Step, 30),
        };

        // Task sequence steps by what their name says, checked before the kind. In seconds.
        public static readonly KeyValuePair<string, double>[] StepSeconds = new KeyValuePair<string, double>[]
        {
            new KeyValuePair<string, double>("apply operating system", 600),
            new KeyValuePair<string, double>("apply data image", 300),
            new KeyValuePair<string, double>("install software updates", 600),
            new KeyValuePair<string, double>("install application", 300),
            new KeyValuePair<string, double>("install package", 180),
            new KeyValuePair<string, double>("apply driver", 180),
            new KeyValuePair<string, double>("auto apply drivers", 180),
            new KeyValuePair<string, double>("setup windows and configuration manager", 300),
            new KeyValuePair<string, double>("partition", 30),
            new KeyValuePair<string, double>("format", 30),
            new KeyValuePair<string, double>("restart", 90),
            new KeyValuePair<string, double>("run command line", 60),
        };

        public const double OtherSeconds = 60, OfficeSeconds = 1200;
        // A running item counts at most this much of its weight, however long it has run.
        public const double RunningCap = 0.9;
        // Until the deployment is done the overall figure stays at or below this.
        public const double NotDoneCap = 0.99;

        static readonly Regex RxOffice = new Regex(@"(microsoft 365 apps|office 365|microsoft office|office 20\d\d|m365 apps)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static double DefaultFor(Item i)
        {
            string name = (i.DisplayName ?? "").ToLowerInvariant();
            if (i.Kind == Kinds.Office || ((i.Kind == Kinds.Win32App || i.Kind == Kinds.MsiApp || i.Kind == Kinds.StoreApp || i.Kind == Kinds.Application) && RxOffice.IsMatch(name)))
                return OfficeSeconds;
            if (i.Kind == Kinds.Step)
                foreach (KeyValuePair<string, double> k in StepSeconds)
                    if (name.IndexOf(k.Key, StringComparison.Ordinal) >= 0) return k.Value;
            foreach (KeyValuePair<string, double> k in DefaultSeconds) if (k.Key == i.Kind) return k.Value;
            return OtherSeconds;
        }

        // How long the item is expected to take: from history when there is some, else by kind.
        public static double Expected(Item i, ExpectedTimes history)
        {
            double? h = history != null ? history.Get(i) : null;
            // The slow step of an item that history only knows from a fast run still counts for something.
            return Math.Max(1, h ?? DefaultFor(i));
        }

        // Items that will not take any time: skipped, not applicable.
        static bool Weightless(Item i)
        {
            return i.State == State.Skipped || i.State == State.NotApplicable;
        }

        // Done or failed: nothing more will happen to it.
        static bool Settled(Item i)
        {
            return States.IsComplete(i.State) || i.State == State.Failed;
        }

        // 0 to 1. Done and failed items count fully, a running item its elapsed share of the expected time (at most 90 %),
        // a waiting one nothing. Never 1 until everything is done, and never above 0.99 before.
        public static double Fraction(Session s, DateTime nowUtc, ExpectedTimes history)
        {
            if (s == null || s.Items.Count == 0) return 0;
            double done = 0, total = 0;
            bool open = false, failed = false;
            foreach (Item i in s.Items)
            {
                if (Weightless(i)) continue;
                double w = Expected(i, history);
                total += w;
                if (i.State == State.Failed) failed = true;
                if (Settled(i))
                {
                    done += w;
                    continue;
                }
                open = true;
                if (States.IsActive(i.State)) done += w * RunningShare(i, w, nowUtc);
            }
            if ((s.GetInfo("Task sequence result") ?? "").StartsWith("Failed", StringComparison.Ordinal)) failed = true;
            if (total <= 0) return open ? 0 : 1;
            double f = Math.Max(0, Math.Min(1, done / total));
            return open || failed ? Math.Min(f, NotDoneCap) : 1;
        }

        // The whole percent to show.
        public static double Percent(Session s, DateTime nowUtc, ExpectedTimes history)
        {
            return Math.Round(Fraction(s, nowUtc, history) * 100);
        }

        static double RunningShare(Item i, double expected, DateTime nowUtc)
        {
            TimeSpan? d = i.Duration(nowUtc);
            double e = d.HasValue ? d.Value.TotalSeconds : 0;
            return Math.Max(0, Math.Min(RunningCap, e / expected));
        }

        // The expected time still to go for what runs now: the longest remainder of a running item, at least a minute.
        public static TimeSpan RunningRemainder(Session s, DateTime nowUtc, ExpectedTimes history)
        {
            double m = 0;
            foreach (Item i in s.Items)
            {
                if (!States.IsActive(i.State)) continue;
                TimeSpan? d = i.Duration(nowUtc);
                double left = Expected(i, history) - (d.HasValue ? d.Value.TotalSeconds : 0);
                m = Math.Max(m, Math.Max(60, left));
            }
            return TimeSpan.FromSeconds(m);
        }

        // The estimate of the time left never says less than what the running items are still expected to take.
        public static TimeLeftEstimate Adjust(TimeLeftEstimate e, Session s, DateTime nowUtc, ExpectedTimes history)
        {
            if (e == null || e.Over || s == null) return e;
            TimeSpan floor = RunningRemainder(s, nowUtc, history);
            if (floor > e.Left)
            {
                e.Left = floor;
                e.Basis += " Not less than the " + Txt.DurationShort(floor) + " the running items are expected to need.";
            }
            return e;
        }
    }
}
