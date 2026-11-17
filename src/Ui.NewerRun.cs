namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Globalization;
    using System.Windows.Forms;

    // A slim line under the toolbar when a newer run of the same device and deployment began while an SCCM run is followed.
    public sealed class NewerRunBar : Panel, IThemed
    {
        public readonly Label Line = new Label();
        public readonly FlatButton Follow = new FlatButton("Follow it", null);

        public NewerRunBar()
        {
            Dock = DockStyle.Top;
            Height = Dpi.S(44);
            Padding = new Padding(Dpi.S(20), Dpi.S(6), Dpi.S(20), Dpi.S(6));
            Line.Dock = DockStyle.Fill;
            Line.TextAlign = ContentAlignment.MiddleLeft;
            Line.AutoEllipsis = true;
            Follow.Primary = true;
            Follow.Compact = true;
            Follow.AutoWidth();
            Follow.Dock = DockStyle.Right;
            Controls.Add(Follow);
            Controls.Add(Line);
            Line.BringToFront();
        }

        public void ApplyTheme(Theme t)
        {
            BackColor = t.AccentSoft;
            Line.BackColor = t.AccentSoft;
            Line.ForeColor = t.Text;
            Follow.ApplyTheme(t);
        }
    }

    public sealed partial class MainForm
    {
        NewerRunBar newerBar;

        // --page newerrun with --remote sccm --pick <device>: follows the oldest run of that device, so the line shows.
        public const string NewerRunPage = "newerrun";

        bool ShotNewerRun(IRemoteCatalog c)
        {
            SccmCatalog sc = c as SccmCatalog;
            if (start.Page != NewerRunPage || sc == null) return false;
            sc.EarlierRuns = true;
            RemoteDeployment pick = null;
            foreach (RemoteDeployment d in sc.List(DateTime.UtcNow.AddDays(-30)))
                if (d.Device.IndexOf(start.Pick, StringComparison.OrdinalIgnoreCase) >= 0 && (pick == null || d.StartUtc < pick.StartUtc)) pick = d;
            if (pick == null) return false;
            Begin(Engine.ForRemote(c.Open(pick), pick), true);
            return true;
        }

        // Shows or hides the line after each read of an SCCM run.
        void UpdateNewerRun(Engine e)
        {
            SccmFeed feed = e != null ? e.Remote as SccmFeed : null;
            DateTime? newer = feed != null ? feed.NewerRunUtc : null;
            if (!newer.HasValue)
            {
                if (newerBar != null) newerBar.Visible = false;
                return;
            }
            if (newerBar == null)
            {
                newerBar = new NewerRunBar();
                newerBar.Follow.Click += Safe.Wrap(delegate { FollowNewerRun(); });
                themed.Add(newerBar);
                Controls.Add(newerBar);
                Controls.SetChildIndex(newerBar, Controls.GetChildIndex(busyBar));
                newerBar.ApplyTheme(theme);
            }
            newerBar.Line.Text = NewerRunText.Line(newer.Value, DateTime.UtcNow);
            newerBar.Visible = true;
        }

        void FollowNewerRun()
        {
            SccmFeed feed = engine != null ? engine.Remote as SccmFeed : null;
            RemoteDeployment d = feed != null ? feed.NewerRun() : null;
            if (d == null) return;
            if (newerBar != null) newerBar.Visible = false;
            Begin(Engine.ForRemote(feed.ForRun(d.StartUtc.Value), d), true);
        }
    }
}
