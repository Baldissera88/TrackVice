namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;

    // The customer document of this Windows user, kept with the other settings in the registry.
    public sealed class PrefsCustomerStorage : ICustomerStorage
    {
        public string Read() { return Prefs.Get("Customers", null); }
        public void Write(string document) { Prefs.Set("Customers", document); }
    }

    public sealed class CustomerSwitch : EventArgs
    {
        public Customer From, To;
        // False when the Graph sign-in belongs to another tenant and must not be used any more.
        public bool KeepSignIn;
    }

    // The customers of this Windows user, and the one in use. The details of that customer are also written to the
    // single settings the device lists read, so a list connects to the customer that was chosen.
    public static class CustomerUi
    {
        static CustomerBook book;
        public static event EventHandler<CustomerSwitch> Switched;

        public static CustomerBook Book
        {
            get
            {
                if (book != null) return book;
                book = new CustomerBook(new PrefsCustomerStorage(), new DpapiProtector());
                try { book.Load(); } catch (Exception) { }
                try { book.Migrate(Legacy()); } catch (Exception) { }
                return book;
            }
        }

        public static string CurrentName
        {
            get { Customer c = Book.Current; return c == null ? null : c.Name; }
        }

        static Customer Legacy()
        {
            Customer c = new Customer();
            c.AgentLocation = Secret.Load("AgentStore");
            c.Tenant = Prefs.Get("GraphTenant", "");
            c.GraphClientId = Prefs.Get("GraphClientId", "");
            c.SccmServer = Prefs.Get("SccmServer", "");
            c.SccmSite = Prefs.Get("SccmSite", "");
            return c;
        }

        // Makes c the customer in use. Returns whether a Graph sign-in made before may still be used.
        public static bool Switch(Customer c)
        {
            Customer before = Book.Current == null ? null : Book.Current.Copy();
            Book.SetCurrent(c.Name);
            Book.Save();
            ToPrefs(c);
            bool keep = Customer.SameSignIn(before, c);
            bool other = before == null || !string.Equals(before.Name, c.Name, StringComparison.OrdinalIgnoreCase);
            if (other) SignIns.ClearGraph();
            if ((other || !keep) && Switched != null)
            {
                CustomerSwitch e = new CustomerSwitch();
                e.From = before;
                e.To = c.Copy();
                e.KeepSignIn = keep;
                Switched(null, e);
            }
            return keep;
        }

        // After the customers were edited: when the one in use changed or was removed, its old sign-in and lists go.
        public static void Edited(Customer before)
        {
            Customer now = Book.Current;
            if (now != null) ToPrefs(now);
            if (before == null) return;
            bool same = now != null && Same(before, now);
            if (same || Switched == null) return;
            CustomerSwitch e = new CustomerSwitch();
            e.From = before;
            e.To = now == null ? null : now.Copy();
            e.KeepSignIn = Customer.SameSignIn(before, now);
            Switched(null, e);
        }

        // Saves details that connected into the customer in use. The first one becomes Default.
        public static void Remember(Action<Customer> set)
        {
            try
            {
                Customer c = Book.Current;
                if (c == null && Book.Count == 0)
                {
                    Customer d = new Customer();
                    d.Name = CustomerBook.DefaultName;
                    c = Book.Add(d);
                    Book.SetCurrent(c.Name);
                }
                if (c == null) return;
                set(c);
                Book.Save();
            }
            catch (Exception) { }
        }

        public static bool Same(Customer a, Customer b)
        {
            return a.Name == b.Name && a.AgentLocation == b.AgentLocation && a.Tenant == b.Tenant && a.GraphClientId == b.GraphClientId
                && a.SccmServer == b.SccmServer && a.SccmSite == b.SccmSite && a.Notes == b.Notes;
        }

        static void ToPrefs(Customer c)
        {
            Prefs.Set("GraphClientId", c.GraphClientId.Length == 0 ? null : c.GraphClientId);
            Prefs.Set("GraphTenant", c.Tenant.Length == 0 ? null : c.Tenant);
            Prefs.Set("SccmServer", c.SccmServer.Length == 0 ? null : c.SccmServer);
            Prefs.Set("SccmSite", c.SccmSite.Length == 0 ? null : c.SccmSite);
            Secret.Save("AgentStore", c.AgentLocation);
        }

        // Where the agents upload to, without the SAS token.
        public static string Where(Customer c)
        {
            return CustomerRules.Where(c.AgentLocation);
        }

        public static string Summary(Customer c)
        {
            List<string> parts = new List<string>();
            parts.Add("Intune: " + (c.Tenant.Length > 0 ? c.Tenant : "the tenant of the account that signs in") + (c.GraphClientId.Length > 0 ? ", own app" : ""));
            if (c.AgentLocation.Length > 0) parts.Add("Agents: " + Where(c));
            if (c.SccmServer.Length > 0) parts.Add("SCCM: " + c.SccmServer + (c.SccmSite.Length > 0 ? " (" + c.SccmSite + ")" : ""));
            string s = string.Join("  ·  ", parts.ToArray());
            string n = c.Notes.Trim();
            if (n.Length > 0) s += "\n" + n.Replace("\r", "").Replace('\n', ' ');
            return s;
        }
    }

    // The customer in use, at the top of a device list. Click it to choose another.
    public sealed class CustomerPicker : FlatControl
    {
        const string Lead = "Customer";
        string value = "";

        public CustomerPicker()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = Dpi.S(32);
            Fit();
        }

        public string Value
        {
            get { return value; }
            set { this.value = value ?? ""; Fit(); Invalidate(); }
        }

        string Shown
        {
            get { return value.Length > 0 ? value : "None"; }
        }

        void Fit()
        {
            int w = Dpi.S(10) + TextRenderer.MeasureText(Lead, Fonts.Small).Width + Dpi.S(8) + TextRenderer.MeasureText(Shown, Fonts.UiSemibold).Width + Dpi.S(8) + Dpi.S(14) + Dpi.S(10);
            Width = Math.Min(w, Dpi.S(360));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : T.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Shapes.Fill(g, r, Dpi.S(5), down ? T.Pressed : hover ? T.Hover : T.Field);
            Shapes.Stroke(g, r, Dpi.S(5), T.FieldStroke, 1f);
            int x = Dpi.S(10);
            int lw = TextRenderer.MeasureText(Lead, Fonts.Small).Width;
            Draw.Text(g, Lead, Fonts.Small, new Rectangle(x, 0, lw + 2, Height), T.Text3, Draw.Line);
            x += lw + Dpi.S(8);
            int cw = Dpi.S(14);
            Draw.Text(g, Shown, Fonts.UiSemibold, new Rectangle(x, 0, Math.Max(0, Width - x - cw - Dpi.S(14)), Height), Enabled ? (value.Length > 0 ? T.Text : T.Text3) : T.Text3, Draw.Line);
            float cx = Width - Dpi.S(10) - cw / 2f, cy = Height / 2f;
            float s = Dpi.S(4);
            using (Pen p = new Pen(Enabled ? T.Text2 : T.Text3, Math.Max(1f, Dpi.S(1.4f))))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLines(p, new PointF[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
            }
            if (Focused && ShowFocusCues) Shapes.Stroke(g, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Dpi.S(5), T.Accent, 1.5f);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // A line of text in a page footer that follows the theme.
    public sealed class FooterNote : FlatControl
    {
        public bool IsError;
        const int MinFooter = 60, MaxFooter = 150;

        public void Say(string text, bool error)
        {
            Text = text ?? "";
            IsError = error;
            Fit();
            Invalidate();
        }

        // A long message wraps onto more lines, and the footer it sits in grows to show all of it.
        void Fit()
        {
            Panel p = Parent as Panel;
            if (p == null || Width < Dpi.S(80)) return;
            int lines = 0;
            if (Text.Length > 0)
                lines = TextRenderer.MeasureText(Text, Fonts.Ui, new Size(Width, 0), Draw.Wrap).Height;
            int want = Math.Max(Dpi.S(MinFooter), lines + p.Padding.Vertical + Dpi.S(4));
            want = Math.Min(want, Dpi.S(MaxFooter));
            if (p.Height != want) p.Height = want;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Fit();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(T.Bg);
            int need = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Fonts.Ui, new Size(Width, 0), Draw.Wrap).Height;
            Draw.Text(e.Graphics, Text, Fonts.Ui, ClientRectangle, IsError ? T.ErrFg : T.Text3, need > Fonts.Ui.Height * 3 / 2 ? Draw.Wrap : Draw.Line);
        }
    }

    // The rows of the customers page. It lays out its own rows, so the list can change while the page is open.
    sealed class CustomerRows : Panel, IThemed, IMeasured
    {
        Theme theme = Theme.Light();
        readonly List<SettingRow> rows = new List<SettingRow>();

        public CustomerRows()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetRows(List<SettingRow> list)
        {
            SuspendLayout();
            foreach (SettingRow r in rows) { Controls.Remove(r); r.Dispose(); }
            rows.Clear();
            foreach (SettingRow r in list)
            {
                rows.Add(r);
                Controls.Add(r);
                r.ApplyTheme(theme);
                Wheel(r);
            }
            ResumeLayout(false);
        }

        // The page scrolls with the wheel over any row.
        void Wheel(Control c)
        {
            c.MouseWheel += delegate (object s, MouseEventArgs e)
            {
                ScrollableControl sc = null;
                for (Control p = Parent; p != null; p = p.Parent) { ScrollableControl x = p as ScrollableControl; if (x != null && x.AutoScroll) { sc = x; break; } }
                if (sc == null) return;
                HandledMouseEventArgs h = e as HandledMouseEventArgs;
                if (h != null) h.Handled = true;
                int y = -sc.AutoScrollPosition.Y - e.Delta / 120 * Dpi.S(80);
                sc.AutoScrollPosition = new Point(0, Math.Max(0, y));
            };
            foreach (Control k in c.Controls) Wheel(k);
        }

        public int Measure(int width)
        {
            int y = 0;
            foreach (SettingRow r in rows)
            {
                int h = r.Measure(width);
                r.SetBounds(0, y, width, h);
                y += h + Dpi.S(6);
            }
            return Math.Max(0, y - Dpi.S(6));
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = t.Bg;
            foreach (SettingRow r in rows) r.ApplyTheme(t);
        }
    }

    // Saved connections per customer: add, rename, edit, remove, import and export.
    public sealed class CustomersPage : PageForm
    {
        readonly CustomerBook book;
        readonly bool live;
        readonly CustomerRows rows = new CustomerRows();
        readonly Toggle links = new Toggle();
        readonly FooterNote note = new FooterNote();

        public CustomersPage(Theme t, CustomerBook book) : base("Customers", t)
        {
            this.book = book;
            live = book == CustomerUi.Book;
            Size = new Size(Dpi.S(820), Dpi.S(700));
            note.Dock = DockStyle.Fill;
            footer.Controls.Add(note);
            note.BringToFront();
            note.ApplyTheme(t);

            FlatButton bAdd = SmallButton("Add a customer", delegate { Edit(null); });
            FlatButton bImport = SmallButton("Import", delegate { Import(); });
            FlatButton bExport = SmallButton("Export", delegate { Export(); });

            Add(new SectionTitle("Customers"));
            Add(new SettingRow(Glyphs2.Device, "Saved connections", "Each customer keeps its Intune tenant, where its agents upload to and its SCCM site. Choose one at the top of a device list to connect to it. The storage links are encrypted for your Windows account.", new ButtonStrip(bAdd)));
            Add(rows);
            Add(new SectionTitle("Key Vault"));
            Add(CustomersVaultUi.Rows(this, theme, book, live, delegate { Fill(); }, note));
            Add(new SectionTitle("Import and export"));
            Add(new SettingRow(Glyphs.Shield, "Include the storage links", "The storage links are credentials: anyone with the file can read what the agents uploaded. When this is off, the file has everything else.", links));
            Add(new SettingRow(Glyphs.Export, "Customers file", "A file with the customers, to move them to another PC. It can be locked with a password, and must be when it holds the storage links. Importing adds new customers and updates the ones with the same name.", new ButtonStrip(bImport, bExport)));
            Fill();
        }

        void Fill()
        {
            List<SettingRow> l = new List<SettingRow>();
            Customer cur = book.Current;
            foreach (Customer c in book.All)
            {
                Customer one = c;
                bool inUse = cur == c;
                List<Control> buttons = new List<Control>();
                if (!inUse) buttons.Add(SmallButton("Use", delegate { Use(one); }));
                buttons.Add(SmallButton("Edit", delegate { Edit(one); }));
                if (CustomerCodeDialog.Has(one.Name)) buttons.Add(SmallButton("Customer code\u2026", delegate { CustomerCodeDialog.Open(this, theme, one); }));
                buttons.Add(SmallButton("Remove", delegate { Remove(one); }));
                SettingRow r = new SettingRow(Glyphs2.Device, c.Name + (inUse ? "  ·  in use" : ""), CustomerUi.Summary(c), new ButtonStrip(buttons.ToArray()));
                l.Add(r);
            }
            if (l.Count == 0) l.Add(new SettingRow(Glyphs.Info, "No customers yet", "Add one for each customer you work for. A device list that connects with details you type in also saves them as the customer Default.", null));
            rows.SetRows(l);
            Relayout();
        }

        Customer Snapshot()
        {
            return book.Current == null ? null : book.Current.Copy();
        }

        void Done(Customer before, string message)
        {
            if (live)
            {
                book.Save();
                CustomerUi.Edited(before);
            }
            Fill();
            note.Say(message, false);
        }

        void Use(Customer c)
        {
            if (live) CustomerUi.Switch(c);
            else book.SetCurrent(c.Name);
            Fill();
            note.Say(c.Name + " is in use. The device lists connect to it.", false);
        }

        void Edit(Customer c)
        {
            using (CustomerEditor d = new CustomerEditor(theme, book, c))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Customer before = Snapshot();
                if (c == null)
                {
                    Customer added = book.Add(d.Result);
                    Done(before, "Added " + added.Name + ".");
                }
                else
                {
                    string old = c.Name;
                    book.Update(old, d.Result);
                    Done(before, string.Equals(old, d.Result.Name.Trim(), StringComparison.Ordinal) ? "Saved " + c.Name + "." : "Renamed " + old + " to " + c.Name + " and saved it.");
                }
            }
        }

        void Remove(Customer c)
        {
            if (MessageBox.Show(this, "Remove " + c.Name + "? Its tenant, storage link and SCCM site are forgotten on this PC.", "Remove customer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Customer before = Snapshot();
            book.Remove(c.Name);
            Done(before, "Removed " + c.Name + ".");
        }

        void Export()
        {
            if (book.Count == 0) { note.Say("There are no customers to export.", true); return; }
            bool protect = links.On || MessageBox.Show(this, "Protect the file with a password?", "Export customers", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            string password = null;
            if (protect)
            {
                password = CustomerPasswordDialog.Ask(this, theme, true);
                if (password == null) return;
            }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = protect ? "Protected customers (*.tvcustomers)|*.tvcustomers" : "Customers (*.json)|*.json";
                d.FileName = "TrackVice customers" + (protect ? CustomerFile.Extension : ".json");
                d.Title = "Export customers";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string json = book.Export(links.On);
                if (protect) File.WriteAllBytes(d.FileName, CustomerFile.Seal(json, password));
                else File.WriteAllText(d.FileName, json, new System.Text.UTF8Encoding(false));
                password = null;
                note.Say(links.On
                    ? "Exported " + book.Count + " customer" + (book.Count == 1 ? "" : "s") + " with the storage links, locked with the password. Keep the file safe."
                    : "Exported " + book.Count + " customer" + (book.Count == 1 ? "" : "s") + (protect ? ", locked with the password" : "") + ". The storage links are left out.", false);
            }
        }

        void Import()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Customers (*.json;*.tvcustomers)|*.json;*.tvcustomers|All files|*.*";
                d.Title = "Import customers";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Customer before = Snapshot();
                ImportResult r;
                try
                {
                    if (new FileInfo(d.FileName).Length > CustomerFile.MaxBytes) { note.Say("This customers file is too large.", true); return; }
                    byte[] data = File.ReadAllBytes(d.FileName);
                    string json;
                    if (CustomerFile.IsSealed(data))
                    {
                        string password = CustomerPasswordDialog.Ask(this, theme, false);
                        if (password == null) return;
                        json = CustomerFile.Open(data, password);
                        password = null;
                    }
                    else json = System.Text.Encoding.UTF8.GetString(data);
                    // The file decides where TrackVice connects with this Windows account and these sign-ins, so it is asked first.
                    ImportResult plan = book.Preview(json);
                    if (plan.Connections.Count > 0)
                    {
                        System.Text.StringBuilder q = new System.Text.StringBuilder("This file makes TrackVice connect to places it does not connect to now, with your Windows account and your sign-ins:\n\n");
                        int shown = Math.Min(plan.Connections.Count, 12);
                        for (int i = 0; i < shown; i++) q.Append(Txt.Short(plan.Connections[i], 200)).Append('\n');
                        if (plan.Connections.Count > shown) q.Append("and ").Append(plan.Connections.Count - shown).Append(" more\n");
                        q.Append("\nImport only a file from someone you trust. Import it?");
                        if (MessageBox.Show(this, q.ToString(), "Import customers", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                    }
                    r = book.Import(json);
                }
                catch (InvalidDataException ex) { note.Say(ex.Message, true); return; }
                Done(before, "Imported: " + r.Added + " new, " + r.Updated + " updated." + (r.HadLinks ? "" : " The file had no storage links.") + (r.Ignored > 0 ? " " + r.Ignored + " value" + (r.Ignored == 1 ? " was" : "s were") + " left out because " + (r.Ignored == 1 ? "it is" : "they are") + " not a valid address, tenant, client ID, server or site code." : ""));
            }
        }
    }

    // Adds or edits one customer.
    public sealed class CustomerEditor : PageForm
    {
        public Customer Result;
        readonly CustomerBook book;
        readonly Customer editing;
        readonly TextBox name, notes, tenant, client, server, site, where;
        readonly FooterNote note = new FooterNote();
        readonly OwnStorage storage;
        readonly FlatButton bGet, bCopyUpload;
        readonly TextBox upload;
        string uploadLink = "", fromStorage = "";

        public CustomerEditor(Theme t, CustomerBook book, Customer c) : base(c == null ? "Add a customer" : "Edit " + c.Name, t)
        {
            this.book = book;
            editing = c;
            Customer v = c ?? new Customer();
            Size = new Size(Dpi.S(760), Dpi.S(700));
            name = Field(v.Name, 300, false);
            notes = Field(v.Notes, 300, false);
            notes.Multiline = true;
            notes.Height = Dpi.S(64);
            notes.ScrollBars = ScrollBars.Vertical;
            tenant = Field(v.Tenant, 300, false);
            client = Field(v.GraphClientId, 300, true);
            server = Field(v.SccmServer, 300, false);
            site = Field(v.SccmSite, 70, false);
            site.CharacterCasing = CharacterCasing.Upper;
            site.MaxLength = 3;
            where = Field(v.AgentLocation, 300, true);
            where.UseSystemPasswordChar = v.AgentLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            // The saved link stays hidden while any of it is in the box: it shows again only once the box was emptied
            // or typed over, so one keystroke does not put the SAS token on the screen.
            where.TextChanged += delegate { if (where.Text.Length <= 1) where.UseSystemPasswordChar = false; };

            storage = YourStorageUi.Load();
            upload = Field("", 300, true);
            bGet = SmallButton("Get it", delegate { Get(); });
            bCopyUpload = SmallButton("Copy", delegate { note.Say(Menus.Copy(uploadLink) ? "Copied the upload link." : "The upload link could not be copied.", false); });
            bCopyUpload.Glyph = Glyphs.Copy;
            bCopyUpload.AutoWidth();
            bCopyUpload.Width += Dpi.S(8);
            upload.ReadOnly = true;
            upload.UseSystemPasswordChar = true;
            Rows();

            bClose.Text = "Cancel";
            bClose.Primary = false;
            bClose.ApplyTheme(t);
            FlatButton bSave = new FlatButton("Save", Glyphs.Save);
            bSave.Primary = true;
            bSave.Width = Math.Max(bSave.Width, Dpi.S(96));
            bSave.Dock = DockStyle.Right;
            bSave.Click += Safe.Wrap(delegate { Save(); });
            bSave.ApplyTheme(t);
            Panel gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = Dpi.S(8);
            note.Dock = DockStyle.Fill;
            note.ApplyTheme(t);
            footer.Controls.Add(gap);
            footer.Controls.Add(bSave);
            footer.Controls.Add(note);
            note.BringToFront();
            AcceptButton = null;
            Shown += delegate { name.Focus(); name.SelectAll(); };
        }

        void Rows()
        {
            ClearItems();
            Add(new SectionTitle("Customer"));
            Add(new SettingRow(Glyphs2.Device, "Name", "Shown at the top of the device lists and in the status bar.", name));
            Add(new SettingRow(Glyphs.Info, "Notes", "For yourself. They are exported with the customer.", notes));
            Add(new SectionTitle("Autopilot in Intune"));
            Add(new SettingRow(Glyphs.Shield, "Tenant", "The tenant ID or domain, for example contoso.onmicrosoft.com. When it is empty, the sign-in goes to the tenant of the account you use, and is never kept when you change customer.", tenant));
            Add(new SettingRow(Glyphs2.Code, "Client ID", "The app registration to sign in with. Leave it empty to use Microsoft Graph Command Line Tools.", client));
            Add(new SectionTitle("TrackVice agent"));
            Add(new SettingRow(Glyphs2.Folder, "Upload location", "The SAS link of the storage container with Read and List, or a folder. Encrypted for your Windows account.", where));
            if (CustomerStorageLink.Offered(storage))
            {
                Add(new SettingRow(Glyphs2.Sync, "Get it from Your storage", fromStorage.Length > 0 ? fromStorage : "Makes the private container for this customer in " + storage.Account + " and puts the viewer link above. A container that is there is used.", bGet));
                if (uploadLink.Length > 0)
                {
                    upload.Text = uploadLink;
                    ButtonStrip s = new ButtonStrip(upload, bCopyUpload);
                    Add(new SettingRow(Glyphs2.Folder, "Upload link for devices", "This is what goes in the customer code. It is not saved here: the customer code is made in Add customer.", s));
                }
            }
            Add(new SectionTitle("SCCM"));
            Add(new SettingRow(Glyphs2.Apps, "SMS Provider", "The server that runs the SMS Provider, usually the site server.", server));
            Add(new SettingRow(Glyphs2.Settings, "Site code", "Optional. Read from the server when it is empty.", site));

        }

        void Get()
        {
            string problem = CustomerStorageLink.Problem(book, name.Text, editing);
            if (problem != null)
            {
                note.Say(problem, true);
                if (name.Text.Trim().Length == 0) name.Focus();
                return;
            }
            note.Say("", false);
            CustomerLinkDialog.Open(this, theme, name.Text.Trim(), Got);
        }

        // Puts the viewer link in the box and shows the upload link. The upload link is not kept.
        public void Got(CustomerStorageResult r)
        {
            where.Text = r.Viewer;
            where.UseSystemPasswordChar = true;
            uploadLink = r.Upload;
            fromStorage = "The viewer link of " + r.Container + " is in the box above. Save the customer to keep it.";
            Rows();
            Relayout();
        }

        TextBox Field(string value, int width, bool mono)
        {
            TextBox b = new TextBox();
            b.Width = Dpi.S(width);
            b.Font = mono ? Fonts.Mono : Fonts.Ui;
            b.BorderStyle = BorderStyle.FixedSingle;
            b.BackColor = theme.Field;
            b.ForeColor = theme.Text;
            b.Text = value ?? "";
            return b;
        }

        void Save()
        {
            string problem = book.NameProblem(name.Text, editing);
            string id = client.Text.Trim();
            if (problem == null && id.Length > 0 && !GraphClient.IsGuid(id)) problem = "The client ID is a GUID, such as 3c8f2a51-7d4b-4e19-a6c2-91b0f5e7d834.";
            if (problem != null)
            {
                note.Say(problem, true);
                return;
            }
            Customer c = new Customer();
            c.Name = name.Text.Trim();
            c.Notes = notes.Text;
            c.Tenant = tenant.Text.Trim();
            c.GraphClientId = id;
            c.SccmServer = server.Text.Trim();
            c.SccmSite = site.Text.Trim();
            c.AgentLocation = where.Text.Trim();
            Result = c;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
