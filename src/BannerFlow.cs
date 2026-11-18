namespace TrackVice
{
    using System;

    // Where the text and the buttons of a banner go at a given width. The buttons stay on the text's line while
    // the text keeps at least MinText beside them; otherwise they go below it, on as many rows as they need.
    public sealed class BannerFlow
    {
        public bool OneRow;
        public int TextWidth;
        // Per button: its row below the text (0 is the first) and its x from the left edge of the free width.
        public int[] Row, X;
        public int Rows;

        public static BannerFlow Arrange(int width, int[] buttons, int gap, int minText)
        {
            BannerFlow f = new BannerFlow();
            width = Math.Max(1, width);
            int n = buttons == null ? 0 : buttons.Length;
            f.Row = new int[n];
            f.X = new int[n];
            int all = 0;
            for (int i = 0; i < n; i++) all += buttons[i] + (i > 0 ? gap : 0);
            if (n == 0 || width - all - gap * 2 >= minText)
            {
                f.OneRow = true;
                f.TextWidth = n == 0 ? width : width - all - gap * 2;
                int x = width - all;
                for (int i = 0; i < n; i++) { f.X[i] = x; x += buttons[i] + gap; }
                f.Rows = 0;
                return f;
            }
            f.TextWidth = width;
            int row = 0, cx = 0;
            for (int i = 0; i < n; i++)
            {
                if (cx > 0 && cx + buttons[i] > width) { row++; cx = 0; }
                f.Row[i] = row;
                f.X[i] = cx;
                cx += buttons[i] + gap;
            }
            f.Rows = row + 1;
            return f;
        }

        // True when no button reaches past the width.
        public bool Inside(int width, int[] buttons)
        {
            for (int i = 0; i < X.Length; i++) if (X[i] < 0 || X[i] + Math.Min(buttons[i], width) > width) return false;
            return true;
        }
    }
}
