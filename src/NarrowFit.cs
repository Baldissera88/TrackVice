namespace TrackVice
{
    using System;

    // Decides which items of a row do not fit and fold away, for the header buttons and the chips of the main window.
    public static class NarrowFit
    {
        // widths: every item with its margins. foldOrder: the indexes of the items that may fold, the first to go first.
        // moreWidth: the room the More button takes once anything is folded (0 when there is none). Items that are not in
        // foldOrder stay. Returns, for each item, whether it is folded.
        public static bool[] Hidden(int[] widths, int[] foldOrder, int moreWidth, int room)
        {
            bool[] hidden = new bool[widths.Length];
            int total = 0;
            foreach (int w in widths) total += w;
            if (total <= room) return hidden;
            total += moreWidth;
            foreach (int i in foldOrder)
            {
                if (total <= room) break;
                if (i < 0 || i >= widths.Length || hidden[i]) continue;
                hidden[i] = true;
                total -= widths[i];
            }
            return hidden;
        }

        // A size that does not go past the room there is, such as a dialog on a small or a scaled screen.
        public static int Capped(int size, int room)
        {
            return room > 0 && size > room ? room : size;
        }

        // Where a window of this size starts so that it stays inside the room from min to min + room, as near to want as it can.
        public static int Inside(int want, int size, int min, int room)
        {
            int max = min + room - size;
            if (want > max) want = max;
            return want < min ? min : want;
        }

        // Where the chips of the main window go. Widths include margins; Rows say in which row each item is, the last item
        // being the support box.
        public sealed class ChipPlan
        {
            public int[] Widths;
            public int[] Rows;
            public int RowCount;
        }

        // nat and min: the natural and the smallest width of each chip. supportWidth: the support box, which does not shrink.
        // Natural widths are used when everything fits in one row; else the chips shrink towards their minimum, in proportion
        // to their slack; else they wrap, and the support box gets a row of its own after them.
        public static ChipPlan Chips(int[] nat, int[] min, int supportWidth, int room)
        {
            int n = nat.Length;
            ChipPlan p = new ChipPlan();
            p.Widths = new int[n + 1];
            p.Rows = new int[n + 1];
            p.Widths[n] = supportWidth;
            int sumNat = 0, sumMin = 0;
            for (int i = 0; i < n; i++) { sumNat += nat[i]; sumMin += Math.Min(min[i], nat[i]); }
            p.RowCount = 1;
            if (sumNat + supportWidth <= room)
            {
                Array.Copy(nat, p.Widths, n);
                return p;
            }
            if (sumMin + supportWidth <= room)
            {
                Shrink(nat, min, 0, n, room - supportWidth, p.Widths);
                return p;
            }
            int row = 0, start = 0, used = 0;
            for (int i = 0; i < n; i++)
            {
                int m = Math.Min(min[i], nat[i]);
                if (used > 0 && used + m > room)
                {
                    Shrink(nat, min, start, i, room, p.Widths);
                    row++;
                    start = i;
                    used = 0;
                }
                used += m;
                p.Rows[i] = row;
            }
            Shrink(nat, min, start, n, room, p.Widths);
            p.Rows[n] = row + 1;
            p.RowCount = row + 2;
            return p;
        }

        // The items from..to-1 at their natural width, or smaller, down to their minimum, so that they fit in room.
        static void Shrink(int[] nat, int[] min, int from, int to, int room, int[] widths)
        {
            int sum = 0, slack = 0;
            for (int i = from; i < to; i++)
            {
                widths[i] = nat[i];
                sum += nat[i];
                slack += nat[i] - Math.Min(min[i], nat[i]);
            }
            int extra = sum - room;
            if (extra <= 0) return;
            if (extra >= slack)
            {
                for (int i = from; i < to; i++) widths[i] = Math.Min(min[i], nat[i]);
                return;
            }
            for (int i = from; i < to; i++) widths[i] = nat[i] - (int)((long)extra * (nat[i] - Math.Min(min[i], nat[i])) / slack);
            sum = 0;
            for (int i = from; i < to; i++) sum += widths[i];
            for (int i = from; sum > room; i = i + 1 >= to ? from : i + 1)
                if (widths[i] > Math.Min(min[i], nat[i])) { widths[i]--; sum--; }
        }

        // Overview shows its tiles in two columns only when the narrower right one, leftPercent being the left one's share
        // of the width without the gap, keeps at least minRight.
        public static bool TwoColumns(int width, int minRight, int gap, int leftPercent)
        {
            return (width - gap) * (100 - leftPercent) / 100 >= minRight;
        }
    }
}
