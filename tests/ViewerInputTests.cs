namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Text;

    // What the viewer reads from storage is written by whoever holds an upload link: sessions, their metadata and the
    // Graph answers about devices are treated as hostile, and the local history stays small whatever they say.
    public static class ViewerInputTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Bombs();
            Depth();
            Texts();
            Counts();
            Listing();
            Wall();
            Graph();
            History();
        }

        static byte[] Gzip(byte[] raw)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (Exception) { return true; }
        }

        static void Bombs()
        {
            int was = Session.MaxJsonBytes;
            try
            {
                Session.MaxJsonBytes = 1024 * 1024;
                // A session padded with spaces: 3 MB of JSON in a few KB of gzip.
                byte[] json = Encoding.UTF8.GetBytes("{\"Computer\":\"PC1\"" + new string(' ', 3 * 1024 * 1024) + "}");
                byte[] packed = Gzip(json);
                T.Check(packed.Length < 64 * 1024, "input: the bomb is small when packed");
                string why = null;
                try { AgentData.Unpack(packed); }
                catch (StoreException ex) { why = ex.Message; }
                T.Check(why != null && why.Contains("larger than"), "input: an upload that unpacks past the limit is refused (" + why + ")");
                T.Check(Throws(delegate { Session.FromJson(Encoding.UTF8.GetString(json)); }), "input: JSON past the limit is refused");
                string path = Path.Combine(Path.GetTempPath(), "tv-s10-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllBytes(path, json);
                    T.Check(Throws(delegate { Session.Load(path); }), "input: a session file past the limit is refused");
                }
                finally { File.Delete(path); }
                // Within the limit it still reads.
                Session ok = AgentData.Unpack(Gzip(Encoding.UTF8.GetBytes("{\"Computer\":\"PC1\"" + new string(' ', 1000) + "}")));
                T.Eq("PC1", ok.Computer, "input: a session within the limit is read");
            }
            finally { Session.MaxJsonBytes = was; }
            T.Check(Throws(delegate { Session.FromJson("null"); }), "input: null is no session");
        }

        static void Depth()
        {
            string deep = "{\"Computer\":\"PC1\",\"Extra\":" + new string('[', 500) + new string(']', 500) + "}";
            T.Check(Throws(delegate { Session.FromJson(deep); }), "input: JSON nested far deeper than a session is refused");
            string fine = "{\"Computer\":\"PC1\",\"Items\":[{\"Key\":\"a\",\"Attempts\":[{\"Index\":1,\"Command\":\"x\"}],\"Info\":[{\"Key\":\"k\",\"Value\":\"v\"}]}],"
                + "\"Events\":[{\"ItemKey\":\"a\",\"Text\":\"t\",\"Evidence\":{\"Kind\":\"Log line\",\"Path\":\"C:\\\\x.log\",\"Line\":3}}]}";
            Session s = Session.FromJson(fine);
            T.Check(s.Items.Count == 1 && s.Events.Count == 1 && s.Events[0].Evidence.Line == 3, "input: a normal session is as deep as it needs");
        }

        static void Texts()
        {
            string name = "PC\u202Egpj.exe";
            string json = "{\"Computer\":\"" + Esc(name) + "\",\"User\":\"a\\u0001b\\u200Bc\",\"Items\":[null,{\"Key\":\"\"},{\"Key\":\"k1\",\"Name\":\"" + new string('N', 5000)
                + "\",\"Percent\":-40,\"StateUtc\":\"9999-12-31T23:59:59.999Z\",\"Attempts\":[null]}],\"Events\":[null,{\"ItemKey\":\"k1\",\"Text\":\"line1\\nline2\\u202E\"}],"
                + "\"Findings\":[null],\"Notes\":[null,\"n\"],\"Info\":[null,{\"Key\":\"Tenant\",\"Value\":\"x\\u2066y\"}],\"Outages\":[null]}";
            Session s = Session.FromJson(json);
            T.Eq("PCgpj.exe", s.Computer, "input: text direction overrides are taken out of the device name");
            T.Eq("a bc", s.User, "input: control characters become spaces and zero-width spaces go");
            T.Eq(1, s.Items.Count, "input: items that are missing or have no key are left out");
            Item i = s.Items[0];
            T.Check(i.Name.Length == Session.MaxName && i.Name.EndsWith("\u2026"), "input: an absurdly long name is cut");
            T.Eq((int?)0, i.Percent, "input: a percent below 0 is 0");
            T.Check(i.StateUtc.Year <= 9000, "input: a time at the end of the calendar is moved back");
            T.Check(i.StateUtc + TimeSpan.FromDays(3650) > i.StateUtc, "input: a time can be counted on from");
            T.Eq(0, i.Attempts.Count, "input: missing attempts are left out");
            T.Eq(1, s.Events.Count, "input: missing events are left out");
            T.Eq("line1\nline2", s.Events[0].Text, "input: event text keeps its lines but not the overrides");
            T.Eq(1, i.Events.Count, "input: the event still belongs to its item");
            T.Eq(0, s.Findings.Count, "input: missing findings are left out");
            T.Eq(1, s.Notes.Count, "input: missing notes are left out");
            T.Eq("xy", s.GetInfo("Tenant"), "input: information values are cleaned");
            T.Eq(0, s.Outages.Count, "input: missing outages are left out");
            // Clean on its own.
            T.Eq(null, Txt.Clean(null, 10, false), "input: null stays null");
            T.Eq("abc", Txt.Clean("abc", 10, false), "input: clean text is kept");
            T.Eq("ab\u2026", Txt.Clean("abcdef", 3, false), "input: text is cut with an ellipsis");
            T.Eq("a\u2026", Txt.Clean("a\uD83D\uDE00bc", 3, false), "input: a cut does not leave half a character");
            T.Eq("a b", Txt.Clean("a\tb", 10, false), "input: a tab in a name is a space");
            T.Eq("a\tb", Txt.Clean("a\tb", 10, true), "input: a tab in text is kept");
        }

        static string Esc(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s)
                if (c < 0x20 || c > 0x7E) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            return b.ToString();
        }

        static void Counts()
        {
            StringBuilder b = new StringBuilder("{\"Computer\":\"PC1\",\"Items\":[");
            for (int k = 0; k < Session.MaxItems + 50; k++) b.Append(k == 0 ? "" : ",").Append("{\"Key\":\"i").Append(k.ToString(CultureInfo.InvariantCulture)).Append("\"}");
            b.Append("],\"Notes\":[");
            for (int k = 0; k < Session.MaxEntries + 10; k++) b.Append(k == 0 ? "" : ",").Append("\"n\"");
            b.Append("]}");
            Session s = Session.FromJson(b.ToString());
            T.Eq(Session.MaxItems, s.Items.Count, "input: a session keeps at most MaxItems items");
            T.Eq("i0", s.Items[0].Key, "input: the first items are kept");
            T.Eq(Session.MaxEntries, s.Notes.Count, "input: a session keeps at most MaxEntries notes");
        }

        static StoreEntry Entry(string name)
        {
            StoreEntry e = new StoreEntry();
            e.Name = name;
            e.ModifiedUtc = Now.AddMinutes(-1);
            return e;
        }

        static void Listing()
        {
            StoreEntry e = Entry("sessions/abc.json.gz");
            e.Meta["computer"] = "EVIL\u202E\r\nPC";
            e.Meta["profile"] = new string('p', 3000);
            e.Meta["current"] = "Installing \u2067x";
            e.Meta["items"] = "3";
            e.Meta["active"] = "1";
            e.Meta["started"] = "9999-12-31T23:59:59Z";
            e.Meta["updated"] = "9999-12-31T23:59:59Z";
            e.Meta["version"] = "2.1.0\u202E";
            RemoteDeployment d = AgentData.ToDeployment(e, Now);
            // AgentData.ListText turns each odd character into a space, then RemoteDeployment.Clean cuts the text.
            T.Check(d.Device.StartsWith("EVIL", StringComparison.Ordinal) && d.Device.EndsWith("PC", StringComparison.Ordinal) && d.Device.IndexOf('\u202E') < 0 && d.Device.IndexOf('\n') < 0 && d.Device.IndexOf('\r') < 0, "input: the device name from the metadata is cleaned");
            T.Check(d.Profile.Length > 0 && d.Profile.Length <= Session.MaxName, "input: the profile from the metadata is cut");
            T.Check(d.StateText.IndexOf('\u2067') < 0, "input: the state text has no isolates");
            T.Eq("2.1.0", d.Version, "input: the version is cleaned");
            T.Check(d.LastUtc.Year <= 9000 && d.StartUtc.Value.Year <= 9000, "input: the times from the metadata stay in the calendar");
            T.Eq("sessions/abc.json.gz", d.Key, "input: the key still names the upload");
            // The same for Autopilot events from Graph, where the device name is the device's own.
            Dictionary<string, object> ev = new Dictionary<string, object>();
            ev["id"] = "e1";
            ev["managedDeviceName"] = "LT\u202E01";
            ev["eventDateTime"] = "9999-12-31T23:59:59Z";
            ev["deploymentState"] = "inProgress";
            ev["deviceSetupStatus"] = "success";
            ev["deviceSetupDuration"] = "P99999D";
            RemoteDeployment g = IntuneCatalog.ToDeployment(ev);
            T.Eq("LT01", g.Device, "input: a device name from Intune is cleaned");
            T.Check(g.LastUtc.Year <= 9000, "input: a time from Intune stays in the calendar");
            T.Eq((TimeSpan?)null, IntuneFeed.Dur("P99999D"), "input: a duration of centuries is not taken");
            T.Eq((TimeSpan?)TimeSpan.FromDays(2), IntuneFeed.Dur("P2D"), "input: a duration of days is taken");
            T.Check(!Throws(delegate { IntuneFeed.Map(ev, new List<Dictionary<string, object>>(), Now, new SnapshotDiff()); }), "input: an event with absurd times maps without an error");
        }

        static void Wall()
        {
            StoreEntry e = Entry("sessions/w1.json.gz");
            e.Meta["computer"] = "W\u202E1";
            e.Meta["items"] = "-5";
            e.Meta["done"] = "-9";
            e.Meta["failed"] = "-1";
            e.Meta["updated"] = "9999-12-31T23:59:59Z";
            WallTile t = WallModel.Tile(e, Now);
            T.Eq("W1", t.Device, "input: the wall cleans the device name");
            T.Check(t.Items == 0 && t.Done == 0 && t.FailedCount == 0, "input: the wall takes no counts below zero");
            T.Check(t.LastUtc.Year <= 9000, "input: the wall keeps times in the calendar");
            StoreEntry noName = Entry("sessions/a\"b<c>|.json.gz");
            T.Eq("a\"b<c>|.json.gz", WallModel.Tile(noName, Now).Device, "input: a blob name with odd characters still names the tile");
            // Finished devices that claim to have started in year 1: the average does not run over.
            List<WallTile> tiles = new List<WallTile>();
            for (int k = 0; k < 20; k++)
            {
                StoreEntry f = Entry("sessions/f" + k + ".json.gz");
                f.Meta["items"] = "2";
                f.Meta["done"] = "2";
                f.Meta["started"] = "0001-01-01T00:00:00Z";
                f.Meta["ended"] = Now.AddMinutes(-5).ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
                f.Meta["updated"] = f.Meta["ended"];
                tiles.Add(WallModel.Tile(f, Now));
            }
            WallSummary s = WallModel.Summarize(tiles, Now, Now.Date);
            T.Eq(20, s.FinishedToday, "input: the finished devices are counted");
            T.Check(s.Average.HasValue && s.Average.Value > TimeSpan.FromDays(365 * 1000), "input: the average of absurd times does not run over into a negative (" + s.Average + ")");
        }

        static void Graph()
        {
            GraphClient g = GraphClient.Resume(null, "contoso.com", null, "rt-1", "admin@contoso.com");
            string why = null;
            try { g.Get("https://evil.example.com/v1.0/devices?$skiptoken=x"); }
            catch (GraphException ex) { why = ex.Message; }
            T.Check(why != null && why.Contains("only sends"), "input: the token is not sent to a link outside Graph (" + why + ")");
            T.Check(GraphClient.IsGraphUrl("https://graph.microsoft.com/beta/deviceManagement/autopilotEvents?$skiptoken=1"), "input: a Graph link is followed");
            T.Check(!GraphClient.IsGraphUrl("http://graph.microsoft.com/beta/x"), "input: a Graph link without https is not");
            T.Check(!GraphClient.IsGraphUrl("https://graph.microsoft.com.evil.example/beta/x"), "input: a look-alike host is not");
            T.Check(!GraphClient.IsGraphUrl("https://user@graph.microsoft.com/beta/x"), "input: a link with a user is not");
            T.Check(!GraphClient.IsGraphUrl("https://graph.microsoft.com:8443/beta/x"), "input: a link with another port is not");
            T.Eq("https://microsoft.com/devicelogin", GraphClient.SafeVerificationUri("https://microsoft.com/devicelogin"), "input: Microsoft's sign-in page is opened");
            T.Eq("https://login.microsoftonline.com/common/oauth2/deviceauth", GraphClient.SafeVerificationUri("https://login.microsoftonline.com/common/oauth2/deviceauth"), "input: a sign-in page of Entra is opened");
            foreach (string bad in new string[] { "file:///C:/Windows/System32/calc.exe", "C:\\Windows\\System32\\calc.exe", "https://evil.example/devicelogin", "http://microsoft.com/devicelogin",
                "https://microsoft.com.evil.example/", "https://notmicrosoft.com/", "\\\\server\\share\\x.exe", null, "" })
                T.Eq(GraphClient.DefaultVerificationUri, GraphClient.SafeVerificationUri(bad), "input: no other page or program is opened for " + (bad ?? "null"));
        }

        static HistoryRun Run(string profile, DateTime end, double total)
        {
            HistoryRun r = new HistoryRun();
            r.Id = "pc|" + profile + "|" + end.Ticks;
            r.Customer = "Contoso";
            r.Kind = "Autopilot";
            r.Profile = profile;
            r.EndUtc = end;
            r.Total = total;
            return r;
        }

        static void History()
        {
            DeploymentHistory h = new DeploymentHistory();
            for (int k = 0; k < DeploymentHistory.MaxKeys + 40; k++) h.Add(Run("made-up " + k, Now.AddMinutes(k), 1800));
            T.Eq(DeploymentHistory.MaxKeys, h.Count, "input: the history keeps at most MaxKeys profiles");
            T.Check(h.Get("Contoso", "Autopilot", "made-up " + (DeploymentHistory.MaxKeys + 39)) != null, "input: the newest profile is kept");
            string kept = h.ToJson();
            T.Check(!kept.Contains("\"made-up 0\"") && kept.Contains("\"made-up 40\""), "input: the oldest profiles made way");
            T.Check(!h.Add(Run("too long", Now, 1e300)), "input: a run of absurd length is not kept");
            T.Check(!h.Add(Run("nan", Now, double.NaN)), "input: a run without a number is not kept");
            HistoryRun big = Run("big", Now.AddDays(1), 1800);
            for (int k = 0; k < DeploymentHistory.MaxValues + 500; k++)
            {
                HistoryValue v = new HistoryValue();
                v.Key = "Win32 app|App " + k;
                v.Seconds = k == 0 ? double.PositiveInfinity : 60;
                big.Items.Add(v);
            }
            T.Check(h.Add(big), "input: a big run is kept");
            T.Eq(DeploymentHistory.MaxValues, big.Items.Count, "input: a run keeps at most MaxValues items");
            T.Check(!big.Items.Exists(delegate (HistoryValue v) { return double.IsInfinity(v.Seconds); }), "input: an item time that is no number is left out");
            // A history.json someone edited by hand.
            string json = h.ToJson().Replace("\"Total\":1800", "\"Total\":-5");
            DeploymentHistory back = DeploymentHistory.FromJson(json);
            T.Eq(0, back.Count, "input: runs with a negative total are not read");
            string path = Path.Combine(Path.GetTempPath(), "tv-s10-history-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using (FileStream fs = File.Create(path)) fs.SetLength(DeploymentHistory.MaxFileBytes + 1);
                T.Eq(0, DeploymentHistory.Load(path).Count, "input: a history.json past its limit is not read");
            }
            finally { File.Delete(path); }
        }
    }
}
