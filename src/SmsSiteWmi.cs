namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Management;

    // The site of ISmsSite, through the SMS Provider over WMI with the signed-in Windows account. The classes and methods are
    // the ones the Configuration Manager documentation gives: SMS_Package (Put), SMS_DistributionPointGroup.AddPackages,
    // SMS_Package.RefreshPkgSource, SMS_TaskSequencePackage.GetSequence, SetSequence and ImportSequence,
    // SMS_TaskSequence_RunCommandLineAction, SMS_TaskSequence_Group and SMS_BootImagePackage.OptionalComponents.
    public sealed class SmsSiteWmi : ISmsSite
    {
        readonly SccmWmi w;

        public SmsSiteWmi(SccmWmi wmi)
        {
            w = wmi;
        }

        public string Name
        {
            get { return "site " + w.SiteCode + " on " + w.Server; }
        }

        public List<SmsPackage> FindPackages(string name)
        {
            List<SmsPackage> l = new List<SmsPackage>();
            foreach (Dictionary<string, object> r in w.Query("SELECT PackageID, Name, PkgSourcePath, Version FROM SMS_Package WHERE Name = '" + name.Replace("'", "") + "'"))
            {
                SmsPackage p = new SmsPackage();
                p.PackageId = SccmCatalog.Str(r, "PackageID");
                p.Name = SccmCatalog.Str(r, "Name");
                p.SourcePath = SccmCatalog.Str(r, "PkgSourcePath");
                p.Version = SccmCatalog.Str(r, "Version");
                l.Add(p);
            }
            return l;
        }

        public List<SmsDpGroup> DistributionPointGroups()
        {
            List<SmsDpGroup> l = new List<SmsDpGroup>();
            foreach (Dictionary<string, object> r in w.Query("SELECT GroupID, Name, MemberCount FROM SMS_DistributionPointGroup"))
            {
                SmsDpGroup g = new SmsDpGroup();
                g.Id = SccmCatalog.Str(r, "GroupID");
                g.Name = SccmCatalog.Str(r, "Name");
                g.Members = (int)SccmCatalog.Int(r, "MemberCount");
                l.Add(g);
            }
            l.Sort(delegate (SmsDpGroup a, SmsDpGroup b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return l;
        }

        public List<SmsTaskSequence> TaskSequences()
        {
            List<SmsTaskSequence> l = new List<SmsTaskSequence>();
            foreach (Dictionary<string, object> r in w.Query("SELECT PackageID, Name FROM SMS_TaskSequencePackage"))
            {
                SmsTaskSequence t = new SmsTaskSequence();
                t.PackageId = SccmCatalog.Str(r, "PackageID");
                t.Name = SccmCatalog.Str(r, "Name");
                l.Add(t);
            }
            return l;
        }

        // Sequence and BootImageID are lazy properties: only a Get of the instance by its key has their value.
        public string SequenceXml(string id)
        {
            Dictionary<string, object> r = w.GetInstance("SMS_TaskSequencePackage", "PackageID", id);
            if (r == null) throw new InvalidOperationException("The task sequence package " + id + " is not on the site.");
            return SccmCatalog.Str(r, "Sequence");
        }

        public string BootImageId(string id)
        {
            Dictionary<string, object> r = w.GetInstance("SMS_TaskSequencePackage", "PackageID", id);
            string b = r == null ? null : SccmCatalog.Str(r, "BootImageID");
            return string.IsNullOrEmpty(b) ? null : b;
        }

        public SmsBootImage BootImage(string id)
        {
            Dictionary<string, object> r = w.GetInstance("SMS_BootImagePackage", "PackageID", id);
            if (r == null) return null;
            SmsBootImage b = new SmsBootImage();
            b.PackageId = id;
            b.Name = SccmCatalog.Str(r, "Name");
            object v;
            System.Collections.IEnumerable e = r.TryGetValue("OptionalComponents", out v) ? v as System.Collections.IEnumerable : null;
            if (e != null) foreach (object o in e) b.Components.Add(Convert.ToInt64(o, CultureInfo.InvariantCulture));
            return b;
        }

        // A package with a source folder (PkgSourceFlag 2, STORAGE_DIRECT) and no program.
        public string CreatePackage(string name, string description, string sourcePath)
        {
            ManagementScope s = w.Scope();
            using (ManagementClass c = new ManagementClass(s, new ManagementPath("SMS_Package"), null))
            using (ManagementObject o = c.CreateInstance())
            {
                o["Name"] = name;
                o["Description"] = description;
                o["PkgSourceFlag"] = 2u;
                o["PkgSourcePath"] = sourcePath;
                ManagementPath p;
                try { p = o.Put(); }
                catch (ManagementException ex) { throw SccmWmi.Failure(ex, "SMS_Package"); }
                string id = KeyOf(p);
                if (id == null)
                {
                    List<SmsPackage> found = FindPackages(name);
                    if (found.Count > 0) id = found[found.Count - 1].PackageId;
                }
                if (id == null) throw new InvalidOperationException("The site made the package but did not give its ID.");
                return id;
            }
        }

        static string KeyOf(ManagementPath p)
        {
            string rel = p == null ? null : p.RelativePath;
            if (string.IsNullOrEmpty(rel)) return null;
            int a = rel.IndexOf('"');
            int b = rel.LastIndexOf('"');
            return a >= 0 && b > a ? rel.Substring(a + 1, b - a - 1) : null;
        }

        static ManagementObject Instance(ManagementScope s, string cls, string key, string id)
        {
            if (!TsReader.SafeId(id)) throw new ArgumentException("The ID is not one of the site.");
            ManagementObject o = new ManagementObject(s, new ManagementPath(cls + "." + key + "='" + id + "'"), null);
            try { o.Get(); }
            catch (ManagementException ex) { o.Dispose(); throw SccmWmi.Failure(ex, cls + "." + key + "=" + id); }
            return o;
        }

        static void Check(ManagementBaseObject outp, string method)
        {
            object v = outp == null ? null : outp["ReturnValue"];
            if (v != null && Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException(method + " returned " + Convert.ToString(v, CultureInfo.InvariantCulture) + ". The reason is in smsprov.log on the SMS Provider.");
        }

        public void AddToGroups(string packageId, List<string> groupIds)
        {
            ManagementScope s = w.Scope();
            foreach (string gid in groupIds)
            {
                using (ManagementObject g = Instance(s, "SMS_DistributionPointGroup", "GroupID", gid))
                {
                    ManagementBaseObject inp = g.GetMethodParameters("AddPackages");
                    inp["PackageIDs"] = new string[] { packageId };
                    try { Check(g.InvokeMethod("AddPackages", inp, null), "AddPackages"); }
                    catch (ManagementException ex) { throw SccmWmi.Failure(ex, "SMS_DistributionPointGroup.AddPackages"); }
                }
            }
        }

        public void RefreshPackage(string packageId)
        {
            using (ManagementObject o = Instance(w.Scope(), "SMS_Package", "PackageID", packageId))
            {
                try { Check(o.InvokeMethod("RefreshPkgSource", null, null), "RefreshPkgSource"); }
                catch (ManagementException ex) { throw SccmWmi.Failure(ex, "SMS_Package.RefreshPkgSource"); }
            }
        }

        // GetSequence gives the SMS_TaskSequence object of the package, its Steps array is changed, and SetSequence puts it back.
        public void EditSequence(string id, List<TsEdit> edits)
        {
            ManagementScope s = w.Scope();
            using (ManagementObject pkg = Instance(s, "SMS_TaskSequencePackage", "PackageID", id))
            using (ManagementClass cls = new ManagementClass(s, new ManagementPath("SMS_TaskSequencePackage"), null))
            {
                try
                {
                    ManagementBaseObject inp = cls.GetMethodParameters("GetSequence");
                    inp["TaskSequencePackage"] = pkg;
                    ManagementBaseObject outp = cls.InvokeMethod("GetSequence", inp, null);
                    Check(outp, "GetSequence");
                    ManagementBaseObject ts = (ManagementBaseObject)outp["TaskSequence"];
                    foreach (TsEdit e in edits)
                    {
                        TsEdit edit = e;
                        if (edit.Insert) Walk(ts, edit.Path, 0, delegate (List<ManagementBaseObject> list) { list.Insert(Math.Min(edit.Index, list.Count), NewStep(s, edit.Spec)); });
                        else
                        {
                            int[] parent = new int[edit.Path.Length - 1];
                            Array.Copy(edit.Path, parent, parent.Length);
                            int at = edit.Path[edit.Path.Length - 1];
                            Walk(ts, parent, 0, delegate (List<ManagementBaseObject> list) { Fill(list[at], edit.Spec); });
                        }
                    }
                    ManagementBaseObject set = cls.GetMethodParameters("SetSequence");
                    set["TaskSequencePackage"] = pkg;
                    set["TaskSequence"] = ts;
                    Check(cls.InvokeMethod("SetSequence", set, null), "SetSequence");
                }
                catch (ManagementException ex) { throw SccmWmi.Failure(ex, "SMS_TaskSequencePackage.SetSequence"); }
            }
        }

        // ImportSequence builds the SMS_TaskSequence object from the XML of the copy, and SetSequence puts it in the package.
        public void RestoreSequence(string id, string xml)
        {
            ManagementScope s = w.Scope();
            using (ManagementObject pkg = Instance(s, "SMS_TaskSequencePackage", "PackageID", id))
            using (ManagementClass cls = new ManagementClass(s, new ManagementPath("SMS_TaskSequencePackage"), null))
            {
                try
                {
                    ManagementBaseObject imp = cls.GetMethodParameters("ImportSequence");
                    imp["SequenceXML"] = xml;
                    ManagementBaseObject outp = cls.InvokeMethod("ImportSequence", imp, null);
                    Check(outp, "ImportSequence");
                    ManagementBaseObject set = cls.GetMethodParameters("SetSequence");
                    set["TaskSequencePackage"] = pkg;
                    set["TaskSequence"] = outp["TaskSequence"];
                    Check(cls.InvokeMethod("SetSequence", set, null), "SetSequence");
                }
                catch (ManagementException ex) { throw SccmWmi.Failure(ex, "SMS_TaskSequencePackage.SetSequence"); }
            }
        }

        static List<ManagementBaseObject> StepsOf(ManagementBaseObject owner)
        {
            List<ManagementBaseObject> l = new List<ManagementBaseObject>();
            Array a = owner["Steps"] as Array;
            if (a != null) foreach (object o in a) l.Add((ManagementBaseObject)o);
            return l;
        }

        // Goes down the groups of path and runs edit on the list of steps there. Each list is set again on the way up,
        // as an embedded object is a copy.
        static void Walk(ManagementBaseObject owner, int[] path, int depth, Action<List<ManagementBaseObject>> edit)
        {
            List<ManagementBaseObject> list = StepsOf(owner);
            if (depth == path.Length) edit(list);
            else Walk(list[path[depth]], path, depth + 1, edit);
            owner["Steps"] = list.ToArray();
        }

        static ManagementBaseObject NewStep(ManagementScope s, TsStepSpec spec)
        {
            using (ManagementClass c = new ManagementClass(s, new ManagementPath("SMS_TaskSequence_RunCommandLineAction"), null))
            {
                ManagementObject step = c.CreateInstance();
                Fill(step, spec);
                step["SuccessCodes"] = "0 3010";
                step["DisableWow64Redirection"] = false;
                return step;
            }
        }

        static void Fill(ManagementBaseObject step, TsStepSpec spec)
        {
            step["Name"] = spec.Name;
            step["Description"] = spec.Description;
            step["CommandLine"] = spec.CommandLine;
            step["PackageID"] = spec.PackageId;
            step["SupportedEnvironment"] = spec.Environment;
            step["ContinueOnError"] = spec.ContinueOnError;
            step["Enabled"] = true;
        }
    }
}
