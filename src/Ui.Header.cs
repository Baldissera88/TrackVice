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
            subtitle.AutoSize = false;
            subtitle.AutoEllipsis = true;
            subtitle.Height = Fonts.Small.Height + Dpi.S(4);
            FitHeader();
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
