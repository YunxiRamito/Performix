using Predex.WinUI.Services.Update;

namespace Performix.Tests;

/// <summary>
/// The hand-off from the replacement script back into the restarted build: invoke-update.ps1
/// appends -RestartArguments, the app parses --updated-from. Both halves are covered here so the
/// switch cannot become decorative again.
/// </summary>
[TestClass]
public sealed class UpdateLaunchNoticeTests
{
    private const string Sha = "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    // ── Transmission: the install path actually passes the old version ────────────────

    [TestMethod]
    public void BuildScriptArguments_PassesTheOldVersionAsRestartArguments()
    {
        IReadOnlyList<string> args = UpdateInstaller.BuildScriptArguments(
            @"C:\app\scripts\invoke-update.ps1", @"C:\Temp\pkg.zip", @"C:\app", 4242, Sha, "0.1.0-alpha.1");

        int index = IndexOf(args, "-RestartArguments");
        Assert.IsGreaterThanOrEqualTo(0, index, "the script's -RestartArguments parameter must be supplied");
        Assert.AreEqual("--updated-from 0.1.0-alpha.1", args[index + 1]);
        // The mandatory parameters must still be there, in their documented order.
        Assert.AreEqual(@"C:\Temp\pkg.zip", args[IndexOf(args, "-Zip") + 1]);
        Assert.AreEqual(@"C:\app", args[IndexOf(args, "-Target") + 1]);
        Assert.AreEqual("4242", args[IndexOf(args, "-Pid") + 1]);
        Assert.AreEqual(Sha, args[IndexOf(args, "-Sha256") + 1]);
    }

    [TestMethod]
    public void BuildScriptArguments_WithoutAVersion_OmitsRestartArguments()
    {
        foreach (string? version in new[] { null, "", "   " })
        {
            IReadOnlyList<string> args = UpdateInstaller.BuildScriptArguments(
                @"C:\app\scripts\invoke-update.ps1", @"C:\Temp\pkg.zip", @"C:\app", 1, Sha, version);

            Assert.AreEqual(-1, IndexOf(args, "-RestartArguments"),
                "an unknown version must not produce an empty hand-off");
        }
    }

    [TestMethod]
    public void BuildRestartArguments_UsesTheDocumentedShape()
    {
        Assert.AreEqual("--updated-from 0.1.0-alpha.1", UpdateInstaller.BuildRestartArguments("0.1.0-alpha.1"));
        Assert.AreEqual("--updated-from 1.2.3", UpdateInstaller.BuildRestartArguments("  1.2.3  "));
        Assert.AreEqual(string.Empty, UpdateInstaller.BuildRestartArguments(null));
        Assert.AreEqual(string.Empty, UpdateInstaller.BuildRestartArguments(" "));
    }

    // ── Parsing: the new process recognises the switch ────────────────────────────────

    [TestMethod]
    public void ParseUpdatedFrom_AcceptsBothCommandLineForms()
    {
        Assert.AreEqual("0.1.0-alpha.1",
            UpdateLaunchNotice.ParseUpdatedFrom(new[] { @"C:\app\Performix.exe", "--updated-from", "0.1.0-alpha.1" }));
        Assert.AreEqual("0.1.0-alpha.1",
            UpdateLaunchNotice.ParseUpdatedFrom(new[] { @"C:\app\Performix.exe", "--updated-from=0.1.0-alpha.1" }));
        Assert.AreEqual("0.1.0-alpha.1",
            UpdateLaunchNotice.ParseUpdatedFrom(new[] { "--UPDATED-FROM", "0.1.0-alpha.1" }));
        Assert.IsTrue(UpdateLaunchNotice.IsPostUpdateLaunch(new[] { "--updated-from", "0.1.0-alpha.1" }));
    }

    [TestMethod]
    public void ParseUpdatedFrom_IgnoresAbsentOrUnreadableValues()
    {
        Assert.IsNull(UpdateLaunchNotice.ParseUpdatedFrom(null));
        Assert.IsNull(UpdateLaunchNotice.ParseUpdatedFrom(Array.Empty<string>()));
        Assert.IsNull(UpdateLaunchNotice.ParseUpdatedFrom(new[] { @"C:\app\Performix.exe" }));
        Assert.IsNull(UpdateLaunchNotice.ParseUpdatedFrom(new[] { "--updated-from", "not-a-version" }),
            "a garbage value must not produce an 'updated to' toast naming a version nobody shipped");
        Assert.IsNull(UpdateLaunchNotice.ParseUpdatedFrom(new[] { "--updated-from" }),
            "a switch with no value is not a hand-off");
        Assert.IsNull(UpdateLaunchNotice.ParseUpdatedFrom(new[] { "--updated-from", "--no-autohide" }),
            "the next switch must not be read as the version");
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return i;
        }
        return -1;
    }
}
