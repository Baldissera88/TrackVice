namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    // Customer onboarding and the writes to Graph and Intune: what reaches the platform script, where tokens go,
    // what an assignment change keeps, and which tenant and app are used.
    public static class OnboardingSecurityTests
    {
        sealed class Web : IWeb
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

            public int Count(string method, string urlPart)
            {
                int n = 0;
                foreach (WebCall c in Calls) if (c.Method == method && c.Url.Contains(urlPart)) n++;
                return n;
            }

            public WebCall Last(string method, string urlPart)
            {
                for (int i = Calls.Count - 1; i >= 0; i--) if (Calls[i].Method == method && Calls[i].Url.Contains(urlPart)) return Calls[i];
                return null;
            }

            public bool AnyTo(string host)
            {
                foreach (WebCall c in Calls) if ((c.Url ?? "").IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
        }

        sealed class Plain : ICustomerProtector
        {
            public string Protect(string plain) { return plain; }
            public string Unprotect(string stored) { return stored; }
        }

        const string TenantA = "11111111-2222-4333-8444-555555555555";
        const string TenantB = "66666666-7777-4888-9999-aaaaaaaaaaaa";

        public static void Run()
        {
            ScriptQuotes();
            HostileCode();
            GraphNextLink();
            ArmLinks();
            AssignmentsKept();
            ReusedGroup();
            Tenants();
            VerificationPage();
            Redirects();
            Container();
            AppRegistrationReuse();
        }

        static string Jwt(string tid)
        {
            string p = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"tid\":\"" + tid + "\",\"upn\":\"admin@contoso.example\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "eyJhbGciOiJub25lIn0." + p + ".sig";
        }

        static string ValidCode()
        {
            CustomerCode c = new CustomerCode();
            c.Name = "Contoso";
            c.Upload = "https://contosotrackvice.blob.core.windows.net/tv-contoso?sp=cw&sig=abc";
            c.Agent = "https://contosotrackvice.blob.core.windows.net/trackvice-agent?sp=r&sig=def";
            using (RSACryptoServiceProvider k = UpdateKeys.Create()) c.Key = UpdateKeys.PublicKey(k);
            return c.ToString();
        }

        // Every quote that ends a PowerShell single-quoted string is doubled, so a value cannot end the string.
        static void ScriptQuotes()
        {
            foreach (char q in new char[] { '\'', '\u2018', '\u2019', '\u201A', '\u201B' })
            {
                string v = "x" + q + "; Start-Process calc #";
                string lit = AgentScript.Quote(v);
                T.Eq("'x" + q + q + "; Start-Process calc #'", lit, "script: quote U+" + ((int)q).ToString("X4") + " is doubled");
                // Read the literal the way PowerShell does: a quote ends it unless the next character is a quote too.
                int i = 1;
                StringBuilder got = new StringBuilder();
                bool closed = false;
                while (i < lit.Length)
                {
                    char c = lit[i];
                    if (AgentScript.IsSingleQuote(c))
                    {
                        if (i + 1 < lit.Length && AgentScript.IsSingleQuote(lit[i + 1])) { got.Append(c); i += 2; continue; }
                        closed = true;
                        break;
                    }
                    got.Append(c);
                    i++;
                }
                T.Check(closed && i == lit.Length - 1 && got.ToString() == v, "script: the value with U+" + ((int)q).ToString("X4") + " stays inside the string");
            }
            ScriptValues sv = new ScriptValues();
            sv.Alert = "https://hooks.example/a\u2019; iex x #";
            sv.Upload = @"\\fs01\Track\u2018Vice";
            string s = AgentScript.Fill(AgentScriptTemplate.Links, sv);
            T.Check(s.Contains("'https://hooks.example/a\u2019\u2019; iex x #'"), "script: a typographic quote in the alert link is doubled in the filled script");
        }

        // A customer code read from a shared key vault goes into a script that runs as System: only a real code is used.
        static void HostileCode()
        {
            string good = ValidCode();
            T.Eq(good, CustomerCodes.Checked("  " + good + " "), "code: a code TrackVice makes is taken, trimmed");
            string[] bad = { "tv1:abc\u2019; Start-Process calc; #", "tv1:abc'; iex x #", "tv1:", "not a code", null, "tv1:e30" };
            foreach (string b in bad)
            {
                bool threw = false;
                try { CustomerCodes.Checked(b); } catch (SetupException) { threw = true; }
                T.Check(threw, "code: refused: " + (b ?? "null"));
            }
            Web w = new Web();
            w.On("GET", IntuneScripts.ScriptsUrl + "?$select", 200, "{\"value\":[{\"id\":\"s1\",\"displayName\":\"" + AddCustomerRun.ScriptName + "\"}]}");
            w.On("PATCH", IntuneScripts.ScriptsUrl + "/s1", 204, "");
            bool refused = false;
            try { CustomerCodeChange.UpdateIntune(new IntuneSetup(w, delegate { return "token"; }), "Contoso", "tv1:abc\u2019; Start-Process calc; #"); }
            catch (SetupException) { refused = true; }
            T.Check(refused, "code: Update the script in Intune refuses a code that is not one TrackVice makes");
            T.Eq(0, w.Count("PATCH", "/s1"), "code: nothing is sent to Intune for it");
            CustomerCodeChange.UpdateIntune(new IntuneSetup(w, delegate { return "token"; }), "Contoso", good);
            T.Eq(1, w.Count("PATCH", "/s1"), "code: a real code is put in Intune");
        }

        static void GraphNextLink()
        {
            Web w = new Web();
            w.On("GET", IntuneScripts.ScriptsUrl + "?$select", 200, "{\"value\":[],\"@odata.nextLink\":\"https://evil.example/steal?x=1\"}");
            bool threw = false;
            try { new IntuneSetup(w, delegate { return "secret-token"; }).FindScript(AddCustomerRun.ScriptName); }
            catch (SetupException) { threw = true; }
            T.Check(threw, "graph: a next page on another host is refused");
            T.Check(!w.AnyTo("evil.example"), "graph: the token is not sent to the host of a next page");
            foreach (string next in new string[] { "https://graph.microsoft.com.evil.example/beta", "http://graph.microsoft.com/beta/x", "https://graph.microsoft.com:8443/beta/x", "https://user@graph.microsoft.com/beta/x", "/beta/x" })
            {
                Dictionary<string, object> page = new Dictionary<string, object>();
                page["@odata.nextLink"] = next;
                bool t = false;
                try { IntuneSetup.NextLink(page); } catch (SetupException) { t = true; }
                T.Check(t, "graph: next page refused: " + next);
            }
            Web ok = new Web();
            ok.Handlers.Add(delegate (WebCall c)
            {
                if (c.Url == IntuneScripts.ScriptsUrl + "?$select=id,displayName") return new WebReply(200, "{\"value\":[],\"@odata.nextLink\":\"https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts?$skiptoken=2\"}");
                if (c.Url.Contains("$skiptoken=2")) return new WebReply(200, "{\"value\":[{\"id\":\"s2\",\"displayName\":\"" + AddCustomerRun.ScriptName + "\"}]}");
                return null;
            });
            T.Eq("s2", new IntuneSetup(ok, delegate { return "t"; }).FindScript(AddCustomerRun.ScriptName), "graph: a next page on Microsoft Graph is followed");
        }

        static void ArmLinks()
        {
            const string Sub = "0b1c2d3e-4f50-4a6b-8c7d-9e0f1a2b3c4d";
            Web w = new Web();
            w.Handlers.Add(delegate (WebCall c)
            {
                if (c.Method == "PUT" && c.Url.Contains("/storageAccounts/contosonew"))
                {
                    WebReply r = new WebReply(202, "");
                    r.Headers["Location"] = "https://evil.example/poll";
                    return r;
                }
                return null;
            });
            Arm arm = new Arm(w, delegate { return "arm-token"; });
            arm.Pause = delegate { };
            bool threw = false;
            try { arm.CreateAccount(Sub, "rg", "contosonew", "westeurope"); } catch (SetupException) { threw = true; }
            T.Check(threw, "arm: a Location on another host is refused");
            T.Check(!w.AnyTo("evil.example"), "arm: the token is not sent to the Location of another host");

            Web n = new Web();
            n.On("GET", "/subscriptions?", 200, "{\"value\":[],\"nextLink\":\"https://management.azure.com@evil.example/subscriptions?page=2\"}");
            threw = false;
            try { new Arm(n, delegate { return "arm-token"; }).Subscriptions(); } catch (SetupException) { threw = true; }
            T.Check(threw && !n.AnyTo("evil.example"), "arm: a nextLink on another host is refused");

            T.Eq(Arm.Base + "/subscriptions?api-version=1", Arm.Absolute("/subscriptions?api-version=1"), "arm: a path goes to Azure Resource Manager");
            T.Eq("https://management.azure.com/x?page=2", Arm.Absolute("https://management.azure.com/x?page=2"), "arm: a link on Azure Resource Manager is followed");
            foreach (string bad in new string[] { "//evil.example/x", "http://management.azure.com/x", "https://management.azure.com.evil.example/x", "subscriptions/x" })
            {
                bool t = false;
                try { Arm.Absolute(bad); } catch (SetupException) { t = true; }
                T.Check(t, "arm: refused: " + bad);
            }
        }

        static Web IntuneWeb(string assignments)
        {
            Web w = new Web();
            w.On("GET", IntuneSetup.GroupsUrl + "?$filter=", 200, "{\"value\":[]}");
            w.Handlers.Add(delegate (WebCall c) { return c.Method == "POST" && c.Url == IntuneSetup.GroupsUrl ? new WebReply(201, "{\"id\":\"g-new\"}") : null; });
            w.On("GET", IntuneScripts.ScriptsUrl + "?$select", 200, "{\"value\":[{\"id\":\"s1\",\"displayName\":\"" + AddCustomerRun.ScriptName + "\"}]}");
            w.On("PATCH", IntuneScripts.ScriptsUrl + "/s1", 204, "");
            w.On("GET", "/s1/assignments", 200, assignments);
            w.On("POST", "/s1/assign", 204, "");
            return w;
        }

        static AddCustomerRun Run(string customer)
        {
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = customer;
            r.Script = "Write-Output 'agent'";
            return r;
        }

        // The assign action replaces every assignment: an exclusion stays an exclusion and All devices stays.
        static void AssignmentsKept()
        {
            Web w = IntuneWeb("{\"value\":["
                + "{\"target\":{\"@odata.type\":\"#microsoft.graph.exclusionGroupAssignmentTarget\",\"groupId\":\"g-servers\"}},"
                + "{\"target\":{\"@odata.type\":\"#microsoft.graph.allDevicesAssignmentTarget\"}},"
                + "{\"target\":{\"@odata.type\":\"#microsoft.graph.groupAssignmentTarget\",\"groupId\":\"g-pilot\"}}]}");
            AddCustomerRun r = Run("Contoso");
            r.ToIntune(new IntuneSetup(w, delegate { return "t"; }), delegate { return true; });
            WebCall a = w.Last("POST", "/s1/assign");
            T.Check(a != null, "assign: the new group is assigned");
            Dictionary<string, object> body = ImeJson.Parse(a != null ? a.Text : "{}") as Dictionary<string, object>;
            List<object> l = ImeJson.Get(body, "deviceManagementScriptAssignments") as List<object>;
            List<string> got = new List<string>();
            if (l != null)
                foreach (object o in l)
                {
                    Dictionary<string, object> t = ImeJson.Get(o as Dictionary<string, object>, "target") as Dictionary<string, object>;
                    got.Add(Http.Str(t, "@odata.type") + "|" + (Http.Str(t, "groupId") ?? ""));
                }
            T.Check(got.Contains("#microsoft.graph.exclusionGroupAssignmentTarget|g-servers"), "assign: the excluded group stays excluded, not assigned");
            T.Check(!got.Contains("#microsoft.graph.groupAssignmentTarget|g-servers"), "assign: the excluded group is not turned into an assignment");
            T.Check(got.Contains("#microsoft.graph.allDevicesAssignmentTarget|"), "assign: All devices is kept");
            T.Check(got.Contains("#microsoft.graph.groupAssignmentTarget|g-pilot") && got.Contains("#microsoft.graph.groupAssignmentTarget|g-new"), "assign: the group it had and the new group");
            T.Eq(4, got.Count, "assign: nothing else is sent");

            Web u = IntuneWeb("{\"value\":[{\"target\":{\"@odata.type\":\"#microsoft.graph.someFutureAssignmentTarget\",\"collectionId\":\"X\"}}]}");
            bool threw = false;
            try { Run("Contoso").ToIntune(new IntuneSetup(u, delegate { return "t"; }), delegate { return true; }); }
            catch (SetupException) { threw = true; }
            T.Check(threw && u.Count("POST", "/s1/assign") == 0, "assign: an assignment TrackVice cannot send back stops the change");

            // Assign only to devices keeps the rest as it was, or changes nothing.
            ScriptSettings ss = new ScriptSettings();
            ss.Id = "s1";
            ss.RunAsAccount = "system";
            List<ScriptAssignment> have = new List<ScriptAssignment>();
            Dictionary<string, object> users = new Dictionary<string, object>();
            users["@odata.type"] = "#microsoft.graph.allLicensedUsersAssignmentTarget";
            have.Add(ScriptChecker.Parse(users));
            Dictionary<string, object> dev = new Dictionary<string, object>();
            dev["@odata.type"] = "#microsoft.graph.allDevicesAssignmentTarget";
            have.Add(ScriptChecker.Parse(dev));
            Dictionary<string, object> odd = new Dictionary<string, object>();
            odd["@odata.type"] = "#microsoft.graph.someFutureAssignmentTarget";
            have.Add(ScriptChecker.Parse(odd));
            ScriptCheck chk = ScriptChecker.Evaluate(ss, have, new Dictionary<string, GroupFacts>());
            Web fw = new Web();
            fw.On("POST", "/s1/assign", 204, "");
            threw = false;
            try { new ScriptCheckApi(fw, delegate { return "t"; }, null).Fix(chk); } catch (SetupException) { threw = true; }
            T.Check(chk.CanFix && threw && fw.Count("POST", "/s1/assign") == 0, "fix: an assignment that cannot be sent back stops Assign only to devices");

            Web p = IntuneWeb("{\"value\":[{\"target\":{\"groupId\":\"g-new\"}}]}");
            Run("Contoso").ToIntune(new IntuneSetup(p, delegate { return "t"; }), delegate { return true; });
            T.Eq(0, p.Count("POST", "/s1/assign"), "assign: a group assigned already is not sent again");
        }

        static void ReusedGroup()
        {
            Web w = IntuneWeb("{\"value\":[]}");
            w.Handlers.Insert(0, delegate (WebCall c)
            {
                return c.Method == "GET" && c.Url.StartsWith(IntuneSetup.GroupsUrl + "?$filter=") ? new WebReply(200, "{\"value\":[{\"id\":\"g-old\",\"displayName\":\"" + AddCustomerRun.GroupName + "\",\"groupTypes\":[],\"membershipRule\":null}]}") : null;
            });
            AddCustomerRun r = Run("Contoso");
            r.ToIntune(new IntuneSetup(w, delegate { return "t"; }), delegate { return true; });
            T.Check(r.GroupReused && string.Join("\n", r.Done.ToArray()).Contains("not the dynamic group TrackVice makes"), "group: a group of that name with assigned members is pointed out");
            Web d = IntuneWeb("{\"value\":[]}");
            d.Handlers.Insert(0, delegate (WebCall c)
            {
                return c.Method == "GET" && c.Url.StartsWith(IntuneSetup.GroupsUrl + "?$filter=") ? new WebReply(200, "{\"value\":[{\"id\":\"g-old\",\"displayName\":\"" + AddCustomerRun.GroupName + "\",\"groupTypes\":[\"DynamicMembership\"],\"membershipRule\":" + AlertSender.Json(AddCustomerRun.GroupRule) + "}]}") : null;
            });
            AddCustomerRun r2 = Run("Contoso");
            r2.ToIntune(new IntuneSetup(d, delegate { return "t"; }), delegate { return true; });
            T.Check(r2.GroupReused && !string.Join("\n", r2.Done.ToArray()).Contains("not the dynamic group"), "group: the dynamic group TrackVice makes is used without a note");
        }

        static void Tenants()
        {
            CustomerBook book = new CustomerBook(new MemoryCustomerStorage(), new Plain());
            Customer a = new Customer();
            a.Name = "Contoso";
            a.Tenant = TenantA;
            book.Add(a);
            Customer f = new Customer();
            f.Name = "Fabrikam";
            f.Tenant = "fabrikam.onmicrosoft.com";
            book.Add(f);
            T.Check(AddCustomerRun.TenantProblem(book, "Contoso", TenantB) != null, "tenant: a sign-in to another tenant than the customer's is refused");
            T.Eq(null, AddCustomerRun.TenantProblem(book, "contoso", TenantA.ToUpperInvariant()), "tenant: the customer's own tenant is fine");
            T.Eq(null, AddCustomerRun.TenantProblem(book, "Fabrikam", TenantB), "tenant: a customer kept with a domain cannot be compared");
            T.Eq(null, AddCustomerRun.TenantProblem(book, "Contoso", null), "tenant: a token without a tenant is not refused here");
            Customer o = AddCustomerRun.OtherInTenant(book, "Northwind", TenantA);
            T.Check(o != null && o.Name == "Contoso", "tenant: a new customer in the tenant of another customer is found");
            T.Eq(null, AddCustomerRun.OtherInTenant(book, "Contoso", TenantA), "tenant: the customer itself is not another customer");
            T.Eq(null, AddCustomerRun.OtherInTenant(book, "Northwind", TenantB), "tenant: a tenant of no customer");
            Web w = new Web();
            T.Eq(TenantB, new IntuneSetup(w, delegate { return Jwt(TenantB); }).Tenant(), "tenant: the tenant of the Intune sign-in is read from its token");
        }

        static void VerificationPage()
        {
            T.Eq("https://microsoft.com/devicelogin", WebSignIn.VerificationPage("https://microsoft.com/devicelogin"), "device code: the Microsoft page is shown");
            T.Eq("https://login.microsoftonline.com/common/oauth2/deviceauth", WebSignIn.VerificationPage("https://login.microsoftonline.com/common/oauth2/deviceauth"), "device code: a login.microsoftonline.com page is shown");
            foreach (string bad in new string[] { "https://evil.example/devicelogin", "file:///C:/Windows/System32/calc.exe", "C:\\Windows\\System32\\calc.exe", "http://microsoft.com/devicelogin", "https://microsoft.com.evil.example/x", "https://evilmicrosoft.com/x", "https://microsoft.com:8443/x", null, "" })
                T.Eq(WebSignIn.DefaultVerificationPage, WebSignIn.VerificationPage(bad), "device code: not opened: " + (bad ?? "null"));
            Web w = new Web();
            w.On("POST", "/devicecode", 200, "{\"device_code\":\"dc\",\"user_code\":\"ABCD\",\"verification_uri\":\"calc.exe\",\"interval\":0,\"expires_in\":60}");
            DeviceCode d = new WebSignIn(w, null, TenantA, IntuneScripts.ScriptScope).Start();
            T.Eq(WebSignIn.DefaultVerificationPage, d.VerificationUri, "device code: the page from the answer is checked before the window opens it");
        }

        static void Redirects()
        {
            WebCall t = WebCall.Json("GET", "https://graph.microsoft.com/v1.0/groups", "token", null);
            T.Check(!HttpWeb.Follows(t), "redirect: a call with a token is not sent on");
            WebCall s = new WebCall();
            s.Url = "https://a.blob.core.windows.net/c/agent/latest.json?sp=r&SIG=abc";
            T.Check(!HttpWeb.Follows(s), "redirect: a call with a signed link is not sent on");
            WebCall p = new WebCall();
            p.Url = "https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode";
            T.Check(HttpWeb.Follows(p), "redirect: a call without a secret is sent on as before");
            bool threw = false;
            try { HttpWeb.ReadCapped(new StringReader(new string('x', 20000)), 10000); } catch (SetupException) { threw = true; }
            T.Check(threw, "reply: an answer over the limit is not read");
            T.Eq(5000, HttpWeb.ReadCapped(new StringReader(new string('x', 5000)), 10000).Length, "reply: an answer under the limit is read");
        }

        static void Container()
        {
            bool threw = false;
            try { new WebContainer(new Web(), "http://a.blob.core.windows.net/trackvice-agent?sp=r&sig=abc"); } catch (StoreException) { threw = true; }
            T.Check(threw, "container: an http link is refused");
            Web w = new Web();
            WebContainer c = new WebContainer(w, "https://a.blob.core.windows.net/trackvice-agent?sp=r&sig=abc");
            foreach (string bad in new string[] { "../tv-other/x", "agent/../../x", "agent//x", "./x" })
            {
                threw = false;
                try { c.Find(bad); } catch (StoreException) { threw = true; }
                T.Check(threw, "container: name refused: " + bad);
            }
            T.Eq(0, w.Calls.Count, "container: nothing is sent for a refused name");
            T.Eq(null, c.Find(UpdateManifest.Name), "container: agent/latest.json is read as before");
        }

        static void AppRegistrationReuse()
        {
            string bad = "{\"value\":[{\"id\":\"o1\",\"appId\":\"c4d5e6f7-1a2b-4c3d-9e8f-0a1b2c3d4e5f\",\"displayName\":\"TrackVice\",\"signInAudience\":\"AzureADMyOrg\","
                + "\"passwordCredentials\":[{\"hint\":\"abc\"}],\"keyCredentials\":[],\"web\":{\"redirectUris\":[\"https://evil.example/cb\"]},\"spa\":{\"redirectUris\":[]},"
                + "\"publicClient\":{\"redirectUris\":[\"" + AppRegistrationMaker.Redirect + "\"]}}]}";
            Web w = new Web();
            w.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, bad);
            w.On("GET", AppRegistrationMaker.PrincipalsUrl + "?", 200, "{\"value\":[]}");
            w.On("POST", AppRegistrationMaker.PrincipalsUrl, 201, "{\"id\":\"sp\"}");
            AppRegistrationMaker m = new AppRegistrationMaker(w, delegate { return Jwt(TenantA); });
            m.Pause = delegate { };
            AppRegistrationResult f = m.Find("TrackVice");
            string all = f == null ? "" : string.Join("; ", f.Problems.ToArray());
            T.Check(f != null && all.Contains("client secret") && all.Contains("https://evil.example/cb"), "app: a secret and a web redirect URI are found");
            T.Check(!all.Contains(AppRegistrationMaker.Redirect), "app: the redirect URI TrackVice adds is not a problem");
            T.Check(w.Last("GET", AppRegistrationMaker.ApplicationsUrl + "?").Url.Contains("passwordCredentials"), "app: the search reads the credentials and redirect URIs");
            bool threw = false;
            try { m.Make("TrackVice"); } catch (SetupException) { threw = true; }
            T.Check(threw, "app: an app named TrackVice with a secret is not used");
            T.Eq(0, w.Count("POST", AppRegistrationMaker.PrincipalsUrl), "app: nothing is made for it");

            Dictionary<string, object> multi = ImeJson.Parse("{\"signInAudience\":\"AzureADMultipleOrgs\",\"publicClient\":{\"redirectUris\":[\"https://evil.example/x\",\"msalc4d5e6f7://auth\",\"http://localhost\"]}}") as Dictionary<string, object>;
            List<string> mp = AppRegistrationMaker.Problems(multi);
            string ms = string.Join("; ", mp.ToArray());
            T.Check(ms.Contains("other tenants") && ms.Contains("https://evil.example/x") && !ms.Contains("msal") && !ms.Contains("localhost"), "app: other tenants and an https redirect URI elsewhere; desktop redirect URIs are fine");

            string clean = "{\"value\":[{\"id\":\"o1\",\"appId\":\"c4d5e6f7-1a2b-4c3d-9e8f-0a1b2c3d4e5f\",\"displayName\":\"TrackVice\",\"signInAudience\":\"AzureADMyOrg\",\"passwordCredentials\":[],\"keyCredentials\":[],\"web\":{\"redirectUris\":[]},\"spa\":{\"redirectUris\":[]},\"publicClient\":{\"redirectUris\":[\"" + AppRegistrationMaker.Redirect + "\"]}}]}";
            Web c = new Web();
            c.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, clean);
            c.On("GET", AppRegistrationMaker.PrincipalsUrl + "?", 200, "{\"value\":[]}");
            c.On("POST", AppRegistrationMaker.PrincipalsUrl, 201, "{\"id\":\"sp\"}");
            AppRegistrationMaker cm = new AppRegistrationMaker(c, delegate { return Jwt(TenantA); });
            cm.Pause = delegate { };
            AppRegistrationResult made = cm.Make("TrackVice");
            T.Check(made.Existed && made.Problems.Count == 0 && made.ClientId == "c4d5e6f7-1a2b-4c3d-9e8f-0a1b2c3d4e5f", "app: an app like the one TrackVice makes is used as before");
        }
    }
}
