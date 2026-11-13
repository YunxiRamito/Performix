using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Performix.Installer;

/// <summary>Process exit codes. Documented in <c>--help</c> and asserted by the acceptance run.</summary>
public static class InstallerExitCodes
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int BadArguments = 2;
    public const int RuntimeDownloadFailed = 3;
    public const int RuntimeInstallFailed = 4;
    public const int ReleaseLookupFailed = 5;
    public const int PackageDownloadFailed = 6;
    public const int InstallFailed = 7;
}

/// <summary>
/// "Am I elevated?" without a dependency on the Windows-specific principal types (the installer
/// targets plain <c>net10.0</c> on purpose). Reads the token elevation flag directly.
/// </summary>
public static class WindowsSecurity
{
    private const int TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// True when the current process runs elevated. Unknown counts as "no": the only thing this
    /// gates is an advice line, and telling a user "you are admin" wrongly is worse than the
    /// opposite.
    /// </summary>
    public static bool IsElevated()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return false;
            }

            if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _))
            {
                return false;
            }

            return elevated != 0;
        }
        catch (Exception)
        {
            // advapi32 should always be there; if it is not, assume unelevated.
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }
}

/// <summary>
/// Makes sure the .NET 10 desktop runtime is present, installing it when it is not.
///
/// The version, URL and SHA-512 are read from the live release metadata every run — nothing is
/// hardcoded, so a 10.0.x servicing release does not need a new installer build.
/// </summary>
public sealed class RuntimeInstaller
{
    private readonly HttpClient _http;
    private readonly ConsoleUi _ui;
    private readonly SegmentedDownloader _downloader;

    public RuntimeInstaller(HttpClient http, ConsoleUi ui, SegmentedDownloader? downloader = null)
    {
        _http = http;
        _ui = ui;
        _downloader = downloader ?? new SegmentedDownloader(log: ui.Detail);
    }

    /// <summary>
    /// Returns <see cref="InstallerExitCodes.Success"/> when the runtime is (or became) usable.
    /// </summary>
    public async Task<int> EnsureAsync(CancellationToken cancellationToken)
    {
        _ui.Step("1/4 检查 .NET 10 桌面运行时");

        RuntimeProbeResult probe = RuntimeProbe.Evaluate(RuntimeProbe.Scan());
        ReportProbe(probe);
        if (probe.IsSatisfied)
        {
            _ui.Ok("运行时已安装，跳过下载");
            return InstallerExitCodes.Success;
        }

        RuntimeMetadataResult metadata = await FetchMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (!metadata.Ok || metadata.Package is null)
        {
            _ui.Error($"无法确定运行时版本：{metadata.Error}");
            return InstallerExitCodes.RuntimeDownloadFailed;
        }

        RuntimePackage package = metadata.Package;
        _ui.Info($"将安装 .NET 桌面运行时 {package.Version}（.NET {metadata.ChannelVersion ?? "10.0"}）");
        _ui.Detail(package.Url);

        string cachePath = Path.Combine(InstallerPaths.CacheRoot, package.FileName);
        ExpectedHash? expected = package.ExpectedHash;

        if (expected is null)
        {
            _ui.Warn($"releases.json 没有给出可解析的 hash（{package.Hash ?? "缺失"}），将以服务器 Content-Length 为准");
        }

        if (await ReuseCachedAsync(cachePath, expected, cancellationToken).ConfigureAwait(false))
        {
            _ui.Ok("复用已下载的运行时安装包");
        }
        else
        {
            if (!await DownloadRuntimeAsync(package, cachePath, expected, cancellationToken).ConfigureAwait(false))
            {
                return InstallerExitCodes.RuntimeDownloadFailed;
            }
        }

        return InstallRuntimeInstaller(cachePath);
    }

    private void ReportProbe(RuntimeProbeResult probe)
    {
        _ui.Info($"桌面运行时 Microsoft.WindowsDesktop.App：{probe.DesktopVersion ?? "未找到 10.x（x64）"}");
        _ui.Info($"基础运行时 Microsoft.NETCore.App：{probe.NetCoreVersion ?? "未找到 10.x（x64）"}");

        if (probe.IsSatisfied)
        {
            return;
        }

        // Say *why* a machine that visibly has dotnet still gets a 55 MB download: x86-only
        // installs and prereleases are found but rejected on purpose (see RuntimeProbe). Only
        // worth saying when something is actually going to be downloaded.
        foreach (RuntimeLocation location in probe.Locations)
        {
            foreach (string name in location.VersionDirectories.Where(name => !RuntimeProbe.IsUsableVersion(name)))
            {
                _ui.Detail($"忽略 {location.Architecture} 的 {location.SharedName} {name}（{location.Root}）");
            }
        }
    }

