namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Eventing.Reader;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    // One record from the System event log, as read or as made up by a test.
    public sealed class SysEvent
    {
        public int Id;
        public string Provider;
        public DateTime TimeUtc;
        public long Record;
        public string Message;
        public List<object> Props = new List<object>();

        public string P(int i)
        {
            return i < Props.Count && Props[i] != null ? Convert.ToString(Props[i], CultureInfo.InvariantCulture).Replace("\u200e", "").Replace("\u200f", "").Trim() : "";
        }
    }

    // One restart or shutdown of the device: who asked for it, when it went down and when it came back.
    public sealed class Restart
    {
        public string Key;
        public DateTime FirstUtc;
        public DateTime? RequestUtc, StopUtc, BootUtc;
        public string Requester, Process, Reason, ReasonCode, ShutdownType, Comment, User;
        public bool Unexpected;
        public long? BugCheck;
        public SysEvent StopEvent, BootEvent;
        public readonly List<SysEvent> Events = new List<SysEvent>();

        public TimeSpan? Downtime
        {
            get { return StopUtc.HasValue && BootUtc.HasValue && BootUtc.Value >= StopUtc.Value ? BootUtc.Value - StopUtc.Value : (TimeSpan?)null; }
        }

        // The time the restart is named after: when the device went down, or the first thing seen of it.
        public DateTime AtUtc
        {
            get { return StopUtc ?? RequestUtc ?? BootUtc ?? FirstUtc; }
        }
    }

    // Restarts from the System event log. The IDs and their fields are the ones Windows has logged for many versions:
    // User32 1074 (a process asked for a restart or shutdown: process, computer, reason, reason code, type, comment, user),
    // EventLog 6005 / 6006 (the event log service started / stopped) and 6008 (the shutdown before was unexpected: time, date),
    // Kernel-General 12 / 13 (the OS started at StartTime / is stopping at StopTime) and Kernel-Power 41 (rebooted without
    // a clean shutdown, with BugcheckCode in decimal). See learn.microsoft.com/archive/technet-wiki/14246.kernel-power-event-id-41
    // and learn.microsoft.com/archive/blogs/yongrhee (event 6008).
    public static class Restarts
    {
        public const string Channel = "System";
        public const string User32 = "User32", EventLog = "EventLog", KernelGeneral = "Microsoft-Windows-Kernel-General", KernelPower = "Microsoft-Windows-Kernel-Power";
        const string KeyPrefix = "restart:";
        // How long a restart may wait for Windows to start again before the next request or stop starts another.
        public static readonly TimeSpan OpenGap = TimeSpan.FromMinutes(30);

        static readonly Regex RxIso = new Regex(@"^(?<d>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<f>\d+))?Z?$", RegexOptions.Compiled);

        // The query's condition: only these events from their own providers, so the rest of the System log is not read.
        public static string Filter()
        {
            return "(Provider[@Name='" + User32 + "'] and EventID=1074)"
                + " or (Provider[@Name='" + EventLog + "'] and (EventID=6005 or EventID=6006 or EventID=6008))"
                + " or (Provider[@Name='" + KernelGeneral + "'] and (EventID=12 or EventID=13))"
                + " or (Provider[@Name='" + KernelPower + "'] and EventID=41)";
        }

        public static bool Known(SysEvent e)
        {
            string p = Prov(e);
            if (p == "user32") return e.Id == 1074;
            if (p == "eventlog") return e.Id == 6005 || e.Id == 6006 || e.Id == 6008;
            if (p == "kernel-general") return e.Id == 12 || e.Id == 13;
            if (p == "kernel-power") return e.Id == 41;
            return false;
        }

        static string Prov(SysEvent e)
        {
            string p = (e.Provider ?? "").ToLowerInvariant();
            if (p.StartsWith("microsoft-windows-")) p = p.Substring(18);
            return p;
        }

        static bool IsRequest(SysEvent e) { return Prov(e) == "user32" && e.Id == 1074; }
        static bool IsStop(SysEvent e) { return (Prov(e) == "kernel-general" && e.Id == 13) || (Prov(e) == "eventlog" && e.Id == 6006); }
        static bool IsKernelStart(SysEvent e) { return Prov(e) == "kernel-general" && e.Id == 12; }
        static bool IsLogStart(SysEvent e) { return Prov(e) == "eventlog" && e.Id == 6005; }
        static bool IsUnexpected(SysEvent e) { return (Prov(e) == "eventlog" && e.Id == 6008) || (Prov(e) == "kernel-power" && e.Id == 41); }

        // Who asked for the restart, in plain words, from the process in event 1074 and its user.
        public static string Requester(string process, string reason, string comment, string user)
        {
            string p = (process ?? "").ToLowerInvariant();
            string file = Txt.FileName(p);
            string r = (reason ?? "").ToLowerInvariant();
            if (p.Contains("intune management extension") || file.Contains("intunewindowsagent") || file.Contains("agentexecutor") || file.Contains("intunemanagementextension"))
                return "Microsoft Intune Management Extension";
            if (file.Contains("trustedinstaller") || file.Contains("tiworker") || file.Contains("mousocoreworker") || file.Contains("usoclient") || file.Contains("wuauclt") || file.Contains("musnotification") || file.Contains("sihclient")
                || (file == "svchost.exe" && r.StartsWith("operating system:") && (r.Contains("service pack") || r.Contains("upgrade") || r.Contains("hotfix") || r.Contains("security fix") || r.Contains("reconfiguration"))))
                return "TrustedInstaller / Windows Update";
            if (file.Contains("cloudexperiencehost") || file.Contains("deviceenroller") || file.Contains("wwahost") || file.Contains("msoobe") || p.Contains("\\oobe\\"))
                return "Enrollment Status Page";
            if (file.Contains("omadmclient")) return "MDM client";
            if (file.Contains("smsts") || file.Contains("tsmanager") || file.Contains("tsprogressui") || file.Contains("osdsetuphook") || file.Contains("smsboot"))
                return "smsts / task sequence";
            if (file.Contains("ccmexec") || file.Contains("ccmrestart") || file.Contains("scnotification")) return "ConfigMgr client";
            if (file == "winlogon.exe" || file == "explorer.exe" || file.Contains("startmenuexperiencehost") || file.Contains("shellexperiencehost") || file == "shutdown.exe")
                return IsSystemUser(user) ? (file.Length > 0 ? file : "Windows") : "user";
            return file.Length > 0 ? file : "unknown";
        }

        static bool IsSystemUser(string user)
        {
            string u = (user ?? "").Trim().ToUpperInvariant();
            return u.Length == 0 || u.StartsWith("NT AUTHORITY\\") || u == "SYSTEM" || u.EndsWith("$");
        }

        // "C:\Windows\system32\winlogon.exe (CONTOSO-PC01)" without the computer name.
        static string ProcessPath(string s)
        {
            if (s == null) return "";
            int k = s.LastIndexOf(" (", StringComparison.Ordinal);
            return k > 0 && s.EndsWith(")") ? s.Substring(0, k) : s;
        }

        // Groups the events into restarts. A restart is what asked for it and what stopped, up to the next start of Windows,
        // with the unexpected-shutdown events Windows writes just after that start.
        public static List<Restart> Build(IEnumerable<SysEvent> input)
        {
            List<SysEvent> evs = new List<SysEvent>();
            foreach (SysEvent e in input) if (e != null && Known(e)) evs.Add(e);
            evs.Sort(delegate (SysEvent a, SysEvent b)
            {
                int c = a.TimeUtc.CompareTo(b.TimeUtc);
                return c != 0 ? c : a.Record.CompareTo(b.Record);
            });
            List<Restart> list = new List<Restart>();
            Restart open = null, last = null;
            foreach (SysEvent e in evs)
            {
                if (IsRequest(e) || IsStop(e))
                {
                    // A restart that was asked for but never happened, or a gap in the log: Windows did not start again
                    // before this next request, so the open one is closed without a start.
                    if (open != null && e.TimeUtc - open.Events[open.Events.Count - 1].TimeUtc > OpenGap) open = null;
                    if (open == null) { open = New(e); list.Add(open); }
                    open.Events.Add(e);
                    continue;
                }
                if (IsKernelStart(e) || IsLogStart(e))
                {
                    // The event log service starts a few seconds after the kernel: the same start.
                    if (IsLogStart(e) && open == null && last != null && last.BootUtc.HasValue && e.TimeUtc - last.BootEvent.TimeUtc < TimeSpan.FromMinutes(3) && IsKernelStart(last.BootEvent))
                    {
                        last.Events.Add(e);
                        continue;
                    }
                    if (open == null) { open = New(e); list.Add(open); }
                    open.Events.Add(e);
                    open.BootEvent = e;
                    open.BootUtc = BootTime(e);
                    last = open;
                    open = null;
                    continue;
                }
                if (IsUnexpected(e))
                {
                    // Written after the start that followed the unexpected stop. Without that start in the history it is left out.
                    if (last != null && open == null && e.TimeUtc - last.BootEvent.TimeUtc < TimeSpan.FromMinutes(15)) last.Events.Add(e);
                }
            }
            foreach (Restart r in list) Fill(r);
            return list;
        }

        static Restart New(SysEvent e)
        {
            Restart r = new Restart();
            r.FirstUtc = e.TimeUtc;
            r.Key = KeyPrefix + e.TimeUtc.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            return r;
        }

        static void Fill(Restart r)
        {
            SysEvent kernelStop = null, logStop = null, request = null, unexpected = null;
            foreach (SysEvent e in r.Events)
            {
                if (IsRequest(e)) request = e;
                else if (Prov(e) == "kernel-general" && e.Id == 13) kernelStop = e;
                else if (Prov(e) == "eventlog" && e.Id == 6006) logStop = e;
                else if (Prov(e) == "eventlog" && e.Id == 6008)
                {
                    r.Unexpected = true;
                    if (unexpected == null) unexpected = e;
                }
                else if (Prov(e) == "kernel-power" && e.Id == 41)
                {
                    r.Unexpected = true;
                    long? code = BugCheck(e);
                    if (code.HasValue) r.BugCheck = code;
                }
            }
            if (request != null)
            {
                r.RequestUtc = request.TimeUtc;
                r.Process = ProcessPath(request.P(0));
                r.Reason = request.P(2);
                r.ReasonCode = request.P(3);
                r.ShutdownType = request.P(4);
                r.Comment = request.P(5);
                r.User = request.P(6);
                r.Requester = Requester(r.Process, r.Reason, r.Comment, r.User);
            }
            else r.Requester = r.Unexpected ? "unexpected, no clean shutdown" : "unknown";
            if (kernelStop != null) { r.StopEvent = kernelStop; r.StopUtc = TimeProp(kernelStop) ?? kernelStop.TimeUtc; }
            else if (logStop != null) { r.StopEvent = logStop; r.StopUtc = logStop.TimeUtc; }
            else if (unexpected != null)
            {
                DateTime? t = UnexpectedTime(unexpected);
                if (t.HasValue && (!r.BootUtc.HasValue || t.Value <= r.BootUtc.Value)) { r.StopEvent = unexpected; r.StopUtc = t; }
            }
        }

        // BugcheckCode of event 41, in decimal. Zero means no stop error was recorded.
        static long? BugCheck(SysEvent e)
        {
            long n;
            return long.TryParse(e.P(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n != 0 ? n : (long?)null;
        }

        static DateTime BootTime(SysEvent e)
        {
            if (IsKernelStart(e)) return TimeProp(e) ?? e.TimeUtc;
            return e.TimeUtc;
        }

        // StartTime of event 12 and StopTime of event 13: a time property, or its text as the event XML has it.
        static DateTime? TimeProp(SysEvent e)
        {
            foreach (object o in e.Props)
            {
                if (o is DateTime)
                {
                    DateTime d = (DateTime)o;
                    if (d.Year < 2001) continue;
                    return d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
                }
                string s = o as string;
                if (s == null) continue;
                Match m = RxIso.Match(s.Replace("\u200e", "").Trim());
                if (!m.Success) continue;
                DateTime t;
                if (!DateTime.TryParseExact(m.Groups["d"].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) continue;
                string f = m.Groups["f"].Value;
                if (f.Length > 0)
                {
                    f = (f + "0000000").Substring(0, 7);
                    t = t.AddTicks(long.Parse(f, CultureInfo.InvariantCulture));
                }
                if (t.Year >= 2001) return t;
            }
            return null;
        }

        // Event 6008 has the local time and date of the stop as text, in the device's own format.
        public static Func<string, DateTime?> LocalToUtc = delegate (string s)
        {
            DateTime t;
            foreach (CultureInfo c in new CultureInfo[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
                if (DateTime.TryParse(s, c, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out t)) return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return null;
        };

        static DateTime? UnexpectedTime(SysEvent e)
        {
            string time = e.P(0), date = e.P(1);
            if (time.Length == 0 || date.Length == 0) return null;
            return LocalToUtc(date + " " + time);
        }

        public static string Name(Restart r, TimeZoneInfo zone)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(r.AtUtc, zone ?? TimeZoneInfo.Local);
            return "Restart at " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        // What a restart adds to its item: plain pairs for the Details pane.
        public static List<Pair> Info(Restart r)
        {
            List<Pair> l = new List<Pair>();
            l.Add(new Pair("Requested by", r.Requester));
            if (!string.IsNullOrEmpty(r.Process)) l.Add(new Pair("Process", r.Process));
            if (!string.IsNullOrEmpty(r.User)) l.Add(new Pair("On behalf of", r.User));
            if (!string.IsNullOrEmpty(r.Reason)) l.Add(new Pair("Reason", r.Reason + (string.IsNullOrEmpty(r.ReasonCode) ? "" : " (" + r.ReasonCode + ")")));
            if (!string.IsNullOrEmpty(r.ShutdownType)) l.Add(new Pair("Shutdown type", r.ShutdownType));
            if (!string.IsNullOrEmpty(r.Comment)) l.Add(new Pair("Comment", r.Comment));
            if (r.StopUtc.HasValue) l.Add(new Pair("Stopped", Txt.Local(r.StopUtc.Value)));
            if (r.BootUtc.HasValue) l.Add(new Pair("Started again", Txt.Local(r.BootUtc.Value)));
            if (r.Downtime.HasValue) l.Add(new Pair("Downtime", Txt.Duration(r.Downtime)));
            if (r.Unexpected) l.Add(new Pair("Clean shutdown", r.BugCheck.HasValue ? "No, stop error 0x" + unchecked((uint)r.BugCheck.Value).ToString("X8", CultureInfo.InvariantCulture) : "No"));
            return l;
        }

        // The line an event adds to the timeline.
        public static string Text(SysEvent e, Restart r)
        {
            string p = Prov(e);
            if (p == "user32")
            {
                string type = e.P(4).ToLowerInvariant().Contains("power off") || e.P(4).ToLowerInvariant().Contains("shutdown") ? "Shutdown" : "Restart";
                string s = type + " requested by " + (r != null ? r.Requester : Requester(ProcessPath(e.P(0)), e.P(2), e.P(5), e.P(6)));
                string proc = Txt.FileName(ProcessPath(e.P(0)));
                if (proc.Length > 0 && r != null && !string.Equals(proc, r.Requester, StringComparison.OrdinalIgnoreCase)) s += " (" + proc + ")";
                if (e.P(2).Length > 0) s += ". Reason: " + e.P(2);
                if (e.P(5).Length > 0) s += ". Comment: " + Txt.Short(e.P(5), 200);
                return s;
            }
            if (p == "kernel-general" && e.Id == 13) return "Windows is shutting down";
            if (p == "kernel-general" && e.Id == 12) return r != null && r.Downtime.HasValue ? "Windows started, " + Txt.Duration(r.Downtime) + " after it stopped" : "Windows started";
            if (p == "eventlog" && e.Id == 6006) return "The event log service stopped";
            if (p == "eventlog" && e.Id == 6005) return "The event log service started";
            if (p == "eventlog" && e.Id == 6008)
            {
                DateTime? t = UnexpectedTime(e);
                return "The shutdown before this start was unexpected" + (t.HasValue ? ": Windows stopped at " + Txt.LocalTime(t.Value) + " without shutting down" : "");
            }
            if (p == "kernel-power" && e.Id == 41)
            {
                long? code = BugCheck(e);
                if (code.HasValue) return "Restarted after a stop error (bug check 0x" + unchecked((uint)code.Value).ToString("X8", CultureInfo.InvariantCulture) + ")";
                return "Restarted without a clean shutdown: the device stopped responding, crashed or lost power";
            }
            return "Event " + e.Id;
        }

        public static bool IsRestart(Item i)
        {
            return i != null && i.PhaseKey == Phases.Restarts;
        }
    }

    // Turns System log events into observations as they arrive. Each event goes on the timeline once, and the restart
    // it belongs to is given its name, state and details again when they change.
    public sealed class RestartWatch
    {
        readonly List<SysEvent> all = new List<SysEvent>();
        readonly HashSet<string> seen = new HashSet<string>();
        readonly Dictionary<string, string> said = new Dictionary<string, string>();
        long lastRecord;
        public TimeZoneInfo Zone = TimeZoneInfo.Local;
        // Events before this are not part of the deployment: the System log keeps the restarts of the device's whole life.
        public DateTime? Floor;
        // The time now, on the device itself. Set when the events are read live: Windows is running, so a restart that has not
        // started again after OpenGap never will.
        public Func<DateTime> Clock;

        public List<Obs> Add(IEnumerable<SysEvent> events)
        {
            List<SysEvent> fresh = new List<SysEvent>();
            foreach (SysEvent e in events)
            {
                if (e == null || !Restarts.Known(e)) continue;
                if (Floor.HasValue && e.TimeUtc < Floor.Value) continue;
                string id = e.Provider + "|" + e.Id + "|" + e.Record + "|" + e.TimeUtc.Ticks;
                if (!seen.Add(id)) continue;
                all.Add(e);
                fresh.Add(e);
            }
            List<Obs> output = new List<Obs>();
            if (fresh.Count == 0) return output;
            List<Restart> rs = Restarts.Build(all);
            for (int k = 0; k < rs.Count; k++)
            {
                Restart r = rs[k];
                string name = Restarts.Name(r, Zone);
                StringBuilder sig = new StringBuilder(name);
                List<Pair> info = Restarts.Info(r);
                DateTime? closed = Closed(r, k + 1 < rs.Count ? rs[k + 1] : null);
                if (closed.HasValue) info.Add(new Pair("Started again", "Not logged"));
                foreach (Pair p in info) sig.Append('|').Append(p.Key).Append('=').Append(p.Value);
                string old;
                if (!said.TryGetValue(r.Key, out old) || old != sig.ToString())
                {
                    said[r.Key] = sig.ToString();
                    Obs o = For(r, name, r.FirstUtc);
                    o.Info = info;
                    output.Add(o);
                }
                foreach (SysEvent e in r.Events)
                {
                    if (!fresh.Contains(e)) continue;
                    output.Add(EventObs(e, r, name));
                }
                if (closed.HasValue && !said.ContainsKey(r.Key + "|closed"))
                {
                    said[r.Key + "|closed"] = "";
                    Obs c = For(r, name, closed.Value);
                    c.State = State.Skipped;
                    c.Text = "No start of Windows was logged after this. The restart was cancelled, or the log has a gap";
                    output.Add(c);
                }
            }
            return output;
        }

        // When a restart without a start of Windows is known to be over: when the next restart began, or, on the device
        // itself, once OpenGap has passed. Null while it may still be going on.
        DateTime? Closed(Restart r, Restart next)
        {
            if (r.BootUtc.HasValue) return null;
            DateTime last = r.Events.Count > 0 ? r.Events[r.Events.Count - 1].TimeUtc : r.FirstUtc;
            if (next != null) return next.FirstUtc > last ? next.FirstUtc : last;
            if (Clock != null && Clock() - last > Restarts.OpenGap) return last.Add(Restarts.OpenGap);
            return null;
        }

        Obs For(Restart r, string name, DateTime t)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "Restarts";
            o.ItemKey = r.Key;
            o.ItemName = name;
            o.ItemKind = Kinds.Restart;
            o.Origin = Origins.Windows;
            o.PhaseKey = Phases.Restarts;
            return o;
        }

        Obs EventObs(SysEvent e, Restart r, string name)
        {
            bool stop = ReferenceEquals(e, r.StopEvent), boot = ReferenceEquals(e, r.BootEvent);
            // The stop of an unexpected shutdown is only known from the event written after the start, so it is put at the stop.
            Obs o = For(r, name, stop && r.StopUtc.HasValue ? r.StopUtc.Value : boot && r.BootUtc.HasValue ? r.BootUtc.Value : e.TimeUtc);
            o.Text = Restarts.Text(e, r);
            if (stop) o.State = State.Installing;
            else if (boot) o.State = State.Succeeded;
            bool power = e.Id == 41;
            o.Severity = power ? 2 : e.Id == 6008 ? 1 : 0;
            string msg = string.IsNullOrEmpty(e.Message) ? o.Text : e.Message.Trim();
            o.Evidence = new Evidence(EvidenceKinds.EventRecord, Restarts.Channel, (int)Math.Min(int.MaxValue, Math.Max(0, e.Record)), msg);
            return o;
        }

        // Reads the restart events from the System log: only their IDs, from sinceUtc on the first read and new records after it.
        public List<Obs> Read(DateTime? sinceUtc)
        {
            StringBuilder q = new StringBuilder("*[System[(");
            q.Append(Restarts.Filter()).Append(")");
            if (lastRecord > 0) q.Append(" and (EventRecordID>").Append(lastRecord.ToString(CultureInfo.InvariantCulture)).Append(")");
            else if (sinceUtc.HasValue) q.Append(" and TimeCreated[@SystemTime>='").Append(sinceUtc.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append("']");
            q.Append("]]");
            List<SysEvent> list = new List<SysEvent>();
            EventLogQuery query = new EventLogQuery(Restarts.Channel, PathType.LogName, q.ToString());
            using (EventLogReader r = new EventLogReader(query))
            {
                int n = 0;
                for (EventRecord e = r.ReadEvent(); e != null && n < 2000; e = r.ReadEvent(), n++)
                {
                    using (e)
                    {
                        SysEvent s = new SysEvent();
                        s.Id = e.Id;
                        s.Provider = e.ProviderName;
                        s.Record = e.RecordId ?? 0;
                        s.TimeUtc = e.TimeCreated.HasValue ? e.TimeCreated.Value.ToUniversalTime() : DateTime.UtcNow;
                        try { s.Message = e.FormatDescription(); } catch (Exception) { }
                        try { foreach (EventProperty p in e.Properties) s.Props.Add(p.Value); } catch (Exception) { }
                        if (s.Record > lastRecord) lastRecord = s.Record;
                        list.Add(s);
                    }
                }
            }
            return Add(list);
        }
    }
}
