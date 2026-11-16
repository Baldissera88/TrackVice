namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class ZoneHistoryTests
    {
        public static void Run()
        {
            // The image starts in Pacific time (bias 480) and moves to UTC+1 (bias -60) at 11:00 UTC.
            DateTime change = new DateTime(2026, 11, 16, 11, 0, 0, DateTimeKind.Utc);
            TimeZoneInfo plusOne = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");
            ZoneHistory z = new ZoneHistory();
            DateTime readAt = new DateTime(2026, 11, 16, 1, 58, 32, DateTimeKind.Utc);
            T.Eq(readAt, z.Fix(readAt, plusOne), "zones: unchanged without a change of zone");
            z.Add(change, -60, 480);
            T.Eq(1, z.Count, "zones: one change");

            // "02:58:32" was written in Pacific time, so it is 10:58:32 UTC, not 01:58:32.
            T.Eq(new DateTime(2026, 11, 16, 10, 58, 32, DateTimeKind.Utc), z.Fix(readAt, plusOne), "zones: a line from before the change gets the old zone");
            // "12:05" after the change is right as read.
            DateTime after = new DateTime(2026, 11, 16, 11, 5, 0, DateTimeKind.Utc);
            T.Eq(after, z.Fix(after, plusOne), "zones: a line after the change keeps the new zone");
            z.Add(change, -60, 480);
            T.Eq(1, z.Count, "zones: the same change once");
            z.Add(change.AddHours(1), -60, -60);
            T.Eq(1, z.Count, "zones: no change of bias is no change");

            // A log line without a bias is marked as local time; one with a bias is not.
            int ln = 0, used;
            List<LogLine> l = CmLog.Parse("<![LOG[a]LOG]!><time=\"02:58:32.1234567\" date=\"11-16-2026\" component=\"IME\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n"
                + "<![LOG[b]LOG]!><time=\"02:58:32.123+480\" date=\"11-16-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">\r\n", "z.log", ref ln, true, plusOne, out used);
            T.Eq(2, l.Count, "zones: two lines");
            T.Check(l[0].LocalTime && !l[1].LocalTime, "zones: only the line without a bias is local time");
        }
    }
}
