namespace TrackVice
{
    using System;

    public enum PublishTargetKind { SetUp, NotSetUp, UseLink }

    // Where Publish agent update puts the files, and why it cannot yet when it cannot.
    public sealed class PublishTarget
    {
        public PublishTargetKind Kind;
        // The container link or folder to publish to. Empty when there is none.
        public string Link = "";
        // The storage account of a link to a container, or empty.
        public string Account = "";
        // One sentence saying what is missing. Empty when Publish can go ahead.
        public string Reason = "";

        public bool CanPublish
        {
            get { return Reason.Length == 0; }
        }

        // saved: the link kept for the customer in use. storageLink: the Create and Write link of Your storage.
        // another: the person chose to give a link or folder. typed: what is in the box then.
        public static PublishTarget Decide(string saved, string storageLink, bool another, string typed)
        {
            saved = Clean(saved);
            storageLink = Clean(storageLink);
            PublishTarget t = new PublishTarget();
            if (another)
            {
                t.Kind = PublishTargetKind.UseLink;
                t.Link = Clean(typed);
                t.Account = AccountOf(t.Link);
                if (t.Link.Length == 0) t.Reason = "Enter a link or a folder to publish to, or use Your storage.";
                return t;
            }
            string link = saved.Length > 0 ? saved : storageLink;
            if (link.Length == 0)
            {
                t.Kind = PublishTargetKind.NotSetUp;
                t.Reason = "Set up Your storage, or use a link or folder you have, to publish.";
                return t;
            }
            t.Link = link;
            t.Account = AccountOf(link);
            // A saved folder is not a storage account: it is shown in the box.
            t.Kind = t.Account.Length > 0 ? PublishTargetKind.SetUp : PublishTargetKind.UseLink;
            return t;
        }

        // The name of the storage account in a container link, such as contosotrackvice, or empty for a folder.
        public static string AccountOf(string link)
        {
            Uri u;
            if (string.IsNullOrEmpty(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out u)) return "";
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return "";
            int dot = u.Host.IndexOf('.');
            return dot > 0 ? u.Host.Substring(0, dot) : u.Host;
        }

        static string Clean(string s)
        {
            return (s ?? "").Trim().Trim('"');
        }
    }
}
