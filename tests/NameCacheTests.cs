namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class NameCacheTests
    {
        const string ScriptId = "0a2ade58-6391-4fb4-80c2-8153740fd068", AppId = "b2d58f03-1e4c-4a6f-9b7d-3c0e2f5a8b13";

        public static void Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), "trackvice-names-" + Guid.NewGuid().ToString("N"));
            try
            {
                NameCache c = new NameCache(dir);
                DateTime read;
                T.Eq(0, c.Load("Contoso", out read).Count, "nothing kept yet");
                Dictionary<string, string> a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                a[ScriptId] = "Set time zone";
                a[AppId] = "Contoso VPN\tClient";
                DateTime t = new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
                T.Check(c.Save("Contoso", a, t), "names are saved");
                Dictionary<string, string> b = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                b["c3e69a14-2f5d-4b70-8c8e-4d1f306b9c24"] = "Company Portal";
                c.Save("Contoso", b, t.AddDays(1));
                Dictionary<string, string> back = c.Load("Contoso", out read);
                T.Eq(3, back.Count, "a later read adds to the names kept");
                T.Eq("Set time zone", back[ScriptId.ToUpperInvariant()], "IDs match whatever their case");
                T.Eq("Contoso VPN Client", back[AppId], "a tab in a name does not break the file");
                T.Eq(t.AddDays(1), read, "the time of the last read");
                T.Eq(0, c.Load("Fabrikam", out read).Count, "one customer's names are not another's");
                T.Eq(0, c.Load("contoso/../x", out read).Count, "a name with path characters stays in the folder");
                c.Delete("Contoso");
                T.Eq(0, c.Load("Contoso", out read).Count, "sign out deletes the names");

                Session s = new Session();
                Tracker tr = new Tracker(s);
                List<Obs> o = new List<Obs>();
                new ImeLogParser("IntuneManagementExtension").Read(new LogLine { Text = "[PowerShell] Processing policy with id = " + ScriptId + " for user 00000000-0000-0000-0000-000000000000", Utc = t, File = "x.log", Thread = "1" }, o);
                tr.ApplyAll(o);
                List<string> unknown = NameCache.Unknown(s, null);
                T.Eq(1, unknown.Count, "a script that shows as an ID is unknown");
                T.Eq(0, NameCache.Unknown(s, a).Count, "a script whose name is known is not");
                tr.AddNames(a);
                T.Eq(0, NameCache.Unknown(s, null).Count, "a named script is not unknown");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

            GraphClient g = GraphClient.Resume(null, "contoso.com", GraphClient.Scope + " " + GraphClient.ScriptScope, "rt-1", "admin@contoso.com");
            T.Check(g.SignedIn, "a kept sign-in counts as signed in");
            T.Eq("rt-1", g.RefreshToken, "the kept token");
            T.Check(g.CanReadScripts, "with its kept permissions");
            T.Eq("admin@contoso.com", g.Account, "and its account");
            T.Check(!new GraphClient(null, null).SignedIn, "a new client is not signed in");
        }
    }
}
