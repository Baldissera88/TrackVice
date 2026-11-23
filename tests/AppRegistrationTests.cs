namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    // The app registration of the customer's own: the permissions in the code, what is sent to Microsoft Graph, and the errors.
    public static class AppRegistrationTests
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
        }

        const string Tenant = "6f1a2b3c-4d5e-4f60-8a71-92b3c4d5e6f7";
        const string AppId = "7c1d2e3f-4a5b-4c6d-8e7f-0a1b2c3d4e5f";
        const string ObjectId = "1b2c3d4e-5f60-4a71-8b92-a3b4c5d6e7f8";

        public static void Run()
        {
            Permissions();
            Body();
            Making();
            Existing();
            Errors();
            Texts();
        }

        static string Jwt()
        {
            string p = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"tid\":\"" + Tenant + "\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "eyJhbGciOiJub25lIn0." + p + ".x";
        }

        static AppRegistrationMaker Maker(FakeWeb w, out int[] pauses)
        {
            int[] p = new int[1];
            AppRegistrationMaker m = new AppRegistrationMaker(w, delegate { return Jwt(); });
            m.Pause = delegate (int ms) { p[0]++; };
            pauses = p;
            return m;
        }

        static string Fail(Action a)
        {
            try { a(); }
            catch (SetupException ex) { return ex.Message; }
            return null;
        }

        static void Permissions()
        {
            HashSet<string> ids = new HashSet<string>(), names = new HashSet<string>();
            foreach (AppPermission p in AppPermissions.All)
            {
                T.Check(GraphClient.IsGuid(p.Id) && ids.Add(p.Id), "permissions: a unique ID for " + p.Name);
                T.Check(names.Add(p.Name) && p.Name.EndsWith(".All") && p.Used.Length > 10, "permissions: a unique name and use for " + p.Name);
                T.Eq(p.Name.Contains("ReadWrite"), p.Write, "permissions: only ReadWrite writes: " + p.Name);
            }
            T.Eq(6, AppPermissions.Reading().Count, "permissions: six that only read");
            T.Eq(2, AppPermissions.Writing().Count, "permissions: two that write");
            // Every permission the code signs in with is in the list, and nothing else writes.
            string[] scopes = { GraphClient.Scope, GraphClient.ScriptScope, LookupChoice.AppScope, LookupChoice.ConfigScope, ReadinessCheck.Scope, IntuneScripts.ScriptScope, IntuneSetup.GroupWriteScope, "https://graph.microsoft.com/Device.Read.All" };
            foreach (string s in scopes) T.Check(AppPermissions.Find(s) != null, "permissions: the code asks for " + s);
            T.Eq(scopes.Length, AppPermissions.All.Length, "permissions: the list has nothing the code does not ask for");
            T.Check(!AppPermissions.Find(GraphClient.Scope).Write && AppPermissions.Find(IntuneScripts.ScriptScope).Write && AppPermissions.Find(IntuneSetup.GroupWriteScope).Write, "permissions: what writes");
            T.Eq(null, AppPermissions.Find("Mail.Read"), "permissions: an unknown one");
            T.Eq("314874da-47d6-4978-88dc-cf0d37f0bb82", AppPermissions.Find("DeviceManagementManagedDevices.Read.All").Id, "permissions: the ID of a delegated permission");
            T.Check(AppPermissions.Names(AppPermissions.Writing()) == "DeviceManagementScripts.ReadWrite.All, Group.ReadWrite.All", "permissions: the names");
        }

        static void Body()
        {
            Dictionary<string, object> d = ImeJson.Parse(SetupJson.Write(AppRegistrationMaker.ApplicationBody("TrackVice"))) as Dictionary<string, object>;
            T.Eq("TrackVice", Http.Str(d, "displayName"), "body: the name");
            T.Eq("AzureADMyOrg", Http.Str(d, "signInAudience"), "body: one tenant only");
            T.Eq(true, ImeJson.Get(d, "isFallbackPublicClient"), "body: public client flows are allowed");
            Dictionary<string, object> pc = ImeJson.Get(d, "publicClient") as Dictionary<string, object>;
            List<object> uris = ImeJson.Get(pc, "redirectUris") as List<object>;
            T.Eq(1, uris.Count, "body: one redirect URI");
            T.Eq(AppRegistrationMaker.Redirect, (string)uris[0], "body: the redirect URI");
            List<object> req = ImeJson.Get(d, "requiredResourceAccess") as List<object>;
            T.Eq(1, req.Count, "body: one resource");
            Dictionary<string, object> graph = req[0] as Dictionary<string, object>;
            T.Eq("00000003-0000-0000-c000-000000000000", Http.Str(graph, "resourceAppId"), "body: Microsoft Graph");
            List<object> access = ImeJson.Get(graph, "resourceAccess") as List<object>;
            T.Eq(AppPermissions.All.Length, access.Count, "body: exactly the permissions of the code");
            for (int i = 0; i < access.Count; i++)
            {
                Dictionary<string, object> a = access[i] as Dictionary<string, object>;
                T.Eq(AppPermissions.All[i].Id, Http.Str(a, "id"), "body: permission " + AppPermissions.All[i].Name);
                T.Eq("Scope", Http.Str(a, "type"), "body: delegated, never an application permission " + AppPermissions.All[i].Name);
            }
            T.Check(!SetupJson.Write(d).Contains("passwordCredentials") && !SetupJson.Write(d).Contains("keyCredentials"), "body: no secret and no certificate");
            T.Eq("{\"appId\":\"" + AppId + "\"}", SetupJson.Write(AppRegistrationMaker.PrincipalBody(AppId)), "body: the enterprise application");
            string link = AppRegistrationMaker.ConsentLink(Tenant, AppId);
            T.Check(link.StartsWith("https://login.microsoftonline.com/" + Tenant + "/adminconsent?client_id=" + AppId + "&redirect_uri=https%3A%2F%2Flogin.microsoftonline.com"), "body: the consent link: " + link);
            T.Check(AppRegistrationMaker.ConsentLink(null, AppId).Contains("/organizations/adminconsent"), "body: a link without a tenant");
            string plan = string.Join("\n", AppRegistrationMaker.Plan("TrackVice").ToArray());
            foreach (AppPermission p in AppPermissions.All) T.Check(plan.Contains(p.Name), "plan: names " + p.Name);
            T.Check(plan.Contains("no secret") && plan.Contains("Allow public client flows") && plan.Contains("enterprise application"), "plan: what it makes");
        }

        static FakeWeb Tenantless()
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, "{\"value\":[]}");
            w.On("POST", AppRegistrationMaker.ApplicationsUrl, 201, "{\"id\":\"" + ObjectId + "\",\"appId\":\"" + AppId + "\"}");
            w.On("GET", AppRegistrationMaker.PrincipalsUrl + "?", 200, "{\"value\":[]}");
            w.On("POST", AppRegistrationMaker.PrincipalsUrl, 201, "{\"id\":\"2c3d4e5f-6071-4b82-9ca3-b4c5d6e7f809\"}");
            return w;
        }

        static void Making()
        {
            FakeWeb w = Tenantless();
            int[] pauses;
            AppRegistrationMaker m = Maker(w, out pauses);
            T.Eq(0, w.Calls.Count, "make: nothing is sent before Make");
            AppRegistrationResult x0 = m.Find("TrackVice");
            T.Eq(null, x0, "make: no app of that name");
            T.Eq(1, w.Calls.Count, "make: looking is one read");
            T.Eq("GET", w.Calls[0].Method, "make: looking only reads");
            T.Check(w.Calls[0].Url.Contains("%24filter=displayName%20eq%20%27TrackVice%27") || w.Calls[0].Url.Contains("displayName"), "make: looks by name");
            T.Eq("Bearer " + Jwt(), w.Calls[0].Headers["Authorization"], "make: with the sign-in");

            AppRegistrationResult x = m.Make("TrackVice");
            T.Eq(AppId, x.ClientId, "make: the Client ID");
            T.Eq(ObjectId, x.ObjectId, "make: the object ID");
            T.Eq(Tenant, x.Tenant, "make: the tenant from the token");
            T.Check(!x.Existed && x.MadePrincipal, "make: made now");
            T.Check(x.ConsentLink.Contains("/" + Tenant + "/adminconsent?client_id=" + AppId), "make: the consent link");
            T.Eq(1, w.Count("POST", AppRegistrationMaker.ApplicationsUrl), "make: one application");
            T.Eq(1, w.Count("POST", AppRegistrationMaker.PrincipalsUrl), "make: one enterprise application");
            T.Eq(0, w.Count("PATCH", "graph.microsoft.com") + w.Count("DELETE", "graph.microsoft.com") + w.Count("PUT", "graph.microsoft.com"), "make: nothing else is changed");
            T.Check(w.Last("POST", AppRegistrationMaker.ApplicationsUrl).Text == SetupJson.Write(AppRegistrationMaker.ApplicationBody("TrackVice")), "make: the body is the one shown");
            T.Check(w.Last("POST", AppRegistrationMaker.PrincipalsUrl).Text.Contains(AppId), "make: the enterprise application is for the app");
            int posts = 0;
            foreach (WebCall c in w.Calls) if (c.Method == "POST") posts++;
            T.Eq(2, posts, "make: two changes in all");
            T.Eq(0, pauses[0], "make: no waiting when it works at once");

            // The directory can answer 400 for a short time after the application is made.
            FakeWeb late = new FakeWeb();
            late.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, "{\"value\":[]}");
            late.On("POST", AppRegistrationMaker.ApplicationsUrl, 201, "{\"id\":\"" + ObjectId + "\",\"appId\":\"" + AppId + "\"}");
            late.On("GET", AppRegistrationMaker.PrincipalsUrl + "?", 200, "{\"value\":[]}");
            int tries = 0;
            late.Handlers.Add(delegate (WebCall c)
            {
                if (c.Method != "POST" || c.Url != AppRegistrationMaker.PrincipalsUrl) return null;
                tries++;
                return tries < 3 ? new WebReply(400, "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"The appId does not reference a valid application object.\"}}") : new WebReply(201, "{\"id\":\"2c3d4e5f-6071-4b82-9ca3-b4c5d6e7f809\"}");
            });
            AppRegistrationMaker m2 = Maker(late, out pauses);
            T.Check(m2.Make("TrackVice").MadePrincipal, "make: tries again until the directory knows the app");
            T.Eq(3, tries, "make: three tries");
            T.Eq(2, pauses[0], "make: waits between tries");
        }

        static void Existing()
        {
            FakeWeb w = new FakeWeb();
            w.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, "{\"value\":[{\"id\":\"" + ObjectId + "\",\"appId\":\"" + AppId + "\",\"displayName\":\"TrackVice\"}]}");
            w.On("GET", AppRegistrationMaker.PrincipalsUrl + "?", 200, "{\"value\":[{\"id\":\"2c3d4e5f-6071-4b82-9ca3-b4c5d6e7f809\"}]}");
            int[] pauses;
            AppRegistrationMaker m = Maker(w, out pauses);
            AppRegistrationResult f = m.Find("TrackVice");
            T.Check(f != null && f.Existed && f.ClientId == AppId, "existing: found by name");
            AppRegistrationResult x = m.Make("TrackVice");
            T.Check(x.Existed && !x.MadePrincipal, "existing: kept as it is");
            int changes = 0;
            foreach (WebCall c in w.Calls) if (c.Method != "GET") changes++;
            T.Eq(0, changes, "existing: nothing is changed");
            T.Eq(AppId, x.ClientId, "existing: its Client ID");

            // An app whose enterprise application is missing gets one.
            FakeWeb half = new FakeWeb();
            half.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, "{\"value\":[{\"id\":\"" + ObjectId + "\",\"appId\":\"" + AppId + "\"}]}");
            half.On("GET", AppRegistrationMaker.PrincipalsUrl + "?", 200, "{\"value\":[]}");
            half.On("POST", AppRegistrationMaker.PrincipalsUrl, 201, "{\"id\":\"x\"}");
            AppRegistrationResult y = Maker(half, out pauses).Make("TrackVice");
            T.Check(y.Existed && y.MadePrincipal && half.Count("POST", AppRegistrationMaker.ApplicationsUrl) == 0, "existing: only the missing enterprise application is made");

            // A name with a quote is not read as part of the filter.
            FakeWeb q = new FakeWeb();
            q.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, "{\"value\":[]}");
            Maker(q, out pauses).Find("Contoso's app");
            T.Check(q.Calls[0].Url.Contains("Contoso%27%27s"), "existing: a quote is doubled: " + q.Calls[0].Url);
        }

        static void Errors()
        {
            int[] pauses;
            FakeWeb w = new FakeWeb();
            w.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 200, "{\"value\":[]}");
            w.On("POST", AppRegistrationMaker.ApplicationsUrl, 403, "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}");
            string m = Fail(delegate { Maker(w, out pauses).Make("TrackVice"); });
            T.Check(m.StartsWith("Access denied: the account may not make the app registration TrackVice.") && m.Contains("Application Developer") && m.Contains("let users register applications"), "errors: no right to make an app: " + m);
            T.Eq(0, w.Count("POST", AppRegistrationMaker.PrincipalsUrl), "errors: nothing more is made");

            FakeWeb sp = Tenantless();
            sp.Handlers.Insert(0, delegate (WebCall c) { return c.Method == "POST" && c.Url == AppRegistrationMaker.PrincipalsUrl ? new WebReply(403, "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges\"}}") : null; });
            m = Fail(delegate { Maker(sp, out pauses).Make("TrackVice"); });
            T.Check(m.Contains("is made, with the Client ID " + AppId) && m.Contains("Cloud Application Administrator") && m.EndsWith("Press Make again to finish it."), "errors: the app is made, its enterprise application is not: " + m);

            FakeWeb expired = new FakeWeb();
            expired.On("GET", AppRegistrationMaker.ApplicationsUrl + "?", 401, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"expired\"}}");
            m = Fail(delegate { Maker(expired, out pauses).Find("TrackVice"); });
            T.Check(m.StartsWith("The sign-in has expired"), "errors: an expired sign-in: " + m);

            FakeWeb other = Tenantless();
            other.Handlers.Insert(0, delegate (WebCall c) { return c.Method == "POST" && c.Url == AppRegistrationMaker.ApplicationsUrl ? new WebReply(400, "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"A policy of the tenant does not allow this name.\"}}") : null; });
            m = Fail(delegate { Maker(other, out pauses).Make("TrackVice"); });
            T.Check(m.StartsWith("Microsoft Graph could not make the app registration TrackVice.") && m.Contains("A policy of the tenant"), "errors: another error keeps Graph's words: " + m);

            // The sign-in that makes the app asks for one permission, and it is not one of the app's.
            T.Eq("https://graph.microsoft.com/Application.ReadWrite.All", AppRegistrationMaker.SignInScope, "errors: the sign-in scope");
            T.Eq(null, AppPermissions.Find(AppRegistrationMaker.SignInScope), "errors: the maker's permission is not given to the app");
        }

        // The texts of Settings and the guide agree with the code.
        static void Texts()
        {
            SettingDef perms = SettingsCatalog.Find("appreg.permissions");
            T.Eq(SettingsCatalog.Intune, perms.Page, "texts: the permissions are on Intune / cloud");
            foreach (AppPermission p in AppPermissions.All) T.Check(perms.Summary.Contains(p.Name), "texts: Settings lists " + p.Name);
            T.Check(perms.Summary.IndexOf("Only read", StringComparison.Ordinal) >= 0 && perms.Summary.IndexOf("write", StringComparison.Ordinal) > perms.Summary.IndexOf("Only read", StringComparison.Ordinal), "texts: the read ones come first");
            string needs = SettingsCatalog.Find("appreg.needs").Summary + SettingsCatalog.Find("appreg.needs").More;
            T.Check(needs.Contains("public client") && needs.Contains("Allow public client flows") && needs.Contains("No redirect URI is needed") && needs.Contains("Application Administrator") && needs.Contains("Grant admin consent"), "texts: what the app needs");
            T.Check(SettingsCatalog.Find("appreg").More.Contains("Conditional Access") && SettingsCatalog.Find("appreg").More.Contains("remove it"), "texts: why one's own");
            T.Check(SettingsCatalog.Search("public client").Count > 0 && SettingsCatalog.Search("app registration").Count >= 3, "texts: found by search");
            T.Check(SettingsCatalog.Find("clientid").Page == SettingsCatalog.Intune, "texts: next to the Client ID");
            if (!File.Exists("docs/AGENT.md")) return;
            string doc = File.ReadAllText("docs/AGENT.md");
            T.Check(doc.Contains("## App registration"), "docs: an App registration section");
            foreach (AppPermission p in AppPermissions.All) T.Check(doc.Contains(p.Name), "docs: lists " + p.Name);
            T.Check(doc.Contains("Allow public client flows") && doc.Contains("adminconsent"), "docs: the setting and the link");
        }
    }
}
