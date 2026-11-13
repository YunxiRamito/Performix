namespace Performix.Installer;

/// <summary>What the installer was asked to do.</summary>
public enum InstallerMode
{
    /// <summary>Install (the default when no mode switch is present).</summary>
    Install,

    /// <summary>Remove shortcuts and the app directory, keep the user config.</summary>
    Uninstall,

    /// <summary>Print usage and exit 0.</summary>
    Help,
}

/// <summary>
/// The parsed command line.
/// </summary>
/// <param name="Mode">Install / Uninstall / Help.</param>
/// <param name="VersionTag">Release tag to install (<c>--version</c>); <c>null</c> = latest.</param>
/// <param name="OfflinePackage">Local <c>.zip</c> to install instead of downloading one.</param>
/// <param name="NoLaunch">Do not start Performix.exe after installing.</param>
/// <param name="DesktopShortcut">Also create a desktop shortcut (default: start menu only).</param>
/// <param name="Silent">Suppress non-error console output.</param>
/// <param name="InstallDirectory">Override the app root (default <c>%LOCALAPPDATA%\Performix\app</c>).</param>
public sealed record InstallerCommand(
    InstallerMode Mode,
    string? VersionTag,
    string? OfflinePackage,
    bool NoLaunch,
    bool DesktopShortcut,
    bool Silent,
    string? InstallDirectory)
{
    public static InstallerCommand Default { get; } =
        new(InstallerMode.Install, null, null, false, false, false, null);
}

/// <summary>Result of <see cref="InstallerCommandLine.Parse"/>.</summary>
public sealed record CommandLineParseResult(InstallerCommand Command, string? Error)
{
    /// <summary>True when the arguments made sense; <see cref="Error"/> says why not otherwise.</summary>
    public bool Ok => Error is null;

    public static CommandLineParseResult Success(InstallerCommand command) => new(command, null);

    public static CommandLineParseResult Failure(string error) => new(InstallerCommand.Default, error);
}

/// <summary>
/// Pure command-line parser. No console, no file system, no environment — the acceptance tests
/// drive it directly (linked into tests/Performix.Tests).
///
/// Rules:
/// * Long options only (<c>--name</c>), values either <c>--name value</c> or <c>--name=value</c>.
/// * An unknown <c>--switch</c> is an error rather than being ignored: a typo such as
///   <c>--no-luanch</c> silently launching the app would be worse than refusing the run.
/// * A missing value (<c>--version</c> at the end, or followed by another switch) is an error;
///   it must never swallow the next switch as its value.
/// </summary>
public static class InstallerCommandLine
{
    public const string VersionSwitch = "--version";
    public const string OfflineSwitch = "--offline";
    public const string NoLaunchSwitch = "--no-launch";
    public const string DesktopShortcutSwitch = "--desktop-shortcut";
    public const string UninstallSwitch = "--uninstall";
    public const string SilentSwitch = "--silent";
    public const string DirectorySwitch = "--dir";
    public const string HelpSwitch = "--help";

    /// <summary>Every switch this installer understands, for the error message and --help.</summary>
    public static IReadOnlyList<string> KnownSwitches { get; } = new[]
    {
        VersionSwitch, OfflineSwitch, NoLaunchSwitch, DesktopShortcutSwitch, UninstallSwitch,
        SilentSwitch, DirectorySwitch, HelpSwitch,
    };

    private static readonly string[] HelpAliases = { "--help", "-h", "-?", "/?", "/help" };

