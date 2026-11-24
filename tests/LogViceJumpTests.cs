namespace TrackVice.Tests
{
    using System;

    public static class LogViceJumpTests
    {
        public static void Run()
        {
            // Which LogVice versions know the jump options.
            T.Check(!LogViceLink.SupportsJump("1.5.6"), "1.5.6 has no jump options");
            T.Check(!LogViceLink.SupportsJump("1.99.99"), "1.99.99 has no jump options");
            T.Check(LogViceLink.SupportsJump("2.0.0"), "2.0.0 has jump options");
            T.Check(LogViceLink.SupportsJump("2.1.0"), "2.1.0 has jump options");
            T.Check(LogViceLink.SupportsJump("2.0.0.0"), "2.0.0.0 has jump options");
            T.Check(LogViceLink.SupportsJump("10.0.0"), "10.0.0 has jump options");
            T.Check(LogViceLink.SupportsJump(" 2.0.0+abc "), "2.0.0 with a build suffix has jump options");
            T.Check(!LogViceLink.SupportsJump(null), "unknown version has no jump options");
            T.Check(!LogViceLink.SupportsJump(""), "empty version has no jump options");
            T.Check(!LogViceLink.SupportsJump("beta"), "unreadable version has no jump options");

            string log = @"C:\ProgramData\Contoso\Logs\AppWorkload.log";
            string v2 = "2.0.0";

            // One file with a line, and the theme.
            LogViceJump j = new LogViceJump(1234, "Vice");
            T.Eq("\"" + log + "\" --line 1234 --theme vice", LogViceLink.BuildArguments(new string[] { log }, j, v2), "one file with a line");

            // Several files with a time: no line, the time in the log's own form.
            j = new LogViceJump();
            j.Time = new DateTime(2026, 11, 24, 9, 5, 7, 42);
            j.Theme = "Amber";
            T.Eq("\"a.log\" \"b.log\" --time \"2026-11-24 09:05:07.042\" --theme amber",
                LogViceLink.BuildArguments(new string[] { "a.log", "b.log" }, j, v2), "several files with a time");

            // A line with several files is left out, and the time is not used when a line is given.
            j = new LogViceJump(10, "Dark");
            j.Time = new DateTime(2026, 11, 24, 9, 5, 7);
            T.Eq("\"a.log\" \"b.log\" --theme dark", LogViceLink.BuildArguments(new string[] { "a.log", "b.log" }, j, v2), "no line for several files");
            T.Eq("\"a.log\" --line 10 --theme dark", LogViceLink.BuildArguments(new string[] { "a.log" }, j, v2), "line wins over time");

            // An event channel gets no --line.
            string chan = "evt:Microsoft-Windows-AAD/Operational";
            T.Eq("\"" + chan + "\" --theme light", LogViceLink.BuildArguments(new string[] { chan }, new LogViceJump(5, "Light"), v2), "event channel with a line");
            T.Eq("\"a.log\" \"" + chan + "\" --theme light", LogViceLink.BuildArguments(new string[] { "a.log", chan }, new LogViceJump(5, "Light"), v2), "file and channel with a line");

            // An old LogVice gets the targets only.
            T.Eq("\"" + log + "\"", LogViceLink.BuildArguments(new string[] { log }, new LogViceJump(1234, "Vice"), "1.5.6"), "old version has no options");
            T.Eq("\"" + log + "\"", LogViceLink.BuildArguments(new string[] { log }, new LogViceJump(1234, "Vice"), null), "unknown version has no options");

            // System, empty and unknown themes are left out.
            T.Eq("\"a.log\" --line 3", LogViceLink.BuildArguments(new string[] { "a.log" }, new LogViceJump(3, "System"), v2), "System has no --theme");
            T.Eq("\"a.log\" --line 3", LogViceLink.BuildArguments(new string[] { "a.log" }, new LogViceJump(3, null), v2), "no theme name");
            T.Eq("\"a.log\" --line 3", LogViceLink.BuildArguments(new string[] { "a.log" }, new LogViceJump(3, "Contoso"), v2), "unknown theme");
            T.Eq("\"a.log\" --theme synthwave", LogViceLink.BuildArguments(new string[] { "a.log" }, new LogViceJump(0, "Synthwave"), v2), "no line, theme only");

            // No jump at all, and empty targets are skipped.
            T.Eq("\"a.log\"", LogViceLink.BuildArguments(new string[] { "a.log", "", null }, null, v2), "no jump");
            T.Eq("--theme vice", LogViceLink.BuildArguments(new string[0], new LogViceJump(1, "Vice"), v2), "no targets, no line");

            // Find text and quoting.
            j = new LogViceJump(7, null);
            j.Find = "said \"stop\" now";
            T.Eq("\"a.log\" --line 7 --find \"said \\\"stop\\\" now\"", LogViceLink.BuildArguments(new string[] { "a.log" }, j, v2), "find text with quotes");
            j.Find = @"C:\Contoso\";
            T.Eq("\"a.log\" --line 7 --find \"C:\\Contoso\\\\\"", LogViceLink.BuildArguments(new string[] { "a.log" }, j, v2), "find text ending in a backslash");
            T.Eq(@"""C:\Contoso\\""", LogViceLink.Quote(@"C:\Contoso\"), "path ending in a backslash");
            T.Eq(@"""a\\\""b""", LogViceLink.Quote("a\\\"b"), "backslashes before a quote");
            T.Eq(@"""a\b c""", LogViceLink.Quote(@"a\b c"), "backslash inside stays");
            T.Eq("\"\"", LogViceLink.Quote(""), "empty value");
            T.Eq("\"\"", LogViceLink.Quote(null), "null value");

            // What CommandLineToArgvW reads back is the original.
            foreach (string s in new string[] { @"C:\Contoso\", "said \"stop\"", "a\\\\\"b\\", @"\\CONTOSO-LT0417\logs\", "plain" })
                T.Eq(s, Unquote(LogViceLink.Quote(s)), "round trip of " + s);
        }

        // The rules of CommandLineToArgvW for one quoted argument.
        static string Unquote(string q)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int i = 1, end = q.Length - 1;
            while (i < end)
            {
                int slashes = 0;
                while (i < end && q[i] == '\\') { slashes++; i++; }
                if (i < end && q[i] == '"') { sb.Append('\\', slashes / 2); if (slashes % 2 == 1) { sb.Append('"'); i++; } else break; }
                else if (i >= end) sb.Append('\\', slashes / 2);
                else { sb.Append('\\', slashes); sb.Append(q[i]); i++; }
            }
            return sb.ToString();
        }
    }
}
