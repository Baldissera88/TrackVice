namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public static class LogViceGetTests
    {
        const string Dl = "https://github.com/Baldissera88/LogVice/releases/download/v1.5.6/";

        // GitHub as the tests see it: a reply for the API and file contents by URL. Every call is written down.
        sealed class FakeWeb : IReleaseWeb
        {
            public readonly List<string> Asked = new List<string>();
            public readonly List<string> Saved = new List<string>();
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public string Reply;
            public bool CancelDuring;

            public string Text(string url)
            {
                Asked.Add(url);
                return Reply;
            }

            public void Save(string url, string path, Action<long, long> progress, Func<bool> cancelled)
            {
                Saved.Add(url);
                byte[] b;
                if (!Files.TryGetValue(url, out b)) throw new System.Net.WebException("not found");
                if (CancelDuring && cancelled != null && cancelled()) throw new OperationCanceledException("The download was cancelled.");
                File.WriteAllBytes(path, b);
                if (progress != null) progress(b.Length, b.Length);
            }
        }

        public static void Run()
        {
            IReleaseWeb web = LogViceGet.Web;
            Func<string, int> msi = LogViceGet.RunMsi;
            Func<string> installed = LogViceGet.InstalledExe;
            string root = Path.Combine(Path.GetTempPath(), "tv-logvice-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                LogViceGet.KeepFolder = Path.Combine(root, "keep");
                Replies();
                Ways();
                AskFirst();
                KeepCopy(root);
                HashMismatch(root);
                Install(root);
                MsiHeld(root);
                Deploying(root);
                Cancelled(root);
                Blocked(root);
            }
            finally
            {
                LogViceGet.Web = web;
                LogViceGet.RunMsi = msi;
                LogViceGet.InstalledExe = installed;
                LogViceGet.KeepFolder = null;
                LogViceGet.SessionExe = null;
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        static string Reply(bool withMsi)
        {
            StringBuilder a = new StringBuilder();
            a.Append("{\"name\":\"LogVice.exe\",\"uploader\":{\"login\":\"contoso\"},\"size\":1843200,\"browser_download_url\":\"" + Dl + "LogVice.exe\"},");
            if (withMsi) a.Append("{\"name\":\"LogVice.msi\",\"size\":2211840,\"browser_download_url\":\"" + Dl + "LogVice.msi\"},");
            a.Append("{\"name\":\"SHA256SUMS.txt\",\"size\":180,\"browser_download_url\":\"" + Dl + "SHA256SUMS.txt\"},");
            a.Append("{\"name\":\"LogVice.exe\",\"size\":5,\"browser_download_url\":\"https://github.com/Contoso/Other/releases/download/v9/LogVice.exe\"}");
            return "{\"html_url\":\"https://github.com/Baldissera88/LogVice/releases/tag/v1.5.6\",\"tag_name\":\"v1.5.6\",\"draft\":false,\"prerelease\":false,\"assets\":[" + a + "]}";
        }

        static LogViceContext Ctx(bool admin)
        {
            LogViceContext c = new LogViceContext();
            c.Window = true;
            c.Admin = admin;
            return c;
        }

        static void Replies()
        {
            LogViceRelease r = new LogViceRelease();
            LogViceGet.Parse(Reply(true), r);
            T.Check(r.Error == null, "a LogVice release reply parses");
            T.Eq("1.5.6", r.Version, "the LogVice version comes from the tag");
            T.Eq(Dl + "LogVice.exe", r.Exe.Url, "the exe of LogVice's own release, not of another repository");
            T.Eq(1843200L, r.Exe.Size, "the exe's size comes from its asset");
            T.Eq(2211840L, r.Msi.Size, "the MSI's size comes from its asset");
            T.Eq(Dl + "SHA256SUMS.txt", r.Sums.Url, "the checksums file");
            T.Eq("1.8 MB", LogViceGet.SizeText(r.Exe), "the size is shown in MB");
            T.Eq("LogVice is not on this PC. Download LogVice 1.5.6 from GitHub?", LogViceGet.Question(r), "the question names the version");

            LogViceRelease noSums = new LogViceRelease();
            LogViceGet.Parse(Reply(true).Replace("SHA256SUMS.txt", "README.txt"), noSums);
            T.Check(noSums.Error != null && noSums.Error.Contains("SHA256SUMS.txt"), "a release without checksums is not offered");
        }

        static void Ways()
        {
            string why;
            T.Eq(LogViceWay.Blocked, LogViceGet.Way(new LogViceContext(), out why), "no download without the window");
            LogViceContext shot = Ctx(true);
            shot.Screenshot = true;
            T.Eq(LogViceWay.Blocked, LogViceGet.Way(shot, out why), "no download for a screenshot");
            T.Eq(LogViceWay.Install, LogViceGet.Way(Ctx(true), out why), "an administrator installs the MSI");
            T.Eq(LogViceWay.Keep, LogViceGet.Way(Ctx(false), out why), "a user keeps the exe in TrackVice's folder");
            LogViceContext pe = Ctx(true);
            pe.WinPE = true;
            pe.TaskSequence = true;
            T.Eq(LogViceWay.NeedsWindows, LogViceGet.Way(pe, out why), "Windows PE says LogVice needs the full Windows");
            T.Check(why.Contains("full Windows"), "and says why");
            LogViceContext oobe = Ctx(true);
            oobe.Oobe = true;
            T.Eq(LogViceWay.Temp, LogViceGet.Way(oobe, out why), "Shift+F10 in OOBE keeps a temporary copy, even for an administrator");
            LogViceContext ts = Ctx(true);
            ts.TaskSequence = true;
            ts.System = true;
            T.Eq(LogViceWay.Temp, LogViceGet.Way(ts, out why), "F8 in a task sequence keeps a temporary copy");
            LogViceContext esp = Ctx(false);
            esp.Deploying = true;
            T.Eq(LogViceWay.Temp, LogViceGet.Way(esp, out why), "a device TrackVice sees deploying keeps a temporary copy");
            LogViceContext system = Ctx(true);
            system.System = true;
            T.Eq(LogViceWay.Blocked, LogViceGet.Way(system, out why), "not as SYSTEM outside a deployment");
            LogViceContext policy = Ctx(true);
            policy.Policy = true;
            T.Eq(LogViceWay.Blocked, LogViceGet.Way(policy, out why), "the policy value stops it");
        }

        static void AskFirst()
        {
            FakeWeb w = new FakeWeb();
            w.Reply = Reply(true);
            LogViceGet.Web = w;
            LogViceRelease r = LogViceGet.Latest();
            T.Check(r.Error == null && r.Version == "1.5.6", "the latest release is read before asking");
            T.Eq(1, w.Asked.Count, "only the release information is read");
            T.Eq(0, w.Saved.Count, "nothing is downloaded before the click");
            T.Check(LogViceGet.WhatHappens(LogViceWay.Keep, r, true).Contains("1.8 MB"), "the question says the size");
        }

        static FakeWeb Web(string root, bool goodHash, bool withMsi)
        {
            FakeWeb w = new FakeWeb();
            w.Reply = Reply(withMsi);
            byte[] exe = Encoding.ASCII.GetBytes("MZ LogVice exe");
            byte[] msi = Encoding.ASCII.GetBytes("MSI LogVice");
            string tmp = Path.Combine(root, "hash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            File.WriteAllBytes(Path.Combine(tmp, "LogVice.exe"), goodHash ? exe : Encoding.ASCII.GetBytes("something else"));
            File.WriteAllBytes(Path.Combine(tmp, "LogVice.msi"), msi);
            string sums = AppUpdate.Sums(new string[] { Path.Combine(tmp, "LogVice.exe"), Path.Combine(tmp, "LogVice.msi") });
            w.Files[Dl + "LogVice.exe"] = exe;
            w.Files[Dl + "LogVice.msi"] = msi;
            w.Files[Dl + "SHA256SUMS.txt"] = Encoding.ASCII.GetBytes(sums);
            LogViceGet.Web = w;
            return w;
        }

        static LogViceRelease Release(bool withMsi)
        {
            LogViceRelease r = new LogViceRelease();
            LogViceGet.Parse(Reply(withMsi), r);
            return r;
        }

        static string Work(string root)
        {
            return Path.Combine(root, "work-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        }

        static void KeepCopy(string root)
        {
            Web(root, true, true);
            int msiRuns = 0;
            LogViceGet.RunMsi = delegate (string f) { msiRuns++; return 0; };
            MemoryUpdatePrefs prefs = new MemoryUpdatePrefs();
            List<long> seen = new List<long>();
            LogViceResult res = LogViceGet.Get(Release(true), Ctx(false), Work(root), prefs, delegate (long done, long all) { seen.Add(done); }, null);
            string expect = Path.Combine(LogViceGet.KeepFolder, "LogVice.exe");
            T.Eq(LogViceWay.Keep, res.Way, "a user gets the folder copy");
            T.Eq(expect, res.Exe, "LogVice.exe is kept in TrackVice's folder");
            T.Check(File.Exists(expect), "the checked exe is there");
            T.Eq(expect, prefs.Get(LogViceGet.PrefName), "where LogVice is, is remembered");
            T.Eq(0, msiRuns, "nothing is installed for a user");
            T.Check(seen.Count > 0, "the download shows progress");

            Web(root, true, false);
            LogViceResult noMsi = LogViceGet.Get(Release(false), Ctx(true), Work(root), new MemoryUpdatePrefs(), null, null);
            T.Eq(LogViceWay.Keep, noMsi.Way, "an administrator gets the exe when the release has no MSI");
            T.Eq(0, msiRuns, "and nothing is installed");
        }

        static void HashMismatch(string root)
        {
            Web(root, false, true);
            LogViceGet.KeepFolder = Path.Combine(root, "keep-bad");
            MemoryUpdatePrefs prefs = new MemoryUpdatePrefs();
            string work = Work(root);
            string err = null;
            try { LogViceGet.Get(Release(true), Ctx(false), work, prefs, null, null); }
            catch (LogViceException ex) { err = ex.Message; }
            T.Check(err != null && err.Contains("does not match its checksum"), "a hash that does not match stops with a clear message");
            T.Check(!File.Exists(Path.Combine(work, "LogVice.exe")), "the bad download is removed");
            T.Check(!File.Exists(Path.Combine(LogViceGet.KeepFolder, "LogVice.exe")), "nothing is kept");
            T.Check(prefs.Get(LogViceGet.PrefName) == null, "nothing is remembered");

            int msiRuns = 0;
            LogViceGet.RunMsi = delegate (string f) { msiRuns++; return 0; };
            FakeWeb w = Web(root, true, true);
            w.Files[Dl + "LogVice.msi"] = Encoding.ASCII.GetBytes("changed MSI");
            err = null;
            try { LogViceGet.Get(Release(true), Ctx(true), Work(root), prefs, null, null); }
            catch (LogViceException ex) { err = ex.Message; }
            T.Check(err != null && msiRuns == 0, "an MSI that does not match is not installed");
            LogViceGet.KeepFolder = Path.Combine(root, "keep");
        }

        static void Install(string root)
        {
            Web(root, true, true);
            string ran = null;
            LogViceGet.RunMsi = delegate (string f) { ran = f; return 0; };
            LogViceGet.InstalledExe = delegate { return @"C:\Program Files\LogVice\LogVice.exe"; };
            MemoryUpdatePrefs prefs = new MemoryUpdatePrefs();
            prefs.Set(LogViceGet.PrefName, @"C:\Old\LogVice.exe");
            LogViceResult res = LogViceGet.Get(Release(true), Ctx(true), Work(root), prefs, null, null);
            T.Eq(LogViceWay.Install, res.Way, "an administrator installs");
            T.Check(ran != null && ran.EndsWith("LogVice.msi"), "the checked MSI is installed");
            T.Eq(@"C:\Program Files\LogVice\LogVice.exe", res.Exe, "LogVice is opened from where the MSI puts it");
            T.Check(prefs.Get(LogViceGet.PrefName) == null, "the MSI is found by itself, so no path is kept");

            Web(root, true, true);
            LogViceGet.RunMsi = delegate (string f) { return 1223; };
            string err = null;
            try { LogViceGet.Get(Release(true), Ctx(true), Work(root), prefs, null, null); }
            catch (LogViceException ex) { err = ex.Message; }
            T.Check(err != null && err.Contains("administrator rights"), "a refused elevation is said plainly");
        }

        // The MSI cannot be swapped between its check and the end of msiexec.
        static void MsiHeld(string root)
        {
            Web(root, true, true);
            bool swapped = false;
            string seen = null;
            LogViceGet.RunMsi = delegate (string f)
            {
                try
                {
                    using (FileStream w = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        byte[] b = Encoding.ASCII.GetBytes("swapped");
                        w.Write(b, 0, b.Length);
                    }
                    swapped = true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                seen = File.ReadAllText(f);
                return 0;
            };
            LogViceGet.Get(Release(true), Ctx(true), Work(root), new MemoryUpdatePrefs(), null, null);
            T.Check(!swapped && seen == "MSI LogVice", "the checked MSI is held while it is installed");
        }

        static void Deploying(string root)
        {
            Web(root, true, true);
            LogViceGet.KeepFolder = Path.Combine(root, "keep-deploying");
            int msiRuns = 0;
            LogViceGet.RunMsi = delegate (string f) { msiRuns++; return 0; };
            MemoryUpdatePrefs prefs = new MemoryUpdatePrefs();
            LogViceContext c = Ctx(true);
            c.Oobe = true;
            string work = Work(root);
            LogViceResult res = LogViceGet.Get(Release(true), c, work, prefs, null, null);
            T.Eq(LogViceWay.Temp, res.Way, "a deploying device gets a temporary copy");
            T.Eq(0, msiRuns, "nothing is installed on a deploying device");
            T.Check(res.Exe.StartsWith(work) && File.Exists(res.Exe), "LogVice.exe stays in the temporary folder");
            T.Eq(res.Exe, LogViceGet.SessionExe, "it is found again in this session");
            T.Check(prefs.Get(LogViceGet.PrefName) == null, "it is not remembered after the session");
            T.Check(!Directory.Exists(LogViceGet.KeepFolder), "nothing is kept in TrackVice's folder");
            LogViceGet.CleanUpSession();
            T.Check(!Directory.Exists(work), "the temporary folder is removed when TrackVice closes");
            LogViceGet.SessionExe = null;
            LogViceGet.KeepFolder = Path.Combine(root, "keep");
        }

        static void Cancelled(string root)
        {
            FakeWeb w = Web(root, true, true);
            w.CancelDuring = true;
            bool stopped = false;
            try { LogViceGet.Get(Release(true), Ctx(false), Work(root), new MemoryUpdatePrefs(), null, delegate { return true; }); }
            catch (OperationCanceledException) { stopped = true; }
            T.Check(stopped, "Cancel stops the download");
        }

        static void Blocked(string root)
        {
            FakeWeb w = Web(root, true, true);
            LogViceContext pe = Ctx(true);
            pe.WinPE = true;
            string err = null;
            try { LogViceGet.Get(Release(true), pe, Work(root), new MemoryUpdatePrefs(), null, null); }
            catch (LogViceException ex) { err = ex.Message; }
            T.Check(err != null && err.Contains("Windows PE"), "Windows PE says why");
            T.Eq(0, w.Saved.Count, "nothing is downloaded in Windows PE");
        }
    }
}
