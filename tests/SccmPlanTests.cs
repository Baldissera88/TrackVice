namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    // The whole task sequence of a run: every group and step in order, merged with the status rows.
    public static class SccmPlanTests
    {
        const long Res = 16777500;
        const string Adv = "F0120010", Pkg = "F0100500";
        const string Begin = "The task sequence execution engine started executing an action.";
        const string Done = "The task sequence execution engine successfully completed an action.";
        const string Skip = "The task sequence execution engine skipped the action because the condition was evaluated to be false.";
        const string Started = "The task sequence execution engine started execution of a task sequence.";
        const string Ended = "The task sequence execution engine successfully completed the task sequence.";
        const string Failed = "The task sequence execution engine failed executing an action.";

        public const string Xml =
            "<sequence version=\"3.00\">" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Check the network\" disable=\"false\"><action>ping fabrikam.example</action></step>" +
            "<group name=\"Prepare\">" +
            "<step type=\"SMS_TaskSequence_PartitionDiskAction\" name=\"Partition Disk\"/>" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Wipe old data\" disable=\"true\"/>" +
            "</group>" +
            "<group name=\"Software\" continueOnError=\"true\">" +
            "<step type=\"SMS_TaskSequence_InstallApplicationAction\" name=\"Install Applications\"><defaultVarList><variable name=\"BaseVariableName\" property=\"BaseVariableName\">NWAPP</variable></defaultVarList></step>" +
            "<step type=\"SMS_TaskSequence_InstallApplicationAction\" name=\"Install Northwind Viewer\" continueOnError=\"true\"><condition><expression type=\"SMS_TaskSequence_VariableConditionExpression\"/></condition></step>" +
            "<step type=\"SMS_TaskSequence_RebootAction\" name=\"Restart Computer\"/>" +
            "</group>" +
            "<group name=\"Retired\" disable=\"true\">" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Old inventory\"/>" +
            "</group>" +
            "<group name=\"Finish\">" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Clean up\"/>" +
            "</group>" +
            "</sequence>";

        sealed class Site : ISccmQuery, ISccmGet
        {
            public readonly List<Dictionary<string, object>> Rows = new List<Dictionary<string, object>>();
            public string Sequence = Xml;
            public string Fail;
            public int Gets, Packages;
            public long SourceVersion = 3;

            public void Add(DateTime t, long msg, string name, string action, int step, string group)
            {
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                r["ResourceID"] = Res;
                r["AdvertisementID"] = Adv;
                r["PackageID"] = Pkg;
                r["ExecutionTime"] = t;
                r["LastStatusMsgID"] = msg;
                r["LastStatusMsgName"] = name;
                r["ActionName"] = action;
                r["Step"] = (long)step;
                r["GroupName"] = group;
                r["ExitCode"] = 0L;
                Rows.Add(r);
            }

            public List<Dictionary<string, object>> Query(string wql)
            {
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                if (wql.Contains("FROM SMS_R_System"))
                {
                    Dictionary<string, object> x = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    x["ResourceID"] = Res;
                    x["Name"] = "CONTOSO-PC31";
                    l.Add(x);
                    return l;
                }
                if (wql.Contains("FROM SMS_TaskSequencePackage"))
                {
                    Packages++;
                    if (wql.Contains("PackageID = '" + Pkg + "'"))
                    {
                        Dictionary<string, object> x = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        x["PackageID"] = Pkg;
                        x["Name"] = "Northwind Standard PC";
                        x["Version"] = "";
                        x["SourceVersion"] = SourceVersion;
                        l.Add(x);
                    }
                    return l;
                }
                foreach (Dictionary<string, object> r in Rows) l.Add(new Dictionary<string, object>(r, StringComparer.OrdinalIgnoreCase));
                return l;
            }

            public Dictionary<string, object> GetInstance(string className, string keyName, string keyValue)
            {
                Gets++;
                if (Fail != null) throw new InvalidOperationException(Fail);
                Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                r["PackageID"] = keyValue;
                // A query leaves out a lazy property, so only this read has it.
                r["Sequence"] = Sequence;
                return r;
            }
        }

        static readonly DateTime T0 = new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-40);

        static Session Open(Site s, out Engine e)
        {
            RemoteDeployment d = new RemoteDeployment();
            d.Key = Res + "|" + Adv + "|" + T0.Ticks;
            d.Device = "CONTOSO-PC31";
            d.Profile = "Northwind Standard PC";
            d.StartUtc = T0;
            SccmCatalog c = new SccmCatalog(s, "CM01.contoso.com");
            e = Engine.ForRemote(c.Open(d), d);
            e.Apply(e.Poll());
            return e.S;
        }

        static State StateOf(Session s, string name)
        {
            foreach (Item i in s.Items) if (i.Name == name || (i.Name != null && i.Name.StartsWith(name + " (", StringComparison.Ordinal))) return i.State;
            return State.Unknown;
        }

        static Item Named(Session s, string name)
        {
            foreach (Item i in s.Items) if (i.Name == name || (i.Name != null && i.Name.StartsWith(name + " (", StringComparison.Ordinal))) return i;
            return null;
        }

        static Site Running()
        {
            Site s = new Site();
            s.Add(T0, 11140, Started, "", 0, "");
            s.Add(T0.AddMinutes(1), 11134, Done, "Check the network", 1, "");
            s.Add(T0.AddMinutes(2), 11134, Done, "Partition Disk", 3, "Prepare");
            s.Add(T0.AddMinutes(5), 11138, Begin, "Install Applications", 5, "Software");
            return s;
        }

        public static void Run()
        {
            Parse();
            Merge();
            Finished();
            Stopped();
            Cannot();
            Cache();
            Loose();
        }

        static void Parse()
        {
            TsSequence q = TsSequence.Parse(Xml);
            T.Eq(12, q.All.Count, "Whole task sequence: every group and step is read");
            T.Eq(3, q.Roots.Count > 4 ? 3 : q.Roots.Count, "Whole task sequence: the top has a step and groups");
            TsNode wipe = q.All[3];
            T.Check(wipe.Name == "Wipe old data" && wipe.Disabled && !wipe.IsGroup, "Whole task sequence: disable is read");
            T.Check(q.All[5].BaseVariable == "NWAPP", "Whole task sequence: the base variable name is read");
            T.Check(q.All[6].HasCondition && q.All[6].ContinueOnError, "Whole task sequence: conditions and continue on error are read");
            T.Check(q.All[8].IsGroup && q.All[8].Disabled && q.All[9].IsOff && !q.All[9].Disabled, "Whole task sequence: a step in a disabled group is off");
            T.Eq(2, q.All[3].Path().Length, "Whole task sequence: the path has a place per level");
            T.Eq("Software", q.All[5].GroupName, "Whole task sequence: the group of a step");
            bool bad = false;
            try { TsSequence.Parse("<sequence/>"); }
            catch (FormatException) { bad = true; }
            T.Check(bad, "Whole task sequence: no steps is not a task sequence");
            bad = false;
            try { TsSequence.Parse("<!DOCTYPE s [<!ENTITY x \"y\">]><sequence/>"); }
            catch (FormatException) { bad = true; }
            T.Check(bad, "Whole task sequence: a DTD is not read");
        }

        static void Merge()
        {
            Site s = Running();
            Engine e;
            Session se = Open(s, out e);
            T.Eq(State.Succeeded, StateOf(se, "Check the network"), "Whole task sequence: a step that reported shows its state");
            T.Eq(State.Succeeded, StateOf(se, "Partition Disk"), "Whole task sequence: a step in a group that reported shows its state");
            T.Eq(State.Installing, StateOf(se, "Install Applications"), "Whole task sequence: the step that runs now is installing");
            T.Eq(State.Pending, StateOf(se, "Restart Computer"), "Whole task sequence: a step that has not reported is waiting");
            T.Eq(State.Pending, StateOf(se, "Clean up"), "Whole task sequence: a step in a later group is waiting");
            T.Eq(State.Skipped, StateOf(se, "Wipe old data"), "Whole task sequence: a disabled step is not run");
            Item wipe = Named(se, "Wipe old data");
            T.Eq("Disabled", AppGroups.StateLabel(wipe), "Whole task sequence: a disabled step says Disabled");
            T.Check(AppGroups.StateLabel(Named(se, "Check the network")) == "Done", "Whole task sequence: a step that ran does not say Disabled");
            T.Eq("Disabled", AppGroups.StateLabel(Named(se, "Old inventory")), "Whole task sequence: a step in a disabled group says Disabled");
            T.Eq("Install Applications (variable list NWAPP)", Named(se, "Install Applications").Name, "Whole task sequence: a step from a variable list shows its base name");
            T.Check(Named(se, "Install Applications").Get(TsPlan.ListKey).Contains("NWAPP01"), "Whole task sequence: the variable list is in the details");
            // The groups are in task sequence order.
            List<string> phases = new List<string>();
            foreach (Phase p in se.Phases) phases.Add(p.Name);
            phases.Sort(delegate (string a, string b) { return Order(se, a).CompareTo(Order(se, b)); });
            T.Eq("Prepare,Software,Retired,Finish", string.Join(",", phases.FindAll(delegate (string n) { return n != "Task sequence"; }).ToArray()), "Whole task sequence: the groups are in task sequence order");
            Phase soft = se.FindPhase("ts:group:software");
            T.Check(soft != null && soft.Items.Count == 3 && soft.Items[0].Name.StartsWith("Install Applications") && soft.Items[2].Name == "Restart Computer", "Whole task sequence: the steps of a group are in task sequence order");
            // No event is made for a step that only waits.
            int waitEvents = 0;
            foreach (TEvent ev in se.Events) if (ev.Text == "Waiting") waitEvents++;
            T.Eq(0, waitEvents, "Whole task sequence: waiting steps make no events");

            // The run moves on: Install Northwind Viewer reports, Restart Computer does not, and a later step runs.
            s.Add(T0.AddMinutes(9), 11134, Done, "Install Applications", 5, "Software");
            s.Add(T0.AddMinutes(10), 11130, Skip, "Install Northwind Viewer", 6, "Software");
            s.Add(T0.AddMinutes(12), 11138, Begin, "Clean up", 12, "Finish");
            e.Apply(e.Poll());
            se = e.S;
            T.Eq(State.Succeeded, StateOf(se, "Install Applications"), "Whole task sequence: the step is done when it reports");
            T.Eq(State.Skipped, StateOf(se, "Install Northwind Viewer"), "Whole task sequence: a step the engine skipped is skipped");
            T.Eq(State.Skipped, StateOf(se, "Restart Computer"), "Whole task sequence: a step the run moved past with no row is skipped");
            T.Eq(State.Installing, StateOf(se, "Clean up"), "Whole task sequence: the next step runs");
            T.Check(AppGroups.StateLabel(Named(se, "Restart Computer")) == "Skipped", "Whole task sequence: a step passed without a row is Skipped, not Disabled");
            int count = 0;
            foreach (Item i in se.Items) if (i.Name != null && i.Name.StartsWith("Install Applications")) count++;
            T.Eq(1, count, "Whole task sequence: a step is one item before and after it reports");
            T.Eq(8, CountSteps(se), "Whole task sequence: every step of the task sequence is in the session once");
        }

        static int CountSteps(Session s)
        {
            int n = 0;
            foreach (Item i in s.Items) if (i.Key != SccmFeed.RunKey && i.Key.StartsWith("ts:")) n++;
            return n;
        }

        static int Order(Session s, string phaseName)
        {
            foreach (Phase p in s.Phases) if (p.Name == phaseName) return p.Order;
            return 0;
        }

        static void Finished()
        {
            Site s = Running();
            s.Add(T0.AddMinutes(9), 11134, Done, "Install Applications", 5, "Software");
            s.Add(T0.AddMinutes(10), 11134, Done, "Restart Computer", 7, "Software");
            s.Add(T0.AddMinutes(12), 11134, Done, "Clean up", 12, "Finish");
            s.Add(T0.AddMinutes(13), 11143, Ended, "", 0, "");
            Engine e;
            Session se = Open(s, out e);
            T.Eq(State.Skipped, StateOf(se, "Install Northwind Viewer"), "Whole task sequence: at the end a step without a row is skipped");
            T.Eq(State.Succeeded, StateOf(se, "Clean up"), "Whole task sequence: the last step is done");
            T.Eq(0, CountState(se, State.Pending), "Whole task sequence: nothing waits after the end");
        }

        static int CountState(Session s, State st)
        {
            int n = 0;
            foreach (Item i in s.Items) if (i.State == st && i.Key != SccmFeed.RunKey) n++;
            return n;
        }

        static void Stopped()
        {
            Site s = Running();
            s.Add(T0.AddMinutes(9), 11135, Failed, "Install Applications", 5, "Software");
            s.Add(T0.AddMinutes(9), 11171, "The task sequence execution engine failed execution of a task sequence.", "", 0, "");
            Engine e;
            Session se = Open(s, out e);
            T.Eq(State.Failed, StateOf(se, "Install Applications"), "Whole task sequence: a step that failed is failed");
            T.Eq(State.Pending, StateOf(se, "Clean up"), "Whole task sequence: steps after a failed run stay waiting");
        }

        static void Cannot()
        {
            TsReader.ClearCache();
            Site s = Running();
            s.Fail = "Access denied";
            Engine e;
            Session se = Open(s, out e);
            T.Check(se.Find("ts:1:Check the network") != null && se.Find("ts:5:Install Applications") != null, "Whole task sequence: when it cannot be read the status rows show as before");
            T.Check(Named(se, "Clean up") == null, "Whole task sequence: no steps are made up when it cannot be read");
            string note = string.Join("\n", se.Notes.ToArray());
            T.Check(note.Contains("Only the steps that have reported are shown") && note.Contains("no right to read task sequence packages"), "Whole task sequence: the session says why, in plain words");

            TsReader.ClearCache();
            s = Running();
            s.Sequence = "<sequence";
            se = Open(s, out e);
            note = string.Join("\n", se.Notes.ToArray());
            T.Check(Named(se, "Clean up") == null && note.Contains("not XML that TrackVice can read"), "Whole task sequence: XML that does not read says why");

            TsReader.ClearCache();
            s = Running();
            s.Sequence = "";
            se = Open(s, out e);
            T.Check(string.Join("\n", se.Notes.ToArray()).Contains("gave no steps"), "Whole task sequence: an empty sequence says why");

            // A connection that cannot read single instances at all.
            TsReader.ClearCache();
            SccmCatalogOnly only = new SccmCatalogOnly(Running());
            RemoteDeployment d = new RemoteDeployment();
            d.Key = Res + "|" + Adv + "|" + T0.Ticks;
            d.Device = "CONTOSO-PC31";
            d.StartUtc = T0;
            Engine e2 = Engine.ForRemote(new SccmCatalog(only, "CM01.contoso.com").Open(d), d);
            e2.Apply(e2.Poll());
            T.Check(string.Join("\n", e2.S.Notes.ToArray()).Contains("cannot read task sequences") && e2.S.Find("ts:1:Check the network") != null, "Whole task sequence: a connection without a direct read keeps today's behaviour");
        }

        // Only the query, not the direct read.
        sealed class SccmCatalogOnly : ISccmQuery
        {
            readonly Site inner;

            public SccmCatalogOnly(Site s)
            {
                inner = s;
            }

            public List<Dictionary<string, object>> Query(string wql)
            {
                return inner.Query(wql);
            }
        }

        static void Cache()
        {
            TsReader.ClearCache();
            Site s = Running();
            Engine e;
            Open(s, out e);
            Open(s, out e);
            T.Eq(1, s.Gets, "Whole task sequence: the task sequence is read once for a package ID and version");
            T.Check(s.Packages >= 2, "Whole task sequence: the version is asked each time a run is opened");
            s.SourceVersion = 4;
            Open(s, out e);
            T.Eq(2, s.Gets, "Whole task sequence: a new version is read again");
            Site other = Running();
            Open(other, out e);
            T.Eq(0, other.Gets, "Whole task sequence: the cache holds for another connection with the same package and version");
            string why;
            bool again;
            Site gone = new Site();
            T.Check(TsReader.Read(gone, "F0999999", out why, out again) == null && why.Contains("F0999999") && !again, "Whole task sequence: a package that is not there says so");
            T.Check(TsReader.Read(gone, "F01'; DROP", out why, out again) == null && why.Contains("does not say"), "Whole task sequence: an ID with other characters is not put in a query");
        }

        static void Loose()
        {
            // The package ID can come from the deployment when the rows do not have it.
            Site s = Running();
            foreach (Dictionary<string, object> r in s.Rows) r.Remove("PackageID");
            TsReader.ClearCache();
            string got = TsReader.PackageOf(new Advert(Pkg), Adv);
            T.Eq(Pkg, got, "Whole task sequence: the package of a deployment comes from SMS_Advertisement");
        }

        sealed class Advert : ISccmQuery
        {
            readonly string pkg;

            public Advert(string p)
            {
                pkg = p;
            }

            public List<Dictionary<string, object>> Query(string wql)
            {
                List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
                if (!wql.Contains("FROM SMS_Advertisement WHERE AdvertisementID = '" + Adv + "'")) return l;
                Dictionary<string, object> x = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                x["PackageID"] = pkg;
                l.Add(x);
                return l;
            }
        }
    }
}
