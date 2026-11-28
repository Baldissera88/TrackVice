namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    // What TrackVice may hand to Windows to start or open. A session, a log or a name read from storage can hold any
    // text, so nothing from them is started as it is: links are TrackVice's own https links, Explorer is started by its
    // full path, and a log goes to LogVice only when it is a file on a drive of this PC or an event channel.
    public static class LaunchGuard
    {
        // The hosts of the links TrackVice opens in the browser: GitHub, the Intune and Azure portals, and the sign-in
        // pages of Microsoft that a device code sign-in names.
        public static readonly string[] LinkHosts = {
            "github.com", "intune.microsoft.com", "endpoint.microsoft.com", "portal.azure.com", "aka.ms",
            "microsoft.com", "www.microsoft.com", "login.microsoft.com", "microsoftonline.com", "login.microsoftonline.com" };

        // A new issue on GitHub carries the text of the report in the link, up to Feedback.MaxIssueUrl.
        public const int MaxLink = 8192, MaxPath = 1024, MaxDetails = 6000;

        public static bool IsWebLink(string url)
        {
            return IsWebLink(url, LinkHosts);
        }

        // An https link to exactly one of the hosts, with no user name, port or character a shell could read as more.
        public static bool IsWebLink(string url, string[] hosts)
        {
            const string scheme = "https://";
            if (string.IsNullOrEmpty(url) || url.Length > MaxLink || hosts == null) return false;
            if (!url.StartsWith(scheme, StringComparison.Ordinal)) return false;
            foreach (char c in url)
                if (c <= ' ' || c > '~' || c == '"' || c == '\'' || c == '<' || c == '>' || c == '\\' || c == '^' || c == '`' || c == '|') return false;
            int end = url.IndexOfAny(new char[] { '/', '?', '#' }, scheme.Length);
            string host = end < 0 ? url.Substring(scheme.Length) : url.Substring(scheme.Length, end - scheme.Length);
            if (host.Length == 0 || host.IndexOfAny(new char[] { '@', ':', '%' }) >= 0) return false;
            foreach (string h in hosts) if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Opens a link in the browser when it is one of the links above, and returns whether it was handed to Windows.
        // Anything else, such as a path, a share or another scheme, would be started as a program or a file.
        public static bool OpenWeb(string url)
        {
            if (!IsWebLink(url)) return false;
            System.Diagnostics.Process.Start(url);
            return true;
        }

        // The link, or otherwise when it is not one to open: for links read from an answer, such as a release page.
        public static string WebLinkOr(string url, string otherwise)
        {
            return IsWebLink(url) ? url : otherwise;
        }

        // Notepad by its full path, so a notepad.exe in the current folder is never started in its place.
        public static string NotepadPath
        {
            get { return Path.Combine(Environment.SystemDirectory, "notepad.exe"); }
        }

        static bool CleanPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaxPath) return false;
            foreach (char c in path)
                if (c < ' ' || c == '"' || c == '<' || c == '>' || c == '|' || c == '*' || c == '?') return false;
            return true;
        }

        static bool HasParentStep(string rest)
        {
            foreach (string part in rest.Split('\\', '/')) if (part.Trim() == "..") return true;
            return false;
        }

        // A file on a drive letter: C:\folder\file. Not a share (\\server\share), a device path (\\?\, \\.\), a relative
        // path, a stream (file:stream) or an option (--find).
        public static bool IsLocalFile(string path)
        {
            if (!CleanPath(path) || path.Length < 4) return false;
            char d = path[0];
            if (!((d >= 'A' && d <= 'Z') || (d >= 'a' && d <= 'z')) || path[1] != ':' || path[2] != '\\') return false;
            string rest = path.Substring(3);
            if (rest.IndexOf(':') >= 0 || rest.StartsWith("\\", StringComparison.Ordinal)) return false;
            return !HasParentStep(rest);
        }

        // "evt:" and the name of an event channel, such as evt:Microsoft-Windows-AAD/Operational.
        public static bool IsEventChannel(string target)
        {
            const string prefix = "evt:";
            if (target == null || !target.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string name = target.Substring(prefix.Length);
            if (name.Length == 0 || name.Length > 255 || !char.IsLetterOrDigit(name[0])) return false;
            foreach (char c in name)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '/' || c == ' ')) return false;
            return true;
        }

        // The path is inside the folder, with no step back out of it.
        public static bool IsUnder(string path, string folder)
        {
            if (!CleanPath(path) || string.IsNullOrEmpty(folder)) return false;
            string root = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
            string p = path.Replace('/', '\\');
            if (root.Length < 2 || p.Length <= root.Length || !p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = p.Substring(root.Length);
            if (rest.IndexOf(':') >= 0 || rest.StartsWith("\\", StringComparison.Ordinal)) return false;
            return !HasParentStep(rest);
        }

        // The targets that may go on LogVice's command line when they come from a session TrackVice did not read from
        // this PC itself: files on a drive, files inside the folder that was opened, and event channels. The others
        // are named in refused. A path to a share would make Windows sign in to the server it names.
        public static string[] LogTargets(string[] targets, string folder, List<string> refused)
        {
            List<string> ok = new List<string>();
            if (targets == null) return ok.ToArray();
            foreach (string t in targets)
            {
                if (string.IsNullOrEmpty(t)) continue;
                if (IsEventChannel(t) || (!t.StartsWith("evt:", StringComparison.Ordinal) && (IsLocalFile(t) || IsUnder(t, folder)))) ok.Add(t);
                else if (refused != null) refused.Add(t);
            }
            return ok.ToArray();
        }

        // A device name or other read text as part of a file name TrackVice proposes: no folders, drives or dots to
        // climb with, so the file is saved where the save window shows.
        public static string FileNamePart(string text)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in (text ?? "").Trim())
            {
                if (b.Length >= 64) break;
                b.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            string s = b.ToString().Trim('_');
            return s.Length == 0 ? "device" : s;
        }

        // The report TrackVice opens after it saved it: only a web page, never a file Windows would run.
        public static bool IsReportFile(string path)
        {
            if (!CleanPath(path)) return false;
            return path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
        }

        // Explorer by its full path, so a file named explorer.exe in the current folder is never started in its place.
        public static string ExplorerPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"); }
        }

        // The command line argument that shows a folder or a file in Explorer, or null when the path is not one to show.
        // Explorer reads a comma as the start of another switch (/select, /root), so a path with one is not shown.
        public static string ExplorerArgument(string path)
        {
            if (!CleanPath(path) || path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("-", StringComparison.Ordinal) || path.IndexOf(',') >= 0) return null;
            string p = path.Length > 3 ? path.TrimEnd('\\') : path;
            // A backslash before the closing quote would escape it.
            return p.EndsWith("\\", StringComparison.Ordinal) ? "\"" + p + "\\\"" : "\"" + p + "\"";
        }

        // The text of an error for a message box, the status bar or the console: passwords, keys, tokens and the
        // signature of a storage link are hidden, as they are in a session.
        public static string Message(Exception ex)
        {
            if (ex == null) return "Unknown error";
            return Hide(ex.Message);
        }

        // The whole error with its stack, for the window that shows an error nothing caught.
        public static string Details(Exception ex)
        {
            if (ex == null) return "Unknown error";
            string s = Hide(ex.ToString());
            return s.Length > MaxDetails ? s.Substring(0, MaxDetails) + "\u2026" : s;
        }

        public static string Hide(string text)
        {
            try { return Secrets.Mask(text ?? "") ?? ""; }
            catch (Exception) { return "The text of the error could not be shown."; }
        }
    }
}
