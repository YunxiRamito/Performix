using System;
using System.Drawing;
using PreySense.Battery;
using PreySense.Core.Hardware;
using PreySense.Core.Services;
using PreySense.Gpu;
using PreySense.Helpers;
using PreySense.Mode;
using PreySense.Rgb;

namespace PreySense.Frontend.Services
{
    public sealed class ProductionHardwareService : IHardwareService, ITelemetrySources, IExtendedLightingHardware, IDisposable
    {
        private readonly WmiController _wmi;
        private readonly Lazy<CustomLightingEffectsEngine> _lightingEngine;
        private readonly Lazy<int?> _memorySpeed = new(ReadMemorySpeed);
        // Live temperature/RPM source for this machine: the Predator Sense service pipe.
        // Acer WMI GetGamingSysInfo is broken here (id 1 -> 0, id 2 -> 0x10000000000, id 6/10 -> 2).
        private readonly AcerPipeSensorSource _pipeSensors = new();
        private int _kbdTimeoutSeconds;

        public event EventHandler<byte>? PowerModeChanged;

        public byte CurrentPowerMode
        {
            // Never claim a hardware mode that could not be read back.
            get => TryGetPowerMode(out byte mode) ? mode : byte.MaxValue;
        }

        private NvidiaGpuControl Gpu => NvidiaGpuControl.Shared;

        /// <summary>
        /// Reads the live Acer operating mode: the OEM service pipe (cmd 33, authoritative on this
        /// chassis) first, then the direct WMI getter (Get is reliable here; only Set is broken).
        /// </summary>
        public bool TryGetPowerMode(out byte mode)
        {
            if (_pipeSensors.TryGetPowerMode(out mode, out _))
                return true;
            return _wmi.TryGetPowerProfile(out mode);
        }
        public CustomLightingEffectsEngine LightingEngine => _lightingEngine.Value;
        public WmiController Wmi => _wmi;

        public ProductionHardwareService()
        {
            _wmi = new WmiController();
            _lightingEngine = new Lazy<CustomLightingEffectsEngine>(() =>
            {
                var engine = new CustomLightingEffectsEngine(_wmi);
                engine.SetIdleTimeoutSeconds(_kbdTimeoutSeconds);
                _wmi.AttachLightingEngine(engine);
                return engine;
            }, true);
            // Force attach before any WMI lighting path can lazy-create a second engine.
            _ = LightingEngine;
            // Hardwired Acer targets: probe HID/EC once at process start, never again.
            _ = _wmi.OemLighting.Discover();
            TelemetryCoordinator.ConfigureMetricSampler(SampleTelemetrySnapshot);
        }

        public PowerSourceKind GetPowerSource() => _wmi.ReadPowerSource();

        private TelemetrySnapshot SampleTelemetrySnapshot(TelemetryMetrics requested, bool fanOnly) =>
            TelemetryCollector.Read(requested, this, fanOnly);

        CpuTelemetrySnapshot? ITelemetrySources.ReadCpuUsage() => CpuUsageMonitor.GetSnapshot(aggregateOnly: true);

        /// <summary>Last source that produced a CPU temperature reading (pipe / driver / ACPI).</summary>
        public SensorSource CpuTemperatureSource { get; private set; } = SensorSource.None;

        /// <summary>Last source that produced a GPU temperature reading (pipe / NVAPI / NVML).</summary>
        public SensorSource GpuTemperatureSource { get; private set; } = SensorSource.None;

        /// <summary>Last source that produced a CPU fan RPM reading (pipe / Acer WMI).</summary>
        public SensorSource CpuFanSource { get; private set; } = SensorSource.None;

        /// <summary>Last source that produced a GPU fan RPM reading (pipe / Acer WMI).</summary>
        public SensorSource GpuFanSource { get; private set; } = SensorSource.None;

        /// <summary>True when the Predator Sense pipe answered the last read.</summary>
        public bool IsPipeSensorAvailable => _pipeSensors.IsPipeAvailable;

        /// <summary>The pipe diagnostics client (read-only cmd 15).</summary>
        public AcerPipeSensorSource PipeSensors => _pipeSensors;

        // CPU temperature: Predator pipe -> CPU driver MSR (removed in this fork) -> ACPI thermal zone.
        int? ITelemetrySources.ReadCpuTemperature()
        {
            var readout = ThermalReadout.CpuTemperature(
                _pipeSensors,
                () => PreySense.Core.Hardware.CpuDriverRuntime.Telemetry?.ReadPackageTemperature());
            CpuTemperatureSource = readout.Source;
            return readout.Value;
        }

