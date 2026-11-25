namespace TrackVice
{
    using System;
    using System.Windows.Forms;

    // Where Overview gets the version published to Your storage.
    public static class AgentVersionUi
    {
        public static void Wire(bool demo)
        {
            AgentVersions.RunningVersion = delegate { return YourStorageUi.RunningVersion; };
            if (demo)
            {
                AgentVersions.Published = delegate { return YourStorageUi.RunningVersion; };
                AgentVersions.Places = delegate { return UpdatePlacesUi.DemoPlaces(YourStorageUi.RunningVersion); };
            }
            else
            {
                AgentVersions.Published = delegate
                {
                    OwnStorage o = YourStorageUi.Load();
                    return o != null && !string.IsNullOrEmpty(o.PublishedVersion) ? o.PublishedVersion : null;
                };
                AgentVersions.Places = delegate { return UpdatePlacesUi.Build(); };
            }
        }
    }

    public sealed partial class MainForm
    {
        // The Agent part of the Session tab: the version, when the agent was installed and when it last updated itself.
        void ShowAgentFacts(RichTextBox r, Session s)
        {
            Section(r, "Agent");
            Field(r, "Network outages seen by the agent", NetworkOutages.Summary(s) ?? "None reported", false);
            Field(r, "Version", string.IsNullOrEmpty(s.AppVersion) ? "Not known" : "TrackVice " + s.AppVersion, false);
            DateTime? installed = AgentVersions.Installed(s);
            DateTime? updated = AgentVersions.Updated(s);
            DateTime? check = AgentVersions.Checked(s);
            DateTime now = DateTime.UtcNow;
            if (installed.HasValue) Field(r, "Installed", Txt.Local(installed.Value) + " (" + Txt.Ago(installed.Value, now) + ")", false);
            if (updated.HasValue) Field(r, "Last self-update", Txt.Local(updated.Value) + " (" + Txt.Ago(updated.Value, now) + ")", false);
            else if (installed.HasValue) Field(r, "Last self-update", "Not since it was installed", false);
            Field(r, "Self-update", AgentVersions.StateText(AgentVersions.State(s)), false);
            if (check.HasValue) Field(r, "Last update check", Txt.Local(check.Value) + " (" + Txt.Ago(check.Value, now) + ")", false);
            if (!installed.HasValue && AgentVersions.State(s) == null)
                Field(r, "Installed", "Not sent by this agent. An agent in a task sequence is not installed, and older agents do not send it.", false);
        }
    }
}
