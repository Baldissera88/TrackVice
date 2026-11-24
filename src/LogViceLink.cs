namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using Microsoft.Win32;

    // Opens logs in LogVice. LogVice takes file paths and event channels ("evt:<channel>") on its command line.
    public static partial class LogViceLink
    {
        public const string MdmAdmin = "evt:Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin";
        public const string MdmOperational = "evt:Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Operational";
        public const string Aad = "evt:Microsoft-Windows-AAD/Operational";
        public const string Autopilot = "evt:Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot";
        public const string ShellCore = "evt:Microsoft-Windows-Shell-Core/Operational";

        public static string ImeFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\IntuneManagementExtension\Logs"); }
        }

        public static string ImeLog
        {
            get { return Path.Combine(ImeFolder, "IntuneManagementExtension.log"); }
        }

        // IntuneManagementExtension.log, and AppWorkload.log when it exists (Win32 app installs on newer IME versions).
        public static string[] ImeLogs()
        {
            List<string> list = new List<string>();
            list.Add(ImeLog);
            try
            {
                string work = Path.Combine(ImeFolder, "AppWorkload.log");
                if (File.Exists(work)) list.Add(work);
            }
            catch (Exception) { }
            return list.ToArray();
        }

        public static string Find()
        {
            List<string> candidates = new List<string>();
            // The chosen path is stored per user. When TrackVice runs as administrator it would start that program
            // as administrator, so only trust it then when it is in a folder that only administrators can change.
            string custom = Prefs.Get("LogVicePath", null);
            if (!string.IsNullOrEmpty(custom) && (!Program.IsAdmin || UnderProgramFiles(custom))) candidates.Add(custom);
            try
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (RegistryKey k = hklm.OpenSubKey(@"Software\LogVice"))
                    {
                        object dir = k == null ? null : k.GetValue("InstallDir");
                        if (dir != null) candidates.Add(Path.Combine(Convert.ToString(dir), "LogVice.exe"));
                    }
                    using (RegistryKey k = hklm.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\LogVice.exe"))
                    {
                        object exe = k == null ? null : k.GetValue(null);
                        if (exe != null) candidates.Add(Convert.ToString(exe).Trim('"'));
                    }
                }
            }
            catch (Exception) { }
            try { candidates.Add(Path.Combine(Path.GetDirectoryName(Program.Location), "LogVice.exe")); } catch (Exception) { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"LogVice\LogVice.exe"));
            // The copy Download keeps for this user, and the temporary one on a device that deploys.
            if (!Program.IsAdmin) candidates.Add(Path.Combine(LogViceGet.KeepFolder, LogViceGet.ExeName));
            if (LogViceGet.SessionExe != null) candidates.Add(LogViceGet.SessionExe);
            foreach (string c in candidates)
            {
                try { if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c; } catch (Exception) { }
            }
            return null;
        }

        static bool UnderProgramFiles(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                foreach (Environment.SpecialFolder f in new Environment.SpecialFolder[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    string root = Environment.GetFolderPath(f);
                    if (!string.IsNullOrEmpty(root) && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // The version of a LogVice.exe, or null when it cannot be read.
        public static string VersionOf(string exe)
        {
            return LogViceGet.VersionOf(exe);
        }

        // True when this LogVice.exe knows the line, time, find and theme options.
        public static bool CanJump(string exe)
        {
            return SupportsJump(VersionOf(exe));
        }

        // Opens targets with the theme only. Callers with a place to land in use the overload with a jump.
        public static bool Open(string exe, params string[] targets)
        {
            return Open(exe, null, targets);
        }

        public static bool Open(string exe, LogViceJump jump, string[] targets)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            LogViceJump use = new LogViceJump();
            if (jump != null) { use.Line = jump.Line; use.Time = jump.Time; use.Find = jump.Find; use.Theme = jump.Theme; }
            if (use.Theme == null) use.Theme = Theme.SavedName();
            ProcessStartInfo psi = new ProcessStartInfo(exe, BuildArguments(targets, use, VersionOf(exe)));
            psi.UseShellExecute = false;
            Process.Start(psi);
            return true;
        }
    }
}
