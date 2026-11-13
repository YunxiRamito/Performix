// WinUI 3 update check — same public surface as the WPF version.
// Checks GitHub releases and optionally opens the browser; no WPF dialog.

using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using PreySense.Helpers;

namespace PreySense.Frontend.Services
{
    public static class UpdateCheckService
    {
        /// <summary>
        /// Numeric version only (0.1.0): System.Version cannot parse a pre-release suffix, so
        /// this is what the update comparison uses.
        /// </summary>
        public static string CurrentVersion =>
            typeof(UpdateCheckService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>
        /// The version the UI shows. The csproj sets Version=0.1.0-alpha.1, which the SDK
        /// stamps into AssemblyInformationalVersion; the numeric AssemblyVersion (0.1.0.1)
        /// must stay four-part, so the Settings page header reads this string instead.
        /// </summary>
        public static string InformationalVersion
        {
            get
            {
                try
                {
                    string? info = typeof(UpdateCheckService).Assembly
                        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion;
                    if (string.IsNullOrWhiteSpace(info)) return CurrentVersion;
                    // SourceLink appends "+<commit>"; the user-facing version stops at '+'.
                    int plus = info.IndexOf('+');
                    return plus > 0 ? info[..plus] : info;
                }
                catch { return CurrentVersion; }
            }
        }

        public static string DisplayVersion => "v" + InformationalVersion;

        // Predex is its own product now, independent of PreySense, and its own repository does
        // not exist yet. The URLs below are the legacy fork feed and are deliberately NOT
        // queried (a 0.1.0-alpha build would always look older than that feed's 3.0.8, which is
        // what used to pop the browser on every start). Fill these in and flip the flag when
        // the Predex repository gets its first release.
        private const string LatestReleaseApi =
            "https://api.github.com/repos/hammadzaigham/PreySense/releases/latest";
        private const string ReleasesUrl =
            "https://github.com/hammadzaigham/PreySense/releases";

        /// <summary>False until Performix has a release feed of its own (see above).</summary>
        private static readonly bool UpdateFeedConfigured = false;

        /// <summary>
        /// Read-only view of the flag for the Updates and About page: while there is no feed
        /// the page shows "no update feed configured" and disables the check button instead of
        /// drawing a fake progress bar (UI-ARCH §2.8 / IA phase 1).
        /// </summary>
        public static bool IsUpdateFeedConfigured => UpdateFeedConfigured;

        // Allow tests to swap the HTTP client.
        internal static Func<string, Task<string>>? HttpGetOverride { get; set; }

        public static async Task CheckForUpdatesAsync()
        {
            if (!UpdateFeedConfigured)
            {
                // No Predex release feed yet: stay completely offline. Nothing pops a browser,
                // nothing compares against the fork's 3.0.8 version line.
                Debug.WriteLine("[Predex] update check skipped: no Predex release feed configured");
                Predex.WinUI.Services.ShellLog.Write("update", "skipped: Predex has no release feed yet (offline by design)");
                return;
            }

            try
            {
                string json;
                if (HttpGetOverride is { } fetch)
                {
                    json = await fetch(LatestReleaseApi);
                }
                else
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Performix");
                    client.Timeout = TimeSpan.FromSeconds(8);
                    json = await client.GetStringAsync(LatestReleaseApi);
                }

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp))
                    return;

                string? tag = tagProp.GetString();
                if (string.IsNullOrWhiteSpace(tag)) return;

                // Strip leading 'v' for comparison.
                string remoteVer = tag.TrimStart('v', 'V');
                if (!Version.TryParse(remoteVer, out Version? remote)) return;
                if (!Version.TryParse(CurrentVersion, out Version? local)) return;
                if (remote <= local) return;

                // A newer version is available.
                // Open the browser — no WPF dialog, just a notification via Debug/AppNotification.
                Debug.WriteLine($"[Predex] Update available: {tag}");

                // If the app-notification API is available on this OS build, use it.
                // Otherwise silently skip — the update will appear on the next launch.
                TryShowUpdateNotification(tag, ReleasesUrl);
            }
            catch { /* non-fatal: network unavailable etc. */ }
        }

        private static void TryShowUpdateNotification(string version, string url)
        {
            // 不自动打开浏览器：Predex 是托盘常驻应用，启动时（尤其是 0.1.0-alpha 这种
            // 预发布版本永远小于 3.0.8 的线上版本）每次拉起都会把 releases 页甩到前台，
            // 把用户从正在做的事里踢出去。改为只记日志，发布页让用户自己决定要不要点。
            Debug.WriteLine($"[Predex] Update available: {version} — {url} (not opened)");
            Predex.WinUI.Services.ShellLog.Write("update",
                $"available: {version} — {url} (no automatic browser launch)");
        }
    }
}
