namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;

    // What was found when the update information of a location was read.
    public enum UpdateState
    {
        // Not read yet.
        NotRead,
        // The location is there and agent/latest.json is not.
        Nothing,
        // agent/latest.json is there: see Version.
        Published,
        // This PC has no link that can read the location, so what is published there is not known.
        NoRead,
        // The read failed: see Problem.
        Failed
    }

    // What the customer book and the saved secrets know about one customer, as far as updates go.
    public sealed class UpdateCustomer
    {
        public string Name = "";
        // The place the agents upload to and the list reads from: a viewer link or a folder.
        public string AgentLocation = "";
        // The customer code, whose agent link is where the agents of the script check for updates. Empty when there is none.
        public string Code = "";
        // The link with Create and Write that was saved for Publish agent update, or a folder.
        public string PublishLink = "";

        public UpdateCustomer() { }

        public UpdateCustomer(string name, string agentLocation, string code, string publishLink)
        {
            Name = name ?? "";
            AgentLocation = agentLocation ?? "";
            Code = code ?? "";
            PublishLink = publishLink ?? "";
        }
    }

    // A place where agents check for updates, and what this PC can do there. Several customers can share one.
    public sealed class UpdateLocation
    {
        // Lower case identity: the container address without the token, or the folder path. Two customers with the same key share a location.
        public string Key = "";
        public string Account = "", Container = "", Folder = "";
        public bool IsYourStorage;
        // True when it is known that the agents check here: Your storage, a customer code, a saved publish link or a share.
        // False when it is only taken from the viewer link of the customer.
        public bool Confirmed;
        public readonly List<string> Customers = new List<string>();
        // The customers that have a customer code: their script downloads the newest signed version for new devices.
        public readonly List<string> WithCode = new List<string>();
        // A link or folder this PC reads with, and one it writes with. Empty when there is none.
        public string ReadLink = "", WriteLink = "";
        // One sentence on what is missing to publish here. Empty when this PC can.
        public string Missing = "";
        // What was read last.
        public UpdateState State = UpdateState.NotRead;
        public string Version = "", Problem = "";
        // agent/latest.json there is signed with another update key than the one on this PC.
        public bool OtherKey;

        public bool IsFolder
        {
            get { return Folder.Length > 0; }
        }

        public bool CanWrite
        {
            get { return WriteLink.Length > 0; }
        }

        public bool CanRead
        {
            get { return ReadLink.Length > 0; }
        }

        // "Your storage", "account/container" or the folder.
        public string Short
        {
            get { return IsYourStorage ? "Your storage" : IsFolder ? Folder : Account + "/" + Container; }
        }

        // The same with the account of Your storage.
        public string Long
        {
            get { return IsYourStorage ? "Your storage (" + Account + "/" + Container + ")" : Short; }
        }

        // Some customer of the location has no customer code, so its script has a fixed TrackVice.exe with its SHA-256.
        public bool HasFixedScript
        {
            get { return Customers.Count > WithCode.Count; }
        }

        public bool Has(string customer)
        {
            foreach (string c in Customers) if (string.Equals(c, customer, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The version published there, or null when nothing is or it is not known.
        public string PublishedVersion
        {
            get { return State == UpdateState.Published && SemVer.IsValid(Version) ? Version : null; }
        }

        // Nothing is published, or something older than running.
        public bool Behind(string running)
        {
            if (!SemVer.IsValid(running)) return false;
            if (State == UpdateState.Nothing) return true;
            return PublishedVersion != null && SemVer.Compare(PublishedVersion, running) < 0;
        }

        // What the Publish dialog ticks first: this PC can write there, it is behind, and nothing says it is the wrong place or the wrong key.
        public bool Wanted(string running)
        {
            if (!CanWrite || OtherKey) return false;
            if (State == UpdateState.Nothing) return Confirmed && Behind(running);
            if (State == UpdateState.Published) return Behind(running);
            return false;
        }

        // "has 2.0.3", "has nothing published" or "is not known".
        public string HasText
        {
            get
            {
                if (State == UpdateState.Published) return "has " + Version;
                if (State == UpdateState.Nothing) return "has nothing published";
                return "has an unknown version";
            }
        }

        // The version in a sentence for a row: "2.0.3", "nothing published", or why it is not known.
        public string VersionText
        {
            get
            {
                switch (State)
                {
                    case UpdateState.Published: return Version + (OtherKey ? ", signed with another update key" : "");
                    case UpdateState.Nothing: return "nothing published";
                    case UpdateState.NoRead: return "not known: this PC has no link that can read there";
                    case UpdateState.Failed: return "not read: " + Problem;
                    default: return "not read yet";
                }
            }
        }
    }

    public enum PublishOutcome { Published, Skipped, Failed }

    // The result of publishing to one location.
    public sealed class PublishLine
    {
        public UpdateLocation Place;
        public PublishOutcome Outcome;
        public string Text = "";

        public string Full
        {
            get { return Place.Short + ": " + Text; }
        }
    }

    // Reads a file that may be missing: null when it is not there.
    public interface IFindStore
    {
        byte[] Find(string name);
    }

    // Where the agents of each customer check for updates, what is published there, and whether this PC can publish there.
    public static class UpdateLocations
    {
        public const string OtherKeyMarker = "is signed with another update key than the one on this PC";
        static readonly TimeSpan SignedFor = TimeSpan.FromHours(12);

        // ---------- the locations ----------

        sealed class Spot
        {
            public string Key = "", Account = "", Container = "", Folder = "";
            public string Read = "", Write = "";
            public bool Confirmed;
        }

        // own is Your storage, or null. nowUtc is for the links that are signed here from the account key.
        public static List<UpdateLocation> Derive(IList<UpdateCustomer> customers, OwnStorage own, DateTime nowUtc)
        {
            List<UpdateLocation> list = new List<UpdateLocation>();
            if (own != null && own.Account.Length > 0)
            {
                string endpoint = string.IsNullOrEmpty(own.BlobEndpoint) ? "https://" + own.Account + ".blob.core.windows.net/" : own.BlobEndpoint;
                UpdateLocation y = new UpdateLocation();
                y.IsYourStorage = true;
                y.Confirmed = true;
                y.Account = own.Account;
                y.Container = Arm.AgentFiles;
                y.Key = ContainerKey(endpoint.TrimEnd('/') + "/" + Arm.AgentFiles);
                y.ReadLink = own.AgentRead ?? "";
                y.WriteLink = own.AgentWrite ?? "";
                list.Add(y);
            }
            if (customers != null)
            {
                foreach (UpdateCustomer c in customers)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Name)) continue;
                    bool hasCode;
                    Spot s = Of(c, out hasCode);
                    if (s == null) continue;
                    UpdateLocation l = null;
                    foreach (UpdateLocation k in list) if (k.Key == s.Key) { l = k; break; }
                    if (l == null)
                    {
                        l = new UpdateLocation();
                        l.Key = s.Key;
                        l.Account = s.Account;
                        l.Container = s.Container;
                        l.Folder = s.Folder;
                        list.Add(l);
                    }
                    if (!l.Has(c.Name)) l.Customers.Add(c.Name);
                    if (hasCode && !l.WithCode.Contains(c.Name)) l.WithCode.Add(c.Name);
                    if (s.Confirmed) l.Confirmed = true;
                    if (l.ReadLink.Length == 0) l.ReadLink = s.Read;
                    if (l.WriteLink.Length == 0) l.WriteLink = s.Write;
                }
            }
            foreach (UpdateLocation l in list) Finish(l, own, nowUtc);
            return list;
        }

        static Spot Of(UpdateCustomer c, out bool hasCode)
        {
            hasCode = false;
            CustomerCode code = null;
            if (!Customer.Blank(c.Code))
            {
                try { code = CustomerCode.Parse(c.Code); }
                catch (Exception) { code = null; }
            }
            string agent = code != null && Stores.IsUrl(code.Agent) ? code.Agent.Trim() : "";
            string pub = Clean(c.PublishLink);
            Spot s = null;
            if (agent.Length > 0)
            {
                s = ContainerSpot(agent, true);
                if (s != null)
                {
                    hasCode = true;
                    s.Read = agent;
                    // The saved publish link counts only when it is for the same container.
                    Spot p = pub.Length > 0 && Stores.IsUrl(pub) ? ContainerSpot(pub, true) : null;
                    if (p != null && p.Key == s.Key) { s.Write = p.Write; if (s.Read.Length == 0) s.Read = p.Read; }
                }
            }
            if (s == null && pub.Length > 0)
            {
                if (Stores.IsUrl(pub))
                {
                    s = ContainerSpot(pub, true);
                    if (s != null) s.Read = Has(pub, 'r') ? pub : "";
                }
                else if (ShareStorage.KindOf(pub) != SharePathKind.Invalid) s = FolderSpot(pub, true);
            }
            if (s == null)
            {
                string at = Clean(c.AgentLocation);
                if (Stores.IsUrl(at))
                {
                    // The agent files are by convention in trackvice-agent of the account the viewer link is for.
                    Spot v = ContainerSpot(at, false);
                    if (v != null)
                    {
                        s = new Spot();
                        s.Account = v.Account;
                        s.Container = Arm.AgentFiles;
                        s.Key = ContainerKey(BaseOf(at, v.Account) + "/" + Arm.AgentFiles);
                    }
                }
                else if (at.Length > 0 && ShareStorage.KindOf(at) != SharePathKind.Invalid) s = FolderSpot(at, false);
            }
            return s;
        }

        static string Clean(string s)
        {
            return (s ?? "").Trim().Trim('"').Trim();
        }

        static string BaseOf(string link, string account)
        {
            Uri u;
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out u)) return "https://" + account + ".blob.core.windows.net";
            return u.Scheme + "://" + u.Authority;
        }

        static Spot ContainerSpot(string link, bool confirmed)
        {
            Uri u;
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out u)) return null;
            string[] parts = u.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 0 || parts[0].Length == 0) return null;
            Spot s = new Spot();
            s.Account = PublishTarget.AccountOf(link);
            s.Container = parts[0];
            s.Key = ContainerKey(u.Scheme + "://" + u.Authority + "/" + parts[0]);
            s.Confirmed = confirmed;
            s.Write = Has(link, 'w') ? link.Trim() : "";
            s.Read = Has(link, 'r') ? link.Trim() : "";
            return s;
        }

        static Spot FolderSpot(string path, bool confirmed)
        {
            Spot s = new Spot();
            s.Folder = ShareStorage.Normalize(path);
            s.Key = "folder:" + s.Folder.ToLowerInvariant();
            s.Confirmed = confirmed;
            s.Read = s.Write = s.Folder;
            return s;
        }

        static string ContainerKey(string containerUrl)
        {
            return containerUrl.Trim().TrimEnd('/').ToLowerInvariant();
        }

        // What a link with a token may do: the sp= letters. A link without sp= (a stored access policy) is taken to allow it.
        static bool Has(string link, char perm)
        {
            string sp = Query(link, "sp");
            return sp == null || sp.IndexOf(perm) >= 0;
        }

        static string Query(string link, string name)
        {
            int q = (link ?? "").IndexOf('?');
            if (q < 0) return null;
            foreach (string part in link.Substring(q + 1).Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && string.Equals(part.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        static void Finish(UpdateLocation l, OwnStorage own, DateTime nowUtc)
        {
            if (l.IsFolder)
            {
                l.ReadLink = l.WriteLink = l.Folder;
                return;
            }
            // The account of Your storage: with its key this PC signs a link for any container of it.
            if (own != null && own.KeyKept && string.Equals(own.Account, l.Account, StringComparison.OrdinalIgnoreCase) && (l.ReadLink.Length == 0 || l.WriteLink.Length == 0))
            {
                string endpoint = string.IsNullOrEmpty(own.BlobEndpoint) ? "https://" + own.Account + ".blob.core.windows.net/" : own.BlobEndpoint;
                try
                {
                    if (l.WriteLink.Length == 0) l.WriteLink = SasLinks.Container(endpoint, own.Account, l.Container, "cw", nowUtc.AddMinutes(-15), nowUtc.Add(SignedFor), own.Key);
                    if (l.ReadLink.Length == 0) l.ReadLink = SasLinks.Container(endpoint, own.Account, l.Container, "r", nowUtc.AddMinutes(-15), nowUtc.Add(SignedFor), own.Key);
                }
                catch (Exception) { }
            }
            if (l.WriteLink.Length == 0) l.Missing = MissingText(l, own);
        }

        // What is missing to publish to a container, and how to get it.
        public static string MissingText(UpdateLocation l, OwnStorage own)
        {
            string portal = "In the Azure portal, open " + l.Account + " > Containers > " + l.Container + " > Shared access tokens, tick Read, Create and Write, and paste the link here.";
            if (own != null && string.Equals(own.Account, l.Account, StringComparison.OrdinalIgnoreCase))
                return "Your storage does not keep its account key on this PC, so TrackVice cannot make a link for " + l.Container + ". Publish from Settings > Your storage (it signs in to Azure), or paste a link. " + portal;
            return "This PC has no link with Create and Write for " + l.Container + " in " + l.Account + ". " + portal
                + " Or sign in to Azure with Storage Account Contributor on " + l.Account + " and set it up as Your storage: TrackVice then gets the key itself.";
        }

        // Null when link can be saved as the link to publish to l with, otherwise what is wrong with it.
        public static string WriteLinkProblem(UpdateLocation l, string link, DateTime nowUtc)
        {
            string s = Clean(link);
            if (s.Length == 0) return "Paste the link.";
            if (l.IsFolder)
            {
                if (Stores.IsUrl(s)) return "This location is a folder. Use a folder, not a link.";
                return ShareStorage.Normalize(s).ToLowerInvariant() == l.Folder.ToLowerInvariant() ? null : "That is not the folder " + l.Folder + ".";
            }
            if (!Stores.IsUrl(s)) return "Paste a link that starts with https://.";
            if (s.IndexOf('?') < 0) return "The link has no access token. Make it under Shared access tokens in the portal.";
            Spot p = ContainerSpot(s, true);
            if (p == null || !string.Equals(p.Account, l.Account, StringComparison.OrdinalIgnoreCase) || !string.Equals(p.Container, l.Container, StringComparison.OrdinalIgnoreCase))
                return "That link is for " + (p == null ? "no container" : p.Account + "/" + p.Container) + ", not for " + l.Account + "/" + l.Container + ".";
            string sp = Query(s, "sp");
            if (sp != null && (sp.IndexOf('c') < 0 || sp.IndexOf('w') < 0)) return "The link allows \"" + sp + "\". It needs Create and Write.";
            string se = Query(s, "se");
            DateTime end;
            if (se != null && DateTime.TryParse(se, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out end) && end <= nowUtc)
                return "The link expired on " + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".";
            return null;
        }

        // A location for a link or folder typed by hand, or null when it is neither. Not tied to a customer.
        public static UpdateLocation FromLink(string where)
        {
            string s = Clean(where);
            Spot sp = null;
            if (Stores.IsUrl(s)) sp = ContainerSpot(s, true);
            else if (s.Length > 0 && ShareStorage.KindOf(s) != SharePathKind.Invalid) sp = FolderSpot(s, true);
            if (sp == null) return null;
            UpdateLocation l = new UpdateLocation();
            l.Key = sp.Key;
            l.Account = sp.Account;
            l.Container = sp.Container;
            l.Folder = sp.Folder;
            l.Confirmed = true;
            l.ReadLink = sp.Read;
            l.WriteLink = sp.Write;
            if (!l.IsFolder && s.IndexOf('?') < 0) { l.ReadLink = l.WriteLink = ""; l.Missing = "The link has no access token. Make it under Shared access tokens in the portal, with Read, Create and Write."; }
            else if (l.WriteLink.Length == 0) l.Missing = "The link does not allow Create and Write. Make one under Shared access tokens in the portal, with Read, Create and Write.";
            return l;
        }

        // Saves a checked link in l, so it can be used at once, and in the read side when it can read too.
        public static void UseWriteLink(UpdateLocation l, string link)
        {
            string s = Clean(link);
            l.WriteLink = s;
            if (!l.IsFolder && Has(s, 'r')) l.ReadLink = s;
            l.Missing = "";
            l.Confirmed = true;
        }

        // ---------- what is published ----------

        public static Func<string, ISessionStore> OpenWith(IWeb web)
        {
            return delegate (string where)
            {
                if (Stores.IsUrl(where)) return new WebContainer(web, where);
                return new FolderStore(where);
            };
        }

        static byte[] Find(ISessionStore s, string name)
        {
            IFindStore f = s as IFindStore;
            if (f != null) return f.Find(name);
            return s.Get(name);
        }

        // Reads agent/latest.json of the location and checks it against the update key of this PC. Never throws.
        public static void Refresh(UpdateLocation l, Func<string, ISessionStore> open, string publicKey)
        {
            l.State = UpdateState.NotRead;
            l.Version = "";
            l.Problem = "";
            l.OtherKey = false;
            if (!l.CanRead) { l.State = UpdateState.NoRead; return; }
            try
            {
                byte[] b = Find(open(l.ReadLink), UpdateManifest.Name);
                if (b == null) { l.State = UpdateState.Nothing; return; }
                UpdateManifest m = UpdateManifest.Parse(b);
                l.Version = m.Version;
                l.State = UpdateState.Published;
                if (!string.IsNullOrEmpty(publicKey))
                {
                    bool signed;
                    try { signed = m.Verify(publicKey); }
                    catch (Exception) { signed = false; }
                    l.OtherKey = !signed;
                }
            }
            catch (Exception ex)
            {
                l.State = UpdateState.Failed;
                l.Problem = ex.Message;
            }
        }

        public static string OtherKeyText(UpdateLocation l)
        {
            return UpdateManifest.Name + " in " + l.Short + " " + OtherKeyMarker + ", so devices would refuse what this PC publishes. Publish from the PC that has that key.";
        }

        // ---------- publishing ----------

        // Publishes exe to every place, one after the other. A place that cannot be published to, has another key or fails
        // does not stop the others. Each place gets its own line.
        public static List<PublishLine> PublishTo(IList<UpdateLocation> places, byte[] exe, string version, RSACryptoServiceProvider key, DateTime nowUtc, Func<string, ISessionStore> open)
        {
            List<PublishLine> lines = new List<PublishLine>();
            string publicKey = UpdateKeys.PublicKey(key);
            List<string> keys = new List<string>();
            foreach (UpdateLocation l in places)
            {
                PublishLine line = new PublishLine();
                line.Place = l;
                lines.Add(line);
                try
                {
                    if (keys.Contains(l.Key)) { Skip(line, "it is the same place as one above."); continue; }
                    keys.Add(l.Key);
                    if (!l.CanWrite) { Skip(line, l.Missing.Length > 0 ? l.Missing : "this PC cannot write there."); continue; }
                    Refresh(l, open, publicKey);
                    if (l.State == UpdateState.Failed) { Skip(line, "could not read " + UpdateManifest.Name + " to check its update key: " + l.Problem); continue; }
                    if (l.OtherKey) { Skip(line, OtherKeyText(l)); continue; }
                    if (l.State == UpdateState.Published && SemVer.IsValid(l.Version))
                    {
                        int c = SemVer.Compare(l.Version, version);
                        if (c == 0) { Skip(line, "it has " + version + " already."); continue; }
                        if (c > 0) { Skip(line, "it has " + l.Version + ", which is newer than " + version + ". Agents never install an older version."); continue; }
                    }
                    UpdatePublisher.Publish(open(l.WriteLink), exe, version, key, nowUtc);
                    l.State = UpdateState.Published;
                    l.Version = version;
                    l.OtherKey = false;
                    line.Outcome = PublishOutcome.Published;
                    line.Text = "published " + version + "." + AfterText(l);
                }
                catch (Exception ex)
                {
                    line.Outcome = PublishOutcome.Failed;
                    line.Text = "failed: " + ex.Message;
                }
            }
            return lines;
        }

        static void Skip(PublishLine line, string why)
        {
            line.Outcome = PublishOutcome.Skipped;
            line.Text = "skipped: " + why;
        }

        // What happens next. Installed agents read the new version within 6 hours. A script with a customer code reads it too
        // for a new device. A script that was made with a fixed TrackVice.exe and its SHA-256 keeps installing that file, which
        // is not replaced here: it would fail the SHA-256 check and no new device would install.
        static string AfterText(UpdateLocation l)
        {
            string s = " Installed agents update within 6 hours.";
            if (l.HasFixedScript)
                s += " " + FixedScriptText(l);
            return s;
        }

        public static string FixedScriptText(UpdateLocation l)
        {
            return "New devices of " + (l.WithCode.Count == 0 ? "its script" : "the customers without a customer code") + " still install the TrackVice.exe their script has (its SHA-256 is fixed in the script, so it is not replaced) and then update themselves. "
                + "To give new devices this version at once, make a customer code and use Customer code... > Update the script in Intune.";
        }

        // The banner after a publish: the places that took the version, and how many did not.
        public static string Summary(IList<PublishLine> lines, string version)
        {
            List<string> done = new List<string>();
            int other = 0;
            foreach (PublishLine l in lines)
            {
                if (l.Outcome == PublishOutcome.Published) done.Add(l.Place.Short);
                else other++;
            }
            if (done.Count == 0)
            {
                if (lines.Count == 1) return "Not published: " + lines[0].Full;
                return "Not published to any of the " + lines.Count + " places. The reasons are below.";
            }
            string s = "Published " + version + " to " + done.Count + (done.Count == 1 ? " place: " : " places: ") + string.Join(", ", done.ToArray()) + ".";
            if (other > 0) s += " Not published to " + other + (other == 1 ? " place" : " places") + ": see below.";
            return s;
        }

        // The same for a publish to Your storage only, as the banner and the status bar do it.
        public static string Done(string version, IList<string> places)
        {
            return "Published " + version + " to " + places.Count + (places.Count == 1 ? " place: " : " places: ") + string.Join(", ", new List<string>(places).ToArray()) + ". Installed agents update within 6 hours.";
        }

        // ---------- remembered between reads ----------

        sealed class Known
        {
            public UpdateState State;
            public string Version = "", Problem = "";
            public bool OtherKey;
        }

        static readonly Dictionary<string, Known> known = new Dictionary<string, Known>();
        static readonly object gate = new object();
        public static DateTime ReadUtc = DateTime.MinValue;

        // Keeps what was read, for Overview, which must not wait for the network.
        public static void Remember(IEnumerable<UpdateLocation> places, DateTime nowUtc)
        {
            lock (gate)
            {
                foreach (UpdateLocation l in places)
                {
                    Known k = new Known();
                    k.State = l.State;
                    k.Version = l.Version;
                    k.Problem = l.Problem;
                    k.OtherKey = l.OtherKey;
                    known[l.Key] = k;
                }
                ReadUtc = nowUtc;
            }
        }

        public static void Apply(IEnumerable<UpdateLocation> places)
        {
            lock (gate)
            {
                foreach (UpdateLocation l in places)
                {
                    Known k;
                    if (!known.TryGetValue(l.Key, out k)) continue;
                    l.State = k.State;
                    l.Version = k.Version;
                    l.Problem = k.Problem;
                    l.OtherKey = k.OtherKey;
                }
            }
        }

        public static void Forget()
        {
            lock (gate)
            {
                known.Clear();
                ReadUtc = DateTime.MinValue;
            }
        }

        // ---------- Overview ----------

        // The location the agents of a customer check, or null. A device with no customer belongs to Your storage.
        public static UpdateLocation For(IList<UpdateLocation> places, string customer)
        {
            if (places == null) return null;
            foreach (UpdateLocation l in places)
                if (!string.IsNullOrEmpty(customer) && l.Has(customer)) return l;
            if (string.IsNullOrEmpty(customer)) foreach (UpdateLocation l in places) if (l.IsYourStorage) return l;
            return null;
        }
    }
}
