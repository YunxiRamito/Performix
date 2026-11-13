using System.Text;

namespace Performix.Installer;

/// <summary>
/// Release/version string helpers. The three inputs are GitHub tags (<c>v0.1.0-alpha.2</c>),
/// runtime directory names (<c>10.0.15</c>) and the odd <c>10.0.0-preview.1</c>; they all need
/// the same "strip the v, split the prerelease, compare the numbers" treatment.
/// </summary>
public static class VersionText
{
    /// <summary>Strips a single leading <c>v</c>/<c>V</c> and trims. Never returns null.</summary>
    public static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        string trimmed = tag.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V') && char.IsDigit(trimmed[1]))
        {
            return trimmed[1..];
        }

        return trimmed;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into a numeric <see cref="Version"/> and an optional
    /// prerelease label. <c>"10.0.15"</c> → (10.0.15, null); <c>"10.0.0-preview.1"</c> →
    /// (10.0.0, "preview.1"); garbage → false.
    /// </summary>
    public static bool TryParse(string? text, out Version version, out string? prerelease)
    {
        version = new Version(0, 0);
        prerelease = null;

        string normalized = NormalizeTag(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        int dash = normalized.IndexOf('-');
        string core = dash < 0 ? normalized : normalized[..dash];
        string? suffix = dash < 0 ? null : normalized[(dash + 1)..];
        if (suffix is not null && suffix.Length == 0)
        {
            return false;
        }

        // Version.TryParse needs at least "major.minor"; a bare "10" is not a runtime directory
        // name we ever see, so rejecting it keeps the scanner honest instead of guessing.
        if (!Version.TryParse(core, out Version? parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        prerelease = suffix;
        return true;
    }

    /// <summary>Parsed version, or <c>null</c> when the text is not a version.</summary>
    public static Version? ParseOrNull(string? text)
        => TryParse(text, out Version version, out _) ? version : null;

    /// <summary>
    /// Orders two version strings: higher is newer, a stable release beats its own prereleases,
    /// and unparseable text sorts below anything parseable (so a junk directory name never wins
    /// a "which release is latest" comparison).
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        bool leftOk = TryParse(left, out Version leftVersion, out string? leftPre);
        bool rightOk = TryParse(right, out Version rightVersion, out string? rightPre);

        if (leftOk && rightOk)
        {
            int numbers = leftVersion.CompareTo(rightVersion);
            if (numbers != 0)
            {
                return numbers;
            }

            if (leftPre is null && rightPre is null)
            {
                return 0;
            }

            if (leftPre is null)
            {
                return 1; // 10.0.15 > 10.0.15-preview.1
            }

            if (rightPre is null)
            {
                return -1;
            }

            return ComparePrerelease(leftPre, rightPre);
        }

        if (leftOk)
        {
            return 1;
        }

        if (rightOk)
        {
            return -1;
        }

        return Math.Sign(string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Orders two prerelease labels the way a human reads them: <c>alpha.10</c> is newer than
    /// <c>alpha.9</c> (a plain string compare says the opposite, which made the installer pick
    /// the older build once a channel passed its ninth alpha). Dot-separated segments are compared
    /// one by one; two numeric segments compare numerically, anything else ordinal, and a label
    /// that runs out of segments first is the smaller one (<c>alpha</c> &lt; <c>alpha.1</c>).
    /// </summary>
    public static int ComparePrerelease(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(left))
        {
            return -1;
        }

        if (string.IsNullOrEmpty(right))
        {
            return 1;
        }

        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');
        int shared = Math.Min(leftParts.Length, rightParts.Length);

        for (int i = 0; i < shared; i++)
        {
            string a = leftParts[i];
            string b = rightParts[i];
            bool aNumeric = int.TryParse(a, out int aNumber);
            bool bNumeric = int.TryParse(b, out int bNumber);

            int segment = aNumeric && bNumeric
                ? aNumber.CompareTo(bNumber)
                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (segment != 0)
            {
                return Math.Sign(segment);
            }
        }

        return Math.Sign(leftParts.Length.CompareTo(rightParts.Length));
    }

    /// <summary>True when both strings name the same release (tag <c>v1.2.3</c> == <c>1.2.3</c>).</summary>
    public static bool AreSame(string? left, string? right) => Compare(left, right) == 0;
}

/// <summary>
/// Every path the installer reads or writes. Kept in one place so uninstall can delete exactly
/// what install created, and so the tests can exercise the pure parts (version directory naming,
/// prune ordering) without touching a real profile.
/// </summary>
public sealed record VersionFolder(string Path, string Name, DateTime LastWriteUtc);

public static class InstallerPaths
{
    public const string AppName = "Performix";
    public const string ExecutableName = "Performix.exe";
    public const string ConfigFileName = "preysense.json";
    public const string ShortcutName = "Performix.lnk";

    /// <summary>How many version directories survive an upgrade (the new one + the previous one).</summary>
    public const int VersionsToKeep = 2;

    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    public static string LocalAppData
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%APPDATA%</c>.</summary>
    public static string RoamingAppData
        => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary><c>%LOCALAPPDATA%\Performix</c> — the product folder; survives uninstall.</summary>
    public static string ProductRoot => Path.Combine(LocalAppData, AppName);

    /// <summary>
    /// <c>%LOCALAPPDATA%\Performix\app</c>: deliberately not Program Files. Performix itself
    /// needs administrator rights at runtime, but installing into the user profile means the
    /// installer never needs elevation to write files (the only elevated step is the runtime
    /// installer, which shares the machine-wide runtime).
    /// </summary>
    public static string DefaultAppRoot => Path.Combine(ProductRoot, "app");

    /// <summary>Where downloaded installers/packages are cached (safe to delete at any time).</summary>
    public static string CacheRoot => Path.Combine(ProductRoot, "cache");

    /// <summary>
    /// <c>preysense.json</c> lives next to the binaries (the app reads AppContext.BaseDirectory),
    /// so it sits inside a version directory that uninstall removes. This is where uninstall
    /// steps it aside, above <c>app\</c>, and prints the path.
    /// </summary>
    public static string PreservedConfigPath => PreservedConfigPathFor(DefaultAppRoot);

    /// <summary>Preserved-config location for an arbitrary app root (<c>--dir</c>).</summary>
    public static string PreservedConfigPathFor(string appRoot)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(appRoot));
        return Path.Combine(string.IsNullOrEmpty(parent) ? ProductRoot : parent, ConfigFileName);
    }

    public static string StartMenuShortcutPath => Path.Combine(
        RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", ShortcutName);

    public static string DesktopShortcutPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    /// <summary><c>&lt;appRoot&gt;\&lt;version&gt;</c>.</summary>
    public static string VersionDirectory(string appRoot, string version)
        => Path.Combine(appRoot, SanitizeVersion(version));

    /// <summary>
    /// Turns a release tag into a directory name: <c>v0.1.0-alpha.2</c> → <c>0.1.0-alpha.2</c>.
    /// Anything a file system could choke on is folded to <c>-</c>; an empty tag becomes
    /// <c>local</c> (an --offline package with no --version).
    /// </summary>
    public static string SanitizeVersion(string? tag)
    {
        string normalized = VersionText.NormalizeTag(tag);
        if (normalized.Length == 0)
        {
            return "local";
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) || c == '\\' || c == '/' ? '-' : c);
        }

        string result = builder.ToString().Trim(' ', '.');
        return result.Length == 0 ? "local" : result;
    }

    /// <summary>
    /// Decides which version directories to delete: newest first, keep <paramref name="keep"/>,
    /// delete the rest. <paramref name="protectedPath"/> (the version just installed) is never
    /// returned, even if its timestamp is odd — deleting the fresh install would be absurd.
    /// Tie-break is by name, newest name first, so the outcome is deterministic.
    /// </summary>
    public static IReadOnlyList<VersionFolder> SelectVersionsToPrune(
        IEnumerable<VersionFolder> folders,
        int keep = VersionsToKeep,
        string? protectedPath = null)
    {
        if (folders is null || keep < 0)
        {
            return Array.Empty<VersionFolder>();
        }

        VersionFolder[] ordered = folders
            .Where(folder => !string.IsNullOrEmpty(folder.Name))
            .OrderByDescending(folder => folder.LastWriteUtc)
            .ThenByDescending(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ordered
            .Skip(keep)
            .Where(folder => !SamePath(folder.Path, protectedPath))
            .ToArray();
    }

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
