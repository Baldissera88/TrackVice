namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text.RegularExpressions;

    // What GitHub says about the latest release of LogVice.
    public sealed class LogViceRelease
    {
        public string Version, Url, Error;
        public GitHubAsset Msi, Exe, Sums;
    }

    // How LogVice can be got here, decided before anything is downloaded.
    public enum LogViceWay
    {
        // Install LogVice.msi: the user is an administrator on a PC that is not deploying.
        Install,
        // Keep LogVice.exe in TrackVice's folder under %LOCALAPPDATA%.
        Keep,
        // A device that deploys: LogVice.exe in a temporary folder for this session only.
        Temp,
        // Windows PE: LogVice needs the full Windows.
        NeedsWindows,
        Blocked
    }

    // What decides the way. Filled from the same checks as the update gate.
    public sealed class LogViceContext
    {
        public bool Window, Screenshot, WinPE, System, Oobe, TaskSequence, Deploying, Policy, Admin;

        public static LogViceContext From(UpdateContext u, bool admin)
        {
            LogViceContext c = new LogViceContext();
            if (u == null) return c;
            c.Window = u.Window;
            c.Screenshot = u.Screenshot;
            c.WinPE = u.WinPE;
            c.System = u.System;
            c.Oobe = u.Oobe;
            c.TaskSequence = u.TaskSequence;
            c.Deploying = u.Deploying;
            c.Policy = u.Policy;
            c.Admin = admin;
            return c;
        }

        public bool DeviceDeploys
        {
            get { return Oobe || TaskSequence || Deploying; }
        }
    }

    // The web as LogViceGet uses it. Replaced in the tests.
    public interface IReleaseWeb
    {
        string Text(string url);
        // progress gets the bytes so far and the whole size, or 0. cancelled is asked while it downloads.
        void Save(string url, string path, Action<long, long> progress, Func<bool> cancelled);
    }

    public sealed class GitHubReleaseWeb : IReleaseWeb
    {
        public string Text(string url)
        {
            return GitHubFiles.FetchText(url);
        }

        public void Save(string url, string path, Action<long, long> progress, Func<bool> cancelled)
        {
            GitHubFiles.Download(url, path, progress, cancelled);
        }
    }

    public sealed class LogViceException : Exception
    {
        public LogViceException(string message) : base(message) { }
    }

    // Where LogVice ended up.
    public sealed class LogViceResult
    {
        public string Exe;
        public LogViceWay Way;
        public string Version;
    }

    // Gets LogVice from its GitHub releases when it is not on this PC, only after the user asked for it.
    public static class LogViceGet
    {
        public const string Repo = "Baldissera88/LogVice";
        public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";
        public const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        public const string MsiName = "LogVice.msi";
        public const string ExeName = "LogVice.exe";
        public const string PrefName = "LogVicePath";

        public static IReleaseWeb Web = new GitHubReleaseWeb();
        // Installs the MSI and returns the exit code of msiexec. Replaced in the tests.
        public static Func<string, int> RunMsi = InstallMsi;
        // Where the MSI puts LogVice.exe.
        public static Func<string> InstalledExe = delegate
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"LogVice\LogVice.exe");
        };
        // A copy in a temporary folder, for this session only, on a device that deploys.
        public static string SessionExe;

        // Says how LogVice can be got here, and why not when it cannot.
        public static LogViceWay Way(LogViceContext c, out string reason)
        {
            reason = null;
            if (c == null || !c.Window) { reason = "TrackVice downloads LogVice only from its window."; return LogViceWay.Blocked; }
            if (c.Screenshot) { reason = "No download while TrackVice takes a screenshot."; return LogViceWay.Blocked; }
            if (c.WinPE) { reason = "LogVice needs the full Windows and does not run in Windows PE. Open the logs after Windows is installed, or copy them to another PC."; return LogViceWay.NeedsWindows; }
            if (c.Policy) { reason = "Your organization has turned off downloads from GitHub with the policy value " + UpdateGate.PolicyValue + ". Find LogVice.exe, or ask for it to be installed."; return LogViceWay.Blocked; }
            if (c.DeviceDeploys) return LogViceWay.Temp;
            if (c.System) { reason = "TrackVice does not download LogVice while it runs as SYSTEM."; return LogViceWay.Blocked; }
            return c.Admin ? LogViceWay.Install : LogViceWay.Keep;
        }

        // The question before anything is downloaded.
        public static string Question(LogViceRelease r)
        {
            string v = r != null && r.Version != null ? " " + r.Version : "";
            return "LogVice is not on this PC. Download LogVice" + v + " from GitHub?";
        }

        // What Download and open does, in one or two sentences.
        public static string WhatHappens(LogViceWay w, LogViceRelease r, bool open)
        {
            string size = r != null ? SizeText(w == LogViceWay.Install ? r.Msi : r.Exe) : null;
            string from = "TrackVice downloads " + (w == LogViceWay.Install ? MsiName : ExeName) + (size != null ? " (" + size + ")" : "") + " from github.com/" + Repo + " and checks it against the release's " + AppUpdate.SumsName + ". ";
            switch (w)
            {
                case LogViceWay.Install: return from + "Then it installs LogVice for everyone on this PC" + (open ? " and opens the logs in it." : ".");
                case LogViceWay.Keep: return from + "Then it keeps LogVice.exe in " + KeepFolder + (open ? " and opens the logs in it" : "") + ". Nothing is installed.";
                case LogViceWay.Temp: return from + "This device is deploying, so nothing is installed: LogVice.exe is kept in a temporary folder until TrackVice closes.";
                default: return null;
            }
        }

        public static string SizeText(GitHubAsset a)
        {
            if (a == null || a.Size <= 0) return null;
            return Bytes(a.Size);
        }

        public static string Bytes(long n)
        {
            if (n < 1024 * 1024) return Math.Max(1, (n + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
            return (n / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // Asks GitHub for the latest release. Only the release information is read, nothing is downloaded.
        public static LogViceRelease Latest()
        {
            LogViceRelease r = new LogViceRelease();
            try { Parse(Web.Text(ApiUrl), r); }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                r.Error = hr != null && hr.StatusCode == HttpStatusCode.NotFound ? "LogVice has no release on GitHub yet." : "GitHub could not be reached.";
            }
            catch (Exception) { r.Error = "GitHub could not be reached."; }
            return r;
        }

        static readonly Regex RxTag = new Regex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
        static readonly Regex RxUrl = new Regex("\"html_url\"\\s*:\\s*\"(https://github\\.com/" + Regex.Escape(Repo) + "/releases/tag/[^\"]+)\"", RegexOptions.IgnoreCase);

        public static void Parse(string json, LogViceRelease r)
        {
            json = json ?? "";
            Match tag = RxTag.Match(json);
            if (!tag.Success) { r.Error = "GitHub returned no version of LogVice."; return; }
            r.Version = tag.Groups[1].Value.Trim().TrimStart('v', 'V');
            Match url = RxUrl.Match(json);
            r.Url = url.Success ? url.Groups[1].Value : ReleasesUrl;
            Dictionary<string, GitHubAsset> a = GitHubFiles.Assets(json, Repo, new string[] { MsiName, ExeName, AppUpdate.SumsName });
            a.TryGetValue(MsiName, out r.Msi);
            a.TryGetValue(ExeName, out r.Exe);
            a.TryGetValue(AppUpdate.SumsName, out r.Sums);
            if (r.Sums == null) r.Error = "The LogVice release has no " + AppUpdate.SumsName + ", so its files cannot be checked.";
            else if (r.Exe == null && r.Msi == null) r.Error = "The LogVice release has no LogVice.exe or LogVice.msi.";
        }

        static string keepFolder;

        // TrackVice's own folder for LogVice, under %LOCALAPPDATA%. Set in the tests.
        public static string KeepFolder
        {
            set { keepFolder = value; }
            get
            {
                if (keepFolder != null) return keepFolder;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                return Path.Combine(Path.Combine(local, "TrackVice"), "LogVice");
            }
        }

        // A new folder for this session under the temporary folder. As SYSTEM in a task sequence that is C:\Windows\Temp,
        // where every user may add files to new folders, and LogVice.exe would then start as SYSTEM from it. So the folder
        // gets its own access list: only SYSTEM, the administrators and this user, nothing inherited.
        public static string NewTempFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "TrackVice-LogVice-" + Guid.NewGuid().ToString("N"));
            CreatePrivateFolder(dir);
            return dir;
        }

        // The access list of such a folder, for the user with the SID given.
        public static string PrivateFolderSddl(string userSid)
        {
            string sddl = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
            if (!string.IsNullOrEmpty(userSid) && userSid != "S-1-5-18" && System.Text.RegularExpressions.Regex.IsMatch(userSid, "^S-1-[0-9-]+$"))
                sddl += "(A;OICI;FA;;;" + userSid + ")";
            return sddl;
        }

        // Makes a new folder that nobody else made first, with the access list above on Windows.
        public static void CreatePrivateFolder(string dir)
        {
            if (Directory.Exists(dir) || File.Exists(dir)) throw new IOException("The folder " + dir + " is already there, so it was not used.");
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Directory.CreateDirectory(dir);
                return;
            }
            string sid;
            using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent()) sid = id.User.Value;
            System.Security.AccessControl.DirectorySecurity ds = new System.Security.AccessControl.DirectorySecurity();
            ds.SetSecurityDescriptorSddlForm(PrivateFolderSddl(sid));
            Directory.CreateDirectory(dir, ds);
        }

        // Downloads, checks and puts LogVice where the way says. work is an empty folder for the download.
        // Throws LogViceException with a plain message when it stops; OperationCanceledException when cancelled.
        public static LogViceResult Get(LogViceRelease r, LogViceContext c, string work, IUpdatePrefs prefs, Action<long, long> progress, Func<bool> cancelled)
        {
            string reason;
            LogViceWay w = Way(c, out reason);
            if (w == LogViceWay.Blocked || w == LogViceWay.NeedsWindows) throw new LogViceException(reason);
            if (r == null || r.Error != null) throw new LogViceException(r == null ? "There is no LogVice release to download." : r.Error);
            // An administrator without an MSI in the release still gets the exe in the folder.
            if (w == LogViceWay.Install && r.Msi == null) w = LogViceWay.Keep;
            GitHubAsset asset = w == LogViceWay.Install ? r.Msi : r.Exe;
            if (asset == null) throw new LogViceException("The LogVice release has no " + (w == LogViceWay.Install ? MsiName : ExeName) + ". Download it from " + (r.Url ?? ReleasesUrl) + ".");
            Directory.CreateDirectory(work);
            string sumsPath = Path.Combine(work, AppUpdate.SumsName);
            string file = Path.Combine(work, asset.Name);
            Web.Save(r.Sums.Url, sumsPath, null, cancelled);
            string sums = AppUpdate.ReadSums(sumsPath);
            if (AppUpdate.ExpectedHash(sums, asset.Name) == null)
                throw new LogViceException(AppUpdate.SumsName + " in the LogVice release does not list " + asset.Name + ", so it cannot be checked. Nothing was installed.");
            Web.Save(asset.Url, file, progress, cancelled);
            if (!AppUpdate.Matches(sums, asset.Name, file))
            {
                try { File.Delete(file); } catch (Exception) { }
                throw new LogViceException("The downloaded " + asset.Name + " does not match its checksum in " + AppUpdate.SumsName + ", so it was not used and nothing was installed. Try again later, or download LogVice from " + (r.Url ?? ReleasesUrl) + ".");
            }
            LogViceResult res = new LogViceResult();
            res.Way = w;
            res.Version = r.Version;
            switch (w)
            {
                case LogViceWay.Install:
                    // Held open for reading while Windows asks for administrator rights and installs, so the MSI cannot be swapped.
                    int code;
                    using (FileStream held = AppUpdate.OpenChecked(file, AppUpdate.ExpectedHash(sums, asset.Name))) code = RunMsi(file);
                    if (code == 1223) throw new LogViceException("LogVice was not installed, because administrator rights were not given.");
                    if (code == 1618) throw new LogViceException("Another installation is running. Try again when it has finished.");
                    if (code != 0 && code != 3010 && code != 1641) throw new LogViceException("The LogVice installation ended with code " + code + ".");
                    res.Exe = InstalledExe();
                    // The MSI registers LogVice, so it is found without a saved path.
                    if (prefs != null) prefs.Set(PrefName, null);
                    break;
                case LogViceWay.Keep:
                    Directory.CreateDirectory(KeepFolder);
                    string target = Path.Combine(KeepFolder, ExeName);
                    File.Copy(file, target, true);
                    res.Exe = target;
                    if (prefs != null) prefs.Set(PrefName, target);
                    break;
                default:
                    // The checked file stays where it is, in the temporary folder, and is not remembered after this session.
                    res.Exe = file;
                    SessionExe = file;
                    break;
            }
            return res;
        }

        static int InstallMsi(string file)
        {
            string log = Path.Combine(Path.GetDirectoryName(file), "install.log");
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
                "/i \"" + file + "\" /passive /norestart /l*v \"" + log + "\"");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) { return ex.NativeErrorCode == 1223 ? 1223 : 1603; }
        }

        // Removes the temporary copy when TrackVice closes. LogVice may still have it open, so this only tries.
        public static void CleanUpSession()
        {
            string exe = SessionExe;
            if (exe == null) return;
            try { Directory.Delete(Path.GetDirectoryName(exe), true); } catch (Exception) { }
        }

        // The version of a LogVice.exe, or null.
        public static string VersionOf(string exe)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
                System.Diagnostics.FileVersionInfo v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                string s = v.ProductVersion ?? v.FileVersion;
                return string.IsNullOrEmpty(s) ? null : s.Trim();
            }
            catch (Exception) { return null; }
        }
    }
}
