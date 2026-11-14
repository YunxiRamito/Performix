using System;

namespace PreySense.Gpu
{
    /// <summary>
    /// The three GPU modes the home panel offers. There used to be four tiles
    /// (Auto / Hybrid / Advanced Optimus / dGPU Only); two of those were the same hardware
    /// state under different NVIDIA marketing names, so the panel now asks the only question
    /// a user can act on: which GPU drives the panel.
    /// </summary>
    public enum GpuModeKind
    {
        /// <summary>iGPU drives the panel, dGPU renders on demand (DDS Optimus / Auto, or plain Optimus).</summary>
        Hybrid = 0,

        /// <summary>iGPU only, dGPU off. No API in this build exposes this state — see <see cref="GpuModes"/>.</summary>
        Integrated = 1,

        /// <summary>The panel is wired straight to the dGPU (hardware MUX, or a BIOS dGPU-only boot).</summary>
        Discrete = 2
    }

    /// <summary>Why a mode cannot be selected on this machine. <see cref="None"/> = selectable.</summary>
    public enum GpuModeBlocker
    {
        None = 0,

        /// <summary>No hardware MUX: the Acer WMI mux reports 0xFF and GetGpuMuxMode() is null.</summary>
        NoHardwareMux,

        /// <summary>No API exposes an integrated-GPU-only state on this machine.</summary>
        NoIgpuOnlyPath,

        /// <summary>Neither DDS nor an iGPU-driven panel: nothing says a hybrid path exists.</summary>
        NoHybridPath,

        /// <summary>The machine booted straight into dGPU-only, so there is no iGPU to drive the panel.</summary>
        BiosDiscreteOnly
    }

    /// <summary>
    /// The raw facts the three-mode decision needs. Every field is something a probe actually
    /// reads from the machine; nothing here is inferred. The decision itself lives in
    /// <see cref="GpuModes"/>, which is a pure function of these six booleans, so the cases that
    /// matter are unit tests instead of a screenshot nobody re-reads.
    ///
    /// Reference machine (Acer Predator PH317-55, measured 2026-11):
    ///   Acer WMI GetGamingMiscSetting(0x02) = 0xFF00  → high byte 0xFF → no MUX → HasHardwareMux = false
    ///   NvCplApiGetInfo(0x17) = supported 0, muxState 0              → DdsSupported  = false
    ///   nvidia-smi display_attached=No, display_active=Disabled       → DisplayDrivenByIgpu = true
    ///   (i.e. the Intel UHD has a live display mode, the RTX 3070 has none)
    /// That combination offers exactly one mode: Hybrid. See GpuModeCapabilityTests.
    /// </summary>
    /// <param name="HasHardwareMux">A hardware display MUX exists (GetGpuMuxMode() is not null).</param>
    /// <param name="DdsAvailable">The NVIDIA DDS API surface loaded at all (nvcpl.dll + NvCplApiInit).</param>
    /// <param name="DdsSupported">DDS reports IsSupported (NvCplApiGetInfo(0x17) succeeded and said so).</param>
    /// <param name="DisplayDrivenByIgpu">The integrated GPU is the one with a live display mode.</param>
    /// <param name="BiosDiscreteGpuOnly">Windows reports the iGPU offline: a BIOS dGPU-only boot.</param>
    /// <param name="IgpuOnlyPathExposed">Some API can actually put the machine into iGPU-only.</param>
    public readonly record struct GpuModeProbe(
        bool HasHardwareMux,
        bool DdsAvailable,
        bool DdsSupported,
        bool DisplayDrivenByIgpu,
        bool BiosDiscreteGpuOnly,
        bool IgpuOnlyPathExposed)
    {
        /// <summary>Nothing is known yet (the value the ViewModel starts from).</summary>
        public static GpuModeProbe Unknown => default;
    }

    /// <summary>One mode's verdict: selectable or not, and if not, why.</summary>
    public readonly record struct GpuModeAvailability(GpuModeKind Kind, bool IsAvailable, GpuModeBlocker Blocker);

    /// <summary>The verdicts for all three modes on one machine.</summary>
    public sealed record GpuModeCapabilities(
        GpuModeAvailability Hybrid,
        GpuModeAvailability Integrated,
        GpuModeAvailability Discrete)
    {
        public GpuModeAvailability this[GpuModeKind kind] => kind switch
        {
            GpuModeKind.Hybrid => Hybrid,
            GpuModeKind.Integrated => Integrated,
            _ => Discrete
        };

        public GpuModeAvailability For(GpuModeKind kind) => this[kind];
    }

    /// <summary>
    /// Where the current mode reading came from. Stable tokens so the log line and the tests can
    /// assert on the reasoning instead of on a translated sentence.
    /// </summary>
    public static class GpuModeBasis
    {
        public const string BiosDiscrete = "bios-discrete";
        public const string DdsAutomatic = "dds-automatic";
        public const string DdsDgpu = "dds-dgpu";
        public const string DdsOptimus = "dds-optimus";
        public const string IgpuRoute = "igpu-route";
        public const string Unknown = "unknown";
    }

