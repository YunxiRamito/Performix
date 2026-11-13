// XAML-side entry point for localized text.
//
// Every user-visible string lives in XAML as its English source text and goes through
// this function, e.g.  Text="{x:Bind local:Loc.T('Performance Mode')}".
// LocalizationService.Translate() resolves it against the linked UiText table (same data
// source as the WPF build, red line 7). Unknown keys fall back to the English source, so
// a missing translation can never render a raw token or an empty label.

using PreySense.Frontend.Services;

namespace Predex.WinUI
{
    internal static class Loc
    {
        public static string T(string englishSource) => LocalizationService.Translate(englishSource);
    }
}
