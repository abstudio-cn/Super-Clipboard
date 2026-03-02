using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace superClipboard
{
    /// <summary>
    /// SettingsPage.xaml 的交互逻辑
    /// </summary>
    public partial class SettingsPage : System.Windows.Controls.UserControl
    {
        private readonly SettingsManager _settings;
        private int themedata;
        private System.Windows.Controls.TextBox _activeHotkeyBox;
        public SettingsPage()
        {
            InitializeComponent();
            _settings = GlobalData.key_settings;
            themedata = _settings._daynight;
            LoadSettings();
        }
        private void LoadSettings()
        {
            txtNormalPaste.Text = _settings.NormalPasteHotkey?.ToString() ?? "未设置";
            txtKeystrokesPaste.Text = _settings.KeystrokesPasteHotkey?.ToString() ?? "未设置";
            if(_settings._daynight == 1) DNSwitch.IsChecked = true;
        }

        private void HotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true; // 阻止输入文本

            var textBox = sender as System.Windows.Controls.TextBox;
            if (textBox == null) return;

            // 忽略单独的修饰键
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                e.Key == Key.LWin || e.Key == Key.RWin)
            {
                return;
            }

            // 转换为 Hotkey
            var hotkey = Hotkey.FromKeyEventArgs(e);
            textBox.Text = hotkey.ToString();
            textBox.Tag = hotkey; // 暂存
            _activeHotkeyBox = textBox;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 从 Tag 中获取设置的值，若未设置则保留原有值
            if (txtNormalPaste.Tag is Hotkey normalHotkey)
                _settings.NormalPasteHotkey = normalHotkey;
            if (txtKeystrokesPaste.Tag is Hotkey keystrokesHotkey)
                _settings.KeystrokesPasteHotkey = keystrokesHotkey;

            // 保存到注册表
            _settings.Save();
            if (themedata != _settings._daynight)
            {
                Restartrequire(sender, e);
            }
        }
        private async void Restartrequire(object sender, RoutedEventArgs e)
        {
            var messageBox2 = new Wpf.Ui.Controls.MessageBox
            {
                Title = "需要重启",                  // 窗口标题（继承自 Window）
                Content = "修改主题需要重启\n是否立即重新启动？",        // 消息内容（可放置任何 UI 元素）

                // 主按钮（通常是确认/是）
                PrimaryButtonText = "是",
                PrimaryButtonIcon = new SymbolIcon(SymbolRegular.Checkmark24),
                PrimaryButtonAppearance = ControlAppearance.Primary,
                IsPrimaryButtonEnabled = true,

                CloseButtonAppearance = ControlAppearance.Secondary,
                CloseButtonIcon = new SymbolIcon(SymbolRegular.Dismiss24),
                CloseButtonText = "否",


                // 是否在标题栏显示标题
                ShowTitle = true
            };
            Wpf.Ui.Controls.MessageBoxResult results = await messageBox2.ShowDialogAsync(showAsDialog: true);

            if (results == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = UacHelper.GetExecutablePath();
                Process.Start(startInfo);
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
        {
            _settings._daynight = 1;

        }

        private void ThemeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _settings._daynight = 0;
        }
    }
}
