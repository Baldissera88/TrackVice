namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    public sealed partial class MainForm
    {
        FlatButton bMore;
        FlatButton[] headerButtons;
        BrandMark brand;
        Crumb crumb;
        FactChips facts;
        SearchField searchField;
        // The buttons that fold into More, the first to go first. Back, Forward, Overview, Settings and Help stay.
        FlatButton[] foldOrder;
        bool fitting;

        // The buttons that do not fit in the header go into a More menu, so the title is never covered.
        void BuildHeaderFold()
        {
            bMore = Button("More", null, delegate { MoreMenu().Show(bMore, new Point(bMore.Width, bMore.Height), ToolStripDropDownDirection.BelowLeft); });
            bMore.Visible = false;
            bMore.Margin = new Padding(Dpi.S(2), 0, 0, 0);
            Tip(bMore, "More: the buttons that do not fit in the window");
            toolbar.Controls.Add(bMore);
            toolbar.Controls.SetChildIndex(bMore, toolbar.Controls.GetChildIndex(bSettings));
            headerButtons = new FlatButton[] { bBack, bForward, bHome, bWatch, bOpen, bDevices, bSave, bExport, bLogVice, bCompact, bTheme, bMore, bSettings, bAbout };
            foldOrder = new FlatButton[] { bCompact, bLogVice, bExport, bSave, bTheme, bOpen, bWatch, bDevices };
            header.Layout += delegate { FitHeader(); };
            foreach (FlatButton b in headerButtons) b.SizeChanged += delegate { FitHeader(); };
            chips.Layout += delegate { FitChips(); };
            Layout += delegate { FitStack(); };
            Shown += delegate { FitStack(); FitChips(); FitHeader(); };
            // The subtitle keeps the text, for the window and for reading; the crumb and the chips show it.
            subtitle.AutoSize = false;
            subtitle.AutoEllipsis = true;
            subtitle.Height = Fonts.Small.Height + Dpi.S(4);
            subtitle.Visible = false;
            BuildHeaderParts();
            FitHeader();
        }

        // The mark, the crumb, the chips with the facts, the search field, and quiet buttons.
        void BuildHeaderParts()
        {
            foreach (FlatButton b in headerButtons)
            {
                b.Quiet = true;
                b.Margin = new Padding(Dpi.S(1), 0, 0, 0);
            }
            bHome.Margin = new Padding(Dpi.S(1), 0, Dpi.S(8), 0);
            bTheme.Margin = new Padding(Dpi.S(8), 0, 0, 0);
            brand = new BrandMark();
            crumb = new Crumb();
            facts = new FactChips();
            searchField = new SearchField();
            searchField.Click += Safe.Wrap(delegate { OpenPalette(""); });
            Tip(searchField, "Search devices, actions, pages and settings, and jump to them (Ctrl+K or Ctrl+P)");
            foreach (Control c in new Control[] { brand, crumb, facts, searchField })
            {
                header.Controls.Add(c);
                themed.Add((IThemed)c);
            }
            // The buttons keep to row one, so they never lie over the row below.
            toolbar.MaximumSize = new Size(0, Dpi.S(44));
            toolbar.SizeChanged += delegate { PlaceHeader(); };
            header.Resize += delegate { PlaceHeader(); FitHeader(); };
            header.Paint += delegate (object o, PaintEventArgs e)
            {
                using (Pen p = new Pen(theme != null ? theme.Divider : Color.Gray, 1f)) e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            BuildPalette();
            PlaceHeader();
        }

        // Row one: the mark, the title, and the buttons at the right. Row two: where you are, the facts as chips, and
        // the search field at the right.
        void PlaceHeader()
        {
            if (brand == null || header.ClientSize.Width <= 0) return;
            int w = header.ClientSize.Width, edge = Dpi.S(18);
            brand.Location = new Point(edge, Dpi.S(10));
            title.Location = new Point(brand.Right + Dpi.S(9), brand.Top + (brand.Height - title.Height) / 2 - Dpi.S(1));
            toolbar.Location = new Point(Math.Max(0, w - toolbar.Width), 0);
            int rowTwo = Dpi.S(46), rowH = Dpi.S(28);
            int sw = Math.Max(Dpi.S(96), Math.Min(SearchField.NaturalWidth, (int)(w * 0.3)));
            searchField.SetBounds(w - edge - sw, rowTwo, sw, rowH);
            int room = Math.Max(0, searchField.Left - edge - Dpi.S(16));
            int cw = Math.Min(crumb.PreferredWidth, room);
            crumb.SetBounds(edge + Dpi.S(2), rowTwo + (rowH - crumb.Height) / 2, cw, crumb.Height);
            int fx = crumb.Right + Dpi.S(14);
            int fw = edge + room - fx;
            facts.SetBounds(fx, rowTwo + (rowH - facts.Height) / 2, Math.Max(0, fw), facts.Height);
            facts.Visible = facts.Items.Length > 0 && fw >= Dpi.S(48);
        }

        // A device is open: its customer or source, its name, and the facts.
        void ShowHeaderDevice(Session s)
        {
            if (crumb == null || engine == null) return;
            string mode = null;
            if (engine.Mode == Modes.Demo) mode = "Demo data";
            else if (engine.Mode == Modes.Intune) mode = "Intune";
            else if (engine.Mode == Modes.Sccm) mode = "SCCM";
            else if (engine.Mode == Modes.Agent) mode = AgentVersions.Header(s.AppVersion);
            else if (engine.Mode == Modes.File) mode = "Saved session";
            else if (engine.Mode == Modes.Folder) mode = "Log folder";
            List<string> l = new List<string>();
            if (watchedCustomer != null && mode != null) l.Add(mode);
            if (s.Items.Count > 0) l.Add(s.Kind);
            string profile = s.GetInfo("Task sequence") ?? s.GetInfo("Autopilot profile");
            if (profile != null) l.Add(profile);
            if (HeaderUser(s) != null) l.Add(HeaderUser(s));
            if (!string.IsNullOrEmpty(s.OsVersion)) l.Add(s.OsVersion);
            DateTime? f = s.FirstUtc;
            if (f.HasValue) l.Add("started " + Txt.Local(f.Value));
            crumb.ShowDevice(watchedCustomer ?? mode ?? "This PC", s.Computer);
            facts.Items = l.ToArray();
            Tip(facts, subtitle.Text);
            PlaceHeader();
        }

        // A page such as Overview is open: its title, and no facts.
        void ShowHeaderPage(string title)
        {
            if (crumb == null) return;
            crumb.ShowPage(title);
            facts.Items = new string[0];
            PlaceHeader();
        }

        static int Taken(FlatButton b)
        {
            return b.Width + b.Margin.Horizontal;
        }

        void FitHeader()
        {
            if (fitting || bMore == null || header.ClientSize.Width <= 0) return;
            fitting = true;
            try
            {
                int titleW = TextRenderer.MeasureText(title.Text, title.Font).Width;
                int room = header.ClientSize.Width - title.Left - titleW - Dpi.S(28) - toolbar.Padding.Horizontal;
                List<FlatButton> shown = new List<FlatButton>();
                foreach (FlatButton b in headerButtons) if (b != bMore) shown.Add(b);
                int[] widths = new int[shown.Count];
                for (int i = 0; i < widths.Length; i++) widths[i] = Taken(shown[i]);
                int[] order = new int[foldOrder.Length];
                for (int i = 0; i < order.Length; i++) order[i] = shown.IndexOf(foldOrder[i]);
                bool[] hidden = NarrowFit.Hidden(widths, order, Taken(bMore), room);
                bool any = false;
                foreach (bool h in hidden) any |= h;
                toolbar.SuspendLayout();
                bMore.Visible = any;
                for (int i = 0; i < shown.Count; i++) shown[i].Visible = !hidden[i];
                toolbar.ResumeLayout(true);
                // A folded button keeps the place of More, so the menu it opens drops from there. Its size stays,
                // so it has its own width again when it comes back.
                foreach (FlatButton b in shown) if (!b.Visible) b.Location = bMore.Location;
                subtitle.Width = Math.Max(Dpi.S(40), header.ClientSize.Width - subtitle.Left - Dpi.S(16));
            }
            finally { fitting = false; }
            PlaceHeader();
        }

        // ---------- the status bar ----------

        void BuildStatusBar()
        {
            statusBar.Resize += delegate { FitStatus(); };
            statusBar.Paint += delegate (object o, PaintEventArgs e)
            {
                using (Pen p = new Pen(theme != null ? theme.Divider : Color.Gray, 1f)) e.Graphics.DrawLine(p, 0, 0, statusBar.Width, 0);
            };
        }

        // The right side takes what it needs, up to 60 % of the bar; what does not fit goes, the least important first.
        void FitStatus()
        {
            int w = statusRight.Fit(Math.Max(Dpi.S(80), (int)(statusBar.ClientSize.Width * 0.6)));
            if (statusRight.Width != w + Dpi.S(2)) statusRight.Width = w + Dpi.S(2);
            statusRight.Invalidate();
        }

        int chipsHeight = Dpi.S(74);

        // No chip is left out. They shrink to their minimum, then wrap into more rows, the support buttons last.
        void FitChips()
        {
            if (fitting || cItems == null || bTicket == null || chips.ClientSize.Width <= 0) return;
            fitting = true;
            try
            {
                StatChip[] stats = new StatChip[] { cItems, cActive, cWaiting, cFailed, cFindings };
                Control[] all = new Control[] { overall, cItems, cActive, cWaiting, cFailed, cFindings, supportBox };
                int n = all.Length - 1;
                int[] nat = new int[n], min = new int[n];
                nat[0] = overall.NaturalWidth + overall.Margin.Horizontal;
                min[0] = overall.MinWidth + overall.Margin.Horizontal;
                for (int i = 1; i < n; i++)
                {
                    nat[i] = stats[i - 1].NaturalWidth + all[i].Margin.Horizontal;
                    min[i] = stats[i - 1].MinWidth + all[i].Margin.Horizontal;
                }
                supportBox.Margin = new Padding(Dpi.S(4), 0, 0, 0);
                LayoutSupport(false);
                int room = chips.ClientSize.Width - chips.Padding.Horizontal;
                NarrowFit.ChipPlan plan = NarrowFit.Chips(nat, min, supportBox.Width + supportBox.Margin.Horizontal, room);
                bool own = plan.Rows[n] > plan.Rows[n - 1];
                chips.SuspendLayout();
                if (own)
                {
                    supportBox.Margin = new Padding(0);
                    LayoutSupport(true);
                    supportBox.Width = Math.Min(supportBox.Width, Math.Max(room, 0));
                }
                chips.WrapContents = plan.RowCount > 1;
                for (int i = 0; i < n; i++)
                {
                    all[i].Visible = true;
                    all[i].Width = Math.Max(1, plan.Widths[i] - all[i].Margin.Horizontal);
                    chips.SetFlowBreak(all[i], plan.Rows[i + 1] != plan.Rows[i]);
                }
                supportBox.Visible = true;
                chips.SetFlowBreak(supportBox, false);
                chipsHeight = chips.Padding.Vertical + (plan.Rows[n - 1] + 1) * Dpi.S(62) + (own ? supportBox.Height : 0);
                chips.ResumeLayout(true);
            }
            finally { fitting = false; }
            FitStack();
        }

        // In a low window the phases and then the chips make room for the list.
        void FitStack()
        {
            int h = ClientSize.Height;
            rail.Height = h < Dpi.S(514) ? 0 : Dpi.S(84);
            int ch = h < Dpi.S(440) ? 0 : chipsHeight;
            if (chips.Height != ch) chips.Height = ch;
        }

        ContextMenuStrip MoreMenu()
        {
            ContextMenuStrip m = Menus.New(theme);
            foreach (FlatButton b in foldOrder)
            {
                if (b.Visible) continue;
                FlatButton button = b;
                string text = string.IsNullOrEmpty(b.Text) ? ToolTipOf(b) : b.Text;
                Menus.Add(m, text, delegate { button.PerformClick(); }).Checked = b.Checked;
            }
            return m;
        }

        // Theme has an icon and no text in the header.
        string ToolTipOf(FlatButton b)
        {
            return b == bTheme ? "Theme" : "";
        }
    }
}
