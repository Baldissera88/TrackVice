namespace TrackVice.Tests
{
    using System;

    public static class T
    {
        public static int Passed, Failed;

        public static void Check(bool ok, string what)
        {
            if (ok) Passed++;
            else
            {
                Failed++;
                Console.WriteLine("FAIL: " + what);
            }
        }

        public static void Eq<TV>(TV expected, TV actual, string what)
        {
            bool ok = Equals(expected, actual);
            Check(ok, what + " (expected " + expected + ", got " + actual + ")");
        }
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "dump") { DemoTests.Dump(args.Length > 1 ? args[1] : "autopilot"); return 0; }
            LogTests.Run();
            ParserTests.Run();
            TrackerTests.Run();
            DemoTests.Run();
            Console.WriteLine(T.Passed + " passed, " + T.Failed + " failed");
            return T.Failed == 0 ? 0 : 1;
        }
    }
}
