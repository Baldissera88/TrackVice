namespace TrackVice.Tests
{
    using System.Collections.Generic;

    public static class ChangelogTests
    {
        public static void Run()
        {
            string md = "# Changelog\n\n## TrackVice 2.0.9\n\n25 November 2026.\n\n- **Bold** and `code` and [a link](http://x).\n- Second item\n  goes on.\n\n## TrackVice 2.0.8\n\n24 November 2026.\n\n- Old one.\n";
            List<ChangelogSection> l = Changelog.Parse(md, "2.0.8");
            T.Eq(2, l.Count, "two versions");
            T.Eq("TrackVice 2.0.9", l[0].Title, "heading is the title");
            T.Eq("2.0.9", l[0].Version, "version from the heading");
            T.Eq("25 November 2026.", l[0].Date, "date line");
            T.Eq(2, l[0].Items.Count, "two bullets");
            T.Eq("Bold and code and a link.", l[0].Items[0], "markdown marks stripped");
            T.Eq("Second item goes on.", l[0].Items[1], "continued line joins its bullet");
            T.Check(!l[0].Current && l[1].Current, "only the running version is marked");
            T.Eq(0, Changelog.Parse("", "1.0").Count, "empty text");
            T.Eq(0, Changelog.Parse(null, "1.0").Count, "no text");
            List<ChangelogSection> real = Changelog.Parse(Changelog.Embedded(), null);
            T.Check(real.Count > 1 && real[0].Title.StartsWith("TrackVice ") && real[0].Items.Count > 0, "the embedded changelog reads");
            foreach (ChangelogSection s in real) foreach (string i in s.Items) T.Check(i.IndexOf("**") < 0 && i.IndexOf('`') < 0, "no markdown marks in " + s.Version);
        }
    }
}
