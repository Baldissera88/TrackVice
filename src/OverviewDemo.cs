namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // Made-up devices for the demo of Overview: the agent devices and task sequences of Contoso deploying now, and a
    // week of finished Autopilot enrollments at Fabrikam. Nothing is read from anywhere.
    public static class OverviewDemo
    {
        public static List<IOverviewSource> Sources(DateTime nowUtc)
        {
            List<IOverviewSource> l = new List<IOverviewSource>();
            l.Add(new CatalogSource("Contoso", OverviewKinds.Agents, new AgentCatalog(new DemoAgentStore())));
            l.Add(new HistorySource("Fabrikam", nowUtc));
            SccmCatalog c = new SccmCatalog(new DemoSccm(nowUtc), "Demo site");
            c.Dmtf = delegate (DateTime t) { return t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".000000+000"; };
            l.Add(new CatalogSource("Contoso", OverviewKinds.Sccm, c));
            return l;
        }

        // Devices opened before, for the recent list of the demo.
        public static RecentStore Recent(DateTime nowUtc)
        {
            RecentStore s = new RecentStore(null);
            s.Touch(Recent(RecentSources.Agent, "Fabrikam", "FAB-LT0007", "sessions/fab-lt0007.json.gz", "Failed", State.Failed, nowUtc.AddHours(-26), "Fabrikam user-driven", "FAB0007XQ"));
            s.Touch(Recent(RecentSources.Sccm, "Contoso", "CONTOSO-PC109", "16777245|P0120006|0", "Completed", State.Succeeded, nowUtc.AddMinutes(-12), "Windows 11 24H2 Standard", ""));
            s.Touch(Recent(RecentSources.Agent, "Contoso", "CONTOSO-LT0433", "sessions/contoso-lt0433.json.gz", "24 of 26 done, 1 failed", State.Failed, nowUtc.AddMinutes(-4), "Contoso standard user-driven", ""));
            s.Touch(Recent(RecentSources.ThisPc, "", "CONTOSO-ADM01", "", "Nothing found yet", State.Unknown, nowUtc.AddHours(-3), "", ""));
            s.Touch(Recent(RecentSources.File, "", "CONTOSO-PC117", @"C:\Cases\CONTOSO-PC117.json", "31 of 31 done", State.Succeeded, nowUtc.AddDays(-2), "Windows 11 Enterprise", ""));
            RecentDevice pin = Recent(RecentSources.Intune, "Contoso", "CONTOSO-LT0398", "8c1e6f2a-4b7d-4e19-a3c5-9d0f1e2b3a4c", "Enrollment in progress", State.Installing, nowUtc.AddDays(-1), "Contoso standard user-driven", "5CG3091KLM");
            pin.User = "lars.berg@contoso.com";
            s.Touch(pin);
            s.Pin(pin.Key, true);
            return s;
        }

        static RecentDevice Recent(string source, string customer, string device, string id, string state, State st, DateTime when, string profile, string serial)
        {
            RecentDevice d = new RecentDevice();
            d.Source = source;
            d.Customer = customer;
            d.Device = device;
            d.Id = id;
            d.StateText = state;
            d.State = st;
            d.LastSeenUtc = when;
            d.Profile = profile;
            d.Serial = serial;
            return d;
        }

        // A week of finished enrollments, with whole sessions so the slowest and failing apps can be worked out.
        sealed class HistorySource : IOverviewSource, IOverviewRuns
        {
            readonly string customer;
            readonly List<RemoteDeployment> rows = new List<RemoteDeployment>();
            readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();

            static readonly string[][] Apps =
            {
                new string[] { "Company Portal", "2" },
                new string[] { "Contoso VPN Client 5.2", "5" },
                new string[] { "Microsoft 365 Apps", "21" },
                new string[] { "Fabrikam CRM 8.1", "9" },
                new string[] { "Adobe Acrobat Reader", "6" },
            };

            public HistorySource(string customer, DateTime nowUtc)
            {
                this.customer = customer;
                now = nowUtc;
                for (int n = 0; n < 14; n++)
                {
                    string name = "FAB-LT" + (n + 1).ToString("0000", CultureInfo.InvariantCulture);
                    DateTime start = nowUtc.AddHours(-(n * 11 + 2.5));
                    bool failed = n == 1 || n == 6 || n == 9;
                    Session s = new Session();
                    s.Computer = name;
                    s.User = "user" + (n + 1) + "@fabrikam.com";
                    Tracker tr = new Tracker(s);
                    tr.EnsurePhase(Phases.AccountSetup, Phases.Name(Phases.AccountSetup), Phases.Order(Phases.AccountSetup));
                    DateTime t = start;
                    for (int k = 0; k < Apps.Length; k++)
                    {
                        double min = double.Parse(Apps[k][1], CultureInfo.InvariantCulture) * (0.8 + ((n * 7 + k * 3) % 5) * 0.1);
                        Item i = new Item();
                        i.Key = "intune:demo-" + k;
                        i.Name = Apps[k][0];
                        i.Kind = k == 0 ? Kinds.StoreApp : Kinds.Win32App;
                        i.PhaseKey = Phases.AccountSetup;
                        i.Order = k;
                        i.State = failed && k == 3 ? State.Failed : State.Succeeded;
                        if (i.State == State.Failed) i.ExitCode = 1603;
                        i.StartUtc = t;
                        i.FirstUtc = t;
                        t = t.AddMinutes(min);
                        i.EndUtc = t;
                        i.LastUtc = t;
                        s.Items.Add(i);
                        if (failed && k == 3) break;
                    }
                    s.Rebuild();
                    RemoteDeployment d = new RemoteDeployment();
                    d.Key = AgentData.BlobName(name.ToLowerInvariant());
                    d.Device = name;
                    d.User = "Autopilot";
                    d.Profile = "Fabrikam user-driven";
                    d.Serial = "FAB" + (n + 1).ToString("0000", CultureInfo.InvariantCulture) + "XQ";
                    d.StartUtc = start;
                    d.EndUtc = t;
                    d.LastUtc = t;
                    d.State = failed ? State.Failed : State.Succeeded;
                    d.StateText = failed ? "1 failed, " + (s.Items.Count - 1) + " of " + s.Items.Count + " done" : "Done, " + s.Items.Count + " items";
                    rows.Add(d);
                    sessions[d.Key] = s;
                }
                RemoteDeployment quiet = new RemoteDeployment();
                quiet.Key = AgentData.BlobName("fab-lt0015");
                quiet.Device = "FAB-LT0015";
                quiet.User = "Autopilot";
                quiet.Profile = "Fabrikam user-driven";
                quiet.StartUtc = nowUtc.AddMinutes(-70);
                quiet.LastUtc = nowUtc.AddMinutes(-42);
                quiet.State = State.Unknown;
                quiet.StateText = "No upload for 42 min · 2 of 5 done · Installing Microsoft 365 Apps";
                rows.Add(quiet);
            }

            public string Customer { get { return customer; } }
            public string Kind { get { return OverviewKinds.Agents; } }
            public int RefreshSeconds { get { return 60; } }
            public IRemoteCatalog Catalog { get { return null; } }

            public List<RemoteDeployment> List(DateTime sinceUtc)
            {
                List<RemoteDeployment> l = new List<RemoteDeployment>();
                foreach (RemoteDeployment d in rows) if (d.LastUtc >= sinceUtc) l.Add(d);
                return l;
            }

            // Older enrollments, some on devices of the week, so a few devices were deployed more than once.
            static readonly object[][] Older =
            {
                new object[] { "FAB-LT0002", 9.0, true }, new object[] { "FAB-LT0002", 31.0, true }, new object[] { "FAB-LT0007", 15.0, false },
                new object[] { "FAB-LT0004", 12.0, false }, new object[] { "FAB-LT0021", 18.0, false }, new object[] { "FAB-LT0022", 22.0, true },
                new object[] { "FAB-LT0023", 27.0, false }, new object[] { "FAB-LT0024", 44.0, false }, new object[] { "FAB-LT0007", 61.0, true },
                new object[] { "FAB-LT0025", 75.0, false }, new object[] { "FAB-LT0026", 120.0, false }, new object[] { "FAB-LT0021", 140.0, true },
            };
            readonly DateTime now;

            public List<RemoteDeployment> Runs(DateTime sinceUtc)
            {
                List<RemoteDeployment> l = new List<RemoteDeployment>(rows);
                for (int k = 0; k < Older.Length; k++)
                {
                    RemoteDeployment d = new RemoteDeployment();
                    string name = (string)Older[k][0];
                    bool failed = (bool)Older[k][2];
                    d.Key = AgentData.BlobName(name.ToLowerInvariant() + "-" + k.ToString(CultureInfo.InvariantCulture));
                    d.Device = name;
                    d.User = "Autopilot";
                    d.Profile = "Fabrikam user-driven";
                    d.StartUtc = now.AddDays(-(double)Older[k][1]);
                    d.EndUtc = d.StartUtc.Value.AddMinutes(52);
                    d.LastUtc = d.EndUtc.Value;
                    d.State = failed ? State.Failed : State.Succeeded;
                    d.StateText = failed ? "1 failed, 4 of 5 done" : "Done, 5 items";
                    if (d.LastUtc >= sinceUtc) l.Add(d);
                }
                return l;
            }

            public Session Load(RemoteDeployment d)
            {
                Session s;
                return sessions.TryGetValue(d.Key, out s) ? s : null;
            }
        }
    }
}
