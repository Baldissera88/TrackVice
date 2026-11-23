namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class AgentVersionTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            Func<string> published = AgentVersions.Published;
            try
            {
                Versions();
                Upload();
                Behind();
                Tiles();
                Report();
                Banner();
            }
            finally { AgentVersions.Published = published; }
        }

        static void Versions()
        {
            T.Check(string.CompareOrdinal(AgentVersions.SortKey("1.10.0"), AgentVersions.SortKey("1.9.3")) > 0, "versions sort by number");
            T.Eq("", AgentVersions.SortKey("unknown"), "text that is not a version sorts first");
            T.Check(AgentVersions.Older("1.4.0", "1.5.0") && !AgentVersions.Older("1.5.0", "1.5.0") && !AgentVersions.Older("1.4.0", null), "older than the published version");
            T.Eq("Agent 1.5.0", AgentVersions.Header("1.5.0"), "the header names the agent version");
            T.Eq("Agent", AgentVersions.Header(null), "and only Agent when it is not known");
            T.Eq("3 on 1.5.0, 2 on 1.4.0", AgentVersions.Counts(new List<string> { "1.4.0", "1.5.0", "1.5.0", "1.4.0", "1.5.0" }), "devices per version, newest first");
            T.Eq(AgentVersions.NoKey, AgentVersions.OffState("", "https://contoso.blob.core.windows.net/trackvice-agent?sv=1"), "no key in the script");
            T.Eq(AgentVersions.NoLink, AgentVersions.OffState("key", " "), "no link in the script");
            T.Eq(AgentVersions.BadKey, AgentVersions.OffState("not a key", "https://contoso.blob.core.windows.net/trackvice-agent?sv=1"), "a key that cannot be used");
        }

        static Session AgentSession(string version)
        {
            Session s = new Session();
            s.Computer = "CONTOSO-PC01";
            s.AppVersion = version;
            return s;
        }

        static void Upload()
        {
            Session s = AgentSession("1.5.0");
            AgentVersions.Stamp(s, Now.AddHours(-20), Now.AddHours(-2), AgentVersions.On, Now.AddHours(-2));
            Dictionary<string, string> m = AgentData.Summary(s, Now, false);
            T.Eq("1.5.0", m["version"], "the version is still uploaded as before");
            T.Eq("2026-11-17T16:00:00Z", m[AgentVersions.MetaInstalled], "the install time is in the metadata");
            T.Eq("2026-11-18T10:00:00Z", m[AgentVersions.MetaUpdated], "the last self-update is in the metadata");
            T.Eq(AgentVersions.On, m[AgentVersions.MetaState], "the self-update state is in the metadata");
            Session back = AgentData.Unpack(AgentData.Pack(s));
            T.Eq(Now.AddHours(-20), AgentVersions.Installed(back).Value, "the install time survives the upload");
            T.Eq(Now.AddHours(-2), AgentVersions.Updated(back).Value, "the last self-update survives the upload");

            StoreEntry e = new StoreEntry();
            e.Name = AgentData.BlobName("contoso-pc01");
            e.ModifiedUtc = Now;
            foreach (KeyValuePair<string, string> kv in m) if (kv.Value != null) e.Meta[kv.Key] = kv.Value;
            T.Eq("1.5.0", AgentData.ToDeployment(e, Now).Version, "the device list has the version");
            T.Eq("1.5.0", WallModel.Tile(e, Now).Version, "the wall has the version");
            T.Check(WallModel.Tile(e, Now).Matches("1.5.0"), "searching the wall finds the version");

            StoreEntry old = new StoreEntry();
            old.Name = AgentData.BlobName("contoso-pc02");
            old.ModifiedUtc = Now;
            old.Meta["computer"] = "CONTOSO-PC02";
            T.Check(AgentData.ToDeployment(old, Now).Version == null, "an upload without a version still lists");
        }

        static OverviewDevice Dev(string name, string version, DateTime last, Session s)
        {
            OverviewDevice o = new OverviewDevice();
            o.Customer = "Contoso";
            o.Kind = OverviewKinds.Agents;
            o.D = new RemoteDeployment();
            o.D.Device = name;
            o.D.Key = name;
            o.D.User = "Autopilot";
            o.D.Version = version;
            o.D.LastUtc = last;
            o.D.StartUtc = last.AddHours(-1);
            o.D.State = State.Installing;
            o.D.StateText = "3 of 12 done";
            o.S = s;
            return o;
        }

        static void Behind()
        {
            Session noKey = AgentSession("1.4.0");
            AgentVersions.Stamp(noKey, Now.AddHours(-9), null, AgentVersions.NoKey, null);
            OverviewDevice a = Dev("CONTOSO-PC01", "1.4.0", Now.AddMinutes(-1), noKey);
            T.Check(AgentVersions.Behind(a, "1.5.0", Now), "an old agent without a key that runs 9 hours stays behind");
            T.Check(!AgentVersions.Behind(a, "1.4.0", Now), "not when it runs the published version");

            Session fresh = AgentSession("1.4.0");
            AgentVersions.Stamp(fresh, Now.AddHours(-2), null, AgentVersions.NoKey, null);
            T.Check(!AgentVersions.Behind(Dev("CONTOSO-PC02", "1.4.0", Now.AddMinutes(-1), fresh), "1.5.0", Now), "not within the first 6 hours");

            Session checkedSoon = AgentSession("1.4.0");
            AgentVersions.Stamp(checkedSoon, Now.AddHours(-30), null, AgentVersions.On, Now.AddHours(-1));
            T.Check(!AgentVersions.Behind(Dev("CONTOSO-PC03", "1.4.0", Now.AddMinutes(-1), checkedSoon), "1.5.0", Now), "not when it checked an hour ago and the next check gets it");

            Session checkedLong = AgentSession("1.4.0");
            AgentVersions.Stamp(checkedLong, Now.AddHours(-30), null, AgentVersions.On, Now.AddHours(-8));
            T.Check(AgentVersions.Behind(Dev("CONTOSO-PC04", "1.4.0", Now.AddMinutes(-1), checkedLong), "1.5.0", Now), "a last check 8 hours ago is too long");

            T.Check(!AgentVersions.Behind(Dev("CONTOSO-PC05", "1.4.0", Now.AddHours(-3), noKey), "1.5.0", Now), "not when the device does not upload any more");

            Session updated = AgentSession("1.4.0");
            AgentVersions.Stamp(updated, Now.AddHours(-30), Now.AddHours(-3), AgentVersions.Signature, null);
            T.Check(!AgentVersions.Behind(Dev("CONTOSO-PC06", "1.4.0", Now.AddMinutes(-1), updated), "1.5.0", Now), "the 6 hours count from the last self-update");

            OverviewDevice old = Dev("CONTOSO-PC07", "1.3.0", Now.AddMinutes(-1), null);
            old.D.StartUtc = Now.AddHours(-10);
            T.Check(AgentVersions.Behind(old, "1.5.0", Now), "an agent that sends no facts is judged by when it started");
        }

        static void Tiles()
        {
            Session noKey = AgentSession("1.4.0");
            AgentVersions.Stamp(noKey, Now.AddHours(-9), null, AgentVersions.NoKey, null);
            Session soon = AgentSession("1.4.0");
            AgentVersions.Stamp(soon, Now.AddHours(-30), null, AgentVersions.On, Now.AddHours(-1));
            OverviewSnapshot snap = new OverviewSnapshot();
            snap.Agents = true;
            snap.Devices.Add(Dev("CONTOSO-PC01", "1.5.0", Now.AddMinutes(-1), AgentSession("1.5.0")));
            snap.Devices.Add(Dev("CONTOSO-PC02", "1.5.0", Now.AddMinutes(-2), AgentSession("1.5.0")));
            snap.Devices.Add(Dev("CONTOSO-PC03", "1.4.0", Now.AddMinutes(-1), noKey));
            snap.Devices.Add(Dev("CONTOSO-PC04", "1.4.0", Now.AddMinutes(-1), soon));
            snap.Devices.Add(Dev("CONTOSO-PC05", "1.2.0", Now.AddDays(-4), null));
            AgentVersions.Published = delegate { return "1.5.0"; };
            OverviewData d = OverviewModel.Build(snap, null, Now, Now.Date, null);
            AttentionTile agents = null, behind = null;
            foreach (AttentionTile t in d.Attention)
            {
                if (t.Key == "agents") agents = t;
                if (t.Key == "agents-behind") behind = t;
            }
            T.Check(agents != null, "Overview has an Agents tile");
            T.Eq("2 on 1.5.0, 2 on 1.4.0 (they update within 6 hours). Your storage has 1.5.0.", agents != null ? agents.Text : null, "it counts the devices per version");
            T.Eq(4, agents != null ? agents.Count : 0, "devices that have not uploaded for 2 days are not counted");
            T.Check(behind != null && behind.Count == 1 && behind.Devices[0].D.Device == "CONTOSO-PC03", "Needs attention has the agent that does not update");
            T.Check(behind != null && behind.Text.Contains("no update key"), "with the likely reason");
            T.Check(behind != null && behind.Level == 1, "as a warning");

            AgentVersions.Published = null;
            OverviewData none = OverviewModel.Build(snap, null, Now, Now.Date, null);
            bool any = false;
            foreach (AttentionTile t in none.Attention) if (t.Key == "agents-behind") any = true;
            T.Check(!any, "without Your storage nothing is behind");

            OverviewData empty = OverviewModel.Build(new OverviewSnapshot(), null, Now, Now.Date, null);
            T.Eq(0, empty.Attention.Count, "no Agents tile without devices");
        }

        static void Report()
        {
            Session s = AgentSession("1.5.0");
            s.Mode = Modes.Agent;
            AgentVersions.Stamp(s, Now.AddHours(-20), null, AgentVersions.Signature, null);
            s.SetInfo("Tenant", "contoso.onmicrosoft.com");
            string html = TrackVice.Report.Html(s, Now);
            T.Check(html.Contains("Agent version") && html.Contains("TrackVice 1.5.0"), "the report has the agent version");
            T.Check(html.Contains("signature check failed"), "and the self-update state in words");
            T.Check(!html.Contains(AgentVersions.CheckedKey) && !html.Contains(">signature<"), "not the raw values");
            Session live = AgentSession("1.5.0");
            live.Mode = Modes.Live;
            T.Eq(0, AgentVersions.ReportRows(live).Count, "a session not from the agent has no agent rows");

            UpdateCheck r = new UpdateCheck();
            T.Eq(AgentVersions.Invalid, r.Why, "an update check that did not get far is not valid");
        }

        static void Banner()
        {
            int[] buttons = { 80, 190, 60 };
            BannerFlow wide = BannerFlow.Arrange(1560, buttons, 6, 280);
            T.Check(wide.OneRow && wide.Inside(1560, buttons), "a wide window has the buttons on the text's line");
            T.Eq(1560 - 80 - 190 - 60 - 12 - 12, wide.TextWidth, "the text gets the rest");
            BannerFlow narrow = BannerFlow.Arrange(560, buttons, 6, 280);
            T.Check(!narrow.OneRow && narrow.Rows == 1 && narrow.Inside(560, buttons), "a narrow window puts the buttons below the text");
            T.Eq(560, narrow.TextWidth, "and the text wraps over the whole width");
            BannerFlow tiny = BannerFlow.Arrange(200, buttons, 6, 280);
            T.Check(tiny.Rows > 1 && tiny.Inside(200, buttons), "a very narrow window wraps the buttons too");
            foreach (int w in new int[] { 1000, 1300, 1600 }) T.Check(BannerFlow.Arrange(w - 40, buttons, 6, 280).Inside(w - 40, buttons), "the buttons stay inside at " + w + " px");
        }
    }
}
