namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;

    // One of the two setup secrets, as the vault holds it.
    public sealed class VaultSetupSecret
    {
        public string Name = "", ContentType = "", Value = "", Fingerprint = "";
        public DateTime? Updated;
    }

    // The questions the person is asked. Each answer defaults to leaving things as they are.
    public interface IVaultSetupAsk
    {
        // Save: the vault holds another update key than this PC. vaultFingerprint is null when the vault would not tell.
        bool ReplaceVaultKey(string vaultFingerprint, string localFingerprint);
        // Get: this PC has another update key than the vault.
        bool ReplaceLocalKey(string localFingerprint, string vaultFingerprint);
        // Get: the vault has later Your storage settings than this PC, or this PC has none with an expiry.
        bool UseVaultStorage(OwnStorage local, OwnStorage vault);
    }

    public sealed class VaultSetupResult
    {
        public readonly List<string> Lines = new List<string>();
        public bool StorageDone, KeyDone, StorageDenied, KeyDenied;

        public string Text
        {
            get { return string.Join(" ", Lines.ToArray()); }
        }
    }

    public enum KeyChoice
    {
        // Nothing to do, or nothing to take.
        Leave,
        // Write or import without asking.
        Go,
        // The keys differ: ask, and leave the key alone unless the answer is yes.
        Ask
    }

    // Your whole setup in the Key Vault: Your storage and the update key, as two secrets with the prefix trackvice-, which the
    // customer list does not read. It is only added to and updated. Nothing is deleted, in the vault or on the PC.
    public sealed class VaultSetup
    {
        public const string StorageName = "trackvice-setup-storage";
        public const string KeyName = "trackvice-setup-updatekey";
        public const string StorageType = "application/vnd.trackvice.storage+json";
        public const string KeyType = "application/vnd.trackvice.updatekey";
        public const string FingerprintTag = "fingerprint";

        public const string AskReplaceVault = "The vault already has another update key. Replacing it makes the devices that have it refuse updates.";
        public const string AskReplaceLocal = "The vault has another update key than this PC. Customer codes made on this PC contain this PC's key. If you replace it, the devices that have those codes refuse updates.";
        public const string RoleNote = "Key Vault Secrets User reads. The update key is the one secret that lets someone sign updates for every device, so give the role on that one secret only to the people who may publish.";
        public const string KeyDeniedText = "This account can read the setup but not the update key (the vault answered 403). Ask for the role Key Vault Secrets User on the secret " + KeyName + ", or publish from a PC that has the key.";

        readonly IVaultHttp http;
        readonly Func<string> token;
        readonly VaultTarget target;

        public VaultSetup(IVaultHttp http, Func<string> token, VaultTarget target)
        {
            this.http = http;
            this.token = token;
            this.target = target;
        }

        // ---------- the decisions ----------

        // Save: whether to write this PC's key over what the vault holds. vaultFp is null when the vault has none.
        public static KeyChoice SaveKey(string vaultFp, string localFp)
        {
            if (string.IsNullOrEmpty(vaultFp)) return KeyChoice.Go;
            return vaultFp == localFp ? KeyChoice.Leave : KeyChoice.Ask;
        }

        // Get: what to do with the key of the vault. localFp is null when this PC has no key.
        public static KeyChoice GetKey(string localFp, string vaultFp)
        {
            if (string.IsNullOrEmpty(vaultFp)) return KeyChoice.Leave;
            if (string.IsNullOrEmpty(localFp)) return KeyChoice.Go;
            return localFp == vaultFp ? KeyChoice.Leave : KeyChoice.Ask;
        }

        // Get: Go when this PC has no Your storage, Ask when the vault's runs longer, otherwise Leave.
        public static KeyChoice GetStorage(OwnStorage local, OwnStorage vault)
        {
            if (vault == null) return KeyChoice.Leave;
            if (local == null) return KeyChoice.Go;
            if (string.Equals(local.Text(), vault.Text(), StringComparison.Ordinal)) return KeyChoice.Leave;
            if (local.ExpiresUtc == default(DateTime) && vault.ExpiresUtc != default(DateTime)) return KeyChoice.Ask;
            return vault.ExpiresUtc > local.ExpiresUtc ? KeyChoice.Ask : KeyChoice.Leave;
        }

        // The fingerprint of the public part of a private key blob, or null when the blob is not a private RSA key.
        public static string FingerprintOf(byte[] privateBlob)
        {
            if (privateBlob == null || privateBlob.Length == 0) return null;
            try
            {
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(new CspParameters(24)))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.ImportCspBlob(privateBlob);
                    if (rsa.PublicOnly) return null;
                    return UpdateKeys.Fingerprint(UpdateKeys.PublicKey(rsa));
                }
            }
            catch (Exception) { return null; }
        }

        // ---------- the calls ----------

        string Url(string name)
        {
            return target.BaseUrl + "/secrets/" + Uri.EscapeDataString(name) + "?" + VaultAddress.Api;
        }

        // The secret, or null when the vault has none of that name. A refusal throws a VaultException.
        public VaultSetupSecret Read(string name)
        {
            WebReply r = http.Send("GET", Url(name), token(), null);
            if (r.Status == 404) return null;
            if (r.Status < 200 || r.Status > 299) throw KeyVaultStore.Fail(r, "read the secret " + name);
            Dictionary<string, object> d = r.Json;
            VaultSetupSecret s = new VaultSetupSecret();
            s.Name = name;
            s.Value = Http.Str(d, "value") ?? "";
            s.ContentType = Http.Str(d, "contentType") ?? "";
            s.Fingerprint = Http.Str(ImeJson.Get(d, "tags") as Dictionary<string, object>, FingerprintTag) ?? "";
            s.Updated = VaultCustomers.FromEpoch(ImeJson.Get(ImeJson.Get(d, "attributes") as Dictionary<string, object>, "updated"));
            return s;
        }

        void Put(string name, string contentType, string value, string fingerprint)
        {
            Dictionary<string, object> body = SetupJson.Obj("value", value, "contentType", contentType);
            if (fingerprint != null) body["tags"] = SetupJson.Obj(FingerprintTag, fingerprint);
            WebReply r = http.Send("PUT", Url(name), token(), SetupJson.Write(body));
            if (r.Status < 200 || r.Status > 299) throw KeyVaultStore.Fail(r, "save the secret " + name);
        }

        static bool Refused(VaultException ex)
        {
            return (ex.Status == 403 || ex.Status == 401) && ex.Message == VaultAddress.RoleText;
        }

        static string LocalFingerprint(IUpdateKeyStore keys)
        {
            byte[] blob = keys.Load();
            if (blob == null) return null;
            // A key that cannot be read is still a key: it is never replaced without asking.
            return FingerprintOf(blob) ?? "unreadable";
        }

        // ---------- what the vault holds ----------

        // The account name and the key fingerprint, for the status line. No secret is returned.
        public string Describe()
        {
            List<string> parts = new List<string>();
            try
            {
                VaultSetupSecret s = Read(StorageName);
                OwnStorage o = s == null ? null : OwnStorage.Parse(s.Value);
                parts.Add(s == null ? "no storage settings" : o == null ? "storage settings that could not be read" : "storage account " + o.Account);
            }
            catch (VaultException ex)
            {
                if (!Refused(ex)) throw;
                parts.Add("storage settings: access refused");
            }
            try
            {
                VaultSetupSecret k = Read(KeyName);
                string fp = k == null ? null : (FingerprintOf(Convert64(k.Value)) ?? (k.Fingerprint.Length > 0 ? k.Fingerprint : null));
                parts.Add(k == null ? "no update key" : fp == null ? "an update key that could not be read" : "update key " + fp);
            }
            catch (VaultException ex)
            {
                if (!Refused(ex)) throw;
                parts.Add("update key: access refused");
            }
            return "The vault holds: " + string.Join(", ", parts.ToArray()) + ".";
        }

        static byte[] Convert64(string s)
        {
            try { return Convert.FromBase64String((s ?? "").Trim()); }
            catch (FormatException) { return null; }
        }

        // ---------- save ----------

        // Writes Your storage and the update key of this PC to the vault. Either may be null: it is then left out.
        public VaultSetupResult Save(OwnStorage storage, IUpdateKeyStore keys, IVaultSetupAsk ask)
        {
            VaultSetupResult res = new VaultSetupResult();
            if (storage == null) res.Lines.Add("Your storage is not set up on this PC, so no storage settings were saved.");
            else
            {
                string text = storage.Text();
                VaultSetupSecret have = null;
                try { have = Read(StorageName); }
                catch (VaultException ex) { if (!Refused(ex)) throw; }
                if (have != null && have.Value == text) res.Lines.Add("The storage settings in the vault are the same already.");
                else
                {
                    Put(StorageName, StorageType, text, null);
                    res.StorageDone = true;
                    res.Lines.Add("Saved the storage settings for " + storage.Account + ".");
                }
            }
            byte[] blob = keys.Load();
            string local = blob == null ? null : FingerprintOf(blob);
            if (blob == null) res.Lines.Add("This PC has no update key, so none was saved.");
            else if (local == null) res.Lines.Add("The update key on this PC could not be read, so it was not saved.");
            else
            {
                string vaultFp = null;
                bool known = true;
                try
                {
                    VaultSetupSecret have = Read(KeyName);
                    if (have != null) vaultFp = FingerprintOf(Convert64(have.Value)) ?? (have.Fingerprint.Length > 0 ? have.Fingerprint : "unknown");
                }
                catch (VaultException ex)
                {
                    // Without the right to read, whether the vault has a key is not known: ask before writing.
                    if (!Refused(ex)) throw;
                    known = false;
                }
                KeyChoice c = known ? SaveKey(vaultFp, local) : KeyChoice.Ask;
                if (c == KeyChoice.Ask && !ask.ReplaceVaultKey(known ? vaultFp : null, local))
                    res.Lines.Add("The update key in the vault was left as it is.");
                else if (c == KeyChoice.Leave) res.Lines.Add("The vault has this update key already (" + local + ").");
                else
                {
                    Put(KeyName, KeyType, Convert.ToBase64String(blob), local);
                    res.KeyDone = true;
                    res.Lines.Add("Saved the update key (" + local + ").");
                }
            }
            return res;
        }

        // ---------- get ----------

        // Takes Your storage and the update key from the vault. The storage settings are saved the way Your storage saves
        // them, in secrets. The key is saved with keys. A key on this PC is never replaced without a yes.
        public VaultSetupResult Get(ISecretStore secrets, IUpdateKeyStore keys, IVaultSetupAsk ask)
        {
            VaultSetupResult res = new VaultSetupResult();
            VaultSetupSecret st = null, ky = null;
            try { st = Read(StorageName); }
            catch (VaultException ex)
            {
                if (!Refused(ex)) throw;
                res.StorageDenied = true;
                res.Lines.Add("This account cannot read the storage settings (the vault answered " + ex.Status + ").");
            }
            try { ky = Read(KeyName); }
            catch (VaultException ex)
            {
                if (!Refused(ex)) throw;
                res.KeyDenied = true;
                res.Lines.Add(KeyDeniedText);
            }
            if (!res.StorageDenied) GetStorage(st, secrets, ask, res);
            if (!res.KeyDenied) GetKey(ky, keys, ask, res);
            return res;
        }

        void GetStorage(VaultSetupSecret st, ISecretStore secrets, IVaultSetupAsk ask, VaultSetupResult res)
        {
            if (st == null) { res.Lines.Add("The vault has no storage settings."); return; }
            OwnStorage vault = OwnStorage.Parse(st.Value);
            if (vault == null) { res.Lines.Add("The storage settings in the vault could not be read."); return; }
            OwnStorage local = OwnStorage.Load(secrets);
            KeyChoice c = GetStorage(local, vault);
            if (c == KeyChoice.Leave)
            {
                res.Lines.Add(local != null && local.Text() == vault.Text() ? "The storage settings are the same as in the vault." : "The storage settings on this PC run as long or longer, so they were kept.");
                return;
            }
            if (c == KeyChoice.Ask && !ask.UseVaultStorage(local, vault))
            {
                res.Lines.Add("The storage settings on this PC were kept.");
                return;
            }
            vault.Save(secrets);
            res.StorageDone = true;
            res.Lines.Add("Got the storage settings for " + vault.Account + ".");
        }

        void GetKey(VaultSetupSecret ky, IUpdateKeyStore keys, IVaultSetupAsk ask, VaultSetupResult res)
        {
            if (ky == null) { res.Lines.Add("The vault has no update key."); return; }
            byte[] blob = Convert64(ky.Value);
            string vaultFp = FingerprintOf(blob);
            if (vaultFp == null) { res.Lines.Add("The update key in the vault could not be read, so it was not used."); return; }
            string local = LocalFingerprint(keys);
            KeyChoice c = GetKey(local, vaultFp);
            if (c == KeyChoice.Leave) { res.Lines.Add("This PC has the same update key as the vault (" + vaultFp + "). Nothing to do."); return; }
            if (c == KeyChoice.Ask && !ask.ReplaceLocalKey(local, vaultFp))
            {
                res.Lines.Add("The update key on this PC was kept (" + local + "). The vault's key is " + vaultFp + ".");
                return;
            }
            keys.Save(blob);
            res.KeyDone = true;
            res.Lines.Add("Got the update key from the vault (" + vaultFp + ").");
        }
    }
}
