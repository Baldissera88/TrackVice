namespace TrackVice
{
    using System;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    // Everything a device needs for one customer, in the single line $Customer of the platform script: where to upload,
    // where to get TrackVice, the key its versions are signed with, and the few settings. "tv1:" and base64url of JSON.
    // It holds a link that can only upload to the customer's container, so keep it like a password.
    public sealed class CustomerCode
    {
        public const string Prefix = "tv1:";
        public const int DefaultMaxAgeHours = 24, DefaultKeepHours = 48, DefaultMaxDays = 30;
        // A real code is about 1,500 characters. Anything much longer is not one.
        public const int MaxLength = 32768;
        // Ten years. A larger number is a mistake or made to overflow a date on the device.
        public const int MaxHours = 87600, MaxDaysLimit = 3650;

        public string Name;
        // Container SAS link with Create and Write: where the devices upload.
        public string Upload;
        // Container SAS link with Read on the agent container: agent/latest.json and agent/TrackVice-<version>.exe.
        public string Agent;
        // The public update key (CSP blob, base64) that agent/latest.json must be signed with.
        public string Key;
        // Optional webhook for alerts.
        public string Alert;
        public int MaxAgeHours = DefaultMaxAgeHours;
        public int KeepHours = DefaultKeepHours;
        public int MaxDays = DefaultMaxDays;
        // Turn on automatic time zone when the agent is installed. Codes made before it have no "tz" and leave it off.
        public bool AutoTimeZone;

        [DataContract(Namespace = "")]
        sealed class Doc
        {
            [DataMember(Name = "n", Order = 1, EmitDefaultValue = false)] public string Name;
            [DataMember(Name = "u", Order = 2)] public string Upload;
            [DataMember(Name = "a", Order = 3)] public string Agent;
            [DataMember(Name = "k", Order = 4)] public string Key;
            [DataMember(Name = "h", Order = 5, EmitDefaultValue = false)] public string Alert;
            [DataMember(Name = "m", Order = 6)] public int MaxAgeHours;
            [DataMember(Name = "keep", Order = 7)] public int KeepHours;
            [DataMember(Name = "d", Order = 8)] public int MaxDays;
            [DataMember(Name = "tz", Order = 9, EmitDefaultValue = false)] public bool AutoTimeZone;
        }

        // What is missing or wrong, or null.
        public string Problem()
        {
            if (string.IsNullOrWhiteSpace(Upload) || !Upload.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "The customer code has no upload link.";
            if (!CustomerRules.HttpsOk(Upload)) return "The upload link in the customer code is not a well-formed https address.";
            if (string.IsNullOrWhiteSpace(Agent) || !Agent.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "The customer code has no link to download TrackVice from.";
            if (!CustomerRules.HttpsOk(Agent)) return "The link to download TrackVice from in the customer code is not a well-formed https address.";
            if (string.IsNullOrWhiteSpace(Key)) return "The customer code has no update key.";
            string k = UpdateKeys.Problem(Key);
            if (k != null) return k;
            if (!string.IsNullOrEmpty(Alert) && !CustomerRules.HttpsOk(Alert)) return "The alert link in the customer code is not an https address.";
            if (MaxAgeHours < 0 || KeepHours <= 0 || MaxDays <= 0) return "The customer code has a setting below zero.";
            if (MaxAgeHours > MaxHours || KeepHours > MaxHours || MaxDays > MaxDaysLimit) return "The customer code has a setting that is too large.";
            return null;
        }

        public override string ToString()
        {
            Doc d = new Doc();
            d.Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
            d.Upload = Upload;
            d.Agent = Agent;
            d.Key = Key;
            d.Alert = string.IsNullOrWhiteSpace(Alert) ? null : Alert.Trim();
            d.MaxAgeHours = MaxAgeHours;
            d.KeepHours = KeepHours;
            d.MaxDays = MaxDays;
            d.AutoTimeZone = AutoTimeZone;
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Doc)).WriteObject(ms, d);
                return Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        public static CustomerCode Parse(string text)
        {
            string s = (text ?? "").Trim();
            if (!s.StartsWith(Prefix, StringComparison.Ordinal)) throw new ArgumentException("This is not a TrackVice customer code. It starts with " + Prefix);
            if (s.Length > MaxLength) throw new ArgumentException("The customer code is too long. Copy it again from TrackVice.");
            // Only base64url after the prefix. Line breaks from a mail or a wrapped paste are skipped, and padding at the end is allowed.
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            string body = s.Substring(Prefix.Length).TrimEnd('=');
            foreach (char ch in body)
            {
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') continue;
                if (ch == '-') sb.Append('+');
                else if (ch == '_') sb.Append('/');
                else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else throw new ArgumentException("The customer code could not be read. Copy it again from TrackVice.");
            }
            while (sb.Length % 4 != 0) sb.Append('=');
            string b = sb.ToString();
            Doc d;
            try
            {
                using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(b))) d = (Doc)new DataContractJsonSerializer(typeof(Doc)).ReadObject(ms);
            }
            catch (Exception) { throw new ArgumentException("The customer code could not be read. Copy it again from TrackVice."); }
            if (d == null) throw new ArgumentException("The customer code is empty.");
            CustomerCode c = new CustomerCode();
            c.Name = CleanName(d.Name);
            c.Upload = d.Upload;
            c.Agent = d.Agent;
            c.Key = d.Key;
            c.Alert = d.Alert;
            c.MaxAgeHours = d.MaxAgeHours;
            c.KeepHours = d.KeepHours > 0 ? d.KeepHours : DefaultKeepHours;
            c.MaxDays = d.MaxDays > 0 ? d.MaxDays : DefaultMaxDays;
            c.AutoTimeZone = d.AutoTimeZone;
            return c;
        }

        // The name is shown, written to the registry of the device and uploaded with each session: one short line of text.
        static string CleanName(string name)
        {
            if (name == null) return null;
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (char ch in name) if (!CustomerRules.Odd(ch.ToString())) b.Append(ch);
            string n = b.ToString().Trim();
            return n.Length > CustomerRules.MaxName ? n.Substring(0, CustomerRules.MaxName).Trim() : n;
        }
    }
}
