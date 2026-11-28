namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    // Customer codes, customers files and the lists kept on this PC, read from someone who is not to be trusted.
    public static class CustomerSecurityTests
    {
        public static void Run()
        {
            Codes();
            SealedFile();
            Rules();
            Import();
            Names();
            Recent();
        }

        static string Code(string json)
        {
            return CustomerCode.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static bool Refused(string code)
        {
            try { CustomerCode.Parse(code); return false; }
            catch (ArgumentException) { return true; }
        }

        static void Codes()
        {
            string key;
            using (RSACryptoServiceProvider rsa = UpdateKeys.Create()) key = UpdateKeys.PublicKey(rsa);
            CustomerCode c = new CustomerCode();
            c.Name = "Contoso";
            c.Upload = "https://contosostore.blob.core.windows.net/tv-contoso?sp=cw&sig=abc%2Bdef%3D";
            c.Agent = "https://contosostore.blob.core.windows.net/trackvice-agent?sp=r&sig=def";
            c.Key = key;
            string s = c.ToString();
            T.Eq(null, c.Problem(), "code security: a real code has no problem");
            T.Eq(c.Upload, CustomerCode.Parse(s.Substring(0, 40) + "\r\n" + s.Substring(40)).Upload, "code security: a code wrapped in a mail still reads");
            T.Eq(c.Upload, CustomerCode.Parse(s + "==").Upload, "code security: padding at the end still reads");

            T.Check(Refused("tv1:" + new string('A', CustomerCode.MaxLength + 4)), "code security: a huge code is refused before it is decoded");
            T.Check(Refused(s.Substring(0, 30) + "%" + s.Substring(30)), "code security: a character outside base64url is refused");
            T.Check(Refused(Code("{\"n\":\"" + new string('x', 40000) + "\",\"u\":\"https://a.example/c?sig=1\"}")), "code security: a code with a huge value is refused");

            CustomerCode n = CustomerCode.Parse(Code("{\"n\":\"Con\\u202Etoso\\r\\nEvil\\u0000" + new string('y', 200) + "\",\"u\":\"https://a.example/c\",\"a\":\"https://a.example/d\",\"k\":\"x\",\"m\":1,\"keep\":1,\"d\":1}"));
            T.Check(n.Name.IndexOf('\u202E') < 0 && n.Name.IndexOf('\n') < 0 && n.Name.IndexOf('\0') < 0, "code security: no control or direction characters in the name");
            T.Check(n.Name.Length <= CustomerRules.MaxName, "code security: the name is one short line");

            CustomerCode b = CustomerCode.Parse(s);
            b.Upload = "https://user:pass@evil.example/c?sig=1";
            T.Check(b.Problem() != null, "code security: an upload link with a user name is refused");
            b = CustomerCode.Parse(s);
            b.Agent = "https://evil.example/c d";
            T.Check(b.Problem() != null, "code security: an agent link with a space is refused");
            b = CustomerCode.Parse(s);
            b.Alert = "https://evil.example/\"x";
            T.Check(b.Problem() != null, "code security: an alert link with a quote is refused");
            b = CustomerCode.Parse(s);
            b.MaxDays = int.MaxValue;
            T.Check(b.Problem() != null, "code security: a number of days that overflows a date is refused");
            b = CustomerCode.Parse(s);
            b.KeepHours = int.MaxValue;
            T.Check(b.Problem() != null, "code security: a number of hours that overflows a date is refused");
        }

        static void SealedFile()
        {
            byte[] big = new byte[CustomerFile.MaxBytes + 16 + 1];
            big[0] = (byte)'T'; big[1] = (byte)'V'; big[2] = (byte)'C'; big[3] = (byte)'F';
            string msg = null;
            try { CustomerFile.Open(big, "password1"); }
            catch (InvalidDataException ex) { msg = ex.Message; }
            T.Check(msg != null && msg.IndexOf("too large", StringComparison.Ordinal) >= 0, "customers file: a huge file is refused before the key is derived");
        }

        static void Rules()
        {
            T.Check(CustomerRules.LocationOk(@"\\FS01\TrackVice$\Uploads"), "rules: a share");
            T.Check(CustomerRules.LocationOk(@"D:\TrackVice\Uploads"), "rules: a folder");
            T.Check(CustomerRules.LocationOk("https://a.blob.core.windows.net/c?sv=1&sig=a%2B"), "rules: a SAS link");
            T.Check(!CustomerRules.LocationOk("http://a.blob.core.windows.net/c?sig=1"), "rules: http is refused");
            T.Check(!CustomerRules.LocationOk("file://evil/share"), "rules: file: is refused");
            T.Check(!CustomerRules.LocationOk(@"\\?\UNC\evil\share"), "rules: \\\\?\\ is refused");
            T.Check(!CustomerRules.LocationOk(@"\\.\pipe\evil"), "rules: \\\\.\\ is refused");
            T.Check(!CustomerRules.LocationOk(@"relative\folder"), "rules: a relative folder is refused");
            T.Check(!CustomerRules.LocationOk("https://user@evil.example/c"), "rules: a user name before the host is refused");
            T.Check(CustomerRules.TenantOk("contoso.onmicrosoft.com") && CustomerRules.TenantOk("72f988bf-86f1-41af-91ab-2d7cd011db47"), "rules: tenant ID or domain");
            T.Check(!CustomerRules.TenantOk("evil.example/x?y") && !CustomerRules.TenantOk("../common"), "rules: a tenant is not a path");
            T.Check(CustomerRules.ServerOk("CM01.contoso.com") && CustomerRules.ServerOk("10.0.0.5"), "rules: server");
            T.Check(!CustomerRules.ServerOk(@"evil\root") && !CustomerRules.ServerOk("a b") && !CustomerRules.ServerOk("x'"), "rules: a server is not a path or a query");
            T.Check(CustomerRules.SiteOk("CTO") && !CustomerRules.SiteOk(@"C\T") && !CustomerRules.SiteOk("CTOX"), "rules: site code");
            T.Eq("https://a.example/c", CustomerRules.Where("https://a.example/c#sig=1"), "rules: what is shown has no token after #");
        }

        static void Import()
        {
            CustomerBook src = new CustomerBook(new MemoryCustomerStorage(), new DpapiLessProtector());
            Customer h = new Customer();
            h.Name = "Hostile";
            h.Tenant = "evil.example/../x";
            h.GraphClientId = "not-a-guid";
            h.SccmServer = @"evil\root\cimv2";
            h.SccmSite = "A'B";
            h.AgentLocation = @"\\?\UNC\evil\share";
            h.Notes = "note\u0007\u202Etext";
            h.AllowDownloads = true;
            src.Add(h);
            Customer g = new Customer();
            g.Name = "Good";
            g.Tenant = "contoso.onmicrosoft.com";
            g.GraphClientId = "6f1d3c2a-8b4e-4f7a-9c0d-2e5b7a1f3c94";
            g.SccmServer = "CM01.contoso.com";
            g.SccmSite = "CTO";
            g.AgentLocation = "https://contosoagents.blob.core.windows.net/trackvice?sv=2024-11-04&sp=rl&sig=SECRETSIG";
            src.Add(g);
            Customer bidi = new Customer();
            bidi.Name = "Con\u202Etoso";
            bidi.Tenant = "fabrikam.onmicrosoft.com";
            src.Add(bidi);
            string file = src.Export(true);

            CustomerBook dst = new CustomerBook(new MemoryCustomerStorage(), new DpapiLessProtector());
            Customer mine = new Customer();
            mine.Name = "Good";
            mine.AgentLocation = @"\\FS01\Mine";
            dst.Add(mine);

            ImportResult plan = dst.Preview(file);
            T.Eq(@"\\FS01\Mine", dst.Find("Good").AgentLocation, "import: the preview changes nothing");
            T.Eq(null, dst.Find("Hostile"), "import: the preview adds nothing");
            T.Check(plan.Connections.Count >= 4, "import: the preview lists the new places to connect to (" + plan.Connections.Count + ")");
            bool sig = false, uncShown = false;
            foreach (string l in plan.Connections)
            {
                if (l.IndexOf("SECRETSIG", StringComparison.Ordinal) >= 0 || l.IndexOf("sig=", StringComparison.OrdinalIgnoreCase) >= 0) sig = true;
                if (l.IndexOf(@"\\FS01\Mine", StringComparison.Ordinal) >= 0) uncShown = true;
            }
            T.Check(!sig, "import: the preview never shows a SAS token");
            T.Check(uncShown, "import: the preview shows what a changed location replaces");

            ImportResult r = dst.Import(file);
            Customer x = dst.Find("Hostile");
            T.Check(x != null, "import: the customer with bad values is added without them");
            T.Eq("", x.Tenant, "import: a tenant that is a path is left out");
            T.Eq("", x.GraphClientId, "import: a client ID that is not a GUID is left out");
            T.Eq("", x.SccmServer, "import: a server that is a WMI path is left out");
            T.Eq("", x.SccmSite, "import: a site code with a quote is left out");
            T.Eq("", x.AgentLocation, "import: a \\\\?\\ location is left out");
            T.Check(x.Notes.IndexOf('\u0007') < 0 && x.Notes.IndexOf('\u202E') < 0, "import: notes lose control and direction characters");
            T.Check(!x.AllowDownloads, "import: a file never turns downloads on");
            T.Eq(5, r.Ignored, "import: the values left out are counted");
            T.Eq(null, dst.Find("Con\u202Etoso"), "import: a name with direction characters is not added");
            Customer good = dst.Find("Good");
            T.Eq("contoso.onmicrosoft.com", good.Tenant, "import: a good tenant is kept");
            T.Eq("CM01.contoso.com", good.SccmServer, "import: a good server is kept");
            T.Eq(g.AgentLocation, good.AgentLocation, "import: a good SAS link is kept");

            StringBuilder many = new StringBuilder("{\"Kind\":\"TrackVice customers\",\"Version\":1,\"IncludesStorageLinks\":false,\"Customers\":[");
            for (int i = 0; i <= CustomerRules.MaxCustomers; i++) many.Append(i == 0 ? "" : ",").Append("{\"Name\":\"c").Append(i).Append("\"}");
            many.Append("]}");
            bool threw = false;
            try { new CustomerBook(new MemoryCustomerStorage(), new DpapiLessProtector()).Import(many.ToString()); }
            catch (InvalidDataException) { threw = true; }
            T.Check(threw, "import: a file with too many customers is refused");
            threw = false;
            try { new CustomerBook(new MemoryCustomerStorage(), new DpapiLessProtector()).Import(new string(' ', CustomerRules.MaxDocument + 1)); }
            catch (InvalidDataException) { threw = true; }
            T.Check(threw, "import: a huge document is refused before it is parsed");
        }

        static void Names()
        {
            string dir = Path.Combine(Path.GetTempPath(), "trackvice-names-sec-" + Guid.NewGuid().ToString("N"));
            try
            {
                NameCache c = new NameCache(dir);
                Dictionary<string, string> a = new Dictionary<string, string>();
                a["x\r\nb2d58f03-1e4c-4a6f-9b7d-3c0e2f5a8b13"] = "Injected";
                a["#read"] = "2000-01-01T00:00:00Z";
                a["0a2ade58-6391-4fb4-80c2-8153740fd068"] = "Real";
                DateTime t = new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
                c.Save("Contoso", a, t);
                DateTime read;
                Dictionary<string, string> back = c.Load("Contoso", out read);
                T.Check(!back.ContainsKey("b2d58f03-1e4c-4a6f-9b7d-3c0e2f5a8b13"), "names: an ID with a line break cannot add a name of its own");
                T.Eq(t, read, "names: an ID called #read does not change when the names were read");
                T.Eq("Real", back["0a2ade58-6391-4fb4-80c2-8153740fd068"], "names: a real ID is kept");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void Recent()
        {
            RecentStore s = new RecentStore(null);
            RecentDevice d = new RecentDevice();
            d.Source = RecentSources.Agent;
            d.Customer = "Contoso";
            d.Device = "PC\u0001\u0085\u2028-01";
            d.Id = "upload-1";
            RecentDevice kept = s.Touch(d);
            T.Check(kept != null && kept.Device.IndexOf('\u0001') < 0 && kept.Device.IndexOf('\u0085') < 0 && kept.Device.IndexOf('\u2028') < 0, "recent: no control or line separator characters are kept");
            string line = RecentStore.Format(kept);
            T.Check(RecentStore.Parse(line) != null && line.Split('\t').Length == 11, "recent: a kept device stays one line of eleven fields");
        }

        sealed class DpapiLessProtector : ICustomerProtector
        {
            public string Protect(string plain) { return "p:" + plain; }
            public string Unprotect(string stored) { return stored.Substring(2); }
        }
    }
}
