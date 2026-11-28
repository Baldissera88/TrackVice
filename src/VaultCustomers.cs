namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // One customer as a secret of the Key Vault.
    public sealed class VaultSecret
    {
        public string Name = "";
        // The real customer name, from the tag "name".
        public string RealName = "";
        public string ContentType = "";
        // The "updated" attribute, UTC. Null when the vault gave none.
        public DateTime? Updated;
        public string Value = "";
    }

    public enum SaveDecision
    {
        // No secret of that name in the vault yet.
        New,
        // The vault holds the same details already.
        Same,
        // The secret has not changed since the last sync.
        Replace,
        // The secret changed after the last sync: ask before writing over it.
        Newer
    }

    public sealed class VaultGetResult
    {
        public int Added, Updated, Skipped;
        // Customer codes in the vault that do not hold the update key of this PC, so they were not kept.
        public int CodesNotKept;
        public bool HadLinks;
    }

    // The rules for customers in a vault, without any network: names, JSON, merge and conflicts.
    // Customers are only added and updated. Nothing is ever deleted, in the vault or on the PC.
    public static class VaultCustomers
    {
        public const string ContentType = "application/vnd.trackvice.customer+json";
        public const string Prefix = "tv-";
        public const int MaxName = 127;
        // Key Vault takes values of up to 25 kilobytes.
        public const int MaxValue = 65536;
        public const string CodeMember = "CustomerCode";

        // "tv-" and the name with letters and digits only, in lower case. A name that cannot be written like that
        // without losing something gets a short hash at the end, so two names never share a secret.
        public static string SecretName(string customer)
        {
            string name = (customer ?? "").Trim();
            StringBuilder b = new StringBuilder();
            bool dash = false;
            foreach (char ch in name)
            {
                if (ch < 128 && char.IsLetterOrDigit(ch)) { b.Append(char.ToLowerInvariant(ch)); dash = false; }
                else if (!dash && b.Length > 0) { b.Append('-'); dash = true; }
            }
            string slug = b.ToString().TrimEnd('-');
            bool exact = slug.Length > 0 && slug == name.ToLowerInvariant() && Prefix.Length + slug.Length <= MaxName;
            if (exact) return Prefix + slug;
            string hash = Hash(name.ToLowerInvariant());
            int room = MaxName - Prefix.Length - 1 - hash.Length;
            if (slug.Length > room) slug = slug.Substring(0, room).TrimEnd('-');
            return slug.Length == 0 ? Prefix + hash : Prefix + slug + "-" + hash;
        }

        static string Hash(string s)
        {
            uint h = 2166136261;
            foreach (byte x in Encoding.UTF8.GetBytes(s)) { h ^= x; h *= 16777619; }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        // A name the vault allows for a secret: letters, digits and hyphens.
        public static bool IsSecretName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxName) return false;
            foreach (char ch in name) if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-')) return false;
            return true;
        }

        public static bool IsOurs(VaultSecret s)
        {
            return s != null && s.Name.StartsWith(Prefix, StringComparison.Ordinal)
                && string.Equals(s.ContentType, ContentType, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- the value of a secret ----------

        // The customer as Export writes it, with the storage link, and the customer code next to it.
        // The text is a customers file of one customer, so Import reads it too.
        public static string ToValue(Customer c, string code)
        {
            CustomerBook one = new CustomerBook(new MemoryCustomerStorage(), new Plain());
            one.Add(c);
            string json = Lines(one.Export(true)).TrimEnd();
            if (string.IsNullOrEmpty(code)) return json;
            int end = json.LastIndexOf('}');
            if (end < 0) return json;
            return json.Substring(0, end) + ",\"" + CodeMember + "\":" + AlertSender.Json(code) + "}";
        }

        // Line breaks between the members are all that differs between PCs, so they are written the same.
        static string Lines(string s)
        {
            return (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        sealed class Plain : ICustomerProtector
        {
            public string Protect(string plain) { return plain; }
            public string Unprotect(string stored) { return stored; }
        }

        // The name of the customer in a secret value, or null when it is not one of ours.
        public static string NameIn(string value)
        {
            Dictionary<string, object> d = Doc(value);
            System.Collections.IList l = ImeJson.Get(d, "Customers") as System.Collections.IList;
            if (l == null || l.Count == 0) return null;
            return Http.Str(l[0] as Dictionary<string, object>, "Name");
        }

        public static string CodeIn(string value)
        {
            string c = Http.Str(Doc(value), CodeMember);
            return string.IsNullOrEmpty(c) ? null : c;
        }

        // Null when the secret is what Save writes: one customer, under the secret name of that customer. Roles can be given
        // per secret, so a secret must not be able to change another customer than the one it is named for.
        public static string Problem(VaultSecret s)
        {
            if (s == null || s.Value == null) return "The secret is empty.";
            if (s.Value.Length > MaxValue) return "The secret is too long to be a customer.";
            System.Collections.IList l = ImeJson.Get(Doc(s.Value), "Customers") as System.Collections.IList;
            if (l == null || l.Count != 1) return "The secret does not hold one customer.";
            string name = Http.Str(l[0] as Dictionary<string, object>, "Name");
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0) return "The customer in the secret has no name.";
            if (!string.Equals(SecretName(name), s.Name, StringComparison.OrdinalIgnoreCase)) return "The secret is named for another customer than the one it holds.";
            return null;
        }

        // A customer code as TrackVice writes it, signed for updateKey, the public update key of this PC. Every code TrackVice
        // makes holds that key, and the devices installed with a code accept only versions signed with the key in it. A
        // secret of one customer can be changed by someone who may not publish, so a code with any other key is not kept:
        // it would let that person sign updates for the customer's devices.
        public static bool CodeFits(string code, string updateKey)
        {
            if (code == null || code.Length > MaxValue || string.IsNullOrEmpty(updateKey) || !code.StartsWith(CustomerCode.Prefix, StringComparison.Ordinal)) return false;
            foreach (char ch in code) if (ch <= ' ' || ch > '~') return false;
            CustomerCode c;
            try { c = CustomerCode.Parse(code); }
            catch (ArgumentException) { return false; }
            return c.Problem() == null && string.Equals((c.Key ?? "").Trim(), updateKey.Trim(), StringComparison.Ordinal);
        }

        static Dictionary<string, object> Doc(string value)
        {
            try { return ImeJson.Parse(value ?? "") as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        // ---------- get ----------

        // Adds and updates the customers of the secrets the way Import does. A secret that cannot be read is skipped.
        // A customer code is kept with ISecretStore when the secret has one that holds updateKey, the public update key of
        // this PC. Without a key on this PC no code is kept; getting the customers again after the setup keeps them.
        public static VaultGetResult Merge(CustomerBook book, IEnumerable<VaultSecret> secrets, ISecretStore codes, string updateKey)
        {
            VaultGetResult res = new VaultGetResult();
            foreach (VaultSecret s in secrets)
            {
                if (Problem(s) != null) { res.Skipped++; continue; }
                try
                {
                    ImportResult r = book.Import(s.Value);
                    res.Added += r.Added;
                    res.Updated += r.Updated;
                    if (r.HadLinks) res.HadLinks = true;
                    string name = NameIn(s.Value), code = CodeIn(s.Value);
                    Customer have = name == null ? null : book.Find(name);
                    if (codes != null && code != null && have != null)
                    {
                        if (CodeFits(code, updateKey)) CustomerCodes.Save(codes, have.Name, code);
                        else res.CodesNotKept++;
                    }
                    if (r.Added + r.Updated == 0) res.Skipped++;
                }
                catch (InvalidDataException) { res.Skipped++; }
            }
            return res;
        }

        // ---------- save ----------

        // What to do with one customer that is to be saved. lastSync is when this PC last read or wrote the vault.
        public static SaveDecision Decide(string localValue, VaultSecret remote, DateTime? lastSync)
        {
            if (remote == null) return SaveDecision.New;
            if (string.Equals(Lines(remote.Value), Lines(localValue), StringComparison.Ordinal)) return SaveDecision.Same;
            if (!lastSync.HasValue || !remote.Updated.HasValue || remote.Updated.Value > lastSync.Value) return SaveDecision.Newer;
            return SaveDecision.Replace;
        }

        // True when no secret of the list, but the ones just written, changed after the last sync.
        public static bool AllSeen(IEnumerable<VaultSecret> listed, ICollection<string> written, DateTime? lastSync)
        {
            foreach (VaultSecret s in listed)
            {
                if (written.Contains(s.Name)) continue;
                if (!lastSync.HasValue || !s.Updated.HasValue || s.Updated.Value > lastSync.Value) return false;
            }
            return lastSync.HasValue;
        }

        public static DateTime? ReadTime(string s)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return null;
        }

        public static string WriteTime(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        const long MaxEpoch = 253402300799;

        // The vault gives times as seconds since 1 January 1970.
        public static DateTime? FromEpoch(object seconds)
        {
            long? n = ImeJson.Long(seconds);
            // Past 31 December 9999 a DateTime throws, and the whole list would fail on one answer.
            if (!n.HasValue || n.Value <= 0 || n.Value > MaxEpoch) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(n.Value);
        }
    }
}
