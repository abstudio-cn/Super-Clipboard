using Microsoft.Win32;
using Microsoft.Windows.Themes;
using superClipboard;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

namespace superClipboard
{
    public partial class MainWindow : FluentWindow
    {
        private Dictionary<string, UserControl> _pages;
        private Dictionary<string, NavigationViewItem> _menuItems;


        public MainWindow()
        {
            InitializeComponent();
            InitializePages();
            GenerateMenuItems();
            SetIconFromEmbeddedResource();
            ApplicationThemeManager.Apply( ApplicationTheme.Dark, WindowBackdropType.Mica);
        }

        private void SetIconFromEmbeddedResource()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "superClipboard.Resources.favicon.ico";
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
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
        }
        private void InitializePages()
        {
            // 初始化页面集合
            _pages = new Dictionary<string, UserControl>
            {
                { "Home", new HomePage() },
                { "Monitor", new ClipMonitor() },
                { "History", new ClipHistory() },
                { "Settings", new SettingsPage() }
            };

            // 初始化菜单项集合
            _menuItems = new Dictionary<string, NavigationViewItem>();
        }

        private void GenerateMenuItems()
        {
            // 清空现有菜单项
            MainNavigationView.MenuItems.Clear();

            // 创建菜单项
            var testItem = CreateNavigationItem("监听面板", SymbolRegular.Desktop24, typeof(superClipboard.ClipMonitor));
            var historyItem = CreateNavigationItem("历史记录", SymbolRegular.History24, typeof(superClipboard.ClipHistory));
            var settingsItem = CreateNavigationItem("设置", SymbolRegular.Settings24, typeof(superClipboard.SettingsPage));

            // 添加到导航视图
            MainNavigationView.MenuItems.Add(testItem);
            MainNavigationView.MenuItems.Add(historyItem);
            MainNavigationView.MenuItems.Add(settingsItem);

            // 存储菜单项引用以便后续操作
            // 设置默认选中项
            MainNavigationView.IsEnabled = true;
            MainNavigationView.ContentOverlay = _pages["Home"];

        }

        private NavigationViewItem CreateNavigationItem(string content, SymbolRegular icon,System.Type classtype )
        {
            var item = new NavigationViewItem
            {
                Name = content,
                Content = content,
                Icon = new SymbolIcon { Symbol = icon },
                TargetPageType = classtype
            };

            // 为菜单项添加点击事件
            item.Click += OnMenuItemClick;

            return item;
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            MainNavigationView.ContentOverlay = null;
            if (sender is NavigationViewItem item)
            {
                string pageKey = item.Content.ToString();

                if (_pages.ContainsKey(pageKey))
                {
                    // 更新面包屑
                    UpdateBreadcrumb(pageKey);
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