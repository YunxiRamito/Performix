// Services — settings sub-page code-behind (settings IA v2).
//
// One switch (the OEM service debloater Core implements for Acer) plus a brand-aware, read-only
// view of the OEM services this machine actually has.
//
// BRAND
//   OemBrandProbe.Detect() reads the vendor once per process (BIOS registry keys, WMI fallback)
//   and OemBrandCatalog turns it into the vendor's official spelling. Every string on this page
//   that names the vendor goes through one formatted key — "{0} services" — so the shell title
//   bar, the Settings entry, this row and the list heading cannot disagree.
//
//   Unidentified vendor: neutral wording ("Vendor services") plus the reason. Guessing a brand
//   would name the wrong vendor's services, which is worse than saying nothing, and the switch is
//   disabled because stopping the wrong vendor's services is exactly the kind of irreversible
//   action this page must not do.
//
// ACTION
//   Only Acer has a backend in this build (Core's AcerServiceDebloater, which also kills the OEM
//   processes and removes the OEM scheduled tasks). For every other brand the switch is disabled
//   and the page says so; the list still shows that vendor's real services, filtered by
//   OemBrandCatalog, so an Acer list can never appear on a Lenovo.
//
// PSSvc stays protected: OemBrandCatalog.ProtectedServices plus Core's own exclusion list keep it
// out of the managed set, and the page states that instead of leaving it in a code comment.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Predex.WinUI.Services;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Predex.WinUI.Pages
{
    /// <summary>
    /// Services settings: the OEM background services switch and the brand-filtered service list,
    /// bound against <see cref="MainViewModel"/>.
    /// </summary>
    public sealed partial class ServicesPage : Page, INotifyPropertyChanged, IPredexPage
    {
        private readonly OemIdentity _identity = OemBrandProbe.Detect();
        private List<OemServiceEntry> _services = [];

        /// <summary>True while a toggle is being applied (keeps the danger line visible).</summary>
        private bool _switching;

        /// <summary>
        /// True after the user confirmed a stop in THIS session. The danger colour follows this
        /// (and a toggle in flight), not the persisted debloat flag: a state inherited from a
        /// previous run is reported in the secondary colour, so opening the page does not look
        /// like an alarm (UX review item 3).
        /// </summary>
        private bool _stoppedInThisSession;

        public ServicesPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── Page contract ────────────────────────────────────────────────────────
        private MainViewModel? _vm;

        public MainViewModel? Vm
        {
            get => _vm;
            set
            {
                if (ReferenceEquals(_vm, value)) return;
                if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm = value;
                if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
                OnPropertyChanged(nameof(Vm));
            }
        }

        // ── Brand-derived text (one key, three places) ───────────────────────────
        /// <summary>The brand in the page. Also used by the shell for the title bar.</summary>
        public OemBrand Brand => _identity.Brand;

        /// <summary>Vendor in its official spelling; null when the machine was not identified.</summary>
        private string? BrandName => _identity.BrandName;

        /// <summary>Row + page label: "ASUS services", or the neutral "Vendor services".</summary>
        public string RowLabel => ServicesTitle.For(_identity);

        public string ListTitle => BrandName is null
            ? Loc.T("Services found on this machine")
            : LocalizationService.Format("Detected {0} services", BrandName);

        public string ServiceCountText => _services.Count.ToString();

        public bool CanManage => _identity.CanManageServices;

        /// <summary>
        /// The consequence of the action, in the vendor's own product name. The wording is
        /// brand-aware because "the official Predator Sense app" means nothing on an ASUS.
        ///
        /// UX review item 3: this is the PREVIEW (the switch is off, the services still run), so
        /// the page renders it in the secondary colour. Once the services are stopped the tense
        /// changes and the line turns danger-coloured — see <see cref="StoppedText"/>.
        /// </summary>
        public string ConsequenceText
        {
            get
            {
                string? app = OemBrandCatalog.ControlAppName(_identity.Brand);
                if (BrandName is null || app is null)
                {
                    return Loc.T("Stopping the wrong vendor's services can break hotkeys, lighting and fan control, so this stays off until the vendor is identified.");
                }

                return LocalizationService.Format(
                    "Stopping these services also stops {0}, the {1} hotkeys and part of the OEM fan control until you turn them back on.",
                    app, BrandName);
            }
        }

        /// <summary>
        /// The same fact once it is true: the services really are stopped. Shown in the danger
        /// colour, and only in that state (or while the switch is being applied).
        /// </summary>
        public string StoppedText
        {
            get
            {
                string? app = OemBrandCatalog.ControlAppName(_identity.Brand);
                if (BrandName is null || app is null) return ConsequenceText;

                return LocalizationService.Format(
                    "{0} is stopped now: the {1} hotkeys and part of the OEM fan control are unavailable until you turn this back on.",
                    app, BrandName);
            }
        }

        /// <summary>True while the switch is off, i.e. the OEM services are stopped by Performix.</summary>
        private bool ServicesStopped => Vm?.IsAcerServicesDebloated ?? false;

        /// <summary>The preview line: only while nothing has been stopped.</summary>
        public Visibility ConsequenceVisibility
            => ServicesStopped ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// The settled "they are stopped" line, in the secondary colour: the state the page
        /// opens in must not look like a fault (UX review item 3).
        /// </summary>
        public Visibility StoppedVisibility
            => ServicesStopped && !IsAlarm ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// The danger colour is reserved for the risky moment: a toggle in flight, or a stop the
        /// user just confirmed in this session. A stopped state inherited from a previous run is
        /// a fact to report, not an alarm to raise on arrival.
        /// </summary>
        public Visibility DangerVisibility
            => ServicesStopped && IsAlarm ? Visibility.Visible : Visibility.Collapsed;

        private bool IsAlarm => _switching || _stoppedInThisSession;

        /// <summary>Shown only while the switch is disabled; names the concrete reason.</summary>
        public string BlockedReason
        {
            get
            {
                if (BrandName is null)
                {
                    return LocalizationService.Format(
                        "The vendor could not be identified ({0}), so no vendor service is touched here.",
                        _identity.SystemManufacturer ?? Loc.T("unknown"));
                }

                return LocalizationService.Format(
                    "Managing {0} services is not implemented in this build: the list below is read only.",
                    BrandName);
            }
        }

        public Visibility BlockedVisibility
            => CanManage ? Visibility.Collapsed : Visibility.Visible;

        public string EmptyText => BrandName is null
            ? Loc.T("The vendor could not be identified, so no service list is shown.")
            : LocalizationService.Format("No {0} services were found on this machine.", BrandName);

        public Visibility EmptyVisibility
            => _services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // ── Lifetime ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            ReloadServices();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
            => LocalizationService.LanguageChanged -= OnLanguageChanged;

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            Bindings.Update();
            RenderServices();
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.IsAcerServicesEnabled)
                or nameof(MainViewModel.IsAcerServicesDebloated))
            {
                Bindings.Update();
                RenderServices();
            }
        }

        // ── Service list (read only) ─────────────────────────────────────────────
        private void ReloadServices()
        {
            _services = OemBrandProbe.ListServices(_identity.Brand);
            OnPropertyChanged(nameof(EmptyVisibility));
            OnPropertyChanged(nameof(ServiceCountText));
            RenderServices();
            Bindings.Update();
        }

        /// <summary>
        /// Builds one row per detected service. Done in code because the list is machine data: a
        /// generated XAML item list would have to bind every row anyway, and this page refreshes
        /// it once per load.
        /// </summary>
        private void RenderServices()
        {
            if (PnlServices is null) return;
            PnlServices.Children.Clear();

            foreach (OemServiceEntry service in _services)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4), ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Name + one purpose line. UX review item 7: a bare list of internal
                // identifiers (AASSvc, AcerCCAgentSvis …) gives nobody a basis for deciding
                // what is safe to stop; a service whose purpose this build does not know says
                // "Unknown purpose" rather than getting an invented description.
                var label = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };

                var name = new TextBlock
                {
                    Text = service.DisplayName,
                    Style = (Style)Resources["ServiceNameStyle"],
                };
                ToolTipService.SetToolTip(name, service.Name);
                AutomationProperties.SetAutomationId(name, "TxtServiceName");

                var purpose = new TextBlock
                {
                    Text = PurposeText(service),
                    Style = (Style)Resources["ServicePurposeStyle"],
                };
                AutomationProperties.SetAutomationId(purpose, "TxtServicePurpose");

                label.Children.Add(name);
                label.Children.Add(purpose);
                Grid.SetColumn(label, 0);

                var state = new TextBlock
                {
                    Text = StateText(service),
                    Style = (Style)Resources["ServiceStateStyle"],
                };
                AutomationProperties.SetAutomationId(state, "TxtServiceState");
                Grid.SetColumn(state, 1);

                row.Children.Add(label);
                row.Children.Add(state);
                PnlServices.Children.Add(row);
            }
        }

        /// <summary>The English purpose line for a known service, or the explicit unknown marker.</summary>
        private static string PurposeText(OemServiceEntry service)
        {
            string? purpose = OemBrandCatalog.ServicePurpose(service.Name);
            return purpose is null ? Loc.T("Unknown purpose") : Loc.T(purpose);
        }

        /// <summary>
        /// UX review item 8: "Stopped" used to cover three different situations. Each row now
        /// says which one it is — Performix stopped it, it simply was not running, or this
        /// machine does not have it at all.
        /// </summary>
        private string StateText(OemServiceEntry service)
        {
            if (!service.IsInstalled) return Loc.T("Not installed");
            if (service.IsRunning) return Loc.T("Running");
            // The switch state is the honest discriminator: it is reconciled against SCM at
            // startup (MainViewModel → AcerServiceDebloater.AreAllServicesDisabled).
            if (ServicesStopped) return Loc.T("Stopped by Performix");
            return service.IsDisabled ? Loc.T("Disabled") : Loc.T("Not running");
        }

        // ── OEM Services (OneWay IsChecked + command flips the state) ───────────
        //
        // UX review item 2: switching the OEM services off is one click that stops and disables
        // every service of the vendor (Core's AcerServiceDebloater also kills the OEM processes
        // and removes the OEM scheduled tasks). That is a system-level, batch, destructive
        // action, so it asks first — with the consequences spelled out and Cancel as the
        // default button (Enter/Esc both back out).
        private async void AcerServices_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch toggle || Vm is not { } vm) return;

            ServicesToggleAction action = ServicesToggleGuard.Classify(
                toggle.IsOn, vm.IsAcerServicesEnabled, CanManage);
            if (action == ServicesToggleAction.Ignore)
            {
                // Binding echo (or an unmanageable vendor): put the control back on the truth.
                Bindings.Update();
                return;
            }

            if (action == ServicesToggleAction.ConfirmThenApply &&
                !ServicesToggleGuard.MayProceed(await ConfirmStopServicesAsync()))
            {
                Bindings.Update();
                return;
            }

            _switching = true;
            Bindings.Update();
            try { await vm.ToggleAcerServicesDebloaterCommand.ExecuteAsync(null); }
            finally
            {
                _switching = false;
                // A stop the user just confirmed stays flagged for this session, so the red line
                // is still there when the list repaints with the new states. Read the ViewModel,
                // not the switch: a failed operation must not raise the alarm.
                _stoppedInThisSession = vm.IsAcerServicesDebloated;
                Bindings.Update();
                ReloadServices();   // the state column has to follow the switch
            }
        }

        /// <summary>
        /// The confirmation in front of the batch stop. Returns false when the user cancels or
        /// the dialog cannot be shown (the safe answer for a destructive action).
        /// </summary>
        private async Task<bool> ConfirmStopServicesAsync()
        {
            var body = new StackPanel { Spacing = 6, MaxWidth = 320 };

            body.Children.Add(new TextBlock
            {
                Text = ConsequenceText,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("PageTextPrimaryBrush"),
            });
            body.Children.Add(new TextBlock
            {
                Text = Loc.T("PSSvc is never stopped or disabled: fan RPM and mode switching depend on it."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("PageTextSecondaryBrush"),
            });
            body.Children.Add(new TextBlock
            {
                Text = Loc.T("You can turn them back on from this page at any time."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("PageTextSecondaryBrush"),
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = LocalizationService.Format("Stop {0} services?", BrandName ?? Loc.T("Vendor services")),
                Content = body,
                PrimaryButtonText = Loc.T("Stop services"),
                CloseButtonText = Loc.T("Cancel"),
                // Cancel is the default: Enter and Esc both leave the machine alone.
                DefaultButton = ContentDialogButton.Close,
            };
            // Destructive confirmation: the same red primary button the GPU MUX prompt uses.
            if (Application.Current?.Resources.TryGetValue("DangerPrimaryButtonStyle", out object? style) == true &&
                style is Style danger)
            {
                dialog.PrimaryButtonStyle = danger;
            }

            try
            {
                ContentDialogResult result = await dialog.ShowAsync();
                ShellLog.Write("services",
                    $"confirm stop OEM services: result={result} confirmed={result == ContentDialogResult.Primary}");
                return result == ContentDialogResult.Primary;
            }
            catch (Exception ex)
            {
                ShellLog.Write("services", $"confirm stop OEM services failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key)
            => Application.Current?.Resources.TryGetValue(key, out object? value) == true &&
               value is Microsoft.UI.Xaml.Media.Brush brush
                ? brush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
