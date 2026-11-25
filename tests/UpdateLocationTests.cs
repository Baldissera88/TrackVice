namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;

    public static class UpdateLocationTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
        const string AccountKey = "bWFkZS11cC1rZXktZm9yLXRoZS10ZXN0cw==";
        const string Own = "contosotrackvice";
        const string Mansoft = "trackvicemansoft2026dlb1";

        // A container or folder in memory. A link and the same container with another token reach the same files.
        sealed class Mem : ISessionStore, IFindStore
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public string Name;
            public string FailPut, FailFind;

            public string Title { get { return Name; } }

            public void Put(string name, byte[] data, Dictionary<string, string> meta)
            {
                if (FailPut != null) throw new StoreException(FailPut);
                Files[name] = data;
            }

            public List<StoreEntry> List(string prefix) { return new List<StoreEntry>(); }

            public byte[] Find(string name)
            {
                if (FailFind != null) throw new StoreException(FailFind);
                byte[] b;
                return Files.TryGetValue(name, out b) ? b : null;
            }

            public byte[] Get(string name)
            {
                byte[] b = Find(name);
                if (b == null) throw new StoreException(name + " is not there.");
                return b;
            }
        }

        sealed class World
        {
            public readonly Dictionary<string, Mem> Stores = new Dictionary<string, Mem>();

            public Mem At(string where)
            {
                int q = where.IndexOf('?');
                string k = (q < 0 ? where : where.Substring(0, q)).TrimEnd('/').ToLowerInvariant();
                Mem m;
                if (!Stores.TryGetValue(k, out m)) { m = new Mem(); m.Name = k; Stores[k] = m; }
                return m;
            }

            public Func<string, ISessionStore> Open { get { return delegate (string w) { return At(w); }; } }
        }

        static string Link(string account, string container, string perms)
        {
            return "https://" + account + ".blob.core.windows.net/" + container + "?sv=2022-11-02&sp=" + perms + "&se=2030-01-01T00%3A00%3A00Z&sig=made-up";
        }

        static OwnStorage OwnSt(bool keyKept)
        {
            AzureAccount a = new AzureAccount();
            a.Name = Own;
            a.BlobEndpoint = "https://" + Own + ".blob.core.windows.net/";
            a.Location = "westeurope";
            OwnStorage o = OwnStorage.Make(a, AccountKey, "Contoso", Now);
            if (!keyKept) o.DropKey();
            return o;
        }

        static string Code(string name, string agent)
        {
            CustomerCode c = new CustomerCode();
            c.Name = name;
            c.Upload = Link("x", "trackvice", "cw");
            c.Agent = agent;
            c.Key = "key";
            return c.ToString();
        }

        static UpdateLocation Find(List<UpdateLocation> l, string shortName)
        {
            foreach (UpdateLocation p in l) if (p.Short == shortName) return p;
            return null;
        }

        public static void Run()
        {
            Derive();
            Share();
            WriteLinks();
            Overview();
            using (RSACryptoServiceProvider key = UpdateKeys.Create())
            using (RSACryptoServiceProvider other = UpdateKeys.Create())
            {
                Versions(key, other);
                Publish(key, other);
            }
        }

        // ---------- the locations ----------

        static void Derive()
        {
            List<UpdateCustomer> cs = new List<UpdateCustomer>();
            // The customer of the real case: made with its own storage, only the viewer link of trackvice is kept.
            cs.Add(new UpdateCustomer("Mansoft_2026_DLB1", Link(Mansoft, "trackvice", "rl"), "", ""));
            // Two customers in one container: one with a customer code and a publish link, one with the viewer link only.
            cs.Add(new UpdateCustomer("Contoso", Link("contosoagents", "trackvice", "rl"), Code("Contoso", Link("contosoagents", "trackvice-agent", "r")), Link("contosoagents", "trackvice-agent", "cw")));
            cs.Add(new UpdateCustomer("Woodgrove", Link("contosoagents", "trackvice", "rl"), "", ""));
            // A customer whose agents check Your storage.
            cs.Add(new UpdateCustomer("Northwind", Link(Own, "trackvice-northwind", "rl"), Code("Northwind", Link(Own, "trackvice-agent", "r")), ""));
            // The account of Your storage, another container: signed from the account key.
            cs.Add(new UpdateCustomer("Tailspin", "", Code("Tailspin", Link(Own, "other-agent", "r")), ""));
            cs.Add(new UpdateCustomer("Nothing", "", "", ""));
            List<UpdateLocation> l = UpdateLocations.Derive(cs, OwnSt(true), Now);

            T.Eq(4, l.Count, "locations: Your storage with Northwind, Mansoft, Contoso and Woodgrove, the other container; empty customers have none");
            T.Check(l[0].IsYourStorage && l[0].Short == "Your storage", "Your storage is first");
            T.Check(l[0].Has("Northwind"), "a customer that reads Your storage's container shares its location");
            T.Check(l[0].CanWrite, "Your storage can be written to");

            UpdateLocation m = Find(l, Mansoft + "/trackvice-agent");
            T.Check(m != null && m.Has("Mansoft_2026_DLB1"), "a customer with only a viewer link has the agent container of its account");
            T.Check(m != null && !m.Confirmed, "which is only taken from the viewer link");
            T.Check(m != null && !m.CanWrite && m.Missing.Contains("Create and Write") && m.Missing.Contains("Shared access tokens") && m.Missing.Contains(Mansoft) && m.Missing.Contains("trackvice-agent"), "no write access: the row says what to paste and where it is made");
            T.Check(m != null && m.Missing.Contains("Storage Account Contributor"), "and the sign-in way");
            T.Check(m != null && !m.CanRead && m.HasFixedScript, "no read link, and the script is the fixed one");

            UpdateLocation c = Find(l, "contosoagents/trackvice-agent");
            T.Check(c != null && c.Customers.Count == 2 && c.Has("Contoso") && c.Has("Woodgrove"), "customers in one container share one location");
            T.Check(c != null && c.Confirmed && c.CanWrite && c.CanRead, "a customer code and a publish link make it known and writable");
            T.Check(c != null && c.WithCode.Count == 1 && c.HasFixedScript, "only Contoso has a customer code");

            UpdateLocation o = Find(l, Own + "/other-agent");
            T.Check(o != null && o.CanWrite && o.WriteLink.Contains("sp=cw") && o.CanRead, "another container of the account of Your storage is signed from its key");
            T.Check(o != null && !o.HasFixedScript, "its customer has a code");

            // Same customers without the account key.
            OwnStorage nokey = OwnSt(false);
            List<UpdateLocation> k = UpdateLocations.Derive(cs, nokey, Now);
            T.Check(!k[0].CanWrite && k[0].Missing.Contains("does not keep its account key"), "Your storage without its key and without a publish link cannot write, and says why");
            UpdateLocation o2 = Find(k, Own + "/other-agent");
            T.Check(o2 != null && !o2.CanWrite && o2.Missing.Contains("Shared access tokens"), "nor another container of its account");

            // Your storage that is not set up.
            List<UpdateLocation> none = UpdateLocations.Derive(cs, null, Now);
            T.Eq(4, none.Count, "without Your storage its container is a location of the customer");
            T.Check(!none[0].IsYourStorage, "which is not called Your storage");
        }

        static void Share()
        {
            List<UpdateCustomer> cs = new List<UpdateCustomer>();
            cs.Add(new UpdateCustomer("Fabrikam", @"\\FS01\TrackVice$\Fabrikam\", "", ""));
            cs.Add(new UpdateCustomer("Fabrikam EU", "", "", @"\\fs01\trackvice$\fabrikam"));
            cs.Add(new UpdateCustomer("Bad", "not a path", "", ""));
            List<UpdateLocation> l = UpdateLocations.Derive(cs, null, Now);
            T.Eq(1, l.Count, "a share used by two customers is one location");
            T.Check(l[0].IsFolder && l[0].CanWrite && l[0].CanRead && l[0].Short == @"\\FS01\TrackVice$\Fabrikam", "a share is read and written as a folder");
            T.Check(l[0].Confirmed && l[0].Customers.Count == 2, "the saved publish folder makes it known");
            T.Check(l[0].Missing.Length == 0 && l[0].HasFixedScript, "a share has a fixed script");
            List<UpdateLocation> only = UpdateLocations.Derive(new UpdateCustomer[] { cs[0] }, null, Now);
            T.Check(!only[0].Confirmed, "a folder taken from the upload folder alone is not confirmed");
        }

        static void WriteLinks()
        {
            UpdateLocation m = UpdateLocations.Derive(new UpdateCustomer[] { new UpdateCustomer("Mansoft", Link(Mansoft, "trackvice", "rl"), "", "") }, null, Now)[0];
            T.Check(UpdateLocations.WriteLinkProblem(m, "", Now) != null, "an empty link is refused");
            T.Check(UpdateLocations.WriteLinkProblem(m, "D:\\x", Now) != null, "a folder is not a link");
            T.Check(UpdateLocations.WriteLinkProblem(m, "https://" + Mansoft + ".blob.core.windows.net/trackvice-agent", Now) != null, "a link without a token is refused");
            string wrong = UpdateLocations.WriteLinkProblem(m, Link("other", "trackvice-agent", "rcw"), Now);
            T.Check(wrong != null && wrong.Contains("other") && wrong.Contains(Mansoft), "a link for another account is refused and says which");
            T.Check(UpdateLocations.WriteLinkProblem(m, Link(Mansoft, "trackvice", "rcw"), Now) != null, "a link for another container is refused");
            string rl = UpdateLocations.WriteLinkProblem(m, Link(Mansoft, "trackvice-agent", "rl"), Now);
            T.Check(rl != null && rl.Contains("Create and Write"), "a read link is refused");
            T.Check(UpdateLocations.WriteLinkProblem(m, "https://" + Mansoft + ".blob.core.windows.net/trackvice-agent?sv=2022-11-02&sp=rcw&se=2020-01-01T00%3A00%3A00Z&sig=x", Now).Contains("expired"), "an expired link is refused");
            T.Check(UpdateLocations.WriteLinkProblem(m, Link(Mansoft, "trackvice-agent", "rcw"), Now) == null, "a link with Read, Create and Write is accepted");
            UpdateLocations.UseWriteLink(m, Link(Mansoft, "trackvice-agent", "rcw"));
            T.Check(m.CanWrite && m.CanRead && m.Missing.Length == 0 && m.Confirmed, "it is used to write and to read");
        }

        // ---------- what is published ----------

        static void Sign(Mem store, RSACryptoServiceProvider key, string version)
        {
            UpdatePublisher.Publish(store, Encoding.ASCII.GetBytes("MZ exe " + version), version, key, Now.AddDays(-3));
        }

        static void Versions(RSACryptoServiceProvider key, RSACryptoServiceProvider other)
        {
            {
                string pub = UpdateKeys.PublicKey(key);
                World w = new World();
                UpdateLocation a = new UpdateLocation();
                a.Key = "a"; a.Account = "a"; a.Container = "trackvice-agent"; a.ReadLink = Link("a", "trackvice-agent", "r"); a.WriteLink = Link("a", "trackvice-agent", "cw"); a.Confirmed = true;
                UpdateLocations.Refresh(a, w.Open, pub);
                T.Eq(UpdateState.Nothing, a.State, "an empty container has nothing published");
                T.Check(a.Behind("2.0.4") && a.Wanted("2.0.4"), "nothing published is behind, and a known writable place is ticked");
                a.Confirmed = false;
                T.Check(!a.Wanted("2.0.4"), "but a place that is only guessed is not ticked");
                a.Confirmed = true;

                Sign(w.At(a.ReadLink), key, "2.0.3");
                UpdateLocations.Refresh(a, w.Open, pub);
                T.Check(a.State == UpdateState.Published && a.Version == "2.0.3" && !a.OtherKey, "the published version is read");
                T.Check(a.Behind("2.0.4") && !a.Behind("2.0.3") && !a.Behind("2.0.2") && !a.Behind(null), "behind is older than the version to publish");
                T.Check(a.Wanted("2.0.4") && !a.Wanted("2.0.3"), "ticked only when behind");

                Sign(w.At(a.ReadLink), other, "2.0.3");
                UpdateLocations.Refresh(a, w.Open, pub);
                T.Check(a.OtherKey && !a.Wanted("2.0.4") && a.VersionText.Contains("another update key"), "another key is seen, shown and not ticked");

                UpdateLocation r = new UpdateLocation();
                r.Key = "r"; r.WriteLink = Link("r", "c", "cw");
                UpdateLocations.Refresh(r, w.Open, pub);
                T.Check(r.State == UpdateState.NoRead && !r.Wanted("2.0.4") && r.VersionText.Contains("no link that can read"), "without a read link the version is not known and the place is not ticked");

                w.At(a.ReadLink).FailFind = "agent/latest.json could not be read (HTTP 403).";
                UpdateLocations.Refresh(a, w.Open, pub);
                T.Check(a.State == UpdateState.Failed && a.Problem.Contains("403") && !a.Wanted("2.0.4"), "a read that fails is shown with its error");

                // Remembered for Overview.
                UpdateLocations.Forget();
                UpdateLocations.Remember(new UpdateLocation[] { a }, Now);
                UpdateLocation again = new UpdateLocation();
                again.Key = "a";
                UpdateLocations.Apply(new UpdateLocation[] { again });
                T.Check(again.State == UpdateState.Failed && again.Problem == a.Problem, "what was read is remembered for Overview");
                UpdateLocations.Forget();
            }
        }

        // ---------- publish ----------

        static void Publish(RSACryptoServiceProvider key, RSACryptoServiceProvider other)
        {
            World w = new World();
            // Ok: Your storage, behind.
            UpdateLocation y = new UpdateLocation();
            y.IsYourStorage = true; y.Key = "y"; y.Account = Own; y.Container = "trackvice-agent"; y.Confirmed = true;
            y.ReadLink = Link(Own, "trackvice-agent", "r"); y.WriteLink = Link(Own, "trackvice-agent", "cw"); y.Customers.Add("Northwind"); y.WithCode.Add("Northwind");
            Sign(w.At(y.ReadLink), key, "2.0.3");
            // Another key.
            UpdateLocation k = new UpdateLocation();
            k.Key = "k"; k.Account = "contosoagents"; k.Container = "trackvice-agent"; k.Confirmed = true;
            k.ReadLink = Link("contosoagents", "trackvice-agent", "r"); k.WriteLink = Link("contosoagents", "trackvice-agent", "cw"); k.Customers.Add("Contoso");
            Sign(w.At(k.ReadLink), other, "1.11.11");
            // No write access.
            UpdateLocation n = new UpdateLocation();
            n.Key = "n"; n.Account = Mansoft; n.Container = "trackvice-agent"; n.Customers.Add("Mansoft");
            n.Missing = UpdateLocations.MissingText(n, null);
            // Fails.
            UpdateLocation f = new UpdateLocation();
            f.Key = "f"; f.Account = "fabrikam"; f.Container = "trackvice-agent"; f.Confirmed = true; f.Customers.Add("Fabrikam"); f.WithCode.Add("Fabrikam");
            f.ReadLink = Link("fabrikam", "trackvice-agent", "r"); f.WriteLink = Link("fabrikam", "trackvice-agent", "cw");
            w.At(f.WriteLink).FailPut = "The upload of agent/TrackVice-2.0.4.exe failed (HTTP 403, AuthorizationPermissionMismatch).";
            // Already there, and newer.
            UpdateLocation s = new UpdateLocation();
            s.Key = "s"; s.Account = "same"; s.Container = "trackvice-agent"; s.Confirmed = true; s.ReadLink = Link("same", "trackvice-agent", "r"); s.WriteLink = Link("same", "trackvice-agent", "cw");
            Sign(w.At(s.ReadLink), key, "2.0.4");
            UpdateLocation newer = new UpdateLocation();
            newer.Key = "newer"; newer.Account = "newer"; newer.Container = "trackvice-agent"; newer.Confirmed = true; newer.ReadLink = Link("newer", "trackvice-agent", "r"); newer.WriteLink = Link("newer", "trackvice-agent", "cw");
            Sign(w.At(newer.ReadLink), key, "2.1.0");
            // The same place twice.
            UpdateLocation dup = new UpdateLocation();
            dup.Key = "y"; dup.Account = Own; dup.Container = "trackvice-agent"; dup.WriteLink = y.WriteLink; dup.ReadLink = y.ReadLink;
            // A legacy script that has a fixed exe, with nothing published.
            UpdateLocation legacy = new UpdateLocation();
            legacy.Key = "legacy"; legacy.Account = "legacy"; legacy.Container = "trackvice-agent"; legacy.Confirmed = true; legacy.Customers.Add("Old");
            legacy.ReadLink = Link("legacy", "trackvice-agent", "r"); legacy.WriteLink = Link("legacy", "trackvice-agent", "cw");

            byte[] exe = Encoding.ASCII.GetBytes("MZ TrackVice 2.0.4");
            List<PublishLine> lines = UpdateLocations.PublishTo(new UpdateLocation[] { y, k, n, f, s, newer, dup, legacy }, exe, "2.0.4", key, Now, w.Open);
            T.Eq(8, lines.Count, "publish: a line for every place");
            T.Eq(PublishOutcome.Published, lines[0].Outcome, "publish: Your storage took it");
            T.Check(lines[0].Text.Contains("published 2.0.4") && !lines[0].Text.Contains("SHA-256"), "publish: customers with a customer code need nothing more for new devices");
            Mem ym = w.At(y.ReadLink);
            UpdateManifest m = UpdateManifest.Parse(ym.Files["agent/latest.json"]);
            T.Check(m.Version == "2.0.4" && m.Verify(UpdateKeys.PublicKey(key)) && ym.Files.ContainsKey("agent/TrackVice-2.0.4.exe"), "publish: latest.json is signed and the exe is there");
            T.Check(!ym.Files.ContainsKey("agent/TrackVice.exe") && !ym.Files.ContainsKey("TrackVice.exe"), "publish: the plain TrackVice.exe that a script pins is not replaced");
            T.Check(y.Version == "2.0.4", "publish: the place knows its new version");

            T.Eq(PublishOutcome.Skipped, lines[1].Outcome, "publish: another key is skipped");
            T.Check(lines[1].Text.Contains(UpdateLocations.OtherKeyMarker) && lines[1].Text.Contains("contosoagents/trackvice-agent"), "publish: with the explanation");
            T.Check(UpdateManifest.Parse(w.At(k.ReadLink).Files["agent/latest.json"]).Version == "1.11.11" && !w.At(k.ReadLink).Files.ContainsKey("agent/TrackVice-2.0.4.exe"), "publish: nothing is written there");

            T.Eq(PublishOutcome.Skipped, lines[2].Outcome, "publish: no write access is skipped");
            T.Check(lines[2].Text.Contains("Shared access tokens") && lines[2].Text.Contains(Mansoft), "publish: and says how to fix it");

            T.Eq(PublishOutcome.Failed, lines[3].Outcome, "publish: an upload that fails is a failure");
            T.Check(lines[3].Text.Contains("AuthorizationPermissionMismatch") && lines[3].Text.StartsWith("failed: "), "publish: with its exact error");
            T.Check(!w.At(f.ReadLink).Files.ContainsKey("agent/latest.json"), "publish: no latest.json after a failed upload");

            T.Check(lines[4].Outcome == PublishOutcome.Skipped && lines[4].Text.Contains("2.0.4 already"), "publish: the same version is skipped");
            T.Check(lines[5].Outcome == PublishOutcome.Skipped && lines[5].Text.Contains("2.1.0") && lines[5].Text.Contains("never install an older"), "publish: a newer version there is kept");
            T.Check(lines[6].Outcome == PublishOutcome.Skipped && lines[6].Text.Contains("same place"), "publish: a place twice is done once");

            T.Eq(PublishOutcome.Published, lines[7].Outcome, "publish: nothing there yet takes it");
            T.Check(lines[7].Text.Contains("SHA-256") && lines[7].Text.Contains("Update the script in Intune") && lines[7].Text.Contains("not replaced"), "publish: a fixed script is told that new devices keep its exe, and how to change it");

            string banner = UpdateLocations.Summary(lines, "2.0.4");
            T.Check(banner.StartsWith("Published 2.0.4 to 2 places: Your storage, legacy/trackvice-agent."), "banner names the places: " + banner);
            T.Check(banner.Contains("Not published to 6 places"), "banner counts the others: " + banner);
            List<PublishLine> one = new List<PublishLine>();
            one.Add(lines[0]);
            T.Eq("Published 2.0.4 to 1 place: Your storage.", UpdateLocations.Summary(one, "2.0.4"), "banner for one place");
            List<PublishLine> bad = new List<PublishLine>();
            bad.Add(lines[3]);
            T.Check(UpdateLocations.Summary(bad, "2.0.4").StartsWith("Not published: fabrikam/trackvice-agent: failed:"), "banner for one failure");
            T.Eq("Published 1.4.0 to 1 place: Your storage. Installed agents update within 6 hours.", UpdateLocations.Done("1.4.0", new string[] { "Your storage" }), "banner after publishing to Your storage");

            // A place with no read link is published without a key check.
            UpdateLocation blind = new UpdateLocation();
            blind.Key = "blind"; blind.Account = "b"; blind.Container = "c"; blind.WriteLink = Link("b", "c", "cw");
            List<PublishLine> bl = UpdateLocations.PublishTo(new UpdateLocation[] { blind }, exe, "2.0.4", key, Now, w.Open);
            T.Eq(PublishOutcome.Published, bl[0].Outcome, "publish: a write-only link publishes");
        }

        // ---------- Overview ----------

        static OverviewDevice Dev(string customer, string name, string version)
        {
            OverviewDevice o = new OverviewDevice();
            o.Customer = customer;
            o.Kind = OverviewKinds.Agents;
            o.D = new RemoteDeployment();
            o.D.Device = name;
            o.D.Key = name;
            o.D.User = "Autopilot";
            o.D.Version = version;
            o.D.LastUtc = Now.AddMinutes(-1);
            o.D.StartUtc = Now.AddHours(-1);
            o.D.State = State.Installing;
            o.D.StateText = "3 of 12 done";
            return o;
        }

        static string Text(List<OverviewDevice> devs, List<UpdateLocation> places, string running, string published)
        {
            OverviewData d = new OverviewData();
            d.Devices.AddRange(devs);
            foreach (AttentionTile t in AgentVersions.Tiles(d, Now, published, places, running)) if (t.Key == "agents") return t.Text;
            return null;
        }

        static void Overview()
        {
            UpdateLocation y = new UpdateLocation();
            y.IsYourStorage = true; y.Key = "y"; y.Account = Own; y.Container = "trackvice-agent"; y.Confirmed = true; y.WriteLink = "w"; y.ReadLink = "r";
            y.State = UpdateState.Published; y.Version = "2.0.4";
            UpdateLocation m = new UpdateLocation();
            m.Key = "m"; m.Account = Mansoft; m.Container = "trackvice-agent"; m.Customers.Add("Mansoft_2026_DLB1"); m.WriteLink = "w"; m.ReadLink = "r";
            m.State = UpdateState.Nothing;
            List<UpdateLocation> places = new List<UpdateLocation>();
            places.Add(y);
            places.Add(m);

            List<OverviewDevice> devs = new List<OverviewDevice>();
            for (int i = 1; i <= 5; i++) devs.Add(Dev("Mansoft_2026_DLB1", "MAN-PC" + i, "1.11.11"));
            devs.Add(Dev("", "OWN-PC1", "2.0.4"));
            string text = Text(devs, places, "2.0.4", "2.0.4");
            T.Eq("Mansoft_2026_DLB1: 5 agents on 1.11.11; " + Mansoft + "/trackvice-agent has nothing published (publish here). 1 agent on 2.0.4; Your storage has 2.0.4.", text, "overview: a location that has nothing is named and flagged");

            m.State = UpdateState.Published; m.Version = "2.0.3";
            text = Text(devs, places, "2.0.4", "2.0.4");
            T.Check(text != null && text.Contains(Mansoft + "/trackvice-agent has 2.0.3 (publish here)."), "overview: a location that is behind is flagged: " + text);
            T.Check(text != null && text.Contains("Older agents update within 6 hours."), "overview: and older agents are told they update");

            m.WriteLink = "";
            text = Text(devs, places, "2.0.4", "2.0.4");
            T.Check(text != null && text.Contains("this PC needs a write link"), "overview: a place this PC cannot write to says so: " + text);
            m.WriteLink = "w";

            // All current.
            List<OverviewDevice> cur = new List<OverviewDevice>();
            cur.Add(Dev("Mansoft_2026_DLB1", "MAN-PC1", "2.0.4"));
            cur.Add(Dev("", "OWN-PC1", "2.0.4"));
            m.Version = "2.0.4";
            T.Eq("All 2 agents are on 2.0.4, the version published.", Text(cur, places, "2.0.4", "2.0.4"), "overview: all current says so");

            // Several customers in one location, and mixed versions.
            m.Customers.Add("Contoso");
            List<OverviewDevice> mix = new List<OverviewDevice>();
            mix.Add(Dev("Mansoft_2026_DLB1", "A1", "2.0.4"));
            mix.Add(Dev("Contoso", "A2", "2.0.3"));
            T.Eq("Mansoft_2026_DLB1 and Contoso: 1 on 2.0.4, 1 on 2.0.3; " + Mansoft + "/trackvice-agent has 2.0.4. Older agents update within 6 hours.", Text(mix, places, "2.0.4", "2.0.4"), "overview: customers of one location and mixed versions");

            // Without locations it is the old text, compared with Your storage.
            T.Eq("1 on 2.0.4, 1 on 2.0.3 (they update within 6 hours). Your storage has 2.0.4.", Text(mix, null, "2.0.4", "2.0.4"), "overview: without locations the text is as before");

            // The agents of a location are compared with that location, not with Your storage.
            OverviewData d = new OverviewData();
            d.Devices.Add(Dev("Mansoft_2026_DLB1", "B1", "1.11.11"));
            m.State = UpdateState.Published; m.Version = "1.11.11";
            bool behind = false;
            foreach (AttentionTile t in AgentVersions.Tiles(d, Now, "2.0.4", places, "2.0.4")) if (t.Key == "agents-behind") behind = true;
            T.Check(!behind, "overview: an agent on what its own location has is not behind, whatever Your storage has");
        }
    }
}
