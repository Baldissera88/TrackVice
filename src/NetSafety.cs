namespace TrackVice
{
    using System;
    using System.IO;
    using System.Net;
    using System.Xml;

    // Shared guards for what comes from the network: the TLS versions every request may use, and a reader for XML
    // that takes no document type definition.
    public static class NetSafety
    {
        // SSL 3.0, TLS 1.0 and TLS 1.1, as SecurityProtocolType has them.
        const int Old = 48 | 192 | 768;
        // TLS 1.2, which .NET Framework 4.5 knows but does not offer by itself.
        const int Tls12 = 3072;

        // The longest XML answer that is read, in characters. A list of 5000 blobs with their metadata is a few million.
        public const long MaxXmlChars = 64L * 1024 * 1024;

        // The protocols to offer, given the ones set now: the old ones are taken out and TLS 1.2 is put in. A newer
        // one that is set stays. 0 means that Windows chooses, which is left alone.
        public static SecurityProtocolType Strong(SecurityProtocolType current)
        {
            int c = (int)current;
            if (c == 0) return current;
            return (SecurityProtocolType)((c & ~Old) | Tls12);
        }

        // Call this before every request. Adding TLS 1.2 alone leaves SSL 3.0 and TLS 1.0 on offer, which is what
        // .NET Framework 4.5 starts with, so a server that only speaks those would still be talked to.
        // Who should call it: GitHub.Request (src/GitHub.cs) and Remote.Request (src/Remote.cs), in place of
        // the line that adds TLS 1.2, which are the two places every HTTP request of TrackVice is made.
        public static void UseStrongTls()
        {
            try
            {
                SecurityProtocolType now = ServicePointManager.SecurityProtocol, want = Strong(now);
                if (want != now) ServicePointManager.SecurityProtocol = want;
            }
            catch (Exception) { }
        }

        // Reads XML that came from somewhere else. A document type definition is refused, so no entity is expanded
        // (a few lines of nested entities grow to gigabytes) and nothing outside the text is fetched.
        // Who should call it: Agent.ParseList (src/Agent.cs) and StoragePolicy.Parse (src/StoragePolicy.cs), which
        // read answers of Azure Storage with XmlDocument.LoadXml.
        public static XmlDocument LoadXml(string xml)
        {
            XmlReaderSettings rs = new XmlReaderSettings();
            rs.DtdProcessing = DtdProcessing.Prohibit;
            rs.XmlResolver = null;
            rs.MaxCharactersInDocument = MaxXmlChars;
            XmlDocument d = new XmlDocument();
            d.XmlResolver = null;
            // An answer of Azure Storage starts with a byte order mark.
            using (XmlReader r = XmlReader.Create(new StringReader((xml ?? "").TrimStart('﻿')), rs)) d.Load(r);
            return d;
        }
    }
}
