// Shell ↔ page contract for a page that carries its owner as a property instead of reading
// App.CurrentShell.ViewModel (docs\SHELL-CONTRACT.md §3).
//
// The shell creates every page in ShellWindow.PageTypes with a parameterless constructor and
// then assigns Vm before the page is added to PageHost. Pages implement the property so the
// assignment is explicit and testable; the ones that want a late assignment to re-run their
// x:Bind graph also raise PropertyChanged for "Vm" (Settings / Tuning) or call
// Bindings.Update() (Lighting). FansPage assigns before first load, so an auto-property is
// enough there.
//
// HomePage is deliberately not a member: it reads App.CurrentShell.ViewModel directly.

using PreySense.Frontend.ViewModels;

namespace Predex.WinUI.Pages
{
    public interface IPredexPage
    {
        MainViewModel? Vm { set; }
    }
}
