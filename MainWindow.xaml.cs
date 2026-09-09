using Microsoft.Win32;
using Microsoft.Windows.Themes;
using superClipboard;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace superClipboard
{
    public partial class MainWindow : FluentWindow
    {
        private Dictionary<string, UserControl> _pages;
        private Dictionary<string, NavigationViewItem> _menuItems;


        public MainWindow()
        {
            InitializeComponent();
            TitleBar.Title = LocalizationService.Instance["app.title"];
            InitializePages();
            GenerateMenuItems();
            SetIconFromEmbeddedResource();
            ApplyBackground();

            // 订阅粘贴队列模式改变事件，更新标题栏提示
            GlobalData._clipboardCore.PasteQueueModeChanged += OnPasteQueueModeChanged;
        }

        /// <summary>
        /// 粘贴队列模式改变时更新标题栏：
        /// 关闭 → “超级剪贴板”；开启 → “超级剪贴板（顺序粘贴模式/倒序粘贴模式）”。
        /// </summary>
        private void OnPasteQueueModeChanged(PasteQueueMode mode)
        {
            var loc = LocalizationService.Instance;

            if (mode == PasteQueueMode.Off)
            {
                TitleBar.Title = loc["app.title"];
                return;
            }

            string modeText = mode == PasteQueueMode.Sequential
                ? loc["history.paste_queue.sequential"]
                : loc["history.paste_queue.reverse"];

            TitleBar.Title = loc.Get("app.title_queue", loc["app.title"], modeText);
        }

        /// <summary>
        /// 应用背景图片设置。夜间模式下亮度减半（叠加黑色半透明遮罩）。
        /// </summary>
        public void ApplyBackground()
        {
            var settings = GlobalData.key_settings;
            string path = settings.BackgroundImagePath;
            bool isNight = settings._daynight == 1;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                BackgroundImage.Visibility = Visibility.Collapsed;
                NightOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                BackgroundImage.Source = bitmap;
                BackgroundImage.Visibility = Visibility.Visible;

                // 夜间模式：叠加 50% 黑色遮罩降低亮度
                NightOverlay.Visibility = isNight ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception)
            {
                BackgroundImage.Visibility = Visibility.Collapsed;
                NightOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void SetIconFromEmbeddedResource()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "superClipboard.Resources.favicon.ico";
            Stream stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                this.Icon = bitmap;
            }
        }
        private void InitializePages()
        {
            // 初始化页面集合（用类型名作 key，与语言无关）
            _pages = new Dictionary<string, UserControl>
            {
                { "Home", new HomePage() },
                { nameof(ClipMonitor), new ClipMonitor() },
                { nameof(ClipHistory), new ClipHistory() },
                { nameof(FileSendPage), new FileSendPage() },
                { nameof(FileReceivePage), new FileReceivePage() },
                { nameof(SettingsPage), new SettingsPage() },
                { nameof(HelpPage), new HelpPage() }
            };

            // 初始化菜单项集合
            _menuItems = new Dictionary<string, NavigationViewItem>();
        }

        private void GenerateMenuItems()
        {
            // 清空现有菜单项
            MainNavigationView.MenuItems.Clear();

            // 创建菜单项
            var loc = LocalizationService.Instance;
            var testItem = CreateNavigationItem(loc["nav.monitor"], SymbolRegular.Desktop24, typeof(superClipboard.ClipMonitor));
            var historyItem = CreateNavigationItem(loc["nav.history"], SymbolRegular.History24, typeof(superClipboard.ClipHistory));
            var fileSendItem = CreateNavigationItem(loc["nav.file_send"], SymbolRegular.Send24, typeof(superClipboard.FileSendPage));
            var fileReceiveItem = CreateNavigationItem(loc["nav.file_receive"], SymbolRegular.Archive24, typeof(superClipboard.FileReceivePage));
            var settingsItem = CreateNavigationItem(loc["nav.settings"], SymbolRegular.Settings24, typeof(superClipboard.SettingsPage));
            var helpItem = CreateNavigationItem(loc["nav.help"], SymbolRegular.QuestionCircle24, typeof(superClipboard.HelpPage));

            // 添加到导航视图
            MainNavigationView.MenuItems.Add(testItem);
            MainNavigationView.MenuItems.Add(historyItem);
            MainNavigationView.MenuItems.Add(fileSendItem);
            MainNavigationView.MenuItems.Add(fileReceiveItem);
            MainNavigationView.MenuItems.Add(settingsItem);
            MainNavigationView.MenuItems.Add(helpItem);

            // 默认显示首页
            MainNavigationView.IsEnabled = true;
            MainFrame.Navigate(_pages["Home"]);

        }

        private NavigationViewItem CreateNavigationItem(string content, SymbolRegular icon,System.Type classtype )
        {
            var item = new NavigationViewItem
            {
                // Name 不能用空格，用类型名代替
                Name = classtype.Name.Replace(" ", "_"),
                Content = content,
                Icon = new SymbolIcon { Symbol = icon },
                // 不设 TargetPageType，由 OnMenuItemClick 手动导航
            };

            // 为菜单项添加点击事件
            item.Click += OnMenuItemClick;

            return item;
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is NavigationViewItem item)
            {
                string pageKey = item.Name;

                if (_pages.TryGetValue(pageKey, out var page))
                {
                    MainFrame.Navigate(page);
                    UpdateBreadcrumb(item.Content?.ToString() ?? pageKey);
                }
            }
        }

        private void UpdateBreadcrumb(string currentPage)
        {
            //BreadcrumbBar.ItemsSource = new List<string> { "Home", currentPage };
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            //ClipHistory._clipboardCore.StopMonitoring();
            //base.OnClosed(e);
        }
    }
}