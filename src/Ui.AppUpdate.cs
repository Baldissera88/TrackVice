namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.Threading;
    using System.Windows.Forms;

    // The update settings in the user's TrackVice settings.
    public sealed class PrefsUpdateStore : IUpdatePrefs
    {
        public string Get(string name)
        {
            return Prefs.Get(name, null);
        }

        public void Set(string name, string value)
        {
            Prefs.Set(name, value);
        }
    }

    // Settings > Updates, the helper window and the start of the helper.
    public static class UpdatesUi
    {
        // The last answer from GitHub in this run, for Settings.
        public static AppUpdateInfo Last;
        // Set by the main window: asks and installs.
        public static Action<AppUpdateInfo> Install;
        // Set by the main window: brings what it knows about this device up to date before a check.
        public static MethodInvoker BeforeCheck;
        // Screenshots show this instead of asking GitHub.
        public static AppUpdateInfo Demo;
        static bool wired;

        public static void Wire()
        {
            if (wired) return;
            wired = true;
            AppUpdate.Prefs = new PrefsUpdateStore();
        }

        public static AppUpdateInfo DemoInfo()
        {
            AppUpdateInfo r = new AppUpdateInfo();
            r.Latest = "1.3.0";
            r.Newer = true;
            r.Url = AppUpdate.ReleasesUrl + "/tag/v1.3.0";
            r.MsiUrl = AppUpdate.ReleasesUrl + "/download/v1.3.0/TrackVice.msi";
            r.ExeUrl = AppUpdate.ReleasesUrl + "/download/v1.3.0/TrackVice.exe";
            r.SumsUrl = AppUpdate.ReleasesUrl + "/download/v1.3.0/SHA256SUMS.txt";
            r.CheckedUtc = DateTime.UtcNow.AddMinutes(-3);
            r.Notes = "17 November 2026.\n\n• TrackVice updates itself from its GitHub releases, as LogVice does. Settings > Updates turns it off.\n• Each SCCM task sequence run is its own row.";
            return r;
        }

        static string WhenText(DateTime utc)
        {
            return utc.ToLocalTime().ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);
        }

        public static List<Control> SettingRows(Theme theme, MethodInvoker relayout)
        {
            Wire();
            List<Control> l = new List<Control>();
            l.Add(new SectionTitle("Updates"));
            string blocked = Demo != null ? null : AppUpdate.Blocked;
            bool policy = Demo == null && blocked != null && blocked.IndexOf(UpdateGate.PolicyValue, StringComparison.Ordinal) >= 0;
            if (policy)
            {
                SettingRow pol = new SettingRow(Glyphs2.Sync, "Check for updates", blocked, null);
                pol.Value = "Off";
                l.Add(pol);
            }
            else
            {
                Toggle tog = new Toggle();
                tog.On = AppUpdate.Enabled;
                tog.Changed += Safe.Wrap(delegate { if (Demo == null) AppUpdate.Enabled = tog.On; });
                l.Add(new SettingRow(Glyphs2.Sync, "Check for updates", "Once a day at start, TrackVice asks GitHub which version is the latest and shows a line in the status bar when there is a newer one. Nothing about you or your devices is sent. Never on the command line, as the agent, in Windows PE, during Windows setup or the ESP, or as SYSTEM. Administrators can turn it off for everyone with the DWORD DisableUpdateCheck = 1 under HKLM\\SOFTWARE\\Policies\\TrackVice.", tog));
            }

            FlatButton b = new FlatButton("Check now", null);
            b.Compact = true;
            SettingRow row = new SettingRow(Glyphs.Info, "Updates", "", b);
            AppUpdateInfo shown = Demo ?? Last;
            MethodInvoker show = null;
            show = delegate
            {
                AppUpdateInfo r = shown;
                string desc, text;
                DateTime? last = Demo != null ? (DateTime?)Demo.CheckedUtc : AppUpdate.LastChecked;
                string when = last.HasValue ? " Last checked " + WhenText(last.Value) + "." : " Not checked yet.";
                if (blocked != null && !policy) { desc = blocked + " Releases are published on " + AppUpdate.ReleasesUrl + "."; text = "Check now"; }
                else if (policy) { desc = "Releases are published on " + AppUpdate.ReleasesUrl + "."; text = "Check now"; }
                else if (r == null) { desc = "You have TrackVice " + Program.Version + "." + when + " Releases are published on " + AppUpdate.ReleasesUrl + "."; text = "Check now"; }
                else if (r.Error != null) { desc = r.Error + when + " Releases are published on " + AppUpdate.ReleasesUrl + "."; text = "Check now"; }
                else if (r.Newer)
                {
                    bool can = Demo != null || r.CanInstall;
                    desc = "TrackVice " + r.Latest + " is out. You have " + Program.Version + "." + when + " " + (can ? "Install downloads it from GitHub, checks it against the release's checksum and installs it. TrackVice then opens again." : "Download opens the release page.");
                    text = (can ? "Install " : "Download ") + r.Latest;
                }
                else { desc = "You have the latest version, " + Program.Version + "." + when; text = "Check now"; }
                row.Desc = desc;
                b.Text = text;
                b.AutoWidth();
                b.Width += Dpi.S(8);
                b.Enabled = blocked == null;
                row.PerformLayout();
                row.Invalidate();
                if (relayout != null) relayout();
            };
            b.Click += Safe.Wrap(delegate
            {
                AppUpdateInfo r = shown;
                if (Demo != null) return;
                if (r != null && r.Newer)
                {
                    if (r.CanInstall && Install != null)
                    {
                        Form f = row.FindForm();
                        AppUpdateInfo go = r;
                        if (f != null && f.Modal)
                        {
                            f.FormClosed += delegate { Install(go); };
                            f.Close();
                        }
                        else Install(go);
                    }
                    else try { Process.Start(r.Url); } catch (Exception) { }
                    return;
                }
                if (BeforeCheck != null) BeforeCheck();
                blocked = AppUpdate.Blocked;
                if (blocked != null) { show(); return; }
                b.Enabled = false;
                row.Desc = "Checking GitHub…";
                row.Invalidate();
                AppUpdate.CheckAsync(delegate (AppUpdateInfo res)
                {
                    try
                    {
                        if (row.IsDisposed || !row.IsHandleCreated) return;
                        row.BeginInvoke((MethodInvoker)delegate
                        {
                            shown = res;
                            if (res.Error == null) Last = res;
                            show();
                        });
                    }
                    catch (InvalidOperationException) { }
                });
            });
            b.ApplyTheme(theme);
            show();
            l.Add(row);
            return l;
        }

        // The helper started with --apply-update, from a copy of TrackVice.exe in the work folder.
        public static int Apply(string[] args)
        {
            if (args.Length < 5) return 2;
            UpdateJob job = new UpdateJob(args);
            try { Application.EnableVisualStyles(); } catch (InvalidOperationException) { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Dpi.Scale = Math.Max(1f, g.DpiX / 96f);
            }
            catch (Exception) { }
            Fonts.Init();
            using (UpdaterForm f = new UpdaterForm(job.Version, Theme.Named(Theme.SavedName())))
            {
                f.Shown += delegate { ThreadPool.QueueUserWorkItem(delegate { job.Run(f); }); };
                Application.Run(f);
            }
            return job.Result;
        }
    }

    // In the status bar: a newer TrackVice is out. Installs only when clicked.
    public sealed class UpdateNote : FlatControl
    {
        const string Action = "Install";
        public event EventHandler InstallClicked;
        bool working;

        public UpdateNote()
        {
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        public void Say(string text, bool action)
        {
            Text = text;
            working = !action;
            Cursor = action ? Cursors.Hand : Cursors.Default;
            Fit();
            Invalidate();
        }

        void Fit()
        {
            int w = TextRenderer.MeasureText(Text, Fonts.Ui).Width + Dpi.S(8);
            if (!working) w += TextRenderer.MeasureText(Action, Fonts.UiSemibold).Width + Dpi.S(4);
            Width = w + Dpi.S(20);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!working && InstallClicked != null) InstallClicked(this, e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            int tw = TextRenderer.MeasureText(Text, Fonts.Ui).Width;
            int x = Dpi.S(16);
            Draw.Text(g, Text, Fonts.Ui, new Rectangle(x, 0, tw + 2, Height), T.Text2, Draw.Line);
            if (working) return;
            Font f = hover ? new Font(Fonts.UiSemibold, FontStyle.Underline) : Fonts.UiSemibold;
            try { Draw.Text(g, Action, f, new Rectangle(x + tw + Dpi.S(8), 0, Width - x - tw - Dpi.S(8), Height), T.Accent, Draw.Line); }
            finally { if (hover) f.Dispose(); }
        }
    }

    // Asks before an update is downloaded: Install, Release page or Not now.
    public sealed class UpdateDialog : PageForm
    {
        public UpdateDialog(Theme t, AppUpdateInfo r, bool msi) : base("Update TrackVice", t)
        {
            Size = new Size(Dpi.S(700), Dpi.S(500));
            Add(new SectionTitle("TrackVice " + r.Latest + " is out"));
            Add(new SettingRow(Glyphs.Info, "Install", "You have " + Program.Version + ". Install downloads " + (msi ? AppUpdate.MsiName : AppUpdate.ExeName) + " from GitHub to a new folder under %LOCALAPPDATA%\\TrackVice, checks it against the release's " + AppUpdate.SumsName + " and installs it. TrackVice closes and opens again." + (msi ? " Windows asks for administrator rights." : " The new TrackVice.exe replaces this one in " + System.IO.Path.GetDirectoryName(Program.Location) + "."), null));
            if (!string.IsNullOrEmpty(r.Notes))
            {
                Add(new SectionTitle("What's new"));
                Add(new SettingRow(null, "TrackVice " + r.Latest, r.Notes, null));
            }
            bClose.Text = "Not now";
            bClose.Primary = false;
            bClose.AutoWidth();
            bClose.Width = Math.Max(bClose.Width + Dpi.S(8), Dpi.S(96));
            bClose.ApplyTheme(t);
            // Docked from the right: Not now, then Release page, then Install.
            Footer("Release page", DialogResult.No, false);
            Footer("Install", DialogResult.Yes, true);
        }

        void Footer(string text, DialogResult result, bool primary)
        {
            FlatButton b = new FlatButton(text, null);
            b.Primary = primary;
            b.AutoWidth();
            b.Width = Math.Max(b.Width + Dpi.S(8), Dpi.S(96));
            b.Dock = DockStyle.Right;
            b.Click += delegate { DialogResult = result; Close(); };
            b.ApplyTheme(theme);
            Panel gap = new Panel();
            gap.Width = Dpi.S(8);
            gap.Dock = DockStyle.Right;
            footer.Controls.Add(gap);
            footer.Controls.Add(b);
            gap.BringToFront();
            b.BringToFront();
        }
    }

    // The helper window: what the update is doing, until TrackVice starts again.
    public sealed class UpdaterForm : Form, IUpdateProgress
    {
        readonly Label heading, status;
        readonly ProgressBar bar;
        readonly Theme theme;
        bool done;

        public UpdaterForm(string version, Theme t)
        {
            theme = t;
            Text = "TrackVice update";
            Font = Fonts.Ui;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            Icon appIcon = Program.AppIcon();
            if (appIcon != null) Icon = appIcon;
            ClientSize = new Size(Dpi.S(460), Dpi.S(150));
            Padding = new Padding(Dpi.S(24), Dpi.S(20), Dpi.S(24), Dpi.S(22));
            BackColor = t.Bg;
            ForeColor = t.Text;

            heading = new Label();
            heading.Dock = DockStyle.Top;
            heading.Font = Fonts.UiSemibold;
            heading.Height = Fonts.UiSemibold.Height + Dpi.S(10);
            heading.Text = "Updating TrackVice" + (string.IsNullOrEmpty(version) ? "" : " to " + version);
            heading.UseMnemonic = false;

            status = new Label();
            status.Dock = DockStyle.Top;
            status.Height = Fonts.Ui.Height * 2 + Dpi.S(8);
            status.ForeColor = t.Text2;
            status.UseMnemonic = false;
            status.Text = "Starting…";

            bar = new ProgressBar();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Dpi.S(6);
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 30;

            Controls.Add(status);
            Controls.Add(heading);
            Controls.Add(bar);
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (!done && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            };
        }

        public void Status(string text)
        {
            Ui(delegate { status.Text = text; }, false);
        }

        public void Problem(string text)
        {
            Ui(delegate { MessageBox.Show(this, text, "TrackVice update", MessageBoxButtons.OK, MessageBoxIcon.Warning); }, true);
        }

        public void Finish()
        {
            Ui(delegate
            {
                done = true;
                Close();
            }, false);
        }

        void Ui(MethodInvoker a, bool wait)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (wait) Invoke(a);
                else BeginInvoke(a);
            }
            catch (InvalidOperationException) { }
        }
    }

    public sealed partial class MainForm
    {
        UpdateNote updateNote;
        AppUpdateInfo pendingUpdate;
        bool installingUpdate;
        volatile bool updateDeploying;
        int updateTries;

        // What stops an update check here. Runs on any thread, so the state of the session is read before, on this one.
        string UpdateBlocked()
        {
            UpdateContext c = new UpdateContext();
            c.Window = true;
            c.Screenshot = start.Shot != null;
            c.Deploying = updateDeploying;
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
            using (LiveRegistry reg = new LiveRegistry()) UpdateGate.ReadDevice(c, reg, Environment.UserName, system, ts);
            return UpdateGate.Reason(c);
        }

        void RefreshUpdateDeploying()
        {
            updateDeploying = engine != null && engine.Mode == Modes.Live && UpdateGate.Deploying(engine.S, DateTime.UtcNow);
        }

        // Once a day at start, after this device has been read, so a deployment on it is known first.
        void StartUpdateCheck()
        {
            if (start.Shot != null) return;
            UpdatesUi.Wire();
            AppUpdate.Gate = UpdateBlocked;
            UpdatesUi.Install = delegate (AppUpdateInfo r) { UpdateInvoke(delegate { InstallUpdate(r); }); };
            UpdatesUi.BeforeCheck = RefreshUpdateDeploying;
            FormClosed += delegate { if (AppUpdate.HandedOff) Environment.Exit(0); };
            ThreadPool.QueueUserWorkItem(delegate { AppUpdate.CleanUp(); });
            if (!AppUpdate.Enabled || !AppUpdate.DueAt(DateTime.UtcNow)) return;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 20000;
            t.Tick += Safe.Wrap(delegate
            {
                if (engine == null || (engine.Mode == Modes.Live && !loadedOnce))
                {
                    if (++updateTries < 15) return;
                }
                t.Stop();
                t.Dispose();
                RefreshUpdateDeploying();
                AppUpdate.CheckAsync(delegate (AppUpdateInfo r) { UpdateInvoke(delegate { UpdateFound(r); }); });
            });
            t.Start();
        }

        void UpdateFound(AppUpdateInfo r)
        {
            if (r == null || r.Error != null) return;
            UpdatesUi.Last = r;
            if (!r.Newer) return;
            pendingUpdate = r;
            ShowUpdateNote(r);
        }

        void ShowUpdateNote(AppUpdateInfo r)
        {
            if (updateNote == null)
            {
                updateNote = new UpdateNote();
                updateNote.Dock = DockStyle.Right;
                updateNote.InstallClicked += Safe.Wrap(delegate
                {
                    if (pendingUpdate == null) return;
                    if (pendingUpdate.CanInstall) InstallUpdate(pendingUpdate);
                    else try { Process.Start(pendingUpdate.Url); } catch (Exception) { }
                });
                themed.Add(updateNote);
                statusBar.Controls.Add(updateNote);
                statusLeft.BringToFront();
                updateNote.ApplyTheme(theme);
            }
            updateNote.Say("TrackVice " + r.Latest + " is out.", true);
            updateNote.Visible = true;
        }

        void InstallUpdate(AppUpdateInfo r)
        {
            if (r == null || installingUpdate) return;
            bool msi = AppUpdate.IsInstalled;
            if (!r.CanInstallWith(msi))
            {
                try { Process.Start(r.Url); } catch (Exception) { }
                return;
            }
            DialogResult a;
            using (UpdateDialog d = new UpdateDialog(theme, r, msi)) a = d.ShowDialog(this);
            if (a == DialogResult.No) { try { Process.Start(r.Url); } catch (Exception) { } return; }
            if (a != DialogResult.Yes) return;
            RefreshUpdateDeploying();
            installingUpdate = true;
            if (updateNote != null) updateNote.Say("Downloading TrackVice " + r.Latest + "…", false);
            string[] reopen = engine != null && (engine.Mode == Modes.File || engine.Mode == Modes.Folder) ? new string[] { engine.S.SourcePath } : null;
            Thread th = new Thread(delegate ()
            {
                string file = null, err = null;
                try { file = AppUpdate.Prepare(r, delegate (int pct) { UpdateInvoke(delegate { if (updateNote != null) updateNote.Say("Downloading TrackVice " + r.Latest + "… " + pct + " %", false); }); }); }
                catch (Exception ex) { err = ex.Message; }
                UpdateInvoke(delegate
                {
                    installingUpdate = false;
                    if (err == null)
                    {
                        try
                        {
                            if (updateNote != null) updateNote.Say("Installing TrackVice " + r.Latest + "…", false);
                            AppUpdate.Launch(file, reopen);
                            AppUpdate.HandedOff = true;
                            Hide();
                            Close();
                            return;
                        }
                        catch (Exception ex) { err = ex.Message; }
                    }
                    if (updateNote != null) updateNote.Say("TrackVice " + r.Latest + " is out.", true);
                    if (MessageBox.Show(this, "TrackVice " + r.Latest + " could not be installed automatically.\r\n\r\n" + err + "\r\n\r\nOpen the release page to download it?", "Update TrackVice", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        try { Process.Start(r.Url); } catch (Exception) { }
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        void UpdateInvoke(MethodInvoker a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        public static readonly string[] UpdatePages = { "updates", "update", "update-note", "updater" };

        // Screenshots: Settings > Updates, the question before Install, the status bar line and the helper window.
        Form ShotUpdate()
        {
            AppUpdateInfo demo = UpdatesUi.DemoInfo();
            switch (start.Page)
            {
                case "updates":
                    if (start.Select == "latest") { demo.Newer = false; demo.Latest = Program.Version; }
                    UpdatesUi.Demo = demo;
                    SettingsDialog sd = new SettingsDialog(theme);
                    sd.Shown += delegate { sd.BeginInvoke((MethodInvoker)delegate { foreach (Control c in sd.Controls) { Panel p = c as Panel; if (p != null && p.AutoScroll) p.AutoScrollPosition = new Point(0, Dpi.S(start.Tab >= 0 ? start.Tab : 5000)); } }); };
                    return sd;
                case "update": return new UpdateDialog(theme, demo, true);
                case "updater":
                    UpdaterForm f = new UpdaterForm(demo.Latest, theme);
                    f.Shown += delegate { f.Status("Installing TrackVice " + demo.Latest + ". Windows asks for administrator rights."); };
                    return f;
                default:
                    pendingUpdate = demo;
                    ShowUpdateNote(demo);
                    return null;
            }
        }
    }
}
