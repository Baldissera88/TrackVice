namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    // Remediations (detection and remediation script pairs) the Intune Management Extension runs. A device knows
    // them by ID only, so they show with the start of their ID until their names are read from Intune.
    public static class RemediationKeys
    {
        public const string Kind = "Remediation";
        public const string Phase = "intune:remediations";
        public const string PhaseName = "Remediations";
        public const int PhaseOrder = 93;

        public static string Key(string id)
        {
            return "remediation:" + (id ?? "").Trim('{', '}').ToLowerInvariant();
        }

        public static string Name(string id)
        {
            return "Remediation " + (id.Length >= 8 ? id.Substring(0, 8).ToLowerInvariant() : id);
        }

        public static Obs New(DateTime timeUtc, string source, string id)
        {
            Obs o = new Obs();
            o.TimeUtc = timeUtc;
            o.Source = source;
            o.ItemKey = Key(id);
            o.ItemName = Name(id);
            o.WeakName = true;
            o.ItemKind = Kind;
            o.Origin = Origins.Intune;
            o.PhaseKey = Phase;
            o.PhaseName = PhaseName;
            o.PhaseOrder = PhaseOrder;
            o.AddInfo("ID", id.Trim('{', '}').ToLowerInvariant());
            return o;
        }

        // The status ImeCollector.RemediationStatus gives, as a state. Finding an issue is a result, not a failure.
        public static State? Map(string status, bool failed)
        {
            if (failed) return State.Failed;
            switch (status)
            {
                case "No issue":
                case "Issue fixed":
                case "Issue found": return State.Succeeded;
                case "No result yet": return State.Pending;
                default: return null;
            }
        }
    }

    // SideCarPolicies\Scripts\Reports and \Execution, read with PolicyVice's IME reader.
    public sealed class RemediationWatch : RegistryWatch
    {
        protected override List<Obs> Snapshot(IRegSource reg, DateTime nowUtc)
        {
            List<Obs> list = new List<Obs>();
            foreach (ImeItem it in ImeCollector.Collect(reg))
            {
                if (it.Kind != ImeKinds.Remediation || string.IsNullOrEmpty(it.Id)) continue;
                DateTime? when = it.Time;
                if (!when.HasValue && !string.IsNullOrEmpty(it.RegistryPath)) when = reg.WriteTime(it.RegistryPath);
                Obs o = RemediationKeys.New(when.HasValue && when.Value <= nowUtc.AddMinutes(1) ? when.Value : nowUtc, "IME registry", it.Id);
                o.State = RemediationKeys.Map(it.Status, it.Failed);
                o.AddInfo("Runs for", it.User);
                o.AddInfo("Result", it.Status);
                long? code = string.IsNullOrEmpty(it.ErrorCode) ? null : Errors.Parse(it.ErrorCode.Split(' ', ':')[0]);
                if (code.HasValue && code.Value != 0) o.ExitCode = code;
                o.Text = "IME registry: " + it.Status + (code.HasValue && code.Value != 0 ? ", " + Errors.Short(code) : "");
                if (o.State == State.Failed) o.Severity = 2;
                o.Evidence = new Evidence(EvidenceKinds.Registry, it.RegistryPath, 0, "Result");
                list.Add(o);
            }
            return list;
        }
    }

    // HealthScripts.log. The Intune Management Extension writes each result as "[HS] new result = " and a JSON object
    // with the same fields as the Result value in the registry. The format is not documented by Microsoft.
    public sealed class HealthScriptsParser : LogParser
    {
        const string ResultMark = "[HS] new result = {";
        static readonly Regex RxSchedule = new Regex(@"\[HS\] inspect \w+ schedule for policy \{?(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})", Ci);

        public override string Source
        {
            get { return "HealthScripts"; }
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            string text = l.Text ?? "";
            string json = ResultJson(text);
            if (json != null)
            {
                Dictionary<string, object> r = ImeJson.Parse(json) as Dictionary<string, object>;
                string id = IntuneKeys.Guid(Http.Str(r, "PolicyId"));
                if (id == null) return;
                Dictionary<string, object> info = ImeJson.Get(r, "Info") as Dictionary<string, object>;
                long? first = ImeJson.Long(ImeJson.Get(info, "FirstDetectExitCode"));
                long? remExit = ImeJson.Long(ImeJson.Get(info, "RemediationExitCode"));
                long? last = ImeJson.Long(ImeJson.Get(info, "LastDetectExitCode"));
                long? remStatus = ImeJson.Long(ImeJson.Get(r, "RemediationStatus"));
                bool remError = !string.IsNullOrEmpty(ImeJson.Text(ImeJson.Get(r, "RemediationScriptErrorDetails")));
                long err = ImeJson.Long(ImeJson.Get(r, "ErrorCode")) ?? 0;
                bool failed;
                string status = ImeCollector.RemediationStatus(first, remExit, last, remStatus, remError, err, out failed);
                Obs o = Copy(New(l), id);
                o.State = RemediationKeys.Map(status, failed);
                o.AddInfo("Result", status);
                if (first.HasValue) o.AddInfo("Detection exit code", first.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (err != 0) o.ExitCode = err;
                o.Text = "Remediation result: " + status.ToLowerInvariant() + (err != 0 ? ", " + Errors.Short(err) : "");
                if (failed) o.Severity = 2;
                output.Add(o);
                return;
            }
            Match m = RxSchedule.Match(text);
            if (m.Success)
            {
                Obs o = Copy(New(l), m.Groups["id"].Value);
                output.Add(o);
            }
        }

        // The JSON after the first "[HS] new result = " that has an end, up to the last } on its line. Found with one pass over the line,
        // so a line from an upload that repeats the start many times without an end costs no more than its length.
        public static string ResultJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int from = 0;
            while (from < text.Length)
            {
                int i = text.IndexOf(ResultMark, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                int start = i + ResultMark.Length - 1;
                int nl = text.IndexOf('\n', start);
                int end = nl < 0 ? text.Length : nl;
                int close = text.LastIndexOf('}', end - 1, end - start);
                if (close > start) return text.Substring(start, close - start + 1);
                // A later start on the same line has no end either: go on with the next line.
                if (nl < 0) return null;
                from = nl + 1;
            }
            return null;
        }

        static Obs Copy(Obs line, string id)
        {
            Obs o = RemediationKeys.New(line.TimeUtc, line.Source, id);
            o.Text = line.Text;
            o.Severity = line.Severity;
            o.Evidence = line.Evidence;
            return o;
        }
    }
}
