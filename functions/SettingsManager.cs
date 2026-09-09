using Microsoft.Win32;
using superClipboard;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace superClipboard
{
    public class SettingsManager
    {
        private const string RegistryKeyPath = @"Software\clipboardManager";

        public Hotkey NormalPasteHotkey { get; set; }
        public Hotkey KeystrokesPasteHotkey { get; set; }
        public Hotkey StopSimulationHotkey { get; set; }
        public string BackgroundImagePath { get; set; } = string.Empty;
        public int _daynight {  get; set; } = 1;
        public string Language { get; set; } = string.Empty;

        /// <summary>
        /// Excel 编辑模式：复制 Excel 内容时等待 1 秒确保完整，
        /// 且文字（而非图片）保持在历史列表最前。
        /// </summary>
        public bool ExcelEditMode { get; set; } = false;

        /// <summary>
        /// 切换 Excel 编辑模式开/关的快捷键（默认 Ctrl+Alt+E）。
        /// </summary>
        public Hotkey ExcelModeHotkey { get; set; }

        /// <summary>
        /// 历史指定粘贴等待时间（毫秒）：按下粘贴热键后，数字键选择历史条目的
        /// 等待窗口时长。范围 200-2000ms（200ms 步进），默认 1000ms。
        /// </summary>
        public int HistoryPasteWaitMs { get; set; } = 1000;

        /// <summary>Excel 编辑模式改变事件（true=开启），供设置页开关实时同步。</summary>
        public event Action<bool>? ExcelEditModeChanged;

        /// <summary>
        /// 设置 Excel 编辑模式（避免重复设置，并在变化时通知订阅者）。
        /// </summary>
        public void SetExcelEditMode(bool value)
        {
            if (ExcelEditMode == value) return;
            ExcelEditMode = value;
            ExcelEditModeChanged?.Invoke(value);
        }

        public SettingsManager()
        {
            // 设置默认值
            NormalPasteHotkey = new Hotkey(Key.V, ModifierKeys.Control);
            KeystrokesPasteHotkey = new Hotkey(Key.V, ModifierKeys.Control | ModifierKeys.Alt);
            StopSimulationHotkey = new Hotkey(Key.Escape, ModifierKeys.None);
            ExcelModeHotkey = new Hotkey(Key.E, ModifierKeys.Control | ModifierKeys.Alt);
        }

        /// <summary>
        /// 从注册表加载设置
        /// </summary>
        public void Load()
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key == null) return;

                // 读取普通粘贴快捷键
                string normal = key.GetValue("NormalPaste") as string;
                if (!string.IsNullOrEmpty(normal))
                    NormalPasteHotkey = ParseHotkey(normal) ?? NormalPasteHotkey;

                // 读取模拟按键粘贴快捷键
                string keystrokes = key.GetValue("KeystrokesPaste") as string;
                if (!string.IsNullOrEmpty(keystrokes))
                    KeystrokesPasteHotkey = ParseHotkey(keystrokes) ?? KeystrokesPasteHotkey;

                // 读取停止模拟输入快捷键
                string stopSim = key.GetValue("StopSimulation") as string;
                if (!string.IsNullOrEmpty(stopSim))
                    StopSimulationHotkey = ParseHotkey(stopSim) ?? StopSimulationHotkey;

                // 读取 Excel 编辑模式开关快捷键
                string excelHotkey = key.GetValue("ExcelModeHotkey") as string;
                if (!string.IsNullOrEmpty(excelHotkey))
                    ExcelModeHotkey = ParseHotkey(excelHotkey) ?? ExcelModeHotkey;

                // 读取历史指定粘贴等待时间（钳位到 200-2000ms）
                if (Convert.ToInt32(key.GetValue("HistoryPasteWaitMs")) is int waitMs && waitMs >= 100)
                    HistoryPasteWaitMs = Math.Min(Math.Max(waitMs, 200), 2000);

                if (Convert.ToInt32(key.GetValue("ThemeType")) is int intValue)
                    _daynight = intValue;

                string bgPath = key.GetValue("BackgroundImage") as string;
                if (!string.IsNullOrEmpty(bgPath))
                    BackgroundImagePath = bgPath;

                Language = key.GetValue("Language") as string ?? string.Empty;

                if (Convert.ToInt32(key.GetValue("ExcelEditMode")) is int excelMode)
                    ExcelEditMode = excelMode == 1;
            }
            catch { /* 忽略错误，使用默认值 */ }
        }

        /// <summary>
        /// 保存设置到注册表
        /// </summary>
        public void Save()
        {
            try
            {
                var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key.SetValue("NormalPaste", SerializeHotkey(NormalPasteHotkey));
                key.SetValue("KeystrokesPaste", SerializeHotkey(KeystrokesPasteHotkey));
                key.SetValue("StopSimulation", SerializeHotkey(StopSimulationHotkey));
                key.SetValue("ExcelModeHotkey", SerializeHotkey(ExcelModeHotkey));
                key.SetValue("HistoryPasteWaitMs", HistoryPasteWaitMs);
                key.SetValue("BackgroundImage", BackgroundImagePath ?? string.Empty);
                key.SetValue("ThemeType", _daynight);
                key.SetValue("Language", Language ?? string.Empty);
                key.SetValue("ExcelEditMode", ExcelEditMode ? 1 : 0);
            }
            catch { /* 处理写入异常（可选） */ }
        }

        public void ReflashTheme()
        {
            if (_daynight == 1)
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Dark,WindowBackdropType.Mica);
            }
            else
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
            }
        }

        private string SerializeHotkey(Hotkey hotkey)
        {
            return $"{hotkey.Modifiers}|{hotkey.Key}";
        }

        private Hotkey ParseHotkey(string str)
        {
            var parts = str.Split('|');
            if (parts.Length == 2 &&
                Enum.TryParse<ModifierKeys>(parts[0], out var modifiers) &&
                Enum.TryParse<Key>(parts[1], out var key))
            {
                return new Hotkey(key, modifiers);
            }
            return null;
        }
    }
}