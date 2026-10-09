using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ChessGame
{
    /// <summary>Steam is missing, not running, or unusable. The message is meant for the user.</summary>
    public sealed class SteamUnavailableException : Exception
    {
        public SteamUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Raw calls into the Steamworks "flat" C API (steam_api64.dll). The DLL is not shipped with this game:
    /// it is taken from the Steam installation (any Steam game contains a copy) after checking Valve's signature.
    /// </summary>
    static class SteamNative
    {
        public const string DllName = "steam_api64.dll";
        public const uint SpacewarAppId = 480;

        static IntPtr module;
        public static string LoadedFrom { get; private set; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        // Functions whose name or availability depends on the DLL's SDK version are bound at run time:
        // Steam games ship DLLs of many ages, and a missing export must not crash the program.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int InitFlatFn(byte[] errMsg1024);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool InitOldFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr InterfaceFn();

        static InitFlatFn initFlat;
        static InitOldFn initOld;
        static InterfaceFn matchmakingFn, userFn, friendsFn;

        /// <summary>Initialises the Steam API. 0 = success, 2 = Steam client not found, 3 = Steam client too old.</summary>
        public static int InitApi(byte[] errMsg1024)
        {
            if (initFlat != null) return initFlat(errMsg1024);
            if (initOld != null) return initOld() ? 0 : 2;   // the old entry point cannot say why it failed
            return 1;
        }

        public static IntPtr GetMatchmaking() { return matchmakingFn(); }
        public static IntPtr GetUser() { return userFn(); }
        public static IntPtr GetFriends() { return friendsFn(); }

        // ---- core
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_Shutdown();
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_GetHSteamPipe();
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ManualDispatch_Init();
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ManualDispatch_RunFrame(int pipe);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ManualDispatch_GetNextCallback(int pipe, IntPtr callbackMsg);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ManualDispatch_FreeLastCallback(int pipe);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ManualDispatch_GetAPICallResult(int pipe, ulong call, IntPtr buffer, int size, int expectedCallback, ref byte failed);

        // ---- interfaces
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);

        // ---- matchmaking (strings are passed as zero-terminated UTF-8 byte arrays)
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int lobbyType, int maxMembers);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_RequestLobbyList(IntPtr self);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(IntPtr self, byte[] key, byte[] value, int comparison);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(IntPtr self, int filter);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(IntPtr self, int max);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyByIndex(IntPtr self, int index);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamMatchmaking_SetLobbyOwner(IntPtr self, ulong lobby, ulong newOwner);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_ISteamFriends_GetFriendRelationship(IntPtr self, ulong steamId);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr self);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, byte[] key);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, byte[] key, byte[] value);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamMatchmaking_SetLobbyJoinable(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.I1)] bool joinable);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamMatchmaking_SendLobbyChatMsg(IntPtr self, ulong lobby, byte[] data, int size);
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_ISteamMatchmaking_GetLobbyChatEntry(IntPtr self, ulong lobby, int chatId, ref ulong user, byte[] data, int capacity, ref int entryType);

        public static byte[] Utf8Z(string s)
        {
            byte[] raw = Encoding.UTF8.GetBytes(s ?? "");
            byte[] z = new byte[raw.Length + 1];
            Array.Copy(raw, z, raw.Length);
            return z;
        }

        public static string FromUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0 && n < 4096) n++;
            byte[] bytes = new byte[n];
            Marshal.Copy(p, bytes, 0, n);
            return Encoding.UTF8.GetString(bytes);
        }

        // ---- locating and loading the DLL

        /// <summary>
        /// What differs from the DLL of the previous run (Steam updated, replaced or removed it), or null. Set by Load();
        /// the DLL has been checked in full again by then, so this is for information only.
        /// </summary>
        public static string ChangeNote { get; private set; }

        /// <summary>Loads steam_api64.dll from next to the game or from the Steam installation.</summary>
        public static void Load()
        {
            if (module != IntPtr.Zero) return;
            if (IntPtr.Size != 8)
                throw new SteamUnavailableException("Режим Steam работает только в 64-битном процессе.");

            // The DLL of the previous run: a Steam update of a game (or of Steam itself) may have changed or removed it since.
            SteamDllStamp last = SteamDllMemory.Recall();
            SteamDllStamp lastNow = last == null ? null : SteamDllStamp.Of(last.Path);

            // 1) next to the program, 2) a path from the environment, 3) the DLL of the previous run if it is byte for byte the same
            // (nothing to look for), 4) Steam games, newest DLL first. A DLL that changed goes through every check again like any other.
            List<string> tried = new List<string>();
            List<string> rejected = new List<string>();
            List<string> first = new List<string>();
            first.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName));
            string fromEnv = Environment.GetEnvironmentVariable("CHESS_STEAM_API");
            if (!string.IsNullOrEmpty(fromEnv)) first.Add(fromEnv);
            if (last != null && last.SameContent(lastNow)) first.Add(last.Path);
            first.AddRange(NewestFirst(FindInSteamLibraries(false)));
            if (TryLoadAny(first, tried, rejected)) { OnLoaded(last); return; }

            // nothing suitable in the usual places: look deeper inside the games (Unity games hide it in *_Data\Plugins)
            if (TryLoadAny(NewestFirst(FindInSteamLibraries(true)), tried, rejected)) { OnLoaded(last); return; }

            string lost = SteamDllMemory.DescribeLostFile(last, lastNow);
            string prefix = lost == null ? "" : lost + ": похоже, Steam обновил файлы.\n";
            if (rejected.Count == 0)
                throw new SteamUnavailableException(
                    prefix + "Не найден steam_api64.dll. Положите steam_api64.dll (его можно взять из папки любой современной игры Steam) " +
                    "рядом с Chess.exe.");
            throw new SteamUnavailableException(
                prefix + "Нашлись только неподходящие steam_api64.dll (" + rejected[0] + ").\n" +
                "Положите свежий steam_api64.dll рядом с Chess.exe: его можно взять из папки недавно вышедшей игры Steam.");
        }

        /// <summary>A usable DLL was found: say what changed against the last run and remember this one.</summary>
        static void OnLoaded(SteamDllStamp last)
        {
            SteamDllStamp now = SteamDllStamp.Of(LoadedFrom);
            ChangeNote = SteamDllMemory.DescribeChange(last, now);
            if (now != null) SteamDllMemory.Remember(now);
        }

        /// <summary>
        /// The loaded DLL had every export but failed in use: remember its content, so that the next start takes another file
        /// (this one is tried again as soon as Steam replaces it with a different build).
        /// </summary>
        public static void MarkLoadedBad()
        {
            SteamDllStamp stamp = LoadedFrom == null ? null : SteamDllStamp.Of(LoadedFrom);
            if (stamp != null) SteamDllMemory.MarkBad(stamp);
        }

        internal static bool TryLoadAnyForTest(IEnumerable<string> paths, List<string> tried, List<string> rejected)
        {
            return TryLoadAny(paths, tried, rejected);
        }

        /// <summary>Loads the first DLL that is signed by Valve, has every function we call and did not fail before.</summary>
        static bool TryLoadAny(IEnumerable<string> paths, List<string> tried, List<string> rejected)
        {
            foreach (string path in paths)
            {
                string key = path.ToLowerInvariant();
                if (tried.Contains(key)) continue;
                tried.Add(key);
                try
                {
                    if (!File.Exists(path) || !IsSignedByValve(path)) continue;
                    if (SteamDllMemory.AnyBad && SteamDllMemory.IsBad(SteamDllStamp.Of(path)))
                    {
                        rejected.Add(path + ": этот файл уже не заработал (будет проверен снова, когда Steam его обновит)");
                        continue;
                    }
                    IntPtr h = LoadLibraryW(path);
                    if (h == IntPtr.Zero) continue;
                    List<string> missing = MissingExports(h);
                    if (missing.Count == 0)
                    {
                        module = h;
                        LoadedFrom = path;
                        Bind(h);
                        return true;
                    }
                    FreeLibrary(h);
                    rejected.Add(path + ": нет " + missing[0]);
                }
                catch (Exception) { }
            }
            return false;
        }

        static List<string> NewestFirst(IEnumerable<string> paths)
        {
            List<KeyValuePair<Version, string>> list = new List<KeyValuePair<Version, string>>();
            foreach (string path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    list.Add(new KeyValuePair<Version, string>(
                        ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion), path));
                }
                catch (Exception) { }
            }
            list.Sort(delegate(KeyValuePair<Version, string> a, KeyValuePair<Version, string> b) { return b.Key.CompareTo(a.Key); });
            List<string> result = new List<string>();
            foreach (KeyValuePair<Version, string> kv in list) result.Add(kv.Value);
            return result;
        }

        /// <summary>Names of the functions this program needs that the loaded DLL does not export (empty = usable).</summary>
        internal static List<string> MissingExports(IntPtr dll)
        {
            List<string> missing = new List<string>();
            foreach (System.Reflection.MethodInfo m in typeof(SteamNative).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.GetCustomAttributes(typeof(DllImportAttribute), false).Length == 0) continue;
                if (GetProcAddress(dll, m.Name) == IntPtr.Zero) missing.Add(m.Name);
            }
            if (GetProcAddress(dll, "SteamAPI_InitFlat") == IntPtr.Zero && GetProcAddress(dll, "SteamAPI_Init") == IntPtr.Zero)
                missing.Add("SteamAPI_InitFlat");
            List<string> exports = ExportNames(dll);
            foreach (string prefix in new string[] { "SteamAPI_SteamMatchmaking_v", "SteamAPI_SteamUser_v", "SteamAPI_SteamFriends_v" })
                if (HighestVersioned(exports, prefix) == null) missing.Add(prefix + "NNN");
            return missing;
        }

        static void Bind(IntPtr dll)
        {
            IntPtr p = GetProcAddress(dll, "SteamAPI_InitFlat");
            if (p != IntPtr.Zero) initFlat = (InitFlatFn)Marshal.GetDelegateForFunctionPointer(p, typeof(InitFlatFn));
            else
            {
                p = GetProcAddress(dll, "SteamAPI_Init");
                initOld = (InitOldFn)Marshal.GetDelegateForFunctionPointer(p, typeof(InitOldFn));
            }
            List<string> exports = ExportNames(dll);
            matchmakingFn = BindInterface(dll, exports, "SteamAPI_SteamMatchmaking_v");
            userFn = BindInterface(dll, exports, "SteamAPI_SteamUser_v");
            friendsFn = BindInterface(dll, exports, "SteamAPI_SteamFriends_v");
        }

        static InterfaceFn BindInterface(IntPtr dll, List<string> exports, string prefix)
        {
            string name = HighestVersioned(exports, prefix);
            return (InterfaceFn)Marshal.GetDelegateForFunctionPointer(GetProcAddress(dll, name), typeof(InterfaceFn));
        }

        /// <summary>Among exports like SteamAPI_SteamUser_v021, v023 ... the one with the highest number.</summary>
        static string HighestVersioned(List<string> exports, string prefix)
        {
            string best = null;
            int bestNumber = -1;
            foreach (string name in exports)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int n;
                if (int.TryParse(name.Substring(prefix.Length), out n) && n > bestNumber) { bestNumber = n; best = name; }
            }
            return best;
        }

        /// <summary>Reads the export table of a DLL that is already mapped into this process (64-bit).</summary>
        static List<string> ExportNames(IntPtr b)
        {
            List<string> names = new List<string>();
            int pe = Marshal.ReadInt32(b, 0x3C);
            int dirRva = Marshal.ReadInt32(b, pe + 24 + 112);   // IMAGE_DIRECTORY_ENTRY_EXPORT of a PE32+ header
            if (dirRva == 0) return names;
            int count = Marshal.ReadInt32(b, dirRva + 24);
            int namesRva = Marshal.ReadInt32(b, dirRva + 32);
            for (int i = 0; i < count; i++)
            {
                IntPtr p = new IntPtr(b.ToInt64() + Marshal.ReadInt32(b, namesRva + 4 * i));
                names.Add(Marshal.PtrToStringAnsi(p));
            }
            return names;
        }

        static Version ParseVersion(string s)
        {
            try { return new Version(s.Replace(',', '.').Replace(" ", "")); }
            catch (Exception) { return new Version(0, 0); }
        }

        static bool IsSignedByValve(string path)
        {
            try
            {
                X509Certificate cert = X509Certificate.CreateFromSignedFile(path);
                return cert.Subject.IndexOf("Valve", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception) { return false; }
        }

        static IEnumerable<string> FindInSteamLibraries(bool deep)
        {
            List<string> libraries = new List<string>();
            string steam = null;
            try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch (Exception) { }
            if (string.IsNullOrEmpty(steam))
            {
                try { steam = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string; } catch (Exception) { }
            }
            if (string.IsNullOrEmpty(steam)) yield break;
            steam = steam.Replace('/', '\\');
            libraries.Add(steam);
            try
            {
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (string line in File.ReadAllLines(vdf))
                    {
                        int i = line.IndexOf("\"path\"", StringComparison.Ordinal);
                        if (i < 0) continue;
                        int a = line.IndexOf('"', i + 6);
                        int b = a < 0 ? -1 : line.IndexOf('"', a + 1);
                        if (a >= 0 && b > a) libraries.Add(line.Substring(a + 1, b - a - 1).Replace(@"\\", @"\"));
                    }
                }
            }
            catch (Exception) { }

            string[] subfolders = { "", "bin", @"bin\x64", "x64", "win64", @"Binaries\Win64" };
            foreach (string lib in libraries)
            {
                string common = Path.Combine(lib, @"steamapps\common");
                string[] games;
                try { games = Directory.GetDirectories(common); }
                catch (Exception) { continue; }
                foreach (string game in games)
                {
                    if (!deep)
                    {
                        foreach (string sub in subfolders)
                            yield return Path.Combine(Path.Combine(game, sub), DllName);
                    }
                    else
                    {
                        foreach (string found in FindBelow(game, 4)) yield return found;
                    }
                }
            }
        }

        /// <summary>steam_api64.dll files inside a folder, looking at most 'depth' levels down.</summary>
        static IEnumerable<string> FindBelow(string folder, int depth)
        {
            string file = Path.Combine(folder, DllName);
            if (File.Exists(file)) yield return file;
            if (depth <= 0) yield break;
            string[] subs;
            try { subs = Directory.GetDirectories(folder); }
            catch (Exception) { yield break; }
            foreach (string sub in subs)
                foreach (string found in FindBelow(sub, depth - 1))
                    yield return found;
        }
    }

    /// <summary>
    /// Talks to the running Steam client with the Spacewar AppID (480): initialises the API once, pumps its
    /// callbacks from a UI timer and offers lobby operations. Everything is single-threaded (UI thread).
    /// </summary>
    public sealed class SteamLobbyService : ILobbyService
    {
        // callback ids (steam_api interfaces)
        const string NotLoggedInText = "Вы не вошли в Steam. Войдите в аккаунт в клиенте Steam и повторите.";

        const int CbApiCallCompleted = 703;
        const int CbLobbyEnter = 504;
        const int CbLobbyChatUpdate = 506;
        const int CbLobbyChatMsg = 507;
        const int CbLobbyMatchList = 510;
        const int CbLobbyCreated = 513;

        const int LobbyTypePublic = 2;      // friends-only lobbies would not show up in RequestLobbyList
        const int RelationshipFriend = 3;   // k_EFriendRelationshipFriend

        [StructLayout(LayoutKind.Sequential)]
        struct CallbackMsg
        {
            public int User;
            public int Callback;
            public IntPtr Param;
            public int ParamSize;
        }

        static SteamLobbyService instance;

#pragma warning disable 649   // set only by tests
        /// <summary>Test switch: keep our own lobby in FindLobbies results.</summary>
        internal static bool ListOwnLobbies;
#pragma warning restore 649

        readonly IntPtr matchmaking, user, friends;
        readonly int pipe;
        readonly Timer pump = new Timer();
        readonly Dictionary<ulong, Action<byte[], bool>> pending = new Dictionary<ulong, Action<byte[], bool>>();
        readonly IntPtr msgBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CallbackMsg)));

        public ulong MySteamId { get; private set; }
        public string MyName { get; private set; }
        public event Action<ulong, ulong, string> ChatReceived;
        public event Action<ulong, ulong, bool> MemberChanged;

        /// <summary>One Steam session per process. Throws SteamUnavailableException with a user-readable message.</summary>
        public static SteamLobbyService Acquire()
        {
            if (instance == null) instance = new SteamLobbyService();
            return instance;
        }

        /// <summary>Shuts the Steam API down (call when the program exits).</summary>
        public static void Release()
        {
            if (instance == null) return;
            instance.pump.Stop();
            try { SteamNative.SteamAPI_Shutdown(); } catch (Exception) { }
            instance = null;
        }

        SteamLobbyService()
        {
            SteamNative.Load();
            PrepareAppId();

            byte[] err = new byte[1024];
            int result;
            try { result = SteamNative.InitApi(err); }
            catch (Exception ex)
            {
                if (ex is EntryPointNotFoundException || ex is BadImageFormatException) SteamNative.MarkLoadedBad();
                throw new SteamUnavailableException("Не удалось запустить Steam API: " + ex.Message);
            }
            if (result != 0)
            {
                string detail = Encoding.UTF8.GetString(err).TrimEnd('\0');
                string hint = result == 3
                    ? "Клиент Steam слишком старый: обновите Steam и повторите."
                    : "Запустите клиент Steam и войдите в аккаунт, затем повторите.";
                throw new SteamUnavailableException("Steam недоступен. " + hint + (detail.Length > 0 ? "\n(" + detail + ")" : ""));
            }

            try
            {
                SteamNative.SteamAPI_ManualDispatch_Init();
                pipe = SteamNative.SteamAPI_GetHSteamPipe();
                matchmaking = SteamNative.GetMatchmaking();
                user = SteamNative.GetUser();
                friends = SteamNative.GetFriends();
                if (matchmaking == IntPtr.Zero || user == IntPtr.Zero || friends == IntPtr.Zero)
                    throw new SteamUnavailableException("Steam не предоставил нужные интерфейсы.");
                if (!SteamNative.SteamAPI_ISteamUser_BLoggedOn(user))
                    throw new SteamUnavailableException(NotLoggedInText);
                MySteamId = SteamNative.SteamAPI_ISteamUser_GetSteamID(user);
                MyName = NetLink.Sanitize(SteamNative.FromUtf8(SteamNative.SteamAPI_ISteamFriends_GetPersonaName(friends)));
                if (MyName.Length == 0) MyName = "Steam";
            }
            catch (EntryPointNotFoundException)
            {
                SteamNative.SteamAPI_Shutdown();
                SteamNative.MarkLoadedBad();
                throw new SteamUnavailableException("Версия steam_api64.dll не подходит (" + SteamNative.LoadedFrom + ").\n" +
                                                    "Перезапустите игру: при следующем запуске будет выбран другой файл.");
            }
            catch (SteamUnavailableException)
            {
                SteamNative.SteamAPI_Shutdown();   // do not stay "in game" after a failed attempt
                throw;
            }

            pump.Interval = 40;
            pump.Tick += delegate { Pump(); };
            pump.Start();
        }

        /// <summary>The API needs to know which app we pretend to be when we are not started by Steam.</summary>
        static void PrepareAppId()
        {
            string id = SteamNative.SpacewarAppId.ToString();
            Environment.SetEnvironmentVariable("SteamAppId", id);
            Environment.SetEnvironmentVariable("SteamGameId", id);
            // steam_appid.txt next to the exe and in the working directory are the documented way
            foreach (string dir in new string[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                try
                {
                    string file = Path.Combine(dir, "steam_appid.txt");
                    if (!File.Exists(file)) File.WriteAllText(file, id);
                }
                catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------- callbacks

        void Pump()
        {
            List<Action> after = new List<Action>();
            SteamNative.SteamAPI_ManualDispatch_RunFrame(pipe);
            while (SteamNative.SteamAPI_ManualDispatch_GetNextCallback(pipe, msgBuffer))
            {
                CallbackMsg msg = (CallbackMsg)Marshal.PtrToStructure(msgBuffer, typeof(CallbackMsg));
                try
                {
                    if (msg.Callback == CbApiCallCompleted)
                        HandleCallCompleted(msg.Param, after);
                    else
                        HandleCallback(msg.Callback, msg.Param, msg.ParamSize, after);
                }
                catch (Exception) { /* a broken callback must not stop the pump */ }
                finally
                {
                    SteamNative.SteamAPI_ManualDispatch_FreeLastCallback(pipe);
                }
            }
            foreach (Action a in after) a();   // run handlers once Steam's queue is consistent again
        }

        void HandleCallCompleted(IntPtr param, List<Action> after)
        {
            // SteamAPICallCompleted_t { uint64 call; int callback; uint32 size; }
            ulong call = (ulong)Marshal.ReadInt64(param, 0);
            int callbackId = Marshal.ReadInt32(param, 8);
            int size = Marshal.ReadInt32(param, 12);
            Action<byte[], bool> handler;
            if (!pending.TryGetValue(call, out handler)) return;
            pending.Remove(call);

            IntPtr buffer = Marshal.AllocHGlobal(Math.Max(size, 8));
            try
            {
                byte failed = 0;
                bool ok = SteamNative.SteamAPI_ManualDispatch_GetAPICallResult(pipe, call, buffer, size, callbackId, ref failed);
                byte[] data = new byte[size];
                if (ok && size > 0) Marshal.Copy(buffer, data, 0, size);
                bool success = ok && failed == 0;
                after.Add(delegate { handler(data, success); });
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        void HandleCallback(int id, IntPtr param, int size, List<Action> after)
        {
            if (id == CbLobbyChatUpdate)
            {
                // LobbyChatUpdate_t { u64 lobby; u64 userChanged; u64 makingChange; u32 stateChange; }
                ulong lobby = (ulong)Marshal.ReadInt64(param, 0);
                ulong who = (ulong)Marshal.ReadInt64(param, 8);
                uint state = (uint)Marshal.ReadInt32(param, 24);
                bool entered = (state & 1) != 0;   // k_EChatMemberStateChangeEntered; Left/Disconnected/Kicked/Banned are the other bits
                after.Add(delegate { if (MemberChanged != null) MemberChanged(lobby, who, entered); });
            }
            else if (id == CbLobbyChatMsg)
            {
                // LobbyChatMsg_t { u64 lobby; u64 user; u8 type; u32 chatId; }
                ulong lobby = (ulong)Marshal.ReadInt64(param, 0);
                int chatId = Marshal.ReadInt32(param, 20);
                ulong sender = 0;
                int type = 0;
                byte[] buf = new byte[4096];
                int n = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyChatEntry(matchmaking, lobby, chatId, ref sender, buf, buf.Length, ref type);
                if (n <= 0) return;
                int len = n;
                while (len > 0 && buf[len - 1] == 0) len--;   // Steam may include a terminating zero
                string text = Encoding.UTF8.GetString(buf, 0, len);
                after.Add(delegate { if (ChatReceived != null) ChatReceived(lobby, sender, text); });
            }
        }

        // ---------------------------------------------------------------- ILobbyService

        public ulong[] GetMembers(ulong lobby)
        {
            int n = SteamNative.SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(matchmaking, lobby);
            List<ulong> list = new List<ulong>();
            for (int i = 0; i < n; i++)
            {
                ulong id = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(matchmaking, lobby, i);
                if (id != 0) list.Add(id);
            }
            return list.ToArray();
        }

        public ulong GetOwner(ulong lobby)
        {
            return SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyOwner(matchmaking, lobby);
        }

        public bool SetOwner(ulong lobby, ulong newOwner)
        {
            return SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyOwner(matchmaking, lobby, newOwner);
        }

        public void CreateLobby(int maxMembers, Action<bool, ulong> done)
        {
            ulong call = SteamNative.SteamAPI_ISteamMatchmaking_CreateLobby(matchmaking, LobbyTypePublic, maxMembers);
            pending[call] = delegate(byte[] data, bool ok)
            {
                // LobbyCreated_t { int result; u64 lobby; }  (the u64 sits at offset 8 or 4 depending on struct packing)
                if (!ok || data.Length < 12 || BitConverter.ToInt32(data, 0) != 1) { done(false, 0); return; }
                ulong id = BitConverter.ToUInt64(data, data.Length >= 16 ? 8 : 4);
                done(id != 0, id);
            };
        }

        public void FindLobbies(Action<List<LobbyInfo>> done)
        {
            SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(matchmaking,
                SteamNative.Utf8Z(SteamLink.LobbyKey), SteamNative.Utf8Z(SteamLink.LobbyValue), 0 /* equal */);
            SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(matchmaking, 3 /* worldwide */);
            SteamNative.SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(matchmaking, 50);
            ulong call = SteamNative.SteamAPI_ISteamMatchmaking_RequestLobbyList(matchmaking);
            pending[call] = delegate(byte[] data, bool ok)
            {
                List<LobbyInfo> list = new List<LobbyInfo>();
                if (ok && data.Length >= 4)
                {
                    int count = BitConverter.ToInt32(data, 0);
                    for (int i = 0; i < count && i < 50; i++)
                    {
                        ulong id = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyByIndex(matchmaking, i);
                        if (id == 0) continue;
                        ulong owner = SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyOwner(matchmaking, id);
                        if (owner == MySteamId && !ListOwnLobbies) continue;   // our own lobby may be listed too
                        LobbyInfo info = new LobbyInfo();
                        info.Id = id;
                        info.IsFriend = owner != 0 && SteamNative.SteamAPI_ISteamFriends_GetFriendRelationship(friends, owner) == RelationshipFriend;
                        info.HostName = NetLink.Sanitize(SteamNative.FromUtf8(
                            SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyData(matchmaking, id, SteamNative.Utf8Z("host"))));
                        info.Members = SteamNative.SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(matchmaking, id);
                        int parsed;
                        string players = SteamNative.FromUtf8(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyData(matchmaking, id, SteamNative.Utf8Z("players")));
                        string watchers = SteamNative.FromUtf8(SteamNative.SteamAPI_ISteamMatchmaking_GetLobbyData(matchmaking, id, SteamNative.Utf8Z("watchers")));
                        if (int.TryParse(players, out parsed)) info.Players = parsed;
                        if (int.TryParse(watchers, out parsed)) info.Watchers = parsed;
                        list.Add(info);
                    }
                }
                done(list);
            };
        }

        public void JoinLobby(ulong lobby, Action<bool> done)
        {
            ulong call = SteamNative.SteamAPI_ISteamMatchmaking_JoinLobby(matchmaking, lobby);
            pending[call] = delegate(byte[] data, bool ok)
            {
                // LobbyEnter_t { u64 lobby; u32 permissions; bool locked; u32 response; }  response 1 = success
                done(ok && data.Length >= 20 && BitConverter.ToInt32(data, 16) == 1);
            };
        }

        public void LeaveLobby(ulong lobby)
        {
            SteamNative.SteamAPI_ISteamMatchmaking_LeaveLobby(matchmaking, lobby);
        }

        public void SetLobbyData(ulong lobby, string key, string value)
        {
            SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyData(matchmaking, lobby, SteamNative.Utf8Z(key), SteamNative.Utf8Z(value));
        }

        public void SetJoinable(ulong lobby, bool joinable)
        {
            SteamNative.SteamAPI_ISteamMatchmaking_SetLobbyJoinable(matchmaking, lobby, joinable);
        }

        public bool SendChat(ulong lobby, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            return SteamNative.SteamAPI_ISteamMatchmaking_SendLobbyChatMsg(matchmaking, lobby, data, data.Length);
        }
    }
}
