namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

    // Text that is cut with an ellipsis, measured as it is drawn.
    public static class Fit
    {
        public static string Text(string text, Font f, int room)
        {
            return RowFit.Ellipsis(text, room, delegate (string s) { return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width; });
        }
    }

    public sealed partial class MainForm
    {
        // The buttons under the detail: Copy details on the left, the others on the right. When they do not fit in a row
        // they wrap into more rows, so none is cut.
        void BuildDetailFoot(Panel foot)
        {
            foot.Controls.Clear();
            FlatButton[] all = new FlatButton[] { bItemCopy, bReport, bItemLog };
            foreach (FlatButton b in all)
            {
                b.Dock = DockStyle.None;
                foot.Controls.Add(b);
                b.VisibleChanged += delegate { LayoutDetailFoot(foot, all); };
                b.SizeChanged += delegate { LayoutDetailFoot(foot, all); };
            }
            foot.Layout += delegate { LayoutDetailFoot(foot, all); };
        }

        bool footBusy;

        void LayoutDetailFoot(Panel foot, FlatButton[] all)
        {
            if (footBusy) return;
            footBusy = true;
            try
            {
                List<FlatButton> shown = new List<FlatButton>();
                foreach (FlatButton b in all) if (b.Visible) shown.Add(b);
                int gap = Dpi.S(8), bh = Dpi.S(32);
                int room = foot.ClientSize.Width - foot.Padding.Horizontal;
                int[] w = new int[shown.Count];
                for (int i = 0; i < w.Length; i++) w[i] = shown[i].Width;
                int[] rows = RowFit.Rows(w, gap, room);
                int n = Math.Max(1, RowFit.RowCount(rows));
                int height = foot.Padding.Vertical + n * bh + (n - 1) * gap;
                if (foot.Height != height) foot.Height = height;
                int x = foot.Padding.Left, last = -1;
                for (int i = 0; i < shown.Count; i++)
                {
                    if (rows[i] != last) { x = foot.Padding.Left; last = rows[i]; }
                    shown[i].Location = new Point(x, foot.Padding.Top + rows[i] * (bh + gap));
                    x += shown[i].Width + gap;
                }
                // On one row the buttons after Copy details sit at the right edge.
                if (n == 1 && shown.Count > 1)
                {
                    int rx = foot.ClientSize.Width - foot.Padding.Right;
                    for (int i = shown.Count - 1; i >= 1; i--)
                    {
                        rx -= shown[i].Width;
                        shown[i].Left = rx;
                        rx -= gap;
                    }
                }
            }
            finally { footBusy = false; }
        }
    }
}
