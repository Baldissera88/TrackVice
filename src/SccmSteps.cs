namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    // Status messages with Step 0 and no group, which belong to a step without saying which: 11910 and any message
    // TrackVice does not know. They join the step with the same action name, or the task sequence itself.
    public static class SccmLoose
    {
        public const string BypassInfo = "Execution policy";
        public const string RunBypassInfo = "Execution policy Bypass in";

        public static bool Is(Dictionary<string, object> r)
        {
            int k = TsMessage.Of(r).Kind;
            return (k == TsMessage.Other || k == TsMessage.BypassWarning) && SccmCatalog.Int(r, "Step") == 0 && string.IsNullOrEmpty(SccmCatalog.Str(r, "GroupName"));
        }

        public static bool IsStepKind(int k)
        {
            return k == TsMessage.StepStarted || k == TsMessage.StepDone || k == TsMessage.StepFailed || k == TsMessage.StepSkipped;
        }

        // For each loose row, the first step row after it with the same action name. Each step takes one message of each ID,
        // so six warnings for six steps with the same name go to six steps.
        public static Dictionary<Dictionary<string, object>, Dictionary<string, object>> Match(List<Dictionary<string, object>> rows)
        {
            Dictionary<Dictionary<string, object>, Dictionary<string, object>> map = new Dictionary<Dictionary<string, object>, Dictionary<string, object>>();
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                Dictionary<string, object> r = rows[i];
                string action = SccmCatalog.Str(r, "ActionName");
                if (string.IsNullOrEmpty(action) || !Is(r)) continue;
                long msg = SccmCatalog.Int(r, "LastStatusMsgID");
                for (int j = i + 1; j < rows.Count; j++)
                {
                    Dictionary<string, object> s = rows[j];
                    if (!IsStepKind(TsMessage.Of(s).Kind) || Is(s)) continue;
                    if (!string.Equals(SccmCatalog.Str(s, "ActionName"), action, StringComparison.OrdinalIgnoreCase)) continue;
                    string key = msg + "|" + SccmCatalog.Int(s, "Step") + "|" + action;
                    if (!taken.Add(key)) continue;
                    map[r] = s;
                    break;
                }
            }
            return map;
        }

        public static void MarkBypass(Obs o, string text)
        {
            o.Severity = 1;
            o.AddInfo(BypassInfo, "Bypass");
            if (text == null || text.StartsWith("Status message ", StringComparison.Ordinal)) o.Text = BypassText;
        }

        public const string BypassText = "Runs PowerShell with execution policy Bypass. The task sequence warns that the scripts are then not checked.";

        // The observation for a loose row: an event on its step, or on the whole task sequence when no step matches.
        public static Obs Obs(Dictionary<string, object> r, Dictionary<string, object> step, DateTime t)
        {
            long msg = SccmCatalog.Int(r, "LastStatusMsgID");
            string name = SccmCatalog.Str(r, "LastStatusMsgName");
            string output = SccmCatalog.Str(r, "ActionOutput");
            string action = SccmCatalog.Str(r, "ActionName");
            bool bypass = TsMessage.Of(r).Kind == TsMessage.BypassWarning;
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "Status message";
            o.Text = !string.IsNullOrEmpty(name) ? name : bypass ? BypassText : "Status message " + msg + (string.IsNullOrEmpty(action) ? "" : " for " + action);
            o.Evidence = new Evidence(EvidenceKinds.StatusMessage, "SMS_TaskSequenceExecutionStatus, message " + msg, (int)Math.Min(int.MaxValue, msg), (name ?? ("Status message " + msg)) + (string.IsNullOrEmpty(output) ? "" : "\r\n" + output));
            o.ItemKind = Kinds.Step;
            o.Origin = Origins.Sccm;
            if (bypass) o.Severity = 1;
            if (step != null)
            {
                long n = SccmCatalog.Int(step, "Step");
                string group = SccmCatalog.Str(step, "GroupName");
                o.ItemKey = "ts:" + n.ToString(CultureInfo.InvariantCulture) + ":" + action;
                o.ItemName = action;
                SetPhase(o, group, n);
                if (bypass) o.AddInfo(BypassInfo, "Bypass");
            }
            else
            {
                o.ItemKey = SccmFeed.RunKey;
                o.ItemName = "Whole task sequence";
                o.PhaseKey = Phases.TaskSequence;
                if (bypass && !string.IsNullOrEmpty(action)) o.AddInfo(RunBypassInfo, action);
            }
            return o;
        }

        public static void SetPhase(Obs o, string group, long step)
        {
            if (!string.IsNullOrEmpty(group))
            {
                o.PhaseKey = "ts:group:" + group.ToLowerInvariant();
                o.PhaseName = group;
                o.PhaseOrder = 100 + (int)Math.Min(10000, step);
            }
            else o.PhaseKey = Phases.TaskSequence;
        }

        // A mild finding for the steps that run PowerShell with execution policy Bypass. It is not a failure.
        public const string FindingKind = "PowerShell Bypass";

        public static void Apply(Session s, List<Finding> list)
        {
            List<string> names = new List<string>();
            Item first = null;
            foreach (Item i in s.Items)
            {
                string n = i.Get(BypassInfo) == "Bypass" ? i.DisplayName : i.Get(RunBypassInfo);
                if (n == null) continue;
                if (first == null) first = i;
                if (!names.Contains(n)) names.Add(n);
            }
            if (first == null) return;
            Finding f = new Finding();
            f.Kind = FindingKind;
            f.Severity = 0;
            f.ItemKey = first.Key == SccmFeed.RunKey ? null : first.Key;
            f.Title = "Runs PowerShell with execution policy Bypass";
            f.Message = (names.Count == 1 ? "The step " : names.Count + " steps ") + string.Join(", ", names.ToArray()) + (names.Count == 1 ? " runs" : " run")
                + " PowerShell with execution policy Bypass. The site logs a warning (11910) for each, because the scripts are then not checked before they run.";
            f.Hint = "Sign the scripts and use AllSigned, or keep Bypass only for scripts in content that only administrators can change.";
            f.TimeUtc = first.FirstUtc;
            for (int k = first.Events.Count - 1; k >= 0; k--)
                if (first.Events[k].Severity == 1 && first.Events[k].Evidence != null) { f.Evidence = first.Events[k].Evidence; break; }
            list.Add(f);
        }
    }

    // The applications an Install Application step installs from a task sequence variable list. Each sends 11904 when it
    // installed or 11905 when it failed, with Step 0, no group, and the application's name and ID in ActionOutput.
    public static class SccmApps
    {
        public const long Installed = 11904, Failed = 11905;
        static readonly Regex AppRx = new Regex(@"^(?<n>.*?)\s*\(?(?<id>ScopeId_[^/\s()]+/Application_[0-9A-Fa-f-]+)\)?", RegexOptions.CultureInvariant);
        static readonly Regex NameRx = new Regex(@"application (?<n>.*?)\s*\((?<id>ScopeId_[^/\s()]+/Application_[0-9A-Fa-f-]+)\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static bool Is(int kind)
        {
            return kind == TsMessage.AppInstalled || kind == TsMessage.AppFailed;
        }

        // The application's name and ID, from ActionOutput or from the message text when the site fills it in.
        public static bool Parse(Dictionary<string, object> r, out string name, out string id)
        {
            name = null;
            id = null;
            // The texts come from the device, and the patterns try every position: the name and ID are at their start.
            Match m = AppRx.Match(Head((SccmCatalog.Str(r, "ActionOutput") ?? "").Trim()));
            if (!m.Success) m = NameRx.Match(Head(SccmCatalog.Str(r, "LastStatusMsgName") ?? ""));
            if (!m.Success) return false;
            id = m.Groups["id"].Value;
            name = m.Groups["n"].Value.Trim();
            if (name.Length == 0) name = id;
            return true;
        }

        const int MaxHead = 4096;

        static string Head(string s)
        {
            return s.Length > MaxHead ? s.Substring(0, MaxHead) : s;
        }

        public static string Key(string id)
        {
            return "ts:app:" + id.ToLowerInvariant();
        }

        // The row of the step that installs the applications: the first step row with the same action name.
        static Dictionary<string, object> StepRow(List<Dictionary<string, object>> rows, string action)
        {
            foreach (Dictionary<string, object> s in rows)
            {
                if (!SccmLoose.IsStepKind(TsMessage.Of(s).Kind) || SccmLoose.Is(s)) continue;
                if (string.Equals(SccmCatalog.Str(s, "ActionName"), action, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        // Adds the item of one application, and the progress of its step while the step runs.
        public static void Add(List<Dictionary<string, object>> rows, Dictionary<string, object> r, bool ended, Batch b)
        {
            string name, id;
            DateTime t = SccmCatalog.Time(r) ?? DateTime.UtcNow;
            string action = SccmCatalog.Str(r, "ActionName") ?? "";
            long msg = SccmCatalog.Int(r, "LastStatusMsgID");
            string msgName = SccmCatalog.Str(r, "LastStatusMsgName") ?? ("Status message " + msg);
            string output = SccmCatalog.Str(r, "ActionOutput");
            bool failed = TsMessage.Of(r).Kind == TsMessage.AppFailed;
            Evidence ev = new Evidence(EvidenceKinds.StatusMessage, "SMS_TaskSequenceExecutionStatus, message " + msg, (int)Math.Min(int.MaxValue, msg), msgName + (string.IsNullOrEmpty(output) ? "" : "\r\n" + output));
            Dictionary<string, object> step = StepRow(rows, action);
            if (!Parse(r, out name, out id))
            {
                Obs plain = SccmLoose.Obs(r, step, t);
                plain.Evidence = ev;
                if (failed) plain.Severity = 2;
                b.Obs.Add(plain);
                return;
            }
            long stepNo = step != null ? SccmCatalog.Int(step, "Step") : 0;
            string group = step != null ? SccmCatalog.Str(step, "GroupName") : null;

            // An application starts when the one before it in the same step ended, or when the step started.
            DateTime? from = null;
            int done = 0;
            foreach (Dictionary<string, object> x in rows)
            {
                DateTime? xt = SccmCatalog.Time(x);
                if (!xt.HasValue || xt.Value > t || x == r) continue;
                int k = TsMessage.Of(x).Kind;
                if (!string.Equals(SccmCatalog.Str(x, "ActionName"), action, StringComparison.OrdinalIgnoreCase)) continue;
                if (Is(k)) { done++; if (!from.HasValue || xt.Value > from.Value) from = xt; }
                else if (k == TsMessage.StepStarted && !SccmLoose.Is(x) && (!from.HasValue || xt.Value > from.Value)) from = xt;
            }
            done++;

            string key = Key(id);
            if (from.HasValue)
            {
                Obs s = New(key, name, group, stepNo, from.Value);
                s.State = State.Installing;
                s.NewAttempt = true;
                b.Obs.Add(s);
            }
            long exit = SccmCatalog.Int(r, "ExitCode");
            long? code = exit == 0 ? (long?)null : unchecked((int)(uint)exit);
            Obs o = New(key, name, group, stepNo, t);
            o.Text = failed ? name + " failed to install" + (code.HasValue ? " (" + Errors.Short(code) + ")" : "") : name + " installed";
            o.Evidence = ev;
            o.AddInfo("Application ID", id);
            if (!string.IsNullOrEmpty(action)) o.AddInfo("Installed by step", action);
            if (failed) { o.State = State.Failed; o.ExitCode = code; o.Severity = 2; }
            else
            {
                o.State = State.Succeeded;
                o.ExitCode = exit;
                if (exit == 3010 || exit == 1641) o.Reboot = true;
            }
            b.Obs.Add(o);

            if (step == null) return;
            // The step says how far it has come while it runs. Which application is next is not reported, only each one that ended.
            bool stepEnded = ended;
            foreach (Dictionary<string, object> x in rows)
            {
                int k = TsMessage.Of(x).Kind;
                if ((k == TsMessage.StepDone || k == TsMessage.StepFailed || k == TsMessage.StepSkipped) && !SccmLoose.Is(x)
                    && string.Equals(SccmCatalog.Str(x, "ActionName"), action, StringComparison.OrdinalIgnoreCase)) stepEnded = true;
            }
            Obs p = new Obs();
            p.TimeUtc = t;
            p.Source = "Status message";
            p.ItemKey = "ts:" + stepNo.ToString(CultureInfo.InvariantCulture) + ":" + action;
            p.ItemName = action;
            p.ItemKind = Kinds.Step;
            p.Origin = Origins.Sccm;
            SccmLoose.SetPhase(p, group, stepNo);
            string last = name + (failed ? " (failed)" : "");
            if (stepEnded) p.Text = done + " done, the last was " + last;
            else
            {
                p.Text = "Installing the next application. " + done + " done, the last was " + last;
                p.State = State.Installing;
            }
            b.Obs.Add(p);
        }

        static Obs New(string key, string name, string group, long step, DateTime t)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "Status message";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = Kinds.Application;
            o.Origin = Origins.Sccm;
            SccmLoose.SetPhase(o, group, step);
            return o;
        }
    }

    // The line in the window that follows an SCCM run when a newer run of the same device and deployment began.
    public static class NewerRunText
    {
        // "16:05" today, "16-11 12:00" on another day.
        public static string When(DateTime utc, DateTime nowUtc)
        {
            DateTime l = utc.ToLocalTime();
            return l.Date == nowUtc.ToLocalTime().Date ? l.ToString("HH:mm", CultureInfo.InvariantCulture) : l.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture);
        }

        public static string Line(DateTime startUtc, DateTime nowUtc)
        {
            return "A newer run started at " + When(startUtc, nowUtc) + ".";
        }
    }
}
