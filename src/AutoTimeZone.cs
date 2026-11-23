namespace TrackVice
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using Microsoft.Win32;

    // What the agent needs to turn on automatic time zone, so tests can fake it.
    public interface ITimeHost
    {
        object Get(string key, string name);
        void SetDword(string key, string name, int value);
        void SetString(string key, string name, string value);
        // Runs a program from System32 and returns its exit code, or -1 when it could not run.
        int Run(string exe, string args);
    }

    // Turns on Set time zone automatically on the device, only when the customer code asks for it. These are the two
    // values Microsoft documents for the setting (learn.microsoft.com/troubleshoot/windows-client/shell-experience/
    // cannot-set-timezone-automatically): Start of the tzautoupdate service, 3 on and 4 off, and the location consent
    // Value, Allow or Deny. Then it asks the Windows Time service to sync the clock (w32tm /resync).
    public static class AutoTimeZone
    {
        public const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\tzautoupdate";
        public const string LocationKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
        public const int On = 3;

        public static void Apply(ITimeHost h, Action<string> log)
        {
            log = log ?? delegate (string s) { };
            try
            {
                object start = h.Get(ServiceKey, "Start");
                if (start == null) log("Automatic time zone: the tzautoupdate service is not on this device. Nothing changed.");
                else if (Convert.ToInt32(start) == On) log("Automatic time zone: already on (tzautoupdate Start is 3).");
                else
                {
                    h.SetDword(ServiceKey, "Start", On);
                    log("Automatic time zone: set HKLM\\" + ServiceKey + " Start from " + Convert.ToInt32(start) + " to 3, as Set time zone automatically does.");
                }
            }
            catch (Exception ex) { log("Automatic time zone: could not set the tzautoupdate service: " + ex.Message); }
            try
            {
                string v = h.Get(LocationKey, "Value") as string;
                if (string.Equals(v, "Allow", StringComparison.OrdinalIgnoreCase)) log("Automatic time zone: location is already allowed.");
                else
                {
                    h.SetString(LocationKey, "Value", "Allow");
                    log("Automatic time zone: set HKLM\\" + LocationKey + " Value from " + (v ?? "not set") + " to Allow, so Windows can find the device's location. A policy that turns location off takes precedence.");
                }
            }
            catch (Exception ex) { log("Automatic time zone: could not allow location: " + ex.Message); }
            int rc = h.Run("w32tm.exe", "/resync");
            log(rc == 0 ? "Time sync: w32tm /resync asked the Windows Time service to sync the clock." : "Time sync: w32tm /resync returned " + rc + ". Windows syncs the clock later by itself.");
        }
    }

    public sealed class WinTimeHost : ITimeHost
    {
        static RegistryKey Hklm()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
        }

        public object Get(string key, string name)
        {
            using (RegistryKey b = Hklm())
            using (RegistryKey k = b.OpenSubKey(key))
                return k == null ? null : k.GetValue(name);
        }

        public void SetDword(string key, string name, int value)
        {
            using (RegistryKey b = Hklm())
            using (RegistryKey k = b.OpenSubKey(key, true))
            {
                if (k == null) throw new InvalidOperationException("HKLM\\" + key + " was not found.");
                k.SetValue(name, value, RegistryValueKind.DWord);
            }
        }

        public void SetString(string key, string name, string value)
        {
            using (RegistryKey b = Hklm())
            using (RegistryKey k = b.CreateSubKey(key))
                k.SetValue(name, value, RegistryValueKind.String);
        }

        public int Run(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(30000)) return -1;
                    return p.ExitCode;
                }
            }
            catch (Exception) { return -1; }
        }
    }
}
