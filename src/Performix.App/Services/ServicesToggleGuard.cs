// Decision rules for the one switch on the Services page (settings IA v2).
//
// WHY THIS FILE EXISTS
// The switch is a single control with three different meanings, and getting one of them wrong
// is destructive: turning it off stops and disables every OEM service of the vendor (Core's
// AcerServiceDebloater also kills the OEM processes and removes the OEM scheduled tasks).
//
// UX review item 2 asked for a confirmation in front of exactly that direction and nothing
// else. The rule is pure — no WinUI, no services, no static state — so it is proven by
// tests\Performix.Tests\ServicesToggleGuardTests.cs instead of by a screenshot of a dialog, and
// a future edit that drops the confirmation (or starts asking before the harmless direction)
// fails a test rather than quietly changing what one click does to the machine.
//
// The three questions, in the order the page asks them:
//   1. Is this Toggled event even a user action? (the OneWay binding pushes IsOn back, and that
//      echo arrives as a Toggled too — acting on it would loop.)
//   2. Can this build manage the vendor's services at all? (only Acer has a backend.)
//   3. Is the user asking to STOP them? Only then is a confirmation required.

using System;

namespace Predex.WinUI.Services
{
    /// <summary>What the page should do with one Toggled event.</summary>
    public enum ServicesToggleAction
    {
        /// <summary>Not a user action (binding echo) or the vendor cannot be managed: do nothing.</summary>
        Ignore,

        /// <summary>Start / re-enable the OEM services: the safe direction, one click.</summary>
        ApplyDirectly,

        /// <summary>Stop and disable the OEM services: ask first.</summary>
        ConfirmThenApply,
    }

    public static class ServicesToggleGuard
    {
        /// <summary>
        /// Classifies one Toggled event.
        /// </summary>
        /// <param name="newIsOn">The switch state the event reports.</param>
        /// <param name="currentIsEnabled">The ViewModel's state (true = services enabled/running).</param>
        /// <param name="canManage">True when this build has a backend for the detected vendor.</param>
        public static ServicesToggleAction Classify(bool newIsOn, bool currentIsEnabled, bool canManage)
        {
            // The OneWay binding raises Toggled when it pushes the ViewModel value into the
            // control. That is an echo, not a click.
            if (newIsOn == currentIsEnabled) return ServicesToggleAction.Ignore;

            if (!canManage) return ServicesToggleAction.Ignore;

            // Switch off = "stop the OEM services".
            return newIsOn ? ServicesToggleAction.ApplyDirectly : ServicesToggleAction.ConfirmThenApply;
        }

        /// <summary>
        /// True when the user's answer to the confirmation lets the action run. A dialog that
        /// could not be shown answers <c>null</c>, and a destructive action must not proceed on
        /// an answer nobody gave.
        /// </summary>
        public static bool MayProceed(bool? confirmed) => confirmed == true;
    }
}
