namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // How long the whole deployment still takes: a point, a range, the clock time it ends and where that comes from.
    // The expected times come from earlier finished deployments (DeploymentHistory) when there are any, else from the
    // defaults by kind in OverallProgress, and then the estimate is rough.
    public sealed class DeploymentEstimate
    {
        // At least a running item still needs this, however long it has run.
        public static readonly TimeSpan Floor = TimeSpan.FromSeconds(30);
        // Never less than this is said to be left while the deployment runs.
        public static readonly TimeSpan MinLeft = TimeSpan.FromMinutes(1);
        // Policies and profiles apply side by side and in seconds: all that wait count at most this much.
        public static readonly TimeSpan ParallelCap = TimeSpan.FromSeconds(30);

        public TimeSpan Left, Low, High;
        public bool HasRange, Over, Rough;
        // The usual total time of earlier deployments, for "taking longer than usual".
        public TimeSpan Usual;
        public DateTime FinishUtc;
        public int Runs;
        public string Profile;
        public string Basis;

        // Kinds IME and the task sequence run one after the other; the others apply in parallel and in seconds.
        static bool Parallel(string kind)
        {
            return kind == Kinds.Policy || kind == Kinds.Profile || kind == Kinds.Certificate || kind == Kinds.Enrollment
                || kind == Kinds.Esp || kind == Kinds.Agent;
        }

        // Null when nothing runs or waits, nothing is known yet, or the deployment began less than half a minute ago.
        public static DeploymentEstimate Make(Session s, DateTime nowUtc, TimeSpan notCounted, ExpectedTimes exp)
        {
            if (s == null || s.Items.Count == 0 || !TimeLeft.StillRuns(s)) return null;
            DateTime? a = EnrollmentAnchor.Of(s) ?? s.FirstUtc;
            if (!a.HasValue || nowUtc <= a.Value) return null;
            TimeSpan elapsed = OffTime.Active(s, a.Value, nowUtc) - notCounted;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            if (elapsed < TimeSpan.FromSeconds(30)) return null;
            HistoryStats h = exp != null ? exp.History : null;

            double sequential = 0, parallel = 0, parallelWaiting = 0;
            int restartsSeen = 0;
            foreach (Item i in s.Items)
            {
                if (i.Kind == Kinds.Restart || i.PhaseKey == Phases.Restarts)
                    if (i.State != State.Skipped) restartsSeen++;
                if (i.State == State.Skipped || i.State == State.NotApplicable) continue;
                if (States.IsComplete(i.State) || i.State == State.Failed) continue;
                double e = OverallProgress.Expected(i, exp);
                bool par = Parallel(i.Kind);
                if (States.IsActive(i.State))
                {
                    TimeSpan? d = i.Duration(nowUtc);
                    double rem = Math.Max(Floor.TotalSeconds, e - (d.HasValue ? d.Value.TotalSeconds : 0));
                    if (par) parallel = Math.Max(parallel, rem);
                    else sequential += rem;
                }
                else if (par) parallelWaiting += e;
                else sequential += e;
            }
            double known = sequential + parallel + Math.Min(parallelWaiting, ParallelCap.TotalSeconds);

            DeploymentEstimate r = new DeploymentEstimate();
            double left;
            if (h != null && h.Count > 0)
            {
                // Restarts the earlier deployments had and this one has not had yet.
                double more = Math.Max(0, Math.Round(h.Restarts) - restartsSeen);
                double restarts = more * (h.RestartSeconds > 0 ? h.RestartSeconds : OverallProgress.DefaultFor(new Item { Kind = Kinds.Restart }));
                // Items that are not visible yet are covered by what the earlier deployments took in all.
                double share = OverallProgress.Fraction(s, nowUtc, exp);
                double fromTotal = h.Total * (1 - share);
                left = Math.Max(known + restarts, fromTotal);
                r.Runs = h.Count;
                r.Profile = h.Profile;
                r.Usual = TimeSpan.FromSeconds(h.Total);
                double high = h.HasSpread ? h.High : h.Total * 1.25;
                if (elapsed.TotalSeconds > high && elapsed.TotalSeconds > h.Total)
                {
                    r.Over = true;
                    r.Basis = Based(r) + ". It has run " + Txt.DurationShort(elapsed) + ", longer than the usual " + Txt.DurationShort(r.Usual) + ".";
                    return r;
                }
                if (h.HasSpread && h.Total > 0)
                {
                    double lo = Math.Max(0.5, Math.Min(1, h.Low / h.Total)), hi = Math.Max(1, Math.Min(2, h.High / h.Total));
                    r.Low = TimeSpan.FromSeconds(Math.Max(MinLeft.TotalSeconds, left * lo));
                    r.High = TimeSpan.FromSeconds(Math.Max(r.Low.TotalSeconds, left * hi));
                    r.HasRange = Minutes(r.High) - Minutes(r.Low) >= 2;
                }
                r.Basis = Based(r) + ".";
            }
            else
            {
                left = known;
                r.Rough = true;
                r.Basis = "No history yet: rough, from typical times per kind of item. Finished deployments of this customer make it better.";
            }
            r.Left = TimeSpan.FromSeconds(Math.Max(MinLeft.TotalSeconds, left));
            if (r.HasRange && r.Low > r.Left) r.Low = r.Left;
            if (r.HasRange && r.High < r.Left) r.High = r.Left;
            r.FinishUtc = nowUtc + r.Left;
            return r;
        }

        static string Based(DeploymentEstimate r)
        {
            return "Based on " + r.Runs + (r.Runs == 1 ? " earlier deployment" : " earlier deployments") + (string.IsNullOrEmpty(r.Profile) ? "" : " of " + r.Profile)
                + ", usually " + Txt.DurationShort(r.Usual);
        }

        // The same rounding as TimeLeft.Words: whole minutes, steps of 5 above 10.
        public static int Minutes(TimeSpan t)
        {
            int min = (int)Math.Ceiling(t.TotalMinutes);
            if (min <= 1) return 1;
            if (min > 10) min = (int)(Math.Round(min / 5.0) * 5);
            return min;
        }

        static string Hm(int min)
        {
            return (min / 60).ToString(CultureInfo.InvariantCulture) + ":" + (min % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        // "20-35" in minutes, "1:05-1:40 h" from an hour on.
        public string RangeText
        {
            get
            {
                if (!HasRange) return "";
                int lo = Minutes(Low), hi = Minutes(High);
                if (hi < 60) return lo.ToString(CultureInfo.InvariantCulture) + "–" + hi.ToString(CultureInfo.InvariantCulture);
                return Hm(lo) + "–" + Hm(hi) + " h";
            }
        }

        public string ClockText
        {
            get { return "done ~" + FinishUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture); }
        }

        public string UsualText
        {
            get { return "usually " + Txt.DurationShort(TimeSpan.FromMinutes(Minutes(Usual))); }
        }

        // The texts from the most to the least, for the room there is: all parts, then without the range, then without the clock.
        public string[] Variants()
        {
            if (Over)
                return new string[] { "taking longer than usual (" + UsualText + ")", "longer than usual (" + UsualText + ")", "longer than usual" };
            string main = TimeLeft.Words(Left);
            if (Rough) return new string[] { main + " · rough", main };
            List<string> l = new List<string>();
            if (HasRange) l.Add(main + " · " + RangeText + " · " + ClockText);
            l.Add(main + " · " + ClockText);
            l.Add(main);
            return l.ToArray();
        }

        // The full text.
        public string Text
        {
            get { return Variants()[0]; }
        }

        // The short text for a list row: "~25 min left", "longer than usual".
        public string Brief
        {
            get { return Over ? "longer than usual" : "~" + TimeLeft.Words(Left).Substring("about ".Length); }
        }

        // The first text that fits, as the width the caller measures allows; else the shortest.
        public string Fit(Func<string, bool> fits)
        {
            string[] v = Variants();
            foreach (string t in v) if (fits(t)) return t;
            return v[v.Length - 1];
        }
    }
}
