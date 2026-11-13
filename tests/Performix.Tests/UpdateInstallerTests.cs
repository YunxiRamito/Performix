using Predex.WinUI.Services.Update;
using PreySense.Tests;
using System;
using System.IO;

namespace Performix.Tests;

/// <summary>
/// Covers the parts of UpdateInstaller that decide *where* things are, without ever starting the
/// replacement script: a test run must not spawn powershell and touch a real installation.
/// </summary>
[TestClass]
public sealed class UpdateInstallerTests
{
    private const string Sha = "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static UpdateAsset Asset()
        => new("Performix-win-x64.zip", "https://github.com/x/y.zip", 3, Sha);

    private static UpdateStateMachine Machine()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        UpdateManifest manifest =
            UpdateManifestParser.Parse(TestManifests.Manifest("0.1.0-alpha.2", sha: Sha), "alpha").Manifest!;
        machine.BeginCheck();
        machine.ApplyCheck(new UpdateCheckResult(true, manifest, manifest.SelectAsset(),
            UpdateSourceCatalog.Official("alpha")[0], false, null,
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 1));
        return machine;
    }

    private static UpdateStateMachine ReadyToInstallMachine()
    {
        UpdateStateMachine machine = Machine();
        machine.BeginDownload();
        machine.BeginVerify();
        machine.MarkReadyToInstall();
        return machine;
    }

    private static UpdateStateMachine FailedMachine()
    {
        UpdateStateMachine machine = Machine();
        machine.BeginDownload();
        machine.BeginVerify();
        machine.Fail(UpdateFailureStage.Verify, "The SHA-256 does not match the manifest.", "detail");
        return machine;
    }

    [TestMethod]
    public void LogPath_IsUnderLocalAppData_PerformixLogs()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Performix", "logs", "update.log");

        Assert.AreEqual(expected, UpdateInstaller.LogPath);
    }

    [TestMethod]
    public void Start_WithMissingPackage_FailsWithoutStartingTheScript()
    {
        string missing = Path.Combine(Path.GetTempPath(), "performix-no-such-" + Guid.NewGuid().ToString("N") + ".zip");

        UpdateInstallStart start = UpdateInstaller.Start(missing, Asset(), ReadyToInstallMachine());

        Assert.IsFalse(start.Ok);
        // Two acceptable reasons: the script was not found (deployed layout differs) or the
        // package was gone. Either way nothing was executed and a reason + log path came back.
        Assert.IsFalse(string.IsNullOrWhiteSpace(start.Reason));
        Assert.AreEqual(UpdateInstaller.LogPath, start.LogPath);
    }

    /// <summary>
    /// The install entry has to check the state machine itself. Before this test a failed page
    /// could start a verified-looking package behind the failure card (no progress, no cancel),
    /// which replaced the program and exited the app without the user ever seeing it.
    /// </summary>
    [TestMethod]
    public void Start_FromAFailedState_IsRefusedWithoutStartingTheScript()
    {
        using var temp = new TempFolder();
        string package = Path.Combine(temp.Path, "Performix-win-x64.zip");
        File.WriteAllText(package, "zip");

        UpdateInstallStart start = UpdateInstaller.Start(package, Asset(), FailedMachine());

        Assert.IsFalse(start.Ok);
        StringAssert.Contains(start.Detail, "state=Failed");
        Assert.IsNull(start.ScriptPath, "the script must not even be resolved when the state refuses the install");
    }

    /// <summary>Downloaded but not yet verified is not installable either.</summary>
    [TestMethod]
    public void Start_FromTheAvailableState_IsRefused()
    {
        using var temp = new TempFolder();
        string package = Path.Combine(temp.Path, "Performix-win-x64.zip");
        File.WriteAllText(package, "zip");

        UpdateInstallStart start = UpdateInstaller.Start(package, Asset(), Machine());

        Assert.IsFalse(start.Ok);
        StringAssert.Contains(start.Detail, "state=Available");
        Assert.IsNull(start.ScriptPath);
    }

    [TestMethod]
    public void ResolveScript_PrefersTheScriptShippedNextToTheExe()
    {
        using var temp = new TempFolder();
        string app = Path.Combine(temp.Path, "Performix");
        string scripts = Path.Combine(app, "scripts");
        Directory.CreateDirectory(scripts);
        string expected = Path.Combine(scripts, "invoke-update.ps1");
        File.WriteAllText(expected, "# test");

        Assert.AreEqual(expected, UpdateInstaller.ResolveScript(app));
    }

    [TestMethod]
    public void ResolveScript_DeployedLayout_DoesNotWalkUpIntoAnAncestor()
    {
        // C:\Users\x\Applications\Performix is a normal, user-writable deployment directory.
        // An invoke-update.ps1 in its parent must never be picked up and executed.
        using var temp = new TempFolder();
        string ancestorScripts = Path.Combine(temp.Path, "scripts");
        Directory.CreateDirectory(ancestorScripts);
        File.WriteAllText(Path.Combine(ancestorScripts, "invoke-update.ps1"), "# planted");

        string deployed = Path.Combine(temp.Path, "Performix");
        Directory.CreateDirectory(deployed);

        Assert.IsNull(UpdateInstaller.ResolveScript(deployed),
            "a non-bin layout must not walk up looking for an attacker-writable script");
    }

    [TestMethod]
    public void ResolveScript_BuildOutputLayout_FindsTheRepositoryScript()
    {
        using var temp = new TempFolder();
        string repositoryScripts = Path.Combine(temp.Path, "scripts");
        Directory.CreateDirectory(repositoryScripts);
        string expected = Path.Combine(repositoryScripts, "invoke-update.ps1");
        File.WriteAllText(expected, "# repository script");

        string buildOutput = Path.Combine(temp.Path, "src", "Performix.App", "bin", "x64", "Debug", "net10.0-windows");
        Directory.CreateDirectory(buildOutput);

        Assert.AreEqual(expected, UpdateInstaller.ResolveScript(buildOutput),
            "a bin\\ layout is a development build and may use the repository script");
    }

    [TestMethod]
    public void ResolveScript_ReturnsAnExistingScript_WhenItDoes()
    {
        string? script = UpdateInstaller.ResolveScript();

        // null is legitimate (test output has no scripts\ folder above it); when a path does come
        // back it must be the real file, not a guess.
        if (script is not null)
        {
            Assert.IsTrue(File.Exists(script), $"ResolveScript returned a path that does not exist: {script}");
            Assert.AreEqual("invoke-update.ps1", Path.GetFileName(script));
        }
    }

    [TestMethod]
    public void PowershellPath_IsAbsoluteAndOutsideTheApplicationDirectory()
    {
        string path = UpdateInstaller.PowershellPath;

        Assert.IsTrue(Path.IsPathRooted(path), path);
        Assert.AreEqual("powershell.exe", Path.GetFileName(path), ignoreCase: true);
        Assert.IsTrue(path.Contains("System32", StringComparison.OrdinalIgnoreCase), path);
        Assert.IsTrue(path.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase), path);
        Assert.IsFalse(path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "powershell must never resolve through the application directory");
    }
}
