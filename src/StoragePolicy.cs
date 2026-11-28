namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Security;
    using System.Text;
    using System.Xml;

    // Blob service requests signed with the account key, as described in "Authorize with Shared Key". Set Container ACL
    // takes no SAS, so the stored access policies of a customer's container are set this way.
    public static class SharedKey
    {
        static Dictionary<string, string> Query(string url)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            int q = url.IndexOf('?');
            if (q < 0) return d;
            foreach (string p in url.Substring(q + 1).Split('&'))
            {
                if (p.Length == 0) continue;
                int e = p.IndexOf('=');
                string name = Uri.UnescapeDataString(e < 0 ? p : p.Substring(0, e)).ToLowerInvariant();
                string value = e < 0 ? "" : Uri.UnescapeDataString(p.Substring(e + 1));
                string have;
                d[name] = d.TryGetValue(name, out have) ? have + "," + value : value;
            }
            return d;
        }

        // The fields in the documented order for the Blob service, version 2015-02-21 and later: an empty Content-Length
        // when there is no body, and an empty Date because x-ms-date is sent.
        public static string StringToSign(WebCall c, string account)
        {
            int length = c.Body == null ? 0 : c.Body.Length;
            string type = c.Body != null ? (c.ContentType ?? "application/octet-stream") : "";
            StringBuilder b = new StringBuilder();
            b.Append(c.Method).Append('\n');
            b.Append('\n').Append('\n');
            b.Append(length > 0 ? length.ToString(CultureInfo.InvariantCulture) : "").Append('\n');
            b.Append('\n');
            b.Append(type).Append('\n');
            for (int i = 0; i < 6; i++) b.Append('\n');
            List<string> names = new List<string>();
            foreach (string k in c.Headers.Keys) if (k.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase)) names.Add(k.ToLowerInvariant());
            names.Sort(StringComparer.Ordinal);
            foreach (string n in names) b.Append(n).Append(':').Append(c.Headers[n].Trim()).Append('\n');
            b.Append('/').Append(account).Append(new Uri(c.Url).AbsolutePath);
            Dictionary<string, string> q = Query(c.Url);
            List<string> keys = new List<string>(q.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys) b.Append('\n').Append(k).Append(':').Append(q[k]);
            return b.ToString();
        }

        public static void Sign(WebCall c, string account, string key, DateTime nowUtc)
        {
            c.Headers["x-ms-date"] = nowUtc.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
            c.Headers["x-ms-version"] = SasLinks.Version;
            c.Headers["Authorization"] = "SharedKey " + account + ":" + SasLinks.Sign(StringToSign(c, account), key);
        }
    }

    // One stored access policy. Start and Expiry are kept as the service wrote them, so policies TrackVice did not make
    // are written back unchanged.
    public sealed class AccessPolicy
    {
        public string Id = "", Start = "", Expiry = "", Permission = "";
    }

    // Get and Set Container ACL. Set replaces every policy of the container, and leaves it private.
    public static class ContainerAcl
    {
        // At most five policies on a container.
        public const int Max = 5;
        public const int MaxXml = 65536;

        // A request signed with the account key goes only to the blob address of the account, for a container name.
        static string Base(OwnStorage own, string container)
        {
            StorageTrust.RequireEndpoint(own);
            string problem = BlobContainers.Problem(container);
            if (problem != null) throw new SetupException(problem);
            return own.BlobEndpoint.TrimEnd('/') + "/" + container;
        }

        static string Url(OwnStorage own, string container)
        {
            return Base(own, container) + "?restype=container&comp=acl";
        }

        public static string Xml(List<AccessPolicy> list)
        {
            StringBuilder b = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><SignedIdentifiers>");
            foreach (AccessPolicy p in list)
            {
                b.Append("<SignedIdentifier><Id>").Append(SecurityElement.Escape(p.Id)).Append("</Id><AccessPolicy>");
                if (p.Start.Length > 0) b.Append("<Start>").Append(SecurityElement.Escape(p.Start)).Append("</Start>");
                if (p.Expiry.Length > 0) b.Append("<Expiry>").Append(SecurityElement.Escape(p.Expiry)).Append("</Expiry>");
                if (p.Permission.Length > 0) b.Append("<Permission>").Append(SecurityElement.Escape(p.Permission)).Append("</Permission>");
                b.Append("</AccessPolicy></SignedIdentifier>");
            }
            return b.Append("</SignedIdentifiers>").ToString();
        }

        public static List<AccessPolicy> Parse(string xml)
        {
            List<AccessPolicy> l = new List<AccessPolicy>();
            string t = (xml ?? "").Trim().TrimStart('﻿');
            if (t.Length == 0) return l;
            // A container has at most five policies, so a long answer is not one. A document type is refused, so no
            // entity is expanded or fetched.
            if (t.Length > MaxXml) throw new XmlException("The access policies are too long.");
            XmlDocument d = new XmlDocument();
            d.XmlResolver = null;
            XmlReaderSettings set = new XmlReaderSettings();
            set.DtdProcessing = DtdProcessing.Prohibit;
            set.XmlResolver = null;
            using (XmlReader r = XmlReader.Create(new System.IO.StringReader(t), set)) d.Load(r);
            foreach (XmlNode n in d.SelectNodes("/SignedIdentifiers/SignedIdentifier"))
            {
                AccessPolicy p = new AccessPolicy();
                p.Id = Text(n, "Id");
                p.Start = Text(n, "AccessPolicy/Start");
                p.Expiry = Text(n, "AccessPolicy/Expiry");
                p.Permission = Text(n, "AccessPolicy/Permission");
                if (p.Id.Length > 0) l.Add(p);
            }
            return l;
        }

        static string Text(XmlNode n, string path)
        {
            XmlNode x = n.SelectSingleNode(path);
            return x == null ? "" : x.InnerText.Trim();
        }

        static SetupException Fail(WebReply r, OwnStorage own, string container, string doing)
        {
            string code = r.Header("x-ms-error-code") ?? "";
            string tail = " (HTTP " + r.Status + (code.Length > 0 ? ", " + code : "") + ").";
            if (r.Status == 404) return new SetupException("The container " + container + " is not in " + own.Account + tail, r.Status, code);
            if (r.Status == 403) return new SetupException("The storage account " + own.Account + " refused the account key" + tail + " If the key was rotated, set up Your storage again.", r.Status, code);
            return new SetupException("Could not " + doing + " of the container " + container + tail, r.Status, code);
        }

        public static List<AccessPolicy> Get(IWeb web, OwnStorage own, string container, DateTime nowUtc)
        {
            WebCall c = new WebCall();
            c.Url = Url(own, container);
            SharedKey.Sign(c, own.Account, own.Key, nowUtc);
            WebReply r = web.Send(c);
            if (r.Status != 200) throw Fail(r, own, container, "read the access policies");
            try { return Parse(r.Text); }
            catch (XmlException) { throw new SetupException("The access policies of the container " + container + " could not be read."); }
        }

        public static void Set(IWeb web, OwnStorage own, string container, List<AccessPolicy> list, DateTime nowUtc)
        {
            if (list.Count > Max) throw new SetupException("The container " + container + " would have " + list.Count + " access policies. Azure allows " + Max + ".");
            WebCall c = new WebCall();
            c.Method = "PUT";
            c.Url = Url(own, container);
            // No body removes every policy.
            if (list.Count > 0)
            {
                c.ContentType = "application/xml";
                c.Body = new UTF8Encoding(false).GetBytes(Xml(list));
            }
            SharedKey.Sign(c, own.Account, own.Key, nowUtc);
            WebReply r = web.Send(c);
            if (r.Status < 200 || r.Status > 299) throw Fail(r, own, container, "set the access policies");
        }

        // Deletes the container and everything in it. False when it was not there.
        public static bool Delete(IWeb web, OwnStorage own, string container, DateTime nowUtc)
        {
            WebCall c = new WebCall();
            c.Method = "DELETE";
            c.Url = Base(own, container) + "?restype=container";
            SharedKey.Sign(c, own.Account, own.Key, nowUtc);
            WebReply r = web.Send(c);
            if (r.Status == 202 || r.Status == 200) return true;
            if (r.Status == 404) return false;
            throw Fail(r, own, container, "delete everything");
        }
    }

    // A service SAS that takes its permissions, start and expiry from a stored access policy: the link carries si= and
    // leaves those out, so removing the policy stops every link signed with it at once.
    public static class PolicySas
    {
        public static string StringToSign(string resource, string identifier, string protocol, string version, string kind)
        {
            return string.Join("\n", new string[] {
                "", "", "", resource, identifier, "", protocol ?? "", version, kind, "", "", "", "", "", "", "" });
        }

        public static string Container(string blobEndpoint, string account, string container, string identifier, string key)
        {
            string sig = SasLinks.Sign(StringToSign("/blob/" + account + "/" + container, identifier, "https", SasLinks.Version, "c"), key);
            return blobEndpoint.TrimEnd('/') + "/" + container + "?si=" + Uri.EscapeDataString(identifier) + "&spr=https&sv=" + SasLinks.Version + "&sr=c&sig=" + Uri.EscapeDataString(sig);
        }
    }

    // The upload and viewer links of one customer of your storage.
    public sealed class CustomerLinks
    {
        public string Container = "", Upload = "", Viewer = "", UploadPolicy = "", ViewerPolicy = "";
        public DateTime ExpiresUtc;
    }

    // The two stored access policies TrackVice keeps on each customer's container: upload (Create, Write) for the devices
    // and viewer (Read, List) for this PC. New links get new names, upload-2 and viewer-2 and so on, because a link
    // follows the name of its policy: the old names are removed, so the old links stop.
    public static class CustomerAccess
    {
        public const string Upload = "upload", Viewer = "viewer";
        public const string UploadPerms = "cw", ViewerPerms = "rl";
        // Azure takes up to 30 seconds to put a new policy in force.
        public const int PolicyDelaySeconds = 30;

        // 1 for upload or viewer, n for upload-n or viewer-n, 0 for a policy TrackVice did not make.
        public static int Generation(string id)
        {
            foreach (string b in new string[] { Upload, Viewer })
            {
                if (id == b) return 1;
                int n;
                if (id.StartsWith(b + "-", StringComparison.Ordinal) && int.TryParse(id.Substring(b.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 1) return n;
            }
            return 0;
        }

        public static string Name(string baseName, int generation)
        {
            return generation <= 1 ? baseName : baseName + "-" + generation.ToString(CultureInfo.InvariantCulture);
        }

        // The policies of the container with TrackVice's replaced by a new pair. Others are kept as they are.
        public static List<AccessPolicy> Replace(List<AccessPolicy> have, DateTime nowUtc, int days, out string upload, out string viewer)
        {
            List<AccessPolicy> l = new List<AccessPolicy>();
            int gen = 0;
            foreach (AccessPolicy p in have)
            {
                int g = Generation(p.Id);
                if (g > 0) gen = Math.Max(gen, g);
                else l.Add(p);
            }
            gen++;
            upload = Name(Upload, gen);
            viewer = Name(Viewer, gen);
            string st = SasLinks.Time(nowUtc.AddMinutes(-15)), se = SasLinks.Time(nowUtc.AddDays(days));
            l.Add(Policy(upload, st, se, UploadPerms));
            l.Add(Policy(viewer, st, se, ViewerPerms));
            return l;
        }

        static AccessPolicy Policy(string id, string start, string expiry, string perms)
        {
            AccessPolicy p = new AccessPolicy();
            p.Id = id;
            p.Start = start;
            p.Expiry = expiry;
            p.Permission = perms;
            return p;
        }

        // Sets a new pair of policies on the container and signs the two links with them. A container made a moment ago
        // has no policies, so they are not read first.
        public static CustomerLinks Grant(IWeb web, OwnStorage own, string container, bool justMade, DateTime nowUtc, int days)
        {
            if (string.IsNullOrEmpty(own.Key)) throw new SetupException("The account key of your storage is needed to sign the links.");
            List<AccessPolicy> have = justMade ? new List<AccessPolicy>() : ContainerAcl.Get(web, own, container, nowUtc);
            string up, view;
            List<AccessPolicy> next = Replace(have, nowUtc, days, out up, out view);
            if (next.Count > ContainerAcl.Max)
                throw new SetupException("The container " + container + " has " + (next.Count - 2) + " access policies that TrackVice did not make. Azure allows " + ContainerAcl.Max + " in all, and TrackVice needs 2. Remove some in the Azure portal.");
            ContainerAcl.Set(web, own, container, next, nowUtc);
            CustomerLinks l = new CustomerLinks();
            l.Container = container;
            l.UploadPolicy = up;
            l.ViewerPolicy = view;
            l.Upload = PolicySas.Container(own.BlobEndpoint, own.Account, container, up, own.Key);
            l.Viewer = PolicySas.Container(own.BlobEndpoint, own.Account, container, view, own.Key);
            l.ExpiresUtc = nowUtc.AddDays(days);
            return l;
        }

        // Removes TrackVice's policies, so every link of the customer stops, and deletes the container only when asked.
        public static List<string> Remove(IWeb web, OwnStorage own, string container, bool deleteContainer, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(own.Key)) throw new SetupException("The account key of your storage is needed to remove the links.");
            List<string> done = new List<string>();
            List<AccessPolicy> have = null;
            try { have = ContainerAcl.Get(web, own, container, nowUtc); }
            catch (SetupException ex) { if (ex.Status != 404) throw; }
            if (have == null) done.Add("The container " + container + " is not in " + own.Account + ", so no link can reach it");
            else
            {
                List<AccessPolicy> keep = new List<AccessPolicy>();
                List<string> gone = new List<string>();
                foreach (AccessPolicy p in have)
                {
                    if (Generation(p.Id) > 0) gone.Add(p.Id);
                    else keep.Add(p);
                }
                if (gone.Count > 0)
                {
                    ContainerAcl.Set(web, own, container, keep, nowUtc);
                    done.Add("Removed the access policies " + string.Join(" and ", gone.ToArray()) + " of " + container + ". Its links stop within 30 seconds");
                }
                else done.Add("The container " + container + " had no access policies of TrackVice");
            }
            if (deleteContainer && have != null)
            {
                bool deleted = ContainerDelete(web, own, container, nowUtc);
                done.Add(deleted ? "Deleted the container " + container + " with all its data" : "The container " + container + " was deleted already");
            }
            return done;
        }

        static bool ContainerDelete(IWeb web, OwnStorage own, string container, DateTime nowUtc)
        {
            return ContainerAcl.Delete(web, own, container, nowUtc);
        }

        // The tv- container in your storage that a link points at, or null for a link elsewhere, such as the customer's own Azure.
        public static string ContainerOf(OwnStorage own, string link)
        {
            if (own == null || string.IsNullOrEmpty(link)) return null;
            Uri u, ep;
            if (!Uri.TryCreate(link, UriKind.Absolute, out u) || !Uri.TryCreate(own.BlobEndpoint, UriKind.Absolute, out ep)) return null;
            if (!string.Equals(u.Host, ep.Host, StringComparison.OrdinalIgnoreCase)) return null;
            string path = u.AbsolutePath.Trim('/');
            if (path.IndexOf('/') >= 0 || !path.StartsWith(StorageSecurity.Prefix, StringComparison.Ordinal)) return null;
            return BlobContainers.Problem(path) == null ? path : null;
        }

        // True when the link names a stored access policy.
        public static bool UsesPolicy(string link)
        {
            int q = (link ?? "").IndexOf('?');
            if (q < 0) return false;
            foreach (string p in link.Substring(q + 1).Split('&')) if (p.StartsWith("si=", StringComparison.Ordinal)) return true;
            return false;
        }
    }

    // New links for a customer of your storage: new policies, and a new customer code with the new upload link.
    public sealed class CustomerRenewal
    {
        public CustomerCode Code;
        public CustomerLinks Links;
        // True when the links before were signed without a policy, so they keep working until they expire.
        public bool OldLinksWithoutPolicy;

        public static CustomerRenewal Make(IWeb web, OwnStorage own, string code, DateTime nowUtc)
        {
            CustomerCode c = CustomerCode.Parse(code);
            string container = CustomerAccess.ContainerOf(own, c.Upload);
            if (container == null) throw new SetupException("This customer's data is not in your storage " + own.Account + ", so TrackVice cannot make new links for it.");
            CustomerRenewal r = new CustomerRenewal();
            r.OldLinksWithoutPolicy = !CustomerAccess.UsesPolicy(c.Upload);
            r.Links = CustomerAccess.Grant(web, own, container, false, nowUtc, AddCustomerRun.LinkDays);
            c.Upload = r.Links.Upload;
            string p = c.Problem();
            if (p != null) throw new SetupException(p);
            r.Code = c;
            return r;
        }
    }
}
