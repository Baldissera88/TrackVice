namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // The two places for other devices: Intune and the cloud, and SCCM on premises. A session or a row in a list
    // belongs to one of them or to both, such as a task sequence followed by Autopilot.
    public static class DeviceSides
    {
        public const string Intune = "intune", Sccm = "sccm", Both = "both";

        // Set by /side on the command line, for /fleet-report and /export-data.
        public static string CliFilter;

        public static string Name(string side)
        {
            return side == Sccm ? "SCCM / on-premises" : side == Intune ? "Intune / cloud" : "Intune and SCCM";
        }

        // intune, sccm or both from a word typed on the command line. Null when it is none of them.
        public static string Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "intune":
                case "cloud":
                case "autopilot": return Intune;
                case "sccm":
                case "configmgr":
                case "onprem":
                case "on-premises": return Sccm;
                case "both":
                case "all":
                case "": return Both;
                default: return null;
            }
        }

        // The side of a session from its phases: task sequences and SCCM software are SCCM, the ESP, Intune apps and
        // MDM policies are Intune. Null when it has nothing of either yet.
        public static string Of(Session s)
        {
            if (s == null) return null;
            bool cm = false, intune = false;
            foreach (Item i in s.Items)
            {
                string k = i.PhaseKey ?? "";
                if (k.StartsWith("ts:") || k.StartsWith("cm:")) cm = true;
                else if (k.StartsWith("esp:") || k.StartsWith("intune:") || k.StartsWith("mdm:")) intune = true;
            }
            if (cm && intune) return Both;
            return cm ? Sccm : intune ? Intune : null;
        }

        // The side of a deployment from its kind as Session.Kind writes it, such as "Task sequence + Autopilot".
        public static string OfKind(string kind)
        {
            string k = (kind ?? "").ToLowerInvariant();
            bool cm = k.Contains("task sequence") || k.Contains("sccm");
            bool intune = k.Contains("autopilot") || k.Contains("intune");
            if (cm && intune) return Both;
            return cm ? Sccm : intune ? Intune : null;
        }

        // Whether something of this side shows under the filter. Under both, everything shows. A session of both sides
        // shows under either, and one with nothing yet only under both.
        public static bool Matches(string side, string filter)
        {
            if (filter == null || filter == Both) return true;
            return side == filter || side == Both;
        }

        public static List<Session> Filter(List<Session> sessions, string filter)
        {
            if (filter == null || filter == Both) return sessions;
            List<Session> l = new List<Session>();
            foreach (Session s in sessions) if (Matches(Of(s), filter)) l.Add(s);
            return l;
        }

        // The same, keeping the keys that go with the sessions in step.
        public static void Filter(List<Session> sessions, List<string> keys, string filter, List<Session> outSessions, List<string> outKeys)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                if (!Matches(Of(sessions[i]), filter)) continue;
                outSessions.Add(sessions[i]);
                outKeys.Add(keys != null && i < keys.Count ? keys[i] : null);
            }
        }

        // The side the Devices menu starts on when none was chosen yet: SCCM only when a site is known and nothing of Intune is.
        public static string Default(SideFacts f)
        {
            if (f != null && f.SccmServer && !f.IntuneAny) return Sccm;
            return Intune;
        }

        public static string Remembered(string saved, SideFacts f)
        {
            return saved == Intune || saved == Sccm ? saved : Default(f);
        }
    }

    // What is set up on this PC, for the empty state of each side.
    public sealed class SideFacts
    {
        // Your storage is set up.
        public bool Storage;
        // A customer has a tenant or an upload location.
        public bool Customers;
        // Signed in to Intune in this run.
        public bool SignedIn;
        // An SMS Provider is known for the customer in use.
        public bool SccmServer;

        public bool IntuneAny
        {
            get { return Storage || Customers || SignedIn; }
        }
    }

    // One entry of the Devices menu. The window maps Id to what it does.
    public sealed class DeviceMenuEntry
    {
        public string Id, Text;
        public bool Separator;

        public DeviceMenuEntry(string id, string text)
        {
            Id = id;
            Text = text;
        }

        public static DeviceMenuEntry Line()
        {
            DeviceMenuEntry e = new DeviceMenuEntry(null, null);
            e.Separator = true;
            return e;
        }
    }

    // What the Devices menu holds on each side. The side in use comes first, then what works for both, then the demos.
    public static class DeviceMenu
    {
        public const string AddCustomer = "addcustomer", Customers = "customers", Intune = "intune", Agents = "agents",
            Readiness = "readiness", Preflight = "preflight", Names = "names", NewCustomer = "newcustomer",
            Sccm = "sccm", SmsProvider = "smsprovider", TsAgent = "tsagent", TsAgents = "tsagents",
            Stats = "stats", Wall = "wall", Compare = "compare", Reference = "reference",
            Watch = "watch",
            DemoIntune = "demo-intune", DemoAgents = "demo-agents", DemoSccm = "demo-sccm", DemoStats = "demo-stats",
            DemoWall = "demo-wall", DemoCompare = "demo-compare", DemoReadiness = "demo-readiness", DemoPreflight = "demo-preflight",
            DemoNewCustomer = "demo-newcustomer", DemoAddCustomer = "demo-addcustomer";

        // Every entry the Devices menu had before it was split, so a test can check that none went missing.
        public static readonly string[] Before =
        {
            AddCustomer, Intune, Agents, Sccm, Stats, Wall, Compare, Reference, Names, Readiness, NewCustomer, Watch,
            DemoIntune, DemoAgents, DemoSccm, DemoStats, DemoWall, DemoCompare, DemoReadiness, DemoNewCustomer, DemoAddCustomer
        };

        public static List<DeviceMenuEntry> For(string side)
        {
            List<DeviceMenuEntry> l = new List<DeviceMenuEntry>();
            if (side == DeviceSides.Sccm)
            {
                l.Add(new DeviceMenuEntry(Sccm, "Task sequences in SCCM…"));
                l.Add(new DeviceMenuEntry(TsAgents, "Devices with the agent in a task sequence…"));
                l.Add(DeviceMenuEntry.Line());
                l.Add(new DeviceMenuEntry(SmsProvider, "SMS Provider connection…"));
                l.Add(new DeviceMenuEntry(TsAgent, "Add the agent to a task sequence…"));
            }
            else
            {
                l.Add(new DeviceMenuEntry(Intune, "Autopilot in Intune…"));
                l.Add(new DeviceMenuEntry(Agents, "Devices with the TrackVice agent…"));
                l.Add(new DeviceMenuEntry(Readiness, "Check devices before enrollment…"));
                l.Add(new DeviceMenuEntry(Preflight, "Pre-flight check…"));
                l.Add(new DeviceMenuEntry(Names, "Names from Intune…"));
                l.Add(DeviceMenuEntry.Line());
                l.Add(new DeviceMenuEntry(AddCustomer, "Add customer…"));
                l.Add(new DeviceMenuEntry(Customers, "Customers…"));
                l.Add(new DeviceMenuEntry(NewCustomer, "New customer with links…"));
            }
            l.Add(DeviceMenuEntry.Line());
            l.Add(new DeviceMenuEntry(Stats, "Statistics for devices with the agent…"));
            l.Add(new DeviceMenuEntry(Wall, "Wall view…"));
            l.Add(new DeviceMenuEntry(Compare, "Compare with another enrollment"));
            l.Add(new DeviceMenuEntry(Reference, null));
            l.Add(DeviceMenuEntry.Line());
            l.Add(new DeviceMenuEntry(Watch, "Watch this device"));
            l.Add(DeviceMenuEntry.Line());
            if (side == DeviceSides.Sccm)
            {
                l.Add(new DeviceMenuEntry(DemoSccm, "Demo: task sequences in SCCM"));
            }
            else
            {
                l.Add(new DeviceMenuEntry(DemoIntune, "Demo: Autopilot in Intune"));
                l.Add(new DeviceMenuEntry(DemoAgents, "Demo: devices with the agent"));
                l.Add(new DeviceMenuEntry(DemoReadiness, "Demo: check before enrollment"));
                l.Add(new DeviceMenuEntry(DemoPreflight, "Demo: pre-flight check"));
                l.Add(new DeviceMenuEntry(DemoNewCustomer, "Demo: new customer"));
                l.Add(new DeviceMenuEntry(DemoAddCustomer, "Demo: add customer"));
            }
            l.Add(new DeviceMenuEntry(DemoStats, "Demo: statistics"));
            l.Add(new DeviceMenuEntry(DemoWall, "Demo: wall view"));
            l.Add(new DeviceMenuEntry(DemoCompare, "Demo: compare two enrollments"));
            return l;
        }

        // The short text at the top of a side with nothing set up yet, and the entry that starts it. Null when the side is ready.
        public static string[] Empty(string side, SideFacts f)
        {
            if (f == null) f = new SideFacts();
            if (side == DeviceSides.Sccm)
            {
                if (f.SccmServer) return null;
                return new string[]
                {
                    "No SCCM site yet",
                    "To start, enter the SMS Provider of your site, usually the site server. It needs remote WMI to it and a role that can read deployment status, such as Read-only Analyst.",
                    SmsProvider, "Enter the SMS Provider…"
                };
            }
            if (f.IntuneAny) return null;
            return new string[]
            {
                "Nothing set up for Intune yet",
                "Autopilot in Intune needs only a sign-in to the tenant. To see everything a device sees, set up Your storage once and add a customer or your own tenant.",
                AddCustomer, "Add customer…"
            };
        }
    }
}
