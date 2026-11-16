namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Eventing.Reader;
    using System.Globalization;

    // The device's time zone over time, from Kernel-General event 22 in the System log ("The time zone bias has changed
    // to NewBias from OldBias"). The Intune Management Extension writes its log in local time without the zone, so a line
    // written before the device changed zone, such as from the Pacific time of a new Windows image to the zone of the
    // region, is read with the zone it was written in. Bias is in minutes, UTC = local + bias.
    public sealed class ZoneHistory
    {
        public sealed class Change
        {
            public DateTime Utc;
            public int OldBias, NewBias;
        }

        public const string Provider = "Microsoft-Windows-Kernel-General";
        public const int EventId = 22;

        readonly List<Change> changes = new List<Change>();
        long lastRecord;

        public int Count
        {
            get { return changes.Count; }
        }

        public void Add(DateTime utc, int newBias, int oldBias)
        {
            if (newBias == oldBias) return;
            foreach (Change c in changes)
                if (c.Utc == utc && c.NewBias == newBias) return;
            Change n = new Change();
            n.Utc = utc;
            n.NewBias = newBias;
            n.OldBias = oldBias;
            changes.Add(n);
            changes.Sort(delegate (Change a, Change b) { return a.Utc.CompareTo(b.Utc); });
        }

        // A time that was read as local time in zone, made right with the zone in effect when it was written.
        // Unchanged when the device never changed zone, or when no period fits.
        public DateTime Fix(DateTime readUtc, TimeZoneInfo zone)
        {
            if (changes.Count == 0) return readUtc;
            DateTime local;
            try { local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(readUtc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local); }
            catch (ArgumentException) { return readUtc; }
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            // Period k runs from the change before it to the change after it, with the bias that was in effect then.
            for (int k = changes.Count; k >= 0; k--)
            {
                int bias = k == 0 ? changes[0].OldBias : changes[k - 1].NewBias;
                DateTime utc = local.AddMinutes(bias);
                DateTime from = k == 0 ? DateTime.MinValue : changes[k - 1].Utc;
                DateTime to = k == changes.Count ? DateTime.MaxValue : changes[k].Utc;
                if (utc >= from && utc < to) return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }
            return readUtc;
        }

        // Reads new time zone changes from the System log. Only event 22 of Kernel-General is read.
        public void Read(DateTime sinceUtc)
        {
            string q = "*[System[Provider[@Name='" + Provider + "'] and EventID=" + EventId
                + (lastRecord > 0 ? " and (EventRecordID>" + lastRecord.ToString(CultureInfo.InvariantCulture) + ")"
                : " and TimeCreated[@SystemTime>='" + sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "']") + "]]";
            using (EventLogReader r = new EventLogReader(new EventLogQuery("System", PathType.LogName, q)))
            {
                int n = 0;
                for (EventRecord e = r.ReadEvent(); e != null && n < 500; e = r.ReadEvent(), n++)
                {
                    using (e)
                    {
                        long rec = e.RecordId ?? 0;
                        if (rec > lastRecord) lastRecord = rec;
                        if (!e.TimeCreated.HasValue || e.Properties.Count < 2) continue;
                        int nb, ob;
                        if (!int.TryParse(Convert.ToString(e.Properties[0].Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out nb)) continue;
                        if (!int.TryParse(Convert.ToString(e.Properties[1].Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out ob)) continue;
                        Add(e.TimeCreated.Value.ToUniversalTime(), nb, ob);
                    }
                }
            }
        }
    }
}
