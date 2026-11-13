// About-page facts and the one action the About card owns (IA phase 1).
//
// The Update/About page must not invent data: the version comes from the assembly's
// InformationalVersion (the same string the Settings header shows), the build time is the
// timestamp of the running Performix.exe, the architecture is the live process / OS pair, and
// the logs folder is the directory the trace log and the crash doctor actually write to
// (shell.log / crash.log / crashdiag.log all live next to the executable — ShellLog.LogPath
// and CrashDoctor.CrashLogPath agree on that).
//
// No hardware access, no network: this helper only reads metadata and opens a folder.

using PreySense.Frontend.Services;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Predex.WinUI.Services
{
    internal static class AboutInfo
    {
        /// <summary>User-facing version, e.g. "v0.1.0-alpha.1".</summary>
        public static string VersionText => UpdateCheckService.DisplayVersion;

        /// <summary>Numeric part with the pre-release suffix removed ("v" is not included).</summary>
        public static string NumericVersion => UpdateCheckService.InformationalVersion;

        /// <summary>Build timestamp of the running executable, or "Unknown" when it cannot be read.</summary>
        public static string BuildTimeText
        {
            get
            {
                try
                {
                    string? path = Environment.ProcessPath
                        ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        return File.GetLastWriteTime(path)
                            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }
                }
                catch
                {
                    // Fall through to the neutral value below: the About page must not crash.
                }
                return LocalizationService.Translate("Unknown");
            }
        }

        /// <summary>"X64 · X64" (process · OS) — what actually runs, not a project setting.</summary>
        public static string ArchitectureText =>
            $"{RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.OSArchitecture}";

        /// <summary>
        /// The folder the logs are written to (next to the executable). If that directory is
        /// gone, the per-user data folder is used and created on demand.
        /// </summary>
        public static string LogsFolderPath
        {
            get
            {
                try
                {
                    if (Directory.Exists(AppContext.BaseDirectory)) return AppContext.BaseDirectory;
                }
                catch
                {
                    // Fall through to the per-user folder.
                }

                string fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Performix");
                try { Directory.CreateDirectory(fallback); } catch { /* read-only profile: return the path anyway */ }
                return fallback;
            }
        }

        /// <summary>Opens <see cref="LogsFolderPath"/> in Explorer. Returns false when the shell refuses.</summary>
        public static bool OpenLogsFolder()
        {
            string path = LogsFolderPath;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "open" });
                ShellLog.Write("about", $"opened logs folder {path}");
                return true;
            }
            catch (Exception ex)
            {
                ShellLog.Write("about", $"open logs folder failed ({path}): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
