namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;

    // A file of a GitHub release.
    public sealed class GitHubAsset
    {
        public string Name, Url;
        // In bytes, or 0 when GitHub did not say.
        public long Size;
    }

    // Reads GitHub releases and downloads their files, for TrackVice's own updates and for LogVice.
    public static class GitHubFiles
    {
        public const long MaxDownload = 256L * 1024 * 1024;
        static readonly Regex RxSize = new Regex("\"size\"\\s*:\\s*(\\d+)");

        // The download links in a reply of releases/latest for the named files. Only files of that repository's own
        // releases are taken. The size is the one GitHub gives in the same asset, before its link.
        public static Dictionary<string, GitHubAsset> Assets(string json, string repo, string[] names)
        {
            Dictionary<string, GitHubAsset> found = new Dictionary<string, GitHubAsset>(StringComparer.OrdinalIgnoreCase);
            json = json ?? "";
            StringBuilder alt = new StringBuilder();
            foreach (string n in names)
            {
                if (alt.Length > 0) alt.Append('|');
                alt.Append(Regex.Escape(n));
            }
            Regex rx = new Regex("\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/" + Regex.Escape(repo) + "/releases/download/[^\"/]+/(" + alt + "))\"", RegexOptions.IgnoreCase);
            int after = 0;
            foreach (Match m in rx.Matches(json))
            {
                GitHubAsset a = new GitHubAsset();
                a.Url = m.Groups[1].Value;
                a.Name = m.Groups[2].Value;
                foreach (string n in names) if (string.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase)) a.Name = n;
                long size = 0;
                foreach (Match s in RxSize.Matches(json.Substring(after, m.Index - after)))
                    long.TryParse(s.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                a.Size = size;
                after = m.Index + m.Length;
                if (!found.ContainsKey(a.Name)) found[a.Name] = a;
            }
            return found;
        }

        // The largest reply read as text: the release lists are far smaller.
        public const long MaxText = 16L * 1024 * 1024;
        public const int MaxRedirects = 5;

        // Only https to GitHub's own hosts, on the default port and without a user name: the API, the release pages and
        // the storage the release files are redirected to.
        public static bool AllowedUrl(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo)) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "github.com" || h == "api.github.com" || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        // Where a redirect from url leads, checked like the first address. Throws when it leads anywhere else.
        public static string NextUrl(string url, string location)
        {
            if (string.IsNullOrEmpty(location)) throw new WebException("GitHub sent a redirect without an address.");
            Uri next;
            if (!Uri.TryCreate(new Uri(url), location.Trim(), out next)) throw new WebException("GitHub sent a redirect TrackVice cannot read.");
            string s = next.AbsoluteUri;
            if (!AllowedUrl(s)) throw new WebException("A download from GitHub was sent to " + next.Scheme + "://" + next.Host + ", which TrackVice does not trust, so it stopped.");
            return s;
        }

        public static HttpWebRequest Request(string url, int timeout)
        {
            if (!AllowedUrl(url)) throw new WebException("TrackVice downloads only over https from GitHub.");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "TrackVice/" + Program.Version;
            req.Timeout = timeout;
            req.ReadWriteTimeout = timeout;
            // Redirects are followed by Open, so that each address is checked before it is asked.
            req.AllowAutoRedirect = false;
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            if (proxy != null) proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
            req.Proxy = proxy;
            return req;
        }

        // Asks url and follows at most MaxRedirects redirects, each to an allowed address only.
        static WebResponse Open(string url, int timeout, string accept)
        {
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                HttpWebRequest req = Request(url, timeout);
                if (accept != null) req.Accept = accept;
                HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
                int code = (int)resp.StatusCode;
                if (code < 300 || code > 399) return resp;
                string location = resp.Headers["Location"];
                resp.Close();
                url = NextUrl(url, location);
            }
            throw new WebException("GitHub sent too many redirects.");
        }

        public static string FetchText(string url)
        {
            using (WebResponse resp = Open(url, 10000, "application/vnd.github+json"))
            using (Stream s = resp.GetResponseStream())
                return ReadText(s, MaxText);
        }

        // Reads UTF-8 text up to max bytes; a longer reply is refused rather than held in memory.
        public static string ReadText(Stream s, long max)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > max) throw new IOException("The reply from GitHub is larger than expected.");
                    ms.Write(buf, 0, n);
                }
                return new UTF8Encoding(false).GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        // Writes the file to a new path. progress gets the bytes so far and the whole size, or 0 when not known.
        // cancelled is asked between reads; the part file is removed when it says yes.
        public static void Download(string url, string path, Action<long, long> progress, Func<bool> cancelled)
        {
            bool ok = false;
            bool created = false;
            try
            {
                using (WebResponse resp = Open(url, 60000, null))
                using (Stream src = resp.GetResponseStream())
                {
                    if (resp.ContentLength > MaxDownload) throw new IOException("The download is larger than expected.");
                    using (FileStream dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        long length = resp.ContentLength > 0 ? resp.ContentLength : 0;
                        byte[] buf = new byte[81920];
                        int n;
                        long total = 0;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (cancelled != null && cancelled()) throw new OperationCanceledException("The download was cancelled.");
                            dst.Write(buf, 0, n);
                            total += n;
                            if (total > MaxDownload) throw new IOException("The download is larger than expected.");
                            if (progress != null) progress(total, length);
                        }
                    }
                }
                ok = true;
            }
            finally
            {
                // Only a file this download made is removed, never one that was there before.
                if (!ok && created) try { File.Delete(path); } catch (Exception) { }
            }
        }
    }
}
