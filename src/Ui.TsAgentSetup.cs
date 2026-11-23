namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // Devices > Add the agent to a task sequence: makes the TrackVice package on the site and adds the two steps to a task sequence.
    sealed class TsAgentDialog : GuideForm
    {
        public const string DemoVersion = "1.5.1";
        public const string DemoSource = "\\\\cm01.contoso.com\\Sources\\TrackVice";

        readonly string customer, code;
        readonly TextBox tSource;
        readonly ComboBox cTs;
        readonly FlatButton bConnect, bPackage, bSteps, bUndo;
        readonly List<Toggle> gGroups = new List<Toggle>();
        TsAgentSetup setup;
        ISmsSite site;
        ITsFiles files;
        ITsCopies copies;
        TsAgentState st;
        string bootText = "", planText = "", lastText = "", connectText = "";
        bool lastBad, filling;
        TsCopy lastCopy;

        public TsAgentDialog(Theme t, string customer, string code, bool isDemo, string variant) : base("Add the agent to a task sequence", t, null, isDemo)
        {
            this.customer = customer;
            this.code = code;
            Size = new Size(Dpi.S(860), Dpi.S(760));
            tSource = Field(360, true);
            cTs = Combo(360, true);
            cTs.SelectedIndexChanged += Safe.Wrap(delegate { if (!filling) Pick(false); });
            bConnect = Button("Connect", delegate { Connect(variant); });
            bPackage = Button("Make the package", delegate { DoPackage(); });
            bPackage.Primary = true;
            bSteps = Button("Add the steps", delegate { DoSteps(); });
            bSteps.Primary = true;
            bUndo = Button("Undo", delegate { DoUndo(); });
            Add(body);
            Rebuild();
            if (demo) Connect(variant);
        }

        public static void Open(IWin32Window owner, Theme t, string customer, string code)
        {
            using (TsAgentDialog d = new TsAgentDialog(t, customer, code, false, null)) d.ShowDialog(owner);
        }

        string Server
        {
            get { return demo ? "cm01.contoso.com" : Prefs.Get("SccmServer", ""); }
        }

        bool Confirm(string question)
        {
            if (demo) return true;
            bool ok = false;
            Invoke((MethodInvoker)delegate
            {
                ok = MessageBox.Show(this, question, "Add the agent to a task sequence", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
            });
            return ok;
        }

        // Made-up site for the screenshots. "new": no package yet. "done": the steps are in.
        void MakeDemo(string variant)
        {
            bool made = variant != "new";
            MemorySmsSite s = MemorySmsSite.Demo(made, DemoSource);
            MemoryFiles f = new MemoryFiles();
            f.RunningVersion = DemoVersion;
            f.Folders[DemoSource] = new List<string>(new string[] { FileSys.ExeName });
            if (made) f.Versions[FileSys.ExeIn(DemoSource)] = variant == "done" ? DemoVersion : "1.4.0";
            site = s;
            files = f;
            copies = new MemoryCopies();
            setup = new TsAgentSetup(site, files, copies, DemoVersion, Confirm);
            if (variant == "done")
            {
                TsResult r = setup.AddSteps("P0100231", "Windows 11 24H2 Standard", "P0100412", code);
                lastText = r.Text;
            }
        }

        void Connect(string variant)
        {
            if (busy) return;
            string remembered = demo ? DemoSource : Prefs.Get("TsAgentSource", "");
            if (!demo && Server.Length == 0)
            {
                note.Say("Set the SMS Provider first: Devices > SMS Provider connection.", true);
                return;
            }
            TsAgentState s = null;
            Work("Connecting to the SMS Provider...", delegate
            {
                if (demo) MakeDemo(variant);
                else
                {
                    SccmWmi w = new SccmWmi(Server, Prefs.Get("SccmSite", ""));
                    Exception err;
                    if (!Guard.Run(delegate { w.Connect(); }, TimeSpan.FromSeconds(45), out err)) throw new TimeoutException(Server + " did not answer within 45 seconds.");
                    if (err != null) throw err;
                    site = new SmsSiteWmi(w);
                    files = new FileSys(Program.Location);
                    copies = new FolderCopies(FolderCopies.DefaultFolder());
                    setup = new TsAgentSetup(site, files, copies, Program.Version, Confirm);
                }
                s = setup.Read(remembered);
            }, delegate
            {
                st = s;
                connectText = "Connected to " + site.Name + ".";
                filling = true;
                tSource.Text = st.Package != null && !string.IsNullOrEmpty(st.Package.SourcePath) ? st.Package.SourcePath : remembered;
                gGroups.Clear();
                foreach (SmsDpGroup g in st.Groups)
                {
                    Toggle tg = Kept(new Toggle());
                    tg.Changed += delegate { if (!filling) Rebuild(); };
                    gGroups.Add(tg);
                }
                cTs.Items.Clear();
                foreach (SmsTaskSequence q in st.Sequences) cTs.Items.Add(q.Name);
                if (cTs.Items.Count > 0) cTs.SelectedIndex = demo && cTs.Items.IndexOf("Windows 11 24H2 Standard") >= 0 ? cTs.Items.IndexOf("Windows 11 24H2 Standard") : 0;
                filling = false;
                Pick(true);
            }, delegate { setup = null; });
        }

        SmsTaskSequence Chosen()
        {
            int i = cTs.SelectedIndex;
            return st != null && i >= 0 && i < st.Sequences.Count ? st.Sequences[i] : null;
        }

        List<SmsDpGroup> ChosenGroups()
        {
            List<SmsDpGroup> l = new List<SmsDpGroup>();
            for (int i = 0; st != null && i < gGroups.Count && i < st.Groups.Count; i++)
                if (gGroups[i].On) l.Add(st.Groups[i]);
            return l;
        }

        string PackageId
        {
            get { return st != null && st.Package != null ? st.Package.PackageId : null; }
        }

        // Reads what the page says about the chosen task sequence. Runs away from the window.
        void Describe(string id)
        {
            string boot = "", plan = "";
            TsCopy c = null;
            if (id != null)
            {
                try { boot = setup.BootImageText(id); }
                catch (Exception ex) { boot = TsAgentPackage.Why(ex, "read the boot image"); }
                try { plan = setup.PlanSteps(id, PackageId ?? TsAgentPackage.Name, code ?? "<customer code>").Text(); }
                catch (FormatException ex) { plan = "TrackVice could not read this task sequence: " + ex.Message; }
                catch (Exception ex) { plan = TsAgentPackage.Why(ex, "read the task sequence"); }
                c = setup.LastCopy(id);
            }
            bootText = boot;
            planText = plan;
            lastCopy = c;
        }

        void Pick(bool quiet)
        {
            SmsTaskSequence q = Chosen();
            if (q == null || setup == null) { Rebuild(); return; }
            if (quiet && !busy)
            {
                Describe(q.PackageId);
                Rebuild();
                return;
            }
            Work("Reading " + q.Name + "...", delegate { Describe(q.PackageId); }, null, null);
        }

        void DoPackage()
        {
            if (setup == null || busy) return;
            string src = tSource.Text.Trim();
            List<SmsDpGroup> g = ChosenGroups();
            TsResult r = null;
            string id = Chosen() != null ? Chosen().PackageId : null;
            Work("Working on the package...", delegate
            {
                r = setup.MakePackage(st, src, g);
                if (r.Done)
                {
                    st = setup.Read(src);
                    Describe(id);
                }
            }, delegate
            {
                lastText = r.Text;
                lastBad = !r.Done;
                if (r.Done && !demo) Prefs.Set("TsAgentSource", src);
                note.Say(r.Text, !r.Done);
            }, null);
        }

        void DoSteps()
        {
            SmsTaskSequence q = Chosen();
            if (setup == null || busy || q == null) return;
            TsResult r = null;
            Work("Changing the task sequence...", delegate
            {
                r = setup.AddSteps(q.PackageId, q.Name, PackageId, code);
                Describe(q.PackageId);
            }, delegate
            {
                lastText = r.Text;
                lastBad = !r.Done;
                note.Say(r.Done ? "Done." : r.Text, !r.Done);
            }, null);
        }

        void DoUndo()
        {
            SmsTaskSequence q = Chosen();
            if (setup == null || busy || q == null) return;
            TsResult r = null;
            Work("Putting the task sequence back...", delegate
            {
                r = setup.Undo(q.PackageId, q.Name);
                Describe(q.PackageId);
            }, delegate
            {
                lastText = r.Text;
                lastBad = !r.Done;
                note.Say(r.Done ? "Done." : r.Text, !r.Done);
            }, null);
        }

        protected override void Build(List<Control> l)
        {
            l.Add(new SectionTitle("Add the agent to a task sequence"));
            l.Add(Row(Glyphs.Info, "What this does", "Makes a package with TrackVice.exe on the site, and adds two steps to a task sequence: one after Partition Disk for Windows PE and one after Setup Windows and ConfigMgr for Windows. Both continue on error. TrackVice shows what it will change and asks first, and keeps a copy of the task sequence so Undo can put it back.", null));
            l.Add(Row(Glyphs2.Device, "SMS Provider", setup != null ? connectText : Server.Length > 0 ? Server + ". Connecting reads packages and task sequences; it changes nothing." : "Not set. Devices > SMS Provider connection saves the server.", setup != null ? null : bConnect));
            if (setup == null || st == null)
            {
                BuildByHand(l);
                return;
            }
            l.Add(new SectionTitle("1. The package"));
            bPackage.Text = st.Package == null ? "Make the package" : "Update the package";
            bPackage.AutoWidth();
            string state = st.Package == null
                ? "The package " + TsAgentPackage.Name + " is not on the site yet."
                : "The package " + st.Package.PackageId + " has TrackVice " + (st.PackageVersion ?? "(version unknown)") + (st.Older ? ". This PC runs " + st.RunningVersion + ", which is newer." : ".");
            l.Add(Row(Glyphs.Save, "Package " + TsAgentPackage.Name, state + "\n" + setup.PackageText(st, tSource.Text.Trim(), ChosenGroups()), bPackage));
            l.Add(Row(Glyphs2.Code, "Source folder", st.Package == null ? "A share the site server can read. TrackVice copies itself there as TrackVice.exe. It is remembered for next time." : "Where the package has its files. It is kept from the package.", tSource));
            tSource.Enabled = st.Package == null;
            for (int i = 0; i < st.Groups.Count && i < gGroups.Count; i++)
                l.Add(Row(Glyphs2.Device, st.Groups[i].Name, "Distribution point group, " + st.Groups[i].Members + (st.Groups[i].Members == 1 ? " distribution point" : " distribution points") + ". Tick it to add the package.", gGroups[i]));
            l.Add(new SectionTitle("2. The task sequence"));
            l.Add(Row(Glyphs2.Apps, "Task sequence", st.Sequences.Count > 0 ? "The two steps go into this one." : "The site has no task sequences.", cTs));
            if (Chosen() != null)
            {
                l.Add(Row(Glyphs.Info, "Boot image", bootText, null));
                bool ready = code != null && PackageId != null;
                bSteps.Enabled = ready && !busy;
                string why = code == null ? "\nAdd customer makes the customer code the steps need." : PackageId == null ? "\nMake the package first." : "";
                l.Add(Row(Glyphs2.Code, "Steps", planText + why, bSteps));
                if (lastCopy != null)
                {
                    bUndo.Enabled = !busy;
                    l.Add(Row(Glyphs.Save, "Undo", "Puts back the task sequence as it was on " + lastCopy.Time.ToString("d MMMM yyyy HH:mm") + ". Copies are in " + copies.Folder + ".", bUndo));
                }
            }
            if (lastText.Length > 0) l.Add(Row(lastBad ? Glyphs.Shield : Glyphs.Info, lastBad ? "Not done" : "Last result", lastText, null));
            BuildByHand(l);
        }

        // The manual way, for a site that TrackVice cannot change.
        void BuildByHand(List<Control> l)
        {
            l.Add(new SectionTitle("By hand"));
            FlatButton copy = Kept(SmallButton("Copy", delegate
            {
                if (code == null) { note.Say("Devices > Intune / cloud > Add customer makes the customer code.", true); return; }
                note.Say(Menus.Copy(TsAgentPackage.CommandLine(code)) ? "Copied the command line." : "The command line could not be copied.", false);
            }));
            l.Add(Row(Glyphs2.Code, "Run Command Line, with the TrackVice package",
                code != null ? "With the customer code of " + customer + ". Copy puts the whole command on the clipboard." : "Devices > Intune / cloud > Add customer makes the customer code.", copy));
            l.Add(new KeyTable(false,
                new string[] { "After Partition Disk", "Starts the agent in Windows PE. The boot image needs WinPE-NetFx." },
                new string[] { "After Setup Windows and ConfigMgr", "Starts the agent again in Windows." },
                new string[] { "On every one of these steps", "Tick Continue on error on the Options tab." }));
        }
    }
}
