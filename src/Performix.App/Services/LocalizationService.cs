// WinUI 3 localization service.
//
// S1 rewrote this file: it used to read a `locale.<lang>.json` from the output directory,
// which was a *second* translation source next to the in-production UiText table
// (WINUI3-MIGRATION.md §5.4 — "必须统一：UiText 是唯一文案源"). It now resolves strings
// exactly like the WPF build: `UiText.For(language)` + English pass-through.
//
// The linked ViewModels call Translate()/Format() only, so the public surface stays
// source-compatible with PreySense.Frontend\Services\LocalizationService.cs.
//
// WinUIShellText is a small additive table for keys that only exist because the WinUI
// shell has new chrome (Home, page placeholders). UiText.cs lives in PreySense.Frontend
// and is not editable from this stage (red line: don't touch the WPF project), so the new
// keys are collected here. Every other language falls back to the English source text.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PreySense.Frontend.Services
{
    public static class LocalizationService
    {
        private static readonly string[] TitlePrefixes = ["Performix • ", "Performix — ", " • ", " — "];

        public static string CurrentLanguage { get; private set; } = "en-US";

        public static bool IsChinese => CurrentLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);

        public static int LanguageIndex => Math.Max(0, Array.IndexOf(UiText.Languages, CurrentLanguage));

        /// <summary>Raised after the language changed so the shell can re-apply its XAML strings.</summary>
        public static event EventHandler? LanguageChanged;

        public static string Normalize(string? language)
        {
            if (string.IsNullOrWhiteSpace(language)) return "en-US";
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (language.StartsWith("hi", StringComparison.OrdinalIgnoreCase)) return "hi-IN";
            if (language.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "es-ES";
            if (language.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "fr-FR";
            return "en-US";
        }

        public static void Initialize()
        {
            string saved = string.Empty;
            try { saved = PreySense.Core.Configuration.ConfigurationService.Instance.GetCurrent().General.Language; }
            catch { /* config unavailable: fall back to the OS UI language */ }
            if (string.IsNullOrWhiteSpace(saved))
                saved = CultureInfo.CurrentUICulture.Name;
            CurrentLanguage = Normalize(saved);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CurrentLanguage);
        }

        public static void SetLanguage(string language)
        {
            string normalized = Normalize(language);
            if (normalized == CurrentLanguage) return;
            CurrentLanguage = normalized;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CurrentLanguage);
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Translates an English source string. Unknown keys return the source unchanged —
        /// that is the same contract as the WPF build, so a missing row shows English
        /// instead of a raw key.
        /// </summary>
        public static string Translate(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (CurrentLanguage == "en-US") return text;

            // R3: the five performance modes are Eco / Quiet / Balanced / Performance / Beast.
            // The rows live here (all five languages) because UiText.cs is in the WPF project
            // (red line). "Turbo" is kept as a legacy alias: Core's ProfileManager still emits
            // it (SourceProfileNames) and every surface must show it as the Beast mode.
            if (TextOverrides.TryGetValue(text, out Dictionary<string, string>? overrideRow) &&
                overrideRow.TryGetValue(CurrentLanguage, out string? overrideText))
                return overrideText;

            if (UiText.For(CurrentLanguage) is { } table && table.TryGetValue(text, out string? translated))
                return translated;

            if (WinUIStrings.TryGetValue(text, out string? shellText))
                return shellText;

            foreach (string prefix in TitlePrefixes)
            {
                if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string rest = text[prefix.Length..];
                if (UiText.For(CurrentLanguage) is { } prefixTable && prefixTable.TryGetValue(rest, out string? page))
                    return prefix + page;
            }

            return text;
        }

        public static string Format(string template, params object?[] args)
        {
            try { return string.Format(CultureInfo.CurrentUICulture, Translate(template), args); }
            catch (FormatException) { return Translate(template); }
        }

        /// <summary>Convenience alias kept from the pre-S1 WinUI service.</summary>
        public static string Get(string key) => Translate(key);

        // ── R3 mode names: five languages, consulted before UiText ───────────────
        // Requirement (R3): 节能 / 静音 / 均衡 / 性能 / 狂暴 with the English source strings
        // Eco / Quiet / Balanced / Performance / Beast, and every language must have a row.
        // Quiet's zh row moves from UiText's 安静 to 静音, Turbo's from 极速 to 狂暴 (Beast).
        private static readonly Dictionary<string, Dictionary<string, string>> TextOverrides =
            new(StringComparer.Ordinal)
            {
                ["Eco"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "节能", ["hi-IN"] = "इको", ["es-ES"] = "Eco", ["fr-FR"] = "Éco",
                },
                ["Quiet"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "静音", ["hi-IN"] = "शांत", ["es-ES"] = "Silencioso", ["fr-FR"] = "Silencieux",
                },
                ["Balanced"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "均衡", ["hi-IN"] = "संतुलित", ["es-ES"] = "Equilibrado", ["fr-FR"] = "Équilibré",
                },
                ["Performance"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "性能", ["hi-IN"] = "प्रदर्शन", ["es-ES"] = "Rendimiento", ["fr-FR"] = "Performance",
                },
                ["Beast"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "狂暴", ["hi-IN"] = "बीस्ट", ["es-ES"] = "Bestia", ["fr-FR"] = "Bête",
                },
                // T1: UiText's own zh row for "Boost Mode" is "Boost 模式"; the construction
                // sheet asks for 睿频模式, so override it (all five languages).
                ["Boost Mode"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "睿频模式", ["hi-IN"] = "बूस्ट मोड", ["es-ES"] = "Modo Boost", ["fr-FR"] = "Mode Boost",
                },
                // Switch state text (X4: every toggle reads 开 / 关, never 关闭 / 已启用 / 80%)
                ["On"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "开", ["hi-IN"] = "चालू", ["es-ES"] = "Activado", ["fr-FR"] = "Activé",
                },
                ["Off"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "关", ["hi-IN"] = "बंद", ["es-ES"] = "Desactivado", ["fr-FR"] = "Désactivé",
                },
                // Legacy alias: Core / the ViewModel still call the fifth mode "Turbo".
                ["Turbo"] = new(StringComparer.Ordinal)
                {
                    ["zh-CN"] = "狂暴", ["hi-IN"] = "बीस्ट", ["es-ES"] = "Bestia", ["fr-FR"] = "Bête",
                },

                // ── Theme page (appearance / window style / material / accent) ─────────
                // Chinese only on purpose: the other four languages fall back to the English
                // source through Translate() (UiText has no row, WinUIStrings is not used for
                // these) — that is the agreed policy for the new theme-page keys.
                ["Appearance"] = new(StringComparer.Ordinal) { ["zh-CN"] = "外观" },
                ["Appearance mode"] = new(StringComparer.Ordinal) { ["zh-CN"] = "外观模式" },
                ["Window style"] = new(StringComparer.Ordinal) { ["zh-CN"] = "窗口风格" },
                ["Follow system"] = new(StringComparer.Ordinal) { ["zh-CN"] = "跟随系统" },
                ["Material"] = new(StringComparer.Ordinal) { ["zh-CN"] = "材质" },
                ["Mica"] = new(StringComparer.Ordinal) { ["zh-CN"] = "云母" },
                ["Acrylic"] = new(StringComparer.Ordinal) { ["zh-CN"] = "亚克力" },
                ["Solid"] = new(StringComparer.Ordinal) { ["zh-CN"] = "纯色" },
                ["Accent colour"] = new(StringComparer.Ordinal) { ["zh-CN"] = "强调色" },
                ["Rounded"] = new(StringComparer.Ordinal) { ["zh-CN"] = "圆角" },
                ["Square"] = new(StringComparer.Ordinal) { ["zh-CN"] = "直角" },
                ["Appearance, window shape, material and accent colour. Every change applies immediately."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "外观模式、窗口形状、材质与强调色。所有改动立即生效。" },
                ["Appearance, window shape, material and accent colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "外观、窗口形状、材质与强调色。" },
                ["These are the same four controls the Windows personalisation page offers. Nothing here touches the hardware."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "与 Windows 个性化页面里的四个外观项一致；这里不动任何硬件。" },
                ["Dark surfaces, the default look."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "深色界面，默认外观。" },
                ["Light surfaces for a bright room."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "明亮环境用的浅色界面。" },
                ["Matches the Windows light / dark setting and follows it while Performix runs."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "跟随 Windows 的浅色/深色设置，运行期间随系统切换。" },
                ["Windows 11 draws a rounded window and rounded controls; Windows 10 squares both. Applied through DWM and the corner-radius tokens."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "Windows 11 是圆角窗口 + 圆角控件；Windows 10 全部直角。通过 DWM 与圆角令牌落地。" },
                ["Rounded window corner and 4 / 6 DIP control radii."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "圆角窗口与 4 / 6 DIP 控件圆角。" },
                ["Square window corner and square controls."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "直角窗口与直角控件。" },
                ["The backdrop behind the window. Mica and acrylic need a supported GPU driver; unsupported entries are greyed out and Performix falls back to a solid colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "窗口背后的材质。云母/亚克力需要受支持的显卡驱动；不支持的选项会灰显，并自动回落纯色。" },
                ["Used for selected tiles, switches, sliders and the primary button. Follow Windows or pick one of the preset colours."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "用于选中项、开关、滑块与主按钮。可跟随 Windows，或从预设颜色里挑一个。" },
                ["Open the colour palette."] = new(StringComparer.Ordinal) { ["zh-CN"] = "打开调色板。" },
                ["Pick an accent colour."] = new(StringComparer.Ordinal) { ["zh-CN"] = "选一个强调色" },
                ["Fixed #3B82F6"] = new(StringComparer.Ordinal) { ["zh-CN"] = "固定 #3B82F6" },
                ["Mica is not available on this system, so the window uses a solid colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "此系统不支持云母，窗口使用纯色。" },
                ["Acrylic is not available on this system, so the window uses a solid colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "此系统不支持亚克力，窗口使用纯色。" },
                ["Neither Mica nor acrylic is available on this system: the window uses a solid colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "此系统既不支持云母也不支持亚克力：窗口使用纯色。" },
                ["Language, startup, Acer services, battery and USB charging. Appearance lives on its own page."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "语言、开机启动、Acer 服务、电池与 USB 充电。外观设置在单独的页面。" },

                // ── Settings directory + General / Power / Updates and About (IA phase 1) ──
                // Chinese only, same policy as the theme-page block above: the other four
                // languages fall back to the English source through Translate().
                // Settings became a directory page (General / Power Profiles / Theme /
                // Updates and About), so the old single-page subtitle is replaced by one row
                // hint per page.
                ["Language, power, appearance, updates and about."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "语言、电源、外观、更新与关于。" },
                ["Language, startup and Acer services."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "语言、开机启动与 Acer 服务。" },
                ["Language, theme, startup and Acer services."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "语言、主题、开机启动与 Acer 服务。" },
                ["Battery limit, USB power-off charging and power profiles."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "电池充电限制、USB 关机充电与电源配置。" },
                ["Updates and About"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新与关于" },
                ["Version {0} · no update feed configured"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "版本 {0} · 未配置更新源" },

                // ── Updates and About page ─────────────────────────────────────────
                ["Version, update state, build and licence."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "版本、更新状态、构建与许可证。" },
                ["Update"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新" },
                ["Update feed"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新源" },
                ["Not configured"] = new(StringComparer.Ordinal) { ["zh-CN"] = "未配置" },
                ["Configured"] = new(StringComparer.Ordinal) { ["zh-CN"] = "已配置" },
                ["Update checking uses the configured release feed."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "检查更新使用已配置的发布源。" },
                ["Check the configured release feed for a newer version."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "向已配置的发布源查询是否有新版本。" },
                ["Performix has no release feed yet, so update checking stays offline and never contacts the network."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "尚未接入发布源，检查更新保持离线，不会访问网络。" },
                ["Check for updates"] = new(StringComparer.Ordinal) { ["zh-CN"] = "检查更新" },
                ["Not wired to a release feed yet."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "尚未接入发布源。" },
                ["About"] = new(StringComparer.Ordinal) { ["zh-CN"] = "关于" },
                ["Version"] = new(StringComparer.Ordinal) { ["zh-CN"] = "版本" },
                ["Build time"] = new(StringComparer.Ordinal) { ["zh-CN"] = "构建时间" },
                ["Architecture"] = new(StringComparer.Ordinal) { ["zh-CN"] = "架构" },
                ["Unknown"] = new(StringComparer.Ordinal) { ["zh-CN"] = "未知" },
                ["Open logs folder"] = new(StringComparer.Ordinal) { ["zh-CN"] = "打开日志目录" },
                ["Logs and crash reports live next to the executable (shell.log, crash.log)."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "日志与崩溃报告位于程序目录（shell.log、crash.log）。" },
                ["GPL-3.0. Performix is free software; third-party components are listed in THIRD-PARTY-NOTICES.md."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "GPL-3.0。Performix 是自由软件；第三方组件见 THIRD-PARTY-NOTICES.md。" },
            };

        // ── New keys introduced by the WinUI shell (zh-CN only; others fall back to English)
        private static readonly Dictionary<string, string> WinUIStrings = new(StringComparer.Ordinal)
        {
            ["Home"] = "首页",
            ["Expand"] = "展开",
            ["Collapse"] = "收起",
            ["Bal"] = "均衡",
            ["Perf"] = "性能",
            ["Adv"] = "高级",
            ["dGPU"] = "独显",
            ["Current mode"] = "当前模式",
            // TOAST-ANIMATION-SPEC §3 accessibility announcement ("已切换到狂暴模式").
            // The mode name is passed in already localized, so only the frame is translated.
            ["Switched to {0} mode"] = "已切换到{0}模式",
            ["Refresh rate"] = "刷新率",
            ["Auto refresh"] = "自动刷新",
            ["Live telemetry"] = "实时遥测",
            ["Navigation"] = "导航",
            ["Skeleton"] = "骨架页",
            ["This page is a placeholder in S1."] = "此页在 S1 阶段为占位内容。",
            ["Language, theme, startup, Acer services, battery and USB charging."] = "语言、主题、开机启动、Acer 服务、电池与 USB 充电。",
            ["CPU scheduling, GPU overclocking, power limits and PROCHOT."] = "CPU 调度、GPU 超频、功耗限制与 PROCHOT。",
            ["Fan curves, live RPM and the curve editor."] = "风扇曲线、实时转速与曲线编辑器。",
            ["Keyboard zones, logo and OEM device lighting."] = "键盘分区、Logo 与 OEM 设备灯效。",
            ["HUD configuration and overlay metric slots."] = "HUD 配置与浮窗指标槽位。",

            // ── Missing source keys reported by the S3/S4/S5 page owners ─────────────
            // These strings are already used by the pages; without a row here the zh-CN UI
            // falls back to English. Only zh-CN is covered (same policy as the rows above).
            ["Per-mode CPU scheduling, GPU clock offsets and the fan entry."] = "按模式调整 CPU 调度、GPU 频率偏移与风扇入口。",
            ["Switch to Custom or Curve mode to edit the curve."] = "切换到自定义或曲线模式后才能编辑曲线。",
            ["Drag a point to change its duty; x is temperature 20–110 °C."] = "拖动节点调整转速；横轴为 20–110 °C 的温度。",
            ["Restore Defaults"] = "恢复默认",
            ["Fan settings are unavailable: the shell ViewModel is missing."] = "风扇设置不可用：缺少界面 ViewModel。",
            ["Zone color"] = "分区颜色",
            ["Saved."] = "已保存。",
            ["Reset lighting settings"] = "重置灯光设置",
            ["Reset to defaults"] = "恢复默认",
            ["Restore the built-in keyboard and logo lighting to their defaults?"] = "要把键盘和 Logo 灯效恢复为默认值吗？",
            ["This lighting mode has no animation speed."] = "该灯效模式没有动画速度。",
            ["This lighting mode does not randomise colours."] = "该灯效模式不支持随机颜色。",

            // ── Lighting device status line ─────────────────────────────────────────
            // The status text is composed in the ViewModel / Core (LightingDeviceStatus and
            // LightingDeviceInfo.Details), so these are the exact strings it can produce;
            // the page passes the value through Loc.T, which falls back to English for
            // anything not listed here (other LED counts, error messages with a message tail).
            ["Detecting lighting devices…"] = "正在检测灯光设备…",
            ["Discovering lighting devices…"] = "正在发现灯光设备…",
            ["Using built-in keyboard lighting."] = "使用内置键盘灯光。",
            ["Keyboard with 4 independent color zones"] = "键盘 4 个独立分区。",
            ["Keyboard with 3 independent color zones"] = "键盘 3 个独立分区。",
            ["Colors apply to this bar. Hardware animations affect all light bars."] = "颜色作用于该灯条；硬件动画影响所有灯条。",
            ["Applied."] = "已应用。",

            // ── R3: tuning labels / controls that have no UiText row ────────────────
            // (UiText already carries Boost Mode / Fan Policy / Apply to Current Mode /
            //  Restore Windows Defaults / Open Curve Editor / Power source / GPU Mode with
            //  all five languages; the pages had the wrong capitalisation, which is why they
            //  rendered in English. Those call sites were fixed instead of duplicated here.)
            ["Max processor state (%)"] = "最大处理器状态 (%)",
            ["EPP preference (0 perf - 100 power)"] = "能效偏好（0 性能 – 100 节能）",
            ["EPP preference"] = "能效偏好",
            ["GPU mode is not readable on this hardware (no MUX / DDS)."] = "此硬件不支持独显直连（无 MUX / DDS），GPU 模式不可读。",
            ["Core parking min (%)"] = "核心驻留下限 (%)",
            ["Core parking max (%)"] = "核心驻留上限 (%)",
            ["Core clock offset (MHz)"] = "核心频率偏移 (MHz)",
            ["Memory clock offset (MHz)"] = "显存频率偏移 (MHz)",
            ["Restore Default 0/0 MHz"] = "恢复默认 (0/0 MHz)",
            ["Fan mode"] = "风扇模式",
            ["Max"] = "最大",
            ["Custom"] = "自定义",
            ["Curve"] = "曲线",
            ["Profile Mode"] = "配置模式",
            ["Live RPM"] = "实时转速",
            ["Ramp up (%/s)"] = "上升斜率 (%/秒)",
            ["Ramp down (%/s)"] = "下降斜率 (%/秒)",
            ["Hysteresis (C)"] = "迟滞 (°C)",
            ["Clamp to Grid (5 %)"] = "吸附到 5% 网格",
            ["Auto Apply"] = "自动应用",
            ["Color"] = "颜色",
            ["Backlight Timeout"] = "背光超时",
            ["seconds"] = "秒",
            ["Limit 80%"] = "上限 80%",
            ["Aggressive"] = "激进",
            ["Efficient Enabled"] = "高效启用",
            ["Efficient Aggressive"] = "高效激进",
            ["Aggressive At Guaranteed"] = "保证值激进",
            ["Efficient Aggressive At Guaranteed"] = "保证值高效激进",
            ["GPU overclocking is off: turn it on to edit the clock offsets."] = "GPU 超频已关闭：打开后才能编辑频率偏移。",

            // ── S6: overlay page (MetricsWindow port) ───────────────────────────────
            // Every metric / position / theme label already has a UiText row in all five
            // languages (CPU Temperature (°C), GPU Fan (RPM), Left/Center/Right, Dark/White …).
            // Only the strings that are new to this page are collected here.
            ["Position"] = "位置",
            ["Graph"] = "曲线",
            ["Remove slot"] = "移除槽位",
            ["Refresh interval"] = "刷新间隔",
            ["1 s (fixed)"] = "1 秒（固定）",
            ["Fixed by the telemetry coordinator: the overlay samples once per second."] =
                "由遥测协调器固定：浮窗每秒采样一次。",
            ["Overlay HUD: metric slots, scale, opacity, position and theme."] =
                "浮窗 HUD：指标槽位、缩放、透明度、位置与主题。",
            ["No overlay slots configured. Click + Add Slot to add metrics."] =
                "还没有配置指标槽位。点「添加插槽」加一条。",
        };
    }
}
