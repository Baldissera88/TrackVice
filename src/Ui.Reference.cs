namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.Windows.Forms;

    // The references of this Windows user, read once per customer and kept until one is set or cleared.
    // The demo has a reference of its own in memory, so trying it never replaces a customer's reference.
    public static class ReferenceUi
    {
        public const string DemoCustomer = "\u0001demo";
        static ReferenceStore store;
        static readonly Dictionary<string, Reference> cache = new Dictionary<string, Reference>(StringComparer.OrdinalIgnoreCase);
        static Reference demo, demoReference;
        // Screenshots use the made-up demo reference for every customer.
        public static bool UseDemo;

        static ReferenceStore Store
        {
            get { if (store == null) store = new ReferenceStore(new FolderReferenceFiles()); return store; }
        }

        public static string CustomerName(string customer)
        {
            return string.IsNullOrEmpty(customer) ? CustomerBook.DefaultName : customer;
        }

        public static Reference Get(string customer)
        {
            if (UseDemo) return DemoReference();
            if (customer == DemoCustomer) return demo;
            string c = CustomerName(customer);
            Reference r;
            if (cache.TryGetValue(c, out r)) return r;
            try { r = Store.Get(c); }
            catch (Exception) { r = null; }
            cache[c] = r;
            return r;
        }

        public static void Set(string customer, Reference r)
        {
            if (customer == DemoCustomer) { demo = r; return; }
            string c = CustomerName(customer);
            Store.Set(c, r);
            cache[c] = r;
        }

        public static void Clear(string customer)
        {
            if (customer == DemoCustomer) { demo = null; return; }
            string c = CustomerName(customer);
            Store.Clear(c);
            cache[c] = null;
        }

        // The reference Statistics compares each enrollment with.
        public static Reference ForStats(bool isDemo)
        {
            return isDemo ? DemoReference() : Get(CustomerUi.CurrentName);
        }

        // The Autopilot demo as it should have gone: Contoso Timesheet and Adobe Acrobat Reader installed,
        // 7-Zip was not part of it, and a print client was.
        public static Reference DemoReference()
        {
            if (demoReference != null) return demoReference;
            Session s = DemoCompare.A();
            Reference r = Reference.From(s, s.ClockUtc, "Contoso", "the demo");
            r.Computer = "CONTOSO-LT0101";
            r.MarkedUtc = s.ClockUtc.AddDays(-3);
            ReferenceItem zip = null;
            foreach (ReferenceItem x in r.Items)
            {
                if (x.Name == "7-Zip 24.08") zip = x;
                if (x.Name == "Contoso Timesheet" || x.Name == "Adobe Acrobat Reader")
                {
                    x.State = State.Succeeded.ToString();
                    x.ExitCode = 0;
                    x.Seconds = x.Name == "Contoso Timesheet" ? 95 : 240;
                }
            }
            if (zip != null)
            {
                r.Items.Remove(zip);
                zip.Key = "11111111-2222-4333-8444-555555555555";
                zip.Name = "Contoso Print Client 3.1";
                r.Items.Add(zip);
            }
            demoReference = r;
            return r;
        }

        // Where the enrollment shown in the main window came from, in words.
        public static string SourceOf(Engine e)
        {
            switch (e.Mode)
            {
                case Modes.Live: return "this device";
                case Modes.File: return "the saved session " + Path.GetFileName(e.S.SourcePath ?? "");
                case Modes.Agent: return "an upload of the TrackVice agent";
                case Modes.Intune: return "Intune";
                case Modes.Sccm: return "SCCM status messages";
                case Modes.Folder: return "a folder of logs";
                case Modes.Demo: return "the demo";
                default: return e.Mode;
            }
        }

        public static CompareDialog DemoCompareDialog(Theme t)
        {
            Session a = DemoCompare.B();
            Reference r = DemoReference();
            CompareDialog d = new CompareDialog(t, a, a.ClockUtc, "the reference", delegate { return r.ToSession("Reference (" + r.Computer + ")"); });
            d.Text = "Compare with the reference";
            return d;
        }

        // /ship-report on the command line: this device, a folder of logs or a saved session, against the reference of the customer in use.
        public static int ShipCli(string html, string from, string folder, int hours)
        {
            using (Engine e = from != null ? Engine.ForFile(from) : folder != null ? Engine.ForFolder(folder) : Engine.ForLive(hours))
            {
                e.Apply(e.Poll());
                Session s = e.S;
                Reference r = Get(CustomerUi.CurrentName);
                File.WriteAllText(html, Report.ShipHtml(s, e.Now, r), new UTF8Encoding(false));
                ShipVerdict v = ShipCheck.Judge(s, r == null ? null : ReferenceCheck.Check(s, e.Now, r));
                Console.WriteLine(s.Computer + ": " + v.Headline + ".");
                if (r == null) Console.WriteLine("  No reference is set for the customer " + CustomerName(CustomerUi.CurrentName) + ".");
                return v.Ready ? 0 : 2;
            }
        }
    }

    // A small pill in the header: whether the enrollment matches the reference of its customer.
    public sealed class ReferenceBadge : FlatControl
    {
        // 0 matches, 1 differs.
        public int Tone;

        public ReferenceBadge()
        {
            Cursor = Cursors.Hand;
            Height = Dpi.S(24);
            Visible = false;
        }

        public void SetText(string text, int tone)
        {
            Tone = tone;
            Text = text;
            Width = Draw.Measure(text, Fonts.Small).Width + Dpi.S(30);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fg = Tone == 0 ? T.OkFg : T.WarnFg;
            Shapes.Fill(g, r, r.Height / 2f, hover ? T.Hover : T.Card);
            Shapes.Stroke(g, r, r.Height / 2f, fg, 1f);
            int d = Dpi.S(8);
            using (SolidBrush b = new SolidBrush(fg))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(b, Dpi.S(10), (Height - d) / 2f, d, d);
            }
            Draw.Text(g, Text, Fonts.Small, new Rectangle(Dpi.S(23), 0, Width - Dpi.S(27), Height), T.Text, Draw.Line);
        }
    }

    public sealed partial class MainForm
    {
        readonly ReferenceBadge refBadge = new ReferenceBadge();
        ReferenceResult refResult;

        void BuildReference()
        {
            if (start.Shot != null && (start.Page == "reference" || start.Page == "refcompare")) ReferenceUi.UseDemo = true;
            refBadge.Click += Safe.Wrap(delegate { ShowReferenceFinding(); });
            themed.Add(refBadge);
            header.Controls.Add(refBadge);
        }

        // The customer whose reference applies: the one a remote device was opened for, else the one in use.
        string ReferenceCustomer
        {
            get
            {
                if (engine != null && engine.Mode == Modes.Demo) return ReferenceUi.DemoCustomer;
                if (engine != null && engine.IsRemote && watchedCustomer != null) return watchedCustomer;
                return CustomerUi.CurrentName;
            }
        }

        string ReferenceCustomerText
        {
            get { string c = ReferenceCustomer; return c == ReferenceUi.DemoCustomer ? "the demo" : ReferenceUi.CustomerName(c); }
        }

        // Runs on every bind, after the findings are made: adds the reference finding and sets the badge.
        void BindReference()
        {
            refResult = null;
            if (engine == null) return;
            Reference r = ReferenceUi.Get(ReferenceCustomer);
            try { refResult = ReferenceCheck.Apply(engine.S, engine.Now, r); }
            catch (Exception) { refResult = null; }
            if (refResult == null)
            {
                refBadge.Visible = false;
                return;
            }
            refBadge.SetText(refResult.Badge, refResult.Matches ? 0 : 1);
            refBadge.Location = new Point(title.Right + Dpi.S(14), title.Top + (title.Height - refBadge.Height) / 2 + Dpi.S(1));
            refBadge.Visible = true;
            tips.SetToolTip(refBadge, "Compared with the reference enrollment of " + r.Computer + ", set " + Txt.Local(r.MarkedUtc) + (refResult.Matches ? "." : ". Click to see the differences."));
        }

        void ShowReferenceFinding()
        {
            if (refResult == null) return;
            tabs.Selected = TabFindings;
            foreach (Finding f in findings.Rows)
                if (f.Kind == ReferenceCheck.FindingKind) { findings.Select(f); return; }
            Status(refResult.Open ? "No differences from the reference so far. Items it has that did not start yet are not counted until nothing runs." : "This enrollment matches the reference.");
        }

        // The reference entries of the Devices menu, and the reference as a third choice to compare with.
        void ReferenceMenu(ContextMenuStrip m)
        {
            string customer = ReferenceCustomer;
            Reference r = ReferenceUi.Get(customer);
            bool has = engine != null && engine.S.Items.Count > 0;
            foreach (ToolStripItem x in m.Items)
            {
                ToolStripMenuItem top = x as ToolStripMenuItem;
                if (top == null || !top.Text.StartsWith("Compare with another enrollment")) continue;
                ToolStripMenuItem sub = new ToolStripMenuItem("The reference" + (r != null ? " (" + r.Computer + ")" : ""), null, Safe.Wrap(delegate { CompareWithReference(); }));
                sub.ForeColor = theme.Text;
                sub.BackColor = theme.Card;
                sub.Padding = new Padding(Dpi.S(4), Dpi.S(3), Dpi.S(4), Dpi.S(3));
                sub.Enabled = r != null && has;
                top.DropDownItems.Add(sub);
            }
            Menus.Add(m, "Use this enrollment as the reference", delegate { SetReference(); }).Enabled = has && !ReferenceUi.UseDemo;
            Menus.Add(m, "Clear the reference" + (r != null ? " (" + r.Computer + ")" : ""), delegate { ClearReference(); }).Enabled = r != null && !ReferenceUi.UseDemo;
        }

        void SetReference()
        {
            if (engine == null || engine.S.Items.Count == 0) return;
            string customer = ReferenceCustomer;
            string who = ReferenceCustomerText;
            Reference old = ReferenceUi.Get(customer);
            Session s = engine.S;
            if (old != null)
            {
                string q = "The reference of " + who + " is the enrollment of " + old.Computer + ", set " + Txt.Local(old.MarkedUtc) + ". Do you want to use the enrollment of " + s.Computer + " instead?";
                if (MessageBox.Show(this, q, "TrackVice", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            string open = ReferenceCheck.StillOpen(s) > 0 ? " Some of its items still run, so their state and time are as they are now." : "";
            ReferenceUi.Set(customer, Reference.From(s, engine.Now, customer == ReferenceUi.DemoCustomer ? "Demo" : ReferenceUi.CustomerName(customer), ReferenceUi.SourceOf(engine)));
            Bind();
            Status("The enrollment of " + s.Computer + " is now the reference of " + who + ". Every enrollment you watch for " + who + " is compared with it." + open);
        }

        void ClearReference()
        {
            string customer = ReferenceCustomer;
            Reference r = ReferenceUi.Get(customer);
            if (r == null) return;
            if (MessageBox.Show(this, "Clear the reference of " + ReferenceCustomerText + "? Enrollments are then no longer compared with the enrollment of " + r.Computer + ".", "TrackVice", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ReferenceUi.Clear(customer);
            if (engine != null) Bind();
            Status("The reference of " + ReferenceCustomerText + " is cleared.");
        }

        void CompareWithReference()
        {
            Reference r = ReferenceUi.Get(ReferenceCustomer);
            if (engine == null || r == null) return;
            using (CompareDialog d = new CompareDialog(theme, engine.S, engine.Now, "the reference", delegate { return r.ToSession("Reference (" + r.Computer + ")"); }))
            {
                d.Text = "Compare with the reference";
                d.ShowDialog(this);
            }
        }

        void ExportShip()
        {
            if (engine == null) return;
            Reference r = ReferenceUi.Get(ReferenceCustomer);
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "HTML report (*.html)|*.html";
                d.FileName = FileStem + "-ready-to-ship.html";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Session s = engine.S;
                File.WriteAllText(d.FileName, Report.ShipHtml(s, engine.Now, r), new UTF8Encoding(false));
                ShipVerdict v = ShipCheck.Judge(s, r == null ? null : ReferenceCheck.Check(s, engine.Now, r));
                Status("Saved " + d.FileName + ". " + v.Headline + "." + (r == null ? " No reference is set for " + ReferenceCustomerText + "." : ""));
                OpenFile(d.FileName);
            }
        }
    }
}
