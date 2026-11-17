namespace TrackVice
{
    using System.Windows.Forms;

    // The switch in the task sequence list that also shows the runs before the newest one of each device and deployment.
    public sealed partial class RemoteDialog
    {
        Label earlierLabel;
        Toggle earlier;

        void BuildEarlierRuns()
        {
            earlierLabel = new Label();
            earlierLabel.Text = "Show earlier runs";
            earlierLabel.AutoSize = true;
            earlierLabel.Margin = new Padding(Dpi.S(20), Dpi.S(9), Dpi.S(4), 0);
            earlier = new Toggle();
            earlier.Margin = new Padding(0, Dpi.S(1), 0, 0);
            earlier.On = Prefs.GetBool("SccmEarlierRuns", false);
            earlier.Changed += Safe.Wrap(delegate
            {
                Prefs.SetBool("SccmEarlierRuns", earlier.On);
                LoadList();
            });
            themed.Add(earlier);
            bar.Controls.Add(earlierLabel);
            bar.Controls.Add(earlier);
        }

        void ThemeEarlierRuns(Theme t)
        {
            if (earlierLabel != null) earlierLabel.ForeColor = t.Text2;
        }

        void EarlierRunsTo(IRemoteCatalog c)
        {
            SccmCatalog sc = c as SccmCatalog;
            if (sc != null && earlier != null) sc.EarlierRuns = earlier.On;
        }

        // Says how many runs the list leaves out, and reads again when the switch changed during the read.
        void NoteEarlierRuns(IRemoteCatalog c)
        {
            SccmCatalog sc = c as SccmCatalog;
            if (sc == null || earlier == null) return;
            if (sc.EarlierRuns != earlier.On) { LoadList(); return; }
            if (!sc.EarlierRuns && sc.Earlier > 0) Status(status.Text + " · " + sc.Earlier + " earlier run" + (sc.Earlier == 1 ? "" : "s") + " not shown");
        }
    }
}
