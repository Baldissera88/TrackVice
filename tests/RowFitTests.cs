namespace TrackVice.Tests
{
    using System;

    public static class RowFitTests
    {
        public static void Run()
        {
            Rows();
            Cut();
        }

        static void Rows()
        {
            int[] one = RowFit.Rows(new int[] { 100, 140, 140 }, 8, 400);
            T.Eq(1, RowFit.RowCount(one), "rowfit: three buttons that fit stay in one row");
            int[] two = RowFit.Rows(new int[] { 100, 140, 140 }, 8, 300);
            T.Check(two[0] == 0 && two[1] == 0 && two[2] == 1, "rowfit: the button that does not fit starts a new row");
            T.Eq(2, RowFit.RowCount(two), "rowfit: two rows");
            int[] edge = RowFit.Rows(new int[] { 100, 100 }, 10, 210);
            T.Eq(1, RowFit.RowCount(edge), "rowfit: a row may be exactly full");
            int[] tight = RowFit.Rows(new int[] { 100, 100 }, 10, 209);
            T.Eq(2, RowFit.RowCount(tight), "rowfit: one pixel less wraps");
            int[] wide = RowFit.Rows(new int[] { 500, 80 }, 8, 300);
            T.Check(wide[0] == 0 && wide[1] == 1, "rowfit: a button wider than the room has its own row");
            T.Eq(0, RowFit.RowCount(RowFit.Rows(new int[0], 8, 300)), "rowfit: no buttons, no rows");
        }

        static int Measure(string s)
        {
            return s.Length * 10;
        }

        static void Cut()
        {
            Func<string, int> m = Measure;
            T.Eq("Device setup", RowFit.Ellipsis("Device setup", 200, m), "ellipsis: a title that fits is kept");
            T.Eq("Account…", RowFit.Ellipsis("Account setup", 80, m), "ellipsis: a long title is cut with an ellipsis");
            T.Eq("Device…", RowFit.Ellipsis("Device preparation", 70, m), "ellipsis: the space before the cut is dropped");
            T.Eq("", RowFit.Ellipsis("Apps", 5, m), "ellipsis: no room for even the ellipsis gives nothing");
            T.Eq("…", RowFit.Ellipsis("Apps", 10, m), "ellipsis: room for the ellipsis only");
            T.Check(Measure(RowFit.Ellipsis("Microsoft 365 Apps for enterprise", 150, m)) <= 150, "ellipsis: the result is never wider than the room");
            T.Eq("", RowFit.Ellipsis("", 50, m), "ellipsis: empty stays empty");
        }
    }
}
