namespace TrackVice
{
    using System.Text;

    // The platform script for one customer: agent/Install-TrackViceAgent.ps1 with $Customer filled in.
    public static class CustomerScript
    {
        public const string Variable = "Customer";

        public static string For(string customerCode)
        {
            return AgentScript.Set(AgentScriptTemplate.Text, Variable, Literal((customerCode ?? "").Trim()));
        }

        // PowerShell ends a single-quoted string at ' and also at the typographic quotes U+2018, U+2019, U+201A and U+201B.
        public static bool IsQuote(char c)
        {
            return c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B';
        }

        // A value as a single-quoted PowerShell string. Every character that would end the string is doubled, so the value
        // stays text whatever it holds. A NUL is left out: nothing a script is filled with has one.
        public static string Literal(string value)
        {
            StringBuilder b = new StringBuilder("'");
            foreach (char c in value ?? "")
            {
                if (c == '\0') continue;
                b.Append(c);
                if (IsQuote(c)) b.Append(c);
            }
            return b.Append('\'').ToString();
        }
    }
}
