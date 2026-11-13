namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Text;
    using Microsoft.Win32;

    public static class Dpi
    {
        public static float Scale = 1f;

        public static int S(float v)
        {
            return (int)Math.Round(v * Scale);
        }
    }

    // The same palettes as LogVice and PolicyVice, so the tools look like one family.
    public sealed class Theme
    {
        public bool IsDark;
        public string Name = "";
        public Color Bg, Card, CardAlt, Stroke, Divider, Text, Text2, Text3;
        public Color Accent, AccentHover, OnAccent, AccentSoft;
        public Color Hover, Pressed, Field, FieldStroke;
        public Color ErrBg, ErrFg, WarnBg, WarnFg, OkFg, Zebra, Header;

        public static Theme Light()
        {
            Theme t = new Theme();
            t.Name = "Light";
            t.Bg = Hex(0xEEF1F6);
            t.Card = Hex(0xFFFFFF);
            t.CardAlt = Hex(0xF5F7FA);
            t.Stroke = Hex(0xDCE1E8);
            t.Divider = Hex(0xE6E9EF);
            t.Text = Hex(0x111418);
            t.Text2 = Hex(0x454B54);
            t.Text3 = Hex(0x6E7580);
            t.Accent = Hex(0x005FB8);
            t.AccentHover = Mix(t.Accent, Color.White, 0.12f);
            t.OnAccent = Color.White;
            t.AccentSoft = Mix(t.Card, t.Accent, 0.14f);
            t.Hover = Mix(t.Card, Color.Black, 0.055f);
            t.Pressed = Mix(t.Card, Color.Black, 0.03f);
            t.Field = Hex(0xFFFFFF);
            t.FieldStroke = Hex(0xD0D5DD);
            t.ErrBg = Hex(0xFDE3E5);
            t.ErrFg = Hex(0xC42B1C);
            t.WarnBg = Hex(0xFFF1C7);
            t.WarnFg = Hex(0x9D5D00);
            t.OkFg = Hex(0x0F7B0F);
            t.Zebra = Hex(0xF7F9FC);
            t.Header = Hex(0xF7F9FC);
            return t;
        }

        public static Theme Dark()
        {
            Theme t = new Theme();
            t.Name = "Dark";
            t.IsDark = true;
            t.Bg = Hex(0x191B20);
            t.Card = Hex(0x24272D);
            t.CardAlt = Hex(0x2C3037);
            t.Stroke = Hex(0x131519);
            t.Divider = Hex(0x363A42);
            t.Text = Hex(0xF5F7FA);
            t.Text2 = Hex(0xD3D7DE);
            t.Text3 = Hex(0xA2A8B2);
            t.Accent = Hex(0x60CDFF);
            t.AccentHover = Mix(t.Accent, Color.Black, 0.1f);
            t.OnAccent = Hex(0x000000);
            t.AccentSoft = Mix(t.Card, t.Accent, 0.2f);
            t.Hover = Mix(t.Card, Color.White, 0.06f);
            t.Pressed = Mix(t.Card, Color.White, 0.035f);
            t.Field = Hex(0x2A2E35);
            t.FieldStroke = Hex(0x3A3F48);
            t.ErrBg = Hex(0x47292B);
            t.ErrFg = Hex(0xFF99A4);
            t.WarnBg = Hex(0x45391D);
            t.WarnFg = Hex(0xFCE100);
            t.OkFg = Hex(0x6CCB5F);
            t.Zebra = Hex(0x282B32);
            t.Header = Hex(0x292C33);
            return t;
        }

        public static Theme Vice()
        {
            Theme t = new Theme();
            t.Name = "Vice";
            t.IsDark = true;
            t.Bg = Hex(0x120D26);
            t.Card = Hex(0x1C1535);
            t.CardAlt = Hex(0x251C45);
            t.Stroke = Hex(0x0B0819);
            t.Divider = Hex(0x352A60);
            t.Text = Hex(0xFFF5FD);
            t.Text2 = Hex(0xE6D5F7);
            t.Text3 = Hex(0xB39CD6);
            t.Accent = Hex(0xFF4FB4);
            t.AccentHover = Mix(t.Accent, Color.White, 0.12f);
            t.OnAccent = Hex(0x1A0718);
            t.AccentSoft = Mix(t.Card, t.Accent, 0.24f);
            t.Hover = Mix(t.Card, Hex(0xFF78DC), 0.09f);
            t.Pressed = Mix(t.Card, Hex(0xFF78DC), 0.05f);
            t.Field = Hex(0x211A3F);
            t.FieldStroke = Hex(0x3D3070);
            t.ErrBg = Hex(0x4A1638);
            t.ErrFg = Hex(0xFF6FA8);
            t.WarnBg = Hex(0x4A2C1F);
            t.WarnFg = Hex(0xFFB36B);
            t.OkFg = Hex(0x2EE6D6);
            t.Zebra = Hex(0x20183D);
            t.Header = Hex(0x221A42);
            return t;
        }

        public static Theme Named(string name)
        {
            switch (name)
            {
                case "Light": return Light();
                case "Dark": return Dark();
                case "Vice": return Vice();
                default: return SystemPrefersDark() ? Dark() : Light();
            }
        }

        public static Color Hex(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(255,
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null);
                if (v is int) return (int)v == 0;
            }
            catch (Exception) { }
            return false;
        }
    }

    public static class Fonts
    {
        public static Font Ui, UiSemibold, Small, SmallSemibold, Title, Mono, MonoSmall, Icons, IconsSmall;
        public static string UiName = "", MonoName = "";

        public static void Init()
        {
            Dictionary<string, bool> installed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (InstalledFontCollection c = new InstalledFontCollection())
                {
                    foreach (FontFamily f in c.Families) installed[f.Name] = true;
                }
            }
            catch (Exception) { }
            string ui = Pick(installed, SystemFonts.MessageBoxFont.FontFamily.Name, "Segoe UI Variable Text", "Segoe UI");
            string semi = Pick(installed, null, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
            string disp = Pick(installed, null, "Segoe UI Variable Display Semibold", "Segoe UI Semibold");
            string mono = Pick(installed, null, "Cascadia Mono", "Consolas", "Lucida Console");
            string icons = Pick(installed, null, "Segoe Fluent Icons", "Segoe MDL2 Assets");
            Ui = new Font(ui, 9.75f);
            UiSemibold = semi != null ? new Font(semi, 9.75f) : new Font(ui, 9.75f, FontStyle.Bold);
            Small = new Font(ui, 8.25f);
            SmallSemibold = semi != null ? new Font(semi, 8.25f) : new Font(ui, 8.25f, FontStyle.Bold);
            Title = disp != null ? new Font(disp, 14f) : new Font(ui, 14f, FontStyle.Bold);
            Mono = mono != null ? new Font(mono, 9.25f) : new Font(FontFamily.GenericMonospace, 9.25f);
            MonoSmall = mono != null ? new Font(mono, 8.25f) : new Font(FontFamily.GenericMonospace, 8.25f);
            Icons = icons != null ? new Font(icons, 10.5f) : null;
            IconsSmall = icons != null ? new Font(icons, 7.5f) : null;
            UiName = Ui.Name;
            MonoName = Mono.Name;
        }

        static string Pick(Dictionary<string, bool> installed, string fallback, params string[] names)
        {
            foreach (string n in names) if (installed.ContainsKey(n)) return n;
            return fallback;
        }
    }

    public static class Glyphs
    {
        public const string Refresh = "";
        public const string Save = "";
        public const string Compare = "";
        public const string Watch = "";
        public const string Export = "";
        public const string Log = "";
        public const string Theme = "";
        public const string Info = "";
        public const string Shield = "";
        public const string Copy = "";
        public const string Search = "";
    }

    // More glyphs from Segoe Fluent Icons and Segoe MDL2 Assets, by code point.
    public static class Glyphs2
    {
        public const string Sync = "";
        public const string Settings = "";
        public const string Back = "";
        public const string Palette = "";
        public const string Calendar = "";
        public const string Folder = "";
        public const string Reset = "";
        public const string Keyboard = "";
        public const string Code = "";
        public const string Apps = "";
        public const string Device = "";
        public const string Help = "";
        public const string Clock = "";
    }

    public static class Prefs
    {
        const string Key = @"Software\TrackVice";

        public static string Location
        {
            get { return @"HKEY_CURRENT_USER\" + Key; }
        }

        public static bool GetBool(string name, bool fallback)
        {
            string v = Get(name, null);
            if (v == null) return fallback;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static void SetBool(string name, bool value)
        {
            Set(name, value ? "1" : "0");
        }

        public static int GetInt(string name, int fallback, int min, int max)
        {
            int n = GetInt(name, fallback);
            return n < min ? min : n > max ? max : n;
        }

        // Removes every TrackVice setting of this user.
        public static void Reset()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(Key, false); } catch (Exception) { }
        }

        public static string Get(string name, string fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v == null ? fallback : Convert.ToString(v);
                }
            }
            catch (Exception) { return fallback; }
        }

        public static int GetInt(string name, int fallback)
        {
            int n;
            return int.TryParse(Get(name, null), out n) ? n : fallback;
        }

        public static void Set(string name, string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value == null) k.DeleteValue(name, false);
                    else k.SetValue(name, value);
                }
            }
            catch (Exception) { }
        }
    }

    // A preference that is a credential, such as a SAS link: kept encrypted for this Windows user.
    public static class Secret
    {
        public static void Save(string name, string value)
        {
            try
            {
                byte[] enc = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value ?? ""), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                Prefs.Set(name, System.Convert.ToBase64String(enc));
            }
            catch (System.Exception) { }
        }

        public static string Load(string name)
        {
            try
            {
                string v = Prefs.Get(name, "");
                if (v.Length == 0) return "";
                return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(System.Convert.FromBase64String(v), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch (System.Exception) { return ""; }
        }
    }
}
