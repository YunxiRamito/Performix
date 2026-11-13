namespace Performix.Installer;

/// <summary>
/// <c>--uninstall</c>: remove the program, keep the user's settings.
///
/// The split is deliberate. <c>preysense.json</c> is the user's configuration (fan curves, modes,
/// theme) and lives inside a version directory because the app reads it from its own base
/// directory — so removing the app would take it with it. Uninstall therefore lifts it out to
/// <c>%LOCALAPPDATA%\Performix\preysense.json</c> (one level above <c>app\</c>) and prints the
/// exact path, which is also where reinstalling can find it again.
/// </summary>
public static class Uninstaller
{
    public static int Run(ConsoleUi ui, string? appRootOverride)
    {
        ui.Step("卸载 Performix");

        int removed = 0;
        foreach (string shortcut in ShortcutService.AllShortcutPaths())
        {
            if (ShortcutService.DeleteShortcut(shortcut))
            {
                ui.Ok($"已删除快捷方式：{shortcut}");
                removed++;
            }
            else
            {
                ui.Detail($"没有快捷方式：{shortcut}");
            }
        }

        string appRoot = string.IsNullOrWhiteSpace(appRootOverride)
            ? InstallerPaths.DefaultAppRoot
            : Path.GetFullPath(appRootOverride);
        string? preserved = PreserveUserConfig(ui, appRoot);

        if (Directory.Exists(appRoot))
        {
            try
            {
                Directory.Delete(appRoot, recursive: true);
                ui.Ok($"已删除程序目录：{appRoot}");
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ui.Error($"删除程序目录失败：{ex.Message}");
                ui.Info("如果 Performix 正在运行，请先退出再重试");

                if (preserved is not null)
                {
                    ui.Info($"用户配置仍在：{preserved}");
                }

                return InstallerExitCodes.InstallFailed;
            }
        }
        else
        {
            ui.Detail($"程序目录不存在：{appRoot}");
        }

        if (removed == 0)
        {
            ui.Info("这台机器上没找到 Performix 的安装痕迹（没有快捷方式，也没有程序目录）");
        }

        // The download cache belongs to a *default* install. With --dir the program lives somewhere
        // the user chose, so their %LOCALAPPDATA%\Performix\cache is not ours to delete.
        if (InstallerPaths.SamePath(appRoot, InstallerPaths.DefaultAppRoot))
        {
            string cacheRoot = InstallerPaths.CacheRoot;
            try
            {
                if (Directory.Exists(cacheRoot))
                {
                    Directory.Delete(cacheRoot, recursive: true);
                    ui.Detail($"已清理下载缓存：{cacheRoot}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ui.Warn($"下载缓存没能删掉（不影响的）：{cacheRoot}");
            }
        }

        if (preserved is not null)
        {
            ui.Ok($"用户配置已保留：{preserved}");
            ui.Info("重装后把该文件放回程序目录即可恢复设置");
        }
        else
        {
            ui.Info("没有找到 preysense.json（用户配置本来就不存在）");
        }

        return InstallerExitCodes.Success;
    }

    /// <summary>
    /// Copies the newest <c>preysense.json</c> found under the app root to the product folder.
    /// Returns where it was preserved, or <c>null</c> when there was nothing to preserve.
    /// </summary>
    private static string? PreserveUserConfig(ConsoleUi ui, string appRoot)
    {
        if (!Directory.Exists(appRoot))
        {
            return null;
        }

        try
        {
            string? source = Directory.GetDirectories(appRoot)
                .Select(directory => new VersionFolder(
                    directory, Path.GetFileName(directory), Directory.GetLastWriteTimeUtc(directory)))
                .OrderByDescending(folder => folder.LastWriteUtc)
                .Select(folder => Path.Combine(folder.Path, InstallerPaths.ConfigFileName))
                .FirstOrDefault(File.Exists)
                // A stray copy at the app root itself (older layout, or a --dir install).
                ?? (File.Exists(Path.Combine(appRoot, InstallerPaths.ConfigFileName))
                    ? Path.Combine(appRoot, InstallerPaths.ConfigFileName)
                    : null);

            if (source is null)
            {
                return null;
            }

            string destination = InstallerPaths.PreservedConfigPathFor(appRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            ui.Detail($"用户配置：{source} → {destination}");
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ui.Warn($"保留用户配置失败：{ex.Message}");
            return null;
        }
    }
}
