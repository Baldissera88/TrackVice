namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;

    // One version in CHANGELOG.md.
    public sealed class ChangelogSection
    {
        public string Version;
        public string Title;
        public string Date;
        public bool Current;
        public readonly List<string> Items = new List<string>();
    }

    // CHANGELOG.md as it is embedded in TrackVice.exe, read for the What's new page.
    public static class Changelog
    {
        public const string ResourceName = "TrackVice.CHANGELOG.md";

        static readonly Regex Link = new Regex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

        // The text of the embedded changelog, or null when this build has none.
        public static string Embedded()
        {
            try
            {
                using (Stream s = typeof(Changelog).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null) return null;
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
                }
            }
            catch (Exception) { return null; }
        }

        // Removes the markdown marks that read badly as plain text.
        public static string Plain(string s)
        {
            if (s == null) return "";
            s = Link.Replace(s, "$1");
            return s.Replace("**", "").Replace("`", "").Trim();
        }

        // "## TrackVice x.y.z" starts a version, "- " starts an item, other lines go on with the item
        // before them, or are the date line. Newest first, as the file has them.
        public static List<ChangelogSection> Parse(string markdown, string currentVersion)
        {
            List<ChangelogSection> l = new List<ChangelogSection>();
            if (string.IsNullOrEmpty(markdown)) return l;
            ChangelogSection cur = null;
            bool inItem = false;
            foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    cur = new ChangelogSection();
                    string h = Plain(line.Substring(3));
                    cur.Title = h;
                    Match m = Regex.Match(h, @"(\d+(\.\d+)+)\s*$");
                    cur.Version = m.Success ? m.Groups[1].Value : h;
                    cur.Current = currentVersion != null && cur.Version == currentVersion;
                    l.Add(cur);
                    inItem = false;
                    continue;
                }
                if (cur == null || line.Length == 0) { inItem = false; continue; }
                if (line.StartsWith("#", StringComparison.Ordinal)) { inItem = false; continue; }
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    cur.Items.Add(Plain(line.Substring(2)));
                    inItem = true;
                }
                else if (inItem && line.StartsWith("  ", StringComparison.Ordinal))
                {
                    int last = cur.Items.Count - 1;
                    cur.Items[last] = cur.Items[last] + " " + Plain(line);
                }
                else if (cur.Date == null && cur.Items.Count == 0) cur.Date = Plain(line);
                else cur.Items.Add(Plain(line));
            }
            return l;
        }
    }
}
