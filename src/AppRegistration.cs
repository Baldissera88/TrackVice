namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // One delegated Microsoft Graph permission that TrackVice asks for. Id is the permission's ID in Microsoft Graph.
    public sealed class AppPermission
    {
        public string Name, Id, Used;
        public bool Write;
    }

    // The delegated permissions in the code, for an app registration of the customer's own. The read ones are used when
    // you read Intune; the two that write are used only by Add customer and Customer code.
    public static class AppPermissions
    {
        public const string GraphAppId = "00000003-0000-0000-c000-000000000000";
        public const string GraphScope = "https://graph.microsoft.com/";

        public static readonly AppPermission[] All = new AppPermission[]
        {
            P("DeviceManagementManagedDevices.Read.All", "314874da-47d6-4978-88dc-cf0d37f0bb82", false, "Autopilot in Intune, and finding the device for its profiles"),
            P("DeviceManagementScripts.Read.All", "d32381d8-ee89-4220-9c83-b672aa68d404", false, "Names of platform scripts and remediations"),
            P("DeviceManagementApps.Read.All", "4edf5f54-4666-44af-9de9-0144fb4b6e8c", false, "Names of apps"),
            P("DeviceManagementConfiguration.Read.All", "f1493658-876a-4c87-8fa7-edb559b3476a", false, "Configuration profiles of a device"),
            P("DeviceManagementServiceConfig.Read.All", "8696daa5-bce5-4b2e-83f9-51b6defc4e1e", false, "The check before enrollment: Autopilot devices, profiles and Enrollment Status Pages"),
            P("Device.Read.All", "951183d1-1a61-466f-a6d1-1fde911bfd95", false, "The groups of a device, in the check before enrollment"),
            P("DeviceManagementScripts.ReadWrite.All", "8b9d79d0-ad75-4566-8619-f7500ecfcebe", true, "Add customer and Customer code: adds and assigns the platform script"),
            P("Group.ReadWrite.All", "4e46008b-f24c-477d-8fff-7bb4ec7aafe0", true, "Add customer: makes the Autopilot device group and searches groups"),
        };

        static AppPermission P(string name, string id, bool write, string used)
        {
            AppPermission p = new AppPermission();
            p.Name = name;
            p.Id = id;
            p.Write = write;
            p.Used = used;
            return p;
        }

        public static List<AppPermission> Reading()
        {
            List<AppPermission> l = new List<AppPermission>();
            foreach (AppPermission p in All) if (!p.Write) l.Add(p);
            return l;
        }

        public static List<AppPermission> Writing()
        {
            List<AppPermission> l = new List<AppPermission>();
            foreach (AppPermission p in All) if (p.Write) l.Add(p);
            return l;
        }

        public static AppPermission Find(string scopeOrName)
        {
            string n = scopeOrName ?? "";
            if (n.StartsWith(GraphScope, StringComparison.OrdinalIgnoreCase)) n = n.Substring(GraphScope.Length);
            foreach (AppPermission p in All) if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        public static string Names(List<AppPermission> l)
        {
            List<string> n = new List<string>();
            foreach (AppPermission p in l) n.Add(p.Name);
            return string.Join(", ", n.ToArray());
        }
    }

    public sealed class AppRegistrationResult
    {
        public string ObjectId, ClientId, Tenant, ConsentLink;
        // The app was there already, so nothing was made for it.
        public bool Existed;
        // The enterprise application (service principal) was made now.
        public bool MadePrincipal;
    }

    // Makes the app registration for TrackVice in the customer's tenant with Microsoft Graph: the application, then its
    // enterprise application. It makes nothing that can sign in on its own: no secret, no certificate, no consent.
    public sealed class AppRegistrationMaker
    {
        public const string DefaultName = "TrackVice";
        // Where the browser returns after the admin consent link. The sign-in with a code does not use it.
        public const string Redirect = "https://login.microsoftonline.com/common/oauth2/nativeclient";
        public const string ApplicationsUrl = "https://graph.microsoft.com/v1.0/applications";
        public const string PrincipalsUrl = "https://graph.microsoft.com/v1.0/servicePrincipals";
        public const string SignInScope = "https://graph.microsoft.com/Application.ReadWrite.All";

        readonly IWeb web;
        readonly Func<string> token;
        // Waits between tries while the new application spreads in the directory. Tests make it return at once.
        public Action<int> Pause = delegate (int ms) { System.Threading.Thread.Sleep(ms); };
        public int Tries = 6;

        public AppRegistrationMaker(IWeb web, Func<string> token)
        {
            this.web = web;
            this.token = token;
        }

        public static Dictionary<string, object> ApplicationBody(string name)
        {
            List<object> access = new List<object>();
            foreach (AppPermission p in AppPermissions.All) access.Add(SetupJson.Obj("id", p.Id, "type", "Scope"));
            return SetupJson.Obj(
                "displayName", name,
                "signInAudience", "AzureADMyOrg",
                "isFallbackPublicClient", true,
                "publicClient", SetupJson.Obj("redirectUris", SetupJson.List(Redirect)),
                "requiredResourceAccess", SetupJson.List(SetupJson.Obj("resourceAppId", AppPermissions.GraphAppId, "resourceAccess", access)));
        }

        public static Dictionary<string, object> PrincipalBody(string appId)
        {
            return SetupJson.Obj("appId", appId);
        }

        public static string ConsentLink(string tenant, string appId)
        {
            string t = string.IsNullOrEmpty(tenant) ? "organizations" : tenant;
            return "https://login.microsoftonline.com/" + Uri.EscapeDataString(t) + "/adminconsent?client_id=" + Uri.EscapeDataString(appId) + "&redirect_uri=" + Uri.EscapeDataString(Redirect);
        }

        // What Make will make, one line each, for the question before it starts.
        public static List<string> Plan(string name)
        {
            List<string> l = new List<string>();
            l.Add("An app registration named " + name + ", for accounts of this tenant only.");
            l.Add("A public client: no secret and no certificate. Allow public client flows is on, which the sign-in with a code needs.");
            l.Add("The redirect URI " + Redirect + ", where the browser returns after the consent link. The sign-in with a code does not use it.");
            l.Add("Delegated Microsoft Graph permissions that only read: " + AppPermissions.Names(AppPermissions.Reading()) + ".");
            l.Add("Delegated permissions that write, used only by Add customer: " + AppPermissions.Names(AppPermissions.Writing()) + ".");
            l.Add("The enterprise application of the app in this tenant.");
            return l;
        }

        WebReply Send(string method, string url, object body)
        {
            return web.Send(WebCall.Json(method, url, token(), body == null ? null : SetupJson.Write(body)));
        }

        static SetupException Fail(WebReply r, string doing, SetupErrors.GraphUse use)
        {
            Dictionary<string, object> e = ImeJson.Get(r.Json, "error") as Dictionary<string, object>;
            string msg = GraphClient.Clean(Http.Str(e, "message"));
            string code = Http.Str(e, "code");
            string known = r.Status == 401 ? null : SetupErrors.Graph(r.Status, code, msg, doing, use);
            if (known != null) return new SetupException(known, r.Status, code);
            string text = r.Status == 401 ? "The sign-in has expired. Sign in again." : "Microsoft Graph could not " + doing + ".";
            if (!string.IsNullOrEmpty(msg)) text += " (" + msg + ")";
            else if (r.Status >= 400 && r.Status != 401) text += " (HTTP " + r.Status + ")";
            return new SetupException(text, r.Status, code);
        }

        static string FilterUrl(string baseUrl, string filter, string select)
        {
            return baseUrl + "?$filter=" + Uri.EscapeDataString(filter) + "&$select=" + select;
        }

        static string Quote(string s)
        {
            return (s ?? "").Replace("'", "''");
        }

        // The first object of a filtered list, or null.
        Dictionary<string, object> First(string url, string doing, SetupErrors.GraphUse use)
        {
            WebReply r = Send("GET", url, null);
            if (r.Status != 200) throw Fail(r, doing, use);
            List<object> v = ImeJson.Get(r.Json, "value") as List<object>;
            if (v != null) foreach (object o in v) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) return d; }
            return null;
        }

        // The app with this name, when the tenant has one. Reads only.
        public AppRegistrationResult Find(string name)
        {
            Dictionary<string, object> d = First(FilterUrl(ApplicationsUrl, "displayName eq '" + Quote(name) + "'", "id,appId,displayName"), "read the app registrations", SetupErrors.GraphUse.AppRegistration);
            if (d == null) return null;
            AppRegistrationResult x = new AppRegistrationResult();
            x.ObjectId = Http.Str(d, "id");
            x.ClientId = Http.Str(d, "appId");
            x.Existed = true;
            x.Tenant = AddCustomerRun.TenantOf(token());
            x.ConsentLink = ConsentLink(x.Tenant, x.ClientId);
            return string.IsNullOrEmpty(x.ClientId) ? null : x;
        }

        // Makes the app and its enterprise application. An app of that name that exists is kept as it is.
        public AppRegistrationResult Make(string name)
        {
            AppRegistrationResult x = Find(name);
            if (x == null)
            {
                WebReply r = Send("POST", ApplicationsUrl, ApplicationBody(name));
                if (r.Status != 201 && r.Status != 200) throw Fail(r, "make the app registration " + name, SetupErrors.GraphUse.AppRegistration);
                x = new AppRegistrationResult();
                x.ObjectId = Http.Str(r.Json, "id");
                x.ClientId = Http.Str(r.Json, "appId");
                if (string.IsNullOrEmpty(x.ClientId)) throw new SetupException("Microsoft Graph did not return the Client ID of the new app.");
                x.Tenant = AddCustomerRun.TenantOf(token());
                x.ConsentLink = ConsentLink(x.Tenant, x.ClientId);
            }
            try { x.MadePrincipal = MakePrincipal(x.ClientId); }
            catch (SetupException ex)
            {
                throw new SetupException("The app registration " + name + " is made, with the Client ID " + x.ClientId + ", but its enterprise application is not. " + ex.Message + " Press Make again to finish it.", ex.Status, ex.Code);
            }
            return x;
        }

        // True when the enterprise application was made now, false when it was there.
        bool MakePrincipal(string appId)
        {
            if (First(FilterUrl(PrincipalsUrl, "appId eq '" + Quote(appId) + "'", "id"), "read the enterprise applications", SetupErrors.GraphUse.ServicePrincipal) != null) return false;
            WebReply r = null;
            // The directory can answer 400 or 404 for a minute after it made the application.
            for (int i = 0; i < Math.Max(1, Tries); i++)
            {
                r = Send("POST", PrincipalsUrl, PrincipalBody(appId));
                if (r.Status == 201 || r.Status == 200) return true;
                if (r.Status != 400 && r.Status != 404) break;
                if (i + 1 < Tries) Pause(3000);
            }
            throw Fail(r, "make the enterprise application", SetupErrors.GraphUse.ServicePrincipal);
        }
    }

    // Answers the requests of the app registration with made-up data, for the demo and screenshots.
    public sealed class DemoAppRegistrationWeb : IWeb
    {
        public const string Tenant = "6f1a2b3c-4d5e-4f60-8a71-92b3c4d5e6f7";
        public const string ClientId = "c4d5e6f7-1a2b-4c3d-9e8f-0a1b2c3d4e5f";
        readonly bool exists;
        public readonly List<WebCall> Calls = new List<WebCall>();

        public DemoAppRegistrationWeb(bool exists)
        {
            this.exists = exists;
        }

        static string Jwt()
        {
            string p = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"upn\":\"admin@northwind.onmicrosoft.com\",\"tid\":\"" + Tenant + "\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "eyJhbGciOiJub25lIn0." + p + ".demo";
        }

        public WebReply Send(WebCall c)
        {
            Calls.Add(c);
            string u = c.Url;
            if (u.Contains("/oauth2/v2.0/devicecode"))
                return new WebReply(200, "{\"device_code\":\"demo\",\"user_code\":\"CQ7D-4KPM\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"interval\":0,\"expires_in\":900}");
            if (u.Contains("/oauth2/v2.0/token"))
                return new WebReply(200, "{\"access_token\":\"" + Jwt() + "\",\"refresh_token\":\"demo\",\"expires_in\":3600}");
            if (u.StartsWith(AppRegistrationMaker.ApplicationsUrl + "?"))
                return new WebReply(200, exists ? "{\"value\":[{\"id\":\"1b2c3d4e-5f60-4a71-8b92-a3b4c5d6e7f8\",\"appId\":\"" + ClientId + "\",\"displayName\":\"TrackVice\"}]}" : "{\"value\":[]}");
            if (u == AppRegistrationMaker.ApplicationsUrl && c.Method == "POST")
                return new WebReply(201, "{\"id\":\"1b2c3d4e-5f60-4a71-8b92-a3b4c5d6e7f8\",\"appId\":\"" + ClientId + "\"}");
            if (u.StartsWith(AppRegistrationMaker.PrincipalsUrl + "?")) return new WebReply(200, "{\"value\":[]}");
            if (u == AppRegistrationMaker.PrincipalsUrl && c.Method == "POST") return new WebReply(201, "{\"id\":\"2c3d4e5f-6071-4b82-9ca3-b4c5d6e7f809\"}");
            return new WebReply(404, "{\"error\":{\"code\":\"NotFound\",\"message\":\"The demo has no answer for this.\"}}");
        }
    }
}
