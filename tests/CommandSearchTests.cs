namespace TrackVice.Tests
{
    using System.Collections.Generic;

    public static class CommandSearchTests
    {
        static CommandEntry E(string group, string title, string keywords)
        {
            CommandEntry e = new CommandEntry();
            e.Group = group;
            e.Title = title;
            e.Keywords = keywords;
            return e;
        }

        static List<CommandEntry> Sample()
        {
            List<CommandEntry> l = new List<CommandEntry>();
            l.Add(E(CommandGroups.Devices, "CONTOSO-LT0417", "anna hansen"));
            l.Add(E(CommandGroups.Devices, "FAB-LT0007", "fabrikam"));
            l.Add(E(CommandGroups.Actions, "Copy for ticket", "support summary"));
            l.Add(E(CommandGroups.Actions, "Open in LogVice", "log"));
            l.Add(E(CommandGroups.Actions, "Open in Intune", "portal"));
            l.Add(E(CommandGroups.Actions, "Save", "session"));
            l.Add(E(CommandGroups.Pages, "Overview", "home"));
            l.Add(E(CommandGroups.Pages, "Updates", "settings version"));
            l.Add(E(CommandGroups.Pages, "Your storage and agents", "settings"));
            l.Add(E(CommandGroups.Pages, "Customers", "settings"));
            return l;
        }

        static string Titles(List<CommandEntry> l)
        {
            List<string> t = new List<string>();
            foreach (CommandEntry e in l) t.Add(e.Title);
            return string.Join(", ", t.ToArray());
        }

        public static void Run()
        {
            Scores();
            Ranking();
            NothingTyped();
            Percent();
        }

        static void Scores()
        {
            // Prefix beats word start beats contains beats keywords beats first letters.
            CommandEntry prefix = E(CommandGroups.Pages, "Logs", "");
            CommandEntry word = E(CommandGroups.Pages, "Open logs", "");
            CommandEntry inside = E(CommandGroups.Pages, "Catalog", "");
            CommandEntry keyword = E(CommandGroups.Pages, "Zebra", "log");
            CommandEntry initials = E(CommandGroups.Pages, "Last open group", "");
            int a = CommandSearch.Score("log", prefix), b = CommandSearch.Score("log", word), c = CommandSearch.Score("log", inside);
            int d = CommandSearch.Score("log", keyword), f = CommandSearch.Score("log", initials);
            T.Check(a > b && b > c && c > d && d > f && f > 0, "prefix > word start > contains > keyword > initials (" + a + " " + b + " " + c + " " + d + " " + f + ")");
            T.Eq(-1, CommandSearch.Score("xyz", prefix), "no match is -1");
            T.Eq(-1, CommandSearch.Score("log xyz", prefix), "every word must match");
            T.Check(CommandSearch.Score("OPEN LOGS", word) > 0, "case does not matter");
            T.Check(CommandSearch.Score("  open   logs ", word) == CommandSearch.Score("open logs", word), "extra spaces do not matter");
            T.Eq(0, CommandSearch.Score("", word), "nothing typed scores 0");

            // The whole title typed beats a longer title that starts with it.
            T.Check(CommandSearch.Score("save", E(CommandGroups.Actions, "Save", "")) > CommandSearch.Score("save", E(CommandGroups.Actions, "Save all", "")), "exact title first");
            T.Check(CommandSearch.Score("open lo", word) > CommandSearch.Score("open lo", E(CommandGroups.Pages, "Open the folder logs", "")), "the words in a row first");

            // A word start counts after a dash, as in device names.
            T.Eq(CommandSearch.WordStart - 1, CommandSearch.Score("lt04", E(CommandGroups.Devices, "CONTOSO-LT0417", "")), "lt04 starts a word of CONTOSO-LT0417");

            // First letters, in order, only from the start of words.
            T.Check(CommandSearch.Score("cft", E(CommandGroups.Actions, "Copy for ticket", "")) > 0, "cft finds Copy for ticket");
            T.Check(CommandSearch.Score("oil", E(CommandGroups.Actions, "Open in LogVice", "")) > 0, "oil finds Open in LogVice");
            T.Eq(-1, CommandSearch.Score("tcf", E(CommandGroups.Actions, "Copy for ticket", "")), "first letters out of order do not match");
            T.Eq(-1, CommandSearch.Score("cpy", E(CommandGroups.Actions, "Copy for ticket", "")), "letters inside words are not first letters");
            T.Check(CommandSearch.InInitials("contoso-lt0417", "cl"), "a dash starts a word");
        }

        static void Ranking()
        {
            List<CommandEntry> all = Sample();
            List<CommandEntry> r = CommandSearch.Search("open", all, 12);
            T.Eq("Open in LogVice, Open in Intune", Titles(r), "open finds the two Open actions, in list order");
            r = CommandSearch.Search("lt04", all, 12);
            T.Eq("CONTOSO-LT0417", Titles(r), "lt04 finds one device");
            r = CommandSearch.Search("anna", all, 12);
            T.Eq("CONTOSO-LT0417", Titles(r), "a keyword finds the device by its user");
            r = CommandSearch.Search("customers", all, 12);
            T.Eq("Customers", r[0].Title, "the page");

            // The best match is the first line, whatever its group, and a word only inside keywords is left out then.
            r = CommandSearch.Search("up", all, 12);
            T.Eq("Updates", Titles(r), "Updates starts with up; support only has it inside");
            r = CommandSearch.Search("storage", all, 12);
            T.Eq("Your storage and agents", r[0].Title, "storage");
            r = CommandSearch.Search("settings", all, 12);
            T.Eq(3, r.Count, "keywords alone still find");

            // Groups stay together, the group with the best line first.
            List<CommandEntry> mixed = new List<CommandEntry>();
            mixed.Add(E(CommandGroups.Devices, "PC-LOG", ""));
            mixed.Add(E(CommandGroups.Actions, "Open in LogVice", ""));
            mixed.Add(E(CommandGroups.Pages, "LogVice", ""));
            mixed.Add(E(CommandGroups.Pages, "Logs folder", ""));
            r = CommandSearch.Search("logvice", mixed, 12);
            T.Eq("LogVice, Open in LogVice", Titles(r), "logvice: the page that is called that, then the action");
            r = CommandSearch.Search("log", mixed, 12);
            T.Eq("LogVice, Logs folder, PC-LOG, Open in LogVice", Titles(r), "log: the pages that start with it, then the device and the action where a word starts with it");

            // The best entries are kept, put together by group.
            List<CommandEntry> many = new List<CommandEntry>();
            for (int i = 0; i < 20; i++) many.Add(E(i % 2 == 0 ? CommandGroups.Pages : CommandGroups.Devices, "item " + i, ""));
            r = CommandSearch.Search("item", many, 12);
            T.Eq(12, r.Count, "capped at 12");
            T.Check(Together(r), "results are put together by group");
            T.Eq(0, CommandSearch.Search("item", many, 0).Count, "nothing when there is no room");

            // Disabled entries are not offered.
            all[4].Enabled = false;
            T.Eq("Open in LogVice", Titles(CommandSearch.Search("open", all, 12)), "a disabled action is left out");

            // Entries a search of their own found are offered after the others, in the order it gave.
            List<CommandEntry> s = new List<CommandEntry>();
            CommandEntry s1 = E(CommandGroups.Settings, "Read every", ""); s1.Found = 1;
            CommandEntry s2 = E(CommandGroups.Settings, "History to read", ""); s2.Found = 2;
            CommandEntry s3 = E(CommandGroups.Settings, "Theme", "");
            s.Add(s1); s.Add(s2); s.Add(s3);
            r = CommandSearch.Search("how often", s, 12);
            T.Eq("Read every, History to read", Titles(r), "found entries need no match of their own, and keep their order");
            s2.Found = 0;
            T.Eq("Read every", Titles(CommandSearch.Search("how often", s, 12)), "others still need one");
            r = CommandSearch.Search("read", s, 12);
            T.Eq("Read every", r[0].Title, "a title match beats a found entry");
            T.Eq("Theme", Titles(CommandSearch.Search("theme", s, 12)), "the setting by its name");
        }

        static bool Together(List<CommandEntry> r)
        {
            List<string> closed = new List<string>();
            for (int i = 0; i < r.Count; i++)
            {
                if (closed.Contains(r[i].Group)) return false;
                if (i + 1 < r.Count && r[i + 1].Group != r[i].Group) closed.Add(r[i].Group);
            }
            return true;
        }

        static void NothingTyped()
        {
            // A few of each group, in the order of the groups, settings never.
            List<CommandEntry> big = Sample();
            for (int i = 0; i < 5; i++) big.Add(E(CommandGroups.Devices, "DEV-" + i, ""));
            big.Add(E(CommandGroups.Settings, "Theme", ""));
            List<CommandEntry> r = CommandSearch.Search("", big, 12);
            int devices = 0, actions = 0, pages = 0, settings = 0;
            foreach (CommandEntry e in r)
            {
                if (e.Group == CommandGroups.Devices) devices++;
                if (e.Group == CommandGroups.Actions) actions++;
                if (e.Group == CommandGroups.Pages) pages++;
                if (e.Group == CommandGroups.Settings) settings++;
            }
            T.Eq(12, r.Count, "nothing typed is capped");
            T.Eq(4, actions, "every action of the sample");
            T.Eq(3, pages, "three pages");
            T.Eq(5, devices, "the devices take the room that is left");
            T.Eq(0, settings, "no settings when nothing is typed");
            T.Eq(CommandGroups.Devices, r[0].Group, "devices first");
            T.Eq("CONTOSO-LT0417", r[0].Title, "in their own order");
            T.Eq(CommandGroups.Pages, r[r.Count - 1].Group, "pages last");

            // No actions, as on Overview: more devices.
            List<CommandEntry> page = new List<CommandEntry>();
            for (int i = 0; i < 20; i++) page.Add(E(CommandGroups.Devices, "DEV-" + i, ""));
            page.Add(E(CommandGroups.Pages, "Overview", ""));
            page.Add(E(CommandGroups.Pages, "Devices", ""));
            r = CommandSearch.Search(" ", page, 12);
            T.Eq(12, r.Count, "spaces are nothing typed");
            T.Eq("DEV-0", r[0].Title, "first device");
            T.Eq("Devices", r[11].Title, "the pages stay");
            T.Eq(0, CommandSearch.Search("", null, 12).Count, "no entries");
        }

        static void Percent()
        {
            T.Eq(92, CommandSearch.PercentOf("24 of 26 done, 1 failed"), "24 of 26");
            T.Eq(100, CommandSearch.PercentOf("31 of 31 done"), "31 of 31");
            T.Eq(50, CommandSearch.PercentOf("Apps: 3 of 6, installing"), "3 of 6,");
            T.Eq(-1, CommandSearch.PercentOf("Completed"), "no numbers");
            T.Eq(-1, CommandSearch.PercentOf(""), "empty");
            T.Eq(-1, CommandSearch.PercentOf(null), "null");
            T.Eq(-1, CommandSearch.PercentOf("0 of 0 done"), "nothing to do");
            T.Eq(-1, CommandSearch.PercentOf("7 of 5 done"), "more done than there is");
        }
    }
}
