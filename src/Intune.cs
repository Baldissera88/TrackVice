namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text.RegularExpressions;

    public static class IntuneKeys
    {
        static readonly Regex RxGuid = new Regex(@"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.Compiled);

        public static string App(string id)
        {
            return "intune:" + (id ?? "").Trim('{', '}').ToLowerInvariant();
        }

        public static string Guid(string s)
        {
            Match m = RxGuid.Match(s ?? "");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }
    }

    // IntuneManagementExtension.log, AppWorkload.log and AgentExecutor.log: Win32 app downloads, installs and detection.
    // The IME log format is not documented and changes between versions, so every pattern is loose and a line it
    // does not know only ends up on the timeline of the app it names.
    public sealed class ImeLogParser : LogParser
    {
        static readonly Regex RxId = new Regex(@"(?:app with id|policy with id|app ids|app id|appid|application id|for app|of app|\(id =|id:|app:)\s*:?\s*\{?(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})", Ci);
        static readonly Regex RxDelta = new Regex("\"(?<f>EnforcementState|DetectionState|ApplicabilityState|ErrorCode)\"\\s*:\\s*\"(?<from>[^\"]*?)\\s*->\\s*(?<to>[^\"]*)\"", Ci);
        static readonly Regex RxDetect = new Regex(@"detection state:?\s*(?<d>NotDetected|Detected)|resulted in[^.]*?\b(?<d2>NotDetected|Detected)\b|detection (?:result|returned)[^.]*?\b(?<d3>NotDetected|Detected)\b", Ci);
        static readonly Regex RxExit = new Regex(@"(?:lpExitCode|exit code|exitcode)[:= ]+(?<c>-?\d+)", Ci);
        static readonly Regex RxNoMapping = new Regex(@"did NOT set mapping for lpExitCode", Ci);
        static readonly Regex RxBytes = new Regex(@"(?:BytesTransferred|BytesDownloaded|bytes downloaded)\D{0,20}(?<a>\d+)\D{1,40}(?:BytesTotal|total bytes|of)\D{0,20}(?<b>\d+)", Ci);
        static readonly Regex RxPercent = new Regex(@"download(?:ed|ing)?[^%]{0,60}?(?<p>\d{1,3}(?:\.\d+)?)\s?%", Ci);
        static readonly Regex RxDownload = new Regex(@"(?:Starting content download|start(?:ing|ed)? (?:to )?download|Downloading (?:app|content)|download started|DO job (?:created|started))", Ci);
        static readonly Regex RxDownloaded = new Regex(@"(?:download(?:ed)? (?:completed|finished|succeeded)|content (?:is )?cached|hash (?:validation|check) (?:passed|succeeded)|Content is downloaded)", Ci);
        static readonly Regex RxInstall = new Regex(@"(?:Launch(?:ing)? Win32AppInstaller|Start(?:ing)? (?:to )?(?:execute|install)|Executing install|ExecutionActionHandler\]?\s*Handler invoked|installer (?:process )?(?:started|launched))", Ci);
        static readonly Regex RxFailedDl = new Regex(@"(?:download(?:ing)? (?:failed|error)|failed to download)", Ci);
        static readonly Regex RxStillRunning = new Regex(@"(?:process (?:is )?still running|Timeout(?:ed)? waiting for (?:the )?(?:install|process)|install(?:ation)? timed out|exceeded (?:the )?(?:max(?:imum)?|install) (?:run )?time)", Ci);
        static readonly Regex RxCommand = new Regex(@"Command line:\s*(?<c>.+?)\s*$", Ci);
        static readonly Regex RxMinutes = new Regex(@"after (?<m>\d+) minutes", Ci);
        static readonly Regex RxIdPhrase = new Regex(@"\s*(?:for |of )?(?:app|policy|application)(?: with)? id:?\s*\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?[,.:]?", Ci);
        static readonly Regex RxNotApplicable = new Regex(@"(?:RequirementsNotMet|will be reported as not applicable|requirement rule[^.]*?(?:not met|NotApplicable)|Applicability\s*[:=]\s*NotApplicable)", Ci);

        readonly string source;
        readonly Dictionary<string, string> currentByThread = new Dictionary<string, string>();

        static readonly Regex RxNoise = new Regex(@"Not sending (?:ESP )?status update|Saved AppInstallStatusReport|Real time status is not reportable|Sending status to company portal|prior AppAuthority|\[GRSManager\]|\[ReevaluationScheduleManager\]|\[V3Processor\]|Found native machine|Failed to parse \w+TimeUTC|Saving DownloadTime|LogonUser failed|Skip PopToastNotification|Detection running|could not be determined|resulted in action status|\[EspAppLockInCalculator\]|Handler (?:invoked|completed)", Ci);
        // The delta as the agent writes it today: "EnforcementState":{"OldValue":"InProgress","NewValue":"Success"}.
        static readonly Regex RxDelta2 = new Regex("\"(?<f>EnforcementState|DetectionState|ApplicabilityState|DesiredState|ErrorCode|EnforcementErrorCode)\"\\s*:\\s*\\{\\s*\"OldValue\"\\s*:\\s*(?:null|\"(?<from>[^\"]*)\"|(?<fromn>-?\\d+))\\s*,\\s*\"NewValue\"\\s*:\\s*(?:null|\"(?<to>[^\"]*)\"|(?<ton>-?\\d+))\\s*\\}", Ci);
        readonly Dictionary<string, string> lastSaid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> commandNext = new Dictionary<string, string>();
        // A bare "id:" is only an app when the words just before it say so: "thread start with id:" and "session id:" are not.
        static bool BareId(string t, Match m)
        {
            if (!m.Value.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) return false;
            string before = t.Substring(Math.Max(0, m.Index - 30), Math.Min(30, m.Index)).ToLowerInvariant();
            return !before.Contains("app") && !before.Contains("policy");
        }

        const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
        readonly ScriptLog scripts = new ScriptLog();

        public ImeLogParser(string src)
        {
            source = src;
        }

        public override string Source
        {
            get { return source; }
        }

        Obs ForApp(LogLine l, string id)
        {
            Obs o = Item(New(l), IntuneKeys.App(id), null, Kinds.Win32App, Origins.Intune, Phases.IntuneApps);
            o.AddInfo("ID", id);
            return o;
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            string t = l.Text ?? "";
            if (source != "AppWorkload" && scripts.Read(l, source, output)) return;
            if (t.IndexOf("Win32App", StringComparison.OrdinalIgnoreCase) < 0 && source != "AgentExecutor" && source != "AppWorkload") return;
            string thread = l.Thread ?? "";
            string id = null;
            // "for user with id: <user> and app: <app>": the first ID is the user, and all zeros means the device.
            Match m = RxId.Match(t);
            while (m.Success && (m.Groups["id"].Value == ZeroGuid || BareId(t, m) || (m.Index >= 5 && t.Substring(0, m.Index).TrimEnd().EndsWith("user with", StringComparison.OrdinalIgnoreCase))))
                m = m.NextMatch();
            if (m.Success)
            {
                id = m.Groups["id"].Value.ToLowerInvariant();
                currentByThread[thread] = id;
            }
            else currentByThread.TryGetValue(thread, out id);
            if (id == null) return;
            // The agent first walks through every app without doing anything, to work out what the ESP has to wait for.
            if (t.IndexOf("[Simulated", StringComparison.OrdinalIgnoreCase) >= 0) return;
            // The install command is on a line of its own, right after the step that announces it.
            if (t.IndexOf("===Step=== InstallBehavior", StringComparison.OrdinalIgnoreCase) >= 0) { commandNext[thread] = id; return; }
            string pending;
            if (commandNext.TryGetValue(thread, out pending))
            {
                commandNext.Remove(thread);
                if (!t.StartsWith("[") && t.Trim().Length > 0 && pending == id)
                {
                    Obs c = ForApp(l, id);
                    c.Command = t.Trim();
                    c.Text = "Install command: " + Txt.Short(t.Trim(), 300);
                    output.Add(c);
                    return;
                }
            }

            Obs o = ForApp(l, id);
            bool useful = m.Success;
            string say = null;
            Match cm = RxCommand.Match(t);
            if (cm.Success) o.Command = cm.Groups["c"].Value;

            // "Report delta" lines from the reporting manager: the most reliable state change in the log.
            List<string[]> deltas = new List<string[]>();
            foreach (Match d in RxDelta.Matches(t)) deltas.Add(new string[] { d.Groups["f"].Value, d.Groups["from"].Value.Trim(), d.Groups["to"].Value.Trim() });
            foreach (Match d in RxDelta2.Matches(t))
                deltas.Add(new string[] { d.Groups["f"].Value, d.Groups["from"].Success ? d.Groups["from"].Value : d.Groups["fromn"].Value, d.Groups["to"].Success ? d.Groups["to"].Value : d.Groups["ton"].Value });
            foreach (string[] d in deltas)
            {
                string f = d[0], from = d[1], to = d[2];
                useful = true;
                if (f.Equals("EnforcementState", StringComparison.OrdinalIgnoreCase))
                {
                    o.State = Enforcement(to, ref o.Reboot) ?? o.State;
                    say = "Enforcement state: " + (from.Length > 0 ? Txt.SplitCamel(from) + " \u2192 " : "") + Txt.SplitCamel(to);
                }
                else if (f.Equals("DetectionState", StringComparison.OrdinalIgnoreCase))
                {
                    o.AddInfo("Last detection", Txt.SplitCamel(to));
                    if (say == null) say = "Detection: " + Txt.SplitCamel(to).ToLowerInvariant();
                }
                else if (f.Equals("DesiredState", StringComparison.OrdinalIgnoreCase))
                {
                    o.AddInfo("Desired state", Txt.SplitCamel(to));
                    if (say == null) say = "Desired state: " + Txt.SplitCamel(to).ToLowerInvariant();
                }
                else if (f.Equals("ApplicabilityState", StringComparison.OrdinalIgnoreCase) && to.Equals("Applicable", StringComparison.OrdinalIgnoreCase))
                {
                    if (say == null) say = "Applicable to this device";
                }
                else if (f.Equals("ApplicabilityState", StringComparison.OrdinalIgnoreCase) && to.Length > 0 && to.IndexOf("NotApplicable", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    o.State = State.NotApplicable;
                    say = "Not applicable: " + Txt.SplitCamel(to).ToLowerInvariant();
                }
                else if (f.Equals("ApplicabilityState", StringComparison.OrdinalIgnoreCase) && to.IndexOf("NotApplicable", StringComparison.OrdinalIgnoreCase) >= 0) o.State = State.NotApplicable;
                else if (f.Equals("ErrorCode", StringComparison.OrdinalIgnoreCase) || f.Equals("EnforcementErrorCode", StringComparison.OrdinalIgnoreCase))
                {
                    long? c = Errors.Parse(to);
                    if (c.HasValue && c.Value != 0) o.ExitCode = c;
                }
            }
            if (o.State == null)
            {
                Match dm = RxDetect.Match(t);
                if (dm.Success)
                {
                    string v = dm.Groups["d"].Success ? dm.Groups["d"].Value : dm.Groups["d2"].Success ? dm.Groups["d2"].Value : dm.Groups["d3"].Value;
                    o.AddInfo("Last detection", v);
                    say = "Detection: " + (v.Equals("Detected", StringComparison.OrdinalIgnoreCase) ? "installed" : "not installed");
                    useful = true;
                }
                else if (RxNotApplicable.IsMatch(t)) { o.State = State.NotApplicable; say = "Requirements not met"; useful = true; }
                else if (RxFailedDl.IsMatch(t)) { o.State = State.Failed; o.Severity = 2; say = "Download failed"; useful = true; }
                else if (RxStillRunning.IsMatch(t))
                {
                    o.Severity = 1;
                    Match mm = RxMinutes.Match(t);
                    say = "The installer is still running" + (mm.Success ? " after " + mm.Groups["m"].Value + " min" : "");
                    useful = true;
                }
                else if (RxInstall.IsMatch(t)) { o.State = State.Installing; say = "Install started" + (o.Command != null ? ": " + o.Command : ""); useful = true; }
                else if (RxDownloaded.IsMatch(t)) { o.Percent = 100; say = "Content downloaded and verified"; useful = true; }
                else if (RxDownload.IsMatch(t)) { o.State = State.Downloading; o.NewAttempt = true; say = "Download started"; useful = true; }
            }
            Match em = RxExit.Match(t);
            if (em.Success)
            {
                o.ExitCode = Errors.Parse(em.Groups["c"].Value);
                o.AddInfo("Installer exit code", em.Groups["c"].Value);
                if (o.ExitCode.HasValue && (o.ExitCode.Value == 3010 || o.ExitCode.Value == 1641)) o.Reboot = true;
                if (RxNoMapping.IsMatch(t)) { o.State = State.Failed; o.Severity = 2; }
                if (say == null) say = "Installer exited with exit code " + em.Groups["c"].Value + (RxNoMapping.IsMatch(t) ? ": not a success code for this app" : "");
                useful = true;
            }
            Match bm = RxBytes.Match(t);
            if (bm.Success)
            {
                double a, b;
                if (double.TryParse(bm.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                    double.TryParse(bm.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b > 0)
                {
                    o.Percent = (int)Math.Min(100, Math.Round(a * 100 / b));
                    if (o.State == null) o.State = State.Downloading;
                    if (say == null) say = "Downloaded " + o.Percent + " %";
                    useful = true;
                }
            }
            else
            {
                Match pm = RxPercent.Match(t);
                double p;
                if (pm.Success && double.TryParse(pm.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out p))
                {
                    o.Percent = (int)Math.Min(100, Math.Round(p));
                    if (o.State == null) o.State = State.Downloading;
                    if (say == null) say = "Downloaded " + o.Percent + " %";
                    useful = true;
                }
            }
            if (!useful && l.Severity < 1) return;
            // Bookkeeping about reporting says nothing about the app itself.
            if (say == null && o.State == null && !o.ExitCode.HasValue && RxNoise.IsMatch(t)) return;
            // The same thing said again for the same app adds nothing.
            if (say != null && o.State != State.Failed && !o.ExitCode.HasValue)
            {
                string prev;
                if (lastSaid.TryGetValue(id, out prev) && prev == say) return;
                lastSaid[id] = say;
            }
            // A plain sentence for the list and the timeline; the raw line stays in the evidence.
            o.Text = say ?? RxIdPhrase.Replace(t, "").Trim();
            if (o.State == State.Failed) o.Severity = 2;
            if (l.Severity > o.Severity) o.Severity = l.Severity;
            output.Add(o);
        }

        static State? Enforcement(string v, ref bool reboot)
        {
            if (v.IndexOf("Reboot", StringComparison.OrdinalIgnoreCase) >= 0) reboot = true;
            string s = v.ToLowerInvariant();
            if (s.StartsWith("success")) return State.Succeeded;
            if (s.Contains("downloadcompleted")) return State.Installing;
            if (s.Contains("download")) return State.Downloading;
            if (s.StartsWith("inprogress") || s.StartsWith("in progress")) return State.Installing;
            if (s.Contains("requirementsnotmet") || s.Contains("notapplicable")) return State.NotApplicable;
            if (s.StartsWith("error") || s.Contains("fail")) return State.Failed;
            if (s.StartsWith("notattempted") || s.StartsWith("not attempted")) return State.Pending;
            return null;
        }

        // Names of Win32 apps from the IME logs in a folder.
        public static Dictionary<string, string> NamesFromFolder(string folder)
        {
            Dictionary<string, string> all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return all;
                List<FileInfo> files = new List<FileInfo>();
                foreach (string pre in new string[] { "IntuneManagementExtension", "AppWorkload", "AppActionProcessor" })
                    foreach (FileInfo f in new DirectoryInfo(folder).GetFiles(pre + "*.log")) files.Add(f);
                files.Sort(delegate (FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                foreach (FileInfo f in files)
                {
                    Dictionary<string, string> n = ImeLog.ReadNames(f.FullName);
                    foreach (KeyValuePair<string, string> kv in n) all[kv.Key.Trim('{', '}').ToLowerInvariant()] = kv.Value;
                }
            }
            catch (Exception) { }
            return all;
        }
    }

    // Reads a snapshot of a registry area and returns only what changed since the last read, as observations.
    public abstract class RegistryWatch
    {
        readonly Dictionary<string, string> last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        protected abstract List<Obs> Snapshot(IRegSource reg, DateTime nowUtc);

        public List<Obs> Read(IRegSource reg, DateTime nowUtc)
        {
            List<Obs> result = new List<Obs>();
            List<Obs> snap;
            try { snap = Snapshot(reg, nowUtc); }
            catch (Exception) { return result; }
            foreach (Obs o in snap)
            {
                string key = o.ItemKey ?? o.Source;
                string sig = Signature(o);
                string old;
                if (last.TryGetValue(key, out old) && old == sig) continue;
                last[key] = sig;
                result.Add(o);
            }
            return result;
        }

        static string Signature(Obs o)
        {
            return (o.State.HasValue ? o.State.Value.ToString() : "") + "|" + (o.ExitCode.HasValue ? o.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : "") + "|" + o.PhaseKey + "|" + o.Percent;
        }
    }

    // The Enrollment Status Page tracking keys. Every key with an InstallationState value is an item; its path says the phase.
    // The layout is not documented, so the reader walks the tree instead of reading fixed paths.
    public sealed class EspWatch : RegistryWatch
    {
        public const string Root = @"HKLM\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking";
        static readonly Regex RxSid = new Regex(@"^S-1-\d+(-\d+)+$", RegexOptions.Compiled);
        static readonly Regex RxApp = new Regex(@"^(?<kind>Win32App|MSI|ModernApp|Store\w*)_(?<id>[0-9A-Fa-f{}-]{36,38})(?:_\d+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public Func<string, string> Names;

        protected override List<Obs> Snapshot(IRegSource reg, DateTime nowUtc)
        {
            List<Obs> list = new List<Obs>();
            Walk(reg, Root, 0, list, nowUtc);
            return list;
        }

        void Walk(IRegSource reg, string path, int depth, List<Obs> list, DateTime nowUtc)
        {
            if (depth > 9) return;
            string rel = path.Length > Root.Length ? path.Substring(Root.Length + 1) : "";
            if (rel.StartsWith("ESPTrackingInfo", StringComparison.OrdinalIgnoreCase)) return;
            RegValue st = reg.Get(path, "InstallationState");
            if (st != null && rel.Length > 0)
            {
                // The key's write time is when the ESP set this state, which matters on a device that finished long ago.
                DateTime? wt = reg.WriteTime(path);
                Obs o = Entry(rel, st.Data, wt.HasValue && wt.Value <= nowUtc.AddMinutes(1) ? wt.Value : nowUtc);
                if (o != null)
                {
                    o.Evidence = new Evidence(EvidenceKinds.Registry, path, 0, "InstallationState = " + st.Data);
                    list.Add(o);
                }
            }
            foreach (string sub in reg.SubKeys(path)) Walk(reg, path + "\\" + sub, depth + 1, list, nowUtc);
        }

        public static string PhaseOf(string rel)
        {
            string[] parts = rel.Split('\\');
            if (parts.Length == 0) return null;
            if (RxSid.IsMatch(parts[0])) return Phases.AccountSetup;
            if (parts[0].Equals("Device", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length > 1 && parts[1].Equals("DevicePreparation", StringComparison.OrdinalIgnoreCase)) return Phases.DevicePreparation;
                return Phases.DeviceSetup;
            }
            return null;
        }

        Obs Entry(string rel, string data, DateTime nowUtc)
        {
            string phase = PhaseOf(rel);
            if (phase == null) return null;
            string[] parts = rel.Split('\\');
            string leaf = parts[parts.Length - 1];
            string parent = parts.Length > 1 ? parts[parts.Length - 2] : "";
            Obs o = new Obs();
            o.TimeUtc = nowUtc;
            o.Source = "ESP";
            o.PhaseKey = phase;
            o.State = InstallationState(data);
            o.Origin = Origins.Intune;
            Match m = RxApp.Match(leaf);
            if (m.Success)
            {
                string id = m.Groups["id"].Value.Trim('{', '}').ToLowerInvariant();
                string k = m.Groups["kind"].Value.ToLowerInvariant();
                o.ItemKind = k == "win32app" ? Kinds.Win32App : k == "msi" ? Kinds.MsiApp : Kinds.StoreApp;
                o.ItemKey = IntuneKeys.App(id);
                o.ItemName = Names != null ? Names(id) : null;
                o.AddInfo("ID", id);
            }
            else if (parent.Equals("SidecarProvisioning", StringComparison.OrdinalIgnoreCase) || leaf.Equals("SidecarProvisioning", StringComparison.OrdinalIgnoreCase)
                || (leaf.Equals("Sidecar", StringComparison.OrdinalIgnoreCase) && parent.Equals("PolicyProviders", StringComparison.OrdinalIgnoreCase)))
            {
                o.ItemKind = Kinds.Agent;
                o.ItemKey = "esp:" + phase + ":ime";
                o.ItemName = "Intune Management Extension";
            }
            else
            {
                o.ItemKind = KindOf(parent, leaf);
                o.ItemKey = "esp:" + phase + ":" + rel.ToLowerInvariant();
                o.ItemName = Txt.SplitCamel(leaf.Replace("Provider", "").Trim('_'));
                if (string.IsNullOrEmpty(o.ItemName)) o.ItemName = leaf;
            }
            o.AddInfo("ESP phase", Phases.Name(phase));
            o.Text = "ESP: " + States.Label(o.State ?? State.Unknown).ToLowerInvariant() + " (InstallationState " + data + ")";
            if (o.State == State.Failed) o.Severity = 2;
            return o;
        }

        static string KindOf(string parent, string leaf)
        {
            string s = (parent + " " + leaf).ToLowerInvariant();
            if (s.Contains("cert")) return Kinds.Certificate;
            if (s.Contains("wifi") || s.Contains("vpn") || s.Contains("profile")) return Kinds.Profile;
            if (s.Contains("polic")) return Kinds.Policy;
            if (s.Contains("msi")) return Kinds.MsiApp;
            if (s.Contains("modern") || s.Contains("store")) return Kinds.StoreApp;
            if (s.Contains("sidecar")) return Kinds.Win32App;
            return Kinds.Policy;
        }

        // 1 not installed, 2 in progress, 3 completed, 4 error.
        public static State InstallationState(string data)
        {
            long? n = ImeCodes.Parse(data);
            if (n.HasValue)
            {
                switch (n.Value)
                {
                    case 1: return State.Pending;
                    case 2: return State.Installing;
                    case 3: return State.Succeeded;
                    case 4: return State.Failed;
                }
            }
            string s = (data ?? "").ToLowerInvariant();
            if (s.Contains("progress")) return State.Installing;
            if (s.Contains("complete") || s.Contains("success")) return State.Succeeded;
            if (s.Contains("error") || s.Contains("fail")) return State.Failed;
            if (s.Contains("notinstalled") || s.Contains("not installed")) return State.Pending;
            return State.Unknown;
        }
    }

    // Win32 apps under the Intune Management Extension key, read with PolicyVice's IME reader.
    public sealed class Win32AppWatch : RegistryWatch
    {
        public Func<string, string> Names;

        protected override List<Obs> Snapshot(IRegSource reg, DateTime nowUtc)
        {
            List<Obs> list = new List<Obs>();
            foreach (ImeItem it in ImeCollector.Collect(reg))
            {
                if (it.Kind != ImeKinds.Win32App || string.IsNullOrEmpty(it.Id)) continue;
                Obs o = new Obs();
                DateTime? when = it.Time;
                if (!when.HasValue && !string.IsNullOrEmpty(it.RegistryPath)) when = reg.WriteTime(it.RegistryPath);
                o.TimeUtc = when.HasValue && when.Value <= nowUtc.AddMinutes(1) ? when.Value : nowUtc;
                o.Source = "IME registry";
                o.ItemKey = IntuneKeys.App(it.Id);
                o.ItemKind = Kinds.Win32App;
                o.Origin = Origins.Intune;
                o.PhaseKey = Phases.IntuneApps;
                o.ItemName = Names != null ? Names(it.Id.ToLowerInvariant()) : null;
                o.AddInfo("ID", it.Id.ToLowerInvariant());
                o.AddInfo("Assignment", it.Assignment);
                o.AddInfo("Status in the registry", it.Status);
                bool reboot;
                o.State = Map(it.Status, it.Failed, out reboot);
                o.Reboot = reboot;
                long? code = null;
                if (!string.IsNullOrEmpty(it.ErrorCode))
                {
                    string raw = it.ErrorCode;
                    int colon = raw.IndexOf(':');
                    if (colon > 0) raw = raw.Substring(0, colon);
                    int paren = raw.IndexOf(' ');
                    if (paren > 0) raw = raw.Substring(0, paren);
                    code = Errors.Parse(raw);
                }
                if (code.HasValue && code.Value != 0) o.ExitCode = code;
                o.Text = "IME registry: " + it.Status + (code.HasValue && code.Value != 0 ? ", " + Errors.Short(code) : "");
                if (o.State == State.Failed) o.Severity = 2;
                o.Evidence = new Evidence(EvidenceKinds.Registry, it.RegistryPath, 0, "EnforcementStateMessage / ComplianceStateMessage");
                list.Add(o);
            }
            return list;
        }

        public static State? Map(string status, bool failed, out bool reboot)
        {
            reboot = false;
            if (failed) return State.Failed;
            string s = (status ?? "").ToLowerInvariant();
            if (s.Contains("reboot pending")) { reboot = true; return State.WaitingReboot; }
            if (s == "installed" || s.StartsWith("installed")) return State.Succeeded;
            if (s.Contains("not applicable")) return State.NotApplicable;
            if (s.Contains("downloaded") || s.Contains("in progress") || s.Contains("waiting for dependencies")) return State.Installing;
            if (s.Contains("retry") || s.Contains("not attempted") || s.Contains("not installed") || s.Contains("waiting for reboot")) return State.Pending;
            return null;
        }
    }

    // The Autopilot profile, the join type and the ESP settings, for the header and the ESP time limit.
    public static class AutopilotInfo
    {
        public const string ProfileKey = @"HKLM\SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot";
        public const string PolicyCacheKey = @"HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache";
        public const string EnrollmentsKey = @"HKLM\SOFTWARE\Microsoft\Enrollments";

        public static List<Pair> Read(IRegSource reg)
        {
            List<Pair> info = new List<Pair>();
            try
            {
                DateTime? installed = WindowsInstalled(reg);
                if (installed.HasValue) info.Add(new Pair("Windows installed", Txt.Local(installed.Value)));
                string profile = Str(reg.Get(ProfileKey, "DeploymentProfileName"));
                string tenant = Str(reg.Get(ProfileKey, "CloudAssignedTenantDomain"));
                if (!string.IsNullOrEmpty(profile)) info.Add(new Pair("Autopilot profile", profile));
                if (!string.IsNullOrEmpty(tenant)) info.Add(new Pair("Tenant", tenant));
                string json = Str(reg.Get(PolicyCacheKey, "PolicyJsonCache"));
                if (!string.IsNullOrEmpty(json))
                {
                    Match m = Regex.Match(json, "\"CloudAssignedDomainJoinMethod\"\\s*:\\s*\"?(?<v>\\d)");
                    if (m.Success) info.Add(new Pair("Join type", m.Groups["v"].Value == "1" ? "Hybrid Entra join" : "Entra join"));
                    m = Regex.Match(json, "\"DeploymentProfileName\"\\s*:\\s*\"(?<v>[^\"]+)\"");
                    if (m.Success && string.IsNullOrEmpty(profile)) info.Add(new Pair("Autopilot profile", m.Groups["v"].Value));
                    m = Regex.Match(json, "\"CloudAssignedOobeConfig\"\\s*:\\s*\"?(?<v>\\d+)");
                    int oobe;
                    if (m.Success && int.TryParse(m.Groups["v"].Value, out oobe) && (oobe & 0x40) != 0) info.Add(new Pair("Autopilot mode", "Self-deploying"));
                }
                foreach (string id in reg.SubKeys(EnrollmentsKey))
                {
                    string fs = EnrollmentsKey + "\\" + id + @"\FirstSync";
                    RegValue to = reg.Get(fs, "SyncFailureTimeout");
                    if (to == null) continue;
                    long? min = ImeCodes.Parse(to.Data);
                    if (min.HasValue && min.Value > 0) info.Add(new Pair("ESP time limit (min)", min.Value.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
            }
            catch (Exception) { }
            return info;
        }

        // InstallDate is the time Windows was installed, in seconds since 1970 (UTC). A feature update resets it.
        public static DateTime? WindowsInstalled(IRegSource reg)
        {
            RegValue v = reg.Get(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate");
            long? s = v == null ? null : ImeCodes.Parse(v.Data);
            if (!s.HasValue || s.Value <= 0) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s.Value);
        }

        static string Str(RegValue v)
        {
            return v == null ? null : v.Data;
        }
    }
}
