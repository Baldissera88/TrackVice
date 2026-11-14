namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // The agent's alerts: what AlertWatch finds is sent to the webhook, retried a few times, and remembered in a small
    // file, so a restart does not send the same alert again.
    public sealed class AgentAlerts
    {
        public const int MaxPerDevice = 15;
        public const int MaxTries = 4;
        public const string FileName = "alerts.txt";

        sealed class Pending
        {
            public Alert Alert;
            public int Tries;
            public DateTime NextUtc;
        }

        readonly string statePath, sessionId;
        readonly Action<Alert> send;
        readonly Action<string> log;
        readonly AlertWatch watch;
        readonly List<string> done = new List<string>();
        readonly List<Pending> queue = new List<Pending>();
        int sentCount;
        bool capLogged;
        public bool Mask = true;

        // statePath may be null when nothing is to be remembered, such as /agent /once from a prompt.
        public AgentAlerts(string statePath, string sessionId, DateTime olderIgnoredUtc, Action<Alert> send, Action<string> log)
        {
            this.statePath = statePath;
            this.sessionId = sessionId ?? "";
            this.send = send;
            this.log = log ?? delegate (string s) { };
            Load();
            watch = new AlertWatch(done);
            watch.OlderIgnoredUtc = olderIgnoredUtc;
        }

        public int SentCount
        {
            get { return sentCount; }
        }

        public int PendingCount
        {
            get { return queue.Count; }
        }

        // The file starts with the session id. A file from an earlier session is ignored.
        void Load()
        {
            if (statePath == null) return;
            try
            {
                if (!File.Exists(statePath)) return;
                string[] lines = File.ReadAllLines(statePath, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != "session " + sessionId) return;
                for (int k = 1; k < lines.Length; k++)
                {
                    string l = lines[k];
                    if (l.StartsWith("sent ", StringComparison.Ordinal)) { done.Add(l.Substring(5)); sentCount++; }
                    else if (l.StartsWith("skip ", StringComparison.Ordinal)) done.Add(l.Substring(5));
                }
            }
            catch (Exception ex) { log("Could not read the sent alerts: " + ex.Message); }
        }

        void Remember(string how, string key)
        {
            if (statePath == null) return;
            try
            {
                bool fresh = !File.Exists(statePath);
                if (!fresh)
                {
                    string[] head = File.ReadAllLines(statePath, Encoding.UTF8);
                    fresh = head.Length == 0 || head[0] != "session " + sessionId;
                }
                if (fresh) File.WriteAllText(statePath, "session " + sessionId + "\n", Encoding.UTF8);
                File.AppendAllText(statePath, how + " " + key.Replace('\n', ' ').Replace('\r', ' ') + "\n", Encoding.UTF8);
            }
            catch (Exception ex) { log("Could not save the sent alerts: " + ex.Message); }
        }

        static Alert Masked(Alert a)
        {
            Alert m = new Alert();
            m.Key = a.Key;
            m.Level = a.Level;
            m.Title = Secrets.Mask(a.Title);
            m.Text = Secrets.Mask(a.Text);
            foreach (Pair p in a.Facts) m.Facts.Add(new Pair(p.Key, Secrets.Mask(p.Value)));
            return m;
        }

        // Called at every cycle of the agent. Never throws: a failed send is logged and tried again later.
        public void Cycle(Session s, bool idle, DateTime nowUtc)
        {
            try
            {
                foreach (Alert a in watch.Check(s, idle, nowUtc))
                {
                    Pending p = new Pending();
                    p.Alert = Mask ? Masked(a) : a;
                    p.NextUtc = nowUtc;
                    queue.Add(p);
                }
            }
            catch (Exception ex) { log("Alert check failed: " + ex.Message); }

            for (int k = 0; k < queue.Count; k++)
            {
                Pending p = queue[k];
                if (p.NextUtc > nowUtc) continue;
                if (sentCount >= MaxPerDevice)
                {
                    if (!capLogged) log("Alert not sent: the limit of " + MaxPerDevice + " alerts for this device is reached. " + p.Alert.Title);
                    capLogged = true;
                    Remember("skip", p.Alert.Key);
                    queue.RemoveAt(k--);
                    continue;
                }
                p.Tries++;
                try
                {
                    send(p.Alert);
                    sentCount++;
                    Remember("sent", p.Alert.Key);
                    log("Alert sent: " + p.Alert.Title);
                    queue.RemoveAt(k--);
                }
                catch (Exception ex)
                {
                    if (p.Tries >= MaxTries)
                    {
                        log("Alert not sent after " + p.Tries + " tries, given up: " + p.Alert.Title + ". " + ex.Message);
                        Remember("skip", p.Alert.Key);
                        queue.RemoveAt(k--);
                    }
                    else
                    {
                        // 1, 2, then 4 minutes between tries.
                        p.NextUtc = nowUtc.AddMinutes(1 << (p.Tries - 1));
                        log("Alert not sent, trying again at " + p.NextUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC: " + ex.Message);
                    }
                }
            }
        }

        public static string StatePath(string folder)
        {
            return Path.Combine(folder, FileName);
        }

        // /agent /alert <url> /alert-test: one alert, so the webhook can be checked before devices use it.
        public static int Test(string url, string computer)
        {
            string problem = AlertSender.Problem(url);
            if (problem != null)
            {
                Console.Error.WriteLine("TrackVice: " + problem);
                return 1;
            }
            Alert a = new Alert();
            a.Key = "test";
            a.Level = "good";
            a.Title = computer + ": test alert";
            a.Text = "The TrackVice agent can send alerts to this address. It sends one when something fails and one when a deployment is finished.";
            a.Facts.Add(new Pair("Device", computer));
            a.Facts.Add(new Pair("Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            try
            {
                AlertSender.Send(url, a);
                Console.WriteLine("The test alert was sent.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("TrackVice: " + ex.Message);
                return 1;
            }
        }
    }

    // When the agent uploads the device's logs: at the first failure, when it goes idle after activity, and at the end.
    // Never more often than every 10 minutes.
    public sealed class LogUploads
    {
        public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);

        readonly ISessionStore store;
        readonly string blob;
        readonly Func<Session, byte[]> make;
        readonly Action<string> log;
        bool pending, sawFailure, sawActive, wasIdle;
        DateTime lastUtc = DateTime.MinValue;
        public int Uploads;

        // make returns the zip, or null when there is nothing to send or it is above LogBundle.MaxBytes.
        public LogUploads(ISessionStore store, string sessionBlob, Func<Session, byte[]> make, Action<string> log)
        {
            this.store = store;
            blob = LogBundle.NameFor(sessionBlob);
            this.make = make;
            this.log = log ?? delegate (string s) { };
        }

        public string Name
        {
            get { return blob; }
        }

        // Whether the logs should go up now. Marks the attempt, so a failure waits for the next reason and gap.
        public bool Due(Session s, bool idle, bool final, DateTime nowUtc)
        {
            if (!sawFailure && s.Count(State.Failed) > 0)
            {
                sawFailure = true;
                pending = true;
            }
            if (s.CountActive > 0) sawActive = true;
            if (idle && !wasIdle && sawActive)
            {
                pending = true;
                sawActive = false;
            }
            wasIdle = idle;
            if (final) pending = true;
            if (!pending || nowUtc - lastUtc < MinGap) return false;
            pending = false;
            lastUtc = nowUtc;
            return true;
        }

        // Never throws.
        public bool Cycle(Session s, bool idle, bool final, DateTime nowUtc)
        {
            if (!Due(s, idle, final, nowUtc)) return false;
            try
            {
                byte[] zip = make(s);
                if (zip == null || zip.Length > LogBundle.MaxBytes)
                {
                    log("Logs not uploaded: nothing to send, or more than " + (LogBundle.MaxBytes / (1024 * 1024)) + " MB.");
                    return false;
                }
                store.Put(blob, zip, null);
                Uploads++;
                log("Logs uploaded, " + (zip.Length / 1024) + " KB.");
                return true;
            }
            catch (Exception ex)
            {
                log("Logs not uploaded: " + ex.Message);
                return false;
            }
        }
    }
}
