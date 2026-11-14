using Performix.Installer;

namespace Performix.Tests;

/// <summary>
/// Installer command-line parsing. The installer runs unattended in CI (<c>--offline … --silent
/// --no-launch</c>) and interactively by hand, so a mis-parsed line either installs the wrong
/// version or silently launches the app when it was told not to.
/// </summary>
[TestClass]
public class InstallerCommandLineTests
{
    [TestMethod]
    public void NoArguments_MeansInstallWithDefaults()
    {
        var result = InstallerCommandLine.Parse(Array.Empty<string>());

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(InstallerMode.Install, result.Command.Mode);
        Assert.IsNull(result.Command.VersionTag);
        Assert.IsNull(result.Command.OfflinePackage);
        Assert.IsNull(result.Command.InstallDirectory);
        Assert.IsFalse(result.Command.NoLaunch);
        Assert.IsFalse(result.Command.DesktopShortcut);
        Assert.IsFalse(result.Command.Silent);
    }

    [TestMethod]
    public void NullArguments_AlsoMeanDefaults()
    {
        var result = InstallerCommandLine.Parse(null);

        Assert.IsTrue(result.Ok);
        Assert.AreEqual(InstallerMode.Install, result.Command.Mode);
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("-?")]
    [DataRow("/?")]
    public void HelpSwitch_SelectsHelpMode(string argument)
    {
        var result = InstallerCommandLine.Parse(new[] { argument });

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(InstallerMode.Help, result.Command.Mode);
    }

    [TestMethod]
    public void UninstallSwitch_SelectsUninstallMode()
    {
        var result = InstallerCommandLine.Parse(new[] { "--uninstall" });

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(InstallerMode.Uninstall, result.Command.Mode);
    }

    [TestMethod]
    public void EveryDocumentedSwitch_ParsesInSpaceForm()
    {
        var result = InstallerCommandLine.Parse(new[]
        {
            "--version", "v0.1.0-alpha.2", "--offline", @"C:\tmp\pkg.zip",
            "--dir", @"D:\apps", "--no-launch", "--desktop-shortcut", "--silent",
        });

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(InstallerMode.Install, result.Command.Mode);
        Assert.AreEqual("v0.1.0-alpha.2", result.Command.VersionTag);
        Assert.AreEqual(@"C:\tmp\pkg.zip", result.Command.OfflinePackage);
        Assert.AreEqual(@"D:\apps", result.Command.InstallDirectory);
        Assert.IsTrue(result.Command.NoLaunch);
        Assert.IsTrue(result.Command.DesktopShortcut);
        Assert.IsTrue(result.Command.Silent);
    }

    [TestMethod]
    public void EveryDocumentedSwitch_ParsesInEqualsForm()
    {
        var result = InstallerCommandLine.Parse(new[]
        {
            "--version=v0.1.0", "--offline=C:\\pkg.zip", "--dir=D:\\apps",
            "--no-launch=false", "--desktop-shortcut=1", "--silent=0",
        });

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("v0.1.0", result.Command.VersionTag);
        Assert.AreEqual(@"C:\pkg.zip", result.Command.OfflinePackage);
        Assert.AreEqual(@"D:\apps", result.Command.InstallDirectory);
        Assert.IsFalse(result.Command.NoLaunch, "explicit false must not become true");
        Assert.IsTrue(result.Command.DesktopShortcut);
        Assert.IsFalse(result.Command.Silent);
    }

    /// <summary>
    /// "--version" as the last token, or immediately followed by another switch, has no value.
    /// Reading "--no-launch" as the version (the old UpdateOptions bug) would ask GitHub for a
    /// release tag called "--no-launch".
    /// </summary>
    [TestMethod]
    public void SwitchesWithoutValues_AreErrors_AndNeverSwallowTheNextSwitch()
    {
        CommandLineParseResult trailing = InstallerCommandLine.Parse(new[] { "--version" });
        Assert.IsFalse(trailing.Ok);
        StringAssert.Contains(trailing.Error, "--version");

        CommandLineParseResult followed = InstallerCommandLine.Parse(new[] { "--version", "--no-launch" });
        Assert.IsFalse(followed.Ok);
        StringAssert.Contains(followed.Error, "--version");

        CommandLineParseResult emptyValue = InstallerCommandLine.Parse(new[] { "--offline=" });
        Assert.IsFalse(emptyValue.Ok);

        CommandLineParseResult missingDir = InstallerCommandLine.Parse(new[] { "--dir" });
        Assert.IsFalse(missingDir.Ok);
    }

