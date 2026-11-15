namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;

    // Starts PackVice and NotifyVice, found like LogVice: the chosen path, the MSI install, App Paths, next to TrackVice.exe and PATH.
    public static class ViceLinks
    {
        // Set for screenshots: the demo then acts as if it ran on the device and both tools and the installer were found.
        public static bool Preview;

        public static IToolProbe Probe
        {
            get
            {
                string here = null;
                try { here = Path.GetDirectoryName(Program.Location); } catch (Exception) { }
                return new WindowsToolProbe(here);
            }
        }

        public static string Find(ViceTool t)
        {
            if (Preview) return @"C:\Program Files\" + t.Name + @"\" + t.Exe;
            return ViceLocator.Find(t, Probe, Prefs.Get(t.Pref, null), Program.IsAdmin);
        }

        // Asks for the exe when it was not found, and keeps the choice. Null when the user cancels.
        public static string FindOrAsk(IWin32Window owner, ViceTool t)
        {
            string exe = Find(t);
            if (exe != null) return exe;
            DialogResult r = MessageBox.Show(owner, t.Name + " was not found on this computer. Do you want to locate " + t.Exe + "?", "TrackVice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return null;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = t.Name + "|" + t.Exe + "|Programs (*.exe)|*.exe";
                d.Title = "Locate " + t.Exe;
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                Prefs.Set(t.Pref, d.FileName);
                return d.FileName;
            }
        }

        public static void Start(string exe, string argument)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, string.IsNullOrEmpty(argument) ? "" : "\"" + argument + "\"");
            psi.UseShellExecute = false;
            try { psi.WorkingDirectory = Path.GetDirectoryName(exe); } catch (Exception) { }
            Process.Start(psi);
        }

        // The suggested message for NotifyVice: a title line, then the text.
        public static string ReadyMessage(string computer)
        {
            return "Your PC is ready\r\n" + (string.IsNullOrEmpty(computer) ? "Your PC" : computer) + " has finished its setup. You can sign in and start working.";
        }

        // The Settings rows that show where each tool was found, with Browse and Clear.
        public static List<Control> SettingRows(Theme theme, MethodInvoker relayout)
        {
            List<Control> l = new List<Control>();
            l.Add(new SectionTitle("PackVice and NotifyVice"));
            l.Add(Row(theme, ViceTool.PackVice, "Opens the .msi of a Win32 app or SCCM application from the IME or ccmcache folder, from the item details.", relayout));
            l.Add(Row(theme, ViceTool.NotifyVice, "Tells the user that a device with the agent is ready, from Devices.", relayout));
            return l;
        }

        static SettingRow Row(Theme theme, ViceTool t, string what, MethodInvoker relayout)
        {
            SettingRow row = null;
            MethodInvoker update = delegate
            {
                string custom = Prefs.Get(t.Pref, null);
                string found = Find(t);
                string where;
                if (!string.IsNullOrEmpty(custom)) where = "Chosen: " + custom + (found == custom ? "" : " (not used)");
                else if (found != null) where = "Found: " + found;
                else where = "Not found. TrackVice looks for the " + t.Name + " MSI install, App Paths, " + t.Exe + " next to TrackVice.exe and PATH. Browse to choose it.";
                row.Desc = what + " " + where;
                row.Invalidate();
                if (relayout != null) relayout();
            };
            FlatButton bBrowse = Small(theme, "Browse", delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = t.Name + "|" + t.Exe + "|Programs|*.exe";
                    d.Title = "Locate " + t.Name;
                    if (d.ShowDialog() != DialogResult.OK) return;
                    Prefs.Set(t.Pref, d.FileName);
                }
                update();
            });
            FlatButton bClear = Small(theme, "Clear", delegate { Prefs.Set(t.Pref, null); update(); });
            row = new SettingRow(Glyphs2.Folder, t.Name, what, new ButtonStrip(bBrowse, bClear));
            update();
            return row;
        }

        static FlatButton Small(Theme theme, string text, EventHandler click)
        {
            FlatButton b = new FlatButton(text, null);
            b.Compact = true;
            b.AutoWidth();
            b.Width += Dpi.S(8);
            b.Click += Safe.Wrap(click);
            b.ApplyTheme(theme);
            return b;
        }
    }

    public sealed partial class MainForm
    {
        readonly FlatButton bItemPack = new FlatButton("Installer in PackVice", Glyphs.Export);
        InstallerLookup shownInstaller;

        // Adds the PackVice button to the item details footer, left of Open in LogVice.
        void AddPackViceButton(Panel foot)
        {
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            gap.Visible = false;
            gap.Tag = "card-foot";
            plain.Add(gap);
            bItemPack.Dock = DockStyle.Right;
            bItemPack.Visible = false;
            bItemPack.VisibleChanged += delegate { gap.Visible = bItemPack.Visible; };
            bItemPack.Click += Safe.Wrap(delegate { OpenInstaller(list.SelectedItem); });
            Tip(bItemPack, "Open the .msi this app was installed from in PackVice");
            themed.Add(bItemPack);
            foot.Controls.Add(gap);
            foot.Controls.Add(bItemPack);
            gap.BringToFront();
            bItemPack.BringToFront();
        }

        // The installer only means something on the device itself: live, or a folder of this PC's logs.
        InstallerLookup Installer(Item i)
        {
            if (engine == null || i == null) return null;
            if (ViceLinks.Preview)
            {
                return InstallerFinder.For(i, new PreviewProbe(i.Get("ID")));
            }
            bool here = engine.Mode == Modes.Live
                || (engine.Mode == Modes.Folder && (string.IsNullOrEmpty(engine.S.Computer) || string.Equals(engine.S.Computer, Environment.MachineName, StringComparison.OrdinalIgnoreCase)));
            if (!here) return null;
            return InstallerFinder.For(i, ViceLinks.Probe);
        }

        // Called when the selection changes: the button shows only when the .msi is on this PC.
        void UpdatePackVice(Item i)
        {
            shownInstaller = Installer(i);
            bItemPack.Visible = shownInstaller != null && shownInstaller.Found;
        }

        // The Installer section of the item details: where the .msi is, or why it is not there.
        void ShowInstaller(RichTextBox r, Item i)
        {
            InstallerLookup l = shownInstaller != null && i == list.SelectedItem ? shownInstaller : Installer(i);
            if (l == null) return;
            Section(r, "Installer");
            if (l.Found) Field(r, "On this PC", l.Path, true);
            else
            {
                Field(r, "Installs", l.Msi, true);
                Plain(r, l.Reason, theme.Text3, null);
            }
        }

        void AddPackViceMenu(ContextMenuStrip m, Item it)
        {
            InstallerLookup l = Installer(it);
            if (l == null) return;
            ToolStripMenuItem x = Menus.Add(m, "Open the installer in PackVice", delegate { OpenInstaller(it); });
            x.Enabled = l.Found;
            if (!l.Found) x.ToolTipText = l.Reason;
        }

        void OpenInstaller(Item i)
        {
            InstallerLookup l = Installer(i);
            if (l == null) { Status("This item was not installed from an .msi on this PC."); return; }
            if (!l.Found) { Status(l.Reason); return; }
            if (ViceLinks.Preview) { Status("Preview: PackVice would open " + l.Path + "."); return; }
            string exe = ViceLinks.FindOrAsk(this, ViceTool.PackVice);
            if (exe == null) return;
            try
            {
                ViceLinks.Start(exe, l.Path);
                Status("Opened " + ViceLocator.FileName(l.Path) + " in PackVice.");
            }
            catch (Exception ex) { Fail(ex); }
        }

        // NotifyVice, for a device watched through the agent. Shown in the Devices menu.
        void AddNotifyViceMenu(ContextMenuStrip m)
        {
            if (engine == null || engine.Mode != Modes.Agent) return;
            FleetRun run = FleetStats.RunOf(engine.S, engine.Now);
            bool ready = ViceLinks.Preview || (run.Finished && run.Failed == 0);
            Menus.Separator(m);
            ToolStripMenuItem x = Menus.Add(m, ready ? "Tell the user with NotifyVice\u2026" : "Tell the user with NotifyVice (when " + engine.S.Computer + " has finished without failures)", delegate { TellUser(); });
            x.Enabled = ready;
        }

        // NotifyVice takes no device or message on its command line, so it is started plainly and the message goes on the clipboard.
        void TellUser()
        {
            string computer = engine.S.Computer;
            string exe = ViceLinks.FindOrAsk(this, ViceTool.NotifyVice);
            if (exe == null) return;
            try { ViceLinks.Start(exe, null); }
            catch (Exception ex) { Fail(ex); return; }
            bool copied = false;
            try { Clipboard.SetText(ViceLinks.ReadyMessage(computer)); copied = true; }
            catch (Exception) { }
            Status(copied
                ? "Started NotifyVice. A title and text for " + computer + " are on the clipboard: choose the device there and paste them into the message."
                : "Started NotifyVice. Choose " + computer + " there and write the message.");
        }

        // The demo's items with every file found, for screenshots.
        sealed class PreviewProbe : IToolProbe
        {
            readonly string id;
            public PreviewProbe(string appId) { id = appId; }
            public bool FileExists(string path) { return true; }
            public bool DirectoryExists(string path) { return true; }
            public string[] Directories(string folder) { return new string[] { folder + @"\" + id + "_1" }; }
            public string MachineValue(string key, string name) { return null; }
            public string WindowsDir { get { return @"C:\WINDOWS"; } }
            public string ProgramFiles { get { return @"C:\Program Files"; } }
            public string ProgramFilesX86 { get { return @"C:\Program Files (x86)"; } }
            public string Here { get { return null; } }
            public string PathVariable { get { return null; } }
        }
    }
}
