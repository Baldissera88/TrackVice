namespace TrackVice.Tests
{
    using System;
    using System.IO;

    public static class CustomerTests
    {
        // Stands in for DPAPI: reversible, and never the plain text.
        sealed class FakeProtector : ICustomerProtector
        {
            public string Protect(string plain)
            {
                return "fake:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Reverse(plain)));
            }

            public string Unprotect(string stored)
            {
                if (!stored.StartsWith("fake:")) throw new FormatException("not protected");
                return Reverse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(5))));
            }

            static string Reverse(string s)
            {
                char[] c = s.ToCharArray();
                Array.Reverse(c);
                return new string(c);
            }
        }

        const string Sas = "https://contosoagents.blob.core.windows.net/trackvice?sv=2024-11-04&sp=rl&sig=TESTSIGNATURE123";

        static Customer Make(string name, string tenant, string link)
        {
            Customer c = new Customer();
            c.Name = name;
            c.Tenant = tenant;
            c.AgentLocation = link;
            return c;
        }

        static CustomerBook NewBook(MemoryCustomerStorage st)
        {
            return new CustomerBook(st, new FakeProtector());
        }

        public static void Run()
        {
            AddRenameRemove();
            UniqueNames();
            Migration();
            ExportLinks();
            ImportMerge();
            RoundTrip();
            StoredIsProtected();
            SignIn();
        }

        static void AddRenameRemove()
        {
            CustomerBook b = NewBook(new MemoryCustomerStorage());
            b.Add(Make("Contoso", "contoso.onmicrosoft.com", ""));
            b.Add(Make("  Fabrikam ", "", ""));
            T.Eq(2, b.Count, "customers: two added");
            T.Eq("Contoso", b.Current.Name, "customers: first added becomes current");
            T.Check(b.Find("fabrikam") != null && b.Find("fabrikam").Name == "Fabrikam", "customers: names are trimmed and found without case");
            b.Rename("contoso", "Contoso Ltd");
            T.Check(b.Find("Contoso") == null && b.Find("Contoso Ltd") != null, "customers: rename");
            T.Eq("Contoso Ltd", b.Current.Name, "customers: renaming the current one keeps it current");
            Customer d = b.Find("Fabrikam").Copy();
            d.Name = "Fabrikam Inc";
            d.SccmSite = "fab";
            b.Update("Fabrikam", d);
            T.Eq("FAB", b.Find("Fabrikam Inc").SccmSite, "customers: edit saves details, site code in capitals");
            T.Check(b.Remove("CONTOSO LTD"), "customers: remove without case");
            T.Eq(1, b.Count, "customers: one left");
            T.Check(b.Current == null, "customers: removing the current one leaves none current");
            T.Check(!b.Remove("Northwind"), "customers: removing an unknown name does nothing");
            T.Eq("Fabrikam Inc", b.All[0].Name, "customers: list");
        }

        static void UniqueNames()
        {
            CustomerBook b = NewBook(new MemoryCustomerStorage());
            b.Add(Make("Contoso", "", ""));
            b.Add(Make("Fabrikam", "", ""));
            bool threw = false;
            try { b.Add(Make("CONTOSO", "", "")); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "customers: a name in other case is refused");
            threw = false;
            try { b.Rename("Fabrikam", "contoso"); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "customers: renaming onto another name is refused");
            threw = false;
            try { b.Add(Make("   ", "", "")); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "customers: an empty name is refused");
            b.Rename("Fabrikam", "FABRIKAM");
            T.Eq("FABRIKAM", b.Find("fabrikam").Name, "customers: a customer may change the case of its own name");
            T.Check(b.NameProblem("contoso", null) != null && b.NameProblem("contoso", b.Find("Contoso")) == null, "customers: name check ignores the customer itself");
            T.Eq(2, b.Count, "customers: refused names add nothing");
        }

        static void Migration()
        {
            MemoryCustomerStorage st = new MemoryCustomerStorage();
            CustomerBook b = NewBook(st);
            T.Check(!b.Load(), "migrate: nothing saved yet");
            Customer legacy = new Customer();
            legacy.AgentLocation = Sas;
            legacy.Tenant = "contoso.onmicrosoft.com";
            legacy.SccmServer = "CM01.contoso.com";
            legacy.SccmSite = "CTO";
            T.Check(b.Migrate(legacy), "migrate: old values become a customer");
            T.Eq(CustomerBook.DefaultName, b.Current.Name, "migrate: called Default and current");
            T.Eq(Sas, b.Current.AgentLocation, "migrate: agent location kept");
            T.Eq("CM01.contoso.com", b.Current.SccmServer, "migrate: SCCM server kept");
            CustomerBook again = NewBook(st);
            again.Load();
            T.Check(!again.Migrate(legacy), "migrate: only on first run");
            T.Eq(1, again.Count, "migrate: no second Default");

            MemoryCustomerStorage st2 = new MemoryCustomerStorage();
            CustomerBook empty = NewBook(st2);
            empty.Load();
            T.Check(!empty.Migrate(new Customer()), "migrate: nothing to keep makes no customer");
            T.Eq(0, empty.Count, "migrate: still empty");
            T.Check(st2.Document != null, "migrate: first run is remembered");
        }

        static void ExportLinks()
        {
            CustomerBook b = NewBook(new MemoryCustomerStorage());
            Customer c = Make("Contoso", "contoso.onmicrosoft.com", Sas);
            c.Notes = "Pre-provisioning";
            b.Add(c);
            string without = b.Export(false);
            T.Check(without.IndexOf("TESTSIGNATURE123", StringComparison.Ordinal) < 0, "export: no storage link by default");
            T.Check(without.IndexOf("left out", StringComparison.Ordinal) >= 0, "export: says the links are left out");
            T.Check(without.IndexOf("contoso.onmicrosoft.com", StringComparison.Ordinal) >= 0, "export: tenant included");
            T.Check(without.IndexOf("Pre-provisioning", StringComparison.Ordinal) >= 0, "export: notes included");
            string with = b.Export(true);
            T.Check(with.IndexOf("TESTSIGNATURE123", StringComparison.Ordinal) >= 0, "export: storage link when asked");
            T.Check(with.IndexOf("Anyone who has it", StringComparison.Ordinal) >= 0, "export: says the file holds the links");
            T.Check(with.IndexOf("fake:", StringComparison.Ordinal) < 0, "export: never the encrypted form");
        }

        static void ImportMerge()
        {
            CustomerBook src = NewBook(new MemoryCustomerStorage());
            Customer a = Make("Contoso", "contoso.onmicrosoft.com", Sas);
            a.SccmServer = "CM02.contoso.com";
            src.Add(a);
            src.Add(Make("Northwind", "northwind.onmicrosoft.com", ""));

            CustomerBook dst = NewBook(new MemoryCustomerStorage());
            Customer have = Make("CONTOSO", "", @"\\FS01\Uploads");
            have.SccmServer = "CM01.contoso.com";
            have.Notes = "Keep me";
            dst.Add(have);
            ImportResult r = dst.Import(src.Export(false));
            T.Eq(1, r.Added, "import: one new customer");
            T.Eq(1, r.Updated, "import: one merged by name");
            T.Check(!r.HadLinks, "import: file without links");
            T.Eq(2, dst.Count, "import: no duplicate for the same name in other case");
            Customer m = dst.Find("contoso");
            T.Eq("CONTOSO", m.Name, "import: keeps the saved name");
            T.Eq("CM02.contoso.com", m.SccmServer, "import: takes the file's values");
            T.Eq("contoso.onmicrosoft.com", m.Tenant, "import: fills empty values");
            T.Eq(@"\\FS01\Uploads", m.AgentLocation, "import: a file without links keeps the saved link");
            T.Eq("Keep me", m.Notes, "import: empty notes in the file keep the saved notes");
            r = dst.Import(src.Export(true));
            T.Check(r.HadLinks, "import: file with links");
            T.Eq(Sas, dst.Find("Contoso").AgentLocation, "import: link taken from a file with links");
            bool threw = false;
            try { dst.Import("{\"Name\":\"x\"}"); } catch (InvalidDataException) { threw = true; }
            T.Check(threw, "import: other JSON is refused");
            threw = false;
            try { dst.Import("not json"); } catch (InvalidDataException) { threw = true; }
            T.Check(threw, "import: text that is not JSON is refused");
        }

        static void RoundTrip()
        {
            MemoryCustomerStorage st = new MemoryCustomerStorage();
            CustomerBook b = NewBook(st);
            Customer c = Make("Contoso", "contoso.onmicrosoft.com", Sas);
            c.GraphClientId = "3c8f2a51-7d4b-4e19-a6c2-91b0f5e7d834";
            c.SccmServer = "CM01.contoso.com";
            c.SccmSite = "CTO";
            c.Notes = "Two lines\nof notes";
            b.Add(c);
            b.Add(Make("Fabrikam", "", ""));
            b.SetCurrent("fabrikam");
            b.Save();
            CustomerBook r = NewBook(st);
            T.Check(r.Load(), "storage: document read back");
            T.Eq(2, r.Count, "storage: both customers");
            T.Eq("Fabrikam", r.Current.Name, "storage: current customer kept");
            Customer x = r.Find("Contoso");
            T.Eq(Sas, x.AgentLocation, "storage: agent location decrypted");
            T.Eq("3c8f2a51-7d4b-4e19-a6c2-91b0f5e7d834", x.GraphClientId, "storage: client ID");
            T.Eq("CTO", x.SccmSite, "storage: site code");
            T.Eq("Two lines\nof notes", x.Notes, "storage: notes");
        }

        static void StoredIsProtected()
        {
            MemoryCustomerStorage st = new MemoryCustomerStorage();
            CustomerBook b = NewBook(st);
            b.Add(Make("Contoso", "", Sas));
            b.Add(Make("Fabrikam", "", @"\\FS01\TrackVice$\Uploads"));
            b.Save();
            T.Check(st.Document.IndexOf("TESTSIGNATURE123", StringComparison.Ordinal) < 0, "stored: no plain SAS link");
            T.Check(st.Document.IndexOf("contosoagents", StringComparison.Ordinal) < 0, "stored: no plain storage account");
            T.Check(st.Document.IndexOf("FS01", StringComparison.Ordinal) < 0, "stored: no plain folder");
            T.Check(st.Document.IndexOf("fake:", StringComparison.Ordinal) >= 0, "stored: protected form present");

            // A link that cannot be decrypted, such as one from another Windows user, comes back empty.
            st.Document = st.Document.Replace("fake:", "other:");
            CustomerBook r = NewBook(st);
            r.Load();
            T.Eq("", r.Find("Contoso").AgentLocation, "stored: unreadable link is empty");
        }

        static void SignIn()
        {
            Customer a = Make("Contoso", "contoso.onmicrosoft.com", "");
            Customer b = Make("Contoso test", "CONTOSO.onmicrosoft.com", "");
            Customer c = Make("Fabrikam", "fabrikam.onmicrosoft.com", "");
            Customer d = Make("Any", "", "");
            T.Check(Customer.SameSignIn(a, b), "sign-in: same tenant keeps it");
            T.Check(!Customer.SameSignIn(a, c), "sign-in: another tenant drops it");
            T.Check(!Customer.SameSignIn(d, d.Copy()), "sign-in: an empty tenant never keeps it");
            b.GraphClientId = "3c8f2a51-7d4b-4e19-a6c2-91b0f5e7d834";
            T.Check(!Customer.SameSignIn(a, b), "sign-in: another app drops it");
        }
    }
}