    [TestMethod]
    public void UnknownSwitch_IsRejected_InsteadOfBeingIgnored()
    {
        CommandLineParseResult result = InstallerCommandLine.Parse(new[] { "--no-luanch" });

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "--no-luanch");
        StringAssert.Contains(result.Error, "--help");
    }

    [TestMethod]
    public void NonBooleanSwitchValue_IsRejected()
    {
        CommandLineParseResult result = InstallerCommandLine.Parse(new[] { "--silent=maybe" });

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "maybe");
    }

    [TestMethod]
    public void Uninstall_RejectsInstallOnlyArguments()
    {
        Assert.IsFalse(InstallerCommandLine.Parse(new[] { "--uninstall", "--version", "v1.0.0" }).Ok);
        Assert.IsFalse(InstallerCommandLine.Parse(new[] { "--uninstall", "--offline", "p.zip" }).Ok);

        CommandLineParseResult ok = InstallerCommandLine.Parse(new[] { "--uninstall", "--dir", @"D:\x", "--silent" });
        Assert.IsTrue(ok.Ok, ok.Error);
        Assert.AreEqual(InstallerMode.Uninstall, ok.Command.Mode);
        Assert.AreEqual(@"D:\x", ok.Command.InstallDirectory);
    }

    [TestMethod]
    public void LeadingProgramName_IsIgnored()
    {
        var result = InstallerCommandLine.Parse(new[] { "PerformixSetup.exe", "--silent" });

        Assert.IsTrue(result.Ok, result.Error);
        Assert.IsTrue(result.Command.Silent);
    }

    [TestMethod]
    public void OptionValue_ReturnsNullForMissingOrSwitchValues()
    {
        Assert.IsNull(InstallerCommandLine.OptionValue(new[] { "--version" }, "--version"));
        Assert.IsNull(InstallerCommandLine.OptionValue(new[] { "--version", "--dir", "x" }, "--version"));
        Assert.AreEqual("v1", InstallerCommandLine.OptionValue(new[] { "--version", "v1" }, "--version"));
        Assert.AreEqual("v2", InstallerCommandLine.OptionValue(new[] { "--version=v2" }, "--version"));
        Assert.IsNull(InstallerCommandLine.OptionValue(null, "--version"));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Runtime detection
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class RuntimeProbeTests
{
    private static RuntimeLocation Location(RuntimeArchitecture architecture, string shared, params string[] versions)
        => new(architecture, @"C:\Program Files\dotnet", shared, versions);

    [TestMethod]
    public void BothX64Runtimes_AreSatisfied()
    {
        var probe = RuntimeProbe.Evaluate(new[]
        {
            Location(RuntimeArchitecture.X64, RuntimeProbe.DesktopSharedName, "10.0.15"),
            Location(RuntimeArchitecture.X64, RuntimeProbe.NetCoreSharedName, "10.0.15"),
        });

        Assert.IsTrue(probe.IsSatisfied);
        Assert.AreEqual("10.0.15", probe.DesktopVersion);
        Assert.AreEqual("10.0.15", probe.NetCoreVersion);
    }

    [TestMethod]
    public void MissingNetCore_IsNotSatisfied()
    {
        var probe = RuntimeProbe.Evaluate(new[]
        {
            Location(RuntimeArchitecture.X64, RuntimeProbe.DesktopSharedName, "10.0.15"),
        });

        Assert.IsFalse(probe.IsSatisfied);
        Assert.IsTrue(probe.DesktopRuntimeFound);
        Assert.IsFalse(probe.NetCoreRuntimeFound);
    }

    /// <summary>An x86 runtime cannot host a win-x64 application, so it does not count.</summary>
    [TestMethod]
    public void X86OnlyInstall_IsNotSatisfied()
    {
        var probe = RuntimeProbe.Evaluate(new[]
        {
            Location(RuntimeArchitecture.X86, RuntimeProbe.DesktopSharedName, "10.0.15"),
            Location(RuntimeArchitecture.X86, RuntimeProbe.NetCoreSharedName, "10.0.15"),
        });

        Assert.IsFalse(probe.IsSatisfied);
        Assert.IsNull(probe.DesktopVersion);
    }

    [TestMethod]
    public void X64WinsOverX86_AndNewestX64VersionIsReported()
    {
        var probe = RuntimeProbe.Evaluate(new[]
        {
            Location(RuntimeArchitecture.X86, RuntimeProbe.DesktopSharedName, "10.0.20"),
            Location(RuntimeArchitecture.X64, RuntimeProbe.DesktopSharedName, "10.0.9", "10.0.15", "9.0.5"),
            Location(RuntimeArchitecture.X64, RuntimeProbe.NetCoreSharedName, "10.0.15"),
        });

        Assert.IsTrue(probe.IsSatisfied);
        Assert.AreEqual("10.0.15", probe.DesktopVersion, "10.0.15 > 10.0.9, and 9.0.5 is not 10.x");
    }

    [DataTestMethod]
    [DataRow("10.0.15", true)]
    [DataRow("10.0.0", true)]
    [DataRow("10.1.2", true)]
    [DataRow("10.0.0-preview.1", false)] // a preview framework will not roll forward for a GA app
    [DataRow("9.0.11", false)]
    [DataRow("11.0.0", false)]
    [DataRow("10", false)]
    [DataRow("not-a-version", false)]
    [DataRow("", false)]
    public void IsUsableVersion_RequiresGax10(string directoryName, bool expected)
        => Assert.AreEqual(expected, RuntimeProbe.IsUsableVersion(directoryName), directoryName);

    [TestMethod]
    public void ScanRoots_ListsBothSharedFoldersAndDeduplicatesRoots()
    {
        var roots = new[]
        {
            (Root: @"C:\Program Files\dotnet", Architecture: RuntimeArchitecture.X64),
            (Root: @"C:\Program Files\dotnet\", Architecture: RuntimeArchitecture.X64), // same root again
        };

        IReadOnlyList<RuntimeLocation> locations = RuntimeProbe.ScanRoots(roots, path =>
            path.EndsWith(RuntimeProbe.DesktopSharedName, StringComparison.Ordinal)
                ? new[] { "10.0.15" }
                : Array.Empty<string>());

        Assert.AreEqual(1, locations.Count, "the duplicate root must not produce a second location");
        Assert.AreEqual(RuntimeProbe.DesktopSharedName, locations[0].SharedName);
        Assert.AreEqual(RuntimeArchitecture.X64, locations[0].Architecture);
    }

    [TestMethod]
    public void ScanRoots_IgnoresUnreadableDirectories()
    {
        IReadOnlyList<RuntimeLocation> locations = RuntimeProbe.ScanRoots(
            new[] { (Root: @"C:\nope", Architecture: RuntimeArchitecture.X64) },
            _ => throw new UnauthorizedAccessException("no access"));

        Assert.AreEqual(0, locations.Count);
    }

    /// <summary>
    /// The bug this pins: the scanner used to look for <c>%ProgramFiles%\shared\…</c> — the
    /// <c>dotnet</c> segment was missing — so a machine with .NET 10 installed reported "not
    /// found" and downloaded 57 MB it did not need. Both helpers are asserted directly, because
    /// the wrong path silently degrades into "no runtime anywhere" instead of an error.
    /// </summary>
    [TestMethod]
    public void DotnetRootUnder_AppendsTheDotnetSegment()
    {
        Assert.AreEqual(
            Path.Combine(@"C:\Program Files", "dotnet"),
            RuntimeProbe.DotnetRootUnder(@"C:\Program Files"));
        Assert.AreEqual(@"C:\Program Files (x86)\dotnet", RuntimeProbe.DotnetRootUnder(@"C:\Program Files (x86)"));
        Assert.AreEqual(string.Empty, RuntimeProbe.DotnetRootUnder(null));
        Assert.AreEqual(string.Empty, RuntimeProbe.DotnetRootUnder("   "));
    }

    [TestMethod]
    public void SharedPath_LivesUnderTheDotnetRootInShared()
    {
        string shared = RuntimeProbe.SharedPath(
            Path.Combine(@"C:\Program Files", "dotnet"), RuntimeProbe.DesktopSharedName);

        Assert.AreEqual(
            @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App",
            shared);
        StringAssert.Contains(shared, @"\dotnet\shared\", "the dotnet segment is the part that gets forgotten");
    }

    [TestMethod]
    public void ScanRoots_LooksInsideTheSharedFolderOfTheGivenRoot()
    {
        var probed = new List<string>();

        RuntimeProbe.ScanRoots(
            new[] { (Root: @"C:\Program Files\dotnet", Architecture: RuntimeArchitecture.X64) },
            path =>
            {
                probed.Add(path);
                return Array.Empty<string>();
            });

        Assert.AreEqual(2, probed.Count);
        Assert.AreEqual(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App", probed[0]);
        Assert.AreEqual(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App", probed[1]);
    }

    /// <summary>
    /// End-to-end over an injected lister: a root that holds 10.0.15 in both shared folders is
    /// satisfied, and the version string survives the trip.
    /// </summary>
    [TestMethod]
    public void ScanRoots_PlusEvaluate_FindsARealisticLayout()
    {
        (string Root, RuntimeArchitecture Architecture) root = (@"C:\Program Files\dotnet", RuntimeArchitecture.X64);

        RuntimeProbeResult result = RuntimeProbe.Evaluate(RuntimeProbe.ScanRoots(
            new[] { root },
            path => path.EndsWith(RuntimeProbe.DesktopSharedName, StringComparison.Ordinal)
                ? new[] { "8.0.11", "10.0.15" }
                : new[] { "10.0.15" }));

        Assert.IsTrue(result.IsSatisfied);
        Assert.AreEqual("10.0.15", result.DesktopVersion);
        Assert.AreEqual("10.0.15", result.NetCoreVersion);
    }

    [TestMethod]
    public void Evaluate_HandlesNoLocations()
    {
        var probe = RuntimeProbe.Evaluate(Array.Empty<RuntimeLocation>());

        Assert.IsFalse(probe.IsSatisfied);
        Assert.IsNull(probe.DesktopVersion);
        Assert.IsFalse(probe.SawInsufficient10x, "an empty scan is not an 'insufficient' scan");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Version strings
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class VersionTextTests
{
    [DataTestMethod]
    [DataRow("v0.1.0-alpha.2", "0.1.0-alpha.2")]
    [DataRow("V1.2.3", "1.2.3")]
    [DataRow(" 10.0.15 ", "10.0.15")]
    [DataRow("varying", "varying")] // "v" followed by a letter is part of the name
    [DataRow(null, "")]
    public void NormalizeTag_StripsOnlyTheVersionPrefix(string? input, string expected)
        => Assert.AreEqual(expected, VersionText.NormalizeTag(input));

    [TestMethod]
    public void TryParse_SplitsPrerelease()
    {
        Assert.IsTrue(VersionText.TryParse("10.0.15", out Version version, out string? pre));
        Assert.AreEqual(new Version(10, 0, 15), version);
        Assert.IsNull(pre);

        Assert.IsTrue(VersionText.TryParse("v10.0.0-preview.1", out Version preview, out string? previewPre));
        Assert.AreEqual(new Version(10, 0, 0), preview);
        Assert.AreEqual("preview.1", previewPre);
    }

    [DataTestMethod]
    [DataRow("10.0.15", "10.0.15", 0)]
    [DataRow("10.0.9", "10.0.15", -1)]
    [DataRow("10.0.15", "10.0.9", 1)]
    [DataRow("v10.0.15", "10.0.15", 0)]
    [DataRow("10.0.15", "10.0.15-preview.1", 1)]  // stable beats its own prerelease
    [DataRow("10.0.15-preview.1", "10.0.15", -1)]
    [DataRow("not-a-version", "10.0.15", -1)]      // junk sorts below a real version
    [DataRow("10.0.15", "not-a-version", 1)]
    [DataRow("v0.1.0-alpha.9", "v0.1.0-alpha.10", -1)] // alpha.10 is newer than alpha.9
    [DataRow("v0.1.0-alpha.10", "v0.1.0-alpha.9", 1)]
    [DataRow("v0.1.0-beta.1", "v0.1.0-alpha.2", 1)]    // beta label sorts after alpha
    [DataRow("v0.1.0-alpha", "v0.1.0-alpha.1", -1)]    // a shorter label is the older one
    [DataRow("v0.2.0-alpha.1", "v0.1.9", 1)]           // numbers outrank the label
    public void Compare_OrdersReleases(string left, string right, int expected)
        => Assert.AreEqual(expected, Math.Sign(VersionText.Compare(left, right)), $"{left} vs {right}");

    [DataTestMethod]
    [DataRow("alpha.9", "alpha.10", -1)]
    [DataRow("alpha.10", "alpha.9", 1)]
    [DataRow("alpha.2", "alpha.2", 0)]
    [DataRow("beta.1", "alpha.10", 1)]
    [DataRow(null, "alpha.1", -1)]
    [DataRow("alpha.1", null, 1)]
    [DataRow(null, null, 0)]
    public void ComparePrerelease_ComparesSegmentsNumerically(string? left, string? right, int expected)
        => Assert.AreEqual(expected, Math.Sign(VersionText.ComparePrerelease(left, right)), $"{left} vs {right}");

    [TestMethod]
    public void AreSame_IgnoresTheTagPrefix()
    {
        Assert.IsTrue(VersionText.AreSame("v1.2.3", "1.2.3"));
        Assert.IsFalse(VersionText.AreSame("1.2.3", "1.2.4"));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Install paths / pruning
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class InstallerPathsTests
{
    [DataTestMethod]
    [DataRow("v0.1.0-alpha.2", "0.1.0-alpha.2")]
    [DataRow("1.0.0", "1.0.0")]
    [DataRow(null, "local")]
    [DataRow("", "local")]
    public void SanitizeVersion_ProducesADirectoryName(string? tag, string expected)
        => Assert.AreEqual(expected, InstallerPaths.SanitizeVersion(tag));

    [TestMethod]
    public void SanitizeVersion_RemovesPathSeparators()
    {
        string name = InstallerPaths.SanitizeVersion("v1.0/../evil");

        Assert.IsFalse(name.Contains('/'));
        Assert.IsFalse(name.Contains('\\'));
        Assert.IsTrue(name.StartsWith("1.0", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VersionDirectory_IsBelowTheAppRoot()
    {
        string path = InstallerPaths.VersionDirectory(@"C:\apps\Performix", "v0.1.0");

        Assert.AreEqual(Path.Combine(@"C:\apps\Performix", "0.1.0"), path);
    }

    [TestMethod]
    public void PreservedConfigPath_SitsAboveTheAppDirectory()
    {
        string preserved = InstallerPaths.PreservedConfigPathFor(@"C:\Users\x\AppData\Local\Performix\app");

        Assert.AreEqual(@"C:\Users\x\AppData\Local\Performix\preysense.json", preserved);
    }

    [TestMethod]
    public void SelectVersionsToPrune_KeepsTheTwoNewest()
    {
        var now = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var folders = new[]
        {
            new VersionFolder(@"C:\app\0.1.0", "0.1.0", now.AddDays(-5)),
            new VersionFolder(@"C:\app\0.2.0", "0.2.0", now.AddDays(-2)),
            new VersionFolder(@"C:\app\0.3.0", "0.3.0", now),
        };

        IReadOnlyList<VersionFolder> prune = InstallerPaths.SelectVersionsToPrune(folders, keep: 2);

        Assert.AreEqual(1, prune.Count);
        Assert.AreEqual("0.1.0", prune[0].Name);
    }

    [TestMethod]
    public void SelectVersionsToPrune_NeverReturnsTheFreshlyInstalledDirectory()
    {
        var now = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var folders = new[]
        {
            new VersionFolder(@"C:\app\new", "new", now.AddDays(-9)),   // odd timestamp, still protected
            new VersionFolder(@"C:\app\a", "a", now),
            new VersionFolder(@"C:\app\b", "b", now.AddDays(-1)),
        };

        IReadOnlyList<VersionFolder> prune =
            InstallerPaths.SelectVersionsToPrune(folders, keep: 1, protectedPath: @"C:\app\new");

        Assert.IsFalse(prune.Any(folder => folder.Name == "new"), "the just-installed version must survive");
        Assert.AreEqual(1, prune.Count);
        Assert.AreEqual("b", prune[0].Name);
    }

    [TestMethod]
    public void SelectVersionsToPrune_HandlesFewerFoldersThanTheKeepCount()
    {
        var folders = new[] { new VersionFolder(@"C:\app\a", "a", DateTime.UtcNow) };

        Assert.AreEqual(0, InstallerPaths.SelectVersionsToPrune(folders, keep: 2).Count);
        Assert.AreEqual(0, InstallerPaths.SelectVersionsToPrune(Array.Empty<VersionFolder>()).Count);
        Assert.AreEqual(0, InstallerPaths.SelectVersionsToPrune(null!).Count);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Segment arithmetic
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class SegmentMathTests
{
    [TestMethod]
    public void Plan_CoversEveryByteExactlyOnce()
    {
        DownloadSegment[] segments = SegmentMath.Plan(1000, 8);

        Assert.AreEqual(8, segments.Length);
        Assert.AreEqual(1000, SegmentMath.TotalBytes(segments));
        Assert.AreEqual(0, segments[0].Start);
        Assert.AreEqual(999, segments[^1].End);

        for (int i = 1; i < segments.Length; i++)
        {
            Assert.AreEqual(segments[i - 1].End + 1, segments[i].Start, "segments must be contiguous");
        }
    }

    [TestMethod]
    public void Plan_GivesTheRemainderToTheLastSegment()
    {
        DownloadSegment[] segments = SegmentMath.Plan(1001, 8);

        Assert.AreEqual(8, segments.Length);
        Assert.AreEqual(1001, SegmentMath.TotalBytes(segments));
        Assert.AreEqual(125, segments[0].Length);
        Assert.AreEqual(126, segments[^1].Length, "the tail takes the remainder");
        Assert.AreEqual(1000, segments[^1].End);
    }

    [TestMethod]
    public void Plan_ZeroLength_HasNoSegments()
    {
        Assert.AreEqual(0, SegmentMath.Plan(0, 8).Length);
        Assert.AreEqual(0, SegmentMath.Plan(0).Length);
        Assert.AreEqual(0, SegmentMath.Plan(-42, 8).Length, "a negative length is not a range");
    }

    [TestMethod]
    public void Plan_SingleSegmentCoversEverything()
    {
        DownloadSegment[] one = SegmentMath.Plan(1000, 1);

        Assert.AreEqual(1, one.Length);
        Assert.AreEqual(0, one[0].Start);
        Assert.AreEqual(999, one[0].End);

        // A non-positive count still yields one usable segment rather than none.
        Assert.AreEqual(1, SegmentMath.Plan(1000, 0).Length);
        Assert.AreEqual(1, SegmentMath.Plan(1000, -3).Length);
    }

    [TestMethod]
    public void Plan_NeverCreatesMoreSegmentsThanBytes()
    {
        DownloadSegment[] segments = SegmentMath.Plan(3, 8);

        Assert.AreEqual(3, segments.Length);
        Assert.AreEqual(3, SegmentMath.TotalBytes(segments));
        Assert.AreEqual(1, segments[0].Length);
        Assert.AreEqual(2, segments[^1].Start);
        Assert.AreEqual(2, segments[^1].End);
    }

    [TestMethod]
    public void Plan_DefaultCountIsEight()
        => Assert.AreEqual(SegmentMath.DefaultSegmentCount, SegmentMath.Plan(10_000_000).Length);

    [TestMethod]
    public void Plan_OneByteFile_IsASingleRange()
    {
        DownloadSegment[] segments = SegmentMath.Plan(1, 8);

        Assert.AreEqual(1, segments.Length);
        Assert.AreEqual(0, segments[0].Start);
        Assert.AreEqual(0, segments[0].End);
        Assert.AreEqual(1, segments[0].Length);
    }

    [TestMethod]
    public void ResumedLength_ClampsToTheSegment()
    {
        var segment = new DownloadSegment(0, 100, 199); // 100 bytes

        Assert.AreEqual(0, SegmentMath.ResumedLength(segment, 0));
        Assert.AreEqual(0, SegmentMath.ResumedLength(segment, -5), "a negative part length is not progress");
        Assert.AreEqual(40, SegmentMath.ResumedLength(segment, 40));
        Assert.AreEqual(100, SegmentMath.ResumedLength(segment, 100));
        Assert.AreEqual(100, SegmentMath.ResumedLength(segment, 500),
            "a stale, longer part file must not push the offset past the end");
    }

    [TestMethod]
    public void IsSegmentComplete_OnlyWhenTheWholeRangeIsOnDisk()
    {
        var segment = new DownloadSegment(2, 100, 199);

        Assert.IsFalse(SegmentMath.IsSegmentComplete(segment, 99));
        Assert.IsTrue(SegmentMath.IsSegmentComplete(segment, 100));
        Assert.IsTrue(SegmentMath.IsSegmentComplete(segment, 101));
    }

    [TestMethod]
    public void RangeHeader_ResumesFromThePartLength()
    {
        var segment = new DownloadSegment(0, 500, 749);

        Assert.AreEqual("bytes=500-749", SegmentMath.RangeHeader(segment, 0));
        Assert.AreEqual("bytes=600-749", SegmentMath.RangeHeader(segment, 100));
        Assert.AreEqual("bytes=749-749", SegmentMath.RangeHeader(segment, 9999),
            "clamped to the last byte: a stale part file must never produce an inverted range");
    }

    [TestMethod]
    public void PartPath_IsUniquePerSegment()
    {
        Assert.AreEqual(@"C:\x\a.exe.part.0", SegmentMath.PartPath(@"C:\x\a.exe", 0));
        Assert.AreEqual(@"C:\x\a.exe.part.7", SegmentMath.PartPath(@"C:\x\a.exe", 7));
    }

    [DataTestMethod]
    [DataRow(true, 1000L, true)]
    [DataRow(false, 1000L, false)]
    [DataRow(true, null, false)]
    [DataRow(true, 0L, false)]
    public void IsRangeSupported_NeedsBothTheHeaderAndALength(bool advertised, long? length, bool expected)
        => Assert.AreEqual(expected, SegmentMath.IsRangeSupported(advertised, length));

    [DataTestMethod]
    [DataRow("bytes 0-0/2048", 2048L)]
    [DataRow("bytes 0-0/1", 1L)]
    public void TryParseContentRangeTotal_ReadsTheTotal(string header, long expected)
    {
        Assert.IsTrue(SegmentMath.TryParseContentRangeTotal(header, out long total));
        Assert.AreEqual(expected, total);
    }

    [DataTestMethod]
    [DataRow("bytes 0-0/*")]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("nonsense")]
    public void TryParseContentRangeTotal_RejectsUnknownOrMalformed(string? header)
        => Assert.IsFalse(SegmentMath.TryParseContentRangeTotal(header, out _));
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Checksums
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class ChecksumsTests
{
    private const string Sha512Hex =
        "f838960962172f1a769c49fa600e6f60f56f8d08a28e3cb878890ffc2e3a7a2e1b65697588e23efa767b8b8e8523ec01f55e875120507dde16d9a0869102dab3";

    private const string Sha256Hex =
        "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    [TestMethod]
    public void TryParse_128Hex_IsSha512()
    {
        Assert.IsTrue(Checksums.TryParse(Sha512Hex, out ExpectedHash? hash));
        Assert.AreEqual(HashKind.Sha512, hash!.Kind);
        Assert.AreEqual(64, hash.Bytes.Length);
    }

    [TestMethod]
    public void TryParse_64Hex_IsSha256()
    {
        Assert.IsTrue(Checksums.TryParse(Sha256Hex, out ExpectedHash? hash));
        Assert.AreEqual(HashKind.Sha256, hash!.Kind);
        Assert.AreEqual(32, hash.Bytes.Length);
    }

    [TestMethod]
    public void TryParse_GitHubDigestForm_IsSha256()
    {
        Assert.IsTrue(Checksums.TryParse("sha256:" + Sha256Hex, out ExpectedHash? hash));
        Assert.AreEqual(HashKind.Sha256, hash!.Kind);
    }

    [TestMethod]
    public void TryParse_Base64_IsAccepted()
    {
        byte[] digest = new byte[64];
        for (int i = 0; i < digest.Length; i++) digest[i] = (byte)i;

        Assert.IsTrue(Checksums.TryParse(Convert.ToBase64String(digest), out ExpectedHash? hash));
        Assert.AreEqual(HashKind.Sha512, hash!.Kind);
        CollectionAssert.AreEqual(digest, hash.Bytes);
    }

    [TestMethod]
    public void TryParse_RejectsJunk()
    {
        Assert.IsFalse(Checksums.TryParse(null, out _));
        Assert.IsFalse(Checksums.TryParse("", out _));
        Assert.IsFalse(Checksums.TryParse("abc", out _));
        Assert.IsFalse(Checksums.TryParse(new string('z', 128), out _));
    }

    [TestMethod]
    public void Matches_ComparesTheDigestAgainstTheFileContent()
    {
        byte[] payload = { 1, 2, 3, 4, 5 };
        string hex = Checksums.ToHex(Checksums.HashBytes(payload, HashKind.Sha512));

        Assert.IsTrue(Checksums.TryParse(hex, out ExpectedHash? expected));
        Assert.IsTrue(Checksums.Matches(Checksums.HashBytes(payload, HashKind.Sha512), expected));

        payload[0] = 99;
        Assert.IsFalse(Checksums.Matches(Checksums.HashBytes(payload, HashKind.Sha512), expected));
    }

    [TestMethod]
    public void Matches_HandlesLengthMismatchAndNull()
    {
        Assert.IsFalse(Checksums.Matches(new byte[4], new ExpectedHash(HashKind.Sha256, new byte[32])));
        Assert.IsFalse(Checksums.Matches(new byte[32], null));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// releases.json parsing
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class RuntimeReleaseMetadataTests
{
    private const string DesktopHash =
        "f838960962172f1a769c49fa600e6f60f56f8d08a28e3cb878890ffc2e3a7a2e1b65697588e23efa767b8b8e8523ec01f55e875120507dde16d9a0869102dab3";

    /// <summary>
    /// The real document shape (verified against builds.dotnet.microsoft.com 10.0/releases.json):
    /// the desktop bootstrapper sits under the release's <c>windowsdesktop</c> section, and its
    /// file name carries no version — the version is in the URL.
    /// </summary>
    private static string RealShape(string latestRuntime = "10.0.15", string releaseVersion = "10.0.15")
        => $$"""
        {
          "channel-version": "10.0",
          "latest-release": "{{latestRuntime}}",
          "latest-runtime": "{{latestRuntime}}",
          "releases": [
            {
              "release-date": "2026-11-11",
              "release-version": "{{releaseVersion}}",
              "runtime": {
                "version": "{{releaseVersion}}",
                "files": [
                  { "name": "dotnet-runtime-{{releaseVersion}}-win-x64.zip", "rid": "win-x64", "url": "https://example.test/runtime.zip", "hash": "aaaa" }
                ]
              },
              "windowsdesktop": {
                "version": "{{releaseVersion}}",
                "files": [
                  { "name": "windowsdesktop-runtime-win-arm64.exe", "rid": "win-arm64", "url": "https://example.test/arm64.exe", "hash": "bbbb" },
                  { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.example.test/{{releaseVersion}}/windowsdesktop-runtime-{{releaseVersion}}-win-x64.exe", "hash": "{{DesktopHash}}" },
                  { "name": "windowsdesktop-runtime-win-x64.zip", "rid": "win-x64", "url": "https://example.test/x64.zip", "hash": "cccc" }
                ]
              }
            }
          ]
        }
        """;

    [TestMethod]
    public void Parse_PicksTheWindowsX64Exe()
    {
        RuntimeMetadataResult result = RuntimeReleaseMetadata.Parse(RealShape());

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("10.0.15", result.Package!.Version);
        Assert.AreEqual("windowsdesktop-runtime-win-x64.exe", result.Package.FileName);
        Assert.AreEqual("https://builds.example.test/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe",
            result.Package.Url);
        Assert.AreEqual(DesktopHash, result.Package.Hash);
        Assert.AreEqual(HashKind.Sha512, result.Package.ExpectedHash!.Kind);
        Assert.AreEqual("10.0", result.ChannelVersion);
    }

    [TestMethod]
    public void Parse_AcceptsTheVersionedFileNameInLegacyFeeds()
    {
        const string legacy = """
        {
          "channel-version": "10.0",
          "latest-runtime": "10.0.7",
          "releases": [
            {
              "release-version": "10.0.7",
              "runtime": {
                "version": "10.0.7",
                "files": [
                  { "name": "windowsdesktop-runtime-10.0.7-win-x64.exe", "rid": "win-x64", "url": "https://example.test/10.0.7.exe", "hash": "dddd" }
                ]
              }
            }
          ]
        }
        """;

        RuntimeMetadataResult result = RuntimeReleaseMetadata.Parse(legacy);

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("windowsdesktop-runtime-10.0.7-win-x64.exe", result.Package!.FileName);
        Assert.AreEqual("10.0.7", result.Package.Version);
    }

    [TestMethod]
    public void Parse_SelectsTheReleaseNamedByLatestRuntime()
    {
        const string twoReleases = """
        {
          "channel-version": "10.0",
          "latest-runtime": "10.0.9",
          "releases": [
            { "release-version": "10.0.15",
              "windowsdesktop": { "version": "10.0.15", "files": [
                { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://example.test/newer.exe", "hash": "eeee" } ] } },
            { "release-version": "10.0.9",
              "windowsdesktop": { "version": "10.0.9", "files": [
                { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://example.test/latest-runtime.exe", "hash": "ffff" } ] } }
          ]
        }
        """;

        RuntimeMetadataResult result = RuntimeReleaseMetadata.Parse(twoReleases);

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("https://example.test/latest-runtime.exe", result.Package!.Url,
            "latest-runtime wins over a merely newer release-version");
    }

    [TestMethod]
    public void Parse_FallsBackToTheNewestReleaseWhenLatestRuntimeIsMissing()
    {
        const string noLatest = """
        {
          "releases": [
            { "release-version": "10.0.2",
              "windowsdesktop": { "version": "10.0.2", "files": [
                { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://example.test/old.exe" } ] } },
            { "release-version": "10.0.10",
              "windowsdesktop": { "version": "10.0.10", "files": [
                { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://example.test/new.exe" } ] } }
          ]
        }
        """;

        RuntimeMetadataResult result = RuntimeReleaseMetadata.Parse(noLatest);

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("https://example.test/new.exe", result.Package!.Url, "10.0.10 > 10.0.2 numerically");
        Assert.IsNull(result.Package.Hash, "a feed without a hash has to be reported as such");
    }

    [TestMethod]
    public void Parse_ReadsThePublishedSizeWhenThereIsOne()
    {
        const string withSize = """
        {
          "latest-runtime": "10.0.15",
          "releases": [
            { "release-version": "10.0.15",
              "windowsdesktop": { "version": "10.0.15", "files": [
                { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://example.test/a.exe", "size": 57000000 } ] } }
          ]
        }
        """;

        RuntimeMetadataResult result = RuntimeReleaseMetadata.Parse(withSize);

        Assert.AreEqual(57_000_000L, result.Package!.Size);
    }

    [TestMethod]
    public void Parse_ReportsErrorsInsteadOfThrowing()
    {
        Assert.IsNotNull(RuntimeReleaseMetadata.Parse(null).Error);
        Assert.IsNotNull(RuntimeReleaseMetadata.Parse("{ not json").Error);

        RuntimeMetadataResult noFiles = RuntimeReleaseMetadata.Parse("""
        { "latest-runtime": "10.0.15", "releases": [ { "release-version": "10.0.15" } ] }
        """);
        Assert.IsFalse(noFiles.Ok);
        StringAssert.Contains(noFiles.Error, "windowsdesktop-runtime");

        RuntimeMetadataResult noReleases = RuntimeReleaseMetadata.Parse("""{ "latest-runtime": "10.0.15" }""");
        Assert.IsFalse(noReleases.Ok);
        StringAssert.Contains(noReleases.Error, "releases");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// GitHub release contract
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class GitHubReleaseTests
{
    private const string Sha256 = "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    /// <summary>
    /// A GitHub release response. <paramref name="digest"/> is the asset's digest field; the comma
    /// that separates it from "size" lives here so a response without a digest stays valid JSON
    /// (a stray trailing comma made the parser reject the fixture instead of the behaviour).
    /// </summary>
    private static string ReleaseJson(string? digest = "sha256:" + Sha256)
    {
        string digestField = digest is null ? string.Empty : ", \"digest\": \"" + digest + "\"";

        return $$"""
        {
          "tag_name": "v0.1.0-alpha.2",
          "prerelease": true,
          "body": "notes",
          "assets": [
            { "name": "Performix-win-x64.zip", "browser_download_url": "https://github.com/YunxiRamito/Performix/releases/download/v0.1.0-alpha.2/Performix-win-x64.zip", "size": 4096{{digestField}} },
            { "name": "Performix-win-x64.zip.sha256", "browser_download_url": "https://github.com/YunxiRamito/Performix/releases/download/v0.1.0-alpha.2/Performix-win-x64.zip.sha256" },
            { "name": "update.json", "browser_download_url": "https://example.test/update.json" }
          ]
        }
        """;
    }

    [TestMethod]
    public void Parse_ReadsTagAndAssets()
    {
        GitHubReleaseParseResult result = GitHubRelease.Parse(ReleaseJson());

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("v0.1.0-alpha.2", result.Release!.Tag);
        Assert.AreEqual(3, result.Release.Assets.Count);
        Assert.AreEqual(4096L, result.Release.Assets[0].Size);
        Assert.IsTrue(result.Release.Prerelease);
    }

    [TestMethod]
    public void Parse_SelectsTheWinX64Package_NotTheManifestOrChecksum()
    {
        GitHubReleaseInfo release = GitHubRelease.Parse(ReleaseJson()).Release!;
        GitHubAsset? asset = release.Package;

        Assert.IsNotNull(asset);
        Assert.AreEqual(GitHubRelease.PackageAssetName, asset.Name);
    }

    [TestMethod]
    public void Parse_ReportsRateLimitMessagesAsErrors()
    {
        GitHubReleaseParseResult result = GitHubRelease.Parse("""
        { "message": "API rate limit exceeded for 1.2.3.4.", "documentation_url": "https://docs.github.com" }
        """);

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "rate limit");
    }

    [TestMethod]
    public void Parse_RejectsEmptyAndMalformedJson()
    {
        Assert.IsFalse(GitHubRelease.Parse(null).Ok);
        Assert.IsFalse(GitHubRelease.Parse("not json").Ok);
        Assert.IsFalse(GitHubRelease.Parse("[]").Ok);
        Assert.IsFalse(GitHubRelease.Parse("""{ "assets": [] }""").Ok, "no tag_name means no version");
    }

    [TestMethod]
    public void DigestSha256_ReadsTheAssetDigest()
    {
        GitHubReleaseInfo release = GitHubRelease.Parse(ReleaseJson()).Release!;

        Assert.AreEqual(Sha256, GitHubRelease.DigestSha256(release.Package));
        Assert.IsNull(GitHubRelease.DigestSha256(new GitHubAsset("a.zip", "https://x", 1, "md5:abc")));
        Assert.IsNull(GitHubRelease.DigestSha256(null));
    }

    [TestMethod]
    public void SelectChecksumAsset_FindsTheSiblingSha256()
    {
        GitHubReleaseInfo release = GitHubRelease.Parse(ReleaseJson()).Release!;

        GitHubAsset? checksum = GitHubRelease.SelectChecksumAsset(release.Assets);

        Assert.IsNotNull(checksum);
        Assert.AreEqual("Performix-win-x64.zip.sha256", checksum.Name);
        Assert.IsNull(GitHubRelease.SelectChecksumAsset(null));
    }

    [DataTestMethod]
    [DataRow(Sha256 + "  Performix-win-x64.zip", Sha256)]
    [DataRow(Sha256 + " *Performix-win-x64.zip", Sha256)]
    [DataRow("SHA256 (Performix-win-x64.zip) = " + Sha256, Sha256)]
    [DataRow(Sha256, Sha256)]
    [DataRow("", null)]
    [DataRow(null, null)]
    [DataRow("not a checksum", null)]
    public void ParseChecksumFile_HandlesTheCommonFormats(string? content, string? expected)
        => Assert.AreEqual(expected, GitHubRelease.ParseChecksumFile(content), content);

    [TestMethod]
    public void ParseChecksumFile_UsesTheFirstDigestInAMultiFileListing()
    {
        string content = """
        abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  other.zip
        9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9  Performix-win-x64.zip
        """;

        Assert.AreEqual("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            GitHubRelease.ParseChecksumFile(content));
    }

    [DataTestMethod]
    [DataRow("https://github.com/YunxiRamito/Performix/releases/tag/v0.1.0-alpha.2", "v0.1.0-alpha.2")]
    [DataRow("https://github.com/YunxiRamito/Performix/releases/tag/v0.1.0-alpha.2?expanded=true", "v0.1.0-alpha.2")]
    [DataRow("https://github.com/YunxiRamito/Performix/releases/tag/v0.1.0-alpha.2/", "v0.1.0-alpha.2")]
    [DataRow("https://github.com/YunxiRamito/Performix/releases/latest", null)]
    [DataRow("https://github.com/YunxiRamito/Performix/releases", null)]
    [DataRow("not a url", null)]
    [DataRow(null, null)]
    public void TagFromLocation_ReadsTheRedirectTarget(string? location, string? expected)
        => Assert.AreEqual(expected, GitHubRelease.TagFromLocation(location), location);

    [TestMethod]
    public void Urls_PointAtTheDocumentedEndpoints()
    {
        Assert.AreEqual("https://api.github.com/repos/YunxiRamito/Performix/releases/latest",
            GitHubRelease.ApiUrl());
        Assert.AreEqual("https://api.github.com/repos/YunxiRamito/Performix/releases/tags/v1.0.0",
            GitHubRelease.ApiUrl("v1.0.0"));
        Assert.AreEqual("https://github.com/YunxiRamito/Performix/releases/latest",
            GitHubRelease.LatestReleasePageUrl());
        Assert.AreEqual(
            "https://github.com/YunxiRamito/Performix/releases/download/v0.1.0/Performix-win-x64.zip",
            GitHubRelease.AssetDownloadUrl("v0.1.0"));
    }

    [TestMethod]
    public void VersionFromFileName_UsesThePackageNameOrFallsBackToLocal()
    {
        Assert.AreEqual("local", GitHubRelease.VersionFromFileName(@"C:\tmp\Performix-win-x64.zip"));
        Assert.AreEqual("0.2.0", GitHubRelease.VersionFromFileName(@"C:\tmp\v0.2.0.zip"));
        Assert.AreEqual("local", GitHubRelease.VersionFromFileName(null));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Latest-release selection (the 404 regression)
//
// /releases/latest answers 404 whenever every published release is a prerelease, which is how
// Performix ships. These tests pin the list endpoint + selection rules that replaced it, so the
// installer can never again answer "无法确定最新版本" while a perfectly installable alpha exists.
// ═══════════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class GitHubReleaseListTests
{
    /// <summary>One release object for the list fixture (no assets needed for selection).</summary>
    private static string Entry(string tag, bool prerelease, bool draft = false,
        string publishedAt = "2026-11-01T00:00:00Z")
        => $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": {{(prerelease ? "true" : "false")}},
          "draft": {{(draft ? "true" : "false")}},
          "published_at": "{{publishedAt}}",
          "assets": [
            { "name": "Performix-win-x64.zip", "browser_download_url": "https://example.test/{{tag}}/Performix-win-x64.zip" }
          ]
        }
        """;

    private static string ListJson(params string[] entries) => "[" + string.Join(",", entries) + "]";

    [TestMethod]
    public void ListApiUrl_AsksForTheReleaseList()
    {
        Assert.AreEqual("https://api.github.com/repos/YunxiRamito/Performix/releases?per_page=20",
            GitHubRelease.ListApiUrl());
        Assert.AreEqual("https://api.github.com/repos/o/r/releases?per_page=5",
            GitHubRelease.ListApiUrl(5, "o", "r"));
    }

    [TestMethod]
    public void ParseReleaseList_ReadsDraftsPrereleasesAndTimestamps()
    {
        GitHubReleaseListParseResult parsed = GitHubRelease.ParseReleaseList(ListJson(
            Entry("v0.1.0-alpha.2", prerelease: true, publishedAt: "2026-11-13T15:25:26Z"),
            Entry("v0.1.0", prerelease: false, draft: true)));

        Assert.IsTrue(parsed.Ok, parsed.Error);
        Assert.AreEqual(2, parsed.Releases.Count);
        Assert.AreEqual("v0.1.0-alpha.2", parsed.Releases[0].Tag);
        Assert.IsTrue(parsed.Releases[0].Prerelease);
        Assert.IsFalse(parsed.Releases[0].Draft);
        Assert.AreEqual(2026, parsed.Releases[0].PublishedAt!.Value.Year);
        Assert.IsTrue(parsed.Releases[1].Draft);
    }

    [TestMethod]
    public void ParseReleaseList_ReportsApiErrorsAndNonArrays()
    {
        GitHubReleaseListParseResult rateLimited = GitHubRelease.ParseReleaseList(
            """{ "message": "API rate limit exceeded" }""");
        Assert.IsFalse(rateLimited.Ok);
        StringAssert.Contains(rateLimited.Error, "rate limit");

        Assert.IsFalse(GitHubRelease.ParseReleaseList(null).Ok);
        Assert.IsFalse(GitHubRelease.ParseReleaseList("{ not json").Ok);
        Assert.IsFalse(GitHubRelease.ParseReleaseList("""{ "tag_name": "v1.0.0" }""").Ok);
    }

    [TestMethod]
    public void ParseReleaseList_EmptyArrayIsNotAnError()
    {
        GitHubReleaseListParseResult parsed = GitHubRelease.ParseReleaseList("[]");

        Assert.IsTrue(parsed.Ok, parsed.Error);
        Assert.AreEqual(0, parsed.Releases.Count);
    }

    /// <summary>The exact field failure: the repository has prereleases only.</summary>
    [TestMethod]
    public void SelectLatest_OnlyPrereleases_PicksTheHighestVersion()
    {
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(
                Entry("v0.1.0-alpha.1", prerelease: true, publishedAt: "2026-11-01T00:00:00Z"),
                Entry("v0.1.0-alpha.2", prerelease: true, publishedAt: "2026-11-13T15:25:26Z"))).Releases);

        Assert.IsNotNull(chosen);
        Assert.AreEqual("v0.1.0-alpha.2", chosen.Tag);
    }

    [TestMethod]
    public void SelectLatest_PrefersStableOverItsOwnPrerelease()
    {
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(
                Entry("v0.1.0-alpha.2", prerelease: true),
                Entry("v0.1.0", prerelease: false))).Releases);

        Assert.IsNotNull(chosen);
        Assert.AreEqual("v0.1.0", chosen.Tag);
    }

    [TestMethod]
    public void SelectLatest_PrefersBetaOverAlpha()
    {
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(
                Entry("v0.2.0-alpha.1", prerelease: true),
                Entry("v0.1.0-beta.1", prerelease: true))).Releases);

        Assert.IsNotNull(chosen);
        Assert.AreEqual("v0.1.0-beta.1", chosen.Tag);
    }

    [TestMethod]
    public void SelectLatest_NeverPicksADraft()
    {
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(
                Entry("v0.3.0", prerelease: false, draft: true),
                Entry("v0.1.0-alpha.2", prerelease: true))).Releases);

        Assert.IsNotNull(chosen);
        Assert.AreEqual("v0.1.0-alpha.2", chosen.Tag);
    }

    [TestMethod]
    public void SelectLatest_SortsVersionSegmentsNumerically()
    {
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(
                Entry("v0.1.0-alpha.9", prerelease: true, publishedAt: "2026-11-10T00:00:00Z"),
                Entry("v0.1.0-alpha.10", prerelease: true, publishedAt: "2026-11-09T00:00:00Z"))).Releases);

        Assert.IsNotNull(chosen);
        Assert.AreEqual("v0.1.0-alpha.10", chosen.Tag, "a string compare would pick alpha.9");
    }

    [TestMethod]
    public void SelectLatest_EqualVersions_TakeTheLaterPublishedOne()
    {
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(
                Entry("v0.1.0-alpha.2", prerelease: true, publishedAt: "2026-11-01T00:00:00Z"),
                Entry("v0.1.0-alpha.2", prerelease: true, publishedAt: "2026-11-13T00:00:00Z"))).Releases);

        Assert.IsNotNull(chosen);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), chosen.PublishedAt);
    }

    [TestMethod]
    public void SelectLatest_EmptyOrAllDraftLists_ReturnNull()
    {
        Assert.IsNull(GitHubRelease.SelectLatest(GitHubRelease.ParseReleaseList("[]").Releases));
        Assert.IsNull(GitHubRelease.SelectLatest(null));
        Assert.IsNull(GitHubRelease.SelectLatest(Array.Empty<GitHubReleaseInfo>()));
        Assert.IsNull(GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(Entry("v0.1.0", prerelease: false, draft: true))).Releases));
    }

    [TestMethod]
    public void SelectLatest_UnparseableTags_FallBackToNewestPublished()
    {
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            GitHubRelease.ParseReleaseList(ListJson(
                Entry("nightly", prerelease: true, publishedAt: "2026-11-01T00:00:00Z"),
                Entry("weekly", prerelease: true, publishedAt: "2026-11-12T00:00:00Z"))).Releases);

        Assert.IsNotNull(chosen);
        Assert.AreEqual("weekly", chosen.Tag);
    }

    [DataTestMethod]
    [DataRow("v0.1.0-alpha.2", ReleaseChannel.Alpha)]
    [DataRow("0.1.0-alpha.2", ReleaseChannel.Alpha)]
    [DataRow("v0.1.0-beta.3", ReleaseChannel.Beta)]
    [DataRow("v0.1.0-rc.1", ReleaseChannel.Beta)]
    [DataRow("v0.1.0", ReleaseChannel.Stable)]
    [DataRow("0.1.0", ReleaseChannel.Stable)]
    public void ChannelOf_ClassifiesTheTag(string tag, ReleaseChannel expected)
        => Assert.AreEqual(expected, GitHubRelease.ChannelOf(new GitHubReleaseInfo(tag, Array.Empty<GitHubAsset>())));

    [TestMethod]
    public void TagCandidates_TogglesTheVPrefix()
    {
        CollectionAssert.AreEqual(new[] { "0.1.0-alpha.2", "v0.1.0-alpha.2" },
            GitHubRelease.TagCandidates("0.1.0-alpha.2").ToArray());
        CollectionAssert.AreEqual(new[] { "v0.1.0-alpha.2", "0.1.0-alpha.2" },
            GitHubRelease.TagCandidates(" v0.1.0-alpha.2 ").ToArray());
        CollectionAssert.AreEqual(new[] { "V0.1.0", "0.1.0" },
            GitHubRelease.TagCandidates("V0.1.0").ToArray());
        Assert.AreEqual(0, GitHubRelease.TagCandidates(null).Count);
        Assert.AreEqual(0, GitHubRelease.TagCandidates("   ").Count);
    }

    [TestMethod]
    public void ReleasesFeedUrl_PointsAtThePublicFeed()
        => Assert.AreEqual("https://github.com/YunxiRamito/Performix/releases.atom",
            GitHubRelease.ReleasesFeedUrl());

    [DataTestMethod]
    [DataRow("0.1.0-alpha.2", "v0.1.0-alpha.2")]   // the spelling the user forgot the "v" for
    [DataRow("v0.1.0-alpha.2", "v0.1.0-alpha.2")]
    [DataRow("V0.1.0-ALPHA.2", "v0.1.0-alpha.2")]
    [DataRow("9.9.9", null)]                       // never invents a tag that is not there
    [DataRow(null, null)]
    public void MatchTag_ResolvesTheSpellingAgainstTheFeed(string? requested, string? expected)
    {
        var feed = new[] { "v0.1.0-alpha.2", "v0.1.0-alpha.1" };

        Assert.AreEqual(expected, GitHubRelease.MatchTag(feed, requested), requested);
        Assert.IsNull(GitHubRelease.MatchTag(Array.Empty<string>(), requested));
        Assert.IsNull(GitHubRelease.MatchTag(null, requested));
    }

    /// <summary>
    /// The feed fallback exists because the API is rate limited per IP; it must survive a real
    /// feed body (self link, entry ids, the tag link) without an XML parser.
    /// </summary>
    [TestMethod]
    public void ParseReleasesFeed_ReadsTagsNewestFirst()
    {
        const string feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:media="http://search.yahoo.com/mrss/">
          <id>tag:github.com,2008:/YunxiRamito/Performix/releases</id>
          <link rel="self" type="application/atom+xml" href="https://github.com/YunxiRamito/Performix/releases.atom"/>
          <entry>
            <id>tag:github.com,2008:Repository/123/v0.1.0-alpha.2</id>
            <updated>2026-11-13T15:25:26Z</updated>
            <link rel="alternate" type="text/html" href="https://github.com/YunxiRamito/Performix/releases/tag/v0.1.0-alpha.2"/>
            <title>v0.1.0-alpha.2</title>
          </entry>
          <entry>
            <id>tag:github.com,2008:Repository/123/v0.1.0-alpha.1</id>
            <link rel="alternate" type="text/html" href="https://github.com/YunxiRamito/Performix/releases/tag/v0.1.0-alpha.1"/>
            <title>v0.1.0-alpha.1</title>
          </entry>
        </feed>
        """;

        CollectionAssert.AreEqual(new[] { "v0.1.0-alpha.2", "v0.1.0-alpha.1" },
            GitHubRelease.ParseReleasesFeed(feed).ToArray());

        Assert.AreEqual(0, GitHubRelease.ParseReleasesFeed(null).Count);
        Assert.AreEqual(0, GitHubRelease.ParseReleasesFeed("<feed></feed>").Count);
        Assert.AreEqual(0, GitHubRelease.ParseReleasesFeed("not xml").Count);
    }

    [TestMethod]
    public void ParseReleasesFeed_SelectionPrefersTheStableEntry()
    {
        const string feed = """
        <feed>
          <entry><link href="https://github.com/o/r/releases/tag/v0.2.0-alpha.1"/></entry>
          <entry><link href="https://github.com/o/r/releases/tag/v0.1.0"/></entry>
        </feed>
        """;

        IReadOnlyList<string> tags = GitHubRelease.ParseReleasesFeed(feed);
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            tags.Select(tag => new GitHubReleaseInfo(tag, Array.Empty<GitHubAsset>())).ToList());

        Assert.IsNotNull(chosen);
        Assert.AreEqual("v0.1.0", chosen.Tag);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Package layout
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
public class PackageExtractorTests
{
    [TestMethod]
    public void DetectSingleRoot_StripsALoneWrapperFolder()
    {
        string? root = PackageExtractor.DetectSingleRoot(new[]
        {
            "Performix-win-x64/Performix.exe",
            "Performix-win-x64/preysense.json",
            "Performix-win-x64/Assets/icon.ico",
        });

        Assert.AreEqual("Performix-win-x64", root);
    }

    [TestMethod]
    public void DetectSingleRoot_KeepsAFlatArchiveFlat()
    {
        Assert.IsNull(PackageExtractor.DetectSingleRoot(new[]
        {
            "Performix.exe",
            "preysense.json",
        }));
    }

    [TestMethod]
    public void DetectSingleRoot_RefusesMultipleTopLevelFolders()
    {
        Assert.IsNull(PackageExtractor.DetectSingleRoot(new[]
        {
            "app/Performix.exe",
            "assets/icon.ico",
        }));
    }

    [TestMethod]
    public void DetectSingleRoot_HandlesBackslashesAndDirectoryEntries()
    {
        Assert.AreEqual("Performix", PackageExtractor.DetectSingleRoot(new[]
        {
            "Performix\\",
            "Performix\\Performix.exe",
        }));
    }

    [TestMethod]
    public void DetectSingleRoot_HandlesEmptyInput()
    {
        Assert.IsNull(PackageExtractor.DetectSingleRoot(null));
        Assert.IsNull(PackageExtractor.DetectSingleRoot(Array.Empty<string>()));
    }

    [TestMethod]
    public void FindExecutable_RequiresTheFileToExist()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PerformixExtractorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.IsNull(PackageExtractor.FindExecutable(directory));

            File.WriteAllText(Path.Combine(directory, InstallerPaths.ExecutableName), "stub");

            Assert.IsNotNull(PackageExtractor.FindExecutable(directory));
            Assert.IsNull(PackageExtractor.FindExecutable(""));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
