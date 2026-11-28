namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The item details sort the words of a finding into what happened, the likely reason and what to do, without adding any.
    public static class DetailTextTests
    {
        public static void Run()
        {
            Hints();
            Messages();
            KindCodes();
            Attempts();
            Restarts();
        }

        static void Hints()
        {
            string why, todo;
            DetailText.SplitHint("A fatal error inside the installer. Add /l*v to the msiexec command line and look for \"return value 3\" in the MSI log.", out why, out todo);
            T.Eq("A fatal error inside the installer.", why, "the reason before the first step");
            T.Eq("Add /l*v to the msiexec command line and look for \"return value 3\" in the MSI log.", todo, "what to do from the first step on");

            DetailText.SplitHint("Check that the connector service runs. Its log is in Applications and Services Logs.", out why, out todo);
            T.Eq(null, why, "a hint that starts with a step has no reason");
            T.Eq("Check that the connector service runs. Its log is in Applications and Services Logs.", todo, "and is all what to do");

            DetailText.SplitHint("Another installation was running. The agent normally retries later.", out why, out todo);
            T.Eq("Another installation was running. The agent normally retries later.", why, "a hint without a step is all reason");
            T.Eq(null, todo, "and has nothing to do");

            DetailText.SplitHint("The reference was set 2026-11-20. If the differences are expected, use this enrollment as the reference.", out why, out todo);
            T.Eq("If the differences are expected, use this enrollment as the reference.", todo, "If ..., and a step is a step");

            DetailText.SplitHint("The clock is off. If the clock is off, sign-ins fail.", out why, out todo);
            T.Eq(null, todo, "sign-ins is not the step sign");

            DetailText.SplitHint("Free disk space. The content is unpacked before it installs.", out why, out todo);
            T.Eq(null, why, "Free disk space is a step");

            DetailText.SplitHint("LogVice 2.0 or later opens at the line. Check it.", out why, out todo);
            T.Eq("LogVice 2.0 or later opens at the line.", why, "a version number does not end a sentence");

            DetailText.SplitHint(null, out why, out todo);
            T.Check(why == null && todo == null, "no hint, nothing");
        }

        static void Messages()
        {
            string what, why;
            DetailText.SplitMessage("It failed with exit code 1603 (A fatal error occurred during installation).", out what, out why);
            T.Eq("It failed with exit code 1603 (A fatal error occurred during installation).", what, "one sentence stays whole");
            T.Eq(null, why, "and has no reason");

            DetailText.SplitMessage("A later run of its script failed: timeout. Usually a short network outage, or the script also runs for the user. The device had no network for a moment.", out what, out why);
            T.Eq("A later run of its script failed: timeout. The device had no network for a moment.", what, "what happened keeps the facts");
            T.Eq("Usually a short network outage, or the script also runs for the user.", why, "the usual reason moves to why");

            DetailText.SplitMessage("Usually this is fine. It ran.", out what, out why);
            T.Eq("Usually this is fine. It ran.", what, "the first sentence never moves");
        }

        static void KindCodes()
        {
            T.Eq("W32", DetailText.KindCode(TrackVice.Kinds.Win32App), "Win32 app tile");
            T.Eq("PS", DetailText.KindCode(TrackVice.Kinds.Script), "script tile");
            T.Eq("REM", DetailText.KindCode("Remediation"), "an unknown kind uses its first letters");
            T.Eq("?", DetailText.KindCode(null), "no kind");
        }

        static void Attempts()
        {
            T.Eq("#2 · 42 s · Failed, 1618", DetailText.AttemptLabel(2, TimeSpan.FromSeconds(42), false, "Failed", 1618, false), "a failed attempt");
            T.Eq("#3 · 55 s · Done, exit 0", DetailText.AttemptLabel(3, TimeSpan.FromSeconds(55), false, "Done", 0, false), "a good attempt");
            T.Eq("#1 · 4 min 10 s so far · Installing", DetailText.AttemptLabel(1, TimeSpan.FromSeconds(250), true, "Installing", null, false), "a running attempt");
            T.Eq("#1 · 1 min 00 s · Done, 3010, restart needed", DetailText.AttemptLabel(1, TimeSpan.FromMinutes(1), false, "Done", 3010, true), "a restart asked for");
        }

        static void Restarts()
        {
            DateTime a = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
            List<DateTime> r = new List<DateTime> { a.AddMinutes(5) };
            T.Check(DetailText.RestartBetween(a, a.AddMinutes(10), false, r), "a restart between two attempts");
            T.Check(!DetailText.RestartBetween(a.AddMinutes(6), a.AddMinutes(10), false, r), "no restart in this gap");
            T.Check(DetailText.RestartBetween(a.AddMinutes(6), a.AddMinutes(10), true, null), "the earlier attempt asked for one");
        }
    }
}
