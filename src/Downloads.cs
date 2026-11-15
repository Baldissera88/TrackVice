namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    // Win32 app downloads in AppWorkload.log: progress in bytes, and the size and time the agent writes when the
    // download is done. Delivery Optimization keeps its own job data in no documented place that can be read without
    // its PowerShell module, so bytes from peers and from HTTP are only shown when a log line names them.
    public sealed class DownloadLog
    {
        const RegexOptions Ci = RegexOptions.Compiled | RegexOptions.IgnoreCase;
        static readonly Regex RxProgress = new Regex(@"Downloading app \(id = [0-9A-Fa-f-]{36}, name (?<n>.*?)\) via (?<via>\w+), bytes (?<a>\d+)/(?<b>\d+)", Ci);
        static readonly Regex RxSize = new Regex(@"^\[Win32App\]\s*Downloaded file size\s+(?<v>[\d.,\s ]+)$", Ci);
        static readonly Regex RxTime = new Regex(@"^\[Win32App\]\s*Downloaded file time\s+(?<v>[\d.,\s ]+)$", Ci);
        static readonly Regex RxStart = new Regex(@"start creating a new download job|Content cache miss for app|Downloading app on session|Starting content download", Ci);
        static readonly Regex RxDone = new Regex(@"DO download and decryption is successfully done|Content is downloaded", Ci);
        // Delivery Optimization's own names for the bytes from each source, as Get-DeliveryOptimizationStatus shows them.
        static readonly Regex RxPeers = new Regex(@"BytesFromPeers\W{0,3}(?<v>\d+)", Ci);
        static readonly Regex RxHttp = new Regex(@"BytesFromHttp\W{0,3}(?<v>\d+)", Ci);

        sealed class Dl
        {
            public DateTime? Start, Done;
            public long? Size, Total, Peers, Http;
            public double? Ms;
            public int LastPct = -1;
            public string Via;
        }

        readonly Dictionary<string, Dl> apps = new Dictionary<string, Dl>(StringComparer.OrdinalIgnoreCase);

        Dl Get(string id)
        {
            Dl d;
            if (!apps.TryGetValue(id, out d)) { d = new Dl(); apps[id] = d; }
            return d;
        }

        // Reads a line of the app with this ID. True when the line was turned into observations here and needs nothing more.
        public bool Read(LogLine l, string id, Func<Obs> make, List<Obs> output)
        {
            string t = l.Text ?? "";
            DateTime at = l.Utc ?? DateTime.MinValue;
            Dl d = Get(id);
            Match m = RxProgress.Match(t);
            if (m.Success)
            {
                long a = long.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture), b = long.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
                if (a == 0 && d.LastPct != 0)
                {
                    d = new Dl();
                    apps[id] = d;
                }
                if (d.Start == null) d.Start = at;
                d.Via = m.Groups["via"].Value;
                if (b > 0) d.Total = b;
                int pct = b > 0 ? (int)Math.Min(100, a * 100 / b) : 0;
                if (pct == d.LastPct) return true;
                d.LastPct = pct;
                Obs o = make();
                o.State = State.Downloading;
                o.Percent = pct;
                o.NewAttempt = a == 0;
                o.Text = a == 0 ? "Downloading with " + Via(d.Via) : "Downloaded " + pct + " %" + (b > 0 ? " of " + Bytes(b) : "");
                output.Add(o);
                return true;
            }
            m = RxSize.Match(t);
            if (m.Success)
            {
                d.Size = Number(m.Groups["v"].Value);
                if (d.Done == null) d.Done = at;
                output.Add(Summary(d, make(), at, false));
                return true;
            }
            m = RxTime.Match(t);
            if (m.Success)
            {
                long? ms = Number(m.Groups["v"].Value);
                if (ms.HasValue) d.Ms = ms.Value;
                if (d.Done == null) d.Done = at;
                output.Add(Summary(d, make(), at, true));
                return true;
            }
            if (RxStart.IsMatch(t) && (d.Start == null || d.Ms.HasValue))
            {
                d = new Dl();
                apps[id] = d;
                d.Start = at;
            }
            if (RxDone.IsMatch(t)) d.Done = at;
            m = RxPeers.Match(t);
            if (m.Success) d.Peers = Number(m.Groups["v"].Value);
            m = RxHttp.Match(t);
            if (m.Success) d.Http = Number(m.Groups["v"].Value);
            if (t.IndexOf("[Win32App DO]", StringComparison.OrdinalIgnoreCase) >= 0 && d.Via == null) d.Via = "DO";
            return false;
        }

        static Obs Summary(Dl d, Obs o, DateTime at, bool say)
        {
            long? bytes = d.Size ?? d.Total;
            double? secs = d.Ms.HasValue ? d.Ms.Value / 1000.0 : d.Start.HasValue && (d.Done ?? at) > d.Start.Value ? ((d.Done ?? at) - d.Start.Value).TotalSeconds : (double?)null;
            if (bytes.HasValue) o.AddInfo("Download size", Bytes(bytes.Value));
            if (secs.HasValue) o.AddInfo("Download time", Downloads.Time(secs.Value));
            double? mbit = bytes.HasValue && secs.HasValue && secs.Value > 0 ? bytes.Value * 8 / 1e6 / secs.Value : (double?)null;
            if (mbit.HasValue) o.AddInfo("Download speed", Downloads.Speed(mbit.Value));
            if (d.Via != null) o.AddInfo("Downloaded with", Via(d.Via));
            long known = (d.Peers ?? 0) + (d.Http ?? 0);
            if (d.Peers.HasValue) o.AddInfo("From peers", Bytes(d.Peers.Value) + (known > 0 ? " (" + (d.Peers.Value * 100 / known) + " %)" : ""));
            if (d.Http.HasValue) o.AddInfo("From HTTP", Bytes(d.Http.Value) + (known > 0 ? " (" + (d.Http.Value * 100 / known) + " %)" : ""));
            if (say && bytes.HasValue && secs.HasValue)
            {
                o.Text = "Downloaded " + Bytes(bytes.Value) + " in " + Downloads.Time(secs.Value) + (mbit.HasValue ? ", " + Downloads.Speed(mbit.Value) : "")
                    + (d.Peers.HasValue && known > 0 ? ", " + (d.Peers.Value * 100 / known) + " % from peers" : "");
            }
            return o;
        }

        static string Via(string v)
        {
            return string.Equals(v, "DO", StringComparison.OrdinalIgnoreCase) ? "Delivery Optimization" : v;
        }

        // 2,742.00 or 2.742,00 as the device's culture writes them, or a plain 2742.
        public static long? Number(string s)
        {
            if (s == null) return null;
            string v = s.Replace(" ", "").Replace(" ", "").Trim();
            v = Regex.Replace(v, @"[.,]\d{1,2}$", "");
            v = v.Replace(",", "").Replace(".", "");
            long n;
            return long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : (long?)null;
        }

        public static string Bytes(long b)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (b < 1024) return b.ToString(c) + " bytes";
            if (b < 1024L * 1024) return (b / 1024.0).ToString("0.#", c) + " KB";
            if (b < 1024L * 1024 * 1024)
            {
                double mb = b / (1024.0 * 1024);
                return mb.ToString(mb < 10 ? "0.#" : "0", c) + " MB";
            }
            return (b / (1024.0 * 1024 * 1024)).ToString("0.##", c) + " GB";
        }
    }

    // What the downloads say: the Slow download finding and the download columns in Statistics.
    public static class Downloads
    {
        public static double SlowAfterSeconds = 300, SlowBelowMbit = 5;

        public static string Speed(double mbit)
        {
            return mbit.ToString(mbit < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " Mbit/s";
        }

        public static string Time(double seconds)
        {
            return seconds < 1 ? "under a second" : Txt.Duration(TimeSpan.FromSeconds(seconds));
        }

        // Download time of an item in seconds, read back from its details.
        public static double? Seconds(Item i)
        {
            string v = i == null ? null : i.Get("Download time");
            if (string.IsNullOrEmpty(v)) return null;
            if (v == "under a second") return 0.5;
            double total = 0;
            bool any = false;
            foreach (Match m in Regex.Matches(v, @"(?<n>\d+)\s*(?<u>h|min|s)\b"))
            {
                double n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                string u = m.Groups["u"].Value;
                total += u == "h" ? n * 3600 : u == "min" ? n * 60 : n;
                any = true;
            }
            return any ? total : (double?)null;
        }

        public static double? Mbit(Item i)
        {
            string v = i == null ? null : i.Get("Download speed");
            if (string.IsNullOrEmpty(v)) return null;
            Match m = Regex.Match(v, @"^(?<n>\d+(?:\.\d+)?) Mbit/s");
            return m.Success ? double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : (double?)null;
        }

        // A download that took more than 5 minutes at under 5 Mbit/s.
        public static void Apply(Session s, List<Finding> list)
        {
            foreach (Item i in s.Items)
            {
                double? secs = Seconds(i), mbit = Mbit(i);
                if (!secs.HasValue || !mbit.HasValue || secs.Value <= SlowAfterSeconds || mbit.Value >= SlowBelowMbit) continue;
                Finding f = new Finding();
                f.Kind = FindingKinds.SlowDownload;
                f.Severity = 1;
                f.ItemKey = i.Key;
                f.Title = i.DisplayName + " downloaded slowly, at " + Speed(mbit.Value);
                string peers = i.Get("From peers");
                f.Message = "It took " + i.Get("Download time") + " to download " + (i.Get("Download size") ?? "its content") + ", an average of " + Speed(mbit.Value) + "."
                    + (peers != null ? " From peers: " + peers + "." : "");
                f.Hint = "Check the network the device is on, and any proxy, VPN or firewall that slows or inspects the download from the Intune content servers. "
                    + "Delivery Optimization can take the content from other devices or a Connected Cache server: check its download mode, and run Get-DeliveryOptimizationStatus on the device to see where the bytes came from.";
                f.TimeUtc = i.EndUtc ?? i.LastUtc;
                for (int k = i.Events.Count - 1; k >= 0; k--)
                {
                    TEvent e = i.Events[k];
                    if (e.Text != null && e.Text.StartsWith("Downloaded ", StringComparison.Ordinal) && e.Text.Contains("Mbit/s"))
                    {
                        f.TimeUtc = e.TimeUtc;
                        f.Evidence = e.Evidence;
                        break;
                    }
                }
                list.Add(f);
            }
        }

        // Adds an item's download to its line in Statistics.
        public static void Count(FleetItem f, Item i)
        {
            double? secs = Seconds(i), mbit = Mbit(i);
            if (!secs.HasValue) return;
            f.Downloads++;
            f.DownloadSeconds += secs.Value;
            if (mbit.HasValue)
            {
                f.DownloadMbit += mbit.Value;
                f.Speeds++;
            }
        }
    }
}
