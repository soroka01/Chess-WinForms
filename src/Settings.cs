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
        static bool loaded;

        /// <summary>Tests switch this off so that they neither read nor overwrite the user's real settings.</summary>
        public static bool Persist = true;

        static string FilePath
        {
            get
            {
                return Path.Combine(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChessGame"), "settings.ini");
            }
        }

        static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            if (!Persist) return;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception) { }
        }

        public static string Get(string key, string fallback)
        {
            EnsureLoaded();
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
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

        public static void Set(string key, string value)
        {
            EnsureLoaded();
            values[key] = value;
        }

        public static void Set(string key, int value) { Set(key, value.ToString()); }
        public static void Set(string key, bool value) { Set(key, value ? "1" : "0"); }

        public static void Save()
        {
            if (!Persist) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }
    }
}