        float? ITelemetrySources.ReadCpuVoltage() => PreySense.Core.Hardware.CpuDriverRuntime.Telemetry?.ReadCoreVoltageVid();
        float? ITelemetrySources.ReadCpuPower() => PreySense.Core.Hardware.CpuDriverRuntime.Telemetry?.ReadPackagePowerWatts();
        int? ITelemetrySources.ReadRamSpeed() => _memorySpeed.Value;
        string? ITelemetrySources.ReadDisplayGpuName() => DisplayManager.GetPrimaryDisplayGpuName();
        NvidiaGpuControl.Metrics ITelemetrySources.ReadGpu(TelemetryMetrics metrics)
        {
            // Read Windows' cached power data before touching NVAPI/NVML or even
            // constructing the GPU client. Missing/asleep adapters remain asleep.
            NvidiaGpuControl.Metrics gpu;
            if (DeviceHelper.GetDisplayDevicePowerState("VEN_10DE") != 1)
            {
                NvmlHelper.Shutdown();
                gpu = default;
            }
            else
            {
                gpu = Gpu.ReadMetrics(metrics);
            }

            // GPU temperature: Predator pipe -> NVAPI (gpu.Temperature) -> NVML.
            // The pipe keeps the fan curve alive even while the dGPU is in a low-power state.
            if ((metrics & TelemetryMetrics.GpuTemperature) != 0)
            {
                var readout = ThermalReadout.GpuTemperature(
                    _pipeSensors, gpu.Temperature, () => NvmlHelper.GetGpuTemperature());
                GpuTemperatureSource = readout.Source;
                if (readout.Value is int temperature)
                    gpu = gpu with { Temperature = temperature };
            }

            return gpu;
        }

        CpuClockSnapshot ITelemetrySources.ReadCpuClockSnapshot(TelemetryMetrics requested) => ReadDriverClocks(requested);

        private static CpuClockSnapshot ReadDriverClocks(TelemetryMetrics requested)
        {
            bool active = (requested & TelemetryMetrics.CpuClock) != 0;
            bool effective = (requested & TelemetryMetrics.CpuEffectiveClock) != 0;
            if (!active && !effective) return default;
            var sample = PreySense.Core.Hardware.CpuDriverRuntime.Telemetry?.SampleTopologyClocks(includeEffective: effective);
            if (sample is not { } clocks) return default;
            static int? Max(int? a, int? b) => a.HasValue && b.HasValue ? Math.Max(a.Value, b.Value) : a ?? b;
            return new(active ? clocks.PCoreActiveMhz : null, active ? clocks.ECoreActiveMhz : null,
                active ? Max(clocks.PCoreActiveMhz, clocks.ECoreActiveMhz) : null,
                effective ? Max(clocks.PCoreEffectiveMhz, clocks.ECoreEffectiveMhz) : null);
        }

        (int? Performance, int? Efficiency) ITelemetrySources.ReadCpuClocks()
        {
            var clocks = ReadDriverClocks(TelemetryMetrics.CpuClock);
            return (clocks.PerformanceMhz, clocks.EfficiencyMhz);
        }

        (int? ActiveMhz, int? EffectiveMhz) ITelemetrySources.ReadCpuActiveEffectiveClocks()
        {
            var clocks = ReadDriverClocks(TelemetryMetrics.CpuClock | TelemetryMetrics.CpuEffectiveClock);
            return (clocks.ActiveMhz, clocks.EffectiveMhz);
        }

        (int? Pl1Watts, int? Pl2Watts) ITelemetrySources.ReadCpuPowerLimits()
        {
            var snap = PreySense.Core.Hardware.CpuDriverRuntime.PowerLimits?.ReadMsrPowerLimits();
            if (snap is { Success: true } s)
            {
                return (s.Pl1Watts > 0 ? s.Pl1Watts : null, s.Pl2Watts > 0 ? s.Pl2Watts : null);
            }

            return default;
        }

        (int? Percent, int? UsedMb) ITelemetrySources.ReadRamUsage()
        {
            var memory = SystemMemoryMonitor.GetMemoryInfo();
            return (memory?.UsedPercent, memory?.UsedMb);
        }

