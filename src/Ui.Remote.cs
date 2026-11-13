namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Threading;
    using System.Windows.Forms;

    // Signs in to Intune or connects to SCCM, lists the deployments on other devices, and lets you pick one to watch.
    public sealed class RemoteDialog : Form
    {
        public const string Intune = "intune", Sccm = "sccm";
        public IRemoteCatalog Catalog;
        public RemoteDeployment Picked;
        public GraphClient Graph;

        Theme theme;
        readonly ManualResetEvent cancel = new ManualResetEvent(false);
        readonly List<IThemed> themed = new List<IThemed>();
        readonly Panel head = new Panel();
        readonly Label title = new Label();
        readonly Label desc = new Label();
        readonly FlowLayoutPanel connect = new FlowLayoutPanel();
        readonly FlowLayoutPanel bar = new FlowLayoutPanel();
        readonly Panel card = new Panel();
        readonly Table<RemoteDeployment> table = new Table<RemoteDeployment>();
        readonly Panel footer = new Panel();
        readonly Label status = new Label();
        readonly FlatButton bWatch = new FlatButton("Watch this device", Glyphs.Watch);
        readonly FlatButton bClose = new FlatButton("Close", null);
        readonly FlatButton bRefresh = new FlatButton("Refresh", Glyphs2.Sync);
        readonly TextBox search = new TextBox();
        readonly Segmented since = new Segmented("24 h", "3 d", "7 d", "30 d");
        readonly System.Windows.Forms.Timer auto = new System.Windows.Forms.Timer();
        static readonly int[] SinceHours = { 24, 72, 168, 720 };
        List<RemoteDeployment> all = new List<RemoteDeployment>();
        bool loading;

        // Intune
        FlatButton bSignIn, bCode;
        Label codeText;
        DeviceCode code;
        // SCCM
        TextBox server, site;
        FlatButton bConnect;

        public RemoteDialog(string kind, Theme t, IRemoteCatalog existing, GraphClient graph, bool demo)
        {
            theme = t;
            Catalog = existing;
            Graph = graph;
            Text = kind == Intune ? "Autopilot in Intune" : "Task sequences in SCCM";
            Icon = Program.AppIcon();
            Font = Fonts.Ui;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(Dpi.S(1180), Dpi.S(720));
            MinimumSize = new Size(Dpi.S(760), Dpi.S(480));

            head.Dock = DockStyle.Top;
            head.Height = Dpi.S(78);
            head.Padding = new Padding(Dpi.S(24), Dpi.S(14), Dpi.S(24), 0);
            title.Text = Text;
            title.Font = Fonts.Title;
            title.AutoSize = true;
            title.Location = new Point(Dpi.S(22), Dpi.S(12));
            desc.AutoSize = false;
            desc.Location = new Point(Dpi.S(24), Dpi.S(44));
            desc.Size = new Size(Dpi.S(1000), Dpi.S(34));
            desc.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            desc.Font = Fonts.Small;
            desc.Text = kind == Intune
                ? "Autopilot deployments as Intune reports them, read-only through Microsoft Graph. Intune only knows what the devices have reported, so it lags behind the device and has no log lines."
                : "Task sequences from the status messages each step sends to the site, read-only through the SMS Provider with your Windows account.";
            head.Controls.Add(title);
            head.Controls.Add(desc);

            connect.Dock = DockStyle.Top;
            connect.Height = Dpi.S(48);
            connect.Padding = new Padding(Dpi.S(22), Dpi.S(6), Dpi.S(22), 0);
            connect.WrapContents = false;
            if (kind == Intune) BuildIntune(); else BuildSccm();

            bar.Dock = DockStyle.Top;
            bar.Height = Dpi.S(46);
            bar.Padding = new Padding(Dpi.S(22), Dpi.S(6), Dpi.S(22), 0);
            bar.WrapContents = false;
            search.Width = Dpi.S(260);
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Margin = new Padding(0, Dpi.S(5), Dpi.S(12), 0);
            search.TextChanged += delegate { Bind(); };
            since.Selected = 1;
            since.Margin = new Padding(0, 0, Dpi.S(12), 0);
            since.Changed += Safe.Wrap(delegate { LoadList(); });
            themed.Add(since);
            bRefresh.Compact = true;
            bRefresh.AutoWidth();
            bRefresh.Margin = new Padding(0, Dpi.S(2), 0, 0);
            bRefresh.Click += Safe.Wrap(delegate { LoadList(); });
            themed.Add(bRefresh);
            bar.Controls.Add(search);
            bar.Controls.Add(since);
            bar.Controls.Add(bRefresh);

            table.Col("Device", 150, delegate (RemoteDeployment d) { return d.Device; });
            table.Col("State", 200, delegate (RemoteDeployment d) { return d.StateText; }).Fill = true;
            if (kind == Intune)
            {
                table.Col("User", 210, delegate (RemoteDeployment d) { return d.User; });
                table.Col("Profile", 190, delegate (RemoteDeployment d) { return d.Profile; }).Fill = true;
            }
            else
            {
                table.Col("Task sequence", 220, delegate (RemoteDeployment d) { return d.Profile; }).Fill = true;
                table.Col("Deployment", 100, delegate (RemoteDeployment d) { return d.User; });
            }
            table.Col("Started", 128, delegate (RemoteDeployment d) { return d.StartUtc.HasValue ? Txt.Local(d.StartUtc.Value).Substring(0, 16) : ""; });
            table.Col("Duration", 92, delegate (RemoteDeployment d) { return Txt.Duration(d.Duration(DateTime.UtcNow)); });
            table.Col(kind == Intune ? "Last report" : "Last status", 100, delegate (RemoteDeployment d) { return Txt.Ago(d.LastUtc, DateTime.UtcNow); });
            table.ForeColor = delegate (RemoteDeployment d) { return d.State == State.Failed ? theme.ErrFg : States.IsActive(d.State) ? theme.Accent : d.State == State.Unknown ? theme.Text3 : Color.Empty; };
            table.EmptyText = kind == Intune ? "Sign in to list the Autopilot deployments in Intune." : "Connect to the SMS Provider to list task sequences.";
            table.SelectionChanged += delegate { bWatch.Enabled = table.Current != null; };
            table.Grid.CellDoubleClick += delegate { Safe.Run(Pick); };
            themed.Add(table);
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Dpi.S(24), Dpi.S(4), Dpi.S(24), Dpi.S(4));
            Panel border = new Panel();
            border.Dock = DockStyle.Fill;
            border.Padding = new Padding(1);
            border.Tag = "border";
            table.Grid.Dock = DockStyle.Fill;
            border.Controls.Add(table.Grid);
            card.Controls.Add(border);

            footer.Dock = DockStyle.Bottom;
            footer.Height = Dpi.S(60);
            footer.Padding = new Padding(Dpi.S(24), Dpi.S(14), Dpi.S(24), Dpi.S(14));
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            bWatch.Primary = true;
            bWatch.Dock = DockStyle.Right;
            bWatch.Enabled = false;
            bWatch.Click += Safe.Wrap(delegate { Pick(); });
            bClose.Dock = DockStyle.Right;
            bClose.Click += delegate { Close(); };
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            themed.Add(bWatch);
            themed.Add(bClose);
            footer.Controls.Add(status);
            footer.Controls.Add(bClose);
            footer.Controls.Add(gap);
            footer.Controls.Add(bWatch);

            Controls.Add(card);
            Controls.Add(bar);
            Controls.Add(connect);
            Controls.Add(head);
            Controls.Add(footer);
            ApplyTheme(t);
            auto.Tick += Safe.Wrap(delegate { if (Catalog != null && !loading) LoadList(); });
            HandleCreated += delegate { Draw.TitleBar(Handle, theme); };
            HandleCreated += delegate
            {
                try { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Search devices, users and profiles"); } catch (Exception) { }
            };
            if (demo)
            {
                foreach (Control c in connect.Controls) c.Enabled = false;
                Label note = new Label();
                note.AutoSize = true;
                note.Margin = new Padding(Dpi.S(12), Dpi.S(9), 0, 0);
                note.Text = kind == Sccm ? "Demo data." : "Demo data: made-up devices, no sign-in needed.";
                note.Tag = "quiet";
                connect.Controls.Add(note);
                ApplyTheme(t);
            }
            Shown += Safe.Wrap(delegate
            {
                if (Catalog != null) Connected();
                else if (demo) { Catalog = DemoCatalog(kind); Connected(); }
            });
            FormClosed += delegate { cancel.Set(); auto.Stop(); };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static IRemoteCatalog DemoSccmCatalog()
        {
            SccmCatalog c = new SccmCatalog(new DemoSccm(DateTime.UtcNow), "Demo site");
            c.Dmtf = delegate (DateTime t) { return t.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".000000+000"; };
            return c;
        }

        public static IRemoteCatalog DemoCatalog(string kind)
        {
            return kind == Intune ? (IRemoteCatalog)new IntuneCatalog(new DemoGraph(DateTime.UtcNow)) : DemoSccmCatalog();
        }

        void BuildIntune()
        {
            bSignIn = new FlatButton("Sign in", Glyphs.Shield);
            bSignIn.Primary = true;
            bSignIn.Margin = new Padding(0, 0, Dpi.S(12), 0);
            bSignIn.Click += Safe.Wrap(delegate { SignIn(); });
            themed.Add(bSignIn);
            codeText = new Label();
            codeText.AutoSize = true;
            codeText.Margin = new Padding(0, Dpi.S(9), Dpi.S(12), 0);
            codeText.Text = "Read-only, with " + (Prefs.Get("GraphClientId", null) != null ? "the client ID set in Settings" : "Microsoft Graph Command Line Tools") + ". Needs DeviceManagementManagedDevices.Read.All.";
            bCode = new FlatButton("Copy code and open the sign-in page", Glyphs.Copy);
            bCode.Compact = true;
            bCode.AutoWidth();
            bCode.Margin = new Padding(0, Dpi.S(2), 0, 0);
            bCode.Visible = false;
            bCode.Click += Safe.Wrap(delegate { OpenSignIn(); });
            themed.Add(bCode);
            connect.Controls.Add(bSignIn);
            connect.Controls.Add(codeText);
            connect.Controls.Add(bCode);
        }

        void BuildSccm()
        {
            Label l1 = new Label();
            l1.Text = "SMS Provider";
            l1.AutoSize = true;
            l1.Margin = new Padding(0, Dpi.S(9), Dpi.S(8), 0);
            server = new TextBox();
            server.Width = Dpi.S(240);
            server.BorderStyle = BorderStyle.FixedSingle;
            server.Margin = new Padding(0, Dpi.S(6), Dpi.S(16), 0);
            server.Text = Prefs.Get("SccmServer", "");
            Label l2 = new Label();
            l2.Text = "Site code";
            l2.AutoSize = true;
            l2.Margin = new Padding(0, Dpi.S(9), Dpi.S(8), 0);
            site = new TextBox();
            site.Width = Dpi.S(70);
            site.BorderStyle = BorderStyle.FixedSingle;
            site.Margin = new Padding(0, Dpi.S(6), Dpi.S(16), 0);
            site.CharacterCasing = CharacterCasing.Upper;
            site.MaxLength = 3;
            site.Text = Prefs.Get("SccmSite", "");
            bConnect = new FlatButton("Connect", Glyphs2.Sync);
            bConnect.Primary = true;
            bConnect.Click += Safe.Wrap(delegate { ConnectSccm(); });
            themed.Add(bConnect);
            Label hint = new Label();
            hint.Text = "Site code is optional.";
            hint.AutoSize = true;
            hint.Margin = new Padding(Dpi.S(12), Dpi.S(9), 0, 0);
            hint.Tag = "quiet";
            connect.Controls.Add(l1);
            connect.Controls.Add(server);
            connect.Controls.Add(l2);
            connect.Controls.Add(site);
            connect.Controls.Add(bConnect);
            connect.Controls.Add(hint);
            server.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Safe.Run(ConnectSccm); } };
            site.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Safe.Run(ConnectSccm); } };
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            foreach (Control c in new Control[] { head, connect, bar, card, footer }) { c.BackColor = t.Bg; c.ForeColor = t.Text; }
            foreach (Control c in card.Controls) if ("border".Equals(c.Tag)) c.BackColor = t.Divider;
            title.ForeColor = t.Text;
            desc.ForeColor = t.Text3;
            status.ForeColor = t.Text3;
            foreach (Control c in connect.Controls)
            {
                Label l = c as Label;
                if (l != null) l.ForeColor = "quiet".Equals(l.Tag) || l == codeText ? t.Text3 : t.Text2;
                TextBox b = c as TextBox;
                if (b != null) { b.BackColor = t.Field; b.ForeColor = t.Text; }
            }
            search.BackColor = t.Field;
            search.ForeColor = t.Text;
            foreach (IThemed x in themed) x.ApplyTheme(t);
            if (IsHandleCreated) Draw.TitleBar(Handle, t);
        }

        void Status(string s)
        {
            status.Text = s;
        }

        void Ui(MethodInvoker m)
        {
            try { if (!IsDisposed) BeginInvoke((MethodInvoker)delegate { Safe.Run(delegate { m(); }); }); }
            catch (Exception) { }
        }

        // ---------- Intune ----------

        void SignIn()
        {
            if (Graph == null || Graph.SignedIn) Graph = new GraphClient(Prefs.Get("GraphClientId", null), Prefs.Get("GraphTenant", null));
            GraphClient g = Graph;
            bSignIn.Enabled = false;
            Status("Getting a sign-in code…");
            Thread th = new Thread(delegate ()
            {
                try
                {
                    DeviceCode d = g.Start();
                    Ui(delegate
                    {
                        code = d;
                        codeText.Text = "Code " + d.UserCode + ": open " + d.VerificationUri + " and sign in with an account that can read Intune devices.";
                        codeText.Font = Fonts.UiSemibold;
                        codeText.ForeColor = theme.Text;
                        bCode.Visible = true;
                        Status("Waiting for you to sign in. The code works for " + (d.ExpiresIn / 60) + " minutes.");
                    });
                    g.Wait(d, cancel);
                    Ui(delegate
                    {
                        Catalog = new IntuneCatalog(g);
                        codeText.Font = Fonts.Ui;
                        bCode.Visible = false;
                        bSignIn.Text = "Sign in again";
                        bSignIn.Primary = false;
                        bSignIn.AutoWidth();
                        bSignIn.Enabled = true;
                        bSignIn.ApplyTheme(theme);
                        Connected();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        bSignIn.Enabled = true;
                        bCode.Visible = false;
                        codeText.Font = Fonts.Ui;
                        Status("Sign-in failed: " + ex.Message);
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        void OpenSignIn()
        {
            if (code == null) return;
            Menus.Copy(code.UserCode);
            try { Process.Start(code.VerificationUri); }
            catch (Exception) { }
            Status("The code " + code.UserCode + " is on the clipboard. Paste it on the sign-in page.");
        }

        // ---------- SCCM ----------

        void ConnectSccm()
        {
            string srv = server.Text.Trim();
            string sc = site.Text.Trim();
            if (srv.Length == 0)
            {
                Status("Enter the server that runs the SMS Provider, usually the site server.");
                server.Focus();
                return;
            }
            bConnect.Enabled = false;
            Status("Connecting to " + srv + "…");
            Thread th = new Thread(delegate ()
            {
                try
                {
                    SccmWmi w = new SccmWmi(srv, sc);
                    Exception err;
                    if (!Guard.Run(delegate { w.Connect(); }, TimeSpan.FromSeconds(45), out err))
                        throw err ?? new TimeoutException(srv + " did not answer within 45 seconds.");
                    Ui(delegate
                    {
                        Prefs.Set("SccmServer", srv);
                        Prefs.Set("SccmSite", w.SiteCode);
                        site.Text = w.SiteCode;
                        bConnect.Enabled = true;
                        Catalog = new SccmCatalog(w, srv);
                        Connected();
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        bConnect.Enabled = true;
                        Status("Could not connect: " + Explain(ex));
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        static string Explain(Exception ex)
        {
            string m = ex.Message.Trim();
            if (ex is UnauthorizedAccessException || m.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
                return "access denied. Your account needs a ConfigMgr role, and DCOM and WMI access to the SMS Provider.";
            if (m.IndexOf("RPC server is unavailable", StringComparison.OrdinalIgnoreCase) >= 0)
                return "the RPC server is unavailable. Check the server name, and that the firewall lets WMI through to the SMS Provider.";
            if (m.IndexOf("Invalid namespace", StringComparison.OrdinalIgnoreCase) >= 0)
                return "that server has no SMS Provider for this site code. Check both.";
            return m;
        }

        // ---------- the list ----------

        void Connected()
        {
            table.EmptyText = "No deployments in this period.";
            auto.Interval = Math.Max(15, Catalog.RefreshSeconds) * 1000;
            auto.Start();
            LoadList();
        }

        void LoadList()
        {
            if (Catalog == null || loading) return;
            loading = true;
            bRefresh.Enabled = false;
            IRemoteCatalog c = Catalog;
            DateTime from = DateTime.UtcNow.AddHours(-SinceHours[Math.Max(0, Math.Min(SinceHours.Length - 1, since.Selected))]);
            Status("Reading " + c.Title + "…");
            Thread th = new Thread(delegate ()
            {
                List<RemoteDeployment> l = null;
                Exception err = null;
                try { l = c.List(from); }
                catch (Exception ex) { err = ex; }
                Ui(delegate
                {
                    loading = false;
                    bRefresh.Enabled = true;
                    if (err != null)
                    {
                        Status("Reading failed: " + (c is SccmCatalog ? Explain(err) : err.Message));
                        return;
                    }
                    all = l;
                    Bind();
                    int running = 0, failed = 0;
                    foreach (RemoteDeployment d in l) { if (States.IsActive(d.State)) running++; if (d.State == State.Failed) failed++; }
                    Status(c.Source + " · " + l.Count + " deployment" + (l.Count == 1 ? "" : "s") + ", " + running + " running, " + failed + " failed · updated " + DateTime.Now.ToString("HH:mm:ss") + ", again in " + (auto.Interval / 1000) + " s");
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        void Bind()
        {
            string q = search.Text.Trim();
            string keep = table.Current != null ? table.Current.Key : null;
            List<RemoteDeployment> rows = new List<RemoteDeployment>();
            foreach (RemoteDeployment d in all)
            {
                if (q.Length > 0 && !Has(d.Device, q) && !Has(d.User, q) && !Has(d.Profile, q) && !Has(d.Serial, q) && !Has(d.StateText, q)) continue;
                rows.Add(d);
            }
            table.SetRows(rows);
            if (keep != null) foreach (RemoteDeployment d in rows) if (d.Key == keep) { table.Select(d); break; }
            bWatch.Enabled = table.Current != null;
        }

        static bool Has(string s, string q)
        {
            return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Pick()
        {
            RemoteDeployment d = table.Current;
            if (d == null || Catalog == null) return;
            Picked = d;
            DialogResult = DialogResult.OK;
            Close();
        }

        // Selects the first deployment whose device matches, for screenshots.
        public void SelectDevice(string name)
        {
            foreach (RemoteDeployment d in table.Rows) if (Has(d.Device, name)) { table.Select(d); return; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.F5) { Safe.Run(LoadList); return true; }
            if (keyData == Keys.Enter && table.Grid.Focused) { Safe.Run(Pick); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
