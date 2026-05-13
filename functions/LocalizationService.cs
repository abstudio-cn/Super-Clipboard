using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace superClipboard
{
    /// <summary>
    /// 多语言本地化服务（单例）
    /// 从 i18n/ 目录加载 JSON 翻译文件，提供字符串查找和语言切换功能
    /// </summary>
    public class LocalizationService
    {
        private static readonly Lazy<LocalizationService> _instance =
            new(() => new LocalizationService());

        public static LocalizationService Instance => _instance.Value;

        /// <summary>JSON 反序列化选项（忽略属性名大小写）</summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>硬编码英文兜底（确保即使没有任何 i18n 文件也不会返回空字符串）</summary>
        private static readonly Dictionary<string, string> _fallbackEn = new()
        {
            ["app.title"] = "Super Clipboard",
            ["home.welcome"] = "Welcome to Super Clipboard",
            ["nav.monitor"] = "Monitor",
            ["nav.history"] = "History",
            ["nav.file_send"] = "File Send",
            ["nav.file_receive"] = "File Receive",
            ["nav.settings"] = "Settings",
            ["common.yes"] = "Yes",
            ["common.no"] = "No",
            ["common.ok"] = "OK",
            ["common.cancel"] = "Cancel",
            ["common.error"] = "Error",
            ["common.hint"] = "Notice",
            ["common.confirm"] = "Confirm",
            ["admin.title"] = "Administrator Required",
            ["admin.content"] = "This application requires administrator privileges.\nRestart as administrator now?",
            ["tray.open"] = "Open",
            ["tray.exit"] = "Exit",
            ["tray.tooltip"] = "Clipboard Manager",
            ["app.already_running"] = "Application is already running.",
            ["settings.restart_required"] = "Restart Required",
            ["settings.restart_theme"] = "Theme change requires restart.\nRestart now?",
            ["settings.restart_language"] = "Language change requires restart.\nRestart now?",
            ["settings.not_set"] = "Not set",
            ["settings.hotkey"] = "Hotkey Settings",
            ["settings.language"] = "Language Settings",
            ["settings.theme"] = "Theme Settings",
            ["settings.background"] = "Background Settings",
            ["settings.save"] = "Save",
            ["settings.browse"] = "Browse",
            ["settings.clear"] = "Clear",
            ["monitor.shortcut_hint"] = "Shortcuts:",
            ["monitor.shortcut_normal"] = "Ctrl+V - Normal Paste",
            ["monitor.shortcut_simulate"] = "Ctrl+Alt+V - Simulated Typing",
            ["monitor.current"] = "Current Clipboard:",
            ["monitor.empty"] = "Clipboard is empty",
            ["monitor.type_text"] = "Text",
            ["monitor.type_image"] = "Image",
            ["monitor.type_files"] = "Files",
            ["filesend.title"] = "File Send",
            ["filesend.start_send"] = "Start Send",
            ["filesend.pause"] = "Pause",
            ["filesend.cancel"] = "Cancel",
            ["filesend.status_ready"] = "Ready",
            ["filereceive.title"] = "File Receive",
            ["filereceive.start_listening"] = "Start Listening",
            ["history.clear_all"] = "Clear History",
        };

        private readonly Dictionary<string, Dictionary<string, string>> _translations = new();
        private Dictionary<string, string> _current = new();
        private List<LanguageInfo> _availableLanguages = new();
        private string _currentLangCode = "en";

        /// <summary>当前语言代码</summary>
        public string CurrentLangCode => _currentLangCode;

        /// <summary>当前已加载的翻译条目数量</summary>
        public int Count => _current.Count;

        /// <summary>可用语言列表</summary>
        public IReadOnlyList<LanguageInfo> AvailableLanguages => _availableLanguages;

        /// <summary>语言切换事件</summary>
        public event Action? LanguageChanged;

        /// <summary>当前语言下的字符串索引器（有硬编码英文兜底）</summary>
        public string this[string key]
        {
            get
            {
                if (_current.TryGetValue(key, out var value))
                    return value;
                // 降级到硬编码英文兜底
                if (_fallbackEn.TryGetValue(key, out var fb))
                    return fb;
                return key;
            }
        }

        private LocalizationService()
        {
            LoadAllTranslations();
        }

        /// <summary>
        /// 获取指定 Key 的本地化字符串（支持格式化参数）
        /// </summary>
        public string Get(string key, params object[] args)
        {
            var text = this[key];
            return args.Length > 0 ? string.Format(text, args) : text;
        }

        /// <summary>
        /// 初始化：根据注册表或系统语言选择语言
        /// </summary>
        public void Initialize(string? savedLangCode)
        {
            string targetCode;

            if (!string.IsNullOrEmpty(savedLangCode) && _translations.ContainsKey(savedLangCode))
            {
                // 使用注册表中保存的语言
                targetCode = savedLangCode;
            }
            else
            {
                // 检测系统语言
                targetCode = DetectSystemLanguage();
            }

            SetLanguage(targetCode);
        }

        /// <summary>
        /// 根据系统 CultureInfo 检测匹配的语言
        /// </summary>
        private string DetectSystemLanguage()
        {
            string systemLang = CultureInfo.CurrentUICulture.Name; // e.g. "zh-CN", "ja-JP"

            // 加载语言映射
            string mapPath = Path.Combine(GetI18nDir(), "languages.json");
            if (File.Exists(mapPath))
            {
                try
                {
                    var json = File.ReadAllText(mapPath);
                    var root = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);
                    if (root.TryGetProperty("system_lang_map", out var map))
                    {
                        // 精确匹配
                        if (map.TryGetProperty(systemLang, out var mapped))
                            return mapped.GetString()!;

                        // 主语言匹配 (e.g. "zh" from "zh-CN")
                        string mainLang = systemLang.Split('-')[0];
                        if (map.TryGetProperty(mainLang, out var mappedMain))
                            return mappedMain.GetString()!;
                    }
                }
                catch { /* fall through */ }
            }

            // 语言映射文件中未找到 → 检查是否直接有对应的翻译文件
            string shortCode = systemLang.Split('-')[0];
            if (_translations.ContainsKey(shortCode))
                return shortCode;

            // 默认 English
            return "en";
        }

        /// <summary>
        /// 切换到指定语言
        /// </summary>
        public void SetLanguage(string langCode)
        {
            if (!_translations.TryGetValue(langCode, out var translations))
            {
                // 降级到 English
                if (!_translations.TryGetValue("en", out translations))
                {
                    // 连 English 文件都没加载 → 用硬编码兜底
                    _currentLangCode = "en";
                    _current = new Dictionary<string, string>(_fallbackEn);
                    LanguageChanged?.Invoke();
                    return;
                }
                langCode = "en";
            }

            _currentLangCode = langCode;
            _current = new Dictionary<string, string>(translations);
            LanguageChanged?.Invoke();
        }

        /// <summary>
        /// 获取 i18n 目录的绝对路径（支持多种运行环境）
        /// </summary>
        private static string GetI18nDir()
        {
            // 优先：应用程序基础目录（bin/Debug 或 publish 目录）
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string i18nDir = Path.Combine(baseDir, "i18n");
            if (Directory.Exists(i18nDir))
                return i18nDir;

            // 降级 1：当前工作目录
            i18nDir = Path.Combine(Directory.GetCurrentDirectory(), "i18n");
            if (Directory.Exists(i18nDir))
                return i18nDir;

            // 降级 2：VS 调试时 BaseDirectory 可能是项目根
            string projectDir = Path.Combine(baseDir, "..", "..", "..", "i18n");
            i18nDir = Path.GetFullPath(projectDir);
            if (Directory.Exists(i18nDir))
                return i18nDir;

            return Path.Combine(baseDir, "i18n");
        }

        /// <summary>
        /// 加载所有语言翻译文件
        /// </summary>
        private void LoadAllTranslations()
        {
            string i18nDir = GetI18nDir();
            if (!Directory.Exists(i18nDir))
                return;

            // 加载语言列表
            string langListPath = Path.Combine(i18nDir, "languages.json");
            if (File.Exists(langListPath))
            {
                try
                {
                    var json = File.ReadAllText(langListPath);
                    var root = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);
                    if (root.TryGetProperty("languages", out var arr))
                    {
                        _availableLanguages = JsonSerializer.Deserialize<List<LanguageInfo>>(
                            arr.GetRawText(), _jsonOptions) ?? new List<LanguageInfo>();
                    }
                }
                catch { /* ignore malformed file */ }
            }

            // 加载每个语言的 JSON 文件
            foreach (var lang in _availableLanguages)
            {
                string langFile = Path.Combine(i18nDir, $"{lang.Code}.json");
                if (!File.Exists(langFile)) continue;

                try
                {
                    var json = File.ReadAllText(langFile);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions);
                    if (dict != null)
                        _translations[lang.Code] = dict;
                }
                catch { /* skip broken files */ }
            }
        }
    }

    /// <summary>
    /// 语言信息（对应 languages.json 中的条目）
    /// </summary>
    public class LanguageInfo
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
