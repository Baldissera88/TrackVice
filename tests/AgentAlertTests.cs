namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Net;
    using System.Text;
    using System.Threading;

    public static class AgentAlertTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Masking();
            MaskedUpload();
            AlertsOnce();
            OldHistory();
            RetryAndCap();
            Payload();
            Loopback();
            LogTiming();
            BundleExtras();
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-alerts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Hidden(string input, string secret, string what)
        {
            string m = Secrets.Mask(input);
            T.Check(m.IndexOf(secret, StringComparison.Ordinal) < 0 && m.Contains(Secrets.Hidden), "mask hides: " + what + " -> " + m);
        }

        static void Kept(string input, string what)
        {
            T.Eq(input, Secrets.Mask(input), "mask keeps: " + what);
        }

        static void Masking()
        {
            // Hidden.
            Hidden("msiexec /i \"Agent.msi\" /qn PASSWORD=Hunter2x INSTALLDIR=C:\\Agent", "Hunter2x", "msiexec property");
            Hidden("msiexec /i app.msi SERVICE_PASSWORD=\"two words\" /qn", "two words", "quoted msiexec property");
            Hidden("msiexec /i office.msi PIDKEY=ABCDE-FGHIJ-KLMNO-PQRST-UVWXY /qn", "ABCDE-FGHIJ", "PIDKEY");
            Hidden("setup.exe /S LICENSEKEY=CONTOSO-1234-5678", "CONTOSO-1234", "license key");
            Hidden("powershell.exe -File join.ps1 -Password 'S3cr3t!' -Domain contoso.com", "S3cr3t!", "PowerShell -Password");
            Hidden("Connect-Thing -ClientSecret abc123def456 -TenantId 00000000-1111-2222-3333-444444444444", "abc123def456", "PowerShell -ClientSecret");
            Hidden("tool.exe --token ghp_ContosoFakeToken123 --verbose", "ghp_ContosoFakeToken123", "--token");
            Hidden("tool.exe /apikey 9f8e7d6c5b4a /silent", "9f8e7d6c5b4a", "/apikey");
            Hidden("Server=sql01.contoso.com;Database=Inv;User ID=svc;Password=P@ss;w0rd", "P@ss", "connection string password");
            Hidden("Server=sql01;Uid=svc;Pwd=Zz99xx;", "Zz99xx", "connection string pwd");
            Hidden("DefaultEndpointsProtocol=https;AccountName=contoso;AccountKey=bm90LWEtcmVhbC1rZXk=;EndpointSuffix=core.windows.net", "bm90LWEtcmVhbC1rZXk", "storage account key");
            Hidden("Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=send;SharedAccessKey=c2VjcmV0c2VjcmV0", "c2VjcmV0c2VjcmV0", "service bus key");
            Hidden("GET https://contoso.blob.core.windows.net/trackvice?sp=cw&st=2026-11-01&sig=AbCdEf%2BgHiJ%3D failed", "AbCdEf%2BgHiJ", "SAS sig");
            Hidden("https://login.contoso.com/cb?code=1&access_token=eyJ0eXAi.abc.def&state=x", "eyJ0eXAi", "access_token in a URL");
            Hidden("Calling https://api.contoso.com/v1?token=t0k3nValue", "t0k3nValue", "token in a URL");
            Hidden("Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", "eyJhbGciOiJSUzI1NiJ9", "bearer token");
            Hidden("Header Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA", "basic auth");
            Hidden("Downloading https://svc:Pa55word@files.contoso.com/app.zip", "Pa55word", "password in a URL");
            Hidden("{\"client_secret\": \"Q~fake.secret_value\", \"client_id\": \"1\"}", "Q~fake.secret_value", "JSON client_secret");
            Hidden("\"refresh_token\":\"0.ARfake\"", "0.ARfake", "JSON refresh token");
            Hidden("$pw = ConvertTo-SecureString \"Winter2026\" -AsPlainText -Force", "Winter2026", "ConvertTo-SecureString");
            Hidden("ConvertTo-SecureString -String 'Summer2026' -AsPlainText -Force", "Summer2026", "ConvertTo-SecureString -String");
            Hidden("cmd /c net user localadmin Spr1ng2026 /add", "Spr1ng2026", "net user password");
            Hidden("secret: hunter22 in config", "hunter22", "secret: value");
            Hidden("ProductKey=XXXXX-YYYYY-ZZZZZ", "XXXXX-YYYYY", "product key");
            Hidden("Password : Autumn2026", "Autumn2026", "spaced colon");

            // Kept as they are.
            Kept("Registry key HKLM\\SOFTWARE\\Contoso was created", "key as a word");
            Kept("The product key was accepted by the setup", "product key in a sentence");
            Kept("Key: {1B2C3D4E-0000-1111-2222-333344445555}", "a GUID after Key:");
            Kept("ProductCode={1B2C3D4E-0000-1111-2222-333344445555} UpgradeCode={9A8B7C6D-0000-1111-2222-333344445555}", "MSI GUIDs");
            Kept("SHA256 hash 3F2A9C0B4E1D7A6B5C4D3E2F1A0B9C8D7E6F5A4B3C2D1E0F9A8B7C6D5E4F3A2B matches", "a hash");
            Kept("Detection: file C:\\Program Files\\Contoso\\app.exe version 1.2.3", "a detection line");
            Kept("powershell.exe -ExecutionPolicy Bypass -File install.ps1 -Password $Password", "a variable, not a value");
            Kept("setup.exe PASSWORD=%ADMINPASS% /quiet", "an environment variable");
            Kept("Policy DevicePasswordEnabled = 0", "a password policy setting");
            Kept("MinDevicePasswordLength=8 PasswordExpirationDays=90", "password length and expiry settings");
            Kept("Getting token for user 00000000-1111-2222-3333-444444444444", "token as a word");
            Kept("POST https://login.contoso.com/tenant/oauth2/token returned 400", "token in a URL path");
            Kept("Basic authentication is not allowed", "Basic as a word");
            Kept("http://server01:8080/path/app.msi", "a port is not a password");
            Kept("Set-LocalUserPassword -UserName admin", "a cmdlet name");
            Kept("signature check passed for app.exe", "signature as a word");
            Kept("net user localadmin /delete", "net user without a password");
            Kept("Password=***", "already hidden");

            // A session: commands, events, item info and findings.
            Session s = new Session();
            Tracker t = new Tracker(s);
            Obs o = new Obs();
            o.TimeUtc = T0;
            o.Source = "test";
            o.ItemKey = "app";
            o.ItemName = "Contoso Agent";
            o.ItemKind = Kinds.Application;
            o.PhaseKey = Phases.SccmApps;
            o.State = State.Installing;
            o.Text = "Running msiexec /i agent.msi PASSWORD=Hunter2x";
            t.Apply(o);
            Item it = s.Find("app");
            it.Info.Add(new Pair("Command", "setup.exe -Token abcdef123456"));
            Attempt at = new Attempt();
            at.Command = "setup.exe /apikey 123abc456def";
            it.Attempts.Add(at);
            Session c = Secrets.MaskedCopy(s);
            string json = c.ToJson();
            T.Check(json.IndexOf("Hunter2x", StringComparison.Ordinal) < 0 && json.IndexOf("abcdef123456", StringComparison.Ordinal) < 0 && json.IndexOf("123abc456def", StringComparison.Ordinal) < 0, "masked copy hides event, info and command");
            T.Check(s.ToJson().Contains("Hunter2x"), "masked copy leaves the original session as it is");
        }

        static Session Sample(double failedAtMin, bool active)
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            Tracker t = new Tracker(s);
            List<Obs> l = new List<Obs>();
            l.Add(Ob("app1", failedAtMin - 2, State.Installing));
            l.Add(Ob("app1", failedAtMin, State.Failed));
            if (active) l.Add(Ob("app2", failedAtMin, State.Installing));
            t.ApplyAll(l);
            Finding f = new Finding();
            f.Kind = FindingKinds.Failed;
            f.Severity = 2;
            f.ItemKey = "app1";
            f.Title = "Contoso App failed";
            f.Message = "Exit code 1603. Command: setup.exe PASSWORD=Hunter2x";
            f.TimeUtc = T0.AddMinutes(failedAtMin);
            s.Findings.Add(f);
            Finding w = new Finding();
            w.Kind = FindingKinds.Retries;
            w.Severity = 1;
            w.ItemKey = "app1";
            w.Title = "Contoso App was tried 2 times";
            s.Findings.Add(w);
            return s;
        }

        static Obs Ob(string key, double min, State st)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddMinutes(min);
            o.Source = "test";
            o.ItemKey = key;
            o.ItemName = key == "app1" ? "Contoso App" : "Contoso Tools";
            o.ItemKind = Kinds.Application;
            o.PhaseKey = Phases.SccmApps;
            o.State = st;
            o.Text = "at " + min;
            return o;
        }

        static void MaskedUpload()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(dir);
                Session s = Sample(5, false);
                AgentOptions o = new AgentOptions();
                new Agent(st, "abc", T0).Cycle(s, T0.AddMinutes(10), o, true);
                string up = AgentData.Unpack(st.Get(AgentData.BlobName("abc"))).ToJson();
                T.Check(!up.Contains("Hunter2x") && up.Contains(Secrets.Hidden), "the agent masks before it uploads");
                T.Check(s.Findings[0].Message.Contains("Hunter2x"), "the agent's own session is not changed");
                o.Mask = false;
                new Agent(st, "abc", T0).Cycle(s, T0.AddMinutes(10), o, true);
                T.Check(AgentData.Unpack(st.Get(AgentData.BlobName("abc"))).ToJson().Contains("Hunter2x"), "/no-mask uploads the text as it is");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void AlertsOnce()
        {
            string dir = TempDir();
            try
            {
                string state = AgentAlerts.StatePath(dir);
                List<Alert> got = new List<Alert>();
                AgentAlerts a = new AgentAlerts(state, "s1", T0.AddHours(-2), delegate (Alert x) { got.Add(x); }, null);
                Session s = Sample(5, true);
                a.Cycle(s, false, T0.AddMinutes(6));
                T.Eq(1, got.Count, "one alert for one failure, none for a warning");
                T.Check(got[0].Text.IndexOf("Hunter2x", StringComparison.Ordinal) < 0, "alert text is masked");
                // The retry count in the title changes; that is not a new alert.
                s.Findings[1].Title = "Contoso App was tried 3 times";
                s.Findings[0].Title = "Contoso App failed again";
                a.Cycle(s, false, T0.AddMinutes(7));
                T.Eq(1, got.Count, "the same failure is not sent twice");

                // A restart: a new agent with the same session and file.
                List<Alert> after = new List<Alert>();
                AgentAlerts b = new AgentAlerts(state, "s1", T0.AddHours(-2), delegate (Alert x) { after.Add(x); }, null);
                b.Cycle(Sample(5, true), false, T0.AddMinutes(30));
                T.Eq(0, after.Count, "no alert again after a restart");
                T.Eq(1, b.SentCount, "the sent count survives a restart");

                // Finished: something ran, then nothing does.
                Session done = Sample(5, false);
                b.Cycle(done, true, T0.AddMinutes(60));
                T.Eq(1, after.Count, "finished alert after activity seen before a restart");
                T.Check(after.Count == 1 && after[0].Title.Contains("finished with 1 failed"), "finished alert says how many failed");
                b.Cycle(done, true, T0.AddMinutes(61));
                T.Eq(1, after.Count, "finished is sent once");

                // A new session on the same device starts fresh.
                List<Alert> other = new List<Alert>();
                new AgentAlerts(state, "s2", T0.AddHours(-2), delegate (Alert x) { other.Add(x); }, null).Cycle(Sample(5, true), false, T0.AddMinutes(6));
                T.Eq(1, other.Count, "another session id ignores the old file");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void OldHistory()
        {
            string dir = TempDir();
            try
            {
                string state = AgentAlerts.StatePath(dir);
                List<Alert> got = new List<Alert>();
                // The agent starts 30 days after the failure.
                DateTime start = T0.AddDays(30);
                AgentAlerts a = new AgentAlerts(state, "s1", start.AddHours(-2), delegate (Alert x) { got.Add(x); }, null);
                Session s = Sample(5, false);
                a.Cycle(s, false, start);
                a.Cycle(s, true, start.AddHours(1));
                T.Eq(0, got.Count, "no alert for old history on a device enrolled long ago");
                T.Check(!File.Exists(state), "nothing is recorded for old history");
                // The same item failing again now still alerts.
                Session n = Sample(30 * 24 * 60 + 5, true);
                a.Cycle(n, false, start.AddMinutes(10));
                T.Eq(1, got.Count, "a new failure on the same device alerts");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void RetryAndCap()
        {
            int calls = 0;
            List<string> log = new List<string>();
            AgentAlerts a = new AgentAlerts(null, "s1", DateTime.MinValue, delegate (Alert x) { calls++; if (calls < 3) throw new WebException("offline"); }, delegate (string m) { log.Add(m); });
            Session s = Sample(5, true);
            DateTime now = T0.AddMinutes(6);
            a.Cycle(s, false, now);
            T.Eq(1, calls, "first try");
            a.Cycle(s, false, now.AddSeconds(30));
            T.Eq(1, calls, "no retry before the wait");
            a.Cycle(s, false, now.AddMinutes(1));
            T.Eq(2, calls, "second try after a minute");
            a.Cycle(s, false, now.AddMinutes(3));
            T.Eq(3, calls, "third try after two more");
            T.Eq(1, a.SentCount, "sent at the third try");
            T.Eq(0, a.PendingCount, "nothing waits after a success");
            T.Check(log.Count >= 3 && log[0].StartsWith("Alert not sent, trying again", StringComparison.Ordinal), "failed sends are logged");

            int tries = 0;
            AgentAlerts g = new AgentAlerts(null, "s1", DateTime.MinValue, delegate (Alert x) { tries++; throw new WebException("offline"); }, null);
            for (int m = 0; m < 30; m++) g.Cycle(s, false, now.AddMinutes(m));
            T.Eq(AgentAlerts.MaxTries, tries, "gives up after a few tries");
            T.Eq(0, g.PendingCount, "a given up alert is dropped");

            int sent = 0;
            AgentAlerts c = new AgentAlerts(null, "s1", DateTime.MinValue, delegate (Alert x) { sent++; }, null);
            Session big = new Session();
            big.Computer = "CONTOSO-PC02";
            for (int k = 0; k < 40; k++)
            {
                Finding f = new Finding();
                f.Kind = FindingKinds.Failed;
                f.Severity = 2;
                f.Title = "App " + k + " failed";
                f.TimeUtc = now;
                big.Findings.Add(f);
            }
            c.Cycle(big, false, now);
            T.Eq(AgentAlerts.MaxPerDevice, sent, "alerts are capped per device");
        }

        static void Payload()
        {
            Alert a = new Alert();
            a.Level = "error";
            a.Title = "CONTOSO-PC01: \"App\" failed";
            a.Text = "Line 1\r\nLine 2\tTab \\ back" + (char)1 + new string('x', 5000);
            a.Facts.Add(new Pair("Device", "CONTOSO-PC01"));
            string p = AlertSender.Payload(a);
            Dictionary<string, object> d = ImeJson.Parse(p) as Dictionary<string, object>;
            T.Check(d != null, "payload is valid JSON");
            T.Check(p.Contains("\\\"App\\\"") && p.Contains("\\n") && p.Contains("\\t") && p.Contains("\\u0001") && p.Contains("\\\\ back"), "payload escapes quotes, new lines, tabs, control characters and backslashes");
            T.Check(p.Length < 12000, "long texts are cut");
            T.Check(p.Contains("application/vnd.microsoft.card.adaptive") && p.Contains("\"type\":\"message\""), "payload is a Teams workflow message with an adaptive card");
            T.Check(AlertSender.Problem("http://hooks.contoso.com/x") != null, "plain http is refused");
            T.Check(AlertSender.Problem("ftp://contoso.com/x") != null, "other schemes are refused");
            T.Check(AlertSender.Problem("not a link") != null, "text is refused");
            T.Check(AlertSender.Problem("https://contoso.example/workflows/1") == null, "https is allowed");
            T.Check(AlertSender.Problem("http://127.0.0.1:8080/x") == null, "http to this computer is allowed");
            bool threw = false;
            try { AlertSender.Send("http://hooks.contoso.com/x", a); } catch (StoreException) { threw = true; }
            T.Check(threw, "sending over plain http is refused");
        }

        static void Loopback()
        {
            HttpListener l = null;
            string prefix = null;
            Random r = new Random();
            for (int k = 0; k < 20 && l == null; k++)
            {
                prefix = "http://127.0.0.1:" + r.Next(20000, 60000) + "/hook/";
                HttpListener x = new HttpListener();
                x.Prefixes.Add(prefix);
                try { x.Start(); l = x; }
                catch (Exception) { try { x.Close(); } catch (Exception) { } }
            }
            if (l == null)
            {
                T.Check(false, "could not start a listener on 127.0.0.1");
                return;
            }
            string body = null, type = null, method = null;
            Thread th = new Thread(delegate ()
            {
                try
                {
                    HttpListenerContext c = l.GetContext();
                    method = c.Request.HttpMethod;
                    type = c.Request.ContentType;
                    using (StreamReader rd = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = rd.ReadToEnd();
                    c.Response.StatusCode = 202;
                    c.Response.Close();
                }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
            try
            {
                Alert a = new Alert();
                a.Level = "error";
                a.Title = "CONTOSO-PC01: Contoso App failed";
                a.Text = "Exit code 1603 \u00E6\u00F8\u00E5";
                a.Facts.Add(new Pair("Device", "CONTOSO-PC01"));
                AlertSender.Send(prefix, a);
                th.Join(5000);
                T.Eq("POST", method, "alert is a POST");
                T.Check(type != null && type.StartsWith("application/json", StringComparison.Ordinal), "alert is JSON");
                Dictionary<string, object> d = body == null ? null : ImeJson.Parse(body) as Dictionary<string, object>;
                T.Check(d != null, "posted body parses as JSON");
                T.Eq("message", d == null ? null : Http.Str(d, "type"), "posted type");
                T.Check(body != null && body.Contains("Exit code 1603 \u00E6\u00F8\u00E5") && body.Contains("CONTOSO-PC01: Contoso App failed"), "posted title and text, in UTF-8");
            }
            catch (Exception ex) { T.Check(false, "alert to a local listener: " + ex.Message); }
            finally { l.Close(); }
        }

        static void LogTiming()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(dir);
                int made = 0;
                List<string> log = new List<string>();
                byte[] small = new byte[] { 80, 75, 5, 6 };
                bool tooBig = false;
                LogUploads u = new LogUploads(st, AgentData.BlobName("abc"), delegate (Session x) { made++; return tooBig ? null : small; }, delegate (string m) { log.Add(m); });
                T.Eq("logs/abc.zip", u.Name, "bundle name next to the session");

                Session running = Sample(5, true);
                Session ok = new Session();
                new Tracker(ok).ApplyAll(new List<Obs> { Ob("app2", 1, State.Installing) });
                DateTime now = T0;
                T.Check(!u.Cycle(ok, false, false, now), "nothing to send while it simply runs");
                T.Check(u.Cycle(running, false, false, now.AddMinutes(1)), "sent at the first failure");
                T.Check(File.Exists(Path.Combine(dir, "logs", "abc.zip")), "bundle is in the store");
                T.Check(!u.Cycle(running, false, false, now.AddMinutes(2)), "a failure is a reason only once");
                Session idle = Sample(5, false);
                T.Check(!u.Cycle(idle, true, false, now.AddMinutes(5)), "going idle within 10 minutes waits");
                T.Check(!u.Cycle(idle, true, false, now.AddMinutes(10)), "still within 10 minutes");
                T.Check(u.Cycle(idle, true, false, now.AddMinutes(11)), "going idle is sent once 10 minutes have passed");
                T.Check(!u.Cycle(idle, true, false, now.AddMinutes(40)), "staying idle is no reason");
                T.Check(u.Cycle(idle, true, true, now.AddMinutes(60)), "final upload");
                T.Eq(3, u.Uploads, "three bundles");

                tooBig = true;
                LogUploads v = new LogUploads(st, AgentData.BlobName("def"), delegate (Session x) { return null; }, delegate (string m) { log.Add(m); });
                T.Check(!v.Cycle(running, false, true, now), "a bundle that is too large is not sent");
                T.Check(log[log.Count - 1].StartsWith("Logs not uploaded", StringComparison.Ordinal), "a skipped bundle is logged");

                File.WriteAllText(Path.Combine(dir, "file.txt"), "not a folder");
                LogUploads w = new LogUploads(new FolderStore(Path.Combine(dir, "file.txt")), AgentData.BlobName("ghi"), delegate (Session x) { return small; }, delegate (string m) { log.Add(m); });
                T.Check(!w.Cycle(running, false, true, now), "a failed upload returns false and does not throw");
                T.Check(log[log.Count - 1].StartsWith("Logs not uploaded", StringComparison.Ordinal), "a failed upload is logged");
                T.Check(!w.Cycle(running, false, true, now.AddMinutes(1)), "a failed upload waits 10 minutes");
                T.Eq(3, made, "the bundle is made only when due");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void BundleExtras()
        {
            string dir = TempDir();
            try
            {
                string ime = Path.Combine(dir, "ime");
                string ccm = Path.Combine(dir, "ccm");
                Directory.CreateDirectory(ime);
                Directory.CreateDirectory(ccm);
                File.WriteAllText(Path.Combine(ime, "IntuneManagementExtension.log"), "ime");
                File.WriteAllText(Path.Combine(ime, "AgentExecutor.log"), "exec");
                File.WriteAllText(Path.Combine(ime, "notes.txt"), "no");
                File.WriteAllText(Path.Combine(ccm, "smsts.log"), "ts");
                File.WriteAllText(Path.Combine(ccm, "AppEnforce.log"), "no");
                Session s = new Session();
                s.Computer = "CONTOSO-PC01";
                string zip = Path.Combine(dir, "b.zip");
                int n = Report.Bundle(s, zip, new string[][] { new string[] { ime, "*.log" }, new string[] { ccm, "smsts*.log" }, new string[] { Path.Combine(dir, "nothere"), "*.log" } });
                T.Eq(3, n, "bundle holds the extra logs that are there");
                List<string> names = new List<string>();
                using (FileStream fs = File.OpenRead(zip))
                using (ZipArchive a = new ZipArchive(fs, ZipArchiveMode.Read)) foreach (ZipArchiveEntry e in a.Entries) names.Add(e.Name);
                T.Check(names.Contains("session.json") && names.Contains("smsts.log") && names.Contains("AgentExecutor.log") && !names.Contains("notes.txt") && !names.Contains("AppEnforce.log"), "bundle names: " + string.Join(",", names.ToArray()));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
