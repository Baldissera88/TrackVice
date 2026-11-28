namespace TrackVice.Tests
{
    using System;

    public static class HeroTextTests
    {
        public static void Run()
        {
            Words();
            Spread();
            Stack();
        }

        static void Words()
        {
            HeroText none = HeroText.From(null);
            T.Eq(0, none.Leads.Length, "hero: no time left without an estimate");
            T.Eq("", none.Lead, "hero: an empty lead without an estimate");
            T.Check(none.Same(HeroText.From(null)) && !none.Same(null), "hero: the same words are the same");

            DeploymentEstimate e = new DeploymentEstimate();
            e.Left = TimeSpan.FromMinutes(20);
            e.Low = TimeSpan.FromMinutes(15);
            e.High = TimeSpan.FromMinutes(24);
            e.HasRange = true;
            e.FinishUtc = new DateTime(2026, 11, 20, 12, 18, 0, DateTimeKind.Utc);
            HeroText h = HeroText.From(e);
            T.Eq("About 20 min left", h.Lead, "hero: the time left starts with a capital");
            T.Eq("~20 min left", h.Leads[h.Leads.Length - 1], "hero: a short form for a narrow card");
            T.Eq(2, h.Details.Length, "hero: the finish with the range, then alone");
            T.Check(h.Details[0].StartsWith(h.Details[1] + " · ", StringComparison.Ordinal) && h.Details[0].EndsWith(" min", StringComparison.Ordinal), "hero: the range has its unit: " + h.Details[0]);
            T.Check(h.Details[1].StartsWith("done ~", StringComparison.Ordinal), "hero: the finish clock: " + h.Details[1]);
            T.Check(!h.Over && !h.Rough && !h.Same(none), "hero: an estimate is not over or rough");

            e.Rough = true;
            T.Eq("rough estimate", HeroText.From(e).Details[0], "hero: a rough estimate says so, without a clock");
            e.Rough = false;
            e.Over = true;
            e.Usual = TimeSpan.FromMinutes(35);
            HeroText over = HeroText.From(e);
            T.Check(over.Over, "hero: over the usual time");
            T.Eq("Taking longer than usual", over.Lead, "hero: over the usual time in words");
            T.Check(over.Details[0].StartsWith("usually ", StringComparison.Ordinal), "hero: the usual time under it: " + over.Details[0]);
        }

        static void Spread()
        {
            int[] w = { 240, 130, 120, 120, 120, 125 };
            int left = HeroText.Spread(w, 1, 6, 180, 1200);
            T.Check(w[1] == 180 && w[2] == 180 && w[5] == 180, "spread: one width, up to the cap");
            T.Eq(240, w[0], "spread: the first card keeps its width");
            T.Eq(1200 - 240 - 5 * 180, left, "spread: the rest is returned");

            int[] v = { 240, 130, 120, 120, 120, 125 };
            T.Eq(0, HeroText.Spread(v, 1, 6, 180, 900), "spread: all the room is used");
            T.Check(v[1] == v[2] && v[2] == v[5] && v[1] >= 130 && 240 + 5 * v[1] <= 900, "spread: one width that fits");

            int[] u = { 240, 130, 120, 120, 120, 125 };
            T.Eq(5, HeroText.Spread(u, 1, 6, 180, 860), "spread: too little room for one width leaves it");
            T.Eq(120, u[2], "spread: nothing changed");
            T.Eq(0, HeroText.Spread(u, 1, 6, 180, 700), "spread: nothing to give when there is no room");
        }

        static void Stack()
        {
            int[] min = { 218, 88, 112, 96, 88, 100 };
            int[] w = new int[7];
            T.Check(HeroText.Stack(min, 248, 664, 12, w), "stack: fits at 700 px");
            T.Eq(664, w[0] + 248, "stack: the overall card and the support buttons fill the first row");
            int sum = 0, low = int.MaxValue, high = 0;
            for (int i = 1; i < 6; i++) { sum += w[i]; low = Math.Min(low, w[i]); high = Math.Max(high, w[i]); }
            T.Eq(664 + 12, sum, "stack: the counts end at the right edge");
            T.Check(high - low <= 1, "stack: the counts have one width");
            for (int i = 1; i < 6; i++) T.Check(w[i] >= min[i], "stack: no count under its minimum");

            T.Check(HeroText.Stack(min, 248, 480, 12, w), "stack: fits tightly");
            T.Check(w[2] == 112 && w[1] < 112 && w[1] >= 88, "stack: the widest keeps its minimum, the others come up to it");
            T.Check(!HeroText.Stack(min, 248, 460, 12, w), "stack: not when the support buttons do not fit beside the card");
            T.Check(!HeroText.Stack(min, 100, 400, 12, w), "stack: not when the counts do not fit in a row");

            int[] l = { 5, 50, 70, 60 };
            HeroText.Level(l, 1, 4, 200);
            T.Check(l[0] == 5 && l[1] + l[2] + l[3] == 200 && l[2] == 70 && Math.Abs(l[1] - l[3]) <= 1, "level: the narrowest first");
        }
    }
}
