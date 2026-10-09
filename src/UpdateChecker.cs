using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>The newest release published on GitHub.</summary>
    public sealed class UpdateInfo
    {
        public string Tag = "";      // "v1.2.0"
        public Version Latest;       // 1.2.0.0
        public string Url = "";      // the release page
        public bool IsNewer;         // newer than the running build

        public string LatestText { get { return Latest.Major + "." + Latest.Minor + "." + Latest.Build; } }
    }

    /// <summary>
    /// Asks GitHub for the latest release of the game and compares it with this build. Nothing is downloaded or installed:
    /// the game only tells that a newer version exists and opens its page.
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>Tests switch this off: no network traffic, no notices.</summary>
        public static bool Enabled = true;

        const int HoursBetweenChecks = 6;

        // the answer of GitHub (the JSON of the latest release); tests replace it
        internal static Func<string> Fetch = FetchLatestRelease;

        static readonly Regex TagField = new Regex("\"tag_name\"\\s*:\\s*\"([^\"\\\\]{1,40})\"");
        static readonly Regex TagShape = new Regex(@"^[vV]?(\d+(\.\d+){1,3})$");

        public static Version Current { get { return Normalize(new Version(AppInfo.Version)); } }

        /// <summary>1.2 and 1.2.0 are the same version.</summary>
        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        /// <summary>The release described by a tag such as "v1.2.0"; null if it is not a version number.</summary>
        internal static UpdateInfo FromTag(string tag, Version current)
        {
            Match shape = TagShape.Match(tag ?? "");
            Version latest;
            if (!shape.Success || !Version.TryParse(shape.Groups[1].Value, out latest)) return null;
            latest = Normalize(latest);
            UpdateInfo info = new UpdateInfo();
            info.Tag = tag;
            info.Latest = latest;
            info.Url = AppInfo.ReleasesUrl + "/tag/" + tag;   // the tag is digits and dots only, so the address is safe to open
            info.IsNewer = latest > Normalize(current);
            return info;
        }

        /// <summary>The answer of GitHub's "latest release" call; null if it does not look like a release.</summary>
        internal static UpdateInfo Parse(string json, Version current)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = TagField.Match(json);
            return m.Success ? FromTag(m.Groups[1].Value, current) : null;
        }

        static string FetchLatestRelease()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + AppInfo.Repository + "/releases/latest");
            request.UserAgent = "Chess-WinForms/" + AppInfo.Version;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                byte[] buffer = new byte[256 * 1024];   // the tag is near the start; a long changelog is not needed
                int total = 0, n;
                while (total < buffer.Length && (n = stream.Read(buffer, total, buffer.Length - total)) > 0) total += n;
                return Encoding.UTF8.GetString(buffer, 0, total);
            }
        }

        /// <summary>
        /// Looks the newest release up on a pool thread; 'done' runs on the UI thread of 'ui' with null if the check failed
        /// (no internet, GitHub busy, no releases yet).
        /// </summary>
        public static void CheckAsync(Control ui, Action<UpdateInfo> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = null;
                try { info = Parse(Fetch(), Current); } catch (Exception) { }
                try { ui.BeginInvoke(new Action(delegate { done(info); })); } catch (Exception) { }   // the window may be gone already
            });
        }

        // ---- what the last check found survives restarts, so that the notice does not wait for the next lookup

        /// <summary>Can be switched off with updates=0 in the settings file.</summary>
        public static bool AutoCheckAllowed { get { return Enabled && Settings.GetBool("updates", true); } }

        /// <summary>The answer of the last successful check (compared with the running build), without any network traffic.</summary>
        public static UpdateInfo Remembered()
        {
            return FromTag(Settings.Get("update.tag", ""), Current);
        }

        /// <summary>The lookup is made at most every few hours: GitHub limits anonymous requests.</summary>
        public static bool DueForAutoCheck()
        {
            long ticks;
            if (!long.TryParse(Settings.Get("update.at", ""), out ticks)) return true;
            DateTime last = new DateTime(Math.Max(0, Math.Min(DateTime.MaxValue.Ticks, ticks)), DateTimeKind.Utc);
            TimeSpan age = DateTime.UtcNow - last;
            return age.TotalHours >= HoursBetweenChecks || age.TotalSeconds < 0;
        }

        public static void Record(UpdateInfo info)
        {
            Settings.Set("update.tag", info.Tag);
            Settings.Set("update.at", DateTime.UtcNow.Ticks.ToString());
            Settings.Save();
        }
    }
}
