namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // The groups of the command palette, in the order they are shown when nothing is typed.
    public static class CommandGroups
    {
        public const string Devices = "Devices", Actions = "Actions", Pages = "Pages", Settings = "Settings";

        public static int Order(string group)
        {
            switch (group)
            {
                case Devices: return 0;
                case Actions: return 1;
                case Pages: return 2;
                case Settings: return 3;
                default: return 4;
            }
        }

        // How many of a group show before anything is typed. Settings only show for a search.
        public static int EmptyLimit(string group)
        {
            switch (group)
            {
                case Devices: return 3;
                case Actions: return 6;
                case Pages: return 3;
                default: return 0;
            }
        }
    }

    // One line of the command palette: a device, an action, a page or a setting.
    public sealed class CommandEntry
    {
        public string Group = "", Title = "", Detail = "", Keywords = "", Hint = "";
        // The heading of the group when it is not just the name of the group, such as "Actions on CONTOSO-LT0417".
        public string GroupLabel;
        // An icon of the icon font, or null.
        public string Glyph;
        // More than 0: a search of its own already found the entry (the settings), best first as 1, 2, 3...
        // It is offered even when the words are only in its longer description.
        public int Found;
        public bool Enabled = true;
        public bool HasState;
        public State State;
        // Percent done for a device, or -1 when it is not known.
        public int Percent = -1;
        public object Tag;
    }

    // Ranks the entries of the command palette. For each word: the title starts with it, then a word of the title starts
    // with it, then the title has it, then the keywords, and the first letters of the words of the title as the last resort.
    public static class CommandSearch
    {
        public const int MaxResults = 12;

        public const int Prefix = 100, WordStart = 70, Inside = 40, KeyWordStart = 25, KeyInside = 12, Initials = 8;

        public static string[] Words(string query)
        {
            return (query ?? "").Trim().ToLowerInvariant().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // -1 when the entry does not have every word, else the higher the better.
        public static int Score(string query, CommandEntry e)
        {
            string[] words = Words(query);
            if (words.Length == 0) return 0;
            string title = (e.Title ?? "").ToLowerInvariant();
            string keys = ((e.Keywords ?? "") + " " + (e.Detail ?? "")).ToLowerInvariant();
            int total = 0;
            foreach (string w in words)
            {
                int s = WordScore(w, title, keys);
                if (s < 0) return -1;
                total += s;
            }
            string whole = string.Join(" ", words);
            if (title == whole) total += 60;
            else if (title.StartsWith(whole, StringComparison.Ordinal)) total += 30;
            // A shorter title is the closer match.
            return total - Math.Min(title.Length / 8, 8);
        }

        static int WordScore(string w, string title, string keys)
        {
            if (title.StartsWith(w, StringComparison.Ordinal)) return Prefix;
            if (AtWordStart(title, w)) return WordStart;
            if (title.IndexOf(w, StringComparison.Ordinal) >= 0) return Inside;
            if (AtWordStart(keys, w)) return KeyWordStart;
            if (keys.IndexOf(w, StringComparison.Ordinal) >= 0) return KeyInside;
            if (w.Length >= 2 && InInitials(title, w)) return Initials;
            return -1;
        }

        static bool AtWordStart(string text, string w)
        {
            int i = 0;
            while ((i = text.IndexOf(w, i, StringComparison.Ordinal)) >= 0)
            {
                if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) return true;
                i++;
            }
            return false;
        }

        // "cft" is in "copy for ticket", and "oil" in "open in logvice": the letters are first letters of words, in order.
        public static bool InInitials(string text, string w)
        {
            int k = 0;
            for (int i = 0; i < text.Length && k < w.Length; i++)
            {
                bool start = char.IsLetterOrDigit(text[i]) && (i == 0 || !char.IsLetterOrDigit(text[i - 1]));
                if (start && text[i] == w[k]) k++;
            }
            return k == w.Length;
        }

        // The entries to show, at most max. Nothing typed gives the first few of each group in their own order, and the
        // devices take what room is left. Else the best ones whatever their group, put together by group: the group with
        // the best entry first, so the first line is always the best match.
        public static List<CommandEntry> Search(string query, IList<CommandEntry> all, int max)
        {
            List<CommandEntry> shown = new List<CommandEntry>();
            if (all == null || max <= 0) return shown;
            if (Words(query).Length == 0) return FirstOfEach(all, max);
            List<int[]> scored = new List<int[]>();
            int best = 0;
            for (int i = 0; i < all.Count; i++)
            {
                CommandEntry e = all[i];
                if (!e.Enabled) continue;
                int s = Score(query, e);
                if (s < 0 && e.Found > 0) s = 1;
                if (s < 0) continue;
                best = Math.Max(best, s);
                scored.Add(new int[] { s, CommandGroups.Order(e.Group), i });
            }
            // A word only in the keywords does not crowd out what has it in the title.
            int floor = best / 6;
            scored.RemoveAll(delegate (int[] x) { return x[0] < floor; });
            scored.Sort(delegate (int[] a, int[] b)
            {
                if (a[0] != b[0]) return b[0].CompareTo(a[0]);
                if (a[1] != b[1]) return a[1].CompareTo(b[1]);
                return a[2].CompareTo(b[2]);
            });
            int take = Math.Min(max, scored.Count);
            for (int i = 0; i < take; i++) shown.Add(all[scored[i][2]]);
            return ByGroup(shown);
        }

        static List<CommandEntry> FirstOfEach(IList<CommandEntry> all, int max)
        {
            List<CommandEntry> picked = new List<CommandEntry>();
            Dictionary<string, int> n = new Dictionary<string, int>();
            foreach (CommandEntry e in all)
            {
                if (!e.Enabled) continue;
                int have;
                n.TryGetValue(e.Group, out have);
                if (have >= CommandGroups.EmptyLimit(e.Group)) continue;
                n[e.Group] = have + 1;
                picked.Add(e);
            }
            // More devices when there is room, such as on Overview where there are no actions.
            foreach (CommandEntry e in all)
            {
                if (picked.Count >= max) break;
                if (e.Enabled && e.Group == CommandGroups.Devices && !picked.Contains(e)) picked.Add(e);
            }
            List<CommandEntry> l = new List<CommandEntry>();
            for (int o = 0; o <= 4; o++)
                foreach (CommandEntry e in picked)
                    if (CommandGroups.Order(e.Group) == o && l.Count < max) l.Add(e);
            return l;
        }

        // Stable: the groups in the order their first entry comes, each keeping the order of its entries.
        static List<CommandEntry> ByGroup(List<CommandEntry> ranked)
        {
            List<string> groups = new List<string>();
            foreach (CommandEntry e in ranked) if (!groups.Contains(e.Group)) groups.Add(e.Group);
            List<CommandEntry> l = new List<CommandEntry>();
            foreach (string g in groups)
                foreach (CommandEntry e in ranked) if (e.Group == g) l.Add(e);
            return l;
        }

        // 92 for "24 of 26 done, 1 failed"; -1 when the text has no "n of m".
        public static int PercentOf(string stateText)
        {
            if (string.IsNullOrEmpty(stateText)) return -1;
            string[] p = stateText.Split(' ');
            for (int i = 1; i + 1 < p.Length; i++)
            {
                int done, total;
                if (p[i] == "of" && int.TryParse(p[i - 1], out done) && int.TryParse(p[i + 1].TrimEnd(',', ';', '.'), out total) && total > 0 && done >= 0 && done <= total)
                    return done * 100 / total;
            }
            return -1;
        }
    }
}
