using System.Reflection;

[assembly: AssemblyTitle("Chess WinForms")]
[assembly: AssemblyVersion(ChessGame.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(ChessGame.AppInfo.Version + ".0")]

namespace ChessGame
{
    static class AppInfo
    {
        /// <summary>This build, as major.minor.patch. A release on GitHub is tagged "v" + this number.</summary>
        public const string Version = "1.1.0";

        public const string Repository = "soroka01/Chess-WinForms";
        public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    }
}
