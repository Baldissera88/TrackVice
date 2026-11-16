namespace TrackVice
{
    // The platform script for one customer: agent/Install-TrackViceAgent.ps1 with $Customer filled in.
    public static class CustomerScript
    {
        public const string Variable = "Customer";

        public static string For(string customerCode)
        {
            return AgentScript.Set(AgentScriptTemplate.Text, Variable, AgentScript.Quote((customerCode ?? "").Trim()));
        }
    }
}
