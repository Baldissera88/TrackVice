namespace TrackVice
{
    using System;
    using System.Globalization;

    public sealed class LifetimeDecision
    {
        public bool Remove;
        public string Reason;

        public LifetimeDecision(bool remove, string reason)
        {
            Remove = remove;
            Reason = reason;
        }
    }

    // Decides when the installed agent removes itself: KeepHours after the deployment is done, not while a
    // pre-provisioned device waits for its user, and in any case MaxDays after it was installed.
    public sealed class AgentLifetime
    {
        public const int DefaultKeepHours = 48;
        public const int DefaultMaxDays = 30;

        public int KeepHours = DefaultKeepHours;
        public int MaxDays = DefaultMaxDays;
        public DateTime InstalledUtc;
        // When the deployment was found done, and how many items it had then. Saved, so a new start of the agent keeps them.
        public DateTime? DoneUtc;
        public int DoneItems;
        public bool Waiting;
        public Action<string> Log;
        public Action Save;

        public AgentLifetime(DateTime installedUtc, int keepHours, int maxDays)
        {
            InstalledUtc = installedUtc;
            KeepHours = keepHours > 0 ? keepHours : DefaultKeepHours;
            MaxDays = maxDays > 0 ? maxDays : DefaultMaxDays;
        }

        // Reads the saved values. An agent installed by an older version has no MaxDays and gets the default.
        public static AgentLifetime FromSettings(Func<string, string> reg, DateTime installedUtc)
        {
            AgentLifetime l = new AgentLifetime(installedUtc, Int(reg("KeepHours")), Int(reg("MaxDays")));
            DateTime t;
            if (DateTime.TryParse(reg("DoneUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
            {
                l.DoneUtc = t;
                l.DoneItems = Int(reg("DoneItems"));
            }
            return l;
        }

        static int Int(string s)
        {
            int n;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0 ? n : 0;
        }

        public DateTime LimitUtc
        {
            get { return InstalledUtc.AddDays(MaxDays); }
        }

        // When the agent removes itself if nothing new starts.
        public DateTime UntilUtc
        {
            get
            {
                if (!DoneUtc.HasValue) return LimitUtc;
                DateTime u = DoneUtc.Value.AddHours(KeepHours);
                return u < LimitUtc ? u : LimitUtc;
            }
        }

        // idle is the agent's own idle: nothing running and nothing new for a while.
        public LifetimeDecision Check(Session s, bool idle, DateTime nowUtc)
        {
            if (nowUtc >= LimitUtc)
                return new LifetimeDecision(true, "Installed " + MaxDays + " days ago, the longest the agent stays. Removing the agent.");

            DateTime from;
            DateTime? to;
            bool boxed = AutopilotFlow.Boxed(s, nowUtc, out from, out to);
            bool waiting = boxed && !to.HasValue;
            if (waiting != Waiting)
            {
                Waiting = waiting;
                Write(waiting ? "The technician phase is finished and the device was resealed. Waiting for the user phase." : "The wait for the user phase is over.");
            }

            if (DoneUtc.HasValue)
            {
                string why = null;
                if (s.CountActive > 0) why = "something is running";
                else if (s.Items.Count > DoneItems) why = (s.Items.Count - DoneItems) + " new items";
                else if (boxed && to.HasValue && to.Value > DoneUtc.Value) why = "the user phase started";
                if (why != null)
                {
                    DoneUtc = null;
                    DoneItems = 0;
                    Write("Something new started after the deployment was done: " + why + ". Counting again when it is done.");
                    Saved();
                }
            }

            if (waiting) return new LifetimeDecision(false, "Waiting for the user phase.");

            if (!DoneUtc.HasValue && idle && s.Items.Count > 0 && s.CountActive == 0)
            {
                DoneUtc = nowUtc;
                DoneItems = s.Items.Count;
                Write("Deployment done, " + s.Items.Count + " items, " + s.Count(State.Failed) + " failed.");
                Write("Counting " + KeepHours + " hours from " + U(nowUtc) + ". The agent removes itself at " + U(UntilUtc) + " unless something new starts.");
                Saved();
            }

            if (!DoneUtc.HasValue) return new LifetimeDecision(false, "The deployment is not done.");
            // Only when nothing has changed for a while, so a device that just started has time to show what it does next.
            if (nowUtc >= DoneUtc.Value.AddHours(KeepHours) && idle)
                return new LifetimeDecision(true, "The deployment was done " + KeepHours + " hours ago. Removing the agent.");
            return new LifetimeDecision(false, "Done, the agent removes itself at " + U(UntilUtc) + ".");
        }

        public static string U(DateTime t)
        {
            return t.ToString("u", CultureInfo.InvariantCulture);
        }

        void Write(string text)
        {
            if (Log != null) Log(text);
        }

        void Saved()
        {
            if (Save != null) Save();
        }
    }
}
