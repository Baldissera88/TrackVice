namespace TrackVice
{
    using System;

    // /customer <code> for /agent-install and /agent-start: the same as /upload, /alert, /keep, /max-days, /update-key
    // and /update with the values in the code. The agent then updates itself from the agent container.
    public static class CustomerInstall
    {
        public static void Apply(AgentOptions o, string code)
        {
            CustomerCode c = CustomerCode.Parse(code);
            string problem = c.Problem();
            if (problem != null) throw new ArgumentException(problem);
            o.Upload = c.Upload;
            o.Alert = string.IsNullOrWhiteSpace(c.Alert) ? null : c.Alert;
            o.KeepHours = c.KeepHours;
            o.MaxDays = c.MaxDays;
            o.UpdateKey = c.Key;
            o.UpdateLink = c.Agent;
            o.CustomerName = string.IsNullOrWhiteSpace(c.Name) ? null : c.Name.Trim();
        }
    }
}
