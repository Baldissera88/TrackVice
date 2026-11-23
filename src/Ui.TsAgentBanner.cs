namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Threading;
    using System.Windows.Forms;

    // The second line under the Publish banner, when the TrackVice package is older. The rule is in TsPackageCheck.
    public sealed class TsPackageBanner : Panel, IThemed
    {
        public readonly Label Text2 = new Label();
        public readonly FlatButton Open = new FlatButton(TsPackageCheck.Button, null);

        public TsPackageBanner()
        {
            Dock = DockStyle.Top;
            Height = Dpi.S(44);
            Padding = new Padding(Dpi.S(20), Dpi.S(6), Dpi.S(20), Dpi.S(6));
            Text2.AutoSize = false;
            Text2.TextAlign = ContentAlignment.MiddleLeft;
            Text2.UseMnemonic = false;
            Open.Compact = true;
            Open.AutoWidth();
            Controls.Add(Text2);
            Controls.Add(Open);
            Visible = false;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (arranging) return;
            arranging = true;
            try { Arrange(); }
            finally { arranging = false; }
        }

        bool arranging;

        void Arrange()
        {
            Open.AutoWidth();
            int bw = Open.Width, bh = Open.Height;
            int avail = Math.Max(Dpi.S(40), ClientSize.Width - Padding.Horizontal);
            bool one = avail >= bw + Dpi.S(280);
            int tw = one ? avail - bw - Dpi.S(12) : avail;
            Size need = TextRenderer.MeasureText(Text2.Text ?? "", Text2.Font, new Size(tw, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int h;
            if (one)
            {
                int line = Math.Max(need.Height, bh);
                Text2.Bounds = new Rectangle(Padding.Left, Padding.Top, tw, line);
                Open.Bounds = new Rectangle(Padding.Left + tw + Dpi.S(12), Padding.Top + (line - bh) / 2, bw, bh);
                h = Padding.Top + line + Padding.Bottom;
            }
            else
            {
                Text2.Bounds = new Rectangle(Padding.Left, Padding.Top, tw, need.Height + Dpi.S(4));
                Open.Bounds = new Rectangle(Padding.Left, Padding.Top + need.Height + Dpi.S(8), Math.Min(bw, avail), bh);
                h = Padding.Top + need.Height + Dpi.S(8) + bh + Padding.Bottom;
            }
            if (Height != h) Height = h;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PerformLayout();
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.Card;
            Text2.BackColor = t.Card;
            Text2.ForeColor = t.Text;
            Text2.Font = Fonts.Ui;
            Open.ApplyTheme(t);
        }
    }

    public sealed partial class MainForm
    {
        TsPackageBanner tsBanner;
        bool tsChecked;

        // --page publishnote --select tspackage: the second line with made-up versions.
        void ShotTsPackageLine()
        {
            tsBanner = new TsPackageBanner();
            themed.Add(tsBanner);
            Controls.Add(tsBanner);
            Controls.SetChildIndex(tsBanner, Controls.GetChildIndex(publishBanner ?? (Control)busyBar));
            tsBanner.ApplyTheme(theme);
            tsBanner.Text2.Text = TsPackageCheck.Text("1.4.0", YourStorageUi.RunningVersion);
            tsBanner.Visible = true;
        }

        // Called when the Publish banner shows. Looks away from the window, once: the share and the site can be slow.
        void ShowTsPackageLine(string running)
        {
            if (tsBanner != null && tsBanner.Visible) return;
            if (tsChecked) return;
            string server = Prefs.Get("SccmServer", "");
            if (server.Length == 0) return;
            tsChecked = true;
            string site = Prefs.Get("SccmSite", ""), remembered = Prefs.Get("TsAgentSource", "");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string old = TsPackageCheck.Older(remembered, delegate
                {
                    SccmWmi w = new SccmWmi(server, site);
                    Exception err;
                    if (!Guard.Run(delegate { w.Connect(); }, TimeSpan.FromSeconds(20), out err) || err != null) return null;
                    System.Collections.Generic.List<SmsPackage> p = new SmsSiteWmi(w).FindPackages(TsAgentPackage.Name);
                    return p.Count > 0 ? p[0].SourcePath : null;
                }, new FileSys(Program.Location), Program.Version);
                if (old == null) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Safe.Run(delegate
                        {
                            if (publishBanner == null || !publishBanner.Visible) { tsChecked = false; return; }
                            if (tsBanner == null)
                            {
                                tsBanner = new TsPackageBanner();
                                tsBanner.Open.Click += Safe.Wrap(delegate { OpenTsAgent(); });
                                themed.Add(tsBanner);
                                Controls.Add(tsBanner);
                                Controls.SetChildIndex(tsBanner, Controls.GetChildIndex(publishBanner));
                                tsBanner.ApplyTheme(theme);
                                publishBanner.VisibleChanged += delegate { if (!publishBanner.Visible) tsBanner.Visible = false; };
                            }
                            tsBanner.Text2.Text = TsPackageCheck.Text(old, running);
                            tsBanner.Visible = true;
                            tsBanner.PerformLayout();
                        });
                    });
                }
                catch (Exception) { }
            });
        }
    }
}
