namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // The colours of one theme as 0xRRGGBB values. Theme in Ui.Theme.cs turns them into colours for the window.
    public sealed class Palette
    {
        public string Name = "";
        public bool IsDark;
        // 1 keeps the rounded corners, 0 draws them sharp.
        public float Corners = 1f;
        // Numbers, times and durations are drawn in the monospace font.
        public bool MonoNumbers;
        // The surface ladder, each a small step from the one before: Bg (the window), Card, CardAlt (raised: chips
        // and headers on a card), then Hover and Pressed on a card. Divider is the hairline around and between them.
        // Text3 is the grey of what waits or is expected; it still reaches 4.5:1 on every surface.
        public int Bg, Card, CardAlt, Stroke, Divider, Text, Text2, Text3;
        public int Accent, AccentHover, OnAccent, AccentSoft;
        public int Hover, Pressed, Field, FieldStroke;
        // The states: OkFg done (teal or green), WarnFg slow or a warning (amber), ErrFg a real failure (red).
        public int ErrBg, ErrFg, WarnBg, WarnFg, OkFg, Zebra, Header;
    }

    public static class Palettes
    {
        // The theme of a new install, before one is chosen in Settings.
        public const string Default = "Vice";

        // The choices in Settings, in this order. System follows the Windows app mode.
        public static readonly string[] Choices = { "System", "Light", "Dark", "Vice", "Terminal", "Amber", "Synthwave" };

        const int White = 0xFFFFFF, Black = 0x000000;

        // The saved choice, or the default when nothing is saved yet.
        public static string SavedName(string stored)
        {
            if (stored == null || stored.Trim().Length == 0) return Default;
            return stored.Trim();
        }

        // The palette for a name, without regard to case. Null for System and for names that are not known.
        public static Palette Find(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "light": return Light();
                case "dark": return Dark();
                case "vice": return Vice();
                case "terminal": return Terminal();
                case "amber": return Amber();
                case "synthwave": return Synthwave();
                default: return null;
            }
        }

        public static List<Palette> All()
        {
            return new List<Palette> { Light(), Dark(), Vice(), Terminal(), Amber(), Synthwave() };
        }

        public static Palette Light()
        {
            Palette t = new Palette();
            t.Name = "Light";
            t.Bg = 0xEEF1F6;
            t.Card = 0xFFFFFF;
            t.CardAlt = 0xF5F7FA;
            t.Stroke = 0xDCE1E8;
            t.Divider = 0xDCE1E8;
            t.Text = 0x111418;
            t.Text2 = 0x454B54;
            // Darker than LogVice's 0x6E7580, so that it reaches 4.5:1 on the page background.
            t.Text3 = 0x636A74;
            t.Accent = 0x005FB8;
            t.AccentHover = Mix(t.Accent, White, 0.12f);
            t.OnAccent = White;
            t.AccentSoft = Mix(t.Card, t.Accent, 0.14f);
            t.Hover = Mix(t.Card, 0x1F2A44, 0.06f);
            t.Pressed = Mix(t.Card, 0x1F2A44, 0.11f);
            t.Field = 0xFFFFFF;
            t.FieldStroke = 0xD0D5DD;
            t.ErrBg = 0xFDE3E5;
            t.ErrFg = 0xC42B1C;
            t.WarnBg = 0xFFF1C7;
            t.WarnFg = 0x9A5B00;
            t.OkFg = 0x0B7A5E;
            t.Zebra = 0xF6F8FB;
            t.Header = 0xF7F9FC;
            return t;
        }

        public static Palette Dark()
        {
            Palette t = new Palette();
            t.Name = "Dark";
            t.IsDark = true;
            t.Bg = 0x191B20;
            t.Card = 0x24272D;
            t.CardAlt = 0x2C3037;
            t.Stroke = 0x131519;
            t.Divider = 0x3A3E47;
            t.Text = 0xF5F7FA;
            t.Text2 = 0xD3D7DE;
            t.Text3 = 0xA2A8B2;
            t.Accent = 0x60CDFF;
            t.AccentHover = Mix(t.Accent, Black, 0.1f);
            t.OnAccent = 0x000000;
            t.AccentSoft = Mix(t.Card, t.Accent, 0.2f);
            t.Hover = Mix(t.Card, White, 0.06f);
            t.Pressed = Mix(t.Card, White, 0.09f);
            t.Field = 0x2A2E35;
            t.FieldStroke = 0x464B56;
            t.ErrBg = 0x47292B;
            t.ErrFg = 0xFF99A4;
            t.WarnBg = 0x45381C;
            t.WarnFg = 0xFFC24D;
            t.OkFg = 0x4CD4B0;
            t.Zebra = 0x282B32;
            t.Header = 0x292C33;
            return t;
        }

        public static Palette Vice()
        {
            Palette t = new Palette();
            t.Name = "Vice";
            t.IsDark = true;
            t.Bg = 0x120D26;
            t.Card = 0x1C1535;
            t.CardAlt = 0x251C45;
            t.Stroke = 0x0B0819;
            t.Divider = 0x3B2F6C;
            t.Text = 0xFFF5FD;
            t.Text2 = 0xE6D5F7;
            t.Text3 = 0xB39CD6;
            t.Accent = 0xFF4FB4;
            t.AccentHover = Mix(t.Accent, White, 0.12f);
            t.OnAccent = 0x1A0718;
            t.AccentSoft = Mix(t.Card, t.Accent, 0.24f);
            t.Hover = Mix(t.Card, 0xFF78DC, 0.09f);
            t.Pressed = Mix(t.Card, 0xFF78DC, 0.15f);
            t.Field = 0x211A3F;
            t.FieldStroke = 0x3D3070;
            // Red, well away from the magenta accent, so that a failure never reads as a selection.
            t.ErrBg = 0x47151F;
            t.ErrFg = 0xFF6464;
            t.WarnBg = 0x4A2C1F;
            t.WarnFg = 0xFFB36B;
            t.OkFg = 0x2EE6D6;
            t.Zebra = 0x20183D;
            t.Header = 0x221A42;
            return t;
        }

        // Green phosphor on near-black. The states differ in brightness as well as colour: running is a bright
        // white-green, done a middle green, waiting and skipped a dim green, warnings amber and failures red.
        public static Palette Terminal()
        {
            Palette t = new Palette();
            t.Name = "Terminal";
            t.IsDark = true;
            t.Corners = 0f;
            t.MonoNumbers = true;
            t.Bg = 0x050805;
            t.Card = 0x0A110A;
            t.CardAlt = 0x0F180F;
            t.Stroke = 0x000000;
            t.Divider = 0x1E3320;
            t.Text = 0xB4FFBE;
            t.Text2 = 0x7EE88E;
            t.Text3 = 0x4FB05E;
            t.Accent = 0xE6FFE9;
            t.AccentHover = Mix(t.Accent, Black, 0.1f);
            t.OnAccent = 0x031003;
            t.AccentSoft = Mix(t.Card, 0x39FF5A, 0.16f);
            t.Hover = Mix(t.Card, 0x39FF5A, 0.08f);
            t.Pressed = Mix(t.Card, 0x39FF5A, 0.13f);
            t.Field = 0x081008;
            t.FieldStroke = 0x2A4A2E;
            t.ErrBg = 0x3A0C0C;
            t.ErrFg = 0xFF5C5C;
            t.WarnBg = 0x2E2200;
            t.WarnFg = 0xFFB52E;
            t.OkFg = 0x3EDC5A;
            t.Zebra = 0x0D150D;
            t.Header = 0x0D160E;
            return t;
        }

        // Amber on black, the same build as Terminal. Failures are red so they stand apart from the amber.
        public static Palette Amber()
        {
            Palette t = new Palette();
            t.Name = "Amber";
            t.IsDark = true;
            t.Corners = 0f;
            t.MonoNumbers = true;
            t.Bg = 0x080500;
            t.Card = 0x110B02;
            t.CardAlt = 0x181004;
            t.Stroke = 0x000000;
            t.Divider = 0x3A2808;
            t.Text = 0xFFD27A;
            t.Text2 = 0xF0B347;
            t.Text3 = 0xB98328;
            t.Accent = 0xFFF2D6;
            t.AccentHover = Mix(t.Accent, Black, 0.1f);
            t.OnAccent = 0x140C00;
            t.AccentSoft = Mix(t.Card, 0xFFB000, 0.16f);
            t.Hover = Mix(t.Card, 0xFFB000, 0.08f);
            t.Pressed = Mix(t.Card, 0xFFB000, 0.13f);
            t.Field = 0x0E0901;
            t.FieldStroke = 0x4A3410;
            t.ErrBg = 0x420B06;
            t.ErrFg = 0xFF5A3D;
            t.WarnBg = 0x332A00;
            t.WarnFg = 0xFFEB3B;
            t.OkFg = 0xF29E16;
            t.Zebra = 0x150E03;
            t.Header = 0x160F03;
            return t;
        }

        // Dark purple with neon pink and cyan: cyan while running, mint when done, pink-red when failed, yellow warnings.
        public static Palette Synthwave()
        {
            Palette t = new Palette();
            t.Name = "Synthwave";
            t.IsDark = true;
            t.Bg = 0x170A2B;
            t.Card = 0x22103D;
            t.CardAlt = 0x2B154C;
            t.Stroke = 0x0B0416;
            t.Divider = 0x48267A;
            t.Text = 0xFBF3FF;
            t.Text2 = 0xE2CCF7;
            t.Text3 = 0xB89BDD;
            t.Accent = 0x2DE2FF;
            t.AccentHover = Mix(t.Accent, White, 0.15f);
            t.OnAccent = 0x0A0320;
            t.AccentSoft = Mix(t.Card, 0xFF3CAC, 0.22f);
            t.Hover = Mix(t.Card, 0xFF3CAC, 0.10f);
            t.Pressed = Mix(t.Card, 0xFF3CAC, 0.16f);
            t.Field = 0x1D0C35;
            t.FieldStroke = 0x4A2A7A;
            t.ErrBg = 0x4D0F3A;
            t.ErrFg = 0xFF5C9A;
            t.WarnBg = 0x3D3012;
            t.WarnFg = 0xFFE45C;
            t.OkFg = 0x5CFFB8;
            t.Zebra = 0x26123F;
            t.Header = 0x281443;
            return t;
        }

        public static int Mix(int a, int b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return (Part(a, 16, b, t) << 16) | (Part(a, 8, b, t) << 8) | Part(a, 0, b, t);
        }

        static int Part(int a, int shift, int b, float t)
        {
            int x = (a >> shift) & 0xFF, y = (b >> shift) & 0xFF;
            return (int)Math.Round(x + (y - x) * t);
        }

        // WCAG relative luminance and contrast ratio.
        public static double Luminance(int rgb)
        {
            return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
        }

        static double Channel(int v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        public static double Contrast(int a, int b)
        {
            double la = Luminance(a), lb = Luminance(b);
            if (la < lb) { double x = la; la = lb; lb = x; }
            return (la + 0.05) / (lb + 0.05);
        }
    }
}
