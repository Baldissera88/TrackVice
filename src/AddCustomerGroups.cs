namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // Where Add customer assigns the platform script.
    public enum ScriptTarget { NewGroup, ChosenGroup, None }

    // A security group of the customer's tenant, picked for the platform script.
    public sealed class TenantGroup
    {
        public string Id = "", Name = "";
        public bool Dynamic;

        public string Members
        {
            get { return Dynamic ? "Dynamic" : "Assigned"; }
        }

        public DirectoryGroup ToDirectoryGroup()
        {
            DirectoryGroup g = new DirectoryGroup();
            g.Id = Id;
            g.Name = Name;
            return g;
        }

        public override string ToString() { return Name; }
    }

    // Finds the security groups of the tenant by name with Microsoft Graph. Group.ReadWrite.All, which Add customer
    // signs in with, also allows listing groups.
    public static class GroupSearch
    {
        public const int Max = 50;

        // $search matches words in displayName. It, and $orderby with $filter, need ConsistencyLevel eventual and $count.
        public static string Url(string text)
        {
            string t = (text ?? "").Trim();
            string url = IntuneSetup.GroupsUrl + "?";
            if (t.Length > 0) url += "$search=" + Uri.EscapeDataString("\"displayName:" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"") + "&";
            return url + "$filter=" + Uri.EscapeDataString("securityEnabled eq true") + "&$select=id,displayName,groupTypes&$orderby=displayName&$count=true&$top=" + Max;
        }

        public static WebCall Call(string text, string token)
        {
            WebCall c = WebCall.Json("GET", Url(text), token, null);
            c.Headers["ConsistencyLevel"] = "eventual";
            return c;
        }

        public static List<TenantGroup> Parse(WebReply r)
        {
            List<TenantGroup> l = new List<TenantGroup>();
            List<object> v = ImeJson.Get(r.Json, "value") as List<object>;
            if (v == null) return l;
            foreach (object o in v)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null || string.IsNullOrEmpty(Http.Str(d, "id"))) continue;
                TenantGroup g = new TenantGroup();
                g.Id = Http.Str(d, "id");
                g.Name = Http.Str(d, "displayName") ?? g.Id;
                List<object> types = ImeJson.Get(d, "groupTypes") as List<object>;
                if (types != null) foreach (object t in types) if (string.Equals(t as string, "DynamicMembership", StringComparison.OrdinalIgnoreCase)) g.Dynamic = true;
                l.Add(g);
                if (l.Count == Max) break;
            }
            return l;
        }

        // Empty text lists the first groups by name.
        public static List<TenantGroup> Find(IWeb web, Func<string> token, string text)
        {
            WebReply r = web.Send(Call(text, token()));
            if (r.Status != 200) throw IntuneSetup.Fail(r, "search the groups");
            return Parse(r);
        }

        // Made-up groups for the demo.
        public const string DemoJson = "{\"value\":[" +
            "{\"id\":\"4f1a7c2e-8b3d-4e5f-9a6b-1c2d3e4f5a02\",\"displayName\":\"Autopilot devices - all\",\"groupTypes\":[\"DynamicMembership\"]}," +
            "{\"id\":\"4f1a7c2e-8b3d-4e5f-9a6b-1c2d3e4f5a01\",\"displayName\":\"Autopilot test devices\",\"groupTypes\":[]}," +
            "{\"id\":\"4f1a7c2e-8b3d-4e5f-9a6b-1c2d3e4f5a03\",\"displayName\":\"Intune pilot ring\",\"groupTypes\":[]}," +
            "{\"id\":\"4f1a7c2e-8b3d-4e5f-9a6b-1c2d3e4f5a04\",\"displayName\":\"Kiosk devices\",\"groupTypes\":[\"DynamicMembership\"]}," +
            "{\"id\":\"4f1a7c2e-8b3d-4e5f-9a6b-1c2d3e4f5a05\",\"displayName\":\"Service desk devices\",\"groupTypes\":[]}," +
            "{\"id\":\"4f1a7c2e-8b3d-4e5f-9a6b-1c2d3e4f5a06\",\"displayName\":\"Windows 11 pilot\",\"groupTypes\":[]}]}";
    }
}
