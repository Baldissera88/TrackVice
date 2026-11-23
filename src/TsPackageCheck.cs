namespace TrackVice
{
    using System;

    // The second line under the Publish banner: the TrackVice package in the task sequence site is older than this TrackVice.
    public static class TsPackageCheck
    {
        public const string Button = "Update the TrackVice package";

        // The version in the package when it is older than the running one, or null. lookup finds the source folder of the package
        // on the site when none is remembered; it may be null.
        public static string Older(string remembered, Func<string> lookup, ITsFiles files, string running)
        {
            string src = (remembered ?? "").Trim();
            if (src.Length == 0 && lookup != null)
            {
                try { src = (lookup() ?? "").Trim(); }
                catch (Exception) { src = ""; }
            }
            try { return TsAgentPackage.OlderVersion(src, files, running); }
            catch (Exception) { return null; }
        }

        public static string Text(string inPackage, string running)
        {
            return "The TrackVice package for task sequences has TrackVice " + inPackage + ". This PC runs " + running + ". Devices started by a task sequence use the older one until the package is updated.";
        }
    }
}
