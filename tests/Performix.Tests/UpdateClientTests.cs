// Regression tests for the update path that do not touch the network or the UI.
//
// The tested code is linked into this project from src\Performix.App (see the Compile items in
// Performix.Tests.csproj): SemanticVersion, UpdateManifestParser, UpdateClient,
// UpdateOptions/UpdateSourceCatalog and UpdateStateMachine. Everything external is injected:
//   • HTTP → FakeHttpHandler (routes by URL, can delay, truncate or stall a body)
//   • disk → a unique temp directory per test
// What is covered here is exactly what the brief calls out as the risky part:
//   • semantic version / pre-release precedence (never a string comparison),
//   • the manifest hard gates (sizeBytes + sha256, schema, channel, https host),
//   • the two source families (official first, accelerated as fallback, highest wins),
//   • download resume via Range, the stall watchdog, unknown Content-Length,
//   • size-before-hash verification with deletion on mismatch,
//   • the page state machine transitions.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Predex.WinUI.Services.Update;

namespace PreySense.Tests;

// ═══════════════════════════════════════════════════════════════════════════════════════
// Semantic version: parsing and SemVer precedence
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
[DoNotParallelize]
public class SemanticVersionTests
{
    [DataTestMethod]
    [DataRow("0.1.0-alpha.1")]
    [DataRow("v0.1.0-alpha.2")]
    [DataRow("V1.2.3")]
    [DataRow("1.2.3.4")]
    [DataRow("1.2.3+build.5")]
    [DataRow("2.0.0-rc.1+abc123")]
    [DataRow("0.1.0")]
    public void TryParse_AcceptsDocumentedShapes(string text)
    {
        Assert.IsTrue(SemanticVersion.TryParse(text, out SemanticVersion? version), text);
        Assert.IsNotNull(version, text);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("one")]
    [DataRow("a.b.c")]
    [DataRow("1.2.3-")]
    [DataRow("1.2.3.4.5")]
    [DataRow("1..2")]
    [DataRow("-1.2.3")]
    [DataRow("1.2.3-rc..1")]
    [DataRow("1.2.3-rc 1")]
    public void TryParse_RejectsGarbage(string text)
    {
        Assert.IsFalse(SemanticVersion.TryParse(text, out SemanticVersion? version), text);
        Assert.IsNull(version, text);
    }

    [TestMethod]
    public void TryParse_RejectsNull()
    {
        Assert.IsFalse(SemanticVersion.TryParse(null, out _));
        Assert.IsNull(SemanticVersion.Parse(null));
    }

