// The "we just updated" hand-off from the replacement script back into the new build.
//
// scripts\invoke-update.ps1 takes -RestartArguments and appends it to the restarted process; the
// install path passes "--updated-from <old version>" (UpdateInstaller.Start). This type is the
// single parser for that switch, shared by App and the tests, so the hand-off cannot drift into a
// string nobody reads again.
//
// Dependency-free on purpose (it is linked into Performix.Tests.csproj): no App, no WinUI, no
// LocalizationService — App owns the wording.

using System;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Parses the "--updated-from &lt;version&gt;" restart hint.</summary>
    public static class UpdateLaunchNotice
    {
        /// <summary>The switch the replacement script is asked to append after a successful swap.</summary>
        public const string UpdatedFromSwitch = "--updated-from";

        /// <summary>LocalizationService key for the toast title; {0} = the version now running.</summary>
        public const string UpdatedTitleKey = "Updated to {0}";

        /// <summary>LocalizationService key for the toast detail; {0} = the version that was running.</summary>
        public const string UpdatedFromKey = "Previously version {0}";

        /// <summary>
        /// The previous version from "--updated-from 0.1.0-alpha.1" / "--updated-from=0.1.0-alpha.1",
        /// or null when the switch is absent or does not carry a readable version. Readable means
        /// SemanticVersion accepts it: a garbage value must not turn into a toast that names a
        /// version nobody ever shipped.
        /// </summary>
        public static string? ParseUpdatedFrom(string[]? args)
        {
            string? raw = UpdateOptions.OptionValue(args ?? Array.Empty<string>(), UpdatedFromSwitch);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string candidate = raw.Trim();
            return SemanticVersion.TryParse(candidate, out SemanticVersion? parsed) && parsed is not null
                ? parsed.ToString()
                : null;
        }

        /// <summary>True when this launch is the first run of a freshly installed build.</summary>
        public static bool IsPostUpdateLaunch(string[]? args) => ParseUpdatedFrom(args) is not null;
    }
}
