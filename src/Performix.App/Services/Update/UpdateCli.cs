// Command-line surface for the update path (`--check-update`), used by the acceptance run and
// by anyone verifying a feed without clicking through the UI.
//
//   Performix.exe --check-update
//   Performix.exe --check-update --update-feed C:\temp\update.json
//   Performix.exe --check-update --update-channel alpha --update-feed http://127.0.0.1:8099/update.json
//
// It runs one check, prints the outcome and exits; it never downloads and never installs. The
// "no release published yet" answer is a SUCCESS (exit code 0): the repository has no release,
// which is a fact about the feed, not an error in the client. Exit code 1 means the check
// itself threw, which should not happen.
//
// The report is also appended to %LOCALAPPDATA%\Performix\logs\update-check.log so the output
// survives a console that cannot show a GUI subsystem process' stdout.

using PreySense.Frontend.Services;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Predex.WinUI.Services.Update
{
    internal static class UpdateCli
    {
        public const string CheckUpdateSwitch = "--check-update";
        public const string AutoSwitch = "--update-auto";
        public const string NoInstallSwitch = "--update-no-install";

        public static bool IsCheckUpdateRequested(string[]? args) => UpdateOptions.HasFlag(args, CheckUpdateSwitch);

        /// <summary>"check" / "download" from --update-auto=…, or null (verification only).</summary>
        public static string? AutoMode(string[]? args)
            => args is null ? null : UpdateOptions.OptionValue(args, AutoSwitch)?.Trim().ToLowerInvariant();

        /// <summary>--update-no-install: run everything up to ReadyToInstall, then stop.</summary>
        public static bool IsInstallSuppressed(string[]? args) => UpdateOptions.HasFlag(args, NoInstallSwitch);

        /// <summary>Builds the options the session should use from the command line.</summary>
        public static UpdateOptions OptionsFromCommandLine(string[]? args)
        {
            UpdateOptions parsed = UpdateOptions.FromCommandLine(args);
            return new UpdateOptions
            {
                Channel = parsed.Channel,
                FeedOverride = parsed.FeedOverride,
                TempRoot = parsed.TempRoot,
                ProductVersion = UpdateCheckService.InformationalVersion,
            };
        }

        /// <summary>Runs one check and prints the report. Returns the process exit code.</summary>
        public static int RunCheck(string[]? args)
        {
            UpdateOptions options = OptionsFromCommandLine(args);
            var report = new StringBuilder();
            int exitCode = 0;

            report.AppendLine("Performix update check");
            report.AppendLine("  current   : " + UpdateCheckService.DisplayVersion + "  (channel " + options.Channel + ")");
            report.AppendLine("  feed      : " + (string.IsNullOrWhiteSpace(options.FeedOverride)
                ? "official first (GitHub Releases + npm registry + raw manifest), then accelerated (gh-proxy / ghproxy.net / jsDelivr / npmmirror)"
                : options.FeedOverride));
            report.AppendLine("  user-agent: " + options.UserAgent);

            try
            {
                using var client = new UpdateClient(options);
                UpdateCheckResult result = client.CheckAsync(options.Channel).GetAwaiter().GetResult();

                if (result.HasRelease && result.Manifest is not null)
                {
                    UpdateManifest manifest = result.Manifest;
                    bool newer = SemanticVersion.IsNewer(manifest.Version.ToString(), UpdateCheckService.InformationalVersion);
                    report.AppendLine("  result    : " + (newer ? "有新版 / update available" : "已是最新 / up to date"));
                    report.AppendLine("  version   : " + manifest.Version + "  (channel " + manifest.Channel + ", published " +
                                      manifest.PublishedAt.ToString("u", CultureInfo.InvariantCulture) + ")");
                    report.AppendLine("  source    : " + (result.Source is null ? "-" : result.Source.Label + " (" + result.Source.Id + ")"));
                    if (result.Asset is not null)
                    {
                        report.AppendLine("  asset     : " + result.Asset.Name + "  " +
                                          UpdateText.FormatBytes(result.Asset.SizeBytes) + "  sha256 " + result.Asset.Sha256);
                    }
                    report.AppendLine("  notes     : " + FirstLine(manifest.Notes));
                    if (manifest.MinVersion is not null)
                    {
                        // The hard gate: below it the package must not be applied in place.
                        bool belowMinimum = SemanticVersion.Compare(
                            SemanticVersion.Parse(UpdateCheckService.InformationalVersion),
                            manifest.MinVersion) < 0;
                        report.AppendLine("  minVersion: " + manifest.MinVersion + (belowMinimum
                            ? "  (running version is below it: in-place update refused, manual install required)"
                            : "  (running version satisfies it)"));
                    }
                }
                else if (result.NoRelease)
                {
                    report.AppendLine("  result    : 无可用发布 / no release published");
                    report.AppendLine("  reason    : " + result.FailureReason);
                }
                else
                {
                    report.AppendLine("  result    : 更新源不可用 / update source unavailable");
                    report.AppendLine("  reason    : " + result.FailureReason);
                }

                report.AppendLine("  sources   : " + result.DescribeAttempts());
                report.AppendLine("  elapsed   : " + result.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                report.AppendLine("  exit      : 0 (the check completed; nothing was downloaded or installed)");
            }
            catch (Exception ex)
            {
                exitCode = 1;
                report.AppendLine("  result    : 检查失败 / check failed");
                report.AppendLine("  error     : " + ex.GetType().Name + ": " + ex.Message);
                report.AppendLine("  exit      : 1");
            }

            string text = report.ToString();

            // A GUI-subsystem process owns no console, so the report only reaches a caller that
            // redirected the standard handles (cmd /c, Start-Process -RedirectStandardOutput).
            // Redirected output is written as UTF-8 so the zh-CN status words survive a pipe to
            // a UTF-8 reader instead of turning into code-page mojibake.
            try
            {
                if (Console.IsOutputRedirected)
                    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
            catch { /* an exotic host without a console must not break the check */ }

            // Both channels on purpose: the log line is what the verification run quotes when
            // the console handles were not inherited.
            try { Console.Out.Write(text); Console.Out.Flush(); } catch { }

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Performix", "logs", "update-check.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath,
                    "# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    " " + string.Join(' ', args ?? Array.Empty<string>()) + Environment.NewLine + text + Environment.NewLine,
                    Encoding.UTF8);
                try { Console.Out.WriteLine("  report    : " + logPath); } catch { }
            }
            catch { /* the console output is the primary evidence */ }

            return exitCode;
        }

        private static string FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(none)";
            string trimmed = text.Trim();
            int newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return newline < 0 ? trimmed : trimmed[..newline] + " …";
        }
    }
}
