namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;

    // ---------- HTTP for the guided setup ----------

    public sealed class WebCall
    {
        public string Method = "GET";
        public string Url;
        public string ContentType;
        public byte[] Body;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static WebCall Json(string method, string url, string token, string json)
        {
            WebCall c = new WebCall();
            c.Method = method;
            c.Url = url;
            if (token != null) c.Headers["Authorization"] = "Bearer " + token;
            if (json != null)
            {
                c.ContentType = "application/json";
                c.Body = Encoding.UTF8.GetBytes(json);
            }
            return c;
        }

        public string Text
        {
            get { return Body == null ? "" : Encoding.UTF8.GetString(Body); }
        }
    }

    public sealed class WebReply
    {
        public int Status;
        public string Text = "";
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public WebReply() { }

        public WebReply(int status, string text)
        {
            Status = status;
            Text = text ?? "";
        }

        public Dictionary<string, object> Json
        {
            get { return Text.Length == 0 ? new Dictionary<string, object>() : ImeJson.Parse(Text.TrimStart('﻿')) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        }

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }
    }

    // Every request of the guided setup goes through this, so tests and the demo can answer in its place.
    public interface IWeb
    {
        WebReply Send(WebCall call);
    }

    public sealed class SetupException : Exception
    {
        public readonly int Status;
        public readonly string Code;

        public SetupException(string message) : base(message) { }

        public SetupException(string message, int status, string code) : base(message)
        {
            Status = status;
            Code = code;
        }
    }

    public sealed class HttpWeb : IWeb
    {
        public WebReply Send(WebCall call)
        {
            HttpWebRequest req = Http.Request(call.Url);
            req.Method = call.Method;
            req.Timeout = 300000;
            req.ReadWriteTimeout = 300000;
            foreach (KeyValuePair<string, string> h in call.Headers)
            {
                if (string.Equals(h.Key, "Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = h.Value;
                else req.Headers[h.Key] = h.Value;
            }
            if (call.Body != null)
            {
                req.ContentType = call.ContentType ?? "application/octet-stream";
                req.ContentLength = call.Body.Length;
                using (Stream s = req.GetRequestStream()) s.Write(call.Body, 0, call.Body.Length);
            }
            else if (call.Method == "PUT" || call.Method == "POST") req.ContentLength = 0;
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new SetupException("Could not reach " + req.RequestUri.Host + ": " + ex.Message);
            }
            using (resp)
            {
                WebReply r = new WebReply();
                r.Status = (int)resp.StatusCode;
                foreach (string k in resp.Headers.AllKeys) r.Headers[k] = resp.Headers[k];
                if (call.Method != "HEAD")
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) r.Text = sr.ReadToEnd();
                return r;
            }
        }
    }

    // Writes the small JSON bodies of the setup. Numbers, true, false, null, strings, lists and objects.
    public static class SetupJson
    {
        public static string Write(object v)
        {
            StringBuilder b = new StringBuilder();
            Write(b, v);
            return b.ToString();
        }

        static void Write(StringBuilder b, object v)
        {
            Dictionary<string, object> d = v as Dictionary<string, object>;
            System.Collections.IList l = v as System.Collections.IList;
            if (v == null) b.Append("null");
            else if (v is string) b.Append(AlertSender.Json((string)v));
            else if (v is bool) b.Append((bool)v ? "true" : "false");
            else if (d != null)
            {
                b.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) b.Append(',');
                    first = false;
                    b.Append(AlertSender.Json(kv.Key)).Append(':');
                    Write(b, kv.Value);
                }
                b.Append('}');
            }
            else if (l != null)
            {
                b.Append('[');
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) b.Append(',');
                    Write(b, l[i]);
                }
                b.Append(']');
            }
            else if (v is double) b.Append(((double)v).ToString("R", CultureInfo.InvariantCulture));
            else if (v is IFormattable) b.Append(((IFormattable)v).ToString(null, CultureInfo.InvariantCulture));
            else b.Append(AlertSender.Json(v.ToString()));
        }

        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        public static List<object> List(params object[] items)
        {
            return new List<object>(items);
        }
    }

    // ---------- sign-in ----------

    // A device code sign-in for one resource, such as Azure Resource Manager or Microsoft Graph.
    // The tokens stay in memory and are gone when the window closes.
    public sealed class WebSignIn
    {
        readonly IWeb web;
        public readonly string ClientId, Tenant, Scopes;
        public string Account;
        string access, refresh;
        DateTime expires;
        // A view takes its tokens from the sign-in it was made from. Scopes that were added later are kept here.
        readonly WebSignIn root;
        readonly Dictionary<string, ScopeToken> more = new Dictionary<string, ScopeToken>();

        sealed class ScopeToken
        {
            public string Access;
            public DateTime Expires;
            public string[] Set;
        }

        public WebSignIn(IWeb web, string clientId, string tenant, string scopes)
        {
            this.web = web;
            ClientId = GraphClient.IsGuid(clientId) ? clientId.Trim().Trim('{', '}') : GraphClient.DefaultClientId;
            Tenant = string.IsNullOrEmpty(tenant) || tenant.Trim().Length == 0 ? "organizations" : tenant.Trim();
            Scopes = scopes;
        }

        WebSignIn(WebSignIn from, string scopes)
        {
            web = from.web;
            ClientId = from.ClientId;
            Tenant = from.Tenant;
            Scopes = scopes;
            root = from;
            Account = from.Account;
        }

        public bool SignedIn
        {
            get { return root != null ? root.Has(Scopes) : access != null; }
        }

        public bool UsesWeb(IWeb w)
        {
            return ReferenceEquals(web, w);
        }

        static string[] SplitScopes(string scopes)
        {
            List<string> l = new List<string>();
            foreach (string x in (scopes ?? "").Split(' ')) if (x.Length > 0 && !l.Contains(x.ToLowerInvariant())) l.Add(x.ToLowerInvariant());
            return l.ToArray();
        }

        static bool Covers(string[] have, string[] want)
        {
            foreach (string w in want) if (Array.IndexOf(have, w) < 0) return false;
            return true;
        }

        // True when a token for all of these scopes is there.
        public bool Has(string scopes)
        {
            WebSignIn r = root ?? this;
            if (r.access == null) return false;
            string[] want = SplitScopes(scopes);
            if (Covers(SplitScopes(r.Scopes), want)) return true;
            lock (r.more) foreach (ScopeToken t in r.more.Values) if (Covers(t.Set, want)) return true;
            return false;
        }

        // The same sign-in for other scopes. It has a token when one is there, or after Quiet or a device code.
        public WebSignIn For(string scopes)
        {
            return new WebSignIn(root ?? this, scopes);
        }

        // Asks for a token for this view's scopes with the refresh token, without the person. False when a device code is needed.
        public bool Quiet()
        {
            if (SignedIn) return true;
            WebSignIn r = root ?? this;
            if (root == null || r.refresh == null) return false;
            try
            {
                Dictionary<string, object> a = Post(Authority + "token",
                    "grant_type=refresh_token&client_id=" + Uri.EscapeDataString(ClientId) + "&refresh_token=" + Uri.EscapeDataString(r.refresh) + "&scope=" + Uri.EscapeDataString(Scopes + " offline_access"));
                return Take(a);
            }
            catch (Exception) { return false; }
        }

        string Authority
        {
            get { return "https://login.microsoftonline.com/" + Uri.EscapeDataString(Tenant) + "/oauth2/v2.0/"; }
        }

        Dictionary<string, object> Post(string url, string form)
        {
            WebCall c = new WebCall();
            c.Method = "POST";
            c.Url = url;
            c.ContentType = "application/x-www-form-urlencoded";
            c.Body = Encoding.UTF8.GetBytes(form);
            return web.Send(c).Json;
        }

        public DeviceCode Start()
        {
            Dictionary<string, object> r = Post(Authority + "devicecode", "client_id=" + Uri.EscapeDataString(ClientId) + "&scope=" + Uri.EscapeDataString(Scopes + " offline_access"));
            string err = Http.Str(r, "error_description") ?? Http.Str(r, "error");
            if (err != null) throw new SetupException(Explain(r) ?? err);
            DeviceCode d = new DeviceCode();
            d.Code = Http.Str(r, "device_code");
            d.UserCode = Http.Str(r, "user_code");
            d.VerificationUri = Http.Str(r, "verification_uri") ?? "https://microsoft.com/devicelogin";
            long? i = ImeJson.Long(ImeJson.Get(r, "interval"));
            long? e = ImeJson.Long(ImeJson.Get(r, "expires_in"));
            if (i.HasValue && i.Value >= 0) d.Interval = (int)i.Value;
            if (e.HasValue && e.Value > 0) d.ExpiresIn = (int)e.Value;
            if (string.IsNullOrEmpty(d.Code) || string.IsNullOrEmpty(d.UserCode)) throw new SetupException("The sign-in service did not return a code.");
            return d;
        }

        public void Wait(DeviceCode d, WaitHandle cancel)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(d.ExpiresIn);
            int interval = d.Interval;
            while (DateTime.UtcNow < until)
            {
                if (cancel.WaitOne(TimeSpan.FromSeconds(interval))) throw new OperationCanceledException();
                Dictionary<string, object> r = Post(Authority + "token",
                    "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&client_id=" + Uri.EscapeDataString(ClientId) + "&device_code=" + Uri.EscapeDataString(d.Code));
                if (Take(r)) return;
                string err = Http.Str(r, "error");
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { interval += 5; continue; }
                if (err == "expired_token") throw new SetupException("The code expired. Start again.");
                string known = Explain(r);
                if (known != null) throw new SetupException(known);
                if (err == "authorization_declined" || err == "access_denied") throw new SetupException("The sign-in was declined.");
                throw new SetupException(Http.Str(r, "error_description") ?? err ?? "The sign-in failed.");
            }
            throw new SetupException("The code expired. Start again.");
        }

        // Says what a refusal of the tenant or the app means, or null for any other error.
        string Explain(Dictionary<string, object> r)
        {
            return SetupErrors.SignIn(Http.Str(r, "error"), Http.Str(r, "error_description"), SetupErrors.AppName(ClientId), SetupErrors.Permissions(Scopes));
        }

        bool Take(Dictionary<string, object> r)
        {
            string token = Http.Str(r, "access_token");
            if (string.IsNullOrEmpty(token)) return false;
            if (root != null)
            {
                root.Put(Scopes, token, Http.Str(r, "refresh_token"), ImeJson.Long(ImeJson.Get(r, "expires_in")));
                if (Account == null) Account = root.Account;
                return true;
            }
            access = token;
            string rt = Http.Str(r, "refresh_token");
            if (!string.IsNullOrEmpty(rt)) refresh = rt;
            long? e = ImeJson.Long(ImeJson.Get(r, "expires_in"));
            expires = DateTime.UtcNow.AddSeconds((e ?? 3600) - 120);
            if (Account == null) Account = AccountOf(token);
            return true;
        }

        void Put(string scopes, string token, string rt, long? e)
        {
            if (!string.IsNullOrEmpty(rt)) refresh = rt;
            ScopeToken t = new ScopeToken();
            t.Access = token;
            t.Set = SplitScopes(scopes);
            t.Expires = DateTime.UtcNow.AddSeconds((e ?? 3600) - 120);
            lock (more) more[string.Join(" ", t.Set)] = t;
            if (Account == null) Account = AccountOf(token);
        }

        // The token of a view: the first sign-in's token, or the one that was added for its scopes.
        string TokenFor(string scopes)
        {
            string[] want = SplitScopes(scopes);
            if (access != null && Covers(SplitScopes(Scopes), want)) return Token();
            ScopeToken t = null;
            lock (more) foreach (ScopeToken x in more.Values) if (Covers(x.Set, want)) { t = x; break; }
            if (t == null) throw new SetupException("Not signed in.");
            if (DateTime.UtcNow < t.Expires || refresh == null) return t.Access;
            Dictionary<string, object> r = Post(Authority + "token",
                "grant_type=refresh_token&client_id=" + Uri.EscapeDataString(ClientId) + "&refresh_token=" + Uri.EscapeDataString(refresh) + "&scope=" + Uri.EscapeDataString(scopes + " offline_access"));
            string token = Http.Str(r, "access_token");
            if (string.IsNullOrEmpty(token)) throw new SetupException("The sign-in has expired. Sign in again.");
            Put(scopes, token, Http.Str(r, "refresh_token"), ImeJson.Long(ImeJson.Get(r, "expires_in")));
            return token;
        }

        // The current access token, renewed with the refresh token when it is about to expire.
        public string Token()
        {
            if (root != null) return root.TokenFor(Scopes);
            if (access == null) throw new SetupException("Not signed in.");
            if (DateTime.UtcNow < expires || refresh == null) return access;
            Dictionary<string, object> r = Post(Authority + "token",
                "grant_type=refresh_token&client_id=" + Uri.EscapeDataString(ClientId) + "&refresh_token=" + Uri.EscapeDataString(refresh) + "&scope=" + Uri.EscapeDataString(Scopes + " offline_access"));
            if (!Take(r)) throw new SetupException("The sign-in has expired. Sign in again.");
            return access;
        }

        // The account name in the token, for the window. The token is not checked here; the service checks it.
        public static string AccountOf(string jwt)
        {
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                string p = parts[1].Replace('-', '+').Replace('_', '/');
                while (p.Length % 4 != 0) p += "=";
                Dictionary<string, object> c = ImeJson.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p))) as Dictionary<string, object>;
                return Http.Str(c, "upn") ?? Http.Str(c, "unique_name") ?? Http.Str(c, "preferred_username");
            }
            catch (Exception) { return null; }
        }
    }

    // ---------- Azure Resource Manager ----------

    public sealed class AzureSubscription
    {
        public string Id, Name, Tenant;

        // Two subscriptions can have the same name, so the start of the ID is shown too.
        public override string ToString() { return (Id ?? "").Length >= 8 ? Name + "  (" + Id.Substring(0, 8) + ")" : Name; }
    }

    public sealed class AzureAccount
    {
        public string Id, Name, Location, ResourceGroup, Kind, BlobEndpoint;
        // The account does not allow shared key access, so a link cannot be signed with its key.
        public bool SharedKeyOff;

        public override string ToString() { return Name + "  (" + ResourceGroup + ", " + Location + ")"; }
    }

    // Creates the storage for the agents with the Azure Resource Manager REST API.
    public sealed class Arm
    {
        public const string Base = "https://management.azure.com";
        public const string Scope = "https://management.azure.com/user_impersonation";
        // Azure PowerShell's public client. Microsoft Graph Command Line Tools is not set up for Azure Resource Manager.
        public const string ClientId = "1950a258-227b-4e31-a9cf-717495945fc2";
        public const string StorageApi = "2023-05-01";
        public const string SubscriptionApi = "2022-12-01";
        public const string GroupApi = "2021-04-01";
        public const string Sessions = "trackvice";
        public const string AgentFiles = "trackvice-agent";
        public const string RuleName = "trackvice-cleanup";
        public const string DefaultRegion = "westeurope";
        public static readonly string[] Regions = { "westeurope", "northeurope", "swedencentral", "norwayeast", "germanywestcentral", "francecentral", "uksouth", "switzerlandnorth", "eastus", "eastus2", "centralus", "westus2", "canadacentral", "australiaeast", "southeastasia", "japaneast" };

        readonly IWeb web;
        readonly Func<string> token;
        // Waits between reads of a long-running operation. Tests make it return at once.
        public Action<int> Pause = delegate (int ms) { Thread.Sleep(ms); };
        public int MaxPolls = 90;

        public Arm(IWeb web, Func<string> token)
        {
            this.web = web;
            this.token = token;
        }

        WebReply Call(string method, string path, object body)
        {
            string url = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : Base + path;
            return web.Send(WebCall.Json(method, url, token(), body == null ? null : SetupJson.Write(body)));
        }

        // Any other request to Azure Resource Manager, with the sign-in of this instance.
        public WebReply Send(string method, string path, object body)
        {
            return Call(method, path, body);
        }

        static SetupException Fail(WebReply r, string doing)
        {
            Dictionary<string, object> e = ImeJson.Get(r.Json, "error") as Dictionary<string, object>;
            string code = Http.Str(e, "code") ?? "";
            string msg = Http.Str(e, "message");
            string text;
            string known = r.Status == 401 && code != "InvalidAuthenticationTokenTenant" ? null : SetupErrors.Azure(r.Status, code, msg, doing);
            if (known != null) return new SetupException(known, r.Status, code);
            if (r.Status == 401) text = "The Azure sign-in has expired or is not valid. Sign in again.";
            else text = "Azure could not " + doing + ".";
            if (!string.IsNullOrEmpty(msg)) text += " " + Txt.Short(msg.Trim(), 300);
            else if (r.Status >= 400) text += " (HTTP " + r.Status + ")";
            return new SetupException(text, r.Status, code);
        }

        static List<Dictionary<string, object>> Values(Dictionary<string, object> r)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            List<object> v = ImeJson.Get(r, "value") as List<object>;
            if (v != null) foreach (object o in v) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) l.Add(d); }
            return l;
        }

        List<Dictionary<string, object>> All(string path, string doing)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            string url = path;
            for (int page = 0; page < 20 && !string.IsNullOrEmpty(url); page++)
            {
                WebReply r = Call("GET", url, null);
                if (r.Status != 200) throw Fail(r, doing);
                Dictionary<string, object> j = r.Json;
                l.AddRange(Values(j));
                url = Http.Str(j, "nextLink");
            }
            return l;
        }

        public List<AzureSubscription> Subscriptions()
        {
            List<AzureSubscription> l = new List<AzureSubscription>();
            foreach (Dictionary<string, object> d in All("/subscriptions?api-version=" + SubscriptionApi, "list the subscriptions"))
            {
                if (!string.Equals(Http.Str(d, "state"), "Enabled", StringComparison.OrdinalIgnoreCase)) continue;
                AzureSubscription s = new AzureSubscription();
                s.Id = Http.Str(d, "subscriptionId");
                s.Name = Http.Str(d, "displayName") ?? s.Id;
                s.Tenant = Http.Str(d, "tenantId");
                if (!string.IsNullOrEmpty(s.Id)) l.Add(s);
            }
            l.Sort(delegate (AzureSubscription a, AzureSubscription b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return l;
        }

        // Names of the resource groups with their regions.
        public List<KeyValuePair<string, string>> ResourceGroups(string sub)
        {
            List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
            foreach (Dictionary<string, object> d in All("/subscriptions/" + Esc(sub) + "/resourcegroups?api-version=" + GroupApi, "list the resource groups"))
            {
                string n = Http.Str(d, "name");
                if (!string.IsNullOrEmpty(n)) l.Add(new KeyValuePair<string, string>(n, Http.Str(d, "location") ?? ""));
            }
            l.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase); });
            return l;
        }

        public void CreateResourceGroup(string sub, string name, string location)
        {
            WebReply r = Call("PUT", "/subscriptions/" + Esc(sub) + "/resourcegroups/" + Esc(name) + "?api-version=" + GroupApi, SetupJson.Obj("location", location));
            if (r.Status == 200 || r.Status == 201) return;
            SetupException ex = Fail(r, "create the resource group " + name);
            if (r.Status == 403) throw new SetupException(ex.Message + " Pick an existing resource group instead.", 403, ex.Code);
            throw ex;
        }

        public List<AzureAccount> StorageAccounts(string sub)
        {
            List<AzureAccount> l = new List<AzureAccount>();
            foreach (Dictionary<string, object> d in All("/subscriptions/" + Esc(sub) + "/providers/Microsoft.Storage/storageAccounts?api-version=" + StorageApi, "list the storage accounts"))
                l.Add(ToAccount(d));
            l.Sort(delegate (AzureAccount a, AzureAccount b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return l;
        }

        static AzureAccount ToAccount(Dictionary<string, object> d)
        {
            AzureAccount a = new AzureAccount();
            a.Id = Http.Str(d, "id") ?? "";
            a.Name = Http.Str(d, "name") ?? "";
            a.Location = Http.Str(d, "location") ?? "";
            a.Kind = Http.Str(d, "kind") ?? "";
            Match m = Regex.Match(a.Id, "/resourceGroups/([^/]+)/", RegexOptions.IgnoreCase);
            a.ResourceGroup = m.Success ? m.Groups[1].Value : "";
            Dictionary<string, object> p = ImeJson.Get(d, "properties") as Dictionary<string, object>;
            Dictionary<string, object> ep = ImeJson.Get(p, "primaryEndpoints") as Dictionary<string, object>;
            a.BlobEndpoint = Http.Str(ep, "blob");
            a.SharedKeyOff = ImeJson.Get(p, "allowSharedKeyAccess") is bool && !(bool)ImeJson.Get(p, "allowSharedKeyAccess");
            if (string.IsNullOrEmpty(a.BlobEndpoint)) a.BlobEndpoint = "https://" + a.Name + ".blob.core.windows.net/";
            if (!a.BlobEndpoint.EndsWith("/")) a.BlobEndpoint += "/";
            return a;
        }

        public static string AccountId(string sub, string group, string name)
        {
            return "/subscriptions/" + Esc(sub) + "/resourceGroups/" + Esc(group) + "/providers/Microsoft.Storage/storageAccounts/" + Esc(name);
        }

        // Null when the name is allowed for a storage account, otherwise why not.
        public static string NameRule(string name)
        {
            string n = name ?? "";
            if (n.Length < 3 || n.Length > 24) return "A storage account name has 3 to 24 characters.";
            if (!Regex.IsMatch(n, "^[a-z0-9]+$")) return "A storage account name has only lowercase letters and digits.";
            return null;
        }

        // A name made from the customer: contosotrackvice, with a number when it has to differ.
        public static string Suggest(string customer, int n)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in (customer ?? "").ToLowerInvariant()) if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) b.Append(c);
            string tail = "trackvice" + (n > 0 ? n.ToString(CultureInfo.InvariantCulture) : "");
            string head = b.ToString();
            if (head.Length + tail.Length > 24) head = head.Substring(0, 24 - tail.Length);
            if (head.Length == 0) head = "agents";
            return head + tail;
        }

        // Null when the name can be used for a new account, otherwise why not.
        public string NameProblem(string sub, string name)
        {
            string rule = NameRule(name);
            if (rule != null) return rule;
            WebReply r = Call("POST", "/subscriptions/" + Esc(sub) + "/providers/Microsoft.Storage/checkNameAvailability?api-version=" + StorageApi,
                SetupJson.Obj("name", name, "type", "Microsoft.Storage/storageAccounts"));
            if (r.Status != 200) throw Fail(r, "check the storage account name");
            Dictionary<string, object> j = r.Json;
            if (ImeJson.Get(j, "nameAvailable") is bool && (bool)ImeJson.Get(j, "nameAvailable")) return null;
            string reason = Http.Str(j, "reason");
            if (reason == "AlreadyExists") return "The name " + name + " is taken. Storage account names are unique in all of Azure.";
            return Http.Str(j, "message") ?? "The name " + name + " cannot be used.";
        }

        // The body of a new account: StorageV2, Standard_LRS, HTTPS only, TLS 1.2, no anonymous blob access, account key on.
        public static Dictionary<string, object> AccountBody(string location)
        {
            return SetupJson.Obj(
                "kind", "StorageV2",
                "location", location,
                "sku", SetupJson.Obj("name", "Standard_LRS"),
                "properties", SetupJson.Obj(
                    "supportsHttpsTrafficOnly", true,
                    "minimumTlsVersion", "TLS1_2",
                    "allowBlobPublicAccess", false,
                    "allowSharedKeyAccess", true,
                    "accessTier", "Hot"),
                "tags", SetupJson.Obj("createdBy", "TrackVice"));
        }

        // Creating an account takes up to a minute: Azure answers 202 and a Location to read until it is done.
        public AzureAccount CreateAccount(string sub, string group, string name, string location)
        {
            string id = AccountId(sub, group, name);
            WebReply r = Call("PUT", id + "?api-version=" + StorageApi, AccountBody(location));
            if (r.Status == 200) return ToAccount(r.Json);
            if (r.Status != 202) throw Fail(r, "create the storage account " + name);
            string poll = r.Header("Location") ?? r.Header("Azure-AsyncOperation");
            for (int i = 0; i < MaxPolls && !string.IsNullOrEmpty(poll); i++)
            {
                int wait = 3;
                string ra = r.Header("Retry-After");
                int sec;
                if (ra != null && int.TryParse(ra, NumberStyles.Integer, CultureInfo.InvariantCulture, out sec) && sec > 0 && sec < 60) wait = sec;
                Pause(wait * 1000);
                r = Call("GET", poll, null);
                if (r.Status == 202) continue;
                if (r.Status == 200)
                {
                    string state = Http.Str(r.Json, "status");
                    if (state == null) return ToAccount(r.Json);
                    if (state == "InProgress") continue;
                    if (state != "Succeeded") throw Fail(r, "create the storage account " + name);
                    break;
                }
                throw Fail(r, "create the storage account " + name);
            }
            r = Call("GET", id + "?api-version=" + StorageApi, null);
            if (r.Status != 200) throw Fail(r, "read the new storage account " + name);
            Dictionary<string, object> p = ImeJson.Get(r.Json, "properties") as Dictionary<string, object>;
            string ps = Http.Str(p, "provisioningState");
            if (ps != null && ps != "Succeeded") throw new SetupException("The storage account " + name + " is not ready yet (" + ps + "). Try again in a minute.");
            return ToAccount(r.Json);
        }

        // Private containers. A container that is there already is left as it is.
        public bool CreateContainer(AzureAccount a, string name)
        {
            WebReply r = Call("PUT", a.Id + "/blobServices/default/containers/" + Esc(name) + "?api-version=" + StorageApi, SetupJson.Obj("properties", SetupJson.Obj("publicAccess", "None")));
            if (r.Status == 200 || r.Status == 201) return true;
            if (r.Status == 409) return false;
            throw Fail(r, "create the container " + name);
        }

        // True when the container is there, false when it is not. Reads only.
        public bool ContainerExists(AzureAccount a, string name)
        {
            WebReply r = Call("GET", a.Id + "/blobServices/default/containers/" + Esc(name) + "?api-version=" + StorageApi, null);
            if (r.Status == 200) return true;
            if (r.Status == 404) return false;
            throw Fail(r, "read the container " + name);
        }

        public static Dictionary<string, object> CleanupRule(int days)
        {
            return SetupJson.Obj(
                "enabled", true,
                "name", RuleName,
                "type", "Lifecycle",
                "definition", SetupJson.Obj(
                    "actions", SetupJson.Obj("baseBlob", SetupJson.Obj("delete", SetupJson.Obj("daysAfterModificationGreaterThan", days))),
                    "filters", SetupJson.Obj(
                        "blobTypes", SetupJson.List("blockBlob"),
                        "prefixMatch", SetupJson.List(Sessions + "/sessions/", Sessions + "/logs/"))));
        }

        // Adds the rule that deletes old sessions and logs. Rules the account has already are kept; one with the same name is replaced.
        public void SetCleanup(AzureAccount a, int days)
        {
            string url = a.Id + "/managementPolicies/default?api-version=" + StorageApi;
            List<object> rules = new List<object>();
            WebReply r = Call("GET", url, null);
            if (r.Status == 200)
            {
                Dictionary<string, object> p = ImeJson.Get(r.Json, "properties") as Dictionary<string, object>;
                Dictionary<string, object> pol = ImeJson.Get(p, "policy") as Dictionary<string, object>;
                List<object> have = ImeJson.Get(pol, "rules") as List<object>;
                if (have != null)
                    foreach (object o in have)
                        if (!string.Equals(Http.Str(o as Dictionary<string, object>, "name"), RuleName, StringComparison.OrdinalIgnoreCase)) rules.Add(o);
            }
            else if (r.Status != 404) throw Fail(r, "read the lifecycle rules");
            rules.Add(CleanupRule(days));
            r = Call("PUT", url, SetupJson.Obj("properties", SetupJson.Obj("policy", SetupJson.Obj("rules", rules))));
            if (r.Status != 200 && r.Status != 201) throw Fail(r, "add the lifecycle rule");
        }

        // The account key, only to sign the links. It is never shown or saved.
        public string Key(AzureAccount a)
        {
            WebReply r = Call("POST", a.Id + "/listKeys?api-version=" + StorageApi, null);
            if (r.Status != 200) throw Fail(r, "read the account key of " + a.Name);
            List<object> keys = ImeJson.Get(r.Json, "keys") as List<object>;
            if (keys != null)
                foreach (object o in keys)
                {
                    Dictionary<string, object> k = o as Dictionary<string, object>;
                    string v = Http.Str(k, "value");
                    string perm = Http.Str(k, "permissions") ?? "Full";
                    if (!string.IsNullOrEmpty(v) && string.Equals(perm, "Full", StringComparison.OrdinalIgnoreCase)) return v;
                }
            throw new SetupException("The storage account " + a.Name + " has no account key. Turn on Allow storage account key access.");
        }

        static string Esc(string s)
        {
            return Uri.EscapeDataString(s ?? "");
        }
    }

    // ---------- links ----------

    // Service SAS links signed with the account key, as described in "Create a service SAS" for version 2020-12-06 and later.
    public static class SasLinks
    {
        public const string Version = "2022-11-02";

        public static string Time(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        // The fields in the documented order. A service SAS made with an account key has no user delegation fields.
        public static string StringToSign(string perms, string start, string expiry, string resource, string protocol, string version, string kind)
        {
            return string.Join("\n", new string[] {
                perms, start ?? "", expiry, resource, "", "", protocol ?? "", version, kind, "", "", "", "", "", "", "" });
        }

        public static string Sign(string stringToSign, string base64Key)
        {
            using (HMACSHA256 h = new HMACSHA256(Convert.FromBase64String(base64Key)))
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        }

        static string Query(string perms, string st, string se, string kind, string sig)
        {
            return "sp=" + perms + (st != null ? "&st=" + Uri.EscapeDataString(st) : "") + "&se=" + Uri.EscapeDataString(se) + "&spr=https&sv=" + Version + "&sr=" + kind + "&sig=" + Uri.EscapeDataString(sig);
        }

        // perms in the documented order for a container: racwdl.
        public static string Container(string blobEndpoint, string account, string container, string perms, DateTime startUtc, DateTime expiryUtc, string key)
        {
            string st = Time(startUtc), se = Time(expiryUtc);
            string sig = Sign(StringToSign(perms, st, se, "/blob/" + account + "/" + container, "https", Version, "c"), key);
            return blobEndpoint.TrimEnd('/') + "/" + container + "?" + Query(perms, st, se, "c", sig);
        }

        public static string Blob(string blobEndpoint, string account, string container, string blob, string perms, DateTime startUtc, DateTime expiryUtc, string key)
        {
            string st = Time(startUtc), se = Time(expiryUtc);
            string sig = Sign(StringToSign(perms, st, se, "/blob/" + account + "/" + container + "/" + blob, "https", Version, "b"), key);
            return blobEndpoint.TrimEnd('/') + "/" + container + "/" + Uri.EscapeDataString(blob) + "?" + Query(perms, st, se, "b", sig);
        }

        // The link with its signature hidden, for the window.
        public static string Masked(string link)
        {
            if (string.IsNullOrEmpty(link)) return "";
            return Regex.Replace(link, "([?&]sig=)[^&]*", "$1" + Secrets.Hidden);
        }
    }

    // The links the agent and the viewer need. The account key they were signed with is not kept.
    public sealed class AgentLinks
    {
        public string Upload, Viewer, Download, UpdateRead, Publish;
        public DateTime ExpiresUtc;

        public static AgentLinks Make(AzureAccount a, string key, DateTime nowUtc, int days)
        {
            // A few minutes back, so a device whose clock is slightly behind can use the links at once.
            DateTime st = nowUtc.AddMinutes(-15), se = nowUtc.AddDays(days);
            AgentLinks l = new AgentLinks();
            l.ExpiresUtc = se;
            l.Upload = SasLinks.Container(a.BlobEndpoint, a.Name, Arm.Sessions, "cw", st, se, key);
            l.Viewer = SasLinks.Container(a.BlobEndpoint, a.Name, Arm.Sessions, "rl", st, se, key);
            l.Download = SasLinks.Blob(a.BlobEndpoint, a.Name, Arm.AgentFiles, "TrackVice.exe", "r", st, se, key);
            l.UpdateRead = SasLinks.Container(a.BlobEndpoint, a.Name, Arm.AgentFiles, "r", st, se, key);
            l.Publish = SasLinks.Container(a.BlobEndpoint, a.Name, Arm.AgentFiles, "cw", st, se, key);
            return l;
        }
    }

    // Puts a file in a container with a container SAS link that has Create and Write.
    public static class BlobUpload
    {
        public static void Put(IWeb web, string containerLink, string name, byte[] data)
        {
            int q = containerLink.IndexOf('?');
            if (q < 0) throw new SetupException("The link has no access token.");
            WebCall c = new WebCall();
            c.Method = "PUT";
            c.Url = containerLink.Substring(0, q).TrimEnd('/') + "/" + Uri.EscapeDataString(name) + containerLink.Substring(q);
            c.Headers["x-ms-blob-type"] = "BlockBlob";
            c.ContentType = "application/octet-stream";
            c.Body = data;
            WebReply r = web.Send(c);
            if (r.Status != 201 && r.Status != 200)
                throw new SetupException("The upload of " + name + " failed (HTTP " + r.Status + (r.Header("x-ms-error-code") != null ? ", " + r.Header("x-ms-error-code") : "") + ").", r.Status, r.Header("x-ms-error-code"));
        }

        // True when the link can read the blob, as a device will.
        public static bool CanRead(IWeb web, string blobLink)
        {
            WebCall c = new WebCall();
            c.Method = "HEAD";
            c.Url = blobLink;
            return web.Send(c).Status == 200;
        }
    }
}
