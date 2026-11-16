namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;

    public static class CustomerSetupTests
    {
        // Answers requests from a list of handlers; the first one that returns a reply wins. Records every call.
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

        public static void Run()
        {
            Signing();
            Links();
            ScriptFill();
            TemplateMatchesFile();
            SignIn();
            Storage();
            StorageErrors();
            ExistingAccount();
            Upload();
            Intune();
            Demo();
            Json();
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

        // Reference signatures made with the Azure Storage SDK for Python (BlobSharedAccessSignature, version 2022-11-02)
        // from the same made-up key and values.
        static void Signing()
        {
            DateTime st = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), se = new DateTime(2027, 11, 15, 0, 0, 0, DateTimeKind.Utc);
            string ep = "https://contosoagents.blob.core.windows.net/";
            string cw = SasLinks.Container(ep, "contosoagents", "trackvice", "cw", st, se, Key);
            string rl = SasLinks.Container(ep, "contosoagents", "trackvice", "rl", st, se, Key);
            string r = SasLinks.Blob(ep, "contosoagents", "trackvice-agent", "TrackVice.exe", "r", st, se, Key);
            T.Eq("C2PzfTLnXRCBrnUVPNnYHzME62CPwChHXRqRW6R4C48=", QueryOf(cw)["sig"], "SAS: container Create and Write signature");
            T.Eq("NpYDKxqApD5CLSJt71xZH8W36PFKfqECO9ndoKHQ6RA=", QueryOf(rl)["sig"], "SAS: container Read and List signature");
            T.Eq("CoY37u2pnkz81ql9rOLeHpmywidN/DRuIpHop+p+H7A=", QueryOf(r)["sig"], "SAS: blob Read signature");
            T.Check(cw.StartsWith("https://contosoagents.blob.core.windows.net/trackvice?"), "SAS: container link points at the container");
            T.Check(r.StartsWith("https://contosoagents.blob.core.windows.net/trackvice-agent/TrackVice.exe?"), "SAS: blob link points at the blob");
            Dictionary<string, string> q = QueryOf(cw);
            T.Eq("cw", q["sp"], "SAS: permissions");
            T.Eq("2026-11-15T00:00:00Z", q["st"], "SAS: start");
            T.Eq("2027-11-15T00:00:00Z", q["se"], "SAS: expiry");
            T.Eq("https", q["spr"], "SAS: HTTPS only");
            T.Eq("c", q["sr"], "SAS: container resource");
            T.Eq("b", QueryOf(r)["sr"], "SAS: blob resource");
            T.Eq(16, SasLinks.StringToSign("r", "a", "b", "/blob/x/y", "https", SasLinks.Version, "c").Split('\n').Length, "SAS: string-to-sign has 16 fields");
            string masked = SasLinks.Masked(cw);
            T.Check(masked.EndsWith("&sig=***") && masked.IndexOf("C2Pz", StringComparison.Ordinal) < 0, "SAS: masked link hides the signature");
            // The links work with the stores the agent and the viewer use.
            AzureStore s = new AzureStore(cw);
            T.Eq("https://contosoagents.blob.core.windows.net/trackvice", s.Title, "SAS: the upload link opens as a store");
        }

        static AzureAccount Account()
        {
            AzureAccount a = new AzureAccount();
            a.Name = "contosotrackvice";
            a.Id = Arm.AccountId(Sub, "rg-trackvice", a.Name);
            a.ResourceGroup = "rg-trackvice";
            a.BlobEndpoint = "https://contosotrackvice.blob.core.windows.net/";
            return a;
        }

        static void Links()
        {
            DateTime now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
            AgentLinks l = AgentLinks.Make(Account(), Key, now, 365);
            T.Eq("cw", QueryOf(l.Upload)["sp"], "Links: upload has Create and Write");
            T.Eq("rl", QueryOf(l.Viewer)["sp"], "Links: viewer has Read and List");
            T.Eq("r", QueryOf(l.Download)["sp"], "Links: download has Read");
            T.Eq("r", QueryOf(l.UpdateRead)["sp"], "Links: update link has Read");
            T.Check(l.UpdateRead.StartsWith("https://contosotrackvice.blob.core.windows.net/trackvice-agent?"), "Links: update link is on trackvice-agent");
            T.Check(l.Publish.StartsWith("https://contosotrackvice.blob.core.windows.net/trackvice-agent?") && QueryOf(l.Publish)["sp"] == "cw", "Links: publish link writes to trackvice-agent");
            T.Check(l.Download.StartsWith("https://contosotrackvice.blob.core.windows.net/trackvice-agent/TrackVice.exe?"), "Links: download is the exe");
            T.Eq("2026-11-15T11:45:00Z", QueryOf(l.Upload)["st"], "Links: start is 15 minutes back");
            T.Eq("2027-11-15T12:00:00Z", QueryOf(l.Viewer)["se"], "Links: valid for the days given");
            T.Eq(now.AddDays(365), l.ExpiresUtc, "Links: expiry kept");
            foreach (string s in new string[] { l.Upload, l.Viewer, l.Download, l.UpdateRead, l.Publish })
                T.Check(s.IndexOf(Key, StringComparison.Ordinal) < 0 && s.IndexOf(Uri.EscapeDataString(Key), StringComparison.Ordinal) < 0, "Links: the key is not in a link");
        }

        static void ScriptFill()
        {
            ScriptValues v = new ScriptValues();
            v.Download = "https://contosotrackvice.blob.core.windows.net/trackvice-agent/TrackVice.exe?sp=r&sig=abc";
            v.Sha256 = "0123ABCD";
            v.Upload = "https://contosotrackvice.blob.core.windows.net/trackvice?sp=cw&sig=def";
            v.Alert = "https://contoso.example/alert?it's";
            v.MaxAgeHours = 24;
            string s = AgentScript.Fill(AgentScriptTemplate.Links, v);
            T.Check(s.Contains("$Download  = '" + v.Download + "'   # Link to TrackVice.exe"), "Script: download filled and comment kept");
            T.Check(s.Contains("$Sha256    = '0123ABCD'"), "Script: hash filled");
            T.Check(s.Contains("$Upload    = '" + v.Upload + "'"), "Script: upload filled");
            T.Check(s.Contains("$MaxAgeHours = 24  # Only install"), "Script: max age filled");
            T.Check(s.Contains("$KeepHours = 48"), "Script: keep hours stays 48");
            T.Check(s.Contains("$Alert     = 'https://contoso.example/alert?it''s'"), "Script: a quote in a value is doubled");
            T.Check(s.Contains("$UpdateKey = ''") && s.Contains("$UpdateLink = ''"), "Script: update values stay empty without a key");
            T.Check(s.Contains("if (-not $Download -or -not $Sha256 -or -not $Upload)"), "Script: the rest is unchanged");
            T.Eq(AgentScriptTemplate.Links.Split('\n').Length, s.Split('\n').Length, "Script: same number of lines");
            bool threw = false;
            try { AgentScript.Set("$Other = ''", "Download", "'x'"); }
            catch (SetupException) { threw = true; }
            T.Check(threw, "Script: a template without the line is refused");
            T.Eq("Install-TrackViceAgent-ContosoLtd.ps1", AgentScript.FileName("Contoso Ltd."), "Script: file name from the customer");
        }

        // The copy inside the exe must be the script in agent/.
        static void TemplateMatchesFile()
        {
            string path = Path.Combine("agent", "Install-TrackViceAgent.ps1");
            if (!File.Exists(path)) { T.Check(false, "Template: agent/Install-TrackViceAgent.ps1 not found"); return; }
            string file = File.ReadAllText(path).Replace("\r\n", "\n");
            T.Check(file == AgentScriptTemplate.Text.Replace("\r\n", "\n"), "Template: AgentScriptTemplate.Text is the same as agent/Install-TrackViceAgent.ps1");
        }

        static string Jwt(string upn)
        {
            return "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"upn\":\"" + upn + "\"}")).TrimEnd('=') + ".y";
        }

        static void SignIn()
        {
            FakeWeb w = new FakeWeb();
            w.On("POST", "/devicecode", 200, "{\"device_code\":\"dc1\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"interval\":0,\"expires_in\":60}");
            int polls = 0;
            w.Handlers.Add(delegate (WebCall c)
            {
                if (!c.Url.EndsWith("/token")) return null;
                polls++;
                return polls < 2 ? new WebReply(400, "{\"error\":\"authorization_pending\"}") : new WebReply(200, "{\"access_token\":\"" + Jwt("admin@contoso.onmicrosoft.com") + "\",\"refresh_token\":\"rt1\",\"expires_in\":3600}");
            });
            WebSignIn s = new WebSignIn(w, Arm.ClientId, "contoso.onmicrosoft.com", Arm.Scope);
            DeviceCode d = s.Start();
            T.Eq("ABCD-EFGH", d.UserCode, "Sign-in: user code");
            WebCall start = w.Calls[0];
            T.Check(start.Url == "https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/devicecode", "Sign-in: device code at the tenant");
            T.Check(start.Text.Contains("client_id=1950a258-227b-4e31-a9cf-717495945fc2"), "Sign-in: Azure PowerShell client for ARM");
            T.Check(start.Text.Contains("scope=" + Uri.EscapeDataString("https://management.azure.com/user_impersonation offline_access")), "Sign-in: ARM scope");
            using (ManualResetEvent cancel = new ManualResetEvent(false)) s.Wait(d, cancel);
            T.Check(s.SignedIn, "Sign-in: signed in after pending");
            T.Eq("admin@contoso.onmicrosoft.com", s.Account, "Sign-in: account from the token");
            T.Eq(2, polls, "Sign-in: polled until done");
            WebSignIn other = new WebSignIn(w, "not a guid", "", IntuneScripts.ScriptScope);
            T.Eq(GraphClient.DefaultClientId, other.ClientId, "Sign-in: falls back to the Graph client");
            T.Eq("organizations", other.Tenant, "Sign-in: empty tenant is organizations");

            FakeWeb w2 = new FakeWeb();
            w2.On("POST", "/token", 400, "{\"error\":\"authorization_declined\"}");
            WebSignIn s2 = new WebSignIn(w2, null, "contoso.onmicrosoft.com", Arm.Scope);
            DeviceCode d2 = new DeviceCode();
            d2.Code = "x";
            d2.Interval = 0;
            string msg = null;
            try { using (ManualResetEvent cancel = new ManualResetEvent(false)) s2.Wait(d2, cancel); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Eq("The sign-in was declined.", msg, "Sign-in: declined");
        }

        static FakeWeb ArmFake()
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", "/subscriptions?api-version=", 200, "{\"value\":[{\"subscriptionId\":\"" + Sub + "\",\"displayName\":\"Contoso Production\",\"state\":\"Enabled\",\"tenantId\":\"t1\"},{\"subscriptionId\":\"9\",\"displayName\":\"Old\",\"state\":\"Disabled\"},{\"subscriptionId\":\"1\",\"displayName\":\"Contoso Dev\",\"state\":\"Enabled\"}]}");
            w.On("GET", "/resourcegroups?api-version=", 200, "{\"value\":[{\"name\":\"rg-b\",\"location\":\"northeurope\"},{\"name\":\"rg-a\",\"location\":\"westeurope\"}]}");
            w.On("PUT", "/resourcegroups/rg-trackvice?", 201, "{}");
            w.On("POST", "/checkNameAvailability?", 200, "{\"nameAvailable\":true}");
            w.Handlers.Add(delegate (WebCall c)
            {
                if (c.Method != "PUT" || !c.Url.Contains("/storageAccounts/contosotrackvice?")) return null;
                WebReply r = new WebReply(202, "");
                r.Headers["Location"] = "https://management.azure.com/subscriptions/" + Sub + "/providers/Microsoft.Storage/locations/westeurope/asyncoperations/op1?api-version=2023-05-01";
                r.Headers["Retry-After"] = "17";
                return r;
            });
            int polls = 0;
            w.Handlers.Add(delegate (WebCall c)
            {
                if (!c.Url.Contains("/asyncoperations/op1")) return null;
                polls++;
                if (polls < 2) return new WebReply(202, "");
                return new WebReply(200, "{\"id\":\"" + Arm.AccountId(Sub, "rg-trackvice", "contosotrackvice") + "\",\"name\":\"contosotrackvice\",\"location\":\"westeurope\",\"properties\":{\"provisioningState\":\"Succeeded\",\"primaryEndpoints\":{\"blob\":\"https://contosotrackvice.blob.core.windows.net/\"}}}");
            });
            w.On("PUT", "/blobServices/default/containers/trackvice?", 201, "{}");
            w.On("PUT", "/blobServices/default/containers/trackvice-agent?", 409, "{\"error\":{\"code\":\"ContainerAlreadyExists\"}}");
            w.On("GET", "/managementPolicies/default?", 200, "{\"properties\":{\"policy\":{\"rules\":[{\"enabled\":true,\"name\":\"keep-other\",\"type\":\"Lifecycle\",\"definition\":{\"actions\":{\"baseBlob\":{\"tierToCool\":{\"daysAfterModificationGreaterThan\":7}}},\"filters\":{\"blobTypes\":[\"blockBlob\"]}}},{\"enabled\":true,\"name\":\"trackvice-cleanup\",\"type\":\"Lifecycle\",\"definition\":{}}]}}}");
            w.On("PUT", "/managementPolicies/default?", 200, "{}");
            w.On("POST", "/listKeys?", 200, "{\"keys\":[{\"keyName\":\"key1\",\"permissions\":\"READ\",\"value\":\"bm8=\"},{\"keyName\":\"key2\",\"permissions\":\"FULL\",\"value\":\"" + Key + "\"}]}");
            return w;
        }

        static void Storage()
        {
            FakeWeb w = ArmFake();
            List<int> pauses = new List<int>();
            Arm arm = new Arm(w, delegate { return "tok"; });
            arm.Pause = delegate (int ms) { pauses.Add(ms); };

            List<AzureSubscription> subs = arm.Subscriptions();
            T.Eq(2, subs.Count, "ARM: disabled subscriptions are left out");
            T.Eq("Contoso Dev", subs[0].Name, "ARM: subscriptions sorted by name");
            T.Check(w.Calls[0].Url == "https://management.azure.com/subscriptions?api-version=2022-12-01" && w.Calls[0].Headers["Authorization"] == "Bearer tok", "ARM: subscriptions URL and token");
            List<KeyValuePair<string, string>> groups = arm.ResourceGroups(Sub);
            T.Eq("rg-a", groups[0].Key, "ARM: resource groups sorted");

            NewCustomerRun run = new NewCustomerRun();
            run.Customer = "Contoso";
            run.Subscription = subs[1];
            run.ResourceGroup = "rg-trackvice";
            run.NewGroup = true;
            run.AccountName = Arm.Suggest("Contoso", 0);
            List<string> progress = new List<string>();
            run.SetUpStorage(arm, delegate (string s) { progress.Add(s); });

            T.Eq("contosotrackvice", run.AccountName, "ARM: suggested name");
            WebCall rg = w.Last("PUT", "/resourcegroups/rg-trackvice?");
            T.Check(rg != null && rg.Text == "{\"location\":\"westeurope\"}", "ARM: resource group in West Europe");
            WebCall check = w.Last("POST", "/checkNameAvailability?api-version=2023-05-01");
            T.Check(check != null && check.Text.Contains("\"name\":\"contosotrackvice\"") && check.Text.Contains("\"type\":\"Microsoft.Storage/storageAccounts\""), "ARM: name checked");
            WebCall create = w.Last("PUT", "/storageAccounts/contosotrackvice?api-version=2023-05-01");
            T.Check(create != null, "ARM: account created with 2023-05-01");
            string body = create == null ? "" : create.Text;
            T.Check(body.Contains("\"kind\":\"StorageV2\"") && body.Contains("\"sku\":{\"name\":\"Standard_LRS\"}"), "ARM: StorageV2 Standard_LRS");
            T.Check(body.Contains("\"supportsHttpsTrafficOnly\":true") && body.Contains("\"minimumTlsVersion\":\"TLS1_2\""), "ARM: HTTPS only and TLS 1.2");
            T.Check(body.Contains("\"allowBlobPublicAccess\":false") && body.Contains("\"allowSharedKeyAccess\":true"), "ARM: no public access, key access on");
            T.Check(body.Contains("\"location\":\"westeurope\""), "ARM: region");
            T.Eq(2, w.Count("GET", "/asyncoperations/op1"), "ARM: polled the operation until done");
            T.Check(pauses.Count == 2 && pauses[0] == 17000, "ARM: waited as Retry-After says");
            T.Eq("https://contosotrackvice.blob.core.windows.net/", run.Account.BlobEndpoint, "ARM: blob endpoint from the account");
            T.Eq("rg-trackvice", run.Account.ResourceGroup, "ARM: resource group from the ID");
            WebCall cont = w.Last("PUT", "/containers/trackvice?");
            T.Check(cont != null && cont.Text == "{\"properties\":{\"publicAccess\":\"None\"}}", "ARM: private container");
            T.Check(run.Done.Contains("Container trackvice-agent, there already"), "ARM: an existing container is fine");
            WebCall pol = w.Last("PUT", "/managementPolicies/default?");
            string p = pol == null ? "" : pol.Text;
            T.Check(p.Contains("\"name\":\"keep-other\""), "ARM: other lifecycle rules are kept");
            T.Eq(1, CountOf(p, "\"name\":\"trackvice-cleanup\""), "ARM: the cleanup rule is replaced, not doubled");
            T.Check(p.Contains("\"prefixMatch\":[\"trackvice/sessions/\",\"trackvice/logs/\"]") && p.Contains("\"daysAfterModificationGreaterThan\":30"), "ARM: deletes sessions and logs after 30 days");
            T.Check(progress.Count >= 5, "ARM: progress reported");

            run.MakeLinks(arm, new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));
            T.Check(run.Links != null && run.Links.Viewer.Contains("sp=rl"), "ARM: links made with the full key");
            string all = run.Links.Upload + run.Links.Viewer + run.Links.Download;
            T.Check(all.IndexOf("bm8=", StringComparison.Ordinal) < 0, "ARM: the read-only key is not used");
            foreach (string d in run.Done) T.Check(d.IndexOf(Key, StringComparison.Ordinal) < 0, "ARM: the key is not in the summary");
        }

        static int CountOf(string s, string part)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        static void StorageErrors()
        {
            FakeWeb w = new FakeWeb();
            w.On("PUT", "/resourcegroups/", 403, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"The client does not have authorization to perform action.\"}}");
            w.On("POST", "/checkNameAvailability?", 200, "{\"nameAvailable\":false,\"reason\":\"AlreadyExists\",\"message\":\"taken\"}");
            Arm arm = new Arm(w, delegate { return "tok"; });
            string msg = null;
            try { arm.CreateResourceGroup(Sub, "rg-new", "westeurope"); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Check(msg != null && msg.StartsWith("Your account may not create the resource group rg-new.") && msg.EndsWith("Pick an existing resource group instead."), "ARM: no right to create a resource group: " + msg);
            T.Check(arm.NameProblem(Sub, "contosotrackvice").StartsWith("The name contosotrackvice is taken."), "ARM: name taken");
            T.Check(arm.NameProblem(Sub, "Contoso-TV") != null, "ARM: name rule");
            T.Eq(1, w.Count("POST", "/checkNameAvailability"), "ARM: a name against the rules is not sent");
            T.Eq(null, Arm.NameRule("abc"), "ARM: 3 characters are fine");
            T.Check(Arm.NameRule("ab") != null && Arm.NameRule(new string('a', 25)) != null, "ARM: length");
            T.Eq("woodgrovebanklttrackvice", Arm.Suggest("Woodgrove Bank Ltd.", 0), "ARM: long names are cut to 24");
            T.Eq("contosotrackvice2", Arm.Suggest("Contoso", 2), "ARM: numbered suggestion");
            T.Eq("agentstrackvice", Arm.Suggest("", 0), "ARM: empty customer gets a name");

            FakeWeb w2 = new FakeWeb();
            w2.On("PUT", "/storageAccounts/", 400, "{\"error\":{\"code\":\"InvalidRequest\",\"message\":\"Bad region.\"}}");
            Arm arm2 = new Arm(w2, delegate { return "tok"; });
            msg = null;
            try { arm2.CreateAccount(Sub, "rg", "contosotrackvice", "nowhere"); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Eq("Azure could not create the storage account contosotrackvice. Bad region.", msg, "ARM: error message");

            FakeWeb w3 = new FakeWeb();
            w3.On("POST", "/listKeys", 200, "{\"keys\":[]}");
            msg = null;
            try { new Arm(w3, delegate { return "tok"; }).Key(Account()); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Check(msg != null && msg.Contains("Allow storage account key access"), "ARM: no key");
        }

        static void ExistingAccount()
        {
            FakeWeb w = ArmFake();
            w.On("GET", "/providers/Microsoft.Storage/storageAccounts?", 200, "{\"value\":[{\"id\":\"" + Arm.AccountId(Sub, "rg-logs", "contosologs") + "\",\"name\":\"contosologs\",\"location\":\"northeurope\",\"kind\":\"StorageV2\",\"properties\":{}}]}");
            Arm arm = new Arm(w, delegate { return "tok"; });
            List<AzureAccount> l = arm.StorageAccounts(Sub);
            T.Eq(1, l.Count, "ARM: existing accounts listed");
            T.Eq("https://contosologs.blob.core.windows.net/", l[0].BlobEndpoint, "ARM: endpoint made from the name when missing");
            NewCustomerRun run = new NewCustomerRun();
            run.Subscription = new AzureSubscription();
            run.Subscription.Id = Sub;
            run.NewAccount = false;
            run.Account = l[0];
            run.RetentionDays = 14;
            run.SetUpStorage(arm, delegate { });
            T.Eq(0, w.Count("PUT", "/storageAccounts/contosologs?"), "ARM: an existing account is not changed");
            T.Eq(0, w.Count("PUT", "/resourcegroups/"), "ARM: no resource group made for an existing account");
            T.Check(w.Last("PUT", "/storageAccounts/contosologs/blobServices/default/containers/trackvice?") != null, "ARM: containers in the existing account");
            T.Check(w.Last("PUT", "/managementPolicies/").Text.Contains("\"daysAfterModificationGreaterThan\":14"), "ARM: days can be changed");
        }

        static void Upload()
        {
            FakeWeb w = new FakeWeb();
            w.On("PUT", ".blob.core.windows.net/trackvice-agent/TrackVice.exe?", 201, "");
            w.On("HEAD", ".blob.core.windows.net/trackvice-agent/TrackVice.exe?", 200, "");
            NewCustomerRun run = new NewCustomerRun();
            run.Links = AgentLinks.Make(Account(), Key, new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), 30);
            byte[] exe = Encoding.ASCII.GetBytes("MZ made-up exe");
            run.Upload(w, exe);
            WebCall put = w.Last("PUT", "TrackVice.exe");
            T.Check(put != null && put.Headers["x-ms-blob-type"] == "BlockBlob" && put.Body.Length == exe.Length, "Upload: block blob with the exe");
            T.Check(put != null && put.Url.Contains("/trackvice-agent/TrackVice.exe?sp=cw&"), "Upload: with the publish link");
            T.Eq(AgentUpdate.Sha256Hex(exe).ToUpperInvariant(), run.ExeSha256, "Upload: SHA-256");
            T.Check(run.DownloadChecked, "Upload: download link checked");
            T.Check(w.Last("HEAD", "?sp=r&") != null, "Upload: checked with the download link");

            run.UpdateKey = "PUBKEY";
            run.Alert = " https://contoso.example/hook ";
            string s = run.Script();
            T.Check(s.Contains("$UpdateLink = '" + run.Links.UpdateRead + "'") && s.Contains("$UpdateKey = 'PUBKEY'"), "Script: update link only with a key");
            T.Check(s.Contains("$Alert     = 'https://contoso.example/hook'"), "Script: alert trimmed");
            T.Check(s.Contains("$Sha256    = '" + run.ExeSha256 + "'"), "Script: hash of the upload");

            FakeWeb bad = new FakeWeb();
            WebReply r403 = new WebReply(403, "");
            r403.Headers["x-ms-error-code"] = "AuthorizationPermissionMismatch";
            bad.Handlers.Add(delegate (WebCall c) { return r403; });
            string msg = null;
            try { BlobUpload.Put(bad, run.Links.Publish, "TrackVice.exe", exe); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Eq("The upload of TrackVice.exe failed (HTTP 403, AuthorizationPermissionMismatch).", msg, "Upload: error");
        }

        static void Intune()
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", "graph.microsoft.com/v1.0/groups?", 200, "{\"value\":[{\"id\":\"g1\",\"displayName\":\"Autopilot devices\"}]}");
            w.On("POST", "/deviceManagementScripts/s1/assign", 204, "");
            w.On("POST", "/deviceManagement/deviceManagementScripts", 201, "{\"id\":\"s1\"}");
            IntuneScripts i = new IntuneScripts(w, delegate { return "gtok"; });
            List<DirectoryGroup> g = i.FindGroups("Autopilot \"x");
            T.Eq(1, g.Count, "Intune: groups found");
            WebCall search = w.Calls[0];
            T.Eq("eventual", search.Headers["ConsistencyLevel"], "Intune: search sends ConsistencyLevel");
            T.Check(search.Url.StartsWith("https://graph.microsoft.com/v1.0/groups?$search=%22displayName%3AAutopilot%20x%22"), "Intune: search URL " + search.Url);

            NewCustomerRun run = new NewCustomerRun();
            run.Links = AgentLinks.Make(Account(), Key, new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), 30);
            run.ExeSha256 = "ABC";
            string msg = null;
            try { run.AddToIntune(i); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Check(msg != null, "Intune: a group is needed");
            run.Group = g[0];
            run.AddToIntune(i);
            T.Eq("s1", run.IntuneScriptId, "Intune: script ID kept");
            WebCall create = null;
            foreach (WebCall c in w.Calls) if (c.Url == IntuneScripts.ScriptsUrl) create = c;
            Dictionary<string, object> body = ImeJson.Parse(create.Text) as Dictionary<string, object>;
            T.Eq("https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts", create.Url, "Intune: beta endpoint");
            T.Eq("system", Http.Str(body, "runAsAccount"), "Intune: runs as system");
            T.Eq(false, ImeJson.Get(body, "enforceSignatureCheck"), "Intune: no signature check");
            T.Eq(false, ImeJson.Get(body, "runAs32Bit"), "Intune: 64-bit");
            T.Eq("Install-TrackViceAgent.ps1", Http.Str(body, "fileName"), "Intune: file name");
            T.Eq("TrackVice agent", Http.Str(body, "displayName"), "Intune: name");
            string content = Encoding.UTF8.GetString(Convert.FromBase64String(Http.Str(body, "scriptContent")));
            T.Check(content == run.Script(), "Intune: content is the filled script");
            WebCall assign = w.Last("POST", "/assign");
            T.Eq("{\"deviceManagementScriptAssignments\":[{\"target\":{\"@odata.type\":\"#microsoft.graph.groupAssignmentTarget\",\"groupId\":\"g1\"}}]}", assign.Text, "Intune: assignment body");
            T.Eq("Bearer gtok", assign.Headers["Authorization"], "Intune: Graph token");
            // A second try after a failed assignment does not add the script twice.
            run.AddToIntune(i);
            T.Eq(1, w.Count("POST", "/deviceManagement/deviceManagementScripts") - w.Count("POST", "/assign"), "Intune: script added once");

            FakeWeb w403 = new FakeWeb();
            w403.On("POST", "deviceManagementScripts", 403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"No permission\"}}");
            msg = null;
            try { new IntuneScripts(w403, delegate { return "t"; }).Create("a", "b", "c"); }
            catch (SetupException ex) { msg = ex.Message; }
            T.Check(msg != null && msg.StartsWith("Access denied") && msg.Contains("DeviceManagementScripts.ReadWrite.All"), "Intune: 403 explained");
        }

        // The whole run against the demo answers, as the window does it in demo mode.
        static void Demo()
        {
            DemoSetupWeb w = new DemoSetupWeb();
            WebSignIn s = new WebSignIn(w, Arm.ClientId, "contoso.onmicrosoft.com", Arm.Scope);
            DeviceCode d = s.Start();
            using (ManualResetEvent cancel = new ManualResetEvent(false)) s.Wait(d, cancel);
            T.Eq("admin@northwind.onmicrosoft.com", s.Account, "Demo: signed in");
            Arm arm = new Arm(w, s.Token);
            arm.Pause = delegate { };
            NewCustomerRun run = new NewCustomerRun();
            run.Customer = "Contoso";
            run.Subscription = arm.Subscriptions()[0];
            run.ResourceGroup = "rg-trackvice";
            run.NewGroup = true;
            run.AccountName = Arm.Suggest(run.Customer, 0);
            run.SetUpStorage(arm, delegate { });
            run.MakeLinks(arm, DateTime.UtcNow);
            run.Upload(w, Encoding.ASCII.GetBytes("MZ"));
            WebSignIn g = new WebSignIn(w, null, "contoso.onmicrosoft.com", IntuneScripts.ScriptScope);
            using (ManualResetEvent cancel = new ManualResetEvent(false)) g.Wait(g.Start(), cancel);
            IntuneScripts i = new IntuneScripts(w, g.Token);
            run.Group = i.FindGroups("Autopilot")[0];
            run.AddToIntune(i);
            T.Check(run.DownloadChecked && run.IntuneScriptId.Length > 0, "Demo: every step answers");
            T.Check(run.Links.Viewer.StartsWith("https://contosotrackvice.blob.core.windows.net/trackvice?"), "Demo: viewer link");
        }

        static void Json()
        {
            string j = SetupJson.Write(SetupJson.Obj("a", "x\"y", "b", 3, "c", true, "d", null, "e", SetupJson.List(1.5, "z")));
            T.Eq("{\"a\":\"x\\\"y\",\"b\":3,\"c\":true,\"d\":null,\"e\":[1.5,\"z\"]}", j, "JSON: writer");
            Dictionary<string, object> back = ImeJson.Parse(j) as Dictionary<string, object>;
            T.Eq("x\"y", Http.Str(back, "a"), "JSON: reads back");
        }
    }
}
