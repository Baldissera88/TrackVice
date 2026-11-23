namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // What the Get started page looks at. Each value comes from real state on this PC, so a test can set it.
    public sealed class GetStartedState
    {
        public bool StorageSet, StorageExpired;
        // The version published to Your storage is known, and this TrackVice is newer than it.
        public bool PublishedKnown, PublishedOlder;
        public int Customers, CustomersWithLocation;
        // A customer code was made on this PC, or the agent was added to a task sequence.
        public bool ScriptMade, TaskSequenceAdded;
        public bool UploadSeen;
        public bool SccmSide;
        // The "Do not show again" tick, and whether the page has opened by itself before.
        public bool Hidden, AutoShown;
    }

    public enum StepStatus { Done, ToDo, Attention, Check }

    public sealed class GetStartedStep
    {
        public string Id, Title, Text, Button, Reason;
        public StepStatus Status;
        // The roles and consents the step needs, shown behind "What do I need?".
        public List<string> Needs = new List<string>();
        public bool IsDone
        {
            get { return Status == StepStatus.Done; }
        }
    }

    // The steps of the Get started page in order, with a status each and the next one to do.
    public static class GetStarted
    {
        public const string Storage = "storage", Publish = "publish", Customer = "customer", Script = "script", Test = "test";
        public const string HiddenPref = "GetStartedHidden", ShownPref = "GetStartedShown";
        public const string GuideLink = "docs/AGENT.md, the part \"Test on one device first\"";

        public static List<GetStartedStep> Steps(GetStartedState s)
        {
            List<GetStartedStep> l = new List<GetStartedStep>();

            GetStartedStep st = Make(Storage, "Your storage", "A storage account in your Azure and an update key. The devices download TrackVice from it and upload what they see to it.",
                s.StorageSet ? "Open Your storage" : "Set up Your storage");
            if (!s.StorageSet) st.Status = StepStatus.ToDo;
            else if (s.StorageExpired) { st.Status = StepStatus.Attention; st.Reason = "The links in the customer codes have run out. New links make them again."; }
            else st.Status = StepStatus.Done;
            foreach (NeedItem n in SetupNeeds.StorageChecklist()) st.Needs.Add(n.Text);
            l.Add(st);

            st = Make(Publish, "Publish TrackVice for the devices", "Puts this version of TrackVice in your storage. The devices download it, and update themselves from it later.",
                "Publish");
            if (s.PublishedKnown && s.PublishedOlder) { st.Status = StepStatus.Attention; st.Reason = "This TrackVice is newer than the one published."; }
            else if (s.PublishedKnown) st.Status = StepStatus.Done;
            else st.Status = StepStatus.ToDo;
            st.Needs.Add("Your storage must be set up first.");
            st.Needs.Add("The update key made in step 1, on this PC. It signs the version, so devices accept only versions from you.");
            SignInNeed key = SetupNeeds.Find("storage-key");
            if (key != null) st.Needs.Add("If you chose not to keep the account key on this PC: " + key.Role + ". " + key.Who + ".");
            l.Add(st);

            st = Make(Customer, "Add a customer", "Makes a container for the customer's uploads and a customer code with the links the devices use. Use your own organization as the customer if you follow your own devices.",
                "Add customer");
            if (s.CustomersWithLocation > 0) st.Status = StepStatus.Done;
            else if (s.Customers > 0) { st.Status = StepStatus.Attention; st.Reason = "A customer is saved, but it has no agent location. Add customer makes one."; }
            else st.Status = StepStatus.ToDo;
            foreach (NeedItem n in SetupNeeds.Checklist(false, false)) st.Needs.Add(n.Text);
            l.Add(st);

            st = Make(Script, "Put the script in Intune or the agent in a task sequence", "The platform script installs the agent on a device during Autopilot. In SCCM, a step in the task sequence starts it.",
                s.SccmSide ? "Add to a task sequence" : "Open the customer code");
            if (s.ScriptMade || s.TaskSequenceAdded) st.Status = StepStatus.Done;
            else if (s.CustomersWithLocation == 0) st.Status = StepStatus.ToDo;
            else { st.Status = StepStatus.Check; st.Reason = "TrackVice cannot see whether the script is in Intune. Open it to check."; }
            SignInNeed intune = SetupNeeds.Find("intune-add");
            if (intune != null)
            {
                st.Needs.Add("For Intune: " + intune.Role + ".");
                st.Needs.Add(intune.Consent + ". " + intune.Who);
            }
            st.Needs.Add("For SCCM: a package with TrackVice.exe and the right to change the task sequence in the console.");
            l.Add(st);

            st = Make(Test, "Test on one device", "Install the agent on one test PC or virtual machine and check that it uploads. " + GuideLink + " says what to look for.",
                "Open the guide");
            st.Status = s.UploadSeen ? StepStatus.Done : StepStatus.ToDo;
            l.Add(st);
            return l;
        }

        static GetStartedStep Make(string id, string title, string text, string button)
        {
            GetStartedStep s = new GetStartedStep();
            s.Id = id;
            s.Title = title;
            s.Text = text;
            s.Button = button;
            return s;
        }

        public static int DoneCount(List<GetStartedStep> steps)
        {
            int n = 0;
            foreach (GetStartedStep s in steps) if (s.IsDone) n++;
            return n;
        }

        // The first step that is not done, or null.
        public static GetStartedStep Next(List<GetStartedStep> steps)
        {
            foreach (GetStartedStep s in steps) if (!s.IsDone) return s;
            return null;
        }

        public static string Progress(List<GetStartedStep> steps)
        {
            return DoneCount(steps) + " of " + steps.Count + " done";
        }

        public static string StatusText(StepStatus s)
        {
            switch (s)
            {
                case StepStatus.Done: return "Done";
                case StepStatus.Attention: return "Needs attention";
                case StepStatus.Check: return "Check";
                default: return "To do";
            }
        }

        // The page opens by itself once, when nothing is set up yet and the person did not tick "Do not show again".
        public static bool AutoShow(GetStartedState s)
        {
            if (s.Hidden || s.AutoShown) return false;
            return !s.StorageSet && s.CustomersWithLocation == 0;
        }
    }
}
