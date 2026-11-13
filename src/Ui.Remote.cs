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
        public const string Intune = "intune", Sccm = "sccm", Agents = "agents";
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
        FlatButton bSignIn;
        Label codeText;
        DeviceCode code;
        SignInPanel signIn;
        ManualResetEvent attempt;
        readonly bool isDemo;
        // SCCM
        TextBox server, site;
        FlatButton bConnect;

        public RemoteDialog(string kind, Theme t, IRemoteCatalog existing, GraphClient graph, bool demo)
        {
            theme = t;
            isDemo = demo;
            Catalog = existing;
            Graph = graph;
            Text = kind == Intune ? "Autopilot in Intune" : kind == Agents ? "Devices with the TrackVice agent" : "Task sequences in SCCM";
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
                : kind == Agents ? "Devices that run the TrackVice agent upload their whole session while they deploy: every app, policy and step, with the log lines behind them. This lists what they have uploaded."
                : "Task sequences from the status messages each step sends to the site, read-only through the SMS Provider with your Windows account.";
            head.Controls.Add(title);
            head.Controls.Add(desc);

            connect.Dock = DockStyle.Top;
            connect.Height = Dpi.S(48);
            connect.Padding = new Padding(Dpi.S(22), Dpi.S(6), Dpi.S(22), 0);
            connect.WrapContents = false;
            if (kind == Intune) BuildIntune(); else if (kind == Agents) BuildAgents(); else BuildSccm();

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
            else if (kind == Agents)
            {
                table.Col("Deployment", 150, delegate (RemoteDeployment d) { return d.User; });
                table.Col("Profile or task sequence", 190, delegate (RemoteDeployment d) { return d.Profile; });
            }
            else
            {
                table.Col("Task sequence", 220, delegate (RemoteDeployment d) { return d.Profile; }).Fill = true;
                table.Col("Deployment", 100, delegate (RemoteDeployment d) { return d.User; });
            }
            table.Col("Started", 128, delegate (RemoteDeployment d) { return d.StartUtc.HasValue ? Txt.Local(d.StartUtc.Value).Substring(0, 16) : ""; });
            table.Col("Duration", 92, delegate (RemoteDeployment d) { return Txt.Duration(d.Duration(DateTime.UtcNow)); });
            table.Col(kind == Intune ? "Last report" : kind == Agents ? "Last upload" : "Last status", 100, delegate (RemoteDeployment d) { return Txt.Ago(d.LastUtc, DateTime.UtcNow); });
            table.ForeColor = delegate (RemoteDeployment d) { return d.State == State.Failed ? theme.ErrFg : States.IsActive(d.State) ? theme.Accent : d.State == State.Unknown ? theme.Text3 : Color.Empty; };
            table.EmptyText = kind == Intune ? "Sign in to list the Autopilot deployments in Intune." : kind == Agents ? "Enter where the agents upload to, and connect.\n\nThat is the SAS link of the storage container, with Read and List, or a folder." : "Connect to the SMS Provider to list task sequences.";
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
            if (kind == Intune)
            {
                signIn = new SignInPanel();
                signIn.Dock = DockStyle.Fill;
                signIn.Visible = existing == null && !demo;
                signIn.Main.Click += Safe.Wrap(delegate { SignIn(); });
                signIn.Copy.Click += Safe.Wrap(delegate { if (code != null) { Menus.Copy(code.UserCode); signIn.Copied(); } });
                signIn.Open.Click += Safe.Wrap(delegate { OpenSignIn(); });
                signIn.Cancel.Click += Safe.Wrap(delegate { CancelSignIn(); });
                themed.Add(signIn);
                border.Controls.Add(signIn);
                signIn.BringToFront();
                connect.Visible = !signIn.Visible;
                bar.Visible = !signIn.Visible;
            }
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
                try { SendMessage(search.Handle, 0x1501, (IntPtr)1, kind == Agents ? "Search devices and profiles" : "Search devices, users and profiles"); } catch (Exception) { }
            };
            if (demo)
            {
                foreach (Control c in connect.Controls) c.Enabled = false;
                Label note = new Label();
                note.AutoSize = true;
                note.Margin = new Padding(Dpi.S(12), Dpi.S(9), 0, 0);
                note.Text = kind != Intune ? "Demo data: made-up devices." : "Demo data: made-up devices, no sign-in needed.";
                note.Tag = "quiet";
                connect.Controls.Add(note);
                ApplyTheme(t);
            }
            Shown += Safe.Wrap(delegate
            {
                if (Catalog != null) Connected();
                else if (demo) { Catalog = DemoCatalog(kind); Connected(); }
            });
            FormClosed += delegate { cancel.Set(); if (attempt != null) attempt.Set(); auto.Stop(); };
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
            if (kind == Agents) return new AgentCatalog(new DemoAgentStore());
            return kind == Intune ? (IRemoteCatalog)new IntuneCatalog(new DemoGraph(DateTime.UtcNow)) : DemoSccmCatalog();
        }

        void BuildIntune()
        {
            codeText = new Label();
            codeText.AutoSize = true;
            codeText.Margin = new Padding(0, Dpi.S(9), Dpi.S(12), 0);
            codeText.Text = "Signed in to Intune, read-only.";
            bSignIn = new FlatButton("Use another account", Glyphs.Shield);
            bSignIn.Compact = true;
            bSignIn.AutoWidth();
            bSignIn.Margin = new Padding(0, Dpi.S(2), 0, 0);
            bSignIn.Click += Safe.Wrap(delegate { SwitchAccount(); });
            themed.Add(bSignIn);
            connect.Controls.Add(codeText);
            connect.Controls.Add(bSignIn);
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

        // Shows a state of the sign-in panel without signing in, for screenshots.
        public void PreviewSignIn(string state)
        {
            if (signIn == null) return;
            auto.Stop();
            Catalog = null;
            ShowSignIn(true);
            if (state == "code") { signIn.ShowCode("GG9PRR88C", "https://login.microsoft.com/device", DateTime.UtcNow.AddMinutes(15)); signIn.Opened(true, true); }
            else if (state == "error") signIn.ShowError(SignInHelp("AADSTS65001: The user or administrator has not consented to use the application."));
            else signIn.Show(SignInPanel.Start);
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

        void ShowSignIn(bool show)
        {
            if (signIn == null) return;
            signIn.Visible = show;
            connect.Visible = !show;
            bar.Visible = !show;
            if (show) signIn.BringToFront();
        }

        // Back to the sign-in panel, to sign in with a different account.
        void SwitchAccount()
        {
            if (isDemo) return;
            auto.Stop();
            Catalog = null;
            Graph = null;
            all = new List<RemoteDeployment>();
            Bind();
            signIn.Show(SignInPanel.Start);
            ShowSignIn(true);
            Status("");
        }

        void CancelSignIn()
        {
            if (attempt != null) attempt.Set();
            code = null;
            signIn.Show(SignInPanel.Start);
            Status("");
        }

        // Gets a code, copies it and opens the sign-in page, then waits until the person has signed in.
        void SignIn()
        {
            Graph = new GraphClient(Prefs.Get("GraphClientId", null), Prefs.Get("GraphTenant", null));
            GraphClient g = Graph;
            ManualResetEvent mine = new ManualResetEvent(false);
            attempt = mine;
            signIn.Show(SignInPanel.Working);
            Status("");
            Thread th = new Thread(delegate ()
            {
                try
                {
                    DeviceCode d = g.Start();
                    if (mine.WaitOne(0)) return;
                    Ui(delegate
                    {
                        if (attempt != mine || mine.WaitOne(0)) return;
                        code = d;
                        signIn.ShowCode(d.UserCode, d.VerificationUri, DateTime.UtcNow.AddSeconds(d.ExpiresIn));
                        OpenSignIn();
                    });
                    g.Wait(d, mine);
                    Ui(delegate
                    {
                        if (attempt != mine) return;
                        Catalog = new IntuneCatalog(g);
                        codeText.Text = string.IsNullOrEmpty(g.Account) ? "Signed in to Intune, read-only." : "Signed in as " + g.Account + ", read-only.";
                        ShowSignIn(false);
                        Connected();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        if (attempt != mine) return;
                        signIn.ShowError(SignInHelp(ex.Message));
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        // Says what to do about the sign-in errors people actually meet.
        static string SignInHelp(string m)
        {
            m = (m ?? "").Trim();
            string low = m.ToLowerInvariant();
            if (low.Contains("aadsts65001") || low.Contains("consent") || low.Contains("aadsts90094"))
                return "Your organization has not approved this yet. An administrator has to consent to DeviceManagementManagedDevices.Read.All for Microsoft Graph Command Line Tools, or for your own app set in Settings.\n\n" + m;
            if (low.Contains("expired")) return "The code expired before the sign-in was finished. Try again: you get a new code.";
            if (low.Contains("declined") || low.Contains("access_denied")) return "The sign-in was cancelled or declined in the browser.";
            if (low.Contains("could not reach")) return "TrackVice could not reach Microsoft's sign-in service. Check the internet connection and any proxy.\n\n" + m;
            if (low.Contains("aadsts50059") || low.Contains("aadsts90002") || low.Contains("tenant")) return "The tenant in Settings was not found. Clear it to sign in to the tenant of your account.\n\n" + m;
            return m;
        }

        void OpenSignIn()
        {
            if (code == null) return;
            bool copied = Menus.Copy(code.UserCode);
            bool opened = true;
            try { Process.Start(code.VerificationUri); }
            catch (Exception) { opened = false; }
            signIn.Opened(opened, copied);
        }

        // ---------- SCCM ----------

        void BuildAgents()
        {
            Label l1 = new Label();
            l1.Text = "Upload location";
            l1.AutoSize = true;
            l1.Margin = new Padding(0, Dpi.S(9), Dpi.S(8), 0);
            server = new TextBox();
            server.Width = Dpi.S(470);
            server.BorderStyle = BorderStyle.FixedSingle;
            server.Margin = new Padding(0, Dpi.S(6), Dpi.S(16), 0);
            server.Text = isDemo ? "" : Secret.Load("AgentStore");
            bConnect = new FlatButton("Connect", Glyphs2.Sync);
            bConnect.Primary = true;
            bConnect.Click += Safe.Wrap(delegate { ConnectAgents(); });
            themed.Add(bConnect);
            Label hint = new Label();
            hint.Text = "A container SAS link with Read and List, or a folder.";
            hint.AutoSize = true;
            hint.Margin = new Padding(Dpi.S(12), Dpi.S(9), 0, 0);
            hint.Tag = "quiet";
            connect.Controls.Add(l1);
            connect.Controls.Add(server);
            connect.Controls.Add(bConnect);
            connect.Controls.Add(hint);
            server.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Safe.Run(ConnectAgents); } };
        }

        void ConnectAgents()
        {
            string where = server.Text.Trim();
            if (where.Length == 0)
            {
                Status("Enter where the agents upload to: the SAS link of the storage container, or a folder.");
                server.Focus();
                return;
            }
            bConnect.Enabled = false;
            Status("Connecting\u2026");
            Thread th = new Thread(delegate ()
            {
                try
                {
                    ISessionStore st = Stores.Open(where);
                    st.List(AgentData.Prefix);
                    Ui(delegate
                    {
                        Secret.Save("AgentStore", where);
                        bConnect.Enabled = true;
                        Catalog = new AgentCatalog(st);
                        Connected();
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        bConnect.Enabled = true;
                        Status("Could not connect: " + ex.Message);
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

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

    // The sign-in for Intune, in the middle of the list: one button to start, then the code, large, with what to do next.
    public sealed class SignInPanel : FlatControl
    {
        public const int Start = 0, Working = 1, Code = 2, Error = 3;
        public readonly FlatButton Main = new FlatButton("Sign in with Microsoft", Glyphs.Shield);
        public readonly FlatButton Copy = new FlatButton("Copy the code", Glyphs.Copy);
        public readonly FlatButton Open = new FlatButton("Open the sign-in page", null);
        public readonly FlatButton Cancel = new FlatButton("Cancel", null);
        int mode;
        string code = "", url = "", error = "", hint = "";
        DateTime expires, copiedAt;
        float spin;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        Font codeFont;

        public SignInPanel()
        {
            Main.Primary = true;
            Main.Height = Dpi.S(38);
            Main.Width += Dpi.S(24);
            Copy.Primary = true;
            foreach (FlatButton b in new FlatButton[] { Main, Copy, Open, Cancel }) Controls.Add(b);
            anim.Interval = 60;
            anim.Tick += delegate
            {
                spin = (spin + 14) % 360;
                if (Visible && (mode == Working || mode == Code)) Invalidate();
            };
            anim.Start();
            Show(Start);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Dispose(); if (codeFont != null) codeFont.Dispose(); }
            base.Dispose(disposing);
        }

        public override void ApplyTheme(Theme t)
        {
            base.ApplyTheme(t);
            BackColor = t.Card;
            foreach (Control c in Controls) { IThemed x = c as IThemed; if (x != null) x.ApplyTheme(t); }
        }

        public void Show(int m)
        {
            mode = m;
            Main.Text = m == Error ? "Try again" : "Sign in with Microsoft";
            Main.AutoWidth();
            Main.Width += Dpi.S(24);
            Main.Visible = m == Start || m == Error;
            Copy.Visible = Open.Visible = m == Code;
            Cancel.Visible = m == Code || m == Working;
            Place();
            Invalidate();
        }

        public void ShowCode(string userCode, string page, DateTime expiresUtc)
        {
            code = userCode ?? "";
            url = page ?? "";
            expires = expiresUtc;
            hint = "";
            Show(Code);
        }

        public void ShowError(string message)
        {
            error = message ?? "";
            Show(Error);
        }

        // After the code was copied and the page opened (or not).
        public void Opened(bool opened, bool copied)
        {
            if (opened && copied) hint = "Your browser has opened the sign-in page, and the code is copied. Paste it there with Ctrl+V and sign in.";
            else if (opened) hint = "Your browser has opened the sign-in page. Type the code there and sign in.";
            else hint = "Open " + url + " in a browser, enter the code and sign in.";
            if (copied) copiedAt = DateTime.UtcNow;
            Place();
            Invalidate();
        }

        public void Copied()
        {
            copiedAt = DateTime.UtcNow;
            Invalidate();
        }

        int ColW
        {
            get { return Math.Min(Dpi.S(560), Math.Max(Dpi.S(240), Width - Dpi.S(48))); }
        }

        Font CodeFont
        {
            get
            {
                if (codeFont == null) codeFont = new Font(Fonts.Mono.FontFamily, 26f, FontStyle.Bold);
                return codeFont;
            }
        }

        string Lead
        {
            get
            {
                if (mode == Code) return hint.Length > 0 ? hint : "Enter this code on the sign-in page.";
                if (mode == Working) return "Getting a sign-in code from Microsoft\u2026";
                if (mode == Error) return error;
                return "TrackVice reads the Autopilot deployment status from Intune. It is read-only: it cannot change devices, apps or policies. The sign-in is kept in memory and never saved.";
            }
        }

        string Heading
        {
            get { return mode == Code ? "Enter this code to sign in" : mode == Error ? "The sign-in did not work" : mode == Working ? "One moment" : "Sign in to see Autopilot deployments"; }
        }

        const string Small1 = "Use an account that can read devices in Intune. The first time, your organization may ask an administrator to approve read access (DeviceManagementManagedDevices.Read.All).";

        // Heights of the parts, so painting and button placement agree.
        void Measure(out int top, out int leadH, out int codeY, out int buttonsY, out int footY)
        {
            int w = ColW;
            leadH = Draw.WrapHeight(Lead, Fonts.Ui, w);
            int markH = mode == Code || mode == Working ? 0 : Dpi.S(56) + Dpi.S(14);
            int headH = Fonts.Title.Height + Dpi.S(8);
            int codeH = mode == Code ? Dpi.S(76) + Dpi.S(18) : 0;
            int btnH = mode == Working ? 0 : Dpi.S(38) + Dpi.S(18);
            int footH = mode == Start ? Draw.WrapHeight(Small1, Fonts.Small, w) : mode == Code || mode == Working ? Dpi.S(56) : 0;
            int total = markH + headH + leadH + Dpi.S(18) + codeH + btnH + footH;
            top = Math.Max(Dpi.S(16), (Height - total) / 2);
            int y = top + markH + headH + leadH + Dpi.S(18);
            codeY = y;
            y += codeH;
            buttonsY = y;
            y += btnH;
            footY = y;
        }

        void Place()
        {
            if (Width <= 0) return;
            int top, leadH, codeY, buttonsY, footY;
            Measure(out top, out leadH, out codeY, out buttonsY, out footY);
            int cx = Width / 2;
            if (mode == Start || mode == Error) Main.Location = new Point(cx - Main.Width / 2, buttonsY);
            if (mode == Code)
            {
                int gap = Dpi.S(10), w = Copy.Width + gap + Open.Width;
                Copy.Location = new Point(cx - w / 2, buttonsY + Dpi.S(3));
                Open.Location = new Point(Copy.Right + gap, buttonsY + Dpi.S(3));
            }
            if (mode == Code || mode == Working) Cancel.Location = new Point(cx - Cancel.Width / 2, footY + Dpi.S(28));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Place();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Card);
            int top, leadH, codeY, buttonsY, footY;
            Measure(out top, out leadH, out codeY, out buttonsY, out footY);
            int w = ColW, x = (Width - w) / 2, y = top;
            TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            if (mode == Start || mode == Error)
            {
                int m = Dpi.S(56);
                Draw.Mark(g, new RectangleF(Width / 2f - m / 2f, y, m, m));
                y += m + Dpi.S(14);
            }
            TextRenderer.DrawText(g, Heading, Fonts.Title, new Rectangle(x, y, w, Fonts.Title.Height + 2), mode == Error ? T.ErrFg : T.Text, center);
            y += Fonts.Title.Height + Dpi.S(8);
            TextRenderer.DrawText(g, Lead, Fonts.Ui, new Rectangle(x, y, w, leadH + 2), T.Text2, center | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            if (mode == Code)
            {
                Size cs = TextRenderer.MeasureText(code, CodeFont, Size.Empty, TextFormatFlags.NoPadding);
                int bw = Math.Min(w, cs.Width + Dpi.S(72)), bh = Dpi.S(76);
                RectangleF box = new RectangleF(Width / 2f - bw / 2f, codeY, bw, bh);
                Shapes.Fill(g, box, Dpi.S(10), T.AccentSoft);
                Shapes.Stroke(g, box, Dpi.S(10), T.Accent, 1.5f);
                TextRenderer.DrawText(g, code, CodeFont, Rectangle.Round(box), T.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
            if (mode == Start) TextRenderer.DrawText(g, Small1, Fonts.Small, new Rectangle(x, footY, w, Height - footY), T.Text3, center | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            if (mode == Code || mode == Working)
            {
                string wait;
                if (mode == Working) wait = "Contacting Microsoft";
                else
                {
                    double left = (expires - DateTime.UtcNow).TotalMinutes;
                    wait = (DateTime.UtcNow - copiedAt).TotalSeconds < 4 ? "Code copied" : "Waiting for you to sign in \u00b7 the code works for " + Math.Max(1, (int)Math.Ceiling(left)) + " more min";
                }
                Size ws = TextRenderer.MeasureText(wait, Fonts.Ui, Size.Empty, TextFormatFlags.NoPadding);
                int ic = Dpi.S(16), tw = ic + Dpi.S(8) + ws.Width, sx = Width / 2 - tw / 2;
                int sy = mode == Working ? buttonsY : footY;
                StateLook.Icon(g, State.Installing, new RectangleF(sx, sy + 1, ic, ic), T, spin);
                TextRenderer.DrawText(g, wait, Fonts.Ui, new Point(sx + ic + Dpi.S(8), sy), T.Text3, TextFormatFlags.NoPadding);
            }
        }
    }
}
