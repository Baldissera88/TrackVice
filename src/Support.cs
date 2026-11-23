namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net.NetworkInformation;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography.X509Certificates;
    using System.Text.RegularExpressions;

    // The keys of the enrolling user and the device facts in Session.Info. They travel with the session, so an upload
    // from an older agent simply has fewer of them, and an older viewer shows them as plain lines on the Session tab.
    public static class DeviceFacts
    {
        public const string User = "User";
        public const string UserName = "User name";
        public const string UserFrom = "User from";
        public const string SiteUser = "Last sign-in the site knows";
        public const string Serial = "Serial number";
        public const string Maker = "Maker";
        public const string Model = "Model";
        public const string Tpm = "TPM";
        public const string SecureBoot = "Secure Boot";
        public const string Bios = "BIOS mode";
        public const string FreeDisk = "Free disk space";
        public const string Network = "Network";
        public const string TimeZone = "Time zone";
        public const string EntraId = "Entra device ID";
        public const string IntuneId = "Intune device ID";
        public const string GroupTag = "Group tag";

        static readonly string[] all = { User, UserName, UserFrom, SiteUser, Serial, Maker, Model, Tpm, SecureBoot, Bios, FreeDisk, Network, TimeZone, EntraId, IntuneId, GroupTag };

        public static bool Is(string key)
        {
            foreach (string k in all) if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The rows of the device card, in order, with only what the session has. Enrolled comes from the MDM enrollment item.
        public static List<Pair> Card(Session s)
        {
            List<Pair> l = new List<Pair>();
            if (s == null) return l;
            Add(l, "User", Who.Label(s));
            if (Who.Label(s) == null) Add(l, "Last sign-in (site)", s.GetInfo(SiteUser));
            Add(l, Serial, s.GetInfo(Serial));
            string maker = s.GetInfo(Maker), model = s.GetInfo(Model);
            string hw = maker != null && model != null ? (model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : maker + " " + model) : maker ?? model;
            Add(l, "Maker and model", hw);
            Add(l, "Windows", s.OsVersion != null && s.OsVersion.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ? s.OsVersion : s.GetInfo("Windows") ?? s.OsVersion);
            Add(l, "Autopilot profile", s.GetInfo("Autopilot profile"));
            Add(l, GroupTag, s.GetInfo(GroupTag));
            DateTime? enrolled = Enrolled(s);
            if (enrolled.HasValue) Add(l, "Enrolled", Txt.Local(enrolled.Value));
            Add(l, EntraId, s.GetInfo(EntraId));
            Add(l, IntuneId, s.GetInfo(IntuneId));
            Add(l, Network, s.GetInfo(Network));
            Add(l, FreeDisk, s.GetInfo(FreeDisk));
            Add(l, Tpm, s.GetInfo(Tpm));
            Add(l, SecureBoot, s.GetInfo(SecureBoot));
            Add(l, Bios, s.GetInfo(Bios));
            Add(l, TimeZone, s.GetInfo(TimeZone));
            return l;
        }

        // When the MDM enrollment of Autopilot finished, from its item in Device preparation.
        public static DateTime? Enrolled(Session s)
        {
            Item i = s.Find(AutopilotEvents.MdmKey);
            if (i == null || i.State != State.Succeeded) return null;
            return i.EndUtc ?? (i.StartUtc ?? (DateTime?)i.LastUtc);
        }

        static void Add(List<Pair> l, string k, string v)
        {
            if (!string.IsNullOrEmpty(v)) l.Add(new Pair(k, v));
        }
    }

    // The person the device is for: the user who enrolled it or signed in, not the account TrackVice runs as.
    public static class Who
    {
        // The UPN. A session from before this was read has the user of Intune or the demo in Session.User.
        public static string Upn(Session s)
        {
            if (s == null) return null;
            string u = s.GetInfo(DeviceFacts.User);
            if (!string.IsNullOrEmpty(u)) return u;
            if (!string.IsNullOrEmpty(s.User) && s.User.IndexOf('@') > 0 && s.User.IndexOf('\\') < 0) return s.User;
            return null;
        }

        public static string Name(Session s)
        {
            if (s == null) return null;
            string n = s.GetInfo(DeviceFacts.UserName);
            return string.IsNullOrEmpty(n) ? null : n;
        }

        // "Anna Hansen (anna.hansen@contoso.com)", or the one that is known, or null.
        public static string Label(Session s)
        {
            return Join(Name(s), Upn(s));
        }

        public static string Join(string name, string upn)
        {
            if (string.IsNullOrEmpty(name)) return string.IsNullOrEmpty(upn) ? null : upn;
            if (string.IsNullOrEmpty(upn) || string.Equals(name, upn, StringComparison.OrdinalIgnoreCase)) return name;
            return name + " (" + upn + ")";
        }

        // The display name when there is one, else the UPN: for tiles and short lines.
        public static string Short(Session s)
        {
            return Name(s) ?? Upn(s);
        }
    }

    // A network adapter as the facts need it.
    public sealed class NetLink
    {
        public string Kind;
        public bool Up, Gateway;

        public NetLink() { }

        public NetLink(string kind, bool up, bool gateway)
        {
            Kind = kind;
            Up = up;
            Gateway = gateway;
        }
    }

    // What the device facts are read from besides the registry. LiveFacts reads this PC; tests fake it.
    public interface IFactSource
    {
        // Rows of a WMI query, or an empty list. May throw.
        List<Dictionary<string, object>> Wmi(string scope, string wql);
        // The subject of a certificate in the computer's personal store, or null.
        string CertSubject(string thumbprint);
        long? FreeBytes { get; }
        long? TotalBytes { get; }
        List<NetLink> Links();
        // "UEFI", "BIOS", or null when Windows does not say.
        string Firmware { get; }
        TimeZoneInfo Zone { get; }
    }

    // Reads the enrolling user, the Entra and Intune device IDs, and the device facts. Everything is read-only.
    public static class FactReader
    {
        public const string JoinInfoKey = @"HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo";
        public const string LogonUiKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI";
        public const string BiosKey = @"HKLM\HARDWARE\DESCRIPTION\System\BIOS";
        public const string SecureBootKey = @"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State";

        static readonly Regex RxAssigned = new Regex("CloudAssignedTenantUpn\\\\?\"\\s*:\\s*\\\\?\"(?<v>[^\"\\\\]+)", RegexOptions.Compiled);
        static readonly Regex RxCn = new Regex(@"(?:^|,)\s*CN=(?<v>[^,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The user and the IDs: from the Entra join, the MDM enrollment, the Autopilot assignment and the last sign-in.
        public static List<Pair> User(IRegSource reg, IFactSource src)
        {
            List<Pair> l = new List<Pair>();
            string upn = null, from = null, entra = null, intune = null;
            foreach (string thumb in Safe(reg.SubKeys(JoinInfoKey)))
            {
                string mail = Str(reg, JoinInfoKey + "\\" + thumb, "UserEmail");
                if (upn == null && Person(mail)) { upn = mail; from = "Entra join"; }
                if (entra == null && src != null)
                {
                    string subject = null;
                    try { subject = src.CertSubject(thumb); }
                    catch (Exception) { }
                    entra = DeviceIdOf(subject);
                }
            }
            foreach (string id in Safe(reg.SubKeys(AutopilotInfo.EnrollmentsKey)))
            {
                string root = AutopilotInfo.EnrollmentsKey + "\\" + id;
                if (!string.Equals(Str(reg, root, "ProviderID"), "MS DM Server", StringComparison.OrdinalIgnoreCase)) continue;
                string u = Str(reg, root, "UPN");
                if (upn == null && Person(u)) { upn = u; from = "MDM enrollment"; }
                string dm = Str(reg, root + @"\DMClient\MS DM Server", "EntDMID");
                if (intune == null && IsGuid(dm)) intune = dm.Trim('{', '}').ToLowerInvariant();
            }
            if (upn == null)
            {
                Match m = RxAssigned.Match(Str(reg, AutopilotInfo.PolicyCacheKey, "PolicyJsonCache") ?? "");
                if (m.Success && Person(m.Groups["v"].Value)) { upn = m.Groups["v"].Value; from = "Autopilot assignment"; }
            }
            string last = Str(reg, LogonUiKey, "LastLoggedOnUser");
            string lastName = Str(reg, LogonUiKey, "LastLoggedOnDisplayName");
            string lastSid = Str(reg, LogonUiKey, "LastLoggedOnUserSID");
            bool real = last != null && last.IndexOf("defaultuser", StringComparison.OrdinalIgnoreCase) < 0 && (lastSid == null || lastSid.StartsWith("S-1-12-1-", StringComparison.Ordinal) || lastSid.StartsWith("S-1-5-21-", StringComparison.Ordinal));
            if (upn == null && real && Person(last)) { upn = last; from = "Last sign-in"; }
            if (upn != null) l.Add(new Pair(DeviceFacts.User, upn));
            if (real && !string.IsNullOrEmpty(lastName) && SamePerson(last, upn)) l.Add(new Pair(DeviceFacts.UserName, lastName.Trim()));
            if (from != null) l.Add(new Pair(DeviceFacts.UserFrom, from));
            if (entra != null) l.Add(new Pair(DeviceFacts.EntraId, entra));
            if (intune != null) l.Add(new Pair(DeviceFacts.IntuneId, intune));
            return l;
        }

        // Read once when TrackVice or the agent starts. WMI calls go through Guard, so a slow WMI cannot hold anything up.
        public static List<Pair> Device(IRegSource reg, IFactSource src, bool wmi)
        {
            List<Pair> l = new List<Pair>();
            string maker = Str(reg, BiosKey, "SystemManufacturer"), model = Str(reg, BiosKey, "SystemProductName");
            if (src != null && wmi)
            {
                List<Dictionary<string, object>> bios = Query(src, @"root\cimv2", "SELECT SerialNumber FROM Win32_BIOS");
                string serial = bios != null && bios.Count > 0 ? Text(bios[0], "SerialNumber") : null;
                if (RealSerial(serial)) l.Add(new Pair(DeviceFacts.Serial, serial.Trim()));
                if (maker == null || model == null)
                {
                    List<Dictionary<string, object>> cs = Query(src, @"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                    if (cs != null && cs.Count > 0) { if (maker == null) maker = Text(cs[0], "Manufacturer"); if (model == null) model = Text(cs[0], "Model"); }
                }
                string tpm = TpmText(Query(src, @"root\cimv2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm"));
                if (tpm != null) l.Add(new Pair(DeviceFacts.Tpm, tpm));
            }
            if (!string.IsNullOrEmpty(maker)) l.Add(new Pair(DeviceFacts.Maker, maker.Trim()));
            if (!string.IsNullOrEmpty(model)) l.Add(new Pair(DeviceFacts.Model, model.Trim()));
            string fw = null;
            if (src != null) { try { fw = src.Firmware; } catch (Exception) { } }
            if (fw != null) l.Add(new Pair(DeviceFacts.Bios, fw == "UEFI" ? "UEFI" : "Legacy BIOS"));
            RegValue sb = reg.Get(SecureBootKey, "UEFISecureBootEnabled");
            long? on = sb == null ? null : ImeCodes.Parse(sb.Data);
            if (on.HasValue) l.Add(new Pair(DeviceFacts.SecureBoot, on.Value == 1 ? "On" : "Off"));
            else if (fw == "BIOS") l.Add(new Pair(DeviceFacts.SecureBoot, "Not available with legacy BIOS"));
            if (src == null) return l;
            try
            {
                long? free = src.FreeBytes, total = src.TotalBytes;
                if (free.HasValue) l.Add(new Pair(DeviceFacts.FreeDisk, Size(free.Value) + (total.HasValue && total.Value > 0 ? " free of " + Size(total.Value) : " free")));
            }
            catch (Exception) { }
            try { string net = NetworkOf(src.Links()); if (net != null) l.Add(new Pair(DeviceFacts.Network, net)); }
            catch (Exception) { }
            try { if (src.Zone != null) l.Add(new Pair(DeviceFacts.TimeZone, ZoneText(src.Zone))); }
            catch (Exception) { }
            return l;
        }

        static List<Dictionary<string, object>> Query(IFactSource src, string scope, string wql)
        {
            List<Dictionary<string, object>> rows = null;
            Exception err;
            // Null when WMI failed or did not answer, so that is not taken for "not found".
            if (!Guard.Run(delegate { try { rows = src.Wmi(scope, wql); } catch (Exception) { rows = null; } }, TimeSpan.FromSeconds(15), out err)) return null;
            return rows;
        }

        // "Ready, version 2.0", "Present, turned off", or "Not found".
        public static string TpmText(List<Dictionary<string, object>> rows)
        {
            if (rows == null) return null;
            if (rows.Count == 0) return "Not found";
            Dictionary<string, object> r = rows[0];
            string spec = Text(r, "SpecVersion");
            string version = spec != null ? spec.Split(',')[0].Trim() : null;
            bool enabled = Flag(r, "IsEnabled_InitialValue"), active = Flag(r, "IsActivated_InitialValue");
            string state = enabled && active ? "Ready" : enabled ? "Present, not activated" : "Present, turned off";
            return state + (string.IsNullOrEmpty(version) ? "" : ", version " + version);
        }

        // "Wired", "Wi-Fi", "Wired and Wi-Fi", "Mobile", or "No network" when no adapter that is up has a gateway.
        public static string NetworkOf(List<NetLink> links)
        {
            if (links == null) return null;
            bool wired = false, wifi = false, mobile = false;
            foreach (NetLink n in links)
            {
                if (!n.Up || !n.Gateway) continue;
                if (n.Kind == "Wi-Fi") wifi = true;
                else if (n.Kind == "Mobile") mobile = true;
                else if (n.Kind == "Wired") wired = true;
            }
            if (wired && wifi) return "Wired and Wi-Fi";
            if (wired) return "Wired";
            if (wifi) return "Wi-Fi";
            if (mobile) return "Mobile";
            return "No network";
        }

        // "W. Europe Standard Time (UTC+01:00)".
        public static string ZoneText(TimeZoneInfo z)
        {
            TimeSpan o = z.BaseUtcOffset;
            string sign = o < TimeSpan.Zero ? "-" : "+";
            if (o < TimeSpan.Zero) o = o.Negate();
            return z.Id + " (UTC" + sign + o.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + o.Minutes.ToString("00", CultureInfo.InvariantCulture) + ")";
        }

        // 182 GB, 9.4 GB, 512 MB.
        public static string Size(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 10) return Math.Round(gb).ToString("0", CultureInfo.InvariantCulture) + " GB";
            if (gb >= 1) return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return Math.Round(bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        // The device ID is the subject of the certificate the Entra join made: CN=<device ID>.
        public static string DeviceIdOf(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return null;
            Match m = RxCn.Match(subject);
            string v = m.Success ? m.Groups["v"].Value.Trim() : null;
            return IsGuid(v) ? v.ToLowerInvariant() : null;
        }

        // A user account, not the placeholder pre-provisioning and self-deploying use.
        static bool Person(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('@') <= 0) return false;
            return !s.StartsWith("foouser@", StringComparison.OrdinalIgnoreCase) && s.IndexOf("defaultuser", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // Whether the last sign-in (AzureAD\AnnaHansen, CONTOSO\anna.hansen or a UPN) is the same person as the UPN.
        public static bool SamePerson(string account, string upn)
        {
            if (string.IsNullOrEmpty(upn)) return true;
            if (string.IsNullOrEmpty(account)) return false;
            string a = account.Substring(account.LastIndexOf('\\') + 1);
            int at = a.IndexOf('@');
            if (at > 0) a = a.Substring(0, at);
            string u = upn.Substring(0, Math.Max(0, upn.IndexOf('@')));
            return Squash(a) == Squash(u) && a.Length > 0;
        }

        static string Squash(string s)
        {
            return s.Replace(".", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
        }

        static bool RealSerial(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Trim().Length < 3) return false;
            string t = s.Trim().ToLowerInvariant();
            return t != "system serial number" && t != "to be filled by o.e.m." && t != "default string" && t != "0";
        }

        static bool IsGuid(string s)
        {
            Guid g;
            return !string.IsNullOrEmpty(s) && Guid.TryParse(s.Trim('{', '}'), out g) && g != Guid.Empty;
        }

        static string Str(IRegSource reg, string path, string name)
        {
            RegValue v;
            try { v = reg.Get(path, name); }
            catch (Exception) { return null; }
            return v == null || string.IsNullOrEmpty(v.Data) ? null : v.Data;
        }

        static string[] Safe(string[] a)
        {
            return a ?? new string[0];
        }

        static string Text(Dictionary<string, object> r, string k)
        {
            object v;
            return r.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        static bool Flag(Dictionary<string, object> r, string k)
        {
            object v;
            if (!r.TryGetValue(k, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            return string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    // When the facts are read: the device once, the user and IDs now and then, since the user signs in during the deployment.
    // Only what changed goes into the batch.
    public sealed class FactWatch
    {
        public IFactSource Source = new LiveFacts();
        string lastUser;

        public List<Pair> Read(IRegSource reg, int reads, bool wmi)
        {
            List<Pair> l = new List<Pair>();
            try
            {
                if (reads == 1) l.AddRange(FactReader.Device(reg, Source, wmi));
                if (reads == 1 || reads % 10 == 0)
                {
                    List<Pair> u = FactReader.User(reg, Source);
                    string sig = "";
                    foreach (Pair p in u) sig += p.Key + "=" + p.Value + ";";
                    if (sig != lastUser) { lastUser = sig; l.AddRange(u); }
                }
            }
            catch (Exception) { }
            return l;
        }
    }

    // This PC.
    public sealed class LiveFacts : IFactSource
    {
        [DllImport("kernel32.dll")]
        static extern bool GetFirmwareType(out int type);

        public List<Dictionary<string, object>> Wmi(string scope, string wql)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(scope, wql))
            using (System.Management.ManagementObjectCollection c = s.Get())
                foreach (System.Management.ManagementBaseObject o in c)
                {
                    Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Management.PropertyData p in o.Properties) r[p.Name] = p.Value;
                    l.Add(r);
                    o.Dispose();
                }
            return l;
        }

        public string CertSubject(string thumbprint)
        {
            X509Store st = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            try
            {
                st.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                X509Certificate2Collection found = st.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
                return found.Count > 0 ? found[0].Subject : null;
            }
            finally { st.Close(); }
        }

        DriveInfo SystemDrive
        {
            get { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)); }
        }

        public long? FreeBytes
        {
            get { return SystemDrive.AvailableFreeSpace; }
        }

        public long? TotalBytes
        {
            get { return SystemDrive.TotalSize; }
        }

        public List<NetLink> Links()
        {
            List<NetLink> l = new List<NetLink>();
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                string kind;
                switch (n.NetworkInterfaceType)
                {
                    case NetworkInterfaceType.Wireless80211: kind = "Wi-Fi"; break;
                    case NetworkInterfaceType.Ethernet:
                    case NetworkInterfaceType.FastEthernetT:
                    case NetworkInterfaceType.FastEthernetFx:
                    case NetworkInterfaceType.GigabitEthernet:
                    case NetworkInterfaceType.Ethernet3Megabit: kind = "Wired"; break;
                    case NetworkInterfaceType.Wwanpp:
                    case NetworkInterfaceType.Wwanpp2: kind = "Mobile"; break;
                    default: continue;
                }
                bool gw = false;
                try { gw = n.GetIPProperties().GatewayAddresses.Count > 0; }
                catch (Exception) { }
                l.Add(new NetLink(kind, n.OperationalStatus == OperationalStatus.Up, gw));
            }
            return l;
        }

        public string Firmware
        {
            get
            {
                int t;
                try { if (GetFirmwareType(out t)) return t == 2 ? "UEFI" : t == 1 ? "BIOS" : null; }
                catch (Exception) { }
                return null;
            }
        }

        public TimeZoneInfo Zone
        {
            get { return TimeZoneInfo.Local; }
        }
    }

    // Made-up users and facts for the demos.
    public static class DemoFacts
    {
        public static void Autopilot(List<Pair> info)
        {
            info.Add(new Pair(DeviceFacts.UserName, "Anna Hansen"));
            info.Add(new Pair(DeviceFacts.UserFrom, "Entra join"));
            info.Add(new Pair(DeviceFacts.Serial, "5CG2041KQZ"));
            info.Add(new Pair(DeviceFacts.Maker, "HP"));
            info.Add(new Pair(DeviceFacts.Model, "EliteBook 840 G9"));
            info.Add(new Pair(DeviceFacts.EntraId, "6f1d2c4a-8b3e-4d5f-9a1b-2c3d4e5f6a7b"));
            info.Add(new Pair(DeviceFacts.IntuneId, "0b7e4f2d-1c9a-4e8b-a3d6-5f2e1c0b9a84"));
            info.Add(new Pair(DeviceFacts.Network, "Wi-Fi"));
            info.Add(new Pair(DeviceFacts.FreeDisk, "182 GB free of 237 GB"));
            info.Add(new Pair(DeviceFacts.Tpm, "Ready, version 2.0"));
            info.Add(new Pair(DeviceFacts.SecureBoot, "On"));
            info.Add(new Pair(DeviceFacts.Bios, "UEFI"));
            info.Add(new Pair(DeviceFacts.TimeZone, "Romance Standard Time (UTC+01:00)"));
        }

        public static void Osd(List<Pair> info)
        {
            info.Add(new Pair(DeviceFacts.Serial, "CZC3214XV8"));
            info.Add(new Pair(DeviceFacts.Maker, "Contoso Hardware"));
            info.Add(new Pair(DeviceFacts.Model, "ProDesk 600"));
            info.Add(new Pair(DeviceFacts.Network, "Wired"));
            info.Add(new Pair(DeviceFacts.FreeDisk, "411 GB free of 476 GB"));
            info.Add(new Pair(DeviceFacts.Tpm, "Ready, version 2.0"));
            info.Add(new Pair(DeviceFacts.SecureBoot, "On"));
            info.Add(new Pair(DeviceFacts.Bios, "UEFI"));
            info.Add(new Pair(DeviceFacts.TimeZone, "Pacific Standard Time (UTC-08:00)"));
        }

        static readonly string[][] People =
        {
            new string[] { "Lars Berg", "lars.berg@contoso.com" },
            new string[] { "Mette Holm", "mette.holm@contoso.com" },
            new string[] { "Jonas Lund", "jonas.lund@contoso.com" },
            new string[] { "Sofie Dahl", "sofie.dahl@contoso.com" },
            new string[] { "Peter Krogh", "peter.krogh@contoso.com" },
            new string[] { "Ida Winther", "ida.winther@contoso.com" },
        };

        // Gives each demo device with the agent a user and serial number of its own.
        public static void Vary(Session s, string computer)
        {
            if (s == null || string.IsNullOrEmpty(computer)) return;
            int h = 0;
            foreach (char c in computer) h = (h * 31 + c) & 0x7fffffff;
            string serial = s.GetInfo(DeviceFacts.Serial);
            if (serial != null) s.SetInfo(DeviceFacts.Serial, serial.Substring(0, 4) + (h % 1000000).ToString("000000", CultureInfo.InvariantCulture));
            if (Who.Upn(s) == null) return;
            string[] p = People[h % People.Length];
            s.SetInfo(DeviceFacts.UserName, p[0]);
            s.SetInfo(DeviceFacts.User, p[1]);
        }
    }

    // The site has no user per task sequence run (SMS_TaskSequenceExecutionStatus has none). SMS_R_System has the user
    // its discovery last saw signed in, which can be the user of the device before it was set up again.
    public static class SiteUser
    {
        public static string Of(ISccmQuery q, long resourceId)
        {
            try
            {
                foreach (Dictionary<string, object> r in q.Query("SELECT LastLogonUserName, LastLogonUserDomain FROM SMS_R_System WHERE ResourceID = " + resourceId.ToString(CultureInfo.InvariantCulture)))
                {
                    object n, d;
                    r.TryGetValue("LastLogonUserName", out n);
                    r.TryGetValue("LastLogonUserDomain", out d);
                    string name = n as string, dom = d as string;
                    if (string.IsNullOrEmpty(name)) return null;
                    return string.IsNullOrEmpty(dom) ? name : dom + "\\" + name;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
