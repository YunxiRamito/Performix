// The four "Interface" switches of the Personalisation page (settings IA v2).
//
// WHAT THESE FOUR SWITCHES ACTUALLY CONTROL
// ----------------------------------------
// The expanded home panel is a stack of rows. Each switch hides its own rows:
//
//   StatusBar       HomePage.TelemetryRow (+ its divider)   CPU / GPU fan RPM, read only
//   GpuBar          HomePage.GpuHeaderRow + GpuGroup        GPU mode header + the four tiles
//   RefreshRateBar  HomePage.DisplayGroup                   auto-refresh chip, Hz picker, mode
//   WidgetBar       the four page entries                   lighting / HUD / fans / tuning
//
// The Settings entry of the widget row is deliberately NOT part of WidgetBar: hiding it would
// leave the panel with no way into Settings, and this window has no other gear.
//
// WHERE THE FLAGS LIVE
// --------------------
// Inside %LOCALAPPDATA%\Predex\appearance.json, the store AppearanceService already owns, as a
// "UiPanels" object next to theme / material / accent. No new file, no new preysense.json field.
// A file written before these switches existed has no "UiPanels" member, which reads as "all
// visible" — exactly the panel every user had until now.
//
// This type is deliberately dependency-free (no WinUI, no dispatcher, no static state), so the
// test project links this file and proves the persistence round-trip
// (tests\Performix.Tests\UiPanelStateTests.cs), the same way it links the update sources.

using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Predex.WinUI.Services
{
    /// <summary>One hideable row (or group of rows) of the expanded home panel.</summary>
    public enum UiPanel
    {
        StatusBar,
        GpuBar,
        RefreshRateBar,
        WidgetBar,
    }

    /// <summary>
    /// Persisted visibility of the four home-panel rows. All four default to visible.
    /// </summary>
    public sealed class UiPanelVisibility
    {
        public bool StatusBar { get; set; } = true;

        public bool GpuBar { get; set; } = true;

        public bool RefreshRateBar { get; set; } = true;

        public bool WidgetBar { get; set; } = true;

        public bool Get(UiPanel panel) => panel switch
        {
            UiPanel.StatusBar => StatusBar,
            UiPanel.GpuBar => GpuBar,
            UiPanel.RefreshRateBar => RefreshRateBar,
            UiPanel.WidgetBar => WidgetBar,
            _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, "unknown home panel"),
        };

        public void Set(UiPanel panel, bool visible)
        {
            switch (panel)
            {
                case UiPanel.StatusBar: StatusBar = visible; break;
                case UiPanel.GpuBar: GpuBar = visible; break;
                case UiPanel.RefreshRateBar: RefreshRateBar = visible; break;
                case UiPanel.WidgetBar: WidgetBar = visible; break;
                default: throw new ArgumentOutOfRangeException(nameof(panel), panel, "unknown home panel");
            }
        }

        /// <summary>True when every row is visible, i.e. the panel shows its full content.</summary>
        public bool AllVisible => StatusBar && GpuBar && RefreshRateBar && WidgetBar;

        /// <summary>The JSON object written as the "UiPanels" member of appearance.json.</summary>
        public JsonObject ToNode() => new()
        {
            ["StatusBar"] = StatusBar,
            ["GpuBar"] = GpuBar,
            ["RefreshRateBar"] = RefreshRateBar,
            ["WidgetBar"] = WidgetBar,
        };

        public string ToJson() => ToNode().ToJsonString();

        /// <summary>
        /// Reads the object back. Null, empty or malformed input yields the default (all
        /// visible) instead of throwing: a damaged appearance.json must never take the shell
        /// down with it (AppearanceService.LoadPersisted has the same contract).
        /// </summary>
        public static UiPanelVisibility FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new UiPanelVisibility();
            try
            {
                return FromNode(JsonNode.Parse(json));
            }
            catch (JsonException)
            {
                return new UiPanelVisibility();
            }
        }

        private static UiPanelVisibility FromNode(JsonNode? node)
        {
            var panels = new UiPanelVisibility();
            if (node is not JsonObject obj) return panels;

            panels.StatusBar = ReadBool(obj, nameof(StatusBar), panels.StatusBar);
            panels.GpuBar = ReadBool(obj, nameof(GpuBar), panels.GpuBar);
            panels.RefreshRateBar = ReadBool(obj, nameof(RefreshRateBar), panels.RefreshRateBar);
            panels.WidgetBar = ReadBool(obj, nameof(WidgetBar), panels.WidgetBar);
            return panels;
        }

        private static bool ReadBool(JsonObject obj, string name, bool fallback)
        {
            if (!obj.TryGetPropertyValue(name, out JsonNode? value) || value is null) return fallback;
            return value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            };
        }

        public UiPanelVisibility Clone() => new()
        {
            StatusBar = StatusBar,
            GpuBar = GpuBar,
            RefreshRateBar = RefreshRateBar,
            WidgetBar = WidgetBar,
        };
    }

    /// <summary>
    /// The "UiPanels" member of appearance.json, read and written without owning the rest of the
    /// document: the theme / material / accent members are serialised by AppearanceService and
    /// must survive untouched.
    /// </summary>
    public static class UiPanelDocument
    {
        public const string MemberName = "UiPanels";

        /// <summary>
        /// Reads the member out of a whole appearance.json text. A missing member, invalid JSON
        /// or a non-object value all read as "all visible" — never an exception, because this
        /// runs during startup.
        /// </summary>
        public static UiPanelVisibility Read(string? appearanceJson)
        {
            if (string.IsNullOrWhiteSpace(appearanceJson)) return new UiPanelVisibility();
            try
            {
                if (JsonNode.Parse(appearanceJson) is not JsonObject root) return new UiPanelVisibility();
                if (!root.TryGetPropertyValue(MemberName, out JsonNode? value)) return new UiPanelVisibility();
                return value is null ? new UiPanelVisibility() : UiPanelVisibility.FromJson(value.ToJsonString());
            }
            catch (JsonException)
            {
                return new UiPanelVisibility();
            }
        }

        /// <summary>
        /// Writes the member into a whole appearance.json text, preserving every other member and
        /// every property the running build does not know about. Empty or invalid input starts a
        /// fresh object.
        /// </summary>
        public static string Write(string? appearanceJson, UiPanelVisibility panels)
        {
            JsonObject root;
            try
            {
                root = string.IsNullOrWhiteSpace(appearanceJson)
                    ? new JsonObject()
                    : JsonNode.Parse(appearanceJson) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject();
            }

            root[MemberName] = panels.ToNode();
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
