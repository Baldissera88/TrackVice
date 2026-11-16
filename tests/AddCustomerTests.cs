namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    public static class AddCustomerTests
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

            public WebCall Last(string method, string urlPart)
            {
                for (int i = Calls.Count - 1; i >= 0; i--) if (Calls[i].Method == method && Calls[i].Url.Contains(urlPart)) return Calls[i];
                return null;
            }

            public int Count(string method, string urlPart)
            {
                int n = 0;
                foreach (WebCall c in Calls) if (c.Method == method && c.Url.Contains(urlPart)) n++;
                return n;
            }
        }

        // A made-up 64-byte key, the bytes 0 to 63.
        const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw==";
        const string Sub = "0b1c2d3e-4f50-4a6b-8c7d-9e0f1a2b3c4d";
        static readonly DateTime Now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        static string publicKey;
        static RSACryptoServiceProvider rsa;

        public static void Run()
        {
            rsa = UpdateKeys.Create();
            publicKey = UpdateKeys.PublicKey(rsa);
            Names();
            Sas();
            Kept();
            Prompt();
            NothingBeforeCreate();
            DefaultPath();
            ExistingGroupAndScript();
            Declined();
            OwnAzure();
            SetUpYourStorage();
            Change();
            rsa.Dispose();
        }

        static int Exact(FakeWeb w, string method, string url)
        {
            int n = 0;
            foreach (WebCall c in w.Calls) if (c.Method == method && c.Url == url) n++;
            return n;
        }

        static OwnStorage Own()
        {
            AzureAccount a = new AzureAccount();
            a.Name = "contosotrackvice";
            a.BlobEndpoint = "https://contosotrackvice.blob.core.windows.net/";
            OwnStorage o = OwnStorage.Make(a, Key, "Contoso Production", Now);
            o.PublishedVersion = "1.1.3";
            return o;
        }

        static Dictionary<string, string> QueryOf(string link)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (string p in link.Substring(link.IndexOf('?') + 1).Split('&'))
            {
                int e = p.IndexOf('=');
                d[p.Substring(0, e)] = Uri.UnescapeDataString(p.Substring(e + 1));
            }
            return d;
        }

        static void Names()
        {
            T.Eq("tv-contoso", BlobContainers.NameFor("Contoso"), "container: simple name");
            T.Eq("tv-northwind-traders", BlobContainers.NameFor("  Northwind  Traders "), "container: spaces become one hyphen");
            T.Eq("tv-solrod-kobenhavn", BlobContainers.NameFor("Sølrød København"), "container: letters with marks are kept as plain letters");
            T.Eq("tv-a-b", BlobContainers.NameFor("A & B!"), "container: no hyphen at the end");
            T.Eq("tv-customer", BlobContainers.NameFor("!!!"), "container: a name without letters");
            string longName = BlobContainers.NameFor(new string('x', 100));
            T.Check(longName.Length == 63 && BlobContainers.Problem(longName) == null, "container: at most 63 characters");
            T.Check(BlobContainers.Problem("tv--x") != null, "container: double hyphen is refused");
            T.Check(BlobContainers.Problem("Tv-x") != null, "container: capitals are refused");
        }

        static void Sas()
        {
            string s = AccountSas.StringToSign("contosotrackvice", "c", "b", "c", "2026-11-16T08:45:00Z", "2026-11-16T10:00:00Z", "2022-11-02");
            T.Eq("contosotrackvice\nc\nb\nc\n2026-11-16T08:45:00Z\n2026-11-16T10:00:00Z\n\nhttps\n2022-11-02\n\n", s, "account sas: the string to sign in the documented order");
            string q = AccountSas.Query("contosotrackvice", Key, "c", "b", "c", Now.AddMinutes(-15), Now.AddHours(1));
            Dictionary<string, string> d = QueryOf("x?" + q);
            T.Eq("b", d["ss"], "account sas: blob service");
            T.Eq("c", d["srt"], "account sas: container resource type");
            T.Eq("c", d["sp"], "account sas: create only");
            T.Eq("https", d["spr"], "account sas: https only");
            string sig;
            using (HMACSHA256 h = new HMACSHA256(Convert.FromBase64String(Key))) sig = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(s)));
            T.Eq(sig, d["sig"], "account sas: signed with the account key");
        }

        static void Kept()
        {
            MemorySecrets s = new MemorySecrets();
            T.Check(OwnStorage.Load(s) == null, "your storage: nothing kept at first");
            OwnStorage o = Own();
            o.Save(s);
            OwnStorage b = OwnStorage.Load(s);
            T.Eq("contosotrackvice", b.Account, "your storage: account kept");
            T.Eq(Key, b.Key, "your storage: key kept");
            T.Eq(o.AgentRead, b.AgentRead, "your storage: read link kept");
            T.Eq(o.AgentWrite, b.AgentWrite, "your storage: write link kept");
            T.Eq("1.1.3", b.PublishedVersion, "your storage: published version kept");
            T.Eq(Now.AddDays(1826).ToString("yyyy-MM-dd"), b.ExpiresUtc.ToString("yyyy-MM-dd"), "your storage: links valid 5 years");
            T.Eq("r", QueryOf(o.AgentRead)["sp"], "your storage: read link has Read only");
            T.Eq("cw", QueryOf(o.AgentWrite)["sp"], "your storage: write link has Create and Write");
            T.Check(o.AgentRead.StartsWith("https://contosotrackvice.blob.core.windows.net/trackvice-agent?"), "your storage: links to trackvice-agent");
            OwnStorage.Forget(s);
            T.Check(OwnStorage.Load(s) == null, "your storage: Remove forgets it");
        }

        static void Prompt()
        {
            OwnStorage o = Own();
            T.Check(OwnPublish.Needed(o, "1.1.2", "1.1.3"), "publish prompt: published version is lower");
            T.Check(!OwnPublish.Needed(o, "1.1.3", "1.1.3"), "publish prompt: not for the same version");
            T.Check(!OwnPublish.Needed(o, "1.2.0", "1.1.3"), "publish prompt: not when a newer version is published");
            T.Check(OwnPublish.Needed(o, null, "1.1.3"), "publish prompt: nothing published yet");
            T.Check(!OwnPublish.Needed(null, "1.1.2", "1.1.3"), "publish prompt: not without your storage");
            T.Check(!OwnPublish.Needed(o, "1.1.3-beta1", "1.1.3-beta1"), "publish prompt: same pre-release");
            T.Eq("TrackVice 1.1.4 is not published to your storage.", OwnPublish.Prompt("1.1.4"), "publish prompt: text");
        }

        static FakeWeb Intune(bool groupThere, bool scriptThere)
        {
            FakeWeb w = new FakeWeb();
            w.On("PUT", "/tv-contoso?restype=container", 201, "");
            w.On("GET", IntuneSetup.GroupsUrl + "?$filter=", 200, groupThere
                ? "{\"value\":[{\"id\":\"a1b2c3d4-0000-4000-8000-000000000001\",\"displayName\":\"TrackVice - Autopilot devices\"}]}"
                : "{\"value\":[]}");
            w.On("POST", IntuneSetup.GroupsUrl, 201, "{\"id\":\"a1b2c3d4-0000-4000-8000-000000000002\"}");
            w.On("GET", IntuneScripts.ScriptsUrl + "?$select", 200, scriptThere
                ? "{\"value\":[{\"id\":\"other\",\"displayName\":\"Something else\"},{\"id\":\"b1b2c3d4-0000-4000-8000-000000000003\",\"displayName\":\"TrackVice agent\"}]}"
                : "{\"value\":[]}");
            w.On("GET", "/assignments", 200, "{\"value\":[{\"target\":{\"groupId\":\"c1b2c3d4-0000-4000-8000-000000000004\"}}]}");
            w.On("PATCH", IntuneScripts.ScriptsUrl + "/", 204, "");
            w.On("POST", "/assign", 204, "");
            w.On("POST", IntuneScripts.ScriptsUrl, 201, "{\"id\":\"b1b2c3d4-0000-4000-8000-000000000005\"}");
            return w;
        }

        static AddCustomerRun NewRun()
        {
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Contoso";
            r.Tenant = "contoso.onmicrosoft.com";
            r.Alert = "https://contoso.example/workflows/alerts";
            return r;
        }

        static void NothingBeforeCreate()
        {
            CustomerBook book = CustomerBook.Demo();
            AddCustomerRun r = NewRun();
            r.Customer = "Northwind";
            T.Eq(null, r.Problem(book), "add customer: a new name is fine");
            r.Alert = "http://example.com/x";
            T.Check(r.Problem(book) != null, "add customer: an alert link that is not https is refused");
            r.Alert = "";
            r.Customer = "";
            T.Check(r.Problem(book) != null, "add customer: a name is needed");
            r.Customer = "Contoso";
            T.Eq(null, r.Problem(book), "add customer: an existing customer is updated");
            List<string> plan = r.Plan(Own(), true);
            T.Check(string.Join("\n", plan.ToArray()).Contains("tv-contoso") && string.Join("\n", plan.ToArray()).Contains("TrackVice - Autopilot devices"), "add customer: the plan names the container and the group");
            r.MakeGroup = false;
            T.Check(string.Join("\n", r.Plan(Own(), true).ToArray()).Contains("not assigned"), "add customer: the plan says the script is not assigned without the group");
            T.Check(string.Join("\n", r.Plan(Own(), false).ToArray()).Contains("Nothing in Intune"), "add customer: the plan without a sign-in");
        }

        static void DefaultPath()
        {
            FakeWeb w = Intune(false, false);
            OwnStorage own = Own();
            AddCustomerRun r = NewRun();
            T.Eq(0, w.Calls.Count, "add customer: nothing is sent before Create");
            r.MakeStorage(w, own, Now);
            WebCall put = w.Last("PUT", "?restype=container&sv=");
            T.Check(put != null && put.Url.StartsWith("https://contosotrackvice.blob.core.windows.net/tv-contoso?restype=container&sv="), "add customer: the container is made with the Blob service");
            T.Eq(SasLinks.Version, put.Headers["x-ms-version"], "add customer: x-ms-version");
            T.Eq("c", QueryOf(put.Url)["srt"], "add customer: with an account SAS");
            T.Eq("w", QueryOf(put.Url)["sp"], "add customer: the account SAS can only write containers");
            T.Eq("upload", QueryOf(r.Upload)["si"], "add customer: upload link uses the upload policy");
            T.Eq("c", QueryOf(r.Upload)["sr"], "add customer: upload link is for the container");
            T.Eq("viewer", QueryOf(r.Viewer)["si"], "add customer: viewer link uses the viewer policy");
            WebCall acl = w.Last("PUT", "comp=acl");
            T.Check(acl != null && acl.Text.Contains("<Id>upload</Id>") && acl.Text.Contains("<Permission>cw</Permission>") && acl.Text.Contains("<Permission>rl</Permission>"), "add customer: the policies are set on the container");
            T.Check(r.Upload.StartsWith("https://contosotrackvice.blob.core.windows.net/tv-contoso?"), "add customer: links to the customer's container");
            T.Eq(Now.AddDays(730).ToString("yyyy-MM-dd"), r.LinksExpireUtc.ToString("yyyy-MM-dd"), "add customer: links valid 2 years");

            r.MakeCode(own, publicKey);
            CustomerCode c = CustomerCode.Parse(r.Code.ToString());
            T.Eq("Contoso", c.Name, "add customer: code name");
            T.Eq(r.Upload, c.Upload, "add customer: code upload is the upload link");
            T.Eq(own.AgentRead, c.Agent, "add customer: code agent is the kept read link");
            T.Eq(publicKey, c.Key, "add customer: code key is the public update key");
            T.Eq("https://contoso.example/workflows/alerts", c.Alert, "add customer: code alert");
            T.Eq(24, c.MaxAgeHours, "add customer: code default max age");
            T.Eq(CustomerScript.For(r.Code.ToString()), r.Script, "add customer: the script is the one for the code");

            int before = w.Calls.Count;
            bool asked = false;
            r.ToIntune(new IntuneSetup(w, delegate { return "token"; }), delegate { asked = true; return true; });
            T.Check(!asked, "add customer: no question when there is no script");
            WebCall g = w.Last("POST", IntuneSetup.GroupsUrl);
            T.Check(g != null && g.Url == IntuneSetup.GroupsUrl, "add customer: the group is made");
            T.Check(g.Text.Contains("\"groupTypes\":[\"DynamicMembership\"]") && g.Text.Contains("device.devicePhysicalIds -any (_ -contains \\\"[ZTDId]\\\")") && g.Text.Contains("\"membershipRuleProcessingState\":\"On\""), "add customer: a dynamic group with the Autopilot rule");
            T.Check(g.Text.Contains("\"displayName\":\"TrackVice - Autopilot devices\""), "add customer: group name");
            WebCall s = null;
            foreach (WebCall x in w.Calls) if (x.Method == "POST" && x.Url == IntuneScripts.ScriptsUrl) s = x;
            T.Check(s != null, "add customer: the script is added");
            T.Check(s.Text.Contains("\"runAsAccount\":\"system\"") && s.Text.Contains("\"enforceSignatureCheck\":false") && s.Text.Contains("\"runAs32Bit\":false") && s.Text.Contains("\"displayName\":\"TrackVice agent\""), "add customer: system context, no signature check, 64-bit");
            T.Check(s.Text.Contains(Convert.ToBase64String(new UTF8Encoding(false).GetBytes(r.Script))), "add customer: the script content");
            WebCall a = w.Last("POST", "/assign");
            T.Check(a != null && a.Url.Contains("b1b2c3d4-0000-4000-8000-000000000005") && a.Text.Contains("a1b2c3d4-0000-4000-8000-000000000002"), "add customer: assigned to the new group");
            T.Eq(0, w.Count("GET", "/assignments"), "add customer: a new script has no assignments to read");
            T.Eq(0, w.Count("PATCH", ""), "add customer: nothing replaced");
            T.Check(r.IntuneDone && !r.GroupReused, "add customer: Intune done");
            T.Check(w.Calls.Count > before, "add customer: Intune calls made");
            foreach (WebCall x in w.Calls)
                if (x.Url.StartsWith("https://graph.microsoft.com")) T.Eq("Bearer token", x.Headers["Authorization"], "add customer: graph calls carry the token");

            CustomerBook book = CustomerBook.Demo();
            Customer saved = r.SaveTo(book);
            T.Eq(r.Viewer, saved.AgentLocation, "add customer: the viewer link is saved in the customer");
            T.Eq("CTO", saved.SccmSite, "add customer: other details of an existing customer are kept");
            r.Customer = "Northwind";
            T.Eq("Northwind", r.SaveTo(book).Name, "add customer: a new customer is added");
            r.Customer = "Contoso";

            MemorySecrets sec = new MemorySecrets();
            CustomerCodes.Save(sec, "Contoso", r.Code.ToString());
            T.Eq(r.Code.ToString(), CustomerCodes.Load(sec, "Contoso"), "add customer: the code is kept");
            T.Check(!CustomerCodes.Masked(r.Code.ToString()).Contains(r.Code.ToString().Substring(12, 20)), "add customer: the code is shown masked");

            string dir = Path.Combine(Path.GetTempPath(), "tv-addcustomer-" + Guid.NewGuid().ToString("N"));
            try
            {
                string p = r.SaveScript(dir);
                T.Eq("Install-TrackViceAgent-Contoso.ps1", Path.GetFileName(p), "add customer: script file name");
                T.Eq(r.Script, File.ReadAllText(p), "add customer: script file content");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        static void ExistingGroupAndScript()
        {
            FakeWeb w = Intune(true, true);
            AddCustomerRun r = NewRun();
            r.MakeStorage(w, Own(), Now);
            r.MakeCode(Own(), publicKey);
            bool asked = false;
            r.ToIntune(new IntuneSetup(w, delegate { return "token"; }), delegate { asked = true; return true; });
            T.Check(asked, "existing: asked before replacing the script");
            T.Eq(0, w.Count("POST", IntuneSetup.GroupsUrl), "existing: no new group");
            T.Check(r.GroupReused && r.Group.Id == "a1b2c3d4-0000-4000-8000-000000000001", "existing: the group with that name is used");
            T.Check(w.Last("GET", IntuneSetup.GroupsUrl).Url.Contains(Uri.EscapeDataString("displayName eq 'TrackVice - Autopilot devices'")), "existing: the group is found by its name");
            T.Eq(0, Exact(w, "POST", IntuneScripts.ScriptsUrl), "existing: no new script");
            WebCall p = w.Last("PATCH", IntuneScripts.ScriptsUrl);
            T.Check(p != null && p.Url.EndsWith("/b1b2c3d4-0000-4000-8000-000000000003"), "existing: the content of the script is replaced");
            T.Check(p.Text.Contains(Convert.ToBase64String(new UTF8Encoding(false).GetBytes(r.Script))), "existing: with the new content");
            WebCall a = w.Last("POST", "/assign");
            T.Check(a.Text.Contains("c1b2c3d4-0000-4000-8000-000000000004") && a.Text.Contains("a1b2c3d4-0000-4000-8000-000000000001"), "existing: the assignments it had are kept");

            FakeWeb w2 = Intune(true, true);
            w2.Handlers.Insert(0, delegate (WebCall c) { return c.Url.EndsWith("/assignments") ? new WebReply(200, "{\"value\":[{\"target\":{\"groupId\":\"a1b2c3d4-0000-4000-8000-000000000001\"}}]}") : null; });
            AddCustomerRun r2 = NewRun();
            r2.MakeStorage(w2, Own(), Now);
            r2.MakeCode(Own(), publicKey);
            r2.ToIntune(new IntuneSetup(w2, delegate { return "token"; }), delegate { return true; });
            T.Eq(0, w2.Count("POST", "/assign"), "existing: no assign when the group is assigned already");

            FakeWeb w3 = Intune(false, false);
            w3.Handlers.Insert(0, delegate (WebCall c) { return c.Url.Contains("restype=container") && !c.Url.Contains("comp=acl") ? new WebReply(409, "") : null; });
            w3.Handlers.Insert(0, delegate (WebCall c) { return c.Url.Contains("comp=acl") ? new WebReply(200, "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers />") : null; });
            AddCustomerRun r3 = NewRun();
            r3.MakeStorage(w3, Own(), Now);
            T.Check(r3.Done[0].Contains("there already"), "existing: a container that is there is used");
            FakeWeb w4 = new FakeWeb();
            w4.Handlers.Add(delegate (WebCall c) { WebReply x = new WebReply(403, ""); x.Headers["x-ms-error-code"] = "AuthenticationFailed"; return x; });
            bool threw = false;
            try { NewRun().MakeStorage(w4, Own(), Now); } catch (SetupException ex) { threw = ex.Message.Contains("AuthenticationFailed"); }
            T.Check(threw, "existing: a refused key says so");
        }

        static void Declined()
        {
            FakeWeb w = Intune(false, true);
            AddCustomerRun r = NewRun();
            r.AddScript = true;
            r.MakeStorage(w, Own(), Now);
            r.MakeCode(Own(), publicKey);
            r.ToIntune(new IntuneSetup(w, delegate { return "token"; }), delegate { return false; });
            T.Eq(0, w.Count("PATCH", ""), "declined: the content is kept");
            T.Check(w.Last("POST", "/assign") != null, "declined: still assigned to the group");

            FakeWeb w2 = Intune(false, false);
            AddCustomerRun r2 = NewRun();
            r2.MakeGroup = false;
            r2.AddScript = false;
            r2.ToIntune(new IntuneSetup(w2, delegate { return "token"; }), delegate { return true; });
            T.Eq(0, w2.Calls.Count, "declined: both options off sends nothing to Intune");
        }

        static void OwnAzure()
        {
            AddCustomerRun r = NewRun();
            r.OwnAzure = true;
            AgentLinks l = new AgentLinks();
            l.Upload = "https://fabrikamtrackvice.blob.core.windows.net/trackvice?sp=cw&sig=x";
            l.Viewer = "https://fabrikamtrackvice.blob.core.windows.net/trackvice?sp=rl&sig=y";
            l.ExpiresUtc = Now.AddDays(730);
            r.UseLinks(l);
            r.MakeCode(Own(), publicKey);
            CustomerCode c = CustomerCode.Parse(r.Code.ToString());
            T.Eq(l.Upload, c.Upload, "own azure: upload goes to the customer's storage");
            T.Eq(Own().AgentRead, c.Agent, "own azure: devices still download from your storage");
            T.Check(string.Join("\n", r.Plan(Own(), true).ToArray()).Contains("customer's Azure"), "own azure: the plan says where");
        }

        static void SetUpYourStorage()
        {
            FakeWeb w = new FakeWeb();
            w.On("PUT", "/blobServices/default/containers/trackvice-agent", 201, "{}");
            w.On("POST", "/listKeys", 200, "{\"keys\":[{\"keyName\":\"key1\",\"permissions\":\"FULL\",\"value\":\"" + Key + "\"}]}");
            w.On("GET", "/trackvice-agent/agent/latest.json", 404, "");
            w.On("PUT", ".blob.core.windows.net/trackvice-agent/agent/", 201, "");
            Arm arm = new Arm(w, delegate { return "arm"; });
            arm.Pause = delegate { };
            OwnStorageRun run = new OwnStorageRun();
            AzureSubscription s = new AzureSubscription();
            s.Id = Sub;
            s.Name = "Contoso Production";
            run.Subscription = s;
            run.NewAccount = false;
            AzureAccount a = new AzureAccount();
            a.Id = Arm.AccountId(Sub, "rg-trackvice", "contosotrackvice");
            a.Name = "contosotrackvice";
            a.BlobEndpoint = "https://contosotrackvice.blob.core.windows.net/";
            run.Account = a;
            MemoryKeyStore keys = new MemoryKeyStore();
            MemorySecrets sec = new MemorySecrets();
            byte[] exe = Encoding.ASCII.GetBytes("MZ made-up TrackVice for the test");
            run.Run(arm, w, keys, sec, exe, "1.1.3", Now, delegate { });
            T.Check(run.KeyCreated && keys.Blob != null, "your storage: an update key is made when there is none");
            T.Check(run.PublishedNow, "your storage: the running version is published when nothing is");
            T.Check(w.Last("PUT", "/agent/TrackVice-1.1.3.exe") != null, "your storage: the exe is uploaded");
            WebCall m = w.Last("PUT", "/agent/latest.json");
            T.Check(m != null && m.Url.Contains("sp=cw"), "your storage: latest.json is written with the write link");
            UpdateManifest man = UpdateManifest.Parse(m.Body);
            using (RSACryptoServiceProvider k = UpdateKeys.Load(keys)) T.Check(man.Verify(UpdateKeys.PublicKey(k)), "your storage: latest.json is signed with the update key");
            OwnStorage kept = OwnStorage.Load(sec);
            T.Check(kept != null && kept.Key == Key && kept.PublishedVersion == "1.1.3", "your storage: kept with the key and the version");

            FakeWeb w2 = new FakeWeb();
            w2.On("PUT", "/blobServices/default/containers/trackvice-agent", 409, "{}");
            w2.On("POST", "/listKeys", 200, "{\"keys\":[{\"keyName\":\"key1\",\"permissions\":\"FULL\",\"value\":\"" + Key + "\"}]}");
            w2.On("GET", "/trackvice-agent/agent/latest.json", 200, Encoding.UTF8.GetString(m.Body));
            OwnStorageRun run2 = new OwnStorageRun();
            run2.Subscription = s;
            run2.NewAccount = false;
            run2.Account = a;
            Arm arm2 = new Arm(w2, delegate { return "arm"; });
            run2.Run(arm2, w2, keys, new MemorySecrets(), exe, "1.1.4", Now, delegate { });
            T.Check(!run2.KeyCreated && !run2.PublishedNow, "your storage: the key that is there is used, and nothing is published over a version");
            T.Eq(0, w2.Count("PUT", ".blob.core.windows.net/"), "your storage: no upload when a version is published");
            T.Eq("1.1.3", run2.Result.PublishedVersion, "your storage: the published version is read");

            OwnStorage own = Own();
            FakeWeb w3 = new FakeWeb();
            w3.On("PUT", "/agent/", 201, "");
            OwnPublish.Publish(w3, own, exe, "1.1.4", rsa, Now);
            T.Eq("1.1.4", own.PublishedVersion, "publish: the kept version follows");
            T.Eq(2, w3.Count("PUT", "/trackvice-agent/agent/"), "publish: exe and latest.json");
        }

        static void Change()
        {
            AddCustomerRun r = NewRun();
            r.MakeStorage(Intune(false, false), Own(), Now);
            r.MakeCode(Own(), publicKey);
            CustomerCode c = CustomerCodeChange.WithAlert(r.Code.ToString(), "https://contoso.example/new");
            T.Eq("https://contoso.example/new", c.Alert, "change: new alert link");
            T.Eq(r.Upload, c.Upload, "change: the rest is kept");
            T.Eq(null, CustomerCodeChange.WithAlert(r.Code.ToString(), "").Alert, "change: alert removed");
            bool threw = false;
            try { CustomerCodeChange.WithAlert(r.Code.ToString(), "ftp://x"); } catch (SetupException) { threw = true; }
            T.Check(threw, "change: an alert that is not https is refused");

            FakeWeb w = Intune(false, true);
            string id = CustomerCodeChange.UpdateIntune(new IntuneSetup(w, delegate { return "token"; }), "Contoso", c.ToString());
            T.Eq("b1b2c3d4-0000-4000-8000-000000000003", id, "change: the script is found by name");
            T.Check(w.Last("PATCH", "").Text.Contains(Convert.ToBase64String(new UTF8Encoding(false).GetBytes(CustomerScript.For(c.ToString())))), "change: the new content is uploaded");
            threw = false;
            try { CustomerCodeChange.UpdateIntune(new IntuneSetup(Intune(false, false), delegate { return "token"; }), "Contoso", c.ToString()); } catch (SetupException) { threw = true; }
            T.Check(threw, "change: no script in the tenant says so");
        }
    }
}
