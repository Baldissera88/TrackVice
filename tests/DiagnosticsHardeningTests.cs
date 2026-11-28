namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Security.Cryptography;
    using System.Text;

    // What a holder of the upload link or of the update place can write, and what the agent and the viewer then do with it.
    public static class DiagnosticsHardeningTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        const string Sid = "7a1e0c52000143c39a115d2b7e9f0a01";

        sealed class Store : ISessionStore
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            // The size the listing reports for every file, when set.
            public long ListedSize = -1;
            public int Gets;
            public string Title { get { return "memory"; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { Files[name] = data; }
            public List<StoreEntry> List(string prefix)
            {
                List<StoreEntry> l = new List<StoreEntry>();
                foreach (string n in Files.Keys)
                    if (n.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        StoreEntry e = new StoreEntry();
                        e.Name = n;
                        e.Size = ListedSize >= 0 ? ListedSize : Files[n].Length;
                        l.Add(e);
                    }
                return l;
            }
            public byte[] Get(string name)
            {
                Gets++;
                byte[] b;
                if (!Files.TryGetValue(name, out b)) throw new StoreMissingException("Not there.");
                return b;
            }
        }

        sealed class Runner : IDiagRunner
        {
            public int Runs;
            public bool Exists(DiagTool tool) { return false; }
            public int Run(DiagTool tool, string cabFolder, out string output)
            {
                Runs++;
                output = "";
                return 0;
            }
        }

        public static void Run()
        {
            StatusFromStorage();
            OutputCap();
            Unpacking();
            Downloads();
            using (RSACryptoServiceProvider key = UpdateKeys.Create())
            {
                StateFiles(key);
                ClockBack(key);
            }
        }

        static void StatusFromStorage()
        {
            T.Eq(0, DiagnosticsStatus.Decode(new string('x', DiagnosticsStatus.MaxText + 1)).Count, "diag hardening: a huge status is not read");
            StringBuilder b = new StringBuilder();
            for (int k = 0; k < 200; k++) b.Append(Guid.NewGuid().ToString("D")).Append("|Uploaded|2026-11-27T10:00:00Z|b|n").Append(k).Append('\n');
            List<DiagnosticsEntry> l = DiagnosticsStatus.Decode(b.ToString());
            T.Eq(DiagnosticsStatus.Keep, l.Count, "diag hardening: only the newest entries are read");
            T.Eq("n199", l[l.Count - 1].Note, "diag hardening: the newest entry is last");
            l = DiagnosticsStatus.Decode(new string('i', DiagnosticsStatus.MaxId + 1) + "|Uploaded|2026-11-27T10:00:00Z|b|n");
            T.Eq(0, l.Count, "diag hardening: an id longer than a request id is not read");
            l = DiagnosticsStatus.Decode("a|" + new string('s', 5000) + "|2026-11-27T10:00:00Z|" + new string('b', 5000) + "|" + new string('n', 20000));
            T.Check(l.Count == 1 && l[0].State.Length <= DiagnosticsStatus.MaxState && l[0].Blob.Length <= DiagnosticsStatus.MaxBlob && l[0].Note.Length <= DiagnosticsStatus.MaxNote, "diag hardening: long fields are cut");
            T.Eq("n", DiagnosticsStatus.Decode("a|Failed|2026-11-27T10:00:00Z|b|n\r\n")[0].Note, "diag hardening: a line end is not part of the note");
        }

        static void OutputCap()
        {
            StringBuilder sb = new StringBuilder();
            WinDiagRunner.AddLine(sb, new string('x', 1000), 100);
            T.Eq(100, sb.Length, "diag hardening: one long line of output is cut at the limit");
            WinDiagRunner.AddLine(sb, "more", 100);
            T.Eq(100, sb.Length, "diag hardening: nothing is added after the limit");
            sb = new StringBuilder();
            WinDiagRunner.AddLine(sb, "short", 100);
            T.Eq("short" + Environment.NewLine, sb.ToString(), "diag hardening: a short line is kept whole");
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-harden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static byte[] Zip(params string[] names)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (ZipArchive z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    foreach (string n in names)
                        using (Stream w = z.CreateEntry(n).Open()) w.WriteByte((byte)'x');
                return ms.ToArray();
            }
        }

        static void Unpacking()
        {
            T.Eq("_CON.log", LogBundle.SafeName("CON.log"), "diag hardening: a device name is not used as a file name");
            T.Eq("_nul", LogBundle.SafeName("dir/nul"), "diag hardening: nul is not used either");
            T.Eq("_COM1.txt", LogBundle.SafeName("COM1.txt"), "diag hardening: nor COM1");
            T.Eq("AppWorkload.log.exe.txt", LogBundle.SafeName("AppWorkload.log.exe"), "diag hardening: a program is written as text");
            T.Eq("desktop.ini.txt", LogBundle.SafeName("desktop.ini"), "diag hardening: desktop.ini is written as text");
            T.Eq("share.url.txt", LogBundle.SafeName("share.url"), "diag hardening: a shortcut is written as text");
            T.Eq("x.lnk.txt", LogBundle.SafeName("x.lnk "), "diag hardening: a shortcut with a space after it too");
            T.Eq(".scf.txt", LogBundle.SafeName(".scf"), "diag hardening: a name that is only an extension too");
            T.Eq("smsts.log", LogBundle.SafeName("smsts.log"), "diag hardening: a log keeps its name");
            T.Eq(DiagCommands.CabName, LogBundle.SafeName(DiagCommands.CabName), "diag hardening: the cab keeps its name");
            T.Eq("session.json", LogBundle.SafeName("session.json"), "diag hardening: the session keeps its name");
            T.Eq("invoice_gol.exe.txt", LogBundle.SafeName("invoice‮gol.exe"), "diag hardening: a right to left mark is replaced");
            string longName = LogBundle.SafeName(new string('a', 400) + ".log");
            T.Check(longName.Length <= LogBundle.MaxNameChars && longName.EndsWith(".log", StringComparison.Ordinal), "diag hardening: a long name is cut and keeps its extension");

            string dir = TempDir();
            try
            {
                string target = Path.Combine(dir, "t");
                Dictionary<string, string> written = new Dictionary<string, string>();
                int n = LogBundle.Unpack(Zip("payload.exe", "CON", "a.log"), target, written);
                T.Eq(3, n, "diag hardening: every entry is written");
                T.Check(File.Exists(Path.Combine(target, "payload.exe.txt")) && !File.Exists(Path.Combine(target, "payload.exe")), "diag hardening: no program in the folder");
                T.Eq(Path.Combine(target, "payload.exe.txt"), written["payload.exe"], "diag hardening: the written map names the renamed file");

                // More entries than a bundle holds: nothing is written.
                string[] many = new string[LogBundle.MaxEntries + 1];
                for (int k = 0; k < many.Length; k++) many[k] = "f" + k + ".log";
                string t2 = Path.Combine(dir, "many");
                bool threw = false;
                try { LogBundle.Unpack(Zip(many), t2, null); }
                catch (InvalidDataException) { threw = true; }
                T.Check(threw && Directory.GetFiles(t2).Length == 0, "diag hardening: a zip with too many files is not unpacked");

                // An entry that says it is larger than the limit: refused before anything is written.
                byte[] zip = Zip("first.log", "bomb.log");
                SetCentralSize(zip, 1, 0x7FFFFFFF);
                string t3 = Path.Combine(dir, "bomb");
                threw = false;
                try { LogBundle.Unpack(zip, t3, null); }
                catch (InvalidDataException) { threw = true; }
                T.Check(threw && Directory.GetFiles(t3).Length == 0, "diag hardening: a zip that says it unpacks to more than the limit is not unpacked");

                threw = false;
                try { LogBundle.Unpack(new byte[0], Path.Combine(dir, "empty"), null); }
                catch (InvalidDataException) { threw = true; }
                T.Check(threw, "diag hardening: an empty bundle is refused");
            }
            finally { Directory.Delete(dir, true); }
        }

        // Sets the uncompressed size of the index-th entry in the central directory.
        static void SetCentralSize(byte[] zip, int index, uint size)
        {
            int seen = 0;
            for (int i = 0; i + 46 <= zip.Length; i++)
            {
                if (zip[i] != 0x50 || zip[i + 1] != 0x4b || zip[i + 2] != 0x01 || zip[i + 3] != 0x02) continue;
                if (seen++ != index) continue;
                byte[] b = BitConverter.GetBytes(size);
                Array.Copy(b, 0, zip, i + 24, 4);
                return;
            }
            throw new InvalidOperationException("No such entry.");
        }

        static void Downloads()
        {
            string blob = AgentData.BlobName(Sid);
            string name = DeviceLogs.FolderName(new string('C', 500), "sessions/" + new string('s', 500) + ".json.gz");
            T.Check(name.Length <= DeviceLogs.MaxComputerChars + 1 + DeviceLogs.MaxIdChars, "diag hardening: the folder name is cut");

            string dir = TempDir();
            try
            {
                Store st = new Store();
                st.Put(LogBundle.NameFor(blob), Zip("a.log"), null);
                st.ListedSize = LogBundle.MaxBytes + 1;
                bool threw = false;
                try { DeviceLogs.Download(new AgentCatalog(st), blob, "PC", Path.Combine(dir, "logs"), Now); }
                catch (StoreException) { threw = true; }
                T.Check(threw && st.Gets == 0, "diag hardening: a bundle larger than the agent uploads is not downloaded");
                threw = false;
                try { DeviceLogs.GetZip(new AgentCatalog(st), blob); }
                catch (StoreException) { threw = true; }
                T.Check(threw && st.Gets == 0, "diag hardening: nor saved");
                st.ListedSize = -1;
                T.Check(DeviceLogs.GetZip(new AgentCatalog(st), blob) != null, "diag hardening: a normal bundle is saved");
            }
            finally { Directory.Delete(dir, true); }
        }

        static AgentDiagnostics Agent(Store requests, string pub, string state, Runner runner, List<string> log)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "trackvice-harden-" + Guid.NewGuid().ToString("N"));
            AgentDiagnostics d = new AgentDiagnostics(requests, new Store(), pub, Sid, null, true, runner, state, tmp, delegate (string x) { log.Add(x); });
            d.Background = false;
            return d;
        }

        static void StateFiles(RSACryptoServiceProvider key)
        {
            string pub = UpdateKeys.PublicKey(key);
            string dir = TempDir();
            try
            {
                // The agent stopped after it wrote the new list and before it replaced the old one: the newest id is in the second file only.
                Store requests = new Store();
                DiagnosticsRequest r = DiagnosticsRequest.Publish(requests, key, Sid, Now);
                string state = Path.Combine(dir, AgentDiagnostics.StateFile);
                File.WriteAllText(state, "handled " + Guid.NewGuid().ToString("D") + "\r\n");
                File.WriteAllText(state + ".new", "handled " + r.RequestId + "\r\n");
                Runner runner = new Runner();
                List<string> log = new List<string>();
                AgentDiagnostics d = Agent(requests, pub, state, runner, log);
                T.Check(!d.Cycle(new Session(), true, Now.AddMinutes(1)) && !d.Busy, "diag hardening: an id that is only in the second file is not followed again");
                T.Check(File.ReadAllText(state).Contains(r.RequestId) && !File.Exists(state + ".new"), "diag hardening: the list is put together again");

                // Only the second file is there.
                File.Delete(state);
                File.WriteAllText(state + ".new", "handled " + r.RequestId + "\r\n");
                d = Agent(requests, pub, state, runner, log);
                T.Check(!d.Cycle(new Session(), true, Now.AddMinutes(1)) && !d.Busy, "diag hardening: an id in the second file alone is not followed again");
                T.Check(File.Exists(state) && File.ReadAllText(state).Contains(r.RequestId), "diag hardening: and the list is there again");
                T.Eq(0, runner.Runs, "diag hardening: nothing was run");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void ClockBack(RSACryptoServiceProvider key)
        {
            // The clock of the device goes back a day: the agent goes on reading requests.
            Store requests = new Store();
            AgentDiagnostics d = Agent(requests, UpdateKeys.PublicKey(key), null, new Runner(), new List<string>());
            Session s = new Session();
            d.Cycle(s, false, Now);
            T.Eq(1, requests.Gets, "diag hardening: the first check reads");
            d.Cycle(s, false, Now.AddDays(-1));
            T.Eq(2, requests.Gets, "diag hardening: a clock set back does not stop the checks");
            d.Cycle(s, false, Now.AddDays(-1).AddMinutes(1));
            T.Eq(2, requests.Gets, "diag hardening: and the checks keep their pace after it");
        }
    }
}
