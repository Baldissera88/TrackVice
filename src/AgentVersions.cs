namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The agent's version and self-update: what the agent adds to its upload, and how the viewer shows it.
    public static class AgentVersions
    {
        // Kept in the session's information, so older viewers simply list them.
        public const string InstalledKey = "Agent installed", UpdatedKey = "Agent updated", StateKey = "Agent self-update", CheckedKey = "Agent update check";
        // The same values in the upload's metadata, for the device list.
        public const string MetaInstalled = "installed", MetaUpdated = "selfupdated", MetaState = "selfupdate";

        // Why an agent did or did not update itself.
        public const string On = "on", NoKey = "nokey", NoLink = "nolink", BadKey = "key", Link = "link", Signature = "signature", Download = "download", Invalid = "invalid";

        // An installed agent checks every 6 hours; a little more is allowed for the check itself.
        public static readonly TimeSpan Grace = TimeSpan.FromHours(6);
        static readonly TimeSpan Slack = TimeSpan.FromMinutes(20);
        // A device counts as running the agent when it uploaded this recently.
        public static readonly TimeSpan Running = TimeSpan.FromMinutes(30);
        // The Agents tile counts devices that uploaded in this time: an agent stays 48 hours after the deployment.
        public static readonly TimeSpan Recent = TimeSpan.FromDays(2);

        public static bool IsKey(string key)
        {
            return key == InstalledKey || key == UpdatedKey || key == StateKey || key == CheckedKey;
        }

        static string Iso(DateTime t)
        {
            return t.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        public static DateTime? Time(string v)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return null;
        }

        // Written by the agent before each upload. Values that are not known are left out.
        public static void Stamp(Session s, DateTime? installedUtc, DateTime? updatedUtc, string state, DateTime? checkedUtc)
        {
            if (s == null) return;
            if (installedUtc.HasValue) s.SetInfo(InstalledKey, Iso(installedUtc.Value));
            if (updatedUtc.HasValue) s.SetInfo(UpdatedKey, Iso(updatedUtc.Value));
            if (!string.IsNullOrEmpty(state)) s.SetInfo(StateKey, state);
            if (checkedUtc.HasValue) s.SetInfo(CheckedKey, Iso(checkedUtc.Value));
        }

        public static void AddMeta(Session s, Dictionary<string, string> m)
        {
            if (s == null || m == null) return;
            if (s.GetInfo(InstalledKey) != null) m[MetaInstalled] = s.GetInfo(InstalledKey);
            if (s.GetInfo(UpdatedKey) != null) m[MetaUpdated] = s.GetInfo(UpdatedKey);
            if (s.GetInfo(StateKey) != null) m[MetaState] = s.GetInfo(StateKey);
        }

        public static DateTime? Installed(Session s)
        {
            return s == null ? null : Time(s.GetInfo(InstalledKey));
        }

        public static DateTime? Updated(Session s)
        {
            return s == null ? null : Time(s.GetInfo(UpdatedKey));
        }

        public static DateTime? Checked(Session s)
        {
            return s == null ? null : Time(s.GetInfo(CheckedKey));
        }

        public static string State(Session s)
        {
            return s == null ? null : s.GetInfo(StateKey);
        }

        // Why self-update is off on an installed agent that has no AgentSelfUpdate.
        public static string OffState(string key, string link)
        {
            if (string.IsNullOrWhiteSpace(key)) return NoKey;
            if (string.IsNullOrWhiteSpace(link)) return NoLink;
            if (UpdateKeys.Problem(key) != null) return BadKey;
            return Link;
        }

        // The state in a plain sentence, or null when there is nothing to say.
        public static string StateText(string state)
        {
            switch (state)
            {
                case On: return "On. It checks your storage every 6 hours.";
                case NoKey: return "Off: the script has no update key.";
                case NoLink: return "Off: the script has no update link.";
                case BadKey: return "Off: the update key in the script cannot be used.";
                case Link: return "The update link could not be used. It may have expired, or the network blocks it.";
                case Signature: return "The signature check failed: the version was published with another update key.";
                case Download: return "The download did not match its signed size or SHA-256.";
                case Invalid: return "The published update information is not valid.";
                default: return null;
            }
        }

        // The likely reason an agent stays behind, from what it said, in one sentence.
        public static string Reason(string state)
        {
            switch (state)
            {
                case NoKey: return "The script has no update key.";
                case NoLink: return "The script has no update link.";
                case BadKey: return "The update key in the script cannot be used.";
                case Link: return "The update link could not be used; it may have expired.";
                case Signature: return "The signature check failed; it was published with another key.";
                case Download: return "The download did not match its signature.";
                case Invalid: return "The published update information is not valid.";
                case On: return "The agent's last check is more than 6 hours ago.";
                default: return "Likely: no update key or link in the script, the signature check failed, or the link expired.";
            }
        }

        // "Agent 1.5.0" for the device header, or "Agent" when the version is not known.
        public static string Header(string version)
        {
            return string.IsNullOrEmpty(version) ? "Agent" : "Agent " + version;
        }

        // Sorts versions by number: 1.10.0 after 1.9.3. Text that is not a version goes first.
        public static string SortKey(string version)
        {
            if (!SemVer.IsValid(version)) return "";
            StringBuilder b = new StringBuilder();
            foreach (string part in version.Split('-')[0].Split('.'))
            {
                int n;
                int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                b.Append(n.ToString("000000", CultureInfo.InvariantCulture)).Append('.');
            }
            return b.ToString();
        }

        public static bool Older(string version, string published)
        {
            return SemVer.IsValid(version) && SemVer.IsValid(published) && SemVer.Compare(version, published) < 0;
        }

        // The agent version of a device on Overview: from the list, or from its session.
        public static string VersionOf(OverviewDevice o)
        {
            if (o == null || o.Kind != OverviewKinds.Agents) return null;
            if (o.D != null && !string.IsNullOrEmpty(o.D.Version)) return o.D.Version;
            return o.S != null && !string.IsNullOrEmpty(o.S.AppVersion) ? o.S.AppVersion : null;
        }

        // Since when the device has run its present version: the last self-update, or the install.
        public static DateTime? RunsSince(OverviewDevice o)
        {
            DateTime? installed = Installed(o.S);
            DateTime? updated = Updated(o.S);
            DateTime? since = installed ?? (o.D != null ? o.D.StartUtc : null);
            if (updated.HasValue && (!since.HasValue || updated.Value > since.Value)) since = updated;
            return since;
        }

        // An agent older than the published version that runs and has not updated for more than 6 hours.
        public static bool Behind(OverviewDevice o, string published, DateTime nowUtc)
        {
            string v = VersionOf(o);
            if (!Older(v, published)) return false;
            // The agent in a task sequence does not update itself: its package decides the version.
            if (o.Side == DeviceSides.Sccm && Installed(o.S) == null) return false;
            if (o.Silent || nowUtc - o.D.LastUtc > Running) return false;
            DateTime? since = RunsSince(o);
            if (!since.HasValue || nowUtc - since.Value <= Grace) return false;
            // It checked a little while ago and found nothing newer: the version was published since, and the next check gets it.
            DateTime? check = Checked(o.S);
            if (State(o.S) == On && check.HasValue && nowUtc - check.Value <= Grace + Slack) return false;
            return true;
        }

        // "12 on 1.5.0, 3 on 1.4.0", newest first.
        public static string Counts(List<string> versions)
        {
            Dictionary<string, int> n = new Dictionary<string, int>();
            foreach (string v in versions) { int c; n.TryGetValue(v, out c); n[v] = c + 1; }
            List<string> keys = new List<string>(n.Keys);
            keys.Sort(delegate (string a, string b) { return string.CompareOrdinal(SortKey(b), SortKey(a)) != 0 ? string.CompareOrdinal(SortKey(b), SortKey(a)) : string.CompareOrdinal(a, b); });
            List<string> parts = new List<string>();
            foreach (string k in keys) parts.Add(n[k].ToString(CultureInfo.InvariantCulture) + " on " + k);
            return string.Join(", ", parts.ToArray());
        }

        // The Agents tile and, when some agents stay behind, the tile under Needs attention.
        public static List<AttentionTile> Tiles(OverviewData data, DateTime nowUtc, string published)
        {
            return Tiles(data, nowUtc, published, null, null);
        }

        // places: where the agents of each customer check for updates, with what is published there. Without them every
        // agent is compared with published, the version of Your storage. running is the TrackVice on this PC.
        public static List<AttentionTile> Tiles(OverviewData data, DateTime nowUtc, string published, IList<UpdateLocation> places, string running)
        {
            List<AttentionTile> l = new List<AttentionTile>();
            if (data == null) return l;
            bool byPlace = places != null && places.Count > 0;
            List<string> versions = new List<string>();
            AttentionTile agents = new AttentionTile();
            agents.Key = "agents";
            agents.Level = 0;
            agents.Order = 60;
            agents.Title = "Agents";
            agents.ActionText = "Show the devices";
            AttentionTile behind = new AttentionTile();
            behind.Key = "agents-behind";
            behind.Level = 1;
            behind.Order = 35;
            behind.Title = "Agents that do not update";
            behind.ActionText = "Show the devices";
            bool olderRunning = false;
            string behindPublished = published;
            Dictionary<string, int> reasons = new Dictionary<string, int>();
            foreach (OverviewDevice o in data.Devices)
            {
                string v = VersionOf(o);
                if (v == null || nowUtc - o.D.LastUtc > Recent) continue;
                versions.Add(v);
                agents.Devices.Add(o);
                string pub = PublishedFor(o, published, places);
                if (Behind(o, pub, nowUtc))
                {
                    if (behind.Devices.Count == 0) behindPublished = pub;
                    behind.Devices.Add(o);
                    string r = Reason(State(o.S));
                    int c;
                    reasons.TryGetValue(r, out c);
                    reasons[r] = c + 1;
                }
                else if (Older(v, pub) && !o.Silent && nowUtc - o.D.LastUtc <= Running) olderRunning = true;
            }
            if (versions.Count == 0) return l;
            agents.Count = agents.Devices.Count;
            agents.Text = byPlace
                ? PlacesText(agents.Devices, places, running, olderRunning)
                : Counts(versions) + (olderRunning ? " (they update within 6 hours)" : "") + "." + (SemVer.IsValid(published) ? " Your storage has " + published + "." : "");
            l.Add(agents);
            if (behind.Devices.Count > 0)
            {
                behind.Devices.Sort(delegate (OverviewDevice a, OverviewDevice b) { return b.D.LastUtc.CompareTo(a.D.LastUtc); });
                behind.Count = behind.Devices.Count;
                string top = null;
                int most = 0;
                foreach (KeyValuePair<string, int> kv in reasons) if (kv.Value > most) { most = kv.Value; top = kv.Key; }
                string names = behind.Devices[0].D.Device + (behind.Count > 1 ? " and " + (behind.Count - 1) + " more" : "");
                behind.Text = (behind.Count == 1 ? "1 device runs" : behind.Count + " devices run") + " an agent older than " + (behindPublished ?? published)
                    + " and has not updated for more than 6 hours: " + names + ". " + top;
                l.Add(behind);
            }
            return l;
        }

        // The version the agent of a device should get: what its own location has, or Your storage's when it has no location.
        static string PublishedFor(OverviewDevice o, string published, IList<UpdateLocation> places)
        {
            if (places == null || places.Count == 0) return published;
            UpdateLocation p = UpdateLocations.For(places, o.Customer);
            return p == null ? published : p.PublishedVersion;
        }

        // "Contoso: 5 agents on 1.11.11; contoso/trackvice-agent has nothing published (publish here)." for each location.
        static string PlacesText(List<OverviewDevice> devices, IList<UpdateLocation> places, string running, bool olderRunning)
        {
            List<UpdateLocation> order = new List<UpdateLocation>();
            Dictionary<UpdateLocation, List<OverviewDevice>> groups = new Dictionary<UpdateLocation, List<OverviewDevice>>();
            List<OverviewDevice> loose = new List<OverviewDevice>();
            foreach (OverviewDevice o in devices)
            {
                UpdateLocation p = UpdateLocations.For(places, o.Customer);
                if (p == null) { loose.Add(o); continue; }
                if (!groups.ContainsKey(p)) { groups[p] = new List<OverviewDevice>(); order.Add(p); }
                groups[p].Add(o);
            }
            // Every agent is on what its location has: say so once.
            bool current = loose.Count == 0;
            string only = null;
            foreach (UpdateLocation p in order)
            {
                foreach (OverviewDevice o in groups[p])
                {
                    string v = VersionOf(o);
                    if (p.PublishedVersion == null || v != p.PublishedVersion || p.Behind(running)) current = false;
                    if (only == null) only = v;
                    else if (only != v) only = "";
                }
            }
            if (current && !string.IsNullOrEmpty(only))
                return "All " + devices.Count + (devices.Count == 1 ? " agent is" : " agents are") + " on " + only + ", the version published.";
            List<string> parts = new List<string>();
            foreach (UpdateLocation p in order)
            {
                List<string> vs = new List<string>();
                List<string> names = new List<string>();
                foreach (OverviewDevice o in groups[p])
                {
                    vs.Add(VersionOf(o));
                    if (!string.IsNullOrEmpty(o.Customer) && !names.Contains(o.Customer)) names.Add(o.Customer);
                }
                string who = Names(names.Count > 0 ? names : p.Customers);
                string flag = "";
                if (p.Behind(running)) flag = p.CanWrite ? " (publish here)" : " (behind, this PC needs a write link)";
                parts.Add((who.Length > 0 ? who + ": " : "") + Phrase(vs) + "; " + p.Short + " " + p.HasText + flag + ".");
            }
            if (loose.Count > 0)
            {
                List<string> vs = new List<string>();
                foreach (OverviewDevice o in loose) vs.Add(VersionOf(o));
                parts.Add(Phrase(vs) + ".");
            }
            if (olderRunning) parts.Add("Older agents update within 6 hours.");
            return string.Join(" ", parts.ToArray());
        }

        static string Phrase(List<string> versions)
        {
            bool one = true;
            foreach (string v in versions) if (v != versions[0]) one = false;
            if (!one) return Counts(versions);
            return versions.Count + (versions.Count == 1 ? " agent" : " agents") + " on " + versions[0];
        }

        static string Names(List<string> names)
        {
            if (names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            if (names.Count == 2) return names[0] + " and " + names[1];
            return names[0] + ", " + names[1] + " and " + (names.Count - 2) + " more";
        }

        // The agent's rows in the Deployment table of the HTML report, for a session the agent uploaded.
        public static List<string[]> ReportRows(Session s)
        {
            List<string[]> l = new List<string[]>();
            if (s == null || s.Mode != Modes.Agent) return l;
            if (!string.IsNullOrEmpty(s.AppVersion)) l.Add(new string[] { "Agent version", "TrackVice " + s.AppVersion });
            DateTime? installed = Installed(s), updated = Updated(s);
            if (installed.HasValue) l.Add(new string[] { "Agent installed", Txt.Local(installed.Value) });
            if (updated.HasValue) l.Add(new string[] { "Agent last self-update", Txt.Local(updated.Value) });
            string st = StateText(State(s));
            if (st != null) l.Add(new string[] { "Agent self-update", st });
            return l;
        }

        // Published version for Overview, set by the window. Null when Your storage is not set up.
        public static Func<string> Published;

        // The update locations for Overview, from what was read last. Null when nothing is set up. Never reads the network.
        public static Func<List<UpdateLocation>> Places;
        // The version of TrackVice on this PC, to tell which location is behind.
        public static Func<string> RunningVersion;

        public static List<UpdateLocation> PlacesNow()
        {
            try
            {
                Func<List<UpdateLocation>> f = Places;
                return f == null ? null : f();
            }
            catch (Exception) { return null; }
        }

        public static string RunningNow()
        {
            try
            {
                Func<string> f = RunningVersion;
                return f == null ? null : f();
            }
            catch (Exception) { return null; }
        }

        public static string PublishedNow()
        {
            try
            {
                Func<string> f = Published;
                return f == null ? null : f();
            }
            catch (Exception) { return null; }
        }

        // Made-up agent facts for the demo devices: some run an older version, one of them without an update key.
        public static void Demo(Session s, string computer, DateTime nowUtc)
        {
            if (s == null) return;
            string c = (computer ?? "").ToUpperInvariant();
            if (c.EndsWith("LT0398"))
            {
                s.AppVersion = "1.4.0";
                Stamp(s, nowUtc.AddHours(-9), null, NoKey, null);
            }
            else if (c.EndsWith("LT0362"))
            {
                s.AppVersion = "1.4.0";
                Stamp(s, nowUtc.AddHours(-2), null, On, nowUtc.AddHours(-2));
            }
            // An agent in a task sequence is not installed and does not update itself.
            else if (c.EndsWith("PC204")) s.AppVersion = "1.4.0";
            else if (s.Kind == null || s.Kind.IndexOf("task sequence", StringComparison.OrdinalIgnoreCase) < 0) Stamp(s, nowUtc.AddHours(-30), nowUtc.AddHours(-3), On, nowUtc.AddHours(-3));
        }
    }
}
