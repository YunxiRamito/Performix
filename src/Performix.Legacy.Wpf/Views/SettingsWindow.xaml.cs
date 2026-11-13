using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PreySense.Frontend.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (DataContext is ViewModels.MainViewModel vm) await vm.RefreshStartupPreferenceAsync();
            };
            UpdateUsbChargingLabel();
            LanguageSelector.SelectedIndex = Services.LocalizationService.LanguageIndex;
            // Keep the resizable window inside the work area on every monitor/DPI.
            double workHeight = SystemParameters.WorkArea.Height;
            double workWidth = SystemParameters.WorkArea.Width;
            MaxHeight = Math.Max(320, workHeight - 40);
            MaxWidth = Math.Max(460, workWidth - 40);
            Height = Math.Min(Height, MaxHeight);
            MinHeight = Math.Min(MinHeight, MaxHeight);
        }

        private void UsbCharging_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateUsbChargingLabel();
        }

        private void UpdateUsbChargingLabel()
        {
            UsbChargingSlider.ValueText = UsbChargingSlider.Value == 0
                ? Services.LocalizationService.Translate("Disabled") : $"{UsbChargingSlider.Value:0}%";
        }

        private async void UsbCharging_MouseReleased(object sender, MouseButtonEventArgs e) => await CommitUsbChargingAsync();
        private async void UsbCharging_MouseCaptureLost(object sender, MouseEventArgs e) => await CommitUsbChargingAsync();
        private async void UsbCharging_KeyReleased(object sender, KeyEventArgs e) => await CommitUsbChargingAsync();

        private async System.Threading.Tasks.Task CommitUsbChargingAsync()
        {
            if (DataContext is ViewModels.MainViewModel vm)
                await vm.SetUsbChargingAsync((int)UsbChargingSlider.Value);
        }

        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageSelector.SelectedItem is ComboBoxItem item && item.Tag is string language)
            {
                Services.LocalizationService.SetLanguage(language);
                UpdateUsbChargingLabel();
            }
        }

    }
}
