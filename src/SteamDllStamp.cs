using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ChessGame
{
    /// <summary>
    /// Identifies one particular steam_api64.dll: where it lies, its size, its version and a hash of its content. Steam replaces these
    /// files when it updates a game or itself, so the game remembers the one that worked and notices when it is not the same file any more.
    /// </summary>
    sealed class SteamDllStamp
    {
        public string Path = "";
        public long Size;
        public string Version = "";
        public string Hash = "";     // first 16 hex digits of SHA-256

        /// <summary>null if the file is missing or cannot be read.</summary>
        public static SteamDllStamp Of(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists) return null;
                SteamDllStamp s = new SteamDllStamp();
                s.Path = info.FullName;
                s.Size = info.Length;
                s.Version = (FileVersionInfo.GetVersionInfo(path).FileVersion ?? "").Replace(',', '.').Replace(" ", "");
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    s.Hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
                return s;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The same bytes (wherever the file lies).</summary>
        public bool SameContent(SteamDllStamp other)
        {
            return other != null && Hash == other.Hash && Size == other.Size;
        }

        public bool SamePlace(SteamDllStamp other)
        {
            return other != null && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>For messages: "1.62.0.0 [a1b2c3]" (the hash tells builds apart when the version number did not change).</summary>
        public string Label
        {
            get { return (Version.Length > 0 ? Version + " " : "") + "[" + Hash.Substring(0, Math.Min(6, Hash.Length)) + "]"; }
        }

        // a Windows path never contains '|'
        public string Serialize() { return Path + "|" + Size + "|" + Version + "|" + Hash; }

        public static SteamDllStamp Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] p = text.Split('|');
            long size;
            if (p.Length != 4 || p[0].Length == 0 || !long.TryParse(p[1], out size) || p[3].Length < 8) return null;
            SteamDllStamp s = new SteamDllStamp();
            s.Path = p[0];
            s.Size = size;
            s.Version = p[2];
            s.Hash = p[3];
            return s;
        }
    }

    /// <summary>
    /// What the game keeps in its settings about the Steam DLL: the one it used last time (to notice that Steam changed it) and the
    /// builds that were loaded but did not work (so they are not tried again until Steam replaces them with something else).
    /// </summary>
    static class SteamDllMemory
    {
        const string LastKey = "steam.dll";
        const string BadKey = "steam.dll.bad";
        const int MaxBad = 8;

        public static SteamDllStamp Recall()
        {
            return SteamDllStamp.Parse(Settings.Get(LastKey, ""));
        }

        public static void Remember(SteamDllStamp stamp)
        {
            Settings.Set(LastKey, stamp.Serialize());
            Settings.Save();
        }

        static List<string> BadList()
        {
            List<string> list = new List<string>();
            foreach (string h in Settings.Get(BadKey, "").Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) list.Add(h);
            return list;
        }

        public static bool AnyBad { get { return BadList().Count > 0; } }

        public static bool IsBad(SteamDllStamp stamp)
        {
            return stamp != null && BadList().Contains(stamp.Hash);
        }

        /// <summary>This build was loaded but failed (a function behaves differently than expected): do not choose it again.</summary>
        public static void MarkBad(SteamDllStamp stamp)
        {
            List<string> list = BadList();
            if (!list.Contains(stamp.Hash)) list.Add(stamp.Hash);
            while (list.Count > MaxBad) list.RemoveAt(0);
            Settings.Set(BadKey, string.Join(",", list.ToArray()));
            SteamDllStamp last = Recall();
            if (last != null && last.Hash == stamp.Hash) Settings.Set(LastKey, "");
            Settings.Save();
        }

        /// <summary>
        /// What happened to the DLL since the last run, for the user: null if it is the same file in the same place (or if there
        /// was no earlier run). 'now' is the DLL that is in use.
        /// </summary>
        public static string DescribeChange(SteamDllStamp before, SteamDllStamp now)
        {
            if (before == null || now == null) return null;
            if (before.SameContent(now))
                return before.SamePlace(now) ? null : "steam_api64.dll теперь берётся из другой папки: " + now.Path;
            if (before.SamePlace(now))
                return "steam_api64.dll обновился (" + before.Label + " → " + now.Label + ")";
            if (!File.Exists(before.Path))
                return "прежний steam_api64.dll пропал (" + before.Path + "), взят другой: " + now.Path + " (" + now.Label + ")";
            return "steam_api64.dll заменён другим файлом (" + before.Label + " → " + now.Label + "): " + now.Path;
        }

        /// <summary>For the case when nothing usable was found: did the file that worked last time change or disappear?</summary>
        public static string DescribeLostFile(SteamDllStamp before, SteamDllStamp atSamePath)
        {
            if (before == null) return null;
            if (atSamePath == null) return "steam_api64.dll, которым игра пользовалась раньше, пропал (" + before.Path + ")";
            if (!before.SameContent(atSamePath))
                return "steam_api64.dll, которым игра пользовалась раньше, изменился (" + before.Label + " → " + atSamePath.Label + ")";
            return null;
        }
    }
}
