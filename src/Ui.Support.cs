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
        readonly FlowLayoutPanel supportBox = new FlowLayoutPanel();
        FlatButton bTicket, bNote, bIntune;
        TimeLeftEstimate timeLeft;

        void BuildSupport()
        {
            if (DeviceNotes.Current == null)
            {
                DeviceNotes.Current = start.Shot != null ? new DeviceNotes(null) : new DeviceNotes(DeviceNotes.DefaultPath);
                if (start.Shot != null) DeviceNotes.Current.Set("CONTOSO-LT0417", "Spare laptop for Finance. Hand over on Friday.");
            }
            supportBox.FlowDirection = FlowDirection.TopDown;
            supportBox.WrapContents = true;
            supportBox.Height = Dpi.S(62);
            supportBox.Padding = new Padding(0);
            supportBox.Margin = new Padding(Dpi.S(4), 0, 0, 0);
            bTicket = SupportButton("Copy for ticket", Glyphs.Copy, delegate { CopyTicket(); });
            bNote = SupportButton("Add a note", null, delegate { EditNote(); });
            bIntune = SupportButton("Open in Intune", Glyphs.Export, delegate { OpenInIntune(); });
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
            b.Height = Dpi.S(27);
            b.AutoWidth();
            b.Margin = new Padding(0, 0, Dpi.S(6), Dpi.S(4));
            b.Click += Safe.Wrap(click);
            themed.Add(b);
            supportBox.Controls.Add(b);
            return b;
        }

        // On its own row in the chips the buttons stand side by side; next to the chips they stand in two lines.
        bool supportRow;

        void LayoutSupport() { LayoutSupport(supportRow); }

        void LayoutSupport(bool row)
        {
            supportRow = row;
            bTicket.AutoWidth();
            bNote.AutoWidth();
            bIntune.AutoWidth();
            supportBox.FlowDirection = row ? FlowDirection.LeftToRight : FlowDirection.TopDown;
            supportBox.WrapContents = !row;
            supportBox.Height = row ? bTicket.Height + Dpi.S(4) : Dpi.S(62);
            if (row)
            {
                supportBox.Width = bTicket.Width + bNote.Width + Dpi.S(12) + (bIntune.Visible ? bIntune.Width + Dpi.S(6) : 0);
                return;
            }
            int col1 = Math.Max(bTicket.Width, bNote.Width);
            bTicket.Width = col1;
            bNote.Width = col1;
            supportBox.Width = col1 + Dpi.S(6) + (bIntune.Visible ? bIntune.Width + Dpi.S(6) : 0);
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
            if (bIntune.Visible != intune) bIntune.Visible = intune;
            LayoutSupport();
            chips.PerformLayout();
            timeLeft = null;
            try
            {
                Reference r = ReferenceUi.Get(ReferenceCustomer);
                if (r == null && engine.Mode == Modes.Demo) r = ReferenceUi.DemoReference();
                UpdateExpected(s, r);
                overall.Progress = OverallProgress.Fraction(s, engine.Now, expected);
                overall.Invalidate();
                timeLeft = OverallProgress.Adjust(TimeLeft.Estimate(s, engine.Now, AutopilotFlow.BoxedTime(s, engine.Now), r, RecentRuns()), s, engine.Now, expected);
            }
            catch (Exception) { timeLeft = null; }
            string left = timeLeft != null ? timeLeft.Text : "";
            if (overall.Remaining != left)
            {
                overall.Remaining = left;
                overall.Invalidate();
                chips.PerformLayout();
            }
            tips.SetToolTip(overall, "How far the deployment has come, counting the items TrackVice has seen. Click to show everything." + (timeLeft != null ? "\n\nTime left: " + timeLeft.Basis : ""));
        }

        // Per-item times from the other devices Overview read and from the reference, made again only when those change.
        void UpdateExpected(Session s, Reference r)
        {
            OverviewSnapshot snap = overviewSnap;
            string stamp = s.Computer + "|" + (snap != null ? snap.ReadUtc.Ticks : 0) + "|" + (r != null ? r.MarkedUtc.Ticks : 0);
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
            expectedFor = stamp;
        }

        // The enrollments Overview read for the last 7 days, of the customer of this device when it has one.
        List<FleetRun> RecentRuns()
        {
            List<FleetRun> l = new List<FleetRun>();
            OverviewSnapshot snap = overviewSnap;
            if (snap == null) return l;
            DateTime now = DateTime.UtcNow;
            foreach (OverviewDevice o in snap.Devices)
            {
                if (o.S == null) continue;
                if (watchedCustomer != null && o.Customer != null && !string.Equals(o.Customer, watchedCustomer, StringComparison.OrdinalIgnoreCase)) continue;
                l.Add(FleetStats.RunOf(o.S, now));
            }
            return l;
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
            if (timeLeft != null) Field(r, "Time left", timeLeft.Text + ". " + timeLeft.Basis, false);
            string link = PortalLinks.Intune(s.GetInfo(DeviceFacts.IntuneId));
            if (link != null) Field(r, "Intune admin center", link, true);
            if (s.GetInfo(DeviceFacts.EntraId) != null) Plain(r, "In Entra ID, search the devices for the Entra device ID.", theme.Text3, Fonts.Small);
            if (note.Length > 0) Field(r, "Note", note, false);
        }

        void CopyTicket()
        {
            if (engine == null) return;
            Session s = engine.S;
            string text = Ticket.Text(s, engine.Now, AutopilotFlow.BoxedTime(s, engine.Now), timeLeft != null ? timeLeft.Text : null, DeviceNotes.Of(s.Computer));
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
