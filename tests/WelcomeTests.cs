namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class WelcomeTests
    {
        public static void Run()
        {
            GateShowsOnAPlainStart();
            GateSkipsDeployingDevices();
            StepsTickFromFacts();
            WordsForOwnOrganization();
            BarHiddenWhenDoneOrHidden();
            SideOfSessions();
            SideOfKinds();
            FilterKeepsKeysInStep();
            MenuKeepsEveryEntry();
            MenuShowsOnlyOneSide();
            EmptyStates();
            DefaultSide();
            DemoUploadsSplit();
        }

        // The demo uploads: three Autopilot devices and two task sequences, one of them with SCCM apps after it.
        static void DemoUploadsSplit()
        {
            List<Session> all = new AgentCatalog(new DemoAgentStore()).LoadAll(DateTime.MinValue, 5000, null);
            int n = all.Count;
            int sccm = DeviceSides.Filter(all, DeviceSides.Sccm).Count, intune = DeviceSides.Filter(all, DeviceSides.Intune).Count;
            T.Check(sccm > 0 && intune > 0, "the demo has both sides");
            T.Eq(n, sccm + intune, "each demo upload is on one side");
            T.Eq(sccm, FleetStats.Build(DeviceSides.Filter(all, DeviceSides.Sccm), DateTime.UtcNow).Runs.Count, "statistics of one side");
        }

        static UpdateContext Window()
        {
            UpdateContext c = new UpdateContext();
            c.Window = true;
            return c;
        }

        static void GateShowsOnAPlainStart()
        {
            T.Eq<string>(null, WelcomeGate.Reason(Window(), false, false), "welcome on a plain first start");
            T.Check(WelcomeGate.Reason(Window(), false, true) != null, "not again once seen");
            T.Check(WelcomeGate.Reason(Window(), true, false) != null, "not when started with --demo, a file or the wall");
            T.Check(WelcomeGate.Reason(new UpdateContext(), false, false) != null, "not without a window");
            T.Check(WelcomeGate.Reason(null, false, false) != null, "not without a context");
        }

        static void GateSkipsDeployingDevices()
        {
            UpdateContext c = Window();
            c.Oobe = true;
            T.Check(WelcomeGate.Reason(c, false, false) != null, "not in OOBE or the ESP");
            c = Window();
            c.System = true;
            T.Check(WelcomeGate.Reason(c, false, false) != null, "not as SYSTEM");
            c = Window();
            c.TaskSequence = true;
            T.Check(WelcomeGate.Reason(c, false, false) != null, "not in a task sequence");
            c = Window();
            c.WinPE = true;
            T.Check(WelcomeGate.Reason(c, false, false) != null, "not in Windows PE");
            c = Window();
            c.Deploying = true;
            T.Check(WelcomeGate.Reason(c, false, false) != null, "not while this device deploys");
            c = Window();
            c.Policy = true;
            T.Eq<string>(null, WelcomeGate.Reason(c, false, false), "the update policy does not hide the welcome");

            // The same registry values the update check reads.
            MemoryRegistry reg = new MemoryRegistry();
            reg.Dword(@"HKLM\SYSTEM\Setup", "OOBEInProgress", 1);
            c = Window();
            UpdateGate.ReadDevice(c, reg, "CONTOSO\\megan", false, false);
            T.Check(WelcomeGate.Reason(c, false, false) != null, "OOBEInProgress from the registry");
            c = Window();
            UpdateGate.ReadDevice(c, new MemoryRegistry(), "defaultuser0", false, false);
            T.Check(WelcomeGate.Reason(c, false, false) != null, "defaultuser0 is OOBE");
            c = Window();
            UpdateGate.ReadDevice(c, new MemoryRegistry(), "CONTOSO\\megan", false, false);
            T.Eq<string>(null, WelcomeGate.Reason(c, false, false), "a plain desktop");
        }

        static void StepsTickFromFacts()
        {
            List<SetupStep> s = GettingStarted.Steps(GettingStarted.Customers, new FixedSetupFacts(false, false, false, false));
            T.Eq(4, s.Count, "four steps");
            T.Eq(0, GettingStarted.DoneCount(s), "nothing done");
            T.Eq(GettingStarted.Storage, GettingStarted.Next(s).Id, "Your storage first");
            T.Eq("Add the first customer", s[1].Title, "customer words");
            s = GettingStarted.Steps(GettingStarted.Customers, new FixedSetupFacts(true, true, false, true));
            T.Eq(3, GettingStarted.DoneCount(s), "three done");
            T.Eq(GettingStarted.Test, GettingStarted.Next(s).Id, "the test is what is left, though the list was opened");
            T.Check(!GettingStarted.AllDone(s), "not all done");
            s = GettingStarted.Steps(GettingStarted.Customers, new FixedSetupFacts(true, true, true, true));
            T.Check(GettingStarted.AllDone(s), "all done");
            T.Check(GettingStarted.Next(s) == null, "no next step");
        }

        static void WordsForOwnOrganization()
        {
            List<SetupStep> own = GettingStarted.Steps(GettingStarted.Own, new FixedSetupFacts(false, false, false, false));
            List<SetupStep> cust = GettingStarted.Steps(GettingStarted.Customers, new FixedSetupFacts(false, false, false, false));
            T.Eq("Add your own tenant", own[1].Title, "own organization words");
            T.Check(own[0].Text != cust[0].Text, "Your storage explained for each");
            T.Check(own[0].Text.IndexOf("customer", StringComparison.OrdinalIgnoreCase) < 0, "no customers for your own organization");
            for (int i = 0; i < own.Count; i++) T.Eq(cust[i].Id, own[i].Id, "same steps " + i);
            foreach (SetupStep x in own)
            {
                T.Check(x.Text.IndexOf('!') < 0, "plain text " + x.Id);
                T.Check(!string.IsNullOrEmpty(x.Action), "an action " + x.Id);
            }
        }

        static void BarHiddenWhenDoneOrHidden()
        {
            List<SetupStep> part = GettingStarted.Steps(GettingStarted.Own, new FixedSetupFacts(true, false, false, false));
            List<SetupStep> done = GettingStarted.Steps(GettingStarted.Own, new FixedSetupFacts(true, true, true, true));
            T.Check(GettingStarted.ShowBar(GettingStarted.Own, false, part), "bar while setting up");
            T.Check(!GettingStarted.ShowBar(GettingStarted.Own, true, part), "bar hidden by the person");
            T.Check(!GettingStarted.ShowBar(GettingStarted.Own, false, done), "bar hidden when all are done");
            T.Check(!GettingStarted.ShowBar(null, false, part), "no bar before choosing to set up");
            T.Eq("Getting started: 1 of 4 done. Next: Add your own tenant.", GettingStarted.BarText(part), "bar text");
        }

        static Session S(params string[] phases)
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            int n = 0;
            foreach (string p in phases)
            {
                Item i = new Item();
                i.Key = "item" + (n++);
                i.Name = i.Key;
                i.PhaseKey = p;
                s.Items.Add(i);
            }
            return s;
        }

        static void SideOfSessions()
        {
            T.Eq(DeviceSides.Sccm, DeviceSides.Of(S(Phases.WinPE, Phases.TaskSequence)), "task sequence");
            T.Eq(DeviceSides.Sccm, DeviceSides.Of(S(Phases.SccmApps)), "SCCM apps");
            T.Eq(DeviceSides.Intune, DeviceSides.Of(S(Phases.DeviceSetup, Phases.IntuneApps)), "Autopilot");
            T.Eq(DeviceSides.Intune, DeviceSides.Of(S(Phases.Policies)), "MDM policies");
            T.Eq(DeviceSides.Both, DeviceSides.Of(S(Phases.TaskSequence, Phases.AccountSetup)), "task sequence then Autopilot");
            T.Eq<string>(null, DeviceSides.Of(S(Phases.Restarts)), "only restarts");
            T.Eq<string>(null, DeviceSides.Of(null), "no session");
        }

        static void SideOfKinds()
        {
            T.Eq(DeviceSides.Sccm, DeviceSides.OfKind("Task sequence"), "task sequence kind");
            T.Eq(DeviceSides.Intune, DeviceSides.OfKind("Autopilot"), "Autopilot kind");
            T.Eq(DeviceSides.Intune, DeviceSides.OfKind("Intune apps"), "Intune apps kind");
            T.Eq(DeviceSides.Both, DeviceSides.OfKind("Task sequence + Autopilot"), "both kinds");
            T.Eq(DeviceSides.Sccm, DeviceSides.OfKind("SCCM software"), "SCCM software");
            T.Eq<string>(null, DeviceSides.OfKind("Nothing yet"), "nothing yet");
            T.Check(DeviceSides.Matches(DeviceSides.Both, DeviceSides.Sccm), "both shows under SCCM");
            T.Check(DeviceSides.Matches(DeviceSides.Both, DeviceSides.Intune), "both shows under Intune");
            T.Check(!DeviceSides.Matches(DeviceSides.Intune, DeviceSides.Sccm), "Intune not under SCCM");
            T.Check(!DeviceSides.Matches(null, DeviceSides.Intune), "nothing yet only under both");
            T.Check(DeviceSides.Matches(null, DeviceSides.Both), "everything under both");
            T.Eq(DeviceSides.Sccm, DeviceSides.Parse("SCCM"), "parse sccm");
            T.Eq(DeviceSides.Intune, DeviceSides.Parse("intune"), "parse intune");
            T.Eq(DeviceSides.Both, DeviceSides.Parse("both"), "parse both");
            T.Eq<string>(null, DeviceSides.Parse("contoso"), "parse something else");
        }

        static void FilterKeepsKeysInStep()
        {
            List<Session> all = new List<Session> { S(Phases.TaskSequence), S(Phases.DeviceSetup), S(Phases.TaskSequence, Phases.DeviceSetup) };
            List<string> keys = new List<string> { "ts", "ap", "both" };
            List<Session> os = new List<Session>();
            List<string> ok = new List<string>();
            DeviceSides.Filter(all, keys, DeviceSides.Intune, os, ok);
            T.Eq(2, os.Count, "two for Intune");
            T.Eq("ap", ok[0], "keys follow 1");
            T.Eq("both", ok[1], "keys follow 2");
            T.Eq(2, DeviceSides.Filter(all, DeviceSides.Sccm).Count, "two for SCCM");
            T.Eq(3, DeviceSides.Filter(all, DeviceSides.Both).Count, "all for both");
        }

        static List<string> Ids(string side)
        {
            List<string> l = new List<string>();
            foreach (DeviceMenuEntry e in DeviceMenu.For(side)) if (!e.Separator) l.Add(e.Id);
            return l;
        }

        static void MenuKeepsEveryEntry()
        {
            List<string> intune = Ids(DeviceSides.Intune);
            List<string> sccm = Ids(DeviceSides.Sccm);
            foreach (string id in DeviceMenu.Before) T.Check(intune.Contains(id) || sccm.Contains(id), "still in the Devices menu: " + id);
            foreach (string id in new string[] { DeviceMenu.Stats, DeviceMenu.Wall, DeviceMenu.Compare, DeviceMenu.Reference, DeviceMenu.Watch })
            {
                T.Check(intune.Contains(id), "on the Intune side: " + id);
                T.Check(sccm.Contains(id), "on the SCCM side: " + id);
            }
        }

        static void MenuShowsOnlyOneSide()
        {
            List<string> intune = Ids(DeviceSides.Intune);
            List<string> sccm = Ids(DeviceSides.Sccm);
            T.Check(!intune.Contains(DeviceMenu.Sccm), "no task sequences on the Intune side");
            T.Check(!sccm.Contains(DeviceMenu.Intune), "no Autopilot on the SCCM side");
            T.Check(!sccm.Contains(DeviceMenu.AddCustomer), "Add customer is on the Intune side");
            T.Check(sccm.Contains(DeviceMenu.SmsProvider) && sccm.Contains(DeviceMenu.TsAgent), "SMS Provider and the task sequence agent on the SCCM side");
            T.Check(intune.Contains(DeviceMenu.Customers), "Customers on the Intune side");
            T.Eq(DeviceMenu.Intune, intune[0], "Intune side starts with Autopilot");
            T.Eq(DeviceMenu.Sccm, sccm[0], "SCCM side starts with task sequences");
        }

        static void EmptyStates()
        {
            SideFacts none = new SideFacts();
            string[] e = DeviceMenu.Empty(DeviceSides.Intune, none);
            T.Check(e != null && e.Length == 4, "Intune empty state");
            T.Eq(DeviceMenu.AddCustomer, e[2], "Intune empty state starts with Add customer");
            e = DeviceMenu.Empty(DeviceSides.Sccm, none);
            T.Check(e != null, "SCCM empty state");
            T.Eq(DeviceMenu.SmsProvider, e[2], "SCCM empty state asks for the SMS Provider");
            SideFacts f = new SideFacts();
            f.SignedIn = true;
            T.Check(DeviceMenu.Empty(DeviceSides.Intune, f) == null, "signed in is enough for Intune");
            T.Check(DeviceMenu.Empty(DeviceSides.Sccm, f) != null, "but not for SCCM");
            f.SccmServer = true;
            T.Check(DeviceMenu.Empty(DeviceSides.Sccm, f) == null, "a customer can have both");
        }

        static void DefaultSide()
        {
            SideFacts f = new SideFacts();
            T.Eq(DeviceSides.Intune, DeviceSides.Default(f), "Intune when nothing is set up");
            f.SccmServer = true;
            T.Eq(DeviceSides.Sccm, DeviceSides.Default(f), "SCCM when only a site is known");
            f.Storage = true;
            T.Eq(DeviceSides.Intune, DeviceSides.Default(f), "Intune when both are");
            T.Eq(DeviceSides.Sccm, DeviceSides.Remembered(DeviceSides.Sccm, f), "the last side is kept");
            T.Eq(DeviceSides.Intune, DeviceSides.Remembered("garbage", f), "a bad value falls back");
        }
    }
}
