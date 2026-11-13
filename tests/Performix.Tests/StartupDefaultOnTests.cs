using System.Text.RegularExpressions;
using PreySense.Core.Configuration;
using PreySense.Frontend.Services;
using static PreySense.Frontend.Services.StartupTaskService;

namespace PreySense.Tests;

/// <summary>
/// "Run on Startup is on by default" policy: a user who never touched the switch gets the
/// logon task registered once, a saved answer is final, and a failed registration only
/// reports "off" (it never throws or blocks startup).
/// </summary>
[TestClass]
public class StartupDefaultOnTests
{
    private const string User = "S-1-5-21-1-2-3-1001";
    private static readonly System.Xml.Linq.XNamespace Ns =
        "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private string _directory = null!;
    private string _exe = null!;
    private Scheduler _fake = null!;
    private bool? _declared;
    private readonly List<bool> _writes = [];

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PerformixStartupDefault-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _exe = Path.Combine(_directory, "Performix.exe");
        File.WriteAllText(_exe, "test fixture; never executed");
        _fake = new Scheduler();
        _declared = null;
        _writes.Clear();
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public void NeverAnswered_FirstRunRegistersTheTaskAndRemembersTheAnswer()
    {
        Assert.IsTrue(Create().ResolveStartupPreference());
        Assert.AreEqual(1, _fake.Creates, "A user who never touched the switch gets the logon task.");
        CollectionAssert.AreEqual(new[] { true }, _writes, "The automatic answer is persisted.");
        Assert.AreEqual(true, _declared);
        // Second launch must not create it again.
        Assert.IsTrue(Create().ResolveStartupPreference());
        Assert.AreEqual(1, _fake.Creates);
    }

    [TestMethod]
    public void ExplicitOff_IsNeverOverriddenOnLaterLaunches()
    {
        _declared = false;
        Assert.IsFalse(Create().ResolveStartupPreference());
        Assert.AreEqual(0, _fake.Creates, "A saved 'off' must never be turned back on.");
        Assert.IsEmpty(_writes);
        Assert.IsFalse(Create().ResolveStartupPreference());
        Assert.AreEqual(0, _fake.Creates);
    }

    [TestMethod]
    public void ExplicitOff_DoesNotDeleteAndReportsTheRealState()
    {
        _declared = false;
        _fake.Xml = BuildLogonTaskXml(_exe, User);
        Assert.IsTrue(Create().ResolveStartupPreference(), "The switch reflects what Windows actually has.");
        Assert.AreEqual(0, _fake.Deletes);
        Assert.AreEqual(0, _fake.Creates);
    }

    [TestMethod]
    public void ExplicitOn_RecreatesATaskThatWentMissing()
    {
        _declared = true;
        Assert.IsTrue(Create().ResolveStartupPreference());
        Assert.AreEqual(1, _fake.Creates);
        Assert.IsEmpty(_writes, "An existing answer is not rewritten.");
    }

    [TestMethod]
    public void NeverAnswered_WithAHealthyTask_KeepsItAndRecordsTheAnswer()
    {
        _fake.Xml = BuildLogonTaskXml(_exe, User);
        Assert.IsTrue(Create().ResolveStartupPreference());
        Assert.AreEqual(0, _fake.Creates);
        CollectionAssert.AreEqual(new[] { true }, _writes);
    }

    [TestMethod]
    public void NeverAnswered_WithATaskDisabledByItsOwner_IsLeftOff()
    {
        var doc = System.Xml.Linq.XDocument.Parse(BuildLogonTaskXml(_exe, User));
        var settings = doc.Descendants(Ns + "Settings").Single();
        settings.Element(Ns + "Enabled")!.Value = "false";
        _fake.Xml = doc.ToString();
        Assert.IsFalse(Create().ResolveStartupPreference());
        Assert.AreEqual(0, _fake.Creates, "A task the owner disabled is not re-enabled.");
    }

