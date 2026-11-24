namespace TrackVice
{
    using System;

    // What Edit customer > Get it from Your storage made: the container and the two links.
    public sealed class CustomerStorageResult
    {
        public string Container = "", Viewer = "", Upload = "";
        public bool ContainerWasThere;
        public DateTime ExpiresUtc;
    }

    // Makes the container and viewer link of a customer that was added by hand or before Your storage, with the
    // same steps as Add customer. The Azure calls go through IWeb.
    public static class CustomerStorageLink
    {
        // The button is shown only when Your storage is set up on this PC.
        public static bool Offered(OwnStorage own)
        {
            return own != null;
        }

        // True when the account key is not kept and no sign-in has read it yet.
        public static bool NeedsSignIn(OwnStorage own, bool signedIn)
        {
            return own != null && !own.KeyKept && !signedIn;
        }

        // Null when the container can be made for this name, otherwise what is wrong.
        public static string Problem(CustomerBook book, string name, Customer editing)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return "Enter the name of the customer first. The container is named after it.";
            string p = book.NameProblem(n, editing);
            if (p != null) return p;
            return AddCustomerRun.ContainerClash(book, n, editing);
        }

        // own must hold the account key. A container that is there is used; only TrackVice's own policies are replaced.
        public static CustomerStorageResult Make(IWeb web, OwnStorage own, string name, DateTime nowUtc)
        {
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = (name ?? "").Trim();
            r.MakeStorage(web, own, nowUtc);
            CustomerStorageResult s = new CustomerStorageResult();
            s.Container = r.Container;
            s.Viewer = r.Viewer;
            s.Upload = r.Upload;
            s.ContainerWasThere = r.ContainerWasThere;
            s.ExpiresUtc = r.LinksExpireUtc;
            return s;
        }
    }
}
