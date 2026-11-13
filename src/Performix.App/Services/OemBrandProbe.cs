// The probing half of the brand detection: read the machine's vendor, then list its OEM
// services. OemBrand.cs holds the pure mapping tables (official brand names, service tokens,
// protected services); this file is the only part that touches Windows.
//
// SOURCE OF THE BRAND — real probes only, never a guess:
//   HKLM\HARDWARE\DESCRIPTION\System\BIOS\SystemManufacturer    (what Win32_ComputerSystem
//                                                               .Manufacturer returns)
//   HKLM\HARDWARE\DESCRIPTION\System\BIOS\BaseBoardManufacturer (the board's own field; covers
//                                                               machines whose system field is
//                                                               the ODM builder instead)
//   WMI Win32_ComputerSystem.Manufacturer / Win32_BaseBoard.Manufacturer as the fallback when
//   the registry value is absent.
// Both reads are read-only and cheap. If neither yields a recognisable vendor the brand stays
// Unknown and the page says so in neutral words — a guessed brand is worse than no brand.
//
// Caching: the vendor cannot change while the process runs, and the shell drops the whole page
// tree every time the window hides to the tray (S6), so an uncached probe would hit the registry
// on every show. The service list is NOT cached: it is the live state of the machine.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;
using PreySense.Core.Services;

namespace Predex.WinUI.Services
{
    /// <summary>What the Services page needs to know about the machine it runs on.</summary>
    public sealed record OemIdentity(
        OemBrand Brand,
        string? SystemManufacturer,
        string? BaseBoardManufacturer)
    {
        /// <summary>Vendor name in the vendor's own spelling, or null when not identified.</summary>
        public string? BrandName => OemBrandCatalog.DisplayName(Brand);

        public bool IsIdentified => BrandName is not null;

        /// <summary>
        /// True only for the vendors this build can actually act on. Stopping OEM services is a
        /// vendor-specific operation and Core implements the Acer set (services + OEM processes +
        /// OEM scheduled tasks); every other brand lists its services read-only and the page says
        /// the action is not implemented instead of pretending.
        /// </summary>
        public bool CanManageServices => Brand == OemBrand.Acer;
    }

    /// <summary>One OEM service the brand filter matched, with its live state.</summary>
    /// <param name="IsInstalled">
    /// False when the name came from a static candidate list but the service controller cannot
    /// be opened — the service is listed in this build's catalogue, not on this machine. The
    /// page needs that to tell "not running" apart from "not here at all" (UX review item 8).
    /// </param>
    public sealed record OemServiceEntry(
        string Name,
        string DisplayName,
        bool IsRunning,
        bool IsDisabled,
        bool IsInstalled);

    /// <summary>
    /// The one place the brand-named page title is composed, so the shell title bar, the Settings
    /// entry and the Services page itself cannot drift: "{0} services" through
    /// LocalizationService, or the neutral "Vendor services" when the vendor is unknown.
    /// </summary>
    public static class ServicesTitle
    {
        public static string For(OemIdentity identity)
        {
            string? brand = identity.BrandName;
            return brand is null
                ? PreySense.Frontend.Services.LocalizationService.Translate("Vendor services")
                : PreySense.Frontend.Services.LocalizationService.Format("{0} services", brand);
        }
    }

    internal static class OemBrandProbe
    {
        private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

        private static OemIdentity? _cached;

        /// <summary>Detected vendor for this process. Never throws.</summary>
        public static OemIdentity Detect()
        {
            if (_cached is not null) return _cached;

            string? system = ReadRegistry("SystemManufacturer") ?? ReadWmi("Win32_ComputerSystem", "Manufacturer");
            string? board = ReadRegistry("BaseBoardManufacturer") ?? ReadWmi("Win32_BaseBoard", "Manufacturer");

            OemBrand brand = OemBrandCatalog.Detect(system, board);
            _cached = new OemIdentity(brand, system, board);
            ShellLog.Write("services",
                $"brand={brand} ({OemBrandCatalog.DisplayName(brand) ?? "unidentified"}) " +
                $"system='{system ?? "?"}' board='{board ?? "?"}'");
            return _cached;
        }

        /// <summary>
        /// The brand's OEM services that exist on this machine. Acer goes through Core's own
        /// discovery (AcerServiceDebloater.GetAllAcerServiceNames) so the list this page shows is
        /// exactly the set the switch acts on; every other brand is filtered by the token table
        /// in OemBrandCatalog. Unknown brand → empty list.
        /// </summary>
        public static List<OemServiceEntry> ListServices(OemBrand brand)
        {
            if (brand == OemBrand.Unknown) return [];

            List<OemServiceRef> candidates;
            try
            {
                candidates = brand == OemBrand.Acer
                    ? AcerServiceDebloater.GetAllAcerServiceNames()
                        .Select(name => new OemServiceRef(name, name))
                        .ToList()
                    : EnumerateInstalledServices();
            }
            catch (Exception ex)
            {
                ShellLog.Write("services", $"service discovery failed: {ex.GetType().Name}: {ex.Message}");
                return [];
            }

            var entries = new List<OemServiceEntry>();
            foreach (OemServiceRef candidate in OemBrandCatalog.Filter(brand, candidates))
            {
                // The display name is only known after opening the controller; the token filter
                // above already ran on both fields for the discovered set.
                string displayName = candidate.DisplayName;
                bool running = false;
                bool disabled = false;
                bool installed = false;
                try
                {
                    using var controller = new ServiceController(candidate.Name);
                    // Constructing a ServiceController never validates the name: the first real
                    // read is what throws for a service this machine does not have. So
                    // "installed" is only true once that read succeeded — otherwise every name
                    // from the static catalogue would look installed (UX review item 8).
                    string reportedName = string.IsNullOrWhiteSpace(controller.DisplayName)
                        ? candidate.Name
                        : controller.DisplayName;
                    running = controller.Status == ServiceControllerStatus.Running;
                    disabled = controller.StartType == ServiceStartMode.Disabled;
                    installed = true;
                    displayName = reportedName;
                }
                catch
                {
                    // Listed in the catalogue but not openable: keep the row with the neutral
                    // state rather than dropping the evidence that it exists.
                }

                entries.Add(new OemServiceEntry(candidate.Name, displayName, running, disabled, installed));
            }

            return entries
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Every installed service as (name, display name); the filter picks the brand's.</summary>
        private static List<OemServiceRef> EnumerateInstalledServices()
        {
            var services = new List<OemServiceRef>();
            try
            {
                foreach (ServiceController controller in ServiceController.GetServices())
                {
                    using (controller)
                    {
                        try
                        {
                            services.Add(new OemServiceRef(
                                controller.ServiceName,
                                controller.DisplayName ?? string.Empty));
                        }
                        catch
                        {
                            // A service can vanish between enumeration and read.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("services", $"service enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }
            return services;
        }

        private static string? ReadRegistry(string name)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BiosKey);
                string? value = (key?.GetValue(name) as string)?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadWmi(string wmiClass, string property)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
                foreach (ManagementObject instance in searcher.Get())
                {
                    using (instance)
                    {
                        string? value = instance[property] as string;
                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                ShellLog.Write("services", $"WMI {wmiClass}.{property} failed: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }
}
