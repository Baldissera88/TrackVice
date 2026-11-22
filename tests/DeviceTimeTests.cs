namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The device's time zone and clock in its facts, the customer code's time zone setting, and the agent's steps for it.
    public static class DeviceTimeTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 18, 8, 40, 0, DateTimeKind.Utc);

        sealed class FakeHost : ITimeHost
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Runs = new List<string>();
            public int Exit;

            public object Get(string key, string name)
            {
                object v;
                return Values.TryGetValue(key + "|" + name, out v) ? v : null;
            }

            public void SetDword(string key, string name, int value) { Values[key + "|" + name] = value; }
            public void SetString(string key, string name, string value) { Values[key + "|" + name] = value; }

            public int Run(string exe, string args)
            {
                Runs.Add(exe + " " + args);
                return Exit;
            }
        }

        public static void Run()
        {
            Facts();
            Clock();
            Code();
            Steps();
        }

        static Session Deploying()
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            Obs o = new Obs();
            o.TimeUtc = T0;
            o.Source = "ESP";
            o.ItemKey = "app:contoso";
            o.ItemName = "Contoso VPN Client";
            o.ItemKind = Kinds.Win32App;
            o.PhaseKey = Phases.DeviceSetup;
            o.State = State.Installing;
            new Tracker(s).Apply(o);
            return s;
        }

        static void Facts()
        {
            ZoneHistory z = new ZoneHistory();
            z.Add(T0.AddMinutes(4), -60, 480);
            List<Pair> facts = DeviceClock.Facts(TimeZoneInfo.Utc, z.Changes, T0);
            T.Eq("UTC (UTC+00:00)", facts[0].Value, "Device time: the zone now");
            T.Eq("2026-11-18 08:44:00 UTC: UTC-08:00 to UTC+01:00", facts[1].Value, "Device time: the change of zone, from Pacific time");
            Session s = Deploying();
            foreach (Pair p in facts) s.SetInfo(p.Key, p.Value);
            List<DeviceClock.ZoneChange> ch = DeviceClock.Changes(s);
            T.Eq(1, ch.Count, "Device time: the change is read back from the session");
            T.Eq(T0.AddMinutes(4), ch.Count > 0 ? ch[0].Utc : DateTime.MinValue, "Device time: at its time");
            Finder.Run(s, T0.AddMinutes(10), true);
            Finding f = s.Findings.Find(delegate (Finding x) { return x.Kind == DeviceClock.ZoneKind; });
            T.Check(f != null && f.Title.Contains("from UTC-08:00 to UTC+01:00"), "Device time: a finding when the zone changed during the deployment");
            Session before = Deploying();
            before.SetInfo(DeviceClock.ChangesInfo, "2026-11-17 08:44:00 UTC: UTC-08:00 to UTC+01:00");
            Finder.Run(before, T0.AddMinutes(10), true);
            T.Check(!before.Findings.Exists(delegate (Finding x) { return x.Kind == DeviceClock.ZoneKind; }), "Device time: no finding for a change the day before");
            T.Check(DeviceClock.FactsOf(s).Exists(delegate (Pair p) { return p.Key == "Time zone"; }), "Device time: the Session tab shows the zone");
            T.Check(DeviceClock.IsFact(DeviceClock.OffsetInfo) && !DeviceClock.IsFact("Tenant"), "Device time: facts are told apart from the deployment's");
        }

        static void Clock()
        {
            DateTime sent = new DateTime(2026, 11, 18, 10, 5, 0, DateTimeKind.Utc);
            // The device is 3 min 10 s ahead: the storage answered at 10:01:51 by its own clock.
            TimeSpan? o = DeviceClock.Measure(sent, sent.AddSeconds(2), "Wed, 18 Nov 2026 10:01:51 GMT");
            T.Eq(TimeSpan.FromSeconds(190), o, "Clock: ahead by the Date header against the middle of the upload");
            T.Eq(null, DeviceClock.Measure(sent, sent.AddMinutes(2), "Wed, 18 Nov 2026 10:01:51 GMT"), "Clock: a slow upload says too little");
            T.Eq(null, DeviceClock.Measure(sent, sent.AddSeconds(1), "not a date"), "Clock: no measurement without a Date header");
            Session s = Deploying();
            DeviceClock.Note(s, o, sent);
            T.Eq("190", s.GetInfo(DeviceClock.OffsetInfo), "Clock: the offset in seconds in the facts");
            T.Check(s.GetInfo(DeviceClock.ClockInfo).StartsWith("3 min 10 s ahead (the device's time at its upload"), "Clock: says how it was measured");
            Finder.Run(s, sent, true);
            Finding f = s.Findings.Find(delegate (Finding x) { return x.Kind == DeviceClock.ClockKind; });
            T.Check(f != null && f.Title == "The clock of CONTOSO-PC01 is 3 min ahead", "Clock: a finding when it is more than 2 minutes off");
            Session ok = Deploying();
            DeviceClock.Note(ok, TimeSpan.FromSeconds(-70), sent);
            Finder.Run(ok, sent, true);
            T.Check(!ok.Findings.Exists(delegate (Finding x) { return x.Kind == DeviceClock.ClockKind; }), "Clock: no finding within 2 minutes");
            T.Check(ok.GetInfo(DeviceClock.ClockInfo).StartsWith("Right, within 2 min"), "Clock: right within 2 minutes");
            AzureStore a = new AzureStore("https://contosostore.blob.core.windows.net/tv-contoso?sp=cw&sig=abc");
            a.LastSentUtc = sent;
            a.LastReceivedUtc = sent.AddSeconds(2);
            a.LastDate = "Wed, 18 Nov 2026 10:01:51 GMT";
            Session viaStore = Deploying();
            DeviceClock.FromStore(a, viaStore);
            T.Eq("190", viaStore.GetInfo(DeviceClock.OffsetInfo), "Clock: measured from the last upload to Azure");
            T.Eq(null, a.LastDate, "Clock: each answer is used once");
            Session folder = Deploying();
            DeviceClock.FromStore(new FolderStore("/tmp/none"), folder);
            T.Eq(null, folder.GetInfo(DeviceClock.OffsetInfo), "Clock: a folder has no clock to compare with");
        }

        static void Code()
        {
            string key;
            using (System.Security.Cryptography.RSACryptoServiceProvider rsa = UpdateKeys.Create()) key = UpdateKeys.PublicKey(rsa);
            CustomerCode c = new CustomerCode();
            c.Name = "Contoso";
            c.Upload = "https://contosostore.blob.core.windows.net/tv-contoso?sp=cw&sig=abc";
            c.Agent = "https://contosostore.blob.core.windows.net/trackvice-agent?sp=r&sig=def";
            c.Key = key;
            string old = c.ToString();
            T.Check(!CustomerCode.Parse(old).AutoTimeZone, "Code: off by default");
            T.Check(!System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(old.Substring(4)))).Contains("\"tz\""), "Code: a code without the setting stays as before");
            c.AutoTimeZone = true;
            string on = c.ToString();
            T.Check(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(on.Substring(4)))).Contains("\"tz\":true"), "Code: the setting is tz in the JSON");
            T.Check(CustomerCode.Parse(on).AutoTimeZone, "Code: read back on");
            AgentOptions o = new AgentOptions();
            CustomerInstall.Apply(o, on);
            T.Check(o.AutoTimeZone, "Code: the agent install gets it from the code");
            AgentOptions o2 = new AgentOptions();
            CustomerInstall.Apply(o2, old);
            T.Check(!o2.AutoTimeZone, "Code: an old code leaves it off");
            T.Check(CustomerCodeChange.WithSettings(old, "", true).AutoTimeZone, "Code: Customer code turns it on");
            T.Check(!CustomerCodeChange.WithSettings(on, "", false).AutoTimeZone, "Code: and off");
            AgentOptions links = new AgentOptions();
            T.Check(!links.AutoTimeZone, "Code: the links script path leaves it off without its switch");
            T.Check(!AgentScriptTemplate.Links.Contains("auto-timezone"), "Code: the links script does not pass the switch");
        }

        static string Pad(string b)
        {
            b = b.Replace('-', '+').Replace('_', '/');
            while (b.Length % 4 != 0) b += "=";
            return b;
        }

        static void Steps()
        {
            FakeHost h = new FakeHost();
            h.Values[AutoTimeZone.ServiceKey + "|Start"] = 4;
            h.Values[AutoTimeZone.LocationKey + "|Value"] = "Deny";
            List<string> log = new List<string>();
            AutoTimeZone.Apply(h, log.Add);
            T.Eq(3, (int)h.Values[AutoTimeZone.ServiceKey + "|Start"], "Steps: tzautoupdate starts manually, 3");
            T.Eq("Allow", (string)h.Values[AutoTimeZone.LocationKey + "|Value"], "Steps: location is allowed");
            T.Eq(1, h.Runs.Count, "Steps: one time sync");
            T.Eq("w32tm.exe /resync", h.Runs.Count > 0 ? h.Runs[0] : null, "Steps: w32tm /resync");
            T.Eq(3, log.Count, "Steps: each step is logged");
            T.Check(log.Count == 3 && log[0].Contains("Start from 4 to 3") && log[1].Contains("from Deny to Allow"), "Steps: the log says what changed");
            FakeHost done = new FakeHost();
            done.Values[AutoTimeZone.ServiceKey + "|Start"] = 3;
            done.Values[AutoTimeZone.LocationKey + "|Value"] = "Allow";
            done.Exit = 1;
            List<string> log2 = new List<string>();
            AutoTimeZone.Apply(done, log2.Add);
            T.Eq(2, done.Values.Count, "Steps: nothing is changed when it is already on");
            T.Check(log2.Count == 3 && log2[0].Contains("already on") && log2[2].Contains("returned 1"), "Steps: the log says it was already on and what the sync returned");
            FakeHost none = new FakeHost();
            List<string> log3 = new List<string>();
            AutoTimeZone.Apply(none, log3.Add);
            T.Check(!none.Values.ContainsKey(AutoTimeZone.ServiceKey + "|Start"), "Steps: no service value is made where the service is missing");
        }
    }
}
