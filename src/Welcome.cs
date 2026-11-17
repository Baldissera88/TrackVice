namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // Whether the welcome page shows at start. It never shows on a device that deploys, the same checks that stop the
    // update check, nor when TrackVice was started to do one thing, such as a demo, a file or the wall.
    public static class WelcomeGate
    {
        public const string SeenPref = "WelcomeSeen";

        // Null when the welcome shows, otherwise why not.
        public static string Reason(UpdateContext c, bool startedForSomething, bool seen)
        {
            if (c == null || !c.Window) return "No window.";
            if (startedForSomething) return "Started with an option.";
            if (c.WinPE) return "Windows PE.";
            if (c.System) return "Runs as SYSTEM.";
            if (c.TaskSequence) return "A task sequence runs.";
            if (c.Oobe) return "Windows setup or the Enrollment Status Page runs.";
            if (c.Deploying) return "A deployment runs on this device.";
            if (seen) return "Seen before.";
            return null;
        }
    }

    // What the steps of Getting started look at. Each one comes from real state on this PC.
    public interface ISetupFacts
    {
        bool StorageReady { get; }
        bool CustomerMade { get; }
        bool UploadSeen { get; }
        bool ListOpened { get; }
    }

    public sealed class FixedSetupFacts : ISetupFacts
    {
        public bool Storage, Customer, Upload, List;

        public FixedSetupFacts(bool storage, bool customer, bool upload, bool list)
        {
            Storage = storage;
            Customer = customer;
            Upload = upload;
            List = list;
        }

        public bool StorageReady { get { return Storage; } }
        public bool CustomerMade { get { return Customer; } }
        public bool UploadSeen { get { return Upload; } }
        public bool ListOpened { get { return List; } }
    }

    public sealed class SetupStep
    {
        public string Id, Title, Text, Action;
        public bool Done;
    }

    // Getting started for following devices from your own PC: the same four steps for your own organization and for
    // customers, with the words for each.
    public static class GettingStarted
    {
        public const string Storage = "storage", Customer = "customer", Test = "test", Follow = "follow";
        public const string Own = "own", Customers = "customers";
        public const string AudiencePref = "SetupFor", UploadPref = "SetupUploadSeen", ListPref = "SetupListOpened", HiddenPref = "SetupHidden";

        public static List<SetupStep> Steps(string audience, ISetupFacts f)
        {
            bool own = audience == Own;
            List<SetupStep> l = new List<SetupStep>();
            l.Add(Step(Storage, "Your storage",
                own ? "One storage account in your Azure. The devices download TrackVice from it and upload what they see to it. Settings > Your storage."
                    : "One storage account in your Azure for all customers. The devices download TrackVice from it, and each customer's uploads go to a private container. Settings > Your storage.",
                f.StorageReady ? "Open" : "Set up", f.StorageReady));
            l.Add(Step(Customer, own ? "Add your own tenant" : "Add the first customer",
                own ? "The same three steps as for a customer, with your organization's name: a container, a code and the platform script for your tenant. Devices > Add customer."
                    : "A container, a customer code and the platform script in the customer's tenant, in three steps. Devices > Add customer.",
                own ? "Add tenant" : "Add customer", f.CustomerMade));
            l.Add(Step(Test, "Test on one VM",
                "Install the agent on one test PC or virtual machine with the " + (own ? "code" : "customer code") + ", and check that it uploads. The guide says what to look for.",
                "Open the guide", f.UploadSeen));
            l.Add(Step(Follow, "Follow the devices",
                "Open the list of devices with the agent. A device shows up there during device setup, and you can watch it as if you sat at it.",
                "Open the list", f.ListOpened));
            return l;
        }

        static SetupStep Step(string id, string title, string text, string action, bool done)
        {
            SetupStep s = new SetupStep();
            s.Id = id;
            s.Title = title;
            s.Text = text;
            s.Action = action;
            s.Done = done;
            return s;
        }

        public static int DoneCount(List<SetupStep> steps)
        {
            int n = 0;
            foreach (SetupStep s in steps) if (s.Done) n++;
            return n;
        }

        public static bool AllDone(List<SetupStep> steps)
        {
            return DoneCount(steps) == steps.Count;
        }

        // The first step that is not done, or null.
        public static SetupStep Next(List<SetupStep> steps)
        {
            foreach (SetupStep s in steps) if (!s.Done) return s;
            return null;
        }

        // The line in the main window while setting up: shown once the person chose to follow devices from this PC,
        // until every step is done or they hid it.
        public static bool ShowBar(string audience, bool hidden, List<SetupStep> steps)
        {
            if (audience != Own && audience != Customers) return false;
            if (hidden) return false;
            return !AllDone(steps);
        }

        public static string BarText(List<SetupStep> steps)
        {
            SetupStep next = Next(steps);
            string s = "Getting started: " + DoneCount(steps) + " of " + steps.Count + " done.";
            return next == null ? s : s + " Next: " + next.Title + ".";
        }
    }
}
