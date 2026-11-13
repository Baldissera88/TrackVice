namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Eventing.Reader;
    using System.Globalization;
    using System.IO;
    using System.Management;
    using System.Text;
    using Microsoft.Win32;

    // One log file and the parser that reads it.
    sealed class Feed
    {
        public LogTail Tail;
        public LogParser Parser;
    }

    // What one read of the device returned.
    public sealed class Batch
    {
        public List<Obs> Obs = new List<Obs>();
        public List<Pair> Aliases = new List<Pair>();
        public Dictionary<string, string> Names;
        public List<Pair> Info = new List<Pair>();
        public List<string> Notes = new List<string>();
        public List<string> Sources = new List<string>();
        // A whole session read from somewhere else, which takes the place of the current one.
        public Session Replace;
    }

    // Reads this device: logs, ESP and IME registry keys, ConfigMgr client WMI and event logs. The first read takes
    // everything since SinceUtc, every later read only what is new. Runs on a background thread.
    public sealed class LiveCollector : IDisposable
    {
        public DateTime SinceUtc;
        public bool UseWmi = true, UseEvents = true;
        readonly List<Feed> feeds = new List<Feed>();
        readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly SmstsParser smsts = new SmstsParser();
        readonly EspWatch esp = new EspWatch();
        readonly Win32AppWatch win32 = new Win32AppWatch();
        readonly MdmWatch mdm = new MdmWatch();
        readonly WmiWatch wmi = new WmiWatch();
        readonly EventWatch events = new EventWatch();
        readonly LiveRegistry reg = new LiveRegistry();
        readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int reads;
        string lastInfo;

        public LiveCollector(DateTime sinceUtc)
        {
            SinceUtc = sinceUtc;
            Func<string, string> n = delegate (string id) { string v; return id != null && names.TryGetValue(id, out v) ? v : null; };
            esp.Names = n;
            win32.Names = n;
        }

        public static string CcmLogFolder
        {
            get
            {
                try
                {
                    object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\CCM\Logging\@Global", "LogDirectory", null);
                    string s = v as string;
                    if (!string.IsNullOrEmpty(s)) return s.TrimEnd('\\');
                }
                catch (Exception) { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"CCM\Logs");
            }
        }

        public static string ImeLogFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\IntuneManagementExtension\Logs"); }
        }

        // Every place smsts.log lives during a task sequence, in the order it moves through them.
        public static List<string> SmstsPaths()
        {
            List<string> l = new List<string>();
            l.Add(@"X:\Windows\Temp\SMSTSLog\smsts.log");
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        l.Add(Path.Combine(d.RootDirectory.FullName, @"_SMSTaskSequence\Logs\Smstslog\smsts.log"));
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            string ccm = CcmLogFolder;
            l.Add(Path.Combine(ccm, @"SMSTSLog\smsts.log"));
            l.Add(Path.Combine(ccm, "smsts.log"));
            l.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Temp\SMSTSLog\smsts.log"));
            return l;
        }

        void Discover(Batch b)
        {
            foreach (string p in SmstsPaths()) Add(p, smsts, b);
            string ccm = CcmLogFolder;
            foreach (string n in LogParser.SccmLogs) AddWithHistory(Path.Combine(ccm, n + ".log"), b);
            string ime = ImeLogFolder;
            foreach (string n in LogParser.ImeLogs) AddWithHistory(Path.Combine(ime, n + ".log"), b);
        }

        void AddWithHistory(string path, Batch b)
        {
            if (known.Contains(path)) return;
            if (!File.Exists(path)) return;
            LogParser parser = LogParser.For(CmLog.BaseName(path));
            if (parser == null) return;
            // Rolled-over copies written since SinceUtc are read once, oldest first, through the same parser.
            try
            {
                string dir = Path.GetDirectoryName(path), bn = CmLog.BaseName(path);
                List<FileInfo> old = new List<FileInfo>();
                foreach (FileInfo f in new DirectoryInfo(dir).GetFiles(bn + "*"))
                {
                    if (string.Equals(f.FullName, path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(CmLog.BaseName(f.FullName), bn, StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.LastWriteTimeUtc < SinceUtc) continue;
                    old.Add(f);
                }
                old.Sort(delegate (FileInfo x, FileInfo y) { return x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc); });
                foreach (FileInfo f in old)
                {
                    foreach (LogLine l in LogTail.ReadAll(f.FullName, null)) Feed(parser, l, b);
                    b.Sources.Add(f.FullName);
                }
            }
            catch (Exception) { }
            Add(path, parser, b);
        }

        void Add(string path, LogParser parser, Batch b)
        {
            if (known.Contains(path)) return;
            bool exists;
            try { exists = File.Exists(path); } catch (Exception) { exists = false; }
            if (!exists) return;
            known.Add(path);
            Feed f = new Feed();
            f.Tail = new LogTail(path);
            f.Parser = parser;
            feeds.Add(f);
            b.Sources.Add(path);
        }

        void Feed(LogParser p, LogLine l, Batch b)
        {
            int before = b.Obs.Count;
            p.Read(l, b.Obs);
            // Lines from before the window still move the parser's state, but are not shown.
            for (int i = b.Obs.Count - 1; i >= before; i--)
                if (b.Obs[i].TimeUtc != DateTime.MinValue && b.Obs[i].TimeUtc < SinceUtc && b.Obs[i].SessionInfo == null) b.Obs.RemoveAt(i);
        }

        public Batch Read(DateTime nowUtc)
        {
            Batch b = new Batch();
            reads++;
            if (reads == 1 || reads % 5 == 0) Discover(b);
            if (reads == 1 || reads % 15 == 0)
            {
                Dictionary<string, string> n = ImeLogParser.NamesFromFolder(ImeLogFolder);
                bool changed = false;
                foreach (KeyValuePair<string, string> kv in n)
                {
                    string old;
                    if (!names.TryGetValue(kv.Key, out old) || old != kv.Value) { names[kv.Key] = kv.Value; changed = true; }
                }
                if (changed || reads == 1) b.Names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
            }
            foreach (Feed f in feeds)
            {
                List<LogLine> lines;
                try { lines = f.Tail.Read(); }
                catch (Exception) { continue; }
                foreach (LogLine l in lines) Feed(f.Parser, l, b);
                if (f.Tail.Note != null)
                {
                    b.Notes.Add(f.Tail.Note);
                    f.Tail.Note = null;
                }
            }
            b.Obs.AddRange(esp.Read(reg, nowUtc));
            if (reads == 1 || reads % 3 == 0) b.Obs.AddRange(win32.Read(reg, nowUtc));
            if (reads == 1 || reads % 2 == 0) b.Obs.AddRange(mdm.Read(reg, nowUtc));
            if (reads == 1 || reads % 10 == 0)
            {
                List<Pair> info = AutopilotInfo.Read(reg);
                StringBuilder sig = new StringBuilder();
                foreach (Pair p in info) sig.Append(p.Key).Append('=').Append(p.Value).Append(';');
                if (sig.ToString() != lastInfo)
                {
                    lastInfo = sig.ToString();
                    b.Info.AddRange(info);
                }
            }
            if (UseWmi && (reads == 1 || reads % 3 == 0))
            {
                Exception err;
                List<Obs> w = null;
                List<Pair> al = null;
                if (Guard.Run(delegate { w = wmi.Read(nowUtc, out al); }, TimeSpan.FromSeconds(20), out err))
                {
                    if (w != null) b.Obs.AddRange(w);
                    if (al != null) b.Aliases.AddRange(al);
                }
                else b.Notes.Add("The ConfigMgr client WMI did not answer within 20 seconds, so application states from WMI were skipped.");
            }
            if (UseEvents)
            {
                if (reads == 1 || reads % 10 == 0)
                {
                    try { foreach (string id in EventWatch.ReadEnrollments(reg)) events.Enrollments.Add(id); }
                    catch (Exception) { }
                }
                try { b.Obs.AddRange(events.Read(reads == 1 ? SinceUtc : (DateTime?)null)); }
                catch (Exception) { }
            }
            if (reg.Denied.Count > 0 && reads == 1) b.Notes.Add("Some registry keys could not be read. Run TrackVice as administrator to see every ESP and Intune app state.");
            return b;
        }

        public void Dispose()
        {
            try { reg.Dispose(); } catch (Exception) { }
        }
    }

    // ConfigMgr client WMI, root\ccm\ClientSDK: applications, programs and software updates, with their evaluation state.
    public sealed class WmiWatch
    {
        readonly Dictionary<string, string> last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, bool> aliased = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool absent;

        public List<Obs> Read(DateTime nowUtc, out List<Pair> aliases)
        {
            List<Obs> list = new List<Obs>();
            List<Pair> al = new List<Pair>();
            aliases = al;
            if (absent) return list;
            ManagementScope scope = new ManagementScope(@"\\.\root\ccm\ClientSDK");
            try { scope.Connect(); }
            catch (Exception)
            {
                absent = true;
                return list;
            }
            EnumerationOptions opt = new EnumerationOptions();
            opt.Timeout = TimeSpan.FromSeconds(15);
            opt.ReturnImmediately = false;
            Query(scope, "SELECT * FROM CCM_Application", opt, delegate (ManagementObject o)
            {
                int ev = Int(o, "EvaluationState");
                string id = Str(o, "Id"), name = Str(o, "Name"), install = Str(o, "InstallState");
                long err = Long(o, "ErrorCode");
                bool installed = string.Equals(install, "Installed", StringComparison.OrdinalIgnoreCase);
                if (ev < 2 && err == 0 && !installed) return;
                Obs x = Make(nowUtc, SccmKeys.App(id), name, Kinds.Application, Phases.SccmApps, ev, Int(o, "PercentComplete"), err);
                x.OnlyIfExists = ev < 2 && err == 0;
                if (installed && ev < 2) x.State = State.Succeeded;
                x.AddInfo("Application ID", id).AddInfo("Revision", Str(o, "Revision")).AddInfo("Install state", install);
                x.Evidence = new Evidence(EvidenceKinds.Wmi, "root\\ccm\\ClientSDK:CCM_Application", 0, "Id=" + id + ", EvaluationState=" + ev + ", InstallState=" + install + ", ErrorCode=" + err);
                Add(list, x);
                if (!aliased.ContainsKey(id)) AddAliases(o, id, al);
            });
            Query(scope, "SELECT * FROM CCM_Program", opt, delegate (ManagementObject o)
            {
                if (Bool(o, "TaskSequence")) return;
                int ev = Int(o, "EvaluationState");
                long err = Long(o, "ErrorCode");
                if (ev < 2 && err == 0) return;
                string pkg = Str(o, "PackageID"), prog = Str(o, "ProgramID");
                Obs x = Make(nowUtc, SccmKeys.Package(pkg, prog), prog + " (" + (Str(o, "PackageName") ?? pkg) + ")", Kinds.Package, Phases.SccmPackages, ev, Int(o, "PercentComplete"), err);
                x.WeakName = true;
                x.AddInfo("Package", pkg).AddInfo("Program", prog);
                x.Evidence = new Evidence(EvidenceKinds.Wmi, "root\\ccm\\ClientSDK:CCM_Program", 0, "PackageID=" + pkg + ", ProgramID=" + prog + ", EvaluationState=" + ev);
                Add(list, x);
            });
            Query(scope, "SELECT * FROM CCM_SoftwareUpdate", opt, delegate (ManagementObject o)
            {
                int ev = Int(o, "EvaluationState");
                long err = Long(o, "ErrorCode");
                if (ev < 2 && err == 0) return;
                string uid = Str(o, "UpdateID");
                Obs x = Make(nowUtc, SccmKeys.Update(uid), Str(o, "Name"), Kinds.Update, Phases.SccmUpdates, ev, Int(o, "PercentComplete"), err);
                x.AddInfo("Article", "KB" + Str(o, "ArticleID")).AddInfo("Update ID", uid);
                x.Evidence = new Evidence(EvidenceKinds.Wmi, "root\\ccm\\ClientSDK:CCM_SoftwareUpdate", 0, "UpdateID=" + uid + ", EvaluationState=" + ev);
                Add(list, x);
            });
            return list;
        }

        void Add(List<Obs> list, Obs o)
        {
            string sig = (o.State.HasValue ? o.State.Value.ToString() : "") + "|" + o.Percent + "|" + o.ExitCode;
            string old;
            if (last.TryGetValue(o.ItemKey, out old) && old == sig) return;
            last[o.ItemKey] = sig;
            list.Add(o);
        }

        void AddAliases(ManagementObject o, string appId, List<Pair> aliases)
        {
            aliased[appId] = true;
            try
            {
                o.Get();
                ManagementBaseObject[] dts = o["AppDTs"] as ManagementBaseObject[];
                if (dts == null) return;
                foreach (ManagementBaseObject dt in dts)
                {
                    string dtId = Convert.ToString(dt["Id"], CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(dtId)) aliases.Add(new Pair(SccmKeys.App(dtId), SccmKeys.App(appId)));
                }
            }
            catch (Exception) { }
        }

        static Obs Make(DateTime now, string key, string name, string kind, string phase, int ev, int pct, long err)
        {
            Obs o = new Obs();
            o.TimeUtc = now;
            o.Source = "WMI";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = kind;
            o.Origin = Origins.Sccm;
            o.PhaseKey = phase;
            o.State = EvalStates.Map(ev);
            if (pct > 0 && pct <= 100) o.Percent = pct;
            if (err != 0)
            {
                o.ExitCode = err;
                if (o.State == null) o.State = State.Failed;
            }
            if (o.State == State.Failed) o.Severity = 2;
            o.Text = "WMI: " + EvalStates.Name(ev) + (o.Percent.HasValue ? ", " + o.Percent + " %" : "") + (err != 0 ? ", " + Errors.Short(err) : "");
            return o;
        }

        static void Query(ManagementScope scope, string wql, EnumerationOptions opt, Action<ManagementObject> each)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new ObjectQuery(wql), opt))
                using (ManagementObjectCollection c = s.Get())
                {
                    foreach (ManagementBaseObject b in c)
                    {
                        ManagementObject o = b as ManagementObject;
                        if (o == null) continue;
                        try { each(o); }
                        catch (Exception) { }
                        finally { o.Dispose(); }
                    }
                }
            }
            catch (Exception) { }
        }

        static string Str(ManagementBaseObject o, string p)
        {
            try { object v = o[p]; return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        static int Int(ManagementBaseObject o, string p)
        {
            try { object v = o[p]; return v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        static long Long(ManagementBaseObject o, string p)
        {
            try
            {
                object v = o[p];
                if (v == null) return 0;
                long n = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                return n > int.MaxValue && n <= uint.MaxValue ? unchecked((int)(uint)n) : n;
            }
            catch (Exception) { return 0; }
        }

        static bool Bool(ManagementBaseObject o, string p)
        {
            try { object v = o[p]; return v is bool && (bool)v; }
            catch (Exception) { return false; }
        }
    }

    // Autopilot, MDM and device registration event logs. They go on the timeline; MDM errors also become a finding.
    public sealed class EventWatch
    {
        public static readonly string[][] Channels =
        {
            new string[] { "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot", "Autopilot", "all" },
            new string[] { "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin", "MDM", "errors+policy" },
            new string[] { "Microsoft-Windows-User Device Registration/Admin", "Device registration", "errors" },
            new string[] { "Microsoft-Windows-AAD/Operational", "Entra ID", "errors" },
        };

        readonly Dictionary<string, long> lastRecord = new Dictionary<string, long>();
        // The device's MDM enrollments. Policies set by anything else, such as the Windows image, are left out.
        public readonly HashSet<string> Enrollments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public const string EnrollmentsKey = @"HKLM\SOFTWARE\Microsoft\Enrollments";

        public static List<string> ReadEnrollments(IRegSource reg)
        {
            List<string> l = new List<string>();
            foreach (string id in reg.SubKeys(EnrollmentsKey))
            {
                RegValue v = reg.Get(EnrollmentsKey + "\\" + id, "ProviderID");
                if (v != null && !string.IsNullOrEmpty(v.Data)) l.Add(id);
            }
            return l;
        }

        public bool FromEnrollment(IList<object> props)
        {
            if (Enrollments.Count == 0) return true;
            return props.Count > 2 && Enrollments.Contains(Convert.ToString(props[2], CultureInfo.InvariantCulture) ?? "");
        }

        public List<Obs> Read(DateTime? sinceUtc)
        {
            List<Obs> list = new List<Obs>();
            foreach (string[] ch in Channels)
            {
                try { ReadChannel(ch[0], ch[1], ch[2], sinceUtc, list); }
                catch (Exception) { }
            }
            return list;
        }

        void ReadChannel(string channel, string source, string what, DateTime? sinceUtc, List<Obs> list)
        {
            bool errorsOnly = what != "all", policy = what == "errors+policy";
            long after;
            lastRecord.TryGetValue(channel, out after);
            StringBuilder q = new StringBuilder("*[System[");
            q.Append(policy ? "(Level=1 or Level=2 or Level=3 or EventID=813 or EventID=814)" : errorsOnly ? "(Level=1 or Level=2 or Level=3)" : "(Level>=0)");
            if (after > 0) q.Append(" and (EventRecordID>").Append(after.ToString(CultureInfo.InvariantCulture)).Append(")");
            else if (sinceUtc.HasValue) q.Append(" and TimeCreated[@SystemTime>='").Append(sinceUtc.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append("']");
            q.Append("]]");
            EventLogQuery query = new EventLogQuery(channel, PathType.LogName, q.ToString());
            int n = 0;
            using (EventLogReader r = new EventLogReader(query))
            {
                for (EventRecord e = r.ReadEvent(); e != null && n < 4000; e = r.ReadEvent(), n++)
                {
                    using (e)
                    {
                        long rec = e.RecordId ?? 0;
                        if (rec > after) lastRecord[channel] = Math.Max(rec, lastRecord.ContainsKey(channel) ? lastRecord[channel] : 0);
                        string msg = null;
                        try { msg = e.FormatDescription(); } catch (Exception) { }
                        if (policy && (e.Id == MdmEvents.SetInt || e.Id == MdmEvents.SetString))
                        {
                            // A policy the MDM client set: it goes under its policy area, not on the list of errors.
                            try
                            {
                                List<object> props = new List<object>();
                                foreach (EventProperty p in e.Properties) props.Add(p.Value);
                                if (!FromEnrollment(props)) continue;
                                Obs po = MdmEvents.Map(e.Id, props, e.TimeCreated.HasValue ? e.TimeCreated.Value.ToUniversalTime() : DateTime.UtcNow, channel, rec, msg);
                                if (po != null) list.Add(po);
                            }
                            catch (Exception) { }
                            continue;
                        }
                        if (string.IsNullOrEmpty(msg)) msg = "Event " + e.Id;
                        Obs o = new Obs();
                        o.TimeUtc = e.TimeCreated.HasValue ? e.TimeCreated.Value.ToUniversalTime() : DateTime.UtcNow;
                        o.Source = source;
                        byte lvl = e.Level ?? 4;
                        o.Severity = lvl <= 2 ? 2 : lvl == 3 ? 1 : 0;
                        o.Text = "Event " + e.Id + ": " + msg.Trim();
                        o.Evidence = new Evidence(EvidenceKinds.EventRecord, channel, (int)Math.Min(int.MaxValue, rec), msg.Trim());
                        list.Add(o);
                    }
                }
            }
        }
    }

    // A folder of logs: a CCM Logs folder, IME logs, or a collected bundle. Every known log in it is read once.
    public static class FolderReader
    {
        public static Batch Read(string folder, TimeZoneInfo zone)
        {
            Batch b = new Batch();
            List<FileInfo> files = new List<FileInfo>();
            foreach (FileInfo f in new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories))
            {
                string ext = f.Extension.ToLowerInvariant();
                if (ext != ".log" && ext != ".lo_") continue;
                if (LogParser.For(CmLog.BaseName(f.FullName)) == null) continue;
                files.Add(f);
            }
            // Oldest first per log, so a rolled-over copy comes before the current one.
            files.Sort(delegate (FileInfo x, FileInfo y) { return x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc); });
            Dictionary<string, LogParser> parsers = new Dictionary<string, LogParser>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in files)
            {
                string bn = CmLog.BaseName(f.FullName);
                LogParser p;
                if (!parsers.TryGetValue(bn, out p))
                {
                    p = LogParser.For(bn);
                    parsers[bn] = p;
                }
                try
                {
                    foreach (LogLine l in LogTail.ReadAll(f.FullName, zone)) p.Read(l, b.Obs);
                    b.Sources.Add(f.FullName);
                }
                catch (Exception ex) { b.Notes.Add("Could not read " + f.FullName + ": " + ex.Message); }
            }
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in DirsWith(files, LogParser.ImeLogs))
                foreach (KeyValuePair<string, string> kv in ImeLogParser.NamesFromFolder(dir)) names[kv.Key] = kv.Value;
            b.Names = names;
            int regs = ReadRegistryExports(folder, names, b);
            if (files.Count == 0 && regs == 0) b.Notes.Add("No logs TrackVice reads were found in " + folder + ". It reads smsts.log, AppEnforce.log, AppDiscovery.log, AppIntentEval.log, execmgr.log, CAS.log, UpdatesDeployment.log, UpdatesHandler.log and the Intune Management Extension logs.");
            return b;
        }

        // Registry exports (.reg) in the folder are read like the registry of the device they came from.
        static int ReadRegistryExports(string folder, Dictionary<string, string> names, Batch b)
        {
            MemoryRegistry mem = new MemoryRegistry();
            DateTime now = DateTime.MinValue;
            int n = 0;
            foreach (FileInfo f in new DirectoryInfo(folder).GetFiles("*.reg", SearchOption.AllDirectories))
            {
                try
                {
                    mem.Clock = f.LastWriteTimeUtc;
                    RegFile.Load(f.FullName, mem);
                    if (f.LastWriteTimeUtc > now) now = f.LastWriteTimeUtc;
                    b.Sources.Add(f.FullName);
                    n++;
                }
                catch (Exception ex) { b.Notes.Add("Could not read " + f.FullName + ": " + ex.Message); }
            }
            if (n == 0) return 0;
            Func<string, string> name = delegate (string id) { string v; return id != null && names.TryGetValue(id, out v) ? v : null; };
            EspWatch esp = new EspWatch();
            esp.Names = name;
            Win32AppWatch win32 = new Win32AppWatch();
            win32.Names = name;
            b.Obs.AddRange(esp.Read(mem, now));
            b.Obs.AddRange(win32.Read(mem, now));
            b.Obs.AddRange(new MdmWatch().Read(mem, now));
            try { b.Info.AddRange(AutopilotInfo.Read(mem)); } catch (Exception) { }
            b.Notes.Add("Registry exports in this folder were read. They have no times of their own, so their states carry the time the export was saved, except where the registry itself holds a time.");
            return n;
        }

        static List<string> DirsWith(List<FileInfo> files, string[] bases)
        {
            List<string> dirs = new List<string>();
            foreach (FileInfo f in files)
            {
                string bn = CmLog.BaseName(f.FullName);
                foreach (string x in bases)
                    if (string.Equals(bn, x, StringComparison.OrdinalIgnoreCase) && !dirs.Contains(f.DirectoryName)) dirs.Add(f.DirectoryName);
            }
            return dirs;
        }
    }
}
