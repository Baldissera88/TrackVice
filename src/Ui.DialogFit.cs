namespace TrackVice
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    // Keeps a dialog inside the working area of the screen it opens on. At 125 % and 150 % scaling or on a small screen
    // a dialog made for a large one would run off the edge, with its buttons out of reach.
    public static class DialogFit
    {
        public static void Keep(Form f)
        {
            f.Load += delegate { Clamp(f); };
        }

        static void Clamp(Form f)
        {
            Rectangle wa;
            try { wa = Screen.FromControl(f).WorkingArea; }
            catch (Exception) { return; }
            f.MinimumSize = new Size(NarrowFit.Capped(f.MinimumSize.Width, wa.Width), NarrowFit.Capped(f.MinimumSize.Height, wa.Height));
            f.Size = new Size(NarrowFit.Capped(f.Width, wa.Width), NarrowFit.Capped(f.Height, wa.Height));
            f.Location = new Point(NarrowFit.Inside(f.Left, f.Width, wa.Left, wa.Width), NarrowFit.Inside(f.Top, f.Height, wa.Top, wa.Height));
        }
    }
}
