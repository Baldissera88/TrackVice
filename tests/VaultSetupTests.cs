namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;

    public static class VaultSetupTests
    {
        sealed class Held
        {
            public string Value, ContentType, Fingerprint;
        }

        // A vault with secrets, tags and refusals per secret, like RBAC on one secret.
        sealed class FakeVault : IVaultHttp
        {
            public readonly Dictionary<string, Held> Secrets = new Dictionary<string, Held>();
            public readonly HashSet<string> Denied = new HashSet<string>();
            public readonly List<string> Writes = new List<string>();
            public readonly List<string> Calls = new List<string>();

            public WebReply Send(string method, string url, string token, string json)
            {
                Calls.Add(method + " " + url);
                string name = Uri.UnescapeDataString(new Uri(url).AbsolutePath.Substring(9));
                if (Denied.Contains(name)) return new WebReply(403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Caller is not authorized.\"}}");
                if (method == "GET")
                {
                    Held h;
                    if (!Secrets.TryGetValue(name, out h)) return new WebReply(404, "{\"error\":{\"code\":\"SecretNotFound\"}}");
                    Dictionary<string, object> d = SetupJson.Obj("value", h.Value, "contentType", h.ContentType, "attributes", SetupJson.Obj("updated", 1790000000L));
                    if (h.Fingerprint != null) d["tags"] = SetupJson.Obj("fingerprint", h.Fingerprint);
                    return new WebReply(200, SetupJson.Write(d));
                }
                Dictionary<string, object> b = ImeJson.Parse(json) as Dictionary<string, object>;
                Held n = new Held();
                n.Value = Http.Str(b, "value");
                n.ContentType = Http.Str(b, "contentType");
                n.Fingerprint = Http.Str(ImeJson.Get(b, "tags") as Dictionary<string, object>, "fingerprint");
                Secrets[name] = n;
                Writes.Add(name);
                return new WebReply(200, "{}");
            }
        }

        sealed class Answers : IVaultSetupAsk
        {
            public bool ReplaceVault, ReplaceLocal, UseStorage;
            public int Asked;
            public string VaultFp, LocalFp;

            public bool ReplaceVaultKey(string vaultFingerprint, string localFingerprint) { Asked++; VaultFp = vaultFingerprint; LocalFp = localFingerprint; return ReplaceVault; }
            public bool ReplaceLocalKey(string localFingerprint, string vaultFingerprint) { Asked++; LocalFp = localFingerprint; VaultFp = vaultFingerprint; return ReplaceLocal; }
            public bool UseVaultStorage(OwnStorage local, OwnStorage vault) { Asked++; return UseStorage; }
        }

        static byte[] blobA, blobB;

        static VaultSetup Make(IVaultHttp v)
        {
            string p;
            return new VaultSetup(v, delegate { return "token"; }, VaultAddress.Parse("contoso-vault", out p));
        }

        static OwnStorage Storage(string account, int year)
        {
            OwnStorage o = new OwnStorage();
            o.Account = account;
            o.BlobEndpoint = "https://" + account + ".blob.core.windows.net/";
            o.Key = "bWFkZS11cC1rZXktZm9yLXRlc3Rz";
            o.Subscription = "Contoso Production";
            o.AgentRead = "https://" + account + ".blob.core.windows.net/trackvice-agent?sv=2022-11-02&sp=rl&sig=TESTSIGNATURE";
            o.AgentWrite = "https://" + account + ".blob.core.windows.net/trackvice-agent?sv=2022-11-02&sp=cw&sig=TESTSIGNATURE";
            o.ExpiresUtc = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            return o;
        }

        static string Fp(byte[] blob)
        {
            return VaultSetup.FingerprintOf(blob);
        }

        public static void Run()
        {
            using (RSACryptoServiceProvider a = UpdateKeys.Create()) blobA = a.ExportCspBlob(true);
            using (RSACryptoServiceProvider b = UpdateKeys.Create()) blobB = b.ExportCspBlob(true);
            T.Check(Fp(blobA) != null && Fp(blobA) != Fp(blobB), "the two test keys have different fingerprints");
            Decisions();
            Saving();
            ReplacingInVault();
            ListingIgnoresSetup();
            EmptyPc();
            SameKey();
            OtherKey();
            Denied();
            StorageChoice();
            Describing();
        }

        static void Decisions()
        {
            T.Eq(KeyChoice.Go, VaultSetup.SaveKey(null, "AA"), "save: no key in the vault");
            T.Eq(KeyChoice.Leave, VaultSetup.SaveKey("AA", "AA"), "save: same key");
            T.Eq(KeyChoice.Ask, VaultSetup.SaveKey("BB", "AA"), "save: other key asks");
            T.Eq(KeyChoice.Go, VaultSetup.GetKey(null, "AA"), "get: no key on the PC");
            T.Eq(KeyChoice.Leave, VaultSetup.GetKey("AA", "AA"), "get: same key");
            T.Eq(KeyChoice.Ask, VaultSetup.GetKey("AA", "BB"), "get: other key asks");
            T.Eq(KeyChoice.Leave, VaultSetup.GetKey("AA", null), "get: nothing in the vault");
            T.Eq("The vault already has another update key. Replacing it makes the devices that have it refuse updates.", VaultSetup.AskReplaceVault, "the question for the vault key");
        }

        static void Saving()
        {
            FakeVault v = new FakeVault();
            MemoryKeyStore keys = new MemoryKeyStore();
            keys.Blob = blobA;
            OwnStorage own = Storage("contosoagents", 2031);
            VaultSetupResult r = Make(v).Save(own, keys, new Answers());
            T.Check(r.StorageDone && r.KeyDone, "save writes both");
            T.Check(v.Secrets.ContainsKey("trackvice-setup-storage") && v.Secrets.ContainsKey("trackvice-setup-updatekey"), "both secret names");
            Held s = v.Secrets["trackvice-setup-storage"], k = v.Secrets["trackvice-setup-updatekey"];
            T.Eq("application/vnd.trackvice.storage+json", s.ContentType, "storage content type");
            T.Eq("application/vnd.trackvice.updatekey", k.ContentType, "key content type");
            T.Eq(own.Text(), s.Value, "storage as JSON");
            T.Eq(Convert.ToBase64String(blobA), k.Value, "key as base64");
            T.Eq(Fp(blobA), k.Fingerprint, "fingerprint tag");
            T.Eq(UpdateKeys.Fingerprint(PublicOf(blobA)), k.Fingerprint, "the tag is the fingerprint the Publish dialog shows");
            T.Eq(null, s.Fingerprint, "the storage secret has no fingerprint tag");

            v.Writes.Clear();
            VaultSetupResult again = Make(v).Save(own, keys, new Answers());
            T.Eq(0, v.Writes.Count, "saving the same again writes nothing");
            T.Check(!again.StorageDone && !again.KeyDone, "nothing done the second time");

            FakeVault w = new FakeVault();
            VaultSetupResult none = Make(w).Save(null, new MemoryKeyStore(), new Answers());
            T.Eq(0, w.Writes.Count, "no setup on the PC: nothing is written");
            T.Check(none.Text.Contains("no storage settings") && none.Text.Contains("no update key"), "says what was left out: " + none.Text);
        }

        static string PublicOf(byte[] blob)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(new CspParameters(24)))
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportCspBlob(blob);
                return UpdateKeys.PublicKey(rsa);
            }
        }

        static void ReplacingInVault()
        {
            FakeVault v = new FakeVault();
            Held old = new Held();
            old.Value = Convert.ToBase64String(blobB);
            old.ContentType = VaultSetup.KeyType;
            old.Fingerprint = Fp(blobB);
            v.Secrets[VaultSetup.KeyName] = old;
            MemoryKeyStore keys = new MemoryKeyStore();
            keys.Blob = blobA;
            Answers no = new Answers();
            VaultSetupResult r = Make(v).Save(null, keys, no);
            T.Eq(1, no.Asked, "another key in the vault asks");
            T.Eq(Fp(blobB), no.VaultFp, "the question has the fingerprint of the vault key");
            T.Check(!r.KeyDone && v.Secrets[VaultSetup.KeyName].Value == old.Value, "the vault key is not replaced by default");
            Answers yes = new Answers();
            yes.ReplaceVault = true;
            r = Make(v).Save(null, keys, yes);
            T.Check(r.KeyDone && v.Secrets[VaultSetup.KeyName].Fingerprint == Fp(blobA), "replaced after a yes");

            // The role to read is missing: it is not known what the vault holds, so it asks.
            FakeVault blind = new FakeVault();
            blind.Denied.Add(VaultSetup.KeyName);
            Answers ask = new Answers();
            Make(blind).Save(null, keys, ask);
            T.Eq(1, ask.Asked, "an unreadable vault key asks too");
            T.Eq(null, ask.VaultFp, "and says the vault key is not known");
        }

        static void ListingIgnoresSetup()
        {
            MemoryVault v = new MemoryVault();
            DateTime t = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            v.Add("tv-contoso", "Contoso", "{\"Customers\":[{\"Name\":\"Contoso\"}]}", t);
            // Even with the customer content type, a name without tv- is not a customer.
            v.Add("trackvice-setup-storage", "x", "{}", t);
            v.Add("trackvice-setup-updatekey", "x", "AAAA", t);
            string p;
            KeyVaultStore store = new KeyVaultStore(v, delegate { return "token"; }, VaultAddress.Parse("contoso-vault", out p));
            List<VaultSecret> l = store.List();
            T.Eq(1, l.Count, "only the customer is listed");
            T.Eq("tv-contoso", l[0].Name, "the customer secret");
        }

        static void EmptyPc()
        {
            FakeVault v = new FakeVault();
            OwnStorage own = Storage("contosoagents", 2031);
            Held s = new Held(), k = new Held();
            s.Value = own.Text(); s.ContentType = VaultSetup.StorageType;
            k.Value = Convert.ToBase64String(blobA); k.ContentType = VaultSetup.KeyType; k.Fingerprint = Fp(blobA);
            v.Secrets[VaultSetup.StorageName] = s;
            v.Secrets[VaultSetup.KeyName] = k;
            MemorySecrets secrets = new MemorySecrets();
            MemoryKeyStore keys = new MemoryKeyStore();
            Answers ask = new Answers();
            VaultSetupResult r = Make(v).Get(secrets, keys, ask);
            T.Eq(0, ask.Asked, "an empty PC asks nothing");
            T.Check(r.StorageDone && r.KeyDone, "both imported");
            T.Eq(own.Text(), OwnStorage.Load(secrets).Text(), "Your storage is saved the way Your storage saves it");
            T.Eq(Convert.ToBase64String(blobA), Convert.ToBase64String(keys.Blob), "the key is in the key store");
            T.Eq(0, v.Writes.Count, "get writes nothing to the vault");
            foreach (KeyValuePair<string, string> kv in secrets.Values)
                T.Check(!kv.Value.Contains(k.Value), "the key is not in the secrets: " + kv.Key);
            T.Eq(1, secrets.Values.Count, "only Your storage is kept in the secrets");
        }

        static void SameKey()
        {
            FakeVault v = new FakeVault();
            Held k = new Held();
            k.Value = Convert.ToBase64String(blobA); k.ContentType = VaultSetup.KeyType; k.Fingerprint = Fp(blobA);
            v.Secrets[VaultSetup.KeyName] = k;
            MemoryKeyStore keys = new MemoryKeyStore();
            keys.Blob = blobA;
            Answers ask = new Answers();
            VaultSetupResult r = Make(v).Get(new MemorySecrets(), keys, ask);
            T.Check(!r.KeyDone && ask.Asked == 0, "the same key: nothing to do, nothing asked");
            T.Check(r.Text.Contains("Nothing to do"), "it says so: " + r.Text);
        }

        static void OtherKey()
        {
            FakeVault v = new FakeVault();
            Held k = new Held();
            k.Value = Convert.ToBase64String(blobB); k.ContentType = VaultSetup.KeyType; k.Fingerprint = Fp(blobB);
            v.Secrets[VaultSetup.KeyName] = k;
            MemoryKeyStore keys = new MemoryKeyStore();
            keys.Blob = blobA;
            Answers ask = new Answers();
            VaultSetupResult r = Make(v).Get(new MemorySecrets(), keys, ask);
            T.Eq(1, ask.Asked, "another key asks");
            T.Eq(Fp(blobA), ask.LocalFp, "the question has the key of this PC");
            T.Eq(Fp(blobB), ask.VaultFp, "and the key of the vault");
            T.Check(!r.KeyDone && keys.Blob == blobA, "the key on this PC is kept by default");
            T.Check(VaultSetup.AskReplaceLocal.Contains("Customer codes made on this PC contain this PC's key"), "the explanation");
            ask.ReplaceLocal = true;
            r = Make(v).Get(new MemorySecrets(), keys, ask);
            T.Check(r.KeyDone && Fp(keys.Blob) == Fp(blobB), "replaced after a yes");

            // A blob that is no key is not imported, and a key that cannot be read is not replaced silently.
            Held bad = new Held();
            bad.Value = "bWFkZS11cC1ub3QtYS1rZXk="; bad.ContentType = VaultSetup.KeyType;
            v.Secrets[VaultSetup.KeyName] = bad;
            MemoryKeyStore empty = new MemoryKeyStore();
            r = Make(v).Get(new MemorySecrets(), empty, new Answers());
            T.Check(!r.KeyDone && empty.Blob == null, "a damaged key is not imported");
            MemoryKeyStore broken = new MemoryKeyStore();
            broken.Blob = new byte[] { 1, 2, 3 };
            v.Secrets[VaultSetup.KeyName].Value = Convert.ToBase64String(blobA);
            Answers a2 = new Answers();
            r = Make(v).Get(new MemorySecrets(), broken, a2);
            T.Check(a2.Asked == 1 && broken.Blob.Length == 3, "an unreadable key on the PC is asked about");
        }

        static void Denied()
        {
            FakeVault v = new FakeVault();
            OwnStorage own = Storage("contosoagents", 2031);
            Held s = new Held();
            s.Value = own.Text(); s.ContentType = VaultSetup.StorageType;
            v.Secrets[VaultSetup.StorageName] = s;
            Held k = new Held();
            k.Value = Convert.ToBase64String(blobA); k.ContentType = VaultSetup.KeyType;
            v.Secrets[VaultSetup.KeyName] = k;
            v.Denied.Add(VaultSetup.KeyName);
            MemorySecrets secrets = new MemorySecrets();
            MemoryKeyStore keys = new MemoryKeyStore();
            VaultSetupResult r = Make(v).Get(secrets, keys, new Answers());
            T.Check(r.StorageDone && OwnStorage.Load(secrets) != null, "403 on the key still imports the storage settings");
            T.Check(r.KeyDenied && !r.KeyDone && keys.Blob == null, "the key is not imported");
            T.Check(r.Text.Contains("but not the update key") && r.Text.Contains("403"), "it says so plainly: " + r.Text);

            // Another refusal, such as the firewall, is not hidden as a missing role.
            bool thrown = false;
            try { Make(new Firewall()).Get(new MemorySecrets(), new MemoryKeyStore(), new Answers()); }
            catch (VaultException) { thrown = true; }
            T.Check(thrown, "a firewall refusal is an error");
        }

        sealed class Firewall : IVaultHttp
        {
            public WebReply Send(string method, string url, string token, string json)
            {
                return new WebReply(403, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Client address is not authorized. ForbiddenByFirewall\"}}");
            }
        }

        static void StorageChoice()
        {
            OwnStorage later = Storage("contosoagents", 2031), earlier = Storage("contosoagents", 2029);
            T.Eq(KeyChoice.Go, VaultSetup.GetStorage(null, later), "no storage on the PC");
            T.Eq(KeyChoice.Leave, VaultSetup.GetStorage(later, later.Copy()), "the same storage");
            T.Eq(KeyChoice.Ask, VaultSetup.GetStorage(earlier, later), "the vault runs longer");
            T.Eq(KeyChoice.Leave, VaultSetup.GetStorage(later, earlier), "this PC runs longer");
            OwnStorage none = Storage("contosoagents", 2029);
            none.ExpiresUtc = default(DateTime);
            T.Eq(KeyChoice.Ask, VaultSetup.GetStorage(none, later), "this PC has no expiry");

            FakeVault v = new FakeVault();
            Held s = new Held();
            s.Value = later.Text(); s.ContentType = VaultSetup.StorageType;
            v.Secrets[VaultSetup.StorageName] = s;
            MemorySecrets secrets = new MemorySecrets();
            earlier.Save(secrets);
            Answers ask = new Answers();
            VaultSetupResult r = Make(v).Get(secrets, new MemoryKeyStore(), ask);
            T.Check(ask.Asked == 1 && !r.StorageDone && OwnStorage.Load(secrets).ExpiresUtc.Year == 2029, "asked, and kept by default");
            ask.UseStorage = true;
            r = Make(v).Get(secrets, new MemoryKeyStore(), ask);
            T.Check(r.StorageDone && OwnStorage.Load(secrets).ExpiresUtc.Year == 2031, "used after a yes");
        }

        static void Describing()
        {
            FakeVault v = new FakeVault();
            MemoryKeyStore keys = new MemoryKeyStore();
            keys.Blob = blobA;
            Make(v).Save(Storage("contosoagents", 2031), keys, new Answers());
            string text = Make(v).Describe();
            T.Check(text.Contains("contosoagents") && text.Contains(Fp(blobA)), "the status has the account and the fingerprint: " + text);
            T.Check(!text.Contains("bWFkZS11cC1rZXk") && !text.Contains(Convert.ToBase64String(blobA)), "no secret in the status");
            v.Denied.Add(VaultSetup.KeyName);
            T.Check(Make(v).Describe().Contains("access refused"), "a refused key is said so");
        }
    }
}
