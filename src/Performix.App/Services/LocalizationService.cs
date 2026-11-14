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

        /// <summary>
        /// One entry of the language picker (Settings → General → Language).
        /// <paramref name="NativeName"/> is the language's own name and is never translated:
        /// a language list is the one place where the label must stay native, otherwise a user
        /// who picked the wrong language cannot find their way back.
        /// </summary>
        public sealed record LanguageOption(string Tag, string NativeName, bool HasTranslation);

        /// <summary>
        /// THE language catalogue: the single place a new language is added. A language becomes
        /// selectable by having a translation table (UiText / the TextOverrides rows below) —
        /// nothing else in the UI has to change, which is what "open for a later PR" means in
        /// practice.
        ///
        /// The five translated languages come first. 正體中文 and 日本語 are listed after them,
        /// disabled, with the reason in the row hint: the design asks for the four-language
        /// direction, but neither has a table yet, and a selectable entry that renders
        /// Simplified Chinese or English would be a fake feature (footer note in
        /// docs\design\UI-ARCH.md §4.1.5 makes the same point).
        /// </summary>
        public static IReadOnlyList<LanguageOption> LanguageOptions { get; } = new LanguageOption[]
        {
            new("zh-CN", "简体中文", true),
            new("en-US", "English", true),
            new("hi-IN", "हिन्दी", true),
            new("es-ES", "Español", true),
            new("fr-FR", "Français", true),
            new("zh-TW", "正體中文", false),
            new("ja-JP", "日本語", false),
        };

        private static readonly string[] LanguageTags =
            LanguageOptions.Select(option => option.Tag).ToArray();

        public static string CurrentLanguage { get; private set; } = "en-US";

        public static bool IsChinese => CurrentLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);

        /// <summary>Every catalogue tag, in picker order (used by the tests and the picker).</summary>
        public static IReadOnlyList<string> Languages => LanguageTags;

        public static int LanguageIndex => Math.Max(0, Array.IndexOf(LanguageTags, CurrentLanguage));

        /// <summary>The catalogue entry for a tag, or null when the tag is not in the catalogue.</summary>
        public static LanguageOption? FindLanguage(string? tag) =>
            string.IsNullOrWhiteSpace(tag)
                ? null
                : LanguageOptions.FirstOrDefault(option =>
                    string.Equals(option.Tag, tag, StringComparison.OrdinalIgnoreCase));

        /// <summary>Raised after the language changed so the shell can re-apply its XAML strings.</summary>
        public static event EventHandler? LanguageChanged;

        public static string Normalize(string? language)
        {
            if (string.IsNullOrWhiteSpace(language)) return "en-US";
            // Traditional Chinese first: "zh-TW" / "zh-HK" / "zh-Hant" must not fall into the
            // Simplified branch below.
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return language.Contains("TW", StringComparison.OrdinalIgnoreCase)
                    || language.Contains("HK", StringComparison.OrdinalIgnoreCase)
                    || language.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || language.Contains("MO", StringComparison.OrdinalIgnoreCase)
                    ? "zh-TW"
                    : "zh-CN";
            }
            if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja-JP";
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

                // ── Home → GPU mode: the three modes (R4 rework) ───────────────────────
                // The row used to offer four tiles (Auto / Hybrid / Advanced Optimus / dGPU Only);
                // two of them were the same hardware state under different NVIDIA names. The three
                // that remain answer one question — which GPU drives the panel — and the English
                // source strings stay Hybrid / Integrated / dGPU Only while the zh rows say
                // 混合模式 / 核显模式 / 独显直连.
                //
                // Only zh-CN is overridden on purpose: the other four languages keep UiText's
                // existing rows for Hybrid and dGPU Only (Híbrido / dGPU seul / …), and a missing
                // language here falls through to UiText instead of showing English.
                ["Hybrid"] = new(StringComparer.Ordinal) { ["zh-CN"] = "混合模式" },
                ["Integrated"] = new(StringComparer.Ordinal) { ["zh-CN"] = "核显模式" },
                ["dGPU Only"] = new(StringComparer.Ordinal) { ["zh-CN"] = "独显直连" },

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
                // UX review item 5: the preset used to read "Fixed #3B82F6" — a hex code is not a
                // colour name, so the option now says what the colour looks like and keeps the
                // value in parentheses (the tooltip carries it too).
                ["Blue (#3B82F6)"] = new(StringComparer.Ordinal) { ["zh-CN"] = "蓝色（#3B82F6）" },
                ["Mica is not available on this system, so the window uses a solid colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "此系统不支持云母，窗口使用纯色。" },
                ["Acrylic is not available on this system, so the window uses a solid colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "此系统不支持亚克力，窗口使用纯色。" },
                ["Neither Mica nor acrylic is available on this system: the window uses a solid colour."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "此系统既不支持云母也不支持亚克力：窗口使用纯色。" },

                // ── Updates and About page ─────────────────────────────────────────
                ["Update"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新" },
                ["Update feed"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新源" },
                ["Not configured"] = new(StringComparer.Ordinal) { ["zh-CN"] = "未配置" },
                ["Configured"] = new(StringComparer.Ordinal) { ["zh-CN"] = "已配置" },
                ["Check for updates"] = new(StringComparer.Ordinal) { ["zh-CN"] = "检查更新" },
                ["About"] = new(StringComparer.Ordinal) { ["zh-CN"] = "关于" },
                ["Version"] = new(StringComparer.Ordinal) { ["zh-CN"] = "版本" },
                ["Build time"] = new(StringComparer.Ordinal) { ["zh-CN"] = "构建时间" },
                ["Architecture"] = new(StringComparer.Ordinal) { ["zh-CN"] = "架构" },
                ["Unknown"] = new(StringComparer.Ordinal) { ["zh-CN"] = "未知" },
                ["Open logs folder"] = new(StringComparer.Ordinal) { ["zh-CN"] = "打开日志目录" },
                // UX review item 14: the button used to say where the files are but not when a
                // user would want them. One clause for the troubleshooting case.
                ["Logs and crash reports live next to the executable (shell.log, crash.log). Open them when you report a problem."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "日志与崩溃报告位于程序目录（shell.log、crash.log）；遇到问题时从这里取。" },
                ["GPL-3.0. Performix is free software; third-party components are listed in THIRD-PARTY-NOTICES.md."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "GPL-3.0。Performix 是自由软件；第三方组件见 THIRD-PARTY-NOTICES.md。" },
                ["Version {0} · {1} channel"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "版本 {0} · {1} 通道" },

                // ── Update page: real state machine (check → download → verify → install) ──
                // Same policy: zh-CN rows here, the other four languages fall back to the
                // English source through Translate().
                ["Not checked"] = new(StringComparer.Ordinal) { ["zh-CN"] = "未检查" },
                ["Checking…"] = new(StringComparer.Ordinal) { ["zh-CN"] = "检查中…" },
                ["Up to date"] = new(StringComparer.Ordinal) { ["zh-CN"] = "已是最新" },
                ["Update available"] = new(StringComparer.Ordinal) { ["zh-CN"] = "有新版" },
                ["Downloading…"] = new(StringComparer.Ordinal) { ["zh-CN"] = "下载中…" },
                ["Verifying…"] = new(StringComparer.Ordinal) { ["zh-CN"] = "校验中…" },
                ["Ready to install"] = new(StringComparer.Ordinal) { ["zh-CN"] = "准备安装" },
                ["No release yet"] = new(StringComparer.Ordinal) { ["zh-CN"] = "暂无发布" },
                ["Update failed"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新失败" },
                ["Current version"] = new(StringComparer.Ordinal) { ["zh-CN"] = "当前版本" },
                ["Channel"] = new(StringComparer.Ordinal) { ["zh-CN"] = "通道" },
                ["Update source"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新源" },
                ["Published"] = new(StringComparer.Ordinal) { ["zh-CN"] = "发布时间" },
                ["Update notes"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新说明" },
                ["Check again"] = new(StringComparer.Ordinal) { ["zh-CN"] = "重新检查" },
                ["Download and install"] = new(StringComparer.Ordinal) { ["zh-CN"] = "下载并安装" },
                ["Cancel"] = new(StringComparer.Ordinal) { ["zh-CN"] = "取消" },
                ["Retry"] = new(StringComparer.Ordinal) { ["zh-CN"] = "重试" },
                ["Switch source and retry"] = new(StringComparer.Ordinal) { ["zh-CN"] = "换源重试" },
                ["Preparing to install…"] = new(StringComparer.Ordinal) { ["zh-CN"] = "准备安装…" },
                ["Verifying the update package…"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "正在校验更新包…" },
                ["{0} downloaded (total size unknown)"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "{0} 已下载（总大小未知）" },
                ["{0} / {1} · {2}"] = new(StringComparer.Ordinal) { ["zh-CN"] = "{0} / {1} · {2}" },
                ["{0} / {1} · {2} · {3} left"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "{0} / {1} · {2} · 剩余 {3}" },
                // Source labels (the ids stay English in logs; the page shows these).
                ["GitHub Releases"] = new(StringComparer.Ordinal) { ["zh-CN"] = "GitHub Releases" },
                ["GitHub raw"] = new(StringComparer.Ordinal) { ["zh-CN"] = "GitHub 原始文件" },
                ["npm registry"] = new(StringComparer.Ordinal) { ["zh-CN"] = "npm 官方源" },
                ["gh-proxy"] = new(StringComparer.Ordinal) { ["zh-CN"] = "gh-proxy 加速" },
                ["ghproxy.net"] = new(StringComparer.Ordinal) { ["zh-CN"] = "ghproxy.net 加速" },
                ["jsDelivr"] = new(StringComparer.Ordinal) { ["zh-CN"] = "jsDelivr 加速" },
                ["npmmirror"] = new(StringComparer.Ordinal) { ["zh-CN"] = "npmmirror 镜像" },
                ["Local test feed"] = new(StringComparer.Ordinal) { ["zh-CN"] = "本地测试源" },
                // Failure sentences the state machine reports verbatim.
                ["No release has been published on the update feed yet."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新源还没有发布任何版本。" },
                ["The update source is unavailable."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新源暂时不可用。" },
                ["The update manifest is incomplete: no asset carries both sizeBytes and sha256."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新清单不完整：没有任何资产同时带 sizeBytes 与 sha256。" },
                ["The update manifest carries no downloadable asset."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新清单里没有可下载的资产。" },
                ["The running version could not be read, so no update decision was made."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "读不出当前版本，因此没有做出更新判断。" },
                ["The update check failed."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "检查更新失败。" },
                ["The update check was cancelled."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "检查已取消。" },
                ["The update package has no usable download address."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新包没有可用的下载地址。" },
                ["The update server rejected the resume range; retrying from scratch."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "服务器拒绝了断点续传，改为重新下载。" },
                ["The downloaded update package is missing."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "已下载的更新包不存在。" },
                ["Verification failed: the file size does not match the manifest."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "校验失败：文件大小与清单不一致。" },
                ["Verification failed: the SHA-256 does not match the manifest."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "校验失败：SHA-256 与清单不一致。" },
                ["Verification failed: the file could not be read."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "校验失败：文件无法读取。" },
                ["Verification was cancelled."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "校验已取消。" },
                ["The verified update package is gone."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "已校验的更新包不存在。" },
                ["The update replacement script (scripts\\invoke-update.ps1) is missing next to Performix.exe."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "替换脚本缺失：Performix.exe 旁边没有 scripts\\invoke-update.ps1。" },
                ["The replacement script did not start."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "替换脚本未能启动。" },

                // ── Update page rework: target line, percent, one-sentence failures ──────
                // English is the source string (Translate returns it unchanged for en-US);
                // zh-CN rows live here. The failure sentences deliberately name the button the
                // user should press, because the card no longer prints the raw technical detail.
                ["New version publish time"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "新版发布时间" },
                ["New version {0}"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "新版本 {0}" },
                ["New version {0} · {1}"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "新版本 {0} · {1}" },
                ["Download and install {0}"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "下载并安装 {0}" },
                ["Download progress"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "下载进度" },
                ["{0}%"] = new(StringComparer.Ordinal) { ["zh-CN"] = "{0}%" },
                ["SHA-256 · expected {0}… · got {1}…"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "SHA-256 · 期望 {0}… · 实际 {1}…" },
                ["The update feed has no release yet. Click Retry to check again, or open the logs folder to see each attempt."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新源还没有发布任何版本。请点「重试」再查一次，或打开日志目录查看每次尝试。" },
                ["The update check failed. Click Retry to check again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "检查更新失败。请点「重试」再检查一次。" },
                ["The download failed. Click Switch source and retry to use another address, or open the logs folder for details."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "下载失败。请点「换源重试」换一个下载地址，或打开日志目录查看详情。" },
                ["The package SHA-256 does not match the manifest, so the file was deleted. Click Switch source and retry to download it again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "安装包的 SHA-256 与清单不一致，文件已删除。请点「换源重试」重新下载。" },
                ["The package size does not match the manifest, so the file was deleted. Click Switch source and retry to download it again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "安装包大小与清单不一致，文件已删除。请点「换源重试」重新下载。" },
                ["The package could not be read, so verification did not finish. Click Switch source and retry to download it again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "安装包无法读取，校验没有完成。请点「换源重试」重新下载。" },
                ["The downloaded package is gone. Click Switch source and retry to download it again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "已下载的安装包不见了。请点「换源重试」重新下载。" },
                ["Verification was cancelled. Click Retry to download the package again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "校验已取消。请点「重试」重新下载安装包。" },
                ["The package could not be verified. Click Switch source and retry to download it again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "安装包未能通过校验。请点「换源重试」重新下载。" },
                ["The update could not be installed. Click Retry, or open the logs folder for details."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新安装未能启动。请点「重试」，或打开日志目录查看详情。" },
                ["The update failed. Click Retry to try again."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "更新失败。请点「重试」再试一次。" },

                // ── minVersion gate: the release cannot be applied over this build ────────
                // The machine's CanDownload is false in this state; these are the words for the
                // status line / block reason (the page owner wires them up).
                ["Needs manual install"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "需手动安装" },
                ["This update needs version {0} or newer. Install the full package manually instead of updating in place."] =
                    new(StringComparer.Ordinal)
                    {
                        ["zh-CN"] = "这个更新要求先有 {0} 或更高版本。请手动安装完整安装包，不能就地升级。",
                    },

                // ── First launch after a successful self-update (--updated-from) ──────────
                // The one-off toast is deliberate: it must not reappear on later launches.
                ["Updated to {0}"] = new(StringComparer.Ordinal) { ["zh-CN"] = "已更新到 {0}" },
                ["Previously version {0}"] = new(StringComparer.Ordinal) { ["zh-CN"] = "更新前版本 {0}" },

                // ── Settings restart: the six-section directory + the five sub-pages ───────
                // Same policy as the blocks above: English is the source string (Loc.T('…') in
                // the XAML), zh-CN lives here. English needs no row — Translate() returns the
                // source unchanged for en-US — so "both languages" means "the English source
                // in the page + the zh-CN row here".
                //
                // COPY DISCIPLINE (docs\UI-DESIGN-RULES.md §7): a settings page carries icon +
                // name + control. The only rows here that are prose are the ones that mark a
                // risky/irreversible action or an unimplemented switch — everything that merely
                // described a page ("Language, power, appearance…") was deleted with the
                // restructure, not renamed.
                ["Personalisation"] = new(StringComparer.Ordinal) { ["zh-CN"] = "个性化" },
                ["Services"] = new(StringComparer.Ordinal) { ["zh-CN"] = "服务" },

                // ── General: language catalogue + the two unimplemented switches ───────────
                ["Language"] = new(StringComparer.Ordinal) { ["zh-CN"] = "语言" },
                ["The list stays open for new languages: adding one means adding one table."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "语言列表保持开放：新增一种语言只需加一张翻译表。" },
                ["No translation table yet — this language is listed so a later PR can add it."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "尚无翻译表——列在这里是为了让后续 PR 补上它。" },
                ["Always on Top"] = new(StringComparer.Ordinal) { ["zh-CN"] = "总在最前" },
                // UX review item 1: "Not implemented: the tray panel hides when it loses focus"
                // was developer wording. A disabled row still has to say why (UI-DESIGN-RULES
                // §7.2 exception 3), so it says the user-facing reason and nothing else.
                ["Not available in this version."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "此版本暂不支持。" },
                ["Special Key Wake"] = new(StringComparer.Ordinal) { ["zh-CN"] = "特殊键唤醒" },
                ["Run on Startup"] = new(StringComparer.Ordinal) { ["zh-CN"] = "开机启动" },
                // UX review item 13: the tooltip used to explain the elevated scheduled task.
                // What the user gets, not how it is registered.
                ["Starts Performix when Windows starts, so fan curves and lighting are already in effect."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "开机时自动启动 Performix，让风扇曲线与灯效开机即生效。" },

                // ── General: power configuration (merged from the old Power page) ──────────
                ["Power Configuration"] = new(StringComparer.Ordinal) { ["zh-CN"] = "电源配置" },
                ["On Battery"] = new(StringComparer.Ordinal) { ["zh-CN"] = "使用电池时" },
                ["On AC"] = new(StringComparer.Ordinal) { ["zh-CN"] = "使用电源时" },
                ["Battery Limit"] = new(StringComparer.Ordinal) { ["zh-CN"] = "电池充电限制" },
                ["Charge cap {0}%"] = new(StringComparer.Ordinal) { ["zh-CN"] = "充电上限 {0}%" },
                ["Energy Saver on Battery"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "使用电池时节能" },

                // ── Personalisation: the four Interface switches ───────────────────────────
                ["Interface"] = new(StringComparer.Ordinal) { ["zh-CN"] = "界面" },
                ["Rows of the expanded panel. Each switch hides its row immediately and is remembered."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "展开面板里的各个栏。开关立即生效，并会被记住。" },
                ["Status bar"] = new(StringComparer.Ordinal) { ["zh-CN"] = "状态条" },
                ["GPU bar"] = new(StringComparer.Ordinal) { ["zh-CN"] = "GPU 栏" },
                ["Refresh rate bar"] = new(StringComparer.Ordinal) { ["zh-CN"] = "刷新率栏" },
                ["Widget bar"] = new(StringComparer.Ordinal) { ["zh-CN"] = "小组件栏" },

                // ── Services page (brand-aware; the brand names are proper nouns) ──────────
                ["{0} services"] = new(StringComparer.Ordinal) { ["zh-CN"] = "{0} 服务" },
                ["Vendor services"] = new(StringComparer.Ordinal) { ["zh-CN"] = "厂商服务" },
                ["Detected {0} services"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "检测到的 {0} 服务" },
                ["Services found on this machine"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "本机找到的服务" },
                ["PSSvc is never stopped or disabled: fan RPM and mode switching depend on it."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "PSSvc 永远不会被停止或禁用：风扇转速与模式切换都依赖它。" },
                ["Stopping these services also stops {0}, the {1} hotkeys and part of the OEM fan control until you turn them back on."] =
                    new(StringComparer.Ordinal)
                    {
                        ["zh-CN"] = "停用这些服务后，{0}、{1} 热键和部分原厂风扇控制会一起失效，直到你重新打开。",
                    },
                ["Stopping the wrong vendor's services can break hotkeys, lighting and fan control, so this stays off until the vendor is identified."] =
                    new(StringComparer.Ordinal)
                    {
                        ["zh-CN"] = "停错厂商的服务可能弄坏热键、灯效与风扇控制，因此在识别出厂商前这项保持关闭。",
                    },
                ["The vendor could not be identified ({0}), so no vendor service is touched here."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "无法识别厂商（{0}），因此这里不会改动任何厂商服务。" },
                ["Managing {0} services is not implemented in this build: the list below is read only."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "本版本尚未实现 {0} 服务的停用：下面的列表只读。" },
                ["The vendor could not be identified, so no service list is shown."] =
                    new(StringComparer.Ordinal)
                    { ["zh-CN"] = "无法识别厂商，因此不显示服务列表。" },
                ["No {0} services were found on this machine."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "本机没有找到 {0} 服务。" },
                ["Running"] = new(StringComparer.Ordinal) { ["zh-CN"] = "运行中" },
                ["Disabled"] = new(StringComparer.Ordinal) { ["zh-CN"] = "已禁用" },
                // UX review item 8: "已停止" covered three different situations. Each row now
                // says which one it is: the service was never running, Performix stopped it, or
                // the machine does not even have it installed.
                ["Not running"] = new(StringComparer.Ordinal) { ["zh-CN"] = "原本未运行" },
                ["Stopped by Performix"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "已由 Performix 停止" },
                ["Not installed"] = new(StringComparer.Ordinal) { ["zh-CN"] = "未安装" },
                ["Unknown purpose"] = new(StringComparer.Ordinal) { ["zh-CN"] = "未知用途" },

                // UX review item 7: one line per known service, so the list is not fourteen bare
                // English identifiers. Unknown services are NOT described here — they show the
                // "Unknown purpose" row instead of a guess.
                ["Lighting control (keyboard and light bars)"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "灯效控制（键盘与灯条）" },
                ["Macro and hotkey support"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "宏与热键支持" },
                ["Hotkeys and system monitoring"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "热键与系统监控" },
                ["Quick Access shortcuts"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "快捷访问（Quick Access）功能" },
                ["Care Center background agent"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "Care Center 后台代理" },
                ["Device information agent"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "设备信息代理" },
                ["Enables Acer peripherals and sensors"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "启用 Acer 外设与传感器" },
                ["General Acer background service"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "Acer 通用后台服务" },
                ["Acer central service"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "Acer 中央服务" },
                ["Hardware control (fan and performance)"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "硬件控制（风扇与性能）" },
                ["Predator Sense main service"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "Predator Sense 主服务" },
                ["Acer smart assistant"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "Acer 智能助手" },
                ["Fan speed and mode switching (protected)"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "风扇转速与模式切换（受保护）" },

                // UX review item 3: the consequence line is red only while the services are
                // actually stopped; in the running state the same fact is a muted note.
                ["{0} is stopped now: the {1} hotkeys and part of the OEM fan control are unavailable until you turn this back on."] =
                    new(StringComparer.Ordinal)
                    {
                        ["zh-CN"] = "已停止 {0}：{1} 热键与部分原厂风扇控制现在不可用，重新打开即可恢复。",
                    },

                // UX review item 2: the confirmation dialog in front of the batch stop.
                ["Stop {0} services?"] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "停止 {0} 服务？" },
                ["Stop services"] = new(StringComparer.Ordinal) { ["zh-CN"] = "停止服务" },
                ["You can turn them back on from this page at any time."] =
                    new(StringComparer.Ordinal) { ["zh-CN"] = "随时可以在这个页面重新打开。" },

                // ── About page ────────────────────────────────────────────────────────────
                ["Repository"] = new(StringComparer.Ordinal) { ["zh-CN"] = "仓库" },
                ["Open repository"] = new(StringComparer.Ordinal) { ["zh-CN"] = "打开仓库" },
                ["Version {0}"] = new(StringComparer.Ordinal) { ["zh-CN"] = "版本 {0}" },
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

            // ── Missing source keys reported by the S3/S4/S5 page owners ─────────────
            // These strings are already used by the pages; without a row here the zh-CN UI
            // falls back to English. Only zh-CN is covered (same policy as the rows above).
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

            // ── Home → GPU mode: tile tooltips ──────────────────────────────────────
            // The tile itself carries icon + name only (UI-DESIGN-RULES §7). These sentences are
            // the tooltip / HelpText: what the mode does when it is selectable, and why the tile
            // is disabled when it is not (§7.2 exception 3 — a greyed control must say why).
            // The ViewModel produces the English source strings (ViewModels\MainViewModel.Gpu.cs,
            // class GpuModeText) and the page runs them through Loc.T.
            ["The integrated GPU drives the display; the discrete GPU renders for apps that ask for it."] =
                "显示由核显输出，独显在应用需要时参与渲染。",
            ["Only the integrated GPU is used; the discrete GPU is switched off."] =
                "只使用核显，独显关闭。",
            ["The display is wired straight to the discrete GPU."] =
                "显示直接由独显输出。",
            ["This machine has no GPU switching hardware (MUX), so only Hybrid mode is available."] =
                "这台机器没有显卡切换硬件（MUX），只能用混合模式。",
            ["No NVIDIA or firmware API on this machine exposes an integrated-GPU-only mode, so it is not supported in this version."] =
                "本机的 NVIDIA 与固件接口都没有提供「仅核显」模式，此版本不支持。",
            ["This machine does not report a hybrid display path, so Hybrid mode cannot be selected."] =
                "本机没有报告混合显示通路，无法选择混合模式。",
            ["The machine booted straight into dGPU-only mode, so no integrated GPU can drive the display."] =
                "机器以独显直连方式启动，没有核显可以输出显示。",
            ["This GPU mode is not available on this machine."] =
                "本机不支持这个 GPU 模式。",
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

            // ── UX review item 10: the three CPU scheduling parameters that have no
            //    user-facing meaning. The explanation is a tooltip on the row's "?" dot, not a
            //    second line on the card (docs\UI-DESIGN-RULES.md §7.1).
            ["Windows keeps at least this share of cores awake. Lower values let more cores sleep: less power, less performance headroom."] =
                "Windows 至少保持唤醒的核心比例。调低可让更多核心休眠：更省电，但性能余量更小。",
            ["The most cores Windows may park. 100 % keeps every core available; lower it only when you want a hard cap on parked cores."] =
                "Windows 允许驻留（休眠）的核心比例上限。100% 表示全部核心随时可用；只有想限制驻留核心数时才调低。",
            ["Energy Performance Preference: 0 leans towards performance, 100 towards power saving. Windows scales between the two."] =
                "能效偏好：0 偏向性能，100 偏向省电，Windows 在两者之间调节。",

            // ── UX review item 15: the live data card says when it was read, and says so when
            //    the read failed instead of leaving stale numbers on screen.
            ["Updated just now"] = "刚刚更新",
            ["Updated {0} s ago"] = "{0} 秒前更新",
            ["Last update {0} s ago · data may be stale"] = "上次更新 {0} 秒前 · 数据可能已失效",
            ["No live data: the sensor read failed."] = "暂无实时数据：传感器读取失败。",

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
            ["No overlay slots configured. Click + Add Slot to add metrics."] =
                "还没有配置指标槽位。点「添加插槽」加一条。",
        };
    }
}
