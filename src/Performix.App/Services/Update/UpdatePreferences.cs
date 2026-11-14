// The "Automatic updates" switch: one stored answer, one read path.
//
// WHERE IT LIVES: preysense.json → General.AutoUpdate (Performix.Core.Configuration.GeneralConfig).
//
// Why preysense.json and not appearance.json:
//   • It is a product preference (like Run on Startup / Hardware tuning), not a look. The
//     appearance store owns theme / material / window style / accent and is written by the theme
//     page; an update preference living there would make the update path depend on a file that
//     the theme page rewrites.
//   • preysense.json is on the self-update preserve whitelist (docs\UPDATE-DESIGN.md §2.4.3), so
//     the answer survives the very update it controls. appearance.json is not part of that
//     contract.
//   • The Core schema is where the rest of the app already reads its persisted switches from, so
//     there is no second "where did I save that" question.
//
// DEFAULT: ON. "Never answered" and "answered on" are the same behaviour, which is why the field
// is a plain bool and not bool? like RunOnStartup.
//
// OFF MEANS OFFLINE: the value read here gates the automatic loop (UpdateAutoRunner), and the
// loop is the only thing that touches the network without the user pressing a button.

using PreySense.Core.Configuration;
using System;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Reads and writes the persisted "Automatic updates" answer.</summary>
    internal static class UpdatePreferences
    {
        /// <summary>What a fresh profile gets (the brief: on by default).</summary>
        public const bool DefaultAutoUpdate = true;

        /// <summary>
        /// Verification only: <c>--update-no-auto</c> turns the automatic loop off for this run
        /// without touching the saved answer (the acceptance scripts must not race it).
        /// </summary>
        public const string NoAutoSwitch = "--update-no-auto";

        private static bool _runOverride;
        private static bool _hasRunOverride;

        /// <summary>True when automatic updates may run in this process.</summary>
        public static bool AutoUpdate
        {
            get
            {
                if (_hasRunOverride && !_runOverride) return false;
                return ReadPersisted();
            }
        }

        /// <summary>The stored answer, ignoring the per-run override (what the page shows).</summary>
        public static bool PersistedAutoUpdate => ReadPersisted();

        private static bool ReadPersisted()
        {
            try
            {
                return ConfigurationService.Instance.GetCurrent().General.AutoUpdate;
            }
            catch (Exception ex)
            {
                // A damaged configuration file must not silently disable updates, and it must not
                // take the shell down either: log once and behave like a fresh profile.
                ShellLog.Write("update",
                    $"auto-update preference unreadable ({ex.GetType().Name}: {ex.Message}); using {DefaultAutoUpdate}");
                return DefaultAutoUpdate;
            }
        }

        /// <summary>
        /// Stores the answer. Returns the value that is now in effect (the stored one), so a
        /// failed write leaves the switch showing the truth instead of the click.
        /// </summary>
        public static bool SetAutoUpdate(bool enabled)
        {
            try
            {
                ConfigurationService.Instance.Update(config => config.General.AutoUpdate = enabled);
                ShellLog.Write("update", $"auto-update = {enabled} (saved to {ConfigurationService.Instance.ConfigPath})");
                return enabled;
            }
            catch (Exception ex)
            {
                ShellLog.Write("update", $"auto-update save failed: {ex.GetType().Name}: {ex.Message}");
                return ReadPersisted();
            }
        }

        /// <summary>Reads the verification opt-out once, from the command line.</summary>
        public static void ApplyCommandLine(string[]? args)
        {
            if (args is null) return;
            if (!UpdateOptions.HasFlag(args, NoAutoSwitch)) return;
            _hasRunOverride = true;
            _runOverride = false;
            ShellLog.Write("update", $"{NoAutoSwitch}: automatic updates off for this run");
        }
    }
}
