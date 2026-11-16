namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    // One line of the security check of your storage.
    public sealed class SecurityItem
    {
        public string Id = "", Title = "", Line = "";
        // What the fix changes, for the question before it, such as "turn off anonymous access to blobs".
        public string Change = "";
        // The text of the button that makes the change.
        public string Button = "";
        public bool Ok;
        // Shown only; TrackVice never changes it.
        public bool Info;
    }

    // What Azure Resource Manager says about the account, read once.
    public sealed class SecurityCheck
    {
        public AzureAccount Account;
        // Null when the account does not say.
        public object PublicAccess, HttpsOnly, SharedKeyAccess;
        public string Tls = "";
        public bool BlobSoftDelete, ContainerSoftDelete;
        public int BlobSoftDays, ContainerSoftDays;
        // Days of the trackvice-customers rule, or 0 when it is not there as TrackVice makes it.
        public int RuleDays;
        public bool RuleThere;

        static bool Is(object v, bool want)
        {
            return v is bool && (bool)v == want;
        }

        public bool SharedKeyOn
        {
            get { return SharedKeyAccess == null || Is(SharedKeyAccess, true); }
        }

        public List<SecurityItem> Items(int days)
        {
            List<SecurityItem> l = new List<SecurityItem>();
            SecurityItem a = Item(StorageSecurity.PublicAccessId, "Anonymous blob access", Is(PublicAccess, false));
            a.Line = a.Ok ? "Off. Nothing in the account can be read without a link or a key." : (PublicAccess == null ? "Not set on the account, so containers may allow it." : "On. A container set to public can be read by anyone.") + " Turning it off sets allowBlobPublicAccess to false.";
            a.Change = "turn off anonymous blob access (allowBlobPublicAccess false)";
            a.Button = "Turn off";
            l.Add(a);
            SecurityItem h = Item(StorageSecurity.HttpsId, "HTTPS only", Is(HttpsOnly, true));
            h.Line = h.Ok ? "On. Requests over plain HTTP are refused." : "Off. Requests over plain HTTP are allowed. TrackVice links only use HTTPS.";
            h.Change = "allow HTTPS only (supportsHttpsTrafficOnly true)";
            h.Button = "Turn on";
            l.Add(h);
            SecurityItem t = Item(StorageSecurity.TlsId, "Minimum TLS 1.2", Tls == "TLS1_2" || Tls == "TLS1_3");
            t.Line = t.Ok ? "The account refuses older TLS versions." : "The minimum is " + (Tls.Length > 0 ? Tls.Replace("TLS1_", "TLS 1.") : "not set, which means TLS 1.0") + ".";
            t.Change = "set the minimum TLS version to 1.2 (minimumTlsVersion TLS1_2)";
            t.Button = "Set TLS 1.2";
            l.Add(t);
            SecurityItem b = Item(StorageSecurity.BlobSoftId, "Blob soft delete", BlobSoftDelete);
            b.Line = BlobSoftDelete ? "On, " + BlobSoftDays + " days. A deleted or overwritten blob can be brought back in that time." : "Off. A deleted blob is gone at once. Turning it on keeps deleted blobs for " + StorageSecurity.SoftDeleteDays + " days.";
            b.Change = "turn on blob soft delete for " + StorageSecurity.SoftDeleteDays + " days";
            b.Button = "Turn on";
            l.Add(b);
            SecurityItem c = Item(StorageSecurity.ContainerSoftId, "Container soft delete", ContainerSoftDelete);
            c.Line = ContainerSoftDelete ? "On, " + ContainerSoftDays + " days. A deleted container can be brought back in that time." : "Off. A deleted container is gone at once. Turning it on keeps deleted containers for " + StorageSecurity.SoftDeleteDays + " days.";
            c.Change = "turn on container soft delete for " + StorageSecurity.SoftDeleteDays + " days";
            c.Button = "Turn on";
            l.Add(c);
            SecurityItem r = Item(StorageSecurity.RuleId, "Delete old uploads", RuleThere && RuleDays == days);
            r.Line = r.Ok ? "The lifecycle rule " + StorageSecurity.RuleName + " deletes blobs in the containers tv-... " + days + " days after they last changed."
                : RuleThere ? "The lifecycle rule " + StorageSecurity.RuleName + " deletes after " + RuleDays + " days. The setting is " + days + " days."
                : "No rule deletes the uploads, so they stay until someone deletes them. The lifecycle rule " + StorageSecurity.RuleName + " deletes blobs in the containers tv-... " + days + " days after they last changed.";
            r.Change = "add or update the lifecycle rule " + StorageSecurity.RuleName + ", which deletes blobs in containers whose names start with tv- " + days + " days after they last changed. Other rules are kept";
            r.Button = RuleThere ? "Update the rule" : "Add the rule";
            l.Add(r);
            SecurityItem k = Item(StorageSecurity.KeyId, "Account key access", SharedKeyOn);
            k.Info = true;
            k.Line = SharedKeyOn ? "On. The links of TrackVice are signed with the account key, so they need it on. Anyone with the key can do anything in the account." : "Off. The links of TrackVice do not work. Turn on Allow storage account key access in the Azure portal.";
            l.Add(k);
            return l;
        }

        static SecurityItem Item(string id, string title, bool ok)
        {
            SecurityItem i = new SecurityItem();
            i.Id = id;
            i.Title = title;
            i.Ok = ok;
            return i;
        }

        public int Open(int days)
        {
            int n = 0;
            foreach (SecurityItem i in Items(days)) if (!i.Ok && !i.Info) n++;
            return n;
        }
    }

    // Reads the security settings of your storage through Azure Resource Manager, and changes one at a time when asked.
    // The account may be used for other things too, so a fix sends only its own setting, and network rules and keys are
    // never changed.
    public sealed class StorageSecurity
    {
        public const string RuleName = "trackvice-customers";
        // Add customer names every container tv-<customer>; a prefix without a slash matches each of them.
        public const string Prefix = "tv-";
        public const int SoftDeleteDays = 7;
        public const int DefaultDays = 30;
        public static readonly int[] DayChoices = { 7, 14, 30, 60, 90 };
        public const string PublicAccessId = "public", HttpsId = "https", TlsId = "tls", BlobSoftId = "blobsoft", ContainerSoftId = "containersoft", RuleId = "rule", KeyId = "key";

        readonly Arm arm;

        public StorageSecurity(Arm arm)
        {
            this.arm = arm;
        }

        static SetupException Fail(WebReply r, string doing)
        {
            Dictionary<string, object> e = ImeJson.Get(r.Json, "error") as Dictionary<string, object>;
            string code = Http.Str(e, "code") ?? "";
            string msg = Http.Str(e, "message");
            string text;
            if (r.Status == 401) text = "The Azure sign-in has expired or is not valid. Sign in again.";
            else if (r.Status == 403 || code == "AuthorizationFailed") text = "Your account may not " + doing + ". It needs Storage Account Contributor on the account.";
            else text = "Azure could not " + doing + ".";
            if (!string.IsNullOrEmpty(msg)) text += " " + Txt.Short(msg.Trim(), 300);
            else if (r.Status >= 400) text += " (HTTP " + r.Status + ")";
            return new SetupException(text, r.Status, code);
        }

        static string Api
        {
            get { return "?api-version=" + Arm.StorageApi; }
        }

        static Dictionary<string, object> Props(WebReply r)
        {
            return ImeJson.Get(r.Json, "properties") as Dictionary<string, object>;
        }

        static int Days(Dictionary<string, object> policy)
        {
            object d = ImeJson.Get(policy, "days");
            return d == null ? 0 : Convert.ToInt32(d, CultureInfo.InvariantCulture);
        }

        public SecurityCheck Read(AzureAccount a)
        {
            SecurityCheck c = new SecurityCheck();
            c.Account = a;
            WebReply r = arm.Send("GET", a.Id + Api, null);
            if (r.Status != 200) throw Fail(r, "read the storage account " + a.Name);
            Dictionary<string, object> p = Props(r);
            c.PublicAccess = ImeJson.Get(p, "allowBlobPublicAccess");
            c.HttpsOnly = ImeJson.Get(p, "supportsHttpsTrafficOnly");
            c.SharedKeyAccess = ImeJson.Get(p, "allowSharedKeyAccess");
            c.Tls = Http.Str(p, "minimumTlsVersion") ?? "";
            string loc = Http.Str(r.Json, "location");
            if (!string.IsNullOrEmpty(loc)) a.Location = loc;
            r = arm.Send("GET", a.Id + "/blobServices/default" + Api, null);
            if (r.Status != 200) throw Fail(r, "read the blob service of " + a.Name);
            p = Props(r);
            Dictionary<string, object> b = ImeJson.Get(p, "deleteRetentionPolicy") as Dictionary<string, object>;
            Dictionary<string, object> k = ImeJson.Get(p, "containerDeleteRetentionPolicy") as Dictionary<string, object>;
            c.BlobSoftDelete = ImeJson.Get(b, "enabled") is bool && (bool)ImeJson.Get(b, "enabled");
            c.BlobSoftDays = Days(b);
            c.ContainerSoftDelete = ImeJson.Get(k, "enabled") is bool && (bool)ImeJson.Get(k, "enabled");
            c.ContainerSoftDays = Days(k);
            List<object> rules = ReadRules(a);
            foreach (object o in rules)
            {
                Dictionary<string, object> rule = o as Dictionary<string, object>;
                if (Http.Str(rule, "name") != RuleName) continue;
                c.RuleThere = RuleDaysOf(rule) > 0;
                c.RuleDays = RuleDaysOf(rule);
            }
            return c;
        }

        // The days of a rule made as TrackVice makes it: on, block blobs under tv-, delete after n days. Otherwise 0.
        static int RuleDaysOf(Dictionary<string, object> rule)
        {
            if (!(ImeJson.Get(rule, "enabled") is bool) || !(bool)ImeJson.Get(rule, "enabled")) return 0;
            Dictionary<string, object> def = ImeJson.Get(rule, "definition") as Dictionary<string, object>;
            Dictionary<string, object> f = ImeJson.Get(def, "filters") as Dictionary<string, object>;
            List<object> prefixes = ImeJson.Get(f, "prefixMatch") as List<object>;
            if (prefixes == null || prefixes.Count != 1 || (prefixes[0] as string) != Prefix) return 0;
            Dictionary<string, object> act = ImeJson.Get(ImeJson.Get(def, "actions") as Dictionary<string, object>, "baseBlob") as Dictionary<string, object>;
            Dictionary<string, object> del = ImeJson.Get(act, "delete") as Dictionary<string, object>;
            object d = ImeJson.Get(del, "daysAfterModificationGreaterThan");
            return d == null ? 0 : Convert.ToInt32(d, CultureInfo.InvariantCulture);
        }

        // The rules the account has, or none.
        List<object> ReadRules(AzureAccount a)
        {
            WebReply r = arm.Send("GET", a.Id + "/managementPolicies/default" + Api, null);
            if (r.Status == 404) return new List<object>();
            if (r.Status != 200) throw Fail(r, "read the lifecycle rules of " + a.Name);
            Dictionary<string, object> pol = ImeJson.Get(Props(r), "policy") as Dictionary<string, object>;
            return ImeJson.Get(pol, "rules") as List<object> ?? new List<object>();
        }

        public static Dictionary<string, object> CustomerRule(int days)
        {
            return SetupJson.Obj(
                "enabled", true,
                "name", RuleName,
                "type", "Lifecycle",
                "definition", SetupJson.Obj(
                    "actions", SetupJson.Obj("baseBlob", SetupJson.Obj("delete", SetupJson.Obj("daysAfterModificationGreaterThan", days))),
                    "filters", SetupJson.Obj(
                        "blobTypes", SetupJson.List("blockBlob"),
                        "prefixMatch", SetupJson.List(Prefix))));
        }

        // Every rule the account has, in its place, with trackvice-customers replaced or added at the end.
        public static List<object> Merge(List<object> have, int days)
        {
            List<object> l = new List<object>();
            bool put = false;
            foreach (object o in have)
            {
                if (Http.Str(o as Dictionary<string, object>, "name") == RuleName)
                {
                    if (!put) l.Add(CustomerRule(days));
                    put = true;
                }
                else l.Add(o);
            }
            if (!put) l.Add(CustomerRule(days));
            return l;
        }

        // Reads the policy, changes only trackvice-customers, and writes it back.
        public void SetCustomerRule(AzureAccount a, int days)
        {
            if (days < 1) throw new SetupException("Uploads are deleted after at least 1 day.");
            List<object> rules = Merge(ReadRules(a), days);
            WebReply r = arm.Send("PUT", a.Id + "/managementPolicies/default" + Api, SetupJson.Obj("properties", SetupJson.Obj("policy", SetupJson.Obj("rules", rules))));
            if (r.Status != 200 && r.Status != 201) throw Fail(r, "set the lifecycle rule " + RuleName);
        }

        // The request body of a fix: only the one setting.
        public static Dictionary<string, object> FixBody(string id)
        {
            switch (id)
            {
                case PublicAccessId: return SetupJson.Obj("properties", SetupJson.Obj("allowBlobPublicAccess", false));
                case HttpsId: return SetupJson.Obj("properties", SetupJson.Obj("supportsHttpsTrafficOnly", true));
                case TlsId: return SetupJson.Obj("properties", SetupJson.Obj("minimumTlsVersion", "TLS1_2"));
                case BlobSoftId: return SetupJson.Obj("properties", SetupJson.Obj("deleteRetentionPolicy", SetupJson.Obj("enabled", true, "days", SoftDeleteDays)));
                case ContainerSoftId: return SetupJson.Obj("properties", SetupJson.Obj("containerDeleteRetentionPolicy", SetupJson.Obj("enabled", true, "days", SoftDeleteDays)));
                default: return null;
            }
        }

        public void Fix(AzureAccount a, string id, int days)
        {
            if (id == RuleId) { SetCustomerRule(a, days); return; }
            Dictionary<string, object> body = FixBody(id);
            if (body == null) throw new SetupException("TrackVice does not change this setting.");
            WebReply r;
            if (id == BlobSoftId || id == ContainerSoftId) r = arm.Send("PUT", a.Id + "/blobServices/default" + Api, body);
            else r = arm.Send("PATCH", a.Id + Api, body);
            if (r.Status != 200 && r.Status != 201 && r.Status != 202) throw Fail(r, "change the storage account " + a.Name);
        }
    }

    // ---------- the account in use ----------

    public sealed class UsedAccount
    {
        public string Name = "";
        public readonly List<string> Customers = new List<string>();
    }

    public sealed class FoundAccount
    {
        public AzureSubscription Subscription;
        public AzureAccount Account;
        public List<string> Customers = new List<string>();
    }

    // Finds the storage account the customers' links point at, so Your storage can preselect it.
    public static class StorageFinder
    {
        static readonly Regex Host = new Regex(@"^https://([a-z0-9]{3,24})\.blob\.core\.windows\.net(/|\?|$)", RegexOptions.IgnoreCase);

        public static string AccountOf(string link)
        {
            Match m = Host.Match((link ?? "").Trim());
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        // The accounts in the customers' links, the one most customers use first.
        public static List<UsedAccount> Used(IEnumerable<Customer> customers)
        {
            Dictionary<string, UsedAccount> by = new Dictionary<string, UsedAccount>(StringComparer.OrdinalIgnoreCase);
            List<UsedAccount> l = new List<UsedAccount>();
            foreach (Customer c in customers)
            {
                string a = AccountOf(c.AgentLocation);
                if (a == null) continue;
                UsedAccount u;
                if (!by.TryGetValue(a, out u)) { u = new UsedAccount(); u.Name = a; by[a] = u; l.Add(u); }
                if (!u.Customers.Contains(c.Name)) u.Customers.Add(c.Name);
            }
            l.Sort(delegate (UsedAccount x, UsedAccount y)
            {
                int n = y.Customers.Count.CompareTo(x.Customers.Count);
                return n != 0 ? n : string.Compare(x.Name, y.Name, StringComparison.Ordinal);
            });
            return l;
        }

        // The used accounts found in these subscriptions, the one most customers use first. A subscription that cannot
        // be read is passed over.
        public static List<FoundAccount> Find(Arm arm, List<AzureSubscription> subs, List<UsedAccount> used)
        {
            List<FoundAccount> l = new List<FoundAccount>();
            if (used.Count == 0) return l;
            foreach (AzureSubscription s in subs)
            {
                List<AzureAccount> accts;
                try { accts = arm.StorageAccounts(s.Id); }
                catch (SetupException) { continue; }
                foreach (AzureAccount a in accts)
                    foreach (UsedAccount u in used)
                        if (string.Equals(a.Name, u.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            FoundAccount f = new FoundAccount();
                            f.Subscription = s;
                            f.Account = a;
                            f.Customers = new List<string>(u.Customers);
                            l.Add(f);
                        }
            }
            l.Sort(delegate (FoundAccount x, FoundAccount y) { return y.Customers.Count.CompareTo(x.Customers.Count); });
            return l;
        }

        public static string UsedLine(List<string> customers)
        {
            if (customers == null || customers.Count == 0) return "";
            return "Used by your " + (customers.Count == 1 ? "customer " : "customers ") + string.Join(", ", customers.ToArray()) + ".";
        }

        // Your storage in Azure: by its resource ID when it is kept, otherwise by its name in the subscriptions of the sign-in.
        public static AzureAccount Locate(Arm arm, OwnStorage own)
        {
            if (!string.IsNullOrEmpty(own.Id))
            {
                AzureAccount a = new AzureAccount();
                a.Id = own.Id;
                a.Name = own.Account;
                a.BlobEndpoint = own.BlobEndpoint;
                a.Location = own.Region;
                Match m = Regex.Match(own.Id, "/resourceGroups/([^/]+)/", RegexOptions.IgnoreCase);
                a.ResourceGroup = m.Success ? m.Groups[1].Value : "";
                return a;
            }
            UsedAccount u = new UsedAccount();
            u.Name = own.Account;
            List<FoundAccount> f = Find(arm, arm.Subscriptions(), new List<UsedAccount>(new UsedAccount[] { u }));
            if (f.Count == 0) throw new SetupException("The storage account " + own.Account + " is not in the subscriptions this account can see. Sign in with an account that has Storage Account Contributor on it, or give its tenant.");
            own.Id = f[0].Account.Id;
            own.Region = f[0].Account.Location ?? "";
            if (string.IsNullOrEmpty(own.Subscription)) own.Subscription = f[0].Subscription.Name;
            return f[0].Account;
        }

        // A copy of your storage with the account key read for this run, when it is not kept on this PC. The copy is never saved.
        public static OwnStorage WithKey(Arm arm, OwnStorage own, DateTime nowUtc)
        {
            if (own.KeyKept) return own;
            AzureAccount a = Locate(arm, own);
            OwnStorage c = own.Copy();
            c.Key = arm.Key(a);
            // A publish link for this run only.
            c.AgentWrite = SasLinks.Container(c.BlobEndpoint, c.Account, Arm.AgentFiles, "cw", nowUtc.AddMinutes(-15), nowUtc.AddHours(2), c.Key);
            return c;
        }
    }

    // ---------- demo ----------

    // Made-up answers for the security check, the stored access policies and the search for your storage. A fix changes
    // the made-up account, so the check shows it after.
    public sealed class DemoStorageWeb : IWeb
    {
        public const string Sub = "7c3e9a51-2b4d-4f68-9e1a-0d5c7b3f2a86";
        readonly IWeb inner;
        readonly bool contoso;
        bool publicOff, containerSoft;
        string rules = "{\"name\":\"keep-backups\",\"enabled\":true,\"type\":\"Lifecycle\",\"definition\":{\"actions\":{\"baseBlob\":{\"tierToCool\":{\"daysAfterModificationGreaterThan\":30}}},\"filters\":{\"blobTypes\":[\"blockBlob\"],\"prefixMatch\":[\"backups/\"]}}}";
        public readonly List<WebCall> Calls = new List<WebCall>();

        // contoso: the subscriptions and accounts of Contoso, whose storage is contosotrackvice; otherwise the
        // answers of the Add customer demo.
        public DemoStorageWeb(bool contoso)
        {
            this.contoso = contoso;
            inner = new DemoAddCustomerWeb();
        }

        static string Account(string name, string group)
        {
            return "{\"id\":\"/subscriptions/" + Sub + "/resourceGroups/" + group + "/providers/Microsoft.Storage/storageAccounts/" + name + "\",\"name\":\"" + name
                + "\",\"location\":\"westeurope\",\"kind\":\"StorageV2\",\"properties\":{\"provisioningState\":\"Succeeded\",\"primaryEndpoints\":{\"blob\":\"https://" + name + ".blob.core.windows.net/\"}}}";
        }

        public WebReply Send(WebCall c)
        {
            Calls.Add(c);
            string u = c.Url;
            if (u.Contains("comp=acl"))
                return c.Method == "GET" ? new WebReply(200, "<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers><SignedIdentifier><Id>upload</Id><AccessPolicy><Start>2026-11-16T08:45:00Z</Start><Expiry>2028-11-15T09:00:00Z</Expiry><Permission>cw</Permission></AccessPolicy></SignedIdentifier><SignedIdentifier><Id>viewer</Id><AccessPolicy><Start>2026-11-16T08:45:00Z</Start><Expiry>2028-11-15T09:00:00Z</Expiry><Permission>rl</Permission></AccessPolicy></SignedIdentifier></SignedIdentifiers>") : new WebReply(200, "");
            if (c.Method == "DELETE" && u.Contains("restype=container")) return new WebReply(202, "");
            if (u.Contains("/managementPolicies/default"))
            {
                if (c.Method == "PUT")
                {
                    List<object> l = ImeJson.Get(ImeJson.Get(ImeJson.Get(ImeJson.Parse(c.Text) as Dictionary<string, object>, "properties") as Dictionary<string, object>, "policy") as Dictionary<string, object>, "rules") as List<object>;
                    List<string> parts = new List<string>();
                    if (l != null) foreach (object o in l) parts.Add(SetupJson.Write(o));
                    rules = string.Join(",", parts.ToArray());
                }
                return new WebReply(200, "{\"properties\":{\"policy\":{\"rules\":[" + rules + "]}}}");
            }
            if (u.Contains("/blobServices/default?"))
            {
                if (c.Method == "PUT" && c.Text.Contains("containerDeleteRetentionPolicy")) containerSoft = true;
                return new WebReply(200, "{\"properties\":{\"deleteRetentionPolicy\":{\"enabled\":true,\"days\":7},\"containerDeleteRetentionPolicy\":{\"enabled\":" + (containerSoft ? "true,\"days\":7" : "false") + "}}}");
            }
            Match m = Regex.Match(u, "/storageAccounts/([a-z0-9]+)\\?api-version", RegexOptions.IgnoreCase);
            if (m.Success && (c.Method == "GET" || c.Method == "PATCH"))
            {
                if (c.Method == "PATCH" && c.Text.Contains("allowBlobPublicAccess")) publicOff = true;
                return new WebReply(200, "{\"id\":\"/subscriptions/" + Sub + "/resourceGroups/rg-trackvice/providers/Microsoft.Storage/storageAccounts/" + m.Groups[1].Value + "\",\"name\":\"" + m.Groups[1].Value
                    + "\",\"location\":\"westeurope\",\"properties\":{\"supportsHttpsTrafficOnly\":true,\"minimumTlsVersion\":\"TLS1_2\",\"allowSharedKeyAccess\":true" + (publicOff ? ",\"allowBlobPublicAccess\":false" : "") + "}}");
            }
            if (contoso && u.StartsWith(Arm.Base + "/subscriptions?"))
                return new WebReply(200, "{\"value\":[{\"subscriptionId\":\"" + Sub + "\",\"displayName\":\"Contoso Production\",\"state\":\"Enabled\"},{\"subscriptionId\":\"2e8b4d17-6a3c-4f95-8b02-c1d7e9f4a365\",\"displayName\":\"Contoso Test\",\"state\":\"Enabled\"}]}");
            if (contoso && u.Contains("/resourcegroups?"))
                return new WebReply(200, "{\"value\":[{\"name\":\"rg-monitoring\",\"location\":\"westeurope\"},{\"name\":\"rg-trackvice\",\"location\":\"westeurope\"}]}");
            if (contoso && u.Contains("/providers/Microsoft.Storage/storageAccounts?"))
                return u.Contains(Sub) ? new WebReply(200, "{\"value\":[" + Account("contosologs", "rg-monitoring") + "," + Account("contosotrackvice", "rg-trackvice") + "]}") : new WebReply(200, "{\"value\":[]}");
            return inner.Send(c);
        }

        // Customers whose links point at contosotrackvice, for the search for your storage.
        public static List<Customer> Customers()
        {
            List<Customer> l = new List<Customer>();
            foreach (string n in new string[] { "Contoso", "Fabrikam" })
            {
                Customer c = new Customer();
                c.Name = n;
                c.AgentLocation = "https://contosotrackvice.blob.core.windows.net/" + BlobContainers.NameFor(n) + "?si=viewer&spr=https&sv=2022-11-02&sr=c&sig=demo";
                l.Add(c);
            }
            Customer w = new Customer();
            w.Name = "Woodgrove Bank";
            w.AgentLocation = "https://woodgroveagents.blob.core.windows.net/trackvice?sp=rl&sig=demo";
            l.Add(w);
            return l;
        }
    }
}
