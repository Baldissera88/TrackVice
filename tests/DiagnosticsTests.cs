namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Security.Cryptography;
    using System.Text;

    // Collect diagnostics: the signed request, what the agent does with it, and what the viewer may ask.
    public static class DiagnosticsTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
        const string Sid = "7a1e0c52000143c39a115d2b7e9f0a01";
        static RSACryptoServiceProvider key, other;
        static string pub;

        sealed class MemStore : ISessionStore
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public int Gets;
            public bool FailGets;
            public string Title { get { return "memory"; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { Files[name] = data; }
            public List<StoreEntry> List(string prefix)
            {
                List<StoreEntry> l = new List<StoreEntry>();
                foreach (string n in Files.Keys)
                    if (n.StartsWith(prefix, StringComparison.Ordinal)) { StoreEntry e = new StoreEntry(); e.Name = n; e.Size = Files[n].Length; l.Add(e); }
                return l;
            }
            public byte[] Get(string name)
            {
                Gets++;
                if (FailGets) throw new StoreException("The network is down.");
                byte[] b;
                if (!Files.TryGetValue(name, out b)) throw new StoreMissingException("Not there.");
                return b;
            }
        }

        sealed class FakeRunner : IDiagRunner
        {
            public bool ToolThere = true;
            public bool Timeout;
            public long CabBytes = 2048;
            public int SleepMs;
            public string Dsreg = "AzureAdJoined : YES\nClientSecret : hunter2hunter2\n";
            public readonly List<string> Calls = new List<string>();
            public bool Exists(DiagTool tool) { return ToolThere; }
            public int Run(DiagTool tool, string cabFolder, out string output)
            {
                // The command line is what the agent's own table gives, as the real runner uses it.
                lock (Calls) Calls.Add(DiagCommands.Exe(tool) + " " + DiagCommands.Arguments(tool, cabFolder) + " @" + DiagCommands.TimeoutMs(tool));
                output = "";
                if (SleepMs > 0) System.Threading.Thread.Sleep(SleepMs);
                if (tool == DiagTool.Dsreg) { output = Dsreg; return 0; }
                if (tool == DiagTool.ImeWin32Apps || tool == DiagTool.ImeSideCarPolicies) { output = "HKEY_LOCAL_MACHINE\\x\n    Name    REG_SZ    value\n    Password    REG_SZ    swordfish99\n"; return 0; }
                if (Timeout) throw new TimeoutException("MdmDiagnosticsTool.exe did not finish.");
                using (FileStream f = File.Create(Path.Combine(cabFolder, DiagCommands.CabName)))
                {
                    byte[] block = new byte[1024 * 1024];
                    for (long left = CabBytes; left > 0; left -= block.Length) f.Write(block, 0, (int)Math.Min(left, block.Length));
                }
                return 0;
            }
        }

        public static void Run()
        {
            key = UpdateKeys.Create();
            other = UpdateKeys.Create();
            pub = UpdateKeys.PublicKey(key);
            try
            {
                Request();
                Strict();
                Refusals();
                Commands();
                Sending();
                Gate();
                Places();
                Status();
                AgentFlow();
                Refused();
                TooBig();
                ToolMissing();
                Restart();
                Beside();
                Schedule();
            }
            finally
            {
                key.Dispose();
                other.Dispose();
            }
        }

        static DiagnosticsRequest Signed()
        {
            DiagnosticsRequest r = DiagnosticsRequest.Create(Sid, Now);
            r.Sign(key);
            return r;
        }

        static void Request()
        {
            DiagnosticsRequest r = Signed();
            T.Check(r.Verify(pub), "diagnostics: a signed request verifies");
            T.Check(r.Problem() == null, "diagnostics: a made request is valid");
            T.Eq("agent/requests/" + Sid + ".json", DiagnosticsRequest.NameFor(Sid), "diagnostics: request name");
            T.Eq(Now.AddHours(2), r.ExpiresUtc, "diagnostics: valid for 2 hours");
            T.Eq("diagnostics", r.Kind, "diagnostics: kind");
            T.Check(r.Wants("mdmdiag") && r.Wants("dsreg") && r.Wants("ime") && r.Wants("logs"), "diagnostics: everything is asked for");
            Guid g;
            T.Check(Guid.TryParse(r.RequestId, out g), "diagnostics: the request id is a GUID");
            DiagnosticsRequest back = DiagnosticsRequest.Parse(r.ToJson());
            T.Check(back.Verify(pub), "diagnostics: still verifies after JSON");
            T.Eq(r.RequestId, back.RequestId, "diagnostics: id from JSON");
            T.Eq(r.ExpiresUtc, back.ExpiresUtc, "diagnostics: expiry from JSON");

            DiagnosticsRequest t = DiagnosticsRequest.Parse(r.ToJson());
            t.Collect = "logs";
            T.Check(!t.Verify(pub), "diagnostics: a changed collect list breaks the signature");
            t = DiagnosticsRequest.Parse(r.ToJson());
            t.ExpiresUtc = t.ExpiresUtc.AddDays(5);
            T.Check(!t.Verify(pub), "diagnostics: a changed expiry breaks the signature");
            t = DiagnosticsRequest.Parse(r.ToJson());
            t.SessionId = "other";
            T.Check(!t.Verify(pub), "diagnostics: a changed session breaks the signature");
            t = DiagnosticsRequest.Parse(r.ToJson());
            t.RequestId = Guid.NewGuid().ToString("D");
            T.Check(!t.Verify(pub), "diagnostics: a changed request id breaks the signature");

            DiagnosticsRequest w = DiagnosticsRequest.Create(Sid, Now);
            w.Sign(other);
            T.Check(!w.Verify(pub), "diagnostics: another key does not verify");
            DiagnosticsRequest u = DiagnosticsRequest.Create(Sid, Now);
            T.Check(!u.Verify(pub), "diagnostics: an unsigned request does not verify");

            // A signed release manifest is not a request.
            UpdateManifest m = new UpdateManifest();
            m.Version = "2.1.0";
            m.Sha256 = AgentUpdate.Sha256Hex(new byte[] { 1 });
            m.Size = 1;
            m.Blob = UpdateManifest.BlobFor("2.1.0");
            m.PublishedUtc = Now;
            m.Sign(key);
            DiagnosticsRequest fake;
            T.Check(DiagnosticsRequest.Refuse(m.ToJson(), pub, Sid, Now, null, out fake) != null, "diagnostics: latest.json does not pass as a request");

            // The viewer writes it to the place.
            MemStore place = new MemStore();
            DiagnosticsRequest p = DiagnosticsRequest.Publish(place, key, Sid, Now);
            T.Check(place.Files.ContainsKey(DiagnosticsRequest.NameFor(Sid)), "diagnostics: Publish writes agent/requests/<session>.json");
            T.Check(DiagnosticsRequest.Parse(place.Files[DiagnosticsRequest.NameFor(Sid)]).Verify(pub), "diagnostics: the written request verifies");
            T.Eq(p.RequestId, DiagnosticsRequest.Parse(place.Files[DiagnosticsRequest.NameFor(Sid)]).RequestId, "diagnostics: Publish returns what it wrote");
            T.Eq(Sid, DiagnosticsRequest.SessionIdOf("sessions/" + Sid + ".json.gz"), "diagnostics: session id of a blob");
            T.Eq("logs/" + Sid + "-diagnostics-abc.zip", LogBundle.NameFor(DiagnosticsRequest.BundleKey(Sid, "abc")), "diagnostics: DeviceLogs finds the bundle by its key");
            T.Eq("logs/" + Sid + "-diagnostics-abc.zip", DiagnosticsRequest.BundleName(Sid, "abc"), "diagnostics: bundle name");
        }

        static void Refusals()
        {
            DiagnosticsRequest r = Signed();
            DiagnosticsRequest o;
            List<string> handled = new List<string>();
            T.Check(DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, Now.AddMinutes(5), handled, out o) == null && o != null, "diagnostics: a good request is followed");
            T.Check(DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, Now.AddHours(2), handled, out o) != null && o == null, "diagnostics: it is refused when it has expired");
            T.Check(DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, Now.AddMinutes(119), handled, out o) == null, "diagnostics: still good a minute before it expires");
            handled.Add(r.RequestId);
            string why = DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, Now, handled, out o);
            T.Check(why != null && why.Contains("already"), "diagnostics: a request that was followed is refused (replay)");
            T.Check(DiagnosticsRequest.Refuse(r.ToJson(), pub, "someone-else", Now, null, out o) != null, "diagnostics: a request for another session is refused");
            DiagnosticsRequest w = DiagnosticsRequest.Create(Sid, Now);
            w.Sign(other);
            why = DiagnosticsRequest.Refuse(w.ToJson(), pub, Sid, Now, null, out o);
            T.Check(why != null && why.Contains("not signed with the update key"), "diagnostics: a wrong key is refused");
            why = DiagnosticsRequest.Refuse(DiagnosticsRequest.Create(Sid, Now).ToJson(), pub, Sid, Now, null, out o);
            T.Check(why != null && why.Contains("not signed"), "diagnostics: an unsigned request is refused");
            T.Check(DiagnosticsRequest.Refuse(Encoding.UTF8.GetBytes("not json"), pub, Sid, Now, null, out o) != null, "diagnostics: rubbish is refused");
            T.Check(DiagnosticsRequest.Refuse(r.ToJson(), "not a key", Sid, Now, null, out o) != null, "diagnostics: a key that cannot be used refuses");

            // Signed with the right key, but not what TrackVice makes: Sign refuses them, and so does the agent.
            DiagnosticsRequest longer = DiagnosticsRequest.Create(Sid, Now);
            longer.ExpiresUtc = Now.AddDays(30);
            bool threw = false;
            try { longer.Sign(key); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "diagnostics: a request that lives a month is not signed");
            why = Forced(longer, Now, out o);
            T.Check(why != null && why.Contains("signed but not valid") && o == null, "diagnostics: a signed request that lives a month is refused");
            DiagnosticsRequest odd = DiagnosticsRequest.Create(Sid, Now);
            odd.Collect = "logs,format-c";
            T.Check(Forced(odd, Now, out o) != null && o == null, "diagnostics: a signed request for something unknown is refused");
            DiagnosticsRequest kind = DiagnosticsRequest.Create(Sid, Now);
            kind.Kind = "reboot";
            T.Check(Forced(kind, Now, out o) != null && o == null, "diagnostics: another kind is refused");
        }

        // Every field has one allowed form, and nothing else is signed or followed.
        static void Strict()
        {
            DiagnosticsRequest o;
            T.Check(DiagnosticsRequest.IsSessionId(Sid) && DiagnosticsRequest.IsSessionId("a-b_C9"), "diagnostics: session ids an agent makes");
            foreach (string bad in new string[] { "", null, "a b", "a\nrequest=x", "a/b", "..", "a.b", "æ", new string('a', 65) })
                T.Check(!DiagnosticsRequest.IsSessionId(bad), "diagnostics: not a session id: " + bad);

            // A session id with a line break would put lines of the attacker's choice into the signed text. It is never signed.
            string evil = Sid + "\nrequest=00000000-0000-0000-0000-000000000001";
            DiagnosticsRequest e = DiagnosticsRequest.Create(evil, Now);
            bool threw = false;
            try { e.Sign(key); } catch (ArgumentException) { threw = true; }
            T.Check(threw && e.Signature == null, "diagnostics: a session id with a line break is not signed");
            threw = false;
            try { DiagnosticsRequest.Publish(new MemStore(), key, evil, Now); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "diagnostics: and not published");
            T.Eq("x", DiagnosticsRequest.SessionIdOf("sessions/x.json.gz"), "diagnostics: session id of a blob, short");

            // Signed by hand with the right key, yet not in the allowed form: refused.
            DiagnosticsRequest r = DiagnosticsRequest.Create(Sid, Now);
            r.RequestId = r.RequestId.ToUpperInvariant();
            T.Check(Forced(r, Now, out o) != null, "diagnostics: a request id in upper case is refused");
            r = DiagnosticsRequest.Create(Sid, Now);
            r.RequestId = Guid.NewGuid().ToString("N");
            T.Check(Forced(r, Now, out o) != null, "diagnostics: a request id without hyphens is refused");
            r = DiagnosticsRequest.Create(Sid, Now);
            r.RequestId = "..\\..\\evil";
            T.Check(Forced(r, Now, out o) != null, "diagnostics: a request id that is a path is refused");
            r = DiagnosticsRequest.Create(Sid, Now);
            r.Collect = "logs,logs";
            T.Check(Forced(r, Now, out o) != null, "diagnostics: the same part twice is refused");
            r = DiagnosticsRequest.Create(Sid, Now);
            r.Collect = "logs, dsreg";
            T.Check(Forced(r, Now, out o) != null, "diagnostics: a part with a space is refused");
            r = DiagnosticsRequest.Create(Sid, Now);
            r.Collect = "cmd.exe /c calc";
            T.Check(Forced(r, Now, out o) != null, "diagnostics: a command as a part is refused");
            r = DiagnosticsRequest.Create(Sid, Now);
            r.Collect = "dsreg";
            T.Check(Forced(r, Now, out o) == null && o.Wants("dsreg") && !o.Wants("logs"), "diagnostics: one part alone is allowed");
            r = DiagnosticsRequest.Create(Sid, Now);
            r.CreatedUtc = DateTime.MinValue;
            r.ExpiresUtc = DateTime.MinValue.AddHours(2);
            T.Check(Forced(r, DateTime.MinValue.AddHours(1), out o) != null, "diagnostics: a request without real times is refused");

            // Made later than the clock of the device says: a little is allowed, more is not.
            r = Signed();
            T.Check(DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, Now.AddMinutes(-5), null, out o) == null, "diagnostics: 5 minutes ahead of the device is followed");
            string why = DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, Now.AddMinutes(-30), null, out o);
            T.Check(why != null && why.Contains("later than the clock"), "diagnostics: 30 minutes ahead of the device is refused");

            // The times are read in one way of writing only.
            string json = Encoding.UTF8.GetString(r.ToJson());
            string other = json.Replace("2026-11-27T10:00:00Z", "2026-11-27 10:00:00Z");
            T.Check(other != json && DiagnosticsRequest.Refuse(Encoding.UTF8.GetBytes(other), pub, Sid, Now, null, out o) != null, "diagnostics: a time written another way is refused");

            // Changed in the file, as someone who can write to the container could: each breaks the signature.
            foreach (string[] c in new string[][] {
                new string[] { "\"collect\":\"logs,mdmdiag,dsreg,ime\"", "\"collect\":\"logs\"" },
                new string[] { "2026-11-27T12:00:00Z", "2026-11-28T09:00:00Z" },
                new string[] { Sid, "0b1e0c52000143c39a115d2b7e9f0a02" },
                new string[] { r.RequestId, Guid.NewGuid().ToString("D") } })
            {
                string changed = json.Replace(c[0], c[1]);
                why = DiagnosticsRequest.Refuse(Encoding.UTF8.GetBytes(changed), pub, c[0] == Sid ? c[1] : Sid, Now, null, out o);
                T.Check(changed != json && why != null && why.Contains("not signed with the update key") && o == null, "diagnostics: changing " + c[0] + " in the file is refused");
            }
            // The signature of one request does not fit another, such as an older one that asked for less.
            DiagnosticsRequest second = Signed();
            DiagnosticsRequest mixed = DiagnosticsRequest.Parse(second.ToJson());
            mixed.Signature = r.Signature;
            T.Check(DiagnosticsRequest.Refuse(mixed.ToJson(), pub, Sid, Now, null, out o) != null, "diagnostics: the signature of another request is refused");

            // More fields in the file change nothing: they are not read.
            string extra = json.Replace("{", "{\"command\":\"cmd.exe /c calc\",\"run\":\"powershell\",");
            T.Check(DiagnosticsRequest.Refuse(Encoding.UTF8.GetBytes(extra), pub, Sid, Now, null, out o) == null && o.Collect == DiagnosticsRequest.DefaultCollect, "diagnostics: extra fields in the file are not read");

            // A large file is not a request.
            byte[] large = new byte[DiagnosticsRequest.MaxBytes + 1];
            why = DiagnosticsRequest.Refuse(large, pub, Sid, Now, null, out o);
            T.Check(why != null && why.Contains("too large"), "diagnostics: a large file is refused");
            T.Check(r.ToJson().Length < DiagnosticsRequest.MaxBytes / 4, "diagnostics: a real request is far below the limit");
            T.Check(DiagnosticsRequest.Refuse(new byte[0], pub, Sid, Now, null, out o) != null && DiagnosticsRequest.Refuse(null, pub, Sid, Now, null, out o) != null, "diagnostics: an empty file is refused");
            T.Check(DiagnosticsRequest.Refuse(Encoding.UTF8.GetBytes("null"), pub, Sid, Now, null, out o) != null, "diagnostics: null is refused");

            // The id of a followed request is found whatever its case in the list.
            List<string> handled = new List<string>();
            handled.Add(r.RequestId.ToUpperInvariant());
            T.Check(DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, Now, handled, out o) != null, "diagnostics: followed already, whatever the case of the id");
        }

        // Signs a request as it is, without the check Sign makes, and reads it as the agent does.
        static string Forced(DiagnosticsRequest r, DateTime now, out DiagnosticsRequest o)
        {
            r.Signature = Convert.ToBase64String(key.SignData(r.Payload(), "SHA256"));
            return DiagnosticsRequest.Refuse(r.ToJson(), pub, Sid, now, null, out o);
        }

        // What the agent can start is a fixed list, with fixed arguments.
        static void Commands()
        {
            T.Eq(4, Enum.GetValues(typeof(DiagTool)).Length, "diagnostics: four things can be started and no more");
            T.Eq("dsregcmd.exe /status", DiagCommands.Exe(DiagTool.Dsreg) + " " + DiagCommands.Arguments(DiagTool.Dsreg, null), "diagnostics: dsregcmd");
            T.Eq("reg.exe query \"HKLM\\SOFTWARE\\Microsoft\\IntuneManagementExtension\\Win32Apps\" /s", DiagCommands.Exe(DiagTool.ImeWin32Apps) + " " + DiagCommands.Arguments(DiagTool.ImeWin32Apps, "ignored"), "diagnostics: reg query Win32Apps");
            T.Eq("reg.exe query \"HKLM\\SOFTWARE\\Microsoft\\IntuneManagementExtension\\SideCarPolicies\" /s", DiagCommands.Exe(DiagTool.ImeSideCarPolicies) + " " + DiagCommands.Arguments(DiagTool.ImeSideCarPolicies, null), "diagnostics: reg query SideCarPolicies");
            string folder = Path.Combine(Path.GetTempPath(), "x");
            T.Eq("MdmDiagnosticsTool.exe -area \"Autopilot;DeviceEnrollment;DeviceProvisioning;TPM\" -cab \"" + Path.Combine(folder, "MDMDiagnostics.cab") + "\"", DiagCommands.Exe(DiagTool.MdmDiagnostics) + " " + DiagCommands.Arguments(DiagTool.MdmDiagnostics, folder), "diagnostics: MdmDiagnosticsTool");
            T.Eq(120000, DiagCommands.TimeoutMs(DiagTool.MdmDiagnostics), "diagnostics: MdmDiagnosticsTool has 120 seconds");
            bool threw = false;
            try { DiagCommands.Arguments(DiagTool.MdmDiagnostics, "a\" & calc & \""); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "diagnostics: a folder with a quote is never put on a command line");
            threw = false;
            try { DiagCommands.Exe((DiagTool)99); } catch (ArgumentException) { threw = true; }
            T.Check(threw, "diagnostics: an unknown tool has no program");
        }

        static UpdateLocation FolderPlace(string folder)
        {
            UpdateLocation l = new UpdateLocation();
            l.Key = folder.ToLowerInvariant();
            l.Folder = folder;
            l.ReadLink = l.WriteLink = folder;
            l.Customers.Add("Contoso");
            return l;
        }

        // The viewer: reads the place first, as Publish does, then signs and writes.
        static void Sending()
        {
            string folder = Path.Combine(Path.GetTempPath(), "trackvice-diag-place-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                Func<string, ISessionStore> open = UpdateLocations.OpenWith(null);
                UpdateLocation place = FolderPlace(folder);
                DiagnosticsRequest r = DiagnosticsSupport.Send(place, open, key, Sid, Now);
                DiagnosticsRequest o;
                byte[] written = new FolderStore(folder).Get(DiagnosticsRequest.NameFor(Sid));
                T.Check(DiagnosticsRequest.Refuse(written, pub, Sid, Now, null, out o) == null && o.RequestId == r.RequestId, "diagnostics: Send writes a request the agent follows");

                // The agents of the place follow another key: nothing is written.
                UpdatePublisher.Publish(new FolderStore(folder), new byte[] { 1, 2, 3 }, "9.9.9", other, Now);
                bool threw = false;
                try { DiagnosticsSupport.Send(place, open, key, "abc", Now); }
                catch (StoreException ex) { threw = ex.Message.Contains("another update key"); }
                T.Check(threw, "diagnostics: Send refuses a place that is signed with another update key");
                T.Check(!File.Exists(Path.Combine(folder, DiagnosticsRequest.NameFor("abc").Replace('/', Path.DirectorySeparatorChar))), "diagnostics: and writes nothing there");

                threw = false;
                try { DiagnosticsSupport.Send(place, open, null, Sid, Now); }
                catch (StoreException ex) { threw = ex.Message.Contains("no update key"); }
                T.Check(threw, "diagnostics: Send without an update key says so");

                UpdateLocation readOnly = FolderPlace(folder);
                readOnly.WriteLink = "";
                readOnly.Missing = "Get access in Publish agent update.";
                threw = false;
                try { DiagnosticsSupport.Send(readOnly, open, key, Sid, Now); }
                catch (StoreException ex) { threw = ex.Message.Contains("cannot write") && ex.Message.Contains("Get access"); }
                T.Check(threw, "diagnostics: Send says what is missing to write");

                threw = false;
                try { DiagnosticsSupport.Send(FolderPlace(Path.Combine(folder, "fresh")), open, key, "bad id", Now); }
                catch (StoreException) { threw = true; }
                T.Check(threw, "diagnostics: Send refuses a session id an agent does not make");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }

        static void Gate()
        {
            string g = DiagnosticsSupport.AgentGate("2.0.0", "2.1.0");
            T.Check(g != null && g.Contains("This agent is 2.0.0; Collect diagnostics needs 2.1.0 or later"), "diagnostics: gate for 2.0.0: " + g);
            T.Check(DiagnosticsSupport.AgentGate("1.4.0", "2.1.0") != null, "diagnostics: gate for 1.4.0");
            T.Check(DiagnosticsSupport.AgentGate("2.1.0", "2.1.0") == null, "diagnostics: the same version may collect");
            T.Check(DiagnosticsSupport.AgentGate("2.10.1", "2.9.0") == null, "diagnostics: 2.10.1 is later than 2.9.0");
            T.Check(DiagnosticsSupport.AgentGate("2.1.0", "2.4.0") == null, "diagnostics: an agent of the first version may collect for a newer viewer");
            T.Check(DiagnosticsSupport.AgentGate("2.0.11", "2.4.0").Contains("needs 2.1.0 or later") && DiagnosticsSupport.AgentGate("2.0.11", "2.4.0").Contains("brings the agents to 2.4.0"), "diagnostics: the gate names the first version and the version to publish");
            T.Check(DiagnosticsSupport.AgentGate("3.0.0", "2.1.0") == null, "diagnostics: a later agent may collect");
            T.Check(DiagnosticsSupport.AgentGate("2.1.0-beta1", "2.1.0") != null, "diagnostics: a 2.1.0 beta is before 2.1.0");
            T.Check(DiagnosticsSupport.AgentGate("", "2.1.0") != null && DiagnosticsSupport.AgentGate(null, "2.1.0") != null, "diagnostics: no version is not enough");
            // An agent of this version can always collect.
            T.Check(DiagnosticsSupport.AgentGate(TrackVice.Program.Version, TrackVice.Program.Version) == null, "diagnostics: an agent of this version may collect");
            string older = DiagnosticsSupport.AgentGate("0.9.0", TrackVice.Program.Version);
            T.Check(older != null && older.Contains("needs " + DiagnosticsSupport.FirstVersion + " or later"), "diagnostics: an older agent is told the first version that can: " + older);
        }

        static UpdateLocation Place(string customer, string write, bool otherKey)
        {
            UpdateLocation l = new UpdateLocation();
            l.Key = "https://a.blob.core.windows.net/trackvice-agent";
            l.Account = "a";
            l.Container = "trackvice-agent";
            l.Customers.Add(customer);
            l.WriteLink = write;
            l.OtherKey = otherKey;
            if (write.Length == 0) l.Missing = "Get access in Publish agent update.";
            return l;
        }

        static void Places()
        {
            string problem;
            List<UpdateLocation> list = new List<UpdateLocation>();
            list.Add(Place("Contoso", "https://a.blob.core.windows.net/trackvice-agent?sp=cw&sig=x", false));
            UpdateLocation p = DiagnosticsSupport.Place(list, "contoso", out problem);
            T.Check(p != null && problem == null, "diagnostics: the place of a customer is found, ignoring case");
            p = DiagnosticsSupport.Place(list, "Fabrikam", out problem);
            T.Check(p == null && problem != null && problem.Contains("Fabrikam"), "diagnostics: an unknown customer says so");
            list.Clear();
            list.Add(Place("Contoso", "", false));
            p = DiagnosticsSupport.Place(list, "Contoso", out problem);
            T.Check(p == null && problem.Contains("cannot write") && problem.Contains("Publish agent update"), "diagnostics: no write link says what is missing: " + problem);
            list.Clear();
            list.Add(Place("Contoso", "https://a.blob.core.windows.net/trackvice-agent?sp=cw&sig=x", true));
            p = DiagnosticsSupport.Place(list, "Contoso", out problem);
            T.Check(p == null && problem.Contains("another update key"), "diagnostics: another key on the place is refused");
            p = DiagnosticsSupport.Place(new List<UpdateLocation>(), "Contoso", out problem);
            T.Check(p == null && problem != null, "diagnostics: no place at all");
        }

        static Session NewSession()
        {
            Session s = new Session();
            s.Computer = "CONTOSO-LT0417";
            s.AppVersion = "2.1.0";
            return s;
        }

        static byte[] Logs(Session s)
        {
            return DeviceLogs.Build(s, new List<KeyValuePair<string, string>>());
        }

        static AgentDiagnostics Make(MemStore requests, MemStore upload, FakeRunner runner, Func<Session, byte[]> logs, string state, List<string> log)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "trackvice-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            AgentDiagnostics d = new AgentDiagnostics(requests, upload, pub, Sid, logs, true, runner, state, tmp, delegate (string x) { log.Add(x); });
            d.Background = false;
            return d;
        }

        static Dictionary<string, string> Unzip(byte[] zip)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            using (ZipArchive a = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                foreach (ZipArchiveEntry e in a.Entries)
                    using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8)) d[e.FullName] = e.FullName.EndsWith(".cab") ? "cab:" + e.Length : r.ReadToEnd();
            return d;
        }

        // The three cycles of one request: received, collecting, uploaded.
        static DiagnosticsEntry Walk(AgentDiagnostics d, Session s, DateTime t, List<string> states)
        {
            for (int k = 0; k < 3; k++)
            {
                T.Check(d.Cycle(s, true, t.AddSeconds(k)), "diagnostics: step " + (k + 1) + " changes the progress");
                d.Stamp(s);
                states.Add(DiagnosticsStatus.Of(s)[0].State);
            }
            return DiagnosticsStatus.Of(s)[0];
        }

        static void AgentFlow()
        {
            MemStore requests = new MemStore(), upload = new MemStore();
            FakeRunner runner = new FakeRunner();
            List<string> log = new List<string>();
            string state = Path.Combine(Path.GetTempPath(), "trackvice-diag-" + Guid.NewGuid().ToString("N") + ".txt");
            AgentDiagnostics d = Make(requests, upload, runner, Logs, state, log);
            Session s = NewSession();

            // 404: nothing is there.
            T.Check(!d.Cycle(s, true, Now), "diagnostics: no request file, nothing happens");
            T.Eq(0, DiagnosticsStatus.Of(s).Count, "diagnostics: no entries for a 404");
            T.Eq(0, log.Count, "diagnostics: a 404 is not logged");

            DiagnosticsRequest r = DiagnosticsRequest.Publish(requests, key, Sid, Now);
            List<string> states = new List<string>();
            DiagnosticsEntry e = Walk(d, s, Now.AddSeconds(60), states);
            T.Eq("Received,Collecting,Uploaded", string.Join(",", states.ToArray()), "diagnostics: progress goes Received, Collecting, Uploaded");
            T.Eq(r.RequestId, e.Id, "diagnostics: the entry has the request id");
            T.Eq(DiagnosticsRequest.BundleName(Sid, r.RequestId), e.Blob, "diagnostics: the entry names the bundle");
            T.Check(upload.Files.ContainsKey(e.Blob), "diagnostics: the bundle is uploaded");
            Dictionary<string, string> zip = Unzip(upload.Files[e.Blob]);
            T.Check(zip.ContainsKey("session.json") && zip.ContainsKey(LogBundle.IndexName), "diagnostics: the bundle has the session and the index");
            T.Check(zip.ContainsKey(AgentDiagnostics.DsregName) && zip[AgentDiagnostics.DsregName].Contains("AzureAdJoined"), "diagnostics: dsregcmd output is in");
            T.Check(!zip[AgentDiagnostics.DsregName].Contains("hunter2hunter2"), "diagnostics: secrets are masked in dsregcmd output");
            T.Check(zip.ContainsKey(AgentDiagnostics.ImeName) && zip[AgentDiagnostics.ImeName].Contains("Win32Apps") && zip[AgentDiagnostics.ImeName].Contains("SideCarPolicies"), "diagnostics: IME registry keys are in");
            T.Check(zip.ContainsKey(DiagCommands.CabName) && zip[DiagCommands.CabName] == "cab:2048", "diagnostics: the cab is in");
            T.Check(!zip[AgentDiagnostics.ImeName].Contains("swordfish99"), "diagnostics: secrets are masked in the registry export");
            T.Check(zip[AgentDiagnostics.ImeName].Contains("Name    REG_SZ    value"), "diagnostics: other registry values stay as they are");
            T.Eq("    ClientSecret    REG_SZ    ***", AgentDiagnostics.MaskRegLine("    ClientSecret    REG_SZ    two words"), "diagnostics: a secret value with a space is hidden whole");
            T.Eq("    PasswordExpirationDays    REG_DWORD    0x5a", AgentDiagnostics.MaskRegLine("    PasswordExpirationDays    REG_DWORD    0x5a"), "diagnostics: a setting about passwords is not a secret");
            T.Eq("HKEY_LOCAL_MACHINE\\SOFTWARE\\x", AgentDiagnostics.MaskRegLine("HKEY_LOCAL_MACHINE\\SOFTWARE\\x"), "diagnostics: a key line stays");
            T.Eq(4, runner.Calls.Count, "diagnostics: dsregcmd, two registry keys and MdmDiagnosticsTool were started, nothing else");
            foreach (string c in runner.Calls) T.Check(c.StartsWith("dsregcmd.exe /status @") || c.StartsWith("reg.exe query \"HKLM\\SOFTWARE\\Microsoft\\IntuneManagementExtension\\") || c.StartsWith("MdmDiagnosticsTool.exe -area "), "diagnostics: only the built-in programs: " + c);
            T.Check(!d.Busy, "diagnostics: not busy when it is uploaded");
            T.Check(zip[AgentDiagnostics.NotesName].Contains(r.RequestId), "diagnostics: the notes name the request");
            T.Check(zip[LogBundle.IndexName].Contains(AgentDiagnostics.DsregName + "\t"), "diagnostics: the index maps the extra files");
            bool tool = false;
            foreach (string c in runner.Calls) if (c.StartsWith("MdmDiagnosticsTool.exe -area \"Autopilot;DeviceEnrollment;DeviceProvisioning;TPM\" -cab ") && c.EndsWith("@120000")) tool = true;
            T.Check(tool, "diagnostics: MdmDiagnosticsTool runs with the areas, a cab path and 120 seconds");

            // The status goes through the session JSON as the agent uploads it.
            Session back = Session.FromJson(s.ToJson());
            DiagnosticsEntry round = DiagnosticsStatus.Find(back, r.RequestId);
            T.Check(round != null && round.State == "Uploaded" && round.Blob == e.Blob, "diagnostics: the status survives the session upload");
            T.Eq(2, DiagnosticsStatus.Step(DiagnosticsStatus.Decode("a|Collecting|2026-11-27T10:00:00Z||")[0]), "diagnostics: Collecting is step 2");
            T.Eq(3, DiagnosticsStatus.Step(round), "diagnostics: Uploaded is step 3");
            T.Check(AgentVersions.IsKey(DiagnosticsStatus.InfoKey), "diagnostics: the status is not listed as device information");
            // The agent uploads a masked copy of the session: the status is the same in it.
            Session masked = Session.FromJson(AgentData.Unpack(AgentData.Pack(Secrets.MaskedCopy(s))).ToJson());
            round = DiagnosticsStatus.Find(masked, r.RequestId);
            T.Check(round != null && round.State == "Uploaded" && round.Blob == e.Blob && round.Utc == e.Utc, "diagnostics: the status survives masking and packing");

            // The same request again is not followed again, here and after a restart.
            int uploads = upload.Files.Count;
            T.Check(!d.Cycle(s, true, Now.AddMinutes(10)), "diagnostics: a request that was followed is ignored");
            AgentDiagnostics restarted = Make(requests, upload, runner, Logs, state, log);
            T.Check(!restarted.Cycle(NewSession(), true, Now.AddMinutes(20)), "diagnostics: it is ignored after a restart too");
            T.Eq(uploads, upload.Files.Count, "diagnostics: nothing more is uploaded");
            int ignored = 0;
            foreach (string l in log) if (l.StartsWith("Diagnostics request ignored")) ignored++;
            T.Eq(2, ignored, "diagnostics: the replay is logged once for each agent");
            // What was uploaded is still told after the restart.
            Session after = NewSession();
            restarted.Stamp(after);
            T.Check(DiagnosticsStatus.Find(after, r.RequestId) != null && DiagnosticsStatus.Find(after, r.RequestId).State == "Uploaded", "diagnostics: the progress is there again after a restart");
            // An older request put back after a newer one was followed is not followed either.
            byte[] first = requests.Files[DiagnosticsRequest.NameFor(Sid)];
            DiagnosticsRequest newer = DiagnosticsRequest.Publish(requests, key, Sid, Now.AddMinutes(30));
            Walk(restarted, after, Now.AddMinutes(31), new List<string>());
            T.Eq(uploads + 1, upload.Files.Count, "diagnostics: a new request is followed");
            requests.Files[DiagnosticsRequest.NameFor(Sid)] = first;
            T.Check(!restarted.Cycle(after, true, Now.AddMinutes(45)), "diagnostics: the first request put back is not followed");
            requests.Files[DiagnosticsRequest.NameFor(Sid)] = newer.ToJson();
            T.Check(!restarted.Cycle(after, true, Now.AddMinutes(50)), "diagnostics: nor the second");
            T.Eq(uploads + 1, upload.Files.Count, "diagnostics: nothing is uploaded for requests put back");
            try { File.Delete(state); } catch (Exception) { }
            // A request for another device, written under this device's name.
            DiagnosticsRequest foreign = DiagnosticsRequest.Create("0b1e0c52000143c39a115d2b7e9f0a02", Now.AddMinutes(55));
            foreign.Sign(key);
            requests.Files[DiagnosticsRequest.NameFor(Sid)] = foreign.ToJson();
            T.Check(!restarted.Cycle(after, true, Now.AddMinutes(56)), "diagnostics: a request of another device is not followed");
            T.Check(log[log.Count - 1].Contains("another session"), "diagnostics: the log says it is for another session");
            try { File.Delete(state); } catch (Exception) { }
        }

        static void Refused()
        {
            MemStore requests = new MemStore(), upload = new MemStore();
            List<string> log = new List<string>();
            AgentDiagnostics d = Make(requests, upload, new FakeRunner(), Logs, null, log);
            Session s = NewSession();
            DiagnosticsRequest bad = DiagnosticsRequest.Create(Sid, Now);
            bad.Sign(other);
            requests.Files[DiagnosticsRequest.NameFor(Sid)] = bad.ToJson();
            for (int k = 0; k < 4; k++) T.Check(!d.Cycle(s, true, Now.AddMinutes(k * 2)), "diagnostics: a wrongly signed request is ignored (" + k + ")");
            T.Eq(1, log.Count, "diagnostics: it is logged once, not at every check");
            T.Check(log[0].Contains("not signed with the update key"), "diagnostics: the log says why");
            T.Eq(0, upload.Files.Count, "diagnostics: nothing is uploaded for it");

            DiagnosticsRequest unsigned = DiagnosticsRequest.Create(Sid, Now);
            requests.Files[DiagnosticsRequest.NameFor(Sid)] = unsigned.ToJson();
            T.Check(!d.Cycle(s, true, Now.AddMinutes(20)), "diagnostics: an unsigned request is ignored");
            T.Eq(2, log.Count, "diagnostics: another file is logged again");

            DiagnosticsRequest old = DiagnosticsRequest.Create(Sid, Now.AddHours(-5));
            old.Sign(key);
            requests.Files[DiagnosticsRequest.NameFor(Sid)] = old.ToJson();
            T.Check(!d.Cycle(s, true, Now.AddMinutes(22)), "diagnostics: an expired request is ignored");
            T.Check(log[log.Count - 1].Contains("expired"), "diagnostics: the log says it expired");

            requests.FailGets = true;
            T.Check(!d.Cycle(s, true, Now.AddMinutes(24)), "diagnostics: a failing read is not an error");
            T.Check(log[log.Count - 1].Contains("network is down"), "diagnostics: a failing read is logged");
            int n = log.Count;
            d.Cycle(s, true, Now.AddMinutes(26));
            T.Eq(n, log.Count, "diagnostics: the same read failure is logged once");
            requests.FailGets = false;
            DiagnosticsRequest.Publish(requests, key, Sid, Now.AddMinutes(27));
            T.Check(d.Cycle(s, true, Now.AddMinutes(28)), "diagnostics: a good request after the bad ones is followed");
        }

        static void TooBig()
        {
            // The log bundle is too large or empty: the session and the text files go up with a note.
            MemStore requests = new MemStore(), upload = new MemStore();
            List<string> log = new List<string>();
            AgentDiagnostics d = Make(requests, upload, new FakeRunner(), delegate (Session x) { return null; }, null, log);
            Session s = NewSession();
            DiagnosticsRequest.Publish(requests, key, Sid, Now);
            DiagnosticsEntry e = Walk(d, s, Now.AddSeconds(30), new List<string>());
            T.Eq("Uploaded", e.State, "diagnostics: with no log bundle it still uploads");
            T.Check(e.Note.Contains("log files were left out"), "diagnostics: the note says the logs were left out: " + e.Note);
            Dictionary<string, string> zip = Unzip(upload.Files[e.Blob]);
            T.Check(zip.ContainsKey("session.json") && zip.ContainsKey(AgentDiagnostics.DsregName), "diagnostics: session and text files are in the smaller bundle");

            // The cab would take the bundle over the limit: it is left out.
            requests = new MemStore();
            upload = new MemStore();
            FakeRunner big = new FakeRunner();
            big.CabBytes = LogBundle.MaxBytes + 1024 * 1024;
            log = new List<string>();
            d = Make(requests, upload, big, Logs, null, log);
            s = NewSession();
            DiagnosticsRequest.Publish(requests, key, Sid, Now);
            e = Walk(d, s, Now.AddSeconds(30), new List<string>());
            T.Eq("Uploaded", e.State, "diagnostics: a cab above the limit does not stop the upload");
            T.Check(e.Note.Contains("cab was left out"), "diagnostics: the note says the cab was left out: " + e.Note);
            zip = Unzip(upload.Files[e.Blob]);
            T.Check(!zip.ContainsKey(DiagCommands.CabName) && zip.ContainsKey(AgentDiagnostics.ImeName), "diagnostics: no cab in the bundle, the rest is");
            T.Check(upload.Files[e.Blob].Length <= LogBundle.MaxBytes, "diagnostics: the bundle respects the limit");
            T.Check(zip[AgentDiagnostics.NotesName].Contains("cab was left out"), "diagnostics: the notes file says so too");
        }

        static void ToolMissing()
        {
            MemStore requests = new MemStore(), upload = new MemStore();
            FakeRunner runner = new FakeRunner();
            runner.ToolThere = false;
            List<string> log = new List<string>();
            AgentDiagnostics d = Make(requests, upload, runner, Logs, null, log);
            Session s = NewSession();
            DiagnosticsRequest.Publish(requests, key, Sid, Now);
            DiagnosticsEntry e = Walk(d, s, Now.AddSeconds(30), new List<string>());
            T.Eq("Uploaded", e.State, "diagnostics: without MdmDiagnosticsTool it still uploads");
            T.Check(e.Note.Contains("MdmDiagnosticsTool.exe is not on this device"), "diagnostics: the note says the tool is missing: " + e.Note);
            T.Check(!Unzip(upload.Files[e.Blob]).ContainsKey(DiagCommands.CabName), "diagnostics: no cab without the tool");
            foreach (string c in runner.Calls) T.Check(!c.StartsWith("MdmDiagnosticsTool"), "diagnostics: the tool is not started when it is missing");

            // The tool does not finish in time.
            requests = new MemStore();
            upload = new MemStore();
            runner = new FakeRunner();
            runner.Timeout = true;
            d = Make(requests, upload, runner, Logs, null, log);
            s = NewSession();
            DiagnosticsRequest.Publish(requests, key, Sid, Now);
            e = Walk(d, s, Now.AddSeconds(30), new List<string>());
            T.Eq("Uploaded", e.State, "diagnostics: a tool that times out is skipped");
            T.Check(e.Note.Contains("did not finish in 120 seconds"), "diagnostics: the note says it was stopped: " + e.Note);

            // The upload fails: the progress says Failed.
            requests = new MemStore();
            FailingUpload failing = new FailingUpload();
            d = new AgentDiagnostics(requests, failing, pub, Sid, Logs, true, new FakeRunner(), null, Path.GetTempPath(), delegate (string x) { log.Add(x); });
            d.Background = false;
            s = NewSession();
            DiagnosticsRequest.Publish(requests, key, Sid, Now);
            e = Walk(d, s, Now.AddSeconds(30), new List<string>());
            T.Eq("Failed", e.State, "diagnostics: a failed upload is Failed");
            T.Check(e.Note.Contains("denied"), "diagnostics: Failed says why: " + e.Note);
        }

        sealed class FailingUpload : ISessionStore
        {
            public string Title { get { return "fail"; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { throw new StoreException("Access denied."); }
            public List<StoreEntry> List(string prefix) { return new List<StoreEntry>(); }
            public byte[] Get(string name) { throw new StoreMissingException("no"); }
        }

        static void Status()
        {
            List<DiagnosticsEntry> l = new List<DiagnosticsEntry>();
            for (int k = 0; k < 8; k++)
            {
                DiagnosticsEntry e = new DiagnosticsEntry();
                e.Id = "id" + k;
                e.State = "Uploaded";
                e.Utc = Now.AddMinutes(k);
                e.Blob = "logs/x|y.zip";
                e.Note = "a note\nwith | bars";
                l.Add(e);
            }
            List<DiagnosticsEntry> back = DiagnosticsStatus.Decode(DiagnosticsStatus.Encode(l));
            T.Eq(DiagnosticsStatus.Keep, back.Count, "diagnostics: only the newest requests are kept");
            T.Eq("id7", back[back.Count - 1].Id, "diagnostics: the newest is last");
            T.Eq(Now.AddMinutes(7), back[back.Count - 1].Utc, "diagnostics: the time round trips");
            T.Check(back[0].Note.IndexOf('\n') < 0 && back[0].Note.StartsWith("a note"), "diagnostics: a note stays on one line");
            T.Eq(0, DiagnosticsStatus.Decode(null).Count, "diagnostics: no status");
            T.Eq(0, DiagnosticsStatus.Step(null), "diagnostics: no entry yet is Requested");
            T.Eq("Requested", DiagnosticsStatus.Label(null), "diagnostics: label of Requested");
        }

        static void Restart()
        {
            // The agent stops between Received and the upload: after the restart the request counts as followed and failed.
            MemStore requests = new MemStore(), upload = new MemStore();
            List<string> log = new List<string>();
            string state = Path.Combine(Path.GetTempPath(), "trackvice-diag-" + Guid.NewGuid().ToString("N") + ".txt");
            AgentDiagnostics d = Make(requests, upload, new FakeRunner(), Logs, state, log);
            Session s = NewSession();
            DiagnosticsRequest r = DiagnosticsRequest.Publish(requests, key, Sid, Now);
            T.Check(d.Cycle(s, true, Now.AddSeconds(5)) && d.Busy, "diagnostics: received, and busy");
            T.Check(File.ReadAllText(state).Contains("handled " + r.RequestId), "diagnostics: the id is saved before anything is collected");
            FakeRunner runner = new FakeRunner();
            AgentDiagnostics again = Make(requests, upload, runner, Logs, state, log);
            Session s2 = NewSession();
            T.Check(!again.Cycle(s2, true, Now.AddSeconds(90)), "diagnostics: a request that was under way at a restart is not started again");
            again.Stamp(s2);
            DiagnosticsEntry e = DiagnosticsStatus.Find(s2, r.RequestId);
            T.Check(e != null && e.State == "Failed" && e.Note == AgentDiagnostics.Interrupted, "diagnostics: it is told as failed, to ask again");
            T.Eq(0, runner.Calls.Count + upload.Files.Count, "diagnostics: nothing is run or uploaded for it");
            try { File.Delete(state); } catch (Exception) { }

            // The id cannot be saved: the request is not followed at all, now or later.
            requests = new MemStore();
            upload = new MemStore();
            log = new List<string>();
            runner = new FakeRunner();
            string nowhere = Path.Combine(Path.Combine(Path.GetTempPath(), "trackvice-diag-missing-" + Guid.NewGuid().ToString("N")), "diagnostics.txt");
            d = Make(requests, upload, runner, Logs, nowhere, log);
            DiagnosticsRequest.Publish(requests, key, Sid, Now);
            for (int k = 0; k < 5; k++) T.Check(!d.Cycle(s, true, Now.AddMinutes(1 + k * 2)), "diagnostics: a request that cannot be remembered is not followed (" + k + ")");
            T.Check(!d.Busy && runner.Calls.Count == 0 && upload.Files.Count == 0, "diagnostics: nothing is run or uploaded for it");
            int told = 0;
            foreach (string l in log) if (l.Contains("could not be remembered")) told++;
            T.Eq(1, told, "diagnostics: the log says once that it could not be remembered");

            // A state file that cannot be read: no request is followed.
            string dir = Path.Combine(Path.GetTempPath(), "trackvice-diag-dir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "diagnostics.txt"));
            log = new List<string>();
            bool made = true;
            try { d = Make(requests, upload, runner, Logs, Path.Combine(dir, "diagnostics.txt"), log); }
            catch (Exception) { made = false; }
            T.Check(made, "diagnostics: a state that cannot be read does not stop the agent");
            T.Check(!d.Cycle(s, true, Now.AddMinutes(1)) && upload.Files.Count == 0, "diagnostics: and nothing is followed");
            try { Directory.Delete(dir, true); } catch (Exception) { }

            // Files that are refused are logged, but someone writing new ones all the time cannot fill the log.
            requests = new MemStore();
            log = new List<string>();
            d = Make(requests, upload, new FakeRunner(), Logs, null, log);
            for (int k = 0; k < 60; k++)
            {
                requests.Files[DiagnosticsRequest.NameFor(Sid)] = Encoding.UTF8.GetBytes("{\"session\":\"" + k + "\"}");
                d.Cycle(s, true, Now.AddMinutes(2 * k));
            }
            T.Eq(AgentDiagnostics.TellAtMost, log.Count, "diagnostics: at most " + AgentDiagnostics.TellAtMost + " refused files are logged");
        }

        // As the agent runs it: the programs run on a thread of their own and the cycle goes on meanwhile.
        static void Beside()
        {
            MemStore requests = new MemStore(), upload = new MemStore();
            FakeRunner runner = new FakeRunner();
            runner.SleepMs = 150;
            List<string> log = new List<string>();
            AgentDiagnostics d = Make(requests, upload, runner, Logs, null, log);
            d.Background = true;
            Session s = NewSession();
            DiagnosticsRequest r = DiagnosticsRequest.Publish(requests, key, Sid, Now);
            T.Check(d.Cycle(s, true, Now), "diagnostics: beside, received");
            T.Check(d.Cycle(s, true, Now.AddSeconds(2)), "diagnostics: beside, collecting");
            System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
            bool changed = d.Cycle(s, true, Now.AddSeconds(4));
            T.Check(!changed && d.Busy && w.ElapsedMilliseconds < 140, "diagnostics: the cycle does not wait for the programs");
            int waits = 0;
            while (!d.Cycle(s, true, Now.AddSeconds(6)) && waits++ < 400) System.Threading.Thread.Sleep(25);
            d.Stamp(s);
            DiagnosticsEntry e = DiagnosticsStatus.Find(s, r.RequestId);
            T.Check(e != null && e.State == "Uploaded" && !d.Busy, "diagnostics: beside, uploaded when the programs are done");
            T.Eq(4, runner.Calls.Count, "diagnostics: beside, every program ran once");

            // Programs that never come back: given up after the longest they may take, and told as failed.
            requests = new MemStore();
            runner = new FakeRunner();
            runner.SleepMs = 3000;
            d = Make(requests, upload, runner, Logs, null, log);
            d.Background = true;
            s = NewSession();
            r = DiagnosticsRequest.Publish(requests, key, Sid, Now);
            d.Cycle(s, true, Now);
            d.Cycle(s, true, Now.AddSeconds(2));
            d.Cycle(s, true, Now.AddSeconds(4));
            T.Check(!d.Cycle(s, true, Now.AddSeconds(4) + DiagCommands.Longest - TimeSpan.FromSeconds(1)), "diagnostics: still waiting inside the time the programs may take");
            T.Check(d.Cycle(s, true, Now.AddSeconds(4) + DiagCommands.Longest), "diagnostics: given up after it");
            d.Stamp(s);
            e = DiagnosticsStatus.Find(s, r.RequestId);
            T.Check(e != null && e.State == "Failed" && !d.Busy, "diagnostics: told as failed");
        }

        static void Schedule()
        {
            // While the deployment runs the request file is read every minute, afterwards every 10 minutes.
            MemStore requests = new MemStore(), upload = new MemStore();
            AgentDiagnostics d = Make(requests, upload, new FakeRunner(), Logs, null, new List<string>());
            Session s = NewSession();
            for (int sec = 0; sec < 300; sec += 2) d.Cycle(s, true, Now.AddSeconds(sec));
            T.Eq(5, requests.Gets, "diagnostics: one read a minute while deploying");
            requests = new MemStore();
            d = Make(requests, upload, new FakeRunner(), Logs, null, new List<string>());
            for (int sec = 0; sec < 1800; sec += 15) d.Cycle(s, false, Now.AddSeconds(sec));
            T.Eq(3, requests.Gets, "diagnostics: one read every 10 minutes after");
        }
    }
}
