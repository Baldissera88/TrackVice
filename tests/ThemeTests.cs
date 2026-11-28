namespace TrackVice.Tests
{
    using System;

    public static class ThemeTests
    {
        public static void Run()
        {
            // A new install has no saved theme and starts in Vice; a saved choice is kept.
            T.Eq("Vice", Palettes.Default, "default theme");
            T.Eq("Vice", Palettes.SavedName(null), "nothing saved gives Vice");
            T.Eq("Vice", Palettes.SavedName("  "), "empty saved value gives Vice");
            T.Eq("Light", Palettes.SavedName("Light"), "saved Light is kept");
            T.Eq("System", Palettes.SavedName("System"), "saved System is kept");

            foreach (string n in new string[] { "terminal", "AMBER", "Synthwave", "vice", "Dark", "light" })
                T.Check(Palettes.Find(n) != null, "theme " + n + " is found without regard to case");
            T.Check(Palettes.Find("System") == null, "System has no palette of its own");
            T.Check(Palettes.Find("Contoso") == null, "unknown theme name");
            T.Eq("Terminal", Palettes.Find("TERMINAL").Name, "terminal name");
            foreach (string c in Palettes.Choices)
                T.Check(c == "System" || Palettes.Find(c) != null, "choice " + c + " has a palette");
            T.Eq(7, Palettes.Choices.Length, "seven choices in Settings");

            T.Eq(0f, Palettes.Terminal().Corners, "terminal has sharp corners");
            T.Eq(0f, Palettes.Amber().Corners, "amber has sharp corners");
            T.Check(Palettes.Terminal().MonoNumbers && Palettes.Amber().MonoNumbers, "terminal and amber draw numbers in monospace");
            T.Eq(1f, Palettes.Vice().Corners, "vice keeps round corners");

            // The palettes of the existing themes are unchanged.
            T.Eq(0xDBE9F5, Palettes.Light().AccentSoft, "light accent soft");
            T.Eq(0x56B8E6, Palettes.Dark().AccentHover, "dark accent hover");

            T.Eq(21.0, Math.Round(Palettes.Contrast(0x000000, 0xFFFFFF), 1), "contrast of black on white");
            T.Eq(1.0, Palettes.Contrast(0x123456, 0x123456), "contrast of a colour with itself");

            foreach (Palette p in Palettes.All()) Contrast(p);
            foreach (Palette p in Palettes.All()) Ladder(p);

            // Vice: a failure is red, not the magenta of the accent.
            T.Check(HueGap(Palettes.Vice().ErrFg, Palettes.Vice().Accent) >= 25, "Vice: failed is not in the accent colour");

            // Terminal and Amber: running, done and waiting differ in brightness, not only in hue.
            foreach (Palette p in new Palette[] { Palettes.Terminal(), Palettes.Amber() })
            {
                T.Check(Palettes.Contrast(p.Accent, p.OkFg) >= 1.4, p.Name + ": running is brighter than done");
                T.Check(Palettes.Contrast(p.OkFg, p.Text3) >= 1.4, p.Name + ": done is brighter than waiting");
                T.Check(HueGap(p.ErrFg, p.OkFg) >= 20, p.Name + ": failed is not in the base colour");
            }
        }

        // WCAG AA, 4.5:1, for text and secondary text on every background they are drawn on.
        static void Contrast(Palette p)
        {
            int[] backs = { p.Bg, p.Card, p.CardAlt, p.Zebra, p.Header, p.Field };
            string[] backNames = { "Bg", "Card", "CardAlt", "Zebra", "Header", "Field" };
            int[] fores = { p.Text, p.Text2, p.Text3 };
            string[] foreNames = { "Text", "Text2", "Text3" };
            for (int b = 0; b < backs.Length; b++)
                for (int f = 0; f < fores.Length; f++)
                    AA(p, foreNames[f] + " on " + backNames[b], fores[f], backs[b]);
            foreach (int c in new int[] { p.OkFg, p.ErrFg, p.WarnFg, p.Accent })
                AA(p, "state colour " + c.ToString("X6") + " on Card", c, p.Card);
            AA(p, "Text on Hover", p.Text, p.Hover);
            AA(p, "Text on AccentSoft", p.Text, p.AccentSoft);
            AA(p, "ErrFg on ErrBg", p.ErrFg, p.ErrBg);
            AA(p, "Text on WarnBg", p.Text, p.WarnBg);
            AA(p, "OnAccent on Accent", p.OnAccent, p.Accent);
        }

        // The surfaces step up from the window to a pressed control, and the hairline shows on the window and on a card.
        static void Ladder(Palette p)
        {
            double card = Palettes.Contrast(p.Card, p.Bg), hover = Palettes.Contrast(p.Hover, p.Card), pressed = Palettes.Contrast(p.Pressed, p.Card);
            T.Check(card > 1.0 && card < 1.3, p.Name + ": the card is one small step from the background, " + card.ToString("0.00"));
            T.Check(Palettes.Contrast(p.CardAlt, p.Card) > 1.0, p.Name + ": raised differs from the card");
            T.Check(hover >= 1.08, p.Name + ": hover shows on a card, " + hover.ToString("0.00"));
            T.Check(pressed > hover, p.Name + ": pressed is a step past hover");
            T.Check(Palettes.Contrast(p.Divider, p.Card) >= 1.25, p.Name + ": the hairline shows on a card");
            T.Check(Palettes.Contrast(p.Divider, p.Bg) >= 1.15, p.Name + ": the hairline shows on the background");
            T.Check(Palettes.Contrast(p.FieldStroke, p.Field) >= 1.4, p.Name + ": the border of a field shows");
            T.Check(Palettes.Contrast(p.Text, p.Pressed) >= 4.5, p.Name + ": Text on Pressed");
        }

        static void AA(Palette p, string what, int fore, int back)
        {
            double c = Palettes.Contrast(fore, back);
            T.Check(c >= 4.5, p.Name + ": " + what + " is " + c.ToString("0.00") + ":1");
        }

        static double HueGap(int a, int b)
        {
            double d = Math.Abs(Hue(a) - Hue(b));
            return d > 180 ? 360 - d : d;
        }

        static double Hue(int rgb)
        {
            double r = ((rgb >> 16) & 0xFF) / 255.0, g = ((rgb >> 8) & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (d == 0) return 0;
            double h = max == r ? (g - b) / d : max == g ? 2 + (b - r) / d : 4 + (r - g) / d;
            h *= 60;
            return h < 0 ? h + 360 : h;
        }
    }
}
