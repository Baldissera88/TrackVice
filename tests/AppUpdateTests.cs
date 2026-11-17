namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;

    public static class AppUpdateTests
    {
        const string Dl = "https://github.com/Baldissera88/TrackVice/releases/download/v";

        public static void Run()
        {
            IUpdatePrefs prefs = AppUpdate.Prefs;
            Func<string> gate = AppUpdate.Gate;
            Func<string, string> fetch = AppUpdate.Fetch;
            try
            {
                Replies();
                Hashes();
                SumsFile();
                Gate();
                Checks();
                Helper();
            }
            finally
            {
                AppUpdate.Prefs = prefs;
                AppUpdate.Gate = gate;
                AppUpdate.Fetch = fetch;
            }
        }

        static string Reply(string tag, bool pre, params string[] assets)
        {
            StringBuilder a = new StringBuilder();
            foreach (string n in assets)
            {
                if (a.Length > 0) a.Append(',');
                a.Append("{\"name\":\"" + n + "\",\"browser_download_url\":\"" + Dl + tag + "/" + n + "\"}");
            }
            return "{\"url\":\"https://api.github.com/repos/Baldissera88/TrackVice/releases/1\",\"html_url\":\"https://github.com/Baldissera88/TrackVice/releases/tag/v" + tag + "\","
                + "\"tag_name\":\"v" + tag + "\",\"name\":\"TrackVice " + tag + "\",\"draft\":false,\"prerelease\":" + (pre ? "true" : "false") + ","
                + "\"assets\":[" + a + "],"
                + "\"body\":\"17 November 2026.\\n\\n- **Updates** from GitHub.\\n- Each run is its own row.\\n\\n### Files\\n\\n| File | SHA-256 |\\n|---|---|\"}";
        }

        static AppUpdateInfo Parse(string json, string current)
        {
            AppUpdateInfo r = new AppUpdateInfo();
            AppUpdate.Parse(json, current, r);
            return r;
        }

        static void Replies()
        {
            AppUpdateInfo n = Parse(Reply("1.3.0", false, "TrackVice.msi", "TrackVice.exe", "SHA256SUMS.txt", "Install-TrackViceAgent.ps1"), "1.2.1");
            T.Check(n.Error == null, "a release reply parses");
            T.Eq("1.3.0", n.Latest, "the latest version comes from tag_name without the v");
            T.Check(n.Newer, "1.3.0 is newer than 1.2.1");
            T.Eq(Dl + "1.3.0/TrackVice.msi", n.MsiUrl, "the MSI asset");
            T.Eq(Dl + "1.3.0/TrackVice.exe", n.ExeUrl, "the exe asset");
            T.Eq(Dl + "1.3.0/SHA256SUMS.txt", n.SumsUrl, "the checksums asset");
            T.Eq("https://github.com/Baldissera88/TrackVice/releases/tag/v1.3.0", n.Url, "the release page");
            T.Check(n.CanInstallWith(true) && n.CanInstallWith(false), "a full release installs as MSI and as exe");
            T.Check(n.Notes.StartsWith("17 November 2026.") && n.Notes.Contains("\u2022 Updates from GitHub.") && !n.Notes.Contains("Files") && !n.Notes.Contains("|"), "the notes are the changelog part, without the table of files");

            AppUpdateInfo same = Parse(Reply("1.2.1", false, "TrackVice.msi", "TrackVice.exe", "SHA256SUMS.txt"), "1.2.1");
            T.Check(same.Error == null && !same.Newer && !same.CanInstallWith(true), "the same version is not offered");
            AppUpdateInfo older = Parse(Reply("1.1.9", false, "TrackVice.msi", "TrackVice.exe", "SHA256SUMS.txt"), "1.2.1");
            T.Check(!older.Newer, "an older version is not offered");
            T.Check(Parse(Reply("1.10.0", false), "1.9.3").Newer, "versions compare by number, not as text");

            AppUpdateInfo pre = Parse(Reply("1.4.0", true, "TrackVice.msi", "TrackVice.exe", "SHA256SUMS.txt"), "1.2.1");
            T.Check(pre.Prerelease && !pre.Newer && !pre.CanInstallWith(true), "a pre-release is not offered");
            AppUpdateInfo beta = Parse(Reply("1.4.0-beta.1", false, "TrackVice.msi", "TrackVice.exe", "SHA256SUMS.txt"), "1.2.1");
            T.Check(beta.Prerelease && !beta.Newer, "a pre-release tag is not offered");

            AppUpdateInfo noSums = Parse(Reply("1.3.0", false, "TrackVice.msi", "TrackVice.exe"), "1.2.1");
            T.Check(noSums.Newer && noSums.SumsUrl == null && !noSums.CanInstallWith(true) && !noSums.CanInstallWith(false), "a release without SHA256SUMS.txt is only offered as a download");
            AppUpdateInfo noMsi = Parse(Reply("1.3.0", false, "TrackVice.exe", "SHA256SUMS.txt"), "1.2.1");
            T.Check(!noMsi.CanInstallWith(true) && noMsi.CanInstallWith(false), "without the MSI only a copy run from a folder installs");
            AppUpdateInfo noAssets = Parse(Reply("1.3.0", false), "1.2.1");
            T.Check(noAssets.Newer && noAssets.MsiUrl == null && noAssets.ExeUrl == null && !noAssets.CanInstallWith(false), "a release without assets is only offered as a download");

            string foreign = Reply("1.3.0", false, "SHA256SUMS.txt").Replace("\"assets\":[", "\"assets\":[{\"name\":\"TrackVice.exe\",\"browser_download_url\":\"https://github.com/Fabrikam/TrackVice/releases/download/v1.3.0/TrackVice.exe\"},");
            T.Check(Parse(foreign, "1.2.1").ExeUrl == null, "assets of another repository are not taken");

            AppUpdateInfo bad = Parse("{\"message\":\"Not Found\"}", "1.2.1");
            T.Check(bad.Error != null && !bad.Newer, "a reply without a version is an error");
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-appupdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Hashes()
        {
            string h = new string('a', 32) + new string('B', 32);
            string sums = h + "  TrackVice.msi\n" + new string('1', 64) + " *TrackVice.exe\r\nnot a line\n" + new string('2', 63) + "  Short.txt\n";
            T.Eq(h.ToLowerInvariant(), AppUpdate.ExpectedHash(sums, "TrackVice.msi"), "the hash of a file, in lower case");
            T.Eq(new string('1', 64), AppUpdate.ExpectedHash(sums, "trackvice.exe"), "a binary-mode line and any case of the name");
            T.Check(AppUpdate.ExpectedHash(sums, "Short.txt") == null, "a line without a 64-character hash is ignored");
            T.Check(AppUpdate.ExpectedHash(sums, "Other.exe") == null, "a file that is not listed has no hash");
            T.Check(AppUpdate.ExpectedHash("zz" + new string('0', 62) + "  TrackVice.exe", "TrackVice.exe") == null, "a hash that is not hex is ignored");

            string dir = TempDir();
            try
            {
                string file = Path.Combine(dir, "TrackVice.exe");
                File.WriteAllText(file, "MZ Contoso test build");
                string good = AppUpdate.FileHash(file) + "  TrackVice.exe\n";
                T.Check(AppUpdate.Matches(good, "TrackVice.exe", file), "a download that matches its checksum is accepted");
                T.Check(AppUpdate.Matches(good.ToUpperInvariant().Replace("TRACKVICE.EXE", "TrackVice.exe"), "TrackVice.exe", file), "an upper-case hash is accepted");
                File.WriteAllText(file, "MZ Contoso changed build");
                T.Check(!AppUpdate.Matches(good, "TrackVice.exe", file), "a download that does not match is refused");
                T.Check(!AppUpdate.Matches("", "TrackVice.exe", file), "a download without a listed hash is refused");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        static void SumsFile()
        {
            string dir = TempDir();
            try
            {
                List<string> paths = new List<string>();
                foreach (string n in AppUpdate.Released)
                {
                    string p = Path.Combine(dir, n);
                    File.WriteAllText(p, "Contoso " + n);
                    paths.Add(p);
                }
                string sums = AppUpdate.Sums(paths);
                string[] lines = sums.TrimEnd('\n').Split('\n');
                T.Eq(5, lines.Length, "SHA256SUMS.txt has one line for each released file");
                bool shape = true;
                foreach (string l in lines) if (!Regex.IsMatch(l, "^[0-9a-f]{64}  [A-Za-z0-9.-]+$")) shape = false;
                T.Check(shape, "each line is a lower-case hash, two spaces and the file name, as sha256sum writes it");
                foreach (string p in paths)
                    T.Eq(AppUpdate.FileHash(p), AppUpdate.ExpectedHash(sums, Path.GetFileName(p)), "the sums file gives the hash of " + Path.GetFileName(p));
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }

            // The release steps list the same files.
            string sh = File.Exists("sums.sh") ? File.ReadAllText("sums.sh") : "";
            string ps = File.Exists("release.ps1") ? File.ReadAllText("release.ps1") : "";
            string pkg = File.Exists("package.sh") ? File.ReadAllText("package.sh") : "";
            T.Check(pkg.Contains("./sums.sh"), "package.sh writes SHA256SUMS.txt");
            foreach (string n in AppUpdate.Released)
            {
                T.Check(sh.Contains(n), "sums.sh lists " + n);
                T.Check(ps.Contains("'" + n + "'"), "release.ps1 lists " + n);
            }
            T.Check(Regex.IsMatch(ps, @"release', 'create'[^\r\n]*\$sumsPath"), "release.ps1 uploads SHA256SUMS.txt");
            T.Check(ps.Contains("'{0}  {1}' -f $hash.ToLowerInvariant()"), "release.ps1 writes the sha256sum format");
        }

        static void Gate()
        {
            T.Eq(UpdateGate.NoWindow, UpdateGate.Reason(null), "no context means no check");
            T.Eq(UpdateGate.NoWindow, UpdateGate.Reason(new UpdateContext()), "without the window (command line, agent, task sequence agent) there is no check");
            UpdateContext ok = new UpdateContext();
            ok.Window = true;
            T.Check(UpdateGate.Reason(ok) == null, "the window on an ordinary PC may check");

            string[] names = { "Screenshot", "WinPE", "System", "Oobe", "TaskSequence", "Deploying", "Policy" };
            foreach (string n in names)
            {
                UpdateContext c = new UpdateContext();
                c.Window = true;
                typeof(UpdateContext).GetField(n).SetValue(c, true);
                T.Check(UpdateGate.Reason(c) != null, "no check when " + n + " is set");
            }

            MemoryRegistry reg = new MemoryRegistry();
            UpdateContext d = new UpdateContext();
            UpdateGate.ReadDevice(d, reg, "Contoso\\adele", false, false);
            T.Check(!d.WinPE && !d.Oobe && !d.Policy && !d.System && !d.TaskSequence, "an ordinary device sets nothing");

            reg.Touch(@"HKLM\SYSTEM\CurrentControlSet\Control\MiniNT");
            reg.Dword(@"HKLM\SYSTEM\Setup", "OOBEInProgress", 0);
            reg.Dword(UpdateGate.PolicyKey, UpdateGate.PolicyValue, 0);
            UpdateGate.ReadDevice(d, reg, "adele", true, true);
            T.Check(d.WinPE, "the MiniNT key means Windows PE");
            T.Check(!d.Oobe, "OOBEInProgress 0 is not OOBE");
            T.Check(!d.Policy, "DisableUpdateCheck 0 does not turn it off");
            T.Check(d.System && d.TaskSequence, "SYSTEM and a running task sequence are taken over");

            MemoryRegistry oobe = new MemoryRegistry();
            oobe.Dword(@"HKLM\SYSTEM\Setup", "OOBEInProgress", 1);
            UpdateContext o = new UpdateContext();
            UpdateGate.ReadDevice(o, oobe, "adele", false, false);
            T.Check(o.Oobe, "OOBEInProgress 1 is OOBE");
            UpdateContext du = new UpdateContext();
            UpdateGate.ReadDevice(du, new MemoryRegistry(), "defaultuser0", false, false);
            T.Check(du.Oobe, "defaultuser0 means the ESP");

            MemoryRegistry pol = new MemoryRegistry();
            pol.Dword(UpdateGate.PolicyKey, UpdateGate.PolicyValue, 1);
            T.Check(UpdateGate.BlockedByPolicy(pol), "DisableUpdateCheck 1 turns it off");
            UpdateContext p = new UpdateContext();
            p.Window = true;
            UpdateGate.ReadDevice(p, pol, "adele", false, false);
            T.Check(UpdateGate.Reason(p).Contains("DisableUpdateCheck"), "the policy is named as the reason");

            DateTime now = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
            T.Check(!UpdateGate.Deploying(null, now), "no session is no deployment");
            Session s = new Session();
            Item app = new Item();
            app.Key = "app";
            app.PhaseKey = Phases.IntuneApps;
            app.State = State.Installing;
            app.LastUtc = now.AddMinutes(-2);
            s.Items.Add(app);
            T.Check(!UpdateGate.Deploying(s, now), "an Intune app installing is not a deployment of the device");
            Item esp = new Item();
            esp.Key = "esp";
            esp.PhaseKey = Phases.DeviceSetup;
            esp.State = State.Pending;
            esp.LastUtc = now.AddMinutes(-5);
            s.Items.Add(esp);
            T.Check(UpdateGate.Deploying(s, now), "an ESP item waiting is a deployment");
            esp.LastUtc = now.AddDays(-3);
            T.Check(!UpdateGate.Deploying(s, now), "an ESP item last heard of days ago is not");
            esp.LastUtc = now.AddMinutes(-5);
            esp.State = State.Succeeded;
            T.Check(!UpdateGate.Deploying(s, now), "a finished ESP is not a deployment");
            Item ts = new Item();
            ts.Key = "ts";
            ts.PhaseKey = Phases.FullOs;
            ts.State = State.Installing;
            ts.LastUtc = now.AddMinutes(-1);
            s.Items.Add(ts);
            T.Check(UpdateGate.Deploying(s, now), "a task sequence step running is a deployment");
        }

        static void Checks()
        {
            MemoryUpdatePrefs prefs = new MemoryUpdatePrefs();
            AppUpdate.Prefs = prefs;
            int calls = 0;
            AppUpdate.Fetch = delegate (string url)
            {
                calls++;
                T.Eq(AppUpdate.ApiUrl, url, "the check asks releases/latest");
                return Reply("99.0.0", false, "TrackVice.msi", "TrackVice.exe", "SHA256SUMS.txt");
            };

            AppUpdate.Gate = delegate { return UpdateGate.NoWindow; };
            AppUpdateInfo blocked = AppUpdate.Check();
            T.Eq(0, calls, "a blocked check makes no network call");
            T.Eq(UpdateGate.NoWindow, blocked.Error, "a blocked check says why");
            T.Check(prefs.Get("UpdateChecked") == null, "a blocked check is not counted as a check");
            bool threw = false;
            try { AppUpdate.Prepare(Parse(Reply("99.0.0", false, "TrackVice.msi", "TrackVice.exe", "SHA256SUMS.txt"), "1.2.1"), null); }
            catch (InvalidOperationException) { threw = true; }
            T.Check(threw && calls == 0, "a blocked install downloads nothing");
            AppUpdate.Gate = null;
            T.Check(AppUpdate.Check().Error != null && calls == 0, "without a gate nothing is asked");

            AppUpdate.Gate = delegate { return null; };
            T.Check(AppUpdate.DueAt(DateTime.UtcNow), "never checked means due");
            AppUpdateInfo r = AppUpdate.Check();
            T.Eq(1, calls, "an allowed check asks GitHub once");
            T.Check(r.Error == null && r.Newer && r.Latest == "99.0.0", "an allowed check finds the newer version");
            T.Check(AppUpdate.LastChecked.HasValue, "the time of the check is kept");
            T.Check(!AppUpdate.DueAt(DateTime.UtcNow.AddHours(1)), "not again the same day");
            T.Check(AppUpdate.DueAt(DateTime.UtcNow.AddHours(21)), "due again the next day");

            T.Check(AppUpdate.Enabled, "checking is on by default");
            AppUpdate.Enabled = false;
            T.Eq("0", prefs.Get("UpdateCheck"), "turning it off is kept");
            T.Check(!AppUpdate.Enabled, "turned off");
            AppUpdate.Enabled = true;
            T.Check(AppUpdate.Enabled && prefs.Get("UpdateCheck") == null, "turned on again");

            AppUpdate.Fetch = delegate (string url) { throw new System.Net.WebException("offline"); };
            AppUpdateInfo off = AppUpdate.Check();
            T.Eq("GitHub could not be reached.", off.Error, "no network is said plainly");
        }

        static void Helper()
        {
            string root = TempDir();
            try
            {
                string a = AppUpdate.NewWorkFolder(root, "1.3.0");
                T.Check(Directory.Exists(a) && Path.GetFileName(a) == "TrackVice-update-1.3.0", "the work folder is named after the version");
                File.WriteAllText(Path.Combine(a, "TrackVice.exe"), "old");
                string b = AppUpdate.NewWorkFolder(root, "1.3.0");
                T.Check(!File.Exists(Path.Combine(b, "TrackVice.exe")), "each download gets a new, empty folder");
                T.Eq("1.3.0", AppUpdate.FolderVersion(Path.Combine(b, "TrackVice.msi")), "the helper reads the version from the folder");
                T.Eq("1.3.0", AppUpdate.FolderVersion(Path.Combine(Path.Combine(root, "TrackVice-update-1.3.0-638675712000000000"), "TrackVice.msi")), "a folder with a time added still gives the version");
                T.Check(AppUpdate.FolderVersion(Path.Combine(root, "TrackVice.msi")) == null, "another folder gives no version");
                string args = AppUpdate.HelperArgs(Path.Combine(b, "TrackVice.msi"), @"C:\Program Files\TrackVice\TrackVice.exe", 4321, new string[] { @"C:\Logs\CONTOSO-PC01\" });
                T.Check(args.StartsWith("--apply-update msi \"") && args.Contains("\" \"C:\\Program Files\\TrackVice\\TrackVice.exe\" 4321 \"C:\\Logs\\CONTOSO-PC01\""), "the helper gets the kind, the file, the target, the process and what to open again");
                UpdateJob job = new UpdateJob(new string[] { "--apply-update", "exe", Path.Combine(b, "TrackVice.exe"), @"D:\Tools\TrackVice.exe", "4321", @"C:\Logs\CONTOSO-PC01" });
                T.Eq("1.3.0", job.Version, "the helper knows the version it installs");
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
            T.Check(AppUpdate.SameFolder(@"/opt/Contoso/TrackVice/", @"/opt/Contoso/TrackVice"), "the install folder compares without the trailing separator");
            T.Check(!AppUpdate.SameFolder(null, @"/opt/Contoso"), "no install folder is not installed");
        }
    }
}
