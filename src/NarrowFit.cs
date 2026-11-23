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

        // Overview shows its tiles in two columns only when the narrower right one, leftPercent being the left one's share
        // of the width without the gap, keeps at least minRight.
        public static bool TwoColumns(int width, int minRight, int gap, int leftPercent)
        {
            return (width - gap) * (100 - leftPercent) / 100 >= minRight;
        }
    }
}
