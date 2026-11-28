namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Windows.Forms;

    // Asks what the customer report needs: the customer, who prepared it and, for a report over many devices, the period.
    public sealed class CustomerReportDialog : Form
    {
        public static readonly string[] Periods = { "Today", "Last 7 days", "Last 30 days", "Custom" };
        readonly TextBox customer = new TextBox(), by = new TextBox();
        readonly Segmented period = new Segmented(Periods);
        readonly DateTimePicker from = new DateTimePicker(), to = new DateTimePicker();
        readonly CheckBox pages = new CheckBox(), pdf = new CheckBox();
        readonly Label lFrom = new Label(), lTo = new Label(), lRange = new Label();
        readonly bool batch;
        public string Customer, PreparedBy;
        public bool MakePdf, PerDevice;
        public int Period;
        public DateTime From, To;

        public CustomerReportDialog(Theme t, bool batch, string customerName, string preparedBy, bool edge)
        {
            this.batch = batch;
            Text = batch ? "Customer report for the devices in Overview" : "Customer report";
            Font = Fonts.Ui;
            Icon = Program.AppIcon();
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            BackColor = t.Bg;
            ForeColor = t.Text;
            int w = Dpi.S(520), x = Dpi.S(20), fw = w - 2 * x, y = Dpi.S(16);
            Label intro = new Label();
            intro.Text = batch
                ? "A report for the customer over the devices of the current filter: the numbers, a chart per day, the devices and what failed or took long."
                : "A report for the customer about this device: the result, the phases, the apps, scripts and policies, and what was found. Passwords and keys are hidden.";
            intro.ForeColor = t.Text2;
            intro.SetBounds(x, y, fw, Dpi.S(48));
            Controls.Add(intro);
            y += Dpi.S(56);
            y = Field("Customer", customer, customerName, t, x, y, fw);
            y = Field("Prepared by (shown in the footer)", by, preparedBy, t, x, y, fw);
            if (batch)
            {
                Label l = Caption("Period", t, x, y, fw);
                period.ApplyTheme(t);
                period.Location = new Point(x, y + Dpi.S(20));
                period.Selected = 1;
                Controls.AddRange(new Control[] { l, period });
                y += Dpi.S(60);
                lFrom.Text = "From";
                lTo.Text = "to";
                foreach (Label c in new Label[] { lFrom, lTo }) { c.ForeColor = t.Text2; c.AutoSize = true; }
                from.Format = DateTimePickerFormat.Short;
                to.Format = DateTimePickerFormat.Short;
                from.Width = to.Width = Dpi.S(130);
                from.Value = DateTime.Today.AddDays(-6);
                to.Value = DateTime.Today;
                lFrom.Location = new Point(x, y + Dpi.S(5));
                from.Location = new Point(x + Dpi.S(44), y);
                lTo.Location = new Point(from.Right + Dpi.S(12), y + Dpi.S(5));
                to.Location = new Point(lTo.Right + Dpi.S(10), y);
                lRange.ForeColor = t.Text2;
                lRange.AutoSize = true;
                lRange.Location = new Point(x, y + Dpi.S(5));
                Controls.AddRange(new Control[] { lFrom, from, lTo, to, lRange });
                period.Changed += delegate { Custom(); };
                Custom();
                y += Dpi.S(38);
                Check(pages, "A page for every device", t, x, y, fw);
                y += Dpi.S(30);
            }
            Check(pdf, edge ? "Also make a PDF with Microsoft Edge" : "A PDF needs Microsoft Edge, which was not found", t, x, y, fw);
            pdf.Checked = edge;
            pdf.Enabled = edge;
            y += Dpi.S(44);
            FlatButton ok = new FlatButton("Make the report", null), cancel = new FlatButton("Cancel", null);
            ok.Primary = true;
            ok.Width = Math.Max(ok.Width, Dpi.S(140));
            cancel.Width = Math.Max(cancel.Width, Dpi.S(96));
            ok.Location = new Point(w - x - ok.Width, y);
            cancel.Location = new Point(ok.Left - Dpi.S(8) - cancel.Width, y);
            ok.ApplyTheme(t);
            cancel.ApplyTheme(t);
            ok.Click += Safe.Wrap(delegate { Accept(); });
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { ok, cancel });
            ClientSize = new Size(w, y + ok.Height + Dpi.S(16));
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
            };
            HandleCreated += delegate { Draw.TitleBar(Handle, t); };
        }

        static Label Caption(string text, Theme t, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = t.Text3;
            l.Font = Fonts.Small;
            l.SetBounds(x, y, w, Dpi.S(18));
            return l;
        }

        int Field(string label, TextBox box, string value, Theme t, int x, int y, int w)
        {
            Controls.Add(Caption(label, t, x, y, w));
            box.Text = value ?? "";
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = t.Field;
            box.ForeColor = t.Text;
            box.SetBounds(x, y + Dpi.S(20), w, Dpi.S(26));
            Controls.Add(box);
            return y + Dpi.S(58);
        }

        void Check(CheckBox c, string text, Theme t, int x, int y, int w)
        {
            c.Text = text;
            c.ForeColor = t.Text;
            c.BackColor = t.Bg;
            c.FlatStyle = FlatStyle.Flat;
            c.SetBounds(x, y, w, Dpi.S(24));
            Controls.Add(c);
        }

        void Custom()
        {
            bool on = period.Selected == 3;
            foreach (Control c in new Control[] { lFrom, from, lTo, to }) c.Visible = on;
            // The days a fixed period covers, where the dates of a custom one are picked.
            lRange.Visible = !on;
            DateTime today = DateTime.Today;
            DateTime first = period.Selected == 0 ? today : today.AddDays(period.Selected == 1 ? -6 : -29);
            lRange.Text = period.Selected == 0 ? today.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)
                : first.ToString("d MMM", CultureInfo.InvariantCulture) + " to " + today.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ", by the day the deployment started";
        }

        void Accept()
        {
            Customer = customer.Text.Trim();
            PreparedBy = by.Text.Trim();
            MakePdf = pdf.Enabled && pdf.Checked;
            PerDevice = batch && pages.Checked;
            Period = period.Selected;
            DateTime today = DateTime.Today;
            if (Period == 0) { From = today; To = today; }
            else if (Period == 1) { From = today.AddDays(-6); To = today; }
            else if (Period == 2) { From = today.AddDays(-29); To = today; }
            else
            {
                From = from.Value.Date;
                To = to.Value.Date;
                if (To < From) { DateTime x = From; From = To; To = x; }
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    public static class CustomerReportUi
    {
        [System.Runtime.InteropServices.DllImport("secur32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetUserNameEx(int format, StringBuilder name, ref int size);

        // The name set for reports, else the display name of the Windows user, else the user name.
        public static string PreparedBy()
        {
            string saved = Prefs.Get("ReportPreparedBy", null);
            if (!string.IsNullOrWhiteSpace(saved)) return saved.Trim();
            try
            {
                StringBuilder b = new StringBuilder(256);
                int n = b.Capacity;
                if (GetUserNameEx(3, b, ref n) != 0 && b.Length > 0) return b.ToString();
            }
            catch (Exception) { }
            try { return Environment.UserName; } catch (Exception) { return ""; }
        }

        public static void Remember(string name)
        {
            if (!string.IsNullOrWhiteSpace(name)) Prefs.Set("ReportPreparedBy", name.Trim());
        }

        public static string PeriodText(int period, DateTime from, DateTime to)
        {
            if (period == 0) return "Today, " + from.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            if (period == 1) return "Last 7 days, " + from.ToString("d MMM", CultureInfo.InvariantCulture) + " to " + to.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            if (period == 2) return "Last 30 days, " + from.ToString("d MMM", CultureInfo.InvariantCulture) + " to " + to.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            return from.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + (from == to ? "" : " to " + to.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
        }

        // Writes the HTML, and the PDF next to it when asked and Edge is there. Returns what to tell the user.
        public static string Save(string html, string htmlPath, bool pdf, out string openPath)
        {
            File.WriteAllText(htmlPath, html, new UTF8Encoding(false));
            openPath = htmlPath;
            if (!pdf) return "Saved " + htmlPath;
            string pdfPath = Path.ChangeExtension(htmlPath, ".pdf");
            PdfResult r = PdfPrint.Make(PdfPrint.FindEdge(), htmlPath, pdfPath, null);
            if (r.Ok)
            {
                openPath = pdfPath;
                return "Saved " + pdfPath + " and " + htmlPath;
            }
            return "Saved " + htmlPath + ". " + r.Message;
        }
    }

    public sealed partial class MainForm
    {
        // Export > Customer report (PDF)…: the report of the device shown.
        void ExportCustomerReport()
        {
            if (engine == null) return;
            bool edge = PdfPrint.FindEdge() != null;
            string customer = watchedCustomer ?? (engine.Mode == Modes.Demo ? "Contoso" : "");
            using (CustomerReportDialog dlg = new CustomerReportDialog(theme, false, customer, CustomerReportUi.PreparedBy(), edge))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string path = AskReportPath(FileStem + "-report.html");
                if (path == null) return;
                CustomerReportOptions o = new CustomerReportOptions();
                o.Customer = dlg.Customer;
                o.PreparedBy = dlg.PreparedBy;
                CustomerReportUi.Remember(dlg.PreparedBy);
                // The session is read here, on the window's thread; only the files are written in the background.
                string html = CustomerReport.Html(engine.S, engine.Now, o);
                Status("Making the report\u2026");
                MakeReport(delegate { return html; }, path, dlg.MakePdf);
            }
        }

        string AskReportPath(string name)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "HTML report (*.html)|*.html";
                d.FileName = name;
                return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
            }
        }

        // Builds and saves the report away from the window, since Edge can take a while, then says what was saved and opens it.
        void MakeReport(Func<string> build, string path, bool pdf)
        {
            Cursor = Cursors.AppStarting;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string say, open = path;
                try { say = CustomerReportUi.Save(build(), path, pdf, out open); }
                catch (Exception ex) { say = "The report could not be made: " + ex.Message; open = null; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Safe.Run(delegate
                        {
                            Cursor = Cursors.Default;
                            Status(say);
                            if (open != null) OpenFile(open);
                        });
                    });
                }
                catch (Exception) { }
            });
        }

        // Overview > Report…: the devices of the filter over a period.
        void OverviewReport()
        {
            OverviewSnapshot snap = overviewSnap;
            if (snap == null || overviewPage == null) { Status("Overview has not read any devices yet."); return; }
            OverviewFilter filter = overviewPage.Filter;
            string customer = filter.Customer;
            if (string.IsNullOrEmpty(customer) && snap.Customers.Count == 1) customer = snap.Customers[0];
            if (string.IsNullOrEmpty(customer)) customer = snap.Customers.Count > 1 ? "All customers" : "";
            bool edge = PdfPrint.FindEdge() != null;
            using (CustomerReportDialog dlg = new CustomerReportDialog(theme, true, customer, CustomerReportUi.PreparedBy(), edge))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string path = AskReportPath("TrackVice-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".html");
                if (path == null) return;
                CustomerReportUi.Remember(dlg.PreparedBy);
                DateTime now = DateTime.UtcNow;
                DateTime day1 = dlg.From, day2 = dlg.To;
                BatchOptions o = new BatchOptions();
                o.Customer = dlg.Customer;
                o.PreparedBy = dlg.PreparedBy;
                o.PeriodText = CustomerReportUi.PeriodText(dlg.Period, dlg.From, dlg.To);
                o.FilterText = filter.Side == DeviceSides.Intune ? "Intune devices" : filter.Side == DeviceSides.Sccm ? "SCCM task sequences" : null;
                o.FromDay = dlg.From;
                o.ToDay = dlg.To;
                o.PerDevice = dlg.PerDevice;
                List<BatchRow> rows = CustomerReportBatch.Rows(snap, filter, day1.ToUniversalTime(), day2.AddDays(1).ToUniversalTime(), now);
                bool pdf = dlg.MakePdf;
                Status("Making the report over " + rows.Count + (rows.Count == 1 ? " device\u2026" : " devices\u2026"));
                MakeReport(delegate
                {
                    // Devices of earlier days are listed without their session: read them for the items and the pages.
                    foreach (BatchRow r in rows)
                    {
                        if (r.S != null || r.Dev == null || r.Dev.Source == null || r.Dev.Kind != OverviewKinds.Agents) continue;
                        try { CustomerReportBatch.Fill(r, r.Dev.Source.Load(r.Dev.D), now); }
                        catch (Exception) { }
                    }
                    return CustomerReportBatch.Html(rows, o, now);
                }, path, pdf);
            }
        }

        // --page customerreport, with --tab 1 for the report over many devices: the dialog, for a screenshot.
        Form ShotCustomerReport()
        {
            return new CustomerReportDialog(theme, start.Tab == 1, start.Tab == 1 ? "Fabrikam" : "Contoso", "Daniel L. Baldissera", start.Select != "noedge");
        }
    }

    // --report-demo <path.html>: writes a sample report of the Autopilot demo device, one of the task sequence demo
    // (-osd) and a sample report over many devices (-batch) next to it, for a look.
    public static class CustomerReportDemo
    {
        public static int Write(string path)
        {
            DateTime now = DateTime.UtcNow;
            Engine e = Engine.ForDemo("autopilot");
            e.Apply(e.DemoTo(e.Player.Scenario.Length + 1));
            CustomerReportOptions o = new CustomerReportOptions();
            o.Customer = "Contoso";
            o.PreparedBy = "Daniel L. Baldissera";
            File.WriteAllText(path, CustomerReport.Html(e.S, e.Now, o), new UTF8Encoding(false));
            OverviewSnapshot snap = new OverviewLoader(OverviewDemo.Sources(now)).Read(now, true);
            BatchOptions b = new BatchOptions();
            b.Customer = "Fabrikam";
            b.PreparedBy = o.PreparedBy;
            b.PeriodText = "Last 7 days";
            b.FromDay = DateTime.Today.AddDays(-6);
            b.ToDay = DateTime.Today;
            b.PerDevice = true;
            OverviewFilter f = new OverviewFilter();
            f.Customer = "Fabrikam";
            List<BatchRow> rows = CustomerReportBatch.Rows(snap, f, now.AddDays(-7), now.AddDays(1), now);
            foreach (BatchRow r in rows)
                if (r.S == null && r.Dev != null && r.Dev.Source != null) CustomerReportBatch.Fill(r, r.Dev.Source.Load(r.Dev.D), now);
            string batchPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path) + "-batch.html");
            File.WriteAllText(batchPath, CustomerReportBatch.Html(rows, b, now), new UTF8Encoding(false));
            Engine ts = Engine.ForDemo("osd");
            ts.Apply(ts.DemoTo(ts.Player.Scenario.Length + 1));
            string osdPath = Path.Combine(Path.GetDirectoryName(batchPath), Path.GetFileNameWithoutExtension(path) + "-osd.html");
            File.WriteAllText(osdPath, CustomerReport.Html(ts.S, ts.Now, o), new UTF8Encoding(false));
            Console.WriteLine("Wrote " + path + ", " + osdPath + " and " + batchPath);
            return 0;
        }
    }
}
