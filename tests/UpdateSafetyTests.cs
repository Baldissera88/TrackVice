namespace TrackVice.Tests
{
    using System;
    using System.IO;
    using System.Net;
    using System.Text;

    // What is checked before TrackVice downloads, keeps or starts a file from GitHub, and how its helper and LogVice are started.
    public static class UpdateSafetyTests
    {
        public static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "tv-update-safety-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                Hosts();
                Redirects();
                Sizes(root);
                ExistingFileKept(root);
                Helper(root);
                Reopen(root);
                Notes();
                TempFolder(root);
                LogViceTargets();
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        static void Hosts()
        {
            T.Check(GitHubFiles.AllowedUrl("https://api.github.com/repos/Baldissera88/TrackVice/releases/latest"), "the API is allowed");
            T.Check(GitHubFiles.AllowedUrl("https://github.com/Baldissera88/TrackVice/releases/download/v2.1.0/TrackVice.msi"), "a release file is allowed");
            T.Check(GitHubFiles.AllowedUrl("https://objects.githubusercontent.com/github-production-release-asset/1/2?x=y"), "GitHub's release storage is allowed");
            T.Check(GitHubFiles.AllowedUrl("https://release-assets.githubusercontent.com/github-production-release-asset/1/2"), "GitHub's newer release storage is allowed");
            T.Check(!GitHubFiles.AllowedUrl("http://github.com/Baldissera88/TrackVice/releases/download/v2.1.0/TrackVice.msi"), "plain http is refused");
            T.Check(!GitHubFiles.AllowedUrl("http://objects.githubusercontent.com/x"), "plain http to the storage is refused");
            T.Check(!GitHubFiles.AllowedUrl("https://github.com.contoso.example/x"), "a look-alike host is refused");
            T.Check(!GitHubFiles.AllowedUrl("https://githubusercontent.com.contoso.example/x"), "a look-alike storage host is refused");
            T.Check(!GitHubFiles.AllowedUrl("https://github.com@contoso.example/x"), "a user name in front of another host is refused");
            T.Check(!GitHubFiles.AllowedUrl("https://github.com:8443/x"), "another port is refused");
            T.Check(!GitHubFiles.AllowedUrl("file:///C:/Windows/notepad.exe"), "a file address is refused");
            T.Check(!GitHubFiles.AllowedUrl(null) && !GitHubFiles.AllowedUrl("TrackVice.msi"), "no address or a relative one is refused");
            bool threw = false;
            try { GitHubFiles.Request("http://github.com/Baldissera88/TrackVice/releases", 1000); }
            catch (WebException) { threw = true; }
            T.Check(threw, "no request is made to an address that is not allowed");
        }

        static void Redirects()
        {
            string start = "https://github.com/Baldissera88/TrackVice/releases/download/v2.1.0/TrackVice.msi";
            T.Eq("https://objects.githubusercontent.com/a/b?sig=1", GitHubFiles.NextUrl(start, "https://objects.githubusercontent.com/a/b?sig=1"), "a redirect to the storage is followed");
            T.Eq("https://github.com/Baldissera88/TrackVice/releases/tag/v2.1.0", GitHubFiles.NextUrl(start, "/Baldissera88/TrackVice/releases/tag/v2.1.0"), "a relative redirect stays on the same host");
            foreach (string bad in new string[] { "http://objects.githubusercontent.com/a", "https://contoso.example/TrackVice.msi", "//contoso.example/x", "", null })
            {
                bool threw = false;
                try { GitHubFiles.NextUrl(start, bad); }
                catch (WebException) { threw = true; }
                T.Check(threw, "a redirect to " + (bad ?? "nothing") + " is refused");
            }
        }

        static void Sizes(string root)
        {
            T.Eq("abc", GitHubFiles.ReadText(new MemoryStream(Encoding.UTF8.GetBytes("abc")), 3), "a reply within the limit is read");
            bool threw = false;
            try { GitHubFiles.ReadText(new MemoryStream(new byte[200000]), 100000); }
            catch (IOException) { threw = true; }
            T.Check(threw, "a reply over the limit is refused");

            string sums = Path.Combine(root, "SHA256SUMS.txt");
            File.WriteAllText(sums, new string('a', 64) + "  TrackVice.msi\n");
            T.Check(AppUpdate.ReadSums(sums).StartsWith(new string('a', 64)), "a normal SHA256SUMS.txt is read");
            File.WriteAllText(sums, new string('x', (int)AppUpdate.MaxSums + 1));
            threw = false;
            try { AppUpdate.ReadSums(sums); }
            catch (InvalidOperationException) { threw = true; }
            T.Check(threw, "a SHA256SUMS.txt far larger than any real one is not read");
        }

        static void ExistingFileKept(string root)
        {
            string path = Path.Combine(root, "kept.exe");
            File.WriteAllText(path, "there before");
            try { GitHubFiles.Download("http://127.0.0.1:9/TrackVice.exe", path, null, null); }
            catch (Exception) { }
            T.Check(File.Exists(path), "a download that fails never removes a file that was there before");
        }

        static void Helper(string root)
        {
            string dir = Path.Combine(root, "TrackVice-update-9.9.9");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "TrackVice.exe");
            File.WriteAllText(file, "new TrackVice");
            string hash = AppUpdate.FileHash(file);

            string args = AppUpdate.HelperArgs(file, hash, @"C:\Program Files\TrackVice\TrackVice.exe", 42, new string[] { @"C:\", @"D:\Logs\PC01\" });
            T.Check(args.StartsWith("--apply-update exe:" + hash + " "), "the helper is given the hash the file was checked against");
            T.Check(args.EndsWith(" 42 \"C:\\\\\" \"D:\\Logs\\PC01\""), "a drive root stays a root and comes back as one argument; a folder loses its last backslash");
            T.Check(!AppUpdate.HelperArgs(file, "not a hash", "t.exe", 1, null).Contains(":not"), "something that is not a hash is not passed on");

            using (FileStream fs = AppUpdate.OpenChecked(file, hash.ToUpperInvariant()))
                T.Check(fs.CanRead && fs.Position == 0, "the checked file is opened when it still has its hash");
            File.WriteAllText(file, "swapped after the check");
            bool threw = false;
            try { using (AppUpdate.OpenChecked(file, hash)) { } }
            catch (InvalidOperationException) { threw = true; }
            T.Check(threw, "a file changed after the check is not installed");
            threw = false;
            try { using (AppUpdate.OpenChecked(file, null)) { } }
            catch (InvalidOperationException) { threw = true; }
            T.Check(threw, "without a hash nothing is installed");

            UpdateJob job = new UpdateJob(new string[] { "--apply-update", "msi:" + hash, Path.Combine(dir, "TrackVice.msi"), @"C:\Program Files\TrackVice\TrackVice.exe", "42" });
            T.Eq("9.9.9", job.Version, "the helper reads its arguments with the hash in them");
        }

        static void Reopen(string root)
        {
            string here = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                string[] a = AppUpdate.ReopenArgs(Modes.Folder, "--wall", false);
                T.Check(a != null && a.Length == 1 && Path.IsPathRooted(a[0]) && !a[0].StartsWith("-"), "a folder TrackVice opens again is given as a full path, never as a switch");
            }
            finally { Environment.CurrentDirectory = here; }
            string[] w = AppUpdate.ReopenArgs(Modes.Live, null, true);
            T.Check(w != null && w.Length == 1 && w[0] == AppUpdate.ResumeWatchArg, "watching still resumes");
        }

        static void Notes()
        {
            string body = new string('x', 50000);
            string json = "{\"tag_name\":\"v99.0.0\",\"body\":\"" + body + "\"}";
            AppUpdateInfo r = new AppUpdateInfo();
            AppUpdate.Parse(json, "1.0.0", r);
            T.Check(r.Notes != null && r.Notes.Length <= AppUpdate.MaxNotes + 1, "very long release notes are cut");
        }

        static void TempFolder(string root)
        {
            string sddl = LogViceGet.PrivateFolderSddl("S-1-5-21-1-2-3-1001");
            T.Eq("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;S-1-5-21-1-2-3-1001)", sddl, "the temporary LogVice folder is for SYSTEM, the administrators and this user, nothing inherited");
            T.Check(!sddl.Contains(";BU)") && !sddl.Contains(";WD)") && !sddl.Contains(";AU)"), "users, everyone and authenticated users get nothing");
            T.Eq("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", LogViceGet.PrivateFolderSddl("S-1-5-18"), "as SYSTEM no third entry is added");
            T.Eq("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", LogViceGet.PrivateFolderSddl(")(A;;FA;;;WD"), "something that is not a SID is left out");

            string there = Path.Combine(root, "made-first");
            Directory.CreateDirectory(there);
            bool threw = false;
            try { LogViceGet.CreatePrivateFolder(there); }
            catch (IOException) { threw = true; }
            T.Check(threw, "a folder someone made first is not used");

            string a = LogViceGet.NewTempFolder();
            string b = LogViceGet.NewTempFolder();
            try
            {
                string name = Path.GetFileName(a);
                T.Check(Directory.Exists(a) && a != b && name.Length == "TrackVice-LogVice-".Length + 32, "each session gets a new folder whose name cannot be guessed");
            }
            finally
            {
                try { Directory.Delete(a, true); } catch (Exception) { }
                try { Directory.Delete(b, true); } catch (Exception) { }
            }
        }

        static void LogViceTargets()
        {
            LogViceJump j = new LogViceJump(5, null);
            T.Eq("\".\\--theme\" --line 5", LogViceLink.BuildArguments(new string[] { "--theme" }, j, "2.0.0"), "a path that looks like an option is opened as a file");
            T.Eq("\"C:\\Windows\\CCM\\Logs\\AppEnforce.log\"", LogViceLink.BuildArguments(new string[] { @"C:\Windows\CCM\Logs\AppEnforce.log" }, null, "2.0.0"), "a normal path is unchanged");
            T.Eq("\"evt:Microsoft-Windows-AAD/Operational\"", LogViceLink.BuildArguments(new string[] { "evt:Microsoft-Windows-AAD/Operational" }, null, "2.0.0"), "an event channel is unchanged");
        }
    }
}
