namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Text.RegularExpressions;

    // One sign-in of the setup: what it is for, which account signs in, and the role and consent it needs.
    public sealed class SignInNeed
    {
        public string Id, Used, Account, Role, Consent, Who;
    }

    // One line of the checklist at the top of step 1 of Add customer.
    public sealed class NeedItem
    {
        public string Id, Text;
    }

    // What Add customer, Your storage and the guide with links ask of the accounts that sign in. The table is the one in
    // docs/AGENT.md. The scopes come from the code that signs in, so a test can compare them.
    public static class SetupNeeds
    {
        public const string ConsentWho = "A Global Administrator, Privileged Role Administrator, Cloud Application Administrator or Application Administrator of the tenant";
        public const string AzureWho = "An Owner of the subscription";
        public const string ConsentWhere = "In the Microsoft Entra admin center, open Enterprise apps, the app and Permissions, and choose Grant admin consent.";
        public const string IntuneRole = "Intune Administrator, or an Intune role that can add and assign platform scripts";
        public const string IntuneRoleWho = "A Global Administrator or Privileged Role Administrator gives the Microsoft Entra role Intune Administrator (Roles and administrators in the Microsoft Entra admin center). An Intune Role Administrator gives an Intune role (Tenant administration > Roles in the Intune admin center).";
        public const string GroupRole = "the right to make groups, which every user has unless the tenant limited it, or the Groups Administrator role";

        static readonly SignInNeed[] table = new SignInNeed[]
        {
            Need("storage-new", "Your storage, a new account", "Your Azure account. It signs in with the public client of Azure PowerShell",
                "Contributor or Owner on the subscription", "None to ask for", AzureWho + " gives the role: in the Azure portal, open the subscription, then Access control (IAM) and Add role assignment"),
            Need("storage-existing", "Your storage, an existing account", "Your Azure account",
                "Storage Account Contributor on the storage account", "None to ask for", AzureWho + " or of the resource group gives the role: in the Azure portal, open the storage account, then Access control (IAM) and Add role assignment"),
            Need("storage-key", "Add customer and Publish when the account key is not kept", "Your Azure account",
                "Storage Account Contributor on Your storage account, to read its key", "None to ask for", AzureWho + " gives the role, as above"),
            Need("intune-add", "Add customer, the Intune part", "An account in the customer's tenant",
                IntuneRole + ". To make the group: " + GroupRole + ", and Microsoft Entra ID P1 in the tenant for a dynamic group",
                "Admin consent for the delegated permissions DeviceManagementScripts.ReadWrite.All and Group.ReadWrite.All", ConsentWho + " gives the consent. " + IntuneRoleWho),
            Need("intune-code", "Customer code, Update the script in Intune", "An account in the customer's tenant",
                IntuneRole, "Admin consent for DeviceManagementScripts.ReadWrite.All", ConsentWho + " gives the consent. " + IntuneRoleWho),
            Need("own-azure", "Add customer with the customer's own Azure", "An account in the customer's tenant",
                "Contributor on a subscription or resource group for a new account, or Storage Account Contributor on an existing one", "None to ask for", "An Owner of the customer's subscription gives the role"),
            Need("links-guide", "The guide with links, the Intune part", "An account in the customer's tenant",
                IntuneRole, "Admin consent for DeviceManagementScripts.ReadWrite.All and Group.ReadBasic.All", ConsentWho + " gives the consent. " + IntuneRoleWho),
        };

        static SignInNeed Need(string id, string used, string account, string role, string consent, string who)
        {
            SignInNeed n = new SignInNeed();
            n.Id = id;
            n.Used = used;
            n.Account = account;
            n.Role = role;
            n.Consent = consent;
            n.Who = who;
            return n;
        }

        public static List<SignInNeed> All()
        {
            return new List<SignInNeed>(table);
        }

        public static SignInNeed Find(string id)
        {
            foreach (SignInNeed n in table) if (n.Id == id) return n;
            return null;
        }

        // The checklist of step 1. ownAzure is the customer's own Azure, askKey is Your storage without a kept key.
        public static List<NeedItem> Checklist(bool ownAzure, bool askKey)
        {
            List<NeedItem> l = new List<NeedItem>();
            if (ownAzure) l.Add(Item("azure", "An account in the customer's tenant with Contributor on a subscription or resource group of theirs, or Storage Account Contributor on a storage account that exists."));
            else if (askKey) l.Add(Item("key", "Your Azure account with Storage Account Contributor on Your storage account. You chose to sign in to Azure each time."));
            l.Add(Item("intune-role", "An account in the customer's tenant with the Intune Administrator role, or an Intune role that can add and assign platform scripts. Or skip Intune in step 2 and add the script yourself."));
            l.Add(Item("consent", "Admin consent in the customer's tenant for DeviceManagementScripts.ReadWrite.All and Group.ReadWrite.All. If you cannot give it, ask a Global Administrator, Privileged Role Administrator, Cloud Application Administrator or Application Administrator."));
            l.Add(Item("group", "To make the group TrackVice - Autopilot devices: the right to make groups, and Microsoft Entra ID P1 in the tenant. Or assign the script to a group that exists, or to none."));
            l.Add(Item("browser", "A browser on this PC for each sign-in. TrackVice shows a code to enter there."));
            return l;
        }

        // The sentence that Getting started adds to a step: what its sign-in needs. Empty for the other steps.
        public static string StepNeeds(string stepId)
        {
            if (stepId == GettingStarted.Storage) return " Needs an Azure account with Contributor on the subscription, or Storage Account Contributor on an existing account.";
            if (stepId == GettingStarted.Customer) return " For Intune it needs an account in the tenant with the Intune Administrator role and admin consent for the platform script and the group. You can skip Intune.";
            return "";
        }

        // The checklist of Your storage.
        public static List<NeedItem> StorageChecklist()
        {
            List<NeedItem> l = new List<NeedItem>();
            l.Add(Item("subscription", "An Azure subscription of your own. The cost is small: a session is a few hundred kilobytes per device."));
            l.Add(Item("new", "For a new storage account: your Azure account with Contributor or Owner on the subscription. It makes the resource group and the account there."));
            l.Add(Item("existing", "For an account that exists: Storage Account Contributor on that account. TrackVice reads its key and makes the container."));
            l.Add(Item("browser", "A browser on this PC. TrackVice shows a code to enter there. If the tenant blocks the Azure PowerShell sign-in, the message says so."));
            return l;
        }

        // The checklist of the guide with links.
        public static List<NeedItem> LinksChecklist()
        {
            List<NeedItem> l = new List<NeedItem>();
            l.Add(Item("azure", "For the storage account: your Azure account with Contributor or Owner on a subscription, or Storage Account Contributor on an account that exists."));
            l.Add(Item("intune-role", "Only for the platform script in step 6: an account in the customer's tenant with the Intune Administrator role, or an Intune role that can add and assign platform scripts."));
            l.Add(Item("consent", "Also for step 6: admin consent in the customer's tenant for DeviceManagementScripts.ReadWrite.All and Group.ReadBasic.All. A Global Administrator, Privileged Role Administrator, Cloud Application Administrator or Application Administrator can give it."));
            l.Add(Item("browser", "A browser on this PC for each sign-in. TrackVice shows a code to enter there."));
            return l;
        }

        static NeedItem Item(string id, string text)
        {
            NeedItem i = new NeedItem();
            i.Id = id;
            i.Text = text;
            return i;
        }

        public const string BoxIntro = "Each sign-in asks for an account with a role, and for consent in the tenant. Tick what you have. Signing in changes nothing.";
    }

    // Plain messages for the answers that mean a role or a consent is missing. They say which one, and who can give it.
    public static class SetupErrors
    {
        public enum GraphUse { Scripts, Groups, AppRegistration, ServicePrincipal }

        // ---------- Azure Resource Manager ----------

        // A message for an Azure answer that is about rights, or null when the answer is another kind of problem.
        public static string Azure(int status, string code, string message, string doing)
        {
            string c = code ?? "";
            string said = message != null && message.Trim().Length > 0 ? " Azure said: " + Txt.Short(message.Trim(), 240) : "";
            if (c == "LinkedAuthorizationFailed")
                return "Your account may not " + doing + ". The resource is in another tenant than your account. Sign in with an account of that tenant: enter its tenant ID or domain in Azure tenant." + said;
            if (c == "RequestDisallowedByPolicy")
                return "An Azure Policy of the subscription does not allow this: " + doing + ". Pick another region or resource group, or ask the Owner of the subscription which policy applies. Azure portal > Policy > Compliance shows it." + said;
            if (c == "MissingSubscriptionRegistration" || c == "SubscriptionNotRegistered")
                return "The subscription is not registered for storage accounts. An Owner or Contributor of the subscription can register it: Azure portal > Subscriptions > the subscription > Resource providers > Microsoft.Storage > Register." + said;
            if (c == "SubscriptionNotFound")
                return "Your account cannot use this subscription. It may have no role on it, or the sign-in is for another tenant. Check Azure tenant and the role on the subscription." + said;
            if (c == "InvalidAuthenticationTokenTenant")
                return "The Azure sign-in is for another tenant than the resource. Enter the tenant of the subscription in Azure tenant and sign in again." + said;
            if (status == 403 || c == "AuthorizationFailed")
            {
                bool sub = AtSubscription(doing);
                string role = sub ? "Contributor (or Owner) on the subscription" : "Storage Account Contributor on the storage account";
                string who = sub ? "An Owner of the subscription" : "An Owner of the storage account, its resource group or the subscription";
                string where = sub ? "open the subscription" : "open the storage account";
                return "Your account may not " + doing + ". It needs the Azure role " + role + ". " + who + " can give it: in the Azure portal, " + where + ", then Access control (IAM) and Add role assignment. A role given a minute ago can take a few minutes to work." + said;
            }
            return null;
        }

        // Making the resource group and the account, and checking the name, are done on the subscription.
        static bool AtSubscription(string doing)
        {
            string d = (doing ?? "").ToLowerInvariant();
            return d.StartsWith("create the resource group", StringComparison.Ordinal)
                || d.StartsWith("create the storage account", StringComparison.Ordinal)
                || d.StartsWith("check the storage account name", StringComparison.Ordinal)
                || d.StartsWith("list the", StringComparison.Ordinal);
        }

        // ---------- Microsoft Graph ----------

        public static string Graph(int status, string code, string message, string doing, GraphUse use)
        {
            string c = code ?? "";
            string m = message ?? "";
            bool denied = status == 403 || c == "Forbidden" || c == "Authorization_RequestDenied" || c == "accessDenied";
            if (!denied)
            {
                if (use == GraphUse.Groups && status == 400 && (m.IndexOf("license", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("premium", StringComparison.OrdinalIgnoreCase) >= 0))
                    return "The tenant cannot make a dynamic group: it needs Microsoft Entra ID P1 (or Intune for Education). Choose Assign to a group I choose or Do not assign in step 3. (" + Txt.Short(m.Trim(), 160) + ")";
                return null;
            }
            string said = m.Trim().Length > 0 ? " (" + Txt.Short(m.Trim(), 200) + ")" : "";
            // A token that lacks the permission is refused with the scopes the call accepts.
            if (m.IndexOf("scope", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Access denied: the account may not " + doing + ". The sign-in lacks the permission " + Permission(use) + ". " + SetupNeeds.ConsentWho + " can give admin consent. " + SetupNeeds.ConsentWhere + " Then sign in again." + said;
            switch (use)
            {
                case GraphUse.Scripts:
                    return "Access denied: the account may not " + doing + ". It needs the role " + IntuneRoleText + ", and consent for DeviceManagementScripts.ReadWrite.All. " + SetupNeeds.IntuneRoleWho + " " + SetupNeeds.ConsentWho + " gives the consent." + said;
                case GraphUse.Groups:
                    return "Access denied: the account may not " + doing + ". It needs " + SetupNeeds.GroupRole + ", and consent for Group.ReadWrite.All. A Groups Administrator can make the group, or choose Assign to a group I choose in step 3. " + SetupNeeds.ConsentWho + " gives the consent." + said;
                case GraphUse.AppRegistration:
                    return "Access denied: the account may not " + doing + ". It needs the Application Developer, Application Administrator or Cloud Application Administrator role, or the tenant must let users register applications. A Global Administrator or Privileged Role Administrator gives the role, in Roles and administrators in the Microsoft Entra admin center." + said;
                default:
                    return "Access denied: the account may not " + doing + ". It needs the Application Administrator or Cloud Application Administrator role. A Global Administrator or Privileged Role Administrator gives the role, in Roles and administrators in the Microsoft Entra admin center." + said;
            }
        }

        const string IntuneRoleText = "Intune Administrator, or an Intune role that can add and assign platform scripts";

        static string Permission(GraphUse use)
        {
            switch (use)
            {
                case GraphUse.Scripts: return "DeviceManagementScripts.ReadWrite.All";
                case GraphUse.Groups: return "Group.ReadWrite.All";
                default: return "Application.ReadWrite.All";
            }
        }

        // ---------- the sign-in ----------

        public static string AppName(string clientId)
        {
            if (string.Equals(clientId, Arm.ClientId, StringComparison.OrdinalIgnoreCase)) return "Azure PowerShell";
            if (string.Equals(clientId, GraphClient.DefaultClientId, StringComparison.OrdinalIgnoreCase)) return "Microsoft Graph Command Line Tools";
            return "the app registration " + clientId;
        }

        // The permissions in a scope list, without the address and the ones that need no consent.
        public static string Permissions(string scopes)
        {
            List<string> l = new List<string>();
            foreach (string s in (scopes ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = s.LastIndexOf('/');
                string p = i >= 0 ? s.Substring(i + 1) : s;
                if (p == "offline_access" || p == "user_impersonation" || p == "openid" || p == "profile") continue;
                if (!l.Contains(p)) l.Add(p);
            }
            return string.Join(", ", l.ToArray());
        }

        // The AADSTS number in an error text, or 0.
        public static int Aadsts(string description)
        {
            Match m = Regex.Match(description ?? "", "AADSTS(\\d+)");
            int n;
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        // A message for a sign-in that the tenant or the app refused, or null for any other error. error is the OAuth error,
        // description Microsoft's text. app is what the person signs in with, perms the permissions it asks for.
        public static string SignIn(string error, string description, string app, string perms)
        {
            int n = Aadsts(description);
            string e = error ?? "";
            string tag = n > 0 ? " Microsoft's code: AADSTS" + n.ToString(CultureInfo.InvariantCulture) + "." : "";
            if (n == 65001 || n == 90094 || e == "consent_required")
            {
                string what = perms != null && perms.Length > 0 ? " for " + perms : "";
                return "The tenant has not given consent" + what + " to " + app + ". " + SetupNeeds.ConsentWho + " can give it. " + SetupNeeds.ConsentWhere + " Then sign in again." + tag;
            }
            if (n == 65004) return "The consent was declined in the browser. Sign in again and accept it, or ask an administrator to give admin consent." + tag;
            if (n == 700016) return "The tenant does not know " + app + ". Check the Client ID in Settings and the tenant. An app registration made in another tenant cannot be used here." + tag;
            if (n == 7000218) return app + " is not set as a public client, so the sign-in with a code is refused. In the Microsoft Entra admin center, open the app registration, then Authentication, Advanced settings, set Allow public client flows to Yes and save." + tag;
            if (n == 53003) return "A Conditional Access policy of the tenant blocked this sign-in. An administrator can find the policy in the sign-in logs of the Microsoft Entra admin center, and allow " + app + " or this PC." + tag;
            if (n == 53000) return "A Conditional Access policy of the tenant needs a compliant device for this sign-in, and this PC is not one." + tag;
            if (n == 50076 || n == 50079) return "The tenant asks for multifactor authentication. Finish it in the browser. If it asks you to register a method, do that first and sign in again." + tag;
            if (n == 50105) return "The account is not assigned to " + app + ", and the app needs an assignment. An administrator can add the account under Enterprise apps, the app and Users and groups." + tag;
            if (n == 70011) return app + " does not know one of the permissions asked for" + (perms != null && perms.Length > 0 ? " (" + perms + ")" : "") + ". For an app registration, add it under API permissions." + tag;
            if (n == 50020) return "The account is not a user of this tenant. Sign in with an account of the tenant, or set Tenant to the tenant the account belongs to." + tag;
            if (n == 90002 || n == 50059) return "The tenant was not found. Check the tenant ID or domain. Empty uses the tenant of the account you sign in with." + tag;
            return null;
        }
    }
}
