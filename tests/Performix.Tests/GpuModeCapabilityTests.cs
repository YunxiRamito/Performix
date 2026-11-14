using PreySense.Core.Services;
using PreySense.Frontend.ViewModels;
using PreySense.Gpu;

namespace PreySense.Tests;

/// <summary>
/// The three-mode GPU panel: which of Hybrid / Integrated / dGPU Only a machine can actually
/// enter, and how the mode it is in right now is identified.
///
/// The decision is a pure function (GpuModes.Evaluate / .Identify) so these cases do not need
/// hardware. <see cref="ReferenceMachine"/> is the measured PH317-55 this work was done on:
///   Acer WMI GetGamingMiscSetting(0x02) = 0xFF00 → high byte 0xFF → no MUX
///   NvCplApiGetInfo(0x17) → supported 0, muxState 0 → DDS cannot switch
///   nvidia-smi display_attached=No / display_active=Disabled → the iGPU drives the panel
/// Exactly one mode is selectable there, and it is Hybrid.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GpuModeCapabilityTests
{
    private static readonly GpuModeProbe ReferenceMachine = new(
        HasHardwareMux: false,
        DdsAvailable: false,
        DdsSupported: false,
        DisplayDrivenByIgpu: true,
        BiosDiscreteGpuOnly: false,
        IgpuOnlyPathExposed: false);

    [TestMethod]
    public void ReferenceMachineOffersOnlyHybrid()
    {
        GpuModeCapabilities caps = GpuModes.Evaluate(ReferenceMachine);

        Assert.IsTrue(caps.Hybrid.IsAvailable, "an iGPU-driven panel is a hybrid wiring, MUX or not");
        Assert.AreEqual(GpuModeBlocker.None, caps.Hybrid.Blocker);

        Assert.IsFalse(caps.Discrete.IsAvailable, "no MUX, so the panel cannot be wired to the dGPU");
        Assert.AreEqual(GpuModeBlocker.NoHardwareMux, caps.Discrete.Blocker);

        Assert.IsFalse(caps.Integrated.IsAvailable, "no API exposes iGPU-only on this build");
        Assert.AreEqual(GpuModeBlocker.NoIgpuOnlyPath, caps.Integrated.Blocker);

        Assert.AreEqual(1, new[] { caps.Hybrid, caps.Integrated, caps.Discrete }.Count(m => m.IsAvailable));
    }

    [TestMethod]
    public void ReferenceMachineIsIdentifiedAsHybridFromThePanelRoute()
    {
        GpuModeIdentification identification = GpuModes.Identify(ReferenceMachine, dds: null);

        Assert.AreEqual(GpuModeKind.Hybrid, identification.Mode);
        Assert.AreEqual(GpuModeBasis.IgpuRoute, identification.Basis);
    }

    [TestMethod]
    public void UnreadablePanelRouteAndNoDdsLeavesTheModeUnknown()
    {
        var nothingReadable = ReferenceMachine with { DisplayDrivenByIgpu = false };

        GpuModeIdentification identification = GpuModes.Identify(nothingReadable, dds: null);

        Assert.IsNull(identification.Mode, "only a total absence of evidence may read N/A");
        Assert.AreEqual(GpuModeBasis.Unknown, identification.Basis);
        Assert.AreEqual("N/A", GpuModes.SourceName(identification.Mode));
    }

    [TestMethod]
    public void HardwareMuxMakesDgpuDirectSelectable()
    {
        var withMux = ReferenceMachine with { HasHardwareMux = true, DisplayDrivenByIgpu = false };

        GpuModeCapabilities caps = GpuModes.Evaluate(withMux);

        Assert.IsTrue(caps.Discrete.IsAvailable);
        Assert.AreEqual(GpuModeBlocker.None, caps.Discrete.Blocker);
        // Without DDS and without an iGPU-driven panel there is still no hybrid path.
        Assert.IsFalse(caps.Hybrid.IsAvailable);
        Assert.AreEqual(GpuModeBlocker.NoHybridPath, caps.Hybrid.Blocker);
    }

    [TestMethod]
    public void DdsWithoutAMuxStillAllowsHybridSwitching()
    {
        var advancedOptimus = new GpuModeProbe(
            HasHardwareMux: false, DdsAvailable: true, DdsSupported: true,
            DisplayDrivenByIgpu: false, BiosDiscreteGpuOnly: false, IgpuOnlyPathExposed: false);

        GpuModeCapabilities caps = GpuModes.Evaluate(advancedOptimus);

        Assert.IsTrue(caps.Hybrid.IsAvailable, "DDS can switch, so Hybrid is selectable");
        Assert.IsFalse(caps.Discrete.IsAvailable, "Advanced Optimus without an Acer MUX is not dGPU-direct");
        Assert.IsFalse(caps.Integrated.IsAvailable);
    }

    [TestMethod]
    public void BiosDiscreteOnlyDisablesHybridAndIntegrated()
    {
        var biosDiscrete = ReferenceMachine with { BiosDiscreteGpuOnly = true, DisplayDrivenByIgpu = false };

        GpuModeCapabilities caps = GpuModes.Evaluate(biosDiscrete);

        Assert.IsFalse(caps.Hybrid.IsAvailable);
        Assert.AreEqual(GpuModeBlocker.BiosDiscreteOnly, caps.Hybrid.Blocker);
        Assert.IsFalse(caps.Integrated.IsAvailable);
        Assert.AreEqual(GpuModeBlocker.BiosDiscreteOnly, caps.Integrated.Blocker);
        // Already in dGPU-only: the firmware plainly supports it, so the tile is not greyed out
        // while it is the selected one.
        Assert.IsTrue(caps.Discrete.IsAvailable);
        Assert.AreEqual(GpuModeKind.Discrete, GpuModes.Identify(biosDiscrete, dds: null).Mode);
    }

    [TestMethod]
    public void IntegratedModeIsNeverOfferedWithoutAnExposedPath()
    {
        // Every combination of the other five facts must leave Integrated unavailable, because no
        // probe in this build can put the machine into iGPU-only.
        foreach (bool mux in new[] { false, true })
        foreach (bool dds in new[] { false, true })
        foreach (bool igpu in new[] { false, true })
        foreach (bool biosDiscrete in new[] { false, true })
        {
            var probe = new GpuModeProbe(mux, dds, dds, igpu, biosDiscrete, IgpuOnlyPathExposed: false);
            GpuModeAvailability integrated = GpuModes.Evaluate(probe).Integrated;
            Assert.IsFalse(integrated.IsAvailable, $"mux={mux} dds={dds} igpu={igpu} bios={biosDiscrete}");
            Assert.AreEqual(
                biosDiscrete ? GpuModeBlocker.BiosDiscreteOnly : GpuModeBlocker.NoIgpuOnlyPath,
                integrated.Blocker);
        }

        // The seam exists so a future probe can flip it without touching the decision.
        var exposed = ReferenceMachine with { IgpuOnlyPathExposed = true };
        Assert.IsTrue(GpuModes.Evaluate(exposed).Integrated.IsAvailable);
    }

    [TestMethod]
    [DataRow(true, 0, GpuModeKind.Hybrid, GpuModeBasis.DdsAutomatic)]
    [DataRow(false, 1, GpuModeKind.Discrete, GpuModeBasis.DdsDgpu)]
    [DataRow(false, 2, GpuModeKind.Hybrid, GpuModeBasis.DdsOptimus)]
    public void DdsRoutesIdentifyHybridAndDiscrete(bool automatic, int muxState, GpuModeKind expected, string basis)
    {
        var dds = new DdsStatus(
            IsSupported: true,
            IsAutomatic: automatic,
            ActiveMux: muxState switch
            {
                1 => DdsActiveMux.dGPU,
                2 => DdsActiveMux.Optimus,
                _ => DdsActiveMux.Unknown
            },
            KmdResponse: 0,
            SrType: 0);

        // The panel route is deliberately the opposite of the driver's answer: DDS wins.
        var probe = ReferenceMachine with { DdsAvailable = true, DdsSupported = true, DisplayDrivenByIgpu = !automatic };

        GpuModeIdentification identification = GpuModes.Identify(probe, dds);

        Assert.AreEqual(expected, identification.Mode);
        Assert.AreEqual(basis, identification.Basis);
    }

    [TestMethod]
    public void FailedOrUnsupportedDdsReadingIsNotTreatedAsEvidence()
    {
        var probe = ReferenceMachine with { DdsAvailable = true, DdsSupported = true, DisplayDrivenByIgpu = false };
        var failed = new DdsStatus(IsSupported: true, IsAutomatic: false, ActiveMux: DdsActiveMux.dGPU, KmdResponse: 7, SrType: 0);
        var unsupported = failed with { IsSupported = false, KmdResponse = 0 };

        Assert.IsNull(GpuModes.Identify(probe, failed).Mode);
        Assert.IsNull(GpuModes.Identify(probe, unsupported).Mode);
    }

    // ── ViewModel wiring: the reference machine's panel state, end to end ────────

    /// <summary>A mock chassis that behaves like the measured PH317-55.</summary>
    private sealed class ReferenceChassis : MockHardwareService
    {
        public ReferenceChassis()
        {
            HasFirmwareMux = false;   // Acer GetGamingMiscSetting(0x02) high byte 0xFF
            IsDdsAvailable = false;   // no usable NvCplApi
            DdsSupported = false;     // NvCplApiGetInfo(0x17) → supported 0
            IsDisplayDrivenByIgpu = true;
        }
    }

    [TestMethod]
    public void ReferenceMachineViewModelDisablesTheModesItCannotEnter()
    {
        var vm = new MainViewModel(new ReferenceChassis());

        Assert.IsTrue(vm.IsGpuHybridModeAvailable);
        Assert.IsFalse(vm.IsGpuDiscreteModeAvailable);
        Assert.IsFalse(vm.IsGpuIntegratedModeAvailable);

        // Current mode is identified, not "N/A".
        Assert.AreEqual("Hybrid", vm.GpuMuxModeName);
        Assert.IsTrue(vm.IsGpuHybridModeActive);
        Assert.IsFalse(vm.IsGpuDiscreteModeActive);
        Assert.IsFalse(vm.IsGpuIntegratedModeActive);
        Assert.AreEqual(GpuModeBasis.IgpuRoute, vm.GpuModeBasisToken);

        // The reason a disabled tile shows is the same sentence the command reports.
        StringAssert.Contains(vm.GpuDiscreteModeToolTip, "MUX");
        StringAssert.Contains(vm.GpuIntegratedModeToolTip, "integrated-GPU-only");
    }

    [TestMethod]
    public async System.Threading.Tasks.Task UnsupportedModesAnswerInsteadOfDoingNothing()
    {
        var vm = new MainViewModel(new ReferenceChassis());

        await vm.SetBiosDiscreteGpuAsync();
        StringAssert.Contains(vm.DiagnosticMessage, "MUX", "pressing dGPU Only on a MUX-less machine must explain itself");

        vm.SetIntegratedGpuMode();
        StringAssert.Contains(vm.DiagnosticMessage, "not supported in this version");
    }
}
