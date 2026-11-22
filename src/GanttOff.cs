namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // The time axis of the Gantt chart with the times the device was off. Collapsed, each time off takes a narrow
    // band of the chart instead of its real length, so a 26 minute deployment with a night off between is readable.
    public sealed class GanttAxis
    {
        public DateTime FromUtc, ToUtc;
        public bool Collapsed;
        // The times off inside the range, clipped to it.
        public readonly List<OffSpan> Bands = new List<OffSpan>();
        // The width of a collapsed band, as a share of the chart.
        public const double BandShare = 0.04;

        public static GanttAxis For(Session s, DateTime fromUtc, DateTime toUtc, DateTime nowUtc, bool collapse)
        {
            GanttAxis a = new GanttAxis();
            a.FromUtc = fromUtc;
            a.ToUtc = toUtc;
            foreach (OffSpan o in OffTime.Spans(s))
            {
                if (!o.Counts(nowUtc)) continue;
                DateTime f = o.FromUtc < fromUtc ? fromUtc : o.FromUtc, t = o.End(nowUtc) > toUtc ? toUtc : o.End(nowUtc);
                if (t <= f) continue;
                OffSpan b = new OffSpan();
                b.ItemKey = o.ItemKey;
                b.FromUtc = f;
                b.ToUtc = t;
                b.Clean = o.Clean;
                a.Bands.Add(b);
            }
            a.Collapsed = collapse && a.Bands.Count > 0;
            return a;
        }

        // The time the device was on in the range: what a collapsed axis spreads over the chart.
        TimeSpan OnTime
        {
            get
            {
                TimeSpan d = ToUtc - FromUtc;
                foreach (OffSpan b in Bands) d -= b.ToUtc.Value - b.FromUtc;
                return d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(1);
            }
        }

        // Where a time is, from 0 at the start of the range to 1 at its end.
        public double At(DateTime utc)
        {
            double span = (ToUtc - FromUtc).Ticks;
            if (span <= 0) return 0;
            if (!Collapsed) return (utc - FromUtc).Ticks / span;
            double band = BandShare, on = 1 - band * Bands.Count;
            if (on < 0.2) { band = 0.8 / Bands.Count; on = 0.2; }
            double onTicks = OnTime.Ticks;
            double x = 0;
            DateTime at = FromUtc;
            foreach (OffSpan b in Bands)
            {
                DateTime bf = b.FromUtc, bt = b.ToUtc.Value;
                if (utc <= bf) return x + (utc - at).Ticks / onTicks * on;
                x += (bf - at).Ticks / onTicks * on;
                if (utc < bt) return x + (utc - bf).Ticks / (double)(bt - bf).Ticks * band;
                x += band;
                at = bt;
            }
            return x + (utc - at).Ticks / onTicks * on;
        }

        public float X(DateTime utc, float left, float width)
        {
            return left + (float)(At(utc) * width);
        }

        // The span the tick step is chosen for: the time on when collapsed.
        public TimeSpan StepSpan
        {
            get { return Collapsed ? OnTime : ToUtc - FromUtc; }
        }

        public bool InBand(DateTime utc)
        {
            foreach (OffSpan b in Bands) if (utc > b.FromUtc && utc < b.ToUtc.Value) return true;
            return false;
        }

        // Ticks on round local times. Collapsed, none inside a time off.
        public List<GanttTick> Ticks(float widthPx, float minPx, TimeZoneInfo zone)
        {
            TimeSpan step = GanttLayout.Step(StepSpan, widthPx * (Collapsed ? (float)(1 - BandShare * Bands.Count) : 1f), minPx);
            List<GanttTick> all = GanttLayout.Ticks(FromUtc, ToUtc, step, zone);
            if (!Collapsed) return all;
            List<GanttTick> l = new List<GanttTick>();
            double last = -1;
            foreach (GanttTick k in all)
            {
                if (InBand(k.Utc)) continue;
                double x = At(k.Utc) * widthPx;
                if (last >= 0 && x - last < minPx * 0.6) continue;
                l.Add(k);
                last = x;
            }
            return l;
        }

        // "Off 9 h 01 min", the label of a band.
        public static string Label(OffSpan b)
        {
            return "Off " + Txt.DurationShort(b.ToUtc.Value - b.FromUtc);
        }

        public static string Tip(OffSpan b)
        {
            return "The device was off from " + Txt.Local(b.FromUtc) + " to " + Txt.Local(b.ToUtc.Value) + " (" + Txt.Duration(b.ToUtc.Value - b.FromUtc) + ").\nThis time does not count as deployment time.";
        }
    }
}
