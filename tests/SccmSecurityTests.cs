namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // What comes from the site, a customer file or Graph is not trusted: WQL text, server names, keys, XML, links.
    public static class SccmSecurityTests
    {
        sealed class Site : ISccmQuery
        {
            public readonly List<string> Asked = new List<string>();
            public readonly List<Dictionary<string, object>> Rows = new List<Dictionary<string, object>>();

            public List<Dictionary<string, object>> Query(string wql)
            {
                Asked.Add(wql);
                if (wql.Contains("FROM SMS_TaskSequenceExecutionStatus")) return new List<Dictionary<string, object>>(Rows);
                return new List<Dictionary<string, object>>();
            }
        }

        sealed class Graph : IGraph
        {
            public readonly List<string> Asked = new List<string>();
            public string First;

            public Dictionary<string, object> Get(string url)
            {
                Asked.Add(url);
                return (Dictionary<string, object>)ImeJson.Parse(Asked.Count == 1 ? First : "{\"value\":[]}");
            }
        }

        public static void Run()
        {
            WqlText();
            WqlInQueries();
            ServerNames();
            Trust();
            Keys();
            SequenceLimits();
            AgentLine();
            GraphLinks();
            Csv();
        }

        static void WqlText()
        {
            T.Eq("abc", Wql.Text("abc"), "plain text is kept");
            T.Eq("O\\'Brien", Wql.Text("O'Brien"), "a quote is escaped");
            T.Eq("x\\\\", Wql.Text("x\\"), "a backslash at the end is escaped, so it cannot escape the closing quote");
            T.Eq("a\\\"b", Wql.Text("a\"b"), "a double quote is escaped");
            T.Eq("ab", Wql.Text("a\r\nb"), "control characters are dropped");
            T.Eq("", Wql.Text(null), "null is empty");
        }

        static void WqlInQueries()
        {
            Site s = new Site();
            Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            r["ResourceID"] = 16777001L;
            r["AdvertisementID"] = "ADV00001";
            r["PackageID"] = "P01\\";
            r["ExecutionTime"] = DateTime.UtcNow.AddMinutes(-5);
            r["LastStatusMsgID"] = 11140L;
            r["LastStatusMsgName"] = "The task sequence execution engine started execution of a task sequence.";
            s.Rows.Add(r);
            Dictionary<string, object> r2 = new Dictionary<string, object>(r, StringComparer.OrdinalIgnoreCase);
            r2["ResourceID"] = 16777002L;
            r2["PackageID"] = " OR Name = 'x";
            s.Rows.Add(r2);
            new SccmCatalog(s, "cm01").List(DateTime.UtcNow.AddHours(-1));
            string names = s.Asked.Find(delegate (string q) { return q.Contains("FROM SMS_TaskSequencePackage"); });
            T.Check(names != null && names.Contains("PackageID = 'P01\\\\'"), "a package ID that ends in a backslash stays inside its quotes: " + names);
            T.Check(names != null && names.Contains("PackageID = ' OR Name = \\'x'"), "a quote in a package ID is escaped, not dropped: " + names);

            Site f = new Site();
            new SccmFeed(f, 16777001, "ADV\\' OR ResourceID > 0 OR AdvertisementID = \\'", null, "PC", null, SccmWmi.Dmtf).Read(DateTime.UtcNow);
            string q0 = f.Asked[0];
            T.Check(q0.Contains("AdvertisementID = 'ADV\\\\\\' OR ResourceID > 0 OR AdvertisementID = \\\\\\''"), "a deployment ID cannot end the string in the query: " + q0);
        }

        static void ServerNames()
        {
            foreach (string ok in new string[] { "cm01", "cm01.contoso.com", "\\\\cm01.contoso.com", " CM01 ", "10.0.0.5", "fe80::1", "", null, "sccm_01.corp.local" })
                T.Check(SccmServerName.Problem(ok) == null, "a server name is taken: " + ok);
            foreach (string bad in new string[] { "\\\\evil\\share", "evil@SSL@443", "evil@80", "host/path", "a b", "http://evil", "host\\root\\cimv2", ".evil", "-evil", "a..b", "fe80::1%12", "evil:443", "host'x", "host\"x", new string('a', 260) })
                T.Check(SccmServerName.Problem(bad) != null, "not a server name: " + bad);
            T.Eq(".", SccmServerName.Normalize("  \\\\ "), "nothing is this PC");
            T.Check(SccmServerName.SiteCodeOk("P01") && SccmServerName.SiteCodeOk("abc"), "site codes are taken");
            foreach (string bad in new string[] { null, "", "P0", "P012", "P0'", "P0\\", "P 1" })
                T.Check(!SccmServerName.SiteCodeOk(bad), "not a site code: " + bad);
        }

        static void Trust()
        {
            Func<string, string> oldRead = SccmTrust.Read;
            Action<string, string> oldWrite = SccmTrust.Write;
            Dictionary<string, string> prefs = new Dictionary<string, string>();
            SccmTrust.Read = delegate (string n) { string v; return prefs.TryGetValue(n, out v) ? v : null; };
            SccmTrust.Write = delegate (string n, string v) { prefs[n] = v; };
            try
            {
                // An update: the server already used on this PC is taken as confirmed, nothing else.
                prefs[SccmTrust.ServerPref] = "CM01.contoso.com";
                SccmTrust.Seed();
                T.Check(SccmTrust.IsTrusted("cm01.contoso.com"), "the server used before the update is trusted");
                T.Check(SccmTrust.IsTrusted("\\\\CM01.CONTOSO.COM"), "the same server in another form is trusted");
                T.Check(!SccmTrust.IsTrusted("collector.evil.example"), "a server from an imported customer is not trusted");
                T.Check(SccmTrust.IsTrusted("."), "this PC is trusted");
                T.Check(SccmTrust.IsTrusted("localhost"), "localhost is trusted");

                // Seed runs once: a server that comes later through the setting is not trusted by it.
                prefs[SccmTrust.ServerPref] = "collector.evil.example";
                SccmTrust.Seed();
                T.Check(!SccmTrust.IsTrusted("collector.evil.example"), "a later server in the setting is not trusted by itself");

                bool threw = false;
                string msg = null;
                try { new SccmWmi("collector.evil.example", "P01").Connect(); }
                catch (InvalidOperationException ex) { threw = true; msg = ex.Message; }
                T.Check(threw && msg.Contains("has not connected"), "Connect refuses a server that was not confirmed, before anything is sent: " + msg);
                threw = false;
                try { SccmWmi.CheckTarget("\\\\evil\\share", null); }
                catch (InvalidOperationException) { threw = true; }
                T.Check(threw, "a path is not a server");
                threw = false;
                try { SccmWmi.CheckTarget("cm01.contoso.com", "P01' OR"); }
                catch (InvalidOperationException) { threw = true; }
                T.Check(threw, "a site code that is not three letters or digits is refused");

                SccmTrust.Add("collector.evil.example");
                T.Check(SccmTrust.IsTrusted("Collector.Evil.Example"), "a confirmed server is trusted");
                SccmWmi.CheckTarget("collector.evil.example", "P01");
                T.Check(true, "a confirmed server passes the check");
                SccmTrust.Add("bad|name");
                T.Check(!prefs[SccmTrust.PrefName].Contains("bad"), "a name that cannot be a server is not kept");

                // A fresh PC with nothing set trusts nothing.
                prefs.Clear();
                T.Check(!SccmTrust.IsTrusted("cm01.contoso.com"), "a fresh PC trusts no server");
                T.Eq("", prefs[SccmTrust.PrefName], "a fresh PC notes an empty list");
            }
            finally
            {
                SccmTrust.Read = oldRead;
                SccmTrust.Write = oldWrite;
            }
            T.Eq((object)"abc", SccmWmi.Clip("abc"), "a short value is kept");
            T.Eq(SccmWmi.MaxText, ((string)SccmWmi.Clip(new string('x', SccmWmi.MaxText + 10))).Length, "a huge text from the site is cut");
            T.Eq((object)5L, SccmWmi.Clip(5L), "a number is kept");
        }

        static void Keys()
        {
            string res, dep, ticks;
            SccmCatalog.SplitKey("16777001|ADV|01|638000000000000000", out res, out dep, out ticks);
            T.Eq("16777001", res, "the resource is the first part");
            T.Eq("ADV|01", dep, "a | in a deployment ID does not move the parts");
            T.Eq("638000000000000000", ticks, "the run is the last part");
            SccmCatalog.SplitKey("16777001|ADV", out res, out dep, out ticks);
            T.Check(res == "16777001" && dep == "ADV" && ticks == null, "a key without a run");
            SccmCatalog.SplitKey("16777001", out res, out dep, out ticks);
            T.Check(res == "16777001" && dep == null && ticks == null, "a key with only the resource");

            RemoteDeployment d = new RemoteDeployment();
            d.Key = "16777001|ADV|99999999999999999999";
            bool ok = true;
            try { new SccmCatalog(new Site(), "cm01").Open(d); }
            catch (Exception) { ok = false; }
            T.Check(ok, "a run time past the end of the calendar does not throw");
            d.Key = "16777001|ADV|" + (DateTime.MaxValue.Ticks + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ok = true;
            try { new SccmCatalog(new Site(), "cm01").Open(d); }
            catch (Exception) { ok = false; }
            T.Check(ok, "ticks just past DateTime.MaxValue do not throw");
        }

        static bool Refused(string xml, string what)
        {
            try { TsSequence.Parse(xml); return false; }
            catch (FormatException ex) { return ex.Message.Length > 0 && (what == null || ex.Message.Contains(what)); }
        }

        static void SequenceLimits()
        {
            StringBuilder deep = new StringBuilder("<sequence>");
            for (int i = 0; i < 5000; i++) deep.Append("<group name=\"g\">");
            deep.Append("<step name=\"s\"/>");
            for (int i = 0; i < 5000; i++) deep.Append("</group>");
            deep.Append("</sequence>");
            T.Check(Refused(deep.ToString(), "nested"), "groups nested without end are refused before they are walked");

            StringBuilder wide = new StringBuilder("<sequence>");
            for (int i = 0; i <= TsSequence.MaxNodes; i++) wide.Append("<step name=\"s\"/>");
            wide.Append("</sequence>");
            T.Check(Refused(wide.ToString(), "more than"), "a task sequence with too many steps is refused");

            string bomb = "<!DOCTYPE sequence [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><sequence><step name=\"&b;\"/></sequence>";
            T.Check(Refused(bomb, null), "a DTD is refused");

            StringBuilder ok = new StringBuilder("<sequence>");
            for (int i = 0; i < 30; i++) ok.Append("<group name=\"g\">");
            ok.Append("<step name=\"s\"/>");
            for (int i = 0; i < 30; i++) ok.Append("</group>");
            ok.Append("</sequence>");
            T.Eq(31, TsSequence.Parse(ok.ToString()).All.Count, "a deep but real task sequence is read");
        }

        static void AgentLine()
        {
            string code = CustomerCodeSample();
            T.Eq(".\\TrackVice.exe /agent-install /customer \"" + code + "\"", AgentCommand.Line(".\\TrackVice.exe /agent-install", code), "a real code makes the command");
            foreach (string bad in new string[] { "tv1:abc\"; Start-Process calc; \"", "tv1:abc`whoami`", "tv1:$(calc)", "tv1:a&b", "tv1:a|b", "tv1:a b", "tv1:a\r\nb", "tv1:a'b", null, "" })
                T.Check(AgentCommand.Line("TrackVice.exe /agent-start", bad) == null, "a code that could leave its quotes is not made into a command: " + bad);
        }

        static string CustomerCodeSample()
        {
            byte[] b = new byte[600];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 37);
            return "tv1:" + Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static void GraphLinks()
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["@odata.nextLink"] = "https://graph.microsoft.com/beta/x?$skiptoken=2";
            T.Eq("https://graph.microsoft.com/beta/x?$skiptoken=2", ReadinessCheck.NextLink(r), "a Graph link is followed");
            foreach (string bad in new string[] { "https://evil.example/x", "http://graph.microsoft.com/beta/x", "https://graph.microsoft.com.evil.example/x", "https://graph.microsoft.com@evil.example/x" })
            {
                r["@odata.nextLink"] = bad;
                T.Check(ReadinessCheck.NextLink(r) == null, "a link outside Graph is not followed with the sign-in: " + bad);
            }
            T.Check(ReadinessCheck.NextLink(new Dictionary<string, object>()) == null, "no link, no next page");

            Graph g = new Graph();
            g.First = "{\"value\":[{\"serialNumber\":\"CON1\"}],\"@odata.nextLink\":\"https://evil.example/steal\"}";
            List<AutopilotEntry> l = ReadinessCheck.ListIdentities(g, 100);
            T.Eq(1, g.Asked.Count, "the list stops at a link outside Graph");
            T.Eq(1, l.Count, "the first page is kept");
        }

        static void Csv()
        {
            ReadyDevice d = new ReadyDevice();
            d.Serial = "\t=cmd|' /c calc'!A0";
            d.Title = "  =HYPERLINK(\"http://evil\")";
            d.Add("-group", "@check", ReadyLevel.Ok, "+1");
            string csv = ReadyLines.ToCsv(new List<ReadyDevice> { d });
            string row = csv.Split(new string[] { "\r\n" }, StringSplitOptions.None)[1];
            T.Check(row.StartsWith("\"'"), "a tab before a formula does not start it: " + row);
            T.Check(row.Contains(",\"'  =HYPERLINK"), "spaces before a formula do not start it: " + row);
            T.Check(row.Contains(",\"'-group\",\"'@check\""), "each cell that starts with a sign is quoted: " + row);
            T.Check(!row.Contains("\t"), "no tab is left in a cell");
        }
    }
}
