namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // Where settings are kept, so tests can use memory instead of HKCU\Software\TrackVice.
    public interface ISettingStore
    {
        string Get(string name);
        // A null value removes the setting, so its default applies again.
        void Set(string name, string value);
    }

    public sealed class MemorySettingStore : ISettingStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Get(string name)
        {
            string v;
            return Values.TryGetValue(name, out v) ? v : null;
        }

        public void Set(string name, string value)
        {
            if (value == null) Values.Remove(name);
            else Values[name] = value;
        }
    }

    // One subject of the Settings window.
    public sealed class SettingPage
    {
        public string Id, Title, Summary;
    }

    // One setting as Settings shows and finds it.
    public sealed class SettingDef
    {
        public string Id, Page, Title, Summary, More, Keywords;
        // The title of the row when another part of TrackVice makes the row, such as Your storage or Updates.
        public string Row;
        // The values under HKCU\Software\TrackVice and what each is when nothing is saved. Only settings with
        // defaults get the changed mark and are reset. Customers, sign-ins and Your storage have none.
        public string[] Keys = new string[0];
        public string[] Defaults = new string[0];

        public bool Resettable
        {
            get { return Keys.Length > 0 && Defaults.Length == Keys.Length; }
        }

        public bool Changed(ISettingStore s)
        {
            if (!Resettable || s == null) return false;
            for (int i = 0; i < Keys.Length; i++)
            {
                if (!Same(s.Get(Keys[i]), Defaults[i])) return true;
            }
            return false;
        }

        // Removes the saved values, so the defaults apply again.
        public int Reset(ISettingStore s)
        {
            if (!Resettable || s == null) return 0;
            int n = 0;
            foreach (string k in Keys)
            {
                if (s.Get(k) == null) continue;
                s.Set(k, null);
                n++;
            }
            return n;
        }

        // Nothing saved is the default. Yes and no are kept as 1 and 0, and some older values as true and false.
        static bool Same(string value, string fallback)
        {
            if (value == null) return true;
            string v = value.Trim();
            if (fallback == "1" || fallback == "0")
            {
                bool on = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                bool off = v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
                if (on || off) return on == (fallback == "1");
            }
            return string.Equals(v, fallback, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The subjects of the Settings window and every setting on them, in the order they are shown.
    public static class SettingsCatalog
    {
        public const string General = "general", ThisDevice = "device", Customers = "customers", Intune = "intune",
            Sccm = "sccm", Storage = "storage", LogVice = "logvice", Data = "data";

        // The subject Settings opened on last.
        public const string LastPagePref = "SettingsPage";

        public static readonly SettingPage[] Pages =
        {
            P(General, "General", "The theme, what TrackVice does when it opens, and updates from GitHub."),
            P(ThisDevice, "This device", "What TrackVice reads on this PC, how far back and how often."),
            P(Customers, "Customers", "The customers you work for and their customer codes."),
            P(Intune, "Intune / cloud", "The sign-in to Intune, and what TrackVice reads from it."),
            P(Sccm, "SCCM / on-premises", "The SMS Provider TrackVice connects to."),
            P(Storage, "Your storage and agents", "Your storage, publishing TrackVice to it, and agent updates."),
            P(LogVice, "LogVice", "Where LogVice is."),
            P(Data, "Data and privacy", "What TrackVice keeps on this PC and what it sends."),
        };

        public static readonly SettingDef[] All =
        {
            // General
            D("theme", General, "Theme", "The colours of TrackVice.",
                "System follows the Windows app mode. Terminal and Amber look like old green and amber screens, with numbers and times in a monospace font.",
                "appearance colour color dark light vice terminal amber synthwave", "Theme", Palettes.Default),
            D("watchonstart", General, "Watch when TrackVice opens", "Start following this device as soon as TrackVice has read it.",
                "When it is off, press Watch or Ctrl+W.", "start open follow", "WatchOnStart", "1"),
            D("updatecheck", General, "Check for updates", "Once a day at start, TrackVice asks GitHub for the latest version.",
                "A line in the status bar shows when there is a newer one. Nothing about you or your devices is sent. Never on the command line, as the agent, in Windows PE, during Windows setup or the ESP, or as SYSTEM. Administrators can turn it off for everyone with the DWORD DisableUpdateCheck = 1 under HKLM\\SOFTWARE\\Policies\\TrackVice.",
                "github version release", "UpdateCheck", "1"),
            Row(D("updates", General, "Updates", "The version you have and the newest on GitHub, with Check now and Install.", null, "github version release install download"), "Updates"),

            // This device
            D("history", ThisDevice, "History to read", "How far back to read logs and event logs.",
                "Since install reads everything since Windows was installed. ESP and Win32 app states are always read, with the time they were saved.",
                "hours days since install", "Hours", "24"),
            D("wmi", ThisDevice, "ConfigMgr client WMI", "Read application, package and update states from root\\ccm\\ClientSDK.",
                "Turn it off on a device where WMI is slow.", "sccm configmgr", "UseWmi", "1"),
            D("events", ThisDevice, "Event logs", "Read the Autopilot, MDM, device registration and Entra ID event logs.",
                "The Autopilot event log is read in full; the others for errors and warnings.", "event viewer", "UseEvents", "1"),
            D("interval", ThisDevice, "Read every", "How often TrackVice reads the device while watching.",
                "Log files and registry keys are read every time, WMI every third time.", "poll interval seconds how often look again", "PollMs", "2000"),
            D("stuck", ThisDevice, "Warn when nothing changes for", "How long a download or install may be silent before it gets a No progress finding.",
                null, "stuck no progress minutes", "StuckMinutes", "15"),
            D("admin", ThisDevice, "Administrator rights", "The ConfigMgr and Intune logs, the ESP keys and some event logs need administrator rights.",
                null, "elevated restart"),

            // Customers
            D("customers", Customers, "Customers", "Saved connections for each customer: the Intune tenant, where its agents upload to and its SCCM site.",
                "Add, edit, remove, import and export them in the list. Choose one at the top of a device list to connect to it.",
                "import export json list tenant connection"),
            D("customercode", Customers, "Customer code", "The code that installs the agent for the customer in use.",
                "It holds the link that uploads to the customer's container, so keep it like a password. From it you can also update the platform script in Intune.",
                "agent install platform script"),

            // Intune / cloud
            D("signin", Intune, "Intune sign-in", "The sign-in kept for the customer in use. Sign out deletes it and the kept names.",
                null, "autopilot sign out graph account"),
            D("clientid", Intune, "Client ID", "The app registration TrackVice signs in with. Empty uses Microsoft Graph Command Line Tools.",
                "Your own app needs the delegated permission DeviceManagementManagedDevices.Read.All and public client flows turned on. The client ID and tenant come from the customer in use when you switch customers.",
                "app registration graph"),
            D("tenant", Intune, "Tenant", "The tenant ID or domain, such as contoso.onmicrosoft.com. Empty uses the tenant of the account you sign in with.",
                null, "domain tenant id"),
            D("names.scripts", Intune, "Script names", "Show platform scripts by their name. Needs DeviceManagementScripts.Read.All.",
                null, "names from intune", "IntuneNames.Scripts", "1"),
            D("names.apps", Intune, "App names", "Show Intune apps the device only knows by ID by their name. Needs DeviceManagementApps.Read.All.",
                null, "names from intune", "IntuneNames.Apps", "0"),
            D("names.remediations", Intune, "Remediation names", "Show remediations by their name. Needs DeviceManagementScripts.Read.All.",
                null, "names from intune", "IntuneNames.Remediations", "0"),
            D("names.profiles", Intune, "Configuration profiles", "Read every configuration profile Intune has for the device, with its state. Needs DeviceManagementConfiguration.Read.All.",
                null, "names from intune policy", "IntuneNames.Profiles", "0"),

            // SCCM / on-premises
            D("smsprovider", Sccm, "SMS Provider", "The server that runs the SMS Provider, usually the site server.",
                "It is saved with the customer in use, so a customer can have Intune and SCCM. TrackVice uses your Windows account, as the console does.",
                "server site configmgr", "SccmServer", null),
            D("sitecode", Sccm, "Site code", "Optional. TrackVice finds it when it connects.", null, "site configmgr", "SccmSite", null),
            D("earlierruns", Sccm, "Show earlier runs", "Task sequences in SCCM lists every run of a device and deployment, not only the newest.",
                null, "task sequence runs history", "SccmEarlierRuns", "0"),

            // Your storage and agents
            Row(D("yourstorage", Storage, "Your storage", "The storage account in your Azure that the agents of all customers upload to.", null, "azure storage account container set up"), "Your storage"),
            Row(D("publish", Storage, "Publish TrackVice to the devices", "Publish this TrackVice.exe to your storage. New devices get it, and installed agents update to it.", null, "publish version agent"), "TrackVice for the devices"),
            Row(D("security", Storage, "Check security", "Check the settings of your storage account against what TrackVice needs.", null, "security check"), "Security"),
            Row(D("retention", Storage, "Delete uploads after", "How long uploads stay in your storage.", null, "days retention lifecycle"), "Delete uploads after"),
            Row(D("accountkey", Storage, "Keep the account key", "Keep the account key of your storage on this PC, or sign in to Azure each time you add a customer.", null, "account key sign in azure"), "Ask for Azure sign-in when adding a customer instead of keeping the account key on this PC"),
            Row(D("removestorage", Storage, "Remove Your storage", "Forget the account key and the links on this PC. Nothing in Azure is deleted.", null, "forget remove"), "Remove"),
            D("agentupdate", Storage, "Publish agent update", "Upload a TrackVice.exe with a signed agent/latest.json for setups with links.",
                "Agents installed with $UpdateKey and $UpdateLink update themselves to it, so the platform script stays the same. The signing key is kept in %LOCALAPPDATA%\\TrackVice\\agent-update-key.bin, encrypted for your Windows account.",
                "update key signing key links"),

            // LogVice
            D("logvice", LogVice, "LogVice", "Where LogVice.exe is. TrackVice opens logs in it.",
                "TrackVice looks for the LogVice MSI install, App Paths and LogVice.exe next to TrackVice.exe. Download gets the latest LogVice from GitHub after it asks; Browse chooses another one.",
                "log viewer path exe", "LogVicePath", ""),

            // Data and privacy
            D("settingsstore", Data, "Where settings are stored", "Settings are kept for your Windows account in the registry.", null, "registry hkcu"),
            D("files", Data, "Files on this PC", "Names from Intune, logs from devices, references and the agent update key are kept under %LOCALAPPDATA%\\TrackVice.",
                null, "folder localappdata cache"),
            D("sends", Data, "What TrackVice sends", "Only what a feature you use needs, to the place it needs.",
                "GitHub: which version is the latest, once a day. Microsoft Graph: what you read from Intune, after you sign in. Azure Storage: the uploads of the agents, to your storage or the customer's. A Teams workflow or other webhook: alerts, when you give one. Nothing is sent to the author of TrackVice.",
                "privacy network github graph azure webhook telemetry"),
            D("signins", Data, "Sign-ins", "The Intune sign-in is kept for each customer, encrypted for your Windows account.", null, "forget sign out token"),
            D("keptdata", Data, "Kept names and logs", "Delete the names read from Intune and the logs downloaded from devices, for every customer.",
                null, "forget delete cache"),
        };

        static SettingPage P(string id, string title, string summary)
        {
            SettingPage p = new SettingPage();
            p.Id = id;
            p.Title = title;
            p.Summary = summary;
            return p;
        }

        // keyDefault holds pairs of a key and its default. A null default means the value has none, so it is not reset.
        static SettingDef D(string id, string page, string title, string summary, string more, string keywords, params string[] keyDefault)
        {
            SettingDef d = new SettingDef();
            d.Id = id;
            d.Page = page;
            d.Title = title;
            d.Summary = summary;
            d.More = more;
            d.Keywords = keywords;
            List<string> keys = new List<string>(), defaults = new List<string>();
            for (int i = 0; i + 1 < keyDefault.Length; i += 2)
            {
                keys.Add(keyDefault[i]);
                if (keyDefault[i + 1] != null) defaults.Add(keyDefault[i + 1]);
            }
            d.Keys = keys.ToArray();
            d.Defaults = defaults.ToArray();
            return d;
        }

        static SettingDef Row(SettingDef d, string row)
        {
            d.Row = row;
            return d;
        }

        public static SettingPage Page(string id)
        {
            foreach (SettingPage p in Pages) if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        public static SettingDef Find(string id)
        {
            foreach (SettingDef d in All) if (d.Id == id) return d;
            return null;
        }

        public static List<SettingDef> On(string page)
        {
            List<SettingDef> l = new List<SettingDef>();
            foreach (SettingDef d in All) if (d.Page == page) l.Add(d);
            return l;
        }

        // The saved subject, or General when none is saved or it is not known any more.
        public static string Remembered(string stored)
        {
            SettingPage p = Page(stored);
            return p != null ? p.Id : General;
        }

        public static bool Resettable(string page)
        {
            foreach (SettingDef d in On(page)) if (d.Resettable) return true;
            return false;
        }

        public static List<SettingDef> ChangedOn(string page, ISettingStore s)
        {
            List<SettingDef> l = new List<SettingDef>();
            foreach (SettingDef d in On(page)) if (d.Changed(s)) l.Add(d);
            return l;
        }

        // Puts back the defaults of one subject. Returns how many saved values were removed.
        public static int Reset(string page, ISettingStore s)
        {
            int n = 0;
            foreach (SettingDef d in On(page)) n += d.Reset(s);
            return n;
        }

        // Settings whose name, description or subject has every word of the text. Names that match come first.
        public static List<SettingDef> Search(string text)
        {
            List<SettingDef> hits = new List<SettingDef>();
            string q = (text ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) return hits;
            string[] words = q.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<int> ranks = new List<int>();
            foreach (SettingDef d in All)
            {
                string title = d.Title.ToLowerInvariant();
                SettingPage p = Page(d.Page);
                string all = title + " " + (d.Summary ?? "") + " " + (d.More ?? "") + " " + (d.Keywords ?? "") + " " + (p != null ? p.Title : "");
                all = all.ToLowerInvariant();
                bool every = true, inTitle = true;
                foreach (string w in words)
                {
                    if (all.IndexOf(w, StringComparison.Ordinal) < 0) every = false;
                    if (title.IndexOf(w, StringComparison.Ordinal) < 0) inTitle = false;
                }
                if (!every) continue;
                int rank = title == q ? 0 : title.StartsWith(q, StringComparison.Ordinal) ? 1 : inTitle ? 2 : 3;
                int at = 0;
                while (at < hits.Count && ranks[at] <= rank) at++;
                hits.Insert(at, d);
                ranks.Insert(at, rank);
            }
            return hits;
        }
    }
}
