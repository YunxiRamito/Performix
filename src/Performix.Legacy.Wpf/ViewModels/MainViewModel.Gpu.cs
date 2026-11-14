using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PreySense.Core.Driver;
using PreySense.Core.Services;
using PreySense.Frontend.Services;
using PreySense.Gpu;
using PreySense.Helpers;

namespace PreySense.Frontend.ViewModels
{
    public partial class MainViewModel
    {
        private int _gpuSwitchInProgress;
        private int _gpuDisplayRevision;

        // Injected seam for user prompts (docs/WINUI3-MIGRATION.md §7.4). Never construct a Window
        // here: the shell wires WpfUserPromptService, tests get the headless default.
        internal IUserPromptService? UserPrompts { get; set; }
        private IUserPromptService Prompts => UserPrompts ?? UnattendedUserPromptService.Instance;

        // Firmware GPU MUX (hardware display switch) support, cached.
        // -1 unknown, 0 unsupported, 1 supported. PH317-55 returns GetGamingMiscSetting(0x02)
        // high byte 0xFF => OEM Get_GPUMode_Support() == 0, i.e. no MUX on this chassis.
        private int _firmwareMuxSupport = -1;

        private bool HasFirmwareMuxSupport()
        {
            if (_firmwareMuxSupport < 0)
            {
                try { _firmwareMuxSupport = _hw.GetGpuMuxMode() is not null ? 1 : 0; }
                catch { _firmwareMuxSupport = 0; }
            }
            return _firmwareMuxSupport == 1;
        }

        // GPU Power Target & Driver Hardware Controls
        [ObservableProperty] private int _gpuPowerTargetWatts = 130;
        [ObservableProperty] private int _gpuPowerTargetMin = GpuPowerLimitConfiguration.MinimumPowerLimitWatts;
        [ObservableProperty] private int _gpuPowerTargetMax = GpuPowerLimitConfiguration.MaximumPowerLimitWatts;
        [ObservableProperty] private int? _gpuBasePowerWatts = GpuPowerBaseline.ReadWatts();

        [ObservableProperty] private int _customQuietGpuWatts = 85;
        [ObservableProperty] private int _customEcoGpuWatts = 85;
        [ObservableProperty] private int _customBalancedGpuWatts = 115;
        [ObservableProperty] private int _customPerfGpuWatts = 125;
        [ObservableProperty] private int _customTurboGpuWatts = 130;
        [ObservableProperty] private string _gpuStateDescription = "N/A";
        [ObservableProperty] private string _activeGpuPowerCeiling = "N/A";
        private bool _gpuHardwareInitialized;

        public void EnsureGpuHardwareInitialized()
        {
            if (_gpuHardwareInitialized)
                return;

            _gpuHardwareInitialized = true;
            _ = Task.Run(InitializeGpuHardwareBackground);
        }

