namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // When the installed agent removes itself, with made-up sessions and a fake clock.
    public static class AgentLifetimeTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);
        const string AppA = "7a1e0c52-0001-4c3e-9a11-5d2b7e9f0a01", AppB = "7a1e0c52-0002-4c3e-9a11-5d2b7e9f0a02", AppC = "7a1e0c52-0003-4c3e-9a11-5d2b7e9f0a03";

        sealed class CountingStore : ISessionStore
        {
            public int Puts;
            public string Title { get { return "test"; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { Puts++; }
            public List<StoreEntry> List(string prefix) { return new List<StoreEntry>(); }
            public byte[] Get(string name) { return null; }
        }

        public static void Run()
        {
            DoneThenRemoved();
            NewAfterDone();
            Resealed();
            HardLimit();
            OldRegistry();
            PausedUploads();
            Script();
        }

        static void Script()
        {
            ScriptValues v = new ScriptValues();
            v.MaxDays = 21;
            string s = AgentScript.Fill(AgentScriptTemplate.Links, v);
            T.Check(s.Contains("$MaxDays   = 21   # The agent removes itself"), "Lifetime: the script gets MaxDays");
            T.Check(s.Contains("$arguments += '/max-days', $MaxDays"), "Lifetime: the script passes /max-days");
        }

        static Obs App(string id, string phase, State state, DateTime t)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "ESP";
            o.ItemKey = IntuneKeys.App(id);
            o.ItemKind = Kinds.Win32App;
            o.PhaseKey = phase;
            o.State = state;
            return o;
        }

        static Session Done()
        {
            Session s = new Session();
            Tracker tr = new Tracker(s);
            tr.Apply(App(AppA, Phases.DeviceSetup, State.Installing, T0));
            tr.Apply(App(AppA, Phases.DeviceSetup, State.Succeeded, T0.AddMinutes(20)));
            return s;
        }

        static Session Technician()
        {
            Session s = Done();
            string msg = "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_WhiteGlove_Success', Value: 'true'.";
            new Tracker(s).ApplyAll(AutopilotEvents.Map(AutopilotEvents.ShellCore, 62407, new List<object>(new object[] { "CommercialOOBE_ESPProgress_WhiteGlove_Success", "true" }), msg, T0.AddMinutes(25), 62407));
            return s;
        }

        static void DoneThenRemoved()
        {
            Session s = Done();
            List<string> log = new List<string>();
            int saves = 0;
            AgentLifetime l = new AgentLifetime(T0, 48, 30);
            l.Log = delegate (string x) { log.Add(x); };
            l.Save = delegate { saves++; };
            T.Check(!l.Check(s, false, T0.AddMinutes(30)).Remove && !l.DoneUtc.HasValue, "Lifetime: not done while it is not idle");
            DateTime done = T0.AddMinutes(50);
            T.Check(!l.Check(s, true, done).Remove, "Lifetime: kept when the deployment is done");
            T.Eq((DateTime?)done, l.DoneUtc, "Lifetime: done is noted");
            T.Eq(1, saves, "Lifetime: done is saved");
            T.Check(log.Count == 2 && log[0].StartsWith("Deployment done, 1 items") && log[1].StartsWith("Counting 48 hours from "), "Lifetime: done and the count are logged: " + string.Join(" / ", log.ToArray()));
            T.Eq(done.AddHours(48), l.UntilUtc, "Lifetime: removal 48 hours after done");
            T.Check(!l.Check(s, true, done.AddHours(47)).Remove, "Lifetime: kept before KeepHours");
            T.Check(!l.Check(s, false, done.AddHours(49)).Remove, "Lifetime: kept after KeepHours while something just changed");
            LifetimeDecision d = l.Check(s, true, done.AddHours(48));
            T.Check(d.Remove && d.Reason.Contains("48 hours ago"), "Lifetime: removed KeepHours after done: " + d.Reason);
        }

        static void NewAfterDone()
        {
            Session s = Done();
            List<string> log = new List<string>();
            AgentLifetime l = new AgentLifetime(T0, 48, 30);
            l.Log = delegate (string x) { log.Add(x); };
            l.Check(s, true, T0.AddHours(1));
            Tracker tr = new Tracker(s);
            tr.Apply(App(AppB, Phases.IntuneApps, State.Installing, T0.AddHours(20)));
            T.Check(!l.Check(s, false, T0.AddHours(20)).Remove && !l.DoneUtc.HasValue, "Lifetime: a new item resets done");
            T.Check(log[log.Count - 1].StartsWith("Something new started after the deployment was done: something is running"), "Lifetime: the reset is logged: " + log[log.Count - 1]);
            tr.Apply(App(AppB, Phases.IntuneApps, State.Succeeded, T0.AddHours(21)));
            l.Check(s, true, T0.AddHours(22));
            T.Eq((DateTime?)T0.AddHours(22), l.DoneUtc, "Lifetime: done again when it is done again");
            T.Check(!l.Check(s, true, T0.AddHours(49)).Remove, "Lifetime: the first done no longer counts");
            T.Check(l.Check(s, true, T0.AddHours(70)).Remove, "Lifetime: removed KeepHours after the second done");

            // An item that is added already finished, as a restart is, also starts the count again.
            Session r = Done();
            AgentLifetime k = new AgentLifetime(T0, 48, 30);
            k.Check(r, true, T0.AddHours(1));
            new Tracker(r).Apply(App(AppC, Phases.IntuneApps, State.Succeeded, T0.AddHours(30)));
            k.Check(r, false, T0.AddHours(30));
            T.Check(!k.DoneUtc.HasValue, "Lifetime: a new finished item resets done");
        }

        static void Resealed()
        {
            Session s = Technician();
            List<string> log = new List<string>();
            AgentLifetime l = new AgentLifetime(T0, 48, 30);
            l.Log = delegate (string x) { log.Add(x); };
            // Done was noted during the technician phase, before the reseal was seen.
            l.DoneUtc = T0.AddMinutes(26);
            l.DoneItems = s.Items.Count;
            LifetimeDecision d = l.Check(s, true, T0.AddDays(5));
            T.Check(!d.Remove && l.Waiting, "Lifetime: a resealed device waits past KeepHours: " + d.Reason);
            T.Check(log.Count > 0 && log[0].Contains("Waiting for the user phase"), "Lifetime: the wait is logged");
            T.Check(!l.Check(s, true, T0.AddDays(6)).Remove, "Lifetime: still waiting a day later");

            Tracker tr = new Tracker(s);
            tr.Apply(App(AppB, Phases.AccountSetup, State.Installing, T0.AddDays(6)));
            T.Check(!l.Check(s, false, T0.AddDays(6).AddMinutes(1)).Remove && !l.Waiting && !l.DoneUtc.HasValue, "Lifetime: the user phase ends the wait and done counts again");
            tr.Apply(App(AppB, Phases.AccountSetup, State.Succeeded, T0.AddDays(6).AddMinutes(20)));
            DateTime done = T0.AddDays(6).AddMinutes(60);
            l.Check(s, true, done);
            T.Eq((DateTime?)done, l.DoneUtc, "Lifetime: done after the user phase");
            T.Check(!l.Check(s, true, done.AddHours(47)).Remove, "Lifetime: kept KeepHours after the user phase");
            T.Check(l.Check(s, true, done.AddHours(48)).Remove, "Lifetime: removed KeepHours after the user phase");
        }

        static void HardLimit()
        {
            Session s = Technician();
            AgentLifetime l = new AgentLifetime(T0, 48, 30);
            T.Check(!l.Check(s, true, T0.AddDays(29)).Remove && l.Waiting, "Lifetime: waiting on day 29");
            LifetimeDecision d = l.Check(s, true, T0.AddDays(30));
            T.Check(d.Remove && d.Reason.Contains("30 days"), "Lifetime: the hard limit removes it while it waits: " + d.Reason);

            Session busy = new Session();
            new Tracker(busy).Apply(App(AppA, Phases.DeviceSetup, State.Installing, T0));
            AgentLifetime b = new AgentLifetime(T0, 48, 10);
            T.Check(!b.Check(busy, false, T0.AddDays(9)).Remove, "Lifetime: a deployment that never ends is kept until the limit");
            T.Check(b.Check(busy, false, T0.AddDays(10)).Remove, "Lifetime: and removed at MaxDays");
        }

        static void OldRegistry()
        {
            Dictionary<string, string> reg = new Dictionary<string, string>();
            reg["KeepHours"] = "72";
            Func<string, string> get = delegate (string n) { string v; return reg.TryGetValue(n, out v) ? v : null; };
            AgentLifetime old = AgentLifetime.FromSettings(get, T0);
            T.Eq(72, old.KeepHours, "Lifetime: KeepHours from the registry");
            T.Eq(30, old.MaxDays, "Lifetime: an older registry without MaxDays uses 30");
            T.Check(!old.DoneUtc.HasValue, "Lifetime: not done without a saved time");
            T.Eq(T0.AddDays(30), old.LimitUtc, "Lifetime: the limit counts from the install");

            reg["MaxDays"] = "14";
            reg["DoneUtc"] = T0.AddHours(3).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            reg["DoneItems"] = "5";
            AgentLifetime now = AgentLifetime.FromSettings(get, T0);
            T.Eq(14, now.MaxDays, "Lifetime: MaxDays from the registry");
            T.Eq((DateTime?)T0.AddHours(3), now.DoneUtc, "Lifetime: a saved done time is kept after a new start");
            T.Eq(5, now.DoneItems, "Lifetime: with its item count");
            reg["DoneUtc"] = "";
            T.Check(!AgentLifetime.FromSettings(get, T0).DoneUtc.HasValue, "Lifetime: an empty done time is not done");
        }

        static void PausedUploads()
        {
            Session s = Done();
            CountingStore st = new CountingStore();
            AgentOptions o = new AgentOptions();
            Agent a = new Agent(st, "contoso-pc01", T0);
            a.Cycle(s, T0, o, false);
            a.Paused = true;
            T.Check(!a.Cycle(s, T0.AddMinutes(10), o, false), "Lifetime: no heartbeat upload while waiting");
            new Tracker(s).Apply(App(AppB, Phases.AccountSetup, State.Installing, T0.AddMinutes(11)));
            T.Check(a.Cycle(s, T0.AddMinutes(11), o, false), "Lifetime: a change is uploaded while waiting");
            T.Eq(2, st.Puts, "Lifetime: two uploads");
        }
    }
}
