namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // A platform script that failed when it ran again after it had succeeded. All IDs and names are made up.
    public static class ScriptRerunTests
    {
        const string Zero = "00000000-0000-0000-0000-000000000000";
        const string UserId = "7c1d2e3f-4a5b-4c6d-8e9f-0a1b2c3d4e5f";
        const string Script = "4e8a1c3d-5b7f-4a9e-8c2d-1f3b5d7e9a01";
        const string Net = "Exception calling DownloadFile: The remote name could not be resolved: 'contoso.blob.core.windows.net'";
        static readonly DateTime T0 = new DateTime(2026, 11, 24, 19, 40, 0, DateTimeKind.Utc);

        static LogLine L(string text, double sec)
        {
            LogLine l = new LogLine();
            l.Text = text;
            l.Raw = text;
            l.Utc = T0.AddSeconds(sec);
            l.File = "x.log";
            l.LineNo = (int)sec;
            l.Thread = "7";
            return l;
        }

        // Run 1 for the device succeeds, run 2 for the user fails with the given error text.
        static Session Build(string name, string mode, string error, bool secondForUser)
        {
            ImeLogParser ime = new ImeLogParser("IntuneManagementExtension");
            List<Obs> o = new List<Obs>();
            ime.Read(L("[PowerShell] Processing policy with id = " + Script + " for user " + Zero, 1), o);
            ime.Read(L("[PowerShell] User Id = " + Zero + ", Policy id = " + Script + ", policy result = Success", 95), o);
            string u = secondForUser ? UserId : Zero;
            ime.Read(L("[PowerShell] Processing policy with id = " + Script + " for user " + u, 400), o);
            if (error != null)
            {
                Obs e = new Obs();
                e.TimeUtc = T0.AddSeconds(410);
                e.Source = "AgentExecutor";
                e.ItemKey = MdmWatch.ScriptKey(Script);
                e.ItemKind = Kinds.Script;
                e.PhaseKey = Phases.Scripts;
                e.Severity = 2;
                e.Text = "Script error: " + error;
                o.Add(e);
            }
            ime.Read(L("[PowerShell] User Id = " + u + ", Policy id = " + Script + ", policy result = Failed", 414), o);
            Session s = new Session();
            s.Mode = mode;
            Tracker t = new Tracker(s);
            t.ApplyAll(o);
            Item i = s.Find(MdmWatch.ScriptKey(Script));
            i.Name = name;
            Finder.Run(s, T0.AddMinutes(30), false);
            return s;
        }

        static Finding Find(Session s, string kind)
        {
            foreach (Finding f in s.Findings) if (f.Kind == kind) return f;
            return null;
        }

        public static void Run()
        {
            T.Check(ScriptRerun.IsNetworkError("The remote name could not be resolved: 'x.blob.core.windows.net'"), "name resolution");
            T.Check(ScriptRerun.IsNetworkError("Unable to connect to the remote server"), "unable to connect");
            T.Check(ScriptRerun.IsNetworkError("The underlying connection was closed"), "connection closed");
            T.Check(ScriptRerun.IsNetworkError("The operation has timed out"), "timed out");
            T.Check(!ScriptRerun.IsNetworkError("Cannot find path 'C:\\x'"), "a path error is no network error");

            // The agent's own script, in a session from the agent: done with a warning.
            Session a = Build("TrackVice agent", Modes.Agent, Net, true);
            Item ai = a.Find(MdmWatch.ScriptKey(Script));
            T.Eq(2, ai.Attempts.Count, "two attempts");
            T.Eq("User", ai.Current.Context, "the second run was for the user");
            T.Eq("Device", ai.Attempts[0].Context, "the first run was for the device");
            T.Eq(State.Succeeded, ai.State, "the agent script is done");
            T.Eq(0, a.FindPhase(Phases.Scripts).FailedCount, "it does not count as failed");
            T.Check(Find(a, FindingKinds.Failed) == null, "no Failed finding");
            Finding w = Find(a, FindingKinds.ScriptRerun);
            T.Check(w != null && w.Severity == 1, "a warning finding");
            T.Check(w != null && w.Message.StartsWith("The agent is installed and running (this session comes from it). A later run of its script failed: "), "the agent text");
            T.Check(w != null && w.Message.Contains("could not be resolved") && w.Message.Contains("Usually a short network outage, or the script also runs for the user."), "reason and cause");
            T.Check(w != null && w.Message.Contains("no network for a moment"), "plain words for the network error");
            T.Eq(State.Succeeded, a.FindPhase(Phases.Scripts).State, "the phase is not failed");
            T.Check(OverallProgress.Fraction(a, T0.AddMinutes(30), null) >= 1.0, "no cap from the failed run");

            // The same script in a session that is not from the agent stays failed.
            Session d = Build("TrackVice agent", Modes.Intune, Net, true);
            T.Eq(State.Failed, d.Find(MdmWatch.ScriptKey(Script)).State, "not from the agent: failed");

            // Any other script: failed, with the explanation and the finding about users.
            Session g = Build("Set time zone", Modes.Live, Net, true);
            T.Eq(State.Failed, g.Find(MdmWatch.ScriptKey(Script)).State, "a failed re-run stays failed");
            Finding f = Find(g, FindingKinds.Failed);
            T.Check(f != null && f.Message.Contains("Succeeded at ") && f.Message.Contains(", failed when it ran again at ") && f.Message.Contains(" for the user."), "succeeded then failed for the user");
            T.Check(f != null && f.Message.Contains("The device had no network for a moment."), "network error in plain words");
            Finding u = Find(g, FindingKinds.ScriptUsers);
            T.Check(u != null && u.Title == "Set time zone runs for users too", "runs for users too");
            T.Check(u != null && u.Hint == "Assign it only to the device group, or check 'Run this script using the logged on credentials'.", "its advice");

            // A re-run for the device: no users finding, and an error that is no network error.
            Session v = Build("Set time zone", Modes.Live, "Cannot find path 'C:\\x'", false);
            Finding fv = Find(v, FindingKinds.Failed);
            T.Check(fv != null && fv.Message.Contains(" for the device."), "for the device");
            T.Check(fv != null && !fv.Message.Contains("no network"), "no network words");
            T.Check(Find(v, FindingKinds.ScriptUsers) == null, "no users finding for a device run");
        }
    }
}
