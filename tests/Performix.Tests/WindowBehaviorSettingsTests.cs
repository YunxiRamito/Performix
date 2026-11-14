using Predex.WinUI.Services;
using PreySense.Core.Configuration;

namespace PreySense.Tests;

/// <summary>
/// Settings → General → "Always on Top" / "Special Key Wake".
///
/// The two things that can silently break are covered here instead of by clicking:
///   1. persistence — the answer has to survive a restart, and it must not take the other
///      General fields (language, startup answer) with it;
///   2. the decision — "Always on Top" is only real if losing focus stops hiding the panel, so
///      that rule is a pure function and is asserted directly.
/// </summary>
[TestClass]
public class WindowBehaviorSettingsTests
{
    private string _directory = null!;
    private string _path = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PreySenseWindowBehavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, ConfigurationService.ConfigFileName);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    // ── Defaults / backward compatibility ────────────────────────────────────
    [TestMethod]
    public void Defaults_AreNotOnTopAndHotkeyWakeOn()
    {
        var service = new ConfigurationService(_path);
        WindowBehaviorSettings settings = WindowBehaviorStore.Read(service);

        Assert.IsFalse(settings.AlwaysOnTop, "a fresh profile must not pin the panel on top");
        Assert.IsTrue(settings.SpecialKeyWake, "the Predator key summoned the panel before the switch existed");
    }

    [TestMethod]
    public void FileWrittenBeforeTheSwitches_ReadsAsTheOldBehaviour()
    {
        // A preysense.json from before these two members existed: General has only Language.
        File.WriteAllText(_path, """
            {"SchemaVersion":2,"General":{"Language":"zh-CN"}}
            """);

        var settings = WindowBehaviorStore.Read(new ConfigurationService(_path));

        Assert.IsFalse(settings.AlwaysOnTop);
        Assert.IsTrue(settings.SpecialKeyWake);
    }

    [TestMethod]
    public void MissingGeneralSection_ReadsAsDefaults()
    {
        Assert.IsFalse(WindowBehaviorStore.Read((PreySenseConfig?)null).AlwaysOnTop);
        Assert.IsTrue(WindowBehaviorStore.Read((PreySenseConfig?)null).SpecialKeyWake);
    }

    // ── Persistence ──────────────────────────────────────────────────────────
    [TestMethod]
    public void RoundTrip_PersistsBothSwitchesAndKeepsTheOtherGeneralFields()
    {
        var service = new ConfigurationService(_path);
        service.Update(config =>
        {
            config.General.Language = "fr-FR";
            config.General.RunOnStartup = false;
        });

        WindowBehaviorStore.Write(service, new WindowBehaviorSettings
        {
            AlwaysOnTop = true,
            SpecialKeyWake = false,
        });

        // A restart = a new service over the same file.
        var reloaded = new ConfigurationService(_path);
        WindowBehaviorSettings settings = WindowBehaviorStore.Read(reloaded);

        Assert.IsTrue(settings.AlwaysOnTop);
        Assert.IsFalse(settings.SpecialKeyWake);
        Assert.AreEqual("fr-FR", reloaded.GetCurrent().General.Language, "language must survive the window-behaviour write");
        Assert.IsFalse(reloaded.GetCurrent().General.RunOnStartup ?? true, "startup answer must survive too");

        // And the file really holds them, not just the in-memory draft.
        string json = File.ReadAllText(_path);
        StringAssert.Contains(json, "\"AlwaysOnTop\": true");
        StringAssert.Contains(json, "\"SpecialKeyWake\": false");
    }

    [TestMethod]
    public void WriteThenRead_IsStableAndIdempotent()
    {
        var service = new ConfigurationService(_path);
        var settings = new WindowBehaviorSettings { AlwaysOnTop = false, SpecialKeyWake = true };

        WindowBehaviorStore.Write(service, settings);
        WindowBehaviorStore.Write(service, settings);

        WindowBehaviorSettings read = WindowBehaviorStore.Read(new ConfigurationService(_path));
        Assert.IsFalse(read.AlwaysOnTop);
        Assert.IsTrue(read.SpecialKeyWake);
    }

    [TestMethod]
    public void Apply_WritesTheDraftWithoutSaving()
    {
        var config = new PreySenseConfig();
        WindowBehaviorStore.Apply(new WindowBehaviorSettings { AlwaysOnTop = true, SpecialKeyWake = false }, config);

        Assert.IsTrue(config.General.AlwaysOnTop);
        Assert.IsFalse(config.General.SpecialKeyWake);
        Assert.IsFalse(File.Exists(_path), "Apply must not touch the file; the caller owns the save");
    }

    [TestMethod]
    public void Write_WithNullArguments_IsANoOp()
    {
        var service = new ConfigurationService(_path);
        WindowBehaviorStore.Write(service, null!);
        WindowBehaviorStore.Write(null!, new WindowBehaviorSettings());
        Assert.IsFalse(File.Exists(_path));
    }

    // ── The decision that makes "on top" real ────────────────────────────────
    [TestMethod]
    public void AutoHide_IsSuppressedByAlwaysOnTop()
    {
        Assert.IsFalse(WindowBehaviorPolicy.AutoHideAllowed(alwaysOnTop: true, noAutoHide: false));
        Assert.IsFalse(WindowBehaviorPolicy.AutoHideAllowed(alwaysOnTop: false, noAutoHide: true));
        Assert.IsTrue(WindowBehaviorPolicy.AutoHideAllowed(alwaysOnTop: false, noAutoHide: false));
    }

    [TestMethod]
    public void ShouldHideAfterDeactivate_CoversEveryInput()
    {
        // Baseline: visible, not foreground, no popup, nothing pinned → hide.
        Assert.IsTrue(WindowBehaviorPolicy.ShouldHideAfterDeactivate(
            alwaysOnTop: false, noAutoHide: false, explicitExit: false,
            windowVisible: true, appForeground: false, popupOpen: false));

        // Always on Top is the case this feature exists for.
        Assert.IsFalse(WindowBehaviorPolicy.ShouldHideAfterDeactivate(
            alwaysOnTop: true, noAutoHide: false, explicitExit: false,
            windowVisible: true, appForeground: false, popupOpen: false));

        // Every other suppression keeps working alongside it.
        Assert.IsFalse(WindowBehaviorPolicy.ShouldHideAfterDeactivate(false, true, false, true, false, false), "--no-autohide");
        Assert.IsFalse(WindowBehaviorPolicy.ShouldHideAfterDeactivate(false, false, true, true, false, false), "quitting");
        Assert.IsFalse(WindowBehaviorPolicy.ShouldHideAfterDeactivate(false, false, false, false, false, false), "already hidden");
        Assert.IsFalse(WindowBehaviorPolicy.ShouldHideAfterDeactivate(false, false, false, true, true, false), "still foreground");
        Assert.IsFalse(WindowBehaviorPolicy.ShouldHideAfterDeactivate(false, false, false, true, false, true), "drop-down open");
    }

    [TestMethod]
    public void VendorHotkey_SummonsOnlyWhileTheSwitchIsOn()
    {
        Assert.IsTrue(WindowBehaviorPolicy.ShouldSummonOnVendorHotkey(specialKeyWake: true));
        Assert.IsFalse(WindowBehaviorPolicy.ShouldSummonOnVendorHotkey(specialKeyWake: false));
    }
}
