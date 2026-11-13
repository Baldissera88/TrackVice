namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading;

    // One deployment on another device, as a remote source lists it.
    public sealed class RemoteDeployment
    {
        public string Key;
        public string Device;
        public string User;
        public string Profile;
        public State State;
        public string StateText;
        public DateTime? StartUtc;
        public DateTime? EndUtc;
        public DateTime LastUtc;
        public string Detail;
        public string Serial;

        public TimeSpan? Duration(DateTime nowUtc)
        {
            if (!StartUtc.HasValue) return null;
            DateTime e = EndUtc ?? (States.IsFinal(State) ? LastUtc : nowUtc);
            return e < StartUtc.Value ? TimeSpan.Zero : e - StartUtc.Value;
        }
    }

    // Lists deployments on other devices: Autopilot in Intune, or task sequences in SCCM.
    public interface IRemoteCatalog
    {
        string Title { get; }
        string Source { get; }
        List<RemoteDeployment> List(DateTime sinceUtc);
        IRemoteFeed Open(RemoteDeployment d);
        int RefreshSeconds { get; }
    }

    // Follows one deployment on another device. Each read returns only what changed since the last one.
    public interface IRemoteFeed
    {
        string Mode { get; }
        Batch Read(DateTime nowUtc);
        int PollMs { get; }
    }

    // Turns snapshots of states into observations, so a state read again unchanged adds nothing to the timeline.
    public sealed class SnapshotDiff
    {
        readonly Dictionary<string, string> last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool Changed(string key, string signature)
        {
            string old;
            if (last.TryGetValue(key, out old) && old == signature) return false;
            last[key] = signature;
            return true;
        }
    }

    // ---------- Microsoft Graph ----------

    public sealed class DeviceCode
    {
        public string Code;
        public string UserCode;
        public string VerificationUri;
        public int Interval = 5;
        public int ExpiresIn = 900;
    }

    public sealed class GraphException : Exception
    {
        public readonly int Status;

        public GraphException(string message) : base(message) { }

        public GraphException(string message, int status) : base(message)
        {
            Status = status;
        }
    }

    public interface IGraph
    {
        Dictionary<string, object> Get(string url);
    }

    // Read-only Microsoft Graph sign-in with the device code flow, the same way PolicyVice signs in. The tokens are
    // kept in memory only, and the access token is renewed with the refresh token while TrackVice watches.
    public sealed class GraphClient : IGraph
    {
        // Microsoft Graph Command Line Tools, the app Microsoft Graph PowerShell signs in with.
        public const string DefaultClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
        public const string Scope = "https://graph.microsoft.com/DeviceManagementManagedDevices.Read.All";

        public readonly string ClientId;
        public readonly string Tenant;
        string access, refresh;
        DateTime expires;
        readonly object gate = new object();
        public string Account;

        public GraphClient(string clientId, string tenant)
        {
            ClientId = IsGuid(clientId) ? clientId.Trim('{', '}') : DefaultClientId;
            Tenant = string.IsNullOrEmpty(tenant) ? "organizations" : tenant.Trim();
        }

        public bool SignedIn
        {
            get { return access != null; }
        }

        public static bool IsGuid(string s)
        {
            Guid g;
            return !string.IsNullOrEmpty(s) && Guid.TryParse(s.Trim('{', '}'), out g);
        }

        string Authority
        {
            get { return "https://login.microsoftonline.com/" + Uri.EscapeDataString(Tenant) + "/oauth2/v2.0/"; }
        }

        public DeviceCode Start()
        {
            Dictionary<string, object> r = Http.Post(Authority + "devicecode", "client_id=" + Uri.EscapeDataString(ClientId) + "&scope=" + Uri.EscapeDataString(Scope + " offline_access"));
            string err = Http.Str(r, "error_description") ?? Http.Str(r, "error");
            if (err != null) throw new GraphException(err);
            DeviceCode d = new DeviceCode();
            d.Code = Http.Str(r, "device_code");
            d.UserCode = Http.Str(r, "user_code");
            d.VerificationUri = Http.Str(r, "verification_uri") ?? "https://microsoft.com/devicelogin";
            long? i = ImeJson.Long(ImeJson.Get(r, "interval"));
            long? e = ImeJson.Long(ImeJson.Get(r, "expires_in"));
            if (i.HasValue && i.Value > 0) d.Interval = (int)i.Value;
            if (e.HasValue && e.Value > 0) d.ExpiresIn = (int)e.Value;
            if (string.IsNullOrEmpty(d.Code) || string.IsNullOrEmpty(d.UserCode)) throw new GraphException("The sign-in service did not return a code.");
            return d;
        }

        // Waits until the user has signed in on the sign-in page.
        public void Wait(DeviceCode d, WaitHandle cancel)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(d.ExpiresIn);
            int interval = d.Interval;
            while (DateTime.UtcNow < until)
            {
                if (cancel.WaitOne(TimeSpan.FromSeconds(interval))) throw new OperationCanceledException();
                Dictionary<string, object> r = Http.Post(Authority + "token",
                    "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&client_id=" + Uri.EscapeDataString(ClientId) + "&device_code=" + Uri.EscapeDataString(d.Code));
                if (Take(r)) return;
                string err = Http.Str(r, "error");
                if (err == "authorization_pending") continue;
                if (err == "slow_down") { interval += 5; continue; }
                if (err == "expired_token") throw new GraphException("The code expired. Start again.");
                if (err == "authorization_declined" || err == "access_denied") throw new GraphException("The sign-in was declined.");
                throw new GraphException(Http.Str(r, "error_description") ?? err ?? "The sign-in failed.");
            }
            throw new GraphException("The code expired. Start again.");
        }

        bool Take(Dictionary<string, object> r)
        {
            string token = Http.Str(r, "access_token");
            if (string.IsNullOrEmpty(token)) return false;
            lock (gate)
            {
                access = token;
                string rt = Http.Str(r, "refresh_token");
                if (!string.IsNullOrEmpty(rt)) refresh = rt;
                long? e = ImeJson.Long(ImeJson.Get(r, "expires_in"));
                expires = DateTime.UtcNow.AddSeconds((e ?? 3600) - 120);
            }
            if (Account == null) Account = AccountFrom(token);
            return true;
        }

        // The account name from the token, for the window. The token is not checked here; Graph checks it.
        static string AccountFrom(string jwt)
        {
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                string p = parts[1].Replace('-', '+').Replace('_', '/');
                while (p.Length % 4 != 0) p += "=";
                Dictionary<string, object> c = ImeJson.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p))) as Dictionary<string, object>;
                return Http.Str(c, "upn") ?? Http.Str(c, "unique_name") ?? Http.Str(c, "preferred_username");
            }
            catch (Exception) { return null; }
        }

        void Renew()
        {
            string rt;
            lock (gate) rt = refresh;
            if (rt == null) throw new GraphException("The sign-in has expired. Sign in again.");
            Dictionary<string, object> r = Http.Post(Authority + "token",
                "grant_type=refresh_token&client_id=" + Uri.EscapeDataString(ClientId) + "&refresh_token=" + Uri.EscapeDataString(rt) + "&scope=" + Uri.EscapeDataString(Scope + " offline_access"));
            if (!Take(r)) throw new GraphException("The sign-in has expired. Sign in again. (" + (Http.Str(r, "error_description") ?? Http.Str(r, "error") ?? "no token") + ")");
        }

        public Dictionary<string, object> Get(string url)
        {
            if (access == null) throw new GraphException("Not signed in.");
            if (DateTime.UtcNow > expires) Renew();
            int status;
            Dictionary<string, object> r = Http.Get(url, access, out status);
            if (status == 401)
            {
                Renew();
                r = Http.Get(url, access, out status);
            }
            string err = Error(r, status);
            if (err != null) throw new GraphException(err, status);
            return r;
        }

        static string Error(Dictionary<string, object> r, int status)
        {
            Dictionary<string, object> e = ImeJson.Get(r, "error") as Dictionary<string, object>;
            if (e == null) return status >= 400 ? "Microsoft Graph answered HTTP " + status + "." : null;
            string code = Http.Str(e, "code"), msg = Http.Str(e, "message");
            if (status == 403 || code == "Forbidden" || code == "Authorization_RequestDenied")
                return "Access denied. The account needs an Intune role that can read devices, and the app needs consent for DeviceManagementManagedDevices.Read.All." + (msg != null ? " (" + Txt.Short(msg, 200) + ")" : "");
            return msg ?? code ?? "Microsoft Graph answered HTTP " + status + ".";
        }
    }

    public static class Http
    {
        public static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v = ImeJson.Get(d, key);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static HttpWebRequest Request(string url)
        {
            // .NET Framework 4.5 does not offer TLS 1.2 by default.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.UserAgent = "TrackVice/" + Program.Version;
            try
            {
                req.Proxy = WebRequest.GetSystemWebProxy();
                req.Proxy.Credentials = CredentialCache.DefaultCredentials;
            }
            catch (Exception) { }
            return req;
        }

        public static Dictionary<string, object> Post(string url, string form)
        {
            HttpWebRequest req = Request(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            byte[] body = Encoding.UTF8.GetBytes(form);
            req.ContentLength = body.Length;
            using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            int status;
            return Send(req, out status);
        }

        public static Dictionary<string, object> Get(string url, string token, out int status)
        {
            HttpWebRequest req = Request(url);
            req.Method = "GET";
            req.Accept = "application/json";
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            return Send(req, out status);
        }

        // Returns the JSON body also for error responses, which carry the error code.
        static Dictionary<string, object> Send(HttpWebRequest req, out int status)
        {
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new GraphException("Could not reach " + req.RequestUri.Host + ": " + ex.Message);
            }
            using (resp)
            using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                status = (int)resp.StatusCode;
                string text = r.ReadToEnd();
                Dictionary<string, object> d = ImeJson.Parse(text) as Dictionary<string, object>;
                if (d == null)
                {
                    if (status == 401) return new Dictionary<string, object>();
                    throw new GraphException("Unexpected answer from " + req.RequestUri.Host + " (HTTP " + status + ").", status);
                }
                return d;
            }
        }
    }

    // ---------- Autopilot in Intune ----------

    // Autopilot deployments from Intune: GET beta/deviceManagement/autopilotEvents, and per deployment its
    // policyStatusDetails (apps and policies the device reported). This is what Intune's Autopilot deployments
    // report shows. It is a beta API and only as current as the device's last report to Intune.
    public sealed class IntuneCatalog : IRemoteCatalog
    {
        public const string EventsUrl = "https://graph.microsoft.com/beta/deviceManagement/autopilotEvents";
        readonly IGraph graph;
        readonly Dictionary<string, Dictionary<string, object>> raw = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        public int MaxPages = 50;

        public IntuneCatalog(IGraph g)
        {
            graph = g;
        }

        public string Title
        {
            get { return "Autopilot in Intune"; }
        }

        public string Source
        {
            get { GraphClient c = graph as GraphClient; return c != null && c.Account != null ? "Signed in as " + c.Account : "Microsoft Graph"; }
        }

        public int RefreshSeconds
        {
            get { return 60; }
        }

        public List<RemoteDeployment> List(DateTime sinceUtc)
        {
            List<RemoteDeployment> list = new List<RemoteDeployment>();
            string url = EventsUrl;
            int pages = 0;
            while (!string.IsNullOrEmpty(url) && pages++ < MaxPages)
            {
                Dictionary<string, object> r = graph.Get(url);
                List<object> value = ImeJson.Get(r, "value") as List<object>;
                if (value != null)
                {
                    foreach (object o in value)
                    {
                        Dictionary<string, object> e = o as Dictionary<string, object>;
                        if (e == null) continue;
                        RemoteDeployment d = ToDeployment(e);
                        if (d == null) continue;
                        DateTime when = d.StartUtc ?? d.LastUtc;
                        if (when < sinceUtc) continue;
                        lock (raw) raw[d.Key] = e;
                        list.Add(d);
                    }
                }
                url = Http.Str(r, "@odata.nextLink");
            }
            // The newest deployment per device: a device that was reset shows its latest try.
            Dictionary<string, RemoteDeployment> newest = new Dictionary<string, RemoteDeployment>(StringComparer.OrdinalIgnoreCase);
            foreach (RemoteDeployment d in list)
            {
                string k = (d.Serial ?? "") + "|" + (d.Device ?? d.Key);
                RemoteDeployment old;
                if (!newest.TryGetValue(k, out old) || (d.StartUtc ?? d.LastUtc) > (old.StartUtc ?? old.LastUtc)) newest[k] = d;
            }
            List<RemoteDeployment> result = new List<RemoteDeployment>(newest.Values);
            result.Sort(Order);
            return result;
        }

        // Running first, then newest first.
        public static int Order(RemoteDeployment a, RemoteDeployment b)
        {
            bool ra = States.IsActive(a.State), rb = States.IsActive(b.State);
            if (ra != rb) return ra ? -1 : 1;
            return (b.StartUtc ?? b.LastUtc).CompareTo(a.StartUtc ?? a.LastUtc);
        }

        public IRemoteFeed Open(RemoteDeployment d)
        {
            return new IntuneFeed(graph, d.Key);
        }

        public static RemoteDeployment ToDeployment(Dictionary<string, object> e)
        {
            string id = Http.Str(e, "id");
            if (string.IsNullOrEmpty(id)) return null;
            RemoteDeployment d = new RemoteDeployment();
            d.Key = id;
            d.Device = Http.Str(e, "managedDeviceName");
            d.Serial = Http.Str(e, "deviceSerialNumber");
            if (string.IsNullOrEmpty(d.Device)) d.Device = d.Serial ?? id;
            d.User = Http.Str(e, "userPrincipalName");
            d.Profile = Http.Str(e, "windowsAutopilotDeploymentProfileDisplayName");
            string st = Http.Str(e, "deploymentState");
            d.State = DeploymentState(st);
            d.StateText = DeploymentText(st);
            d.StartUtc = Time(e, "deploymentStartDateTime") ?? Time(e, "enrollmentStartDateTime");
            d.EndUtc = Time(e, "deploymentEndDateTime");
            d.LastUtc = Time(e, "eventDateTime") ?? d.EndUtc ?? d.StartUtc ?? DateTime.UtcNow;
            d.Detail = Http.Str(e, "enrollmentFailureDetails");
            return d;
        }

        // Graph sends 0001-01-01 for times it does not have.
        public static DateTime? Time(Dictionary<string, object> e, string key)
        {
            string s = Http.Str(e, key);
            DateTime t;
            if (string.IsNullOrEmpty(s) || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return null;
            return t.Year < 2000 ? (DateTime?)null : DateTime.SpecifyKind(t, DateTimeKind.Utc);
        }

        public static State DeploymentState(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "success": case "successonretry": return State.Succeeded;
                case "successwithtimeout": return State.Succeeded;
                case "inprogress": return State.Installing;
                case "failure": return State.Failed;
                case "notattempted": return State.Pending;
                case "disabled": return State.Skipped;
                default: return State.Unknown;
            }
        }

        public static string DeploymentText(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "success": return "Succeeded";
                case "successonretry": return "Succeeded on a retry";
                case "successwithtimeout": return "Succeeded, the ESP timed out";
                case "inprogress": return "In progress";
                case "failure": return "Failed";
                case "notattempted": return "Not started";
                case "disabled": return "Turned off";
                default: return "Unknown";
            }
        }

        // complianceStatus: unknown, compliant, installed, notCompliant, notInstalled, error.
        public static State PolicyState(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "installed": case "compliant": return State.Succeeded;
                case "error": return State.Failed;
                case "notinstalled": case "notcompliant": return State.Pending;
                default: return State.Unknown;
            }
        }
    }

    public sealed class IntuneFeed : IRemoteFeed
    {
        const string AutopilotPhase = "intune:autopilot";
        readonly IGraph graph;
        readonly string id;
        readonly SnapshotDiff diff = new SnapshotDiff();
        bool first = true;

        public IntuneFeed(IGraph g, string eventId)
        {
            graph = g;
            id = eventId;
        }

        public string Mode
        {
            get { return Modes.Intune; }
        }

        public int PollMs
        {
            get { return 60000; }
        }

        public Batch Read(DateTime nowUtc)
        {
            Dictionary<string, object> e = graph.Get(IntuneCatalog.EventsUrl + "/" + Uri.EscapeDataString(id));
            List<Dictionary<string, object>> details = new List<Dictionary<string, object>>();
            string url = IntuneCatalog.EventsUrl + "/" + Uri.EscapeDataString(id) + "/policyStatusDetails";
            int pages = 0;
            while (!string.IsNullOrEmpty(url) && pages++ < 20)
            {
                Dictionary<string, object> r = graph.Get(url);
                List<object> value = ImeJson.Get(r, "value") as List<object>;
                if (value != null) foreach (object o in value) { Dictionary<string, object> x = o as Dictionary<string, object>; if (x != null) details.Add(x); }
                url = Http.Str(r, "@odata.nextLink");
            }
            Batch b = Map(e, details, nowUtc, diff);
            if (first)
            {
                b.Notes.Add("This deployment is read from Intune with Microsoft Graph. It is only as current as the device's last report to Intune, and it has no log lines. Run TrackVice on the device itself to see every step as it happens.");
                first = false;
            }
            return b;
        }

        // Turns one Autopilot event and its policy details into observations. Shared by the live feed, the demo and the tests.
        public static Batch Map(Dictionary<string, object> e, List<Dictionary<string, object>> details, DateTime nowUtc, SnapshotDiff diff)
        {
            Batch b = new Batch();
            RemoteDeployment d = IntuneCatalog.ToDeployment(e);
            if (d == null) return b;
            b.Info.Add(new Pair("Source", "Intune (Microsoft Graph)"));
            b.Info.Add(new Pair("Device", d.Device));
            if (!string.IsNullOrEmpty(d.Serial)) b.Info.Add(new Pair("Serial number", d.Serial));
            if (!string.IsNullOrEmpty(d.User)) b.Info.Add(new Pair("User", d.User));
            if (!string.IsNullOrEmpty(d.Profile)) b.Info.Add(new Pair("Autopilot profile", d.Profile));
            string esp = Http.Str(e, "windows10EnrollmentCompletionPageConfigurationDisplayName");
            if (!string.IsNullOrEmpty(esp)) b.Info.Add(new Pair("ESP profile", esp));
            string type = Http.Str(e, "enrollmentType");
            if (!string.IsNullOrEmpty(type)) b.Info.Add(new Pair("Enrollment type", Txt.SplitCamel(type)));
            string os = Http.Str(e, "osVersion");
            if (!string.IsNullOrEmpty(os)) b.Info.Add(new Pair("Windows", os));
            b.Info.Add(new Pair("Deployment state", d.StateText));
            if (string.Equals(Http.Str(e, "deploymentState"), "successWithTimeout", StringComparison.OrdinalIgnoreCase)) b.Info.Add(new Pair("ESP time limit", "Reached: Intune reports success with timeout"));
            if (!string.IsNullOrEmpty(d.Detail)) b.Info.Add(new Pair("Failure details", d.Detail));
            DateTime? reg = IntuneCatalog.Time(e, "deviceRegisteredDateTime");
            if (reg.HasValue) b.Info.Add(new Pair("Registered with Autopilot", Txt.Local(reg.Value)));
            b.Info.Add(new Pair("Last report to Intune", Txt.Local(d.LastUtc)));

            DateTime start = d.StartUtc ?? d.LastUtc;
            TimeSpan? devDur = Dur(Http.Str(e, "deviceSetupDuration"));
            TimeSpan? accDur = Dur(Http.Str(e, "accountSetupDuration"));
            Phase(b, diff, "intune:devicesetup", "Device setup", Http.Str(e, "deviceSetupStatus"), start, devDur, d.LastUtc, nowUtc);
            DateTime accStart = devDur.HasValue ? start + devDur.Value : start;
            Phase(b, diff, "intune:accountsetup", "Account setup", Http.Str(e, "accountSetupStatus"), accStart, accDur, d.LastUtc, nowUtc);
            if (!string.IsNullOrEmpty(d.Detail) && diff.Changed("detail", d.Detail))
            {
                Obs o = Base(d.LastUtc, "intune:deployment", "Autopilot deployment", Kinds.Enrollment, AutopilotPhase);
                o.PhaseName = "Autopilot";
                o.PhaseOrder = 30;
                o.Severity = 2;
                o.State = State.Failed;
                o.Text = d.Detail;
                b.Obs.Add(o);
            }

                foreach (Dictionary<string, object> x in details)
            {
                string pid = Http.Str(x, "id");
                if (string.IsNullOrEmpty(pid)) continue;
                string ptype = (Http.Str(x, "policyType") ?? "").ToLowerInvariant();
                string cs = Http.Str(x, "complianceStatus");
                long? err = ImeJson.Long(ImeJson.Get(x, "errorCode"));
                bool tracked = string.Equals(Http.Str(x, "trackedOnEnrollmentStatus"), "true", StringComparison.OrdinalIgnoreCase);
                DateTime when = IntuneCatalog.Time(x, "lastReportedDateTime") ?? d.LastUtc;
                State st = IntuneCatalog.PolicyState(cs);
                if (err.HasValue && err.Value != 0 && st != State.Succeeded) st = State.Failed;
                string sig = st + "|" + err + "|" + when.Ticks;
                if (!diff.Changed("p:" + pid, sig)) continue;
                bool app = ptype == "application" || ptype == "appmodel";
                Obs o = Base(when, "intune:" + pid.ToLowerInvariant(), Http.Str(x, "displayName") ?? pid, app ? Kinds.Win32App : Kinds.Policy, app ? Phases.IntuneApps : "intune:policies");
                o.PhaseName = app ? (tracked ? "Apps the ESP waits for" : "Apps") : (tracked ? "Policies the ESP waits for" : "Policies");
                o.PhaseOrder = app ? (tracked ? 45 : 90) : (tracked ? 44 : 91);
                o.PhaseKey = app ? (tracked ? "intune:esp-apps" : Phases.IntuneApps) : (tracked ? "intune:esp-policies" : "intune:policies");
                o.State = st;
                if (err.HasValue && err.Value != 0) o.ExitCode = err;
                o.AddInfo("ID", pid.ToLowerInvariant());
                o.AddInfo("Policy type", Txt.SplitCamel(Http.Str(x, "policyType")));
                o.AddInfo("Tracked by the ESP", tracked ? "Yes" : "No");
                o.AddInfo("Status in Intune", Txt.SplitCamel(cs));
                o.Text = "Intune: " + Txt.SplitCamel(cs ?? "unknown").ToLowerInvariant() + (err.HasValue && err.Value != 0 ? ", " + Errors.Describe(err.Value) : "");
                if (st == State.Failed) o.Severity = 2;
                o.Evidence = new Evidence(EvidenceKinds.Graph, "autopilotEvents/" + Http.Str(e, "id") + "/policyStatusDetails/" + pid, 0, Raw(x));
                b.Obs.Add(o);
            }
            return b;
        }

        static void Phase(Batch b, SnapshotDiff diff, string key, string name, string status, DateTime start, TimeSpan? dur, DateTime last, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(status)) return;
            State st = IntuneCatalog.DeploymentState(status);
            if (st == State.Unknown && !dur.HasValue) return;
            string sig = st + "|" + (dur.HasValue ? dur.Value.Ticks : 0);
            if (!diff.Changed(key, sig)) return;
            // A start observation first, so the item has its real start and duration.
            if (st != State.Pending && st != State.Skipped && st != State.Unknown)
            {
                Obs s = Base(start, key, name, Kinds.Enrollment, AutopilotPhase);
                s.PhaseName = "Autopilot";
                s.PhaseOrder = 30;
                s.State = State.Installing;
                s.Text = name + " started";
                if (diff.Changed(key + ":start", start.Ticks.ToString(CultureInfo.InvariantCulture))) b.Obs.Add(s);
            }
            Obs o = Base(st == State.Installing ? last : dur.HasValue ? start + dur.Value : last, key, name, Kinds.Enrollment, AutopilotPhase);
            o.PhaseName = "Autopilot";
            o.PhaseOrder = 30;
            o.State = st;
            o.Text = name + ": " + IntuneCatalog.DeploymentText(status).ToLowerInvariant() + (dur.HasValue && st != State.Installing ? " after " + Txt.Duration(dur) : "");
            if (st == State.Failed) o.Severity = 2;
            b.Obs.Add(o);
        }

        static Obs Base(DateTime t, string key, string name, string kind, string phase)
        {
            Obs o = new Obs();
            o.TimeUtc = t;
            o.Source = "Intune";
            o.ItemKey = key;
            o.ItemName = name;
            o.ItemKind = kind;
            o.Origin = Origins.Intune;
            o.PhaseKey = phase;
            return o;
        }

        // ISO 8601 durations as Graph writes them: PT1H2M3.5S, P1DT2H.
        public static TimeSpan? Dur(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                TimeSpan t = System.Xml.XmlConvert.ToTimeSpan(s);
                return t > TimeSpan.Zero ? (TimeSpan?)t : null;
            }
            catch (Exception) { return null; }
        }

        static string Raw(Dictionary<string, object> x)
        {
            StringBuilder b = new StringBuilder();
            foreach (KeyValuePair<string, object> kv in x)
            {
                if (kv.Key.StartsWith("@")) continue;
                if (b.Length > 0) b.Append(", ");
                b.Append(kv.Key).Append('=').Append(Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
            }
            return b.ToString();
        }
    }
}
