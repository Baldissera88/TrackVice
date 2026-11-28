namespace TrackVice
{
    using System;

    // The words of the overall card at the top of a device: the time left as one line, and what goes under it, from the
    // most to the least, so the card shows the first that fits.
    public sealed class HeroText
    {
        // "About 18 min left" then "~18 min left"; "Taking longer than usual" then "Longer than usual"; none when there is
        // no estimate.
        public string[] Leads = new string[0];
        // "done ~21:12 · 15–24 min", then "done ~21:12"; "usually 35 min"; "rough estimate".
        public string[] Details = new string[0];
        public bool Over, Rough;

        public static HeroText From(DeploymentEstimate e)
        {
            HeroText h = new HeroText();
            if (e == null) return h;
            if (e.Over)
            {
                h.Over = true;
                h.Leads = new string[] { "Taking longer than usual", "Longer than usual" };
                h.Details = new string[] { e.UsualText };
                return h;
            }
            string words = TimeLeft.Words(e.Left);
            h.Leads = words.StartsWith("about ", StringComparison.Ordinal) ? new string[] { Cap(words), "~" + words.Substring(6) } : new string[] { Cap(words) };
            if (e.Rough)
            {
                h.Rough = true;
                h.Details = new string[] { "rough estimate" };
                return h;
            }
            string range = e.RangeText;
            if (range.Length > 0 && !range.EndsWith(" h", StringComparison.Ordinal)) range += " min";
            h.Details = range.Length > 0 ? new string[] { e.ClockText + " · " + range, e.ClockText } : new string[] { e.ClockText };
            return h;
        }

        public string Lead
        {
            get { return Leads.Length > 0 ? Leads[0] : ""; }
        }

        public bool Same(HeroText o)
        {
            return o != null && o.Over == Over && o.Rough == Rough && Equal(o.Leads, Leads) && Equal(o.Details, Details);
        }

        static bool Equal(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static string Cap(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // The cards from..to-1 of a row that has room to spare get one width: the widest one's, and more, up to cap, when
        // the room allows. Widths include the margins. Returns the room still left, for before the support buttons, so they
        // end at the right edge. Nothing changes when the cards cannot all get the widest one's width.
        public static int Spread(int[] widths, int from, int to, int cap, int room)
        {
            int sum = 0;
            foreach (int w in widths) sum += w;
            int extra = room - sum;
            if (extra <= 0) return 0;
            if (to <= from) return extra;
            int widest = 0;
            for (int i = from; i < to; i++) widest = Math.Max(widest, widths[i]);
            int need = 0;
            for (int i = from; i < to; i++) need += widest - widths[i];
            if (need > extra) return extra;
            int each = Math.Max(widest, Math.Min(cap, widest + (extra - need) / (to - from)));
            for (int i = from; i < to; i++)
            {
                extra -= each - widths[i];
                widths[i] = each;
            }
            return Math.Max(0, extra);
        }

        // A window too narrow for the cards in one row: the overall card with the support buttons beside it, and the
        // counts in a row of their own, as wide as the window and as even as their smallest widths allow. min: the
        // smallest widths, the overall card first, each with the gap after it. False when that does not fit either; else
        // widths has the widths, with the gap.
        public static bool Stack(int[] min, int support, int room, int gap, int[] widths)
        {
            int n = min.Length;
            if (n < 2 || min[0] + support > room) return false;
            int sum = 0;
            for (int i = 1; i < n; i++) sum += min[i];
            if (sum > room + gap) return false;
            widths[0] = room - support;
            for (int i = 1; i < n; i++) widths[i] = min[i];
            Level(widths, 1, n, room + gap);
            return true;
        }

        // Widens the items from..to-1 until they take up room, the narrowest first, so they end up as even as they can.
        public static void Level(int[] widths, int from, int to, int room)
        {
            if (to <= from) return;
            int sum = 0;
            for (int i = from; i < to; i++) sum += widths[i];
            for (int extra = room - sum; extra > 0; extra--)
            {
                int k = from;
                for (int i = from + 1; i < to; i++) if (widths[i] < widths[k]) k = i;
                widths[k]++;
            }
        }
    }
}
