namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    // The device's time zone and how far its clock is off, kept in the session's facts, with a finding when the zone
    // changed during the deployment or the clock is more than MaxOffset off.
    public static class DeviceClock
    {
        public const string ZoneInfo = "Time zone";
        public const string ChangesInfo = "Time zone changes";
        public const string ClockInfo = "Clock";
        public const string OffsetInfo = "Clock offset (s)";
        public const string ZoneKind = "Time zone changed";
        public const string ClockKind = "Clock off";
        public static readonly TimeSpan MaxOffset = TimeSpan.FromMinutes(2);
        // A measurement over a slower upload says too little.
        static readonly TimeSpan MaxRoundTrip = TimeSpan.FromSeconds(30);

        static readonly Regex RxChange = new Regex(@"(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) UTC: (?<from>UTC[+-]\d{2}:\d{2}) to (?<to>UTC[+-]\d{2}:\d{2})", RegexOptions.Compiled);

        // UTC+01:00 for a bias of -60. Windows keeps the bias in minutes, UTC = local + bias.
        public static string Offset(int bias)
        {
            int m = -bias;
            return "UTC" + (m < 0 ? "-" : "+") + (Math.Abs(m) / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (Math.Abs(m) % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        public static string ZoneText(TimeZoneInfo zone, DateTime nowUtc)
        {
            if (zone == null) return null;
            TimeSpan o = zone.GetUtcOffset(nowUtc);
            return zone.Id + " (UTC" + (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture) + ")";
        }

        public static string ChangesText(IList<ZoneHistory.Change> changes)
        {
            if (changes == null || changes.Count == 0) return null;
            StringBuilder b = new StringBuilder();
            foreach (ZoneHistory.Change c in changes)
            {
                if (b.Length > 0) b.Append("; ");
                b.Append(c.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC: ").Append(Offset(c.OldBias)).Append(" to ").Append(Offset(c.NewBias));
            }
            return b.ToString();
        }

        // The facts the device reads about itself: its zone now, and every change of zone since the history it reads.
        public static List<Pair> Facts(TimeZoneInfo zone, IList<ZoneHistory.Change> changes, DateTime nowUtc)
        {
            List<Pair> l = new List<Pair>();
            string z = ZoneText(zone, nowUtc);
            if (z != null) l.Add(new Pair(ZoneInfo, z));
            string c = ChangesText(changes);
            if (c != null) l.Add(new Pair(ChangesInfo, c));
            return l;
        }

        public sealed class ZoneChange
        {
            public DateTime Utc;
            public string From, To;
        }

        public static List<ZoneChange> Changes(Session s)
        {
            List<ZoneChange> l = new List<ZoneChange>();
            string v = s.GetInfo(ChangesInfo);
            if (v == null) return l;
            foreach (Match m in RxChange.Matches(v))
            {
                DateTime t;
                if (!DateTime.TryParseExact(m.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) continue;
                ZoneChange c = new ZoneChange();
                c.Utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
                c.From = m.Groups["from"].Value;
                c.To = m.Groups["to"].Value;
                l.Add(c);
            }
            return l;
        }

        // The device clock against the storage's clock: the time the device sent the upload, halfway through the request,
        // minus the Date header of the storage's answer. Positive when the device is ahead. Null when it says too little.
        public static TimeSpan? Measure(DateTime sentUtc, DateTime receivedUtc, string dateHeader)
        {
            if (string.IsNullOrEmpty(dateHeader) || receivedUtc < sentUtc || receivedUtc - sentUtc > MaxRoundTrip) return null;
            DateTime server;
            if (!DateTime.TryParseExact(dateHeader.Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out server)) return null;
            DateTime mid = sentUtc.AddTicks((receivedUtc - sentUtc).Ticks / 2);
            return TimeSpan.FromSeconds(Math.Round((mid - server).TotalSeconds));
        }

        public static string ClockText(TimeSpan offset, DateTime measuredUtc)
        {
            string how = " (the device's time at its upload " + measuredUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC against the Date header of the storage's answer)";
            if (offset.Duration() <= MaxOffset) return "Right, within " + Txt.DurationShort(MaxOffset) + how;
            return Txt.Duration(offset.Duration()) + (offset > TimeSpan.Zero ? " ahead" : " behind") + how;
        }

        // Keeps a measurement in the session's facts.
        public static void Note(Session s, TimeSpan? offset, DateTime measuredUtc)
        {
            if (s == null || !offset.HasValue) return;
            s.SetInfo(ClockInfo, ClockText(offset.Value, measuredUtc));
            s.SetInfo(OffsetInfo, ((long)offset.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        // From the last upload to an Azure store. A folder has no clock of its own to compare with.
        public static void FromStore(ISessionStore store, Session s)
        {
            AzureStore a = store as AzureStore;
            if (a == null || a.LastDate == null) return;
            Note(s, Measure(a.LastSentUtc, a.LastReceivedUtc, a.LastDate), a.LastSentUtc);
            a.LastDate = null;
        }

        public static bool IsFact(string key)
        {
            return key == ZoneInfo || key == ChangesInfo || key == ClockInfo || key == OffsetInfo;
        }

        // The facts for the device section of the Session tab, in plain words.
        public static List<Pair> FactsOf(Session s)
        {
            List<Pair> l = new List<Pair>();
            // The time zone itself is in the For support facts (the same Info key), so it is not repeated here.
            foreach (ZoneChange c in Changes(s)) l.Add(new Pair("Zone changed", Txt.Local(c.Utc) + ", from " + c.From + " to " + c.To));
            if (s.GetInfo(ClockInfo) != null) l.Add(new Pair("Clock", s.GetInfo(ClockInfo)));
            return l;
        }

        public static TimeSpan? OffsetOf(Session s)
        {
            long n;
            string v = s.GetInfo(OffsetInfo);
            return v != null && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? TimeSpan.FromSeconds(n) : (TimeSpan?)null;
        }

        public static void Apply(Session s, List<Finding> list, DateTime nowUtc)
        {
            DateTime? first = s.FirstUtc;
            if (first.HasValue)
            {
                DateTime? end = WallModel.Ended(s);
                foreach (ZoneChange c in Changes(s))
                {
                    if (c.Utc < first.Value.AddMinutes(-5) || (end.HasValue && c.Utc > end.Value)) continue;
                    Finding f = new Finding();
                    f.Kind = ZoneKind;
                    f.Severity = 0;
                    f.TimeUtc = c.Utc;
                    f.Title = "The time zone changed during the deployment, from " + c.From + " to " + c.To;
                    f.Message = "Windows changed the zone at " + Txt.Local(c.Utc) + ". On the device, the clock and the log lines written before that show the hour of the old zone. TrackVice reads those lines with the zone they were written in and shows every time in your own time zone.";
                    f.Hint = "Set the time zone with an Intune setting (TimeLanguageSettings/ConfigureTimeZone), or turn on automatic time zone for new devices in the customer code. See docs/AGENT.md.";
                    list.Add(f);
                    break;
                }
            }
            TimeSpan? o = OffsetOf(s);
            if (o.HasValue && o.Value.Duration() > MaxOffset)
            {
                Finding f = new Finding();
                f.Kind = ClockKind;
                f.Severity = 1;
                f.TimeUtc = nowUtc;
                f.Title = "The clock of " + s.Computer + " is " + Txt.DurationShort(o.Value.Duration()) + (o.Value > TimeSpan.Zero ? " ahead" : " behind");
                f.Message = "Measured at the agent's upload: the device's time against the Date header of the storage's answer. The times the device itself shows and writes in its logs are that much off.";
                f.Hint = "Windows sets its clock from a time server (w32time). Check that the device can reach it. Sign-ins and certificates can fail when the clock is more than a few minutes off.";
                list.Add(f);
            }
        }
    }
}