        private void InitializeGpuHardwareBackground()
        {
            try
            {
                int? baseline = ReadOrCaptureGpuBaseline();

                if (_disposed)
                    return;

                _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed)
                        return;

                    GpuBasePowerWatts = baseline;
                    ActiveGpuPowerCeiling = "N/A";
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainViewModel] Lazy GPU initialization error: {ex.Message}");
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGpuOptimusActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuAutoActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuNvidiaOnlyActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuEnduranceActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuStandardActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuUltimateActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuAdvancedOptimusActive))]
        // 0 = Optimus (iGPU), 1 = Automatic (Auto DDS), 2 = NVIDIA GPU Only (dGPU), -1 = unknown.
        // Legacy DDS-era value: the three-mode panel reads IsGpuHybrid/Integrated/DiscreteModeActive
        // instead. It is still derived (see LegacyMuxMode) because the WPF shell keys its four-tile
        // grid off it and the older tests assert on it.
        private int _activeGpuMuxMode = -1;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GpuModeDisplayName))]
        private string _gpuMuxModeName = "N/A";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGpuEnduranceActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuStandardActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuUltimateActive))]
        [NotifyPropertyChangedFor(nameof(GpuModeTileColumns))]
        [NotifyPropertyChangedFor(nameof(IsGpuOptimusActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuAutoActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuNvidiaOnlyActive))]
        [NotifyPropertyChangedFor(nameof(IsGpuAdvancedOptimusActive))]
        [NotifyPropertyChangedFor(nameof(ShowDdsGpuTiles))]
        private bool _isBiosDiscreteGpuActive;

        // ── Three-mode capability layer ──────────────────────────────────────────
        // The panel asks one question: which GPU drives the panel. The verdict is a pure
        // function of six probe booleans (Core\Gpu\GpuModeCapabilities.cs); this file only
        // supplies them and exposes the results. Measured on the reference PH317-55:
        // no hardware MUX (Acer GetGamingMiscSetting(0x02) = 0xFF00), DDS reports supported=0,
        // the panel is driven by the iGPU — so exactly one mode is selectable: Hybrid.
        private GpuModeProbe _gpuProbe = GpuModeProbe.Unknown;
        private GpuModeCapabilities? _gpuCapabilities;
        private GpuModeKind? _currentGpuMode;
        private string _currentGpuModeBasis = GpuModeBasis.Unknown;
        private string? _lastLoggedGpuModeLine;

        // Lazily evaluated so a ViewModel created without its constructor (the test doubles do
        // that) still answers with "nothing is available" instead of a null reference.
        private GpuModeCapabilities Capabilities => _gpuCapabilities ??= GpuModes.Evaluate(_gpuProbe);

        /// <summary>The verdicts for this machine (hybrid / integrated / discrete).</summary>
        public GpuModeCapabilities GpuCapabilities => Capabilities;

        /// <summary>Evidence token behind the current reading (see <see cref="GpuModeBasis"/>).</summary>
        public string GpuModeBasisToken => _currentGpuModeBasis;

        // Which tile is selected right now.
        public bool IsGpuHybridModeActive => _currentGpuMode == GpuModeKind.Hybrid;
        public bool IsGpuIntegratedModeActive => _currentGpuMode == GpuModeKind.Integrated;
        public bool IsGpuDiscreteModeActive => _currentGpuMode == GpuModeKind.Discrete;

        // Whether this machine can enter each mode. False greys the tile out and puts the reason
        // in its tooltip; a mode is never hidden, because a hidden tile reads as "the app forgot
        // to draw it" while a disabled one reads as "this machine cannot do that".
        public bool IsGpuHybridModeAvailable => Capabilities.Hybrid.IsAvailable;
        public bool IsGpuIntegratedModeAvailable => Capabilities.Integrated.IsAvailable;
        public bool IsGpuDiscreteModeAvailable => Capabilities.Discrete.IsAvailable;

        public string GpuHybridModeToolTip => GpuModeText.TileToolTip(Capabilities.Hybrid);
        public string GpuIntegratedModeToolTip => GpuModeText.TileToolTip(Capabilities.Integrated);
        public string GpuDiscreteModeToolTip => GpuModeText.TileToolTip(Capabilities.Discrete);

        /// <summary>
        /// The current mode, localized. "N/A" (nothing could be read at all) is still mapped to
        /// the em dash by the XAML converter, per docs\UI-DESIGN-RULES §5.
        /// </summary>
        public string GpuModeDisplayName => LocalizationService.Translate(GpuMuxModeName);

        /// <summary>
        /// Tooltip of the header readout: the same sentence the selected tile carries, so the two
        /// never disagree; only when nothing could be read does it say so instead.
        /// </summary>
        public string GpuModeDisplayToolTip => _currentGpuMode is { } mode
            ? LocalizationService.Translate(GpuModeText.TileToolTip(Capabilities.For(mode)))
            : LocalizationService.Translate("GPU mode is not readable on this hardware (no MUX / DDS).");

        public bool ShowDdsGpuTiles => !IsBiosDiscreteGpuActive;
        public int GpuModeTileColumns => IsBiosDiscreteGpuActive ? 2 : 4;


        // GPU selection flags (0=Optimus, 1=Automatic, 2=NVIDIA Only)
        public bool IsGpuEnduranceActive => !IsBiosDiscreteGpuActive && ActiveGpuMuxMode == 0;
        public bool IsGpuStandardActive => !IsBiosDiscreteGpuActive && ActiveGpuMuxMode == 1;
        public bool IsGpuUltimateActive => !IsBiosDiscreteGpuActive && ActiveGpuMuxMode == 2;
        public bool IsGpuOptimusActive => !IsBiosDiscreteGpuActive && ActiveGpuMuxMode == 0;
        public bool IsGpuAutoActive => !IsBiosDiscreteGpuActive && ActiveGpuMuxMode == 1;
        public bool IsGpuNvidiaOnlyActive => !IsBiosDiscreteGpuActive && ActiveGpuMuxMode == 2;
        public bool IsGpuAdvancedOptimusActive => ActiveGpuMuxMode == 2 && !IsBiosDiscreteGpuActive;
        partial void OnIsBiosDiscreteGpuActiveChanged(bool value)
        {
            _gpuProbe = _gpuProbe with { BiosDiscreteGpuOnly = value };
            if (value)
                GpuMuxModeName = GpuModes.SourceName(GpuModeKind.Discrete);
        }

        /// <summary>
        /// Reads the six facts the three-mode decision needs. Every read is individually guarded:
        /// one broken provider must not cost the user the whole panel, it only removes the
        /// evidence for that one mode.
        /// </summary>
        private GpuModeProbe ReadGpuModeProbe(bool biosDiscrete)
        {
            bool mux = false, ddsAvailable = false, ddsSupported = false, displayIgpu = false;
            try { mux = HasFirmwareMuxSupport(); } catch { }
            try { ddsAvailable = _hw.IsDdsAvailable; } catch { }
            try { ddsSupported = ddsAvailable && _hw.IsDdsSupported(); } catch { }
            try { displayIgpu = _hw.IsDisplayDrivenByIgpu; } catch { }
            return new GpuModeProbe(
                HasHardwareMux: mux,
                DdsAvailable: ddsAvailable,
                DdsSupported: ddsSupported,
                DisplayDrivenByIgpu: displayIgpu,
                BiosDiscreteGpuOnly: biosDiscrete,
                IgpuOnlyPathExposed: GpuModes.IgpuOnlyPathExposed);
        }

        private async Task RefreshGpuModeStateAsync(bool duringSwitch = false)
        {
            using var readLease = duringSwitch ? null : HardwareOperationGate.TryEnterTelemetry();
            if (!duringSwitch && readLease == null) return;
            int revision = System.Threading.Volatile.Read(ref _gpuDisplayRevision);
            var state = await Task.Run(() =>
            {
                bool biosDiscrete = _hw.IsBiosDiscreteGpuActive;
                GpuModeProbe probe = ReadGpuModeProbe(biosDiscrete);
                DdsStatus? dds = null;
                if (!biosDiscrete && probe.DdsAvailable)
                {
                    try { dds = _hw.GetDdsStatus(); } catch { }
                }
                return (biosDiscrete, probe, dds);
            });
            if (_disposed) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || revision != System.Threading.Volatile.Read(ref _gpuDisplayRevision)) return;
                IsBiosDiscreteGpuActive = state.biosDiscrete;
                ApplyGpuModeState(state.probe, state.dds);
            });
        }

        private void NotifyGpuSelectionsChanged()
        {
            OnPropertyChanged(nameof(ShowDdsGpuTiles));
            OnPropertyChanged(nameof(GpuModeTileColumns));
            OnPropertyChanged(nameof(IsGpuEnduranceActive));
            OnPropertyChanged(nameof(IsGpuStandardActive));
            OnPropertyChanged(nameof(IsGpuUltimateActive));
            OnPropertyChanged(nameof(IsGpuOptimusActive));
            OnPropertyChanged(nameof(IsGpuAutoActive));
            OnPropertyChanged(nameof(IsGpuNvidiaOnlyActive));
            OnPropertyChanged(nameof(IsGpuAdvancedOptimusActive));
            // Three-mode panel
            OnPropertyChanged(nameof(GpuCapabilities));
            OnPropertyChanged(nameof(GpuModeBasisToken));
            OnPropertyChanged(nameof(IsGpuHybridModeActive));
            OnPropertyChanged(nameof(IsGpuIntegratedModeActive));
            OnPropertyChanged(nameof(IsGpuDiscreteModeActive));
            OnPropertyChanged(nameof(IsGpuHybridModeAvailable));
            OnPropertyChanged(nameof(IsGpuIntegratedModeAvailable));
            OnPropertyChanged(nameof(IsGpuDiscreteModeAvailable));
            OnPropertyChanged(nameof(GpuHybridModeToolTip));
            OnPropertyChanged(nameof(GpuIntegratedModeToolTip));
            OnPropertyChanged(nameof(GpuDiscreteModeToolTip));
            OnPropertyChanged(nameof(GpuModeDisplayName));
            OnPropertyChanged(nameof(GpuModeDisplayToolTip));
        }

        /// <summary>
        /// Applies a probe + DDS reading: recomputes the three verdicts, identifies the mode the
        /// machine is in, and writes the evidence into the log (which is the only way to tell
        /// "this chassis cannot do it" from "the read failed" after the fact).
        /// </summary>
        private void ApplyGpuModeState(GpuModeProbe probe, DdsStatus? dds)
        {
            _gpuProbe = probe;
            _gpuCapabilities = GpuModes.Evaluate(probe);
            GpuModeIdentification identification = GpuModes.Identify(probe, dds);
            _currentGpuMode = identification.Mode;
            _currentGpuModeBasis = identification.Basis;

            ActiveGpuMuxMode = LegacyMuxMode(identification.Mode, dds);
            GpuMuxModeName = GpuModes.SourceName(identification.Mode);

            GpuModeCapabilities caps = Capabilities;
            // AppLogger.Log() only persists lines that look like failures (Logging\AppLogger.cs),
            // so a status line has to go through Error() to survive — the same call the pipe
            // status line uses. A repeat with an unchanged verdict is dropped, otherwise a
            // device-change storm would fill debug.log with identical rows.
            string line =
                "GPU modes: " +
                $"mux={probe.HasHardwareMux} ddsAvailable={probe.DdsAvailable} ddsSupported={probe.DdsSupported} " +
                $"displayIgpu={probe.DisplayDrivenByIgpu} biosDiscrete={probe.BiosDiscreteGpuOnly} → " +
                $"hybrid={caps.Hybrid.IsAvailable}/{caps.Hybrid.Blocker} " +
                $"integrated={caps.Integrated.IsAvailable}/{caps.Integrated.Blocker} " +
                $"discrete={caps.Discrete.IsAvailable}/{caps.Discrete.Blocker} " +
                $"current={GpuModes.SourceName(identification.Mode)} ({identification.Basis})";
            if (line != _lastLoggedGpuModeLine)
            {
                _lastLoggedGpuModeLine = line;
                AppLogger.Error(line);
            }

            NotifyGpuSelectionsChanged();
        }

        /// <summary>
        /// Re-derives the mode from a driver reading using the probe already on file. This is the
        /// single entry point for "the driver state changed" (and the one the tests drive).
        /// </summary>
        private void ApplyDdsStatus(DdsStatus? status) => ApplyGpuModeState(_gpuProbe, status);

        /// <summary>
        /// Translates the three-mode reading back into the DDS-era number (0 Optimus, 1 Automatic,
        /// 2 dGPU, -1 unknown) that the WPF shell and the older tests still key off.
        /// </summary>
        private static int LegacyMuxMode(GpuModeKind? mode, DdsStatus? dds) => mode switch
        {
            GpuModeKind.Discrete => 2,
            GpuModeKind.Hybrid => dds?.IsAutomatic == true ? 1 : 0,
            _ => -1
        };

        /// <summary>
        /// Name of a DDS request value for the progress messages. 1 is DDS "Automatic", which is
        /// not a tile any more (the panel only offers Hybrid / Integrated / dGPU), so it keeps its
        /// own name instead of borrowing a tile label.
        /// </summary>
        private static string DdsTargetName(int muxVal) => muxVal switch
        {
            (int)DdsDisplayMode.NvidiaGpuOnly => GpuModes.SourceName(GpuModeKind.Discrete),
            (int)DdsDisplayMode.Automatic => "Automatic",
            _ => GpuModes.SourceName(GpuModeKind.Hybrid)
        };

        private static void SaveGpuModePreference(int mode)
        {
            try
            {
                PreySense.Core.Configuration.ConfigurationService.Instance.Update(cfg =>
                {
                    cfg.Session.GpuModePreference = Math.Clamp(mode, 0, 2);
                });
            }
            catch { }
        }

        [RelayCommand]
        public async Task ApplyGpuClocksAsync()
        {
            var offsets = GpuOffsetsForMode(GpuModeIndex(ActivePowerMode));
            if (offsets is null) { DiagnosticMessage = "Set GPU offsets before applying."; return; }
            await ApplyTuningProfileAsync(ActivePowerMode, Pl1Watts, Pl2Watts,
                TauForMode(ActivePowerMode), offsets, persist: false);
        }

        [RelayCommand]
        public async Task SetGpuMuxModeAsync(object? muxParam)
        {
            int muxVal = muxParam switch
            {
                int i => i,
                byte b => (int)b,
                string s when int.TryParse(s, out int parsed) => parsed,
                _ => -1
            };
            if (muxVal < 0 || muxVal > 2) return;

            if (IsBiosDiscreteGpuActive)
            {
                if (muxVal == 1)
                    await SetBiosGpuModeAsync(PreySense.Helpers.AcerWmi.GpuMux.Automatic, "Would you like to switch to Auto and restart now?", "Performix • Auto", " • Auto");
                else
                    // Leaving dGPU-only is a firmware switch; the tile must say so instead of
                    // doing nothing when the machine cannot go there.
                    DiagnosticMessage = LocalizationService.Translate(GpuModeText.BlockerMessage(GpuModeBlocker.BiosDiscreteOnly));
                return;
            }

            // The hybrid tile is the only one that reaches this command from the home panel.
            // A disabled tile cannot be clicked, so this guard is for the keyboard/automation
            // path: it answers with the reason rather than swallowing the press.
            if (muxVal == (int)DdsDisplayMode.Optimus && !IsGpuHybridModeAvailable)
            {
                DiagnosticMessage = LocalizationService.Translate(
                    GpuModeText.BlockerMessage(Capabilities.Hybrid.Blocker));
                return;
            }

            await ApplyGpuMuxModeCoreAsync(muxVal, persistPreference: true);
        }

        /// <summary>
        /// The integrated-GPU-only tile. There is no implementation path in this build: nvcpl's
        /// DDS status (0x17) reports Optimus / Automatic / dGPU only, and the Acer MUX offers
        /// Optimus / Discrete / Automatic — neither can turn the dGPU off. Reporting "switched"
        /// would be a fake feature, so the command says what is missing.
        /// </summary>
        [RelayCommand]
        public void SetIntegratedGpuMode()
        {
            DiagnosticMessage = LocalizationService.Translate(
                GpuModeText.BlockerMessage(GpuModeBlocker.NoIgpuOnlyPath));
            AppLogger.Log("GPU modes: integrated (iGPU only) requested but no API exposes that state on this build.");
        }

        private async Task ApplyGpuMuxModeCoreAsync(int muxVal, bool persistPreference)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _gpuSwitchInProgress, 1, 0) != 0)
            {
                DiagnosticMessage = "GPU mode switch already in progress.";
                return;
            }
            System.Threading.Interlocked.Increment(ref _gpuDisplayRevision);
            DiagnosticMessage = $"Switching GPU mode to {DdsTargetName(muxVal)}...";
            IsBusy = true;
            try
            {
                using var hardwareLease = await HardwareOperationGate.EnterDisplaySwitchAsync();
                var current = await Task.Run(() => _hw.IsDdsAvailable ? _hw.GetDdsStatus() : null);
                if (current?.IsSupported != true)
                {
                    DiagnosticMessage = "NVIDIA Advanced Optimus is not available.";
                    return;
                }
                var targetDdsMode = (DdsDisplayMode)muxVal;
                if (current.Matches(targetDdsMode))
                {
                    // Auto may currently route through either GPU. Do not reset its MUX
                    // to the Optimus base or COMMIT an unchanged live driver preference.
                    ApplyDdsStatus(current);
                    if (persistPreference) SaveGpuModePreference(muxVal);
                    DiagnosticMessage = $"GPU mode already set to {DdsTargetName(muxVal)}.";
                    return;
                }
                var prevRenderMode = System.Windows.Media.RenderOptions.ProcessRenderMode;
                try
                {
                    System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    if (System.Windows.Application.Current != null)
                        await _dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    if (_hw is ProductionHardwareService) await Task.Run(NvmlHelper.Shutdown);
                    var (ok, msg) = await Task.Run(() => _hw.SetDdsMode(targetDdsMode));
                    if (!ok)
                    {
                        ApplyDdsStatus(await Task.Run(() => _hw.GetDdsStatus()));
                        DiagnosticMessage = $"Could not switch GPU mode: {msg}";
                        return;
                    }
                    DdsStatus? confirmed = null;
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        confirmed = await Task.Run(() => _hw.GetDdsStatus());
                        if (confirmed?.Matches(targetDdsMode) == true) break;
                        if (attempt < 19) await Task.Delay(250);
                    }
                    ApplyDdsStatus(confirmed);
                    if (confirmed?.Matches(targetDdsMode) != true)
                    {
                        bool blocked = await Task.Run(() => _hw.HasDdsBlockers());
                        DiagnosticMessage = blocked
                            ? "GPU switch blocked by an app. Close it and try again."
                            : "GPU mode change was not confirmed.";
                        return;
                    }
                    // Adapter handles acquired before re-enable may still be invalid.
                    await Task.Run(_hw.RefreshGpuAdapter);
                    // The panel moved: re-read which adapter owns it before the next identification.
                    GpuDisplayRoute.Invalidate();
                    if (persistPreference)
                        SaveGpuModePreference(muxVal);
                    DiagnosticMessage = $"GPU mode set to {DdsTargetName(muxVal)}.";
                    var display = await Task.Run(() => (_hw.GetSupportedRefreshRates(), _hw.GetDisplayRefreshRate()));
                    ApplyRefreshRateOptions(display.Item1, display.Item2);
                }
                finally { System.Windows.Media.RenderOptions.ProcessRenderMode = prevRenderMode; }
            }
            catch (Exception ex) { DiagnosticMessage = $"GPU mode error: {ex.Message}"; }
            finally
            {
                IsBusy = false;
                System.Threading.Volatile.Write(ref _gpuSwitchInProgress, 0);
            }
        }

        [RelayCommand]
        public async Task RestartDiscreteGpuAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _gpuSwitchInProgress, 1, 0) != 0) return;
            System.Threading.Interlocked.Increment(ref _gpuDisplayRevision);
            IsBusy = true;
            DiagnosticMessage = "Power-cycling and restarting Discrete GPU (reboot-free)...";
            try
            {
                bool ok;
                string msg;
                using (await HardwareOperationGate.EnterDisplaySwitchAsync())
                {
                    (ok, msg) = await Task.Run(() => _hw.RestartDiscreteGpu());
                    GpuDisplayRoute.Invalidate();
                    await RefreshGpuModeStateAsync(duringSwitch: true);
                }
                DiagnosticMessage = msg;
                await Prompts.NotifyAsync(
                    "Performix • Discrete GPU Power Cycle",
                    " • Discrete GPU Power Cycle",
                    msg,
                    "OK");
            }
            catch (Exception ex)
            {
                DiagnosticMessage = $"Could not restart the NVIDIA GPU: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                System.Threading.Volatile.Write(ref _gpuSwitchInProgress, 0);
            }
        }

        [RelayCommand]
        public async Task SetBiosDiscreteGpuAsync()
        {
            if (IsBiosDiscreteGpuActive)
            {
                // Not silent: the tile is already the selected one, and a press that does
                // nothing reads as a broken button.
                DiagnosticMessage = LocalizationService.Translate(
                    GpuModeText.BlockerMessage(GpuModeBlocker.BiosDiscreteOnly));
                return;
            }

            // The verdict comes from the capability layer, not from a second ad-hoc MUX probe:
            // "no MUX" and "dGPU direct is unavailable" must never disagree on screen.
            if (!IsGpuDiscreteModeAvailable)
            {
                DiagnosticMessage = LocalizationService.Translate(
                    GpuModeText.BlockerMessage(Capabilities.Discrete.Blocker));
                return;
            }

            await SetBiosGpuModeAsync(
                PreySense.Helpers.AcerWmi.GpuMux.Discrete,
                "Would you like to switch to dGPU Only mode and restart now?",
                "Performix • Hardware Discrete GPU",
                " • Hardware Display MUX");
        }

        private async Task SetBiosGpuModeAsync(int muxMode, string message, string title, string subtitle)
        {
            if (!await ConfirmGpuRestartAsync(message, title, subtitle))
                return;

            IsBusy = true;
            try
            {
                bool ok = await Task.Run(() => _hw.SetGpuMuxMode(muxMode));
                if (ok)
                {
                    IsBiosDiscreteGpuActive = muxMode == PreySense.Helpers.AcerWmi.GpuMux.Discrete;
                    NotifyGpuSelectionsChanged();
                    DiagnosticMessage = "Restarting to apply the GPU mode...";
                    RestartToApplyGpuMode();
                }
                else
                {
                    DiagnosticMessage = "Could not set the GPU mode.";
                }
            }
            catch (Exception ex)
            {
                DiagnosticMessage = $"GPU mode error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<bool> ConfirmGpuRestartAsync(string message, string title, string subtitle)
            => await Prompts.ConfirmAsync(title, subtitle, message, "Apply & Restart Now", "Cancel");

        private static void RestartToApplyGpuMode()
        {
            // /t 0 and no comment. A delayed shutdown with /c opens a second Windows dialog.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0 /f",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }


    }

    /// <summary>
    /// The sentences the three GPU tiles and their commands use, as English source strings only.
    /// The WinUI shell renders them through Loc.T (the zh-CN rows live in the shell's
    /// LocalizationService); the ViewModel runs them through LocalizationService.Translate where it
    /// has to produce a DiagnosticMessage. One sentence per blocker, so the tooltip of a disabled
    /// tile and the message after pressing it can never drift apart.
    /// </summary>
    internal static class GpuModeText
    {
        public static string TileToolTip(GpuModeAvailability availability) => availability.IsAvailable
            ? availability.Kind switch
            {
                GpuModeKind.Hybrid =>
                    "The integrated GPU drives the display; the discrete GPU renders for apps that ask for it.",
                GpuModeKind.Integrated =>
                    "Only the integrated GPU is used; the discrete GPU is switched off.",
                _ =>
                    "The display is wired straight to the discrete GPU."
            }
            : BlockerMessage(availability.Blocker);

        public static string BlockerMessage(GpuModeBlocker blocker) => blocker switch
        {
            GpuModeBlocker.NoHardwareMux =>
                "This machine has no GPU switching hardware (MUX), so only Hybrid mode is available.",
            GpuModeBlocker.NoIgpuOnlyPath =>
                "No NVIDIA or firmware API on this machine exposes an integrated-GPU-only mode, so it is not supported in this version.",
            GpuModeBlocker.NoHybridPath =>
                "This machine does not report a hybrid display path, so Hybrid mode cannot be selected.",
            GpuModeBlocker.BiosDiscreteOnly =>
                "The machine booted straight into dGPU-only mode, so no integrated GPU can drive the display.",
            _ =>
                "This GPU mode is not available on this machine."
        };
    }
}
