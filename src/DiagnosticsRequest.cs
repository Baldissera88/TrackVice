namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Security.Cryptography;
    using System.Text;

    // ---------- the signed request ----------

    // agent/requests/<session id>.json: a request from the viewer to one device with the agent, to collect diagnostics.
    // It is signed with the same update key as agent/latest.json, so a device only follows requests that were made on a
    // PC that holds the private key, whoever can write to the container.
    //
    // What keeps a request from being forged or used again:
    // - every field is in the signed text, and the text starts with its own title, so a signed latest.json is not a request;
    // - every field has one allowed form (Problem), so the signed text can be read in one way only, and nothing is signed
    //   or followed that has another form;
    // - the request names the session of one device and is followed by that session only;
    // - it is valid from when it was made until it expires, 2 hours later;
    // - the agent keeps the id of every request it followed (AgentDiagnostics) and follows each id once.
    // The request holds no command, path or argument. It can only name parts of a fixed list, and each part is a collector
    // that is built into the agent (DiagCommands).
    public sealed class DiagnosticsRequest
    {
        public const string Folder = "agent/requests/";
        public const string KindDiagnostics = "diagnostics";
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
        // A signed request that lives longer than this is refused.
        public static readonly TimeSpan LongestLife = TimeSpan.FromHours(24);
        // A request made this much ahead of the clock of the device is still followed. Later than that it is refused.
        public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(10);
        // The file is a few hundred bytes. Anything much larger is not read as a request.
        public const int MaxBytes = 8 * 1024;
        public const int MaxSessionId = 64;
        const string IsoFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";

        // What can be collected. The default is all of it.
        public const string Logs = "logs", MdmDiag = "mdmdiag", Dsreg = "dsreg", Ime = "ime";
        public static readonly string[] AllParts = new string[] { Logs, MdmDiag, Dsreg, Ime };

        public string SessionId;
        public string RequestId;
        public string Kind;
        // Comma separated parts, in the order of AllParts.
        public string Collect;
        public DateTime CreatedUtc;
        public DateTime ExpiresUtc;
        public string Signature;

        // "agent/requests/<session id>.json". A session id with characters that are not safe in a name gets "_" for them.
        public static string NameFor(string sessionId)
        {
            return Folder + Clean(sessionId) + ".json";
        }

        // The id of the session behind a blob such as "sessions/<id>.json.gz".
        public static string SessionIdOf(string sessionBlob)
        {
            string id = Path.GetFileName(sessionBlob ?? "");
            int dot = id.IndexOf('.');
            if (dot > 0) id = id.Substring(0, dot);
            return id;
        }

        static string Clean(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char ch in s ?? "") b.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            return b.ToString();
        }

        // Where the agent puts the collected bundle: beside the log bundle of the session.
        public static string BundleName(string sessionId, string requestId)
        {
            return LogBundle.Prefix + Clean(sessionId) + "-diagnostics-" + Clean(requestId) + ".zip";
        }

        // A session blob that LogBundle.NameFor maps to BundleName, so DeviceLogs downloads and unpacks the bundle as it does the logs.
        public static string BundleKey(string sessionId, string requestId)
        {
            return AgentData.Prefix + Clean(sessionId) + "-diagnostics-" + Clean(requestId) + ".json.gz";
        }

        // Letters, digits, "-" and "_" only, as the agent makes them. Anything else is never signed or followed.
        public static bool IsSessionId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > MaxSessionId) return false;
            foreach (char ch in s)
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_')) return false;
            return true;
        }

        // A GUID in the one form Create writes it: lower case with hyphens.
        public static bool IsRequestId(string s)
        {
            Guid g;
            return !string.IsNullOrEmpty(s) && Guid.TryParseExact(s, "D", out g) && g != Guid.Empty && g.ToString("D") == s;
        }

        static string Iso(DateTime t)
        {
            return t.ToUniversalTime().ToString(IsoFormat, CultureInfo.InvariantCulture);
        }

        public static DateTime Whole(DateTime t)
        {
            DateTime u = t.ToUniversalTime();
            return new DateTime(u.Ticks - u.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }

        public static string DefaultCollect
        {
            get { return string.Join(",", AllParts); }
        }

        public static DiagnosticsRequest Create(string sessionId, DateTime nowUtc)
        {
            DiagnosticsRequest r = new DiagnosticsRequest();
            r.SessionId = sessionId;
            r.RequestId = Guid.NewGuid().ToString("D");
            r.Kind = KindDiagnostics;
            r.Collect = DefaultCollect;
            r.CreatedUtc = Whole(nowUtc);
            r.ExpiresUtc = r.CreatedUtc + Lifetime;
            return r;
        }

        public bool Wants(string part)
        {
            foreach (string p in (Collect ?? "").Split(',')) if (p == part) return true;
            return false;
        }

        // The exact text that is signed. Every field is in it.
        public byte[] Payload()
        {
            string s = "TrackVice agent request\n"
                + "session=" + SessionId + "\n"
                + "request=" + RequestId + "\n"
                + "kind=" + Kind + "\n"
                + "collect=" + Collect + "\n"
                + "created=" + Iso(CreatedUtc) + "\n"
                + "expires=" + Iso(ExpiresUtc) + "\n";
            return Encoding.UTF8.GetBytes(s);
        }

        // Only a request in the allowed form is signed, so nothing this PC read from a storage can end up in a signature
        // in another form than the agent checks.
        public void Sign(RSACryptoServiceProvider key)
        {
            if (key.KeySize < UpdateManifest.KeyBits) throw new ArgumentException("The signing key is shorter than " + UpdateManifest.KeyBits + " bits.");
            string problem = Problem();
            if (problem != null) throw new ArgumentException("The request is not signed. " + problem);
            Signature = Convert.ToBase64String(key.SignData(Payload(), "SHA256"));
        }

        public bool Verify(string publicKey)
        {
            if (string.IsNullOrEmpty(Signature)) return false;
            byte[] sig;
            try { sig = Convert.FromBase64String(Signature); }
            catch (FormatException) { return false; }
            using (RSACryptoServiceProvider rsa = UpdateKeys.FromPublic(publicKey))
                return rsa.VerifyData(Payload(), "SHA256", sig);
        }

        // What is wrong with the fields, or null.
        public string Problem()
        {
            if (string.IsNullOrEmpty(SessionId)) return "It names no session.";
            if (!IsSessionId(SessionId)) return "Its session id is not one an agent makes.";
            if (!IsRequestId(RequestId)) return "Its request id is not a GUID.";
            if (Kind != KindDiagnostics) return "Its kind is not known.";
            if (string.IsNullOrEmpty(Collect)) return "It asks for nothing.";
            List<string> seen = new List<string>();
            foreach (string p in Collect.Split(','))
            {
                if (Array.IndexOf(AllParts, p) < 0) return "It asks for something that cannot be collected.";
                if (seen.Contains(p)) return "It asks for the same thing twice.";
                seen.Add(p);
            }
            if (CreatedUtc.Year < 2020 || ExpiresUtc.Year < 2020) return "It has no time it was made or no time it expires.";
            if (CreatedUtc != Whole(CreatedUtc) || ExpiresUtc != Whole(ExpiresUtc)) return "Its times are not in whole seconds.";
            if (ExpiresUtc <= CreatedUtc) return "It expires before it was made.";
            if (ExpiresUtc - CreatedUtc > LongestLife) return "It is valid for more than " + (int)LongestLife.TotalHours + " hours.";
            return null;
        }

        [DataContract(Namespace = "")]
        sealed class Doc
        {
            [DataMember(Name = "session", Order = 1)] public string Session;
            [DataMember(Name = "request", Order = 2)] public string Request;
            [DataMember(Name = "kind", Order = 3)] public string Kind;
            [DataMember(Name = "collect", Order = 4)] public string Collect;
            [DataMember(Name = "created", Order = 5)] public string Created;
            [DataMember(Name = "expires", Order = 6)] public string Expires;
            [DataMember(Name = "signature", Order = 7)] public string Signature;
        }

        public byte[] ToJson()
        {
            Doc d = new Doc();
            d.Session = SessionId;
            d.Request = RequestId;
            d.Kind = Kind;
            d.Collect = Collect;
            d.Created = Iso(CreatedUtc);
            d.Expires = Iso(ExpiresUtc);
            d.Signature = Signature;
            using (MemoryStream ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Doc)).WriteObject(ms, d);
                return ms.ToArray();
            }
        }

        static DateTime Time(string s)
        {
            DateTime t;
            if (s != null && DateTime.TryParseExact(s, IsoFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return DateTime.MinValue;
        }

        public static DiagnosticsRequest Parse(byte[] json)
        {
            if (json == null || json.Length == 0) throw new StoreException("The request is empty.");
            if (json.Length > MaxBytes) throw new StoreException("The request is " + json.Length + " bytes, which is too large to be one.");
            Doc d;
            try
            {
                using (MemoryStream ms = new MemoryStream(json)) d = (Doc)new DataContractJsonSerializer(typeof(Doc)).ReadObject(ms);
            }
            catch (Exception ex) { throw new StoreException("The request could not be read: " + ex.Message); }
            if (d == null) throw new StoreException("The request is empty.");
            DiagnosticsRequest r = new DiagnosticsRequest();
            r.SessionId = d.Session;
            r.RequestId = d.Request;
            r.Kind = d.Kind;
            r.Collect = d.Collect;
            r.Signature = d.Signature;
            // Only the one way of writing a time. Anything else leaves it empty, which is not valid.
            r.CreatedUtc = Time(d.Created);
            r.ExpiresUtc = Time(d.Expires);
            return r;
        }

        // Reads a request as the agent does, and says whether it is to be followed. Null when it is, else the reason it is ignored.
        // handled holds the ids of requests that were followed already. Nothing of an unsigned or wrongly signed request is
        // used or repeated in the reason: the signature is checked before anything else is looked at.
        public static string Refuse(byte[] raw, string publicKey, string sessionId, DateTime nowUtc, ICollection<string> handled, out DiagnosticsRequest request)
        {
            request = null;
            DiagnosticsRequest r;
            try { r = Parse(raw); }
            catch (Exception ex) { return ex.Message; }
            if (string.IsNullOrEmpty(r.Signature)) return "The request is not signed.";
            bool ok;
            try { ok = r.Verify(publicKey); }
            catch (Exception ex) { return "The update key cannot be used: " + ex.Message; }
            if (!ok) return "The request is not signed with the update key of this device.";
            string problem = r.Problem();
            if (problem != null) return "The request is signed but not valid. " + problem;
            if (!IsSessionId(sessionId) || !string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)) return "The request is for another session.";
            nowUtc = nowUtc.ToUniversalTime();
            if (nowUtc >= r.ExpiresUtc) return "The request expired " + Iso(r.ExpiresUtc) + ".";
            if (r.CreatedUtc - nowUtc > ClockSlack) return "The request was made " + Iso(r.CreatedUtc) + ", which is later than the clock of this device.";
            if (handled != null)
                foreach (string h in handled)
                    if (string.Equals(h, r.RequestId, StringComparison.OrdinalIgnoreCase)) return "The request " + r.RequestId + " was followed already.";
            request = r;
            return null;
        }

        // The viewer: signs a request for the session and writes it to the place the device's agent reads.
        public static DiagnosticsRequest Publish(ISessionStore place, RSACryptoServiceProvider key, string sessionId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("The session has no id.");
            if (!IsSessionId(sessionId)) throw new ArgumentException("The session id is not one an agent makes, so no request is signed for it.");
            DiagnosticsRequest r = Create(sessionId, nowUtc);
            r.Sign(key);
            place.Put(NameFor(sessionId), r.ToJson(), null);
            return r;
        }
    }

    // ---------- what the viewer may ask, and from where ----------

    public static class DiagnosticsSupport
    {
        // The first TrackVice whose agent reads diagnostics requests. It stays fixed: later releases keep reading them, so an
        // agent of 2.1.0 can still collect when the viewer is newer.
        public const string FirstVersion = "2.1.0";

        // Null when the agent can collect diagnostics, else the sentence for the viewer. current is the version of this
        // TrackVice, which Publish agent update brings the agents to.
        public static string AgentGate(string agentVersion, string current)
        {
            string need = "Collect diagnostics needs " + FirstVersion + " or later.";
            string publish = SemVer.IsValid(current) ? " Publish agent update brings the agents to " + current + "." : "";
            if (!SemVer.IsValid(agentVersion)) return "The version of this agent is not known; " + need + publish;
            if (SemVer.Compare(agentVersion, FirstVersion) < 0) return "This agent is " + agentVersion + "; " + need + publish;
            return null;
        }

        // The place the customer's agents read updates and requests from, or null with the reason in problem.
        public static UpdateLocation Place(IList<UpdateLocation> places, string customer, out string problem)
        {
            problem = null;
            List<UpdateLocation> mine = new List<UpdateLocation>();
            if (places != null)
            {
                foreach (UpdateLocation l in places) if (!string.IsNullOrEmpty(customer) && l.Has(customer)) mine.Add(l);
                // Without a known customer the only place there is will do.
                if (mine.Count == 0 && string.IsNullOrEmpty(customer) && places.Count == 1) mine.Add(places[0]);
            }
            if (mine.Count == 0)
            {
                problem = "TrackVice does not know where the agents of " + (string.IsNullOrEmpty(customer) ? "this device" : customer) + " read their updates. The request is written there, so set up Your storage or add the customer code in Settings, then try again.";
                return null;
            }
            foreach (UpdateLocation l in mine) if (l.CanWrite && !l.OtherKey) return l;
            UpdateLocation first = mine[0];
            if (first.OtherKey) problem = OtherKeyText(first);
            else problem = "This PC cannot write to " + first.Short + ", where the agent reads requests. " + (first.Missing.Length > 0 ? first.Missing : "Open Publish agent update and get access to it, then try again.");
            return null;
        }

        // Signs a request for the session and writes it to the place, as the viewer does. Reads agent/latest.json there
        // first, as Publish does: a place whose agents follow another update key would ignore the request. Throws with a
        // sentence for the person.
        public static DiagnosticsRequest Send(UpdateLocation place, Func<string, ISessionStore> open, RSACryptoServiceProvider key, string sessionId, DateTime nowUtc)
        {
            if (key == null) throw new StoreException("This PC has no update key. Publish agent update makes it, and the agents must have been given its public key.");
            if (!place.CanWrite) throw new StoreException("This PC cannot write to " + place.Short + ". " + (place.Missing.Length > 0 ? place.Missing : "Open Publish agent update and get access to it, then try again."));
            UpdateLocations.Refresh(place, open, UpdateKeys.PublicKey(key));
            if (place.State == UpdateState.Failed) throw new StoreException("Could not read " + UpdateManifest.Name + " in " + place.Short + " to check its update key: " + place.Problem);
            if (place.OtherKey) throw new StoreException(OtherKeyText(place));
            try { return DiagnosticsRequest.Publish(open(place.WriteLink), key, sessionId, nowUtc); }
            catch (ArgumentException ex) { throw new StoreException(ex.Message); }
        }

        static string OtherKeyText(UpdateLocation l)
        {
            return l.Short + " holds an agent update signed with another update key than the one on this PC, so the agent would ignore a request signed here. Use the PC that publishes there.";
        }

        // What is collected, in words, for the question before the request is made.
        public static string WhatIsCollected()
        {
            return "The logs the agent uploads already, the cab of MdmDiagnosticsTool for Autopilot, device enrollment, device provisioning and TPM, "
                + "the output of dsregcmd /status, and the Intune Management Extension registry keys Win32Apps and SideCarPolicies. "
                + "Secrets are masked in the session and the text files. The log files and the cab are as they are on the device, as in the logs the agent uploads.";
        }

        public static string WhenItIsRead()
        {
            return "The agent reads requests every minute while the device deploys, and every 10 minutes after.";
        }
    }

    // ---------- the progress the agent writes into its session ----------

    public sealed class DiagnosticsEntry
    {
        public string Id = "";
        // Received, Collecting, Uploaded or Failed.
        public string State = "";
        public DateTime Utc;
        public string Blob = "";
        public string Note = "";
    }

    public static class DiagnosticsStatus
    {
        public const string InfoKey = "Diagnostics requests";
        public const string Received = "Received", Collecting = "Collecting", Uploaded = "Uploaded", Failed = "Failed";
        public const int Keep = 5;

        static string Clean(string s)
        {
            return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        }

        // One line per request: id|state|utc|blob|note. The newest last.
        public static string Encode(IList<DiagnosticsEntry> list)
        {
            StringBuilder b = new StringBuilder();
            int from = Math.Max(0, list.Count - Keep);
            for (int k = from; k < list.Count; k++)
            {
                DiagnosticsEntry e = list[k];
                if (b.Length > 0) b.Append('\n');
                b.Append(Clean(e.Id)).Append('|').Append(Clean(e.State)).Append('|').Append(e.Utc.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture))
                    .Append('|').Append(Clean(e.Blob)).Append('|').Append(Clean(e.Note));
            }
            return b.ToString();
        }

        // The longest id, state, blob name and note that are read. The agent writes shorter ones.
        public const int MaxId = 64, MaxState = 16, MaxBlob = 200, MaxNote = 400;
        // More text than this is not what an agent writes, and is not read.
        public const int MaxText = 64 * 1024;

        static string Cut(string s, int max)
        {
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // The viewer reads this from a session in the storage, where anyone with the upload link can write. So only the
        // newest Keep entries are read, and every field is cut to the longest the agent writes.
        public static List<DiagnosticsEntry> Decode(string text)
        {
            List<DiagnosticsEntry> l = new List<DiagnosticsEntry>();
            if (string.IsNullOrEmpty(text) || text.Length > MaxText) return l;
            foreach (string line in text.Split('\n'))
            {
                string[] p = line.Split(new char[] { '|' }, 5);
                if (p.Length < 5 || p[0].Length == 0 || p[0].Length > MaxId) continue;
                DiagnosticsEntry e = new DiagnosticsEntry();
                e.Id = p[0];
                e.State = Cut(p[1], MaxState);
                DateTime t = AgentVersions.Time(p[2]) ?? DateTime.MinValue;
                e.Utc = t;
                e.Blob = Cut(p[3], MaxBlob);
                e.Note = Cut(p[4].TrimEnd('\r'), MaxNote);
                l.Add(e);
            }
            if (l.Count > Keep) l.RemoveRange(0, l.Count - Keep);
            return l;
        }

        public static List<DiagnosticsEntry> Of(Session s)
        {
            return s == null ? new List<DiagnosticsEntry>() : Decode(s.GetInfo(InfoKey));
        }

        public static DiagnosticsEntry Find(Session s, string requestId)
        {
            foreach (DiagnosticsEntry e in Of(s)) if (string.Equals(e.Id, requestId, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public static void Write(Session s, IList<DiagnosticsEntry> list)
        {
            if (s == null || list.Count == 0) return;
            s.SetInfo(InfoKey, Encode(list));
        }

        // 0 requested, 1 received, 2 collecting, 3 uploaded; -1 failed.
        public static int Step(DiagnosticsEntry e)
        {
            if (e == null) return 0;
            switch (e.State)
            {
                case Received: return 1;
                case Collecting: return 2;
                case Uploaded: return 3;
                case Failed: return -1;
                default: return 0;
            }
        }

        public static string Label(DiagnosticsEntry e)
        {
            switch (Step(e))
            {
                case 1: return "Received by the agent";
                case 2: return "Collecting on the device";
                case 3: return "Uploaded";
                case -1: return "Failed";
                default: return "Requested";
            }
        }
    }
}
