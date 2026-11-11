namespace TrackVice
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Threading;
    using Microsoft.Win32;

    public sealed class RegValue
    {
        public string Kind;
        public string Data;

        public RegValue(string kind, string data)
        {
            Kind = kind;
            Data = data;
        }
    }

    // Read-only view of the registry. Paths start with HKLM, HKCU or HKU.
    public interface IRegSource
    {
        string[] SubKeys(string path);
        string[] ValueNames(string path);
        RegValue Get(string path, string name);
        List<string> Denied { get; }
    }

    // Runs work on a background thread and waits a limited time, so a hanging call (WMI) cannot hang the app.
    public static class Guard
    {
        public static bool Run(ThreadStart work, TimeSpan timeout, out Exception error)
        {
            Exception err = null;
            Thread t = new Thread(delegate ()
            {
                try { work(); }
                catch (Exception ex) { err = ex; }
            });
            t.IsBackground = true;
            t.Name = "TrackVice reader";
            t.Start();
            bool done = t.Join(timeout);
            error = done ? err : null;
            return done;
        }
    }

    public sealed class LiveRegistry : IRegSource, IDisposable
    {
        readonly List<string> denied = new List<string>();
        readonly Dictionary<RegistryHive, RegistryKey> roots = new Dictionary<RegistryHive, RegistryKey>();
        readonly object gate = new object();
        string lastPath;
        RegistryKey lastKey;
        bool lastOwned;

        public List<string> Denied
        {
            get { return denied; }
        }

        RegistryKey Root(RegistryHive h)
        {
            RegistryKey k;
            if (!roots.TryGetValue(h, out k))
            {
                k = RegistryKey.OpenBaseKey(h, RegistryView.Registry64);
                roots[h] = k;
            }
            return k;
        }

        // Opens a key read-only. The last key stays open, because callers read the values of one key in a row.
        RegistryKey Key(string path)
        {
            if (lastPath != null && string.Equals(path, lastPath, StringComparison.OrdinalIgnoreCase)) return lastKey;
            if (lastKey != null && lastOwned) lastKey.Dispose();
            lastKey = null;
            lastOwned = false;
            lastPath = path;

            int i = path.IndexOf('\\');
            string hive = i < 0 ? path : path.Substring(0, i);
            string sub = i < 0 ? "" : path.Substring(i + 1);
            RegistryHive h;
            switch (hive.ToUpperInvariant())
            {
                case "HKLM": h = RegistryHive.LocalMachine; break;
                case "HKCU": h = RegistryHive.CurrentUser; break;
                case "HKU": h = RegistryHive.Users; break;
                default: return null;
            }
            try
            {
                RegistryKey root = Root(h);
                if (sub.Length == 0) lastKey = root;
                else
                {
                    lastKey = root.OpenSubKey(sub, false);
                    lastOwned = lastKey != null;
                }
            }
            catch (System.Security.SecurityException) { Deny(path); }
            catch (UnauthorizedAccessException) { Deny(path); }
            catch (System.IO.IOException) { }
            catch (ArgumentException) { }
            return lastKey;
        }

        void Deny(string path)
        {
            if (!denied.Contains(path)) denied.Add(path);
        }

        public string[] SubKeys(string path)
        {
            lock (gate)
            {
                RegistryKey k = Key(path);
                if (k == null) return new string[0];
                try { return k.GetSubKeyNames(); }
                catch (System.Security.SecurityException) { Deny(path); }
                catch (UnauthorizedAccessException) { Deny(path); }
                catch (Exception) { }
                return new string[0];
            }
        }

        public string[] ValueNames(string path)
        {
            lock (gate)
            {
                RegistryKey k = Key(path);
                if (k == null) return new string[0];
                try { return k.GetValueNames(); }
                catch (System.Security.SecurityException) { Deny(path); }
                catch (UnauthorizedAccessException) { Deny(path); }
                catch (Exception) { }
                return new string[0];
            }
        }

        public RegValue Get(string path, string name)
        {
            lock (gate)
            {
                RegistryKey k = Key(path);
                if (k == null) return null;
                try
                {
                    object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (v == null) return null;
                    RegistryValueKind kind = k.GetValueKind(name);
                    return new RegValue(KindName(kind), Format(v));
                }
                catch (System.Security.SecurityException) { Deny(path); }
                catch (UnauthorizedAccessException) { Deny(path); }
                catch (Exception) { }
                return null;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (lastKey != null && lastOwned) lastKey.Dispose();
                lastKey = null;
                lastPath = null;
                foreach (RegistryKey k in roots.Values) k.Dispose();
                roots.Clear();
            }
        }

        static string KindName(RegistryValueKind k)
        {
            switch (k)
            {
                case RegistryValueKind.DWord: return "REG_DWORD";
                case RegistryValueKind.QWord: return "REG_QWORD";
                case RegistryValueKind.String: return "REG_SZ";
                case RegistryValueKind.ExpandString: return "REG_EXPAND_SZ";
                case RegistryValueKind.MultiString: return "REG_MULTI_SZ";
                case RegistryValueKind.Binary: return "REG_BINARY";
                default: return "REG_NONE";
            }
        }

        public static string Format(object v)
        {
            if (v is int) return unchecked((uint)(int)v).ToString(CultureInfo.InvariantCulture);
            if (v is long) return unchecked((ulong)(long)v).ToString(CultureInfo.InvariantCulture);
            string[] multi = v as string[];
            if (multi != null) return string.Join("; ", multi);
            byte[] bytes = v as byte[];
            if (bytes != null) return Hex(bytes);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static string Hex(byte[] bytes)
        {
            StringBuilder b = new StringBuilder(bytes.Length * 3);
            int max = Math.Min(bytes.Length, 256);
            for (int i = 0; i < max; i++)
            {
                if (i > 0) b.Append(' ');
                b.Append(bytes[i].ToString("X2"));
            }
            if (bytes.Length > max) b.Append(" \u2026 (" + bytes.Length + " bytes)");
            return b.ToString();
        }
    }

}
