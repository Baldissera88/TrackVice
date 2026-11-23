namespace TrackVice
{
    using System;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Threading;
    using System.Windows.Forms;

    // A thin progress bar for a row.
    public sealed class ThinBar : FlatControl
    {
        double value;

        public ThinBar()
        {
            Size = new Size(Dpi.S(220), Dpi.S(8));
            TabStop = false;
        }

        public double Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Card);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            StateLook.Bar(g, new RectangleF(0, 0, Width - 1, Height - 1), value, T.Accent, Theme.Mix(T.Card, T.Text3, 0.3f));
        }
    }

    // Asks before LogVice is downloaded, then shows the download with its size and Cancel.
    public sealed class LogViceDialog : PageForm
    {
        public LogViceResult Result;
        // The user chose Find LogVice.exe instead.
        public bool FindChosen;
        // True for screenshots: nothing is asked or downloaded.
        public bool Demo;

        readonly LogViceContext ctx;
        readonly bool open;
        readonly FlatButton bGo, bFind, bPage;
        readonly ThinBar bar = new ThinBar();
        static bool cleanUpHooked;
        LogViceRelease rel;
        LogViceWay way;
        string reason;
        volatile bool cancel;
        bool working, closeAfter;

        public LogViceDialog(Theme t, LogViceContext c, bool open) : base("Get LogVice", t)
        {
            ctx = c;
            this.open = open;
            Size = new Size(Dpi.S(700), Dpi.S(430));
            way = LogViceGet.Way(c, out reason);
            bClose.Text = "Cancel";
            bClose.Primary = false;
            bClose.AutoWidth();
            bClose.Width = Math.Max(bClose.Width + Dpi.S(8), Dpi.S(96));
            bClose.ApplyTheme(t);
            // Docked from the right: Cancel, then Find LogVice.exe, then Release page, then Download and open.
            bFind = Footer("Find LogVice.exe", false, delegate { FindChosen = true; DialogResult = DialogResult.OK; Close(); });
            bPage = Footer("Release page", false, delegate { try { Process.Start(rel != null && rel.Url != null ? rel.Url : LogViceGet.ReleasesUrl); } catch (Exception) { } });
            bGo = Footer(open ? "Download and open" : "Download", true, delegate { Start(); });
            bGo.Enabled = false;
            bPage.Visible = false;
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                // Cancel during the download stops it; the dialog closes when the download has ended.
                if (working) { cancel = true; closeAfter = true; e.Cancel = true; Say("Cancelling", "Stopping the download…", false); }
            };
            if (way == LogViceWay.Blocked || way == LogViceWay.NeedsWindows)
            {
                Card(way == LogViceWay.NeedsWindows ? "This is Windows PE" : "LogVice cannot be downloaded here", reason, false);
                bGo.Visible = false;
            }
            else Card(LogViceGet.Question(null), "Asking GitHub for the latest version of LogVice…", false);
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

        // One card with what is going on, and the progress bar under it while downloading.
        void Card(string title, string text, bool progress)
        {
            ClearItems();
            Add(new SectionTitle("LogVice"));
            SettingRow row = new SettingRow(Glyphs.Log, title, text, progress ? bar : null);
            row.WrapAccessory = true;
            Add(row);
            if (!progress && !working && rel != null && rel.Error == null && (way == LogViceWay.Install || way == LogViceWay.Keep || way == LogViceWay.Temp))
                Add(new SettingRow(Glyphs.Info, "Or find it yourself", "Find LogVice.exe chooses a LogVice that is on this PC or a share. TrackVice remembers it.", null));
            Relayout();
        }

        void Say(string title, string text, bool progress)
        {
            Card(title, text, progress);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Demo || way == LogViceWay.Blocked || way == LogViceWay.NeedsWindows) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                LogViceRelease r = LogViceGet.Latest();
                Ui(delegate { Asked(r); });
            });
        }

        // Shows the question with the version GitHub gave. Nothing has been downloaded yet.
        public void Asked(LogViceRelease r)
        {
            rel = r;
            if (r.Error != null)
            {
                Say("LogVice could not be downloaded", r.Error + " Find LogVice.exe, or download it from the release page.", false);
                bPage.Visible = true;
                return;
            }
            Say(LogViceGet.Question(r), LogViceGet.WhatHappens(way, r, open), false);
            bGo.Enabled = true;
        }

        void Start()
        {
            if (rel == null || rel.Error != null || working) return;
            working = true;
            cancel = false;
            bGo.Enabled = false;
            bFind.Enabled = false;
            Progress(0, 0);
            string work = way == LogViceWay.Temp ? LogViceGet.NewTempFolder() : Path.Combine(AppUpdate.UpdatesRoot, "LogVice-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            IUpdatePrefs prefs = new PrefsUpdateStore();
            LogViceRelease r = rel;
            LogViceContext c = ctx;
            Thread th = new Thread(delegate ()
            {
                LogViceResult res = null;
                string err = null;
                bool stopped = false;
                try { res = LogViceGet.Get(r, c, work, prefs, delegate (long done, long all) { Ui(delegate { Progress(done, all); }); }, delegate { return cancel; }); }
                catch (OperationCanceledException) { stopped = true; }
                catch (LogViceException ex) { err = ex.Message; }
                catch (Exception ex) { err = "LogVice could not be downloaded: " + ex.Message; }
                // The download folder is not needed after the exe was copied or the MSI installed.
                if (res == null || res.Way != LogViceWay.Temp)
                    try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
                Ui(delegate { Ended(res, err, stopped); });
            });
            th.IsBackground = true;
            th.Start();
        }

        public void Progress(long done, long all)
        {
            bGo.Enabled = false;
            bFind.Enabled = false;
            long size = all > 0 ? all : (way == LogViceWay.Install ? SizeOf(rel.Msi) : SizeOf(rel.Exe));
            bar.Value = size > 0 ? (double)done / size : 0;
            string name = way == LogViceWay.Install ? LogViceGet.MsiName : LogViceGet.ExeName;
            string text = done == 0 ? "Starting…" : LogViceGet.Bytes(done) + (size > 0 ? " of " + LogViceGet.Bytes(size) : "");
            Say("Downloading " + name + " " + (rel.Version ?? ""), text + ". Cancel stops the download; nothing is kept.", true);
        }

        static long SizeOf(GitHubAsset a)
        {
            return a == null ? 0 : a.Size;
        }

        void Ended(LogViceResult res, string err, bool stopped)
        {
            working = false;
            bFind.Enabled = true;
            if (stopped)
            {
                if (closeAfter) { Close(); return; }
                bGo.Enabled = true;
                Say("The download was cancelled", "Nothing was kept or installed. " + LogViceGet.WhatHappens(way, rel, open), false);
                return;
            }
            if (err != null)
            {
                Failed(err);
                return;
            }
            Result = res;
            if (res.Way == LogViceWay.Temp && !cleanUpHooked)
            {
                cleanUpHooked = true;
                Application.ApplicationExit += delegate { LogViceGet.CleanUpSession(); };
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        public void Failed(string err)
        {
            working = false;
            Say("LogVice was not downloaded", err, false);
            bPage.Visible = true;
            bGo.Enabled = false;
        }

        void Ui(MethodInvoker a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { Safe.Run(delegate { a(); }); }); } catch (InvalidOperationException) { }
        }

        // A made-up release, for screenshots.
        public static LogViceRelease DemoRelease()
        {
            LogViceRelease r = new LogViceRelease();
            r.Version = "1.5.6";
            r.Url = LogViceGet.ReleasesUrl + "/tag/v1.5.6";
            r.Exe = new GitHubAsset();
            r.Exe.Name = LogViceGet.ExeName;
            r.Exe.Url = LogViceGet.ReleasesUrl + "/download/v1.5.6/LogVice.exe";
            r.Exe.Size = 1843200;
            r.Msi = new GitHubAsset();
            r.Msi.Name = LogViceGet.MsiName;
            r.Msi.Url = LogViceGet.ReleasesUrl + "/download/v1.5.6/LogVice.msi";
            r.Msi.Size = 2211840;
            r.Sums = new GitHubAsset();
            r.Sums.Name = AppUpdate.SumsName;
            r.Sums.Url = LogViceGet.ReleasesUrl + "/download/v1.5.6/SHA256SUMS.txt";
            return r;
        }
    }

    public sealed partial class MainForm
    {
        // What decides how LogVice can be got on this PC now: the same checks as the update gate, read on this thread.
        internal LogViceContext LogViceContextNow()
        {
            RefreshUpdateDeploying();
            UpdateContext u = new UpdateContext();
            u.Window = true;
            u.Screenshot = start.Shot != null;
            u.Deploying = updateDeploying;
            bool system = false;
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent()) system = id.IsSystem;
            }
            catch (Exception) { }
            bool ts = false;
            try
            {
                foreach (string n in new string[] { "TSManager", "TsProgressUI" })
                {
                    Process[] ps = Process.GetProcessesByName(n);
                    if (ps.Length > 0) ts = true;
                    foreach (Process p in ps) p.Dispose();
                }
            }
            catch (Exception) { }
            using (LiveRegistry reg = new LiveRegistry()) UpdateGate.ReadDevice(u, reg, Environment.UserName, system, ts);
            return LogViceContext.From(u, Program.IsAdmin);
        }

        // LogVice is not found: asks first, then downloads it or lets the user find it. Returns the exe, or null.
        string GetLogVice()
        {
            LogViceContext c = LogViceContextNow();
            LogViceResult res;
            bool find;
            using (LogViceDialog d = new LogViceDialog(theme, c, true))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                res = d.Result;
                find = d.FindChosen;
            }
            if (find) return FindLogViceExe();
            if (res == null) return null;
            string exe = LogViceLink.Find() ?? res.Exe;
            Status(res.Way == LogViceWay.Install ? "LogVice " + res.Version + " is installed." : res.Way == LogViceWay.Temp ? "LogVice " + res.Version + " is in a temporary folder until TrackVice closes." : "LogVice " + res.Version + " is in " + LogViceGet.KeepFolder + ".");
            return exe;
        }

        string FindLogViceExe()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "LogVice|LogVice.exe|Programs (*.exe)|*.exe";
                d.Title = "Locate LogVice.exe";
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                Prefs.Set(LogViceGet.PrefName, d.FileName);
                return d.FileName;
            }
        }

        public static readonly string[] LogVicePages = { "logvice-get", "logvice-progress", "logvice-failed", "logvice-pe", "logvice-deploying" };

        // Screenshots: the question, the download, a failed check, Windows PE and a device that deploys.
        Form ShotLogVice()
        {
            LogViceContext c = new LogViceContext();
            c.Window = true;
            if (start.Page == "logvice-pe") c.WinPE = true;
            if (start.Page == "logvice-deploying") c.Oobe = true;
            LogViceDialog d = new LogViceDialog(theme, c, true);
            d.Demo = true;
            LogViceRelease r = LogViceDialog.DemoRelease();
            d.Shown += delegate
            {
                if (start.Page == "logvice-pe") return;
                d.Asked(r);
                if (start.Page == "logvice-progress") d.Progress(1048576, r.Exe.Size);
                else if (start.Page == "logvice-failed") d.Failed("The downloaded LogVice.exe does not match its checksum in SHA256SUMS.txt, so it was not used and nothing was installed. Try again later, or download LogVice from " + r.Url + ".");
            };
            return d;
        }
    }
}
