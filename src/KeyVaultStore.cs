namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;

    // ---------- the vault address ----------

    // A vault that TrackVice may talk to: its address, the permission to ask for and the sign-in service of its cloud.
    public sealed class VaultTarget
    {
        public string BaseUrl, Host, Scope, LoginHost;

        public string Display
        {
            get { return Host; }
        }
    }

    public static class VaultAddress
    {
        public const string Api = "api-version=7.4";
        public const string RoleText = "Your account needs the role Key Vault Secrets User on this vault (Secrets Officer to add or change).";

        static readonly string[][] Clouds = new string[][]
        {
            new string[] { ".vault.azure.net", "https://vault.azure.net/user_impersonation", "login.microsoftonline.com" },
            new string[] { ".vault.azure.cn", "https://vault.azure.cn/user_impersonation", "login.partner.microsoftonline.cn" },
            new string[] { ".vault.usgovcloudapi.net", "https://vault.usgovcloudapi.net/user_impersonation", "login.microsoftonline.us" }
        };

        static readonly Regex RxLabel = new Regex("^[a-z0-9][a-z0-9-]{1,22}[a-z0-9]$", RegexOptions.Compiled);

        // A vault name, or an address that starts with https:// and ends in a Key Vault host. Null target and the reason
        // in problem when it is neither. Nothing else is ever called, so a typing mistake cannot send a token elsewhere.
        public static VaultTarget Parse(string input, out string problem)
        {
            problem = null;
            string s = (input ?? "").Trim();
            if (s.Length == 0) { problem = "Enter the name or address of the Key Vault."; return null; }
            string host;
            if (s.IndexOf('.') < 0 && s.IndexOf('/') < 0 && s.IndexOf(':') < 0) host = s.ToLowerInvariant() + Clouds[0][0];
            else
            {
                if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { problem = "The address must start with https://."; return null; }
                if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "https://" + s;
                Uri u;
                if (!Uri.TryCreate(s, UriKind.Absolute, out u) || !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    problem = "The address must start with https:// and end in .vault.azure.net.";
                    return null;
                }
                if (u.UserInfo.Length > 0 || (!u.IsDefaultPort && u.Port != 443)) { problem = "The address has a user name or a port. Use the address of the vault only."; return null; }
                host = u.Host.ToLowerInvariant();
            }
            foreach (string[] c in Clouds)
            {
                if (!host.EndsWith(c[0], StringComparison.Ordinal)) continue;
                string label = host.Substring(0, host.Length - c[0].Length);
                if (!RxLabel.IsMatch(label)) break;
                VaultTarget t = new VaultTarget();
                t.Host = host;
                t.BaseUrl = "https://" + host;
                t.Scope = c[1];
                t.LoginHost = c[2];
                return t;
            }
            problem = "This is not a Key Vault address. Use the name of the vault, or an address such as https://contoso-vault.vault.azure.net.";
            return null;
        }
    }

    // ---------- the calls ----------

    // The HTTP calls to the vault. The real one sends them; a test or the demo answers in its place.
    public interface IVaultHttp
    {
        WebReply Send(string method, string url, string token, string json);
    }

    public sealed class VaultHttp : IVaultHttp
    {
        readonly IWeb web;

        public VaultHttp(IWeb web)
        {
            this.web = web;
        }

        public WebReply Send(string method, string url, string token, string json)
        {
            return web.Send(WebCall.Json(method, url, token, json));
        }
    }

    public sealed class VaultException : Exception
    {
        public readonly int Status;

        public VaultException(string message, int status) : base(message)
        {
            Status = status;
        }
    }

    // Reads and writes the customer secrets of one vault. It never deletes a secret.
    public sealed class KeyVaultStore
    {
        readonly IVaultHttp http;
        readonly Func<string> token;
        readonly VaultTarget target;

        public KeyVaultStore(IVaultHttp http, Func<string> token, VaultTarget target)
        {
            this.http = http;
            this.token = token;
            this.target = target;
        }

        WebReply Call(string method, string url, string json, string doing)
        {
            WebReply r = http.Send(method, url, token(), json);
            if (r.Status >= 200 && r.Status < 300) return r;
            throw Fail(r, doing);
        }

        public static VaultException Fail(WebReply r, string doing)
        {
            if (r.Status == 401 || r.Status == 403)
            {
                string msg = RoleMessage(r);
                return new VaultException(msg, r.Status);
            }
            Dictionary<string, object> e = ImeJson.Get(r.Json, "error") as Dictionary<string, object>;
            string said = Http.Str(e, "message");
            string code = Http.Str(e, "code");
            string text = "Could not " + doing + ". The vault answered HTTP " + r.Status + (said != null ? ": " + Txt.Short(said, 200) : code != null ? ": " + code : ".");
            return new VaultException(text, r.Status);
        }

        // The vault's own firewall also answers 403, and then the role is not the problem.
        static string RoleMessage(WebReply r)
        {
            if (r.Text.IndexOf("ForbiddenByFirewall", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The firewall of the vault does not allow this PC. Add its address under Networking in the Azure portal.";
            return VaultAddress.RoleText;
        }

        string SecretUrl(string name)
        {
            return target.BaseUrl + "/secrets/" + Uri.EscapeDataString(name) + "?" + VaultAddress.Api;
        }

        // The customer secrets of the vault, without their values.
        public List<VaultSecret> List()
        {
            List<VaultSecret> all = new List<VaultSecret>();
            string url = target.BaseUrl + "/secrets?" + VaultAddress.Api;
            int pages = 0;
            while (url != null && pages++ < 200)
            {
                WebReply r = Call("GET", url, null, "list the secrets");
                Dictionary<string, object> d = r.Json;
                System.Collections.IList items = ImeJson.Get(d, "value") as System.Collections.IList;
                if (items != null)
                {
                    foreach (object o in items)
                    {
                        VaultSecret s = Read(o as Dictionary<string, object>);
                        if (s != null && VaultCustomers.IsOurs(s)) all.Add(s);
                    }
                }
                url = Next(Http.Str(d, "nextLink"));
            }
            return all;
        }

        // The next page must be on the same vault. A link to another host is not followed.
        string Next(string link)
        {
            if (string.IsNullOrEmpty(link)) return null;
            Uri u;
            if (!Uri.TryCreate(link, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps || !string.Equals(u.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                || !u.IsDefaultPort || u.UserInfo.Length > 0 || !link.StartsWith(target.BaseUrl + "/", StringComparison.OrdinalIgnoreCase))
                throw new VaultException("The vault gave a link to another address, so the list was stopped.", 0);
            return link;
        }

        static VaultSecret Read(Dictionary<string, object> o)
        {
            if (o == null) return null;
            VaultSecret s = new VaultSecret();
            string id = Http.Str(o, "id");
            if (string.IsNullOrEmpty(id)) return null;
            // The id is https://host/secrets/name, and ends in the version when it is the id of a version.
            string name = "";
            string[] parts = id.Split('/');
            for (int i = 0; i + 1 < parts.Length; i++) if (parts[i] == "secrets") { name = parts[i + 1]; break; }
            // The name goes into the address of the next call and into messages, so it must be a secret name.
            if (!VaultCustomers.IsSecretName(name)) return null;
            s.Name = name;
            s.ContentType = Http.Str(o, "contentType") ?? "";
            s.Value = Http.Str(o, "value") ?? "";
            Dictionary<string, object> a = ImeJson.Get(o, "attributes") as Dictionary<string, object>;
            s.Updated = VaultCustomers.FromEpoch(ImeJson.Get(a, "updated"));
            s.RealName = Http.Str(ImeJson.Get(o, "tags") as Dictionary<string, object>, "name") ?? "";
            return s;
        }

        // The secret with its value, or null when there is none of that name.
        public VaultSecret Get(string name)
        {
            WebReply r = http.Send("GET", SecretUrl(name), token(), null);
            if (r.Status == 404) return null;
            if (r.Status < 200 || r.Status > 299) throw Fail(r, "read the secret " + name);
            VaultSecret s = Read(r.Json);
            // An answer that is not this secret must not look like "no such secret": Save would then write over it without asking.
            if (s == null || !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new VaultException("The vault gave an answer for the secret " + name + " that could not be read.", 0);
            return s;
        }

        // Adds the secret, or adds a new version of it. Returns the time the vault gives it.
        public DateTime? Put(string name, string realName, string value)
        {
            Dictionary<string, object> body = SetupJson.Obj(
                "value", value,
                "contentType", VaultCustomers.ContentType,
                "tags", SetupJson.Obj("name", realName));
            WebReply r = Call("PUT", SecretUrl(name), SetupJson.Write(body), "save the secret " + name);
            VaultSecret s = Read(r.Json);
            return s == null ? null : s.Updated;
        }
    }

    // ---------- the sign-in ----------

    // A device code sign-in for the Key Vault permission user_impersonation. The refresh token can be kept by the caller,
    // encrypted for the Windows account, and used to sign in again without a code.
    public sealed class VaultSignIn
    {
        public const string Hint = "If the tenant blocks the sign-in, an administrator can give admin consent for Azure Key Vault user_impersonation.";

        readonly IWeb web;
        readonly VaultTarget target;
        public readonly string ClientId, Tenant;
        public string Account;
        string access, refresh;
        DateTime expires;
        // Called with each new refresh token.
        public Action<string> TokenChanged;

        public VaultSignIn(IWeb web, VaultTarget target, string clientId, string tenant)
        {
            this.web = web;
            this.target = target;
            ClientId = GraphClient.IsGuid(clientId) ? clientId.Trim().Trim('{', '}') : GraphClient.DefaultClientId;
            Tenant = string.IsNullOrEmpty(tenant) || tenant.Trim().Length == 0 ? "organizations" : tenant.Trim();
        }

        public bool SignedIn
        {
            get { return access != null || refresh != null; }
        }

        public string RefreshToken
        {
            get { return refresh; }
        }

        // A sign-in kept from an earlier start. The first call gets a fresh access token with it.
        public void Resume(string refreshToken, string account)
        {
            refresh = refreshToken;
            Account = account;
        }

        string Scopes
        {
            get { return target.Scope + " offline_access"; }
        }

        string Authority
        {
            get { return "https://" + target.LoginHost + "/" + Uri.EscapeDataString(Tenant) + "/oauth2/v2.0/"; }
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
            Dictionary<string, object> r = Post(Authority + "devicecode", "client_id=" + Uri.EscapeDataString(ClientId) + "&scope=" + Uri.EscapeDataString(Scopes));
            string err = Http.Str(r, "error_description") ?? Http.Str(r, "error");
            if (err != null) throw new SetupException(Explain(r));
            DeviceCode d = new DeviceCode();
            d.Code = Http.Str(r, "device_code");
            d.UserCode = Http.Str(r, "user_code");
            // The page is opened in the browser, so it must be a web page and nothing else that Windows can start.
            d.VerificationUri = WebLinks.HttpsOr(Http.Str(r, "verification_uri"), WebLinks.DeviceLogin);
            long? i = ImeJson.Long(ImeJson.Get(r, "interval"));
            long? e = ImeJson.Long(ImeJson.Get(r, "expires_in"));
            if (i.HasValue && i.Value >= 0) d.Interval = (int)Math.Min(i.Value, WebLinks.MaxInterval);
            if (e.HasValue && e.Value > 0) d.ExpiresIn = (int)Math.Min(e.Value, WebLinks.MaxExpires);
            if (string.IsNullOrEmpty(d.Code) || string.IsNullOrEmpty(d.UserCode)) throw new SetupException("The sign-in service did not return a code.");
            return d;
        }

        public void Wait(DeviceCode d, WaitHandle cancel)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(1, Math.Min(d.ExpiresIn, WebLinks.MaxExpires)));
            // A wait that the answer made huge or negative would never end or would throw.
            int interval = Math.Max(0, Math.Min(d.Interval, WebLinks.MaxInterval));
            while (DateTime.UtcNow < until)
            {
                if (cancel.WaitOne(TimeSpan.FromSeconds(interval))) throw new OperationCanceledException();
                Dictionary<string, object> r = Post(Authority + "token",
                    "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&client_id=" + Uri.EscapeDataString(ClientId) + "&device_code=" + Uri.EscapeDataString(d.Code));
                if (Take(r)) return;
                string err = Http.Str(r, "error");
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { interval = Math.Min(interval + 5, WebLinks.MaxInterval); continue; }
                if (err == "expired_token") throw new SetupException("The code expired. Start again.");
                if (err == "authorization_declined" || err == "access_denied") throw new SetupException("The sign-in was declined.");
                throw new SetupException(Explain(r));
            }
            throw new SetupException("The code expired. Start again.");
        }

        // The tenant's own words, with what an administrator can do about it.
        string Explain(Dictionary<string, object> r)
        {
            string error = Http.Str(r, "error"), text = Http.Str(r, "error_description");
            string known = SetupErrors.SignIn(error, text, SetupErrors.AppName(ClientId), "Azure Key Vault user_impersonation");
            if (known != null) return known + " " + Hint;
            return (text ?? error ?? "The sign-in failed.") + " " + Hint;
        }

        bool Take(Dictionary<string, object> r)
        {
            string t = Http.Str(r, "access_token");
            if (string.IsNullOrEmpty(t)) return false;
            access = t;
            string rt = Http.Str(r, "refresh_token");
            if (!string.IsNullOrEmpty(rt) && rt != refresh)
            {
                refresh = rt;
                Action<string> changed = TokenChanged;
                if (changed != null) { try { changed(rt); } catch (Exception) { } }
            }
            long? e = ImeJson.Long(ImeJson.Get(r, "expires_in"));
            expires = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(e ?? 3600, 86400)) - 120);
            if (Account == null) Account = WebLinks.Plain(WebSignIn.AccountOf(t), 120);
            return true;
        }

        // The current access token, renewed with the refresh token when it is about to expire.
        public string Token()
        {
            if (access != null && (DateTime.UtcNow < expires || refresh == null)) return access;
            if (refresh == null) throw new SetupException("Not signed in.");
            Dictionary<string, object> r = Post(Authority + "token",
                "grant_type=refresh_token&client_id=" + Uri.EscapeDataString(ClientId) + "&refresh_token=" + Uri.EscapeDataString(refresh) + "&scope=" + Uri.EscapeDataString(Scopes));
            if (!Take(r)) throw new SetupException("The sign-in has expired. Sign in again. " + Explain(r));
            return access;
        }
    }

    // ---------- what a sign-in answer may make TrackVice do ----------

    public static class WebLinks
    {
        public const string DeviceLogin = "https://microsoft.com/devicelogin";
        public const int MaxInterval = 60, MaxExpires = 3600;

        // True for an absolute https address without a user name, which is all that is ever opened in the browser.
        public static bool IsHttps(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || url.Length > 2048 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (char ch in url) if (ch <= ' ' || ch == '"' || ch == '\\' || ch > '~') return false;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.Host.Length > 0;
        }

        public static string HttpsOr(string url, string otherwise)
        {
            return IsHttps(url) ? url : otherwise;
        }

        // One line of text without control characters, for a name that comes from outside.
        public static string Plain(string s, int max)
        {
            if (s == null) return null;
            StringBuilder b = new StringBuilder();
            foreach (char ch in s)
            {
                if (b.Length >= max) break;
                if (!char.IsControl(ch)) b.Append(ch);
            }
            return b.ToString();
        }
    }

    // ---------- a vault in memory ----------

    // Answers like a Key Vault with RBAC, for tests and for the demo. Roles say what the signed-in account may do.
    public sealed class MemoryVault : IVaultHttp
    {
        public bool CanRead = true, CanWrite = true;
        // Secrets per name: value, content type, name tag and the time they changed.
        public readonly Dictionary<string, VaultSecret> Secrets = new Dictionary<string, VaultSecret>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new List<string>();
        public int PageSize = 25;
        public DateTime Now = DateTime.UtcNow;
        public string Host = "contoso-vault.vault.azure.net";

        public VaultSecret Add(string name, string realName, string value, DateTime updated)
        {
            VaultSecret s = new VaultSecret();
            s.Name = name;
            s.RealName = realName;
            s.Value = value;
            s.ContentType = VaultCustomers.ContentType;
            s.Updated = updated;
            Secrets[name] = s;
            return s;
        }

        static long Epoch(DateTime? t)
        {
            return (long)(t.Value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        Dictionary<string, object> One(VaultSecret s, bool withValue)
        {
            Dictionary<string, object> d = SetupJson.Obj(
                "id", "https://" + Host + "/secrets/" + s.Name + "/0123456789abcdef0123456789abcdef",
                "contentType", s.ContentType,
                "attributes", SetupJson.Obj("enabled", true, "updated", Epoch(s.Updated), "created", Epoch(s.Updated)),
                "tags", SetupJson.Obj("name", s.RealName));
            if (withValue) d["value"] = s.Value;
            return d;
        }

        public WebReply Send(string method, string url, string token, string json)
        {
            Calls.Add(method + " " + url);
            bool write = method == "PUT";
            if (token == null) return new WebReply(401, "{\"error\":{\"code\":\"Unauthorized\",\"message\":\"Missing token.\"}}");
            if (write ? !CanWrite : !CanRead) return new WebReply(403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Caller is not authorized to perform action on resource.\"}}");
            Uri u = new Uri(url);
            string path = u.AbsolutePath;
            if (path == "/secrets")
            {
                List<VaultSecret> all = new List<VaultSecret>(Secrets.Values);
                all.Sort(delegate (VaultSecret a, VaultSecret b) { return string.CompareOrdinal(a.Name, b.Name); });
                int skip = 0;
                string q = u.Query;
                int i = q.IndexOf("skip=", StringComparison.Ordinal);
                if (i >= 0) int.TryParse(q.Substring(i + 5).Split('&')[0], out skip);
                List<object> items = new List<object>();
                for (int k = skip; k < all.Count && k < skip + PageSize; k++) items.Add(One(all[k], false));
                Dictionary<string, object> page = SetupJson.Obj("value", items);
                page["nextLink"] = skip + PageSize < all.Count ? "https://" + Host + "/secrets?" + VaultAddress.Api + "&skip=" + (skip + PageSize) : null;
                return new WebReply(200, SetupJson.Write(page));
            }
            if (path.StartsWith("/secrets/", StringComparison.Ordinal))
            {
                string name = Uri.UnescapeDataString(path.Substring(9));
                VaultSecret have;
                if (method == "GET")
                {
                    if (!Secrets.TryGetValue(name, out have)) return new WebReply(404, "{\"error\":{\"code\":\"SecretNotFound\",\"message\":\"A secret with (name/id) " + name + " was not found in this key vault.\"}}");
                    return new WebReply(200, SetupJson.Write(One(have, true)));
                }
                if (method == "PUT")
                {
                    Dictionary<string, object> b = ImeJson.Parse(json) as Dictionary<string, object>;
                    VaultSecret s = Add(name, Http.Str(ImeJson.Get(b, "tags") as Dictionary<string, object>, "name") ?? "", Http.Str(b, "value") ?? "", Now);
                    s.ContentType = Http.Str(b, "contentType") ?? "";
                    return new WebReply(200, SetupJson.Write(One(s, true)));
                }
            }
            return new WebReply(404, "{\"error\":{\"code\":\"NotFound\",\"message\":\"Not found.\"}}");
        }
    }
}
