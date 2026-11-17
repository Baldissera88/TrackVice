namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // A line of text in a menu: a short title and a wrapped sentence or two, for the empty state of a side.
    public sealed class MenuNote : FlatControl
    {
        public string Title, Desc;

        public MenuNote(string title, string desc)
        {
            Title = title;
            Desc = desc;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            int w = Dpi.S(340);
            Size = new Size(w, Dpi.S(10) + Fonts.UiSemibold.Height + Dpi.S(2) + Draw.WrapHeight(desc, Fonts.Small, w - Dpi.S(20)) + Dpi.S(8));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Card);
            int x = Dpi.S(10), w = Width - Dpi.S(20);
            Draw.Text(g, Title, Fonts.UiSemibold, new Rectangle(x, Dpi.S(8), w, Fonts.UiSemibold.Height + 2), T.Text, Draw.Line);
            int y = Dpi.S(10) + Fonts.UiSemibold.Height + Dpi.S(2);
            Draw.Text(g, Desc, Fonts.Small, new Rectangle(x, y, w, Height - y), T.Text2, Draw.Wrap);
        }
    }

    // The guide for step 3 of Getting started: install the agent on one test PC or VM, and what to look for.
    public sealed class TestVmDialog : PageForm
    {
        public bool OpenList;

        public TestVmDialog(Theme t, bool own, string[] customerAndCode) : base("Test on one VM", t)
        {
            Size = new Size(Dpi.S(820), Dpi.S(700));
            string who = customerAndCode != null ? customerAndCode[0] : null;
            string code = customerAndCode != null ? customerAndCode[1] : null;
            string cmd = ".\\TrackVice.exe /agent-install /customer \"<code>\"";
            Add(new SectionTitle("1. Install the agent on one test PC or VM"));
            FlatButton copy = SmallButton("Copy", delegate
            {
                if (code == null) { Safe.Say("Add " + (own ? "your tenant" : "a customer") + " first: Devices > Add customer makes the code."); return; }
                Menus.Copy(".\\TrackVice.exe /agent-install /customer \"" + code + "\"");
            });
            Add(new SettingRow(Glyphs2.Code, "In PowerShell as administrator, in the folder with TrackVice.exe",
                (code != null ? "With the " + (own ? "code" : "customer code") + " of " + who + ". Copy puts the whole command on the clipboard." : "Devices > Add customer makes the " + (own ? "code" : "customer code") + ". It is not made yet.")
                + " This skips the 24-hour check of the platform script, so it also works on a PC enrolled long ago.", copy));
            Add(new KeyTable(true,
                new string[] { cmd, "Installs the agent and starts it. It prints when it removes itself." },
                new string[] { "& 'C:\\Program Files\\TrackVice Agent\\TrackVice.exe' /agent-remove", "Removes it again after the test." }));
            Add(new SectionTitle("2. What to look for"));
            Add(new KeyTable(false,
                new string[] { "Task Scheduler", "The task TrackVice Agent is there and running." },
                new string[] { "agent.log", "C:\\Program Files\\TrackVice Agent\\agent.log says the agent started, made its first upload, and that self-update is on." },
                new string[] { "The device list", "Devices > Devices with the TrackVice agent, with " + (who ?? (own ? "your tenant" : "the customer")) + " chosen at the top, has a row for the PC within a minute. This step ticks itself off then." }));
            Add(new SectionTitle("3. Then one real enrollment"));
            Add(new SettingRow(Glyphs2.Device, "Enroll or reset one Autopilot device",
                "The platform script runs during device setup, and the device appears in the list. When it does not, docs/AGENT.md has a troubleshooting part.", null));
            FlatButton list = new FlatButton("Open the device list", null);
            list.Dock = DockStyle.Right;
            list.Click += Safe.Wrap(delegate { OpenList = true; Close(); });
            list.ApplyTheme(t);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            footer.Controls.Add(gap);
            footer.Controls.Add(list);
        }
    }

    // SCCM / on-premises: the SMS Provider to read task sequences from, and how to add the agent to a task sequence.
    public sealed class SccmSetupDialog : PageForm
    {
        public bool OpenList;
        readonly TextBox server = new TextBox();
        readonly TextBox site = new TextBox();

        public SccmSetupDialog(Theme t, bool agentFirst, string customer, string code, bool demo) : base(agentFirst ? "The agent in a task sequence" : "SMS Provider connection", t)
        {
            Size = new Size(Dpi.S(860), Dpi.S(720));
            server.Width = Dpi.S(260);
            site.Width = Dpi.S(70);
            site.CharacterCasing = CharacterCasing.Upper;
            site.MaxLength = 3;
            foreach (TextBox b in new TextBox[] { server, site })
            {
                b.BorderStyle = BorderStyle.FixedSingle;
                b.Font = Fonts.Ui;
            }
            server.Text = demo ? "cm01.contoso.com" : Prefs.Get("SccmServer", "");
            site.Text = demo ? "P01" : Prefs.Get("SccmSite", "");
            if (agentFirst) { AgentPart(customer, code); ProviderPart(customer, demo); }
            else { ProviderPart(customer, demo); AgentPart(customer, code); }
            FlatButton list = new FlatButton("Task sequences in SCCM", null);
            list.Dock = DockStyle.Right;
            list.Click += Safe.Wrap(delegate { Save(demo); OpenList = true; Close(); });
            list.ApplyTheme(t);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            footer.Controls.Add(gap);
            footer.Controls.Add(list);
            FormClosing += delegate { if (!demo) Save(false); };
        }

        void ProviderPart(string customer, bool demo)
        {
            Add(new SectionTitle("SMS Provider"));
            Add(new SettingRow(Glyphs2.Device, "Server", "The server that runs the SMS Provider, usually the site server. Saved with " + (customer != null ? "the customer " + customer : "this PC") + ", so a customer can have Intune and SCCM.", server));
            Add(new SettingRow(Glyphs2.Code, "Site code", "Optional. TrackVice finds it when it connects.", site));
            Add(new KeyTable(false,
                new string[] { "Rights", "A role that can read task sequence deployment status, such as Read-only Analyst, and remote WMI access to the SMS Provider. TrackVice uses your Windows account, as the console does." },
                new string[] { "What it reads", "The status message each task sequence step sends to the site. A step shows once it has reported, a little after it ran." }));
        }

        void AgentPart(string customer, string code)
        {
            Add(new SectionTitle("The agent in a task sequence"));
            Add(new SettingRow(Glyphs2.Apps, "What it adds",
                "Everything the device sees, from Windows PE to the end, as one session in Devices with the TrackVice agent. Nothing is installed on the device. It needs WinPE-NetFx on the boot image for the part in Windows PE, and a package with TrackVice.exe.", null));
            FlatButton copy = SmallButton("Copy", delegate
            {
                if (code == null) { Safe.Say("Devices > Intune / cloud > Add customer makes the customer code."); return; }
                Menus.Copy("TrackVice.exe /agent-start /customer \"" + code + "\"");
            });
            Add(new SettingRow(Glyphs2.Code, "Run Command Line, with the TrackVice package",
                code != null ? "With the customer code of " + customer + ". Copy puts the whole command on the clipboard." : "Devices > Intune / cloud > Add customer makes the customer code, also for a site without Intune.", copy));
            Add(new KeyTable(true,
                new string[] { "TrackVice.exe /agent-start /customer \"<code>\"", "Starts the agent in the background and returns at once." },
                new string[] { "TrackVice.exe /agent-stop", "Optional, last step: the last upload, and the agent stops at once." }));
            Add(new KeyTable(false,
                new string[] { "After Partition Disk", "Starts the agent in Windows PE." },
                new string[] { "After Setup Windows and ConfigMgr", "The restart into Windows ends the agent from Windows PE. This starts it again in Windows." },
                new string[] { "After each Restart Computer", "A restart ends the agent. This starts it again." },
                new string[] { "On every one of these steps", "Tick Continue on error on the Options tab, so the task sequence goes on if the agent cannot start." }));
        }

        void Save(bool demo)
        {
            if (demo) return;
            string srv = server.Text.Trim(), sc = site.Text.Trim();
            if (srv == Prefs.Get("SccmServer", "") && sc == Prefs.Get("SccmSite", "")) return;
            Prefs.Set("SccmServer", srv.Length == 0 ? null : srv);
            Prefs.Set("SccmSite", sc.Length == 0 ? null : sc);
            CustomerUi.Remember(delegate (Customer cu) { cu.SccmServer = srv; cu.SccmSite = sc; });
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            foreach (TextBox b in new TextBox[] { server, site }) { b.BackColor = theme.Field; b.ForeColor = theme.Text; }
        }
    }

    // A side filter for lists of enrollments: Intune and SCCM, Intune, or SCCM.
    public static class SideFilterUi
    {
        public static readonly string[] Values = { DeviceSides.Both, DeviceSides.Intune, DeviceSides.Sccm };

        public static Segmented Make(string side)
        {
            Segmented s = new Segmented("Both", "Intune", "SCCM");
            int i = Array.IndexOf(Values, side ?? DeviceSides.Both);
            s.Selected = i < 0 ? 0 : i;
            return s;
        }

        public static string Of(Segmented s)
        {
            return s == null ? DeviceSides.Both : Values[Math.Max(0, Math.Min(Values.Length - 1, s.Selected))];
        }
    }

    // The filter for the side in the list of devices with the agent, and how many rows it had.
    public sealed partial class RemoteDialog
    {
        Segmented sideFilter;
        // The side the list starts on, set before it is shown.
        public string Side = DeviceSides.Both;

        public int Listed
        {
            get { return all.Count; }
        }

        void BuildSideFilter()
        {
            sideFilter = SideFilterUi.Make(Side);
            sideFilter.Margin = new Padding(Dpi.S(8), 0, 0, 0);
            sideFilter.Changed += Safe.Wrap(delegate { Bind(); });
            themed.Add(sideFilter);
            bar.Controls.Add(sideFilter);
            Shown += delegate
            {
                int i = Array.IndexOf(SideFilterUi.Values, Side);
                if (i >= 0) sideFilter.Selected = i;
            };
        }

        bool SideShows(RemoteDeployment d)
        {
            return sideFilter == null || DeviceSides.Matches(DeviceSides.OfKind(d.User), SideFilterUi.Of(sideFilter));
        }
    }

    public sealed partial class MainForm
    {
        // The side the Devices menu shows: intune or sccm.
        string DevicesSide
        {
            get { return DeviceSides.Remembered(shotSide ?? Prefs.Get("DevicesSide", null), Facts()); }
        }

        string shotSide;
        SideFacts shotFacts;

        SideFacts Facts()
        {
            if (shotFacts != null) return shotFacts;
            SideFacts f = new SideFacts();
            try { f.Storage = YourStorageUi.Load() != null; }
            catch (Exception) { }
            try
            {
                foreach (Customer c in CustomerUi.Book.All)
                {
                    if (!string.IsNullOrEmpty(c.Tenant) || !string.IsNullOrEmpty(c.AgentLocation)) f.Customers = true;
                }
                Customer cur = CustomerUi.Book.Current;
                f.SccmServer = !string.IsNullOrEmpty(Prefs.Get("SccmServer", null)) || (cur != null && !string.IsNullOrEmpty(cur.SccmServer));
            }
            catch (Exception) { }
            f.SignedIn = graph != null;
            return f;
        }

        void ShowDevicesMenu()
        {
            DevicesMenu().Show(bDevices, new Point(0, bDevices.Height));
        }

        // The Devices menu: a switch between Intune / cloud and SCCM / on-premises at the top, and under it only the
        // side in use, then what works for both. The side is remembered.
        ContextMenuStrip DevicesMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            string side = DevicesSide;
            Segmented sw = new Segmented(DeviceSides.Name(DeviceSides.Intune), DeviceSides.Name(DeviceSides.Sccm));
            sw.Selected = side == DeviceSides.Sccm ? 1 : 0;
            sw.ApplyTheme(theme);
            sw.Changed += Safe.Wrap(delegate
            {
                string to = sw.Selected == 1 ? DeviceSides.Sccm : DeviceSides.Intune;
                if (start.Shot == null) Prefs.Set("DevicesSide", to);
                else shotSide = to;
                m.Close();
                BeginInvoke((MethodInvoker)delegate { Safe.Run(ShowDevicesMenu); });
            });
            ToolStripControlHost host = new ToolStripControlHost(sw);
            host.Margin = new Padding(Dpi.S(6), Dpi.S(6), Dpi.S(6), Dpi.S(4));
            host.AutoSize = false;
            host.Size = sw.Size;
            m.Items.Add(host);
            Tip(sw, "Intune / cloud: Autopilot, devices with the agent and customers. SCCM / on-premises: task sequences from your site.");

            string[] empty = DeviceMenu.Empty(side, Facts());
            if (empty != null)
            {
                MenuNote note = new MenuNote(empty[0], empty[1]);
                note.ApplyTheme(theme);
                ToolStripControlHost nh = new ToolStripControlHost(note);
                nh.AutoSize = false;
                nh.Size = note.Size;
                nh.Margin = new Padding(Dpi.S(6), 0, Dpi.S(6), 0);
                m.Items.Add(nh);
                string id = empty[2];
                ToolStripMenuItem go = Menus.Add(m, empty[3], delegate { DeviceAction(id); });
                go.Font = Fonts.UiSemibold;
                go.ForeColor = theme.Accent;
            }
            Menus.Separator(m);

            ToolStripMenuItem demos = null;
            foreach (DeviceMenuEntry e in DeviceMenu.For(side))
            {
                if (e.Separator) { Menus.Separator(m); continue; }
                string id = e.Id;
                // The demos go in one submenu, so the menu shows the real things first.
                if (e.Text != null && e.Text.StartsWith("Demo: ", StringComparison.Ordinal))
                {
                    if (demos == null)
                    {
                        demos = new ToolStripMenuItem("Demos");
                        demos.ForeColor = theme.Text;
                        demos.Padding = new Padding(Dpi.S(4), Dpi.S(3), Dpi.S(4), Dpi.S(3));
                        demos.DropDown.BackColor = theme.Card;
                        demos.DropDown.Renderer = m.Renderer;
                        m.Items.Add(demos);
                    }
                    string text = e.Text.Substring(6);
                    ToolStripMenuItem sub = new ToolStripMenuItem(char.ToUpperInvariant(text[0]) + text.Substring(1), null, Safe.Wrap(delegate { DeviceAction(id); }));
                    sub.ForeColor = theme.Text;
                    sub.BackColor = theme.Card;
                    sub.Padding = demos.Padding;
                    demos.DropDownItems.Add(sub);
                    continue;
                }
                if (id == DeviceMenu.Compare) CompareDialog.AddMenu(m, theme, engine != null, delegate { CompareWith(true); }, delegate { CompareWith(false); });
                else if (id == DeviceMenu.Reference) ReferenceMenu(m);
                else if (id == DeviceMenu.NotifyVice) AddNotifyViceMenu(m);
                else Menus.Add(m, e.Text, delegate { DeviceAction(id); });
            }
            return m;
        }

        void DeviceAction(string id)
        {
            switch (id)
            {
                case DeviceMenu.AddCustomer: OpenAddCustomer(false); UpdateSetupBar(); break;
                case DeviceMenu.Customers: using (CustomersPage p = new CustomersPage(theme, CustomerUi.Book)) p.ShowDialog(this); break;
                case DeviceMenu.Intune: OpenRemote(RemoteDialog.Intune); break;
                case DeviceMenu.Agents: OpenRemote(RemoteDialog.Agents); break;
                case DeviceMenu.TsAgents: OpenAgents(DeviceSides.Sccm); break;
                case DeviceMenu.Sccm: OpenRemote(RemoteDialog.Sccm); break;
                case DeviceMenu.SmsProvider: OpenSccmSetup(false); break;
                case DeviceMenu.TsAgent: OpenSccmSetup(true); break;
                case DeviceMenu.Readiness: OpenReadiness(false); break;
                case DeviceMenu.Names: NamesFromIntune(); break;
                case DeviceMenu.NewCustomer: OpenNewCustomer(false); break;
                case DeviceMenu.Stats: OpenStats(false); break;
                case DeviceMenu.Wall: OpenWall(null, false); break;
                case DeviceMenu.Watch: Begin(Settings.NewLive(), true); break;
                case DeviceMenu.DemoIntune: OpenRemote(RemoteDialog.Intune, true); break;
                case DeviceMenu.DemoAgents: OpenRemote(RemoteDialog.Agents, true); break;
                case DeviceMenu.DemoSccm: OpenRemote(RemoteDialog.Sccm, true); break;
                case DeviceMenu.DemoStats: OpenStats(true); break;
                case DeviceMenu.DemoWall: OpenWall(null, true); break;
                case DeviceMenu.DemoCompare: using (CompareDialog d = CompareDialog.ForDemo(theme)) d.ShowDialog(this); break;
                case DeviceMenu.DemoReadiness: OpenReadiness(true); break;
                case DeviceMenu.DemoNewCustomer: OpenNewCustomer(true); break;
                case DeviceMenu.DemoAddCustomer: OpenAddCustomer(true); break;
            }
        }

        // The statistics filter starts on the side of the Devices menu.
        string StatsSide
        {
            get { return DevicesSide == DeviceSides.Sccm ? DeviceSides.Sccm : DeviceSides.Both; }
        }

        void OpenSccmSetup(bool agentFirst)
        {
            string[] tc = TestCode();
            Customer cur = CustomerUi.Book.Current;
            bool list;
            using (SccmSetupDialog d = new SccmSetupDialog(theme, agentFirst, cur != null ? cur.Name : null, tc != null ? tc[1] : null, false))
            {
                d.ShowDialog(this);
                list = d.OpenList;
            }
            sccmCatalog = null;
            if (list) OpenRemote(RemoteDialog.Sccm);
        }

        // --page devices-intune and devices-sccm show the Devices menu on that side, --select empty its empty state.
        public static readonly string[] DevicePages = { "devices-intune", "devices-sccm", "sccmsetup", "tsagent" };

        Form ShotDevices()
        {
            if (start.Page == "sccmsetup" || start.Page == "tsagent")
                return new SccmSetupDialog(theme, start.Page == "tsagent", "Contoso", "tv1:made-up-code-for-the-demo", true);
            shotSide = start.Page == "devices-sccm" ? DeviceSides.Sccm : DeviceSides.Intune;
            shotFacts = new SideFacts();
            if (start.Select != "empty")
            {
                shotFacts.Storage = shotFacts.Customers = true;
                shotFacts.SccmServer = true;
            }
            ShowDevicesMenu();
            return null;
        }
    }
}
