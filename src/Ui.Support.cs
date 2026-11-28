namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // A small window to write the note of a device. Notes are kept on this PC only.
    public sealed class NoteDialog : Form
    {
        readonly Label what = new Label();
        readonly TextBox box = new TextBox();
        readonly Label count = new Label();
        readonly FlatButton bSave = new FlatButton("Save", null);
        readonly FlatButton bRemove = new FlatButton("Remove the note", null);
        readonly FlatButton bCancel = new FlatButton("Cancel", null);
        public string Note;

        public NoteDialog(Theme t, string device, string note)
        {
            Text = "Note for " + device;
            Font = Fonts.Ui;
            Icon = Program.AppIcon();
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(Dpi.S(520), Dpi.S(190));
            BackColor = t.Bg;
            ForeColor = t.Text;
            Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(16));
            what.Text = "A short note about " + device + ", such as who it is for or what to check. It is kept on this PC and shows in the device lists and in Copy for ticket.";
            what.ForeColor = t.Text2;
            what.SetBounds(Dpi.S(20), Dpi.S(16), Dpi.S(480), Dpi.S(48));
            box.Text = note ?? "";
            box.MaxLength = DeviceNotes.MaxLength;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = t.Field;
            box.ForeColor = t.Text;
            box.SetBounds(Dpi.S(20), Dpi.S(70), Dpi.S(480), Dpi.S(28));
            count.ForeColor = t.Text3;
            count.Font = Fonts.Small;
            count.TextAlign = ContentAlignment.MiddleRight;
            count.SetBounds(Dpi.S(300), Dpi.S(102), Dpi.S(200), Dpi.S(18));
            box.TextChanged += delegate { Count(); };
            Count();
            int y = Dpi.S(138);
            bSave.Primary = true;
            bSave.Width = Math.Max(bSave.Width, Dpi.S(96));
            bCancel.Width = Math.Max(bCancel.Width, Dpi.S(96));
            bSave.Location = new Point(Dpi.S(500) - bSave.Width, y);
            bCancel.Location = new Point(bSave.Left - Dpi.S(8) - bCancel.Width, y);
            bRemove.Location = new Point(Dpi.S(20), y);
            bRemove.Visible = !string.IsNullOrEmpty(note);
            foreach (FlatButton b in new FlatButton[] { bSave, bRemove, bCancel }) b.ApplyTheme(t);
            bSave.Click += Safe.Wrap(delegate { Note = box.Text; DialogResult = DialogResult.OK; Close(); });
            bRemove.Click += Safe.Wrap(delegate { Note = ""; DialogResult = DialogResult.OK; Close(); });
            bCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = null;
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Note = box.Text; DialogResult = DialogResult.OK; Close(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
            };
            Controls.AddRange(new Control[] { what, box, count, bSave, bRemove, bCancel });
            HandleCreated += delegate { Draw.TitleBar(Handle, t); };
            Shown += delegate { box.Focus(); box.SelectionStart = box.Text.Length; };
        }

        void Count()
        {
            count.Text = box.Text.Length + " of " + DeviceNotes.MaxLength;
        }

        // Asks for the note of a device and keeps it. Returns true when it changed.
        public static bool Edit(IWin32Window owner, Theme t, string device)
        {
            if (string.IsNullOrEmpty(device) || DeviceNotes.Current == null) return false;
            string old = DeviceNotes.Current.Get(device);
            using (NoteDialog d = new NoteDialog(t, device, old))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                DeviceNotes.Current.Set(device, d.Note);
                return DeviceNotes.Current.Get(device) != old;
            }
        }
    }

    // The user, the device card, time left, Copy for ticket, Open in Intune and the note, on the device being watched.
    public sealed partial class MainForm
    {
        public const string NotePage = "note";
        readonly Panel supportBox = new Panel();
        FlatButton bTicket, bNote, bIntune;
        DeploymentEstimate estimate;
        DeploymentHistory history;
        // Edit, in Segoe Fluent Icons and Segoe MDL2 Assets.
        const string NoteGlyph = "\uE70F";

        void BuildSupport()
        {
            if (DeviceNotes.Current == null)
            {
                DeviceNotes.Current = start.Shot != null ? new DeviceNotes(null) : new DeviceNotes(DeviceNotes.DefaultPath);
                if (start.Shot != null) DeviceNotes.Current.Set("CONTOSO-LT0417", "Spare laptop for Finance. Hand over on Friday.");
            }
            supportBox.Height = Hero.CardHeight;
            supportBox.Padding = new Padding(0);
            supportBox.Margin = new Padding(0);
            bTicket = SupportButton("Copy for ticket", Glyphs.Copy, delegate { CopyTicket(); });
            bNote = SupportButton("Add a note", NoteGlyph, delegate { EditNote(); });
            bIntune = SupportButton("Open in Intune", Glyphs.Export, delegate { OpenInIntune(); });
            BuildDiagnosticsButton();
            Tip(bTicket, "Copy a short summary of this device for a support ticket: device, user, state, what failed and why, what it waits for, start and time taken. Secrets are masked.");
            Tip(bIntune, "Open the device's page in the Intune admin center");
            chips.Controls.Add(supportBox);
            LayoutSupport();
        }

        FlatButton SupportButton(string text, string glyph, EventHandler click)
        {
            FlatButton b = new FlatButton(text, glyph);
            b.Compact = true;
            b.Outline = true;
            b.AutoWidth();
            b.Click += Safe.Wrap(click);
            themed.Add(b);
            supportBox.Controls.Add(b);
            return b;
        }

        // On their own row under the chips the buttons stand side by side. Beside the chips they fill the height of the
        // cards in two rows: Copy for ticket and Open in Intune, then the note and Collect diagnostics. Open in Intune only
        // shows for a device that Intune knows, Collect diagnostics only for a device with the agent (Visible says false
        // for all of them while the window is not shown yet, so the flags keep what should show).
        bool supportRow, supportIntune, supportDiag;

        void LayoutSupport() { LayoutSupport(supportRow); }

        void LayoutSupport(bool row)
        {
            supportRow = row;
            bTicket.AutoWidth();
            bNote.AutoWidth();
            bIntune.AutoWidth();
            bDiagnostics.AutoWidth();
            int gap = Dpi.S(8);
            if (row)
            {
                int h = Dpi.S(32), x = 0;
                foreach (FlatButton b in new FlatButton[] { bTicket, bIntune, bDiagnostics, bNote })
                {
                    if (b == bIntune && !supportIntune) continue;
                    if (b == bDiagnostics && !supportDiag) continue;
                    b.SetBounds(x, 0, b.Width, h);
                    x += b.Width + gap;
                }
                supportBox.Size = new Size(Math.Max(1, x - gap), h);
                return;
            }
            // Two rows as high as the cards: Copy for ticket and Open in Intune, then the note and Collect diagnostics.
            int bh = (Hero.CardHeight - gap) / 2;
            int top = bTicket.Width + (supportIntune ? gap + bIntune.Width : 0);
            int bottom = bNote.Width + (supportDiag ? gap + bDiagnostics.Width : 0);
            int w = Math.Max(top, bottom);
            bTicket.SetBounds(0, 0, supportIntune ? w - gap - bIntune.Width : w, bh);
            bIntune.SetBounds(w - bIntune.Width, 0, bIntune.Width, bh);
            bNote.SetBounds(0, Hero.CardHeight - bh, supportDiag ? w - gap - bDiagnostics.Width : w, bh);
            bDiagnostics.SetBounds(w - bDiagnostics.Width, Hero.CardHeight - bh, bDiagnostics.Width, bh);
            supportBox.Size = new Size(w, Hero.CardHeight);
        }

        // Runs at the end of every bind.
        void BindSupport()
        {
            if (engine == null || bTicket == null) return;
            Session s = engine.S;
            bool any = s.Items.Count > 0 || Who.Label(s) != null;
            bTicket.Enabled = any;
            string note = DeviceNotes.Of(s.Computer);
            bNote.Text = note.Length > 0 ? "Note: " + Txt.Short(note, 18) : "Add a note";
            tips.SetToolTip(bNote, note.Length > 0 ? note + "\n\nClick to change the note. It is kept on this PC." : "A short note about this device, kept on this PC and shown in the device lists.");
            bool intune = PortalLinks.Intune(s.GetInfo(DeviceFacts.IntuneId)) != null;
            supportIntune = intune;
            bIntune.Visible = intune;
            supportDiag = DiagnosticsButtonShown();
            bDiagnostics.Visible = supportDiag;
            LayoutSupport();
            chips.PerformLayout();
            RefreshEstimate();
        }

        // The overall figure and the time left, from the same expected times, so that they agree. Also on every tick, so an
        // estimate that has passed is never shown.
        void RefreshEstimate()
        {
            if (engine == null) return;
            Session s = engine.S;
            estimate = null;
            try
            {
                Reference r = ReferenceUi.Get(ReferenceCustomer);
                if (r == null && engine.Mode == Modes.Demo) r = ReferenceUi.DemoReference();
                UpdateExpected(s, r);
                overall.Progress = OverallProgress.Fraction(s, engine.Now, expected);
                overall.Invalidate();
                estimate = DeploymentEstimate.Make(s, engine.Now, AutopilotFlow.BoxedTime(s, engine.Now), expected);
            }
            catch (Exception) { estimate = null; }
            if (overall.SetWords(HeroText.From(estimate))) chips.PerformLayout();
            tips.SetToolTip(overall, "How far the deployment has come, counting the items TrackVice has seen. Click to show everything." + (estimate != null ? "\n\nTime left: " + estimate.Basis : ""));
        }

        // Per-item times from the other devices Overview read and from the reference, made again only when those change.
        void UpdateExpected(Session s, Reference r)
        {
            OverviewSnapshot snap = overviewSnap;
            // The demo has no Overview read yet: its made-up finished devices of Contoso stand in for the earlier deployments.
            if (snap == null && engine.Mode == Modes.Demo) snap = new OverviewLoader(OverviewDemo.Sources(DateTime.UtcNow)).Read(DateTime.UtcNow, true);
            string customer = watchedCustomer ?? (engine.Mode == Modes.Demo ? "Contoso" : "");
            string stamp = s.Computer + "|" + (snap != null ? snap.ReadUtc.Ticks : 0) + "|" + (r != null ? r.MarkedUtc.Ticks : 0) + "|" + s.Kind + "|" + (s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile")) + "|" + TimeLeft.StillRuns(s);
            if (expected != null && (string)expectedFor == stamp) return;
            List<Session> others = new List<Session>();
            if (snap != null)
                foreach (OverviewDevice o in snap.Devices)
                {
                    if (o.S == null) continue;
                    if (watchedCustomer != null && o.Customer != null && !string.Equals(o.Customer, watchedCustomer, StringComparison.OrdinalIgnoreCase)) continue;
                    others.Add(o.S);
                }
            expected = ExpectedTimes.From(others, s.Computer, r, DateTime.UtcNow);
            // The finished deployments seen so far, kept on this PC, make the expected times of the whole deployment.
            bool keep = engine.Mode != Modes.Demo && start.Shot == null;
            if (history == null) history = keep ? DeploymentHistory.Load(DeploymentHistory.DefaultPath) : new DeploymentHistory();
            DeploymentHistory.Shared = history;
            if (snap != null) history.Learn(snap.Devices, DateTime.UtcNow);
            if (r != null) history.Learn(r, string.IsNullOrEmpty(r.Customer) ? customer : r.Customer);
            history.Learn(s, customer, DateTime.UtcNow);
            if (history.Dirty && keep) history.Save(DeploymentHistory.DefaultPath);
            expected.History = history.Get(customer, s.Kind, s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile"));
            expectedFor = stamp;
        }

        // The user in the header: who enrolls or signed in, else the account TrackVice runs as on this PC.
        string HeaderUser(Session s)
        {
            string who = Who.Label(s);
            if (who != null) return who;
            if (engine.Mode == Modes.Folder || engine.Mode == Modes.Agent || string.IsNullOrEmpty(s.User)) return null;
            return s.User;
        }

        // The device card at the top of the Session tab.
        void WriteSupport(RichTextBox r, Session s)
        {
            List<Pair> card = DeviceFacts.Card(s);
            string note = DeviceNotes.Of(s.Computer);
            if (card.Count == 0 && note.Length == 0) return;
            Section(r, "For support");
            foreach (Pair p in card) Field(r, p.Key, p.Value + (p.Key == "User" && s.GetInfo(DeviceFacts.UserFrom) != null ? "  (" + s.GetInfo(DeviceFacts.UserFrom) + ")" : ""), p.Key.EndsWith("ID", StringComparison.Ordinal));
            if (estimate != null) Field(r, "Time left", estimate.Text + ". " + estimate.Basis, false);
            string link = PortalLinks.Intune(s.GetInfo(DeviceFacts.IntuneId));
            if (link != null) Field(r, "Intune admin center", link, true);
            if (s.GetInfo(DeviceFacts.EntraId) != null) Plain(r, "In Entra ID, search the devices for the Entra device ID.", theme.Text3, Fonts.Small);
            if (note.Length > 0) Field(r, "Note", note, false);
        }

        void CopyTicket()
        {
            if (engine == null) return;
            Session s = engine.S;
            string text = Ticket.Text(s, engine.Now, AutopilotFlow.BoxedTime(s, engine.Now), estimate != null ? estimate.Text : null, DeviceNotes.Of(s.Computer));
            if (Menus.Copy(text)) Status("A summary of " + s.Computer + " for a ticket is on the clipboard.");
        }

        void EditNote()
        {
            if (engine == null || string.IsNullOrEmpty(engine.S.Computer)) return;
            if (NoteDialog.Edit(this, theme, engine.S.Computer))
            {
                BindSupport();
                if (tabs.Selected == TabSession) ShowSession();
                Status(DeviceNotes.Of(engine.S.Computer).Length > 0 ? "The note for " + engine.S.Computer + " is kept on this PC." : "The note for " + engine.S.Computer + " is removed.");
            }
        }

        void OpenInIntune()
        {
            string link = engine != null ? PortalLinks.Intune(engine.S.GetInfo(DeviceFacts.IntuneId)) : null;
            if (link != null) OpenLink(link);
        }

        // --page note: the note window of the device shown.
        Form ShotNote()
        {
            string device = engine != null ? engine.S.Computer : "CONTOSO-PC01";
            return new NoteDialog(theme, device, DeviceNotes.Of(device));
        }
    }

    // The user and the note in the lists of other devices, and Copy for ticket in the menu of a row.
    public sealed partial class RemoteDialog
    {
        void AddSupportColumns(string kind)
        {
            if (kind == Agents) table.Col("User", 160, delegate (RemoteDeployment d) { return d.EnrollUser; });
            table.Col("Note", 120, delegate (RemoteDeployment d) { return DeviceNotes.Of(d.Device); });
            // Room for the new columns, as far as the screen allows.
            Width = Math.Min(Width + Dpi.S(kind == Agents ? 280 : 120), Math.Max(Width, Screen.PrimaryScreen.WorkingArea.Width - Dpi.S(40)));
            table.MenuExtras = delegate (ContextMenuStrip m, RemoteDeployment d)
            {
                Menus.Separator(m);
                if (kind == Agents) Menus.Add(m, "Copy for ticket", delegate { CopyTicket(d); });
                Menus.Add(m, DeviceNotes.Of(d.Device).Length > 0 ? "Change the note…" : "Add a note…", delegate { if (NoteDialog.Edit(this, theme, d.Device)) table.Grid.Invalidate(); });
            };
        }

        static bool HasNote(RemoteDeployment d, string q)
        {
            return (d.EnrollUser != null && d.EnrollUser.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) || DeviceNotes.Of(d.Device).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void CopyTicket(RemoteDeployment d)
        {
            AgentCatalog a = Catalog as AgentCatalog;
            if (a == null) return;
            Session s = a.Load(d.Key);
            if (s == null) return;
            DateTime now = s.SavedUtc.Year > 2000 ? s.SavedUtc : DateTime.UtcNow;
            if (Menus.Copy(Ticket.Text(s, now, AutopilotFlow.BoxedTime(s, now), null, DeviceNotes.Of(s.Computer)))) status.Text = "A summary of " + d.Device + " for a ticket is on the clipboard.";
        }
    }
}