    [TestMethod]
    public void RegistrationFailure_ReportsOffWithoutThrowingOrPersistingAnAnswer()
    {
        _fake.CorruptReadback = true;
        bool enabled = true;
        try { enabled = Create().ResolveStartupPreference(); }
        catch (Exception ex) { Assert.Fail($"Registration must never throw into startup: {ex}"); }
        Assert.IsFalse(enabled, "A failed registration must show as 'off', not as enabled.");
        Assert.AreEqual(1, _fake.Creates);
        Assert.IsEmpty(_writes, "No answer is stored, so the next launch tries again.");
        Assert.IsNull(_declared);
    }

    [TestMethod]
    public void UnreadablePreferenceStore_DoesNotBreakStartupOrBlockRegistration()
    {
        var registration = new StartupTaskRegistration(_fake.Run, () => _exe, () => User,
            () => throw new InvalidOperationException("settings unavailable"), _ => throw new InvalidOperationException("disk full"));
        Assert.IsTrue(registration.ResolveStartupPreference(), "An unreadable store falls back to the first-run default.");
        Assert.AreEqual(1, _fake.Creates);
    }

    [TestMethod]
    public void TurningTheSwitchOff_DeletesTheTaskAndRecordsTheChoice()
    {
        _fake.Xml = BuildLogonTaskXml(_exe, User);
        var registration = Create();
        Assert.IsTrue(registration.SetEnabled(false));
        registration.RecordUserChoice(false);
        Assert.AreEqual(1, _fake.Deletes);
        Assert.IsNull(_fake.Xml);
        Assert.AreEqual(false, _declared);
        Assert.IsFalse(Create().ResolveStartupPreference(), "The task stays deleted after the user said off.");
    }

    [TestMethod]
    public void FailedTaskOperation_StillRecordsWhatTheUserAsked()
    {
        _fake.DeleteFails = true;
        var registration = Create();
        Assert.IsFalse(registration.SetEnabled(false));
        registration.RecordUserChoice(false);
        Assert.AreEqual(false, _declared, "Intent wins even when the delete failed, so we never re-enable later.");
        Assert.IsFalse(Create().ResolveStartupPreference());
        Assert.AreEqual(0, _fake.Creates);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public void ShouldRegister_FollowsTheSavedAnswer(bool? declared, bool expected)
        => Assert.AreEqual(expected, StartupTaskRegistration.ShouldRegister(declared));

    [TestMethod]
    public void GeneralRunOnStartup_StartsUndeclaredAndKeepsAnExplicitOff()
    {
        string path = Path.Combine(_directory, "preysense.json");
        var store = new ConfigurationService(path);
        Assert.IsNull(store.GetCurrent().General.RunOnStartup, "A fresh profile has no answer yet.");
        store.Update(cfg => cfg.General.RunOnStartup = false);
        Assert.AreEqual(false, new ConfigurationService(path).GetCurrent().General.RunOnStartup);
        string json = File.ReadAllText(path);
        Assert.Contains("\"RunOnStartup\": false", json);
    }

    private StartupTaskRegistration Create() =>
        new(_fake.Run, () => _exe, () => User, () => _declared, Write);

    private void Write(bool enable)
    {
        _writes.Add(enable);
        _declared = enable;
    }

    private sealed class Scheduler
    {
        public string? Xml;
        public int Creates, Deletes;
        public bool CorruptReadback;
        public bool DeleteFails;

        public SchtasksResult Run(string args)
        {
            if (args.StartsWith("/Query"))
                return Xml is null ? new(1, "Task not found") : new(0, Xml);
            if (args.StartsWith("/Delete"))
            {
                Deletes++;
                if (DeleteFails) return new(1, "Access denied");
                Xml = null;
                return new(0, "Deleted");
            }
            if (args.StartsWith("/Create"))
            {
                Creates++;
                string path = Regex.Match(args, "/XML \"([^\"]+)\"").Groups[1].Value;
                Xml = CorruptReadback ? "<broken>" : File.ReadAllText(path);
                return new(0, "Created");
            }
            throw new InvalidOperationException(args);
        }
    }
}
