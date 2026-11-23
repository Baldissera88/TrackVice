namespace TrackVice.Tests
{
    using System;

    // A Windows Server is not followed as an Autopilot or Hybrid Entra join deployment.
    public static class ServerEventTests
    {
        const string Key = @"HKLM\SYSTEM\CurrentControlSet\Control\ProductOptions";

        public static void Run()
        {
            MemoryRegistry server = new MemoryRegistry();
            server.Sz(Key, "ProductType", "ServerNT");
            T.Check(EventWatch.IsServer(server), "Server: ServerNT is a server");

            MemoryRegistry dc = new MemoryRegistry();
            dc.Sz(Key, "ProductType", "LanmanNT");
            T.Check(EventWatch.IsServer(dc), "Server: LanmanNT, a domain controller, is a server");

            MemoryRegistry client = new MemoryRegistry();
            client.Sz(Key, "ProductType", "WinNT");
            T.Check(!EventWatch.IsServer(client), "Server: WinNT is a client");
            T.Check(!EventWatch.IsServer(new MemoryRegistry()), "Server: nothing there is a client");
        }
    }
}
