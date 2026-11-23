namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public static class DownloadTests
    {
        const string Pkg = "P0100777", Adv = "CON20077";

        const string Xml =
            "<sequence version=\"3.00\">" +
            "<group name=\"Configure Contoso\" description=\"\">" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Install Contoso agent\" description=\"\" runIn=\"FullOS\">" +
            "<defaultVarList><variable name=\"PackageID\" property=\"PackageID\">CON000A1</variable><variable name=\"WorkingDirectory\" property=\"WorkingDirectory\">C:\\Contoso</variable></defaultVarList>" +
            "<action>setup.exe /quiet /logfile C:\\Windows\\Temp\\contoso.log</action></step>" +
            "<step type=\"SMS_TaskSequence_RunPowerShellScriptAction\" name=\"Map the Contoso drive\" description=\"\" runIn=\"FullOS\">" +
            "<defaultVarList><variable name=\"OSDRunPowerShellScriptExecutionPolicy\" property=\"OSDRunPowerShellScriptExecutionPolicy\">Bypass</variable>" +
            "<variable name=\"OSDRunPowerShellScriptScriptText\" property=\"OSDRunPowerShellScriptScriptText\">New-PSDrive -Name Z -Root \\\\fs01.contoso.com\\setup -PSProvider FileSystem</variable></defaultVarList>" +
            "<action>powershell.exe -NoProfile</action></step>" +
            "</group></sequence>";

        sealed class Site : ISccmQuery, ISccmGet
        {
            public string AppXml;

            public List<Dictionary<string, object>> Query(string wql)
            {
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (wql.Contains("FROM SMS_Advertisement")) { r["AdvertisementID"] = Adv; r["PackageID"] = Pkg; }
                else if (wql.Contains("FROM SMS_TaskSequencePackage")) { r["PackageID"] = Pkg; r["Name"] = "Contoso Windows 11"; r["Version"] = "7"; r["SourceVersion"] = 7L; }
                else if (wql.Contains("FROM SMS_ApplicationAssignment")) { r["CollectionName"] = "Contoso all laptops"; r["OfferTypeID"] = 0L; }
                else if (wql.Contains("FROM SMS_Application")) { r["CI_ID"] = 4411L; r["LocalizedDisplayName"] = "Contoso Viewer"; }
                else return l;
                l.Add(r);
                return l;
            }

            public Dictionary<string, object> GetInstance(string className, string keyName, string keyValue)
            {
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (className == "SMS_TaskSequencePackage") { r["PackageID"] = Pkg; r["Sequence"] = Xml; return r; }
                r["LocalizedDisplayName"] = "Contoso Viewer";
                r["Manufacturer"] = "Contoso Ltd";
                r["SoftwareVersion"] = "4.2";
                r["SDMPackageXML"] = AppXml;
                return r;
            }
        }

        sealed class Graph : IGraph
        {
            public readonly List<string> Urls = new List<string>();

            public Dictionary<string, object> Get(string url)
            {
                Urls.Add(url);
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (url.Contains("/deviceManagementScripts/"))
                {
                    byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
                    byte[] body = Encoding.UTF8.GetBytes("Write-Output 'Hello from Contoso'");
                    byte[] all = new byte[bom.Length + body.Length];
                    bom.CopyTo(all, 0);
                    body.CopyTo(all, bom.Length);
                    r["scriptContent"] = Convert.ToBase64String(all);
                }
                else if (url.Contains("/deviceHealthScripts/"))
                {
                    r["detectionScriptContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("exit 1"));
                    r["remediationScriptContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("Restart-Service Spooler"));
                }
                else if (url.Contains("/mobileApps/"))
                {
                    r["@odata.type"] = "#microsoft.graph.win32LobApp";
                    r["id"] = "0b6a1f2c-1d3e-4f50-8a61-7b8c9d0e1f23";
                    r["displayName"] = "Contoso Viewer";
                    r["publisher"] = "Contoso Ltd";
                    r["displayVersion"] = "4.2";
                    r["installCommandLine"] = "setup.exe /S";
                    r["uninstallCommandLine"] = "setup.exe /U";
                    r["rules"] = new List<object>
                    {
                        Dict("@odata.type", "#microsoft.graph.win32LobAppRegistryRule", "ruleType", "detection", "keyPath", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Contoso\\Viewer", "valueName", "Version"),
                        Dict("@odata.type", "#microsoft.graph.win32LobAppFileSystemRule", "ruleType", "requirement", "path", "C:\\Program Files\\Contoso", "fileOrFolderName", "base.dll")
                    };
                    r["returnCodes"] = new List<object> { Dict("returnCode", 0L, "type", "success"), Dict("returnCode", 3010L, "type", "softReboot") };
                    r["assignments"] = new List<object> { Dict("intent", "required", "target", Dict("@odata.type", "#microsoft.graph.groupAssignmentTarget", "groupId", "9a8b7c6d-0000-4000-8000-000000000001")) };
                }
                return r;
            }

            static Dictionary<string, object> Dict(params object[] kv)
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
                return d;
            }
        }

        sealed class Plain : ICustomerProtector
        {
            public string Protect(string plain) { return plain; }
            public string Unprotect(string stored) { return stored; }
        }

        public static void Run()
        {
            SafeNames();
            Permission();
            Masking();
            LogLine();
            ScriptsFromGraph();
            StepText();
            AppDetails();
        }

        static Item Make(string kind, string origin, string key, string id)
        {
            Item it = new Item();
            it.Kind = kind;
            it.Origin = origin;
            it.Key = key;
            it.Name = "Contoso item";
            if (id != null) it.Set("ID", id);
            return it;
        }

        static void SafeNames()
        {
            T.Eq("a_b_c.ps1", DlNames.Safe("a/b\\c", ".ps1"), "dl names: path characters");
            T.Eq("Contoso_ setup.txt", DlNames.Safe("Contoso: setup", "txt"), "dl names: colon and extension without a dot");
            T.Eq("_con.txt", DlNames.Safe("con", ".txt"), "dl names: reserved name");
            T.Eq("download.txt", DlNames.Safe("..", ".txt"), "dl names: only dots");
            T.Eq("download.txt", DlNames.Safe(null, ".txt"), "dl names: empty");
            T.Check(!DlNames.Safe("..\\..\\Windows\\evil", ".ps1").Contains("\\") && !DlNames.Safe("../x", ".ps1").Contains("/"), "dl names: no way out of the folder");
            T.Check(DlNames.Safe(new string('x', 300), ".ps1").Length <= 84, "dl names: not too long");
            T.Eq("x.txt", DlNames.Safe("x", ".p/s1"), "dl names: odd extension falls back");
        }

        static void Permission()
        {
            Customer c = new Customer();
            c.Name = "Contoso";
            T.Check(!DlPolicy.Allowed(c) && !DlPolicy.Allowed(null), "dl permission: off by default");
            T.Check(DlPolicy.Ask(c), "dl permission: asks by default");

            MemoryCustomerStorage st = new MemoryCustomerStorage();
            CustomerBook b = new CustomerBook(st, new Plain());
            b.Add(c);
            b.Find("Contoso").AllowDownloads = true;
            b.Find("Contoso").DownloadsWithoutAsking = true;
            b.Save();
            CustomerBook b2 = new CustomerBook(st, new Plain());
            b2.Load();
            T.Check(DlPolicy.Allowed(b2.Find("Contoso")) && !DlPolicy.Ask(b2.Find("Contoso")), "dl permission: saved with the customer");
            Customer copy = b2.Find("Contoso").Copy();
            T.Check(copy.AllowDownloads && copy.DownloadsWithoutAsking, "dl permission: kept by Copy");

            // A file saved by an earlier version has neither value.
            string old = System.Text.RegularExpressions.Regex.Replace(st.Document, ",\\s*\"(AllowDownloads|DownloadsWithoutAsking)\":\\s*true", "");
            T.Check(!old.Contains("AllowDownloads"), "dl permission: test file has no download values");
            MemoryCustomerStorage st2 = new MemoryCustomerStorage();
            st2.Document = old;
            CustomerBook b3 = new CustomerBook(st2, new Plain());
            T.Check(b3.Load() && b3.Find("Contoso") != null && !DlPolicy.Allowed(b3.Find("Contoso")), "dl permission: an old file is read and downloads stay off");

            // An imported file never turns it on.
            CustomerBook b4 = new CustomerBook(new MemoryCustomerStorage(), new Plain());
            b4.Import(b2.Export(false));
            T.Check(b4.Find("Contoso") != null && !DlPolicy.Allowed(b4.Find("Contoso")), "dl permission: import does not turn it on");

            Item script = Make(Kinds.Script, Origins.Windows, "script:x", "5c2f7a10-1111-4222-8333-444455556666");
            T.Check(DlTargets.For(script, null) != null, "dl targets: an Intune script");
        }

        static void Masking()
        {
            string text = "Invoke-Setup -Password Winter2026! -Quiet\r\n$token = 'abc123def456'\r\nWrite-Output 'ok'";
            bool changed;
            string on = DlText.Prepare(text, true, out changed);
            T.Check(changed && !on.Contains("Winter2026!") && !on.Contains("abc123def456") && on.Contains("Write-Output 'ok'"), "dl masking: secrets hidden when on");
            string off = DlText.Prepare(text, false, out changed);
            T.Check(!changed && off == text, "dl masking: nothing changed when off");
            DlText.Prepare("Write-Output 'ok'", true, out changed);
            T.Check(!changed, "dl masking: nothing to hide is not reported");
            MemorySettingStore s = new MemorySettingStore();
            T.Check(DlPolicy.Mask(s), "dl masking: on by default");
            s.Set(DlPolicy.MaskKey, "0");
            T.Check(!DlPolicy.Mask(s), "dl masking: can be turned off");
            FakeDownloadSource fake = new FakeDownloadSource();
            DlTarget t = new DlTarget();
            t.Kind = DlKind.PlatformScript;
            t.Name = "Contoso drive";
            T.Check(!DlText.Prepare(fake.Read(t).Text, true, out changed).Contains("Winter2026!") && changed, "dl masking: the fake script has a secret to hide");
        }

        static void LogLine()
        {
            string line = DlLog.Line(new DateTime(2026, 11, 3, 9, 5, 7), "CONTOSO\\anna", "Contoso", "Map the\tdrive\r\n", "Map the drive.ps1");
            T.Eq("2026-11-03 09:05:07\tCONTOSO\\anna\tContoso\tMap the drive\tMap the drive.ps1", line, "dl log: one line with five parts");
            string dir = Path.Combine(Path.GetTempPath(), "tv-dl-" + Guid.NewGuid().ToString("N"));
            try
            {
                string file = Path.Combine(dir, "sub", DlLog.FileName);
                DlLog log = new DlLog(file, delegate { return new DateTime(2026, 11, 3, 9, 5, 7); }, "CONTOSO\\anna");
                string secret = "Winter2026!";
                FakeDownloadSource fake = new FakeDownloadSource();
                DlTarget t = new DlTarget();
                t.Kind = DlKind.PlatformScript;
                t.Name = "Contoso drive";
                string content = fake.Read(t).Text;
                T.Check(content.Contains(secret), "dl log: the script has a secret");
                log.Add("Contoso", t.Name, "Contoso drive.ps1");
                log.Add("Contoso", "Contoso Viewer", "Contoso Viewer.txt");
                string all = File.ReadAllText(file);
                T.Eq(2, all.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length, "dl log: a line for each download");
                T.Check(!all.Contains(secret) && !all.Contains("Invoke-Setup") && !all.Contains("fs01"), "dl log: no content");
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static void ScriptsFromGraph()
        {
            Graph g = new Graph();
            DownloadReader r = new DownloadReader(g, null);
            Item it = Make(Kinds.Script, Origins.Windows, "script:1", "5C2F7A10-1111-4222-8333-444455556666");
            DlTarget t = DlTargets.For(it, null);
            T.Check(t != null && t.Kind == DlKind.PlatformScript && t.Id == "5c2f7a10-1111-4222-8333-444455556666", "dl scripts: target from the item");
            DlContent c = r.Read(t);
            T.Eq("Write-Output 'Hello from Contoso'", c.Text, "dl scripts: base64 decoded, byte order mark dropped");
            T.Eq(".ps1", c.Extension, "dl scripts: saved as .ps1");
            T.Check(g.Urls[0].EndsWith("/deviceManagementScripts/5c2f7a10-1111-4222-8333-444455556666"), "dl scripts: reads the one script");

            DlTarget rem = DlTargets.For(Make(RemediationKeys.Kind, Origins.Intune, "rem:1", "5c2f7a10-1111-4222-8333-444455556666"), null);
            DlContent rc = r.Read(rem);
            T.Check(rc.Text.Contains("# Detection script\r\nexit 1") && rc.Text.Contains("# Remediation script\r\nRestart-Service Spooler"), "dl scripts: both scripts of a remediation");

            bool said = false;
            try { new DownloadReader(null, null).Read(t); }
            catch (DlException ex) { said = ex.Message.Contains("Sign in"); }
            T.Check(said, "dl scripts: not signed in says so");

            T.Check(DlTargets.For(Make(Kinds.Script, Origins.Windows, "script:2", "not-an-id"), null) == null, "dl targets: an item without a valid ID has no Download");
            T.Check(DlTargets.For(Make(Kinds.Policy, Origins.Intune, "p", "5c2f7a10-1111-4222-8333-444455556666"), null) == null, "dl targets: a policy has no Download");
        }

        static void StepText()
        {
            Site site = new Site();
            DownloadReader r = new DownloadReader(null, site);
            DlTarget ps = DlTargets.For(Make(Kinds.Step, Origins.Sccm, "ts:plan:3", null), Adv);
            T.Check(ps != null && ps.Kind == DlKind.SccmStep && ps.Id == "3", "dl step: target from the plan key");
            DlContent c = r.Read(ps);
            T.Eq(".ps1", c.Extension, "dl step: a script step is saved as .ps1");
            T.Check(c.Text.Contains("New-PSDrive -Name Z -Root \\\\fs01.contoso.com\\setup") && c.Text.Contains("# Step: Map the Contoso drive") && c.Text.Contains("# OSDRunPowerShellScriptExecutionPolicy: Bypass"), "dl step: script text and settings");

            DlContent cmd = r.Read(DlTargets.For(Make(Kinds.Step, Origins.Sccm, "ts:plan:2", null), Adv));
            T.Eq(".txt", cmd.Extension, "dl step: a command line step is saved as .txt");
            T.Check(cmd.Text.Contains("Command line: setup.exe /quiet /logfile C:\\Windows\\Temp\\contoso.log") && cmd.Text.Contains("PackageID: CON000A1") && cmd.Text.Contains("Group: Configure Contoso"), "dl step: command line and package");

            T.Check(DlTargets.For(Make(Kinds.Step, Origins.Sccm, "ts:plan:1", null), null) == null, "dl step: no deployment, no Download");
            T.Check(DlTargets.For(Make(Kinds.Step, Origins.Sccm, "step:partition", null), Adv) == null, "dl step: a step not from the plan has no Download");
            bool said = false;
            try { r.Read(DlTargets.For(Make(Kinds.Step, Origins.Sccm, "ts:plan:9", null), Adv)); }
            catch (DlException ex) { said = ex.Message.Contains("not in the task sequence"); }
            T.Check(said, "dl step: a step that is gone says so");
        }

        static void AppDetails()
        {
            Graph g = new Graph();
            DownloadReader r = new DownloadReader(g, null);
            DlTarget t = DlTargets.For(Make(Kinds.Win32App, Origins.Intune, "intune:0b6a1f2c-1d3e-4f50-8a61-7b8c9d0e1f23", "0b6a1f2c-1d3e-4f50-8a61-7b8c9d0e1f23"), null);
            T.Check(t != null && t.Kind == DlKind.IntuneApp, "dl app: target from a Win32 app");
            DlContent c = r.Read(t);
            T.Eq(".txt", c.Extension, "dl app: saved as .txt");
            string x = c.Text;
            T.Check(x.Contains("The app files are not included.") && x.Contains("Name: Contoso Viewer") && x.Contains("Install command: setup.exe /S") && x.Contains("Uninstall command: setup.exe /U"), "dl app: name and commands");
            T.Check(x.Contains("Detection rules") && x.Contains("keyPath: HKEY_LOCAL_MACHINE\\SOFTWARE\\Contoso\\Viewer") && x.Contains("Requirements") && x.Contains("fileOrFolderName: base.dll"), "dl app: detection and requirement rules");
            T.Check(x.Contains("3010: softReboot") && x.Contains("required: ") && x.Contains("group 9a8b7c6d-0000-4000-8000-000000000001"), "dl app: return codes and assignments");
            T.Check(g.Urls[0].Contains("/deviceAppManagement/mobileApps/0b6a1f2c") && g.Urls[0].EndsWith("$expand=assignments"), "dl app: one call with the assignments");

            Site site = new Site();
            site.AppXml = "<AppMgmtDigest xmlns=\"http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/AppMgmtDigest\"><Application><DeploymentTypes>" +
                "<DeploymentType><Title>Contoso Viewer MSI</Title><Installer Technology=\"MSI\"><CustomData><InstallCommandLine>msiexec /i viewer.msi /qn</InstallCommandLine>" +
                "<UninstallCommandLine>msiexec /x {11111111-2222-3333-4444-555555555555} /qn</UninstallCommandLine><MaxExecuteTime>30</MaxExecuteTime>" +
                "<ExitCodes><ExitCode Code=\"0\" Class=\"Success\"/><ExitCode Code=\"3010\" Class=\"SoftReboot\"/></ExitCodes></CustomData></Installer>" +
                "<Requirements><Rule><Annotation><DisplayName Text=\"Operating system One of Windows 11\"/></Annotation></Rule></Requirements></DeploymentType>" +
                "</DeploymentTypes></Application></AppMgmtDigest>";
            DownloadReader sr = new DownloadReader(null, site);
            DlTarget st = DlTargets.For(Make(Kinds.Application, Origins.Sccm, "cm:scopeid_abc/application_11111111-2222-3333-4444-555555555555", null), null);
            T.Check(st != null && st.Kind == DlKind.SccmApp, "dl app: target from an SCCM application");
            string y = sr.Read(st).Text;
            T.Check(y.Contains("Name: Contoso Viewer") && y.Contains("Install command: msiexec /i viewer.msi /qn") && y.Contains("Uninstall command: msiexec /x"), "dl app: SCCM commands");
            T.Check(y.Contains("Operating system One of Windows 11") && y.Contains("3010: SoftReboot") && y.Contains("Contoso all laptops (required)"), "dl app: SCCM requirements, return codes and assignments");
            T.Check(y.Contains("The app files are not included."), "dl app: says the files are not included");

            DlTarget bad = new DlTarget();
            bad.Kind = DlKind.SccmApp;
            bad.Id = "x' OR 1=1 --";
            bool said = false;
            try { sr.Read(bad); }
            catch (DlException) { said = true; }
            T.Check(said, "dl app: an ID with a quote is not put in a query");
        }
    }
}
