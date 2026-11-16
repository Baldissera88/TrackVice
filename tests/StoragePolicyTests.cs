namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;

    // Stored access policies on the customers' containers, Remove customer, New links, the lifecycle rule, the security
    // check of your storage, the search for the account in use and the mode that keeps no account key.
    public static class StoragePolicyTests
    {
        sealed class FakeWeb : IWeb
        {
            public readonly List<WebCall> Calls = new List<WebCall>();
            public readonly List<Func<WebCall, WebReply>> Handlers = new List<Func<WebCall, WebReply>>();

            public WebReply Send(WebCall c)
            {
                Calls.Add(c);
                foreach (Func<WebCall, WebReply> h in Handlers)
                {
                    WebReply r = h(c);
                    if (r != null) return r;
                }
                return new WebReply(404, "{\"error\":{\"code\":\"NotFound\",\"message\":\"no fake answer\"}}");
            }

            public void On(string method, string urlPart, int status, string body)
            {
                Handlers.Add(delegate (WebCall c) { return c.Method == method && c.Url.Contains(urlPart) ? new WebReply(status, body) : null; });
            }

            public List<WebCall> All(string method, string urlPart)
            {
                List<WebCall> l = new List<WebCall>();
                foreach (WebCall c in Calls) if (c.Method == method && c.Url.Contains(urlPart)) l.Add(c);
                return l;
            }
        }

        // A made-up 32-byte key, the bytes 1 to 32.
        static readonly string Key = Convert.ToBase64String(Bytes(1, 32));
        const string Sub = "5d2a7c91-3e4f-4b60-8a1d-9c0e2f4b6a73";
        const string AccountId = "/subscriptions/" + Sub + "/resourceGroups/rg-trackvice/providers/Microsoft.Storage/storageAccounts/contosotrackvice";
        static readonly DateTime Now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

        static byte[] Bytes(int from, int n)
        {
            byte[] b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(from + i);
            return b;
        }

        static string Hmac(string s)
        {
            using (HMACSHA256 h = new HMACSHA256(Convert.FromBase64String(Key))) return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        static Dictionary<string, string> QueryOf(string link)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            int q = link.IndexOf('?');
            if (q < 0) return d;
            foreach (string p in link.Substring(q + 1).Split('&'))
            {
                int e = p.IndexOf('=');
                if (e > 0) d[p.Substring(0, e)] = Uri.UnescapeDataString(p.Substring(e + 1));
            }
            return d;
        }

        static OwnStorage Own()
        {
            AzureAccount a = new AzureAccount();
            a.Id = AccountId;
            a.Name = "contosotrackvice";
            a.Location = "westeurope";
            a.BlobEndpoint = "https://contosotrackvice.blob.core.windows.net/";
            return OwnStorage.Make(a, Key, "Contoso Production", Now);
        }

        static AzureAccount Account()
        {
            AzureAccount a = new AzureAccount();
            a.Id = AccountId;
            a.Name = "contosotrackvice";
            return a;
        }

        const string Acl = "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers>"
            + "<SignedIdentifier><Id>upload</Id><AccessPolicy><Start>2026-01-01T00:00:00Z</Start><Expiry>2028-01-01T00:00:00Z</Expiry><Permission>cw</Permission></AccessPolicy></SignedIdentifier>"
            + "<SignedIdentifier><Id>viewer</Id><AccessPolicy><Start>2026-01-01T00:00:00Z</Start><Expiry>2028-01-01T00:00:00Z</Expiry><Permission>rl</Permission></AccessPolicy></SignedIdentifier>"
            + "<SignedIdentifier><Id>backup-reader</Id><AccessPolicy><Start>2026-02-01T00:00:00.0000000Z</Start><Expiry>2027-02-01T00:00:00.0000000Z</Expiry><Permission>r</Permission></AccessPolicy></SignedIdentifier>"
            + "</SignedIdentifiers>";

        public static void Run()
        {
            PolicyLinks();
            SharedKeySigning();
            NewLinks();
            Remove();
            LifecycleRule();
            Security();
            Preselect();
            KeyNotKept();
            OwnAzureContainers();
        }

        static void PolicyLinks()
        {
            T.Eq("\n\n\n/blob/contosotrackvice/tv-contoso\nupload\n\nhttps\n2022-11-02\nc\n\n\n\n\n\n\n", PolicySas.StringToSign("/blob/contosotrackvice/tv-contoso", "upload", "https", "2022-11-02", "c"), "policy sas: the string to sign of the docs, with the policy fields empty");
            FakeWeb w = new FakeWeb();
            w.On("PUT", "/tv-contoso?restype=container&sv=", 201, "");
            w.On("PUT", "/tv-contoso?restype=container&comp=acl", 200, "");
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            r.MakeStorage(w, Own(), Now);
            List<WebCall> acl = w.All("PUT", "comp=acl");
            T.Eq(1, acl.Count, "policy: one Set Container ACL");
            T.Eq(0, w.All("GET", "comp=acl").Count, "policy: a new container is not read first");
            T.Eq("application/xml", acl[0].ContentType, "policy: XML body");
            string expected = "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers>"
                + "<SignedIdentifier><Id>upload</Id><AccessPolicy><Start>2026-11-16T08:45:00Z</Start><Expiry>2028-11-15T09:00:00Z</Expiry><Permission>cw</Permission></AccessPolicy></SignedIdentifier>"
                + "<SignedIdentifier><Id>viewer</Id><AccessPolicy><Start>2026-11-16T08:45:00Z</Start><Expiry>2028-11-15T09:00:00Z</Expiry><Permission>rl</Permission></AccessPolicy></SignedIdentifier>"
                + "</SignedIdentifiers>";
            T.Eq(expected, acl[0].Text, "policy: upload (Create, Write) and viewer (Read, List), valid 2 years");
            T.Check(acl[0].Headers.ContainsKey("Authorization") && acl[0].Headers["Authorization"].StartsWith("SharedKey contosotrackvice:"), "policy: signed with Shared Key");
            Dictionary<string, string> u = QueryOf(r.Upload), v = QueryOf(r.Viewer);
            T.Check(r.Upload.StartsWith("https://contosotrackvice.blob.core.windows.net/tv-contoso?si=upload&"), "policy: upload link names the policy");
            T.Check(!u.ContainsKey("sp") && !u.ContainsKey("st") && !u.ContainsKey("se"), "policy: the link leaves out what the policy holds");
            T.Eq("https", u["spr"], "policy: https only");
            T.Eq("c", u["sr"], "policy: container link");
            T.Eq(Hmac(PolicySas.StringToSign("/blob/contosotrackvice/tv-contoso", "upload", "https", SasLinks.Version, "c")), u["sig"], "policy: upload link signature");
            T.Eq("viewer", v["si"], "policy: viewer link names the viewer policy");
            T.Eq(Hmac(PolicySas.StringToSign("/blob/contosotrackvice/tv-contoso", "viewer", "https", SasLinks.Version, "c")), v["sig"], "policy: viewer link signature");

            List<AccessPolicy> parsed = ContainerAcl.Parse(Acl);
            T.Eq(3, parsed.Count, "acl: three policies read");
            T.Eq("2026-02-01T00:00:00.0000000Z", parsed[2].Start, "acl: times are kept as written");
            T.Eq(0, ContainerAcl.Parse("").Count, "acl: an empty body has none");
        }

        static void SharedKeySigning()
        {
            WebCall c = new WebCall();
            c.Method = "PUT";
            c.Url = "https://contosotrackvice.blob.core.windows.net/tv-contoso?restype=container&comp=acl";
            c.ContentType = "application/xml";
            c.Body = Encoding.UTF8.GetBytes("<SignedIdentifiers />");
            SharedKey.Sign(c, "contosotrackvice", Key, Now);
            string sts = "PUT\n\n\n21\n\napplication/xml\n\n\n\n\n\n\nx-ms-date:Mon, 16 Nov 2026 09:00:00 GMT\nx-ms-version:2022-11-02\n/contosotrackvice/tv-contoso\ncomp:acl\nrestype:container";
            T.Eq(sts, SharedKey.StringToSign(c, "contosotrackvice"), "shared key: the string to sign of the docs");
            T.Eq("SharedKey contosotrackvice:" + Hmac(sts), c.Headers["Authorization"], "shared key: the Authorization header");
            WebCall d = new WebCall();
            d.Method = "PUT";
            d.Url = c.Url;
            SharedKey.Sign(d, "contosotrackvice", Key, Now);
            T.Check(SharedKey.StringToSign(d, "contosotrackvice").StartsWith("PUT\n\n\n\n\n\n"), "shared key: no length and no type without a body");
        }

        static FakeWeb AclWeb(string acl)
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", "/tv-contoso?restype=container&comp=acl", 200, acl);
            w.On("PUT", "/tv-contoso?restype=container&comp=acl", 200, "");
            w.On("DELETE", "/tv-contoso?restype=container", 202, "");
            return w;
        }

        static void NewLinks()
        {
            FakeWeb w = AclWeb(Acl);
            OwnStorage own = Own();
            CustomerCode code = new CustomerCode();
            code.Name = "Contoso";
            code.Upload = PolicySas.Container(own.BlobEndpoint, own.Account, "tv-contoso", "upload", Key);
            code.Agent = own.AgentRead;
            using (RSACryptoServiceProvider k = UpdateKeys.Create()) code.Key = UpdateKeys.PublicKey(k);
            code.Alert = "https://contoso.example/workflows/alerts";
            CustomerRenewal r = CustomerRenewal.Make(w, own, code.ToString(), Now);
            T.Eq("upload-2", r.Links.UploadPolicy, "new links: a new name for the upload policy");
            T.Eq("viewer-2", r.Links.ViewerPolicy, "new links: a new name for the viewer policy");
            string body = w.All("PUT", "comp=acl")[0].Text;
            T.Check(!body.Contains("<Id>upload</Id>") && !body.Contains("<Id>viewer</Id>"), "new links: the old policies are removed, so the old links stop");
            T.Check(body.Contains("<Id>upload-2</Id>") && body.Contains("<Id>viewer-2</Id>"), "new links: the new policies are set");
            T.Check(body.Contains("<Id>backup-reader</Id><AccessPolicy><Start>2026-02-01T00:00:00.0000000Z</Start>"), "new links: other policies are kept as they were");
            T.Eq(r.Links.Upload, r.Code.Upload, "new links: the code has the new upload link");
            T.Eq(code.Alert, r.Code.Alert, "new links: the rest of the code is kept");
            T.Check(!r.OldLinksWithoutPolicy, "new links: the old links had a policy");
            T.Eq(3, CustomerAccess.Generation("viewer-3"), "new links: generation of a name");
            T.Eq(0, CustomerAccess.Generation("uploader"), "new links: a name TrackVice did not make");

            FakeWeb full = AclWeb(Acl.Replace("</SignedIdentifiers>", "<SignedIdentifier><Id>a</Id><AccessPolicy /></SignedIdentifier><SignedIdentifier><Id>b</Id><AccessPolicy /></SignedIdentifier><SignedIdentifier><Id>c</Id><AccessPolicy /></SignedIdentifier></SignedIdentifiers>"));
            bool threw = false;
            try { CustomerAccess.Grant(full, own, "tv-contoso", false, Now, 730); } catch (SetupException) { threw = true; }
            T.Check(threw && full.All("PUT", "comp=acl").Count == 0, "new links: more than 5 policies is refused before anything is sent");

            code.Upload = "https://fabrikamagents.blob.core.windows.net/trackvice?sp=cw&sig=x";
            threw = false;
            try { CustomerRenewal.Make(AclWeb(Acl), own, code.ToString(), Now); } catch (SetupException) { threw = true; }
            T.Check(threw, "new links: not for data in the customer's own Azure");
        }

        static void Remove()
        {
            OwnStorage own = Own();
            FakeWeb w = AclWeb(Acl);
            List<string> done = CustomerAccess.Remove(w, own, "tv-contoso", false, Now);
            List<WebCall> put = w.All("PUT", "comp=acl");
            T.Eq(1, put.Count, "remove: the policies are set once");
            T.Check(!put[0].Text.Contains("<Id>upload</Id>") && !put[0].Text.Contains("<Id>viewer</Id>") && put[0].Text.Contains("<Id>backup-reader</Id>"), "remove: TrackVice's policies go, others stay");
            T.Eq(0, w.All("DELETE", "").Count, "remove: the container is kept unless chosen");
            T.Check(done.Count == 1 && done[0].Contains("upload and viewer"), "remove: says what was removed");

            FakeWeb w2 = AclWeb(Acl.Replace("<SignedIdentifier><Id>backup-reader</Id><AccessPolicy><Start>2026-02-01T00:00:00.0000000Z</Start><Expiry>2027-02-01T00:00:00.0000000Z</Expiry><Permission>r</Permission></AccessPolicy></SignedIdentifier>", ""));
            List<string> done2 = CustomerAccess.Remove(w2, own, "tv-contoso", true, Now);
            WebCall p2 = w2.All("PUT", "comp=acl")[0];
            T.Check(p2.Body == null, "remove: no body removes every policy");
            List<WebCall> del = w2.All("DELETE", "/tv-contoso?restype=container");
            T.Eq(1, del.Count, "remove: the container is deleted when chosen");
            T.Check(del[0].Headers["Authorization"].StartsWith("SharedKey contosotrackvice:"), "remove: the delete is signed with Shared Key");
            T.Check(done2[done2.Count - 1].Contains("Deleted the container tv-contoso"), "remove: says the container was deleted");

            T.Eq("tv-contoso", CustomerAccess.ContainerOf(own, "https://contosotrackvice.blob.core.windows.net/tv-contoso?si=viewer&sig=x"), "remove: the container of a link in your storage");
            T.Eq(null, CustomerAccess.ContainerOf(own, "https://fabrikamagents.blob.core.windows.net/trackvice?sp=rl&sig=x"), "remove: not a container in your storage");
            T.Eq(null, CustomerAccess.ContainerOf(own, "https://contosotrackvice.blob.core.windows.net/trackvice-agent?sp=r&sig=x"), "remove: never the agent container");
            T.Check(!CustomerAccess.UsesPolicy("https://contosotrackvice.blob.core.windows.net/tv-contoso?sp=cw&sig=x"), "remove: a link without a policy is told apart");
        }

        static FakeWeb ArmWeb(string rules)
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", AccountId + "/managementPolicies/default?api-version=", rules == null ? 404 : 200, rules == null ? "{\"error\":{\"code\":\"ManagementPolicyNotFound\"}}" : "{\"properties\":{\"policy\":{\"rules\":[" + rules + "]}}}");
            w.On("PUT", AccountId + "/managementPolicies/default?api-version=", 200, "{}");
            return w;
        }

        static void LifecycleRule()
        {
            string other = "{\"name\":\"keep-backups\",\"enabled\":true,\"type\":\"Lifecycle\",\"definition\":{\"actions\":{\"baseBlob\":{\"tierToCool\":{\"daysAfterModificationGreaterThan\":30}}},\"filters\":{\"blobTypes\":[\"blockBlob\"],\"prefixMatch\":[\"backups/\"]}}}";
            string cleanup = "{\"name\":\"trackvice-cleanup\",\"enabled\":true,\"type\":\"Lifecycle\",\"definition\":{\"actions\":{\"baseBlob\":{\"delete\":{\"daysAfterModificationGreaterThan\":30}}},\"filters\":{\"blobTypes\":[\"blockBlob\"],\"prefixMatch\":[\"trackvice/sessions/\",\"trackvice/logs/\"]}}}";
            string old = "{\"name\":\"trackvice-customers\",\"enabled\":true,\"type\":\"Lifecycle\",\"definition\":{\"actions\":{\"baseBlob\":{\"delete\":{\"daysAfterModificationGreaterThan\":90}}},\"filters\":{\"blobTypes\":[\"blockBlob\"],\"prefixMatch\":[\"tv-\"]}}}";
            FakeWeb w = ArmWeb(other + "," + old + "," + cleanup);
            new StorageSecurity(new Arm(w, delegate { return "arm"; })).SetCustomerRule(Account(), 30);
            WebCall put = w.All("PUT", "/managementPolicies/default")[0];
            string rule = SetupJson.Write(StorageSecurity.CustomerRule(30));
            T.Eq("{\"properties\":{\"policy\":{\"rules\":[" + SetupJson.Write(ImeJson.Parse(other)) + "," + rule + "," + SetupJson.Write(ImeJson.Parse(cleanup)) + "]}}}", put.Text, "rule: other rules kept in place, only trackvice-customers replaced");
            T.Check(rule.Contains("\"name\":\"trackvice-customers\"") && rule.Contains("\"prefixMatch\":[\"tv-\"]") && rule.Contains("\"blobTypes\":[\"blockBlob\"]") && rule.Contains("\"daysAfterModificationGreaterThan\":30"), "rule: block blobs under tv-, deleted after 30 days");
            T.Check(put.Url.Contains("api-version=" + Arm.StorageApi), "rule: the storage api-version");
            T.Eq(BlobContainers.NameFor("Contoso").Substring(0, 3), StorageSecurity.Prefix, "rule: the prefix is how Add customer names containers");

            FakeWeb w2 = ArmWeb(null);
            new StorageSecurity(new Arm(w2, delegate { return "arm"; })).SetCustomerRule(Account(), 14);
            T.Eq("{\"properties\":{\"policy\":{\"rules\":[" + SetupJson.Write(StorageSecurity.CustomerRule(14)) + "]}}}", w2.All("PUT", "/managementPolicies/default")[0].Text, "rule: an account without a policy gets only this rule");
        }

        static FakeWeb SecurityWeb()
        {
            FakeWeb w = ArmWeb(null);
            w.On("GET", AccountId + "?api-version=", 200, "{\"id\":\"" + AccountId + "\",\"name\":\"contosotrackvice\",\"location\":\"swedencentral\",\"properties\":{\"allowBlobPublicAccess\":true,\"supportsHttpsTrafficOnly\":false,\"minimumTlsVersion\":\"TLS1_0\"}}");
            w.On("GET", AccountId + "/blobServices/default?api-version=", 200, "{\"properties\":{\"deleteRetentionPolicy\":{\"enabled\":true,\"days\":14},\"containerDeleteRetentionPolicy\":{\"enabled\":false}}}");
            w.On("PATCH", AccountId + "?api-version=", 200, "{}");
            w.On("PUT", AccountId + "/blobServices/default?api-version=", 200, "{}");
            return w;
        }

        static void Security()
        {
            FakeWeb w = SecurityWeb();
            StorageSecurity sec = new StorageSecurity(new Arm(w, delegate { return "arm"; }));
            AzureAccount a = Account();
            SecurityCheck c = sec.Read(a);
            Dictionary<string, SecurityItem> items = new Dictionary<string, SecurityItem>();
            foreach (SecurityItem i in c.Items(30)) items[i.Id] = i;
            T.Check(!items["public"].Ok && !items["https"].Ok && !items["tls"].Ok, "security: public access, http and TLS 1.0 are not in order");
            T.Check(items["blobsoft"].Ok && items["blobsoft"].Line.Contains("14 days"), "security: blob soft delete on");
            T.Check(!items["containersoft"].Ok && !items["rule"].Ok, "security: container soft delete off, no rule");
            T.Check(items["key"].Info && items["key"].Ok, "security: account key access is shown, on when not set");
            T.Eq("swedencentral", a.Location, "security: the region is read");
            int before = w.Calls.Count;
            T.Eq(0, w.All("PATCH", "").Count + w.All("PUT", "").Count, "security: reading changes nothing");

            string[] ids = { "public", "https", "tls", "blobsoft", "containersoft" };
            string[] bodies = {
                "{\"properties\":{\"allowBlobPublicAccess\":false}}",
                "{\"properties\":{\"supportsHttpsTrafficOnly\":true}}",
                "{\"properties\":{\"minimumTlsVersion\":\"TLS1_2\"}}",
                "{\"properties\":{\"deleteRetentionPolicy\":{\"enabled\":true,\"days\":7}}}",
                "{\"properties\":{\"containerDeleteRetentionPolicy\":{\"enabled\":true,\"days\":7}}}" };
            for (int i = 0; i < ids.Length; i++)
            {
                FakeWeb f = SecurityWeb();
                new StorageSecurity(new Arm(f, delegate { return "arm"; })).Fix(a, ids[i], 30);
                T.Eq(1, f.Calls.Count, "security fix " + ids[i] + ": one request");
                WebCall x = f.Calls[0];
                T.Eq(i < 3 ? "PATCH" : "PUT", x.Method, "security fix " + ids[i] + ": method");
                T.Check(x.Url.StartsWith(Arm.Base + AccountId + (i < 3 ? "?" : "/blobServices/default?")), "security fix " + ids[i] + ": the account itself");
                T.Eq(bodies[i], x.Text, "security fix " + ids[i] + ": only its own setting");
            }
            FakeWeb fr = SecurityWeb();
            new StorageSecurity(new Arm(fr, delegate { return "arm"; })).Fix(a, "rule", 30);
            T.Check(fr.All("PUT", "/managementPolicies/default").Count == 1 && fr.All("PATCH", "").Count == 0, "security fix rule: only the lifecycle policy");
            bool threw = false;
            try { new StorageSecurity(new Arm(SecurityWeb(), delegate { return "arm"; })).Fix(a, "key", 30); } catch (SetupException) { threw = true; }
            T.Check(threw, "security: the account key setting is never changed");
            T.Check(before > 0, "security: requests were made to read");
        }

        static void Preselect()
        {
            List<Customer> l = new List<Customer>();
            string[,] cs = {
                { "Contoso", "https://contosotrackvice.blob.core.windows.net/tv-contoso?si=viewer&sig=x" },
                { "Fabrikam", "https://contosotrackvice.blob.core.windows.net/tv-fabrikam?sp=rl&sig=x" },
                { "Northwind", "https://northwindagents.blob.core.windows.net/trackvice?sp=rl&sig=x" },
                { "Woodgrove", "\\\\FS01\\TrackVice$" } };
            for (int i = 0; i < cs.GetLength(0); i++) { Customer c = new Customer(); c.Name = cs[i, 0]; c.AgentLocation = cs[i, 1]; l.Add(c); }
            List<UsedAccount> used = StorageFinder.Used(l);
            T.Eq(2, used.Count, "preselect: two accounts in the links, folders left out");
            T.Eq("contosotrackvice", used[0].Name, "preselect: the account most customers use first");
            T.Eq("Used by your customers Contoso, Fabrikam.", StorageFinder.UsedLine(used[0].Customers), "preselect: the line");
            T.Eq("Used by your customer Northwind.", StorageFinder.UsedLine(used[1].Customers), "preselect: one customer");

            FakeWeb w = new FakeWeb();
            string other = "8b1d3f5a-7c9e-4a2b-9d4f-6e8a0c2b4d61";
            w.On("GET", "/subscriptions/" + other + "/providers/Microsoft.Storage/storageAccounts?", 200, "{\"value\":[{\"id\":\"/subscriptions/" + other + "/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/northwindagents\",\"name\":\"northwindagents\",\"location\":\"northeurope\"}]}");
            w.On("GET", "/subscriptions/" + Sub + "/providers/Microsoft.Storage/storageAccounts?", 200, "{\"value\":[{\"id\":\"/subscriptions/" + Sub + "/resourceGroups/rg-logs/providers/Microsoft.Storage/storageAccounts/contosologs\",\"name\":\"contosologs\"},{\"id\":\"" + AccountId + "\",\"name\":\"contosotrackvice\",\"location\":\"westeurope\"}]}");
            List<AzureSubscription> subs = new List<AzureSubscription>();
            foreach (string id in new string[] { other, Sub, "0f0f0f0f-0000-4000-8000-000000000000" }) { AzureSubscription s = new AzureSubscription(); s.Id = id; s.Name = id; subs.Add(s); }
            List<FoundAccount> found = StorageFinder.Find(new Arm(w, delegate { return "arm"; }), subs, used);
            T.Eq(2, found.Count, "preselect: both accounts found, a subscription that cannot be read is passed over");
            T.Eq("contosotrackvice", found[0].Account.Name, "preselect: the one most customers use is picked");
            T.Eq(Sub, found[0].Subscription.Id, "preselect: with its subscription");
            T.Eq(0, StorageFinder.Find(new Arm(w, delegate { return "arm"; }), subs, new List<UsedAccount>()).Count, "preselect: nothing to look for without links");
        }

        static void KeyNotKept()
        {
            FakeWeb w = new FakeWeb();
            w.On("PUT", "/blobServices/default/containers/trackvice-agent", 201, "{}");
            w.On("POST", "/listKeys", 200, "{\"keys\":[{\"keyName\":\"key1\",\"permissions\":\"FULL\",\"value\":\"" + Key + "\"}]}");
            w.On("GET", "/managementPolicies/default", 404, "{}");
            w.On("PUT", "/managementPolicies/default", 200, "{}");
            w.On("GET", "/trackvice-agent/agent/latest.json", 404, "");
            w.On("PUT", ".blob.core.windows.net/trackvice-agent/agent/", 201, "");
            Arm arm = new Arm(w, delegate { return "arm"; });
            OwnStorageRun run = new OwnStorageRun();
            AzureSubscription s = new AzureSubscription();
            s.Id = Sub;
            s.Name = "Contoso Production";
            s.Tenant = "contoso.onmicrosoft.com";
            run.Subscription = s;
            run.NewAccount = false;
            AzureAccount a = Account();
            a.BlobEndpoint = "https://contosotrackvice.blob.core.windows.net/";
            a.Location = "westeurope";
            run.Account = a;
            run.AskKey = true;
            MemorySecrets sec = new MemorySecrets();
            run.Run(arm, w, new MemoryKeyStore(), sec, Encoding.ASCII.GetBytes("MZ made-up"), "1.3.0", Now, delegate { });
            T.Check(run.PublishedNow, "no key: the first version is still published");
            T.Eq(1, w.All("PUT", "/managementPolicies/default").Count, "setup: the lifecycle rule is set");
            string text = sec.Values[OwnStorage.SecretName];
            T.Check(!text.Contains(Key) && !text.Contains(Uri.EscapeDataString(Key)), "no key: the account key is not kept");
            OwnStorage kept = OwnStorage.Load(sec);
            T.Check(kept != null && !kept.KeyKept && kept.AskKey && kept.AgentWrite.Length == 0, "no key: no key and no publish link, the setting kept");
            T.Check(kept.AgentRead.Length > 0, "no key: the read link is kept");
            T.Eq("westeurope", kept.Region, "setup: the region is kept");
            T.Eq(30, kept.RuleDays, "setup: the rule state is kept");
            T.Eq(AccountId, kept.Id, "setup: the account ID is kept");

            OwnStorage withKey = StorageFinder.WithKey(arm, kept, Now);
            T.Eq(Key, withKey.Key, "no key: the key is read for the run");
            T.Check(!kept.KeyKept, "no key: the kept copy still has no key");
            T.Check(QueryOf(withKey.AgentWrite)["se"] == SasLinks.Time(Now.AddHours(2)), "no key: the publish link of the run is short");
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            FakeWeb blob = new FakeWeb();
            blob.On("PUT", "restype=container", 201, "");
            r.MakeStorage(blob, withKey, Now);
            kept.Save(sec);
            T.Check(!sec.Values[OwnStorage.SecretName].Contains(Key), "no key: still not kept after Add customer");
            OwnStorage plain = Own();
            T.Check(StorageFinder.WithKey(arm, plain, Now) == plain, "kept key: used as it is");
        }

        static void OwnAzureContainers()
        {
            FakeWeb w = new FakeWeb();
            w.On("PUT", "/blobServices/default/containers/", 201, "{}");
            w.On("GET", "/managementPolicies/default", 404, "{}");
            w.On("PUT", "/managementPolicies/default", 200, "{}");
            w.On("POST", "/listKeys", 200, "{\"keys\":[{\"keyName\":\"key1\",\"permissions\":\"FULL\",\"value\":\"" + Key + "\"}]}");
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Fabrikam";
            r.OwnAzure = true;
            NewCustomerRun st = new NewCustomerRun();
            AzureSubscription s = new AzureSubscription();
            s.Id = Sub;
            s.Name = "Fabrikam";
            st.Subscription = s;
            st.NewAccount = false;
            AzureAccount a = new AzureAccount();
            a.Id = "/subscriptions/" + Sub + "/resourceGroups/rg-a/providers/Microsoft.Storage/storageAccounts/fabrikamtrackvice";
            a.Name = "fabrikamtrackvice";
            a.BlobEndpoint = "https://fabrikamtrackvice.blob.core.windows.net/";
            st.Account = a;
            r.Storage = st;
            r.SetUpOwnAzure(new Arm(w, delegate { return "arm"; }), Now, delegate { });
            T.Eq(1, w.All("PUT", "/containers/trackvice?").Count, "own azure: the container trackvice is made");
            T.Eq(0, w.All("PUT", "/containers/trackvice-agent").Count, "own azure: no unused trackvice-agent container");
            T.Check(r.Upload.StartsWith("https://fabrikamtrackvice.blob.core.windows.net/trackvice?"), "own azure: the links are to the customer's storage");
            T.Eq(730, (int)Math.Round((r.LinksExpireUtc - Now).TotalDays), "own azure: links valid 2 years");
        }
    }
}
