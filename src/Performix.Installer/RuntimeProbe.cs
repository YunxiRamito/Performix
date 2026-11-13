namespace Performix.Installer;

/// <summary>Which bitness a dotnet install root serves.</summary>
public enum RuntimeArchitecture
{
    X64,
    X86,
    Unknown,
}

/// <summary>
/// One candidate <c>dotnet</c> root: <c>&lt;Root&gt;\shared\&lt;SharedName&gt;\&lt;version&gt;\</c>.
/// <paramref name="VersionDirectories"/> are the directory names found under the shared folder.
/// </summary>
public sealed record RuntimeLocation(
    RuntimeArchitecture Architecture,
    string Root,
    string SharedName,
    IReadOnlyList<string> VersionDirectories);

/// <summary>The answer to "does this machine already have what Performix needs?".</summary>
public sealed record RuntimeProbeResult(
    bool DesktopRuntimeFound,
    bool NetCoreRuntimeFound,
    IReadOnlyList<RuntimeLocation> Locations)
{
    /// <summary>Both halves of the .NET 10 runtime are present, so nothing must be downloaded.</summary>
    public bool IsSatisfied => DesktopRuntimeFound && NetCoreRuntimeFound;

    /// <summary>Newest satisfying desktop runtime version string, for the "already installed" line.</summary>
    public string? DesktopVersion { get; init; }

    /// <summary>Newest satisfying Microsoft.NETCore.App version string.</summary>
    public string? NetCoreVersion { get; init; }

    /// <summary>
    /// Something 10.x exists but only as x86 or only as a prerelease: worth telling the user
    /// (it explains why we still download 55 MB) instead of silently reinstalling.
    /// </summary>
    public bool SawInsufficient10x
        => Locations.Count > 0 && !IsSatisfied;
}

/// <summary>
/// Detects the .NET 10 desktop runtime.
///
/// What "installed" means here — and why it is deliberately strict:
/// * BOTH <c>Microsoft.WindowsDesktop.App</c> and <c>Microsoft.NETCore.App</c> must have a
///   <c>10.*</c> version that is x64 and not a prerelease. The desktop runtime is what Performix
///   links against, but the shared framework underneath is what actually fails at startup, so a
///   half-installed 10.x must not be treated as good.
/// * A prerelease (10.0.0-preview.1) does NOT count: the app targets GA .NET 10 and a preview
///   framework will not roll forward into it. Downloading the GA runtime over it is harmless
///   (the installer is side-by-side), so the strict direction is the safe one.
/// * x86 does not count: Performix is built for win-x64 and cannot load an x86 framework.
///   x86 locations are still scanned and reported, because "I do have .NET 10" next to a 55 MB
///   download needs the explanation.
///
/// Multiple roots are scanned, not one: <c>%ProgramFiles%\dotnet</c> (the normal install),
/// <c>%ProgramFiles(x86)%\dotnet</c>, and <c>DOTNET_ROOT</c> / <c>DOTNET_ROOT_X64</c> /
/// <c>DOTNET_ROOT_X86</c> (portable/dev layouts, which is exactly how this repository's CI and
/// the build machine are set up).
/// </summary>
public static class RuntimeProbe
{
    public const string DesktopSharedName = "Microsoft.WindowsDesktop.App";
    public const string NetCoreSharedName = "Microsoft.NETCore.App";
    public const int RequiredMajor = 10;

    /// <summary>
    /// Scans the real machine. Never throws on unreadable directories — a missing folder simply
    /// means "not found", which is the whole point of the check.
    /// </summary>
    public static IReadOnlyList<RuntimeLocation> Scan() => ScanRoots(CandidateRoots(), ListVersionDirectories);

