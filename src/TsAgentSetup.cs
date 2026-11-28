namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // Putting the agent in a task sequence through the SMS Provider: a package with TrackVice.exe, and two Run Command Line steps.
    // The site is behind ISmsSite, the files behind ITsFiles and ITsCopies, so tests can fake them.

    public sealed class SmsPackage
    {
        public string PackageId, Name, SourcePath, Version;
    }

    public sealed class SmsTaskSequence
    {
        public string PackageId, Name;
    }

    public sealed class SmsDpGroup
    {
        public string Id, Name;
        public int Members;
    }

    public sealed class SmsBootImage
    {
        public string PackageId, Name;
        // The identifiers of SMS_BootImagePackage.OptionalComponents.
        public List<long> Components = new List<long>();
    }

    // A Run Command Line step TrackVice makes or sets again.
    public sealed class TsStepSpec
    {
        public string Name, Description, CommandLine, PackageId, Environment;
        public bool ContinueOnError = true;
    }

    // One change to a task sequence. An update changes the step at Path. An insert puts a step in the list of the group at
    // Path (an empty path is the top), at Index.
    public sealed class TsEdit
    {
        public bool Insert;
        public int[] Path = new int[0];
        public int Index;
        public TsStepSpec Spec;
    }

    public interface ISmsSite
    {
        string Name { get; }
        List<SmsPackage> FindPackages(string name);
        List<SmsDpGroup> DistributionPointGroups();
        List<SmsTaskSequence> TaskSequences();
        // The Sequence XML of a task sequence package.
        string SequenceXml(string taskSequenceId);
        // The ID of the boot image the task sequence refers to, or null.
        string BootImageId(string taskSequenceId);
        SmsBootImage BootImage(string bootImageId);
        // Makes a package with a source folder and no program. Returns its package ID.
        string CreatePackage(string name, string description, string sourcePath);
        void AddToGroups(string packageId, List<string> groupIds);
        // Copies the latest source files to every distribution point of the package.
        void RefreshPackage(string packageId);
        void EditSequence(string taskSequenceId, List<TsEdit> edits);
        void RestoreSequence(string taskSequenceId, string xml);
    }

    public interface ITsFiles
    {
        // TrackVice.exe that is running.
        string RunningExe { get; }
        bool FolderExists(string folder);
        // The names of the files in a folder.
        List<string> FilesIn(string folder);
        void CopyExe(string exe, string folder);
        // The version a TrackVice.exe says it is, or null.
        string VersionOf(string exe);
    }

    public sealed class TsCopy
    {
        public string Path, Xml;
        public DateTime Time;
    }

    public interface ITsCopies
    {
        // Keeps the XML and returns where. tag is "" for the copy made before a change.
        string Save(string taskSequenceId, string name, string xml, string tag, DateTime when);
        // The newest copy made before a change, or null.
        TsCopy Latest(string taskSequenceId);
        string Folder { get; }
    }

    public sealed class FileSys : ITsFiles
    {
        public const string ExeName = "TrackVice.exe";
        readonly string exe;

        // TrackVice.exe in a folder of the site's side, always written with backslashes.
        public static string ExeIn(string folder)
        {
            return folder.TrimEnd('\\', '/') + "\\" + ExeName;
        }

        public FileSys(string runningExe)
        {
            exe = runningExe;
        }

        public string RunningExe
        {
            get { return exe; }
        }

        public bool FolderExists(string folder)
        {
            try { return Directory.Exists(folder); }
            catch (Exception) { return false; }
        }

        public List<string> FilesIn(string folder)
        {
            List<string> l = new List<string>();
            foreach (string f in Directory.GetFileSystemEntries(folder)) l.Add(Path.GetFileName(f));
            return l;
        }

        public void CopyExe(string from, string folder)
        {
            File.Copy(from, Path.Combine(folder, ExeName), true);
        }

        public string VersionOf(string path)
        {
            try { return File.Exists(path) ? SemVer.OfFile(path) : null; }
            catch (Exception) { return null; }
        }
    }

    // The copies of task sequences, as files on this PC.
    public sealed class FolderCopies : ITsCopies
    {
        readonly string folder;
        public const string UndoTag = "before-undo";

        public FolderCopies(string path)
        {
            folder = path;
        }

        public string Folder
        {
            get { return folder; }
        }

        public static string DefaultFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackVice", "task-sequence-copies");
        }

        public string Save(string id, string name, string xml, string tag, DateTime when)
        {
            if (!TsReader.SafeId(id)) throw new ArgumentException("The ID is not one of the site.");
            Directory.CreateDirectory(folder);
            string file = id + "-" + when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(tag) ? "" : "-" + tag) + ".xml";
            string path = Path.Combine(folder, file);
            File.WriteAllText(path, xml ?? "", new UTF8Encoding(false));
            return path;
        }

        public TsCopy Latest(string id)
        {
            if (!TsReader.SafeId(id) || !Directory.Exists(folder)) return null;
            string best = null;
            foreach (string f in Directory.GetFiles(folder, id + "-*.xml"))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (n.EndsWith("-" + UndoTag, StringComparison.Ordinal)) continue;
                if (best == null || string.CompareOrdinal(n, Path.GetFileNameWithoutExtension(best)) > 0) best = f;
            }
            if (best == null) return null;
            TsCopy c = new TsCopy();
            c.Path = best;
            c.Xml = File.ReadAllText(best, Encoding.UTF8);
            string stamp = Path.GetFileNameWithoutExtension(best).Substring(id.Length + 1);
            DateTime t;
            c.Time = DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : File.GetLastWriteTime(best);
            return c;
        }
    }

    // What the page shows when it has read the site.
    public sealed class TsAgentState
    {
        public SmsPackage Package;
        // The version of TrackVice.exe in the source folder of the package, or null.
        public string PackageVersion;
        public string RunningVersion;
        public List<SmsDpGroup> Groups = new List<SmsDpGroup>();
        public List<SmsTaskSequence> Sequences = new List<SmsTaskSequence>();

        public bool Older
        {
            get { return TsAgentPackage.IsOlder(PackageVersion, RunningVersion); }
        }
    }

    public sealed class TsResult
    {
        public bool Done;
        public string Text, PackageId, CopyPath;

        public TsResult(bool done, string text)
        {
            Done = done;
            Text = text;
        }
    }

    public static class TsAgentPackage
    {
        public const string Name = "TrackVice agent";
        public const string StepWinPE = "TrackVice agent (WinPE)", StepWindows = "TrackVice agent (Windows)";
        public const string Description = "TrackVice.exe for the agent in task sequences. It has no program. Made by TrackVice.";
        public const long NetFx = 14;

        public static string CommandLine(string code)
        {
            return FileSys.ExeName + " /agent-start /customer \"" + code + "\"";
        }

        // The command line runs as SYSTEM on every device of the task sequence, so only the characters of a customer code
        // go into it: a quote or a space would end the value and start another argument.
        public static bool SafeCode(string code)
        {
            if (code == null || code.Length <= CustomerCode.Prefix.Length || code.Length > 16384 || !code.StartsWith(CustomerCode.Prefix, StringComparison.Ordinal)) return false;
            for (int i = CustomerCode.Prefix.Length; i < code.Length; i++)
            {
                char c = code[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '+' || c == '/' || c == '=';
                if (!ok) return false;
            }
            return true;
        }

        public const string BadCode = "This customer code has characters a customer code never has, so it is not put in a command line. Copy it again from TrackVice > Settings > Customers > Customer code.";

        // The package holds an older TrackVice.exe than the one that runs.
        public static bool IsOlder(string inPackage, string running)
        {
            return SemVer.IsValid(inPackage) && SemVer.IsValid(running) && SemVer.Compare(inPackage, running) < 0;
        }

        // The version in the package when it is older than the running one, else null. Reads only the file in its source folder.
        public static string OlderVersion(string sourceFolder, ITsFiles files, string running)
        {
            if (string.IsNullOrEmpty(sourceFolder) || files == null) return null;
            string v = files.VersionOf(FileSys.ExeIn(sourceFolder));
            return IsOlder(v, running) ? v : null;
        }

        // A short, plain reason for a failed step, naming what is missing when that can be told.
        public static string Why(Exception ex, string doing)
        {
            string m = ex.Message.Trim();
            if (ex is UnauthorizedAccessException) return "TrackVice could not " + doing + ": no write access to the source folder. The account that runs TrackVice needs write access to the share. (" + m + ")";
            if (ex is IOException) return "TrackVice could not " + doing + ": " + m;
            string low = m.ToLowerInvariant();
            if (low.Contains("denied") || low.Contains("0x80041003"))
                return "TrackVice could not " + doing + ": the account that runs TrackVice lacks a right on the site. It needs a role that can edit task sequences and packages and distribute content, such as Operating System Deployment Manager or Full Administrator. (" + m + ")";
            return "TrackVice could not " + doing + ": " + m;
        }
    }

    // What to change in a task sequence, worked out from its XML.
    public sealed class TsAgentPlan
    {
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<TsEdit> Edits = new List<TsEdit>();

        // A Run Command Line step that starts the agent.
        public static bool IsAgentStep(TsNode n)
        {
            if (n == null || n.IsGroup || n.Type.IndexOf("RunCommandLine", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return n.Name.StartsWith(TsAgentPackage.Name, StringComparison.OrdinalIgnoreCase)
                || (n.Action.IndexOf("/agent-start", StringComparison.OrdinalIgnoreCase) >= 0 && n.Action.IndexOf(FileSys.ExeName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsPartition(TsNode n)
        {
            return !n.IsGroup && (n.Type.EndsWith("PartitionDiskAction", StringComparison.OrdinalIgnoreCase)
                || n.Name.IndexOf("Partition Disk", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsSetup(TsNode n)
        {
            return !n.IsGroup && (n.Type.EndsWith("SetupWindowsAndSMSAction", StringComparison.OrdinalIgnoreCase)
                || n.Name.IndexOf("Setup Windows and Configuration Manager", StringComparison.OrdinalIgnoreCase) >= 0
                || n.Name.IndexOf("Setup Windows and ConfigMgr", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static TsNode Last(TsSequence s, Predicate<TsNode> p)
        {
            TsNode hit = null;
            foreach (TsNode n in s.All) if (p(n)) hit = n;
            return hit;
        }

        public static TsAgentPlan Make(TsSequence seq, string packageId, string code)
        {
            TsAgentPlan p = new TsAgentPlan();
            string cmd = TsAgentPackage.CommandLine(code);
            Role(p, seq, packageId, cmd, TsAgentPackage.StepWinPE, "WinPE", "Starts the agent in Windows PE.", Last(seq, IsPartition), "Partition Disk");
            Role(p, seq, packageId, cmd, TsAgentPackage.StepWindows, "FullOS", "Starts the agent again in Windows, after the restart that ended it in Windows PE.", Last(seq, IsSetup), "Setup Windows and ConfigMgr");
            foreach (TsNode n in seq.All)
            {
                if (!IsAgentStep(n) || string.Equals(n.Name, TsAgentPackage.StepWinPE, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Name, TsAgentPackage.StepWindows, StringComparison.OrdinalIgnoreCase)) continue;
                p.Lines.Add("The step " + n.Name + " also starts the agent. It is left as it is.");
            }
            // Updates first, then inserts from the last place to the first, so a place does not move before it is used.
            p.Edits.Sort(delegate (TsEdit a, TsEdit b)
            {
                if (a.Insert != b.Insert) return a.Insert ? 1 : -1;
                if (!a.Insert) return 0;
                return -ComparePlace(a, b);
            });
            return p;
        }

        static int ComparePlace(TsEdit a, TsEdit b)
        {
            List<int> x = new List<int>(a.Path), y = new List<int>(b.Path);
            x.Add(a.Index);
            y.Add(b.Index);
            for (int i = 0; i < Math.Min(x.Count, y.Count); i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return x.Count.CompareTo(y.Count);
        }

        static void Role(TsAgentPlan p, TsSequence seq, string pkg, string cmd, string name, string env, string why, TsNode anchor, string anchorName)
        {
            TsStepSpec spec = new TsStepSpec();
            spec.Name = name;
            spec.Description = why + " Added by TrackVice.";
            spec.CommandLine = cmd;
            spec.PackageId = pkg;
            spec.Environment = env;
            TsNode have = null;
            foreach (TsNode n in seq.All) if (!n.IsGroup && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) { have = n; break; }
            if (have != null)
            {
                bool current = have.Action.IndexOf(cmd, StringComparison.Ordinal) >= 0 && have.Action.IndexOf(pkg, StringComparison.OrdinalIgnoreCase) >= 0 && have.ContinueOnError && !have.Disabled;
                if (current) { p.Lines.Add("The step " + name + " is there with this package and command line. Nothing to change."); return; }
                TsEdit u = new TsEdit();
                u.Path = have.Path();
                u.Spec = spec;
                p.Edits.Add(u);
                p.Lines.Add("Set the step " + name + " again: the package " + pkg + ", the command line, Continue on error on, and enabled.");
                return;
            }
            if (anchor == null)
            {
                p.Problems.Add("The step " + name + " is not added: the task sequence has no " + anchorName + " step to put it after. Add it by hand where it should run.");
                return;
            }
            int[] path = anchor.Path();
            TsEdit e = new TsEdit();
            e.Insert = true;
            e.Path = new int[path.Length - 1];
            Array.Copy(path, e.Path, e.Path.Length);
            e.Index = path[path.Length - 1] + 1;
            e.Spec = spec;
            p.Edits.Add(e);
            p.Lines.Add("Add the step " + name + " after " + anchor.Name + (anchor.GroupName != null ? " in the group " + anchor.GroupName : "") + ", with the package " + pkg + ", Continue on error on.");
        }

        public string Text()
        {
            StringBuilder b = new StringBuilder();
            foreach (string l in Lines) b.Append(l).Append("\n");
            foreach (string l in Problems) b.Append(l).Append("\n");
            return b.ToString().TrimEnd('\n');
        }
    }

    // The steps of the page. Nothing is changed on the site before the confirm function says yes.
    public sealed class TsAgentSetup
    {
        readonly ISmsSite site;
        readonly ITsFiles files;
        readonly ITsCopies copies;
        readonly string running;
        readonly Func<string, bool> confirm;
        public Func<DateTime> Now = delegate { return DateTime.Now; };

        public TsAgentSetup(ISmsSite site, ITsFiles files, ITsCopies copies, string runningVersion, Func<string, bool> confirm)
        {
            this.site = site;
            this.files = files;
            this.copies = copies;
            running = runningVersion;
            this.confirm = confirm;
        }

        public ISmsSite Site
        {
            get { return site; }
        }

        public TsAgentState Read(string rememberedSource)
        {
            TsAgentState s = new TsAgentState();
            s.RunningVersion = running;
            List<SmsPackage> found = site.FindPackages(TsAgentPackage.Name);
            if (found.Count > 0) s.Package = found[0];
            string src = s.Package != null && !string.IsNullOrEmpty(s.Package.SourcePath) ? s.Package.SourcePath : rememberedSource;
            if (s.Package != null && !string.IsNullOrEmpty(src)) s.PackageVersion = files.VersionOf(FileSys.ExeIn(src));
            s.Groups = site.DistributionPointGroups();
            s.Sequences = site.TaskSequences();
            s.Sequences.Sort(delegate (SmsTaskSequence a, SmsTaskSequence b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return s;
        }

        // The part of the page about the boot image: whether it has WinPE-NetFx, and how to add it. TrackVice does not change boot images.
        public string BootImageText(string taskSequenceId)
        {
            string id = site.BootImageId(taskSequenceId);
            if (string.IsNullOrEmpty(id)) return "This task sequence does not name a boot image, so TrackVice cannot say whether Windows PE has WinPE-NetFx. Look at the boot image the devices start from.";
            SmsBootImage bi = site.BootImage(id);
            if (bi == null) return "The boot image " + id + " could not be read.";
            if (bi.Components.Contains(TsAgentPackage.NetFx)) return "The boot image " + bi.Name + " has WinPE-NetFx. The agent runs in Windows PE and in Windows.";
            return "The boot image " + bi.Name + " does not have WinPE-NetFx. Add it in the console: Software Library > Operating Systems > Boot Images, the properties of the image, Optional Components. Then update its distribution points. Until then the agent runs only in Windows. TrackVice does not change boot images.";
        }

        public static string CheckSource(string folder)
        {
            string f = (folder ?? "").Trim();
            if (f.Length == 0) return "Enter the source folder first: a share the site server can read, such as \\\\cm01.contoso.com\\Sources\\TrackVice.";
            if (!f.StartsWith("\\\\", StringComparison.Ordinal)) return "The source folder must be a network share that starts with \\\\, so the site server can reach it.";
            // \\?\ and \\.\ name a path on this PC, not a share: a package on the site must not point the copy at a local folder.
            if (f.StartsWith("\\\\?", StringComparison.Ordinal) || f.StartsWith("\\\\.\\", StringComparison.Ordinal) || f.IndexOf('"') >= 0)
                return "The source folder must be a network share, \\\\server\\share\\folder. " + f + " is not one.";
            return null;
        }

        // The text of what making or updating the package does, for the question and for the page.
        public string PackageText(TsAgentState st, string source, List<SmsDpGroup> groups)
        {
            StringBuilder b = new StringBuilder();
            string exe = files.RunningExe;
            if (st.Package == null)
            {
                b.Append("Copy TrackVice ").Append(running).Append(" from this PC to ").Append(source).Append(" as ").Append(FileSys.ExeName).Append(".\n");
                b.Append("Make the package ").Append(TsAgentPackage.Name).Append(" on ").Append(site.Name).Append(" with that folder as its source. It has no program.\n");
            }
            else
            {
                b.Append("Copy TrackVice ").Append(running).Append(" to ").Append(st.Package.SourcePath).Append(" as ").Append(FileSys.ExeName).Append(st.PackageVersion != null ? ", over " + st.PackageVersion : "").Append(".\n");
                b.Append("Update the content of the package ").Append(st.Package.PackageId).Append(" on every distribution point it is on.\n");
            }
            if (groups.Count > 0)
            {
                List<string> names = new List<string>();
                foreach (SmsDpGroup g in groups) names.Add(g.Name);
                b.Append("Add the package to the distribution point groups: ").Append(string.Join(", ", names.ToArray())).Append(".\n");
            }
            else if (st.Package == null) b.Append("It is not distributed: no distribution point group is ticked.\n");
            return b.ToString().TrimEnd('\n');
        }

        // Makes the package, or updates its content when it is there. Asks first, and changes nothing when the answer is no.
        public TsResult MakePackage(TsAgentState st, string source, List<SmsDpGroup> groups)
        {
            string folder = st.Package != null && !string.IsNullOrEmpty(st.Package.SourcePath) ? st.Package.SourcePath : (source ?? "").Trim();
            string bad = CheckSource(folder);
            if (bad != null) return new TsResult(false, bad);
            if (!files.FolderExists(folder)) return new TsResult(false, "TrackVice cannot open " + folder + " from this PC.");
            string exe = files.RunningExe;
            if (string.IsNullOrEmpty(exe)) return new TsResult(false, "TrackVice cannot find its own file to copy.");
            StringBuilder q = new StringBuilder(PackageText(st, folder, groups));
            try
            {
                List<string> others = files.FilesIn(folder);
                others.RemoveAll(delegate (string n) { return string.Equals(n, FileSys.ExeName, StringComparison.OrdinalIgnoreCase); });
                if (others.Count > 0) q.Append("\n\nThe folder also holds ").Append(others.Count).Append(others.Count == 1 ? " other file" : " other files").Append(". They are in the package too.");
            }
            catch (Exception) { }
            q.Append("\n\nNothing else is changed.");
            if (!confirm(q.ToString())) return new TsResult(false, "Nothing was changed.");
            string step = "copy TrackVice.exe to " + folder;
            try
            {
                files.CopyExe(exe, folder);
                string id;
                if (st.Package == null)
                {
                    step = "make the package";
                    id = site.CreatePackage(TsAgentPackage.Name, TsAgentPackage.Description, folder);
                }
                else
                {
                    step = "update the content of the package " + st.Package.PackageId;
                    id = st.Package.PackageId;
                    site.RefreshPackage(id);
                }
                if (groups.Count > 0)
                {
                    step = "add the package to the distribution point groups";
                    List<string> ids = new List<string>();
                    foreach (SmsDpGroup g in groups) ids.Add(g.Id);
                    site.AddToGroups(id, ids);
                }
                TsResult r = new TsResult(true, st.Package == null ? "The package " + TsAgentPackage.Name + " (" + id + ") is made with TrackVice " + running + "." : "The package " + id + " has TrackVice " + running + " and is being sent to its distribution points.");
                r.PackageId = id;
                return r;
            }
            catch (Exception ex) { return new TsResult(false, TsAgentPackage.Why(ex, step)); }
        }

        // The steps that will be added or set, for the page and the question.
        public TsAgentPlan PlanSteps(string taskSequenceId, string packageId, string code)
        {
            return TsAgentPlan.Make(TsSequence.Parse(site.SequenceXml(taskSequenceId)), packageId, code);
        }

        // Adds the two steps, or sets them again when they are there. Keeps a copy of the task sequence first.
        public TsResult AddSteps(string taskSequenceId, string taskSequenceName, string packageId, string code)
        {
            if (string.IsNullOrEmpty(code)) return new TsResult(false, "Add customer makes the customer code the steps need.");
            if (!TsAgentPackage.SafeCode(code)) return new TsResult(false, TsAgentPackage.BadCode);
            if (string.IsNullOrEmpty(packageId)) return new TsResult(false, "Make the package first: the steps run TrackVice.exe from it.");
            string xml, step = "read the task sequence";
            TsAgentPlan plan;
            try
            {
                xml = site.SequenceXml(taskSequenceId);
                plan = TsAgentPlan.Make(TsSequence.Parse(xml), packageId, code);
            }
            catch (FormatException ex) { return new TsResult(false, "TrackVice could not " + step + ": " + ex.Message); }
            catch (Exception ex) { return new TsResult(false, TsAgentPackage.Why(ex, step)); }
            if (plan.Edits.Count == 0) return new TsResult(true, plan.Lines.Count > 0 ? plan.Text() : "There is nothing to change.");
            string q = "In the task sequence " + taskSequenceName + " on " + site.Name + ":\n\n" + plan.Text()
                + "\n\nTrackVice first keeps a copy of the task sequence in " + copies.Folder + ". Undo puts it back.\nNothing else is changed.";
            if (!confirm(q)) return new TsResult(false, "Nothing was changed.");
            string path;
            try { path = copies.Save(taskSequenceId, taskSequenceName, xml, "", Now()); }
            catch (Exception ex) { return new TsResult(false, "Nothing was changed. TrackVice could not keep a copy of the task sequence first: " + ex.Message.Trim()); }
            try { site.EditSequence(taskSequenceId, plan.Edits); }
            catch (Exception ex) { return new TsResult(false, TsAgentPackage.Why(ex, "change the task sequence")); }
            TsResult r = new TsResult(true, "Done. " + plan.Text());
            r.CopyPath = path;
            return r;
        }

        public TsCopy LastCopy(string taskSequenceId)
        {
            try { return copies.Latest(taskSequenceId); }
            catch (Exception) { return null; }
        }

        // Writes the last copy back. Keeps the task sequence as it is now first, so this can be undone too.
        public TsResult Undo(string taskSequenceId, string taskSequenceName)
        {
            TsCopy c = LastCopy(taskSequenceId);
            if (c == null) return new TsResult(false, "No copy of this task sequence is kept on this PC.");
            string q = "Put back " + taskSequenceName + " as it was on " + c.Time.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture) + " on " + site.Name
                + "?\n\nChanges made to it since then are lost. TrackVice first keeps the task sequence as it is now in " + copies.Folder + ".\nNothing else is changed.";
            if (!confirm(q)) return new TsResult(false, "Nothing was changed.");
            try
            {
                copies.Save(taskSequenceId, taskSequenceName, site.SequenceXml(taskSequenceId), FolderCopies.UndoTag, Now());
                site.RestoreSequence(taskSequenceId, c.Xml);
            }
            catch (Exception ex) { return new TsResult(false, TsAgentPackage.Why(ex, "put the task sequence back")); }
            return new TsResult(true, "The task sequence is put back as it was on " + c.Time.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture) + ".");
        }
    }
}