    private async Task<RuntimeMetadataResult> FetchMetadataAsync(CancellationToken cancellationToken)
    {
        _ui.Info($"读取运行时版本信息：{RuntimeReleaseMetadata.DefaultUrl}");
        string? json = null;
        string? lastError = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, RuntimeReleaseMetadata.DefaultUrl);
                using HttpResponseMessage response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                }
                else
                {
                    json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex.Message;
            }

            if (attempt < 3)
            {
                _ui.Warn($"读取失败（{lastError}），重试 {attempt}/2");
                await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        if (json is null)
        {
            return RuntimeMetadataResult.Failed($"下载 releases.json 失败：{lastError}");
        }

        return RuntimeReleaseMetadata.Parse(json);
    }

    /// <summary>True when the cached file is already the right package (and checksum-verified).</summary>
    private async Task<bool> ReuseCachedAsync(string path, ExpectedHash? expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (expected is null)
        {
            // Without a hash, a cached file of nonzero length is the best we can do.
            _ui.Warn("没有校验用的 hash，缓存文件将直接使用");
            return new FileInfo(path).Length > 0;
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] digest = Checksums.HashFile(path, expected.Kind);
            return Checksums.Matches(digest, expected);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DownloadRuntimeAsync(
        RuntimePackage package, string cachePath, ExpectedHash? expected, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            _ui.Info(attempt == 1 ? "下载运行时安装包" : "校验不符，重新下载");

            try
            {
                await _downloader.DownloadAsync(
                    _http, package.Url, cachePath, package.Size,
                    probe => _ui.Progress(probe), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                _ui.Error($"下载失败：{ex.Message}");
                SegmentedDownloader.DeleteArtifacts(cachePath);
                if (attempt >= 2)
                {
                    return false;
                }

                continue;
            }

            _ui.CloseProgressLine();

            if (expected is null)
            {
                _ui.Warn("发布方未提供 hash，跳过完整性校验（仅以长度判断）");
                return true;
            }

            byte[] digest = await Task.Run(
                () => Checksums.HashFile(cachePath, expected.Kind), cancellationToken).ConfigureAwait(false);
            bool matches = Checksums.Matches(digest, expected);

            _ui.Info($"{Checksums.Describe(expected.Kind)}：{Checksums.ToHex(digest)}");
            if (matches)
            {
                _ui.Ok("校验通过");
                return true;
            }

            _ui.Error($"{Checksums.Describe(expected.Kind)} 不符（期望 {Checksums.ToHex(expected.Bytes)}），删除重来");
            SegmentedDownloader.DeleteArtifacts(cachePath);
        }

        _ui.Error("运行时安装包两次校验都不符，已停止");
        return false;
    }

    /// <summary>Runs the bootstrapper silently and maps its exit code.</summary>
    private int InstallRuntimeInstaller(string installerPath)
    {
        _ui.Step("安装 .NET 10 桌面运行时");

        if (!WindowsSecurity.IsElevated())
        {
            // The bootstrapper carries a requireAdministrator manifest; launching it through the
            // shell is what raises the UAC prompt. Without elevation it would fail with 740.
            _ui.Warn("当前不是管理员，Windows 会弹出 UAC 提示，请允许");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/install /quiet /norestart",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        };

        int exitCode;
        try
        {
            using Process process = Process.Start(startInfo)
                                    ?? throw new InvalidOperationException("无法启动运行时安装程序");
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _ui.Error("运行时安装被取消（用户在 UAC 提示里点了“否”）");
            return InstallerExitCodes.RuntimeInstallFailed;
        }
        catch (Exception ex)
        {
            _ui.Error($"启动运行时安装程序失败：{ex.Message}");
            return InstallerExitCodes.RuntimeInstallFailed;
        }

        _ui.Detail($"安装程序退出码：{exitCode}（{DescribeInstallerExitCode(exitCode)}）");

        if (exitCode is not (0 or 3010))
        {
            _ui.Error($"运行时安装失败：{DescribeInstallerExitCode(exitCode)}");
            return InstallerExitCodes.RuntimeInstallFailed;
        }

        if (exitCode == 3010)
        {
            _ui.Warn("运行时已安装，需要重启系统才能完全生效");
        }

        RuntimeProbeResult after = RuntimeProbe.Evaluate(RuntimeProbe.Scan());
        if (after.IsSatisfied)
        {
            _ui.Ok($"运行时安装完成：{after.DesktopVersion}");
            return InstallerExitCodes.Success;
        }

        _ui.Warn("安装程序报告成功，但重新检测仍未找到 10.x x64 运行时；若刚装了运行时请重启后重试");
        return InstallerExitCodes.RuntimeInstallFailed;
    }

    /// <summary>MSI/bootstrapper exit codes worth naming instead of printing a bare number.</summary>
    private static string DescribeInstallerExitCode(int code) => code switch
    {
        0 => "成功",
        3010 => "成功，需要重启",
        1601 => "没有找到已安装的版本",
        1602 => "用户取消",
        1603 => "安装失败（常见原因：权限不足、另一个安装正在进行、系统策略限制）",
        1618 => "另一个安装程序正在运行，请稍后重试",
        1619 => "无法打开安装包",
        1620 => "无法读取安装包",
        1625 => "安装被系统策略禁止",
        1638 => "已有另一个版本安装（同一产品已安装）",
        1641 => "成功，正在重启",
        5100 => "已安装更高版本",
        _ => "未知退出码，请查看 %TEMP% 下的 dd_windowsdesktop_runtime*.log",
    };
}
