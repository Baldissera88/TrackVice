namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Security.Principal;
    using System.Text;

    // What a standard user on the device, or anyone holding an upload link, must not be able to do through the agent.
    public static class AgentSecurityTests
    {
        public static void Run()
        {
            Names();
            FolderEscape();
            BlobUrls();
            ListXml();
            Caps();
            Summary();
            Links();
            SafeFolder();
            Owners();
            ChildQuotes();
            HostileSession();
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "trackvice-sec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (StoreException) { return true; }
        }

        static void Names()
        {
            foreach (string ok in new string[] { "sessions/abc.json.gz", "agent/latest.json", "agent/TrackVice-2.1.0.exe", "logs/ts-1-diagnostics-2.zip", "sessions/a b.json.gz" })
                T.Check(Stores.SafeName(ok), "a name TrackVice writes is safe: " + ok);
            foreach (string bad in new string[] { null, "", "../x", "sessions/../../x", "sessions/./x", "a//b", "/abs", "sessions/", "a\\b", "C:x", "c:/x", "x:stream", "a/\u0001", " /x", "a/ ", new string('a', 1025) })
                T.Check(!Stores.SafeName(bad), "a name that could leave the store is refused: " + (bad == null ? "null" : bad.Length > 40 ? "long" : bad));
        }

        static void FolderEscape()
        {
            string dir = TempDir();
            try
            {
                string root = Path.Combine(dir, "store");
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(dir, "outside.txt"), "secret");
                FolderStore st = new FolderStore(root);
                T.Check(Throws(delegate { st.Get("../outside.txt"); }), "a folder store does not read above its folder");
                T.Check(Throws(delegate { st.Find("sessions/../../outside.txt"); }), "nor finds a file there");
                T.Check(Throws(delegate { st.Put("../written.txt", new byte[] { 1 }, null); }), "nor writes there");
                T.Check(!File.Exists(Path.Combine(dir, "written.txt")), "and nothing was written above the folder");
                T.Check(Throws(delegate { st.List("../"); }), "nor lists there");

                Directory.CreateDirectory(Path.Combine(root, "sessions"));
                File.WriteAllText(Path.Combine(root, "sessions", "a.json.gz"), "x");
                File.WriteAllText(Path.Combine(root, "sessions", "a.json.gz.meta"), "computer=PC%zz1\nitems=" + new string('1', 10) + "\n");
                List<StoreEntry> l = st.List("sessions/");
                T.Eq(1, l.Count, "a meta file with a broken escape is still listed");
                T.Eq("PC%zz1", l[0].Meta["computer"], "and its value is kept as it is");
                File.WriteAllText(Path.Combine(root, "sessions", "a.json.gz.meta"), "computer=" + new string('A', (int)Stores.MaxMeta + 10) + "\n");
                l = st.List("sessions/");
                T.Check(l.Count == 1 && !l[0].Meta.ContainsKey("computer"), "a meta file above the limit is not read");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void BlobUrls()
        {
            AzureStore az = new AzureStore("https://acct.blob.core.windows.net/trackvice?sv=2024&sp=rl&sig=abc");
            string u = az.UrlOf("sessions/x?sig=evil#frag%41.json.gz");
            T.Check(u.StartsWith("https://acct.blob.core.windows.net/trackvice/sessions/x%3Fsig%3Devil%23frag%2541.json.gz?"), "a blob name with ? # % stays inside the path: " + u);
            T.Check(u.EndsWith("?sv=2024&sp=rl&sig=abc") && u.IndexOf('?') == u.LastIndexOf('?'), "the token is the only query");
            T.Check(Throws(delegate { az.UrlOf("../other/x"); }), "a blob name cannot point at another container");
        }

        static void ListXml()
        {
            string bomb = "<?xml version=\"1.0\"?><!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>"
                + "<EnumerationResults><Blobs><Blob><Name>&b;</Name></Blob></Blobs></EnumerationResults>";
            bool refused = false;
            string marker;
            try { AzureStore.ParseList(bomb, out marker); }
            catch (System.Xml.XmlException) { refused = true; }
            T.Check(refused, "a listing with a document type is refused, so entities are never expanded");
            string evil = "<EnumerationResults><Blobs><Blob><Name>sessions/a.json.gz</Name><Metadata><computer>PC%zz</computer></Metadata></Blob></Blobs></EnumerationResults>";
            List<StoreEntry> l = AzureStore.ParseList(evil, out marker);
            T.Eq("PC%zz", l[0].Meta["computer"], "a metadata value with a broken escape does not stop the listing");
        }

        static void Caps()
        {
            bool threw = false;
            try { Stores.ReadCapped(new MemoryStream(new byte[1001]), 1000, "too large"); }
            catch (StoreException ex) { threw = ex.Message == "too large"; }
            T.Check(threw, "a read above the cap stops with an error");
            T.Eq(1000, Stores.ReadCapped(new MemoryStream(new byte[1000]), 1000, "x").Length, "a read at the cap is whole");

            MemoryStream z = new MemoryStream();
            using (GZipStream g = new GZipStream(z, CompressionMode.Compress, true)) g.Write(new byte[4 * 1024 * 1024], 0, 4 * 1024 * 1024);
            threw = false;
            using (GZipStream g = new GZipStream(new MemoryStream(z.ToArray()), CompressionMode.Decompress))
            {
                try { Stores.ReadCapped(g, 1024 * 1024, "bomb"); }
                catch (StoreException) { threw = true; }
            }
            T.Check(threw && z.Length < 64 * 1024, "a small zip that unpacks to more than the cap is stopped while it is unpacked");
            T.Check(AgentData.MaxSessionBytes < Stores.MaxDownload * 2 && AgentData.MaxSessionBytes >= 64L * 1024 * 1024, "a session is capped, with room above what an agent uploads");
        }

        static void Summary()
        {
            StoreEntry e = new StoreEntry();
            e.Name = "sessions/x.json.gz";
            e.Meta["computer"] = "PC\u202E1\r\nFAKE\u200B" + new string('A', 1000);
            e.Meta["items"] = "-5";
            e.Meta["done"] = "-2147483648";
            e.Meta["failed"] = "3";
            e.Meta["updated"] = DateTime.UtcNow.ToString("o");
            RemoteDeployment d = AgentData.ToDeployment(e, DateTime.UtcNow);
            T.Check(d.Device.IndexOf('\u202E') < 0 && d.Device.IndexOf('\r') < 0 && d.Device.IndexOf('\n') < 0 && d.Device.IndexOf('\u200B') < 0, "the list shows a device name without control or direction characters");
            T.Check(d.Device.Length <= AgentData.MaxSummaryText, "and not longer than " + AgentData.MaxSummaryText);
            T.Check(d.StateText.IndexOf('-') < 0, "a negative count from an upload is read as 0: " + d.StateText);

            StoreEntry noName = new StoreEntry();
            noName.Name = "sessions/a|b<c>\"d.json.gz";
            bool ok = true;
            try { T.Eq("a|b<c>\"d.json.gz", AgentData.ToDeployment(noName, DateTime.UtcNow).Device, "a session without a computer name shows its blob name"); }
            catch (ArgumentException) { ok = false; }
            T.Check(ok, "a blob name with characters a file name cannot have does not stop the list");

            Dictionary<string, string> m = new Dictionary<string, string>();
            m["current"] = "Installing setup.exe /password Secret123!";
            m["items"] = "3";
            Dictionary<string, string> masked = AgentData.MaskedSummary(m);
            T.Check(masked["current"].IndexOf("Secret123") < 0, "the summary uploaded beside the session is masked too");
            T.Eq("3", masked["items"], "numbers in the summary stay");

            ListStore ls = new ListStore();
            foreach (string n in new string[] { "sessions/good.json.gz", "sessions/../evil.json.gz", "sessions/a\u0001.json.gz", "sessions/b\\c.json.gz" })
            {
                StoreEntry x = new StoreEntry();
                x.Name = n;
                x.ModifiedUtc = DateTime.UtcNow;
                ls.Entries.Add(x);
            }
            List<RemoteDeployment> l = new AgentCatalog(ls).List(DateTime.UtcNow.AddDays(-1));
            T.Eq(1, l.Count, "blobs with names TrackVice never writes are not listed");
            T.Eq("sessions/good.json.gz", l[0].Key, "the good one is");
        }

        sealed class ListStore : ISessionStore
        {
            public readonly List<StoreEntry> Entries = new List<StoreEntry>();
            public string Title { get { return "test"; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { }
            public List<StoreEntry> List(string prefix) { return Entries; }
            public byte[] Get(string name) { throw new StoreMissingException("no"); }
        }

        static bool CanLink()
        {
            return Environment.OSVersion.Platform == PlatformID.Unix && File.Exists("/bin/ln");
        }

        static void Ln(string target, string link)
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("/bin/ln", "-s \"" + target + "\" \"" + link + "\"");
            psi.UseShellExecute = false;
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)) p.WaitForExit();
        }

        // A user who can write in a local upload folder points it elsewhere; the agent writes there as SYSTEM.
        static void Links()
        {
            if (!CanLink()) return;
            string dir = TempDir();
            try
            {
                string root = Path.Combine(dir, "upload"), victim = Path.Combine(dir, "system");
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(victim);
                Ln(victim, Path.Combine(root, "sessions"));
                FolderStore st = new FolderStore(root);
                T.Check(Throws(delegate { st.Put("sessions/a.json.gz", new byte[] { 1, 2 }, null); }), "an upload folder whose sessions folder is a link is not written through");
                T.Eq(0, Directory.GetFileSystemEntries(victim).Length, "nothing was written where the link points");
                File.Delete(Path.Combine(root, "sessions"));

                Directory.CreateDirectory(Path.Combine(root, "sessions"));
                string precious = Path.Combine(victim, "config");
                File.WriteAllText(precious, "keep");
                Ln(precious, Path.Combine(root, "sessions", "b.json.gz.tmp"));
                T.Check(Throws(delegate { st.Put("sessions/b.json.gz", new byte[] { 1, 2 }, null); }), "a temporary file that is a link is not written through");
                T.Eq("keep", File.ReadAllText(precious), "the file it points at is unchanged");
                File.Delete(Path.Combine(root, "sessions", "b.json.gz.tmp"));

                File.WriteAllText(Path.Combine(root, "sessions", "c.json.gz.tmp"), "left over");
                st.Put("sessions/c.json.gz", new byte[] { 7 }, null);
                T.Eq(1, st.Get("sessions/c.json.gz").Length, "a left-over temporary file is replaced");
            }
            finally { Directory.Delete(dir, true); }
        }

        sealed class FakeAcl : IFolderAcl
        {
            public readonly HashSet<string> Untrusted = new HashSet<string>();
            public int Restricted;
            public bool Fail;
            public bool Trusted(string path)
            {
                if (Fail) throw new UnauthorizedAccessException("no");
                return !Untrusted.Contains(Path.GetFileName(path));
            }
            public void Restrict(string folder) { Restricted++; }
        }

        static void SafeFolder()
        {
            string dir = TempDir();
            try
            {
                string folder = Path.Combine(dir, "TrackVice");
                FakeAcl acl = new FakeAcl();
                T.Check(AgentFolder.Secure(folder, acl, null) && Directory.Exists(folder) && acl.Restricted == 1, "a new folder is made and restricted");

                File.WriteAllText(Path.Combine(folder, "state.txt"), "id=x");
                File.WriteAllText(Path.Combine(folder, "stop"), "stop");
                acl.Untrusted.Add("stop");
                T.Check(AgentFolder.Secure(folder, acl, null), "the agent's own folder is kept");
                T.Check(File.Exists(Path.Combine(folder, "state.txt")), "with the agent's own files");
                T.Check(!File.Exists(Path.Combine(folder, "stop")), "a file a user put there is removed");

                acl.Untrusted.Add("TrackVice");
                File.WriteAllText(Path.Combine(folder, "agent.log"), "planted");
                T.Check(AgentFolder.Secure(folder, acl, null), "a folder a user made is replaced");
                T.Eq(0, Directory.GetFileSystemEntries(folder).Length, "and what the user put in it is gone");
                acl.Untrusted.Clear();

                if (CanLink())
                {
                    string victim = Path.Combine(dir, "system32");
                    Directory.CreateDirectory(victim);
                    File.WriteAllText(Path.Combine(victim, "stop"), "important");
                    Directory.Delete(folder, true);
                    Ln(victim, folder);
                    T.Check(AgentFolder.Secure(folder, acl, null) && !AgentFolder.IsLink(folder) && Directory.Exists(folder), "a folder that is a link is replaced by a real one");
                    T.Eq("important", File.ReadAllText(Path.Combine(victim, "stop")), "and nothing where it pointed is touched");

                    File.WriteAllText(Path.Combine(victim, "agent.log"), "system file");
                    Ln(Path.Combine(victim, "agent.log"), Path.Combine(folder, "agent.log"));
                    Ln(victim, Path.Combine(folder, "sub"));
                    T.Check(AgentFolder.Secure(folder, acl, null), "the folder with links in it is kept");
                    T.Check(!File.Exists(Path.Combine(folder, "agent.log")) && !Directory.Exists(Path.Combine(folder, "sub")), "the links in it are removed");
                    T.Check(File.Exists(Path.Combine(victim, "agent.log")) && File.Exists(Path.Combine(victim, "stop")), "not what they point at");

                    acl.Untrusted.Add("TrackVice");
                    Ln(victim, Path.Combine(folder, "deep"));
                    T.Check(AgentFolder.Secure(folder, acl, null), "a user's folder with a link in it is removed");
                    T.Check(File.Exists(Path.Combine(victim, "agent.log")) && File.Exists(Path.Combine(victim, "stop")), "without following the link");
                    acl.Untrusted.Clear();
                }

                acl.Fail = true;
                File.WriteAllText(Path.Combine(folder, "x"), "x");
                T.Check(!AgentFolder.Secure(folder, acl, null), "a folder whose owner cannot be read is not used");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void Owners()
        {
            T.Check(AgentGuard.TrustedOwner(new SecurityIdentifier("S-1-5-18")), "SYSTEM is trusted");
            T.Check(AgentGuard.TrustedOwner(new SecurityIdentifier("S-1-5-32-544")), "the administrators are trusted");
            T.Check(!AgentGuard.TrustedOwner(new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330-1001")), "a user is not");
            T.Check(!AgentGuard.TrustedOwner(new SecurityIdentifier("S-1-5-32-545")), "nor the users group");
            T.Check(!AgentGuard.TrustedOwner(null), "nor no owner");
        }

        // Splits a command line as Windows programs do (CommandLineToArgvW), to check what the child agent receives.
        static List<string> Split(string line)
        {
            List<string> l = new List<string>();
            StringBuilder cur = null;
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\')
                {
                    int n = 0;
                    while (i < line.Length && line[i] == '\\') { n++; i++; }
                    if (cur == null) cur = new StringBuilder();
                    if (i < line.Length && line[i] == '"')
                    {
                        cur.Append('\\', n / 2);
                        if (n % 2 == 1) cur.Append('"');
                        else quoted = !quoted;
                    }
                    else { cur.Append('\\', n); i--; }
                    continue;
                }
                if (c == '"') { if (cur == null) cur = new StringBuilder(); quoted = !quoted; continue; }
                if (c == ' ' && !quoted) { if (cur != null) l.Add(cur.ToString()); cur = null; continue; }
                if (cur == null) cur = new StringBuilder();
                cur.Append(c);
            }
            if (cur != null) l.Add(cur.ToString());
            return l;
        }

        static void ChildQuotes()
        {
            AgentOptions o = new AgentOptions();
            o.Upload = @"\\server\share\TrackVice\";
            o.Id = "dev1";
            o.Alert = "https://example.invalid/hook";
            List<string> a = Split(AgentControl.ChildArgs(o));
            T.Eq(@"\\server\share\TrackVice\", a[3], "a folder with a trailing backslash reaches the agent whole");
            T.Check(a.Contains("/id") && a.Contains("dev1") && a.Contains("/alert"), "and the next arguments stay arguments");
            T.Eq(@"""C:\a b\\""", AgentControl.Quote(@"C:\a b\"), "backslashes before the closing quote are doubled");
            T.Eq(@"""C:\a\b""", AgentControl.Quote(@"C:\a\b"), "other backslashes stay");
            T.Eq("\"ab\"", AgentControl.Quote("a\"b"), "quotes are removed");
        }

        static void HostileSession()
        {
            Session s = new Session();
            s.SetInfo(DeviceClock.OffsetInfo, long.MaxValue.ToString());
            T.Check(!DeviceClock.OffsetOf(s).HasValue, "a clock offset no clock can have is not believed");
            bool ok = true;
            try { DeviceClock.Apply(s, new List<Finding>(), DateTime.UtcNow); }
            catch (OverflowException) { ok = false; }
            T.Check(ok, "a hostile clock offset does not stop the findings");
            s.SetInfo(DeviceClock.OffsetInfo, "-600");
            T.Eq(TimeSpan.FromMinutes(-10), DeviceClock.OffsetOf(s).Value, "a real offset is read");

            List<NetOutage> edge = new List<NetOutage>();
            NetOutage a = new NetOutage();
            a.FromUtc = DateTime.MinValue;
            a.ToUtc = DateTime.MaxValue;
            edge.Add(a);
            edge.Add(null);
            ok = true;
            try
            {
                T.Check(NetworkOutages.Find(edge, DateTime.MaxValue) == a, "an outage at the ends of the calendar is found");
                T.Check(NetworkOutages.Find(edge, DateTime.MinValue) == a, "from both ends");
            }
            catch (ArgumentOutOfRangeException) { ok = false; }
            T.Check(ok, "outages at the ends of the calendar do not throw");

            Session many = new Session();
            DateTime t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int k = 0; k < 5000; k++)
            {
                NetOutage o = new NetOutage();
                o.FromUtc = t0.AddMinutes(-k);
                o.ToUtc = o.FromUtc.AddSeconds(5);
                many.Outages.Add(o);
            }
            many.Outages.Add(null);
            NetworkOutages.AddEvents(many);
            T.Eq(OutageRecorder.Max, many.Outages.Count, "an upload with more outages than an agent keeps is cut to the last ones");
            T.Eq(OutageRecorder.Max, many.Events.Count, "and gives that many events");
        }
    }
}
