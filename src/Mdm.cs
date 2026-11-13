namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;

    // Reads a registry export (.reg) into a MemoryRegistry, so a collected folder can be read like the device itself.
    public static class RegFile
    {
        public static void Load(string path, MemoryRegistry reg)
        {
            byte[] raw = File.ReadAllBytes(path);
            string text = raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE ? Encoding.Unicode.GetString(raw, 2, raw.Length - 2) : Encoding.UTF8.GetString(raw);
            Parse(text, reg);
        }

        public static void Parse(string text, MemoryRegistry reg)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            string key = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    key = Short(line.Substring(1, line.Length - 2));
                    reg.Touch(key);
                    continue;
                }
                if (key == null || (line[0] != '"' && line[0] != '@')) continue;
                int p = 0;
                string name = "";
                if (line[0] == '@') p = 1;
                else
                {
                    StringBuilder nb = new StringBuilder();
                    p = 1;
                    while (p < line.Length && line[p] != '"')
                    {
                        if (line[p] == '\\' && p + 1 < line.Length) p++;
                        nb.Append(line[p]);
                        p++;
                    }
                    p++;
                    name = nb.ToString();
                }
                if (p >= line.Length || line[p] != '=') continue;
                p++;
                string rest = line.Substring(p);
                if (rest.StartsWith("\""))
                {
                    // A string can run over several lines until its closing quote.
                    StringBuilder sb = new StringBuilder();
                    int q = 1;
                    string cur = rest;
                    while (true)
                    {
                        bool closed = false;
                        while (q < cur.Length)
                        {
                            char c = cur[q];
                            if (c == '\\' && q + 1 < cur.Length) { sb.Append(cur[q + 1]); q += 2; continue; }
                            if (c == '"') { closed = true; break; }
                            sb.Append(c);
                            q++;
                        }
                        if (closed || i + 1 >= lines.Length) break;
                        sb.Append('\n');
                        cur = lines[++i];
                        q = 0;
                    }
                    reg.Set(key, name, "REG_SZ", sb.ToString());
                }
                else if (rest.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                {
                    uint n;
                    if (uint.TryParse(rest.Substring(6).Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n))
                        reg.Set(key, name, "REG_DWORD", n.ToString(CultureInfo.InvariantCulture));
                }
                else if (rest.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = rest.IndexOf(':');
                    if (colon < 0) continue;
                    string type = rest.Substring(0, colon).ToLowerInvariant();
                    StringBuilder hex = new StringBuilder(rest.Substring(colon + 1));
                    while (hex.Length > 0 && hex[hex.Length - 1] == '\\' && i + 1 < lines.Length)
                    {
                        hex.Length--;
                        hex.Append(lines[++i].Trim());
                    }
                    List<byte> bytes = new List<byte>();
                    foreach (string b in hex.ToString().Split(','))
                    {
                        byte v;
                        if (byte.TryParse(b.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)) bytes.Add(v);
                    }
                    byte[] arr = bytes.ToArray();
                    if (type == "hex(b)" && arr.Length == 8) reg.Set(key, name, "REG_QWORD", BitConverter.ToUInt64(arr, 0).ToString(CultureInfo.InvariantCulture));
                    else if (type == "hex(7)") reg.Set(key, name, "REG_MULTI_SZ", string.Join("; ", Encoding.Unicode.GetString(arr).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)));
                    else if (type == "hex(2)") reg.Set(key, name, "REG_EXPAND_SZ", Encoding.Unicode.GetString(arr).TrimEnd('\0'));
                    else reg.Set(key, name, "REG_BINARY", LiveRegistry.Hex(arr));
                }
            }
        }

        static string Short(string key)
        {
            if (key.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)) return "HKLM" + key.Substring(18);
            if (key.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)) return "HKCU" + key.Substring(17);
            if (key.StartsWith("HKEY_USERS", StringComparison.OrdinalIgnoreCase)) return "HKU" + key.Substring(10);
            return key;
        }
    }

    // What the MDM side of an enrollment leaves in the registry, beside the ESP's own tracking keys:
    // the policies, profiles and packages the ESP waits for, Microsoft 365 Apps, MSI apps and platform scripts.
    public sealed class MdmWatch
    {
        public const string Expected = EspWatch.Root + @"\ESPTrackingInfo\Diagnostics";
        public const string Office = @"HKLM\SOFTWARE\Microsoft\OfficeCSP";
        public const string Msi = @"HKLM\SOFTWARE\Microsoft\EnterpriseDesktopAppManagement";
        public const string Scripts = @"HKLM\SOFTWARE\Microsoft\IntuneManagementExtension\Policies";
        public const string Uninstall = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        public const string Uninstall32 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        static readonly Regex RxOffice = new Regex(@"/Office/Installation/(?<id>[0-9A-Fa-f-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxMsi = new Regex(@"/EnterpriseDesktopAppManagement/MSI/(?<id>[^/]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxProduct = new Regex("Product ID=\"(?<p>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxAttr = new Regex("(?<n>Channel|OfficeClientEdition)=\"(?<v>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxLanguage = new Regex("Language ID=\"(?<l>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The ESP phase of the things the ESP waits for, by item key.
        readonly Dictionary<string, string> espPhase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<Obs> Read(IRegSource reg, DateTime nowUtc)
        {
            List<Obs> list = new List<Obs>();
            try { ReadExpected(reg, nowUtc, list); } catch (Exception) { }
            try { ReadOffice(reg, nowUtc, list); } catch (Exception) { }
            try { ReadMsi(reg, nowUtc, list); } catch (Exception) { }
            try { ReadScripts(reg, nowUtc, list); } catch (Exception) { }
            return list;
        }

        static Obs Make(DateTime t, string source, string key, string name, string kind, string phase, State? state, string text, string path, string value)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = source;
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = kind;
            o.Origin = Origins.Intune;
            o.PhaseKey = phase;
            o.State = state;
            o.Text = text;
            if (state == State.Failed) o.Severity = 2;
            o.Evidence = new Evidence(EvidenceKinds.Registry, path, 0, value);
            return o;
        }

        static DateTime When(IRegSource reg, string path, DateTime nowUtc)
        {
            DateTime? wt = reg.WriteTime(path);
            return wt.HasValue && wt.Value <= nowUtc.AddMinutes(1) ? wt.Value : nowUtc;
        }

        static long Num(IRegSource reg, string path, string name, long missing)
        {
            RegValue v = reg.Get(path, name);
            long? n = v == null ? null : ImeCodes.Parse(v.Data);
            return n ?? missing;
        }

        static string Str(IRegSource reg, string path, string name)
        {
            RegValue v = reg.Get(path, name);
            return v == null || string.IsNullOrEmpty(v.Data) ? null : v.Data;
        }

        // ---- what the ESP waits for ----

        // ESPTrackingInfo\Diagnostics\<category>\<time>: one value per thing the ESP waits for, 0 while it waits and 1 when it is there.
        // Every change is saved under a new time, which gives the history.
        void ReadExpected(IRegSource reg, DateTime nowUtc, List<Obs> list)
        {
            foreach (string cat in reg.SubKeys(Expected))
            {
                if (cat.Equals("Sidecar", StringComparison.OrdinalIgnoreCase)) continue;
                List<KeyValuePair<DateTime, string>> times = new List<KeyValuePair<DateTime, string>>();
                foreach (string sub in reg.SubKeys(Expected + "\\" + cat))
                {
                    DateTime t;
                    if (DateTime.TryParse(sub, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                        times.Add(new KeyValuePair<DateTime, string>(t, sub));
                }
                times.Sort(delegate (KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b) { return a.Key.CompareTo(b.Key); });
                foreach (KeyValuePair<DateTime, string> ts in times)
                {
                    string path = Expected + "\\" + cat + "\\" + ts.Value;
                    foreach (string uri in reg.ValueNames(path))
                    {
                        if (!uri.StartsWith("./")) continue;
                        long v = Num(reg, path, uri, -1);
                        if (!seen.Add("x|" + cat + "|" + uri + "|" + v)) continue;
                        Obs o = ExpectedItem(cat, uri, v, ts.Key, path);
                        if (o == null) continue;
                        espPhase[o.ItemKey] = o.PhaseKey;
                        list.Add(o);
                    }
                }
            }
        }

        public static string UriName(string uri)
        {
            string s = uri;
            foreach (string pre in new string[] { "./Device/Vendor/MSFT/", "./User/Vendor/MSFT/", "./Vendor/MSFT/", "./Device/", "./User/", "./" })
                if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(pre.Length); break; }
            try { s = Uri.UnescapeDataString(s); } catch (UriFormatException) { }
            if (s.StartsWith("Policy/Config/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = s.Substring(14).Split('/');
                return parts.Length >= 2 ? parts[0] + ": " + string.Join("/", parts, 1, parts.Length - 1) : s.Substring(14);
            }
            return s.Replace("/", " / ");
        }

        static Obs ExpectedItem(string cat, string uri, long v, DateTime t, string path)
        {
            string phase = uri.StartsWith("./User/", StringComparison.OrdinalIgnoreCase) ? Phases.AccountSetup : Phases.DeviceSetup;
            string c = cat.ToLowerInvariant();
            string key, name, kind, what;
            bool weak = false;
            State wait = State.Pending;
            if (c.Contains("msi"))
            {
                Match om = RxOffice.Match(uri), mm = RxMsi.Match(uri);
                wait = State.Installing;
                if (om.Success)
                {
                    key = "office:" + om.Groups["id"].Value.ToLowerInvariant();
                    name = "Microsoft 365 Apps";
                    kind = Kinds.Office;
                    what = "Microsoft 365 Apps";
                }
                else if (mm.Success)
                {
                    string code = mm.Groups["id"].Value.Trim('{', '}').ToLowerInvariant();
                    key = "msi:" + code;
                    name = "MSI {" + code + "}";
                    kind = Kinds.MsiApp;
                    what = "this MSI app";
                }
                else
                {
                    key = "esp:expected:" + uri.ToLowerInvariant();
                    name = UriName(uri);
                    kind = Kinds.MsiApp;
                    what = "this package";
                }
                weak = true;
            }
            else if (c.Contains("modern"))
            {
                key = "esp:expected:" + uri.ToLowerInvariant();
                string s = UriName(uri);
                int i = s.IndexOf("AppInstallation / ", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) s = s.Substring(i + 18);
                int slash = s.IndexOf(" / ", StringComparison.Ordinal);
                if (slash > 0) s = s.Substring(0, slash);
                name = s;
                kind = Kinds.StoreApp;
                what = "this Store app";
                wait = State.Installing;
            }
            else if (c.Contains("network"))
            {
                key = "esp:expected:" + uri.ToLowerInvariant();
                string s = UriName(uri);
                name = s.StartsWith("WiFi / Profile / ", StringComparison.OrdinalIgnoreCase) ? "Wi-Fi: " + s.Substring(17)
                    : s.StartsWith("VPNv2 / ", StringComparison.OrdinalIgnoreCase) ? "VPN: " + s.Substring(8) : s;
                kind = Kinds.Profile;
                what = "this network profile";
            }
            else if (c.Contains("cert"))
            {
                key = "esp:expected:" + uri.ToLowerInvariant();
                name = "Certificate: " + UriName(uri);
                kind = Kinds.Certificate;
                what = "this certificate";
            }
            else
            {
                key = "esp:expected:" + uri.ToLowerInvariant();
                name = UriName(uri);
                kind = Kinds.Policy;
                what = "this policy";
            }
            State st = v == 1 ? State.Succeeded : v == 0 ? wait : State.Unknown;
            string text = v == 1 ? "ESP: " + what + " is in place" : v == 0 ? "ESP: waiting for " + what : "ESP: state " + v;
            Obs o = Make(t, "ESP", key, name, kind, phase, st, text, path, uri + " = " + v);
            o.WeakName = weak;
            o.AddInfo("ESP phase", Phases.Name(phase)).AddInfo("The ESP waits for", uri);
            return o;
        }

        string PhaseFor(string key, string fallback)
        {
            string p;
            return espPhase.TryGetValue(key, out p) ? p : fallback;
        }

        // ---- Microsoft 365 Apps, installed by the Office CSP ----

        public static string OfficeName(string config)
        {
            List<string> names = new List<string>();
            foreach (Match m in RxProduct.Matches(config ?? ""))
            {
                string p = m.Groups["p"].Value, n;
                switch (p.ToLowerInvariant())
                {
                    case "o365proplusretail": n = "Microsoft 365 Apps for enterprise"; break;
                    case "o365businessretail": n = "Microsoft 365 Apps for business"; break;
                    case "visioproretail": n = "Visio"; break;
                    case "projectproretail": n = "Project"; break;
                    default: n = p; break;
                }
                if (!names.Contains(n)) names.Add(n);
            }
            return names.Count == 0 ? "Microsoft 365 Apps" : string.Join(" + ", names.ToArray());
        }

        void ReadOffice(IRegSource reg, DateTime nowUtc, List<Obs> list)
        {
            foreach (string id in reg.SubKeys(Office))
            {
                string path = Office + "\\" + id;
                long final = Num(reg, path, "FinalStatus", -1), status = Num(reg, path, "Status", -1);
                if (!seen.Add("o|" + id + "|" + final + "|" + status)) continue;
                string config = Str(reg, path, "") ?? "";
                // FinalStatus: 70 installed, 60 failed. Until it is set, the install is running.
                State st = final == 70 ? State.Succeeded : final == 60 ? State.Failed : State.Installing;
                string text = final == 70 ? "Office CSP: installed" : final == 60 ? "Office CSP: failed" + (status > 0 ? ", status " + status : "") : "Office CSP: installing" + (status > 0 && status != 997 ? ", status " + status : "");
                string key = "office:" + id.ToLowerInvariant();
                Obs o = Make(When(reg, path, nowUtc), "Office CSP", key, OfficeName(config), Kinds.Office, PhaseFor(key, Phases.IntuneApps), st, text, path,
                    "Status = " + (status < 0 ? "(not set)" : status.ToString(CultureInfo.InvariantCulture)) + ", FinalStatus = " + (final < 0 ? "(not set)" : final.ToString(CultureInfo.InvariantCulture)));
                if (st == State.Failed && status > 0) o.ExitCode = status;
                o.AddInfo("ID", id.ToLowerInvariant());
                foreach (Match m in RxAttr.Matches(config))
                    o.AddInfo(m.Groups["n"].Value.Equals("Channel", StringComparison.OrdinalIgnoreCase) ? "Update channel" : "Architecture", m.Groups["v"].Value + (m.Groups["n"].Value.Equals("Channel", StringComparison.OrdinalIgnoreCase) ? "" : "-bit"));
                List<string> langs = new List<string>();
                foreach (Match m in RxLanguage.Matches(config)) if (!langs.Contains(m.Groups["l"].Value)) langs.Add(m.Groups["l"].Value);
                if (langs.Count > 0) o.AddInfo("Languages", string.Join(", ", langs.ToArray()));
                list.Add(o);
            }
        }

        // ---- MSI apps, installed by the MDM client itself ----

        public static State MsiState(long status, out string label)
        {
            switch (status)
            {
                case 10: label = "initialized"; return State.Pending;
                case 20: label = "downloading"; return State.Downloading;
                case 25: label = "waiting to retry the download"; return State.Downloading;
                case 30: label = "download failed"; return State.Failed;
                case 40: label = "downloaded"; return State.Installing;
                case 48: label = "waiting for a user session"; return State.Pending;
                case 50: label = "installing"; return State.Installing;
                case 55: label = "waiting to retry the install"; return State.Installing;
                case 60: label = "install failed"; return State.Failed;
                case 70: label = "installed"; return State.Succeeded;
                default: label = "status " + status; return State.Unknown;
            }
        }

        static string MsiName(IRegSource reg, string code, string url)
        {
            foreach (string root in new string[] { Uninstall, Uninstall32 })
            {
                string n = Str(reg, root + "\\{" + code + "}", "DisplayName");
                if (n != null) return n;
            }
            if (string.IsNullOrEmpty(url)) return null;
            string file = url;
            int q = file.IndexOf('?');
            if (q >= 0) file = file.Substring(0, q);
            file = file.Substring(file.LastIndexOf('/') + 1);
            if (file.Equals("IntuneWindowsAgent.msi", StringComparison.OrdinalIgnoreCase)) return "Intune Management Extension (IntuneWindowsAgent.msi)";
            return file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? file : null;
        }

        void ReadMsi(IRegSource reg, DateTime nowUtc, List<Obs> list)
        {
            foreach (string sid in reg.SubKeys(Msi))
            {
                string root = Msi + "\\" + sid + "\\MSI";
                bool user = sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase);
                foreach (string sub in reg.SubKeys(root))
                {
                    string path = root + "\\" + sub;
                    string code = sub.Trim('{', '}').ToLowerInvariant();
                    long status = Num(reg, path, "Status", -1), err = Num(reg, path, "LastError", 0);
                    if (status < 0) continue;
                    // The same product can be saved under {id} and {{id}}.
                    if (!seen.Add("m|" + code + "|" + status + "|" + err)) continue;
                    string label;
                    State st = MsiState(status, out label);
                    string key = "msi:" + code;
                    string url = Str(reg, path, "CurrentDownloadUrl");
                    string name = MsiName(reg, code, url);
                    Obs o = Make(When(reg, path, nowUtc), "MDM", key, name ?? "MSI {" + code + "}", Kinds.MsiApp, PhaseFor(key, user ? Phases.AccountSetup : Phases.IntuneApps), st,
                        "MSI: " + label + (err != 0 ? ", " + Errors.Short(err) : ""), path, "Status = " + status + ", LastError = " + err);
                    if (!espPhase.ContainsKey(key) && user) o.PhaseKey = Phases.IntuneApps;
                    o.WeakName = name == null;
                    if (err != 0) o.ExitCode = unchecked((int)(uint)err);
                    o.AddInfo("Product code", "{" + code + "}").AddInfo("Version", Str(reg, path, "ProductVersion")).AddInfo("Context", user ? "User" : "Device");
                    string cmd = Str(reg, path, "CommandLine");
                    if (cmd != null) o.Command = cmd;
                    if (url != null)
                    {
                        int q = url.IndexOf('?');
                        o.AddInfo("Downloaded from", q > 0 ? url.Substring(0, q) : url);
                    }
                    list.Add(o);
                }
            }
        }

        // ---- platform scripts ----

        public static string ScriptKey(string id)
        {
            return "script:" + id.ToLowerInvariant();
        }

        public static string ScriptName(string id)
        {
            return "Platform script " + (id.Length >= 8 ? id.Substring(0, 8).ToLowerInvariant() : id);
        }

        static readonly Regex RxStamp = new Regex(@"^\s*(?<a>\d{1,2})\D(?<b>\d{1,2})\D(?<y>\d{4})\D+(?<h>\d{1,2})\D(?<m>\d{2})\D(?<s>\d{2})\s*(?<ap>[AaPp][Mm])?", RegexOptions.Compiled);

        // The agent saves this time in the format of the device's language, such as "13/11/2026 11.57.09" or "11/13/2026 11:57:09 AM".
        // When day and month could be either way round, the reading that is not in the future wins, day first.
        public static DateTime? LocalStamp(string s, DateTime nowUtc)
        {
            if (s == null) return null;
            Match m = RxStamp.Match(s);
            if (!m.Success) return null;
            int a = int.Parse(m.Groups["a"].Value), b = int.Parse(m.Groups["b"].Value), y = int.Parse(m.Groups["y"].Value);
            int h = int.Parse(m.Groups["h"].Value), mi = int.Parse(m.Groups["m"].Value), se = int.Parse(m.Groups["s"].Value);
            if (m.Groups["ap"].Success)
            {
                bool pm = m.Groups["ap"].Value.ToUpperInvariant() == "PM";
                if (pm && h < 12) h += 12;
                if (!pm && h == 12) h = 0;
            }
            foreach (bool dayFirst in new bool[] { true, false })
            {
                int day = dayFirst ? a : b, month = dayFirst ? b : a;
                if (month < 1 || month > 12 || day < 1 || day > 31 || h > 23) continue;
                try
                {
                    DateTime t = new DateTime(y, month, day, h, mi, se, DateTimeKind.Utc);
                    if (t <= nowUtc.AddMinutes(5)) return t;
                }
                catch (ArgumentOutOfRangeException) { }
            }
            return null;
        }

        // The result the Intune Management Extension saved for each platform script. The names are not on the device.
        void ReadScripts(IRegSource reg, DateTime nowUtc, List<Obs> list)
        {
            foreach (string user in reg.SubKeys(Scripts))
            {
                foreach (string id in reg.SubKeys(Scripts + "\\" + user))
                {
                    string path = Scripts + "\\" + user + "\\" + id;
                    string result = Str(reg, path, "Result");
                    if (result == null || id.Length != 36) continue;
                    if (!seen.Add("s|" + id + "|" + result)) continue;
                    State? st = result.Equals("Success", StringComparison.OrdinalIgnoreCase) ? State.Succeeded : result.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? State.Failed : (State?)null;
                    long err = Num(reg, path, "ErrorCode", 0);
                    Obs o = Make(LocalStamp(Str(reg, path, "LastUpdatedTimeUtc"), nowUtc) ?? When(reg, path, nowUtc), "IME registry", ScriptKey(id), ScriptName(id), Kinds.Script, Phases.Scripts, st,
                        "IME registry: script result " + result, path, "Result = " + result + ", ErrorCode = " + err);
                    o.WeakName = true;
                    o.AddInfo("ID", id.ToLowerInvariant()).AddInfo("Runs as", Str(reg, path, "RunAsAccount")).AddInfo("Target", Str(reg, path, "TargetType"));
                    o.AddInfo("Times downloaded", Str(reg, path, "DownloadCount"));
                    list.Add(o);
                }
            }
        }
    }

    // Platform scripts in IntuneManagementExtension.log and AgentExecutor.log.
    public sealed class ScriptLog
    {
        static readonly Regex RxStart = new Regex(@"\[PowerShell\] Processing policy with id = (?<id>[0-9A-Fa-f-]{36}) for user (?<u>[0-9A-Fa-f-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxResult = new Regex(@"\[PowerShell\] User Id = [0-9A-Fa-f-]{36}, Policy id = (?<id>[0-9A-Fa-f-]{36}), policy result = (?<r>\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxFile = new Regex(@"\\Policies\\Scripts\\[0-9A-Fa-f-]{36}_(?<id>[0-9A-Fa-f-]{36})\.ps1", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxExit = new Regex(@"^Powershell exit code is (?<c>-?\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxSpace = new Regex(@"\s+", RegexOptions.Compiled);

        string current;
        string host;

        static Obs For(LogLine l, string source, string id)
        {
            Obs o = Obs.Log(l, source);
            o.ItemKey = MdmWatch.ScriptKey(id);
            o.ItemName = MdmWatch.ScriptName(id);
            o.WeakName = true;
            o.ItemKind = Kinds.Script;
            o.Origin = Origins.Intune;
            o.PhaseKey = Phases.Scripts;
            o.AddInfo("ID", id.ToLowerInvariant());
            return o;
        }

        static string Flat(string s, int max)
        {
            return Txt.Short(RxSpace.Replace(s ?? "", " ").Trim(), max);
        }

        // True when the line was about a platform script.
        public bool Read(LogLine l, string source, List<Obs> output)
        {
            string t = l.Text ?? "";
            if (source == "AgentExecutor") return Executor(l, t, source, output);
            if (t.IndexOf("[PowerShell]", StringComparison.OrdinalIgnoreCase) < 0) return false;
            Match m = RxStart.Match(t);
            if (m.Success)
            {
                Obs o = For(l, source, m.Groups["id"].Value);
                o.State = State.Installing;
                o.NewAttempt = true;
                o.Text = "Script started";
                o.AddInfo("Context", m.Groups["u"].Value.StartsWith("00000000") ? "Device" : "User");
                output.Add(o);
                return true;
            }
            m = RxResult.Match(t);
            if (m.Success)
            {
                Obs o = For(l, source, m.Groups["id"].Value);
                bool ok = m.Groups["r"].Value.Equals("Success", StringComparison.OrdinalIgnoreCase);
                o.State = ok ? State.Succeeded : State.Failed;
                if (!ok) o.Severity = 2;
                o.Text = "Script finished: " + m.Groups["r"].Value;
                output.Add(o);
                return true;
            }
            return true;
        }

        bool Executor(LogLine l, string t, string source, List<Obs> output)
        {
            Match f = RxFile.Match(t);
            if (f.Success && t.StartsWith("Adding argument", StringComparison.OrdinalIgnoreCase))
            {
                current = f.Groups["id"].Value;
                host = null;
                return true;
            }
            if (current == null) return false;
            if (t.StartsWith("PowerShell path is", StringComparison.OrdinalIgnoreCase))
            {
                host = t.IndexOf("SysWOW64", StringComparison.OrdinalIgnoreCase) >= 0 ? "32-bit" : "64-bit";
                return true;
            }
            Match e = RxExit.Match(t);
            if (e.Success)
            {
                Obs o = For(l, source, current);
                o.ExitCode = Errors.Parse(e.Groups["c"].Value);
                o.Text = "PowerShell exited with exit code " + e.Groups["c"].Value;
                o.AddInfo("PowerShell exit code", e.Groups["c"].Value).AddInfo("PowerShell host", host);
                if (o.ExitCode.HasValue && o.ExitCode.Value != 0) o.Severity = 1;
                output.Add(o);
                return true;
            }
            if (t.StartsWith("error from script =", StringComparison.OrdinalIgnoreCase))
            {
                string err = Flat(t.Substring(19), 600);
                if (err.Length > 0)
                {
                    Obs o = For(l, source, current);
                    o.Severity = 2;
                    o.Text = "Script error: " + err;
                    output.Add(o);
                }
                return true;
            }
            if (t.StartsWith("write output done. output =", StringComparison.OrdinalIgnoreCase))
            {
                string body = t.Substring(27);
                int cut = body.LastIndexOf(", error =", StringComparison.OrdinalIgnoreCase);
                if (cut >= 0) body = body.Substring(0, cut);
                body = Flat(body, 600);
                if (body.Length > 0)
                {
                    Obs o = For(l, source, current);
                    o.Text = "Script output: " + body;
                    output.Add(o);
                }
                return true;
            }
            if (t.StartsWith("Agent executor completed", StringComparison.OrdinalIgnoreCase))
            {
                current = null;
                return true;
            }
            return true;
        }
    }

    // The MDM event log: every policy the MDM client set, by policy area.
    public static class MdmEvents
    {
        public const int SetInt = 813, SetString = 814;

        public static string Value(object v)
        {
            if (v == null) return "";
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                long? n = ImeCodes.Parse(s);
                if (n.HasValue) return n.Value.ToString(CultureInfo.InvariantCulture);
            }
            return s;
        }

        // Event 813 and 814: policy, area, enrollment, device or user, value.
        public static Obs Map(int id, IList<object> props, DateTime timeUtc, string channel, long record, string message)
        {
            if (id != SetInt && id != SetString) return null;
            if (props == null || props.Count < 5) return null;
            string policy = Convert.ToString(props[0], CultureInfo.InvariantCulture), area = Convert.ToString(props[1], CultureInfo.InvariantCulture);
            // "knobs" are the defaults of the Windows image, not something the MDM server sent.
            if (string.IsNullOrEmpty(policy) || string.IsNullOrEmpty(area) || area.Equals("knobs", StringComparison.OrdinalIgnoreCase)) return null;
            string scope = Convert.ToString(props[3], CultureInfo.InvariantCulture) ?? "";
            string value = Value(props[4]);
            Obs o = new Obs();
            o.TimeUtc = timeUtc;
            o.Source = "MDM";
            o.ItemKey = "mdm:area:" + area.ToLowerInvariant();
            o.ItemName = area;
            o.ItemKind = Kinds.Policy;
            o.Origin = Origins.Intune;
            o.PhaseKey = Phases.Policies;
            o.State = State.Succeeded;
            o.Text = policy + " = " + Txt.Short(value, 200) + (scope.Equals("device", StringComparison.OrdinalIgnoreCase) || scope.Length == 0 ? "" : " (user)");
            o.Evidence = new Evidence(EvidenceKinds.EventRecord, channel, (int)Math.Min(int.MaxValue, record), string.IsNullOrEmpty(message) ? "Event " + id + ": " + area + "/" + policy + " = " + value : message);
            return o;
        }
    }
}