    /// <summary>Current mode + the evidence it was derived from.</summary>
    public readonly record struct GpuModeIdentification(GpuModeKind? Mode, string Basis)
    {
        public static GpuModeIdentification Unknown { get; } = new(null, GpuModeBasis.Unknown);
    }

    /// <summary>
    /// The three-mode decision, as a pure function. No hardware access, no localization, no UI
    /// state — call it with the probe and it tells you what this machine can do.
    /// </summary>
    public static class GpuModes
    {
        /// <summary>
        /// Whether any probe in this build can put the machine into iGPU-only. It cannot:
        /// nvcpl's DDS status (0x17) only reports Optimus / Automatic / dGPU, and the Acer MUX
        /// offers Optimus / Discrete / Automatic. "iGPU only" is therefore reported as
        /// unavailable rather than aliased onto Hybrid — a tile that claims a mode the hardware
        /// cannot enter is a fake feature.
        /// </summary>
        public const bool IgpuOnlyPathExposed = false;

        public static GpuModeCapabilities Evaluate(GpuModeProbe probe) => new(
            Hybrid: EvaluateHybrid(probe),
            Integrated: EvaluateIntegrated(probe),
            Discrete: EvaluateDiscrete(probe));

        /// <summary>iGPU drives the panel: DDS can switch it, or Optimus already is the wiring.</summary>
        private static GpuModeAvailability EvaluateHybrid(GpuModeProbe probe)
        {
            if (probe.BiosDiscreteGpuOnly)
                return new(GpuModeKind.Hybrid, false, GpuModeBlocker.BiosDiscreteOnly);

            if ((probe.DdsAvailable && probe.DdsSupported) || probe.DisplayDrivenByIgpu)
                return new(GpuModeKind.Hybrid, true, GpuModeBlocker.None);

            return new(GpuModeKind.Hybrid, false, GpuModeBlocker.NoHybridPath);
        }

        private static GpuModeAvailability EvaluateIntegrated(GpuModeProbe probe)
        {
            if (probe.BiosDiscreteGpuOnly)
                return new(GpuModeKind.Integrated, false, GpuModeBlocker.BiosDiscreteOnly);

            return probe.IgpuOnlyPathExposed
                ? new(GpuModeKind.Integrated, true, GpuModeBlocker.None)
                : new(GpuModeKind.Integrated, false, GpuModeBlocker.NoIgpuOnlyPath);
        }

        /// <summary>
        /// dGPU direct needs the hardware MUX. A BIOS dGPU-only boot counts as available even
        /// without a readable MUX value: the machine is already in that state, which proves the
        /// firmware supports it, and greying out the tile the user is currently in reads as a bug.
        /// </summary>
        private static GpuModeAvailability EvaluateDiscrete(GpuModeProbe probe) =>
            probe.HasHardwareMux || probe.BiosDiscreteGpuOnly
                ? new(GpuModeKind.Discrete, true, GpuModeBlocker.None)
                : new(GpuModeKind.Discrete, false, GpuModeBlocker.NoHardwareMux);

        /// <summary>
        /// Which mode the machine is in right now, and on what evidence.
        ///
        /// The old rule only looked at DDS, so a machine without DDS always read "N/A" even when
        /// the panel was plainly being driven by the iGPU (plain Optimus). A hybrid wiring is a
        /// fact that can be observed without any NVIDIA API, so it is used as the fallback: only
        /// when the panel route is unreadable too does the mode stay unknown.
        /// </summary>
        public static GpuModeIdentification Identify(GpuModeProbe probe, DdsStatus? dds)
        {
            if (probe.BiosDiscreteGpuOnly)
                return new(GpuModeKind.Discrete, GpuModeBasis.BiosDiscrete);

            if (dds is { IsSupported: true, KmdResponse: 0 })
            {
                // Auto may route through either GPU; it is still the dynamic-hybrid mode.
                if (dds.IsAutomatic)
                    return new(GpuModeKind.Hybrid, GpuModeBasis.DdsAutomatic);
                if (dds.ActiveMux == DdsActiveMux.dGPU)
                    return new(GpuModeKind.Discrete, GpuModeBasis.DdsDgpu);
                if (dds.ActiveMux == DdsActiveMux.Optimus)
                    return new(GpuModeKind.Hybrid, GpuModeBasis.DdsOptimus);
            }

            if (probe.DisplayDrivenByIgpu)
                return new(GpuModeKind.Hybrid, GpuModeBasis.IgpuRoute);

            return GpuModeIdentification.Unknown;
        }

        /// <summary>The English source name of a mode; the UI localizes it (docs\UI-DESIGN-RULES §7).</summary>
        public static string SourceName(GpuModeKind? mode) => mode switch
        {
            GpuModeKind.Hybrid => "Hybrid",
            GpuModeKind.Integrated => "Integrated",
            GpuModeKind.Discrete => "dGPU Only",
            _ => "N/A"
        };
    }
}
