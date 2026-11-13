namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    public static class Program
    {
        public const string Developer = "Daniel L. Baldissera";
        public const string BuildDate = "13 November 2026";

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static string Version
        {
            get
            {
                object[] a = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                return a.Length > 0 ? ((System.Reflection.AssemblyInformationalVersionAttribute)a[0]).InformationalVersion : typeof(Program).Assembly.GetName().Version.ToString();
            }
        }

        public static string Location
        {
            get
            {
                try
                {
                    string p = typeof(Program).Assembly.Location;
                    return string.IsNullOrEmpty(p) ? Application.ExecutablePath : p;
                }
                catch (Exception) { return Application.ExecutablePath; }
            }
        }

        public static bool IsAdmin
        {
            get
            {
                try
                {
                    return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch (Exception) { return false; }
            }
        }

        static Icon icon;

        public static Icon AppIcon()
        {
            if (icon != null) return icon;
            try
            {
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("TrackVice.ico"))
                {
                    if (s != null) icon = new Icon(s);
                }
            }
            catch (Exception) { }
            return icon;
        }

        public static string OsVersion()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (Microsoft.Win32.RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string name = Convert.ToString(k.GetValue("ProductName"));
                        string disp = Convert.ToString(k.GetValue("DisplayVersion"));
                        string build = Convert.ToString(k.GetValue("CurrentBuild"));
                        object ubr = k.GetValue("UBR");
                        int b;
                        if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                        return (name + " " + disp).Trim() + " (" + build + (ubr != null ? "." + ubr : "") + ")";
                    }
                }
            }
            catch (Exception) { }
            return Environment.OSVersion.VersionString;
        }

        public static bool RestartElevated()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception) { return false; }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            args = args ?? new string[0];
            if (args.Length > 0 && (args[0].StartsWith("/") || (args[0].StartsWith("-") && !args[0].StartsWith("--"))))
            {
                try { AttachConsole(-1); } catch (Exception) { }
                Console.WriteLine();
                return Cli.Run(args);
            }
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                MessageBox.Show(ex != null ? ex.ToString() : "Unknown error", "TrackVice", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            try { SetProcessDPIAware(); } catch (Exception) { }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
            }
            catch (InvalidOperationException) { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Dpi.Scale = Math.Max(1f, g.DpiX / 96f);
            }
            catch (Exception) { }
            Fonts.Init();
            try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch (Exception) { }
            Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                if (Safe.Reporter != null) Safe.Report(e.Exception);
                else MessageBox.Show(e.Exception.Message, "TrackVice", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            StartOptions o = new StartOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string low = a.ToLowerInvariant();
                if (low == "--demo")
                {
                    o.Demo = "autopilot";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) o.Demo = args[++i];
                }
                else if (low == "--shot" && i + 1 < args.Length) o.Shot = args[++i];
                else if (low == "--at" && i + 1 < args.Length) double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out o.At);
                else if (low == "--theme" && i + 1 < args.Length) o.Theme = args[++i];
                else if (low == "--tab" && i + 1 < args.Length) int.TryParse(args[++i], out o.Tab);
                else if (low == "--select" && i + 1 < args.Length) o.Select = args[++i];
                else if (low == "--compact") o.Compact = true;
                else if (low == "--page" && i + 1 < args.Length) o.Page = args[++i];
                else if (low == "--remote" && i + 1 < args.Length) o.Remote = args[++i].ToLowerInvariant();
                else if (low == "--pick" && i + 1 < args.Length) o.Pick = args[++i];
                else if (low == "--size" && i + 1 < args.Length)
                {
                    string[] wh = args[++i].Split('x');
                    int w, h;
                    if (wh.Length == 2 && int.TryParse(wh[0], out w) && int.TryParse(wh[1], out h)) o.Size = new Size(w, h);
                }
                else if (Directory.Exists(a)) o.Folder = a;
                else if (a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(a)) o.File = a;
            }
            using (MainForm f = new MainForm(o))
            {
                Application.Run(f);
            }
            return 0;
        }
    }

    public sealed class StartOptions
    {
        public string Demo, Folder, File, Shot, Theme, Select, Page, Remote, Pick;
        public double At = -1;
        public int Tab = -1;
        public bool Compact;
        public Size Size;
    }

    // Command line use, for scripts and Intune remediations.
    public static class Cli
    {
        public static void Usage()
        {
            Console.WriteLine("TrackVice " + Program.Version + " - follows SCCM and Intune deployments on this device");
            Console.WriteLine();
            foreach (string[] r in AboutDialog.CommandLine) Console.WriteLine("  " + r[0].PadRight(34) + " " + r[1]);
            Console.WriteLine();
            Console.WriteLine("Exit codes: 0 done, 1 error, 2 failed items were found");
        }

        public static int Run(string[] args)
        {
            string report = null, csv = null, session = null, folder = null, agent = null;
            AgentOptions ao = new AgentOptions();
            int hours = 24;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].TrimStart('/', '-').ToLowerInvariant();
                    switch (a)
                    {
                        case "report": report = Next(args, ref i); break;
                        case "csv": csv = Next(args, ref i); break;
                        case "session": session = Next(args, ref i); break;
                        case "folder": folder = Next(args, ref i); break;
                        case "hours": hours = Math.Max(0, int.Parse(Next(args, ref i), CultureInfo.InvariantCulture)); break;
                        case "agent": agent = "run"; break;
                        case "agent-install": agent = "install"; break;
                        case "agent-remove": agent = "remove"; break;
                        case "upload": ao.Upload = Next(args, ref i); break;
                        case "keep": ao.KeepHours = Math.Max(1, int.Parse(Next(args, ref i), CultureInfo.InvariantCulture)); break;
                        case "once": ao.Once = true; break;
                        case "?":
                        case "h":
                        case "help": Usage(); return 0;
                        default: throw new ArgumentException("Unknown option: " + args[i]);
                    }
                }
                if (agent == "install") return Agent.Install(ao, System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (agent == "remove")
                {
                    if (!Program.IsAdmin) throw new UnauthorizedAccessException("Removing the agent needs administrator rights.");
                    Agent.Remove();
                    Console.WriteLine("The TrackVice agent is removed. Delete " + Agent.InstallDir + " to remove its files.");
                    return 0;
                }
                if (agent == "run") return Agent.Run(ao);
                if (report == null && csv == null && session == null)
                {
                    Usage();
                    return 1;
                }
                using (Engine e = folder != null ? Engine.ForFolder(folder) : Engine.ForLive(hours))
                {
                    e.Apply(e.Poll());
                    Session s = e.S;
                    if (session != null) s.Save(session);
                    if (report != null) File.WriteAllText(report, Report.Html(s, e.Now));
                    if (csv != null) Report.Csv(s, csv);
                    int failed = s.Count(State.Failed);
                    Console.WriteLine(s.Computer + ": " + s.Kind + ", " + s.Items.Count + " items, " + s.CountComplete + " done, " + s.CountActive + " in progress, " + failed + " failed, " + s.Findings.Count + " findings");
                    foreach (Finding f in s.Findings) Console.WriteLine("  " + f.SeverityName.PadRight(8) + " " + f.Title);
                    return failed > 0 ? 2 : 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("TrackVice: " + ex.Message);
                return 1;
            }
        }

        static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Missing value after " + args[i]);
            return args[++i];
        }
    }
}
