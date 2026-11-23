namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;

    // What the sign-ins of the setup need, and the plain messages for the answers that mean a role or a consent is missing.
    public static class SetupNeedsTests
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
        }

        const string Sub = "0b1c2d3e-4f50-4a6b-8c7d-9e0f1a2b3c4d";

        public static void Run()
        {
            Table();
            Checklists();
            AzureErrors();
            GraphErrors();
            SignInErrors();
            Docs();
        }

        static string Fail(Action a)
        {
            try { a(); }
            catch (SetupException ex) { return ex.Message; }
            return null;
        }

        static void Table()
        {
            HashSet<string> ids = new HashSet<string>();
            foreach (SignInNeed n in SetupNeeds.All())
            {
                T.Check(ids.Add(n.Id), "needs: one row per ID " + n.Id);
                T.Check(!string.IsNullOrEmpty(n.Used) && !string.IsNullOrEmpty(n.Account) && !string.IsNullOrEmpty(n.Role) && !string.IsNullOrEmpty(n.Consent) && !string.IsNullOrEmpty(n.Who), "needs: every column is filled for " + n.Id);
            }
            SignInNeed add = SetupNeeds.Find("intune-add");
            // The permissions in the code are the ones in the table.
            foreach (string scope in IntuneSetup.Scopes.Split(' '))
                T.Check(add.Consent.Contains(LookupChoice.Permission(scope)), "needs: Add customer lists " + scope);
            T.Check(SetupNeeds.Find("intune-code").Consent.Contains(LookupChoice.Permission(IntuneScripts.ScriptScope)), "needs: Customer code lists the script permission");
            T.Check(SetupNeeds.Find("links-guide").Consent.Contains(LookupChoice.Permission(IntuneScripts.GroupScope)), "needs: the guide with links lists the group permission it asks for");
            T.Check(SetupNeeds.Find("storage-new").Role.StartsWith("Contributor"), "needs: a new account needs Contributor");
            T.Check(SetupNeeds.Find("storage-existing").Role.StartsWith("Storage Account Contributor"), "needs: an existing account needs Storage Account Contributor");
            T.Check(SetupNeeds.Find("intune-add").Role.Contains("Intune Administrator") && SetupNeeds.Find("intune-add").Role.Contains("Entra ID P1"), "needs: the Intune role and the license of dynamic groups");
            T.Check(SetupNeeds.Find("intune-add").Who.Contains("Cloud Application Administrator") && SetupNeeds.Find("intune-add").Who.Contains("Privileged Role Administrator"), "needs: who gives consent");
            T.Eq(null, SetupNeeds.Find("nothing"), "needs: an unknown row");
        }

        static void Checklists()
        {
            List<NeedItem> mine = SetupNeeds.Checklist(false, false);
            List<NeedItem> own = SetupNeeds.Checklist(true, false);
            List<NeedItem> ask = SetupNeeds.Checklist(false, true);
            T.Eq(mine.Count + 1, own.Count, "checklist: the customer's own Azure adds a line");
            T.Eq(mine.Count + 1, ask.Count, "checklist: signing in to Azure each time adds a line");
            T.Eq("azure", own[0].Id, "checklist: the Azure line is first");
            T.Eq("key", ask[0].Id, "checklist: the key line");
            HashSet<string> ids = new HashSet<string>();
            foreach (NeedItem i in own) { T.Check(ids.Add(i.Id), "checklist: one line per ID " + i.Id); T.Check(i.Text.Length > 10 && !i.Text.Contains("!"), "checklist: plain text " + i.Id); }
            bool consent = false;
            foreach (NeedItem i in mine) if (i.Id == "consent" && i.Text.Contains("DeviceManagementScripts.ReadWrite.All") && i.Text.Contains("Group.ReadWrite.All")) consent = true;
            T.Check(consent, "checklist: the consent line names both permissions");
            List<SetupStep> steps = GettingStarted.Steps(GettingStarted.Customers, new FixedSetupFacts(false, false, false, false));
            T.Check(steps[0].Text.Contains("Storage Account Contributor") && steps[1].Text.Contains("Intune Administrator"), "checklist: Getting started says what the first two steps need");
            T.Check(!steps[2].Text.Contains("Contributor") && !steps[3].Text.Contains("Administrator"), "checklist: the other steps say nothing of it");
            T.Check(SetupNeeds.StorageChecklist().Count >= 3, "checklist: Your storage has one");
            T.Check(SetupNeeds.LinksChecklist().Count >= 3, "checklist: the guide with links has one");
        }

        static void AzureErrors()
        {
            FakeWeb w = new FakeWeb();
            w.On("PUT", "/resourcegroups/", 403, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"The client does not have authorization to perform action 'Microsoft.Resources/subscriptions/resourcegroups/write'.\"}}");
            w.On("POST", "/listKeys", 403, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"no\"}}");
            w.On("PUT", "/managementPolicies/", 403, "{\"error\":{\"code\":\"LinkedAuthorizationFailed\",\"message\":\"other tenant\"}}");
            w.On("PUT", "/storageAccounts/contosonew", 403, "{\"error\":{\"code\":\"RequestDisallowedByPolicy\",\"message\":\"Resource was disallowed by policy.\"}}");
            w.On("POST", "/checkNameAvailability", 409, "{\"error\":{\"code\":\"MissingSubscriptionRegistration\",\"message\":\"The subscription is not registered to use namespace 'Microsoft.Storage'.\"}}");
            w.On("GET", "/subscriptions?", 404, "{\"error\":{\"code\":\"SubscriptionNotFound\",\"message\":\"The subscription could not be found.\"}}");
            w.On("GET", "/resourcegroups?", 401, "{\"error\":{\"code\":\"InvalidAuthenticationTokenTenant\",\"message\":\"The access token is from the wrong issuer.\"}}");
            w.On("GET", "/storageAccounts?", 401, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"expired\"}}");
            Arm arm = new Arm(w, delegate { return "tok"; });
            AzureAccount a = new AzureAccount();
            a.Id = Arm.AccountId(Sub, "rg-contoso", "contosotrackvice");
            a.Name = "contosotrackvice";

            string m = Fail(delegate { arm.CreateResourceGroup(Sub, "rg-new", "westeurope"); });
            T.Check(m.StartsWith("Your account may not create the resource group rg-new."), "azure: the action: " + m);
            T.Check(m.Contains("Contributor (or Owner) on the subscription") && m.Contains("An Owner of the subscription") && m.Contains("Access control (IAM)"), "azure: a new resource group needs Contributor on the subscription: " + m);
            T.Check(m.Contains("Azure said: The client does not have authorization"), "azure: Azure's own words are kept");

            m = Fail(delegate { arm.Key(a); });
            T.Check(m.Contains("Storage Account Contributor on the storage account") && m.Contains("open the storage account"), "azure: the key needs Storage Account Contributor: " + m);

            m = Fail(delegate { arm.SetCleanup(a, 30); });
            T.Check(m.Contains("another tenant") || m.Contains("other tenant"), "azure: LinkedAuthorizationFailed names the tenant: " + m);
            m = Fail(delegate { arm.CreateAccount(Sub, "rg-contoso", "contosonew", "westeurope"); });
            T.Check(m.Contains("Azure Policy") && m.Contains("Compliance"), "azure: a policy: " + m);
            m = Fail(delegate { arm.NameProblem(Sub, "contosonew"); });
            T.Check(m.Contains("not registered") && m.Contains("Microsoft.Storage") && m.Contains("Resource providers"), "azure: a provider that is not registered: " + m);
            m = Fail(delegate { arm.Subscriptions(); });
            T.Check(m.Contains("no role on it") && m.Contains("Azure tenant"), "azure: subscription not found: " + m);
            m = Fail(delegate { arm.ResourceGroups(Sub); });
            T.Check(m.Contains("another tenant than the resource") && m.Contains("Azure tenant"), "azure: a token of another tenant: " + m);
            m = Fail(delegate { arm.StorageAccounts(Sub); });
            T.Check(m.StartsWith("The Azure sign-in has expired"), "azure: 401 is an expired sign-in: " + m);

            FakeWeb other = new FakeWeb();
            other.On("PUT", "/storageAccounts/", 400, "{\"error\":{\"code\":\"InvalidParameter\",\"message\":\"bad\"}}");
            m = Fail(delegate { new Arm(other, delegate { return "t"; }).CreateAccount(Sub, "rg", "contosonew", "westeurope"); });
            T.Check(m.StartsWith("Azure could not create the storage account contosonew.") && !m.Contains("role"), "azure: another error is not called a role: " + m);
        }

        static void GraphErrors()
        {
            FakeWeb w = new FakeWeb();
            w.On("POST", "deviceManagementScripts", 403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Access is denied\"}}");
            w.On("POST", "/groups", 403, "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}");
            w.On("GET", "deviceManagementScripts", 403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Application is not authorized to perform this operation. Application must have one of the following scopes: DeviceManagementScripts.Read.All\"}}");
            string m = Fail(delegate { new IntuneScripts(w, delegate { return "t"; }).Create("a", "b", "c"); });
            T.Check(m.StartsWith("Access denied: the account may not "), "graph: scripts: starts as before: " + m);
            T.Check(m.Contains("Intune Administrator") && m.Contains("DeviceManagementScripts.ReadWrite.All") && m.Contains("Privileged Role Administrator") && m.Contains("Intune Role Administrator"), "graph: scripts: the role, the consent and who gives them: " + m);

            IntuneSetup setup = new IntuneSetup(w, delegate { return "t"; });
            m = Fail(delegate { setup.CreateGroup("Contoso devices", "rule"); });
            T.Check(m.Contains("Groups Administrator") && m.Contains("Group.ReadWrite.All") && m.Contains("Assign to a group I choose"), "graph: groups: the role, the consent and the way round: " + m);
            m = Fail(delegate { setup.FindScript("x"); });
            T.Check(m.Contains("lacks the permission DeviceManagementScripts.ReadWrite.All") && m.Contains("Grant admin consent"), "graph: a token without the scope points to the consent: " + m);

            FakeWeb lic = new FakeWeb();
            lic.On("POST", "/groups", 400, "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"A premium license is required for dynamic membership.\"}}");
            m = Fail(delegate { new IntuneSetup(lic, delegate { return "t"; }).CreateGroup("Contoso devices", "rule"); });
            T.Check(m.Contains("Microsoft Entra ID P1"), "graph: a license for dynamic groups: " + m);

            FakeWeb gone = new FakeWeb();
            gone.On("POST", "deviceManagementScripts", 401, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"x\"}}");
            m = Fail(delegate { new IntuneScripts(gone, delegate { return "t"; }).Create("a", "b", "c"); });
            T.Check(m.StartsWith("The Intune sign-in has expired"), "graph: 401 is an expired sign-in: " + m);

            T.Eq(null, SetupErrors.Graph(500, "InternalServerError", "x", "do it", SetupErrors.GraphUse.Scripts), "graph: a server error is not about rights");
            string app = SetupErrors.Graph(403, "Authorization_RequestDenied", "Insufficient privileges", "make the app registration TrackVice", SetupErrors.GraphUse.AppRegistration);
            T.Check(app.Contains("Application Developer") && app.Contains("let users register applications"), "graph: app registration: " + app);
            string sp = SetupErrors.Graph(403, "Authorization_RequestDenied", "Insufficient privileges", "make the enterprise application", SetupErrors.GraphUse.ServicePrincipal);
            T.Check(sp.Contains("Cloud Application Administrator"), "graph: enterprise application: " + sp);
        }

        static string Reject(string endpoint, string error, string description, string client, string scopes)
        {
            FakeWeb w = new FakeWeb();
            string body = "{\"error\":\"" + error + "\",\"error_description\":\"" + description + "\"}";
            w.On("POST", "/devicecode", endpoint == "devicecode" ? 400 : 200, endpoint == "devicecode" ? body : "{\"device_code\":\"d\",\"user_code\":\"U\",\"interval\":0,\"expires_in\":60}");
            w.On("POST", "/token", 400, body);
            WebSignIn s = new WebSignIn(w, client, "contoso.onmicrosoft.com", scopes);
            return Fail(delegate
            {
                DeviceCode d = s.Start();
                s.Wait(d, new ManualResetEvent(false));
            });
        }

        static void SignInErrors()
        {
            string graph = IntuneSetup.Scopes;
            string m = Reject("token", "interaction_required", "AADSTS65001: The user or administrator has not consented to use the application. Trace ID: x", null, graph);
            T.Check(m.Contains("has not given consent for DeviceManagementScripts.ReadWrite.All, Group.ReadWrite.All to Microsoft Graph Command Line Tools") && m.Contains("Cloud Application Administrator") && m.Contains("AADSTS65001"), "sign-in: 65001 says which consent and who: " + m);
            m = Reject("token", "invalid_grant", "AADSTS90094: The grant requires admin permission.", null, graph);
            T.Check(m.Contains("has not given consent") && m.Contains("Grant admin consent"), "sign-in: 90094: " + m);
            m = Reject("token", "consent_required", "needs consent", null, graph);
            T.Check(m.Contains("has not given consent"), "sign-in: the consent_required error name: " + m);
            m = Reject("token", "invalid_client", "AADSTS7000218: The request body must contain the following parameter: 'client_assertion' or 'client_secret'.", "7c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f", graph);
            T.Check(m.Contains("public client") && m.Contains("Allow public client flows") && m.Contains("7c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f"), "sign-in: 7000218 names the setting: " + m);
            m = Reject("devicecode", "unauthorized_client", "AADSTS700016: Application with identifier '7c1d2e3f' was not found in the directory 'Contoso'.", "7c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f", graph);
            T.Check(m.Contains("does not know the app registration 7c1d2e3f") && m.Contains("Client ID"), "sign-in: 700016: " + m);
            m = Reject("token", "access_denied", "AADSTS53003: Access has been blocked by Conditional Access policies.", null, graph);
            T.Check(m.Contains("Conditional Access") && m.Contains("sign-in logs"), "sign-in: 53003: " + m);
            m = Reject("token", "invalid_grant", "AADSTS50079: The user is required to use multi-factor authentication.", null, graph);
            T.Check(m.Contains("multifactor"), "sign-in: 50079: " + m);
            m = Reject("devicecode", "invalid_scope", "AADSTS70011: The provided request must include a 'scope' input parameter.", "7c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f", graph);
            T.Check(m.Contains("API permissions"), "sign-in: 70011: " + m);
            m = Reject("devicecode", "invalid_request", "AADSTS90002: Tenant 'x' not found.", null, graph);
            T.Check(m.Contains("tenant was not found"), "sign-in: 90002: " + m);
            m = Reject("token", "invalid_grant", "AADSTS50105: The signed in user is not assigned to a role for the application.", null, graph);
            T.Check(m.Contains("not assigned") && m.Contains("Users and groups"), "sign-in: 50105: " + m);
            m = Reject("token", "invalid_grant", "AADSTS65004: The user declined to consent.", Arm.ClientId, Arm.Scope);
            T.Check(m.Contains("declined"), "sign-in: 65004: " + m);
            m = Reject("token", "invalid_grant", "AADSTS65001: no consent for Azure PowerShell", Arm.ClientId, Arm.Scope);
            T.Check(m.Contains("to Azure PowerShell") && !m.Contains("user_impersonation"), "sign-in: Azure PowerShell is named: " + m);
            m = Reject("token", "access_denied", "The user denied.", null, graph);
            T.Eq("The sign-in was declined.", m, "sign-in: a plain decline is as it was");
            m = Reject("token", "invalid_grant", "Something else went wrong.", null, graph);
            T.Eq("Something else went wrong.", m, "sign-in: another error keeps Microsoft's text");
            T.Eq("DeviceManagementScripts.ReadWrite.All, Group.ReadWrite.All", SetupErrors.Permissions(graph + " offline_access"), "sign-in: the permissions of the scopes");
            T.Eq(65001, SetupErrors.Aadsts("AADSTS65001: x"), "sign-in: the AADSTS number");
            T.Eq(0, SetupErrors.Aadsts("no code"), "sign-in: none");
        }

        // The table and the permissions are in the guide.
        static void Docs()
        {
            if (!File.Exists("docs/AGENT.md")) return;
            string doc = File.ReadAllText("docs/AGENT.md");
            T.Check(doc.Contains("## What you need"), "docs: a What you need section");
            foreach (SignInNeed n in SetupNeeds.All())
            {
                foreach (string perm in new string[] { "DeviceManagementScripts.ReadWrite.All", "Group.ReadWrite.All" })
                    if (n.Consent.Contains(perm)) T.Check(doc.Contains(perm), "docs: " + perm);
            }
            T.Check(doc.Contains("Storage Account Contributor") && doc.Contains("Intune Administrator") && doc.Contains("Privileged Role Administrator"), "docs: the roles");
            T.Check(doc.Contains("AADSTS65001") && doc.Contains("AuthorizationFailed") && doc.Contains("Authorization_RequestDenied"), "docs: the error codes");
        }
    }
}
