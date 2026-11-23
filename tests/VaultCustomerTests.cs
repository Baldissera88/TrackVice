namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    public static class VaultCustomerTests
    {
        static Customer Contoso()
        {
            Customer c = new Customer();
            c.Name = "Contoso";
            c.Tenant = "contoso.onmicrosoft.com";
            c.AgentLocation = "https://contosoagents.blob.core.windows.net/trackvice?sv=2024-11-04&sp=rl&sig=TESTSIGNATURE";
            c.SccmServer = "CM01.contoso.com";
            c.SccmSite = "cto";
            c.Notes = "Made up, for tests.";
            return c;
        }

        static Customer Fabrikam()
        {
            Customer c = new Customer();
            c.Name = "Fabrikam";
            c.GraphClientId = "6f1d3c2a-8b4e-4f7a-9c0d-2e5b7a1f3c94";
            c.AgentLocation = @"\\FS01\TrackVice$\Uploads";
            return c;
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

        static VaultTarget Target()
        {
            string p;
            return VaultAddress.Parse("contoso-vault", out p);
        }

        static KeyVaultStore Store(MemoryVault v)
        {
            return new KeyVaultStore(v, delegate { return "token"; }, Target());
        }

        static bool Valid(string name)
        {
            if (name.Length < 1 || name.Length > 127) return false;
            foreach (char c in name) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-')) return false;
            return true;
        }

        public static void Run()
        {
            Names();
            Json();
            Listing();
            Denied();
            Addresses();
            Conflicts();
            NothingDeleted();
            SignIn();
        }

        static void Names()
        {
            T.Eq("tv-contoso", VaultCustomers.SecretName("Contoso"), "plain name");
            T.Eq("tv-contoso", VaultCustomers.SecretName("  CONTOSO "), "case and spaces");
            string a = VaultCustomers.SecretName("Contoso A/S");
            T.Check(a.StartsWith("tv-contoso-a-s-") && Valid(a), "a name with signs gets a hash: " + a);
            T.Check(VaultCustomers.SecretName("Contoso A") != VaultCustomers.SecretName("Contoso-A"), "names that look alike stay apart");
            string d = VaultCustomers.SecretName("Fabrikam Æøå");
            T.Check(Valid(d) && d != VaultCustomers.SecretName("Fabrikam"), "non-ASCII letters are left out: " + d);
            string only = VaultCustomers.SecretName("Æøå");
            T.Check(Valid(only) && only.StartsWith("tv-"), "a name with no letters or digits still has a name: " + only);
            string longName = VaultCustomers.SecretName(new string('x', 300));
            T.Check(longName.Length <= 127 && Valid(longName), "a long name is cut to 127: " + longName.Length);
            string longSigns = VaultCustomers.SecretName(new string('a', 100) + " & " + new string('b', 100));
            T.Check(longSigns.Length <= 127 && Valid(longSigns), "a long name with signs is cut");
            T.Eq(VaultCustomers.SecretName("Woodgrove Bank"), VaultCustomers.SecretName("Woodgrove Bank"), "the name is the same each time");
        }

        static void Json()
        {
            CustomerBook src = NewBook();
            src.Add(Contoso());
            src.Add(Fabrikam());
            string v = VaultCustomers.ToValue(src.Find("Contoso"), "tv1:abcdef");
            T.Check(v.Contains("contosoagents.blob.core.windows.net"), "the value has the storage link");
            T.Eq("Contoso", VaultCustomers.NameIn(v), "name in the value");
            T.Eq("tv1:abcdef", VaultCustomers.CodeIn(v), "customer code in the value");
            T.Eq(null, VaultCustomers.CodeIn(VaultCustomers.ToValue(src.Find("Fabrikam"), null)), "no code when there is none");

            MemoryVault vault = new MemoryVault();
            DateTime t = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            vault.Add(VaultCustomers.SecretName("Contoso"), "Contoso", v, t);
            vault.Add(VaultCustomers.SecretName("Fabrikam"), "Fabrikam", VaultCustomers.ToValue(src.Find("Fabrikam"), null), t);
            KeyVaultStore store = Store(vault);
            List<VaultSecret> got = new List<VaultSecret>();
            foreach (VaultSecret s in store.List()) got.Add(store.Get(s.Name));

            CustomerBook dst = NewBook();
            MemorySecrets codes = new MemorySecrets();
            VaultGetResult r = VaultCustomers.Merge(dst, got, codes);
            T.Eq(2, r.Added, "two added");
            T.Eq(0, r.Updated, "none updated");
            Customer c = dst.Find("Contoso");
            T.Check(c != null && c.Tenant == "contoso.onmicrosoft.com" && c.SccmSite == "CTO" && c.Notes == "Made up, for tests." && c.SccmServer == "CM01.contoso.com", "details of Contoso");
            T.Eq(Contoso().AgentLocation, c.AgentLocation, "storage link kept");
            T.Eq("tv1:abcdef", CustomerCodes.Load(codes, "Contoso"), "customer code kept");
            T.Eq(@"\\FS01\TrackVice$\Uploads", dst.Find("Fabrikam").AgentLocation, "folder of Fabrikam");
            T.Eq("6f1d3c2a-8b4e-4f7a-9c0d-2e5b7a1f3c94", dst.Find("Fabrikam").GraphClientId, "client id of Fabrikam");
            T.Eq(null, CustomerCodes.Load(codes, "Fabrikam"), "no code for Fabrikam");

            // The same again updates and adds none.
            VaultGetResult again = VaultCustomers.Merge(dst, got, codes);
            T.Check(again.Added == 0 && again.Updated == 2, "second read updates");

            // The value is also a customers file of one customer.
            CustomerBook file = NewBook();
            T.Eq(1, file.Import(v).Added, "the value imports as a file");

            VaultGetResult bad = VaultCustomers.Merge(dst, new VaultSecret[] { new VaultSecret { Name = "tv-x", Value = "not json" } }, codes);
            T.Eq(1, bad.Skipped, "a secret that is not a customer is skipped");
        }

        static void Listing()
        {
            MemoryVault vault = new MemoryVault();
            vault.PageSize = 2;
            DateTime t = DateTime.UtcNow.AddDays(-1);
            string[] names = { "Contoso", "Fabrikam", "Woodgrove Bank", "Tailspin Toys", "Adatum" };
            foreach (string n in names)
            {
                Customer c = new Customer();
                c.Name = n;
                vault.Add(VaultCustomers.SecretName(n), n, VaultCustomers.ToValue(c, null), t);
            }
            VaultSecret other = vault.Add("other-secret", "", "x", t);
            other.ContentType = "text/plain";
            List<VaultSecret> l = Store(vault).List();
            T.Eq(5, l.Count, "five customers over three pages, other secrets left out");
            int lists = 0;
            foreach (string call in vault.Calls) if (call.StartsWith("GET https://contoso-vault.vault.azure.net/secrets?api-version=7.4")) lists++;
            T.Eq(3, lists, "three list calls");
            T.Check(l[0].RealName.Length > 0 && l[0].Updated.HasValue, "name tag and time are read");
            T.Check(vault.Calls[0].Contains("api-version=7.4"), "api version");

            MemoryVault away = new MemoryVault();
            away.Host = "evil.example.com";
            away.PageSize = 1;
            away.Add("tv-a", "A", "{}", t);
            away.Add("tv-b", "B", "{}", t);
            try { Store(away).List(); T.Check(false, "a next link to another host is refused"); }
            catch (VaultException) { T.Check(true, "next link refused"); }
        }

        static void Denied()
        {
            MemoryVault vault = new MemoryVault();
            vault.CanRead = false;
            try { Store(vault).List(); T.Check(false, "403 is an error"); }
            catch (VaultException ex)
            {
                T.Eq(403, ex.Status, "status");
                T.Eq("Your account needs the role Key Vault Secrets User on this vault (Secrets Officer to add or change).", ex.Message, "403 message");
            }
            vault.CanRead = true;
            vault.CanWrite = false;
            try { Store(vault).Put("tv-contoso", "Contoso", "{}"); T.Check(false, "write refused"); }
            catch (VaultException ex) { T.Eq(VaultAddress.RoleText, ex.Message, "write 403 message"); }

            KeyVaultStore noToken = new KeyVaultStore(vault, delegate { return null; }, Target());
            try { noToken.List(); T.Check(false, "401 is an error"); }
            catch (VaultException ex) { T.Eq(VaultAddress.RoleText, ex.Message, "401 message"); }

            WebReply fw = new WebReply(403, "{\"error\":{\"code\":\"Forbidden\",\"innererror\":{\"code\":\"ForbiddenByFirewall\"}}}");
            T.Check(KeyVaultStore.Fail(fw, "list").Message.Contains("firewall"), "firewall is told apart");
            WebReply other = new WebReply(500, "{\"error\":{\"code\":\"Oops\",\"message\":\"The vault is busy.\"}}");
            T.Check(KeyVaultStore.Fail(other, "list the secrets").Message.Contains("The vault is busy."), "other errors show the vault's text");
            T.Eq(null, Store(new MemoryVault()).Get("tv-none"), "a missing secret is null");
        }

        static void Addresses()
        {
            string p;
            VaultTarget t = VaultAddress.Parse("contoso-vault", out p);
            T.Check(t != null && t.BaseUrl == "https://contoso-vault.vault.azure.net", "a name");
            T.Eq("https://vault.azure.net/user_impersonation", t.Scope, "scope");
            T.Eq("https://contoso-vault.vault.azure.net", VaultAddress.Parse("https://Contoso-Vault.vault.azure.net/secrets/x?y=1", out p).BaseUrl, "an address with a path");
            T.Check(VaultAddress.Parse("contoso-vault.vault.azure.net", out p) != null, "an address without https:// is completed");
            VaultTarget cn = VaultAddress.Parse("https://contoso-vault.vault.azure.cn", out p);
            T.Check(cn != null && cn.Scope == "https://vault.azure.cn/user_impersonation" && cn.LoginHost == "login.partner.microsoftonline.cn", "china cloud");
            VaultTarget us = VaultAddress.Parse("https://contoso-vault.vault.usgovcloudapi.net", out p);
            T.Check(us != null && us.LoginHost == "login.microsoftonline.us", "us government cloud");

            T.Eq(null, VaultAddress.Parse("http://contoso-vault.vault.azure.net", out p), "http is refused");
            T.Check(p.Contains("https://"), "http problem says https");
            string[] bad = {
                "https://contoso-vault.example.com", "https://vault.azure.net.evil.com", "https://evil.com/contoso-vault.vault.azure.net",
                "https://contoso.vault.azure.net.evil.com", "https://a.b.vault.azure.net", "https://user@contoso-vault.vault.azure.net",
                "https://contoso-vault.vault.azure.net:8443", "ftp://contoso-vault.vault.azure.net", "", "  ", "x", "-bad-.vault.azure.net", "https://vault.azure.net"
            };
            foreach (string b in bad)
            {
                T.Check(VaultAddress.Parse(b, out p) == null && !string.IsNullOrEmpty(p), "refused: " + b);
            }
        }

        static void Conflicts()
        {
            Customer c = Contoso();
            string local = VaultCustomers.ToValue(c, null);
            DateTime sync = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            T.Eq(SaveDecision.New, VaultCustomers.Decide(local, null, sync), "no secret yet");
            VaultSecret r = new VaultSecret();
            r.Value = local;
            r.Updated = sync.AddDays(5);
            T.Eq(SaveDecision.Same, VaultCustomers.Decide(local, r, sync), "same details are not written again");
            r.Value = VaultCustomers.ToValue(Fabrikam(), null);
            T.Eq(SaveDecision.Newer, VaultCustomers.Decide(local, r, sync), "changed after the last sync");
            r.Updated = sync.AddDays(-1);
            T.Eq(SaveDecision.Replace, VaultCustomers.Decide(local, r, sync), "older than the last sync");
            T.Eq(SaveDecision.Newer, VaultCustomers.Decide(local, r, null), "never synced asks");
            r.Updated = null;
            T.Eq(SaveDecision.Newer, VaultCustomers.Decide(local, r, sync), "no time asks");

            MemoryVault vault = new MemoryVault();
            string name = VaultCustomers.SecretName("Contoso");
            vault.Add(name, "Contoso", r.Value, sync.AddHours(3));
            KeyVaultStore store = Store(vault);
            VaultSecret live = store.Get(name);
            T.Eq(SaveDecision.Newer, VaultCustomers.Decide(local, live, sync), "the vault's updated time is used");
            vault.Now = sync.AddHours(9);
            DateTime? at = store.Put(name, "Contoso", local);
            T.Check(at.HasValue && at.Value == sync.AddHours(9), "Put gives the new time");
            T.Eq(local, store.Get(name).Value, "written");
            T.Eq("Contoso", store.Get(name).RealName, "name tag written");
            T.Eq(VaultCustomers.ContentType, store.Get(name).ContentType, "content type written");
            DateTime when = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            VaultSecret mine = new VaultSecret(), theirs = new VaultSecret();
            mine.Name = "tv-contoso";
            mine.Updated = when.AddDays(1);
            theirs.Name = "tv-fabrikam";
            theirs.Updated = when.AddDays(-1);
            T.Check(VaultCustomers.AllSeen(new VaultSecret[] { mine, theirs }, new string[] { "tv-contoso" }, when), "the last sync moves on after a save when nothing else changed");
            theirs.Updated = when.AddDays(2);
            T.Check(!VaultCustomers.AllSeen(new VaultSecret[] { mine, theirs }, new string[] { "tv-contoso" }, when), "it stays when another secret changed");
            T.Check(!VaultCustomers.AllSeen(new VaultSecret[] { theirs }, new string[0], null), "it stays when there was no sync");
            DateTime? old = VaultCustomers.ReadTime(VaultCustomers.WriteTime(sync));
            T.Check(old.HasValue && old.Value == sync, "the sync time reads back");
            T.Eq(null, VaultCustomers.ReadTime("nonsense"), "a bad sync time is none");
        }

        static void NothingDeleted()
        {
            CustomerBook book = NewBook();
            book.Add(Contoso());
            book.Add(Fabrikam());
            Customer w = new Customer();
            w.Name = "Woodgrove Bank";
            book.Add(w);
            MemoryVault vault = new MemoryVault();
            vault.Add(VaultCustomers.SecretName("Fabrikam"), "Fabrikam", VaultCustomers.ToValue(Fabrikam(), null), DateTime.UtcNow);
            KeyVaultStore store = Store(vault);
            List<VaultSecret> got = new List<VaultSecret>();
            foreach (VaultSecret s in store.List()) got.Add(store.Get(s.Name));
            VaultGetResult r = VaultCustomers.Merge(book, got, null);
            T.Eq(3, book.Count, "customers that are not in the vault stay");
            T.Check(r.Added == 0 && r.Updated == 1, "one updated");
            VaultGetResult none = VaultCustomers.Merge(book, new VaultSecret[0], null);
            T.Eq(3, book.Count, "an empty vault removes nothing");
            T.Check(none.Added == 0 && none.Updated == 0, "nothing to do");

            store.Put(VaultCustomers.SecretName("Contoso"), "Contoso", VaultCustomers.ToValue(Contoso(), null));
            T.Eq(2, vault.Secrets.Count, "saving one customer leaves the other secrets");
            foreach (string call in vault.Calls) T.Check(!call.StartsWith("DELETE"), "no delete call: " + call);
            T.Check(vault.Secrets.ContainsKey("tv-fabrikam"), "Fabrikam is still in the vault");
        }

        // ---------- sign-in ----------

        sealed class FakeLogin : IWeb
        {
            public readonly List<string> Calls = new List<string>();
            public string TokenAnswer = null;
            public string DeviceAnswer = "{\"device_code\":\"dc\",\"user_code\":\"ABCD1234\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"interval\":0,\"expires_in\":60}";

            public static string Jwt()
            {
                string p = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"upn\":\"ada@contoso.com\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return "eyJhbGciOiJub25lIn0." + p + ".x";
            }

            public WebReply Send(WebCall call)
            {
                Calls.Add(call.Url + " " + call.Text);
                if (call.Url.EndsWith("/devicecode")) return new WebReply(200, DeviceAnswer);
                if (TokenAnswer != null) return new WebReply(400, TokenAnswer);
                if (call.Text.Contains("grant_type=refresh_token")) return new WebReply(200, "{\"access_token\":\"" + Jwt() + "\",\"refresh_token\":\"rt2\",\"expires_in\":3600}");
                return new WebReply(200, "{\"access_token\":\"" + Jwt() + "\",\"refresh_token\":\"rt1\",\"expires_in\":3600}");
            }
        }

        static void SignIn()
        {
            FakeLogin web = new FakeLogin();
            VaultSignIn s = new VaultSignIn(web, Target(), null, null);
            List<string> kept = new List<string>();
            s.TokenChanged = delegate (string rt) { kept.Add(rt); };
            DeviceCode d = s.Start();
            T.Eq("ABCD1234", d.UserCode, "user code");
            T.Check(web.Calls[0].StartsWith("https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode"), "organizations by default");
            T.Check(web.Calls[0].Contains(Uri.EscapeDataString("https://vault.azure.net/user_impersonation")), "the Key Vault scope is asked for");
            T.Check(web.Calls[0].Contains(GraphClient.DefaultClientId), "the Graph Command Line Tools client by default");
            s.Wait(d, new System.Threading.ManualResetEvent(false));
            T.Eq("ada@contoso.com", s.Account, "account");
            T.Eq("rt1", s.RefreshToken, "refresh token");
            T.Eq(1, kept.Count, "the new refresh token is handed out");
            T.Check(s.Token().StartsWith("eyJ"), "access token");

            VaultSignIn again = new VaultSignIn(web, Target(), "6f1d3c2a-8b4e-4f7a-9c0d-2e5b7a1f3c94", "contoso.onmicrosoft.com");
            again.Resume("rt1", "ada@contoso.com");
            T.Check(again.SignedIn, "a kept sign-in counts as signed in");
            string tok = again.Token();
            T.Check(tok.StartsWith("eyJ") && again.RefreshToken == "rt2", "the kept token gets an access token");
            T.Check(web.Calls[web.Calls.Count - 1].StartsWith("https://login.microsoftonline.com/contoso.onmicrosoft.com/") && web.Calls[web.Calls.Count - 1].Contains("6f1d3c2a-8b4e-4f7a-9c0d-2e5b7a1f3c94"), "the own app and tenant");

            web.TokenAnswer = "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS65001: The user or administrator has not consented to use the application.\"}";
            VaultSignIn blocked = new VaultSignIn(web, Target(), null, null);
            try { blocked.Wait(blocked.Start(), new System.Threading.ManualResetEvent(false)); T.Check(false, "consent error"); }
            catch (SetupException ex)
            {
                T.Check(ex.Message.Contains("AADSTS65001") && ex.Message.Contains("admin consent for Azure Key Vault user_impersonation"), "consent hint: " + ex.Message);
            }
            web.TokenAnswer = "{\"error\":\"invalid_request\",\"error_description\":\"AADSTS900144: The request body must contain the parameter.\"}";
            try { blocked.Wait(blocked.Start(), new System.Threading.ManualResetEvent(false)); T.Check(false, "other error"); }
            catch (SetupException ex) { T.Check(ex.Message.Contains("AADSTS900144") && ex.Message.Contains("user_impersonation"), "the real text and the hint: " + ex.Message); }
            T.Check(!new VaultSignIn(web, Target(), null, null).SignedIn, "not signed in at first");
        }
    }
}
