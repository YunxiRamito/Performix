using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PreySense.Core.Services;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;

namespace PreySense.Tests
{
    /// <summary>
    /// Guards the ViewModel → IUserPromptService seam added for the WinUI 3 migration
    /// (docs/WINUI3-MIGRATION.md §7.4). The GPU paths used to build CustomModalDialog directly,
    /// which cannot work in a single-window WinUI shell and cannot run in a unit test.
    /// </summary>
    [TestClass]
    public class UserPromptServiceTests
    {
        private sealed class RecordingPromptService : IUserPromptService
        {
            public bool ConfirmResult { get; set; }
            public int ConfirmCount { get; private set; }
            public int NotifyCount { get; private set; }
            public (string Title, string Subtitle, string Message, string ConfirmText, string CancelText)? LastConfirm { get; private set; }
            public (string Title, string Subtitle, string Message, string ConfirmText)? LastNotify { get; private set; }

            public bool Confirm(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
            {
                ConfirmCount++;
                LastConfirm = (title, subtitle, message, confirmText, cancelText);
                return ConfirmResult;
            }

            public Task<bool> ConfirmAsync(string title, string subtitle, string message, string confirmText, string cancelText = "Cancel")
                => Task.FromResult(Confirm(title, subtitle, message, confirmText, cancelText));

            public void Notify(string title, string subtitle, string message, string confirmText)
            {
                NotifyCount++;
                LastNotify = (title, subtitle, message, confirmText);
            }

            public Task NotifyAsync(string title, string subtitle, string message, string confirmText)
            {
                Notify(title, subtitle, message, confirmText);
                return Task.CompletedTask;
            }
        }

        [TestMethod]
        public async Task GpuRestartConfirmation_GoesThroughInjectedService()
        {
            var hw = new MockHardwareService();
            var prompts = new RecordingPromptService { ConfirmResult = false };
            var vm = new MainViewModel(hw) { UserPrompts = prompts };

            await vm.SetBiosDiscreteGpuAsync();

            Assert.AreEqual(1, prompts.ConfirmCount, "The confirmation must come from the injected service.");
            Assert.AreEqual(0, prompts.NotifyCount);
            var prompt = prompts.LastConfirm!.Value;
            Assert.AreEqual("Performix • Hardware Discrete GPU", prompt.Title);
            Assert.AreEqual(" • Hardware Display MUX", prompt.Subtitle);
            Assert.AreEqual("Would you like to switch to dGPU Only mode and restart now?", prompt.Message);
            Assert.AreEqual("Apply & Restart Now", prompt.ConfirmText);
            Assert.AreEqual("Cancel", prompt.CancelText);
            Assert.AreEqual(0, hw.SetGpuMuxModeCallCount, "Declining the prompt must not touch the firmware MUX.");
        }

        [TestMethod]
        public async Task GpuRestartConfirmation_WithoutInjection_IsHeadlessAndDeclines()
        {
            var hw = new MockHardwareService();
            // No UserPrompts: the headless default must answer "no" instead of building a window.
            var vm = new MainViewModel(hw);

            await vm.SetBiosDiscreteGpuAsync();

            Assert.AreEqual(0, hw.SetGpuMuxModeCallCount);
        }

        [TestMethod]
        public async Task UnattendedPromptService_NeverBlocksOnAWindow()
        {
            IUserPromptService prompts = UnattendedUserPromptService.Instance;

            Assert.IsFalse(prompts.Confirm("t", "s", "m", "ok", "cancel"));
            Assert.IsFalse(await prompts.ConfirmAsync("t", "s", "m", "ok", "cancel"));
            prompts.Notify("t", "s", "m", "ok");
            await prompts.NotifyAsync("t", "s", "m", "ok");
        }

        [TestMethod]
        public void GpuViewModel_NoLongerBuildsWindowsItself()
        {
            // Same source-scan style as Test_GpuMux_DoesNotFallBackToAcerWmi: the whole point of the
            // seam is that ViewModels stop referencing Application.Current / CustomModalDialog.
            string root = AppContext.BaseDirectory;
            DirectoryInfo? dir = new(root);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Performix.slnx")))
                dir = dir.Parent;
            Assert.IsNotNull(dir);
            string gpu = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Performix.Legacy.Wpf", "ViewModels", "MainViewModel.Gpu.cs"));

            Assert.DoesNotContain("CustomModalDialog", gpu);
            Assert.DoesNotContain("ShowDialog", gpu);
            Assert.Contains("Prompts.ConfirmAsync(", gpu);
            Assert.Contains("Prompts.NotifyAsync(", gpu);
        }
    }
}
