namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class SettingsTests
    {
        public static void Run()
        {
            SubjectsInOrder();
            EverySettingOnOnePage();
            SearchFindsSettings();
            ChangedFromDefault();
            ResetLeavesCustomersAlone();
            RemembersTheLastSubject();
        }

        static void SubjectsInOrder()
        {
            List<string> titles = new List<string>();
            foreach (SettingPage p in SettingsCatalog.Pages) titles.Add(p.Title);
            T.Eq("General|This device|Customers|Intune / cloud|SCCM / on-premises|Your storage and agents|LogVice|Updates|Data and privacy", string.Join("|", titles.ToArray()), "the subjects in order");
            foreach (SettingPage p in SettingsCatalog.Pages) T.Check(SettingsCatalog.On(p.Id).Count > 0, "settings on " + p.Title);
        }

        static void EverySettingOnOnePage()
        {
            HashSet<string> ids = new HashSet<string>();
            Dictionary<string, int> keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SettingDef d in SettingsCatalog.All)
            {
                T.Check(ids.Add(d.Id), "one setting per ID: " + d.Id);
                T.Check(SettingsCatalog.Page(d.Page) != null, d.Id + " is on a known page");
                T.Check(!string.IsNullOrEmpty(d.Title) && !string.IsNullOrEmpty(d.Summary), d.Id + " has a name and a description");
                int pages = 0;
                foreach (SettingPage p in SettingsCatalog.Pages) if (SettingsCatalog.On(p.Id).Contains(d)) pages++;
                T.Eq(1, pages, "pages that show " + d.Id);
                foreach (string k in d.Keys)
                {
                    int n;
                    keys.TryGetValue(k, out n);
                    keys[k] = n + 1;
                }
            }
            string[] saved = { "Theme", "WatchOnStart", "UpdateCheck", "Hours", "UseWmi", "UseEvents", "PollMs", "StuckMinutes",
                "IntuneNames.Scripts", "IntuneNames.Apps", "IntuneNames.Remediations", "IntuneNames.Profiles", "Expected.Intune", "Expected.History",
                "SccmServer", "SccmSite", "SccmEarlierRuns", "LogVicePath" };
            foreach (string k in saved)
            {
                int n;
                keys.TryGetValue(k, out n);
                T.Eq(1, n, "settings kept under " + k);
            }
            T.Eq(saved.Length, keys.Count, "no other keys");
            T.Eq(SettingsCatalog.ThisDevice, SettingsCatalog.Find("wmi").Page, "WMI is on This device");
            T.Eq(SettingsCatalog.Updates, SettingsCatalog.Find("updatecheck").Page, "the update check is on Updates");
            T.Eq(SettingsCatalog.Updates, SettingsCatalog.Find("updates").Page, "Updates is on Updates");
            T.Eq(SettingsCatalog.Updates, SettingsCatalog.Find("releasepage").Page, "the release page is on Updates");
            T.Eq(SettingsCatalog.Updates, SettingsCatalog.Find("whatsnew").Page, "What's new is on Updates");
            T.Eq(AppUpdate.ReleasesUrl, SettingsCatalog.Find("releasepage").Summary, "the release page row shows the address");
            T.Check(SettingsCatalog.Resettable(SettingsCatalog.Updates), "Updates can be reset");
            T.Eq(SettingsCatalog.Storage, SettingsCatalog.Find("agentupdate").Page, "agent updates are with Your storage");
            T.Eq(SettingsCatalog.Intune, SettingsCatalog.Find("names.apps").Page, "names from Intune are on Intune / cloud");
            T.Check(SettingsCatalog.Find("customers").Keys.Length == 0, "the customers are not a single value");
        }

        static string First(string q)
        {
            List<SettingDef> l = SettingsCatalog.Search(q);
            return l.Count == 0 ? null : l[0].Id;
        }

        static bool Has(string q, string id)
        {
            foreach (SettingDef d in SettingsCatalog.Search(q)) if (d.Id == id) return true;
            return false;
        }

        static void SearchFindsSettings()
        {
            T.Eq("theme", First("theme"), "a name");
            T.Eq("theme", First("  THEME "), "case and spaces");
            T.Eq("wmi", First("wmi"), "part of a name");
            T.Eq("clientid", First("client id"), "two words");
            T.Eq("logvice", First("logvice"), "LogVice");
            T.Eq("history", First("history"), "History to read");
            T.Check(Has("ClientSDK", "wmi"), "a word of the description");
            T.Check(Has("since windows was installed", "history"), "a phrase of More");
            T.Check(Has("synthwave", "theme"), "a keyword");
            T.Check(Has("import", "customers"), "import and export are with the customers");
            T.Check(Has("security", "security"), "Check security");
            T.Check(Has("signing key", "agentupdate"), "the update key");
            T.Check(Has("sms provider", "smsprovider"), "the SMS Provider");
            T.Eq("tenant", First("tenant"), "a name before descriptions that mention it");
            T.Eq(0, SettingsCatalog.Search("contoso fabrikam spaceship").Count, "nothing for unknown words");
            T.Eq(0, SettingsCatalog.Search("").Count, "nothing for an empty search");
            T.Eq(0, SettingsCatalog.Search(null).Count, "nothing for no search");
            List<SettingDef> l = SettingsCatalog.Search("update");
            T.Check(l.Count > 2 && l[0].Id == "updates", "the setting named Updates first: " + (l.Count > 0 ? l[0].Id : ""));
        }

        static void ChangedFromDefault()
        {
            MemorySettingStore s = new MemorySettingStore();
            foreach (SettingDef d in SettingsCatalog.All) T.Check(!d.Changed(s), "nothing saved, nothing changed: " + d.Id);
            SettingDef hours = SettingsCatalog.Find("history");
            s.Set("Hours", "24");
            T.Check(!hours.Changed(s), "the default saved is not a change");
            s.Set("Hours", "72");
            T.Check(hours.Changed(s), "3 days is a change");
            SettingDef wmi = SettingsCatalog.Find("wmi");
            s.Set("UseWmi", "1");
            T.Check(!wmi.Changed(s), "on is the default");
            s.Set("UseWmi", "true");
            T.Check(!wmi.Changed(s), "an older true is on");
            s.Set("UseWmi", "0");
            T.Check(wmi.Changed(s), "off is a change");
            SettingDef theme = SettingsCatalog.Find("theme");
            s.Set("Theme", "vice");
            T.Check(!theme.Changed(s), "Vice in any case is the default");
            s.Set("Theme", "Light");
            T.Check(theme.Changed(s), "Light is a change");
            s.Set("UpdateCheck", "0");
            T.Check(SettingsCatalog.Find("updatecheck").Changed(s), "updates turned off");
            SettingDef logvice = SettingsCatalog.Find("logvice");
            s.Set("LogVicePath", @"D:\Tools\LogVice.exe");
            T.Check(logvice.Changed(s), "a chosen LogVice");
            s.Set("IntuneNames.Scripts", "0");
            s.Set("IntuneNames.Apps", "1");
            T.Eq(2, SettingsCatalog.ChangedOn(SettingsCatalog.Intune, s).Count, "two names changed");
            s.Set("SccmServer", "cm01.contoso.com");
            s.Set("GraphTenant", "contoso.onmicrosoft.com");
            T.Check(!SettingsCatalog.Find("smsprovider").Changed(s), "the SMS Provider belongs to the customer, no mark");
            T.Eq(0, SettingsCatalog.ChangedOn(SettingsCatalog.Customers, s).Count, "no marks on Customers");
            T.Check(SettingsCatalog.Resettable(SettingsCatalog.General), "General can be reset");
            T.Check(!SettingsCatalog.Resettable(SettingsCatalog.Customers), "Customers cannot be reset");
            T.Check(!SettingsCatalog.Resettable(SettingsCatalog.Storage), "Your storage cannot be reset");
        }

        static void ResetLeavesCustomersAlone()
        {
            MemorySettingStore s = new MemorySettingStore();
            Dictionary<string, string> kept = new Dictionary<string, string>();
            kept["Customers"] = "{ \"Kind\": \"TrackVice customers\", \"Customers\": [ { \"Name\": \"Contoso\" } ] }";
            kept["GraphTenant"] = "contoso.onmicrosoft.com";
            kept["GraphClientId"] = "6b1f3c2a-1111-4c2d-9e8f-0a1b2c3d4e5f";
            kept["GraphToken|contoso"] = "made-up";
            kept["GraphAccount|contoso"] = "megan@contoso.com";
            kept["SccmServer"] = "cm01.contoso.com";
            kept["SccmSite"] = "P01";
            kept["OwnStorage"] = "made-up";
            kept["IntuneNames"] = "1";
            foreach (KeyValuePair<string, string> kv in kept) s.Set(kv.Key, kv.Value);
            s.Set("Theme", "Terminal");
            s.Set("WatchOnStart", "0");
            s.Set("Hours", "0");
            s.Set("PollMs", "5000");
            s.Set("IntuneNames.Profiles", "1");
            s.Set("SccmEarlierRuns", "1");
            s.Set("LogVicePath", @"D:\Tools\LogVice.exe");

            T.Eq(1, SettingsCatalog.Reset(SettingsCatalog.Intune, s), "Intune: only the names");
            T.Eq(null, s.Get("IntuneNames.Profiles"), "profiles back to the default");
            T.Eq(0, SettingsCatalog.Reset(SettingsCatalog.Customers, s), "Customers: nothing");
            T.Eq(0, SettingsCatalog.Reset(SettingsCatalog.Storage, s), "Your storage: nothing");
            T.Eq(0, SettingsCatalog.Reset(SettingsCatalog.Data, s), "Data and privacy: nothing");
            T.Eq("5000", s.Get("PollMs"), "another page is not reset");
            foreach (SettingPage p in SettingsCatalog.Pages) SettingsCatalog.Reset(p.Id, s);
            foreach (SettingDef d in SettingsCatalog.All) T.Check(!d.Changed(s), "reset everything: " + d.Id);
            T.Eq(null, s.Get("Theme"), "theme reset");
            T.Eq(null, s.Get("LogVicePath"), "LogVice reset");
            T.Eq(null, s.Get("SccmEarlierRuns"), "earlier runs reset");
            foreach (KeyValuePair<string, string> kv in kept) T.Eq(kv.Value, s.Get(kv.Key), "kept after Reset: " + kv.Key);
            T.Eq(0, SettingsCatalog.Reset(SettingsCatalog.General, s), "a second reset has nothing to do");
        }

        static void RemembersTheLastSubject()
        {
            T.Eq(SettingsCatalog.General, SettingsCatalog.Remembered(null), "General at first");
            T.Eq(SettingsCatalog.Sccm, SettingsCatalog.Remembered("sccm"), "the saved subject");
            T.Eq(SettingsCatalog.LogVice, SettingsCatalog.Remembered("LogVice"), "any case");
            T.Eq(SettingsCatalog.General, SettingsCatalog.Remembered("packvice"), "a subject that is gone");
        }
    }
}
