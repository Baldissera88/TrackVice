namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // A time the device was off: from a shutdown to the next start of Windows. ToUtc is null while it is still off.
    public sealed class OffSpan
    {
        public DateTime FromUtc;
        public DateTime? ToUtc;
        public string ItemKey;
        // False when Windows did not shut down cleanly (power loss, a stop error).
        public bool Clean = true;
        // The restart item's own time: the shutdown and the start, without the time off.
        public TimeSpan Own;

        public DateTime End(DateTime nowUtc)
        {
            return ToUtc ?? (nowUtc > FromUtc ? nowUtc : FromUtc);
        }

        // Long enough to be time off and not a restart that took a while.
        public bool Counts(DateTime nowUtc)
        {
            return End(nowUtc) - FromUtc > OffTime.Min;
        }
    }

    // The times off of one session, kept on its items so their durations leave them out.
    public sealed class OffSpans
    {
        public readonly List<OffSpan> List = new List<OffSpan>();

        // How much of from..to the device was off.
        public TimeSpan Within(DateTime fromUtc, DateTime toUtc)
        {
            TimeSpan sum = TimeSpan.Zero;
            foreach (OffSpan o in List)
            {
                if (!o.Counts(toUtc)) continue;
                sum += OffTime.Overlap(fromUtc, toUtc, o.FromUtc, o.End(toUtc));
            }
            return sum;
        }

        // The duration of an item without the time off. A long shutdown's own item takes the shutdown and the start.
        public TimeSpan Active(Item i, DateTime startUtc, DateTime endUtc, TimeSpan d)
        {
            if (i != null)
                foreach (OffSpan o in List)
                    if (o.ItemKey == i.Key && o.ToUtc.HasValue && o.Counts(endUtc)) return o.Own;
            return d - Within(startUtc, endUtc);
        }
    }

    // Time the device was off does not count as deployment time: a shutdown that lasted more than Min, from the
    // restart items (src/Restarts.cs), is left out of the overall time, phase and item times, statistics and reports.
    public static class OffTime
    {
        public static readonly TimeSpan Min = TimeSpan.FromMinutes(15);
        // The upload metadata with the time a device that is off went down.
        public const string MetaKey = "off";

        public static TimeSpan Overlap(DateTime a1, DateTime a2, DateTime b1, DateTime b2)
        {
            DateTime x = a1 > b1 ? a1 : b1, y = a2 < b2 ? a2 : b2;
            return y > x ? y - x : TimeSpan.Zero;
        }

        // Every shutdown of the session with its stop and start, also the short ones; OffSpan.Counts tells them apart.
        public static List<OffSpan> Spans(Session s)
        {
            List<OffSpan> l = new List<OffSpan>();
            if (s == null) return l;
            foreach (Item i in s.Items)
            {
                if (i.Kind != Kinds.Restart && i.PhaseKey != Phases.Restarts) continue;
                if (i.State == State.Skipped) continue;
                if (!i.StartUtc.HasValue) continue;
                OffSpan o = new OffSpan();
                o.ItemKey = i.Key;
                o.FromUtc = i.StartUtc.Value;
                o.Clean = i.Get("Clean shutdown") == null;
                if (i.EndUtc.HasValue && i.State == State.Succeeded)
                {
                    if (i.EndUtc.Value < o.FromUtc) continue;
                    o.ToUtc = i.EndUtc.Value;
                    o.Own = OwnTime(i);
                }
                else if (!States.IsActive(i.State)) continue;
                l.Add(o);
            }
            l.Sort(delegate (OffSpan a, OffSpan b) { return a.FromUtc.CompareTo(b.FromUtc); });
            return l;
        }

        // From the request to the stop, and from the start of Windows to the last restart event just after it.
        static TimeSpan OwnTime(Item i)
        {
            TimeSpan d = TimeSpan.Zero;
            DateTime stop = i.StartUtc.Value, boot = i.EndUtc.Value;
            if (i.FirstUtc.Year > 2000 && i.FirstUtc < stop && stop - i.FirstUtc < Min) d += stop - i.FirstUtc;
            DateTime last = boot;
            foreach (TEvent e in i.Events)
                if (e.TimeUtc > last && e.TimeUtc - boot < TimeSpan.FromMinutes(5)) last = e.TimeUtc;
            return d + (last - boot);
        }

        // Puts the times off on every item, so Item.Duration and Phase.Duration leave them out. Run after changes.
        public static void Mark(Session s)
        {
            if (s == null) return;
            List<OffSpan> l = Spans(s);
            OffSpans spans = null;
            if (l.Count > 0)
            {
                spans = new OffSpans();
                spans.List.AddRange(l);
            }
            foreach (Item i in s.Items) i.Off = spans;
        }

        // The time off between two times, from shutdowns only.
        public static TimeSpan Off(Session s, DateTime fromUtc, DateTime toUtc)
        {
            OffSpans o = new OffSpans();
            o.List.AddRange(Spans(s));
            return o.Within(fromUtc, toUtc);
        }

        // The running time between two times: without the time off, and without the time a pre-provisioned device was boxed.
        public static TimeSpan Active(Session s, DateTime fromUtc, DateTime toUtc)
        {
            if (toUtc <= fromUtc) return TimeSpan.Zero;
            List<DateTime[]> gaps = new List<DateTime[]>();
            foreach (OffSpan o in Spans(s))
                if (o.Counts(toUtc)) gaps.Add(new DateTime[] { o.FromUtc, o.End(toUtc) });
            DateTime bf;
            DateTime? bt;
            if (AutopilotFlow.Boxed(s, toUtc, out bf, out bt)) gaps.Add(new DateTime[] { bf, bt ?? toUtc });
            gaps.Sort(delegate (DateTime[] a, DateTime[] b) { return a[0].CompareTo(b[0]); });
            TimeSpan off = TimeSpan.Zero;
            DateTime reach = fromUtc;
            foreach (DateTime[] g in gaps)
            {
                DateTime a = g[0] > reach ? g[0] : reach;
                DateTime b = g[1] < toUtc ? g[1] : toUtc;
                if (b > a) { off += b - a; reach = b; }
            }
            TimeSpan d = toUtc - fromUtc - off;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }

        public static TimeSpan Active(OffSpans spans, DateTime fromUtc, DateTime toUtc)
        {
            TimeSpan d = toUtc - fromUtc - (spans != null ? spans.Within(fromUtc, toUtc) : TimeSpan.Zero);
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }

        // The overall time: "26 min", or "26 min · off 9 h 01 min" when the device was off.
        public static string Elapsed(Session s, DateTime nowUtc)
        {
            DateTime? f = s.FirstUtc;
            if (!f.HasValue) return "";
            string t = Txt.DurationShort(Active(s, f.Value, nowUtc));
            TimeSpan off = Off(s, f.Value, nowUtc);
            return off > TimeSpan.Zero ? t + " · off " + Txt.DurationShort(off) : t;
        }

        // When the device went down, while it has not started again after a clean shutdown or restart. Null otherwise.
        public static DateTime? OffSince(Session s)
        {
            DateTime? since = null;
            foreach (OffSpan o in Spans(s))
            {
                if (o.ToUtc.HasValue || !o.Clean) continue;
                if (since == null || o.FromUtc > since.Value) since = o.FromUtc;
            }
            // Asked for, and the agent uploaded before Windows logged the stop. Not time off: it may still be cancelled.
            foreach (Item i in s.Items)
                if (i.Kind == Kinds.Restart && !i.StartUtc.HasValue && i.State == State.Unknown && i.Get("Requested by") != null && (since == null || i.FirstUtc > since.Value))
                    since = i.FirstUtc;
            return since;
        }

        // "00:56" today, "17 Nov 00:56" on another day, in the viewer's time zone.
        public static string At(DateTime utc, DateTime nowUtc)
        {
            DateTime l = utc.ToLocalTime(), n = nowUtc.ToLocalTime();
            return l.Date == n.Date ? l.ToString("HH:mm", CultureInfo.InvariantCulture) : l.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        }

        // The device list's words for a device that shut down: "Off since 00:56", or null when it is not off.
        public static string ListText(DateTime? offUtc, DateTime nowUtc)
        {
            return offUtc.HasValue ? "Off since " + At(offUtc.Value, nowUtc) : null;
        }

        // No progress is not a finding while the device was off: that time does not count as silence.
        public static void Apply(Session s, List<Finding> list, DateTime nowUtc)
        {
            List<OffSpan> spans = Spans(s);
            if (spans.Count == 0) return;
            OffSpans o = new OffSpans();
            o.List.AddRange(spans);
            list.RemoveAll(delegate (Finding f)
            {
                if (f.Kind != FindingKinds.Stuck || f.ItemKey == null) return false;
                Item i = s.Find(f.ItemKey);
                if (i == null) return false;
                if (i.Kind == Kinds.Restart) return true;
                TimeSpan quiet = nowUtc - i.LastUtc - o.Within(i.LastUtc, nowUtc);
                return quiet <= Finder.StuckAfter;
            });
        }

        // An agent that stopped uploading because its device shut down: the device is off, nothing is wrong.
        public static void Silent(Session s, DateTime lastUploadUtc, DateTime nowUtc)
        {
            DateTime? off = OffSince(s);
            if (!off.HasValue) return;
            Finding f = s.Findings.Find(delegate (Finding x) { return x.Kind == FindingKinds.NoUploads; });
            if (f == null) return;
            f.Severity = 0;
            f.Title = s.Computer + " is off since " + At(off.Value, nowUtc);
            f.Message = "Windows shut down at " + Txt.Local(off.Value) + " and the last upload was " + Txt.Local(lastUploadUtc) + ". The time off does not count as deployment time.";
            f.Hint = "The agent starts again with Windows and continues this session.";
        }

        // The agent uploads at once when the device starts to shut down, so the list can say it is off.
        public static bool ShuttingDown(Session s, ref DateTime? told)
        {
            DateTime? off = OffSince(s);
            if (!off.HasValue || (told.HasValue && told.Value == off.Value)) return false;
            told = off;
            return true;
        }
    }
}
