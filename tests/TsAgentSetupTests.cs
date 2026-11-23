namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    // Adding the agent to a task sequence through the site, with a site in memory.
    public static class TsAgentSetupTests
    {
        const string Share = "\\\\cm01.contoso.com\\Sources\\TrackVice";
        const string Code = "tv1:made-up-code-for-contoso";
        const string Ts = "P0100231";

        sealed class Rig
        {
            public MemorySmsSite Site = MemorySmsSite.Demo(false, null);
            public MemoryFiles Files = new MemoryFiles();
            public MemoryCopies Copies = new MemoryCopies();
            public int Asked;
            public bool Answer = true;
            public string LastQuestion;
            public TsAgentSetup Setup;

            public Rig()
            {
                Files.Folders[Share] = new List<string>();
                Setup = new TsAgentSetup(Site, Files, Copies, "1.5.1", delegate (string q) { Asked++; LastQuestion = q; return Answer; });
            }
        }

        public static void Run()
        {
            Package();
            Steps();
            Undo();
            NoAnswer();
            Failures();
            Placement();
            Boot();
            Banner();
            PackageLine();
            Store();
        }

        static List<SmsDpGroup> Pick(Rig r, params int[] i)
        {
            List<SmsDpGroup> l = new List<SmsDpGroup>();
            foreach (int k in i) l.Add(r.Site.Groups[k]);
            return l;
        }

        static void Package()
        {
            Rig r = new Rig();
            TsAgentState st = r.Setup.Read(null);
            T.Check(st.Package == null && st.Groups.Count == 3 && st.Sequences.Count == 2, "Add the agent: the page reads the groups and task sequences, and no package yet");
            TsResult res = r.Setup.MakePackage(st, Share, Pick(r, 0, 1));
            T.Check(res.Done && res.PackageId != null, "Add the agent: the package is made");
            T.Eq(1, r.Site.Packages.Count, "Add the agent: one package is made");
            T.Eq(TsAgentPackage.Name, r.Site.Packages[0].Name, "Add the agent: the package is called TrackVice agent");
            T.Eq(Share, r.Site.Packages[0].SourcePath, "Add the agent: the package has the source folder of the user");
            T.Eq(1, r.Files.Copied.Count, "Add the agent: TrackVice.exe is copied to the source folder");
            T.Eq("1.5.1", r.Files.VersionOf(FileSys.ExeIn(Share)), "Add the agent: the copy is this version");
            T.Check(r.Site.InGroups.Count == 2 && r.Site.InGroups[r.Site.Groups[0].Id].Contains(res.PackageId) && r.Site.InGroups[r.Site.Groups[1].Id].Contains(res.PackageId), "Add the agent: the package goes to the groups that are ticked");
            T.Check(!r.Site.InGroups.ContainsKey(r.Site.Groups[2].Id), "Add the agent: and to no other group");
            T.Check(r.LastQuestion.Contains("It has no program") && r.LastQuestion.Contains("All distribution points, Copenhagen") && r.LastQuestion.Contains(Share), "Add the agent: the question says what is made");
            T.Eq(0, r.Site.Refreshed.Count, "Add the agent: making the package does not refresh content");

            // The next time it is updated, not made again.
            r.Files.RunningVersion = "1.6.0";
            r.Files.Versions[FileSys.ExeIn(Share)] = "1.5.1";
            st = r.Setup.Read(null);
            T.Check(st.Package != null && st.PackageVersion == "1.5.1", "Add the agent: the package is found, with the version in its source folder");
            st.RunningVersion = "1.6.0";
            T.Check(st.Older, "Add the agent: a package with an older TrackVice.exe is older");
            TsAgentSetup later = new TsAgentSetup(r.Site, r.Files, r.Copies, "1.6.0", delegate (string q) { r.LastQuestion = q; return true; });
            res = later.MakePackage(st, Share, Pick(r));
            T.Check(res.Done && res.Text.Contains("is being sent"), "Add the agent: an existing package is updated");
            T.Eq(1, r.Site.Packages.Count, "Add the agent: the package is made once");
            T.Eq(1, r.Site.Refreshed[st.Package.PackageId], "Add the agent: its content is refreshed once");
            T.Eq("1.6.0", r.Files.VersionOf(FileSys.ExeIn(Share)), "Add the agent: the new TrackVice.exe is in the source folder");
            T.Check(r.LastQuestion.Contains("over 1.5.1") && r.LastQuestion.Contains("Update the content"), "Add the agent: the question for an update says what changes");
            int creates = 0;
            foreach (string w in r.Site.Writes) if (w.StartsWith("create package", StringComparison.Ordinal)) creates++;
            T.Eq(1, creates, "Add the agent: the package is created once, however many times it is updated");

            // Other files in the folder go in the package, and the question says so.
            Rig o = new Rig();
            o.Files.Folders[Share].Add("readme.txt");
            o.Setup.MakePackage(o.Setup.Read(null), Share, Pick(o));
            T.Check(o.LastQuestion.Contains("also holds 1 other file"), "Add the agent: other files in the source folder are named in the question");
            T.Check(o.LastQuestion.Contains("not distributed"), "Add the agent: a package with no group says it is not distributed");
        }

        static int AgentSteps(string xml)
        {
            int n = 0;
            foreach (TsNode x in TsSequence.Parse(xml).All) if (TsAgentPlan.IsAgentStep(x)) n++;
            return n;
        }

        static void Steps()
        {
            Rig r = new Rig();
            string pkg = r.Setup.MakePackage(r.Setup.Read(null), Share, Pick(r, 0)).PackageId;
            r.Site.Writes.Clear();
            string before = r.Site.Xml[Ts];
            TsResult res = r.Setup.AddSteps(Ts, "Windows 11 24H2 Standard", pkg, Code);
            T.Check(res.Done, "Add the agent: the steps are added");
            string xml = r.Site.Xml[Ts];
            T.Eq(2, AgentSteps(xml), "Add the agent: two steps are added");
            TsSequence q = TsSequence.Parse(xml);
            TsNode pe = null, win = null;
            foreach (TsNode x in q.All) { if (x.Name == TsAgentPackage.StepWinPE) pe = x; if (x.Name == TsAgentPackage.StepWindows) win = x; }
            T.Check(pe != null && pe.Parent != null && pe.Parent.Name == "Prepare disk" && pe.Parent.Children[0].Name == "Partition Disk 0 - UEFI" && pe.Parent.Children[1] == pe, "Add the agent: the Windows PE step goes right after Partition Disk");
            T.Check(win != null && win.Parent != null && win.Parent.Name == "Install Windows" && win.Parent.Children.IndexOf(win) == win.Parent.Children.Count - 1 && win.Parent.Children[win.Parent.Children.Count - 2].Name == "Setup Windows and Configuration Manager", "Add the agent: the Windows step goes right after Setup Windows and ConfigMgr");
            T.Check(pe.ContinueOnError && win.ContinueOnError && !pe.Disabled, "Add the agent: Continue on error is on");
            T.Check(pe.Action.Contains("smsswd.exe /run:" + pkg) && pe.Action.Contains("TrackVice.exe /agent-start /customer \"" + Code + "\""), "Add the agent: the step runs TrackVice.exe from the package with the customer code");
            T.Check(r.LastQuestion.Contains("after Partition Disk 0 - UEFI in the group Prepare disk") && r.LastQuestion.Contains("task-sequence-copies"), "Add the agent: the question shows where, and where the copy goes");
            T.Eq(19 + 2, q.All.Count, "Add the agent: nothing else in the task sequence changes");
            T.Eq(1, r.Copies.Saved.Count, "Add the agent: one copy is kept before the change");
            T.Eq(before, r.Copies.Saved[0].Xml, "Add the agent: the copy is the task sequence as it was");

            // Again: nothing to add.
            int writes = r.Site.Writes.Count;
            r.Asked = 0;
            res = r.Setup.AddSteps(Ts, "Windows 11 24H2 Standard", pkg, Code);
            T.Check(res.Done && res.Text.Contains("Nothing to change"), "Add the agent: when the steps are there with the same package and code, nothing changes");
            T.Eq(writes, r.Site.Writes.Count, "Add the agent: and nothing is written");
            T.Eq(0, r.Asked, "Add the agent: and nothing is asked");
            T.Eq(1, r.Copies.Saved.Count, "Add the agent: and no copy is made");

            // Another code: both steps are set again, not added again.
            res = r.Setup.AddSteps(Ts, "Windows 11 24H2 Standard", pkg, "tv1:another-made-up-code");
            T.Check(res.Done, "Add the agent: the steps are updated");
            T.Eq(2, AgentSteps(r.Site.Xml[Ts]), "Add the agent: the steps are updated instead of added again");
            T.Eq(21, TsSequence.Parse(r.Site.Xml[Ts]).All.Count, "Add the agent: the task sequence has no more steps after an update");
            T.Check(r.Site.Xml[Ts].Contains("another-made-up-code") && !r.Site.Xml[Ts].Contains(Code), "Add the agent: the update has the new command line");
            int inserts = 0, updates = 0;
            foreach (string w in r.Site.Writes) { if (w.StartsWith("insert step", StringComparison.Ordinal)) inserts++; if (w.StartsWith("update step", StringComparison.Ordinal)) updates++; }
            T.Check(inserts == 2 && updates == 2, "Add the agent: two inserts the first time, two updates the second");
            T.Eq(2, r.Copies.Saved.Count, "Add the agent: a copy is kept before the update too");

            // A step with the right name that someone disabled is set again.
            string xml2 = r.Site.Xml[Ts];
            int at = xml2.IndexOf("name=\"TrackVice agent (Windows)\"", StringComparison.Ordinal);
            int off = xml2.IndexOf("disable=\"false\"", at, StringComparison.Ordinal);
            r.Site.Xml[Ts] = xml2.Substring(0, off) + "disable=\"true\"" + xml2.Substring(off + 15);
            r.Setup.AddSteps(Ts, "Windows 11 24H2 Standard", pkg, "tv1:another-made-up-code");
            T.Check(!TsSequence.Parse(r.Site.Xml[Ts]).All.Find(delegate (TsNode x) { return x.Name == TsAgentPackage.StepWindows; }).Disabled, "Add the agent: a disabled step of TrackVice is enabled again");

            res = r.Setup.AddSteps(Ts, "x", null, Code);
            T.Check(!res.Done && res.Text.Contains("package first"), "Add the agent: no steps before the package");
            res = r.Setup.AddSteps(Ts, "x", pkg, "");
            T.Check(!res.Done && res.Text.Contains("customer code"), "Add the agent: no steps without a customer code");
        }

        static void Undo()
        {
            Rig r = new Rig();
            string pkg = r.Setup.MakePackage(r.Setup.Read(null), Share, Pick(r)).PackageId;
            string original = r.Site.Xml[Ts];
            T.Check(!r.Setup.Undo(Ts, "x").Done, "Undo: with no copy there is nothing to put back");
            r.Setup.AddSteps(Ts, "Windows 11 24H2 Standard", pkg, Code);
            string changed = r.Site.Xml[Ts];
            T.Check(changed != original, "Undo: the task sequence is changed");
            TsResult res = r.Setup.Undo(Ts, "Windows 11 24H2 Standard");
            T.Check(res.Done, "Undo: the copy is put back");
            T.Eq(original, r.Site.Xml[Ts], "Undo: the task sequence is as it was");
            T.Eq(2, r.Copies.Saved.Count, "Undo: the task sequence as it was just before is kept too");
            T.Eq(changed, r.Copies.Saved[1].Xml, "Undo: that copy is the changed task sequence");
            T.Eq("before-undo", r.Copies.Tags[1], "Undo: that copy is marked");
            T.Check(r.Setup.LastCopy(Ts).Xml == original, "Undo: the copy to put back is still the one made before the change");
            T.Check(r.LastQuestion.Contains("Changes made to it since then are lost"), "Undo: the question says what is lost");
            // And again, with the same result.
            r.Site.Xml[Ts] = changed;
            T.Check(r.Setup.Undo(Ts, "x").Done && r.Site.Xml[Ts] == original, "Undo: it can be done again");
            T.Check(r.Site.Writes[r.Site.Writes.Count - 1].StartsWith("restore sequence", StringComparison.Ordinal), "Undo: the site is asked to restore");
        }

        static void NoAnswer()
        {
            Rig r = new Rig();
            TsAgentState st = r.Setup.Read(null);
            r.Answer = false;
            TsResult a = r.Setup.MakePackage(st, Share, Pick(r, 0));
            T.Check(!a.Done && a.Text == "Nothing was changed.", "No answer: the package is not made");
            T.Check(r.Site.Writes.Count == 0 && r.Site.Packages.Count == 0 && r.Files.Copied.Count == 0 && r.Site.InGroups.Count == 0, "No answer: nothing is written, copied or distributed");
            r.Answer = true;
            string pkg = r.Setup.MakePackage(st, Share, Pick(r)).PackageId;
            r.Site.Writes.Clear();
            r.Copies.Saved.Clear();
            r.Copies.Tags.Clear();
            string xml = r.Site.Xml[Ts];
            r.Answer = false;
            TsResult b = r.Setup.AddSteps(Ts, "x", pkg, Code);
            T.Check(!b.Done && b.Text == "Nothing was changed." && r.Site.Writes.Count == 0 && r.Site.Xml[Ts] == xml && r.Copies.Saved.Count == 0, "No answer: the steps are not added and no copy is kept");
            r.Answer = true;
            r.Setup.AddSteps(Ts, "x", pkg, Code);
            r.Site.Writes.Clear();
            r.Copies.Saved.Clear();
            string changed = r.Site.Xml[Ts];
            r.Answer = false;
            TsResult c = r.Setup.Undo(Ts, "x");
            T.Check(!c.Done && r.Site.Writes.Count == 0 && r.Site.Xml[Ts] == changed, "No answer: Undo changes nothing");
            r.Answer = true;
            Rig u = new Rig();
            u.Answer = false;
            TsAgentState us = u.Setup.Read(null);
            T.Check(!u.Setup.MakePackage(us, Share, Pick(u)).Done && u.Asked == 1, "No answer: the question is asked once");
            // Reading changes nothing and asks nothing.
            Rig rd = new Rig();
            rd.Setup.Read(null);
            rd.Setup.PlanSteps(Ts, "P0100412", Code);
            rd.Setup.BootImageText(Ts);
            T.Check(rd.Asked == 0 && rd.Site.Writes.Count == 0, "No answer: reading the site asks nothing and writes nothing");
        }

        static void Failures()
        {
            Rig r = new Rig();
            TsAgentState st = r.Setup.Read(null);
            r.Files.FailCopy = "Access to the path is denied.";
            TsResult res = r.Setup.MakePackage(st, Share, Pick(r, 0));
            T.Check(!res.Done && res.Text.Contains("write access to the share"), "Failures: no write access to the share says so");
            T.Eq(0, r.Site.Packages.Count, "Failures: and no package is made");

            r = new Rig();
            r.Site.FailWith = "Access denied";
            res = r.Setup.MakePackage(r.Setup.Read(null), Share, Pick(r));
            T.Check(!res.Done && res.Text.Contains("Operating System Deployment Manager") && res.Text.Contains("make the package"), "Failures: a missing right on the site names the roles");

            r = new Rig();
            string pkg = r.Setup.MakePackage(r.Setup.Read(null), Share, Pick(r)).PackageId;
            r.Site.FailWith = "Generic failure 0x80041003";
            r.Site.Writes.Clear();
            res = r.Setup.AddSteps(Ts, "x", pkg, Code);
            T.Check(!res.Done && res.Text.Contains("change the task sequence") && res.Text.Contains("Full Administrator"), "Failures: a step that fails says which and what is missing");
            T.Eq(1, r.Copies.Saved.Count, "Failures: the copy was kept before the change was tried");

            r = new Rig();
            pkg = r.Setup.MakePackage(r.Setup.Read(null), Share, Pick(r)).PackageId;
            r.Copies.FailSave = "Disk full";
            string xml = r.Site.Xml[Ts];
            res = r.Setup.AddSteps(Ts, "x", pkg, Code);
            T.Check(!res.Done && res.Text.Contains("Nothing was changed") && res.Text.Contains("Disk full") && r.Site.Xml[Ts] == xml, "Failures: when the copy cannot be kept, the task sequence is not changed");

            r = new Rig();
            res = r.Setup.MakePackage(r.Setup.Read(null), "C:\\Sources\\TrackVice", Pick(r));
            T.Check(!res.Done && res.Text.Contains("network share"), "Failures: a local folder is not a source for the site");
            res = r.Setup.MakePackage(r.Setup.Read(null), "", Pick(r));
            T.Check(!res.Done && res.Text.Contains("Enter the source folder"), "Failures: no folder says so");
            res = r.Setup.MakePackage(r.Setup.Read(null), "\\\\cm01.contoso.com\\Sources\\Nothing", Pick(r));
            T.Check(!res.Done && res.Text.Contains("cannot open"), "Failures: a folder that cannot be opened says so");

            r = new Rig();
            r.Site.Xml[Ts] = "<sequence";
            res = r.Setup.AddSteps(Ts, "x", "P0100412", Code);
            T.Check(!res.Done && res.Text.Contains("not XML"), "Failures: XML that does not read changes nothing");
        }

        const string Flat =
            "<sequence version=\"3.00\">" +
            "<step type=\"SMS_TaskSequence_PartitionDiskAction\" name=\"Format and Partition Disk (BIOS)\"/>" +
            "<step type=\"SMS_TaskSequence_PartitionDiskAction\" name=\"Format and Partition Disk (UEFI)\"/>" +
            "<step type=\"SMS_TaskSequence_ApplyOperatingSystemAction\" name=\"Apply Operating System\"/>" +
            "<step type=\"SMS_TaskSequence_SetupWindowsAndSMSAction\" name=\"Setup Windows and ConfigMgr\"/>" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Start agent by hand\"><action>smsswd.exe /run:P0100001 TrackVice.exe /agent-start /customer \"x\"</action></step>" +
            "</sequence>";

        static void Placement()
        {
            TsAgentPlan p = TsAgentPlan.Make(TsSequence.Parse(Flat), "P0100412", Code);
            T.Eq(2, p.Edits.Count, "Placement: two steps are added");
            T.Check(p.Edits[0].Index > p.Edits[1].Index, "Placement: the inserts run from the last place to the first");
            T.Check(p.Edits[1].Index == 2 && p.Edits[0].Index == 4, "Placement: after the last Partition Disk step and after Setup Windows, in the list of the top");
            T.Check(p.Text().Contains("also starts the agent") && p.Text().Contains("Start agent by hand"), "Placement: a step of the user that starts the agent is named and left alone");
            MemorySmsSite s = new MemorySmsSite();
            s.Xml["X"] = Flat;
            s.EditSequence("X", p.Edits);
            TsSequence q = TsSequence.Parse(s.Xml["X"]);
            T.Check(q.All[2].Name == TsAgentPackage.StepWinPE && q.All[4].Name == "Setup Windows and ConfigMgr" && q.All[5].Name == TsAgentPackage.StepWindows, "Placement: both steps are in the right places after the edits");
            // Without a Partition Disk step, only the Windows step is added, and the other is explained.
            string noPart = Flat.Replace("PartitionDiskAction", "ApplyDataImageAction").Replace("Format and Partition Disk", "Apply Data");
            p = TsAgentPlan.Make(TsSequence.Parse(noPart), "P0100412", Code);
            T.Check(p.Edits.Count == 1 && p.Problems.Count == 1 && p.Problems[0].Contains("Partition Disk"), "Placement: with no Partition Disk step, that step is not added and the reason is given");
            string noSetup = Flat.Replace("SetupWindowsAndSMSAction", "ApplyDataImageAction").Replace("Setup Windows and ConfigMgr", "Something else");
            p = TsAgentPlan.Make(TsSequence.Parse(noSetup), "P0100412", Code);
            T.Check(p.Edits.Count == 1 && p.Problems.Count == 1 && p.Problems[0].Contains("Setup Windows"), "Placement: with no Setup Windows step, that step is not added and the reason is given");
        }

        static void Boot()
        {
            Rig r = new Rig();
            T.Check(r.Setup.BootImageText(Ts).Contains("has WinPE-NetFx"), "Boot image: it says when the boot image has WinPE-NetFx");
            T.Check(r.Setup.BootImageText("P0100245").Contains("does not have WinPE-NetFx") && r.Setup.BootImageText("P0100245").Contains("Optional Components") && r.Setup.BootImageText("P0100245").Contains("TrackVice does not change boot images"), "Boot image: it says how to add WinPE-NetFx in the console, and that TrackVice does not");
            T.Check(r.Setup.BootImageText("P0100245").Contains("only in Windows"), "Boot image: it says the agent then runs only in Windows");
            r.Site.BootImageOf.Remove(Ts);
            T.Check(r.Setup.BootImageText(Ts).Contains("does not name a boot image"), "Boot image: a task sequence without one says so");
            r.Site.BootImageOf[Ts] = "P0109999";
            T.Check(r.Setup.BootImageText(Ts).Contains("could not be read"), "Boot image: a boot image that cannot be read says so");
            T.Eq(14L, TsAgentPackage.NetFx, "Boot image: WinPE-NetFx is optional component 14");
            T.Eq(0, r.Site.Writes.Count, "Boot image: boot images are never changed");
        }

        static void Banner()
        {
            MemoryFiles f = new MemoryFiles();
            f.Versions[FileSys.ExeIn(Share)] = "1.4.0";
            T.Eq("1.4.0", TsAgentPackage.OlderVersion(Share, f, "1.5.1"), "Banner: a package with an older TrackVice.exe is offered an update");
            f.Versions[FileSys.ExeIn(Share)] = "1.5.1";
            T.Eq((string)null, TsAgentPackage.OlderVersion(Share, f, "1.5.1"), "Banner: not when the package has this version");
            f.Versions[FileSys.ExeIn(Share)] = "1.6.0";
            T.Eq((string)null, TsAgentPackage.OlderVersion(Share, f, "1.5.1"), "Banner: not when the package has a newer one");
            T.Eq((string)null, TsAgentPackage.OlderVersion("\\\\cm01.contoso.com\\Nothing", f, "1.5.1"), "Banner: not when the file cannot be read");
            T.Eq((string)null, TsAgentPackage.OlderVersion(null, f, "1.5.1"), "Banner: not without a source folder");
        }

        static void PackageLine()
        {
            MemoryFiles f = new MemoryFiles();
            f.Folders[Share] = new List<string>();
            f.Versions[FileSys.ExeIn(Share)] = "1.4.0";
            T.Eq("1.4.0", TsPackageCheck.Older(Share, null, f, "1.5.1"), "Package line: an older package is found from the remembered folder");
            T.Eq("1.4.0", TsPackageCheck.Older("", delegate { return Share; }, f, "1.5.1"), "Package line: the folder comes from the site when none is remembered");
            T.Eq((string)null, TsPackageCheck.Older(Share, null, f, "1.4.0"), "Package line: nothing when the package is as new");
            T.Eq((string)null, TsPackageCheck.Older("", delegate { throw new InvalidOperationException("no"); }, f, "1.5.1"), "Package line: nothing when the site cannot be asked");
            T.Eq((string)null, TsPackageCheck.Older("", null, f, "1.5.1"), "Package line: nothing without a folder");
            T.Check(TsPackageCheck.Text("1.4.0", "1.5.1").Contains("1.4.0") && TsPackageCheck.Text("1.4.0", "1.5.1").Contains("1.5.1"), "Package line: the text names both versions");
        }

        static void Store()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-ts-copies-" + Guid.NewGuid().ToString("N"));
            try
            {
                FolderCopies c = new FolderCopies(dir);
                T.Eq((TsCopy)null, c.Latest(Ts), "Copies: none before the first");
                DateTime t = new DateTime(2026, 11, 20, 9, 30, 15);
                string p1 = c.Save(Ts, "Windows 11 24H2 Standard", "<sequence><step name=\"A\"/></sequence>", "", t);
                string p2 = c.Save(Ts, "Windows 11 24H2 Standard", "<sequence><step name=\"B\"/></sequence>", "", t.AddHours(1));
                c.Save(Ts, "Windows 11 24H2 Standard", "<sequence><step name=\"C\"/></sequence>", FolderCopies.UndoTag, t.AddHours(2));
                c.Save("P0100245", "Other", "<sequence/>", "", t.AddHours(3));
                TsCopy last = c.Latest(Ts);
                T.Check(last != null && last.Xml.Contains("name=\"B\"") && last.Time == t.AddHours(1), "Copies: the newest copy made before a change, with its time");
                T.Check(File.Exists(p1) && File.Exists(p2) && Path.GetFileName(p1).StartsWith(Ts + "-20261120-093015", StringComparison.Ordinal), "Copies: each copy is a file named with the task sequence and the time");
                bool bad = false;
                try { c.Save("..\\x", "n", "<a/>", "", t); }
                catch (ArgumentException) { bad = true; }
                T.Check(bad, "Copies: an ID with path characters is refused");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
        }
    }
}