    /// <summary>
    /// Scans an explicit set of roots with an injected directory lister — this is the seam the
    /// tests use (fake roots, no file system).
    /// </summary>
    public static IReadOnlyList<RuntimeLocation> ScanRoots(
        IEnumerable<(string Root, RuntimeArchitecture Architecture)> roots,
        Func<string, IReadOnlyList<string>> listVersionDirectories)
    {
        var locations = new List<RuntimeLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string root, RuntimeArchitecture architecture) in roots)
        {
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string sharedName in new[] { DesktopSharedName, NetCoreSharedName })
            {
                string sharedPath = SharedPath(fullRoot, sharedName);
                if (!seen.Add(sharedPath))
                {
                    continue; // the same root reached twice (ProgramFiles == DOTNET_ROOT)
                }

                IReadOnlyList<string> versions;
                try
                {
                    versions = listVersionDirectories(sharedPath);
                }
                catch (Exception)
                {
                    versions = Array.Empty<string>();
                }

                if (versions.Count == 0)
                {
                    continue;
                }

                locations.Add(new RuntimeLocation(architecture, fullRoot, sharedName, versions));
            }
        }

        return locations;
    }

    /// <summary>
    /// Every plausible dotnet root on this machine, in priority order, without duplicates.
    /// </summary>
    public static IReadOnlyList<(string Root, RuntimeArchitecture Architecture)> CandidateRoots()
    {
        var roots = new List<(string Root, RuntimeArchitecture Architecture)>();

        Add(DotnetRootUnder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            RuntimeArchitecture.X64);
        Add(DotnetRootUnder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            RuntimeArchitecture.X86);

        // DOTNET_ROOT* already points AT the dotnet root; they must not get "dotnet" appended.
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"), RuntimeArchitecture.Unknown);
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"), RuntimeArchitecture.X64);
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT_X86"), RuntimeArchitecture.X86);

        return roots;

        void Add(string? root, RuntimeArchitecture architecture)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            foreach ((string existing, _) in roots)
            {
                if (InstallerPaths.SamePath(existing, root))
                {
                    return;
                }
            }

            if (!SafeDirectoryExists(root))
            {
                return;
            }

            // A DOTNET_ROOT we cannot classify is classified by where it lives: x86 dotnet lives
            // under a path with an x86 segment, portable layouts do not.
            RuntimeArchitecture resolved = architecture == RuntimeArchitecture.Unknown
                ? InferArchitecture(root)
                : architecture;
            roots.Add((root, resolved));
        }
    }

    /// <summary>
    /// The dotnet installation root under a Program Files folder:
    /// <c>C:\Program Files</c> → <c>C:\Program Files\dotnet</c>.
    /// Kept separate (and tested) because forgetting this segment makes every probe silently
    /// report "no runtime" while the runtime sits right there — the machine then downloads 57 MB
    /// it did not need.
    /// </summary>
    public static string DotnetRootUnder(string? programFilesFolder)
        => string.IsNullOrWhiteSpace(programFilesFolder)
            ? string.Empty
            : Path.Combine(programFilesFolder, "dotnet");

    /// <summary>
    /// <c>&lt;root&gt;\shared\&lt;sharedName&gt;</c> — where the shared frameworks live.
    /// <paramref name="root"/> is the dotnet root itself (<c>…\dotnet</c>), not Program Files.
    /// </summary>
    public static string SharedPath(string root, string sharedName)
        => Path.Combine(root, "shared", sharedName);

    private static RuntimeArchitecture InferArchitecture(string root)
        => root.Contains("x86", StringComparison.OrdinalIgnoreCase)
           && !root.Contains("x64", StringComparison.OrdinalIgnoreCase)
            ? RuntimeArchitecture.X86
            : RuntimeArchitecture.X64;

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ListVersionDirectories(string sharedPath)
    {
        try
        {
            if (!Directory.Exists(sharedPath))
            {
                return Array.Empty<string>();
            }

            return Directory.GetDirectories(sharedPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Turns scanned locations into the install decision. Pure: the tests feed it fake
    /// locations and assert on the boolean.
    /// </summary>
    public static RuntimeProbeResult Evaluate(IEnumerable<RuntimeLocation>? locations)
    {
        RuntimeLocation[] all = locations?.ToArray() ?? Array.Empty<RuntimeLocation>();

        string? desktop = BestVersion(all, DesktopSharedName);
        string? netCore = BestVersion(all, NetCoreSharedName);

        return new RuntimeProbeResult(desktop is not null, netCore is not null, all)
        {
            DesktopVersion = desktop,
            NetCoreVersion = netCore,
        };
    }

    /// <summary>
    /// Newest version string in the x64 shared folders that is 10.x and not a prerelease,
    /// or <c>null</c> when there is none.
    /// </summary>
    public static string? BestVersion(IEnumerable<RuntimeLocation> locations, string sharedName)
    {
        string? best = null;
        foreach (RuntimeLocation location in locations)
        {
            if (location.Architecture != RuntimeArchitecture.X64
                || !string.Equals(location.SharedName, sharedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string directory in location.VersionDirectories)
            {
                if (!IsUsableVersion(directory))
                {
                    continue;
                }

                if (best is null || VersionText.Compare(directory, best) > 0)
                {
                    best = directory;
                }
            }
        }

        return best;
    }

    /// <summary>True when a shared-framework directory name is a usable 10.x (x64 assumed by caller).</summary>
    public static bool IsUsableVersion(string? directoryName)
    {
        if (!VersionText.TryParse(directoryName, out Version version, out string? prerelease))
        {
            return false;
        }

        return version.Major == RequiredMajor && prerelease is null;
    }
}
