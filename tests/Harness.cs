namespace TrackVice.Tests
{
    using System;

    public static class T
    {
        public static int Passed, Failed;

        public static void Check(bool ok, string what)
        {
            if (ok) Passed++;
            else
            {
                Failed++;
                Console.WriteLine("FAIL: " + what);
            }
        }

        public static void Eq<TV>(TV expected, TV actual, string what)
        {
            bool ok = Equals(expected, actual);
            Check(ok, what + " (expected " + expected + ", got " + actual + ")");
        }
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "dump") { DemoTests.Dump(args.Length > 1 ? args[1] : "autopilot"); return 0; }
            LogTests.Run();
            ParserTests.Run();
            TrackerTests.Run();
            DemoTests.Run();
            RemoteTests.Run();
            SccmRunTests.Run();
            SccmResumeTests.Run();
            AgentTests.Run();
            AgentSecurityTests.Run();
            NetworkOutageTests.Run();
            SignInsTests.Run();
            CommandSearchTests.Run();
            NameCacheTests.Run();
            EspStepTests.Run();
            MdmTests.Run();
            GanttTests.Run();
            WhyTests.Run();
            ScriptRerunTests.Run();
            FleetTests.Run();
            AgentAlertTests.Run();
            CompareTests.Run();
            DeviceLogTests.Run();
            ReportTests.Run();
            CustomerTests.Run();
            VaultCustomerTests.Run();
            VaultSetupTests.Run();
            TsAgentTests.Run();
            IntuneLookupTests.Run();
            AutopilotTests.Run();
            MergeTests.Run();
            WallTests.Run();
            WallTimeLeftTests.Run();
            AgentUpdateTests.Run();
            ReadinessTests.Run();
            ReferenceTests.Run();
            RestartDownloadTests.Run();
            ZoneHistoryTests.Run();
            CustomerCodeTests.Run();
            DataExportTests.Run();
            SettingsTests.Run();
            ChangelogTests.Run();
            CustomerSetupTests.Run();
            AgentLifetimeTests.Run();
            AppGroupTests.Run();
            CustomerInstallTests.Run();
            ShareStorageTests.Run();
            InstallScriptSecurityTests.Run();
            AddCustomerTests.Run();
            AddCustomerGroupTests.Run();
            IntuneScriptCheckTests.Run();
            CustomerFlowTests.Run();
            StoragePolicyTests.Run();
            AppUpdateTests.Run();
            WelcomeTests.Run();
            ThemeTests.Run();
            LogViceJumpTests.Run();
            FeedbackTests.Run();
            OverviewTests.Run();
            SupportTests.Run();
            OffTimeTests.Run();
            DeviceTimeTests.Run();
            ServerEventTests.Run();
            LogViceGetTests.Run();
            UpdateSafetyTests.Run();
            AgentVersionTests.Run();
            SetupNeedsTests.Run();
            AppRegistrationTests.Run();
            MailTests.Run();
            NarrowTests.Run();
            HeroTextTests.Run();
            RowFitTests.Run();
            SccmPlanTests.Run();
            DownloadTests.Run();
            TsAgentSetupTests.Run();
            CustomerFileTests.Run();
            PublishTargetTests.Run();
            UpdateLocationTests.Run();
            PublishAccessTests.Run();
            UpdateHardeningTests.Run();
            GetStartedTests.Run();
            CustomerStorageLinkTests.Run();
            EnrollmentAnchorTests.Run();
            DeploymentEstimateTests.Run();
            ExpectedAppsTests.Run();
            DetailTextTests.Run();
            PreflightTests.Run();
            CustomerReportTests.Run();
            DiagnosticsTests.Run();
            LogSafetyTests.Run();
            ViewerInputTests.Run();
            DiagnosticsHardeningTests.Run();
            OnboardingSecurityTests.Run();
            Console.WriteLine(T.Passed + " passed, " + T.Failed + " failed");
            return T.Failed == 0 ? 0 : 1;
        }
    }
}
