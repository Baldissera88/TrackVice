namespace TrackVice
{
    using System;

    // Layout rules for rows of buttons and for titles that do not have the room they want.
    public static class RowFit
    {
        // The row of each item when items of these widths are placed left to right with a gap between them and a new row
        // starts where the next one would pass the room. An item wider than the room has a row of its own.
        public static int[] Rows(int[] widths, int gap, int room)
        {
            int[] rows = new int[widths.Length];
            int row = 0, x = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                if (x > 0 && x + gap + widths[i] > room)
                {
                    row++;
                    x = 0;
                }
                x += (x > 0 ? gap : 0) + widths[i];
                rows[i] = row;
            }
            return rows;
        }

        public static int RowCount(int[] rows)
        {
            int n = 0;
            foreach (int r in rows) if (r + 1 > n) n = r + 1;
            return n;
        }

        // The text cut with "..." so that it is not wider than the room. measure gives the width of a text.
        public static string Ellipsis(string text, int room, Func<string, int> measure)
        {
            if (string.IsNullOrEmpty(text) || measure(text) <= room) return text;
            const string dots = "…";
            if (measure(dots) > room) return "";
            int lo = 0, hi = text.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (measure(text.Substring(0, mid).TrimEnd() + dots) <= room) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + dots;
        }
    }
}
