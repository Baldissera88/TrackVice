namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // The four steps the Enrollment Status Page shows for device setup and account setup, from
    // HKLM\SOFTWARE\Microsoft\Enrollments\<enrollment>\FirstSync (device) and FirstSync\<user SID> (account).
    // Timestamp is when the phase started; each ...Duration counts the seconds of that step while it runs and stops when it is done.
    public sealed class EspFirstSync
    {
        public const string EnrollmentsKey = @"HKLM\SOFTWARE\Microsoft\Enrollments";
        public const string ContinuedKind = "ESP continued anyway";

        static readonly string[][] Steps = new string[][]
        {
            new string[] { "PolicyDuration", "policies", "Security policies", Kinds.Policy },
            new string[] { "CertificatesDuration", "certificates", "Certificates", Kinds.Certificate },
            new string[] { "NetworkingDuration", "network", "Network connections", Kinds.Profile },
            new string[] { "ApplicationsDuration", "apps", "Apps", Kinds.Esp },
        };

        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static DateTime? FileTime(RegValue v)
        {
            if (v == null || string.IsNullOrEmpty(v.Data)) return null;
            long ft;
            if (v.Kind == "REG_QWORD" && long.TryParse(v.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out ft)) { }
            else
            {
                // REG_BINARY of eight bytes, written as hex.
                string h = v.Data.Replace(" ", "").Replace(",", "");
                if (h.Length != 16) return null;
                byte[] b = new byte[8];
                for (int i = 0; i < 8; i++)
                    if (!byte.TryParse(h.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out b[i])) return null;
                ft = BitConverter.ToInt64(b, 0);
            }
            if (ft <= 0) return null;
            try { return DateTime.FromFileTimeUtc(ft); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        static long? Num(IRegSource reg, string path, string name)
        {
            RegValue v = reg.Get(path, name);
            return v == null ? null : ImeCodes.Parse(v.Data);
        }

        public List<Obs> Read(IRegSource reg, DateTime nowUtc)
        {
            List<Obs> list = new List<Obs>();
            try
            {
                foreach (string id in reg.SubKeys(EnrollmentsKey))
                {
                    string fs = EnrollmentsKey + "\\" + id + @"\FirstSync";
                    if (reg.Get(fs, "SyncFailureTimeout") == null && reg.Get(fs, "Timestamp") == null) continue;
                    Phase(reg, fs, Phases.DeviceSetup, "device", nowUtc, list);
                    long? cont = Num(reg, fs, "WasContinuedAnyway");
                    if (cont.HasValue && cont.Value == 1 && seen.Add("continued|" + id))
                    {
                        Obs o = Make(fs, "esp:continued", "Continue anyway", Kinds.Esp, Phases.DeviceSetup, State.Skipped, When(reg, fs, nowUtc),
                            "The ESP was left with Continue anyway before device setup was done", "WasContinuedAnyway = 1");
                        o.Severity = 1;
                        list.Add(o);
                    }
                    foreach (string sub in reg.SubKeys(fs))
                    {
                        if (!AccountSetup.IsSid(sub)) continue;
                        Phase(reg, fs + "\\" + sub, Phases.AccountSetup, "user:" + sub, nowUtc, list);
                        AccountDone(reg, sub, list);
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        // HasProvisioningCompleted under the user's ESP tracking key: account setup is over.
        void AccountDone(IRegSource reg, string sid, List<Obs> list)
        {
            string path = EspWatch.Root + "\\" + sid + "\\Setup";
            long? done = Num(reg, path, "HasProvisioningCompleted");
            if (!done.HasValue || done.Value == 0 || !seen.Add("accountdone|" + sid)) return;
            Obs o = new Obs();
            o.Source = "ESP";
            o.AddSessionInfo(EspSteps.AccountDoneInfo, EspSteps.AccountDoneText);
            list.Add(o);
        }

        static DateTime When(IRegSource reg, string path, DateTime nowUtc)
        {
            DateTime? t = reg.WriteTime(path);
            return t.HasValue && t.Value <= nowUtc.AddMinutes(1) ? t.Value : nowUtc;
        }

        static Obs Make(string path, string key, string name, string kind, string phase, State? state, DateTime t, string text, string value)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "ESP";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = kind;
            o.Origin = Origins.Intune;
            o.PhaseKey = phase;
            o.State = state;
            o.Text = text;
            o.Evidence = new Evidence(EvidenceKinds.Registry, path, 0, value);
            return o;
        }

        readonly Dictionary<string, string> said = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Windows counts each ...Duration up while the step runs and stops counting when it is done, so a step is still
        // running while its count reaches the last time the key was written, and that write was a moment ago.
        public static bool Running(DateTime start, long seconds, DateTime? written, DateTime nowUtc)
        {
            if (!written.HasValue) return false;
            DateTime end = start.AddSeconds(seconds);
            return end >= written.Value.AddSeconds(-15) && nowUtc - written.Value < TimeSpan.FromSeconds(90);
        }

        void Phase(IRegSource reg, string path, string phase, string who, DateTime nowUtc, List<Obs> list)
        {
            DateTime? start = FileTime(reg.Get(path, "Timestamp"));
            if (!start.HasValue) return;
            DateTime? written = reg.WriteTime(path);
            foreach (string[] s in Steps)
            {
                long? sec = Num(reg, path, s[0]);
                string key = "esp:step:" + who + ":" + s[1];
                string prev;
                said.TryGetValue(key, out prev);
                if (prev == null)
                {
                    Obs o = Make(path, key, s[2], s[3], phase, State.Installing, start.Value, "ESP: " + s[2].ToLowerInvariant() + " started", "Timestamp");
                    o.NewAttempt = true;
                    list.Add(o);
                    said[key] = "running";
                    prev = "running";
                }
                if (!sec.HasValue || sec.Value < 0) continue;
                if (seen.Add("counted|" + key))
                {
                    // Windows wrote the step's time, so the step ends from it and not from other evidence (EspSteps).
                    Obs c = Make(path, key, s[2], s[3], phase, null, start.Value, null, s[0]);
                    c.AddInfo(EspSteps.CountedKey, "Yes");
                    list.Add(c);
                }
                bool running = Running(start.Value, sec.Value, written, nowUtc);
                if (running && prev != "running")
                {
                    list.Add(Make(path, key, s[2], s[3], phase, State.Installing, nowUtc, "ESP: " + s[2].ToLowerInvariant() + " running again", s[0] + " = " + sec.Value));
                    said[key] = "running";
                }
                else if (!running && prev != "done:" + sec.Value)
                {
                    DateTime end = start.Value.AddSeconds(sec.Value);
                    list.Add(Make(path, key, s[2], s[3], phase, State.Succeeded, end > nowUtc ? nowUtc : end,
                        "ESP: " + s[2].ToLowerInvariant() + " done after " + Txt.DurationShort(TimeSpan.FromSeconds(sec.Value)), s[0] + " = " + sec.Value));
                    said[key] = "done:" + sec.Value;
                }
            }
            long? server = Num(reg, path, "IsServerProvisioningDone");
            if (server.HasValue && server.Value == 1 && seen.Add(phase + "|" + who + "|server"))
                list.Add(Make(path, "esp:step:" + who + ":policies", Steps[0][2], Kinds.Policy, phase, null, When(reg, path, nowUtc), "ESP: everything Intune sends for this phase has arrived", "IsServerProvisioningDone = 1"));
        }
    }
}
