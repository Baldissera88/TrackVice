namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Text;
    using System.Text.RegularExpressions;

    // One Win32 app, platform script or remediation known to the Intune Management Extension.
    [DataContract]
    public sealed class ImeItem
    {
        [DataMember] public string Kind;
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string User;
        [DataMember] public string Status;
        [DataMember] public string ErrorCode;
        [DataMember] public DateTime? Time;
        [DataMember] public bool Failed;
        [DataMember] public string Detail;
        [DataMember] public string RegistryPath;
        [DataMember] public string UserId;
        [DataMember] public string Assignment;

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Id : Name; }
        }
    }

    public static class ImeKinds
    {
        public const string Win32App = "Win32 app";
        public const string PlatformScript = "Platform script";
        public const string Remediation = "Remediation";
        public const string Agent = "IME";
    }

    // Reads the Intune Management Extension state from the registry. Read-only.
    // The layout is described, with sources, in docs/research/ime.md.
    public static class ImeCollector
    {
        public const string Root = @"HKLM\SOFTWARE\Microsoft\IntuneManagementExtension";
        public const string Win32AppsKey = Root + @"\Win32Apps";
        public const string PoliciesKey = Root + @"\Policies";
        public const string ReportsKey = Root + @"\SideCarPolicies\Scripts\Reports";
        public const string ExecutionKey = Root + @"\SideCarPolicies\Scripts\Execution";
        public const string StatusReportsKey = Root + @"\SideCarPolicies\StatusServiceReports";
        public const string DeviceUserId = "00000000-0000-0000-0000-000000000000";
        public const string DeviceUserSid = "S-0-0-00-0000000000-0000000000-000000000-000";
        public const int DefaultMaxPerKind = 5000;

        const string NL = "\r\n";
        const int MaxDetail = 16000;
        const int MaxValueText = 2000;
        const int MaxExtraValues = 40;

        static readonly Regex RxKey = new Regex(@"^\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?(?:_(\d{1,9}))?$", RegexOptions.Compiled);
        static readonly Regex RxSid = new Regex(@"^S-\d+(-\d+)+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        sealed class Rev
        {
            public int Number;
            public string Key;
        }

        sealed class Grs
        {
            public string Hash;
            public string Raw;
            public DateTime? Time;
        }

        public static List<ImeItem> Collect(IRegSource reg)
        {
            return Collect(reg, DefaultMaxPerKind);
        }

        public static List<ImeItem> Collect(IRegSource reg, int maxPerKind)
        {
            List<ImeItem> list = new List<ImeItem>();
            if (reg == null) return list;
            if (maxPerKind < 1) maxPerKind = 1;
            List<string> notes = new List<string>();
            try { ReadWin32Apps(reg, list, maxPerKind, notes); } catch (Exception) { }
            try { ReadStatusReports(reg, list, maxPerKind, notes); } catch (Exception) { }
            try { ReadScripts(reg, list, maxPerKind, notes); } catch (Exception) { }
            try { ReadRemediations(reg, list, maxPerKind, notes); } catch (Exception) { }
            try { AddAgentNotes(reg, list, notes); } catch (Exception) { }
            try { list.Sort(Compare); } catch (Exception) { }
            return list;
        }

        // ---- Win32 apps: Win32Apps\<user object ID>\<app ID>_<revision> ----

        static void ReadWin32Apps(IRegSource reg, List<ImeItem> list, int max, List<string> notes)
        {
            int count = 0;
            foreach (string user in reg.SubKeys(Win32AppsKey))
            {
                if (!IsUserKey(user)) continue;
                string up = Win32AppsKey + "\\" + user;
                Dictionary<string, Grs> grs = null;
                try { grs = ReadGrs(reg, up + @"\GRS"); } catch (Exception) { }
                foreach (KeyValuePair<string, List<Rev>> app in Group(reg.SubKeys(up)))
                {
                    if (count >= max)
                    {
                        notes.Add("Only the first " + max + " Win32 apps are shown.");
                        return;
                    }
                    try
                    {
                        ImeItem it = ReadWin32App(reg, up, user, app.Key, app.Value, grs);
                        if (it != null)
                        {
                            list.Add(it);
                            count++;
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        static ImeItem ReadWin32App(IRegSource reg, string userPath, string user, string appId, List<Rev> revs, Dictionary<string, Grs> grs)
        {
            Rev top = revs[0];
            string key = userPath + "\\" + top.Key;
            string csRaw, esRaw;
            bool csOk, esOk;
            Dictionary<string, object> cs = ReadJson(reg, key, "ComplianceStateMessage", out csRaw, out csOk);
            Dictionary<string, object> es = ReadJson(reg, key, "EnforcementStateMessage", out esRaw, out esOk);

            long? applicability = ImeJson.Long(ImeJson.Get(cs, "Applicability"));
            long? compliance = ImeJson.Long(ImeJson.Get(cs, "ComplianceState"));
            long? desired = ImeJson.Long(ImeJson.Get(cs, "DesiredState"));
            long? enforcement = ImeJson.Long(ImeJson.Get(es, "EnforcementState"));
            long esErr = ImeJson.Long(ImeJson.Get(es, "ErrorCode")) ?? 0;
            long csErr = ImeJson.Long(ImeJson.Get(cs, "ErrorCode")) ?? 0;
            long err = esErr != 0 ? esErr : csErr;

            ImeItem it = new ImeItem();
            it.Kind = ImeKinds.Win32App;
            it.Id = appId;
            it.User = UserLabel(user);
            it.RegistryPath = key;
            bool failed = false;
            if (cs == null && es == null) it.Status = "Unknown";
            else it.Status = AppStatus(applicability, compliance, desired, enforcement, err, out failed);
            it.Failed = failed;
            it.ErrorCode = ImeCodes.Format(err);

            RegValue intent = reg.Get(key, "Intent");
            long? intentNo = intent == null ? null : ImeCodes.Parse(intent.Data);
            if (intentNo.HasValue) it.Assignment = Lookup(intentNo.Value, Intents);

            string timeRaw = Str(reg.Get(key, "LastUpdatedTimeUtc"));
            it.Time = ImeTime.Parse(timeRaw);
            Grs g;
            if (grs == null || !grs.TryGetValue(appId, out g)) g = null;
            if (!it.Time.HasValue) it.Time = ImeTime.Parse(Str(reg.Get(key, "DownloadStartTimeUTC")));
            if (!it.Time.HasValue && g != null) it.Time = g.Time;

            StringBuilder b = new StringBuilder();
            b.Append("Win32 app ").Append(appId).Append(top.Number >= 0 ? ", revision " + top.Number : "");
            if (revs.Count > 1)
            {
                List<string> older = new List<string>();
                for (int i = 1; i < revs.Count && i < 20; i++) older.Add(revs[i].Number >= 0 ? revs[i].Number.ToString(CultureInfo.InvariantCulture) : revs[i].Key);
                b.Append(" (also in the registry: revision ").Append(string.Join(", ", older.ToArray())).Append(")");
            }
            b.Append(NL);
            b.Append("Assigned to: ").Append(TargetText(user)).Append(NL);
            if (intentNo.HasValue) b.Append("Assignment: ").Append(Named(intentNo, Intents)).Append(NL);
            AppendTime(b, "Last updated", timeRaw, it.Time);
            AppendJson(b, "Compliance state message", cs, csRaw, csOk);
            AppendJson(b, "Enforcement state message", es, esRaw, esOk);
            if (enforcement.HasValue && EnforcementInfo.ContainsKey(enforcement.Value))
                b.Append("Meaning: ").Append(EnforcementInfo[enforcement.Value]).Append(NL);
            string hint = ImeCodes.Hint(err);
            if (hint != null) b.Append("Hint: ").Append(hint).Append(NL);

            if (g != null)
            {
                b.Append("Retry schedule (GRS): ");
                if (g.Time.HasValue)
                {
                    b.Append("last attempt ").Append(Utc(g.Time.Value));
                    if (it.Failed) b.Append(", next attempt after about ").Append(Utc(g.Time.Value.AddHours(24))).Append(" (24-hour re-evaluation schedule)");
                }
                else b.Append(string.IsNullOrEmpty(g.Raw) ? "entry present" : "value " + Cap(g.Raw, 100));
                b.Append(NL).Append("  Key: GRS\\").Append(g.Hash).Append(NL);
            }

            AppendValues(b, "Registry values", reg, key, new string[] { "ComplianceStateMessage", "EnforcementStateMessage" });
            AppendSubKeys(b, reg, key, new string[] { "ComplianceStateMessage", "EnforcementStateMessage" });
            it.Detail = Finish(b);
            return it;
        }

        // Readable status from the compliance and enforcement state messages. Numbers follow the
        // IME enums documented by Ben Whitmore (msendpointmgr.com, 2023) and Eswar Koneti (2019).
        public static string AppStatus(long? applicability, long? compliance, long? desired, long? enforcement, long errorCode, out bool failed)
        {
            failed = false;
            if (applicability.HasValue && applicability.Value != 0) return "Not applicable";
            long es = enforcement ?? -1;
            bool uninstall = desired.HasValue && desired.Value == 1;

            if (compliance.HasValue && compliance.Value == 1)
            {
                if (uninstall)
                {
                    string f = Failure(es, errorCode, true, out failed);
                    if (f != null) return f;
                    if (es >= 2000 && es < 3000) return Progress(es);
                }
                return es == 2008 ? "Installed, reboot pending" : "Installed";
            }
            if (compliance.HasValue && compliance.Value == 2)
            {
                if (uninstall) return "Not installed";
                string f = Failure(es, errorCode, false, out failed);
                if (f != null) return f;
                if (es >= 6000 && es < 7000) return NotAttempted(es, out failed);
                if (es == 3000) return "Not applicable";
                if (es >= 2000 && es < 3000) return Progress(es);
                return "Not installed";
            }
            if (compliance.HasValue && compliance.Value == 4)
            {
                string f = Failure(es, errorCode, uninstall, out failed);
                if (f != null) return f;
                failed = true;
                return "Failed";
            }
            if (compliance.HasValue && compliance.Value == 100) return "Cleanup";

            // No usable compliance state: go by the enforcement state alone.
            string ef = Failure(es, errorCode, uninstall, out failed);
            if (ef != null) return ef;
            if (es >= 1000 && es < 2000) return uninstall ? "Uninstalled" : "Installed";
            if (es >= 2000 && es < 3000) return Progress(es);
            if (es == 3000) return "Not applicable";
            if (es >= 6000 && es < 7000) return NotAttempted(es, out failed);
            return "Unknown";
        }

        static string Failure(long es, long errorCode, bool uninstall, out bool failed)
        {
            failed = false;
            bool errState = es >= 5000 && es < 6000;
            bool errCode = errorCode != 0 && !ImeCodes.IsSuccess(errorCode) && !(es >= 1000 && es < 5000) && !(es >= 6000 && es < 7000);
            if (!errState && !errCode) return null;
            failed = true;
            if (es == 5999) return "Waiting for retry";
            if (es == 5003) return "Download failed";
            if (unchecked((uint)errorCode) == 0x87D1041C) return "Detection failed";
            return uninstall ? "Uninstall failed" : "Install failed";
        }

        static string Progress(long es)
        {
            if (es == 2008) return "Installed, reboot pending";
            if (es == 2009) return "Downloaded";
            if (es == 2011) return "Uninstalled, reboot pending";
            if (es == 2007) return "Waiting for dependencies";
            return "In progress";
        }

        static string NotAttempted(long es, out bool failed)
        {
            failed = es == 6001 || es == 6009;
            if (es == 6001) return "Dependency failed";
            if (es == 6002) return "Waiting for reboot";
            if (es == 6009) return "Superseded app uninstall failed";
            return "Not attempted";
        }

        static Dictionary<string, Grs> ReadGrs(IRegSource reg, string grsPath)
        {
            Dictionary<string, Grs> map = new Dictionary<string, Grs>(StringComparer.OrdinalIgnoreCase);
            foreach (string hash in reg.SubKeys(grsPath))
            {
                string hp = grsPath + "\\" + hash;
                foreach (string name in reg.ValueNames(hp)) AddGrs(map, name, hash, Str(reg.Get(hp, name)));
                foreach (string sub in reg.SubKeys(hp))
                {
                    string raw = null;
                    foreach (string name in reg.ValueNames(hp + "\\" + sub))
                    {
                        raw = Str(reg.Get(hp + "\\" + sub, name));
                        if (ImeTime.Parse(raw).HasValue) break;
                    }
                    AddGrs(map, sub, hash, raw);
                }
            }
            return map;
        }

        static void AddGrs(Dictionary<string, Grs> map, string name, string hash, string raw)
        {
            string id;
            int rev;
            if (!SplitKey(name, out id, out rev)) return;
            Grs g = new Grs();
            g.Hash = hash;
            g.Raw = raw;
            g.Time = ImeTime.Parse(raw);
            Grs old;
            if (map.TryGetValue(id, out old) && old.Time.HasValue && (!g.Time.HasValue || g.Time.Value < old.Time.Value)) return;
            map[id] = g;
        }

        // ---- Status service reports: SideCarPolicies\StatusServiceReports\<user>\<entry> ----
        // Seen in the IntuneDebug module; merged into the Win32 app items, or added for apps
        // that have no Win32Apps state (for example requirements not met).

        static void ReadStatusReports(IRegSource reg, List<ImeItem> list, int max, List<string> notes)
        {
            Dictionary<string, ImeItem> apps = new Dictionary<string, ImeItem>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (ImeItem i in list)
            {
                if (i.Kind != ImeKinds.Win32App) continue;
                apps[UserKey(i.User) + "|" + i.Id] = i;
                count++;
            }
            foreach (string user in reg.SubKeys(StatusReportsKey))
            {
                if (!IsUserKey(user)) continue;
                string up = StatusReportsKey + "\\" + user;
                foreach (string entry in reg.SubKeys(up))
                {
                    try
                    {
                        string ep = up + "\\" + entry;
                        List<KeyValuePair<string, string>> values = Values(reg, ep, MaxExtraValues);
                        string appId = null, status = null, applic = null, errRaw = null;
                        foreach (KeyValuePair<string, string> v in values)
                        {
                            if (Eq(v.Key, "AppId")) appId = v.Value;
                            else if (Eq(v.Key, "Status")) status = v.Value;
                            else if (Eq(v.Key, "ApplicabilityCode")) applic = v.Value;
                            else if (Eq(v.Key, "ErrorCode")) errRaw = v.Value;
                        }
                        string id;
                        int rev;
                        if (!SplitKey(appId, out id, out rev) && !SplitKey(entry, out id, out rev)) continue;

                        StringBuilder s = new StringBuilder();
                        foreach (KeyValuePair<string, string> v in values)
                            s.Append(NL).Append("  ").Append(v.Key.Length == 0 ? "(Default)" : v.Key).Append(" = ").Append(Cap(v.Value, 300));

                        ImeItem it;
                        if (apps.TryGetValue(UserKey(user) + "|" + id, out it))
                        {
                            it.Detail = Finish(new StringBuilder(it.Detail ?? "").Append(NL).Append("Status service report (").Append(ep).Append("):").Append(s.ToString()).Append(NL));
                            continue;
                        }
                        if (count >= max)
                        {
                            notes.Add("Only the first " + max + " Win32 apps are shown.");
                            return;
                        }
                        long code = ImeCodes.Parse(errRaw) ?? 0;
                        it = new ImeItem();
                        it.Kind = ImeKinds.Win32App;
                        it.Id = id;
                        it.User = UserLabel(user);
                        it.RegistryPath = ep;
                        it.ErrorCode = ImeCodes.Format(code);
                        bool notApplicable = !string.IsNullOrEmpty(applic) && !Eq(applic, "Applicable") && !Eq(applic, "0");
                        if (notApplicable) it.Status = "Not applicable";
                        else if (!string.IsNullOrEmpty(status)) it.Status = Sentence(status);
                        else it.Status = "Unknown";
                        string ls = (status ?? "").ToLowerInvariant();
                        it.Failed = !notApplicable && ((code != 0 && !ImeCodes.IsSuccess(code)) || ls.Contains("fail") || ls.Contains("error"));
                        StringBuilder b = new StringBuilder();
                        b.Append("Win32 app ").Append(id).Append(NL);
                        b.Append("Assigned to: ").Append(TargetText(user)).Append(NL);
                        b.Append("There is no state for this app under Win32Apps. This comes from the status service report only.").Append(NL);
                        b.Append("Status service report (").Append(ep).Append("):").Append(s.ToString()).Append(NL);
                        it.Detail = Finish(b);
                        list.Add(it);
                        apps[UserKey(user) + "|" + id] = it;
                        count++;
                    }
                    catch (Exception) { }
                }
            }
        }

        // ---- Platform scripts: Policies\<user object ID>\<script ID> ----

        static void ReadScripts(IRegSource reg, List<ImeItem> list, int max, List<string> notes)
        {
            int count = 0;
            foreach (string user in reg.SubKeys(PoliciesKey))
            {
                if (!IsUserKey(user)) continue;
                string up = PoliciesKey + "\\" + user;
                foreach (KeyValuePair<string, List<Rev>> script in Group(reg.SubKeys(up)))
                {
                    if (count >= max)
                    {
                        notes.Add("Only the first " + max + " platform scripts are shown.");
                        return;
                    }
                    try
                    {
                        list.Add(ReadScript(reg, up + "\\" + script.Value[0].Key, user, script.Key));
                        count++;
                    }
                    catch (Exception) { }
                }
            }
        }

        static ImeItem ReadScript(IRegSource reg, string key, string user, string id)
        {
            string result = Str(reg.Get(key, "Result"));
            long err = ImeCodes.Parse(Str(reg.Get(key, "ErrorCode"))) ?? 0;
            string timeRaw = Str(reg.Get(key, "LastUpdatedTimeUtc"));
            string runs = Str(reg.Get(key, "DownloadCount"));
            string runAs = Str(reg.Get(key, "RunAsAccount"));
            string details = Str(reg.Get(key, "ResultDetails"));

            ImeItem it = new ImeItem();
            it.Kind = ImeKinds.PlatformScript;
            it.Id = id;
            it.User = UserLabel(user);
            it.RegistryPath = key;
            bool failed;
            it.Status = ScriptStatus(result, err, out failed);
            it.Failed = failed;
            it.ErrorCode = ImeCodes.Format(err);
            it.Time = ImeTime.Parse(timeRaw);

            StringBuilder b = new StringBuilder();
            b.Append("Platform script ").Append(id).Append(NL);
            b.Append("Assigned to: ").Append(TargetText(user)).Append(NL);
            b.Append("Result: ").Append(string.IsNullOrEmpty(result) ? "(empty)" : result).Append(NL);
            if (err != 0) b.Append("Error code: ").Append(ImeCodes.Format(err)).Append(NL);
            if (!string.IsNullOrEmpty(runs))
            {
                b.Append("Runs (DownloadCount): ").Append(runs);
                if (failed) b.Append(". The agent retries a failed script at the next three check-ins, then not again until the script or its assignment changes");
                b.Append(NL);
            }
            if (!string.IsNullOrEmpty(runAs)) b.Append("Run as account (RunAsAccount): ").Append(runAs).Append(NL);
            AppendTime(b, "Last updated", timeRaw, it.Time);
            if (!string.IsNullOrEmpty(details))
            {
                bool ok;
                Dictionary<string, object> d = ImeJson.Parse(details, out ok) as Dictionary<string, object>;
                if (d != null)
                {
                    object msg = ImeJson.Get(d, "ExecutionMsg");
                    if (msg != null) AppendBlock(b, "Output", ImeJson.Text(msg));
                    Dictionary<string, object> rest = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, object> kv in d) if (!Eq(kv.Key, "ExecutionMsg")) rest[kv.Key] = kv.Value;
                    if (rest.Count > 0 || !ok) AppendJson(b, "Result details", rest, details, ok);
                }
                else AppendBlock(b, "Result details", details);
            }
            AppendValues(b, "Other registry values", reg, key, new string[] { "Result", "ErrorCode", "LastUpdatedTimeUtc", "DownloadCount", "RunAsAccount", "ResultDetails" });
            it.Detail = Finish(b);
            return it;
        }

        public static string ScriptStatus(string result, long errorCode, out bool failed)
        {
            string r = (result ?? "").Trim();
            bool badCode = errorCode != 0 && !ImeCodes.IsSuccess(errorCode);
            failed = false;
            if (Eq(r, "Success") || Eq(r, "Succeeded"))
            {
                failed = badCode;
                return badCode ? "Failed" : "Succeeded";
            }
            if (Eq(r, "Failed") || Eq(r, "Failure") || Eq(r, "Fail"))
            {
                failed = true;
                return "Failed";
            }
            if (r.Length == 0)
            {
                failed = badCode;
                return badCode ? "Failed" : "Pending";
            }
            string lr = r.ToLowerInvariant();
            failed = badCode || lr.Contains("fail") || lr.Contains("error");
            return Sentence(r);
        }

        // "NotInstalled" to "Not installed".
        static string Sentence(string s)
        {
            string t = Txt.SplitCamel((s ?? "").Trim());
            if (string.IsNullOrEmpty(t)) return "Unknown";
            t = Cap(t, 80);
            return t.Substring(0, 1).ToUpperInvariant() + t.Substring(1).ToLowerInvariant();
        }

        // ---- Remediations: SideCarPolicies\Scripts\Reports and \Execution ----

        static void ReadRemediations(IRegSource reg, List<ImeItem> list, int max, List<string> notes)
        {
            Dictionary<string, string[]> found = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            CollectRemediationKeys(reg, ReportsKey, 0, found, order);
            CollectRemediationKeys(reg, ExecutionKey, 1, found, order);
            int count = 0;
            foreach (string k in order)
            {
                if (count >= max)
                {
                    notes.Add("Only the first " + max + " remediations are shown.");
                    return;
                }
                try
                {
                    string[] e = found[k];
                    list.Add(ReadRemediation(reg, e[0], e[1], e[2], e[3]));
                    count++;
                }
                catch (Exception) { }
            }
        }

        // found[user|policy] = { report key, execution key, user, policy ID }
        static void CollectRemediationKeys(IRegSource reg, string root, int slot, Dictionary<string, string[]> found, List<string> order)
        {
            foreach (string user in reg.SubKeys(root))
            {
                if (!IsUserKey(user)) continue;
                string up = root + "\\" + user;
                foreach (KeyValuePair<string, List<Rev>> p in Group(reg.SubKeys(up)))
                {
                    string k = user + "|" + p.Key;
                    string[] e;
                    if (!found.TryGetValue(k, out e))
                    {
                        if (order.Count >= 100000) return;
                        e = new string[] { null, null, user, p.Key };
                        found[k] = e;
                        order.Add(k);
                    }
                    e[slot] = up + "\\" + p.Value[0].Key;
                }
            }
        }

        static ImeItem ReadRemediation(IRegSource reg, string reportKey, string execKey, string user, string id)
        {
            ImeItem it = new ImeItem();
            it.Kind = ImeKinds.Remediation;
            it.Id = id;
            it.User = UserLabel(user);
            it.RegistryPath = reportKey ?? execKey;

            string raw = null;
            bool ok = true;
            Dictionary<string, object> r = null;
            if (reportKey != null) r = ReadJson(reg, reportKey, "Result", out raw, out ok);
            string execRaw = execKey == null ? null : Str(reg.Get(execKey, "LastExecution"));
            it.Time = ImeTime.Parse(execRaw);

            Dictionary<string, object> info = ImeJson.Get(r, "Info") as Dictionary<string, object>;
            long? first = ImeJson.Long(ImeJson.Get(info, "FirstDetectExitCode"));
            long? remExit = ImeJson.Long(ImeJson.Get(info, "RemediationExitCode"));
            long? last = ImeJson.Long(ImeJson.Get(info, "LastDetectExitCode"));
            long? remStatus = ImeJson.Long(ImeJson.Get(r, "RemediationStatus"));
            bool remError = !string.IsNullOrEmpty(ImeJson.Text(ImeJson.Get(r, "RemediationScriptErrorDetails")));
            long err = ImeJson.Long(ImeJson.Get(r, "ErrorCode")) ?? 0;

            bool failed;
            if (r == null)
            {
                it.Status = reportKey == null ? "No result yet" : "Unknown";
                failed = false;
            }
            else it.Status = RemediationStatus(first, remExit, last, remStatus, remError, err, out failed);
            it.Failed = failed;
            it.ErrorCode = ImeCodes.Format(err);

            StringBuilder b = new StringBuilder();
            b.Append("Remediation ").Append(id).Append(NL);
            b.Append("Assigned to: ").Append(TargetText(user)).Append(NL);
            AppendTime(b, "Last run", execRaw, it.Time);
            if (reportKey != null)
            {
                string up = reportKey.Substring(0, reportKey.LastIndexOf('\\'));
                string full = Str(reg.Get(up, "LastFullReportTimeUTC"));
                AppendTime(b, "Last full report", full, ImeTime.Parse(full));
            }
            if (first.HasValue)
            {
                b.Append("Detection: exit code ").Append(first.Value).Append(first.Value == 0 ? " (no issue)" : first.Value == 1 ? " (issue found)" : " (not 0 or 1; Microsoft documents exit 1 as issue found)").Append(NL);
                if (first.Value != 0 && remStatus.HasValue && (remStatus.Value == 1 || remStatus.Value == 2 || remStatus.Value == 3))
                {
                    if (remExit.HasValue) b.Append("Remediation: exit code ").Append(remExit.Value).Append(NL);
                    if (last.HasValue && remStatus.Value != 3) b.Append("Detection after remediation: exit code ").Append(last.Value).Append(last.Value == 0 ? " (no issue)" : " (issue still found)").Append(NL);
                }
            }
            if (remStatus.HasValue) b.Append("Remediation status: ").Append(Named(remStatus, RemediationStatuses)).Append(NL);
            if (reportKey == null) b.Append("There is an execution record but no report yet.").Append(NL);
            AppendJson(b, "Result", r, raw, ok);
            if (reportKey != null) AppendValues(b, "Other registry values", reg, reportKey, new string[0]);
            if (execKey != null) AppendValues(b, "Execution record (" + execKey + ")", reg, execKey, new string[0]);
            it.Detail = Finish(b);
            return it;
        }

        // RemediationStatus numbers: 1 fixed, 2 recurred, 3 remediation script error, 4 no issue.
        // Not documented by Microsoft; based on Tech by Jeff (2026) and the samples by Peter van der
        // Woude (2020). The exit codes in Info are used when the number is missing or contradicts them.
        public static string RemediationStatus(long? firstDetect, long? remediationExit, long? lastDetect, long? remediationStatus, bool remediationError, long errorCode, out bool failed)
        {
            failed = false;
            string s = null;
            long rs = remediationStatus ?? -1;
            if (rs == 1) s = "Issue fixed";
            else if (rs == 2) { s = "Issue recurred"; failed = true; }
            else if (rs == 3) { s = "Remediation failed"; failed = true; }
            else if (rs == 4 && (!firstDetect.HasValue || firstDetect.Value == 0)) s = "No issue";

            // Without a known status the Info exit codes decide. They default to 0, so a 0 does not
            // prove that the remediation ran; only a failure is read from them.
            if (s == null)
            {
                if (!firstDetect.HasValue) s = "Unknown";
                else if (firstDetect.Value == 0) s = "No issue";
                else if (remediationError || (remediationExit.HasValue && remediationExit.Value != 0)) { s = "Remediation failed"; failed = true; }
                else if (firstDetect.Value == 1 && lastDetect.HasValue && lastDetect.Value != 0) { s = "Issue recurred"; failed = true; }
                else s = "Issue found";
            }
            if (errorCode != 0 && !ImeCodes.IsSuccess(errorCode))
            {
                failed = true;
                if (s == "No issue" || s == "Unknown" || s == "Issue found") s = "Failed";
            }
            return s;
        }

        // ---- Notes about the agent itself ----

        static void AddAgentNotes(IRegSource reg, List<ImeItem> list, List<string> notes)
        {
            List<string> denied = new List<string>();
            foreach (string d in reg.Denied)
                if (d != null && d.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && !denied.Contains(d)) denied.Add(d);
            if (denied.Count > 0)
            {
                ImeItem it = new ImeItem();
                it.Kind = ImeKinds.Agent;
                it.Id = "IntuneManagementExtension";
                it.Name = "Intune Management Extension";
                it.User = "System";
                it.Status = "Access denied";
                it.RegistryPath = Root;
                StringBuilder b = new StringBuilder();
                b.Append("TrackVice could not read part of the Intune Management Extension state, so Win32 apps, scripts or remediations may be missing. Run TrackVice as administrator.").Append(NL);
                b.Append("Keys that could not be read:").Append(NL);
                for (int i = 0; i < denied.Count && i < 20; i++) b.Append("  ").Append(denied[i]).Append(NL);
                it.Detail = Finish(b);
                list.Add(it);
            }
            List<string> uniq = new List<string>();
            foreach (string n in notes) if (!uniq.Contains(n)) uniq.Add(n);
            if (uniq.Count > 0)
            {
                ImeItem it = new ImeItem();
                it.Kind = ImeKinds.Agent;
                it.Id = "Limit";
                it.Name = "Intune Management Extension";
                it.User = "System";
                it.Status = "Not all items shown";
                it.RegistryPath = Root;
                it.Detail = string.Join(NL, uniq.ToArray());
                list.Add(it);
            }
        }

        // ---- Helpers ----

        static int Compare(ImeItem a, ImeItem b)
        {
            int c = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
            if (c != 0) return c;
            c = b.Failed.CompareTo(a.Failed);
            if (c != 0) return c;
            c = (a.User == "System" ? 0 : 1).CompareTo(b.User == "System" ? 0 : 1);
            if (c != 0) return c;
            c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.User, b.User);
        }

        static int KindOrder(string kind)
        {
            if (kind == ImeKinds.Agent) return 0;
            if (kind == ImeKinds.Win32App) return 1;
            if (kind == ImeKinds.PlatformScript) return 2;
            if (kind == ImeKinds.Remediation) return 3;
            return 4;
        }

        public static bool IsDeviceUser(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string t = id.Trim('{', '}');
            return string.Equals(t, DeviceUserId, StringComparison.OrdinalIgnoreCase) || string.Equals(t, DeviceUserSid, StringComparison.OrdinalIgnoreCase);
        }

        public static string UserLabel(string id)
        {
            return IsDeviceUser(id) ? "System" : id;
        }

        static string UserKey(string label)
        {
            return label == "System" || IsDeviceUser(label) ? DeviceUserId : (label ?? "");
        }

        static string TargetText(string id)
        {
            if (IsDeviceUser(id)) return "device (" + id + ")";
            return RxSid.IsMatch(id ?? "") ? "user with SID " + id : "user with Entra object ID " + id;
        }

        static bool IsUserKey(string name)
        {
            string id;
            int rev;
            if (name == null) return false;
            if (RxSid.IsMatch(name)) return true;
            return SplitKey(name, out id, out rev) && rev < 0;
        }

        // Splits "<GUID>_<n>" into the GUID and the revision n (-1 when there is no suffix).
        static bool SplitKey(string name, out string id, out int revision)
        {
            id = null;
            revision = -1;
            if (string.IsNullOrEmpty(name)) return false;
            Match m = RxKey.Match(name.Trim());
            if (!m.Success) return false;
            id = m.Groups[1].Value;
            if (m.Groups[2].Success)
            {
                int r;
                revision = int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out r) ? r : 0;
            }
            return true;
        }

        // Groups "<GUID>_<n>" keys by GUID, newest revision first.
        static List<KeyValuePair<string, List<Rev>>> Group(string[] keys)
        {
            Dictionary<string, List<Rev>> map = new Dictionary<string, List<Rev>>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            foreach (string k in keys)
            {
                string id;
                int rev;
                if (!SplitKey(k, out id, out rev)) continue;
                List<Rev> l;
                if (!map.TryGetValue(id, out l))
                {
                    l = new List<Rev>();
                    map[id] = l;
                    order.Add(id);
                }
                Rev r = new Rev();
                r.Number = rev;
                r.Key = k;
                l.Add(r);
            }
            List<KeyValuePair<string, List<Rev>>> result = new List<KeyValuePair<string, List<Rev>>>();
            foreach (string id in order)
            {
                List<Rev> l = map[id];
                l.Sort(delegate (Rev a, Rev b) { return b.Number.CompareTo(a.Number); });
                result.Add(new KeyValuePair<string, List<Rev>>(id, l));
            }
            return result;
        }

        // The JSON is a value of the same name in a subkey of the same name (Win32Apps), or a value on the key itself.
        static Dictionary<string, object> ReadJson(IRegSource reg, string key, string name, out string raw, out bool complete)
        {
            raw = Str(reg.Get(key + "\\" + name, name));
            if (raw == null) raw = Str(reg.Get(key, name));
            complete = true;
            if (raw == null) return null;
            object o = ImeJson.Parse(raw, out complete);
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null)
            {
                complete = false;
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
            return d;
        }

        static string Str(RegValue v)
        {
            return v == null ? null : v.Data;
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static List<KeyValuePair<string, string>> Values(IRegSource reg, string key, int max)
        {
            List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
            foreach (string n in reg.ValueNames(key))
            {
                if (l.Count >= max) break;
                RegValue v = reg.Get(key, n);
                if (v != null) l.Add(new KeyValuePair<string, string>(n ?? "", v.Data ?? ""));
            }
            return l;
        }

        static void AppendTime(StringBuilder b, string label, string raw, DateTime? t)
        {
            if (string.IsNullOrEmpty(raw)) return;
            b.Append(label).Append(": ");
            if (t.HasValue)
            {
                string f = Utc(t.Value);
                b.Append(f);
                if (raw.Trim() + " UTC" != f) b.Append(" (registry text: ").Append(Cap(raw, 80)).Append(")");
            }
            else b.Append(Cap(raw, 80)).Append(" (not a recognised date)");
            b.Append(NL);
        }

        static string Utc(DateTime t)
        {
            return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        }

        static void AppendJson(StringBuilder b, string title, Dictionary<string, object> d, string raw, bool complete)
        {
            if (raw == null && d == null) return;
            b.Append(title).Append(":").Append(NL);
            if (d != null)
                foreach (KeyValuePair<string, object> kv in d) AppendField(b, "  ", kv.Key, kv.Value, 0);
            if (!complete)
            {
                b.Append("  (the JSON is incomplete or not valid; showing what could be read)").Append(NL);
                if (d == null || d.Count == 0) b.Append("  Raw: ").Append(Cap(raw, 500).Replace("\r", " ").Replace("\n", " ")).Append(NL);
            }
        }

        static void AppendField(StringBuilder b, string indent, string key, object v, int depth)
        {
            Dictionary<string, object> sub = v as Dictionary<string, object>;
            if (sub != null && depth < 3)
            {
                b.Append(indent).Append(Label(key)).Append(":").Append(NL);
                foreach (KeyValuePair<string, object> kv in sub) AppendField(b, indent + "  ", kv.Key, kv.Value, depth + 1);
                return;
            }
            string text = FieldText(key, v);
            if (text.IndexOf('\n') >= 0)
            {
                b.Append(indent).Append(Label(key)).Append(":").Append(NL);
                foreach (string line in text.Replace("\r\n", "\n").Split('\n')) b.Append(indent).Append("    ").Append(line.TrimEnd('\r')).Append(NL);
            }
            else b.Append(indent).Append(Label(key)).Append(": ").Append(text).Append(NL);
        }

        static string Label(string key)
        {
            string l;
            return FieldLabels.TryGetValue(key, out l) ? l : key;
        }

        static string FieldText(string key, object v)
        {
            if (v == null) return "null";
            Dictionary<long, string> map;
            if (FieldEnums.TryGetValue(key, out map)) return Named(ImeJson.Long(v), map) ?? Cap(ImeJson.Text(v), MaxValueText);
            if (Eq(key, "ErrorCode"))
            {
                long? c = ImeJson.Long(v);
                if (c.HasValue) return c.Value == 0 ? "0 (none)" : ImeCodes.Format(c.Value);
            }
            return Cap(ImeJson.Text(v), MaxValueText);
        }

        static void AppendBlock(StringBuilder b, string title, string text)
        {
            if (text == null) return;
            b.Append(title).Append(":").Append(NL);
            foreach (string line in Cap(text, MaxValueText * 2).Replace("\r\n", "\n").Split('\n')) b.Append("    ").Append(line.TrimEnd('\r')).Append(NL);
        }

        static void AppendValues(StringBuilder b, string title, IRegSource reg, string key, string[] skip)
        {
            List<KeyValuePair<string, string>> values = Values(reg, key, MaxExtraValues + skip.Length);
            bool any = false;
            foreach (KeyValuePair<string, string> v in values)
            {
                bool s = false;
                foreach (string k in skip) if (Eq(k, v.Key)) s = true;
                if (s) continue;
                if (!any) b.Append(title).Append(":").Append(NL);
                any = true;
                b.Append("  ").Append(v.Key.Length == 0 ? "(Default)" : v.Key).Append(" = ").Append(Cap(v.Value, 300).Replace("\r", " ").Replace("\n", " ")).Append(NL);
            }
        }

        static void AppendSubKeys(StringBuilder b, IRegSource reg, string key, string[] skip)
        {
            int n = 0;
            foreach (string sub in reg.SubKeys(key))
            {
                bool s = false;
                foreach (string k in skip) if (Eq(k, sub)) s = true;
                if (s) continue;
                if (n++ >= 10) break;
                b.Append("Subkey ").Append(sub).Append(":").Append(NL);
                foreach (KeyValuePair<string, string> v in Values(reg, key + "\\" + sub, 20))
                    b.Append("  ").Append(v.Key.Length == 0 ? "(Default)" : v.Key).Append(" = ").Append(Cap(v.Value, 300).Replace("\r", " ").Replace("\n", " ")).Append(NL);
            }
        }

        static string Finish(StringBuilder b)
        {
            string s = b.ToString();
            while (s.EndsWith(NL)) s = s.Substring(0, s.Length - NL.Length);
            return Cap(s, MaxDetail);
        }

        static string Cap(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "… (" + s.Length + " characters)";
        }

        static string Named(long? v, Dictionary<long, string> map)
        {
            if (!v.HasValue) return null;
            string n;
            string num = v.Value.ToString(CultureInfo.InvariantCulture);
            return map.TryGetValue(v.Value, out n) ? n + " (" + num + ")" : "unknown value " + num;
        }

        static string Lookup(long v, Dictionary<long, string> map)
        {
            string n;
            return map.TryGetValue(v, out n) ? n : v.ToString(CultureInfo.InvariantCulture);
        }

        // ---- State tables ----

        static readonly Dictionary<long, string> Applicabilities = new Dictionary<long, string>
        {
            { 0, "Applicable" }, { 1, "Requirements not met" }, { 3, "Host platform not applicable" },
            { 1000, "Processor architecture not applicable" }, { 1001, "Minimum disk space not met" },
            { 1002, "Minimum OS version not met" }, { 1003, "Minimum physical memory not met" },
            { 1004, "Minimum logical processor count not met" }, { 1005, "Minimum CPU speed not met" },
            { 1006, "File system requirement rule not met" }, { 1007, "Registry requirement rule not met" },
            { 1008, "Script requirement rule not met" }, { 1009, "Not targeted and superseding apps not applicable" },
            { 1010, "Assignment filter criteria not met" }, { 1011, "App unsupported due to unknown reason" },
            { 1012, "User context app not supported during device-only check-in" },
            { 2000, "COSU minimum API level not met" }, { 2001, "COSU management mode" },
            { 2002, "COSU unsupported" }, { 2003, "COSU app incompatible" },
        };

        static readonly Dictionary<long, string> ComplianceStates = new Dictionary<long, string>
        {
            { 0, "Unknown" }, { 1, "Installed" }, { 2, "Not installed" }, { 3, "Conflict" },
            { 4, "Error" }, { 5, "Unknown" }, { 100, "Cleanup" },
        };

        static readonly Dictionary<long, string> DesiredStates = new Dictionary<long, string>
        {
            { 0, "None" }, { 1, "Not present" }, { 2, "Present" }, { 3, "Unknown" }, { 4, "Available" },
        };

        static readonly Dictionary<long, string> TargetingMethods = new Dictionary<long, string>
        {
            { 0, "Targeted" }, { 1, "Dependency of a targeted app" },
        };

        static readonly Dictionary<long, string> InstallContexts = new Dictionary<long, string>
        {
            { 1, "User" }, { 2, "System" },
        };

        static readonly Dictionary<long, string> TargetTypes = new Dictionary<long, string>
        {
            { 0, "None" }, { 1, "User" }, { 2, "Device" }, { 3, "Device and user" },
        };

        static readonly Dictionary<long, string> Intents = new Dictionary<long, string>
        {
            { 0, "Not targeted (dependency)" }, { 1, "Available" }, { 3, "Required" }, { 4, "Uninstall" },
        };

        static readonly Dictionary<long, string> EnforcementStates = new Dictionary<long, string>
        {
            { 1000, "Success" }, { 1003, "Success (fast notify)" }, { 1004, "Success, but a dependency failed to install" },
            { 1005, "Success, but a dependency has requirements not met" }, { 1006, "Success, but a dependency is pending reboot" },
            { 1007, "Success, but a dependency has automatic install off" }, { 1008, "Success, but the iOS App Store update failed" },
            { 1009, "Success, VPP app has an update available" }, { 1010, "Success, but the user rejected the update" },
            { 1011, "Success, uninstall pending reboot" }, { 1012, "Success, superseded app uninstall failed" },
            { 1013, "Success, superseded app uninstall pending reboot" }, { 1014, "Success, superseding apps detected" },
            { 1015, "Success, superseded apps detected" }, { 1016, "Success, app removed by supersedence" },
            { 1017, "Success, but a dependency is blocked by managed installer policy" }, { 1018, "Success, uninstalling superseded apps" },
            { 2000, "In progress" }, { 2007, "In progress, dependency installing" }, { 2008, "In progress, pending reboot" },
            { 2009, "In progress, download completed" }, { 2010, "In progress, pending uninstall of superseded apps" },
            { 2011, "In progress, uninstall pending reboot" }, { 2012, "In progress, pending managed installer" },
            { 3000, "Requirements not met" }, { 4000, "Unknown" },
            { 5000, "Error" }, { 5003, "Error downloading content" }, { 5006, "Error, conflicts prevent installation" },
            { 5015, "Error, managed installer AppLocker policy not applied" }, { 5999, "Error, will retry immediately" },
            { 6000, "Not attempted" }, { 6001, "Not attempted, a dependency failed" }, { 6002, "Not attempted, pending reboot" },
            { 6003, "Not attempted, a dependency has requirements not met" }, { 6004, "Not attempted, automatic install off" },
            { 6005, "Not attempted, a dependency has automatic install off" }, { 6006, "Not attempted, managed app no longer present" },
            { 6007, "Not attempted, the user rejected the install" }, { 6008, "Not attempted, the user is not signed in to the App Store" },
            { 6009, "Not attempted, superseded app uninstall failed" }, { 6010, "Not attempted, superseded app uninstall pending reboot" },
            { 6011, "Not attempted, untargeted superseding apps detected" }, { 6012, "Not attempted, a dependency is blocked by managed installer policy" },
            { 6013, "Not attempted, unsupported or indeterminate superseded app" },
        };

        // Longer descriptions from the IntuneStuff module by Ondrej Sebela.
        static readonly Dictionary<long, string> EnforcementInfo = new Dictionary<long, string>
        {
            { 2008, "The app has been installed but is not usable until the device has rebooted." },
            { 2009, "The app has been downloaded but no installation has been attempted." },
            { 3000, "Enforcement was aborted because the requirements are not met." },
            { 5000, "Enforcement failed. The error code gives the detailed reason." },
            { 5003, "The client was unable to download the app content." },
            { 5999, "Enforcement failed and will be retried immediately." },
            { 6001, "The install is blocked because one or more dependencies failed to install." },
            { 6002, "The install is blocked by a pending hard reboot." },
            { 6003, "The install is blocked because one or more dependencies have requirements that are not met." },
            { 6004, "The app is a dependency of another app and is set not to install automatically." },
            { 6005, "The install is blocked because one or more dependencies are set not to install automatically." },
        };

        static readonly Dictionary<long, string> RemediationStatuses = new Dictionary<long, string>
        {
            { 1, "Issue fixed" }, { 2, "Issue recurred" }, { 3, "Remediation script error" }, { 4, "No issue" },
        };

        static readonly Dictionary<string, Dictionary<long, string>> FieldEnums = new Dictionary<string, Dictionary<long, string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "Applicability", Applicabilities }, { "ComplianceState", ComplianceStates }, { "DesiredState", DesiredStates },
            { "TargetingMethod", TargetingMethods }, { "InstallContext", InstallContexts }, { "TargetType", TargetTypes },
            { "EnforcementState", EnforcementStates }, { "RemediationStatus", RemediationStatuses },
        };

        static readonly Dictionary<string, string> FieldLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ComplianceState", "Compliance state" }, { "DesiredState", "Desired state" }, { "ErrorCode", "Error code" },
            { "TargetingMethod", "Targeting method" }, { "InstallContext", "Install context" }, { "TargetType", "Target type" },
            { "ProductVersion", "Product version" }, { "AssignmentFilterIds", "Assignment filter IDs" },
            { "EnforcementState", "Enforcement state" }, { "RemediationStatus", "Remediation status" },
            { "PreRemediationDetectScriptOutput", "Detection output" }, { "PreRemediationDetectScriptError", "Detection error" },
            { "RemediationScriptErrorDetails", "Remediation error" }, { "PostRemediationDetectScriptOutput", "Detection output after remediation" },
            { "PostRemediationDetectScriptError", "Detection error after remediation" }, { "FirstDetectExitCode", "First detection exit code" },
            { "LastDetectExitCode", "Last detection exit code" }, { "RemediationExitCode", "Remediation exit code" },
            { "ErrorDetails", "Error details" }, { "ResultType", "Result type" }, { "InternalVersion", "Internal version" },
            { "PolicyId", "Policy ID" }, { "UserId", "User ID" }, { "PolicyHash", "Policy hash" }, { "ResultDetails", "Result details" },
        };
    }

    // Error codes seen in IME state, with the meanings that Microsoft documents.
    public static class ImeCodes
    {
        static readonly Dictionary<uint, string> Known = new Dictionary<uint, string>
        {
            { 0x87D1041C, "The application was not detected after installation completed successfully." },
            { 0x87D300C9, "The unmonitored process is in progress, however it may timeout." },
            { 0x87D30065, "Failed to retrieve content information." },
            { 0x87D30067, "Error unzipping downloaded content." },
            { 0x80070643, "Fatal error during installation (Windows Installer 1603)." },
            { 0x8000FFFF, "Unexpected error during installation." },
        };

        // Windows Installer exit codes (MsiExec.exe and InstMsi.exe error messages).
        static readonly Dictionary<uint, string> Msi = new Dictionary<uint, string>
        {
            { 1602, "The user cancelled the installation." },
            { 1603, "A fatal error occurred during installation." },
            { 1618, "Another installation is already in progress." },
            { 1619, "The installation package could not be opened." },
            { 1620, "The installation package could not be opened. It may not be a valid Windows Installer package." },
            { 1625, "The installation is forbidden by system policy." },
            { 1633, "The installation package is not supported on this platform." },
            { 1638, "Another version of this product is already installed." },
            { 1639, "Invalid command line argument." },
            { 1641, "The installer has initiated a restart. This indicates success." },
            { 3010, "A restart is required to complete the install. This indicates success." },
        };

        static readonly Dictionary<uint, string> Hints = new Dictionary<uint, string>
        {
            { 0x87D1041C, "The installer finished, but the detection rule did not find the app. Check the detection rule against what the installer really writes." },
            { 0x87D300C9, "The install command was still running when the agent stopped watching it. Check that the command is silent and does not wait for input." },
        };

        // 0, and the Windows Installer success codes 3010 and 1641, also as HRESULT.
        public static bool IsSuccess(long code)
        {
            if (code == 0 || code == 3010 || code == 1641) return true;
            if (code < int.MinValue || code > uint.MaxValue) return false;
            uint u = unchecked((uint)code);
            return u == 0x80070BC2 || u == 0x80070669;
        }

        public static long? Parse(string raw)
        {
            if (raw == null) return null;
            string s = raw.Trim();
            if (s.Length == 0) return null;
            long n;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(s.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n)) return n;
                return null;
            }
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return n;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d == Math.Floor(d) && Math.Abs(d) < 9e15) return (long)d;
            return null;
        }

        // "0x87D1041C: meaning" for HRESULTs, "0x00000643 (1603): meaning" for exit codes, null for 0.
        public static string Format(long code)
        {
            if (code == 0) return null;
            if (code < int.MinValue || code > uint.MaxValue) return code.ToString(CultureInfo.InvariantCulture);
            uint u = unchecked((uint)code);
            string s = "0x" + u.ToString("X8", CultureInfo.InvariantCulture);
            if (code > 0 && code < 0x10000) s += " (" + code.ToString(CultureInfo.InvariantCulture) + ")";
            string m = Meaning(code);
            return m == null ? s : s + ": " + m;
        }

        public static string Meaning(long code)
        {
            if (code < int.MinValue || code > uint.MaxValue) return null;
            uint u = unchecked((uint)code);
            string m;
            if (Known.TryGetValue(u, out m)) return m;
            if (u < 0x10000 && Msi.TryGetValue(u, out m)) return m;
            if ((u & 0xFFFF0000) == 0x80070000)
            {
                if (Msi.TryGetValue(u & 0xFFFF, out m)) return m;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    try
                    {
                        string w = new System.ComponentModel.Win32Exception((int)(u & 0xFFFF)).Message;
                        if (!string.IsNullOrEmpty(w) && !w.StartsWith("Unknown error", StringComparison.OrdinalIgnoreCase)) return w.Trim();
                    }
                    catch (Exception) { }
                }
            }
            return null;
        }

        public static string Hint(long code)
        {
            if (code == 0 || code < int.MinValue || code > uint.MaxValue) return null;
            string h;
            return Hints.TryGetValue(unchecked((uint)code), out h) ? h : null;
        }
    }

    // Tolerant reader for the date and time text the IME writes to the registry.
    public static class ImeTime
    {
        const DateTimeStyles Utc = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly Regex RxIso = new Regex(@"^\d{4}-\d{1,2}-\d{1,2}", RegexOptions.Compiled);
        static readonly string[] Us = { "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm", "M/d/yyyy h:mm tt", "M/d/yyyy H:mm:ss.FFFFFFF" };
        static readonly string[] DaySlash = { "d/M/yyyy H:mm:ss", "d/M/yyyy H:mm" };
        static readonly string[] DayDot = { "d.M.yyyy H:mm:ss", "d.M.yyyy H:mm", "d.M.yyyy H:mm:ss.FFFFFFF" };
        static readonly string[] DayDash = { "d-M-yyyy H:mm:ss", "d-M-yyyy H:mm" };

        // Returns the time in UTC, or null when the text is not a date this reader knows.
        public static DateTime? Parse(string raw)
        {
            try
            {
                if (raw == null) return null;
                string s = raw.Trim();
                if (s.Length == 0 || s.Length > 64) return null;
                long n;
                if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return FromNumber(n);
                DateTime d, e;
                if (RxIso.IsMatch(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, Utc, out d)) return AsUtc(d);
                // The IME writes times like "10/01/2026 02:38:27", the invariant culture format
                // (month first). A month-first reading in the future is read day first instead.
                if (DateTime.TryParseExact(s, Us, CultureInfo.InvariantCulture, Utc, out d))
                {
                    if (d > DateTime.UtcNow.AddDays(2) && DateTime.TryParseExact(s, DaySlash, CultureInfo.InvariantCulture, Utc, out e) && e <= DateTime.UtcNow.AddDays(2)) return AsUtc(e);
                    return AsUtc(d);
                }
                if (DateTime.TryParseExact(s, DaySlash, CultureInfo.InvariantCulture, Utc, out d)) return AsUtc(d);
                // Dotted dates are day first in every culture that uses them.
                if (DateTime.TryParseExact(s, DayDot, CultureInfo.InvariantCulture, Utc, out d)) return AsUtc(d);
                if (DateTime.TryParse(s, CultureInfo.CurrentCulture, Utc, out d)) return AsUtc(d);
                if (DateTime.TryParseExact(s, DayDash, CultureInfo.InvariantCulture, Utc, out d)) return AsUtc(d);
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, Utc, out d)) return AsUtc(d);
            }
            catch (Exception) { }
            return null;
        }

        static DateTime? FromNumber(long n)
        {
            if (n >= 116444736000000000L && n <= 159000000000000000L) return DateTime.FromFileTimeUtc(n);
            if (n >= 621355968000000000L && n <= 700000000000000000L) return new DateTime(n, DateTimeKind.Utc);
            if (n >= 1000000000000L && n <= 4102444800000L) return Epoch.AddMilliseconds(n);
            if (n >= 1000000000L && n <= 4102444800L) return Epoch.AddSeconds(n);
            return null;
        }

        static DateTime? AsUtc(DateTime d)
        {
            if (d.Year < 2000 || d.Year > 2200) return null;
            return d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);
        }
    }

    // Finds Win32 app (and, where logged, script) names in the IME log files. Optional: the
    // registry has IDs only. Reads with FileShare.ReadWrite, reads at most the last maxBytes,
    // never writes and never throws.
    public static class ImeLog
    {
        public const long DefaultMaxBytes = 20L * 1024 * 1024;
        public const long FolderMaxBytes = 100L * 1024 * 1024;
        const long HardMaxBytes = 256L * 1024 * 1024;
        const int MaxNames = 100000;

        static readonly string[] Markers = { "Get policies =", "RequestPayload:" };
        static readonly string[] IdKeys = { "Id", "AppId", "PolicyId" };
        static readonly string[] NameKeys = { "Name", "ApplicationName", "DisplayName", "PolicyName" };
        static readonly string[] Prefixes = { "IntuneManagementExtension", "AppWorkload", "HealthScripts", "AppActionProcessor" };
        static readonly Regex RxGuid = new Regex(@"^\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?$", RegexOptions.Compiled);
        static readonly Regex RxIdName = new Regex(@"\(id = (?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}), name(?: =)? (?<rest>[^\r\n]*)", RegexOptions.Compiled);

        public static string DefaultFolder
        {
            get
            {
                try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\IntuneManagementExtension\Logs"); }
                catch (Exception) { return null; }
            }
        }

        // Fills Name on the items that have none, from one log file. Returns how many were named.
        public static int FillNames(List<ImeItem> items, string logPath)
        {
            return FillNames(items, ReadNames(logPath));
        }

        public static int FillNames(List<ImeItem> items, Dictionary<string, string> names)
        {
            int n = 0;
            if (items == null || names == null || names.Count == 0) return 0;
            try
            {
                foreach (ImeItem it in items)
                {
                    if (it == null || !string.IsNullOrEmpty(it.Name) || string.IsNullOrEmpty(it.Id)) continue;
                    string name;
                    if (names.TryGetValue(it.Id.Trim('{', '}'), out name))
                    {
                        it.Name = name;
                        n++;
                    }
                }
            }
            catch (Exception) { }
            return n;
        }

        // Reads IntuneManagementExtension*.log, AppWorkload*.log, HealthScripts*.log and
        // AppActionProcessor*.log in the folder, newest first, at most FolderMaxBytes in all.
        public static int FillNamesFromFolder(List<ImeItem> items, string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
                List<FileInfo> files = new List<FileInfo>();
                foreach (FileInfo f in new DirectoryInfo(folder).GetFiles("*.log"))
                {
                    string n = f.Name.TrimStart('_');
                    foreach (string p in Prefixes)
                        if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { files.Add(f); break; }
                }
                files.Sort(delegate (FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                Dictionary<string, string> merged = NewMap();
                long budget = FolderMaxBytes;
                for (int i = 0; i < files.Count && i < 40 && budget > 0; i++)
                {
                    long take = Math.Min(DefaultMaxBytes, budget);
                    budget -= Math.Min(take, files[i].Length);
                    foreach (KeyValuePair<string, string> kv in CachedNames(files[i], take))
                        if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
                }
                return FillNames(items, merged);
            }
            catch (Exception) { return 0; }
        }

        // Rolled-over logs do not change, so their names are read once. Watch reads the device often.
        static readonly Dictionary<string, KeyValuePair<string, Dictionary<string, string>>> cache = new Dictionary<string, KeyValuePair<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, string> CachedNames(FileInfo f, long take)
        {
            string stamp = f.Length.ToString(CultureInfo.InvariantCulture) + "|" + f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + take.ToString(CultureInfo.InvariantCulture);
            lock (cache)
            {
                KeyValuePair<string, Dictionary<string, string>> hit;
                if (cache.TryGetValue(f.FullName, out hit) && hit.Key == stamp) return hit.Value;
            }
            Dictionary<string, string> names = ReadNames(f.FullName, take);
            lock (cache)
            {
                if (cache.Count > 200) cache.Clear();
                cache[f.FullName] = new KeyValuePair<string, Dictionary<string, string>>(stamp, names);
            }
            return names;
        }

        public static Dictionary<string, string> ReadNames(string path)
        {
            return ReadNames(path, DefaultMaxBytes);
        }

        // ID to name, from the last maxBytes of the file. Later lines win. Empty on any error.
        public static Dictionary<string, string> ReadNames(string path, long maxBytes)
        {
            Dictionary<string, string> names = NewMap();
            try
            {
                string text = ReadTail(path, maxBytes);
                if (text != null) Parse(text, names);
            }
            catch (Exception) { }
            return names;
        }

        public static Dictionary<string, string> ParseNames(string text)
        {
            Dictionary<string, string> names = NewMap();
            try { Parse(text, names); }
            catch (Exception) { }
            return names;
        }

        static Dictionary<string, string> NewMap()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        static string ReadTail(string path, long maxBytes)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (maxBytes <= 0) maxBytes = DefaultMaxBytes;
            if (maxBytes > HardMaxBytes) maxBytes = HardMaxBytes;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length;
                byte[] head = new byte[3];
                int hn = fs.Read(head, 0, 3);
                Encoding enc = new UTF8Encoding(false, false);
                int bom = 0;
                if (hn >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) bom = 3;
                else if (hn >= 2 && head[0] == 0xFF && head[1] == 0xFE) { enc = new UnicodeEncoding(false, false, false); bom = 2; }
                else if (hn >= 2 && head[0] == 0xFE && head[1] == 0xFF) { enc = new UnicodeEncoding(true, false, false); bom = 2; }
                long start = Math.Max(bom, len - maxBytes);
                if (bom == 2 && ((start - bom) % 2) != 0) start++;
                if (start >= len) return "";
                fs.Seek(start, SeekOrigin.Begin);
                int want = (int)Math.Min(maxBytes, len - start);
                byte[] buf = new byte[want];
                int got = 0;
                while (got < want)
                {
                    int r = fs.Read(buf, got, want - got);
                    if (r <= 0) break;
                    got += r;
                }
                string text = enc.GetString(buf, 0, got);
                if (start > bom)
                {
                    int nl = text.IndexOf('\n');
                    text = nl >= 0 ? text.Substring(nl + 1) : "";
                }
                return text;
            }
        }

        static void Parse(string text, Dictionary<string, string> names)
        {
            if (string.IsNullOrEmpty(text)) return;
            List<KeyValuePair<int, string[]>> found = new List<KeyValuePair<int, string[]>>();

            foreach (string marker in Markers)
            {
                int p = text.IndexOf(marker, StringComparison.Ordinal);
                while (p >= 0 && found.Count < MaxNames)
                {
                    int j = p + marker.Length;
                    while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
                    int next = j;
                    if (j < text.Length && text[j] == '[')
                    {
                        // Parse only the log message, which ends at "]LOG]!>" in a CMTrace line.
                        int stop = text.IndexOf("]LOG]!>", j, StringComparison.Ordinal);
                        if (stop < 0) stop = text.Length;
                        string seg = text.Substring(j, stop - j);
                        int end;
                        bool complete;
                        List<object> arr = ImeJson.Parse(seg, 0, true, out end, out complete) as List<object>;
                        end += j;
                        if (arr != null)
                        {
                            foreach (object o in arr)
                            {
                                Dictionary<string, object> d = o as Dictionary<string, object>;
                                if (d == null) continue;
                                string id = Pick(d, IdKeys, true);
                                string name = Pick(d, NameKeys, false);
                                if (id != null && name != null) found.Add(new KeyValuePair<int, string[]>(p, new string[] { id, name }));
                            }
                        }
                        next = Math.Max(end, j + 1);
                    }
                    p = next < text.Length ? text.IndexOf(marker, next, StringComparison.Ordinal) : -1;
                }
            }

            foreach (Match m in RxIdName.Matches(text))
            {
                if (found.Count >= MaxNames) break;
                string name = Clean(UntilClose(m.Groups["rest"].Value));
                if (name != null) found.Add(new KeyValuePair<int, string[]>(m.Index, new string[] { m.Groups["id"].Value, name }));
            }

            found.Sort(delegate (KeyValuePair<int, string[]> a, KeyValuePair<int, string[]> b) { return a.Key.CompareTo(b.Key); });
            foreach (KeyValuePair<int, string[]> f in found) names[f.Value[0]] = f.Value[1];
        }

        // The text up to the ')' that closes "(id = ..., name ...", keeping balanced parentheses
        // in the name, such as "App (x64)". Null when the line ends first.
        static string UntilClose(string rest)
        {
            int depth = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                char c = rest[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    if (depth == 0) return rest.Substring(0, i);
                    depth--;
                }
                else if (c == ']' && string.CompareOrdinal(rest, i, "]LOG]!>", 0, 7) == 0) return null;
            }
            return null;
        }

        static string Pick(Dictionary<string, object> d, string[] keys, bool guid)
        {
            foreach (string k in keys)
            {
                string s = ImeJson.Get(d, k) as string;
                if (s == null) continue;
                if (guid)
                {
                    Match m = RxGuid.Match(s.Trim());
                    if (m.Success) return m.Groups[1].Value;
                    continue;
                }
                s = Clean(s);
                if (s != null) return s;
            }
            return null;
        }

        static string Clean(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.Length == 0 || s.Length > 256) return null;
            foreach (char c in s) if (c < ' ') return null;
            if (s.IndexOf("<![LOG[", StringComparison.Ordinal) >= 0 || s.IndexOf("]LOG]!>", StringComparison.Ordinal) >= 0) return null;
            return s;
        }
    }

    // Small JSON reader for the IME registry values and log lines. Objects become
    // Dictionary<string, object> (keys ignore case), arrays List<object>, numbers long or
    // double. Never throws: on bad input it returns what it could read and complete = false.
    public static class ImeJson
    {
        const int MaxDepth = 64;

        public static object Parse(string s)
        {
            bool complete;
            return Parse(s, out complete);
        }

        public static object Parse(string s, out bool complete)
        {
            int end;
            object v = Parse(s, 0, false, out end, out complete);
            if (complete && s != null)
            {
                while (end < s.Length && char.IsWhiteSpace(s[end])) end++;
                if (end < s.Length) complete = false;
            }
            return v;
        }

        // Reads one value starting at start. strict: control characters end a string as an error,
        // so a value cut off in a log line does not swallow the next line.
        public static object Parse(string s, int start, bool strict, out int end, out bool complete)
        {
            end = start;
            complete = false;
            if (s == null || start < 0 || start >= s.Length) return null;
            Reader r = new Reader(s, start, strict);
            object v = null;
            try { v = r.Value(); }
            catch (Exception) { r.Error = true; }
            end = r.Pos;
            complete = !r.Error;
            return v;
        }

        public static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && key != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static long? Long(object v)
        {
            if (v == null) return null;
            if (v is long) return (long)v;
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && Math.Abs(d) < 9e15) return (long)d;
                return null;
            }
            string s = v as string;
            return s != null ? ImeCodes.Parse(s) : null;
        }

        public static string Text(object v)
        {
            if (v == null) return null;
            string s = v as string;
            if (s != null) return s;
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            StringBuilder b = new StringBuilder();
            Write(b, v, 0);
            return b.ToString();
        }

        static void Write(StringBuilder b, object v, int depth)
        {
            if (b.Length > 4000) return;
            if (depth > MaxDepth) { b.Append("…"); return; }
            Dictionary<string, object> d = v as Dictionary<string, object>;
            List<object> l = v as List<object>;
            string s = v as string;
            if (v == null) b.Append("null");
            else if (s != null) b.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            else if (d != null)
            {
                b.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) b.Append(',');
                    first = false;
                    b.Append('"').Append(kv.Key).Append("\":");
                    Write(b, kv.Value, depth + 1);
                }
                b.Append('}');
            }
            else if (l != null)
            {
                b.Append('[');
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) b.Append(',');
                    Write(b, l[i], depth + 1);
                }
                b.Append(']');
            }
            else b.Append(Text(v));
        }

        sealed class Reader
        {
            readonly string s;
            readonly int n;
            readonly bool strict;
            int depth;
            public int Pos;
            public bool Error;

            public Reader(string text, int start, bool strictStrings)
            {
                s = text;
                n = text.Length;
                Pos = start;
                strict = strictStrings;
            }

            void Ws()
            {
                while (Pos < n)
                {
                    char c = s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') Pos++;
                    else break;
                }
            }

            public object Value()
            {
                Ws();
                if (Pos >= n) { Error = true; return null; }
                char c = s[Pos];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                if (Lit("true")) return true;
                if (Lit("false")) return false;
                if (Lit("null")) return null;
                Error = true;
                return null;
            }

            bool Lit(string w)
            {
                if (string.CompareOrdinal(s, Pos, w, 0, w.Length) != 0) return false;
                Pos += w.Length;
                return true;
            }

            Dictionary<string, object> Obj()
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (++depth > MaxDepth) { Error = true; depth--; return d; }
                Pos++;
                Ws();
                if (Pos < n && s[Pos] == '}') { Pos++; depth--; return d; }
                while (!Error)
                {
                    Ws();
                    if (Pos >= n || s[Pos] != '"') { Error = true; break; }
                    string k = Str();
                    if (Error) break;
                    Ws();
                    if (Pos >= n || s[Pos] != ':') { Error = true; break; }
                    Pos++;
                    object v = Value();
                    if (Error)
                    {
                        if (v is Dictionary<string, object> || v is List<object>) d[k] = v;
                        break;
                    }
                    d[k] = v;
                    Ws();
                    if (Pos >= n) { Error = true; break; }
                    if (s[Pos] == ',') { Pos++; continue; }
                    if (s[Pos] == '}') { Pos++; break; }
                    Error = true;
                }
                depth--;
                return d;
            }

            List<object> Arr()
            {
                List<object> l = new List<object>();
                if (++depth > MaxDepth) { Error = true; depth--; return l; }
                Pos++;
                Ws();
                if (Pos < n && s[Pos] == ']') { Pos++; depth--; return l; }
                while (!Error)
                {
                    object v = Value();
                    if (Error)
                    {
                        if (v is Dictionary<string, object> || v is List<object>) l.Add(v);
                        break;
                    }
                    l.Add(v);
                    Ws();
                    if (Pos >= n) { Error = true; break; }
                    if (s[Pos] == ',') { Pos++; continue; }
                    if (s[Pos] == ']') { Pos++; break; }
                    Error = true;
                }
                depth--;
                return l;
            }

            string Str()
            {
                Pos++;
                StringBuilder b = null;
                int run = Pos;
                while (Pos < n)
                {
                    char c = s[Pos];
                    if (c == '"')
                    {
                        string r = b == null ? s.Substring(run, Pos - run) : b.Append(s, run, Pos - run).ToString();
                        Pos++;
                        return r;
                    }
                    if (c == '\\')
                    {
                        if (b == null) b = new StringBuilder();
                        b.Append(s, run, Pos - run);
                        Pos++;
                        if (Pos >= n) break;
                        char e = s[Pos++];
                        switch (e)
                        {
                            case 'n': b.Append('\n'); break;
                            case 'r': b.Append('\r'); break;
                            case 't': b.Append('\t'); break;
                            case 'b': b.Append('\b'); break;
                            case 'f': b.Append('\f'); break;
                            case 'u':
                                int code;
                                if (Pos + 4 <= n && int.TryParse(s.Substring(Pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                {
                                    b.Append((char)code);
                                    Pos += 4;
                                }
                                else b.Append('u');
                                break;
                            default: b.Append(e); break;
                        }
                        run = Pos;
                        continue;
                    }
                    if (strict && c < ' ') break;
                    Pos++;
                }
                Error = true;
                return null;
            }

            object Num()
            {
                int st = Pos;
                if (s[Pos] == '-') Pos++;
                while (Pos < n)
                {
                    char c = s[Pos];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') Pos++;
                    else break;
                }
                // Inside an object or array, a number that runs to the end of the text may be cut off.
                if (Pos >= n && depth > 0) { Error = true; return null; }
                string t = s.Substring(st, Pos - st);
                long l;
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                Error = true;
                return null;
            }
        }
    }
}
