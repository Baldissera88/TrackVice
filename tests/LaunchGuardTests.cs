namespace TrackVice.Tests
{
    using System;
    using System.Collections.Generic;

    public static class LaunchGuardTests
    {
        const string Channel = "evt:Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot";

        public static void Run()
        {
            Links();
            LogTargets();
            Explorer();
            FileNames();
            Reports();
            Errors();
            Percent();
        }

        static void Links()
        {
            T.Check(LaunchGuard.IsWebLink("https://github.com/Baldissera88/TrackVice/blob/main/docs/AGENT.md#test-on-one-device-first"), "the agent guide opens");
            T.Check(LaunchGuard.IsWebLink(PortalLinks.Intune("0f8fad5b-d9cb-469f-a165-70867728950e")), "an Intune device page opens");
            T.Check(LaunchGuard.IsWebLink(AppUpdate.ReleasesUrl), "the release page opens");
            T.Check(!LaunchGuard.IsWebLink(@"C:\Windows\System32\calc.exe"), "a program is not started");
            T.Check(!LaunchGuard.IsWebLink(@"\\attacker\share\x.exe"), "a share is not opened");
            T.Check(!LaunchGuard.IsWebLink("file:///C:/Windows/System32/calc.exe"), "a file link is not opened");
            T.Check(!LaunchGuard.IsWebLink("http://github.com/x"), "plain http is not opened");
            T.Check(!LaunchGuard.IsWebLink("ms-settings:"), "another scheme is not opened");
            T.Check(!LaunchGuard.IsWebLink("https://github.com.evil.example/x"), "a host that only starts like github.com");
            T.Check(!LaunchGuard.IsWebLink("https://evil.example/github.com"), "a host named in the path");
            T.Check(!LaunchGuard.IsWebLink("https://github.com@evil.example/"), "a user name before the host");
            T.Check(!LaunchGuard.IsWebLink("https://github.com:8443/"), "a port");
            T.Check(!LaunchGuard.IsWebLink("https://github.com\\@evil.example/"), "a backslash");
            T.Check(!LaunchGuard.IsWebLink("https://github.com/x\" --other"), "a quote");
            T.Check(!LaunchGuard.IsWebLink("https://github.com/x y"), "a space");
            T.Check(!LaunchGuard.IsWebLink("https://github.com/\u00e9"), "a character outside ASCII");
            T.Check(!LaunchGuard.IsWebLink(null) && !LaunchGuard.IsWebLink(""), "nothing");
            T.Check(!LaunchGuard.IsWebLink("https://github.com/" + new string('a', LaunchGuard.MaxLink)), "too long");
            T.Check(LaunchGuard.IsWebLink("https://GitHub.com/x"), "the host in capitals");
        }

        static void LogTargets()
        {
            List<string> refused = new List<string>();
            string[] ok = LaunchGuard.LogTargets(new string[]
            {
                @"C:\Windows\CCM\Logs\AppEnforce.log",
                Channel,
                @"\\attacker\share\smsts.log",
                @"//attacker/share/smsts.log",
                @"\\?\C:\Windows\x.log",
                @"\\.\pipe\x",
                "--find",
                "evt:x\" --theme y",
                "AppEnforce.log",
                @"C:\Logs\x.log:stream",
                @"C:\Logs\..\..\x.log",
                null,
                "",
            }, null, refused);
            T.Eq(2, ok.Length, "a session from storage: a file on a drive and an event channel pass");
            T.Eq(@"C:\Windows\CCM\Logs\AppEnforce.log", ok.Length > 0 ? ok[0] : null, "the file on a drive");
            T.Eq(Channel, ok.Length > 1 ? ok[1] : null, "the channel");
            T.Eq(9, refused.Count, "shares, device paths, options, bad channels, relative paths and streams are refused");

            string folder = @"\\server\logs\PC01";
            refused.Clear();
            ok = LaunchGuard.LogTargets(new string[] { folder + @"\CCM\AppEnforce.log", folder + @"\..\PC02\x.log", @"\\server\logs\PC012\x.log", @"\\other\x.log" }, folder, refused);
            T.Eq(1, ok.Length, "a folder of logs opened from a share: only its own files");
            T.Eq(3, refused.Count, "a step out of the folder, a sibling with the same start and another server");

            T.Check(LaunchGuard.IsUnder(@"C:\Users\a\AppData\Local\TrackVice\Logs\PC01-x\smsts.log", @"C:\Users\a\AppData\Local\TrackVice\Logs"), "a downloaded device log");
            T.Check(!LaunchGuard.IsUnder(@"C:\Users\a\AppData\Local\TrackVice\Logs", @"C:\Users\a\AppData\Local\TrackVice\Logs"), "the folder itself is not in it");
            T.Check(!LaunchGuard.IsUnder(@"C:\Users\a\AppData\Local\TrackVice\Logs2\x.log", @"C:\Users\a\AppData\Local\TrackVice\Logs"), "a folder with a longer name");
            T.Check(!LaunchGuard.IsUnder(@"C:\x.log", null), "no folder");

            T.Check(LaunchGuard.IsEventChannel("evt:Microsoft-Windows-AAD/Operational"), "a channel");
            T.Check(!LaunchGuard.IsEventChannel("evt:"), "an empty channel");
            T.Check(!LaunchGuard.IsEventChannel("evt:-x"), "a channel that starts like an option");
            T.Check(!LaunchGuard.IsEventChannel("evt:a\\b"), "a backslash in a channel");
        }

        static void Explorer()
        {
            T.Eq("\"C:\\Users\\a\\AppData\\Local\\TrackVice\"", LaunchGuard.ExplorerArgument(@"C:\Users\a\AppData\Local\TrackVice\"), "a folder, quoted, without the last backslash");
            T.Eq("\"C:\\\\\"", LaunchGuard.ExplorerArgument(@"C:\"), "a drive keeps its backslash, doubled before the quote");
            T.Eq(null, LaunchGuard.ExplorerArgument("/select,C:\\Windows\\System32\\calc.exe"), "a switch");
            T.Eq(null, LaunchGuard.ExplorerArgument(@"C:\Logs\PC01,/root,C:\Windows"), "a comma would start another switch");
            T.Eq(null, LaunchGuard.ExplorerArgument("C:\\Logs\" /select,\"C:\\x"), "a quote would end the argument");
            T.Eq(null, LaunchGuard.ExplorerArgument("-x"), "an option");
            T.Eq(null, LaunchGuard.ExplorerArgument(null), "nothing");
        }

        static void FileNames()
        {
            T.Eq("PC01", LaunchGuard.FileNamePart("PC01"), "a plain device name");
            T.Eq("Windows_System32_x", LaunchGuard.FileNamePart(@"..\..\Windows\System32\x"), "no folders or steps up");
            T.Eq("C__x", LaunchGuard.FileNamePart("C:/x"), "no drive");
            T.Eq("device", LaunchGuard.FileNamePart(".."), "only dots");
            T.Eq("device", LaunchGuard.FileNamePart(null), "no name");
            T.Eq(64, LaunchGuard.FileNamePart(new string('A', 500)).Length, "a long name is cut");
        }

        static void Reports()
        {
            T.Check(LaunchGuard.IsReportFile(@"C:\Users\a\Desktop\TrackVice-PC01.html"), "the saved report opens");
            T.Check(!LaunchGuard.IsReportFile(@"C:\Users\a\Desktop\TrackVice-PC01.exe"), "a program is not started");
            T.Check(!LaunchGuard.IsReportFile(@"C:\Users\a\Desktop\x.html.lnk"), "a shortcut is not opened");
            T.Check(!LaunchGuard.IsReportFile("x\".html"), "a quote");
        }

        static void Errors()
        {
            Exception sas = new InvalidOperationException("Upload failed: https://acct.blob.core.windows.net/c?sv=2022-11-02&sp=cw&sig=AbCdEf0123456789%2Bxyz");
            T.Check(LaunchGuard.Message(sas).IndexOf("AbCdEf0123456789", StringComparison.Ordinal) < 0, "the signature of a storage link is hidden in an error");
            Exception bearer = new Exception("401 for Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.e30.sig");
            T.Check(LaunchGuard.Details(bearer).IndexOf("eyJ0eXAiOiJKV1Qi", StringComparison.Ordinal) < 0, "a token is hidden in the error window");
            T.Check(LaunchGuard.Details(new Exception(new string('x', 20000))).Length <= LaunchGuard.MaxDetails + 1, "the error window has a size limit");
            T.Eq("Unknown error", LaunchGuard.Message(null), "no error");
            T.Check(LaunchGuard.Hide("password=Secret123!").IndexOf("Secret123", StringComparison.Ordinal) < 0, "a copied line is masked");
        }

        static void Percent()
        {
            T.Eq(95, CommandSearch.PercentOf("2000000000 of 2100000000 done"), "a huge count from storage does not overflow");
        }
    }
}
