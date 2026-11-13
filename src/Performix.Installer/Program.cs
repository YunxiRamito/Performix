using System.Reflection;
using System.Text;

namespace Performix.Installer;

/// <summary>
/// PerformixSetup.exe — the Windows x64 installer.
///
/// Double-click behaviour (no arguments): check the .NET 10 desktop runtime, download and install
/// it when missing, install the newest Performix release into a fresh version directory, create
/// the start-menu shortcut, launch the app, exit. Every step is idempotent, so running it again
/// on an installed machine costs one probe and a "nothing to do".
///
/// The UI is the console (see <see cref="ConsoleUi"/>): a progress bar with percentage, rate and
/// ETA. That is a deliberate constraint, not an oversight — a WinForms/WPF shell would push the
/// self-contained executable past 80 MB, and the entire point of this program is to run on a
/// machine where .NET is not installed yet.
/// </summary>
internal static class Program
{
    internal const string UserAgent = "PerformixSetup (+https://github.com/YunxiRamito/Performix)";

    private static async Task<int> Main(string[] args)
    {
        // Chinese output in a console whose code page is not UTF-8 would otherwise be mojibake.
        TryUseUtf8Console();

        CommandLineParseResult parsed = InstallerCommandLine.Parse(args);
        if (!parsed.Ok)
        {
            Console.Error.WriteLine($"参数错误：{parsed.Error}");
            Console.Error.WriteLine("用 PerformixSetup.exe --help 查看用法。");
            return InstallerExitCodes.BadArguments;
        }

        InstallerCommand command = parsed.Command;

        if (command.Mode == InstallerMode.Help)
        {
            ConsoleUi.PrintHelp(Version);
            return InstallerExitCodes.Success;
        }

        var ui = new ConsoleUi(command.Silent);

        if (command.Mode == InstallerMode.Uninstall)
        {
            try
            {
                return Uninstaller.Run(ui, command.InstallDirectory);
            }
            catch (Exception ex)
            {
                ui.Error($"卸载失败：{ex.Message}");
                return InstallerExitCodes.UnexpectedError;
            }
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            // Cancel the transfer and exit cleanly instead of killing the process: the .part
            // files stay on disk so the next run resumes instead of restarting from zero.
            eventArgs.Cancel = true;
            ui.Warn("收到中断，正在停止…");
            cancellation.Cancel();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            ui.Banner("Performix", Version);
            ui.Info($"目标：Windows x64 · 安装到 {command.InstallDirectory ?? InstallerPaths.DefaultAppRoot}");

            using HttpClient http = CreateHttpClient();
            var downloader = new SegmentedDownloader(log: ui.Detail);

            var runtimeInstaller = new RuntimeInstaller(http, ui, downloader);
            int runtimeExit = await runtimeInstaller.EnsureAsync(cancellation.Token).ConfigureAwait(false);
            if (runtimeExit != InstallerExitCodes.Success)
            {
                return runtimeExit;
            }

            var appInstaller = new AppInstaller(http, ui, downloader);
            AppInstallResult result = await appInstaller.InstallAsync(command, cancellation.Token)
                .ConfigureAwait(false);

            if (!result.Ok)
            {
                return result.ExitCode;
            }

            ui.CloseProgressLine();
            ui.Step("完成");
            ui.Ok($"Performix {result.Version} 已安装");
            ui.Info($"安装目录：{result.Directory}");
            ui.Info($"快捷方式：{InstallerPaths.StartMenuShortcutPath}");
            ui.Info("卸载命令：PerformixSetup.exe --uninstall");
            return InstallerExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            ui.Error("已取消");
            return InstallerExitCodes.UnexpectedError;
        }
        catch (Exception ex)
        {
            ui.Error($"安装失败：{ex.Message}");
            ui.Detail(ex.ToString());
            return InstallerExitCodes.UnexpectedError;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Build version, from the repository-wide InformationalVersion.</summary>
    private static string Version
        => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(Program).Assembly.GetName().Version?.ToString()
           ?? "0.0.0";

    private static HttpClient CreateHttpClient()
    {
        // No automatic decompression: the probe trusts Content-Length, and a transparently
        // decompressed body would make that length describe the compressed stream instead.
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        };

        var client = new HttpClient(handler)
        {
            // Long enough for a 55 MB runtime on a slow link; per-request cancellation still works.
            Timeout = TimeSpan.FromMinutes(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void TryUseUtf8Console()
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
        }
        catch (Exception)
        {
            // Some hosts refuse the encoding change; the default code page still works.
        }
    }
}
