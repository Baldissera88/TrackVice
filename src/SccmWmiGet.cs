namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Management;

    // Reads one instance by its key, so the lazy properties have their value (Sequence of SMS_TaskSequencePackage,
    // OptionalComponents of SMS_BootImagePackage), as the SMS Provider documentation says to.
    public sealed partial class SccmWmi : ISccmGet
    {
        // The connection, made when it is first needed.
        public ManagementScope Scope()
        {
            if (scope == null) Connect();
            return scope;
        }

        public Dictionary<string, object> GetInstance(string className, string keyName, string keyValue)
        {
            if (!TsReader.SafeId(className) || !TsReader.SafeId(keyName) || !TsReader.SafeId(keyValue)) throw new ArgumentException("The ID is not one of the site.");
            ManagementScope s = Scope();
            try
            {
                using (ManagementObject o = new ManagementObject(s, new ManagementPath(className + "." + keyName + "='" + keyValue + "'"), null))
                {
                    o.Get();
                    Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData p in o.Properties)
                    {
                        object v = p.Value;
                        if (v != null && p.Type == CimType.DateTime) v = FromDmtf(v as string);
                        row[p.Name] = v;
                    }
                    return row;
                }
            }
            catch (ManagementException ex)
            {
                if (ex.ErrorCode == ManagementStatus.NotFound) return null;
                throw Failure(ex, className + "." + keyName + "=" + keyValue);
            }
        }

        // "Generic failure" alone says nothing. The SMS Provider puts the reason in the extended error.
        public static InvalidOperationException Failure(ManagementException ex, string what)
        {
            string why = null;
            try { if (ex.ErrorInformation != null) why = Convert.ToString(ex.ErrorInformation["Description"], CultureInfo.InvariantCulture); }
            catch (Exception) { }
            return new InvalidOperationException(ex.Message.Trim() + (string.IsNullOrEmpty(why) ? "" : ": " + why.Trim()) + " (" + what + ")", ex);
        }
    }
}
