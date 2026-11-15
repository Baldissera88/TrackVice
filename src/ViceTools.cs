namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    // What finding the sibling tools and an app's installer needs from the PC, so tests can fake it.
    public interface IToolProbe
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        // The full paths of the folders in a folder, or none when it does not exist.
        string[] Directories(string folder);
        // A value under HKLM in the 64-bit view, or null. A null name reads the default value.
        string MachineValue(string key, string name);
        string WindowsDir { get; }
        string ProgramFiles { get; }
        string ProgramFilesX86 { get; }
        // The folder TrackVice.exe runs from.
        string Here { get; }
        string PathVariable { get; }
    }

    // A sibling tool TrackVice can start: where its MSI registers it and the setting that holds a chosen path.
    public sealed class ViceTool
    {
        public string Name, Exe, Pref;
        // Registry keys whose default value is a command line that starts the tool, such as a file type's open command.
        public string[] OpenCommands = new string[0];

        public static readonly ViceTool PackVice = Make("PackVice", @"Software\Classes\PackVice.Package\shell\open\command", @"Software\Classes\PackVice.Project\shell\open\command");
        public static readonly ViceTool NotifyVice = Make("NotifyVice");

        static ViceTool Make(string name, params string[] open)
        {
            ViceTool t = new ViceTool();
            t.Name = name;
            t.Exe = name + ".exe";
            t.Pref = name + "Path";
            t.OpenCommands = open;
            return t;
        }
    }

    public static class ViceLocator
    {
        // The places to look, in order: the chosen path, the MSI's own key, App Paths, file type commands, next to
        // TrackVice.exe, Program Files and PATH. When TrackVice runs as administrator, a path a user can change is not
        // trusted, since the tool would start as administrator too.
        public static List<string> Candidates(ViceTool t, IToolProbe p, string chosen, bool admin)
        {
            List<string> l = new List<string>();
            if (!string.IsNullOrEmpty(chosen) && (!admin || UnderProgramFiles(chosen, p))) l.Add(chosen);
            string dir = p.MachineValue(@"Software\" + t.Name, "InstallDir");
            if (!string.IsNullOrEmpty(dir)) l.Add(Join(dir, t.Exe));
            string app = p.MachineValue(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + t.Exe, null);
            if (!string.IsNullOrEmpty(app)) l.Add(app.Trim().Trim('"'));
            foreach (string key in t.OpenCommands)
            {
                List<string> parts = CommandLines.Split(p.MachineValue(key, null));
                if (parts.Count > 0 && string.Equals(FileName(parts[0]), t.Exe, StringComparison.OrdinalIgnoreCase)) l.Add(parts[0]);
            }
            if (!string.IsNullOrEmpty(p.Here)) l.Add(Join(p.Here, t.Exe));
            if (!string.IsNullOrEmpty(p.ProgramFiles)) l.Add(Join(Join(p.ProgramFiles, t.Name), t.Exe));
            if (!string.IsNullOrEmpty(p.ProgramFilesX86)) l.Add(Join(Join(p.ProgramFilesX86, t.Name), t.Exe));
            foreach (string d in (p.PathVariable ?? "").Split(';'))
            {
                string x = d.Trim().Trim('"');
                if (x.Length == 0) continue;
                string c = Join(x, t.Exe);
                if (!admin || UnderProgramFiles(c, p)) l.Add(c);
            }
            return l;
        }

        public static string Find(ViceTool t, IToolProbe p, string chosen, bool admin)
        {
            foreach (string c in Candidates(t, p, chosen, admin))
            {
                try { if (p.FileExists(c)) return c; }
                catch (Exception) { }
            }
            return null;
        }

        public static bool UnderProgramFiles(string path, IToolProbe p)
        {
            if (string.IsNullOrEmpty(path) || path.Contains("..")) return false;
            foreach (string root in new string[] { p.ProgramFiles, p.ProgramFilesX86 })
            {
                if (string.IsNullOrEmpty(root)) continue;
                if (path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Windows paths are joined by hand, so the result is the same when the tests run on Linux.
        public static string Join(string folder, string name)
        {
            if (string.IsNullOrEmpty(folder)) return name;
            return folder.TrimEnd('\\', '/') + "\\" + name.TrimStart('\\');
        }

        public static string FileName(string path)
        {
            if (path == null) return null;
            int k = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return k < 0 ? path : path.Substring(k + 1);
        }
    }

    public static class CommandLines
    {
        // Splits a command line into its parts the way Windows programs read it: spaces separate, quotes group.
        public static List<string> Split(string cmd)
        {
            List<string> l = new List<string>();
            if (string.IsNullOrEmpty(cmd)) return l;
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in cmd)
            {
                if (c == '"') { quoted = !quoted; any = true; continue; }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (any) l.Add(b.ToString());
                    b.Length = 0;
                    any = false;
                    continue;
                }
                b.Append(c);
                any = true;
            }
            if (any) l.Add(b.ToString());
            return l;
        }

        // The .msi file a command line installs: "msiexec /i x.msi", also behind "cmd /c", or the .msi itself.
        // Setup programs and scripts that run msiexec themselves are not followed, and a product code is not a file.
        public static string MsiOf(string cmd)
        {
            List<string> p = Split(cmd);
            if (p.Count == 0) return null;
            if (IsMsi(p[0])) return p[0];
            int at = 0;
            string first = (ViceLocator.FileName(p[0]) ?? "").ToLowerInvariant();
            if (first == "cmd" || first == "cmd.exe")
            {
                at = 1;
                while (at < p.Count && p[at].StartsWith("/") && !IsMsiexec(p[at])) at++;
                if (at < p.Count && IsMsi(p[at])) return p[at];
            }
            if (at >= p.Count || !IsMsiexec(p[at])) return null;
            for (int k = at + 1; k < p.Count; k++)
            {
                string a = p[k];
                if (a.Length < 2 || (a[0] != '/' && a[0] != '-')) continue;
                string o = a.Substring(1).ToLowerInvariant();
                string value = null;
                if (o == "i" || o == "package") value = k + 1 < p.Count ? p[k + 1] : null;
                else if (o.StartsWith("i") && IsMsi(a.Substring(2))) value = a.Substring(2);
                else if (o.StartsWith("package") && IsMsi(a.Substring(8))) value = a.Substring(8);
                if (value != null) return IsMsi(value) ? value : null;
            }
            return null;
        }

        static bool IsMsi(string s)
        {
            return s != null && s.Length > 4 && s.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsMsiexec(string s)
        {
            string n = (ViceLocator.FileName(s) ?? "").ToLowerInvariant();
            return n == "msiexec" || n == "msiexec.exe";
        }
    }

    // Where the installer of an app is on this PC, or why it is not there.
    public sealed class InstallerLookup
    {
        public string Msi, Folder, Path, Reason;

        public bool Found
        {
            get { return Path != null; }
        }
    }

    public static class InstallerFinder
    {
        // Null when the item is not an app installed from an .msi: no action and no line about it.
        public static InstallerLookup For(Item i, IToolProbe p)
        {
            if (i == null) return null;
            if (i.Kind != Kinds.Win32App && i.Kind != Kinds.Application) return null;
            string msi = CommandLines.MsiOf(i.Get("Command line"));
            if (msi == null) return null;
            InstallerLookup r = new InstallerLookup();
            r.Msi = msi;
            if (IsRooted(msi))
            {
                if (p.FileExists(msi)) r.Path = msi;
                else r.Reason = msi + " was not found.";
                return r;
            }
            if (i.Kind == Kinds.Win32App)
            {
                string id = i.Get("ID");
                string cache = ViceLocator.Join(p.WindowsDir, "IMECache");
                if (string.IsNullOrEmpty(id)) { r.Reason = "The app ID is not known, so its folder in " + cache + " cannot be found."; return r; }
                r.Folder = ImeFolder(p.Directories(cache), id);
                if (r.Folder == null) { r.Reason = cache + " has no folder for this app. The Intune Management Extension removes it after the install."; return r; }
            }
            else
            {
                r.Folder = i.Get("Content path");
                if (string.IsNullOrEmpty(r.Folder)) { r.Folder = null; r.Reason = "AppEnforce.log did not say which ccmcache folder holds the content."; return r; }
                if (!p.DirectoryExists(r.Folder)) { r.Reason = r.Folder + " is not on this PC any more. The ConfigMgr client may have cleaned its cache."; return r; }
            }
            string full = ViceLocator.Join(r.Folder, msi);
            if (p.FileExists(full)) r.Path = full;
            else r.Reason = msi + " is not in " + r.Folder + ".";
            return r;
        }

        // The IME keeps each app's content in <app id>_<version>. With more than one version the highest is used.
        public static string ImeFolder(string[] folders, string id)
        {
            string best = null;
            long bestVersion = -1;
            foreach (string f in folders ?? new string[0])
            {
                string n = ViceLocator.FileName(f.TrimEnd('\\', '/'));
                if (n == null || !n.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase)) continue;
                long v;
                if (!long.TryParse(n.Substring(id.Length + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v)) v = 0;
                if (v > bestVersion) { bestVersion = v; best = f; }
            }
            return best;
        }

        static bool IsRooted(string path)
        {
            return path.StartsWith("\\\\") || (path.Length > 2 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        }
    }

    // The probe for this PC.
    public sealed class WindowsToolProbe : IToolProbe
    {
        readonly string here;

        public WindowsToolProbe(string exeFolder)
        {
            here = exeFolder;
        }

        public bool FileExists(string path)
        {
            try { return File.Exists(path); }
            catch (Exception) { return false; }
        }

        public bool DirectoryExists(string path)
        {
            try { return Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        public string[] Directories(string folder)
        {
            try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : new string[0]; }
            catch (Exception) { return new string[0]; }
        }

        public string MachineValue(string key, string name)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (Microsoft.Win32.RegistryKey k = hklm.OpenSubKey(key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v == null ? null : Convert.ToString(v);
                }
            }
            catch (Exception) { return null; }
        }

        public string WindowsDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
        }

        public string ProgramFiles
        {
            get
            {
                // A 32-bit process gets the x86 folder for ProgramFiles; the tools install to the 64-bit one.
                string w = Environment.GetEnvironmentVariable("ProgramW6432");
                return string.IsNullOrEmpty(w) ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : w;
            }
        }

        public string ProgramFilesX86
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); }
        }

        public string Here
        {
            get { return here; }
        }

        public string PathVariable
        {
            get { return Environment.GetEnvironmentVariable("PATH"); }
        }
    }
}
