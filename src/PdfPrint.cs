namespace TrackVice
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;

    // Makes a PDF from an HTML file with Microsoft Edge, which prints it headless. Nothing else is needed on the PC.
    public sealed class PdfResult
    {
        public bool Ok;
        // Why there is no PDF, in a sentence for the user.
        public string Message;
    }

    // Runs a program and waits for it. Returns the exit code, or null when it did not end within the time and was stopped.
    public delegate int? ProcessRunner(string exe, string arguments, int timeoutMs);

    public static class PdfPrint
    {
        public const int TimeoutMs = 60000;
        // The copy of the report Edge prints, and the PDF it writes, in a folder of their own.
        public const string HtmlName = "report.html", PdfName = "report.pdf", ProfileName = "profile";

        public static string FindEdge(Func<string, bool> exists, string programFilesX86, string programFiles)
        {
            foreach (string root in new string[] { programFilesX86, programFiles })
            {
                if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root)) continue;
                string p = Path.Combine(Path.Combine(Path.Combine(root, "Microsoft"), "Edge"), Path.Combine("Application", "msedge.exe"));
                if (exists(p)) return p;
            }
            return null;
        }

        // The Program Files folders as Windows knows them, not as environment variables, which the user can change.
        public static string FindEdge()
        {
            return FindEdge(File.Exists, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        }

        // A file:// address of a local file, with spaces and # escaped, so Edge cannot take it for a switch or a web address.
        public static string FileUri(string path)
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }

        // A profile folder of its own keeps a running Edge from taking the job over and ending without a PDF. The other
        // switches keep the headless Edge from going online for updates, sync, extensions or a first-run page.
        public static string Arguments(string html, string pdf, string profile)
        {
            return "--headless --disable-gpu --no-pdf-header-footer --no-first-run --no-default-browser-check --disable-extensions --disable-sync --disable-background-networking --disable-component-update"
                + (string.IsNullOrEmpty(profile) ? "" : " --user-data-dir=\"" + profile + "\"") + " --print-to-pdf=\"" + pdf + "\" \"" + html + "\"";
        }

        public static int? Run(string exe, string arguments, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                if (p.WaitForExit(timeoutMs)) return p.ExitCode;
                // Edge starts helper processes that keep the profile folder open: stop them all.
                try
                {
                    string kill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                    ProcessStartInfo k = new ProcessStartInfo(kill, "/PID " + p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " /T /F");
                    k.UseShellExecute = false;
                    k.CreateNoWindow = true;
                    using (Process kp = Process.Start(k)) kp.WaitForExit(10000);
                }
                catch (Exception) { }
                try { p.Kill(); } catch (Exception) { }
                return null;
            }
        }

        // Edge reads a copy of the report in a new folder of this user's temp folder and writes the PDF there; the PDF is
        // then copied to pdf. So Edge only ever opens a file TrackVice just wrote, whatever folder the report was saved to.
        public static PdfResult Make(string edge, string html, string pdf, ProcessRunner run)
        {
            PdfResult r = new PdfResult();
            if (string.IsNullOrEmpty(edge))
            {
                r.Message = "Microsoft Edge was not found, so only the HTML file was made.";
                return r;
            }
            string work = Path.Combine(Path.GetTempPath(), "TrackVice-pdf-" + Guid.NewGuid().ToString("N"));
            try
            {
                try { if (File.Exists(pdf)) File.Delete(pdf); } catch (Exception) { }
                string copy = Path.Combine(work, HtmlName), made = Path.Combine(work, PdfName), profile = Path.Combine(work, ProfileName);
                try
                {
                    Directory.CreateDirectory(work);
                    File.Copy(html, copy);
                }
                catch (Exception ex)
                {
                    r.Message = "The report could not be copied for Edge (" + ex.Message + "), so only the HTML file was made.";
                    return r;
                }
                int? code;
                try { code = (run ?? Run)(edge, Arguments(FileUri(copy), made, profile), TimeoutMs); }
                catch (Exception ex)
                {
                    r.Message = "Edge could not be started (" + ex.Message + "), so only the HTML file was made.";
                    return r;
                }
                if (!code.HasValue)
                {
                    r.Message = "Edge did not finish within " + (TimeoutMs / 1000) + " seconds and was stopped, so only the HTML file was made.";
                    return r;
                }
                bool exists = false;
                try { exists = File.Exists(made) && new FileInfo(made).Length > 0; } catch (Exception) { }
                if (code.Value != 0 && !exists)
                {
                    r.Message = "Edge ended with code " + code.Value + " and made no PDF, so only the HTML file was made.";
                    return r;
                }
                if (!exists)
                {
                    r.Message = "Edge made no PDF, so only the HTML file was made.";
                    return r;
                }
                try { File.Copy(made, pdf, true); }
                catch (Exception ex)
                {
                    r.Message = "The PDF could not be saved (" + ex.Message + "), so only the HTML file was made.";
                    return r;
                }
                r.Ok = true;
                return r;
            }
            finally
            {
                // Edge's helpers can hold the profile for a moment after it ends.
                for (int k = 0; k < 5; k++)
                {
                    try
                    {
                        if (Directory.Exists(work)) Directory.Delete(work, true);
                        break;
                    }
                    catch (Exception) { Thread.Sleep(300); }
                }
            }
        }
    }
}
