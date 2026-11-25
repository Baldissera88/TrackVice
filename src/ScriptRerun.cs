namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    // A platform script that ran again after it had succeeded, and failed the second time.
    public static class ScriptRerun
    {
        static readonly Regex RxNetwork = new Regex(@"could not be resolved|name resolution|no such host is known|unable to connect|connection was closed|connection.{0,20}(forcibly )?closed|timed out|timeout|network is unreachable|no network|actively refused", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public const string NetworkWords = "The device had no network for a moment.";

        public static bool IsNetworkError(string text)
        {
            return !string.IsNullOrEmpty(text) && RxNetwork.IsMatch(text);
        }

        // The platform script TrackVice itself installs the agent with: by its name, or by what it wrote.
        public static bool IsAgentScript(Item i)
        {
            if (i == null || i.Kind != Kinds.Script) return false;
            string n = i.Name ?? "";
            if (n.StartsWith(AddCustomerRun.ScriptName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (TEvent e in i.Events)
            {
                string t = e.Text ?? "";
                if (t.StartsWith("Script output: TrackVice ", StringComparison.Ordinal) || t.IndexOf("The TrackVice agent was not installed", StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // The failed last attempt and the success before it, or null.
        public static Attempt Failed(Item i, out Attempt earlier)
        {
            earlier = null;
            if (i == null || i.Kind != Kinds.Script || i.Attempts.Count < 2) return null;
            Attempt last = i.Current;
            if (last.State != State.Failed) return null;
            for (int k = i.Attempts.Count - 2; k >= 0; k--)
                if (i.Attempts[k].State == State.Succeeded) { earlier = i.Attempts[k]; break; }
            return earlier == null ? null : last;
        }

        public static bool IsAgentRerun(Session s, Item i)
        {
            Attempt e;
            return s != null && s.Mode == Modes.Agent && Failed(i, out e) != null && IsAgentScript(i);
        }

        // In a session that comes from the agent, the agent's own script is done, whatever its later run said.
        public static void Settle(Session s)
        {
            if (s == null || s.Mode != Modes.Agent) return;
            foreach (Item i in s.Items)
            {
                Attempt e;
                Attempt last = Failed(i, out e);
                if (last == null || !IsAgentScript(i)) continue;
                if (i.State == State.Failed)
                {
                    i.State = State.Succeeded;
                    i.ExitCode = e.ExitCode ?? 0;
                    i.Percent = 100;
                }
            }
        }

        // The text the failed attempt left behind: its error, else the output after it started.
        public static string Reason(Item i, Attempt last)
        {
            string err = Explain.ScriptError(i);
            if (!string.IsNullOrEmpty(err)) return err;
            for (int k = i.Events.Count - 1; k >= 0; k--)
            {
                TEvent e = i.Events[k];
                if (e.TimeUtc < last.StartUtc) break;
                if (e.Severity >= 2 && !string.IsNullOrEmpty(e.Text)) return e.Text;
            }
            return null;
        }

        static string Who(Attempt a)
        {
            return a.Context == "User" ? "the user" : a.Context == "Device" ? "the device" : "the device or the user";
        }

        // "Succeeded at 19:40:21, failed when it ran again at 19:46:44 for the user." Null when there is no such pair.
        public static string Sentence(Item i)
        {
            Attempt e;
            Attempt last = Failed(i, out e);
            if (last == null) return null;
            return "Succeeded at " + Txt.LocalTime(e.EndUtc ?? e.StartUtc) + ", failed when it ran again at " + Txt.LocalTime(last.StartUtc) + " for " + Who(last) + ".";
        }

        // Added to the message of the Failed finding of any platform script.
        public static string Extra(Item i)
        {
            Attempt e;
            Attempt last = Failed(i, out e);
            string text = "";
            if (last != null) text += " " + Sentence(i);
            Attempt cur = i.Current;
            if (cur != null && cur.State == State.Failed && IsNetworkError(Reason(i, cur) ?? ""))
                text += " " + NetworkWords;
            return text;
        }

        static string Short(string s)
        {
            return Txt.Short(s ?? "", 200);
        }

        public static void Add(Session s, List<Finding> list)
        {
            foreach (Item i in s.Items)
            {
                Attempt e;
                Attempt last = Failed(i, out e);
                if (last == null) continue;
                string reason = Reason(i, last);
                if (s.Mode == Modes.Agent && IsAgentScript(i))
                {
                    string why = string.IsNullOrEmpty(reason) ? "no error text was seen" : Short(reason);
                    Finding f = Make(FindingKinds.ScriptRerun, 1, i, i.DisplayName + ": a later run failed",
                        "The agent is installed and running (this session comes from it). A later run of its script failed: " + why + ". Usually a short network outage, or the script also runs for the user."
                        + (IsNetworkError(reason) ? " " + NetworkWords : ""), null, last);
                    list.Add(f);
                }
                if (last.Context == "User")
                    list.Add(Make(FindingKinds.ScriptUsers, 1, i, i.DisplayName + " runs for users too",
                        Sentence(i) + " The script was assigned in a way that also runs it in the signed-in user's context.",
                        "Assign it only to the device group, or check 'Run this script using the logged on credentials'.", last));
            }
        }

        static Finding Make(string kind, int sev, Item i, string title, string msg, string hint, Attempt last)
        {
            Finding f = new Finding();
            f.Kind = kind;
            f.Severity = sev;
            f.ItemKey = i.Key;
            f.Title = title;
            f.Message = msg;
            f.Hint = hint;
            f.TimeUtc = last.EndUtc ?? last.StartUtc;
            f.Evidence = i.LastEvidence;
            return f;
        }
    }
}
