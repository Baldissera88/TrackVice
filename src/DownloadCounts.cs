namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net;

    // One release on GitHub and how often each of its files was downloaded.
    public sealed class ReleaseCount
    {
        public string Version = "";
        public DateTime? PublishedUtc;
        public bool Prerelease;
        public readonly List<Pair> Files = new List<Pair>();
        public long Total;
    }

    // Help > Downloads: the download counts GitHub keeps for each release. This is the only thing TrackVice learns about
    // how many use it, and it collects nothing.
    public sealed class DownloadCounts
    {
        public const string ApiUrl = "https://api.github.com/repos/" + AppUpdate.Repo + "/releases?per_page=100";
        public readonly List<ReleaseCount> Releases = new List<ReleaseCount>();
        public string Error;
        public DateTime ReadUtc;

        public long Total
        {
            get { long n = 0; foreach (ReleaseCount r in Releases) n += r.Total; return n; }
        }

        // Asks GitHub through the same code as the update check, and under the same rules: never on the command line,
        // as the agent, in Windows PE, during Windows setup or the ESP, while a task sequence runs, or as SYSTEM.
        public static DownloadCounts Read()
        {
            DownloadCounts c = new DownloadCounts();
            string blocked = AppUpdate.Blocked;
            if (blocked != null) { c.Error = blocked.Replace("No update check", "TrackVice does not ask GitHub"); return c; }
            try
            {
                Parse(AppUpdate.Fetch(ApiUrl), c);
                c.ReadUtc = DateTime.UtcNow;
            }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                c.Error = hr != null && (int)hr.StatusCode == 403 ? "GitHub says to wait: it allows 60 questions an hour from one address. Try again later." : "GitHub could not be reached.";
            }
            catch (Exception) { c.Error = "GitHub could not be reached."; }
            return c;
        }

        // Reads the reply of /releases: an array of releases, each with tag_name, published_at, draft, prerelease and assets.
        public static void Parse(string json, DownloadCounts c)
        {
            bool complete;
            List<object> all = ImeJson.Parse(json ?? "", out complete) as List<object>;
            if (all == null) { c.Error = "GitHub returned no list of releases."; return; }
            foreach (object o in all)
            {
                Dictionary<string, object> rel = o as Dictionary<string, object>;
                if (rel == null || True(rel, "draft")) continue;
                ReleaseCount r = new ReleaseCount();
                r.Version = (Str(rel, "tag_name") ?? "").Trim().TrimStart('v', 'V');
                if (r.Version.Length == 0) continue;
                r.Prerelease = True(rel, "prerelease");
                DateTime t;
                string pub = Str(rel, "published_at");
                if (pub != null && DateTime.TryParse(pub, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) r.PublishedUtc = t;
                List<object> assets = Get(rel, "assets") as List<object>;
                if (assets != null)
                    foreach (object a in assets)
                    {
                        Dictionary<string, object> d = a as Dictionary<string, object>;
                        if (d == null) continue;
                        long n = Num(d, "download_count");
                        r.Files.Add(new Pair(Str(d, "name") ?? "", n.ToString(CultureInfo.InvariantCulture)));
                        r.Total += n;
                    }
                c.Releases.Add(r);
            }
            // Newest first, by version, since a release can be published after a later one.
            c.Releases.Sort(delegate (ReleaseCount a, ReleaseCount b)
            {
                if (SemVer.IsValid(a.Version) && SemVer.IsValid(b.Version)) return SemVer.Compare(b.Version, a.Version);
                return string.CompareOrdinal(b.Version, a.Version);
            });
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            return Get(d, key) as string;
        }

        static bool True(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v is bool && (bool)v;
        }

        static long Num(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is int) return (int)v;
            return 0;
        }

        // Made-up counts for the demo and screenshots.
        public static DownloadCounts Demo()
        {
            DownloadCounts c = new DownloadCounts();
            string[][] rows =
            {
                new string[] { "1.4.0", "2026-11-02", "212", "158", "61" },
                new string[] { "1.3.2", "2026-10-14", "341", "204", "88" },
                new string[] { "1.3.0", "2026-09-22", "296", "177", "70" },
                new string[] { "1.2.1", "2026-08-30", "184", "120", "41" },
                new string[] { "1.1.2", "2026-07-11", "97", "65", "23" },
            };
            foreach (string[] x in rows)
            {
                ReleaseCount r = new ReleaseCount();
                r.Version = x[0];
                r.PublishedUtc = DateTime.ParseExact(x[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                string[] names = { AppUpdate.MsiName, AppUpdate.ExeName, "Install-TrackViceAgent.ps1" };
                for (int i = 0; i < names.Length; i++)
                {
                    r.Files.Add(new Pair(names[i], x[2 + i]));
                    r.Total += long.Parse(x[2 + i], CultureInfo.InvariantCulture);
                }
                c.Releases.Add(r);
            }
            c.ReadUtc = DateTime.UtcNow;
            return c;
        }
    }
}
