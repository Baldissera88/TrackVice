namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    // Values put into the install scripts and command lines that run as SYSTEM on every device.
    public static class InstallScriptSecurityTests
    {
        public static void Run()
        {
            Literals();
            CustomerScriptLine();
            FilledLinksScript();
            TaskSequenceCode();
            SourceFolder();
            InstalledCheck();
            CustomerTool();
        }

        // Reads a single-quoted PowerShell string at the start of text, as PowerShell does: ' and the typographic quotes
        // U+2018 to U+201B end it, and two of them in a row are one quote. Returns the value, and in rest what follows the string.
        static string ReadLiteral(string text, out string rest)
        {
            rest = null;
            if (text.Length == 0 || !CustomerScript.IsQuote(text[0])) return null;
            StringBuilder b = new StringBuilder();
            int i = 1;
            while (i < text.Length)
            {
                char c = text[i];
                if (CustomerScript.IsQuote(c))
                {
                    if (i + 1 < text.Length && CustomerScript.IsQuote(text[i + 1])) { b.Append(text[i + 1]); i += 2; continue; }
                    rest = text.Substring(i + 1);
                    return b.ToString();
                }
                b.Append(c);
                i++;
            }
            return null;
        }

        static readonly string[] Hostile =
        {
            "it's",
            "x' ; Start-Process calc ; '",
            "x\u2019; Start-Process calc; \u2018",
            "x\u2018\u2019\u201A\u201B; iex $env:X; '",
            "x''' + (calc) + '''",
            "\u2019",
            "a\nb\r\n$Sha256 = 'x'",
        };

        static void Literals()
        {
            foreach (string v in Hostile)
            {
                string lit = CustomerScript.Literal(v);
                string rest;
                string back = ReadLiteral(lit, out rest);
                T.Eq(v, back, "Script literal: the value comes back whole: " + v.Replace("\n", "\\n").Replace("\r", "\\r"));
                T.Eq("", rest, "Script literal: nothing follows the string: " + v.Replace("\n", "\\n").Replace("\r", "\\r"));
                T.Eq(lit, AgentScript.Quote(v), "Script literal: the setup quotes the same way: " + v.Replace("\n", "\\n"));
            }
            T.Eq("'ab'", CustomerScript.Literal("a\0b"), "Script literal: a NUL is left out");
            T.Eq("''", CustomerScript.Literal(null), "Script literal: null is empty");
        }

        static string ValueOf(string script, string name)
        {
            foreach (string line in script.Split('\n'))
            {
                if (!line.StartsWith("$" + name + " ", StringComparison.Ordinal) && !line.StartsWith("$" + name + "=", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                string after = line.Substring(eq + 1).TrimStart(' ');
                string rest;
                string v = ReadLiteral(after, out rest);
                if (v == null) return null;
                // After the string only the comment may follow.
                string tail = rest.Trim();
                return tail.Length == 0 || tail.StartsWith("#", StringComparison.Ordinal) ? v : null;
            }
            return null;
        }

        static void CustomerScriptLine()
        {
            string code = "tv1:abc\u2019; Start-Process calc; #";
            string s = CustomerScript.For(code);
            T.Eq(code, ValueOf(s, "Customer"), "Customer script: a typographic quote in the code stays in $Customer");
            T.Check(s.IndexOf("\u2019\u2019", StringComparison.Ordinal) > 0, "Customer script: the typographic quote is doubled");
        }

        static void FilledLinksScript()
        {
            ScriptValues v = new ScriptValues();
            v.Download = "https://a.blob.core.windows.net/trackvice-agent/TrackVice.exe?sig=\u2018x";
            v.Sha256 = "ABCD";
            v.Upload = "https://a.blob.core.windows.net/trackvice?sig=y\u2019; Start-Process calc; \u2019";
            v.Alert = "https://contoso.example/hook\u201B + (calc) + \u201A";
            v.UpdateKey = "KEY'";
            v.UpdateLink = "\\\\fs01\\it\u2019s";
            string s = AgentScript.Fill(AgentScriptTemplate.Links, v);
            T.Eq(v.Download, ValueOf(s, "Download"), "Links script: $Download stays one string");
            T.Eq(v.Upload, ValueOf(s, "Upload"), "Links script: $Upload stays one string");
            T.Eq(v.Alert, ValueOf(s, "Alert"), "Links script: $Alert stays one string");
            T.Eq(v.UpdateKey, ValueOf(s, "UpdateKey"), "Links script: $UpdateKey stays one string");
            T.Eq(v.UpdateLink, ValueOf(s, "UpdateLink"), "Links script: $UpdateLink stays one string");
            string sh = AgentScript.Fill(AgentScriptTemplate.Share, v);
            T.Eq(v.Upload, ValueOf(sh, "Upload"), "Share script: $Upload stays one string");
        }

        static string RealCode()
        {
            CustomerCode c = new CustomerCode();
            c.Name = "Contoso \"Ltd\" & Sons";
            c.Upload = "https://contosostore.blob.core.windows.net/tv-contoso?sp=cw&sig=abc%2B";
            c.Agent = "https://contosostore.blob.core.windows.net/trackvice-agent?sp=r&sig=def";
            using (RSACryptoServiceProvider rsa = UpdateKeys.Create()) c.Key = UpdateKeys.PublicKey(rsa);
            c.Alert = "https://contoso.example/alert?a=1&b=\"2\"";
            return c.ToString();
        }

        static void TaskSequenceCode()
        {
            string code = RealCode();
            T.Check(TsAgentPackage.SafeCode(code), "TS code: a customer code TrackVice makes goes into the command line");
            T.Check(TsAgentPackage.SafeCode("tv1:made-up-code-for-contoso"), "TS code: the made-up codes of the tests and demo");
            string[] bad = { null, "", "tv1:", "TV1:abc", "abc", "tv1:abc\" /update \"\\\\evil\\share", "tv1:abc def", "tv1:abc&calc", "tv1:abc\r\ncalc", "tv1:abc%PATH%", "tv1:" + new string('a', 20000) };
            foreach (string b in bad) T.Check(!TsAgentPackage.SafeCode(b), "TS code: refused: " + (b == null ? "null" : b.Length > 40 ? b.Substring(0, 40) : b).Replace("\r", "\\r").Replace("\n", "\\n"));

            MemorySmsSite site = MemorySmsSite.Demo(false, null);
            MemoryFiles files = new MemoryFiles();
            string share = "\\\\cm01.contoso.com\\Sources\\TrackVice";
            files.Folders[share] = new List<string>();
            int asked = 0;
            TsAgentSetup setup = new TsAgentSetup(site, files, new MemoryCopies(), "1.5.1", delegate (string q) { asked++; return true; });
            TsAgentState st = setup.Read(null);
            string pkg = setup.MakePackage(st, share, new List<SmsDpGroup>()).PackageId;
            T.Check(pkg != null, "TS code: the package is made");
            site.Writes.Clear();
            asked = 0;
            string id = "P0100231";
            string before = site.Xml[id];
            TsResult r = setup.AddSteps(id, "x", pkg, "tv1:abc\" /update \"\\\\evil\\share");
            T.Check(!r.Done && r.Text == TsAgentPackage.BadCode, "TS code: a code with a quote is not put in the task sequence");
            T.Eq(0, site.Writes.Count, "TS code: nothing is written to the site");
            T.Eq(0, asked, "TS code: nothing is asked");
            T.Eq(before, site.Xml[id], "TS code: the task sequence is unchanged");
            r = setup.AddSteps(id, "x", pkg, code);
            T.Check(r.Done, "TS code: a real code is accepted: " + r.Text);
            T.Check(site.Xml[id].Contains(code), "TS code: a real code is put in the steps");
        }

        static void SourceFolder()
        {
            T.Check(TsAgentSetup.CheckSource("\\\\cm01.contoso.com\\Sources\\TrackVice") == null, "TS source: a share is accepted");
            string[] bad = { "\\\\?\\C:\\Windows\\Temp", "\\\\.\\C:\\Temp", "\\\\?\\UNC\\cm01\\x", "\\\\cm01\\a\" b", "C:\\Sources", "" };
            foreach (string b in bad) T.Check(TsAgentSetup.CheckSource(b) != null, "TS source: refused: " + b);
        }

        // A standard user can register a task called TrackVice Agent in a folder of their own, or in the root folder running
        // as themselves. The scripts must not take that for the agent and skip the install.
        static void InstalledCheck()
        {
            string[] names = { "Install-TrackViceAgent.ps1", "Install-TrackViceAgent-Links.ps1", "Install-TrackViceAgent-Share.ps1" };
            string[] texts = { AgentScriptTemplate.Text, AgentScriptTemplate.Links, AgentScriptTemplate.Share };
            for (int i = 0; i < names.Length; i++)
            {
                string s = texts[i];
                T.Check(s.Contains("Get-ScheduledTask -TaskPath '\\' -TaskName 'TrackVice Agent'"), "Installed check " + names[i] + ": only the task in the root folder");
                T.Check(s.Contains("Where-Object { $_.Principal.UserId -in 'SYSTEM', 'S-1-5-18', 'NT AUTHORITY\\SYSTEM' }"), "Installed check " + names[i] + ": only a task that runs as SYSTEM");
                T.Check(s.IndexOf("Get-ScheduledTask -TaskName", StringComparison.Ordinal) < 0, "Installed check " + names[i] + ": no search for the name in every folder");
                string path = Path.Combine("agent", names[i]);
                T.Check(File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == s.Replace("\r\n", "\n"), "Installed check: the copy in the exe is the same as agent/" + names[i]);
            }
        }

        // New-TrackViceCustomer.ps1 fills the links script itself: it must quote as TrackVice does.
        static void CustomerTool()
        {
            string path = Path.Combine("agent", "New-TrackViceCustomer.ps1");
            string s = File.Exists(path) ? File.ReadAllText(path) : "";
            T.Check(s.Contains("return \"'\" + ($Value -replace \"['\\u2018\\u2019\\u201A\\u201B]\", '$0$0') + \"'\""), "New customer tool: every kind of single quote is doubled");
        }
    }
}
