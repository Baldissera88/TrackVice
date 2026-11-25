namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Windows.Forms;

    // Settings > Intune / cloud > Expected apps. Two sources, each its own switch.
    public static class ExpectedPrefs
    {
        public const string IntuneKey = "Expected.Intune", HistoryKey = "Expected.History";

        // Needs an Intune sign-in for the customer; without one this source cannot be read, whatever the switch says.
        public static bool Intune
        {
            get { return Prefs.GetBool(IntuneKey, true); }
        }

        public static bool History
        {
            get { return Prefs.GetBool(HistoryKey, true); }
        }

        public static bool Any
        {
            get { return Intune || History; }
        }
    }

    public sealed partial class MainForm
    {
        // What Intune said about the device that is open, and when. Read again every few minutes while the deployment runs.
        ExpectedSet intuneExpected;
        Engine intuneExpectedFor;
        DateTime intuneExpectedUtc;
        bool intuneExpectedBusy, intuneExpectedUserSeen;
        string intuneExpectedNote;
        public static readonly TimeSpan ExpectedEvery = TimeSpan.FromMinutes(5);

        // The set the open device is shown with: Intune when it could be read, else the earlier deployments, else none.
        ExpectedSet ChooseExpected()
        {
            if (engine == null) return null;
            Session s = engine.S;
            if (engine.Mode == Modes.Demo)
            {
                DemoPlayer p = engine.Player;
                return ExpectedPrefs.Intune && p != null ? p.Scenario.Expected : null;
            }
            if (ExpectedPrefs.Intune && intuneExpectedFor == engine && intuneExpected != null && graph != null && graph.SignedIn) return intuneExpected;
            if (!ExpectedPrefs.History) return null;
            if (history == null) history = start.Shot == null ? DeploymentHistory.Load(DeploymentHistory.DefaultPath) : new DeploymentHistory();
            string customer = watchedCustomer ?? "";
            return ExpectedApps.FromHistory(history.Recent(customer, s.Kind, s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile")));
        }

        // Merges the expected apps into the session. True when what shows of them changed.
        bool RefreshExpectedApps()
        {
            if (engine == null) return false;
            try { return engine.SetExpected(ChooseExpected()); }
            catch (Exception) { return false; }
        }

        // Asks Intune for the apps assigned to the device, at most every ExpectedEvery while the deployment runs, and once more
        // when the user phase starts.
        void ReadExpectedFromIntune()
        {
            Engine e = engine;
            GraphClient g = graph;
            if (e == null || e.Mode == Modes.Demo || intuneExpectedBusy || !ExpectedPrefs.Intune || g == null || !g.SignedIn) return;
            Session s = e.S;
            if (s.Items.Count == 0) return;
            ExpectedQuery q = ExpectedQuery.Of(s);
            if (!q.Usable) return;
            bool first = intuneExpectedFor != e;
            bool userNow = ExpectedApps.UserStarted(s);
            bool due = first || DateTime.UtcNow - intuneExpectedUtc >= ExpectedEvery && TimeLeft.StillRuns(s) || userNow && !intuneExpectedUserSeen;
            if (!due) return;
            intuneExpectedBusy = true;
            intuneExpectedUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ExpectedSet set = null;
                string note = null;
                try { set = ExpectedIntune.Read(g, q, DateTime.UtcNow, out note); }
                catch (Exception ex) { note = ExpectedIntune.Problem(ex); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate { Safe.Run(delegate { ExpectedRead(e, set, note, userNow); }); });
                }
                catch (Exception) { }
            });
        }

        void ExpectedRead(Engine e, ExpectedSet set, string note, bool userSeen)
        {
            intuneExpectedBusy = false;
            if (e != engine) return;
            intuneExpectedFor = e;
            intuneExpectedUserSeen = userSeen;
            if (set != null)
            {
                intuneExpected = set;
                intuneExpectedNote = null;
                // A device that only knows an app by its ID shows the name Intune gave it.
                Dictionary<string, string> names = set.Names();
                if (names.Count > 0) e.AddNames(names);
            }
            else if (note != null && note != intuneExpectedNote)
            {
                intuneExpectedNote = note;
                Status(note + (ExpectedPrefs.History ? " Expected apps come from earlier deployments instead." : ""));
            }
            RefreshExpectedApps();
            Bind();
        }

        // Forgets what was read for another device or customer.
        void ForgetExpected()
        {
            intuneExpected = null;
            intuneExpectedFor = null;
            intuneExpectedNote = null;
            intuneExpectedUserSeen = false;
        }
    }
}
