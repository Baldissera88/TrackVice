namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;

    public static class AgentUpdateTests
    {
        static RSACryptoServiceProvider key, other;
        static string pub;
        static readonly MemoryKeyStore otherStore = new MemoryKeyStore();
        static bool otherCreated;

        public static void Run()
        {
            key = UpdateKeys.Create();
            // Making a 3072-bit key is slow under Mono, so the second key is also the one the key store test uses.
            other = UpdateKeys.LoadOrCreate(otherStore, out otherCreated);
            pub = UpdateKeys.PublicKey(key);
            try
            {
                Manifest();
                Keys();
                Versions();
                Decisions();
                Download();
                Publish();
                Apply();
                Processes();
                SelfUpdate();
            }
            finally
            {
                key.Dispose();
                other.Dispose();
            }
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static UpdateManifest Signed(string version)
        {
            UpdateManifest m = new UpdateManifest();
            m.Version = version;
            m.Sha256 = AgentUpdate.Sha256Hex(new byte[] { 1, 2, 3 });
            m.Size = 3;
            m.Blob = UpdateManifest.BlobFor(version);
            m.PublishedUtc = new DateTime(2026, 11, 15, 9, 30, 0, DateTimeKind.Utc);
            m.Sign(key);
            return m;
        }

        static void Manifest()
        {
            UpdateManifest m = Signed("1.0.6");
            T.Check(m.Verify(pub), "a signed manifest verifies");
            T.Check(m.Problem() == null, "a signed manifest is valid");
            UpdateManifest back = UpdateManifest.Parse(m.ToJson());
            T.Check(back.Verify(pub), "the manifest still verifies after JSON");
            T.Eq("1.0.6", back.Version, "manifest version from JSON");
            T.Eq(m.PublishedUtc, back.PublishedUtc, "manifest time from JSON");

            UpdateManifest v = UpdateManifest.Parse(m.ToJson());
            v.Version = "9.0.0";
            v.Blob = UpdateManifest.BlobFor("9.0.0");
            T.Check(!v.Verify(pub), "a changed version breaks the signature");

            UpdateManifest h = UpdateManifest.Parse(m.ToJson());
            h.Sha256 = AgentUpdate.Sha256Hex(new byte[] { 6, 6, 6 });
            T.Check(!h.Verify(pub), "a changed SHA-256 breaks the signature");

            UpdateManifest s = UpdateManifest.Parse(m.ToJson());
            s.Size = 4;
            T.Check(!s.Verify(pub), "a changed size breaks the signature");

            T.Check(!m.Verify(UpdateKeys.PublicKey(other)), "another key does not verify it");

            UpdateManifest none = UpdateManifest.Parse(m.ToJson());
            none.Signature = null;
            T.Check(!none.Verify(pub), "an unsigned manifest does not verify");

            UpdateManifest odd = Signed("1.0.6");
            odd.Blob = "sessions/abc.json.gz";
            odd.Sign(key);
            T.Check(odd.Verify(pub) && odd.Problem() != null, "a signed manifest naming another blob is still refused");
            UpdateManifest path = Signed("1.0.6");
            path.Version = "../../x";
            T.Check(path.Problem() != null, "a version that is a path is refused");
        }

        static void Keys()
        {
            T.Check(UpdateKeys.Problem(pub) == null, "the public key can be used");
            T.Check(UpdateKeys.Problem("not a key!") != null, "text that is not base64 is refused");
            T.Check(UpdateKeys.Problem(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })) != null, "base64 that is not a key is refused");
            using (RSACryptoServiceProvider small = new RSACryptoServiceProvider(1024))
                T.Check(UpdateKeys.Problem(UpdateKeys.PublicKey(small)) != null, "a key under 3072 bits is refused");
            T.Eq(3072, key.KeySize, "the signing key has 3072 bits");

            MemoryKeyStore ks = otherStore;
            bool created;
            string first = UpdateKeys.PublicKey(other);
            T.Check(otherCreated && ks.Blob != null, "the key is made on first use");
            T.Eq(3072, other.KeySize, "a made key has 3072 bits");
            using (RSACryptoServiceProvider k = UpdateKeys.LoadOrCreate(ks, out created))
            {
                T.Check(!created, "the key is made only once");
                T.Eq(first, UpdateKeys.PublicKey(k), "the saved key is used again");
            }
            T.Eq(UpdateKeys.Fingerprint(first), UpdateKeys.Fingerprint(first), "the fingerprint is stable");
        }

        static void Versions()
        {
            T.Eq(1, SemVer.Compare("1.0.6", "1.0.5"), "1.0.6 is newer than 1.0.5");
            T.Eq(-1, SemVer.Compare("1.0.5", "1.0.10"), "versions compare as numbers");
            T.Eq(0, SemVer.Compare("1.0.5", "1.0.5"), "the same version");
            T.Eq(0, SemVer.Compare("1.0.5", "1.0.5.0"), "a fourth part of 0 is the same");
            T.Eq(1, SemVer.Compare("2.0", "1.9.9"), "a major version is newer");
            T.Eq(-1, SemVer.Compare("1.1.0-beta1", "1.1.0"), "a beta comes before the release");
            T.Eq(1, SemVer.Compare("1.1.0-beta1", "1.0.9"), "a beta of a later version is newer");
            T.Eq(0, SemVer.Compare("1.0.5+abc", "1.0.5"), "build data is ignored");
            T.Check(SemVer.IsValid("1.0.5") && SemVer.IsValid("1.1.0-beta2"), "valid versions");
            T.Check(!SemVer.IsValid("1.0.5/x") && !SemVer.IsValid("") && !SemVer.IsValid(null), "invalid versions");
        }

        static void Decisions()
        {
            T.Eq(UpdateDecision.Newer, AgentUpdate.Decide("1.0.5", pub, "https://contoso.blob.core.windows.net/trackvice-agent?sp=r", "1.0.6"), "decide: newer");
            T.Eq(UpdateDecision.Same, AgentUpdate.Decide("1.0.5", pub, "D:\\updates", "1.0.5"), "decide: same");
            T.Eq(UpdateDecision.Older, AgentUpdate.Decide("1.0.5", pub, "D:\\updates", "1.0.4"), "decide: older is never installed");
            T.Eq(UpdateDecision.NoKey, AgentUpdate.Decide("1.0.5", "", "D:\\updates", "1.0.6"), "decide: no key");
            T.Eq(UpdateDecision.NoKey, AgentUpdate.Decide("1.0.5", null, null, "1.0.6"), "decide: no key and no link");
            T.Eq(UpdateDecision.NoLink, AgentUpdate.Decide("1.0.5", pub, " ", "1.0.6"), "decide: no link");
            T.Eq(UpdateDecision.Invalid, AgentUpdate.Decide("1.0.5", pub, "D:\\updates", "next"), "decide: not a version");
        }

        static byte[] FakeExe(int n)
        {
            byte[] b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(i * 7 + 3);
            return b;
        }

        static void Download()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(Path.Combine(dir, "store"));
                byte[] exe = FakeExe(5000);
                UpdatePublisher.Publish(st, exe, "1.0.6", key, DateTime.UtcNow);
                string stage = Path.Combine(dir, "stage");

                UpdateCheck r = AgentUpdate.Check(st, pub, "1.0.5", stage);
                T.Eq(UpdateDecision.Newer, r.Decision, "download: a newer version");
                T.Check(r.Path != null && File.Exists(r.Path), "download: the exe is saved");
                T.Check(r.Path != null && AgentUpdate.Sha256OfFile(r.Path) == AgentUpdate.Sha256Hex(exe), "download: the saved exe is the published one");
                T.Check(r.Path != null && Path.GetFileName(r.Path) == "TrackVice-1.0.6.exe", "download: the file is named after the version");

                r = AgentUpdate.Check(st, pub, "1.0.6", Path.Combine(dir, "stage2"));
                T.Check(r.Decision == UpdateDecision.Same && r.Path == null, "download: nothing when the version is the same");
                r = AgentUpdate.Check(st, pub, "1.0.7", Path.Combine(dir, "stage3"));
                T.Check(r.Decision == UpdateDecision.Older && r.Path == null, "download: nothing when the published version is older");
                T.Check(!Directory.Exists(Path.Combine(dir, "stage2")) && !Directory.Exists(Path.Combine(dir, "stage3")), "download: nothing is written when nothing is installed");

                r = AgentUpdate.Check(st, UpdateKeys.PublicKey(other), "1.0.5", Path.Combine(dir, "stage4"));
                T.Check(r.Decision == UpdateDecision.Invalid && r.Path == null, "download: another key is refused");

                // A device with the upload link replaces the exe in the container.
                byte[] evil = FakeExe(5000);
                evil[100] ^= 0xFF;
                st.Put(UpdateManifest.BlobFor("1.0.6"), evil, null);
                r = AgentUpdate.Check(st, pub, "1.0.5", Path.Combine(dir, "stage5"));
                T.Check(r.Decision == UpdateDecision.Invalid && r.Path == null && r.Message.Contains("SHA-256"), "download: a replaced exe is refused by its hash");
                st.Put(UpdateManifest.BlobFor("1.0.6"), FakeExe(4000), null);
                r = AgentUpdate.Check(st, pub, "1.0.5", Path.Combine(dir, "stage6"));
                T.Check(r.Decision == UpdateDecision.Invalid && r.Message.Contains("bytes"), "download: a replaced exe is refused by its size");

                // And replaces the manifest with one of its own.
                UpdateManifest fake = UpdateManifest.Parse(st.Get(UpdateManifest.Name));
                fake.Version = "1.0.9";
                fake.Blob = UpdateManifest.BlobFor("1.0.9");
                st.Put(UpdateManifest.Name, fake.ToJson(), null);
                r = AgentUpdate.Check(st, pub, "1.0.5", Path.Combine(dir, "stage7"));
                T.Check(r.Decision == UpdateDecision.Invalid && r.Path == null, "download: a changed manifest is refused");

                r = AgentUpdate.Check(new FolderStore(Path.Combine(dir, "empty")), pub, "1.0.5", Path.Combine(dir, "stage8"));
                T.Check(r.Decision == UpdateDecision.Invalid && r.Message.StartsWith("No update information"), "download: no manifest");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void Publish()
        {
            string dir = TempDir();
            try
            {
                FolderStore st = new FolderStore(dir);
                byte[] exe = FakeExe(1234);
                UpdateManifest m = UpdatePublisher.Publish(st, exe, "1.0.6", key, new DateTime(2026, 11, 15, 10, 0, 0, 500, DateTimeKind.Utc));
                T.Check(File.Exists(Path.Combine(Path.Combine(dir, "agent"), "TrackVice-1.0.6.exe")), "publish writes the exe");
                T.Check(File.Exists(Path.Combine(Path.Combine(dir, "agent"), "latest.json")), "publish writes the manifest");
                T.Eq(1234, st.Get("agent/TrackVice-1.0.6.exe").Length, "the published exe is whole");
                UpdateManifest back = UpdateManifest.Parse(st.Get(UpdateManifest.Name));
                T.Check(back.Verify(pub), "the published manifest is signed");
                T.Eq(AgentUpdate.Sha256Hex(exe), back.Sha256, "the published manifest has the hash");
                T.Eq(1234L, back.Size, "the published manifest has the size");
                T.Eq(m.PublishedUtc, back.PublishedUtc, "the published time is whole seconds");
                bool threw = false;
                try { UpdatePublisher.Publish(st, exe, "latest", key, DateTime.UtcNow); } catch (ArgumentException) { threw = true; }
                T.Check(threw, "publish refuses a version that is not a number");
            }
            finally { Directory.Delete(dir, true); }
        }

        sealed class FakeOps : IUpdateOps
        {
            public readonly List<string> Did = new List<string>();
            public bool NewExists = true;
            public string NewHash = "AB", TargetHash = "AB";
            public int ExitAfterWaits = 1;
            public string StillRunning;
            public int ReplaceFails;
            int waits;

            public bool Exists(string path) { return NewExists; }
            public string Hash(string path) { return path == "new.exe" ? NewHash : TargetHash; }
            public bool WaitForAgentExit(int ms) { waits++; Did.Add("wait"); return waits >= ExitAfterWaits; }
            public string StopAgents(string target) { Did.Add("stop"); return StillRunning; }
            public void Replace(string source, string target)
            {
                if (ReplaceFails-- > 0) { Did.Add("replace failed"); throw new IOException("in use"); }
                Did.Add("replace");
            }
            public void Restore(string target) { Did.Add("restore"); }
            public void StartTask() { Did.Add("start"); }
            public void Delete(string path) { Did.Add("delete " + path); }
            public void Sleep(int ms) { }
        }

        static string Steps(FakeOps o)
        {
            return string.Join(",", o.Did.ToArray());
        }

        static void Apply()
        {
            List<string> log = new List<string>();
            Action<string> l = delegate (string s) { log.Add(s); };

            FakeOps ok = new FakeOps();
            T.Eq(0, UpdateApply.Run(ok, "new.exe", "agent.exe", "ab", l), "apply: done");
            T.Eq("wait,replace,delete new.exe,start", Steps(ok), "apply: waits, replaces, cleans up and starts the task");

            FakeOps slow = new FakeOps();
            slow.ExitAfterWaits = 2;
            T.Eq(0, UpdateApply.Run(slow, "new.exe", "agent.exe", "AB", l), "apply: an agent that does not exit is stopped");
            T.Eq("wait,stop,wait,replace,delete new.exe,start", Steps(slow), "apply: the steps after stopping it");

            FakeOps stuck = new FakeOps();
            stuck.ExitAfterWaits = 99;
            stuck.StillRunning = "TrackVice.exe (process 4711)";
            T.Eq(1, UpdateApply.Run(stuck, "new.exe", "agent.exe", "AB", l), "apply: an agent that cannot be stopped");
            T.Check(!stuck.Did.Contains("replace") && !stuck.Did.Contains("start"), "apply: nothing is replaced and the running agent is left alone");
            T.Check(log[log.Count - 1].Contains("process 4711"), "apply: the log names the process");

            FakeOps locked = new FakeOps();
            locked.ReplaceFails = 99;
            T.Eq(1, UpdateApply.Run(locked, "new.exe", "agent.exe", "AB", l), "apply: a file that stays locked");
            T.Eq(UpdateApply.Tries + 1, locked.Did.Count - 2, "apply: tries a few times");
            T.Check(locked.Did[locked.Did.Count - 1] == "start", "apply: the old agent is started again");

            FakeOps once = new FakeOps();
            once.ReplaceFails = 2;
            T.Eq(0, UpdateApply.Run(once, "new.exe", "agent.exe", "AB", l), "apply: a short lock is waited out");

            FakeOps bad = new FakeOps();
            bad.NewHash = "CD";
            T.Eq(1, UpdateApply.Run(bad, "new.exe", "agent.exe", "AB", l), "apply: a new exe with another hash");
            T.Eq("delete new.exe,start", Steps(bad), "apply: it is not used and the agent goes on");

            FakeOps wrong = new FakeOps();
            wrong.TargetHash = "EF";
            T.Eq(1, UpdateApply.Run(wrong, "new.exe", "agent.exe", "AB", l), "apply: a replaced file with another hash");
            T.Check(wrong.Did.Contains("restore") && wrong.Did[wrong.Did.Count - 1] == "start", "apply: the earlier exe is put back and started");

            FakeOps gone = new FakeOps();
            gone.NewExists = false;
            T.Eq(1, UpdateApply.Run(gone, "new.exe", "agent.exe", "AB", l), "apply: no new exe");
            T.Eq("start", Steps(gone), "apply: no new exe starts the agent again");

            FakeOps nohash = new FakeOps();
            T.Eq(1, UpdateApply.Run(nohash, "new.exe", "agent.exe", null, l), "apply: no hash given");
            T.Check(!nohash.Did.Contains("replace") && nohash.Did[nohash.Did.Count - 1] == "start", "apply: without a hash nothing is replaced and the agent goes on");
        }

        sealed class FakeProcs : IProcessSource
        {
            public readonly List<AgentProc> All = new List<AgentProc>();
            public readonly List<int> Killed = new List<int>();
            public bool Unkillable;

            public List<AgentProc> Find(string processName)
            {
                List<AgentProc> l = new List<AgentProc>();
                foreach (AgentProc p in All) if (!Killed.Contains(p.Id)) l.Add(p);
                return l;
            }

            public void Kill(int id)
            {
                if (Unkillable) throw new InvalidOperationException("Access is denied");
                Killed.Add(id);
            }
        }

        static AgentProc P(int id, string path, string cmd)
        {
            AgentProc p = new AgentProc();
            p.Id = id;
            p.Name = "TrackVice.exe";
            p.Path = path;
            p.CommandLine = cmd;
            return p;
        }

        static void Processes()
        {
            string target = @"C:\Program Files\TrackVice Agent\TrackVice.exe";
            FakeProcs f = new FakeProcs();
            f.All.Add(P(10, target, null));
            f.All.Add(P(11, null, "\"C:\\Program Files\\TrackVice Agent\\TrackVice.exe\" /agent"));
            f.All.Add(P(12, @"C:\Users\admin\Downloads\TrackVice.exe", null));
            f.All.Add(P(13, null, null));
            f.All.Add(P(14, null, "C:\\Program Files\\TrackVice Agent\\TrackVice.exe.old /agent"));
            f.All.Add(P(15, target.ToLowerInvariant(), null));
            List<AgentProc> m = AgentProcesses.Matching(f.All, target, 15);
            List<int> ids = new List<int>();
            foreach (AgentProc p in m) ids.Add(p.Id);
            T.Eq("10,11", string.Join(",", ids.ConvertAll(delegate (int i) { return i.ToString(); }).ToArray()), "processes: by path, or by command line when the path cannot be read, never itself");

            T.Eq(null, AgentProcesses.Stop(f, target, 15), "processes: both are stopped");
            T.Eq(2, f.Killed.Count, "processes: two were killed");

            FakeProcs g = new FakeProcs();
            g.Unkillable = true;
            g.All.Add(P(4711, null, "\"" + target + "\" /agent"));
            string still = AgentProcesses.Stop(g, target, 1);
            T.Check(still != null && still.Contains("4711"), "processes: one that cannot be stopped is named");
            string msg = AgentProcesses.ReplaceFailed(target, "The file is in use.", still);
            T.Check(msg.Contains("process 4711") && msg.Contains("Restart the device"), "the install message names the process and suggests a restart");
            T.Check(AgentProcesses.ReplaceFailed(target, "The file is in use.", null).Contains("Restart the device"), "the install message without a process also suggests a restart");
        }

        sealed class FakeLauncher : IUpdateLauncher
        {
            public string Copied, Started, Args;
            public void CopySelf(string to) { Copied = to; }
            public void StartDetached(string exe, string arguments) { Started = exe; Args = arguments; }
        }

        static void SelfUpdate()
        {
            List<string> log = new List<string>();
            Action<string> l = delegate (string s) { log.Add(s); };
            string dir = TempDir();
            try
            {
                string install = Path.Combine(dir, "install");
                Directory.CreateDirectory(install);
                string link = Path.Combine(dir, "store");
                FakeLauncher fl = new FakeLauncher();
                T.Check(AgentSelfUpdate.For(null, link, "1.0.5", install, fl, l) == null, "self-update is off without a key");
                T.Eq(0, log.Count, "without a key the agent behaves as before and logs nothing");
                T.Check(AgentSelfUpdate.For(pub, "", "1.0.5", install, fl, l) == null, "self-update is off without a link");
                T.Check(log.Count == 1 && log[0].Contains("no update link"), "a key without a link is logged");
                T.Check(AgentSelfUpdate.For("bad", link, "1.0.5", install, fl, l) == null, "self-update is off with a bad key");

                File.WriteAllText(Path.Combine(install, AgentUpdate.HelperName), "left over");
                AgentSelfUpdate u = AgentSelfUpdate.For(pub, link, "1.0.5", install, fl, l);
                T.Check(u != null, "self-update is on with a key and a link");
                T.Check(!File.Exists(Path.Combine(install, AgentUpdate.HelperName)), "a helper left by an earlier update is removed");

                DateTime now = new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
                Directory.CreateDirectory(link);
                T.Check(!u.Cycle(now), "no update published yet");
                T.Eq(now.AddHours(6), u.NextUtc, "the next check is 6 hours later");
                UpdatePublisher.Publish(new FolderStore(link), FakeExe(800), "1.0.6", key, now);
                T.Check(!u.Cycle(now.AddHours(1)), "no check before 6 hours");
                T.Check(u.Cycle(now.AddHours(6)), "the update is started at the next check");
                T.Eq(Path.Combine(install, AgentUpdate.HelperName), fl.Copied, "the agent copies itself to the helper name");
                T.Eq(fl.Copied, fl.Started, "the helper copy is started");
                T.Check(fl.Args.StartsWith("/agent-update-apply \"") && fl.Args.Contains("TrackVice-1.0.6.exe") && fl.Args.Contains("/sha256 " + AgentUpdate.Sha256Hex(FakeExe(800))), "the helper gets the new exe and its hash");
                T.Check(File.Exists(Path.Combine(install, AgentUpdate.TriedFile)), "the version being installed is noted");

                // The helper failed and started the old agent again: it does not download and stop again at once.
                log.Clear();
                fl.Started = null;
                AgentSelfUpdate again = AgentSelfUpdate.For(pub, link, "1.0.5", install, fl, l);
                T.Check(log.Count == 1 && log[0].Contains("did not finish"), "a failed update is logged at the next start");
                T.Check(!File.Exists(Path.Combine(install, AgentUpdate.TriedFile)), "the note is removed once read");
                DateTime later = now.AddHours(7);
                T.Check(!again.Cycle(later) && fl.Started == null, "after a failed update the first check waits");
                T.Eq(later.AddHours(6), again.NextUtc, "the next try is 6 hours later");
                T.Check(again.Cycle(later.AddHours(6)) && fl.Started != null, "then the update is tried again");

                // The update worked: the new version starts as usual.
                log.Clear();
                AgentSelfUpdate done = AgentSelfUpdate.For(pub, link, "1.0.6", install, fl, l);
                T.Check(log.Count == 1 && log[0].StartsWith("Self-update is on"), "after a good update the agent checks as usual");
                T.Check(!done.Cycle(later.AddHours(7)), "the new version does not update to itself");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
