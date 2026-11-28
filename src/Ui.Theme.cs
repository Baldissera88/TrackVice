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
        public bool IsDark, MonoNumbers;
        public float Corners = 1f;
        public string Name = "";
        public Color Bg, Card, CardAlt, Stroke, Divider, Text, Text2, Text3;
        public Color Accent, AccentHover, OnAccent, AccentSoft;
        public Color Hover, Pressed, Field, FieldStroke;
        public Color ErrBg, ErrFg, WarnBg, WarnFg, OkFg, Zebra, Header;

        public static Theme Light() { return From(Palettes.Light()); }
        public static Theme Dark() { return From(Palettes.Dark()); }
        public static Theme Vice() { return From(Palettes.Vice()); }
        public static Theme Terminal() { return From(Palettes.Terminal()); }
        public static Theme Amber() { return From(Palettes.Amber()); }
        public static Theme Synthwave() { return From(Palettes.Synthwave()); }

        public static Theme From(Palette p)
        {
            Theme t = new Theme();
            t.Name = p.Name;
            t.IsDark = p.IsDark;
            t.Corners = p.Corners;
            t.MonoNumbers = p.MonoNumbers;
            t.Bg = Hex(p.Bg);
            t.Card = Hex(p.Card);
            t.CardAlt = Hex(p.CardAlt);
            t.Stroke = Hex(p.Stroke);
            t.Divider = Hex(p.Divider);
            t.Text = Hex(p.Text);
            t.Text2 = Hex(p.Text2);
            t.Text3 = Hex(p.Text3);
            t.Accent = Hex(p.Accent);
            t.AccentHover = Hex(p.AccentHover);
            t.OnAccent = Hex(p.OnAccent);
            t.AccentSoft = Hex(p.AccentSoft);
            t.Hover = Hex(p.Hover);
            t.Pressed = Hex(p.Pressed);
            t.Field = Hex(p.Field);
            t.FieldStroke = Hex(p.FieldStroke);
            t.ErrBg = Hex(p.ErrBg);
            t.ErrFg = Hex(p.ErrFg);
            t.WarnBg = Hex(p.WarnBg);
            t.WarnFg = Hex(p.WarnFg);
            t.OkFg = Hex(p.OkFg);
            t.Zebra = Hex(p.Zebra);
            t.Header = Hex(p.Header);
            return t;
        }

        // System, or a name that is not known, follows the Windows app mode.
        public static Theme Named(string name)
        {
            Palette p = Palettes.Find(name);
            if (p != null) return From(p);
            return SystemPrefersDark() ? Dark() : Light();
        }

        // The theme name saved in Settings, or Vice when none is saved yet.
        public static string SavedName()
        {
            return Palettes.SavedName(Prefs.Get("Theme", null));
        }

        // How round the corners of the theme in use are. Set when a control takes a theme, read by Shapes.
        public static float CornerScale = 1f;

        // A number, time or duration in the theme's font for numbers: the monospace font in Terminal and Amber.
        public Font Num(Font normal)
        {
            if (!MonoNumbers || normal == null) return normal;
            if (normal == Fonts.Title) return Fonts.MonoTitle;
            if (normal == Fonts.Small || normal == Fonts.SmallSemibold) return Fonts.MonoSmall;
            if (normal == Fonts.UiSemibold) return Fonts.MonoSemibold;
            return Fonts.Mono;
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

    // Whether short animations run. They are off when Windows has "Show animations in Windows" or its visual
    // effects turned off, and over Remote Desktop, where every frame is sent over the network.
    public static class Fx
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SystemParametersInfo(int action, int param, ref int value, int winIni);

        const int SPI_GETCLIENTAREAANIMATION = 0x1042;

        public static bool On
        {
            get
            {
                try
                {
                    if (!System.Windows.Forms.SystemInformation.UIEffectsEnabled) return false;
                    if (System.Windows.Forms.SystemInformation.TerminalServerSession) return false;
                }
                catch (Exception) { }
                try
                {
                    int on = 1;
                    if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref on, 0) && on == 0) return false;
                }
                catch (Exception) { }
                return true;
            }
        }

        public static float EaseOut(float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            float u = 1 - t;
            return 1 - u * u * u;
        }

        public static float EaseInOut(float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
        }
    }

    // A value that eases out from where it is to a target in Ms milliseconds and calls back to repaint on the way.
    // With animations off it jumps to the target.
    public sealed class Tween : IDisposable
    {
        public float Value;
        public readonly int Ms;
        readonly Action changed;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        float from, to;
        int start;

        public Tween(Action changed, int ms)
        {
            this.changed = changed;
            Ms = ms;
            timer.Interval = 15;
            timer.Tick += delegate { Step(); };
        }

        public bool Running
        {
            get { return timer.Enabled; }
        }

        public void Snap(float v)
        {
            timer.Stop();
            Value = from = to = v;
        }

        public void To(float target)
        {
            if (target == to && (timer.Enabled || Value == target)) return;
            if (!Fx.On || Ms <= 0) { Snap(target); changed(); return; }
            from = Value;
            to = target;
            start = Environment.TickCount;
            timer.Start();
        }

        void Step()
        {
            float t = Math.Min(1f, (Environment.TickCount - start) / (float)Ms);
            Value = from + (to - from) * Fx.EaseOut(t);
            if (t >= 1f) { Value = to; timer.Stop(); }
            changed();
        }

        public void Dispose()
        {
            timer.Dispose();
        }
    }

    public static class Fonts
    {
        public static Font Ui, UiSemibold, Small, SmallSemibold, Title, Mono, MonoSmall, MonoSemibold, MonoTitle, Icons, IconsSmall;
        public static string UiName = "", MonoName = "";
        // The type scale, in pixels at 100 %: Display 22, Title 19, Heading 15, body 13 (Ui), Small 11.
        // A big single number, such as a percentage in a ring, uses Px(32, true).
        public static Font Display { get { return Px(22, true); } }
        public static Font Heading { get { return Px(15, true); } }
        static string family = "Segoe UI", semiFamily, displayFamily;
        static readonly Dictionary<string, Font> px = new Dictionary<string, Font>();

        // A font of the UI family at a height in pixels at 100 %, cached. It follows the size of Ui, so it grows with
        // the screen scale like the other fonts. Semibold uses the semibold face when one is installed, and the
        // Display face from 20 pixels up.
        public static Font Px(float pixels, bool semibold)
        {
            float pt = pixels * (Ui != null ? Ui.Size / 13f : 0.75f);
            string key = pt.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + (semibold ? "s" : "r");
            Font f;
            lock (px)
            {
                if (px.TryGetValue(key, out f)) return f;
                string semi = pixels >= 20 && displayFamily != null ? displayFamily : semiFamily;
                if (!semibold) f = new Font(family, pt);
                else f = semi != null ? new Font(semi, pt) : new Font(family, pt, FontStyle.Bold);
                px[key] = f;
            }
            return f;
        }

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
            // Windows cuts a font name at 31 characters, so the Display semibold face is listed as "... Semib".
            string disp = Pick(installed, null, "Segoe UI Variable Display Semibold", "Segoe UI Variable Display Semib", "Segoe UI Semibold");
            string mono = Pick(installed, null, "Cascadia Mono", "Consolas", "Lucida Console");
            string icons = Pick(installed, null, "Segoe Fluent Icons", "Segoe MDL2 Assets");
            family = ui ?? "Segoe UI";
            semiFamily = semi;
            displayFamily = disp;
            lock (px) px.Clear();
            Ui = new Font(ui, 9.75f);
            UiSemibold = semi != null ? new Font(semi, 9.75f) : new Font(ui, 9.75f, FontStyle.Bold);
            Small = new Font(ui, 8.25f);
            SmallSemibold = semi != null ? new Font(semi, 8.25f) : new Font(ui, 8.25f, FontStyle.Bold);
            Title = disp != null ? new Font(disp, 14f) : new Font(ui, 14f, FontStyle.Bold);
            Mono = mono != null ? new Font(mono, 9.25f) : new Font(FontFamily.GenericMonospace, 9.25f);
            MonoSmall = mono != null ? new Font(mono, 8.25f) : new Font(FontFamily.GenericMonospace, 8.25f);
            MonoSemibold = new Font(Mono.FontFamily, 9.25f, FontStyle.Bold);
            MonoTitle = new Font(Mono.FontFamily, 13f, FontStyle.Bold);
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
