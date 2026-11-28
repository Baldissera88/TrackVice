namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    // Turns lines of one kind of log into observations. One instance per log, because most logs need a little state.
    public abstract class LogParser
    {
        public abstract string Source { get; }
        public abstract void Read(LogLine l, List<Obs> output);

        protected Obs New(LogLine l)
        {
            return Obs.Log(l, Source);
        }

        protected static Obs Item(Obs o, string key, string name, string kind, string origin, string phase)
        {
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = kind;
            o.Origin = origin;
            o.PhaseKey = phase;
            return o;
        }

        protected const RegexOptions Ci = RegexOptions.Compiled | RegexOptions.IgnoreCase;

        // A pattern for log text, which is not trusted: it gives up after CmLog.MatchTimeout. A name in a pattern is at most
        // 1024 characters and a command line at most 32767, so a line that repeats the start of a pattern costs one pass
        // and not a pass for every repeat.
        protected static Regex Rx(string pattern)
        {
            return new Regex(pattern, Ci, CmLog.MatchTimeout);
        }

        // Reads one line. A line the parser cannot handle, such as one too complex for a pattern, is skipped with what it
        // had added, and false is returned, so it does not stop the reading of everything after it.
        public static bool Safe(LogParser p, LogLine l, List<Obs> output)
        {
            int before = output.Count;
            try
            {
                p.Read(l, output);
                return true;
            }
            catch (Exception)
            {
                if (output.Count > before) output.RemoveRange(before, output.Count - before);
                return false;
            }
        }

        // The parser for a log, by its base name (CmLog.BaseName). Null for logs TrackVice does not read.
        public static LogParser For(string baseName)
        {
            switch ((baseName ?? "").ToLowerInvariant())
            {
                case "smsts": return new SmstsParser();
                case "appenforce": return new AppEnforceParser();
                case "appdiscovery": return new AppDiscoveryParser();
                case "appintenteval": return new AppIntentEvalParser();
                case "execmgr": return new ExecmgrParser();
                case "cas": return new CasParser();
                case "updatesdeployment": return new UpdatesParser("UpdatesDeployment");
                case "updateshandler": return new UpdatesParser("UpdatesHandler");
                case "intunemanagementextension": return new ImeLogParser("IntuneManagementExtension");
                case "appworkload": return new ImeLogParser("AppWorkload");
                case "agentexecutor": return new ImeLogParser("AgentExecutor");
                case "healthscripts": return new HealthScriptsParser();
                default: return null;
            }
        }

        public static readonly string[] SccmLogs = { "AppEnforce", "AppDiscovery", "AppIntentEval", "execmgr", "CAS", "UpdatesDeployment", "UpdatesHandler" };
        public static readonly string[] ImeLogs = { "IntuneManagementExtension", "AppWorkload", "AgentExecutor", "HealthScripts" };
    }

    // smsts.log: the steps of a task sequence. It moves during OSD, so one parser reads every copy and skips lines it already saw.
    public sealed class SmstsParser : LogParser
    {
        static readonly Regex RxStart = Rx(@"Start executing an instruc?tion\. Instruction name: (?<n>.{1,1024}?)\. Instruction pointer: (?<p>\d+)");
        static readonly Regex RxOk = Rx(@"Successfully complete?d? the action \((?<n>.{1,1024}?)\) with the exit win32 code (?<c>-?\d+)");
        static readonly Regex RxFail = new Regex(@"Failed to run the action: (?<n>.{1,1024}?)\.\s*(?<rest>.*)$", Ci | RegexOptions.Singleline, CmLog.MatchTimeout);
        static readonly Regex RxSkip = Rx(@"The action \((?<n>.{1,1024}?)\) has been skipped because the condition is evaluated to be false");
        static readonly Regex RxContinue = Rx(@"The action \((?<n>.{1,1024}?)\) failed but (?:the task sequence|execution) (?:will )?continues?");
        static readonly Regex RxVar = Rx(@"Set (?:a )?(?:global|local)? ?(?:environment )?variable (?<k>_SMSTS\w+|_SMSTSPackageName|_SMSTSInWinPE|_SMSTSPackageID|_SMSTSAdvertID)=(?<v>.*)$");
        static readonly Regex RxCmd = Rx(@"^Executing command line: (?<c>.+)$");
        static readonly Regex RxExit = Rx(@"Process completed with exit code (?<c>-?\d+)");
        static readonly Regex RxErr = Rx(@"(?:Error:?\s*|code\s+|hr=)(?<c>0x[0-9A-Fa-f]{8}|[0-9A-Fa-f]{8}|-?\d{4,10})");
        static readonly Regex RxTsFail = Rx(@"(?:Task sequence execution failed with error code (?<c>\S+)|Execution of task sequence failed|Task Sequence Engine failed! Code: (?<c2>\S+))");
        static readonly Regex RxTsDone = Rx(@"Task sequence completed (?<c>0x[0-9A-Fa-f]+)");
        static readonly Regex RxGroupFail = Rx(@"The execution of the group \((?<g>.{1,1024}?)\) has failed");
        static readonly Regex RxReboot = Rx(@"^(?:Rebooting|Restarting|Reboot(?:ing)? (?:the )?(?:computer|system)|The computer will restart|Executing reboot)");

        readonly HashSet<string> seen = new HashSet<string>();
        readonly Dictionary<string, string> keyByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string currentKey, currentName, phase = Phases.TaskSequence, tsName;
        bool sawPhase;

        public override string Source
        {
            get { return "smsts"; }
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            // The same line in two copies of smsts.log: keep the first.
            string sig = (l.Utc.HasValue ? l.Utc.Value.Ticks.ToString(CultureInfo.InvariantCulture) : "") + "|" + l.Thread + "|" + l.Text;
            if (!seen.Add(sig)) return;
            string t = l.Text ?? "";
            Match m;

            if ((m = RxVar.Match(t)).Success)
            {
                string k = m.Groups["k"].Value, v = m.Groups["v"].Value.Trim();
                if (k.Equals("_SMSTSPackageName", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                {
                    tsName = v;
                    Obs o = New(l);
                    o.Text = null;
                    o.AddSessionInfo("Task sequence", v);
                    output.Add(o);
                }
                else if (k.Equals("_SMSTSPackageID", StringComparison.OrdinalIgnoreCase)) { Obs o = New(l); o.Text = null; o.AddSessionInfo("Task sequence ID", v); output.Add(o); }
                else if (k.Equals("_SMSTSAdvertID", StringComparison.OrdinalIgnoreCase)) { Obs o = New(l); o.Text = null; o.AddSessionInfo("Deployment ID", v); output.Add(o); }
                else if (k.Equals("_SMSTSInWinPE", StringComparison.OrdinalIgnoreCase))
                {
                    string np = v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? Phases.WinPE : Phases.FullOs;
                    if (np != phase || !sawPhase)
                    {
                        phase = np;
                        sawPhase = true;
                    }
                }
                return;
            }
            if ((m = RxStart.Match(t)).Success)
            {
                string name = m.Groups["n"].Value.Trim();
                string key = "ts:" + m.Groups["p"].Value + ":" + name;
                keyByName[name] = key;
                currentKey = key;
                currentName = name;
                Obs o = Item(New(l), key, name, Kinds.Step, Origins.Sccm, phase);
                o.State = State.Installing;
                o.NewAttempt = true;
                o.Text = "Started step " + name;
                o.AddInfo("Instruction pointer", m.Groups["p"].Value);
                if (tsName != null) o.AddInfo("Task sequence", tsName);
                output.Add(o);
                return;
            }
            if ((m = RxOk.Match(t)).Success)
            {
                string name = m.Groups["n"].Value.Trim();
                Obs o = Item(New(l), KeyFor(name), name, Kinds.Step, Origins.Sccm, phase);
                o.State = State.Succeeded;
                long? c = Errors.Parse(m.Groups["c"].Value);
                o.ExitCode = c;
                o.Text = "Completed with exit code " + m.Groups["c"].Value;
                if (c.HasValue && (c.Value == 3010 || c.Value == 1641)) o.Reboot = true;
                output.Add(o);
                return;
            }
            if ((m = RxFail.Match(t)).Success)
            {
                string name = m.Groups["n"].Value.Trim();
                Obs o = Item(New(l), KeyFor(name), name, Kinds.Step, Origins.Sccm, phase);
                o.State = State.Failed;
                o.Severity = 2;
                Match e = RxErr.Match(m.Groups["rest"].Value);
                if (e.Success) o.ExitCode = Errors.Parse(e.Groups["c"].Value);
                output.Add(o);
                return;
            }
            if ((m = RxContinue.Match(t)).Success)
            {
                string name = m.Groups["n"].Value.Trim();
                Obs o = Item(New(l), KeyFor(name), name, Kinds.Step, Origins.Sccm, phase);
                o.Severity = 1;
                o.AddInfo("Continue on error", "Yes");
                output.Add(o);
                return;
            }
            if ((m = RxSkip.Match(t)).Success)
            {
                string name = m.Groups["n"].Value.Trim();
                Obs o = Item(New(l), KeyFor(name), name, Kinds.Step, Origins.Sccm, phase);
                o.State = State.Skipped;
                o.Text = "Skipped: its condition is false";
                output.Add(o);
                return;
            }
            if ((m = RxCmd.Match(t)).Success && currentKey != null)
            {
                Obs o = Item(New(l), currentKey, currentName, Kinds.Step, Origins.Sccm, phase);
                o.Command = m.Groups["c"].Value.Trim();
                output.Add(o);
                return;
            }
            if ((m = RxExit.Match(t)).Success && currentKey != null)
            {
                Obs o = Item(New(l), currentKey, currentName, Kinds.Step, Origins.Sccm, phase);
                o.ExitCode = Errors.Parse(m.Groups["c"].Value);
                output.Add(o);
                return;
            }
            if ((m = RxTsFail.Match(t)).Success)
            {
                string c = m.Groups["c"].Success ? m.Groups["c"].Value : m.Groups["c2"].Success ? m.Groups["c2"].Value : "";
                long? code = Errors.Parse(c);
                Obs o = New(l);
                o.Severity = 2;
                o.AddSessionInfo("Task sequence result", "Failed" + (code.HasValue ? ": " + Errors.Describe(code.Value) : ""));
                if (l.Utc.HasValue) o.AddSessionInfo("Task sequence ended", l.Utc.Value.ToString("o", CultureInfo.InvariantCulture));
                output.Add(o);
                return;
            }
            if ((m = RxTsDone.Match(t)).Success)
            {
                long? code = Errors.Parse(m.Groups["c"].Value);
                Obs o = New(l);
                if (code.HasValue && code.Value == 0) o.AddSessionInfo("Task sequence result", "Succeeded");
                else
                {
                    o.Severity = 2;
                    o.AddSessionInfo("Task sequence result", "Failed: " + Errors.Describe(code ?? -1));
                }
                if (l.Utc.HasValue) o.AddSessionInfo("Task sequence ended", l.Utc.Value.ToString("o", CultureInfo.InvariantCulture));
                output.Add(o);
                return;
            }
            if ((m = RxGroupFail.Match(t)).Success)
            {
                Obs o = New(l);
                o.Severity = 2;
                output.Add(o);
                return;
            }
            if (RxReboot.IsMatch(t))
            {
                Obs o = New(l);
                o.Severity = 1;
                output.Add(o);
                return;
            }
            // Errors from the task sequence engine are worth a place on the timeline, attached to the running step.
            if (l.Severity >= 2 && currentKey != null)
            {
                Obs o = Item(New(l), currentKey, currentName, Kinds.Step, Origins.Sccm, phase);
                o.OnlyIfExists = true;
                output.Add(o);
            }
        }

        string KeyFor(string name)
        {
            string k;
            return keyByName.TryGetValue(name, out k) ? k : "ts:" + name;
        }
    }

    public static class SccmKeys
    {
        static readonly Regex RxScope = new Regex(@"(?<id>ScopeId_[0-9A-Fa-f-]+/(?:Application|DeploymentType)_[0-9A-Fa-f-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string App(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Match m = RxScope.Match(id);
            return "cm:" + (m.Success ? m.Groups["id"].Value : id.Trim()).ToLowerInvariant();
        }

        public static string Update(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            int i = id.IndexOf("SUM_", StringComparison.OrdinalIgnoreCase);
            return "upd:" + (i >= 0 ? id.Substring(i + 4) : id).Trim().ToLowerInvariant();
        }

        public static string Package(string pkg, string program)
        {
            return "pkg:" + (pkg ?? "").ToUpperInvariant() + ":" + (program ?? "").ToLowerInvariant();
        }
    }

    // AppEnforce.log: one install or uninstall of an application deployment type, from command line to detection.
    public sealed class AppEnforceParser : LogParser
    {
        static readonly Regex RxStart = Rx("\\+\\+\\+ Starting (?<act>Install|Uninstall) enforcement for App DT \"(?<dt>[^\"]{1,1024})\" ApplicationDeliveryType - (?<id>[^,]{1,1024}), Revision - (?<rev>\\d+)(?:, ContentPath - (?<cp>[^,]{0,32767}))?(?:, Execution Context - (?<ctx>\\w+))?");
        static readonly Regex RxCmd = Rx(@"Executing Command line: (?<c>.{1,32767}?)\s+with (?:user|system) context");
        static readonly Regex RxProcExit = Rx(@"Process (?<pid>\d+) terminated with exitcode: (?<c>-?\d+)");
        static readonly Regex RxMatched = Rx(@"Matched exit code (?<c>-?\d+) to a (?<k>\w+) entry in exit codes table");
        static readonly Regex RxUnmatched = Rx(@"Unmatched exit code \((?<c>-?\d+)\) is considered an execution failure");
        static readonly Regex RxFound = Rx(@"\+\+\+ Discovered application \[AppDT Id: (?<id>[^,\]]+)");
        static readonly Regex RxNotFound = Rx(@"\+\+\+ Application not discovered\. \[AppDT Id: (?<id>[^,\]]+)");
        static readonly Regex RxDone = Rx("App enforcement completed \\((?<s>\\d+) seconds\\) for App DT \"(?<dt>[^\"]{1,1024})\" \\[(?<id>[^\\]]{1,1024})\\]");
        static readonly Regex RxWait = Rx(@"Waiting for process (?<pid>\d+) to finish\.\s*Timeout = (?<t>\d+) minutes");
        static readonly Regex RxTimeout = Rx(@"(?:Job|Process) .{0,1024}time(?:d)? ?out|exceeded (?:the )?(?:maximum|max) run ?time");

        sealed class Run
        {
            public string Key, Name;
            public bool Installed, ExitOk, Uninstall;
            public long? Exit;
        }

        readonly Dictionary<string, Run> runs = new Dictionary<string, Run>(StringComparer.OrdinalIgnoreCase);
        Run current;

        public override string Source
        {
            get { return "AppEnforce"; }
        }

        Obs ForRun(LogLine l, Run r)
        {
            return Item(New(l), r.Key, r.Name, Kinds.Application, Origins.Sccm, Phases.SccmApps);
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            string t = l.Text ?? "";
            Match m;
            if ((m = RxStart.Match(t)).Success)
            {
                Run r = new Run();
                r.Key = SccmKeys.App(m.Groups["id"].Value);
                r.Name = m.Groups["dt"].Value;
                r.Uninstall = m.Groups["act"].Value.Equals("Uninstall", StringComparison.OrdinalIgnoreCase);
                runs[r.Key] = r;
                current = r;
                Obs o = ForRun(l, r);
                o.WeakName = true;
                o.State = State.Installing;
                o.NewAttempt = true;
                o.Text = (r.Uninstall ? "Uninstall" : "Install") + " started for deployment type " + r.Name;
                o.AddInfo("Deployment type", r.Name).AddInfo("Deployment type ID", m.Groups["id"].Value.Trim()).AddInfo("Revision", m.Groups["rev"].Value)
                 .AddInfo("Content path", m.Groups["cp"].Value.Trim()).AddInfo("Execution context", m.Groups["ctx"].Value);
                if (r.Uninstall) o.AddInfo("Action", "Uninstall");
                output.Add(o);
                return;
            }
            if ((m = RxCmd.Match(t)).Success && current != null)
            {
                Obs o = ForRun(l, current);
                o.Command = m.Groups["c"].Value.Trim();
                o.Text = "Running " + Txt.Short(o.Command, 200);
                output.Add(o);
                return;
            }
            if ((m = RxWait.Match(t)).Success && current != null)
            {
                Obs o = ForRun(l, current);
                o.AddInfo("Maximum run time", m.Groups["t"].Value + " min");
                output.Add(o);
                return;
            }
            if ((m = RxProcExit.Match(t)).Success && current != null)
            {
                current.Exit = Errors.Parse(m.Groups["c"].Value);
                Obs o = ForRun(l, current);
                o.ExitCode = current.Exit;
                o.AddInfo("Installer exit code", m.Groups["c"].Value);
                o.Text = "Installer exited with " + Errors.Describe(current.Exit ?? 0);
                output.Add(o);
                return;
            }
            if ((m = RxMatched.Match(t)).Success && current != null)
            {
                string k = m.Groups["k"].Value;
                long? c = Errors.Parse(m.Groups["c"].Value);
                Obs o = ForRun(l, current);
                o.ExitCode = c;
                if (k.Equals("Failure", StringComparison.OrdinalIgnoreCase))
                {
                    o.State = State.Failed;
                    o.Severity = 2;
                }
                else if (k.Equals("FastRetry", StringComparison.OrdinalIgnoreCase))
                {
                    o.State = State.Pending;
                    o.Severity = 1;
                }
                else
                {
                    current.ExitOk = true;
                    o.State = State.Detecting;
                    if (k.IndexOf("Reboot", StringComparison.OrdinalIgnoreCase) >= 0) o.Reboot = true;
                }
                output.Add(o);
                return;
            }
            if ((m = RxUnmatched.Match(t)).Success && current != null)
            {
                Obs o = ForRun(l, current);
                o.ExitCode = Errors.Parse(m.Groups["c"].Value);
                o.State = State.Failed;
                o.Severity = 2;
                output.Add(o);
                return;
            }
            if ((m = RxFound.Match(t)).Success)
            {
                Run r = Find(m.Groups["id"].Value);
                if (r == null) return;
                r.Installed = true;
                Obs o = ForRun(l, r);
                if (r.Uninstall)
                {
                    if (r.ExitOk)
                    {
                        o.State = State.Failed;
                        o.ExitCode = unchecked((int)0x87D00325);
                        o.Severity = 2;
                    }
                }
                else
                {
                    o.State = State.Succeeded;
                    o.Text = r.ExitOk ? "Detected after install" : "Already installed: detected before install";
                }
                output.Add(o);
                return;
            }
            if ((m = RxNotFound.Match(t)).Success)
            {
                Run r = Find(m.Groups["id"].Value);
                if (r == null) return;
                Obs o = ForRun(l, r);
                if (r.ExitOk && !r.Uninstall)
                {
                    o.State = State.Failed;
                    o.ExitCode = unchecked((int)0x87D00324);
                    o.Severity = 2;
                    o.Text = "Not detected after install";
                }
                else if (r.Uninstall && r.ExitOk) o.State = State.Succeeded;
                output.Add(o);
                return;
            }
            if ((m = RxDone.Match(t)).Success)
            {
                Run r = Find(m.Groups["id"].Value) ?? current;
                if (r == null) return;
                Obs o = ForRun(l, r);
                o.Text = "Enforcement completed in " + m.Groups["s"].Value + " s";
                output.Add(o);
                if (r == current) current = null;
                return;
            }
            if (current != null && (l.Severity >= 2 || RxTimeout.IsMatch(t)))
            {
                Obs o = ForRun(l, current);
                if (RxTimeout.IsMatch(t))
                {
                    o.State = State.Failed;
                    o.ExitCode = unchecked((int)0x87D00213);
                }
                o.Severity = Math.Max(1, l.Severity);
                output.Add(o);
            }
        }

        Run Find(string id)
        {
            Run r;
            return runs.TryGetValue(SccmKeys.App(id), out r) ? r : null;
        }
    }

    // AppDiscovery.log: detection runs. Only adds to applications another source already follows.
    public sealed class AppDiscoveryParser : LogParser
    {
        static readonly Regex RxFound = Rx(@"\+\+\+ Discovered application \[AppDT Id: (?<id>[^,\]]+)");
        static readonly Regex RxNotFound = Rx(@"\+\+\+ Application not discovered\. \[AppDT Id: (?<id>[^,\]]+)");

        public override string Source
        {
            get { return "AppDiscovery"; }
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            Match m = RxFound.Match(l.Text ?? "");
            bool found = m.Success;
            if (!found) m = RxNotFound.Match(l.Text ?? "");
            if (!m.Success) return;
            Obs o = Item(New(l), SccmKeys.App(m.Groups["id"].Value), null, Kinds.Application, Origins.Sccm, Phases.SccmApps);
            o.OnlyIfExists = true;
            o.Text = found ? "Detection: installed" : "Detection: not installed";
            output.Add(o);
        }
    }

    // AppIntentEval.log: which applications this device should have and what it has.
    public sealed class AppIntentEvalParser : LogParser
    {
        static readonly Regex RxIntent = Rx(@"(?<id>ScopeId_[0-9A-Fa-f-]+/DeploymentType_[0-9A-Fa-f-]+)\s*(?:,\s*Revision:?\s*\d+)?\s*:-\s*Current State = (?<cur>\w+), Applicability = (?<ap>\w+), ResolvedState = (?<rs>\w+), ConfigureState = (?<cs>\w+)(?:, Title = (?<t>.+))?$");

        public override string Source
        {
            get { return "AppIntentEval"; }
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            Match m = RxIntent.Match(l.Text ?? "");
            if (!m.Success) return;
            string cur = m.Groups["cur"].Value, ap = m.Groups["ap"].Value, cs = m.Groups["cs"].Value;
            State? st = null;
            if (ap.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase)) st = State.NotApplicable;
            else if (cur.Equals("Installed", StringComparison.OrdinalIgnoreCase) && !cs.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)) st = State.Succeeded;
            else if (cs.Equals("Install", StringComparison.OrdinalIgnoreCase) || cs.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)) st = State.Pending;
            // Available apps that are not installed are not part of a deployment run.
            if (st == null) return;
            string title = m.Groups["t"].Success ? m.Groups["t"].Value.Trim() : null;
            Obs o = Item(New(l), SccmKeys.App(m.Groups["id"].Value), title, Kinds.Application, Origins.Sccm, Phases.SccmApps);
            o.WeakName = true;
            o.State = st;
            o.Text = "Intent: current state " + cur + ", " + Txt.SplitCamel(cs).ToLowerInvariant();
            output.Add(o);
        }
    }

    // execmgr.log: packages and programs.
    public sealed class ExecmgrParser : LogParser
    {
        static readonly Regex RxState = Rx(@"Execution Request for advert (?<adv>\w+) package (?<pkg>\w+) program (?<prog>.{1,1024}?) state change from (?<from>\w+) to (?<to>\w+)");
        static readonly Regex RxDone = Rx(@"Execution is complete for program (?<prog>.{1,1024}?)\. The exit code is (?<c>-?\d+), the execution status is (?<st>\w+)");
        static readonly Regex RxRun = Rx(@"Executing program (?<cmd>.{1,32767}?) in (?:User|Admin|System) context");

        string lastKey, lastName;
        readonly Dictionary<string, string> keyByProgram = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public override string Source
        {
            get { return "execmgr"; }
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            string t = l.Text ?? "";
            Match m;
            if ((m = RxState.Match(t)).Success)
            {
                string pkg = m.Groups["pkg"].Value, prog = m.Groups["prog"].Value.Trim();
                string key = SccmKeys.Package(pkg, prog);
                string name = prog + " (" + pkg + ")";
                lastKey = key;
                lastName = name;
                keyByProgram[prog] = key;
                Obs o = Item(New(l), key, name, Kinds.Package, Origins.Sccm, Phases.SccmPackages);
                o.WeakName = true;
                o.AddInfo("Package", pkg).AddInfo("Program", prog).AddInfo("Deployment", m.Groups["adv"].Value);
                string to = m.Groups["to"].Value;
                switch (to.ToLowerInvariant())
                {
                    case "waitingcontent": o.State = State.Downloading; break;
                    case "ready": case "waitingdependency": case "waitingservicewindow": case "waitinguserlogon": case "waitingdisabled": case "notifyexecution": o.State = State.Pending; break;
                    case "running": case "executing": o.State = State.Installing; o.NewAttempt = !m.Groups["from"].Value.Equals("Running", StringComparison.OrdinalIgnoreCase); break;
                    case "completed": case "succeeded": o.State = State.Succeeded; break;
                    case "failed": case "failure": o.State = State.Failed; o.Severity = 2; break;
                }
                o.Text = "State: " + Txt.SplitCamel(m.Groups["from"].Value) + " → " + Txt.SplitCamel(to);
                output.Add(o);
                return;
            }
            if ((m = RxRun.Match(t)).Success && lastKey != null)
            {
                Obs o = Item(New(l), lastKey, lastName, Kinds.Package, Origins.Sccm, Phases.SccmPackages);
                o.Command = m.Groups["cmd"].Value.Trim();
                output.Add(o);
                return;
            }
            if ((m = RxDone.Match(t)).Success)
            {
                string prog = m.Groups["prog"].Value.Trim();
                string key;
                if (!keyByProgram.TryGetValue(prog, out key)) key = lastKey;
                if (key == null) return;
                Obs o = Item(New(l), key, null, Kinds.Package, Origins.Sccm, Phases.SccmPackages);
                o.ExitCode = Errors.Parse(m.Groups["c"].Value);
                string st = m.Groups["st"].Value;
                if (st.Equals("Success", StringComparison.OrdinalIgnoreCase)) o.State = State.Succeeded;
                else if (st.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0) { o.State = State.Failed; o.Severity = 2; }
                if (o.ExitCode.HasValue && (o.ExitCode.Value == 3010 || o.ExitCode.Value == 1641)) o.Reboot = true;
                output.Add(o);
            }
        }
    }

    // CAS.log: content downloads into the ccmcache.
    public sealed class CasParser : LogParser
    {
        static readonly Regex RxStart = Rx(@"Download started for content (?<c>[^\s,]+)");
        static readonly Regex RxDone = Rx(@"Download completed for content (?<c>[^\s,]+)");
        static readonly Regex RxFail = Rx(@"Download failed for content (?<c>[^\s,]+)(?:.{0,1024}?error (?<e>0x[0-9A-Fa-f]+))?");
        static readonly Regex RxCached = Rx(@"Saved Content ID Mapping (?<c>[^\s,]+), (?<p>.+)$");

        public override string Source
        {
            get { return "CAS"; }
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            string t = l.Text ?? "";
            Match m;
            if ((m = RxStart.Match(t)).Success) { Obs o = Content(l, m.Groups["c"].Value, State.Downloading, true); o.Text = "Download started"; output.Add(o); }
            else if ((m = RxDone.Match(t)).Success) { Obs o = Content(l, m.Groups["c"].Value, State.Succeeded, false); o.Text = "Download completed"; output.Add(o); }
            else if ((m = RxFail.Match(t)).Success)
            {
                Obs o = Content(l, m.Groups["c"].Value, State.Failed, false);
                o.Severity = 2;
                o.Text = "Download failed";
                if (m.Groups["e"].Success) o.ExitCode = Errors.Parse(m.Groups["e"].Value);
                output.Add(o);
            }
            else if ((m = RxCached.Match(t)).Success)
            {
                Obs o = Content(l, m.Groups["c"].Value, null, false);
                o.AddInfo("Cache folder", m.Groups["p"].Value.Trim());
                o.Text = "Cached in " + m.Groups["p"].Value.Trim();
                output.Add(o);
            }
        }

        Obs Content(LogLine l, string id, State? s, bool attempt)
        {
            Obs o = Item(New(l), "content:" + id.ToLowerInvariant(), id, Kinds.Content, Origins.Sccm, Phases.Content);
            o.State = s;
            o.NewAttempt = attempt;
            return o;
        }
    }

    // UpdatesDeployment.log and UpdatesHandler.log: software update installs.
    public sealed class UpdatesParser : LogParser
    {
        static readonly Regex RxAdded = Rx(@"Update \((?<id>[^)]+)\) Name \((?<n>.{1,1024}?)\) ArticleID \((?<kb>\w+)\) added to the targeted list");
        static readonly Regex RxProgress = Rx(@"Update \((?<id>[^)]+)\) Progress: Status = (?<st>\w+)(?:, PercentComplete = (?<p>\d+))?(?:, DownloadSize = \d+)?(?:, Result = (?<r>0x[0-9A-Fa-f]+))?");
        readonly string source;

        public UpdatesParser(string src)
        {
            source = src;
        }

        public override string Source
        {
            get { return source; }
        }

        public override void Read(LogLine l, List<Obs> output)
        {
            string t = l.Text ?? "";
            Match m;
            if ((m = RxAdded.Match(t)).Success)
            {
                Obs o = Item(New(l), SccmKeys.Update(m.Groups["id"].Value), m.Groups["n"].Value.Trim(), Kinds.Update, Origins.Sccm, Phases.SccmUpdates);
                o.State = State.Pending;
                o.AddInfo("Article", "KB" + m.Groups["kb"].Value.TrimStart('K', 'B', 'k', 'b')).AddInfo("Update ID", m.Groups["id"].Value);
                o.Text = "Targeted for install";
                output.Add(o);
                return;
            }
            if ((m = RxProgress.Match(t)).Success)
            {
                Obs o = Item(New(l), SccmKeys.Update(m.Groups["id"].Value), null, Kinds.Update, Origins.Sccm, Phases.SccmUpdates);
                string st = m.Groups["st"].Value;
                o.State = Map(st);
                if (m.Groups["p"].Success)
                {
                    int p;
                    if (int.TryParse(m.Groups["p"].Value, out p)) o.Percent = p;
                }
                if (st.IndexOf("Reboot", StringComparison.OrdinalIgnoreCase) >= 0) o.Reboot = true;
                if (m.Groups["r"].Success)
                {
                    long? r = Errors.Parse(m.Groups["r"].Value);
                    if (r.HasValue && r.Value != 0) o.ExitCode = r;
                }
                if (o.State == State.Failed) o.Severity = 2;
                o.Text = Txt.SplitCamel(st.Replace("ciState", "")) + (o.Percent.HasValue ? ", " + o.Percent + " %" : "");
                output.Add(o);
            }
        }

        static State? Map(string st)
        {
            switch (st.ToLowerInvariant())
            {
                case "cistatedownloading": case "cistatepredownload": return State.Downloading;
                case "cistatewaitinstall": case "cistatewaitservicewindow": case "cistatesubmitted": case "cistatedetecting": return State.Pending;
                case "cistateinstalling": return State.Installing;
                case "cistateverifying": return State.Detecting;
                case "cistateinstallcomplete": return State.Succeeded;
                case "cistatependingsoftreboot": case "cistatependinghardreboot": case "cistatewaitreboot": return State.WaitingReboot;
                case "cistateerror": return State.Failed;
                default: return null;
            }
        }
    }

    // ConfigMgr client WMI (root\ccm\ClientSDK) evaluation states, the same list for applications, programs and updates.
    public static class EvalStates
    {
        public static string Name(int s)
        {
            switch (s)
            {
                case 0: return "None";
                case 1: return "Available";
                case 2: return "Submitted";
                case 3: return "Detecting";
                case 4: return "Pre-download";
                case 5: return "Downloading";
                case 6: return "Waiting to install";
                case 7: return "Installing";
                case 8: return "Pending soft restart";
                case 9: return "Pending hard restart";
                case 10: return "Waiting for restart";
                case 11: return "Verifying";
                case 12: return "Install complete";
                case 13: return "Error";
                case 14: return "Waiting for a maintenance window";
                case 15: return "Waiting for user sign-in";
                case 16: return "Waiting for user sign-out";
                case 17: return "Waiting for user sign-in for the job";
                case 18: return "Waiting for user to reconnect";
                case 19: return "Pending user sign-out";
                case 20: return "Pending update";
                case 21: return "Waiting to retry";
                case 22: return "Waiting for presentation mode off";
                case 23: return "Waiting for orchestration";
                default: return "State " + s.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static State? Map(int s)
        {
            switch (s)
            {
                case 2: case 6: case 14: case 15: case 16: case 17: case 18: case 19: case 20: case 21: case 22: case 23: return State.Pending;
                case 3: case 11: return State.Detecting;
                case 4: case 5: return State.Downloading;
                case 7: return State.Installing;
                case 8: case 9: case 10: return State.WaitingReboot;
                case 12: return State.Succeeded;
                case 13: return State.Failed;
                default: return null;
            }
        }
    }
}
