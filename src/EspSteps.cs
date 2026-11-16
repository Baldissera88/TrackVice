namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // Ends the ESP steps (Security policies, Certificates, Network connections, Apps) that Windows never closed.
    // EspFirstSync closes a step when Windows writes its ...Duration value. On some devices that value never comes,
    // for example after Continue anyway in device setup with the user already at the desktop. A step without it is
    // skipped once other evidence says the ESP no longer waits there, and not before.
    public static class EspSteps
    {
        public const string StepPrefix = "esp:step:";
        public const string CountedKey = "Time counted by Windows";
        public const string AccountDoneInfo = "Account setup";
        public const string AccountDoneText = "Done, the ESP says";
        // Time for a late ...Duration value to arrive before other evidence ends a step.
        public static TimeSpan Grace = TimeSpan.FromMinutes(5);

        public static bool IsStep(Item i)
        {
            return i != null && i.Key != null && i.Key.StartsWith(StepPrefix, StringComparison.OrdinalIgnoreCase);
        }

        static bool Open(Item i)
        {
            return IsStep(i) && !States.IsFinal(i.State) && i.State != State.WaitingReboot && i.Get(CountedKey) == null;
        }

        static bool Blocking(Item i)
        {
            return !IsStep(i) && i.Key != "esp:continued";
        }

        static DateTime End(Item i)
        {
            return i.EndUtc ?? i.LastUtc;
        }

        // Why account setup is over without its steps, and from when. False while it may still run.
        public static bool AccountOver(Session s, DateTime nowUtc, out string why, out DateTime when)
        {
            why = null;
            when = DateTime.MinValue;
            Phase acc = s.FindPhase(Phases.AccountSetup);
            if (acc == null || acc.Items.Count == 0) return false;
            DateTime last = DateTime.MinValue;
            int blocking = 0;
            bool allFinal = true;
            foreach (Item i in acc.Items)
            {
                if (!Blocking(i)) continue;
                blocking++;
                if (!States.IsFinal(i.State) && i.State != State.WaitingReboot) allFinal = false;
                else if (End(i) > last) last = End(i);
            }
            if (s.GetInfo(AccountDoneInfo) == AccountDoneText)
            {
                why = "The ESP says account setup is done, but Windows did not write how long this step took.";
                when = last != DateTime.MinValue ? last : (acc.StartUtc ?? nowUtc);
                return true;
            }
            if (blocking > 0 && allFinal && last + Grace <= nowUtc)
            {
                why = "Everything account setup waited for has ended, but Windows did not write how long this step took.";
                when = last;
                return true;
            }
            DateTime t;
            if (ContinuedAndSignedIn(s, out t) && t + Grace <= nowUtc)
            {
                why = "Device setup was left with Continue anyway and the user has signed in, but Windows did not write how long this step took.";
                when = t;
                return true;
            }
            return false;
        }

        // Continue anyway in device setup, and a user who signed in after it: the user is at the desktop.
        public static bool ContinuedAndSignedIn(Session s, out DateTime when)
        {
            when = DateTime.MinValue;
            Item cont = s.Find("esp:continued");
            if (cont == null) return false;
            DateTime? signIn = SignedIn(s);
            if (!signIn.HasValue) return false;
            when = signIn.Value > cont.StateUtc ? signIn.Value : cont.StateUtc;
            return true;
        }

        // The first sign of a user: account setup, or an app or script that ran for a user.
        public static DateTime? SignedIn(Session s)
        {
            DateTime? first = null;
            Phase acc = s.FindPhase(Phases.AccountSetup);
            if (acc != null && acc.Items.Count > 0) first = acc.StartUtc;
            foreach (Item i in s.Items)
            {
                bool user = (AppGroups.IsApp(i) && i.Get(AppGroups.TargetKey) == AppGroups.User && i.StartUtc.HasValue) || (i.Kind == Kinds.Script && i.Get("Context") == "User");
                if (!user) continue;
                DateTime t = i.StartUtc ?? i.FirstUtc;
                if (first == null || t < first.Value) first = t;
            }
            return first;
        }

        // Closes the steps that are over, and drops No progress for steps: Windows counts a step without saying more,
        // and the items it waits for have findings of their own.
        public static void Apply(Session s, List<Finding> list, DateTime nowUtc)
        {
            Close(s, nowUtc);
            list.RemoveAll(delegate (Finding f) { return f.Kind == FindingKinds.Stuck && f.ItemKey != null && f.ItemKey.StartsWith(StepPrefix, StringComparison.OrdinalIgnoreCase); });
        }

        public static void Close(Session s, DateTime nowUtc)
        {
            Phase acc = s.FindPhase(Phases.AccountSetup);
            if (acc != null)
            {
                List<Item> open = new List<Item>();
                foreach (Item i in acc.Items) if (Open(i)) open.Add(i);
                string why;
                DateTime when;
                if (open.Count > 0 && AccountOver(s, nowUtc, out why, out when))
                    foreach (Item i in open) Skip(s, i, when, why);
            }
            Phase dev = s.FindPhase(Phases.DeviceSetup);
            if (dev != null)
            {
                List<Item> open = new List<Item>();
                foreach (Item i in dev.Items) if (Open(i)) open.Add(i);
                DateTime when;
                if (open.Count > 0 && ContinuedAndSignedIn(s, out when) && when + Grace <= nowUtc)
                    foreach (Item i in open) Skip(s, i, when, "Device setup was left with Continue anyway before this step was done, and the user has signed in.");
            }
        }

        static void Skip(Session s, Item i, DateTime when, string why)
        {
            if (when < i.StateUtc) when = i.StateUtc;
            i.State = State.Skipped;
            i.StateUtc = when;
            i.EndUtc = when;
            if (when > i.LastUtc) i.LastUtc = when;
            Attempt a = i.Current;
            if (a != null && !a.EndUtc.HasValue)
            {
                a.State = State.Skipped;
                a.EndUtc = when;
            }
            long seq = 0;
            foreach (TEvent x in s.Events) if (x.Seq > seq) seq = x.Seq;
            TEvent e = new TEvent();
            e.TimeUtc = when;
            e.Source = "ESP";
            e.Text = why;
            e.StateName = State.Skipped.ToString();
            e.ItemKey = i.Key;
            e.Seq = seq + 1;
            Insert(s.Events, e);
            Insert(i.Events, e);
        }

        static void Insert(List<TEvent> list, TEvent e)
        {
            int k = list.Count;
            while (k > 0 && list[k - 1].TimeUtc > e.TimeUtc) k--;
            list.Insert(k, e);
        }
    }
}
