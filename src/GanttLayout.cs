namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // One row of the Gantt chart: a phase header (Item is null) or an item with its bar.
    public sealed class GanttRow
    {
        public Phase Phase;
        public Item Item;
        public DateTime StartUtc;
        public DateTime EndUtc;

        public bool IsHeader
        {
            get { return Item == null; }
        }

        // Shorter than a second: drawn as a tick instead of a bar.
        public bool Instant
        {
            get { return Item != null && EndUtc - StartUtc < GanttLayout.MinSpan; }
        }

        public TimeSpan Duration
        {
            get { return EndUtc - StartUtc; }
        }
    }

    public sealed class GanttTick
    {
        public DateTime Utc;
        public string Label;
    }

    // Rows, time range, axis ticks and bar positions for the Gantt tab, without any drawing.
    public static class GanttLayout
    {
        public static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(1);

        static readonly TimeSpan[] Steps =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1)
        };

        public static DateTime BarStart(Item i)
        {
            return i.StartUtc ?? i.FirstUtc;
        }

        // The end time if there is one, the last activity once the item is final, now while it runs.
        public static DateTime BarEnd(Item i, DateTime nowUtc)
        {
            DateTime s = BarStart(i);
            DateTime e = i.EndUtc ?? (States.IsActive(i.State) ? nowUtc : i.LastUtc);
            return e < s ? s : e;
        }

        public static List<GanttRow> Rows(Session s, DateTime nowUtc, bool showPolicies, bool onlyTimed)
        {
            List<GanttRow> rows = new List<GanttRow>();
            foreach (Phase p in s.OrderedPhases)
            {
                if (!showPolicies && p.Key == Phases.Policies) continue;
                List<GanttRow> items = new List<GanttRow>();
                foreach (Item i in p.Items)
                {
                    GanttRow r = new GanttRow();
                    r.Phase = p;
                    r.Item = i;
                    r.StartUtc = BarStart(i);
                    r.EndUtc = BarEnd(i, nowUtc);
                    if (onlyTimed && r.Instant && !States.IsActive(i.State)) continue;
                    items.Add(r);
                }
                if (items.Count == 0) continue;
                items.Sort(delegate (GanttRow a, GanttRow b)
                {
                    int c = a.StartUtc.CompareTo(b.StartUtc);
                    if (c != 0) return c;
                    c = a.Item.Order.CompareTo(b.Item.Order);
                    return c != 0 ? c : string.Compare(a.Item.DisplayName, b.Item.DisplayName, StringComparison.OrdinalIgnoreCase);
                });
                GanttRow h = new GanttRow();
                h.Phase = p;
                h.StartUtc = items[0].StartUtc;
                h.EndUtc = items[0].EndUtc;
                foreach (GanttRow r in items) if (r.EndUtc > h.EndUtc) h.EndUtc = r.EndUtc;
                rows.Add(h);
                rows.AddRange(items);
            }
            return rows;
        }

        // Restarts of the device, drawn as thin lines across the chart at the time it went down.
        public static List<GanttTick> RestartMarks(Session s, TimeZoneInfo zone)
        {
            List<GanttTick> l = new List<GanttTick>();
            Phase p = s.FindPhase(Phases.Restarts);
            if (p == null) return l;
            foreach (Item i in p.Items)
            {
                GanttTick k = new GanttTick();
                k.Utc = BarStart(i);
                k.Label = (i.Off != null && i.Off.List.Exists(delegate (OffSpan o) { return o.ItemKey == i.Key && o.ToUtc.HasValue && o.Counts(o.ToUtc.Value); }) ? "Off " : "Restart ")
                    + TimeZoneInfo.ConvertTimeFromUtc(k.Utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
                l.Add(k);
            }
            l.Sort(delegate (GanttTick a, GanttTick b) { return a.Utc.CompareTo(b.Utc); });
            return l;
        }

        // The time span the chart shows: every bar, now for a live session, and never less than a minute.
        public static void Range(List<GanttRow> rows, DateTime nowUtc, bool live, out DateTime fromUtc, out DateTime toUtc)
        {
            fromUtc = DateTime.MaxValue;
            toUtc = DateTime.MinValue;
            foreach (GanttRow r in rows)
            {
                if (r.StartUtc < fromUtc) fromUtc = r.StartUtc;
                if (r.EndUtc > toUtc) toUtc = r.EndUtc;
            }
            if (rows.Count == 0) { fromUtc = nowUtc; toUtc = nowUtc; }
            if (live && nowUtc > toUtc) toUtc = nowUtc;
            if (toUtc - fromUtc < TimeSpan.FromMinutes(1)) toUtc = fromUtc + TimeSpan.FromMinutes(1);
            // A little room after the last bar for its duration.
            toUtc += TimeSpan.FromTicks((toUtc - fromUtc).Ticks / 50);
        }

        // The smallest step that keeps ticks at least minPx apart.
        public static TimeSpan Step(TimeSpan span, float widthPx, float minPx)
        {
            if (widthPx <= 0) return Steps[Steps.Length - 1];
            foreach (TimeSpan st in Steps)
                if (st.Ticks / (double)span.Ticks * widthPx >= minPx) return st;
            return Steps[Steps.Length - 1];
        }

        // Ticks on round local times between from and to.
        public static List<GanttTick> Ticks(DateTime fromUtc, DateTime toUtc, TimeSpan step, TimeZoneInfo zone)
        {
            List<GanttTick> l = new List<GanttTick>();
            DateTime localFrom = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone);
            long t = (localFrom.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks;
            string fmt = step < TimeSpan.FromMinutes(1) ? "HH:mm:ss" : "HH:mm";
            for (int n = 0; n < 500; n++, t += step.Ticks)
            {
                DateTime local = new DateTime(t, DateTimeKind.Unspecified);
                DateTime utc = local - zone.GetUtcOffset(local);
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                if (utc > toUtc) break;
                GanttTick k = new GanttTick();
                k.Utc = utc;
                k.Label = local.ToString(fmt, CultureInfo.InvariantCulture);
                l.Add(k);
            }
            return l;
        }

        public static float X(DateTime utc, DateTime fromUtc, DateTime toUtc, float left, float width)
        {
            double span = (toUtc - fromUtc).Ticks;
            if (span <= 0) return left;
            return left + (float)((utc - fromUtc).Ticks / span * width);
        }

        // The row under a point, or -1. The rows start below the axis and scroll by top pixels.
        public static int RowAt(int y, int axisH, int top, int rowH, int count)
        {
            if (y < axisH || rowH <= 0) return -1;
            int k = (y - axisH + top) / rowH;
            return k >= 0 && k < count ? k : -1;
        }

        public static int ClampTop(int top, int count, int rowH, int viewH)
        {
            int max = Math.Max(0, count * rowH - viewH);
            return Math.Max(0, Math.Min(top, max));
        }

        // Where to write the duration: after the bar when there is room, else -1.
        public static float LabelX(float barRight, float textW, float chartRight, float gap)
        {
            float x = barRight + gap;
            return x + textW <= chartRight ? x : -1;
        }
    }
}
