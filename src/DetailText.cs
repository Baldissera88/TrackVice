namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    // The words the code already has for a finding, sorted into what happened, the likely reason and what to do, for the
    // item details. Nothing is added or reworded: a sentence that cannot be told apart clearly stays where it was.
    public static class DetailText
    {
        // A sentence ends at . ! or ? followed by space and a capital, a quote or a bracket, so "2.0" and "e.g. msiexec" stay whole.
        static readonly Regex RxSentence = new Regex(@"(?<=[.!?])\s+(?=[A-Z""'(])", RegexOptions.Compiled);

        // A sentence of the message that says what is probably behind it.
        static readonly Regex RxWhy = new Regex(@"^(?:(?:This|That|It)\b.*\b(?:often|usually|likely|probably)\b|(?:Usually|Often|Probably|Likely|Most likely)\b)", RegexOptions.Compiled);

        const string Verbs = @"(?:Add|Allow|Approve|Ask|Assign|Change|Check|Choose|Clear|Close|Collect|Compare|Confirm|Contact|Copy|Create|Delete|Deploy|Disable|Distribute|Do|Download|Enable|Ensure|Exclude|Find|Fix|Free (?:up|disk|space)|Give|Grant|Increase|Inspect|Install|Keep|Let|Look|Make|Move|Open|Point|Publish|Put|Raise|Read|Reboot|Redistribute|Reinstall|Remove|Rename|Repackage|Repair|Re-?run|Reset|Restart|Retry|Review|Run|See|Select|Send|Set|Sign|Start|Stop|Sync|Test|Try|Turn|Uninstall|Update|Upload|Use|Verify|Wait)(?![\w-])";

        // A sentence of the hint that tells what to do: it starts with a verb, or with "If ..., " and then a verb.
        static readonly Regex RxDo = new Regex("^(?:" + Verbs + "|If\\b[^,.]*,\\s*(?i:" + Verbs + "))", RegexOptions.Compiled);

        public static string[] Sentences(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            List<string> l = new List<string>();
            foreach (string p in RxSentence.Split(text.Trim())) if (p.Trim().Length > 0) l.Add(p.Trim());
            return l.ToArray();
        }

        // The sentences of the message that say what is likely behind it move to why; the first sentence always stays.
        public static void SplitMessage(string message, out string what, out string why)
        {
            what = message;
            why = null;
            string[] parts = Sentences(message);
            if (parts.Length < 2) return;
            List<string> keep = new List<string>(), moved = new List<string>();
            foreach (string p in parts)
            {
                if (keep.Count > 0 && RxWhy.IsMatch(p)) moved.Add(p);
                else keep.Add(p);
            }
            if (moved.Count == 0) return;
            what = string.Join(" ", keep.ToArray());
            why = string.Join(" ", moved.ToArray());
        }

        // The sentences of the hint before the first one that tells what to do are the reason; from there on it is what to do.
        // A hint without such a sentence is all reason, one that starts with one is all what to do.
        public static void SplitHint(string hint, out string why, out string todo)
        {
            why = null;
            todo = null;
            string[] parts = Sentences(hint);
            if (parts.Length == 0) return;
            int first = -1;
            for (int k = 0; k < parts.Length; k++) if (RxDo.IsMatch(parts[k])) { first = k; break; }
            if (first < 0) { why = hint.Trim(); return; }
            if (first == 0) { todo = hint.Trim(); return; }
            why = string.Join(" ", parts, 0, first);
            todo = string.Join(" ", parts, first, parts.Length - first);
        }

        // Three letters for the tile beside the item name.
        public static string KindCode(string kind)
        {
            switch (kind)
            {
                case Kinds.Win32App: return "W32";
                case Kinds.MsiApp: return "MSI";
                case Kinds.StoreApp: return "STO";
                case Kinds.Script: return "PS";
                case Kinds.Office: return "365";
                case Kinds.Policy: return "POL";
                case Kinds.Certificate: return "CRT";
                case Kinds.Profile: return "PRF";
                case Kinds.Enrollment: return "ENR";
                case Kinds.Agent: return "AGT";
                case Kinds.Esp: return "ESP";
                case Kinds.Restart: return "RST";
                case Kinds.Step: return "TS";
                case Kinds.Application: return "APP";
                case Kinds.Package: return "PKG";
                case Kinds.Update: return "UPD";
                case Kinds.Content: return "CNT";
            }
            if (string.IsNullOrEmpty(kind) || kind.Trim().Length == 0) return "?";
            string k = kind.Trim().ToUpperInvariant();
            return k.Length > 3 ? k.Substring(0, 3) : k;
        }

        // "#2 · 42 s · Failed, 1618", "#3 · 55 s · Done, exit 0", "#1 · 4 min 10 s so far · Installing".
        public static string AttemptLabel(int index, TimeSpan? took, bool open, string state, long? exitCode, bool reboot)
        {
            string s = "#" + index.ToString(CultureInfo.InvariantCulture);
            string d = Txt.Duration(took);
            if (d.Length > 0) s += " · " + d + (open ? " so far" : "");
            else if (open) s += " · running";
            s += " · " + state;
            if (exitCode.HasValue) s += ", " + (exitCode.Value == 0 ? "exit 0" : Errors.Short(exitCode));
            if (reboot) s += ", restart needed";
            return s;
        }

        // Whether the device restarted between two attempts: the earlier one asked for it, or a restart was seen in between.
        public static bool RestartBetween(DateTime fromUtc, DateTime toUtc, bool earlierAskedForRestart, IEnumerable<DateTime> restartsUtc)
        {
            if (earlierAskedForRestart) return true;
            if (restartsUtc == null || toUtc < fromUtc) return false;
            foreach (DateTime r in restartsUtc) if (r >= fromUtc && r <= toUtc) return true;
            return false;
        }
    }
}
