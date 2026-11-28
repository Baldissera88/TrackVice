namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;

    public sealed class LogLine
    {
        public string Text;
        public string Raw;
        public DateTime? Utc;
        public string Component;
        // 0 information, 1 warning, 2 error.
        public int Severity;
        public string Thread;
        public string File;
        public int LineNo;
        // The time was local time without a zone, as the Intune Management Extension writes it.
        public bool LocalTime;
    }

    // Reads the CMTrace format that ConfigMgr and the Intune Management Extension write, the older simple
    // ConfigMgr format ("text $$<component><time><thread>"), and plain lines.
    public static class CmLog
    {
        // How long one regular expression may work on one line. Log text is not trusted: a crafted line must not hold up the agent.
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
        static readonly Regex RxAttrs = new Regex("time=\"(?<time>[^\"]*)\"\\s+date=\"(?<date>[^\"]*)\"\\s+component=\"(?<comp>[^\"]*)\"(?:\\s+context=\"[^\"]*\")?(?:\\s+type=\"(?<type>[^\"]*)\")?(?:\\s+thread=\"(?<thread>[^\"]*)\")?", RegexOptions.Compiled, MatchTimeout);
        // The end of a simple ConfigMgr line, from its "$$<": "$$<component><date time><thread=n>".
        static readonly Regex RxSimpleTail = new Regex(@"\G\$\$<(?<comp>[^>]*)><(?<date>\d{1,2}-\d{1,2}-\d{4}) (?<time>[^>]+)><thread=(?<thread>[^>]*)>\s*$", RegexOptions.Compiled, MatchTimeout);
        const string Open = "<![LOG[";
        const string Close = "]LOG]!>";
        // The attributes of a CMTrace record are a few hundred characters; more is not a record TrackVice reads attributes from.
        const int MaxAttrs = 4096;

        // Parses complete records from text. Returns how many characters were used: a record still being written at the end stays for the next read.
        // lineNo is the line number of the first character of text, and is moved past what was used.
        public static List<LogLine> Parse(string text, string file, ref int lineNo, bool final, TimeZoneInfo zone, out int used)
        {
            List<LogLine> list = new List<LogLine>();
            int pos = 0;
            used = 0;
            int n = text.Length;
            while (pos < n)
            {
                int start = pos;
                // Skip blank line ends between records. The line count moves only once the record after them is used.
                int skipped = 0;
                while (start < n && (text[start] == '\r' || text[start] == '\n'))
                {
                    if (text[start] == '\n') skipped++;
                    start++;
                }
                if (start >= n)
                {
                    lineNo += skipped;
                    used = n;
                    break;
                }
                LogLine l;
                int next;
                if (string.CompareOrdinal(text, start, Open, 0, Open.Length) == 0)
                {
                    int close = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
                    if (close < 0) { if (final) close = -1; else break; }
                    int end = close < 0 ? -1 : text.IndexOf('>', close + Close.Length);
                    if (end < 0 && !final) break;
                    int lineEnd = end < 0 ? NextLineEnd(text, start) : end + 1;
                    string raw = text.Substring(start, lineEnd - start);
                    l = new LogLine();
                    if (close >= 0)
                    {
                        l.Text = text.Substring(start + Open.Length, close - start - Open.Length).TrimEnd();
                        Match m = Attrs(raw, close - start);
                        if (m != null && m.Success)
                        {
                            l.Component = m.Groups["comp"].Value;
                            l.Thread = m.Groups["thread"].Value;
                            int type;
                            if (int.TryParse(m.Groups["type"].Value, out type)) l.Severity = type >= 3 ? 2 : type == 2 ? 1 : 0;
                            l.Utc = Time(m.Groups["date"].Value, m.Groups["time"].Value, zone);
                            l.LocalTime = l.Utc.HasValue && !HasBias(m.Groups["time"].Value);
                        }
                    }
                    else l.Text = raw.Substring(Open.Length);
                    l.Raw = raw;
                    next = lineEnd;
                }
                else
                {
                    int lineEnd = NextLineEnd(text, start);
                    if (lineEnd >= n && !final && text[n - 1] != '\n') break;
                    string raw = text.Substring(start, lineEnd - start).TrimEnd('\r', '\n');
                    l = new LogLine();
                    l.Raw = raw;
                    int tail;
                    Match m = Simple(raw, out tail);
                    if (m != null)
                    {
                        l.Text = raw.Substring(0, tail).TrimEnd();
                        l.Component = m.Groups["comp"].Value;
                        l.Thread = m.Groups["thread"].Value;
                        l.Utc = Time(m.Groups["date"].Value, m.Groups["time"].Value, zone);
                        l.LocalTime = l.Utc.HasValue && !HasBias(m.Groups["time"].Value);
                    }
                    else l.Text = raw;
                    next = lineEnd;
                }
                lineNo += skipped;
                l.File = file;
                l.LineNo = lineNo + 1;
                for (int k = start; k < next && k < n; k++) if (text[k] == '\n') lineNo++;
                if (l.Severity == 0) l.Severity = Guess(l.Text);
                list.Add(l);
                pos = next;
                used = pos;
            }
            return list;
        }

        static int NextLineEnd(string text, int start)
        {
            int i = text.IndexOf('\n', start);
            return i < 0 ? text.Length : i + 1;
        }

        // The attributes after "]LOG]!>", from at most MaxAttrs characters. Null when they cannot be read in time.
        static Match Attrs(string raw, int from)
        {
            try { return RxAttrs.Match(raw, from, Math.Min(raw.Length - from, MaxAttrs)); }
            catch (RegexMatchTimeoutException) { return null; }
        }

        // A simple ConfigMgr line: "text $$<component><date time><thread=n>". The end holds no '>' but the three that close
        // its parts, so its "$$<" lies between the fourth and the third '>' from the end. Found that way, a long line costs
        // one pass, where a pattern that tries every "$$<" can take minutes. tail is where the "$$<" starts.
        static Match Simple(string raw, out int tail)
        {
            tail = -1;
            int third = -1, fourth = -1, seen = 0;
            for (int i = raw.Length - 1; i >= 0; i--)
            {
                if (raw[i] != '>') continue;
                seen++;
                if (seen == 3) third = i;
                else if (seen == 4) { fourth = i; break; }
            }
            if (third < 0) return null;
            int from = fourth + 1;
            int at = raw.IndexOf("$$<", from, third - from, StringComparison.Ordinal);
            if (at < 0) return null;
            Match m;
            try { m = RxSimpleTail.Match(raw, at); }
            catch (RegexMatchTimeoutException) { return null; }
            if (!m.Success) return null;
            tail = at;
            return m;
        }

        static int Guess(string text)
        {
            return 0;
        }

        static readonly string[] Dates = { "MM-dd-yyyy", "M-d-yyyy", "MM-d-yyyy", "M-dd-yyyy" };

        // CMTrace time is local time with the bias in minutes, UTC = local + bias: "10:02:03.123-060".
        // The IME writes local time with seven decimals and no bias.
        public static bool HasBias(string time)
        {
            string t = (time ?? "").Trim();
            int sign = Math.Max(t.LastIndexOf('+'), t.LastIndexOf('-'));
            int bias;
            return sign > 0 && int.TryParse(t.Substring(sign), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bias);
        }

        public static DateTime? Time(string date, string time, TimeZoneInfo zone)
        {
            if (string.IsNullOrEmpty(date) || string.IsNullOrEmpty(time)) return null;
            int bias = 0;
            bool hasBias = false;
            string t = time.Trim();
            int sign = Math.Max(t.LastIndexOf('+'), t.LastIndexOf('-'));
            if (sign > 0)
            {
                hasBias = int.TryParse(t.Substring(sign), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bias);
                t = t.Substring(0, sign);
            }
            DateTime d;
            if (!DateTime.TryParseExact(date.Trim(), Dates, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
            string[] hms = t.Split(':');
            if (hms.Length < 3) return null;
            int h, mi;
            double sec;
            if (!int.TryParse(hms[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return null;
            if (!int.TryParse(hms[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out mi)) return null;
            if (!double.TryParse(hms[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sec)) return null;
            // Out of range parts are not a time: added to the date they would throw and stop the reading of the whole log.
            if (h < 0 || h > 24 || mi < 0 || mi > 59 || !(sec >= 0 && sec < 61) || (hasBias && Math.Abs(bias) > 24 * 60)) return null;
            DateTime local;
            try
            {
                local = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Unspecified).AddHours(h).AddMinutes(mi).AddTicks((long)Math.Round(sec * TimeSpan.TicksPerSecond));
                if (hasBias) return DateTime.SpecifyKind(local.AddMinutes(bias), DateTimeKind.Utc);
            }
            catch (ArgumentOutOfRangeException) { return null; }
            try { return TimeZoneInfo.ConvertTimeToUtc(local, zone ?? TimeZoneInfo.Local); }
            catch (ArgumentException) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
        }

        // The name a log file goes by, without the rollover date: AppEnforce-20261111-101010.log and AppEnforce.lo_ are AppEnforce.
        public static string BaseName(string path)
        {
            string n = path ?? "";
            int slash = Math.Max(n.LastIndexOf('\\'), n.LastIndexOf('/'));
            if (slash >= 0) n = n.Substring(slash + 1);
            int dot = n.LastIndexOf('.');
            if (dot > 0) n = n.Substring(0, dot);
            Match m = Regex.Match(n, @"^(?<b>.+?)-\d{8}-\d{6}$");
            if (m.Success) n = m.Groups["b"].Value;
            m = Regex.Match(n, @"^(?<b>.+?)-\d{8}$");
            if (m.Success) n = m.Groups["b"].Value;
            return n;
        }
    }

    // Follows one log file: remembers how far it has read and returns only new records. A file that got shorter
    // was rolled over, so it is read again from the start.
    public sealed class LogTail
    {
        public readonly string Path;
        long offset;
        int lineNo;
        string pending = "";
        Encoding encoding;
        DateTime lastWrite;
        public long MaxRead = 16L * 1024 * 1024;
        public TimeZoneInfo Zone;
        public string Note;
        // When set, the open file is read only when this accepts it, such as LogTrust.AdminOwned for a log in a folder
        // that a standard user can write. Checked on the open handle, so the file cannot be swapped between check and read.
        public Func<FileStream, bool> Trusted;

        public LogTail(string path)
        {
            Path = path;
        }

        public bool Exists
        {
            get
            {
                try { return File.Exists(Path); }
                catch (Exception) { return false; }
            }
        }

        public List<LogLine> Read()
        {
            List<LogLine> result = new List<LogLine>();
            FileInfo fi;
            try
            {
                fi = new FileInfo(Path);
                if (!fi.Exists) return result;
            }
            catch (Exception) { return result; }
            long len = fi.Length;
            if (len < offset || (len == offset && fi.LastWriteTimeUtc > lastWrite && offset > 0 && len < 16))
            {
                offset = 0;
                lineNo = 0;
                pending = "";
                encoding = null;
            }
            lastWrite = fi.LastWriteTimeUtc;
            if (len == offset) return result;
            byte[] buf;
            try
            {
                using (FileStream fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (Trusted != null && !Trusted(fs))
                    {
                        Note = Path + " was not read: it is not owned by SYSTEM or the administrators, so anyone could have written it.";
                        return result;
                    }
                    if (offset == 0 && len > MaxRead)
                    {
                        // A very large file: read its end only.
                        offset = len - MaxRead;
                        Note = System.IO.Path.GetFileName(Path) + " is larger than " + (MaxRead / 1024 / 1024) + " MB, so only its end was read.";
                        lineNo = -1;
                    }
                    long want = Math.Min(len - offset, MaxRead);
                    fs.Seek(offset, SeekOrigin.Begin);
                    buf = new byte[want];
                    int got = 0;
                    while (got < want)
                    {
                        int r = fs.Read(buf, got, (int)(want - got));
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got < want) Array.Resize(ref buf, got);
                }
            }
            catch (IOException) { return result; }
            catch (UnauthorizedAccessException)
            {
                Note = "Access denied to " + Path + ". Run TrackVice as administrator.";
                return result;
            }
            int skip = 0;
            if (encoding == null) encoding = Detect(buf, out skip);
            // Keep whole characters: for UTF-16 an even number of bytes, for UTF-8 up to the last complete sequence.
            int usable = buf.Length;
            if (encoding is UnicodeEncoding) usable -= (usable - skip) % 2;
            else usable = Utf8Whole(buf, skip, usable);
            string text = pending + encoding.GetString(buf, skip, usable - skip);
            offset += usable;
            if (lineNo < 0)
            {
                // Started in the middle of a big file: drop the partial first line.
                int nl = text.IndexOf('\n');
                text = nl >= 0 ? text.Substring(nl + 1) : "";
                lineNo = 0;
            }
            int used;
            int ln = lineNo;
            result = CmLog.Parse(text, Path, ref ln, false, Zone, out used);
            lineNo = ln;
            pending = text.Substring(used);
            if (pending.Length > 4 * 1024 * 1024) pending = "";
            return result;
        }

        static int Utf8Whole(byte[] b, int start, int end)
        {
            int i = end - 1, back = 0;
            while (i >= start && back < 4 && (b[i] & 0xC0) == 0x80) { i--; back++; }
            if (i < start) return end;
            byte lead = b[i];
            int need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
            return end - i >= need ? end : i;
        }

        static Encoding Detect(byte[] b, out int skip)
        {
            skip = 0;
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { skip = 2; return new UnicodeEncoding(false, false); }
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { skip = 3; return new UTF8Encoding(false); }
            // UTF-16 without a BOM: every second byte zero in the first characters.
            if (b.Length >= 8 && b[1] == 0 && b[3] == 0 && b[5] == 0 && b[0] != 0) return new UnicodeEncoding(false, false);
            return new UTF8Encoding(false);
        }

        // Reads a whole file once, for a folder of logs.
        public static List<LogLine> ReadAll(string path, TimeZoneInfo zone)
        {
            return ReadAll(path, zone, null);
        }

        public static List<LogLine> ReadAll(string path, TimeZoneInfo zone, Func<FileStream, bool> trusted)
        {
            LogTail t = new LogTail(path);
            t.Zone = zone;
            t.Trusted = trusted;
            t.MaxRead = 64L * 1024 * 1024;
            List<LogLine> l = t.Read();
            if (t.pending.Length > 0)
            {
                int used, ln = t.lineNo;
                l.AddRange(CmLog.Parse(t.pending, path, ref ln, true, zone, out used));
            }
            return l;
        }
    }

    // Who may have written a log. A standard user can create C:\_SMSTaskSequence and C:\Windows\Temp\SMSTSLog, so an
    // smsts.log there is believed only when SYSTEM, the administrators or TrustedInstaller own it, as the task sequence's own does.
    public static class LogTrust
    {
        const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        public static bool IsAdminSid(string sid)
        {
            return sid == "S-1-5-18" || sid == "S-1-5-32-544" || sid == TrustedInstaller;
        }

        public static bool AdminOwned(FileStream fs)
        {
            // Other systems have no Windows owners; there is no SYSTEM agent to protect there.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return true;
            try
            {
                System.Security.AccessControl.FileSecurity sec = fs.GetAccessControl();
                System.Security.Principal.SecurityIdentifier owner = sec.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
                if (owner != null) return IsAdminSid(owner.Value);
            }
            catch (Exception) { }
            // A file system without owners: only Windows PE, where nobody else is signed in, is trusted then.
            return InWinPe();
        }

        static bool InWinPe()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT")) return k != null;
            }
            catch (Exception) { return false; }
        }
    }
}
