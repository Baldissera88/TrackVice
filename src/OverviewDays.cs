namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // One bar of the chart of the last days on Overview: the deployments that finished that day, done and failed.
    public sealed class DayBar
    {
        public DateTime Day;
        public string Label;
        public int Done, Failed;
        public bool Today;

        public int Total
        {
            get { return Done + Failed; }
        }
    }

    public static class OverviewDays
    {
        public const int Count = 7;

        // The last 7 days, oldest first and today last, from the deployments that finished in them. today is the local
        // date. A deployment that finished just over 6 days ago counts on the oldest day, so the bars add up to the
        // finished deployments of the last 7 days.
        public static List<DayBar> Build(IEnumerable<OverviewDevice> finished, DateTime today, CultureInfo culture)
        {
            List<DayBar> l = new List<DayBar>();
            for (int i = Count - 1; i >= 0; i--)
            {
                DayBar b = new DayBar();
                b.Day = today.Date.AddDays(-i);
                b.Today = i == 0;
                b.Label = i == 0 ? "Today" : b.Day.ToString("ddd", culture ?? CultureInfo.InvariantCulture);
                l.Add(b);
            }
            if (finished == null) return l;
            foreach (OverviewDevice o in finished)
            {
                if (o == null || o.D == null) continue;
                int ago = (int)Math.Round((today.Date - o.EndUtc.ToLocalTime().Date).TotalDays);
                int idx = Math.Max(0, Math.Min(Count - 1, Count - 1 - ago));
                if (o.HasFailed) l[idx].Failed++;
                else l[idx].Done++;
            }
            return l;
        }
    }
}
