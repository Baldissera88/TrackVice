namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Globalization;

    // --scale 125 for screenshots: sets the scale Windows would set at 125 %, and makes the fonts as large as Windows
    // would draw them. On Windows the scale comes from the screen, so this is only used to look at other scales.
    public static class ShotScale
    {
        public static void Apply(string percent)
        {
            double p;
            if (!double.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out p) || p < 100 || p > 400) return;
            float z = (float)(p / 100.0);
            float before = Dpi.Scale;
            Dpi.Scale = z;
            float k = z / before;
            Fonts.Ui = Grow(Fonts.Ui, k);
            Fonts.UiSemibold = Grow(Fonts.UiSemibold, k);
            Fonts.Small = Grow(Fonts.Small, k);
            Fonts.SmallSemibold = Grow(Fonts.SmallSemibold, k);
            Fonts.Title = Grow(Fonts.Title, k);
            Fonts.Mono = Grow(Fonts.Mono, k);
            Fonts.MonoSmall = Grow(Fonts.MonoSmall, k);
            Fonts.MonoSemibold = Grow(Fonts.MonoSemibold, k);
            Fonts.MonoTitle = Grow(Fonts.MonoTitle, k);
            Fonts.Icons = Grow(Fonts.Icons, k);
            Fonts.IconsSmall = Grow(Fonts.IconsSmall, k);
        }

        static Font Grow(Font f, float k)
        {
            return f == null ? null : new Font(f.FontFamily, f.Size * k, f.Style);
        }
    }
}
