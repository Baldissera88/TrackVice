namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class MailTests
    {
        const string Choice = "HKCU\\Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\mailto\\UserChoice";

        public static void Run()
        {
            Detect();
            Opening();
            Text();
        }

        static void Detect()
        {
            MemoryRegistry none = new MemoryRegistry();
            T.Check(!MailHandler.HasProgram(none), "an empty registry has no mail program");
            T.Check(!MailHandler.HasProgram(null), "no registry, no mail program");

            MemoryRegistry own = new MemoryRegistry();
            own.Sz("HKLM\\SOFTWARE\\Classes\\mailto\\shell\\open\\command", "", "\"C:\\Program Files\\Contoso Mail\\mail.exe\" \"%1\"");
            T.Check(MailHandler.HasProgram(own), "mailto with an open command and no user choice has a program");

            MemoryRegistry user = new MemoryRegistry();
            user.Sz(Choice, "ProgId", "Contoso.Mail.Url");
            user.Sz("HKLM\\SOFTWARE\\Classes\\Contoso.Mail.Url\\shell\\open\\command", "", "mail.exe \"%1\"");
            T.Check(MailHandler.HasProgram(user), "the chosen program, with a command under HKLM, counts");

            MemoryRegistry perUser = new MemoryRegistry();
            perUser.Sz(Choice, "ProgId", "Contoso.Mail.Url");
            perUser.Sz("HKCU\\Software\\Classes\\Contoso.Mail.Url\\shell\\open\\command", "", "mail.exe \"%1\"");
            T.Check(MailHandler.HasProgram(perUser), "the chosen program, with a command under HKCU, counts");

            MemoryRegistry gone = new MemoryRegistry();
            gone.Sz(Choice, "ProgId", "Contoso.Mail.Url");
            gone.Sz("HKLM\\SOFTWARE\\Classes\\mailto\\shell\\open\\command", "", "mail.exe \"%1\"");
            T.Check(!MailHandler.HasProgram(gone), "a chosen program that is not registered has no program, whatever mailto says");

            MemoryRegistry packaged = new MemoryRegistry();
            packaged.Sz(Choice, "ProgId", "AppXcontoso0000");
            packaged.Sz("HKLM\\SOFTWARE\\Classes\\AppXcontoso0000\\Application", "ApplicationName", "@{Contoso.Mail}");
            T.Check(MailHandler.HasProgram(packaged), "a packaged app with an Application key counts");

            MemoryRegistry delegateExec = new MemoryRegistry();
            delegateExec.Sz("HKLM\\SOFTWARE\\Classes\\mailto\\shell\\open\\command", "DelegateExecute", "{00000000-0000-0000-0000-000000000000}");
            T.Check(MailHandler.HasProgram(delegateExec), "a command with DelegateExecute counts");

            MemoryRegistry blank = new MemoryRegistry();
            blank.Sz(Choice, "ProgId", "  ");
            blank.Sz("HKLM\\SOFTWARE\\Classes\\mailto\\shell\\open\\command", "", "");
            T.Check(!MailHandler.HasProgram(blank), "blank values count as none");
        }

        static MemoryRegistry WithProgram()
        {
            MemoryRegistry r = new MemoryRegistry();
            r.Sz("HKLM\\SOFTWARE\\Classes\\mailto\\shell\\open\\command", "", "mail.exe \"%1\"");
            return r;
        }

        static void Opening()
        {
            string url = Feedback.MailUrl("A note from CONTOSO-PC01", "Line one\nLine two", Feedback.MaxMailUrl);
            List<string> launched = new List<string>();
            List<string> copied = new List<string>();
            Func<string, bool> copy = delegate (string s) { copied.Add(s); return true; };

            MailResult ok = MailHandler.Open(WithProgram(), url, "Line one\nLine two", delegate (string u) { launched.Add(u); }, copy);
            T.Eq(MailOutcome.Opened, ok.Outcome, "a PC with a mail program opens the link");
            T.Eq(1, launched.Count, "the link was opened once");
            T.Eq(url, launched.Count > 0 ? launched[0] : "", "the link is the mailto link");
            T.Eq(0, copied.Count, "nothing is copied when the link opens");

            launched.Clear();
            MailResult none = MailHandler.Open(new MemoryRegistry(), url, "Line one\nLine two", delegate (string u) { launched.Add(u); }, copy);
            T.Eq(MailOutcome.NoProgram, none.Outcome, "a PC without a mail program is told so");
            T.Eq(0, launched.Count, "the link is not opened without a program");
            T.Check(none.Copied, "the copy worked");
            T.Eq(1, copied.Count, "the text is copied once");
            T.Eq("Subject: A note from CONTOSO-PC01\r\n\r\nLine one\r\nLine two", copied[0], "the clipboard has the subject and the text");

            copied.Clear();
            MailResult fail = MailHandler.Open(WithProgram(), url, "Body", delegate (string u) { throw new InvalidOperationException("No application is associated"); }, copy);
            T.Eq(MailOutcome.NoProgram, fail.Outcome, "a failed open is treated as no program");
            T.Eq("No application is associated", fail.Error, "the error is kept");
            T.Eq(1, copied.Count, "the text is copied after a failed open");

            MailResult busy = MailHandler.Open(new MemoryRegistry(), url, "Body", delegate (string u) { }, delegate (string s) { return false; });
            T.Check(busy.Outcome == MailOutcome.NoProgram && !busy.Copied, "a busy clipboard is reported as not copied");
        }

        static void Text()
        {
            string url = Feedback.MailUrl("Tell the author & more", "x", Feedback.MaxMailUrl);
            T.Eq("Tell the author & more", MailHandler.Subject(url), "the subject is read back from the link");
            T.Eq("", MailHandler.Subject("mailto:x@example.com"), "a link without a subject gives none");
            T.Eq("", MailHandler.Subject(null), "no link gives no subject");
            T.Eq("Body", MailHandler.Clip("mailto:x@example.com", "Body"), "without a subject only the text is copied");
            T.Check(MailHandler.NoProgramMessage.IndexOf("There is no e-mail program on this PC. The text is copied: paste it into an e-mail to " + Feedback.Email, StringComparison.Ordinal) == 0, "the message is as the roadmap says");
        }
    }
}
