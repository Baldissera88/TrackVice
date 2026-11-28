namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // What the Key Vault, the sign-in service and the storage settings may make TrackVice do: where a token or a request
    // signed with the account key may go, which customer codes are kept, and what a vault answer may not break.
    public static class VaultStorageTrustTests
    {
        public static void Run()
        {
            CodesNeedTheUpdateKey();
            SecretForAnotherCustomer();
            HugeTimes();
            VaultLinksAndNames();
            DeviceCodeAnswer();
            Endpoints();
            VaultStorageSettings();
            KeyTagIsNotTheKey();
            SignedRequestsStayHome();
            ArmIdStaysOnArm();
            AclXml();
        }

        static VaultTarget Target()
        {
            string p;
            return VaultAddress.Parse("contoso-vault", out p);
        }

        static CustomerBook NewBook()
        {
            return new CustomerBook(new MemoryCustomerStorage(), new NoProtect());
        }

        sealed class NoProtect : ICustomerProtector
        {
            public string Protect(string plain) { return plain; }
            public string Unprotect(string stored) { return stored; }
        }

        static Customer Named(string name)
        {
            Customer c = new Customer();
            c.Name = name;
            c.AgentLocation = "https://contosoagents.blob.core.windows.net/tv-" + name.ToLowerInvariant() + "?si=viewer&sig=TESTSIGNATURE";
            return c;
        }

        static string OtherKey()
        {
            bool made;
            using (System.Security.Cryptography.RSACryptoServiceProvider k = UpdateKeys.LoadOrCreate(new MemoryKeyStore(), out made)) return UpdateKeys.PublicKey(k);
        }

        // ---------- customers ----------

        static void CodesNeedTheUpdateKey()
        {
            string mine = VaultCustomerTests.UpdateKey();
            string theirs = OtherKey();
            VaultSecret good = Secret("Contoso", VaultCustomerTests.Code(mine));
            VaultSecret planted = Secret("Fabrikam", VaultCustomerTests.Code(theirs));
            CustomerBook book = NewBook();
            MemorySecrets codes = new MemorySecrets();
            VaultGetResult r = VaultCustomers.Merge(book, new VaultSecret[] { good, planted }, codes, mine);
            T.Eq(2, r.Added, "Trust: both customers are added");
            T.Check(CustomerCodes.Load(codes, "Contoso") != null, "Trust: a code with this PC's update key is kept");
            T.Eq(null, CustomerCodes.Load(codes, "Fabrikam"), "Trust: a code with another update key is not kept");
            T.Eq(1, r.CodesNotKept, "Trust: the code not kept is counted");

            MemorySecrets none = new MemorySecrets();
            VaultGetResult noKey = VaultCustomers.Merge(NewBook(), new VaultSecret[] { good }, none, null);
            T.Eq(null, CustomerCodes.Load(none, "Contoso"), "Trust: without an update key on this PC no code is kept");
            T.Eq(1, noKey.CodesNotKept, "Trust: and that is said");

            T.Check(!VaultCustomers.CodeFits("tv1:@@@", mine), "Trust: a damaged code does not fit");
            T.Check(!VaultCustomers.CodeFits(VaultCustomerTests.Code(mine) + "\r\nWrite-Host x", mine), "Trust: a code with a line break does not fit");
            T.Check(VaultCustomers.CodeFits(VaultCustomerTests.Code(mine), mine), "Trust: the code TrackVice makes fits");
        }

        static VaultSecret Secret(string customer, string code)
        {
            VaultSecret s = new VaultSecret();
            s.Name = VaultCustomers.SecretName(customer);
            s.ContentType = VaultCustomers.ContentType;
            s.Value = VaultCustomers.ToValue(Named(customer), code);
            return s;
        }

        static void SecretForAnotherCustomer()
        {
            // Someone who may write only the secret of Fabrikam puts Contoso's details in it.
            VaultSecret s = Secret("Contoso", null);
            s.Name = VaultCustomers.SecretName("Fabrikam");
            CustomerBook book = NewBook();
            Customer c = Named("Contoso");
            book.Add(c);
            string before = book.Find("Contoso").AgentLocation;
            VaultSecret changed = Secret("Contoso", null);
            changed.Name = s.Name;
            changed.Value = changed.Value.Replace("contosoagents", "attackeragents");
            VaultGetResult r = VaultCustomers.Merge(book, new VaultSecret[] { changed }, null, null);
            T.Eq(1, r.Skipped, "Trust: a secret named for another customer is skipped");
            T.Eq(before, book.Find("Contoso").AgentLocation, "Trust: the other customer is not changed");

            VaultSecret two = Secret("Contoso", null);
            CustomerBook both = NewBook();
            both.Add(Named("Contoso"));
            both.Add(Named("Fabrikam"));
            two.Value = both.Export(true);
            T.Check(VaultCustomers.Problem(two) != null, "Trust: a secret with two customers is refused");
            T.Eq(null, VaultCustomers.Problem(Secret("Contoso A/S", null)), "Trust: a name with signs still matches its secret");
        }

        static void HugeTimes()
        {
            T.Eq(null, VaultCustomers.FromEpoch(99999999999999L), "Trust: a time past the year 9999 is no time");
            T.Check(VaultCustomers.FromEpoch(1790000000L).HasValue, "Trust: a normal time is read");
            ListVault v = new ListVault();
            v.Page = "{\"value\":[{\"id\":\"https://contoso-vault.vault.azure.net/secrets/tv-contoso\",\"contentType\":\"" + VaultCustomers.ContentType + "\",\"attributes\":{\"updated\":99999999999999}}]}";
            List<VaultSecret> l = null;
            try { l = new KeyVaultStore(v, delegate { return "token"; }, Target()).List(); }
            catch (ArgumentOutOfRangeException) { }
            T.Check(l != null && l.Count == 1 && !l[0].Updated.HasValue, "Trust: one odd time does not stop the list");
        }

        // ---------- the vault ----------

        sealed class ListVault : IVaultHttp
        {
            public string Page = "{\"value\":[]}";
            public string One;
            public readonly List<string> Urls = new List<string>();

            public WebReply Send(string method, string url, string token, string json)
            {
                Urls.Add(url);
                if (new Uri(url).AbsolutePath == "/secrets") return new WebReply(200, Page);
                return One != null ? new WebReply(200, One) : new WebReply(404, "{}");
            }
        }

        static bool ListFails(string nextLink)
        {
            ListVault v = new ListVault();
            v.Page = "{\"value\":[],\"nextLink\":\"" + nextLink + "\"}";
            try { new KeyVaultStore(v, delegate { return "token"; }, Target()).List(); }
            catch (VaultException) { return v.Urls.Count == 1; }
            return false;
        }

        static void VaultLinksAndNames()
        {
            T.Check(ListFails("https://evil.example/secrets?x=1"), "Trust: a next page on another host is not called");
            T.Check(ListFails("https://contoso-vault.vault.azure.net:8443/secrets?x=1"), "Trust: a next page on another port is not called");
            T.Check(ListFails("https://user@contoso-vault.vault.azure.net/secrets?x=1"), "Trust: a next page with a user name is not called");
            T.Check(ListFails("http://contoso-vault.vault.azure.net/secrets?x=1"), "Trust: a next page over http is not called");

            ListVault v = new ListVault();
            v.Page = "{\"value\":[{\"id\":\"https://contoso-vault.vault.azure.net/secrets/tv-a..%2f..%2fkeys\",\"contentType\":\"" + VaultCustomers.ContentType + "\"},"
                + "{\"id\":\"https://contoso-vault.vault.azure.net/secrets/tv-contoso\",\"contentType\":\"" + VaultCustomers.ContentType + "\"}]}";
            List<VaultSecret> l = new KeyVaultStore(v, delegate { return "token"; }, Target()).List();
            T.Check(l.Count == 1 && l[0].Name == "tv-contoso", "Trust: a listed name that is not a secret name is left out");

            ListVault g = new ListVault();
            g.One = "{\"id\":\"https://contoso-vault.vault.azure.net/secrets/tv-other/1\",\"value\":\"x\"}";
            bool threw = false;
            try { new KeyVaultStore(g, delegate { return "token"; }, Target()).Get("tv-contoso"); }
            catch (VaultException) { threw = true; }
            T.Check(threw, "Trust: an answer for another secret is not taken as this one");
            g.One = "{\"value\":\"x\"}";
            threw = false;
            try { new KeyVaultStore(g, delegate { return "token"; }, Target()).Get("tv-contoso"); }
            catch (VaultException) { threw = true; }
            T.Check(threw, "Trust: an answer without an id is not taken as no secret, so Save asks");
        }

        sealed class LoginWeb : IWeb
        {
            public string Answer;

            public WebReply Send(WebCall call)
            {
                return new WebReply(200, Answer);
            }
        }

        static void DeviceCodeAnswer()
        {
            LoginWeb w = new LoginWeb();
            w.Answer = "{\"device_code\":\"dc\",\"user_code\":\"ABCD\",\"verification_uri\":\"file:///C:/Windows/System32/calc.exe\",\"interval\":99999999999,\"expires_in\":99999999999}";
            DeviceCode d = new VaultSignIn(w, Target(), null, null).Start();
            T.Eq(WebLinks.DeviceLogin, d.VerificationUri, "Trust: a sign-in page that is not https is not opened");
            T.Check(d.Interval <= WebLinks.MaxInterval && d.ExpiresIn <= WebLinks.MaxExpires, "Trust: a huge wait is cut");
            w.Answer = "{\"device_code\":\"dc\",\"user_code\":\"ABCD\",\"verification_uri\":\"https://microsoft.com/devicelogin\"}";
            T.Eq("https://microsoft.com/devicelogin", new VaultSignIn(w, Target(), null, null).Start().VerificationUri, "Trust: the usual page is kept");

            T.Check(WebLinks.IsHttps("https://login.microsoft.com/device"), "Trust: https is a web page");
            T.Check(!WebLinks.IsHttps("http://login.microsoft.com/device"), "Trust: http is not opened");
            T.Check(!WebLinks.IsHttps("\\\\evil\\share\\x.exe"), "Trust: a share is not opened");
            T.Check(!WebLinks.IsHttps("https://a\" --x"), "Trust: a quote is not opened");
            T.Check(!WebLinks.IsHttps("https://user:pw@example.com/"), "Trust: a user name is not opened");
            T.Eq("adacontoso.com", WebLinks.Plain("ada\r\ncontoso.com", 120), "Trust: control characters leave a name");
        }

        // ---------- the storage settings ----------

        static OwnStorage Storage(string account, string endpoint)
        {
            OwnStorage o = new OwnStorage();
            o.Account = account;
            o.BlobEndpoint = endpoint;
            o.Key = Convert.ToBase64String(Encoding.UTF8.GetBytes("made-up key for tests"));
            o.AgentRead = endpoint.TrimEnd('/') + "/trackvice-agent?sp=r&sig=TESTSIGNATURE";
            o.AgentWrite = endpoint.TrimEnd('/') + "/trackvice-agent?sp=cw&sig=TESTSIGNATURE";
            o.ExpiresUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return o;
        }

        static void Endpoints()
        {
            T.Eq(null, StorageTrust.EndpointProblem("contosoagents", "https://contosoagents.blob.core.windows.net/"), "Trust: the public cloud");
            T.Eq(null, StorageTrust.EndpointProblem("contosoagents", "https://contosoagents.blob.core.chinacloudapi.cn"), "Trust: Azure China");
            T.Eq(null, StorageTrust.EndpointProblem("contosoagents", "https://contosoagents.blob.core.usgovcloudapi.net/"), "Trust: Azure Government");
            T.Eq(null, StorageTrust.EndpointProblem("contosoagents", "https://contosoagents.z12.blob.storage.azure.net/"), "Trust: an Azure DNS zone");
            foreach (string bad in new string[] {
                "https://evil.example/", "http://contosoagents.blob.core.windows.net/", "https://otheraccount.blob.core.windows.net/",
                "https://contosoagents.blob.core.windows.net.evil.example/", "https://contosoagents.blob.core.windows.net:444/",
                "https://x@contosoagents.blob.core.windows.net/", "https://contosoagents.blob.core.windows.net/path/", "https://evil.example/contosoagents.blob.core.windows.net" })
                T.Check(StorageTrust.EndpointProblem("contosoagents", bad) != null, "Trust: not the account's blob address: " + bad);
            T.Check(StorageTrust.EndpointProblem("Contoso/../x", "https://contoso.blob.core.windows.net/") != null, "Trust: an account name that is not one");

            string id = "/subscriptions/7c3e9a51-2b4d-4f68-9e1a-0d5c7b3f2a86/resourceGroups/rg-trackvice/providers/Microsoft.Storage/storageAccounts/contosoagents";
            T.Eq(null, StorageTrust.IdProblem(id, "contosoagents"), "Trust: the ID of the account");
            T.Check(StorageTrust.IdProblem(id, "other") != null, "Trust: the ID of another account");
            T.Check(StorageTrust.IdProblem("https://evil.example" + id, null) != null, "Trust: an ID that is an address");
            T.Check(StorageTrust.IdProblem("@evil.example" + id, null) != null, "Trust: an ID that makes another host");
            T.Check(StorageTrust.IdProblem(id + "/../../../x", null) != null, "Trust: an ID that climbs out");

            T.Eq(null, StorageTrust.Problem(Storage("contosoagents", "https://contosoagents.blob.core.windows.net/")), "Trust: settings of one account");
            OwnStorage link = Storage("contosoagents", "https://contosoagents.blob.core.windows.net/");
            link.AgentRead = "https://evil.example/trackvice-agent?sp=r&sig=x";
            T.Check(StorageTrust.Problem(link) != null, "Trust: a read link to another host");
            OwnStorage tenant = Storage("contosoagents", "https://contosoagents.blob.core.windows.net/");
            tenant.Tenant = "contoso.com/../../evil";
            T.Check(StorageTrust.Problem(tenant) != null, "Trust: a tenant that is a path");
        }

        sealed class SetupVault : IVaultHttp
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public readonly Dictionary<string, string> Tags = new Dictionary<string, string>();
            public readonly List<string> Puts = new List<string>();

            public WebReply Send(string method, string url, string token, string json)
            {
                string name = Uri.UnescapeDataString(new Uri(url).AbsolutePath.Substring(9));
                if (method == "PUT") { Puts.Add(name); return new WebReply(200, "{}"); }
                string v;
                if (!Values.TryGetValue(name, out v)) return new WebReply(404, "{}");
                Dictionary<string, object> d = SetupJson.Obj("value", v, "attributes", SetupJson.Obj("updated", 1790000000L));
                string tag;
                if (Tags.TryGetValue(name, out tag)) d["tags"] = SetupJson.Obj(VaultSetup.FingerprintTag, tag);
                return new WebReply(200, SetupJson.Write(d));
            }
        }

        sealed class Asks : IVaultSetupAsk
        {
            public int Asked;
            public bool ReplaceVaultKey(string vaultFingerprint, string localFingerprint) { Asked++; return false; }
            public bool ReplaceLocalKey(string localFingerprint, string vaultFingerprint) { Asked++; return false; }
            public bool UseVaultStorage(OwnStorage local, OwnStorage vault) { Asked++; return true; }
        }

        static VaultSetup Setup(IVaultHttp v)
        {
            return new VaultSetup(v, delegate { return "token"; }, Target());
        }

        static void VaultStorageSettings()
        {
            SetupVault v = new SetupVault();
            v.Values[VaultSetup.StorageName] = Storage("contosoagents", "https://evil.example/").Text();
            MemorySecrets secrets = new MemorySecrets();
            Asks a = new Asks();
            VaultSetupResult r = Setup(v).Get(secrets, new MemoryKeyStore(), a);
            T.Check(!r.StorageDone && OwnStorage.Load(secrets) == null, "Trust: storage settings with another address are not taken");
            T.Check(r.Text.Contains(VaultSetup.NotAzureText), "Trust: and that is said");
            T.Eq(null, VaultSetup.FromVault(v.Values[VaultSetup.StorageName]), "Trust: the status does not name them as usable");
            T.Check(Setup(v).Describe().Contains("could not be read"), "Trust: the vault status says so");

            v.Values[VaultSetup.StorageName] = Storage("contosoagents", "https://contosoagents.blob.core.windows.net/").Text();
            r = Setup(v).Get(secrets, new MemoryKeyStore(), a);
            T.Check(r.StorageDone && OwnStorage.Load(secrets) != null, "Trust: settings of the account itself are taken");
        }

        static void KeyTagIsNotTheKey()
        {
            MemoryKeyStore keys = new MemoryKeyStore();
            bool made;
            using (UpdateKeys.LoadOrCreate(keys, out made)) { }
            string fp = VaultSetup.FingerprintOf(keys.Load());
            SetupVault v = new SetupVault();
            // Someone who may write the key secret puts something else in it, with the tag of this PC's key.
            v.Values[VaultSetup.KeyName] = "bm90IGEga2V5";
            v.Tags[VaultSetup.KeyName] = fp;
            Asks a = new Asks();
            VaultSetupResult r = Setup(v).Save(null, keys, a);
            T.Eq(1, a.Asked, "Trust: a key that cannot be read is another key, so Save asks");
            T.Check(!r.Text.Contains("has this update key already"), "Trust: the tag alone is not the same key: " + r.Text);
            T.Check(!Setup(v).Describe().Contains(fp), "Trust: the vault status does not show the tag as the key");
        }

        // ---------- requests with the account key and the Azure sign-in ----------

        sealed class CountWeb : IWeb
        {
            public readonly List<string> Urls = new List<string>();
            public WebReply Send(WebCall c)
            {
                Urls.Add(c.Url);
                if (c.Url.Contains("/subscriptions?")) return new WebReply(200, "{\"value\":[]}");
                return new WebReply(200, "<SignedIdentifiers/>");
            }
        }

        static void SignedRequestsStayHome()
        {
            CountWeb w = new CountWeb();
            OwnStorage evil = Storage("contosoagents", "https://evil.example/");
            bool refused = false;
            try { ContainerAcl.Get(w, evil, "tv-contoso", DateTime.UtcNow); }
            catch (SetupException) { refused = true; }
            T.Check(refused && w.Urls.Count == 0, "Trust: no request signed with the account key goes to another host");
            refused = false;
            try { ContainerAcl.Delete(w, Storage("contosoagents", "https://contosoagents.blob.core.windows.net/"), "tv-contoso/../x", DateTime.UtcNow); }
            catch (SetupException) { refused = true; }
            T.Check(refused && w.Urls.Count == 0, "Trust: a container name that is a path is refused");
            ContainerAcl.Get(w, Storage("contosoagents", "https://contosoagents.blob.core.windows.net/"), "tv-contoso", DateTime.UtcNow);
            T.Eq(1, w.Urls.Count, "Trust: the account's own address is called");
        }

        static void ArmIdStaysOnArm()
        {
            CountWeb w = new CountWeb();
            Arm arm = new Arm(w, delegate { return "token"; });
            AzureAccount a = new AzureAccount();
            a.Name = "contosoagents";
            a.Id = "https://evil.example/subscriptions/x/resourceGroups/y/providers/Microsoft.Storage/storageAccounts/contosoagents";
            bool refused = false;
            try { new StorageSecurity(arm).Read(a); }
            catch (SetupException) { refused = true; }
            T.Check(refused && w.Urls.Count == 0, "Trust: the Azure sign-in is not sent to an ID that is an address");
            refused = false;
            try { new StorageSecurity(arm).Fix(a, StorageSecurity.TlsId, 30); }
            catch (SetupException) { refused = true; }
            T.Check(refused && w.Urls.Count == 0, "Trust: nor is a fix");

            OwnStorage own = Storage("contosoagents", "https://contosoagents.blob.core.windows.net/");
            own.Id = "@evil.example/subscriptions/x/resourceGroups/y/providers/Microsoft.Storage/storageAccounts/contosoagents";
            try { StorageFinder.Locate(arm, own); }
            catch (SetupException) { }
            bool home = w.Urls.Count > 0;
            foreach (string u in w.Urls) if (!u.StartsWith(Arm.Base + "/", StringComparison.Ordinal) || u.Contains("evil")) home = false;
            T.Check(home, "Trust: an ID that is not of the account is not used; the account is looked up by name");
        }

        static void AclXml()
        {
            bool refused = false;
            try { ContainerAcl.Parse("<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x \"xxxxxxxxxx\"><!ENTITY y \"&x;&x;&x;&x;&x;&x;&x;&x;\">]><SignedIdentifiers><SignedIdentifier><Id>&y;</Id></SignedIdentifier></SignedIdentifiers>"); }
            catch (System.Xml.XmlException) { refused = true; }
            T.Check(refused, "Trust: access policies with a document type are refused");
            refused = false;
            try { ContainerAcl.Parse("<SignedIdentifiers>" + new string(' ', ContainerAcl.MaxXml + 10) + "</SignedIdentifiers>"); }
            catch (System.Xml.XmlException) { refused = true; }
            T.Check(refused, "Trust: access policies too long to be five are refused");
            T.Eq(1, ContainerAcl.Parse("<SignedIdentifiers><SignedIdentifier><Id>upload</Id><AccessPolicy><Permission>cw</Permission></AccessPolicy></SignedIdentifier></SignedIdentifiers>").Count, "Trust: normal access policies are read");
        }
    }
}
