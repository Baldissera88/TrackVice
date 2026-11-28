namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Text;

    // Flaws that crossed files: one helper made safe, and the callers in other files that did not use it yet.
    public static class CrossFileSecurityTests
    {
        sealed class Web : IWeb
        {
            public int Status = 200;
            public string Text = "";
            public readonly List<WebCall> Calls = new List<WebCall>();

            public WebReply Send(WebCall c)
            {
                Calls.Add(c);
                return new WebReply(Status, Text);
            }
        }

        sealed class Site : ISccmQuery
        {
            public readonly DateTime When = DateTime.UtcNow.AddMinutes(-30);

            public List<Dictionary<string, object>> Query(string wql)
            {
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (wql.Contains("FROM SMS_TaskSequenceExecutionStatus"))
                {
                    r["ResourceID"] = 16777220L;
                    r["AdvertisementID"] = "CM120001";
                    r["PackageID"] = "CM100002";
                    r["ExecutionTime"] = When;
                    r["LastStatusMsgID"] = 11140L;
                    r["LastStatusMsgName"] = "The task sequence execution engine started";
                    r["ActionName"] = "Format\u0007 disk\r\nFAKE LINE";
                }
                else if (wql.Contains("FROM SMS_R_System"))
                {
                    r["ResourceID"] = 16777220L;
                    r["Name"] = "PC01\r\nInjected row\u0000" + new string('x', 5000);
                }
                else if (wql.Contains("FROM SMS_TaskSequencePackage"))
                {
                    r["PackageID"] = "CM100002";
                    r["Name"] = "Windows 11\u001b[31m";
                }
                else return l;
                l.Add(r);
                return l;
            }
        }

        public static void Run()
        {
            Requests();
            SignInPages();
            Launching();
            CustomerCodes();
            StoreCaps();
            Published();
            SccmRows();
            Lifetime();
            ShareDoc();
        }

        static void Requests()
        {
            HttpWebRequest req = Http.Request("https://graph.microsoft.com/beta/x");
            T.Check(!req.AllowAutoRedirect, "cross-file: a request that may carry a token does not follow a redirect by itself");
            int p = (int)ServicePointManager.SecurityProtocol;
            T.Check(p == 0 || (p & (48 | 192 | 768)) == 0, "cross-file: SSL 3.0, TLS 1.0 and TLS 1.1 are no longer offered after a request is made");
            T.Check(p == 0 || (p & 3072) != 0, "cross-file: TLS 1.2 is offered");
            T.Eq("abc", Http.ReadCapped(new StringReader("abc"), 3), "cross-file: an answer within the limit is read");
            T.Check(Http.ReadCapped(new StringReader(new string('a', 20000)), 10000) == null, "cross-file: an answer above the limit is not read");
            try
            {
                Dictionary<string, object> page = new Dictionary<string, object>();
                page["@odata.nextLink"] = "https://evil.example/beta/next";
                GraphLinks.Next(page);
                T.Check(false, "cross-file: a next page off Microsoft Graph stops the read");
            }
            catch (GraphException) { T.Check(true, "cross-file: a next page off Microsoft Graph stops the read"); }
        }

        static void SignInPages()
        {
            T.Eq("https://login.microsoft.com/device", GraphClient.SafeVerificationUri("https://login.microsoft.com/device"), "cross-file: the newer sign-in page is opened");
            T.Eq(GraphClient.DefaultVerificationUri, GraphClient.SafeVerificationUri("https://attacker.microsoft.com/devicelogin"), "cross-file: a sign-in page on a host the browser does not open is replaced");
            T.Eq(WebSignIn.DefaultVerificationPage, WebSignIn.VerificationPage("https://x.microsoftonline.com/devicelogin"), "cross-file: setup sign-in: only a page that is opened is shown");
            Web w = new Web();
            w.Text = "{\"device_code\":\"dc\",\"user_code\":\"ABCD\",\"verification_uri\":\"https://evil.example/devicelogin\",\"interval\":2147483647,\"expires_in\":9223372036854775807}";
            DeviceCode d = new WebSignIn(w, null, "11111111-2222-4333-8444-555555555555", "https://graph.microsoft.com/.default").Start();
            T.Check(d.Interval <= WebLinks.MaxInterval && d.ExpiresIn <= WebLinks.MaxExpires, "cross-file: setup sign-in: the wait the answer asks for is capped");
            T.Eq(WebSignIn.DefaultVerificationPage, d.VerificationUri, "cross-file: setup sign-in: another page is not opened");
        }

        static void Launching()
        {
            foreach (string ok in new string[] { "https://portal.azure.com/#home", "https://aka.ms/intune", "https://login.microsoftonline.com/common/oauth2/deviceauth", "https://microsoft.com/devicelogin", "https://endpoint.microsoft.com/" })
                T.Check(LaunchGuard.IsWebLink(ok), "cross-file: a Microsoft page TrackVice links to opens: " + ok);
            foreach (string bad in new string[] { "https://evil.example/", "https://login.microsoftonline.com.evil.example/", "calc.exe", "\\\\server\\share\\x.exe" })
                T.Check(!LaunchGuard.IsWebLink(bad), "cross-file: not opened: " + bad);
            T.Eq(AppUpdate.ReleasesUrl, LaunchGuard.WebLinkOr("https://github.com/x/y/releases/tag/1.0\" --x", AppUpdate.ReleasesUrl), "cross-file: a release link from the answer that is not clean falls back to the release page");
            T.Eq("https://github.com/Baldissera88/TrackVice/releases/tag/v2.1.1", LaunchGuard.WebLinkOr("https://github.com/Baldissera88/TrackVice/releases/tag/v2.1.1", AppUpdate.ReleasesUrl), "cross-file: a clean release link is kept");
            T.Check(LaunchGuard.IsWebLink("https://github.com/Baldissera88/TrackVice/issues/new?body=" + new string('a', Feedback.MaxIssueUrl - 70)), "cross-file: a new issue with a long report opens");
            // Environment.SystemDirectory is empty under Mono on Linux; on Windows it is C:\Windows\System32.
            T.Check(LaunchGuard.NotepadPath.EndsWith("notepad.exe", StringComparison.Ordinal) && (Environment.SystemDirectory.Length == 0 || Path.IsPathRooted(LaunchGuard.NotepadPath)), "cross-file: Notepad is started by its full path");
        }

        static void CustomerCodes()
        {
            const string code = "tv1:eyJ2IjoxLCJ1IjoiaHR0cHM6Ly9jb250b3NvLmJsb2IuY29yZS53aW5kb3dzLm5ldC94P3NpZz1hYmMifQ";
            string line = "TrackVice.exe /agent-install /customer \"" + code + "\"";
            string m = Secrets.Mask(line);
            T.Check(!m.Contains(code.Substring(4)) && m.Contains("tv1:" + Secrets.Hidden), "cross-file: a customer code is masked in a session, keeping tv1:");
            T.Eq(m, ShareMask.Text(line), "cross-file: Secrets and ShareMask hide a customer code the same way");
            T.Eq("the prefix tv1: alone", Secrets.Mask("the prefix tv1: alone"), "cross-file: the word tv1: alone is not a code");
            T.Check(Secrets.Mask("x tv1:" + new string('A', 20000)).Contains("tv1:" + Secrets.Hidden), "cross-file: a very long code is masked in time");
        }

        static void StoreCaps()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-crossfile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "agent", "requests"));
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "agent", "requests", "big.json"), new byte[DiagnosticsRequest.MaxBytes + 1]);
                FolderStore s = new FolderStore(dir);
                T.Check(s is ICappedStore, "cross-file: a folder store reads with a limit");
                bool refused = false;
                try { UpdateSource.Read(s, "agent/requests/big.json", DiagnosticsRequest.MaxBytes); }
                catch (StoreException) { refused = true; }
                T.Check(refused, "cross-file: a request file larger than a request is not read");
                T.Eq(DiagnosticsRequest.MaxBytes + 1, s.Get("agent/requests/big.json").Length, "cross-file: without a limit the file is read as before");
                T.Check(new AzureStore("https://contoso.blob.core.windows.net/c?sv=2022&sig=x") is ICappedStore, "cross-file: an Azure container reads with a limit");

                File.WriteAllBytes(Path.Combine(dir, "agent", "latest.json"), new byte[UpdateManifest.MaxJson + 10]);
                T.Check(ShareStorage.KeyProblem(dir, "<RSAKeyValue><Modulus>AQAB</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>") != null, "cross-file: a huge latest.json on the share is refused, not read");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }

            Web w = new Web();
            w.Text = new string('a', UpdateManifest.MaxJson + 1);
            WebContainer c = new WebContainer(w, "https://contoso.blob.core.windows.net/trackvice-agent?sv=2022&sig=x");
            bool big = false;
            try { c.Find(UpdateManifest.Name, UpdateManifest.MaxJson); }
            catch (StoreTooLargeException) { big = true; }
            T.Check(big, "cross-file: a latest.json larger than a manifest is not taken from the container");
            T.Check(c is ICappedStore, "cross-file: a container link reads with a limit");
        }

        static void Published()
        {
            Web w = new Web();
            w.Text = "{\"version\":\"2.1.0\\r\\nInjected\",\"sha256\":\"00\",\"size\":1,\"blob\":\"agent/x.exe\",\"signature\":\"x\"}";
            T.Check(OwnPublish.Published(w, "https://contoso.blob.core.windows.net/trackvice-agent?sv=2022&sig=x") == null, "cross-file: a published version that is not a version is not shown");
            w.Text = "{\"version\":\"2.1.0\",\"sha256\":\"00\",\"size\":1,\"blob\":\"agent/x.exe\",\"signature\":\"x\"}";
            T.Eq("2.1.0", OwnPublish.Published(w, "https://contoso.blob.core.windows.net/trackvice-agent?sv=2022&sig=x"), "cross-file: a real version is shown");
        }

        static void SccmRows()
        {
            List<RemoteDeployment> l;
            try { l = new SccmCatalog(new Site(), "cm01.contoso.com").List(DateTime.UtcNow.AddDays(-1)); }
            catch (Exception ex) { T.Check(false, "cross-file: the SCCM list could not be read in the test: " + ex.Message); return; }
            T.Eq(1, l.Count, "cross-file: the SCCM row is listed");
            if (l.Count != 1) return;
            T.Check(l[0].Device.IndexOf('\n') < 0 && l[0].Device.IndexOf('\0') < 0 && l[0].Device.Length <= Session.MaxName, "cross-file: a device name from the site is one clean line");
            T.Check(l[0].Profile.IndexOf('\u001b') < 0, "cross-file: a task sequence name from the site has no control characters");
            T.Check(l[0].StateText.IndexOf('\n') < 0 && l[0].StateText.IndexOf('\u0007') < 0, "cross-file: a step name from the site is one clean line");
        }

        static void Lifetime()
        {
            AgentLifetime l = new AgentLifetime(DateTime.UtcNow, int.MaxValue, int.MaxValue);
            l.DoneUtc = DateTime.UtcNow;
            DateTime until = DateTime.MinValue;
            try { until = l.UntilUtc; }
            catch (ArgumentOutOfRangeException) { }
            T.Check(until > DateTime.UtcNow, "cross-file: huge /keep and /max-days values do not overflow the date the agent ends");
            T.Eq(CustomerCode.MaxHours, l.KeepHours, "cross-file: /keep is at most ten years");
            T.Eq(CustomerCode.MaxDaysLimit, l.MaxDays, "cross-file: /max-days is at most ten years");
        }

        static void ShareDoc()
        {
            string path = Path.Combine("docs", "AGENT.md");
            if (!File.Exists(path)) return;
            string doc = File.ReadAllText(path, Encoding.UTF8);
            foreach (string line in ShareStorage.Icacls(ShareStorage.Example))
                T.Check(doc.Contains(line), "cross-file: docs/AGENT.md shows the icacls line TrackVice copies: " + line);
            T.Check(!doc.Contains("/remove:g \"Domain Computers\""), "cross-file: docs/AGENT.md no longer shows the old share lines");
            T.Check(doc.Contains("## Security in this release") && doc.Contains("SECURITY.md"), "cross-file: docs/AGENT.md says what changed for administrators");
        }
    }
}
