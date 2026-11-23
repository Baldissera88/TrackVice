namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    // The connection details of one customer: an Intune tenant, where its agents upload to and an SCCM site.
    public sealed class Customer
    {
        public string Name = "";
        // The SAS link of the storage container or a folder. It is a credential.
        public string AgentLocation = "";
        public string Tenant = "";
        public string GraphClientId = "";
        public string SccmServer = "";
        public string SccmSite = "";
        public string Notes = "";
        // Allows the Download item for scripts and app details. Off until it is turned on for this customer.
        public bool AllowDownloads;
        // Download without the question first. Off means TrackVice asks before each download.
        public bool DownloadsWithoutAsking;

        public Customer Copy()
        {
            return (Customer)MemberwiseClone();
        }

        public bool IsEmpty
        {
            get { return Blank(AgentLocation) && Blank(Tenant) && Blank(GraphClientId) && Blank(SccmServer) && Blank(SccmSite) && Blank(Notes); }
        }

        // True when a Graph sign-in made for a may be used for b. An empty tenant means the tenant of
        // whichever account signs in, so it is never taken to be the same.
        public static bool SameSignIn(Customer a, Customer b)
        {
            if (a == null || b == null) return false;
            if (Blank(a.Tenant) || Blank(b.Tenant)) return false;
            return string.Equals(a.Tenant.Trim(), b.Tenant.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Norm(a.GraphClientId), Norm(b.GraphClientId), StringComparison.OrdinalIgnoreCase);
        }

        static string Norm(string s)
        {
            return (s ?? "").Trim().Trim('{', '}');
        }

        internal static bool Blank(string s)
        {
            return string.IsNullOrEmpty(s) || s.Trim().Length == 0;
        }

        internal void Fix()
        {
            Name = (Name ?? "").Trim();
            AgentLocation = (AgentLocation ?? "").Trim();
            Tenant = (Tenant ?? "").Trim();
            GraphClientId = (GraphClientId ?? "").Trim();
            SccmServer = (SccmServer ?? "").Trim();
            SccmSite = (SccmSite ?? "").Trim().ToUpperInvariant();
            Notes = Notes ?? "";
        }
    }

    // Where the customer document of this Windows user is kept.
    public interface ICustomerStorage
    {
        // Null when nothing has been saved yet.
        string Read();
        void Write(string document);
    }

    // Encrypts the credential parts of a customer before they are stored.
    public interface ICustomerProtector
    {
        string Protect(string plain);
        string Unprotect(string stored);
    }

    public sealed class MemoryCustomerStorage : ICustomerStorage
    {
        public string Document;

        public string Read() { return Document; }
        public void Write(string document) { Document = document; }
    }

    // Windows DPAPI for the current user: only this user on this computer can read it back.
    public sealed class DpapiProtector : ICustomerProtector
    {
        public string Protect(string plain)
        {
            byte[] enc = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(plain ?? ""), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public string Unprotect(string stored)
        {
            byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
    }

    [DataContract(Name = "Customer", Namespace = "")]
    sealed class CustomerRecord
    {
        [DataMember(Order = 1)] public string Name;
        [DataMember(Order = 2, EmitDefaultValue = false)] public string Tenant;
        [DataMember(Order = 3, EmitDefaultValue = false)] public string GraphClientId;
        [DataMember(Order = 4, EmitDefaultValue = false)] public string SccmServer;
        [DataMember(Order = 5, EmitDefaultValue = false)] public string SccmSite;
        [DataMember(Order = 6, EmitDefaultValue = false)] public string AgentLocation;
        [DataMember(Order = 7, EmitDefaultValue = false)] public string AgentLocationProtected;
        [DataMember(Order = 8, EmitDefaultValue = false)] public string Notes;
        [DataMember(Order = 9, EmitDefaultValue = false)] public bool AllowDownloads;
        [DataMember(Order = 10, EmitDefaultValue = false)] public bool DownloadsWithoutAsking;
    }

    [DataContract(Name = "Customers", Namespace = "")]
    sealed class CustomerDocument
    {
        [DataMember(Order = 1)] public string Kind;
        [DataMember(Order = 2)] public int Version;
        [DataMember(Order = 3, EmitDefaultValue = false)] public string Current;
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Note;
        [DataMember(Order = 5)] public bool IncludesStorageLinks;
        [DataMember(Order = 6)] public List<CustomerRecord> Customers;
    }

    public sealed class ImportResult
    {
        public int Added, Updated;
        public bool HadLinks;
    }

    // The saved customers of this Windows user, and which one is in use.
    public sealed class CustomerBook
    {
        public const string DefaultName = "Default";
        const string DocKind = "TrackVice customers";
        readonly ICustomerStorage storage;
        readonly ICustomerProtector protector;
        readonly List<Customer> list = new List<Customer>();
        string current;

        public CustomerBook(ICustomerStorage storage, ICustomerProtector protector)
        {
            this.storage = storage;
            this.protector = protector;
        }

        // Sorted by name.
        public List<Customer> All
        {
            get
            {
                List<Customer> l = new List<Customer>(list);
                l.Sort(delegate (Customer a, Customer b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
                return l;
            }
        }

        public int Count
        {
            get { return list.Count; }
        }

        public Customer Current
        {
            get { return Find(current); }
        }

        public void SetCurrent(string name)
        {
            Customer c = Find(name);
            current = c == null ? null : c.Name;
        }

        public Customer Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Trim();
            foreach (Customer c in list) if (string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // Null when the name can be used, otherwise what is wrong with it.
        public string NameProblem(string name, Customer except)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return "Enter a name for the customer.";
            if (n.Length > 80) return "The name is too long. Use at most 80 characters.";
            Customer other = Find(n);
            if (other != null && other != except) return "There is already a customer called " + other.Name + ".";
            return null;
        }

        public Customer Add(Customer details)
        {
            Customer c = details.Copy();
            c.Fix();
            string p = NameProblem(c.Name, null);
            if (p != null) throw new ArgumentException(p);
            list.Add(c);
            if (current == null) current = c.Name;
            return c;
        }

        public void Rename(string oldName, string newName)
        {
            Customer c = Find(oldName);
            if (c == null) throw new ArgumentException("There is no customer called " + oldName + ".");
            string p = NameProblem(newName, c);
            if (p != null) throw new ArgumentException(p);
            bool wasCurrent = current != null && string.Equals(current, c.Name, StringComparison.OrdinalIgnoreCase);
            c.Name = newName.Trim();
            if (wasCurrent) current = c.Name;
        }

        // Replaces every detail of the customer called name, its name too.
        public Customer Update(string name, Customer details)
        {
            Customer c = Find(name);
            if (c == null) throw new ArgumentException("There is no customer called " + name + ".");
            Customer d = details.Copy();
            d.Fix();
            string p = NameProblem(d.Name, c);
            if (p != null) throw new ArgumentException(p);
            bool wasCurrent = current != null && string.Equals(current, c.Name, StringComparison.OrdinalIgnoreCase);
            c.Name = d.Name;
            c.AgentLocation = d.AgentLocation;
            c.Tenant = d.Tenant;
            c.GraphClientId = d.GraphClientId;
            c.SccmServer = d.SccmServer;
            c.SccmSite = d.SccmSite;
            c.Notes = d.Notes;
            c.AllowDownloads = d.AllowDownloads;
            c.DownloadsWithoutAsking = d.DownloadsWithoutAsking;
            if (wasCurrent) current = c.Name;
            return c;
        }

        public bool Remove(string name)
        {
            Customer c = Find(name);
            if (c == null) return false;
            list.Remove(c);
            if (current != null && string.Equals(current, c.Name, StringComparison.OrdinalIgnoreCase)) current = null;
            return true;
        }

        // Reads the saved document. False when nothing was saved yet.
        public bool Load()
        {
            list.Clear();
            current = null;
            string doc = storage.Read();
            if (string.IsNullOrEmpty(doc)) return false;
            CustomerDocument d = Parse(doc);
            if (d.Customers != null)
            {
                foreach (CustomerRecord r in d.Customers)
                {
                    Customer c = FromRecord(r);
                    if (r != null && !string.IsNullOrEmpty(r.AgentLocationProtected))
                    {
                        try { c.AgentLocation = protector.Unprotect(r.AgentLocationProtected); }
                        catch (Exception) { c.AgentLocation = ""; }
                    }
                    if (c.Name.Length == 0 || Find(c.Name) != null) continue;
                    list.Add(c);
                }
            }
            SetCurrent(d.Current);
            return true;
        }

        public void Save()
        {
            CustomerDocument d = NewDocument();
            d.Current = Current == null ? null : Current.Name;
            foreach (Customer c in All)
            {
                CustomerRecord r = ToRecord(c);
                if (c.AgentLocation.Length > 0) r.AgentLocationProtected = protector.Protect(c.AgentLocation);
                d.Customers.Add(r);
            }
            storage.Write(Write(d));
        }

        // On first run, the single set of values TrackVice kept before becomes the customer Default.
        public bool Migrate(Customer legacy)
        {
            if (storage.Read() != null || list.Count > 0) return false;
            if (legacy == null || legacy.IsEmpty)
            {
                Save();
                return false;
            }
            Customer c = legacy.Copy();
            c.Name = DefaultName;
            Add(c);
            current = c.Name;
            Save();
            return true;
        }

        // The storage links are credentials, so they are only written when asked for.
        public string Export(bool includeLinks)
        {
            CustomerDocument d = NewDocument();
            d.IncludesStorageLinks = includeLinks;
            d.Note = includeLinks
                ? "This file holds the storage links of the agents. Anyone who has it can read what the agents uploaded."
                : "The storage links of the agents are left out. Export with Include the storage links to keep them.";
            foreach (Customer c in All)
            {
                CustomerRecord r = ToRecord(c);
                if (includeLinks && c.AgentLocation.Length > 0) r.AgentLocation = c.AgentLocation;
                d.Customers.Add(r);
            }
            return Write(d);
        }

        // Adds the customers of an export. A customer with a name that exists already gets the details
        // the file has; an empty value in the file leaves the saved one as it is.
        public ImportResult Import(string json)
        {
            CustomerDocument d = Parse(json);
            ImportResult res = new ImportResult();
            if (d.Customers == null) return res;
            foreach (CustomerRecord r in d.Customers)
            {
                Customer c = FromRecord(r);
                if (c.Name.Length == 0 || c.Name.Length > 80) continue;
                // A file never turns downloads on. It is turned on in Settings on this PC.
                c.AllowDownloads = false;
                c.DownloadsWithoutAsking = false;
                if (c.AgentLocation.Length > 0) res.HadLinks = true;
                Customer have = Find(c.Name);
                if (have == null)
                {
                    Add(c);
                    res.Added++;
                    continue;
                }
                if (c.AgentLocation.Length > 0) have.AgentLocation = c.AgentLocation;
                if (c.Tenant.Length > 0) have.Tenant = c.Tenant;
                if (c.GraphClientId.Length > 0) have.GraphClientId = c.GraphClientId;
                if (c.SccmServer.Length > 0) have.SccmServer = c.SccmServer;
                if (c.SccmSite.Length > 0) have.SccmSite = c.SccmSite;
                if (c.Notes.Length > 0) have.Notes = c.Notes;
                res.Updated++;
            }
            return res;
        }

        // Made-up customers for the demo and for screenshots.
        public static CustomerBook Demo()
        {
            CustomerBook b = new CustomerBook(new MemoryCustomerStorage(), new PlainProtector());
            Customer a = new Customer();
            a.Name = "Contoso";
            a.Tenant = "contoso.onmicrosoft.com";
            a.AgentLocation = "https://contosoagents.blob.core.windows.net/trackvice?sv=2024-11-04&sp=rl&sig=demo";
            a.SccmServer = "CM01.contoso.com";
            a.SccmSite = "CTO";
            a.Notes = "Autopilot pre-provisioning in the Copenhagen office.";
            b.Add(a);
            Customer f = new Customer();
            f.Name = "Fabrikam";
            f.Tenant = "fabrikam.onmicrosoft.com";
            f.GraphClientId = "6f1d3c2a-8b4e-4f7a-9c0d-2e5b7a1f3c94";
            f.AgentLocation = @"\\FS01\TrackVice$\Uploads";
            b.Add(f);
            Customer w = new Customer();
            w.Name = "Woodgrove Bank";
            w.SccmServer = "SCCM.woodgrove.local";
            w.SccmSite = "WGB";
            b.Add(w);
            b.SetCurrent("Contoso");
            return b;
        }

        sealed class PlainProtector : ICustomerProtector
        {
            public string Protect(string plain) { return plain; }
            public string Unprotect(string stored) { return stored; }
        }

        static CustomerDocument NewDocument()
        {
            CustomerDocument d = new CustomerDocument();
            d.Kind = DocKind;
            d.Version = 1;
            d.Customers = new List<CustomerRecord>();
            return d;
        }

        static CustomerRecord ToRecord(Customer c)
        {
            CustomerRecord r = new CustomerRecord();
            r.Name = c.Name;
            r.Tenant = Empty(c.Tenant);
            r.GraphClientId = Empty(c.GraphClientId);
            r.SccmServer = Empty(c.SccmServer);
            r.SccmSite = Empty(c.SccmSite);
            r.Notes = Empty(c.Notes);
            r.AllowDownloads = c.AllowDownloads;
            r.DownloadsWithoutAsking = c.DownloadsWithoutAsking;
            return r;
        }

        static Customer FromRecord(CustomerRecord r)
        {
            Customer c = new Customer();
            if (r != null)
            {
                c.Name = r.Name;
                c.Tenant = r.Tenant;
                c.GraphClientId = r.GraphClientId;
                c.SccmServer = r.SccmServer;
                c.SccmSite = r.SccmSite;
                c.AgentLocation = r.AgentLocation;
                c.Notes = r.Notes;
                c.AllowDownloads = r.AllowDownloads;
                c.DownloadsWithoutAsking = r.DownloadsWithoutAsking;
            }
            c.Fix();
            return c;
        }

        static string Empty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        static DataContractJsonSerializer Serializer()
        {
            return new DataContractJsonSerializer(typeof(CustomerDocument));
        }

        static string Write(CustomerDocument d)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (System.Xml.XmlDictionaryWriter w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true))
                {
                    Serializer().WriteObject(w, d);
                    w.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        static CustomerDocument Parse(string json)
        {
            CustomerDocument d;
            try
            {
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                    d = (CustomerDocument)Serializer().ReadObject(ms);
            }
            catch (Exception) { d = null; }
            if (d == null || d.Kind != DocKind) throw new InvalidDataException("This is not a list of TrackVice customers.");
            return d;
        }
    }
}