    /// <summary>
    /// Parses <paramref name="args"/>. <c>null</c> or empty means "install with defaults".
    /// </summary>
    public static CommandLineParseResult Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return CommandLineParseResult.Success(InstallerCommand.Default);
        }

        InstallerMode mode = InstallerMode.Install;
        string? version = null;
        string? offline = null;
        string? directory = null;
        bool noLaunch = false;
        bool desktop = false;
        bool silent = false;

        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            // --help first: "/?" and "-?" do not start with a letter, and the argv[0] skip below
            // would otherwise swallow a "/?"-style help request.
            if (IsHelp(argument))
            {
                return CommandLineParseResult.Success(InstallerCommand.Default with { Mode = InstallerMode.Help });
            }

            // Only the first token may be the program name (when a caller passes args with argv[0]).
            if (i == 0 && !argument.StartsWith('-'))
            {
                continue;
            }

            string name = argument;
            string? inlineValue = null;
            int equals = argument.IndexOf('=');
            if (equals > 0)
            {
                name = argument[..equals];
                inlineValue = argument[(equals + 1)..];
            }

            switch (name.ToLowerInvariant())
            {
                case UninstallSwitch:
                    if (inlineValue is not null)
                    {
                        return CommandLineParseResult.Failure($"{UninstallSwitch} 不接受值：{inlineValue}");
                    }

                    mode = InstallerMode.Uninstall;
                    break;

                case NoLaunchSwitch:
                    if (!TryParseSwitchValue(inlineValue, out noLaunch, out string? noLaunchError))
                    {
                        return CommandLineParseResult.Failure($"{NoLaunchSwitch}: {noLaunchError}");
                    }

                    break;

                case DesktopShortcutSwitch:
                    if (!TryParseSwitchValue(inlineValue, out desktop, out string? desktopError))
                    {
                        return CommandLineParseResult.Failure($"{DesktopShortcutSwitch}: {desktopError}");
                    }

                    break;

                case SilentSwitch:
                    if (!TryParseSwitchValue(inlineValue, out silent, out string? silentError))
                    {
                        return CommandLineParseResult.Failure($"{SilentSwitch}: {silentError}");
                    }

                    break;

                case VersionSwitch:
                case OfflineSwitch:
                case DirectorySwitch:
                    string? value;
                    if (inlineValue is not null)
                    {
                        value = inlineValue;
                    }
                    else if (i + 1 < args.Count && !LooksLikeSwitch(args[i + 1]))
                    {
                        value = args[++i];
                    }
                    else
                    {
                        value = null;
                    }

                    if (value is null)
                    {
                        return CommandLineParseResult.Failure($"{name} 需要一个值（{name} <value> 或 {name}=<value>）");
                    }

                    if (value.Length == 0)
                    {
                        return CommandLineParseResult.Failure($"{name} 的值是空的");
                    }

                    switch (name.ToLowerInvariant())
                    {
                        case VersionSwitch: version = value; break;
                        case OfflineSwitch: offline = value; break;
                        default: directory = value; break;
                    }

                    break;

                default:
                    return CommandLineParseResult.Failure(
                        $"未知参数：{argument}（用 --help 查看支持的参数）");
            }
        }

        if (mode == InstallerMode.Uninstall)
        {
            if (offline is not null || version is not null)
            {
                // Not fatal in spirit, but silently ignoring them would be a lie about what ran.
                return CommandLineParseResult.Failure($"{UninstallSwitch} 不能和 --version / --offline 一起用");
            }

            return CommandLineParseResult.Success(
                InstallerCommand.Default with { Mode = InstallerMode.Uninstall, Silent = silent, InstallDirectory = directory });
        }

        return CommandLineParseResult.Success(new InstallerCommand(
            InstallerMode.Install, version, offline, noLaunch, desktop, silent, directory));
    }

    /// <summary>
    /// The value of <paramref name="name"/>, or <c>null</c> when it has none.
    /// A following token that starts with <c>-</c> is a switch, not a value.
    /// <c>--name=value</c> anywhere on the line still wins.
    /// </summary>
    public static string? OptionValue(IReadOnlyList<string>? args, string name)
    {
        if (args is null || args.Count == 0)
        {
            return null;
        }

        string? found = null;
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !LooksLikeSwitch(args[i + 1]))
                {
                    found = args[i + 1];
                }
            }
            else if (argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                found = argument[(name.Length + 1)..];
            }
        }

        return found;
    }

    private static bool LooksLikeSwitch(string token) => token.Length > 0 && token[0] == '-';

    private static bool IsHelp(string argument)
        => HelpAliases.Contains(argument.ToLowerInvariant());

    private static bool TryParseSwitchValue(string? inlineValue, out bool value, out string? error)
    {
        error = null;
        if (inlineValue is null)
        {
            value = true;
            return true;
        }

        if (bool.TryParse(inlineValue, out value))
        {
            return true;
        }

        switch (inlineValue.ToLowerInvariant())
        {
            case "1": value = true; return true;
            case "0": value = false; return true;
            default:
                value = false;
                error = $"\"{inlineValue}\" 不是布尔值（true/false/1/0）";
                return false;
        }
    }
}
