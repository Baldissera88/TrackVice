namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Xml;

    // Reads one instance of an SMS Provider class by its key. A query leaves out the lazy properties of the
    // class (Sequence of SMS_TaskSequencePackage, for one), so only a direct Get has their value.
    public interface ISccmGet
    {
        // The properties of the instance, or null when there is none with that key.
        Dictionary<string, object> GetInstance(string className, string keyName, string keyValue);
    }

    // A group or a step of a task sequence, read from the Sequence XML of its package.
    public sealed class TsNode
    {
        public bool IsGroup, Disabled, ContinueOnError, HasCondition;
        public string Name = "", Type = "", Action = "";
        public TsNode Parent;
        public readonly List<TsNode> Children = new List<TsNode>();
        public readonly Dictionary<string, string> Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The place among all groups and steps in task sequence order, from 1.
        public int Index;

        // The base name of the variable list a step installs from, such as APP for APP01, APP02.
        public string BaseVariable
        {
            get
            {
                string v;
                return Vars.TryGetValue("BaseVariableName", out v) && v.Length > 0 ? v : null;
            }
        }

        // Disabled itself or in a disabled group.
        public bool IsOff
        {
            get
            {
                for (TsNode n = this; n != null; n = n.Parent) if (n.Disabled) return true;
                return false;
            }
        }

        public string GroupName
        {
            get { return Parent != null ? Parent.Name : null; }
        }

        // The place of the node among its brothers, at the top among the roots.
        public int RootIndex;

        // Where the node is: its position in its parent at each level, from the top.
        public int[] Path()
        {
            List<int> l = new List<int>();
            for (TsNode n = this; n != null; n = n.Parent)
                l.Insert(0, n.Parent != null ? n.Parent.Children.IndexOf(n) : n.RootIndex);
            return l.ToArray();
        }
    }

    // The groups and steps of a task sequence in the order the engine runs them.
    public sealed class TsSequence
    {
        public readonly List<TsNode> Roots = new List<TsNode>();
        // Every group and step, in order.
        public readonly List<TsNode> All = new List<TsNode>();

        // The XML of SMS_TaskSequencePackage.Sequence: a sequence element with group and step elements in it.
        public static TsSequence Parse(string xml)
        {
            if (string.IsNullOrEmpty(xml) || xml.Trim().Length == 0) throw new FormatException("The task sequence is empty.");
            XmlReaderSettings rs = new XmlReaderSettings();
            rs.DtdProcessing = DtdProcessing.Prohibit;
            rs.XmlResolver = null;
            XmlDocument d = new XmlDocument();
            try
            {
                using (XmlReader r = XmlReader.Create(new StringReader(xml), rs)) d.Load(r);
            }
            catch (XmlException ex) { throw new FormatException("The task sequence is not XML that TrackVice can read: " + ex.Message); }
            TsSequence s = new TsSequence();
            if (d.DocumentElement == null) throw new FormatException("The task sequence is empty.");
            s.Fill(d.DocumentElement, null, s.Roots);
            if (s.All.Count == 0) throw new FormatException("The task sequence has no groups or steps.");
            return s;
        }

        void Fill(XmlElement parentEl, TsNode parent, List<TsNode> into)
        {
            foreach (XmlNode c in parentEl.ChildNodes)
            {
                XmlElement e = c as XmlElement;
                if (e == null) continue;
                string n = e.LocalName.ToLowerInvariant();
                if (n != "group" && n != "step") continue;
                TsNode node = new TsNode();
                node.IsGroup = n == "group";
                node.Name = Att(e, "name");
                node.Type = Att(e, "type");
                node.Disabled = IsTrue(Att(e, "disable"));
                node.ContinueOnError = IsTrue(Att(e, "continueOnError"));
                node.Parent = parent;
                node.RootIndex = into.Count;
                node.Index = All.Count + 1;
                foreach (XmlNode k in e.ChildNodes)
                {
                    XmlElement ke = k as XmlElement;
                    if (ke == null) continue;
                    string kn = ke.LocalName.ToLowerInvariant();
                    if (kn == "condition") node.HasCondition = ke.HasChildNodes;
                    else if (kn == "action") node.Action = ke.InnerText.Trim();
                    else if (kn == "defaultvarlist")
                    {
                        foreach (XmlNode v in ke.ChildNodes)
                        {
                            XmlElement ve = v as XmlElement;
                            if (ve == null || !string.Equals(ve.LocalName, "variable", StringComparison.OrdinalIgnoreCase)) continue;
                            string vn = Att(ve, "name");
                            if (vn.Length > 0) node.Vars[vn] = ve.InnerText.Trim();
                        }
                    }
                }
                All.Add(node);
                into.Add(node);
                if (node.IsGroup) Fill(e, node, node.Children);
            }
        }

        static string Att(XmlElement e, string name)
        {
            foreach (XmlAttribute a in e.Attributes) if (string.Equals(a.LocalName, name, StringComparison.OrdinalIgnoreCase)) return a.Value ?? "";
            return "";
        }

        static bool IsTrue(string v)
        {
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    // The task sequence of a deployment, read from the site once per package ID and version.
    public static class TsReader
    {
        static readonly Dictionary<string, TsSequence> cache = new Dictionary<string, TsSequence>(StringComparer.OrdinalIgnoreCase);

        // IDs of the site are letters and digits. Anything else is not put in a query.
        public static bool SafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '{' || c == '}')) return false;
            return true;
        }

        // The package a deployment runs: SMS_Advertisement.PackageID for the deployment ID.
        public static string PackageOf(ISccmQuery q, string deployment)
        {
            if (!SafeId(deployment)) return null;
            try
            {
                foreach (Dictionary<string, object> r in q.Query("SELECT AdvertisementID, PackageID FROM SMS_Advertisement WHERE AdvertisementID = '" + deployment + "'"))
                    return SccmCatalog.Str(r, "PackageID");
            }
            catch (Exception) { }
            return null;
        }

        // The task sequence of the package, or null and a plain reason. again says that trying later may work.
        public static TsSequence Read(ISccmQuery q, string packageId, out string why, out bool again)
        {
            why = null;
            again = false;
            ISccmGet g = q as ISccmGet;
            if (g == null) { why = "This connection to the site cannot read task sequences."; return null; }
            if (!SafeId(packageId)) { why = "The deployment does not say which task sequence it runs."; return null; }
            try
            {
                List<Dictionary<string, object>> rows = q.Query("SELECT PackageID, Name, Version, SourceVersion FROM SMS_TaskSequencePackage WHERE PackageID = '" + packageId + "'");
                if (rows.Count == 0)
                {
                    why = "The task sequence package " + packageId + " is not on the site, or the account that runs TrackVice cannot see it.";
                    return null;
                }
                string key = packageId + "|" + SccmCatalog.Int(rows[0], "SourceVersion") + "|" + SccmCatalog.Str(rows[0], "Version");
                lock (cache)
                {
                    TsSequence hit;
                    if (cache.TryGetValue(key, out hit)) return hit;
                }
                Dictionary<string, object> inst = g.GetInstance("SMS_TaskSequencePackage", "PackageID", packageId);
                string xml = inst == null ? null : SccmCatalog.Str(inst, "Sequence");
                if (string.IsNullOrEmpty(xml))
                {
                    why = "The site gave no steps for the task sequence package " + packageId + ".";
                    return null;
                }
                TsSequence s = TsSequence.Parse(xml);
                lock (cache) cache[key] = s;
                return s;
            }
            catch (FormatException ex)
            {
                why = ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                string m = ex.Message.Trim();
                again = true;
                if (m.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("0x80041003", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    again = false;
                    why = "The account that runs TrackVice has no right to read task sequence packages.";
                }
                else why = "The site did not give the task sequence: " + m;
                return null;
            }
        }

        public static void ClearCache()
        {
            lock (cache) cache.Clear();
        }
    }

    // The task sequence merged with the status rows of a run: every group and step from the start.
    public sealed class TsPlan
    {
        public const string DisabledKey = "Disabled", DisabledText = "In the task sequence", ListKey = "Variable list";
        public readonly TsSequence Seq;
        readonly List<TsNode> steps = new List<TsNode>();
        readonly Dictionary<string, TsNode> claim = new Dictionary<string, TsNode>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<TsNode> claimed = new HashSet<TsNode>();
        readonly HashSet<string> aliased = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<TsNode, string> sent = new Dictionary<TsNode, string>();
        DateTime baseUtc;
        bool started;

        public TsPlan(TsSequence seq)
        {
            Seq = seq;
            foreach (TsNode n in seq.All) if (!n.IsGroup) steps.Add(n);
        }

        public static string PlanKey(TsNode n)
        {
            return "ts:plan:" + n.Index.ToString(CultureInfo.InvariantCulture);
        }

        // The name shown for a step: a step that installs from a variable list says which.
        public static string Display(TsNode n)
        {
            return n.BaseVariable != null ? n.Name + " (variable list " + n.BaseVariable + ")" : n.Name;
        }

        // "Disabled" for an item the task sequence has switched off, else null.
        public static string DisabledLabel(Item i)
        {
            return i != null && i.State == State.Skipped && i.Get(DisabledKey) != null ? "Disabled" : null;
        }

        public int Claimed
        {
            get { return claimed.Count; }
        }

        public int StepCount
        {
            get { return steps.Count; }
        }

        class Reported
        {
            public string Key, Action, Group;
            public long Step;
        }

        // Matches the steps that reported with the steps of the task sequence, and adds an observation for each step that is
        // new or changed. Observations of reported steps move to the item of their step in the task sequence.
        public void Apply(List<Dictionary<string, object>> rows, DateTime? runUtc, Batch b)
        {
            List<Reported> reported = new List<Reported>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool ended = false;
            DateTime? first = null;
            foreach (Dictionary<string, object> r in rows)
            {
                DateTime? t = SccmCatalog.Time(r);
                if (t.HasValue && (!first.HasValue || t.Value < first.Value)) first = t;
                int k = TsMessage.Of(r).Kind;
                if (k == TsMessage.TsDone) ended = true;
                string action = SccmCatalog.Str(r, "ActionName");
                if (string.IsNullOrEmpty(action) || SccmLoose.Is(r) || !SccmLoose.IsStepKind(k)) continue;
                long step = SccmCatalog.Int(r, "Step");
                string key = "ts:" + step.ToString(CultureInfo.InvariantCulture) + ":" + action;
                if (!seen.Add(key)) continue;
                Reported x = new Reported();
                x.Key = key;
                x.Action = action;
                x.Group = SccmCatalog.Str(r, "GroupName");
                x.Step = step;
                reported.Add(x);
            }
            if (!started)
            {
                DateTime t0 = first ?? DateTime.UtcNow;
                if (runUtc.HasValue && runUtc.Value < t0) t0 = runUtc.Value;
                baseUtc = t0.AddSeconds(-1);
            }
            reported.Sort(delegate (Reported a, Reported c) { return a.Step.CompareTo(c.Step); });
            // Two passes: the step with the same name in the same group first, then any step with the name.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Reported x in reported)
                {
                    if (claim.ContainsKey(x.Key)) continue;
                    foreach (TsNode n in steps)
                    {
                        if (claimed.Contains(n) || !string.Equals(n.Name, x.Action, StringComparison.OrdinalIgnoreCase)) continue;
                        if (pass == 0 && !string.Equals(n.GroupName ?? "", x.Group ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                        claim[x.Key] = n;
                        claimed.Add(n);
                        break;
                    }
                }
            }
            // The item of a reported step keeps the key its status rows give it. It takes the place and the name of the step
            // in the task sequence, and the item made for the step before it reported goes into it.
            foreach (Obs o in b.Obs)
            {
                TsNode n;
                if (o.ItemKey != null && claim.TryGetValue(o.ItemKey, out n)) o.ItemName = Display(n);
            }
            foreach (KeyValuePair<string, TsNode> c in claim)
                if (aliased.Add(c.Key)) b.Aliases.Add(new Pair(PlanKey(c.Value), c.Key));
            int last = 0;
            foreach (TsNode n in claimed) if (n.Index > last) last = n.Index;
            List<Obs> add = new List<Obs>();
            foreach (TsNode n in steps)
            {
                bool done = claimed.Contains(n);
                State st = done ? State.Pending : n.IsOff ? State.Skipped : ended || n.Index < last ? State.Skipped : State.Pending;
                string sig = st.ToString() + (n.IsOff ? "|off" : "");
                string was;
                if (sent.TryGetValue(n, out was) && (done || was == sig)) continue;
                sent[n] = sig;
                add.Add(Make(n, st));
            }
            started = true;
            b.Obs.InsertRange(0, add);
        }

        Obs Make(TsNode n, State st)
        {
            Obs o = new Obs();
            o.TimeUtc = baseUtc;
            o.Source = "Task sequence";
            o.ItemKey = PlanKey(n);
            o.ItemName = Display(n);
            o.ItemKind = Kinds.Step;
            o.Origin = Origins.Sccm;
            o.State = st;
            o.Quiet = true;
            string group = n.GroupName;
            if (!string.IsNullOrEmpty(group))
            {
                o.PhaseKey = "ts:group:" + group.ToLowerInvariant();
                o.PhaseName = group;
                o.PhaseOrder = 100 + Math.Min(10000, n.Index);
                o.AddInfo("Group", group);
            }
            else o.PhaseKey = Phases.TaskSequence;
            o.AddInfo("Place in the task sequence", n.Index.ToString(CultureInfo.InvariantCulture));
            if (n.IsOff) o.AddInfo(DisabledKey, DisabledText);
            if (n.BaseVariable != null) o.AddInfo(ListKey, n.BaseVariable + "01, " + n.BaseVariable + "02 and so on");
            return o;
        }
    }

    // Loads the task sequence of a run when the run is opened, and merges it with the status rows after each read.
    public sealed class TsPlanFeed
    {
        public const string BaseNote = "This task sequence is read from the status messages it sends to the site. They arrive a little after each step. Run TrackVice on the device to see smsts.log line by line.";
        TsPlan plan;
        string why;
        int tries;
        bool noted;

        public bool Ready
        {
            get { return plan != null; }
        }

        public string Why
        {
            get { return why; }
        }

        // Call after the rows of a read were turned into observations.
        public void Update(ISccmQuery q, string deployment, List<Dictionary<string, object>> rows, DateTime? runUtc, Batch b)
        {
            if (plan == null && tries < 3 && rows.Count > 0)
            {
                tries++;
                string pkg = null;
                foreach (Dictionary<string, object> r in rows)
                {
                    pkg = SccmCatalog.Str(r, "PackageID");
                    if (!string.IsNullOrEmpty(pkg)) break;
                }
                if (string.IsNullOrEmpty(pkg)) pkg = TsReader.PackageOf(q, deployment);
                bool again;
                TsSequence s = TsReader.Read(q, pkg, out why, out again);
                if (s != null) { plan = new TsPlan(s); why = null; }
                else if (!again) tries = 3;
            }
            if (plan != null)
            {
                plan.Apply(rows, runUtc, b);
                if (!noted)
                {
                    noted = true;
                    b.Notes.Add("The steps come from the task sequence on the site. A step that has not reported shows as Waiting. A step the run went past without a status message shows as Skipped, and a step switched off in the task sequence as Disabled.");
                }
            }
            else if (tries >= 3 && !noted && why != null)
            {
                noted = true;
                b.Notes.Add("Only the steps that have reported are shown. The task sequence itself was not read. " + why);
            }
        }
    }
}
