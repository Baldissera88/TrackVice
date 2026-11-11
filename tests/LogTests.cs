namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public static class LogTests
    {
        static string Cm(string text, string time, string date, string comp, int type)
        {
            return "<![LOG[" + text + "]LOG]!><time=\"" + time + "\" date=\"" + date + "\" component=\"" + comp + "\" context=\"\" type=\"" + type + "\" thread=\"12\" file=\"x.cpp:1\">";
        }

        public static void Run()
        {
            // CMTrace with a bias: UTC = local + bias minutes.
            int ln = 0, used;
            string text = Cm("Hello", "10:02:03.123-060", "11-11-2026", "AppEnforce", 1) + "\r\n" + Cm("Bad thing", "10:02:04.000-060", "11-11-2026", "AppEnforce", 3) + "\r\n";
            List<LogLine> l = CmLog.Parse(text, "a.log", ref ln, false, TimeZoneInfo.Utc, out used);
            T.Eq(2, l.Count, "two CMTrace records");
            T.Eq(new DateTime(2026, 11, 11, 9, 2, 3, 123, DateTimeKind.Utc), l[0].Utc.Value, "bias -060 gives UTC an hour earlier");
            T.Eq("AppEnforce", l[0].Component, "component");
            T.Eq(2, l[1].Severity, "type 3 is an error");
            T.Eq(1, l[0].LineNo, "first line number");
            T.Eq(2, l[1].LineNo, "second line number");
            T.Eq(text.Length, used, "all text used");

            // A record that spans lines, and one still being written.
            ln = 0;
            string multi = Cm("first line\r\nsecond line", "08:00:00.000+000", "01-02-2026", "TSManager", 1) + "\r\n<![LOG[unfinished";
            l = CmLog.Parse(multi, "b.log", ref ln, false, TimeZoneInfo.Utc, out used);
            T.Eq(1, l.Count, "an unfinished record waits");
            T.Check(l[0].Text.Contains("second line"), "multi-line text kept");
            T.Check(multi.Substring(used).TrimStart().StartsWith("<![LOG[unfinished"), "unfinished record left over");
            T.Eq(1, ln, "line count stops before the unfinished record");

            // IME: seven decimals and no bias, so local time in the given zone.
            ln = 0;
            TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
            l = CmLog.Parse(Cm("[Win32App] x", "14:32:01.5677135", "11-10-2026", "AppWorkload", 1) + "\n", "c.log", ref ln, true, plusTwo, out used);
            T.Eq(new DateTime(2026, 11, 10, 12, 32, 1, DateTimeKind.Utc).AddTicks(5677135), l[0].Utc.Value, "IME local time to UTC");

            // The older simple format.
            ln = 0;
            l = CmLog.Parse("Download started for content X $$<ContentAccess><11-11-2026 10:00:00.000-060><thread=1234 (0x4D2)>\r\n", "cas.log", ref ln, false, TimeZoneInfo.Utc, out used);
            T.Eq(1, l.Count, "simple format record");
            T.Eq("ContentAccess", l.Count > 0 ? l[0].Component : null, "simple format component");
            T.Eq("Download started for content X", l.Count > 0 ? l[0].Text : null, "simple format text");
            T.Eq(new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc), l.Count > 0 ? l[0].Utc.Value : DateTime.MinValue, "simple format time");

            T.Eq("AppEnforce", CmLog.BaseName(@"C:\Logs\AppEnforce-20261111-101010.log"), "rollover name");
            T.Eq("AppEnforce", CmLog.BaseName(@"C:\Logs\AppEnforce.lo_"), "lo_ name");
            T.Eq("IntuneManagementExtension", CmLog.BaseName(@"C:\Logs\IntuneManagementExtension-20261101-090000.log"), "IME rollover name");
            T.Eq("smsts", CmLog.BaseName(@"X:\Windows\Temp\SMSTSLog\smsts.log"), "smsts name");

            Tail();
        }

        static void Tail()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "AppEnforce.log");
                string r1 = Cm("one", "10:00:00.000+000", "11-11-2026", "AppEnforce", 1) + "\r\n";
                string r2 = Cm("two", "10:00:01.000+000", "11-11-2026", "AppEnforce", 1) + "\r\n";
                File.WriteAllText(path, r1 + r2.Substring(0, 20), new UTF8Encoding(false));
                LogTail t = new LogTail(path);
                t.Zone = TimeZoneInfo.Utc;
                List<LogLine> got = t.Read();
                T.Eq(1, got.Count, "tail reads the complete record");
                File.AppendAllText(path, r2.Substring(20), new UTF8Encoding(false));
                got = t.Read();
                T.Eq(1, got.Count, "tail finishes the half-written record");
                T.Eq("two", got.Count > 0 ? got[0].Text : null, "tail text");
                T.Eq(2, got.Count > 0 ? got[0].LineNo : 0, "tail keeps line numbers");
                T.Eq(0, t.Read().Count, "nothing new");
                // Rolled over: shorter file, read from the start.
                File.WriteAllText(path, Cm("three", "10:00:02.000+000", "11-11-2026", "AppEnforce", 1) + "\r\n", new UTF8Encoding(false));
                got = t.Read();
                T.Eq(1, got.Count, "rollover is read from the start");
                T.Eq(1, got.Count > 0 ? got[0].LineNo : 0, "rollover restarts line numbers");

                // UTF-16 with a BOM.
                string p16 = Path.Combine(dir, "smsts.log");
                File.WriteAllText(p16, r1 + r2, new UnicodeEncoding(false, true));
                List<LogLine> all = LogTail.ReadAll(p16, TimeZoneInfo.Utc);
                T.Eq(2, all.Count, "UTF-16 log");
                T.Eq("one", all.Count > 0 ? all[0].Text : null, "UTF-16 text");

                // A UTF-8 character split across two reads.
                string pu = Path.Combine(dir, "CAS.log");
                byte[] full = Encoding.UTF8.GetBytes(Cm("Bl\u00e5b\u00e6rgr\u00f8d", "10:00:00.000+000", "11-11-2026", "CAS", 1) + "\r\n");
                int cut = Array.IndexOf(full, (byte)0xC3) + 1;
                File.WriteAllBytes(pu, Sub(full, 0, cut));
                LogTail tu = new LogTail(pu);
                tu.Read();
                using (FileStream fs = new FileStream(pu, FileMode.Append)) fs.Write(full, cut, full.Length - cut);
                got = tu.Read();
                T.Eq("Bl\u00e5b\u00e6rgr\u00f8d", got.Count > 0 ? got[0].Text : null, "split UTF-8 character survives");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static byte[] Sub(byte[] b, int start, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(b, start, r, 0, len);
            return r;
        }
    }
}
