namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using Microsoft.Win32;

    // What GitHub says about the latest release of TrackVice.
    public sealed class AppUpdateInfo
    {
        public string Latest;
        public string Url;
        public bool Newer;
        public bool Prerelease;
        public string Error;
        public string MsiUrl;
        public string ExeUrl;
        public string SumsUrl;
        public string Notes;
        public DateTime CheckedUtc;

        // An MSI install takes the MSI, a copy run from a folder the exe. Both need SHA256SUMS.txt.
        public bool CanInstallWith(bool installed)
        {
            return Newer && SumsUrl != null && (installed ? MsiUrl != null : ExeUrl != null);
        }

        public bool CanInstall
        {
            get { return CanInstallWith(AppUpdate.IsInstalled); }
        }
    }

    // Where the update settings are kept: the user's TrackVice settings in the app, a dictionary in the tests.
    public interface IUpdatePrefs
    {
        string Get(string name);
        // Null removes it.
        void Set(string name, string value);
    }

    public sealed class MemoryUpdatePrefs : IUpdatePrefs
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string name)
        {
            string v;
            return Values.TryGetValue(name, out v) ? v : null;
        }

        public void Set(string name, string value)
        {
            if (value == null) Values.Remove(name);
            else Values[name] = value;
        }
    }

    // What decides whether TrackVice may ask GitHub for updates. Each one, when true, stops every check and download.
    public sealed class UpdateContext
    {
        // False for the command line, the agent and the task sequence agent.
        public bool Window;
        public bool Screenshot;
        public bool WinPE;
        public bool System;
        // Windows setup or the Enrollment Status Page runs on this device, or the user is defaultuser0.
        public bool Oobe;
        public bool TaskSequence;
        // Not used by the update gate: following a deployment never blocks a check or an install. Other gates read it.
        public bool Deploying;
        public bool Policy;
    }

    public static class UpdateGate
    {
        public const string PolicyKey = @"HKLM\SOFTWARE\Policies\TrackVice";
        public const string PolicyValue = "DisableUpdateCheck";
        public const string NoWindow = "TrackVice checks for updates only when it runs with its window, not on the command line or as the agent.";

        // Null when TrackVice may check, otherwise why not.
        public static string Reason(UpdateContext c)
        {
            if (c == null || !c.Window) return NoWindow;
            if (c.Screenshot) return "No update check while TrackVice takes a screenshot.";
            if (c.WinPE) return "No update check in Windows PE.";
            if (c.System) return "No update check while TrackVice runs as SYSTEM.";
            if (c.TaskSequence) return "No update check while a task sequence runs on this device.";
            if (c.Oobe) return "No update check while Windows setup or the Enrollment Status Page runs on this device.";
            if (c.Policy) return "Your organization has turned this off with the policy value " + PolicyValue + ".";
            return null;
        }

        // Null when TrackVice may install an update now, otherwise why not. Only a real deployment on this device stops it.
        public static string InstallReason(UpdateContext c)
        {
            if (c == null || !c.Window) return NoWindow;
            if (c.WinPE) return "It is not installed in Windows PE.";
            if (c.System) return "It is not installed while TrackVice runs as SYSTEM.";
            if (c.TaskSequence) return "It is not installed while a task sequence runs on this device.";
            if (c.Oobe) return "It is not installed while Windows setup or the Enrollment Status Page runs on this device.";
            if (c.Policy) return "Your organization has turned updates off with the policy value " + PolicyValue + ".";
            return null;
        }

        // Reads the parts of the context that come from the device itself.
        public static void ReadDevice(UpdateContext c, IRegSource reg, string user, bool system, bool tsRunning)
        {
            c.System = system;
            c.TaskSequence = tsRunning;
            c.WinPE = Has(reg.SubKeys(@"HKLM\SYSTEM\CurrentControlSet\Control"), "MiniNT");
            c.Oobe = On(reg, @"HKLM\SYSTEM\Setup", "OOBEInProgress") || On(reg, @"HKLM\SYSTEM\Setup", "SystemSetupInProgress")
                || (user ?? "").StartsWith("defaultuser", StringComparison.OrdinalIgnoreCase);
            c.Policy = On(reg, PolicyKey, PolicyValue);
        }

        public static bool BlockedByPolicy(IRegSource reg)
        {
            return On(reg, PolicyKey, PolicyValue);
        }

        // An ESP or task sequence item that is not finished and was heard from in the last 12 hours.
        public static bool Deploying(Session s, DateTime nowUtc)
        {
            if (s == null) return false;
            foreach (Item i in s.Items)
            {
                string p = i.PhaseKey ?? "";
                if (!p.StartsWith("esp:") && !p.StartsWith("ts:") && p != "intune:autopilot" && !p.StartsWith("intune:esp")) continue;
                if (States.IsFinal(i.State)) continue;
                DateTime last = i.LastUtc > i.StateUtc ? i.LastUtc : i.StateUtc;
                if (nowUtc - last < TimeSpan.FromHours(12)) return true;
            }
            return false;
        }

        static bool On(IRegSource reg, string path, string name)
        {
            RegValue v = reg.Get(path, name);
            long n;
            return v != null && long.TryParse((v.Data ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n != 0;
        }

        static bool Has(string[] names, string name)
        {
            foreach (string n in names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // Updates TrackVice itself from its GitHub releases, as LogVice does.
    public static class AppUpdate
    {
        public const string Repo = "Baldissera88/TrackVice";
        public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";
        public const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        public const string MsiName = "TrackVice.msi";
        public const string ExeName = "TrackVice.exe";
        public const string SumsName = "SHA256SUMS.txt";
        // The files SHA256SUMS.txt lists in every release.
        public static readonly string[] Released = { MsiName, ExeName, "Install-TrackViceAgent.ps1", "Install-TrackViceAgent-Links.ps1", "New-TrackViceCustomer.ps1" };

        public static IUpdatePrefs Prefs = new MemoryUpdatePrefs();

        // Says why no check or download may happen now, or null. Unless the window sets it, nothing may happen.
        public static Func<string> Gate = delegate { return UpdateGate.NoWindow; };

        // Reads a URL as text. Replaced in the tests.
        public static Func<string, string> Fetch = FetchText;

        public static bool HandedOff;

        public static string Blocked
        {
            get
            {
                try
                {
                    Func<string> g = Gate;
                    return g == null ? UpdateGate.NoWindow : g();
                }
                catch (Exception) { return UpdateGate.NoWindow; }
            }
        }

        public static bool Enabled
        {
            get { return Prefs.Get("UpdateCheck") != "0"; }
            set { Prefs.Set("UpdateCheck", value ? null : "0"); }
        }

        public static DateTime? LastChecked
        {
            get
            {
                long ticks;
                string s = Prefs.Get("UpdateChecked");
                if (string.IsNullOrEmpty(s) || !long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return null;
                try { return new DateTime(ticks, DateTimeKind.Utc); }
                catch (ArgumentException) { return null; }
            }
        }

        // The first check comes this long after the window is shown, then one every CheckEvery while TrackVice stays open.
        public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

        // Given to the new TrackVice after an update, so it follows this device again and turns Watch on.
        public const string ResumeWatchArg = "--resume-watch";

        public static bool IsResumeWatch(string arg)
        {
            return string.Equals(arg, ResumeWatchArg, StringComparison.OrdinalIgnoreCase);
        }

        // What the new TrackVice opens: the saved session or folder of a file or folder session, or the watch switch for a live one.
        // The path is made full, since the new TrackVice starts in another folder and reads a leading "-" as a switch.
        public static string[] ReopenArgs(string mode, string sourcePath, bool watching)
        {
            if ((mode == Modes.File || mode == Modes.Folder) && !string.IsNullOrEmpty(sourcePath))
            {
                string full = sourcePath;
                bool drive = full.Length >= 2 && full[1] == ':';
                bool absolute = (drive && full.Length >= 3 && (full[2] == '\\' || full[2] == '/')) || full.StartsWith("\\\\", StringComparison.Ordinal);
                if (!absolute && (drive || !Path.IsPathRooted(full)))
                {
                    try { full = Path.GetFullPath(full); }
                    catch (Exception) { return null; }
                }
                return new string[] { full };
            }
            if (mode == Modes.Live && watching) return new string[] { ResumeWatchArg };
            return null;
        }

        public static void CheckAsync(Action<AppUpdateInfo> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                AppUpdateInfo r = Check();
                if (done != null) done(r);
            });
        }

        public static AppUpdateInfo Check()
        {
            AppUpdateInfo r = new AppUpdateInfo();
            string blocked = Blocked;
            if (blocked != null) { r.Error = blocked; return r; }
            try
            {
                string json = Fetch(ApiUrl);
                Parse(json, Program.Version, r);
                r.CheckedUtc = DateTime.UtcNow;
                Prefs.Set("UpdateChecked", r.CheckedUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                r.Error = hr != null && hr.StatusCode == HttpStatusCode.NotFound ? "No release has been published yet." : "GitHub could not be reached.";
            }
            catch (Exception) { r.Error = "GitHub could not be reached."; }
            return r;
        }

        static readonly Regex RxTag = new Regex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
        static readonly Regex RxUrl = new Regex("\"html_url\"\\s*:\\s*\"(https://github\\.com/" + Regex.Escape(Repo) + "/releases/tag/[^\"]+)\"", RegexOptions.IgnoreCase);
        static readonly Regex RxPre = new Regex("\"prerelease\"\\s*:\\s*true");
        static readonly Regex RxDraft = new Regex("\"draft\"\\s*:\\s*true");
        static readonly Regex RxBody = new Regex("\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

        // Reads a reply of releases/latest. Only assets of this repository's own releases are taken.
        public static void Parse(string json, string current, AppUpdateInfo r)
        {
            json = json ?? "";
            Match tag = RxTag.Match(json);
            if (!tag.Success) { r.Error = "GitHub returned no version."; return; }
            r.Latest = tag.Groups[1].Value.Trim().TrimStart('v', 'V');
            Match url = RxUrl.Match(json);
            r.Url = url.Success ? url.Groups[1].Value : ReleasesUrl;
            // releases/latest skips pre-releases and drafts; a tag like 1.3.0-beta is not offered either.
            r.Prerelease = RxPre.IsMatch(json) || RxDraft.IsMatch(json) || r.Latest.IndexOf('-') >= 0;
            r.Newer = !r.Prerelease && IsNewer(r.Latest, current);
            Dictionary<string, GitHubAsset> assets = GitHubFiles.Assets(json, Repo, new string[] { MsiName, ExeName, SumsName });
            GitHubAsset a;
            if (assets.TryGetValue(MsiName, out a)) r.MsiUrl = a.Url;
            if (assets.TryGetValue(ExeName, out a)) r.ExeUrl = a.Url;
            if (assets.TryGetValue(SumsName, out a)) r.SumsUrl = a.Url;
            Match body = RxBody.Match(json);
            if (body.Success)
            {
                string notes = NotesText(JsonString(body.Groups[1].Value));
                if (notes.Length > MaxNotes) notes = notes.Substring(0, MaxNotes).TrimEnd() + "\u2026";
                r.Notes = notes;
            }
        }

        // The most of the release notes the question before an install shows.
        public const int MaxNotes = 6000;

        public static bool IsNewer(string latest, string current)
        {
            if (!SemVer.IsValid(latest) || !SemVer.IsValid(current)) return false;
            return SemVer.Compare(latest, current) > 0;
        }

        public static string JsonString(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                int code;
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else sb.Append(n);
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        // The release notes as plain lines: the changelog part, without the table of files that release.ps1 adds.
        public static string NotesText(string md)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string raw in (md ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("### Files")) break;
                if (line.StartsWith("|")) continue;
                line = line.Replace("**", "").Replace("`", "");
                if (line.StartsWith("#")) line = line.TrimStart('#').Trim();
                if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line.Substring(2);
                if (line.Length == 0 && (sb.Length == 0 || sb.ToString().EndsWith("\n\n"))) continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString().Trim();
        }

        static string FetchText(string url)
        {
            return GitHubFiles.FetchText(url);
        }

        static void Download(string url, string path, Action<int> progress)
        {
            int last = -1;
            GitHubFiles.Download(url, path, delegate (long done, long length)
            {
                if (progress == null || length <= 0) return;
                int pct = (int)Math.Min(100, done * 100 / length);
                if (pct != last) { last = pct; progress(pct); }
            }, null);
        }

        // The hash SHA256SUMS.txt gives for file, in lower case, or null. Lines are "<hash>  <file>" as sha256sum writes them.
        public static string ExpectedHash(string sums, string file)
        {
            foreach (string raw in (sums ?? "").Split('\n'))
            {
                string line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp != 64 || !IsHex(line.Substring(0, 64))) continue;
                string name = line.Substring(sp).Trim().TrimStart('*');
                if (string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) return line.Substring(0, 64).ToLowerInvariant();
            }
            return null;
        }

        // SHA256SUMS.txt lists a few files in a few hundred bytes. A larger one is not read.
        public const long MaxSums = 64 * 1024;

        public static string ReadSums(string path)
        {
            if (new FileInfo(path).Length > MaxSums) throw new InvalidOperationException(SumsName + " in the release is larger than expected, so nothing was installed.");
            return File.ReadAllText(path);
        }

        static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        // Writes the lines of SHA256SUMS.txt for the files, in the order given.
        public static string Sums(IEnumerable<string> paths)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string p in paths) sb.Append(FileHash(p)).Append("  ").Append(Path.GetFileName(p)).Append('\n');
            return sb.ToString();
        }

        public static string FileHash(string path)
        {
            return AgentUpdate.Sha256OfFile(path).ToLowerInvariant();
        }

        // True when the file has the hash SHA256SUMS.txt gives for name.
        public static bool Matches(string sums, string name, string file)
        {
            string want = ExpectedHash(sums, name);
            return want != null && File.Exists(file) && FileHash(file) == want;
        }

        // A folder only this user can write: under %LOCALAPPDATA%, made new for each download.
        public static string UpdatesRoot
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                return Path.Combine(Path.Combine(local, "TrackVice"), "Updates");
            }
        }

        public const string FolderPrefix = "TrackVice-update-";

        public static string NewWorkFolder(string root, string version)
        {
            string safe = Regex.Replace(version ?? "", "[^0-9A-Za-z.\\-]", "");
            string dir = Path.Combine(root, FolderPrefix + safe);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { dir = Path.Combine(root, FolderPrefix + safe + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)); }
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        // The version in a work folder's name, or null.
        public static string FolderVersion(string file)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(file ?? "") ?? "") ?? "";
            if (!folder.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string v = folder.Substring(FolderPrefix.Length);
            int dash = v.IndexOf('-');
            return dash > 0 && v.Length - dash > 12 ? v.Substring(0, dash) : v;
        }

        public static bool IsInstalled
        {
            get
            {
                try
                {
                    string dir;
                    using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (RegistryKey k = b.OpenSubKey(@"SOFTWARE\TrackVice"))
                        dir = k != null ? k.GetValue("InstallDir") as string : null;
                    return SameFolder(dir, Path.GetDirectoryName(Program.Location));
                }
                catch (Exception) { return false; }
            }
        }

        public static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        // Downloads SHA256SUMS.txt and the MSI or exe to a new folder and checks the hash. Returns the checked file.
        public static string Prepare(AppUpdateInfo r, Action<int> progress)
        {
            string hash;
            return Prepare(r, progress, out hash);
        }

        // The same, and gives the hash the file was checked against, for the helper to check it again before it installs.
        public static string Prepare(AppUpdateInfo r, Action<int> progress, out string hash)
        {
            hash = null;
            string blocked = Blocked;
            if (blocked != null) throw new InvalidOperationException(blocked);
            bool msi = IsInstalled;
            string url = msi ? r.MsiUrl : r.ExeUrl;
            string name = msi ? MsiName : ExeName;
            if (url == null || r.SumsUrl == null) throw new InvalidOperationException("This release has no " + (url == null ? name : SumsName) + " to install automatically. Download it from the release page.");
            string dir = NewWorkFolder(UpdatesRoot, r.Latest);
            string sumsPath = Path.Combine(dir, SumsName);
            string file = Path.Combine(dir, name);
            Download(r.SumsUrl, sumsPath, null);
            string sums = ReadSums(sumsPath);
            if (ExpectedHash(sums, name) == null) throw new InvalidOperationException(SumsName + " in the release does not list " + name + ".");
            Download(url, file, progress);
            if (!Matches(sums, name, file))
            {
                try { File.Delete(file); } catch (Exception) { }
                throw new InvalidOperationException("The downloaded " + name + " does not match its checksum, so it was not installed. Try again later, or download it from the release page.");
            }
            hash = ExpectedHash(sums, name);
            return file;
        }

        // The arguments for the helper: --apply-update msi|exe[:<sha256>] "<file>" "<target>" <pid> ["<path to open>"].
        // The helper is a copy of this same exe, so both ends always agree on this form. Each value is quoted so that it
        // comes back as the one argument it was.
        public static string HelperArgs(string file, string target, int pid, string[] reopen)
        {
            return HelperArgs(file, null, target, pid, reopen);
        }

        public static string HelperArgs(string file, string hash, string target, int pid, string[] reopen)
        {
            bool msi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            StringBuilder a = new StringBuilder();
            a.Append("--apply-update ").Append(msi ? "msi" : "exe");
            if (hash != null && hash.Length == 64 && IsHex(hash)) a.Append(':').Append(hash.ToLowerInvariant());
            a.Append(' ').Append(LogViceLink.Quote(file));
            a.Append(' ').Append(LogViceLink.Quote(target));
            a.Append(' ').Append(pid.ToString(CultureInfo.InvariantCulture));
            if (reopen != null)
                foreach (string p in reopen)
                    if (!string.IsNullOrEmpty(p)) a.Append(' ').Append(LogViceLink.Quote(TrimFolder(p)));
            return a.ToString();
        }

        // A folder without its last backslash, but a drive root stays "C:\", which "C:" would not mean.
        static string TrimFolder(string p)
        {
            string t = p.TrimEnd('\\');
            return t.Length == 0 || t.EndsWith(":", StringComparison.Ordinal) ? p : t;
        }

        // Opens the file so that it cannot be changed while it is used, after checking it still has the hash it was
        // checked against when it was downloaded. The folder is the user's, so anything running as the user could
        // otherwise swap it before Windows asks for administrator rights to install it.
        public static FileStream OpenChecked(string file, string hash)
        {
            if (hash == null || hash.Length != 64 || !IsHex(hash)) throw new InvalidOperationException("The update has no checksum to check it against, so it was not installed.");
            FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                string got;
                using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
                    got = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "");
                if (!string.Equals(got, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The downloaded " + Path.GetFileName(file) + " was changed after it was checked, so it was not installed.");
                fs.Position = 0;
                return fs;
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        // Starts a copy of this exe from the work folder as the helper. It closes TrackVice, installs and starts it again.
        // hash is the one Prepare checked the file against; the helper checks it again before it installs.
        public static void Launch(string file, string hash, string[] reopen)
        {
            bool msi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            string dir = Path.GetDirectoryName(file);
            string helper = Path.Combine(dir, "TrackVice-updater.exe");
            File.Copy(Program.Location, helper, true);
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(helper, HelperArgs(file, hash, Program.Location, System.Diagnostics.Process.GetCurrentProcess().Id, reopen));
            psi.UseShellExecute = false;
            psi.WorkingDirectory = dir;
            try
            {
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception)
            {
                if (!msi) throw;
                // Without the helper nothing holds the file while msiexec runs, so it is at least checked again first.
                using (FileStream held = OpenChecked(file, hash)) { }
                System.Diagnostics.ProcessStartInfo inst = new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
                    "/i " + LogViceLink.Quote(file) + " /passive /norestart");
                inst.UseShellExecute = true;
                inst.Verb = "runas";
                System.Diagnostics.Process.Start(inst);
            }
        }

        // Removes work folders of earlier updates, but not the one this copy runs from, nor one just made.
        public static void CleanUp()
        {
            try
            {
                string root = UpdatesRoot;
                if (!Directory.Exists(root)) return;
                string mine = Path.GetDirectoryName(Program.Location) ?? "";
                foreach (string d in Directory.GetDirectories(root, FolderPrefix + "*"))
                {
                    if (mine.StartsWith(d, StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) < TimeSpan.FromMinutes(10)) continue;
                    try { Directory.Delete(d, true); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    // What the helper window shows while it works.
    public interface IUpdateProgress
    {
        void Status(string text);
        void Problem(string text);
        void Finish();
    }

    // The helper started with --apply-update: waits for TrackVice to close, installs the MSI or replaces the exe, and starts TrackVice again.
    public sealed class UpdateJob
    {
        readonly bool msi;
        readonly string file, target, hash;
        readonly int pid;
        readonly string[] reopen;
        public readonly string Version;
        public int Result = 1;

        public UpdateJob(string[] args)
        {
            // "msi" or "exe", then ":" and the hash the file was checked against.
            string kind = args[1] ?? "";
            int colon = kind.IndexOf(':');
            if (colon >= 0) { hash = kind.Substring(colon + 1); kind = kind.Substring(0, colon); }
            msi = kind == "msi";
            file = args[2];
            target = args[3];
            int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            reopen = new string[Math.Max(0, args.Length - 5)];
            Array.Copy(args, 5, reopen, 0, reopen.Length);
            Version = AppUpdate.FolderVersion(file);
        }

        public void Run(IUpdateProgress ui)
        {
            string problem = null;
            try
            {
                ui.Status("Waiting for TrackVice to close…");
                System.Diagnostics.Process parent = null;
                try { parent = System.Diagnostics.Process.GetProcessById(pid); } catch (Exception) { }
                if (parent != null)
                    using (parent)
                    {
                        bool gone = false;
                        try { gone = parent.WaitForExit(15000); } catch (Exception) { gone = true; }
                        if (!gone)
                        {
                            ui.Status("TrackVice is still open. Closing it…");
                            CloseOrKill(parent);
                        }
                    }
                CloseOthers();
                if (msi)
                {
                    ui.Status("Installing TrackVice" + (Version != null ? " " + Version : "") + ". Windows asks for administrator rights.");
                    problem = InstallMsi();
                }
                else
                {
                    ui.Status("Replacing TrackVice.exe…");
                    problem = ReplaceExe();
                }
            }
            catch (Exception ex) { problem = "The update stopped: " + ex.Message; }
            if (problem != null) ui.Problem(problem);
            ui.Status("Starting TrackVice…");
            try
            {
                StringBuilder a = new StringBuilder();
                foreach (string p in reopen) if (!string.IsNullOrEmpty(p)) a.Append(' ').Append(LogViceLink.Quote(p));
                System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo(target, a.ToString().Trim());
                start.UseShellExecute = false;
                start.WorkingDirectory = Path.GetDirectoryName(target);
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex) { ui.Problem("TrackVice could not start again: " + ex.Message); }
            Result = problem == null ? 0 : 1;
            ui.Finish();
        }

        // Other windows of the same TrackVice.exe, which would keep the file in use. The agent runs a copy elsewhere and is left alone.
        void CloseOthers()
        {
            string name = Path.GetFileNameWithoutExtension(target);
            string full = Path.GetFullPath(target);
            System.Diagnostics.Process[] all;
            try { all = System.Diagnostics.Process.GetProcessesByName(name); }
            catch (Exception) { return; }
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (System.Diagnostics.Process p in all)
                using (p)
                {
                    if (p.Id == self) continue;
                    string path = null;
                    try { path = p.MainModule.FileName; } catch (Exception) { }
                    if (path == null || !string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase)) continue;
                    CloseOrKill(p);
                }
        }

        static void CloseOrKill(System.Diagnostics.Process p)
        {
            try { if (p.HasExited) return; } catch (Exception) { return; }
            try { p.CloseMainWindow(); } catch (Exception) { }
            try { if (p.WaitForExit(8000)) return; } catch (Exception) { return; }
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception) { }
        }

        string InstallMsi()
        {
            string log = Path.Combine(Path.GetDirectoryName(file), "install.log");
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
                    "/i " + LogViceLink.Quote(file) + " /passive /norestart /l*v " + LogViceLink.Quote(log));
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                // Held open for reading only until msiexec has finished, so the checked MSI is the one installed.
                using (FileStream held = AppUpdate.OpenChecked(file, hash))
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit();
                    int code = p.ExitCode;
                    if (code == 1618) return "Another installation is running. Try again when it has finished.";
                    if (code != 0 && code != 3010 && code != 1641)
                        return "The installation ended with code " + code + ". The installer log is " + log + ".";
                }
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? "The update was not installed, because administrator rights were not given." : "The installer could not start: " + ex.Message;
            }
        }

        string ReplaceExe()
        {
            Exception last = null;
            using (FileStream held = AppUpdate.OpenChecked(file, hash))
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    File.Copy(file, target, true);
                    return null;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(500);
                }
            }
            return "TrackVice could not replace " + target + ": " + last.Message + "\r\n\r\nThe new version is in " + file + ". Copy it there yourself.";
        }
    }
}
