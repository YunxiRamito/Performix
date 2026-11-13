// Regression tests for the cached-page re-subscription bug (fans page RPM froze after the
// page was returned to and re-entered).
//
// The shell caches page instances (Shell\ShellWindow.xaml.cs `_pages`), so Loaded / Unloaded
// fire repeatedly on one object while IPredexPage.Vm is assigned exactly once. The page used
// to subscribe only inside the Vm setter, and the setter returns early when the value is
// unchanged: the second "Vm ??= ..." in OnLoaded therefore re-subscribed nothing and the page
// stopped receiving CpuFanText / GpuFanText changes.
//
// The subscription bookkeeping now lives in Predex.App's PropertyChangedSubscription, which is
// linked into this test project (no WinUI dependency), so the exact attach/detach counts the
// page performs are asserted here instead of needing a UI host.

using System.ComponentModel;
using Predex.WinUI.Services;

namespace PreySense.Tests;

[TestClass]
public sealed class PropertyChangedSubscriptionTests
{
    private sealed class FakeSource : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public int SubscriberCount => PropertyChanged?.GetInvocationList().Length ?? 0;

        public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class FakePage
    {
        public int Notifications;

        public void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => Notifications++;
    }

    [TestMethod]
    public void Attach_ThenDetach_LeavesNoSubscriber()
    {
        var source = new FakeSource();
        var page = new FakePage();
        var subscription = new PropertyChangedSubscription();

        subscription.Attach(source, page.OnPropertyChanged);
        Assert.AreEqual(1, source.SubscriberCount);
        Assert.IsTrue(subscription.IsAttached);

        subscription.Detach();
        Assert.AreEqual(0, source.SubscriberCount);
        Assert.IsFalse(subscription.IsAttached);
    }

    [TestMethod]
    public void Detach_WithoutAttach_IsNoOp()
    {
        var subscription = new PropertyChangedSubscription();

        subscription.Detach();
        subscription.Detach();

        Assert.AreEqual(0, subscription.AttachCount);
        Assert.AreEqual(0, subscription.DetachCount);
        Assert.IsFalse(subscription.IsAttached);
    }

    /// <summary>
    /// The bug itself: Loaded → Unloaded → Loaded on the same page. After the second Loaded the
    /// page must receive one event per VM raise (not zero, not two).
    /// </summary>
    [TestMethod]
    public void Reattach_AfterUnload_RestoresExactlyOneSubscription()
    {
        var source = new FakeSource();
        var page = new FakePage();
        var subscription = new PropertyChangedSubscription();

        // First entry: shell assigned Vm (setter) and the page attached on Loaded.
        subscription.Attach(source, page.OnPropertyChanged);
        subscription.Attach(source, page.OnPropertyChanged);   // OnLoaded attaches unconditionally
        Assert.AreEqual(1, source.SubscriberCount, "attach must not accumulate handlers");

        // Back to another page.
        subscription.Detach();
        Assert.AreEqual(0, source.SubscriberCount);

        // Re-entry: the Vm setter is skipped (same instance), OnLoaded re-attaches.
        subscription.Attach(source, page.OnPropertyChanged);
        Assert.AreEqual(1, source.SubscriberCount, "re-entry must re-subscribe exactly once");

        source.Raise("CpuFanText");

        Assert.AreEqual(1, page.Notifications);
        Assert.AreEqual(3, subscription.AttachCount);
        Assert.AreEqual(2, subscription.DetachCount);   // one per replaced/dropped subscription
    }

    [TestMethod]
    public void Attach_ToNewSource_MovesTheSubscription()
    {
        var first = new FakeSource();
        var second = new FakeSource();
        var page = new FakePage();
        var subscription = new PropertyChangedSubscription();

        subscription.Attach(first, page.OnPropertyChanged);
        subscription.Attach(second, page.OnPropertyChanged);

        Assert.AreEqual(0, first.SubscriberCount);
        Assert.AreEqual(1, second.SubscriberCount);
        Assert.AreSame(second, subscription.Source);

        first.Raise("CpuFanText");
        Assert.AreEqual(0, page.Notifications);

        second.Raise("CpuFanText");
        Assert.AreEqual(1, page.Notifications);
    }

    [TestMethod]
    public void Attach_NullSource_DetachesOnly()
    {
        var source = new FakeSource();
        var page = new FakePage();
        var subscription = new PropertyChangedSubscription();

        subscription.Attach(source, page.OnPropertyChanged);
        subscription.Attach(null, page.OnPropertyChanged);

        Assert.AreEqual(0, source.SubscriberCount);
        Assert.IsFalse(subscription.IsAttached);
        Assert.AreEqual(1, subscription.AttachCount);   // only the first call registered
        Assert.AreEqual(1, subscription.DetachCount);
    }

    /// <summary>
    /// Detach must remove the handler that was attached, not whatever method group is passed
    /// later — the page passes a fresh delegate on every call.
    /// </summary>
    [TestMethod]
    public void Detach_RemovesTheAttachedDelegate()
    {
        var source = new FakeSource();
        var first = new FakePage();
        var second = new FakePage();
        var subscription = new PropertyChangedSubscription();

        subscription.Attach(source, first.OnPropertyChanged);
        subscription.Attach(source, second.OnPropertyChanged);

        Assert.AreEqual(1, source.SubscriberCount);

        source.Raise("GpuFanText");

        Assert.AreEqual(0, first.Notifications, "the replaced delegate must be gone");
        Assert.AreEqual(1, second.Notifications);

        subscription.Detach();
        source.Raise("GpuFanText");

        Assert.AreEqual(0, source.SubscriberCount);
        Assert.AreEqual(1, second.Notifications);
    }
}
