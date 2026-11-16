namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;

    // Your storage, a published version, Add customer and the platform script together, against a made-up blob service
    // that checks the SAS links as Azure does: what a device fetches must be exactly what was published.
    public static class CustomerFlowTests
    {
        const string Account = "contosotrackvice";
        const string Endpoint = "https://contosotrackvice.blob.core.windows.net/";
        // A made-up 64-byte account key, the bytes 64 to 127.
        static readonly string Key = Convert.ToBase64String(Bytes(64, 64));
        const string Sub = "4a1b2c3d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
        static readonly DateTime Now = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

        static byte[] Bytes(int from, int n)
        {
            byte[] b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(from + i);
            return b;
        }

        static Dictionary<string, string> Query(string url)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            int q = url.IndexOf('?');
            if (q < 0) return d;
            foreach (string p in url.Substring(q + 1).Split('&'))
            {
                int e = p.IndexOf('=');
                if (e > 0) d[p.Substring(0, e)] = Uri.UnescapeDataString(p.Substring(e + 1));
            }
            return d;
        }

        // The blob service of one storage account. Service SAS and account SAS signatures are checked with the key.
        sealed class BlobService : IWeb
        {
            public readonly Dictionary<string, byte[]> Blobs = new Dictionary<string, byte[]>();
            public readonly HashSet<string> Containers = new HashSet<string>();
            public readonly List<WebCall> Calls = new List<WebCall>();
            public readonly List<string> Order = new List<string>();
            // The stored access policies of each container, set with Set Container ACL.
            public readonly Dictionary<string, List<AccessPolicy>> Policies = new Dictionary<string, List<AccessPolicy>>();

            // 403 unless the link is signed for this container (or is an account SAS) and has one of the letters in need.
            string Allowed(string url, string container, string need, bool accountSas)
            {
                Dictionary<string, string> q = Query(url);
                string sp, sig, st, se, si;
                string expect;
                if (q.TryGetValue("si", out si) && !accountSas)
                {
                    // A link with a policy: its permissions and times come from the policy, and are not in the link.
                    if (q.ContainsKey("sp") || q.ContainsKey("se") || q.ContainsKey("st")) return "a field both in the link and the policy";
                    List<AccessPolicy> pol;
                    AccessPolicy found = null;
                    if (Policies.TryGetValue(container, out pol)) foreach (AccessPolicy p in pol) if (p.Id == si) found = p;
                    if (found == null) return "no such policy";
                    if (!q.ContainsKey("sr") || q["sr"] != "c" || !q.TryGetValue("sig", out sig)) return "not a container link";
                    expect = SasLinks.Sign(string.Join("\n", new string[] { "", "", "", "/blob/" + Account + "/" + container, si, "", q["spr"], q["sv"], "c", "", "", "", "", "", "", "" }), Key);
                    if (expect != sig) return "signature does not match";
                    sp = found.Permission;
                    st = found.Start;
                    se = found.Expiry;
                }
                else
                {
                if (!q.TryGetValue("sp", out sp) || !q.TryGetValue("sig", out sig) || !q.TryGetValue("se", out se)) return "no SAS";
                q.TryGetValue("st", out st);
                if (accountSas)
                {
                    if (!q.ContainsKey("ss") || !q["ss"].Contains("b") || !q.ContainsKey("srt") || !q["srt"].Contains("c")) return "not an account SAS for containers";
                    expect = SasLinks.Sign(AccountSas.StringToSign(Account, sp, q["ss"], q["srt"], st, se, q["sv"]), Key);
                }
                else
                {
                    if (q.ContainsKey("ss")) return "an account SAS where a container link is expected";
                    if (!q.ContainsKey("sr") || q["sr"] != "c") return "not a container link";
                    expect = SasLinks.Sign(SasLinks.StringToSign(sp, st, se, "/blob/" + Account + "/" + container, "https", q["sv"], "c"), Key);
                }
                if (expect != sig) return "signature does not match";
                }
                DateTime s, e;
                DateTime.TryParse(se, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out e);
                if (st != null && DateTime.TryParse(st, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out s) && s > Now) return "not valid yet";
                if (e < Now) return "expired";
                foreach (char c in need) if (sp.IndexOf(c) >= 0) return null;
                return "permission " + sp + " lacks " + need;
            }

            static void Split(string url, out string container, out string blob)
            {
                Uri u = new Uri(url);
                string path = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'));
                int s = path.IndexOf('/');
                container = s < 0 ? path : path.Substring(0, s);
                blob = s < 0 ? "" : path.Substring(s + 1);
            }

            public WebReply Send(WebCall c)
            {
                Calls.Add(c);
                if (!c.Url.StartsWith(Endpoint)) return new WebReply(404, "");
                string container, blob;
                Split(c.Url, out container, out blob);
                Dictionary<string, string> q = Query(c.Url);
                if (q.ContainsKey("restype") && q["restype"] == "container" && c.Method == "PUT" && !q.ContainsKey("comp"))
                {
                    string why = Allowed(c.Url, container, BlobContainers.ContainerPerms, true);
                    if (why != null) return Refused(why);
                    if (!Containers.Add(container)) return new WebReply(409, "");
                    return new WebReply(201, "");
                }
                if (!Containers.Contains(container)) return new WebReply(404, "");
                if (q.ContainsKey("comp") && q["comp"] == "acl")
                {
                    string auth;
                    if (!c.Headers.TryGetValue("Authorization", out auth) || !c.Headers.ContainsKey("x-ms-date")) return Refused("no Shared Key");
                    if (auth != "SharedKey " + Account + ":" + SasLinks.Sign(SharedKey.StringToSign(c, Account), Key)) return Refused("Shared Key signature does not match");
                    if (c.Method == "PUT") { Policies[container] = ContainerAcl.Parse(c.Text); return new WebReply(200, ""); }
                    List<AccessPolicy> have;
                    return new WebReply(200, ContainerAcl.Xml(Policies.TryGetValue(container, out have) ? have : new List<AccessPolicy>()));
                }
                string k = container + "/" + blob;
                if (c.Method == "PUT")
                {
                    string why = Allowed(c.Url, container, Blobs.ContainsKey(k) ? "w" : "cw", false);
                    if (why != null) return Refused(why);
                    Blobs[k] = c.Body;
                    Order.Add(k);
                    return new WebReply(201, "");
                }
                byte[] data = Get(c.Url);
                if (data == null) return new WebReply(404, "");
                return new WebReply(200, Encoding.UTF8.GetString(data));
            }

            static WebReply Refused(string why)
            {
                WebReply r = new WebReply(403, why);
                r.Headers["x-ms-error-code"] = "AuthorizationPermissionMismatch";
                return r;
            }

            // What a GET with this link returns, as bytes. Throws when the link may not read.
            public byte[] Get(string url)
            {
                string container, blob;
                Split(url, out container, out blob);
                string why = Allowed(url, container, "r", false);
                if (why != null) throw new InvalidOperationException("403: " + why);
                byte[] b;
                return Blobs.TryGetValue(container + "/" + blob, out b) ? b : null;
            }

            public bool CanPut(string url)
            {
                string container, blob;
                Split(url, out container, out blob);
                return Allowed(url, container, "cw", false) == null;
            }
        }

        // The agent's self-update reads the agent container through the link in the code, as a device does.
        sealed class DeviceStore : ISessionStore
        {
            readonly BlobService svc;
            readonly string link;

            public DeviceStore(BlobService svc, string link)
            {
                this.svc = svc;
                this.link = link;
            }

            public string Title
            {
                get { return link.Substring(0, link.IndexOf('?')); }
            }

            public void Put(string name, byte[] data, Dictionary<string, string> meta)
            {
                throw new StoreException("read only");
            }

            public List<StoreEntry> List(string prefix)
            {
                throw new StoreException("read only");
            }

            public byte[] Get(string name)
            {
                byte[] b = svc.Get(BlobLink(link, name));
                if (b == null) throw new StoreException(name + " is not there.");
                return b;
            }
        }

        // Get-BlobLink of agent/Install-TrackViceAgent.ps1: the blob name goes before the ? of the container link.
        static string BlobLink(string container, string blob)
        {
            string[] parts = container.Split(new char[] { '?' }, 2);
            string link = parts[0].TrimEnd('/') + "/" + blob;
            if (parts.Length > 1) link += "?" + parts[1];
            return link;
        }

        // The JSON in $Customer, read as the script reads it.
        static Dictionary<string, object> Decode(string script)
        {
            Match m = Regex.Match(script, @"^\$Customer = '([^']*)'", RegexOptions.Multiline);
            string text = m.Groups[1].Value.Trim();
            T.Check(text.StartsWith("tv1:"), "Flow: the script holds a customer code");
            text = text.Substring(4).Replace('-', '+').Replace('_', '/');
            while (text.Length % 4 != 0) text += "=";
            return ImeJson.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(text))) as Dictionary<string, object>;
        }

        // The check the script makes: the payload from the fields of agent/latest.json, verified with the key from the code.
        static bool ScriptAccepts(byte[] latest, string key)
        {
            string raw = Encoding.UTF8.GetString(latest);
            Dictionary<string, object> m = ImeJson.Parse(raw) as Dictionary<string, object>;
            string version = Http.Str(m, "version"), blob = Http.Str(m, "blob");
            string sha = (Http.Str(m, "sha256") ?? "").ToUpperInvariant();
            long size = Convert.ToInt64(m["size"], CultureInfo.InvariantCulture);
            string published = Regex.Match(raw, "\"published\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
            if (blob != "agent/TrackVice-" + version + ".exe") return false;
            string payload = string.Join("\n", new string[] { "TrackVice agent update", "version=" + version, "sha256=" + sha, "size=" + size.ToString(CultureInfo.InvariantCulture), "blob=" + blob, "published=" + published, "" });
            try
            {
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportCspBlob(Convert.FromBase64String(key));
                    return rsa.VerifyData(Encoding.UTF8.GetBytes(payload), "SHA256", Convert.FromBase64String(Http.Str(m, "signature")));
                }
            }
            catch (Exception) { return false; }
        }

        static string Sha(byte[] b)
        {
            return AgentUpdate.Sha256Hex(b).ToUpperInvariant();
        }

        public static void Run()
        {
            using (RSACryptoServiceProvider signing = UpdateKeys.Create())
            using (RSACryptoServiceProvider other = UpdateKeys.Create())
            {
                EndToEnd(signing, other);
                OtherKeyOnThisPc(signing, other);
                Codes(signing);
            }
            ScriptShape();
        }

        static BlobService SetUp(MemoryKeyStore keys, MemorySecrets secrets, out OwnStorage own, byte[] first)
        {
            BlobService svc = new BlobService();
            // Azure Resource Manager: the container trackvice-agent and the account key.
            Arm arm = new Arm(new ArmFake(svc), delegate { return "arm-token"; });
            arm.Pause = delegate { };
            OwnStorageRun run = new OwnStorageRun();
            AzureSubscription s = new AzureSubscription();
            s.Id = Sub;
            s.Name = "Contoso Production";
            run.Subscription = s;
            run.NewAccount = false;
            AzureAccount a = new AzureAccount();
            a.Id = Arm.AccountId(Sub, "rg-trackvice", Account);
            a.Name = Account;
            a.BlobEndpoint = Endpoint;
            run.Account = a;
            run.Run(arm, svc, keys, secrets, first, "1.1.3", Now, delegate { });
            own = OwnStorage.Load(secrets);
            return svc;
        }

        sealed class ArmFake : IWeb
        {
            readonly BlobService svc;

            public ArmFake(BlobService svc)
            {
                this.svc = svc;
            }

            public WebReply Send(WebCall c)
            {
                if (c.Url.Contains("/blobServices/default/containers/" + Arm.AgentFiles)) return new WebReply(svc.Containers.Add(Arm.AgentFiles) ? 201 : 200, "{}");
                if (c.Url.Contains("/listKeys")) return new WebReply(200, "{\"keys\":[{\"keyName\":\"key1\",\"permissions\":\"FULL\",\"value\":\"" + Key + "\"}]}");
                return svc.Send(c);
            }
        }

        static void EndToEnd(RSACryptoServiceProvider signing, RSACryptoServiceProvider other)
        {
            MemoryKeyStore keys = new MemoryKeyStore();
            keys.Save(signing.ExportCspBlob(true));
            MemorySecrets secrets = new MemorySecrets();
            byte[] first = Encoding.ASCII.GetBytes("MZ made-up TrackVice 1.1.3");
            OwnStorage own;
            BlobService svc = SetUp(keys, secrets, out own, first);
            T.Check(own != null && own.Key == Key, "Flow: your storage is kept with the account key");
            T.Eq("1.1.3", own.PublishedVersion, "Flow: the running version is published at setup");

            // A new version, published as the prompt does.
            byte[] exe = new byte[300000];
            new Random(7).NextBytes(exe);
            int callsBefore = svc.Calls.Count;
            UpdateManifest m = OwnPublish.Publish(svc, own, exe, "1.1.4", signing, Now);
            List<WebCall> puts = svc.Calls.GetRange(callsBefore, svc.Calls.Count - callsBefore);
            T.Eq(2, puts.Count, "Flow: publishing is two uploads");
            T.Check(puts[0].Url.Contains("/" + Arm.AgentFiles + "/agent/TrackVice-1.1.4.exe?") && puts[1].Url.Contains("/" + Arm.AgentFiles + "/agent/latest.json?"), "Flow: the exe is uploaded before latest.json");
            foreach (WebCall c in puts)
            {
                T.Eq("cw", Query(c.Url)["sp"], "Flow: publishing uses the Create and Write link");
                T.Check(c.Url.EndsWith(own.AgentWrite.Substring(own.AgentWrite.IndexOf('?'))), "Flow: publishing uses the kept publish link");
            }

            // Add customer, the default path.
            CustomerBook book = CustomerBook.Demo();
            AddCustomerRun r = new AddCustomerRun();
            r.Customer = "Northwind Traders";
            r.Alert = "https://northwind.example/workflows/alerts";
            T.Eq(null, r.Problem(book), "Flow: the customer can be added");
            string pub = UpdateKeys.PublicKey(signing);
            T.Eq(null, OwnPublish.KeyProblem(svc, own, pub), "Flow: the key of this PC signed what is published");
            r.MakeStorage(svc, own, Now);
            T.Check(svc.Containers.Contains("tv-northwind-traders"), "Flow: the customer's container is made");
            WebCall made = null;
            foreach (WebCall c in svc.Calls) if (c.Url.Contains("restype=container") && !c.Url.Contains("comp=acl")) made = c;
            Dictionary<string, string> aq = Query(made.Url);
            T.Eq(BlobContainers.ContainerPerms, aq["sp"], "Flow: the account SAS has only the permission to make a container");
            T.Eq("b", aq["ss"], "Flow: the account SAS is for the Blob service only");
            T.Eq("c", aq["srt"], "Flow: the account SAS is for containers only");
            T.Eq("https", aq["spr"], "Flow: the account SAS is for https only");
            DateTime ast = DateTime.Parse(aq["st"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            DateTime ase = DateTime.Parse(aq["se"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            T.Check(ase - ast <= TimeSpan.FromHours(2) && ase <= Now.AddHours(1), "Flow: the account SAS is valid for a short time");
            r.MakeCode(own, pub);
            string code = r.Code.ToString();
            string script = CustomerScript.For(code);

            // What the device gets.
            Dictionary<string, object> d = Decode(script);
            string a = Http.Str(d, "a"), u = Http.Str(d, "u"), k = Http.Str(d, "k");
            T.Eq(own.AgentRead, a, "Flow: the code's agent link is the kept read link");
            Dictionary<string, string> qa = Query(a), qu = Query(u), qv = Query(r.Viewer);
            T.Check(a.StartsWith(Endpoint + Arm.AgentFiles + "?") && qa["sp"] == "r" && qa["sr"] == "c", "Flow: the agent link is the container link with Read only");
            T.Check(u.StartsWith(Endpoint + "tv-northwind-traders?") && qu["si"] == "upload" && qu["sr"] == "c" && qu["spr"] == "https", "Flow: the upload link uses the upload policy on the customer's container");
            T.Check(r.Viewer.StartsWith(Endpoint + "tv-northwind-traders?") && qv["si"] == "viewer" && qv["sr"] == "c", "Flow: the viewer link uses the viewer policy on the customer's container");
            T.Check(svc.CanPut(BlobLink(u, "sessions/x.json.gz")), "Flow: the upload link can upload to the customer's container");
            T.Check(!svc.CanPut(BlobLink(r.Viewer, "sessions/x.json.gz")), "Flow: the viewer link cannot upload");
            T.Eq(pub, k, "Flow: the code holds the public update key");
            string writeSig = Query(own.AgentWrite)["sig"];
            foreach (string s in new string[] { code, script })
            {
                T.Check(!s.Contains(Key) && !s.Contains(Uri.EscapeDataString(Key)), "Flow: the account key is not in the code or the script");
                T.Check(!s.Contains(writeSig) && !s.Contains(Uri.EscapeDataString(writeSig)), "Flow: the publish link is not in the code or the script");
            }
            T.Check(!script.Contains(Query(r.Viewer)["sig"]) && !script.Contains(Uri.EscapeDataString(Query(r.Viewer)["sig"])), "Flow: the viewer link is not in the script");
            T.Check(!svc.CanPut(BlobLink(a, "agent/latest.json")), "Flow: a device cannot write to the agent container");
            T.Check(!svc.CanPut(BlobLink(u, "agent/latest.json").Replace("/tv-northwind-traders/", "/" + Arm.AgentFiles + "/")), "Flow: the upload link cannot write to the agent container");

            // The script's downloads, by the links it builds.
            string latestLink = BlobLink(a, "agent/latest.json");
            T.Eq(Endpoint + Arm.AgentFiles + "/agent/latest.json" + a.Substring(a.IndexOf('?')), latestLink, "Flow: the blob name goes before the ? of the container link");
            byte[] latest = svc.Get(latestLink);
            T.Check(latest != null && Encoding.UTF8.GetString(latest) == Encoding.UTF8.GetString(m.ToJson()), "Flow: the script reads the latest.json that was published");
            T.Check(ScriptAccepts(latest, k), "Flow: the script accepts the signature with the key in the code");
            UpdateManifest got = UpdateManifest.Parse(latest);
            byte[] dl = svc.Get(BlobLink(a, got.Blob));
            T.Check(dl != null && dl.Length == exe.Length && Sha(dl) == Sha(exe), "Flow: the script downloads exactly the published exe");
            T.Eq(got.Sha256.ToUpperInvariant(), Sha(dl), "Flow: its SHA-256 is the signed one");
            T.Eq(got.Size, (long)dl.Length, "Flow: its size is the signed one");

            // /agent-install /customer, and the self-update the installed agent then does.
            AgentOptions o = new AgentOptions();
            CustomerInstall.Apply(o, code);
            T.Eq(a, o.UpdateLink, "Flow: the agent updates itself from the agent container");
            T.Eq(u, o.Upload, "Flow: the agent uploads with the upload link");
            string stage = Path.Combine(Path.GetTempPath(), "tv-flow-" + Guid.NewGuid().ToString("N"));
            try
            {
                UpdateCheck chk = AgentUpdate.Check(new DeviceStore(svc, o.UpdateLink), o.UpdateKey, "1.1.3", stage);
                T.Eq(UpdateDecision.Newer, chk.Decision, "Flow: the installed agent sees the new version");
                T.Check(chk.Path != null && Sha(File.ReadAllBytes(chk.Path)) == Sha(exe), "Flow: the agent stages exactly the published exe");

                // A manifest signed with another key, put there by someone who can write to the container.
                UpdateManifest forged = UpdateManifest.Parse(latest);
                forged.Sign(other);
                svc.Blobs[Arm.AgentFiles + "/" + UpdateManifest.Name] = forged.ToJson();
                T.Check(!ScriptAccepts(svc.Get(latestLink), k), "Flow: the script refuses latest.json signed with another key");
                UpdateCheck bad = AgentUpdate.Check(new DeviceStore(svc, o.UpdateLink), o.UpdateKey, "1.1.3", stage);
                T.Eq(UpdateDecision.Invalid, bad.Decision, "Flow: the self-update refuses latest.json signed with another key");
                T.Check(bad.Path == null, "Flow: nothing is staged from it");

                // A changed exe under a correctly signed manifest.
                svc.Blobs[Arm.AgentFiles + "/" + UpdateManifest.Name] = latest;
                byte[] changed = (byte[])exe.Clone();
                changed[100] ^= 0xFF;
                svc.Blobs[Arm.AgentFiles + "/" + got.Blob] = changed;
                T.Check(Sha(svc.Get(BlobLink(a, got.Blob))) != got.Sha256.ToUpperInvariant(), "Flow: the script sees a changed exe by its SHA-256");
                T.Eq(UpdateDecision.Invalid, AgentUpdate.Check(new DeviceStore(svc, o.UpdateLink), o.UpdateKey, "1.1.3", stage).Decision, "Flow: the self-update refuses a changed exe");
            }
            finally { try { Directory.Delete(stage, true); } catch (Exception) { } }

            // A publish that fails at the exe leaves the published version as it was.
            svc.Blobs[Arm.AgentFiles + "/" + got.Blob] = exe;
            OwnStorage broken = OwnStorage.Parse(own.Text());
            broken.AgentWrite = own.AgentRead;
            bool threw = false;
            try { OwnPublish.Publish(svc, broken, exe, "1.1.5", signing, Now); }
            catch (Exception) { threw = true; }
            T.Check(threw, "Flow: a publish with a link that cannot write fails");
            T.Eq(Encoding.UTF8.GetString(latest), Encoding.UTF8.GetString(svc.Blobs[Arm.AgentFiles + "/" + UpdateManifest.Name]), "Flow: a failed publish leaves latest.json as it was");
            T.Eq("1.1.4", broken.PublishedVersion, "Flow: a failed publish keeps the published version");

            // Another customer whose name gives the same container is refused.
            book.Add(Customer("Northwind Traders"));
            AddCustomerRun r2 = new AddCustomerRun();
            r2.Customer = "Northwind-Traders";
            T.Check(r2.Problem(book) != null, "Flow: two customers cannot share one container");
            r2.OwnAzure = true;
            T.Eq(null, r2.Problem(book), "Flow: with their own Azure the container name does not matter");
            r2.Customer = "Northwind Traders";
            r2.OwnAzure = false;
            T.Eq(null, r2.Problem(book), "Flow: the same customer again uses its container");
        }

        static Customer Customer(string name)
        {
            Customer c = new Customer();
            c.Name = name;
            return c;
        }

        // A PC with another update key must neither publish over the version nor make codes the devices would refuse.
        static void OtherKeyOnThisPc(RSACryptoServiceProvider signing, RSACryptoServiceProvider other)
        {
            MemoryKeyStore keys = new MemoryKeyStore();
            keys.Save(signing.ExportCspBlob(true));
            OwnStorage own;
            BlobService svc = SetUp(keys, new MemorySecrets(), out own, Encoding.ASCII.GetBytes("MZ made-up TrackVice 1.1.3"));
            string otherKey = UpdateKeys.PublicKey(other);
            T.Check(OwnPublish.KeyProblem(svc, own, otherKey) != null, "Flow: another key on this PC is found");
            T.Check(OwnPublish.KeyProblem(svc, own, "") != null, "Flow: no key on this PC is found");
            T.Eq(null, OwnPublish.KeyProblem(svc, own, UpdateKeys.PublicKey(signing)), "Flow: the same key is fine");

            MemoryKeyStore second = new MemoryKeyStore();
            second.Save(other.ExportCspBlob(true));
            OwnStorage again;
            BlobService svc2 = svc;
            OwnStorageRun run = new OwnStorageRun();
            AzureSubscription s = new AzureSubscription();
            s.Id = Sub;
            s.Name = "Contoso Production";
            run.Subscription = s;
            run.NewAccount = false;
            AzureAccount a = new AzureAccount();
            a.Id = Arm.AccountId(Sub, "rg-trackvice", Account);
            a.Name = Account;
            a.BlobEndpoint = Endpoint;
            run.Account = a;
            Arm arm = new Arm(new ArmFake(svc2), delegate { return "arm-token"; });
            arm.Pause = delegate { };
            MemorySecrets sec2 = new MemorySecrets();
            run.Run(arm, svc2, second, sec2, Encoding.ASCII.GetBytes("MZ made-up TrackVice 1.1.4"), "1.1.4", Now, delegate { });
            again = OwnStorage.Load(sec2);
            T.Check(!run.PublishedNow && again.PublishedVersion == "1.1.3", "Flow: set up on a second PC does not publish over the version");
            T.Check(string.Join("\n", run.Done.ToArray()).Contains("another update key"), "Flow: set up on a second PC says its key is not the one that signed");
            T.Check(ScriptAccepts(svc2.Get(BlobLink(again.AgentRead, UpdateManifest.Name)), UpdateKeys.PublicKey(signing)), "Flow: the published version still has the first signature");
        }

        static void Codes(RSACryptoServiceProvider signing)
        {
            CustomerCode c = new CustomerCode();
            c.Name = "Contoso";
            c.Upload = Endpoint + "tv-contoso?sp=cw&sig=x";
            c.Agent = Endpoint + Arm.AgentFiles + "?sp=r&sig=y";
            c.Key = Convert.ToBase64String(signing.ExportCspBlob(true));
            T.Check(c.Problem() != null, "Flow: a code that holds the private key is refused");
            foreach (string bad in new string[] { "AAAA", Convert.ToBase64String(Bytes(0, 148)) })
            {
                c.Key = bad;
                bool refused = false;
                try { CustomerInstall.Apply(new AgentOptions(), c.ToString()); }
                catch (ArgumentException) { refused = true; }
                T.Check(refused, "Flow: /customer refuses a code with a bad key");
            }
            using (RSACryptoServiceProvider small = new RSACryptoServiceProvider(1024))
            {
                c.Key = UpdateKeys.PublicKey(small);
                T.Check(c.Problem() != null, "Flow: a code with a short key is refused");
            }

            // A real-size code, with 2-year links and a 3072-bit key, on the command line of /agent-install.
            c.Key = UpdateKeys.PublicKey(signing);
            c.Upload = SasLinks.Container(Endpoint, Account, "tv-" + new string('n', 60), "cw", Now, Now.AddDays(730), Key);
            c.Agent = SasLinks.Container(Endpoint, Account, Arm.AgentFiles, "r", Now, Now.AddDays(1826), Key);
            c.Alert = "https://prod-00.westeurope.logic.azure.com:443/workflows/" + new string('0', 32) + "/triggers/manual/paths/invoke?api-version=2016-06-01&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=" + new string('s', 43);
            string code = c.ToString();
            string line = "\"C:\\Windows\\TEMP\\TrackVice-" + Guid.NewGuid().ToString("N") + "\\TrackVice.exe\" /agent-install /customer \"" + code + "\"";
            T.Check(code.Length < 4000 && line.Length < 32767, "Flow: a full code fits on a command line (" + code.Length + " characters)");
            T.Check(Regex.IsMatch(code, "^tv1:[A-Za-z0-9_-]+$"), "Flow: a code needs no quoting inside the quotes");
        }

        // The parts of the script the flow above copies.
        static void ScriptShape()
        {
            string s = AgentScriptTemplate.Text;
            T.Check(s.Contains("$parts = $Container.Split([char[]]'?', 2)") && s.Contains("$link = $parts[0].TrimEnd('/') + '/' + $Blob") && s.Contains("$link += '?' + $parts[1]"), "Flow: Get-BlobLink is the one copied here");
            T.Check(s.Contains("Get-BlobLink -Container $code.a -Blob 'agent/latest.json'") && s.Contains("Get-BlobLink -Container $code.a -Blob $blob"), "Flow: the script downloads both from the agent link");
            T.Check(s.Contains("-UseBasicParsing") && s.Contains("[Net.SecurityProtocolType]::Tls12"), "Flow: the script works in Windows PowerShell 5.1");
            int verify = s.IndexOf("if (-not $valid)"), download = s.IndexOf("-Blob $blob"), hash = s.IndexOf("$hash -ne $sha256"), run = s.IndexOf("Start-Process");
            T.Check(verify > 0 && verify < download && download < hash && hash < run, "Flow: the script checks the signature before the download and the hash before it runs the exe");
            T.Check(s.Contains("[IO.Directory]::CreateDirectory($work, $acl)") && s.Contains("SetAccessRuleProtection($true, $false)"), "Flow: the exe is kept in a folder only SYSTEM and administrators can change");
            T.Check(!Regex.IsMatch(s, @"\?\?|\?\.|-Parallel|ConvertFrom-Json\s+-AsHashtable|-Depth|ForEach-Object\s+-Parallel|&&|\|\|"), "Flow: no PowerShell 7 syntax");
        }
    }
}