    [TestMethod]
    public void Compare_PreReleaseNumbersAreNumeric()
    {
        Assert.IsGreaterThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("0.1.0-alpha.2"), SemanticVersion.Parse("0.1.0-alpha.1")));
        Assert.IsLessThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("0.1.0-alpha.9"), SemanticVersion.Parse("0.1.0-alpha.10")));
    }

    [TestMethod]
    public void Compare_AlphaBetaRcStableOrdering()
    {
        string[] ladder = { "1.0.0-alpha.1", "1.0.0-beta.1", "1.0.0-rc.1", "1.0.0" };
        for (int i = 1; i < ladder.Length; i++)
        {
            Assert.IsLessThan(0, SemanticVersion.Compare(
                SemanticVersion.Parse(ladder[i - 1]), SemanticVersion.Parse(ladder[i])), $"{ladder[i - 1]} < {ladder[i]}");
        }
    }

    [TestMethod]
    public void Compare_ReleaseBeatsItsOwnPreRelease()
    {
        Assert.IsGreaterThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("0.1.0"), SemanticVersion.Parse("0.1.0-rc.1")));
        Assert.IsLessThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("0.1.0-alpha.1"), SemanticVersion.Parse("0.1.0")));
    }

    [TestMethod]
    public void Compare_NumericIdentifierSortsBelowAlphanumeric()
    {
        Assert.IsLessThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("1.0.0-1"), SemanticVersion.Parse("1.0.0-alpha")));
        Assert.IsGreaterThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("1.0.0-alpha"), SemanticVersion.Parse("1.0.0-1")));
    }

    [TestMethod]
    public void Compare_ShorterPreReleaseLoses()
    {
        Assert.IsLessThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("1.0.0-alpha"), SemanticVersion.Parse("1.0.0-alpha.1")));
    }

    [TestMethod]
    public void Compare_RevisionMissingEqualsZero()
    {
        Assert.AreEqual(0, SemanticVersion.Compare(
            SemanticVersion.Parse("1.7.1.0"), SemanticVersion.Parse("1.7.1")));
        Assert.IsGreaterThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("1.7.1.1"), SemanticVersion.Parse("1.7.1")));
    }

    [TestMethod]
    public void Compare_BuildMetadataIsIgnored()
    {
        Assert.AreEqual(0, SemanticVersion.Compare(
            SemanticVersion.Parse("1.2.3+build.9"), SemanticVersion.Parse("1.2.3+other.1")));
    }

    [TestMethod]
    public void Compare_CorePartsAreNumericNotLexical()
    {
        Assert.IsGreaterThan(0, SemanticVersion.Compare(
            SemanticVersion.Parse("0.10.0"), SemanticVersion.Parse("0.9.0")));
    }

    [TestMethod]
    public void Compare_HandlesNulls()
    {
        Assert.AreEqual(0, SemanticVersion.Compare(null, null));
        Assert.IsLessThan(0, SemanticVersion.Compare(null, SemanticVersion.Parse("1.0.0")));
        Assert.IsGreaterThan(0, SemanticVersion.Compare(SemanticVersion.Parse("1.0.0"), null));
    }

    [TestMethod]
    public void IsNewer_RequiresBothSidesToParse()
    {
        Assert.IsTrue(SemanticVersion.IsNewer("0.1.0-alpha.2", "0.1.0-alpha.1"));
        Assert.IsFalse(SemanticVersion.IsNewer("0.1.0-alpha.1", "0.1.0-alpha.1"));
        Assert.IsFalse(SemanticVersion.IsNewer("0.1.0-rc.1", "0.1.0"));
        // An unreadable value is never "newer": the update path must not install on garbage.
        Assert.IsFalse(SemanticVersion.IsNewer("garbage", "0.1.0-alpha.1"));
        Assert.IsFalse(SemanticVersion.IsNewer("0.1.0-alpha.2", "garbage"));
    }

    [TestMethod]
    public void ToString_RoundTripsPrereleaseAndRevision()
    {
        Assert.AreEqual("0.1.0-alpha.2", SemanticVersion.Parse("v0.1.0-alpha.2")!.ToString());
        Assert.AreEqual("1.2.3.4", SemanticVersion.Parse("1.2.3.4+meta")!.ToString());
        Assert.AreEqual("1.2.3", SemanticVersion.Parse("1.2.3")!.ToString());
    }

    [TestMethod]
    public void EqualsAndGetHashCode_AgreeWhenNumericPreReleaseIdentifiersDifferInLeadingZeros()
    {
        // CompareNumeric ignores leading zeros, so these two are the same version. The hash
        // must agree, otherwise a HashSet/Dictionary silently holds two "equal" versions.
        SemanticVersion padded = SemanticVersion.Parse("1.0.0-a.01")!;
        SemanticVersion plain = SemanticVersion.Parse("1.0.0-a.1")!;

        Assert.AreEqual(0, padded.CompareTo(plain));
        Assert.IsTrue(padded.Equals(plain));
        Assert.AreEqual(plain.GetHashCode(), padded.GetHashCode());

        var set = new HashSet<SemanticVersion> { padded };
        Assert.IsFalse(set.Add(plain), "an equal version must not be stored twice");
        Assert.AreEqual(1, set.Count);

        Assert.AreEqual(
            SemanticVersion.Parse("1.0.0-alpha.0001")!.GetHashCode(),
            SemanticVersion.Parse("1.0.0-alpha.1")!.GetHashCode());
    }

    [TestMethod]
    public void GetHashCode_StillSeparatesVersionCores()
    {
        Assert.AreNotEqual(
            SemanticVersion.Parse("1.0.0-a.1")!.GetHashCode(),
            SemanticVersion.Parse("1.0.1-a.1")!.GetHashCode());
        Assert.AreNotEqual(
            SemanticVersion.Parse("1.0.0-a.1")!.GetHashCode(),
            SemanticVersion.Parse("1.0.0-a.2")!.GetHashCode());
    }

    [TestMethod]
    public void CompareToObject_NullReturnsOnePerTheIComparableContract()
    {
        SemanticVersion version = SemanticVersion.Parse("1.0.0")!;

        // IComparable.CompareTo(object) says: "By definition, any object compares greater than
        // null". Throwing here makes Array.Sort/List.Sort fail on a null element.
        Assert.AreEqual(1, ((IComparable)version).CompareTo(null));
        Assert.ThrowsExactly<ArgumentException>(() => ((IComparable)version).CompareTo(42));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Manifest parsing + hard gates
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
[DoNotParallelize]
public class UpdateManifestParserTests
{
    private const string Sha = "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    [TestMethod]
    public void Parse_ValidManifest_ExposesAssetAndNotes()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, minVersion: "0.1.0-alpha.1");
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsTrue(parsed.Succeeded, parsed.Failure);
        UpdateManifest manifest = parsed.Manifest!;
        Assert.AreEqual(1, manifest.Schema);
        Assert.AreEqual("alpha", manifest.Channel);
        Assert.AreEqual("0.1.0-alpha.2", manifest.Version.ToString());
        Assert.AreEqual("first release notes", manifest.Notes);
        Assert.AreEqual(1, manifest.Assets.Count);
        Assert.AreEqual(0, manifest.RejectedAssets.Count);
        Assert.AreEqual("0.1.0-alpha.1", manifest.MinVersion!.ToString());
        Assert.AreEqual(TimeSpan.Zero, manifest.PublishedAt.Offset);
        Assert.AreEqual("Performix-win-x64.zip", manifest.SelectAsset()!.Name);
        Assert.AreEqual(4096, manifest.SelectAsset()!.SizeBytes);
        Assert.AreEqual(Sha, manifest.SelectAsset()!.Sha256);
    }

    [TestMethod]
    public void Parse_AssetWithoutSha256_IsRejectedAsIncomplete()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: null);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        Assert.IsNull(parsed.Manifest);
        Assert.Contains("incomplete", parsed.Failure!);
        Assert.AreEqual(1, parsed.RejectedAssets.Count);
        StringAssert.Contains(parsed.RejectedAssets[0].Reason, "sha256");
    }

    [TestMethod]
    public void Parse_AssetWithoutSizeBytes_IsRejectedAsIncomplete()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, sizeBytes: 0);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        Assert.Contains("incomplete", parsed.Failure!);
        StringAssert.Contains(parsed.RejectedAssets[0].Reason, "sizeBytes");
    }

    [DataTestMethod]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public void Parse_ImplausibleSize_IsRejected(long sizeBytes)
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, sizeBytes: sizeBytes);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.RejectedAssets[0].Reason, "sizeBytes");
    }

    [DataTestMethod]
    [DataRow("xyz")]
    [DataRow("9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f")]   // 62 chars
    [DataRow("9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9ff")] // 66 chars
    [DataRow("zz2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    public void Parse_InvalidSha256_IsRejected(string sha)
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: sha);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.RejectedAssets[0].Reason, "sha256");
    }

    [TestMethod]
    public void Parse_ChannelMismatch_Fails()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, channel: "beta");
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.Failure, "channel");
    }

    [TestMethod]
    public void Parse_UnsupportedSchema_Fails()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, schema: 2);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.Failure, "schema");
    }

    [TestMethod]
    public void Parse_UnreadableVersion_Fails()
    {
        string json = TestManifests.Manifest("not-a-version", sha: Sha);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.Failure, "version");
    }

    [TestMethod]
    public void Parse_MissingPublishedAt_Fails()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, publishedAt: null);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.Failure, "publishedAt");
    }

    [TestMethod]
    public void Parse_NoAssets_Fails()
    {
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(
            """{ "schema": 1, "channel": "alpha", "version": "0.1.0-alpha.2", "publishedAt": "2026-11-14T00:00:00Z", "assets": [] }""",
            "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.Failure, "no asset carries both sizeBytes and sha256");
    }

    [TestMethod]
    public void Parse_NotJson_Fails()
    {
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse("<html>404</html>", "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.Failure, "JSON");
    }

    [TestMethod]
    public void Parse_HttpAssetUrl_RejectedUnlessLocalOverride()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha,
            url: "http://127.0.0.1:8099/Performix-win-x64.zip");

        // A shipping build only accepts https release hosts.
        Assert.IsFalse(UpdateManifestParser.Parse(json, "alpha").Succeeded);
        // The local test feed may point at loopback http, otherwise the state machine could not
        // be verified without publishing a release.
        Assert.IsTrue(UpdateManifestParser.Parse(json, "alpha", allowLocalUrls: true).Succeeded);
    }

    [TestMethod]
    public void Parse_AssetNameWithPathSeparators_IsRejected()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, assetName: "../../evil.zip");
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.RejectedAssets[0].Reason, "plain file name");
    }

    [DataTestMethod]
    [DataRow("..")]
    [DataRow("..\\evil.zip")]
    [DataRow("a..b.zip")]
    [DataRow("CON")]
    [DataRow("con.zip")]
    [DataRow("NUL.tar.gz")]
    [DataRow("AUX")]
    [DataRow("COM1")]
    [DataRow("com1.zip")]
    [DataRow("LPT9.bin")]
    [DataRow("Performix-win-x64.zip.")]
    public void Parse_AssetNameThatIsNotAPlainFileName_IsRejected(string assetName)
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, assetName: assetName);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded, assetName);
        Assert.AreEqual(1, parsed.RejectedAssets.Count, assetName);
        StringAssert.Contains(parsed.RejectedAssets[0].Reason, "plain file name", assetName);
    }

    [DataTestMethod]
    [DataRow("Performix-win-x64.zip")]
    [DataRow("Performix-0.1.0-alpha.2.tar.gz")]
    [DataRow("com10.zip")]
    [DataRow("console.zip")]
    [DataRow("auxiliary.zip")]
    public void Parse_OrdinaryAssetName_IsAccepted(string assetName)
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, assetName: assetName);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsTrue(parsed.Succeeded, parsed.Failure);
        Assert.AreEqual(assetName, parsed.Manifest!.Assets[0].Name);
    }

    [DataTestMethod]
    [DataRow("\"minVersion\": 5")]
    [DataRow("\"minVersion\": null")]
    [DataRow("\"minVersion\": [\"0.1.0\"]")]
    [DataRow("\"minVersion\": { \"value\": \"0.1.0\" }")]
    [DataRow("\"minVersion\": \"\"")]
    [DataRow("\"minVersion\": \"not-a-version\"")]
    public void Parse_UnreadableMinVersion_RejectsTheWholeManifest(string minVersionJson)
    {
        string json = """
        {
          "schema": 1, "channel": "alpha", "version": "0.1.0-alpha.2",
          "publishedAt": "2026-11-14T00:00:00Z",
          MINVERSION,
          "assets": [
            { "name": "Performix-win-x64.zip", "url": "https://github.com/a/b.zip",
              "sizeBytes": 10, "sha256": "SHA" }
          ]
        }
        """.Replace("MINVERSION", minVersionJson).Replace("SHA", Sha);

        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsFalse(parsed.Succeeded, "a declared minimum version must never be silently dropped");
        Assert.IsNull(parsed.Manifest);
        StringAssert.Contains(parsed.Failure!, "minVersion");
    }

    [TestMethod]
    public void Parse_MinVersionAbsent_IsStillAccepted()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.2", sha: Sha);
        UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, "alpha");

        Assert.IsTrue(parsed.Succeeded, parsed.Failure);
        Assert.IsNull(parsed.Manifest!.MinVersion);
    }

    [TestMethod]
    public void Parse_SelectAsset_PrefersWinX64Zip()
    {
        string json = """
        {
          "schema": 1, "channel": "alpha", "version": "0.1.0-alpha.2",
          "publishedAt": "2026-11-14T00:00:00Z",
          "assets": [
            { "name": "Performix-win-arm64.zip", "url": "https://github.com/a/b.zip", "sizeBytes": 10, "sha256": "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9" },
            { "name": "Performix-win-x64.zip",   "url": "https://github.com/a/c.zip", "sizeBytes": 20, "sha256": "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9" },
            { "name": "Performix.dbg",           "url": "https://github.com/a/d.dbg", "sizeBytes": 30, "sha256": "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9" }
          ]
        }
        """;
        UpdateManifest manifest = UpdateManifestParser.Parse(json, "alpha").Manifest!;

        Assert.AreEqual("Performix-win-x64.zip", manifest.SelectAsset()!.Name);
        Assert.AreEqual(3, manifest.Assets.Count);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Page state machine
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
[DoNotParallelize]
public class UpdateStateMachineTests
{
    private const string Sha = "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static UpdateManifest Manifest(string version = "0.1.0-alpha.2")
        => UpdateManifestParser.Parse(TestManifests.Manifest(version, sha: Sha), "alpha").Manifest!;

    private static UpdateCheckResult ReleaseResult(string version = "0.1.0-alpha.2")
    {
        UpdateManifest manifest = Manifest(version);
        return new UpdateCheckResult(true, manifest, manifest.SelectAsset(),
            UpdateSourceCatalog.Official("alpha")[0], false, null,
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 12);
    }

    private static UpdateCheckResult NoReleaseResult()
        => new(false, null, null, null, true,
            "No release has been published on the update feed yet.",
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 12);

    /// <summary>Drives a machine into one of the "operation owns the page" states.</summary>
    private static UpdateStateMachine MachineIn(UpdatePageState state)
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        if (state == UpdatePageState.Checking) return machine;

        machine.ApplyCheck(ReleaseResult());
        if (state == UpdatePageState.Available) return machine;

        machine.BeginDownload();
        if (state == UpdatePageState.Downloading) return machine;

        machine.BeginVerify();
        if (state == UpdatePageState.Verifying) return machine;

        machine.MarkReadyToInstall();
        Assert.AreEqual(UpdatePageState.ReadyToInstall, machine.State);
        return machine;
    }

    [TestMethod]
    public void Initial_IsIdleAndCheckable()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");

        Assert.AreEqual(UpdatePageState.Idle, machine.State);
        Assert.IsTrue(machine.CanCheck);
        Assert.IsFalse(machine.IsBusy);
        Assert.IsFalse(machine.HasFailure);
        Assert.IsFalse(machine.IsProgressVisible);
    }

    [TestMethod]
    public void ApplyCheck_NewerVersion_BecomesAvailable()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        Assert.AreEqual(UpdatePageState.Checking, machine.State);
        Assert.IsTrue(machine.IsBusy);

        machine.ApplyCheck(ReleaseResult("0.1.0-alpha.2"));

        Assert.AreEqual(UpdatePageState.Available, machine.State);
        Assert.IsTrue(machine.IsNewer);
        Assert.IsTrue(machine.CanDownload);
        Assert.IsFalse(machine.IsBusy);
        Assert.AreEqual("0.1.0-alpha.2", machine.Manifest!.Version.ToString());
    }

    [TestMethod]
    public void ApplyCheck_SameVersion_IsUpToDate()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.2");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult("0.1.0-alpha.2"));

        Assert.AreEqual(UpdatePageState.UpToDate, machine.State);
        Assert.IsFalse(machine.IsNewer);
        Assert.IsFalse(machine.CanDownload);
    }

    [TestMethod]
    public void ApplyCheck_NewerStable_UpdatesAnAlphaBuild()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult("0.1.0"));

        Assert.AreEqual(UpdatePageState.Available, machine.State);
        Assert.IsTrue(machine.IsNewer);
    }

    [TestMethod]
    public void ApplyCheck_NeverDowngrades()
    {
        var machine = new UpdateStateMachine("alpha", "0.2.0");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult("0.1.0-alpha.9"));

        Assert.AreEqual(UpdatePageState.UpToDate, machine.State);
        Assert.IsFalse(machine.IsNewer);
    }

    [TestMethod]
    public void ApplyCheck_NoRelease_IsFailedButFlaggedAsNoRelease()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(NoReleaseResult());

        Assert.AreEqual(UpdatePageState.Failed, machine.State);
        Assert.AreEqual(UpdateFailureStage.Check, machine.FailureStage);
        Assert.IsTrue(machine.IsNoRelease);
        StringAssert.Contains(machine.FailureReason, "No release");
        Assert.IsTrue(machine.CanCheck);
    }

    [TestMethod]
    public void ApplyCheck_UnreadableCurrentVersion_FailsInsteadOfClaimingUpToDate()
    {
        var machine = new UpdateStateMachine("alpha", "not-a-version");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult("0.1.0-alpha.2"));

        Assert.AreEqual(UpdatePageState.Failed, machine.State);
        Assert.AreEqual(UpdateFailureStage.Check, machine.FailureStage);
        Assert.IsFalse(machine.IsNewer);
    }

    [TestMethod]
    public void Download_ProgressWithKnownTotal_ReportsPercent()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());
        machine.BeginDownload();

        Assert.AreEqual(UpdatePageState.Downloading, machine.State);
        Assert.IsTrue(machine.IsProgressVisible);
        Assert.IsTrue(machine.CanCancel);

        machine.ApplyProgress(new UpdateDownloadProgress(50, 100, 1024, TimeSpan.FromSeconds(2)));
        Assert.AreEqual(50.0, machine.Percent, 0.001);
        Assert.IsFalse(machine.IsIndeterminate);

        machine.ApplyProgress(new UpdateDownloadProgress(150, 100, 1024, null));
        Assert.AreEqual(100.0, machine.Percent, 0.001, "percent must clamp at 100");
    }

    [TestMethod]
    public void Download_UnknownTotal_IsIndeterminateAndHasNoPercent()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());
        machine.BeginDownload();
        machine.ApplyProgress(new UpdateDownloadProgress(4096, -1, 512, null));

        Assert.IsTrue(machine.IsIndeterminate);
        Assert.AreEqual(-1.0, machine.Percent, 0.001, "a missing Content-Length must not become a percentage");
    }

    [TestMethod]
    public void Verify_ThenReadyToInstall_RequiresTheDocumentedOrder()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());

        machine.BeginVerify();
        Assert.AreEqual(UpdatePageState.Available, machine.State, "verification cannot start without a download");

        machine.BeginDownload();
        machine.BeginVerify();
        Assert.AreEqual(UpdatePageState.Verifying, machine.State);
        Assert.IsFalse(machine.CanCancel);

        machine.MarkReadyToInstall();
        Assert.AreEqual(UpdatePageState.ReadyToInstall, machine.State);
        Assert.IsTrue(machine.IsBusy);
        Assert.IsFalse(machine.CanCheck);
    }

    [TestMethod]
    public void CancelDownload_KeepsTheAvailableState()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());
        machine.BeginDownload();
        machine.ApplyProgress(new UpdateDownloadProgress(2048, 4096, 1024, TimeSpan.FromSeconds(2)));

        machine.CancelDownload();

        Assert.AreEqual(UpdatePageState.Available, machine.State);
        Assert.IsTrue(machine.CanDownload);
        Assert.AreEqual(UpdateDownloadProgress.Empty, machine.Progress);
    }

    [TestMethod]
    public void Fail_CarriesStageAndReason()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());
        machine.BeginDownload();

        machine.Fail(UpdateFailureStage.Verify, "Verification failed: the SHA-256 does not match the manifest.", "detail");

        Assert.AreEqual(UpdatePageState.Failed, machine.State);
        Assert.AreEqual(UpdateFailureStage.Verify, machine.FailureStage);
        Assert.IsTrue(machine.HasFailure);
        Assert.AreEqual("detail", machine.FailureDetail);
        Assert.IsTrue(machine.CanCheck);
    }

    /// <summary>
    /// "重试" on a failed card: Failed → Available through TryRetry, and only then a transfer may
    /// start. Before this, DownloadAsync returned on !_downloadRunning &amp;&amp; !CanDownload and the
    /// click did nothing but write "download skipped: state=Failed".
    /// </summary>
    [TestMethod]
    public void Retry_AfterDownloadVerifyOrInstallFailure_ReturnsToAvailableAndDownloads()
    {
        foreach (UpdateFailureStage stage in new[]
                 {
                     UpdateFailureStage.Download, UpdateFailureStage.Verify, UpdateFailureStage.Install,
                 })
        {
            var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
            machine.BeginCheck();
            machine.ApplyCheck(ReleaseResult());
            machine.BeginDownload();
            machine.Fail(stage, "boom", "detail");

            Assert.IsFalse(machine.CanDownload, $"{stage}: a failed card must not download before the retry");
            Assert.IsTrue(machine.CanRetry, $"{stage}: a failed {stage} is retryable");

            Assert.IsTrue(machine.TryRetry(), stage.ToString());
            Assert.AreEqual(UpdatePageState.Available, machine.State, stage.ToString());
            Assert.IsTrue(machine.CanDownload, stage.ToString());
            Assert.AreEqual(UpdateFailureStage.None, machine.FailureStage, stage.ToString());
            Assert.IsNull(machine.FailureReason, stage.ToString());
            Assert.IsNull(machine.FailureDetail, stage.ToString());

            Assert.IsTrue(machine.BeginDownload(), stage.ToString());
            Assert.AreEqual(UpdatePageState.Downloading, machine.State, stage.ToString());
            Assert.IsTrue(machine.IsProgressVisible, stage.ToString());
            Assert.IsTrue(machine.CanCancel, stage.ToString());
        }
    }

    /// <summary>
    /// "换源重试" takes the same state machine entry as the plain retry: BeginDownload migrates
    /// Failed → Available first. Before this, rotateSource:true skipped the CanDownload gate, so a
    /// transfer ran behind the failure card (no progress, no cancel) and, if it verified, started
    /// the installer and quit the app without the user knowing.
    /// </summary>
    [TestMethod]
    public void RotateSourceRetry_AfterVerifyFailure_MigratesTheStateBeforeDownloading()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());
        machine.BeginDownload();
        machine.BeginVerify();
        machine.Fail(UpdateFailureStage.Verify, "The SHA-256 does not match the manifest.", "expected …");

        Assert.AreEqual(UpdatePageState.Failed, machine.State);
        Assert.IsFalse(machine.IsProgressVisible);
        Assert.IsFalse(machine.CanCancel);

        Assert.IsTrue(machine.BeginDownload(rotatedSource: true));

        Assert.AreEqual(UpdatePageState.Downloading, machine.State);
        Assert.IsTrue(machine.IsProgressVisible, "the rotation retry must show a progress bar");
        Assert.IsTrue(machine.CanCancel, "the rotation retry must be cancelable");
        Assert.IsTrue(machine.LastDownloadRotatedSource);
        Assert.AreEqual(UpdateFailureStage.None, machine.FailureStage);
        Assert.AreEqual(UpdateDownloadProgress.Empty, machine.Progress);
    }

    /// <summary>
    /// A failed download / install entry is refused outright when the machine has no retryable
    /// package, and a failed CHECK cannot go straight to a transfer (it needs a new check).
    /// </summary>
    [TestMethod]
    public void BeginDownload_FromAFailedCheck_IsRefused()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(NoReleaseResult());

        Assert.AreEqual(UpdateFailureStage.Check, machine.FailureStage);
        Assert.IsFalse(machine.CanRetry);
        Assert.IsFalse(machine.TryRetry());
        Assert.IsFalse(machine.BeginDownload());
        Assert.IsFalse(machine.BeginDownload(rotatedSource: true));

        Assert.AreEqual(UpdatePageState.Failed, machine.State);
        Assert.AreEqual(UpdateFailureStage.Check, machine.FailureStage);
        StringAssert.Contains(machine.FailureReason, "No release");
        Assert.IsTrue(machine.CanCheck);
    }

    [TestMethod]
    public void BeginDownload_BeforeTheFirstCheck_IsRefused()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");

        Assert.IsFalse(machine.CanRetry);
        Assert.IsFalse(machine.BeginDownload());
        Assert.IsFalse(machine.BeginDownload(rotatedSource: true));
        Assert.AreEqual(UpdatePageState.Idle, machine.State);
        Assert.IsFalse(machine.IsBusy);
    }

    /// <summary>
    /// A second check must never start while one is running, and a check must never run over a
    /// download / verification / ready-to-install package. Before this, BeginCheck only rejected
    /// <see cref="UpdateStateMachine.IsBusy"/> and CheckAsync ignored the refusal, so the check
    /// went ahead anyway: two checks competed, and a completed check could overwrite
    /// ReadyToInstall and throw the verified package away.
    /// </summary>
    [TestMethod]
    public void BeginCheck_IsRefusedWhileAnOperationOwnsThePage()
    {
        foreach (UpdatePageState state in new[]
                 {
                     UpdatePageState.Checking, UpdatePageState.Downloading,
                     UpdatePageState.Verifying, UpdatePageState.ReadyToInstall,
                 })
        {
            UpdateStateMachine machine = MachineIn(state);

            Assert.AreEqual(state, machine.State, state.ToString());
            Assert.IsFalse(machine.CanCheck, state.ToString());
            Assert.IsFalse(machine.BeginCheck(), state.ToString());
            Assert.AreEqual(state, machine.State, $"{state}: a refused check must not change the state");
        }
    }

    [TestMethod]
    public void BeginCheck_FromFailed_IsAllowed()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(NoReleaseResult());
        Assert.AreEqual(UpdatePageState.Failed, machine.State);

        Assert.IsTrue(machine.BeginCheck());
        Assert.AreEqual(UpdatePageState.Checking, machine.State);
        Assert.AreEqual(UpdateFailureStage.None, machine.FailureStage);
        Assert.IsNull(machine.FailureReason);
    }

    /// <summary>
    /// The stale-result guard: a check that lost the page to a later operation must not apply
    /// its answer. In production this is the slow check that finishes after the user already
    /// downloaded and verified a package.
    /// </summary>
    [TestMethod]
    public void ApplyCheck_AfterThePageMovedOn_NeverOverwritesReadyToInstall()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());
        machine.BeginDownload();
        machine.BeginVerify();
        machine.MarkReadyToInstall();
        string verifiedVersion = machine.Manifest!.Version.ToString();

        // "已是最新" from a check that started before the download.
        var upToDate = new UpdateCheckResult(true,
            UpdateManifestParser.Parse(TestManifests.Manifest("0.1.0-alpha.1", sha: Sha), "alpha").Manifest!,
            null, UpdateSourceCatalog.Official("alpha")[0], false, null,
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 5);

        Assert.IsFalse(machine.ApplyCheck(upToDate), "a stale check result must be dropped");
        Assert.AreEqual(UpdatePageState.ReadyToInstall, machine.State);
        Assert.AreEqual(verifiedVersion, machine.Manifest!.Version.ToString(), "the verified package stays");
        Assert.IsTrue(machine.Asset is not null);
    }

    [TestMethod]
    public void ApplyCheck_WithoutABeginCheck_IsIgnored()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");

        Assert.IsFalse(machine.ApplyCheck(ReleaseResult()));
        Assert.AreEqual(UpdatePageState.Idle, machine.State);
        Assert.IsNull(machine.Check);
    }

    /// <summary>
    /// The "换源重试" path must end up in Downloading, so the cancel button actually works there.
    /// Before this it stayed Failed while the transfer ran: cancel was ignored, the log claimed
    /// "cancelled, partial kept" and no button could stop it.
    /// </summary>
    [TestMethod]
    public void RotateSourceRetry_IsCancelable_AndKeepsTheCheckResult()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(ReleaseResult());
        machine.BeginDownload();
        machine.BeginVerify();
        machine.Fail(UpdateFailureStage.Verify, "Verification failed: the file size does not match the manifest.");

        Assert.IsTrue(machine.BeginDownload(rotatedSource: true));
        Assert.AreEqual(UpdatePageState.Downloading, machine.State);
        Assert.IsTrue(machine.CanCancel, "the rotation transfer must be cancelable");

        machine.CancelDownload();

        Assert.AreEqual(UpdatePageState.Available, machine.State);
        Assert.IsTrue(machine.CanDownload, "the check result survives a cancelled rotation");
        Assert.IsFalse(machine.IsBusy);
        Assert.AreEqual(UpdateDownloadProgress.Empty, machine.Progress);
    }

    [TestMethod]
    public void Reset_ReturnsToUnchecked()
    {
        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(NoReleaseResult());

        machine.Reset();

        Assert.AreEqual(UpdatePageState.Idle, machine.State);
        Assert.IsNull(machine.Check);
        Assert.IsNull(machine.FailureReason);
        Assert.IsFalse(machine.IsNoRelease);
    }

    [TestMethod]
    public void BelowMinimumVersion_IsDetected()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.5", sha: Sha, minVersion: "0.1.0-alpha.4");
        UpdateManifest manifest = UpdateManifestParser.Parse(json, "alpha").Manifest!;
        var result = new UpdateCheckResult(true, manifest, manifest.SelectAsset(),
            UpdateSourceCatalog.Official("alpha")[0], false, null,
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 1);

        var old = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        old.BeginCheck();
        old.ApplyCheck(result);
        Assert.IsTrue(old.BelowMinimumVersion);

        var current = new UpdateStateMachine("alpha", "0.1.0-alpha.4");
        current.BeginCheck();
        current.ApplyCheck(result);
        Assert.IsFalse(current.BelowMinimumVersion);
    }

    /// <summary>
    /// THE minVersion GATE: below the manifest's minimum the release is still "newer" (the page
    /// and the log say so) but it must not be downloaded and installed over this build. Before
    /// this test existed CanDownload only looked at Available && Asset && IsNewer, so an
    /// incompatible package was copied over the installation.
    /// </summary>
    [TestMethod]
    public void BelowMinimumVersion_BlocksDownload_AndReportsWhy()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.5", sha: Sha, minVersion: "0.1.0-alpha.4");
        UpdateManifest manifest = UpdateManifestParser.Parse(json, "alpha").Manifest!;
        var result = new UpdateCheckResult(true, manifest, manifest.SelectAsset(),
            UpdateSourceCatalog.Official("alpha")[0], false, null,
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 1);

        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(result);

        Assert.IsTrue(machine.IsNewer, "the newer version must still be reported honestly");
        Assert.AreEqual(UpdatePageState.Available, machine.State);
        Assert.IsTrue(machine.RequiresManualInstall);
        Assert.IsFalse(machine.CanDownload, "a build below minVersion must not download and install in place");
        Assert.AreEqual("0.1.0-alpha.4", machine.RequiredMinimumVersion!.ToString());
        StringAssert.Contains(machine.DownloadBlockReason, "0.1.0-alpha.4");

        // The refusal is enforced, not just advertised: BeginDownload cannot leave the state.
        machine.BeginDownload();
        Assert.AreEqual(UpdatePageState.Available, machine.State);
        Assert.IsFalse(machine.IsBusy);
    }

    [TestMethod]
    public void AtOrAboveMinimumVersion_DownloadStaysAllowed()
    {
        string json = TestManifests.Manifest("0.1.0-alpha.5", sha: Sha, minVersion: "0.1.0-alpha.4");
        UpdateManifest manifest = UpdateManifestParser.Parse(json, "alpha").Manifest!;
        var result = new UpdateCheckResult(true, manifest, manifest.SelectAsset(),
            UpdateSourceCatalog.Official("alpha")[0], false, null,
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 1);

        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.4");
        machine.BeginCheck();
        machine.ApplyCheck(result);

        Assert.IsFalse(machine.RequiresManualInstall);
        Assert.IsTrue(machine.CanDownload);
        Assert.IsNull(machine.DownloadBlockReason);
        Assert.IsNull(machine.RequiredMinimumVersion);
    }

    [TestMethod]
    public void NoMinVersion_KeepsTheNormalDownloadPath()
    {
        UpdateManifest manifest = UpdateManifestParser.Parse(
            TestManifests.Manifest("0.1.0-alpha.5", sha: Sha), "alpha").Manifest!;
        var result = new UpdateCheckResult(true, manifest, manifest.SelectAsset(),
            UpdateSourceCatalog.Official("alpha")[0], false, null,
            Array.Empty<UpdateAttemptInfo>(), Array.Empty<UpdateAssetRejection>(), 1);

        var machine = new UpdateStateMachine("alpha", "0.1.0-alpha.1");
        machine.BeginCheck();
        machine.ApplyCheck(result);

        Assert.IsFalse(machine.BelowMinimumVersion, "no declared minimum means no gate");
        Assert.IsTrue(machine.CanDownload);
        Assert.IsNull(machine.DownloadBlockReason);
    }

    [DataTestMethod]
    [DataRow(0L, "0 B")]
    [DataRow(512L, "512 B")]
    [DataRow(1023L, "1023 B")]
    [DataRow(1024L, "1.0 KB")]
    [DataRow(2048L, "2.0 KB")]
    [DataRow(100L * 1024, "100 KB")]
    [DataRow(1023L * 1024, "1023 KB")]
    [DataRow(13002342L, "12.4 MB")]
    // Rounding must carry into the next unit instead of printing a value that is not valid in
    // its own unit: 1024*1024-1 bytes is 1023.999 KB, i.e. "1.0 MB", never "1024 KB".
    [DataRow((1024L * 1024) - 1, "1.0 MB")]
    [DataRow(1048525L, "1.0 MB")]                        // 1023.95 KB
    [DataRow(1024L * 1024, "1.0 MB")]
    [DataRow(1073322394L, "1.0 GB")]                     // 1023.6 MB
    [DataRow(1024L * 1024 * 1024, "1.0 GB")]
    public void UpdateText_FormatsBytes(long bytes, string expected)
    {
        Assert.AreEqual(expected, UpdateText.FormatBytes(bytes));
    }

    [TestMethod]
    public void UpdateText_FormatBytes_NeverPrintsAValueOfOneThousandTwentyFour()
    {
        // Sweep every boundary that can round up into the next unit.
        long[] values =
        {
            1024L * 1024 - 1, (long)(1023.95 * 1024), (long)(1023.5 * 1024),
            1024L * 1024 * 1024 - 1, (long)(1023.95 * 1024 * 1024), 1023L * 1024,
        };
        foreach (long bytes in values)
        {
            string text = UpdateText.FormatBytes(bytes);
            StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex(@"^1024(\.0)? "), text);
        }
        Assert.AreEqual("1.0 MB", UpdateText.FormatBytes(1024L * 1024 - 1));
        Assert.AreEqual("1.0 GB", UpdateText.FormatBytes(1024L * 1024 * 1024 - 1));
    }

    [TestMethod]
    public void UpdateText_FormatsSpeedAndRemaining()
    {
        Assert.AreEqual("0 B/s", UpdateText.FormatSpeed(0));
        Assert.AreEqual("3.1 MB/s", UpdateText.FormatSpeed(3.1 * 1024 * 1024));
        Assert.AreEqual("00:32", UpdateText.FormatRemaining(TimeSpan.FromSeconds(32)));
        Assert.AreEqual("02:05", UpdateText.FormatRemaining(TimeSpan.FromSeconds(125)));
        Assert.AreEqual(string.Empty, UpdateText.FormatRemaining(null));
        Assert.AreEqual(string.Empty, UpdateText.FormatRemaining(TimeSpan.FromSeconds(-1)));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Client: source strategy, download, verification
// ═══════════════════════════════════════════════════════════════════════════════════════

[TestClass]
[DoNotParallelize]
public class UpdateClientTests
{
    private const string Sha = "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static readonly UpdateSource GitHubOfficial = UpdateSourceCatalog.Official("alpha").First(s => s.Id == "github-releases");

    private static string ManifestAssetUrl => "https://github.com/YunxiRamito/Performix/releases/download/v0.1.0-alpha.2/update.json";

    private static UpdateOptions Options(string tempRoot, int maxRetries = 2, TimeSpan? stall = null,
        TimeSpan? group = null, TimeSpan? total = null, string? feed = null)
        => new()
        {
            Channel = "alpha",
            FeedOverride = feed,
            TempRoot = tempRoot,
            MaxRetries = maxRetries,
            StallTimeout = stall ?? TimeSpan.FromSeconds(20),
            GroupBudget = group ?? TimeSpan.FromSeconds(2),
            TotalBudget = total ?? TimeSpan.FromSeconds(4),
        };

    // ── Check: official group first, accelerated as fallback ──────────────────────────

    [TestMethod]
    public void Check_OfficialGroupProducesManifest_AcceleratedIsNotQueried()
    {
        using var temp = new TempFolder();
        string releases = TestManifests.ReleaseList("v0.1.0-alpha.2", "update.json", ManifestAssetUrl);
        using var handler = new FakeHttpHandler()
            .MapJson(GitHubOfficial.Url, releases)
            .MapJson(ManifestAssetUrl, TestManifests.Manifest("0.1.0-alpha.2", sha: Sha));
        using var client = new UpdateClient(Options(temp.Path), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsTrue(result.HasRelease, result.FailureReason);
        Assert.IsFalse(result.NoRelease);
        Assert.AreEqual("github-releases", result.Source!.Id);
        Assert.AreEqual("0.1.0-alpha.2", result.Manifest!.Version.ToString());
        Assert.AreEqual("Performix-win-x64.zip", result.Asset!.Name);
        Assert.IsFalse(handler.Requests.Any(r => r.Url.Contains("gh-proxy")),
            "the accelerated group must not run when the official group produced a manifest");
        Assert.IsTrue(handler.Requests.All(r => r.UserAgent.StartsWith("Performix/", StringComparison.Ordinal)),
            "every request must carry User-Agent: Performix/<version>");
    }

    [TestMethod]
    public void Check_OfficialGroupFails_FallsBackToAcceleratedSource()
    {
        using var temp = new TempFolder();
        string ghProxyUrl = UpdateSourceCatalog.Accelerated("alpha").First(s => s.Id == "gh-proxy").Url;
        using var handler = new FakeHttpHandler()
            .MapJson(ghProxyUrl, TestManifests.Manifest("0.1.0-alpha.4", sha: Sha));
        using var client = new UpdateClient(Options(temp.Path), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsTrue(result.HasRelease, result.FailureReason);
        Assert.AreEqual("gh-proxy", result.Source!.Id);
        Assert.AreEqual("0.1.0-alpha.4", result.Manifest!.Version.ToString());
        Assert.IsTrue(handler.Requests.Any(r => r.Url.Contains("gh-proxy.com")), "the accelerated group was queried");
    }

    [TestMethod]
    public void Check_AllSourcesMissing_ReportsNoReleaseNotAnError()
    {
        using var temp = new TempFolder();
        using var handler = new FakeHttpHandler();
        using var client = new UpdateClient(Options(temp.Path), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsFalse(result.HasRelease);
        Assert.IsTrue(result.NoRelease);
        Assert.AreEqual("No release has been published on the update feed yet.", result.FailureReason);
        Assert.IsNull(result.Manifest);
        Assert.IsTrue(result.Attempts.Count >= 5, "both groups were queried");
        Assert.IsTrue(result.DescribeAttempts().Contains("no release"));
    }

    [TestMethod]
    public void Check_PicksHighestVersionWithinTheOfficialGroup()
    {
        using var temp = new TempFolder();
        string releases = TestManifests.ReleaseList("v0.1.0-alpha.2", "update.json", ManifestAssetUrl);
        string npmUrl = UpdateSourceCatalog.Official("alpha").First(s => s.Id == "npm-official").Url;
        string tarballUrl = "https://registry.npmjs.org/performix/-/performix-0.1.0-alpha.3.tgz";
        byte[] tarball = TestManifests.BuildTarball(("package/update.json", TestManifests.Manifest("0.1.0-alpha.3", sha: Sha)));

        using var handler = new FakeHttpHandler()
            .MapJson(GitHubOfficial.Url, releases)
            .MapJson(ManifestAssetUrl, TestManifests.Manifest("0.1.0-alpha.2", sha: Sha))
            .MapJson(npmUrl, TestManifests.NpmMetadata("0.1.0-alpha.3", tarballUrl))
            .MapBytes(tarballUrl, tarball);
        using var client = new UpdateClient(Options(temp.Path), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsTrue(result.HasRelease, result.FailureReason);
        Assert.AreEqual("0.1.0-alpha.3", result.Manifest!.Version.ToString(), "the highest complete version wins");
        Assert.AreEqual("npm-official", result.Source!.Id, "the manifest came out of the npm tarball");
    }

    [TestMethod]
    public void Check_IncompleteManifest_IsReportedWithTheGateReason()
    {
        using var temp = new TempFolder();
        string releases = TestManifests.ReleaseList("v0.1.0-alpha.2", "update.json", ManifestAssetUrl);
        using var handler = new FakeHttpHandler()
            .MapJson(GitHubOfficial.Url, releases)
            .MapJson(ManifestAssetUrl, TestManifests.Manifest("0.1.0-alpha.2", sha: null));
        using var client = new UpdateClient(Options(temp.Path), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsFalse(result.HasRelease);
        Assert.AreEqual("The update manifest is incomplete: no asset carries both sizeBytes and sha256.",
            result.FailureReason);
        Assert.IsTrue(result.RejectedAssets.Count > 0, "the rejected asset is kept for the log/UI");
    }

    [TestMethod]
    public void Check_OfficialApiUnavailableButOtherSourcesSayMissing_IsStillNoRelease()
    {
        using var temp = new TempFolder();
        // The anonymous GitHub API is rate limited (403) and gh-proxy is unreachable; the npm
        // registry and the committed manifest both answer 404. That is the "nothing published"
        // answer, not an update error — one rate-limited source must not turn it into one.
        using var handler = new FakeHttpHandler()
            .Map(GitHubOfficial.Url, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client = new UpdateClient(Options(temp.Path), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsFalse(result.HasRelease);
        Assert.IsTrue(result.NoRelease, result.FailureReason);
        Assert.AreEqual("No release has been published on the update feed yet.", result.FailureReason);
        StringAssert.Contains(result.DescribeAttempts(), "github-releases");
    }

    [TestMethod]
    public void Check_ChannelMismatchManifest_IsASourceFailure()
    {
        using var temp = new TempFolder();
        string releases = TestManifests.ReleaseList("v0.1.0-alpha.2", "update.json", ManifestAssetUrl);
        using var handler = new FakeHttpHandler()
            .MapJson(GitHubOfficial.Url, releases)
            .MapJson(ManifestAssetUrl, TestManifests.Manifest("0.1.0-alpha.2", sha: Sha, channel: "beta"));
        using var client = new UpdateClient(Options(temp.Path), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsFalse(result.HasRelease);
        Assert.AreEqual("The update manifest is for channel 'beta', not 'alpha'.", result.FailureReason);
        StringAssert.Contains(result.DescribeAttempts(), "channel");
    }

    [TestMethod]
    public void Check_TimedOutOfficialGroup_StillUsesTheAcceleratedGroup()
    {
        using var temp = new TempFolder();
        string ghProxyUrl = UpdateSourceCatalog.Accelerated("alpha").First(s => s.Id == "gh-proxy").Url;
        using var handler = new FakeHttpHandler()
            .MapDelay(GitHubOfficial.Url, TimeSpan.FromSeconds(5))
            .MapJson(ghProxyUrl, TestManifests.Manifest("0.1.0-alpha.6", sha: Sha));
        using var client = new UpdateClient(
            Options(temp.Path, group: TimeSpan.FromMilliseconds(200), total: TimeSpan.FromSeconds(4)), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsTrue(result.HasRelease, result.FailureReason);
        Assert.AreEqual("gh-proxy", result.Source!.Id);
        Assert.IsTrue(result.Attempts.Any(a => a.SourceId == "github-releases" && !a.Succeeded),
            "the timed out official source is recorded, not dropped");
    }

    [TestMethod]
    public void Check_FeedOverride_UsesOnlyTheLocalSource()
    {
        using var temp = new TempFolder();
        string feedPath = Path.Combine(temp.Path, "update.json");
        File.WriteAllText(feedPath, TestManifests.Manifest("0.1.0-alpha.7", sha: Sha));
        using var handler = new FakeHttpHandler();
        using var client = new UpdateClient(Options(temp.Path, feed: feedPath), handler);

        UpdateCheckResult result = client.CheckAsync("alpha").GetAwaiter().GetResult();

        Assert.IsTrue(result.HasRelease, result.FailureReason);
        Assert.AreEqual("override", result.Source!.Id);
        Assert.AreEqual(0, handler.Requests.Count, "an override feed must not touch the network");
    }

    // ── Download ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Download_KnownLength_WritesTheFileAndReportsProgress()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(8192);
        string url = "https://example.test/files/Performix-win-x64.zip";
        var asset = new UpdateAsset("Performix-win-x64.zip", url, payload.Length, TestManifests.Sha256(payload));

        using var handler = new FakeHttpHandler().MapBytes(url, payload);
        using var client = new UpdateClient(Options(temp.Path), handler);
        var samples = new List<UpdateDownloadProgress>();

        UpdateDownloadResult result = client.DownloadAsync(asset, null, new CaptureProgress(samples), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.AreEqual(payload.Length, result.Bytes);
        Assert.AreEqual(url, result.Url);
        Assert.IsTrue(File.Exists(result.FilePath));
        CollectionAssert.AreEqual(payload, File.ReadAllBytes(result.FilePath));
        Assert.IsTrue(samples.Count > 0);
        UpdateDownloadProgress last = samples[^1];
        Assert.AreEqual(payload.Length, last.TotalBytes, "the declared Content-Length is the total");
        Assert.AreEqual(payload.Length, last.ReceivedBytes);
        Assert.AreEqual(100.0, last.Percent, 0.001);
    }

    [TestMethod]
    public void Download_NoContentLength_UsesAnUnknownTotal()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(4096);
        string url = "https://example.test/files/chunked.zip";
        var asset = new UpdateAsset("chunked.zip", url, payload.Length, TestManifests.Sha256(payload));

        using var handler = new FakeHttpHandler().Map(url, (_, _) =>
        {
            var content = new StreamContent(new NonSeekableStream(payload));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            return Task.FromResult(response);
        });
        using var client = new UpdateClient(Options(temp.Path), handler);
        var samples = new List<UpdateDownloadProgress>();

        UpdateDownloadResult result = client.DownloadAsync(asset, null, new CaptureProgress(samples), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.AreEqual(payload.Length, result.Bytes);
        Assert.AreEqual(-1, samples[^1].TotalBytes, "no Content-Length must surface as an unknown total");
        Assert.IsFalse(samples[^1].IsTotalKnown);
    }

    [TestMethod]
    public void Download_InterruptedMidStream_ResumesWithRange()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(8192);
        string url = "https://example.test/files/resume.zip";
        var asset = new UpdateAsset("resume.zip", url, payload.Length, TestManifests.Sha256(payload));
        const int failAfter = 3072;

        using var handler = new FakeHttpHandler().Map(url, (request, _) =>
        {
            long from = 0;
            RangeHeaderValue? range = request.Headers.Range;
            if (range?.Ranges.FirstOrDefault()?.From is long start) from = start;

            if (from == 0)
            {
                var content = new StreamContent(new ThrowingStream(payload, failAfter));
                content.Headers.ContentLength = payload.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            byte[] remaining = payload[(int)from..];
            var resume = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remaining),
            };
            resume.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            return Task.FromResult(resume);
        });
        using var client = new UpdateClient(Options(temp.Path, maxRetries: 2), handler);

        UpdateDownloadResult result = client.DownloadAsync(asset, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        CollectionAssert.AreEqual(payload, File.ReadAllBytes(result.FilePath));
        Assert.AreEqual(2, handler.Requests.Count, "one interrupted attempt plus one resume");
        Assert.IsNull(handler.Requests[0].Range);
        Assert.AreEqual("bytes=" + failAfter + "-", handler.Requests[1].Range);
    }

    [TestMethod]
    public void Download_StalledStream_AbortsWithTheWatchdog()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(4096);
        string url = "https://example.test/files/stall.zip";
        var asset = new UpdateAsset("stall.zip", url, payload.Length, TestManifests.Sha256(payload));

        using var handler = new FakeHttpHandler().Map(url, (_, _) =>
        {
            var content = new StreamContent(new StallStream(payload, 512));
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new UpdateClient(
            Options(temp.Path, maxRetries: 0, stall: TimeSpan.FromMilliseconds(200)), handler);

        UpdateDownloadException error = Assert.ThrowsExactly<UpdateDownloadException>(() =>
            client.DownloadAsync(asset, null, null, CancellationToken.None).GetAwaiter().GetResult());

        StringAssert.Contains(error.Message, "stalled");
    }

    [TestMethod]
    public void Download_FileUrl_CopiesTheLocalPackage()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(2048);
        string sourcePath = Path.Combine(temp.Path, "local-package.zip");
        File.WriteAllBytes(sourcePath, payload);
        var asset = new UpdateAsset("local-package.zip", new Uri(sourcePath).AbsoluteUri, payload.Length,
            TestManifests.Sha256(payload));

        using var client = new UpdateClient(Options(temp.Path), new FakeHttpHandler());
        UpdateDownloadResult result = client.DownloadAsync(asset, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        CollectionAssert.AreEqual(payload, File.ReadAllBytes(result.FilePath));
    }

    [TestMethod]
    public void Download_SecondConcurrentCall_IsRefusedAndDoesNotInterfere()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(4096);
        string url = "https://example.test/files/busy.zip";
        var asset = new UpdateAsset("busy.zip", url, payload.Length, TestManifests.Sha256(payload));

        using var gate = new GatedReadStream(payload);
        using var handler = new FakeHttpHandler().Map(url, (_, _) =>
        {
            var content = new StreamContent(gate);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new UpdateClient(Options(temp.Path), handler);

        Task<UpdateDownloadResult> first = client.DownloadAsync(asset, null, null, CancellationToken.None);
        Assert.IsTrue(gate.WaitUntilReading(TimeSpan.FromSeconds(10)), "the first download reached the body");

        // "换源重试" while a transfer is in flight must not start a second writer on the same
        // .part file. The client refuses it outright; the session cancels the running one.
        UpdateDownloadException error = Assert.ThrowsExactly<UpdateDownloadException>(() =>
            client.DownloadAsync(asset, null, null, CancellationToken.None, rotateSource: true)
                .GetAwaiter().GetResult());
        StringAssert.Contains(error.Message, "already running");
        Assert.AreEqual(1, handler.Requests.Count, "the refused call must not start another transfer");

        gate.Release();
        UpdateDownloadResult result = first.GetAwaiter().GetResult();
        Assert.AreEqual(payload.Length, result.Bytes);
        CollectionAssert.AreEqual(payload, File.ReadAllBytes(result.FilePath));
    }

    // ── Verification (size first, then SHA-256) ──────────────────────────────────────

    [TestMethod]
    public void Verify_CorrectPackage_Passes()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(1024);
        string path = Path.Combine(temp.Path, "good.zip");
        File.WriteAllBytes(path, payload);
        var asset = new UpdateAsset("good.zip", "https://example.test/good.zip", payload.Length, TestManifests.Sha256(payload));

        UpdateVerifyOutcome outcome = UpdateClient.VerifyAsync(path, asset).GetAwaiter().GetResult();

        Assert.IsTrue(outcome.Ok, outcome.Reason);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(TestManifests.Sha256(payload), outcome.Sha256);
    }

    [TestMethod]
    public void Verify_SizeMismatch_FailsAndDeletesTheFile()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(1024);
        string path = Path.Combine(temp.Path, "short.zip");
        File.WriteAllBytes(path, payload[..512]);
        // Resume fragments next to it: a package the verifier refused must not leave anything
        // that a later attempt could append to.
        File.WriteAllBytes(path + ".part", payload[..256]);
        File.WriteAllText(path + ".part.meta", "{\"url\":\"https://example.test/short.zip\"}");
        var asset = new UpdateAsset("short.zip", "https://example.test/short.zip", payload.Length, TestManifests.Sha256(payload));

        UpdateVerifyOutcome outcome = UpdateClient.VerifyAsync(path, asset).GetAwaiter().GetResult();

        Assert.IsFalse(outcome.Ok);
        StringAssert.Contains(outcome.Reason, "size");
        Assert.IsFalse(File.Exists(path), "a package that failed verification must not survive");
        Assert.IsFalse(File.Exists(path + ".part"), "a refused package must not leave a resumable .part");
        Assert.IsFalse(File.Exists(path + ".part.meta"), "the .part sidecar must be dropped with it");
    }

    [TestMethod]
    public void Verify_HashMismatch_FailsAndDeletesTheFileAndItsPartial()
    {
        using var temp = new TempFolder();
        byte[] payload = TestManifests.Payload(1024);
        string path = Path.Combine(temp.Path, "bad.zip");
        File.WriteAllBytes(path, payload);
        File.WriteAllBytes(path + ".part", payload);
        // Right size, wrong hash: exactly the "truncated/replaced package" case the gate exists
        // for. The expected hash is the one of a different payload.
        var asset = new UpdateAsset("bad.zip", "https://example.test/bad.zip", payload.Length,
            TestManifests.Sha256(TestManifests.Payload(1024, seed: 7)));

        UpdateVerifyOutcome outcome = UpdateClient.VerifyAsync(path, asset).GetAwaiter().GetResult();

        Assert.IsFalse(outcome.Ok);
        StringAssert.Contains(outcome.Reason, "SHA-256");
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".part"));
    }

    [TestMethod]
    public void Verify_MissingFile_Fails()
    {
        using var temp = new TempFolder();
        var asset = new UpdateAsset("gone.zip", "https://example.test/gone.zip", 10, Sha);

        UpdateVerifyOutcome outcome = UpdateClient.VerifyAsync(Path.Combine(temp.Path, "gone.zip"), asset)
            .GetAwaiter().GetResult();

        Assert.IsFalse(outcome.Ok);
        StringAssert.Contains(outcome.Reason, "missing");
    }

    // ── Candidates, options ──────────────────────────────────────────────────────────

    [TestMethod]
    public void BuildDownloadCandidates_GitHubAssetGetsProxyPrefixes()
    {
        using var temp = new TempFolder();
        using var client = new UpdateClient(Options(temp.Path), new FakeHttpHandler());
        var asset = new UpdateAsset("Performix-win-x64.zip",
            "https://github.com/YunxiRamito/Performix/releases/download/v0.1.0-alpha.2/Performix-win-x64.zip",
            10, Sha);

        List<string> direct = client.BuildDownloadCandidates(asset, null);
        Assert.AreEqual(3, direct.Count);
        Assert.AreEqual(asset.Url, direct[0]);
        Assert.IsTrue(direct[1].StartsWith("https://gh-proxy.com/", StringComparison.Ordinal));
        Assert.IsTrue(direct[2].StartsWith("https://ghproxy.net/", StringComparison.Ordinal));

        UpdateSource ghProxy = UpdateSourceCatalog.Accelerated("alpha").First(s => s.Id == "gh-proxy");
        List<string> proxied = client.BuildDownloadCandidates(asset, ghProxy);
        Assert.IsTrue(proxied[0].StartsWith("https://gh-proxy.com/", StringComparison.Ordinal),
            "an accelerated manifest downloads through the proxy first");

        var local = new UpdateAsset("local.zip", "http://127.0.0.1:8099/local.zip", 10, Sha);
        Assert.AreEqual(1, client.BuildDownloadCandidates(local, null).Count);
    }

    [TestMethod]
    public void OptionsFromCommandLine_ParsesFeedChannelAndTemp()
    {
        UpdateOptions equalsForm = UpdateOptions.FromCommandLine(
            new[] { "Performix.exe", "--update-feed=C:\\temp\\update.json", "--update-channel=Alpha" });
        Assert.AreEqual("C:\\temp\\update.json", equalsForm.FeedOverride);
        Assert.AreEqual("alpha", equalsForm.Channel);
        Assert.IsTrue(equalsForm.AllowLocalUrls);

        UpdateOptions spaceForm = UpdateOptions.FromCommandLine(
            new[] { "Performix.exe", "--update-feed", "C:\\temp\\update.json", "--update-temp", "C:\\temp\\dl" });
        Assert.AreEqual("C:\\temp\\update.json", spaceForm.FeedOverride);
        Assert.AreEqual("C:\\temp\\dl", spaceForm.TempRoot);

        UpdateOptions defaults = UpdateOptions.FromCommandLine(Array.Empty<string>());
        Assert.AreEqual(UpdateOptions.DefaultChannel, defaults.Channel);
        Assert.IsNull(defaults.FeedOverride);
        Assert.IsFalse(defaults.AllowLocalUrls);
    }

    [TestMethod]
    public void OptionValue_DoesNotSwallowTheFollowingSwitch()
    {
        // "--update-feed --no-autohide" used to yield feed="--no-autohide", i.e. the update
        // source became a nonsense path.
        Assert.IsNull(UpdateOptions.OptionValue(new[] { "--update-feed", "--no-autohide" }, "--update-feed"));
        Assert.IsNull(UpdateOptions.OptionValue(new[] { "--update-feed" }, "--update-feed"));

        // ...but a real value (even one that is not a path) is still read, and an "--name=value"
        // later on the command line still wins.
        Assert.AreEqual("C:\\temp\\update.json",
            UpdateOptions.OptionValue(new[] { "--update-feed", "C:\\temp\\update.json" }, "--update-feed"));
        Assert.AreEqual("https://example.test/update.json",
            UpdateOptions.OptionValue(
                new[] { "--update-feed", "--no-autohide", "--update-feed=https://example.test/update.json" },
                "--update-feed"));

        UpdateOptions options = UpdateOptions.FromCommandLine(new[] { "Performix.exe", "--update-feed", "--no-autohide" });
        Assert.IsNull(options.FeedOverride, "a switch is not a value");
        Assert.IsFalse(options.AllowLocalUrls);
    }

    [DataTestMethod]
    [DataRow("--update-no-install", true)]
    [DataRow("--update-no-install=true", true)]
    [DataRow("--update-no-install=TRUE", true)]
    [DataRow("--update-no-install=false", false)]
    [DataRow("--update-no-install=False", false)]
    [DataRow("--update-no-install=0", false)]
    [DataRow("--update-no-install=maybe", false)]
    [DataRow("--other-switch", false)]
    public void HasFlag_ParsesBooleanValuesInsteadOfTrustingThePrefix(string argument, bool expected)
    {
        Assert.AreEqual(expected, UpdateOptions.HasFlag(new[] { "Performix.exe", argument }, "--update-no-install"),
            argument);
    }

    [TestMethod]
    public void HasFlag_HandlesNullAndMissing()
    {
        Assert.IsFalse(UpdateOptions.HasFlag(null, "--update-no-install"));
        Assert.IsFalse(UpdateOptions.HasFlag(Array.Empty<string>(), "--update-no-install"));
        Assert.IsTrue(UpdateOptions.HasFlag(new[] { "--check-update" }, "--check-update"));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Test doubles and fixtures
// ═══════════════════════════════════════════════════════════════════════════════════════

internal static class TestManifests
{
    public static string Manifest(string version, string channel = "alpha", long sizeBytes = 4096,
        string? sha = null, string assetName = "Performix-win-x64.zip",
        string url = "https://github.com/YunxiRamito/Performix/releases/download/v0.1.0-alpha.2/Performix-win-x64.zip",
        string? publishedAt = "2026-11-14T00:00:00Z", string? minVersion = null, int schema = 1,
        string notes = "first release notes")
    {
        var json = new StringBuilder();
        json.Append('{');
        json.Append("\"schema\":").Append(schema).Append(',');
        json.Append("\"channel\":\"").Append(channel).Append("\",");
        json.Append("\"version\":\"").Append(version).Append("\",");
        if (publishedAt is not null)
            json.Append("\"publishedAt\":\"").Append(publishedAt).Append("\",");
        json.Append("\"notes\":\"").Append(notes).Append("\",");
        if (minVersion is not null)
            json.Append("\"minVersion\":\"").Append(minVersion).Append("\",");
        // The name goes through the JSON encoder: a test row like "..\evil.zip" is a valid
        // candidate asset name, not an invalid JSON escape — the parser must be the thing that
        // rejects it, otherwise the test passes for the wrong reason.
        json.Append("\"assets\":[{\"name\":").Append(System.Text.Json.JsonSerializer.Serialize(assetName)).Append(',');
        json.Append("\"url\":\"").Append(url).Append("\",");
        json.Append("\"sizeBytes\":").Append(sizeBytes);
        if (sha is not null) json.Append(",\"sha256\":\"").Append(sha).Append('"');
        json.Append("}]}");
        return json.ToString();
    }

    public static string ReleaseList(string tag, string assetName, string assetUrl, bool draft = false)
        => """
        [
          {
            "tag_name": "TAG",
            "draft": DRAFT,
            "prerelease": true,
            "assets": [ { "name": "ASSET", "browser_download_url": "URL" } ]
          }
        ]
        """.Replace("TAG", tag).Replace("DRAFT", draft ? "true" : "false")
             .Replace("ASSET", assetName).Replace("URL", assetUrl);

    public static string NpmMetadata(string version, string tarballUrl)
        => """
        { "name": "performix", "versions": { "VER": { "dist": { "tarball": "TARBALL" } } } }
        """.Replace("VER", version).Replace("TARBALL", tarballUrl);

    public static byte[] BuildTarball(params (string Name, string Text)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                };
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    public static byte[] Payload(int length, int seed = 3)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)((i * seed + 17) % 251);
        return bytes;
    }

    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record RecordedRequest(string Url, string? Range, string UserAgent);

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public FakeHttpHandler Map(string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _routes[url] = handler;
        return this;
    }

    public FakeHttpHandler MapJson(string url, string json)
        => Map(url, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    public FakeHttpHandler MapBytes(string url, byte[] bytes)
        => Map(url, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        }));

    public FakeHttpHandler MapDelay(string url, TimeSpan delay)
        => Map(url, async (_, cancellationToken) =>
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.ToString();
        string? range = request.Headers.Range?.ToString();
        string userAgent = request.Headers.UserAgent.ToString();
        lock (_requests) _requests.Add(new RecordedRequest(url, range, userAgent));

        if (_routes.TryGetValue(url, out var handler)) return await handler(request, cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal sealed class CaptureProgress : IProgress<UpdateDownloadProgress>
{
    private readonly List<UpdateDownloadProgress> _samples;

    public CaptureProgress(List<UpdateDownloadProgress> samples) => _samples = samples;

    public void Report(UpdateDownloadProgress value)
    {
        lock (_samples) _samples.Add(value);
    }
}

/// <summary>Reads <c>failAfter</c> bytes, then behaves like a reset connection.</summary>
internal sealed class ThrowingStream : Stream
{
    private readonly byte[] _data;
    private readonly int _failAfter;
    private int _position;

    public ThrowingStream(byte[] data, int failAfter)
    {
        _data = data;
        _failAfter = failAfter;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _failAfter) throw new IOException("simulated connection reset");
        int count = Math.Min(Math.Min(buffer.Length, _failAfter - _position), _data.Length - _position);
        _data.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;
        return new ValueTask<int>(count);
    }
}

/// <summary>Delivers a prefix, then never produces another byte (watchdog test).</summary>
internal sealed class StallStream : Stream
{
    private readonly byte[] _data;
    private readonly int _stallAfter;
    private int _position;

    public StallStream(byte[] data, int stallAfter)
    {
        _data = data;
        _stallAfter = stallAfter;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _stallAfter)
        {
            // No new bytes: only the caller's stall timer ends this.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return 0;
        }

        int count = Math.Min(Math.Min(buffer.Length, _stallAfter - _position), _data.Length - _position);
        _data.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;
        return count;
    }
}

/// <summary>Delivers nothing until released, so a download can be held "in flight" by a test.</summary>
internal sealed class GatedReadStream : Stream
{
    private readonly byte[] _data;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    public GatedReadStream(byte[] data) => _data = data;

    /// <summary>True once the downloader has asked for the first body bytes.</summary>
    public bool WaitUntilReading(TimeSpan timeout) => _reading.Task.Wait(timeout);

    public void Release() => _release.TrySetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _reading.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        if (_position >= _data.Length) return 0;
        int count = Math.Min(buffer.Length, _data.Length - _position);
        _data.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;
        return count;
    }
}

/// <summary>A stream with no length, so StreamContent sends no Content-Length.</summary>
internal sealed class NonSeekableStream : Stream
{
    private readonly byte[] _data;
    private int _position;

    public NonSeekableStream(byte[] data) => _data = data;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int remaining = _data.Length - _position;
        if (remaining <= 0) return 0;
        int copied = Math.Min(count, remaining);
        Array.Copy(_data, _position, buffer, offset, copied);
        _position += copied;
        return copied;
    }
}

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PerformixTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* the OS cleans %TEMP% */ }
    }
}
