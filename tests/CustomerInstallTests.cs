namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;

    public static class CustomerInstallTests
    {
        public static void Run()
        {
            SameAsSeparateOptions();
            Refused();
            ScriptForCode();
            TemplatesMatchFiles();
            PayloadAsTheScriptBuildsIt();
            ScriptsAreRobust();
        }

        static CustomerCode Code(string key)
        {
            CustomerCode c = new CustomerCode();
            c.Name = "Contoso";
            c.Upload = "https://contosostore.blob.core.windows.net/tv-contoso?sp=cw&sig=abc";
            c.Agent = "https://contosostore.blob.core.windows.net/trackvice-agent?sp=r&sig=def";
            c.Key = key;
            c.Alert = "https://contoso.example/alert";
            c.KeepHours = 12;
            c.MaxDays = 10;
            return c;
        }

        static string NewKey()
        {
            using (RSACryptoServiceProvider rsa = UpdateKeys.Create()) return UpdateKeys.PublicKey(rsa);
        }

        static void SameAsSeparateOptions()
        {
            string key = NewKey();
            CustomerCode c = Code(key);
            AgentOptions o = new AgentOptions();
            CustomerInstall.Apply(o, c.ToString());

            AgentOptions s = new AgentOptions();
            s.Upload = c.Upload;
            s.Alert = c.Alert;
            s.KeepHours = 12;
            s.MaxDays = 10;
            s.UpdateKey = key;
            s.UpdateLink = c.Agent;

            T.Eq(s.Upload, o.Upload, "Customer install: /upload from the code");
            T.Eq(s.Alert, o.Alert, "Customer install: /alert from the code");
            T.Eq(s.KeepHours, o.KeepHours, "Customer install: /keep from the code");
            T.Eq(s.MaxDays, o.MaxDays, "Customer install: /max-days from the code");
            T.Eq(s.UpdateKey, o.UpdateKey, "Customer install: /update-key from the code");
            T.Eq(s.UpdateLink, o.UpdateLink, "Customer install: /update is the agent container");
            T.Eq("Contoso", o.CustomerName, "Customer install: the customer name is kept");
            T.Eq(s.Mask, o.Mask, "Customer install: masking unchanged");
            T.Eq(AgentControl.ChildArgs(s), AgentControl.ChildArgs(o), "Customer install: /agent-start passes the same values on");

            c.Alert = null;
            c.Name = null;
            c.KeepHours = CustomerCode.DefaultKeepHours;
            c.MaxDays = CustomerCode.DefaultMaxDays;
            o = new AgentOptions();
            CustomerInstall.Apply(o, c.ToString());
            T.Eq(null, o.Alert, "Customer install: no alert without one in the code");
            T.Eq(null, o.CustomerName, "Customer install: no name without one in the code");
            T.Eq(48, o.KeepHours, "Customer install: keep default");
            T.Eq(30, o.MaxDays, "Customer install: max days default");
        }

        static bool Refuses(string code, string what)
        {
            try { CustomerInstall.Apply(new AgentOptions(), code); }
            catch (ArgumentException ex)
            {
                T.Check(ex.Message.Length > 0, what + ": says why");
                return true;
            }
            return false;
        }

        static void Refused()
        {
            T.Check(Refuses("", "Customer install: empty"), "Customer install: an empty code is refused");
            T.Check(Refuses("https://contosostore.blob.core.windows.net/tv-contoso?sp=cw", "Customer install: a link"), "Customer install: a link is refused");
            T.Check(Refuses("tv1:@@@", "Customer install: damaged"), "Customer install: a damaged code is refused");
            CustomerCode c = Code(NewKey());
            c.Key = "";
            T.Check(Refuses(c.ToString(), "Customer install: no key"), "Customer install: a code without a key is refused");
            c = Code(NewKey());
            c.Agent = "http://contosostore.blob.core.windows.net/trackvice-agent";
            T.Check(Refuses(c.ToString(), "Customer install: no https"), "Customer install: a code without an https agent link is refused");
        }

        static void ScriptForCode()
        {
            string code = Code(NewKey()).ToString();
            string s = CustomerScript.For(code);
            string[] a = AgentScriptTemplate.Text.Split('\n');
            string[] b = s.Split('\n');
            T.Eq(a.Length, b.Length, "Customer script: same number of lines");
            int changed = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] == b[i]) continue;
                changed++;
                T.Eq("$Customer = '" + code + "'   # The customer code from TrackVice > Devices > Add customer", b[i].TrimEnd('\r'), "Customer script: the $Customer line holds the code");
            }
            T.Eq(1, changed, "Customer script: only the $Customer line changes");
            T.Check(AgentScriptTemplate.Text.Contains("$Customer = ''   # The customer code"), "Customer script: the template has an empty $Customer");
            T.Check(CustomerScript.For(" " + code + " ").Contains("$Customer = '" + code + "'"), "Customer script: spaces around the code are left out");
        }

        // Run twice by Intune (device, then user) with a DNS failure in between: the second run must do nothing, and a download waits and retries.
        static void ScriptsAreRobust()
        {
            string[] names = { "customer", "links", "share" };
            string[] scripts = { AgentScriptTemplate.Text, AgentScriptTemplate.Links, AgentScriptTemplate.Share };
            for (int i = 0; i < scripts.Length; i++)
            {
                string s = scripts[i].Replace("\r\n", "\n");
                string n = "Robust " + names[i] + ": ";
                int idem = s.IndexOf("is already $state. Nothing to do.");
                int first = s.IndexOf("Invoke-WithRetry -HostName $");
                T.Check(idem > 0 && first > idem, n + "the installed check comes before the first download");
                T.Check(s.Contains("Join-Path (Join-Path $programFiles 'TrackVice Agent') 'TrackVice.exe'") && s.Contains("Get-ScheduledTask -TaskPath '\\' -TaskName 'TrackVice Agent'"), n + "checks the exe and the task of the agent");
                T.Check(s.Contains("$useInstalled = $true") && s.Contains("$exe = $agentExe"), n + "registers the task again from the installed exe");
                T.Check(s.Contains("[Net.Dns]::GetHostAddresses($HostName)") && s.Contains("[int]$Seconds = 180"), n + "waits for the name of the host");
                T.Check(s.Contains("for ($attempt = 1; $attempt -le 5; $attempt++)") && s.Contains("$delays = 5, 15, 30, 60"), n + "downloads up to 5 times with a backoff");
                T.Check(s.Contains("could not be downloaded from $HostName after 5 attempts: $reason"), n + "the last failure names the host and the error");
                T.Check(first > 0 && s.IndexOf("$null = Wait-ForHost -HostName $") < first, n + "waits for the host before the first download");
            }
            T.Check(!AgentScriptTemplate.Links.Contains("$installedHash") && !AgentScriptTemplate.Share.Contains("$installedHash"), "Robust: an installed agent is kept whatever its SHA-256, so it is never downgraded");
            T.Check(AgentScriptTemplate.Links.Contains("SecurityProtocol = [Net.SecurityProtocolType]::Tls12") && AgentScriptTemplate.Text.Contains("SecurityProtocol = [Net.SecurityProtocolType]::Tls12"), "Robust: TLS 1.2 is set");
            T.Check(!AgentScriptTemplate.Text.Contains("Invoke-WebRequest -Uri (Get-BlobLink"), "Robust: the customer script has no direct download");
        }

        static void SameFile(string name, string text)
        {
            string path = Path.Combine("agent", name);
            if (!File.Exists(path)) { T.Check(false, "Template: agent/" + name + " not found"); return; }
            string file = File.ReadAllText(path).Replace("\r\n", "\n");
            T.Check(file == text.Replace("\r\n", "\n"), "Template: the copy in the exe is the same as agent/" + name);
        }

        static void TemplatesMatchFiles()
        {
            SameFile("Install-TrackViceAgent.ps1", AgentScriptTemplate.Text);
            SameFile("Install-TrackViceAgent-Links.ps1", AgentScriptTemplate.Links);
            T.Check(AgentScriptTemplate.Links.Contains("$Download  = ''") && AgentScriptTemplate.Links.Contains("$UpdateLink = ''"), "Template: the links script keeps its values");
            T.Check(AgentScriptTemplate.Text.Contains("'/agent-install', '/customer'"), "Template: the script installs with /customer");
        }

        // The script rebuilds the signed text from the fields of agent/latest.json. Its $payload line is read here and
        // filled with the manifest's values, and must give exactly UpdateManifest.Payload().
        static void PayloadAsTheScriptBuildsIt()
        {
            UpdateManifest m = new UpdateManifest();
            m.Version = "9.9.9";
            m.Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            m.Size = 1234567;
            m.Blob = UpdateManifest.BlobFor(m.Version);
            m.PublishedUtc = new DateTime(2026, 11, 16, 9, 30, 5, DateTimeKind.Utc);
            string key;
            using (RSACryptoServiceProvider rsa = UpdateKeys.Create())
            {
                m.Sign(rsa);
                key = UpdateKeys.PublicKey(rsa);
            }
            string json = Encoding.UTF8.GetString(m.ToJson());
            string script = AgentScriptTemplate.Text;

            Match pub = Regex.Match(script, @"^\s*\$published = \[regex\]::Match\(\$raw, '([^']*)'\)\.Groups\[1\]\.Value", RegexOptions.Multiline);
            T.Check(pub.Success, "Payload: the script reads published from the text");
            string published = pub.Success ? Regex.Match(json, pub.Groups[1].Value).Groups[1].Value : "";
            T.Eq("2026-11-16T09:30:05Z", published, "Payload: published as the text in the json");

            Dictionary<string, string> values = new Dictionary<string, string>();
            values["version"] = m.Version;
            values["sha256"] = m.Sha256.ToUpperInvariant();
            values["size"] = m.Size.ToString(CultureInfo.InvariantCulture);
            values["blob"] = m.Blob;
            values["published"] = published;

            Match line = Regex.Match(script, @"^\s*\$payload = (.*)$", RegexOptions.Multiline);
            T.Check(line.Success, "Payload: the script has a $payload line");
            T.Check(script.Contains("$bytes = [Text.Encoding]::UTF8.GetBytes($payload -join \"`n\")"), "Payload: joined with a line feed and UTF-8");
            List<string> parts = new List<string>();
            foreach (string part in line.Groups[1].Value.TrimEnd('\r').Split(new string[] { ", " }, StringSplitOptions.None))
            {
                string text = Regex.Match(part, "'([^']*)'").Groups[1].Value;
                Match v = Regex.Match(part, @"\+ \$(\w+)");
                if (v.Success)
                {
                    string value;
                    T.Check(values.TryGetValue(v.Groups[1].Value, out value), "Payload: known field " + v.Groups[1].Value);
                    text += value;
                }
                parts.Add(text);
            }
            string built = string.Join("\n", parts.ToArray());
            T.Eq(Encoding.UTF8.GetString(m.Payload()), built, "Payload: the script builds the text that is signed");

            // Verified as the script does it: a plain provider with the public key imported.
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportCspBlob(Convert.FromBase64String(key));
                T.Check(rsa.VerifyData(Encoding.UTF8.GetBytes(built), "SHA256", Convert.FromBase64String(m.Signature)), "Payload: the signature matches the text the script builds");
                T.Check(!rsa.VerifyData(Encoding.UTF8.GetBytes(built.Replace("9.9.9", "9.9.8")), "SHA256", Convert.FromBase64String(m.Signature)), "Payload: another version does not match");
            }
        }
    }
}
