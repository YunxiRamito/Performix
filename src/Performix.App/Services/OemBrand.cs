// Vendor identity for the Services page (settings IA v2).
//
// The Services page must call the machine's vendor by its own name: "Acer 服务" on an Acer,
// "ASUS 服务" on an ASUS, and a neutral "厂商服务 / Vendor services" when the machine cannot be
// identified. It must also list that vendor's services, never another vendor's.
//
// This file is the pure half: manufacturer string → brand, and brand → the tokens that decide
// which installed service belongs to it. No WinUI, no WMI, no registry, no static state — the
// test project links this file and proves the mapping (tests\Performix.Tests\OemBrandTests.cs).
// The probing half lives in Services\OemBrandProbe.cs.
//
// OFFICIAL SPELLINGS are part of the contract, not cosmetics: vendors write "ASUS", not "Asus";
// "GIGABYTE", not "Gigabyte"; "MSI" for Micro-Star; "HP" for Hewlett-Packard; "Lenovo", "Dell",
// "Alienware", "Razer". A wrong capitalisation reads as a third-party product.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Predex.WinUI.Services
{
    /// <summary>A PC vendor the Services page can name.</summary>
    public enum OemBrand
    {
        /// <summary>Not identified: neutral wording, no guessed name.</summary>
        Unknown,
        Acer,
        Asus,
        Lenovo,
        Hp,
        Dell,
        Alienware,
        Msi,
        Razer,
        Gigabyte,
        Samsung,
        Microsoft,
        Huawei,
        Xiaomi,
        Toshiba,
        Fujitsu,
    }

    public static class OemBrandCatalog
    {
        /// <summary>
        /// Services that must never be stopped or disabled, whatever the vendor. PSSvc stays on
        /// this list for the reason Core documents (AcerServiceDebloater.ExcludedServiceNames):
        /// it owns the Predator named pipe that fan RPM and mode switching depend on, and its
        /// display name "Predator Service" would otherwise match the discovery rule.
        /// </summary>
        public static readonly IReadOnlySet<string> ProtectedServices =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PreySense",
                "PSSvc",
                "Psched",
                "IntcAzAudAddService",
                "DtsApo4Service",
                "AcerApplicationBaseDriver_Device",
            };

        /// <summary>
        /// Brand name in the vendor's own spelling. Null for <see cref="OemBrand.Unknown"/>: the
        /// caller must use the neutral wording rather than invent a name.
        /// </summary>
        public static string? DisplayName(OemBrand brand) => brand switch
        {
            OemBrand.Acer => "Acer",
            OemBrand.Asus => "ASUS",
            OemBrand.Lenovo => "Lenovo",
            OemBrand.Hp => "HP",
            OemBrand.Dell => "Dell",
            OemBrand.Alienware => "Alienware",
            OemBrand.Msi => "MSI",
            OemBrand.Razer => "Razer",
            OemBrand.Gigabyte => "GIGABYTE",
            OemBrand.Samsung => "Samsung",
            OemBrand.Microsoft => "Microsoft",
            OemBrand.Huawei => "HUAWEI",
            OemBrand.Xiaomi => "Xiaomi",
            OemBrand.Toshiba => "Toshiba",
            OemBrand.Fujitsu => "Fujitsu",
            _ => null,
        };

        /// <summary>
        /// The vendor's control app, named in the consequence line ("the official Predator Sense
        /// app"). Null when the brand is unknown or has no single well-known app.
        /// </summary>
        public static string? ControlAppName(OemBrand brand) => brand switch
        {
            OemBrand.Acer => "Predator Sense",
            OemBrand.Asus => "Armoury Crate",
            OemBrand.Lenovo => "Lenovo Vantage",
            OemBrand.Hp => "OMEN Gaming Hub",
            OemBrand.Dell => "Dell SupportAssist",
            OemBrand.Alienware => "Alienware Command Center",
            OemBrand.Msi => "MSI Center",
            OemBrand.Razer => "Razer Synapse",
            OemBrand.Gigabyte => "GIGABYTE Control Center",
            _ => null,
        };

        /// <summary>
        /// Maps the two manufacturer strings to a brand. Both are consulted because they
        /// disagree on plenty of machines (the board maker on an OEM-built chassis), and the
        /// system value is the more authoritative of the two.
        ///
        /// Order matters: Alienware boards report "Dell Inc.", so Alienware is tested before
        /// Dell; "Micro-Star" must not be caught by a generic "star".
        /// </summary>
        public static OemBrand Detect(string? systemManufacturer, string? baseBoardManufacturer)
        {
            OemBrand fromSystem = DetectSingle(systemManufacturer);
            if (fromSystem != OemBrand.Unknown) return fromSystem;
            return DetectSingle(baseBoardManufacturer);
        }

        /// <summary>Brand from one manufacturer string; <see cref="OemBrand.Unknown"/> if no match.</summary>
        public static OemBrand DetectSingle(string? manufacturer)
        {
            if (string.IsNullOrWhiteSpace(manufacturer)) return OemBrand.Unknown;

            ReadOnlySpan<char> raw = manufacturer.AsSpan().Trim();
            // A manufacturer field can be a placeholder on a hand-built box: those are not
            // vendors, and treating them as one would name the wrong OEM.
            if (Contains(raw, "to be filled") || Contains(raw, "system manufacturer")
                || Contains(raw, "o.e.m") || Contains(raw, "default string")) return OemBrand.Unknown;

            if (Contains(raw, "alienware")) return OemBrand.Alienware;
            if (Contains(raw, "acer") || Contains(raw, "predator")) return OemBrand.Acer;
            if (Contains(raw, "asus") || Contains(raw, "asustek")) return OemBrand.Asus;
            if (Contains(raw, "lenovo")) return OemBrand.Lenovo;
            if (Contains(raw, "hewlett") || Contains(raw, "hp inc") || EqualsToken(raw, "hp")) return OemBrand.Hp;
            if (Contains(raw, "dell")) return OemBrand.Dell;
            if (Contains(raw, "micro-star") || Contains(raw, "micro star") || EqualsToken(raw, "msi")) return OemBrand.Msi;
            if (Contains(raw, "razer")) return OemBrand.Razer;
            if (Contains(raw, "gigabyte") || Contains(raw, "giga-byte") || Contains(raw, "aorus")) return OemBrand.Gigabyte;
            if (Contains(raw, "samsung")) return OemBrand.Samsung;
            if (Contains(raw, "microsoft")) return OemBrand.Microsoft;
            if (Contains(raw, "huawei")) return OemBrand.Huawei;
            if (Contains(raw, "xiaomi")) return OemBrand.Xiaomi;
            if (Contains(raw, "toshiba") || Contains(raw, "dynabook")) return OemBrand.Toshiba;
            if (Contains(raw, "fujitsu")) return OemBrand.Fujitsu;
            return OemBrand.Unknown;
        }

        /// <summary>
        /// One-line purpose for the OEM services this page can explain, keyed by service name.
        /// The value is the English source string, i.e. the localization key the page passes
        /// through LocalizationService.
        ///
        /// UX review item 7: a list of fourteen raw identifiers (AASSvc, AcerCCAgentSvis,
        /// AcerLightingService …) does not let anyone decide what is safe to stop. Only services
        /// whose job is actually known appear here — anything else gets the page's "Unknown
        /// purpose" row instead of an invented description.
        /// </summary>
        private static readonly Dictionary<string, string> ServicePurposes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["AASSvc"] = "Macro and hotkey support",
                ["ASMSvc"] = "Hotkeys and system monitoring",
                ["AcerLightingService"] = "Lighting control (keyboard and light bars)",
                ["AcerQAAgentSvis"] = "Quick Access shortcuts",
                ["AcerCCAgentSvis"] = "Care Center background agent",
                ["AcerDIAgentSvis"] = "Device information agent",
                ["AcerDeviceEnablingServiceV2"] = "Enables Acer peripherals and sensors",
                ["ADESvc"] = "Enables Acer peripherals and sensors",
                ["AcerServiceSvc"] = "General Acer background service",
                ["AcerCentralService"] = "Acer central service",
                ["AcerSysHardwareService"] = "Hardware control (fan and performance)",
                ["AcerHardwareService"] = "Hardware control (fan and performance)",
                ["PredatorSenseService"] = "Predator Sense main service",
                ["AICO_svc"] = "Acer smart assistant",
                ["AICO2_svc"] = "Acer smart assistant",
                ["PSSvc"] = "Fan speed and mode switching (protected)",
            };

        /// <summary>
        /// The English purpose line for a service, or null when this build does not know it.
        /// Null is the answer the page renders as "Unknown purpose" — never a guess.
        /// </summary>
        public static string? ServicePurpose(string? serviceName) =>
            string.IsNullOrWhiteSpace(serviceName)
                ? null
                : ServicePurposes.TryGetValue(serviceName, out string? purpose) ? purpose : null;

        /// <summary>
        /// Service-name / display-name tokens per brand. A service belongs to the brand when its
        /// name or its display name carries one of the tokens — that is how each vendor's set is
        /// discovered without hard-coding a machine-specific list a driver update would
        /// invalidate. <see cref="OemBrand.Unknown"/> matches nothing: no brand, no list.
        /// </summary>
        public static IReadOnlyList<string> ServiceTokens(OemBrand brand) => brand switch
        {
            // Core's AcerServiceDebloater.ManagedServices covers the same set; these tokens also
            // catch the dynamically discovered names it accepts.
            OemBrand.Acer => ["acer", "predator", "aassvc", "asmsvc", "aico"],
            // ASUS services ship as ASUS* / Asus*, with Armoury Crate and ROG as product names.
            OemBrand.Asus => ["asus", "armoury", "rog live", "atk"],
            OemBrand.Lenovo => ["lenovo", "vantage", "imcontroller"],
            OemBrand.Hp => ["hewlett", "hp inc", "hp ", "hpq", "hpsupport", "hpcomm"],
            OemBrand.Dell => ["dell", "supportassist"],
            OemBrand.Alienware => ["alienware", "awcc"],
            OemBrand.Msi => ["msi", "dragon center", "dragoncenter", "micro star"],
            OemBrand.Razer => ["razer", "rzaction", "rzupdater"],
            OemBrand.Gigabyte => ["gigabyte", "aorus"],
            OemBrand.Samsung => ["samsung"],
            OemBrand.Microsoft => ["surface"],
            OemBrand.Huawei => ["huawei"],
            OemBrand.Xiaomi => ["xiaomi"],
            OemBrand.Toshiba => ["toshiba", "dynabook"],
            OemBrand.Fujitsu => ["fujitsu"],
            _ => [],
        };

        /// <summary>
        /// True when the service belongs to the brand's OEM set. Protected services never match,
        /// whatever their display name says.
        /// </summary>
        public static bool IsBrandService(OemBrand brand, string? serviceName, string? displayName)
        {
            if (brand == OemBrand.Unknown) return false;
            if (!string.IsNullOrWhiteSpace(serviceName) && ProtectedServices.Contains(serviceName))
                return false;

            IReadOnlyList<string> tokens = ServiceTokens(brand);
            if (tokens.Count == 0) return false;

            string haystack = ((serviceName ?? string.Empty) + " " + (displayName ?? string.Empty))
                .ToLowerInvariant();
            return tokens.Any(token => haystack.Contains(token, StringComparison.Ordinal));
        }

        /// <summary>
        /// Keeps only this brand's services out of an (name, displayName) sequence. Ordering is by
        /// display name so the list is stable between reloads.
        /// </summary>
        public static List<OemServiceRef> Filter(
            OemBrand brand, IEnumerable<OemServiceRef> services)
            => services
                .Where(service => IsBrandService(brand, service.Name, service.DisplayName))
                .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static bool Contains(ReadOnlySpan<char> value, string token)
            => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool EqualsToken(ReadOnlySpan<char> value, string token)
            => value.Equals(token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One installed service as the probe reports it.</summary>
    public sealed record OemServiceRef(string Name, string DisplayName);
}
