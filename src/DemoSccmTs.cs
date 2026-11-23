namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // The made-up task sequence of the SCCM demo, as the Sequence property of its package holds it.
    public sealed partial class DemoSccm : ISccmGet
    {
        public const string PackageId = "P0100231";

        public const string SequenceXml =
            "<sequence version=\"3.00\">" +
            "<group name=\"Prepare disk\" description=\"\">" +
            "<step type=\"SMS_TaskSequence_PartitionDiskAction\" name=\"Partition Disk 0 - UEFI\" description=\"\" runIn=\"WinPE\" successCodeList=\"0 3010\" retryCount=\"0\"><action>TSDiskPart.exe</action></step>" +
            "</group>" +
            "<group name=\"Install Windows\" description=\"\">" +
            "<step type=\"SMS_TaskSequence_ApplyOperatingSystemAction\" name=\"Apply Operating System\" description=\"\" runIn=\"WinPE\" successCodeList=\"0 3010\" retryCount=\"0\"></step>" +
            "<step type=\"SMS_TaskSequence_ApplyWindowsSettingsAction\" name=\"Apply Windows Settings\" description=\"\" runIn=\"WinPE\" successCodeList=\"0 3010\" retryCount=\"0\"></step>" +
            "<step type=\"SMS_TaskSequence_ApplyDriverPackageAction\" name=\"Apply Device Drivers - Dell Latitude 7450\" description=\"\" runIn=\"WinPE\" successCodeList=\"0 3010\" retryCount=\"0\"><condition><expression type=\"SMS_TaskSequence_WMIConditionExpression\"/></condition></step>" +
            "<step type=\"SMS_TaskSequence_SetupWindowsAndSMSAction\" name=\"Setup Windows and Configuration Manager\" description=\"\" runIn=\"WinPE\" successCodeList=\"0 3010\" retryCount=\"0\"></step>" +
            "</group>" +
            "<group name=\"Applications\" description=\"\">" +
            "<step type=\"SMS_TaskSequence_InstallApplicationAction\" name=\"Install Microsoft 365 Apps\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"2\"><defaultVarList><variable name=\"ApplicationName\" property=\"ApplicationName\">Microsoft 365 Apps</variable></defaultVarList></step>" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Install Language Pack da-DK\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"0\"><action>lpksetup.exe /i da-DK</action></step>" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Install Language Pack fr-FR\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"0\"><action>lpksetup.exe /i fr-FR</action><condition><expression type=\"SMS_TaskSequence_VariableConditionExpression\"/></condition></step>" +
            "<step type=\"SMS_TaskSequence_InstallApplicationAction\" name=\"Install Applications\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"2\"><defaultVarList><variable name=\"BaseVariableName\" property=\"BaseVariableName\">APP</variable><variable name=\"ContinueOnInstallError\" property=\"ContinueOnInstallError\">true</variable></defaultVarList></step>" +
            "<step type=\"SMS_TaskSequence_InstallApplicationAction\" name=\"Install Teams classic\" description=\"\" disable=\"true\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"2\"></step>" +
            "</group>" +
            "<group name=\"Updates\" description=\"\">" +
            "<step type=\"SMS_TaskSequence_InstallUpdateAction\" name=\"Install Software Updates\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"0\"></step>" +
            "<step type=\"SMS_TaskSequence_RebootAction\" name=\"Restart Computer\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"0\"></step>" +
            "</group>" +
            "<group name=\"Finish\" description=\"\">" +
            "<step type=\"SMS_TaskSequence_EnableBitLockerAction\" name=\"Enable BitLocker\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"0\"></step>" +
            "<step type=\"SMS_TaskSequence_RunCommandLineAction\" name=\"Remove temporary files\" description=\"\" runIn=\"FullOS\" successCodeList=\"0 3010\" retryCount=\"0\" continueOnError=\"true\"><action>cmd /c rd /s /q C:\\Temp\\Setup</action></step>" +
            "</group>" +
            "</sequence>";

        public Dictionary<string, object> GetInstance(string className, string keyName, string keyValue)
        {
            if (className != "SMS_TaskSequencePackage" || keyValue != PackageId) return null;
            Dictionary<string, object> r = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            r["PackageID"] = PackageId;
            r["Name"] = "Windows 11 24H2 Standard";
            r["Sequence"] = SequenceXml;
            return r;
        }
    }
}
