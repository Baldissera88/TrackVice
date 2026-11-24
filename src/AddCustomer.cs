namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Security.Cryptography;
    using System.Text;

    // Small secrets kept on this PC. The window keeps them encrypted for the Windows user with DPAPI.
    public interface ISecretStore
    {
        // Null or empty when nothing is kept.
        string Load(string name);
        // Null removes it.
        void Save(string name, string value);
    }

    public sealed class MemorySecrets : ISecretStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Load(string name)
        {
            string v;
            return Values.TryGetValue(name, out v) ? v : null;
        }

        public void Save(string name, string value)
        {
            if (value == null) Values.Remove(name);
            else Values[name] = value;
        }
    }

    // ---------- your storage ----------

    // The one storage account in the administrator's own Azure: TrackVice versions in trackvice-agent, and a container
    // per customer for the uploads. The account key is kept to make the customers' containers and links.
    public sealed class OwnStorage
    {
        public const string SecretName = "YourStorage";
        // The agent links are valid for 5 years.
        public const int AgentLinkDays = 1826;

        public string Account = "", BlobEndpoint = "", Key = "", Subscription = "";
        // Read on trackvice-agent: goes into every customer code.
        public string AgentRead = "";
        // Create and Write on trackvice-agent: publishes new versions.
        public string AgentWrite = "";
        public DateTime ExpiresUtc;
        public string PublishedVersion = "";
        // The resource ID, region and tenant in Azure, when known. Older setups have none until Check security finds them.
        public string Id = "", Region = "", Tenant = "";
        // Do not keep the account key: Add customer, Publish and the customer's links sign in to Azure to read it.
        public bool AskKey;
        // Uploads are deleted this many days after they last changed. RuleDays is what the rule in Azure was last seen
        // to do, 0 when TrackVice has not seen it.
        public int RetentionDays = StorageSecurity.DefaultDays, RuleDays;

        public bool KeyKept
        {
            get { return !string.IsNullOrEmpty(Key); }
        }

        public OwnStorage Copy()
        {
            return (OwnStorage)MemberwiseClone();
        }

        // Forgets the account key and the publish link. The read link stays: it only reads TrackVice versions.
        public void DropKey()
        {
            Key = "";
            AgentWrite = "";
        }

        [DataContract(Namespace = "")]
        sealed class Doc
        {
            [DataMember(Order = 1)] public string Account;
            [DataMember(Order = 2)] public string BlobEndpoint;
            [DataMember(Order = 3)] public string Key;
            [DataMember(Order = 4)] public string Subscription;
            [DataMember(Order = 5)] public string AgentRead;
            [DataMember(Order = 6)] public string AgentWrite;
            [DataMember(Order = 7)] public string Expires;
            [DataMember(Order = 8)] public string Published;
            [DataMember(Order = 9, EmitDefaultValue = false)] public string Id;
            [DataMember(Order = 10, EmitDefaultValue = false)] public string Region;
            [DataMember(Order = 11, EmitDefaultValue = false)] public string Tenant;
            [DataMember(Order = 12, EmitDefaultValue = false)] public bool AskKey;
            [DataMember(Order = 13, EmitDefaultValue = false)] public int Days;
            [DataMember(Order = 14, EmitDefaultValue = false)] public int RuleDays;
        }

        public static OwnStorage Make(AzureAccount a, string key, string subscription, DateTime nowUtc)
        {
            OwnStorage o = new OwnStorage();
            o.Account = a.Name;
            o.BlobEndpoint = a.BlobEndpoint;
            o.Key = key;
            o.Subscription = subscription ?? "";
            o.Id = a.Id ?? "";
            o.Region = a.Location ?? "";
            o.SignAgentLinks(nowUtc);
            return o;
        }

        public void SignAgentLinks(DateTime nowUtc)
        {
            DateTime st = nowUtc.AddMinutes(-15), se = nowUtc.AddDays(AgentLinkDays);
            AgentRead = SasLinks.Container(BlobEndpoint, Account, Arm.AgentFiles, "r", st, se, Key);
            AgentWrite = SasLinks.Container(BlobEndpoint, Account, Arm.AgentFiles, "cw", st, se, Key);
            ExpiresUtc = se;
        }

        public string Text()
        {
            Doc d = new Doc();
            d.Account = Account;
            d.BlobEndpoint = BlobEndpoint;
            d.Key = Key;
            d.Subscription = Subscription;
            d.AgentRead = AgentRead;
            d.AgentWrite = AgentWrite;
            d.Expires = SasLinks.Time(ExpiresUtc);
            d.Published = PublishedVersion;
            d.Id = Empty(Id);
            d.Region = Empty(Region);
            d.Tenant = Empty(Tenant);
            d.AskKey = AskKey;
            d.Days = RetentionDays;
            d.RuleDays = RuleDays;
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Doc)).WriteObject(ms, d);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static OwnStorage Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Doc d;
            try
            {
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(text))) d = (Doc)new DataContractJsonSerializer(typeof(Doc)).ReadObject(ms);
            }
            catch (Exception) { return null; }
            if (d == null || string.IsNullOrEmpty(d.Account) || (string.IsNullOrEmpty(d.Key) && !d.AskKey) || string.IsNullOrEmpty(d.AgentRead)) return null;
            OwnStorage o = new OwnStorage();
            o.Account = d.Account;
            o.BlobEndpoint = string.IsNullOrEmpty(d.BlobEndpoint) ? "https://" + d.Account + ".blob.core.windows.net/" : d.BlobEndpoint;
            o.Key = d.Key ?? "";
            o.Subscription = d.Subscription ?? "";
            o.AgentRead = d.AgentRead;
            o.AgentWrite = d.AgentWrite ?? "";
            DateTime t;
            if (DateTime.TryParse(d.Expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) o.ExpiresUtc = t;
            o.PublishedVersion = d.Published ?? "";
            o.Id = d.Id ?? "";
            o.Region = d.Region ?? "";
            o.Tenant = d.Tenant ?? "";
            o.AskKey = d.AskKey;
            o.RetentionDays = d.Days > 0 ? d.Days : StorageSecurity.DefaultDays;
            o.RuleDays = d.RuleDays;
            return o;
        }

        static string Empty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public static OwnStorage Load(ISecretStore s)
        {
            try { return Parse(s.Load(SecretName)); }
            catch (Exception) { return null; }
        }

        public void Save(ISecretStore s)
        {
            s.Save(SecretName, Text());
        }

        // Forgets the copy on this PC. Nothing in Azure is changed.
        public static void Forget(ISecretStore s)
        {
            s.Save(SecretName, null);
        }
    }

    // An account SAS, as described in "Create an account SAS" for version 2020-12-06 and later. Used only for a moment,
    // to make a customer's container with the Blob service.
    public static class AccountSas
    {
        public static string StringToSign(string account, string perms, string services, string types, string start, string expiry, string version)
        {
            return account + "\n" + perms + "\n" + services + "\n" + types + "\n" + start + "\n" + expiry + "\n\nhttps\n" + version + "\n\n";
        }

        public static string Query(string account, string key, string perms, string services, string types, DateTime startUtc, DateTime expiryUtc)
        {
            string st = SasLinks.Time(startUtc), se = SasLinks.Time(expiryUtc);
            string sig = SasLinks.Sign(StringToSign(account, perms, services, types, st, se, SasLinks.Version), key);
            return "sv=" + SasLinks.Version + "&ss=" + services + "&srt=" + types + "&sp=" + perms + "&st=" + Uri.EscapeDataString(st) + "&se=" + Uri.EscapeDataString(se) + "&spr=https&sig=" + Uri.EscapeDataString(sig);
        }
    }

    public static class BlobContainers
    {
        // tv-<customer>: lowercase letters, digits and single hyphens, 3 to 63 characters.
        public static string NameFor(string customer)
        {
            StringBuilder b = new StringBuilder("tv-");
            string s = (customer ?? "").Trim().ToLowerInvariant().Replace("æ", "ae").Replace("ø", "o").Replace("ß", "ss").Normalize(NormalizationForm.FormD);
            foreach (char c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) b.Append(c);
                else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                else if (b[b.Length - 1] != '-') b.Append('-');
            }
            string n = b.ToString();
            if (n.Length > 63) n = n.Substring(0, 63);
            n = n.TrimEnd('-');
            if (n.Length < 4) n = "tv-customer";
            return n;
        }

        // Null when the name can be used for a container.
        public static string Problem(string name)
        {
            string n = name ?? "";
            if (n.Length < 3 || n.Length > 63) return "A container name has 3 to 63 characters.";
            if (!System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9]))*$")) return "A container name has lowercase letters, digits and single hyphens, and starts and ends with a letter or digit.";
            return null;
        }

        // Create (c) is only for blobs in an account SAS; a container is made with Write on the container resource type.
        public const string ContainerPerms = "w";

        // Makes a private container with the Blob service. False when it was there already.
        public static bool Create(IWeb web, OwnStorage own, string name, DateTime nowUtc)
        {
            string problem = Problem(name);
            if (problem != null) throw new SetupException(problem);
            WebCall c = new WebCall();
            c.Method = "PUT";
            c.Url = own.BlobEndpoint.TrimEnd('/') + "/" + name + "?restype=container&" + AccountSas.Query(own.Account, own.Key, ContainerPerms, "b", "c", nowUtc.AddMinutes(-15), nowUtc.AddHours(1));
            c.Headers["x-ms-version"] = SasLinks.Version;
            WebReply r = web.Send(c);
            if (r.Status == 201) return true;
            string code = r.Header("x-ms-error-code") ?? "";
            if (r.Status == 409 && (code.Length == 0 || code == "ContainerAlreadyExists")) return false;
            if (r.Status == 403) throw new SetupException("The storage account " + own.Account + " refused the account key (HTTP 403" + (code.Length > 0 ? ", " + code : "") + "). If the key was rotated, set up Your storage again.", r.Status, code);
            throw new SetupException("The container " + name + " could not be made (HTTP " + r.Status + (code.Length > 0 ? ", " + code : "") + ").", r.Status, code);
        }
    }

    // A container reached with a SAS link through IWeb, for agent/latest.json and the versions.
    public sealed class WebContainer : ISessionStore
    {
        readonly IWeb web;
        readonly string container, sas;

        public WebContainer(IWeb web, string link)
        {
            this.web = web;
            int q = (link ?? "").IndexOf('?');
            if (q < 0) throw new StoreException("The link has no access token.");
            container = link.Substring(0, q).TrimEnd('/');
            sas = link.Substring(q + 1);
        }

        public string Title
        {
            get { return container; }
        }

        string Url(string name)
        {
            List<string> parts = new List<string>();
            foreach (string p in name.Split('/')) parts.Add(Uri.EscapeDataString(p));
            return container + "/" + string.Join("/", parts.ToArray()) + "?" + sas;
        }

        public void Put(string name, byte[] data, Dictionary<string, string> meta)
        {
            WebCall c = new WebCall();
            c.Method = "PUT";
            c.Url = Url(name);
            c.Headers["x-ms-blob-type"] = "BlockBlob";
            c.Headers["x-ms-version"] = SasLinks.Version;
            c.ContentType = "application/octet-stream";
            c.Body = data;
            WebReply r = web.Send(c);
            if (r.Status != 201 && r.Status != 200)
                throw new StoreException("The upload of " + name + " failed (HTTP " + r.Status + (r.Header("x-ms-error-code") != null ? ", " + r.Header("x-ms-error-code") : "") + ").");
        }

        public List<StoreEntry> List(string prefix)
        {
            throw new StoreException("This link is only used to read and write single files.");
        }

        // Null when the file is not there.
        public byte[] Find(string name)
        {
            WebCall c = new WebCall();
            c.Url = Url(name);
            c.Headers["x-ms-version"] = SasLinks.Version;
            WebReply r = web.Send(c);
            if (r.Status == 404) return null;
            if (r.Status != 200) throw new StoreException(name + " could not be read (HTTP " + r.Status + ").");
            return Encoding.UTF8.GetBytes(r.Text);
        }

        public byte[] Get(string name)
        {
            byte[] b = Find(name);
            if (b == null) throw new StoreException(name + " is not there.");
            return b;
        }
    }

    public static class OwnPublish
    {
        // The version in agent/latest.json of your storage, or null when nothing is published.
        public static string Published(IWeb web, string readLink)
        {
            byte[] b = new WebContainer(web, readLink).Find(UpdateManifest.Name);
            if (b == null) return null;
            return UpdateManifest.Parse(b).Version;
        }

        public static UpdateManifest Publish(IWeb web, OwnStorage own, byte[] exe, string version, RSACryptoServiceProvider key, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(own.AgentWrite)) throw new SetupException("Your storage has no link to publish with. Set it up again.");
            UpdateManifest m = UpdatePublisher.Publish(new WebContainer(web, own.AgentWrite), exe, version, key, nowUtc);
            own.PublishedVersion = m.Version;
            return m;
        }

        // True when the running version should be offered for publishing: nothing is published, or an older version.
        public static bool Needed(OwnStorage own, string published, string running)
        {
            if (own == null || !SemVer.IsValid(running)) return false;
            if (string.IsNullOrEmpty(published) || !SemVer.IsValid(published)) return true;
            return SemVer.Compare(published, running) < 0;
        }

        // Why versions and customer codes made with this PC's update key would be refused, or null. The devices trust the key
        // that signed agent/latest.json, so a PC with another key must not publish over it or make codes with its own key.
        public static string KeyProblem(IWeb web, OwnStorage own, string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey)) return "This PC has no update key. Set up Your storage again to make one.";
            byte[] b = new WebContainer(web, own.AgentRead).Find(UpdateManifest.Name);
            if (b == null) return null;
            bool signed;
            try { signed = UpdateManifest.Parse(b).Verify(publicKey); }
            catch (Exception) { signed = false; }
            if (signed) return null;
            return UpdateManifest.Name + " in " + own.Account + " is signed with another update key than the one on this PC, so devices would refuse what this PC makes. Publish and add customers on the PC that has that key.";
        }

        public static string Prompt(string running)
        {
            return "TrackVice " + running + " is not published to your storage.";
        }
    }

    // The short guide that sets up your storage: an account, the container trackvice-agent, the kept key and links,
    // the update key, and the running TrackVice.exe as the first version.
    public sealed class OwnStorageRun
    {
        public AzureSubscription Subscription;
        public bool NewAccount = true, NewGroup;
        public string ResourceGroup = "rg-trackvice", AccountName = "", Region = Arm.DefaultRegion;
        public AzureAccount Account;
        public OwnStorage Result;
        public bool KeyCreated, PublishedNow;
        // Do not keep the account key on this PC after the setup.
        public bool AskKey;
        public int RetentionDays = StorageSecurity.DefaultDays;
        // Why the lifecycle rule could not be set, or empty.
        public string RuleError = "";
        public readonly List<string> Done = new List<string>();

        public void Run(Arm arm, IWeb web, IUpdateKeyStore keys, ISecretStore secrets, byte[] exe, string exeVersion, DateTime nowUtc, Action<string> progress)
        {
            if (Subscription == null) throw new SetupException("Pick a subscription.");
            if (NewAccount && Account == null)
            {
                string rule = Arm.NameRule(AccountName);
                if (rule != null) throw new SetupException(rule);
                if ((ResourceGroup ?? "").Trim().Length == 0) throw new SetupException("Pick or enter a resource group.");
                if (NewGroup)
                {
                    progress("Creating the resource group " + ResourceGroup + "...");
                    arm.CreateResourceGroup(Subscription.Id, ResourceGroup, Region);
                    Done.Add("Resource group " + ResourceGroup + " in " + Region);
                    NewGroup = false;
                }
                progress("Creating the storage account " + AccountName + ". This takes up to a minute...");
                string problem = arm.NameProblem(Subscription.Id, AccountName);
                if (problem != null) throw new SetupException(problem);
                Account = arm.CreateAccount(Subscription.Id, ResourceGroup, AccountName, Region);
                Done.Add("Storage account " + Account.Name + ": StorageV2, Standard_LRS, HTTPS only, TLS 1.2, no anonymous access");
            }
            if (Account == null) throw new SetupException("Pick a storage account.");
            progress("Creating the container " + Arm.AgentFiles + "...");
            bool made = arm.CreateContainer(Account, Arm.AgentFiles);
            Done.Add("Container " + Arm.AgentFiles + (made ? "" : ", there already"));
            progress("Adding the lifecycle rule " + StorageSecurity.RuleName + "...");
            bool ruleSet = false;
            try
            {
                new StorageSecurity(arm).SetCustomerRule(Account, RetentionDays);
                ruleSet = true;
                Done.Add("Lifecycle rule " + StorageSecurity.RuleName + ": deletes blobs in the containers tv-... " + RetentionDays + " days after they last changed. Other rules are kept");
            }
            catch (SetupException ex)
            {
                RuleError = ex.Message;
                Done.Add("The lifecycle rule " + StorageSecurity.RuleName + " was not added: " + ex.Message);
            }
            progress("Reading the account key...");
            Result = OwnStorage.Make(Account, arm.Key(Account), Subscription.Name, nowUtc);
            Result.Tenant = Subscription.Tenant ?? "";
            Result.RetentionDays = RetentionDays;
            Result.RuleDays = ruleSet ? RetentionDays : 0;
            Done.Add((AskKey ? "A read link to " + Arm.AgentFiles + " kept on this PC" : "Account key and the links to " + Arm.AgentFiles + " kept on this PC") + ", valid until " + Result.ExpiresUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using (RSACryptoServiceProvider key = UpdateKeys.LoadOrCreate(keys, out KeyCreated))
            {
                if (KeyCreated) Done.Add("A new update key, kept on this PC");
                progress("Reading " + UpdateManifest.Name + "...");
                string have = OwnPublish.Published(web, Result.AgentRead);
                if (have == null && exe != null)
                {
                    progress("Publishing TrackVice " + exeVersion + "...");
                    OwnPublish.Publish(web, Result, exe, exeVersion, key, nowUtc);
                    PublishedNow = true;
                    Done.Add("Published TrackVice " + exeVersion + " to " + Arm.AgentFiles);
                }
                else
                {
                    Result.PublishedVersion = have ?? "";
                    string problem = have != null ? OwnPublish.KeyProblem(web, Result, UpdateKeys.PublicKey(key)) : null;
                    if (problem != null) Done.Add(problem);
                }
            }
            if (AskKey)
            {
                Result.AskKey = true;
                Result.DropKey();
                Done.Add("The account key is not kept on this PC. Add customer and Publish sign in to Azure to read it");
            }
            Result.Save(secrets);
        }
    }

    // ---------- the customer codes ----------

    public static class CustomerCodes
    {
        static string Name(string customer)
        {
            return "CustomerCode." + (customer ?? "").Trim();
        }

        public static string Load(ISecretStore s, string customer)
        {
            try
            {
                string v = s.Load(Name(customer));
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch (Exception) { return null; }
        }

        public static void Save(ISecretStore s, string customer, string code)
        {
            s.Save(Name(customer), code);
        }

        // tv1:abcdef... with the rest hidden, for the window.
        public static string Masked(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int keep = Math.Min(code.Length, CustomerCode.Prefix.Length + 6);
            return code.Substring(0, keep) + Secrets.Hidden + " (" + code.Length + " characters)";
        }
    }

    // ---------- Intune ----------

    // Finds or makes the Autopilot device group, and adds, updates and assigns the platform script.
    public sealed class IntuneSetup
    {
        public const string GroupWriteScope = "https://graph.microsoft.com/Group.ReadWrite.All";
        public const string GroupsUrl = "https://graph.microsoft.com/v1.0/groups";
        public const string Scopes = IntuneScripts.ScriptScope + " " + GroupWriteScope;

        readonly IWeb web;
        readonly Func<string> token;

        public IntuneSetup(IWeb web, Func<string> token)
        {
            this.web = web;
            this.token = token;
        }

        internal static SetupException Fail(WebReply r, string doing)
        {
            Dictionary<string, object> e = ImeJson.Get(r.Json, "error") as Dictionary<string, object>;
            string msg = GraphClient.Clean(Http.Str(e, "message"));
            string text;
            string known = r.Status == 401 ? null : SetupErrors.Graph(r.Status, Http.Str(e, "code"), msg, doing, doing.IndexOf("group", StringComparison.OrdinalIgnoreCase) >= 0 ? SetupErrors.GraphUse.Groups : SetupErrors.GraphUse.Scripts);
            if (known != null) return new SetupException(known, r.Status, Http.Str(e, "code"));
            if (r.Status == 401) text = "The Intune sign-in has expired. Sign in again.";
            else text = "Microsoft Graph could not " + doing + ".";
            if (!string.IsNullOrEmpty(msg)) text += " (" + msg + ")";
            else if (r.Status >= 400 && r.Status != 401 && r.Status != 403) text += " (HTTP " + r.Status + ")";
            return new SetupException(text, r.Status, Http.Str(e, "code"));
        }

        WebReply Send(string method, string url, object body)
        {
            return web.Send(WebCall.Json(method, url, token(), body == null ? null : SetupJson.Write(body)));
        }

        static List<Dictionary<string, object>> Values(WebReply r)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            List<object> v = ImeJson.Get(r.Json, "value") as List<object>;
            if (v != null) foreach (object o in v) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) l.Add(d); }
            return l;
        }

        public static string GroupUrl(string name)
        {
            return GroupsUrl + "?$filter=" + Uri.EscapeDataString("displayName eq '" + (name ?? "").Replace("'", "''") + "'") + "&$select=id,displayName";
        }

        // The group with exactly this name, or null.
        public DirectoryGroup FindGroup(string name)
        {
            WebReply r = Send("GET", GroupUrl(name), null);
            if (r.Status != 200) throw Fail(r, "read the groups");
            foreach (Dictionary<string, object> d in Values(r))
            {
                if (!string.Equals(Http.Str(d, "displayName"), name, StringComparison.OrdinalIgnoreCase)) continue;
                DirectoryGroup g = new DirectoryGroup();
                g.Id = Http.Str(d, "id");
                g.Name = Http.Str(d, "displayName");
                if (!string.IsNullOrEmpty(g.Id)) return g;
            }
            return null;
        }

        public static Dictionary<string, object> GroupBody(string name, string rule)
        {
            return SetupJson.Obj(
                "displayName", name,
                "description", "Devices registered with Windows Autopilot. Made by TrackVice for the TrackVice agent.",
                "groupTypes", SetupJson.List("DynamicMembership"),
                "mailEnabled", false,
                "mailNickname", "trackvice-autopilot-devices",
                "securityEnabled", true,
                "membershipRule", rule,
                "membershipRuleProcessingState", "On");
        }

        public DirectoryGroup CreateGroup(string name, string rule)
        {
            WebReply r = Send("POST", GroupsUrl, GroupBody(name, rule));
            if (r.Status != 201 && r.Status != 200) throw Fail(r, "make the group " + name);
            DirectoryGroup g = new DirectoryGroup();
            g.Id = Http.Str(r.Json, "id");
            g.Name = name;
            if (string.IsNullOrEmpty(g.Id)) throw new SetupException("Microsoft Graph did not return the ID of the new group.");
            return g;
        }

        // The ID of the platform script with this name, or null.
        public string FindScript(string name)
        {
            string url = IntuneScripts.ScriptsUrl + "?$select=id,displayName";
            for (int page = 0; page < 20 && !string.IsNullOrEmpty(url); page++)
            {
                WebReply r = Send("GET", url, null);
                if (r.Status != 200) throw Fail(r, "read the platform scripts");
                foreach (Dictionary<string, object> d in Values(r))
                    if (string.Equals(Http.Str(d, "displayName"), name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Http.Str(d, "id"))) return Http.Str(d, "id");
                url = Http.Str(r.Json, "@odata.nextLink");
            }
            return null;
        }

        public string CreateScript(string name, string fileName, string content)
        {
            return new IntuneScripts(web, token).Create(name, fileName, content);
        }

        public void UpdateScript(string id, string name, string fileName, string content)
        {
            WebReply r = Send("PATCH", IntuneScripts.ScriptsUrl + "/" + Uri.EscapeDataString(id), IntuneScripts.ScriptBody(name, fileName, content));
            if (r.Status != 200 && r.Status != 204) throw Fail(r, "change the platform script " + name);
        }

        public List<string> AssignedGroups(string id)
        {
            WebReply r = Send("GET", IntuneScripts.ScriptsUrl + "/" + Uri.EscapeDataString(id) + "/assignments", null);
            if (r.Status != 200) throw Fail(r, "read the assignments of the platform script");
            List<string> l = new List<string>();
            foreach (Dictionary<string, object> d in Values(r))
            {
                string g = Http.Str(ImeJson.Get(d, "target") as Dictionary<string, object>, "groupId");
                if (!string.IsNullOrEmpty(g)) l.Add(g);
            }
            return l;
        }

        // The assign action replaces every assignment, so the ones the script has are sent with the new group.
        public void Assign(string id, List<string> groups)
        {
            List<object> l = new List<object>();
            foreach (string g in groups) l.Add(SetupJson.Obj("target", SetupJson.Obj("@odata.type", "#microsoft.graph.groupAssignmentTarget", "groupId", g)));
            WebReply r = Send("POST", IntuneScripts.ScriptsUrl + "/" + Uri.EscapeDataString(id) + "/assign", SetupJson.Obj("deviceManagementScriptAssignments", l));
            if (r.Status != 204 && r.Status != 200) throw Fail(r, "assign the platform script");
        }
    }

    // ---------- add a customer ----------

    // What Add customer chose and made. Nothing is sent before Create.
    public sealed class AddCustomerRun
    {
        public const string GroupName = "TrackVice - Autopilot devices";
        public const string GroupRule = "(device.devicePhysicalIds -any (_ -contains \"[ZTDId]\"))";
        public const string ScriptName = "TrackVice agent";
        public const int LinkDays = 730;

        public string Customer = "", Tenant = "", Alert = "";
        // Store the data in the customer's own Azure instead of your storage.
        public bool OwnAzure;
        public bool AddScript = true;
        // Turn on automatic time zone on new devices. Off unless chosen.
        public bool AutoTimeZone;
        // Where the platform script is assigned. Chosen is the group picked for ScriptTarget.ChosenGroup.
        public ScriptTarget Target = ScriptTarget.NewGroup;
        public TenantGroup Chosen;
        // With OwnAzure: the storage steps of the New customer guide, run against the customer's subscription.
        public NewCustomerRun Storage;

        public string Container = "", Upload = "", Viewer = "";
        public DateTime LinksExpireUtc;
        public bool ContainerWasThere;
        public CustomerCode Code;
        public string Script = "";
        public DirectoryGroup Group;
        public bool GroupReused;
        public string ScriptId = "";
        public bool ScriptExisted, ScriptReplaced, IntuneDone;
        public string IntuneError = "", ScriptPath = "";
        public readonly List<string> Done = new List<string>();

        // The tenant ID in an access token, or null. The token is not checked here; the service checks it.
        public static string TenantOf(string jwt)
        {
            try
            {
                string[] parts = (jwt ?? "").Split('.');
                if (parts.Length < 2) return null;
                string p = parts[1].Replace('-', '+').Replace('_', '/');
                while (p.Length % 4 != 0) p += "=";
                return Http.Str(ImeJson.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p))) as Dictionary<string, object>, "tid");
            }
            catch (Exception) { return null; }
        }

        // Make the group TrackVice - Autopilot devices and assign the script to it. Off leaves the script not assigned.
        public bool MakeGroup
        {
            get { return Target == ScriptTarget.NewGroup; }
            set { Target = value ? ScriptTarget.NewGroup : ScriptTarget.None; }
        }

        // What keeps Create from starting in step 3, or null.
        public string CreateProblem(bool intune)
        {
            if (intune && AddScript && Target == ScriptTarget.ChosenGroup && Chosen == null) return "Search for the group and pick it, or choose another assignment.";
            return null;
        }

        public string ContainerName
        {
            get { return BlobContainers.NameFor(Customer); }
        }

        // What is wrong with the first step, or null.
        public string Problem(CustomerBook book)
        {
            string n = (Customer ?? "").Trim();
            if (book.Find(n) == null)
            {
                string p = book.NameProblem(n, null);
                if (p != null) return p;
            }
            string a = (Alert ?? "").Trim();
            if (a.Length > 0 && AlertSender.Problem(a) != null) return AlertSender.Problem(a);
            if (!OwnAzure) return ContainerClash(book, n, null);
            return null;
        }

        // Null, or why another customer's container would be the same as this name's.
        public static string ContainerClash(CustomerBook book, string name, Customer except)
        {
            string n = (name ?? "").Trim();
            string container = BlobContainers.NameFor(n);
            foreach (Customer c in book.All)
                if (c != except && !string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase) && BlobContainers.NameFor(c.Name) == container)
                    return "The customer " + c.Name + " would use the same container " + container + ". Give this customer a name that differs in letters or digits.";
            return null;
        }

        // The lines of the Create page: exactly what will be made.
        public List<string> Plan(OwnStorage own, bool intune)
        {
            List<string> l = new List<string>();
            if (OwnAzure)
            {
                l.Add("In the customer's Azure: a storage account with the private container " + Arm.Sessions + ", a lifecycle rule that deletes uploads after 30 days, and links valid for 2 years.");
            }
            else
            {
                l.Add("In your storage " + (own != null ? own.Account : "") + ": the private container " + ContainerName + ", or the one that is there.");
                l.Add("Two stored access policies on it, " + CustomerAccess.Upload + " (Create, Write) and " + CustomerAccess.Viewer + " (Read, List), valid for 2 years, and a link signed with each: one that can only upload, for the devices, and one that can read, for you. Remove customer removes the policies, and both links stop.");
            }
            l.Add("The customer code and the platform script, saved on this PC. Devices download TrackVice from your storage.");
            if (intune && AddScript)
            {
                string script = "In Intune: the platform script " + ScriptName + " (system context, no signature check, 64-bit)";
                if (Target == ScriptTarget.NewGroup)
                {
                    l.Add("In Intune: the dynamic device group " + GroupName + ", or the one with that name.");
                    l.Add(script + ", assigned to that group.");
                }
                else if (Target == ScriptTarget.ChosenGroup)
                    l.Add(script + (Chosen != null ? ", assigned to the group " + Chosen.Name + "." : ", assigned to the group you pick below.") + " No group is made.");
                else l.Add(script + ", not assigned. You assign it in Intune.");
            }
            else l.Add("Nothing in Intune. Add the saved script as a platform script yourself.");
            return l;
        }

        // Makes the customer's container in your storage and signs its links.
        public void MakeStorage(IWeb web, OwnStorage own, DateTime nowUtc)
        {
            if (own == null) throw new SetupException("Set up Your storage first.");
            Container = ContainerName;
            bool made = BlobContainers.Create(web, own, Container, nowUtc);
            ContainerWasThere = !made;
            Done.Add("Container " + Container + " in " + own.Account + (made ? "" : ", there already"));
            CustomerLinks links = CustomerAccess.Grant(web, own, Container, made, nowUtc, LinkDays);
            Upload = links.Upload;
            Viewer = links.Viewer;
            LinksExpireUtc = links.ExpiresUtc;
            Done.Add("Access policies " + links.UploadPolicy + " and " + links.ViewerPolicy + " on " + Container + ", and the upload and viewer links signed with them, valid until " + links.ExpiresUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ". Azure puts them in force within 30 seconds");
        }

        // The storage in the customer's own Azure: the account, the container trackvice and the lifecycle rule, and the
        // links. TrackVice versions still come from your storage, so no trackvice-agent container is made there.
        public void SetUpOwnAzure(Arm arm, DateTime nowUtc, Action<string> progress)
        {
            NewCustomerRun st = Storage;
            if (st == null) throw new SetupException("Sign in to the customer's Azure and pick the storage account.");
            st.AgentContainer = false;
            st.LinkDays = LinkDays;
            int from = st.Done.Count;
            if (!st.StorageReady) st.SetUpStorage(arm, progress);
            if (st.Links == null) { progress("Signing the links..."); st.MakeLinks(arm, nowUtc); }
            Done.AddRange(st.Done.GetRange(from, st.Done.Count - from));
            UseLinks(st.Links);
        }

        // Takes the links the storage steps of the New customer guide made in the customer's Azure.
        public void UseLinks(AgentLinks links)
        {
            Upload = links.Upload;
            Viewer = links.Viewer;
            LinksExpireUtc = links.ExpiresUtc;
            Container = Arm.Sessions;
        }

        public void MakeCode(OwnStorage own, string publicKey)
        {
            if (own == null) throw new SetupException("Set up Your storage first.");
            if (string.IsNullOrEmpty(publicKey)) throw new SetupException("This PC has no update key. Set up Your storage again to make one.");
            CustomerCode c = new CustomerCode();
            c.Name = Customer.Trim();
            c.Upload = Upload;
            c.Agent = own.AgentRead;
            c.Key = publicKey;
            c.Alert = (Alert ?? "").Trim();
            c.AutoTimeZone = AutoTimeZone;
            string p = c.Problem();
            if (p != null) throw new SetupException(p);
            Code = c;
            Script = CustomerScript.For(c.ToString());
        }

        // The group and the script. A script that is there gets new content only when replace says yes.
        public void ToIntune(IntuneSetup api, Func<bool> replace)
        {
            if (AddScript && Target == ScriptTarget.NewGroup)
            {
                Group = api.FindGroup(GroupName);
                GroupReused = Group != null;
                if (Group == null) Group = api.CreateGroup(GroupName, GroupRule);
                Done.Add("Group " + GroupName + (GroupReused ? ", there already" : ", dynamic"));
            }
            else if (AddScript && Target == ScriptTarget.ChosenGroup)
            {
                if (Chosen == null || string.IsNullOrEmpty(Chosen.Id)) throw new SetupException("No group was picked for the platform script.");
                Group = Chosen.ToDirectoryGroup();
            }
            if (AddScript)
            {
                string file = AgentScript.FileName(Customer);
                ScriptId = api.FindScript(ScriptName) ?? "";
                ScriptExisted = ScriptId.Length > 0;
                if (!ScriptExisted)
                {
                    ScriptId = api.CreateScript(ScriptName, file, Script);
                    Done.Add("Platform script " + ScriptName);
                }
                else if (replace())
                {
                    api.UpdateScript(ScriptId, ScriptName, file, Script);
                    ScriptReplaced = true;
                    Done.Add("Platform script " + ScriptName + ", content replaced");
                }
                else Done.Add("Platform script " + ScriptName + ", there already, content kept");
                if (Group != null)
                {
                    List<string> have = ScriptExisted ? api.AssignedGroups(ScriptId) : new List<string>();
                    if (!have.Contains(Group.Id))
                    {
                        have.Add(Group.Id);
                        api.Assign(ScriptId, have);
                        Done.Add("Assigned to " + Group.Name);
                    }
                    else Done.Add("Assigned to " + Group.Name + " already");
                }
                else Done.Add("Not assigned. Assign it in Intune");
            }
            IntuneDone = true;
        }

        // Adds the customer, or updates the one with this name: the tenant and the viewer link, which the book keeps encrypted.
        public Customer SaveTo(CustomerBook book)
        {
            Customer have = book.Find(Customer);
            Customer d = have != null ? have.Copy() : new Customer();
            d.Name = have != null ? have.Name : Customer.Trim();
            if ((Tenant ?? "").Trim().Length > 0) d.Tenant = Tenant.Trim();
            d.AgentLocation = Viewer;
            return have != null ? book.Update(have.Name, d) : book.Add(d);
        }

        public string SaveScript(string folder)
        {
            Directory.CreateDirectory(folder);
            ScriptPath = Path.Combine(folder, AgentScript.FileName(Customer));
            File.WriteAllText(ScriptPath, Script, new UTF8Encoding(false));
            return ScriptPath;
        }
    }

    // Step 1 asks before it takes the name of a customer that exists, since Create then makes new links and the old ones stop.
    public static class ExistingName
    {
        public const string ChooseAnother = "Choose another name";

        // The customer with this name, ignoring case, or null.
        public static Customer Find(CustomerBook book, string name)
        {
            return book == null ? null : book.Find((name ?? "").Trim());
        }

        // True when the name is of a customer that exists, and the update was not confirmed for this name.
        public static bool MustAsk(CustomerBook book, string name, string confirmed)
        {
            Customer c = Find(book, name);
            return c != null && !string.Equals(c.Name, (confirmed ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static string Question(string name)
        {
            return Title(name) + ". " + Detail(name);
        }

        public static string Title(string name)
        {
            return name + " already exists";
        }

        public static string Detail(string name)
        {
            return "Update " + name + ": new links are made and the old ones stop, so update the script in Intune too.";
        }

        public static string UpdateButton(string name)
        {
            return "Update " + name;
        }
    }

    // ---------- change a customer later ----------

    public static class CustomerCodeChange
    {
        // The code with another alert link, checked.
        public static CustomerCode WithAlert(string code, string alert)
        {
            CustomerCode c = CustomerCode.Parse(code);
            string a = (alert ?? "").Trim();
            if (a.Length > 0 && AlertSender.Problem(a) != null) throw new SetupException(AlertSender.Problem(a));
            c.Alert = a.Length > 0 ? a : null;
            string p = c.Problem();
            if (p != null) throw new SetupException(p);
            return c;
        }

        // The code with another alert link and the automatic time zone on or off, checked.
        public static CustomerCode WithSettings(string code, string alert, bool autoTimeZone)
        {
            CustomerCode c = WithAlert(code, alert);
            c.AutoTimeZone = autoTimeZone;
            return c;
        }

        // Puts the script for the code in the platform script TrackVice agent.
        public static string UpdateIntune(IntuneSetup api, string customer, string code)
        {
            string id = api.FindScript(AddCustomerRun.ScriptName);
            if (id == null) throw new SetupException("This tenant has no platform script called " + AddCustomerRun.ScriptName + ". Add customer adds it.");
            api.UpdateScript(id, AddCustomerRun.ScriptName, AgentScript.FileName(customer), CustomerScript.For(code));
            return id;
        }
    }

    // Answers the requests of Add customer and Your storage with made-up data, and the rest as the guided setup demo does.
    public sealed class DemoAddCustomerWeb : IWeb
    {
        public const string GroupId = "9d4e2b71-3c5a-4f86-b0e1-7a2c4d6f8b13";
        public const string ScriptId = "5e9b1c3d-7a2f-4b86-9d0e-3f4a5b6c7d8e";
        readonly DemoSetupWeb inner = new DemoSetupWeb();
        public readonly List<WebCall> Calls = new List<WebCall>();

        public WebReply Send(WebCall c)
        {
            Calls.Add(c);
            string u = c.Url;
            if (u.Contains("restype=container")) return new WebReply(201, "");
            if (c.Method == "GET" && u.Contains("/" + Arm.AgentFiles + "/agent/latest.json")) return new WebReply(404, "");
            if (c.Headers.ContainsKey("ConsistencyLevel") && u.StartsWith(IntuneSetup.GroupsUrl + "?")) return new WebReply(200, GroupSearch.DemoJson);
            if (u.StartsWith(IntuneSetup.GroupsUrl + "?$filter=")) return new WebReply(200, "{\"value\":[]}");
            if (u == IntuneSetup.GroupsUrl && c.Method == "POST") return new WebReply(201, "{\"id\":\"" + GroupId + "\"}");
            if (u.StartsWith(IntuneScripts.ScriptsUrl + "?$select")) return new WebReply(200, "{\"value\":[]}");
            if (u.EndsWith("/assignments")) return new WebReply(200, "{\"value\":[]}");
            if (c.Method == "PATCH") return new WebReply(204, "");
            return inner.Send(c);
        }
    }
}
