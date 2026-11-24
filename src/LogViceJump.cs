namespace TrackVice
{
    using System;
    using System.Globalization;
    using System.Text;

    // Where to land in a log, and in which theme. LogVice 2.0.0 and later read these as command line options.
    public sealed class LogViceJump
    {
        // The line of the one file to open, or 0 for none.
        public int Line;
        // The time to land on, as the log shows it, or null for none.
        public DateTime? Time;
        // Text to search for from the row it landed on, or null.
        public string Find;
        // The TrackVice theme name. System and names LogVice does not know are left out.
        public string Theme;

        public LogViceJump() { }

        public LogViceJump(int line, string theme)
        {
            Line = line;
            Theme = theme;
        }
    }

    // The part of LogViceLink that needs no windows, registry or files, so the tests can run it.
    public static partial class LogViceLink
    {
        // The first LogVice version that knows --line, --time, --find and --theme. Older ones take every argument as a path.
        public static bool SupportsJump(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            int[] parts = new int[3];
            int index = 0, digits = 0;
            long value = 0;
            foreach (char c in version.Trim())
            {
                if (c >= '0' && c <= '9')
                {
                    value = value * 10 + (c - '0');
                    digits++;
                    if (value > 100000) value = 100000;
                }
                else if (c == '.' && digits > 0 && index < 2)
                {
                    parts[index++] = (int)value;
                    value = 0;
                    digits = 0;
                }
                else break;
            }
            if (digits == 0) return false;
            parts[index] = (int)value;
            return parts[0] >= 2;
        }

        // The theme option value for a TrackVice theme name, or null for System, empty and unknown names.
        public static string ThemeOption(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Palettes.Find(name) == null) return null;
            return name.Trim().ToLowerInvariant();
        }

        // The whole argument string: the targets, then the jump options when the LogVice version knows them.
        public static string BuildArguments(string[] targets, LogViceJump jump, string version)
        {
            StringBuilder args = new StringBuilder();
            int files = 0;
            bool channel = false;
            if (targets != null)
            {
                foreach (string t in targets)
                {
                    if (string.IsNullOrEmpty(t)) continue;
                    if (args.Length > 0) args.Append(' ');
                    args.Append(Quote(t));
                    if (t.StartsWith("evt:", StringComparison.Ordinal)) channel = true;
                    else files++;
                }
            }
            if (jump == null || !SupportsJump(version)) return args.ToString();
            bool line = false;
            if (jump.Line > 0 && files == 1 && !channel)
            {
                Add(args, "--line", jump.Line.ToString(CultureInfo.InvariantCulture));
                line = true;
            }
            if (!line && jump.Line <= 0 && jump.Time.HasValue)
                Add(args, "--time", jump.Time.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(jump.Find)) Add(args, "--find", jump.Find);
            string theme = ThemeOption(jump.Theme);
            if (theme != null) Add(args, "--theme", theme);
            return args.ToString();
        }

        static void Add(StringBuilder args, string option, string value)
        {
            if (args.Length > 0) args.Append(' ');
            args.Append(option).Append(' ').Append(NeedsQuotes(value) ? Quote(value) : value);
        }

        static bool NeedsQuotes(string value)
        {
            return string.IsNullOrEmpty(value) || value.IndexOfAny(new char[] { ' ', '\t', '"', '\\' }) >= 0;
        }

        // One argument the way CommandLineToArgvW reads it back: backslashes before a quote are doubled and the quote
        // is escaped, and backslashes at the end are doubled before the closing quote.
        public static string Quote(string value)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int slashes = 0;
            foreach (char c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', slashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', slashes);
                    sb.Append(c);
                }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
