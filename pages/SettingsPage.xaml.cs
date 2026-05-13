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
        private readonly LocalizationService _loc;
        private int themedata;
        private bool _isInitializing = true;
        private System.Windows.Controls.TextBox _activeHotkeyBox;
        public SettingsPage()
        {
            InitializeComponent();
            _settings = GlobalData.key_settings;
            _loc = LocalizationService.Instance;
            themedata = _settings._daynight;
            LoadSettings();
            _isInitializing = false;
        }
        private void LoadSettings()
        {
            // 本地化文本
            SectionHotkey.Text = _loc["settings.hotkey"];
            LblNormalPaste.Text = _loc["settings.normal_paste"];
            LblKeystrokesPaste.Content = _loc["settings.keystrokes_paste"];
            LblStopSimulation.Content = _loc["settings.stop_simulation"];
            txtNormalPaste.ToolTip = _loc["settings.hotkey_tooltip"];
            txtKeystrokesPaste.ToolTip = _loc["settings.hotkey_tooltip"];
            txtStopSimulation.ToolTip = _loc["settings.hotkey_stop_tooltip"];

            SectionLanguage.Text = _loc["settings.language"];
            LblSelectLang.Content = _loc["settings.select_language"];

            SectionTheme.Text = _loc["settings.theme"];
            LblCurrentTheme.Content = _loc["settings.current_theme"];
            DNSwitch.OffContent = _loc["settings.theme_light"];
            DNSwitch.OnContent = _loc["settings.theme_dark"];

            SectionBackground.Text = _loc["settings.background"];
            LblBgImage.Content = _loc["settings.background_image"];
            btnBrowseBg.Content = _loc["settings.browse"];
            btnClearBg.Content = _loc["settings.clear"];
            btnSave.Content = _loc["settings.save"];

            txtNormalPaste.Text = _settings.NormalPasteHotkey?.ToString() ?? _loc["settings.not_set"];
            txtKeystrokesPaste.Text = _settings.KeystrokesPasteHotkey?.ToString() ?? _loc["settings.not_set"];
            txtStopSimulation.Text = _settings.StopSimulationHotkey?.ToString() ?? _loc["settings.not_set"];
            if(_settings._daynight == 1) DNSwitch.IsChecked = true;

            // 加载背景图片路径
            txtBackgroundPath.Text = string.IsNullOrEmpty(_settings.BackgroundImagePath)
                ? _loc["settings.not_set"]
                : _settings.BackgroundImagePath;

            // 填充语言下拉框
            LanguageComboBox?.Items.Clear();
            if (LanguageComboBox != null)
            {
                foreach (var lang in _loc.AvailableLanguages)
                {
                    LanguageComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = lang.Name,
                        Tag = lang.Code
                    });
                }

                // 选中当前语言
                for (int i = 0; i < LanguageComboBox.Items.Count; i++)
                {
                    var item = LanguageComboBox.Items[i] as ComboBoxItem;
                    if (item?.Tag as string == _loc.CurrentLangCode)
                    {
                        LanguageComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
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

        private void BtnBrowseBg_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                txtBackgroundPath.Text = dlg.FileName;
                // 即时预览
                ApplyBackgroundPreview(dlg.FileName);
            }
        }

        private void BtnClearBg_Click(object sender, RoutedEventArgs e)
        {
            txtBackgroundPath.Text = _loc["settings.not_set"];
            _settings.BackgroundImagePath = string.Empty;
            ApplyBackgroundPreview(null);
        }

        /// <summary>
        /// 即时预览背景效果
        /// </summary>
        private void ApplyBackgroundPreview(string path)
        {
            var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                _settings.BackgroundImagePath = path ?? string.Empty;
                mainWindow.ApplyBackground();
            }
        }

        private bool _languageChanged = false;

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            var comboBox = sender as System.Windows.Controls.ComboBox;
            if (comboBox == null) return;
            var item = comboBox.SelectedItem as ComboBoxItem;
            if (item?.Tag is string langCode && langCode != _loc.CurrentLangCode)
            {
                _languageChanged = true;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 从 Tag 中获取设置的值，若未设置则保留原有值
            if (txtNormalPaste.Tag is Hotkey normalHotkey)
                _settings.NormalPasteHotkey = normalHotkey;
            if (txtKeystrokesPaste.Tag is Hotkey keystrokesHotkey)
                _settings.KeystrokesPasteHotkey = keystrokesHotkey;
            if (txtStopSimulation.Tag is Hotkey stopHotkey)
                _settings.StopSimulationHotkey = stopHotkey;

            // 保存背景图片路径
            if (txtBackgroundPath.Text != _loc["settings.not_set"])
                _settings.BackgroundImagePath = txtBackgroundPath.Text;

            // 保存语言设置
            var selectedLang = LanguageComboBox?.SelectedItem as ComboBoxItem;
            string newLang = selectedLang?.Tag as string ?? _loc.CurrentLangCode;
            _settings.Language = newLang;

            // 保存到注册表
            _settings.Save();

            // 检查是否需要重启
            bool needRestart = false;
            string restartContent = "";

            if (themedata != _settings._daynight)
            {
                needRestart = true;
                restartContent = _loc["settings.restart_theme"];
            }
            else if (_languageChanged)
            {
                needRestart = true;
                restartContent = _loc["settings.restart_language"];
            }

            if (needRestart)
            {
                Restartrequire(restartContent);
            }
        }
        private async void Restartrequire(string content)
        {
            try
            {
                var messageBox2 = new Wpf.Ui.Controls.MessageBox
                {
                    Title = _loc["settings.restart_required"],
                    Content = content,

                    PrimaryButtonText = _loc["common.yes"],
                    PrimaryButtonIcon = new SymbolIcon(SymbolRegular.Checkmark24),
                    PrimaryButtonAppearance = ControlAppearance.Primary,
                    IsPrimaryButtonEnabled = true,

                    CloseButtonAppearance = ControlAppearance.Secondary,
                    CloseButtonIcon = new SymbolIcon(SymbolRegular.Dismiss24),
                    CloseButtonText = _loc["common.no"],

                    ShowTitle = true
                };
                Wpf.Ui.Controls.MessageBoxResult results = await messageBox2.ShowDialogAsync(showAsDialog: true);

                if (results == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    // 如果切换了语言，先将新语言设置到服务并保存
                    if (_languageChanged)
                    {
                        var selectedLang = LanguageComboBox?.SelectedItem as ComboBoxItem;
                        if (selectedLang?.Tag is string langCode)
                        {
                            _settings.Language = langCode;
                            _settings.Save();
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            ProcessStartInfo startInfo = new ProcessStartInfo();
                            startInfo.FileName = UacHelper.GetExecutablePath();
                            Process.Start(startInfo);
                        }
                        catch (Exception)
                        {
                            return;
                        }
                        System.Windows.Application.Current.Shutdown();
                    });
                }
            }
            catch (Exception)
            {
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
