namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // Network outages the agent sees as failed uploads: recording, merging, saving, and the line for a failure near one.
    public static class NetworkOutageTests
    {
        static readonly DateTime D = new DateTime(2026, 11, 20, 19, 50, 0, DateTimeKind.Utc);
        const string Closed = "Could not reach acct.blob.core.windows.net: The underlying connection was closed: An unexpected error occurred on a send.";
        const string NoName = "Could not reach acct.blob.core.windows.net: The remote name could not be resolved: 'acct.blob.core.windows.net'";
        const string Refused = "The link does not allow TrackVice to write.";

        sealed class Flaky : ISessionStore
        {
            public string Fail;
            public string Title { get { return "flaky"; } }
            public void Put(string name, byte[] data, Dictionary<string, string> meta) { if (Fail != null) throw new StoreException(Fail); }
            public List<StoreEntry> List(string prefix) { return new List<StoreEntry>(); }
            public byte[] Get(string name) { throw new StoreException("none"); }
        }

        public static void Run()
        {
            Recording();
            Merging();
            Reasons();
            ThroughTheAgent();
            Json();
            Near();
            Events();
        }

        static void Recording()
        {
            OutageRecorder r = new OutageRecorder();
            r.Success(D);
            T.Eq(0, r.List.Count, "a working upload is no outage");
            r.Failure(D.AddSeconds(10), Closed);
            T.Check(r.Open, "a failed upload opens an outage");
            T.Eq(0, r.List.Count, "an outage is not saved before uploads work again");
            r.Success(D.AddSeconds(13));
            T.Eq(1, r.List.Count, "the outage is saved when uploads work again");
            T.Eq(D.AddSeconds(10), r.List[0].FromUtc, "it starts at the first failed upload");
            T.Eq(D.AddSeconds(13), r.List[0].ToUtc, "it ends at the first upload that worked");
            T.Eq(NetworkOutages.ConnectionClosed, r.List[0].Reason, "the reason is the closed connection");
            T.Eq(1, r.List[0].Failures, "one failed upload");
            r.Failure(D.AddMinutes(5), Refused);
            r.Success(D.AddMinutes(6));
            T.Eq(1, r.List.Count, "a refused link is not a network outage");
        }

        static void Merging()
        {
            OutageRecorder r = new OutageRecorder();
            r.Failure(D, Closed);
            r.Success(D.AddSeconds(3));
            r.Failure(D.AddSeconds(20), NoName);
            r.Failure(D.AddSeconds(30), NoName);
            r.Success(D.AddSeconds(45));
            T.Eq(1, r.List.Count, "outages less than 30 s apart are one");
            T.Eq(D, r.List[0].FromUtc, "the merged outage starts with the first");
            T.Eq(D.AddSeconds(45), r.List[0].ToUtc, "and ends with the last");
            T.Eq(3, r.List[0].Failures, "with all failed uploads");
            T.Eq(NetworkOutages.ConnectionClosed, r.List[0].Reason, "and the reason of the first");
            r.Failure(D.AddMinutes(5), NoName);
            r.Success(D.AddMinutes(5).AddSeconds(40));
            T.Eq(2, r.List.Count, "a later outage is another");
            T.Eq(NetworkOutages.NameResolution, r.List[1].Reason, "with its own reason");
            T.Eq(40.0, r.List[1].Length.TotalSeconds, "and its length");
            T.Eq(2, r.Snapshot().Count, "the snapshot has them all");
        }

        static void Reasons()
        {
            T.Eq(NetworkOutages.NameResolution, NetworkOutages.Reason("Could not reach x: The remote name could not be resolved: 'x'"), "name could not be resolved");
            T.Eq(NetworkOutages.CouldNotConnect, NetworkOutages.Reason("Could not reach x: Unable to connect to the remote server"), "unable to connect");
            T.Eq(NetworkOutages.Timeout, NetworkOutages.Reason("Could not reach x: The operation has timed out."), "timeout");
            T.Eq(NetworkOutages.ConnectionClosed, NetworkOutages.Reason(Closed), "connection closed");
            T.Eq(NetworkOutages.Other, NetworkOutages.Reason("Could not reach x: something else"), "other");
            T.Check(NetworkOutages.IsNetworkError("Could not reach x: something else"), "any could-not-reach error is a network error");
            T.Check(!NetworkOutages.IsNetworkError(Refused), "a refused link is not");
            T.Check(!NetworkOutages.IsNetworkError(null), "nothing is not");
        }

        static void ThroughTheAgent()
        {
            Flaky st = new Flaky();
            Session s = new Session();
            s.Computer = "PC1";
            Agent a = new Agent(st, "x", D);
            AgentOptions o = new AgentOptions();
            T.Check(a.Cycle(s, D, o, true), "uploads");
            st.Fail = Closed;
            T.Check(!a.Cycle(s, D.AddSeconds(23), o, true), "a failed upload");
            st.Fail = null;
            T.Check(a.Cycle(s, D.AddSeconds(26), o, true), "uploads again");
            T.Eq(1, a.Outages.List.Count, "the agent saw one outage");
            T.Eq(3.0, a.Outages.List[0].Length.TotalSeconds, "of 3 s");
            T.Check(a.Cycle(s, D.AddSeconds(60), o, true), "the next upload");
            T.Eq(1, s.Outages.Count, "carries the outage in the session");
            T.Check(AgentData.Signature(s) != AgentData.Signature(new Session()), "the signature is made of more than the outages");
        }

        static void Json()
        {
            Session s = new Session();
            s.Computer = "PC1";
            NetOutage o = new NetOutage();
            o.FromUtc = D;
            o.ToUtc = D.AddSeconds(3);
            o.Reason = NetworkOutages.ConnectionClosed;
            o.Failures = 2;
            s.Outages.Add(o);
            string json = s.ToJson();
            T.Check(json.Contains("\"Outages\""), "the outages are in the JSON");
            Session b = Session.FromJson(json);
            T.Eq(1, b.Outages.Count, "the outage is read back");
            T.Eq(D, b.Outages[0].FromUtc.ToUniversalTime(), "with its start");
            T.Eq(3.0, b.Outages[0].Length.TotalSeconds, "its length");
            T.Eq(NetworkOutages.ConnectionClosed, b.Outages[0].Reason, "its reason");
            T.Eq(2, b.Outages[0].Failures, "its failures");
            Session pack = AgentData.Unpack(AgentData.Pack(s));
            T.Eq(1, pack.Outages.Count, "they survive the upload format");
            Session old = Session.FromJson("{\"Format\":1,\"Computer\":\"OLD\",\"Items\":[],\"Events\":[]}");
            T.Check(old.Outages != null && old.Outages.Count == 0, "a session without outages has none");
            T.Eq(null, NetworkOutages.Summary(old), "and no summary");
            Session newer = Session.FromJson("{\"Format\":1,\"Computer\":\"X\",\"Unknown\":5,\"Outages\":[{\"FromUtc\":\"2026-11-20T19:50:23.000Z\",\"ToUtc\":\"2026-11-20T19:50:26.000Z\",\"Reason\":\"timeout\",\"Failures\":1,\"More\":1}]}");
            T.Eq(1, newer.Outages.Count, "unknown fields are ignored");
        }

        static void Near()
        {
            List<NetOutage> l = new List<NetOutage>();
            NetOutage o = new NetOutage();
            o.FromUtc = D;
            o.ToUtc = D.AddSeconds(40);
            o.Reason = NetworkOutages.NameResolution;
            l.Add(o);
            string line = NetworkOutages.NearFailure(l, D.AddSeconds(50), "The remote name could not be resolved", null);
            T.Check(line != null && line.StartsWith("The device had no network then (see " + Txt.LocalTime(D) + "–" + Txt.LocalTime(D.AddSeconds(40)) + ")"), "a name failure in the outage gets the line: " + line);
            T.Check(NetworkOutages.NearFailure(l, D.AddSeconds(-25), "unable to connect", null) != null, "30 s before counts");
            T.Check(NetworkOutages.NearFailure(l, D.AddSeconds(69), "unable to connect", null) != null, "30 s after the end counts");
            T.Check(NetworkOutages.NearFailure(l, D.AddSeconds(75), "unable to connect", null) == null, "later does not");
            T.Check(NetworkOutages.NearFailure(l, D.AddSeconds(10), "Access is denied", null) == null, "a failure that is not about the network gets no line");
            T.Check(NetworkOutages.NearFailure(l, D.AddSeconds(10), null, unchecked((int)0x80072EE7)) != null, "a WinINet name error code counts");
            T.Check(NetworkOutages.NearFailure(l, D.AddSeconds(10), null, 0x80072EE7L) != null, "also as an unsigned code");
            T.Check(NetworkOutages.NearFailure(null, D, "unable to connect", null) == null, "no outages, no line");

            Session s = new Session();
            s.Outages.AddRange(l);
            Item i = new Item();
            i.Key = "script1";
            i.Name = "Install tool";
            i.StateName = "Failed";
            i.EndUtc = D.AddSeconds(20);
            i.ExitCode = 1;
            i.Info.Add(new Pair("Error", "Unable to connect to the remote server"));
            T.Check(NetworkOutages.NearItem(s, i) != null, "a failed item near an outage gets the line");
            i.Info.Clear();
            T.Check(NetworkOutages.NearItem(s, i) == null, "without a network error it gets none");
        }

        static void Events()
        {
            Session s = new Session();
            TEvent before = new TEvent();
            before.TimeUtc = D.AddSeconds(-5);
            before.Text = "a";
            TEvent after = new TEvent();
            after.TimeUtc = D.AddSeconds(60);
            after.Text = "b";
            s.Events.Add(before);
            s.Events.Add(after);
            NetOutage o = new NetOutage();
            o.FromUtc = D;
            o.ToUtc = D.AddSeconds(3);
            o.Reason = NetworkOutages.ConnectionClosed;
            s.Outages.Add(o);
            NetworkOutages.AddEvents(s);
            NetworkOutages.AddEvents(s);
            T.Eq(3, s.Events.Count, "the outage is one event, also when added twice");
            T.Eq("No network to the storage: " + Txt.LocalTime(D) + "–" + Txt.LocalTime(D.AddSeconds(3)) + " (3 s), connection closed", s.Events[1].Text, "the text of the event");
            T.Eq(1, s.Events[1].Severity, "it is a warning");
            T.Eq("1 (longest 3 s)", NetworkOutages.Summary(s), "the summary of the session tab");
        }
    }
}
