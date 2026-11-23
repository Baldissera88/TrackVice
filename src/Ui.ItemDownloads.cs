namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // The question before a download: what is saved, what happens to secrets, and that it is logged.
    public sealed class DownloadDialog : PageForm
    {
        public DownloadDialog(Theme t, DlTarget target, bool mask) : base("Download", t)
        {
            Size = new System.Drawing.Size(Dpi.S(620), Dpi.S(460));
            MinimumSize = new System.Drawing.Size(Dpi.S(520), Dpi.S(380));
            Add(new SectionTitle(target.IsScript ? "Download a script" : "Download app details"));
            Add(new SettingRow(Glyphs.Info, "Item", target.Name, null));
            Add(new SettingRow(target.IsScript ? Glyphs2.Code : Glyphs2.Apps, "What is saved", Saved(target), null));
            Add(new SettingRow(Glyphs.Shield, "Secrets", mask
                ? "Passwords, keys and tokens in the text are hidden before it is saved. The save window says when something was hidden."
                : "The text is saved as it is. Hiding secrets is turned off in Settings > Customers.", null));
            Add(new SettingRow(Glyphs.Log, "Log", "The download is written to the download log: the time, your Windows user, the customer, the item and the file name. The text is not logged.", null));
            bClose.Text = "Cancel";
            bClose.Primary = false;
            bClose.AutoWidth();
            bClose.Width = Math.Max(bClose.Width + Dpi.S(8), Dpi.S(96));
            FlatButton save = new FlatButton("Save as…", null);
            save.Primary = true;
            save.Width = Math.Max(save.Width + Dpi.S(8), Dpi.S(110));
            save.Dock = DockStyle.Right;
            save.Click += delegate { DialogResult = DialogResult.Yes; Close(); };
            save.ApplyTheme(t);
            Panel gap = new Panel();
            gap.Width = Dpi.S(8);
            gap.Dock = DockStyle.Right;
            footer.Controls.Add(gap);
            footer.Controls.Add(save);
            gap.BringToFront();
            save.BringToFront();
            bClose.ApplyTheme(t);
        }

        static string Saved(DlTarget t)
        {
            switch (t.Kind)
            {
                case DlKind.PlatformScript: return "The text of the platform script, as Intune holds it. It is saved as a .ps1 file.";
                case DlKind.Remediation: return "The text of the detection script and the remediation script, as Intune holds them. It is saved as a .ps1 file.";
                case DlKind.SccmStep: return "The command line, the script text and the package and program of the step, as the task sequence on the site holds them.";
                default: return "The details of the app as a .txt file: name, install and uninstall command, detection rules, requirements, return codes and assignments. The app files are not downloaded.";
            }
        }
    }

    // The settings of downloads on Settings > Customers. The permission is kept with the customer in use.
    public sealed class DownloadSettingsUi
    {
        Toggle allow, ask, mask;
        SettingRow rAllow, rAsk, rMask, rLog;
        bool quiet;

        public List<Control> Build(Func<string, SettingRow, SettingRow> bind, Func<string, EventHandler, FlatButton> small)
        {
            allow = new Toggle();
            ask = new Toggle();
            mask = new Toggle();
            allow.Changed += Safe.Wrap(delegate
            {
                if (quiet) return;
                bool on = allow.On;
                CustomerUi.Remember(delegate (Customer c) { c.AllowDownloads = on; });
                Refresh();
            });
            ask.Changed += Safe.Wrap(delegate
            {
                if (quiet) return;
                bool on = ask.On;
                CustomerUi.Remember(delegate (Customer c) { c.DownloadsWithoutAsking = !on; });
            });
            mask.Changed += Safe.Wrap(delegate { Prefs.SetBool(DlPolicy.MaskKey, mask.On); });
            FlatButton log = small("Open the log", delegate { OpenLog(); });
            rAllow = bind("downloads", new SettingRow(Glyphs2.Folder, "Allow downloading scripts and app details", null, allow));
            rAsk = bind("downloads.ask", new SettingRow(Glyphs.Shield, "Ask before each download", null, ask));
            rMask = bind("downloads.mask", new SettingRow(Glyphs.Shield, "Hide secrets in downloads", null, mask));
            rLog = bind("downloads.log", new SettingRow(Glyphs.Log, "Download log", null, log));
            rLog.Desc = rLog.Desc + " Kept in " + DlLog.DefaultPath + ".";
            Refresh();
            List<Control> l = new List<Control>();
            l.Add(new SectionTitle("Downloads"));
            l.Add(rAllow);
            l.Add(rAsk);
            l.Add(rMask);
            l.Add(rLog);
            return l;
        }

        // Shows the permission of the customer in use. Call it again when the customer changes.
        public void Refresh()
        {
            if (allow == null) return;
            Customer c = CustomerUi.Book.Current;
            quiet = true;
            allow.On = DlPolicy.Allowed(c);
            ask.On = DlPolicy.Ask(c);
            mask.On = DlPolicy.Mask(new PrefsSettingStore());
            quiet = false;
            ask.Enabled = allow.On;
            allow.Invalidate();
            ask.Invalidate();
            rAllow.Invalidate();
        }

        static void OpenLog()
        {
            string p = DlLog.DefaultPath;
            if (!File.Exists(p)) { Safe.Say("Nothing has been downloaded yet, so there is no log."); return; }
            try { Process.Start(p); }
            catch (Exception) { try { Process.Start("notepad.exe", "\"" + p + "\""); } catch (Exception) { Safe.Say("The log could not be opened: " + p); } }
        }
    }

    public sealed partial class MainForm
    {
        // The Download item of a step, script or app in the list. It is there only when the customer in use allows it.
        void AddDownloadItem(ContextMenuStrip m, Item it)
        {
            bool demo = demoOpen || (engine != null && engine.Mode == Modes.Demo);
            if (!demo && !DlPolicy.Allowed(CustomerUi.Book.Current)) return;
            DlTarget t = DlTargets.For(it, SccmDeployment());
            if (t == null) return;
            Menus.Add(m, "Download…", delegate { Download(t, demo); });
        }

        // The SCCM deployment of the run that is open, from the key of the remote run.
        string SccmDeployment()
        {
            if (engine == null || engine.Mode != Modes.Sccm || string.IsNullOrEmpty(engine.S.SourcePath)) return null;
            string[] k = engine.S.SourcePath.Split('|');
            return k.Length > 1 ? k[1] : null;
        }

        void Download(DlTarget t, bool demo)
        {
            Customer cu = CustomerUi.Book.Current;
            bool mask = DlPolicy.Mask(new PrefsSettingStore());
            if (demo || DlPolicy.Ask(cu))
            {
                using (DownloadDialog d = new DownloadDialog(theme, t, mask))
                    if (d.ShowDialog(this) != DialogResult.Yes) return;
            }
            IDownloadSource src;
            if (demo) src = new FakeDownloadSource();
            else
            {
                SccmCatalog sc = sccmCatalog as SccmCatalog;
                src = new DownloadReader(graph, sc != null ? sc.Query : null);
            }
            string customer = demo ? null : CustomerUi.CurrentName;
            Cursor = Cursors.WaitCursor;
            Status("Reading " + t.Name + "…");
            Thread th = new Thread(delegate ()
            {
                DlContent c = null;
                string problem = null;
                try { c = src.Read(t); }
                catch (DlException ex) { problem = ex.Message; }
                catch (Exception ex) { problem = "It could not be read: " + ex.Message.Trim(); }
                try { BeginInvoke(new Action(delegate { Cursor = Cursors.Default; Status(""); FinishDownload(t, c, problem, mask, customer, demo); })); }
                catch (InvalidOperationException) { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void FinishDownload(DlTarget t, DlContent c, string problem, bool mask, string customer, bool demo)
        {
            if (c == null)
            {
                MessageBox.Show(this, problem ?? "Nothing was read.", "Download", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool hidden;
            string text = DlText.Prepare(c.Text, mask, out hidden);
            using (SaveFileDialog d = new SaveFileDialog())
            {
                string ext = c.Extension;
                d.Filter = (ext == ".ps1" ? "PowerShell script (*.ps1)|*.ps1|" : "") + "Text (*.txt)|*.txt";
                d.FileName = DlNames.Safe(t.Name, ext);
                d.OverwritePrompt = true;
                d.Title = hidden ? "Save as. Secrets in the text were hidden." : "Save as";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string file = d.FileName;
                File.WriteAllText(file, text, new UTF8Encoding(true));
                string note = "";
                if (!demo)
                {
                    try { DlLog.Default().Add(customer, t.Name, Path.GetFileName(file)); }
                    catch (Exception ex) { note = " The download log could not be written: " + ex.Message.Trim(); }
                }
                Status("Saved " + file + (hidden ? ". Secrets were hidden." : "") + note);
            }
        }

        // For screenshots: the setting, or the question before a download.
        Form ShotDownloads(bool confirm)
        {
            if (!confirm) return SettingsDialog.ForShot(theme, "settings-" + SettingsCatalog.Customers, "downloads");
            DlTarget t = new DlTarget();
            t.Kind = DlKind.PlatformScript;
            t.Name = "Set up the shared drive";
            t.Id = "3f6d1c2e-8a41-4b7e-9c55-0d2a7e6b1f90";
            return new DownloadDialog(theme, t, true);
        }
    }
}
