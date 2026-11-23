namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // Finds out whether Windows has a program for mailto: links, and opens one with a fallback when it has none.
    public static class MailHandler
    {
        const string Choice = "HKCU\\Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\mailto\\UserChoice";
        static readonly string[] ClassRoots = { "HKCU\\Software\\Classes\\", "HKLM\\SOFTWARE\\Classes\\" };

        // True when the user has chosen a program for mailto that is still installed, or, when the user has chosen none,
        // when mailto has an open command of its own. Windows merges HKCU and HKLM under HKEY_CLASSES_ROOT, so both are read.
        public static bool HasProgram(IRegSource reg)
        {
            if (reg == null) return false;
            RegValue choice = reg.Get(Choice, "ProgId");
            if (choice != null && !string.IsNullOrEmpty(choice.Data) && choice.Data.Trim().Length > 0) return Opens(reg, choice.Data.Trim());
            return Opens(reg, "mailto");
        }

        // A class opens something when its shell\open\command has a command or a DelegateExecute, or, for a packaged app,
        // when the class has an Application key.
        static bool Opens(IRegSource reg, string cls)
        {
            foreach (string root in ClassRoots)
            {
                string cmd = root + cls + "\\shell\\open\\command";
                RegValue d = reg.Get(cmd, "");
                if (d != null && !string.IsNullOrEmpty(d.Data) && d.Data.Trim().Length > 0) return true;
                RegValue dx = reg.Get(cmd, "DelegateExecute");
                if (dx != null) return true;
                if (reg.Get(root + cls + "\\Application", "ApplicationName") != null) return true;
            }
            return false;
        }

        // The subject of a mailto: link made by Feedback.MailUrl, or "".
        public static string Subject(string mailUrl)
        {
            int q = (mailUrl ?? "").IndexOf('?');
            if (q < 0) return "";
            foreach (string p in mailUrl.Substring(q + 1).Split('&'))
            {
                if (!p.StartsWith("subject=", StringComparison.Ordinal)) continue;
                try { return Uri.UnescapeDataString(p.Substring(8)); }
                catch (UriFormatException) { return ""; }
            }
            return "";
        }

        // What goes on the clipboard when there is no mail program: the subject, then the whole text.
        public static string Clip(string mailUrl, string fullText)
        {
            string s = Subject(mailUrl);
            StringBuilder b = new StringBuilder();
            if (s.Length > 0) b.Append("Subject: ").Append(s).Append("\r\n\r\n");
            b.Append((fullText ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return b.ToString();
        }

        public static string NoProgramMessage
        {
            get { return "There is no e-mail program on this PC. The text is copied: paste it into an e-mail to " + Feedback.Email; }
        }

        // Opens the link when Windows has a mail program. When it has none, or opening fails, the text is copied and the
        // result is NoProgram. launch opens the link; copy puts text on the clipboard and says whether it worked.
        public static MailResult Open(IRegSource reg, string mailUrl, string fullText, Action<string> launch, Func<string, bool> copy)
        {
            MailResult r = new MailResult();
            if (!HasProgram(reg))
            {
                r.Outcome = MailOutcome.NoProgram;
                r.Copied = copy(Clip(mailUrl, fullText));
                return r;
            }
            try
            {
                launch(mailUrl);
                r.Outcome = MailOutcome.Opened;
            }
            catch (Exception ex)
            {
                r.Outcome = MailOutcome.NoProgram;
                r.Error = ex.Message;
                r.Copied = copy(Clip(mailUrl, fullText));
            }
            return r;
        }
    }

    public enum MailOutcome { Opened, NoProgram }

    public sealed class MailResult
    {
        public MailOutcome Outcome;
        public bool Copied;
        public string Error;
    }
}
