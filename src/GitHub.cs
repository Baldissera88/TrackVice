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

        public static HttpWebRequest Request(string url, int timeout)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "TrackVice/" + Program.Version;
            req.Timeout = timeout;
            req.ReadWriteTimeout = timeout;
            req.AllowAutoRedirect = true;
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            if (proxy != null) proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
            req.Proxy = proxy;
            return req;
        }

        public static string FetchText(string url)
        {
            HttpWebRequest req = Request(url, 10000);
            req.Accept = "application/vnd.github+json";
            using (WebResponse resp = req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // Writes the file to a new path. progress gets the bytes so far and the whole size, or 0 when not known.
        // cancelled is asked between reads; the part file is removed when it says yes.
        public static void Download(string url, string path, Action<long, long> progress, Func<bool> cancelled)
        {
            HttpWebRequest req = Request(url, 60000);
            bool ok = false;
            try
            {
                using (WebResponse resp = req.GetResponse())
                using (Stream src = resp.GetResponseStream())
                using (FileStream dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
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
                ok = true;
            }
            finally
            {
                if (!ok) try { File.Delete(path); } catch (Exception) { }
            }
        }
    }
}
