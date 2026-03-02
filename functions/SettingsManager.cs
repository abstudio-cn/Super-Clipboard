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
        public int _daynight {  get; set; } = 1;

        public SettingsManager()
        {
            // 设置默认值
            NormalPasteHotkey = new Hotkey(Key.V, ModifierKeys.Control);
            KeystrokesPasteHotkey = new Hotkey(Key.V, ModifierKeys.Control | ModifierKeys.Alt);
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

                if (Convert.ToInt32(key.GetValue("ThemeType")) is int intValue)
                    _daynight = intValue;
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
                key.SetValue("ThemeType", _daynight);
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