namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;

    public enum SharePathKind { Invalid, Unc, Local }

    // What the check of a share found. Ok means this PC can use it; Warning is shown beside an ok.
    public sealed class ShareCheck
    {
        public bool Ok;
        public SharePathKind Kind;
        public string Message = "", Warning = "";
    }

    // A share or folder instead of Azure storage: the checks, the permissions the devices need, and the agent command.
    // The agent already uploads to a folder (FolderStore); this adds nothing to what the device does.
    public static class ShareStorage
    {
        public const string AgentFolder = "agent";
        public const string ExeName = "TrackVice.exe";
        public const string Example = @"\\server\TrackVice\Contoso";

        static readonly Regex Unc = new Regex(@"^\\\\[^\\/:*?""<>|]+\\[^\\/:*?""<>|]+(\\[^\\/:*?""<>|]+)*$");
        static readonly Regex Drive = new Regex(@"^[A-Za-z]:\\([^\\/:*?""<>|]+\\?)*$");

        public static string Normalize(string path)
        {
            string s = (path ?? "").Trim().Trim('"').Trim();
            while (s.Length > 3 && (s.EndsWith("\\") || s.EndsWith("/"))) s = s.Substring(0, s.Length - 1);
            return s;
        }

        public static SharePathKind KindOf(string path)
        {
            string s = Normalize(path);
            if (Unc.IsMatch(s)) return SharePathKind.Unc;
            if (Drive.IsMatch(s)) return SharePathKind.Local;
            // A rooted path of another system, such as /srv/trackvice, is a local folder as well.
            if (s.Length > 0 && s.IndexOf('\\') < 0 && Path.IsPathRooted(s) && Path.DirectorySeparatorChar == '/') return SharePathKind.Local;
            return SharePathKind.Invalid;
        }

        // What is wrong with the path, or null. A local or mapped path is not wrong here: Check warns about it.
        public static string PathProblem(string path)
        {
            string s = Normalize(path);
            if (s.Length == 0) return "Enter the path of the share, such as " + Example + ".";
            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "A share path starts with \\\\ and the server name. Links to Azure storage belong to the Azure storage choice.";
            if (KindOf(s) != SharePathKind.Invalid) return null;
            if (s.StartsWith("\\\\", StringComparison.Ordinal)) return "The path needs a server and a share name after \\\\, such as " + Example + ". The characters / : * ? \" < > | are not allowed in a name.";
            return "Enter a UNC path: \\\\server\\share\\folder, such as " + Example + ".";
        }

        public static string LocalWarning(string path)
        {
            return "This is not a UNC path. Devices cannot reach " + Normalize(path) + ": a drive letter or local folder does not exist on them, and SYSTEM has no mapped drives. Use \\\\server\\share\\folder for the devices.";
        }

        public static string AgentDir(string share)
        {
            return Normalize(share) + "\\" + AgentFolder;
        }

        // The file the script copies. Agents update from the share itself, which has agent\latest.json and agent\TrackVice-<version>.exe.
        public static string ExePath(string share)
        {
            return AgentDir(share) + "\\" + ExeName;
        }

        // Tests from this PC that the folder exists or can be made, and that a file can be written, read back and deleted.
        // It cannot tell what the devices may do: they write as their computer accounts, see Permissions.
        public static ShareCheck Check(string path)
        {
            ShareCheck r = new ShareCheck();
            string s = Normalize(path);
            string problem = PathProblem(s);
            if (problem != null) { r.Message = problem; return r; }
            r.Kind = KindOf(s);
            string step = "reach or make the folder " + s;
            bool made = false;
            try
            {
                if (!Directory.Exists(s)) { Directory.CreateDirectory(s); made = true; }
                step = "write a test file in " + s;
                FolderStore store = new FolderStore(s);
                string name = "sessions/trackvice-check-" + Guid.NewGuid().ToString("N") + ".txt";
                byte[] data = Encoding.UTF8.GetBytes("TrackVice share check " + DateTime.UtcNow.ToString("o"));
                store.Put(name, data, null);
                string file = Path.Combine(s, name.Replace('/', Path.DirectorySeparatorChar));
                step = "read the test file back from " + s;
                byte[] back = store.Get(name);
                if (back.Length != data.Length || Convert.ToBase64String(back) != Convert.ToBase64String(data))
                    throw new IOException("the test file read back is not what was written");
                step = "delete the test file in " + s;
                File.Delete(file);
                if (File.Exists(file + ".meta")) File.Delete(file + ".meta");
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException) r.Message = "Access is denied: this PC could not " + step + ". " + ex.Message + " Your account needs Modify on the share and the folder.";
                else if (ex is DirectoryNotFoundException || ex is FileNotFoundException) r.Message = "The path was not found: this PC could not " + step + ". " + ex.Message;
                else r.Message = "This PC could not " + step + ": " + ex.Message;
                return r;
            }
            r.Ok = true;
            r.Message = (made ? "Made the folder " : "Found the folder ") + s + ", wrote a test file, read it back and deleted it. This PC can use it.";
            if (r.Kind == SharePathKind.Local) r.Warning = LocalWarning(s);
            return r;
        }

        // Why this PC's update key cannot publish to the share: agent\latest.json there is signed with another key. Null when it can.
        public static string KeyProblem(string share, string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey)) return "This PC has no update key.";
            string file = Path.Combine(Normalize(share), AgentFolder, "latest.json");
            if (!File.Exists(file)) return null;
            bool signed;
            try { signed = UpdateManifest.Parse(File.ReadAllBytes(file)).Verify(publicKey); }
            catch (Exception) { signed = false; }
            if (signed) return null;
            return UpdateManifest.Name + " in " + Normalize(share) + " is signed with another update key than the one on this PC, so devices would refuse what this PC publishes. Publish from the PC that has that key.";
        }

        // The icacls lines for the share folder. Run them on the file server, in a prompt as administrator.
        public static List<string> Icacls(string path)
        {
            string p = Normalize(path);
            string agent = AgentDir(p);
            List<string> l = new List<string>();
            l.Add("if not exist \"" + agent + "\" mkdir \"" + agent + "\"");
            l.Add("icacls \"" + p + "\" /inheritance:r /grant \"NT AUTHORITY\\SYSTEM:(OI)(CI)F\" /grant \"BUILTIN\\Administrators:(OI)(CI)F\"");
            // Create files and folders and write: the devices add sessions and logs, and cannot list or read what others sent. CREATOR OWNER lets a device replace its own files.
            l.Add("icacls \"" + p + "\" /grant \"Domain Computers:(OI)(CI)(X,RA,WD,AD)\" /grant \"CREATOR OWNER:(OI)(CI)(IO)M\"");
            // The agent folder is where devices read TrackVice.exe and the signed update from: read only.
            l.Add("icacls \"" + agent + "\" /inheritance:d");
            l.Add("icacls \"" + agent + "\" /remove:g \"Domain Computers\" \"CREATOR OWNER\"");
            l.Add("icacls \"" + agent + "\" /grant \"Domain Computers:(OI)(CI)RX\"");
            return l;
        }

        public static string IcaclsText(string path)
        {
            return string.Join("\r\n", Icacls(path).ToArray());
        }

        public const string PermissionsText = "The agent writes as the device's computer account: give Domain Computers Create files / Write data on the share folder (and Change on the share itself). "
            + "Only administrators read and change the rest, and the agent folder is read only for devices.";

        public const string LimitsText = "Fits SCCM task sequences in full Windows, and Hybrid Autopilot devices that can reach the share. It does not fit cloud-only Autopilot devices on the internet. "
            + "Windows PE only when the share accepts the account it has there.";

        // The step of an SCCM task sequence: starts the agent in the background and returns at once.
        public static string TaskSequenceCommand(string share, string alert)
        {
            StringBuilder b = new StringBuilder(ExeName + " /agent-start /upload \"" + Normalize(share) + "\"");
            if (!string.IsNullOrWhiteSpace(alert)) b.Append(" /alert \"").Append(alert.Trim()).Append("\"");
            return b.ToString();
        }

        // Puts TrackVice.exe and the signed version in the agent folder of the share. The first is what the script copies,
        // the second is what agents update from, signed with the key of this PC (made now when there is none).
        public static UpdateManifest Publish(string share, byte[] exe, string version, RSACryptoServiceProvider key, DateTime nowUtc)
        {
            string s = Normalize(share);
            string problem = KeyProblem(s, UpdateKeys.PublicKey(key));
            if (problem != null) throw new SetupException(problem);
            string dir = Path.Combine(s, AgentFolder);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, ExeName), exe);
            return UpdatePublisher.Publish(new FolderStore(s), exe, version, key, nowUtc);
        }
    }
}
