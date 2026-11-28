namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;

    public enum TargetKind { Group, Exclusion, AllUsers, AllDevices }

    // One assignment of the platform script.
    public sealed class ScriptAssignment
    {
        public TargetKind Kind;
        public string GroupId = "";
        // The target type Graph reported, kept so that a change sends the same type back.
        public string OdataType = "";
    }

    // The settings of the platform script that matter for who runs it.
    public sealed class ScriptSettings
    {
        public string Id = "", Name = "", RunAsAccount = "";
        public bool EnforceSignatureCheck, RunAs32Bit;
    }

    // What is known of a group an assignment points to. Problem is set when the group could not be read.
    public sealed class GroupFacts
    {
        public string Id = "", Name = "", Rule = "", Problem = "";
        public bool Dynamic;
        // Null: not known.
        public bool? HasUsers;
    }

    public sealed class ScriptCheck
    {
        public readonly List<string> Lines = new List<string>();
        public bool RunsAsUser;
        // The assignments that run the script for users, and the ones that stay when they go.
        public readonly List<ScriptAssignment> UserTargets = new List<ScriptAssignment>();
        public readonly List<ScriptAssignment> Keep = new List<ScriptAssignment>();
        public readonly List<string> KeepNames = new List<string>();
        public readonly List<string> RemoveNames = new List<string>();
        public string ScriptId = "";

        public bool UsersTargeted { get { return UserTargets.Count > 0; } }

        // The fix needs a device assignment to stay, or the script would be assigned to nothing.
        public bool CanFix { get { return UsersTargeted && KeepNames.Count > 0; } }

        public string FixButton { get { return "Assign only to " + string.Join(", ", KeepNames.ToArray()); } }

        public string FixQuestion
        {
            get { return "Change the assignments of the platform script " + AddCustomerRun.ScriptName + "?\n\nRemoved: " + string.Join(", ", RemoveNames.ToArray()) + "\nKept: " + string.Join(", ", KeepNames.ToArray()); }
        }

        public string Text { get { return string.Join("\n", Lines.ToArray()); } }
    }

    public static class ScriptChecker
    {
        public const string UsersLine = "The script also runs when a user signs in. Assign it only to a device group.";
        public const string AllUsersName = "All users";
        public const string AllDevicesName = "All devices";

        // True when a dynamic membership rule picks users. Null when the rule is empty or names neither.
        public static bool? RuleHasUsers(string rule)
        {
            if (string.IsNullOrEmpty(rule) || rule.Trim().Length == 0) return null;
            if (Regex.IsMatch(rule, @"(^|[^A-Za-z0-9_.])user\.", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(rule, @"(^|[^A-Za-z0-9_.])device\.", RegexOptions.IgnoreCase)) return false;
            return null;
        }

        static GroupFacts FactsOf(Dictionary<string, GroupFacts> groups, string id)
        {
            GroupFacts g;
            if (groups != null && groups.TryGetValue(id, out g)) return g;
            g = new GroupFacts();
            g.Id = id;
            g.Name = id;
            g.Problem = "the group was not read";
            return g;
        }

        public static string RunsAs(string runAsAccount)
        {
            return string.Equals(runAsAccount, "user", StringComparison.OrdinalIgnoreCase) ? "the signed-in user" : string.Equals(runAsAccount, "system", StringComparison.OrdinalIgnoreCase) ? "System" : string.IsNullOrEmpty(runAsAccount) ? "not known" : runAsAccount;
        }

        // The findings for the script, from its settings, its assignments and the groups they point to. Nothing is read or changed here.
        public static ScriptCheck Evaluate(ScriptSettings s, List<ScriptAssignment> assignments, Dictionary<string, GroupFacts> groups)
        {
            ScriptCheck c = new ScriptCheck();
            c.ScriptId = s.Id;
            c.RunsAsUser = string.Equals(s.RunAsAccount, "user", StringComparison.OrdinalIgnoreCase);
            c.Lines.Add("Runs as: " + RunsAs(s.RunAsAccount) + ".");
            if (c.RunsAsUser) c.Lines.Add("Run this script using the logged on credentials is on. Update the script in Intune sets it to System.");
            if (s.EnforceSignatureCheck) c.Lines.Add("The script signature is checked, and the script is not signed. Update the script in Intune turns the check off.");
            if (s.RunAs32Bit) c.Lines.Add("The script runs in a 32-bit PowerShell host. Update the script in Intune turns this off.");
            if (assignments.Count == 0) c.Lines.Add("Assigned to: nothing. Devices do not get the script.");
            foreach (ScriptAssignment a in assignments)
            {
                switch (a.Kind)
                {
                    case TargetKind.AllUsers:
                        c.Lines.Add("Assigned to: " + AllUsersName + " (users).");
                        c.UserTargets.Add(a);
                        c.RemoveNames.Add(AllUsersName);
                        break;
                    case TargetKind.AllDevices:
                        c.Lines.Add("Assigned to: " + AllDevicesName + " (devices only).");
                        c.Keep.Add(a);
                        c.KeepNames.Add(AllDevicesName);
                        break;
                    case TargetKind.Exclusion:
                        c.Lines.Add("Excluded: " + FactsOf(groups, a.GroupId).Name + ".");
                        c.Keep.Add(a);
                        break;
                    default:
                        GroupFacts g = FactsOf(groups, a.GroupId);
                        if (g.HasUsers == true)
                        {
                            c.Lines.Add("Assigned to: " + g.Name + " (contains users).");
                            c.UserTargets.Add(a);
                            c.RemoveNames.Add(g.Name);
                        }
                        else if (g.HasUsers == false)
                        {
                            c.Lines.Add("Assigned to: " + g.Name + " (devices only).");
                            c.Keep.Add(a);
                            c.KeepNames.Add(g.Name);
                        }
                        else
                        {
                            c.Lines.Add("Assigned to: " + g.Name + " (could not tell if it contains users" + (g.Problem.Length > 0 ? ": " + g.Problem : "") + ").");
                            c.Keep.Add(a);
                            c.KeepNames.Add(g.Name);
                        }
                        break;
                }
            }
            if (c.UsersTargeted) c.Lines.Add(UsersLine);
            else if (assignments.Count > 0 && !c.RunsAsUser) c.Lines.Add("The script runs for the device only.");
            return c;
        }

        // The body of the assign action for the assignments that stay.
        public static Dictionary<string, object> AssignBody(List<ScriptAssignment> keep)
        {
            List<object> l = new List<object>();
            foreach (ScriptAssignment a in keep)
            {
                Dictionary<string, object> t;
                if (a.Kind == TargetKind.AllDevices) t = SetupJson.Obj("@odata.type", "#microsoft.graph.allDevicesAssignmentTarget");
                else if (a.Kind == TargetKind.AllUsers) t = SetupJson.Obj("@odata.type", "#microsoft.graph.allLicensedUsersAssignmentTarget");
                else t = SetupJson.Obj("@odata.type", a.Kind == TargetKind.Exclusion ? "#microsoft.graph.exclusionGroupAssignmentTarget" : "#microsoft.graph.groupAssignmentTarget", "groupId", a.GroupId);
                l.Add(SetupJson.Obj("target", t));
            }
            return SetupJson.Obj("deviceManagementScriptAssignments", l);
        }

        public static ScriptAssignment Parse(Dictionary<string, object> target)
        {
            string type = Http.Str(target, "@odata.type") ?? "";
            ScriptAssignment a = new ScriptAssignment();
            a.OdataType = type;
            a.GroupId = Http.Str(target, "groupId") ?? "";
            if (type.EndsWith("allLicensedUsersAssignmentTarget", StringComparison.OrdinalIgnoreCase)) a.Kind = TargetKind.AllUsers;
            else if (type.EndsWith("allDevicesAssignmentTarget", StringComparison.OrdinalIgnoreCase)) a.Kind = TargetKind.AllDevices;
            else if (type.IndexOf("exclusion", StringComparison.OrdinalIgnoreCase) >= 0) a.Kind = TargetKind.Exclusion;
            else a.Kind = TargetKind.Group;
            return a;
        }
    }

    // Reads the platform script, its assignments and the groups from Microsoft Graph. The script needs DeviceManagementScripts.ReadWrite.All;
    // the groups need GroupMember.Read.All. Without a token for the groups the answer says so and the rest is still checked.
    public sealed class ScriptCheckApi
    {
        public const string GroupsUrl = "https://graph.microsoft.com/v1.0/groups/";
        public const string NoGroupPermission = "TrackVice has no permission to read groups. GroupMember.Read.All needs admin consent in the customer's tenant";

        readonly IWeb web;
        readonly Func<string> scriptToken, groupToken;

        // groupToken may be null, or throw, when the sign-in has no token for GroupMember.Read.All.
        public ScriptCheckApi(IWeb web, Func<string> scriptToken, Func<string> groupToken)
        {
            this.web = web;
            this.scriptToken = scriptToken;
            this.groupToken = groupToken;
        }

        public static string FindUrl(string name)
        {
            return IntuneScripts.ScriptsUrl + "?$filter=" + Uri.EscapeDataString("displayName eq '" + (name ?? "").Replace("'", "''") + "'") + "&$select=id,displayName,runAsAccount,enforceSignatureCheck,runAs32Bit";
        }

        static List<Dictionary<string, object>> Values(WebReply r)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            List<object> v = ImeJson.Get(r.Json, "value") as List<object>;
            if (v != null) foreach (object o in v) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) l.Add(d); }
            return l;
        }

        WebReply Get(string url, Func<string> token, bool eventual)
        {
            WebCall c = WebCall.Json("GET", url, token(), null);
            if (eventual) c.Headers["ConsistencyLevel"] = "eventual";
            return web.Send(c);
        }

        // Null when the tenant has no script with this name.
        public ScriptSettings FindScript(string name)
        {
            WebReply r = Get(FindUrl(name), scriptToken, false);
            if (r.Status != 200) throw IntuneSetup.Fail(r, "read the platform scripts");
            foreach (Dictionary<string, object> d in Values(r))
            {
                if (!string.Equals(Http.Str(d, "displayName"), name, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(Http.Str(d, "id"))) continue;
                ScriptSettings s = new ScriptSettings();
                s.Id = Http.Str(d, "id");
                s.Name = Http.Str(d, "displayName");
                s.RunAsAccount = Http.Str(d, "runAsAccount") ?? "";
                object sig = ImeJson.Get(d, "enforceSignatureCheck"), b32 = ImeJson.Get(d, "runAs32Bit");
                s.EnforceSignatureCheck = sig is bool && (bool)sig;
                s.RunAs32Bit = b32 is bool && (bool)b32;
                return s;
            }
            return null;
        }

        public List<ScriptAssignment> Assignments(string id)
        {
            string url = IntuneScripts.ScriptsUrl + "/" + Uri.EscapeDataString(id);
            WebReply r = Get(url + "/assignments", scriptToken, false);
            if (r.Status != 200) throw IntuneSetup.Fail(r, "read the assignments of the platform script");
            List<ScriptAssignment> l = new List<ScriptAssignment>();
            foreach (Dictionary<string, object> d in Values(r))
            {
                Dictionary<string, object> t = ImeJson.Get(d, "target") as Dictionary<string, object>;
                if (t != null) l.Add(ScriptChecker.Parse(t));
            }
            // The older group assignments of the script. A group that is not in the list above is added; a refusal here is not an error.
            WebReply g = Get(url + "/groupAssignments", scriptToken, false);
            if (g.Status == 200)
                foreach (Dictionary<string, object> d in Values(g))
                {
                    string gid = Http.Str(d, "targetGroupId");
                    if (string.IsNullOrEmpty(gid)) continue;
                    bool there = false;
                    foreach (ScriptAssignment a in l) if (string.Equals(a.GroupId, gid, StringComparison.OrdinalIgnoreCase)) there = true;
                    if (!there) { ScriptAssignment a = new ScriptAssignment(); a.GroupId = gid; a.OdataType = "#microsoft.graph.groupAssignmentTarget"; l.Add(a); }
                }
            return l;
        }

        string GroupTokenOrNull()
        {
            if (groupToken == null) return null;
            try { return groupToken(); }
            catch (Exception) { return null; }
        }

        public GroupFacts Group(string id)
        {
            GroupFacts f = new GroupFacts();
            f.Id = id;
            f.Name = id;
            string tok = GroupTokenOrNull();
            if (tok == null) { f.Problem = NoGroupPermission; return f; }
            Func<string> t = delegate { return tok; };
            string url = GroupsUrl + Uri.EscapeDataString(id);
            WebReply r = Get(url + "?$select=displayName,membershipRule,groupTypes", t, false);
            if (r.Status == 403 || r.Status == 401) { f.Problem = NoGroupPermission; return f; }
            if (r.Status == 404) { f.Problem = "the group no longer exists"; return f; }
            if (r.Status != 200) { f.Problem = "Graph answered HTTP " + r.Status; return f; }
            f.Name = Http.Str(r.Json, "displayName") ?? id;
            f.Rule = Http.Str(r.Json, "membershipRule") ?? "";
            List<object> types = ImeJson.Get(r.Json, "groupTypes") as List<object>;
            if (types != null) foreach (object o in types) if (string.Equals(o as string, "DynamicMembership", StringComparison.OrdinalIgnoreCase)) f.Dynamic = true;
            if (f.Dynamic)
            {
                f.HasUsers = ScriptChecker.RuleHasUsers(f.Rule);
                if (f.HasUsers == null) f.Problem = "the membership rule names neither users nor devices";
                return f;
            }
            WebReply n = Get(url + "/members/microsoft.graph.user/$count", t, true);
            int count;
            if (n.Status == 200 && int.TryParse((n.Text ?? "").Trim().TrimStart('﻿'), out count)) { f.HasUsers = count > 0; return f; }
            if (n.Status == 403 || n.Status == 401) { f.Problem = NoGroupPermission; return f; }
            // The count is not there: ask for one user.
            WebReply m = Get(url + "/members/microsoft.graph.user?$top=1&$select=id", t, true);
            if (m.Status == 200) { f.HasUsers = Values(m).Count > 0; return f; }
            f.Problem = m.Status == 403 || m.Status == 401 ? NoGroupPermission : "the members could not be read (HTTP " + m.Status + ")";
            return f;
        }

        // The script by this name, checked. Throws a SetupException when the script is not there or Intune refuses.
        public ScriptCheck Check(string scriptName)
        {
            ScriptSettings s = FindScript(scriptName);
            if (s == null) throw new SetupException("This tenant has no platform script called " + scriptName + ". Add customer adds it.");
            List<ScriptAssignment> a = Assignments(s.Id);
            Dictionary<string, GroupFacts> groups = new Dictionary<string, GroupFacts>(StringComparer.OrdinalIgnoreCase);
            foreach (ScriptAssignment x in a)
                if ((x.Kind == TargetKind.Group || x.Kind == TargetKind.Exclusion) && x.GroupId.Length > 0 && !groups.ContainsKey(x.GroupId)) groups[x.GroupId] = Group(x.GroupId);
            ScriptCheck c = ScriptChecker.Evaluate(s, a, groups);
            return c;
        }

        // Replaces the assignments with the ones that stay. The assign action replaces them all.
        public void Fix(ScriptCheck c)
        {
            if (!c.CanFix) throw new SetupException("There is no device assignment to keep.");
            // The assign action replaces them all, so an assignment that cannot be sent back as it was stops the change.
            foreach (ScriptAssignment a in c.Keep)
                if ((a.Kind == TargetKind.Group || a.Kind == TargetKind.Exclusion) && a.GroupId.Length == 0)
                    throw new SetupException("The platform script has an assignment TrackVice cannot keep (" + (a.OdataType.Length > 0 ? a.OdataType : "no type") + "). Change the assignments in Intune.");
            WebReply r = web.Send(WebCall.Json("POST", IntuneScripts.ScriptsUrl + "/" + Uri.EscapeDataString(c.ScriptId) + "/assign", scriptToken(), SetupJson.Write(ScriptChecker.AssignBody(c.Keep))));
            if (r.Status != 204 && r.Status != 200) throw IntuneSetup.Fail(r, "assign the platform script");
        }
    }
}
