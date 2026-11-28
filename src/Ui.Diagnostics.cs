namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Windows.Forms;

    // A request this PC made, kept while the window of the device is open, so the progress is there again when the
    // dialog is opened again.
    public sealed class DiagnosticsAsked
    {
        public string Id = "";
        public DateTime Utc;
    }

    // What the dialog needs from the window, so the same dialog serves the viewer, the demo and screenshots.
    public sealed class DiagnosticsHooks
    {
        public string Computer = "", AgentVersion = "";
        // Where the request is written, in words.
        public string Place = "";
        // Why nothing can be asked, or null.
        public string Blocked;
        public bool Demo;
        // The request made last from this window, or null.
        public DiagnosticsAsked Asked;
        public Action<DiagnosticsAsked> Remember;
        // Signs and writes the request. Runs on a worker thread and throws with a sentence for the person.
        public Func<DiagnosticsRequest> Send;
        // The session as last read from the device's upload.
        public Func<Session> Session;
        // Downloads and unpacks the bundle of a request (worker thread), or null when it is not there.
        public Func<string, DeviceLogCopy> Download;
        // The zip as uploaded (worker thread), or null.
        public Func<string, byte[]> GetZip;
        public Action<DeviceLogCopy> OpenInLogVice;
        public Action<string> Say;
    }

    // Collect diagnostics: explains what is collected, writes the request and follows it from Requested to Uploaded.
    public sealed class DiagnosticsDialog : PageForm
    {
        readonly DiagnosticsHooks h;
        readonly FlatButton bGo, bOpen, bSave;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly ThinBar bar = new ThinBar();
        // The request that is followed: one made here, or the newest the device reports.
        string requestId;
        DateTime? askedUtc;
        string error, shown = "";
        bool sending, busyFiles;
        DeviceLogCopy copy;
        // Only for screenshots: the step to show.
        int demoStep = -1;

        public DiagnosticsDialog(Theme t, DiagnosticsHooks hooks) : base("Collect diagnostics", t)
        {
            h = hooks;
            Size = new Size(Dpi.S(720), Dpi.S(640));
            bClose.Text = "Close";
            bClose.AutoWidth();
            bClose.Width = Math.Max(bClose.Width + Dpi.S(8), Dpi.S(96));
            bClose.Primary = false;
            bClose.ApplyTheme(t);
            // Docked from the right: Close, then Save zip, Open in LogVice, and Collect diagnostics.
            bSave = Footer("Save zip", false, delegate { SaveZip(); });
            bOpen = Footer("Open in LogVice", true, delegate { OpenCopy(); });
            bGo = Footer("Collect diagnostics", true, delegate { Ask(); });
            tick.Interval = 1000;
            tick.Tick += delegate { Safe.Run(Refresh2); };
            FormClosed += delegate { tick.Stop(); tick.Dispose(); };
            if (h.Asked != null)
            {
                requestId = h.Asked.Id;
                askedUtc = h.Asked.Utc;
            }
            else if (h.Blocked == null)
            {
                // Nothing asked from this window: what the device reports last, such as a request made on another PC.
                List<DiagnosticsEntry> l = Entries();
                // The session is read from the storage: an id that is not one of a request is not followed.
                if (l.Count > 0 && DiagnosticsRequest.IsRequestId(l[l.Count - 1].Id)) requestId = l[l.Count - 1].Id;
            }
            Build();
            if (requestId != null) tick.Start();
        }

        FlatButton Footer(string text, bool primary, MethodInvoker click)
        {
            FlatButton b = new FlatButton(text, null);
            b.Primary = primary;
            b.AutoWidth();
            b.Width = Math.Max(b.Width + Dpi.S(8), Dpi.S(96));
            b.Dock = DockStyle.Right;
            b.Click += Safe.Wrap(delegate { click(); });
            b.ApplyTheme(theme);
            Panel gap = new Panel();
            gap.Width = Dpi.S(8);
            gap.Dock = DockStyle.Right;
            footer.Controls.Add(gap);
            footer.Controls.Add(b);
            gap.BringToFront();
            b.BringToFront();
            return b;
        }

        void Ui(MethodInvoker a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        List<DiagnosticsEntry> Entries()
        {
            Session s = null;
            try { s = h.Session != null ? h.Session() : null; }
            catch (Exception) { }
            return DiagnosticsStatus.Of(s);
        }

        // The request's entry in the session, or made up in the demo.
        DiagnosticsEntry Entry()
        {
            if (requestId == null) return null;
            if (h.Demo)
            {
                double s = askedUtc.HasValue ? (DateTime.UtcNow - askedUtc.Value).TotalSeconds : 0;
                int step = demoStep >= 0 ? demoStep : s < 2 ? 0 : s < 5 ? 1 : s < 9 ? 2 : 3;
                if (step == 0) return null;
                DiagnosticsEntry d = new DiagnosticsEntry();
                d.Id = requestId;
                d.Utc = (askedUtc ?? DateTime.UtcNow).AddSeconds(demoStep >= 0 ? 40 * step : Math.Min(s, 9));
                d.State = step == 1 ? DiagnosticsStatus.Received : step == 2 ? DiagnosticsStatus.Collecting : DiagnosticsStatus.Uploaded;
                d.Blob = DiagnosticsRequest.BundleName("demo", requestId);
                return d;
            }
            foreach (DiagnosticsEntry e in Entries()) if (string.Equals(e.Id, requestId, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // Asked here, never read by the agent, and no longer valid.
        bool Expired(DiagnosticsEntry e)
        {
            return requestId != null && e == null && askedUtc.HasValue && DateTime.UtcNow - askedUtc.Value >= DiagnosticsRequest.Lifetime;
        }

        void Refresh2()
        {
            if (requestId == null) return;
            DiagnosticsEntry e = Entry();
            // The wait is shown in whole minutes, so the page is only made again when something changes.
            int waited = e == null && askedUtc.HasValue ? (int)(DateTime.UtcNow - askedUtc.Value).TotalMinutes : 0;
            string stamp = e == null ? "w" + waited : e.State + "|" + e.Note + "|" + e.Utc.Ticks + "|" + e.Blob;
            if (stamp == shown) return;
            shown = stamp;
            Build();
            if (Expired(e) || (e != null && (e.State == DiagnosticsStatus.Uploaded || e.State == DiagnosticsStatus.Failed))) tick.Stop();
        }

        void Ask()
        {
            if (sending || h.Blocked != null) return;
            sending = true;
            error = null;
            Build();
            ThreadPool.QueueUserWorkItem(delegate
            {
                DiagnosticsRequest r = null;
                string err = null;
                try { r = h.Send(); }
                catch (Exception ex) { err = ex.Message; }
                Ui(delegate
                {
                    sending = false;
                    error = err;
                    if (r != null)
                    {
                        requestId = r.RequestId;
                        askedUtc = DateTime.UtcNow;
                        DiagnosticsAsked a = new DiagnosticsAsked();
                        a.Id = r.RequestId;
                        a.Utc = askedUtc.Value;
                        if (h.Remember != null) h.Remember(a);
                        copy = null;
                        shown = "";
                        tick.Start();
                    }
                    Build();
                });
            });
        }

        static string When(DateTime utc)
        {
            return utc.Year > 2000 ? Txt.LocalTime(utc) : "";
        }

        void Build()
        {
            ClearItems();
            Add(new SectionTitle("Collect diagnostics"));
            DiagnosticsEntry e = Entry();
            bool expired = Expired(e);
            int step = requestId == null ? 0 : DiagnosticsStatus.Step(e);
            bool failed = step < 0 || expired;
            bool done = step == 3;
            bool working = requestId != null && !failed && !done;
            bGo.Visible = h.Blocked == null && !sending && !working;
            bGo.Text = requestId == null ? "Collect diagnostics" : "Collect again";
            // One main button: the diagnostics that are there come before asking for new ones.
            if (bGo.Primary == done)
            {
                bGo.Primary = !done;
                bGo.ApplyTheme(theme);
            }
            bGo.AutoWidth();
            bGo.Width = Math.Max(bGo.Width + Dpi.S(8), Dpi.S(96));
            bOpen.Visible = done;
            bSave.Visible = done;
            bOpen.Enabled = bSave.Enabled = !busyFiles;
            if (h.Blocked != null)
            {
                Add(Row(Glyphs.Info, "Not possible for " + h.Computer, h.Blocked));
                Add(Row(Glyphs.Log, "What it collects", DiagnosticsSupport.WhatIsCollected()));
                Relayout();
                return;
            }
            if (error != null) Add(Row(Glyphs.Info, "The request was not made", error));
            if (requestId == null)
            {
                Add(Row(Glyphs.Log, "Diagnostics from " + h.Computer, DiagnosticsSupport.WhatIsCollected()));
                Add(Row(Glyphs.Shield, "How the request reaches the device", "TrackVice signs the request with the update key of this PC and writes it to " + (h.Place.Length > 0 ? h.Place : "the place the agents of the customer read updates from") + ". The agent only reads from there and uploads to its storage, so nothing connects to the device. It follows a request once, only when it is signed with its update key and made for this device, and it is valid for 2 hours. The request holds no commands: the agent runs only the tools above."));
                if (sending) Add(Row(Glyphs2.Sync, "Writing the request", "Signing it and writing it for the agent…"));
                else Add(Row(Glyphs2.Clock, "How long it takes", DiagnosticsSupport.WhenItIsRead() + " Collecting takes a few minutes."));
                Relayout();
                return;
            }
            string asked = askedUtc.HasValue ? "Asked at " + When(askedUtc.Value) + ". " : "";
            string title = expired ? "Not read by the agent" : failed ? "Failed" : done ? "Uploaded" : "In progress";
            string text;
            if (expired) text = asked + "The agent did not read the request in the 2 hours it was valid. The device may be off or without network. Collect again when it uploads again.";
            else if (failed) text = e.Note.Length > 0 ? e.Note : "The agent could not collect or upload the diagnostics.";
            else if (done) text = "The agent uploaded the diagnostics at " + When(e.Utc) + ".";
            else text = asked + "You can close this window; the agent goes on.";
            bar.Value = step / 3.0;
            SettingRow head = new SettingRow(failed ? Glyphs.Info : done ? Glyphs.Save : Glyphs2.Sync, title, text, failed ? null : bar);
            head.WrapAccessory = true;
            Add(head);
            if (sending) Add(Row(Glyphs2.Sync, "Writing the request", "Signing it and writing it for the agent…"));
            if (failed)
            {
                Relayout();
                return;
            }
            string[] titles = new string[] { "Requested", "Received by the agent", "Collecting on the device", "Uploaded" };
            string[] now = new string[] { "", DiagnosticsSupport.WhenItIsRead(), "It collects the logs, the MDM diagnostics, dsregcmd and the Intune Management Extension keys. This takes a few minutes.", "" };
            for (int k = 0; k < 4; k++)
            {
                string line;
                if (k == 0) line = askedUtc.HasValue ? "Signed and written at " + When(askedUtc.Value) : "Done";
                else if (k == 3 && done) line = e.Note.Length > 0 ? e.Note : "Done";
                else if (k <= step) line = k == step ? "Now. " + now[k] : "Done";
                else if (k == step + 1 && k < 3) line = "Waiting. " + now[k];
                else line = "Waiting";
                if (k == 1 && step == 0 && askedUtc.HasValue && (DateTime.UtcNow - askedUtc.Value).TotalMinutes >= 3)
                    line = "Not yet, after " + Txt.DurationShort(DateTime.UtcNow - askedUtc.Value) + ". " + now[1] + " A device that is off or without network reads it when it is back, if that is within 2 hours.";
                bool past = k == 0 || k < step || (k == 3 && done);
                Add(Row(past ? Glyphs.Save : k == step ? Glyphs2.Sync : Glyphs2.Clock, titles[k], line));
            }
            Relayout();
        }

        SettingRow Row(string glyph, string title, string text)
        {
            SettingRow r = new SettingRow(glyph, title, text, null);
            r.WrapAccessory = true;
            return r;
        }

        // Downloads the bundle once, then opens it in LogVice.
        void OpenCopy()
        {
            if (requestId == null || busyFiles) return;
            if (copy != null) { h.OpenInLogVice(copy); return; }
            busyFiles = true;
            Build();
            string id = requestId;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DeviceLogCopy c = null;
                string err = null;
                try { c = h.Download(id); }
                catch (Exception ex) { err = ex.Message; }
                Ui(delegate
                {
                    busyFiles = false;
                    Build();
                    if (err != null) { h.Say("The diagnostics of " + h.Computer + " could not be downloaded: " + err); return; }
                    if (c == null) { h.Say(h.Demo ? "The demo has no real diagnostics to open." : "The diagnostics of " + h.Computer + " are not in the storage. They may have been removed there."); return; }
                    if (id == requestId) copy = c;
                    h.OpenInLogVice(c);
                });
            });
        }

        void SaveZip()
        {
            if (requestId == null || busyFiles) return;
            string path;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Zip files (*.zip)|*.zip";
                d.FileName = Clean(h.Computer) + "-diagnostics.zip";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                path = d.FileName;
            }
            busyFiles = true;
            Build();
            string id = requestId;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                bool had = false;
                try
                {
                    byte[] zip = h.GetZip(id);
                    had = zip != null;
                    if (had) File.WriteAllBytes(path, zip);
                }
                catch (Exception ex) { err = ex.Message; }
                Ui(delegate
                {
                    busyFiles = false;
                    Build();
                    h.Say(err != null ? "The zip could not be saved: " + err : !had ? (h.Demo ? "The demo has no real diagnostics to save." : "The diagnostics of " + h.Computer + " are not in the storage. They may have been removed there.") : "Saved " + path);
                });
            });
        }

        static string Clean(string s)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (char ch in s ?? "") b.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            return b.Length > 0 ? b.ToString() : "device";
        }

        // Made-up device, for screenshots. page: diagnostics, diagnostics-requested, diagnostics-running, diagnostics-done,
        // diagnostics-failed, diagnostics-old.
        public static DiagnosticsDialog ForShot(Theme t, string page)
        {
            DiagnosticsHooks h = new DiagnosticsHooks();
            h.Computer = "CONTOSO-LT0417";
            h.AgentVersion = Program.Version;
            h.Demo = true;
            h.Place = "Your storage (contosoagents/trackvice-agent)";
            h.Say = delegate (string s) { };
            h.Send = delegate { return DiagnosticsRequest.Create("demo", DateTime.UtcNow); };
            h.Session = delegate { return null; };
            if (page == "diagnostics-old") h.Blocked = DiagnosticsSupport.AgentGate("2.0.0", Program.Version);
            int step = page == "diagnostics-requested" ? 0 : page == "diagnostics-running" ? 2 : page == "diagnostics-done" ? 3 : -1;
            if (step >= 0 || page == "diagnostics-failed")
            {
                h.Asked = new DiagnosticsAsked();
                h.Asked.Id = Guid.NewGuid().ToString("D");
                h.Asked.Utc = DateTime.UtcNow.AddMinutes(step == 0 ? -4 : -3);
            }
            if (page == "diagnostics-failed")
            {
                h.Demo = false;
                Session s = new Session();
                List<DiagnosticsEntry> l = new List<DiagnosticsEntry>();
                DiagnosticsEntry e = new DiagnosticsEntry();
                e.Id = h.Asked.Id;
                e.State = DiagnosticsStatus.Failed;
                e.Utc = DateTime.UtcNow;
                e.Note = "The upload was refused: the upload link of the device has expired.";
                l.Add(e);
                DiagnosticsStatus.Write(s, l);
                h.Session = delegate { return s; };
            }
            DiagnosticsDialog d = new DiagnosticsDialog(t, h);
            if (step >= 0)
            {
                d.demoStep = step;
                d.tick.Stop();
                d.Build();
            }
            return d;
        }
    }

    public sealed partial class MainForm
    {
        DiagnosticsDialog diagnostics;
        FlatButton bDiagnostics;
        // The request made last for each session, while TrackVice runs.
        readonly Dictionary<string, DiagnosticsAsked> diagnosticsAsked = new Dictionary<string, DiagnosticsAsked>(StringComparer.Ordinal);

        // The button beside Copy for ticket. Only shown on a device with the TrackVice agent.
        void BuildDiagnosticsButton()
        {
            bDiagnostics = SupportButton("Collect diagnostics", Glyphs.Log, delegate { CollectDiagnostics(); });
            bDiagnostics.Visible = false;
            Tip(bDiagnostics, "Ask the agent on this device for its logs, MDM diagnostics, dsregcmd /status and the Intune Management Extension registry keys");
        }

        bool DiagnosticsButtonShown()
        {
            return engine != null && engine.Mode == Modes.Agent && engine.Remote is AgentFeed;
        }

        // Collect diagnostics for the device in the window: only for a device with the TrackVice agent.
        void CollectDiagnostics()
        {
            Engine e = engine;
            AgentFeed feed = e == null ? null : e.Remote as AgentFeed;
            if (feed == null) { Status("Collect diagnostics is there for devices with the TrackVice agent. Open one from Devices > Devices with the TrackVice agent."); return; }
            if (diagnostics != null && !diagnostics.IsDisposed) diagnostics.Close();
            bool demo = feed.Store is DemoAgentStore;
            string computer = e.S.Computer;
            string sessionId = DiagnosticsRequest.SessionIdOf(feed.Blob);
            string customer = watchedCustomer;
            DiagnosticsHooks h = new DiagnosticsHooks();
            h.Computer = computer;
            h.AgentVersion = e.S.AppVersion;
            h.Demo = demo;
            h.Blocked = demo ? null : DiagnosticsSupport.AgentGate(e.S.AppVersion, Program.Version);
            // The id is part of what is signed. One that an agent does not make is never signed.
            if (h.Blocked == null && !demo && !DiagnosticsRequest.IsSessionId(sessionId)) h.Blocked = "The session of this device has an id the agent does not make, so no request is signed for it.";
            h.Say = delegate (string s) { Status(s); };
            h.Session = delegate { return e.S; };
            DiagnosticsAsked asked;
            if (diagnosticsAsked.TryGetValue(sessionId, out asked)) h.Asked = asked;
            h.Remember = delegate (DiagnosticsAsked a) { diagnosticsAsked[sessionId] = a; };
            UpdateLocation place = null;
            string problem = null;
            if (demo) h.Place = "Your storage (contosoagents/trackvice-agent)";
            else
            {
                place = DiagnosticsSupport.Place(UpdatePlacesUi.Build(), customer, out problem);
                if (place != null) h.Place = place.Long;
            }
            h.Send = delegate
            {
                if (demo) return DiagnosticsRequest.Create(sessionId, DateTime.UtcNow);
                if (place == null) throw new StoreException(problem);
                RSACryptoServiceProvider key = null;
                try { key = UpdateKeys.Load(YourStorageUi.Keys); }
                catch (Exception ex) { throw new StoreException("The update key of this PC could not be read: " + ex.Message); }
                try { return DiagnosticsSupport.Send(place, UpdateLocations.OpenWith(YourStorageUi.Web), key, sessionId, DateTime.UtcNow); }
                finally { if (key != null) key.Dispose(); }
            };
            // The name of the bundle is made here from the session and the request, never taken from what the device reports.
            h.Download = delegate (string id)
            {
                if (!DiagnosticsRequest.IsRequestId(id)) return null;
                return DeviceLogs.Download(new AgentCatalog(feed.Store), DiagnosticsRequest.BundleKey(sessionId, id), computer, DeviceLogs.Root, DateTime.UtcNow);
            };
            h.GetZip = delegate (string id)
            {
                if (!DiagnosticsRequest.IsRequestId(id)) return null;
                return DeviceLogs.GetZip(new AgentCatalog(feed.Store), DiagnosticsRequest.BundleKey(sessionId, id));
            };
            h.OpenInLogVice = delegate (DeviceLogCopy c)
            {
                List<string> files = new List<string>();
                foreach (KeyValuePair<string, string> kv in c.Files)
                {
                    string ext = Path.GetExtension(kv.Value).ToLowerInvariant();
                    if ((ext == ".log" || ext == ".txt" || ext == ".csv") && !kv.Value.EndsWith(LogBundle.IndexName, StringComparison.OrdinalIgnoreCase) && files.Count < 20) files.Add(kv.Value);
                }
                if (files.Count == 0) { Status("The diagnostics are in " + c.Folder + "."); return; }
                openingDeviceCopy = true;
                try { OpenInLogViceAt(0, null, files.ToArray()); }
                finally { openingDeviceCopy = false; }
                Status("The diagnostics of " + computer + " are in " + c.Folder + ". The MDM diagnostics cab is there too.");
            };
            diagnostics = new DiagnosticsDialog(theme, h);
            diagnostics.Show(this);
        }
    }
}
