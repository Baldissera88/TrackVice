namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Xml;

    // A site in memory, for the demo and for tests: it keeps packages, groups and task sequence XML, and writes what it is asked to.
    public sealed class MemorySmsSite : ISmsSite
    {
        public string Title = "site P01 on cm01.contoso.com";
        public readonly List<SmsPackage> Packages = new List<SmsPackage>();
        public readonly List<SmsDpGroup> Groups = new List<SmsDpGroup>();
        public readonly List<SmsTaskSequence> Sequences = new List<SmsTaskSequence>();
        public readonly Dictionary<string, string> Xml = new Dictionary<string, string>();
        public readonly Dictionary<string, string> BootImageOf = new Dictionary<string, string>();
        public readonly Dictionary<string, SmsBootImage> BootImages = new Dictionary<string, SmsBootImage>();
        // Every call that changes the site, as a short line.
        public readonly List<string> Writes = new List<string>();
        public readonly Dictionary<string, List<string>> InGroups = new Dictionary<string, List<string>>();
        public readonly Dictionary<string, int> Refreshed = new Dictionary<string, int>();
        // When set, every change fails with this text.
        public string FailWith;
        int next = 100;

        public string Name
        {
            get { return Title; }
        }

        public List<SmsPackage> FindPackages(string name)
        {
            return Packages.FindAll(delegate (SmsPackage p) { return p.Name == name; });
        }

        public List<SmsDpGroup> DistributionPointGroups()
        {
            return new List<SmsDpGroup>(Groups);
        }

        public List<SmsTaskSequence> TaskSequences()
        {
            return new List<SmsTaskSequence>(Sequences);
        }

        public string SequenceXml(string id)
        {
            string x;
            if (!Xml.TryGetValue(id, out x)) throw new InvalidOperationException("The task sequence package " + id + " is not on the site.");
            return x;
        }

        public string BootImageId(string id)
        {
            string b;
            return BootImageOf.TryGetValue(id, out b) ? b : null;
        }

        public SmsBootImage BootImage(string id)
        {
            SmsBootImage b;
            return BootImages.TryGetValue(id, out b) ? b : null;
        }

        void Guard()
        {
            if (FailWith != null) throw new InvalidOperationException(FailWith);
        }

        public string CreatePackage(string name, string description, string sourcePath)
        {
            Guard();
            SmsPackage p = new SmsPackage();
            p.PackageId = "P01" + (next++).ToString("00000", CultureInfo.InvariantCulture);
            p.Name = name;
            p.SourcePath = sourcePath;
            Packages.Add(p);
            Writes.Add("create package " + name + " " + sourcePath + " " + description);
            return p.PackageId;
        }

        public void AddToGroups(string packageId, List<string> groupIds)
        {
            Guard();
            foreach (string g in groupIds)
            {
                List<string> l;
                if (!InGroups.TryGetValue(g, out l)) { l = new List<string>(); InGroups[g] = l; }
                if (!l.Contains(packageId)) l.Add(packageId);
                Writes.Add("add package " + packageId + " to group " + g);
            }
        }

        public void RefreshPackage(string packageId)
        {
            Guard();
            int n;
            Refreshed.TryGetValue(packageId, out n);
            Refreshed[packageId] = n + 1;
            Writes.Add("refresh package " + packageId);
        }

        public void EditSequence(string id, List<TsEdit> edits)
        {
            Guard();
            XmlDocument d = new XmlDocument();
            d.XmlResolver = null;
            d.LoadXml(SequenceXml(id));
            foreach (TsEdit e in edits)
            {
                XmlElement owner = d.DocumentElement;
                int[] path = e.Insert ? e.Path : Parent(e.Path);
                foreach (int i in path) owner = Child(owner, i);
                if (e.Insert)
                {
                    XmlElement step = d.CreateElement("step");
                    Fill(step, e.Spec);
                    List<XmlElement> kids = Kids(owner);
                    if (e.Index >= kids.Count) owner.AppendChild(step);
                    else owner.InsertBefore(step, kids[e.Index]);
                    Writes.Add("insert step " + e.Spec.Name + " at " + string.Join(".", Strings(path)) + "/" + e.Index);
                }
                else
                {
                    XmlElement step = Child(owner, e.Path[e.Path.Length - 1]);
                    Fill(step, e.Spec);
                    Writes.Add("update step " + e.Spec.Name);
                }
            }
            Xml[id] = d.OuterXml;
        }

        static int[] Parent(int[] path)
        {
            int[] p = new int[path.Length - 1];
            Array.Copy(path, p, p.Length);
            return p;
        }

        static string[] Strings(int[] path)
        {
            string[] s = new string[path.Length];
            for (int i = 0; i < s.Length; i++) s[i] = path[i].ToString(CultureInfo.InvariantCulture);
            return s;
        }

        static List<XmlElement> Kids(XmlElement owner)
        {
            List<XmlElement> l = new List<XmlElement>();
            foreach (XmlNode n in owner.ChildNodes)
            {
                XmlElement e = n as XmlElement;
                if (e != null && (e.Name == "group" || e.Name == "step")) l.Add(e);
            }
            return l;
        }

        static XmlElement Child(XmlElement owner, int i)
        {
            return Kids(owner)[i];
        }

        // The attributes and the action text the console writes for a Run Command Line step.
        static void Fill(XmlElement step, TsStepSpec spec)
        {
            step.SetAttribute("type", "SMS_TaskSequence_RunCommandLineAction");
            step.SetAttribute("name", spec.Name);
            step.SetAttribute("description", spec.Description);
            step.SetAttribute("runIn", spec.Environment);
            step.SetAttribute("disable", "false");
            step.SetAttribute("continueOnError", spec.ContinueOnError ? "true" : "false");
            step.InnerXml = "<action>" + System.Security.SecurityElement.Escape("smsswd.exe /run:" + spec.PackageId + " " + spec.CommandLine) + "</action>";
        }

        public void RestoreSequence(string id, string xml)
        {
            Guard();
            Xml[id] = xml;
            Writes.Add("restore sequence " + id);
        }

        // A site for the demo: a package that holds an older TrackVice, groups, and two task sequences.
        public static MemorySmsSite Demo(bool packageMade, string packageSource)
        {
            MemorySmsSite s = new MemorySmsSite();
            if (packageMade)
            {
                SmsPackage p = new SmsPackage();
                p.PackageId = "P0100412";
                p.Name = TsAgentPackage.Name;
                p.SourcePath = packageSource;
                s.Packages.Add(p);
            }
            string[][] groups = { new string[] { "{8A1F0C52-6D1B-4E3C-9B7A-2C41D5E60F11}", "All distribution points", "6" }, new string[] { "{0D93B7E4-52A8-4C0F-8E16-7B3F2A9C14D8}", "Copenhagen", "2" }, new string[] { "{5C27E8A1-93B4-4F6D-A0E2-1D8B6C47F935}", "Aarhus", "1" } };
            foreach (string[] g in groups)
            {
                SmsDpGroup x = new SmsDpGroup();
                x.Id = g[0];
                x.Name = g[1];
                x.Members = int.Parse(g[2], CultureInfo.InvariantCulture);
                s.Groups.Add(x);
            }
            AddSequence(s, "P0100231", "Windows 11 24H2 Standard", DemoSccm.SequenceXml, "P0100077");
            AddSequence(s, "P0100245", "Windows 11 24H2 Kiosk", DemoSccm.SequenceXml.Replace("Install Teams classic", "Install Kiosk shell"), "P0100078");
            SmsBootImage ok = new SmsBootImage();
            ok.PackageId = "P0100077";
            ok.Name = "x64 10.0.26100";
            ok.Components.AddRange(new long[] { 11, 14, 26 });
            s.BootImages["P0100077"] = ok;
            SmsBootImage bare = new SmsBootImage();
            bare.PackageId = "P0100078";
            bare.Name = "Kiosk x64 10.0.26100";
            bare.Components.AddRange(new long[] { 11, 26 });
            s.BootImages["P0100078"] = bare;
            return s;
        }

        static void AddSequence(MemorySmsSite s, string id, string name, string xml, string boot)
        {
            SmsTaskSequence t = new SmsTaskSequence();
            t.PackageId = id;
            t.Name = name;
            s.Sequences.Add(t);
            s.Xml[id] = xml;
            s.BootImageOf[id] = boot;
        }
    }

    // Files for the demo and tests: the versions of TrackVice.exe by folder, and the files in each folder.
    public sealed class MemoryFiles : ITsFiles
    {
        public string Exe = "C:\\Tools\\TrackVice\\TrackVice.exe";
        // Folder name to the files in it. The version of its TrackVice.exe is in Versions.
        public readonly Dictionary<string, List<string>> Folders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Copied = new List<string>();
        public string RunningVersion = "1.5.1";
        public string FailCopy;

        public string RunningExe
        {
            get { return Exe; }
        }

        public bool FolderExists(string folder)
        {
            return Folders.ContainsKey(folder);
        }

        public List<string> FilesIn(string folder)
        {
            List<string> l;
            return Folders.TryGetValue(folder, out l) ? new List<string>(l) : new List<string>();
        }

        public void CopyExe(string exe, string folder)
        {
            if (FailCopy != null) throw new UnauthorizedAccessException(FailCopy);
            List<string> l;
            if (!Folders.TryGetValue(folder, out l)) throw new DirectoryNotFoundException(folder);
            if (!l.Contains(FileSys.ExeName)) l.Add(FileSys.ExeName);
            Versions[FileSys.ExeIn(folder)] = RunningVersion;
            Copied.Add(folder);
        }

        public string VersionOf(string exe)
        {
            string v;
            return Versions.TryGetValue(exe, out v) ? v : null;
        }
    }

    // Copies of task sequences in memory.
    public sealed class MemoryCopies : ITsCopies
    {
        public readonly List<TsCopy> Saved = new List<TsCopy>();
        public readonly List<string> Tags = new List<string>();
        public string FailSave;

        public string Folder
        {
            get { return "C:\\Users\\admin\\AppData\\Local\\TrackVice\\task-sequence-copies"; }
        }

        public string Save(string id, string name, string xml, string tag, DateTime when)
        {
            if (FailSave != null) throw new IOException(FailSave);
            TsCopy c = new TsCopy();
            c.Path = id + "-" + Saved.Count + (tag.Length > 0 ? "-" + tag : "") + ".xml";
            c.Xml = xml;
            c.Time = when;
            Saved.Add(c);
            Tags.Add(tag);
            return c.Path;
        }

        public TsCopy Latest(string id)
        {
            for (int i = Saved.Count - 1; i >= 0; i--) if (Tags[i].Length == 0 && Saved[i].Path.StartsWith(id + "-", StringComparison.Ordinal)) return Saved[i];
            return null;
        }
    }
}
