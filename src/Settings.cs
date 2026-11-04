using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChessGame
{
    /// <summary>Tiny key=value settings file in %APPDATA%\ChessGame. Every failure is swallowed: settings are a convenience.</summary>
    public static class Settings
    {
        static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        static readonly object gate = new object();   // callers may be on other threads (Steam DLL memory, update checker)
        static bool loaded;
        static bool unreadable;   // the file exists but could not be read: saving would overwrite it with only the new keys

        /// <summary>Tests switch this off so that they neither read nor overwrite the user's real settings.</summary>
        public static bool Persist = true;

        /// <summary>Tests: use this file instead of the real one.</summary>
#pragma warning disable 649   // assigned only by tests
        internal static string PathOverride;
#pragma warning restore 649

        static string FilePath
        {
            get
            {
                if (PathOverride != null) return PathOverride;
                return Path.Combine(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChessGame"), "settings.ini");
            }
        }

        /// <summary>Tests: forget what was loaded, the next access reads the file again.</summary>
        internal static void ResetForTest()
        {
            lock (gate)
            {
                values.Clear();
                loaded = false;
                unreadable = false;
            }
        }

        internal static bool UnreadableForTest { get { lock (gate) { return unreadable; } } }

        static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            if (!Persist) return;
            try
            {
                if (!File.Exists(FilePath)) return;   // the first run: nothing to lose
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception)
            {
                // a sharing violation, an antivirus scan, a second instance: the file may be fine, we just have not seen it
                unreadable = true;
            }
        }

        public static string Get(string key, string fallback)
        {
            lock (gate)
            {
                EnsureLoaded();
                string v;
                return values.TryGetValue(key, out v) ? v : fallback;
            }
        }

        public static int GetInt(string key, int fallback)
        {
            int n;
            return int.TryParse(Get(key, null), out n) ? n : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            string v = Get(key, null);
            if (v == null) return fallback;
            return v == "1" || v == "true";
        }

        /// <summary>A line break in a key or value would start a new line in the file, i.e. smuggle in another setting.</summary>
        static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", "").Replace("\n", "");
        }

        public static void Set(string key, string value)
        {
            key = OneLine(key);
            if (key.Length == 0 || key.IndexOf('=') >= 0) return;
            lock (gate)
            {
                EnsureLoaded();
                values[key] = OneLine(value);
            }
        }

        public static void Set(string key, int value) { Set(key, value.ToString()); }
        public static void Set(string key, bool value) { Set(key, value ? "1" : "0"); }

        public static void Save()
        {
            if (!Persist) return;
            lock (gate)
            {
                EnsureLoaded();
                if (unreadable)
                {
                    // try to read it once more; if it still cannot be read the file stays as it is
                    loaded = false;
                    unreadable = false;
                    Dictionary<string, string> mine = new Dictionary<string, string>(values);
                    EnsureLoaded();
                    if (unreadable) return;
                    foreach (KeyValuePair<string, string> kv in mine) values[kv.Key] = kv.Value;   // what was set meanwhile wins
                }
                try
                {
                    string path = FilePath;
                    string dir = Path.GetDirectoryName(path);
                    Directory.CreateDirectory(dir);
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);

                    // write a temporary file next to the real one and swap: a crash in the middle never leaves a half-written file
                    string temp = path + ".tmp";
                    File.WriteAllLines(temp, lines.ToArray(), new UTF8Encoding(false));
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                    }
                    catch (Exception)
                    {
                        try { File.Delete(temp); } catch (Exception) { }
                        throw;
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
