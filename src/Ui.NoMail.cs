namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    // Shown when Windows has no program for e-mail links. The text is already on the clipboard.
    public sealed class NoMailDialog : FeedbackPage
    {
        readonly string text;

        public NoMailDialog(Theme t, string clipText, bool copied) : base("No e-mail program", t)
        {
            text = clipText;
            Size = new Size(Dpi.S(640), Dpi.S(250));
            MinimumSize = new Size(Dpi.S(480), Dpi.S(230));
            Add(new SettingRow(Glyphs.Info, "No e-mail program", copied
                ? MailHandler.NoProgramMessage + ". You can also copy the address, or write the text in a GitHub discussion."
                : "The text could not be copied. Write to " + Feedback.Email + ", or write the text in a GitHub discussion.", null));
            Secondary();
            Footer("Open a discussion", false, delegate { OpenDiscussion(text); });
            Footer("Copy address", true, delegate { Menus.Copy(Feedback.Email); });
        }
    }
}
