namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    // Account setup: the user phase of the ESP, kept under the SID of the user who signed in.
    // https://learn.microsoft.com/troubleshoot/mem/intune/device-enrollment/understand-troubleshoot-esp
    public static class AccountSetup
    {
        static readonly Regex RxSid = new Regex(@"^S-1-\d+(-\d+)+$", RegexOptions.Compiled);

        public static bool IsSid(string s)
        {
            return s != null && RxSid.IsMatch(s);
        }

        // Subkeys named by the time the ESP saved them, oldest first. Others are left out.
        public static List<KeyValuePair<DateTime, string>> Times(string[] subs)
        {
            List<KeyValuePair<DateTime, string>> times = new List<KeyValuePair<DateTime, string>>();
            foreach (string sub in subs)
            {
                DateTime t;
                if (DateTime.TryParse(sub, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                    times.Add(new KeyValuePair<DateTime, string>(t, sub));
            }
            times.Sort(delegate (KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b) { return a.Key.CompareTo(b.Key); });
            return times;
        }

        public static void MoveToAccount(Obs o, string sid)
        {
            o.PhaseKey = Phases.AccountSetup;
            if (o.Info != null)
                foreach (Pair p in o.Info)
                    if (p.Key == "ESP phase") p.Value = Phases.Name(Phases.AccountSetup);
        }
    }

    // Autopilot milestones in the event logs: plain words on the timeline, and items in Device preparation.
    // Event IDs and their fields are from the providers' own manifests (as listed in github.com/jdu2600/Windows10EtwEvents),
    // the ones Microsoft documents in learn.microsoft.com/autopilot/troubleshoot-oobe, and Get-AutopilotDiagnostics.
    public static class AutopilotEvents
    {
        public const string Autopilot = "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot";
        public const string MdmAdmin = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin";
        public const string MdmOperational = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Operational";
        public const string Registration = "Microsoft-Windows-User Device Registration/Admin";
        public const string ShellCore = "Microsoft-Windows-Shell-Core/Operational";

        public const string TpmKey = "autopilot:tpm", EnrollKey = "autopilot:enrollment", MdmKey = "autopilot:mdm";
        public const string OdjKey = "autopilot:odj", DcKey = "autopilot:dc", JoinKey = "autopilot:hybridjoin", PreProvKey = "autopilot:preprovisioning";
        public const string DppInfo = "Device preparation page";

        static readonly Regex RxStates = new Regex(@"from (?<a>\S+) to (?<b>[^\s.]+)", RegexOptions.Compiled);

        // Events that are not errors or warnings but are read as well.
        public static int[] Extra(string channel)
        {
            if (Same(channel, MdmAdmin)) return new int[] { 72, 105, 106, 107, 109, 110, 111 };
            if (Same(channel, MdmOperational)) return new int[] { 101 };
            if (Same(channel, Registration)) return new int[] { 306, 335 };
            if (Same(channel, ShellCore)) return new int[] { 62407 };
            return new int[0];
        }

        // The event query's condition for a channel: every event ("all"), errors and warnings ("errors"), those and the
        // policies the MDM client set ("errors+policy"), or only the events above ("ids").
        public static string Filter(string channel, string what)
        {
            List<string> parts = new List<string>();
            if (what == "all") parts.Add("Level>=0");
            else if (what != "ids") parts.Add("Level=1 or Level=2 or Level=3");
            if (what == "errors+policy")
            {
                parts.Add("EventID=" + MdmEvents.SetInt.ToString(CultureInfo.InvariantCulture));
                parts.Add("EventID=" + MdmEvents.SetString.ToString(CultureInfo.InvariantCulture));
            }
            if (what != "all")
                foreach (int id in Extra(channel)) parts.Add("EventID=" + id.ToString(CultureInfo.InvariantCulture));
            if (parts.Count == 0) parts.Add("EventID=0");
            return "(" + string.Join(" or ", parts.ToArray()) + ")";
        }

        static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // What an event means. An empty list: not a known milestone, so it goes on the timeline as it is.
        public static List<Obs> Map(string channel, int id, IList<object> props, string message, DateTime timeUtc, long record)
        {
            List<Obs> l = new List<Obs>();
            Ctx c = new Ctx(channel, id, props ?? new List<object>(), message, timeUtc, record, l);
            if (Same(channel, Autopilot)) MapAutopilot(c);
            else if (Same(channel, MdmAdmin) || Same(channel, MdmOperational)) MapMdm(c);
            else if (Same(channel, Registration)) MapRegistration(c);
            else if (Same(channel, ShellCore)) MapShell(c);
            return l;
        }

        sealed class Ctx
        {
            public readonly string Channel, Message;
            public readonly int Id;
            public readonly IList<object> Props;
            public readonly DateTime Time;
            public readonly long Record;
            public readonly List<Obs> Out;

            public Ctx(string channel, int id, IList<object> props, string message, DateTime time, long record, List<Obs> output)
            {
                Channel = channel;
                Id = id;
                Props = props;
                Message = message;
                Time = time;
                Record = record;
                Out = output;
            }

            public string P(int i)
            {
                return i < Props.Count && Props[i] != null ? Convert.ToString(Props[i], CultureInfo.InvariantCulture) : "";
            }

            // An HRESULT field, as a signed 32-bit code like the rest of TrackVice keeps them.
            public long? Code(int i)
            {
                long? n = Errors.Parse(P(i));
                if (n.HasValue && n.Value > int.MaxValue && n.Value <= uint.MaxValue) n = unchecked((int)(uint)n.Value);
                return n;
            }

            public string Err(int i)
            {
                long? n = Code(i);
                return n.HasValue ? Errors.Describe(n.Value) : "an unknown error";
            }

            public Obs Add(string source, int sev, string text)
            {
                Obs o = new Obs();
                o.TimeUtc = Time;
                o.Source = source;
                o.Severity = sev;
                o.Text = text;
                o.Evidence = new Evidence(EvidenceKinds.EventRecord, Channel, (int)Math.Min(int.MaxValue, Record), string.IsNullOrEmpty(Message) ? "Event " + Id : Message.Trim());
                Out.Add(o);
                return o;
            }

            // An event that moves a Device preparation item.
            public Obs Item(string source, string key, string name, State? state, int sev, string text)
            {
                Obs o = Add(source, sev, text);
                o.ItemKey = key;
                o.ItemName = name;
                o.ItemKind = Kinds.Enrollment;
                o.Origin = Origins.Windows;
                o.PhaseKey = Phases.DevicePreparation;
                o.State = state;
                return o;
            }
        }

        // ---- Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot ----

        const string Tpm = "TPM attestation", Enroll = "Device enrollment by Autopilot";

        static void MapAutopilot(Ctx c)
        {
            const string S = "Autopilot";
            switch (c.Id)
            {
                // learn.microsoft.com/autopilot/troubleshoot-oobe: 100 is usually only a wait for the profile.
                case 100: c.Add(S, 0, "Autopilot setting " + c.P(0) + " is not set yet. This is normal until the profile is downloaded."); break;
                case 101:
                case 103: c.Add(S, 0, "Autopilot profile setting: " + c.P(0) + " = " + c.P(1)); break;
                case 109: c.Add(S, 0, "Setup screen setting from the profile: " + c.P(0) + " = " + c.P(1)); break;
                case 111: c.Add(S, 0, "Autopilot read the profile settings for the setup screens."); break;
                case 115: c.Add(S, 2, "Autopilot found no valid MDM for the tenant: " + c.Err(0) + ". The tenant must be licensed for exactly one MDM."); break;
                case 153:
                    {
                        Match m = RxStates.Match(c.Message ?? "");
                        string to = m.Success ? m.Groups["b"].Value : c.P(1);
                        if (to.IndexOf("Available", StringComparison.OrdinalIgnoreCase) >= 0) c.Add(S, 0, "Autopilot profile downloaded. The device is ready to deploy with Autopilot.");
                        else c.Add(S, 0, "Autopilot profile state changed from " + (m.Success ? m.Groups["a"].Value : c.P(0)) + " to " + to + ".");
                        break;
                    }
                case 160: c.Add(S, 0, "Autopilot is downloading its profile."); break;
                case 161: c.Add(S, 0, "Autopilot profile downloaded."); break;
                case 163: c.Add(S, 0, "The Autopilot profile is already on this device, so it was not downloaded again."); break;
                case 164: c.Add(S, 0, "The Internet can be reached for the profile download."); break;
                case 165: c.Add(S, 1, "The Internet cannot be reached yet. The profile download waits for it."); break;
                case 170: c.Add(S, 0, "Autopilot profile download complete."); break;
                case 172: c.Add(S, 2, "Autopilot could not mark the profile as available: " + c.Err(0) + "."); break;
                case 185: c.Add(S, 2, "Autopilot could not read its settings: " + c.Err(0) + "."); break;
                case 314: c.Add(S, 2, "The Autopilot profile could not be downloaded. Its settings are not used, and the device is not managed by Autopilot."); break;
                case 706: c.Add(S, 1, "The Autopilot profile download failed: " + c.Err(0) + "."); break;
                case 707: c.Add(S, 2, "The Autopilot profile download failed: " + c.Err(0) + "."); break;

                // TPM attestation, for self-deploying mode and pre-provisioning.
                case 150:
                case 151:
                case 232: c.Item(S, TpmKey, Tpm, State.Installing, 0, "TPM attestation started."); break;
                case 177: c.Item(S, TpmKey, Tpm, State.Installing, 0, "Preparing the TPM for attestation, try " + c.P(0) + " of " + c.P(1) + "."); break;
                case 221: c.Item(S, TpmKey, Tpm, State.Installing, 0, "TPM attestation started, phase " + c.P(0) + "."); break;
                case 227: c.Item(S, TpmKey, Tpm, null, 0, "TPM attestation phase " + c.P(1) + " finished."); break;
                case 169: c.Item(S, TpmKey, Tpm, State.Succeeded, 0, "TPM identity confirmed."); break;
                case 224: c.Item(S, TpmKey, Tpm, State.Succeeded, 0, "TPM attestation was already done."); break;
                case 229: c.Item(S, TpmKey, Tpm, State.Succeeded, 0, "TPM attestation finished" + Ms(c.P(1)) + "."); break;
                case 156: c.Item(S, TpmKey, Tpm, State.Failed, 2, "The profile requires TPM attestation, but the TPM is not set up for it. Autopilot cannot go on."); break;
                case 171: Fail(c, TpmKey, Tpm, 0, "Autopilot could not confirm the TPM identity"); break;
                case 189: c.Item(S, TpmKey, Tpm, State.Failed, 2, "Preparing the TPM failed on every try."); break;
                case 211: Fail(c, TpmKey, Tpm, 0, "The TPM failed the attestation check"); break;
                case 217: Fail(c, TpmKey, Tpm, 0, "TPM attestation failed"); break;
                case 226: Fail(c, TpmKey, Tpm, 0, "TPM attestation failed in phase " + c.P(1) + " at step " + c.P(2)); break;
                case 230:
                case 231: Fail(c, TpmKey, Tpm, 0, "TPM attestation failed"); break;
                case 243: c.Item(S, TpmKey, Tpm, State.Failed, 2, "TPM attestation timed out."); break;

                // The device enrollment Autopilot does itself with the TPM: Entra join and MDM enrollment without a user.
                case 178: c.Item(S, EnrollKey, Enroll, State.Installing, 0, "Autopilot started the device enrollment.").AddInfo("What", "Entra join and MDM enrollment with the TPM, as in self-deploying mode and pre-provisioning"); break;
                case 179: c.Item(S, EnrollKey, Enroll, State.Installing, 0, "Device enrollment phase " + c.P(0) + " started."); break;
                case 181: c.Item(S, EnrollKey, Enroll, null, 0, "Device enrollment phase " + c.P(0) + " finished."); break;
                case 192: c.Item(S, EnrollKey, Enroll, null, 1, "Device enrollment failed with " + c.Err(0) + ". Autopilot tries again, try " + c.P(2) + " of " + c.P(3) + "."); break;
                case 303: c.Item(S, EnrollKey, Enroll, State.Succeeded, 0, "Device enrollment succeeded."); break;
                case 180: Fail(c, EnrollKey, Enroll, 1, "Device enrollment failed in phase " + c.P(0)); break;
                case 300: Fail(c, EnrollKey, Enroll, 0, "Device enrollment could not start"); break;
                case 301:
                case 302: Fail(c, EnrollKey, Enroll, 1, "Device enrollment failed in stage " + c.P(0)); break;

                // Autopilot device preparation, which shows its own page instead of the ESP.
                case 409:
                case 410:
                case 411:
                case 419:
                    {
                        Obs o = c.Add(S, c.Id == 410 ? 0 : c.Id == 419 ? 2 : 1,
                            c.Id == 410 ? "The device preparation page settings were loaded."
                            : c.Id == 409 ? "The device preparation page settings are missing or not valid, so the defaults are used."
                            : c.Id == 411 ? "A device preparation page setting could not be read: " + c.P(1) + "."
                            : "The device preparation page could not start: " + c.Err(0) + ".");
                        o.AddSessionInfo(DppInfo, "Seen in the Autopilot event log " + Txt.Local(c.Time));
                        break;
                    }
            }
        }

        static void Fail(Ctx c, string key, string name, int field, string what)
        {
            Obs o = c.Item("Autopilot", key, name, State.Failed, 2, what + ": " + c.Err(field) + ".");
            long? code = c.Code(field);
            if (code.HasValue && code.Value != 0) o.ExitCode = code;
        }

        static string Ms(string ms)
        {
            long n;
            if (!long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n <= 0) return "";
            return " in " + Txt.DurationShort(TimeSpan.FromMilliseconds(n));
        }

        // ---- MDM: enrollment and offline domain join (hybrid Entra join) ----

        const string Odj = "Offline domain join", Dc = "Domain controller check";

        static void MapMdm(Ctx c)
        {
            const string S = "Hybrid join";
            if (Same(c.Channel, MdmOperational))
            {
                if (c.Id == 101) c.Item(S, DcKey, Dc, State.Succeeded, 0, "A domain controller was reached" + Ms(MdmEvents.Value(c.Props.Count > 0 ? c.Props[0] : null)) + ".");
                return;
            }
            switch (c.Id)
            {
                case 72: c.Item("MDM", MdmKey, "MDM enrollment", State.Succeeded, 0, "MDM enrollment succeeded."); break;
                case 111:
                    {
                        c.Item(S, OdjKey, Odj, State.Installing, 0, "Waiting for the domain join blob from the Intune Connector for Active Directory.");
                        bool need = c.P(0).Equals("true", StringComparison.OrdinalIgnoreCase) || c.P(0) == "1";
                        if (need) c.Item(S, DcKey, Dc, State.Pending, 0, "A domain controller must be reachable before setup goes on.");
                        else c.Item(S, DcKey, Dc, State.NotApplicable, 0, "The domain controller check is skipped, as set in the Autopilot profile.");
                        break;
                    }
                case 107: c.Item(S, OdjKey, Odj, State.Succeeded, 0, "The domain join blob was applied."); break;
                case 108:
                    {
                        Obs o = c.Item(S, OdjKey, Odj, State.Failed, 2, "Applying the domain join blob failed: " + c.Err(0) + ".");
                        o.ExitCode = c.Code(0);
                        break;
                    }
                case 109:
                case 110:
                    {
                        // The state values as Get-AutopilotDiagnostics reads them (oofhours.com, Michael Niehaus).
                        long? st = Errors.Parse(MdmEvents.Value(c.Props.Count > 0 ? c.Props[0] : null));
                        if (st == 1) c.Item(S, OdjKey, Odj, State.Installing, 0, "Waiting for the domain join blob.");
                        else if (st == 2) c.Item(S, OdjKey, Odj, State.Succeeded, 0, "The domain join blob was processed.");
                        else if (st == 3) c.Item(S, OdjKey, Odj, State.Failed, 2, "Timed out waiting for the domain join blob or for a domain controller.");
                        else c.Add(S, 0, "Offline domain join state: " + (st.HasValue ? st.Value.ToString(CultureInfo.InvariantCulture) : c.P(0)) + (st == 0 ? " (not used)" : "") + ".");
                        break;
                    }
                case 105: c.Item(S, DcKey, Dc, State.Installing, 0, "Looking up a domain controller."); break;
                case 106: c.Item(S, DcKey, Dc, State.Installing, 0, "Pinging the domain controller."); break;
                case 100:
                    {
                        Obs o = c.Item(S, DcKey, Dc, State.Failed, 2, "No domain controller could be reached" + Ms(MdmEvents.Value(c.Props.Count > 0 ? c.Props[0] : null)) + ": " + c.Err(1) + ".");
                        o.ExitCode = c.Code(1);
                        break;
                    }
            }
        }

        // ---- Hybrid Entra join: the device registers itself in Entra ID after it joined the domain ----

        const string Join = "Hybrid Entra join";

        static void MapRegistration(Ctx c)
        {
            const string S = "Device registration";
            switch (c.Id)
            {
                case 306: c.Item(S, JoinKey, Join, State.Succeeded, 0, "Hybrid Entra join registration succeeded."); break;
                case 304: c.Item(S, JoinKey, Join, State.Pending, 1, "Hybrid Entra join registration failed at the join phase, exit code " + Errors.Short(c.Code(0)) + ". Windows tries again later."); break;
                case 305: c.Item(S, JoinKey, Join, State.Pending, 1, "Hybrid Entra join registration could not get a token, exit code " + Errors.Short(c.Code(0)) + ". Windows tries again later."); break;
                case 334: c.Item(S, JoinKey, Join, State.Pending, 1, "Hybrid Entra join cannot start: no domain controller could be found."); break;
                case 335: c.Add(S, 0, "The device is already joined to Entra ID."); break;
            }
        }

        // ---- Shell-Core: the setup screens. Event 62407 carries the name of what the screen did. ----
        // The names are not documented by Microsoft; they are the ones github.com/okieselbach/AutopilotMonitor reads.

        static void MapShell(Ctx c)
        {
            const string S = "Windows setup";
            string name = c.P(0) + " " + (c.Message ?? "");
            if (c.Id != 62407 || name.IndexOf("CommercialOOBE_ESPProgress", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (name.IndexOf("WhiteGlove_Success", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Obs o = c.Item(S, PreProvKey, "Pre-provisioning", State.Succeeded, 0, "Pre-provisioning: the technician phase finished. The device can be resealed and given to the user.");
                o.AddSessionInfo("Pre-provisioning", "Technician phase finished, waiting for the user");
            }
            else if (name.IndexOf("WhiteGlove_Failed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Obs o = c.Item(S, PreProvKey, "Pre-provisioning", State.Failed, 2, "Pre-provisioning failed in the technician phase.");
                o.AddSessionInfo("Pre-provisioning", "Technician phase failed");
            }
            else if (name.IndexOf("Page_Exiting", StringComparison.OrdinalIgnoreCase) >= 0) c.Add(S, 0, "The Enrollment Status Page finished a phase.");
            else if (name.IndexOf("_Failure", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("_Timeout", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("_Abort", StringComparison.OrdinalIgnoreCase) >= 0)
                c.Add(S, 2, "The Enrollment Status Page showed an error.");
        }
    }
}
