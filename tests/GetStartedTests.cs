namespace TrackVice.Tests
{
    using System.Collections.Generic;

    public static class GetStartedTests
    {
        public static void Run()
        {
            NothingSetUp();
            OnlyStorage();
            StorageAndPublished();
            AllDone();
            CustomerWithoutLocation();
            PublishedOlderNeedsAttention();
            ScriptCannotBeKnown();
            TaskSequenceCountsAsDone();
            DoNotShowAgain();
        }

        static StepStatus Of(List<GetStartedStep> l, string id)
        {
            foreach (GetStartedStep s in l) if (s.Id == id) return s.Status;
            T.Check(false, "step " + id + " exists");
            return StepStatus.ToDo;
        }

        static GetStartedState Storage()
        {
            GetStartedState s = new GetStartedState();
            s.StorageSet = true;
            return s;
        }

        static void NothingSetUp()
        {
            List<GetStartedStep> l = GetStarted.Steps(new GetStartedState());
            T.Eq(5, l.Count, "five steps");
            T.Eq(0, GetStarted.DoneCount(l), "nothing done");
            T.Eq("0 of 5 done", GetStarted.Progress(l), "progress");
            T.Eq(GetStarted.Storage, GetStarted.Next(l).Id, "storage is next");
            foreach (GetStartedStep s in l) T.Eq(StepStatus.ToDo, s.Status, s.Id + " is to do");
            T.Eq("Set up Your storage", l[0].Button, "storage button");
        }

        static void OnlyStorage()
        {
            List<GetStartedStep> l = GetStarted.Steps(Storage());
            T.Eq(StepStatus.Done, Of(l, GetStarted.Storage), "storage done");
            T.Eq(StepStatus.ToDo, Of(l, GetStarted.Publish), "publish to do");
            T.Eq(GetStarted.Publish, GetStarted.Next(l).Id, "publish is next");
            T.Eq("1 of 5 done", GetStarted.Progress(l), "progress");
        }

        static void StorageAndPublished()
        {
            GetStartedState s = Storage();
            s.PublishedKnown = true;
            List<GetStartedStep> l = GetStarted.Steps(s);
            T.Eq(StepStatus.Done, Of(l, GetStarted.Publish), "publish done");
            T.Eq(StepStatus.ToDo, Of(l, GetStarted.Customer), "customer to do");
            T.Eq(GetStarted.Customer, GetStarted.Next(l).Id, "customer is next");
            T.Eq("2 of 5 done", GetStarted.Progress(l), "progress");
        }

        static void AllDone()
        {
            GetStartedState s = Storage();
            s.PublishedKnown = true;
            s.Customers = s.CustomersWithLocation = 1;
            s.ScriptMade = true;
            s.UploadSeen = true;
            List<GetStartedStep> l = GetStarted.Steps(s);
            T.Eq(5, GetStarted.DoneCount(l), "all done");
            T.Check(GetStarted.Next(l) == null, "no next step");
            T.Eq("5 of 5 done", GetStarted.Progress(l), "progress");
        }

        static void CustomerWithoutLocation()
        {
            GetStartedState s = Storage();
            s.PublishedKnown = true;
            s.Customers = 1;
            List<GetStartedStep> l = GetStarted.Steps(s);
            T.Eq(StepStatus.Attention, Of(l, GetStarted.Customer), "customer without location needs attention");
            T.Eq(StepStatus.ToDo, Of(l, GetStarted.Script), "script is not asked for yet");
            T.Eq(GetStarted.Customer, GetStarted.Next(l).Id, "customer is still next");
            T.Check(l[2].Reason != null && l[2].Reason.Length > 0, "a reason is given");
        }

        static void PublishedOlderNeedsAttention()
        {
            GetStartedState s = Storage();
            s.PublishedKnown = s.PublishedOlder = true;
            List<GetStartedStep> l = GetStarted.Steps(s);
            T.Eq(StepStatus.Attention, Of(l, GetStarted.Publish), "older published version");
            s.StorageExpired = true;
            T.Eq(StepStatus.Attention, Of(GetStarted.Steps(s), GetStarted.Storage), "expired links");
        }

        // A customer exists, but nothing shows that its script was made: Check, never Done.
        static void ScriptCannotBeKnown()
        {
            GetStartedState s = Storage();
            s.Customers = s.CustomersWithLocation = 1;
            List<GetStartedStep> l = GetStarted.Steps(s);
            T.Eq(StepStatus.Check, Of(l, GetStarted.Script), "script is Check");
            T.Eq("Open the customer code", l[3].Button, "Intune side button");
            s.SccmSide = true;
            T.Eq("Add to a task sequence", GetStarted.Steps(s)[3].Button, "SCCM side button");
        }

        static void TaskSequenceCountsAsDone()
        {
            GetStartedState s = Storage();
            s.Customers = s.CustomersWithLocation = 1;
            s.TaskSequenceAdded = true;
            T.Eq(StepStatus.Done, Of(GetStarted.Steps(s), GetStarted.Script), "task sequence step added");
        }

        static void DoNotShowAgain()
        {
            GetStartedState s = new GetStartedState();
            T.Check(GetStarted.AutoShow(s), "shows at first");
            s.Hidden = true;
            T.Check(!GetStarted.AutoShow(s), "do not show again is honoured");
            s.Hidden = false;
            s.AutoShown = true;
            T.Check(!GetStarted.AutoShow(s), "shows only once");
            s.AutoShown = false;
            s.StorageSet = true;
            T.Check(!GetStarted.AutoShow(s), "not when storage is set up");
            s.StorageSet = false;
            s.CustomersWithLocation = 1;
            T.Check(!GetStarted.AutoShow(s), "not when a customer has a location");
        }
    }
}
