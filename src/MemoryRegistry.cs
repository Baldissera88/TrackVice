namespace TrackVice
{
    using System;
    using System.Collections.Generic;

    // An in-memory registry, used by the demo and the tests.
    public sealed class MemoryRegistry : IRegSource
    {
        readonly Dictionary<string, Dictionary<string, RegValue>> keys = new Dictionary<string, Dictionary<string, RegValue>>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> denied = new List<string>();
        readonly Dictionary<string, DateTime> written = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // When set, every value written records this as its key's write time.
        public DateTime? Clock;

        public DateTime? WriteTime(string path)
        {
            DateTime t;
            return written.TryGetValue(path, out t) ? (DateTime?)t : null;
        }

        public List<string> Denied
        {
            get { return denied; }
        }

        public void Set(string path, string name, string kind, string data)
        {
            Ensure(path)[name] = new RegValue(kind, data);
            if (Clock.HasValue) written[path] = Clock.Value;
        }

        // Makes a key exist without a value.
        public void Touch(string path)
        {
            Ensure(path);
        }

        public void Dword(string path, string name, int data)
        {
            Set(path, name, "REG_DWORD", data.ToString());
        }

        public void Sz(string path, string name, string data)
        {
            Set(path, name, "REG_SZ", data);
        }

        Dictionary<string, RegValue> Ensure(string path)
        {
            Dictionary<string, RegValue> v;
            if (!keys.TryGetValue(path, out v))
            {
                v = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
                keys[path] = v;
                int i = path.LastIndexOf('\\');
                if (i > 0) Ensure(path.Substring(0, i));
            }
            return v;
        }

        public string[] SubKeys(string path)
        {
            List<string> l = new List<string>();
            string prefix = path + "\\";
            foreach (string k in keys.Keys)
            {
                if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = k.Substring(prefix.Length);
                if (rest.IndexOf('\\') < 0) l.Add(rest);
            }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l.ToArray();
        }

        public string[] ValueNames(string path)
        {
            Dictionary<string, RegValue> v;
            if (!keys.TryGetValue(path, out v)) return new string[0];
            return new List<string>(v.Keys).ToArray();
        }

        public RegValue Get(string path, string name)
        {
            Dictionary<string, RegValue> v;
            RegValue r;
            return keys.TryGetValue(path, out v) && v.TryGetValue(name, out r) ? r : null;
        }
    }
}
