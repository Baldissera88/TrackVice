namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class FleetTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Averages();
            FailRate();
            PoliciesLeftOut();
            MedianOdd();
            MedianEven();
            RunningNotFinished();
            Keys();
            Csv();
        }

        static Session S(string computer)
        {
            Session s = new Session();
            s.Computer = computer;
            s.ClockUtc = T0.AddHours(2);
            return s;
        }

        // An item that started at minute `from` and has run for `minutes`.
        static Item Add(Session s, string name, string kind, string phase, State st, int from, int minutes)
        {
            Item i = new Item();
            i.Key = name.ToLowerInvariant();
            i.Name = name;
            i.Kind = kind;
            i.PhaseKey = phase;
            i.State = st;
            i.FirstUtc = T0.AddMinutes(from);
            i.StartUtc = T0.AddMinutes(from);
            i.LastUtc = T0.AddMinutes(from + minutes);
            if (States.IsFinal(st)) i.EndUtc = T0.AddMinutes(from + minutes);
            s.Items.Add(i);
            return i;
        }

        static FleetItem Find(FleetStats st, string name)
        {
            foreach (FleetItem f in st.Items) if (f.Name == name) return f;
            return null;
        }

        static void Averages()
        {
            Session a = S("CONTOSO-PC01");
            Add(a, "Contoso VPN", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 0, 10);
            Session b = S("CONTOSO-PC02");
            Add(b, "Contoso VPN", Kinds.Win32App, Phases.DeviceSetup, State.Failed, 0, 20);
            Session c = S("CONTOSO-PC03");
            Add(c, "Contoso VPN", Kinds.Win32App, Phases.DeviceSetup, State.Installing, 0, 90);
            Session d = S("CONTOSO-PC04");
            Add(d, "Contoso VPN", Kinds.Win32App, Phases.DeviceSetup, State.NotApplicable, 0, 50);
            FleetStats st = FleetStats.Build(new Session[] { a, b, c, d }, T0.AddHours(3));
            FleetItem f = Find(st, "Contoso VPN");
            T.Eq(4, f.Devices, "fleet: devices");
            T.Eq(2, f.Timed, "fleet: only items that ran to an end are timed");
            T.Eq(900.0, f.AvgSeconds, "fleet: average over finished items");
            T.Eq(1200.0, f.MaxSeconds, "fleet: longest");
            T.Eq("CONTOSO-PC02", f.SlowestOn, "fleet: slowest on");
            T.Eq(1, f.Running, "fleet: running count");
        }

        static void FailRate()
        {
            List<Session> l = new List<Session>();
            State[] states = { State.Succeeded, State.Succeeded, State.Succeeded, State.Failed, State.Installing };
            for (int n = 0; n < states.Length; n++)
            {
                Session s = S("CONTOSO-PC1" + n);
                Add(s, "7-Zip", Kinds.Win32App, Phases.DeviceSetup, states[n], 0, 2);
                l.Add(s);
            }
            FleetItem f = Find(FleetStats.Build(l, T0.AddHours(3)), "7-Zip");
            T.Eq(5, f.Devices, "fleet: fail rate devices");
            T.Eq(1, f.Failed, "fleet: fail rate failed");
            T.Eq(25.0, f.FailRate, "fleet: fail rate leaves out devices still working on it");
            T.Eq(0.0, new FleetItem().FailRate, "fleet: fail rate of nothing");
        }

        static void PoliciesLeftOut()
        {
            Session s = S("CONTOSO-PC20");
            Add(s, "Contoso App", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 0, 5);
            Add(s, "BitLocker", Kinds.Policy, Phases.Policies, State.Succeeded, 0, 300);
            Add(s, "Firewall", Kinds.Policy, Phases.DeviceSetup, State.Succeeded, 0, 300);
            Add(s, "Wi-Fi Contoso", Kinds.Profile, Phases.DeviceSetup, State.Succeeded, 0, 300);
            FleetStats st = FleetStats.Build(new Session[] { s }, T0.AddHours(6));
            T.Eq(1, st.Items.Count, "fleet: policies and profiles are left out");
            T.Eq(1, st.Runs[0].Items, "fleet: run counts no policies");
            T.Eq(300.0, st.Runs[0].Seconds, "fleet: run time leaves out policies");
        }

        static FleetStats Totals(params int[] minutes)
        {
            List<Session> l = new List<Session>();
            for (int n = 0; n < minutes.Length; n++)
            {
                Session s = S("CONTOSO-PC3" + n);
                Add(s, "Step " + n, Kinds.Step, Phases.FullOs, State.Succeeded, 0, minutes[n]);
                l.Add(s);
            }
            return FleetStats.Build(l, T0.AddHours(6));
        }

        static void MedianOdd()
        {
            FleetStats st = Totals(30, 10, 50);
            T.Eq(3, st.Finished, "fleet: finished, odd");
            T.Eq(1800.0, st.MedianSeconds, "fleet: median, odd");
            T.Eq(1800.0, st.AvgSeconds, "fleet: average, odd");
            T.Eq(3000.0, st.LongestSeconds, "fleet: longest, odd");
        }

        static void MedianEven()
        {
            FleetStats st = Totals(40, 10, 20, 100);
            T.Eq(1800.0, st.MedianSeconds, "fleet: median, even");
            T.Eq(2550.0, st.AvgSeconds, "fleet: average, even");
            FleetStats none = Totals();
            T.Eq(0, none.Finished, "fleet: nothing finished");
            T.Eq(0.0, none.MedianSeconds, "fleet: no median without runs");
        }

        static void RunningNotFinished()
        {
            Session busy = S("CONTOSO-PC40");
            Add(busy, "Contoso CRM", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 0, 10);
            Add(busy, "Contoso Office", Kinds.Win32App, Phases.DeviceSetup, State.Downloading, 10, 30);
            Session waiting = S("CONTOSO-PC41");
            Add(waiting, "Contoso CRM", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 0, 10);
            Item p = Add(waiting, "Contoso Office", Kinds.Win32App, Phases.DeviceSetup, State.Pending, 0, 0);
            p.StartUtc = null;
            Session done = S("CONTOSO-PC42");
            Add(done, "Contoso CRM", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 0, 10);
            Add(done, "Contoso Office", Kinds.Win32App, Phases.DeviceSetup, State.Failed, 10, 20);
            FleetStats st = FleetStats.Build(new Session[] { busy, waiting, done }, T0.AddHours(3));
            T.Eq(3, st.Runs.Count, "fleet: runs");
            T.Eq(1, st.Finished, "fleet: running and waiting runs are not finished");
            T.Eq(1, st.WithFailures, "fleet: with failures");
            T.Eq(1800.0, st.AvgSeconds, "fleet: average over finished runs only");
            FleetRun b = null;
            foreach (FleetRun r in st.Runs) if (r.Computer == "CONTOSO-PC40") b = r;
            T.Check(b != null && !b.Finished && b.Running == 1, "fleet: busy run is running");
            T.Eq(7200.0, b.Seconds, "fleet: a running run lasts until the device clock");
            FleetItem crm = Find(st, "Contoso CRM");
            T.Eq(600.0, crm.AvgSeconds, "fleet: item timed on unfinished runs too");
        }

        static void Keys()
        {
            Session a = S("CONTOSO-PC50");
            Add(a, "Contoso App", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 0, 5);
            Session b = S("CONTOSO-PC51");
            Add(b, "Contoso App", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 30, 5);
            FleetStats st = FleetStats.Build(new Session[] { a, b }, new string[] { "agent/contoso-pc50.json.gz", "agent/contoso-pc51.json.gz" }, T0.AddHours(3));
            T.Eq("CONTOSO-PC51", st.Runs[0].Computer, "fleet: newest run first");
            T.Eq("agent/contoso-pc51.json.gz", st.Runs[0].Key, "fleet: run keeps its upload name");
            T.Eq("agent/contoso-pc50.json.gz", st.Runs[1].Key, "fleet: second run keeps its upload name");
        }

        static void Csv()
        {
            Session s = S("CONTOSO-PC60");
            Add(s, "Contoso \"Pro\", 2026", Kinds.Win32App, Phases.DeviceSetup, State.Succeeded, 0, 2);
            Add(s, "=cmd|' /C calc'!A0", Kinds.Win32App, Phases.DeviceSetup, State.Failed, 0, 1);
            Add(s, "Two\nlines", Kinds.Script, Phases.Scripts, State.Succeeded, 0, 1);
            s.Info.Add(new Pair("Autopilot profile", "Contoso; standard"));
            FleetStats st = FleetStats.Build(new Session[] { s }, T0.AddHours(3));
            string csv = st.ToCsv();
            string[] lines = csv.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            T.Eq(4, lines.Length, "csv: one line per item and a header");
            T.Check(lines[0].StartsWith("Name,Kind,Devices"), "csv: header");
            T.Check(csv.Contains("\"Contoso \"\"Pro\"\", 2026\",\"Win32 app\",1,1,0,0,120,120,\"CONTOSO-PC60\""), "csv: quotes doubled, comma kept inside quotes: " + csv);
            T.Check(csv.Contains("\"'=cmd|' /C calc'!A0\""), "csv: a formula is not run by a spreadsheet");
            T.Check(csv.Contains("\"Two lines\""), "csv: no line break inside a value");
            T.Check(csv.Contains(",100,"), "csv: fail rate");
            string runs = st.RunsToCsv();
            T.Check(runs.StartsWith("Device,Deployment,Profile,"), "csv: runs header");
            T.Check(runs.Contains("\"CONTOSO-PC60\""), "csv: run device");
            T.Check(runs.Contains("\"Contoso; standard\""), "csv: run profile");
            T.Check(runs.Contains("\"2026-11-02 08:00:00\""), "csv: run start in UTC");
        }
    }
}
