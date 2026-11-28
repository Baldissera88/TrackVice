namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;

    // Painting for the Timeline and Findings grids: severity dots, source chips, day and hour separators, finding cards.
    // The data and the columns stay as the Table defines them; only the cells are drawn differently.
    public static class SecondaryViews
    {
        const TextFormatFlags Line = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

        static Color SevColor(int sev, Theme t)
        {
            return sev >= 2 ? t.ErrFg : sev == 1 ? t.WarnFg : t.Text3;
        }

        static Color SourceColor(string source, Theme t)
        {
            Color[] cs = new Color[] { t.Accent, t.OkFg, t.WarnFg, t.Text2 };
            int h = 0;
            foreach (char ch in source ?? "") h = h * 31 + ch;
            return cs[(h & 0x7fffffff) % cs.Length];
        }

        public static void AttachTimeline(Table<TEvent> table, Func<Theme> theme)
        {
            DataGridView g = table.Grid;
            g.Columns[0].DefaultCellStyle.Padding = new Padding(Dpi.S(20), 0, 0, 0);
            g.CellPainting += delegate (object s, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= table.Rows.Count || e.ColumnIndex < 0) return;
                Theme t = theme();
                TEvent ev = table.Rows[e.RowIndex];
                Rectangle b = e.CellBounds;
                if (e.ColumnIndex == 1)
                {
                    e.Paint(b, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
                    string src = ev.Source ?? "";
                    if (src.Length > 0)
                    {
                        Color c = SourceColor(src, t);
                        Size ts = TextRenderer.MeasureText(src, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding);
                        int w = Math.Min(ts.Width + Dpi.S(14), b.Width - Dpi.S(8)), h = Dpi.S(20);
                        Rectangle r = new Rectangle(b.X + Dpi.S(4), b.Y + (b.Height - h) / 2, w, h);
                        SmoothingMode old = e.Graphics.SmoothingMode;
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        Shapes.Fill(e.Graphics, r, h / 2f, Theme.Mix(t.Card, c, t.IsDark ? 0.18f : 0.10f));
                        e.Graphics.SmoothingMode = old;
                        TextRenderer.DrawText(e.Graphics, src, Fonts.Small, new Rectangle(r.X + Dpi.S(7), r.Y, r.Width - Dpi.S(10), r.Height), Theme.Mix(c, t.Text, 0.35f), Line);
                    }
                }
                else e.Paint(b, e.PaintParts);
                if (e.ColumnIndex == 0)
                {
                    int d = Dpi.S(8);
                    SmoothingMode old = e.Graphics.SmoothingMode;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush br = new SolidBrush(SevColor(ev.Severity, t)))
                        e.Graphics.FillEllipse(br, b.X + Dpi.S(8), b.Y + (b.Height - d) / 2f, d, d);
                    e.Graphics.SmoothingMode = old;
                }
                // The line above a row: a day change is the strongest, an hour change a little softer.
                if (e.RowIndex > 0)
                {
                    DateTime a = table.Rows[e.RowIndex - 1].TimeUtc.ToLocalTime(), c2 = ev.TimeUtc.ToLocalTime();
                    if (a.Date != c2.Date || a.Hour != c2.Hour)
                    {
                        bool day = a.Date != c2.Date;
                        using (Pen p = new Pen(day ? t.Accent : Theme.Mix(t.Divider, t.Text3, 0.5f), day ? 2f : 1f))
                            e.Graphics.DrawLine(p, b.X, b.Y, b.Right, b.Y);
                    }
                }
                e.Handled = true;
            };
        }

        // Findings as cards: one visible column, taller rows, the headers hidden.
        public static void AttachFindings(Table<Finding> table, Func<Theme> theme)
        {
            DataGridView g = table.Grid;
            g.ColumnHeadersVisible = false;
            g.RowTemplate.Height = Dpi.S(92);
            g.CellBorderStyle = DataGridViewCellBorderStyle.None;
            foreach (DataGridViewColumn c in g.Columns) if (c.HeaderText != "Finding") c.Visible = false;
            g.CellPainting += delegate (object s, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= table.Rows.Count || e.ColumnIndex < 0) return;
                Theme t = theme();
                Finding f = table.Rows[e.RowIndex];
                Graphics gr = e.Graphics;
                bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
                Rectangle b = e.CellBounds;
                using (SolidBrush bg = new SolidBrush(t.Card)) gr.FillRectangle(bg, b);
                RectangleF card = new RectangleF(b.X + Dpi.S(8), b.Y + Dpi.S(4), b.Width - Dpi.S(16), b.Height - Dpi.S(8));
                SmoothingMode old = gr.SmoothingMode;
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                Color sc = SevColor(f.Severity, t);
                Shapes.Fill(gr, card, Dpi.S(10), sel ? t.AccentSoft : t.CardAlt);
                Shapes.Stroke(gr, card, Dpi.S(10), sel ? t.Accent : t.Divider, 1f);
                int d = Dpi.S(10);
                int x = (int)card.X + Dpi.S(16), y = (int)card.Y + Dpi.S(14);
                using (SolidBrush br = new SolidBrush(sc)) gr.FillEllipse(br, x, y + Dpi.S(6), d, d);
                gr.SmoothingMode = old;
                int tx = x + d + Dpi.S(12), right = (int)card.Right - Dpi.S(16);
                string meta = f.Kind + "  ·  " + Txt.LocalTime(f.TimeUtc);
                int mw = Math.Min(Dpi.S(260), TextRenderer.MeasureText(meta, Fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width + 4);
                TextRenderer.DrawText(gr, meta, Fonts.Small, new Rectangle(right - mw, y, mw, Dpi.S(22)), t.Text3, Line | TextFormatFlags.Right);
                int sw = TextRenderer.MeasureText(f.SeverityName, Fonts.SmallSemibold, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(gr, f.SeverityName, Fonts.SmallSemibold, new Rectangle(tx, y, sw + 4, Dpi.S(22)), sc, Line);
                TextRenderer.DrawText(gr, f.Title, Fonts.UiSemibold, new Rectangle(tx + sw + Dpi.S(10), y, Math.Max(10, right - mw - Dpi.S(10) - tx - sw - Dpi.S(10)), Dpi.S(22)), t.Text, Line);
                int ty = y + Dpi.S(26);
                if (!string.IsNullOrEmpty(f.Message))
                    TextRenderer.DrawText(gr, f.Message, Fonts.Small, new Rectangle(tx, ty, right - tx, Dpi.S(18)), t.Text2, Line);
                if (!string.IsNullOrEmpty(f.Hint))
                {
                    // What to do about it, marked with a short bar in the accent.
                    int hy = ty + Dpi.S(21);
                    using (SolidBrush br = new SolidBrush(t.Accent)) gr.FillRectangle(br, tx, hy + Dpi.S(3), Dpi.S(2), Dpi.S(12));
                    TextRenderer.DrawText(gr, f.Hint, Fonts.Small, new Rectangle(tx + Dpi.S(9), hy, right - tx - Dpi.S(9), Dpi.S(18)), t.Text, Line);
                }
                e.Handled = true;
            };
        }
    }
}