        (int? Cpu, int? Gpu) ITelemetrySources.ReadFanRpms(bool cpu, bool gpu)
        {
            // Fan RPM: Predator pipe first. Acer WMI is only accepted when its answer is
            // physically plausible — on this chassis GetGamingSysInfo returns 0 / 0x10000000000
            // for the fan ids, which must never reach the fan curve.
            int? cpuRpm = null;
            int? gpuRpm = null;

            if (cpu)
            {
                var readout = ThermalReadout.FanRpm(
                    _pipeSensors, AcerPipeSensorSource.SensorCpuFanRpm, () => _wmi.CpuFanRpm);
                CpuFanSource = readout.Source;
                cpuRpm = readout.Value;
            }

            if (gpu)
            {
                var readout = ThermalReadout.FanRpm(
                    _pipeSensors, AcerPipeSensorSource.SensorGpuFanRpm, () => _wmi.GpuFanRpm);
                GpuFanSource = readout.Source;
                gpuRpm = readout.Value;
            }

            return (cpuRpm, gpuRpm);
        }

        private static int? ReadMemorySpeed()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT ConfiguredClockSpeed FROM Win32_PhysicalMemory");
                using var results = searcher.Get();
                foreach (System.Management.ManagementObject module in results)
                {
                    using (module)
                        if (module["ConfiguredClockSpeed"] is uint speed && speed > 0) return (int)speed;
                }
            }
            catch (System.Management.ManagementException) { }
            return null;
        }


        public void RefreshGpuAdapter() => NvidiaGpuControl.Shared.RefreshDevice();

        /// <summary>Last operating-mode switch attempt, for diagnostics/UI (never optimistic).</summary>
        public string LastPowerModeStatus { get; private set; } = string.Empty;

        /// <summary>
        /// Switches the Acer operating mode. The OEM service pipe (cmd 32) is the working channel on
        /// this chassis; the direct root\WMI SetGamingMiscSetting call is only a fallback for when the
        /// pipe is unavailable (it fails here with a firmware error). Success is confirmed by reading
        /// the mode back — the caller must not assume the switch happened.
        /// </summary>
        public bool SetPowerMode(byte mode)
        {
            string status = string.Empty;

            // Always try the pipe first: IsPipeAvailable may still be false before the first read,
            // so it must not decide the channel. A transport failure (pipe unreachable) is the only
            // case that falls back to the legacy WMI setter.
            if (_pipeSensors.TrySetPowerMode(mode, out status))
            {
                // TrySetPowerMode already confirms with a cmd 33 readback.
                if (!_pipeSensors.TryGetPowerMode(out byte readBack, out string readStatus) || readBack != mode)
                {
                    LastPowerModeStatus = $"readback mismatch after set: {readStatus}";
                    AppLogger.Error($"[Mode] {LastPowerModeStatus}");
                    return false;
                }
            }
            else if (!_pipeSensors.IsPipeAvailable)
            {
                // Pipe gone (PSSvc stopped): try the legacy WMI path, then confirm via WMI Get,
                // which is known-good on this chassis.
                if (!_wmi.SetPowerMode(mode))
                {
                    LastPowerModeStatus =
                        $"kernel-mode WMI switch failed and the Predator service pipe is unavailable ({status})";
                    AppLogger.Error($"[Mode] {LastPowerModeStatus}");
                    return false;
                }
                if (!_wmi.TryGetPowerProfile(out byte readBack) || readBack != mode)
                {
                    LastPowerModeStatus = "WMI readback mismatch after set";
                    AppLogger.Error($"[Mode] {LastPowerModeStatus}");
                    return false;
                }
                status = "mode set through the legacy WMI fallback (pipe unavailable)";
            }
            else
            {
                // Pipe answered but refused / disagreed on readback: never claim success.
                LastPowerModeStatus = status;
                AppLogger.Error($"[Mode] Pipe switch to 0x{mode:X2} failed: {status}");
                return false;
            }

            LastPowerModeStatus = status;
            AppLogger.Log($"[Mode] 0x{mode:X2} applied ({status})");
            PowerModeChanged?.Invoke(this, mode);
            return true;
        }

        /// <summary>
        /// Ensures the Predator service pipe is usable, starting <c>PSSvc</c> if needed.
        /// Blocking; call from a background thread. Returns a user-facing status message.
        /// </summary>
        public (bool Available, string Status) EnsurePipeService()
        {
            var result = PredatorPipeService.EnsureAvailable(_pipeSensors);
            LastPowerModeStatus = result.Status;
            if (!result.Available)
                AppLogger.Error($"[Pipe] {result.Status}");
            else
                AppLogger.Log($"[Pipe] {result.Status}");
            return (result.Available, result.Status);
        }

        /// <summary>
        /// Sets the EC fan behaviour (0 = Auto, 1 = Max, 2 = Custom). W10: the Acer EC exposes a
        /// single behaviour register for both fans — there is no independent GPU fan mode on this
        /// chassis, so the old cpu/gpu parameter pair was misleading and silently ignored.
        /// </summary>
        public bool SetFanMode(byte mode) => _wmi.SetFanControl(mode);

        public bool SetFanSpeeds(byte cpuPercentage, byte gpuPercentage)
        {
            return _wmi.SetFanControl(2, cpuPercentage, gpuPercentage);
        }

        public void InvalidateFanTargets() => _wmi.InvalidateFanTargets();

        public int GetBatteryChargeLimit()
        {
            return BatteryControl.GetBatteryLimit();
        }

        public bool SetBatteryChargeLimit(int limitMode)
        {
            return BatteryControl.SetBatteryLimit(_wmi, limitMode);
        }

        public int? GetGpuMuxMode()
        {
            return _wmi.GetGpuMuxModeWmi();
        }

        public bool SetGpuMuxMode(int muxMode)
        {
            return _wmi.SetGpuMuxMode(muxMode);
        }

        public bool IsBiosDiscreteGpuActive
        {
            get
            {
                // BIOS dGPU only keeps the iGPU device node, but Windows reports it Off Line.
                return DeviceHelper.IsDiscreteGpuOnlyDisplay();
            }
        }

        // The panel route, read from Win32_VideoController (see GpuDisplayRoute). Kept behind the
        // hardware seam so the three-mode decision can be tested with a fixed answer.
        public bool IsDisplayDrivenByIgpu => GpuDisplayRoute.IsDisplayDrivenByIgpu() == true;

        public bool IsDdsAvailable => NvidiaDdsService.Instance.IsAvailable;
        public bool IsDdsSupported() => NvidiaDdsService.Instance.IsDdsSupported();
        public DdsStatus? GetDdsStatus() => NvidiaDdsService.Instance.GetStatus();
        public bool HasDdsBlockers() => NvidiaDdsService.Instance.HasBlockingApps();
        public (bool Success, string Message) SetDdsMode(DdsDisplayMode mode) => NvidiaDdsService.Instance.SetMode(mode);

        public (bool Success, string Message) RestartDiscreteGpu()
        {
            try
            {
                AppLogger.Log("RestartDiscreteGpu: Initiating reboot-free dGPU power cycle via Windows PnP...");

                var control = NvidiaGpuControl.Shared;
                if (control.IsValid)
                {
                    control.KillGPUApps();
                }

                if (!DeviceHelper.RestartDevice("VEN_10DE"))
                    return (false, "Windows could not restart the NVIDIA adapter.");
                NvidiaGpuControl.RestartNVService();
                System.Threading.Thread.Sleep(1500);
                NvidiaDdsService.Instance.Dispose();
                control.RefreshDevice();
                if (NvidiaDdsService.Instance.IsAvailable && NvidiaDdsService.Instance.IsDdsSupported())
                {
                    var (changed, detail) = NvidiaDdsService.Instance.SetMode(DdsDisplayMode.Automatic);
                    if (!changed) return (false, $"Adapter restarted; automatic display mode failed: {detail}");
                }

                return (true, "Discrete GPU successfully power-cycled and reinitialized without system reboot.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"RestartDiscreteGpu error: {ex.Message}");
                return (false, $"dGPU restart failed: {ex.Message}");
            }
        }

        public bool IsRgbAvailable => _wmi.IsEnekRgbAvailable || _wmi.OemLighting.HasKeyboard;
        public System.Collections.Generic.IReadOnlyList<LightingDeviceInfo> DiscoverLightingDevices(bool refresh = false) =>
            _wmi.OemLighting.Discover(refresh: refresh);
        public void ApplyLightingDevice(string deviceId, PreySense.Core.Configuration.DeviceLightingConfig settings)
        {
            if (_wmi.OemLighting.Discover().Any(d => d.Id == deviceId && d.Kind == "Keyboard") && _lightingEngine.IsValueCreated)
                LightingEngine.StopAndWait();
            byte powerMode = TryGetPowerMode(out byte live) ? live : (byte)0x01;
            var resolved = PreySense.Rgb.ModeKeyLighting.ResolveForApply(settings, powerMode);
            _wmi.OemLighting.Apply(deviceId, resolved);
        }

        public void SetRgbStatic(byte zone, Color color)
        {
            LightingEngine.StopAndWait();
            int idx = Math.Clamp(zone - 1, 0, 3);
            Color[] colors = new Color[4];
            Array.Copy(_wmi.ZoneColors, colors, 4);
            colors[idx] = color;
            _wmi.SetZoneColors(colors, _wmi.Brightness);
        }

        public void SetRgbAllZones(Color z1, Color z2, Color z3, Color z4) =>
            SetRgbAllZones(z1, z2, z3, z4, _wmi.Brightness);

        public void SetRgbAllZones(Color z1, Color z2, Color z3, Color z4, byte brightness)
        {
            LightingEngine.StopAndWait();
            _wmi.SetZoneColors([z1, z2, z3, z4], brightness);
        }

        public void SetRgbHardwareEffect(int mode, byte r, byte g, byte b, byte brightness, byte speed, byte direction)
        {
            LightingEngine.StopAndWait();
            _wmi.SetRgbMode(mode, r, g, b, brightness, speed, direction);
        }

        public void SetRgbSpeed(byte speed)
        {
            LightingEngine.SetSpeed(speed);
            _wmi.SetSpeed(speed);
        }

        public void SetRgbBrightness(byte brightness)
        {
            byte scaled = (byte)Math.Clamp((int)brightness, 0, 100);
            LightingEngine.SetBrightness(scaled);
            if (LightingEngine.IsRunning)
                _wmi.NoteBrightness(scaled);
            else
                _wmi.SetBrightness(scaled);
        }

        public int? GetKeyboardBrightness() => _wmi.GetKeyboardBrightness();

        public bool IsCoverLightingAvailable => _wmi.IsCoverLogoHidAvailable;
        public void SetCoverLogoLighting(int mode, Color color, byte brightness, byte speed, bool enabled = true)
        {
            _wmi.SetLogoLighting(mode, color.R, color.G, color.B, brightness, speed, enabled);
        }

        // WMI / platform system controls
        private bool _lcdOverdrive;
        public bool GetLcdOverdrive() => _lcdOverdrive;
        public bool SetLcdOverdrive(bool enable)
        {
            bool ok = _wmi.SetLcdOverdrive(enable);
            if (ok) _lcdOverdrive = enable;
            return ok;
        }

        public bool GetBootSound() => _wmi.GetBootSound();
        public void SetBootSound(bool enable) => _wmi.SetBootSound(enable);

        public bool GetKbdBacklightTimeout() => _wmi.GetLedTimeout();
        public void SetKbdBacklightTimeout(bool enable) => SetKbdBacklightTimeoutSeconds(enable ? 30 : 0);
        public void SetKbdBacklightTimeoutSeconds(int seconds)
        {
            _kbdTimeoutSeconds = Math.Max(0, seconds);
            bool softwareOwnsTimeout = _lightingEngine.IsValueCreated && LightingEngine.IsRunning && _kbdTimeoutSeconds > 0;
            // Animation frames reset the EC idle timer. While a software effect is running, keep the hardware timeout off.
            _wmi.SetLedTimeoutSeconds(softwareOwnsTimeout ? 0 : _kbdTimeoutSeconds);
            if (_lightingEngine.IsValueCreated)
                LightingEngine.SetIdleTimeoutSeconds(_kbdTimeoutSeconds);
        }

        public int GetUsbChargingLevel() => _wmi.GetUsbChargingLevel();
        public void SetUsbChargingLevel(int percent) => _wmi.SetUsbChargingLevel(percent);

        private int _blueLightShieldMode = 0;
        public bool GetBlueLightShield() => _blueLightShieldMode > 0;
        public int GetBlueLightShieldMode() => _blueLightShieldMode;
        public void SetBlueLightShield(bool enable) => SetBlueLightShield(enable ? 1 : 0);
        public void SetBlueLightShield(int mode)
        {
            _blueLightShieldMode = mode;
            DisplayManager.SetBlueLightShield(mode);
        }

        public int GetDisplayRefreshRate() => DisplayManager.GetCurrentRefreshRate();
        public bool SetDisplayRefreshRate(int hz) => DisplayManager.SetRefreshRate(hz);
        public System.Collections.Generic.List<int> GetSupportedRefreshRates() => DisplayManager.GetSupportedRefreshRates();




        public void Dispose()
        {
            if (_lightingEngine.IsValueCreated)
                _lightingEngine.Value.Dispose();
            _wmi.Dispose();
            TelemetryCoordinator.Reset();
            GC.SuppressFinalize(this);
        }
    }
}
