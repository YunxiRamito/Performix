using System.IO.Compression;

namespace Performix.Installer;

/// <summary>
/// Unpacks the release package into a version directory.
///
/// Two things are worth being explicit about, because both have bitten release zips before:
///
/// 1. A zip may wrap everything in a single top-level folder (<c>Performix-win-x64\Performix.exe</c>)
///    or place the binaries at the root. The version directory must end up containing
///    <c>Performix.exe</c> directly, because the shortcut, the working directory and the
///    <c>preysense.json</c> lookup all assume app base == version directory. So a lone wrapper
///    folder is flattened and everything else is copied verbatim.
/// 2. The executable is verified to exist after extraction. Installing "successfully" into a
///    directory with no Performix.exe is the failure mode that only shows up when the user
///    double-clicks the shortcut.
/// </summary>
public static class PackageExtractor
{
    /// <summary>Directory-marker entries in a zip end with '/' and carry no data.</summary>
    public const char ZipDirectorySeparator = '/';

    /// <summary>
    /// If every file entry lives under one common top-level folder and nothing sits at the root,
    /// returns that folder's name; otherwise <c>null</c> (copy as-is). Pure, so the decision is
    /// tested directly on entry-name lists.
    /// </summary>
    public static string? DetectSingleRoot(IEnumerable<string>? entryNames)
    {
        if (entryNames is null)
        {
            return null;
        }

        string? root = null;
        bool sawFile = false;

        foreach (string raw in entryNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string normalized = raw.Replace('\\', ZipDirectorySeparator).TrimStart(ZipDirectorySeparator);
            if (normalized.Length == 0 || raw.EndsWith(ZipDirectorySeparator) || raw.EndsWith('\\'))
            {
                continue; // the folder entry itself
            }

            int slash = normalized.IndexOf(ZipDirectorySeparator);
            if (slash <= 0)
            {
                // A file directly at the archive root: no wrapper folder to strip.
                return null;
            }

            string first = normalized[..slash];
            if (root is null)
            {
                root = first;
            }
            else if (!string.Equals(root, first, StringComparison.OrdinalIgnoreCase))
            {
                return null; // more than one top-level folder
            }

            sawFile = true;
        }

        return sawFile ? root : null;
    }

    /// <summary>
    /// Extracts <paramref name="packagePath"/> into <paramref name="targetDirectory"/>, flattening
    /// a single wrapper folder. The target is created if needed; existing files are overwritten
    /// (the caller has already decided this version directory may be replaced).
    /// </summary>
    public static void ExtractTo(string packagePath, string targetDirectory)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("安装包不存在", packagePath);
        }

        Directory.CreateDirectory(targetDirectory);

        string scratch = Path.Combine(
            Path.GetTempPath(), "PerformixSetup-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            {
                string? root = DetectSingleRoot(archive.Entries.Select(entry => entry.FullName));
                // ExtractToDirectory refuses entries that escape the target (zip-slip), which is
                // why the archive is never unpacked straight into the install directory.
                ZipFile.ExtractToDirectory(packagePath, scratch, overwriteFiles: true);

                string source = root is null ? scratch : Path.Combine(scratch, root);
                if (!Directory.Exists(source))
                {
                    throw new IOException($"解压失败：压缩包结构异常（找不到 {root ?? "."}）");
                }

                MoveContents(source, targetDirectory);
            }
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    /// <summary>Moves every file under <paramref name="source"/> into <paramref name="destination"/>.</summary>
    private static void MoveContents(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
    }

    /// <summary>The installed executable's full path, or <c>null</c> when it is missing.</summary>
    public static string? FindExecutable(string directory, string executableName = InstallerPaths.ExecutableName)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string candidate = Path.Combine(directory, executableName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // %TEMP% cleanup is best effort; a leftover folder never breaks an install.
        }
    }
}
