namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Windows.Forms;

    // The update locations of this PC's customers, for Publish agent update and Overview.
    public static class UpdatePlacesUi
    {
        const string PublishSecret = "AgentUpdateStore.";
        static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
        static readonly object gate = new object();
        static bool reading;
        static List<UpdateLocation> demo;

        public static string SecretName(string customer)
        {
            return PublishSecret + customer;
        }

        // Derived from the customers, their saved links and codes, and Your storage, with what was read last.
        public static List<UpdateLocation> Build()
        {
            if (demo != null) return demo;
            List<UpdateCustomer> cs = new List<UpdateCustomer>();
            foreach (Customer c in CustomerUi.Book.All)
                cs.Add(new UpdateCustomer(c.Name, c.AgentLocation, CustomerCodes.Load(YourStorageUi.Secrets, c.Name) ?? "", Secret.Load(SecretName(c.Name))));
            List<UpdateLocation> l = UpdateLocations.Derive(cs, YourStorageUi.Load(), DateTime.UtcNow);
            UpdateLocations.Apply(l);
            return l;
        }

        // Keeps a link for Publish agent update for every customer of the location.
        public static void SaveLink(UpdateLocation place, string link)
        {
            foreach (string name in place.Customers) Secret.Save(SecretName(name), link);
        }

        public static string PublicKeyOrNull()
        {
            try
            {
                using (RSACryptoServiceProvider k = UpdateKeys.Load(YourStorageUi.Keys)) return k == null ? null : UpdateKeys.PublicKey(k);
            }
            catch (Exception) { return null; }
        }

        // Reads agent/latest.json of every location away from the window. Does nothing when it was read lately or is being read.
        // done runs on a worker thread.
        public static void RefreshIfStale(Action done)
        {
            if (demo != null) return;
            lock (gate)
            {
                if (reading || DateTime.UtcNow - UpdateLocations.ReadUtc < Stale) return;
                reading = true;
            }
            // Set at once, so a failing read is not repeated at every redraw.
            UpdateLocations.ReadUtc = DateTime.UtcNow;
            List<UpdateLocation> places = Build();
            string key = PublicKeyOrNull();
            Func<string, ISessionStore> open = UpdateLocations.OpenWith(YourStorageUi.Web);
            Thread th = new Thread(delegate ()
            {
                try
                {
                    foreach (UpdateLocation l in places) UpdateLocations.Refresh(l, open, key);
                    UpdateLocations.Remember(places, DateTime.UtcNow);
                }
                catch (Exception) { }
                finally { lock (gate) reading = false; }
                try { if (done != null) done(); }
                catch (Exception) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        // ---------- made-up ----------

        // Contoso and Woodgrove share a container their agents read, Fabrikam has a share, and Your storage is there too.
        public static List<UpdateLocation> DemoPlaces(string running)
        {
            if (demo != null) return demo;
            string key = "made-up";
            CustomerCode contoso = new CustomerCode();
            contoso.Name = "Contoso";
            contoso.Upload = "https://contosoagents.blob.core.windows.net/trackvice?sv=2022-11-02&sp=cw&sig=made-up";
            contoso.Agent = "https://contosoagents.blob.core.windows.net/trackvice-agent?sv=2022-11-02&sp=r&sig=made-up";
            contoso.Key = key;
            List<UpdateCustomer> cs = new List<UpdateCustomer>();
            cs.Add(new UpdateCustomer("Contoso", "https://contosoagents.blob.core.windows.net/trackvice?sv=2022-11-02&sp=rl&sig=made-up", contoso.ToString(), ""));
            cs.Add(new UpdateCustomer("Fabrikam", @"\\FS01\TrackVice$\Fabrikam", "", @"\\FS01\TrackVice$\Fabrikam"));
            cs.Add(new UpdateCustomer("Woodgrove Bank", "https://contosoagents.blob.core.windows.net/trackvice?sv=2022-11-02&sp=rl&sig=made-up", "", ""));
            OwnStorage own = OwnStorage.Load(YourStorageUi.DemoSecrets());
            List<UpdateLocation> l = UpdateLocations.Derive(cs, own, DateTime.UtcNow);
            string older = "2.0.2";
            foreach (UpdateLocation p in l)
            {
                if (p.IsYourStorage) { p.State = UpdateState.Published; p.Version = older; }
                else if (p.IsFolder) { p.State = UpdateState.Nothing; }
                else { p.State = UpdateState.Published; p.Version = "1.4.0"; }
            }
            return l;
        }

        // Makes the demo places the ones Build returns, so Overview and the Publish dialog show them.
        public static void UseDemo(string running)
        {
            demo = DemoPlaces(running);
        }

        public static void ClearDemo()
        {
            demo = null;
        }

        // An empty folder-like store in memory, for the demo's Publish.
        public sealed class DemoStore : ISessionStore, IFindStore
        {
            readonly Dictionary<string, byte[]> files;
            readonly string title;

            public DemoStore(string title, Dictionary<string, byte[]> files)
            {
                this.title = title;
                this.files = files;
            }

            public string Title { get { return title; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { files[name] = data; }
            public List<StoreEntry> List(string prefix) { return new List<StoreEntry>(); }
            public byte[] Find(string name) { byte[] b; return files.TryGetValue(name, out b) ? b : null; }
            public byte[] Get(string name) { byte[] b = Find(name); if (b == null) throw new StoreException(name + " is not there."); return b; }
        }
    }
}
