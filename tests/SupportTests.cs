namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public static class SupportTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

        sealed class FakeFacts : IFactSource
        {
            public readonly Dictionary<string, List<Dictionary<string, object>>> Rows = new Dictionary<string, List<Dictionary<string, object>>>();
            public readonly Dictionary<string, string> Certs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<NetLink> Net = new List<NetLink>();
            public string Fw = "UEFI";
            public bool WmiFails;

            public List<Dictionary<string, object>> Wmi(string scope, string wql)
            {
                if (WmiFails) throw new InvalidOperationException("WMI is not there");
                foreach (KeyValuePair<string, List<Dictionary<string, object>>> kv in Rows) if (wql.Contains(kv.Key)) return kv.Value;
                return new List<Dictionary<string, object>>();
            }

            public string CertSubject(string thumbprint)
            {
                string s;
                return Certs.TryGetValue(thumbprint, out s) ? s : null;
            }

            public long? FreeBytes { get { return 182L * 1073741824; } }
            public long? TotalBytes { get { return 237L * 1073741824; } }
            public List<NetLink> Links() { return Net; }
            public string Firmware { get { return Fw; } }
            public TimeZoneInfo Zone { get { return TimeZoneInfo.Utc; } }
        }

        sealed class FakeSccm : ISccmQuery
        {
            public List<Dictionary<string, object>> Query(string wql)
            {
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                if (!wql.Contains("SMS_R_System") || !wql.Contains("16777301")) return l;
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["LastLogonUserName"] = "anna.hansen";
                r["LastLogonUserDomain"] = "CONTOSO";
                l.Add(r);
                return l;
            }
        }

        static Dictionary<string, object> Row(params object[] kv)
        {
            Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < kv.Length; i += 2) r[(string)kv[i]] = kv[i + 1];
            return r;
        }

        static string Get(List<Pair> l, string key)
        {
            foreach (Pair p in l) if (p.Key == key) return p.Value;
            return null;
        }

        static Obs O(string key, string name, double min, State st, long? code)
        {
            Obs o = new Obs();
            o.TimeUtc = T0.AddMinutes(min);
            o.Source = "test";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = Kinds.Win32App;
            o.PhaseKey = Phases.DeviceSetup;
            o.State = st;
            o.ExitCode = code;
            o.Text = "at " + min;
            return o;
        }

        static Session Running()
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            s.SetInfo("Autopilot profile", "Contoso standard");
            Tracker t = new Tracker(s);
            t.ApplyAll(new List<Obs>
            {
                O("a", "Contoso Timesheet", 0, State.Installing, null), O("a", "Contoso Timesheet", 4, State.Succeeded, 0),
                O("b", "Contoso VPN Client", 5, State.Installing, null),
                O("c", "Contoso Print Client", 6, State.Pending, null),
            });
            return s;
        }

        public static void Run()
        {
            User();
            Device();
            Site();
            Left();
            Tickets();
            Links();
            Notes();
            Who();
        }

        static void User()
        {
            MemoryRegistry reg = new MemoryRegistry();
            FakeFacts src = new FakeFacts();
            string join = FactReader.JoinInfoKey + @"\A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";
            reg.Sz(join, "UserEmail", "anna.hansen@contoso.com");
            src.Certs["A1B2C3D4E5F60718293A4B5C6D7E8F9012345678"] = "CN=6F1D2C4A-8B3E-4D5F-9A1B-2C3D4E5F6A7B";
            string enr = AutopilotInfo.EnrollmentsKey + @"\{11111111-2222-3333-4444-555555555555}";
            reg.Sz(enr, "ProviderID", "MS DM Server");
            reg.Sz(enr, "UPN", "anna.hansen@contoso.com");
            reg.Sz(enr + @"\DMClient\MS DM Server", "EntDMID", "0B7E4F2D-1C9A-4E8B-A3D6-5F2E1C0B9A84");
            reg.Sz(FactReader.LogonUiKey, "LastLoggedOnUser", @"AzureAD\AnnaHansen");
            reg.Sz(FactReader.LogonUiKey, "LastLoggedOnDisplayName", "Anna Hansen");
            reg.Sz(FactReader.LogonUiKey, "LastLoggedOnUserSID", "S-1-12-1-1111-2222-3333-4444");
            List<Pair> u = FactReader.User(reg, src);
            T.Eq("anna.hansen@contoso.com", Get(u, DeviceFacts.User), "user: UPN from the Entra join");
            T.Eq("Anna Hansen", Get(u, DeviceFacts.UserName), "user: display name from the last sign-in");
            T.Eq("Entra join", Get(u, DeviceFacts.UserFrom), "user: where it came from");
            T.Eq("6f1d2c4a-8b3e-4d5f-9a1b-2c3d4e5f6a7b", Get(u, DeviceFacts.EntraId), "user: Entra device ID from the join certificate");
            T.Eq("0b7e4f2d-1c9a-4e8b-a3d6-5f2e1c0b9a84", Get(u, DeviceFacts.IntuneId), "user: Intune device ID from EntDMID");

            // Pre-provisioning joins with a placeholder user; the MDM enrollment or the assignment gives the real one.
            MemoryRegistry pp = new MemoryRegistry();
            pp.Sz(join, "UserEmail", "foouser@contoso.com");
            pp.Sz(AutopilotInfo.PolicyCacheKey, "PolicyJsonCache", "{\"CloudAssignedAadServerData\":\"{\\\"ZeroTouchConfig\\\":{\\\"CloudAssignedTenantUpn\\\":\\\"lars.berg@contoso.com\\\",\\\"ForcedEnrollment\\\":1}}\"}");
            pp.Sz(FactReader.LogonUiKey, "LastLoggedOnUser", @"defaultuser0");
            pp.Sz(FactReader.LogonUiKey, "LastLoggedOnDisplayName", "defaultuser0");
            List<Pair> p = FactReader.User(pp, src);
            T.Eq("lars.berg@contoso.com", Get(p, DeviceFacts.User), "user: the Autopilot assignment over a placeholder");
            T.Eq("Autopilot assignment", Get(p, DeviceFacts.UserFrom), "user: from the assignment");
            T.Check(Get(p, DeviceFacts.UserName) == null, "user: no display name of the OOBE account");

            // Someone else signed in: the display name is left out rather than shown for the wrong user.
            reg.Sz(FactReader.LogonUiKey, "LastLoggedOnUser", @"AzureAD\TechnicianOne");
            reg.Sz(FactReader.LogonUiKey, "LastLoggedOnDisplayName", "Technician One");
            T.Check(Get(FactReader.User(reg, src), DeviceFacts.UserName) == null, "user: another person's name is not used");
            T.Check(FactReader.SamePerson(@"CONTOSO\anna.hansen", "anna.hansen@contoso.com"), "same person: domain account");
            T.Check(FactReader.SamePerson("anna.hansen@contoso.com", "anna.hansen@contoso.com"), "same person: UPN");
            T.Check(FactReader.User(new MemoryRegistry(), src).Count == 0, "user: nothing to read gives nothing");
            T.Eq("6f1d2c4a-8b3e-4d5f-9a1b-2c3d4e5f6a7b", FactReader.DeviceIdOf("CN=6f1d2c4a-8b3e-4d5f-9a1b-2c3d4e5f6a7b"), "device ID from subject");
            T.Check(FactReader.DeviceIdOf("CN=CONTOSO-PC01") == null, "device ID: not a GUID");
        }

        static void Device()
        {
            MemoryRegistry reg = new MemoryRegistry();
            reg.Sz(FactReader.BiosKey, "SystemManufacturer", "Contoso Hardware");
            reg.Sz(FactReader.BiosKey, "SystemProductName", "ProDesk 600");
            reg.Dword(FactReader.SecureBootKey, "UEFISecureBootEnabled", 1);
            FakeFacts src = new FakeFacts();
            src.Rows["Win32_BIOS"] = new List<Dictionary<string, object>> { Row("SerialNumber", "CZC3214XV8 ") };
            src.Rows["Win32_Tpm"] = new List<Dictionary<string, object>> { Row("IsEnabled_InitialValue", true, "IsActivated_InitialValue", true, "SpecVersion", "2.0, 0, 1.59") };
            src.Net = new List<NetLink> { new NetLink("Wired", true, true), new NetLink("Wi-Fi", false, false) };
            List<Pair> d = FactReader.Device(reg, src, true);
            T.Eq("CZC3214XV8", Get(d, DeviceFacts.Serial), "facts: serial number");
            T.Eq("Contoso Hardware", Get(d, DeviceFacts.Maker), "facts: maker");
            T.Eq("ProDesk 600", Get(d, DeviceFacts.Model), "facts: model");
            T.Eq("Ready, version 2.0", Get(d, DeviceFacts.Tpm), "facts: TPM");
            T.Eq("On", Get(d, DeviceFacts.SecureBoot), "facts: Secure Boot");
            T.Eq("UEFI", Get(d, DeviceFacts.Bios), "facts: BIOS mode");
            T.Eq("182 GB free of 237 GB", Get(d, DeviceFacts.FreeDisk), "facts: free disk");
            T.Eq("Wired", Get(d, DeviceFacts.Network), "facts: network");
            T.Eq("UTC (UTC+00:00)", Get(d, DeviceFacts.TimeZone), "facts: time zone");

            // WMI that fails or is off leaves out what needs it, and the rest is still there.
            src.WmiFails = true;
            src.Fw = "BIOS";
            MemoryRegistry old = new MemoryRegistry();
            List<Pair> e = FactReader.Device(old, src, true);
            T.Check(Get(e, DeviceFacts.Serial) == null && Get(e, DeviceFacts.Tpm) == null, "facts: nothing from a failing WMI");
            T.Eq("Legacy BIOS", Get(e, DeviceFacts.Bios), "facts: legacy BIOS");
            T.Eq("Not available with legacy BIOS", Get(e, DeviceFacts.SecureBoot), "facts: no Secure Boot with BIOS");
            T.Check(Get(FactReader.Device(reg, src, false), DeviceFacts.Serial) == null, "facts: no WMI when it is off");

            T.Eq("Not found", FactReader.TpmText(new List<Dictionary<string, object>>()), "TPM: none");
            T.Eq("Present, turned off, version 1.2", FactReader.TpmText(new List<Dictionary<string, object>> { Row("IsEnabled_InitialValue", false, "SpecVersion", "1.2, 2, 3") }), "TPM: off");
            T.Eq("Wired and Wi-Fi", FactReader.NetworkOf(new List<NetLink> { new NetLink("Wired", true, true), new NetLink("Wi-Fi", true, true) }), "network: both");
            T.Eq("No network", FactReader.NetworkOf(new List<NetLink> { new NetLink("Wi-Fi", true, false) }), "network: none with a gateway");
            T.Eq("9.4 GB", FactReader.Size((long)(9.4 * 1073741824)), "size: GB with a decimal");
            T.Eq("512 MB", FactReader.Size(512L * 1048576), "size: MB");

            // The user is read again only when it changed; the device only on the first read.
            MemoryRegistry r2 = new MemoryRegistry();
            r2.Sz(FactReader.LogonUiKey, "LastLoggedOnUser", "anna.hansen@contoso.com");
            r2.Sz(FactReader.LogonUiKey, "LastLoggedOnUserSID", "S-1-12-1-1-2-3-4");
            FactWatch w = new FactWatch();
            w.Source = new FakeFacts();
            List<Pair> first = w.Read(r2, 1, false);
            T.Check(Get(first, DeviceFacts.User) == "anna.hansen@contoso.com" && Get(first, DeviceFacts.FreeDisk) != null, "watch: first read has user and device");
            T.Eq(0, w.Read(r2, 10, false).Count, "watch: nothing new");
            r2.Sz(FactReader.LogonUiKey, "LastLoggedOnUser", "lars.berg@contoso.com");
            T.Eq(0, w.Read(r2, 11, false).Count, "watch: not between the reads of the user");
            T.Eq("lars.berg@contoso.com", Get(w.Read(r2, 20, false), DeviceFacts.User), "watch: a new user");

            // Old sessions without facts still load, and the facts travel with the session.
            Session s = Running();
            foreach (Pair p in d) s.SetInfo(p.Key, p.Value);
            Session back = Session.FromJson(s.ToJson());
            T.Eq("CZC3214XV8", back.GetInfo(DeviceFacts.Serial), "facts: kept in the saved session");
            List<Pair> card = DeviceFacts.Card(back);
            T.Eq("Contoso Hardware ProDesk 600", Get(card, "Maker and model"), "card: maker and model");
            T.Check(DeviceFacts.Is("TPM") && !DeviceFacts.Is("Tenant"), "facts: keys");
        }

        static void Site()
        {
            T.Eq(@"CONTOSO\anna.hansen", SiteUser.Of(new FakeSccm(), 16777301), "SCCM: last sign-in from SMS_R_System");
            T.Check(SiteUser.Of(new FakeSccm(), 16777302) == null, "SCCM: nothing when the site has no user");
            Session s = new Session();
            s.SetInfo(DeviceFacts.SiteUser, @"CONTOSO\anna.hansen");
            T.Check(TrackVice.Who.Label(s) == null, "SCCM: the site's last sign-in is not taken as the enrolling user");
            T.Eq(@"CONTOSO\anna.hansen", Get(DeviceFacts.Card(s), "Last sign-in (site)"), "SCCM: shown as the site's last sign-in");
        }

        static Reference Ref(double minutes)
        {
            Reference r = new Reference();
            r.Computer = "CONTOSO-REF01";
            r.Profile = "Contoso standard";
            for (int i = 0; i < 3; i++)
            {
                ReferenceItem x = new ReferenceItem();
                x.Key = "k" + i;
                x.Offset = i * 60;
                x.Seconds = i == 2 ? minutes * 60 - 120 : 60;
                x.State = State.Succeeded.ToString();
                x.Kind = Kinds.Win32App;
                r.Items.Add(x);
            }
            return r;
        }

        static FleetRun Run(double minutes, string profile)
        {
            FleetRun r = new FleetRun();
            r.Computer = "CONTOSO-PC" + minutes;
            r.Profile = profile;
            r.Items = 5;
            r.Done = 5;
            r.Seconds = minutes * 60;
            return r;
        }

        static void Left()
        {
            Session s = Running();
            DateTime now = T0.AddMinutes(10);
            TimeLeftEstimate e = TimeLeft.Estimate(s, now, TimeSpan.Zero, Ref(35), null);
            T.Eq("about 25 min left", e.Text, "time left: from the reference");
            T.Check(e.Basis.Contains("CONTOSO-REF01"), "time left: says where it comes from");
            Reference other = Ref(35);
            other.Profile = "Kiosk";
            T.Check(TimeLeft.Estimate(s, now, TimeSpan.Zero, other, null) == null, "time left: a reference of another profile is not used");

            List<FleetRun> runs = new List<FleetRun> { Run(30, "Contoso standard"), Run(40, "Contoso standard"), Run(50, "Contoso standard"), Run(90, "Kiosk") };
            T.Eq("about 30 min left", TimeLeft.Estimate(s, now, TimeSpan.Zero, null, runs).Text, "time left: middle of the last 7 days");
            T.Check(TimeLeft.Estimate(s, now, TimeSpan.Zero, null, runs.GetRange(0, 2)) == null, "time left: too few enrollments");
            T.Eq("longer than usual", TimeLeft.Estimate(s, T0.AddMinutes(70), TimeSpan.Zero, Ref(35), null).Text, "time left: past the usual time");
            T.Eq("about 25 min left", TimeLeft.Estimate(s, T0.AddMinutes(20), TimeSpan.FromMinutes(10), Ref(35), null).Text, "time left: time not counted is left out");
            T.Check(TimeLeft.Estimate(s, T0.AddSeconds(30), TimeSpan.Zero, Ref(35), null) == null, "time left: not in the first minute");
            Session failed = Running();
            new Tracker(failed).ApplyAll(new List<Obs> { O("b", "Contoso VPN Client", 7, State.Failed, 1603), O("c", "Contoso Print Client", 8, State.Succeeded, 0) });
            T.Check(TimeLeft.Estimate(failed, now, TimeSpan.Zero, Ref(35), null) == null, "time left: nothing when it failed");

            T.Eq("about 1 min left", TimeLeft.Words(TimeSpan.FromSeconds(20)), "words: under a minute");
            T.Eq("about 7 min left", TimeLeft.Words(TimeSpan.FromMinutes(6.2)), "words: minutes");
            T.Eq("about 25 min left", TimeLeft.Words(TimeSpan.FromMinutes(23)), "words: rounded to 5");
            T.Eq("about 1 h 10 min left", TimeLeft.Words(TimeSpan.FromMinutes(71)), "words: hours");
            T.Eq("about 2 h left", TimeLeft.Words(TimeSpan.FromMinutes(119)), "words: whole hours");
        }

        static void Tickets()
        {
            Session s = Running();
            s.SetInfo(DeviceFacts.User, "anna.hansen@contoso.com");
            s.SetInfo(DeviceFacts.UserName, "Anna Hansen");
            s.SetInfo(DeviceFacts.Serial, "5CG2041KQZ");
            s.SetInfo(DeviceFacts.IntuneId, "0b7e4f2d-1c9a-4e8b-a3d6-5f2e1c0b9a84");
            new Tracker(s).ApplyAll(new List<Obs> { O("c", "Contoso Print Client", 7, State.Failed, 1603) });
            Item b = s.Find("b");
            b.Info.Add(new Pair("Command line", "setup.exe /quiet"));
            DateTime now = T0.AddMinutes(12);
            string t = Ticket.Text(s, now, TimeSpan.Zero, "about 25 min left", "Spare laptop, password=Hunter2x");
            T.Check(t.StartsWith("Device: CONTOSO-PC01, serial 5CG2041KQZ\r\n", StringComparison.Ordinal), "ticket: device first");
            T.Check(t.Contains("User: Anna Hansen (anna.hansen@contoso.com)"), "ticket: user");
            T.Check(t.Contains("Deployment: " + s.Kind + ", Contoso standard"), "ticket: deployment");
            T.Check(t.Contains("State: Installing, 1 of 3 done, 1 failed"), "ticket: state");
            T.Check(t.Contains("Failed: Contoso Print Client: exit code 1603"), "ticket: what failed and why");
            T.Check(t.Contains("Waits for: Contoso VPN Client (installing)"), "ticket: what it waits for");
            T.Check(t.Contains("Started: " + Txt.Local(T0) + ", running for 12 min"), "ticket: start and time");
            T.Check(t.Contains("Time left: about 25 min left"), "ticket: time left");
            T.Check(t.Contains("Intune device ID: 0b7e4f2d"), "ticket: Intune ID");
            T.Check(t.Contains("Note: Spare laptop") && !t.Contains("Hunter2x"), "ticket: the note, with the secret masked");
            T.Check(!t.Contains("Entra device ID"), "ticket: leaves out what is not known");
            T.Check(t.Split('\n').Length <= 12, "ticket: short");
        }

        static void Links()
        {
            T.Eq("https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/overview/mdmDeviceId/0b7e4f2d-1c9a-4e8b-a3d6-5f2e1c0b9a84",
                PortalLinks.Intune("{0B7E4F2D-1C9A-4E8B-A3D6-5F2E1C0B9A84}"), "link: Intune device page");
            T.Check(PortalLinks.Intune(null) == null && PortalLinks.Intune("not-a-guid") == null && PortalLinks.Intune(Guid.Empty.ToString()) == null, "link: none without a valid ID");
        }

        static void Notes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-notes-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "notes.txt");
            try
            {
                DeviceNotes n = new DeviceNotes(path);
                n.Set("CONTOSO-PC01", "For Anna\tin Finance.\r\nHand over Friday.");
                n.Set("CONTOSO-PC02", new string('x', 500));
                DeviceNotes again = new DeviceNotes(path);
                T.Eq("For Anna in Finance. Hand over Friday.", again.Get("contoso-pc01"), "notes: one line, kept, any case");
                T.Eq(DeviceNotes.MaxLength, again.Get("CONTOSO-PC02").Length, "notes: at most the length");
                again.Set("CONTOSO-PC02", "  ");
                T.Eq(1, new DeviceNotes(path).Count, "notes: an empty note removes it");
                T.Eq("", again.Get("CONTOSO-PC03"), "notes: none");
                DeviceNotes mem = new DeviceNotes(null);
                mem.Set("CONTOSO-PC09", "In memory");
                T.Eq("In memory", mem.Get("CONTOSO-PC09"), "notes: in memory without a path");

                // The search finds a device by its note and its user.
                DeviceNotes keep = DeviceNotes.Current;
                DeviceNotes.Current = again;
                RecentDevice r = new RecentDevice();
                r.Source = RecentSources.Agent;
                r.Device = "CONTOSO-PC01";
                r.Id = "sessions/contoso-pc01.json.gz";
                r.User = "Anna Hansen (anna.hansen@contoso.com)";
                T.Eq(1, DeviceSearch.Search("finance", new RecentDevice[] { r }, null, 10).Count, "search: by note");
                T.Eq(1, DeviceSearch.Search("anna.hansen", new RecentDevice[] { r }, null, 10).Count, "search: by user");
                DeviceNotes.Current = keep;
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        static void Who()
        {
            Session s = new Session();
            s.User = @"CONTOSO\admin";
            T.Check(TrackVice.Who.Label(s) == null, "who: the account TrackVice runs as is not the user");
            s.User = "anna.hansen@contoso.com";
            T.Eq("anna.hansen@contoso.com", TrackVice.Who.Label(s), "who: an older session with the user of Intune");
            s.SetInfo(DeviceFacts.UserName, "Anna Hansen");
            T.Eq("Anna Hansen (anna.hansen@contoso.com)", TrackVice.Who.Label(s), "who: name and UPN");
            T.Eq("Anna Hansen", TrackVice.Who.Short(s), "who: short");

            // The agent list shows the user without downloading the session.
            Dictionary<string, string> m = AgentData.Summary(s, T0, false);
            StoreEntry e = new StoreEntry();
            e.Name = "sessions/x.json.gz";
            foreach (KeyValuePair<string, string> kv in m) if (kv.Value != null) e.Meta[kv.Key] = kv.Value;
            T.Eq("Anna Hansen (anna.hansen@contoso.com)", AgentData.ToDeployment(e, T0).EnrollUser, "agent list: user from the upload's summary");
            T.Eq("Anna Hansen", WallModel.Tile(e, T0).User, "wall: user");
            T.Check(WallModel.Tile(e, T0).Matches("hansen"), "wall: search by user");
            StoreEntry old = new StoreEntry();
            old.Name = "sessions/y.json.gz";
            old.Meta["computer"] = "CONTOSO-PC02";
            T.Check(AgentData.ToDeployment(old, T0).EnrollUser == null, "agent list: an older upload has no user");
        }
    }
}
