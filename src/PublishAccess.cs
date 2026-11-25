namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    public enum AccessOutcome
    {
        // A link with Read, Create and Write was made and saved.
        Saved,
        // The container is not there: ask, then call again with createContainer.
        AskContainer,
        // The account key cannot be used: Message says why, and how to make the link by hand.
        Manual,
        // No subscription of the account has the storage account.
        NotFound
    }

    public sealed class AccessResult
    {
        public AccessOutcome Outcome;
        public string Message = "";
        public string Link = "";
        public AzureAccount Account;
        public AzureSubscription Subscription;
        public DateTime ExpiresUtc;
    }

    // Gets a link with Read, Create and Write for an update location this PC cannot write to, from the signed-in Azure account.
    // Only reads, except for creating the container when the person agreed. The account key is used to sign the link and is not kept.
    public static class PublishAccess
    {
        public const int Days = 365;
        public const string Perms = "rcw";

        // The steps of the portal, with the account and the container filled in.
        public static string ManualSteps(string account, string container, bool keyOff)
        {
            string s = "1. Azure portal > Storage accounts > " + account + " > Containers > " + container + ".\n"
                + "2. Shared access tokens.\n"
                + "3. Permissions: tick Read, Create and Write.\n"
                + "4. Expiry: a date a year or more ahead.\n"
                + "5. Generate SAS token and URL, copy the Blob SAS URL (the whole link, not the Blob SAS token) and paste it here.";
            if (keyOff) s += "\nThe account does not allow account key access, so choose Signing method: User delegation key (you need Storage Blob Data Contributor on the account).";
            return s;
        }

        public static string ManualSteps(UpdateLocation l)
        {
            return ManualSteps(l.Account, l.Container, false);
        }

        static string Role(AzureAccount a, AzureSubscription s, string who, string doing)
        {
            return "Your account" + (string.IsNullOrEmpty(who) ? "" : " (" + who + ")") + " cannot " + doing + ". The Azure role missing is Storage Account Contributor (Owner of the account also works) on the storage account " + a.Name
                + ", scope " + a.Id + (s != null ? " in the subscription " + s.Name : "") + ". "
                + "In the Azure portal: Storage accounts > " + a.Name + " > Access control (IAM) > Add role assignment > Storage Account Contributor > your account. "
                + "A role given a minute ago can take a few minutes to work, and a role you get through Privileged Identity Management must be activated first. Nothing was changed in Azure.";
        }

        // Finds the account in the subscriptions, and makes and saves the link. Never throws for what Azure answers.
        // save keeps the link for the customers of the place, encrypted. It can be null.
        public static AccessResult Get(Arm arm, string who, UpdateLocation place, bool createContainer, DateTime nowUtc, Action<UpdateLocation, string> save)
        {
            AccessResult res = new AccessResult();
            try
            {
                List<AzureSubscription> subs = arm.Subscriptions();
                int unreadable = 0;
                foreach (AzureSubscription s in subs)
                {
                    List<AzureAccount> accts;
                    try { accts = arm.StorageAccounts(s.Id); }
                    catch (SetupException) { unreadable++; continue; }
                    foreach (AzureAccount a in accts)
                        if (string.Equals(a.Name, place.Account, StringComparison.OrdinalIgnoreCase)) { res.Account = a; res.Subscription = s; break; }
                    if (res.Account != null) break;
                }
                if (res.Account == null)
                {
                    res.Outcome = AccessOutcome.NotFound;
                    res.Message = "The storage account " + place.Account + " is not in any of the " + subs.Count + (subs.Count == 1 ? " subscription" : " subscriptions") + " that " + (string.IsNullOrEmpty(who) ? "this account" : who) + " can see"
                        + (unreadable > 0 ? " (" + unreadable + " could not be read)" : "") + ". It may be in another Azure tenant: sign in with an account of that tenant (Settings > Your storage > Azure tenant), or this account has no role on it. Nothing was changed.\n"
                        + "Or make the link by hand:\n" + ManualSteps(place.Account, place.Container, false);
                    return res;
                }
                AzureAccount acct = res.Account;
                if (acct.SharedKeyOff)
                {
                    res.Outcome = AccessOutcome.Manual;
                    res.Message = "The storage account " + acct.Name + " does not allow account key access (Allow storage account key access is off), so TrackVice cannot sign a link with its key, and it does not change that. Make the link by hand:\n" + ManualSteps(acct.Name, place.Container, true);
                    return res;
                }
                bool there = true;
                try { there = arm.ContainerExists(acct, place.Container); }
                catch (SetupException ex) { if (ex.Status != 403) throw; }
                if (!there)
                {
                    if (!createContainer)
                    {
                        res.Outcome = AccessOutcome.AskContainer;
                        res.Message = "The container " + place.Container + " is not in " + acct.Name + " yet. Create it? It is made private, with no public access.";
                        return res;
                    }
                    try { arm.CreateContainer(acct, place.Container); }
                    catch (SetupException ex)
                    {
                        if (ex.Status != 403) throw;
                        res.Outcome = AccessOutcome.Manual;
                        res.Message = Role(acct, res.Subscription, who, "create the container " + place.Container) + "\nOr make the container in the portal and the link by hand:\n" + ManualSteps(acct.Name, place.Container, false);
                        return res;
                    }
                }
                string key;
                try { key = arm.Key(acct); }
                catch (SetupException ex)
                {
                    if (ex.Status != 403) throw;
                    res.Outcome = AccessOutcome.Manual;
                    res.Message = Role(acct, res.Subscription, who, "read the account key of " + acct.Name) + "\nOr make the link by hand:\n" + ManualSteps(acct.Name, place.Container, false);
                    return res;
                }
                DateTime end = nowUtc.AddDays(Days);
                // A few minutes back, so a PC whose clock is slightly behind can use the link at once.
                res.Link = SasLinks.Container(acct.BlobEndpoint, acct.Name, place.Container, Perms, nowUtc.AddMinutes(-15), end, key);
                res.ExpiresUtc = end;
                UpdateLocations.UseWriteLink(place, res.Link);
                if (save != null) save(place, res.Link);
                res.Outcome = AccessOutcome.Saved;
                res.Message = "Got access to " + acct.Name + "/" + place.Container + ": a link with Read, Create and Write, valid until " + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ", saved encrypted for your Windows account.";
                return res;
            }
            catch (SetupException ex)
            {
                res.Outcome = AccessOutcome.Manual;
                res.Message = ex.Message + "\nOr make the link by hand:\n" + ManualSteps(place.Account, place.Container, false);
                return res;
            }
        }
    }
}
