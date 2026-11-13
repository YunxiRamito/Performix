using System;
using PreySense.Core.Configuration;

namespace PreySense.Frontend.Services
{
    /// <summary>
    /// Remembers whether the user ever answered the "Run on Startup" switch, so the first-run
    /// default can never override a later decision. It lives in the existing settings file
    /// (preysense.json → General.RunOnStartup) next to the other General preferences:
    /// null = never answered, true/false = the user's own choice.
    /// </summary>
    internal static class StartupPreferenceStore
    {
        /// <summary>Null when the user never answered (or the settings cannot be read).</summary>
        public static bool? Read()
        {
            try { return ConfigurationService.Instance.GetCurrent().General.RunOnStartup; }
            catch (Exception ex)
            {
                AppLogger.Error($"Startup preference read failed: {ex}");
                return null;
            }
        }

        public static void Write(bool enabled)
        {
            try { ConfigurationService.Instance.Update(cfg => cfg.General.RunOnStartup = enabled); }
            catch (Exception ex) { AppLogger.Error($"Startup preference save failed: {ex}"); }
        }
    }
}
