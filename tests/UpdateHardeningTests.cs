namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    // What a person who can write to an update location, or who holds the mutex of the agent, can and cannot do.
    public static class UpdateHardeningTests
    {
        sealed class Mem : ISessionStore
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly List<string> Read = new List<string>();
            public string Title { get { return "mem"; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { Files[name] = data; }
            public List<StoreEntry> List(string prefix) { return new List<StoreEntry>(); }
            public byte[] Get(string name)
            {
                Read.Add(name);
                byte[] b;
                if (!Files.TryGetValue(name, out b)) throw new StoreMissingException(name + " is not there.");
                return b;
            }
        }

        // A stream that must not be read.
        sealed class NoRead : MemoryStream
        {
            public override int Read(byte[] buffer, int offset, int count) { throw new InvalidOperationException("read"); }
        }

        sealed class Ops : IUpdateOps
        {
            public readonly List<string> Did = new List<string>();
            public bool Exists(string path) { return true; }
            public string Hash(string path) { return "AB"; }
            // Another program holds the mutex of the agent all the time.
            public bool WaitForAgentExit(int ms) { Did.Add("wait"); return false; }
            public string StopAgents(string target) { Did.Add("stop"); return null; }
            public void Replace(string source, string target) { Did.Add("replace"); }
            public void Restore(string target) { Did.Add("restore"); }
            public void StartTask() { Did.Add("start"); }
            public void Delete(string path) { Did.Add("delete"); }
            public void Sleep(int ms) { }
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-harden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void Run()
        {
            using (RSACryptoServiceProvider key = UpdateKeys.Create())
            {
                string pub = UpdateKeys.PublicKey(key);
                Versions();
                ManifestSize(key, pub);
                ExeSize(key, pub);
                Capped();
                Viewer(key, pub);
            }
            Mutex();
            WriteLinks();
            WebPages();
        }

        static UpdateManifest Signed(RSACryptoServiceProvider key, string version, byte[] exe)
        {
            UpdateManifest m = new UpdateManifest();
            m.Version = version;
            m.Sha256 = AgentUpdate.Sha256Hex(exe);
            m.Size = exe.LongLength;
            m.Blob = UpdateManifest.BlobFor(version);
            m.PublishedUtc = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
            m.Sign(key);
            return m;
        }

        static void Versions()
        {
            T.Check(!SemVer.IsValid("1.2.3\n"), "harden: a version that ends with a line break is refused");
            T.Check(!SemVer.IsValid("\u0661.\u0662.\u0663"), "harden: digits of other scripts are not a version");
            T.Check(SemVer.IsValid("2.1.0") && SemVer.IsValid("2.2.0-beta1"), "harden: normal versions still pass");
            UpdateManifest m = new UpdateManifest();
            m.Version = "1.2.3\n";
            m.Sha256 = new string('A', 64);
            m.Size = 3;
            m.Blob = UpdateManifest.BlobFor(m.Version);
            T.Check(m.Problem() != null, "harden: a signed version with a line break names no file the agent writes");
            m.Version = "1.2.3";
            m.Blob = UpdateManifest.BlobFor(m.Version);
            m.Sha256 = new string('A', 64) + "\n";
            T.Check(m.Problem() != null, "harden: a SHA-256 with a line break is refused");
        }

        // A huge agent/latest.json is not a manifest: the agent does not parse it, even when a signed one is inside.
        static void ManifestSize(RSACryptoServiceProvider key, string pub)
        {
            byte[] exe = Encoding.ASCII.GetBytes("new agent");
            Mem s = new Mem();
            string json = Encoding.UTF8.GetString(Signed(key, "9.0.0", exe).ToJson());
            s.Files[UpdateManifest.Name] = Encoding.UTF8.GetBytes(json.Substring(0, 1) + new string(' ', UpdateManifest.MaxJson) + json.Substring(1));
            s.Files[UpdateManifest.BlobFor("9.0.0")] = exe;
            string dir = TempDir();
            try
            {
                UpdateCheck r = AgentUpdate.Check(s, pub, "2.1.0", dir);
                T.Eq(UpdateDecision.Invalid, r.Decision, "harden: a manifest larger than the limit is refused");
                T.Check(r.Path == null && !s.Read.Contains(UpdateManifest.BlobFor("9.0.0")), "harden: nothing is downloaded for it");
                T.Eq(UpdateKind.Refused, r.Kind, "harden: it is not retried quickly");

                bool threw = false;
                try { UpdateManifest.Parse(new byte[UpdateManifest.MaxJson + 1]); }
                catch (StoreException) { threw = true; }
                T.Check(threw, "harden: Parse refuses more than the limit");
                threw = false;
                try { UpdateManifest.Parse(null); }
                catch (StoreException) { threw = true; }
                T.Check(threw, "harden: Parse refuses nothing");
            }
            finally { Directory.Delete(dir, true); }
        }

        // The exe in a folder location is never read past its signed size.
        static void ExeSize(RSACryptoServiceProvider key, string pub)
        {
            string loc = TempDir(), stage = TempDir();
            try
            {
                byte[] exe = Encoding.ASCII.GetBytes("the real new agent");
                Directory.CreateDirectory(Path.Combine(loc, "agent"));
                File.WriteAllBytes(Path.Combine(loc, "agent" + Path.DirectorySeparatorChar + "latest.json"), Signed(key, "9.0.0", exe).ToJson());
                byte[] big = new byte[exe.Length + 200000];
                File.WriteAllBytes(Path.Combine(loc, "agent" + Path.DirectorySeparatorChar + "TrackVice-9.0.0.exe"), big);
                UpdateSource src = UpdateSource.Open(loc);
                UpdateCheck r = AgentUpdate.Check(src, pub, "2.1.0", stage);
                T.Check(r.Decision == UpdateDecision.Invalid && r.Path == null, "harden: an exe larger than the signed size is not installed");
                T.Check(r.Message.Contains("more than the signed size"), "harden: the reason is its size: " + r.Message);
                T.Eq(0, Directory.GetFiles(stage).Length, "harden: nothing is staged");

                bool threw = false;
                try { UpdateSource.Read(src, UpdateManifest.BlobFor("9.0.0"), exe.Length); }
                catch (StoreTooLargeException) { threw = true; }
                T.Check(threw, "harden: a folder read stops at the limit");

                File.WriteAllBytes(Path.Combine(loc, "agent" + Path.DirectorySeparatorChar + "TrackVice-9.0.0.exe"), exe);
                r = AgentUpdate.Check(src, pub, "2.1.0", stage);
                T.Check(r.Path != null && File.Exists(r.Path), "harden: the signed exe is still installed: " + r.Message);

                threw = false;
                try { UpdateSource.Open(loc).Get("agent/none.exe", 10); }
                catch (StoreMissingException) { threw = true; }
                T.Check(threw, "harden: a missing file is missing, as before");
                threw = false;
                try { UpdateSource.Open("http://contoso.blob.core.windows.net/trackvice-agent?sp=r&sig=x"); }
                catch (StoreException) { threw = true; }
                T.Check(threw, "harden: an update link without https is refused");
            }
            finally
            {
                Directory.Delete(loc, true);
                Directory.Delete(stage, true);
            }
        }

        static void Capped()
        {
            bool threw = false;
            try { UpdateSource.ReadCapped(new NoRead(), 1000, 10, "x", TimeSpan.FromMinutes(1)); }
            catch (StoreTooLargeException) { threw = true; }
            T.Check(threw, "harden: a declared length over the limit is refused before reading");

            threw = false;
            try { UpdateSource.ReadCapped(new MemoryStream(new byte[300000]), -1, 200000, "x", TimeSpan.FromMinutes(1)); }
            catch (StoreTooLargeException) { threw = true; }
            T.Check(threw, "harden: an undeclared length is stopped at the limit");

            T.Eq(5, UpdateSource.ReadCapped(new MemoryStream(new byte[5]), 5, 5, "x", TimeSpan.FromMinutes(1)).Length, "harden: a file of exactly the limit is read");

            threw = false;
            try { UpdateSource.ReadCapped(new MemoryStream(new byte[10]), -1, 100, "x", TimeSpan.FromTicks(-1)); }
            catch (StoreException) { threw = true; }
            T.Check(threw, "harden: a read that takes too long is given up");
        }

        static UpdateLocation Folder(string dir)
        {
            UpdateLocation l = new UpdateLocation();
            l.Key = "folder:" + dir.ToLowerInvariant();
            l.Folder = dir;
            l.ReadLink = l.WriteLink = dir;
            return l;
        }

        // The viewer reads agent/latest.json of every place, which anybody who can write there controls.
        static void Viewer(RSACryptoServiceProvider key, string pub)
        {
            string dir = TempDir();
            try
            {
                Func<string, ISessionStore> open = UpdateLocations.OpenWith(null);
                UpdateLocation l = Folder(dir);
                UpdateLocations.Refresh(l, open, pub);
                T.Eq(UpdateState.Nothing, l.State, "harden: an empty folder has nothing published");

                Directory.CreateDirectory(Path.Combine(dir, "agent"));
                string file = Path.Combine(dir, "agent" + Path.DirectorySeparatorChar + "latest.json");
                File.WriteAllBytes(file, new byte[2 * 1024 * 1024]);
                UpdateLocations.Refresh(l, open, pub);
                T.Eq(UpdateState.Failed, l.State, "harden: a huge latest.json in a folder is refused");
                T.Check(l.Problem.Contains("more than"), "harden: it says why: " + l.Problem);

                UpdateManifest m = Signed(key, "2.0.3", new byte[] { 1 });
                m.Version = "<b>9.9.9</b>\r\nAll devices are fine";
                File.WriteAllBytes(file, m.ToJson());
                UpdateLocations.Refresh(l, open, pub);
                T.Eq(UpdateState.Published, l.State, "harden: an unsigned latest.json is still read");
                T.Eq(UpdateLocations.UnreadableVersion, l.Version, "harden: only a version number is shown from it");
                T.Check(l.OtherKey && !l.Wanted("2.1.0"), "harden: it is not trusted");

                File.WriteAllBytes(file, Signed(key, "2.0.3", new byte[] { 1 }).ToJson());
                UpdateLocations.Refresh(l, open, pub);
                T.Check(l.State == UpdateState.Published && l.Version == "2.0.3" && !l.OtherKey, "harden: a signed latest.json in a folder is read as before");

                UpdateLocation gone = Folder(Path.Combine(dir, "not-there"));
                UpdateLocations.Refresh(gone, open, pub);
                T.Eq(UpdateState.Failed, gone.State, "harden: a folder that is not there is a failure, not nothing published");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void Mutex()
        {
            List<string> log = new List<string>();
            Ops o = new Ops();
            int rc = UpdateApply.Run(o, "new.exe", "agent.exe", "AB", delegate (string s) { log.Add(s); });
            T.Eq(0, rc, "harden: a mutex held by another program does not keep the update out");
            T.Eq("wait,stop,wait,replace,delete,start", string.Join(",", o.Did.ToArray()), "harden: once no agent runs, it is replaced and the task is started");
        }

        static void WriteLinks()
        {
            DateTime now = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
            UpdateLocation l = UpdateLocations.FromLink("https://contosoagents.blob.core.windows.net/trackvice-agent?sp=r&sig=x");
            T.Check(l != null, "harden: a location from a link");
            T.Check(UpdateLocations.WriteLinkProblem(l, "https://contosoagents.blob.core.windows.net/trackvice-agent?sv=2022-11-02&sr=c&sp=rcw&sig=x", now) == null, "harden: a link for the same container is taken");
            string p = UpdateLocations.WriteLinkProblem(l, "https://contosoagents.blob.core.windows.net.example.net/trackvice-agent?sv=2022-11-02&sr=c&sp=rcw&sig=x", now);
            T.Check(p != null && p.Contains("example.net"), "harden: a link to another host with the same first part is refused: " + p);
            T.Check(UpdateLocations.WriteLinkProblem(l, "https://contosoagents.example.net/trackvice-agent?sv=2022-11-02&sr=c&sp=rcw&sig=x", now) != null, "harden: a link to another domain is refused");
        }

        static void WebPages()
        {
            T.Check(PublishAccess.IsWebPage("https://microsoft.com/devicelogin"), "harden: the sign-in page is opened");
            T.Check(PublishAccess.IsWebPage(" https://login.microsoft.com/device "), "harden: spaces around it do not matter");
            T.Check(!PublishAccess.IsWebPage("C:\\Windows\\System32\\cmd.exe"), "harden: a program is not opened");
            T.Check(!PublishAccess.IsWebPage("file:///C:/Users/Public/x.hta"), "harden: a file is not opened");
            T.Check(!PublishAccess.IsWebPage("\\\\server\\share\\x.exe"), "harden: a share is not opened");
            T.Check(!PublishAccess.IsWebPage("http://microsoft.com/devicelogin"), "harden: http is not opened");
            T.Check(!PublishAccess.IsWebPage("ms-settings:") && !PublishAccess.IsWebPage(null) && !PublishAccess.IsWebPage(""), "harden: other schemes and nothing are not opened");
        }
    }
}
