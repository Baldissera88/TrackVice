namespace TrackVice
{
    using System.Collections.Generic;

    // What error codes that deployments often hit mean, and what to do next.
    public static class ErrorBase
    {
        sealed class Entry
        {
            public string Meaning;
            public string Next;

            public Entry(string meaning, string next)
            {
                Meaning = meaning;
                Next = next;
            }
        }

        static readonly Dictionary<uint, Entry> Codes = new Dictionary<uint, Entry>
        {
            // Windows Installer exit codes. 0x8007xxxx forms fall back to these.
            { 1602, new Entry("The user cancelled the installation.", "Make the install command silent, for example msiexec /qn, so no one can cancel it.") },
            { 1603, new Entry("A fatal error occurred during installation.", "Add /l*v to the msiexec command line and look for \"return value 3\" in the MSI log.") },
            { 1605, new Entry("This action is only valid for products that are installed. Usually an uninstall ran for a product that is not on the device.", "Check the product code in the uninstall command against the one that is installed.") },
            { 1612, new Entry("The installation source for this product is not available.", "Windows Installer needs the original MSI to repair, update or remove the product. Check that the cached package or the source folder still exists.") },
            { 1618, new Entry("Another installation is already in progress.", "Another installation was running. The agent normally retries later.") },
            { 1619, new Entry("The installation package could not be opened.", "Check that the MSI is in the content and that the command line names it correctly, relative to the working folder.") },
            { 1620, new Entry("The installation package could not be opened. It may not be a valid Windows Installer package.", "The file is often damaged or incomplete. Redistribute or upload the content again.") },
            { 1624, new Entry("Error applying transforms.", "Check that the MST file named in TRANSFORMS= is in the content and was made for this MSI.") },
            { 1638, new Entry("Another version of this product is already installed.", "Remove the old version first, or set up an upgrade: supersedence, or a major upgrade in the MSI.") },
            { 1641, new Entry("The installer started a restart. This counts as success.", "Nothing to fix. Items after it usually wait for the restart.") },
            { 3010, new Entry("A restart is required to finish the install. This counts as success.", "Restart the device to finish the install.") },

            // Win32 errors as HRESULT.
            { 0x80070002, new Entry("The system cannot find the file specified.", "A file the install needs is missing: check the content, the command line and the working folder.") },
            { 0x80070005, new Entry("Access is denied.", "Check the install context (system or user). Security software or application control often blocks the installer; check its logs.") },
            { 0x80070020, new Entry("The file is in use by another process.", "Close the app or service that holds the file, or install while it is not running. A restart usually frees it.") },
            { 0x80070070, new Entry("There is not enough space on the disk.", "Free disk space. The content is downloaded and unpacked before it installs, so it needs room for both.") },
            { 0x800704C7, new Entry("The operation was cancelled.", "Often a restart, a sign-out or a time limit stopped the install. Check what happened on the device at that time.") },
            { 0x80072EE2, new Entry("The connection to the server timed out.", "Check the network path to the service: proxy, firewall and VPN. It often clears on a later retry.") },
            { 0x80072EE7, new Entry("The server name could not be resolved.", "Check DNS and that the device has a working network connection.") },
            { 0x80072F8F, new Entry("A security error occurred while connecting to the server.", "Usually a wrong date and time on the device, or a proxy that inspects TLS. Check the clock and the proxy.") },
            { 0x80072F76, new Entry("The HTTP header that was asked for was not found.", "The download was cut off or changed on the way, often by a proxy. Check the proxy and the download logs.") },
            { 0x80190190, new Entry("The server answered HTTP 400 (bad request).", "The request was not valid for the service. Check the address and that the device's clock and registration are right.") },
            { 0x80190191, new Entry("The server answered HTTP 401 (unauthorized).", "The device or user could not sign in to the service. Check the device registration and the user's sign-in.") },
            { 0x80190193, new Entry("The server answered HTTP 403 (forbidden).", "The service refused the request. In enrollment this often means the user may not enroll or has no licence.") },
            { 0x80190194, new Entry("The server answered HTTP 404 (not found).", "The content or address does not exist on the server. Check the content location or the URL.") },

            // Intune Management Extension.
            { 0x87D1041C, new Entry("The application was not detected after installation completed successfully.", "The installer finished, but the detection rule did not find the app. Check the detection rule against what the installer really writes.") },
            { 0x87D30065, new Entry("Failed to retrieve content information.", "The agent could not get the app's content details from Intune. Check the network and proxy, and that the content finished uploading.") },
            { 0x87D30067, new Entry("Error unzipping downloaded content.", "Check free disk space. If it keeps happening, upload the .intunewin file again.") },
            { 0x87D300C9, new Entry("The unmonitored process is in progress, however it may time out.", "The install command was still running when the agent stopped watching it. Check that the command is silent and does not wait for input.") },

            // Configuration Manager.
            { 0x80004005, new Entry("Unspecified error.", "A general error. The log lines just before it usually name the real problem.") },
            { 0x80070490, new Entry("Element not found.", "In a task sequence this often means a driver, disk or setting the step expected is missing. Check the step's lines in smsts.log.") },
            { 0x800700A1, new Entry("The specified path is not valid.", "In a task sequence this often means the drive or partition the step uses does not exist. Check the disk and partition steps.") },
            { 0x80091007, new Entry("The hash value is not correct.", "The downloaded content does not match the package. Update the distribution points or redistribute the package.") },
            { 0x8007000E, new Entry("Not enough memory resources are available.", "In Windows PE, give the device or virtual machine more memory, or raise the scratch space of the boot image.") },
            { 0x87D00324, new Entry("The application was not detected after installation completed.", "The installer finished, but the detection method did not find the app. Check the detection method against what the installer writes.") },
            { 0x87D00607, new Entry("The content for this deployment was not found on a distribution point.", "Distribute the content to a distribution point in the device's boundary group, or allow fallback.") },
            { 0x87D00213, new Entry("The job timed out before it finished.", "The installer ran longer than the maximum run time. Check that it is silent and does not wait for input.") },
        };

        // Status values of the Office CSP (Microsoft 365 Apps installed by the MDM client).
        static readonly Dictionary<long, Entry> OfficeCodes = new Dictionary<long, Entry>
        {
            { 13, new Entry("The signature of the downloaded Office Deployment Tool could not be checked.", "Check that the device reaches the Office CDN without a proxy changing the download.") },
            { 1460, new Entry("The Office Deployment Tool could not be downloaded in time.", "Check that the device reaches the Office CDN, and the proxy and firewall rules for it.") },
            { 1602, new Entry("The installation was cancelled.", "Check whether a user or another process stopped the setup.") },
            { 1603, new Entry("A prerequisite check failed. Usually an MSI version of Office is installed, or the installed Office has the other bitness.", "Remove the older Office, or install the same architecture (32 or 64-bit) as the Office on the device.") },
            { 17000, new Entry("The Click-to-Run client could not be started.", "Restart the device and let the install run again.") },
            { 17001, new Entry("The installation could not be queued in the Click-to-Run client.", "Restart the device and let the install run again.") },
            { 17002, new Entry("The process could not complete. Usually it was cancelled by the user or another install, the disk ran full, or a language ID is unknown.", "Check free disk space and the languages in the configuration.") },
            { 17003, new Entry("Another Office setup was already running.", "Wait for the other setup to end. The install is usually tried again.") },
            { 17004, new Entry("Needed content was not available. Usually an unknown product or language, content missing on the CDN, or a failed signature check.", "Check the product and language IDs in the configuration, and that the device reaches the Office CDN.") },
            { 17005, new Entry("The setup was cancelled as planned.", null) },
            { 17006, new Entry("Running Office apps blocked the update.", "Close the Office apps, or let setup close them, and try again.") },
            { 17007, new Entry("The Click-to-Run client asked for clean-up of an installation that was being removed.", "Remove Office fully, then install again.") },
            { 17100, new Entry("The Click-to-Run client did not accept its command line.", "Check the configuration XML in the Office policy.") },
        };

        static Entry Find(long code)
        {
            if (code == 0 || code < int.MinValue || code > uint.MaxValue) return null;
            uint u = unchecked((uint)code);
            Entry e;
            if (Codes.TryGetValue(u, out e)) return e;
            if ((u & 0xFFFF0000) == 0x80070000 && Codes.TryGetValue(u & 0xFFFF, out e)) return e;
            return null;
        }

        public static string Meaning(long code)
        {
            Entry e = Find(code);
            return e == null ? null : e.Meaning;
        }

        public static string Next(long code)
        {
            Entry e = Find(code);
            return e == null ? null : e.Next;
        }

        public static string OfficeMeaning(long status)
        {
            Entry e;
            return OfficeCodes.TryGetValue(status, out e) ? e.Meaning : null;
        }

        public static string OfficeNext(long status)
        {
            Entry e;
            return OfficeCodes.TryGetValue(status, out e) ? e.Next : null;
        }
    }
}
