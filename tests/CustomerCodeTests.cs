namespace TrackVice.Tests
{
    using System;
    using System.Security.Cryptography;

    public static class CustomerCodeTests
    {
        public static void Run()
        {
            string key;
            using (RSACryptoServiceProvider rsa = UpdateKeys.Create()) key = UpdateKeys.PublicKey(rsa);
            CustomerCode c = new CustomerCode();
            c.Name = "Contoso";
            c.Upload = "https://contosostore.blob.core.windows.net/tv-contoso?sp=cw&sig=abc";
            c.Agent = "https://contosostore.blob.core.windows.net/trackvice-agent?sp=r&sig=def";
            c.Key = key;
            T.Eq(null, c.Problem(), "customer code: a full code has no problem");
            string s = c.ToString();
            T.Check(s.StartsWith("tv1:") && s.IndexOf('=') < 0 && s.IndexOf('+') < 0 && s.IndexOf('/') < 0, "customer code: one line of base64url after tv1:");
            CustomerCode p = CustomerCode.Parse(s);
            T.Eq("Contoso", p.Name, "customer code: name");
            T.Eq(c.Upload, p.Upload, "customer code: upload link");
            T.Eq(c.Agent, p.Agent, "customer code: agent link");
            T.Eq(key, p.Key, "customer code: key");
            T.Eq(null, p.Alert, "customer code: no alert");
            T.Eq(24, p.MaxAgeHours, "customer code: max age default");
            T.Eq(48, p.KeepHours, "customer code: keep default");
            T.Eq(30, p.MaxDays, "customer code: max days default");
            bool threw = false;
            try { CustomerCode.Parse("https://example"); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "customer code: anything else is refused");
            c.Upload = "";
            T.Check(c.Problem() != null, "customer code: no upload link is a problem");
        }
    }
}
