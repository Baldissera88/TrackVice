namespace TrackVice.Tests
{
    using System;
    using System.IO;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Xml;

    // The TLS and XML guards in NetSafety, and the security rules of the build, the MSI, the workflow and the
    // release scripts, read from the files in the repository.
    public static class BuildSecurityTests
    {
        public static void Run()
        {
            Tls();
            Xml();
            Msi();
            Workflow();
            ReleaseScripts();
            Ignored();
            Policy();
        }

        static void Tls()
        {
            T.Eq((SecurityProtocolType)3072, NetSafety.Strong((SecurityProtocolType)(48 | 192)), "SSL 3.0 and TLS 1.0, the .NET 4.5 default, become TLS 1.2 only");
            T.Eq((SecurityProtocolType)3072, NetSafety.Strong((SecurityProtocolType)(48 | 192 | 768 | 3072)), "TLS 1.1 and older are taken out");
            T.Eq((SecurityProtocolType)(3072 | 12288), NetSafety.Strong((SecurityProtocolType)(192 | 12288)), "TLS 1.3 stays when it is set");
            T.Eq((SecurityProtocolType)0, NetSafety.Strong((SecurityProtocolType)0), "the system default is left to Windows");

            SecurityProtocolType before = ServicePointManager.SecurityProtocol;
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)(192 | 3072);
                NetSafety.UseStrongTls();
                T.Eq((SecurityProtocolType)3072, ServicePointManager.SecurityProtocol, "TLS 1.0 is no longer offered after UseStrongTls");
            }
            catch (NotSupportedException) { }
            finally
            {
                try { ServicePointManager.SecurityProtocol = before; } catch (Exception) { }
            }
        }

        static bool Refused(string xml)
        {
            try { NetSafety.LoadXml(xml); return false; }
            catch (XmlException) { return true; }
        }

        static void Xml()
        {
            string laughs = "<?xml version=\"1.0\"?><!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
                + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]><EnumerationResults>&d;</EnumerationResults>";
            T.Check(Refused(laughs), "XML with nested entities is refused");
            T.Check(Refused("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e;</x>"), "XML with an external entity is refused");
            T.Check(Refused("<!DOCTYPE x SYSTEM \"http://example.invalid/x.dtd\"><x/>"), "XML with an external DTD is refused");

            string list = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults><Blobs><Blob><Name>sessions/a&amp;b.json.gz</Name></Blob></Blobs>"
                + "<NextMarker>" + new string('m', 2000).Replace("m", "&amp;") + "</NextMarker></EnumerationResults>";
            XmlDocument d = NetSafety.LoadXml(list);
            T.Eq("sessions/a&b.json.gz", d.SelectSingleNode("/EnumerationResults/Blobs/Blob/Name").InnerText, "an Azure list with a byte order mark is read");
            T.Eq(2000, d.SelectSingleNode("/EnumerationResults/NextMarker").InnerText.Length, "many character references are read");
            T.Check(Refused(""), "empty text is refused as XML, as XmlDocument.LoadXml does");
        }

        static string Repo(string path)
        {
            return File.ReadAllText(path.Replace('/', Path.DirectorySeparatorChar));
        }

        static void Msi()
        {
            string wxs = Repo("TrackVice.wxs");
            Match ca = Regex.Match(wxs, "<CustomAction Id=\"CloseTrackVice\"[^>]*ExeCommand=\"([^\"]*)\"[^>]*/>");
            T.Check(ca.Success, "TrackVice.wxs has the CloseTrackVice action");
            T.Eq("&quot;[System64Folder]taskkill.exe&quot; /F /IM TrackVice.exe", ca.Groups[1].Value, "taskkill.exe runs as SYSTEM by its full, quoted path");
            T.Check(Regex.IsMatch(wxs, "<Custom Action=\"CloseTrackVice\"[^>]*>NOT REINSTALL</Custom>"), "CloseTrackVice does not run in a repair a standard user can start");
            T.Check(wxs.Contains("InstallScope=\"perMachine\"") && wxs.Contains("<Directory Id=\"ProgramFiles64Folder\">"), "the MSI installs per machine under Program Files");
            T.Check(wxs.Contains("UpgradeCode=\"6635DE95-B38E-46BA-A802-22FD5E781A74\""), "the upgrade code is the one of every release");
            T.Check(!Regex.IsMatch(wxs, "<(CreateFolder|Permission|PermissionEx)\\b"), "the MSI changes no folder permissions");
            Match other = Regex.Match(wxs, "<CustomAction Id=\"(?!CloseTrackVice\")");
            T.Check(!other.Success, "CloseTrackVice is the only custom action");

            string patch = Repo("patch_msi.sh");
            T.Check(patch.Contains("'\"[System64Folder]taskkill.exe\" /F /IM TrackVice.exe'"), "patch_msi.sh writes the same full, quoted command for wixl");
            T.Check(patch.Contains("'CloseTrackVice': 'NOT REINSTALL'"), "patch_msi.sh writes the same condition for wixl");
            T.Check(patch.Contains("grep -qF '\"[System64Folder]taskkill.exe\" /F /IM TrackVice.exe'"), "patch_msi.sh stops when the MSI holds another command");
        }

        static void Workflow()
        {
            foreach (string f in Directory.GetFiles(Path.Combine(".github", "workflows")))
            {
                string y = File.ReadAllText(f);
                string name = Path.GetFileName(f);
                foreach (Match u in Regex.Matches(y, "uses:\\s*(\\S+)"))
                    T.Check(Regex.IsMatch(u.Groups[1].Value, "^[\\w.-]+/[\\w./-]+@[0-9a-f]{40}$"), name + ": " + u.Groups[1].Value + " is pinned by commit");
                T.Check(Regex.IsMatch(y, "(?m)^permissions:\\s*\\n\\s+contents: read\\s*$"), name + ": the token can only read");
                T.Check(!y.Contains("pull_request_target") && !y.Contains("workflow_run"), name + ": no trigger runs pull request code with secrets");
                T.Check(!y.Contains("secrets."), name + ": no secret is used");
                T.Check(!Regex.IsMatch(y, "\\$\\{\\{\\s*github\\.(event|head_ref)"), name + ": no title, branch or body is put into a script");
                T.Check(!y.Contains("actions/checkout@") || y.Contains("persist-credentials: false"), name + ": the checkout keeps no token on disk");
            }
        }

        static void ReleaseScripts()
        {
            string release = Repo("release.ps1");
            Match gh = Regex.Match(release, "\\$ghArgs = @\\('release', 'create'[^\\r\\n]*");
            T.Check(gh.Success, "release.ps1 creates the release");
            foreach (string v in new string[] { "$exe", "$msi", "$sumsPath", "$agentScript", "$linksScript", "$shareScript", "$customerScript" })
                T.Check(gh.Value.Contains(v + ","), "the release gets " + v);

            string manifests = Repo("winget/New-Manifests.ps1");
            T.Check(!manifests.Contains("'--token'"), "New-Manifests.ps1 puts no token on the wingetcreate command line");
            T.Check(manifests.Contains("$env:WINGET_CREATE_GITHUB_TOKEN = $Token") && manifests.Contains("$env:WINGET_CREATE_GITHUB_TOKEN = $tokenBefore"), "the token goes in the environment and is put back");
            T.Check(!Repo("winget/manifests.sh").Contains("--token"), "manifests.sh does not suggest a token on the command line");
        }

        static void Ignored()
        {
            string ig = Repo(".gitignore");
            foreach (string p in new string[] { "*.pfx", "*.p12", "*.pem", "*.key", "*.snk", "*.tvcustomers", "agent-update-key.bin", "metadata.json", ".env" })
                T.Check(Regex.IsMatch(ig, "(?m)^" + Regex.Escape(p) + "\\r?$"), ".gitignore leaves out " + p);
        }

        static void Policy()
        {
            T.Check(File.Exists("SECURITY.md"), "SECURITY.md is at the root of the repository");
            if (!File.Exists("SECURITY.md")) return;
            string s = File.ReadAllText("SECURITY.md");
            T.Check(s.Contains("https://github.com/Baldissera88/TrackVice/security/advisories/new"), "SECURITY.md links private vulnerability reporting");
            T.Check(s.Contains("dbal.itx@gmail.com"), "SECURITY.md gives the e-mail address");
            T.Check(s.Contains("SHA256SUMS.txt") && s.Contains("Get-FileHash"), "SECURITY.md says how to check a download");
            T.Check(s.Contains("## Checking a download"), "the anchor docs/RELEASE.md links to exists");
        }
    }
}
