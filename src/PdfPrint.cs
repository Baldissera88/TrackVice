namespace TrackVice
{
    using System;
    using System.Diagnostics;
    using System.IO;

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

        public static string FindEdge(Func<string, bool> exists, string programFilesX86, string programFiles)
        {
            foreach (string root in new string[] { programFilesX86, programFiles })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string p = Path.Combine(Path.Combine(Path.Combine(root, "Microsoft"), "Edge"), Path.Combine("Application", "msedge.exe"));
                if (exists(p)) return p;
            }
            return null;
        }

        public static string FindEdge()
        {
            return FindEdge(File.Exists, Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"));
        }

        // A profile folder of its own keeps a running Edge from taking the job over and ending without a PDF.
        public static string Arguments(string html, string pdf, string profile)
        {
            return "--headless --disable-gpu --no-pdf-header-footer" + (string.IsNullOrEmpty(profile) ? "" : " --user-data-dir=\"" + profile + "\"") + " --print-to-pdf=\"" + pdf + "\" \"" + html + "\"";
        }

        public static int? Run(string exe, string arguments, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                if (p.WaitForExit(timeoutMs)) return p.ExitCode;
                try { p.Kill(); } catch (Exception) { }
                return null;
            }
        }

        public static PdfResult Make(string edge, string html, string pdf, ProcessRunner run)
        {
            PdfResult r = new PdfResult();
            if (string.IsNullOrEmpty(edge))
            {
                r.Message = "Microsoft Edge was not found, so only the HTML file was made.";
                return r;
            }
            string profile = Path.Combine(Path.GetTempPath(), "TrackVice-edge-" + Guid.NewGuid().ToString("N"));
            try
            {
                try { if (File.Exists(pdf)) File.Delete(pdf); } catch (Exception) { }
                int? code;
                try { code = (run ?? Run)(edge, Arguments(html, pdf, profile), TimeoutMs); }
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
                try { exists = File.Exists(pdf) && new FileInfo(pdf).Length > 0; } catch (Exception) { }
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
                r.Ok = true;
                return r;
            }
            finally
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch (Exception) { }
            }
        }
    }
}
