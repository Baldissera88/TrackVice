namespace TrackVice
{
    // The platform script for one customer: agent/Install-TrackViceAgent.ps1 with $Customer filled in.
    public static class CustomerScript
    {
        public const string Variable = "Customer";

        // Placeholder until the one-line script is in place; the setup calls only this.
        public static string For(string customerCode)
        {
            return "$Customer = " + AgentScript.Quote(customerCode ?? "") + "\r\n";
        }
    }
}
