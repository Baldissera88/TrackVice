namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // Edit customer > Get it from Your storage: the container and viewer link of a customer made by hand.
    public static class CustomerStorageLinkTests
    {
        sealed class FakeProtector : ICustomerProtector
        {
            public string Protect(string plain) { return plain; }
            public string Unprotect(string stored) { return stored; }
        }

        sealed class FakeWeb : IWeb
        {
            public readonly List<WebCall> Calls = new List<WebCall>();
            public int ContainerStatus = 201;
            public string Acl = "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers />";

            public WebReply Send(WebCall c)
            {
                Calls.Add(c);
                if (c.Url.Contains("comp=acl")) return c.Method == "GET" ? new WebReply(200, Acl) : new WebReply(200, "");
                if (c.Method == "PUT" && c.Url.Contains("restype=container")) return new WebReply(ContainerStatus, "");
                return new WebReply(404, "");
            }

            public List<WebCall> All(string method, string part)
            {
                List<WebCall> l = new List<WebCall>();
                foreach (WebCall c in Calls) if (c.Method == method && c.Url.Contains(part)) l.Add(c);
                return l;
            }
        }

        const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw==";
        static readonly DateTime Now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

        static OwnStorage Own(bool withKey)
        {
            AzureAccount a = new AzureAccount();
            a.Name = "contosotrackvice";
            a.BlobEndpoint = "https://contosotrackvice.blob.core.windows.net/";
            OwnStorage o = OwnStorage.Make(a, Key, "Contoso Production", Now);
            if (!withKey) o.DropKey();
            return o;
        }

        static Customer Cust(string name)
        {
            Customer c = new Customer();
            c.Name = name;
            return c;
        }

        static string Si(string link)
        {
            int q = link.IndexOf('?');
            foreach (string p in link.Substring(q + 1).Split('&')) if (p.StartsWith("si=")) return p.Substring(3);
            return "";
        }

        public static void Run()
        {
            Made();
            Reused();
            Names();
            Decisions();
        }

        static void Made()
        {
            FakeWeb w = new FakeWeb();
            CustomerStorageResult r = CustomerStorageLink.Make(w, Own(true), "Contoso", Now);
            T.Eq("tv-contoso", r.Container, "storage link: the container is named after the customer");
            T.Eq(1, w.All("PUT", "/tv-contoso?restype=container&sv=").Count, "storage link: the container is made");
            List<WebCall> acl = w.All("PUT", "comp=acl");
            T.Eq(1, acl.Count, "storage link: the policies are set");
            T.Check(acl[0].Text.Contains("<Id>upload</Id>") && acl[0].Text.Contains("<Permission>cw</Permission>") && acl[0].Text.Contains("<Id>viewer</Id>") && acl[0].Text.Contains("<Permission>rl</Permission>"), "storage link: upload is Create, Write and viewer is Read, List");
            T.Check(!r.ContainerWasThere, "storage link: a new container is said to be new");
            T.Eq("viewer", Si(r.Viewer), "storage link: the viewer link ends with the stored access policy name");
            T.Check(r.Viewer.Contains("?si=viewer&") && r.Viewer.Contains("&sig="), "storage link: the viewer link names the policy and is signed");
            T.Eq("upload", Si(r.Upload), "storage link: the upload link uses the upload policy");
            T.Check(r.Viewer.StartsWith("https://contosotrackvice.blob.core.windows.net/tv-contoso?"), "storage link: the viewer link is for the container");
        }

        static void Reused()
        {
            FakeWeb w = new FakeWeb();
            w.ContainerStatus = 409;
            w.Acl = "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers>"
                + "<SignedIdentifier><Id>upload</Id><AccessPolicy><Start>2026-01-01T00:00:00Z</Start><Expiry>2028-01-01T00:00:00Z</Expiry><Permission>cw</Permission></AccessPolicy></SignedIdentifier>"
                + "<SignedIdentifier><Id>backup-reader</Id><AccessPolicy><Start>2026-02-01T00:00:00Z</Start><Expiry>2027-02-01T00:00:00Z</Expiry><Permission>r</Permission></AccessPolicy></SignedIdentifier>"
                + "</SignedIdentifiers>";
            CustomerStorageResult r = CustomerStorageLink.Make(w, Own(true), "Contoso", Now);
            T.Check(r.ContainerWasThere, "storage link: a container that is there is used");
            T.Eq(1, w.All("GET", "comp=acl").Count, "storage link: its policies are read first");
            string set = w.All("PUT", "comp=acl")[0].Text;
            T.Check(set.Contains("<Id>backup-reader</Id>"), "storage link: a policy that TrackVice did not make is kept");
            T.Check(set.Contains("<Id>upload-2</Id>") && set.Contains("<Id>viewer-2</Id>") && !set.Contains("<Id>upload</Id>"), "storage link: TrackVice's own policies are replaced");
            T.Eq("viewer-2", Si(r.Viewer), "storage link: the viewer link follows the new policy");
        }

        static void Names()
        {
            CustomerBook book = new CustomerBook(new MemoryCustomerStorage(), new FakeProtector());
            Customer a = book.Add(Cust("Contoso A/S"));
            book.Add(Cust("Fabrikam"));
            T.Check(CustomerStorageLink.Problem(book, "", a) != null, "storage link: an empty name asks for the name");
            T.Check(CustomerStorageLink.Problem(book, "   ", null).Contains("name"), "storage link: a blank name asks for the name");
            T.Eq(null, CustomerStorageLink.Problem(book, "Contoso A/S", a), "storage link: the customer's own name is fine");
            T.Eq(null, CustomerStorageLink.Problem(book, "Northwind", null), "storage link: a new name is fine");
            string clash = CustomerStorageLink.Problem(book, "Contoso A S", null);
            T.Eq("The customer Contoso A/S would use the same container tv-contoso-a-s. Give this customer a name that differs in letters or digits.", clash, "storage link: the same container as another customer is refused with the Add customer message");
            T.Check(CustomerStorageLink.Problem(book, "Fabrikam", a) != null, "storage link: a name that another customer has is refused");
            T.Eq(null, CustomerStorageLink.Problem(book, "Contoso A S", a), "storage link: the same container as the customer being edited is fine");
        }

        static void Decisions()
        {
            T.Check(!CustomerStorageLink.Offered(null), "storage link: no Your storage hides the button");
            T.Check(CustomerStorageLink.Offered(Own(true)), "storage link: Your storage shows the button");
            T.Check(!CustomerStorageLink.NeedsSignIn(Own(true), false), "storage link: a kept key needs no sign-in");
            T.Check(CustomerStorageLink.NeedsSignIn(Own(false), false), "storage link: no kept key needs a sign-in");
            T.Check(!CustomerStorageLink.NeedsSignIn(Own(false), true), "storage link: signed in needs no sign-in");
            bool threw = false;
            try { CustomerStorageLink.Make(new FakeWeb(), null, "Contoso", Now); } catch (SetupException) { threw = true; }
            T.Check(threw, "storage link: no Your storage is an error, not a crash");
        }
    }
}
