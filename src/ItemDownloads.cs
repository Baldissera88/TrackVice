namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Xml;

    // Download of a script or the details of an app from an item in a deployment. It is off for every customer until
    // it is turned on in Settings. App files are never downloaded, only a text with the details.
    public enum DlKind { PlatformScript, Remediation, SccmStep, IntuneApp, SccmApp }

    // What an item can give: its kind and the ID the source knows it by.
    public sealed class DlTarget
    {
        public DlKind Kind;
        // The Intune ID, the step number in the task sequence, or the model name of the SCCM application.
        public string Id;
        public string Name;
        // The deployment of a task sequence step, to find its package.
        public string Deployment;

        public bool IsScript
        {
            get { return Kind == DlKind.PlatformScript || Kind == DlKind.Remediation || Kind == DlKind.SccmStep; }
        }

        public string What
        {
            get
            {
                switch (Kind)
                {
                    case DlKind.PlatformScript: return "the script";
                    case DlKind.Remediation: return "the detection and remediation scripts";
                    case DlKind.SccmStep: return "the command line and script of this step";
                    default: return "the details of this app";
                }
            }
        }
    }

    // The text to save and its file type.
    public sealed class DlContent
    {
        public string Text = "";
        public string Extension = ".txt";
    }

    // A reason in plain words, shown to the person.
    public sealed class DlException : Exception
    {
        public DlException(string message) : base(message) { }
    }

    public interface IDownloadSource
    {
        DlContent Read(DlTarget t);
    }

    public static class DlTargets
    {
        // What the item can give, or null. deployment is the SCCM deployment ID of a remote task sequence run, if known.
        public static DlTarget For(Item it, string deployment)
        {
            if (it == null) return null;
            string id = it.Get("ID");
            string key = it.Key ?? "";
            DlTarget t = new DlTarget();
            t.Name = it.DisplayName;
            if (it.Origin == Origins.Sccm)
            {
                if (it.Kind == Kinds.Step && key.StartsWith("ts:plan:", StringComparison.OrdinalIgnoreCase))
                {
                    int n;
                    if (!int.TryParse(key.Substring(8), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1) return null;
                    if (string.IsNullOrEmpty(deployment)) return null;
                    t.Kind = DlKind.SccmStep;
                    t.Id = n.ToString(CultureInfo.InvariantCulture);
                    t.Deployment = deployment;
                    return t;
                }
                if (it.Kind == Kinds.Application && key.StartsWith("cm:scopeid_", StringComparison.OrdinalIgnoreCase) && key.IndexOf("/application_", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    t.Kind = DlKind.SccmApp;
                    t.Id = key.Substring(3);
                    return t;
                }
                return null;
            }
            string guid = IntuneKeys.Guid(id);
            if (guid == null) return null;
            t.Id = guid;
            if (it.Kind == Kinds.Script) t.Kind = DlKind.PlatformScript;
            else if (it.Kind == RemediationKeys.Kind) t.Kind = DlKind.Remediation;
            else if (it.Kind == Kinds.Win32App || it.Kind == Kinds.MsiApp) t.Kind = DlKind.IntuneApp;
            else return null;
            return t;
        }
    }

    // Names of saved files: no path characters, no names Windows keeps for itself, not too long.
    public static class DlNames
    {
        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        public static string Safe(string name, string extension)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (c < ' ' || "\\/:*?\"<>|".IndexOf(c) >= 0) b.Append('_');
                else b.Append(c);
            }
            string s = b.ToString().Trim().TrimEnd('.', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            while (s.Contains("__")) s = s.Replace("__", "_");
            if (s.Length > 80) s = s.Substring(0, 80).TrimEnd('.', ' ');
            if (s.Length == 0 || s.Trim('_', '.', ' ').Length == 0) s = "download";
            foreach (string r in Reserved)
                if (string.Equals(s, r, StringComparison.OrdinalIgnoreCase)) { s = "_" + s; break; }
            string e = extension ?? "";
            if (e.Length > 0 && e[0] != '.') e = "." + e;
            foreach (char c in e) if (!(char.IsLetterOrDigit(c) || c == '.')) { e = ".txt"; break; }
            return s + e;
        }
    }

    // The text as it is saved: secrets hidden when asked, and whether anything was.
    public static class DlText
    {
        public static string Prepare(string text, bool mask, out bool changed)
        {
            string t = text ?? "";
            changed = false;
            if (!mask) return t;
            string m = Secrets.Mask(t);
            changed = !string.Equals(m, t, StringComparison.Ordinal);
            return m;
        }
    }

    // One line for each download in a file of the TrackVice folder: the time, the Windows user, the customer, the item
    // and the file name. The content is never written.
    public sealed class DlLog
    {
        public const string FileName = "downloads.log";
        readonly string path;
        readonly Func<DateTime> now;
        readonly string user;

        public DlLog(string file, Func<DateTime> clock, string windowsUser)
        {
            path = file;
            now = clock;
            user = windowsUser;
        }

        public string Path
        {
            get { return path; }
        }

        public static string DefaultPath
        {
            get { return System.IO.Path.Combine(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice"), FileName); }
        }

        public static DlLog Default()
        {
            return new DlLog(DefaultPath, delegate { return DateTime.Now; }, Environment.UserDomainName + "\\" + Environment.UserName);
        }

        public static string Line(DateTime time, string windowsUser, string customer, string item, string file)
        {
            return time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + One(windowsUser) + "\t" + One(customer) + "\t" + One(item) + "\t" + One(file);
        }

        static string One(string s)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            StringBuilder b = new StringBuilder();
            foreach (char c in s) b.Append(c < ' ' ? ' ' : c);
            return b.ToString().Trim();
        }

        public void Add(string customer, string item, string file)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, Line(now(), user, customer, item, file) + "\r\n", new UTF8Encoding(false));
        }
    }

    // The permission of the customer in use.
    public static class DlPolicy
    {
        public static bool Allowed(Customer c)
        {
            return c != null && c.AllowDownloads;
        }

        public static bool Ask(Customer c)
        {
            return c == null || !c.DownloadsWithoutAsking;
        }

        public const string MaskKey = "DownloadMask";

        public static bool Mask(ISettingStore s)
        {
            string v = s == null ? null : s.Get(MaskKey);
            return v == null || !(v.Trim() == "0" || string.Equals(v.Trim(), "false", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Reads from Microsoft Graph (Intune scripts, remediations and apps) and from the SMS Provider (task sequence steps
    // and applications). Either one may be missing when the person is not signed in or connected.
    public sealed class DownloadReader : IDownloadSource
    {
        readonly IGraph graph;
        readonly ISccmQuery site;

        public DownloadReader(IGraph g, ISccmQuery q)
        {
            graph = g;
            site = q;
        }

        public DlContent Read(DlTarget t)
        {
            switch (t.Kind)
            {
                case DlKind.PlatformScript: return PlatformScript(NeedGraph(), t.Id);
                case DlKind.Remediation: return Remediation(NeedGraph(), t.Id);
                case DlKind.IntuneApp: return IntuneApp(NeedGraph(), t.Id);
                case DlKind.SccmStep: return SccmStep(NeedSite(), t);
                default: return SccmApp(NeedSite(), t.Id);
            }
        }

        IGraph NeedGraph()
        {
            if (graph == null) throw new DlException("Sign in to Intune first, with Devices > Autopilot in Intune.");
            return graph;
        }

        ISccmQuery NeedSite()
        {
            if (site == null) throw new DlException("Connect to the site first, with Devices > Task sequences in SCCM.");
            return site;
        }

        static string Fail(Exception ex, string permission)
        {
            string m = ex.Message.Trim();
            if (m.IndexOf("403", StringComparison.Ordinal) >= 0 || m.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Intune did not allow this. The sign-in needs " + permission + ", and the account needs a role that can read it.";
            return "Intune did not give it: " + m;
        }

        public const string ScriptsUrl = "https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts/";
        public const string HealthUrl = "https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts/";
        public const string AppsUrl = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/";

        // Script content in Graph is base64 of the file as it was uploaded, often with a byte order mark.
        public static string Decode(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return "";
            byte[] b;
            try { b = Convert.FromBase64String(base64.Trim()); }
            catch (FormatException) { throw new DlException("The script in Intune is not in a form TrackVice can read."); }
            int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            return Encoding.UTF8.GetString(b, skip, b.Length - skip);
        }

        static void CheckId(string id)
        {
            if (IntuneKeys.Guid(id) == null || id.Length != 36) throw new DlException("The ID of the item is not an Intune ID.");
        }

        static DlContent PlatformScript(IGraph g, string id)
        {
            CheckId(id);
            Dictionary<string, object> r;
            try { r = g.Get(ScriptsUrl + id); }
            catch (Exception ex) { throw new DlException(Fail(ex, "DeviceManagementScripts.Read.All")); }
            string content = Http.Str(r, "scriptContent");
            if (string.IsNullOrEmpty(content)) throw new DlException("Intune gave no script text for this item.");
            DlContent c = new DlContent();
            c.Text = Decode(content);
            c.Extension = ".ps1";
            return c;
        }

        static DlContent Remediation(IGraph g, string id)
        {
            CheckId(id);
            Dictionary<string, object> r;
            try { r = g.Get(HealthUrl + id); }
            catch (Exception ex) { throw new DlException(Fail(ex, "DeviceManagementScripts.Read.All")); }
            string det = Http.Str(r, "detectionScriptContent"), rem = Http.Str(r, "remediationScriptContent");
            if (string.IsNullOrEmpty(det) && string.IsNullOrEmpty(rem)) throw new DlException("Intune gave no script text for this item.");
            StringBuilder b = new StringBuilder();
            b.Append("# Detection script\r\n").Append(Decode(det)).Append("\r\n\r\n");
            b.Append("# Remediation script\r\n").Append(string.IsNullOrEmpty(rem) ? "# This remediation has no remediation script.\r\n" : Decode(rem)).Append("\r\n");
            DlContent c = new DlContent();
            c.Text = b.ToString();
            c.Extension = ".ps1";
            return c;
        }

        static DlContent IntuneApp(IGraph g, string id)
        {
            CheckId(id);
            Dictionary<string, object> r;
            try { r = g.Get(AppsUrl + id + "?$expand=assignments"); }
            catch (Exception ex) { throw new DlException(Fail(ex, "DeviceManagementApps.Read.All")); }
            DlContent c = new DlContent();
            c.Text = AppText.FromIntune(r);
            return c;
        }

        static DlContent SccmStep(ISccmQuery q, DlTarget t)
        {
            string why;
            bool again;
            string pkg = TsReader.PackageOf(q, t.Deployment);
            if (string.IsNullOrEmpty(pkg)) throw new DlException("The site did not say which task sequence this deployment runs.");
            TsSequence s = TsReader.Read(q, pkg, out why, out again);
            if (s == null) throw new DlException(why);
            int n = int.Parse(t.Id, CultureInfo.InvariantCulture);
            TsNode node = null;
            foreach (TsNode x in s.All) if (x.Index == n) { node = x; break; }
            if (node == null || node.IsGroup) throw new DlException("This step is not in the task sequence on the site any more.");
            return StepText.Make(node, pkg);
        }

        static DlContent SccmApp(ISccmQuery q, string model)
        {
            if (!AppText.SafeModel(model)) throw new DlException("The ID of the application is not one of the site.");
            ISccmGet g = q as ISccmGet;
            if (g == null) throw new DlException("This connection to the site cannot read applications.");
            try
            {
                List<Dictionary<string, object>> rows = q.Query("SELECT CI_ID, LocalizedDisplayName FROM SMS_Application WHERE ModelName = '" + model + "' AND IsLatest = 'TRUE'");
                if (rows.Count == 0) throw new DlException("The application is not on the site, or the account that runs TrackVice cannot see it.");
                string ci = SccmCatalog.Str(rows[0], "CI_ID");
                Dictionary<string, object> inst = g.GetInstance("SMS_Application", "CI_ID", ci);
                if (inst == null) throw new DlException("The site gave no details for this application.");
                string name = SccmCatalog.Str(inst, "LocalizedDisplayName");
                List<string> assigned = new List<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    try
                    {
                        foreach (Dictionary<string, object> a in q.Query("SELECT CollectionName, OfferTypeID FROM SMS_ApplicationAssignment WHERE ApplicationName = '" + name.Replace("'", "''") + "'"))
                            assigned.Add(SccmCatalog.Str(a, "CollectionName") + " (" + AppText.Offer(SccmCatalog.Int(a, "OfferTypeID")) + ")");
                    }
                    catch (Exception) { }
                }
                DlContent c = new DlContent();
                c.Text = AppText.FromSccm(inst, assigned);
                return c;
            }
            catch (DlException) { throw; }
            catch (Exception ex) { throw new DlException("The site did not give it: " + ex.Message.Trim()); }
        }
    }

    // The text of a task sequence step.
    public static class StepText
    {
        public static DlContent Make(TsNode n, string package)
        {
            StringBuilder b = new StringBuilder();
            string script = null;
            foreach (KeyValuePair<string, string> v in n.Vars)
                if (v.Key.EndsWith("ScriptText", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v.Value)) script = v.Value;
            b.Append("Step: ").Append(n.Name).Append("\r\n");
            b.Append("Type: ").Append(n.Type).Append("\r\n");
            b.Append("Place in the task sequence: ").Append(n.Index.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (!string.IsNullOrEmpty(n.GroupName)) b.Append("Group: ").Append(n.GroupName).Append("\r\n");
            b.Append("Task sequence package: ").Append(package).Append("\r\n");
            if (n.Disabled) b.Append("This step is disabled.\r\n");
            if (n.Action.Length > 0) b.Append("Command line: ").Append(n.Action).Append("\r\n");
            foreach (KeyValuePair<string, string> v in n.Vars)
            {
                if (v.Key.EndsWith("ScriptText", StringComparison.OrdinalIgnoreCase)) continue;
                b.Append(v.Key).Append(": ").Append(v.Value).Append("\r\n");
            }
            DlContent c = new DlContent();
            if (script != null)
            {
                StringBuilder s = new StringBuilder();
                foreach (string line in b.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) s.Append("# ").Append(line).Append("\r\n");
                s.Append("\r\n").Append(script).Append("\r\n");
                c.Text = s.ToString();
                c.Extension = ".ps1";
            }
            else
            {
                if (n.Action.Length == 0 && n.Vars.Count == 0) b.Append("The step has no command line or script in the task sequence.\r\n");
                c.Text = b.ToString();
            }
            return c;
        }
    }

    // The details of an app as text. Only what the source gives is written.
    public static class AppText
    {
        public static bool SafeModel(string m)
        {
            if (string.IsNullOrEmpty(m) || m.Length > 200) return false;
            foreach (char c in m) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '/')) return false;
            return true;
        }

        public static string Offer(long v)
        {
            return v == 0 ? "required" : v == 2 ? "available" : "offer " + v.ToString(CultureInfo.InvariantCulture);
        }

        static void Line(StringBuilder b, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            b.Append(label).Append(": ").Append(value).Append("\r\n");
        }

        static List<Dictionary<string, object>> List(Dictionary<string, object> d, string key)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            List<object> o = ImeJson.Get(d, key) as List<object>;
            if (o != null) foreach (object x in o) { Dictionary<string, object> e = x as Dictionary<string, object>; if (e != null) l.Add(e); }
            return l;
        }

        static string Kind(string odata)
        {
            string s = odata ?? "";
            int i = s.LastIndexOf('.');
            return Txt.SplitCamel(i >= 0 ? s.Substring(i + 1) : s);
        }

        // The Intune app as Microsoft Graph gives it: a Win32 app has command lines, rules and return codes.
        public static string FromIntune(Dictionary<string, object> r)
        {
            StringBuilder b = new StringBuilder();
            b.Append("App details from Intune. The app files are not included.\r\n\r\n");
            Line(b, "Name", Http.Str(r, "displayName"));
            Line(b, "Type", Kind(Http.Str(r, "@odata.type")));
            Line(b, "Publisher", Http.Str(r, "publisher"));
            Line(b, "Version", Http.Str(r, "displayVersion"));
            Line(b, "ID", Http.Str(r, "id"));
            Line(b, "File name", Http.Str(r, "fileName"));
            Line(b, "Install command", Http.Str(r, "installCommandLine"));
            Line(b, "Uninstall command", Http.Str(r, "uninstallCommandLine"));
            Dictionary<string, object> exp = ImeJson.Get(r, "installExperience") as Dictionary<string, object>;
            if (exp != null)
            {
                Line(b, "Install behavior", Http.Str(exp, "runAsAccount"));
                Line(b, "Restart behavior", Http.Str(exp, "deviceRestartBehavior"));
            }
            Line(b, "Minimum Windows release", Http.Str(r, "minimumSupportedWindowsRelease"));
            Line(b, "Architectures", Http.Str(r, "applicableArchitectures"));
            Line(b, "Minimum free disk space (MB)", Http.Str(r, "minimumFreeDiskSpaceInMB"));
            Line(b, "Minimum memory (MB)", Http.Str(r, "minimumMemoryInMB"));

            List<Dictionary<string, object>> detect = new List<Dictionary<string, object>>(), need = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> x in List(r, "rules"))
            {
                string t = Http.Str(x, "ruleType");
                if (string.Equals(t, "requirement", StringComparison.OrdinalIgnoreCase)) need.Add(x); else detect.Add(x);
            }
            if (detect.Count == 0) detect = List(r, "detectionRules");
            if (need.Count == 0) need = List(r, "requirementRules");
            Rules(b, "Detection rules", detect);
            Rules(b, "Requirements", need);

            List<Dictionary<string, object>> codes = List(r, "returnCodes");
            if (codes.Count > 0)
            {
                b.Append("\r\nReturn codes\r\n");
                foreach (Dictionary<string, object> c in codes) b.Append("  ").Append(Http.Str(c, "returnCode")).Append(": ").Append(Http.Str(c, "type")).Append("\r\n");
            }
            List<Dictionary<string, object>> asg = List(r, "assignments");
            b.Append("\r\nAssignments\r\n");
            if (asg.Count == 0) b.Append("  None read.\r\n");
            foreach (Dictionary<string, object> a in asg)
            {
                Dictionary<string, object> tg = ImeJson.Get(a, "target") as Dictionary<string, object>;
                string kind = Kind(Http.Str(tg, "@odata.type"));
                string gid = Http.Str(tg, "groupId");
                b.Append("  ").Append(Http.Str(a, "intent")).Append(": ").Append(kind).Append(string.IsNullOrEmpty(gid) ? "" : ", group " + gid).Append("\r\n");
            }
            return b.ToString();
        }

        static void Rules(StringBuilder b, string title, List<Dictionary<string, object>> rules)
        {
            if (rules.Count == 0) return;
            b.Append("\r\n").Append(title).Append("\r\n");
            foreach (Dictionary<string, object> x in rules)
            {
                b.Append("  ").Append(Kind(Http.Str(x, "@odata.type")));
                foreach (KeyValuePair<string, object> kv in x)
                {
                    if (kv.Key.StartsWith("@", StringComparison.Ordinal) || kv.Key == "ruleType" || kv.Value == null) continue;
                    string v = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                    if (kv.Key.EndsWith("Content", StringComparison.OrdinalIgnoreCase) && kv.Key.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        b.Append("\r\n    ").Append(kv.Key).Append(":\r\n");
                        string txt;
                        try { txt = DownloadReader.Decode(v); } catch (DlException) { txt = v; }
                        foreach (string line in txt.Replace("\r\n", "\n").Split('\n')) b.Append("      ").Append(line).Append("\r\n");
                        continue;
                    }
                    if (v.Length == 0) continue;
                    b.Append("\r\n    ").Append(kv.Key).Append(": ").Append(v);
                }
                b.Append("\r\n");
            }
        }

        // The application of SCCM: its deployment types are in SDMPackageXML. A part the XML does not have is left out.
        public static string FromSccm(Dictionary<string, object> app, List<string> assignments)
        {
            StringBuilder b = new StringBuilder();
            b.Append("App details from the site. The app files are not included.\r\n\r\n");
            Line(b, "Name", SccmCatalog.Str(app, "LocalizedDisplayName"));
            Line(b, "Publisher", SccmCatalog.Str(app, "Manufacturer"));
            Line(b, "Version", SccmCatalog.Str(app, "SoftwareVersion"));
            Line(b, "ID", SccmCatalog.Str(app, "ModelName"));
            string xml = SccmCatalog.Str(app, "SDMPackageXML");
            if (!string.IsNullOrEmpty(xml))
            {
                try { DeploymentTypes(b, xml); }
                catch (XmlException) { b.Append("\r\nThe definition of the application is not XML that TrackVice can read.\r\n"); }
            }
            else b.Append("\r\nThe site gave no definition of the deployment types.\r\n");
            b.Append("\r\nAssignments\r\n");
            if (assignments == null || assignments.Count == 0) b.Append("  None read.\r\n");
            else foreach (string a in assignments) b.Append("  ").Append(a).Append("\r\n");
            return b.ToString();
        }

        static void DeploymentTypes(StringBuilder b, string xml)
        {
            XmlReaderSettings rs = new XmlReaderSettings();
            rs.DtdProcessing = DtdProcessing.Prohibit;
            rs.XmlResolver = null;
            XmlDocument d = new XmlDocument();
            using (XmlReader r = XmlReader.Create(new StringReader(xml), rs)) d.Load(r);
            int n = 0;
            foreach (XmlElement dt in Descendants(d.DocumentElement, "DeploymentType"))
            {
                n++;
                b.Append("\r\nDeployment type ").Append(n.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                Line(b, "Name", Text(dt, "Title"));
                foreach (XmlElement i in Descendants(dt, "Installer")) Line(b, "Technology", i.GetAttribute("Technology"));
                Line(b, "Install command", Text(dt, "InstallCommandLine"));
                Line(b, "Uninstall command", Text(dt, "UninstallCommandLine"));
                Line(b, "Logon requirement", Text(dt, "RequiresLogOn"));
                Line(b, "Maximum run time (minutes)", Text(dt, "MaxExecuteTime"));
                foreach (XmlElement det in Descendants(dt, "EnhancedDetectionMethod"))
                {
                    b.Append("  Detection rules\r\n");
                    foreach (XmlElement s in Descendants(det, "Settings"))
                        foreach (XmlNode k in s.ChildNodes)
                        {
                            XmlElement e = k as XmlElement;
                            if (e == null) continue;
                            b.Append("    ").Append(e.LocalName);
                            foreach (XmlAttribute a in e.Attributes) if (a.Value.Length > 0 && a.LocalName != "LogicalName" && a.LocalName != "AuthoringScopeId") b.Append(", ").Append(a.LocalName).Append(" = ").Append(a.Value);
                            b.Append("\r\n");
                        }
                }
                foreach (XmlElement det in Descendants(dt, "DetectionMethod")) Line(b, "Detection method", det.InnerText.Trim());
                List<string> need = new List<string>();
                foreach (XmlElement req in Descendants(dt, "Requirements"))
                    foreach (XmlElement dn in Descendants(req, "DisplayName")) { string t = dn.GetAttribute("Text"); if (t.Length > 0) need.Add(t); }
                if (need.Count > 0)
                {
                    b.Append("  Requirements\r\n");
                    foreach (string t in need) b.Append("    ").Append(t).Append("\r\n");
                }
                List<XmlElement> codes = Descendants(dt, "ExitCode");
                if (codes.Count > 0)
                {
                    b.Append("  Return codes\r\n");
                    foreach (XmlElement c in codes) b.Append("    ").Append(c.GetAttribute("Code")).Append(": ").Append(c.GetAttribute("Class")).Append("\r\n");
                }
            }
            if (n == 0) b.Append("\r\nThe definition has no deployment types.\r\n");
        }

        static List<XmlElement> Descendants(XmlElement from, string local)
        {
            List<XmlElement> l = new List<XmlElement>();
            if (from == null) return l;
            foreach (XmlNode x in from.GetElementsByTagName("*"))
            {
                XmlElement e = x as XmlElement;
                if (e != null && string.Equals(e.LocalName, local, StringComparison.OrdinalIgnoreCase)) l.Add(e);
            }
            return l;
        }

        static string Text(XmlElement from, string local)
        {
            List<XmlElement> l = Descendants(from, local);
            if (l.Count == 0) return null;
            string t = l[0].InnerText.Trim();
            return t.Length == 0 ? null : t;
        }
    }

    // Made-up texts for the demo and for tests.
    public sealed class FakeDownloadSource : IDownloadSource
    {
        public readonly List<string> Asked = new List<string>();

        public DlContent Read(DlTarget t)
        {
            Asked.Add(t.Kind + ":" + t.Id);
            DlContent c = new DlContent();
            if (t.IsScript)
            {
                c.Extension = ".ps1";
                c.Text = "# " + t.Name + "\r\n$server = 'fs01.contoso.com'\r\nNew-PSDrive -Name Z -PSProvider FileSystem -Root \"\\\\$server\\setup\" -Credential $cred\r\nInvoke-Setup -Password Winter2026! -Quiet\r\n";
            }
            else c.Text = "App details. The app files are not included.\r\n\r\nName: " + t.Name + "\r\nInstall command: setup.exe /S /apikey=ab12cd34ef56\r\nUninstall command: setup.exe /U\r\n";
            return c;
        }
    }
}
