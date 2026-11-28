namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.Serialization;
    using System.Text;

    // A time the agent could not reach the storage because the device had no network, saved in the session.
    [DataContract]
    public sealed class NetOutage
    {
        [DataMember] public DateTime FromUtc;
        [DataMember] public DateTime ToUtc;
        // One of NetworkOutages.Reasons.
        [DataMember] public string Reason;
        // The failed uploads in it.
        [DataMember] public int Failures;

        public TimeSpan Length
        {
            get { return ToUtc > FromUtc ? ToUtc - FromUtc : TimeSpan.Zero; }
        }
    }

    // Finds outages from the results of the agent's uploads, and tells where a failure was probably caused by one.
    public sealed class OutageRecorder
    {
        public readonly List<NetOutage> List = new List<NetOutage>();
        // Outages closer than this are one.
        public static readonly TimeSpan MergeWithin = TimeSpan.FromSeconds(30);
        // The most the agent keeps, and the most the viewer reads from an upload.
        public const int Max = 200;

        DateTime? failFrom;
        string failReason;
        int failures;

        // An upload worked: an outage that was going on ends here.
        public void Success(DateTime nowUtc)
        {
            if (!failFrom.HasValue) return;
            Add(failFrom.Value, nowUtc, failReason, failures);
            failFrom = null;
            failReason = null;
            failures = 0;
        }

        // An upload failed. Only network errors count: a refused link or a full disk is not an outage.
        public void Failure(DateTime nowUtc, string message)
        {
            if (!NetworkOutages.IsNetworkError(message)) return;
            string r = NetworkOutages.Reason(message);
            if (!failFrom.HasValue)
            {
                failFrom = nowUtc;
                failReason = r;
            }
            else if (failReason == NetworkOutages.Other && r != NetworkOutages.Other) failReason = r;
            failures++;
        }

        // True while uploads fail because of the network.
        public bool Open
        {
            get { return failFrom.HasValue; }
        }

        void Add(DateTime from, DateTime to, string reason, int count)
        {
            if (List.Count > 0)
            {
                NetOutage last = List[List.Count - 1];
                if (from - last.ToUtc < MergeWithin)
                {
                    last.ToUtc = to;
                    last.Failures += count;
                    if (last.Reason == NetworkOutages.Other) last.Reason = reason;
                    return;
                }
            }
            NetOutage o = new NetOutage();
            o.FromUtc = from;
            o.ToUtc = to;
            o.Reason = reason;
            o.Failures = count;
            List.Add(o);
            if (List.Count > Max) List.RemoveAt(0);
        }

        // A copy for the session, so the session does not change while the recorder goes on.
        public List<NetOutage> Snapshot()
        {
            List<NetOutage> l = new List<NetOutage>();
            foreach (NetOutage o in List)
            {
                NetOutage c = new NetOutage();
                c.FromUtc = o.FromUtc;
                c.ToUtc = o.ToUtc;
                c.Reason = o.Reason;
                c.Failures = o.Failures;
                l.Add(c);
            }
            return l;
        }
    }

    public static class NetworkOutages
    {
        public const string NameResolution = "name resolution", CouldNotConnect = "could not connect", ConnectionClosed = "connection closed", Timeout = "timeout", Other = "other";
        // How far from an outage a failure can be and still be put down to it.
        public static readonly TimeSpan Near = TimeSpan.FromSeconds(30);
        public const string EventSource = "Network";

        static readonly string[][] Words =
        {
            new string[] { NameResolution, "could not be resolved", "name resolution", "no such host", "name or service not known", "no address associated", "dns" },
            new string[] { Timeout, "timed out", "timeout", "time-out" },
            new string[] { ConnectionClosed, "connection was closed", "connection closed", "forcibly closed", "connection reset", "reset by peer", "broken pipe", "unexpected eof", "connection was aborted" },
            new string[] { CouldNotConnect, "unable to connect", "could not connect", "cannot connect", "connection refused", "actively refused", "unreachable", "no route to host", "no network", "network is down" }
        };

        // WinINet and WinHTTP errors of a network that is not there: 12007 name not resolved, 12002 timeout, 12029 cannot connect,
        // 12030 connection aborted, 12031 reset, 12152 and 12163 no answer or no connection.
        static readonly long[] HResults = { 0x80072EE7, 0x80072EE2, 0x80072EFD, 0x80072EFE, 0x80072EFF, 0x80072F78, 0x80072EE9, 0x80072EFA };

        static string Lower(string s)
        {
            return (s ?? "").ToLowerInvariant();
        }

        // The kind of network failure the text tells of, or null when it tells of none.
        static string Category(string text)
        {
            string t = Lower(text);
            if (t.Length == 0) return null;
            foreach (string[] w in Words)
                for (int k = 1; k < w.Length; k++)
                    if (t.IndexOf(w[k], StringComparison.Ordinal) >= 0) return w[0];
            return null;
        }

        // An upload error the network is the reason for: the agent's "Could not reach <host>: ..." or one of the known texts.
        public static bool IsNetworkError(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.StartsWith("Could not reach ", StringComparison.OrdinalIgnoreCase) || Category(message) != null;
        }

        // Name resolution, could not connect, connection closed, timeout, or other.
        public static string Reason(string message)
        {
            return Category(message) ?? Other;
        }

        // An error text or code of an item that can be put down to a missing network, for example a script that could not resolve a name.
        public static bool IsNetworkFailure(string text, long? exitCode)
        {
            if (Category(text) != null) return true;
            if (exitCode.HasValue)
            {
                long c = exitCode.Value & 0xFFFFFFFFL;
                foreach (long h in HResults) if (c == h) return true;
            }
            return false;
        }

        // "19:50:23-19:50:26 (3 s)", in local time.
        public static string Span(NetOutage o)
        {
            return Txt.LocalTime(o.FromUtc) + "–" + Txt.LocalTime(o.ToUtc) + " (" + Txt.Duration(o.Length) + ")";
        }

        public static string EventText(NetOutage o)
        {
            return "No network to the storage: " + Span(o) + ", " + (o.Reason ?? Other);
        }

        // The outage a failure at this time is near, or null. The failure counts when it falls inside the outage or within Near of it.
        public static NetOutage Find(IList<NetOutage> outages, DateTime failedUtc)
        {
            if (outages == null) return null;
            // In ticks, so a time at either end of the calendar in an uploaded session does not overflow.
            long at = failedUtc.Ticks, near = Near.Ticks;
            foreach (NetOutage o in outages)
                if (o != null && at + near >= o.FromUtc.Ticks && at - near <= o.ToUtc.Ticks) return o;
            return null;
        }

        // The line for the details of a failed item: "The device had no network then (see 19:50:23-19:50:26)", or null.
        public static string NearFailure(IList<NetOutage> outages, DateTime failedUtc, string errorText, long? exitCode)
        {
            if (!IsNetworkFailure(errorText, exitCode)) return null;
            NetOutage o = Find(outages, failedUtc);
            if (o == null) return null;
            return "The device had no network then (see " + Txt.LocalTime(o.FromUtc) + "–" + Txt.LocalTime(o.ToUtc) + ")";
        }

        // The same for an item of the session.
        public static string NearItem(Session s, Item i)
        {
            if (s == null || i == null || s.Outages == null || s.Outages.Count == 0 || i.State != State.Failed) return null;
            DateTime at = i.EndUtc ?? (i.StateUtc > DateTime.MinValue ? i.StateUtc : i.LastUtc);
            StringBuilder text = new StringBuilder();
            if (i.ExitCode.HasValue && i.ExitCode.Value != 0) text.Append(Errors.Describe(i.ExitCode.Value)).Append(' ');
            foreach (TEvent e in i.Events) if (e.Severity >= 1 && e.Text != null) text.Append(e.Text).Append(' ');
            foreach (Pair p in i.Info) if (p.Value != null) text.Append(p.Value).Append(' ');
            return NearFailure(s.Outages, at, text.ToString(), i.ExitCode);
        }

        // The outages as events for the timeline, in place of earlier ones, in time order.
        public static void AddEvents(Session s)
        {
            if (s == null) return;
            s.Events.RemoveAll(delegate (TEvent e) { return e.Source == EventSource; });
            if (s.Outages == null) return;
            // An uploaded session is read as it is: more outages than an agent keeps are not from an agent.
            s.Outages.RemoveAll(delegate (NetOutage o) { return o == null; });
            if (s.Outages.Count > OutageRecorder.Max) s.Outages.RemoveRange(0, s.Outages.Count - OutageRecorder.Max);
            foreach (NetOutage o in s.Outages)
            {
                TEvent e = new TEvent();
                e.TimeUtc = o.FromUtc;
                e.Source = EventSource;
                e.Severity = 1;
                e.Text = EventText(o);
                int at = s.Events.Count;
                while (at > 0 && s.Events[at - 1].TimeUtc > e.TimeUtc) at--;
                s.Events.Insert(at, e);
            }
        }

        // Made-up outages for the demo devices of the agent: two on one device, one short on another.
        public static void Demo(Session s, string computer, DateTime nowUtc)
        {
            if (s == null) return;
            string c = (computer ?? "").ToUpperInvariant();
            if (c.EndsWith("LT0417")) { Demo(s, nowUtc.AddMinutes(-9), 6, ConnectionClosed, 2); Demo(s, nowUtc.AddMinutes(-4), 38, NameResolution, 9); }
            else if (c.EndsWith("PC117")) Demo(s, nowUtc.AddMinutes(-6), 3, ConnectionClosed, 1);
        }

        static void Demo(Session s, DateTime fromUtc, int seconds, string reason, int failures)
        {
            NetOutage o = new NetOutage();
            o.FromUtc = fromUtc;
            o.ToUtc = fromUtc.AddSeconds(seconds);
            o.Reason = reason;
            o.Failures = failures;
            s.Outages.Add(o);
        }

        // "2 (longest 40 s)", or null when there are none.
        public static string Summary(Session s)
        {
            if (s == null || s.Outages == null || s.Outages.Count == 0) return null;
            TimeSpan max = TimeSpan.Zero;
            foreach (NetOutage o in s.Outages) if (o.Length > max) max = o.Length;
            return s.Outages.Count.ToString(CultureInfo.InvariantCulture) + " (longest " + Txt.Duration(max) + ")";
        }
    }
}
